using PVOMR.Core.Imaging;
using PVOMR.Core.Model;

namespace PVOMR.Core.Analysis;

public static class SystemBuilder
{
    public static List<StaffSystem> Build(BinaryImage bin, List<Staff> staves, List<string> log)
    {
        int n = staves.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int a) { while (parent[a] != a) a = parent[a] = parent[parent[a]]; return a; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); }

        // 1. spanning barlines
        foreach (var s in staves)
            foreach (var b in s.Barlines)
                for (int k = b.StaffFrom; k < b.StaffTo; k++) Union(k, k + 1);

        // 2. connectors left of the staves: brackets, braces and system barlines are vertical ink runs spanning several staves
        double ssMed = StaffDetector.Median(staves.Select(st => st.Space));
        int xMin = (int)Math.Max(0, staves.Min(st => st.Left) - 7 * ssMed);
        int xMax = (int)Math.Min(bin.Width - 1, staves.Max(st => st.Left) + 1.0 * ssMed);
        int bridge = (int)Math.Max(3, 1.2 * ssMed);
        var tops = staves.Select(st => st.TopY(st.Left)).ToList();
        var bots = staves.Select(st => st.BottomY(st.Left)).ToList();
        int connectors = 0;
        var connectorSets = new HashSet<string>();
        for (int x = xMin; x <= xMax; x++)
        {
            // skip columns that are mostly ink over the whole page (scan borders, binding shadows)
            int colInk = 0;
            for (int yy = 0; yy < bin.Height; yy += 4) if (bin[x, yy]) colInk++;
            if (colInk > 0.5 * (bin.Height / 4)) continue;
            int y = 0;
            while (y < bin.Height)
            {
                if (!bin[x, y]) { y++; continue; }
                int top = y, bottom = y, ink = 1, gap = 0;
                y++;
                while (y < bin.Height)
                {
                    if (bin[x, y]) { bottom = y; ink++; gap = 0; }
                    else if (++gap > bridge) break;
                    y++;
                }
                int len = bottom - top + 1;
                if (len < 5 * ssMed || ink < 0.8 * len || len > 0.9 * bin.Height) continue;
                var spanned = new List<int>();
                for (int i = 0; i < n; i++)
                {
                    var st = staves[i];
                    double dx = st.Left - x;
                    if (dx < -0.8 * st.Space || dx > 7 * st.Space) continue;
                    if (tops[i] >= top - 1.5 * st.Space && bots[i] <= bottom + 1.5 * st.Space) spanned.Add(i);
                }
                if (spanned.Count < 2) continue;
                double outerTop = spanned.Min(i => tops[i]), outerBot = spanned.Max(i => bots[i]);
                if (outerTop - top > 4 * ssMed || bottom - outerBot > 4 * ssMed) continue; // page-long rules, margins
                for (int k = 1; k < spanned.Count; k++) Union(spanned[0], spanned[k]);
                connectors++;
                string key = string.Join(",", spanned.Select(i => i + 1));
                if (!connectorSets.Contains(key)) { connectorSets.Add(key); log.Add($"connector at x={x} y={top}-{bottom} spans staves {key}"); }
            }
        }
        if (connectors > 0) log.Add($"system connectors: {connectors} column runs");

        var systems = new List<StaffSystem>();
        var byRoot = new Dictionary<int, StaffSystem>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (!byRoot.TryGetValue(r, out var sys)) { sys = new StaffSystem(); byRoot[r] = sys; systems.Add(sys); }
            sys.Staves.Add(staves[i]);
        }
        systems.Sort((p, q) => p.Staves[0].CenterY.CompareTo(q.Staves[0].CenterY));
        for (int i = 0; i < systems.Count; i++)
        {
            var sys = systems[i];
            sys.Index = i;
            foreach (var st in sys.Staves) st.SystemIndex = i;
            double left = sys.Staves.Min(s => s.Left), right = sys.Staves.Max(s => s.Right);
            double top = sys.Staves.Min(s => s.TopY(s.Left)), bot = sys.Staves.Max(s => s.BottomY(s.Left));
            sys.Bounds = new Rect((int)left, (int)top, (int)right, (int)bot);
            BuildMeasures(sys);
            DetectBraces(bin, sys, log);
        }
        return systems;
    }

    /// <summary>
    /// Marks adjacent staves joined by a curly brace at the left edge. Per row the ink run nearest to the staves (left
    /// of the system line) is traced over the stroke's whole height, which may overshoot the staves a little: a brace
    /// has its tips well to the right of its middle (the cusp points left), a bracket or system line is straight, and
    /// a stroke that runs on past the pair belongs to a bracket over more staves.
    /// </summary>
    private static void DetectBraces(BinaryImage bin, StaffSystem sys, List<string> log)
    {
        bool dump = Environment.GetEnvironmentVariable("PVOMR_BRACEDUMP") == "1";
        for (int i = 0; i + 1 < sys.Staves.Count; i++)
        {
            Staff a = sys.Staves[i], b = sys.Staves[i + 1];
            double ss = (a.Space + b.Space) / 2;
            double left = Math.Min(a.Left, b.Left);
            int xFrom = (int)Math.Round(left - 0.35 * ss), xTo = (int)Math.Max(0, Math.Round(left - 4.0 * ss));
            int top = (int)Math.Round(a.TopY(left)), bottom = (int)Math.Round(b.BottomY(left));
            int y0 = (int)Math.Max(0, top - 2.5 * ss), y1 = (int)Math.Min(bin.Height - 1, bottom + 2.5 * ss);
            if (bottom - top < 6 * ss || xFrom <= xTo) continue;
            var lx = new int?[y1 - y0 + 1];
            var rx = new int?[y1 - y0 + 1];
            for (int y = y0; y <= y1; y++)
            {
                int x = xFrom;
                while (x >= xTo && !bin[x, y]) x--;
                if (x < xTo) continue;
                rx[y - y0] = x;
                while (x - 1 >= xTo && bin[x - 1, y]) x--;
                lx[y - y0] = x;
            }
            // the stroke: the run of inked rows (gaps up to a third of a space bridged) through the middle of the pair
            int midRow = (top + bottom) / 2 - y0, gapMax = (int)Math.Max(2, 0.8 * ss);
            if (lx[midRow] == null) { if (dump) log.Add($"brace? staves {a.Index + 1}/{b.Index + 1}: no ink beside the middle"); continue; }
            int e0 = midRow, e1 = midRow;
            for (int r = midRow, gap = 0; r >= 0; r--) { if (lx[r] != null) { e0 = r; gap = 0; } else if (++gap > gapMax) break; }
            for (int r = midRow, gap = 0; r < lx.Length; r++) { if (lx[r] != null) { e1 = r; gap = 0; } else if (++gap > gapMax) break; }
            int len = e1 - e0 + 1;
            bool runsOn = e0 == 0 || e1 == lx.Length - 1;
            bool covers = y0 + e0 <= top + 1.5 * ss && y0 + e1 >= bottom - 1.5 * ss;
            double Median(int r0, int r1, int?[]? arr = null)
            {
                arr ??= lx;
                var v = new List<int>();
                for (int r = Math.Max(e0, r0); r <= Math.Min(e1, r1); r++) if (arr[r] is int x) v.Add(x);
                if (v.Count == 0) return double.NaN;
                v.Sort(); return v[v.Count / 2];
            }
            int tip = (int)Math.Max(2, 0.04 * len);
            double tipTop = Median(e0, e0 + tip), tipBot = Median(e1 - tip, e1);
            double cusp = Enumerable.Range(e0 + (int)(0.35 * len), (int)(0.3 * len)).Where(r => lx[r] != null).Select(r => (double)lx[r]!.Value).DefaultIfEmpty(double.NaN).Min();
            // the right edge: the swellings of the two arms stand out to the right of the thin middle
            double swellTop = Median(e0 + (int)(0.2 * len), e0 + (int)(0.35 * len), rx), swellBot = Median(e0 + (int)(0.65 * len), e0 + (int)(0.8 * len), rx);
            double midRight = Median(e0 + (int)(0.47 * len), e0 + (int)(0.53 * len), rx);
            double leftCurve = Math.Min(tipTop, tipBot) - cusp, rightDip = Math.Min(swellTop, swellBot) - midRight;
            if (dump) log.Add($"brace? staves {a.Index + 1}/{b.Index + 1}: stroke y {y0 + e0}-{y0 + e1} (staves {top}-{bottom}), tips x {tipTop}/{tipBot}, cusp x {cusp}, right edge {swellTop}/{midRight}/{swellBot} (ss {ss:0.0}){(runsOn ? ", runs on" : "")}");
            if (runsOn || !covers || double.IsNaN(cusp) || double.IsNaN(tipTop) || double.IsNaN(tipBot)) continue;
            if (!(leftCurve >= 0.4 * ss || leftCurve >= 0.2 * ss && rightDip >= 0.3 * ss)) continue;
            a.BraceToNext = true;
            log.Add($"brace joins staves {a.Index + 1} and {b.Index + 1}");
        }
    }

    private static void BuildMeasures(StaffSystem sys)
    {
        double ss = sys.Staves.Average(s => s.Space);
        // cluster barline positions across staves
        var xs = new List<(double x, int staff, bool spanning, Barline b)>();
        // the lines at the very start of the staves (system line, bracket edge, opening repeat) are one boundary at the
        // system's left edge however wide the bracket makes them; they never open a measure of their own
        double left = sys.Staves.Min(st => st.Left);
        var starts = new List<(double x, int staff, bool spanning, Barline b)>();
        foreach (var st in sys.Staves)
            foreach (var b in st.Barlines)
            {
                if (b.X0 <= st.Left + 1.5 * ss) starts.Add((left, st.Index, b.Spanning, b));
                else xs.Add((b.X, st.Index, b.Spanning, b));
            }
        xs.Sort((p, q) => p.x.CompareTo(q.x));
        var clusters = new List<List<(double x, int staff, bool spanning, Barline b)>>();
        if (starts.Count > 0) clusters.Add(starts);
        foreach (var e in xs)
        {
            if (clusters.Count > 0 && e.x - clusters[^1][^1].x <= 0.8 * ss) clusters[^1].Add(e);
            else clusters.Add(new() { e });
        }
        int need = sys.Staves.Count >= 2 ? Math.Max(2, (sys.Staves.Count + 1) / 2) : 1;
        var boundaries = new List<double>();
        foreach (var c in clusters)
        {
            int distinct = c.Select(e => e.staff).Distinct().Count();
            if (distinct >= need || c.Any(e => e.spanning || e.b.ConnectedUp)) boundaries.Add(c.Average(e => e.x));
        }
        sys.MeasureBoundaries.Clear();
        sys.MeasureBoundaries.AddRange(boundaries);

        foreach (var st in sys.Staves)
        {
            st.Measures.Clear();
            var bs = boundaries.Where(x => x >= st.Left - 1.5 * ss && x <= st.Right + 1.5 * ss).ToList();
            var edges = new List<double>();
            if (bs.Count == 0 || bs[0] > st.Left + 2 * ss) edges.Add(st.Left);
            edges.AddRange(bs);
            if (bs.Count == 0 || bs[^1] < st.Right - 2 * ss) edges.Add(st.Right);
            for (int i = 0; i + 1 < edges.Count; i++)
            {
                if (edges[i + 1] - edges[i] < 0.5 * ss) continue;
                var m = new Measure { Index = st.Measures.Count, X0 = edges[i], X1 = edges[i + 1], Staff = st };
                m.LeftBarline = st.Barlines.FirstOrDefault(b => Math.Abs(b.X - edges[i]) <= 0.8 * ss);
                m.RightBarline = st.Barlines.FirstOrDefault(b => Math.Abs(b.X - edges[i + 1]) <= 0.8 * ss);
                st.Measures.Add(m);
            }
        }
    }
}
