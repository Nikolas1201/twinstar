using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Twinstar;

/// <summary>
/// Friends tab: Steam friends with live presence (official Web API, the user's own key), sorted by
/// in-game / online / away / offline, with a detail panel. Messaging and joining hand over to the
/// Steam client (steam:// links), which shows its own window over the console; PS brings you back.
/// </summary>
sealed partial class MainForm
{
    List<SteamFriends.Friend> _friends = new();
    string? _friendsError;
    bool _friendsLoading;
    long _friendsLoadedAt;
    int _friendSel, _friendScroll;
    SteamFriends.Friend? _friendTarget;

    string? SteamIdForFriends => !string.IsNullOrEmpty(_settings.SteamId) ? _settings.SteamId : SteamFriends.LocalSteamId();

    /// <summary>Loads the friend list (at most every 30 s unless forced).</summary>
    async void RefreshFriends(bool force = false)
    {
        if (_friendsLoading || (!force && Environment.TickCount64 - _friendsLoadedAt < 30_000 && _friendsLoadedAt != 0)) return;
        var key = _settings.SteamApiKey?.Trim();
        var id = SteamIdForFriends;
        if (string.IsNullOrEmpty(key) || id == null) { _friendsError = null; return; }
        _friendsLoading = true;
        try
        {
            _friends = await SteamFriends.FetchAsync(key, id);
            _friendsError = null;
            _friendSel = Math.Clamp(_friendSel, 0, Math.Max(0, _friends.Count - 1));
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            _friendsError = "Steam refused the request: check the API key, and that your friends list is public (Steam › Profile › Privacy).";
        }
        catch (Exception ex) { _friendsError = "Couldn't load friends: " + ex.Message; }
        finally
        {
            _friendsLoading = false;
            _friendsLoadedAt = Environment.TickCount64;
            RequestFrames();
        }
    }

    bool FriendsInput(Btn b)
    {
        int n = _friends.Count;
        switch (b)
        {
            case Btn.Up when _friendSel == 0 || n == 0: _homeFocus = FocusTop; _topSel = TabFriends; return true;
            case Btn.Up: _friendSel--; return true;
            case Btn.Down when n > 0: _friendSel = Math.Min(n - 1, _friendSel + 1); return true;
            case Btn.A when n > 0: OpenFriend(_friends[_friendSel]); return true;
            case Btn.X: RefreshFriends(force: true); Toast("Refreshing friends…"); return true;
            case Btn.Y: OpenDiscord(); return true;
        }
        return false;
    }

    void OpenFriend(SteamFriends.Friend f)
    {
        _friendTarget = f;
        OpenMenu(Page.Friend);
    }

    List<MenuItem> FriendItems()
    {
        var f = _friendTarget;
        if (f == null) return new() { new("Back", PopPage) };
        var items = new List<MenuItem> { new("Message", () => SteamAction($"steam://friends/message/{f.Id}"), SteamFriends.StatusText(f)) };
        if (f.InGame) items.Add(new($"Join {f.Game}", () => SteamAction($"steam://friends/joingame/{f.Id}")));
        items.Add(new("View profile", () => SteamAction($"steam://url/SteamIDPage/{f.Id}")));
        items.Add(new("Open Steam friends & invites", () => SteamAction("steam://open/friends")));
        items.Add(new("Back", PopPage));
        return items;
    }

