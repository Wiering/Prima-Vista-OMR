namespace PVOMR.Core.Score;

/// <summary>Semantic score model (what MusicXML needs), independent of pixels.</summary>
public sealed class Score
{
    public const int Divisions = 96;   // divisions per quarter note: supports 64th notes, dots and triplets
    public string? Title { get; set; }
    public string? Source { get; set; }
    public List<Part> Parts { get; } = new();
    public List<string> Warnings { get; } = new();
}

public sealed class Part
{
    public string Id { get; set; } = "P1";
    public string Name { get; set; } = "Part 1";
    /// <summary>The short name printed on later systems (e.g. "Vc."), when one was read.</summary>
    public string? Abbreviation { get; set; }
    /// <summary>The recognised instrument (sound for playback), null when the name was not understood.</summary>
    public Instrument? Instrument { get; set; }
    /// <summary>Written-to-sounding interval of a transposing instrument (clarinet in B flat, horn in F ...).</summary>
    public Instruments.Transposition? Transpose { get; set; }
    public int StaffCount { get; set; } = 1;
    public List<ScoreMeasure> Measures { get; } = new();
}

public sealed record ClefSpec(string Sign, int Line, int OctaveChange = 0)
{
    public static readonly ClefSpec Treble = new("G", 2);
    public static readonly ClefSpec Bass = new("F", 4);
    public static readonly ClefSpec Alto = new("C", 3);
    public static readonly ClefSpec Tenor = new("C", 4);
    public static readonly ClefSpec Percussion = new("percussion", 2);
    public static readonly ClefSpec Treble8vb = new("G", 2, -1);
    public static readonly ClefSpec Bass8vb = new("F", 4, -1);
}

public sealed record TimeSpec(int Beats, int BeatType, string? Symbol = null)
{
    /// <summary>Measure length in divisions.</summary>
    public int Length => Beats * 4 * Score.Divisions / BeatType;
    /// <summary>A compound metre (6/8, 9/8, 12/8, 6/16 ...): beats grouped in threes.</summary>
    public bool Compound => BeatType >= 8 && Beats % 3 == 0;
    /// <summary>
    /// Written against <paramref name="simple"/> as triplets: a compound signature whose measure is one and a half
    /// times as long (9/8 over 3/4, 6/8 over 2/4, 12/8 over C), so that its eighths are the other staff's triplet eighths.
    /// </summary>
    public bool TripletsAgainst(TimeSpec simple) => Compound && !simple.Compound && Length * 2 == simple.Length * 3;
    public override string ToString() => $"{Beats}/{BeatType}";
}

public enum BarStyle { Regular, LightLight, LightHeavy, HeavyLight, Heavy, None }

/// <summary>How the recognised durations of one measure compare with the time signature (per staff and voice).</summary>
public sealed class MeasureFit
{
    /// <summary>Expected length in divisions, null when no time signature is known.</summary>
    public int? Expected { get; set; }
    /// <summary>Expected length per staff of the part when the staves have their own time signatures (9/8 over 3/4).</summary>
    public int?[] ExpectedPerStaff { get; } = new int?[2];
    public int? ExpectedFor(int staff) => ExpectedPerStaff[Math.Clamp(staff - 1, 0, 1)] ?? Expected;
    /// <summary>Summed durations per (staff, voice) before any uncertain event was dropped.</summary>
    public List<(int Staff, int Voice, int Sum)> Sums { get; } = new();
    /// <summary>Uncertain events dropped to make the measure fit, per staff of the part (index 0 = staff 1).</summary>
    public int[] DroppedPerStaff { get; } = new int[2];
    public int Dropped => DroppedPerStaff[0] + DroppedPerStaff[1];
    // the fullest voice of each staff must fill the measure; a second voice may stop early (a held note under a
    // moving line has no written rests), but no voice may overflow
    public bool Fits => Expected == null || (!TooLong && !TooShort);
    public bool TooLong => Expected != null && Sums.Any(s => s.Sum > ExpectedFor(s.Staff));
    public bool TooShort => Expected != null && Sums.GroupBy(s => s.Staff).Any(g => g.Max(s => s.Sum) < ExpectedFor(g.Key));
    /// <summary>The same check restricted to one staff of a two-staff part (1 = upper, 2 = lower).</summary>
    public MeasureFit ForStaff(int staffNo)
    {
        var f = new MeasureFit { Expected = ExpectedFor(staffNo) };
        f.DroppedPerStaff[0] = DroppedPerStaff[Math.Clamp(staffNo - 1, 0, 1)];
        f.Sums.AddRange(Sums.Where(s => s.Staff == staffNo));
        return f;
    }
}

