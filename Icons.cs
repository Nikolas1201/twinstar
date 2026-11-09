using System.Drawing.Drawing2D;

namespace Twinstar;

/// <summary>
/// Hand-drawn vector icons (designed on a 24×24 grid, outline style) and pixel-exact text
/// centering. Font glyphs carry built-in padding and baseline offsets, so they never sit dead
/// centre in a circle or pill; these are centred on their actual ink.
/// </summary>
static class Icons
{
    /// <summary>Maps the 24×24 design grid onto the centre of a box.</summary>
    static Matrix GridTo(RectangleF box)
    {
        float s = Math.Min(box.Width, box.Height) / 24f;
        var m = new Matrix();
        m.Translate(box.X + box.Width / 2 - 12 * s, box.Y + box.Height / 2 - 12 * s);
        m.Scale(s, s);
        return m;
    }

    static Pen OutlinePen(Color c, RectangleF box, float gridWidth) =>
        new(c, gridWidth * Math.Min(box.Width, box.Height) / 24f)
        {
            LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round,
        };

    static void Stroke(Graphics g, GraphicsPath path, RectangleF box, Color c, float gridWidth)
    {
        using var m = GridTo(box);
        path.Transform(m);
        using var pen = OutlinePen(c, box, gridWidth);
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawPath(pen, path);
        g.SmoothingMode = mode;
    }

    static void Fill(Graphics g, GraphicsPath path, RectangleF box, Color c)
    {
        using var m = GridTo(box);
        path.Transform(m);
        using var b = new SolidBrush(c);
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillPath(b, path);
        g.SmoothingMode = mode;
    }

    /// <summary>Game controller: body with two grips, d-pad and two face buttons.</summary>
    public static void Gamepad(Graphics g, RectangleF box, Color c, float weight = 1.6f)
    {
        using (var body = new GraphicsPath())
        {
            body.AddLine(7.2f, 6.5f, 16.8f, 6.5f);
            body.AddBezier(16.8f, 6.5f, 18.9f, 6.5f, 20.3f, 7.9f, 20.8f, 10.0f);
            body.AddLine(20.8f, 10.0f, 22.0f, 15.6f);
            body.AddBezier(22.0f, 15.6f, 22.5f, 18.0f, 20.2f, 19.6f, 18.4f, 18.3f);
            body.AddLine(18.4f, 18.3f, 15.6f, 15.8f);
            body.AddLine(15.6f, 15.8f, 8.4f, 15.8f);
            body.AddLine(8.4f, 15.8f, 5.6f, 18.3f);
            body.AddBezier(5.6f, 18.3f, 3.8f, 19.6f, 1.5f, 18.0f, 2.0f, 15.6f);
            body.AddLine(2.0f, 15.6f, 3.2f, 10.0f);
            body.AddBezier(3.2f, 10.0f, 3.7f, 7.9f, 5.1f, 6.5f, 7.2f, 6.5f);
            body.CloseFigure();
            // D-pad
            body.StartFigure(); body.AddLine(7.6f, 9.4f, 7.6f, 13.0f);
            body.StartFigure(); body.AddLine(5.8f, 11.2f, 9.4f, 11.2f);
            Stroke(g, body, box, c, weight);
        }
        using var buttons = new GraphicsPath();
        buttons.AddEllipse(15.2f, 9.0f, 1.9f, 1.9f);
        buttons.AddEllipse(17.4f, 11.1f, 1.9f, 1.9f);
        Fill(g, buttons, box, c);
    }

    /// <summary>Settings cog: eight squared-off teeth around a ring with a centre hole.</summary>
    public static void Gear(Graphics g, RectangleF box, Color c, float weight = 1.6f)
    {
        const int teeth = 8;
        const float outer = 10.0f, root = 7.6f, hole = 3.1f;
        const float topHalf = 0.20f, rootHalf = 0.33f; // radians either side of each tooth's centre
        var pts = new List<PointF>();
        for (int i = 0; i < teeth; i++)
        {
            float a = i * MathF.PI * 2 / teeth;
            PointF P(float r, float ang) => new(12 + r * MathF.Cos(ang), 12 + r * MathF.Sin(ang));
            pts.Add(P(root, a - rootHalf));
            pts.Add(P(outer, a - topHalf));
            pts.Add(P(outer, a + topHalf));
            pts.Add(P(root, a + rootHalf));
        }
        using var path = new GraphicsPath();
        path.AddPolygon(pts.ToArray());
        path.AddEllipse(12 - hole, 12 - hole, hole * 2, hole * 2);
        Stroke(g, path, box, c, weight);
    }

    /// <summary>Desktop monitor on a stand.</summary>
    public static void Monitor(Graphics g, RectangleF box, Color c, float weight = 1.6f)
    {
        using var path = new GraphicsPath();
        AddRounded(path, new RectangleF(2.5f, 4.0f, 19.0f, 12.5f), 1.8f);
        path.StartFigure(); path.AddLine(12f, 16.5f, 12f, 19.6f);
        path.StartFigure(); path.AddLine(8.0f, 20.2f, 16.0f, 20.2f);
        Stroke(g, path, box, c, weight);
    }

    /// <summary>Three dots ("more options"), filled.</summary>
    public static void Dots(Graphics g, RectangleF box, Color c)
    {
        using var path = new GraphicsPath();
        foreach (float x in new[] { 6f, 12f, 18f }) path.AddEllipse(x - 1.7f, 12 - 1.7f, 3.4f, 3.4f);
        Fill(g, path, box, c);
    }

    /// <summary>Play triangle with softened corners, optically centred.</summary>
    public static void Play(Graphics g, RectangleF box, Color c)
    {
        using var path = new GraphicsPath();
        path.AddPolygon(new[] { new PointF(8.5f, 5.5f), new PointF(19.0f, 12f), new PointF(8.5f, 18.5f) });
        Fill(g, path, box, c);
        using var round = new GraphicsPath();
        round.AddPolygon(new[] { new PointF(8.5f, 5.5f), new PointF(19.0f, 12f), new PointF(8.5f, 18.5f) });
        Stroke(g, round, box, c, 1.6f);
    }

    /// <summary>Left/right arrows for "choose" hints.</summary>
    public static void LeftRight(Graphics g, RectangleF box, Color c)
    {
        using var path = new GraphicsPath();
        path.AddPolygon(new[] { new PointF(3f, 12f), new PointF(10f, 7f), new PointF(10f, 17f) });
        path.AddPolygon(new[] { new PointF(21f, 12f), new PointF(14f, 7f), new PointF(14f, 17f) });
        Fill(g, path, box, c);
    }

    static void AddRounded(GraphicsPath p, RectangleF r, float radius)
    {
        float d = radius * 2;
        p.StartFigure();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
    }

    /// <summary>
    /// Draws text centred on its actual ink inside a box (not on the font's line box), so single
    /// letters and short labels sit exactly in the middle of circles and pills.
    /// </summary>
    public static void TextCentered(Graphics g, string text, Font font, Color c, RectangleF box)
    {
        using var path = new GraphicsPath();
        path.AddString(text, font.FontFamily, (int)font.Style, font.Size, PointF.Empty, StringFormat.GenericTypographic);
        var ink = path.GetBounds();
        if (ink.Width <= 0) return;
        using var m = new Matrix();
        m.Translate(box.X + (box.Width - ink.Width) / 2 - ink.X, box.Y + (box.Height - ink.Height) / 2 - ink.Y);
        path.Transform(m);
        using var b = new SolidBrush(c);
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillPath(b, path);
        g.SmoothingMode = mode;
    }
}
