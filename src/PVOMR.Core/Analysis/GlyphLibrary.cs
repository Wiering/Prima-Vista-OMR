using PVOMR.Core.Model;
using SkiaSharp;

namespace PVOMR.Core.Analysis;

/// <summary>A SMuFL glyph rendered from a font, normalised to a 32x32 bit mask plus its real size in staff spaces.</summary>
public sealed class GlyphTemplate
{
    public const int N = 32;
    public string Name = "";
    public string Font = "";
    public SymbolKind Kind;
    public double WidthSS, HeightSS;
    /// <summary>Bounding box top relative to the glyph origin (baseline) in staff spaces (negative = above the origin).</summary>
    public double TopSS;
    public ulong[] Bits = new ulong[N * N / 64];
    public ulong[] Dilated = new ulong[N * N / 64];
    public int Count;
    public override string ToString() => $"{Name} ({Font}) {WidthSS:0.00}x{HeightSS:0.00}ss";
}

public sealed class GlyphMatch
{
    public GlyphTemplate Template = null!;
    public double Score;
    public override string ToString() => $"{Template.Name} {Score:0.00}";
}

public sealed class GlyphLibrary
{
    public List<GlyphTemplate> Templates { get; } = new();
    /// <summary>Digits for bar numbers: the SMuFL tuplet and time signature digits and the digits of common text fonts
    /// (bar numbers are set in the text font, often bold or italic). Kept apart from the music glyphs.</summary>
    public List<GlyphTemplate> Digits { get; } = new();
    public List<string> Fonts { get; } = new();

    public static readonly (string name, int cp, SymbolKind kind)[] GlyphSet =
    {
        ("gClef", 0xE050, SymbolKind.Clef), ("fClef", 0xE062, SymbolKind.Clef), ("cClef", 0xE05C, SymbolKind.Clef),
        ("accidentalSharp", 0xE262, SymbolKind.Accidental), ("accidentalFlat", 0xE260, SymbolKind.Accidental),
        ("accidentalNatural", 0xE261, SymbolKind.Accidental), ("accidentalDoubleSharp", 0xE263, SymbolKind.Accidental),
        ("accidentalDoubleFlat", 0xE264, SymbolKind.Accidental),
        ("restWhole", 0xE4E3, SymbolKind.Rest), ("restHalf", 0xE4E4, SymbolKind.Rest), ("restQuarter", 0xE4E5, SymbolKind.Rest),
        ("rest8th", 0xE4E6, SymbolKind.Rest), ("rest16th", 0xE4E7, SymbolKind.Rest), ("rest32nd", 0xE4E8, SymbolKind.Rest),
        ("rest64th", 0xE4E9, SymbolKind.Rest),
        ("timeSig0", 0xE080, SymbolKind.TimeSignature), ("timeSig1", 0xE081, SymbolKind.TimeSignature), ("timeSig2", 0xE082, SymbolKind.TimeSignature),
        ("timeSig3", 0xE083, SymbolKind.TimeSignature), ("timeSig4", 0xE084, SymbolKind.TimeSignature), ("timeSig5", 0xE085, SymbolKind.TimeSignature),
        ("timeSig6", 0xE086, SymbolKind.TimeSignature), ("timeSig7", 0xE087, SymbolKind.TimeSignature), ("timeSig8", 0xE088, SymbolKind.TimeSignature),
        ("timeSig9", 0xE089, SymbolKind.TimeSignature), ("timeSigCommon", 0xE08A, SymbolKind.TimeSignature), ("timeSigCutCommon", 0xE08B, SymbolKind.TimeSignature),
        ("noteheadBlack", 0xE0A4, SymbolKind.Note), ("noteheadHalf", 0xE0A3, SymbolKind.Note), ("noteheadWhole", 0xE0A2, SymbolKind.Note),
        ("noteheadDoubleWhole", 0xE0A0, SymbolKind.Note),
        ("flag8thUp", 0xE240, SymbolKind.Flag), ("flag8thDown", 0xE241, SymbolKind.Flag), ("flag16thUp", 0xE242, SymbolKind.Flag),
        ("flag16thDown", 0xE243, SymbolKind.Flag),
        ("dynamicPiano", 0xE520, SymbolKind.Dynamic), ("dynamicForte", 0xE522, SymbolKind.Dynamic), ("dynamicMezzo", 0xE521, SymbolKind.Dynamic),
        ("dynamicSforzando", 0xE524, SymbolKind.Dynamic), ("dynamicPP", 0xE52B, SymbolKind.Dynamic), ("dynamicFF", 0xE52F, SymbolKind.Dynamic),
        ("dynamicMP", 0xE52C, SymbolKind.Dynamic), ("dynamicMF", 0xE52D, SymbolKind.Dynamic), ("dynamicFFF", 0xE530, SymbolKind.Dynamic),
        ("dynamicPPP", 0xE52A, SymbolKind.Dynamic), ("dynamicSforzato", 0xE539, SymbolKind.Dynamic), ("dynamicRinforzando2", 0xE53D, SymbolKind.Dynamic),
        ("augmentationDot", 0xE1E7, SymbolKind.Dot),
        ("articAccentAbove", 0xE4A0, SymbolKind.Articulation), ("articTenutoAbove", 0xE4A4, SymbolKind.Articulation),
        ("articMarcatoAbove", 0xE4AC, SymbolKind.Articulation), ("articStaccatissimoAbove", 0xE4A6, SymbolKind.Articulation),
        ("fermataAbove", 0xE4C0, SymbolKind.Fermata), ("fermataBelow", 0xE4C1, SymbolKind.Fermata),
        ("ornamentTrill", 0xE566, SymbolKind.Ornament), ("ornamentTurn", 0xE567, SymbolKind.Ornament), ("ornamentMordent", 0xE56D, SymbolKind.Ornament),
        ("ornamentShortTrill", 0xE56C, SymbolKind.Ornament),
        ("segno", 0xE047, SymbolKind.Other), ("coda", 0xE048, SymbolKind.Other),
        ("keyboardPedalPed", 0xE650, SymbolKind.Pedal), ("keyboardPedalUp", 0xE655, SymbolKind.Pedal),
        ("breathMarkComma", 0xE4CE, SymbolKind.Breath), ("caesura", 0xE4D1, SymbolKind.Breath),
        ("brace", 0xE000, SymbolKind.Brace),
        ("stringsDownBow", 0xE610, SymbolKind.Articulation), ("stringsUpBow", 0xE612, SymbolKind.Articulation),
        ("tuplet2", 0xE882, SymbolKind.Tuplet), ("tuplet3", 0xE883, SymbolKind.Tuplet), ("tuplet5", 0xE885, SymbolKind.Tuplet), ("tuplet6", 0xE886, SymbolKind.Tuplet), ("tuplet7", 0xE887, SymbolKind.Tuplet),
    };

