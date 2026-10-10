namespace PVOMR.Core.Imaging;

/// <summary>Removes dark scan borders (black bands along the page edges) that would otherwise act as giant vertical/horizontal lines.</summary>
public static class BorderCleaner
{
    public static string Clean(BinaryImage bin, double maxFraction = 0.12, double inkThreshold = 0.2)
    {
        int w = bin.Width, h = bin.Height;
        var colFrac = new double[w];
        var rowFrac = new double[h];
        for (int y = 0; y < h; y++)
        {
            int row = y * w, s = 0;
            for (int x = 0; x < w; x++) { if (bin.Data[row + x] != 0) { s++; colFrac[x]++; } }
            rowFrac[y] = s / (double)w;
        }
        for (int x = 0; x < w; x++) colFrac[x] /= h;

        int left = Extent(colFrac, 0, +1, (int)(maxFraction * w), inkThreshold);
        int right = Extent(colFrac, w - 1, -1, (int)(maxFraction * w), inkThreshold);
        int top = Extent(rowFrac, 0, +1, (int)(maxFraction * h), inkThreshold);
        int bottom = Extent(rowFrac, h - 1, -1, (int)(maxFraction * h), inkThreshold);
        if (left > 0) bin.FillRect(0, 0, left + 2, h, false);
        if (right > 0) bin.FillRect(w - right - 2, 0, w, h, false);
        if (top > 0) bin.FillRect(0, 0, w, top + 2, false);
        if (bottom > 0) bin.FillRect(0, h - bottom - 2, w, h, false);
        return left + right + top + bottom == 0 ? "" : $"borders cleared: left {left}px, right {right}px, top {top}px, bottom {bottom}px";
    }

    /// <summary>Number of lines from the edge that belong to a dark band (ink fraction above threshold, small gaps bridged).</summary>
    private static int Extent(double[] frac, int start, int dir, int max, double thr)
    {
        int n = 0, gap = 0, i = start;
        for (int k = 0; k < max && i >= 0 && i < frac.Length; k++, i += dir)
        {
            if (frac[i] >= thr) { n = k + 1; gap = 0; }
            else if (++gap > 3) break;
        }
        return n;
    }
}