    /// <summary>Opens a Steam client window over the console; the PS/Guide button returns.</summary>
    async void SteamAction(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Toast("Couldn't open Steam: " + ex.Message); return; }
        _view = View.Home;
        TopMost = false; // let Steam's window appear on top
        await Task.Delay(900);
        var w = Native.TopOtherWindow(Handle);
        if (w != IntPtr.Zero) Native.ForceForeground(w);
        Toast($"Press {GuideName} to come back");
    }

    Bitmap? AvatarFor(SteamFriends.Friend f, int size)
    {
        var file = string.IsNullOrEmpty(f.Avatar) ? null : ArtCache.Get(f.Avatar);
        if (file == null) return null;
        return Art($"avatar|{f.Id}|{size}", () =>
        {
            // Circle-cropped once, so drawing it is a plain image copy.
            using var img = LoadImage(file);
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using var scaled = new Bitmap(img, size, size);
            using var brush = new TextureBrush(scaled);
            g.FillEllipse(brush, 0, 0, size, size);
            return (bmp, false);
        });
    }

    static Color PresenceColor(SteamFriends.Friend f) =>
        f.InGame ? Color.FromArgb(132, 204, 22) : f.State == 1 || f.State >= 5 ? Color.FromArgb(56, 189, 248) : f.State != 0 ? Color.FromArgb(234, 179, 8) : Color.FromArgb(100, 116, 139);

    void PaintFriends(Graphics g, float margin, double now)
    {
        float u = U, W = ClientSize.Width, H = ClientSize.Height;
        RefreshFriends();
        float y = 130 * u;
        int online = _friends.Count(f => f.Online);
        g.DrawString("Friends", F(52, FontStyle.Regular, "Segoe UI Light"), Brushes.White, margin - 4 * u, y);
        using (var sb = new SolidBrush(Dim))
            g.DrawString(_friends.Count > 0 ? $"{online} online  ·  {_friends.Count} on Steam" : "Steam", F(22), sb, margin, y + 76 * u);
        y += 140 * u;

        string? message = null;
        if (string.IsNullOrEmpty(_settings.SteamApiKey))
            message = "Connect Steam to see your friends:\n\n" +
                      "1.  Get a free Web API key at steamcommunity.com/dev/apikey\n" +
                      "2.  In Desktop mode run  Twinstar.exe --edit  and paste it as \"steamApiKey\"\n" +
                      "3.  Make your friends list public: Steam › Profile › Edit Profile › Privacy\n\n" +
                      $"Steam account found on this PC: {SteamIdForFriends ?? "none (sign in to Steam once)"}";
        else if (_friendsError != null) message = _friendsError;
        else if (_friends.Count == 0) message = _friendsLoading || _friendsLoadedAt == 0 ? "Loading friends…" : "No friends found on this Steam account.";
        if (message != null)
        {
            using var mb = new SolidBrush(Color.FromArgb(225, 255, 255, 255));
            g.DrawString(message, F(26), mb, new RectangleF(margin, y, W * 0.7f, H - y - 120 * u));
            PaintHints(g, new[] { ("X", "Refresh"), ("Y", "Discord"), ("LB RB", Tabs[(_tab + 1) % Tabs.Length]), ("≡", "Settings") }, null);
            return;
        }

        // ---- List on the left ----
        _friendSel = Math.Clamp(_friendSel, 0, _friends.Count - 1);
        float rowH = 84 * u, listW = Math.Min(760 * u, W * 0.45f);
        int visible = Math.Max(1, (int)((H - y - 110 * u) / rowH));
        if (_friendSel < _friendScroll) _friendScroll = _friendSel;
        if (_friendSel >= _friendScroll + visible) _friendScroll = _friendSel - visible + 1;
        bool listFocused = _homeFocus != FocusTop;
        for (int i = _friendScroll; i < Math.Min(_friends.Count, _friendScroll + visible); i++)
        {
            var f = _friends[i];
            var r = new RectangleF(margin - 16 * u, y + (i - _friendScroll) * rowH, listW, rowH - 8 * u);
            if (i == _friendSel && listFocused)
            {
                using var p = Rounded(r, 16 * u);
                using var hb = new SolidBrush(Color.FromArgb(40, 255, 255, 255));
                g.FillPath(hb, p);
                using var pen = new Pen(Color.FromArgb(225, 255, 255, 255), 2.5f * u);
                g.DrawPath(pen, p);
            }
            float a = 56 * u;
            var av = new RectangleF(r.X + 14 * u, r.Y + (r.Height - a) / 2, a, a);
            var bmp = AvatarFor(f, (int)a);
            if (bmp != null) g.DrawImageUnscaled(bmp, (int)av.X, (int)av.Y);
            else using (var ph = new SolidBrush(Color.FromArgb(50, 255, 255, 255))) g.FillEllipse(ph, av);
            // Presence dot with a dark rim, bottom-right of the avatar.
            var dot = new RectangleF(av.Right - 15 * u, av.Bottom - 15 * u, 16 * u, 16 * u);
            using (var rim = new SolidBrush(Shade)) g.FillEllipse(rim, RectangleF.Inflate(dot, 3 * u, 3 * u));
            using (var db = new SolidBrush(PresenceColor(f))) g.FillEllipse(db, dot);

            float tx = av.Right + 18 * u, tw = r.Right - tx - 14 * u;
            using var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            using (var nb = new SolidBrush(f.Online ? Color.White : Color.FromArgb(170, 255, 255, 255)))
                g.DrawString(f.Name, F(26, FontStyle.Bold), nb, new RectangleF(tx, r.Y + 10 * u, tw, 36 * u), sf);
            using (var sb2 = new SolidBrush(f.InGame ? PresenceColor(f) : Dim))
                g.DrawString(SteamFriends.StatusText(f), F(20), sb2, new RectangleF(tx, r.Y + 44 * u, tw, 30 * u), sf);
            int idx = i;
            _hits.Add((r, () => { _friendSel = idx; _homeFocus = FocusRow; OpenFriend(_friends[idx]); }, () => { _friendSel = idx; _homeFocus = FocusRow; RequestFrames(); }));
        }

        // ---- Detail panel on the right for the highlighted friend ----
        var sel = _friends[_friendSel];
        float px = margin + listW + 60 * u, pw = W - px - margin;
        float big = Math.Min(220 * u, pw * 0.4f);
        var bigRect = new RectangleF(px, y, big, big);
        var bigAv = AvatarFor(sel, (int)big);
        if (bigAv != null) g.DrawImageUnscaled(bigAv, (int)bigRect.X, (int)bigRect.Y);
        using (var ring = new Pen(PresenceColor(sel), 5 * u)) g.DrawEllipse(ring, RectangleF.Inflate(bigRect, 4 * u, 4 * u));
        g.DrawString(sel.Name, F(48, FontStyle.Bold), Brushes.White, new RectangleF(px, bigRect.Bottom + 26 * u, pw, 70 * u));
        using (var st = new SolidBrush(PresenceColor(sel)))
            g.DrawString(SteamFriends.StatusText(sel), F(26), st, new RectangleF(px, bigRect.Bottom + 96 * u, pw, 40 * u));
        if (sel.InGame && sel.GameId != null)
        {
            // The game they're in, using Steam's store header art.
            var header = ArtCache.Get($"https://cdn.cloudflare.steamstatic.com/steam/apps/{sel.GameId}/header.jpg");
            if (header != null)
            {
                var art = Art($"gamehdr|{sel.GameId}", () =>
                {
                    using var img = LoadImage(header);
                    var b = new Bitmap((int)(460 * u), (int)(215 * u), PixelFormat.Format32bppPArgb);
                    using var gg = Graphics.FromImage(b);
                    gg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    gg.DrawImage(img, 0, 0, b.Width, b.Height);
                    return (b, false);
                });
                if (art != null) g.DrawImageUnscaled(art, (int)px, (int)(bigRect.Bottom + 150 * u));
            }
        }

        PaintHints(g, new[] { ("A", "Message / join"), ("X", "Refresh"), ("Y", "Discord"), ("LB RB", Tabs[(_tab + 1) % Tabs.Length]) }, null);
    }
}