    private static readonly string[] PreferredFonts = { "Bravura.otf", "Leland.otf", "Gootville.otf", "FinaleMaestro.otf", "MScore.otf", "Petaluma.otf", "Eugene.otf", "MuseJazz.otf" };

    public static GlyphLibrary? Load(string? fontDir, List<string> log)
    {
        fontDir ??= FindDefaultFontDir();
        if (fontDir == null || !Directory.Exists(fontDir)) { log.Add("no SMuFL font directory found; template classification disabled"); return null; }
        var lib = new GlyphLibrary();
        var files = Directory.GetFiles(fontDir, "*.otf").Concat(Directory.GetFiles(fontDir, "*.ttf")).ToList();
        var ordered = PreferredFonts.Select(p => files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), p, StringComparison.OrdinalIgnoreCase))).Where(f => f != null).Select(f => f!).ToList();
        foreach (var f in ordered)
        {
            try { lib.AddFont(f); lib.Fonts.Add(Path.GetFileNameWithoutExtension(f)); }
            catch (Exception ex) { log.Add($"font {Path.GetFileName(f)} failed: {ex.Message}"); }
        }
        foreach (var (family, bold, italic) in new[] { ("Times New Roman", false, false), ("Times New Roman", true, false), ("Times New Roman", false, true), ("Times New Roman", true, true),
                                                      ("Arial", false, false), ("Arial", true, false), ("Arial", false, true), ("Arial", true, true) })
        {
            try
            {
                using var tf = SKTypeface.FromFamilyName(family, bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
                if (tf != null) for (int d = 0; d <= 9; d++) lib.AddDigit(tf, ((char)('0' + d)).ToString(), d, $"{family}{(bold ? " bold" : "")}{(italic ? " italic" : "")}");
            }
            catch (Exception ex) { log.Add($"digit font {family} failed: {ex.Message}"); }
        }
        log.Add($"glyph templates: {lib.Templates.Count} from {lib.Fonts.Count} fonts ({string.Join(", ", lib.Fonts)})");
        return lib.Templates.Count > 0 ? lib : null;
    }

    private static string? FindDefaultFontDir()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "fonts"),
            Path.Combine(Directory.GetCurrentDirectory(), "fonts"),
            @"D:\OMR\fonts",
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

    private void AddFont(string path)
    {
        using var tf = SKTypeface.FromFile(path) ?? throw new IOException("cannot load typeface");
        const int ss = 40;                // render at 40 px per staff space
        float fontSize = 4 * ss;          // 1 em = 1 staff height = 4 staff spaces
        using var font = new SKFont(tf, fontSize) { Subpixel = false, Edging = SKFontEdging.Alias };
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = false, Style = SKPaintStyle.Fill };
        const int W = 16 * ss, H = 16 * ss;
        using var bmp = new SKBitmap(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        float ox = 4 * ss, oy = 8 * ss;   // origin (baseline) position on the canvas
        string fontName = Path.GetFileNameWithoutExtension(path);
        foreach (var (name, cp, kind) in GlyphSet)
        {
            string s = char.ConvertFromUtf32(cp);
            if (!tf.ContainsGlyphs(s)) continue;
            canvas.Clear(SKColors.White);
            canvas.DrawText(s, ox, oy, SKTextAlign.Left, font, paint);
            canvas.Flush();
            var px = bmp.GetPixelSpan();
            int x0 = W, y0 = H, x1 = -1, y1 = -1;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (px[(y * W + x) * 4 + 1] < 128) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
                }
            if (x1 < 0) continue;
            int w = x1 - x0 + 1, h = y1 - y0 + 1;
            var mask = new byte[w * h];
            int cnt = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (px[(y * W + x) * 4 + 1] < 128) { mask[(y - y0) * w + (x - x0)] = 1; cnt++; }
            if (cnt < 4) continue;
            var t = new GlyphTemplate { Name = name, Font = fontName, Kind = kind, WidthSS = w / (double)ss, HeightSS = h / (double)ss, TopSS = (y0 - oy) / ss };
            Normalize(mask, w, h, t.Bits);
            Dilate(t.Bits, t.Dilated);
            t.Count = PopCount(t.Bits);
            Templates.Add(t);
        }
        for (int d = 0; d <= 9; d++)
        {
            AddDigit(tf, char.ConvertFromUtf32(0xE880 + d), d, fontName + " tuplet");
            AddDigit(tf, char.ConvertFromUtf32(0xE080 + d), d, fontName + " timeSig");
        }
    }

    private void AddDigit(SKTypeface tf, string s, int digit, string fontName)
    {
        if (!tf.ContainsGlyphs(s)) return;
        const int size = 160, W = 320, H = 320;
        using var font = new SKFont(tf, size) { Subpixel = false, Edging = SKFontEdging.Alias };
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = false, Style = SKPaintStyle.Fill };
        using var bmp = new SKBitmap(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        canvas.DrawText(s, 60, 220, SKTextAlign.Left, font, paint);
        canvas.Flush();
        var px = bmp.GetPixelSpan();
        int x0 = W, y0 = H, x1 = -1, y1 = -1;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                if (px[(y * W + x) * 4 + 1] < 128) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
        if (x1 < 0) return;
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        var mask = new byte[w * h];
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                if (px[(y * W + x) * 4 + 1] < 128) mask[(y - y0) * w + (x - x0)] = 1;
        var t = new GlyphTemplate { Name = "digit" + digit, Font = fontName, Kind = SymbolKind.Number, WidthSS = w, HeightSS = h };
        Normalize(mask, w, h, t.Bits);
        Dilate(t.Bits, t.Dilated);
        t.Count = PopCount(t.Bits);
        Digits.Add(t);
    }

    /// <summary>The best digit for a glyph mask, judged by shape and proportions only (bar numbers come in any size).</summary>
    public (int digit, double score) MatchDigit(byte[] mask, int w, int h) => RankDigits(mask, w, h)[0];

    /// <summary>All ten digits for a glyph mask, best first.</summary>
    public List<(int digit, double score)> RankDigits(byte[] mask, int w, int h)
    {
        var bits = new ulong[GlyphTemplate.N * GlyphTemplate.N / 64];
        var dil = new ulong[bits.Length];
        Normalize(mask, w, h, bits);
        Dilate(bits, dil);
        int cnt = PopCount(bits);
        double aspect = w / (double)h;
        var best = new double[10];
        foreach (var t in Digits)
        {
            double s = Similarity(bits, dil, cnt, t.Bits, t.Dilated, t.Count) - 0.5 * Math.Abs(Math.Log(aspect / (t.WidthSS / t.HeightSS)));
            int d = t.Name[5] - '0';
            if (s > best[d]) best[d] = s;
        }
        return Enumerable.Range(0, 10).Select(d => (d, best[d])).OrderByDescending(p => p.Item2).ToList();
    }

    /// <summary>Resample a w x h byte mask into a 32x32 bitset (each cell set if at least half of its source pixels are ink).</summary>
    public static void Normalize(byte[] mask, int w, int h, ulong[] bits)
    {
        Array.Clear(bits);
        int n = GlyphTemplate.N;
        for (int j = 0; j < n; j++)
        {
            int sy0 = j * h / n, sy1 = Math.Max(sy0 + 1, (j + 1) * h / n);
            for (int i = 0; i < n; i++)
            {
                int sx0 = i * w / n, sx1 = Math.Max(sx0 + 1, (i + 1) * w / n);
                int tot = 0, ink = 0;
                for (int y = sy0; y < sy1 && y < h; y++)
                    for (int x = sx0; x < sx1 && x < w; x++) { tot++; ink += mask[y * w + x]; }
                if (tot > 0 && ink * 2 >= tot) bits[(j * n + i) >> 6] |= 1UL << ((j * n + i) & 63);
            }
        }
    }

    public static void Dilate(ulong[] src, ulong[] dst)
    {
        Array.Clear(dst);
        int n = GlyphTemplate.N;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                bool any = false;
                for (int dj = -1; dj <= 1 && !any; dj++)
                    for (int di = -1; di <= 1; di++)
                    {
                        int jj = j + dj, ii = i + di;
                        if (jj < 0 || ii < 0 || jj >= n || ii >= n) continue;
                        int b = jj * n + ii;
                        if ((src[b >> 6] & (1UL << (b & 63))) != 0) { any = true; break; }
                    }
                if (any) dst[(j * n + i) >> 6] |= 1UL << ((j * n + i) & 63);
            }
    }

    public static int PopCount(ulong[] a) { int c = 0; foreach (var v in a) c += System.Numerics.BitOperations.PopCount(v); return c; }

    /// <summary>Symmetric tolerant overlap: mean of (a within dilated b) and (b within dilated a).</summary>
    public static double Similarity(ulong[] a, ulong[] aDil, int aCount, ulong[] b, ulong[] bDil, int bCount)
    {
        int ab = 0, ba = 0;
        for (int i = 0; i < a.Length; i++)
        {
            ab += System.Numerics.BitOperations.PopCount(a[i] & bDil[i]);
            ba += System.Numerics.BitOperations.PopCount(b[i] & aDil[i]);
        }
        if (aCount == 0 || bCount == 0) return 0;
        double s1 = ab / (double)aCount, s2 = ba / (double)bCount;
        return Math.Min(s1, s2) * 0.6 + (s1 + s2) / 2 * 0.4;
    }

    /// <summary>Match a component mask of size (w,h) pixels at a given staff space against all templates of compatible size.</summary>
    public List<GlyphMatch> Match(byte[] mask, int w, int h, double ss, double sizeTolerance = 0.35, Func<GlyphTemplate, bool>? filter = null)
    {
        var bits = new ulong[GlyphTemplate.N * GlyphTemplate.N / 64];
        var dil = new ulong[bits.Length];
        Normalize(mask, w, h, bits);
        Dilate(bits, dil);
        int cnt = PopCount(bits);
        double wss = w / ss, hss = h / ss;
        var best = new Dictionary<string, GlyphMatch>();
        double lo = 1 - sizeTolerance, hi = 1 / (1 - sizeTolerance);
        foreach (var t in Templates)
        {
            if (filter != null && !filter(t)) continue;
            double rw = wss / t.WidthSS, rh = hss / t.HeightSS;
            if (rw < lo || rw > hi || rh < lo || rh > hi) continue;
            double sizePenalty = (Math.Abs(Math.Log(rw)) + Math.Abs(Math.Log(rh))) * 0.25;
            double s = Similarity(bits, dil, cnt, t.Bits, t.Dilated, t.Count) - sizePenalty;
            if (!best.TryGetValue(t.Name, out var m) || s > m.Score) best[t.Name] = new GlyphMatch { Template = t, Score = s };
        }
        return best.Values.OrderByDescending(m => m.Score).ToList();
    }
}
