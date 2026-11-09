using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Twinstar;

enum GameSource { Steam, Epic, Custom }

sealed class Game
{
    public string Name = "";
    public GameSource Source;
    public string LaunchTarget = "";
    public string LaunchArgs = "";
    public string WorkingDir = "";
    /// <summary>Any running process whose exe lives under this folder belongs to the game.</summary>
    public string InstallDir = "";
    public string? ImagePath;
    public string? IconExe;
    public bool Hidden;   // excluded: never comes back on a scan
    public bool Removed;  // removed until the next manual scan
    public bool CustomArt;
    /// <summary>Official cover the store itself provides (Epic's launcher cache), downloaded on demand.</summary>
    public string? StoreArtUrl;
    /// <summary>Store the game was installed from, when a scraper knows it (e.g. "GOG").</summary>
    public string Store = "";
    /// <summary>Wide background art: a local file, else the first URL that downloads (Playnite order: banner, store bg).</summary>
    public string? HeroPath;
    public List<string> HeroUrls = new();
    /// <summary>Transparent title logo shown instead of the plain-text name.</summary>
    public string? LogoPath, LogoUrl;
    /// <summary>Steam app this game is (or was matched to); drives HD cover, background and logo.</summary>
    public string? SteamAppId;
    /// <summary>Added through Settings > Add an app; shown under the Apps tab.</summary>
    public bool IsApp;
    /// <summary>Runs inside an emulator shared with other games (so "running" must mean *this* game).</summary>
    public bool Emulated;

    /// <summary>Local path of the wide art if available now (may start a background download).</summary>
    public string? HeroFile => HeroPath != null && File.Exists(HeroPath) ? HeroPath : ArtCache.First(HeroUrls);

    public string? LogoFile => LogoPath != null && File.Exists(LogoPath) ? LogoPath : LogoUrl != null ? ArtCache.Get(LogoUrl) : null;

    /// <summary>Points the game's background/logo (and cover, if it has none) at a Steam app's CDN art, as Playnite does.</summary>
    public void UseSteamArt(string appId, bool cover)
    {
        SteamAppId = appId;
        var cdn = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/";
        HeroUrls.InsertRange(0, new[] { cdn + "library_hero.jpg", cdn + "page_bg_generated_v6b.jpg" });
        LogoUrl ??= cdn + "logo.png";
        if (cover && (ImagePath == null || !File.Exists(ImagePath))) StoreArtUrl = cdn + "library_600x900_2x.jpg";
    }

    Image? _image;
    bool _imageLoaded;
    readonly Dictionary<Size, Bitmap> _scaled = new();

    public Image? Image
    {
        get
        {
            if (_imageLoaded) return _image;
            if (ImagePath == null && StoreArtUrl != null)
            {
                // Store-provided cover (Epic): use it once the art cache has downloaded it.
                ImagePath = ArtCache.Get(StoreArtUrl);
                if (ImagePath == null && ArtCache.IsPending(StoreArtUrl)) return null;
            }
            _imageLoaded = true;
            try
            {
                if (ImagePath != null && File.Exists(ImagePath))
                    using (var fs = File.OpenRead(ImagePath)) _image = new Bitmap(System.Drawing.Image.FromStream(fs));
                else if (IconExe != null && File.Exists(IconExe))
                    _image = Icon.ExtractAssociatedIcon(IconExe)?.ToBitmap();
            }
            catch (Exception ex) { Log.Write($"Image load failed for {Name}: {ex.Message}"); }
            return _image;
        }
    }

    public bool IsIcon => ImagePath == null || !File.Exists(ImagePath);

    /// <summary>Cover art cropped and scaled to exactly this size, cached so painting stays cheap.</summary>
    public Bitmap? CoverAt(Size size)
    {
        if (Image == null || IsIcon || size.Width < 1 || size.Height < 1) return null;
        if (_scaled.TryGetValue(size, out var hit)) return hit;
        if (_scaled.Count >= 4)
        {
            foreach (var b in _scaled.Values) b.Dispose();
            _scaled.Clear();
        }
        var bmp = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        _scaled[size] = bmp;
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var img = Image;
        float scale = Math.Max((float)size.Width / img.Width, (float)size.Height / img.Height);
        float w = img.Width * scale, h = img.Height * scale;
        g.DrawImage(img, (size.Width - w) / 2, (size.Height - h) / 2, w, h);
        return bmp;
    }

