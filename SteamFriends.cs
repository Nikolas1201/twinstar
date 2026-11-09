using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Twinstar;

/// <summary>
/// Steam friends through Steam's official Web API (needs the user's own free API key and a public
/// friends list). Read-only: chatting and joining are handed to the Steam client via steam:// links.
/// </summary>
static class SteamFriends
{
    public sealed record Friend(string Id, string Name, int State, string? Game, string? GameId, string Avatar, long LastLogoff)
    {
        public bool InGame => !string.IsNullOrEmpty(Game);
        public bool Online => State != 0;
        /// <summary>Sort order: in a game, online, away/busy/snooze, offline.</summary>
        public int Rank => InGame ? 0 : State == 1 || State >= 5 ? 1 : State != 0 ? 2 : 3;
    }

    /// <summary>The SteamID64 of the account last signed in to the Steam client on this PC.</summary>
    public static string? LocalSteamId()
    {
        try
        {
            var steam = (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string)?.Replace('/', '\\');
            var file = steam == null ? null : Path.Combine(steam, "config", "loginusers.vdf");
            if (file == null || !File.Exists(file)) return null;
            var text = File.ReadAllText(file);
            string? first = null;
            foreach (Match m in Regex.Matches(text, "\"(\\d{17})\"\\s*\\{([^}]*)\\}"))
            {
                first ??= m.Groups[1].Value;
                if (Regex.IsMatch(m.Groups[2].Value, "\"MostRecent\"\\s*\"1\"")) return m.Groups[1].Value;
            }
            return first;
        }
        catch { return null; }
    }

    public static async Task<List<Friend>> FetchAsync(string apiKey, string steamId)
    {
        var list = await Artwork.Http.GetStringAsync(
            $"https://api.steampowered.com/ISteamUser/GetFriendList/v1/?key={apiKey}&steamid={steamId}&relationship=friend");
        using var listDoc = JsonDocument.Parse(list);
        var ids = listDoc.RootElement.GetProperty("friendslist").GetProperty("friends").EnumerateArray()
            .Select(f => f.GetProperty("steamid").GetString()!).ToList();

        var friends = new List<Friend>();
        foreach (var chunk in ids.Chunk(100)) // the summaries call takes up to 100 ids
        {
            var json = await Artwork.Http.GetStringAsync(
                $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={apiKey}&steamids={string.Join(',', chunk)}");
            using var doc = JsonDocument.Parse(json);
            foreach (var p in doc.RootElement.GetProperty("response").GetProperty("players").EnumerateArray())
            {
                string Str(string k) => p.TryGetProperty(k, out var v) ? v.ToString() : "";
                friends.Add(new Friend(Str("steamid"), Str("personaname"),
                    p.TryGetProperty("personastate", out var st) ? st.GetInt32() : 0,
                    Str("gameextrainfo") is { Length: > 0 } g ? g : null,
                    Str("gameid") is { Length: > 0 } gid ? gid : null,
                    Str("avatarfull"),
                    p.TryGetProperty("lastlogoff", out var lo) ? lo.GetInt64() : 0));
            }
        }
        return friends.OrderBy(f => f.Rank).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static string StatusText(Friend f) => f.InGame ? $"Playing {f.Game}" : f.State switch
    {
        1 => "Online",
        2 => "Busy",
        3 => "Away",
        4 => "Snooze",
        5 => "Looking to trade",
        6 => "Looking to play",
        _ => f.LastLogoff > 0 ? $"Offline · last seen {Ago(DateTimeOffset.FromUnixTimeSeconds(f.LastLogoff))}" : "Offline",
    };

    static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.Now - t;
        return d.TotalMinutes < 60 ? $"{Math.Max(1, (int)d.TotalMinutes)} min ago"
            : d.TotalHours < 24 ? $"{(int)d.TotalHours} h ago"
            : d.TotalDays < 30 ? $"{(int)d.TotalDays} days ago"
            : t.LocalDateTime.ToString("d MMM yyyy");
    }
}
