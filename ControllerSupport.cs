using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Twinstar;

/// <summary>Controller support of a game, from Steam's store categories (28 = full, 18 = partial).</summary>
static class ControllerSupport
{
    public const string Full = "full", Partial = "partial", None = "none", Unknown = "unknown";

    public static async Task<string> LookupAsync(string steamAppId)
    {
        try
        {
            var json = await Artwork.Http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={steamAppId}&filters=categories");
            using var doc = JsonDocument.Parse(json);
            var app = doc.RootElement.GetProperty(steamAppId);
            if (!app.GetProperty("success").GetBoolean()) return Unknown;
            if (!app.TryGetProperty("data", out var data) || !data.TryGetProperty("categories", out var cats)) return None;
            var ids = cats.EnumerateArray().Select(c => c.GetProperty("id").GetInt32()).ToHashSet();
            return ids.Contains(28) ? Full : ids.Contains(18) ? Partial : None;
        }
        catch (Exception ex) { Log.Write($"Controller support {steamAppId}: {ex.Message}"); return Unknown; }
    }

    public const string Detected = "detected";

    /// <summary>
    /// Store tags first (reliable when they say yes); otherwise the game's own files decide.
    /// Steam games only need the label, since Steam Input handles their controls anyway.
    /// </summary>
    public static async Task<string> DetermineAsync(string? steamAppId, string installDir, bool steamGame)
    {
        string tag = steamAppId != null ? await LookupAsync(steamAppId) : Unknown;
        if (tag is Full or Partial || steamGame) return tag;
        return await Task.Run(() => BinariesUseControllers(installDir)) ? Detected : None;
    }

    // Strings that only appear in programs that read game controllers.
    static readonly byte[][] Signatures = new[] { "XInputGetState", "xinput1_", "XINPUT1_", "xinput9_1_0", "Windows.Gaming.Input", "SDL_GameController", "GameControllerDB" }
        .Select(System.Text.Encoding.ASCII.GetBytes).ToArray();

    /// <summary>
    /// Looks inside a game's own executables and libraries for controller APIs. Store tags miss many
    /// games (no tag ≠ no support), but a game that links XInput / Windows.Gaming.Input / SDL's
    /// gamepad layer reads controllers.
    /// </summary>
    public static bool BinariesUseControllers(string dir) => BinariesContain(dir, Signatures);

    // Signs a game talks to PlayStation pads itself: the DS4's USB product name, Sony's vendor id,
    // DualShock/DualSense names, or input layers that support them (Rewired, SDL's gamepad layer).
    // Generic "PlayStation" text is ignored: it also appears in button-prompt names and SDKs.
    static readonly byte[][] PlayStationSignatures = new[] { "Wireless Controller", "DualShock", "DualSense", "054C", "054c", "Rewired", "SDL_GameController" }
        .SelectMany(s => new[] { System.Text.Encoding.ASCII.GetBytes(s), System.Text.Encoding.Unicode.GetBytes(s) }).ToArray();

    /// <summary>True when the game handles PlayStation controllers natively (so DS4Windows isn't needed).</summary>
    public static bool BinariesSupportPlayStation(string dir) => BinariesContain(dir, PlayStationSignatures);

    static bool BinariesContain(string dir, byte[][] signatures)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
        var files = Directory.EnumerateFiles(dir, "*.*", opts)
            .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f)).Where(f => f.Length < 400_000_000)
            .OrderByDescending(f => f.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) // main exe usually decides it
            .Take(120);
        var buffer = new byte[1 << 20];
        foreach (var f in files)
        {
            try
            {
                using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
                int carry = 0;
                while (true)
                {
                    int read = fs.Read(buffer, carry, buffer.Length - carry);
                    if (read <= 0) break;
                    var span = new ReadOnlySpan<byte>(buffer, 0, carry + read);
                    foreach (var sig in signatures) if (span.IndexOf(sig) >= 0) return true;
                    // Keep the tail so a signature split across reads is still found.
                    carry = Math.Min(48, span.Length);
                    span[^carry..].CopyTo(buffer);
                }
            }
            catch { }
        }
        return false;
    }

    public static string Describe(string support) => support switch
    {
        Full => "Full controller support",
        Partial => "Partial controller support",
        Detected => "Controller support",
        None => "Keyboard & mouse game",
        _ => "",
    };
}

