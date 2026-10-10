using System.Globalization;
using System.Text;
using PVOMR.Core.Analysis;
using PVOMR.Core.Imaging;
using PVOMR.Core.Model;
using PVOMR.Core.Score;
using SkiaSharp;

namespace PVOMR.Core.Eval;

/// <summary>
/// Builds a labelled crop data set for the glyph classifier: connected components of the analysed synthetic pages are
/// labelled with the MuseScore SVG element they cover (head type and rest type come from the MusicXML truth), and
/// glyphs rendered from the SMuFL fonts are added with random degradations.
/// </summary>
public static class CropExtractor
{
    public const int Size = 40;

    public static readonly string[] Labels =
    {
        "head_black", "head_half", "head_whole", "rest_whole", "rest_half", "rest_quarter", "rest_8th", "rest_16th",
        "clef_g", "clef_f", "clef_c", "acc_sharp", "acc_flat", "acc_natural", "digit", "timesig_common", "dot", "dynamic",
        "text", "slur", "line", "beam", "flag", "fragment", "composite", "other",
    };

    public sealed class Stats { public Dictionary<string, int> Counts = new(); public int Pages; }

    public static Stats ExtractFromPages(string dir, string outDir, PageAnalyzer analyzer, double dpi, Random rnd, bool withAugment)
    {
        var stats = new Stats();
        Directory.CreateDirectory(outDir);
        var csv = new StringBuilder();
        string csvPath = Path.Combine(outDir, "index.csv");
        if (!File.Exists(csvPath)) csv.AppendLine("file,label,w_ss,h_ss,source");
        int fileNo = Directory.GetFiles(outDir, "*.png").Length;
        foreach (var xml in Directory.GetFiles(dir, "*.musicxml").OrderBy(f => f))
        {
            string stem = Path.GetFileNameWithoutExtension(xml);
            string pdf = Path.Combine(dir, stem + ".pdf");
            if (!File.Exists(pdf)) continue;
            var truth = MusicXmlReader.Read(xml);
            foreach (var page in Input.PageSource.Enumerate(pdf, null, dpi))
            {
                string svg = Path.Combine(dir, "svg", $"{stem}-{page.PageNumber}.svg");
                if (!File.Exists(svg)) continue;
                var img = page.Image;
                string aug = "";
                if (withAugment && rnd.NextDouble() < 0.6)
                {
                    aug = RandomAugment(rnd);
                    img = Augment.Apply(img, aug, rnd.Next());
                }
                var res = analyzer.Analyze(img, pdf, page.PageNumber, page.Dpi, null, out var ctx);
                if (ctx.Components == null || res.Staves.Count == 0) continue;
                var svgTruth = SvgTruth.Load(svg, page.Dpi);
                var labels = new ElementLabeller(svgTruth, truth);
                stats.Pages++;
                foreach (var c in ctx.Components.Components)
                {
                    if (c.PixelCount == 0) continue;
                    var staff = NearestStaff(res.Staves, c);
                    if (staff == null) continue;
                    double ss = staff.Space;
                    if (c.Width < 0.15 * ss && c.Height < 0.15 * ss) continue;
                    if (c.Width > 9 * ss || c.Height > 9 * ss) continue;
                    string label = labels.LabelFor(c);
                    if (label == "line" && rnd.NextDouble() < 0.7) continue;    // plenty of stems/barlines, keep a sample
                    if (label == "other" && rnd.NextDouble() < 0.5) continue;
                    var mask = ctx.Components.Mask(c);
                    string file = $"{fileNo++:000000}.png";
                    SaveCrop(mask, c.Width, c.Height, Path.Combine(outDir, file));
                    csv.AppendLine($"{file},{label},{(c.Width / ss).ToString("0.000", CultureInfo.InvariantCulture)},{(c.Height / ss).ToString("0.000", CultureInfo.InvariantCulture)},{stem}{(aug.Length > 0 ? "+aug" : "")}");
                    stats.Counts[label] = stats.Counts.GetValueOrDefault(label) + 1;
                }
            }
        }
        File.AppendAllText(csvPath, csv.ToString());
        return stats;
    }

