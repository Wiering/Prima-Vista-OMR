using PVOMR.Core.Imaging;
using PVOMR.Core.Model;

namespace PVOMR.Core.Analysis;

/// <summary>
/// Turns connected components of the staff-less image into musical symbols: template classification for isolated
/// glyphs, and structural analysis (stems, note heads, beams, flags) for note groups.
/// </summary>
public sealed class SymbolAnalyzer
{
    private readonly GlyphLibrary? _lib;
    private readonly BinaryImage _bin;
    private readonly BinaryImage _ns;
    private readonly ComponentMap _cm;
    private readonly PageAnalysis _page;
    private readonly AnalysisOptions _opt;

    public SymbolAnalyzer(GlyphLibrary? lib, BinaryImage bin, BinaryImage noStaff, ComponentMap cm, PageAnalysis page, AnalysisOptions opt)
    {
        _lib = lib; _bin = bin; _ns = noStaff; _cm = cm; _page = page; _opt = opt;
    }

    private sealed class Zone { public Staff Staff = null!; public double Top, Bottom; }
    private readonly HashSet<Component> _consumed = new();

    public void Run()
    {
        var staves = _page.Staves;
        if (staves.Count == 0) return;
        var zones = BuildZones(staves);
        int noise = 0, outside = 0;
        var perStaff = staves.ToDictionary(s => s, _ => new List<MusicSymbol>());
        var compsPerStaff = staves.ToDictionary(s => s, _ => new List<Component>());

        foreach (var c in _cm.Components)
        {
            var staff = AssignStaff(c, zones);
            if (staff == null)
            {
                outside++;
                if (c.Width >= _page.StaffSpace * 0.5 || c.Height >= _page.StaffSpace * 0.5)
                    _page.PageSymbols.Add(new MusicSymbol { Kind = SymbolKind.Unknown, Name = "ink", Bounds = ToRect(c), Confidence = 0 });
                continue;
            }
            double ss = staff.Space;
            if (c.PixelCount < Math.Max(3, 0.03 * ss * ss) && c.Width < 0.3 * ss && c.Height < 0.3 * ss) { noise++; continue; }
            if (IsBarlineComponent(c, staff)) continue;
            compsPerStaff[staff].Add(c);
        }
        _page.Notes.Add($"components: {_cm.Components.Count}, noise {noise}, outside staves {outside}");

        foreach (var staff in staves)
        {
            var comps = compsPerStaff[staff];
            var syms = perStaff[staff];
            var consumed = GroupTextLines(comps, staff, syms);
            var classified = new List<(MusicSymbol sym, Component comp)>();
            foreach (var c in comps)
            {
                if (consumed.Contains(c) || _consumed.Contains(c)) continue;
                var sym = Classify(c, staff);
                if (sym != null) classified.Add((sym, c));
            }
            classified.RemoveAll(p => _consumed.Contains(p.comp));
            MergeAndRematch(classified, staff);
            syms.AddRange(classified.Select(p => p.sym));
            MergeText(syms, staff);
            MergeDynamics(syms, staff);
            GroupDottedLines(syms, staff);
            AttachDetachedFlags(syms, staff);
            AttachDotsAndAccidentals(syms, staff);
            DetectHairpinsAndSlurs(syms, staff);
            foreach (var s in syms.OrderBy(s => s.Bounds.X0))
            {
                var m = FindMeasure(staff, s);
                if (m != null) m.Symbols.Add(s);
            }
        }
    }

    /// <summary>Groups runs of 4+ glyph-sized components outside the staff into text lines (titles, lyrics, "cresc.").</summary>
    private HashSet<Component> GroupTextLines(List<Component> comps, Staff staff, List<MusicSymbol> syms)
    {
        double ss = staff.Space;
        var consumed = new HashSet<Component>();
        var cands = comps.Where(c =>
        {
            double h = c.Height / ss, w = c.Width / ss;
            if (h < 0.25 || h > 2.6 || w > 4) return false;
            double cy = c.CenterY;
            return cy < staff.TopY(c.CenterX) - 0.4 * ss || cy > staff.BottomY(c.CenterX) + 0.4 * ss;
        }).OrderBy(c => c.X0).ToList();
        var chains = new List<List<Component>>();
        foreach (var c in cands)
        {
            List<Component>? best = null; double bestGap = double.MaxValue;
            foreach (var ch in chains)
            {
                var last = ch[^1];
                double gap = c.X0 - last.X1;
                if (gap > 0.6 * ss || gap < -0.5 * ss) continue;
                int yo = Math.Min(c.Y1, last.Y1) - Math.Max(c.Y0, last.Y0);
                if (yo < 0.3 * Math.Min(c.Height, last.Height)) continue;
                double hr = (double)c.Height / last.Height;
                if (hr > 3 || hr < 1 / 3.0) continue;
                if (gap < bestGap) { bestGap = gap; best = ch; }
            }
            if (best != null) best.Add(c); else chains.Add(new List<Component> { c });
        }
        foreach (var ch in chains)
        {
            if (ch.Count < 4) continue;
            // a run of accidentals inside the staff is excluded above; here we also skip chains that look like note heads with ledger lines
            int x0 = ch.Min(c => c.X0), x1 = ch.Max(c => c.X1), y0 = ch.Min(c => c.Y0), y1 = ch.Max(c => c.Y1);
            if ((y1 - y0) > 3 * ss) continue;
            var t = new MusicSymbol { Kind = SymbolKind.Text, Name = "text", Bounds = new Rect(x0, y0, x1, y1), Staff = staff, Confidence = 0.5 };
            t.Attributes["glyphs"] = ch.Count.ToString();
            syms.Add(t);
            foreach (var c in ch) consumed.Add(c);
        }
        return consumed;
    }

