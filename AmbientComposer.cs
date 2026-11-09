namespace Twinstar;

/// <summary>
/// Original ambient loop in the style of console home screens: slow (72 bpm), major-key chords on warm
/// filtered "supersaw" pads, a gentle electric-piano arpeggio bouncing through a stereo echo, a soft
/// sub bass, high shimmer swells, and everything bathed in a large reverb. Rendered with wrap-around
/// and with the effects run over the loop twice, so the tails flow across the loop point seamlessly.
/// </summary>
static class AmbientComposer
{
    const int Rate = 44100;
    const double Bpm = 72, Beat = 60 / Bpm, Bar = Beat * 4;
    const int Bars = 16, BarsPerChord = 2;

    // (bass, pad voicing) as MIDI notes. Fmaj9, Dm9, Bbmaj9, C(add9), Am(add9), Dm9, Gm9, Bb/C.
    static readonly (int bass, int[] pad)[] Chords =
    {
        (41, new[] { 53, 57, 60, 64, 67 }),
        (38, new[] { 50, 53, 57, 60, 64 }),
        (46, new[] { 50, 53, 57, 60, 62 }),
        (36, new[] { 52, 55, 60, 62, 67 }),
        (45, new[] { 48, 52, 55, 59, 64 }),
        (38, new[] { 50, 53, 57, 60, 64 }),
        (43, new[] { 50, 53, 57, 58, 62 }),
        (36, new[] { 50, 53, 58, 62, 65 }),
    };

    public static int LengthSamples => (int)(Bars * Bar * Rate);

    public static byte[] Compose()
    {
        int n = LengthSamples;
        var dryL = new float[n]; var dryR = new float[n];   // straight to the reverb and the mix
        var sendL = new float[n]; var sendR = new float[n]; // also fed through the echo
        var rng = new Random(11);
        double chordLen = Bar * BarsPerChord;

        for (int c = 0; c < Chords.Length; c++)
        {
            double t0 = c * chordLen;
            var (bass, pad) = Chords[c];
            for (int k = 0; k < pad.Length; k++)
            {
                float pan = 0.2f + 0.6f * k / (pad.Length - 1);
                Pad(dryL, dryR, t0 - 1.5, chordLen + 4.5, Hz(pad[k]), 0.030, pan, rng);
            }
            Sub(dryL, dryR, t0 - 0.5, chordLen + 2.5, Hz(bass), 0.09);
        }

        // Electric-piano arpeggio: eighth notes over chord tones an octave up, with rests and gentle accents.
        int[][] patterns = { new[] { 0, 2, 4, 2, 3, 4, 1, -1 }, new[] { 4, 3, 2, -1, 1, 2, 4, 3 }, new[] { 0, -1, 2, 4, -1, 3, 2, 1 } };
        for (int bar = 0; bar < Bars; bar++)
        {
            var pad = Chords[bar / BarsPerChord].pad;
            var pattern = patterns[(bar * 7 + 3) % patterns.Length];
            for (int step = 0; step < 8; step++)
            {
                int idx = pattern[step];
                if (idx < 0 || rng.NextDouble() < 0.12) continue; // breathing space
                double t = bar * Bar + step * Beat / 2 + (rng.NextDouble() - 0.5) * 0.012; // a touch of human timing
                double vel = (step % 2 == 0 ? 0.9 : 0.65) * (0.8 + rng.NextDouble() * 0.2);
                float pan = step % 2 == 0 ? 0.38f : 0.62f;
                ElectricPiano(sendL, sendR, t, Hz(pad[idx] + 12), 0.075 * vel, pan);
            }
        }

        // Shimmer: slow high swells every four bars, mostly heard through the reverb.
        for (int bar = 0; bar < Bars; bar += 4)
        {
            var pad = Chords[bar / BarsPerChord].pad;
            Shimmer(dryL, dryR, bar * Bar + Beat, Bar * 3, Hz(pad[^1] + 12), 0.022);
        }

        // Echo on the arpeggio, then a big reverb over everything.
        var (echoL, echoR) = PingPong(sendL, sendR, Beat * 0.75, 0.45, 0.35);
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
            outL[i] = inL[i] * 0.55f + wetL[i] * 0.9f;
            outR[i] = inR[i] * 0.55f + wetR[i] * 0.9f;
            peak = Math.Max(peak, Math.Max(Math.Abs(outL[i]), Math.Abs(outR[i])));
        }
        float gain = 0.6f / peak;
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

    internal static double Hz(int midi) => 440 * Math.Pow(2, (midi - 69) / 12.0);

    internal static int Wrap(int i, int n) => ((i % n) + n) % n;

    internal static double Swell(int k, int count, double attack, double release)
    {
        double a = Math.Min(1, k / (attack * Rate)), r = Math.Min(1, (count - k) / (release * Rate));
        double e = Math.Min(a, r);
        return 0.5 - 0.5 * Math.Cos(Math.PI * e);
    }

