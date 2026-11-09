using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Twinstar;

/// <summary>
/// Animation: a frame loop paced by the compositor's vsync (DwmFlush), so animations run at the
/// monitor's full refresh rate instead of a ~64 Hz WinForms timer. Every animation is time-based,
/// so it moves at the same speed on 60, 144 or 240 Hz. The loop sleeps whenever nothing moves.
/// Also holds the animated Console/Desktop selector and the toast.
/// </summary>
sealed partial class MainForm
{
    [DllImport("dwmapi.dll")] static extern int DwmFlush();

    static double NowMs => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    Thread? _frameThread;
    readonly AutoResetEvent _frameWake = new(false);
    volatile bool _wantFrames;
    double _lastFrameMs;

    /// <summary>Starts (or keeps) the vsync frame loop running until animations settle.</summary>
    void RequestFrames()
    {
        if (!_wantFrames) _lastFrameMs = NowMs;
        _wantFrames = true;
        if (_frameThread == null)
        {
            _frameThread = new Thread(FrameLoop) { IsBackground = true, Name = "Frames", Priority = ThreadPriority.AboveNormal };
            _frameThread.Start();
        }
        _frameWake.Set();
    }

    void FrameLoop()
    {
        while (true)
        {
            _frameWake.WaitOne();
            while (_wantFrames)
            {
                // Blocks until the next vblank composition: one frame per monitor refresh.
                if (DwmFlush() != 0) Thread.Sleep(4);
                if (IsDisposed || !IsHandleCreated) return;
                try { Invoke(FrameTick); }
                catch (ObjectDisposedException) { return; }
                catch (InvalidOperationException) { return; }
            }
        }
    }

    void FrameTick()
    {
        double now = NowMs;
        float dt = (float)Math.Clamp((now - _lastFrameMs) / 1000.0, 0, 0.05);
        _lastFrameMs = now;
        bool busy = false;
        try { busy = Animate(dt, now); }
        catch (Exception ex) { Log.Write("Animate: " + ex.Message); }
        Invalidate();
        Update(); // paint now, inside this vblank, rather than whenever the message queue gets to it
        if (!busy) _wantFrames = false;
    }

    /// <summary>Advances every animation; true while anything is still moving.</summary>
    bool Animate(float dt, double now)
    {
        bool busy = _view == View.Launching; // spinner
        if (Visible && _view is View.Home or View.Menu) busy |= AnimateHome(dt, now);
        if (_view == View.Menu) busy |= AnimateMenu(dt, now);
        if (_view == View.Selector) busy |= AnimateSelector(dt, now);
        if (_toast != null) busy = true; // toasts slide/fade in and out
        return busy;
    }

    // ---- helpers ----

    static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    static float EaseOutCubic(float t) { t = Clamp01(t); return 1 - (1 - t) * (1 - t) * (1 - t); }
    static float EaseInOutCubic(float t) { t = Clamp01(t); return t < 0.5f ? 4 * t * t * t : 1 - MathF.Pow(-2 * t + 2, 3) / 2; }
    static float Progress(double since, double durationMs, double now) => Clamp01((float)((now - since) / durationMs));
    static float Lerp(float a, float b, float t) => a + (b - a) * t;
    static Color WithAlpha(Color c, float a) => Color.FromArgb(Math.Clamp((int)(c.A * a), 0, 255), c);

    /// <summary>Frame-rate independent exponential approach; returns true until it has arrived.</summary>
    static bool Spring(ref float value, float target, float dt, float speed = 16f, float epsilon = 0.0015f)
    {
        value += (target - value) * (1 - MathF.Exp(-speed * dt));
        if (MathF.Abs(target - value) < epsilon) { value = target; return false; }
        return true;
    }

    // ================= Menus =================

    double _menuOpenAt = -1e9, _pageAt = -1e9;
    float _menuIndexAnim;
    Bitmap? _menuBackdrop, _menuGlass;
    bool _menuBackdropDirty = true;

    bool AnimateMenu(float dt, double now)
    {
        bool busy = Spring(ref _menuIndexAnim, _menuIndex, dt, 22f);
        busy |= now - _menuOpenAt < 280 || now - _pageAt < 240;
        return busy;
    }

