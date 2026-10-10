namespace PVOMR.Core.Imaging;

public sealed class Component
{
    public int Label;
    public int X0, Y0, X1, Y1; // inclusive bbox
    public int PixelCount;
    public int Width => X1 - X0 + 1;
    public int Height => Y1 - Y0 + 1;
    public double CenterX => (X0 + X1) / 2.0;
    public double CenterY => (Y0 + Y1) / 2.0;
    public double Fill => PixelCount / (double)(Width * Height);
    public override string ToString() => $"#{Label} [{X0},{Y0}-{X1},{Y1}] n={PixelCount}";
}

public sealed class ComponentMap
{
    public int Width, Height;
    public int[] Labels = Array.Empty<int>();   // 0 = background
    public List<Component> Components = new();  // index = label-1

    public Component? At(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return null;
        int l = Labels[y * Width + x];
        return l == 0 ? null : Components[l - 1];
    }

    public bool Is(int x, int y, int label) => (uint)x < (uint)Width && (uint)y < (uint)Height && Labels[y * Width + x] == label;

    /// <summary>Relabel all pixels of b as a and grow a's bounding box; b becomes empty (PixelCount 0).</summary>
    public void Merge(Component a, Component b)
    {
        if (a == b || b.PixelCount == 0) return;
        for (int y = b.Y0; y <= b.Y1; y++)
            for (int x = b.X0; x <= b.X1; x++)
                if (Labels[y * Width + x] == b.Label) Labels[y * Width + x] = a.Label;
        a.X0 = Math.Min(a.X0, b.X0); a.Y0 = Math.Min(a.Y0, b.Y0); a.X1 = Math.Max(a.X1, b.X1); a.Y1 = Math.Max(a.Y1, b.Y1);
        a.PixelCount += b.PixelCount;
        b.PixelCount = 0;
    }

    /// <summary>Extract the mask (1 = pixel of this component) of a component within its bounding box.</summary>
    public byte[] Mask(Component c)
    {
        var m = new byte[c.Width * c.Height];
        for (int y = c.Y0; y <= c.Y1; y++)
            for (int x = c.X0; x <= c.X1; x++)
                if (Labels[y * Width + x] == c.Label) m[(y - c.Y0) * c.Width + (x - c.X0)] = 1;
        return m;
    }
}

public static class ConnectedComponents
{
    /// <summary>8-connected labelling with union-find.</summary>
    public static ComponentMap Label(BinaryImage img)
    {
        int w = img.Width, h = img.Height;
        var labels = new int[w * h];
        var parent = new List<int> { 0 };
        int Find(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) { if (a < b) parent[b] = a; else parent[a] = b; } }

        var d = img.Data;
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                if (d[row + x] == 0) continue;
                int l = 0;
                if (x > 0 && labels[row + x - 1] != 0) l = labels[row + x - 1];
                if (y > 0)
                {
                    int up = row - w + x;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx;
                        if (xx < 0 || xx >= w) continue;
                        int n = labels[up + dx];
                        if (n == 0) continue;
                        if (l == 0) l = n; else if (n != l) Union(l, n);
                    }
                }
                if (l == 0) { l = parent.Count; parent.Add(l); }
                labels[row + x] = l;
            }
        }
        var remap = new int[parent.Count];
        var comps = new List<Component>();
        for (int i = 1; i < parent.Count; i++)
        {
            int r = Find(i);
            if (remap[r] == 0) { remap[r] = comps.Count + 1; comps.Add(new Component { Label = comps.Count + 1, X0 = int.MaxValue, Y0 = int.MaxValue, X1 = -1, Y1 = -1 }); }
            remap[i] = remap[r];
        }
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int l = labels[row + x];
                if (l == 0) continue;
                l = remap[l];
                labels[row + x] = l;
                var c = comps[l - 1];
                c.PixelCount++;
                if (x < c.X0) c.X0 = x;
                if (x > c.X1) c.X1 = x;
                if (y < c.Y0) c.Y0 = y;
                if (y > c.Y1) c.Y1 = y;
            }
        }
        return new ComponentMap { Width = w, Height = h, Labels = labels, Components = comps };
    }
}
