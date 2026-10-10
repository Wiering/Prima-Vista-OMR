namespace PVOMR.Core.Imaging;

/// <summary>8-bit grayscale image, 0 = black, 255 = white.</summary>
public sealed class GrayImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }

    public GrayImage(int width, int height, byte[]? data = null)
    {
        Width = width;
        Height = height;
        Data = data ?? new byte[width * height];
        if (Data.Length != width * height) throw new ArgumentException("data size mismatch");
    }

    public byte this[int x, int y]
    {
        get => Data[y * Width + x];
        set => Data[y * Width + x] = value;
    }

    public GrayImage Clone() => new(Width, Height, (byte[])Data.Clone());

    /// <summary>Box-filter downscale by an integer factor.</summary>
    public GrayImage Downscale(int factor)
    {
        if (factor <= 1) return this;
        int w = Width / factor, h = Height / factor;
        var r = new GrayImage(w, h);
        int area = factor * factor;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int row = (y * factor + dy) * Width + x * factor;
                    for (int dx = 0; dx < factor; dx++) sum += Data[row + dx];
                }
                r.Data[y * w + x] = (byte)(sum / area);
            }
        }
        return r;
    }

    /// <summary>Bilinear resample to a new size.</summary>
    public GrayImage Resize(int newWidth, int newHeight)
    {
        if (newWidth == Width && newHeight == Height) return this;
        var r = new GrayImage(newWidth, newHeight);
        double sx = (double)Width / newWidth, sy = (double)Height / newHeight;
        Parallel.For(0, newHeight, y =>
        {
            double fy = (y + 0.5) * sy - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(fy), 0, Height - 1);
            int y1 = Math.Min(y0 + 1, Height - 1);
            double wy = Math.Clamp(fy - y0, 0, 1);
            for (int x = 0; x < newWidth; x++)
            {
                double fx = (x + 0.5) * sx - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(fx), 0, Width - 1);
                int x1 = Math.Min(x0 + 1, Width - 1);
                double wx = Math.Clamp(fx - x0, 0, 1);
                double v = Data[y0 * Width + x0] * (1 - wx) * (1 - wy) + Data[y0 * Width + x1] * wx * (1 - wy)
                         + Data[y1 * Width + x0] * (1 - wx) * wy + Data[y1 * Width + x1] * wx * wy;
                r.Data[y * newWidth + x] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
            }
        });
        return r;
    }

    /// <summary>Rotate around the image centre by <paramref name="degrees"/> (counter-clockwise positive), bilinear, white fill.</summary>
    public GrayImage Rotate(double degrees)
    {
        if (Math.Abs(degrees) < 1e-6) return this;
        double rad = degrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        var r = new GrayImage(Width, Height);
        double cx = Width / 2.0, cy = Height / 2.0;
        Parallel.For(0, Height, y =>
        {
            for (int x = 0; x < Width; x++)
            {
                double dx = x - cx, dy = y - cy;
                double sx = cos * dx + sin * dy + cx;
                double sy = -sin * dx + cos * dy + cy;
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                if (x0 < 0 || y0 < 0 || x0 >= Width - 1 || y0 >= Height - 1)
                {
                    r.Data[y * Width + x] = 255;
                    continue;
                }
                double fx = sx - x0, fy = sy - y0;
                int i = y0 * Width + x0;
                double v = Data[i] * (1 - fx) * (1 - fy) + Data[i + 1] * fx * (1 - fy)
                         + Data[i + Width] * (1 - fx) * fy + Data[i + Width + 1] * fx * fy;
                r.Data[y * Width + x] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
            }
        });
        return r;
    }

    public GrayImage Rotate90(int quarterTurns)
    {
        quarterTurns = ((quarterTurns % 4) + 4) % 4;
        if (quarterTurns == 0) return this;
        if (quarterTurns == 2)
        {
            var r2 = new GrayImage(Width, Height);
            for (int i = 0, n = Data.Length; i < n; i++) r2.Data[n - 1 - i] = Data[i];
            return r2;
        }
        var r = new GrayImage(Height, Width);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                if (quarterTurns == 1) r[y, Width - 1 - x] = this[x, y];      // 90 degrees CCW
                else r[Height - 1 - y, x] = this[x, y];                        // 90 degrees CW
            }
        return r;
    }
}