public sealed class ScoreMeasure
{
    public MeasureFit Fit { get; } = new();
    public int Number { get; set; }
    public bool Implicit { get; set; }                 // pickup / partial measure
    public ClefSpec?[] Clefs { get; set; } = new ClefSpec?[2];   // per staff, only when (re)stated in this measure
    public int? KeyFifths { get; set; }
    public TimeSpec? Time { get; set; }
    /// <summary>The time signature stated on each staff of the part in this measure (differs between the staves
    /// only for a per-staff metre such as 9/8 over 3/4).</summary>
    public TimeSpec?[] StaffTimes { get; } = new TimeSpec?[2];
    public BarStyle LeftBar { get; set; } = BarStyle.None;
    public BarStyle RightBar { get; set; } = BarStyle.Regular;
    public bool RepeatForward { get; set; }
    public bool RepeatBackward { get; set; }
    public bool NewSystem { get; set; }
    public bool NewPage { get; set; }
    public List<ScoreEvent> Events { get; } = new();   // notes and rests, any voice/staff
    public List<Direction> Directions { get; } = new();
    public List<string> Warnings { get; } = new();
    public int X0 { get; set; }
    public int X1 { get; set; }
    public int Page { get; set; }
}

public sealed record PitchSpec(string Step, int Alter, int Octave)
{
    public override string ToString() => $"{Step}{(Alter switch { 1 => "#", -1 => "b", 2 => "x", -2 => "bb", _ => "" })}{Octave}";
}

public sealed class ScoreNote
{
    public PitchSpec Pitch { get; set; } = new("C", 0, 4);
    public string? AccidentalName { get; set; }   // sharp, flat, natural, double-sharp, flat-flat (displayed)
    public bool TieStart { get; set; }
    public bool TieStop { get; set; }
    public int SourceX { get; set; }
    public int SourceY { get; set; }
}

public enum BeamState { None, Begin, Continue, End, ForwardHook, BackwardHook }

public sealed class ScoreEvent
{
    public bool IsRest { get; set; }
    public bool MeasureRest { get; set; }
    public int Voice { get; set; } = 1;
    public int Staff { get; set; } = 1;
    /// <summary>Staff the note is written on when it differs from the voice's staff (cross-staff notes of a grand staff).</summary>
    public int? NotatedStaff { get; set; }
    /// <summary>Beamed group the event came from (0 = none): its beamed members form one voice.</summary>
    public int GroupId { get; set; }
    public int Duration { get; set; }                 // divisions
    public string Type { get; set; } = "quarter";     // MusicXML note type
    public int Dots { get; set; }
    public bool? StemUp { get; set; }
    public List<ScoreNote> Notes { get; } = new();    // chord when > 1
    public BeamState[] Beams { get; set; } = Array.Empty<BeamState>();   // index = beam level - 1
    public List<string> Articulations { get; } = new();
    public bool Fermata { get; set; }
    /// <summary>Tremolo strokes (1-4, 0 = none) and kind: "single" (slashes through one stem: the note repeated) or
    /// "start"/"stop" (two notes alternating, joined by tremolo beams).</summary>
    public int TremoloMarks { get; set; }
    /// <summary>A grace note (small note before the beat; takes no time in the measure); slashed = acciaccatura.</summary>
    public bool Grace { get; set; }
    public bool GraceSlash { get; set; }
    /// <summary>Printed small: becomes a grace note only when the measure overflows by exactly such notes.</summary>
    public bool GraceCandidate { get; set; }
    /// <summary>Alternative note value for a dotted beamed pair read alike (applied when it makes the measure fit).</summary>
    public string? AltType { get; set; }
    public ScoreEvent? AltPair { get; set; }
    public string? TremoloType { get; set; }
    public List<int> SlurStarts { get; } = new();
    public List<int> SlurStops { get; } = new();
    /// <summary>Tuplet ratio (actual:normal), 0 = none. Duration already reflects the tuplet.</summary>
    public int TupletActual { get; set; }
    public int TupletNormal { get; set; }
    public bool TupletStart { get; set; }
    public bool TupletStop { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public double Confidence { get; set; }
    /// <summary>Isolated note head without a stem, or a duration the recogniser was unsure about.</summary>
    public bool Uncertain { get; set; }
    public override string ToString() => IsRest ? $"rest {Type}" : $"{string.Join("+", Notes.Select(n => n.Pitch))} {Type}{new string('.', Dots)}";
}

public enum DirectionKind { Dynamics, WedgeStart, WedgeStop, Words }

public sealed class Direction
{
    public DirectionKind Kind { get; set; }
    public string Text { get; set; } = "";             // dynamics mark (p, mf ...) or words
    public bool Crescendo { get; set; }
    public int Staff { get; set; } = 1;
    public bool Above { get; set; }
    public int X { get; set; }
    /// <summary>Offset in divisions from the measure start (position of the nearest event).</summary>
    public int Offset { get; set; }
}
