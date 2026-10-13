using PVOMR.Core.Model;

namespace PVOMR.Core.Analysis;

/// <summary>
/// Bar line edits after detection: adding a bar line splits the measure under it in two, removing one joins the two
/// measures beside it, both on every staff of the system, with the symbols shared out by position. Used for
/// corrections and for symbols that can only be a missed bar line (a "brace" in the middle of a staff).
/// </summary>
public static class MeasureSplitter
{
    private static List<Staff> SystemStaves(PageAnalysis page, Staff staff, out StaffSystem? sys)
    {
        sys = page.Systems.FirstOrDefault(s => s.Staves.Contains(staff));
        return sys != null ? sys.Staves : new List<Staff> { staff };
    }

    /// <summary>Splits the measures at the position of <paramref name="bar"/> (which leaves the symbol lists). False when
    /// there is no measure to split there (too close to an existing bar line).</summary>
    public static bool SplitAt(PageAnalysis page, Staff staff, MusicSymbol bar)
    {
        if (!AddBarline(page, staff, bar.Bounds.CenterX, bar.Bounds.X0, bar.Bounds.X1, BarlineKind.Single)) return false;
        foreach (var st in SystemStaves(page, staff, out _))
            foreach (var m in st.Measures) m.Symbols.Remove(bar);
        return true;
    }

    /// <summary>Adds a bar line at x on every staff of the system (where a measure can be split there).</summary>
    public static bool AddBarline(PageAnalysis page, Staff staff, double x, int x0, int x1, BarlineKind kind)
    {
        bool any = false;
        foreach (var st in SystemStaves(page, staff, out var sys))
        {
            var m = st.Measures.FirstOrDefault(m => x > m.X0 + 1.0 * st.Space && x < m.X1 - 1.0 * st.Space);
            if (m == null) continue;
            var line = new Barline { X = x, X0 = x0, X1 = x1, Kind = kind, StaffFrom = st.Index, StaffTo = st.Index };
            var right = new Measure { Staff = st, X0 = x, X1 = m.X1, LeftBarline = line, RightBarline = m.RightBarline };
            m.X1 = x; m.RightBarline = line;
            foreach (var s in m.Symbols.Where(s => s.Bounds.CenterX > x).ToList()) { m.Symbols.Remove(s); right.Symbols.Add(s); }
            st.Measures.Insert(st.Measures.IndexOf(m) + 1, right);
            Renumber(st);
            st.Barlines.Add(line);
            st.Barlines.Sort((a, b) => a.X.CompareTo(b.X));
            any = true;
        }
        var system = page.Systems.FirstOrDefault(s => s.Staves.Contains(staff));
        if (any && system != null && !system.MeasureBoundaries.Any(b => Math.Abs(b - x) < staff.Space)) { system.MeasureBoundaries.Add(x); system.MeasureBoundaries.Sort(); }
        return any;
    }

    /// <summary>The bar line of a staff nearest to x, within one staff space.</summary>
    public static Barline? BarlineAt(Staff staff, double x) =>
        staff.Barlines.Where(b => Math.Abs(b.X - x) <= Math.Max(staff.Space, (b.X1 - b.X0) / 2.0 + 0.5 * staff.Space))
                      .OrderBy(b => Math.Abs(b.X - x)).FirstOrDefault();

    /// <summary>Removes the bar line at x on every staff of the system, joining the measures on both sides. False when
    /// there is none, or it is the first or last line of the staff (nothing to join).</summary>
    public static bool RemoveBarline(PageAnalysis page, Staff staff, double x, bool systemWide = true)
    {
        bool any = false;
        foreach (var st in systemWide ? SystemStaves(page, staff, out _) : new List<Staff> { staff })
        {
            var b = BarlineAt(st, x);
            double bx = b?.X ?? x;
            // the two measures that meet at the line (found by position: a measure may hold another staff's line object)
            int i = st.Measures.FindIndex(m => m.RightBarline != null && m.RightBarline == b);
            if (i < 0) i = st.Measures.FindIndex(m => Math.Abs(m.X1 - bx) <= Math.Max(st.Space, b == null ? 0 : (b.X1 - b.X0) / 2.0 + 0.5 * st.Space));
            double edgeTol = Math.Max(st.Space, b == null ? 0 : (b.X1 - b.X0) / 2.0 + 0.5 * st.Space);
            if (b != null && !st.Measures.Any(m => Math.Abs(m.X0 - bx) <= edgeTol || Math.Abs(m.X1 - bx) <= edgeTol))
            {
                // a line inside a measure (no boundary: a sliver was merged over it): it only has to go from the list
                st.Barlines.Remove(b);
                any = true;
                continue;
            }
            if (i < 0 || i + 1 >= st.Measures.Count)
            {
                // the first or last line of the staff (often the heavy bar of a clef at the start): nothing to join,
                // the measure next to it simply runs to the staff's edge
                double tol = Math.Max(st.Space, b == null ? 0 : (b.X1 - b.X0) / 2.0 + 0.5 * st.Space);
                var firstM = st.Measures.FirstOrDefault(); var lastM = st.Measures.LastOrDefault();
                if (firstM != null && Math.Abs(firstM.X0 - bx) <= tol) { firstM.X0 = Math.Min(firstM.X0, st.Left); firstM.LeftBarline = null; }
                else if (lastM != null && Math.Abs(lastM.X1 - bx) <= tol) { lastM.X1 = Math.Max(lastM.X1, st.Right); lastM.RightBarline = null; }
                else continue;
                if (b != null) st.Barlines.Remove(b);
                any = true;
                continue;
            }
            var left = st.Measures[i]; var right = st.Measures[i + 1];
            left.X1 = right.X1; left.RightBarline = right.RightBarline;
            left.Symbols.AddRange(right.Symbols);
            st.Measures.RemoveAt(i + 1);
            Renumber(st);
            if (b != null) st.Barlines.Remove(b);
            any = true;
        }
        var system = page.Systems.FirstOrDefault(s => s.Staves.Contains(staff));
        if (any && system != null) system.MeasureBoundaries.RemoveAll(mb => Math.Abs(mb - x) < staff.Space);
        return any;
    }

