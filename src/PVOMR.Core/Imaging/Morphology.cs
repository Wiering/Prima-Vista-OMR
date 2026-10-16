namespace PVOMR.Core.Imaging;

/// <summary>Binary morphology with square structuring elements (separable: a row pass and a column pass).</summary>
public static class Morphology
{
    /// <summary>Ink grown by r pixels in every direction (a (2r+1) square).</summary>
    public static BinaryImage Dilate(BinaryImage b, int r) => Pass(Pass(b, r, true, true), r, false, true);

    /// <summary>Ink shrunk by r pixels (a pixel stays ink only when its whole (2r+1) square is ink).</summary>
    public static BinaryImage Erode(BinaryImage b, int r) => Pass(Pass(b, r, true, false), r, false, false);

    /// <summary>Closing: small white gaps and holes in the ink (up to 2r pixels) are filled, shapes keep their size.</summary>
    public static BinaryImage Close(BinaryImage b, int r) => Erode(Dilate(b, r), r);

    private static BinaryImage Pass(BinaryImage b, int r, bool horizontal, bool dilate)
    {
        int w = b.Width, h = b.Height;
        var o = new BinaryImage(w, h);
        int outer = horizontal ? h : w, inner = horizontal ? w : h;
        Parallel.For(0, outer, i =>
        {
            // running count of ink in the window along the line
            int count = 0;
            int At(int k) => horizontal ? (k >= 0 && k < w && b.Data[i * w + k] != 0 ? 1 : 0) : (k >= 0 && k < h && b.Data[k * w + i] != 0 ? 1 : 0);
            for (int k = -r; k <= r - 1; k++) count += At(k);
            for (int k = 0; k < inner; k++)
            {
                count += At(k + r);
                if (k - r - 1 >= -r) count -= At(k - r - 1);
                bool ink = dilate ? count > 0 : count == 2 * r + 1;
                if (ink) { if (horizontal) o.Data[i * w + k] = 1; else o.Data[k * w + i] = 1; }
            }
        });
        return o;
    }
}
