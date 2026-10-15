using System.Diagnostics;
using PVOMR.Core.Imaging;
using PVOMR.Core.Model;

namespace PVOMR.Core.Analysis;

/// <summary>Intermediate images kept for debugging / overlays.</summary>
public sealed class AnalysisContext
{
    public GrayImage Gray { get; set; } = null!;
    public BinaryImage Binary { get; set; } = null!;
    public BinaryImage NoStaff { get; set; } = null!;
    public ComponentMap? Components { get; set; }
}

public sealed class PageAnalyzer
{
    private readonly AnalysisOptions _opt;
    private readonly GlyphLibrary? _glyphs;
    private readonly GlyphClassifier? _classifier;
    private readonly GlyphClassifier? _headVerifier;

    public PageAnalyzer(AnalysisOptions opt, GlyphLibrary? glyphs, GlyphClassifier? classifier = null, GlyphClassifier? headVerifier = null)
    {
        _opt = opt;
        _glyphs = glyphs;
        _classifier = classifier;
        _headVerifier = headVerifier;
    }

    /// <summary>Analyse a page. <paramref name="rerender"/> may be used to obtain the page at a higher DPI.</summary>
    public PageAnalysis Analyze(GrayImage gray, string source, int pageNumber, double dpi, Func<double, GrayImage>? rerender, out AnalysisContext ctx, PageAnalysis? previous = null)
    {
        var sw = Stopwatch.StartNew();
        var result = new PageAnalysis { Source = source, PageNumber = pageNumber, Dpi = dpi };
        ctx = new AnalysisContext();

        // 1. binarize (coarse) + deskew; 2. staves (repeated once with the staff slope as the precise skew)
        string binDesc = "";
        BinaryImage Bin(GrayImage g, double d)
        {
            var b = Binarizer.Binarize(g, _opt.Binarization, SauvolaWindow(d), out binDesc);
            string cleaned = BorderCleaner.Clean(b);
            if (cleaned.Length > 0) binDesc += ", " + cleaned;
            return b;
        }
        var bin0 = Bin(gray, dpi);
        double angle = 0;
        if (_opt.Deskew)
        {
            angle = -Deskew.EstimateAngle(bin0);
            if (Math.Abs(angle) >= 0.1)
            {
                gray = gray.Rotate(angle);
                bin0 = Bin(gray, dpi);
            }
            else angle = 0;
        }
        var staffRes = StaffDetector.Detect(bin0);
        // orientation fallback: pages scanned sideways
        if (staffRes.Staves.Count == 0 && gray.Width > 200 && gray.Height > 200)
        {
            var rot = gray.Rotate90(1);
            var binRot = Bin(rot, dpi);
            var resRot = StaffDetector.Detect(binRot);
            if (resRot.Staves.Count >= 2)
            {
                result.Notes.Add($"no staves in the original orientation; {resRot.Staves.Count} found after rotating the page by 90 degrees");
                gray = rot; bin0 = binRot; staffRes = resRot;
                result.Rotated90 = true;
            }
            else { Bin(gray, dpi); }
        }
        if (_opt.Deskew && staffRes.Staves.Count > 0)
        {
            double residual = StaffSlopeDegrees(staffRes.Staves);
            if (Math.Abs(residual) >= 0.12 && Math.Abs(residual) <= 5)
            {
                result.Notes.Add($"residual staff slope {residual:+0.00;-0.00} deg after projection deskew {angle:+0.00;-0.00}; rotating again");
                angle += residual;
                gray = gray.Rotate(residual);
                bin0 = Bin(gray, dpi);
                staffRes = StaffDetector.Detect(bin0);
            }
        }
        result.SkewDegrees = angle;
        result.Binarization = binDesc;
        result.Notes.AddRange(staffRes.Log);

        // 2b. re-render at higher resolution when the staff space is too small
        double ssEstimate = staffRes.Staves.Count > 0 ? staffRes.StaffSpace : 0;
        if (staffRes.RunLengthSpace >= 6 && staffRes.RunLengthSpace < _opt.MinStaffSpace && (ssEstimate == 0 || ssEstimate > 2.5 * staffRes.RunLengthSpace))
            ssEstimate = staffRes.RunLengthSpace;   // the run-length statistics disagree with the groups: trust the small value and re-render
        if (rerender != null && ssEstimate > 0 && ssEstimate < _opt.MinStaffSpace && dpi < 900)
        {
            double newDpi = Math.Min(900, Math.Ceiling(dpi * (_opt.MinStaffSpace + 2) / ssEstimate / 50) * 50);
            result.Notes.Add($"staff space {ssEstimate:0.0}px too small at {dpi} dpi; re-rendering at {newDpi} dpi");
            var gray2 = rerender(newDpi);
            if (Math.Abs(angle) >= 0.1) gray2 = gray2.Rotate(angle);
            gray = gray2; dpi = newDpi; result.Dpi = dpi;
            bin0 = Bin(gray, dpi);
            result.Binarization = binDesc;
            staffRes = StaffDetector.Detect(bin0);
            result.Notes.AddRange(staffRes.Log);
        }

        result.Width = gray.Width; result.Height = gray.Height;
        result.StaffSpace = staffRes.StaffSpace;
        result.LineThickness = staffRes.LineThickness;
        result.Staves.AddRange(staffRes.Staves);
        ctx.Gray = gray; ctx.Binary = bin0;

        if (result.Staves.Count == 0)
        {
            result.Notes.Add("no staves found: page does not seem to contain music");
            ctx.NoStaff = bin0;
            result.Elapsed = sw.Elapsed;
            return result;
        }

        // 3. staff removal, barlines, systems
        var noStaff = StaffRemover.Remove(bin0, result.Staves);
        ctx.NoStaff = noStaff;
        var comps = ConnectedComponents.Label(noStaff);
        ctx.Components = comps;
        BarlineDetector.Detect(bin0, noStaff, comps, result.Staves, result.Notes);
        // a guitar chord diagram is a small grid: a "staff" a few spaces wide whose strings read as a run of bar lines
        if (result.Staves.Count > 1)
        {
            double width = result.Staves.Select(st => st.Right - st.Left).OrderBy(w => w).ElementAt(result.Staves.Count / 2);
            int removed = result.Staves.RemoveAll(st => st.Right - st.Left < 25 * st.Space && st.Right - st.Left < width / 3
                                                         && st.Barlines.Count(b => b.Kind != BarlineKind.SystemStart) >= 4);
            if (removed > 0)
            {
                // renumber, also the staves the bar lines span (by their old numbers)
                var newIndex = result.Staves.Select((st, i) => (st.Index, i)).ToDictionary(p => p.Index, p => p.i);
                int Map(int old, bool up) { while (!newIndex.ContainsKey(old) && old >= 0 && old <= newIndex.Keys.Max()) old += up ? 1 : -1; return newIndex.TryGetValue(old, out int v) ? v : 0; }
                foreach (var st in result.Staves)
                    foreach (var b in st.Barlines) { int from = Map(b.StaffFrom, true), to = Map(b.StaffTo, false); b.StaffFrom = from; b.StaffTo = Math.Max(from, to); }
                for (int i = 0; i < result.Staves.Count; i++) result.Staves[i].Index = i;
                result.Notes.Add($"{removed} chord diagram(s) taken for staves dropped");
            }
        }
        result.Systems.AddRange(SystemBuilder.Build(bin0, result.Staves, result.Notes));
        if (_opt.TextRecognizer != null)
        {
            InstrumentNameReader.Read(gray, result, _opt.TextRecognizer);
            foreach (var st in result.Staves.Where(s => s.InstrumentName != null)) result.Notes.Add($"staff {st.Index + 1} instrument name: {st.InstrumentName}");
            BarNumberReader.Read(gray, result, _opt.TextRecognizer);
        }

        // 4. symbols
        var symbolAnalyzer = new SymbolAnalyzer(_glyphs, bin0, noStaff, comps, result, _opt, _classifier, _headVerifier);
        symbolAnalyzer.Run();
        MeasureSplitter.SplitMissedBarlines(result);
        MeasureSplitter.RemoveBarlinesInClefs(result);
        BarNumberReader.FromSymbols(result);
        MultiRests.Detect(result);

        // 5. musical interpretation (clefs, key signatures, pitches)
        MusicInterpreter.Interpret(result, previous);

        result.Elapsed = sw.Elapsed;
        return result;
    }

    /// <summary>Median slope of all staff lines in degrees (positive = lines go down to the right).</summary>
    private static double StaffSlopeDegrees(List<Staff> staves)
    {
        var slopes = new List<double>();
        foreach (var st in staves)
        {
            if (st.Right - st.Left < 10 * st.Space) continue;
            foreach (var line in st.Lines)
            {
                double x0 = st.Left + 0.1 * (st.Right - st.Left), x1 = st.Right - 0.1 * (st.Right - st.Left);
                slopes.Add(Math.Atan2(line.YAt(x1) - line.YAt(x0), x1 - x0) * 180 / Math.PI);
            }
        }
        return slopes.Count == 0 ? 0 : StaffDetector.Median(slopes);
    }

    private static int SauvolaWindow(double dpi) => Math.Max(15, (int)(dpi / 6) | 1);
}