/// <summary>
/// Starts DS4Windows (PlayStation pad → virtual Xbox controller) for games that only understand Xbox
/// controllers, and closes it again afterwards — but only if we started it.
/// </summary>
static class Ds4Windows
{
    static Process? _ours;

    public static bool Running => _ours is { HasExited: false };

    public static string? Find(string configured)
    {
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var p in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DS4Windows", "DS4Windows.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "DS4Windows", "DS4Windows.exe"),
            Path.Combine(local, "DS4Windows", "DS4Windows.exe"),
        })
            if (File.Exists(p)) return p;
        // Portable copies usually live wherever the zip was extracted.
        foreach (var root in new[] { Path.Combine(home, "Downloads"), Path.Combine(home, "Desktop"), Path.Combine(home, "Documents"), local })
        {
            if (!Directory.Exists(root)) continue;
            var hit = Directory.EnumerateFiles(root, "DS4Windows.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true })
                .FirstOrDefault();
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>Makes DS4Windows run (true) or stops the copy we started (false).</summary>
    public static void Ensure(bool wanted, string configuredPath)
    {
        if (wanted)
        {
            if (Running || Process.GetProcessesByName("DS4Windows").Length > 0) return; // ours, or the user's own
            var exe = Find(configuredPath);
            if (exe == null) { Log.Write("DS4Windows not found; set ds4WindowsPath in settings.json"); return; }
            try
            {
                _ours = Process.Start(new ProcessStartInfo(exe, "-m") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
                Log.Write("Started DS4Windows for a game that needs an Xbox controller");
            }
            catch (Exception ex) { Log.Write("DS4Windows start failed: " + ex.Message); }
        }
        else if (Running)
        {
            try
            {
                _ours!.CloseMainWindow();
                if (!_ours.WaitForExit(2500)) _ours.Kill();
            }
            catch { }
            _ours = null;
            Log.Write("Stopped DS4Windows");
        }
    }
}

/// <summary>
/// Controller → keyboard & mouse for games without controller support (what Steam Input's desktop
/// templates do): left stick = WASD, right stick = mouse, triggers = mouse buttons, buttons = common keys.
/// Sends scan codes, which games read more reliably than virtual keys.
/// </summary>
sealed class KbmMapper
{
    // Button → scan code. Guide is never mapped: it opens the console menu.
    static readonly (ushort mask, ushort scan)[] Keys =
    {
        (Gamepad.AMask, 0x39),      // Space
        (Gamepad.BMask, 0x1D),      // Left Ctrl (crouch)
        (Gamepad.XMask, 0x13),      // R (reload)
        (Gamepad.YMask, 0x12),      // E (interact)
        (Gamepad.LBMask, 0x10),     // Q
        (Gamepad.RBMask, 0x21),     // F
        (Gamepad.StartMask, 0x01),  // Esc
        (Gamepad.BackMask, 0x0F),   // Tab
        (0x0040, 0x2A),             // L3 → Left Shift (sprint)
        (0x0080, 0x2F),             // R3 → V
        (Gamepad.DUp, 0x02), (Gamepad.DRight, 0x03), (Gamepad.DDown, 0x04), (Gamepad.DLeft, 0x05), // 1-4
    };
    const ushort W = 0x11, A = 0x1E, S = 0x1F, D = 0x20;

    readonly HashSet<ushort> _down = new();
    bool _lmb, _rmb;
    float _mx, _my; // sub-pixel remainder so slow aiming still moves

    public void Tick(Gamepad.PadState s, bool active, float dt)
    {
        if (!active) { ReleaseAll(); return; }
        var want = new HashSet<ushort>();
        foreach (var (mask, scan) in Keys) if (s.Has(mask)) want.Add(scan);
        // Movement keys with a little hysteresis so they don't chatter at the threshold.
        bool Dir(float v, ushort key, bool positive) => positive ? v > (_down.Contains(key) ? 0.35f : 0.5f) : v < -(_down.Contains(key) ? 0.35f : 0.5f);
        if (Dir(s.LY, W, true)) want.Add(W);
        if (Dir(s.LY, S, false)) want.Add(S);
        if (Dir(s.LX, D, true)) want.Add(D);
        if (Dir(s.LX, A, false)) want.Add(A);

        foreach (var k in want) if (_down.Add(k)) Key(k, true);
        foreach (var k in _down.Where(k => !want.Contains(k)).ToList()) { _down.Remove(k); Key(k, false); }

        bool lmb = s.RT > 0.5f, rmb = s.LT > 0.5f;
        if (lmb != _lmb) { _lmb = lmb; Mouse(lmb ? 0x0002u : 0x0004u, 0, 0); }
        if (rmb != _rmb) { _rmb = rmb; Mouse(rmb ? 0x0008u : 0x0010u, 0, 0); }

        // Right stick: dead zone, then a curve for fine aim near the centre and speed at full tilt.
        static float Curve(float v) { float a = Math.Abs(v); if (a < 0.12f) return 0; a = (a - 0.12f) / 0.88f; return MathF.Sign(v) * MathF.Pow(a, 1.8f); }
        const float speed = 1500; // pixels per second at full tilt
        _mx += Curve(s.RX) * speed * dt;
        _my += -Curve(s.RY) * speed * dt;
        int dx = (int)_mx, dy = (int)_my;
        if (dx != 0 || dy != 0) { _mx -= dx; _my -= dy; Mouse(0x0001, dx, dy); }
    }

    public void ReleaseAll()
    {
        foreach (var k in _down) Key(k, false);
        _down.Clear();
        if (_lmb) { _lmb = false; Mouse(0x0004, 0, 0); }
        if (_rmb) { _rmb = false; Mouse(0x0010, 0, 0); }
        _mx = _my = 0;
    }

    // ---- SendInput ----

    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion u; }
    [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);

    static void Key(ushort scan, bool down)
    {
        var i = new INPUT { type = 1 };
        i.u.ki = new KEYBDINPUT { wScan = scan, dwFlags = 0x0008 /* SCANCODE */ | (down ? 0u : 0x0002u /* KEYUP */) };
        SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>());
    }

    internal static void Mouse(uint flags, int dx, int dy, uint data = 0)
    {
        var i = new INPUT { type = 0 };
        i.u.mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags, mouseData = data };
        SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>());
    }
}

