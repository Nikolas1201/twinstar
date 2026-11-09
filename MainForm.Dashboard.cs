using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Twinstar;

/// <summary>
/// The console home screen: full-screen art of the selected game behind a Games/Apps tab bar,
/// a row of cover tiles (selected one enlarged), the game's logo/title and actions, and a
/// "Recently played" card row.
///
/// Performance: GDI+ is CPU-only, so anything big is prepared once on a worker thread and cached:
/// backgrounds are pre-composed with their shading (one fast copy per frame), and card art and
/// logos are decoded off the UI thread. The background only switches once the selection settles.
/// </summary>
sealed partial class MainForm
{
    int _tab;          // 0 = Games, 1 = Apps
    // Focus zones, top to bottom. Up/Down moves between zones, Left/Right within one.
    const int FocusTop = -1, FocusRow = 0, FocusActions = 1, FocusCards = 2;
    int _homeFocus = FocusRow;
    int _topSel;       // 0 Games, 1 Apps, 2 controller, 3 settings, 4 profile/power
    int _actionSel;    // 0 Play/Resume, 1 options (•••)
    int TopItems => Tabs.Length + 3; // tabs, then controller, settings, profile/power
    int _cardSel;

    static readonly string[] Tabs = { "Games", "Apps", "Friends" };
    const int TabFriends = 2;
    static readonly Color Shade = Color.FromArgb(8, 10, 18);

    List<Game> TabGames() => _tab == TabFriends ? new() : _games.Where(g => g.IsApp == (_tab == 1)).ToList();

    // ---- Effects: per-game accent colours and cached shadow/glow bitmaps ----

    readonly Dictionary<string, Color> _accent = new();
    readonly Dictionary<string, Bitmap> _fxCache = new();

    /// <summary>The game's signature colour (from its cover), used for its glow and background tint.</summary>
    Color AccentFor(Game g) => _accent.TryGetValue(g.Name, out var c) ? c : FromHue(StableHue(g.Name), 0.55f);

    Bitmap FxBitmap(string key, Func<Bitmap> make)
    {
        if (!_fxCache.TryGetValue(key, out var b)) _fxCache[key] = b = make();
        return b;
    }

    /// <summary>Draws a bitmap faded to <paramref name="a"/>, using the fast unscaled path when it fits exactly.</summary>
    static void DrawFaded(Graphics g, Bitmap b, RectangleF dest, float a)
    {
        if (a <= 0.01f) return;
        if (a >= 0.995f)
        {
            if (Math.Abs(dest.Width - b.Width) < 2f && Math.Abs(dest.Height - b.Height) < 2f)
                g.DrawImageUnscaled(b, (int)MathF.Round(dest.X), (int)MathF.Round(dest.Y));
            else g.DrawImage(b, dest);
            return;
        }
        using var ia = new ImageAttributes();
        ia.SetColorMatrix(new ColorMatrix { Matrix33 = a });
        g.DrawImage(b, Rectangle.Round(dest), 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, ia);
    }

    Game? SelectedGame()
    {
        var list = TabGames();
        if (list.Count == 0) return null;
        _sel = Math.Clamp(_sel, 0, list.Count - 1);
        return list[_sel];
    }

    List<Game> RecentGames()
    {
        var list = TabGames();
        var recent = list.Where(g => _settings.LastPlayed.ContainsKey(g.Name))
            .OrderByDescending(g => _settings.LastPlayed[g.Name]).Take(8).ToList();
        return recent.Count > 0 ? recent : list.Take(8).ToList();
    }

    /// <summary>The game the title, actions and background describe: whichever row has focus.</summary>
    Game? FocusedGame()
    {
        var recent = RecentGames();
        return _homeFocus == FocusCards && recent.Count > 0 ? recent[Math.Clamp(_cardSel, 0, recent.Count - 1)] : SelectedGame();
    }

    // ================= Input =================

    void HomeInput(Btn b)
    {
        var list = TabGames();
        int n = list.Count;
        var cards = RecentGames();
        var game = n > 0 ? list[Math.Clamp(_sel, 0, n - 1)] : null;
        if (b is Btn.LB or Btn.RB) { int keep = _homeFocus; SwitchTab(b == Btn.RB ? 1 : -1); if (keep == FocusTop) _homeFocus = FocusTop; return; }
        if (_tab == TabFriends && _homeFocus != FocusTop && FriendsInput(b)) return;

        switch (_homeFocus)
        {
            case FocusTop:
                switch (b)
                {
                    case Btn.Left: _topSel = Math.Max(0, _topSel - 1); return;
                    case Btn.Right: _topSel = Math.Min(TopItems - 1, _topSel + 1); return;
                    case Btn.Down or Btn.B: _homeFocus = FocusRow; return;
                    case Btn.A: ActivateTopItem(_topSel); return;
                }
                break;

            case FocusActions when game != null:
                switch (b)
                {
                    case Btn.Left: _actionSel = 0; return;
                    case Btn.Right: _actionSel = 1; return;
                    case Btn.Up or Btn.B: _homeFocus = FocusRow; return;
                    case Btn.Down when cards.Count > 0: _homeFocus = FocusCards; return;
                    case Btn.A: if (_actionSel == 0) LaunchOrResume(game); else OpenGameOptions(game); return;
                    case Btn.Y: OpenGameOptions(game); return;
                }
                break;

            case FocusCards when cards.Count > 0:
                _cardSel = Math.Clamp(_cardSel, 0, cards.Count - 1);
                switch (b)
                {
                    case Btn.Left: _cardSel = Math.Max(0, _cardSel - 1); return;
                    case Btn.Right: _cardSel = Math.Min(cards.Count - 1, _cardSel + 1); return;
                    case Btn.Up: _homeFocus = n > 0 ? FocusActions : FocusRow; return;
                    case Btn.B: _homeFocus = FocusRow; return;
                    case Btn.A: LaunchOrResume(cards[_cardSel]); return;
                    case Btn.Y: OpenGameOptions(cards[_cardSel]); return;
                }
                break;

            default:
                _homeFocus = FocusRow;
                switch (b)
                {
                    case Btn.Left when n > 0: _sel = Math.Max(0, _sel - 1); return;
                    case Btn.Right when n > 0: _sel = Math.Min(n - 1, _sel + 1); return;
                    case Btn.Up: _homeFocus = FocusTop; _topSel = _tab; return;
                    case Btn.Down when n > 0: _homeFocus = FocusActions; _actionSel = 0; return;
                    case Btn.Down when cards.Count > 0: _homeFocus = FocusCards; return;
                    case Btn.A when game != null: LaunchOrResume(game); return;
                    case Btn.Y when game != null: OpenGameOptions(game); return;
                }
                break;
        }
        switch (b)
        {
            case Btn.X: ReloadLibrary(announce: true, manual: true); break;
            case Btn.Start: OpenMenu(Page.Settings); break; // Options/Menu button
            case Btn.Back: OpenMenu(Page.Power); break;
        }
    }

