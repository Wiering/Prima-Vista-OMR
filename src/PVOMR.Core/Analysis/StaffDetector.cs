using PVOMR.Core.Imaging;
using PVOMR.Core.Model;

namespace PVOMR.Core.Analysis;

public sealed class StaffDetectionResult
{
    public List<Staff> Staves { get; } = new();
    public double StaffSpace { get; set; }
    public double LineThickness { get; set; }
    public int RunLengthThickness { get; set; }
    public int RunLengthSpace { get; set; }
    public int Strips { get; set; }
    public List<string> Log { get; } = new();
}

/// <summary>
/// Staff line detection. The page is cut into vertical strips; in every strip the horizontal projection of *thin*
/// ink (short vertical runs, i.e. not beams/stems/note heads) yields candidate line rows, which are grouped into
/// 5-line candidates with uniform spacing. Candidates are linked across strips into staves, and finally every line
/// is refined at full resolution and stored as a polyline so curved (book) scans are handled.
/// </summary>
public static class StaffDetector
{
    private sealed class Segment { public double Y; public int Thickness; public int Weight; }
    private sealed class Group { public double[] Ys = new double[5]; public double Spacing; public double Thickness; public int Strip; public bool Partial; }
    private sealed class Chain
    {
        public List<Group> Groups = new();
        public Group Last => Groups[^1];
        public bool Closed;
    }

    public static (int thickness, int space) EstimateRunLengths(BinaryImage bin)
    {
        var black = new long[256];
        var white = new long[256];
        int w = bin.Width, h = bin.Height;
        for (int x = 0; x < w; x += 2)
        {
            int run = 0; bool cur = false;
            for (int y = 0; y < h; y++)
            {
                bool v = bin.Data[y * w + x] != 0;
                if (v == cur) { run++; continue; }
                if (run > 0 && run < 256) { if (cur) black[run]++; else white[run]++; }
                cur = v; run = 1;
            }
        }
        int th = 1; long best = -1;
        for (int i = 1; i < 256; i++) if (black[i] > best) { best = black[i]; th = i; }
        int sp = 0; best = -1;
        for (int i = Math.Max(2, th); i < 256; i++) if (white[i] > best) { best = white[i]; sp = i; }
        return (th, sp + th);
    }

