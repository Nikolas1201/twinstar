using System.Drawing.Drawing2D;

namespace Twinstar;

/// <summary>Per-game options (Triangle/Y on a tile): remove, exclude, and artwork search with an on-screen keyboard.</summary>
sealed partial class MainForm
{
    Game? _optGame;

    void OpenGameOptions(Game g)
    {
        _optGame = g;
        OpenMenu(Page.GameOptions);
    }

    /// <summary>After a reload the library holds new Game objects; point the options page at the fresh one.</summary>
    void RefreshOptGame()
    {
        if (_optGame == null) return;
        _optGame = GameLibrary.Load(_settings, includeHidden: true)
            .FirstOrDefault(g => g.Name.Equals(_optGame.Name, StringComparison.OrdinalIgnoreCase)) ?? _optGame;
    }

    bool IsCustom(Game g) =>
        _settings.CustomGames.Any(c => c.Name.Equals(g.Name, StringComparison.OrdinalIgnoreCase));

    List<MenuItem> GameOptionItems()
    {
        var g = _optGame!;
        bool running = _running.Contains(g);
        bool custom = IsCustom(g);
        var items = new List<MenuItem> { new(running ? "Resume" : "Play", () => LaunchOrResume(g)) };
        if (running) items.Add(new("Close game", () => { _active = g; CloseActiveGame(); }));
        items.Add(new("Find artwork", () => StartArtSearch(g.Name, push: true), g.IsIcon ? "No art" : null));
        if (g.CustomArt) items.Add(new("Reset artwork", () => ResetArtwork(g)));
        items.Add(ControllerModeItem(g));
        items.Add(new("Remove from library", () => RemoveGame(g), custom ? "Deletes it" : "Until next scan"));
        if (!custom) items.Add(new("Exclude from scans", () => ExcludeGame(g), "Never comes back"));
        items.Add(new("Back", PopPage));
        return items;
    }

    void RemoveGame(Game g)
    {
        var custom = _settings.CustomGames.FirstOrDefault(c => c.Name.Equals(g.Name, StringComparison.OrdinalIgnoreCase));
        if (custom != null) _settings.CustomGames.Remove(custom);
        else if (!_settings.Removed.Contains(g.Name, StringComparer.OrdinalIgnoreCase)) _settings.Removed.Add(g.Name);
        _settings.Save();
        ReloadLibrary(announce: false);
        _view = View.Home;
        Toast(custom != null ? $"Removed {g.Name}" : $"Removed {g.Name} (a scan brings it back)");
    }

