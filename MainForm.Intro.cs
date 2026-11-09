using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Twinstar;

/// <summary>
/// Boot intro, "two worlds become one": out of the dark, two streams of light (console and desktop)
/// enter from opposite sides, spiralling around each other and shedding thousands of sparks. The sparks
/// weave together into a 3D "T" of pure light, which ignites in a flash and leaves the solid logo
/// against a nebula. Light is summed per pixel in a plain int buffer, so it really glows.
/// </summary>
sealed partial class MainForm
{
    double _introAt = -1e9;
    bool _introShown;
    const double IntroMs = 5000;
    const float IgniteMs = 3200, LogoDistance = 6, LogoLift = 0.42f, ExtrudeHalf = 0.34f;

    bool IntroPlaying => NowMs - _introAt < IntroMs;

    // The T's outline, clockwise, in logo units (2 tall).
    static readonly (float x, float y)[] LogoOutline =
    {
        (-.98f, 1), (.98f, 1), (.98f, .46f), (.3f, .46f), (.3f, -1), (-.3f, -1), (-.3f, .46f), (-.98f, .46f),
    };
    static bool InsideLogo(float x, float y) => y > .46f || MathF.Abs(x) < .3f;

    struct Spark
    {
        public float Side, Birth, Jitter, Radius, GatherAt, Twinkle;
        public V3 Target, Burst;
    }

    static readonly Spark[] IntroSparks = MakeSparks();
    static readonly (float x, float y, float phase)[] IntroStars = MakeStars();

    static Spark[] MakeSparks()
    {
        var rng = new Random(11);
        float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
        var sparks = new Spark[1400];
        var edges = LogoOutline.Select((a, i) =>
        {
            var b = LogoOutline[(i + 1) % LogoOutline.Length];
            return MathF.Sqrt((b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y));
        }).ToArray();
        float perimeter = edges.Sum();
        for (int i = 0; i < sparks.Length; i++)
        {
            // Most land on the T's edges, so it's legible as a shape made only of points.
            float x, y;
            if (i % 20 < 12)
            {
                // Spaced evenly along the whole outline, however long each edge is.
                float along = R(0, perimeter);
                int e = 0;
                while (e < edges.Length - 1 && along > edges[e]) along -= edges[e++];
                var (ax, ay) = LogoOutline[e];
                var (bx, by) = LogoOutline[(e + 1) % LogoOutline.Length];
                float p = Math.Min(1, along / edges[e]);
                x = ax + (bx - ax) * p; y = ay + (by - ay) * p;
            }
            else
                do { x = R(-.98f, .98f); y = R(-1, 1); } while (!InsideLogo(x, y));
            float z = i % 3 == 0 ? R(-ExtrudeHalf, ExtrudeHalf) : (i % 2 == 0 ? -ExtrudeHalf : ExtrudeHalf);
            sparks[i] = new Spark
            {
                Side = i % 2 == 0 ? -1 : 1,
                Birth = R(150, 2000),
                Jitter = R(-.5f, .5f),
                Radius = R(.7f, 1.3f),
                GatherAt = R(1650, 2100),
                Twinkle = R(0, MathF.Tau),
                Target = new V3(x, y, z),
                Burst = new V3(x + R(-.6f, .6f), y + R(-.6f, .6f), R(-1.2f, 1.2f)),
            };
        }
        return sparks;
    }

    static (float, float, float)[] MakeStars()
    {
        var rng = new Random(3);
        var stars = new (float, float, float)[220];
        for (int i = 0; i < stars.Length; i++) stars[i] = ((float)rng.NextDouble(), (float)rng.NextDouble(), (float)(rng.NextDouble() * MathF.Tau));
        return stars;
    }

    readonly struct V3(float x, float y, float z)
    {
        public readonly float X = x, Y = y, Z = z;
        public static float Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    }