    /// <summary>Unknown fragments are merged with close neighbours and matched again (F clef + dots, broken glyphs).</summary>
    private void MergeAndRematch(List<(MusicSymbol sym, Component comp)> items, Staff staff)
    {
        if (_lib == null) return;
        double ss = staff.Space;
        var unknown = items.Where(p => p.sym.Kind == SymbolKind.Unknown || (p.sym.Kind == SymbolKind.Text && p.sym.Attributes.Count == 0)
                                    || (p.sym.Kind is SymbolKind.Flag or SymbolKind.Articulation or SymbolKind.Breath or SymbolKind.Ornament && p.sym.Confidence < 0.85)).ToList();
        foreach (var u in unknown)
        {
            if (!items.Contains(u)) continue;
            var r = u.sym.Bounds;
            var neighbours = items.Where(p => p != u && p.sym.Kind is SymbolKind.Unknown or SymbolKind.Dot or SymbolKind.Text or SymbolKind.Ledger or SymbolKind.Articulation
                                     && p.comp.X0 <= r.X1 + 0.35 * ss && p.comp.X1 >= r.X0 - 0.35 * ss
                                     && p.comp.Y0 <= r.Y1 + 0.5 * ss && p.comp.Y1 >= r.Y0 - 0.5 * ss)
                                  .OrderBy(p => Math.Abs(p.comp.CenterY - r.CenterY)).Take(3).ToList();
            if (neighbours.Count == 0) continue;
            // try the union of the fragment with 1..n nearest neighbours
            var group = new List<(MusicSymbol sym, Component comp)> { u };
            GlyphMatch? bestMatch = null; List<(MusicSymbol sym, Component comp)>? bestGroup = null;
            foreach (var nb in neighbours)
            {
                group.Add(nb);
                var comps = group.Select(p => p.comp).ToList();
                int x0 = comps.Min(c => c.X0), y0 = comps.Min(c => c.Y0), x1 = comps.Max(c => c.X1), y1 = comps.Max(c => c.Y1);
                int w = x1 - x0 + 1, h = y1 - y0 + 1;
                if (w > 7 * ss || h > 9.5 * ss) break;
                var mask = new byte[w * h];
                foreach (var c in comps)
                    for (int y = c.Y0; y <= c.Y1; y++)
                        for (int x = c.X0; x <= c.X1; x++)
                            if (_cm.Labels[y * _cm.Width + x] == c.Label) mask[(y - y0) * w + (x - x0)] = 1;
                var matches = _lib.Match(mask, w, h, ss);
                if (matches.Count == 0) continue;
                var m = matches[0];
                if (m.Score >= Threshold(m.Template.Kind) + 0.02 && m.Score > u.sym.Confidence + 0.02 && (bestMatch == null || m.Score > bestMatch.Score))
                {
                    bestMatch = m; bestGroup = new List<(MusicSymbol, Component)>(group);
                }
            }
            if (bestMatch == null || bestGroup == null) continue;
            var cs = bestGroup.Select(p => p.comp).ToList();
            var merged = new MusicSymbol
            {
                Kind = bestMatch.Template.Kind, Name = bestMatch.Template.Name, Staff = staff, Confidence = Math.Round(bestMatch.Score, 2),
                Bounds = new Rect(cs.Min(c => c.X0), cs.Min(c => c.Y0), cs.Max(c => c.X1), cs.Max(c => c.Y1)),
            };
            merged.Attributes["merged"] = bestGroup.Count.ToString();
            var fake = new Component { X0 = merged.Bounds.X0, Y0 = merged.Bounds.Y0, X1 = merged.Bounds.X1, Y1 = merged.Bounds.Y1, PixelCount = cs.Sum(c => c.PixelCount) };
            PostProcessTemplate(merged, fake, staff, false);
            foreach (var p in bestGroup) items.Remove(p);
            items.Add((merged, fake));
        }
    }

    private static readonly Dictionary<string, string> DynamicLetters = new()
    {
        ["dynamicPiano"] = "p", ["dynamicForte"] = "f", ["dynamicMezzo"] = "m", ["dynamicSforzando"] = "s", ["dynamicRinforzando2"] = "rf",
        ["dynamicPP"] = "pp", ["dynamicFF"] = "ff", ["dynamicMP"] = "mp", ["dynamicMF"] = "mf", ["dynamicFFF"] = "fff", ["dynamicPPP"] = "ppp", ["dynamicSforzato"] = "sf",
    };

    /// <summary>Sequences of 4+ small dots on one row become a dotted line (ottava/pedal extension).</summary>
    private void GroupDottedLines(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        var dots = syms.Where(s => s.Kind == SymbolKind.Dot).OrderBy(s => s.Bounds.X0).ToList();
        var used = new HashSet<MusicSymbol>();
        foreach (var d in dots)
        {
            if (used.Contains(d)) continue;
            var chain = new List<MusicSymbol> { d };
            var last = d;
            foreach (var o in dots)
            {
                if (used.Contains(o) || o == d || o.Bounds.X0 <= last.Bounds.X0) continue;
                double gap = o.Bounds.X0 - last.Bounds.X1;
                if (gap > 2.0 * ss) { if (o.Bounds.X0 - last.Bounds.X1 > 2.0 * ss) break; }
                if (gap < 0 || Math.Abs(o.Bounds.CenterY - last.Bounds.CenterY) > 0.35 * ss) continue;
                chain.Add(o); last = o;
            }
            if (chain.Count < 4) continue;
            var r = chain[0].Bounds;
            foreach (var o in chain.Skip(1)) r = r.Union(o.Bounds);
            var line = new MusicSymbol { Kind = SymbolKind.Other, Name = "dotted line", Bounds = r, Staff = staff, Confidence = 0.6 };
            line.Attributes["dots"] = chain.Count.ToString();
            foreach (var o in chain) { used.Add(o); syms.Remove(o); }
            syms.Add(line);
        }
    }