    /// <summary>Top bar: Games/Apps tabs, controller status, settings, profile (power).</summary>
    void ActivateTopItem(int i)
    {
        switch (i)
        {
            case var t when t < Tabs.Length:
                if (i != _tab) SwitchTab(i - _tab);
                _homeFocus = FocusTop;
                _topSel = i;
                break;
            case var c when c == Tabs.Length:
                Toast(_pad.AnyConnected ? $"{_pad.Name} connected" + (_pad.Count > 1 ? $" (+{_pad.Count - 1} more)" : "") : "No controller connected");
                break;
            case var s when s == Tabs.Length + 1: OpenMenu(Page.Settings); break;
            default: OpenMenu(Page.Power); break;
        }
    }

    // ---- Animated focus highlights (one value per focusable element, 0..1) ----

    readonly Dictionary<string, float> _focusGlow = new();

    string? FocusedElement() => _homeFocus switch
    {
        FocusTop => $"top{_topSel}",
        FocusActions => _actionSel == 0 ? "play" : "more",
        _ => null,
    };

    bool AnimateFocus(float dt)
    {
        var target = FocusedElement();
        if (target != null) _focusGlow.TryAdd(target, 0);
        bool busy = false;
        foreach (var key in _focusGlow.Keys.ToList())
        {
            float v = _focusGlow[key];
            busy |= Spring(ref v, key == target ? 1 : 0, dt, 18f);
            _focusGlow[key] = v;
        }
        return busy;
    }

    float Glow(string id) => _focusGlow.TryGetValue(id, out var v) ? v : 0;

    /// <summary>Soft pill/circle highlight behind a top-bar item, fading with its focus value.</summary>
    void DrawTopHighlight(Graphics g, RectangleF r, float f, bool circle)
    {
        if (f < 0.01f) return;
        float u = U;
        r.Inflate(10 * u * (0.7f + 0.3f * f), 8 * u * (0.7f + 0.3f * f));
        using var fill = new SolidBrush(Color.FromArgb((int)(45 * f), 255, 255, 255));
        using var pen = new Pen(Color.FromArgb((int)(235 * f), 255, 255, 255), 2.5f * u);
        if (circle) { g.FillEllipse(fill, r); g.DrawEllipse(pen, r); }
        else
        {
            using var path = Rounded(r, r.Height / 2);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }
    }


    // ================= Off-thread art =================

    /// <summary>Bitmaps built on worker threads, keyed by purpose + game + size. Null = nothing to show.</summary>
    readonly Dictionary<string, Bitmap?> _art = new();
    readonly HashSet<string> _artBuilding = new();
    /// <summary>Keys built from a fallback (e.g. blurred cover) that should be rebuilt when real art arrives.</summary>
    readonly HashSet<string> _artProvisional = new();
    readonly Queue<string> _artOrder = new();

    /// <summary>
    /// Returns the cached bitmap for a key, or null while it's being built on a worker thread
    /// (the screen repaints when it's ready). The builder must not touch UI-thread objects.
    /// </summary>
    Bitmap? Art(string key, Func<(Bitmap? bmp, bool provisional)> build)
    {
        if (_art.TryGetValue(key, out var bmp)) return bmp;
        if (!_artBuilding.Add(key)) return null;
        Task.Run(() =>
        {
            (Bitmap? bmp, bool provisional) r = (null, false);
            try { r = build(); }
            catch (Exception ex) { Log.Write($"Art {key}: {ex.Message}"); }
            if (!IsHandleCreated) { r.bmp?.Dispose(); return; }
            BeginInvoke(() =>
            {
                _artBuilding.Remove(key);
                _art[key] = r.bmp;
                if (r.provisional) _artProvisional.Add(key); else _artProvisional.Remove(key);
                _artOrder.Enqueue(key);
                // Keep memory bounded: full-screen backgrounds are ~8 MB each.
                while (_art.Count > 60 && _artOrder.TryDequeue(out var old))
                    if (old != key && _art.Remove(old, out var o) && o != _bgShown && o != _bgPrev) { if (o != null) Gdi.Forget(o); o?.Dispose(); }
                _menuBackdropDirty = true;
                Invalidate();
            });
        });
        return null;
    }

    /// <summary>Remote art finished downloading: rebuild anything that was showing a fallback.</summary>
    void OnArtDownloaded()
    {
        foreach (var key in _artProvisional.ToList())
            if (_art.Remove(key, out var b) && b != _bgShown && b != _bgPrev) { if (b != null) Gdi.Forget(b); b?.Dispose(); }
        _artProvisional.Clear();
        foreach (var key in _art.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) _art.Remove(key);
        _menuBackdropDirty = true;
        Invalidate();
    }

    static Image LoadImage(string path)
    {
        using var fs = File.OpenRead(path);
        using var img = Image.FromStream(fs);
        return new Bitmap(img);
    }

    /// <summary>Draws an image to fill a rectangle, cropping the overflow (like CSS object-fit: cover).</summary>
    static void DrawCover(Graphics g, Image img, RectangleF r, float focusY = 0.5f)
    {
        float scale = Math.Max(r.Width / img.Width, r.Height / img.Height);
        float w = img.Width * scale, h = img.Height * scale;
        g.DrawImage(img, r.X + (r.Width - w) / 2, r.Y + (r.Height - h) * focusY, w, h);
    }

    // ---- Background: art + all shading baked into one screen-sized bitmap ----

    Game? _bgWant;
    long _bgWantSince;
    Bitmap? _bgShown;
    readonly System.Windows.Forms.Timer _bgSettle = new() { Interval = 140 };

