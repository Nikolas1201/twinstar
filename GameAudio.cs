using System.Runtime.InteropServices;

namespace Twinstar;

/// <summary>
/// Mutes and unmutes a game's own audio through Windows' per-application audio sessions (what the
/// Volume Mixer controls), on every active output device. Only sessions this class muted are unmuted
/// again, so a game the user muted themselves stays muted.
/// </summary>
static class GameAudio
{
    static readonly HashSet<uint> MutedByUs = new();

    public static void Mute(IEnumerable<int> pids) => Apply(pids.Select(p => (uint)p).ToHashSet(), mute: true);

    /// <summary>Unmutes every session we muted earlier (whatever process it belongs to).</summary>
    public static void UnmuteAll()
    {
        if (MutedByUs.Count == 0) return;
        Apply(MutedByUs.ToHashSet(), mute: false);
        MutedByUs.Clear();
    }

    /// <summary>Diagnostic: every audio session's process id and mute state (changes nothing).</summary>
    public static List<string> Describe()
    {
        var lines = new List<string>();
        Apply(new HashSet<uint>(), mute: false, lines);
        return lines;
    }

    static void Apply(HashSet<uint> pids, bool mute, List<string>? describe = null)
    {
        if (pids.Count == 0 && describe == null) return;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            enumerator.EnumAudioEndpoints(0 /* render */, 1 /* active */, out var devices);
            devices.GetCount(out int deviceCount);
            describe?.Add($"{deviceCount} active output devices");
            var iid = typeof(IAudioSessionManager2).GUID;
            for (int d = 0; d < deviceCount; d++)
            {
                devices.Item(d, out var device);
                device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var managerObj);
                var manager = (IAudioSessionManager2)managerObj;
                manager.GetSessionEnumerator(out var sessions);
                sessions.GetCount(out int count);
                describe?.Add($"device {d}: {count} sessions");
                for (int i = 0; i < count; i++)
                {
                    sessions.GetSession(i, out var c2);
                    object control = c2;
                    c2.GetProcessId(out uint pid);
                    if (describe != null && control is ISimpleAudioVolume v0)
                    {
                        v0.GetMute(out bool m);
                        v0.GetMasterVolume(out float vol);
                        string name = "";
                        try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
                        describe.Add($"device {d}: pid {pid} {name} muted={m} volume={vol:F2}");
                        continue;
                    }
                    if (!pids.Contains(pid) || control is not ISimpleAudioVolume volume) continue;
                    var ctx = Guid.Empty;
                    if (mute)
                    {
                        volume.GetMute(out bool already);
                        if (already) continue; // muted by the user: leave it alone, and never unmute it later
                        volume.SetMute(true, ref ctx);
                        MutedByUs.Add(pid);
                    }
                    else volume.SetMute(false, ref ctx);
                }
            }
        }
        catch (Exception ex) { Log.Write("Game audio: " + ex.Message); }
    }

    // ---- Core Audio COM interfaces (only the members used; vtable order must match) ----

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr groupingParam, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr groupingParam, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, ref Guid ctx);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