    /// <summary>Flags that are not connected to their stem (thin engraving, scan gaps) are attached to the nearest stem end.</summary>
    private void AttachDetachedFlags(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        var notes = syms.Where(s => s.Kind is SymbolKind.Note or SymbolKind.Chord && s.Flags == 0 && s.StemUp.HasValue && s.Attributes.ContainsKey("stemx")).ToList();
        if (notes.Count == 0) return;
        var cands = syms.Where(s => s.Kind == SymbolKind.Flag || ((s.Kind is SymbolKind.Unknown or SymbolKind.Text or SymbolKind.Breath or SymbolKind.Articulation or SymbolKind.Ornament)
                                     && s.Bounds.Height >= 1.3 * ss && s.Bounds.Height <= 3.6 * ss && s.Bounds.Width >= 0.35 * ss && s.Bounds.Width <= 1.6 * ss)).ToList();
        foreach (var n in notes)
        {
            var parts = n.Attributes["stemx"].Split(',');
            int stemX1 = int.Parse(parts[1]);
            int freeEndY = int.Parse(parts[2]);
            var f = cands.Where(c => c.Bounds.X0 >= stemX1 - 0.25 * ss && c.Bounds.X0 <= stemX1 + 0.9 * ss
                                     && (n.StemUp == true ? c.Bounds.Y0 - freeEndY >= -0.5 * ss && c.Bounds.Y0 - freeEndY <= 1.5 * ss
                                                          : freeEndY - c.Bounds.Y1 >= -0.5 * ss && freeEndY - c.Bounds.Y1 <= 1.5 * ss))
                         .OrderBy(c => Math.Abs(c.Bounds.X0 - stemX1)).FirstOrDefault();
            if (f == null) continue;
            n.Flags = f.Kind == SymbolKind.Flag && f.Name.Contains("16th") ? 2 : f.Kind == SymbolKind.Flag && f.Name.Contains("32nd") ? 3 : 1;
            bool hollow = n.Notes.All(h => h.HeadType == "half");
            if (!hollow) n.Duration = n.Flags switch { 1 => "8th", 2 => "16th", 3 => "32nd", _ => "64th" };
            n.Attributes["flag"] = "detached";
            n.Bounds = n.Bounds.Union(f.Bounds);
            syms.Remove(f); cands.Remove(f);
        }
    }

    private void MergeDynamics(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        var dyn = syms.Where(s => s.Kind == SymbolKind.Dynamic).OrderBy(s => s.Bounds.X0).ToList();
        foreach (var d in dyn) d.Attributes["text"] = DynamicLetters.GetValueOrDefault(d.Name, d.Name.Replace("dynamic", "").ToLowerInvariant());
        for (int i = 0; i < dyn.Count; i++)
        {
            var cur = dyn[i];
            if (!syms.Contains(cur)) continue;
            for (int j = i + 1; j < dyn.Count; j++)
            {
                var nx = dyn[j];
                if (nx.Bounds.X0 - cur.Bounds.X1 > 0.5 * ss) break;
                if (Math.Min(nx.Bounds.Y1, cur.Bounds.Y1) - Math.Max(nx.Bounds.Y0, cur.Bounds.Y0) < 0.3 * ss) continue;
                cur.Attributes["text"] += nx.Attributes["text"];
                cur.Bounds = cur.Bounds.Union(nx.Bounds);
                cur.Confidence = Math.Min(cur.Confidence, nx.Confidence);
                syms.Remove(nx);
            }
            cur.Name = cur.Attributes["text"];
        }
    }

    private static Rect ToRect(Component c) => new(c.X0, c.Y0, c.X1, c.Y1);

    private List<Zone> BuildZones(List<Staff> staves)
    {
        var zones = new List<Zone>();
        var sorted = staves.OrderBy(s => s.CenterY).ToList();
        foreach (var s in sorted)
        {
            double xm = (s.Left + s.Right) / 2;
            double top = s.TopY(xm) - 5.5 * s.Space, bot = s.BottomY(xm) + 5.5 * s.Space;
            foreach (var o in sorted)
            {
                if (o == s) continue;
                if (Math.Min(o.Right, s.Right) - Math.Max(o.Left, s.Left) < 2 * s.Space) continue; // no x overlap
                double oxm = (o.Left + o.Right) / 2;
                if (o.CenterY < s.CenterY)
                {
                    double mid = (o.BottomY(oxm) + s.TopY(xm)) / 2;
                    if (mid > top) top = mid;
                }
                else
                {
                    double mid = (s.BottomY(xm) + o.TopY(oxm)) / 2;
                    if (mid < bot) bot = mid;
                }
            }
            zones.Add(new Zone { Staff = s, Top = top, Bottom = bot });
        }
        return zones;
    }

    private Staff? AssignStaff(Component c, List<Zone> zones)
    {
        double cx = c.CenterX, cy = c.CenterY;
        Zone? best = null; double bestD = double.MaxValue;
        foreach (var z in zones)
        {
            var s = z.Staff;
            if (cx < s.Left - 3 * s.Space || cx > s.Right + 3 * s.Space) continue;
            if (cy < z.Top || cy > z.Bottom) continue;
            double top = s.TopY(cx), bot = s.BottomY(cx);
            double d = cy < top ? top - cy : cy > bot ? cy - bot : 0;
            if (d < bestD) { bestD = d; best = z; }
        }
        return best?.Staff;
    }

    private bool IsBarlineComponent(Component c, Staff staff)
    {
        if (c.Width > 0.9 * staff.Space) return false;
        if (c.Height < 0.85 * staff.Height) return false;
        foreach (var b in staff.Barlines)
            if (c.X0 <= b.X1 + 2 && c.X1 >= b.X0 - 2) return true;
        return false;
    }

    private Measure? FindMeasure(Staff staff, MusicSymbol s)
    {
        if (staff.Measures.Count == 0) return null;
        double x = s.Kind is SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup && s.Notes.Count > 0 ? s.Notes.Min(n => n.HeadX) : s.Bounds.CenterX;
        if (s.Kind == SymbolKind.NoteGroup && s.Notes.Count > 0) x = s.Notes.Min(n => n.HeadX);
        foreach (var m in staff.Measures) if (x >= m.X0 && x < m.X1) return m;
        return x < staff.Measures[0].X0 ? staff.Measures[0] : staff.Measures[^1];
    }

