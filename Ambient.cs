using System.Runtime.InteropServices;

namespace Twinstar;

/// <summary>
/// Console-style ambience: calm generative music loops (one for the home screen, one for the mode
/// selector), synthesized once in the background. Both are mixed in software into a single waveOut
/// stream, with per-track fades applied to the samples, so switching crossfades smoothly. (Volume
/// is never set with waveOutSetVolume: on modern Windows that changes the whole app's session
/// volume, so two faders fighting over it made everything crackle.)
/// </summary>
static class Ambient
{
    const int Rate = 44100;
    const float MasterVolume = 0.55f;
    const int BufferFrames = 2048, BufferCount = 4; // ~46 ms each, ~186 ms queued

    [StructLayout(LayoutKind.Sequential)]
    struct WaveFormatEx
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WaveHdr
    {
        public IntPtr lpData;
        public uint dwBufferLength, dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags, dwLoops;
        public IntPtr lpNext, reserved;
    }

    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hwo, uint device, ref WaveFormatEx fmt, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutPause(IntPtr hwo);
    [DllImport("winmm.dll")] static extern int waveOutRestart(IntPtr hwo);
    [DllImport("winmm.dll")] static extern int waveOutGetVolume(IntPtr hwo, out uint volume);
    [DllImport("winmm.dll")] static extern int waveOutSetVolume(IntPtr hwo, uint volume);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);
    const uint WAVE_MAPPER = 0xFFFFFFFF, CALLBACK_EVENT = 0x50000, WHDR_DONE = 1;
    static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.dwFlags));

    /// <summary>Which loop plays: the console home/menus, or the mode selector.</summary>
    public enum Track { Home, Selector }

    /// <summary>One loop: composed lazily the first time it's wanted, then mixed in at its own fade level.</summary>
    sealed class Voice(Func<byte[]> compose)
    {
        public volatile short[]? Pcm; // interleaved stereo
        public volatile bool Want;
        public float Volume;
        public int Pos;
        bool _composing;

        public void Ensure()
        {
            if (Pcm != null || _composing) return;
            _composing = true;
            Task.Run(() =>
            {
                try
                {
                    var bytes = compose();
                    var pcm = new short[bytes.Length / 2];
                    Buffer.BlockCopy(bytes, 0, pcm, 0, bytes.Length);
                    Pcm = pcm;
                    Wake.Set();
                }
                catch (Exception ex) { Log.Write("Ambient: " + ex.Message); }
            });
        }
    }

    static readonly Voice[] Voices = { new(AmbientComposer.Compose), new(SelectorComposer.Compose) };
    static readonly AutoResetEvent Wake = new(false);
    static Thread? _mixer;
    static readonly object Gate = new();

    /// <summary>
    /// Older versions faded with waveOutSetVolume, which Windows applies to (and remembers for) the
    /// whole app, so it could be left at 0 and silence every sound. Puts a silenced app back to full.
    /// </summary>
    public static void RestoreSessionVolume()
    {
        try
        {
            var fmt = new WaveFormatEx { wFormatTag = 1, nChannels = 2, nSamplesPerSec = Rate, wBitsPerSample = 16, nBlockAlign = 4, nAvgBytesPerSec = Rate * 4 };
            if (waveOutOpen(out var dev, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) return;
            if (waveOutGetVolume(dev, out uint v) == 0 && v == 0)
            {
                waveOutSetVolume(dev, 0xFFFFFFFF);
                Log.Write("Ambient: app volume was stuck at 0, restored");
            }
            waveOutClose(dev);
        }
        catch { }
    }

    /// <summary>Asks for the ambience to be audible (fades in) or silent (fades out, then pauses).</summary>
    public static void Set(bool on) => Set(on, Track.Home);

    /// <summary>Plays one track (the other fades out, so switching crossfades) or silences both.</summary>
    public static void Set(bool on, Track track)
    {
        for (int i = 0; i < Voices.Length; i++)
        {
            bool want = on && i == (int)track;
            if (want) Voices[i].Ensure();
            Voices[i].Want = want;
        }
        if (on)
            lock (Gate)
                if (_mixer == null)
                {
                    _mixer = new Thread(MixLoop) { IsBackground = true, Name = "Ambient", Priority = ThreadPriority.AboveNormal };
                    _mixer.Start();
                }
        Wake.Set();
    }

    static bool _loggedWriteError;

    static void MixLoop()
    {
        try
        {
            var fmt = new WaveFormatEx
            {
                wFormatTag = 1, nChannels = 2, nSamplesPerSec = Rate, wBitsPerSample = 16,
                nBlockAlign = 4, nAvgBytesPerSec = Rate * 4,
            };
            using var done = new AutoResetEvent(false);
            if (waveOutOpen(out var dev, WAVE_MAPPER, ref fmt, done.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT) != 0)
            { Log.Write("Ambient: no audio device"); return; }

            // Buffers and headers live in unmanaged memory for as long as the app runs.
            int size = Marshal.SizeOf<WaveHdr>(), bytes = BufferFrames * 4;
            var headers = new IntPtr[BufferCount];
            for (int b = 0; b < BufferCount; b++)
            {
                var hdr = new WaveHdr { lpData = Marshal.AllocHGlobal(bytes), dwBufferLength = (uint)bytes };
                headers[b] = Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(hdr, headers[b], false);
                waveOutPrepareHeader(dev, headers[b], size);
                // Marked "free" until first filled; keep the PREPARED flag Windows just set, or every write is refused.
                Marshal.WriteInt32(headers[b], FlagsOffset, Marshal.ReadInt32(headers[b], FlagsOffset) | (int)WHDR_DONE);
            }

            var mix = new short[BufferFrames * 2];
            bool paused = false;
            while (true)
            {
                // Nothing playing or wanted: pause the device (so it doesn't hold the audio endpoint
                // or keep the PC awake) and sleep until asked again.
                if (Voices.All(v => !v.Want && v.Volume <= 0))
                {
                    if (!paused) { waveOutPause(dev); paused = true; }
                    Wake.WaitOne();
                    continue;
                }
                if (paused) { waveOutRestart(dev); paused = false; }

                bool wrote = false;
                foreach (var h in headers)
                {
                    if ((Marshal.ReadInt32(h, FlagsOffset) & WHDR_DONE) == 0) continue;
                    Fill(mix);
                    Marshal.Copy(mix, 0, Marshal.ReadIntPtr(h), mix.Length);
                    Marshal.WriteInt32(h, FlagsOffset, Marshal.ReadInt32(h, FlagsOffset) & ~(int)WHDR_DONE);
                    int err = waveOutWrite(dev, h, size);
                    if (err == 0) _buffersWritten++;
                    if (err != 0 && !_loggedWriteError) { _loggedWriteError = true; Log.Write($"Ambient: waveOutWrite failed ({err})"); }
                    wrote = true;
                }
                if (!wrote) WaitHandle.WaitAny(new WaitHandle[] { done, Wake }, 50);
            }
        }
        catch (Exception ex) { Log.Write("Ambient mixer: " + ex.Message); }
    }

    static long _buffersWritten;

    /// <summary>For diagnostics: buffers played and each track's state.</summary>
    public static string Describe() =>
        $"{_buffersWritten} buffers played; " + string.Join(", ", Voices.Select((v, i) => $"{(Track)i}: loaded={v.Pcm != null} volume={v.Volume:F2}"));

    /// <summary>Mixes one buffer, moving each track's fade a little every sample (no zipper noise).</summary>
    static void Fill(short[] mix)
    {
        const float fadeIn = 1f / (1.5f * Rate), fadeOut = 1f / (0.8f * Rate);
        Array.Clear(mix);
        foreach (var v in Voices)
        {
            var pcm = v.Pcm;
            if (pcm == null) continue;
            float vol = v.Volume, target = v.Want ? 1 : 0;
            if (vol <= 0 && target <= 0) continue;
            int pos = v.Pos;
            for (int f = 0; f < BufferFrames; f++)
            {
                vol = target > vol ? Math.Min(target, vol + fadeIn) : Math.Max(target, vol - fadeOut);
                float g = vol * vol * MasterVolume;
                mix[f * 2] = (short)Math.Clamp(mix[f * 2] + pcm[pos] * g, short.MinValue, short.MaxValue);
                mix[f * 2 + 1] = (short)Math.Clamp(mix[f * 2 + 1] + pcm[pos + 1] * g, short.MinValue, short.MaxValue);
                pos += 2;
                if (pos >= pcm.Length) pos = 0;
            }
            v.Volume = vol;
            v.Pos = pos;
        }
    }

    /// <summary>Writes a loop to a WAV file (diagnostic: lets you listen without starting the console).</summary>
    public static void ExportWav(string path, Track track = Track.Home)
    {
        var pcm = track == Track.Selector ? SelectorComposer.Compose() : AmbientComposer.Compose();
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8.ToArray()); w.Write(36 + pcm.Length); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(pcm.Length); w.Write(pcm);
    }
}