    /// <summary>Sets the kind of the bar line at x on every staff of the system.</summary>
    public static bool SetBarlineKind(PageAnalysis page, Staff staff, double x, BarlineKind kind)
    {
        bool any = false;
        foreach (var st in SystemStaves(page, staff, out _))
        {
            var b = BarlineAt(st, x);
            if (b == null) continue;
            b.Kind = kind;
            any = true;
        }
        return any;
    }

    private static void Renumber(Staff st)
    {
        for (int i = 0; i < st.Measures.Count; i++) st.Measures[i].Index = i;
    }

    /// <summary>
    /// The heavy and thin bars of an old-style C clef look like a (double) bar line: a bar line that runs through a clef
    /// symbol is removed and the measures beside it are joined. Returns the number removed.
    /// </summary>
    public static int RemoveBarlinesInClefs(PageAnalysis page)
    {
        int removed = 0;
        foreach (var st in page.Staves)
        {
            double ss = st.Space;
            // only C clefs (their heavy and thin bars), and only a line within the clef's own ink: a clef change right
            // after a real bar line in a full score keeps its bar line; the line goes on this staff only
            var clefs = st.Measures.SelectMany(m => m.Symbols).Where(s => s.Kind == SymbolKind.Clef && s.Name == "cClef" && s.Bounds.Width >= 1.2 * ss).ToList();
            foreach (var c in clefs)
            {
                var bar = st.Barlines.FirstOrDefault(b => b.X0 >= c.Bounds.X0 - 2 && b.X <= c.Bounds.X0 + 0.45 * c.Bounds.Width);
                if (bar == null) continue;
                if (RemoveBarline(page, st, bar.X, systemWide: false))
                {
                    page.Notes.Add($"staff {st.Index + 1}: bar line at x={bar.X:0} is part of a clef; removed");
                    removed++;
                }
            }
        }
        return removed;
    }

    /// <summary>
    /// A brace matched in the middle of a staff of a one-staff system is a bar line the detector missed (a thin stroke
    /// over the full staff height; braces only stand left of the system). Returns the number of bar lines added.
    /// </summary>
    public static int SplitMissedBarlines(PageAnalysis page)
    {
        int added = 0;
        foreach (var sys in page.Systems.Where(s => s.Staves.Count == 1))
        {
            var st = sys.Staves[0];
            double ss = st.Space;
            // braces, and thin strokes that run exactly from the top line to the bottom line taken for something else
            // (a "clef" or an unknown symbol a fraction of a space wide)
            bool Candidate(MusicSymbol s)
            {
                double cx = s.Bounds.CenterX;
                if (s.Kind == SymbolKind.Brace)
                    return s.Bounds.Height >= 3.4 * ss && s.Bounds.Height <= 5.0 * ss && s.Bounds.Width <= 0.6 * ss
                           && s.Bounds.Y0 <= st.TopY(cx) + 0.5 * ss && s.Bounds.Y1 >= st.BottomY(cx) - 0.5 * ss;
                return s.Kind is SymbolKind.Unknown or SymbolKind.Clef or SymbolKind.Text && s.Notes.Count == 0 && s.Bounds.Width <= 0.5 * ss
                       && Math.Abs(s.Bounds.Y0 - st.TopY(cx)) <= 0.4 * ss && Math.Abs(s.Bounds.Y1 - st.BottomY(cx)) <= 0.4 * ss;
            }
            var strokes = st.Measures.SelectMany(m => m.Symbols).Where(Candidate).ToList();
            foreach (var b in strokes)
            {
                double cx = b.Bounds.CenterX;
                if (cx < st.Left + 4 * ss || cx > st.Right - 1.0 * ss) continue;
                if (SplitAt(page, st, b))
                {
                    page.Notes.Add($"staff {st.Index + 1}: a {b.Kind.ToString().ToLowerInvariant()} at x={cx:0} inside the staff taken as a missed bar line");
                    added++;
                }
            }
        }
        return added;
    }
}
