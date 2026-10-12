using PVOMR.Core.Model;

namespace PVOMR.Core.Score;

/// <summary>
/// Converts page analyses (geometry + classified symbols) into the semantic score model: parts, measures,
/// voices, events with durations, directions, slurs/ties, with validation against the time signature.
/// </summary>
public static class MusicAssembler
{
    private static readonly string[] Letters = { "C", "D", "E", "F", "G", "A", "B" };

    public sealed class Options
    {
        /// <summary>null = auto (two staves per system with treble+bass become one two-staff part).</summary>
        public bool? GrandStaff { get; set; }
        public string? Title { get; set; }
        /// <summary>When set, receives the score measure built from every recognised measure (for interactive checks).</summary>
        public Dictionary<Measure, ScoreMeasure>? MeasureMap { get; set; }
        /// <summary>Filled with MeasureMap: the staff number (1 or 2) each recognised measure became within its part.</summary>
        public Dictionary<Measure, int>? MeasureStaff { get; set; }
    }

    private sealed class StaffContext
    {
        public ClefKind Clef = ClefKind.Unknown;
        public int? Key;
        public TimeSpec? Time;
        public int MeasureNumber;
        public int SlurCounter;
    }

    public static Score Build(IReadOnlyList<PageAnalysis> pages, Options? options = null)
    {
        options ??= new Options();
        var score = new Score { Title = options.Title, Source = pages.Count > 0 ? pages[0].Source : null };
        var systems = pages.SelectMany(p => p.Systems.Select(s => (page: p, sys: s))).ToList();
        if (systems.Count == 0) { score.Warnings.Add("no systems found"); return score; }

        int maxStaves = systems.Max(s => s.sys.Staves.Count);
        // reference layout: the first system with the full staff count; other systems are aligned to it by clef
        var reference = systems.First(s => s.sys.Staves.Count == maxStaves).sys;
        if (systems.Any(s => s.sys.Staves.Count != maxStaves))
            score.Warnings.Add("systems have different staff counts; staves are aligned to the fullest system by clef and order; absent parts get rest measures");
        // parts: one per staff, except keyboard (and harp) staves joined by a brace, which form one two-staff part
        var layout = PartLayout(systems.Select(s => s.sys).ToList(), reference, options.GrandStaff);
        int partCount = layout.Count;
        int keyboardParts = layout.Count(l => l.Length == 2), keyboards = 0;
        for (int i = 0; i < partCount; i++)
        {
            bool two = layout[i].Length == 2;
            string name = two ? (keyboardParts > 1 ? $"Piano {++keyboards}" : "Piano") : partCount == 1 ? "Part" : $"Part {i + 1}";
            score.Parts.Add(new Part { Id = $"P{i + 1}", Name = name, StaffCount = two ? 2 : 1 });
        }
        // reference staff -> (part, staff number within the part)
        var partOf = new (int part, int staffNo)[maxStaves];
        for (int i = 0; i < partCount; i++)
            for (int k = 0; k < layout[i].Length; k++) partOf[layout[i][k]] = (i, k + 1);

        var namesOf = Enumerable.Range(0, partCount).Select(_ => new List<string>()).ToArray();
        var contexts = new StaffContext[partCount * 2];
        for (int i = 0; i < contexts.Length; i++) contexts[i] = new StaffContext();
        var partMeasureCount = new int[partCount];
        int page = 0; PageAnalysis? lastPage = null;

        foreach (var (pg, sys) in systems)
        {
            bool newPage = pg != lastPage; lastPage = pg; if (newPage) page++;
            // measures of the system: by boundary index; each staff has its own Measure list aligned by x
            int measureCount = sys.Staves.Max(s => s.Measures.Count);
            int[] refOfStaff = AlignStaves(sys, reference);
            // staves in a compound metre written against simple time on another staff of the system (9/8 over 3/4):
            // their notes are triplets of the simple metre
            var sysTimes = sys.Staves.Select(st => ParseTime(st.TimeSignature)).ToList();
            var tripletStaves = sys.Staves.Where((st, k) => sysTimes[k] is TimeSpec t && sysTimes.Any(o => o != null && t.TripletsAgainst(o))).ToHashSet();
            // this system's staff for each (part, staff number), if present
            var staffAt = new int[partCount, 2];
            for (int i = 0; i < partCount; i++) { staffAt[i, 0] = -1; staffAt[i, 1] = -1; }
            for (int k = 0; k < sys.Staves.Count; k++)
            {
                var (pi, sn) = partOf[Math.Min(refOfStaff[k], maxStaves - 1)];
                if (staffAt[pi, sn - 1] < 0) staffAt[pi, sn - 1] = k;
                if (sys.Staves[k].InstrumentName is string nm) namesOf[pi].Add(nm);
            }
            // a sliver at the system start holding only clef, key and time (a thick or repeat bar line right after the
            // time signature) is no measure of its own: its signs go into the next measure
            bool sliver = measureCount > 1 && sys.Staves.All(st => st.Measures.Count > 1 && !st.Measures[0].Symbols.Any(IsMusic));
            int firstMi = sliver ? 1 : 0;
            for (int mi = firstMi; mi < measureCount; mi++)
            {
                for (int pi = 0; pi < partCount; pi++)
                {
                    var part = score.Parts[pi];
                    var sm = new ScoreMeasure { Page = pg.PageNumber, NewSystem = mi == firstMi, NewPage = mi == firstMi && newPage };
                    bool any = false;
                    for (int sIdx = 0; sIdx < part.StaffCount; sIdx++)
                    {
                        int staffPos = staffAt[pi, sIdx];
                        if (staffPos < 0) continue;
                        var staff = sys.Staves[staffPos];
                        if (mi >= staff.Measures.Count) continue;
                        var m = staff.Measures[mi];
                        var built = sliver && mi == 1 ? Merge(staff.Measures[0], m) : m;
                        any = true;
                        var ctx = contexts[pi * 2 + sIdx];
                        int otherPos = part.StaffCount == 2 ? staffAt[pi, 1 - sIdx] : -1;
                        var sibling = otherPos >= 0 ? sys.Staves[otherPos] : null;
                        BuildMeasure(sm, built, staff, sIdx + 1, ctx, mi == firstMi, partMeasureCount[pi] == 0, sibling, sibling != null ? 2 - sIdx : 0, tripletStaves.Contains(staff));
                        if (options.MeasureMap != null)
                        {
                            options.MeasureStaff ??= new();
                            foreach (var mm in sliver && mi == 1 ? new[] { staff.Measures[0], m } : new[] { m }) { options.MeasureMap[mm] = sm; options.MeasureStaff[mm] = sIdx + 1; }
                        }
                    }
                    if (!any)
                    {
                        // the part has no staff in this system (instrument omitted): a whole-measure rest keeps the parts aligned
                        var ctx = contexts[pi * 2];
                        sm.Events.Add(new ScoreEvent { IsRest = true, MeasureRest = true, Type = "whole", Duration = ctx.Time?.Length ?? 4 * Score.Divisions, Voice = 1, Staff = 1 });
                        if (partMeasureCount[pi] == 0) { sm.Clefs[0] = ClefSpec.Treble; sm.KeyFifths = 0; }
                    }
                    partMeasureCount[pi]++;
                    sm.Number = partMeasureCount[pi];
                    part.Measures.Add(sm);
                }
            }
        }
        NameParts(score, namesOf, pages);
        foreach (var part in score.Parts) FinishPart(part, score);
        return score;
    }