    void ExcludeGame(Game g)
    {
        if (!_settings.Hidden.Contains(g.Name, StringComparer.OrdinalIgnoreCase)) _settings.Hidden.Add(g.Name);
        _settings.Removed.RemoveAll(r => r.Equals(g.Name, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        ReloadLibrary(announce: false);
        _view = View.Home;
        Toast($"{g.Name} excluded. Undo in Settings › Manage library");
    }

    void ResetArtwork(Game g)
    {
        _settings.Artwork.Remove(g.Name);
        _settings.Save();
        ReloadLibrary(announce: false);
        RefreshOptGame();
        Toast("Artwork reset");
    }

    // ================= Artwork search =================

    string _artQuery = "";
    List<Artwork.Result> _artResults = new();
    bool _artBusy;
    readonly Dictionary<Artwork.Result, Bitmap?> _previews = new();
    readonly Dictionary<Artwork.Result, byte[]> _previewBytes = new();
    readonly HashSet<Artwork.Result> _previewLoading = new();

    async void StartArtSearch(string query, bool push)
    {
        _artQuery = query.Trim();
        _artResults = new();
        _artBusy = true;
        if (push) PushPage(Page.ArtResults);
        _menuIndex = 0;
        Invalidate();
        try
        {
            var results = await Artwork.SearchAsync(_artQuery, _settings, _optGame);
            if (_artQuery != query.Trim()) return; // a newer search replaced this one
            _artResults = results;
            if (results.Count > 0 && _page == Page.ArtResults) _menuIndex = 1; // first real result
        }
        catch (Exception ex) { Log.Write("Art search: " + ex.Message); }
        finally
        {
            _artBusy = false;
            Invalidate();
        }
    }

    List<MenuItem> ArtResultItems()
    {
        var items = new List<MenuItem> { new("Search a different name…", OpenKeyboard, $"\"{_artQuery}\"") };
        if (_artBusy) items.Add(new("Searching…", () => { }));
        else if (_artResults.Count == 0) items.Add(new("No matches. Try a different name", OpenKeyboard));
        else items.AddRange(_artResults.Select(r => new MenuItem(r.Name, () => ApplyArt(r), r.Source)));
        return items;
    }

    Artwork.Result? SelectedArtResult() =>
        !_artBusy && _menuIndex >= 1 && _menuIndex - 1 < _artResults.Count ? _artResults[_menuIndex - 1] : null;

    async void LoadPreview(Artwork.Result r)
    {
        if (!_previewLoading.Add(r)) return;
        Bitmap? bmp = null;
        try
        {
            var bytes = await Artwork.FetchCoverAsync(r, _settings);
            if (bytes != null)
            {
                _previewBytes[r] = bytes;
                using var ms = new MemoryStream(bytes);
                using var img = Image.FromStream(ms);
                bmp = new Bitmap(img);
            }
        }
        catch (Exception ex) { Log.Write("Preview: " + ex.Message); }
        _previews[r] = bmp;
        _previewLoading.Remove(r);
        Invalidate();
    }

    async void ApplyArt(Artwork.Result r)
    {
        var g = _optGame;
        if (g == null) return;
        Toast("Downloading artwork…");
        if (!_previewBytes.TryGetValue(r, out var bytes)) bytes = await Artwork.FetchCoverAsync(r, _settings) ?? Array.Empty<byte>();
        if (bytes.Length == 0) { Toast("That result has no artwork. Try another"); return; }
        _settings.Artwork[g.Name] = Artwork.Save(g.Name, bytes);
        // Picking a Steam result also brings that game's background and logo, Playnite-style.
        if (r.Source == "Steam" && r.Url == null && g.Source != GameSource.Steam) _settings.SteamIds[g.Name] = r.Id;
        _settings.Save();
        ReloadLibrary(announce: false);
        RefreshOptGame();
        if (_page == Page.ArtResults) PopPage();
        Toast($"Artwork updated for {g.Name}");
    }

    void PaintArtPreview(Graphics g, float panelW)
    {
        float u = U, W = ClientSize.Width, H = ClientSize.Height;
        float space = W - panelW;
        float ch = Math.Min(H * 0.62f, space * 0.75f * 1.5f), cw = ch / 1.5f;
        var rect = new RectangleF(panelW + (space - cw) / 2, (H - ch) / 2 - 30 * u, cw, ch);
        var r = SelectedArtResult();
        using var path = Rounded(rect, 16 * u);
        using (var bg = new SolidBrush(Color.FromArgb(25, 255, 255, 255))) g.FillPath(bg, path);

        string? caption = null;
        if (r == null) caption = _artBusy ? "Searching…" : "Pick a result to preview";
        else if (!_previews.TryGetValue(r, out var bmp)) { caption = "Loading preview…"; LoadPreview(r); }
        else if (bmp == null) caption = "No artwork for this one";
        else
        {
            var state = g.Save();
            g.SetClip(path);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            float scale = Math.Max(rect.Width / bmp.Width, rect.Height / bmp.Height);
            float w = bmp.Width * scale, h = bmp.Height * scale;
            g.DrawImage(bmp, rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h);
            g.Restore(state);
            using var pen = new Pen(Color.White, 3 * u);
            g.DrawPath(pen, path);
        }
        if (caption != null) DrawCentered(g, caption, F(24), Dim, rect.X + rect.Width / 2, rect.Y + rect.Height / 2 - 16 * u);
        if (r != null) DrawCentered(g, $"{r.Name}  ·  {r.Source}", F(22), Color.White, rect.X + rect.Width / 2, rect.Bottom + 20 * u);
    }

    // ---- Automatic: best match for every game without art ----

    bool _fillingArt;

    async void FillMissingArtwork()
    {
        if (_fillingArt) { Toast("Already looking for artwork…"); return; }
        var missing = _games.Where(g => g.IsIcon).ToList();
        if (missing.Count == 0) { Toast("Every game already has artwork"); return; }
        _fillingArt = true;
        var found = new Dictionary<string, string>();
        var foundIds = new Dictionary<string, string>();
        try
        {
            for (int i = 0; i < missing.Count; i++)
            {
                var name = missing[i].Name;
                Toast($"Finding artwork {i + 1}/{missing.Count}: {name}");
                var match = Artwork.BestMatch(name, await Artwork.SearchAsync(name, _settings, missing[i]));
                if (match == null) continue;
                var bytes = await Artwork.FetchCoverAsync(match, _settings);
                if (bytes != null) found[name] = Artwork.Save(name, bytes);
                if (match.Source == "Steam" && match.Url == null) foundIds[name] = match.Id;
            }
        }
        catch (Exception ex) { Log.Write("Fill artwork: " + ex.Message); }
        finally { _fillingArt = false; }

        // Merge into freshly loaded settings so changes made meanwhile aren't overwritten.
        _settings = Settings.Load();
        foreach (var (name, path) in found) _settings.Artwork[name] = path;
        foreach (var (name, id) in foundIds) _settings.SteamIds[name] = id;
        _settings.Save();
        ReloadLibrary(announce: false);
        int missed = missing.Count - found.Count;
        Toast(missed == 0 ? $"Added artwork for all {found.Count} games"
            : $"Added artwork for {found.Count} of {missing.Count}. Use {ButtonLabel("Y")} › Find artwork for the rest");
    }

    // ================= On-screen keyboard =================

    static readonly string[] KeyRows = { "1234567890", "QWERTYUIOP", "ASDFGHJKL'", "ZXCVBNM-:." };
    static readonly string[] ActionKeys = { "Space", "Delete", "Clear", "Search" };
    string _kbText = "";
    int _kbRow = 1, _kbCol;
    /// <summary>What the on-screen keyboard is typing for: artwork search or the Add an app filter.</summary>
    bool _kbForApps;
    string _appFilter = "";

    void OpenAppSearch()
    {
        _kbForApps = true;
        _kbText = _appFilter;
        _kbRow = 1;
        _kbCol = 0;
        PushPage(Page.Keyboard);
    }

    void OpenKeyboard()
    {
        _kbForApps = false;
        _kbText = _artQuery;
        _kbRow = 1;
        _kbCol = 0;
        PushPage(Page.Keyboard);
    }

    int RowLength(int row) => row < KeyRows.Length ? KeyRows[row].Length : ActionKeys.Length;

    void KeyboardInput(Btn b)
    {
        int rows = KeyRows.Length + 1;
        switch (b)
        {
            case Btn.Left: _kbCol = (_kbCol + RowLength(_kbRow) - 1) % RowLength(_kbRow); break;
            case Btn.Right: _kbCol = (_kbCol + 1) % RowLength(_kbRow); break;
            case Btn.Up:
            case Btn.Down:
                int from = RowLength(_kbRow);
                _kbRow = (_kbRow + (b == Btn.Up ? rows - 1 : 1)) % rows;
                _kbCol = Math.Min(RowLength(_kbRow) - 1, _kbCol * RowLength(_kbRow) / from);
                break;
            case Btn.A: PressKey(_kbRow, _kbCol); break;
            case Btn.X: Backspace(); break;
            case Btn.Y: TypeChar(' '); break;
            case Btn.Start: SubmitSearch(); break;
            case Btn.B: PopPage(); break;
        }
        Invalidate();
    }

    void PressKey(int row, int col)
    {
        if (row < KeyRows.Length) { TypeChar(KeyRows[row][col]); return; }
        switch (ActionKeys[col])
        {
            case "Space": TypeChar(' '); break;
            case "Delete": Backspace(); break;
            case "Clear": _kbText = ""; break;
            default: SubmitSearch(); break;
        }
    }

    void TypeChar(char c)
    {
        if (_kbText.Length >= 60) return;
        // Capitalize the start of each word, lowercase the rest (looks like a normal title).
        bool wordStart = _kbText.Length == 0 || _kbText[^1] == ' ';
        _kbText += wordStart ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c);
        Invalidate();
    }

    void Backspace()
    {
        if (_kbText.Length > 0) _kbText = _kbText[..^1];
        Invalidate();
    }

    void SubmitSearch()
    {
        if (_kbForApps)
        {
            // An empty search simply shows every app again.
            _appFilter = _kbText.Trim();
            PopPage();
            _menuIndex = 0;
            return;
        }
        if (string.IsNullOrWhiteSpace(_kbText)) { Toast("Type a name first"); return; }
        PopPage(); // back to the results page
        StartArtSearch(_kbText, push: false);
    }

    /// <summary>Lets a real keyboard type into the search box. Returns true when the key belongs to it.</summary>
    bool PhysicalKeyboardKey(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter: SubmitSearch(); return true;
            case Keys.Escape: PopPage(); return true;
            case Keys.Back: Backspace(); return true;
            case Keys.Up: KeyboardInput(Btn.Up); return true;
            case Keys.Down: KeyboardInput(Btn.Down); return true;
            case Keys.Left: KeyboardInput(Btn.Left); return true;
            case Keys.Right: KeyboardInput(Btn.Right); return true;
        }
        return !e.Control && !e.Alt; // printable keys arrive in OnKeyPress
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (_view == View.Menu && _page == Page.Keyboard && !char.IsControl(e.KeyChar))
        {
            if (_kbText.Length < 60) _kbText += e.KeyChar; // real keyboards type exactly what's pressed
            e.Handled = true;
            Invalidate();
        }
    }

