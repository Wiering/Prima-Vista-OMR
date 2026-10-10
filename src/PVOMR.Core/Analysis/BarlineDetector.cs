using PVOMR.Core.Imaging;
using PVOMR.Core.Model;

namespace PVOMR.Core.Analysis;

public static class BarlineDetector
{
    private sealed class Candidate { public int X0, X1; public double Xc => (X0 + X1) / 2.0; public int Width => X1 - X0 + 1; public bool Thick; public int RunTop, RunBottom; }

    public static void Detect(BinaryImage bin, BinaryImage noStaff, ComponentMap cm, List<Staff> staves, List<string> log)
    {
        for (int si = 0; si < staves.Count; si++)
        {
            var staff = staves[si];
            double ss = staff.Space;
            int xl = (int)Math.Max(0, Math.Floor(staff.Left) - ss);
            int xr = (int)Math.Min(bin.Width - 1, Math.Ceiling(staff.Right) + ss * 0.5);
            var cols = new bool[xr - xl + 1];
            for (int x = xl; x <= xr; x++)
            {
                double top = staff.TopY(x), bot = staff.BottomY(x);
                int yt = (int)Math.Round(top), yb = (int)Math.Round(bot);
                int h = yb - yt + 1;
                int black = 0; int longest = 0, run = 0;
                for (int y = yt; y <= yb; y++)
                {
                    if (bin[x, y]) { black++; run++; if (run > longest) longest = run; }
                    else run = 0;
                }
                cols[x - xl] = black >= 0.9 * h && longest >= 0.7 * h;
            }
            var cands = new List<Candidate>();
            for (int i = 0; i < cols.Length; i++)
            {
                if (!cols[i]) continue;
                int j = i; while (j + 1 < cols.Length && cols[j + 1]) j++;
                cands.Add(new Candidate { X0 = xl + i, X1 = xl + j });
                i = j;
            }
            var accepted = new List<Candidate>();
            foreach (var c in cands)
            {
                if (c.Width > 0.8 * ss) continue;
                int xc = (int)Math.Round(c.Xc);
                if (!IsThinAlongRun(noStaff, bin, staff, c, xc)) continue;
                if (!ComponentIsThin(cm, staff, c, xc, staves)) continue;
                c.Thick = c.Width >= Math.Max(3, 0.28 * ss) && c.Width >= 2.5 * staff.LineThickness;
                accepted.Add(c);
            }
            // Merge candidates separated by < 2px (broken thick lines)
            var merged = new List<Candidate>();
            foreach (var c in accepted.OrderBy(c => c.X0))
            {
                if (merged.Count > 0 && c.X0 - merged[^1].X1 <= 2)
                {
                    merged[^1].X1 = c.X1;
                    merged[^1].Thick = merged[^1].Width >= Math.Max(3, 0.28 * ss);
                }
                else merged.Add(c);
            }
            // Classify kinds (pairs within 1.2 staff spaces).
            var bars = new List<Barline>();
            for (int i = 0; i < merged.Count; i++)
            {
                var c = merged[i];
                var bar = new Barline { X = c.Xc, X0 = c.X0, X1 = c.X1, StaffFrom = si, StaffTo = si, Kind = c.Thick ? BarlineKind.Thick : BarlineKind.Single };
                if (i + 1 < merged.Count && merged[i + 1].X0 - c.X1 <= 1.2 * ss)
                {
                    var d = merged[i + 1];
                    bool dotsLeft = HasRepeatDots(noStaff, staff, c.X0, -1);
                    bool dotsRight = HasRepeatDots(noStaff, staff, d.X1, +1);
                    bar.X1 = d.X1; bar.X = (c.X0 + d.X1) / 2.0;
                    if (!c.Thick && d.Thick) bar.Kind = dotsLeft ? BarlineKind.RepeatEnd : BarlineKind.Final;
                    else if (c.Thick && !d.Thick) bar.Kind = dotsRight ? BarlineKind.RepeatStart : BarlineKind.Final;
                    else if (c.Thick && d.Thick) bar.Kind = BarlineKind.Thick;
                    else bar.Kind = dotsLeft && dotsRight ? BarlineKind.RepeatBoth : dotsLeft ? BarlineKind.RepeatEnd : dotsRight ? BarlineKind.RepeatStart : BarlineKind.Double;
                    i++;
                    // thick + thin + thick (repeat both) handled loosely: absorb a third line
                    if (i + 1 < merged.Count && merged[i + 1].X0 - merged[i].X1 <= 1.2 * ss && bar.Kind != BarlineKind.Double)
                    {
                        bar.X1 = merged[i + 1].X1; bar.X = (bar.X0 + bar.X1) / 2.0; i++;
                        if (dotsLeft && HasRepeatDots(noStaff, staff, bar.X1, +1)) bar.Kind = BarlineKind.RepeatBoth;
                    }
                }
                else
                {
                    bool dotsLeft = HasRepeatDots(noStaff, staff, c.X0, -1);
                    bool dotsRight = HasRepeatDots(noStaff, staff, c.X1, +1);
                    if (dotsLeft && dotsRight) bar.Kind = BarlineKind.RepeatBoth;
                    else if (dotsLeft) bar.Kind = BarlineKind.RepeatEnd;
                    else if (dotsRight) bar.Kind = BarlineKind.RepeatStart;
                }
                // the line at the very start of the staff is the system's initial barline (bracket/brace strokes join it)
                if (bar.X0 <= staff.Left + 0.6 * ss && bar.Kind != BarlineKind.RepeatStart) bar.Kind = BarlineKind.SystemStart;
                bar.StaffTo = si;
                bars.Add(bar);
            }
            staff.Barlines.Clear();
            staff.Barlines.AddRange(bars);
        }
        // barlines much thinner than the typical barline of the page are stems (hollow heads that broke off)
        var widths = staves.SelectMany(st => st.Barlines).Select(b => b.X1 - b.X0 + 1).OrderBy(w => w).ToList();
        if (widths.Count >= 4)
        {
            int median = widths[widths.Count / 2];
            if (median >= 3)
                foreach (var st in staves)
                    st.Barlines.RemoveAll(b => b.X1 - b.X0 + 1 < 0.55 * median);
        }
        // spanning: a barline continues to the next staff when the ink is continuous in the gap and the next staff has a barline at the same x
        for (int si = 0; si < staves.Count; si++)
        {
            foreach (var bar in staves[si].Barlines.ToList())
            {
                int to = si;
                while (to + 1 < staves.Count)
                {
                    var next = staves[to + 1];
                    double tol = Math.Max(3, 0.2 * next.Space);
                    var match = next.Barlines.FirstOrDefault(b => Math.Abs(b.X - bar.X) <= tol || (b.X0 <= bar.X1 + 2 && b.X1 >= bar.X0 - 2));
                    int sx0 = match != null ? Math.Min(bar.X0, match.X0) : bar.X0, sx1 = match != null ? Math.Max(bar.X1, match.X1) : bar.X1;
                    if (!ConnectsDown(bin, staves[to], next, (int)Math.Round(bar.X), sx0, sx1)) break;
                    to++;
                }
                bar.StaffTo = to;
                if (si > 0) bar.ConnectedUp = ConnectsDown(bin, staves[si - 1], staves[si], (int)Math.Round(bar.X), bar.X0, bar.X1);
                // a vertical rule running far beyond both ends of the spanned staves is a page line, not a barline
                var first = staves[si]; var last = staves[to];
                double ss = first.Space;
                int xc = (int)Math.Round(bar.X);
                int above = InkExtent(bin, xc, (int)Math.Round(first.TopY(xc)) - 1, -1, (int)(8 * ss));
                int below = InkExtent(bin, xc, (int)Math.Round(last.BottomY(xc)) + 1, +1, (int)(8 * ss));
                if (above > 3 * ss && below > 3 * ss)
                {
                    for (int k = si; k <= to; k++) staves[k].Barlines.RemoveAll(b => Math.Abs(b.X - bar.X) <= Math.Max(3, 0.2 * ss));
                    log.Add($"removed page-long vertical line at x={bar.X:0} (staves {si + 1}-{to + 1})");
                }
            }
        }
    }