    private static string RandomAugment(Random rnd)
    {
        var parts = new List<string>();
        if (rnd.NextDouble() < 0.6) parts.Add($"blur={0.5 + rnd.NextDouble() * 1.0:0.00}");
        if (rnd.NextDouble() < 0.5) parts.Add($"noise={0.02 + rnd.NextDouble() * 0.08:0.000}");
        if (rnd.NextDouble() < 0.4) parts.Add($"scale={0.55 + rnd.NextDouble() * 0.4:0.00}");
        if (rnd.NextDouble() < 0.3) parts.Add($"thick=1");
        if (rnd.NextDouble() < 0.2) parts.Add($"thin=1");
        if (rnd.NextDouble() < 0.3) parts.Add($"contrast={0.5 + rnd.NextDouble() * 0.4:0.00}");
        if (parts.Count == 0) parts.Add("blur=0.8");
        return string.Join(",", parts);
    }

    private static Staff? NearestStaff(List<Staff> staves, Component c)
    {
        Staff? best = null; double bd = double.MaxValue;
        foreach (var s in staves)
        {
            if (c.CenterX < s.Left - 3 * s.Space || c.CenterX > s.Right + 3 * s.Space) continue;
            double top = s.TopY(c.CenterX), bot = s.BottomY(c.CenterX);
            double d = c.CenterY < top ? top - c.CenterY : c.CenterY > bot ? c.CenterY - bot : 0;
            if (d < bd && d < 8 * s.Space) { bd = d; best = s; }
        }
        return best;
    }