    static V3 Turn(V3 v, float yaw, float pitch)
    {
        float cy = MathF.Cos(yaw), sy = MathF.Sin(yaw), cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);
        float x = v.X * cy + v.Z * sy, z = -v.X * sy + v.Z * cy;
        return new V3(x, v.Y * cp - z * sp, v.Y * sp + z * cp);
    }

    static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)Lerp(a.R, b.R, t), (int)Lerp(a.G, b.G, t), (int)Lerp(a.B, b.B, t));

    /// <summary>The second stream's colour, chosen to sit well against the theme's accent.</summary>
    Color IntroPartner => CurrentSelTheme.Key switch
    {
        "ember" => Color.FromArgb(236, 72, 153),
        "aurora" => Color.FromArgb(167, 139, 250),
        _ => Color.FromArgb(167, 139, 250),
    };

    // ---- Pixel buffer: capped at 1080 lines so a 4K screen costs the same ----
    int[]? _iPixels, _iNebula, _iFlash;
    GCHandle _iHandle;
    Bitmap? _iBitmap;
    int _iW, _iH;
    int[][]? _iKernels;

    void EnsureIntroBuffers()
    {
        if (_iBitmap != null) return;
        float scale = Math.Min(1, 1080f / ClientSize.Height);
        _iW = (int)(ClientSize.Width * scale); _iH = (int)(ClientSize.Height * scale);
        _iPixels = new int[_iW * _iH];
        _iHandle = GCHandle.Alloc(_iPixels, GCHandleType.Pinned);
        _iBitmap = new Bitmap(_iW, _iH, _iW * 4, PixelFormat.Format32bppRgb, _iHandle.AddrOfPinnedObject());
        var accent = CurrentSelTheme.Accent;
        _iNebula = BakeNebula(accent, IntroPartner);
        _iFlash = BakeFlash(accent);
        float s = _iH / 1080f;
        _iKernels = new[] { Kernel((int)Math.Max(1, 3 * s)), Kernel((int)Math.Max(2, 5 * s)), Kernel((int)Math.Max(3, 8 * s)), Kernel((int)(72 * s)), Kernel((int)Math.Max(1, 2 * s)) };
    }

    void ReleaseIntroBuffers()
    {
        if (_iBitmap == null) return;
        _iBitmap.Dispose(); _iBitmap = null;
        _iHandle.Free();
        _iPixels = _iNebula = _iFlash = null;
        _iKernels = null;
    }

    static int[] Kernel(int r)
    {
        int n = r * 2 + 1;
        var k = new int[n * n];
        for (int y = -r; y <= r; y++)
            for (int x = -r; x <= r; x++)
            {
                float d = (x * x + y * y) / (float)(r * r);
                k[(y + r) * n + x + r] = d >= 1 ? 0 : (int)(256 * MathF.Pow(1 - d, 3));
            }
        return k;
    }

    /// <summary>Adds light to the buffer: colours sum and saturate, exactly how light behaves.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)] // plays once at boot: no time for the JIT to warm up
    void AddLight(int cx, int cy, int[] kernel, int cr, int cg, int cb)
    {
        var px = _iPixels!;
        int n = (int)MathF.Sqrt(kernel.Length), r = n / 2;
        if (cx + r < 0 || cy + r < 0 || cx - r >= _iW || cy - r >= _iH) return;
        for (int y = Math.Max(-r, -cy); y <= r && cy + y < _iH; y++)
            for (int x = Math.Max(-r, -cx); x <= r && cx + x < _iW; x++)
            {
                int v = kernel[(y + r) * n + x + r];
                if (v == 0) continue;
                int i = (cy + y) * _iW + cx + x, c = px[i];
                int R = ((c >> 16) & 255) + (cr * v >> 8), G = ((c >> 8) & 255) + (cg * v >> 8), B = (c & 255) + (cb * v >> 8);
                px[i] = (R > 255 ? 255 : R) << 16 | (G > 255 ? 255 : G) << 8 | (B > 255 ? 255 : B);
            }
    }

    /// <summary>Soft clouds in the two stream colours, from layered noise at low resolution, upscaled.</summary>
    int[] BakeNebula(Color a, Color b)
    {
        const int lw = 320, lh = 180;
        var rng = new Random(29);
        float[] Fbm(int seed)
        {
            var r = new Random(seed);
            var field = new float[lw * lh];
            float amp = 1;
            for (int cell = 64; cell >= 8; cell /= 2)
            {
                int gw = lw / cell + 2, gh = lh / cell + 2;
                var grid = new float[gw * gh];
                for (int i = 0; i < grid.Length; i++) grid[i] = (float)r.NextDouble();
                for (int y = 0; y < lh; y++)
                    for (int x = 0; x < lw; x++)
                    {
                        float fx = x / (float)cell, fy = y / (float)cell;
                        int ix = (int)fx, iy = (int)fy;
                        float tx = fx - ix, ty = fy - iy;
                        tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
                        float top = Lerp(grid[iy * gw + ix], grid[iy * gw + ix + 1], tx);
                        float bot = Lerp(grid[(iy + 1) * gw + ix], grid[(iy + 1) * gw + ix + 1], tx);
                        field[y * lw + x] += Lerp(top, bot, ty) * amp;
                    }
                amp *= 0.55f;
            }
            return field;
        }
        var n1 = Task.Run(() => Fbm(41)); var n2 = Task.Run(() => Fbm(97));
        var f1 = n1.Result; var f2 = n2.Result;
        var pixels = new int[_iW * _iH];
        for (int y = 0; y < _iH; y++)
        {
            float ly = y * (lh - 1f) / _iH;
            int y0 = (int)ly; float ty = ly - y0;
            for (int x = 0; x < _iW; x++)
            {
                float lx = x * (lw - 1f) / _iW;
                int x0 = (int)lx; float tx = lx - x0;
                int at = y0 * lw + x0;
                float S(float[] f) => Lerp(Lerp(f[at], f[at + 1], tx), Lerp(f[at + lw], f[at + lw + 1], tx), ty);
                // Clouds lean to opposite sides: one world on the left, the other on the right.
                float side = x / (float)_iW;
                float va = MathF.Pow(Clamp01((S(f1) - 0.95f) * 1.3f), 2) * (1.2f - side);
                float vb = MathF.Pow(Clamp01((S(f2) - 0.95f) * 1.3f), 2) * (0.2f + side);
                float dither = (float)rng.NextDouble() * 1.5f; // hides banding in the dark gradients
                pixels[y * _iW + x] = Math.Min(255, (int)(a.R * va * 0.33f + b.R * vb * 0.33f + 4 + dither)) << 16
                                    | Math.Min(255, (int)(a.G * va * 0.33f + b.G * vb * 0.33f + 5 + dither)) << 8
                                    | Math.Min(255, (int)(a.B * va * 0.33f + b.B * vb * 0.33f + 12 + dither));
            }
        }
        return pixels;
    }

    /// <summary>The ignition's light, once: a white core plus a long lens streak in the accent colour.</summary>
    int[] BakeFlash(Color accent)
    {
        var map = new int[_iW * _iH];
        float cx = _iW / 2f, cy = _iH * 0.46f - LogoLift * _iH * 0.83f / LogoDistance;
        float h = _iH, w = _iW;
        var streak = Mix(accent, Color.White, 0.35f);
        Parallel.For(0, _iH, y =>
        {
            float dy = y - cy;
            for (int x = 0; x < _iW; x++)
            {
                float dx = x - cx, d2 = dx * dx + dy * dy;
                float core = MathF.Exp(-d2 / (2 * 0.15f * h * 0.15f * h));
                float halo = 0.4f * MathF.Exp(-d2 / (2 * 0.36f * h * 0.36f * h));
                float ax = MathF.Abs(dx);
                float s = MathF.Exp(-dy * dy / (2 * 0.007f * h * 0.007f * h)) * MathF.Exp(-ax / (0.42f * w))
                        + 0.3f * MathF.Exp(-dy * dy / (2 * 0.04f * h * 0.04f * h)) * MathF.Exp(-ax / (0.22f * w));
                int r = (int)(255 * core + streak.R * (s + halo)), g = (int)(255 * core + streak.G * (s + halo)), b = (int)(255 * core + streak.B * (s + halo));
                if (r + g + b > 1) map[y * _iW + x] = Math.Min(255, r) << 16 | Math.Min(255, g) << 8 | Math.Min(255, b);
            }
        });
        return map;
    }

    /// <summary>Paints the whole intro at its current moment (over everything, until it ends).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    void PaintIntro(Graphics g, double now)
    {
        double td = now - _introAt;
        if (td < 0 || td >= IntroMs) { ReleaseIntroBuffers(); return; }
        EnsureIntroBuffers();
        float t = (float)td, since = t - IgniteMs;
        int w = _iW, h = _iH;
        var px = _iPixels!;
        var accent = CurrentSelTheme.Accent;
        var partner = IntroPartner;
        float focal = h * 0.83f, cx = w / 2f, cy = h * 0.46f;
        PointF P(float x, float y, float z) => new(cx + x * focal / z, cy - y * focal / z);

        // Backdrop: black until ignition, when the nebula is revealed by the light.
        float neb = since > 0 ? EaseOutCubic(since / 900) : 0;
        if (neb <= 0) Array.Clear(px);
        else if (neb >= 1) Array.Copy(_iNebula!, px, px.Length);
        else
        {
            int k = (int)(neb * 256);
            var src = _iNebula!;
            for (int i = 0; i < px.Length; i++)
            {
                int c = src[i];
                px[i] = (((c >> 16) & 255) * k >> 8) << 16 | (((c >> 8) & 255) * k >> 8) << 8 | ((c & 255) * k >> 8);
            }
        }

        // Faint stars, there from the first moment, so the dark has depth.
        float starIn = Clamp01(t / 900);
        foreach (var (sx, sy, phase) in IntroStars)
        {
            int v = (int)((70 + 60 * MathF.Sin(t * 0.0022f + phase)) * starIn);
            if (v > 2) AddLight((int)(sx * w), (int)(sy * h), _iKernels![4], v, v, Math.Min(255, v + 18));
        }

        // The logo's slow turn; the sparks' destinations turn with it, so they land on the solid shape.
        float arrive = EaseOutCubic(t / IgniteMs);
        float hold = since > 0 ? EaseInOutCubic(since / (float)(IntroMs - IgniteMs)) : 0;
        float yaw = -0.5f - 1.25f * (1 - arrive) + hold * 0.3f;
        const float pitch = 0.12f;
        float punch = since > 0 ? MathF.Sin(MathF.PI * Clamp01(since / 420)) * 0.5f : 0;

        // Solid logo, drawn into the buffer so the flash and the sparks shine over it.
        if (since > -30)
            using (var bg = Graphics.FromImage(_iBitmap!))
            {
                bg.SmoothingMode = SmoothingMode.AntiAlias;
                DrawLogo(bg, P, yaw, pitch, LogoDistance - punch, accent, out var face);
                if (face != null)
                    using (face)
                        if (since is > 450 and < 1250)
                        {
                            float sx = -1.9f + 3.8f * EaseInOutCubic((since - 450) / 800);
                            PointF Q(float ox, float oy)
                            {
                                var v = Turn(new V3(ox, oy, -ExtrudeHalf), yaw, pitch);
                                return P(v.X, v.Y + LogoLift, v.Z + LogoDistance - punch);
                            }
                            bg.SetClip(face);
                            using var white = new SolidBrush(Color.FromArgb(190, 255, 255, 255));
                            bg.FillPolygon(white, new[] { Q(sx - 0.1f, 1.1f), Q(sx + 0.26f, 1.1f), Q(sx - 0.1f, -1.1f), Q(sx - 0.46f, -1.1f) });
                        }
            }

        // ---- The two streams of sparks ----
        var kernels = _iKernels!;
        static (float x, float y, float z) Head(float tm, float side)
        {
            float p = Clamp01(tm / IgniteMs), e = p * p * (3 - 2 * p);
            float a = tm * 0.0045f + (side > 0 ? MathF.PI : 0), r = 2.2f * (1 - 0.75f * e);
            return (side * 6.1f * (1 - e), LogoLift + MathF.Cos(a) * r, LogoDistance + MathF.Sin(a) * r);
        }
        foreach (ref readonly var s in IntroSparks.AsSpan())
        {
            if (t < s.Birth) continue;
            float bright = Clamp01((t - s.Birth) / 220), x, y, z;
            var settled = Turn(s.Target, yaw, pitch);
            float tx = settled.X, ty = settled.Y + LogoLift, tz = settled.Z + LogoDistance;
            float g2 = EaseInOutCubic((t - s.GatherAt) / 900);
            if (since > 0)
            {
                // Ignition throws them outward as embers.
                float b = EaseOutCubic(since / 1500);
                var dir = Turn(s.Burst, yaw, pitch);
                x = tx + dir.X * 4.2f * b; y = ty + dir.Y * 4.2f * b; z = Math.Max(1.2f, tz + dir.Z * 2.5f * b);
                bright *= 1 - Clamp01(since / 1500);
            }
            else
            {
                float age = t - s.Birth;
                var (hx, hy, hz) = Head(s.Birth, s.Side);
                float a = MathF.Atan2(hz - LogoDistance, hy - LogoLift) + s.Jitter + age * 0.0019f * -s.Side;
                float r = MathF.Sqrt((hy - LogoLift) * (hy - LogoLift) + (hz - LogoDistance) * (hz - LogoDistance)) * s.Radius;
                float fx = hx * (1 - 0.38f * EaseInOutCubic(age / 2400)) + s.Jitter * 0.5f;
                float fy = LogoLift + MathF.Cos(a) * r, fz = LogoDistance + MathF.Sin(a) * r;
                x = Lerp(fx, tx, g2); y = Lerp(fy, ty, g2); z = Lerp(fz, tz, g2);
            }
            if (bright <= 0.01f) continue;
            var pos = P(x, y, z);
            bright *= (0.72f + 0.28f * MathF.Sin(t * 0.012f + s.Twinkle)) * Math.Min(1.4f, 7f / z) * (1.25f + g2 * 0.5f);
            var c = Mix(s.Side < 0 ? accent : partner, Color.White, 0.08f + g2 * 0.55f);
            AddLight((int)pos.X, (int)pos.Y, kernels[z < 4.8f ? 2 : z < 7 ? 1 : 0], (int)(c.R * bright), (int)(c.G * bright), (int)(c.B * bright));
        }

        // The two bright heads leading each stream, until they meet.
        if (since < 0)
            foreach (var side in new[] { -1f, 1f })
            {
                var (hx, hy, hz) = Head(t, side);
                var pos = P(hx, hy, hz);
                float b = Clamp01(t / 500) * 0.9f;
                var c = side < 0 ? accent : partner;
                AddLight((int)pos.X, (int)pos.Y, kernels[3], (int)(c.R * b), (int)(c.G * b), (int)(c.B * b));
                AddLight((int)pos.X, (int)pos.Y, kernels[2], 255, 255, 255);
            }

        // ---- Ignition: a fast rise as they collide, then a long decay ----
        float flash = since < -160 ? 0 : since < 0 ? EaseInOutCubic((since + 160) / 160) : MathF.Exp(-since / 340);
        if (flash > 0.012f)
        {
            int k = (int)(flash * 300);
            var map = _iFlash!;
            for (int i = 0; i < px.Length; i++)
            {
                int m = map[i];
                if (m == 0) continue;
                int c = px[i];
                int R = ((c >> 16) & 255) + (((m >> 16) & 255) * k >> 8), G = ((c >> 8) & 255) + (((m >> 8) & 255) * k >> 8), B = (c & 255) + ((m & 255) * k >> 8);
                px[i] = (R > 255 ? 255 : R) << 16 | (G > 255 ? 255 : G) << 8 | (B > 255 ? 255 : B);
            }
        }

        // Onto the screen in one straight copy.
        var mode = g.CompositingMode;
        g.CompositingMode = CompositingMode.SourceCopy;
        if (w == ClientSize.Width && h == ClientSize.Height) g.DrawImageUnscaled(_iBitmap!, 0, 0);
        else g.DrawImage(_iBitmap!, ClientRectangle);
        g.CompositingMode = mode;

        // ---- Name: letters drift apart into place as the light clears ----
        float u = U, W = ClientSize.Width, H = ClientSize.Height;
        if (since > 260)
        {
            const string word = "TWINSTAR";
            var wf = F(58, FontStyle.Regular, "Segoe UI Light");
            float spread = EaseOutCubic((since - 260) / 900), track = (22 + 22 * spread) * u;
            var widths = word.Select(ch => g.MeasureString(ch.ToString(), wf, PointF.Empty, StringFormat.GenericTypographic).Width).ToArray();
            float total = widths.Sum() + track * (word.Length - 1), x = W / 2 - total / 2, wy = H * 0.68f;
            using (var b = new SolidBrush(WithAlpha(Color.White, Clamp01((since - 260) / 500))))
                for (int i = 0; i < word.Length; i++)
                {
                    g.DrawString(word[i].ToString(), wf, b, x, wy, StringFormat.GenericTypographic);
                    x += widths[i] + track;
                }
            DrawCentered(g, "C O N S O L E   ·   D E S K T O P", F(18, FontStyle.Regular), WithAlpha(accent, Clamp01((since - 760) / 500)), W / 2, wy + 100 * u);
        }

        float fade = Clamp01((t - ((float)IntroMs - 420)) / 380);
        if (fade > 0)
            using (var black = new SolidBrush(Color.FromArgb((int)(255 * fade), 0, 0, 0))) g.FillRectangle(black, ClientRectangle);
    }

    /// <summary>Diagnostic: writes the intro as numbered images at a steady frame rate, for making a video.</summary>
    public void RenderIntroFrames(string dir, int fps)
    {
        _preview = true;
        CreateControl();
        Bounds = new Rectangle(new Point(-3840, 0), new Size(1920, 1080));
        _settings = Settings.Load();
        Directory.CreateDirectory(dir);
        using var bmp = new Bitmap(1920, 1080, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        _introAt = 0;
        int frames = (int)(IntroMs / 1000 * fps);
        for (int i = 0; i < frames; i++)
        {
            PaintIntro(g, i * 1000.0 / fps);
            bmp.Save(Path.Combine(dir, $"f{i:D5}.png"), ImageFormat.Png);
        }
        ReleaseIntroBuffers();
    }

    /// <summary>
    /// Draws the extruded T: visible walls far to near, then the visible end cap on top (walls only
    /// ever run away from it, so that order is always right). Colours are solid and lit per face.
    /// </summary>
    void DrawLogo(Graphics g, Func<float, float, float, PointF> project, float yaw, float pitch, float dist, Color accent, out GraphicsPath? visibleFace)
    {
        visibleFace = null;
        var light = new V3(-0.42f, 0.66f, -0.62f);
        int n = LogoOutline.Length;

        V3 World(V3 turned) => new(turned.X, turned.Y + LogoLift, turned.Z + dist);
        PointF Screen(V3 v) => project(v.X, v.Y, v.Z);

        var front = new V3[n]; var back = new V3[n];
        for (int i = 0; i < n; i++)
        {
            front[i] = World(Turn(new V3(LogoOutline[i].x, LogoOutline[i].y, -ExtrudeHalf), yaw, pitch));
            back[i] = World(Turn(new V3(LogoOutline[i].x, LogoOutline[i].y, ExtrudeHalf), yaw, pitch));
        }

        var walls = new List<(float depth, PointF[] quad, Color color)>(n);
        var deep = Mix(accent, Color.Black, 0.82f);
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            float dx = LogoOutline[j].x - LogoOutline[i].x, dy = LogoOutline[j].y - LogoOutline[i].y, len = MathF.Sqrt(dx * dx + dy * dy);
            var normal = Turn(new V3(-dy / len, dx / len, 0), yaw, pitch);
            var a = front[i]; var b = front[j];
            if (V3.Dot(normal, new V3((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2)) >= 0) continue; // faces away
            float lit = 0.12f + 0.88f * Math.Max(0, V3.Dot(normal, light));
            float depth = (a.Z + b.Z + back[i].Z + back[j].Z) / 4;
            walls.Add((depth, new[] { Screen(a), Screen(b), Screen(back[j]), Screen(back[i]) }, Mix(deep, accent, lit)));
        }
        foreach (var (_, quad, color) in walls.OrderByDescending(w => w.depth))
        {
            using var b = new SolidBrush(color);
            g.FillPolygon(b, quad);
            using var seam = new Pen(color, 1f); // closes hairline gaps between faces
            g.DrawPolygon(seam, quad);
        }

        // Whichever end cap faces the camera.
        var frontNormal = Turn(new V3(0, 0, -1), yaw, pitch);
        bool frontVisible = V3.Dot(frontNormal, front[0]) < 0;
        var cap = frontVisible ? front : back;
        var capNormal = frontVisible ? frontNormal : new V3(-frontNormal.X, -frontNormal.Y, -frontNormal.Z);
        var capPoints = cap.Select(Screen).ToArray();
        float capLit = 0.35f + 0.65f * Math.Max(0, V3.Dot(capNormal, light));
        var capColor = Mix(Mix(accent, Color.Black, 0.25f), Color.FromArgb(236, 255, 252), capLit);
        // Lighter at the top, sinking towards the accent at the bottom: reads as a lit surface, not a flat cut-out.
        float top = capPoints.Min(p => p.Y), bottom = capPoints.Max(p => p.Y);
        using (var b = new LinearGradientBrush(new PointF(0, top - 1), new PointF(0, bottom + 1), capColor, Mix(capColor, accent, 0.62f)))
            g.FillPolygon(b, capPoints);
        using (var edge = new Pen(Color.FromArgb(235, 255, 255, 255), 1.6f * U) { LineJoin = LineJoin.Round })
            g.DrawPolygon(edge, capPoints);
        if (frontVisible)
        {
            visibleFace = new GraphicsPath();
            visibleFace.AddPolygon(capPoints);
        }
    }
}