    public void Launch()
    {
        var psi = new ProcessStartInfo(LaunchTarget, LaunchArgs) { UseShellExecute = true };
        if (!string.IsNullOrEmpty(WorkingDir) && Directory.Exists(WorkingDir)) psi.WorkingDirectory = WorkingDir;
        Process.Start(psi);
    }
}

static class GameLibrary
{
    public static string? SteamExe;

    /// <summary>Automatic library sources, in the order shown in Settings → Library sources.</summary>
    public static readonly (string key, string name, Func<Settings, IEnumerable<Game>> scan)[] Sources =
    {
        ("steam", "Steam", _ => ScanSteam()),
        ("epic", "Epic Games", _ => ScanEpic()),
        ("gog", "GOG Galaxy", _ => Scrapers.Gog()),
        ("ubisoft", "Ubisoft Connect", _ => Scrapers.Ubisoft()),
        ("ea", "EA app", _ => Scrapers.ByPublisher("Electronic Arts", "EA app", "Origin")),
        ("battlenet", "Battle.net", _ => Scrapers.ByPublisher("Blizzard Entertainment", "Battle.net")),
        ("riot", "Riot Games", _ => Scrapers.ByPublisher("Riot Games", "Riot Client", "Riot Vanguard")),
        ("rockstar", "Rockstar Games", _ => Scrapers.ByPublisher("Rockstar Games", "Rockstar Games Launcher", "Rockstar Games Social Club")),
        ("humble", "Humble App", _ => Scrapers.Humble()),
        ("roblox", "Roblox", _ => Scrapers.Roblox()),
        ("minecraft", "Minecraft (Java, Prism, MultiMC)", _ => Scrapers.Minecraft()),
        ("xbox", "Xbox / Game Pass", _ => Scrapers.Xbox()),
        ("itch", "itch.io", _ => Scrapers.Itch()),
        ("folders", "Game folders", s => Scrapers.Folders(s.ScanFolders)),
        ("shortcuts", "Emulator & custom shortcuts", _ => Scrapers.Shortcuts()),
    };

    public static List<Game> Load(Settings settings, bool includeHidden = false)
    {
        var games = new List<Game>();
        // Custom games first so they win name clashes with scanned copies.
        foreach (var c in settings.CustomGames) Try("Custom " + c.Name, () => games.Add(FromCustom(c)));
        foreach (var (key, name, scan) in Sources)
            if (settings.SourceOn(key)) Try(name, () => games.AddRange(scan(settings)));

        var hidden = new HashSet<string>(settings.Hidden, StringComparer.OrdinalIgnoreCase);
        var removed = new HashSet<string>(settings.Removed, StringComparer.OrdinalIgnoreCase);
        foreach (var g in games)
        {
            g.Hidden = hidden.Contains(g.Name);
            g.Removed = removed.Contains(g.Name);
            if (settings.Artwork.TryGetValue(g.Name, out var art) && File.Exists(art))
            {
                g.ImagePath = art;
                g.CustomArt = true;
            }
            // Non-Steam games matched to a Steam app (automatically or via Find artwork) borrow its art.
            if (g.SteamAppId == null && settings.SteamIds.TryGetValue(g.Name, out var sid) && sid != "")
                g.UseSteamArt(sid, cover: g.StoreArtUrl == null);
        }
        return games
            .Where(g => includeHidden || (!g.Hidden && !g.Removed))
            .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Select(grp => grp.First())
            .OrderByDescending(g => settings.LastPlayed.TryGetValue(g.Name, out var t) ? t : DateTime.MinValue)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static void Try(string what, Action a)
    {
        try { a(); } catch (Exception ex) { Log.Write($"{what} scan failed: {ex.Message}"); }
    }

    static Game FromCustom(CustomGame c)
    {
        bool isFile = File.Exists(c.Path);
        string dir = c.ProcessDir;
        if (string.IsNullOrEmpty(dir) && isFile) dir = Path.GetDirectoryName(c.Path) ?? "";
        return new Game
        {
            Name = string.IsNullOrWhiteSpace(c.Name) ? Path.GetFileNameWithoutExtension(c.Path) : c.Name,
            Source = GameSource.Custom,
            LaunchTarget = c.Path,
            LaunchArgs = c.Args,
            WorkingDir = string.IsNullOrEmpty(c.WorkingDir) ? (isFile ? Path.GetDirectoryName(c.Path) ?? "" : "") : c.WorkingDir,
            InstallDir = dir,
            ImagePath = string.IsNullOrEmpty(c.Image) ? null : c.Image,
            IconExe = isFile ? c.Path : null,
            IsApp = c.IsApp,
        };
    }

    // ---- Steam ----

    static readonly Regex VdfPair = new("\"([^\"]+)\"\\s+\"([^\"]*)\"", RegexOptions.Compiled);
    static readonly string[] SteamNonGames =
        { "Steamworks Common Redistributables", "Proton", "Steam Linux Runtime", "SteamVR", "Steam Audio", "Wallpaper Engine" };

    static IEnumerable<Game> ScanSteam()
    {
        var steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath)) yield break;
        steamPath = steamPath.Replace('/', '\\');
        SteamExe = Path.Combine(steamPath, "steam.exe");