    Bitmap? BackgroundFor(Game g)
    {
        var size = ClientSize;
        string? hero = g.HeroFile, cover = g.IsIcon ? null : g.ImagePath;
        var accent = AccentFor(g);
        return Art($"bg|{g.Name}|{size.Width}x{size.Height}", () =>
        {
            if (hero == null && cover == null) return (null, g.HeroUrls.Count > 0);
            using var img = LoadImage(hero ?? cover!);
            var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using var gr = Graphics.FromImage(bmp);
            gr.InterpolationMode = InterpolationMode.HighQualityBilinear;
            if (hero != null) DrawCover(gr, img, new RectangleF(0, 0, size.Width, size.Height), 0.3f);
            else
            {
                // Soft blur of the cover: shrink a lot, then stretch back up with smoothing.
                int sw = Math.Max(8, size.Width / 28), sh = Math.Max(8, size.Height / 28);
                using var small = new Bitmap(sw, sh);
                using (var sg = Graphics.FromImage(small))
                {
                    sg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    DrawCover(sg, img, new RectangleF(0, 0, sw, sh));
                }
                gr.PixelOffsetMode = PixelOffsetMode.Half;
                gr.DrawImage(small, new Rectangle(-size.Width / 20, -size.Height / 20, size.Width * 11 / 10, size.Height * 11 / 10));
            }
            DrawShading(gr, size.Width, size.Height);
            // Mood lighting: the game's colour glows up from the bottom-left corner.
            using (var wash = new GraphicsPath())
            {
                wash.AddEllipse(-size.Width * 0.35f, size.Height * 0.3f, size.Width * 1.05f, size.Height * 1.2f);
                using var pg = new PathGradientBrush(wash) { CenterColor = Color.FromArgb(80, accent), SurroundColors = new[] { Color.FromArgb(0, accent) } };
                gr.FillPath(pg, wash);
            }
            // A cover blur is only a stand-in while the real background may still be downloading.
            return (bmp, hero == null && g.HeroUrls.Count > 0);
        });
    }

    /// <summary>Darkens where text sits: overall, left side, bottom, top bar.</summary>
    static void DrawShading(Graphics g, float W, float H)
    {
        using (var dim = new SolidBrush(Color.FromArgb(60, Shade))) g.FillRectangle(dim, 0, 0, W, H);
        using (var left = new LinearGradientBrush(new RectangleF(0, 0, W * 0.75f, H), Color.FromArgb(225, Shade), Color.FromArgb(0, Shade), 0f))
            g.FillRectangle(left, 0, 0, W * 0.75f - 1, H);
        using (var bottom = new LinearGradientBrush(new RectangleF(0, H * 0.4f, W, H * 0.6f + 1), Color.FromArgb(0, Shade), Color.FromArgb(240, Shade), 90f))
            g.FillRectangle(bottom, 0, H * 0.4f + 1, W, H * 0.6f);
        using (var top = new LinearGradientBrush(new RectangleF(0, 0, W, H * 0.3f), Color.FromArgb(170, Shade), Color.FromArgb(0, Shade), 90f))
            g.FillRectangle(top, 0, 0, W, H * 0.3f - 1);
    }

    Bitmap? _plainBg;

    void PaintBackdrop(Graphics g, Game? focused)
    {
        // Only switch backgrounds once the selection rests, so fast scrolling stays smooth.
        if (focused != _bgWant)
        {
            _bgWant = focused;
            _bgWantSince = Environment.TickCount64;
            _bgSettle.Stop();
            _bgSettle.Start();
        }
        if (focused != null && Environment.TickCount64 - _bgWantSince >= _bgSettle.Interval)
        {
            var bmp = BackgroundFor(focused);
            if ((bmp != null || !_artBuilding.Contains($"bg|{focused.Name}|{ClientSize.Width}x{ClientSize.Height}")) && bmp != _bgShown)
            {
                _bgPrev = ScreenBackground(_bgShown);
                _bgShown = bmp;
                _bgDipAt = NowMs;
                RequestFrames();
            }
        }

        var shown = ScreenBackground(_bgShown);
        var mode = g.CompositingMode;
        g.CompositingMode = CompositingMode.SourceCopy; // straight copy: the fastest GDI+ draw there is
        float fade = EaseOutCubic(Progress(_bgDipAt, 260, NowMs));
        if (fade < 0.995f && _bgPrev != null && _bgPrev.Size == ClientSize && _bgPrev != shown)
        {
            // Crossfade old -> new. GDI's AlphaBlend does this several times faster than GDI+.
            g.DrawImageUnscaled(_bgPrev, 0, 0);
            g.CompositingMode = mode;
            Gdi.BlendOnto(g, shown, (byte)(fade * 255));
        }
        else
        {
            g.DrawImageUnscaled(shown, 0, 0);
            g.CompositingMode = mode;
        }
    }

    Bitmap? _bgPrev;

    /// <summary>The background to show for a bitmap that may be missing or the wrong size: the plain backdrop.</summary>
    Bitmap ScreenBackground(Bitmap? bmp)
    {
        if (bmp != null && bmp.Size == ClientSize) return bmp;
        if (_plainBg == null || _plainBg.Size != ClientSize)
        {
            _plainBg?.Dispose();
            _plainBg = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
            using var pg = Graphics.FromImage(_plainBg);
            using (var bg = new LinearGradientBrush(ClientRectangle, BgTop, BgBottom, 90f)) pg.FillRectangle(bg, ClientRectangle);
            DrawShading(pg, ClientSize.Width, ClientSize.Height);
        }
        return _plainBg;
    }

    // ---- Logos and cards ----

    // ================= Painting =================