    /// <summary>
    /// Behind a menu the home screen doesn't change, so it's rendered once into a snapshot and
    /// copied each frame; the slide-in then only costs the panel itself.
    /// </summary>
    void PaintMenuBackdrop(Graphics g)
    {
        if (_menuBackdropDirty || _menuBackdrop == null || _menuBackdrop.Size != ClientSize)
        {
            _menuBackdrop?.Dispose();
            _menuBackdrop = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
            using (var mg = Graphics.FromImage(_menuBackdrop))
            {
                mg.SmoothingMode = SmoothingMode.AntiAlias;
                mg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                mg.InterpolationMode = InterpolationMode.Bilinear;
                PaintHome(mg);
                // Frosted glass for the menu panel: the screen behind it, blurred and tinted.
                _menuGlass?.Dispose();
                _menuGlass = Fx.Frost(_menuBackdrop, new Rectangle(0, 0, Math.Min(ClientSize.Width, (int)(1100 * U)), ClientSize.Height),
                    Color.FromArgb(150, 12, 16, 28));
                // The menu's dimming is part of the snapshot, so a frame is one copy plus the panel.
                using var shade = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
                mg.FillRectangle(shade, 0, 0, ClientSize.Width, ClientSize.Height);
            }
            _hits.Clear();
            _menuBackdropDirty = false;
        }
        var mode = g.CompositingMode;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImageUnscaled(_menuBackdrop, 0, 0);
        g.CompositingMode = mode;
    }

    // ================= Toast =================

    double _toastAt;

    void PaintToast(Graphics g)
    {
        if (_toast == null) return;
        if (Environment.TickCount64 > _toastUntil) { _toast = null; return; }
        float u = U;
        double now = NowMs, left = _toastUntil - Environment.TickCount64;
        float show = EaseOutCubic(Progress(_toastAt, 220, now)) * Clamp01((float)(left / 300.0));
        var size = g.MeasureString(_toast, F(24));
        var r = new RectangleF((ClientSize.Width - size.Width) / 2 - 30 * u, ClientSize.Height - 180 * u + (1 - show) * 30 * u,
            size.Width + 60 * u, 64 * u);
        using (var p = Rounded(r, 32 * u))
        using (var b = new SolidBrush(Color.FromArgb((int)(235 * show), 30, 41, 59))) g.FillPath(b, p);
        DrawCentered(g, _toast, F(24), WithAlpha(Color.White, show), r.X + r.Width / 2, r.Y + 14 * u);
    }

    // ================= Console / Desktop selector =================

    double _selectorAt = -1e9, _selectorExitAt = -1;
    int _selectorChoice;
    readonly float[] _selectorHover = new float[2];
    Bitmap? _selectorBg;
    string? _selectorBgTheme;

    /// <summary>Selector palette: backdrop gradient, two soft glows, accent, and card fill (resting / highlighted).</summary>
    sealed record SelTheme(string Key, string Name, Color Top, Color Bottom, Color GlowA, Color GlowB, Color Accent, Color Card, Color CardHover);

    static readonly SelTheme[] SelThemes =
    {
        new("aurora", "Aurora", BgTop, BgBottom, Color.FromArgb(70, 59, 130, 246), Color.FromArgb(55, 124, 58, 237),
            Accent, Panel, Color.FromArgb(30, 41, 72)),
        new("ember", "Ember", Color.FromArgb(20, 9, 12), Color.FromArgb(42, 16, 22), Color.FromArgb(85, 249, 115, 22), Color.FromArgb(60, 219, 39, 119),
            Color.FromArgb(251, 146, 60), Color.FromArgb(32, 16, 20), Color.FromArgb(66, 30, 30)),
        new("mint", "Mint", Color.FromArgb(6, 18, 20), Color.FromArgb(10, 34, 36), Color.FromArgb(70, 20, 184, 166), Color.FromArgb(50, 132, 204, 22),
            Color.FromArgb(45, 212, 191), Color.FromArgb(12, 30, 32), Color.FromArgb(20, 58, 58)),
    };

    SelTheme CurrentSelTheme => SelThemes.FirstOrDefault(t => t.Key == _settings.SelectorTheme) ?? SelThemes[0];

