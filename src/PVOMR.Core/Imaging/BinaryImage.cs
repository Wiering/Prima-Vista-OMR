namespace PVOMR.Core.Imaging;

/// <summary>Binary image, 1 = ink (black), 0 = background.</summary>
public sealed class BinaryImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }

    public BinaryImage(int width, int height, byte[]? data = null)
    {
        Width = width; Height = height;
        Data = data ?? new byte[width * height];
    }

    public bool this[int x, int y]
    {
        get => (uint)x < (uint)Width && (uint)y < (uint)Height && Data[y * Width + x] != 0;
        set { if ((uint)x < (uint)Width && (uint)y < (uint)Height) Data[y * Width + x] = value ? (byte)1 : (byte)0; }
    }

    public BinaryImage Clone() => new(Width, Height, (byte[])Data.Clone());

    public int[] HorizontalProjection(int x0, int x1, int y0, int y1)
    {
        x0 = Math.Max(0, x0); x1 = Math.Min(Width, x1); y0 = Math.Max(0, y0); y1 = Math.Min(Height, y1);
        var p = new int[Math.Max(0, y1 - y0)];
        for (int y = y0; y < y1; y++)
        {
            int row = y * Width, s = 0;
            for (int x = x0; x < x1; x++) s += Data[row + x];
            p[y - y0] = s;
        }
        return p;
    }

    public int[] VerticalProjection(int x0, int x1, int y0, int y1)
    {
        x0 = Math.Max(0, x0); x1 = Math.Min(Width, x1); y0 = Math.Max(0, y0); y1 = Math.Min(Height, y1);
        var p = new int[Math.Max(0, x1 - x0)];
        for (int y = y0; y < y1; y++)
        {
            int row = y * Width;
            for (int x = x0; x < x1; x++) p[x - x0] += Data[row + x];
        }
        return p;
    }

    /// <summary>Length of the vertical black run through (x,y), and its start row. Returns 0 if (x,y) is white.</summary>
    public int VerticalRunAt(int x, int y, out int top)
    {
        top = y;
        if (!this[x, y]) return 0;
        int t = y; while (t > 0 && this[x, t - 1]) t--;
        int b = y; while (b < Height - 1 && this[x, b + 1]) b++;
        top = t;
        return b - t + 1;
    }

    public int HorizontalRunAt(int x, int y, out int left)
    {
        left = x;
        if (!this[x, y]) return 0;
        int l = x; while (l > 0 && this[l - 1, y]) l--;
        int r = x; while (r < Width - 1 && this[r + 1, y]) r++;
        left = l;
        return r - l + 1;
    }

    public int CountBlack(int x0, int y0, int x1, int y1)
    {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(Width, x1); y1 = Math.Min(Height, y1);
        int s = 0;
        for (int y = y0; y < y1; y++)
        {
            int row = y * Width;
            for (int x = x0; x < x1; x++) s += Data[row + x];
        }
        return s;
    }

    public void FillRect(int x0, int y0, int x1, int y1, bool value)
    {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(Width, x1); y1 = Math.Min(Height, y1);
        if (x1 <= x0 || y1 <= y0) return;
        byte v = value ? (byte)1 : (byte)0;
        for (int y = y0; y < y1; y++) Array.Fill(Data, v, y * Width + x0, x1 - x0);
    }
}