    /// <summary>Writes a component mask as a Size x Size PNG (ink = white), aspect preserved, 10% margin.</summary>
    public static void SaveCrop(byte[] mask, int w, int h, string path)
    {
        var pixels = Normalize(mask, w, h);
        using var bmp = new SKBitmap(Size, Size, SKColorType.Gray8, SKAlphaType.Opaque);
        var span = bmp.GetPixelSpan();
        unsafe
        {
            fixed (byte* p = span)
                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++) p[y * bmp.RowBytes + x] = (byte)Math.Clamp((int)Math.Round(pixels[y * Size + x] * 255), 0, 255);
        }
        ImageIO.SavePng(bmp, path);
    }

    /// <summary>Resample a mask into a Size x Size float grid (0..1 ink coverage), aspect preserved and centred, 10% margin.</summary>
    public static float[] Normalize(byte[] mask, int w, int h)
    {
        var outp = new float[Size * Size];
        int inner = (int)Math.Round(Size * 0.8);
        double scale = Math.Min(inner / (double)w, inner / (double)h);
        int tw = Math.Max(1, (int)Math.Round(w * scale)), th = Math.Max(1, (int)Math.Round(h * scale));
        int ox = (Size - tw) / 2, oy = (Size - th) / 2;
        for (int ty = 0; ty < th; ty++)
        {
            int sy0 = (int)(ty / scale), sy1 = Math.Max(sy0 + 1, (int)((ty + 1) / scale));
            for (int tx = 0; tx < tw; tx++)
            {
                int sx0 = (int)(tx / scale), sx1 = Math.Max(sx0 + 1, (int)((tx + 1) / scale));
                int tot = 0, ink = 0;
                for (int y = sy0; y < sy1 && y < h; y++)
                    for (int x = sx0; x < sx1 && x < w; x++) { tot++; ink += mask[y * w + x]; }
                outp[(oy + ty) * Size + ox + tx] = tot == 0 ? 0 : ink / (float)tot;
            }
        }
        return outp;
    }

    // ------------------------------------------------------------------ head windows (head verifier training data)

    public const double HeadWindow = 2.0;   // window side in staff spaces

    /// <summary>Ink coverage grid (Size x Size) of a square window of HeadWindow staff spaces centred at (cx, cy).</summary>
    public static float[] WindowGrid(BinaryImage img, double cx, double cy, double ss)
    {
        var g = new float[Size * Size];
        double cell = HeadWindow * ss / Size;
        double x0 = cx - HeadWindow * ss / 2, y0 = cy - HeadWindow * ss / 2;
        for (int j = 0; j < Size; j++)
        {
            int sy0 = (int)Math.Floor(y0 + j * cell), sy1 = Math.Max(sy0 + 1, (int)Math.Floor(y0 + (j + 1) * cell));
            for (int i = 0; i < Size; i++)
            {
                int sx0 = (int)Math.Floor(x0 + i * cell), sx1 = Math.Max(sx0 + 1, (int)Math.Floor(x0 + (i + 1) * cell));
                int tot = 0, ink = 0;
                for (int y = sy0; y < sy1; y++)
                    for (int x = sx0; x < sx1; x++) { tot++; if (img[x, y]) ink++; }
                g[j * Size + i] = tot == 0 ? 0 : ink / (float)tot;
            }
        }
        return g;
    }

    public static Stats ExtractHeadWindows(string dir, string outDir, PageAnalyzer analyzer, double dpi, Random rnd, bool withAugment)
    {
        var stats = new Stats();
        Directory.CreateDirectory(outDir);
        var csv = new StringBuilder();
        string csvPath = Path.Combine(outDir, "index.csv");
        if (!File.Exists(csvPath)) csv.AppendLine("file,label,w_ss,h_ss,source");
        int fileNo = Directory.GetFiles(outDir, "*.png").Length;
        foreach (var xml in Directory.GetFiles(dir, "*.musicxml").OrderBy(f => f))
        {
            string stem = Path.GetFileNameWithoutExtension(xml);
            string pdf = Path.Combine(dir, stem + ".pdf");
            if (!File.Exists(pdf)) continue;
            var truth = MusicXmlReader.Read(xml);
            foreach (var page in Input.PageSource.Enumerate(pdf, null, dpi))
            {
                string svg = Path.Combine(dir, "svg", $"{stem}-{page.PageNumber}.svg");
                if (!File.Exists(svg)) continue;
                var img = page.Image;
                string aug = "";
                if (withAugment && rnd.NextDouble() < 0.6) { aug = RandomAugment(rnd); img = Augment.Apply(img, aug, rnd.Next()); }
                var res = analyzer.Analyze(img, pdf, page.PageNumber, page.Dpi, null, out var ctx);
                if (res.Staves.Count == 0) continue;
                var svgTruth = SvgTruth.Load(svg, page.Dpi);
                var labels = new ElementLabeller(svgTruth, truth);
                stats.Pages++;
                var heads = svgTruth.Elements.Where(e => e.Class == "Note").ToList();
                void Emit(double cx, double cy, double ss, string label)
                {
                    var grid = WindowGrid(ctx.NoStaff, cx, cy, ss);
                    string file = $"{fileNo++:000000}.png";
                    SaveGrid(grid, Path.Combine(outDir, file));
                    csv.AppendLine($"{file},{label},{HeadWindow.ToString("0.0", CultureInfo.InvariantCulture)},{HeadWindow.ToString("0.0", CultureInfo.InvariantCulture)},{stem}{(aug.Length > 0 ? "+aug" : "")}");
                    stats.Counts[label] = stats.Counts.GetValueOrDefault(label) + 1;
                }
                // positives: every head, with small jitter (two samples each)
                foreach (var h in heads)
                {
                    var st = svgTruth.StaffAt(h.CenterX, h.CenterY); if (st == null) continue;
                    double ss = st.Space;
                    string label = labels.LabelOf(h);
                    if (label == "head_whole") continue;            // whole notes have no stem: handled by the glyph classifier
                    if (label != "head_black" && label != "head_half") continue;
                    for (int k = 0; k < 2; k++)
                        Emit(h.CenterX + (rnd.NextDouble() - 0.5) * 0.3 * ss, h.CenterY + (rnd.NextDouble() - 0.5) * 0.3 * ss, ss, label);
                }
                // negatives: points along stems and beams away from heads, half-way positions, random symbol spots
                bool NearHead(double x, double y, double ss) => heads.Any(h => Math.Abs(h.CenterX - x) < 0.8 * ss && Math.Abs(h.CenterY - y) < 0.7 * ss);
                foreach (var e in svgTruth.Elements.Where(e => e.Class is "Stem" or "Beam" or "Hook" or "Rest" or "Accidental" or "KeySig" or "Clef" or "Dynamic" or "NoteDot" or "TimeSig"))
                {
                    var st = svgTruth.StaffAt(e.CenterX, e.CenterY); if (st == null) continue;
                    double ss = st.Space;
                    int samples = e.Class == "Stem" ? 3 : 1;
                    for (int k = 0; k < samples; k++)
                    {
                        double x = e.X0 + rnd.NextDouble() * Math.Max(1, e.Width), y = e.Y0 + rnd.NextDouble() * Math.Max(1, e.Height);
                        if (e.Class == "Stem") x += (rnd.NextDouble() < 0.5 ? -1 : 1) * 0.55 * ss;   // where a head candidate would sit
                        if (NearHead(x, y, ss)) continue;
                        Emit(x, y, ss, "none");
                    }
                }
                // near-miss negatives: head positions shifted by one staff space (the next line/space) and horizontally by a head width
                foreach (var h in heads.Take(heads.Count / 2))
                {
                    var st = svgTruth.StaffAt(h.CenterX, h.CenterY); if (st == null) continue;
                    double ss = st.Space;
                    double x = h.CenterX, y = h.CenterY + (rnd.NextDouble() < 0.5 ? -1 : 1) * ss;
                    if (!NearHead(x, y, ss)) Emit(x, y, ss, "none");
                }
            }
        }
        File.AppendAllText(csvPath, csv.ToString());
        return stats;
    }

    private static void SaveGrid(float[] grid, string path)
    {
        using var bmp = new SKBitmap(Size, Size, SKColorType.Gray8, SKAlphaType.Opaque);
        var span = bmp.GetPixelSpan();
        unsafe
        {
            fixed (byte* p = span)
                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++) p[y * bmp.RowBytes + x] = (byte)Math.Clamp((int)Math.Round(grid[y * Size + x] * 255), 0, 255);
        }
        ImageIO.SavePng(bmp, path);
    }

    // ------------------------------------------------------------------ font glyph crops

    private static readonly (string glyph, string label)[] FontGlyphs =
    {
        ("noteheadBlack", "head_black"), ("noteheadHalf", "head_half"), ("noteheadWhole", "head_whole"),
        ("restWhole", "rest_whole"), ("restHalf", "rest_half"), ("restQuarter", "rest_quarter"), ("rest8th", "rest_8th"), ("rest16th", "rest_16th"),
        ("gClef", "clef_g"), ("fClef", "clef_f"), ("cClef", "clef_c"),
        ("accidentalSharp", "acc_sharp"), ("accidentalFlat", "acc_flat"), ("accidentalNatural", "acc_natural"),
        ("timeSig0", "digit"), ("timeSig1", "digit"), ("timeSig2", "digit"), ("timeSig3", "digit"), ("timeSig4", "digit"), ("timeSig5", "digit"),
        ("timeSig6", "digit"), ("timeSig7", "digit"), ("timeSig8", "digit"), ("timeSig9", "digit"), ("timeSigCommon", "timesig_common"), ("timeSigCutCommon", "timesig_common"),
        ("augmentationDot", "dot"),
        ("dynamicPiano", "dynamic"), ("dynamicForte", "dynamic"), ("dynamicMezzo", "dynamic"), ("dynamicPP", "dynamic"), ("dynamicFF", "dynamic"), ("dynamicMF", "dynamic"), ("dynamicMP", "dynamic"), ("dynamicSforzando", "dynamic"),
        ("flag8thUp", "flag"), ("flag8thDown", "flag"), ("flag16thUp", "flag"), ("flag16thDown", "flag"),
    };

    public static Stats ExtractFromFonts(string fontDir, string outDir, int variantsPerGlyph, Random rnd)
    {
        var stats = new Stats();
        Directory.CreateDirectory(outDir);
        var csv = new StringBuilder();
        string csvPath = Path.Combine(outDir, "index.csv");
        if (!File.Exists(csvPath)) csv.AppendLine("file,label,w_ss,h_ss,source");
        int fileNo = Directory.GetFiles(outDir, "*.png").Length;
        var fonts = Directory.GetFiles(fontDir, "*.otf").Where(f => !Path.GetFileName(f).Contains("Text")).ToList();
        foreach (var fontPath in fonts)
        {
            using var tf = SKTypeface.FromFile(fontPath);
            if (tf == null) continue;
            string fontName = Path.GetFileNameWithoutExtension(fontPath);
            foreach (var (glyph, label) in FontGlyphs)
            {
                int cp = GlyphLibrary.GlyphSet.FirstOrDefault(g => g.name == glyph).cp;
                if (cp == 0) continue;
                string s = char.ConvertFromUtf32(cp);
                if (!tf.ContainsGlyphs(s)) continue;
                for (int v = 0; v < variantsPerGlyph; v++)
                {
                    double ss = 14 + rnd.NextDouble() * 16;      // staff space in pixels
                    var gray = RenderGlyph(tf, s, ss, rnd);
                    if (gray == null) continue;
                    string aug = RandomAugment(rnd);
                    if (rnd.NextDouble() < 0.5) gray = Augment.Apply(gray, aug, rnd.Next());
                    var bin = Binarizer.Otsu(gray, 128);
                    // largest component only (keeps the glyph when noise adds specks)
                    var cm = ConnectedComponents.Label(bin);
                    var comp = cm.Components.OrderByDescending(c => c.PixelCount).FirstOrDefault();
                    if (comp == null || comp.PixelCount < 4) continue;
                    var mask = cm.Mask(comp);
                    string file = $"{fileNo++:000000}.png";
                    SaveCrop(mask, comp.Width, comp.Height, Path.Combine(outDir, file));
                    csv.AppendLine($"{file},{label},{(comp.Width / ss).ToString("0.000", CultureInfo.InvariantCulture)},{(comp.Height / ss).ToString("0.000", CultureInfo.InvariantCulture)},font:{fontName}");
                    stats.Counts[label] = stats.Counts.GetValueOrDefault(label) + 1;
                }
            }
        }
        File.AppendAllText(csvPath, csv.ToString());
        return stats;
    }

    private static GrayImage? RenderGlyph(SKTypeface tf, string s, double ss, Random rnd)
    {
        float fontSize = (float)(4 * ss);
        using var font = new SKFont(tf, fontSize) { Subpixel = true, Edging = SKFontEdging.Antialias };
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Fill };
        int W = (int)(10 * ss), H = (int)(12 * ss);
        using var bmp = new SKBitmap(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        canvas.RotateDegrees((float)(rnd.NextDouble() * 4 - 2), W / 2f, H / 2f);
        canvas.DrawText(s, (float)(2 * ss), (float)(7 * ss), SKTextAlign.Left, font, paint);
        canvas.Flush();
        var g = ImageIO.FromBitmap(bmp);
        // crop to ink bounds with margin
        int x0 = W, y0 = H, x1 = -1, y1 = -1;
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) if (g[x, y] < 128) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        if (x1 < 0) return null;
        int m = (int)ss;
        x0 = Math.Max(0, x0 - m); y0 = Math.Max(0, y0 - m); x1 = Math.Min(W - 1, x1 + m); y1 = Math.Min(H - 1, y1 + m);
        var r = new GrayImage(x1 - x0 + 1, y1 - y0 + 1);
        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) r[x - x0, y - y0] = g[x, y];
        return r;
    }
}

