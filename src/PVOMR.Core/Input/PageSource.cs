using PDFtoImage;
using PVOMR.Core.Imaging;

namespace PVOMR.Core.Input;

public sealed record PageImage(string Source, int PageNumber, int PageCount, GrayImage Image, double Dpi, bool FromPdf);

public static class PageSource
{
    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".webp", ".gif" };

    public static bool IsPdf(string path) => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);
    public static bool IsImage(string path) => ImageExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Parse page selections like "1-3,5,8-" (1-based). Null = all.</summary>
    public static HashSet<int>? ParsePages(string? spec, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var set = new HashSet<int>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int dash = part.IndexOf('-');
            if (dash < 0) { set.Add(int.Parse(part)); continue; }
            int a = dash == 0 ? 1 : int.Parse(part[..dash]);
            int b = dash == part.Length - 1 ? pageCount : int.Parse(part[(dash + 1)..]);
            for (int i = a; i <= b; i++) set.Add(i);
        }
        return set;
    }

    public static int GetPdfPageCount(string path)
    {
        using var fs = File.OpenRead(path);
        return Conversion.GetPageCount(fs);
    }

    /// <summary>Effective DPI for a page: the requested DPI refers to a normal (A4/letter) page; oddly sized pages are scaled so the short side is about the same pixel size.</summary>
    public static double EffectiveDpi(string path, int pageNumber1Based, double requestedDpi)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var size = Conversion.GetPageSize(fs, new Index(pageNumber1Based - 1));
            double shortSideInches = Math.Min(size.Width, size.Height) / 72.0;
            if (shortSideInches <= 0) return requestedDpi;
            // normal pages (and double-page spreads) keep the requested DPI; only clearly wrong page sizes are rescaled
            if (shortSideInches >= 5.5 && shortSideInches <= 13) return requestedDpi;
            double scale = (shortSideInches < 5.5 ? 8.27 : 11.7) / shortSideInches;
            return Math.Round(requestedDpi * Math.Clamp(scale, 0.2, 5));
        }
        catch { return requestedDpi; }
    }

    public static GrayImage RenderPdfPage(string path, int pageNumber1Based, double dpi)
    {
        using var fs = File.OpenRead(path);
        using var bmp = Conversion.ToImage(fs, new Index(pageNumber1Based - 1), false, null,
            new RenderOptions(Dpi: (int)Math.Round(dpi), WithAnnotations: false, AntiAliasing: PdfAntiAliasing.All, BackgroundColor: SkiaSharp.SKColors.White));
        return ImageIO.FromBitmap(bmp);
    }

    public static IEnumerable<PageImage> Enumerate(string path, string? pages, double dpi)
    {
        if (IsPdf(path))
        {
            int count = GetPdfPageCount(path);
            var sel = ParsePages(pages, count);
            for (int p = 1; p <= count; p++)
            {
                if (sel != null && !sel.Contains(p)) continue;
                double eff = EffectiveDpi(path, p, dpi);
                yield return new PageImage(path, p, count, RenderPdfPage(path, p, eff), eff, true);
            }
        }
        else if (IsImage(path))
        {
            yield return new PageImage(path, 1, 1, ImageIO.Load(path), dpi, false);
        }
        else throw new NotSupportedException($"Unsupported input: {path}");
    }
}
