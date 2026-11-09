using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Microsoft.Win32;

namespace Twinstar;

enum Mode { Selector, Desktop, Console }
enum View { Selector, Home, Launching, InGame, Menu }

/// <summary>
/// One fullscreen window that is the mode selector, the console library, the guide overlay and,
/// while a game runs, the black backdrop behind it (so the desktop is never visible).
/// </summary>
sealed partial class MainForm : Form
{
    const int HotkeySelector = 1, HotkeyGuide = 2;

    readonly bool _isShell;
    Settings _settings = Settings.Load();
    List<Game> _games = new();
    HashSet<Game> _running = new();

    Mode _mode = Mode.Selector;
    View _view = View.Selector;
    int _selectorIndex;
    int _sel;
    int _menuIndex;
    float _scroll, _scrollTarget;
    int _cols = 6;

    Game? _active;
    long _launchStarted;
    int _missingTicks;
    long _lastRefresh;

    string? _toast;
    long _toastUntil;

    readonly Gamepad _pad = new();
    readonly System.Windows.Forms.Timer _padTimer = new() { Interval = 16 };
    readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 500 };
    readonly NotifyIcon _tray = new();
    readonly List<(RectangleF rect, Action click, Action? hover)> _hits = new();

    static readonly Color BgTop = Color.FromArgb(10, 14, 26), BgBottom = Color.FromArgb(20, 28, 48);
    static readonly Color Accent = Color.FromArgb(59, 130, 246), Green = Color.FromArgb(34, 197, 94);
    static readonly Color Panel = Color.FromArgb(17, 24, 39), Dim = Color.FromArgb(148, 163, 184);

    public MainForm(bool isShell)
    {
        _isShell = isShell;
        Text = "Twinstar";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        KeyPreview = true;
        ShowInTaskbar = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        FitToScreen();
        SystemEvents.DisplaySettingsChanged += (_, _) => BeginInvoke(FitToScreen);

        _pad.Pressed += OnButton;
        ArtCache.Downloaded += () => { if (IsHandleCreated) BeginInvoke(OnArtDownloaded); };
        Native.WindowShown += hwnd => Safe(() => OnWindowShown(hwnd));
        _pad.ConnectionChanged += count =>
        {
            if (!Visible || _view == View.InGame) return;
            if (count > 0) SetCursorHidden(true);
            Toast(count == 0 ? "Controller disconnected" : count == 1 ? $"{_pad.Name} connected" : $"{count} controllers connected");
        };
        _padTimer.Tick += (_, _) => { _pad.Poll(); PadTick(); };
        _statusTimer.Tick += (_, _) => StatusTick();
        _bgSettle.Tick += (_, _) => { _bgSettle.Stop(); Invalidate(); };
        _padTimer.Start();
        _statusTimer.Start();

        _tray.Text = "Twinstar (Ctrl+Alt+F12)";
        _tray.Icon = SystemIcons.Application;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Mode selector (Ctrl+Alt+F12)", null, (_, _) => ShowSelector());
        menu.Items.Add("Console mode", null, (_, _) => EnterConsole());
        menu.Items.Add("Edit games / settings…", null, (_, _) => OpenSettingsFile());
        menu.Items.Add("-");
        menu.Items.Add("Quit Twinstar", null, (_, _) => Quit());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowSelector();
    }

    void FitToScreen()
    {
        Bounds = Screen.PrimaryScreen!.Bounds;
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _pad.RegisterRawInput(Handle); // DualShock 4 / DualSense
        // The keyboard hook is the primary path (it wins even if another app grabbed the combo);
        // RegisterHotKey is the backup for when the hook isn't called, e.g. elevated foreground apps.
        Native.CtrlAltKey += vk => BeginInvoke(() => { if (vk == 0x7B) ShowSelector(); else OnButton(Btn.Guide); });
        Native.RegisterHotKey(Handle, HotkeySelector, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x7B); // F12
        Native.RegisterHotKey(Handle, HotkeyGuide, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x7A); // F11
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Gamepad.WM_INPUT) _pad.HandleRawInput(m.LParam); // still passed on: DefWindowProc frees the input
        else if (m.Msg == Gamepad.WM_INPUT_DEVICE_CHANGE) _pad.HandleDeviceChange(m.WParam, m.LParam);
        if (m.Msg == Native.WM_HOTKEY)
        {
            if ((int)m.WParam == HotkeySelector) ShowSelector();
            else if ((int)m.WParam == HotkeyGuide) OnButton(Btn.Guide);
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Alt+F4 must not kill the shell; quitting goes through the tray menu.
        if (e.CloseReason == CloseReason.UserClosing && !_quitting) { e.Cancel = true; return; }
        base.OnFormClosing(e);
    }

    bool _quitting;
    void Quit()
    {
        _quitting = true;
        GameAudio.UnmuteAll();
        StopControllerHelpers();
        Native.KillShellExplorer = false;
        Native.ReleaseShell();
        Native.BlockWinKey = false;
        Native.SuppressTaskbar = false;
        if (_isShell && !Native.ExplorerRunning()) StartExplorer();
        _tray.Visible = false;
        Native.UnregisterHotKey(Handle, HotkeySelector);
        Native.UnregisterHotKey(Handle, HotkeyGuide);
        Application.Exit();
    }

    // ================= Mode switching =================

    Task<List<Game>>? _scanTask;

    /// <summary>Starts scanning every library source on a worker thread, unless a scan is already running.</summary>
    public void PrefetchLibrary()
    {
        if (_scanTask == null) // a finished-but-unused scan is kept until it has been applied
            _scanTask = Task.Run(() => GameLibrary.Load(Settings.Load(), includeHidden: true));
    }

    void ApplyScanWhenReady()
    {
        var task = _scanTask;
        if (task == null) return;
        void Apply()
        {
            if (_scanTask == task) _scanTask = null;
            if (!task.IsCompletedSuccessfully) return;
            ReloadLibrary(announce: false, scanned: task.Result);
            MatchSteamArtInBackground(); // one-time Steam art lookup for non-Steam games
            DetectControllerSupport();   // one-time controller-support lookup per game
            if (_settings.StartSteamSilently && GameLibrary.SteamExe != null && File.Exists(GameLibrary.SteamExe)
                && Process.GetProcessesByName("steam").Length == 0)
                TryStart(GameLibrary.SteamExe, "-silent");
        }
        if (task.IsCompleted) Apply();
        else task.ContinueWith(_ => { if (IsHandleCreated) BeginInvoke(Apply); });
    }

    public void ShowSelector()
    {
        PrefetchLibrary(); // warm the library while the user is choosing
        _view = View.Selector;
        _selectorIndex = 0;
        _selectorAt = NowMs;
        _selectorExitAt = -1;
        Array.Clear(_selectorHover);
        BringUsToFront();
        RequestFrames();
        UpdateAmbient();
    }

    public void EnterConsole()
    {
        _mode = Mode.Console;
        _tray.Visible = false;
        Native.BlockWinKey = true;
        if (_isShell && Native.ExplorerRunning())
        {
            // We are the real shell, so Explorer was only started for desktop mode: remove it entirely.
            foreach (var p in Process.GetProcessesByName("explorer"))
                try { p.Kill(); } catch { }
        }
        Native.SuppressTaskbar = true; // hidden the instant Explorer shows it again
        if (_isShell)
        {
            Native.KillShellExplorer = true;
            Native.ClaimShell(); // so an Explorer that Windows starts can't become the shell
        }

        _scroll = _scrollTarget = 0;
        _homeEnterAt = NowMs; // home fades in from the selector's black
        if (!_introShown && !_preview)
        {
            _introShown = true;
            _introAt = NowMs;
            _homeEnterAt = _introAt + IntroMs - 50;
            Sounds.Play(Sfx.Boot);
        }
        _menuBackdropDirty = true;
        // The library scan runs on a worker (started while the selector was showing), so the switch
        // itself never stalls; the result is applied the moment it's ready.
        PrefetchLibrary();
        ApplyScanWhenReady();

        if (_active != null && _running.Contains(_active)) OpenMenu(Page.Guide);
        else { _view = View.Home; BringUsToFront(); }
        RequestFrames();
        UpdateAmbient();
    }

    public void EnterDesktop()
    {
        _mode = Mode.Desktop;
        Native.BlockWinKey = false;
        TopMost = false;
        Hide();
        Ambient.Set(false);
        GameAudio.UnmuteAll();
        StopControllerHelpers();
        Native.KillShellExplorer = false;
        Native.ReleaseShell(); // let Explorer become the desktop shell
        Native.SuppressTaskbar = false;
        if (!Native.ExplorerRunning()) StartExplorer();
        _tray.Visible = true;
    }

    static void StartExplorer() =>
        TryStart(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "");

    static void TryStart(string file, string args)
    {
        try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false }); }
        catch (Exception ex) { Log.Write($"Start {file} failed: {ex.Message}"); }
    }

    static void OpenSettingsFile()
    {
        Settings.Load(); // makes sure the file exists
        TryStart("notepad.exe", $"\"{Settings.FilePath}\"");
    }

    void BringUsToFront()
    {
        if (!Visible) Show();
        TopMost = true;
        Native.ForceForeground(Handle);
        Activate();
        Invalidate();
    }

    // ================= Games =================

    void LaunchOrResume(Game g)
    {
        RefreshRunning(force: true);
        _active = g;
        if (_running.Contains(g))
        {
            FocusGame();
            return;
        }
        _blockedUntil.Remove(g.Name); // launching it yourself lifts a cancel/close block
        if (g.Emulated)
        {
            // One emulator, many games: remember which of its games is the one running.
            _emuLaunched.RemoveWhere(n => _games.Any(o => o.Name == n && o.InstallDir == g.InstallDir));
            _emuLaunched.Add(g.Name);
        }
        _settings.LastPlayed[g.Name] = DateTime.Now;
        _settings.Save();
        Sounds.Play(Sfx.Launch);
        Ambient.Set(false); // the game's own audio takes over
        try { g.Launch(); }
        catch (Exception ex)
        {
            Toast($"Couldn't start {g.Name}: {ex.Message}");
            _active = null;
            return;
        }
        // Stay topmost while starting so launcher popups (Steam etc.) don't break immersion.
        _view = View.Launching;
        _launchStarted = Environment.TickCount64;
        _missingTicks = 0;
        RequestFrames();
        Invalidate();
    }

    void FocusGame()
    {
        if (_active == null) return;
        GameAudio.UnmuteAll(); // going back to the game, by any route: its sound returns
        var hwnd = GameProcesses.MainWindowFor(_active);
        if (hwnd == IntPtr.Zero)
        {
            _view = View.Launching;
            _launchStarted = Environment.TickCount64;
            RequestFrames();
            Invalidate();
            return;
        }
        _view = View.InGame;
        Ambient.Set(false);
        GameAudio.UnmuteAll(); // back in the game: its sound returns
        _missingTicks = _noWindowTicks = 0;
        TopMost = false;
        Invalidate();
        // Tuck the backdrop directly under the game first, so the game shows even if Windows
        // refuses the focus change; then hand it focus.
        Native.PlaceBehind(Handle, hwnd);
        Native.ForceForeground(hwnd);
        if (Native.GetForegroundWindow() != hwnd)
            Log.Write($"Focus: {_active.Name} window 0x{hwnd:X} didn't take focus (foreground 0x{Native.GetForegroundWindow():X})");
    }

    int _noWindowTicks;

    // ---- Cancelled/closed games stay closed ----
    // Launchers (EA app, Steam, ...) often start the game a few seconds after you asked, so closing
    // it once isn't enough: for a short while, any new instance of a cancelled game is closed again.

    readonly Dictionary<string, long> _blockedUntil = new();
    readonly HashSet<string> _closing = new();

    void BlockGame(Game g)
    {
        _blockedUntil[g.Name] = Environment.TickCount64 + 60_000;
        _ = GameProcesses.CloseAsync(g);
    }

    void EnforceBlocks()
    {
        long now = Environment.TickCount64;
        foreach (var name in _blockedUntil.Keys.ToList())
        {
            if (now > _blockedUntil[name]) { _blockedUntil.Remove(name); continue; }
            var g = _games.FirstOrDefault(x => x.Name == name);
            if (g == null || _closing.Contains(name) || GameProcesses.PidsFor(g).Count == 0) continue;
            _closing.Add(name);
            Log.Write($"Closing {name} again: it was cancelled but its launcher started it anyway");
            GameProcesses.CloseAsync(g).ContinueWith(_ => BeginInvoke(() => _closing.Remove(name)));
        }
    }

    async void CloseActiveGame()
    {
        var g = _active;
        if (g == null) return;
        _blockedUntil[g.Name] = Environment.TickCount64 + 60_000;
        _view = View.Home;
        Toast($"Closing {g.Name}…");
        BringUsToFront();
        await GameProcesses.CloseAsync(g);
        if (_active == g) _active = null;
        RefreshRunning(force: true);
        Toast($"{g.Name} closed");
        if (_mode == Mode.Console) BringUsToFront();
    }

    void RefreshRunning(bool force = false)
    {
        long now = Environment.TickCount64;
        // The full process scan is relatively costly, so poll slowly unless a game is starting/running.
        int interval = _view is View.Launching or View.InGame ? 500 : 1000;
        if (!force && now - _lastRefresh < interval) return;
        _lastRefresh = now;
        if (force)
        {
            GameProcesses.Refresh();
            _running = _games.Where(IsShowingNow).ToHashSet();
            return;
        }
        // Periodic scans run on a worker so the UI never stalls on them.
        if (_scanning) return;
        _scanning = true;
        Task.Run(GameProcesses.Refresh).ContinueWith(_ =>
        {
            if (!IsHandleCreated) return;
            BeginInvoke(() =>
            {
                _scanning = false;
                var now2 = _games.Where(IsShowingNow).ToHashSet();
                if (!now2.SetEquals(_running)) { _running = now2; Invalidate(); }
            });
        });
    }

    bool _scanning;
    readonly HashSet<string> _emuLaunched = new();

    /// <summary>Running with a window; for emulated games, only the one launched through the console.</summary>
    bool IsShowingNow(Game g) => GameProcesses.IsShowing(g) && (!g.Emulated || _emuLaunched.Contains(g.Name));
    long _lastExplorerCheck;
    int _lastMinute = -1;

    /// <summary>Ambience plays on the selector and the console home/menus, never over a running game.</summary>
    void UpdateAmbient() =>
        Ambient.Set(_settings.AmbientMusic && !_preview && Visible &&
            (_view == View.Selector || (_mode == Mode.Console && ((_view == View.Home && !IntroPlaying) || (_view == View.Menu && _rootPage != Page.Guide)))),
            _view == View.Selector ? Ambient.Track.Selector : Ambient.Track.Home);

    void StatusTick()
    {
        UpdateAmbient();
        if (_toast != null && Environment.TickCount64 > _toastUntil) Invalidate();
        if (_preview) return; // off-screen diagnostic render: never touch the real desktop
        if (_mode == Mode.Desktop)
        {
            // Desktop mode must always have a taskbar, even if something (a crash, an older
            // instance) left it hidden.
            Native.SetTaskbarVisible(true);
            return;
        }
        if (_mode != Mode.Console) return;
        Native.SetTaskbarVisible(false); // backup for the instant hook
        if (_isShell) Native.ClaimShell(); // retries until Explorer's old registration is gone
        if (_isShell && Environment.TickCount64 - _lastExplorerCheck > 2000)
        {
            // As the real shell, console mode runs without Explorer; if something restarted it, end it again.
            _lastExplorerCheck = Environment.TickCount64;
            foreach (var p in Process.GetProcessesByName("explorer"))
                try { p.Kill(); } catch { }
        }

        int wasRunning = _running.Count;
        EnforceBlocks();
        if (_view == View.InGame) GameAudio.UnmuteAll(); // safety net: never leave a game we're playing muted
        UpdateControllerHelpers();
        UpdatePresence();
        RefreshRunning();
        long elapsed = Environment.TickCount64 - _launchStarted;

        switch (_view)
        {
            case View.Launching when _active != null:
                var hwnd = GameProcesses.MainWindowFor(_active);
                if (hwnd != IntPtr.Zero) { FocusGame(); break; }
                bool alive = GameProcesses.IsRunning(_active);
                if (!alive && elapsed > 90_000)
                {
                    Toast($"{_active.Name} didn't start");
                    _active = null;
                    _view = View.Home;
                   
                    BringUsToFront();
                }
                break;

            case View.InGame when _active != null:
                if (!GameProcesses.IsRunning(_active))
                {
                    // Cover the screen right away so nothing behind the game shows, then give
                    // launchers that restart themselves a moment before deciding the game quit.
                    if (_missingTicks++ == 0) BringUsToFront();
                    if (_missingTicks >= 4)
                    {
                        _active = null;
                        _view = View.Home;
                        BringUsToFront();
                    }
                    break;
                }
                _missingTicks = 0;
                var w = GameProcesses.MainWindowFor(_active);
                if (w == IntPtr.Zero)
                {
                    // Game is between windows (splash -> main, display mode change). Taking focus now
                    // would make fullscreen games minimize, so only cover after it stays gone for 3 s.
                    if (++_noWindowTicks >= 6)
                    {
                        _view = View.Launching;
                        _launchStarted = Environment.TickCount64;
                        RequestFrames();
                        BringUsToFront();
                    }
                    break;
                }
                _noWindowTicks = 0;
                if (Native.GetForegroundWindow() == Handle || TopMost)
                    FocusGame(); // the backdrop got in front of the game: hand focus back
                break;
        }

        bool minuteChanged = DateTime.Now.Minute != _lastMinute;
        _lastMinute = DateTime.Now.Minute;
        if (wasRunning != _running.Count || (minuteChanged && _view is View.Home or View.Menu)
            || (_view == View.Menu && _page == Page.Keyboard)) Invalidate();
    }

    void Toast(string msg)
    {
        if (_toast == null || Environment.TickCount64 > _toastUntil) _toastAt = NowMs;
        _toast = msg;
        _toastUntil = Environment.TickCount64 + 3500;
        RequestFrames();
    }

    // ================= Input =================

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_view == View.Menu && _page == Page.Keyboard && PhysicalKeyboardKey(e)) { e.Handled = true; return; }
        Btn? b = e.KeyCode switch
        {
            Keys.Up => Btn.Up, Keys.Down => Btn.Down, Keys.Left => Btn.Left, Keys.Right => Btn.Right,
            Keys.Enter or Keys.Space => Btn.A, Keys.Escape or Keys.Back => Btn.B,
            Keys.X => Btn.X, Keys.Y => Btn.Y, Keys.Apps or Keys.F1 => Btn.Start,
            _ => null,
        };
        if (b != null) { OnButton(b.Value); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Right) { OnButton(Btn.B); return; }
        foreach (var hit in _hits.ToArray())
            if (hit.rect.Contains(e.Location)) { Safe(hit.click); return; }
    }

    Point _lastMouse;
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Math.Abs(e.X - _lastMouse.X) + Math.Abs(e.Y - _lastMouse.Y) < 4) return;
        _lastMouse = e.Location;
        SetCursorHidden(false);
        foreach (var hit in _hits.ToArray())
            if (hit.rect.Contains(e.Location) && hit.hover != null) { hit.hover(); Invalidate(); return; }
    }

    bool _cursorHidden;
    void SetCursorHidden(bool hide)
    {
        if (hide == _cursorHidden) return;
        _cursorHidden = hide;
        if (hide) Cursor.Hide(); else Cursor.Show();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_view == View.Home) OnButton(e.Delta > 0 ? Btn.Left : Btn.Right);
        else if (_view == View.Menu) OnButton(e.Delta > 0 ? Btn.Up : Btn.Down);
    }

    void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { Log.Write(ex.ToString()); Toast("Error: " + ex.Message); }
    }

    void OnButton(Btn b) => Safe(() =>
    {
        if (_pointerActive && b != Btn.Guide) return;
        if (_view == View.Home && IntroPlaying)
        {
            // Any button skips the boot intro.
            _introAt = -1e9;
            _homeEnterAt = NowMs;
            RequestFrames();
            return;
        }
        if (Visible && (_view != View.InGame || b == Btn.Guide) && _mode == Mode.Console || _view == View.Selector)
            Sounds.Play(b switch { Btn.A or Btn.Start => Sfx.Select, Btn.B => Sfx.Back, Btn.Guide => Sfx.Open, _ => Sfx.Move });
        if (_view != View.InGame && Visible) SetCursorHidden(true);
        HandleButton(b);
    });

    void HandleButton(Btn b)
    {
        if (!Visible && b != Btn.Guide) return;

        if (b == Btn.Guide)
        {
            if (_mode != Mode.Console || _view == View.Selector) return;
            RefreshRunning(force: true);
            // The process decides, not the window: fullscreen games often hide theirs while the menu covers them.
            bool gameUp = _active != null && GameProcesses.IsRunning(_active);
            switch (_view)
            {
                case View.Menu when gameUp: FocusGame(); break;
                case View.InGame:
                case View.Launching:
                    if (_active != null)
                    {
                        GameAudio.Mute(GameProcesses.PidsFor(_active)); // silence the game while its menu is up
                        OpenMenu(Page.Guide);
                    }
                    break;
                default:
                    // Home / any menu: Guide jumps straight back into the open game, like a console.
                    if (!gameUp && _running.Count > 0) { _active = _running.First(); gameUp = true; }
                    if (gameUp) FocusGame();
                    else { _view = View.Home; BringUsToFront(); }
                    break;
            }
            return;
        }

        // While a game has focus, controller input belongs to the game.
        if (_view == View.InGame)
        {
            // Our backdrop only receives input if it somehow ended up focused: A takes you back to the game.
            if (b == Btn.A && Native.GetForegroundWindow() == Handle) FocusGame();
            return;
        }

        switch (_view)
        {
            case View.Selector:
                if (_selectorExitAt >= 0) return; // already switching
                if (b is Btn.Left or Btn.Up) _selectorIndex = 0;
                else if (b is Btn.Right or Btn.Down) _selectorIndex = 1;
                else if (b is Btn.A or Btn.Start) { StartSelectorExit(_selectorIndex); return; }
                else if (b == Btn.Y) CycleSelectorTheme();
                RequestFrames();
                break;

            case View.Home:
                HomeInput(b);
                RequestFrames();
                break;

            case View.Launching:
                if (b == Btn.B) { if (_active != null) BlockGame(_active); _active = null; _view = View.Home; BringUsToFront(); }
                else if (b == Btn.X) RevealLauncherWindow();
                break;

            case View.Menu when _page == Page.Keyboard:
                KeyboardInput(b);
                break;

            case View.Menu:
                var items = PageItems();
                int n = items.Count;
                if (n == 0) { if (b == Btn.B) PopPage(); break; }
                if (b == Btn.Up) _menuIndex = (_menuIndex + n - 1) % n;
                else if (b == Btn.Down) _menuIndex = (_menuIndex + 1) % n;
                else if (b == Btn.LB) _menuIndex = Math.Max(0, _menuIndex - 8);
                else if (b == Btn.RB) _menuIndex = Math.Min(n - 1, _menuIndex + 8);
                else if (b == Btn.A) { items[Math.Min(_menuIndex, n - 1)].Action(); return; }
                else if (b == Btn.X) items[Math.Min(_menuIndex, n - 1)].Alt?.Invoke();
                else if (b == Btn.B) PopPage();
                else if (b == Btn.Start && _rootPage == Page.Settings) CloseMenu();
                RequestFrames();
                break;
        }
        Invalidate();
    }

    /// <summary>Drops our always-on-top cover and fronts whatever launcher/sign-in window is waiting.</summary>
    void RevealLauncherWindow()
    {
        TopMost = false;
        var w = Native.TopOtherWindow(Handle);
        if (w != IntPtr.Zero) Native.ForceForeground(w);
        else Toast("No launcher window found");
    }

    /// <summary>Rescans every enabled source; reports how many games are new since the last scan.</summary>
    void ReloadLibrary(bool announce, bool manual = false, List<Game>? scanned = null)
    {
        _settings = Settings.Load();
        Sounds.Enabled = _settings.UiSounds;
        if (manual && _settings.Removed.Count > 0)
        {
            // "Remove" only lasts until you scan again; "Exclude from scans" is the permanent one.
            _settings.Removed.Clear();
            _settings.Save();
        }
        var all = !manual && scanned != null ? scanned : GameLibrary.Load(_settings, includeHidden: true);
        _games = all.Where(g => !g.Hidden && !g.Removed).ToList();
        var known = new HashSet<string>(_settings.KnownGames, StringComparer.OrdinalIgnoreCase);
        var fresh = all.Where(g => !known.Contains(g.Name)).ToList();
        bool firstScan = known.Count == 0;
        if (fresh.Count > 0)
        {
            _settings.KnownGames = all.Select(g => g.Name).ToList();
            _settings.Save();
        }
        _sel = Math.Clamp(_sel, 0, Math.Max(0, _games.Count - 1));
        // Map the last process snapshot onto the new game objects now; a fresh scan follows on a worker.
        _running = _games.Where(IsShowingNow).ToHashSet();
        _lastRefresh = 0;
        RefreshRunning();
        _menuBackdropDirty = true;
        if (fresh.Count > 0 && !firstScan)
            Toast(fresh.Count == 1 ? $"Added {fresh[0].Name}" : $"Found {fresh.Count} new games");
        else if (announce)
            Toast($"Scan complete: {_games.Count} games");
        Invalidate();
    }

    // ---- Menu pages (guide, power, settings and its sub-pages) ----

    enum Page { Guide, Power, Settings, Sources, AddApp, Manage, GameOptions, ArtResults, Keyboard, Friend }
    sealed record MenuItem(string Label, Action Action, string? Value = null, Action? Alt = null);

    Page _page, _rootPage;
    readonly Stack<(Page page, int index)> _pageStack = new();
    List<Scrapers.InstalledApp> _apps = new();
    List<Game> _manageList = new();

    void OpenMenu(Page p)
    {
        _pageStack.Clear();
        _page = _rootPage = p;
        _menuIndex = 0;
        _menuIndexAnim = 0;
        _view = View.Menu;
        _menuOpenAt = NowMs;
        _menuBackdropDirty = true;
        LoadPage(p);
        BringUsToFront();
        RequestFrames();
    }

    void PushPage(Page p)
    {
        _pageStack.Push((_page, _menuIndex));
        _page = p;
        _menuIndex = 0;
        _menuIndexAnim = 0;
        _pageAt = NowMs;
        LoadPage(p);
        RequestFrames();
    }

    void PopPage()
    {
        if (_pageStack.Count == 0) { CloseMenu(); return; }
        (_page, _menuIndex) = _pageStack.Pop();
        _menuIndexAnim = _menuIndex;
        _pageAt = NowMs;
        RequestFrames();
    }

    void CloseMenu()
    {
        if (_rootPage == Page.Guide && _active != null && GameProcesses.IsRunning(_active)) FocusGame();
        else { _view = View.Home; RequestFrames(); }
    }

    void LoadPage(Page p)
    {
        if (p == Page.AddApp)
        {
            Toast("Looking for installed apps…");
            Update();
            _apps = Scrapers.StartMenuApps();
            _appFilter = "";
        }
        else if (p == Page.Manage)
            _manageList = GameLibrary.Load(Settings.Load(), includeHidden: true);
    }

    static string OnOff(bool on) => on ? "On" : "Off";

    List<MenuItem> PageItems()
    {
        switch (_page)
        {
            case Page.Guide when _active != null:
                return new()
                {
                    new($"Resume {_active.Name}", FocusGame),
                    new($"Close {_active.Name}", CloseActiveGame),
                    new("Mouse mode", () => { ToggleGameMouse(); FocusGame(); },
                        $"{OnOff(GameMouseOn)}  ·  {ButtonLabel("VIEW")} + {ButtonLabel("≡")}"),
                    new("Home", () => { _view = View.Home; Invalidate(); }),
                    new("Open Discord", OpenDiscord),
                    new("Settings", () => PushPage(Page.Settings)),
                    new("Power & modes", () => PushPage(Page.Power)),
                };

            case Page.Settings:
                int enabled = GameLibrary.Sources.Count(s => _settings.SourceOn(s.key));
                return new()
                {
                    new("Scan for games now", () => ReloadLibrary(announce: true, manual: true)),
                    new("Get missing artwork", FillMissingArtwork, $"{_games.Count(g => g.IsIcon)} missing"),
                    new("Library sources", () => PushPage(Page.Sources), $"{enabled} on"),
                    new("Add an app", () => PushPage(Page.AddApp)),
                    new("Manage library", () => PushPage(Page.Manage), $"{_games.Count} shown"),
                    new("Selector theme", CycleSelectorTheme, CurrentSelTheme.Name),
                    new("UI sounds", () =>
                    {
                        _settings.UiSounds = !_settings.UiSounds;
                        _settings.Save();
                        Sounds.Enabled = _settings.UiSounds;
                    }, OnOff(_settings.UiSounds)),
                    new("Ambient music", () =>
                    {
                        _settings.AmbientMusic = !_settings.AmbientMusic;
                        _settings.Save();
                        UpdateAmbient();
                    }, OnOff(_settings.AmbientMusic)),
                    new("Power & modes", () => PushPage(Page.Power)),
                    new("Back", PopPage),
                };

            case Page.Sources:
                return GameLibrary.Sources.Select(s => new MenuItem(s.name, () =>
                {
                    _settings.Sources[s.key] = !_settings.SourceOn(s.key);
                    _settings.Save();
                    ReloadLibrary(announce: false);
                }, OnOff(_settings.SourceOn(s.key)))).ToList();

            case Page.AddApp:
                var inLibrary = new HashSet<string>(_settings.CustomGames.Select(c => c.Path), StringComparer.OrdinalIgnoreCase);
                var search = new List<MenuItem>
                {
                    new(_appFilter == "" ? "Search apps…" : $"Search: {_appFilter}", OpenAppSearch,
                        _appFilter == "" ? $"{_apps.Count} apps" : null),
                };
                if (_appFilter != "") search.Add(new("Clear search", () => { _appFilter = ""; _menuIndex = 0; RequestFrames(); }));
                // Every word typed must appear in the name, in any order ("steam big" finds "Steam Big Picture").
                var words = _appFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var shown = _apps.Where(a => words.All(w => a.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
                if (_appFilter != "" && shown.Count == 0) search.Add(new($"No apps match \"{_appFilter}\"", OpenAppSearch));
                return search.Concat(shown.Select(a => new MenuItem(a.Name, () =>
                {
                    if (inLibrary.Contains(a.Target)) { Toast($"{a.Name} is already in your library"); return; }
                    _settings.CustomGames.Add(new CustomGame
                    {
                        Name = a.Name, Path = a.Target, Args = a.Args, WorkingDir = a.WorkingDir, IsApp = true,
                    });
                    _settings.Hidden.RemoveAll(h => h.Equals(a.Name, StringComparison.OrdinalIgnoreCase));
                    _settings.Save();
                    ReloadLibrary(announce: false);
                    Toast($"Added {a.Name}");
                }, inLibrary.Contains(a.Target) ? "In library" : null))).ToList();

            case Page.Manage:
                return _manageList.Select(g =>
                {
                    var custom = _settings.CustomGames.FirstOrDefault(c => c.Name.Equals(g.Name, StringComparison.OrdinalIgnoreCase));
                    Action? remove = custom == null ? null : () =>
                    {
                        _settings.CustomGames.Remove(custom);
                        _settings.Save();
                        _manageList.Remove(g);
                        ReloadLibrary(announce: false);
                        Toast($"Removed {g.Name}");
                    };
                    return new MenuItem(g.Name, () =>
                    {
                        // Excluded/removed games come back; shown ones get excluded.
                        bool restore = g.Hidden || g.Removed;
                        _settings.Hidden.RemoveAll(h => h.Equals(g.Name, StringComparison.OrdinalIgnoreCase));
                        _settings.Removed.RemoveAll(h => h.Equals(g.Name, StringComparison.OrdinalIgnoreCase));
                        if (!restore) _settings.Hidden.Add(g.Name);
                        g.Hidden = !restore;
                        g.Removed = false;
                        _settings.Save();
                        ReloadLibrary(announce: false);
                    }, g.Hidden ? "Excluded" : g.Removed ? "Removed" : "Shown", remove);
                }).ToList();

            case Page.GameOptions when _optGame != null: return GameOptionItems();
            case Page.ArtResults: return ArtResultItems();
            case Page.Keyboard: return new();
            case Page.Friend: return FriendItems();

            default: // Power (also the Guide page if its game vanished)
                return new()
                {
                    new("Switch to Desktop", () => { Sounds.Play(Sfx.Bye); EnterDesktop(); }),
                    new("Mode selector", ShowSelector),
                    new("Restart PC", () => TryStart("shutdown.exe", "/r /t 0")),
                    new("Shut down", () => TryStart("shutdown.exe", "/s /t 0")),
                    new("Back", PopPage),
                };
        }
    }

    string PageTitle() => _page switch
    {
        Page.Settings => "Settings",
        Page.Sources => "Library sources",
        Page.AddApp => "Add an app",
        Page.Manage => "Manage library",
        Page.GameOptions => _optGame?.Name ?? "",
        Page.ArtResults => "Find artwork",
        Page.Friend => _friendTarget?.Name ?? "Friend",
        Page.Keyboard => _kbForApps ? "Search apps" : "Search artwork",
        _ => "Power & modes",
    };

    // ================= Painting =================

    float U => ClientSize.Height / 1080f;
    readonly Dictionary<(float, FontStyle, string), Font> _fonts = new();

    Font F(float px, FontStyle style = FontStyle.Regular, string family = "Segoe UI")
    {
        float size = Math.Max(6, px * U);
        if (!_fonts.TryGetValue((size, style, family), out var f))
            _fonts[(size, style, family)] = f = new Font(family, size, style, GraphicsUnit.Pixel);
        return f;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.Bilinear;
        _hits.Clear();
        _tileBakesThisFrame = 0;

        if (_view == View.InGame) { PaintInGame(g); return; }

        // Home and its menus paint their own pre-composed background.
        if (_view is not (View.Home or View.Menu))
            using (var bg = new LinearGradientBrush(ClientRectangle, BgTop, BgBottom, 90f))
                g.FillRectangle(bg, ClientRectangle);

        switch (_view)
        {
            case View.Selector: PaintSelector(g); break;
            case View.Home: PaintHome(g); break;
            case View.Launching: PaintLaunching(g); break;
            case View.Menu:
                PaintMenuBackdrop(g);
                PaintMenu(g); break;
        }
        PaintToast(g);
    }

    void PaintLaunching(Graphics g)
    {
        float u = U, W = ClientSize.Width, H = ClientSize.Height;
        if (_active == null) return;
        float tw = 300 * u, th = 450 * u;
        DrawTile(g, _active, new RectangleF((W - tw) / 2, H * 0.16f, tw, th), false);

        float cy = H * 0.16f + th + 80 * u;
        float angle = (float)(NowMs % 1200 / 1200 * 360);
        using (var pen = new Pen(Accent, 6 * u) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(pen, W / 2 - 24 * u, cy - 24 * u, 48 * u, 48 * u, angle, 270);
        DrawCentered(g, $"Starting {_active.Name}", F(36, FontStyle.Bold), Color.White, W / 2, cy + 50 * u);
        long secs = (Environment.TickCount64 - _launchStarted) / 1000;
        if (secs > 20)
            DrawCentered(g, $"Taking a while? A launcher or sign-in window may be waiting (press {ButtonLabel("X")} to show it).",
                F(22), Dim, W / 2, cy + 105 * u);
        PaintHints(g, new[] { ("B", "Cancel"), ("X", "Show launcher windows"), ("GUIDE", "Menu") }, null);
    }

    void PaintInGame(Graphics g)
    {
        g.Clear(Color.Black);
        if (_active == null) return;
        DrawCentered(g, $"{_active.Name} is running", F(30, FontStyle.Bold), Color.FromArgb(90, 255, 255, 255),
            ClientSize.Width / 2f, ClientSize.Height / 2f - 30 * U);
        DrawCentered(g, $"Press {ButtonLabel("A")} to return to the game, or the {GuideName} for the menu", F(22), Color.FromArgb(60, 255, 255, 255),
            ClientSize.Width / 2f, ClientSize.Height / 2f + 20 * U);
    }

    int _menuScroll;

    void PaintMenu(Graphics g)
    {
        float u = U, H = ClientSize.Height;
        bool guide = _page == Page.Guide && _active != null;
        var headerGame = guide ? _active : _page == Page.GameOptions ? _optGame : null;
        float panelW = (guide || _page == Page.Power ? 620 : _page == Page.Keyboard ? 1100 : 900) * u;
        double now = NowMs;
        float open = EaseOutCubic(Progress(_menuOpenAt, 280, now));
        float pageIn = EaseOutCubic(Progress(_pageAt, 240, now));
        // Frosted-glass panel, anchored to the screen so the blur lines up with what's behind it.
        float vis = open * panelW;
        if (_menuGlass != null && vis > 1)
        {
            int vw = (int)Math.Min(vis, _menuGlass.Width);
            g.DrawImage(_menuGlass, new Rectangle(0, 0, vw, (int)H), new Rectangle(0, 0, vw, (int)H), GraphicsUnit.Pixel);
        }
        else using (var pb = new SolidBrush(Panel)) g.FillRectangle(pb, 0, 0, vis, H);
        using (var drop = new LinearGradientBrush(new RectangleF(vis, 0, 48 * u, H), Color.FromArgb(110, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 0f))
            g.FillRectangle(drop, vis, 0, 48 * u, H);
        using (var hairline = new SolidBrush(Color.FromArgb(70, 255, 255, 255))) g.FillRectangle(hairline, vis - 1.5f * u, 0, 1.5f * u, H);
        var outer = g.Save();
        g.TranslateTransform(-(1 - open) * panelW, 0);

        float x = 60 * u, y = 70 * u;
        if (headerGame != null)
        {
            var art = new RectangleF(x, y, 140 * u, 210 * u);
            DrawTile(g, headerGame, art, false);
            g.DrawString(headerGame.Name, F(32, FontStyle.Bold), Brushes.White,
                new RectangleF(x + 170 * u, y + 20 * u, panelW - x - 230 * u, 120 * u));
            using (var gb = new SolidBrush(_running.Contains(headerGame) ? Green : Dim))
                g.DrawString(_running.Contains(headerGame) ? "Running" : "Not running", F(22), gb, x + 170 * u, y + 150 * u);
            y += 270 * u;
        }
        else
        {
            g.DrawString(PageTitle(), F(40, FontStyle.Bold), Brushes.White, x, y);
            y += 110 * u;
        }

        if (_page == Page.Keyboard) { PaintKeyboard(g, x, y, panelW); g.Restore(outer); return; }
        if (_page == Page.ArtResults) PaintArtPreview(g, panelW);
        g.TranslateTransform((1 - pageIn) * 28 * u, 0);

        var items = PageItems();
        if (items.Count == 0)
        {
            using var db = new SolidBrush(Dim);
            g.DrawString(_page == Page.AddApp ? "No installed apps found" : "Nothing here yet", F(26), db, x, y);
        }
        _menuIndex = Math.Clamp(_menuIndex, 0, Math.Max(0, items.Count - 1));

        // Scroll long lists (installed apps, library) so the selected row stays visible.
        float step = 90 * u;
        int visible = Math.Max(1, (int)((H - y - 130 * u) / step));
        if (_menuIndex < _menuScroll) _menuScroll = _menuIndex;
        else if (_menuIndex >= _menuScroll + visible) _menuScroll = _menuIndex - visible + 1;
        _menuScroll = Math.Clamp(_menuScroll, 0, Math.Max(0, items.Count - visible));

        float rowW = panelW - 100 * u;
        // The highlight glides between rows instead of jumping.
        float hiIndex = Math.Clamp(_menuIndexAnim, _menuScroll, Math.Max(_menuScroll, Math.Min(items.Count, _menuScroll + visible) - 1));
        if (items.Count > 0)
        {
            var hr = new RectangleF(x - 20 * u, y + (hiIndex - _menuScroll) * step, rowW, 76 * u);
            using var p = Rounded(hr, 14 * u);
            using var sb = new SolidBrush(Color.FromArgb(34, 255, 255, 255));
            g.FillPath(sb, p);
            using var pen = new Pen(Color.FromArgb(225, 255, 255, 255), 2.5f * u);
            g.DrawPath(pen, p);
        }
        var itemColor = WithAlpha(Color.White, pageIn);
        using var itemBrush = new SolidBrush(itemColor);
        for (int i = _menuScroll; i < Math.Min(items.Count, _menuScroll + visible); i++)
        {
            var item = items[i];
            var r = new RectangleF(x - 20 * u, y, rowW, 76 * u);
            bool on = i == _menuIndex;
            float valueW = 0;
            if (item.Value != null)
            {
                var vf = F(24, FontStyle.Bold);
                valueW = g.MeasureString(item.Value, vf).Width;
                bool positive = item.Value is "On" or "Shown" or "In library";
                using var vb = new SolidBrush(positive ? Green : Dim);
                g.DrawString(item.Value, vf, vb, r.Right - 24 * u - valueW, r.Y + (r.Height - vf.Height) / 2);
            }
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(item.Label, F(28, on ? FontStyle.Bold : FontStyle.Regular),
                    itemBrush, new RectangleF(r.X + 24 * u, r.Y, r.Width - 60 * u - valueW, r.Height), sf);
            int idx = i;
            // Hit boxes in screen space (the panel is translated while it slides in).
            var screenR = new RectangleF(r.X - (1 - open) * panelW + (1 - pageIn) * 28 * u, r.Y, r.Width, r.Height);
            _hits.Add((screenR, () => Safe(item.Action), () => { _menuIndex = idx; RequestFrames(); }));
            y += step;
        }
        if (_menuScroll + visible < items.Count)
        {
            using var mb = new SolidBrush(Dim);
            g.DrawString($"▼ {items.Count - _menuScroll - visible} more", F(20), mb, x, y);
        }

        g.Restore(outer);
        var hints = new List<(string, string)> { ("A", "Select"), ("B", guide && _pageStack.Count == 0 ? "Resume" : "Back") };
        if (_page == Page.Manage) { hints[0] = ("A", "Show / hide"); hints.Add(("X", "Remove added app")); }
        if (items.Count > visible) hints.Add(("LB RB", "Page"));
        PaintHints(g, hints.ToArray(), null);
    }

    void PaintHints(Graphics g, (string glyph, string text)[] hints, string? right)
    {
        float u = U, W = ClientSize.Width, H = ClientSize.Height;
        float y = H - 70 * u, x = 80 * u;
        bool ps = _pad.Kind == PadKind.PlayStation;
        foreach (var (rawGlyph, text) in hints)
        {
            float d = 40 * u;
            if (ps && rawGlyph is "A" or "B" or "X" or "Y")
            {
                // PlayStation face buttons: draw the shape itself (Segoe UI has no reliable glyphs for them).
                var circle = new RectangleF(x, y, d, d);
                using (var bg = new SolidBrush(Color.FromArgb(30, 41, 59))) g.FillEllipse(bg, circle);
                DrawPsShape(g, rawGlyph, circle);
                x += d + 10 * u;
                g.DrawString(text, F(22), Brushes.White, new RectangleF(x, y, 2000, d), HintLabelFormat);
                x += g.MeasureString(text, F(22)).Width + 34 * u;
                continue;
            }
            string glyph = ButtonLabel(rawGlyph);
            Color c = rawGlyph switch
            {
                "A" => Green, "B" => Color.FromArgb(239, 68, 68), "X" => Accent, "Y" => Color.FromArgb(234, 179, 8),
                _ => Color.FromArgb(100, 116, 139),
            };
            bool arrows = rawGlyph == "◄►";
            float gw = arrows ? d * 1.5f : Math.Max(d, g.MeasureString(glyph, F(20, FontStyle.Bold)).Width + 12 * u);
            var pill = new RectangleF(x, y, gw, d);
            using (var p = Rounded(pill, d / 2))
            using (var b = new SolidBrush(c)) g.FillPath(b, p);
            if (arrows) Icons.LeftRight(g, RectangleF.Inflate(pill, -5 * u, -5 * u), Color.White);
            else Icons.TextCentered(g, glyph, F(glyph.Length > 1 ? 17 : 20, FontStyle.Bold), Color.White,
                RectangleF.Inflate(pill, glyph.Length > 1 ? -10 * u : 0, 0));
            x += gw + 10 * u;
            g.DrawString(text, F(22), Brushes.White, new RectangleF(x, y, 2000, d), HintLabelFormat);
            x += g.MeasureString(text, F(22)).Width + 34 * u;
        }
        if (right != null)
        {
            var s = g.MeasureString(right, F(20));
            using var b = new SolidBrush(Dim);
            g.DrawString(right, F(20), b, W - 80 * u - s.Width, y + 7 * u);
        }
    }

    static readonly StringFormat HintLabelFormat = new() { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };

    /// <summary>Hint label for a button in the connected controller's own naming.</summary>
    string ButtonLabel(string xboxName)
    {
        if (_pad.Kind != PadKind.PlayStation) return xboxName;
        bool dualSense = _pad.Name.StartsWith("DualSense");
        return xboxName switch
        {
            "A" => "Cross", "B" => "Circle", "X" => "Square", "Y" => "Triangle",
            "≡" => "OPTIONS", "VIEW" => dualSense ? "CREATE" : "SHARE", "GUIDE" => "PS", "LB RB" => "L1 R1",
            _ => xboxName,
        };
    }

    /// <summary>"Guide button" / "PS button" etc. for sentences.</summary>
    string GuideName => _pad.Kind == PadKind.PlayStation ? "PS button" : "Guide button";

    void DrawPsShape(Graphics g, string xboxName, RectangleF c)
    {
        float u = U, cx = c.X + c.Width / 2, cy = c.Y + c.Height / 2, r = c.Width * 0.24f;
        (Color color, int kind) = xboxName switch
        {
            "A" => (Color.FromArgb(124, 178, 232), 0), // cross
            "B" => (Color.FromArgb(232, 106, 106), 1), // circle
            "X" => (Color.FromArgb(214, 140, 200), 2), // square
            _ => (Color.FromArgb(64, 196, 160), 3),    // triangle
        };
        using var pen = new Pen(color, 3.5f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (kind)
        {
            case 0:
                g.DrawLine(pen, cx - r, cy - r, cx + r, cy + r);
                g.DrawLine(pen, cx - r, cy + r, cx + r, cy - r);
                break;
            case 1: g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2); break;
            case 2: g.DrawRectangle(pen, cx - r * 0.9f, cy - r * 0.9f, r * 1.8f, r * 1.8f); break;
            default:
                g.DrawPolygon(pen, new[] { new PointF(cx, cy - r * 1.05f), new PointF(cx + r * 1.1f, cy + r * 0.8f), new PointF(cx - r * 1.1f, cy + r * 0.8f) });
                break;
        }
    }

    // ---- drawing helpers ----

    static void DrawCentered(Graphics g, string s, Font f, Color c, float cx, float y)
    {
        var size = g.MeasureString(s, f);
        using var b = new SolidBrush(c);
        g.DrawString(s, f, b, cx - size.Width / 2, y);
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Placeholder card hue from the name; string.GetHashCode is randomized per run, so it can't be used.</summary>
    static int StableHue(string name)
    {
        int h = 17;
        foreach (char c in name) h = unchecked(h * 31 + c);
        return (int)((uint)h % 360);
    }

    static Color FromHue(int hue, float light)
    {
        double h = hue / 60.0, c = (1 - Math.Abs(2 * light - 1)) * 0.7, x = c * (1 - Math.Abs(h % 2 - 1)), m = light - c / 2;
        (double r, double g, double b) = h switch
        {
            < 1 => (c, x, 0.0), < 2 => (x, c, 0.0), < 3 => (0.0, c, x),
            < 4 => (0.0, x, c), < 5 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }

    void DrawGamepadIcon(Graphics g, RectangleF box, Color c) => Icons.Gamepad(g, box, c, 1.4f);

    void DrawMonitorIcon(Graphics g, RectangleF box, Color c) => Icons.Monitor(g, box, c, 1.4f);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            foreach (var f in _fonts.Values) f.Dispose();
        }
        base.Dispose(disposing);
    }
}