    private static bool IsMusic(MusicSymbol s) => s.Kind is SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup or SymbolKind.Rest;

    /// <summary>A system-start sliver and the measure after it as one measure (the sliver's left bar line is the system line).</summary>
    private static Measure Merge(Measure sliver, Measure next)
    {
        var m = new Measure { Index = next.Index, X0 = sliver.X0, X1 = next.X1, Staff = next.Staff, LeftBarline = next.LeftBarline ?? sliver.LeftBarline, RightBarline = next.RightBarline };
        m.Symbols.AddRange(sliver.Symbols);
        m.Symbols.AddRange(next.Symbols);
        return m;
    }

    /// <summary>
    /// Maps the staves of a system to part indices of the reference system. Equal counts map by position; a
    /// system with fewer staves (instruments omitted) is aligned monotonically to the reference by clef kind.
    /// </summary>
    private static int[] AlignStaves(StaffSystem sys, StaffSystem reference)
    {
        int n = sys.Staves.Count, m = reference.Staves.Count;
        if (n >= m) return Enumerable.Range(0, n).Select(i => Math.Min(i, m - 1)).ToArray();
        // dynamic programming: cost 0 for matching clef, 1 for a different one, 0.6 when either clef is unknown; skipping reference staves is free
        var cost = new double[n + 1, m + 1];
        for (int i = 1; i <= n; i++) for (int j = 0; j <= m; j++) cost[i, j] = double.PositiveInfinity;
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
            {
                double c = StaffCost(sys.Staves[i - 1], reference.Staves[j - 1], reference);
                cost[i, j] = Math.Min(cost[i - 1, j - 1] + c, cost[i, j - 1]);
            }
        var result = new int[n];
        int a = n, b = m;
        while (a > 0 && b > 0)
        {
            double c = StaffCost(sys.Staves[a - 1], reference.Staves[b - 1], reference);
            if (Math.Abs(cost[a, b] - (cost[a - 1, b - 1] + c)) < 1e-9) { result[a - 1] = b - 1; a--; b--; }
            else b--;
        }
        while (a > 0) { result[a - 1] = Math.Max(0, a - 1); a--; }
        return result;
    }

    /// <summary>How unlikely it is that a staff of a sparser system is the given staff of the reference system: the
    /// instrument names decide when both were read and understood, otherwise the clefs.</summary>
    private static double StaffCost(Staff s, Staff reference, StaffSystem refSystem)
    {
        var a = Instruments.Match(s.InstrumentName); var b = Instruments.Match(reference.InstrumentName);
        if (a != null && b != null) return a.Id == b.Id ? 0 : 2;
        // a named staff should not take an unnamed reference staff while the reference names that instrument elsewhere
        if (a != null && refSystem.Staves.Any(r => r != reference && Instruments.Match(r.InstrumentName)?.Id == a.Id)) return 2;
        return ClefCost(s.Clef, reference.Clef);
    }

