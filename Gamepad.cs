using System.Runtime.InteropServices;

namespace Twinstar;

enum Btn { Up, Down, Left, Right, A, B, X, Y, Start, Back, Guide, LB, RB }
enum PadKind { None, Xbox, PlayStation }

/// <summary>
/// Reads controllers from two sources and merges them into one stream of button presses:
///  - XInput (Xbox and anything presenting as one). The Guide button is only reported by the
///    undocumented XInputGetStateEx (ordinal 100 of xinput1_4.dll).
///  - Raw HID reports from Sony DualShock 4 / DualSense pads, which XInput doesn't see.
/// Both work while our window is in the background, which is what lets Guide/PS pull us over a game.
/// </summary>
sealed class Gamepad
{
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }

    // Padding for XInputGetStateEx, whose struct is larger on some systems.
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE_EX { public XINPUT_STATE State; public uint Reserved; }

    [DllImport("xinput1_4.dll", EntryPoint = "#100")] static extern int XInputGetStateEx(int index, out XINPUT_STATE_EX state);
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern int XInputGetState(int index, out XINPUT_STATE state);

    // XInput button bits; Sony reports are translated into the same layout.
    public const ushort DUp = 0x0001, DDown = 0x0002, DLeft = 0x0004, DRight = 0x0008, StartMask = 0x0010, BackMask = 0x0020,
        LBMask = 0x0100, RBMask = 0x0200, GuideMask = 0x0400, AMask = 0x1000, BMask = 0x2000, XMask = 0x4000, YMask = 0x8000;

    static readonly (ushort mask, Btn btn)[] Map =
    {
        (DUp, Btn.Up), (DDown, Btn.Down), (DLeft, Btn.Left), (DRight, Btn.Right),
        (StartMask, Btn.Start), (BackMask, Btn.Back), (LBMask, Btn.LB), (RBMask, Btn.RB),
        (GuideMask, Btn.Guide), (AMask, Btn.A), (BMask, Btn.B), (XMask, Btn.X), (YMask, Btn.Y),
    };

    const short StickThreshold = 16000;
    const int RepeatDelayMs = 380, RepeatRateMs = 110;
    const int ConnectGraceMs = 1500;
    const int SonyTimeoutMs = 1000; // Sony pads stream reports constantly; silence means it's gone

    public event Action<Btn>? Pressed;

    /// <summary>Continuous state of all controllers merged (sticks -1..1, Y up; triggers 0..1), for the input mapper.</summary>
    public struct PadState
    {
        public ushort Buttons;
        public float LX, LY, RX, RY, LT, RT;
        public bool Has(ushort mask) => (Buttons & mask) != 0;
    }
    public PadState State { get; private set; }
    PadState _accum;

    void Merge(XINPUT_GAMEPAD p)
    {
        static float Axis(short v) => Math.Clamp(v / 32767f, -1, 1);
        static float Bigger(float a, float b) => Math.Abs(b) > Math.Abs(a) ? b : a;
        _accum.Buttons |= p.wButtons;
        _accum.LX = Bigger(_accum.LX, Axis(p.sThumbLX)); _accum.LY = Bigger(_accum.LY, Axis(p.sThumbLY));
        _accum.RX = Bigger(_accum.RX, Axis(p.sThumbRX)); _accum.RY = Bigger(_accum.RY, Axis(p.sThumbRY));
        _accum.LT = Math.Max(_accum.LT, p.bLeftTrigger / 255f); _accum.RT = Math.Max(_accum.RT, p.bRightTrigger / 255f);
    }
    /// <summary>Raised with the new controller count whenever a controller is plugged in or drops out.</summary>
    public event Action<int>? ConnectionChanged;
    public bool AnyConnected => Count > 0;
    public int Count { get; private set; }

    /// <summary>Type of the controller last used (or connected), for button prompts.</summary>
    public PadKind Kind { get; private set; }
    public string Name { get; private set; } = "";

    /// <summary>Per-controller state used to ignore buttons held while it connects.</summary>
    sealed class Slot
    {
        public long ConnectedAt;
        // Buttons held at the moment a controller connects (e.g. the Guide/PS press that powers it on)
        // are ignored until released, so connecting never triggers an action by itself.
        public ushort Suppressed;
        public PadKind Kind;
        public string Name = "";
    }

    bool _useEx = true;
    readonly Slot?[] _xinput = new Slot?[4];
    long _lastSlotScan;
    readonly HashSet<Btn> _held = new();
    readonly Dictionary<Btn, long> _nextRepeat = new();
    Slot? _lastUsed;

    public void Poll()
    {
        long now = Environment.TickCount64;
        // Querying an empty slot is slow, so only rescan disconnected slots twice a second.
        bool scanAll = now - _lastSlotScan > 500;
        if (scanAll) _lastSlotScan = now;

        var down = new HashSet<Btn>();
        var live = new List<Slot>();
        _accum = default;
        for (int i = 0; i < 4; i++)
        {
            if (_xinput[i] == null && !scanAll) continue;
            if (!TryRead(i, out var pad)) { _xinput[i] = null; continue; }
            _xinput[i] ??= NewSlot(now, pad.wButtons, PadKind.Xbox, "Xbox controller");
            Collect(_xinput[i]!, pad, now, down);
            Merge(pad);
            live.Add(_xinput[i]!);
        }

        foreach (var (handle, dev) in _sony.ToArray())
        {
            if (!dev.HasReport) continue;
            if (now - dev.LastReport > SonyTimeoutMs) { dev.Slot = null; dev.HasReport = false; continue; }
            dev.Slot ??= NewSlot(now, dev.State.wButtons, PadKind.PlayStation, dev.Name);
            Collect(dev.Slot, dev.State, now, down);
            Merge(dev.State);
            live.Add(dev.Slot);
        }

        State = _accum;
        if (live.Count != Count)
        {
            Count = live.Count;
            if (_lastUsed == null || !live.Contains(_lastUsed)) _lastUsed = live.LastOrDefault();
            UpdateKind();
            ConnectionChanged?.Invoke(Count);
        }

        foreach (var b in down)
        {
            if (_held.Add(b))
            {
                _nextRepeat[b] = now + RepeatDelayMs;
                Pressed?.Invoke(b);
            }
            else if (b <= Btn.Right && now >= _nextRepeat[b])
            {
                _nextRepeat[b] = now + RepeatRateMs;
                Pressed?.Invoke(b);
            }
        }
        _held.RemoveWhere(b => !down.Contains(b));
    }

    /// <summary>A freshly connected controller becomes the one whose button prompts we show.</summary>
    Slot NewSlot(long now, ushort buttons, PadKind kind, string name) =>
        _lastUsed = new() { ConnectedAt = now, Suppressed = (ushort)(buttons | GuideMask), Kind = kind, Name = name };

    void Collect(Slot s, XINPUT_GAMEPAD pad, long now, HashSet<Btn> down)
    {
        s.Suppressed &= pad.wButtons;
        if (now - s.ConnectedAt < ConnectGraceMs && (pad.wButtons & GuideMask) != 0) s.Suppressed |= GuideMask;
        if (now - s.ConnectedAt < 150) return; // let a fresh connection settle (stick drift, startup packets)

        ushort buttons = (ushort)(pad.wButtons & ~s.Suppressed);
        int before = down.Count;
        foreach (var (mask, btn) in Map)
            if ((buttons & mask) != 0) down.Add(btn);
        if (pad.sThumbLY > StickThreshold) down.Add(Btn.Up);
        if (pad.sThumbLY < -StickThreshold) down.Add(Btn.Down);
        if (pad.sThumbLX < -StickThreshold) down.Add(Btn.Left);
        if (pad.sThumbLX > StickThreshold) down.Add(Btn.Right);
        if (down.Count > before && _lastUsed != s) { _lastUsed = s; UpdateKind(); }
    }

    void UpdateKind()
    {
        Kind = _lastUsed?.Kind ?? PadKind.None;
        Name = _lastUsed?.Name ?? "";
    }

    bool TryRead(int index, out XINPUT_GAMEPAD pad)
    {
        pad = default;
        try
        {
            if (_useEx)
            {
                if (XInputGetStateEx(index, out var ex) != 0) return false;
                pad = ex.State.Gamepad;
                return true;
            }
            if (XInputGetState(index, out var st) != 0) return false;
            pad = st.Gamepad;
            return true;
        }
        catch (EntryPointNotFoundException) { _useEx = false; return false; }
        catch (DllNotFoundException) { return false; }
    }

    // ================= Sony (DualShock 4 / DualSense) over Raw Input =================

    sealed class SonyDevice
    {
        public bool Supported;
        public bool DualSense;
        public string Name = "";
        public bool HasReport;
        public long LastReport;
        public XINPUT_GAMEPAD State;
        public Slot? Slot;
    }

    readonly Dictionary<IntPtr, SonyDevice> _sony = new();

    const ushort SonyVendor = 0x054C;
    static readonly HashSet<uint> DualShock4Pids = new() { 0x05C4, 0x09CC, 0x0BA0, 0x0BA1 };
    static readonly HashSet<uint> DualSensePids = new() { 0x0CE6, 0x0DF2 };

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }

    [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr hRawInput, uint command, byte[]? data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint command, byte[] data, ref uint size);

    public const int WM_INPUT = 0x00FF, WM_INPUT_DEVICE_CHANGE = 0x00FE;
    const uint RID_INPUT = 0x10000003, RIDI_DEVICEINFO = 0x2000000b;
    const uint RIDEV_INPUTSINK = 0x100, RIDEV_DEVNOTIFY = 0x2000;
    static readonly uint HeaderSize = (uint)(8 + 2 * IntPtr.Size);

    /// <summary>Subscribes the window to HID gamepad/joystick reports, including while it's in the background.</summary>
    public void RegisterRawInput(IntPtr hwnd)
    {
        var devs = new[]
        {
            new RAWINPUTDEVICE { UsagePage = 1, Usage = 5, Flags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, Target = hwnd }, // gamepad
            new RAWINPUTDEVICE { UsagePage = 1, Usage = 4, Flags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, Target = hwnd }, // joystick
        };
        if (!RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            Log.Write("RegisterRawInputDevices failed: " + Marshal.GetLastWin32Error());
    }

    public void HandleDeviceChange(IntPtr wParam, IntPtr hDevice)
    {
        if ((int)wParam == 2) _sony.Remove(hDevice); // GIDC_REMOVAL
    }

    public void HandleRawInput(IntPtr lParam)
    {
        uint size = 0;
        GetRawInputData(lParam, RID_INPUT, null, ref size, HeaderSize);
        if (size == 0 || size > 4096) return;
        var buf = new byte[size];
        if (GetRawInputData(lParam, RID_INPUT, buf, ref size, HeaderSize) != size) return;
        if (BitConverter.ToInt32(buf, 0) != 2) return; // RIM_TYPEHID

        var hDevice = (IntPtr)(IntPtr.Size == 8 ? BitConverter.ToInt64(buf, 8) : BitConverter.ToInt32(buf, 8));
        if (!_sony.TryGetValue(hDevice, out var dev)) _sony[hDevice] = dev = Identify(hDevice);
        if (!dev.Supported) return;

        int off = (int)HeaderSize;
        int reportSize = BitConverter.ToInt32(buf, off);
        int reportCount = BitConverter.ToInt32(buf, off + 4);
        if (reportSize <= 0 || reportCount <= 0) return;
        int start = off + 8 + reportSize * (reportCount - 1); // newest report
        if (start + reportSize > buf.Length) return;
        var report = new ReadOnlySpan<byte>(buf, start, reportSize);

        if (TryParseSony(report, dev.DualSense, out var state))
        {
            dev.State = state;
            dev.LastReport = Environment.TickCount64;
            dev.HasReport = true;
        }
    }

    static SonyDevice Identify(IntPtr hDevice)
    {
        var info = new byte[32];
        BitConverter.GetBytes(32).CopyTo(info, 0); // cbSize
        uint size = 32;
        var dev = new SonyDevice();
        if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICEINFO, info, ref size) == unchecked((uint)-1)) return dev;
        uint vid = BitConverter.ToUInt32(info, 8), pid = BitConverter.ToUInt32(info, 12);
        if (vid != SonyVendor) return dev; // Xbox pads also show up here; XInput already handles them
        dev.DualSense = DualSensePids.Contains(pid);
        dev.Supported = dev.DualSense || DualShock4Pids.Contains(pid);
        dev.Name = dev.DualSense ? (pid == 0x0DF2 ? "DualSense Edge" : "DualSense") : "DualShock 4";
        if (dev.Supported) Log.Write($"Found {dev.Name} (PID {pid:X4})");
        return dev;
    }

    /// <summary>Translates a DS4/DualSense input report (USB or Bluetooth) into XInput layout.</summary>
    static bool TryParseSony(ReadOnlySpan<byte> r, bool dualSense, out XINPUT_GAMEPAD pad)
    {
        pad = default;
        if (r.Length < 10) return false;
        // Offsets are relative to the USB report; Bluetooth "full" reports carry extra header bytes.
        // DualSense full reports keep buttons at 8..10; DS4 and both pads' simple BT reports use 5..7.
        int shift, b0;
        byte id = r[0];
        if (dualSense && id == 0x31) { shift = 1; b0 = 8; }
        else if (dualSense && id == 0x01 && r.Length >= 30) { shift = 0; b0 = 8; }
        else if (id == 0x11) { shift = 2; b0 = 5; }
        else if (id == 0x01) { shift = 0; b0 = 5; }
        else return false;
        if (r.Length < b0 + shift + 3) return false;

        byte lx = r[1 + shift], ly = r[2 + shift];
        byte face = r[b0 + shift], shoulders = r[b0 + shift + 1], misc = r[b0 + shift + 2];

        ushort buttons = 0;
        int hat = face & 0x0F;
        if (hat is 0 or 1 or 7) buttons |= DUp;
        if (hat is 1 or 2 or 3) buttons |= DRight;
        if (hat is 3 or 4 or 5) buttons |= DDown;
        if (hat is 5 or 6 or 7) buttons |= DLeft;
        if ((face & 0x10) != 0) buttons |= XMask;      // Square
        if ((face & 0x20) != 0) buttons |= AMask;      // Cross
        if ((face & 0x40) != 0) buttons |= BMask;      // Circle
        if ((face & 0x80) != 0) buttons |= YMask;      // Triangle
        if ((shoulders & 0x01) != 0) buttons |= LBMask; // L1
        if ((shoulders & 0x02) != 0) buttons |= RBMask; // R1
        if ((shoulders & 0x10) != 0) buttons |= BackMask;  // Share / Create
        if ((shoulders & 0x20) != 0) buttons |= StartMask; // Options
        if ((misc & 0x01) != 0) buttons |= GuideMask;      // PS button

        pad.wButtons = buttons;
        pad.sThumbLX = (short)Math.Clamp((lx - 128) * 256, short.MinValue, short.MaxValue);
        pad.sThumbLY = (short)Math.Clamp((128 - ly) * 256, short.MinValue, short.MaxValue); // HID Y grows downward
        byte rx = r[3 + shift], ry = r[4 + shift];
        pad.sThumbRX = (short)Math.Clamp((rx - 128) * 256, short.MinValue, short.MaxValue);
        pad.sThumbRY = (short)Math.Clamp((128 - ry) * 256, short.MinValue, short.MaxValue);
        // Analog triggers: DualSense full reports keep them right after the sticks; DS4 after the buttons.
        int lt = b0 == 8 ? 5 + shift : b0 + shift + 3;
        if (lt + 1 < r.Length) { pad.bLeftTrigger = r[lt]; pad.bRightTrigger = r[lt + 1]; }
        return true;
    }
}