/// <summary>Assigns a class label to a connected component using the SVG elements and the MusicXML truth.</summary>
public sealed class ElementLabeller
{
    private readonly SvgTruth _svg;
    private readonly Dictionary<SvgElement, string> _elementLabel = new();

    public ElementLabeller(SvgTruth svg, Score.Score truth)
    {
        _svg = svg;
        // default labels from class
        foreach (var e in svg.Elements)
            _elementLabel[e] = e.Class switch
            {
                "Note" => e.SubPaths >= 2 ? (e.Width >= 1.4 * SpaceAt(e) ? "head_whole" : "head_half") : "head_black",
                "Rest" => RestLabelByShape(e),
                "Clef" => "clef_g", "KeySig" => "acc_flat", "Accidental" => "acc_sharp", "TimeSig" => "digit", "NoteDot" => "dot",
                "Dynamic" => "dynamic", "Text" or "MeasureNumber" or "InstrumentName" or "Title" or "Subtitle" or "Composer" or "Lyrics" or "Tempo" => "text",
                "SlurSegment" or "TieSegment" => "slur", "Stem" or "BarLine" or "LedgerLine" => "line", "Beam" => "beam", "Hook" => "flag",
                "Tuplet" => "digit",
                "Fermata" or "Articulation" or "HairpinSegment" => "other",
                _ => "other",
            };
        RefineFromTruth(truth);
    }

