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
        bool grand = options.GrandStaff ?? (maxStaves == 2 && systems.All(s => s.sys.Staves.Count == 2) && LooksLikeGrandStaff(systems[0].sys));
        int partCount = grand ? 1 : maxStaves;
        for (int i = 0; i < partCount; i++)
            score.Parts.Add(new Part { Id = $"P{i + 1}", Name = grand ? "Piano" : partCount == 1 ? "Part" : $"Part {i + 1}", StaffCount = grand ? 2 : 1 });
        if (systems.Any(s => s.sys.Staves.Count != maxStaves))
            score.Warnings.Add("systems have different staff counts; staves are mapped to parts by position from the top");

        var contexts = new StaffContext[partCount * (grand ? 2 : 1)];
        for (int i = 0; i < contexts.Length; i++) contexts[i] = new StaffContext();
        var partMeasureCount = new int[partCount];
        int page = 0; PageAnalysis? lastPage = null;

        foreach (var (pg, sys) in systems)
        {
            bool newPage = pg != lastPage; lastPage = pg; if (newPage) page++;
            // measures of the system: by boundary index; each staff has its own Measure list aligned by x
            int measureCount = sys.Staves.Max(s => s.Measures.Count);
            for (int mi = 0; mi < measureCount; mi++)
            {
                for (int pi = 0; pi < partCount; pi++)
                {
                    var part = score.Parts[pi];
                    var sm = new ScoreMeasure { Page = pg.PageNumber, NewSystem = mi == 0, NewPage = mi == 0 && newPage };
                    int staffCount = grand ? 2 : 1;
                    bool any = false;
                    for (int sIdx = 0; sIdx < staffCount; sIdx++)
                    {
                        int staffPos = grand ? sIdx : pi;
                        if (staffPos >= sys.Staves.Count) continue;
                        var staff = sys.Staves[staffPos];
                        if (mi >= staff.Measures.Count) continue;
                        var m = staff.Measures[mi];
                        any = true;
                        var ctx = contexts[grand ? sIdx : pi];
                        BuildMeasure(sm, m, staff, sIdx + 1, ctx, mi == 0, partMeasureCount[pi] == 0);
                    }
                    if (!any) continue;
                    partMeasureCount[pi]++;
                    sm.Number = partMeasureCount[pi];
                    part.Measures.Add(sm);
                }
            }
        }
        foreach (var part in score.Parts) FinishPart(part, score);
        return score;
    }

    private static bool LooksLikeGrandStaff(StaffSystem sys)
    {
        var a = sys.Staves[0].Clef; var b = sys.Staves[1].Clef;
        return (a is ClefKind.Treble or ClefKind.Unknown) && (b is ClefKind.Bass or ClefKind.Unknown) && !(a == ClefKind.Unknown && b == ClefKind.Unknown)
               || (a == ClefKind.Treble && b == ClefKind.Treble);
    }

    // ------------------------------------------------------------------ one staff-measure

    private static void BuildMeasure(ScoreMeasure sm, Measure m, Staff staff, int staffNo, StaffContext ctx, bool firstInSystem, bool firstOfPart)
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
        if (syms.Any(s => s.Kind == SymbolKind.TimeSignature) && time != null && (ctx.Time == null || !ctx.Time.Equals(time)))
        {
            if (staffNo == 1) sm.Time = time;
            ctx.Time = time;
        }
        else if (ctx.Time == null && time != null) { if (staffNo == 1) sm.Time = time; ctx.Time = time; }
        else if (firstOfPart && ctx.Time == null && staffNo == 1) sm.Warnings.Add("time signature unknown");

        // events
        var events = new List<ScoreEvent>();
        foreach (var s in syms)
        {
            switch (s.Kind)
            {
                case SymbolKind.Note:
                case SymbolKind.Chord:
                    events.Add(MakeNoteEvent(s, staff, staffNo, clef, ctx.Key ?? 0, sm));
                    break;
                case SymbolKind.NoteGroup:
                    events.AddRange(MakeGroupEvents(s, staff, staffNo, clef, ctx.Key ?? 0, sm));
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

        AssignVoices(events, staff, staffNo);
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
    }

    /// <summary>Checks every voice of a staff against the measure length; drops uncertain events when that makes the measure add up.</summary>
    private static void Validate(ScoreMeasure sm, TimeSpec time, int staffNo)
    {
        int expected = time.Length;
        foreach (var voice in sm.Events.Where(e => e.Staff == staffNo).GroupBy(e => e.Voice).ToList())
        {
            var evs = voice.ToList();
            int sum = evs.Sum(e => e.Duration);
            if (sum == expected) continue;
            if (evs.Count == 1 && evs[0].IsRest && evs[0].Type == "whole") { evs[0].Duration = expected; evs[0].MeasureRest = true; continue; }
            if (sum > expected)
            {
                var uncertain = evs.Where(e => e.Uncertain).ToList();
                ScoreEvent? drop1 = uncertain.FirstOrDefault(u => sum - u.Duration == expected);
                if (drop1 != null)
                {
                    sm.Events.Remove(drop1);
                    sm.Warnings.Add($"staff {staffNo} voice {voice.Key}: dropped uncertain {drop1} at x={drop1.X} to fit {time}");
                    continue;
                }
                var pair = uncertain.SelectMany((a, i) => uncertain.Skip(i + 1).Select(b => (a, b))).FirstOrDefault(p => sum - p.a.Duration - p.b.Duration == expected);
                if (pair.a != null)
                {
                    sm.Events.Remove(pair.a); sm.Events.Remove(pair.b);
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
        if (s.StemUp == null || s.Notes.Any(n => n.Step > 18 || n.Step < -10)) ev.Uncertain = true;
        if (s.Attributes.TryGetValue("articulation", out var art) && art == "staccato") ev.Articulations.Add("staccato");
        return ev;
    }

    private static IEnumerable<ScoreEvent> MakeGroupEvents(MusicSymbol group, Staff staff, int staffNo, ClefKind clef, int fifths, ScoreMeasure sm)
    {
        var children = group.Children.OrderBy(c => c.Notes.Min(n => n.HeadX)).ToList();
        var evs = children.Select(c => MakeNoteEvent(c, staff, staffNo, clef, fifths, sm)).ToList();
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
            // a note inside a beamed group without beams is most likely a beam-count miss: give it one beam
            foreach (var (e, c) in evs.Zip(children)) if (c.Flags == 0 && e.Type == "quarter") { e.Type = "eighth"; e.Duration = DurationOf("eighth", e.Dots); sm.Warnings.Add($"beamed note at x={e.X} had no beam count; assuming 8th"); }
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
        if (beatType is not (1 or 2 or 4 or 8 or 16 or 32)) beatType = 4;
        return new TimeSpec(beats, beatType);
    }

    // ------------------------------------------------------------------ voices

    private static void AssignVoices(List<ScoreEvent> events, Staff staff, int staffNo)
    {
        var notes = events.Where(e => !e.IsRest && e.StemUp.HasValue).ToList();
        bool twoVoices = false;
        double ss = staff.Space;
        int ups = notes.Count(n => n.StemUp == true), downs = notes.Count(n => n.StemUp == false);
        if (ups >= 2 && downs >= 2)
            foreach (var a in notes.Where(n => n.StemUp == true))
                if (notes.Any(b => b.StemUp == false && Math.Abs(b.X - a.X) < 1.2 * ss)) { twoVoices = true; break; }
        int baseVoice = (staffNo - 1) * 2 + 1;
        foreach (var e in events)
        {
            if (!twoVoices) { e.Voice = baseVoice; continue; }
            if (e.IsRest) e.Voice = e.Y > staff.CenterY ? baseVoice + 1 : baseVoice;
            else e.Voice = e.StemUp == false ? baseVoice + 1 : baseVoice;
        }
        if (twoVoices)
        {
            // rests shared by both voices: keep them in voice 1 only when voice 2 already accounts for that time span
        }
    }

    // ------------------------------------------------------------------ finishing

    private static void FinishPart(Part part, Score score)
    {
        // no time signature recognised anywhere: infer it from the most common measure length
        if (part.Measures.Count >= 3 && !part.Measures.Any(m => m.Time != null))
        {
            var sums = part.Measures.Select(m => m.Events.Where(e => e.Voice == 1).Sum(e => e.Duration)).Where(x => x > 0).ToList();
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
