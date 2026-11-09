using System.Diagnostics;
using Microsoft.Win32;

namespace Twinstar;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--edit"))
        {
            Settings.Load();
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Settings.FilePath}\""));
            return;
        }
        if (args.Contains("--list"))
        {
            // Diagnostic: writes the detected library to games.txt next to settings.json.
            var games = GameLibrary.Load(Settings.Load());
            File.WriteAllLines(Path.Combine(Settings.Dir, "games.txt"),
                games.Select(g => $"{g.Source}\t{g.Name}\t{g.InstallDir}\t{g.ImagePath ?? "(no art)"}")
                    .Append("--- installed apps (Settings > Add an app) ---")
                    .Concat(Scrapers.StartMenuApps().Select(a => $"{a.Name}\t{a.Target}")));
            return;
        }

        if (args.Contains("--art"))
        {
            // Diagnostic dry run: what "Get missing artwork" would pick, written to art.txt. Saves nothing.
            var s = Settings.Load();
            var lines = new List<string>();
            foreach (var g in GameLibrary.Load(s).Where(g => g.IsIcon))
            {
                var results = Artwork.SearchAsync(g.Name, s, g).GetAwaiter().GetResult();
                var m = Artwork.BestMatch(g.Name, results);
                var cover = m == null ? null : Artwork.FetchCoverAsync(m, s).GetAwaiter().GetResult();
                lines.Add($"{g.Name}\t-> {(m == null ? "(no confident match)" : $"{m.Name} [{m.Source} {m.Id}] cover {(cover == null ? "MISSING" : cover.Length / 1024 + " KB")}")}" +
                          $"\t(top results: {string.Join(", ", results.Take(3).Select(r => r.Name))})");
            }
            File.WriteAllLines(Path.Combine(Settings.Dir, "art.txt"), lines);
            return;
        }

        if (args.Length >= 3 && args[0] == "--migrate-test")
        {
            Settings.MigrateFolder(args[1], args[2]);
            return;
        }

        if (args.Length >= 2 && args[0] == "--intro-frames")
        {
            ApplicationConfiguration.Initialize();
            using var form = new MainForm(isShell: false);
            form.RenderIntroFrames(args[1], 60);
            return;
        }

        if (args.Length >= 2 && args[0] == "--render")
        {
            // Diagnostic: draws the console home off-screen to a PNG (lets art downloads finish first).
            ApplicationConfiguration.Initialize();
            using var form = new MainForm(isShell: false);
            form.RenderForPreview(args[1], new Size(1920, 1080), args.Length > 2 ? args[2] : "home");
            return;
        }

        if (args.Length >= 1 && args[0] == "--ambient-test")
        {
            // Diagnostic: plays the selector loop, crossfades to the home loop, then fades out.
            Ambient.Set(true, Ambient.Track.Selector);
            Thread.Sleep(6000);
            Ambient.Set(true, Ambient.Track.Home);
            Thread.Sleep(6000);
            Ambient.Set(false);
            Thread.Sleep(1500);
            Log.Write("ambient-test: " + Ambient.Describe());
            return;
        }

        if (args.Length >= 2 && args[0] == "--ambient-wav")
        {
            Ambient.ExportWav(args[1], args.Length > 2 && args[2] == "selector" ? Ambient.Track.Selector : Ambient.Track.Home);
            return;
        }

        if (args.Length >= 3 && args[0] == "--sfx-wav")
        {
            Sounds.Export(Enum.Parse<Sfx>(args[1], true), args[2]);
            return;
        }

        if (args.Length >= 2 && args[0] == "--audio-test")
        {
            // Diagnostic: mute one process, record the state, unmute it again.
            int pid = int.Parse(args[1]);
            GameAudio.Mute(new[] { pid });
            var during = GameAudio.Describe().Where(l => l.Contains($"pid {pid} "));
            GameAudio.UnmuteAll();
            var after = GameAudio.Describe().Where(l => l.Contains($"pid {pid} "));
            File.WriteAllLines(Path.Combine(Settings.Dir, "audio.txt"), during.Select(l => "muted:   " + l).Concat(after.Select(l => "unmuted: " + l)));
            return;
        }

        if (args.Contains("--controllers"))
        {
            // Diagnostic: controller support per game as the console would decide it (nothing saved).
            var s = Settings.Load();
            var lines = GameLibrary.Load(s).Where(g => !g.IsApp).Select(g =>
            {
                string support = ControllerSupport.DetermineAsync(g.SteamAppId, g.InstallDir, g.Source == GameSource.Steam).GetAwaiter().GetResult();
                bool ps = g.Source != GameSource.Steam && ControllerSupport.BinariesSupportPlayStation(g.InstallDir);
                string mode = g.Source == GameSource.Steam ? "Steam Input" : support == ControllerSupport.None ? "keyboard & mouse mapper"
                    : ps ? "native (PlayStation pads supported, nothing needed)" : "controller (DS4Windows with a PS pad)";
                return $"{g.Name}: {support} -> {mode}";
            }).ToList();
            lines.Add("DS4Windows: " + (Ds4Windows.Find(s.Ds4WindowsPath) ?? "not found"));
            File.WriteAllLines(Path.Combine(Settings.Dir, "controllers.txt"), lines);
            return;
        }

        if (args.Contains("--audio-sessions"))
        {
            List<string> lines = new();
            var t = new Thread(() => lines = GameAudio.Describe());
            t.SetApartmentState(ApartmentState.MTA);
            t.Start(); t.Join();
            File.WriteAllLines(Path.Combine(Settings.Dir, "audio.txt"), lines);
            return;
        }

        if (args.Contains("--pads"))
        {
            // Diagnostic: logs controllers and button presses for 8 seconds.
            ApplicationConfiguration.Initialize();
            var pad = new Gamepad();
            var probe = new PadProbe(pad);
            pad.ConnectionChanged += n => Log.Write($"pads: {n} connected, prompts={pad.Kind} ({pad.Name})");
            pad.Pressed += b => Log.Write($"pads: pressed {b} on {pad.Name}");
            var t = new System.Windows.Forms.Timer { Interval = 16 };
            t.Tick += (_, _) => pad.Poll();
            t.Start();
            var quit = new System.Windows.Forms.Timer { Interval = 8000 };
            quit.Tick += (_, _) => Application.Exit();
            quit.Start();
            probe.CreateControl();
            Application.Run();
            return;
        }

        using var mutex = new Mutex(true, @"Local\Twinstar.SingleInstance", out bool first);
        if (!first) return;

        Settings.MigrateFromOldName();
        bool isShell = IsRegisteredShell();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Write("Fatal: " + e.ExceptionObject);
            // Never leave the user on a black screen: hand the session to Explorer.
            Native.SuppressTaskbar = false;
            if (isShell && !Native.ExplorerRunning())
                Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        };
        Application.ThreadException += (_, e) => Log.Write("UI error: " + e.Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        ApplicationConfiguration.Initialize();
        Native.InstallKeyboardHook();
        Native.InstallTaskbarHook();
        try
        {
            var form = new MainForm(isShell);
            Ambient.RestoreSessionVolume();
            form.PrefetchLibrary(); // scan in the background while the selector appears
            form.Shown += (_, _) =>
            {
                if (args.Contains("--console")) form.EnterConsole();
                else if (args.Contains("--desktop")) form.EnterDesktop();
                else form.ShowSelector();
            };
            Application.Run(form);
        }
        finally
        {
            Native.RemoveKeyboardHook();
            Native.RemoveTaskbarHook();
            Native.SuppressTaskbar = false;
        }
    }

    /// <summary>True when Windows launches us at sign-in instead of Explorer (Winlogon Shell value).</summary>
    static bool IsRegisteredShell()
    {
        const string key = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var k = hive.OpenSubKey(key);
            if (k?.GetValue("Shell") is string s && s.Contains("Twinstar", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

/// <summary>Invisible window that receives raw HID input for the --pads diagnostic.</summary>
sealed class PadProbe : Control
{
    readonly Gamepad _pad;
    public PadProbe(Gamepad pad) => _pad = pad;
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); _pad.RegisterRawInput(Handle); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Gamepad.WM_INPUT) _pad.HandleRawInput(m.LParam);
        else if (m.Msg == Gamepad.WM_INPUT_DEVICE_CHANGE) _pad.HandleDeviceChange(m.WParam, m.LParam);
        base.WndProc(ref m);
    }
}