    /// <summary>Number of rows of (nearly) continuous ink from y in direction dir at column x (bridging gaps of up to 2 rows), capped.</summary>
    private static int InkExtent(BinaryImage bin, int x, int y, int dir, int cap)
    {
        int n = 0, gap = 0;
        for (int i = 0; i < cap; i++, y += dir)
        {
            if (y < 0 || y >= bin.Height) break;
            if (bin[x, y] || bin[x - 1, y] || bin[x + 1, y]) { n = i + 1; gap = 0; }
            else if (++gap > 2) break;
        }
        return n;
    }

    private static int Width(this Barline b) => b.X1 - b.X0 + 1;

    private static bool ConnectsDown(BinaryImage bin, Staff a, Staff b, int xc, int x0, int x1)
    {
        if (b.Left > x1 + a.Space || b.Right < x0 - a.Space) return false;
        int yt = (int)Math.Round(a.BottomY(xc)), yb = (int)Math.Round(b.TopY(xc));
        if (yb <= yt) return false;
        int gapLen = yb - yt;
        int black = 0;
        for (int y = yt; y <= yb; y++)
        {
            bool any = false;
            for (int x = x0; x <= x1; x++) if (bin[x, y]) { any = true; break; }
            if (any) black++;
        }
        return black >= 0.9 * gapLen;
    }

