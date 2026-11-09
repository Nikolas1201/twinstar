using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Twinstar;

/// <summary>
/// Discord Rich Presence through the Discord client's official local IPC pipe (discord-ipc-N).
/// Needs only the client ID of a Discord application the user created; its name is what Discord
/// shows as "Playing …". Updates are throttled to Discord's limit (5 per 20 s).
/// </summary>
static class DiscordRpc
{
    static NamedPipeClientStream? _pipe;
    static string? _clientId;
    static string? _sent; // last activity JSON sent, to skip duplicates
    static long _lastSend;
    static readonly object Gate = new();

    public static bool Connected => _pipe is { IsConnected: true };

    /// <summary>Sets (or with null details, clears) the activity. Safe to call often.</summary>
    public static void Set(string clientId, string? details, string? state, DateTimeOffset? since)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return;
        var activity = details == null ? null : new Dictionary<string, object?>
        {
            ["details"] = details,
            ["state"] = state,
            ["timestamps"] = since == null ? null : new Dictionary<string, long> { ["start"] = since.Value.ToUnixTimeSeconds() },
            ["instance"] = false,
        };
        var json = JsonSerializer.Serialize(activity);
        if (json == _sent || Environment.TickCount64 - _lastSend < 4000) return; // unchanged, or too soon
        Task.Run(() =>
        {
            lock (Gate)
            {
                try
                {
                    if (!EnsureConnected(clientId.Trim())) return;
                    var payload = new Dictionary<string, object?>
                    {
                        ["cmd"] = "SET_ACTIVITY",
                        ["args"] = new Dictionary<string, object?> { ["pid"] = Environment.ProcessId, ["activity"] = activity },
                        ["nonce"] = Guid.NewGuid().ToString(),
                    };
                    Write(1, JsonSerializer.Serialize(payload));
                    _sent = json;
                    _lastSend = Environment.TickCount64;
                }
                catch (Exception ex) { Log.Write("Discord RPC: " + ex.Message); Drop(); }
            }
        });
    }

    static bool EnsureConnected(string clientId)
    {
        if (Connected && clientId == _clientId) return true;
        Drop();
        for (int i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try { pipe.Connect(150); }
            catch { pipe.Dispose(); continue; }
            _pipe = pipe;
            _clientId = clientId;
            Write(0, JsonSerializer.Serialize(new { v = 1, client_id = clientId })); // handshake
            // Discord answers every frame; drain them so its writes never block.
            var reader = pipe;
            Task.Run(() =>
            {
                var buf = new byte[8192];
                try { while (reader.IsConnected && reader.Read(buf, 0, buf.Length) > 0) { } } catch { }
            });
            Log.Write($"Discord RPC connected on discord-ipc-{i}");
            return true;
        }
        return false; // Discord isn't running
    }

    static void Write(int opcode, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + body.Length];
        BitConverter.GetBytes(opcode).CopyTo(frame, 0);
        BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
        body.CopyTo(frame, 8);
        _pipe!.Write(frame, 0, frame.Length);
        _pipe.Flush();
    }

    static void Drop()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        _sent = null;
    }
}