    private double SpaceAt(SvgElement e) => _svg.StaffAt(e.CenterX, e.CenterY)?.Space ?? 20;

    private string RestLabelByShape(SvgElement e)
    {
        var st = _svg.StaffAt(e.CenterX, e.CenterY);
        double ss = st?.Space ?? 20;
        double h = e.Height / ss, w = e.Width / ss;
        if (h < 0.75)
        {
            if (st != null)
            {
                double line = st.LineY.OrderBy(l => Math.Min(Math.Abs(l - e.Y0), Math.Abs(l - e.Y1))).First();
                return Math.Abs(line - e.Y0) < Math.Abs(line - e.Y1) ? "rest_whole" : "rest_half";
            }
            return "rest_whole";
        }
        if (h < 2.3) return "rest_8th";
        return w > 1.15 ? "rest_16th" : "rest_quarter";
    }

    /// <summary>Head types, rest types, clefs, key signature and accidental kinds from the MusicXML truth, matched measure by measure.</summary>
    private void RefineFromTruth(Score.Score truth)
    {
        if (truth.Parts.Count == 0 || _svg.Staves.Count == 0) return;
        var part = truth.Parts[0];
        int staffCount = Math.Max(1, part.StaffCount);
        var staves = _svg.Staves.OrderBy(s => s.Top).ToList();
        // clefs and key signature kind
        for (int i = 0; i < staves.Count; i++)
        {
            int staffInPart = i % staffCount;
            var clef = part.Measures[0].Clefs[staffInPart] ?? (staffInPart == 1 ? ClefSpec.Bass : ClefSpec.Treble);
            string clefLabel = clef.Sign == "F" ? "clef_f" : clef.Sign == "C" ? "clef_c" : "clef_g";
            foreach (var e in _svg.Elements.Where(e => e.Class == "Clef" && _svg.StaffAt(e.CenterX, e.CenterY) == staves[i])) _elementLabel[e] = clefLabel;
        }
        int fifths = part.Measures[0].KeyFifths ?? 0;
        foreach (var e in _svg.Elements.Where(e => e.Class == "KeySig")) _elementLabel[e] = fifths < 0 ? "acc_flat" : "acc_sharp";
        var time = part.Measures[0].Time;
        if (time?.Symbol != null) foreach (var e in _svg.Elements.Where(e => e.Class == "TimeSig")) _elementLabel[e] = "timesig_common";

        // measure-wise matching of heads and rests
        int systems = staves.Count / staffCount;
        int measureIdx = 0;
        for (int sys = 0; sys < systems && measureIdx < part.Measures.Count; sys++)
        {
            var top = staves[sys * staffCount];
            var xs = new List<double> { top.Left };
            xs.AddRange(top.Barlines.Where(b => b > top.Left + top.Space));
            if (xs[^1] < top.Right - top.Space) xs.Add(top.Right);
            int measuresInSystem = xs.Count - 1;
            for (int m = 0; m < measuresInSystem && measureIdx < part.Measures.Count; m++, measureIdx++)
            {
                var tm = part.Measures[measureIdx];
                double mx0 = xs[m], mx1 = xs[m + 1];
                for (int si = 0; si < staffCount; si++)
                {
                    var st = staves[sys * staffCount + si];
                    var evs = tm.Events.Where(e => e.Staff == si + 1).OrderBy(e => e.X).ToList();
                    // heads
                    var truthHeads = evs.Where(e => !e.IsRest).SelectMany(e => e.Notes.OrderByDescending(n => PitchOrder(n.Pitch)).Select(n => (e, n))).ToList();
                    var svgHeads = _svg.Elements.Where(e => e.Class == "Note" && _svg.StaffAt(e.CenterX, e.CenterY) == st && e.CenterX >= mx0 && e.CenterX < mx1)
                                                .OrderBy(e => Math.Round(e.CenterX / (0.5 * st.Space))).ThenBy(e => e.CenterY).ToList();
                    if (truthHeads.Count == svgHeads.Count)
                        for (int k = 0; k < svgHeads.Count; k++)
                        {
                            var (ev, note) = truthHeads[k];
                            _elementLabel[svgHeads[k]] = ev.Type == "whole" || ev.Type == "breve" ? "head_whole" : ev.Type == "half" ? "head_half" : "head_black";
                            if (note.AccidentalName != null)
                            {
                                var acc = _svg.Elements.Where(e => e.Class == "Accidental" && e.X1 <= svgHeads[k].X0 + 2 && e.X1 >= svgHeads[k].X0 - 1.6 * st.Space && Math.Abs(e.CenterY - svgHeads[k].CenterY) < 1.2 * st.Space)
                                                       .OrderBy(e => svgHeads[k].X0 - e.X1).FirstOrDefault();
                                if (acc != null) _elementLabel[acc] = note.AccidentalName switch { "flat" => "acc_flat", "natural" => "acc_natural", _ => "acc_sharp" };
                            }
                        }
                    // rests
                    var truthRests = evs.Where(e => e.IsRest).ToList();
                    var svgRests = _svg.Elements.Where(e => e.Class == "Rest" && _svg.StaffAt(e.CenterX, e.CenterY) == st && e.CenterX >= mx0 && e.CenterX < mx1).OrderBy(e => e.CenterX).ToList();
                    if (truthRests.Count == svgRests.Count)
                        for (int k = 0; k < svgRests.Count; k++)
                            _elementLabel[svgRests[k]] = truthRests[k].Type switch { "whole" => "rest_whole", "half" => "rest_half", "quarter" => "rest_quarter", "eighth" => "rest_8th", "16th" => "rest_16th", _ => _elementLabel[svgRests[k]] };
                }
            }
        }
    }

