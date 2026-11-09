using System.Text.Json;
using System.Text.Json.Serialization;

namespace Twinstar;

sealed class CustomGame
{
    public string Name { get; set; } = "";
    /// <summary>Exe, shortcut or URI (e.g. steam://, com.epicgames.launcher://) to launch.</summary>
    public string Path { get; set; } = "";
    public string Args { get; set; } = "";
    public string WorkingDir { get; set; } = "";
    /// <summary>Optional cover image (portrait works best).</summary>
    public string Image { get; set; } = "";
    /// <summary>Folder whose processes count as "the game". Defaults to the exe's folder.</summary>
    public string ProcessDir { get; set; } = "";
    /// <summary>Added through "Add an app": listed under the Apps tab instead of Games.</summary>
    public bool IsApp { get; set; }
}

sealed class Settings
{
    /// <summary>Library source key -> enabled. Missing keys count as enabled.</summary>
    public Dictionary<string, bool> Sources { get; set; } = new();
    /// <summary>Folders where every subfolder is a game (like Playnite's folder scan).</summary>
    public List<string> ScanFolders { get; set; } = new();
    /// <summary>Names seen by the last scan, used to report what is new.</summary>
    public List<string> KnownGames { get; set; } = new();
    /// <summary>Starts Steam with -silent on entering console mode so its window never pops up.</summary>
    public bool StartSteamSilently { get; set; } = true;
    public List<CustomGame> CustomGames { get; set; } = new();
    /// <summary>Game names excluded from the library permanently (scans never bring them back).</summary>
    public List<string> Hidden { get; set; } = new();
    /// <summary>Game names removed until the next manual scan.</summary>
    public List<string> Removed { get; set; } = new();
    /// <summary>Game name -> matched Steam app id ("" = looked up, no match). Gives non-Steam games Steam art.</summary>
    public Dictionary<string, string> SteamIds { get; set; } = new();
    /// <summary>Game name -> cover image chosen with "Find artwork".</summary>
    public Dictionary<string, string> Artwork { get; set; } = new();
    /// <summary>Optional free key from steamgriddb.com/profile/preferences/api for more artwork choices.</summary>
    public string SteamGridDbKey { get; set; } = "";
    /// <summary>Soft navigation/select sounds in the console UI.</summary>
    public bool UiSounds { get; set; } = true;
    /// <summary>Look of the Console/Desktop selector: "mint" (default), "aurora" or "ember".</summary>
    public string SelectorTheme { get; set; } = "mint";
    /// <summary>Calm background music on the console home screen and menus.</summary>
    public bool AmbientMusic { get; set; } = true;
    /// <summary>Game name -> controller support from Steam's store ("full", "partial", "none").</summary>
    public Dictionary<string, string> ControllerSupport { get; set; } = new();
    /// <summary>Game name -> forced controller mode ("controller", "kbm"); absent = automatic.</summary>
    public Dictionary<string, string> ControllerMode { get; set; } = new();
    /// <summary>Start DS4Windows for non-Steam controller games when a PlayStation pad is used.</summary>
    public bool UseDs4Windows { get; set; } = true;
    /// <summary>Game name -> whether the game reads PlayStation pads itself (then DS4Windows is never started).</summary>
    public Dictionary<string, bool> PlayStationNative { get; set; } = new();
    /// <summary>Optional path to DS4Windows.exe; found automatically when empty.</summary>
    public string Ds4WindowsPath { get; set; } = "";
    /// <summary>Your own free Steam Web API key (steamcommunity.com/dev/apikey), for the Friends tab.</summary>
    public string SteamApiKey { get; set; } = "";
    /// <summary>Optional SteamID64 override; normally read from the Steam client.</summary>
    public string SteamId { get; set; } = "";
    /// <summary>Client ID of your own Discord application (discord.com/developers), for Rich Presence.</summary>
    public string DiscordClientId { get; set; } = "";
    public Dictionary<string, DateTime> LastPlayed { get; set; } = new();

    public bool SourceOn(string key) => !Sources.TryGetValue(key, out var on) || on;

    public static string Dir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Twinstar");
    public static string FilePath => System.IO.Path.Combine(Dir, "settings.json");

    /// <summary>Twinstar used to be called Hybrid Console: brings its settings, artwork and shortcuts over, once.</summary>
    public static void MigrateFromOldName() => MigrateFolder(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HybridConsole"), Dir);

    public static void MigrateFolder(string oldDir, string newDir)
    {
        var marker = System.IO.Path.Combine(newDir, ".migrated");
        if (!Directory.Exists(oldDir) || File.Exists(marker)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(oldDir, "*", SearchOption.AllDirectories))
            {
                var dest = System.IO.Path.Combine(newDir, System.IO.Path.GetRelativePath(oldDir, file));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
            // Chosen artwork is saved as full paths into the data folder.
            var settings = System.IO.Path.Combine(newDir, "settings.json");
            if (File.Exists(settings))
                File.WriteAllText(settings, File.ReadAllText(settings).Replace(oldDir.Replace("\\", "\\\\"), newDir.Replace("\\", "\\\\")));
            File.WriteAllText(marker, "");
            Directory.Delete(oldDir, recursive: true);
        }
        catch (Exception ex) { Log.Write("Migration from the old folder failed: " + ex.Message); }
    }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception ex) { Log.Write("Settings load failed: " + ex.Message); }
        var s = new Settings();
        s.Save();
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) { Log.Write("Settings save failed: " + ex.Message); }
    }
}

static class Log
{
    static readonly object Gate = new();
    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.Dir);
                File.AppendAllText(System.IO.Path.Combine(Settings.Dir, "log.txt"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
