using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Twinstar;

/// <summary>Win32 interop: window focus, hotkeys, taskbar hiding, process paths, keyboard hook.</summary>
static class Native
{
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    public const int WM_HOTKEY = 0x0312;
    public const uint WM_CLOSE = 0x0010;
    const int SW_HIDE = 0, SW_SHOW = 5, SW_RESTORE = 9;
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOOLWINDOW = 0x80;
    const uint GW_OWNER = 4;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);

    /// <summary>Brings a window to the foreground even when Windows' focus-stealing rules would block it.</summary>
    public static void ForceForeground(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return;
        if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);

        IntPtr fg = GetForegroundWindow();
        if (fg == hWnd) return;
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint myThread = GetCurrentThreadId();
        bool attached = fg != IntPtr.Zero && fgThread != myThread && AttachThreadInput(myThread, fgThread, true);
        BringWindowToTop(hWnd);
        bool ok = SetForegroundWindow(hWnd);
        if (attached) AttachThreadInput(myThread, fgThread, false);

        if (!ok || GetForegroundWindow() != hWnd)
        {
            // Last resort: a synthetic Alt tap grants this process foreground rights.
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
        }
    }

    public static string? GetProcessPath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// Visible, non-tool top-level windows of the given processes. Minimized windows are included
    /// (with a tiny area so a restored window wins): fullscreen games minimize when they lose
    /// focus, and dropping them here would lose track of the game for good.
    /// </summary>
    public static List<(IntPtr hWnd, int pid, int area)> GetMainWindows(ISet<int> pids)
    {
        var result = new List<(IntPtr, int, int)>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var owner = GetWindow(h, GW_OWNER);
            if (owner != IntPtr.Zero && IsWindowVisible(owner)) return true; // dialogs of a visible window
            if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (!pids.Contains((int)pid)) return true;
            if (IsIconic(h)) { result.Add((h, (int)pid, 1)); return true; }
            GetWindowRect(h, out var r);
            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            if (w < 200 || ht < 150) return true;
            result.Add((h, (int)pid, w * ht));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    /// <summary>Moves a window in the z-order to sit directly beneath another one.</summary>
    public static void PlaceBehind(IntPtr hWnd, IntPtr above) =>
        SetWindowPos(hWnd, above, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);

    static readonly HashSet<string> ShellClasses = new() { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW" };

    /// <summary>Topmost-in-z-order normal app window that isn't ours or part of the desktop.</summary>
    public static IntPtr TopOtherWindow(IntPtr exclude)
    {
        IntPtr found = IntPtr.Zero;
        int me = Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (h == exclude || !IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == me) return true;
            var cls = new StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            if (ShellClasses.Contains(cls.ToString())) return true;
            GetWindowRect(h, out var r);
            if (r.Right - r.Left < 200 || r.Bottom - r.Top < 150) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    // ---- Explorer taskbar ----

    static IEnumerable<IntPtr> TaskbarWindows()
    {
        foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            IntPtr h = IntPtr.Zero;
            while ((h = FindWindowEx(IntPtr.Zero, h, cls, null)) != IntPtr.Zero) yield return h;
        }
    }

    public static void SetTaskbarVisible(bool visible)
    {
        foreach (var h in TaskbarWindows())
            if (IsWindowVisible(h) != visible) ShowWindow(h, visible ? SW_SHOW : SW_HIDE);
    }

    public static bool ExplorerRunning() => Process.GetProcessesByName("explorer").Length > 0;

    // ---- Instant taskbar suppression (console mode) ----
    // Polling leaves the taskbar visible until the next tick; a WinEvent hook hears the moment any
    // window is shown, so a taskbar Explorer re-shows (restart, notification, display change) is
    // hidden again before it is ever drawn on screen.

    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc fn, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    const uint EVENT_OBJECT_CREATE = 0x8000, EVENT_OBJECT_SHOW = 0x8002, WINEVENT_OUTOFCONTEXT = 0;

    static IntPtr _winEventHook;
    static WinEventProc? _winEventProc; // kept alive so the GC doesn't collect the callback
    static volatile bool _suppressTaskbar;

    /// <summary>While true, every taskbar window is hidden the instant it appears.</summary>
    public static bool SuppressTaskbar
    {
        get => _suppressTaskbar;
        set
        {
            _suppressTaskbar = value;
            SetTaskbarVisible(!value);
        }
    }

    /// <summary>Set in shell mode while in console: an Explorer that tries to become the shell is closed instantly.</summary>
    public static volatile bool KillShellExplorer;

    // ---- Shell window registration ----
    // Explorer only turns into the desktop + taskbar when no shell window is registered. Claiming that
    // slot makes any Explorer that Windows starts (opening a folder, some links) a plain folder process.

    [DllImport("user32.dll")] static extern bool SetShellWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    static NativeWindow? _shellWindow;

    public static void ClaimShell()
    {
        if (_shellWindow != null || GetShellWindow() != IntPtr.Zero) return;
        _shellWindow = new NativeWindow();
        _shellWindow.CreateHandle(new CreateParams { Caption = "TwinstarShell", Style = unchecked((int)0x80000000) /* WS_POPUP, hidden */ });
        if (!SetShellWindow(_shellWindow.Handle)) { _shellWindow.DestroyHandle(); _shellWindow = null; }
    }

    /// <summary>Gives the shell slot back (desktop mode), so Explorer can become the shell normally.</summary>
    public static void ReleaseShell()
    {
        _shellWindow?.DestroyHandle();
        _shellWindow = null;
    }

    /// <summary>Raised (on the UI thread) whenever any top-level window becomes visible.</summary>
    public static event Action<IntPtr>? WindowShown;

    public static string? ProcessNameOf(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        var path = GetProcessPath((int)pid);
        return path == null ? null : Path.GetFileNameWithoutExtension(path);
    }

    public static string WindowTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);

    /// <summary>Call once from the UI thread (the hook's callbacks arrive through its message loop).</summary>
    public static void InstallTaskbarHook()
    {
        _winEventProc = (_, ev, hwnd, idObject, _, _, _) =>
        {
            if (idObject != 0 || hwnd == IntPtr.Zero) return; // OBJID_WINDOW only
            if (ev == EVENT_OBJECT_SHOW && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero) WindowShown?.Invoke(hwnd);
            if (!_suppressTaskbar) return;
            var cls = new StringBuilder(32);
            GetClassName(hwnd, cls, cls.Capacity);
            var name = cls.ToString();
            if (name is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                ShowWindow(hwnd, SW_HIDE);
                // As the real shell in console mode, an Explorer that builds a taskbar is ended at once.
                if (KillShellExplorer)
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    try { Process.GetProcessById((int)pid).Kill(); } catch { }
                }
            }
        };
        _winEventHook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    public static void RemoveTaskbarHook()
    {
        if (_winEventHook != IntPtr.Zero) UnhookWinEvent(_winEventHook);
        _winEventHook = IntPtr.Zero;
    }

    // ---- Low-level keyboard hook (blocks the Windows key in console mode) ----

    const int WH_KEYBOARD_LL = 13;
    delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

    static IntPtr _hook;
    static LowLevelProc? _hookProc; // kept alive so the GC doesn't collect the callback
    public static volatile bool BlockWinKey;
    /// <summary>Raised with the virtual key when Ctrl+Alt+F11/F12 is pressed (works even if another app owns the hotkey).</summary>
    public static event Action<int>? CtrlAltKey;

    public static void InstallKeyboardHook()
    {
        _hookProc = (code, w, l) =>
        {
            if (code >= 0)
            {
                int vk = Marshal.ReadInt32(l);
                if (BlockWinKey && (vk == 0x5B || vk == 0x5C)) return (IntPtr)1; // LWin / RWin
                bool keyDown = w == 0x100 || w == 0x104; // WM_KEYDOWN / WM_SYSKEYDOWN
                if ((vk == 0x7A || vk == 0x7B) &&
                    (GetAsyncKeyState(0x11) & 0x8000) != 0 && (GetAsyncKeyState(0x12) & 0x8000) != 0)
                {
                    if (keyDown) CtrlAltKey?.Invoke(vk);
                    return (IntPtr)1;
                }
            }
            return CallNextHookEx(_hook, code, w, l);
        };
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
    }

    public static void RemoveKeyboardHook()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