    void PaintKeyboard(Graphics g, float x, float y, float panelW)
    {
        float u = U;
        float avail = panelW - 100 * u, gap = 12 * u, keyH = 76 * u;

        var box = new RectangleF(x - 20 * u, y, avail, 84 * u);
        using (var bp = Rounded(box, 14 * u))
        {
            using var bb = new SolidBrush(Color.FromArgb(11, 15, 26));
            g.FillPath(bb, bp);
            using var pen = new Pen(Accent, 3 * u);
            g.DrawPath(pen, bp);
        }
        bool caret = Environment.TickCount64 / 500 % 2 == 0;
        using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(_kbText + (caret ? "|" : " "), F(32, FontStyle.Bold), Brushes.White,
                new RectangleF(box.X + 24 * u, box.Y, box.Width - 48 * u, box.Height), sf);
        y += box.Height + 40 * u;

        for (int row = 0; row <= KeyRows.Length; row++)
        {
            int count = RowLength(row);
            float kw = (avail - gap * (count - 1)) / count;
            for (int col = 0; col < count; col++)
            {
                var r = new RectangleF(x - 20 * u + col * (kw + gap), y, kw, keyH);
                bool on = row == _kbRow && col == _kbCol;
                using (var kp = Rounded(r, 12 * u))
                {
                    using var kb = new SolidBrush(on ? Color.FromArgb(37, 56, 100) : Color.FromArgb(30, 41, 59));
                    g.FillPath(kb, kp);
                    if (on) { using var pen = new Pen(Color.White, 3 * u); g.DrawPath(pen, kp); }
                }
                string label = row < KeyRows.Length ? KeyRows[row][col].ToString() : ActionKeys[col];
                DrawCentered(g, label, F(row < KeyRows.Length ? 30 : 24, FontStyle.Bold),
                    row == KeyRows.Length && col == ActionKeys.Length - 1 ? Green : Color.White,
                    r.X + r.Width / 2, r.Y + r.Height / 2 - (row < KeyRows.Length ? 21 : 17) * u);
                int rr = row, cc = col;
                _hits.Add((r, () => { _kbRow = rr; _kbCol = cc; PressKey(rr, cc); Invalidate(); }, () => { _kbRow = rr; _kbCol = cc; }));
            }
            y += keyH + gap;
        }

        // The caret blinks via the status timer's twice-a-second repaint of menus.
        PaintHints(g, new[] { ("A", "Type"), ("X", "Delete"), ("Y", "Space"), ("≡", "Search"), ("B", "Cancel") }, null);
    }
}
