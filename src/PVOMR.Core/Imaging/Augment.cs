namespace PVOMR.Core.Imaging;

/// <summary>Image degradations that imitate scan quality: blur, noise, skew, contrast loss, rescaling, ink erosion.</summary>
public static class Augment
{
    /// <summary>Spec: comma separated key=value pairs, e.g. "blur=1.2,noise=0.08,skew=1.5,scale=0.6,contrast=0.6,speckle=0.002,thin=1". Unknown keys are ignored.</summary>
    public static GrayImage Apply(GrayImage img, string spec, int seed = 1)
    {
        var rnd = new Random(seed);
        var opts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=');
            if (kv.Length == 2 && double.TryParse(kv[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)) opts[kv[0]] = v;
            else if (kv.Length == 1) opts[kv[0]] = 1;
        }
        if (opts.TryGetValue("scale", out double scale) && scale > 0 && Math.Abs(scale - 1) > 0.01)
        {
            // downscale then upscale back: loses detail like a low resolution scan
            var small = img.Resize(Math.Max(10, (int)(img.Width * scale)), Math.Max(10, (int)(img.Height * scale)));
            img = small.Resize(img.Width, img.Height);
        }
        if (opts.TryGetValue("thin", out double thin) && thin > 0) img = Erode(img, (int)thin);
        if (opts.TryGetValue("thick", out double thick) && thick > 0) img = Dilate(img, (int)thick);
        if (opts.TryGetValue("blur", out double blur) && blur > 0) img = GaussianBlur(img, blur);
        if (opts.TryGetValue("contrast", out double contrast) && contrast > 0 && contrast < 1)
        {
            // ink becomes grey, background slightly darker
            var r = new GrayImage(img.Width, img.Height);
            for (int i = 0; i < r.Data.Length; i++) r.Data[i] = (byte)Math.Clamp(255 - (255 - img.Data[i]) * contrast - 20 * (1 - contrast), 0, 255);
            img = r;
        }
        if (opts.TryGetValue("noise", out double noise) && noise > 0)
        {
            var r = img.Clone();
            double sigma = noise * 255;
            for (int i = 0; i < r.Data.Length; i++)
            {
                double u1 = 1 - rnd.NextDouble(), u2 = rnd.NextDouble();
                double g = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
                r.Data[i] = (byte)Math.Clamp(r.Data[i] + g * sigma, 0, 255);
            }
            img = r;
        }
        if (opts.TryGetValue("speckle", out double speckle) && speckle > 0)
        {
            var r = img.Clone();
            int count = (int)(r.Data.Length * speckle);
            for (int k = 0; k < count; k++)
            {
                int x = rnd.Next(r.Width), y = rnd.Next(r.Height);
                int size = rnd.Next(1, 3);
                for (int dy = 0; dy < size; dy++) for (int dx = 0; dx < size; dx++) if (x + dx < r.Width && y + dy < r.Height) r[x + dx, y + dy] = 0;
            }
            img = r;
        }
        if (opts.TryGetValue("skew", out double skew) && Math.Abs(skew) > 0.01) img = img.Rotate(skew);
        return img;
    }

    public static GrayImage GaussianBlur(GrayImage img, double sigma)
    {
        int radius = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        var kernel = new double[2 * radius + 1];
        double sum = 0;
        for (int i = -radius; i <= radius; i++) { kernel[i + radius] = Math.Exp(-i * i / (2 * sigma * sigma)); sum += kernel[i + radius]; }
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= sum;
        int w = img.Width, h = img.Height;
        var tmp = new float[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                double acc = 0;
                for (int i = -radius; i <= radius; i++) { int xx = Math.Clamp(x + i, 0, w - 1); acc += img.Data[y * w + xx] * kernel[i + radius]; }
                tmp[y * w + x] = (float)acc;
            }
        });
        var r = new GrayImage(w, h);
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                double acc = 0;
                for (int i = -radius; i <= radius; i++) { int yy = Math.Clamp(y + i, 0, h - 1); acc += tmp[yy * w + x] * kernel[i + radius]; }
                r.Data[y * w + x] = (byte)Math.Clamp(Math.Round(acc), 0, 255);
            }
        });
        return r;
    }

    /// <summary>Erode ink (make strokes thinner) by taking the maximum over a (2r+1) square.</summary>
    public static GrayImage Erode(GrayImage img, int r) => MinMax(img, r, true);
    /// <summary>Dilate ink (make strokes thicker) by taking the minimum over a (2r+1) square.</summary>
    public static GrayImage Dilate(GrayImage img, int r) => MinMax(img, r, false);

    private static GrayImage MinMax(GrayImage img, int r, bool max)
    {
        int w = img.Width, h = img.Height;
        var tmp = new byte[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int v = max ? 0 : 255;
                for (int i = -r; i <= r; i++) { int xx = Math.Clamp(x + i, 0, w - 1); int p = img.Data[y * w + xx]; v = max ? Math.Max(v, p) : Math.Min(v, p); }
                tmp[y * w + x] = (byte)v;
            }
        });
        var res = new GrayImage(w, h);
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int v = max ? 0 : 255;
                for (int i = -r; i <= r; i++) { int yy = Math.Clamp(y + i, 0, h - 1); int p = tmp[yy * w + x]; v = max ? Math.Max(v, p) : Math.Min(v, p); }
                res.Data[y * w + x] = (byte)v;
            }
        });
        return res;
    }
}