    // ------------------------------------------------------------------ classification

    private MusicSymbol? Classify(Component c, Staff staff)
    {
        double ss = staff.Space;
        var sym = new MusicSymbol { Bounds = ToRect(c), Staff = staff, Kind = SymbolKind.Unknown, Name = "unknown" };
        double w = c.Width / ss, h = c.Height / ss;

        // tiny round blob: a dot (augmentation / staccato / repeat dot)
        if (w <= 0.55 && h <= 0.55 && c.Fill > 0.55 && w >= 0.12 && h >= 0.12 && Math.Abs(w - h) < 0.3)
        {
            sym.Kind = SymbolKind.Dot; sym.Name = "dot"; sym.Confidence = 0.8;
            return sym;
        }

        // 0a. clefs first: their long vertical strokes would otherwise be taken for stems
        if (_lib != null && h >= 2.6 && h <= 9.5 && w >= 1.0 && w <= 5)
        {
            bool atStart = c.X0 <= staff.Left + 3.5 * ss && h >= 2.6;
            var mask0 = _cm.Mask(c);
            var clefMatches = _lib.Match(mask0, c.Width, c.Height, ss, 0.35, t => t.Kind == SymbolKind.Clef);
            // the F clef's two dots (and broken clefs) are separate components: try the union with small neighbours to the right
            var dots = _cm.Components.Where(o => o != c && !_consumed.Contains(o) && o.Width <= 1.6 * ss && o.Height <= 2.0 * ss && o.PixelCount >= 0.02 * ss * ss
                                                 && o.X1 >= c.X0 - 0.6 * ss && o.X0 <= c.X1 + 0.7 * ss && o.Y0 >= c.Y0 - 0.6 * ss && o.Y1 <= c.Y1 + 0.6 * ss)
                                     .OrderBy(o => Math.Min(Math.Abs(o.X0 - c.X1), Math.Abs(o.X1 - c.X0))).Take(4).ToList();
            if (dots.Count > 0)
            {
                var all = new List<Component> { c }; all.AddRange(dots);
                int ux0 = all.Min(o => o.X0), uy0 = all.Min(o => o.Y0), ux1 = all.Max(o => o.X1), uy1 = all.Max(o => o.Y1);
                int uw = ux1 - ux0 + 1, uh = uy1 - uy0 + 1;
                var um = new byte[uw * uh];
                foreach (var o in all)
                    for (int y = o.Y0; y <= o.Y1; y++)
                        for (int x = o.X0; x <= o.X1; x++)
                            if (_cm.Labels[y * _cm.Width + x] == o.Label) um[(y - uy0) * uw + (x - ux0)] = 1;
                var um2 = _lib.Match(um, uw, uh, ss, 0.35, t => t.Kind == SymbolKind.Clef);
                if (um2.Count > 0 && (clefMatches.Count == 0 || um2[0].Score > clefMatches[0].Score + 0.03))
                {
                    clefMatches = um2;
                    sym.Bounds = new Rect(ux0, uy0, ux1, uy1);
                    foreach (var o in dots) _consumed.Add(o);
                    sym.Attributes["merged"] = all.Count.ToString();
                }
            }
            if (_opt.Verbose && clefMatches.Count > 0) sym.Attributes["clefscore"] = $"{clefMatches[0].Template.Name} {clefMatches[0].Score:0.00}";
            if (clefMatches.Count > 0 && clefMatches[0].Template.Name == "unpitchedPercussionClef1" && clefMatches[0].Score < 0.82) clefMatches.RemoveAt(0);
            if (clefMatches.Count > 0 && clefMatches[0].Score >= (atStart ? 0.52 : Threshold(SymbolKind.Clef)))
            {
                sym.Kind = SymbolKind.Clef; sym.Name = clefMatches[0].Template.Name; sym.Confidence = Math.Round(clefMatches[0].Score, 2);
                PostProcessTemplate(sym, c, staff, false);
                return sym;
            }
        }
        // 0b. whole / half rests: a solid bar hanging below or sitting on a staff line
        if (w >= 0.8 && w <= 2.0 && h >= 0.3 && h <= 0.8 && c.Fill > 0.75)
        {
            double stepTop = staff.StepAt(c.CenterX, c.Y0), stepBot = staff.StepAt(c.CenterX, c.Y1 + 1);
            bool topOnLine = Math.Abs(stepTop - Math.Round(stepTop)) < 0.3 && ((int)Math.Round(stepTop)) % 2 == 0 && stepTop >= -0.5 && stepTop <= 8.5;
            bool botOnLine = Math.Abs(stepBot - Math.Round(stepBot)) < 0.3 && ((int)Math.Round(stepBot)) % 2 == 0 && stepBot >= -0.5 && stepBot <= 8.5;
            if (topOnLine && !botOnLine) { sym.Kind = SymbolKind.Rest; sym.Name = "restWhole"; sym.Duration = "whole"; sym.Confidence = 0.7; return sym; }
            if (botOnLine && !topOnLine) { sym.Kind = SymbolKind.Rest; sym.Name = "restHalf"; sym.Duration = "half"; sym.Confidence = 0.7; return sym; }
        }

        // 1. stems -> note structures
        var stems = FindStems(c, staff);
        if (stems.Count > 0)
        {
            var noteSym = AnalyzeNoteGroup(c, staff, stems);
            if (noteSym != null) return noteSym;
        }

        // 2. template matching for isolated glyphs
        if (_lib != null && w <= 7 && h <= 9.5 && w >= 0.15 && h >= 0.15)
        {
            var mask = _cm.Mask(c);
            var matches = _lib.Match(mask, c.Width, c.Height, ss);
            if (matches.Count > 0)
            {
                var m = matches[0];
                if (m.Template.Kind == SymbolKind.Breath && c.CenterY > staff.TopY(c.CenterX) - 0.3 * ss && matches.Count > 1 && matches[1].Score >= m.Score - 0.08)
                    m = matches[1];
                double thr = Threshold(m.Template.Kind);
                if (m.Score >= thr && m.Template.Kind == SymbolKind.Dynamic)
                {
                    double cy = c.CenterY;
                    if (cy > staff.TopY(c.CenterX) - 0.5 * ss && cy < staff.BottomY(c.CenterX) + 0.5 * ss) thr = 0.85;
                }
                if (m.Score >= thr)
                {
                    sym.Kind = m.Template.Kind; sym.Name = m.Template.Name; sym.Confidence = Math.Round(m.Score, 2);
                    if (matches.Count > 1) sym.Attributes["alt"] = $"{matches[1].Template.Name} {matches[1].Score:0.00}";
                    PostProcessTemplate(sym, c, staff, stems.Count > 0);
                    return sym;
                }
                sym.Attributes["best"] = $"{m.Template.Name} {m.Score:0.00}";
            }
        }

        // 3. shape heuristics
        if (w >= 2.5 && h <= 0.5 && c.Fill > 0.6) { sym.Kind = SymbolKind.Ledger; sym.Name = "horizontal line"; sym.Confidence = 0.5; return sym; }
        if (w >= 1.2 && w <= 3.0 && h <= 0.35 && c.Fill > 0.6) { sym.Kind = SymbolKind.Ledger; sym.Name = "ledger line"; sym.Confidence = 0.6; return sym; }
        if (w >= 2.0 && c.Fill < 0.35 && w / Math.Max(h, 0.05) >= 2.0)
        {
            sym.Kind = SymbolKind.Slur; sym.Name = "slur/tie/hairpin"; sym.Confidence = 0.4;
            return sym;
        }
        if (h >= 0.5 && h <= 2.6 && w <= 2.5)
        {
            sym.Kind = SymbolKind.Text; sym.Name = "glyph"; sym.Confidence = 0.3;
            return sym;
        }
        sym.Description = $"{w:0.0}x{h:0.0}ss fill {c.Fill:0.00}";
        return sym;
    }

