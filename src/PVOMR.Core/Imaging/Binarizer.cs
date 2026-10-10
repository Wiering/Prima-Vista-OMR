namespace PVOMR.Core.Imaging;

public enum BinarizationMethod { Auto, Otsu, Sauvola }

public static class Binarizer
{
    public static int OtsuThreshold(GrayImage img)
    {
        var hist = new long[256];
        foreach (var v in img.Data) hist[v]++;
        long total = img.Data.Length;
        double sum = 0;
        for (int i = 0; i < 256; i++) sum += i * (double)hist[i];
        double sumB = 0; long wB = 0; double maxVar = -1; int thr = 128;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t]; if (wB == 0) continue;
            long wF = total - wB; if (wF == 0) break;
            sumB += t * (double)hist[t];
            double mB = sumB / wB, mF = (sum - sumB) / wF;
            double v = wB * (double)wF * (mB - mF) * (mB - mF);
            if (v > maxVar) { maxVar = v; thr = t; }
        }
        return thr;
    }

    public static BinaryImage Otsu(GrayImage img, int? threshold = null)
    {
        int t = threshold ?? OtsuThreshold(img);
        var b = new BinaryImage(img.Width, img.Height);
        var src = img.Data; var dst = b.Data;
        for (int i = 0; i < src.Length; i++) dst[i] = src[i] <= t ? (byte)1 : (byte)0;
        return b;
    }

    /// <summary>Sauvola local threshold using integral images. Pixels brighter than the ceiling are never ink.</summary>
    public static BinaryImage Sauvola(GrayImage img, int window, double k = 0.25, int globalCeiling = 255)
    {
        int w = img.Width, h = img.Height;
        var integ = new long[(w + 1) * (h + 1)];
        var integ2 = new long[(w + 1) * (h + 1)];
        for (int y = 1; y <= h; y++)
        {
            long rs = 0, rs2 = 0;
            int row = (y - 1) * w;
            for (int x = 1; x <= w; x++)
            {
                int v = img.Data[row + x - 1];
                rs += v; rs2 += v * v;
                integ[y * (w + 1) + x] = integ[(y - 1) * (w + 1) + x] + rs;
                integ2[y * (w + 1) + x] = integ2[(y - 1) * (w + 1) + x] + rs2;
            }
        }
        var b = new BinaryImage(w, h);
        int r = window / 2;
        const double R = 128.0;
        Parallel.For(0, h, y =>
        {
            int y0 = Math.Max(0, y - r), y1 = Math.Min(h, y + r + 1);
            for (int x = 0; x < w; x++)
            {
                int v = img.Data[y * w + x];
                if (v > globalCeiling) continue;
                int x0 = Math.Max(0, x - r), x1 = Math.Min(w, x + r + 1);
                long area = (long)(x1 - x0) * (y1 - y0);
                long s = integ[y1 * (w + 1) + x1] - integ[y0 * (w + 1) + x1] - integ[y1 * (w + 1) + x0] + integ[y0 * (w + 1) + x0];
                long s2 = integ2[y1 * (w + 1) + x1] - integ2[y0 * (w + 1) + x1] - integ2[y1 * (w + 1) + x0] + integ2[y0 * (w + 1) + x0];
                double mean = (double)s / area;
                double variance = (double)s2 / area - mean * mean;
                double std = variance > 0 ? Math.Sqrt(variance) : 0;
                double t = mean * (1 + k * (std / R - 1));
                if (v <= t) b.Data[y * w + x] = 1;
            }
        });
        return b;
    }

    /// <summary>
    /// Auto: Otsu for pages with a clean, uniform background, otherwise Sauvola capped by a relaxed global threshold.
    /// </summary>
    public static BinaryImage Binarize(GrayImage img, BinarizationMethod method, int sauvolaWindow, out string description)
    {
        int otsu = OtsuThreshold(img);
        // bilevel (1-bit, possibly dithered) sources: a fixed mid threshold is the only sensible choice
        long extreme = 0;
        foreach (var v in img.Data) if (v <= 8 || v >= 247) extreme++;
        if (extreme >= 0.985 * img.Data.Length && method != BinarizationMethod.Sauvola)
        {
            description = "bilevel(t=127)";
            return Otsu(img, 127);
        }
        if (method == BinarizationMethod.Otsu)
        {
            description = $"otsu(t={otsu})";
            return Otsu(img, otsu);
        }
        if (method == BinarizationMethod.Auto)
        {
            // Measure background unevenness: local means of bright pixels on a coarse grid.
            var small = img.Downscale(8);
            var means = new List<double>();
            int bs = 8;
            for (int by = 0; by + bs <= small.Height; by += bs)
                for (int bx = 0; bx + bs <= small.Width; bx += bs)
                {
                    double s = 0; int n = 0;
                    for (int y = by; y < by + bs; y++)
                        for (int x = bx; x < bx + bs; x++)
                        {
                            int v = small[x, y];
                            if (v > otsu) { s += v; n++; }
                        }
                    if (n > bs * bs / 2) means.Add(s / n);
                }
            double unevenness = 0;
            if (means.Count > 4)
            {
                means.Sort();
                unevenness = means[^Math.Max(1, means.Count / 20)] - means[Math.Max(0, means.Count / 20)];
            }
            bool useSauvola = unevenness > 60 || (means.Count > 0 && means[means.Count / 2] < 170);
            if (!useSauvola)
            {
                description = $"otsu(t={otsu}, bg-range={unevenness:0})";
                return Otsu(img, otsu);
            }
            description = $"sauvola(w={sauvolaWindow}, bg-range={unevenness:0}, otsu={otsu})";
        }
        else description = $"sauvola(w={sauvolaWindow}, otsu={otsu})";
        int ceiling = Math.Min(255, otsu + 30);
        return Sauvola(img, sauvolaWindow, 0.25, ceiling);
    }
}
