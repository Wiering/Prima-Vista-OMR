using PVOMR.Core.Analysis;
using PVOMR.Core.Model;

static class Diagnostics
{
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