    /// <summary>
    /// Part names from the instrument names read left of the staves: the longest reading is the full name (first
    /// system), the shortest one a different abbreviation; the instrument (playback sound) is the majority match. A
    /// single part without names takes an instrument named in a short line of the first page's header ("Violoncello").
    /// </summary>
    private static void NameParts(Score score, List<string>[] namesOf, IReadOnlyList<PageAnalysis> pages)
    {
        var bassWord = new bool[score.Parts.Count];
        for (int i = 0; i < score.Parts.Count; i++)
        {
            var part = score.Parts[i];
            var readings = namesOf[i].Select(n => (text: n, inst: Instruments.Match(n, out bool bw, out bool fz), bw, fz)).ToList();
            var known = readings.Where(r => r.inst != null).ToList();
            if (known.Count > 0)
            {
                var top = known.GroupBy(r => r.inst!.Id).OrderByDescending(g => g.Count()).First().ToList();
                part.Instrument = top[0].inst;
                bassWord[i] = top.All(r => r.bw);
                // prefer readings that matched without OCR tolerance; the longest is the full name
                var fullR = top.OrderBy(r => r.fz).ThenByDescending(r => r.text.Length).First();
                var shortR = top.OrderBy(r => r.fz).ThenBy(r => r.text.Length).First();
                string full = Instruments.DisplayName(fullR.text, fullR.inst!, fullR.fz), shortest = Instruments.DisplayName(shortR.text, shortR.inst!, shortR.fz);
                part.Name = full;
                if (shortest.Length < full.Length && shortest.Length <= 8) part.Abbreviation = shortest;
            }
            else if (readings.Count > 0)
            {
                // a name that is not in the table is still printed text; keep the most frequent longer reading
                var name = readings.GroupBy(r => r.text).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key.Length).First().Key;
                if (Instruments.LooksLikeName(name)) part.Name = name;
            }
            if (part.Instrument == null && part.StaffCount == 2) part.Instrument = Instruments.Match("Piano");
        }
        // "Basso" is the bass voice when the other parts are voices, otherwise the double basses
        if (score.Parts.Count(p => p.Instrument?.Voice == true) >= 2)
            for (int i = 0; i < score.Parts.Count; i++) if (bassWord[i]) score.Parts[i].Instrument = Instruments.Bass;
        if (score.Parts.Count == 1 && score.Parts[0].Instrument == null && score.Parts[0].StaffCount == 1)
        {
            foreach (var line in pages.Take(1).SelectMany(p => p.HeaderText))
            {
                if (Instruments.Tokens(line).Count > 3) continue;
                var inst = Instruments.Match(line, out bool bw);
                if (inst == null || bw && Instruments.Tokens(line).Count == 1 && Instruments.Tokens(line)[0].Length <= 1) continue;
                score.Parts[0].Instrument = inst; score.Parts[0].Name = line;
                break;
            }
        }
    }

    private static double ClefCost(ClefKind a, ClefKind b)
    {
        if (a == ClefKind.Unknown || b == ClefKind.Unknown) return 0.6;
        return a == b ? 0 : 1;
    }

    /// <summary>
    /// Which reference staves form a part: a braced pair is one two-staff part when it is keyboard-like (any braced
    /// pair in a system of up to four staves; in larger, orchestral systems only treble over bass, since a brace there
    /// may also join e.g. cellos and basses). A score of two-staff systems only keeps the earlier rule: one piano part
    /// when the clefs look like a grand staff (or a brace is seen), or as forced by the option.
    /// </summary>
    private static List<int[]> PartLayout(List<StaffSystem> systems, StaffSystem reference, bool? forceGrand)
    {
        int n = reference.Staves.Count;
        var layout = new List<int[]>();
        if (n == 2 && systems.All(s => s.Staves.Count == 2))
        {
            bool braced = systems.Any(s => s.Staves[0].BraceToNext);
            bool grand = forceGrand ?? (LooksLikeGrandStaff(reference) || braced);
            if (grand) { layout.Add(new[] { 0, 1 }); return layout; }
        }
        if (forceGrand == false || n < 2) { for (int i = 0; i < n; i++) layout.Add(new[] { i }); return layout; }
        // a pair counts as braced when any system with the full staff count has the brace (a broken brace is missed)
        var full = systems.Where(s => s.Staves.Count == n).ToList();
        for (int i = 0; i < n; i++)
        {
            // the braced layout must be common: a single braced system in a part of single staves is a divisi
            // passage ("div." cellos on two staves), not a keyboard part
            int bracedSystems = i + 1 < n ? full.Count(s => s.Staves[i].BraceToNext) : 0;
            bool braced = bracedSystems > 0 && (full.Count * 3 >= systems.Count || bracedSystems >= 3);
            if (braced && KeyboardPair(reference, i)) { layout.Add(new[] { i, i + 1 }); i++; }
            else layout.Add(new[] { i });
        }
        return layout;
    }

    private static bool KeyboardPair(StaffSystem sys, int i)
    {
        if (sys.Staves.Count <= 4) return true;
        var a = sys.Staves[i].Clef; var b = sys.Staves[i + 1].Clef;
        return a is ClefKind.Treble or ClefKind.Unknown && b is ClefKind.Bass or ClefKind.Unknown && !(a == ClefKind.Unknown && b == ClefKind.Unknown);
    }

    private static bool LooksLikeGrandStaff(StaffSystem sys)
    {
        var a = sys.Staves[0].Clef; var b = sys.Staves[1].Clef;
        return (a is ClefKind.Treble or ClefKind.Unknown) && (b is ClefKind.Bass or ClefKind.Unknown) && !(a == ClefKind.Unknown && b == ClefKind.Unknown)
               || (a == ClefKind.Treble && b == ClefKind.Treble);
    }

    // ------------------------------------------------------------------ one staff-measure

    private static void BuildMeasure(ScoreMeasure sm, Measure m, Staff staff, int staffNo, StaffContext ctx, bool firstInSystem, bool firstOfPart, Staff? sibling = null, int siblingNo = 0, bool triplets = false)
    {
        double ss = staff.Space;
        sm.X0 = (int)m.X0; sm.X1 = (int)m.X1;
        var syms = m.Symbols.OrderBy(SymbolX).ToList();

        // attributes: clef / key / time changes seen in this measure (or inherited from the staff for the first measure of a system)
        var clefSym = syms.FirstOrDefault(s => s.Kind == SymbolKind.Clef && ClefOf(s) != ClefKind.Unknown);
        ClefKind clef = clefSym != null ? ClefOf(clefSym) : (firstInSystem && staff.Clef != ClefKind.Unknown ? staff.Clef : ctx.Clef);
        if (clef == ClefKind.Unknown && firstOfPart) { clef = staffNo == 2 ? ClefKind.Bass : ClefKind.Treble; sm.Warnings.Add($"staff {staffNo}: clef unknown, assuming {clef}"); }
        if (clef != ctx.Clef && clef != ClefKind.Unknown) { sm.Clefs[staffNo - 1] = ToClefSpec(clef); ctx.Clef = clef; }

        if (syms.Any(s => s.Kind == SymbolKind.KeySignature) || (firstInSystem && staff.KeyFifths != (ctx.Key ?? 0)))
        {
            int fifths = staff.KeyFifths;
            if (fifths != (ctx.Key ?? int.MinValue) && staffNo == 1) { sm.KeyFifths = fifths; }
            ctx.Key = fifths;
        }
        else if (firstOfPart && staffNo == 1) { sm.KeyFifths = staff.KeyFifths; ctx.Key = staff.KeyFifths; }

        var time = ParseTime(staff.TimeSignature);
        // a signature printed on this staff is recorded for it even when unchanged (the other staff may change alone)
        if (syms.Any(s => s.Kind == SymbolKind.TimeSignature) && time != null) sm.StaffTimes[staffNo - 1] = time;
        if (syms.Any(s => s.Kind == SymbolKind.TimeSignature) && time != null && (ctx.Time == null || !ctx.Time.Equals(time)))
        {
            if (staffNo == 1) sm.Time = time;
            sm.StaffTimes[staffNo - 1] = time;
            ctx.Time = time;
        }
        else if (ctx.Time == null && time != null) { if (staffNo == 1) sm.Time = time; sm.StaffTimes[staffNo - 1] = time; ctx.Time = time; }
        else if (firstOfPart && ctx.Time == null && staffNo == 1) sm.Warnings.Add("time signature unknown");

        // events
        var events = new List<ScoreEvent>();
        foreach (var s in syms)
        {
            switch (s.Kind)
            {
                case SymbolKind.Note:
                case SymbolKind.Chord:
                {
                    var ev = MakeNoteEvent(s, staff, staffNo, clef, ctx.Key ?? 0, sm);
                    if (sibling != null && s.Notes.Count > 0 && s.Notes.All(n => n.CrossStaff == sibling)) ev.NotatedStaff = siblingNo;
                    events.Add(ev);
                }
                    break;
                case SymbolKind.NoteGroup:
                {
                    var ordered = s.Children.OrderBy(c => c.Notes.Min(n => n.HeadX)).ToList();
                    var evs = MakeGroupEvents(s, staff, staffNo, clef, ctx.Key ?? 0, sm).ToList();
                    if (sibling != null)
                        foreach (var (e, c) in evs.Zip(ordered)) if (c.Notes.Count > 0 && c.Notes.All(n => n.CrossStaff == sibling)) e.NotatedStaff = siblingNo;
                    events.AddRange(evs);
                }
                    break;
                case SymbolKind.Rest:
                    events.Add(MakeRest(s, staffNo));
                    break;
            }
        }
        // fermatas: attach to the nearest event in x
        foreach (var f in syms.Where(s => s.Kind == SymbolKind.Fermata))
        {
            var ev = Nearest(events, f.Bounds.CenterX);
            if (ev != null) ev.Fermata = true;
        }
        // articulations
        foreach (var a in syms.Where(s => s.Kind == SymbolKind.Articulation))
        {
            var ev = Nearest(events, a.Bounds.CenterX, 1.0 * ss);
            if (ev == null || ev.IsRest) continue;
            string? art = a.Name switch
            {
                "articAccentAbove" => "accent", "articTenutoAbove" => "tenuto", "articMarcatoAbove" => "strong-accent",
                "articStaccatissimoAbove" => "staccatissimo", "stringsDownBow" => "down-bow", "stringsUpBow" => "up-bow", _ => null
            };
            if (art != null && !ev.Articulations.Contains(art)) ev.Articulations.Add(art);
        }

        AssignVoices(events, staff, staffNo, ctx.Time?.Length);
        events = events.OrderBy(e => e.Voice).ThenBy(e => e.X).ToList();
        sm.Events.AddRange(events);

        // directions
        foreach (var d in syms.Where(s => s.Kind == SymbolKind.Dynamic && IsDynamicMark(s.Attributes.GetValueOrDefault("text", ""))))
            sm.Directions.Add(new Direction { Kind = DirectionKind.Dynamics, Text = d.Attributes.GetValueOrDefault("text", d.Name), Staff = staffNo, X = d.Bounds.X0, Above = d.Bounds.CenterY < staff.TopY(d.Bounds.CenterX) });
        foreach (var h in syms.Where(s => s.Kind == SymbolKind.Hairpin))
        {
            bool cresc = h.Name.StartsWith("crescendo");
            sm.Directions.Add(new Direction { Kind = DirectionKind.WedgeStart, Crescendo = cresc, Staff = staffNo, X = h.Bounds.X0 });
            sm.Directions.Add(new Direction { Kind = DirectionKind.WedgeStop, Crescendo = cresc, Staff = staffNo, X = h.Bounds.X1 });
        }
        // slurs / ties within this measure (symbols are assigned to the measure of their centre; endpoints may lie in neighbours)
        foreach (var sl in syms.Where(s => s.Kind == SymbolKind.Slur))
        {
            var left = NearestNote(events, sl.Bounds.X0, 1.5 * ss);
            var right = NearestNote(events, sl.Bounds.X1, 1.5 * ss);
            if (left == null || right == null || left == right) { sm.Directions.Add(new Direction { Kind = DirectionKind.Words, Text = "", X = sl.Bounds.X0 }); continue; }
            bool tie = left.Notes.Count == 1 && right.Notes.Count == 1 && left.Notes[0].Pitch == right.Notes[0].Pitch
                       && !events.Any(e => !e.IsRest && e.Voice == left.Voice && e.X > left.X && e.X < right.X);
            if (tie) { left.Notes[0].TieStart = true; right.Notes[0].TieStop = true; }
            else
            {
                int n = (ctx.SlurCounter++ % 6) + 1;
                left.SlurStarts.Add(n); right.SlurStops.Add(n);
            }
        }
        sm.Directions.RemoveAll(d => d.Kind == DirectionKind.Words && d.Text == "");

        // barline styles (from the staff that defines the part's first staff)
        if (staffNo == 1)
        {
            if (m.LeftBarline != null) ApplyBarline(sm, m.LeftBarline.Kind, true);
            if (m.RightBarline != null) ApplyBarline(sm, m.RightBarline.Kind, false);
            else if (m.X1 >= staff.Right - 2 * ss) sm.Warnings.Add("no barline at the end of the measure (staff end)");
        }

        // validation against the time signature
        if (ctx.Time != null) Validate(sm, ctx.Time, staffNo);
        else if (events.Count == 1 && events[0].IsRest && events[0].Type == "whole") events[0].MeasureRest = true;

        // a compound staff against simple time (9/8 over 3/4): checked against its own signature above, then its
        // notes become triplets so that both staves span the same measure (3 written eighths = 2 real ones)
        if (triplets && ctx.Time is { Compound: true })
            foreach (var e in events.Where(e => sm.Events.Contains(e)))
            {
                e.Duration = e.Duration * 2 / 3;
                if (e.TupletActual == 0) { e.TupletActual = 3; e.TupletNormal = 2; }
                else { e.TupletActual *= 3; e.TupletNormal *= 2; }
            }
    }

    /// <summary>Checks every voice of a staff against the measure length; drops uncertain events when that makes the measure add up.</summary>
    private static void Validate(ScoreMeasure sm, TimeSpec time, int staffNo)
    {
        int expected = time.Length;
        if (staffNo == 1 || sm.Fit.Expected == null) sm.Fit.Expected = expected;
        sm.Fit.ExpectedPerStaff[Math.Clamp(staffNo - 1, 0, 1)] = expected;
        foreach (var voice in sm.Events.Where(e => e.Staff == staffNo).GroupBy(e => e.Voice).ToList())
        {
            var evs = voice.ToList();
            int sum = evs.Sum(e => e.Duration);
            if (evs.Count == 1 && evs[0].IsRest && evs[0].Type == "whole") { evs[0].Duration = expected; evs[0].MeasureRest = true; sm.Fit.Sums.Add((staffNo, voice.Key, expected)); continue; }
            sm.Fit.Sums.Add((staffNo, voice.Key, sum));
            if (sum == expected) continue;
            if (sum > expected)
            {
                var uncertain = evs.Where(e => e.Uncertain).ToList();
                ScoreEvent? drop1 = uncertain.FirstOrDefault(u => sum - u.Duration == expected);
                if (drop1 != null)
                {
                    sm.Events.Remove(drop1); sm.Fit.DroppedPerStaff[Math.Clamp(staffNo - 1, 0, 1)]++;
                    sm.Warnings.Add($"staff {staffNo} voice {voice.Key}: dropped uncertain {drop1} at x={drop1.X} to fit {time}");
                    continue;
                }
                var pair = uncertain.SelectMany((a, i) => uncertain.Skip(i + 1).Select(b => (a, b))).FirstOrDefault(p => sum - p.a.Duration - p.b.Duration == expected);
                if (pair.a != null)
                {
                    sm.Events.Remove(pair.a); sm.Events.Remove(pair.b); sm.Fit.DroppedPerStaff[Math.Clamp(staffNo - 1, 0, 1)] += 2;
                    sm.Warnings.Add($"staff {staffNo} voice {voice.Key}: dropped uncertain {pair.a} and {pair.b} to fit {time}");
                    continue;
                }
            }
            sm.Warnings.Add($"staff {staffNo} voice {voice.Key}: duration {Fraction(sum)} instead of {Fraction(expected)} ({time})");
        }
    }

    private static void ApplyBarline(ScoreMeasure sm, BarlineKind kind, bool left)
    {
        switch (kind)
        {
            case BarlineKind.Double: if (!left) sm.RightBar = BarStyle.LightLight; break;
            case BarlineKind.Final: if (!left) sm.RightBar = BarStyle.LightHeavy; break;
            case BarlineKind.Thick: if (!left) sm.RightBar = BarStyle.Heavy; break;
            case BarlineKind.RepeatStart: if (left) { sm.LeftBar = BarStyle.HeavyLight; sm.RepeatForward = true; } break;
            case BarlineKind.RepeatEnd: if (!left) { sm.RightBar = BarStyle.LightHeavy; sm.RepeatBackward = true; } break;
            case BarlineKind.RepeatBoth:
                if (left) { sm.LeftBar = BarStyle.HeavyLight; sm.RepeatForward = true; } else { sm.RightBar = BarStyle.LightHeavy; sm.RepeatBackward = true; }
                break;
        }
    }

    private static readonly HashSet<string> DynamicMarks = new() { "p", "pp", "ppp", "pppp", "f", "ff", "fff", "ffff", "mp", "mf", "sf", "sfp", "sfz", "fp", "rf", "rfz", "fz", "sffz" };
    private static bool IsDynamicMark(string t) => DynamicMarks.Contains(t);

    private static string Fraction(int divisions) => $"{divisions / (double)Score.Divisions:0.##} quarters";

    private static double SymbolX(MusicSymbol s) => s.Kind is SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup && s.Notes.Count > 0 ? s.Notes.Min(n => n.HeadX) : s.Bounds.CenterX;

    private static ScoreEvent? Nearest(List<ScoreEvent> events, double x, double maxDist = double.MaxValue)
    {
        ScoreEvent? best = null; double bd = maxDist;
        foreach (var e in events) { double d = Math.Abs(e.X - x); if (d < bd) { bd = d; best = e; } }
        return best;
    }

    private static ScoreEvent? NearestNote(List<ScoreEvent> events, double x, double maxDist)
    {
        ScoreEvent? best = null; double bd = maxDist;
        foreach (var e in events.Where(e => !e.IsRest)) { double d = Math.Abs(e.X - x); if (d < bd) { bd = d; best = e; } }
        return best;
    }

    // ------------------------------------------------------------------ events

    private static ScoreEvent MakeNoteEvent(MusicSymbol s, Staff staff, int staffNo, ClefKind clef, int fifths, ScoreMeasure sm)
    {
        var ev = new ScoreEvent { Staff = staffNo, StemUp = s.StemUp, X = (int)Math.Round(s.Notes.Count > 0 ? s.Notes.Min(n => n.HeadX) : s.Bounds.CenterX), Y = (int)s.Bounds.CenterY, Confidence = s.Confidence };
        SetType(ev, s.Duration, s.Dots > 0 ? s.Dots : s.Notes.Max(n => n.Dots), sm);
        foreach (var n in s.Notes.OrderBy(n => n.Step)) ev.Notes.Add(MakeNote(n, clef, fifths));
        // stemless heads (whole notes, but also text glyphs taken for heads) and heads far outside the staff are suspects
        // user-corrected or user-added symbols are never dropped to make a measure fit
        bool asserted = s.Attributes.ContainsKey("corrected") || s.Attributes.ContainsKey("added");
        if (!asserted && (s.StemUp == null || s.Notes.Any(n => n.Step > 18 || n.Step < -10))) ev.Uncertain = true;
        if (s.Attributes.TryGetValue("articulation", out var art) && art == "staccato") ev.Articulations.Add("staccato");
        return ev;
    }

    private static int _groupCounter;

    private static IEnumerable<ScoreEvent> MakeGroupEvents(MusicSymbol group, Staff staff, int staffNo, ClefKind clef, int fifths, ScoreMeasure sm)
    {
        var children = group.Children.OrderBy(c => c.Notes.Min(n => n.HeadX)).ToList();
        var evs = children.Select(c => MakeNoteEvent(c, staff, staffNo, clef, fifths, sm)).ToList();
        int groupId = ++_groupCounter;
        foreach (var e in evs) e.GroupId = groupId;
        if (group.Attributes.TryGetValue("tuplet", out var tupletText))
        {
            // "3@1409;3@1480": each digit applies to the run of `actual` notes centred under it
            var used = new bool[evs.Count];
            foreach (var spec in tupletText.Split(';'))
            {
                var parts = spec.Split('@');
                if (!int.TryParse(parts[0], out int actual) || actual < 2) continue;
                double tx = parts.Length > 1 && double.TryParse(parts[1], out double txv) ? txv : group.Bounds.CenterX;
                int normal = actual == 3 ? 2 : actual is 5 or 6 or 7 ? 4 : actual == 2 ? 3 : 2;
                if (evs.Count < actual) { sm.Warnings.Add($"tuplet {actual} over a group of {evs.Count} notes at x={group.Bounds.X0}: ignored"); continue; }
                int best = -1; double bestD = double.MaxValue;
                for (int i = 0; i + actual <= evs.Count; i++)
                {
                    if (Enumerable.Range(i, actual).Any(k => used[k])) continue;
                    double mean = Enumerable.Range(i, actual).Average(k => (double)evs[k].X);
                    double d = Math.Abs(mean - tx);
                    if (d < bestD) { bestD = d; best = i; }
                }
                if (best < 0) continue;
                for (int k = 0; k < actual; k++)
                {
                    var e = evs[best + k];
                    e.Duration = e.Duration * normal / actual;
                    e.TupletActual = actual; e.TupletNormal = normal;
                    e.TupletStart = k == 0; e.TupletStop = k == actual - 1;
                    used[best + k] = true;
                }
            }
        }
        if (group.Name.StartsWith("beamed"))
        {
            int maxLevels = children.Max(c => c.Flags);
            for (int i = 0; i < evs.Count; i++)
            {
                int levels = children[i].Flags;
                var beams = new BeamState[levels];
                for (int k = 0; k < levels; k++)
                {
                    bool prev = i > 0 && children[i - 1].Flags > k;
                    bool next = i + 1 < evs.Count && children[i + 1].Flags > k;
                    beams[k] = prev && next ? BeamState.Continue : prev ? BeamState.End : next ? BeamState.Begin : (i > 0 ? BeamState.BackwardHook : BeamState.ForwardHook);
                }
                evs[i].Beams = beams;
            }
            // a note inside a beamed group without beams is most likely a beam-count miss: give it one beam, unless its
            // stem points the other way than the beamed notes (a second voice sharing the group's ink)
            var flaggedChildren = children.Where(c => c.Flags > 0).ToList();
            bool? beamedUp = flaggedChildren.Count == 0 ? null : flaggedChildren.Count(c => c.StemUp == true) * 2 >= flaggedChildren.Count;
            foreach (var (e, c) in evs.Zip(children))
            {
                if (c.Flags != 0 || e.Type != "quarter") continue;
                if (beamedUp != null && c.StemUp != null && c.StemUp != beamedUp) { e.GroupId = 0; continue; }
                e.Type = "eighth"; e.Duration = DurationOf("eighth", e.Dots); sm.Warnings.Add($"beamed note at x={e.X} had no beam count; assuming 8th");
            }
        }
        return evs;
    }

    private static ScoreEvent MakeRest(MusicSymbol s, int staffNo)
    {
        var ev = new ScoreEvent { IsRest = true, Staff = staffNo, X = (int)s.Bounds.CenterX, Y = (int)s.Bounds.CenterY, Confidence = s.Confidence };
        SetType(ev, s.Duration, s.Dots, null);
        return ev;
    }

    private static void SetType(ScoreEvent ev, string? duration, int dots, ScoreMeasure? sm)
    {
        string d = (duration ?? "quarter").TrimEnd('?');
        string type = d switch
        {
            "breve" => "breve", "whole" => "whole", "half" => "half", "quarter" => "quarter", "8th" => "eighth", "16th" => "16th",
            "32nd" => "32nd", "64th" => "64th", _ => "quarter"
        };
        if (type == "quarter" && d != "quarter") sm?.Warnings.Add($"unknown duration '{duration}' at x={ev.X}; assuming quarter");
        if (duration == null || duration.EndsWith("?")) ev.Uncertain = true;
        ev.Type = type; ev.Dots = Math.Min(dots, 2);
        ev.Duration = DurationOf(type, ev.Dots);
    }

    public static int DurationOf(string type, int dots)
    {
        int d = type switch { "breve" => 8 * Score.Divisions, "whole" => 4 * Score.Divisions, "half" => 2 * Score.Divisions, "quarter" => Score.Divisions, "eighth" => Score.Divisions / 2, "16th" => Score.Divisions / 4, "32nd" => Score.Divisions / 8, "64th" => Score.Divisions / 16, _ => Score.Divisions };
        int total = d; int add = d;
        for (int i = 0; i < dots; i++) { add /= 2; total += add; }
        return total;
    }

    private static ScoreNote MakeNote(Note n, ClefKind clef, int fifths)
    {
        var note = new ScoreNote { SourceX = (int)n.HeadX, SourceY = (int)n.HeadY };
        var p = ParsePitch(n.Pitch);
        if (p == null)
        {
            // fall back to the step with an assumed clef
            int baseIdx = BottomLineIndex(clef == ClefKind.Unknown ? ClefKind.Treble : clef);
            int idx = Math.Clamp(baseIdx + n.Step, 0, 69);
            p = new PitchSpec(Letters[idx % 7], 0, idx / 7);
        }
        note.Pitch = p;
        note.AccidentalName = n.AccidentalName switch
        {
            "accidentalSharp" => "sharp", "accidentalFlat" => "flat", "accidentalNatural" => "natural", "accidentalDoubleSharp" => "double-sharp", "accidentalDoubleFlat" => "flat-flat", _ => null
        };
        return note;
    }

    public static PitchSpec? ParsePitch(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.StartsWith("step")) return null;
        string step = s[..1];
        if (Array.IndexOf(Letters, step) < 0) return null;
        int i = 1, alter = 0;
        while (i < s.Length && !char.IsDigit(s[i]) && s[i] != '-')
        {
            alter += s[i] switch { '#' => 1, 'b' => -1, 'x' => 2, _ => 0 };
            i++;
        }
        if (!int.TryParse(s[i..], out int octave)) return null;
        return new PitchSpec(step, alter, octave);
    }

    private static int BottomLineIndex(ClefKind clef) => clef switch
    {
        ClefKind.Treble => 4 * 7 + 2, ClefKind.Treble8vb => 3 * 7 + 2, ClefKind.Bass => 2 * 7 + 4, ClefKind.Bass8vb => 1 * 7 + 4,
        ClefKind.Alto => 3 * 7 + 3, ClefKind.Tenor => 3 * 7 + 1, _ => 4 * 7 + 2,
    };

    private static ClefKind ClefOf(MusicSymbol s) => s.Attributes.TryGetValue("clef", out var v) && Enum.TryParse<ClefKind>(v, out var k) ? k : ClefKind.Unknown;

    private static ClefSpec ToClefSpec(ClefKind k) => k switch
    {
        ClefKind.Bass => ClefSpec.Bass, ClefKind.Alto => ClefSpec.Alto, ClefKind.Tenor => ClefSpec.Tenor, ClefKind.Percussion => ClefSpec.Percussion,
        ClefKind.Treble8vb => ClefSpec.Treble8vb, ClefKind.Bass8vb => ClefSpec.Bass8vb, _ => ClefSpec.Treble,
    };

    public static TimeSpec? ParseTime(string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) return null;
        if (t.StartsWith("cut")) return new TimeSpec(2, 2, "cut");
        if (t.StartsWith("C")) return new TimeSpec(4, 4, "common");
        var parts = t.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int beats) || beats <= 0 || beats > 32) return null;
        if (!int.TryParse(parts[1], out int beatType) || beatType <= 0) beatType = 4;
        if (beatType == 1) beatType = 4;   // a "1" under the beats is almost always a misread "4" (x/1 metres are very rare)
        if (beatType is not (2 or 4 or 8 or 16 or 32)) beatType = 4;
        return new TimeSpec(beats, beatType);
    }

    // ------------------------------------------------------------------ voices

    private static void AssignVoices(List<ScoreEvent> events, Staff staff, int staffNo, int? expected = null)
    {
        var notes = events.Where(e => !e.IsRest && e.StemUp.HasValue).ToList();
        bool twoVoices = false;
        double ss = staff.Space;
        // cross-staff members of a beamed group have their stems on the other side of the beam: for the voice they
        // follow the direction of the group's members on this staff
        var dir = events.ToDictionary(e => e, e => e.StemUp);
        foreach (var g in events.Where(e => e.GroupId > 0).GroupBy(e => e.GroupId))
        {
            var home = g.Where(e => e.Beams.Length > 0 && e.NotatedStaff == null).ToList();
            var away = g.Where(e => e.Beams.Length > 0 && e.NotatedStaff != null).ToList();
            if (home.Count == 0 || away.Count == 0) continue;
            bool up = home.Count(e => e.StemUp == true) * 2 >= home.Count;
            foreach (var e in away) dir[e] = up;
        }
        int ups = notes.Count(n => dir[n] == true), downs = notes.Count(n => dir[n] == false);
        // an up-stem and a down-stem note at the same x are two voices (also a unison with stems both ways), adjacent ones are not
        if (ups >= 1 && downs >= 1)
            foreach (var a in notes.Where(n => dir[n] == true))
                if (notes.Any(b => dir[b] == false && Math.Abs(b.X - a.X) < 0.6 * ss)) { twoVoices = true; break; }
        int baseVoice = (staffNo - 1) * 2 + 1;
        // the default split: by stem direction, rests by their height
        bool DefaultSecond(ScoreEvent e) => e.IsRest ? e.Y > staff.CenterY : dir[e] == false;
        var second = events.ToDictionary(e => e, e => twoVoices && DefaultSecond(e));

        // when the measure length is known, let it arbitrate: the split (or no split) whose voices fill the measure wins
        if (expected is int L && events.Count > 0)
        {
            int total = events.Sum(e => e.Duration);
            // what the validation step can still repair: an overflowing voice whose surplus is exactly one or two
            // uncertain notes counts as fitting
            int Repaired(List<ScoreEvent> voice)
            {
                int sum = voice.Sum(e => e.Duration);
                if (sum <= L) return sum;
                var unc = voice.Where(e => e.Uncertain).ToList();
                if (unc.Any(u => sum - u.Duration == L)) return L;
                if (unc.SelectMany((a, i) => unc.Skip(i + 1).Select(b => a.Duration + b.Duration)).Any(d => sum - d == L)) return L;
                return sum;
            }
            double Cost(Func<ScoreEvent, bool> isSecond)
            {
                var va = events.Where(e => !isSecond(e)).ToList(); var vb = events.Where(isSecond).ToList();
                int a = Repaired(va), b = Repaired(vb);
                // a repair (dropping notes) costs a little: of two splits that both add up, the one keeping every note wins
                double repairs = (a != va.Sum(e => e.Duration) ? 0.5 : 0) + (b != vb.Sum(e => e.Duration) ? 0.5 : 0);
                if (b == 0) return Math.Abs(a - L) + repairs;
                // the second voice may stop early (a held bass note under a moving line); it must not overflow
                return Math.Abs(a - L) + (b > L ? (b - L) : 0.25 * (L - b)) + repairs;
            }
            var candidates = new List<(string name, Func<ScoreEvent, bool> isSecond)>
            {
                ("single", _ => false),
                ("direction", DefaultSecond),
                ("direction, rests up", e => !e.IsRest && dir[e] == false),
                ("direction, rests down", e => e.IsRest || dir[e] == false),
                ("direction flipped", e => e.IsRest ? e.Y <= staff.CenterY : dir[e] == true),
                ("long notes", e => !e.IsRest && (e.Type is "whole" or "half" || e.Dots > 0) && e.Beams.Length == 0),
            };
            // only consider splits that use both stem directions or separate long from short notes; a measure that adds
            // up as one voice stays one voice
            bool bothDirections = notes.Any(n => dir[n] == true) && notes.Any(n => dir[n] == false);
            double currentCost = Cost(e => second[e]);
            (string name, Func<ScoreEvent, bool> isSecond, double cost) best = ("current", e => second[e], currentCost);
            if (total == L) best = ("single", _ => false, 0);
            else
                foreach (var (name, isSecond) in candidates)
                {
                    if (name != "single" && name != "long notes" && !bothDirections) continue;
                    // a second voice made only of uncertain notes is a stray head hiding from the duration check
                    // (exception: held long notes struck together with a note of the moving voice, such as a drone
                    // double stop; stemless, so uncertain, but a glyph that lines up with a head is no stray mark)
                    if (name != "single" && !events.Any(e => isSecond(e) && !e.Uncertain && !e.IsRest)
                        && !(name == "long notes" && events.Where(isSecond).All(e => !e.IsRest && e.Notes.Count > 0
                                && e.Y > staff.TopY(e.X) - 2 * ss && e.Y < staff.BottomY(e.X) + 2 * ss
                                && events.Any(o => !isSecond(o) && !o.IsRest && Math.Abs(o.X - e.X) < 0.8 * ss)))) continue;
                    double c = Cost(isSecond);
                    // an alternative split must make the measure add up exactly; a direction-based one may also merely
                    // improve a measure that really has stems both ways (a single-voice measure with a wrong duration
                    // must not be "fixed" by inventing a second voice)
                    bool directionBased = name.StartsWith("direction") && bothDirections;
                    if (c < best.cost - 1e-9 && (c < 1e-9 || directionBased)) best = (name, isSecond, c);
                }
            if (Environment.GetEnvironmentVariable("PVOMR_VOICEDUMP") == "1")
                Console.Error.WriteLine($"voices L={L} total={total} pick={best.name} cost={best.cost:0.##} events=" + string.Join(" ", events.Select(e => $"{e.Type}{(e.Uncertain ? "?" : "")}@{e.X}{(e.StemUp == null ? "" : e.StemUp == true ? "u" : "d")}")));
            foreach (var e in events) second[e] = best.isSecond(e);
            twoVoices = events.Any(e => second[e]);
        }
        foreach (var e in events) e.Voice = second[e] ? baseVoice + 1 : baseVoice;
    }

    // ------------------------------------------------------------------ finishing

    private static void FinishPart(Part part, Score score)
    {
        // no time signature recognised anywhere: infer it from the most common measure length
        if (part.Measures.Count >= 3 && !part.Measures.Any(m => m.Time != null))
        {
            // measures holding only a whole rest take any length and say nothing about the time signature
            var sums = part.Measures.Where(m => !(m.Events.Count == 1 && m.Events[0].IsRest && m.Events[0].Type == "whole"))
                                    .Select(m => m.Events.Where(e => e.Voice == 1).Sum(e => e.Duration)).Where(x => x > 0).ToList();
            if (sums.Count >= 3)
            {
                int mode = sums.GroupBy(x => x).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
                int share = sums.Count(x => x == mode);
                if (share >= Math.Max(2, sums.Count / 3) && mode % (Score.Divisions / 2) == 0)
                {
                    int eighths = mode / (Score.Divisions / 2);
                    TimeSpec t = eighths % 2 == 0 ? new TimeSpec(eighths / 2, 4) : new TimeSpec(eighths, 8);
                    part.Measures[0].Time = t;
                    part.Measures[0].Warnings.RemoveAll(w => w.Contains("time signature unknown"));
                    score.Warnings.Add($"{part.Id}: no time signature recognised; inferred {t} from {share} of {sums.Count} measures");
                    foreach (var m in part.Measures)
                        for (int st = 1; st <= part.StaffCount; st++) Validate(m, t, st);
                }
            }
        }
        // sanity of the time signature in force: a signature almost no measure fits, while most measures agree on another
        // plausible length, was misread (a "5/4" over 3/8 music, a "1/4" carried along a whole part) and is replaced
        {
            // runs of measures under one time signature (or under none); a page break ends a run so that a signature
            // misread on one page does not drag the next page along
            var runs = new List<List<ScoreMeasure>>();
            foreach (var m in part.Measures)
            {
                if (runs.Count > 0 && runs[^1][0].Fit.Expected == m.Fit.Expected && runs[^1][0].Page == m.Page && (m.Time == null || m.Time.Length == m.Fit.Expected)) runs[^1].Add(m);
                else runs.Add(new List<ScoreMeasure> { m });
            }
            foreach (var run in runs)
            {
                if (run.Count < 4) continue;
                // staves with their own metres (9/8 over 3/4) were each checked against their own signature
                if (run.Any(m => m.StaffTimes[0] is TimeSpec a && m.StaffTimes[1] is TimeSpec b && (a.TripletsAgainst(b) || b.TripletsAgainst(a)))) continue;
                int? expected = run[0].Fit.Expected;
                // a signature read from the C or cut-C glyph is reliable; only digit readings and carried values are questioned
                var inForceMeasure = part.Measures.LastOrDefault(m => m.Number <= run[0].Number && m.Time != null);
                if (expected != null && inForceMeasure?.Time?.Symbol != null && inForceMeasure.Time.Length == expected && inForceMeasure.Page == run[0].Page) continue;
                var informative = run.Where(m => !(m.Events.Count == 1 && m.Events[0].IsRest && m.Events[0].Type == "whole") && m.Events.Count > 0).ToList();
                if (informative.Count < 4) continue;
                // one sample per staff (the fullest voice): the two staves of a piano measure are judged separately
                var sums = informative.SelectMany(m => m.Events.GroupBy(e => e.Staff).Select(g => g.GroupBy(e => e.Voice).Select(v => v.Sum(e => e.Duration)).Max()))
                                      .Where(x => x > 0).ToList();
                int fitting = expected == null ? 0 : sums.Count(x => x == expected);
                if (expected != null && fitting * 5 >= sums.Count) continue;   // at least a fifth fits: the signature stands
                if (sums.Count < 4) continue;
                int mode = sums.GroupBy(x => x).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
                int share = sums.Count(x => x == mode);
                int eighth = Score.Divisions / 2;
                // the agreeing measures must be at least 30% (40% when no signature was read at all) and twice the fitting ones
                int needed = expected == null ? (sums.Count * 2 + 4) / 5 : (sums.Count * 3 + 9) / 10;
                if (share < needed || share < 2 * fitting || mode == expected || mode % eighth != 0 || mode < 2 * eighth || mode > 24 * eighth) continue;
                int eighths = mode / eighth;
                TimeSpec t = eighths % 2 == 0 ? new TimeSpec(eighths / 2, 4) : new TimeSpec(eighths, 8);
                var first = run[0];
                score.Warnings.Add(expected == null
                    ? $"{part.Id} m{first.Number} (page {first.Page}): no time signature; {t} inferred from {share} of {sums.Count} measures"
                    : $"{part.Id} m{first.Number} (page {first.Page}): time signature {Fraction(expected.Value)} fits {fitting} of {informative.Count} measures; replaced by {t}, which {share} agree on");
                first.Time = t;
                foreach (var m in run)
                {
                    if (m != first && m.Time != null) m.Time = null;
                    m.Warnings.RemoveAll(w => w.Contains("duration") || w.Contains("dropped uncertain"));
                    m.Fit.Sums.Clear(); m.Fit.DroppedPerStaff[0] = m.Fit.DroppedPerStaff[1] = 0;
                    for (int st = 1; st <= part.StaffCount; st++) Validate(m, t, st);
                }
            }
        }
        // pickup measure
        if (part.Measures.Count > 1)
        {
            var first = part.Measures[0];
            var time = first.Time;
            if (time != null)
            {
                var sums = first.Events.GroupBy(e => e.Voice).Select(g => g.Sum(e => e.Duration)).ToList();
                int sum = sums.Count > 0 ? sums.Max() : 0;
                bool consistent = sums.Count > 0 && sums.All(x => x == sum);
                if (consistent && sum > 0 && sum < time.Length && sum % (Score.Divisions / 4) == 0 && first.Warnings.Any(w => w.Contains("duration")))
                {
                    first.Implicit = true;
                    first.Warnings.RemoveAll(w => w.Contains("duration"));
                    foreach (var m in part.Measures) m.Number--;   // pickup is measure 0
                    first.Number = 0;
                }
            }
        }
        // barline styles from the detected barline kinds are set by the caller via measure boundaries; defaults: regular
        foreach (var m in part.Measures)
            foreach (var w in m.Warnings) score.Warnings.Add($"{part.Id} m{m.Number} (page {m.Page}): {w}");
    }
}
