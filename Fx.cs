using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Twinstar;

/// <summary>
/// Pre-rendered visual effects for a CPU renderer: blur, soft shadows/glows and a game's accent
/// colour. Everything here is computed once and cached by the caller, never per frame.
/// </summary>
static class Fx
{
    /// <summary>Approximate gaussian blur: repeated box blurs on a 32bpp bitmap, in place.</summary>
    public static void Blur(Bitmap bmp, int radius, int passes = 3)
    {
        if (radius < 1) return;
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        int w = bmp.Width, h = bmp.Height, stride = data.Stride / 4;
        var px = new int[stride * h];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        var tmp = new int[px.Length];
        for (int p = 0; p < passes; p++)
        {
            BoxPass(px, tmp, w, h, stride, radius, horizontal: true);
            BoxPass(tmp, px, w, h, stride, radius, horizontal: false);
        }
        Marshal.Copy(px, 0, data.Scan0, px.Length);
        bmp.UnlockBits(data);
    }

    static void BoxPass(int[] src, int[] dst, int w, int h, int stride, int r, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        int div = r * 2 + 1;
        for (int line = 0; line < lines; line++)
        {
            int Index(int i) => horizontal ? line * stride + i : i * stride + line;
            int sa = 0, sr = 0, sg = 0, sb = 0;
            for (int i = -r; i <= r; i++)
            {
                int c = src[Index(Math.Clamp(i, 0, len - 1))];
                sa += (c >> 24) & 255; sr += (c >> 16) & 255; sg += (c >> 8) & 255; sb += c & 255;
            }
            for (int i = 0; i < len; i++)
            {
                dst[Index(i)] = (sa / div << 24) | (sr / div << 16) | (sg / div << 8) | (sb / div);
                int cOut = src[Index(Math.Clamp(i - r, 0, len - 1))], cIn = src[Index(Math.Clamp(i + r + 1, 0, len - 1))];
                sa += ((cIn >> 24) & 255) - ((cOut >> 24) & 255);
                sr += ((cIn >> 16) & 255) - ((cOut >> 16) & 255);
                sg += ((cIn >> 8) & 255) - ((cOut >> 8) & 255);
                sb += (cIn & 255) - (cOut & 255);
            }
        }
    }

    /// <summary>
    /// Frosted glass: the source region shrunk, blurred, then stretched back up with smoothing.
    /// Shrinking first makes a big, soft blur cheap.
    /// </summary>
    public static Bitmap Frost(Bitmap src, Rectangle region, Color tint)
    {
        int f = 8;
        using var small = new Bitmap(Math.Max(1, region.Width / f), Math.Max(1, region.Height / f), PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, new Rectangle(0, 0, small.Width, small.Height), region, GraphicsUnit.Pixel);
        }
        Blur(small, 3);
        var glass = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(glass))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(small, new Rectangle(0, 0, glass.Width, glass.Height));
            using var t = new SolidBrush(tint);
            g.FillRectangle(t, 0, 0, glass.Width, glass.Height);
        }
        return glass;
    }

    /// <summary>
    /// A soft shadow or glow: a blurred rounded rectangle of the given colour. The bitmap is larger
    /// than w×h by <paramref name="spread"/> on every side; draw it offset by -spread.
    /// </summary>
    public static Bitmap SoftRect(int w, int h, float radius, int spread, Color c)
    {
        // Rendered at quarter size and blurred there, then scaled up: big soft edges for little work.
        const int f = 4;
        int sw = (w + spread * 2) / f + 2, sh = (h + spread * 2) / f + 2;
        using var small = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Rounded(new RectangleF(spread / (float)f + 1, spread / (float)f + 1, w / (float)f, h / (float)f), radius / f);
            using var b = new SolidBrush(c);
            g.FillPath(b, path);
        }
        Blur(small, Math.Max(1, spread / f / 2));
        var bmp = new Bitmap(w + spread * 2, h + spread * 2, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(small, new RectangleF(-f, -f, sw * f, sh * f));
        }
        return bmp;
    }

    /// <summary>The image's most characteristic vivid colour, brightened for use as a glow/tint.</summary>
    public static Color AccentOf(Image img)
    {
        using var small = new Bitmap(24, 24, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(img, 0, 0, 24, 24);
        }
        double r = 0, gr = 0, b = 0, wsum = 0;
        for (int y = 0; y < 24; y++)
            for (int x = 0; x < 24; x++)
            {
                var c = small.GetPixel(x, y);
                int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
                double sat = max == 0 ? 0 : (max - min) / (double)max, val = max / 255.0;
                // Favour saturated, mid-to-bright pixels: that's the colour people remember a cover by.
                double weight = sat * sat * (0.2 + val) + 0.002;
                r += c.R * weight; gr += c.G * weight; b += c.B * weight; wsum += weight;
            }
        r /= wsum; gr /= wsum; b /= wsum;
        double peak = Math.Max(r, Math.Max(gr, b));
        if (peak < 1) return Color.FromArgb(59, 130, 246);
        double k = 230 / peak; // bring the brightest channel up to a lively level
        return Color.FromArgb((int)Math.Min(255, r * k), (int)Math.Min(255, gr * k), (int)Math.Min(255, b * k));
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = Math.Max(0.5f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
