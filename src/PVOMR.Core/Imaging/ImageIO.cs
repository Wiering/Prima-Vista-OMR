using SkiaSharp;

namespace PVOMR.Core.Imaging;

public static class ImageIO
{
    public static GrayImage FromBitmap(SKBitmap bmp)
    {
        var g = new GrayImage(bmp.Width, bmp.Height);
        if (bmp.ColorType == SKColorType.Gray8)
        {
            var span = bmp.GetPixelSpan();
            for (int y = 0; y < bmp.Height; y++)
                span.Slice(y * bmp.RowBytes, bmp.Width).CopyTo(g.Data.AsSpan(y * bmp.Width, bmp.Width));
            return g;
        }
        SKBitmap? conv = null;
        if (bmp.ColorType != SKColorType.Bgra8888 && bmp.ColorType != SKColorType.Rgba8888)
            conv = bmp.Copy(SKColorType.Bgra8888);
        var src = conv ?? bmp;
        var pixels = src.GetPixelSpan();
        bool bgra = src.ColorType == SKColorType.Bgra8888;
        int n = src.Width * src.Height;
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            int b = pixels[o], gch = pixels[o + 1], r = pixels[o + 2], a = pixels[o + 3];
            if (!bgra) (b, r) = (r, b);
            int lum = (r * 299 + gch * 587 + b * 114) / 1000;
            if (a < 255) lum = (lum * a + 255 * (255 - a)) / 255; // composite on white
            g.Data[i] = (byte)lum;
        }
        conv?.Dispose();
        return g;
    }

    public static GrayImage Load(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".tif" or ".tiff") return LoadTiff(path);
        using var bmp = SKBitmap.Decode(path) ?? throw new IOException($"Cannot decode image: {path}");
        return FromBitmap(bmp);
    }

    private static GrayImage LoadTiff(string path)
    {
        using var tif = BitMiracle.LibTiff.Classic.Tiff.Open(path, "r") ?? throw new IOException($"Cannot open TIFF: {path}");
        int w = tif.GetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGEWIDTH)[0].ToInt();
        int h = tif.GetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGELENGTH)[0].ToInt();
        var raster = new int[w * h];
        if (!tif.ReadRGBAImageOriented(w, h, raster, BitMiracle.LibTiff.Classic.Orientation.TOPLEFT)) throw new IOException($"Cannot decode TIFF: {path}");
        var g = new GrayImage(w, h);
        for (int i = 0; i < raster.Length; i++)
        {
            int v = raster[i];
            int r = v & 0xff, gg = (v >> 8) & 0xff, b = (v >> 16) & 0xff;
            g.Data[i] = (byte)((r * 299 + gg * 587 + b * 114) / 1000);
        }
        return g;
    }

    public static SKBitmap ToBitmap(GrayImage g)
    {
        var bmp = new SKBitmap(g.Width, g.Height, SKColorType.Gray8, SKAlphaType.Opaque);
        var span = bmp.GetPixelSpan();
        unsafe
        {
            fixed (byte* p = span)
            {
                for (int y = 0; y < g.Height; y++)
                    g.Data.AsSpan(y * g.Width, g.Width).CopyTo(new Span<byte>(p + y * bmp.RowBytes, g.Width));
            }
        }
        return bmp;
    }

    public static SKBitmap ToBitmap(BinaryImage b)
    {
        var g = new GrayImage(b.Width, b.Height);
        for (int i = 0; i < g.Data.Length; i++) g.Data[i] = b.Data[i] != 0 ? (byte)0 : (byte)255;
        return ToBitmap(g);
    }

    public static void SavePng(SKBitmap bmp, string path)
    {
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }
}
