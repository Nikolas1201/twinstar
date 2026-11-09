using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Twinstar;

/// <summary>Playnite-style library scrapers for stores beyond Steam/Epic, plus the installed-apps list.</summary>
static class Scrapers
{
    static readonly Regex NotAGameExe = new(
        @"unins|uninstall|setup|install|crash|report|redist|vc_?redist|dxsetup|directx|dotnet|helper|update|patch|config|launcherpatcher|ue4prereq|vcredist|easyanticheat|battleye|cefprocess|webhelper",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ---- GOG Galaxy / GOG offline installers ----

    public static IEnumerable<Game> Gog()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (root == null) yield break;
        foreach (var id in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(id);
            if (k == null) continue;
            var exe = k.GetValue("exe") as string ?? k.GetValue("EXE") as string ?? "";
            var name = k.GetValue("gameName") as string ?? k.GetValue("GAMENAME") as string ?? "";
            var dir = k.GetValue("path") as string ?? k.GetValue("PATH") as string ?? "";
            if (name == "" || !File.Exists(exe)) continue; // DLC entries have no exe
            yield return new Game
            {
                Name = name,
                Source = GameSource.Custom,
                LaunchTarget = exe,
                LaunchArgs = k.GetValue("launchParam") as string ?? "",
                Store = "GOG",
                WorkingDir = k.GetValue("workingDir") as string ?? dir,
                InstallDir = dir,
                IconExe = exe,
            };
        }
    }

    // ---- Ubisoft Connect ----

