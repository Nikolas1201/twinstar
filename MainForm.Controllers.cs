namespace Twinstar;

/// <summary>
/// Per-game controller handling in console mode:
///  - Steam games: left alone (Steam Input already adapts the pad when they're launched through Steam).
///  - Controller games outside Steam: DS4Windows runs while you play with a PlayStation pad, so games
///    that only understand Xbox controllers still work.
///  - Keyboard & mouse games: the built-in mapper turns the pad into keys and mouse while the game has focus.
/// Support comes from Steam's store data and can be overridden per game in Game options.
/// </summary>
sealed partial class MainForm
{
    const string ModeAuto = "auto", ModeSteam = "steam", ModeController = "controller", ModeKbm = "kbm";

    readonly KbmMapper _mapper = new();
    HashSet<int> _activePids = new();
    long _lastPadTick;

    string SupportFor(Game g) => _settings.ControllerSupport.TryGetValue(g.Name, out var s) ? s : ControllerSupport.Unknown;

    string OverrideFor(Game g) => _settings.ControllerMode.TryGetValue(g.Name, out var m) ? m : ModeAuto;

    /// <summary>How this game gets controller input: steam, controller (DS4Windows if needed) or kbm (mapper).</summary>
    string ControllerModeFor(Game g)
    {
        var o = OverrideFor(g);
        if (o != ModeAuto) return o;
        if (g.Source == GameSource.Steam) return ModeSteam;
        return SupportFor(g) == ControllerSupport.None ? ModeKbm : ModeController;
    }

    string ControllerLabel(Game g) => ControllerModeFor(g) == ModeKbm
        ? "Keyboard & mouse (mapped to controller)"
        : ControllerSupport.Describe(SupportFor(g));

    /// <summary>Runs every controller poll (~60 Hz): drives the keyboard/mouse mapper while it applies.</summary>
    void PadTick()
    {
        long now = Environment.TickCount64;
        float dt = Math.Clamp((now - _lastPadTick) / 1000f, 0, 0.05f);
        _lastPadTick = now;
        bool active = _mode == Mode.Console && _view == View.InGame && _active != null
            && ControllerModeFor(_active) == ModeKbm && ForegroundIsGame();
        _mapper.Tick(_pad.State, active, dt);
        UpdatePointer(dt);
    }

    bool ForegroundIsGame()
    {
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint pid);
        return _activePids.Contains((int)pid);
    }

    /// <summary>Runs twice a second in console mode: keeps DS4Windows running exactly while it's needed.</summary>
    void UpdateControllerHelpers()
    {
        _activePids = _active != null ? GameProcesses.PidsFor(_active) : new();
        // DS4Windows only for games that speak Xbox controllers but not PlayStation ones.
        bool needsXbox = _active != null && GameProcesses.IsRunning(_active) && ControllerModeFor(_active) == ModeController
            && _settings.UseDs4Windows && !(_settings.PlayStationNative.TryGetValue(_active.Name, out var ps) && ps);
        // Once started for a game it stays until the game ends (DS4Windows hides the real pad, which
        // would otherwise make it look like no PlayStation controller is connected any more).
        Ds4Windows.Ensure(needsXbox && (Ds4Windows.Running || _pad.Kind == PadKind.PlayStation), _settings.Ds4WindowsPath);
    }

    void StopControllerHelpers()
    {
        _mapper.ReleaseAll();
        Ds4Windows.Ensure(false, _settings.Ds4WindowsPath);
    }

    MenuItem ControllerModeItem(Game g)
    {
        var o = OverrideFor(g);
        string value = o switch
        {
            ModeController => "Controller",
            ModeKbm => "Keyboard & mouse",
            ModeSteam => "Steam Input",
            _ => "Auto: " + ControllerModeFor(g) switch { ModeSteam => "Steam Input", ModeKbm => "Keyboard & mouse", _ => "Controller" },
        };
        return new MenuItem("Controller mode", () =>
        {
            // Auto → Controller → Keyboard & mouse → Auto
            _settings.ControllerMode[g.Name] = o switch { ModeAuto => ModeController, ModeController => ModeKbm, _ => ModeAuto };
            if (_settings.ControllerMode[g.Name] == ModeAuto) _settings.ControllerMode.Remove(g.Name);
            _settings.Save();
            RequestFrames();
        }, value);
    }

    bool _detectingSupport;

    /// <summary>Looks up controller support once per game (Steam store data), in the background.</summary>
    async void DetectControllerSupport()
    {
        if (_detectingSupport) return;
        var todo = _games.Where(g => !g.IsApp && (!_settings.ControllerSupport.ContainsKey(g.Name)
            || (g.Source != GameSource.Steam && !_settings.PlayStationNative.ContainsKey(g.Name)))).ToList();
        if (todo.Count == 0) return;
        _detectingSupport = true;
        var found = new Dictionary<string, string>();
        var psNative = new Dictionary<string, bool>();
        try
        {
            foreach (var g in todo)
            {
                var s = _settings.ControllerSupport.TryGetValue(g.Name, out var known) ? known
                    : await ControllerSupport.DetermineAsync(g.SteamAppId, g.InstallDir, g.Source == GameSource.Steam);
                if (s != ControllerSupport.Unknown) found[g.Name] = s;
                if (g.Source != GameSource.Steam)
                {
                    var dir = g.InstallDir;
                    psNative[g.Name] = await Task.Run(() => ControllerSupport.BinariesSupportPlayStation(dir));
                }
                if (g.SteamAppId != null) await Task.Delay(400); // be gentle with the store API
            }
        }
        finally { _detectingSupport = false; }
        _settings = Settings.Load();
        foreach (var (name, s) in found) _settings.ControllerSupport[name] = s;
        foreach (var (name, ps) in psNative) _settings.PlayStationNative[name] = ps;
        _settings.Save();
        Log.Write($"Controller support: {string.Join(", ", found.Select(kv => $"{kv.Key}={kv.Value}"))}");
        RequestFrames();
    }
}