    private static int PitchOrder(PitchSpec p) => p.Octave * 7 + Array.IndexOf(new[] { "C", "D", "E", "F", "G", "A", "B" }, p.Step);

    public string LabelOf(SvgElement e) => _elementLabel.TryGetValue(e, out var l) ? l : "other";

    /// <summary>Label of a component: the element(s) whose boxes cover its pixels.</summary>
    public string LabelFor(Component c)
    {
        var hits = new List<(SvgElement e, double coverOfComp, double coverOfElem)>();
        foreach (var e in _svg.Elements)
        {
            double ix0 = Math.Max(c.X0, e.X0), iy0 = Math.Max(c.Y0, e.Y0), ix1 = Math.Min(c.X1 + 1, e.X1), iy1 = Math.Min(c.Y1 + 1, e.Y1);
            if (ix1 <= ix0 || iy1 <= iy0) continue;
            double inter = (ix1 - ix0) * (iy1 - iy0);
            double compArea = c.Width * (double)c.Height, elemArea = Math.Max(1, e.Width * e.Height);
            hits.Add((e, inter / compArea, inter / elemArea));
        }
        if (hits.Count == 0) return "other";
        var glyphHits = hits.Where(h => h.e.Class is not ("Stem" or "LedgerLine" or "BarLine" or "StaffLines") && h.coverOfComp > 0.2).ToList();
        var lineHits = hits.Where(h => h.e.Class is "Stem" or "LedgerLine" or "BarLine").ToList();
        if (glyphHits.Count == 0)
        {
            if (lineHits.Count > 0 && lineHits.Max(h => h.coverOfComp) > 0.5) return "line";
            return "other";
        }
        var distinctClasses = glyphHits.Select(h => _elementLabel[h.e]).Distinct().ToList();
        var best = glyphHits.OrderByDescending(h => h.coverOfComp * h.coverOfElem).First();
        bool hasStemOrBeam = lineHits.Any(h => h.e.Class == "Stem" && h.coverOfElem > 0.5) || glyphHits.Any(h => h.e.Class is "Beam" or "Hook" && h.coverOfElem > 0.5 && _elementLabel[best.e].StartsWith("head"));
        if (distinctClasses.Count > 1 || (hasStemOrBeam && _elementLabel[best.e].StartsWith("head"))) return "composite";
        if (glyphHits.Count(h => _elementLabel[h.e] == _elementLabel[best.e] && h.coverOfElem > 0.5) > 1) return "composite";
        if (best.coverOfElem < 0.6 || best.coverOfComp < 0.6) return "fragment";
        return _elementLabel[best.e];
    }
}