    void CycleSelectorTheme()
    {
        int i = Array.IndexOf(SelThemes, CurrentSelTheme);
        _settings.SelectorTheme = SelThemes[(i + 1) % SelThemes.Length].Key;
        _settings.Save();
        _selectorThemeAt = NowMs;
        RequestFrames();
    }

    double _selectorThemeAt = -1e9;

    void StartSelectorExit(int choice)
    {
        if (_selectorExitAt >= 0) return; // already leaving
        _selectorIndex = choice;
        _selectorChoice = choice;
        _selectorExitAt = NowMs;
        if (choice == 1) Sounds.Play(Sfx.Bye);
        RequestFrames();
    }

    bool AnimateSelector(float dt, double now)
    {
        bool busy = now - _selectorAt < 900 || now - _selectorThemeAt < 1200;
        for (int i = 0; i < 2; i++) busy |= Spring(ref _selectorHover[i], i == _selectorIndex ? 1 : 0, dt, 18f);
        if (_selectorExitAt >= 0)
        {
            busy = true;
            if (now - _selectorExitAt >= 340)
            {
                // The fade to black finished: switch modes underneath it.
                _selectorExitAt = -1;
                if (_selectorChoice == 0) EnterConsole(); else EnterDesktop();
            }
        }
        return busy;
    }

