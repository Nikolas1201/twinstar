namespace Twinstar;

/// <summary>
/// Discord in console mode: its windows (incoming calls, the app itself) are allowed above the
/// console and driven with the controller as a mouse; Rich Presence shows what you're doing.
/// </summary>
sealed partial class MainForm
{
    readonly PointerMapper _pointer = new();
    bool _pointerActive;
    IntPtr _fgChecked;
    bool _fgIsOverlayApp;
    readonly DateTimeOffset _consoleSince = DateTimeOffset.Now;

    static bool IsOverlayApp(string? process) => process != null &&
        (process.StartsWith("Discord", StringComparison.OrdinalIgnoreCase)
         || process.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase)
         || process.Equals("steam", StringComparison.OrdinalIgnoreCase));

    /// <summary>Discord/Steam window in front of the console UI: the controller works as a mouse for it.</summary>
    void UpdatePointer(float dt)
    {
        var fg = Native.GetForegroundWindow();
        if (fg != _fgChecked) { _fgChecked = fg; _fgIsOverlayApp = fg != Handle && IsOverlayApp(Native.ProcessNameOf(fg)); }
        // Share + Options together, in a game: mouse mode on/off (for menus without controller
        // support, like Roblox's server list). The game keeps reading the pad as well.
        bool chord = _pad.State.Has(Gamepad.BackMask) && _pad.State.Has(Gamepad.StartMask);
        if (chord && !_chordHeld && _mode == Mode.Console && _view == View.InGame && _active != null) ToggleGameMouse();
        _chordHeld = chord;
        bool gameMouse = _view == View.InGame && _gameMouseFor != null && _gameMouseFor == _active && ForegroundIsGame();
        bool active = _mode == Mode.Console && (gameMouse || (_view != View.InGame && _fgIsOverlayApp));
        if (active != _pointerActive && !active) _pointer.Release();
        _pointerActive = active;
        _pointer.Tick(_pad.State, active, dt);
    }

    Game? _gameMouseFor;
    bool _chordHeld;

    bool GameMouseOn => _gameMouseFor != null && _gameMouseFor == _active;

    void ToggleGameMouse()
    {
        _gameMouseFor = GameMouseOn ? null : _active;
        Sounds.Play(GameMouseOn ? Sfx.Open : Sfx.Back);
    }

    /// <summary>A Discord window appeared (incoming call, notification, the app): let it above the console.</summary>
    void OnWindowShown(IntPtr hwnd)
    {
        if (_mode != Mode.Console || !Visible || !TopMost || _view == View.InGame) return;
        var name = Native.ProcessNameOf(hwnd);
        if (name == null || !name.StartsWith("Discord", StringComparison.OrdinalIgnoreCase)) return;
        if (Native.WindowTitle(hwnd).Contains("Updater", StringComparison.OrdinalIgnoreCase)) return; // startup splash
        TopMost = false;
        Native.ForceForeground(hwnd);
        Toast($"Discord: stick moves the pointer, {ButtonLabel("A")} clicks, {GuideName} returns");
    }

    /// <summary>Opens Discord over the console (or focuses it if it's already open).</summary>
    void OpenDiscord() => SteamAction("discord://");

    /// <summary>Rich Presence (needs a Discord app ID in settings): what you're doing in the console.</summary>
    void UpdatePresence()
    {
        if (string.IsNullOrWhiteSpace(_settings.DiscordClientId)) return;
        if (_mode != Mode.Console || _view is View.InGame or View.Launching)
        {
            // In a game, Discord's own game detection shows what you're playing.
            DiscordRpc.Set(_settings.DiscordClientId, null, null, null);
            return;
        }
        string details = _tab == TabFriends ? "Checking on friends" : _view == View.Menu ? "In the menu" : "Browsing the library";
        var focused = FocusedGame();
        string state = focused != null ? $"Looking at {focused.Name}" : $"{_games.Count} games";
        DiscordRpc.Set(_settings.DiscordClientId, details, state, _consoleSince);
    }
}