    private static double Threshold(SymbolKind k) => k switch
    {
        SymbolKind.Clef => 0.62,
        SymbolKind.TimeSignature => 0.66,
        SymbolKind.Rest => 0.64,
        SymbolKind.Accidental => 0.62,
        SymbolKind.Note => 0.70,
        SymbolKind.Dynamic => 0.72,
        SymbolKind.Dot => 0.7,
        SymbolKind.Flag => 0.75,
        _ => 0.66,
    };

    private void PostProcessTemplate(MusicSymbol sym, Component c, Staff staff, bool hadStems)
    {
        double ss = staff.Space;
        switch (sym.Kind)
        {
            case SymbolKind.Note:
            {
                // isolated note head (whole / half without stem / detached black head)
                var n = new Note { Head = sym.Bounds, HeadType = sym.Name switch { "noteheadWhole" => "whole", "noteheadHalf" => "half", "noteheadDoubleWhole" => "double whole", _ => "black" } };
                n.Step = (int)Math.Round(staff.StepAt(n.HeadX, n.HeadY));
                sym.Notes.Add(n);
                sym.Duration = n.HeadType switch { "whole" => "whole", "double whole" => "breve", "half" => "half?", _ => "quarter?" };
                sym.Name = "note";
                break;
            }
            case SymbolKind.Rest:
                sym.Duration = sym.Name switch
                {
                    "restWhole" => "whole", "restHalf" => "half", "restQuarter" => "quarter", "rest8th" => "8th", "rest16th" => "16th", "rest32nd" => "32nd", "rest64th" => "64th", _ => null
                };
                // whole vs half rest: whole hangs below a line, half sits on a line
                if (sym.Name is "restWhole" or "restHalf")
                {
                    double stepTop = staff.StepAt(sym.Bounds.CenterX, sym.Bounds.Y0), stepBot = staff.StepAt(sym.Bounds.CenterX, sym.Bounds.Y1);
                    bool topOnLine = Math.Abs(stepTop - Math.Round(stepTop)) < 0.3 && ((int)Math.Round(stepTop)) % 2 == 0;
                    bool botOnLine = Math.Abs(stepBot - Math.Round(stepBot)) < 0.3 && ((int)Math.Round(stepBot)) % 2 == 0;
                    if (topOnLine && !botOnLine) { sym.Name = "restWhole"; sym.Duration = "whole"; }
                    else if (botOnLine && !topOnLine) { sym.Name = "restHalf"; sym.Duration = "half"; }
                }
                break;
            case SymbolKind.Clef:
                sym.Attributes["clef"] = sym.Name switch { "gClef" => "treble", "fClef" => "bass", "cClef" => "alto/tenor", "unpitchedPercussionClef1" => "percussion", _ => "?" };
                if (sym.Name == "cClef")
                {
                    double stepMid = staff.StepAt(sym.Bounds.CenterX, sym.Bounds.CenterY);
                    sym.Attributes["clef"] = Math.Abs(stepMid - 4) <= 1.2 ? "alto" : Math.Abs(stepMid - 6) <= 1.2 ? "tenor" : "c-clef";
                }
                break;
            case SymbolKind.Accidental:
                sym.Step = (int)Math.Round(staff.StepAt(sym.Bounds.CenterX, AccidentalCenterY(sym)));
                break;
        }
    }

    private static double AccidentalCenterY(MusicSymbol s)
    {
        // flats sit higher: their "center" (the loop) is in the lower half of the bbox
        return s.Name is "accidentalFlat" or "accidentalDoubleFlat" ? s.Bounds.Y0 + s.Bounds.Height * 0.72 : s.Bounds.CenterY;
    }