    public static IEnumerable<Game> Ubisoft()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
        if (root == null) yield break;
        foreach (var id in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(id);
            var dir = (k?.GetValue("InstallDir") as string)?.Replace('/', '\\').TrimEnd('\\');
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            yield return new Game
            {
                Name = Path.GetFileName(dir),
                Source = GameSource.Custom,
                LaunchTarget = $"uplay://launch/{id}/0",
                InstallDir = dir,
                IconExe = MainExe(dir),
            };
        }
    }

    // ---- EA / Battle.net / Riot: their games register normal uninstall entries ----

    public static IEnumerable<Game> ByPublisher(string publisher, params string[] excludeNames)
    {
        foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var root = hive.OpenSubKey(path);
            if (root == null) continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k == null) continue;
                var pub = k.GetValue("Publisher") as string ?? "";
                var name = k.GetValue("DisplayName") as string ?? "";
                if (!pub.Contains(publisher, StringComparison.OrdinalIgnoreCase) || name == "") continue;
                if (excludeNames.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase))) continue;
                var dir = (k.GetValue("InstallLocation") as string ?? "").Trim('"').TrimEnd('\\');
                var icon = (k.GetValue("DisplayIcon") as string ?? "").Split(',')[0].Trim('"');
                string? exe = File.Exists(icon) && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !NotAGameExe.IsMatch(Path.GetFileName(icon))
                    ? icon : (Directory.Exists(dir) ? MainExe(dir) : null);
                if (exe == null) continue;
                if (dir == "" || !Directory.Exists(dir)) dir = Path.GetDirectoryName(exe)!;
                yield return new Game
                {
                    Name = name,
                    Source = GameSource.Custom,
                    LaunchTarget = exe,
                    WorkingDir = Path.GetDirectoryName(exe)!,
                    InstallDir = dir,
                    IconExe = exe,
                };
            }
        }
    }

    // ---- Xbox app / Game Pass (X:\XboxGames\<game>\Content) ----

    public static IEnumerable<Game> Xbox()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            var root = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            if (!Directory.Exists(root)) continue;
            foreach (var gameDir in Directory.GetDirectories(root))
            {
                var content = Path.Combine(gameDir, "Content");
                var cfg = Path.Combine(content, "MicrosoftGame.config");
                if (!File.Exists(cfg)) continue;
                Game? game = null;
                try
                {
                    var doc = XDocument.Load(cfg);
                    var exeName = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Executable")?.Attribute("Name")?.Value;
                    var visuals = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals");
                    var display = visuals?.Attribute("DefaultDisplayName")?.Value;
                    var exe = exeName != null ? Path.Combine(content, exeName) : MainExe(content);
                    if (exe == null || !File.Exists(exe)) continue;
                    string? art = null;
                    foreach (var attr in new[] { "Square480x480Logo", "Square150x150Logo", "StoreLogo" })
                    {
                        var rel = visuals?.Attribute(attr)?.Value;
                        if (rel != null && File.Exists(Path.Combine(content, rel))) { art = Path.Combine(content, rel); break; }
                    }
                    game = new Game
                    {
                        Name = string.IsNullOrWhiteSpace(display) || display.StartsWith("ms-resource") ? Path.GetFileName(gameDir) : display,
                        Source = GameSource.Custom,
                        LaunchTarget = exe,
                        WorkingDir = Path.GetDirectoryName(exe)!,
                        InstallDir = content,
                        ImagePath = art,
                        IconExe = exe,
                    };
                }
                catch (Exception ex) { Log.Write($"Xbox config {cfg}: {ex.Message}"); }
                if (game != null) yield return game;
            }
        }
    }

    // ---- Roblox (player, or Bloxstrap/Fishstrap if installed) ----

    public static IEnumerable<Game> Roblox()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var versions = Path.Combine(local, "Roblox", "Versions");
        // Prefer a custom bootstrapper, then Roblox's own Start Menu shortcut (it survives updates,
        // which move the player into a new version folder), then the newest player exe.
        string? target = null, args = "";
        foreach (var strap in new[] { "Bloxstrap", "Fishstrap" })
        {
            var exe = Path.Combine(local, strap, strap + ".exe");
            if (File.Exists(exe)) { target = exe; args = "-player"; break; }
        }
        var lnk = Path.Combine(roaming, @"Microsoft\Windows\Start Menu\Programs\Roblox\Roblox Player.lnk");
        if (target == null && File.Exists(lnk)) target = lnk;
        if (target == null && Directory.Exists(versions))
            target = Directory.GetFiles(versions, "RobloxPlayerBeta.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
        if (target == null) yield break;
        var icon = Directory.Exists(versions)
            ? Directory.GetFiles(versions, "RobloxPlayerBeta.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        yield return new Game
        {
            Name = "Roblox",
            Source = GameSource.Custom,
            Store = "Roblox",
            LaunchTarget = target,
            LaunchArgs = args,
            InstallDir = Directory.Exists(versions) ? versions : Path.GetDirectoryName(target)!,
            IconExe = icon ?? (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? target : null),
        };
    }

    // ---- Minecraft: official Java launcher, plus every Prism Launcher / MultiMC instance ----

    public static IEnumerable<Game> Minecraft()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var exe in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Minecraft Launcher", "MinecraftLauncher.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Minecraft Launcher", "MinecraftLauncher.exe"),
        })
        {
            if (!File.Exists(exe)) continue;
            yield return new Game
            {
                Name = "Minecraft Launcher", Source = GameSource.Custom, Store = "Minecraft",
                LaunchTarget = exe, WorkingDir = Path.GetDirectoryName(exe)!, InstallDir = Path.GetDirectoryName(exe)!, IconExe = exe,
            };
            break;
        }

        // Instance launchers: each modpack/version gets its own tile and starts straight into the game.
        var launchers = new (string data, string[] exes, string arg)[]
        {
            (Path.Combine(roaming, "PrismLauncher"), new[] { Path.Combine(local, "Programs", "PrismLauncher", "prismlauncher.exe"), Path.Combine(roaming, "PrismLauncher", "prismlauncher.exe") }, "--launch"),
            (Path.Combine(roaming, "MultiMC"), new[] { Path.Combine(roaming, "MultiMC", "MultiMC.exe"), Path.Combine(local, "MultiMC", "MultiMC.exe") }, "--launch"),
        };
        foreach (var (data, exes, arg) in launchers)
        {
            var exe = exes.FirstOrDefault(File.Exists);
            var instances = Path.Combine(data, "instances");
            if (exe == null || !Directory.Exists(instances)) continue;
            foreach (var dir in Directory.GetDirectories(instances))
            {
                var cfg = Path.Combine(dir, "instance.cfg");
                if (!File.Exists(cfg)) continue;
                var name = File.ReadLines(cfg).FirstOrDefault(l => l.StartsWith("name="))?[5..].Trim();
                if (string.IsNullOrEmpty(name)) name = Path.GetFileName(dir);
                yield return new Game
                {
                    Name = $"Minecraft: {name}", Source = GameSource.Custom, Store = "Minecraft",
                    LaunchTarget = exe, LaunchArgs = $"{arg} \"{Path.GetFileName(dir)}\"",
                    WorkingDir = Path.GetDirectoryName(exe)!,
                    InstallDir = Path.GetDirectoryName(exe)!, // the game itself runs in Java; the launcher stays alive with it
                    IconExe = exe,
                };
            }
        }
    }

    // ---- Humble App (installed games listed in its config) ----

    public static IEnumerable<Game> Humble()
    {
        var cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Humble App", "config.json");
        if (!File.Exists(cfg)) yield break;
        var found = new List<Game>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfg));
            // Games sit in an array under a versioned key ("game-collection-4"); accept any array of game objects.
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                foreach (var item in prop.Value.EnumerateArray())
                {
                    if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    string Str(string k) => item.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
                    var status = Str("status");
                    var dir = Str("filePath");
                    var exeRel = Str("executablePath");
                    if (status is not ("installed" or "downloaded") || dir == "" || exeRel == "") continue;
                    var exe = Path.IsPathRooted(exeRel) ? exeRel : Path.Combine(dir, exeRel);
                    if (!File.Exists(exe)) continue;
                    found.Add(new Game
                    {
                        Name = Str("gameName") is { Length: > 0 } n ? n : Path.GetFileName(dir),
                        Source = GameSource.Custom, Store = "Humble",
                        LaunchTarget = exe, WorkingDir = Path.GetDirectoryName(exe)!, InstallDir = dir, IconExe = exe,
                    });
                }
            }
        }
        catch (Exception ex) { Log.Write("Humble config: " + ex.Message); }
        foreach (var g in found) yield return g;
    }

    // ---- Emulator game shortcuts + the drop-in Shortcuts folder ----

    // Emulator exe name (lower-case, no extension) prefix -> display name.
    static readonly (string exe, string name)[] Emulators =
    {
        ("ryujinx", "Ryujinx"), ("yuzu", "yuzu"), ("suyu", "suyu"), ("sudachi", "Sudachi"), ("citron", "Citron"), ("eden", "Eden"),
        ("rpcs3", "RPCS3"), ("pcsx2", "PCSX2"), ("duckstation", "DuckStation"), ("dolphin", "Dolphin"), ("cemu", "Cemu"),
        ("ppsspp", "PPSSPP"), ("retroarch", "RetroArch"), ("xemu", "xemu"), ("xenia", "Xenia"), ("vita3k", "Vita3K"),
        ("citra", "Citra"), ("lime3ds", "Lime3DS"), ("azahar", "Azahar"), ("melonds", "melonDS"), ("mgba", "mGBA"),
        ("snes9x", "Snes9x"), ("project64", "Project64"), ("flycast", "Flycast"), ("redream", "Redream"), ("shadps4", "shadPS4"),
    };

    public static string ShortcutsFolder => Path.Combine(Settings.Dir, "Shortcuts");

    /// <summary>
    /// Games from shortcuts: emulator game shortcuts on the desktop / Start Menu (each becomes its own
    /// game, labelled with its emulator), plus every shortcut placed in the Shortcuts folder.
    /// </summary>
    public static IEnumerable<Game> Shortcuts()
    {
        Directory.CreateDirectory(ShortcutsFolder);
        var found = new List<Game>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new (string dir, bool anyShortcut)[]
        {
            (ShortcutsFolder, true),
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), false),
            (Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), false),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), false),
        };
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
        foreach (var (dir, any) in roots)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", opts))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not (".lnk" or ".url")) continue;
                var name = Path.GetFileNameWithoutExtension(file).Replace(" - Shortcut", "").Trim();
                Game? game = ext == ".url" ? (any ? FromUrlShortcut(file, name) : null) : FromLinkShortcut(file, name, any);
                if (game != null && seen.Add(game.Name)) found.Add(game);
            }
        }
        return found;
    }

    static Game? FromLinkShortcut(string lnk, string name, bool any)
    {
        var (target, args, workDir, icon) = ResolveShortcut(lnk);
        if (string.IsNullOrEmpty(target)) return null;
        var exe = Path.GetFileNameWithoutExtension(target).ToLowerInvariant();
        var emulator = Emulators.FirstOrDefault(e => exe.StartsWith(e.exe)).name;
        // Outside the Shortcuts folder only emulator shortcuts that open a specific game count
        // (the emulator's own shortcut, with no arguments, is an app, not a game).
        if (!any && (emulator == null || string.IsNullOrWhiteSpace(args))) return null;
        var (iconFile, iconExe) = IconOf(icon, target);
        // PlayStation emulators: the game's own folder holds its official icon and background.
        var (psIcon, psBackground) = emulator is "shadPS4" or "RPCS3" ? PlayStationArt(args, target) : (null, null);
        if (psIcon != null) iconFile = psIcon;
        return new Game
        {
            Name = name,
            Source = GameSource.Custom,
            Store = emulator ?? "Shortcut",
            Emulated = emulator != null,
            LaunchTarget = lnk, // launching the shortcut itself keeps its exact arguments and working folder
            WorkingDir = string.IsNullOrEmpty(workDir) ? Path.GetDirectoryName(target) ?? "" : workDir,
            // Programs inside Windows' own folders aren't tracked: everything there would count as "this game".
            InstallDir = target.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
                ? "" : Path.GetDirectoryName(target) ?? "",
            ImagePath = iconFile,
            HeroPath = psBackground,
            IconExe = iconExe ?? (File.Exists(target) ? target : null),
        };
    }

    /// <summary>
    /// Finds a PS4/PS3 game's own artwork from its emulator shortcut. PS4 dumps keep it in
    /// sce_sys/icon0.png + pic1.png (the console's home-screen background); PS3 games in
    /// ICON0.PNG + PIC1.PNG next to PARAM.SFO. RPCS3 shortcuts may name the game by id instead of path.
    /// </summary>
    static (string? icon, string? background) PlayStationArt(string args, string emulatorExe)
    {
        var candidates = new List<string>();
        foreach (Match m in Regex.Matches(args, "\"([^\"]+)\"|(\\S+)"))
        {
            var token = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            var id = Regex.Match(token, @"%RPCS3_GAMEID%:(\w+)");
            if (id.Success) candidates.AddRange(Rpcs3GameDirs(id.Groups[1].Value, Path.GetDirectoryName(emulatorExe)!));
            else if (token.Contains('\\') || token.Contains('/')) candidates.Add(token.Replace('/', '\\'));
        }
        foreach (var start in candidates)
        {
            // Walk up from whatever was passed (eboot, executable, folder) to the game's root.
            var dir = File.Exists(start) ? Path.GetDirectoryName(start) : Directory.Exists(start) ? start : null;
            for (int depth = 0; dir != null && depth < 5; depth++, dir = Path.GetDirectoryName(dir))
            {
                foreach (var artDir in new[] { Path.Combine(dir, "sce_sys"), Path.Combine(dir, "PS3_GAME"), dir })
                {
                    var icon = FirstExisting(artDir, "icon0.png", "ICON0.PNG");
                    if (icon == null) continue;
                    return (icon, FirstExisting(artDir, "pic1.png", "PIC1.PNG", "pic0.png", "PIC0.PNG"));
                }
            }
        }
        return (null, null);
    }

    static string? FirstExisting(string dir, params string[] names) =>
        Directory.Exists(dir) ? names.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists) : null;

    /// <summary>RPCS3 game id -> folders: installed (dev_hdd0/game/ID) and disc games listed in games.yml.</summary>
    static IEnumerable<string> Rpcs3GameDirs(string id, string rpcs3Dir)
    {
        yield return Path.Combine(rpcs3Dir, "dev_hdd0", "game", id);
        foreach (var yml in new[] { Path.Combine(rpcs3Dir, "config", "games.yml"), Path.Combine(rpcs3Dir, "games.yml") })
        {
            if (!File.Exists(yml)) continue;
            foreach (var line in File.ReadLines(yml))
            {
                var m = Regex.Match(line, $@"^\s*{Regex.Escape(id)}\s*:\s*(.+?)\s*$");
                if (m.Success) yield return m.Groups[1].Value.Trim('"', '\'').Replace('/', '\\');
            }
        }
    }

    static Game? FromUrlShortcut(string url, string name)
    {
        var lines = File.ReadAllLines(url);
        string Val(string key) => lines.FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))?[(key.Length + 1)..] ?? "";
        if (Val("URL") == "") return null;
        var icon = Val("IconFile");
        return new Game
        {
            Name = name, Source = GameSource.Custom, Store = "Shortcut",
            LaunchTarget = url,
            ImagePath = icon.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) || icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? icon : null,
            IconExe = icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(icon) ? icon : null,
        };
    }

    /// <summary>Emulators often give game shortcuts the game's own icon (.ico/.png): use it as the tile art.</summary>
    static (string? file, string? exe) IconOf(string iconLocation, string target)
    {
        var path = iconLocation.Split(',')[0].Trim('"').Trim();
        if (path == "" || !File.Exists(path)) return (null, null);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".ico" or ".png" or ".jpg" ? (path, null) : ext is ".exe" or ".dll" ? (null, path) : (null, null);
    }

    static (string target, string args, string workDir, string icon) ResolveShortcut(string lnk)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            object shell = Activator.CreateInstance(shellType)!;
            object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk })!;
            string Prop(string p) => sc.GetType().InvokeMember(p, BindingFlags.GetProperty, null, sc, null) as string ?? "";
            return (Prop("TargetPath"), Prop("Arguments"), Prop("WorkingDirectory"), Prop("IconLocation"));
        }
        catch { return ("", "", "", ""); }
    }

    // ---- itch.io app installs ----

    public static IEnumerable<Game> Itch() =>
        Folders(new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "itch", "apps") });

    // ---- Folder scan: every subfolder is a game, its biggest real exe is the launcher ----

    public static IEnumerable<Game> Folders(IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var exe = MainExe(dir);
                if (exe == null) continue;
                yield return new Game
                {
                    Name = Path.GetFileName(dir),
                    Source = GameSource.Custom,
                    LaunchTarget = exe,
                    WorkingDir = Path.GetDirectoryName(exe)!,
                    InstallDir = dir,
                    IconExe = exe,
                };
            }
        }
    }

    public static string? MainExe(string dir)
    {
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
            return Directory.EnumerateFiles(dir, "*.exe", opts)
                .Where(f => !NotAGameExe.IsMatch(Path.GetFileName(f)))
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.Length)
                .FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    // ---- Installed apps (Start Menu shortcuts), for Settings → Add an app ----

    public sealed record InstalledApp(string Name, string Target, string Args, string WorkingDir);

    static readonly Regex NotAnApp = new(
        @"uninstall|readme|help|manual|documentation|website|support|license|release notes|changelog|setup|repair|safe mode",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<InstalledApp> StartMenuApps()
    {
        var result = new List<InstalledApp>();
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return result;
        object shell = Activator.CreateInstance(shellType)!;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\";
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var root in roots.Where(Directory.Exists))
        foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", opts))
        {
            var name = Path.GetFileNameWithoutExtension(lnk);
            if (NotAnApp.IsMatch(name)) continue;
            try
            {
                object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk })!;
                string Prop(string p) => sc.GetType().InvokeMember(p, BindingFlags.GetProperty, null, sc, null) as string ?? "";
                var target = Prop("TargetPath");
                if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) continue;
                if (NotAGameExe.IsMatch(Path.GetFileName(target)) || !seen.Add(target)) continue;
                if (target.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase)) continue; // control panel, installer icons
                result.Add(new InstalledApp(name, target, Prop("Arguments"), Prop("WorkingDirectory")));
            }
            catch { }
        }
        return result.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
