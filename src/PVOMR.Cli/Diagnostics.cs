using PVOMR.Core.Analysis;
using PVOMR.Core.Model;

static class Diagnostics
{
    /// <summary>Duration check of one page: how many measures add up to the time signature (assembler validation).</summary>
    public static void PrintFit(PageAnalysis p, bool verbose)
    {
        var map = new Dictionary<Measure, PVOMR.Core.Score.ScoreMeasure>();
        try { PVOMR.Core.Score.MusicAssembler.Build(new[] { p }, new PVOMR.Core.Score.MusicAssembler.Options { MeasureMap = map }); }
        catch (Exception ex) { Console.WriteLine($"  fit: assembler failed: {ex.Message}"); return; }
        int total = 0, ok = 0, tooLong = 0, tooShort = 0, dropped = 0, unknown = 0;
        var misfits = new List<string>();
        foreach (var st in p.Staves)
            foreach (var m in st.Measures)
            {
                total++;
                if (!map.TryGetValue(m, out var sm)) { unknown++; continue; }
                var f = sm.Fit;
                if (f.Expected == null) { unknown++; continue; }
                if (f.Fits && f.Dropped == 0) { ok++; continue; }
                if (f.Dropped > 0) dropped++; else if (f.TooLong) tooLong++; else tooShort++;
                string sums = string.Join(",", f.Sums.Select(x => (x.Sum / (double)PVOMR.Core.Score.Score.Divisions).ToString("0.##")));
                misfits.Add($"    {st.Index + 1}.{m.Index + 1} x{(int)m.X0}-{(int)m.X1}: {sums} of {f.Expected.Value / (double)PVOMR.Core.Score.Score.Divisions:0.##} quarters{(f.Dropped > 0 ? $", {f.Dropped} dropped" : "")}");
            }
        int known = total - unknown;
        Console.WriteLine($"  fit: {ok}/{known} measures add up ({(known > 0 ? 100.0 * ok / known : 0):0}%), {tooLong} too long, {tooShort} too short, {dropped} needed dropping{(unknown > 0 ? $", {unknown} without time signature" : "")}");
        if (verbose) foreach (var l in misfits) Console.WriteLine(l);
    }

    public static void DumpStaves(PageAnalysis p, AnalysisContext ctx)
    {
        var bin = ctx.Binary;
        foreach (var st in p.Staves)
        {
            Console.WriteLine($"-- staff {st.Index + 1}: left {st.Left:0} right {st.Right:0} space {st.Space:0.0} thick {st.LineThickness:0.0}; line points: {string.Join(",", st.Lines.Select(l => l.Points.Count))}");
            Console.WriteLine("   barlines: " + string.Join(" ", st.Barlines.Select(b => $"{b.Kind}@{b.X0}-{b.X1}{(b.Spanning ? "s" : "")}")));
            foreach (double f in new[] { 0.05, 0.5, 0.95 })
            {
                int x = (int)(st.Left + f * (st.Right - st.Left));
                var model = st.Lines.Select(l => l.YAt(x)).Select(y => y.ToString("0.0")).ToList();
                int y0 = (int)(st.TopY(x) - 2 * st.Space), y1 = (int)(st.BottomY(x) + 2 * st.Space);
                var runs = new List<string>();
                for (int y = y0; y <= y1; y++)
                {
                    if (!bin[x, y]) continue;
                    int len = bin.VerticalRunAt(x, y, out int top);
                    runs.Add($"{top}+{len}");
                    y = top + len;
                }
                Console.WriteLine($"   x={x}: model y = {string.Join(" ", model)}; ink runs = {string.Join(" ", runs)}");
            }
        }
    }
}