    public static StaffDetectionResult Detect(BinaryImage bin, int minStripsPerStaff = 2)
    {
        var result = new StaffDetectionResult();
        var (rlThick, rlSpace) = EstimateRunLengths(bin);
        result.RunLengthThickness = rlThick;
        result.RunLengthSpace = rlSpace;
        double ssGuess = Math.Clamp(rlSpace, 6, 120);
        int thickGuess = Math.Clamp(rlThick, 1, 8);
        int maxRun = Math.Max(3, (int)Math.Round(2.5 * thickGuess));
        int stripW = (int)Math.Clamp(5 * ssGuess, 48, 260);
        int strips = Math.Max(1, (bin.Width + stripW - 1) / stripW);
        result.Strips = strips;
        result.Log.Add($"run-length estimate: line thickness {rlThick}px, staff space {rlSpace}px; strips={strips} (w={stripW})");

        // 1. candidate groups per strip
        var groupsPerStrip = new List<Group>[strips];
        Parallel.For(0, strips, s =>
        {
            int x0 = s * stripW, x1 = Math.Min(bin.Width, x0 + stripW);
            var segs = FindSegments(bin, x0, x1, maxRun, ssGuess);
            groupsPerStrip[s] = FindGroups(segs, s, ssGuess, x1 - x0);
        });
        var allGroups = groupsPerStrip.SelectMany(g => g).ToList();
        if (Environment.GetEnvironmentVariable("PVOMR_STRIPDUMP") is string sd && int.TryParse(sd, out int dumpStrip) && dumpStrip < strips)
        {
            var segs = FindSegments(bin, dumpStrip * stripW, Math.Min(bin.Width, (dumpStrip + 1) * stripW), maxRun, ssGuess);
            result.Log.Add($"strip {dumpStrip} segments: " + string.Join(" ", segs.Select(g => $"{g.Y:0}/{g.Thickness}/{g.Weight}")));
            result.Log.Add($"strip {dumpStrip} groups: " + string.Join(" ", groupsPerStrip[dumpStrip].Select(g => $"[{string.Join(",", g.Ys.Select(y => y.ToString("0")))}]")));
            if (Environment.GetEnvironmentVariable("PVOMR_ROWS") is string rows && rows.Split('-') is { Length: 2 } rr && int.TryParse(rr[0], out int r0) && int.TryParse(rr[1], out int r1))
            {
                int x0 = dumpStrip * stripW, x1 = Math.Min(bin.Width, x0 + stripW);
                var thin = new List<string>(); var all = new List<string>();
                for (int y = Math.Max(0, r0); y <= Math.Min(bin.Height - 1, r1); y++)
                {
                    int t = 0, a = 0;
                    for (int x = x0; x < x1; x++)
                    {
                        if (!bin[x, y]) continue;
                        a++;
                        int len = bin.VerticalRunAt(x, y, out _);
                        if (len <= maxRun) t++;
                    }
                    thin.Add($"{y}:{t}/{a}");
                }
                result.Log.Add($"strip {dumpStrip} rows (thin/all ink, maxRun={maxRun}): " + string.Join(" ", thin));
            }
        }
        result.Log.Add("groups per strip: " + string.Join("", groupsPerStrip.Select(g => g.Count > 9 ? "+" : g.Count.ToString())));
        if (allGroups.Count == 0)
        {
            result.Log.Add("no 5-line groups found");
            return result;
        }

        // 2. global staff space = mode of group spacings
        double ss = Mode(allGroups.Where(g => !g.Partial).Select(g => g.Spacing).DefaultIfEmpty(allGroups[0].Spacing), 0.5);
        result.StaffSpace = ss;
        result.LineThickness = Math.Max(1, Median(allGroups.Select(g => g.Thickness)));
        result.Log.Add($"group-based estimate: staff space {ss:0.0}px, line thickness {result.LineThickness:0.0}px ({allGroups.Count} strip groups)");

        // 3. link groups across strips into chains (all five lines must agree with the extrapolated chain)
        var chains = new List<Chain>();
        for (int s = 0; s < strips; s++)
        {
            var open = chains.Where(c => !c.Closed).ToList();
            var assigned = new HashSet<Chain>();
            foreach (var g in groupsPerStrip[s].OrderBy(g => g.Ys[0]))
            {
                if (g.Spacing < 0.4 * ss || g.Spacing > 2.3 * ss) continue;
                Chain? bestChain = null; double bestD = double.MaxValue;
                foreach (var c in open)
                {
                    if (assigned.Contains(c)) continue;
                    var last = c.Last;
                    int gap = s - last.Strip;
                    if (gap <= 0 || gap > 3) continue;
                    double slope = 0;
                    if (c.Groups.Count >= 2)
                    {
                        var prev = c.Groups[^2];
                        slope = (last.Ys[0] - prev.Ys[0] + last.Ys[4] - prev.Ys[4]) / 2.0 / Math.Max(1, last.Strip - prev.Strip);
                        slope = Math.Clamp(slope, -0.06 * stripW, 0.06 * stripW);
                    }
                    double d = 0;
                    for (int k = 0; k < 5; k++) d += Math.Abs(g.Ys[k] - (last.Ys[k] + slope * gap));
                    d /= 5;
                    double spRatio = g.Spacing / last.Spacing;
                    double tol = (0.3 * last.Spacing + 1.5) * (gap == 1 ? 1 : 1.6);
                    if (d > tol || spRatio < 0.9 || spRatio > 1.11) continue;
                    if (d < bestD) { bestD = d; bestChain = c; }
                }
                if (bestChain != null)
                {
                    bestChain.Groups.Add(g);
                    assigned.Add(bestChain);
                }
                else if (!g.Partial)
                {
                    var c = new Chain(); c.Groups.Add(g); chains.Add(c);
                }
            }
            foreach (var c in chains) if (!c.Closed && s - c.Last.Strip > 3) c.Closed = true;
        }

        // 4. chains -> staves
        var staves = new List<Staff>();
        foreach (var c in chains)
        {
            if (c.Groups.Count < minStripsPerStaff || c.Groups.Count(g => !g.Partial) < 1) continue;
            var staff = BuildStaff(bin, c, stripW, maxRun);
            if (staff == null) continue;
            if (staff.Right - staff.Left < 6 * staff.Space) continue;
            staves.Add(staff);
        }
        // Remove staves that overlap with a longer staff (duplicates from fragmented chains).
        var keep = new List<Staff>();
        foreach (var st in staves.OrderByDescending(s => s.Right - s.Left))
            if (!keep.Any(k => Overlaps(k, st) || SharesLine(k, st))) keep.Add(st);
        // Merge collinear fragments of the same staff (chains broken by clutter).
        keep = MergeFragments(bin, keep, maxRun, result.Log);
        // Validate: staff lines must be continuous thin ink (rejects equally spaced text rows).
        var valid = new List<Staff>();
        foreach (var st in keep)
        {
            double thin = LineCoverage(bin, st, maxRun), longRuns = LongRunCoverage(bin, st);
            double widthSS = (st.Right - st.Left) / st.Space;
            double needLong = widthSS < 25 ? 0.45 : 0.3;
            if (thin >= 0.3 && longRuns >= needLong && widthSS >= 10) { valid.Add(st); result.Log.Add($"staff at y={st.CenterY:0} x={st.Left:0}-{st.Right:0}: thin {thin:0.00} long {longRuns:0.00}"); }
            else result.Log.Add($"rejected staff candidate at y={st.CenterY:0} x={st.Left:0}-{st.Right:0}: thin-ink coverage {thin:0.00}, long-run coverage {longRuns:0.00}");
        }
        keep = valid;
        foreach (var st in keep) ShiftIfOutsideLineIsBetter(bin, st, maxRun, result.Log);
        // Short candidates right next to a much longer staff with the same spacing are ledger-line structures.
        double maxWidth = keep.Count == 0 ? 0 : keep.Max(st => st.Right - st.Left);
        keep = keep.Where(st =>
        {
            double w = st.Right - st.Left;
            double xm = (st.Left + st.Right) / 2;
            foreach (var o in keep)
            {
                // well under half the width: anywhere within seven spaces of the longer staff; a wider candidate only
                // when it hangs about one space off the longer staff (a stack of ledger lines under low notes; real
                // staves are never that close)
                double ow = o.Right - o.Left;
                bool shortOne = w < 0.5 * maxWidth && ow >= 2 * w, closeOne = w < 0.85 * ow;
                if (o == st || !(shortOne || closeOne)) continue;
                if (xm < o.Left || xm > o.Right) continue;
                double r = st.Space / o.Space;
                if (r < 0.9 || r > 1.1) continue;
                double dTop = o.TopY(xm) - st.BottomY(xm), dBot = st.TopY(xm) - o.BottomY(xm);
                double reach = shortOne ? 7 : 2;
                if ((dTop > -0.5 * st.Space && dTop < reach * st.Space) || (dBot > -0.5 * st.Space && dBot < reach * st.Space))
                {
                    result.Log.Add($"rejected short staff candidate at y={st.CenterY:0} x={st.Left:0}-{st.Right:0}: ledger-line zone of a longer staff");
                    return false;
                }
            }
            return true;
        }).ToList();
        int idx = 0;
        foreach (var st in keep.OrderBy(s => s.CenterY).ThenBy(s => s.Left)) { st.Index = idx++; result.Staves.Add(st); }
        result.Log.Add($"{result.Staves.Count} staves");
        return result;
    }