    /// <summary>Warm pad note: two detuned saws through a two-pole low-pass whose cutoff slowly breathes.</summary>
    internal static void Pad(float[] l, float[] r, double start, double len, double f, double vol, float pan, Random rng)
    {
        int n = l.Length, s0 = (int)(start * Rate), count = (int)(len * Rate);
        double f1 = f * Math.Pow(2, 7 / 1200.0), f2 = f * Math.Pow(2, -7 / 1200.0);
        double p1 = rng.NextDouble(), p2 = rng.NextDouble(), y1 = 0, y2 = 0, a = 0;
        double lfoPhase = rng.NextDouble() * Math.PI * 2;
        for (int k = 0; k < count; k++)
        {
            if ((k & 31) == 0)
            {
                double cutoff = 700 + 450 * Math.Sin(lfoPhase + 2 * Math.PI * 0.05 * k / Rate) + f * 1.5;
                a = 1 - Math.Exp(-2 * Math.PI * cutoff / Rate);
            }
            p1 += f1 / Rate; p2 += f2 / Rate;
            double saw = 2 * (p1 - Math.Floor(p1 + 0.5)) + 2 * (p2 - Math.Floor(p2 + 0.5));
            y1 += a * (saw - y1); y2 += a * (y1 - y2);
            double s = y2 * Swell(k, count, 2.2, 3.2) * vol;
            int i = Wrap(s0 + k, n);
            l[i] += (float)(s * (1 - pan)); r[i] += (float)(s * pan);
        }
    }

    internal static void Sub(float[] l, float[] r, double start, double len, double f, double vol)
    {
        int n = l.Length, s0 = (int)(start * Rate), count = (int)(len * Rate);
        double w = 2 * Math.PI * f / Rate;
        for (int k = 0; k < count; k++)
        {
            double s = Math.Sin(w * k) * Swell(k, count, 1.0, 2.0) * vol;
            int i = Wrap(s0 + k, n);
            l[i] += (float)s; r[i] += (float)s;
        }
    }

    /// <summary>Soft electric piano (simple FM): bright attack that mellows as the modulation decays.</summary>
    static void ElectricPiano(float[] l, float[] r, double start, double f, double vol, float pan)
    {
        int n = l.Length, s0 = (int)(start * Rate), count = (int)(3.2 * Rate);
        double w = 2 * Math.PI * f / Rate;
        for (int k = 0; k < count; k++)
        {
            double t = k / (double)Rate;
            double index = 1.8 * Math.Exp(-t * 7);
            double env = Math.Min(1, k / (0.003 * Rate)) * Math.Exp(-t * 1.7);
            double s = (Math.Sin(w * k + index * Math.Sin(w * k)) + 0.12 * Math.Sin(2 * w * k) * Math.Exp(-t * 4)) * env * vol;
            int i = Wrap(s0 + k, n);
            l[i] += (float)(s * (1 - pan)); r[i] += (float)(s * pan);
        }
    }

    internal static void Shimmer(float[] l, float[] r, double start, double len, double f, double vol)
    {
        int n = l.Length, s0 = (int)(start * Rate), count = (int)(len * Rate);
        double w = 2 * Math.PI * f / Rate;
        for (int k = 0; k < count; k++)
        {
            double env = Swell(k, count, len * 0.45, len * 0.5);
            double s = (Math.Sin(w * k) + 0.5 * Math.Sin(1.5 * w * k)) * env * vol;
            int i = Wrap(s0 + k, n);
            l[i] += (float)(s * 0.4); r[i] += (float)(s * 0.6);
        }
    }

    /// <summary>
    /// Effects run over the loop twice and keep the second pass, so echoes and reverb tails that ring
    /// past the end are already present at the start: the loop point is inaudible.
    /// </summary>
    internal static (float[], float[]) PingPong(float[] inL, float[] inR, double time, double feedback, double mix)
    {
        int n = inL.Length, d = (int)(time * Rate);
        var bufL = new float[d]; var bufR = new float[d];
        var outL = new float[n]; var outR = new float[n];
        int pos = 0;
        double lpL = 0, lpR = 0;
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < n; i++)
            {
                float dl = bufL[pos], dr = bufR[pos];
                lpL += 0.35 * (dl - lpL); lpR += 0.35 * (dr - lpR); // each repeat a little darker
                // Cross-feed: left repeats come back on the right and vice versa.
                bufL[pos] = (float)(inL[i] + lpR * feedback);
                bufR[pos] = (float)(inR[i] * 0.3 + lpL * feedback);
                pos = (pos + 1) % d;
                if (pass == 1) { outL[i] = (float)(dl * mix); outR[i] = (float)(dr * mix); }
            }
        return (outL, outR);
    }

    /// <summary>Freeverb-style hall: eight damped combs and four all-passes per channel, slightly offset for width.</summary>
    internal static (float[], float[]) Reverb(float[] inL, float[] inR)
    {
        int[] combs = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 }, allpasses = { 556, 441, 341, 225 };
        float[] Channel(int spread)
        {
            int n = inL.Length;
            var cb = combs.Select(c => new float[c + spread]).ToArray();
            var cs = new float[combs.Length];
            var cp = new int[combs.Length];
            var ab = allpasses.Select(a => new float[a + spread]).ToArray();
            var ap = new int[allpasses.Length];
            var output = new float[n];
            const float feedback = 0.88f, damp = 0.25f, inputGain = 0.012f;
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < n; i++)
                {
                    float x = (inL[i] + inR[i]) * inputGain, sum = 0;
                    for (int c = 0; c < cb.Length; c++)
                    {
                        float y = cb[c][cp[c]];
                        cs[c] = y * (1 - damp) + cs[c] * damp;
                        cb[c][cp[c]] = x + cs[c] * feedback;
                        cp[c] = (cp[c] + 1) % cb[c].Length;
                        sum += y;
                    }
                    for (int a = 0; a < ab.Length; a++)
                    {
                        float b = ab[a][ap[a]];
                        ab[a][ap[a]] = sum + b * 0.5f;
                        sum = b - sum;
                        ap[a] = (ap[a] + 1) % ab[a].Length;
                    }
                    if (pass == 1) output[i] = sum;
                }
            return output;
        }
        var left = Task.Run(() => Channel(0));
        var right = Task.Run(() => Channel(23));
        return (left.Result, right.Result);
    }
}
