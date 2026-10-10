namespace PVOMR.Core.Imaging;

public static class Deskew
{
    /// <summary>
    /// Estimate the skew angle (degrees, CCW positive as used by GrayImage.Rotate) by maximising the
    /// sharpness of the horizontal projection profile of a downscaled binary image.
    /// </summary>
    public static double EstimateAngle(BinaryImage bin, double maxAngle = 4.0, double coarseStep = 0.25, double fineStep = 0.05)
    {
        int f = Math.Max(1, bin.Width / 700);
        int w = bin.Width / f, h = bin.Height / f;
        var pts = new List<(int x, int y)>();
        var rnd = new Random(1);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sx = x * f, sy = y * f;
                if (bin.Data[sy * bin.Width + sx] != 0) pts.Add((x, y));
            }
        if (pts.Count < 100) return 0;
        if (pts.Count > 150_000)
        {
            var sub = new List<(int, int)>(150_000);
            double keep = 150_000.0 / pts.Count;
            foreach (var p in pts) if (rnd.NextDouble() < keep) sub.Add(p);
            pts = sub;
        }
        double best = 0, bestScore = double.MinValue;
        double Score(double deg)
        {
            double rad = deg * Math.PI / 180, s = Math.Sin(rad), c = Math.Cos(rad);
            var proj = new int[h + w + 2];
            int off = w;
            foreach (var (x, y) in pts)
            {
                int yy = (int)Math.Round(c * y - s * (x - w / 2.0)) + off;
                if (yy >= 0 && yy < proj.Length) proj[yy]++;
            }
            double sum = 0;
            for (int i = 1; i < proj.Length; i++) { double d = proj[i] - proj[i - 1]; sum += d * d; }
            return sum;
        }
        for (double a = -maxAngle; a <= maxAngle + 1e-9; a += coarseStep)
        {
            double sc = Score(a);
            if (sc > bestScore) { bestScore = sc; best = a; }
        }
        double centre = best;
        for (double a = centre - coarseStep; a <= centre + coarseStep + 1e-9; a += fineStep)
        {
            double sc = Score(a);
            if (sc > bestScore) { bestScore = sc; best = a; }
        }
        return best;
    }
}