    // ------------------------------------------------------------------ stems and note groups

    private sealed class Stem
    {
        public int X;           // representative column
        public int X0, X1;      // column range
        public int Top, Bottom; // vertical extent of the stem run
        public List<Note> Heads = new();
        public bool Up;
        public int Flags;
        public int Length => Bottom - Top + 1;
    }

    private List<Stem> FindStems(Component c, Staff staff)
    {
        double ss = staff.Space;
        int minLen = (int)Math.Round(2.2 * ss);
        var stems = new List<Stem>();
        if (c.Height < minLen) return stems;
        int label = c.Label;
        Stem? cur = null;
        for (int x = c.X0; x <= c.X1; x++)
        {
            int bestLen = 0, bestTop = 0, run = 0, runTop = 0;
            for (int y = c.Y0; y <= c.Y1 + 1; y++)
            {
                bool on = y <= c.Y1 && _cm.Labels[y * _cm.Width + x] == label;
                if (on) { if (run == 0) runTop = y; run++; }
                else { if (run > bestLen) { bestLen = run; bestTop = runTop; } run = 0; }
            }
            bool stemCol = bestLen >= minLen;
            if (stemCol)
            {
                int bestBottom = bestTop + bestLen - 1;
                int overlap = cur == null ? 0 : Math.Min(bestBottom, cur.Bottom) - Math.Max(bestTop, cur.Top);
                if (cur != null && x == cur.X1 + 1 && overlap >= 0.6 * Math.Min(bestLen, cur.Length))
                {
                    cur.X1 = x;
                    if (bestLen > cur.Length) cur.X = x;
                    cur.Top = Math.Min(cur.Top, bestTop); cur.Bottom = Math.Max(cur.Bottom, bestBottom);
                }
                else
                {
                    cur = new Stem { X = x, X0 = x, X1 = x, Top = bestTop, Bottom = bestTop + bestLen - 1 };
                    stems.Add(cur);
                }
            }
            else cur = null;
        }
        // keep thin candidates only
        double maxW = Math.Max(3, 0.45 * ss);
        return stems.Where(s => s.X1 - s.X0 + 1 <= maxW).ToList();
    }

    private MusicSymbol? AnalyzeNoteGroup(Component c, Staff staff, List<Stem> stems)
    {
        double ss = staff.Space;
        double headW = 1.18 * ss, headH = 1.0 * ss;
        foreach (var st in stems) FindHeads(st, staff, headW, headH);
        foreach (var st in stems) st.Heads.RemoveAll(h => Math.Abs(h.Step) > 20);
        var withHeads = stems.Where(s => s.Heads.Count > 0).ToList();
        if (withHeads.Count == 0) return null;

        // beams between consecutive stems?
        var beamed = new bool[withHeads.Count - 1 < 0 ? 0 : withHeads.Count - 1];
        for (int i = 0; i + 1 < withHeads.Count; i++) beamed[i] = HasBeamBetween(withHeads[i], withHeads[i + 1], c, staff);
        bool anyBeam = beamed.Any(b => b);

        foreach (var st in withHeads)
        {
            st.Flags = CountFlagsOrBeams(st, c, staff);
        }

        var sym = new MusicSymbol { Bounds = ToRect(c), Staff = staff, Confidence = 0.7 };
        if (_opt.Verbose) sym.Attributes["stems"] = string.Join(",", withHeads.Select(s => $"{s.X}:{s.Top}-{s.Bottom}{(s.Up ? "u" : "d")}f{s.Flags}h{s.Heads.Count}"));
        if (withHeads.Count == 1)
        {
            var st = withHeads[0];
            FillNoteSymbol(sym, st, staff);
            return sym;
        }
        // multiple stems: beamed group (or several touching notes)
        sym.Kind = SymbolKind.NoteGroup;
        sym.Name = anyBeam ? "beamed group" : "note cluster";
        foreach (var st in withHeads)
        {
            var child = new MusicSymbol { Bounds = new Rect(Math.Min(st.X0, st.Heads.Min(h => h.Head.X0)), Math.Min(st.Top, st.Heads.Min(h => h.Head.Y0)), Math.Max(st.X1, st.Heads.Max(h => h.Head.X1)), Math.Max(st.Bottom, st.Heads.Max(h => h.Head.Y1))), Staff = staff, Confidence = 0.7 };
            FillNoteSymbol(child, st, staff);
            sym.Children.Add(child);
            sym.Notes.AddRange(child.Notes);
        }
        return sym;
    }

    private static void FillNoteSymbol(MusicSymbol sym, Stem st, Staff staff)
    {
        sym.Attributes["stemx"] = $"{st.X0},{st.X1},{(st.Up ? st.Top : st.Bottom)}";
        sym.Kind = st.Heads.Count > 1 ? SymbolKind.Chord : SymbolKind.Note;
        sym.Name = st.Heads.Count > 1 ? "chord" : "note";
        sym.StemUp = st.Up;
        sym.Flags = st.Flags;
        sym.Notes.AddRange(st.Heads.OrderByDescending(h => h.Step));
        bool hollow = st.Heads.All(h => h.HeadType == "half");
        sym.Duration = hollow ? "half" : st.Flags switch { 0 => "quarter", 1 => "8th", 2 => "16th", 3 => "32nd", _ => "64th" };
        if (hollow && st.Flags > 0) sym.Attributes["note"] = "hollow head with flags/beams";
    }

