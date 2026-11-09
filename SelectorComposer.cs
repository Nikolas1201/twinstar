using static Twinstar.AmbientComposer;

namespace Twinstar;

/// <summary>
/// Original ambient loop for the mode selector, a calmer "waiting room" than the home screen: slower
/// (60 bpm), open suspended chords in D, a sparse crystal-bell melody drifting through a long stereo
/// echo, a soft breathing wind bed, and the same seamless-loop reverb as the home track.
/// </summary>
static class SelectorComposer
{
    const int Rate = 44100;
    const double Bpm = 60, Beat = 60 / Bpm, Bar = Beat * 4;
    const int Bars = 16, BarsPerChord = 2;

    // (bass, pad voicing) as MIDI notes. Dadd9, Bm9, Gmaj7#11, Asus2, played twice.
    static readonly (int bass, int[] pad)[] Chords =
    {
        (38, new[] { 50, 57, 62, 64, 66 }),
        (35, new[] { 50, 54, 59, 61, 64 }),
        (43, new[] { 50, 55, 59, 61, 66 }),
        (45, new[] { 52, 57, 59, 64, 69 }),
    };

    // D major pentatonic, two octaves up from the pads: the bells always sit nicely on every chord.
    static readonly int[] Scale = { 74, 76, 78, 81, 83, 86, 88, 90 };

    public static byte[] Compose()
    {
        int n = (int)(Bars * Bar * Rate);
        var dryL = new float[n]; var dryR = new float[n];
        var sendL = new float[n]; var sendR = new float[n];
        var rng = new Random(23);
        double chordLen = Bar * BarsPerChord;

        for (int c = 0; c < Bars / BarsPerChord; c++)
        {
            double t0 = c * chordLen;
            var (bass, pad) = Chords[c % Chords.Length];
            for (int k = 0; k < pad.Length; k++)
            {
                float pan = 0.15f + 0.7f * k / (pad.Length - 1);
                Pad(dryL, dryR, t0 - 2.0, chordLen + 5.5, Hz(pad[k]), 0.026, pan, rng);
            }
            Sub(dryL, dryR, t0 - 0.8, chordLen + 3.0, Hz(bass), 0.07);
        }

        // Crystal melody: a slow wandering line, mostly stepwise, with plenty of rests.
        int pos = 3;
        for (int bar = 0; bar < Bars; bar++)
            for (int beat = 0; beat < 4; beat++)
            {
                if (rng.NextDouble() < (beat % 2 == 0 ? 0.35 : 0.7)) continue;
                pos = Math.Clamp(pos + rng.Next(-2, 3), 0, Scale.Length - 1);
                double t = bar * Bar + beat * Beat + (rng.NextDouble() < 0.3 ? Beat / 2 : 0);
                float pan = 0.3f + (float)rng.NextDouble() * 0.4f;
                Crystal(sendL, sendR, t, Hz(Scale[pos]), 0.05 * (0.7 + rng.NextDouble() * 0.3), pan);
            }

        // Shimmer swells every four bars, a fifth above the top of the chord.
        for (int bar = 0; bar < Bars; bar += 4)
        {
            var pad = Chords[(bar / BarsPerChord) % Chords.Length].pad;
            Shimmer(dryL, dryR, bar * Bar + Beat * 2, Bar * 3.5, Hz(pad[^1] + 7), 0.018);
        }

        Wind(dryL, dryR, rng);

        // Long dotted echo on the bells, then the big reverb over everything.
        var (echoL, echoR) = PingPong(sendL, sendR, Beat * 1.5, 0.5, 0.4);
        var inL = new float[n]; var inR = new float[n];
        for (int i = 0; i < n; i++)
        {
            inL[i] = dryL[i] + sendL[i] + echoL[i];
            inR[i] = dryR[i] + sendR[i] + echoR[i];
        }
        var (wetL, wetR) = Reverb(inL, inR);

        var outL = new float[n]; var outR = new float[n];
        float peak = 1e-6f;
        for (int i = 0; i < n; i++)
        {
            outL[i] = inL[i] * 0.5f + wetL[i];
            outR[i] = inR[i] * 0.5f + wetR[i];
            peak = Math.Max(peak, Math.Max(Math.Abs(outL[i]), Math.Abs(outR[i])));
        }
        float gain = 0.55f / peak;
        var bytes = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            short l = (short)(Math.Clamp(outL[i] * gain, -1, 1) * short.MaxValue);
            short r = (short)(Math.Clamp(outR[i] * gain, -1, 1) * short.MaxValue);
            bytes[i * 4] = (byte)l; bytes[i * 4 + 1] = (byte)(l >> 8);
            bytes[i * 4 + 2] = (byte)r; bytes[i * 4 + 3] = (byte)(r >> 8);
        }
        return bytes;
    }

    /// <summary>Glassy bell: a pure tone plus inharmonic partials that die away quickly, like a celesta.</summary>
    static void Crystal(float[] l, float[] r, double start, double f, double vol, float pan)
    {
        int n = l.Length, s0 = (int)(start * Rate), count = (int)(4.0 * Rate);
        double w = 2 * Math.PI * f / Rate;
        for (int k = 0; k < count; k++)
        {
            double t = k / (double)Rate;
            double env = Math.Min(1, k / (0.002 * Rate)) * Math.Exp(-t * 1.3);
            double s = (Math.Sin(w * k)
                        + 0.35 * Math.Sin(w * k * 2.76) * Math.Exp(-t * 6)
                        + 0.15 * Math.Sin(w * k * 5.4) * Math.Exp(-t * 12)) * env * vol;
            int i = Wrap(s0 + k, n);
            l[i] += (float)(s * (1 - pan)); r[i] += (float)(s * pan);
        }
    }

    /// <summary>Soft wind: filtered noise whose level and brightness drift slowly, like breathing.</summary>
    static void Wind(float[] l, float[] r, Random rng)
    {
        int n = l.Length;
        double yl = 0, yr = 0, yl2 = 0, yr2 = 0;
        // Whole cycles over the loop so the swells line up at the loop point.
        double cycles = 4, wSlow = 2 * Math.PI * cycles / n;
        for (int i = 0; i < n; i++)
        {
            double lfo = 0.5 - 0.5 * Math.Cos(wSlow * i);
            double a = 1 - Math.Exp(-2 * Math.PI * (300 + 500 * lfo) / Rate);
            yl += a * (rng.NextDouble() * 2 - 1 - yl); yl2 += a * (yl - yl2);
            yr += a * (rng.NextDouble() * 2 - 1 - yr); yr2 += a * (yr - yr2);
            double vol = 0.03 + 0.05 * lfo;
            l[i] += (float)(yl2 * vol); r[i] += (float)(yr2 * vol);
        }
    }
}
