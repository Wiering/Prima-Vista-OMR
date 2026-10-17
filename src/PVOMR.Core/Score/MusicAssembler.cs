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
        /// <summary>Measures before the first time signature of a part take that signature (checking pages out of context).</summary>
        public bool AssumeFirstTime { get; set; }
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
            bool sliver = measureCount > 1 && sys.Staves.All(st => st.Measures.Count > 1 && !st.Measures[0].Symbols.Any(IsMusic) && st.Measures[0].MultiRest == 0);
            int firstMi = sliver ? 1 : 0;
            // the bar number printed over the system: the first one sets the numbering (an export that starts in the
            // middle of a piece), the others are checked against the count
            // a movement starts at bar 1: an unprinted number the next printed ones are checked against
            if (sys.Staves[0].MovementStart && sys.Staves[0].BarNumber == null)
                score.BarNumbers.Add(new BarNumberCheck(pg.PageNumber, sys.Staves[0].Index, 1, partMeasureCount[0] + 1));
            if (sys.Staves[0].BarNumber is int printed)
            {
                if (partMeasureCount.All(c => c == 0) && score.BarNumbers.Count == 0)
                    for (int i = 0; i < partCount; i++) partMeasureCount[i] = printed - 1;
                score.BarNumbers.Add(new BarNumberCheck(pg.PageNumber, sys.Staves[0].Index, printed, partMeasureCount[0] + 1));
            }
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
                    // a multi-measure rest: that many whole-measure rests, the first one marked
                    int s0 = staffAt[pi, 0];
                    if (part.StaffCount == 1 && s0 >= 0 && mi < sys.Staves[s0].Measures.Count && sys.Staves[s0].Measures[mi].MultiRest is int mr and >= 2)
                    {
                        var ctx = contexts[pi * 2];
                        ScoreEvent Rest() => new() { IsRest = true, MeasureRest = true, Type = "whole", Duration = ctx.Time?.Length ?? 4 * Score.Divisions, Voice = 1, Staff = 1 };
                        sm.Events.Clear(); sm.Events.Add(Rest()); sm.MultiRest = mr;
                        sm.Fit.Sums.Clear(); sm.Warnings.RemoveAll(w => w.Contains("duration"));
                        if (ctx.Time != null) Validate(sm, ctx.Time, 1);
                        for (int k = 1; k < mr; k++)
                        {
                            var extra = new ScoreMeasure { Page = pg.PageNumber };
                            extra.Events.Add(Rest());
                            if (ctx.Time != null) Validate(extra, ctx.Time, 1);
                            partMeasureCount[pi]++;
                            extra.Number = partMeasureCount[pi];
                            part.Measures.Add(extra);
                        }
                    }
                }
            }
        }
        NameParts(score, namesOf, pages);
        foreach (var part in score.Parts) FinishPart(part, score);
        // printed bar numbers against the count: the measures between two printed numbers must match their difference
        // (a single misread number then marks the two neighbouring checks, a missed or extra bar line the rest)
        // a number out of line with both neighbours is a misreading and is left out; a number below the previous
        // one starts a new movement (numbering restarts), which is no disagreement
        // (counted by the measures' numbers once the part is finished: an upbeat or the two halves of a measure split
        // by a repeat sign count as one)
        var nums = score.BarNumbers;
        var firstPart = score.Parts.Count > 0 ? score.Parts[0].Measures : new List<ScoreMeasure>();
        int N(BarNumberCheck c) => c.Counted >= 1 && c.Counted <= firstPart.Count ? firstPart[c.Counted - 1].Number : c.Counted;
        var trusted = nums.Where((b, i) => !(i > 0 && i + 1 < nums.Count && nums[i - 1].Printed < nums[i + 1].Printed
                                              && !(nums[i - 1].Printed < b.Printed && b.Printed < nums[i + 1].Printed))).ToList();
        for (int i = 1; i < trusted.Count; i++)
        {
            var a = trusted[i - 1]; var b = trusted[i];
            if (b.Printed <= a.Printed) continue;
            int counted = N(b) - N(a);
            b.Agrees = b.Printed - a.Printed == counted;
            b.Since = a.Printed; b.PrintedSpan = b.Printed - a.Printed; b.CountedSpan = counted;
            if (b.Agrees == false)
                score.Warnings.Add($"bar numbers: {b.Printed - a.Printed} measures printed between bar {a.Printed} (page {a.Page}, staff {a.Staff + 1}) and bar {b.Printed} (page {b.Page}, staff {b.Staff + 1}), {counted} counted");
        }
        if (options.AssumeFirstTime)
        {
            // measures still without a time signature (before the first one found) are checked against the part's first
            // signature, or the first of any part
            var anyFirst = score.Parts.Select(p => p.Measures.FirstOrDefault(m => m.Time != null)?.Time).FirstOrDefault(t => t != null);
            foreach (var part in score.Parts)
            {
                var t = part.Measures.FirstOrDefault(m => m.Time != null)?.Time ?? anyFirst;
                if (t == null) continue;
                foreach (var m in part.Measures)
                    for (int st = 1; st <= part.StaffCount; st++)
                        if (m.Fit.ForStaff(st).Expected == null) Validate(m, t, st);
            }
        }
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
        // transposing instruments: the key in the printed name, else the usual one (all readings: the abbreviation may
        // carry the key when the full name was misread)
        for (int i = 0; i < score.Parts.Count; i++)
        {
            var part = score.Parts[i];
            if (part.Instrument == null) continue;
            var withKey = namesOf[i].Where(n => Instruments.Match(n)?.Id == part.Instrument.Id).Prepend(part.Name)
                                    .Select(n => Instruments.TranspositionOf(part.Instrument, n)).ToList();
            var named = namesOf[i].Concat(new[] { part.Name }).FirstOrDefault(n => Instruments.Tokens(n).Any(t => t is "in" or "en"));
            part.Transpose = named != null ? Instruments.TranspositionOf(part.Instrument, named) : withKey.FirstOrDefault();
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
            // the key printed in this measure (a change of key inside the staff); the staff's own value is its last key
            int fifths = syms.FirstOrDefault(s => s.Kind == SymbolKind.KeySignature && s.Attributes.ContainsKey("fifths")) is { } ks
                         && int.TryParse(ks.Attributes["fifths"], out int f) ? f : staff.KeyFifths;
            if (fifths != (ctx.Key ?? int.MinValue) && staffNo == 1) { sm.KeyFifths = fifths; }
            ctx.Key = fifths;
        }
        else if (firstOfPart && staffNo == 1) { sm.KeyFifths = staff.KeyFifths; ctx.Key = staff.KeyFifths; }

        // the signature printed in this measure (a stray digit marked "ignored" is none); the staff's own value is the last
        // one on the staff, which holds for this measure only when no change follows later on the staff
        static TimeSpec? Own(Measure x) => x.Symbols.Where(s => s.Kind == SymbolKind.TimeSignature).Select(s => ParseTime(s.Attributes.GetValueOrDefault("time"))).FirstOrDefault(t => t != null);
        var own = Own(m);
        int mIndex = staff.Measures.IndexOf(m);
        bool changeLater = mIndex >= 0 && staff.Measures.Skip(mIndex + 1).Any(x => Own(x) != null);
        var time = own ?? (changeLater ? null : ParseTime(staff.TimeSignature));
        // a sliver without music before the staff's first signature (the staff start left of the system's bar line) is
        // under that signature
        if (own == null && changeLater && mIndex >= 0 && !m.Symbols.Any(IsMusic))
        {
            int k = mIndex + 1;
            while (k < staff.Measures.Count && Own(staff.Measures[k]) == null && !staff.Measures[k].Symbols.Any(IsMusic)) k++;
            if (k < staff.Measures.Count && Own(staff.Measures[k]) is TimeSpec next) time = next;
        }
        // a signature printed on this staff is recorded for it even when unchanged (the other staff may change alone)
        if (own != null) sm.StaffTimes[staffNo - 1] = own;
        if (own != null && (ctx.Time == null || !ctx.Time.Equals(own)))
        {
            if (staffNo == 1) sm.Time = time;
            if (staffNo == 1) sm.TimeFromStartGlyph = syms.Any(s => s.Kind == SymbolKind.TimeSignature && s.Name is "timeSigCommon" or "timeSigCutCommon" && s.Attributes.ContainsKey("timezone"));
            sm.StaffTimes[staffNo - 1] = time;
            ctx.Time = time;
        }
        else if (ctx.Time == null && time != null) { if (staffNo == 1) sm.Time = time; sm.StaffTimes[staffNo - 1] = time; ctx.Time = time; }
        else if (own != null && staffNo == 1) sm.TimeRestated = true;
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
                    if (s.Name != "multiRest") events.Add(MakeRest(s, staffNo));   // a multi-measure rest is written by the caller
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

        // a loose triplet "3" (over separate notes, no beam or bracket): in a voice too long for the measure, the run of
        // notes centred under it whose third is exactly the surplus becomes the triplet
        if (ctx.Time != null)
        {
            var loose = m.Symbols.Where(s => s.Kind == SymbolKind.Tuplet && s.Attributes.ContainsKey("loose")).ToList();
            // several triplets in a row over the whole measure (a "3" over each, some perhaps not read): a voice exactly
            // half as long again as the measure, with no tuplet yet, is triplets throughout
            if (loose.Count > 0)
                foreach (var voice in sm.Events.Where(e => e.Staff == staffNo && !e.Grace).GroupBy(e => e.Voice))
                {
                    var evs = voice.ToList();
                    if (evs.Count >= 3 && evs.All(e => e.TupletActual == 0 && e.Duration % 3 == 0) && 2 * evs.Sum(e => e.Duration) == 3 * ctx.Time.Length
                        && evs.Any(e => loose.Any(t => Math.Abs(e.X - t.Bounds.CenterX) <= 2.5 * ss)))
                        foreach (var e in evs) { e.Duration = e.Duration * 2 / 3; e.TupletActual = 3; e.TupletNormal = 2; }
                }
            foreach (var t in loose)
                LooseTriplet(sm, staffNo, t, ctx.Time.Length, ss);
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

    private static void LooseTriplet(ScoreMeasure sm, int staffNo, MusicSymbol t, int length, double ss)
    {
        double tx = t.Bounds.CenterX;
        foreach (var voice in sm.Events.Where(e => e.Staff == staffNo && !e.Grace).GroupBy(e => e.Voice))
        {
            var evs = voice.OrderBy(e => e.X).ToList();
            int surplus = evs.Sum(e => e.Duration) - length;
            if (surplus <= 0 || evs.Any(e => e.TupletActual != 0)) continue;
            int best = -1, bestLen = 0; double bestD = double.MaxValue;
            for (int i = 0; i < evs.Count; i++)
                for (int n = 2; n <= 4 && i + n <= evs.Count; n++)
                {
                    int span = evs.Skip(i).Take(n).Sum(e => e.Duration);
                    if (span % 3 != 0 || span / 3 != surplus) continue;
                    double mid = (evs[i].X + evs[i + n - 1].X) / 2.0;
                    double d = Math.Abs(mid - tx);
                    if (d <= 2.5 * ss && d < bestD) { bestD = d; best = i; bestLen = n; }
                }
            if (best < 0) continue;
            for (int k = 0; k < bestLen; k++)
            {
                var e = evs[best + k];
                e.LooseTripletFrom = e.Duration;
                e.Duration = e.Duration * 2 / 3;
                e.TupletActual = 3; e.TupletNormal = 2;
                e.TupletStart = k == 0; e.TupletStop = k == bestLen - 1;
            }
            return;
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
            // a lone rest in a staff's measure is the bar rest: a whole rest, or a block read as a half rest (it sits on a
            // line instead of hanging from one) that alone could not fill the measure
            if (evs.Count == 1 && evs[0].IsRest && evs[0].Dots == 0 && evs[0].TupletActual == 0
                && (evs[0].Type == "whole" || evs[0].Type == "half" && expected > evs[0].Duration && !sm.Events.Any(e => e.Staff == staffNo && e != evs[0])))
            {
                evs[0].Duration = expected; evs[0].MeasureRest = true;
                // beside another voice the bar rest says nothing about that voice's length (it must not hide a short one)
                if (!sm.Events.Any(e => e.Staff == staffNo && e.Voice != voice.Key)) sm.Fit.Sums.Add((staffNo, voice.Key, expected));
                continue;
            }
            sm.Fit.Sums.Add((staffNo, voice.Key, sum));
            if (sum != expected)
            {
                // dotted beamed pairs read alike (see MakeGroupEvents): all of them, or a single one, as dotted 8th + 16th
                var pairs = evs.Where(e => e.AltType != null).GroupBy(e => e.AltPair).Select(g => g.ToList()).Where(g => g.Count == 2).ToList();
                int Delta(List<ScoreEvent> pr) => pr.Sum(e => DurationOf(e.AltType!, e.Dots) - e.Duration);
                List<List<ScoreEvent>>? fix = pairs.Count > 0 && sum + pairs.Sum(Delta) == expected ? pairs : pairs.FirstOrDefault(pr => sum + Delta(pr) == expected) is { } one ? new() { one } : null;
                if (fix != null)
                {
                    foreach (var pr in fix)
                        foreach (var e in pr) { e.Type = e.AltType!; e.Duration = DurationOf(e.Type, e.Dots); e.Beams = BeamsFor(e.Type, pr.IndexOf(e)); }
                    sum = evs.Sum(e => e.Duration);
                    sm.Fit.Sums[^1] = (staffNo, voice.Key, sum);
                    sm.Warnings.Add($"staff {staffNo} voice {voice.Key}: {fix.Count} dotted beamed pair(s) read as dotted 8th + 16th to fit {time}");
                }
            }
            if (sum < expected)
            {
                // eroded augmentation dots: all candidates, or one, or two of them, when they make up exactly the deficit
                var dc = evs.Where(e => e.DotCandidate && e.Dots == 0 && e.TupletActual == 0 && !e.Grace).ToList();
                int Gain(ScoreEvent e) => DurationOf(e.Type, 1) - e.Duration;
                List<ScoreEvent>? take = dc.Count == 0 ? null
                    : sum + dc.Sum(Gain) == expected ? dc
                    : dc.FirstOrDefault(e => sum + Gain(e) == expected) is { } d1 ? new() { d1 }
                    : dc.SelectMany((a, i) => dc.Skip(i + 1).Select(b => new List<ScoreEvent> { a, b })).FirstOrDefault(p => sum + p.Sum(Gain) == expected);
                if (take != null)
                {
                    foreach (var e in take) { e.Dots = 1; e.Duration = DurationOf(e.Type, 1); }
                    sum = evs.Sum(e => e.Duration);
                    sm.Fit.Sums[^1] = (staffNo, voice.Key, sum);
                    sm.Warnings.Add($"staff {staffNo} voice {voice.Key}: {take.Count} faint augmentation dot(s) taken to fit {time}");
                }
            }
            if (sum > expected)
            {
                // a stemless black head (its stem and flag broke away) read as a quarter: an 8th when exactly that
                // makes the voice fit
                // (also a quarter with a possible hairline flag); one of them, or all of them
                // (also a "half" note whose head is mostly ink: a speckled black head)
                var cands = evs.Where(e => e.StemlessBlack && e.Type == "quarter" && e.Dots == 0 || e.HollowDoubt && e.Type == "half" && e.Dots == 0 && e.TupletActual == 0).ToList();
                var take = cands.FirstOrDefault(e => sum - e.Duration / 2 == expected) is { } one ? new List<ScoreEvent> { one }
                         : cands.Count > 1 && sum - cands.Sum(e => e.Duration / 2) == expected ? cands : null;
                if (take != null)
                {
                    foreach (var e in take) { e.Type = e.Type == "half" ? "quarter" : "eighth"; e.Duration /= 2; }
                    sum = evs.Sum(e => e.Duration);
                    sm.Fit.Sums[^1] = (staffNo, voice.Key, sum);
                    sm.Warnings.Add($"staff {staffNo} voice {voice.Key}: {take.Count} note(s) without a clear flag or with a flecked head taken as half as long to fit {time}");
                }
            }
            if (sum > expected)
            {
                // 8ths under a beam as thick as two merged beams: 16ths when exactly that makes the voice fit (the whole
                // beamed group: its notes share the beam)
                var thick = evs.Where(e => e.ThickBeam && e.Type == "eighth" && e.Dots == 0 && e.TupletActual == 0).ToList();
                // (the group's 8ths: where the merged band thins, some of its stems already count two beams)
                var groups = thick.GroupBy(e => e.GroupId).Where(g => g.Key != 0)
                                  .Select(g => evs.Where(e => e.GroupId == g.Key && e.Type == "eighth" && e.Dots == 0 && e.TupletActual == 0).ToList())
                                  .Where(g => !evs.Any(e => e.GroupId == g[0].GroupId && e.Dots > 0)).ToList();
                List<ScoreEvent>? pick = groups.FirstOrDefault(g => sum - g.Sum(e => e.Duration / 2) == expected);
                // several groups: the first ones that together make up the surplus
                for (int n = 2; pick == null && n <= groups.Count; n++)
                    if (sum - groups.Take(n).Sum(g => g.Sum(e => e.Duration / 2)) == expected) pick = groups.Take(n).SelectMany(g => g).ToList();
                if (pick != null)
                {
                    foreach (var e in pick)
                    {
                        e.Type = "16th"; e.Duration /= 2;
                        if (e.Beams.Length == 1) e.Beams = new[] { e.Beams[0], e.Beams[0] };
                    }
                    sum = evs.Sum(e => e.Duration);
                    sm.Fit.Sums[^1] = (staffNo, voice.Key, sum);
                    sm.Warnings.Add($"staff {staffNo} voice {voice.Key}: {pick.Count} 8th(s) under a thick beam taken as 16ths to fit {time}");
                }
            }
            // the beam counts seen from afar: the notes of a beamed group re-timed by them, a group at a time (the first
            // ones that together make the measure add up)
            if (sum != expected)
            {
                bool more = sum > expected;
                static string? TypeFor(int beams) => beams switch { 1 => "eighth", 2 => "16th", 3 => "32nd", 4 => "64th", _ => null };
                static int BeamsOf(string t) => t switch { "eighth" => 1, "16th" => 2, "32nd" => 3, "64th" => 4, _ => 0 };
                var changes = evs.Where(e => e.GroupId != 0 && e.TupletActual == 0 && !e.Grace && BeamsOf(e.Type) > 0
                                             && (more ? e.CoarseMore > BeamsOf(e.Type) : e.CoarseLess > 0 && e.CoarseLess < BeamsOf(e.Type)))
                                 .GroupBy(e => e.GroupId)
                                 .Select(g => g.Select(e => (e, type: TypeFor(more ? e.CoarseMore : e.CoarseLess)!)).Where(c => c.type != null).ToList())
                                 .Where(g => g.Count > 0).ToList();
                int Delta(List<(ScoreEvent e, string type)> g) => g.Sum(c => DurationOf(c.type, c.e.Dots) - c.e.Duration);
                List<(ScoreEvent e, string type)>? pick = null;
                for (int n = 1; pick == null && n <= changes.Count; n++)
                    if (sum + changes.Take(n).Sum(Delta) == expected) pick = changes.Take(n).SelectMany(g => g).ToList();
                pick ??= changes.FirstOrDefault(g => sum + Delta(g) == expected);
                if (pick != null)
                {
                    foreach (var (e, type) in pick)
                    {
                        int from = e.Beams.Length, to = BeamsOf(type);
                        e.Type = type; e.Duration = DurationOf(type, e.Dots);
                        if (to > from && from > 0) e.Beams = e.Beams.Concat(Enumerable.Repeat(e.Beams[^1], to - from)).ToArray();
                        else if (to < from) e.Beams = e.Beams.Take(to).ToArray();
                    }
                    sum = evs.Sum(e => e.Duration);
                    sm.Fit.Sums[^1] = (staffNo, voice.Key, sum);
                    sm.Warnings.Add($"staff {staffNo} voice {voice.Key}: {pick.Count} note(s) re-timed by the beams seen from afar to fit {time}");
                }
            }
            // groups read otherwise than their look-alikes: the look-alikes' rhythm, a group at a time
            if (sum != expected)
            {
                static string? TypeOf(int beams) => beams switch { 1 => "eighth", 2 => "16th", 3 => "32nd", 4 => "64th", _ => null };
                var alts = evs.Where(e => e.Pattern != null && e.GroupId != 0 && e.TupletActual == 0 && !e.Grace && TypeOf(e.Pattern.Value.Beams) != null)
                              .GroupBy(e => e.GroupId).Select(g => g.ToList())
                              .Where(g => evs.Count(e => e.GroupId == g[0].GroupId) == g.Count).ToList();
                int Delta(List<ScoreEvent> g) => g.Sum(e => DurationOf(TypeOf(e.Pattern!.Value.Beams)!, e.Pattern.Value.Dots) - e.Duration);
                List<ScoreEvent>? pick = null;
                for (int n = 1; pick == null && n <= alts.Count; n++)
                    if (sum + alts.Take(n).Sum(Delta) == expected) pick = alts.Take(n).SelectMany(g => g).ToList();
                pick ??= alts.FirstOrDefault(g => sum + Delta(g) == expected);
                if (pick != null)
                {
                    foreach (var e in pick)
                    {
                        var (beams, dots) = e.Pattern!.Value;
                        int from = e.Beams.Length;
                        e.Type = TypeOf(beams)!; e.Dots = dots; e.Duration = DurationOf(e.Type, dots);
                        if (beams > from && from > 0) e.Beams = e.Beams.Concat(Enumerable.Repeat(e.Beams[^1], beams - from)).ToArray();
                        else if (beams < from) e.Beams = e.Beams.Take(beams).ToArray();
                    }
                    sum = evs.Sum(e => e.Duration);
                    sm.Fit.Sums[^1] = (staffNo, voice.Key, sum);
                    sm.Warnings.Add($"staff {staffNo} voice {voice.Key}: {pick.Count} note(s) given the rhythm of look-alike groups to fit {time}");
                }
            }
            // quarters joined by beams seen only from afar: the run's notes beamed, a run at a time
            if (sum > expected)
            {
                static string? TypeFor(int beams) => beams switch { 1 => "eighth", 2 => "16th", 3 => "32nd", _ => null };
                var runs = evs.Where(e => e.CoarseChain > 0 && e.Type == "quarter" && e.TupletActual == 0 && !e.Grace && TypeFor(e.CoarseBeamed) != null)
                              .GroupBy(e => e.CoarseChain).Select(g => g.ToList()).Where(g => g.Count >= 2).ToList();
                int Delta(List<ScoreEvent> g) => g.Sum(e => DurationOf(TypeFor(e.CoarseBeamed)!, e.Dots) - e.Duration);
                List<ScoreEvent>? pick = runs.FirstOrDefault(g => sum + Delta(g) == expected);
                for (int n = 2; pick == null && n <= runs.Count; n++)
                    if (sum + runs.Take(n).Sum(Delta) == expected) pick = runs.Take(n).SelectMany(g => g).ToList();
                if (pick != null)
                {
                    foreach (var g in pick.GroupBy(e => e.CoarseChain))
                    {
                        var list = g.OrderBy(e => e.X).ToList();
                        for (int k = 0; k < list.Count; k++)
                        {
                            var e = list[k];
                            e.Type = TypeFor(e.CoarseBeamed)!; e.Duration = DurationOf(e.Type, e.Dots);
                            var st = k == 0 ? BeamState.Begin : k == list.Count - 1 ? BeamState.End : BeamState.Continue;
                            e.Beams = Enumerable.Repeat(st, e.CoarseBeamed).ToArray();
                        }
                    }
                    sum = evs.Sum(e => e.Duration);
                    sm.Fit.Sums[^1] = (staffNo, voice.Key, sum);
                    sm.Warnings.Add($"staff {staffNo} voice {voice.Key}: {pick.Count} quarter(s) beamed by beams seen from afar to fit {time}");
                }
            }
            // quarters whose heads are light from afar: half notes, one or all of them, when that makes the measure fit
            if (sum < expected)
            {
                var hollow = evs.Where(e => e.CoarseHollow && e.Type == "quarter" && e.TupletActual == 0).ToList();
                var take = hollow.FirstOrDefault(e => sum + e.Duration == expected) is { } one ? new List<ScoreEvent> { one }
                         : hollow.Count > 1 && sum + hollow.Sum(e => e.Duration) == expected ? hollow : null;
                if (take != null)
                {
                    foreach (var e in take) { e.Type = "half"; e.Duration *= 2; }
                    sum = evs.Sum(e => e.Duration);
                    sm.Fit.Sums[^1] = (staffNo, voice.Key, sum);
                    sm.Warnings.Add($"staff {staffNo} voice {voice.Key}: {take.Count} quarter(s) with a light head taken as half notes to fit {time}");
                }
            }
            // two "whole notes" filling twice the measure: half notes whose stems were not printed or lost (an old print)
            if (sum == 2 * expected && evs.Count == 2 && evs.All(e => !e.IsRest && e.Type == "whole" && e.Dots == 0 && e.TupletActual == 0 && e.Duration == expected))
            {
                foreach (var e in evs) { e.Type = "half"; e.Duration /= 2; }
                sum = expected;
                sm.Fit.Sums[^1] = (staffNo, voice.Key, sum);
                sm.Warnings.Add($"staff {staffNo} voice {voice.Key}: two stemless heads taken as half notes to fit {time}");
            }
            if (sum == expected) continue;
            if (sum > expected)
            {
                // small notes that make up exactly the surplus are grace notes: they stay, taking no time
                var graces = evs.Where(e => e.GraceCandidate).ToList();
                if (graces.Count > 0 && sum - graces.Sum(e => e.Duration) == expected)
                {
                    foreach (var g in graces)
                    {
                        g.Grace = true; g.Duration = 0; g.Beams = Array.Empty<BeamState>();
                        if (g.Type is "quarter" or "half" or "whole") g.Type = "eighth";
                    }
                    sm.Fit.Sums[^1] = (staffNo, voice.Key, expected);
                    sm.Warnings.Add($"staff {staffNo} voice {voice.Key}: {graces.Count} small note(s) taken as grace notes to fit {time}");
                    continue;
                }
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
        SetType(ev, s.Duration, s.Dots > 0 ? s.Dots : s.Notes.Select(n => n.Dots).DefaultIfEmpty(0).Max(), sm);
        foreach (var n in s.Notes.OrderBy(n => n.Step)) ev.Notes.Add(MakeNote(n, clef, fifths));
        // stemless heads (whole notes, but also text glyphs taken for heads) and heads far outside the staff are suspects
        // user-corrected or user-added symbols are never dropped to make a measure fit
        bool asserted = s.Attributes.ContainsKey("corrected") || s.Attributes.ContainsKey("added");
        if (!asserted && (s.StemUp == null || s.Notes.Any(n => n.Step > 18 || n.Step < -10))) ev.Uncertain = true;
        if (!asserted && s.StemUp == null && s.Notes.Count > 0 && s.Notes.All(n => n.HeadType == "black")) ev.StemlessBlack = true;
        if (!asserted && s.Attributes.ContainsKey("flagcand")) ev.StemlessBlack = true;   // a hairline flag may make it an 8th
        if (!asserted && s.Attributes.ContainsKey("hollowdoubt")) ev.HollowDoubt = true;
        if (!asserted && s.Attributes.ContainsKey("thickbeam")) ev.ThickBeam = true;
        if (!asserted && s.Attributes.ContainsKey("coarsehollow")) ev.CoarseHollow = true;
        if (!asserted && s.Attributes.TryGetValue("coarsebeamed", out var cbs) && int.TryParse(cbs, out int cbn)
            && s.Attributes.TryGetValue("coarsechain", out var ccs) && int.TryParse(ccs, out int ccn)) { ev.CoarseBeamed = cbn; ev.CoarseChain = ccn; }
        if (!asserted && s.Attributes.TryGetValue("patternbeams", out var pb) && pb.Split('.') is { Length: 2 } pp && int.TryParse(pp[0], out int pbn) && int.TryParse(pp[1], out int pdn)) ev.Pattern = (pbn, pdn);
        if (!asserted && s.Attributes.TryGetValue("coarsemore", out var cm) && int.TryParse(cm, out int cmn)) ev.CoarseMore = cmn;
        if (!asserted && s.Attributes.TryGetValue("coarseless", out var cl) && int.TryParse(cl, out int cln)) ev.CoarseLess = cln;
        if (s.Attributes.TryGetValue("articulation", out var art) && art == "staccato") ev.Articulations.Add("staccato");
        if (s.Attributes.ContainsKey("dotcand") && ev.Dots == 0 && !ev.IsRest) ev.DotCandidate = true;
        // small notes: grace-note candidates, settled by the measure length in Validate
        if (s.Attributes.TryGetValue("grace", out var gr))
        {
            ev.GraceCandidate = true; ev.GraceSlash = gr == "slash"; ev.Uncertain = true;
        }
        // slashes through the stem of a hollow note: a single-note tremolo, the written value stays
        if (s.Attributes.TryGetValue("tremolo", out var trem) && int.TryParse(trem, out int marks)) { ev.TremoloMarks = marks; ev.TremoloType = "single"; }
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
        // two hollow notes joined by beams: an alternating (two-note) tremolo; each note shows the value of the whole
        // figure, so each lasts half of it (MusicXML: time modification 2:1, tremolo start/stop, no beams)
        if (group.Name.StartsWith("beamed") && evs.Count >= 2 && evs.Count % 2 == 0 && evs.All(e => e.TremoloType == "single"))
        {
            for (int i = 0; i + 1 < evs.Count; i += 2)
                foreach (var (e, k) in new[] { (evs[i], 0), (evs[i + 1], 1) })
                {
                    e.TremoloType = k == 0 ? "start" : "stop";
                    e.Duration /= 2;
                    e.TupletActual = 2; e.TupletNormal = 1;
                    e.GroupId = 0;
                }
            return evs;
        }
        // a beamed pair with one dotted note is a dotted 8th and a 16th (dotted 16th and 32nd in fast music): the
        // short hook of the second beam is easily credited to the wrong stem or missed, leaving both notes alike
        if (group.Name.StartsWith("beamed") && evs.Count == 2 && evs.Count(e => e.Dots == 1) == 1 && evs[0].Type == evs[1].Type
            && !evs.Any(e => e.TupletActual != 0 || e.Grace || e.TremoloType != null) && evs[0].Type is "eighth" or "16th" or "32nd")
        {
            // an alternative reading only: Validate applies it when it makes the voice fill the measure exactly (the dot
            // may also be a stray one, and then the partner really is what it looks like)
            string longT = evs[0].Type == "32nd" ? "16th" : "eighth", shortT = evs[0].Type == "32nd" ? "32nd" : "16th";
            foreach (var e in evs) { e.AltType = e.Dots == 1 ? longT : shortT; e.AltPair = evs[0]; }
        }
        // an undotted beamed pair of an 8th and a 16th: a tapering or thick beam end credited one note a second beam;
        // two 8ths when that makes the measure fit
        else if (group.Name.StartsWith("beamed") && evs.Count == 2 && evs.All(e => e.Dots == 0 && !e.DotCandidate) && evs[0].Type != evs[1].Type
                 && evs.All(e => e.Type is "eighth" or "16th") && !evs.Any(e => e.TupletActual != 0 || e.Grace || e.TremoloType != null))
        {
            foreach (var e in evs) { e.AltType = "eighth"; e.AltPair = evs[0]; }
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

    /// <summary>Beams of one note of a two-note beamed figure: one shared beam, and a hook for the shorter note.</summary>
    private static BeamState[] BeamsFor(string type, int index)
    {
        int levels = type switch { "eighth" => 1, "16th" => 2, "32nd" => 3, "64th" => 4, _ => 0 };
        var b = new BeamState[levels];
        for (int k = 0; k < levels; k++) b[k] = k == 0 ? (index == 0 ? BeamState.Begin : BeamState.End) : (index == 0 ? BeamState.ForwardHook : BeamState.BackwardHook);
        return b;
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
        // the same note read twice (one symbol found by two passes): once
        for (int i = events.Count - 1; i >= 1; i--)
        {
            var e = events[i];
            if (e.IsRest || e.Notes.Count == 0) continue;
            if (events.Take(i).Any(o => !o.IsRest && Math.Abs(o.X - e.X) <= 2 && o.StemUp == e.StemUp && o.Duration == e.Duration
                                        && o.Notes.Select(n => n.Pitch).SequenceEqual(e.Notes.Select(n => n.Pitch)))) events.RemoveAt(i);
        }
        // two notes struck together with their own stems, one up and one down, of the same length: a chord when that
        // makes the measure add up as one voice (two voices would both have to hold the rests that follow)
        if (expected is int len && events.Count > 1)
        {
            double ss0 = staff.Space;
            // (the up-stem one may begin a beamed group; a down-stem "quarter" with a possible flag is an 8th beside an 8th)
            var upNotes = events.Where(e => !e.IsRest && e.StemUp == true && e.TupletActual == 0).ToList();
            bool Same(ScoreEvent d, ScoreEvent u) => d.Duration == u.Duration || d.StemlessBlack && d.Type == "quarter" && d.Dots == 0 && 2 * u.Duration == d.Duration;
            // (a double stop: the heads right above each other, at most a fourth apart)
            static int Diat(ScoreNote n) => n.Pitch.Octave * 7 + "CDEFGAB".IndexOf(n.Pitch.Step[0]);
            bool Close(ScoreEvent d, ScoreEvent u) => d.Notes.Count > 0 && u.Notes.Count > 0
                && Math.Abs(d.Notes.Max(Diat) - u.Notes.Min(Diat)) <= 3 || Math.Abs(u.Notes.Min(Diat) - d.Notes.Max(Diat)) <= 3;
            var pairs = upNotes.Select(u => (u, d: events.FirstOrDefault(d => !d.IsRest && d.StemUp == false && d.GroupId == 0 && d.TupletActual == 0
                                                                     && Math.Abs(d.X - u.X) < 0.4 * ss0 && Same(d, u) && Close(d, u))))
                           .Where(p => p.d != null).ToList();
            // (also when no other note of the measure has its stem down: one voice, whatever the time signature read
            // so far says, which a later check may still correct)
            bool otherDown = events.Any(e => !e.IsRest && e.StemUp == false && !pairs.Any(p => p.d == e));
            if (pairs.Count > 0 && (events.Sum(e => e.Duration) - pairs.Sum(p => p.d!.Duration) == len || !otherDown))
                foreach (var (u, d) in pairs)
                {
                    u.Notes.AddRange(d!.Notes);
                    events.Remove(d);
                }
        }
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
                if (notes.Any(b => dir[b] == false && Math.Abs(b.X - a.X) < 1.0 * ss)) { twoVoices = true; break; }
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
            // a whole rest hanging outside the staff, in a measure with notes: the other player's (or voice's) bar rest
            bool BarRestApart(ScoreEvent e) => e.IsRest && e.Type == "whole" && (e.Y > staff.BottomY(e.X) || e.Y < staff.TopY(e.X))
                                               && events.Any(o => !o.IsRest);
            static bool OnlyBarRest(List<ScoreEvent> v) => v.Count == 1 && v[0].IsRest && v[0].Type == "whole";
            double Cost(Func<ScoreEvent, bool> isSecond)
            {
                var va = events.Where(e => !isSecond(e)).ToList(); var vb = events.Where(isSecond).ToList();
                int a = Repaired(va), b = OnlyBarRest(vb) ? L : Repaired(vb);
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
                ("bar rest apart", BarRestApart),
            };
            // only consider splits that use both stem directions or separate long from short notes; a measure that adds
            // up as one voice stays one voice
            bool bothDirections = notes.Any(n => dir[n] == true) && notes.Any(n => dir[n] == false);
            double currentCost = Cost(e => second[e]);
            (string name, Func<ScoreEvent, bool> isSecond, double cost) best = ("current", e => second[e], currentCost);
            if (total == L) best = ("single", _ => false, 0);
            // two bar rests one above the other: each voice resting through the measure
            else if (events.Count == 2 && events.All(e => e.IsRest && e.Type == "whole") && Math.Abs(events[0].X - events[1].X) < 1.0 * ss
                     && Math.Abs(events[0].Y - events[1].Y) >= 1.0 * ss)
            {
                var lower = events.MaxBy(e => e.Y)!;
                best = ("two bar rests", e => e == lower, 0);
            }
            else
                foreach (var (name, isSecond) in candidates)
                {
                    if (name == "bar rest apart" && events.Count(BarRestApart) != 1) continue;
                    if (name != "single" && name != "long notes" && name != "bar rest apart" && !bothDirections) continue;
                    // a second voice made only of uncertain notes is a stray head hiding from the duration check
                    // (exception: held long notes struck together with a note of the moving voice, such as a drone
                    // double stop; stemless, so uncertain, but a glyph that lines up with a head is no stray mark)
                    if (name != "single" && name != "bar rest apart" && !events.Any(e => isSecond(e) && !e.Uncertain && !e.IsRest)
                        && !(name == "long notes" && events.Where(isSecond).All(e => !e.IsRest && e.Notes.Count > 0
                                && e.Y > staff.TopY(e.X) - 2 * ss && e.Y < staff.BottomY(e.X) + 2 * ss
                                && events.Any(o => !isSecond(o) && !o.IsRest && Math.Abs(o.X - e.X) < 0.8 * ss)))) continue;
                    double c = Cost(isSecond);
                    // a second voice that does not fill the measure needs both voices to start it together (a note,
                    // chord, whole note or rest of each at the same place): otherwise a melody whose stems merely turn
                    // with the pitch would hide its wrong duration in an invented, unfinished second voice
                    if (name != "single")
                    {
                        var vb = events.Where(isSecond).ToList();
                        if (!OnlyBarRest(vb) && Repaired(vb) < L)
                        {
                            var fa = events.Where(e => !isSecond(e)).MinBy(e => e.X);
                            var fb = vb.MinBy(e => e.X);
                            if (fa == null || fb == null || Math.Abs(fa.X - fb.X) > 1.6 * ss || (fa.IsRest && fb.IsRest)) continue;
                        }
                    }
                    // an alternative split must make the measure add up exactly; a direction-based one may also merely
                    // improve a measure that really has stems both ways (a single-voice measure with a wrong duration
                    // must not be "fixed" by inventing a second voice)
                    bool directionBased = name.StartsWith("direction") && bothDirections || name == "bar rest apart";
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
                // (a final bar line ends a run too: a new movement, its signature perhaps not read)
                // (and a signature printed again: what follows it is under it whatever was carried in before)
                if (runs.Count > 0 && runs[^1][0].Fit.Expected == m.Fit.Expected && runs[^1][0].Page == m.Page && (m.Time == null || m.Time.Length == m.Fit.Expected)
                    && runs[^1][^1].RightBar != BarStyle.LightHeavy && !m.TimeRestated) runs[^1].Add(m);
                else runs.Add(new List<ScoreMeasure> { m });
            }
            // a replaced signature stays replaced on the following pages, up to the next printed one (their measures were
            // checked against the misread value carried over)
            (int from, TimeSpec to)? carriedFix = null;
            // one sample per staff (the fullest voice): the two staves of a piano measure are judged separately
            List<int> Samples(List<ScoreMeasure> run) => run
                .Where(m => !(m.Events.Count == 1 && m.Events[0].IsRest && m.Events[0].Type == "whole") && m.Events.Count > 0)
                // (as written: a loose triplet was fitted to the signature being questioned)
                .SelectMany(m => m.Events.GroupBy(e => e.Staff).Select(g => g.GroupBy(e => e.Voice).Select(v => v.Sum(e => e.LooseTripletFrom > 0 ? e.LooseTripletFrom : e.Duration)).Max()))
                .Where(x => x > 0).ToList();
            void Revalidate(List<ScoreMeasure> run, TimeSpec t)
            {
                foreach (var m in run)
                {
                    // loose triplets were placed to fit the old signature
                    foreach (var e in m.Events.Where(e => e.LooseTripletFrom > 0))
                    {
                        e.Duration = e.LooseTripletFrom; e.LooseTripletFrom = 0;
                        e.TupletActual = 0; e.TupletNormal = 0; e.TupletStart = e.TupletStop = false;
                    }
                    m.Warnings.RemoveAll(w => w.Contains("duration") || w.Contains("dropped uncertain"));
                    m.Fit.Sums.Clear(); m.Fit.DroppedPerStaff[0] = m.Fit.DroppedPerStaff[1] = 0;
                    for (int st = 1; st <= part.StaffCount; st++) Validate(m, t, st);
                }
            }
            foreach (var run in runs)
            {
                int? assembled = run[0].Fit.Expected;   // the value carried into this run when the score was assembled
                if (run.Any(m => m.Time != null || m.TimeRestated)) carriedFix = null;
                else if (carriedFix is { } cf && run[0].Fit.Expected == cf.from && Samples(run).Count(x => x == cf.to.Length) > Samples(run).Count(x => x == cf.from))
                {
                    // ... and the page is still checked against it (a new movement whose signature was not read)
                    Revalidate(run, cf.to);
                }
                else carriedFix = null;
                if (run.Count < 4) continue;
                // staves with their own metres (9/8 over 3/4) were each checked against their own signature
                if (run.Any(m => m.StaffTimes[0] is TimeSpec a && m.StaffTimes[1] is TimeSpec b && (a.TripletsAgainst(b) || b.TripletsAgainst(a)))) continue;
                int? expected = run[0].Fit.Expected;
                // a signature read from the C or cut-C glyph in the signature zone at the start of a staff is reliable; digit
                // readings, carried values and a C read further along (a smudge at a repeat sign) are questioned
                var inForceMeasure = part.Measures.LastOrDefault(m => m.Number <= run[0].Number && m.Time != null);
                // (up to the end of its movement: a final bar line in between starts another one)
                bool sameMovement = inForceMeasure != null && !part.Measures.Skip(part.Measures.IndexOf(inForceMeasure)).TakeWhile(m => m != run[0]).Any(m => m.RightBar == BarStyle.LightHeavy);
                if (expected != null && inForceMeasure?.Time?.Symbol != null && inForceMeasure.Time.Length == expected && inForceMeasure.Page == run[0].Page
                    && inForceMeasure.TimeFromStartGlyph && sameMovement) continue;
                var informative = run.Where(m => !(m.Events.Count == 1 && m.Events[0].IsRest && m.Events[0].Type == "whole") && m.Events.Count > 0).ToList();
                if (informative.Count < 4) continue;
                var sums = Samples(informative);
                int fitting = expected == null ? 0 : sums.Count(x => x == expected);
                if (sums.Count < 4) continue;
                int mode = sums.GroupBy(x => x).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
                int share = sums.Count(x => x == mode);
                // at least a fifth fits: the signature stands, unless another length holds the majority and half as many
                // measures again
                if (expected != null && fitting * 5 >= sums.Count && !(share * 2 >= sums.Count && share * 2 >= 3 * fitting)) continue;
                int eighth = Score.Divisions / 2;
                // the agreeing measures must be at least 30% (40% when no signature was read at all) and half as many again as the fitting ones
                int needed = expected == null ? (sums.Count * 2 + 4) / 5 : (sums.Count * 3 + 9) / 10;
                if (share < needed || share * 2 < 3 * fitting || mode == expected || mode % eighth != 0 || mode < 2 * eighth || mode > 24 * eighth) continue;
                int eighths = mode / eighth;
                TimeSpec t = eighths % 2 == 0 ? new TimeSpec(eighths / 2, 4) : new TimeSpec(eighths, 8);
                var first = run[0];
                score.Warnings.Add(expected == null
                    ? $"{part.Id} m{first.Number} (page {first.Page}): no time signature; {t} inferred from {share} of {sums.Count} measures"
                    : $"{part.Id} m{first.Number} (page {first.Page}): time signature {Fraction(expected.Value)} fits {fitting} of {informative.Count} measures; replaced by {t}, which {share} agree on");
                first.Time = t;
                foreach (var m in run) if (m != first && m.Time != null) m.Time = null;
                Revalidate(run, t);
                if (assembled != null) carriedFix = (assembled.Value, t);
            }
        }
        // pickup measures: the first measure of the part, and the first of a movement (after a final bar line, its time
        // signature stated again) when it is shorter than the time signature
        for (int i = 0; i < part.Measures.Count - 1; i++)
        {
            var first = part.Measures[i];
            if (i > 0 && !(part.Measures[i - 1].RightBar == BarStyle.LightHeavy && first.Time != null)) continue;
            var time = first.Time;
            if (time == null) continue;
            var sums = first.Events.GroupBy(e => e.Voice).Select(g => g.Sum(e => e.Duration)).ToList();
            int sum = sums.Count > 0 ? sums.Max() : 0;
            bool consistent = sums.Count > 0 && sums.All(x => x == sum);
            if (consistent && sum > 0 && sum < time.Length && sum % (Score.Divisions / 4) == 0 && first.Warnings.Any(w => w.Contains("duration")))
            {
                first.Implicit = true;
                first.Warnings.RemoveAll(w => w.Contains("duration"));
                first.Fit.Sums.Clear();
                if (i == 0)
                {
                    foreach (var m in part.Measures) m.Number--;   // pickup is measure 0
                    first.Number = 0;
                }
                // the movement's last measure (up to its final bar line, or the part's end) completes the pickup: it is
                // short by exactly the pickup's length
                int j = i + 1;
                while (j < part.Measures.Count - 1 && part.Measures[j].RightBar != BarStyle.LightHeavy && !(part.Measures[j + 1].Time != null && part.Measures[j].RightBar == BarStyle.LightLight)) j++;
                var last = part.Measures[j];
                int rest = last.Fit.Sums.Count > 0 ? last.Fit.Sums.Max(x => x.Sum) : -1;
                // a quarter of the pickup that may carry a hairline flag is the 8th the final measure leaves room for
                if (rest > 0 && rest != time.Length - sum && first.Events.FirstOrDefault(e => !e.IsRest && e.StemlessBlack && e.Type == "quarter" && time.Length - (sum - e.Duration / 2) == rest) is { } fl)
                {
                    fl.Type = "eighth"; fl.Duration /= 2; fl.Beams = Array.Empty<BeamState>();
                    sum -= fl.Duration;
                }
                if (j > i && last.Time == null && rest == time.Length - sum && last.Fit.Sums.All(x => x.Sum <= time.Length - sum))
                {
                    last.Implicit = true;
                    last.Warnings.RemoveAll(w => w.Contains("duration"));
                    last.Fit.Sums.Clear();
                }
            }
        }
        // a measure split by a repeat sign (or a double bar line): the part before it and the part after it are each
        // short, together exactly one measure (the upbeat to the repeated section); the second shares the first's number
        {
            TimeSpec? time = null;
            for (int i = 0; i + 1 < part.Measures.Count; i++)
            {
                var a = part.Measures[i]; var b = part.Measures[i + 1];
                if (a.Time != null) time = a.Time;
                if (time == null || b.Time != null || b.Implicit) continue;
                bool split = a.RepeatBackward || b.RepeatForward || a.RightBar is BarStyle.LightLight or BarStyle.LightHeavy || b.LeftBar is BarStyle.HeavyLight;
                if (!split) continue;
                int Sum(ScoreMeasure m)
                {
                    var sums = m.Events.Where(e => !e.Grace).GroupBy(e => (e.Staff, e.Voice)).Select(g => g.Sum(e => e.Duration)).ToList();
                    return sums.Count == 0 ? 0 : sums.Max();
                }
                int sa = Sum(a), sb = Sum(b);
                if (sa <= 0 || sb <= 0 || sa >= time.Length || sb >= time.Length || sa + sb != time.Length) continue;
                if (!a.Warnings.Any(w => w.Contains("duration")) && !b.Warnings.Any(w => w.Contains("duration"))) continue;
                foreach (var m in new[] { a, b }) { m.Warnings.RemoveAll(w => w.Contains("duration")); m.Fit.Sums.Clear(); }
                b.Implicit = true;
                for (int k = i + 1; k < part.Measures.Count; k++) part.Measures[k].Number--;
                i++;
            }
        }
        // barline styles from the detected barline kinds are set by the caller via measure boundaries; defaults: regular
        foreach (var m in part.Measures)
            foreach (var w in m.Warnings) score.Warnings.Add($"{part.Id} m{m.Number} (page {m.Page}): {w}");
    }
}
