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
    private readonly GlyphClassifier? _clf;
    private readonly GlyphClassifier? _heads;

    public SymbolAnalyzer(GlyphLibrary? lib, BinaryImage bin, BinaryImage noStaff, ComponentMap cm, PageAnalysis page, AnalysisOptions opt, GlyphClassifier? classifier = null, GlyphClassifier? headVerifier = null)
    {
        _lib = lib; _bin = bin; _ns = noStaff; _cm = cm; _page = page; _opt = opt; _clf = classifier; _heads = headVerifier;
    }

    /// <summary>Clef decision for a mask: the classifier when available (probability mass of the clef classes), else templates.</summary>
    private (string? name, double score) ClefFor(byte[] mask, int w, int h, double ss)
    {
        if (_clf != null)
        {
            var p = _clf.Classify(mask, w, h, ss);
            double pg = Prob(p, "clef_g"), pf = Prob(p, "clef_f"), pc = Prob(p, "clef_c");
            double best = Math.Max(pg, Math.Max(pf, pc));
            if (pg + pf + pc < 0.35) return (null, best);
            return (best == pg ? "gClef" : best == pf ? "fClef" : "cClef", best);
        }
        if (_lib == null) return (null, 0);
        var m = _lib.Match(mask, w, h, ss, 0.35, t => t.Kind == SymbolKind.Clef);
        return m.Count == 0 ? (null, 0) : (m[0].Template.Name, m[0].Score);
    }

    private static double Prob(GlyphClassifier.Prediction p, string label)
    {
        for (int i = 0; i < p.Probabilities.Length; i++) if (i < Eval.CropExtractor.Labels.Length && Eval.CropExtractor.Labels[i] == label) return p.Probabilities[i];
        return 0;
    }

    /// <summary>Turns a classifier prediction into a symbol. Returns false for fragments/composites/other (caller falls back to heuristics).</summary>
    private bool ApplyPrediction(MusicSymbol sym, Component c, Staff staff, GlyphClassifier.Prediction p)
    {
        double ss = staff.Space;
        double h = c.Height / ss;
        string label = p.Label; double prob = p.Probability;
        bool barRest = label is "rest_whole" or "rest_half" && prob >= 0.3 && WholeHalfRestPlausible(c, staff) && c.Fill >= 0.85 && c.Height <= 0.9 * ss && RestOnLine(c, staff) != null;
        if (prob < 0.5 && !barRest) return false;
        sym.Confidence = Math.Round(prob, 2);
        if (_opt.Verbose) sym.Attributes["cnn"] = $"{p.Label} {p.Probability:0.00} / {p.Second} {p.SecondProbability:0.00}";
        switch (label)
        {
            case "head_black": case "head_half": case "head_whole":
                if (prob < 0.6) return false;
                // head sizes: a whole note is wider but never taller than about 1.3 spaces
                if (label == "head_whole" ? h > 1.45 || c.Width > 2.1 * ss || c.Width < 1.0 * ss : h > 1.6 || c.Width > 2.0 * ss) return false;
                // a head has a head's size: a sliver (the end of a slur) is none
                if (h < 0.6 || c.Width < 0.8 * ss) return false;
                // a hollow head on its own (its stem broke away) must be a sure verdict: slur pieces look much like it
                if (label == "head_half" && p.Probability < 0.85) return false;
                // an isolated head well outside the staff must sit on a ledger line (which is part of its component); letters are not
                {
                    double stepHere = staff.StepAt(c.CenterX, c.CenterY);
                    if ((stepHere < -2.5 || stepHere > 10.5) && c.Width < 1.45 * ss && _ns.HorizontalRunAt((int)c.CenterX, (int)Math.Round(c.CenterY), out _) < 1.4 * ss) return false;
                }
                sym.Kind = SymbolKind.Note; sym.Name = label == "head_black" ? "noteheadBlack" : label == "head_half" ? "noteheadHalf" : "noteheadWhole";
                PostProcessTemplate(sym, c, staff, false);
                return true;
            case "rest_whole": case "rest_half": case "rest_quarter": case "rest_8th": case "rest_16th":
            {
                // rests live inside the staff; whole/half rests are solid bars of at least 0.8 x 0.3 staff spaces
                double cy = c.CenterY, top = staff.TopY(c.CenterX), bot = staff.BottomY(c.CenterX);
                if (cy < top - 1.0 * ss || cy > bot + 1.0 * ss) return false;
                // whole and half rests are solid bars inside the staff; ledger-line remnants below or above it are not rests
                if (label is "rest_whole" or "rest_half" && !WholeHalfRestPlausible(c, staff)) return false;
                // flagged rests are tall: an 8th rest spans two spaces, a quarter rest nearly three
                if (label is "rest_8th" or "rest_16th" && (h < 1.4 || h > 3.4)) return false;
                if (label == "rest_quarter" && (h < 2.0 || h > 3.8)) return false;
                if (prob < 0.6 && !barRest) return false;
                if (barRest) label = RestOnLine(c, staff)!;
                sym.Kind = SymbolKind.Rest; sym.Name = label switch { "rest_whole" => "restWhole", "rest_half" => "restHalf", "rest_quarter" => "restQuarter", "rest_8th" => "rest8th", _ => "rest16th" };
                PostProcessTemplate(sym, c, staff, false);
                return true;
            }
            case "clef_g": case "clef_f": case "clef_c":
                sym.Kind = SymbolKind.Clef; sym.Name = label == "clef_g" ? "gClef" : label == "clef_f" ? "fClef" : "cClef";
                PostProcessTemplate(sym, c, staff, false);
                return true;
            case "acc_sharp": case "acc_flat": case "acc_natural":
                sym.Kind = SymbolKind.Accidental; sym.Name = label == "acc_sharp" ? "accidentalSharp" : label == "acc_flat" ? "accidentalFlat" : "accidentalNatural";
                PostProcessTemplate(sym, c, staff, false);
                return true;
            case "timesig_common":
                sym.Kind = SymbolKind.TimeSignature; sym.Name = "timeSigCommon";
                return true;
            case "digit":
            {
                if (h < 1.7 && TryTupletDigit(sym, c, staff)) return true;
                sym.Kind = SymbolKind.TimeSignature; sym.Name = "timeSig?";
                if (_lib != null)
                {
                    var m = _lib.Match(_cm.Mask(c), c.Width, c.Height, ss, 0.45, t => t.Kind == SymbolKind.TimeSignature && t.Name.Length == 8);
                    if (m.Count > 0) sym.Name = m[0].Template.Name;
                }
                return true;
            }
            case "dot":
                if (OnStaffLine(c, staff)) return false;   // augmentation and staccato dots sit in spaces; a blob on a line is a line remnant
                sym.Kind = SymbolKind.Dot; sym.Name = "dot";
                return true;
            case "dynamic":
            {
                sym.Kind = SymbolKind.Dynamic; sym.Name = "dynamic";
                if (_lib != null)
                {
                    var m = _lib.Match(_cm.Mask(c), c.Width, c.Height, ss, 0.4, t => t.Kind == SymbolKind.Dynamic);
                    if (m.Count > 0 && m[0].Score >= 0.5) sym.Name = m[0].Template.Name;
                }
                return true;
            }
            case "text":
                if (TryTupletDigit(sym, c, staff)) return true;
                sym.Kind = SymbolKind.Text; sym.Name = "glyph"; return true;
            case "slur": sym.Kind = SymbolKind.Slur; sym.Name = "slur/tie/hairpin"; return true;
            case "line": sym.Kind = SymbolKind.Ledger; sym.Name = c.Width > c.Height ? "ledger line" : "vertical line"; return true;
            case "flag": sym.Kind = SymbolKind.Flag; sym.Name = "flag"; return true;
            case "beam": sym.Kind = SymbolKind.Beam; sym.Name = "beam"; return true;
            default: return false;   // fragment, composite, other
        }
    }

    /// <summary>True when the component's centre lies within a fifth of a space of a staff line (inside the staff).</summary>
    private static bool OnStaffLine(Component c, Staff staff)
    {
        double step = staff.StepAt(c.CenterX, c.CenterY);
        if (step < -0.5 || step > 8.5) return false;
        double r = Math.Round(step);
        return ((int)r) % 2 == 0 && Math.Abs(step - r) <= 0.4;
    }

    /// <summary>"rest_whole" when the bar hangs from a staff line, "rest_half" when it sits on one, else null.</summary>
    private static string? RestOnLine(Component c, Staff staff)
    {
        double stepTop = staff.StepAt(c.CenterX, c.Y0), stepBot = staff.StepAt(c.CenterX, c.Y1 + 1);
        bool topOnLine = Math.Abs(stepTop - Math.Round(stepTop)) < 0.3 && ((int)Math.Round(stepTop)) % 2 == 0 && stepTop >= -0.5 && stepTop <= 8.5;
        bool botOnLine = Math.Abs(stepBot - Math.Round(stepBot)) < 0.3 && ((int)Math.Round(stepBot)) % 2 == 0 && stepBot >= -0.5 && stepBot <= 8.5;
        if (topOnLine && !botOnLine) return "rest_whole";
        if (botOnLine && !topOnLine) return "rest_half";
        return null;
    }

    /// <summary>Whole and half rests are solid bars at least 0.8 x 0.4 staff spaces inside the staff; slur pieces sliced by the staff lines and ledger-line remnants are thinner or outside.</summary>
    private static bool WholeHalfRestPlausible(Component c, Staff staff)
    {
        double ss = staff.Space, cy = c.CenterY;
        return c.Width >= 0.8 * ss && c.Height >= 0.4 * ss && c.Fill >= 0.7 && cy >= staff.TopY(c.CenterX) - 0.2 * ss && cy <= staff.BottomY(c.CenterX) + 0.2 * ss;
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
        foreach (var staff in staves) ReuniteAcrossLines(compsPerStaff[staff], staff);
        ReadBarNumberDigits();
        _page.Notes.Add($"components: {_cm.Components.Count}, noise {noise}, outside staves {outside}");

        foreach (var staff in staves)
        {
            var comps = compsPerStaff[staff];
            var syms = perStaff[staff];
            var consumed = GroupTextLines(comps, staff, syms);
            DetectClefAtStart(comps, staff, syms, consumed);
            // template placement is for small and medium staves (large glyphs survive staff removal, and the cost grows
            // with the square of the staff space; spurious "staves" on text pages have spacings of 100+ px)
            if (staff.Space <= MaxPlacementSpace)
            {
                if (!syms.Any(s => s.Kind == SymbolKind.Clef)) DetectClefByTemplate(comps, staff, syms, consumed);
                DetectKeySignature(comps, staff, syms, consumed);
                DetectTimeSignature(comps, staff, syms, consumed);
                DetectKeyChanges(comps, staff, syms, consumed);
                DetectTimeChanges(comps, staff, syms, consumed);
            }
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
            // a clef change is at least a space wide: a narrow "clef" inside the staff is a flat or natural (left to the
            // accidental passes); at the start a clef may be found by its thick stroke alone
            // (the solid thick bar of a C clef is mostly ink; a flat or natural is not)
            foreach (var c in syms.Where(s => s.Kind == SymbolKind.Clef && s.Bounds.Width < 0.8 * staff.Space && s.Bounds.X0 > staff.Left + 4 * staff.Space && InkFill(s.Bounds) < 0.7)) { c.Kind = SymbolKind.Unknown; c.Name = "unknown"; }
            MergeText(syms, staff);
            MergeDynamics(syms, staff);
            GroupDottedLines(syms, staff);
            MergeDuplicateRests(syms, staff);
            LooseTupletThrees(syms, staff);
            AttachTuplets(syms, staff);
            AttachDetachedStems(syms, staff);
            AttachDetachedHeads(syms, staff);
            RemoveDuplicateHeads(syms, staff);
            ChordHeadsOfOtherVoice(syms, staff);
            MergeBrokenStemChords(syms, staff);
            AttachDetachedFlags(syms, staff);
            ContinueBeams(syms, staff);
            LinkNotesUnderBeamEnds(syms, staff);
            LinkFragmentsUnderBeams(syms, staff);
            DropOverlappingHeads(syms, staff);
            DropSymbolsOnHeads(syms, staff);
            AccidentalsInUnknowns(syms, staff);
            DoubleSharpsReadAsText(syms, staff);
            TrillsAboveStaff(syms, staff);
            DropNotesWithoutHeads(syms);
            BarlinesReadAsStems(syms, staff);
            // nothing is written left of the staff lines but the brace or bracket, whose tip can look like a head on a stem
            syms.RemoveAll(s => s.Kind is SymbolKind.Note or SymbolKind.Chord && s.Notes.Count > 0 && s.Notes.All(n => n.HeadX < staff.Left));
            ResolveLoneBeamlets(syms, staff);
            FlagFragments(syms, staff);
            HeadsAfterAccidentals(syms, staff);
            HeadsOnLooseStems(syms, staff);
            DigitsReadAsHeads(syms, staff);
            // a note clear of the staff stands on ledger lines, the first one a space off the staff; without it the
            // "note" is handwriting or text (a chord with heads that far out on its own is a stemless one)
            syms.RemoveAll(s => (s.Kind is SymbolKind.Note or SymbolKind.NoteGroup || s.Kind == SymbolKind.Chord && s.StemUp == null) && s.Notes.Count > 0
                                && s.Notes.All(n => (n.Step < -1 || n.Step > 9)
                                                    && !HasLedgerLine(n.HeadX, staff.BottomY(n.HeadX) - (n.Step > 9 ? 10 : -2) * staff.Space / 2, staff.Space)));
            EighthRestsFromPieces(syms, staff);
            QuarterRestsFromPieces(syms, staff);
            QuarterRestsByShape(syms, staff);
            SixteenthRestsFromPieces(syms, staff);
            AccidentalsReadAsHalfNotes(syms, staff);
            StackedWholeNotes(syms, staff);
            AttachDotsAndAccidentals(syms, staff);
            AccidentalsInInk(syms, staff);
            DropRestsInAccidentals(syms, staff);
            // a "half or whole rest" on or close beside a note head (1.2 ss) is a piece of a tie or ledger line at the chord
            {
                var hd = syms.Where(s => s.Kind is SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup).SelectMany(s => s.Notes.Concat(s.Children.SelectMany(c => c.Notes))).ToList();
                syms.RemoveAll(r => r.Kind == SymbolKind.Rest && r.Duration is "half" or "whole" && r.Name != "multiRest"
                                    && hd.Any(n => n.Head.X0 < r.Bounds.X1 + 1.2 * staff.Space && n.Head.X1 > r.Bounds.X0 - 1.2 * staff.Space && Math.Abs(n.HeadY - r.Bounds.CenterY) <= 0.6 * staff.Space));
            }
            // an unsure short rest floating a space clear of the staff: strings and dots of a chord diagram, text
            syms.RemoveAll(r => r.Kind == SymbolKind.Rest && r.Duration is "quarter" or "8th" or "16th" && r.Confidence < 0.8
                                && (r.Bounds.Y1 < staff.TopY(r.Bounds.CenterX) - staff.Space || r.Bounds.Y0 > staff.BottomY(r.Bounds.CenterX) + staff.Space));
            TiesReadAsHeads(syms, staff);
            DetectHairpinsAndSlurs(syms, staff);
            MarkGraceNotes(syms, staff);
            DropNotesBeforeClef(syms, staff);
            HeadStepsFromInk(syms, staff);
            foreach (var s in syms.OrderBy(s => s.Bounds.X0))
            {
                // a beam carried across a bar line: each measure gets the stems on its side
                if (s.Kind == SymbolKind.NoteGroup && s.Children.Count >= 2)
                {
                    var parts = s.Children.Where(c => c.Notes.Count > 0).GroupBy(c => FindMeasure(staff, c)).Where(g => g.Key != null).ToList();
                    if (parts.Count > 1)
                    {
                        foreach (var g in parts)
                        {
                            var kids = g.ToList();
                            var part = new MusicSymbol { Kind = SymbolKind.NoteGroup, Name = s.Name, Staff = staff, Confidence = s.Confidence, StemUp = s.StemUp, Flags = s.Flags,
                                                         Duration = s.Duration, Bounds = kids.Skip(1).Aggregate(kids[0].Bounds, (a, c) => a.Union(c.Bounds)) };
                            foreach (var kv in s.Attributes) part.Attributes[kv.Key] = kv.Value;
                            part.Attributes["beam"] = "across a bar line";
                            part.Children.AddRange(kids);
                            part.Notes.AddRange(kids.SelectMany(c => c.Notes));
                            g.Key!.Symbols.Add(part);
                        }
                        continue;
                    }
                }
                var m = FindMeasure(staff, s);
                if (m != null) m.Symbols.Add(s);
            }
        }
    }

    /// <summary>
    /// Thin outlines of hollow heads break at binarisation and staff removal cuts heads that sit on a line, leaving
    /// arcs, dots and halves. Clusters of small touching/near components whose union has the size and silhouette
    /// of a note head are reunited into one component.
    /// </summary>
    private void ReuniteAcrossLines(List<Component> comps, Staff staff)
    {
        double ss = staff.Space;
        int thick = (int)Math.Ceiling(staff.LineThickness);
        var small = comps.Where(c => c.PixelCount > 0 && c.Width <= 1.3 * ss && c.Height <= 1.3 * ss && (c.Width >= 0.15 * ss || c.Height >= 0.15 * ss)).OrderBy(c => c.X0).ToList();
        var done = new HashSet<Component>();
        // whole-note halves: two thin arcs of head height facing each other, the thin top/bottom strokes gone
        foreach (var a in small)
        {
            if (a.PixelCount == 0 || done.Contains(a) || a.Width > 0.7 * ss || a.Height < 0.8 * ss) continue;
            var b = small.FirstOrDefault(o => o != a && o.PixelCount > 0 && !done.Contains(o) && o.Width <= 0.7 * ss && o.Height >= 0.8 * ss
                                               && o.X0 > a.X1 && o.X0 - a.X1 <= 0.75 * ss
                                               && Math.Min(a.Y1, o.Y1) - Math.Max(a.Y0, o.Y0) >= 0.8 * Math.Min(a.Height, o.Height));
            if (b == null) continue;
            int uw = b.X1 - a.X0 + 1;
            if (uw < 1.1 * ss || uw > 2.0 * ss) continue;
            _cm.Merge(a, b); comps.Remove(b); done.Add(a); done.Add(b);
        }
        foreach (var seed in small)
        {
            if (done.Contains(seed) || seed.PixelCount == 0) continue;
            var cluster = new List<Component> { seed };
            int x0 = seed.X0, y0 = seed.Y0, x1 = seed.X1, y1 = seed.Y1;
            bool grown = true;
            while (grown)
            {
                grown = false;
                foreach (var b in small)
                {
                    if (b.PixelCount == 0 || done.Contains(b) || cluster.Contains(b)) continue;
                    if (b.X0 > x1 + 0.45 * ss || b.X1 < x0 - 0.45 * ss) continue;
                    if (b.Y0 > y1 + thick + 2 || b.Y1 < y0 - thick - 2) continue;
                    int nx0 = Math.Min(x0, b.X0), ny0 = Math.Min(y0, b.Y0), nx1 = Math.Max(x1, b.X1), ny1 = Math.Max(y1, b.Y1);
                    if (nx1 - nx0 + 1 > 2.0 * ss || ny1 - ny0 + 1 > 1.4 * ss) continue;
                    cluster.Add(b); x0 = nx0; y0 = ny0; x1 = nx1; y1 = ny1; grown = true;
                }
            }
            if (cluster.Count < 2) continue;
            int uw = x1 - x0 + 1, uh = y1 - y0 + 1;
            if (uw < 0.9 * ss || uw > 2.0 * ss || uh < 0.7 * ss || uh > 1.4 * ss) continue;
            if (!EllipseLike(cluster, x0, y0, uw, uh)) continue;
            // two filled blobs side by side are neighbouring heads, not fragments of one head
            if (uw > 1.3 * ss && cluster.Count(p => p.Fill > 0.6 && p.Width >= 0.45 * ss && p.Height >= 0.6 * ss) >= 2) continue;
            foreach (var b in cluster.Skip(1)) { _cm.Merge(seed, b); comps.Remove(b); done.Add(b); }
            done.Add(seed);
        }
    }

    /// <summary>Silhouette test for a union of fragments: ink concentrates inside the inscribed ellipse, little in the corners.</summary>
    private bool EllipseLike(List<Component> parts, int x0, int y0, int w, int h)
    {
        double cx = x0 + (w - 1) / 2.0, cy = y0 + (h - 1) / 2.0, rx = w / 2.0, ry = h / 2.0;
        int inside = 0, insideInk = 0, corner = 0, cornerInk = 0;
        var labels = parts.Select(p => p.Label).ToHashSet();
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++)
            {
                double dx = (x - cx) / rx, dy = (y - cy) / ry;
                bool ink = labels.Contains(_cm.Labels[y * _cm.Width + x]);
                if (dx * dx + dy * dy <= 1.0) { inside++; if (ink) insideInk++; }
                else { corner++; if (ink) cornerInk++; }
            }
        double fi = inside == 0 ? 0 : insideInk / (double)inside, fc = corner == 0 ? 0 : cornerInk / (double)corner;
        return fi >= 0.18 && fc <= 0.3 && fi > 1.5 * fc;
    }

    /// <summary>
    /// The clef at the start of a staff is often broken into several components (C clef bars and curls, F clef dots,
    /// thin strokes in old engravings). Union all fragments in the clef zone and match the union against the clef templates.
    /// </summary>
    private void DetectClefAtStart(List<Component> comps, Staff staff, List<MusicSymbol> syms, HashSet<Component> consumed)
    {
        if (_lib == null && _clf == null) return;
        double ss = staff.Space;
        double xm = staff.Left + 1.5 * ss;
        double top = staff.TopY(xm), bot = staff.BottomY(xm);
        var zone = comps.Where(c => !consumed.Contains(c) && c.X0 >= staff.Left - 1.0 * ss && c.X0 < staff.Left + 2.8 * ss
                                    && c.Y1 > top - 2.5 * ss && c.Y0 < bot + 2.5 * ss
                                    && !(c.Width > 1.2 * ss && c.Height < 0.5 * ss) && c.PixelCount >= 0.02 * ss * ss)
                        .OrderBy(c => c.X0).ToList();
        if (zone.Count == 0) return;
        string? bestName = null; double bestScore = 0; int bestK = 0, bestSkip = 0;
        // leading small components (bracket bits, speckles) may precede the clef: try skipping up to two of them
        for (int skip = 0; skip <= Math.Min(3, zone.Count - 1); skip++)
        for (int k = zone.Count - skip; k >= 1; k--)
        {
            if (skip > 0 && zone.Take(skip).Any(c => c.Width > 1.5 * ss || c.Height > 1.5 * ss)) break;
            var parts = zone.Skip(skip).Take(k).ToList();
            int x0 = parts.Min(c => c.X0), y0 = parts.Min(c => c.Y0), x1 = parts.Max(c => c.X1), y1 = parts.Max(c => c.Y1);
            int w = x1 - x0 + 1, h = y1 - y0 + 1;
            if (h < 2.4 * ss || h > 9.5 * ss || w < 0.9 * ss || w > 5 * ss) continue;
            var mask = new byte[w * h];
            foreach (var c in parts)
                for (int y = c.Y0; y <= c.Y1; y++)
                    for (int x = c.X0; x <= c.X1; x++)
                        if (_cm.Labels[y * _cm.Width + x] == c.Label) mask[(y - y0) * w + (x - x0)] = 1;
            var (name, score) = ClefFor(mask, w, h, ss);
            if (name != null && score > bestScore) { bestName = name; bestScore = score; bestK = k; bestSkip = skip; }
        }
        if (bestName == null || bestScore < 0.55) return;
        var used = zone.Skip(bestSkip).Take(bestK).ToList();
        // a C clef matched on its bars alone (the curls broken off by staff removal): the pieces within the glyph's
        // width that lie inside the staff are its curls, not the digits of a time signature
        if (bestName == "cClef")
        {
            int ux0 = used.Min(c => c.X0), ux1 = used.Max(c => c.X1);
            double glyphW = _lib?.Templates.Where(t => t.Name == "cClef" && !HandwrittenFonts.Contains(t.Font)).Select(t => t.WidthSS).DefaultIfEmpty(2.6).Average() ?? 2.6;
            if (ux1 - ux0 + 1 < 0.8 * glyphW * ss)
                used.AddRange(comps.Where(c => !consumed.Contains(c) && !used.Contains(c) && c.X0 > ux0 && c.X0 <= ux0 + 0.9 * glyphW * ss && c.X1 <= ux0 + 1.15 * glyphW * ss
                                              && c.Y0 >= top - 0.6 * ss && c.Y1 <= bot + 0.6 * ss));
        }
        var sym = new MusicSymbol
        {
            Kind = SymbolKind.Clef, Name = bestName, Staff = staff, Confidence = Math.Round(bestScore, 2),
            Bounds = new Rect(used.Min(c => c.X0), used.Min(c => c.Y0), used.Max(c => c.X1), used.Max(c => c.Y1)),
        };
        if (used.Count > 1) sym.Attributes["merged"] = used.Count.ToString();
        var fake = new Component { X0 = sym.Bounds.X0, Y0 = sym.Bounds.Y0, X1 = sym.Bounds.X1, Y1 = sym.Bounds.Y1, PixelCount = used.Sum(c => c.PixelCount) };
        PostProcessTemplate(sym, fake, staff, false);
        syms.Add(sym);
        foreach (var c in used) consumed.Add(c);
    }

    // steps (0 = bottom line) of the n-th sharp / flat of a key signature in the treble clef; other clefs are offsets of these
    private static readonly int[] SharpStepsTreble = { 8, 5, 9, 6, 3, 7, 4 };
    private static readonly int[] FlatStepsTreble = { 4, 7, 3, 6, 2, 5, 1 };

    private static int? ExpectedKeyStep(string? clefKind, bool sharp, int n)
    {
        if (n < 0 || n > 6) return null;
        int t = sharp ? SharpStepsTreble[n] : FlatStepsTreble[n];
        switch (clefKind)
        {
            case "treble": return t;
            case "bass": return t - 2;
            case "alto": return t - 1;
            case "tenor": { int v = t + 1; return v > 8 ? v - 7 : v; }
            default: return null;
        }
    }

    private static string? ClefKindName(MusicSymbol clef) => clef.Name switch
    {
        "gClef" or "gClef8vb" or "gClef8va" => "treble",
        "fClef" or "fClef8vb" => "bass",
        "cClef" => clef.Attributes.TryGetValue("clef", out var k) && k is "alto" or "tenor" ? k : null,
        _ => null,
    };

    private const double MaxPlacementSpace = 40;
    private static readonly bool KeyDump = Environment.GetEnvironmentVariable("PVOMR_KEYDUMP") == "1";
    private static readonly bool KeyScan = Environment.GetEnvironmentVariable("PVOMR_KEYSCAN") == "1";

    /// <summary>A glyph template resampled to w x h pixels (a pixel is ink when at least half of the covered 32x32 cells are).</summary>
    private static byte[] RenderTemplate(GlyphTemplate t, int w, int h)
    {
        int n = GlyphTemplate.N;
        var m = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int j0 = y * n / h, j1 = Math.Max(j0 + 1, (y + 1) * n / h);
            for (int x = 0; x < w; x++)
            {
                int i0 = x * n / w, i1 = Math.Max(i0 + 1, (x + 1) * n / w);
                int tot = 0, ink = 0;
                for (int j = j0; j < j1 && j < n; j++)
                    for (int i = i0; i < i1 && i < n; i++) { tot++; int b = j * n + i; if ((t.Bits[b >> 6] & (1UL << (b & 63))) != 0) ink++; }
                if (tot > 0 && ink * 2 >= tot) m[y * w + x] = 1;
            }
        }
        return m;
    }

    /// <summary>
    /// Tolerant overlap between a rendered template placed at (px, py) and the original binary image, ignoring the
    /// rows covered by the staff lines (staff removal shreds small glyphs, so the unremoved image is compared and the
    /// line rows are treated as unknown). Returns the mean of template recall and ink precision.
    /// </summary>
    private double StaffAwareScore(byte[] tmpl, int w, int h, int px, int py, Staff staff)
    {
        if (px < 1 || py < 1 || px + w + 1 >= _bin.Width || py + h + 1 >= _bin.Height) return 0;
        double half = staff.LineThickness / 2 + 1, xc = px + w / 2.0;
        var ignore = new bool[h];
        foreach (var line in staff.Lines)
        {
            double ly = line.YAt(xc);
            for (int y = 0; y < h; y++) if (Math.Abs(py + y - ly) <= half) ignore[y] = true;
        }
        int tol = Math.Clamp((int)Math.Round(staff.Space / 16), 1, 2);
        // columns of a barline or bracket (ink running well beyond the staff at both ends) are unknown too
        var ignoreCol = new bool[w];
        int ignoredCols = 0;
        for (int x = 0; x < w; x++) if (ignoreCol[x] = IsBarlineColumn(px + x, staff)) ignoredCols++;
        if (ignoredCols > 0.4 * w) return 0;
        int tCount = 0, tHit = 0, oCount = 0, oHit = 0;
        for (int y = 0; y < h; y++)
        {
            if (ignore[y]) continue;
            for (int x = 0; x < w; x++)
            {
                if (ignoreCol[x]) continue;
                bool t = tmpl[y * w + x] != 0, o = _bin[px + x, py + y];
                if (!t && !o) continue;
                if (t)
                {
                    tCount++;
                    bool hit = false;
                    for (int dy = -tol; dy <= tol && !hit; dy++) for (int dx = -tol; dx <= tol; dx++) if (_bin[px + x + dx, py + y + dy]) { hit = true; break; }
                    if (hit) tHit++;
                }
                if (o)
                {
                    oCount++;
                    bool hit = false;
                    for (int dy = -tol; dy <= tol && !hit; dy++)
                        for (int dx = -tol; dx <= tol; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                            if (tmpl[yy * w + xx] != 0) { hit = true; break; }
                        }
                    if (hit) oHit++;
                }
            }
        }
        if (tCount == 0 || oCount == 0) return 0;
        return 0.5 * ((double)tHit / tCount + (double)oHit / oCount);
    }

    /// <summary>The right edge of the clef including the two dots of an F clef when they were not merged into it.</summary>
    private int ClefRightEdge(MusicSymbol clef, Staff staff)
    {
        double ss = staff.Space;
        if (clef.Name != "fClef") return clef.Bounds.X1;
        // the two dots sit in the spaces either side of the fourth line (steps 5 and 7): a short run of columns with ink in both bands
        int xFrom = clef.Bounds.X1 + 1, xTo = (int)Math.Round(clef.Bounds.X0 + 3.4 * ss);
        int band = Math.Max(1, (int)Math.Round(0.2 * ss));
        bool Both(int x)
        {
            double bottom = staff.BottomY(x);
            bool a = false, b = false;
            for (int dy = -band; dy <= band && !(a && b); dy++)
            {
                int y5 = (int)Math.Round(bottom - 2.5 * ss) + dy, y7 = (int)Math.Round(bottom - 3.5 * ss) + dy;
                if (x >= 0 && x < _bin.Width && y7 >= 0 && y5 < _bin.Height) { a |= _bin[x, y5]; b |= _bin[x, y7]; }
            }
            return a && b;
        }
        int runStart = -1, runEnd = -1, gap = 0;
        for (int x = Math.Max(0, xFrom); x < xTo && x < _bin.Width; x++)
        {
            if (Both(x)) { if (runStart < 0) runStart = x; runEnd = x; gap = 0; }
            else if (runStart >= 0 && ++gap > 1) break;
        }
        if (runStart >= 0 && runEnd - runStart + 1 <= 0.8 * ss) return Math.Max(clef.Bounds.X1, runEnd + 1);
        return Math.Max(clef.Bounds.X1, (int)Math.Round(clef.Bounds.X0 + 2.55 * ss));
    }

    /// <summary>Fraction of the columns [px, px+w) that carry ink in the rows [yFrom, yTo) of the original image.</summary>
    /// <summary>The share of columns with ink in rows [yFrom, yTo); with joinY set, only ink joined to row joinY by an
    /// unbroken vertical run counts (a stroke going on from the glyph, not a mark standing apart).</summary>
    private double InkColumns(int px, int w, int yFrom, int yTo, int joinY = -1)
    {
        if (w <= 0 || yTo <= yFrom) return 0;
        int cols = 0;
        for (int x = px; x < px + w; x++)
        {
            if (x < 0 || x >= _bin.Width) continue;
            if (joinY >= 0)
            {
                if (joinY >= _bin.Height || !_bin[x, joinY]) continue;
                int y = joinY, step = joinY < yFrom ? 1 : -1, target = joinY < yFrom ? yFrom : yTo - 1;
                while (y != target && y + step >= 0 && y + step < _bin.Height && _bin[x, y + step]) y += step;
                if (y == target) cols++;
                continue;
            }
            for (int y = Math.Max(0, yFrom); y < yTo && y < _bin.Height; y++) if (_bin[x, y]) { cols++; break; }
        }
        return (double)cols / w;
    }

    private static readonly HashSet<string> HandwrittenFonts = new() { "MuseJazz", "Petaluma" };

    /// <summary>Best score of any time-signature glyph (digits above / below the middle line, common time) centred near cx.</summary>
    private double BestTimeGlyphScore(Staff staff, int cx, int dyRange)
    {
        double ss = staff.Space, bottom = staff.BottomY(cx);
        int span = Math.Max(1, (int)Math.Round(0.3 * ss));
        double best = 0;
        foreach (var (name, step) in new[] { ("timeSig2", 6), ("timeSig3", 6), ("timeSig4", 6), ("timeSig5", 6), ("timeSig6", 6), ("timeSig7", 6), ("timeSig8", 6), ("timeSig9", 6),
                                             ("timeSig2", 2), ("timeSig4", 2), ("timeSig8", 2), ("timeSigCommon", 4), ("timeSigCutCommon", 4) })
        {
            var pl = BestPlacement(name, staff, bottom - step * ss / 2, cx - span, cx + span, dyRange, centred: true);
            if (pl != null && pl.Score > best) best = pl.Score;
        }
        return best;
    }

    /// <summary>True when the ink run through the staff's middle line at column x reaches 0.8 staff spaces beyond both outer lines.</summary>
    private bool IsBarlineColumn(int x, Staff staff)
    {
        if (x < 0 || x >= _bin.Width) return false;
        double ss = staff.Space;
        int yMid = (int)Math.Round(staff.Lines[2].YAt(x));
        if (yMid < 0 || yMid >= _bin.Height || !_bin[x, yMid]) return false;
        int top = (int)Math.Round(staff.TopY(x) - 0.8 * ss), bottom = (int)Math.Round(staff.BottomY(x) + 0.8 * ss);
        int gap = 0;
        for (int y = yMid; y > top; y--)
        {
            if (y < 0) return false;
            if (_bin[x, y]) gap = 0; else if (++gap > 1) return false;
        }
        gap = 0;
        for (int y = yMid; y < bottom; y++)
        {
            if (y >= _bin.Height) return false;
            if (_bin[x, y]) gap = 0; else if (++gap > 1) return false;
        }
        return true;
    }

    /// <summary>True when some column in [px, px+w) has a vertical ink run of at least 0.8 staff spaces through row y.
    /// With outward set (-1 up, +1 down) the run must also go on at least 0.6 staff spaces from y that way: a glyph's own
    /// stroke (the line of a cut C) only pokes out a little.</summary>
    private bool HasStemThrough(int px, int w, int y, double ss, int outward = 0)
    {
        if (y < 0 || y >= _bin.Height) return false;
        for (int x = px; x < px + w; x++)
        {
            if (x < 0 || x >= _bin.Width || !_bin[x, y]) continue;
            int up = y, down = y;
            while (up > 0 && _bin[x, up - 1]) up--;
            while (down < _bin.Height - 1 && _bin[x, down + 1]) down++;
            if (down - up + 1 < 0.8 * ss) continue;
            if (outward < 0 && y - up < 0.6 * ss || outward > 0 && down - y < 0.6 * ss) continue;
            return true;
        }
        return false;
    }

    private sealed class Placement { public GlyphTemplate T = null!; public byte[] Mask = null!; public int X, Y, W, H; public double Score; }

    /// <summary>Best placement of any template with the given name: the glyph origin's y is yOrigin (+-dyRange), its left edge scanned over [xFrom, xTo].</summary>
    private Placement? BestPlacement(string name, Staff staff, double yOrigin, int xFrom, int xTo, int dyRange, bool centred = false)
    {
        Placement? best = null;
        double ss = staff.Space;
        foreach (var t in _lib!.Templates.Where(t => t.Name == name && !HandwrittenFonts.Contains(t.Font)))
        {
            int w = Math.Max(2, (int)Math.Round(t.WidthSS * ss)), h = Math.Max(2, (int)Math.Round(t.HeightSS * ss));
            var mask = RenderTemplate(t, w, h);
            int y0 = (int)Math.Round(yOrigin + t.TopSS * ss);
            for (int x = xFrom; x <= xTo; x++)
                for (int dy = -dyRange; dy <= dyRange; dy++)
                {
                    int px = centred ? x - w / 2 : x;
                    double sc = StaffAwareScore(mask, w, h, px, y0 + dy, staff);
                    if (best == null || sc > best.Score) best = new Placement { T = t, Mask = mask, X = px, Y = y0 + dy, W = w, H = h, Score = sc };
                }
        }
        return best;
    }

    /// <summary>Components lying (by bounding-box area) mostly inside the rectangle are consumed.</summary>
    private static void ConsumeInside(List<Component> comps, Rect r, HashSet<Component> consumed, double minFraction = 0.5)
    {
        foreach (var c in comps)
        {
            if (consumed.Contains(c)) continue;
            int ox = Math.Min(c.X1, r.X1 + 1) - Math.Max(c.X0, r.X0 - 1) + 1, oy = Math.Min(c.Y1, r.Y1 + 1) - Math.Max(c.Y0, r.Y0 - 1) + 1;
            if (ox <= 0 || oy <= 0) continue;
            if ((double)ox * oy >= minFraction * c.Width * c.Height) consumed.Add(c);
        }
    }

    /// <summary>
    /// Clef fallback for small or badly scanned staves where the clef shatters into many fragments: the clef templates
    /// are placed in the original image at the height the staff dictates (G clef on line 2, F clef on line 4, C clef
    /// on line 3 or 4) and scored with the staff-line rows ignored.
    /// </summary>
    private void DetectClefByTemplate(List<Component> comps, Staff staff, List<MusicSymbol> syms, HashSet<Component> consumed)
    {
        if (_lib == null) return;
        double ss = staff.Space;
        // something clef-sized must be there: fragments of at least one staff space in height near the staff start
        if (!comps.Any(c => !consumed.Contains(c) && c.X0 >= staff.Left - 1.0 * ss && c.X0 < staff.Left + 2.8 * ss && c.Height >= 0.8 * ss
                            && c.Y1 > staff.TopY(c.CenterX) - 2.5 * ss && c.Y0 < staff.BottomY(c.CenterX) + 2.5 * ss)) return;
        int xFrom = (int)Math.Round(staff.Left - 0.3 * ss), xTo = (int)Math.Round(staff.Left + 2.2 * ss);
        double bottom = staff.BottomY(staff.Left + 1.5 * ss);
        // C clefs of older editions are much wider than the font glyphs: try horizontally stretched templates too
        var hypotheses = new (string name, int step, string kind, double stretch)[]
        {
            ("gClef", 2, "treble", 1), ("fClef", 6, "bass", 1), ("cClef", 4, "alto", 1), ("cClef", 6, "tenor", 1),
            ("cClef", 4, "alto", 1.3), ("cClef", 6, "tenor", 1.3), ("cClef", 4, "alto", 1.6), ("cClef", 6, "tenor", 1.6),
        };
        Placement? best = null; string? bestKind = null;
        int dyRange = Math.Max(1, (int)Math.Round(0.12 * ss));
        foreach (var (name, step, kind, stretch) in hypotheses)
        {
            // coarse scan every other column, then refine around the best position
            var coarse = BestPlacementStep(name, staff, bottom - step * ss / 2, xFrom, xTo, dyRange, 2, stretch);
            if (coarse == null) continue;
            var fine = BestPlacementStep(name, staff, bottom - step * ss / 2, coarse.X - 1, coarse.X + 1, dyRange, 1, stretch) ?? coarse;
            if (KeyDump) Console.Error.WriteLine($"clefhyp staff@{staff.Left},{staff.TopY(staff.Left):0} {name}/{kind} {fine.Score:0.00} at x{fine.X} w{fine.W} ({fine.T.Font})");
            if (best == null || fine.Score > best.Score) { best = fine; bestKind = kind; }
        }
        if (KeyDump && best != null) Console.Error.WriteLine($"clefzone staff@{staff.Left},{staff.TopY(staff.Left):0} {best.T.Name}/{bestKind} score {best.Score:0.00} at x{best.X} ({best.T.Font})");
        if (best == null || best.Score < 0.75) return;
        var rect = new Rect(best.X, best.Y, best.X + best.W - 1, best.Y + best.H - 1);
        var sym = new MusicSymbol { Kind = SymbolKind.Clef, Name = best.T.Name, Staff = staff, Confidence = Math.Round(best.Score, 2), Bounds = rect };
        sym.Attributes["clefzone"] = "1";
        var fake = new Component { X0 = rect.X0, Y0 = rect.Y0, X1 = rect.X1, Y1 = rect.Y1, PixelCount = best.W * best.H / 3 };
        PostProcessTemplate(sym, fake, staff, false);
        if (best.T.Name == "cClef") sym.Attributes["clef"] = bestKind!;
        syms.Add(sym);
        ConsumeInside(comps, rect, consumed);
    }

    private Placement? BestPlacementStep(string name, Staff staff, double yOrigin, int xFrom, int xTo, int dyRange, int xStep, double stretch = 1)
    {
        Placement? best = null;
        double ss = staff.Space;
        foreach (var t in _lib!.Templates.Where(t => t.Name == name && !HandwrittenFonts.Contains(t.Font)))
        {
            int w = Math.Max(2, (int)Math.Round(t.WidthSS * ss * stretch)), h = Math.Max(2, (int)Math.Round(t.HeightSS * ss));
            var mask = RenderTemplate(t, w, h);
            int y0 = (int)Math.Round(yOrigin + t.TopSS * ss);
            for (int x = xFrom; x <= xTo; x += xStep)
                for (int dy = -dyRange; dy <= dyRange; dy += xStep)
                {
                    double sc = StaffAwareScore(mask, w, h, x, y0 + dy, staff);
                    if (best == null || sc > best.Score) best = new Placement { T = t, Mask = mask, X = x, Y = y0 + dy, W = w, H = h, Score = sc };
                }
        }
        return best;
    }

    /// <summary>
    /// Key signature right after the clef. On small staves the sharps and flats are shredded by staff removal, so the
    /// n-th accidental is searched as a whole in the original image at the position the clef dictates for it (both
    /// kinds for the first one), with the staff-line rows ignored; the run ends at the first position without a match.
    /// </summary>
    private void DetectKeySignature(List<Component> comps, Staff staff, List<MusicSymbol> syms, HashSet<Component> consumed)
    {
        if (_lib == null) return;
        var clef = syms.FirstOrDefault(s => s.Kind == SymbolKind.Clef);
        if (clef == null) return;
        string? clefKind = ClefKindName(clef);
        if (clefKind == null) return;
        int edge = ClefRightEdge(clef, staff);
        var (accepted, kind) = KeyRunAt(edge, clefKind, staff);
        // one sharp may be the second flat of a key whose first flat touches the clef (inside its box): flats from a
        // little inside the clef, when at least two of them are found, win
        if (accepted.Count == 1 && kind == "accidentalSharp")
        {
            var (flats, fk) = KeyRunAt(edge - (int)Math.Round(0.8 * staff.Space), clefKind, staff, "accidentalFlat", insideClef: true);
            // (the "sharp" is then the second flat itself)
            if (flats.Count >= 2 && flats.All(f => f.Score >= 0.8) && Math.Abs(flats[1].X - accepted[0].X) <= 0.5 * staff.Space) { accepted = flats; kind = fk; }
        }
        AddKeySymbols(comps, staff, syms, consumed, accepted, kind, change: false);
    }

    /// <summary>
    /// A change of key inside a system: a run of at least two sharps or flats at the key signature places right after
    /// a double (or final, thick, repeat-start) bar line, found like the key signature at the system start.
    /// </summary>
    private void DetectKeyChanges(List<Component> comps, Staff staff, List<MusicSymbol> syms, HashSet<Component> consumed)
    {
        if (_lib == null) return;
        var clef = syms.FirstOrDefault(s => s.Kind == SymbolKind.Clef);
        string? clefKind = clef == null ? null : ClefKindName(clef);
        if (clefKind == null) return;
        double ss = staff.Space;
        foreach (var bar in staff.Barlines.Where(b => b.Kind is BarlineKind.Double or BarlineKind.Final or BarlineKind.Thick or BarlineKind.RepeatStart or BarlineKind.RepeatBoth
                                                      && b.X1 < staff.Right - 2 * ss))
        {
            int xStart = bar.X1;
            foreach (var t in comps.Where(c => c.X0 > xStart && c.X0 <= xStart + 0.8 * ss && c.Width <= 0.6 * ss && c.Height >= 3.5 * ss).OrderBy(c => c.X0)) xStart = Math.Max(xStart, t.X1);
            var (accepted, kind) = KeyRunAt(xStart, clefKind, staff);
            if (KeyDump) Console.Error.WriteLine($"key change staff {staff.Index + 1} after bar {bar.X0}: {accepted.Count} {kind}");
            if (accepted.Count < 2) continue;
            AddKeySymbols(comps, staff, syms, consumed, accepted, kind, change: true);
        }
    }

    private void AddKeySymbols(List<Component> comps, Staff staff, List<MusicSymbol> syms, HashSet<Component> consumed, List<Placement> accepted, string? kind, bool change)
    {
        if (accepted.Count == 0) return;
        foreach (var pl in accepted)
        {
            var rect = new Rect(pl.X, pl.Y, pl.X + pl.W - 1, pl.Y + pl.H - 1);
            var sym = new MusicSymbol { Kind = SymbolKind.Accidental, Name = kind!, Staff = staff, Confidence = Math.Round(pl.Score, 2), Bounds = rect };
            sym.Attributes["keyzone"] = "1";
            if (change) sym.Attributes["keychange"] = "1";
            var fake = new Component { X0 = rect.X0, Y0 = rect.Y0, X1 = rect.X1, Y1 = rect.Y1, PixelCount = pl.W * pl.H / 3 };
            PostProcessTemplate(sym, fake, staff, false);
            syms.Add(sym);
            ConsumeInside(comps, rect, consumed);
        }
    }

    /// <summary>The key signature run starting right of x: each next accidental at the place the clef gives it.</summary>
    private (List<Placement> accepted, string? kind) KeyRunAt(int lastX1, string clefKind, Staff staff, string? onlyKind = null, bool insideClef = false)
    {
        double ss = staff.Space;
        const double Threshold = 0.66;
        int dyRange = Math.Max(1, (int)Math.Round(0.15 * ss));
        string? kind = onlyKind;
        var accepted = new List<Placement>();
        for (int n = 0; n < 7; n++)
        {
            int xFrom = lastX1 + (n == 0 ? (int)Math.Round(0.05 * ss) : -(int)Math.Round(0.1 * ss));
            int xTo = lastX1 + (int)Math.Round((n == 0 ? 2.2 : 0.8) * ss);
            Placement? best = null; string? bestKind = null;
            foreach (var k in kind != null ? new[] { kind } : new[] { "accidentalFlat", "accidentalSharp" })
            {
                int? step = ExpectedKeyStep(clefKind, k == "accidentalSharp", n);
                if (step == null) break;
                double yOrigin = staff.BottomY(lastX1 + 0.5 * ss) - step.Value * ss / 2;
                var pl = BestPlacement(k, staff, yOrigin, xFrom, xTo, dyRange);
                // some engravers set a low accidental an octave up, inside the staff (the A sharp on the top line of a
                // bass staff): the better of the two
                if (step.Value <= 2 && n > 0)
                {
                    var up = BestPlacement(k, staff, yOrigin - 3.5 * ss, xFrom, xTo, dyRange);
                    if (up != null && (pl == null || up.Score > pl.Score)) pl = up;
                }
                if (pl != null && (best == null || pl.Score > best.Score)) { best = pl; bestKind = k; }
                if (KeyScan && k == (kind ?? "accidentalFlat"))
                    Console.Error.WriteLine($"keyscan staff@{staff.Left},{staff.TopY(staff.Left):0} x{xFrom}..{xTo}: " + string.Join(" ", Enumerable.Range(xFrom, xTo - xFrom + 1).Select(x => (BestPlacement(k, staff, yOrigin, x, x, dyRange)?.Score ?? 0).ToString("0.00"))));
            }
            // after a confident first accidental the following ones may be weaker (heavier ink, touching neighbours)
            double thr = Threshold;
            bool ok = best != null && best.Score >= thr;
            // a time-signature digit explains the ink clearly better: the key signature ended before it
            double digit = ok ? BestTimeGlyphScore(staff, best!.X + best.W / 2, dyRange) : 0;
            // (no time signature stands inside the clef: a first accidental touching it is not weighed against digits)
            if (ok && digit > best!.Score + 0.03 && !(insideClef && n == 0)) ok = false;
            // accidentals of a key signature are evenly spaced
            if (ok && accepted.Count >= 2)
            {
                double gap = best!.X - accepted[^1].X, prevGap = accepted[^1].X - accepted[^2].X;
                if (gap < 0.6 * prevGap || gap > 1.6 * prevGap) ok = false;
            }
            if (KeyDump && best != null) Console.Error.WriteLine($"keyzone staff@{staff.Left},{staff.TopY(staff.Left):0} n={n} {bestKind} score {best.Score:0.00} digit {digit:0.00} at x{best.X} y{best.Y} ({best.T.Font}) -> {(ok ? "ok" : "stop")}");
            if (!ok) break;
            kind = bestKind; accepted.Add(best!); lastX1 = best.X + best.W - 1;
        }
        return (accepted, kind);
    }

    /// <summary>
    /// Time signature after the clef and key signature: digit templates (upper digit between the top and middle
    /// line, lower digit below) and the common/cut-time glyphs are placed in the original image and scored with the
    /// staff-line rows ignored; the best column wins when both halves match.
    /// </summary>
    private void DetectTimeSignature(List<Component> comps, Staff staff, List<MusicSymbol> syms, HashSet<Component> consumed)
    {
        if (_lib == null) return;
        var clef = syms.FirstOrDefault(s => s.Kind == SymbolKind.Clef);
        if (clef == null) return;
        double ss = staff.Space;
        var keyAcc = syms.Where(s => s.Kind == SymbolKind.Accidental && s.Attributes.ContainsKey("keyzone")).ToList();
        int xStart = keyAcc.Count > 0 ? keyAcc.Max(s => s.Bounds.X1) : ClefRightEdge(clef, staff);
        // nothing of glyph size in the zone: no time signature (saves the template scan on most staves)
        if (!comps.Any(c => !consumed.Contains(c) && c.X0 >= xStart && c.X0 < xStart + 3 * ss && c.Height >= 0.8 * ss
                            && c.CenterY > staff.TopY(c.CenterX) - 0.5 * ss && c.CenterY < staff.BottomY(c.CenterX) + 0.5 * ss)) return;
        var sym = TimeSignatureAt(xStart, staff);
        if (sym == null) return;
        sym.Attributes["timezone"] = "1";
        syms.Add(sym);
        ConsumeInside(comps, sym.Bounds, consumed);
    }

    /// <summary>
    /// A change of metre inside a system: digits filling the staff height (often touching, one component) right
    /// after a bar line, usually a double one, are matched like the time signature at the system start.
    /// </summary>
    private void DetectTimeChanges(List<Component> comps, Staff staff, List<MusicSymbol> syms, HashSet<Component> consumed)
    {
        if (_lib == null) return;
        double ss = staff.Space;
        foreach (var bar in staff.Barlines.Where(b => b.Kind != BarlineKind.SystemStart && b.X1 < staff.Right - 2 * ss))
        {
            // the thick line of a double bar may be left as a separate stroke: start after it
            int xStart = bar.X1;
            foreach (var t in comps.Where(c => c.X0 > xStart && c.X0 <= xStart + 0.8 * ss && c.Width <= 0.6 * ss && c.Height >= 3.5 * ss).OrderBy(c => c.X0)) xStart = Math.Max(xStart, t.X1);
            // a change of key comes first
            var keyChange = syms.Where(k => k.Attributes.ContainsKey("keychange") && k.Bounds.X0 > bar.X1 && k.Bounds.X0 < bar.X1 + 8 * ss).ToList();
            if (keyChange.Count > 0) xStart = Math.Max(xStart, keyChange.Max(k => k.Bounds.X1));
            // a change of key not read as one (naturals cancelling the old key, then the new accidentals): the time
            // signature may also start after any of the accidental-sized pieces following the bar
            var starts = new List<int> { xStart };
            foreach (var c in comps.Where(c => !consumed.Contains(c) && c.X0 > xStart && c.X0 < xStart + 5 * ss && c.Width >= 0.4 * ss && c.Width <= 1.2 * ss
                                               && c.Height >= 1.8 * ss && c.Height <= 3.6 * ss && c.CenterY > staff.TopY(c.CenterX) - 0.5 * ss && c.CenterY < staff.BottomY(c.CenterX) + 0.5 * ss)
                                   .OrderBy(c => c.X0))
            {
                if (c.X0 > starts[^1] + 1.5 * ss) break;   // the run of accidentals ended
                starts.Add(Math.Max(starts[^1], c.X1));
            }
            MusicSymbol? sym = null;
            foreach (int x0 in starts)
            {
                var near = comps.Where(c => !consumed.Contains(c) && c.X0 > x0 && c.X0 < x0 + 1.5 * ss && c.Width >= 0.6 * ss && c.Width <= 2.4 * ss
                                            && c.CenterY > staff.TopY(c.CenterX) && c.CenterY < staff.BottomY(c.CenterX)).ToList();
                // one staff-high piece (touching digits) or two stacked digit-sized pieces
                bool tall = near.Any(c => c.Height >= 3.2 * ss && c.Height <= 4.6 * ss);
                bool stacked = near.Any(a => a.Height >= 1.4 * ss && a.Height <= 2.4 * ss && near.Any(b => b != a && b.Y0 > a.CenterY + ss && b.Height >= 1.4 * ss && b.Height <= 2.4 * ss && Math.Abs(b.CenterX - a.CenterX) < 0.8 * ss));
                // or a C (common or cut time, its left half may break off) around the middle line, often after a change of key
                bool common = near.Any(c => c.Height >= 1.8 * ss && c.Height <= 3.2 * ss && c.Width >= 0.5 * ss && Math.Abs(c.CenterY - staff.Lines[2].YAt(c.CenterX)) <= 0.5 * ss);
                if (!tall && !stacked && !common) continue;
                // right after a change of key a C is expected: no note head beside an accidental to tell it from (the open-C
                // test), and a worn one is enough
                sym = keyChange.Count > 0 ? TimeSignatureAt(x0, staff, afterBar: false, commonMin: 0.65) : TimeSignatureAt(x0, staff, afterBar: true);
                if (KeyDump) Console.Error.WriteLine($"time change staff {staff.Index + 1} after bar {bar.X0} from {x0}: {(sym == null ? "none" : sym.Attributes.GetValueOrDefault("stack") ?? sym.Name)}");
                if (sym != null) break;
            }
            if (sym == null) continue;
            sym.Attributes["timezone"] = "1";
            syms.Add(sym);
            ConsumeInside(comps, sym.Bounds, consumed);
        }
    }

    private MusicSymbol? TimeSignatureAt(int xStart, Staff staff, bool afterBar = false, double commonMin = 0.85)
    {
        double ss = staff.Space;
        const double Threshold = 0.66;
        int dyRange = Math.Max(1, (int)Math.Round(0.12 * ss));
        int cxFrom = xStart + (int)Math.Round(0.5 * ss), cxTo = xStart + (int)Math.Round(2.6 * ss);
        double xm = xStart + 1.5 * ss;
        double yUpper = staff.BottomY(xm) - 3 * ss, yLower = staff.BottomY(xm) - 1 * ss, yMid = staff.BottomY(xm) - 2 * ss;
        string[] uppers = { "timeSig1", "timeSig2", "timeSig3", "timeSig4", "timeSig5", "timeSig6", "timeSig7", "timeSig8", "timeSig9" };
        string[] lowers = { "timeSig1", "timeSig2", "timeSig4", "timeSig8" };
        Placement? bu = null, bl = null;
        foreach (var u in uppers) { var pl = BestPlacement(u, staff, yUpper, cxFrom, cxTo, dyRange, centred: true); if (pl != null && (bu == null || pl.Score > bu.Score)) bu = pl; }
        if (bu != null && bu.Score >= Threshold)
        {
            int cx = bu.X + bu.W / 2;
            int span = Math.Max(1, (int)Math.Round(0.2 * ss));
            // a lower 1 is very rare, and its thin template also fits a wider digit broken by the staff lines
            double Rank(Placement p) => p.Score - (p.T.Name == "timeSig1" ? 0.06 : 0);
            foreach (var l in lowers) { var pl = BestPlacement(l, staff, yLower, cx - span, cx + span, dyRange, centred: true); if (pl != null && (bl == null || Rank(pl) > Rank(bl))) bl = pl; }
        }
        Placement? bc = null;
        foreach (var c in new[] { "timeSigCommon", "timeSigCutCommon" }) { var pl = BestPlacement(c, staff, yMid, cxFrom, cxTo, dyRange, centred: true); if (pl != null && (bc == null || pl.Score > bc.Score)) bc = pl; }
        double digitScore = bu != null && bl != null ? 0.5 * (bu.Score + bl.Score) : 0;
        // digits end at the outer staff lines: ink continuing above the upper or below the lower digit is a stem or a bar, not a digit
        if (bu != null && bl != null)
        {
            int ext = (int)Math.Round(0.5 * ss), lineHalf = (int)Math.Ceiling(staff.LineThickness / 2) + 1;
            // (a quarter space clear of the template: a bold digit runs a little past it)
            int clear = (int)Math.Round(0.25 * ss);
            // (joined to the digit's edge: a dynamic or text below the staff stands apart)
            double above = InkColumns(bu.X, bu.W, bu.Y - lineHalf - clear - ext, bu.Y - lineHalf - clear, bu.Y),
                   below = InkColumns(bl.X, bl.W, bl.Y + bl.H + lineHalf + clear, bl.Y + bl.H + lineHalf + clear + ext, bl.Y + bl.H - 1);
            if (above > 0.3 || below > 0.3) digitScore = 0;
            // a stem through the digit's edge (a note, not a digit): a vertical ink run of a staff space crossing the boundary
            // (going on beyond the edge: a digit's own stroke, the stem of a 4, pokes out only a little)
            if (HasStemThrough(bu.X, bu.W, bu.Y - lineHalf - 1, ss, -1) || HasStemThrough(bl.X, bl.W, bl.Y + bl.H + lineHalf + 1, ss, +1)) digitScore = 0;
            // the stroke of a cut C pokes out a little at both ends (a 4's stem at one end only)
            if (HasStemThrough(bu.X, bu.W, bu.Y - lineHalf - 1, ss) && HasStemThrough(bl.X, bl.W, bl.Y + bl.H + lineHalf + 1, ss)) digitScore = 0;
            if (bu.T.Name == "timeSig1" && bl.T.Name == "timeSig1") digitScore = 0;
            else if (bu.T.Name == "timeSig1" || bl.T.Name == "timeSig1") digitScore -= 0.08;
        }
        double commonScore = bc?.Score ?? 0;
        // likewise a stem through the top or bottom of a C: a note head after the bar line, not a signature (the stroke of
        // a cut C pokes out only a little)
        if (bc != null)
        {
            int lineHalf = (int)Math.Ceiling(staff.LineThickness / 2) + 1;
            if (HasStemThrough(bc.X, bc.W, bc.Y - lineHalf - 1, ss, -1) || HasStemThrough(bc.X, bc.W, bc.Y + bc.H + lineHalf, ss, +1)) commonScore = 0;
            // a C is open on the right: ink filling its right middle is a note head beside an accidental, not a C
            // (checked after a bar line, where notes follow closely)
            if (afterBar)
            {
                int ox0 = bc.X + (int)(0.75 * bc.W), ox1 = bc.X + bc.W - 1, oy0 = bc.Y + (int)(0.42 * bc.H), oy1 = bc.Y + (int)(0.58 * bc.H);
                int open = 0, filled = 0;
                for (int y = Math.Max(0, oy0); y <= Math.Min(_bin.Height - 1, oy1); y++)
                    for (int x = Math.Max(0, ox0); x <= Math.Min(_bin.Width - 1, ox1); x++) { open++; if (_bin[x, y]) filled++; }
                if (open > 0 && filled > 0.5 * open) commonScore = 0;
            }
        }
        if (KeyDump) Console.Error.WriteLine($"timezone staff@{staff.Left},{staff.TopY(staff.Left):0} digits {bu?.T.Name}/{bl?.T.Name} {bu?.Score:0.00}/{bl?.Score:0.00} common {bc?.T.Name} {commonScore:0.00}");
        // a two-digit numerator (10 to 16, as in 12/8): a 1 with a second digit beside it, the lower digit centred under
        // the pair; it wins when all three fit clearly better than a single upper digit does
        {
            var b1 = BestPlacement("timeSig1", staff, yUpper, cxFrom - (int)Math.Round(0.4 * ss), cxTo, dyRange, centred: true);
            if (b1 != null && b1.Score >= 0.7)
            {
                Placement? b2 = null;
                foreach (var u in new[] { "timeSig0", "timeSig2", "timeSig5", "timeSig6" })   // (no 11: a thin 3 reads as two 1s)
                {
                    var pl = BestPlacement(u, staff, yUpper, b1.X + b1.W - (int)Math.Round(0.15 * ss), b1.X + b1.W + (int)Math.Round(0.45 * ss), dyRange);
                    if (pl != null && (b2 == null || pl.Score > b2.Score)) b2 = pl;
                }
                // two digits side by side are clearly wider than one (the strokes of a 3 also take a 1 and another 1)
                if (b2 != null && b2.Score >= 0.7 && b2.X + b2.W / 2 >= b1.X + b1.W && (bu == null || b2.X + b2.W - b1.X >= 1.4 * bu.W))
                {
                    int pcx = (b1.X + b2.X + b2.W) / 2, span = Math.Max(1, (int)Math.Round(0.3 * ss));
                    Placement? tl = null;
                    foreach (var l in new[] { "timeSig2", "timeSig4", "timeSig8" })
                    {
                        var pl = BestPlacement(l, staff, yLower, pcx - span, pcx + span, dyRange, centred: true);
                        if (pl != null && (tl == null || pl.Score > tl.Score)) tl = pl;
                    }
                    double two = tl == null ? 0 : (b1.Score + b2.Score + tl.Score) / 3;
                    // one digit centred on the pair that fits about as well is what is printed (a 3 also takes a 1 and a 5)
                    double single = new[] { "timeSig2", "timeSig3", "timeSig4", "timeSig5", "timeSig6", "timeSig7", "timeSig8", "timeSig9" }
                        .Select(u => BestPlacement(u, staff, yUpper, pcx - span, pcx + span, dyRange, centred: true)?.Score ?? 0).Max();
                    if (single >= Math.Min(b1.Score, b2.Score) - 0.03) two = 0;
                    int lineHalf = (int)Math.Ceiling(staff.LineThickness / 2) + 1;
                    bool stem = tl != null && (HasStemThrough(b1.X, b2.X + b2.W - b1.X, b1.Y - lineHalf - 1, ss, -1) || HasStemThrough(tl.X, tl.W, tl.Y + tl.H + lineHalf + 1, ss, +1));
                    if (KeyDump) Console.Error.WriteLine($"   two-digit 1{b2.T.Name[7..]}/{tl?.T.Name[7..]} {b1.Score:0.00} {b2.Score:0.00} {tl?.Score:0.00} stem {stem}");
                    if (tl != null && tl.Score >= 0.75 && two >= 0.82 && two > digitScore + 0.02 && two > commonScore && !stem)
                    {
                        var rect = new Rect(Math.Min(b1.X, tl.X), b1.Y, Math.Max(b2.X + b2.W, tl.X + tl.W) - 1, tl.Y + tl.H - 1);
                        var two2 = new MusicSymbol { Kind = SymbolKind.TimeSignature, Name = "timeSigStack", Staff = staff, Confidence = Math.Round(two, 2), Bounds = rect };
                        two2.Attributes["stack"] = $"1{b2.T.Name[7..]}/{tl.T.Name[7..]}";
                        return two2;
                    }
                }
            }
        }
        MusicSymbol? sym;
        if (digitScore >= 0.85 && bu!.Score >= 0.78 && bl!.Score >= 0.78 && digitScore >= commonScore)
        {
            var rect = new Rect(Math.Min(bu.X, bl.X), bu.Y, Math.Max(bu.X + bu.W, bl.X + bl.W) - 1, bl.Y + bl.H - 1);
            sym = new MusicSymbol { Kind = SymbolKind.TimeSignature, Name = "timeSigStack", Staff = staff, Confidence = Math.Round(digitScore, 2), Bounds = rect };
            sym.Attributes["stack"] = $"{bu.T.Name[7..]}/{bl.T.Name[7..]}";
        }
        else if (commonScore >= commonMin)
        {
            var rect = new Rect(bc!.X, bc.Y, bc.X + bc.W - 1, bc.Y + bc.H - 1);
            sym = new MusicSymbol { Kind = SymbolKind.TimeSignature, Name = bc.T.Name, Staff = staff, Confidence = Math.Round(commonScore, 2), Bounds = rect };
        }
        else sym = null;
        return sym;
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

    /// <summary>
    /// An 8th rest cut by a staff line: the round blob above (read as text or a dot) and the thin slanted stroke below
    /// it (text, unknown, a tiny "dynamic"), the stroke starting right under the blob's right half.
    /// </summary>
    private static void EighthRestsFromPieces(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        bool InStaff(MusicSymbol s) => s.Bounds.CenterY > staff.TopY(s.Bounds.CenterX) && s.Bounds.CenterY < staff.BottomY(s.Bounds.CenterX);
        bool Fragment(MusicSymbol s) => s.Kind is SymbolKind.Text or SymbolKind.Unknown or SymbolKind.Dot || TinyDynamic(s, staff);
        // (the blob may also have been taken for a double sharp or a digit of unknown value, small blobs of that size)
        var blobs = syms.Where(s => (Fragment(s) || s.Kind == SymbolKind.Accidental && s.Name.Contains("DoubleSharp")
                                     || s.Kind == SymbolKind.TimeSignature && s.Name == "timeSig?" && !s.Attributes.ContainsKey("timezone")) && InStaff(s)
                                    && s.Bounds.Width >= 0.4 * ss && s.Bounds.Width <= 1.3 * ss && s.Bounds.Height >= 0.35 * ss && s.Bounds.Height <= 1.4 * ss).ToList();
        foreach (var b in blobs)
        {
            if (!syms.Contains(b)) continue;
            var stroke = b.Bounds.Width < 0.6 * ss ? null
                : syms.FirstOrDefault(s => s != b && Fragment(s) && s.Bounds.Width <= 0.6 * ss && s.Bounds.Height >= 0.6 * ss && s.Bounds.Height <= 1.8 * ss
                                           && s.Bounds.Y0 >= b.Bounds.Y1 - 0.35 * ss && s.Bounds.Y0 - b.Bounds.Y1 <= 0.45 * ss
                                           && s.Bounds.CenterX >= b.Bounds.CenterX && s.Bounds.X0 <= b.Bounds.X1 + 0.1 * ss);
            // a smaller blob (a dot) with the stroke right beside it, read as an unsure "s"
            stroke ??= b.Kind == SymbolKind.Dot ? syms.FirstOrDefault(s => s != b && s.Kind == SymbolKind.Dynamic && s.Confidence < 0.7 && s.Bounds.Width <= 0.6 * ss
                                                     && s.Bounds.Height >= 1.2 * ss && s.Bounds.Height <= 2.0 * ss
                                                     && s.Bounds.Y0 >= b.Bounds.Y0 && s.Bounds.Y0 <= b.Bounds.Y1 && s.Bounds.X0 >= b.Bounds.X1 - 0.1 * ss && s.Bounds.X0 <= b.Bounds.X1 + 0.3 * ss) : null;
            if (stroke == null) continue;
            var u = b.Bounds.Union(stroke.Bounds);
            if (u.Height > 2.4 * ss) continue;
            var rest = new MusicSymbol { Kind = SymbolKind.Rest, Name = "rest8th", Duration = "8th", Staff = staff, Confidence = 0.7, Bounds = u };
            rest.Attributes["pieces"] = "blob+stroke";
            syms.Remove(b); syms.Remove(stroke);
            // slivers between them (the cut at the staff line)
            syms.RemoveAll(s => s.Kind == SymbolKind.Unknown && s.Bounds.Width <= 0.6 * ss && s.Bounds.Height <= 0.3 * ss
                                && s.Bounds.X0 >= u.X0 && s.Bounds.X1 <= u.X1 && s.Bounds.Y0 >= u.Y0 && s.Bounds.Y1 <= u.Y1);
            syms.Add(rest);
        }
    }

    /// <summary>
    /// A flat tie between two notes, cut by a staff line, read as a stemless hollow head: too flat for a head and with
    /// a thin stroke through its middle (a hollow head is empty there). It becomes a slur, with a thin fragment in line.
    /// </summary>
    private void TiesReadAsHeads(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        bool Fragment(MusicSymbol s) => s.Kind is SymbolKind.Text or SymbolKind.Unknown;
        foreach (var h in syms.Where(s => s.Kind == SymbolKind.Note && s.StemUp == null && s.Notes.Count == 1 && s.Notes[0].HeadType is "half" or "whole"
                                          && s.Bounds.Height <= 0.9 * ss && s.Bounds.Width >= 1.5 * s.Bounds.Height).ToList())
        {
            var n = h.Notes[0];
            if (HeadInkHeight(n.HeadX, n.HeadY, ss) is not double ink || ink > 0.3 * ss) continue;
            var slur = new MusicSymbol { Kind = SymbolKind.Slur, Name = "slur/tie", Staff = staff, Confidence = 0.5, Bounds = h.Bounds };
            slur.Attributes["pieces"] = "flat head";
            foreach (var f in syms.Where(s => Fragment(s) && s.Bounds.Height <= 0.9 * ss
                                              && Math.Min(s.Bounds.Y1, h.Bounds.Y1) - Math.Max(s.Bounds.Y0, h.Bounds.Y0) >= 0.5 * Math.Min(s.Bounds.Height, h.Bounds.Height)
                                              && (s.Bounds.X0 > h.Bounds.X1 ? s.Bounds.X0 - h.Bounds.X1 : h.Bounds.X0 - s.Bounds.X1) <= 1.2 * ss).ToList())
            {
                slur.Bounds = slur.Bounds.Union(f.Bounds);
                syms.Remove(f);
            }
            syms.Remove(h); syms.Add(slur);
        }
    }

    /// <summary>
    /// A 16th rest whose upper blob came apart (read as text or a dot): an 8th rest with a loose blob of a rest's size
    /// right on top of it, over its stroke.
    /// </summary>
    private static void SixteenthRestsFromPieces(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        bool Fragment(MusicSymbol s) => s.Kind is SymbolKind.Text or SymbolKind.Unknown or SymbolKind.Dot || TinyDynamic(s, staff);
        // (the "8th rest" left is taller than an 8th rest: a 16th rest's lower blob and its long stroke)
        foreach (var r in syms.Where(s => s.Kind == SymbolKind.Rest && s.Duration == "8th" && s.Bounds.Height >= 1.9 * ss).ToList())
        {
            var blob = syms.FirstOrDefault(s => s != r && Fragment(s) && s.Bounds.Width >= 0.3 * ss && s.Bounds.Width <= 1.1 * ss && s.Bounds.Height >= 0.3 * ss && s.Bounds.Height <= 1.1 * ss
                                                && s.Bounds.Y0 <= r.Bounds.Y0 - 0.2 * ss
                                                && s.Bounds.Y1 >= r.Bounds.Y0 - 0.5 * ss && s.Bounds.Y1 <= r.Bounds.Y0 + 0.4 * ss
                                                && s.Bounds.CenterX >= r.Bounds.X0 - 0.2 * ss && s.Bounds.CenterX <= r.Bounds.X1 + 0.2 * ss);
            if (blob == null) continue;
            r.Name = "rest16th"; r.Duration = "16th";
            r.Bounds = r.Bounds.Union(blob.Bounds);
            r.Attributes["pieces"] = "blob+8th rest";
            syms.Remove(blob);
        }
    }

    /// <summary>
    /// A quarter rest cut by a staff line: the zigzag above (read as text or a "1") and the hook below it (a fragment,
    /// an articulation, or a C outside the time signature zone), together a quarter rest's size.
    /// </summary>
    private static void QuarterRestsFromPieces(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        bool InStaff(MusicSymbol s) => s.Bounds.CenterY > staff.TopY(s.Bounds.CenterX) && s.Bounds.CenterY < staff.BottomY(s.Bounds.CenterX);
        bool Fragment(MusicSymbol s) => s.Kind is SymbolKind.Text or SymbolKind.Unknown or SymbolKind.Dot || TinyDynamic(s, staff);
        // outside the time signature zone the zigzag also reads as a "1" and the hook as a C or an articulation
        bool LooseDigit(MusicSymbol s) => s.Kind == SymbolKind.TimeSignature && !s.Attributes.ContainsKey("timezone");
        var tops = syms.Where(s => (Fragment(s) || LooseDigit(s)) && InStaff(s) && s.Bounds.Width >= 0.4 * ss && s.Bounds.Width <= 1.1 * ss && s.Bounds.Height >= 1.4 * ss && s.Bounds.Height <= 2.4 * ss).ToList();
        foreach (var t in tops)
        {
            if (!syms.Contains(t)) continue;
            var hook = syms.FirstOrDefault(s => s != t && (Fragment(s) || LooseDigit(s) || s.Kind == SymbolKind.Articulation)
                                                && s.Bounds.Width <= 1.0 * ss && s.Bounds.Height >= 0.4 * ss && s.Bounds.Height <= 1.3 * ss
                                                && s.Bounds.Y0 >= t.Bounds.Y1 - 0.5 * ss && s.Bounds.Y0 <= t.Bounds.Y1 + 0.2 * ss
                                                && s.Bounds.X1 >= t.Bounds.X0 && s.Bounds.X0 <= t.Bounds.CenterX);
            if (hook == null) continue;
            var u = t.Bounds.Union(hook.Bounds);
            if (u.Height < 2.2 * ss || u.Height > 3.3 * ss || u.Width > 1.2 * ss) continue;
            var rest = new MusicSymbol { Kind = SymbolKind.Rest, Name = "restQuarter", Duration = "quarter", Staff = staff, Confidence = 0.7, Bounds = u };
            rest.Attributes["pieces"] = "zigzag+hook";
            syms.Remove(t); syms.Remove(hook); syms.Add(rest);
        }
    }

    /// <summary>
    /// A worn quarter rest the classifier gave up on (read as text, or as an unsure "p"): one piece of a quarter rest's size around the
    /// middle of the staff whose ink matches the quarter rest template.
    /// </summary>
    private void QuarterRestsByShape(List<MusicSymbol> syms, Staff staff)
    {
        if (_lib == null) return;
        double ss = staff.Space;
        foreach (var t in syms.Where(s => (s.Kind is SymbolKind.Text or SymbolKind.Unknown || s.Kind == SymbolKind.Dynamic && s.Confidence < 0.8) && s.Bounds.Width >= 0.6 * ss && s.Bounds.Width <= 1.2 * ss
                                          && s.Bounds.Height >= 2.0 * ss && s.Bounds.Height <= 3.0 * ss).ToList())
        {
            var r = t.Bounds;
            if (Math.Abs(r.CenterY - staff.Lines[2].YAt(r.CenterX)) > 0.6 * ss) continue;
            int w = r.Width, h = r.Height;
            var mask = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (_ns[r.X0 + x, r.Y0 + y]) mask[y * w + x] = 1;
            var m = _lib.Match(mask, w, h, ss, 0.4, g => g.Kind == SymbolKind.Rest);
            // a clear winner: the 8th and 16th rests match a worn quarter rest half as well
            if (m.Count == 0 || m[0].Template.Name != "restQuarter" || m[0].Score < 0.6 || m.Count > 1 && m[1].Score > m[0].Score - 0.1) continue;
            t.Kind = SymbolKind.Rest; t.Name = "restQuarter"; t.Duration = "quarter"; t.Confidence = Math.Round(m[0].Score, 2);
            t.Attributes["rest"] = "by shape";
        }
    }

    /// <summary>A "dynamic" much smaller than a letter, inside the staff: a fragment (the stroke of a rest cut by a staff line).</summary>
    private static bool TinyDynamic(MusicSymbol s, Staff staff) => s.Kind == SymbolKind.Dynamic && s.Bounds.Height <= 1.3 * staff.Space && s.Bounds.Width <= 0.6 * staff.Space
        && s.Bounds.CenterY > staff.TopY(s.Bounds.CenterX) && s.Bounds.CenterY < staff.BottomY(s.Bounds.CenterX);

    /// <summary>Unknown fragments are merged with close neighbours and matched again (F clef + dots, broken glyphs).</summary>
    private void MergeAndRematch(List<(MusicSymbol sym, Component comp)> items, Staff staff)
    {
        // (the "cnn" attribute is a --verbose note only: it must not change which text counts as unknown)
        if (_lib == null && _clf == null) return;
        double ss = staff.Space;
        var unknown = items.Where(p => p.sym.Kind == SymbolKind.Unknown || (p.sym.Kind == SymbolKind.Text && p.sym.Attributes.Keys.All(k => k == "cnn"))
                                    || (p.sym.Kind is SymbolKind.Flag or SymbolKind.Articulation or SymbolKind.Breath or SymbolKind.Ornament && p.sym.Confidence < 0.85)
                                    || TinyDynamic(p.sym, staff)).ToList();
        foreach (var u in unknown)
        {
            if (!items.Contains(u)) continue;
            var r = u.sym.Bounds;
            var neighbours = items.Where(p => p != u && (p.sym.Kind is SymbolKind.Unknown or SymbolKind.Dot or SymbolKind.Text or SymbolKind.Ledger or SymbolKind.Articulation || TinyDynamic(p.sym, staff))
                                     && p.comp.X0 <= r.X1 + 0.35 * ss && p.comp.X1 >= r.X0 - 0.35 * ss
                                     && p.comp.Y0 <= r.Y1 + 0.5 * ss && p.comp.Y1 >= r.Y0 - 0.5 * ss)
                                  .OrderBy(p => Math.Abs(p.comp.CenterY - r.CenterY)).Take(3).ToList();
            if (neighbours.Count == 0) continue;
            // try the union of the fragment with 1..n nearest neighbours
            var group = new List<(MusicSymbol sym, Component comp)> { u };
            GlyphMatch? bestMatch = null; GlyphClassifier.Prediction? bestPred = null; List<(MusicSymbol sym, Component comp)>? bestGroup = null;
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
                if (_clf != null)
                {
                    var pr = _clf.Classify(mask, w, h, ss);
                    if (pr.Probability >= 0.7 && pr.Probability > u.sym.Confidence + 0.05 && pr.Label is not ("fragment" or "composite" or "other" or "line" or "text" or "slur" or "beam")
                        && (bestPred == null || pr.Probability > bestPred.Probability))
                    { bestPred = pr; bestGroup = new List<(MusicSymbol, Component)>(group); }
                    continue;
                }
                var matches = _lib.Match(mask, w, h, ss);
                if (matches.Count > 0 && matches[0].Template.Name == "unpitchedPercussionClef1" && matches[0].Score < 0.82) matches.RemoveAt(0);
                if (matches.Count == 0) continue;
                var m = matches[0];
                if (m.Score >= Threshold(m.Template.Kind) + 0.02 && m.Score > u.sym.Confidence + 0.02 && (bestMatch == null || m.Score > bestMatch.Score))
                {
                    bestMatch = m; bestGroup = new List<(MusicSymbol, Component)>(group);
                }
            }
            if ((bestMatch == null && bestPred == null) || bestGroup == null) continue;
            var cs = bestGroup.Select(p => p.comp).ToList();
            var merged = new MusicSymbol
            {
                Staff = staff,
                Bounds = new Rect(cs.Min(c => c.X0), cs.Min(c => c.Y0), cs.Max(c => c.X1), cs.Max(c => c.Y1)),
            };
            merged.Attributes["merged"] = bestGroup.Count.ToString();
            var fake = new Component { X0 = merged.Bounds.X0, Y0 = merged.Bounds.Y0, X1 = merged.Bounds.X1, Y1 = merged.Bounds.Y1, PixelCount = cs.Sum(c => c.PixelCount), Label = cs[0].Label };
            if (bestPred != null)
            {
                if (!ApplyPrediction(merged, fake, staff, bestPred)) continue;
            }
            else
            {
                merged.Kind = bestMatch!.Template.Kind; merged.Name = bestMatch.Template.Name; merged.Confidence = Math.Round(bestMatch.Score, 2);
                PostProcessTemplate(merged, fake, staff, false);
            }
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

    /// <summary>Isolated heads (template or hollow detection) get their stem back when a thin vertical component ends at their side.</summary>
    private void AttachDetachedStems(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        var heads = syms.Where(s => s.Kind == SymbolKind.Note && s.StemUp == null && s.Notes.Count == 1 && s.Notes[0].HeadType is "half" or "black").ToList();
        if (heads.Count == 0) return;
        var stems = syms.Where(s => s.Kind is SymbolKind.Unknown or SymbolKind.Text or SymbolKind.Ledger && s.Bounds.Width <= 0.35 * ss && s.Bounds.Height >= 1.8 * ss).ToList();
        foreach (var h in heads)
        {
            var r = h.Notes[0].Head;
            MusicSymbol? up = stems.FirstOrDefault(st => st.Bounds.X0 >= r.X1 - 0.4 * ss && st.Bounds.X0 <= r.X1 + 0.3 * ss && st.Bounds.Y1 >= r.Y0 - 0.3 * ss && st.Bounds.Y1 <= r.Y1 + 0.3 * ss && st.Bounds.Y0 < r.Y0);
            MusicSymbol? down = up != null ? null : stems.FirstOrDefault(st => st.Bounds.X1 <= r.X0 + 0.4 * ss && st.Bounds.X1 >= r.X0 - 0.3 * ss && st.Bounds.Y0 <= r.Y1 + 0.3 * ss && st.Bounds.Y0 >= r.Y0 - 0.3 * ss && st.Bounds.Y1 > r.Y1);
            var stem = up ?? down;
            if (stem == null) continue;
            h.StemUp = up != null;
            h.Duration = h.Notes[0].HeadType == "half" ? "half" : "quarter";
            h.Attributes["stemx"] = $"{stem.Bounds.X0},{stem.Bounds.X1},{(up != null ? stem.Bounds.Y0 : stem.Bounds.Y1)}";
            h.Attributes["stem"] = "reattached";
            h.Bounds = h.Bounds.Union(stem.Bounds);
            syms.Remove(stem); stems.Remove(stem);
            // other stemless heads along the same stem form a chord with it
            foreach (var o in heads.Where(o => o != h && syms.Contains(o) && o.StemUp == null).ToList())
            {
                var ro = o.Notes[0].Head;
                bool beside = up != null ? ro.X1 >= stem.Bounds.X0 - 0.4 * ss && ro.X1 <= stem.Bounds.X1 + 0.3 * ss
                                         : ro.X0 <= stem.Bounds.X1 + 0.4 * ss && ro.X0 >= stem.Bounds.X0 - 0.3 * ss;
                if (!beside || ro.CenterY < stem.Bounds.Y0 - 0.6 * ss || ro.CenterY > stem.Bounds.Y1 + 0.6 * ss) continue;
                h.Notes.Add(o.Notes[0]);
                h.Kind = SymbolKind.Chord; h.Name = "chord";
                h.Bounds = h.Bounds.Union(o.Bounds);
                syms.Remove(o);
            }
            if (h.Notes.Count > 1) h.Notes.Sort((a, b) => b.Step.CompareTo(a.Step));
        }
    }

    /// <summary>
    /// One head read twice, by a chord with its stem one way and by a note of the other voice with its stem the other way,
    /// a step apart (the two stems place it slightly differently): it is the single note's, not the chord's (a real unison
    /// is read at the same step and stays).
    /// </summary>
    private static void ChordHeadsOfOtherVoice(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        var owners = syms.Where(s => s.Kind is SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup)
                         .SelectMany(s => s.Kind == SymbolKind.NoteGroup && s.Children.Count > 0 ? s.Children : new List<MusicSymbol> { s })
                         .Where(o => o.StemUp != null && o.Notes.Count > 0).ToList();
        foreach (var chord in owners.Where(o => o.Notes.Count >= 2))
            foreach (var h in chord.Notes.ToList())
            {
                if (chord.Notes.Count < 2) break;
                bool other = owners.Any(o => o != chord && o.StemUp == !chord.StemUp && o.Notes.Count == 1
                                             && Math.Abs(o.Notes[0].HeadX - h.HeadX) <= 0.4 * ss && Math.Abs(o.Notes[0].HeadY - h.HeadY) <= 0.4 * ss
                                             && Math.Abs(o.Notes[0].Step - h.Step) == 1);
                if (other) chord.Notes.Remove(h);
            }
    }

    /// <summary>
    /// A stemless head (a hollow head that broke off its stem and was classified on its own) that coincides with a head
    /// of a stemmed note or chord is the same head seen twice: the stemless copy goes.
    /// </summary>
    private static void RemoveDuplicateHeads(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        var stemmed = syms.Where(s => s.Kind is SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup && (s.StemUp != null || s.Kind == SymbolKind.NoteGroup))
                          .SelectMany(s => s.Notes).ToList();
        if (stemmed.Count == 0) return;
        foreach (var h in syms.Where(s => s.Kind == SymbolKind.Note && s.StemUp == null && s.Notes.Count == 1).ToList())
        {
            var n = h.Notes[0];
            // the stem's head finder may place a broken-off head a little beside the real one: touching heads on one step
            if (stemmed.Any(m => Math.Abs(m.HeadY - n.HeadY) <= 0.5 * ss && (Math.Abs(m.HeadX - n.HeadX) <= 0.7 * ss
                                 || Math.Abs(m.HeadX - n.HeadX) <= 1.2 * ss && n.Head.X1 >= m.Head.X0 - 0.2 * ss && n.Head.X0 <= m.Head.X1 + 0.2 * ss))) syms.Remove(h);
        }
    }

    /// <summary>
    /// A stem broken at a chord's head into two symbols: a single note whose stem ends at that head, and a "chord" the
    /// other way whose short stem only joins that head to the chord's other heads beyond it. They are one chord with the
    /// single note's stem and flags.
    /// </summary>
    private void MergeBrokenStemChords(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        static bool Same(Note a, Note b, double ss) => Math.Abs(a.HeadY - b.HeadY) <= 0.5 * ss && Math.Abs(a.HeadX - b.HeadX) <= 0.5 * ss;
        foreach (var one in syms.Where(s => s.Kind == SymbolKind.Note && s.StemUp != null && s.Notes.Count == 1).ToList())
        {
            var n = one.Notes[0];
            bool up = one.StemUp == true;
            var chord = syms.FirstOrDefault(s => s != one && s.Kind == SymbolKind.Chord && s.StemUp == !up && s.Flags == 0 && s.Notes.Count >= 2
                                                 && s.Notes.Any(m => Same(m, n, ss))
                                                 && s.Notes.Where(m => !Same(m, n, ss)).All(m => up ? m.HeadY > n.HeadY + 0.5 * ss : m.HeadY < n.HeadY - 0.5 * ss));
            if (chord == null) continue;
            one.Notes.Clear();
            one.Notes.AddRange(chord.Notes.OrderByDescending(m => m.Step));
            one.Kind = SymbolKind.Chord; one.Name = "chord";
            one.Bounds = one.Bounds.Union(chord.Bounds);
            one.Attributes["stem"] = "broken, chord joined";
            syms.Remove(chord);
        }
    }

    /// <summary>
    /// A black head without a stem that lies inside the span of a beamed group (its stem broke off the beam) becomes
    /// a member of that group with the group's beam count.
    /// </summary>
    private void AttachDetachedHeads(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        var heads = syms.Where(s => s.Kind == SymbolKind.Note && s.StemUp == null && s.Notes.Count == 1 && s.Notes[0].HeadType == "black").ToList();
        if (heads.Count == 0) return;
        var groups = syms.Where(s => s.Kind == SymbolKind.NoteGroup && s.Children.Count >= 2 && s.Children.Any(ch => ch.Flags > 0)).ToList();
        foreach (var h in heads)
        {
            var r = h.Notes[0].Head;
            var g = groups.FirstOrDefault(gr => r.CenterX >= gr.Bounds.X0 - 1.0 * ss && r.CenterX <= gr.Bounds.X1 + 1.0 * ss
                                                && r.CenterY >= gr.Bounds.Y0 - 1.0 * ss && r.CenterY <= gr.Bounds.Y1 + 1.0 * ss
                                                && !gr.Notes.Any(n => Math.Abs(n.HeadX - r.CenterX) < 0.6 * ss && Math.Abs(n.HeadY - r.CenterY) < 0.6 * ss));
            if (g == null) continue;
            int flags = g.Children.Where(ch => ch.Flags > 0).Select(ch => ch.Flags).GroupBy(f => f).OrderByDescending(gr => gr.Count()).First().Key;
            bool up = g.Children.Count(ch => ch.StemUp == true) > g.Children.Count / 2;
            var child = new MusicSymbol { Kind = SymbolKind.Note, Name = "note", Bounds = r, Staff = staff, Confidence = 0.6, StemUp = up, Flags = flags };
            child.Duration = flags switch { 1 => "8th", 2 => "16th", 3 => "32nd", _ => "64th" };
            child.Notes.Add(h.Notes[0]);
            child.Attributes["stem"] = "missing, attached to group";
            int at = g.Children.FindIndex(ch => ch.Bounds.CenterX > r.CenterX);
            if (at < 0) g.Children.Add(child); else g.Children.Insert(at, child);
            g.Notes.Add(h.Notes[0]);
            g.Bounds = g.Bounds.Union(r);
            syms.Remove(h);
        }
    }

    /// <summary>
    /// Notes under a beam whose stems stop a little short of it (a few pixels missing) are pieces of their own (head
    /// plus stem stub, too short for a stem) and come out as text. A piece hanging just under (stem up) or over the
    /// beam of a beamed note or group, with a note head in it, becomes a note of that group with the beam count of
    /// its nearest neighbour; a single beamed note becomes a group.
    /// </summary>
    private void LinkFragmentsUnderBeams(List<MusicSymbol> syms, Staff staff)
    {
        if (_heads == null) return;
        double ss = staff.Space;
        static (int x0, int x1, int end)? StemOf(MusicSymbol s)
        {
            if (!s.Attributes.TryGetValue("stemx", out var v)) return null;
            var p = v.Split(',');
            return (int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]));
        }
        // a group's direction is that of its notes (the group symbol itself often carries none)
        static bool? Dir(MusicSymbol s) => s.Kind == SymbolKind.NoteGroup
            ? (s.Children.Count(c => c.StemUp == true) * 2 > s.Children.Count ? true : s.Children.Count(c => c.StemUp == false) * 2 > s.Children.Count ? false : null)
            : s.StemUp;
        var beamed = syms.Where(s => Dir(s) != null && (s.Kind is SymbolKind.Note or SymbolKind.Chord && s.Flags > 0 && StemOf(s) != null
                                     || s.Kind == SymbolKind.NoteGroup && s.Children.Count > 0 && s.Children.All(c => StemOf(c) != null))).ToList();
        foreach (var b in beamed)
        {
            bool up = Dir(b) == true;
            var members = b.Kind == SymbolKind.NoteGroup ? b.Children.ToList() : new List<MusicSymbol> { b };
            var ends = members.Select(m => (x: (StemOf(m)!.Value.x0 + StemOf(m)!.Value.x1) / 2.0, y: (double)StemOf(m)!.Value.end)).OrderBy(e => e.x).ToList();
            double slope = ends.Count >= 2 && ends[^1].x > ends[0].x ? (ends[^1].y - ends[0].y) / (ends[^1].x - ends[0].x) : 0;
            double BeamAt(double x) => ends[0].y + slope * (x - ends[0].x);
            // text, unknown, and "time signatures" outside the signature zone (a head touching an accidental)
            // (also articulations inside the staff: a head with a stem stub taken for a bowing sign)
            var pieces = syms.Where(s => (s.Kind is SymbolKind.Text or SymbolKind.Unknown || s.Kind == SymbolKind.TimeSignature && !s.Attributes.ContainsKey("timezone")
                                          || s.Kind is SymbolKind.Articulation or SymbolKind.Ornament && s.Bounds.CenterY > staff.TopY(s.Bounds.CenterX) && s.Bounds.CenterY < staff.BottomY(s.Bounds.CenterX)) && s != b
                                         && s.Bounds.X1 > b.Bounds.X0 - 0.5 * ss && s.Bounds.X0 < b.Bounds.X1 + 0.5 * ss
                                         && !members.Any(m => Math.Abs((StemOf(m)!.Value.x0 + StemOf(m)!.Value.x1) / 2.0 - s.Bounds.CenterX) < 0.8 * ss)
                                         && s.Bounds.Height >= 1.0 * ss && s.Bounds.Height <= 4.5 * ss && s.Bounds.Width <= 2.0 * ss
                                         && (up ? s.Bounds.Y0 - BeamAt(s.Bounds.CenterX) : BeamAt(s.Bounds.CenterX) - s.Bounds.Y1) is double gap
                                         && gap >= 0 && gap <= 7.0 * ss).ToList();
            double nearGap = 1.0 * ss + 0.6 * ss * Math.Max(1, b.Flags);
            // separate notes read as quarters standing under the beam, their stems pointing at it and ending short of it
            // (also notes with a "flag": a broken-off piece of this beam counted at their own stem)
            var quarters = syms.Where(s => s != b && s.Kind is SymbolKind.Note or SymbolKind.Chord && (s.Flags == 0 || s.Flags > 0 && b.Kind != SymbolKind.NoteGroup)
                                           && s.StemUp == up && StemOf(s) is not null
                                           && s.Notes.All(n => n.HeadType == "black")
                                           && (StemOf(s)!.Value.x0 + StemOf(s)!.Value.x1) / 2.0 is double qx && qx > b.Bounds.X0 - 0.5 * ss && qx < b.Bounds.X1 + 0.5 * ss
                                           && !members.Any(m => Math.Abs((StemOf(m)!.Value.x0 + StemOf(m)!.Value.x1) / 2.0 - qx) < 0.8 * ss)
                                           && (up ? StemOf(s)!.Value.end - BeamAt(qx) : BeamAt(qx) - StemOf(s)!.Value.end) is double qg && qg >= -0.3 * ss && qg <= 3.0 * ss).ToList();
            if (pieces.Count == 0 && quarters.Count == 0) continue;
            var added = new List<MusicSymbol>();
            foreach (var q in quarters)
            {
                var nearest = members.OrderBy(m => Math.Abs(m.Bounds.CenterX - q.Bounds.CenterX)).First();
                var qs = StemOf(q)!.Value;
                // the beam must really run over this stem (a quarter right after the group stands under no beam)
                if (BeamsAt((qs.x0 + qs.x1) / 2, BeamAt((qs.x0 + qs.x1) / 2.0), up, ss) is not int over || over < 1) continue;
                q.Flags = BeamsAt((qs.x0 + qs.x1) / 2, BeamAt((qs.x0 + qs.x1) / 2.0), up, ss) is int bc and >= 1 ? bc : nearest.Flags;
                q.Duration = q.Flags switch { 1 => "8th", 2 => "16th", 3 => "32nd", _ => "64th" };
                q.Attributes["beam"] = "stem short of the beam";
                added.Add(q);
                syms.Remove(q);
            }
            foreach (var piece in pieces)
            {
                // the piece's own component, and the best head in it
                Component? comp = null;
                for (int y = piece.Bounds.Y0; y <= piece.Bounds.Y1 && comp == null; y++)
                    for (int x = piece.Bounds.X0; x <= piece.Bounds.X1 && comp == null; x++)
                    {
                        int label = _cm.Labels[y * _cm.Width + x];
                        if (label > 0 && label - 1 < _cm.Components.Count && _cm.Components[label - 1].Height >= 0.8 * piece.Bounds.Height) comp = _cm.Components[label - 1];
                    }
                if (comp == null) continue;
                var found = FindHeadInGap(comp, staff, piece.Bounds.X0, piece.Bounds.X1, up, BeamAt);
                if (found == null) continue;
                var (hx, hy, score, hollow) = found.Value;
                if (hollow) continue;
                // further from the beam (a long stretch of its stem belongs to the beam's piece): a stem must run from
                // the head to the beam
                double pieceGap = up ? piece.Bounds.Y0 - BeamAt(piece.Bounds.CenterX) : BeamAt(piece.Bounds.CenterX) - piece.Bounds.Y1;
                if (pieceGap > nearGap && !StemToBeam((int)Math.Round(hx), (int)Math.Round(hy), up, BeamAt(hx), ss)) continue;
                var nearest = members.Concat(added).OrderBy(m => Math.Abs(m.Bounds.CenterX - hx)).First();
                var head = new Note { Head = new Rect((int)Math.Round(hx - 0.59 * ss), (int)Math.Round(hy - 0.5 * ss), (int)Math.Round(hx + 0.59 * ss), (int)Math.Round(hy + 0.5 * ss)), HeadType = "black", Confidence = score };
                head.Step = (int)Math.Round(staff.StepAt(hx, hy));
                int sx = (int)Math.Round(up ? hx + 0.5 * ss : hx - 0.5 * ss);
                int pf = BeamsAt(sx, BeamAt(sx), up, ss) is int pc and >= 1 ? pc : nearest.Flags;
                var note = new MusicSymbol { Kind = SymbolKind.Note, Name = "note", Staff = staff, Bounds = piece.Bounds, Confidence = 0.6, StemUp = up, Flags = pf };
                note.Duration = note.Flags switch { 1 => "8th", 2 => "16th", 3 => "32nd", _ => "64th" };
                note.Notes.Add(head);
                note.Attributes["stemx"] = $"{sx},{sx},{(int)Math.Round(BeamAt(sx))}";
                note.Attributes["beam"] = "stem short of the beam";
                added.Add(note);
                syms.Remove(piece);
            }
            if (added.Count == 0) continue;
            if (b.Kind == SymbolKind.NoteGroup)
            {
                b.Children.AddRange(added);
                b.Children.Sort((p, q) => (p.Notes.Count > 0 ? p.Notes.Min(n => n.HeadX) : p.Bounds.CenterX).CompareTo(q.Notes.Count > 0 ? q.Notes.Min(n => n.HeadX) : q.Bounds.CenterX));
                foreach (var a in added) { b.Notes.AddRange(a.Notes); b.Bounds = b.Bounds.Union(a.Bounds); }
            }
            else
            {
                // a single beamed note becomes a group with the notes found under its beam
                var single = new MusicSymbol { Kind = b.Kind, Name = b.Name, Staff = b.Staff, Bounds = b.Bounds, Confidence = b.Confidence, StemUp = b.StemUp, Flags = b.Flags, Duration = b.Duration, Dots = b.Dots };
                single.Notes.AddRange(b.Notes);
                foreach (var kv in b.Attributes) single.Attributes[kv.Key] = kv.Value;
                b.Kind = SymbolKind.NoteGroup; b.Name = "beamed group";
                b.Children.Clear(); b.Children.Add(single); b.Children.AddRange(added);
                b.Children.Sort((p, q) => (p.Notes.Count > 0 ? p.Notes.Min(n => n.HeadX) : p.Bounds.CenterX).CompareTo(q.Notes.Count > 0 ? q.Notes.Min(n => n.HeadX) : q.Bounds.CenterX));
                foreach (var a in added) { b.Notes.AddRange(a.Notes); b.Bounds = b.Bounds.Union(a.Bounds); }
            }
        }
    }

    /// <summary>
    /// An accidental that touches a slur (or other ink) becomes part of a larger unknown or text piece. For every head
    /// without an accidental, the ink of such pieces in the accidental's place (left of the head, at its height) is
    /// cut out and given to the glyph classifier: a sure sharp, flat or natural is attached to the head.
    /// </summary>
    private void AccidentalsInUnknowns(List<MusicSymbol> syms, Staff staff)
    {
        if (_clf == null) return;
        double ss = staff.Space;
        var pieces = syms.Where(s => s.Kind is SymbolKind.Unknown or SymbolKind.Text or SymbolKind.Slur).ToList();
        if (pieces.Count == 0) return;
        var heads = syms.Where(s => s.Kind is SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup)
                        .SelectMany(s => s.Notes.Concat(s.Children.SelectMany(c => c.Notes))).Distinct().Where(h => h.Accidental == int.MinValue).ToList();
        foreach (var h in heads)
        {
            int wx0 = (int)Math.Round(h.Head.X0 - 1.5 * ss), wx1 = h.Head.X0 - 1;
            int wy0 = (int)Math.Round(h.HeadY - 1.8 * ss), wy1 = (int)Math.Round(h.HeadY + 1.8 * ss);
            var over = pieces.Where(p => p.Bounds.X0 <= wx1 && p.Bounds.X1 >= wx0 && p.Bounds.Y0 <= wy1 && p.Bounds.Y1 >= wy0).ToList();
            if (over.Count == 0) continue;
            // the components of those pieces, within the window
            var labels = new HashSet<int>();
            foreach (var p in over)
                for (int y = Math.Max(p.Bounds.Y0, wy0); y <= Math.Min(p.Bounds.Y1, wy1); y++)
                    for (int x = Math.Max(p.Bounds.X0, wx0); x <= Math.Min(p.Bounds.X1, wx1); x++)
                        if (x >= 0 && y >= 0 && x < _cm.Width && y < _cm.Height && _cm.Labels[y * _cm.Width + x] is int l && l > 0) labels.Add(l);
            int ix0 = int.MaxValue, iy0 = int.MaxValue, ix1 = -1, iy1 = -1;
            for (int y = Math.Max(0, wy0); y <= Math.Min(_cm.Height - 1, wy1); y++)
                for (int x = Math.Max(0, wx0); x <= Math.Min(_cm.Width - 1, wx1); x++)
                    if (labels.Contains(_cm.Labels[y * _cm.Width + x])) { ix0 = Math.Min(ix0, x); ix1 = Math.Max(ix1, x); iy0 = Math.Min(iy0, y); iy1 = Math.Max(iy1, y); }
            if (ix1 < 0) continue;
            // a slur through the window widens it: keep the columns around the accidental's vertical strokes (every
            // accidental has one at least 1.2 spaces long)
            int sx0 = int.MaxValue, sx1 = -1, sTop = int.MaxValue, sBot = -1;
            for (int x = ix0; x <= ix1; x++)
            {
                int run = 0, best = 0, bestEnd = 0;
                for (int y = iy0; y <= iy1; y++) { if (labels.Contains(_cm.Labels[y * _cm.Width + x])) { run++; if (run > best) { best = run; bestEnd = y; } } else run = 0; }
                if (best >= 1.2 * ss) { sx0 = Math.Min(sx0, x); sx1 = Math.Max(sx1, x); sTop = Math.Min(sTop, bestEnd - best + 1); sBot = Math.Max(sBot, bestEnd); }
            }
            if (sx1 < 0) continue;
            ix0 = Math.Max(ix0, (int)Math.Round(sx0 - 0.4 * ss)); ix1 = Math.Min(ix1, (int)Math.Round(sx1 + 0.55 * ss));
            // vertically the accidental's own strokes (a slur on top is cut away), with a little margin
            iy0 = Math.Max(0, (int)Math.Round(sTop - 0.2 * ss)); iy1 = Math.Min(_cm.Height - 1, (int)Math.Round(sBot + 0.2 * ss));
            // a slur running through the top or bottom of the crop: rows inked over nearly the full width are cut off
            int RowInk(int y) { int c = 0; for (int x = ix0; x <= ix1; x++) if (labels.Contains(_cm.Labels[y * _cm.Width + x])) c++; return c; }
            int fullRow = (int)Math.Round(0.75 * (ix1 - ix0 + 1));
            while (iy1 - iy0 > 1.3 * ss && RowInk(iy0) >= fullRow) iy0++;
            while (iy1 - iy0 > 1.3 * ss && RowInk(iy1) >= fullRow) iy1--;
            while (iy1 - iy0 > 1.3 * ss && RowInk(iy0) == 0) iy0++;
            int w = ix1 - ix0 + 1, hgt = iy1 - iy0 + 1;
            if (hgt < 1.3 * ss || w < 0.3 * ss || w > 1.5 * ss) continue;
            var mask = new byte[w * hgt];
            for (int y = iy0; y <= iy1; y++)
                for (int x = ix0; x <= ix1; x++)
                    if (labels.Contains(_cm.Labels[y * _cm.Width + x])) mask[(y - iy0) * w + (x - ix0)] = 1;
            var pr = _clf.Classify(mask, w, hgt, ss);
            if (!pr.Label.StartsWith("acc_") || pr.Probability < 0.6)
            {
                // the classifier hesitates over the bits of slur left in the crop: a clear template match decides
                if (_lib == null || pr.Label.StartsWith("head_") || pr.Label.StartsWith("rest_")) continue;
                var mt = _lib.Match(mask, w, hgt, ss, 0.35, t => t.Kind == SymbolKind.Accidental);
                if (mt.Count == 0 || mt[0].Score < 0.72) continue;
                string nm = mt[0].Template.Name;
                string lab = nm.Contains("Sharp") ? "acc_sharp" : nm.Contains("Flat") ? "acc_flat" : nm.Contains("Natural") ? "acc_natural" : "";
                if (lab.Length == 0 || nm.Contains("Double")) continue;
                pr = new GlyphClassifier.Prediction { Label = lab, Probability = (float)mt[0].Score };
            }
            // an old-style natural reads as a sharp: its left stroke stands clearly higher than its right one at both
            // ends (a sharp's strokes are about level); measured on all the ink there
            if (pr.Label == "acc_sharp" && NaturalStrokes(ix0, iy0, ix1, iy1, ss)) pr = new GlyphClassifier.Prediction { Label = "acc_natural", Probability = pr.Probability };
            // the accidental's centre must be at the head's height (flats sit a little higher)
            double acy = pr.Label == "acc_flat" ? sBot - 0.5 * ss : (sTop + sBot) / 2.0;
            if (Math.Abs(acy - h.HeadY) > 0.6 * ss) continue;
            h.Accidental = pr.Label switch { "acc_sharp" => 1, "acc_flat" => -1, _ => 0 };
            h.AccidentalName = pr.Label switch { "acc_sharp" => "accidentalSharp", "acc_flat" => "accidentalFlat", _ => "accidentalNatural" };
            h.AccidentalUnsure = pr.Probability < 0.75;
            // a piece that was nothing but the accidental is used up
            foreach (var p in over.Where(p => p.Kind != SymbolKind.Slur && p.Bounds.X0 >= wx0 - 2 && p.Bounds.X1 <= wx1 + 2).ToList()) { syms.Remove(p); pieces.Remove(p); }
        }
    }

    /// <summary>
    /// Note heads do not overlap. Two heads of different stems that cover each other without sitting on the same spot
    /// (the head two voices share) cannot both be real (a "head" found next to a real one, with the stroke of a flat
    /// as its stem): the one with less ink inside its outline goes, and a stem left without heads with it.
    /// </summary>
    private void DropOverlappingHeads(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        var owners = new List<(Note head, MusicSymbol sym, MusicSymbol? group)>();
        foreach (var s in syms)
        {
            if (s.Kind is SymbolKind.Note or SymbolKind.Chord) foreach (var n in s.Notes) owners.Add((n, s, null));
            else if (s.Kind == SymbolKind.NoteGroup) foreach (var c in s.Children) foreach (var n in c.Notes) owners.Add((n, c, s));
        }
        int Ink(Note n)
        {
            double cx = n.HeadX, cy = n.HeadY, rx = Math.Max(1, n.Head.Width / 2.0), ry = Math.Max(1, n.Head.Height / 2.0);
            int ink = 0;
            for (int y = n.Head.Y0; y <= n.Head.Y1; y++)
                for (int x = n.Head.X0; x <= n.Head.X1; x++)
                {
                    double dx = (x - cx) / rx, dy = (y - cy) / ry;
                    if (dx * dx + dy * dy <= 1 && x >= 0 && y >= 0 && x < _ns.Width && y < _ns.Height && _ns[x, y]) ink++;
                }
            return ink;
        }
        var gone = new HashSet<Note>();
        for (int i = 0; i < owners.Count; i++)
            for (int j = i + 1; j < owners.Count; j++)
            {
                var (a, sa, _) = owners[i]; var (b, sb, _) = owners[j];
                if (sa == sb || gone.Contains(a) || gone.Contains(b)) continue;
                double dx = Math.Abs(a.HeadX - b.HeadX), dy = Math.Abs(a.HeadY - b.HeadY);
                if (dx < 0.25 * ss && dy < 0.3 * ss)
                {
                    // one head shared by two voices has stems both ways; with stems the same way it is one note found
                    // twice (the next group's first note taken again at the end of a beam): the copy whose stem does
                    // not stand at the head's edge goes
                    if (sa.StemUp == null || sa.StemUp != sb.StemUp) continue;
                    double Off(MusicSymbol sym, Note h) => sym.Attributes.TryGetValue("stemx", out var v) && v.Split(',') is var pp
                        ? Math.Abs((int.Parse(pp[0]) + int.Parse(pp[1])) / 2.0 - (sym.StemUp == true ? h.Head.X1 : h.Head.X0)) : double.MaxValue;
                    var drop = Off(sa, a) > Off(sb, b) ? owners[i] : owners[j];
                    if (drop.sym.Attributes.ContainsKey("corrected") || drop.sym.Attributes.ContainsKey("added")) continue;
                    gone.Add(drop.head);
                    continue;
                }
                if (dx >= 0.8 * ss || dy >= 0.6 * ss) continue;          // side by side or a step apart: fine
                var loser = Ink(a) >= Ink(b) ? owners[j] : owners[i];
                if (loser.sym.Attributes.ContainsKey("corrected") || loser.sym.Attributes.ContainsKey("added")) continue;
                gone.Add(loser.head);
                if (_opt.Verbose) loser.sym.Attributes["overlap"] = "head removed (overlaps a stronger head)";
            }
        if (gone.Count == 0) return;
        foreach (var (head, sym, group) in owners.Where(o => gone.Contains(o.head)))
        {
            sym.Notes.Remove(head);
            group?.Notes.Remove(head);
            if (sym.Notes.Count > 0) continue;
            if (group != null)
            {
                group.Children.Remove(sym);
                if (group.Children.Count == 0) syms.Remove(group);
                else if (group.Children.Count == 1)
                {
                    // one note left: no group any more
                    var only = group.Children[0];
                    syms[syms.IndexOf(group)] = only;
                }
            }
            else syms.Remove(sym);
        }
    }

    /// <summary>The number of beams crossing a stem at x: separate ink runs in a narrow column, starting at the
    /// outer beam line and going towards the heads, as long as the gaps stay small (null when none).</summary>
    private int? BeamsAt(int x, double beamY, bool up, double ss)
    {
        // from a little outside the expected beam line (stem ends, and with them the line, can be off), counting only
        // strokes of a beam's thickness (a slur outside the beam is thinner)
        int dir = up ? 1 : -1, y = (int)Math.Round(beamY - dir * 1.0 * ss), end = (int)Math.Round(beamY + dir * 2.0 * ss);
        int runs = 0, gap = 0, len = 0; bool inRun = false;
        for (; up ? y <= end : y >= end; y += dir)
        {
            if (y < 0 || y >= _ns.Height) break;
            // a beam fills the columns on one side of the stem at least (both in the middle of a group, only the inner
            // side at its ends); the stem alone does not
            int step = (int)Math.Max(1, ss / 8);
            bool Ink(int dx) { int xx = x + dx * step; return xx >= 0 && xx < _ns.Width && _ns[xx, y]; }
            bool beam = Enumerable.Range(-5, 5).Count(Ink) >= 5 || Enumerable.Range(1, 5).Count(Ink) >= 5;
            if (beam) { if (!inRun) { inRun = true; len = 0; } if (++len == (int)Math.Max(2, Math.Round(0.25 * ss))) runs++; gap = 0; }
            else { inRun = false; if (runs > 0 && ++gap > 0.6 * ss) break; }
        }
        return runs == 0 ? null : Math.Min(runs, 4);
    }

    /// <summary>
    /// A note with one beam more than both neighbours shares that beam with one of them (16th 16th 8th, 8th 16th
    /// 16th); a broken stem lost it from the count. The beams are counted halfway between the stems, and the side
    /// where the extra beam really runs gets it.
    /// </summary>
    private void ResolveLoneBeamlets(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        static (int x0, int x1, int end)? StemOf(MusicSymbol s)
        {
            if (!s.Attributes.TryGetValue("stemx", out var v)) return null;
            var p = v.Split(',');
            return (int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]));
        }
        // beamed notes are no half notes: in a group with black heads a hollow one came out light (tremolo pairs have
        // hollow heads only and stay)
        foreach (var g in syms.Where(s => s.Kind == SymbolKind.NoteGroup && s.Children.Any(c => c.Notes.Count > 0 && c.Notes.All(n => n.HeadType == "black"))))
            foreach (var c in g.Children.Where(c => c.Notes.Count > 0 && c.Notes.All(n => n.HeadType == "half") && c.Flags >= 1))
            {
                foreach (var n in c.Notes) n.HeadType = "black";
                c.Duration = c.Flags switch { 1 => "8th", 2 => "16th", 3 => "32nd", _ => "64th" };
                c.Attributes.Remove("tremolo");
            }
        // a note in a beamed group has at least one beam (its stem stopped short of the beam: no count)
        // (no beam over its stem at all: it only touches the group's ink, a quarter standing after it)
        foreach (var g in syms.Where(s => s.Kind == SymbolKind.NoteGroup && s.Children.Count >= 2).ToList())
            foreach (var c in g.Children.Where(c => c.Flags == 0 && c.StemUp != null && c.Notes.All(n => n.HeadType == "black")).ToList())
            {
                int? counted = StemOf(c) is { } st ? BeamsAt((st.x0 + st.x1) / 2, st.end, c.StemUp == true, ss) : null;
                if (counted is int k && k >= 1)
                {
                    c.Flags = k;
                    c.Duration = c.Flags switch { 1 => "8th", 2 => "16th", 3 => "32nd", _ => "64th" };
                    continue;
                }
                bool atEnd = g.Children.IndexOf(c) is int ci && (ci == 0 || ci == g.Children.Count - 1);
                if (!atEnd) { c.Flags = 1; c.Duration = "8th"; continue; }
                g.Children.Remove(c);
                foreach (var n in c.Notes) g.Notes.Remove(n);
                c.Duration = "quarter";
                syms.Add(c);
                if (g.Children.Count == 1) syms[syms.IndexOf(g)] = g.Children[0];
            }
        // three or more beams counted at a stem: a bit of staff line or a slur next to the beams can add one; the
        // beams of a beam's thickness decide
        foreach (var g in syms.Where(s => s.Kind == SymbolKind.NoteGroup))
            foreach (var c in g.Children.Where(c => c.Flags >= 3 && StemOf(c) != null && c.StemUp != null))
            {
                var (cx0, cx1, cEnd) = StemOf(c)!.Value;
                if (BeamsAt((cx0 + cx1) / 2, cEnd, c.StemUp == true, ss) is int real && real >= 1 && real < c.Flags)
                {
                    c.Flags = real;
                    c.Duration = real switch { 1 => "8th", 2 => "16th", _ => "32nd" };
                }
            }
        foreach (var g in syms.Where(s => s.Kind == SymbolKind.NoteGroup && s.Children.Count >= 3))
        {
            var kids = g.Children.Where(c => StemOf(c) != null && c.StemUp != null).OrderBy(c => StemOf(c)!.Value.x0).ToList();
            for (int i = 1; i + 1 < kids.Count; i++)
            {
                var a = kids[i - 1]; var m = kids[i]; var b = kids[i + 1];
                if (m.Flags != a.Flags + 1 || a.Flags != b.Flags || a.StemUp != m.StemUp || b.StemUp != m.StemUp) continue;
                var (ax0, ax1, aEnd) = StemOf(a)!.Value; var (mx0, mx1, mEnd) = StemOf(m)!.Value; var (bx0, bx1, bEnd) = StemOf(b)!.Value;
                int Mid(int x0, int x1, int y0, int y1, out double y) { y = (y0 + y1) / 2.0; return (x0 + x1) / 2; }
                int la = Mid((ax0 + ax1) / 2, (mx0 + mx1) / 2, aEnd, mEnd, out double ya), lb = Mid((mx0 + mx1) / 2, (bx0 + bx1) / 2, mEnd, bEnd, out double yb);
                int na = BeamsAt(la, ya, m.StemUp == true, ss) ?? 0, nb = BeamsAt(lb, yb, m.StemUp == true, ss) ?? 0;
                var target = na > nb ? a : nb > na ? b : null;
                if (target == null) continue;
                target.Flags = m.Flags;
                target.Duration = target.Flags switch { 1 => "8th", 2 => "16th", 3 => "32nd", _ => "64th" };
                if (_opt.Verbose) target.Attributes["beam"] = "shares its neighbour's extra beam";
            }
        }
        // a beam that ends at a middle stem of a group belongs to that note too (16th 16th 8th: the second beam stops
        // at the second stem): counted right beside the stem on each side, the fuller side wins (a neighbour's
        // beamlet stops short of the stem)
        foreach (var g in syms.Where(s => s.Kind == SymbolKind.NoteGroup && s.Children.Count >= 3))
        {
            var kids = g.Children.Where(c => StemOf(c) != null && c.StemUp != null && c.Flags >= 1 && c.Flags <= 3).OrderBy(c => StemOf(c)!.Value.x0).ToList();
            if (kids.Count < 3 || kids.Any(c => c.StemUp != kids[0].StemUp)) continue;
            int off = (int)Math.Max(2, Math.Round(0.15 * ss));
            for (int i = 1; i + 1 < kids.Count; i++)
            {
                var (x0, x1, end) = StemOf(kids[i])!.Value;
                bool up = kids[i].StemUp == true;
                int n = Math.Max(BeamsAt(x0 - off, end, up, ss) ?? 0, BeamsAt(x1 + off, end, up, ss) ?? 0);
                if (n != kids[i].Flags + 1) continue;
                kids[i].Flags = n;
                kids[i].Duration = n switch { 1 => "8th", 2 => "16th", 3 => "32nd", _ => "64th" };
                if (_opt.Verbose) kids[i].Attributes["beam"] = "a beam ends at its stem";
            }
        }
    }

    /// <summary>
    /// "tr" above the staff (read as text, or as a dynamic p, pp, m): dynamics in a part stand below the staff, a
    /// trill above it. The ink of the symbol (both letters) is matched against the trill glyph.
    /// </summary>
    private void TrillsAboveStaff(List<MusicSymbol> syms, Staff staff)
    {
        if (_lib == null) return;
        double ss = staff.Space;
        foreach (var s in syms.Where(s => s.Kind is SymbolKind.Text or SymbolKind.Dynamic or SymbolKind.Unknown).ToList())
        {
            var r = s.Bounds;
            if (r.Y1 > staff.TopY(r.CenterX) + 0.3 * ss || r.Width < 0.8 * ss || r.Width > 3.0 * ss || r.Height < 0.7 * ss || r.Height > 2.2 * ss) continue;
            // all ink in the box (the t and the r may be separate pieces)
            int w = r.Width, h = r.Height;
            var mask = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (_ns[r.X0 + x, r.Y0 + y]) mask[y * w + x] = 1;
            var m = _lib.Match(mask, w, h, ss, 0.3, t => t.Name == "ornamentTrill");
            if (m.Count == 0 || m[0].Score < 0.6) continue;
            // a hollow head above the staff or a piece of slur is no trill
            if (_clf != null && _clf.Classify(mask, w, h, ss) is var pr && (pr.Label.StartsWith("head_") || pr.Label == "slur") && pr.Probability >= 0.5) continue;
            s.Kind = SymbolKind.Ornament; s.Name = "ornamentTrill"; s.Confidence = Math.Round(m[0].Score, 2);
        }
    }

    /// <summary>The repairs above can take every head away from a note: such notes (and empty groups) are dropped,
    /// a group left with one note becomes that note.</summary>
    private static void DropNotesWithoutHeads(List<MusicSymbol> syms)
    {
        static void OneHeadIsNote(MusicSymbol s) { if (s.Kind == SymbolKind.Chord && s.Notes.Count == 1) { s.Kind = SymbolKind.Note; s.Name = "note"; } }
        for (int i = syms.Count - 1; i >= 0; i--)
        {
            var s = syms[i];
            if (s.Kind is SymbolKind.Note or SymbolKind.Chord && s.Notes.Count == 0) { syms.RemoveAt(i); continue; }
            OneHeadIsNote(s);
            foreach (var c in s.Children) OneHeadIsNote(c);
            if (s.Kind != SymbolKind.NoteGroup) continue;
            s.Children.RemoveAll(c => c.Notes.Count == 0);
            if (s.Children.Count == 0) syms.RemoveAt(i);
            else if (s.Children.Count == 1 && !s.Attributes.ContainsKey("tuplet")) syms[i] = s.Children[0];
        }
    }

    /// <summary>
    /// One piece of ink is one symbol: a head found by a note (often through a gap in its stem) is not also a rest,
    /// text or unknown symbol of its own. Such a symbol centred on a used head is dropped.
    /// </summary>
    private static void DropSymbolsOnHeads(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        var heads = syms.Where(s => s.Kind is SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup)
                        .SelectMany(s => s.Notes.Concat(s.Children.SelectMany(c => c.Notes))).ToList();
        if (heads.Count == 0) return;
        syms.RemoveAll(s => s.Kind is SymbolKind.Rest or SymbolKind.Text or SymbolKind.Unknown && s.Bounds.Height <= 2.5 * ss && s.Bounds.Width <= 2.6 * ss
                            && !s.Attributes.ContainsKey("corrected") && !s.Attributes.ContainsKey("added")
                            && heads.Any(h => Math.Abs(h.HeadX - s.Bounds.CenterX) <= 0.5 * ss && Math.Abs(h.HeadY - s.Bounds.CenterY) <= 0.6 * ss
                                              && h.Head.X0 <= s.Bounds.X1 && h.Head.X1 >= s.Bounds.X0));
    }

    /// <summary>
    /// Every end of a beam stands on a stem. When the beam runs on past the outermost stem of its group and a separate
    /// note (read as a quarter, its stem broken a little short of the beam) stands right under that end with its stem
    /// pointing at the beam, the note belongs to the group: it joins it with the beam count of its neighbour.
    /// </summary>
    private void LinkNotesUnderBeamEnds(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        static (int x0, int x1, int end)? StemOf(MusicSymbol s)
        {
            if (!s.Attributes.TryGetValue("stemx", out var v)) return null;
            var p = v.Split(',');
            return (int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]));
        }
        var groups = syms.Where(s => s.Kind == SymbolKind.NoteGroup && s.Children.Count >= 2 && s.Children.All(c => c.Attributes.ContainsKey("stemx"))).ToList();
        foreach (var g in groups)
        {
            var kids = g.Children.OrderBy(c => StemOf(c)!.Value.x0).ToList();
            foreach (bool left in new[] { true, false })
            {
                var edge = left ? kids[0] : kids[^1];
                if (edge.Flags < 1 || edge.StemUp == null) continue;
                var (ex0, ex1, eEnd) = StemOf(edge)!.Value;
                // follow the beam's ink outward from the edge stem, along the line through the two outer stem ends
                var next = left ? kids[1] : kids[^2];
                var (nx0, _, nEnd) = StemOf(next)!.Value;
                double slope = nx0 != ex0 ? (double)(nEnd - eEnd) / (nx0 - ex0) : 0;
                // the beam may slope more than the line through the stem ends: follow its ink, re-centring on it column by column
                int band = Math.Max(2, (int)Math.Round(0.3 * ss)), dir = left ? -1 : 1, x = left ? ex0 - 1 : ex1 + 1, last = left ? ex0 : ex1, misses = 0;
                double yb = eEnd;
                while (Math.Abs(x - (left ? ex0 : ex1)) < 3 * ss && x > 0 && x < _ns.Width - 1)
                {
                    yb += slope * dir;
                    int sum = 0, n = 0;
                    for (int dy = -band; dy <= band; dy++) { int y = (int)Math.Round(yb) + dy; if (y >= 0 && y < _ns.Height && _ns[x, y]) { sum += y; n++; } }
                    // (by at most a pixel a column: a beam is straight)
                    if (n > 0) { yb += Math.Clamp((double)sum / n - yb, -1, 1); last = x; misses = 0; }
                    else if (++misses > 2) break;
                    x += dir;
                }
                double overhang = Math.Abs(last - (left ? ex0 : ex1));
                if (overhang < 0.8 * ss) continue;
                double beamY = yb;
                // a separate note whose stem stands under the beam's end, pointing at it, ending a little short of it
                var cand = syms.Where(s => s != g && s.Kind is SymbolKind.Note or SymbolKind.Chord && s.StemUp == edge.StemUp && StemOf(s) is not null)
                               .Select(s => (s, st: StemOf(s)!.Value))
                               .Where(t => ((t.st.x0 + t.st.x1) / 2.0 - last) * dir is double outward && outward >= -0.6 * ss && outward <= 1.2 * ss
                                           && (edge.StemUp == true ? t.st.end - beamY : beamY - t.st.end) is double gap && gap >= -0.3 * ss && gap <= 2.5 * ss)
                               .OrderBy(t => Math.Abs((t.st.x0 + t.st.x1) / 2.0 - last)).FirstOrDefault();
                if (cand.s == null) continue;
                var q = cand.s;
                // as many beams as reach this end (an 8th's single beam under a 16th pair's two)
                int beams = 0, run = 0, xs = last - dir * Math.Max(1, (int)(0.15 * ss));
                double yIn = edge.StemUp == true ? 1 : -1;
                for (int k = -band; k <= (int)((edge.Flags + 0.5) * 0.9 * ss); k++)
                {
                    int y = (int)Math.Round(yb + yIn * k);
                    bool on = y >= 0 && y < _ns.Height && _ns[xs, y];
                    if (on) run++;
                    else { if (run >= 0.2 * ss) beams++; run = 0; }
                }
                if (run >= 0.2 * ss) beams++;
                q.Flags = Math.Clamp(beams, 1, edge.Flags);
                q.Duration = q.Flags switch { 1 => "8th", 2 => "16th", 3 => "32nd", _ => "64th" };
                q.Attributes["beam"] = "joined at the beam end";
                syms.Remove(q);
                if (left) g.Children.Insert(0, q); else g.Children.Add(q);
                g.Notes.AddRange(q.Notes);
                g.Bounds = g.Bounds.Union(q.Bounds);
                kids = g.Children.OrderBy(c => StemOf(c)!.Value.x0).ToList();
            }
        }
    }

    /// <summary>
    /// A note read as a quarter (no flags) whose stem end lies on the beam of a neighbouring beamed note, with the
    /// beam's ink actually reaching its stem, is a member of that beam: its stem ink broke off the beam.
    /// </summary>
    private void ContinueBeams(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        var quarters = syms.Where(s => s.Kind is SymbolKind.Note or SymbolKind.Chord && s.Flags == 0 && s.StemUp.HasValue && s.Attributes.ContainsKey("stemx")
                                       && s.Notes.All(n => n.HeadType == "black")).ToList();
        if (quarters.Count == 0) return;
        var beamed = syms.Where(s => (s.Kind is SymbolKind.Note or SymbolKind.Chord && s.Flags > 0 && s.Attributes.ContainsKey("stemx"))
                                     || (s.Kind == SymbolKind.NoteGroup && s.Children.Any(c => c.Flags > 0 && c.Attributes.ContainsKey("stemx")))).ToList();
        foreach (var q in quarters)
        {
            var parts = q.Attributes["stemx"].Split(',');
            int qx0 = int.Parse(parts[0]), qx1 = int.Parse(parts[1]), qEnd = int.Parse(parts[2]);
            foreach (var b in beamed)
            {
                // the nearest beamed stem of the neighbour on the side facing this note
                var members = b.Kind == SymbolKind.NoteGroup ? b.Children.Where(c => c.Flags > 0 && c.Attributes.ContainsKey("stemx")).ToList() : new List<MusicSymbol> { b };
                MusicSymbol? best = null; int bestDist = int.MaxValue, bx = 0, bEnd = 0;
                foreach (var m in members)
                {
                    if (m.StemUp != q.StemUp) continue;
                    var mp = m.Attributes["stemx"].Split(',');
                    int mx0 = int.Parse(mp[0]), mx1 = int.Parse(mp[1]), mEnd = int.Parse(mp[2]);
                    int dist = mx0 > qx1 ? mx0 - qx1 : qx0 - mx1;
                    if (dist < 0 || dist > 2.5 * ss || Math.Abs(mEnd - qEnd) > 0.5 * ss) continue;
                    if (dist < bestDist) { bestDist = dist; best = m; bx = mx0 > qx1 ? mx0 : mx1; bEnd = mEnd; }
                }
                if (best == null) continue;
                // the beam's ink must run from the neighbour's stem to this stem at the beam level
                int from = Math.Min(bx, bx > qx1 ? qx1 : qx0), to = Math.Max(bx, bx > qx1 ? qx1 : qx0);
                int band = Math.Max(1, (int)Math.Round(0.25 * ss)), cols = 0, inked = 0;
                for (int x = from; x <= to; x++)
                {
                    cols++;
                    for (int dy = -band; dy <= band; dy++)
                    {
                        int y = (qEnd + bEnd) / 2 + dy;
                        if (x >= 0 && x < _ns.Width && y >= 0 && y < _ns.Height && _ns[x, y]) { inked++; break; }
                    }
                }
                if (cols == 0 || inked < 0.8 * cols) continue;
                int flags = best.Flags;
                q.Flags = flags;
                q.Duration = flags switch { 1 => "8th", 2 => "16th", 3 => "32nd", _ => "64th" };
                q.Attributes["beam"] = "continued from neighbour";
                break;
            }
        }
    }

    /// <summary>Flags that are not connected to their stem (thin engraving, scan gaps) are attached to the nearest stem end.</summary>
    private void AttachDetachedFlags(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        var notes = syms.Where(s => s.Kind is SymbolKind.Note or SymbolKind.Chord && s.Flags == 0 && s.StemUp.HasValue && s.Attributes.ContainsKey("stemx")).ToList();
        if (notes.Count == 0) return;
        // thin engravings leave the flag as a narrow spine or even a blob next to the stem end
        var cands = syms.Where(s => s.Kind == SymbolKind.Flag || ((s.Kind is SymbolKind.Unknown or SymbolKind.Text or SymbolKind.Breath or SymbolKind.Articulation or SymbolKind.Ornament)
                                     && s.Bounds.Height >= 0.9 * ss && s.Bounds.Height <= 3.6 * ss && s.Bounds.Width >= 0.12 * ss && s.Bounds.Width <= 1.6 * ss)
                                    || (s.Kind == SymbolKind.Dot && s.Bounds.Height >= 0.3 * ss)).ToList();
        foreach (var n in notes)
        {
            var parts = n.Attributes["stemx"].Split(',');
            int stemX1 = int.Parse(parts[1]);
            int freeEndY = int.Parse(parts[2]);
            var f = cands.Where(c => c.Bounds.X0 >= stemX1 - 0.25 * ss && c.Bounds.X0 <= stemX1 + 0.9 * ss
                                     && (n.StemUp == true ? c.Bounds.Y0 - freeEndY >= -0.5 * ss && c.Bounds.Y0 - freeEndY <= 1.5 * ss
                                                          : freeEndY - c.Bounds.Y1 >= -0.5 * ss && freeEndY - c.Bounds.Y1 <= 1.5 * ss)
                                     // a dot or a thin spine only counts when it hugs the stem end (within half a space)
                                     && (c.Kind == SymbolKind.Flag || c.Bounds.Width >= 0.35 * ss || c.Bounds.X0 - stemX1 <= 0.5 * ss))
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

    /// <summary>
    /// A "rest" squeezed against the left of a head that has an accidental, at the head's height, is a piece of that
    /// accidental (the stroke of a sharp or natural cut by the staff removal): rests keep their distance from notes.
    /// </summary>
    private static void DropRestsInAccidentals(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        var heads = syms.Where(s => s.Kind is SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup)
                        .SelectMany(s => s.Notes.Concat(s.Children.SelectMany(c => c.Notes))).Where(n => n.AccidentalName != null).ToList();
        if (heads.Count == 0) return;
        foreach (var r in syms.Where(s => s.Kind == SymbolKind.Rest && s.Duration is "quarter" or "8th" or "16th").ToList())
            if (heads.Any(h => h.Head.X0 - r.Bounds.X1 >= -0.3 * ss && h.Head.X0 - r.Bounds.X1 <= 1.0 * ss
                               && h.HeadY >= r.Bounds.Y0 && h.HeadY <= r.Bounds.Y1))
                syms.Remove(r);
    }

    private static readonly bool DotDump = Environment.GetEnvironmentVariable("PVOMR_DOTDUMP") == "1";
    private static readonly bool BarNumDump = Environment.GetEnvironmentVariable("PVOMR_BARNUMDUMP") == "1";

    /// <summary>
    /// Bar numbers the OCR missed (italic or bold numbers often are): the glyphs over the start of a system read digit
    /// by digit against the digit templates. Every glyph of the number must be a clear digit; a number followed by a
    /// dot or by more text is a movement heading ("2. Adagio"), and bar 1 is never numbered.
    /// </summary>
    private void ReadBarNumberDigits()
    {
        if (_lib == null || _lib.Digits.Count == 0) return;
        foreach (var sys in _page.Systems)
        {
            var st = sys.Staves[0];
            // an OCR reading is checked too: the OCR tends to drop the narrow leading 1 of numbers over 100
            int? ocr = st.BarNumber;
            double ss = st.Space;
            int y1 = (int)Math.Round(st.TopY(st.Left) - 0.6 * ss), y0 = (int)(y1 - 4.5 * ss);
            int x0 = (int)(st.Left - 1.5 * ss), x1 = (int)(st.Left + 8 * ss);
            var near = _cm.Components.Where(c => c.PixelCount > 0 && c.CenterX >= x0 && c.CenterX <= x1 + 6 * ss && c.CenterY >= y0 && c.Y1 <= y1 + 0.3 * ss).OrderBy(c => c.X0).ToList();
            var glyphs = near.Where(c => c.Height >= 0.8 * ss && c.Height <= 2.8 * ss && c.Width >= 0.15 * ss && c.Width <= 2.2 * ss).ToList();
            if (glyphs.Count == 0 || glyphs[0].X0 > x1) continue;
            // the first run of glyphs of one height on one baseline
            // (text on another line, such as a tempo word just above, may interleave from left to right: skipped)
            var run = new List<Component> { glyphs[0] };
            foreach (var g in glyphs.Skip(1))
            {
                var last = run[^1];
                if (Math.Abs(g.Y1 - last.Y1) > 0.35 * ss || g.Height > 1.35 * last.Height || g.Height < 0.74 * last.Height) continue;
                if (g.X0 - last.X1 > 0.7 * ss) break;
                run.Add(g);
            }
            if (run.Count > 4) continue;
            int rx1 = run[^1].X1, baseY = run.Max(c => c.Y1), hgt = run.Max(c => c.Height);
            // anything right after the number on its line (a dot, letters): a heading, not a bar number
            bool heading = near.Any(c => !run.Contains(c) && c.X0 > rx1 && c.X0 - rx1 <= 1.2 * ss && c.Y1 <= baseY + 0.3 * ss && c.Y0 >= baseY - 1.3 * hgt
                                         && (c.Width <= 0.45 * ss && c.Height <= 0.45 * ss && c.Y1 >= baseY - 0.3 * ss || c.Height >= 0.5 * hgt && c.Width <= 2.2 * ss));
            // a movement number is followed by its period ("2. Adagio"); a word starting with a digit-like letter is not one
            bool period = near.Any(c => !run.Contains(c) && c.X0 > rx1 && c.X0 - rx1 <= 0.6 * ss && c.Width <= 0.45 * ss && c.Height <= 0.45 * ss
                                        && c.Y1 >= baseY - 0.3 * ss && c.Y1 <= baseY + 0.3 * ss);
            var ranks = run.Select(c => _lib.RankDigits(_cm.Mask(c), c.Width, c.Height)).ToList();
            var digits = ranks.Select(r => (d: r[0].digit, s: r[0].score)).ToList();
            if (BarNumDump) Console.Error.WriteLine($"barnum staff {st.Index + 1}: {string.Join(" ", digits.Select(d => $"{d.d}:{d.s:0.00}"))}{(heading ? " heading" : "")}");
            if (digits.Any(d => d.d < 0 || d.s < 0.72) || digits[0].d == 0) continue;
            // a heading has a short movement number; three digits and more are a bar number with text after it
            if (heading && run.Count >= 3) heading = false;
            if (heading && ocr == null)
            {
                // a short number followed by a dot or a word: the movement number of a heading
                if (run.Count <= 2 && period)
                {
                    st.MovementStart = true;
                    // the heading's text line: glyphs following on the number's line, word gaps included
                    var line = new Rect(run.Min(c => c.X0), run.Min(c => c.Y0), rx1, baseY);
                    int lineTop = (int)(baseY - 1.6 * hgt), lineBot = (int)(baseY + 0.6 * hgt);
                    foreach (var c in _cm.Components.Where(c => c.PixelCount > 0 && c.X0 > line.X0 && c.Y0 >= lineTop && c.Y1 <= lineBot && c.Height <= 2.5 * hgt).OrderBy(c => c.X0))
                    {
                        if (c.X0 - line.X1 > 1.5 * ss) break;
                        line = line.Union(new Rect(c.X0, c.Y0, c.X1, c.Y1));
                    }
                    st.HeadingBounds = line;
                    _page.Notes.Add($"staff {st.Index + 1}: movement {string.Concat(digits.Select(d => d.d))} starts");
                }
                continue;
            }
            int n = int.Parse(string.Concat(digits.Select(d => d.d)));
            if (n < 2) continue;
            // similar digits (5/6/8, 3/8, 0/8 in italic numbers): one swapped digit within a small margin of the best
            void AddSwaps()
            {
                for (int i = 0; i < ranks.Count; i++)
                    foreach (var alt in ranks[i].Skip(1).Where(a => a.score >= ranks[i][0].score - 0.08))
                    {
                        var ds = digits.Select(d => d.d).ToArray(); ds[i] = alt.digit;
                        if (ds[0] == 0) continue;
                        int v = int.Parse(string.Concat(ds));
                        if (v >= 2 && v != st.BarNumber && !st.BarNumberAlternatives.Contains(v)) st.BarNumberAlternatives.Add(v);
                    }
            }
            if (ocr != null)
            {
                if (n == ocr) continue;
                // otherwise both readings (and the digit shapes' look-alikes) stay in play: the neighbouring systems decide
                if (!(n.ToString().Length > ocr.Value.ToString().Length && (n.ToString().EndsWith(ocr.Value.ToString()) || n.ToString().StartsWith(ocr.Value.ToString()))))
                {
                    if (!st.BarNumberAlternatives.Contains(n)) st.BarNumberAlternatives.Add(n);
                    AddSwaps();
                    continue;
                }
                _page.Notes.Add($"staff {st.Index + 1}: bar number {ocr} (OCR) read as {n} from the digit shapes");
            }
            st.BarNumber = n;
            st.BarNumberBounds = new Rect(run.Min(c => c.X0), run.Min(c => c.Y0), run.Max(c => c.X1), run.Max(c => c.Y1));
            AddSwaps();
            _page.Notes.Add($"staff {st.Index + 1}: bar number {n} (from digit shapes{(st.BarNumberAlternatives.Count > 0 ? ", or " + string.Join("/", st.BarNumberAlternatives) : "")})");
        }
    }

    /// <summary>
    /// A thin flag broken up by the staff removal leaves small fragments beside the free half of the stem. Such a
    /// fragment makes a lone black quarter a flag candidate: the duration check turns it into an 8th only when the
    /// measure then adds up.
    /// </summary>
    private void FlagFragments(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        var frags = syms.Where(s => s.Kind is SymbolKind.Unknown or SymbolKind.Text && s.Bounds.Width <= 1.0 * ss && s.Bounds.Height <= 1.5 * ss
                                    && s.Bounds.Width >= 2 && s.Bounds.Height >= 2).ToList();
        foreach (var n in syms.Where(s => s.Kind == SymbolKind.Note && s.Flags == 0 && s.StemUp.HasValue && s.Notes.Count == 1
                                          && s.Notes[0].HeadType == "black" && !s.Attributes.ContainsKey("flagcand")))
        {
            bool up = n.StemUp == true;
            int stemX0, stemX1, end;
            if (n.Attributes.TryGetValue("stemx", out var sx))
            {
                var p = sx.Split(',');
                stemX0 = int.Parse(p[0]); stemX1 = int.Parse(p[1]); end = int.Parse(p[2]);
            }
            else
            {
                // a template note: the stem is on the right edge going up, on the left edge going down
                int w = Math.Max(2, (int)Math.Round(0.15 * ss));
                stemX0 = up ? n.Bounds.X1 - w : n.Bounds.X0; stemX1 = stemX0 + w;
                end = up ? n.Bounds.Y0 : n.Bounds.Y1;
            }
            var head = n.Notes[0].Head;
            if (Math.Abs(end - n.Notes[0].HeadY) < 2.0 * ss) continue;
            // a flag drawn along the stem (a thin curve from the stem's end back up to the head; template notes and some
            // stems never had theirs examined) is ink right of the stem between head and end, as for a hairline flag
            {
                int ylo = Math.Max(0, up ? end : (int)Math.Round(head.Y1 + 0.2 * ss)), yhi = Math.Min(_cm.Height - 1, up ? (int)Math.Round(head.Y0 - 0.2 * ss) : end);
                int cx0 = Math.Max(0, stemX1 + 2), cx1 = Math.Min(_cm.Width - 1, (int)Math.Round(stemX1 + 1.0 * ss));
                if (yhi - ylo >= 0.8 * ss && cx1 > cx0)
                {
                    int cols = 0, inked = 0;
                    for (int x = cx0; x <= cx1; x++)
                    {
                        cols++;
                        for (int y = ylo; y <= yhi; y++) if (_cm.Labels[y * _cm.Width + x] > 0) { inked++; break; }
                    }
                    if (inked >= 0.4 * cols) { n.Attributes["flagcand"] = "1"; continue; }
                }
            }
            bool hit = frags.Any(f => f.Bounds.CenterX >= stemX0 && f.Bounds.CenterX <= stemX1 + 1.0 * ss
                                      && (up ? f.Bounds.Y0 >= end - 0.3 * ss && f.Bounds.Y1 <= end + 2.5 * ss && f.Bounds.Y1 < head.Y0
                                             : f.Bounds.Y1 <= end + 0.3 * ss && f.Bounds.Y0 >= end - 2.5 * ss && f.Bounds.Y0 > head.Y1));
            if (hit) n.Attributes["flagcand"] = "1";
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
        if (w <= 0.55 && h <= 0.55 && c.Fill > 0.55 && w >= 0.12 && h >= 0.12 && Math.Abs(w - h) < 0.3 && !OnStaffLine(c, staff))
        {
            sym.Kind = SymbolKind.Dot; sym.Name = "dot"; sym.Confidence = 0.8;
            return sym;
        }

        // 0a. clefs first: their long vertical strokes would otherwise be taken for stems
        if (_lib != null && h >= 2.6 && h <= 9.5 && w >= 1.0 && w <= 5)
        {
            bool atStart = c.X0 <= staff.Left + 3.5 * ss && h >= 2.6;
            Rect? unionBounds = null; List<Component>? unionParts = null;
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
                    unionBounds = new Rect(ux0, uy0, ux1, uy1);
                    unionParts = dots;
                }
            }
            if (_opt.Verbose && clefMatches.Count > 0) sym.Attributes["clefscore"] = $"{clefMatches[0].Template.Name} {clefMatches[0].Score:0.00}";
            if (clefMatches.Count > 0 && clefMatches[0].Template.Name == "unpitchedPercussionClef1" && clefMatches[0].Score < 0.82) clefMatches.RemoveAt(0);
            if (clefMatches.Count > 0 && clefMatches[0].Score >= (atStart ? 0.52 : 0.74))
            {
                if (unionBounds != null && unionParts != null)
                {
                    sym.Bounds = unionBounds.Value;
                    foreach (var o in unionParts) _consumed.Add(o);
                    sym.Attributes["merged"] = (unionParts.Count + 1).ToString();
                }
                sym.Kind = SymbolKind.Clef; sym.Name = clefMatches[0].Template.Name; sym.Confidence = Math.Round(clefMatches[0].Score, 2);
                PostProcessTemplate(sym, c, staff, false);
                return sym;
            }
        }
        // 0a2. stacked time signature digits that touch (6/8, 3/4 ...): one component spanning the staff height; split at the middle line
        if (_lib != null && h >= 3.4 && h <= 4.6 && w >= 0.8 && w <= 2.8 && c.X0 <= staff.Left + 12 * ss
            && Math.Abs(c.Y0 - staff.TopY(c.CenterX)) <= 0.6 * ss && Math.Abs(c.Y1 - staff.BottomY(c.CenterX)) <= 0.6 * ss)
        {
            var full = _cm.Mask(c);
            int split = (int)Math.Round(staff.Lines[2].YAt(c.CenterX)) - c.Y0;
            if (split > 2 && split < c.Height - 2)
            {
                string? Digit(byte[] mask, int y0, int y1)
                {
                    int x0 = int.MaxValue, x1 = -1, yy0 = int.MaxValue, yy1 = -1;
                    for (int y = y0; y < y1; y++) for (int x = 0; x < c.Width; x++) if (mask[y * c.Width + x] != 0) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); yy0 = Math.Min(yy0, y); yy1 = Math.Max(yy1, y); }
                    if (x1 < 0 || yy1 - yy0 < 0.8 * ss) return null;
                    int sw = x1 - x0 + 1, sh = yy1 - yy0 + 1;
                    var sub = new byte[sw * sh];
                    for (int y = yy0; y <= yy1; y++) for (int x = x0; x <= x1; x++) sub[(y - yy0) * sw + (x - x0)] = mask[y * c.Width + x];
                    var mm = _lib.Match(sub, sw, sh, ss, 0.4, t => t.Kind == SymbolKind.TimeSignature && t.Name.Length == 8);
                    return mm.Count > 0 && mm[0].Score >= 0.6 ? mm[0].Template.Name[7..] : null;
                }
                string? upper = Digit(full, 0, split), lower = Digit(full, split, c.Height);
                if (upper != null && lower != null)
                {
                    sym.Kind = SymbolKind.TimeSignature; sym.Name = "timeSigStack"; sym.Confidence = 0.7;
                    sym.Attributes["stack"] = $"{upper}/{lower}";
                    return sym;
                }
            }
        }

        // 0b. whole / half rests: a solid bar hanging below or sitting on a staff line
        if (w >= 0.8 && w <= 2.0 && h >= 0.4 && (h <= 0.8 && c.Fill > 0.75 || h <= 0.9 && c.Fill > 0.85))
        {
            double stepTop = staff.StepAt(c.CenterX, c.Y0), stepBot = staff.StepAt(c.CenterX, c.Y1 + 1);
            bool topOnLine = Math.Abs(stepTop - Math.Round(stepTop)) < 0.3 && ((int)Math.Round(stepTop)) % 2 == 0 && stepTop >= -0.5 && stepTop <= 8.5;
            bool botOnLine = Math.Abs(stepBot - Math.Round(stepBot)) < 0.3 && ((int)Math.Round(stepBot)) % 2 == 0 && stepBot >= -0.5 && stepBot <= 8.5;
            if (topOnLine && !botOnLine) { sym.Kind = SymbolKind.Rest; sym.Name = "restWhole"; sym.Duration = "whole"; sym.Confidence = 0.7; return sym; }
            if (botOnLine && !topOnLine) { sym.Kind = SymbolKind.Rest; sym.Name = "restHalf"; sym.Duration = "half"; sym.Confidence = 0.7; return sym; }
        }

        // 0c. accidental-sized components: sharps and naturals have vertical strokes that look like stems
        if (w >= 0.45 && w <= 1.4 && h >= 1.8 && h <= 3.4)
        {
            if (_clf != null)
            {
                var pa = _clf.Classify(_cm.Mask(c), c.Width, c.Height, ss);
                if (pa.Label.StartsWith("acc_") && pa.Probability >= 0.6 && ApplyPrediction(sym, c, staff, pa)) return sym;
            }
            else if (_lib != null)
            {
                var ma = _lib.Match(_cm.Mask(c), c.Width, c.Height, ss, 0.35, t => t.Kind == SymbolKind.Accidental);
                if (ma.Count > 0 && ma[0].Score >= 0.7)
                {
                    sym.Kind = SymbolKind.Accidental; sym.Name = ma[0].Template.Name; sym.Confidence = Math.Round(ma[0].Score, 2);
                    PostProcessTemplate(sym, c, staff, false);
                    return sym;
                }
            }
        }

        // 1. stems -> note structures (a component far taller than two staves is a bar line or bracket, never a note group)
        var stems = h <= 20 ? FindStems(c, staff) : new List<Stem>();
        if (stems.Count > 0)
        {
            int stemCount = stems.Count;   // the analysis drops stems without heads from the list
            var noteSym = AnalyzeNoteGroup(c, staff, stems);
            // touching heads of a chord can hold vertical runs that pass for short stems beside the real one and keep
            // the heads from it: again without stems lying within the reach of a longer stem beside them
            if (noteSym == null && stemCount > 1)
            {
                var fresh = FindStems(c, staff);
                var kept = fresh.Where(t => !fresh.Any(o => o != t && o.Bottom - o.Top > t.Bottom - t.Top && Math.Abs(o.X0 - t.X0) <= 1.5 * ss
                                                           && o.Top <= t.Top && o.Bottom >= t.Bottom)).ToList();
                if (kept.Count < fresh.Count && kept.Count > 0) noteSym = AnalyzeNoteGroup(c, staff, kept);
            }
            if (noteSym != null) return noteSym;
        }

        // 1b. isolated hollow heads (whole notes, half-note heads whose stem broke off), also on ledger lines
        if (stems.Count == 0 && w >= 0.85 && w <= 2.2 && h >= 0.75 && h <= 1.3)
        {
            var (fill, core, band, sideBand) = HollowStats(c, staff);
            double stepHollow = staff.StepAt(c.CenterX, c.CenterY);
            bool ledgerOk = stepHollow > -2.5 && stepHollow < 10.5 || HasLedgerLine(c.CenterX, c.CenterY, ss);
            if (ledgerOk && (band >= 0.45 || sideBand >= 0.5) && core <= 0.3 && fill >= 0.2 && fill <= 0.8
                && !ConfidentNonHead(c, ss))
            {
                var n = new Note { Head = sym.Bounds, HeadType = w >= 1.35 ? "whole" : "half" };
                n.Step = (int)Math.Round(staff.StepAt(n.HeadX, n.HeadY));
                sym.Kind = SymbolKind.Note; sym.Name = "note"; sym.Confidence = 0.7;
                sym.Notes.Add(n);
                sym.Duration = n.HeadType == "whole" ? "whole" : "half?";
                return sym;
            }
        }

        // 2a. trained classifier for isolated glyphs
        if (_clf != null && w <= 7 && h <= 9.5 && w >= 0.15 && h >= 0.15)
        {
            var p = _clf.Classify(_cm.Mask(c), c.Width, c.Height, ss);
            if (ApplyPrediction(sym, c, staff, p)) return sym;
            if (_opt.Verbose) sym.Attributes["cnn"] = $"{p.Label} {p.Probability:0.00}";
            if (p.Label.StartsWith("head_") && p.Probability >= 0.6) goto heuristics;   // a head rejected by the position prior: not a glyph either
            if (p.Label is "fragment" or "composite" or "other" && p.Probability >= 0.6)
            {
                if (TryTupletDigit(sym, c, staff)) return sym;   // the models were not trained on tuplet digits
                // on small staves the classifier is unsure about rests: a rest-shaped component inside the staff is still template matched
                bool restShaped = ss < 18 && w >= 0.6 && w <= 1.5 && h >= 2.2 && h <= 3.8 && c.CenterY > staff.TopY(c.CenterX) - 0.5 * ss && c.CenterY < staff.BottomY(c.CenterX) + 0.5 * ss;
                if (restShaped && _lib != null)
                {
                    var mr = _lib.Match(_cm.Mask(c), c.Width, c.Height, ss, 0.4, t => t.Kind == SymbolKind.Rest);
                    if (mr.Count > 0 && mr[0].Score >= 0.6)
                    {
                        sym.Kind = SymbolKind.Rest; sym.Name = mr[0].Template.Name; sym.Confidence = Math.Round(mr[0].Score, 2);
                        PostProcessTemplate(sym, c, staff, false);
                        return sym;
                    }
                }
                // trust the rejection: no template matching, straight to the shape heuristics
                goto heuristics;
            }
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
                // a bar-shaped match (whole/half rest, tenuto) must have the body of a rest to be one; thinner bars are slur pieces
                if (m.Score >= thr && m.Template.Name is "restWhole" or "restHalf" && !WholeHalfRestPlausible(c, staff)) thr = double.MaxValue;
                // a lone head well outside the staff needs a ledger line, as on the classifier path; text letters (o, c, e) do not have one
                if (m.Score >= thr && m.Template.Kind == SymbolKind.Note)
                {
                    double stepHere = staff.StepAt(c.CenterX, c.CenterY);
                    if ((stepHere < -2.5 || stepHere > 10.5) && !HasLedgerLine(c.CenterX, c.CenterY, ss)) thr = double.MaxValue;
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

        heuristics:
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

    /// <summary>Ink statistics of a component against its inscribed ellipse: overall fill, core fill (centre, ignoring a
    /// horizontal band for a ledger line) and outer band fill.</summary>
    private (double fill, double core, double band, double sideBand) HollowStats(Component c, Staff staff)
    {
        double cx = c.CenterX, cy = c.CenterY, rx = c.Width / 2.0, ry = c.Height / 2.0;
        int lineBand = (int)Math.Ceiling(staff.LineThickness) + 1;
        int n = 0, ink = 0, coreN = 0, coreInk = 0, bandN = 0, bandInk = 0, sideN = 0, sideInk = 0;
        for (int y = c.Y0; y <= c.Y1; y++)
            for (int x = c.X0; x <= c.X1; x++)
            {
                double dx = (x - cx) / rx, dy = (y - cy) / ry, r2 = dx * dx + dy * dy;
                if (r2 > 1.0) continue;
                bool on = _cm.Labels[y * _cm.Width + x] == c.Label;
                n++; if (on) ink++;
                if (r2 <= 0.2 && Math.Abs(y - cy) > lineBand) { coreN++; if (on) coreInk++; }
                if (r2 >= 0.45) { bandN++; if (on) bandInk++; if (Math.Abs(dx) > 0.55) { sideN++; if (on) sideInk++; } }
            }
        return (n == 0 ? 0 : ink / (double)n, coreN == 0 ? 1 : coreInk / (double)coreN, bandN == 0 ? 0 : bandInk / (double)bandN, sideN == 0 ? 0 : sideInk / (double)sideN);
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

    /// <summary>A small digit (about one staff space tall) outside the staff lines: a tuplet number if it matches the tuplet digit templates.</summary>
    private bool TryTupletDigit(MusicSymbol sym, Component c, Staff staff)
    {
        if (_lib == null) return false;
        double ss = staff.Space;
        double h = c.Height / ss, w = c.Width / ss;
        if (h < 0.7 || h > 1.7 || w < 0.4 || w > 1.3) return false;
        double cy = c.CenterY;
        if (cy > staff.TopY(c.CenterX) + 0.3 * ss && cy < staff.BottomY(c.CenterX) - 0.3 * ss) return false;   // inside the staff: fingering or text
        var m = _lib.Match(_cm.Mask(c), c.Width, c.Height, ss, 0.4, t => t.Kind == SymbolKind.Tuplet);
        if (m.Count == 0 || m[0].Score < 0.62) return false;
        sym.Kind = SymbolKind.Tuplet; sym.Name = m[0].Template.Name; sym.Confidence = Math.Round(m[0].Score, 2);
        sym.Attributes["tuplet"] = m[0].Template.Name[6..];
        return true;
    }

    /// <summary>
    /// A small "3" over (or under) separate notes, a triplet without a beam or bracket, is often read as text: a small
    /// glyph outside the staff, close to the stem ends of notes, that matches the 3 better than other digits.
    /// </summary>
    private void LooseTupletThrees(List<MusicSymbol> syms, Staff staff)
    {
        if (_lib == null) return;
        double ss = staff.Space;
        foreach (var t in syms.Where(s => (s.Kind is SymbolKind.Text or SymbolKind.Unknown && (!s.Attributes.TryGetValue("glyphs", out var g) || g == "1")
                                           || s.Kind == SymbolKind.Tuplet && s.Confidence < 0.8 && s.Name != "tuplet3")
                                          && s.Bounds.Height >= 0.6 * ss && s.Bounds.Height <= 1.4 * ss && s.Bounds.Width >= 0.3 * ss && s.Bounds.Width <= 1.1 * ss).ToList())
        {
            if (!syms.Contains(t)) continue;
            // a thin 3 breaks into pieces (one may even go to the neighbouring staff): the box of all ink close around it
            int pad = (int)Math.Round(0.4 * ss);
            int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = -1, by1 = -1;
            for (int y = Math.Max(0, t.Bounds.Y0 - pad); y <= Math.Min(_ns.Height - 1, t.Bounds.Y1 + pad); y++)
                for (int x = Math.Max(0, t.Bounds.X0 - pad); x <= Math.Min(_ns.Width - 1, t.Bounds.X1 + pad); x++)
                    if (_ns[x, y]) { bx0 = Math.Min(bx0, x); by0 = Math.Min(by0, y); bx1 = Math.Max(bx1, x); by1 = Math.Max(by1, y); }
            var r = bx1 < 0 ? t.Bounds : new Rect(bx0, by0, bx1, by1);
            if (r.Height > 1.6 * ss || r.Width > 1.3 * ss) r = t.Bounds;
            var pieces = syms.Where(s => s != t && s.Kind is SymbolKind.Text or SymbolKind.Unknown or SymbolKind.Dot
                                         && s.Bounds.X0 >= r.X0 && s.Bounds.X1 <= r.X1 && s.Bounds.Y0 >= r.Y0 && s.Bounds.Y1 <= r.Y1).ToList();
            bool above = r.Y1 < staff.TopY(r.CenterX) - 0.2 * ss, below = r.Y0 > staff.BottomY(r.CenterX) + 0.2 * ss;
            if (!above && !below) continue;
            // close to a note's stem end on that side
            if (!syms.Any(n => n.Kind is SymbolKind.Note or SymbolKind.Chord && n.Bounds.X0 <= r.X1 + 0.5 * ss && n.Bounds.X1 >= r.X0 - 0.5 * ss
                               && (above ? Math.Abs(n.Bounds.Y0 - r.Y1) <= 3.5 * ss : Math.Abs(r.Y0 - n.Bounds.Y1) <= 3.5 * ss))) continue;
            int w = r.Width, h = r.Height;
            var mask = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (_ns[r.X0 + x, r.Y0 + y]) mask[y * w + x] = 1;
            var m = _lib.Match(mask, w, h, ss, 0.5, g => g.Kind == SymbolKind.Tuplet || g.Kind == SymbolKind.TimeSignature && g.Name.Length == 8 && char.IsDigit(g.Name[7]));
            if (Environment.GetEnvironmentVariable("PVOMR_TUPDUMP") == "1") Console.Error.WriteLine($"TUP {r}: {string.Join(" ", m.Take(3).Select(x => $"{x.Template.Name} {x.Score:0.00}"))}");
            if (m.Count == 0 || m[0].Template.Name[^1] != '3' || m[0].Score < 0.5) continue;
            t.Kind = SymbolKind.Tuplet; t.Name = "tuplet3"; t.Confidence = Math.Round(m[0].Score, 2); t.Bounds = r;
            t.Attributes["tuplet"] = "3";
            t.Attributes["loose"] = "1";
            foreach (var p in pieces) syms.Remove(p);
        }
    }

    /// <summary>Tuplet digits are attached to the note group (or run of notes) they sit above or below.</summary>
    private void AttachTuplets(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        foreach (var t in syms.Where(s => s.Kind == SymbolKind.Tuplet).ToList())
        {
            // a digit of the bar number over the system start is no tuplet
            if (staff.BarNumberBounds is Rect bn && t.Bounds.X1 >= bn.X0 && t.Bounds.X0 <= bn.X1 && t.Bounds.Y1 >= bn.Y0 && t.Bounds.Y0 <= bn.Y1) continue;
            double cx = t.Bounds.CenterX;
            var group = syms.Where(s => s.Kind == SymbolKind.NoteGroup && cx >= s.Bounds.X0 - 0.5 * ss && cx <= s.Bounds.X1 + 0.5 * ss
                                        && (Math.Abs(t.Bounds.Y1 - s.Bounds.Y0) <= 3.0 * ss || Math.Abs(t.Bounds.Y0 - s.Bounds.Y1) <= 3.0 * ss))
                            .OrderBy(s => Math.Min(Math.Abs(t.Bounds.Y1 - s.Bounds.Y0), Math.Abs(t.Bounds.Y0 - s.Bounds.Y1))).FirstOrDefault();
            if (group == null)
            {
                // over separate notes: the assembler places it where it makes the measure add up
                if (t.Name == "tuplet3" || t.Attributes.GetValueOrDefault("tuplet") == "3") t.Attributes["loose"] = "1";
                continue;
            }
            // fingerings look the same: a tuplet digit sits on the beam side of the group and divides its note count
            string digit = t.Attributes.TryGetValue("tuplet", out var dv) ? dv : t.Name.StartsWith("tuplet") ? t.Name[6..] : "";
            if (!int.TryParse(digit, out int actual) || actual is not (3 or 5 or 6 or 7)) continue;
            int count = group.Children.Count;
            if (count < actual || count % actual != 0) continue;
            bool stemsUp = group.Children.Count(ch => ch.StemUp == true) > group.Children.Count / 2;
            bool above = t.Bounds.CenterY < group.Bounds.CenterY;
            if (stemsUp != above) continue;
            // several tuplets may sit over one beamed group (two triplets under one beam): keep every digit with its x position;
            // different digits over one group are fingerings, not tuplets
            if (group.Attributes.TryGetValue("tuplet", out var prev))
            {
                if (!prev.Split(';').All(sp => sp.Split('@')[0] == digit)) { group.Attributes.Remove("tuplet"); group.Attributes["fingering"] = "1"; syms.Remove(t); continue; }
                group.Attributes["tuplet"] = prev + ";" + digit + "@" + (int)cx;
            }
            else if (group.Attributes.ContainsKey("fingering")) { syms.Remove(t); continue; }
            else group.Attributes["tuplet"] = digit + "@" + (int)cx;
            syms.Remove(t);
        }
    }

    /// <summary>Rests recognised twice from a split glyph (two overlapping rest symbols of the same kind) are merged.</summary>
    private static void MergeDuplicateRests(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        var rests = syms.Where(s => s.Kind == SymbolKind.Rest).OrderBy(s => s.Bounds.X0).ToList();
        for (int i = 0; i < rests.Count; i++)
            for (int j = i + 1; j < rests.Count; j++)
            {
                var a = rests[i]; var b = rests[j];
                if (!syms.Contains(a) || !syms.Contains(b)) continue;
                if (b.Bounds.X0 > a.Bounds.X1 + 0.3 * ss) break;
                if (Math.Min(a.Bounds.Y1, b.Bounds.Y1) - Math.Max(a.Bounds.Y0, b.Bounds.Y0) < -0.3 * ss) continue;
                var keep = a.Confidence >= b.Confidence ? a : b; var drop = keep == a ? b : a;
                keep.Bounds = keep.Bounds.Union(drop.Bounds);
                syms.Remove(drop);
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
        public string Trace = "";
        public bool FlagCandidate;   // a hairline flag may be there (settled by the measure length)
        public bool Up;
        public int Flags;
        /// <summary>Order within a group; a head split off a shared stem sorts by its own x.</summary>
        public double SortX = double.NaN;
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
        var cols = new Dictionary<int, (int len, int top)>();   // longest vertical run per column
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
            cols[x] = (bestLen, bestTop);
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
        // a stem printed or scanned slightly slanted drifts across columns, so no single column holds a long run: where
        // no stem was found nearby, a run over two neighbouring columns of a thin stroke is one too
        for (int x = c.X0; x < c.X1; x++)
        {
            if (stems.Any(st => x >= st.X0 - 0.35 * ss && x <= st.X1 + 0.35 * ss)) continue;
            int bestLen = 0, bestTop = 0, run = 0, runTop = 0;
            for (int y = c.Y0; y <= c.Y1 + 1; y++)
            {
                bool on = y <= c.Y1 && (_cm.Labels[y * _cm.Width + x] == label || _cm.Labels[y * _cm.Width + x + 1] == label);
                if (on) { if (run == 0) runTop = y; run++; }
                else { if (run > bestLen) { bestLen = run; bestTop = runTop; } run = 0; }
            }
            if (bestLen < minLen || cols[x].len < 0.6 * ss || cols[x + 1].len < 0.6 * ss) continue;
            // thin along the run: rows inked wider than a stem (a head, a beam) may only make up its ends
            int wide = 0;
            for (int y = bestTop; y < bestTop + bestLen; y++)
            {
                int w = 0;
                for (int xx = Math.Max(c.X0, x - 3); xx <= Math.Min(c.X1, x + 4); xx++) if (_cm.Labels[y * _cm.Width + xx] == label) w++;
                if (w > 4) wide++;
            }
            if (bestLen - wide < minLen * 0.8) continue;
            stems.Add(new Stem { X = x, X0 = x, X1 = x + 1, Top = bestTop, Bottom = bestTop + bestLen - 1 });
        }
        stems.Sort((a, b) => a.X0.CompareTo(b.X0));
        // keep thin candidates only
        double maxW = Math.Max(3, 0.45 * ss);
        // a chord of heads a third apart (a triad) merges into a blob taller than the minimum stem length, so the stem
        // runs on into columns of head ink: the stem is then the thin set of columns whose run is clearly the longest
        for (int i = 0; i < stems.Count; i++)
        {
            var s = stems[i];
            if (s.X1 - s.X0 + 1 <= maxW) continue;
            var lens = Enumerable.Range(s.X0, s.X1 - s.X0 + 1).Select(x => cols[x].len).OrderBy(l => l).ToList();
            int blob = lens[lens.Count / 2];   // the typical column: head ink
            var top = Enumerable.Range(s.X0, s.X1 - s.X0 + 1).Where(x => cols[x].len >= blob + 1.0 * ss).ToList();
            if (top.Count == 0 || top.Max() - top.Min() + 1 > maxW || top.Max() - top.Min() + 1 != top.Count) continue;
            int bx = top.OrderByDescending(x => cols[x].len).First();
            stems[i] = new Stem { X = bx, X0 = top.Min(), X1 = top.Max(), Top = top.Min(x => cols[x].top), Bottom = top.Max(x => cols[x].top + cols[x].len - 1) };
        }
        return stems.Where(s => s.X1 - s.X0 + 1 <= maxW).ToList();
    }

    /// <summary>A hairline flag (modern engraving, often broken and joined to the head): a quarter has no ink of its own
    /// right of the stem between the head and the stem's end; a note that has is a flag candidate.</summary>
    private bool HairlineFlag(Stem st, Component c, Staff staff)
    {
        double ss = staff.Space;
        var head = st.Heads[0];
        int end = st.Up ? st.Top : st.Bottom;
        int from = st.Up ? (int)Math.Round(head.Head.Y0 - 0.2 * ss) : (int)Math.Round(head.Head.Y1 + 0.2 * ss);
        int ylo = Math.Min(from, end), yhi = Math.Max(from, end);
        if (yhi - ylo < 0.8 * ss) return false;
        int x0 = Math.Max(0, st.X1 + 2), x1 = Math.Min(_cm.Width - 1, (int)Math.Round(st.X1 + 1.0 * ss));
        if (x1 <= x0) return false;
        ylo = Math.Max(0, ylo); yhi = Math.Min(_cm.Height - 1, yhi);
        // the flag may be broken where staff lines were removed, so pieces of other components count too; rows
        // inked across the whole width are a ledger line or a beam, not a flag
        var lineRow = new bool[yhi - ylo + 1];
        for (int y = ylo; y <= yhi; y++)
        {
            int n = 0;
            for (int x = x0; x <= x1; x++) if (_cm.Labels[y * _cm.Width + x] > 0) n++;
            lineRow[y - ylo] = n >= 0.85 * (x1 - x0 + 1);
        }
        int cols = 0, inked = 0;
        for (int x = x0; x <= x1; x++)
        {
            cols++;
            for (int y = ylo; y <= yhi; y++)
                if (!lineRow[y - ylo] && _cm.Labels[y * _cm.Width + x] > 0) { inked++; break; }
        }
        return inked >= 0.4 * cols;
    }

    private MusicSymbol? AnalyzeNoteGroup(Component c, Staff staff, List<Stem> stems)
    {
        double ss = staff.Space;
        double headW = 1.18 * ss, headH = 1.0 * ss;
        // a flag or a thick stem can split one stem into two neighbouring runs: keep the longer one
        for (int i = stems.Count - 1; i > 0; i--)
        {
            var a = stems[i - 1]; var b = stems[i];
            if (b.X - a.X > 0.35 * ss || Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) < 0.5 * ss) continue;
            var keep = a.Length >= b.Length ? a : b; var drop = keep == a ? b : a;
            keep.X0 = Math.Min(a.X0, b.X0); keep.X1 = Math.Max(a.X1, b.X1);
            keep.Top = Math.Min(a.Top, b.Top); keep.Bottom = Math.Max(a.Bottom, b.Bottom);
            stems.Remove(drop);
        }
        // a head the stem runs past (the upper note of a chord) can break one stem into two runs end to end in the same
        // column: join them, or the lower run becomes a second note holding the chord's heads
        for (int i = 0; i < stems.Count; i++)
            for (int j = 0; j < stems.Count; j++)
            {
                if (i == j) continue;
                var a = stems[i]; var b = stems[j];
                int gap = b.Top - a.Bottom;
                if (Math.Abs(b.X - a.X) > 0.2 * ss || gap < -0.5 * ss || gap > 1.6 * ss) continue;
                // not among more runs side by side (a brace, a bracket)
                if (stems.Any(o => o != a && o != b && Math.Abs(o.X - a.X) <= 0.5 * ss)) continue;
                a.X0 = Math.Min(a.X0, b.X0); a.X1 = Math.Max(a.X1, b.X1);
                a.Top = Math.Min(a.Top, b.Top); a.Bottom = Math.Max(a.Bottom, b.Bottom);
                if (_opt.Verbose) a.Trace += "[broken stem joined]";
                stems.RemoveAt(j); i = -1; break;
            }
        _stemLabel = c.Label;
        foreach (var st in stems) FindHeads(st, staff, headW, headH);
        _stemLabel = -1;
        foreach (var st in stems) st.Heads.RemoveAll(h => Math.Abs(h.Step) > 20);
        // a stem without a head next to a stem holding two heads side by side: the head standing where the empty
        // stem's head belongs (left of a down stem, right of an up stem) is that stem's, not a chord's second
        foreach (var empty in stems.Where(s => s.Heads.Count == 0).ToList())
        {
            foreach (var full in stems.Where(s => s != empty && s.Heads.Count >= 2 && Math.Abs(s.X - empty.X) < 2.5 * staff.Space).ToList())
            {
                double Expect(Stem s, bool up) => up ? s.X1 + 0.6 * staff.Space : s.X0 - 0.6 * staff.Space;
                bool up = full.Up;
                var move = full.Heads.Where(h => Math.Abs(h.HeadX - Expect(empty, up)) < 0.5 * staff.Space && Math.Abs(h.HeadX - Expect(empty, up)) < Math.Abs(h.HeadX - Expect(full, up)) - 0.3 * staff.Space
                                                 && h.HeadY >= empty.Top - 0.8 * staff.Space && h.HeadY <= empty.Bottom + 0.8 * staff.Space).ToList();
                if (move.Count == 0 || move.Count == full.Heads.Count) continue;
                foreach (var h in move) { full.Heads.Remove(h); empty.Heads.Add(h); }
                empty.Up = up;
                if (_opt.Verbose) empty.Trace += "[head taken from a neighbour's chord]";
                break;
            }
        }
        var withHeads = stems.Where(s => s.Heads.Count > 0).ToList();
        if (withHeads.Count == 0) return null;
        // in a group of black heads a stem whose heads are all hollow is an accidental or a stem end taken for a head
        if (withHeads.Count >= 2)
        {
            int blackStems = withHeads.Count(s => s.Heads.Any(h => h.HeadType == "black"));
            // a hollow "head" at the beam itself (the white gap between two beams at the beam's end) is beam, not a note
            bool AtBeam(Stem s) => s.Heads.All(h => withHeads.Any(o => o != s && o.Heads.Any(k => k.HeadType == "black") && Math.Abs(h.HeadY - (o.Up ? o.Top : o.Bottom)) <= 1.2 * staff.Space));
            // a sure head at the far end of its stem, away from the beam, is a black head that came out light (a scan
            // with a pale centre): beamed notes are never half notes
            bool FarFromBeam(Stem s) => s.Heads.All(h => Math.Abs(h.HeadY - (s.Up ? s.Top : s.Bottom)) >= 1.5 * staff.Space);
            if (blackStems >= 1)
                foreach (var st in withHeads.Where(s => s.Heads.All(h => h.HeadType == "half") && s.Heads.Max(h => h.Confidence) >= 0.9 && !AtBeam(s) && FarFromBeam(s)).ToList())
                {
                    foreach (var h in st.Heads) h.HeadType = "black";
                    if (_opt.Verbose) st.Trace += "[hollow in black group: taken as black]";
                }
            if (blackStems >= 1)
                foreach (var st in withHeads.Where(s => s.Heads.All(h => h.HeadType == "half") && (blackStems >= 2 || s.Heads.Max(h => h.Confidence) < 0.9 || AtBeam(s))).ToList())
                {
                    if (_opt.Verbose) st.Trace += "[hollow in black group: dropped]";
                    st.Heads.Clear(); withHeads.Remove(st);
                }
        }

        // in a beamed group a "head" right at the beam end of a stem that also has a head at its other end is beam
        // ink (the thick wedge where two beams meet the stem), not the upper note of a chord
        if (withHeads.Count >= 2)
            foreach (var st in withHeads.Where(s => s.Heads.Count >= 2))
            {
                // the beam end is the one level with the beam ends of the group's single-head stems (the direction of a
                // stem with heads at both ends is a guess)
                var others = withHeads.Where(o => o != st && o.Heads.Count == 1).Select(o => (double)(o.Up ? o.Top : o.Bottom)).OrderBy(y => y).ToList();
                bool up = st.Up;
                if (others.Count > 0)
                {
                    double beamY = others[others.Count / 2];
                    up = Math.Abs(st.Top - beamY) < Math.Abs(st.Bottom - beamY);
                    // level with it: single-head stems of another voice (beamed on the other side) say nothing
                    if (Math.Abs((up ? st.Top : st.Bottom) - beamY) > 1.5 * staff.Space) continue;
                }
                int yEnd = up ? st.Top : st.Bottom;
                var atEnd = st.Heads.Where(h => Math.Abs(h.HeadY - yEnd) <= 1.0 * staff.Space).ToList();
                if (atEnd.Count == 0 || !st.Heads.Any(h => Math.Abs(h.HeadY - yEnd) >= 2.0 * staff.Space)) continue;
                foreach (var h in atEnd) st.Heads.Remove(h);
                if (_opt.Verbose) st.Trace += up != st.Up ? "[stem turned: head at beam end dropped]" : "[head at beam end: dropped]";
                st.Up = up;
            }

        // beams between consecutive stems?
        var beamed = new bool[withHeads.Count - 1 < 0 ? 0 : withHeads.Count - 1];
        for (int i = 0; i + 1 < withHeads.Count; i++) beamed[i] = HasBeamBetween(withHeads[i], withHeads[i + 1], c, staff);
        bool anyBeam = beamed.Any(b => b);

        foreach (var st in withHeads)
        {
            st.Flags = CountFlagsOrBeams(st, c, staff, withHeads.Count == 1);
            // a hairline flag (modern engraving): a thin curve of the stem's own ink running out to the right of the
            // stem's end, present in most columns over a space
            if (withHeads.Count == 1 && st.Flags == 0 && st.Heads.All(h => h.HeadType == "black") && HairlineFlag(st, c, staff))
            {
                st.FlagCandidate = true;
                if (_opt.Verbose) st.Trace += "[hairline flag?]";
            }
        }
        if (withHeads.Count >= 2) RepairGroup(stems, withHeads, c, staff);
        else if (withHeads.Count == 1 && withHeads[0].Flags > 0) RepairBeamEnd(stems, withHeads, c, staff);
        if (withHeads.Count == 0) return null;

        var sym = new MusicSymbol { Bounds = ToRect(c), Staff = staff, Confidence = 0.7 };
        if (_opt.Verbose) sym.Attributes["stems"] = string.Join(",", stems.Select(s => $"{s.X}:{s.Top}-{s.Bottom}{(s.Up ? "u" : "d")}f{s.Flags}h{s.Heads.Count}{s.Trace}"));
        if (withHeads.Count == 1)
        {
            var st = withHeads[0];
            // a "head" at the flag end of a flagged stem that also has a normal head is the flag's blob
            if (st.Flags > 0 && st.Heads.Count >= 2)
            {
                double end = st.Up ? st.Top : st.Bottom;
                var atEnd = st.Heads.Where(h => Math.Abs(h.HeadY - end) <= 1.0 * ss).ToList();
                if (atEnd.Count > 0 && st.Heads.Any(h => Math.Abs(h.HeadY - end) >= 2.0 * ss))
                {
                    foreach (var h in atEnd) st.Heads.Remove(h);
                    if (_opt.Verbose) st.Trace += $"[{atEnd.Count} flag-end blob(s) dropped]";
                }
            }
            // a hollow head with beams/flags found with little confidence is usually ledger lines or a slur crossing a stem
            if (st.Flags > 0 && st.Heads.All(h => h.HeadType == "half") && st.Heads.Max(h => h.Confidence) < 0.7) return null;
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

    /// <summary>
    /// Repairs a beamed group with the knowledge that all its stems end at a common beam: stems that stop short of
    /// it (broken ink) take the majority beam count; a pair of head-less thin strokes right before a head is a sharp
    /// (or natural) whose cross strokes must not count as beams; a head sitting at the beam end of a stem is a
    /// separate short-stemmed note, not a chord member; a wide gap between stems hides a note whose stem broke off.
    /// </summary>
    private void RepairGroup(List<Stem> stems, List<Stem> withHeads, Component c, Staff staff)
    {
        double ss = staff.Space;
        var flagged = withHeads.Where(s => s.Flags > 0).Select(s => s.Flags).ToList();
        if (flagged.Count == 0) return;
        int majority = flagged.GroupBy(f => f).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
        bool up = withHeads.Count(s => s.Up) > withHeads.Count / 2;
        // the beam line: least squares through the ends of the stems that carry the majority beam count (beams slope)
        var fitPts = withHeads.Where(s => s.Flags == majority && s.Up == up).Select(s => ((double)s.X, (double)(s.Up ? s.Top : s.Bottom))).ToList();
        double slope = 0, intercept = double.NaN;
        if (fitPts.Count >= 1)
        {
            double mx = fitPts.Average(p => p.Item1), my = fitPts.Average(p => p.Item2);
            double sxx = fitPts.Sum(p => (p.Item1 - mx) * (p.Item1 - mx)), sxy = fitPts.Sum(p => (p.Item1 - mx) * (p.Item2 - my));
            slope = fitPts.Count >= 2 && sxx > 0 ? sxy / sxx : 0;
            intercept = my - slope * mx;
        }
        double BeamAt(double x) => intercept + slope * x;
        double beamEnd = fitPts.Count > 0 ? BeamAt(fitPts.Average(p => p.Item1)) : double.NaN;

        // 0. a beam with heads on both sides of it straddles two staves (cross-staff beaming): the heads on the far side
        //    of the beam, seen from this staff, belong to the neighbouring staff of the system
        if (!double.IsNaN(intercept) && withHeads.Any(s => s.Flags > 0 && s.Up) && withHeads.Any(s => s.Flags > 0 && !s.Up))
        {
            var above = _page.Staves.FirstOrDefault(o => o.SystemIndex == staff.SystemIndex && o.Index == staff.Index - 1);
            var below = _page.Staves.FirstOrDefault(o => o.SystemIndex == staff.SystemIndex && o.Index == staff.Index + 1);
            foreach (var st in withHeads.Where(s => s.Flags > 0))
                foreach (var h in st.Heads)
                {
                    bool headAboveBeam = h.HeadY < BeamAt(h.HeadX);
                    double stepH = staff.StepAt(h.HeadX, h.HeadY);
                    // only heads well beyond the staff (two spaces past the outer line) that lie nearer the neighbour
                    Staff? target = headAboveBeam && stepH > 12.5 ? above : !headAboveBeam && stepH < -4.5 ? below : null;
                    if (target == null || h.CrossStaff == target) continue;
                    double dThis = Math.Min(Math.Abs(h.HeadY - staff.TopY(h.HeadX)), Math.Abs(h.HeadY - staff.BottomY(h.HeadX)));
                    double dOther = Math.Min(Math.Abs(h.HeadY - target.TopY(h.HeadX)), Math.Abs(h.HeadY - target.BottomY(h.HeadX)));
                    if (dOther > 1.5 * dThis) continue;
                    h.CrossStaff = target;
                    h.Step = (int)Math.Round(target.StepAt(h.HeadX, h.HeadY));
                    if (_opt.Verbose) st.Trace += $"[head {h.HeadX:0},{h.HeadY:0} -> staff {target.Index + 1}]";
                }
        }

        // 1. stems that stop short of the beam line lost their lower beams to a gap in the ink
        if (!double.IsNaN(intercept))
            foreach (var st in withHeads.Where(s => s.Up == up && s.Flags < majority))
            {
                double end = st.Up ? st.Top : st.Bottom, atBeam = BeamAt(st.X);
                double shortBy = st.Up ? end - atBeam : atBeam - end;
                if (shortBy >= 0.4 * ss && shortBy <= 3.0 * ss)
                {
                    // the beams actually crossing the stem's line decide when they can be counted (a partial beam that
                    // stops at the neighbour leaves this stem with fewer); otherwise the majority
                    int? there = BeamsAt(st.X, atBeam, st.Up, ss);
                    st.Flags = there is int t && t >= Math.Max(1, st.Flags) && t < majority ? t : majority;
                    if (_opt.Verbose) st.Trace += st.Flags == majority ? "[SHORT:majority]" : $"[SHORT:{st.Flags} beams counted]";
                }
            }

        // 2. sharps and naturals that touch the group: two thin head-less strokes right before a head
        var headless = stems.Where(s => s.Heads.Count == 0).OrderBy(s => s.X).ToList();
        for (int i = 0; i + 1 < headless.Count; i++)
        {
            var a = headless[i]; var b = headless[i + 1];
            double dx = b.X - a.X;
            if (dx < 0.22 * ss || dx > 0.8 * ss) continue;
            int la = a.Bottom - a.Top + 1, lb = b.Bottom - b.Top + 1;
            if (la < 1.6 * ss || la > 3.8 * ss || lb < 1.6 * ss || lb > 3.8 * ss) continue;
            double overlap = (Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) + 1) / (double)Math.Min(la, lb);
            if (overlap < 0.35) continue;
            var target = withHeads.Where(s => s.X0 > b.X1 && s.X0 - b.X1 <= 1.6 * ss).OrderBy(s => s.X0).FirstOrDefault();
            if (target == null) continue;
            double yTop = Math.Min(a.Top, b.Top), yBot = Math.Max(a.Bottom, b.Bottom);
            var head = target.Heads.FirstOrDefault(h => h.HeadY >= yTop - 0.3 * ss && h.HeadY <= yBot + 0.3 * ss && h.Accidental == int.MinValue);
            if (head == null) continue;
            // a natural's left stroke stands higher than its right one at both ends; a sharp's strokes are about level
            bool offset = b.Top - a.Top >= 0.35 * ss && b.Bottom - a.Bottom >= 0.35 * ss;
            bool sharp = overlap >= 0.6 && !offset;
            head.Accidental = sharp ? 1 : 0;
            head.AccidentalName = sharp ? "accidentalSharp" : "accidentalNatural";
            if (target.Flags > majority) target.Flags = majority;
            if (_opt.Verbose) target.Trace += sharp ? "[SHARP]" : "[NATURAL]";
        }

        // 3. a "head" at the beam end of a stem that also carries a normal head is the beam's end blob, not a chord
        //    member; it also hid the beams from the count, so the stem takes the majority beam count. A hollow head
        //    near the beam line on any stem is the same blob (the curl of a flag, the thick end of a beam).
        if (majority >= 1)
            foreach (var st in withHeads.ToList())
            {
                double end = st.Up ? st.Top : st.Bottom;
                var atBeam = st.Heads.Where(h => Math.Abs(h.HeadY - end) <= 1.0 * ss).ToList();
                var normal = st.Heads.Where(h => Math.Abs(h.HeadY - end) >= 2.0 * ss).ToList();
                // the direction was guessed from the wrong end: the "beam end" carries a real black head, the other end a
                // hollow blob, and the group's other black stems point the other way, so the beam is at the blob's end
                if (atBeam.Count > 0 && normal.Count > 0 && atBeam.All(h => h.HeadType == "black") && normal.All(h => h.HeadType == "half")
                    && withHeads.Any(o => o != st && o.Up != st.Up && o.Heads.Any(k => k.HeadType == "black")))
                {
                    foreach (var h in normal) st.Heads.Remove(h);
                    st.Up = !st.Up;
                    // the beams at this stem decide (a partial beam to the neighbour), else the majority
                    st.Flags = !double.IsNaN(intercept) && BeamsAt(st.X, BeamAt(st.X), st.Up, ss) is int bt && bt >= 1 ? bt : Math.Max(st.Flags, majority);
                    if (_opt.Verbose) st.Trace += "[stem turned: hollow blob at the beam dropped]";
                    continue;
                }
                if (atBeam.Count > 0 && normal.Count > 0)
                {
                    foreach (var h in atBeam) st.Heads.Remove(h);
                    if (st.Flags < majority) st.Flags = majority;
                    if (_opt.Verbose) st.Trace += $"[{atBeam.Count} beam-end blob(s) dropped]";
                }
                if (!double.IsNaN(intercept))
                {
                    var hollowBlobs = st.Heads.Where(h => h.HeadType == "half" && Math.Abs(h.HeadY - BeamAt(h.HeadX)) <= 1.5 * ss).ToList();
                    if (hollowBlobs.Count > 0 && hollowBlobs.Count < withHeads.Sum(s => s.Heads.Count))
                    {
                        foreach (var h in hollowBlobs) st.Heads.Remove(h);
                        if (_opt.Verbose) st.Trace += $"[{hollowBlobs.Count} hollow blob(s) at the beam dropped]";
                    }
                }
                if (st.Heads.Count == 0) withHeads.Remove(st);
                else
                {
                    // the direction follows the heads that are left
                    double stemMid = (st.Top + st.Bottom) / 2.0;
                    st.Up = st.Heads.Average(h => h.HeadY) > stemMid;
                }
            }

        // 3b. a head claimed by two neighbouring stems that both extend to the same side of it (a flag's vertical part,
        //     a thick stem split in two): the shorter stem goes. Two voices sharing a head have stems on opposite sides.
        static bool ExtendsDown(Stem s, Note h) => (s.Bottom - h.HeadY) > (h.HeadY - s.Top);
        for (int i = withHeads.Count - 1; i >= 0; i--)
        {
            var b = withHeads[i];
            Stem? a = null;
            foreach (var o in withHeads.Take(i))
            {
                if (Math.Abs(o.X - b.X) > 1.6 * ss) continue;
                var shared = o.Heads.FirstOrDefault(h => b.Heads.Any(k => Math.Abs(k.HeadX - h.HeadX) <= 0.6 * ss && Math.Abs(k.HeadY - h.HeadY) <= 0.4 * ss));
                if (shared == null) continue;
                var sharedB = b.Heads.First(k => Math.Abs(k.HeadX - shared.HeadX) <= 0.6 * ss && Math.Abs(k.HeadY - shared.HeadY) <= 0.4 * ss);
                if (ExtendsDown(o, shared) != ExtendsDown(b, sharedB)) continue;   // genuine two voices
                a = o; break;
            }
            if (a == null) continue;
            // two stems well apart, one holding a head of its own besides the shared one: neighbouring notes in a run
            // (the head between them reached by both); the shared head is the other stem's, which has no other
            if (Math.Abs(a.X - b.X) >= 0.8 * ss)
            {
                bool Own(Stem s, Stem o) => s.Heads.Any(h => !o.Heads.Any(k => Math.Abs(k.HeadX - h.HeadX) <= 0.6 * ss && Math.Abs(k.HeadY - h.HeadY) <= 0.4 * ss));
                var (more, only) = Own(b, a) && !Own(a, b) ? (b, a) : Own(a, b) && !Own(b, a) ? (a, b) : (null, null);
                if (more != null)
                {
                    more.Heads.RemoveAll(h => only!.Heads.Any(k => Math.Abs(k.HeadX - h.HeadX) <= 0.6 * ss && Math.Abs(k.HeadY - h.HeadY) <= 0.4 * ss));
                    if (_opt.Verbose) more.Trace += "[shared head left to the neighbour]";
                    continue;
                }
            }
            var drop = (b.Bottom - b.Top) <= (a.Bottom - a.Top) ? b : a;
            var keep = drop == a ? b : a;
            foreach (var h in drop.Heads) if (!keep.Heads.Any(k => Math.Abs(k.HeadY - h.HeadY) <= 0.4 * ss)) keep.Heads.Add(h);
            if (keep.Flags < drop.Flags) keep.Flags = drop.Flags;
            if (_opt.Verbose) keep.Trace += "[duplicate stem merged]";
            drop.Heads.Clear(); withHeads.Remove(drop);
            i = Math.Min(i, withHeads.Count);
        }
        if (withHeads.Count == 0) return;

        // 4. a wide gap between stems: look for a head whose stem broke off the beam
        if (_heads != null && withHeads.Count >= 3)
        {
            var ordered = withHeads.OrderBy(s => double.IsNaN(s.SortX) ? s.X : s.SortX).ToList();
            var spacings = new List<double>();
            for (int i = 0; i + 1 < ordered.Count; i++) spacings.Add(ordered[i + 1].X - ordered[i].X);
            var sorted = spacings.Where(s => s > 0.5 * ss).OrderBy(s => s).ToList();
            if (sorted.Count >= 2)
            {
                double typical = sorted[(sorted.Count - 1) / 2];
                for (int i = 0; i + 1 < ordered.Count; i++)
                {
                    var a = ordered[i]; var b = ordered[i + 1];
                    double gap = b.X - a.X;
                    if (gap < 1.7 * typical) continue;
                    var found = FindHeadInGap(c, staff, a.X + 0.8 * ss, b.X - 0.8 * ss, up, BeamAt, taken: withHeads.SelectMany(w => w.Heads).ToList());
                    if (found == null) continue;
                    var (hx, hy, score, hollow) = found.Value;
                    var head = new Note { Head = new Rect((int)Math.Round(hx - 0.59 * ss), (int)Math.Round(hy - 0.5 * ss), (int)Math.Round(hx + 0.59 * ss), (int)Math.Round(hy + 0.5 * ss)), HeadType = hollow ? "half" : "black", Confidence = score };
                    double stepG = staff.StepAt(hx, hy);
                    head.CrossStaff = stepG <= -2.5 || stepG >= 10.5 ? OtherStaffAt(staff, hx, hy) : null;
                    head.Step = (int)Math.Round((head.CrossStaff ?? staff).StepAt(hx, hy));
                    int sx = (int)Math.Round(up ? hx + 0.5 * ss : hx - 0.5 * ss);
                    var extra = new Stem { X = sx, X0 = sx, X1 = sx, Up = up, Flags = majority, SortX = hx, Top = (int)Math.Min(head.Head.Y0, beamEnd), Bottom = (int)Math.Max(head.Head.Y1, beamEnd) };
                    extra.Heads.Add(head);
                    if (_opt.Verbose) extra.Trace = $"[GAP head p{score:0.00}]";
                    withHeads.Add(extra); stems.Add(extra);
                }
            }
        }
        RepairBeamEnd(stems, withHeads, c, staff);
        withHeads.Sort((p, q) => (double.IsNaN(p.SortX) ? p.X : p.SortX).CompareTo(double.IsNaN(q.SortX) ? q.X : q.SortX));
    }

    /// <summary>Step 5 of the group repair, also for a single beamed stem: a head under the far end of the beam.</summary>
    private void RepairBeamEnd(List<Stem> stems, List<Stem> withHeads, Component c, Staff staff)
    {
        double ss = staff.Space;
        var flagged = withHeads.Where(s => s.Flags > 0).Select(s => s.Flags).ToList();
        if (flagged.Count == 0) return;
        int majority = flagged.GroupBy(f => f).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
        bool up = withHeads.Count(s => s.Up) > withHeads.Count / 2;
        var fitPts = withHeads.Where(s => s.Flags == majority && s.Up == up).Select(s => ((double)s.X, (double)(s.Up ? s.Top : s.Bottom))).ToList();
        if (fitPts.Count == 0) return;
        double mx = fitPts.Average(p => p.Item1), my = fitPts.Average(p => p.Item2);
        double sxx = fitPts.Sum(p => (p.Item1 - mx) * (p.Item1 - mx)), sxy = fitPts.Sum(p => (p.Item1 - mx) * (p.Item2 - my));
        double slope = fitPts.Count >= 2 && sxx > 0 ? sxy / sxx : 0, intercept = my - slope * mx;
        double BeamAt(double x) => intercept + slope * x;
        // 5. the beam runs on past the outermost stem: the note there lost its stem (its head merged with a tie or slur
        // into another component). Left of the first stem always (flags point right), right of the last one only in a
        // group, where the overhang is beam and not a flag
        if (_heads == null || withHeads.Count == 0) return;
        {
            var ordered = withHeads.OrderBy(s => double.IsNaN(s.SortX) ? s.X : s.SortX).ToList();
            var first = ordered[0]; var last = ordered[^1];
            var tries = new List<(double from, double to, double stemX)>();
            if (first.X - c.X0 >= 1.3 * ss) tries.Add((c.X0 - 0.8 * ss, first.X - 1.0 * ss, c.X0));
            if (ordered.Count >= 2 && c.X1 - last.X >= 1.5 * ss) tries.Add((last.X + 1.0 * ss, c.X1 + 0.8 * ss, c.X1));
            foreach (var (from, to, endX) in tries)
            {
                // the beam must really reach out there (at the beam level of the nearer stem)
                double by = BeamAt(endX);
                if (double.IsNaN(by)) continue;
                var found = FindHeadInGap(c, staff, from, to, up, BeamAt, viaStem: true);
                if (found == null || found.Value.score < 0.9) continue;
                var (hx, hy, score, hollow) = found.Value;
                if (withHeads.Any(w => w.Heads.Any(h => Math.Abs(h.HeadX - hx) < 0.8 * ss))) continue;
                var head = new Note { Head = new Rect((int)Math.Round(hx - 0.59 * ss), (int)Math.Round(hy - 0.5 * ss), (int)Math.Round(hx + 0.59 * ss), (int)Math.Round(hy + 0.5 * ss)), HeadType = hollow ? "half" : "black", Confidence = score };
                head.Step = (int)Math.Round(staff.StepAt(hx, hy));
                int sx = (int)Math.Round(up ? hx + 0.5 * ss : hx - 0.5 * ss);
                var extra = new Stem { X = sx, X0 = sx, X1 = sx, Up = up, Flags = majority, SortX = hx, Top = (int)Math.Min(head.Head.Y0, by), Bottom = (int)Math.Max(head.Head.Y1, by) };
                extra.Heads.Add(head);
                if (_opt.Verbose) extra.Trace = $"[BEAM END head p{score:0.00}]";
                withHeads.Add(extra); stems.Add(extra);
            }
        }
    }

    /// <summary>Best note head (verifier) inside the component between two x positions, or null.</summary>
    private (double x, double y, double score, bool hollow)? FindHeadInGap(Component c, Staff staff, double xFrom, double xTo, bool up, Func<double, double> beamAt, bool viaStem = false, IReadOnlyList<Note>? taken = null)
    {
        double ss = staff.Space;
        if (xTo - xFrom < 0.6 * ss) return null;
        int iBlack = _heads!.LabelIndex("head_black"), iHalf = _heads.LabelIndex("head_half");
        int stepX = Math.Max(2, (int)Math.Round(0.25 * ss)), stepY = Math.Max(2, (int)Math.Round(0.2 * ss));
        var grids = new List<float[]>(); var pos = new List<(int x, int y)>();
        for (int x = (int)xFrom; x <= (int)xTo; x += stepX)
            for (int y = c.Y0; y <= c.Y1; y += stepY) { grids.Add(Eval.CropExtractor.WindowGrid(_ns, x, y, ss)); pos.Add((x, y)); }
        if (grids.Count == 0) return null;
        var probs = _heads.ClassifyGrids(grids, (float)Eval.CropExtractor.HeadWindow, (float)Eval.CropExtractor.HeadWindow);
        (double x, double y, double score, bool hollow)? best = null;
        for (int i = 0; i < pos.Count; i++)
        {
            double pb = iBlack >= 0 ? probs[i][iBlack] : 0, ph = iHalf >= 0 ? probs[i][iHalf] : 0, p = pb + ph;
            if (p < 0.9 || (best != null && p <= best.Value.score)) continue;
            var (x, y) = pos[i];
            // on the head side of the beam, at least 1.5 spaces away from it
            double beamY = beamAt(x);
            if (up ? y < beamY + 1.5 * ss : y > beamY - 1.5 * ss) continue;
            // the head must be ink of this component, not a ledger line or a beam; at a beam end it may belong to another
            // component (merged with a tie) when a stem runs from it to the beam
            int label = _cm.Labels[y * _cm.Width + x];
            if (label != c.Label)
            {
                if (!viaStem || label <= 0) continue;
                var other = label - 1 < _cm.Components.Count ? _cm.Components[label - 1] : null;
                if (other == null || other.Width > 3.0 * ss || other.Height > 2.6 * ss || !StemToBeam(x, y, up, beamY, ss)) continue;
            }
            double stepF = staff.StepAt(x, y);
            if ((stepF <= -2.5 || stepF >= 10.5) && OtherStaffAt(staff, x, y) == null && !HasLedgerLine(x, y, ss)) continue;
            // not on a head the group already has (it would be found again next to its own stem)
            if (taken != null && taken.Any(t => Math.Abs(t.HeadX - x) < 0.7 * ss && Math.Abs(t.HeadY - y) < 0.6 * ss)) continue;
            // a beam is only near the beam line: further out, ink running on sideways is a slur or the next head
            if (Math.Abs(y - beamY) < 2.0 * ss && (LooksLikeBeam(x, y, ss, 1) || LooksLikeBeam(x, y, ss, -1))) continue;
            best = (x, y, p, ph > pb);
        }
        if (best is { } bb && !bb.hollow && InkCentreY(bb.x, bb.y, ss) is double iy) best = (bb.x, iy, bb.score, bb.hollow);
        return best;
    }

    /// <summary>A stem from the head at (x, y) to the beam: ink in a narrow column beside the head on the stem side over
    /// at least 80% of the rows between the head and the beam.</summary>
    private bool StemToBeam(int x, int y, bool up, double beamY, double ss)
    {
        int sx = (int)Math.Round(up ? x + 0.5 * ss : x - 0.5 * ss), w = Math.Max(2, (int)Math.Round(0.2 * ss));
        int y0 = (int)Math.Round(Math.Min(y, beamY)), y1 = (int)Math.Round(Math.Max(y, beamY));
        if (y1 - y0 < ss) return false;
        int rows = 0, inked = 0;
        for (int yy = y0; yy <= y1; yy++)
        {
            rows++;
            for (int xx = sx - w; xx <= sx + w; xx++) if (xx >= 0 && xx < _bin.Width && yy >= 0 && yy < _bin.Height && _bin[xx, yy]) { inked++; break; }
        }
        return inked >= 0.8 * rows;
    }

    private static void FillNoteSymbol(MusicSymbol sym, Stem st, Staff staff)
    {
        sym.Attributes["stemx"] = $"{st.X0},{st.X1},{(st.Up ? st.Top : st.Bottom)}";
        sym.Kind = st.Heads.Count > 1 ? SymbolKind.Chord : SymbolKind.Note;
        sym.Name = st.Heads.Count > 1 ? "chord" : "note";
        sym.StemUp = st.Up;
        sym.Flags = st.Flags;
        if (st.FlagCandidate && st.Flags == 0) sym.Attributes["flagcand"] = "1";
        sym.Notes.AddRange(st.Heads.OrderByDescending(h => h.Step));
        bool hollow = st.Heads.All(h => h.HeadType == "half");
        sym.Duration = hollow ? "half" : st.Flags switch { 0 => "quarter", 1 => "8th", 2 => "16th", 3 => "32nd", _ => "64th" };
        // a hollow head never carries flags or beams: the strokes are tremolo slashes (repeated notes)
        if (hollow && st.Flags > 0) { sym.Attributes["note"] = "hollow head with flags/beams"; sym.Attributes["tremolo"] = Math.Min(st.Flags, 4).ToString(); }
    }

    private void FindHeads(Stem st, Staff staff, double headW, double headH)
    {
        if (_heads != null)
        {
            FindHeadsWithVerifier(st, staff, headW, headH);
            if (st.Heads.Count > 0) return;
            // the verifier found nothing (e.g. a head touching an accidental): fall back to the geometric scorer with a strict threshold
            FindHeadsGeometric(st, staff, headW, headH, 0.72);
            if (_opt.Verbose && st.Heads.Count > 0) st.Trace += "[geometric fallback]";
            return;
        }
        FindHeadsGeometric(st, staff, headW, headH, 0.52);
    }

    private void FindHeadsGeometric(Stem st, Staff staff, double headW, double headH, double minScore)
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
            var sorted = scores.Where(s => s.score >= minScore).OrderByDescending(s => s.score).ToList();
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

    /// <summary>Head detection with the trained verifier: windows along both sides of the stem, batch-classified, non-maximum suppression.</summary>
    /// <summary>Another staff of the same system whose band (lines plus one space) contains the point: the home of a cross-staff head.</summary>
    private Staff? OtherStaffAt(Staff staff, double x, double y)
    {
        foreach (var o in _page.Staves)
        {
            // cross-staff beams join neighbouring staves only
            if (o == staff || o.SystemIndex != staff.SystemIndex || Math.Abs(o.Index - staff.Index) != 1) continue;
            if (x < o.Left - o.Space || x > o.Right + o.Space) continue;
            if (y >= o.TopY(x) - 1.0 * o.Space && y <= o.BottomY(x) + 1.0 * o.Space) return o;
        }
        return null;
    }

    private int _stemLabel = -1;

    /// <summary>True when a pixel of the given component label lies within r of (cx, cy) in the staff-less image.</summary>
    private bool TouchesLabel(double cx, double cy, double r, int label)
    {
        int ir = (int)Math.Ceiling(r);
        int x0 = (int)Math.Round(cx) - ir, y0 = (int)Math.Round(cy) - ir;
        for (int y = Math.Max(0, y0); y <= Math.Min(_cm.Height - 1, y0 + 2 * ir); y++)
            for (int x = Math.Max(0, x0); x <= Math.Min(_cm.Width - 1, x0 + 2 * ir); x++)
                if (_cm.Labels[y * _cm.Width + x] == label) return true;
        return false;
    }

    private void FindHeadsWithVerifier(Stem st, Staff staff, double headW, double headH)
    {
        FindHeadsWithVerifier(st, staff, headW, headH, 0.6);
        // nothing at the stem: the stem ink may be broken near the head, so look a little further out
        if (st.Heads.Count == 0) FindHeadsWithVerifier(st, staff, headW, headH, 1.2);
    }

    private void FindHeadsWithVerifier(Stem st, Staff staff, double headW, double headH, double reach)
    {
        double ss = staff.Space;
        double rx = headW / 2;
        int iBlack = _heads!.LabelIndex("head_black"), iHalf = _heads.LabelIndex("head_half");
        var sides = new (int sign, double cx)[] { (-1, st.X0 - rx + 0.08 * ss), (+1, st.X1 + rx - 0.08 * ss) };
        int step = Math.Max(2, (int)Math.Round(0.18 * ss));
        int yFrom = (int)(st.Top - reach * ss), yTo = (int)(st.Bottom + reach * ss);
        var found = new List<(double score, double cx, double cy, bool hollow, int sign)>();
        foreach (var (sign, cx) in sides)
        {
            var grids = new List<float[]>(); var ys = new List<int>();
            for (int cy = yFrom; cy <= yTo; cy += step) { grids.Add(Eval.CropExtractor.WindowGrid(_ns, cx, cy, ss)); ys.Add(cy); }
            var probs = _heads.ClassifyGrids(grids, (float)Eval.CropExtractor.HeadWindow, (float)Eval.CropExtractor.HeadWindow);
            var cands = new List<(int y, double p, bool hollow)>();
            for (int i = 0; i < ys.Count; i++)
            {
                double pb = iBlack >= 0 ? probs[i][iBlack] : 0, ph = iHalf >= 0 ? probs[i][iHalf] : 0;
                double p = pb + ph;
                if (p >= 0.5) cands.Add((ys[i], p, ph > pb));
            }
            int minSep = (int)(0.85 * ss);
            var taken = new List<int>();
            foreach (var c in cands.OrderByDescending(c => c.p))
            {
                if (taken.Any(t => Math.Abs(t - c.y) < minSep)) continue;
                taken.Add(c.y);
                // centre: probability-weighted mean of the positions around the maximum (the scan is coarse and the model saturates)
                var near = cands.Where(o => Math.Abs(o.y - c.y) <= 0.5 * ss && o.p >= 0.8 * c.p).ToList();
                double cyRef = near.Sum(o => o.y * o.p) / near.Sum(o => o.p);
                // the windows along the stem still see the head below (stem down) or above it: the mean is pulled
                // towards the stem. A black head's own ink, the run straight through its middle, gives the centre
                if (!c.hollow && InkCentreY(cx, cyRef, ss) is double inkY) cyRef = inkY;
                found.Add((c.p, cx, cyRef, c.hollow, sign));
            }
        }
        foreach (var f in found.OrderByDescending(f => f.score))
        {
            if (st.Heads.Any(h => Math.Abs(h.HeadY - f.cy) < 0.4 * ss)) continue;
            // a head that is not part of the stem's component (accidental box or text next to a bare stroke) must be very sure
            if (f.score < 0.9 && _stemLabel >= 0 && !TouchesLabel(f.cx, f.cy, 0.35 * ss, _stemLabel)) { if (_opt.Verbose) st.Trace += $"[{f.cx:0},{f.cy:0} p{f.score:0.00} DETACHED]"; continue; }
            bool beam = LooksLikeBeam(f.cx, f.cy, ss, -f.sign);
            // outside the staff a real head sits on or next to ledger lines; text and stray marks do not
            double stepF = staff.StepAt(f.cx, f.cy);
            // a head inside the neighbouring staff belongs to this stem only at the stem's far end (cross-staff beaming);
            // heads of the other staff that merely pass by a long stem do not
            Staff? other = stepF <= -2.5 || stepF >= 10.5 ? OtherStaffAt(staff, f.cx, f.cy) : null;
            if (other != null && Math.Min(Math.Abs(f.cy - st.Top), Math.Abs(f.cy - st.Bottom)) > 1.0 * ss) { if (_opt.Verbose) st.Trace += $"[{f.cx:0},{f.cy:0} other staff mid-stem]"; continue; }
            if ((stepF <= -2.5 || stepF >= 10.5) && other == null && !HasLedgerLine(f.cx, f.cy, ss)) { if (_opt.Verbose) st.Trace += $"[{f.cx:0},{f.cy:0} NOLEDGER]"; continue; }
            if (_opt.Verbose) st.Trace += $"[{f.cx:0},{f.cy:0} p{f.score:0.00} {(f.hollow ? "h" : "b")}{(beam ? " BEAM" + _beamWhy : "")}]";
            if (beam) continue;
            var (cxr, cyr) = (f.cx, f.cy);
            var head = new Note
            {
                Head = new Rect((int)Math.Round(cxr - rx), (int)Math.Round(cyr - headH / 2), (int)Math.Round(cxr + rx), (int)Math.Round(cyr + headH / 2)),
                HeadType = f.hollow ? "half" : "black",
                Confidence = Math.Min(1, f.score),
                CrossStaff = other,
            };
            head.Step = (int)Math.Round((other ?? staff).StepAt(cxr, cyr));
            if (other != null && _opt.Verbose) st.Trace += $"[{f.cx:0},{f.cy:0} cross-staff {other.Index + 1}]";
            st.Heads.Add(head);
        }
        if (st.Heads.Count == 0) return;
        double stemMid = (st.Top + st.Bottom) / 2.0;
        st.Up = st.Heads.Average(h => h.HeadY) > stemMid;
        st.Heads.RemoveAll(h => h.HeadY < st.Top - (reach + 0.1) * ss || h.HeadY > st.Bottom + (reach + 0.1) * ss);
    }

    private static readonly bool HeadStepInk = Environment.GetEnvironmentVariable("PVOMR_HEADSTEP") != "0";
    private static readonly bool HeadStepDump = Environment.GetEnvironmentVariable("PVOMR_HEADSTEPDUMP") is "1" or "2";
    private static readonly bool HeadStepAll = Environment.GetEnvironmentVariable("PVOMR_HEADSTEPDUMP") == "2";

    /// <summary>
    /// A black head's position comes from matching its shape, which a stem joining it, a ledger line or staff line
    /// through it and a beam close by can pull half a space off: the head is then read a step too low or high. At the
    /// end, when everything else has been decided with the head where it was found, the step alone is set from the
    /// head's own ink (the middle of its vertical runs across the head, away from the stem) where that is clearly a
    /// step away. The head keeps its place, so dots, voices and stems are not affected.
    /// </summary>
    private void HeadStepsFromInk(List<MusicSymbol> syms, Staff staff)
    {
        if (!HeadStepInk) return;
        double ss = staff.Space;
        var owners = syms.Where(s => s.Kind is SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup)
                         .SelectMany(s => s.Children.Count > 0 ? s.Children : new List<MusicSymbol> { s }).ToList();
        var all = owners.SelectMany(o => o.Notes).ToList();
        foreach (var o in owners)
            foreach (var n in o.Notes)
            {
                if (n.HeadType != "black" || n.CrossStaff != null) continue;
                // another head of the same stem a second away shares the ink: left alone
                if (all.Any(m => m != n && Math.Abs(m.HeadX - n.HeadX) < 1.2 * ss && Math.Abs(m.HeadY - n.HeadY) < 1.3 * ss)) continue;
                var mids = new List<double>();
                foreach (double dx in new[] { -0.15, 0.0, 0.15 })
                {
                    int x = (int)Math.Round(n.HeadX + dx * ss);
                    int y0 = (int)Math.Round(n.HeadY);
                    int y = -1;
                    for (int d = 0; d <= 0.4 * ss && y < 0; d++) { if (_ns[x, y0 + d]) y = y0 + d; else if (_ns[x, y0 - d]) y = y0 - d; }
                    if (y < 0) continue;
                    int top = y, bot = y;
                    while (_ns[x, top - 1] && y - top < 2 * ss) top--;
                    while (_ns[x, bot + 1] && bot - y < 2 * ss) bot++;
                    int len = bot - top + 1;
                    if (len >= 0.7 * ss && len <= 1.25 * ss) mids.Add((top + bot) / 2.0);
                }
                if (HeadStepAll) Console.WriteLine($"HEADSTEP? page {_page.PageNumber} staff {staff.Index + 1} head {n.HeadX:0},{n.HeadY:0} step {n.Step}: mids {string.Join(" ", mids.Select(m => $"{staff.StepAt(n.HeadX, m):0.00}"))}");
                if (mids.Count < 3 || mids.Max() - mids.Min() > 0.25 * ss) continue;
                double my = mids.Average();
                double now = staff.StepAt(n.HeadX, n.HeadY), ink = staff.StepAt(n.HeadX, my);
                int step = (int)Math.Round(ink);
                // a step away: the ink centre close to a step position
                if (Math.Abs(step - n.Step) != 1 || Math.Abs(ink - step) > 0.3) continue;
                if (HeadStepDump) Console.WriteLine($"HEADSTEP page {_page.PageNumber} staff {staff.Index + 1} head {n.HeadX:0},{n.HeadY:0}: step {n.Step} -> {step} (found {now:0.00}, ink {ink:0.00})");
                n.Step = step;
            }
    }

    /// <summary>The vertical centre of a black head from its ink: the run through (cx, near cy) in the staff-less image
    /// on three columns across the head, when the runs have a head's height (null otherwise).</summary>
    // experimental (PVOMR_INKCENTRE=1): corrects some stem-down heads read a step low, but costs fitting measures on
    // real pages (dots and voices follow the moved heads) and leaves heads that touch a neighbour wrong
    private static readonly bool NoInkCentre = Environment.GetEnvironmentVariable("PVOMR_INKCENTRE") != "1";

    private double? InkCentreY(double cx, double cy, double ss)
    {
        if (NoInkCentre) return null;
        var mids = new List<double>();
        foreach (double dx in new[] { -0.2, 0.0, 0.2 })
        {
            int x = (int)Math.Round(cx + dx * ss);
            if (x < 0 || x >= _ns.Width) continue;
            int y = -1;
            for (int d = 0; d <= 0.5 * ss && y < 0; d++)
            {
                int ya = (int)Math.Round(cy) + d, yb = (int)Math.Round(cy) - d;
                if (ya >= 0 && ya < _ns.Height && _ns[x, ya]) y = ya; else if (yb >= 0 && yb < _ns.Height && _ns[x, yb]) y = yb;
            }
            if (y < 0) continue;
            int top = y, bot = y;
            while (top > 0 && _ns[x, top - 1] && y - top < 2 * ss) top--;
            while (bot < _ns.Height - 1 && _ns[x, bot + 1] && bot - y < 2 * ss) bot++;
            int len = bot - top + 1;
            if (len >= 0.6 * ss && len <= 1.3 * ss) mids.Add((top + bot) / 2.0);
        }
        if (mids.Count < 2) return null;
        mids.Sort();
        double m = mids[mids.Count / 2];
        return Math.Abs(m - cy) <= 0.45 * ss ? m : null;
    }

    /// <summary>A beam (or its end) under a head candidate: ink continues (within a band that tolerates the beam slope) for more than 1.5 staff spaces from the centre.</summary>
    /// <summary>The classifier is sure this hollow-looking component is a fingering digit or a letter, not a head.</summary>
    private bool ConfidentNonHead(Component c, double ss)
    {
        if (_clf == null) return false;
        var p = _clf.Classify(_cm.Mask(c), c.Width, c.Height, ss);
        return p.Label is "digit" or "text" or "dynamic" && p.Probability >= 0.6;
    }

    /// <summary>
    /// An accidental always stands before a note. When none follows it but a head-sized piece read as text or unknown
    /// does, at the accidental's height, and the head verifier calls it a hollow head, it is a whole note (stemless
    /// hollow heads are no shape the note analysis builds from). The accidental and a dot after it attach as usual.
    /// </summary>
    private void HeadsAfterAccidentals(List<MusicSymbol> syms, Staff staff)
    {
        if (_heads == null) return;
        double ss = staff.Space;
        int iBlack = _heads.LabelIndex("head_black"), iHalf = _heads.LabelIndex("head_half");
        var heads = syms.Where(s => s.Kind is SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup).SelectMany(s => s.Notes.Concat(s.Children.SelectMany(c => c.Notes))).ToList();
        foreach (var acc in syms.Where(s => s.Kind == SymbolKind.Accidental).ToList())
        {
            double ay = AccidentalCenterY(acc);
            // a note already follows: nothing to rescue
            if (heads.Any(h => h.Head.X0 > acc.Bounds.X1 - 0.25 * ss && h.Head.X0 < acc.Bounds.X1 + 1.6 * ss && Math.Abs(h.HeadY - ay) <= 0.6 * ss)) continue;
            var piece = syms.Where(s => s.Kind is SymbolKind.Text or SymbolKind.Unknown && s.Bounds.X0 > acc.Bounds.X1 - 0.25 * ss && s.Bounds.X0 < acc.Bounds.X1 + 1.6 * ss
                                        && Math.Abs(s.Bounds.CenterY - ay) <= 0.6 * ss && s.Bounds.Width >= 0.9 * ss && s.Bounds.Width <= 2.0 * ss
                                        && s.Bounds.Height >= 0.6 * ss && s.Bounds.Height <= 1.5 * ss)
                            .OrderBy(s => s.Bounds.X0).FirstOrDefault();
            if (piece == null) continue;
            if (MakeWholeNote(piece, staff, 0.6, iBlack, iHalf) is Note note) { piece.Attributes["head"] = "after an accidental"; heads.Add(note); }
        }
        // stemless hollow heads anywhere (whole notes, also merged with their ledger line): sure verdicts only, and
        // outside the staff a ledger line must be there
        foreach (var piece in syms.Where(s => s.Kind is SymbolKind.Text or SymbolKind.Unknown or SymbolKind.Ledger
                                              && s.Bounds.Width >= 1.1 * ss && s.Bounds.Width <= 2.8 * ss && s.Bounds.Height >= 0.7 * ss && s.Bounds.Height <= 1.6 * ss).ToList())
        {
            double step = staff.StepAt(piece.Bounds.CenterX, piece.Bounds.CenterY);
            if (step < -6 || step > 14) continue;
            bool outside = step < -1.5 || step > 9.5;
            if (outside && piece.Kind != SymbolKind.Ledger && !HasLedgerLine(piece.Bounds.CenterX, piece.Bounds.CenterY, ss)) continue;
            if (heads.Any(h => Math.Abs(h.HeadX - piece.Bounds.CenterX) < 0.8 * ss && Math.Abs(h.HeadY - piece.Bounds.CenterY) < 0.8 * ss)) continue;
            // the broken-off head of a stemmed note, which the stem's head finder placed a little beside it
            if (heads.Any(h => Math.Abs(h.HeadY - piece.Bounds.CenterY) <= 0.5 * ss && Math.Abs(h.HeadX - piece.Bounds.CenterX) <= 1.2 * ss
                               && piece.Bounds.X1 >= h.Head.X0 - 0.2 * ss && piece.Bounds.X0 <= h.Head.X1 + 0.2 * ss)) continue;
            if (MakeWholeNote(piece, staff, 0.85, iBlack, iHalf) is Note note) { piece.Attributes["head"] = "hollow, stemless"; heads.Add(note); }
        }
    }

    /// <summary>
    /// A stem that broke off its black head (also lower down, the head keeping a piece of it), the head left in a piece
    /// of other ink (a ledger line, a slur end, a smudge) that was read as text: where the head verifier sees a black
    /// head up to 2 ss beyond the stem's end, inside such a piece, the piece is that note (a quarter; a flagged stem is
    /// too wide to be taken for a loose one). The head sits in the middle of the positions the verifier accepts.
    /// </summary>
    private void HeadsOnLooseStems(List<MusicSymbol> syms, Staff staff)
    {
        if (_heads == null) return;
        double ss = staff.Space;
        int iBlack = _heads.LabelIndex("head_black"), iHalf = _heads.LabelIndex("head_half");
        var heads = syms.Where(s => s.Kind is SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup).SelectMany(s => s.Notes.Concat(s.Children.SelectMany(c => c.Notes))).ToList();
        var stems = syms.Where(s => s.Kind is SymbolKind.Text or SymbolKind.Unknown && s.Bounds.Width <= 0.35 * ss && s.Bounds.Height >= 1.8 * ss && s.Bounds.Height <= 4.5 * ss).ToList();
        foreach (var st in stems)
        {
            (double p, double cx, double cy, bool up, MusicSymbol piece)? best = null;
            var hits = new List<(double cy, bool up, MusicSymbol piece)>();
            foreach (bool up in new[] { false, true })
            {
                // a stem down hangs from the head's left edge, a stem up rises from its right edge
                double cx = up ? st.Bounds.X1 - 0.55 * ss : st.Bounds.X0 + 0.55 * ss;
                double end = up ? st.Bounds.Y1 : st.Bounds.Y0;
                for (double dy = -2.0; dy <= 1.0001; dy += 0.25)
                {
                    double cy = end + (up ? -dy : dy) * ss;
                    if (up ? cy < end - 0.3 * ss : cy > end + 0.3 * ss) continue;
                    var piece = syms.FirstOrDefault(s => s != st && s.Kind is SymbolKind.Text or SymbolKind.Unknown && s.Bounds.Contains((int)Math.Round(cx), (int)Math.Round(cy))
                                                         && s.Bounds.Width >= 0.8 * ss && s.Bounds.Width <= 2.6 * ss && s.Bounds.Height <= 2.6 * ss);
                    if (piece == null || heads.Any(h => Math.Abs(h.HeadX - cx) < 0.8 * ss && Math.Abs(h.HeadY - cy) < 0.8 * ss)) continue;
                    var probs = _heads.ClassifyGrids(new List<float[]> { Eval.CropExtractor.WindowGrid(_ns, cx, cy, ss) }, (float)Eval.CropExtractor.HeadWindow, (float)Eval.CropExtractor.HeadWindow);
                    double pb = iBlack >= 0 ? probs[0][iBlack] : 0, ph = iHalf >= 0 ? probs[0][iHalf] : 0;
                    if (pb < 0.8 || pb <= ph) continue;
                    hits.Add((cy, up, piece));
                    if (best == null || pb > best.Value.p) best = (pb, cx, cy, up, piece);
                }
            }
            if (best is not var (bp, bx, by, bup, bpiece)) continue;
            var same = hits.Where(h => h.up == bup && h.piece == bpiece).Select(h => h.cy).ToList();
            by = (same.Min() + same.Max()) / 2;
            var head = new Rect((int)Math.Round(bx - 0.6 * ss), (int)Math.Round(by - 0.45 * ss), (int)Math.Round(bx + 0.6 * ss), (int)Math.Round(by + 0.45 * ss));
            var note = new Note { Head = head, HeadType = "black", Confidence = bp, Step = (int)Math.Round(staff.StepAt(bx, by)) };
            bpiece.Kind = SymbolKind.Note; bpiece.Name = "note"; bpiece.Duration = "quarter"; bpiece.StemUp = bup; bpiece.Confidence = Math.Round(bp, 2);
            bpiece.Notes.Clear(); bpiece.Notes.Add(note);
            bpiece.Attributes["stemx"] = $"{st.Bounds.X0},{st.Bounds.X1},{(bup ? st.Bounds.Y0 : st.Bounds.Y1)}";
            bpiece.Attributes["stem"] = "reattached";
            bpiece.Bounds = bpiece.Bounds.Union(st.Bounds);
            syms.Remove(st);
            heads.Add(note);
        }
    }

    /// <summary>
    /// The upper digit of a time signature (a round "2" or "3") read as a stemless head: a lone digit in the lower
    /// half of the staff with such a "note" right on top of it is read again as one stacked signature, the ink of both
    /// split at the middle line and each half matched against the digit templates (touching digits share pieces).
    /// </summary>
    private void DigitsReadAsHeads(List<MusicSymbol> syms, Staff staff)
    {
        if (_lib == null) return;
        double ss = staff.Space;
        foreach (var low in syms.Where(s => s.Kind == SymbolKind.TimeSignature && s.Name.Length == 8 && s.Name.StartsWith("timeSig") && char.IsDigit(s.Name[7])).ToList())
        {
            var lb = low.Bounds;
            double mid = staff.Lines[2].YAt(lb.CenterX);
            if (lb.CenterY <= mid || syms.Any(s => s != low && s.Kind == SymbolKind.TimeSignature && s.Bounds.CenterY < mid && s.Bounds.X1 > lb.X0 && s.Bounds.X0 < lb.X1)) continue;
            var head = syms.FirstOrDefault(s => s.Kind == SymbolKind.Note && s.StemUp == null && s.Notes.Count == 1 && s.Bounds.CenterY < mid
                                                && s.Bounds.Y1 <= lb.Y0 + 0.4 * ss && s.Bounds.Y1 >= lb.Y0 - 0.6 * ss
                                                && Math.Min(s.Bounds.X1, lb.X1) - Math.Max(s.Bounds.X0, lb.X0) >= 0.6 * Math.Min(s.Bounds.Width, lb.Width));
            if (head == null) continue;
            var r = head.Bounds.Union(lb);
            int split = (int)Math.Round(mid) - r.Y0;
            string? Digit(int y0, int y1)
            {
                int x0 = int.MaxValue, x1 = -1, yy0 = int.MaxValue, yy1 = -1;
                for (int y = y0; y < y1; y++) for (int x = r.X0; x <= r.X1; x++) if (_ns[x, r.Y0 + y]) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); yy0 = Math.Min(yy0, y); yy1 = Math.Max(yy1, y); }
                if (x1 < 0 || yy1 - yy0 < 0.8 * ss) return null;
                int sw = x1 - x0 + 1, sh = yy1 - yy0 + 1;
                var sub = new byte[sw * sh];
                for (int y = yy0; y <= yy1; y++) for (int x = x0; x <= x1; x++) if (_ns[x, r.Y0 + y]) sub[(y - yy0) * sw + (x - x0)] = 1;
                var mm = _lib.Match(sub, sw, sh, ss, 0.4, t => t.Kind == SymbolKind.TimeSignature && t.Name.Length == 8);
                return mm.Count > 0 && mm[0].Score >= 0.6 ? mm[0].Template.Name[7..] : null;
            }
            string? upper = Digit(0, split), lower = Digit(split, r.Height);
            if (upper == null || lower == null) continue;
            low.Name = "timeSigStack"; low.Bounds = r; low.Confidence = 0.7;
            low.Attributes["stack"] = $"{upper}/{lower}";
            low.Attributes["digit"] = "upper read as a head";
            syms.Remove(head);
        }
    }

    /// <summary>Turns a piece into a whole note when the head verifier calls it hollow with at least the given probability.</summary>
    private Note? MakeWholeNote(MusicSymbol piece, Staff staff, double minP, int iBlack, int iHalf)
    {
        double ss = staff.Space;
        // a ledger line merged with the head widens the piece; the head sits at its vertical centre
        double cx = piece.Bounds.CenterX, cy = piece.Bounds.CenterY;
        var probs = _heads!.ClassifyGrids(new List<float[]> { Eval.CropExtractor.WindowGrid(_ns, cx, cy, ss) }, (float)Eval.CropExtractor.HeadWindow, (float)Eval.CropExtractor.HeadWindow);
        double pb = iBlack >= 0 ? probs[0][iBlack] : 0, ph = iHalf >= 0 ? probs[0][iHalf] : 0;
        if (ph < minP || ph < pb) return null;
        var head = new Rect((int)Math.Round(cx - 0.75 * ss), (int)Math.Round(cy - 0.5 * ss), (int)Math.Round(cx + 0.75 * ss), (int)Math.Round(cy + 0.5 * ss));
        var note = new Note { Head = head, HeadType = "whole", Confidence = ph };
        note.Step = (int)Math.Round(staff.StepAt(cx, cy));
        piece.Kind = SymbolKind.Note; piece.Name = "note"; piece.Duration = "whole"; piece.StemUp = null; piece.Confidence = Math.Round(ph, 2);
        piece.Notes.Clear(); piece.Notes.Add(note);
        return note;
    }

    /// <summary>
    /// Nothing sounds before the clef at the start of a staff: a stemless "head" above or below the staff there is part
    /// of a boxed rehearsal number or other text over the clef (old parts print the box right above it).
    /// </summary>
    private void DropNotesBeforeClef(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        var clef = syms.Where(s => s.Kind == SymbolKind.Clef && s.Bounds.X0 <= staff.Left + 4 * ss).OrderBy(s => s.Bounds.X0).FirstOrDefault();
        if (clef == null) return;
        syms.RemoveAll(s => s.Kind is SymbolKind.Note or SymbolKind.Chord && s.StemUp == null && s.Bounds.CenterX <= clef.Bounds.X1 + 0.5 * ss
                            && s.Notes.Count > 0 && s.Notes.All(n => n.Step > 9 || n.Step < -1));
    }

    /// <summary>
    /// Grace notes are printed small: a note, chord or beamed group whose black heads are all clearly narrower than a
    /// normal head (measured on the ink, just off the head's centre line so a ledger line through it does not count)
    /// is marked as grace notes ("grace" attribute; a single flagged one is slashed, an acciaccatura).
    /// </summary>
    private void MarkGraceNotes(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        // a small black head (also one the verifier took for hollow): short vertical ink through its centre; a real
        // hollow head has no ink at its centre at all
        bool Small(Note n) => HeadInkHeight(n.HeadX, n.HeadY, ss) is double h && h < 0.8 * ss && h > 0.3 * ss
                              && HeadInkWidth(n.HeadX, n.HeadY, ss) is double w && w < 0.95 * ss;
        // grace stems are short too (about 2.5 spaces from head to end against 3 or more)
        bool ShortStem(MusicSymbol s)
        {
            if (!s.Attributes.TryGetValue("stemx", out var sx)) return true;
            var parts = sx.Split(',');
            if (parts.Length < 3 || !double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double end)) return true;
            double far = s.StemUp == true ? s.Notes.Max(n => n.HeadY) : s.Notes.Min(n => n.HeadY);
            return Math.Abs(end - far) < 2.9 * ss;
        }
        foreach (var s in syms)
        {
            if (_opt.Verbose)
                foreach (var n in s.Notes.Concat(s.Children.SelectMany(c => c.Notes)))
                    s.Attributes["headh"] = (s.Attributes.GetValueOrDefault("headh") + " " + (HeadInkHeight(n.HeadX, n.HeadY, ss) is double hh ? (hh / ss).ToString("0.00") : "-")).Trim();
            if (s.Kind is SymbolKind.Note or SymbolKind.Chord && s.StemUp.HasValue && s.Notes.Count > 0 && s.Notes.All(Small) && ShortStem(s))
            {
                s.Attributes["grace"] = s.Flags == 1 ? "slash" : "1";
                s.Attributes.Remove("tremolo");
            }
            else if (s.Kind == SymbolKind.NoteGroup && s.Children.Count > 0 && s.Children.All(c => c.Notes.Count > 0 && c.Notes.All(Small) && ShortStem(c)))
            {
                s.Attributes["grace"] = "1";
                foreach (var c in s.Children) { c.Attributes["grace"] = "1"; c.Attributes.Remove("tremolo"); }
            }
        }
    }

    /// <summary>The vertical ink run through a head's centre (looking a fifth of a space to either side when the exact
    /// centre column misses), or null when there is no ink there (a hollow head).</summary>
    private double? HeadInkHeight(double cx, double cy, double ss)
    {
        int y = (int)Math.Round(cy);
        double? best = null;
        foreach (int dx in new[] { 0, -(int)(0.15 * ss), (int)(0.15 * ss) })
        {
            int x = (int)Math.Round(cx) + dx;
            if (!_ns[x, y]) continue;
            int t = y, b = y;
            while (_ns[x, t - 1] && y - t < 2 * ss) t--;
            while (_ns[x, b + 1] && b - y < 2 * ss) b++;
            double h = b - t + 1;
            if (best == null || h < best) best = h;
        }
        return best;
    }

    /// <summary>The ink width of a black head: the shortest run through the head's centre column over the rows a
    /// quarter space above and below its centre (null when there is no ink there).</summary>
    private double? HeadInkWidth(double cx, double cy, double ss)
    {
        int x = (int)Math.Round(cx);
        double best = double.MaxValue;
        foreach (double off in new[] { -0.25, 0.25 })
        {
            int y = (int)Math.Round(cy + off * ss);
            int xs = x;
            // the centre column may just miss the ink of a small head: look a little to both sides
            if (!_ns[xs, y]) { for (int d = 1; d <= (int)(0.2 * ss) && !_ns[xs, y]; d++) xs = _ns[x - d, y] ? x - d : _ns[x + d, y] ? x + d : xs; }
            if (!_ns[xs, y]) return null;
            int l = xs, r = xs;
            while (_ns[l - 1, y] && xs - l < 3 * ss) l--;
            while (_ns[r + 1, y] && r - xs < 3 * ss) r++;
            best = Math.Min(best, r - l + 1);
        }
        return best;
    }

    /// <summary>A ledger line through or right above/below the head: a thin horizontal run of at least 1.3 staff spaces within one space of the centre.</summary>
    private bool HasLedgerLine(double cx, double cy, double ss)
    {
        int x = (int)Math.Round(cx);
        for (int dy = -(int)Math.Round(0.6 * ss); dy <= (int)Math.Round(0.6 * ss); dy++)
        {
            int y = (int)Math.Round(cy) + dy;
            foreach (int dx in new[] { 0, -3, 3 })
            {
                if (!_ns[x + dx, y]) continue;
                if (_ns.HorizontalRunAt(x + dx, y, out _) >= 1.3 * ss) return true;
            }
        }
        return false;
    }

    private string _beamWhy = "";

    private bool LooksLikeBeam(double cx, double cy, double ss, int stemSide)
    {
        int xStem = (int)Math.Round(cx + stemSide * 0.55 * ss);      // the stem column: beams start here and run away from the head
        int xFar = (int)Math.Round(cx - stemSide * 0.7 * ss);        // just outside the head on the other side (an accidental sits there, a beam runs on)
        int reach = (int)Math.Round(1.5 * ss), half = (int)Math.Max(2, Math.Round(0.3 * ss));
        int hitsStem = 0, hitsFar = 0;
        int minThick = (int)Math.Max(3, Math.Round(0.28 * ss)), maxThick = (int)Math.Round(1.4 * ss);   // up to two touching beams
        foreach (double off in new[] { -0.2, 0.0, 0.2 })
        {
            int y = (int)Math.Round(cy + off * ss);
            if (ContinuousBeam(xStem, y, stemSide, reach, half, minThick, maxThick)) hitsStem++;
            if (ContinuousBeam(xFar, y, -stemSide, reach, half, minThick, maxThick)) hitsFar++;
        }
        _beamWhy = hitsStem >= 2 ? "s" : hitsFar >= 2 ? "f" : "";
        return hitsStem >= 2 || hitsFar >= 2;
    }

    /// <summary>Beam-like ink along a walk: in (almost) every column a vertical run of beam thickness passes through the band.</summary>
    private bool ContinuousBeam(int x, int y, int dir, int reach, int half, int minThick, int maxThick)
    {
        int gap = 0;
        for (int i = 0; i <= reach; i++)
        {
            int xx = x + dir * i;
            bool beamLike = false;
            for (int dy = -half; dy <= half && !beamLike; dy++)
            {
                if (!_ns[xx, y + dy]) continue;
                int len = _ns.VerticalRunAt(xx, y + dy, out int top);
                beamLike = len >= minThick && len <= maxThick;
                dy = top + len - y;   // skip the rest of this run
            }
            if (beamLike) gap = 0;
            else if (++gap > 3) return false;
        }
        return true;
    }

    private bool ContinuousBand(int x, int y, int dir, int reach, int half)
    {
        int gap = 0;
        for (int i = 0; i <= reach; i++)
        {
            bool ink = false;
            for (int dy = -half; dy <= half && !ink; dy++) ink = _ns[x + dir * i, y + dy];
            if (ink) gap = 0;
            else if (++gap > 3) return false;
        }
        return true;
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
                    if (Math.Abs(dx) < 0.75 && Math.Abs(dy) > 1.0) continue;   // directly above/below: other chord heads live there
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

    private int CountFlagsOrBeams(Stem st, Component c, Staff staff, bool single = false)
    {
        double ss = staff.Space;
        int label = c.Label;
        // beams and flags are thick bands; ledger lines crossing a stem are thinner (thin flag strokes are picked up
        // by the detached-flag pass instead: counting them here cost durations on every synthetic set)
        int minBand = Math.Max(3, (int)Math.Round(0.3 * ss));
        // the free end of the stem is opposite to the heads
        int yEnd = st.Up ? st.Top : st.Bottom;
        int best = Count(2.6);
        // a fourth beam (64ths) lies beyond the usual reach: look further only when three were found
        if (best == 3) best = Math.Max(best, Count(3.3));
        return Math.Min(best, 4);

        int Count(double reach)
        {
        int best = 0;
        int outW = Math.Max(2, (int)Math.Round(0.3 * ss));
        bool Wide(int x, int y0, int y1) => x >= c.X0 && x <= c.X1 && Enumerable.Range(y0, y1 - y0 + 1).Any(y => _cm.Labels[y * _cm.Width + x] == label);
        foreach (double off in new[] { 0.25, 0.45, 0.7, -0.25, -0.45, -0.7 })
        {
            int dx = (int)Math.Round(off * ss);
            int x = (dx > 0 ? st.X1 : st.X0) + dx;
            if (x < c.X0 || x > c.X1) continue;
            int bands = 0, run = 0, runTop = 0;
            int yFrom = st.Up ? yEnd - (int)(0.3 * ss) : yEnd - (int)(reach * ss);
            int yTo = st.Up ? yEnd + (int)(reach * ss) : yEnd + (int)(0.3 * ss);
            // stop when reaching the head area
            if (st.Up) yTo = Math.Min(yTo, (int)(st.Heads.Min(h => h.HeadY) - 0.8 * ss));
            else yFrom = Math.Max(yFrom, (int)(st.Heads.Max(h => h.HeadY) + 0.8 * ss));
            for (int y = yFrom; y <= yTo + 1; y++)
            {
                bool on = y >= c.Y0 && y <= c.Y1 && _cm.Labels[y * _cm.Width + x] == label;
                if (on) { if (run == 0) runTop = y; run++; }
                else
                {
                    // a beam or flag runs sideways: a smudge or sliver beside the stem has no ink 0.3 ss further out
                    if (run >= minBand && Wide(x + Math.Sign(dx) * outW, runTop, runTop + run - 1)) bands++;
                    run = 0;
                }
            }
            if (bands > best) best = bands;
        }
        return best;
        }
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

    /// <summary>
    /// A bar line of this staff, or one the other staves of the system have and this one lacks (a repeat sign, its dots
    /// read as heads): a note there whose stem fills the staff height is that bar line.
    /// </summary>
    private void BarlinesReadAsStems(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        var sys = _page.Systems.FirstOrDefault(s => s.Staves.Contains(staff));
        // the staff's own heavy, double and repeat bar lines (a note may stand right at a single one): the note's box
        // takes in the line itself (a chord right beside it does not); a line
        // only the other staves have: near its position
        var xs = staff.Barlines.Where(b => b.Kind is not (BarlineKind.SystemStart or BarlineKind.Single)).Select(b => (x: (b.X0 + b.X1) / 2.0, reach: 0.0)).ToList();
        if (sys != null && sys.Staves.Count > 1)
            xs.AddRange(sys.MeasureBoundaries.Where(x => !staff.Barlines.Any(b => b.X0 - 0.6 * ss <= x && b.X1 + 0.6 * ss >= x)).Select(x => (x, reach: 0.4 * ss)));
        foreach (var (x, reach) in xs)
            syms.RemoveAll(s => s.Kind is SymbolKind.Note or SymbolKind.Chord && s.Bounds.X0 <= x + reach && s.Bounds.X1 >= x - reach
                                && s.Bounds.Y0 <= staff.TopY(x) + 0.3 * ss && s.Bounds.Y1 >= staff.BottomY(x) - 0.3 * ss
                                && s.Notes.All(n => Math.Abs(n.HeadX - x) <= 1.2 * ss));
    }

    /// <summary>Stemless whole notes one above the other (within half a space across) are one chord.</summary>
    private static void StackedWholeNotes(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        var wholes = syms.Where(s => s.Kind is SymbolKind.Note or SymbolKind.Chord && s.StemUp == null && s.Notes.Count > 0 && s.Notes.All(n => n.HeadType == "whole"))
                         .OrderBy(s => s.Notes[0].HeadX).ToList();
        foreach (var w in wholes)
        {
            if (!syms.Contains(w)) continue;
            foreach (var o in wholes.Where(o => o != w && syms.Contains(o) && Math.Abs(o.Notes[0].HeadX - w.Notes[0].HeadX) <= 0.5 * ss
                                                && o.Notes.All(n => w.Notes.All(m => m.Step != n.Step))).ToList())
            {
                w.Notes.AddRange(o.Notes);
                w.Bounds = w.Bounds.Union(o.Bounds);
                syms.Remove(o);
            }
            if (w.Notes.Count > 1) { w.Kind = SymbolKind.Chord; w.Name = "chord"; w.Notes.Sort((a, b) => b.Step.CompareTo(a.Step)); }
        }
    }

    /// <summary>
    /// A double sharp (a small x) is often read as text: a small square glyph right before a head at its height that
    /// matches the double sharp template.
    /// </summary>
    private void DoubleSharpsReadAsText(List<MusicSymbol> syms, Staff staff)
    {
        if (_lib == null) return;
        double ss = staff.Space;
        var heads = syms.Where(s => s.Kind is SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup).SelectMany(s => s.Notes.Concat(s.Children.SelectMany(c => c.Notes))).ToList();
        foreach (var t in syms.Where(s => s.Kind is SymbolKind.Text or SymbolKind.Unknown && (!s.Attributes.TryGetValue("glyphs", out var g) || g == "1")
                                          && s.Bounds.Width >= 0.4 * ss && s.Bounds.Width <= 1.0 * ss && s.Bounds.Height >= 0.4 * ss && s.Bounds.Height <= 1.0 * ss
                                          && Math.Abs(s.Bounds.Width - s.Bounds.Height) <= 0.3 * ss).ToList())
        {
            var r = t.Bounds;
            if (!heads.Any(h => h.Head.X0 >= r.X1 - 0.1 * ss && h.Head.X0 - r.X1 <= 0.8 * ss && Math.Abs(h.HeadY - r.CenterY) <= 0.4 * ss)) continue;
            int w = r.Width, hh = r.Height;
            var mask = new byte[w * hh];
            for (int y = 0; y < hh; y++)
                for (int x = 0; x < w; x++)
                    if (_ns[r.X0 + x, r.Y0 + y]) mask[y * w + x] = 1;
            // an x is open; a solid blob there is a heavy dot (of the note before it)
            if (mask.Count(v => v != 0) > 0.6 * w * hh) continue;
            var m = _lib.Match(mask, w, hh, ss, 0.4, g => g.Kind == SymbolKind.Accidental);
            if (m.Count == 0 || m[0].Template.Name != "accidentalDoubleSharp" || m[0].Score < 0.55) continue;
            t.Kind = SymbolKind.Accidental; t.Name = "accidentalDoubleSharp"; t.Confidence = Math.Round(m[0].Score, 2);
            t.Step = (int)Math.Round(staff.StepAt(r.CenterX, r.CenterY));
        }
    }

    /// <summary>
    /// A natural or sharp whose box reads as a hollow head on a short stem (often where a slur touches it): a "half note"
    /// a short one (under 3.2 ss tall) right before another head at about its height is matched against the accidentals and
    /// becomes the best one, or goes when none fits.
    /// </summary>
    private void AccidentalsReadAsHalfNotes(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        var heads = syms.Where(s => s.Kind is SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup).SelectMany(s => s.Notes.Concat(s.Children.SelectMany(c => c.Notes))).ToList();
        foreach (var h in syms.Where(s => s.Kind == SymbolKind.Note && s.Notes.Count == 1 && s.Notes[0].HeadType == "half" && s.StemUp != null && s.Bounds.Height < 3.2 * ss).ToList())
        {
            var n = h.Notes[0];
            if (!heads.Any(o => o != n && o.Head.X0 > n.HeadX && o.Head.X0 - n.Head.X1 <= 1.4 * ss && Math.Abs(o.HeadY - n.HeadY) <= 1.0 * ss)) continue;
            var r = h.Bounds;
            int w = r.Width, hh = r.Height;
            var mask = new byte[w * hh];
            for (int y = 0; y < hh; y++)
                for (int x = 0; x < w; x++)
                    if (_ns[r.X0 + x, r.Y0 + y]) mask[y * w + x] = 1;
            var m = _lib?.Match(mask, w, hh, ss, 0.4, t => t.Kind == SymbolKind.Accidental);
            if (m != null && m.Count > 0 && m[0].Score >= 0.5)
            {
                h.Kind = SymbolKind.Accidental; h.Name = m[0].Template.Name; h.Confidence = Math.Round(m[0].Score, 2);
                h.Notes.Clear(); h.StemUp = null; h.Duration = null; h.Flags = 0;
                h.Attributes["accidental"] = "read as a half note";
            }
            else syms.Remove(h);
        }
    }

    /// <summary>A thin horizontal ink run (a tie, not a beam or head) of 1.5 spaces or more starting within 0.4 spaces
    /// right of the box, at its rows, outside the notes.</summary>
    private bool TieRightOf(Rect b, double ss, List<MusicSymbol> syms)
    {
        // (ink inside a note or beamed group, its beam or the next head, is no tie)
        var notes = syms.Where(s => s.Kind is SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup).Select(s => s.Bounds).ToList();
        for (int y = b.Y0; y <= b.Y1; y++)
            for (int x = b.X1 + 1; x <= b.X1 + (int)Math.Round(0.4 * ss); x++)
                if (x < _ns.Width && _ns[x, y] && !notes.Any(r => r.Contains(x, y)) && _ns.HorizontalRunAt(x, y, out _) >= 1.5 * ss && _ns.VerticalRunAt(x, y, out _) <= 0.45 * ss) return true;
        return false;
    }

    /// <summary>The leftmost and rightmost long vertical strokes in the box: the left one starts and ends a third of a
    /// space or more higher than the right one (a natural).</summary>
    private bool NaturalStrokes(int x0, int y0, int x1, int y1, double ss)
    {
        var cols = new List<(int x, int top, int bot)>();
        for (int x = x0; x <= x1; x++)
        {
            int run = 0, best = 0, end = 0;
            for (int y = y0; y <= y1; y++) { if (_ns[x, y]) { run++; if (run > best) { best = run; end = y; } } else run = 0; }
            if (best >= 1.2 * ss) cols.Add((x, end - best + 1, end));
        }
        if (cols.Count < 2 || cols[^1].x - cols[0].x < 0.3 * ss) return false;
        var left = cols.Where(c => c.x <= cols[0].x + 2).ToList();
        var right = cols.Where(c => c.x >= cols[^1].x - 2).ToList();
        double lt = left.Average(c => c.top), lb = left.Average(c => c.bot), rt = right.Average(c => c.top), rb = right.Average(c => c.bot);
        return rt - lt >= 0.35 * ss && rb - lb >= 0.35 * ss;
    }

    private static readonly bool AccInkDump = Environment.GetEnvironmentVariable("PVOMR_ACCINK") == "1";
    private static readonly double[] Slopes = { 0, 0.2, -0.2, 0.4, -0.4, 0.6, -0.6 };
    private static readonly (int page, int staff, int x)? AccInkAt =
        Environment.GetEnvironmentVariable("PVOMR_ACCINK_AT") is string at && at.Split(',') is { Length: 3 } p ? (int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2])) : null;

    /// <summary>
    /// An accidental joined to other ink (a beam it touches, the ledger line through a flat, a slur) is no symbol of its
    /// own and is not found by the passes above. For each head still without an accidental, the ink left of it is taken
    /// as it is, without the long strokes of other symbols: stems (vertical runs longer than an accidental) and beams,
    /// ledger lines and slurs (horizontal runs longer than an accidental is wide), and without other heads. What is left
    /// must have an accidental's vertical stroke at the head's height; the glyph classifier, sure of it, decides.
    /// </summary>
    private void AccidentalsInInk(List<MusicSymbol> syms, Staff staff)
    {
        if (_clf == null) return;
        double ss = staff.Space;
        var heads = syms.Where(s => s.Kind is SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup)
                        .SelectMany(s => s.Notes.Concat(s.Children.SelectMany(c => c.Notes))).Distinct().ToList();
        // ink that belongs to other symbols: heads, and signs already read (accidentals, key, clef, time, rests, numbers)
        var taken = heads.Select(n => n.Head)
                         .Concat(syms.Where(s => s.Kind is SymbolKind.Accidental or SymbolKind.KeySignature or SymbolKind.Clef or SymbolKind.TimeSignature
                                                  or SymbolKind.Rest or SymbolKind.Number or SymbolKind.Dynamic).Select(s => s.Bounds)).ToList();
        int maxV = (int)Math.Round(3.3 * ss), maxH = (int)Math.Round(1.5 * ss);
        foreach (var h in heads.Where(n => n.Accidental == int.MinValue))
        {
            int wx0 = (int)Math.Round(h.Head.X0 - 1.7 * ss), wx1 = h.Head.X0 - 2;
            int wy0 = (int)Math.Round(h.HeadY - 2.0 * ss), wy1 = (int)Math.Round(h.HeadY + 2.0 * ss);
            int w = wx1 - wx0 + 1, hh = wy1 - wy0 + 1;
            if (w < 0.6 * ss || wx0 < 0 || wy0 < 0 || wx1 >= _ns.Width || wy1 >= _ns.Height) continue;
            var mask = new byte[w * hh];
            int ink = 0;
            for (int y = wy0; y <= wy1; y++)
                for (int x = wx0; x <= wx1; x++)
                {
                    if (!_ns[x, y]) continue;
                    if (taken.Any(r => x >= r.X0 - 1 && x <= r.X1 + 1 && y >= r.Y0 - 1 && y <= r.Y1 + 1)) continue;
                    mask[(y - wy0) * w + (x - wx0)] = 1; ink++;
                }
            if (ink < 0.5 * ss * ss) continue;
            // long strokes of other symbols, measured around the window: first the horizontal ones (beams, ledger lines,
            // slurs), then the vertical ones on what is left (stems; an accidental touching a beam is no longer than itself)
            // (beams slope: the run is measured along a few slopes, the longest counts)
            int ex0 = wx0 - maxH - 1, ey0 = wy0 - maxV - 1, ew = w + 2 * maxH + 2, eh = hh + 2 * maxV + 2;
            var hLong = new bool[ew * eh];
            for (int y = 0; y < eh; y++)
                for (int x = maxH + 1; x < maxH + 1 + w; x++)
                {
                    int px = ex0 + x, py = ey0 + y;
                    if (!_ns[px, py]) continue;
                    foreach (double slope in Slopes)
                    {
                        int len = 1;
                        for (int d = 1; d <= maxH && _ns[px + d, py + (int)Math.Round(slope * d)]; d++) len++;
                        for (int d = 1; d <= maxH && _ns[px - d, py - (int)Math.Round(slope * d)]; d++) len++;
                        if (len > maxH) { hLong[y * ew + x] = true; break; }
                    }
                }
            var vLong = new bool[ew * eh];
            for (int x = 0; x < ew; x++)
                for (int y = 0; y < eh;)
                {
                    bool On(int yy) => _ns[ex0 + x, ey0 + yy] && !hLong[yy * ew + x];
                    if (!On(y)) { y++; continue; }
                    int s0 = y; while (y < eh && On(y)) y++;
                    if (y - s0 > maxV || s0 == 0 || y == eh) for (int k = s0; k < y; k++) vLong[k * ew + x] = true;
                }
            var keep = new byte[w * hh];
            for (int y = 0; y < hh; y++)
                for (int x = 0; x < w; x++)
                {
                    int e = (y + wy0 - ey0) * ew + (x + wx0 - ex0);
                    if (mask[y * w + x] != 0 && !hLong[e] && !vLong[e]) keep[y * w + x] = 1;
                }
            // a ledger line or beam edge crossing an accidental's stroke leaves a gap: bridged where ink continues on both sides
            int gap = (int)Math.Round(0.35 * ss);
            for (int x = 0; x < w; x++)
                for (int y = 1; y < hh - 1; y++)
                {
                    if (keep[y * w + x] != 0 || mask[y * w + x] == 0) continue;
                    bool above = false, below = false;
                    for (int d = 1; d <= gap && !above; d++) above = y - d >= 0 && keep[(y - d) * w + x] != 0;
                    for (int d = 1; d <= gap && !below; d++) below = y + d < hh && keep[(y + d) * w + x] != 0;
                    if (above && below) keep[y * w + x] = 2;
                }
            for (int i = 0; i < keep.Length; i++) if (keep[i] == 2) keep[i] = 1;
            if (AccInkAt is var (ap, ast, ax) && ap == _page.PageNumber && ast == staff.Index + 1 && Math.Abs(h.HeadX - ax) < 80)
            {
                Console.WriteLine($"ACCINK window page {ap} staff {ast} head x={h.HeadX:0} y={h.HeadY:0} ({w}x{hh}; # kept, + removed ink)");
                for (int y = 0; y < hh; y++) { var sb = new System.Text.StringBuilder(); for (int x = 0; x < w; x++) sb.Append(keep[y * w + x] != 0 ? '#' : mask[y * w + x] != 0 ? '+' : '.'); Console.WriteLine(sb.ToString()); }
            }
            // the accidental's vertical strokes (each accidental has one at least 1.2 spaces long), near the head's height
            int sx0 = int.MaxValue, sx1 = -1, sTop = int.MaxValue, sBot = -1;
            for (int x = 0; x < w; x++)
            {
                int run = 0, best = 0, bestEnd = 0;
                for (int y = 0; y < hh; y++) { if (keep[y * w + x] != 0) { run++; if (run > best) { best = run; bestEnd = y; } } else run = 0; }
                if (best >= 1.2 * ss && best <= 3.2 * ss) { sx0 = Math.Min(sx0, x); sx1 = Math.Max(sx1, x); sTop = Math.Min(sTop, bestEnd - best + 1); sBot = Math.Max(sBot, bestEnd); }
            }
            if (sx1 < 0) continue;
            int cx0 = Math.Max(0, (int)Math.Round(sx0 - 0.4 * ss)), cx1 = Math.Min(w - 1, (int)Math.Round(sx1 + 0.55 * ss));
            int cy0 = Math.Max(0, (int)Math.Round(sTop - 0.2 * ss)), cy1 = Math.Min(hh - 1, (int)Math.Round(sBot + 0.2 * ss));
            // tight around the ink there
            {
                int ix0 = int.MaxValue, ix1 = -1, iy0 = int.MaxValue, iy1 = -1;
                for (int y = cy0; y <= cy1; y++)
                    for (int x = cx0; x <= cx1; x++)
                        if (keep[y * w + x] != 0) { ix0 = Math.Min(ix0, x); ix1 = Math.Max(ix1, x); iy0 = Math.Min(iy0, y); iy1 = Math.Max(iy1, y); }
                if (ix1 < 0) continue;
                cx0 = ix0; cx1 = ix1; cy0 = iy0; cy1 = iy1;
            }
            int cw = cx1 - cx0 + 1, ch = cy1 - cy0 + 1;
            if (AccInkAt is var (ap3, ast3, ax3) && ap3 == _page.PageNumber && ast3 == staff.Index + 1 && Math.Abs(h.HeadX - ax3) < 80)
                Console.WriteLine($"ACCINK strokes x {sx0}-{sx1} y {sTop}-{sBot}; crop x {cx0}-{cx1} y {cy0}-{cy1} ({cw}x{ch})");
            // (a little wider than an accidental: bits of a slur at the sides)
            if (cw < 0.3 * ss || cw > 1.7 * ss || ch < 1.3 * ss) continue;
            // the crop is classified in a few versions, the surest accidental reading counts: as cleaned; with the ink
            // of beams and ledger lines left in (only stems taken out: taking those away can bite into the accidental);
            // and, only when those find none, with a third or two thirds of a space cut off at the top or bottom (a slur
            // across the accidental's stem; cut, a natural can look like a sharp)
            var stemsOnly = new byte[w * hh];
            for (int y = 0; y < hh; y++)
                for (int x = 0; x < w; x++)
                    if (mask[y * w + x] != 0 && !vLong[(y + wy0 - ey0) * ew + (x + wx0 - ex0)]) stemsOnly[y * w + x] = 1;
            double Acc(GlyphClassifier.Prediction p) => p.Label.StartsWith("acc_") ? p.Probability : 0;
            GlyphClassifier.Prediction pr = default!; byte[] crop = Array.Empty<byte>(); double top = 0, bot = 0; bool any = false;
            int t1 = (int)Math.Round(ss / 3), t2 = (int)Math.Round(2 * ss / 3);
            foreach (var (tt, tb) in new[] { (0, 0), (t1, 0), (t2, 0), (0, t1), (0, t2) })
                foreach (var src in new[] { keep, stemsOnly })
                {
                    if ((tt > 0 || tb > 0) && any && Acc(pr) >= 0.6) break;
                    int ry0 = cy0 + tt, ry1 = cy1 - tb;
                    if (ry1 - ry0 + 1 < 1.3 * ss) continue;
                    int ix0 = int.MaxValue, ix1 = -1, iy0 = int.MaxValue, iy1 = -1;
                    for (int y = ry0; y <= ry1; y++)
                        for (int x = cx0; x <= cx1; x++)
                            if (src[y * w + x] != 0) { ix0 = Math.Min(ix0, x); ix1 = Math.Max(ix1, x); iy0 = Math.Min(iy0, y); iy1 = Math.Max(iy1, y); }
                    if (ix1 < 0 || iy1 - iy0 + 1 < 1.3 * ss || ix1 - ix0 + 1 < 0.3 * ss) continue;
                    int vw = ix1 - ix0 + 1, vh = iy1 - iy0 + 1;
                    var v = new byte[vw * vh];
                    for (int y = 0; y < vh; y++)
                        for (int x = 0; x < vw; x++)
                            v[y * vw + x] = src[(y + iy0) * w + x + ix0];
                    var p = _clf.Classify(v, vw, vh, ss);
                    if (any && Acc(p) <= Acc(pr)) continue;
                    pr = p; crop = v; cw = vw; ch = vh; top = wy0 + iy0; bot = wy0 + iy1; any = true;
                }
            if (!any) continue;
            double acy = pr.Label == "acc_flat" ? bot - 0.5 * ss : (top + bot) / 2.0;
            bool ok = pr.Label.StartsWith("acc_") && pr.Probability >= 0.6 && Math.Abs(acy - h.HeadY) <= 0.6 * ss;
            if (AccInkAt is var (ap2, ast2, ax2) && ap2 == _page.PageNumber && ast2 == staff.Index + 1 && Math.Abs(h.HeadX - ax2) < 80 && _lib != null)
            {
                var mt = _lib.Match(crop, cw, ch, ss, 0.3, t => t.Kind == SymbolKind.Accidental);
                Console.WriteLine($"ACCINK crop head x={h.HeadX:0}: cnn {pr.Label} {pr.Probability:0.00}; templates " + string.Join(", ", mt.Take(3).Select(m => $"{m.Template.Name} {m.Score:0.00}")));
                for (int y = 0; y < ch; y++) { var sb = new System.Text.StringBuilder(); for (int x = 0; x < cw; x++) sb.Append(crop[y * cw + x] != 0 ? '#' : '.'); Console.WriteLine(sb.ToString()); }
            }
            if (AccInkDump)
            {
                Console.WriteLine($"ACCINK staff {staff.Index + 1} head x={h.HeadX:0} y={h.HeadY:0} step {h.Step}: {pr.Label} {pr.Probability:0.00} centre {acy - h.HeadY:+0;-0}px {(ok ? "TAKEN" : "")}");
                var sb = new System.Text.StringBuilder();
                for (int y = 0; y < ch; y++) { for (int x = 0; x < cw; x++) sb.Append(crop[y * cw + x] != 0 ? '#' : '.'); sb.Append('\n'); }
                Console.Write(sb.ToString());
            }
            if (!ok) continue;
            string label = pr.Label;
            // (measured on the cleaned crop: around it are slurs and heads)
            if (label == "acc_sharp" && NaturalStrokes(crop, cw, ch, ss)) label = "acc_natural";
            h.Accidental = label switch { "acc_sharp" => 1, "acc_flat" => -1, _ => 0 };
            h.AccidentalName = label switch { "acc_sharp" => "accidentalSharp", "acc_flat" => "accidentalFlat", _ => "accidentalNatural" };
            h.AccidentalUnsure = pr.Probability < 0.9;
        }
    }

    /// <summary>NaturalStrokes on a crop: the left vertical stroke stands clearly higher than the right one at both ends.</summary>
    private static bool NaturalStrokes(byte[] m, int w, int h, double ss)
    {
        var cols = new List<(int x, int top, int bot)>();
        for (int x = 0; x < w; x++)
        {
            int run = 0, best = 0, end = 0;
            for (int y = 0; y < h; y++) { if (m[y * w + x] != 0) { run++; if (run > best) { best = run; end = y; } } else run = 0; }
            if (best >= 1.2 * ss) cols.Add((x, end - best + 1, end));
        }
        if (cols.Count < 2 || cols[^1].x - cols[0].x < 0.3 * ss) return false;
        // each stroke where it is longest (in a heavy print a stroke widens at its ends into short columns)
        double mid = (cols[0].x + cols[^1].x) / 2.0;
        var l = cols.Where(c => c.x < mid).OrderByDescending(c => c.bot - c.top).FirstOrDefault();
        var r = cols.Where(c => c.x > mid).OrderByDescending(c => c.bot - c.top).FirstOrDefault();
        if (l == default || r == default || r.x - l.x < 0.3 * ss) return false;
        // (a slur joining one end shortens that difference: together they count, each clearly there)
        double dt = r.top - l.top, db = r.bot - l.bot;
        return dt >= 0.1 * ss && db >= 0.1 * ss && dt + db >= 0.6 * ss;
    }

    private double InkFill(Rect r)
    {
        int ink = 0;
        for (int y = r.Y0; y <= r.Y1; y++)
            for (int x = r.X0; x <= r.X1; x++)
                if (_ns[x, y]) ink++;
        return ink / (double)(r.Width * r.Height);
    }

    private void AttachDotsAndAccidentals(List<MusicSymbol> syms, Staff staff)
    {
        double ss = staff.Space;
        // heads with the symbol that owns them: for a beamed group the child, so a dot lands on the right note
        var notes = syms.Where(s => s.Kind is SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup)
                        .SelectMany(s => s.Kind == SymbolKind.NoteGroup && s.Children.Count > 0
                                         ? s.Children.SelectMany(c => c.Notes.Select(n => (sym: c, note: n)))
                                         : s.Notes.Select(n => (sym: s, note: n))).ToList();
        // a heavy augmentation dot (up to 0.75 ss, too big for the dot test) is often read as a text glyph: a solid
        // round blob close right of a head at head height, with no other note in between, is that dot
        foreach (var t in syms.Where(s => s.Kind is SymbolKind.Text or SymbolKind.Unknown && (!s.Attributes.TryGetValue("glyphs", out var gl) || gl == "1")).ToList())
        {
            var b = t.Bounds;
            double w = b.Width / ss, h = b.Height / ss;
            if (w < 0.4 || w > 0.75 || h < 0.4 || h > 0.75 || Math.Abs(w - h) > 0.2) continue;
            double step = staff.StepAt(b.CenterX, b.CenterY);
            if (step >= -0.5 && step <= 8.5 && ((int)Math.Round(step)) % 2 == 0 && Math.Abs(step - Math.Round(step)) <= 0.4) continue;   // dots sit in spaces
            int ink = 0;
            for (int y = b.Y0; y <= b.Y1; y++)
                for (int x = b.X0; x <= b.X1; x++)
                    if (_cm.Labels[y * _cm.Width + x] != 0) ink++;
            if (ink < 0.65 * b.Width * b.Height) continue;
            bool beside = notes.Any(p => b.CenterX > p.note.Head.X1 && b.X0 - p.note.Head.X1 <= 1.2 * ss && Math.Abs(b.CenterY - p.note.HeadY) <= 0.75 * ss
                                         && !notes.Any(q => q.note.Head.X0 > p.note.Head.X1 && q.note.Head.X0 < b.CenterX));
            if (beside) { t.Kind = SymbolKind.Dot; t.Name = "dot"; t.Confidence = 0.6; }
        }
        // the dot of a dotted rest standing on a staff line keeps only a sliver when the line is removed (clear of the
        // rest: a speck touching it is noise)
        foreach (var r in syms.Where(s => s.Kind == SymbolKind.Rest && s.Dots == 0 && s.Duration is "quarter" or "8th" or "16th" or "half").ToList())
        {
            var sliver = syms.FirstOrDefault(s => s.Kind == SymbolKind.Unknown && s.Bounds.Width >= 0.2 * ss && s.Bounds.Width <= 0.55 * ss && s.Bounds.Height <= 0.55 * ss
                                                  && s.Bounds.X0 >= r.Bounds.X1 + 0.4 * ss && s.Bounds.CenterX < r.Bounds.X1 + 1.4 * ss
                                                  && Math.Abs(staff.StepAt(s.Bounds.CenterX, s.Bounds.CenterY) / 2 - Math.Round(staff.StepAt(s.Bounds.CenterX, s.Bounds.CenterY) / 2)) <= 0.2
                                                  && s.Bounds.CenterY >= r.Bounds.Y0 - 0.6 * ss && s.Bounds.CenterY <= r.Bounds.Y1 + 0.3 * ss
                                                  && !notes.Any(p => p.note.HeadX > r.Bounds.X1 && p.note.Head.X0 < s.Bounds.CenterX));
            if (sliver != null) { r.Dots = 1; r.Attributes["dot"] = "sliver on a line"; syms.Remove(sliver); }
        }
        var dots = syms.Where(s => s.Kind == SymbolKind.Dot).OrderBy(s => s.Bounds.X0).ToList();
        var dotOf = new Dictionary<Note, MusicSymbol>();
        var accidentalBoxes = syms.Where(s => s.Kind is SymbolKind.Accidental or SymbolKind.KeySignature).Select(s => s.Bounds).ToList();
        foreach (var d in dots)
        {
            // a "dot" inside or touching an accidental is a piece of it (the end of a natural's stroke), not a dot
            int pad = (int)Math.Round(0.15 * ss);
            if (accidentalBoxes.Any(b => d.Bounds.X1 >= b.X0 - pad && d.Bounds.X0 <= b.X1 + pad && d.Bounds.Y1 >= b.Y0 - pad && d.Bounds.Y0 <= b.Y1 + pad)) { syms.Remove(d); continue; }
            // a dot is round: a sliver (the middle of a natural sign) is none
            double aspect = d.Bounds.Width / (double)Math.Max(1, d.Bounds.Height);
            if (aspect < 0.45 || aspect > 2.2) continue;
            // the broken-off tip of a tie: the tie's long body starts right after it
            if (TieRightOf(d.Bounds, ss, syms)) { syms.Remove(d); continue; }
            // augmentation dot: right of a head, roughly at head height (in a space); one per head, nearest in height,
            // and a second dot only when it lies beside the first (double dot), not above or below it
            // some engravers set the dot well clear of the head: further out only when no other note stands in between
            bool Reach(Note h) => d.Bounds.CenterX < h.Head.X1 + 1.4 * ss
                || d.Bounds.CenterX < h.Head.X1 + 2.1 * ss && !notes.Any(q => q.note.Head.X0 > h.Head.X1 && q.note.Head.X0 < d.Bounds.CenterX);
            var cand = notes.Where(p => d.Bounds.CenterX > p.note.Head.X1 + 0.05 * ss && Reach(p.note) && Math.Abs(d.Bounds.CenterY - p.note.HeadY) <= 0.75 * ss
                                        && (!dotOf.TryGetValue(p.note, out var prev) || (Math.Abs(prev.Bounds.CenterY - d.Bounds.CenterY) <= 0.3 * ss && d.Bounds.X0 > prev.Bounds.X1)))
                            .OrderBy(p => Math.Abs(d.Bounds.CenterY - p.note.HeadY)).ThenBy(p => Math.Abs(d.Bounds.CenterX - p.note.Head.X1)).FirstOrDefault();
            if (DotDump)
                Console.Error.WriteLine($"DOT {d.Bounds} -> {(cand.note == null ? "none" : $"head {cand.note.Head}")}; near: {string.Join(" ", notes.Where(p => Math.Abs(p.note.HeadY - d.Bounds.CenterY) < 1.5 * ss && p.note.Head.X1 < d.Bounds.X0 && d.Bounds.X0 - p.note.Head.X1 < 3 * ss).Select(p => p.note.Head.ToString()))}");
            if (cand.note != null)
            {
                // a head shared by two voices (two notes at the same place): the dot belongs to the longer note
                var shared = notes.Where(p => p.sym != cand.sym && Math.Abs(p.note.HeadX - cand.note.HeadX) <= 0.3 * ss && Math.Abs(p.note.HeadY - cand.note.HeadY) <= 0.3 * ss)
                                  .OrderBy(p => p.sym.Flags).FirstOrDefault();
                if (shared.note != null && shared.sym.Flags < cand.sym.Flags) cand = shared;
                cand.note.Dots++;
                cand.sym.Dots = Math.Max(cand.sym.Dots, cand.note.Dots);
                dotOf[cand.note] = d;
                syms.Remove(d);
                continue;
            }
            // a dotted rest: the dot right of the rest, within its height (a dotted quarter or 8th rest)
            var rest = syms.Where(r => r.Kind == SymbolKind.Rest && r.Dots == 0 && r.Duration is "quarter" or "8th" or "16th" or "half"
                                       && d.Bounds.CenterX > r.Bounds.X1 && d.Bounds.CenterX < r.Bounds.X1 + 1.4 * ss
                                       && d.Bounds.CenterY >= r.Bounds.Y0 - 0.3 * ss && d.Bounds.CenterY <= r.Bounds.Y0 + 0.6 * r.Bounds.Height)
                           .OrderBy(r => d.Bounds.CenterX - r.Bounds.X1).FirstOrDefault();
            if (rest != null && !notes.Any(p => p.note.HeadX > rest.Bounds.X1 && p.note.Head.X0 < d.Bounds.CenterX))
            {
                rest.Dots = 1;
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
        // an augmentation dot next to a staff line loses most of its ink to the staff removal and is left as a small
        // unknown or text fragment: such a fragment in the dot position of an undotted note makes it a dot candidate,
        // which the duration check takes only when the measure then adds up
        var frags = syms.Where(s => s.Kind is SymbolKind.Unknown or SymbolKind.Text && s.Bounds.Width <= 0.6 * ss && s.Bounds.Height <= 0.65 * ss
                                    && s.Bounds.Width >= 2 && s.Bounds.Height >= 2).ToList();
        foreach (var f in frags)
        {
            var cand = notes.Where(p => p.note.Dots == 0 && f.Bounds.CenterX > p.note.Head.X1 + 0.1 * ss
                                        && (f.Bounds.CenterX < p.note.Head.X1 + 1.2 * ss || f.Bounds.CenterX < p.note.Head.X1 + 2.1 * ss
                                            && !notes.Any(q => q.note.Head.X0 > p.note.Head.X1 && q.note.Head.X0 < f.Bounds.CenterX))
                                        && Math.Abs(f.Bounds.CenterY - p.note.HeadY) <= 0.7 * ss)
                            .OrderBy(p => Math.Abs(f.Bounds.CenterX - p.note.Head.X1)).FirstOrDefault();
            // not when another head lies between the head and the fragment (the fragment belongs to that one)
            if (cand.note == null || notes.Any(p => p.note != cand.note && p.note.HeadX > cand.note.HeadX + 0.3 * ss && p.note.Head.X0 < f.Bounds.CenterX)) continue;
            cand.sym.Attributes["dotcand"] = "1";
        }
        // a natural or sharp in pieces next to its head (one stroke left alone, the other merged into a stem): a thin
        // stroke of an accidental's height just left of a head without an accidental, the ink between them matched
        foreach (var u in syms.Where(s => s.Kind is SymbolKind.Unknown or SymbolKind.Text && s.Bounds.Width <= 0.6 * ss && s.Bounds.Height >= 2.0 * ss && s.Bounds.Height <= 3.4 * ss).ToList())
        {
            var hp = notes.Where(p => p.note.Accidental == int.MinValue && p.note.Head.X0 > u.Bounds.X1 && p.note.Head.X0 - u.Bounds.X1 <= 0.9 * ss
                                      && p.note.HeadY > u.Bounds.Y0 && p.note.HeadY < u.Bounds.Y1).OrderBy(p => p.note.Head.X0).FirstOrDefault();
            if (hp.note == null || _lib == null) continue;
            var r = new Rect(u.Bounds.X0, u.Bounds.Y0, hp.note.Head.X0 - 1, u.Bounds.Y1);
            int w = r.Width, hh = r.Height;
            if (w < 0.6 * ss || w > 1.5 * ss) continue;
            var mask = new byte[w * hh];
            for (int y = 0; y < hh; y++) for (int x = 0; x < w; x++) if (_ns[r.X0 + x, r.Y0 + y]) mask[y * w + x] = 1;
            var m = _lib.Match(mask, w, hh, ss, 0.4, t => t.Kind == SymbolKind.Accidental && !t.Name.Contains("Double"));
            if (m.Count == 0 || m[0].Score < 0.65) continue;
            string name = m[0].Template.Name;
            if (name == "accidentalSharp" && NaturalStrokes(r.X0, r.Y0, r.X1, r.Y1, ss)) name = "accidentalNatural";
            hp.note.AccidentalName = name;
            hp.note.Accidental = name switch { "accidentalSharp" => 1, "accidentalFlat" => -1, _ => 0 };
            hp.note.AccidentalUnsure = true;
            syms.Remove(u);
            syms.RemoveAll(d => d.Kind == SymbolKind.Dot && d.Bounds.X0 >= r.X0 && d.Bounds.X1 <= r.X1 && d.Bounds.Y0 >= r.Y0 && d.Bounds.Y1 <= r.Y1);
        }
        var accs = syms.Where(s => s.Kind == SymbolKind.Accidental).ToList();
        foreach (var a in accs)
        {
            double ay = AccidentalCenterY(a);
            var cand = notes.Where(p => a.Bounds.X1 < p.note.Head.X0 + 0.25 * ss && a.Bounds.X1 > p.note.Head.X0 - 1.6 * ss && Math.Abs(ay - p.note.HeadY) <= 0.6 * ss)
                            .OrderBy(p => p.note.Head.X0 - a.Bounds.X1).FirstOrDefault();
            if (cand.note != null)
            {
                // an old-style natural classified as a sharp: its strokes are offset (see NaturalStrokes)
                if (a.Name == "accidentalSharp" && NaturalStrokes(a.Bounds.X0, a.Bounds.Y0, a.Bounds.X1, a.Bounds.Y1, ss)) a.Name = "accidentalNatural";
                cand.note.AccidentalName = a.Name;
                cand.note.AccidentalUnsure = a.Confidence < 0.75;
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
            // the open end of a hairpin is two thin lines with paper between them; a slur's steep end is one stroke
            int wideX = lh > rh ? r.X0 + 1 : r.X1 - 1;
            // only the slur's own ink (the largest component in its box): a head or stem inside the box is no line
            var counts = new Dictionary<int, int>();
            for (int y = r.Y0; y <= r.Y1; y++) for (int x = r.X0; x <= r.X1; x++) { int l = _cm.Labels[y * _cm.Width + x]; if (l > 0) counts[l] = counts.GetValueOrDefault(l) + 1; }
            int own = counts.Count == 0 ? -1 : counts.OrderByDescending(kv => kv.Value).First().Key;
            bool twoLines = Enumerable.Range(0, Math.Max(1, (int)(0.6 * ss))).Any(d => InkRuns(wideX + (lh > rh ? d : -d), r, own) >= 2);
            // hairpins lie level (the point at the height of the open end's middle) and under the staff, not above it
            int pointX = lh > rh ? r.X1 - 1 : r.X0 + 1;
            double pointY = InkMidY(pointX, r, own), openY = InkMidY(wideX, r, own);
            bool level = !double.IsNaN(pointY) && !double.IsNaN(openY) && Math.Abs(pointY - openY) <= 0.35 * ss;
            bool above = r.CenterY < staff.TopY(r.CenterX);
            if (r.Height >= 0.6 * ss && ((lh <= 0.25 * ss && rh >= 0.5 * ss) || (rh <= 0.25 * ss && lh >= 0.5 * ss)) && mh < r.Height && twoLines && level && !above)
            {
                s.Kind = SymbolKind.Hairpin; s.Name = lh < rh ? "crescendo hairpin" : "diminuendo hairpin"; s.Confidence = 0.6;
            }
            else { s.Name = "slur/tie"; s.Confidence = 0.5; }
            s.Attributes["width"] = $"{r.Width / ss:0.0}ss";
            // a short arc with a dot under its middle is a fermata (arc and dot are separate pieces in old prints)
            if (s.Kind == SymbolKind.Slur && r.Width >= 1.0 * ss && r.Width <= 3.2 * ss)
            {
                var dot = syms.FirstOrDefault(d => d != s && d.Kind is SymbolKind.Dot or SymbolKind.Text or SymbolKind.Unknown
                                                   && d.Bounds.Width <= 0.7 * ss && d.Bounds.Height <= 0.7 * ss
                                                   && Math.Abs(d.Bounds.CenterX - r.CenterX) <= 0.4 * ss && d.Bounds.CenterY > r.Y0 && d.Bounds.CenterY < r.Y1 + 0.6 * ss);
                if (dot != null)
                {
                    bool overStaff = r.CenterY < staff.TopY(r.CenterX);
                    s.Kind = SymbolKind.Fermata; s.Name = overStaff ? "fermataAbove" : "fermataBelow"; s.Confidence = 0.7;
                    s.Bounds = s.Bounds.Union(dot.Bounds);
                    syms.Remove(dot);
                }
            }
        }
    }

    /// <summary>Middle of the ink of one component in a column of a rectangle (NaN when none).</summary>
    private double InkMidY(int x, Rect r, int label)
    {
        int top = -1, bot = -1;
        if (x < 0 || x >= _ns.Width) return double.NaN;
        for (int y = r.Y0; y <= r.Y1; y++)
            if (y >= 0 && y < _ns.Height && (label < 0 ? _ns[x, y] : _cm.Labels[y * _cm.Width + x] == label)) { if (top < 0) top = y; bot = y; }
        return top < 0 ? double.NaN : (top + bot) / 2.0;
    }

    /// <summary>Separate ink runs in one column of a rectangle.</summary>
    private int InkRuns(int x, Rect r, int label = -1)
    {
        if (x < 0 || x >= _ns.Width) return 0;
        int runs = 0; bool inRun = false;
        for (int y = r.Y0; y <= r.Y1; y++)
        {
            bool on = y >= 0 && y < _ns.Height && (label < 0 ? _ns[x, y] : _cm.Labels[y * _cm.Width + x] == label);
            if (on && !inRun) runs++;
            inRun = on;
        }
        return runs;
    }

    private int InkHeight(int x, Rect r)
    {
        int top = -1, bot = -1;
        for (int y = r.Y0; y <= r.Y1; y++) if (_ns[x, y]) { if (top < 0) top = y; bot = y; }
        return top < 0 ? 0 : bot - top + 1;
    }
}