    private void FindHeads(Stem st, Staff staff, double headW, double headH)
    {
        double ss = staff.Space;
        double rx = headW / 2, ry = headH / 2;
        // candidate centres: left side and right side of the stem
        var sides = new (int sign, double cx)[] { (-1, st.X0 - rx + 0.08 * ss), (+1, st.X1 + rx - 0.08 * ss) };
        var found = new List<(double score, int side, double cx, double cy, bool hollow)>();
        int yFrom = (int)(st.Top - 0.6 * ss), yTo = (int)(st.Bottom + 0.6 * ss);
        foreach (var (sign, cx) in sides)
        {
            var scores = new List<(int y, double score, bool hollow)>();
            for (int cy = yFrom; cy <= yTo; cy++)
            {
                var (score, hollow) = HeadScore(cx, cy, rx, ry, sign, st);
                scores.Add((cy, score, hollow));
            }
            // non-maximum suppression
            int minSep = (int)(0.85 * ss);
            var sorted = scores.Where(s => s.score >= 0.52).OrderByDescending(s => s.score).ToList();
            var taken = new List<int>();
            foreach (var s in sorted)
            {
                if (taken.Any(t => Math.Abs(t - s.y) < minSep)) continue;
                taken.Add(s.y);
                found.Add((s.score, sign, cx, s.y, s.hollow));
            }
        }
        // heads must sit near a stem end unless this is a chord; keep heads along the stem
        foreach (var f in found.OrderByDescending(f => f.score))
        {
            // reject duplicate (same y on both sides, pick better)
            if (st.Heads.Any(h => Math.Abs(h.HeadY - f.cy) < 0.4 * ss)) continue;
            var head = new Note
            {
                Head = new Rect((int)Math.Round(f.cx - rx), (int)Math.Round(f.cy - ry), (int)Math.Round(f.cx + rx), (int)Math.Round(f.cy + ry)),
                HeadType = f.hollow ? "half" : "black",
            };
            head.Step = (int)Math.Round(staff.StepAt(f.cx, f.cy));
            st.Heads.Add(head);
        }
        if (st.Heads.Count == 0) return;
        // stem direction: heads concentrated at the bottom -> up
        double stemMid = (st.Top + st.Bottom) / 2.0;
        double avgY = st.Heads.Average(h => h.HeadY);
        st.Up = avgY > stemMid;
        // sanity: heads must be within the stem's vertical extent (+ half a head)
        st.Heads.RemoveAll(h => h.HeadY < st.Top - 0.7 * ss || h.HeadY > st.Bottom + 0.7 * ss);
    }

    /// <summary>Score an elliptical note head hypothesis centred at (cx,cy). Returns (score, hollow).</summary>
    private (double score, bool hollow) HeadScore(double cx, double cy, double rx, double ry, int side, Stem st)
    {
        int inside = 0, insideInk = 0, core = 0, coreInk = 0, ring = 0, ringInk = 0;
        int x0 = (int)Math.Floor(cx - rx * 1.6), x1 = (int)Math.Ceiling(cx + rx * 1.6);
        int y0 = (int)Math.Floor(cy - ry * 1.6), y1 = (int)Math.Ceiling(cy + ry * 1.6);
        for (int y = y0; y <= y1; y++)
        {
            double dy = (y - cy) / ry;
            for (int x = x0; x <= x1; x++)
            {
                double dx = (x - cx) / rx;
                double r2 = dx * dx + dy * dy;
                bool ink = _ns[x, y];
                if (r2 <= 1.0)
                {
                    inside++; if (ink) insideInk++;
                    if (r2 <= 0.25) { core++; if (ink) coreInk++; }
                }
                else if (r2 <= 2.4)
                {
                    // ignore the stem columns; beyond the stem only a narrow band at head height counts (beams), and
                    // a thin horizontal band through the centre is ignored on the head side (ledger lines)
                    bool stemCol = x >= st.X0 - 1 && x <= st.X1 + 1;
                    if (stemCol) continue;
                    bool farSide = side < 0 ? x > st.X1 + 1 : x < st.X0 - 1;
                    if (farSide) { if (Math.Abs(y - cy) > ry * 0.35) continue; }
                    else if (Math.Abs(y - cy) <= ry * 0.22) continue;
                    ring++; if (ink) ringInk++;
                }
            }
        }
        if (inside == 0) return (0, false);
        double fill = insideInk / (double)inside;
        double ringFill = ring > 0 ? ringInk / (double)ring : 0;
        double coreFill = core > 0 ? coreInk / (double)core : fill;
        bool hollow = coreFill < 0.35 && fill >= 0.35 && fill <= 0.8;
        double score = hollow ? (fill - coreFill) + 0.35 : fill;
        score -= 0.9 * ringFill;
        return (score, hollow);
    }

    private bool HasBeamBetween(Stem a, Stem b, Component c, Staff staff)
    {
        double ss = staff.Space;
        int xm = (a.X + b.X) / 2;
        if (b.X - a.X < 2) return false;
        // look for a thick band (>= 0.3 ss) of component ink at the mid column
        int label = c.Label;
        int run = 0;
        for (int y = c.Y0; y <= c.Y1 + 1; y++)
        {
            bool on = y <= c.Y1 && _cm.Labels[y * _cm.Width + xm] == label;
            if (on) run++;
            else { if (run >= 0.3 * ss && run <= 2.5 * ss) return true; run = 0; }
        }
        return false;
    }

    private int CountFlagsOrBeams(Stem st, Component c, Staff staff)
    {
        double ss = staff.Space;
        int label = c.Label;
        // the free end of the stem is opposite to the heads
        int yEnd = st.Up ? st.Top : st.Bottom;
        int best = 0;
        foreach (double off in new[] { 0.25, 0.45, 0.7, -0.25, -0.45, -0.7 })
        {
            int dx = (int)Math.Round(off * ss);
            int x = (dx > 0 ? st.X1 : st.X0) + dx;
            if (x < c.X0 || x > c.X1) continue;
            int bands = 0, run = 0;
            int yFrom = st.Up ? yEnd - (int)(0.3 * ss) : yEnd - (int)(2.6 * ss);
            int yTo = st.Up ? yEnd + (int)(2.6 * ss) : yEnd + (int)(0.3 * ss);
            // stop when reaching the head area
            if (st.Up) yTo = Math.Min(yTo, (int)(st.Heads.Min(h => h.HeadY) - 0.8 * ss));
            else yFrom = Math.Max(yFrom, (int)(st.Heads.Max(h => h.HeadY) + 0.8 * ss));
            for (int y = yFrom; y <= yTo + 1; y++)
            {
                bool on = y >= c.Y0 && y <= c.Y1 && _cm.Labels[y * _cm.Width + x] == label;
                if (on) run++;
                else { if (run >= Math.Max(2, 0.2 * ss)) bands++; run = 0; }
            }
            if (bands > best) best = bands;
        }
        return Math.Min(best, 4);
    }