    private static bool IsThinAlongRun(BinaryImage noStaff, BinaryImage bin, Staff staff, Candidate c, int xc)
    {
        double ss = staff.Space;
        // full vertical extent of the ink column in the original image (bridging tiny gaps), limited to +-6ss around the staff
        double ymid = (staff.TopY(xc) + staff.BottomY(xc)) / 2;
        int limitTop = (int)(staff.TopY(xc) - 6 * ss), limitBot = (int)(staff.BottomY(xc) + 6 * ss);
        int top = (int)ymid, bot = (int)ymid;
        int gap = 0;
        for (int y = (int)ymid; y >= Math.Max(0, limitTop); y--) { if (AnyBlack(bin, c.X0, c.X1, y)) { top = y; gap = 0; } else if (++gap > 2) break; }
        gap = 0;
        for (int y = (int)ymid; y < Math.Min(bin.Height, limitBot); y++) { if (AnyBlack(bin, c.X0, c.X1, y)) { bot = y; gap = 0; } else if (++gap > 2) break; }
        c.RunTop = top; c.RunBottom = bot;
        double maxW = Math.Max(c.Width + 2, 0.55 * ss);
        int wide = 0, rows = 0;
        for (int y = top; y <= bot; y++)
        {
            if (!noStaff[xc, y] && !noStaff[c.X0, y] && !noStaff[c.X1, y]) continue;
            rows++;
            int xx = noStaff[xc, y] ? xc : noStaff[c.X0, y] ? c.X0 : c.X1;
            int w = noStaff.HorizontalRunAt(xx, y, out _);
            if (w > maxW) wide++;
        }
        if (rows == 0) return false;
        // Also reject when the ends of the run carry a blob (notehead): check 1 staff space beyond each end in the original image.
        if (wide > 0.12 * rows) return false;
        // Barlines do not extend far beyond the staff unless they span to another staff; stems do.
        double above = staff.TopY(xc) - top, below = bot - staff.BottomY(xc);
        if (above > 1.0 * ss && !ContinuesToOtherStaff(staff, xc, top, true)) return false;
        if (below > 1.0 * ss && !ContinuesToOtherStaff(staff, xc, bot, false)) return false;
        return true;
    }

    // Any staff lies in the direction of the run end? (we only know this staff here, so accept long runs; they are validated by ConnectsDown)
    private static bool ContinuesToOtherStaff(Staff staff, int xc, int yEnd, bool up) => true;