    void PaintTopBar(Graphics g, float margin)
    {
        float u = U, W = ClientSize.Width, y = 30 * u;
        float x = margin;
        for (int i = 0; i < Tabs.Length; i++)
        {
            var f = F(30, i == _tab ? FontStyle.Bold : FontStyle.Regular);
            var size = g.MeasureString(Tabs[i], f);
            DrawTopHighlight(g, new RectangleF(x, y + 2 * u, size.Width, size.Height - 4 * u), Glow($"top{i}"), circle: false);
            using (var b = new SolidBrush(i == _tab ? Color.White : Color.FromArgb(150, 255, 255, 255)))
                g.DrawString(Tabs[i], f, b, x, y);
            int idx = i;
            _hits.Add((new RectangleF(x, y, size.Width, size.Height), () => { if (idx != _tab) SwitchTab(idx - _tab); }, null));
            x += size.Width + 30 * u;
        }

        // Right side: controller status, settings, profile initial, clock.
        float right = W - margin;
        var clock = DateTime.Now.ToString("h:mm tt");
        var cf = F(28);
        var cs = g.MeasureString(clock, cf);
        g.DrawString(clock, cf, Brushes.White, right - cs.Width, y + 2 * u);
        right -= cs.Width + 34 * u;

        float d = 44 * u;
        var avatar = new RectangleF(right - d, y, d, d);
        DrawTopHighlight(g, avatar, Glow($"top{Tabs.Length + 2}"), circle: true);
        using (var ab = new LinearGradientBrush(avatar, Accent, Color.FromArgb(124, 58, 237), 45f)) g.FillEllipse(ab, avatar);
        var initial = string.IsNullOrEmpty(Environment.UserName) ? "?" : Environment.UserName[..1].ToUpperInvariant();
        Icons.TextCentered(g, initial, F(20, FontStyle.Bold), Color.White, avatar);
        _hits.Add((avatar, () => OpenMenu(Page.Power), null));
        right -= d + 22 * u;

        // Every top-right item shares one box size, so icons and focus circles line up exactly.
        var gear = new RectangleF(right - d, y, d, d);
        DrawTopHighlight(g, gear, Glow($"top{Tabs.Length + 1}"), circle: true);
        Icons.Gear(g, RectangleF.Inflate(gear, -8 * u, -8 * u), Color.White, 1.7f);
        _hits.Add((gear, () => OpenMenu(Page.Settings), null));
        right -= d + 22 * u;

        var padIcon = new RectangleF(right - d, y, d, d);
        DrawTopHighlight(g, padIcon, Glow($"top{Tabs.Length}"), circle: true);
        _hits.Add((padIcon, () => ActivateTopItem(Tabs.Length), null));
        Icons.Gamepad(g, RectangleF.Inflate(padIcon, -6 * u, -6 * u), _pad.AnyConnected ? Color.White : Color.FromArgb(110, 255, 255, 255), 1.7f);
        if (_pad.AnyConnected)
        {
            // Status dot with a dark rim so it reads cleanly over the icon.
            var dot = new RectangleF(padIcon.Right - 13 * u, padIcon.Bottom - 13 * u, 11 * u, 11 * u);
            using (var rim = new SolidBrush(Shade)) g.FillEllipse(rim, RectangleF.Inflate(dot, 2 * u, 2 * u));
            using (var b = new SolidBrush(Green)) g.FillEllipse(b, dot);
        }
    }

    // ================= Animation state =================

    float _selAnim, _cardSelAnim, _cardFocusAnim, _glowAnim;
    double _infoAt = -1e9, _bgDipAt = -1e9, _homeEnterAt = -1e9, _rowIntroAt = -1e9;
    Game? _infoGame;

    void SwitchTab(int dir)
    {
        _tab = (_tab + Tabs.Length + dir) % Tabs.Length;
        _sel = 0;
        _selAnim = 0;
        _cardSel = 0;
        _cardSelAnim = 0;
        _homeFocus = 0;
        _rowIntroAt = NowMs;
        RequestFrames();
    }

    /// <summary>Advances home-screen animations; returns true while anything is still moving.</summary>
    bool AnimateHome(float dt, double now)
    {
        bool busy = Spring(ref _selAnim, _sel, dt, 15f);
        // The selected cover's glow blooms once the row has stopped moving.
        if (MathF.Abs(_selAnim - _sel) > 0.001f) _glowAnim = 0;
        else busy |= Spring(ref _glowAnim, 1, dt, 9f);
        busy |= Spring(ref _cardSelAnim, _cardSel, dt, 15f);
        busy |= Spring(ref _cardFocusAnim, _homeFocus == FocusCards ? 1 : 0, dt, 14f);
        busy |= AnimateFocus(dt);
        busy |= IntroPlaying;
        busy |= now - _infoAt < 320 || now - _bgDipAt < 260 || now - _homeEnterAt < 520 || now - _rowIntroAt < 380;
        return busy;
    }

    // ================= Pre-rendered tiles and cards =================

    readonly Dictionary<string, Bitmap> _tiles = new();

    /// <summary>
    /// A game's cover (or colored placeholder) with rounded, anti-aliased corners, rendered once.
    /// Drawing it is then a single image call, however many tiles are on screen.
    /// </summary>
    int _tileBakesThisFrame;

