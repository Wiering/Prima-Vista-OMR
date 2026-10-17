namespace PVOMR.Core.Imaging;

/// <summary>
/// The page seen from afar: the ink averaged over boxes so that a staff space is about three pixels (a staff twelve
/// pixels high). Specks, flecks and gaps of a pixel or two vanish into shades of gray, while the music's structure
/// (beams, heads, stems, bar lines) stays plain. A second opinion for decisions the detailed image makes poorly.
/// Values are ink coverage, 0 (paper) to 1 (solid ink); positions are asked for in the original image's pixels.
/// </summary>
public sealed class CoarseView
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>Original pixels per coarse pixel.</summary>
    public double Scale { get; }
    public float[] Data { get; }

    public CoarseView(BinaryImage b, double staffSpace, double coarseSpace = 3.0)
    {
        Scale = Math.Max(1.0, staffSpace / coarseSpace);
        Width = Math.Max(1, (int)Math.Ceiling(b.Width / Scale));
        Height = Math.Max(1, (int)Math.Ceiling(b.Height / Scale));
        Data = new float[Width * Height];
        // integral image of the ink, then the coverage of each coarse box
        var sum = new int[(b.Width + 1) * (b.Height + 1)];
        int sw = b.Width + 1;
        for (int y = 0; y < b.Height; y++)
        {
            int row = 0;
            for (int x = 0; x < b.Width; x++)
            {
                row += b.Data[y * b.Width + x];
                sum[(y + 1) * sw + x + 1] = sum[y * sw + x + 1] + row;
            }
        }
        for (int cy = 0; cy < Height; cy++)
        {
            int y0 = (int)Math.Round(cy * Scale), y1 = Math.Min(b.Height, (int)Math.Round((cy + 1) * Scale));
            for (int cx = 0; cx < Width; cx++)
            {
                int x0 = (int)Math.Round(cx * Scale), x1 = Math.Min(b.Width, (int)Math.Round((cx + 1) * Scale));
                int area = (x1 - x0) * (y1 - y0);
                if (area <= 0) continue;
                int s = sum[y1 * sw + x1] - sum[y0 * sw + x1] - sum[y1 * sw + x0] + sum[y0 * sw + x0];
                Data[cy * Width + cx] = s / (float)area;
            }
        }
    }

    /// <summary>Coverage at an original-image position (bilinear between the coarse pixels' centres).</summary>
    public double At(double x, double y)
    {
        double fx = x / Scale - 0.5, fy = y / Scale - 0.5;
        int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
        double ax = fx - ix, ay = fy - iy;
        double V(int cx, int cy) => cx < 0 || cy < 0 || cx >= Width || cy >= Height ? 0 : Data[cy * Width + cx];
        return (1 - ay) * ((1 - ax) * V(ix, iy) + ax * V(ix + 1, iy)) + ay * ((1 - ax) * V(ix, iy + 1) + ax * V(ix + 1, iy + 1));
    }

    /// <summary>The ink met going straight down from y0 to y1 at x, in original pixels (coverage integrated over the way):
    /// the thickness of what lies across the line, however broken or merged it is.</summary>
    public double ColumnInk(double x, double y0, double y1)
    {
        if (y1 < y0) (y0, y1) = (y1, y0);
        double step = Scale / 4, ink = 0;
        for (double y = y0; y < y1; y += step) ink += At(x, y) * Math.Min(step, y1 - y);
        return ink;
    }

    /// <summary>Mean coverage over a box (original coordinates).</summary>
    public double MeanIn(double x0, double y0, double x1, double y1)
    {
        double step = Scale / 2, s = 0; int n = 0;
        for (double y = y0; y <= y1; y += step)
            for (double x = x0; x <= x1; x += step) { s += At(x, y); n++; }
        return n == 0 ? 0 : s / n;
    }

    /// <summary>The coarse image as gray (ink black), for looking at it.</summary>
    public GrayImage ToGray()
    {
        var g = new GrayImage(Width, Height);
        for (int i = 0; i < Data.Length; i++) g.Data[i] = (byte)Math.Round(255 * (1 - Math.Clamp(Data[i], 0f, 1f)));
        return g;
    }
}