/// <summary>
/// Controller as a mouse pointer, for app windows shown over the console (Discord calls, Steam chat):
/// either stick moves the pointer, Cross/R2 = left click, Square/L2 = right click, D-pad = scroll.
/// </summary>
sealed class PointerMapper
{
    bool _left, _right;
    float _mx, _my;
    long _nextScroll;

    public void Tick(Gamepad.PadState s, bool active, float dt)
    {
        if (!active) { Release(); return; }
        static float Pick(float a, float b) => Math.Abs(a) > Math.Abs(b) ? a : b;
        static float Curve(float v) { float a = Math.Abs(v); if (a < 0.15f) return 0; a = (a - 0.15f) / 0.85f; return MathF.Sign(v) * a * a; }
        const float speed = 1300;
        _mx += Curve(Pick(s.LX, s.RX)) * speed * dt;
        _my += -Curve(Pick(s.LY, s.RY)) * speed * dt;
        int dx = (int)_mx, dy = (int)_my;
        if (dx != 0 || dy != 0) { _mx -= dx; _my -= dy; KbmMapper.Mouse(0x0001, dx, dy); }

        bool left = s.Has(Gamepad.AMask) || s.RT > 0.5f, right = s.Has(Gamepad.XMask) || s.LT > 0.5f;
        if (left != _left) { _left = left; KbmMapper.Mouse(left ? 0x0002u : 0x0004u, 0, 0); }
        if (right != _right) { _right = right; KbmMapper.Mouse(right ? 0x0008u : 0x0010u, 0, 0); }

        long now = Environment.TickCount64;
        int wheel = s.Has(Gamepad.DUp) ? 120 : s.Has(Gamepad.DDown) ? -120 : 0;
        if (wheel != 0 && now >= _nextScroll) { _nextScroll = now + 90; KbmMapper.Mouse(0x0800, 0, 0, unchecked((uint)wheel)); }
    }

    public void Release()
    {
        if (_left) { _left = false; KbmMapper.Mouse(0x0004, 0, 0); }
        if (_right) { _right = false; KbmMapper.Mouse(0x0010, 0, 0); }
        _mx = _my = 0;
    }
}
