using System.Runtime.InteropServices;

namespace Twinstar;

enum Sfx { Move, Select, Back, Open, Launch, Bye, Boot }

/// <summary>
/// Soft console UI sounds, synthesized once at startup (no audio files) and played asynchronously.
/// </summary>
static class Sounds
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern bool PlaySound(IntPtr sound, IntPtr hmod, uint flags);
    const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_MEMORY = 0x4;

    public static bool Enabled = true;
    static readonly Dictionary<Sfx, GCHandle> Pinned = new();
    const int Rate = 44100;
    static readonly Dictionary<Sfx, byte[]> Clips = new()
    {
        // (frequency start -> end Hz, duration s, volume); short sine "glass" tones with a fast attack.
        [Sfx.Move] = Tone((1850, 1850, 0.035, 0.16)),
        [Sfx.Select] = Tone((880, 880, 0.06, 0.20), (1320, 1320, 0.10, 0.20)),
        [Sfx.Back] = Tone((1100, 700, 0.09, 0.18)),
        [Sfx.Open] = Tone((520, 980, 0.16, 0.16)),
        [Sfx.Launch] = Tone((440, 440, 0.09, 0.18), (660, 660, 0.09, 0.18), (880, 1320, 0.28, 0.20)),
        // "Bye-bye!": a bright note, then a lower one that sags a little at the end like a waved
        // goodbye, with a faint octave sparkle and a soft echo tail.
        [Sfx.Bye] = Chime(1.3,
            (0.00, 1046.5, 1046.5, 0.30, 0.17),
            (0.00, 2093.0, 2093.0, 0.18, 0.04),
            (0.16, 784.0, 740.0, 0.75, 0.19),
            (0.16, 1568.0, 1480.0, 0.40, 0.035)),
        [Sfx.Boot] = BootSwell(),
    };

    public static void Play(Sfx s)
    {
        if (!Enabled) return;
        // Async playback reads the buffer after this call returns, so clips stay pinned for good.
        if (!Pinned.TryGetValue(s, out var h)) Pinned[s] = h = GCHandle.Alloc(Clips[s], GCHandleType.Pinned);
        try { PlaySound(h.AddrOfPinnedObject(), IntPtr.Zero, SND_ASYNC | SND_MEMORY | SND_NODEFAULT); } catch { }
    }

    /// <summary>Writes a clip to a WAV file (for previewing).</summary>
    public static void Export(Sfx s, string path) => File.WriteAllBytes(path, Clips[s]);

    /// <summary>Overlapping bell notes (start s, f0 -> f1 Hz, ring s, volume) with a soft echo.</summary>
    static byte[] Chime(double total, params (double at, double f0, double f1, double dur, double vol)[] notes)
    {
        var mix = new double[(int)(total * Rate)];
        foreach (var (at, f0, f1, dur, vol) in notes)
        {
            int start = (int)(at * Rate), n = (int)(dur * Rate);
            double phase = 0;
            for (int i = 0; i < n && start + i < mix.Length; i++)
            {
                double t = i / (double)n;
                // The glide happens late in the note, so it starts on pitch and droops as it fades.
                double f = f0 + (f1 - f0) * t * t;
                phase += 2 * Math.PI * f / Rate;
                double env = Math.Min(1, i / (0.006 * Rate)) * Math.Exp(-5.0 * t) * (1 - t);
                mix[start + i] += (Math.Sin(phase) + 0.18 * Math.Sin(phase * 3)) * env * vol;
            }
        }
        int delay = (int)(0.13 * Rate);
        for (int i = delay; i < mix.Length; i++) mix[i] += mix[i - delay] * 0.28;
        return Wav(mix.Select(v => (short)(Math.Clamp(v, -1, 1) * short.MaxValue)).ToList());
    }

    /// <summary>
    /// Boot, timed to the intro: two tones, one climbing and one falling, close in on each other over a
    /// building chord and whoosh (the two streams of light), and meet on the same note at 3.2 s, the
    /// ignition, with a deep thump and a bright two-note bell. Then everything rings out together.
    /// </summary>
    static byte[] BootSwell()
    {
        const double total = 6.0, land = 3.2;
        var mix = new double[(int)(total * Rate)];
        // Pad: Cmaj9 voicing, each note two slightly detuned sines for a soft chorus.
        double[] pad = { 130.81, 196.00, 329.63, 493.88, 587.33 };
        foreach (var f in pad)
            foreach (var detune in new[] { 0.997, 1.003 })
            {
                double phase = 0, vol = f < 200 ? 0.055 : 0.04;
                for (int i = 0; i < mix.Length; i++)
                {
                    double t = i / (double)Rate;
                    phase += 2 * Math.PI * f * detune / Rate;
                    double env = t < land ? Math.Pow(t / land, 2.2) : Math.Exp(-(t - land) * 1.3);
                    mix[i] += Math.Sin(phase) * env * vol;
                }
            }
        // Whoosh: noise through a filter that opens as the logo approaches, cut off at the landing.
        var rng = new Random(5);
        double y1 = 0, y2 = 0;
        for (int i = 0; i < (int)((land + 0.25) * Rate); i++)
        {
            double t = i / (double)Rate, rise = Math.Min(1, t / land);
            double a = 1 - Math.Exp(-2 * Math.PI * (200 + 3800 * rise * rise) / Rate);
            y1 += a * (rng.NextDouble() * 2 - 1 - y1); y2 += a * (y1 - y2);
            double env = t < land ? Math.Pow(rise, 3) : Math.Max(0, 1 - (t - land) / 0.25);
            mix[i] += y2 * env * 0.28;
        }
        // The two streams: gliding towards each other until they meet on the landing note.
        foreach (var (from, to, side) in new[] { (261.63, 1046.5, 1.0), (2093.0, 1046.5, -1.0) })
        {
            double phase = 0;
            int n = (int)(land * Rate);
            for (int i = 0; i < n; i++)
            {
                double p = i / (double)n, f = from * Math.Pow(to / from, p * p);
                phase += 2 * Math.PI * f / Rate;
                double wobble = 1 + 0.25 * Math.Sin(p * 34 * side);
                mix[i] += Math.Sin(phase) * Math.Pow(p, 1.6) * wobble * 0.05;
            }
        }
        // Landing thump: a sine that drops quickly in pitch.
        {
            int start = (int)(land * Rate), n = (int)(0.7 * Rate);
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                phase += 2 * Math.PI * (48 + 70 * Math.Exp(-t * 28)) / Rate;
                mix[start + i] += Math.Sin(phase) * Math.Min(1, i / (0.002 * Rate)) * Math.Exp(-t * 6.5) * 0.3;
            }
        }
        // Bell chime on the landing: a rising fifth, with an airy octave.
        void Bell(double at, double f, double dur, double vol)
        {
            int start = (int)(at * Rate), n = (int)(dur * Rate);
            double phase = 0;
            for (int i = 0; i < n && start + i < mix.Length; i++)
            {
                double t = i / (double)n;
                phase += 2 * Math.PI * f / Rate;
                double env = Math.Min(1, i / (0.005 * Rate)) * Math.Exp(-4.0 * t) * (1 - t);
                mix[start + i] += (Math.Sin(phase) + 0.15 * Math.Sin(phase * 3)) * env * vol;
            }
        }
        Bell(land, 1046.5, 1.4, 0.13);
        Bell(land + 0.14, 1568.0, 1.3, 0.11);
        Bell(land + 0.14, 3136.0, 0.6, 0.025);
        // Two echo taps to give it space.
        foreach (var (d, g) in new[] { (0.17, 0.25), (0.31, 0.14) })
        {
            int delay = (int)(d * Rate);
            for (int i = mix.Length - 1; i >= delay; i--) mix[i] += mix[i - delay] * g;
        }
        // Gentle fade at the very end so it never clicks.
        int tail = (int)(0.3 * Rate);
        for (int i = 0; i < tail; i++) mix[mix.Length - 1 - i] *= i / (double)tail;
        return Wav(mix.Select(v => (short)(Math.Clamp(v, -1, 1) * short.MaxValue)).ToList());
    }

    /// <summary>Builds a 16-bit mono WAV from consecutive sweeps.</summary>
    static byte[] Tone(params (double f0, double f1, double dur, double vol)[] parts)
    {
        var samples = new List<short>();
        foreach (var (f0, f1, dur, vol) in parts)
        {
            int n = (int)(dur * Rate);
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)n;
                double f = f0 + (f1 - f0) * t;
                phase += 2 * Math.PI * f / Rate;
                // 4 ms attack, exponential decay: a soft "tink" instead of a click.
                double env = Math.Min(1, i / (0.004 * Rate)) * Math.Exp(-4.5 * t);
                double s = (Math.Sin(phase) + 0.25 * Math.Sin(phase * 2)) * env * vol;
                samples.Add((short)(s * short.MaxValue));
            }
        }
        return Wav(samples);
    }

    static byte[] Wav(List<short> samples)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataLen = samples.Count * 2;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + dataLen); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(dataLen);
        foreach (var s in samples) w.Write(s);
        return ms.ToArray();
    }
}