    private static List<Staff> MergeFragments(BinaryImage bin, List<Staff> staves, int maxRun, List<string> log)
    {
        var list = staves.OrderBy(s => s.Left).ToList();
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < list.Count && !merged; i++)
                for (int j = 0; j < list.Count; j++)
                {
                    if (i == j) continue;
                    var a = list[i]; var b = list[j];
                    if (a.Left >= b.Left) continue;
                    double gap = b.Left - a.Right;
                    if (gap > 14 * a.Space || gap < -4 * a.Space) continue;
                    double spRatio = a.Space / b.Space;
                    if (spRatio < 0.9 || spRatio > 1.11) continue;
                    double xm = (a.Right + b.Left) / 2;
                    if (b.Right <= a.Right) continue;
                    bool agree = true; double cover = 0;
                    int thinRun = Math.Max(maxRun, (int)Math.Round(2.5 * a.LineThickness));
                    for (int k = 0; k < 5 && agree; k++)
                    {
                        double ya = a.Lines[k].YAt(xm), yb = b.Lines[k].YAt(xm);
                        if (Math.Abs(ya - yb) > 0.45 * a.Space) agree = false;
                        cover += gap <= 0 ? 1 : GapCoverage(bin, a.Lines[k], b.Lines[k], (int)a.Right, (int)b.Left, thinRun, a.Space);
                    }
                    if (!agree || cover / 5 < 0.35) continue;
                    // merge b into a
                    for (int k = 0; k < 5; k++)
                    {
                        var pts = a.Lines[k].Points;
                        pts.AddRange(b.Lines[k].Points.Where(p => p.X > pts[^1].X));
                    }
                    a.Right = b.Right;
                    a.Space = (a.Space + b.Space) / 2;
                    foreach (var line in a.Lines) Refine(bin, line, a, thinRun);
                    list.RemoveAt(j);
                    merged = true;
                    break;
                }
        }
        return list;
    }

    /// <summary>Fraction of columns in the gap between two fragments where ink is found near the interpolated line.</summary>
    private static double GapCoverage(BinaryImage bin, StaffLine la, StaffLine lb, int xa, int xb, int thinRun, double space)
    {
        if (xb - xa < 2) return 1;
        double ya = la.YAt(xa), yb = lb.YAt(xb);
        int tol = (int)Math.Max(2, 0.3 * space);
        int hit = 0, n = 0;
        for (int x = xa; x <= xb; x += 2)
        {
            double y = ya + (yb - ya) * (x - xa) / (double)(xb - xa);
            n++;
            int yc = (int)Math.Round(y);
            for (int dy = -tol; dy <= tol; dy++) if (bin[x, yc + dy]) { hit++; break; }
        }
        return n == 0 ? 0 : hit / (double)n;
    }

    /// <summary>Mean fraction of columns where thin ink lies on the modelled line (continuity of the five lines).</summary>
    public static double LineCoverage(BinaryImage bin, Staff st, int maxRun)
    {
        int thinRun = Math.Max(maxRun, (int)Math.Round(2.5 * st.LineThickness));
        int tol = (int)Math.Max(2, 0.3 * st.Space);
        double total = 0;
        foreach (var line in st.Lines)
        {
            int hit = 0, n = 0;
            for (int x = (int)st.Left; x <= (int)st.Right; x += 3)
            {
                n++;
                int yc = (int)Math.Round(line.YAt(x));
                for (int dy = -tol; dy <= tol; dy++)
                {
                    int yy = yc + dy;
                    if (!bin[x, yy]) continue;
                    int len = bin.VerticalRunAt(x, yy, out int top);
                    if (len <= thinRun) { hit++; break; }
                    dy = top + len - yc;
                }
            }
            total += n == 0 ? 0 : hit / (double)n;
        }
        return total / st.Lines.Count;
    }

    /// <summary>
    /// A staff whose weakest outer line is covered much worse than the line one spacing outside the staff was built
    /// from a row of ledger lines or a beam: shift it by one line and re-refine.
    /// </summary>
    private static void ShiftIfOutsideLineIsBetter(BinaryImage bin, Staff st, int maxRun, List<string> log)
    {
        int thinRun = Math.Max(maxRun, (int)Math.Round(2.5 * st.LineThickness));
        double[] cover = st.Lines.Select(l => LongRunCoverageOfLine(bin, st, l, 0)).ToArray();
        double above = LongRunCoverageOfLine(bin, st, st.Lines[0], -st.Space);
        double below = LongRunCoverageOfLine(bin, st, st.Lines[4], +st.Space);
        bool shiftUp = above > cover[4] + 0.3 && above > 0.6 && cover[4] < Math.Min(cover[1], cover[2]) - 0.2;
        bool shiftDown = below > cover[0] + 0.3 && below > 0.6 && cover[0] < Math.Min(cover[2], cover[3]) - 0.2;
        if (!shiftUp && !shiftDown) return;
        var lines = new List<StaffLine>();
        if (shiftUp)
        {
            var top = new StaffLine { Thickness = st.LineThickness };
            foreach (var pt in st.Lines[0].Points) top.Points.Add((pt.X, pt.Y - st.Space));
            lines.Add(top); lines.AddRange(st.Lines.Take(4));
        }
        else
        {
            var bottom = new StaffLine { Thickness = st.LineThickness };
            foreach (var pt in st.Lines[4].Points) bottom.Points.Add((pt.X, pt.Y + st.Space));
            lines.AddRange(st.Lines.Skip(1)); lines.Add(bottom);
        }
        st.Lines.Clear(); st.Lines.AddRange(lines);
        foreach (var line in st.Lines) Refine(bin, line, st, thinRun);
        log.Add($"staff at y={st.CenterY:0} shifted one line {(shiftUp ? "up" : "down")}: outside line coverage {(shiftUp ? above : below):0.00} vs weakest outer line {(shiftUp ? cover[4] : cover[0]):0.00}");
    }

    private static double LongRunCoverageOfLine(BinaryImage bin, Staff st, StaffLine line, double dy)
    {
        int tol = (int)Math.Max(1, Math.Round(st.LineThickness * 0.6 + 0.5));
        int minRun = (int)Math.Max(4, 0.8 * st.Space);
        int x0 = (int)st.Left, x1 = (int)st.Right;
        if (x1 - x0 < 4) return 0;
        int covered = 0, run = 0;
        for (int x = x0; x <= x1 + 1; x++)
        {
            bool on = false;
            if (x <= x1)
            {
                int yc = (int)Math.Round(line.YAt(x) + dy);
                for (int d = -tol; d <= tol && !on; d++) on = bin[x, yc + d];
            }
            if (on) run++;
            else { if (run >= minRun) covered += run; run = 0; }
        }
        return covered / (double)(x1 - x0 + 1);
    }

    /// <summary>Fraction of the staff width covered by horizontal ink runs of at least 0.8 staff spaces lying on the modelled lines.</summary>
    public static double LongRunCoverage(BinaryImage bin, Staff st)
    {
        int tol = (int)Math.Max(1, Math.Round(st.LineThickness * 0.6 + 0.5));
        int minRun = (int)Math.Max(4, 0.8 * st.Space);
        double total = 0;
        int x0 = (int)st.Left, x1 = (int)st.Right;
        if (x1 - x0 < 4) return 0;
        foreach (var line in st.Lines)
        {
            int covered = 0, run = 0;
            for (int x = x0; x <= x1 + 1; x++)
            {
                bool on = false;
                if (x <= x1)
                {
                    int yc = (int)Math.Round(line.YAt(x));
                    for (int dy = -tol; dy <= tol && !on; dy++) on = bin[x, yc + dy];
                }
                if (on) run++;
                else { if (run >= minRun) covered += run; run = 0; }
            }
            total += covered / (double)(x1 - x0 + 1);
        }
        return total / st.Lines.Count;
    }

    /// <summary>True when the shorter staff b has a line lying on one of a's lines over their common x range (false staff built from ledger lines or clutter).</summary>
    private static bool SharesLine(Staff a, Staff b)
    {
        double xo = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        if (xo < 2 * a.Space) return false;
        if (b.Right - b.Left > 0.8 * (a.Right - a.Left)) return false;
        double xm = Math.Max(a.Left, b.Left) + xo / 2;
        foreach (var la in a.Lines)
            foreach (var lb in b.Lines)
                if (Math.Abs(la.YAt(xm) - lb.YAt(xm)) < 0.3 * a.Space) return true;
        return false;
    }

    private static bool Overlaps(Staff a, Staff b)
    {
        double xo = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        if (xo < 2 * a.Space) return false;
        double xm = Math.Max(a.Left, b.Left) + xo / 2;
        double aTop = a.TopY(xm), aBot = a.BottomY(xm), bTop = b.TopY(xm), bBot = b.BottomY(xm);
        double yo = Math.Min(aBot, bBot) - Math.Max(aTop, bTop);
        return yo > 0.5 * Math.Min(a.Height, b.Height);
    }

    /// <summary>Rows of a strip that are mostly covered by thin ink (vertical runs no longer than maxRun).</summary>
    private static List<Segment> FindSegments(BinaryImage bin, int x0, int x1, int maxRun, double ssGuess)
    {
        int h = bin.Height, w = bin.Width;
        var proj = new int[h];
        var d = bin.Data;
        for (int x = x0; x < x1; x++)
        {
            int y = 0;
            while (y < h)
            {
                if (d[y * w + x] == 0) { y++; continue; }
                int start = y;
                while (y < h && d[y * w + x] != 0) y++;
                int len = y - start;
                if (len <= maxRun) for (int yy = start; yy < y; yy++) proj[yy]++;
            }
        }
        int width = x1 - x0;
        double thr = 0.20 * width;
        var segs = new List<Segment>();
        int r = 0;
        while (r < h)
        {
            if (proj[r] < thr) { r++; continue; }
            int start = r; long wsum = 0, wy = 0;
            while (r < h && proj[r] >= thr) { wsum += proj[r]; wy += (long)proj[r] * r; r++; }
            int thick = r - start;
            if (thick <= Math.Max(3, 0.7 * ssGuess))
                segs.Add(new Segment { Y = (double)wy / wsum, Thickness = thick, Weight = (int)(wsum / Math.Max(1, thick)) });
        }
        // Bands about twice the typical thickness are two touching lines (upscaled scans, heavy ink): split them.
        if (segs.Count >= 5)
        {
            double med = Median(segs.Select(g => (double)g.Thickness));
            var split = new List<Segment>();
            foreach (var g in segs)
            {
                if (med >= 2 && g.Thickness >= 1.7 * med && g.Thickness <= 2.6 * med)
                {
                    double q = g.Thickness / 4.0;
                    split.Add(new Segment { Y = g.Y - q, Thickness = g.Thickness / 2, Weight = g.Weight });
                    split.Add(new Segment { Y = g.Y + q, Thickness = g.Thickness / 2, Weight = g.Weight });
                }
                else split.Add(g);
            }
            segs = split.OrderBy(g => g.Y).ToList();
        }
        return segs;
    }

    private static List<Group> FindGroups(List<Segment> segs, int strip, double ssGuess, int stripWidth)
    {
        var groups = new List<Group>();
        int n = segs.Count;
        var used = new bool[n];
        var candidates = new List<(int[] idx, double[] ys, int missing, double score, double spacing, double thick)>();
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < Math.Min(n, i + 7); j++)
            {
                double d = segs[j].Y - segs[i].Y;
                if (d < 3) continue;
                if (d > 150) break;
                double tol = Math.Max(2.0, 0.2 * d);
                double maxThick = Math.Max(3.0, 0.5 * d);
                if (segs[i].Thickness > maxThick || segs[j].Thickness > maxThick) continue;
                var idx = new int[5]; idx[0] = i; idx[1] = j;
                var ys = new double[5]; ys[0] = segs[i].Y; ys[1] = segs[j].Y;
                double dev = 0; int missing = 0; int missingAt = -1;
                int from = j + 1;
                double lastY = segs[j].Y;
                int found = 2;
                for (int k = 2; k <= 4; k++)
                {
                    double dEst = (lastY - segs[i].Y) / (k - 1 - (missingAt >= 0 && missingAt < k ? 0 : 0));
                    dEst = (lastY - ys[0]) / (k - 1);
                    double target = lastY + dEst;
                    int best = -1; double bestDev = double.MaxValue;
                    for (int m = from; m < n; m++)
                    {
                        double e = segs[m].Y - target;
                        if (e > tol) break;
                        if (Math.Abs(e) <= tol && segs[m].Thickness <= maxThick && Math.Abs(e) < bestDev) { bestDev = Math.Abs(e); best = m; }
                    }
                    if (best < 0)
                    {
                        if (missing > 0) { found = -1; break; }
                        missing++; missingAt = k; idx[k] = -1; ys[k] = target; lastY = target; dev += tol;
                        continue;
                    }
                    idx[k] = best; ys[k] = segs[best].Y; dev += bestDev; from = best + 1; lastY = segs[best].Y; found++;
                }
                if (found < 0) continue;
                if (missing == 1 && missingAt == 4)
                {
                    // four consecutive lines: the missing one may be above as well
                    double sp = (ys[3] - ys[0]) / 3.0;
                    var ys2 = new double[] { ys[0] - sp, ys[0], ys[1], ys[2], ys[3] };
                    var idx2 = new[] { -1, idx[0], idx[1], idx[2], idx[3] };
                    double thick2 = idx2.Where(t => t >= 0).Average(t => (double)segs[t].Thickness);
                    double minCover2 = idx2.Where(t => t >= 0).Min(t => segs[t].Weight / (double)Math.Max(1, stripWidth));
                    candidates.Add((idx2, ys2, 1, dev / d + Math.Abs(Math.Log(sp / ssGuess)) * 0.3 + 0.5 + (1 - Math.Min(1, minCover2)) * 1.2, sp, thick2));
                }
                double spacing = (ys[4] - ys[0]) / 4.0;
                double thick = idx.Where(t => t >= 0).Average(t => (double)segs[t].Thickness);
                double minCover = idx.Where(t => t >= 0).Min(t => segs[t].Weight / (double)Math.Max(1, stripWidth));
                double score = dev / d + Math.Abs(Math.Log(spacing / ssGuess)) * 0.3 + (missing > 0 ? 0.5 : 0) + (1 - Math.Min(1, minCover)) * 1.2;
                candidates.Add((idx, ys, missing, score, spacing, thick));
            }
        }
        foreach (var c in candidates.OrderBy(c => c.score))
        {
            if (c.idx.Any(t => t >= 0 && used[t])) continue;
            var g = new Group { Spacing = c.spacing, Thickness = c.thick, Strip = strip, Partial = c.missing > 0 };
            for (int k = 0; k < 5; k++) { g.Ys[k] = c.ys[k]; if (c.idx[k] >= 0) used[c.idx[k]] = true; }
            groups.Add(g);
        }
        return groups;
    }

    private static Staff? BuildStaff(BinaryImage bin, Chain chain, int stripW, int maxRun)
    {
        var staff = new Staff();
        staff.Space = chain.Groups.Average(g => g.Spacing);
        staff.LineThickness = Math.Max(1, chain.Groups.Average(g => g.Thickness));
        for (int k = 0; k < 5; k++)
        {
            var line = new StaffLine { Thickness = staff.LineThickness };
            foreach (var g in chain.Groups) line.Points.Add((g.Strip * stripW + stripW / 2.0, g.Ys[k]));
            staff.Lines.Add(line);
        }
        int thinRun = Math.Max(maxRun, (int)Math.Round(2.5 * staff.LineThickness));
        // Extend each line beyond the first/last strip centre by following thin ink, then refine all lines.
        var lefts = new List<double>(); var rights = new List<double>();
        foreach (var line in staff.Lines)
        {
            double xStart = line.Points[0].X, xEnd = line.Points[^1].X;
            lefts.Add(Follow(bin, line, (int)xStart, -1, staff, thinRun));
            rights.Add(Follow(bin, line, (int)xEnd, +1, staff, thinRun));
        }
        lefts.Sort(); rights.Sort();
        staff.Left = lefts[2];
        staff.Right = rights[2];
        foreach (var line in staff.Lines) Refine(bin, line, staff, thinRun);
        return staff;
    }

    /// <summary>Follow a staff line from x in direction dir along thin ink; returns the x where the ink stops.</summary>
    private static double Follow(BinaryImage bin, StaffLine line, int xStart, int dir, Staff staff, int thinRun)
    {
        double y0 = line.YAt(xStart);
        double slope = 0;
        if (line.Points.Count >= 2)
        {
            var p = line.Points;
            var (ax, ay) = dir < 0 ? p[0] : p[^1];
            var (bx, by) = dir < 0 ? p[1] : p[^2];
            slope = bx != ax ? (ay - by) / (ax - bx) : 0;
            slope = Math.Clamp(slope, -0.06, 0.06);
        }
        double y = y0;
        int maxGap = (int)Math.Max(3, 1.5 * staff.Space);
        int tol = (int)Math.Max(2, staff.LineThickness + 1);
        double band = Math.Max(3, 0.45 * staff.Space);
        int lastInk = xStart;
        int x = xStart;
        var tracked = new List<(double X, double Y)>();
        int sinceTrack = 0;
        while (x >= 0 && x < bin.Width)
        {
            double expected = y0 + slope * (x - xStart);
            int yc = (int)Math.Round(y);
            int found = -1; int bestDy = int.MaxValue;
            for (int dy = -tol; dy <= tol; dy++)
            {
                int yy = yc + dy;
                if (!bin[x, yy]) continue;
                int len = bin.VerticalRunAt(x, yy, out int top);
                if (len > thinRun) { dy = top + len - yc; continue; }  // thick ink: symbol, skip
                double cy = top + (len - 1) / 2.0;
                if (Math.Abs(cy - expected) > band) continue;
                if (Math.Abs(dy) < bestDy) { bestDy = Math.Abs(dy); found = yy; }
            }
            if (found >= 0)
            {
                int len = bin.VerticalRunAt(x, found, out int top);
                double cy = top + (len - 1) / 2.0;
                y = 0.8 * y + 0.2 * cy;
                lastInk = x;
                if (++sinceTrack >= 24) { tracked.Add((x, y)); sinceTrack = 0; }
            }
            else if (Math.Abs(x - lastInk) > maxGap) break;
            x += dir;
        }
        if (tracked.Count > 0)
        {
            if (dir < 0) { tracked.Reverse(); line.Points.InsertRange(0, tracked.Where(p => p.X < line.Points[0].X)); }
            else line.Points.AddRange(tracked.Where(p => p.X > line.Points[^1].X));
        }
        return lastInk;
    }

    /// <summary>Re-measure the line position at full resolution: median of thin-ink centres in windows along x.</summary>
    private static void Refine(BinaryImage bin, StaffLine line, Staff staff, int thinRun)
    {
        int x0 = (int)Math.Max(0, staff.Left), x1 = (int)Math.Min(bin.Width - 1, staff.Right);
        if (x1 - x0 < 16) return;
        double tol = Math.Max(2, 0.3 * staff.Space);
        var samples = new List<(int x, double y)>();
        for (int x = x0; x <= x1; x += 2)
        {
            double ym = line.YAt(x);
            int yc = (int)Math.Round(ym);
            double best = double.NaN; double bestD = double.MaxValue;
            for (int yy = yc - (int)tol; yy <= yc + (int)tol; yy++)
            {
                if (!bin[x, yy]) continue;
                int len = bin.VerticalRunAt(x, yy, out int top);
                if (len <= thinRun)
                {
                    double cy = top + (len - 1) / 2.0;
                    double d = Math.Abs(cy - ym);
                    if (d < bestD) { bestD = d; best = cy; }
                }
                yy = top + len;
            }
            if (!double.IsNaN(best)) samples.Add((x, best));
        }
        if (samples.Count < 8) return;
        int win = (int)Math.Max(24, 2 * staff.Space);
        var pts = new List<(double X, double Y)>();
        for (int cx = x0; cx <= x1; cx += win)
        {
            var inWin = samples.Where(s => s.x >= cx - win && s.x <= cx + win).Select(s => s.y).ToList();
            if (inWin.Count >= 4) pts.Add((cx, Median(inWin)));
            else pts.Add((cx, line.YAt(cx)));
        }
        pts.Add((x1, pts.Count > 0 ? pts[^1].Y : line.YAt(x1)));
        line.Points.Clear();
        line.Points.AddRange(pts);
    }

    public static double Median(IEnumerable<double> values)
    {
        var l = values.OrderBy(v => v).ToList();
        if (l.Count == 0) return 0;
        return l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2;
    }

    public static double Mode(IEnumerable<double> values, double binSize)
    {
        var counts = new Dictionary<int, (int n, double sum)>();
        foreach (var v in values)
        {
            int b = (int)Math.Round(v / binSize);
            counts[b] = counts.TryGetValue(b, out var c) ? (c.n + 1, c.sum + v) : (1, v);
        }
        if (counts.Count == 0) return 0;
        var best = counts.OrderByDescending(kv => kv.Value.n).First();
        return best.Value.sum / best.Value.n;
    }
}