    Bitmap? TileBitmap(Game game, int w, int h)
    {
        string key0 = $"{game.Name}|{game.ImagePath}|";
        if (_tiles.TryGetValue(key0 + $"1|{w}x{h}", out var fast) || _tiles.TryGetValue(key0 + $"0|{w}x{h}", out fast)) return fast;
        // Preparing a tile decodes and resizes its cover; a few per frame keeps the screen moving
        // (a full row at once would freeze the transition). The rest appear over the next frames.
        if (++_tileBakesThisFrame > 3) { _menuBackdropDirty = true; RequestFrames(); return null; }
        var img = game.Image; // may start a store-cover download
        if (img != null && !game.IsIcon && !_accent.ContainsKey(game.Name))
            try { _accent[game.Name] = Fx.AccentOf(img); } catch { }
        string key = $"{game.Name}|{game.ImagePath}|{(img == null ? 0 : 1)}|{w}x{h}";
        if (_tiles.TryGetValue(key, out var hit)) return hit;
        if (_tiles.Count > 400) { foreach (var b in _tiles.Values) b.Dispose(); _tiles.Clear(); }

        float u = U;
        var content = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(content))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var r = new RectangleF(0, 0, w, h);
            if (img != null && !game.IsIcon) DrawCover(g, img, r, 0.5f);
            else
            {
                // No art: a colored card with the icon (if any) and the title.
                int hue = StableHue(game.Name);
                using (var bg = new LinearGradientBrush(r, FromHue(hue, 0.45f), FromHue((hue + 40) % 360, 0.25f), 60f))
                    g.FillRectangle(bg, r);
                if (img != null)
                {
                    float s = w * 0.4f;
                    g.DrawImage(img, (w - s) / 2, h * 0.22f, s, s);
                }
                var tr = new RectangleF(w * 0.08f, h * 0.58f, w * 0.84f, h * 0.38f);
                using var sf = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord };
                g.DrawString(game.Name, F(Math.Clamp(w / u / 9, 13, 30), FontStyle.Bold), Brushes.White, tr, sf);
            }
        }
        var tile = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(tile))
        using (var brush = new TextureBrush(content))
        using (var path = Rounded(new RectangleF(0, 0, w, h), Math.Min(w, h) * 0.085f))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(brush, path);
        }
        content.Dispose();
        _tiles[key] = tile;
        return tile;
    }

    /// <summary>Draws a game tile at any size from the pre-rendered bitmap, plus focus ring and running dot.</summary>
    void DrawTile(Graphics g, Game game, RectangleF r, bool on, float alpha = 1f, bool resting = true, float glow = 0)
    {
        float u = U;
        // Under the cover: a glow in the game's own colour (when selected), then a soft drop shadow.
        int baseW = (int)(160 * u), spread = (int)(60 * u);
        // Glow strength is quantised to pre-rendered levels: a per-frame fade in GDI+ is far too slow.
        int level = (int)MathF.Round(glow * alpha * 6);
        if (level > 0)
        {
            var gb = FxBitmap($"glow|{game.Name}|{baseW}|{level}", () =>
                Fx.SoftRect(baseW, (int)(baseW * 1.5f), 14 * u, spread, Color.FromArgb(42 * level, AccentFor(game))));
            float k = r.Width / baseW;
            DrawFaded(g, gb, RectangleF.Inflate(r, spread * k, spread * k), 1);
        }
        int sw0 = (int)MathF.Round(r.Width), ssp = (int)(18 * u);
        var shadow = FxBitmap($"shadow|{(resting ? sw0 : baseW)}", () =>
        {
            int w0 = resting ? sw0 : baseW;
            return Fx.SoftRect(w0, (int)MathF.Round(w0 * 1.5f), w0 * 0.085f, ssp, Color.FromArgb(150, 0, 0, 0));
        });
        float sk = r.Width / (resting ? sw0 : baseW);
        var interp = g.InterpolationMode;
        if (!resting) g.InterpolationMode = InterpolationMode.NearestNeighbor; // a blurred shadow hides it, and it's much faster
        DrawFaded(g, shadow, new RectangleF(r.X - ssp * sk, r.Y - ssp * sk + 10 * u, shadow.Width * sk, shadow.Height * sk), alpha);
        g.InterpolationMode = interp;
        // Resting tiles use a bitmap of exactly their size and a plain copy (no resampling);
        // only tiles caught mid-grow are scaled from the large version.
        int bw = resting ? (int)MathF.Round(r.Width) : (int)(160 * u);
        var tile = TileBitmap(game, bw, (int)MathF.Round(bw * 1.5f));
        if (tile == null)
        {
            // Not prepared yet (it will be within a frame or two): a soft placeholder in its colour.
            using var ph = Rounded(r, Math.Min(r.Width, r.Height) * 0.085f);
            using var pb = new SolidBrush(Color.FromArgb((int)(70 * alpha), FromHue(StableHue(game.Name), 0.35f)));
            g.FillPath(pb, ph);
            return;
        }
        if (alpha >= 0.995f && resting) g.DrawImageUnscaled(tile, (int)MathF.Round(r.X), (int)MathF.Round(r.Y));
        else if (alpha >= 0.995f) g.DrawImage(tile, r);
        else
        {
            using var ia = new ImageAttributes();
            ia.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
            g.DrawImage(tile, Rectangle.Round(r), 0, 0, tile.Width, tile.Height, GraphicsUnit.Pixel, ia);
        }
        float radius = Math.Min(r.Width, r.Height) * 0.085f;
        if (_running.Contains(game))
            using (var dot = new SolidBrush(Green)) g.FillEllipse(dot, r.Right - 22 * u, r.Bottom - 22 * u, 12 * u, 12 * u);
        if (on)
        {
            var ring = RectangleF.Inflate(r, 3 * u, 3 * u);
            using var path = Rounded(ring, radius + 3 * u);
            using var pen = new Pen(WithAlpha(Color.White, alpha), 4 * u);
            g.DrawPath(pen, path);
        }
    }

    Bitmap? LogoFor(Game g, int maxW, int maxH)
    {
        var path = g.LogoFile;
        if (path == null) return null;
        return Art($"logo|{g.Name}|{maxW}x{maxH}", () =>
        {
            // Pre-scaled to its on-screen size so drawing it (even faded) is cheap.
            using var img = LoadImage(path);
            float s = Math.Min((float)maxW / img.Width, (float)maxH / img.Height);
            int w = Math.Max(1, (int)(img.Width * s)), h = Math.Max(1, (int)(img.Height * s));
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using var gr = Graphics.FromImage(bmp);
            gr.InterpolationMode = InterpolationMode.HighQualityBicubic;
            gr.DrawImage(img, 0, 0, w, h);
            return (bmp, false);
        });
    }

    /// <summary>A finished "Recently played" card: art, rounded corners, caption gradient and name.</summary>
    Bitmap? CardArt(Game game, Size size)
    {
        string? hero = game.HeroFile, cover = game.IsIcon ? null : game.ImagePath;
        string? iconExe = game.IconExe;
        string name = game.Name;
        float u = U;
        return Art($"card|{name}|{cover}|{size.Width}x{size.Height}", () =>
        {
            using var content = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            var r = new RectangleF(0, 0, size.Width, size.Height);
            using (var gr = Graphics.FromImage(content))
            {
                gr.InterpolationMode = InterpolationMode.HighQualityBilinear;
                gr.SmoothingMode = SmoothingMode.AntiAlias;
                gr.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                if (hero != null || cover != null)
                {
                    using var img = LoadImage(hero ?? cover!);
                    DrawCover(gr, img, r, hero != null ? 0.3f : 0.25f);
                }
                else
                {
                    int hue = StableHue(name);
                    using (var fb = new LinearGradientBrush(r, FromHue(hue, 0.45f), FromHue((hue + 40) % 360, 0.25f), 30f))
                        gr.FillRectangle(fb, r);
                    if (iconExe != null && File.Exists(iconExe))
                        using (var icon = Icon.ExtractAssociatedIcon(iconExe)?.ToBitmap())
                            if (icon != null)
                            {
                                float s = r.Height * 0.42f;
                                gr.DrawImage(icon, (r.Width - s) / 2, r.Height * 0.16f, s, s);
                            }
                }
                using (var cap = new LinearGradientBrush(new RectangleF(0, r.Height - 80 * u, r.Width, 80 * u), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(200, 0, 0, 0), 90f))
                    gr.FillRectangle(cap, 0, r.Height - 79 * u, r.Width, 79 * u);
                // Fonts are per-thread here: GDI+ objects aren't shared with the UI thread.
                using var font = new Font("Segoe UI", 20 * u, GraphicsUnit.Pixel);
                using var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                gr.DrawString(name, font, Brushes.White, new RectangleF(16 * u, r.Height - 42 * u, r.Width - 32 * u, 30 * u), sf);
            }
            var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using (var gr = Graphics.FromImage(bmp))
            using (var brush = new TextureBrush(content))
            using (var path = Rounded(r, 12 * u))
            {
                gr.SmoothingMode = SmoothingMode.AntiAlias;
                gr.FillPath(brush, path);
            }
            // Cover/icon stand-ins get replaced once a hero background downloads.
            return (bmp, hero == null && game.HeroUrls.Count > 0);
        });
    }

    // ================= Painting =================

    void PaintHome(Graphics g)
    {
        float u = U, W = ClientSize.Width, H = ClientSize.Height;
        float margin = 64 * u;
        double now = NowMs;
        var list = TabGames();
        var sel = FocusedGame();
        PaintBackdrop(g, sel);

        PaintTopBar(g, margin);

        if (_tab == TabFriends)
        {
            PaintFriends(g, margin, now);
            PaintEnterFade(g, now);
            PaintIntro(g, now);
            return;
        }

        if (list.Count == 0)
        {
            g.DrawString(_tab == 1 ? "No apps yet" : "No games found", F(56, FontStyle.Regular, "Segoe UI Light"), Brushes.White, margin, H * 0.42f);
            using var db = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
            g.DrawString(_tab == 1 ? "Add apps in Settings › Add an app" : "Install games with Steam, Epic, GOG and more, then press X to scan",
                F(24), db, margin + 4 * u, H * 0.42f + 90 * u);
            PaintHints(g, new[] { ("LB RB", Tabs[(_tab + 1) % Tabs.Length]), ("X", "Scan"), ("≡", "Settings"), ("VIEW", "Power") }, null);
            PaintEnterFade(g, now);
            PaintIntro(g, now);
            return;
        }

        // ---- Cover row: sizes and positions follow the animated selection, so the row glides
        //      and the selected cover grows smoothly at any refresh rate ----
        float sw = 112 * u, bw = 160 * u, gap = 14 * u, rowY = 96 * u;
        float intro = EaseOutCubic(Progress(Math.Max(_rowIntroAt, _homeEnterAt), 420, now));
        float rowShift = (1 - intro) * 50 * u;
        float x = margin - _selAnim * (sw + gap) + rowShift;
        for (int i = 0; i < list.Count; i++)
        {
            float big = Clamp01(1 - MathF.Abs(i - _selAnim));
            float w = Lerp(sw, bw, big);
            var r = new RectangleF(x, rowY, w, w * 1.5f);
            x += w + gap;
            if (r.Right < -10 || r.X > W) continue;
            DrawTile(g, list[i], r, i == _sel && _homeFocus == FocusRow && big > 0.5f, intro < 1 ? intro : 1f, resting: big is 0f or 1f,
                glow: i == _sel && _homeFocus == FocusRow && big >= 1 ? _glowAnim : 0);
            int idx = i;
            _hits.Add((r, () => { if (_sel == idx && _homeFocus == FocusRow) LaunchOrResume(list[idx]); else { _sel = idx; _homeFocus = FocusRow; RequestFrames(); } }, null));
        }

        // ---- Focused game: info line, logo (or big title), actions; slides in when it changes ----
        if (sel != null)
        {
            if (sel != _infoGame) { _infoGame = sel; _infoAt = now; RequestFrames(); }
            float t = EaseOutCubic(Progress(_infoAt, 320, now));
            float ox = (1 - t) * 26 * u, a = t;
            float y = H * 0.345f;
            bool running = _running.Contains(sel);
            string ctl = ControllerLabel(sel);
            string info = running ? "Running now" : $"{SourceName(sel)}  ·  {LastPlayedText(sel)}" + (ctl != "" ? $"  ·  {ctl}" : "");
            using (var ib = new SolidBrush(WithAlpha(running ? Green : Color.FromArgb(215, 255, 255, 255), a)))
                g.DrawString(info, F(24), ib, margin + ox, y);
            y += 48 * u;

            float maxW = 560 * u, maxH = 170 * u;
            var logo = LogoFor(sel, (int)maxW, (int)maxH);
            if (logo != null)
            {
                var dest = new RectangleF(margin + ox, y + (maxH - logo.Height), logo.Width, logo.Height);
                if (a >= 0.995f) g.DrawImageUnscaled(logo, (int)dest.X, (int)dest.Y);
                else
                {
                    using var ia = new ImageAttributes();
                    ia.SetColorMatrix(new ColorMatrix { Matrix33 = a });
                    g.DrawImage(logo, Rectangle.Round(dest), 0, 0, logo.Width, logo.Height, GraphicsUnit.Pixel, ia);
                }
                y += maxH + 30 * u;
            }
            else
            {
                var titleFont = F(68, FontStyle.Regular, "Segoe UI Light");
                var titleRect = new RectangleF(margin - 4 * u + ox, y, W * 0.55f, titleFont.Height * 2.1f);
                using var sf = new StringFormat { Trimming = StringTrimming.EllipsisWord };
                var measured = g.MeasureString(sel.Name, titleFont, (int)titleRect.Width, sf);
                using (var tb = new SolidBrush(WithAlpha(Color.White, a))) g.DrawString(sel.Name, titleFont, tb, titleRect, sf);
                y += Math.Min(measured.Height, titleRect.Height) + 26 * u;
            }

            var play = new RectangleF(margin, y, 230 * u, 66 * u);
            float fp = Glow("play");
            var playShown = RectangleF.Inflate(play, 6 * u * fp, 3 * u * fp);
            using (var pp = Rounded(playShown, playShown.Height / 2))
            using (var pb = new SolidBrush(Color.White)) g.FillPath(pb, pp);
            if (fp > 0.01f)
                using (var ring = Rounded(RectangleF.Inflate(playShown, 6 * u, 6 * u), playShown.Height / 2 + 6 * u))
                using (var pen = new Pen(Color.FromArgb((int)(230 * fp), 255, 255, 255), 3 * u)) g.DrawPath(pen, ring);
            {
                string label = running ? "Resume" : "Play";
                var lf = F(26, FontStyle.Bold);
                float iconS = 24 * u, gapS = 10 * u, labelW = g.MeasureString(label, lf, PointF.Empty, StringFormat.GenericTypographic).Width;
                float gx = play.X + (play.Width - (iconS + gapS + labelW)) / 2;
                Icons.Play(g, new RectangleF(gx, play.Y + (play.Height - iconS) / 2, iconS, iconS), Color.Black);
                Icons.TextCentered(g, label, lf, Color.Black, new RectangleF(gx + iconS + gapS, play.Y, labelW, play.Height));
            }
            _hits.Add((play, () => LaunchOrResume(sel), null));

            var more = new RectangleF(play.Right + 18 * u, y, 66 * u, 66 * u);
            float fm = Glow("more");
            var moreShown = RectangleF.Inflate(more, 3 * u * fm, 3 * u * fm);
            using (var mb = new SolidBrush(Color.FromArgb((int)(70 + 110 * fm), 255, 255, 255))) g.FillEllipse(mb, moreShown);
            if (fm > 0.01f)
                using (var pen = new Pen(Color.FromArgb((int)(230 * fm), 255, 255, 255), 3 * u)) g.DrawEllipse(pen, RectangleF.Inflate(moreShown, 6 * u, 6 * u));
            Icons.Dots(g, RectangleF.Inflate(more, -14 * u, -14 * u), Color.White);
            _hits.Add((more, () => OpenGameOptions(sel), null));
        }

        PaintCards(g, margin, now);

        bool selRunning = sel != null && _running.Contains(sel);
        string aLabel = _homeFocus switch
        {
            FocusTop => _topSel < Tabs.Length ? Tabs[_topSel] : (_topSel - Tabs.Length) switch { 0 => "Controller", 1 => "Settings", _ => "Power" },
            FocusActions when _actionSel == 1 => "Options",
            _ => selRunning ? "Resume" : "Play",
        };
        var hints = new List<(string, string)> { ("A", aLabel) };
        if (_homeFocus != FocusTop) hints.Add(("Y", "Options"));
        else hints.Add(("B", "Back"));
        if (_running.Count > 0) hints.Add(("GUIDE", "Back to game"));
        hints.Add(("LB RB", Tabs[(_tab + 1) % Tabs.Length]));
        hints.Add(("≡", "Settings"));
        PaintHints(g, hints.ToArray(), null);
        PaintEnterFade(g, now);
        PaintIntro(g, now);
    }

    /// <summary>Coming from the selector the screen is black; fade the home screen in from it.</summary>
    void PaintEnterFade(Graphics g, double now)
    {
        float fade = 1 - EaseOutCubic(Progress(_homeEnterAt, 520, now));
        if (fade > 0.01f)
            using (var black = new SolidBrush(Color.FromArgb((int)(255 * fade), 0, 0, 0))) g.FillRectangle(black, ClientRectangle);
    }

    void PaintCards(Graphics g, float margin, double now)
    {
        var cards = RecentGames();
        if (cards.Count == 0) return;
        float u = U, W = ClientSize.Width, H = ClientSize.Height;
        float intro = EaseOutCubic(Progress(_homeEnterAt + 120, 480, now));
        float cw = 340 * u, ch = cw * 9 / 16, gap = 20 * u, y = H - ch - 104 * u + (1 - intro) * 60 * u;
        g.DrawString(_settings.LastPlayed.Count > 0 ? "Recently played" : "Your library", F(22, FontStyle.Bold), Brushes.White, margin, y - 40 * u);

        _cardSel = Math.Clamp(_cardSel, 0, cards.Count - 1);
        int fit = Math.Max(1, (int)((W - margin) / (cw + gap)));
        float first = Math.Max(0, _cardSelAnim - fit + 1);
        var size = new Size((int)cw, (int)ch);
        for (int i = 0; i < cards.Count; i++)
        {
            float x = margin + (i - first) * (cw + gap);
            if (x + cw < 0) continue;
            if (x > W) break;
            var game = cards[i];
            // The focused card lifts slightly; the lift follows focus smoothly.
            float lift = Clamp01(1 - MathF.Abs(i - _cardSelAnim)) * _cardFocusAnim;
            var r = new RectangleF(x, y, cw, ch);
            var shown = RectangleF.Inflate(r, cw * 0.03f * lift, ch * 0.03f * lift);
            shown.Y -= 6 * u * lift;
            int csp = (int)(22 * u);
            if (lift > 0.01f)
            {
                var cg = FxBitmap($"cardglow|{game.Name}|{size.Width}", () =>
                    Fx.SoftRect(size.Width, size.Height, 12 * u, (int)(44 * u), Color.FromArgb(220, AccentFor(game))));
                float kx = shown.Width / size.Width, gsp = 44 * u * kx;
                DrawFaded(g, cg, RectangleF.Inflate(shown, gsp, gsp), lift * 0.85f);
            }
            var cshadow = FxBitmap($"cardshadow|{size.Width}", () => Fx.SoftRect(size.Width, size.Height, 12 * u, csp, Color.FromArgb(160, 0, 0, 0)));
            DrawFaded(g, cshadow, new RectangleF(shown.X - csp, shown.Y - csp + 10 * u, shown.Width + csp * 2, shown.Height + csp * 2), 1);
            var art = CardArt(game, size);
            if (art != null && lift < 0.005f) g.DrawImageUnscaled(art, (int)MathF.Round(r.X), (int)MathF.Round(r.Y));
            else if (art != null) g.DrawImage(art, shown);
            else
            {
                using var path = Rounded(shown, 12 * u);
                using var fb = new SolidBrush(Color.FromArgb(40, 255, 255, 255));
                g.FillPath(fb, path);
            }
            if (_running.Contains(game))
                using (var dot = new SolidBrush(Green)) g.FillEllipse(dot, shown.Right - 26 * u, shown.Y + 14 * u, 12 * u, 12 * u);
            if (lift > 0.02f)
            {
                using var ring = Rounded(RectangleF.Inflate(shown, 2 * u, 2 * u), 14 * u);
                using var pen = new Pen(WithAlpha(Color.White, lift), 4 * u);
                g.DrawPath(pen, ring);
            }
            int idx = i;
            _hits.Add((r, () => LaunchOrResume(cards[idx]), null));
        }
    }

    // ================= Background Steam matching (Playnite's "download metadata") =================

    bool _matching;

    /// <summary>
    /// Matches non-Steam games to Steam apps once (exact title match), so they get Steam's HD cover,
    /// hero background and logo, like Playnite's metadata download. Results are remembered in settings.
    /// </summary>
    async void MatchSteamArtInBackground()
    {
        if (_matching) return;
        var todo = _games.Where(g => g.SteamAppId == null && !g.IsApp && !_settings.SteamIds.ContainsKey(g.Name)).Select(g => g.Name).ToList();
        if (todo.Count == 0) return;
        _matching = true;
        var found = new Dictionary<string, string>();
        try
        {
            foreach (var name in todo)
            {
                var match = Artwork.BestMatch(name, (await Artwork.SearchAsync(name, _settings)).Where(r => r.Source == "Steam" && r.Url == null).ToList());
                found[name] = match?.Id ?? "";
            }
        }
        catch (Exception ex) { Log.Write("Steam matching: " + ex.Message); }
        finally { _matching = false; }

        _settings = Settings.Load();
        foreach (var (name, id) in found) _settings.SteamIds.TryAdd(name, id);
        _settings.Save();
        if (found.Values.Any(v => v != ""))
        {
            ReloadLibrary(announce: false);
            Log.Write($"Steam art matched: {string.Join(", ", found.Where(kv => kv.Value != "").Select(kv => kv.Key))}");
        }
    }

    // ================= Diagnostics =================

    bool _preview;

    /// <summary>Diagnostic (--render): paints a screen off-screen, saves it and logs the frame time.</summary>
    static double IntroPreviewMs(string what) => what.Length > 6 ? double.Parse(what[6..]) : 2200;

    public void RenderForPreview(string path, Size size, string what)
    {
        _preview = true; // keeps StatusTick from hiding the real taskbar while this runs
        CreateControl();
        Bounds = new Rectangle(new Point(-size.Width * 2, 0), size);
        _mode = Mode.Console;
        _settings = Settings.Load();
        _games = GameLibrary.Load(_settings);
        _view = View.Home;
        if (what == "match") MatchSteamArtInBackground();
        if (what == "cards") _homeFocus = FocusCards;
        if (what == "friends") _tab = TabFriends;
        if (what == "play") { _homeFocus = FocusActions; _actionSel = 0; _focusGlow["play"] = 1; }
        if (what == "top") { _homeFocus = FocusTop; _topSel = Tabs.Length + 1; _focusGlow[$"top{Tabs.Length + 1}"] = 1; }
        if (what == "options" && _games.Count > 0) { _optGame = _games[0]; _page = _rootPage = Page.GameOptions; _view = View.Menu; }
        if (what.Length > 3 && what.StartsWith("sel") && char.IsDigit(what[3])) _sel = int.Parse(what[3..]);
        if (what.StartsWith("selector"))
        {
            // "selector" or "selector:<theme>" (the theme isn't saved).
            _view = View.Selector; _selectorAt = -1e9; _selectorHover[0] = 1;
            if (what.Length > 9) _settings.SelectorTheme = what[9..];
        }
        var until = Environment.TickCount64 + (what == "match" ? 25000 : 8000);
        using var bmp = new Bitmap(size.Width, size.Height);
        while (Environment.TickCount64 < until)
        {
            DrawToBitmap(bmp, new Rectangle(Point.Empty, size));
            Application.DoEvents();
            Thread.Sleep(150);
        }
        _selAnim = _sel; _cardSelAnim = _cardSel; _cardFocusAnim = _homeFocus == FocusCards ? 1 : 0; _glowAnim = 1;
        _infoAt = _bgDipAt = _homeEnterAt = _rowIntroAt = -1e9;
        if (what.StartsWith("intro")) _introAt = NowMs - IntroPreviewMs(what);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) DrawToBitmap(bmp, new Rectangle(Point.Empty, size));
        Log.Write($"render: {sw.Elapsed.TotalMilliseconds / 20:F1} ms per frame at {size.Width}x{size.Height} (incl. capture)");
        // Paint-only timing, split by part, straight into a buffer like the real double-buffered paint.
        // Same kind of buffer WinForms double-buffering paints into (GDI-backed), so GDI calls cost what they really cost.
        using (var screen = CreateGraphics())
        using (var buffered = BufferedGraphicsManager.Current.Allocate(screen, new Rectangle(Point.Empty, size)))
        {
            var g = buffered.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.Bilinear;
            double T(Action a) { a(); var t = System.Diagnostics.Stopwatch.StartNew(); for (int i = 0; i < 20; i++) { _tileBakesThisFrame = 0; a(); } return t.Elapsed.TotalMilliseconds / 20; }
            Log.Write($"paint parts: total {T(() => { _hits.Clear(); PaintHome(g); }):F1} ms | backdrop {T(() => PaintBackdrop(g, FocusedGame())):F1} | " +
                      $"topbar {T(() => PaintTopBar(g, 64 * U)):F1} | cards {T(() => PaintCards(g, 64 * U, NowMs)):F1}");
            // Mid-animation frame: selection halfway between two tiles, info and background fading in.
            _selAnim = _sel + 0.5f; _infoAt = NowMs; _bgDipAt = NowMs;
            Log.Write($"paint while animating: {T(() => { _hits.Clear(); PaintHome(g); }):F1} ms");
            _selAnim = _sel; _infoAt = _bgDipAt = -1e9;
            _selAnim = _sel + 0.5f;
            Log.Write($"  only row moving: {T(() => { _hits.Clear(); PaintHome(g); }):F1} ms");
            _selAnim = _sel; _infoAt = NowMs; _infoGame = FocusedGame();
            Log.Write($"  only info fading: {T(() => { _hits.Clear(); PaintHome(g); }):F1} ms");
            _infoAt = -1e9; _bgDipAt = NowMs; _bgPrev = ScreenBackground(null); // crossfade from the plain backdrop
            Log.Write($"  only background dip: {T(() => { _hits.Clear(); PaintHome(g); }):F1} ms");
            _bgDipAt = -1e9;
            _view = View.Menu; _page = _rootPage = Page.Settings; _menuOpenAt = NowMs; _menuBackdropDirty = true;
            Log.Write($"menu slide-in frame: {T(() => { _hits.Clear(); PaintMenuBackdrop(g); PaintMenu(g); }):F1} ms");
            _view = View.Selector; _selectorAt = NowMs;
            Log.Write($"selector entrance frame: {T(() => { _hits.Clear(); PaintSelector(g); }):F1} ms");
            _view = what.StartsWith("selector") ? View.Selector : View.Home;
        }
        if (what.StartsWith("intro")) { _view = View.Home; _introAt = NowMs - IntroPreviewMs(what); DrawToBitmap(bmp, new Rectangle(Point.Empty, size)); }
        else if (what == "options") { _view = View.Menu; _page = _rootPage = Page.GameOptions; _menuOpenAt = _pageAt = -1e9; _menuBackdropDirty = true; DrawToBitmap(bmp, new Rectangle(Point.Empty, size)); }
        else
        {
            _selectorAt = -1e9; // fully entered
            DrawToBitmap(bmp, new Rectangle(Point.Empty, size));
        }
        bmp.Save(path, ImageFormat.Png);
    }

    static string SourceName(Game g) => g.IsApp ? "App" : !string.IsNullOrEmpty(g.Store) ? g.Store : g.Source switch
    {
        GameSource.Steam => "Steam",
        GameSource.Epic => "Epic Games",
        _ => "PC game",
    };

    string LastPlayedText(Game g)
    {
        if (!_settings.LastPlayed.TryGetValue(g.Name, out var t)) return "Not played yet";
        var days = (DateTime.Now.Date - t.Date).Days;
        return days switch
        {
            0 => "Last played today",
            1 => "Last played yesterday",
            < 7 => $"Last played {days} days ago",
            _ => $"Last played {t:d MMM yyyy}",
        };
    }
}
