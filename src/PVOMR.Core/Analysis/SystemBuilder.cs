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
        }
        return systems;
    }

    private static void BuildMeasures(StaffSystem sys)
    {
        double ss = sys.Staves.Average(s => s.Space);
        // cluster barline positions across staves
        var xs = new List<(double x, int staff, bool spanning, Barline b)>();
        foreach (var st in sys.Staves)
            foreach (var b in st.Barlines) xs.Add((b.X, st.Index, b.Spanning, b));
        xs.Sort((p, q) => p.x.CompareTo(q.x));
        var clusters = new List<List<(double x, int staff, bool spanning, Barline b)>>();
        foreach (var e in xs)
        {
            if (clusters.Count > 0 && e.x - clusters[^1][^1].x <= 0.8 * ss) clusters[^1].Add(e);
            else clusters.Add(new() { e });
        }
        int need = Math.Max(1, (sys.Staves.Count + 1) / 2);
        var boundaries = new List<double>();
        foreach (var c in clusters)
        {
            int distinct = c.Select(e => e.staff).Distinct().Count();
            if (distinct >= need || c.Any(e => e.spanning)) boundaries.Add(c.Average(e => e.x));
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