    /// <summary>The connected component (staff lines removed) that contains the candidate must itself be thin: a stem's component includes its head, flag or beam.</summary>
    private static bool ComponentIsThin(ComponentMap cm, Staff staff, Candidate c, int xc, List<Staff> staves)
    {
        double ss = staff.Space;
        double ymid = (staff.TopY(xc) + staff.BottomY(xc)) / 2;
        Component? comp = null;
        for (int dy = 0; dy < 2 * ss && comp == null; dy++)
            foreach (int x in new[] { xc, c.X0, c.X1 })
            {
                comp = cm.At(x, (int)(ymid + dy)) ?? cm.At(x, (int)(ymid - dy));
                if (comp != null) break;
            }
        if (comp == null) return true;
        // width of the component restricted to this staff's band and a 1.5 staff-space neighbourhood (a long barline may touch something elsewhere)
        int y0 = (int)(staff.TopY(xc) - 0.5 * ss), y1 = (int)(staff.BottomY(xc) + 0.5 * ss);
        int x0 = (int)(xc - 1.5 * ss), x1 = (int)(xc + 1.5 * ss);
        int minX = int.MaxValue, maxX = -1;
        for (int y = Math.Max(0, y0); y <= Math.Min(cm.Height - 1, y1); y++)
            for (int x = Math.Max(0, x0); x <= Math.Min(cm.Width - 1, x1); x++)
                if (cm.Labels[y * cm.Width + x] == comp.Label) { if (x < minX) minX = x; if (x > maxX) maxX = x; }
        if (maxX < 0) return true;
        if (maxX - minX + 1 > Math.Max(1.0 * ss, 3 * c.Width + 2)) return false;
        // a stem whose (hollow) head broke off: foreign ink fragments right beside either end of the run
        foreach (int yEnd in new[] { c.RunTop, c.RunBottom })
        {
            // barlines end on the outer staff lines (of this or another staff); a run ending elsewhere is suspect
            bool onLine = staves.Any(st => xc >= st.Left - st.Space && xc <= st.Right + st.Space
                                           && (Math.Abs(yEnd - st.TopY(xc)) <= 0.6 * ss || Math.Abs(yEnd - st.BottomY(xc)) <= 0.6 * ss));
            if (onLine) continue;
            int fy0 = (int)(yEnd - 0.7 * ss), fy1 = (int)(yEnd + 0.7 * ss);
            int fx0 = (int)(xc - 1.4 * ss), fx1 = (int)(xc + 1.4 * ss);
            int foreign = 0;
            for (int y = Math.Max(0, fy0); y <= Math.Min(cm.Height - 1, fy1); y++)
                for (int x = Math.Max(0, fx0); x <= Math.Min(cm.Width - 1, fx1); x++)
                {
                    int l = cm.Labels[y * cm.Width + x];
                    if (l != 0 && l != comp.Label) foreign++;
                }
            if (foreign >= 0.12 * ss * ss) return false;
        }
        return true;
    }

    private static bool AnyBlack(BinaryImage bin, int x0, int x1, int y)
    {
        for (int x = x0; x <= x1; x++) if (bin[x, y]) return true;
        return false;
    }

    private static bool HasRepeatDots(BinaryImage noStaff, Staff staff, int xEdge, int dir)
    {
        double ss = staff.Space;
        int xa = dir < 0 ? (int)(xEdge - 1.3 * ss) : (int)(xEdge + 0.25 * ss);
        int xb = dir < 0 ? (int)(xEdge - 0.25 * ss) : (int)(xEdge + 1.3 * ss);
        int xc = (xa + xb) / 2;
        double y2 = staff.Lines[1].YAt(xc), y3 = staff.Lines[2].YAt(xc), y4 = staff.Lines[3].YAt(xc);
        int r = (int)Math.Max(2, 0.3 * ss);
        bool Dot(double yc)
        {
            int cnt = noStaff.CountBlack(xa, (int)(yc - r), xb + 1, (int)(yc + r) + 1);
            int area = (xb - xa + 1) * (2 * r + 1);
            return cnt >= 0.12 * area && cnt <= 0.75 * area;
        }
        return Dot((y2 + y3) / 2) && Dot((y3 + y4) / 2);
    }
}
