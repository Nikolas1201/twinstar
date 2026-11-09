using System.Security.Cryptography;
using System.Text;

namespace Twinstar;

/// <summary>
/// Disk cache for remote art (store covers, hero backgrounds). Get() returns the local file when it
/// is cached, otherwise starts a background download and returns null; Downloaded fires when done.
/// </summary>
static class ArtCache
{
    static readonly object Gate = new();
    static readonly HashSet<string> Pending = new();
    static readonly HashSet<string> Failed = new();

    /// <summary>Raised on a worker thread after a download finishes; marshal to the UI before repainting.</summary>
    public static event Action? Downloaded;

    static string Dir => Path.Combine(Settings.Dir, "art", "cache");

    static string PathFor(string url)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..16];
        var ext = Path.GetExtension(new Uri(url).AbsolutePath);
        return Path.Combine(Dir, hash + (ext is ".jpg" or ".jpeg" or ".png" ? ext : ".jpg"));
    }

    public static bool IsPending(string url) { lock (Gate) return Pending.Contains(url); }

    /// <summary>First URL in the list that is (or becomes) available, skipping ones that failed to download.</summary>
    public static string? First(IEnumerable<string> urls)
    {
        foreach (var url in urls)
        {
            var path = Get(url);
            if (path != null) return path;
            lock (Gate) if (!Failed.Contains(url)) return null; // still downloading: wait for it
        }
        return null;
    }

    public static string? Get(string url)
    {
        string path;
        try { path = PathFor(url); } catch { return null; }
        if (File.Exists(path)) return path;
        lock (Gate)
        {
            if (Failed.Contains(url) || !Pending.Add(url)) return null;
        }
        _ = Task.Run(async () =>
        {
            bool ok = false;
            try
            {
                using var resp = await Artwork.Http.GetAsync(url);
                if (resp.IsSuccessStatusCode)
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync();
                    if (bytes.Length > 1000)
                    {
                        Directory.CreateDirectory(Dir);
                        await File.WriteAllBytesAsync(path + ".tmp", bytes);
                        File.Move(path + ".tmp", path, true);
                        ok = true;
                    }
                }
            }
            catch (Exception ex) { Log.Write($"Art download failed ({url}): {ex.Message}"); }
            lock (Gate)
            {
                Pending.Remove(url);
                if (!ok) Failed.Add(url);
            }
            Downloaded?.Invoke();
        });
        return null;
    }
}