        var libraries = new List<string> { steamPath };
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in VdfPair.Matches(File.ReadAllText(vdf)))
                if (m.Groups[1].Value == "path") libraries.Add(m.Groups[2].Value.Replace(@"\\", @"\"));

        var seen = new HashSet<string>();
        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
            {
                var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in VdfPair.Matches(File.ReadAllText(acf)))
                    kv.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
                if (!kv.TryGetValue("appid", out var id) || !kv.TryGetValue("name", out var name)) continue;
                if (id == "228980" || SteamNonGames.Any(n => name.StartsWith(n, StringComparison.OrdinalIgnoreCase))) continue;
                if (!seen.Add(id)) continue;
                kv.TryGetValue("installdir", out var installDir);
                var game = new Game
                {
                    Name = name,
                    Source = GameSource.Steam,
                    LaunchTarget = SteamExe,
                    LaunchArgs = $"-silent steam://rungameid/{id}",
                    InstallDir = Path.Combine(apps, "common", installDir ?? name),
                    ImagePath = FindSteamArt(steamPath, id, $"{id}_library_600x900.jpg", $"{id}_header.jpg",
                        "library_600x900*.jpg", "library_capsule*.jpg", "header*.jpg"),
                    HeroPath = FindSteamArt(steamPath, id, $"{id}_library_hero.jpg", "library_hero.jpg", "library_hero*.jpg"),
                    LogoPath = FindSteamArt(steamPath, id, $"{id}_logo.png", "logo.png", "logo*.png"),
                };
                game.UseSteamArt(id, cover: game.ImagePath == null);
                yield return game;
            }
        }
    }

    /// <summary>
    /// First match in Steam's local art cache. Older clients keep flat "{appid}_name.jpg" files;
    /// newer ones a folder per app, sometimes with hashed subfolders. Patterns with "*" search the folder.
    /// </summary>
    static string? FindSteamArt(string steamPath, string appId, params string[] names)
    {
        var cache = Path.Combine(steamPath, "appcache", "librarycache");
        if (!Directory.Exists(cache)) return null;
        var appDir = Path.Combine(cache, appId);
        foreach (var name in names)
        {
            if (!name.Contains('*'))
            {
                var p = Path.Combine(cache, name);
                if (File.Exists(p)) return p;
                p = Path.Combine(appDir, name);
                if (File.Exists(p)) return p;
            }
            else if (Directory.Exists(appDir))
            {
                var hit = Directory.GetFiles(appDir, name, SearchOption.AllDirectories).FirstOrDefault();
                if (hit != null) return hit;
            }
        }
        return null;
    }

    // ---- Epic ----

    /// <summary>
    /// Catalog item id -> portrait cover URL, from the Epic launcher's local catalog cache
    /// (a base64-encoded JSON array of the store entries for games you own).
    /// </summary>
    static Dictionary<string, (string? tall, string? wide)> EpicCovers()
    {
        var map = new Dictionary<string, (string?, string?)>();
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Catalog", "catcache.bin");
        try
        {
            if (!File.Exists(path)) return map;
            using var doc = JsonDocument.Parse(Convert.FromBase64String(File.ReadAllText(path).Trim()));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var id) || !item.TryGetProperty("keyImages", out var images)) continue;
                string? tall = null, box = null;
                foreach (var img in images.EnumerateArray())
                {
                    var type = img.GetProperty("type").GetString();
                    var url = img.GetProperty("url").GetString();
                    if (type == "DieselGameBoxTall") tall = url;
                    else if (type == "DieselGameBox") box = url;
                }
                if (tall != null || box != null) map[id.GetString()!] = (tall ?? box, box);
            }
        }
        catch (Exception ex) { Log.Write("Epic catalog cache: " + ex.Message); }
        return map;
    }

    static IEnumerable<Game> ScanEpic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;
        var covers = EpicCovers();
        foreach (var file in Directory.GetFiles(dir, "*.item"))
        {
            Game? game = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                if (r.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array &&
                    !cats.EnumerateArray().Any(c => c.GetString() == "games")) continue;
                var install = Str("InstallLocation");
                var exe = Path.Combine(install, Str("LaunchExecutable"));
                game = new Game
                {
                    Name = Str("DisplayName"),
                    Source = GameSource.Epic,
                    LaunchTarget = $"com.epicgames.launcher://apps/{Str("CatalogNamespace")}%3A{Str("CatalogItemId")}%3A{Str("AppName")}?action=launch&silent=true",
                    InstallDir = install,
                    IconExe = File.Exists(exe) ? exe : null,
                    StoreArtUrl = covers.GetValueOrDefault(Str("CatalogItemId")).tall,
                    HeroUrls = covers.GetValueOrDefault(Str("CatalogItemId")).wide is string wide ? new() { wide } : new(),
                };
            }
            catch (Exception ex) { Log.Write($"Epic manifest {file}: {ex.Message}"); }
            if (game != null && game.Name != "") yield return game;
        }
    }
}