    void PaintSelector(Graphics g)
    {
        float u = U, W = ClientSize.Width, H = ClientSize.Height;
        double now = NowMs;

        var th = CurrentSelTheme;
        var accent = th.Accent;
        // Static backdrop (gradient + soft glows) is baked once per screen size and theme.
        if (_selectorBg == null || _selectorBg.Size != ClientSize || _selectorBgTheme != th.Key)
        {
            _selectorBgTheme = th.Key;
            _selectorBg?.Dispose();
            _selectorBg = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
            using var bg = Graphics.FromImage(_selectorBg);
            using (var lin = new LinearGradientBrush(ClientRectangle, th.Top, th.Bottom, 90f)) bg.FillRectangle(lin, ClientRectangle);
            void Glow(float cx, float cy, float r, Color c)
            {
                using var path = new GraphicsPath();
                path.AddEllipse(cx - r, cy - r, r * 2, r * 2);
                using var pg = new PathGradientBrush(path) { CenterColor = c, SurroundColors = new[] { Color.FromArgb(0, c) } };
                bg.FillPath(pg, path);
            }
            Glow(W * 0.28f, H * 0.62f, H * 0.7f, th.GlowA);
            Glow(W * 0.74f, H * 0.38f, H * 0.6f, th.GlowB);
        }
        var mode = g.CompositingMode;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImageUnscaled(_selectorBg, 0, 0);
        g.CompositingMode = mode;

        // Entrance: title drops in, then the two cards rise one after the other.
        float tTitle = EaseOutCubic(Progress(_selectorAt, 500, now));
        DrawCentered(g, "How do you want to use your PC?", F(54, FontStyle.Bold), WithAlpha(Color.White, tTitle),
            W / 2, H * 0.2f - (1 - tTitle) * 24 * u);
        DrawCentered(g, _pad.AnyConnected ? $"{_pad.Name} connected — choose a mode" : "Connect a controller, or use mouse / keyboard",
            F(26), WithAlpha(_pad.AnyConnected ? Green : Dim, tTitle), W / 2, H * 0.2f + 84 * u - (1 - tTitle) * 16 * u);

        float exit = _selectorExitAt >= 0 ? EaseInOutCubic(Progress(_selectorExitAt, 340, now)) : 0;
        float cw = 520 * u, ch = 380 * u, gap = 80 * u;
        float x0 = (W - cw * 2 - gap) / 2, y0 = H * 0.33f;
        string[] titles = { "Console", "Desktop" };
        string[] subs = { "Your games, full screen, controller first", "Start Windows like normal" };
        for (int i = 0; i < 2; i++)
        {
            float enter = EaseOutCubic(Progress(_selectorAt + 120 + i * 90, 520, now));
            float hover = _selectorHover[i];
            bool chosen = _selectorExitAt >= 0 && i == _selectorChoice;
            float alpha = enter * (_selectorExitAt >= 0 && !chosen ? 1 - exit : 1);
            if (alpha <= 0.01f) continue;

            var r = new RectangleF(x0 + i * (cw + gap), y0 + (1 - enter) * 70 * u, cw, ch);
            float grow = 12 * u * hover + (chosen ? 30 * u * exit : 0);
            r.Inflate(grow, grow);
            int ssp = (int)(44 * u);
            // Shadow and glow are drawn at their baked size, centred on the card (never rescaled), and the
            // glow's strength steps through pre-rendered levels: per-frame fading in GDI+ is what made this lag.
            float ccx = r.X + r.Width / 2, ccy = r.Y + r.Height / 2;
            var cardShadow = FxBitmap($"selshadow|{(int)cw}", () => Fx.SoftRect((int)cw, (int)ch, 28 * u, ssp, Color.FromArgb(170, 0, 0, 0)));
            DrawFaded(g, cardShadow, new RectangleF(ccx - cardShadow.Width / 2f, ccy - cardShadow.Height / 2f + 18 * u, cardShadow.Width, cardShadow.Height),
                alpha > 0.97f ? 1 : alpha);
            int glowLevel = (int)MathF.Round(alpha * hover * 6);
            if (glowLevel > 0)
            {
                var cardGlow = FxBitmap($"selglow|{th.Key}|{(int)cw}|{glowLevel}", () =>
                    Fx.SoftRect((int)cw + 24, (int)ch + 24, 28 * u, ssp, Color.FromArgb(28 * glowLevel, accent)));
                DrawFaded(g, cardGlow, new RectangleF(ccx - cardGlow.Width / 2f, ccy - cardGlow.Height / 2f, cardGlow.Width, cardGlow.Height), 1);
            }
            using (var path = Rounded(r, 28 * u))
            {
                var fillCol = Color.FromArgb(
                    (int)Lerp(th.Card.R, th.CardHover.R, hover), (int)Lerp(th.Card.G, th.CardHover.G, hover), (int)Lerp(th.Card.B, th.CardHover.B, hover));
                using var fill = new SolidBrush(WithAlpha(fillCol, alpha * 0.78f)); // translucent: the glows show through
                g.FillPath(fill, path);
                if (hover > 0.02f) using (var pen = new Pen(WithAlpha(accent, hover * alpha), 5 * u)) g.DrawPath(pen, path);
            }
            var iconCol = WithAlpha(Color.FromArgb((int)Lerp(Dim.R, accent.R, hover), (int)Lerp(Dim.G, accent.G, hover), (int)Lerp(Dim.B, accent.B, hover)), alpha);
            var iconBox = new RectangleF(r.X + r.Width / 2 - 110 * u, r.Y + 50 * u - hover * 6 * u, 220 * u, 150 * u);
            if (i == 0) DrawGamepadIcon(g, iconBox, iconCol); else DrawMonitorIcon(g, iconBox, iconCol);
            DrawCentered(g, titles[i], F(44, FontStyle.Bold), WithAlpha(Color.White, alpha), r.X + r.Width / 2, r.Bottom - 120 * u);
            DrawCentered(g, subs[i], F(22), WithAlpha(Dim, alpha), r.X + r.Width / 2, r.Bottom - 65 * u);
            int idx = i;
            _hits.Add((r, () => StartSelectorExit(idx), () => { if (_selectorExitAt < 0) { _selectorIndex = idx; RequestFrames(); } }));
        }

        PaintHints(g, new[] { ("A", "Select"), ("◄►", "Choose"), ("Y", $"Theme: {th.Name}") }, "Ctrl+Alt+F12 brings you back here anytime");

        // Theme switch: the theme's name flashes up at the bottom.
        float tp = (float)((now - _selectorThemeAt) / 1200);
        if (tp is >= 0 and < 1)
        {
            float a = tp < 0.15f ? tp / 0.15f : 1 - Clamp01((tp - 0.6f) / 0.4f);
            DrawCentered(g, th.Name, F(30, FontStyle.Bold), WithAlpha(accent, a), W / 2, H * 0.33f + 380 * u + 70 * u);
        }

        // Leaving: everything fades to black, then the chosen mode takes over.
        if (exit > 0)
            using (var black = new SolidBrush(Color.FromArgb((int)(255 * exit), 0, 0, 0))) g.FillRectangle(black, ClientRectangle);
    }
}
