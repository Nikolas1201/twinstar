using System.Runtime.InteropServices;

namespace Twinstar;

/// <summary>
/// Classic GDI blending for the one thing GDI+ is slow at: fading a full-screen image.
/// Bitmaps are converted to GDI bitmaps once and cached (only a few backgrounds at a time).
/// </summary>
static class Gdi
{
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("msimg32.dll")]
    static extern bool AlphaBlend(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int blendFunction);

    static readonly Dictionary<Bitmap, IntPtr> Handles = new();
    static readonly Queue<Bitmap> Order = new();

    static IntPtr HandleFor(Bitmap bmp)
    {
        if (Handles.TryGetValue(bmp, out var h)) return h;
        h = bmp.GetHbitmap(Color.Black);
        Handles[bmp] = h;
        Order.Enqueue(bmp);
        while (Order.Count > 4) Forget(Order.Dequeue());
        return h;
    }

    /// <summary>Releases the GDI copy of a bitmap (call before disposing it).</summary>
    public static void Forget(Bitmap bmp)
    {
        if (Handles.Remove(bmp, out var h)) DeleteObject(h);
    }

    /// <summary>Draws the bitmap at 0,0 over what's already there with a constant opacity (0-255).</summary>
    public static void BlendOnto(Graphics g, Bitmap bmp, byte alpha)
    {
        var hbm = HandleFor(bmp);
        var hdc = g.GetHdc();
        try
        {
            var mem = CreateCompatibleDC(hdc);
            var old = SelectObject(mem, hbm);
            // BLENDFUNCTION { AC_SRC_OVER, 0, SourceConstantAlpha, 0 } packed into an int.
            AlphaBlend(hdc, 0, 0, bmp.Width, bmp.Height, mem, 0, 0, bmp.Width, bmp.Height, alpha << 16);
            SelectObject(mem, old);
            DeleteDC(mem);
        }
        finally { g.ReleaseHdc(hdc); }
    }
}