    // ------------------------------------------------------------------ post-processing

    private void MergeText(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        var glyphs = syms.Where(s => s.Kind == SymbolKind.Text || (s.Kind == SymbolKind.Unknown && s.Bounds.Height <= 2.6 * ss && s.Bounds.Width <= 2.5 * ss)).OrderBy(s => s.Bounds.X0).ToList();
        var used = new HashSet<MusicSymbol>();
        foreach (var g in glyphs)
        {
            if (used.Contains(g)) continue;
            var run = new List<MusicSymbol> { g };
            used.Add(g);
            var last = g;
            foreach (var o in glyphs)
            {
                if (used.Contains(o) || o.Bounds.X0 < last.Bounds.X0) continue;
                double gap = o.Bounds.X0 - last.Bounds.X1;
                int yo = Math.Min(o.Bounds.Y1, last.Bounds.Y1) - Math.Max(o.Bounds.Y0, last.Bounds.Y0);
                if (gap <= 0.7 * ss && gap >= -0.3 * ss && yo > 0)
                {
                    run.Add(o); used.Add(o); last = o;
                }
            }
            if (run.Count >= 2)
            {
                var r = run[0].Bounds;
                foreach (var o in run.Skip(1)) r = r.Union(o.Bounds);
                var t = new MusicSymbol { Kind = SymbolKind.Text, Name = "text", Bounds = r, Staff = staff, Confidence = 0.4 };
                t.Attributes["glyphs"] = run.Count.ToString();
                foreach (var o in run) syms.Remove(o);
                syms.Add(t);
            }
        }
    }

    private void AttachDotsAndAccidentals(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        var notes = syms.Where(s => s.Kind is SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup).SelectMany(s => s.Notes.Select(n => (sym: s, note: n))).ToList();
        var dots = syms.Where(s => s.Kind == SymbolKind.Dot).ToList();
        foreach (var d in dots)
        {
            // augmentation dot: right of a head, roughly at head height (in a space)
            var cand = notes.Where(p => d.Bounds.CenterX > p.note.Head.X1 + 0.05 * ss && d.Bounds.CenterX < p.note.Head.X1 + 1.4 * ss && Math.Abs(d.Bounds.CenterY - p.note.HeadY) <= 0.75 * ss)
                            .OrderBy(p => Math.Abs(d.Bounds.CenterX - p.note.Head.X1)).FirstOrDefault();
            if (cand.note != null)
            {
                cand.note.Dots++;
                cand.sym.Dots = Math.Max(cand.sym.Dots, cand.note.Dots);
                syms.Remove(d);
                continue;
            }
            // staccato: above or below a head
            var stac = notes.Where(p => Math.Abs(d.Bounds.CenterX - p.note.HeadX) <= 0.5 * ss && Math.Abs(d.Bounds.CenterY - p.note.HeadY) >= 0.6 * ss && Math.Abs(d.Bounds.CenterY - p.note.HeadY) <= 2.2 * ss)
                            .OrderBy(p => Math.Abs(d.Bounds.CenterY - p.note.HeadY)).FirstOrDefault();
            if (stac.note != null)
            {
                stac.sym.Attributes["articulation"] = "staccato";
                syms.Remove(d);
            }
        }
        var accs = syms.Where(s => s.Kind == SymbolKind.Accidental).ToList();
        foreach (var a in accs)
        {
            double ay = AccidentalCenterY(a);
            var cand = notes.Where(p => a.Bounds.X1 < p.note.Head.X0 + 0.15 * ss && a.Bounds.X1 > p.note.Head.X0 - 1.6 * ss && Math.Abs(ay - p.note.HeadY) <= 0.45 * ss)
                            .OrderBy(p => p.note.Head.X0 - a.Bounds.X1).FirstOrDefault();
            if (cand.note != null)
            {
                cand.note.AccidentalName = a.Name;
                cand.note.Accidental = a.Name switch { "accidentalSharp" => 1, "accidentalFlat" => -1, "accidentalNatural" => 0, "accidentalDoubleSharp" => 2, "accidentalDoubleFlat" => -2, _ => int.MinValue };
                syms.Remove(a);
            }
        }
    }

    private void DetectHairpinsAndSlurs(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        foreach (var s in syms.Where(s => s.Kind == SymbolKind.Slur).ToList())
        {
            var r = s.Bounds;
            // measure ink height at the left and right ends
            int lh = InkHeight(r.X0 + 1, r), rh = InkHeight(r.X1 - 1, r);
            int mh = InkHeight((r.X0 + r.X1) / 2, r);
            if (r.Height >= 0.6 * ss && ((lh <= 0.25 * ss && rh >= 0.5 * ss) || (rh <= 0.25 * ss && lh >= 0.5 * ss)) && mh < r.Height)
            {
                s.Kind = SymbolKind.Hairpin; s.Name = lh < rh ? "crescendo hairpin" : "diminuendo hairpin"; s.Confidence = 0.6;
            }
            else { s.Name = "slur/tie"; s.Confidence = 0.5; }
            s.Attributes["width"] = $"{r.Width / ss:0.0}ss";
        }
    }

    private int InkHeight(int x, Rect r)
    {
        int top = -1, bot = -1;
        for (int y = r.Y0; y <= r.Y1; y++) if (_ns[x, y]) { if (top < 0) top = y; bot = y; }
        return top < 0 ? 0 : bot - top + 1;
    }
}