/// <summary>Finds and manages the processes/windows that belong to a game, by install folder.</summary>
static class GameProcesses
{
    static Dictionary<int, string> _snapshot = new();

    /// <summary>Refreshes the pid -> exe path snapshot. Call once per tick, then query.</summary>
    public static void Refresh()
    {
        var map = new Dictionary<int, string>();
        int me = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == me || p.Id <= 4) continue;
                var path = Native.GetProcessPath(p.Id);
                if (path != null) map[p.Id] = path;
            }
        }
        _snapshot = map;
    }

    public static HashSet<int> PidsFor(Game g)
    {
        var set = new HashSet<int>();
        if (string.IsNullOrEmpty(g.InstallDir)) return set;
        var dir = g.InstallDir.TrimEnd('\\') + "\\";
        foreach (var (pid, path) in _snapshot)
            if (path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) set.Add(pid);
        return set;
    }

    // Background helpers games and launchers leave running after you quit (crash reporters, updaters,
    // anti-cheat, web helpers). They still belong to the game for closing it, but don't mean "running".
    static readonly System.Text.RegularExpressions.Regex Helper = new(
        @"crash|report|update|unins|setup|install|helper|service|cefprocess|webhelper|easyanticheat|battleye|eac_|vanguard|overlay|redist|launcherpatcher",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>True when the game itself (not just a leftover helper process) is running.</summary>
    public static bool IsRunning(Game g)
    {
        foreach (var pid in PidsFor(g))
            if (_snapshot.TryGetValue(pid, out var path) && !Helper.IsMatch(Path.GetFileNameWithoutExtension(path)))
                return true;
        return false;
    }

    /// <summary>
    /// True when the game is running with a window (minimized counts). Background-only processes,
    /// like a player that lingers after you close it, don't make a game show as running.
    /// </summary>
    public static bool IsShowing(Game g)
    {
        var pids = new HashSet<int>(PidsFor(g).Where(pid => _snapshot.TryGetValue(pid, out var path) && !Helper.IsMatch(Path.GetFileNameWithoutExtension(path))));
        return pids.Count > 0 && Native.GetMainWindows(pids).Count > 0;
    }

    public static IntPtr MainWindowFor(Game g)
    {
        var pids = PidsFor(g);
        if (pids.Count == 0) return IntPtr.Zero;
        var wins = Native.GetMainWindows(pids);
        return wins.Count == 0 ? IntPtr.Zero : wins.MaxBy(w => w.area).hWnd;
    }

    /// <summary>Asks the game to close, then force-kills whatever is left.</summary>
    public static async Task CloseAsync(Game g)
    {
        Refresh();
        var pids = PidsFor(g);
        foreach (var w in Native.GetMainWindows(pids)) Native.PostMessage(w.hWnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        for (int i = 0; i < 16 && pids.Count > 0; i++)
        {
            await Task.Delay(250);
            pids.RemoveWhere(pid => { try { using var p = Process.GetProcessById(pid); return p.HasExited; } catch { return true; } });
        }
        foreach (var pid in pids)
        {
            try { using var p = Process.GetProcessById(pid); p.Kill(true); }
            catch (Exception ex) { Log.Write($"Kill {pid} failed: {ex.Message}"); }
        }
    }
}
