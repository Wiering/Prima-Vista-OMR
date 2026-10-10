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
    public override string ToString() => $"{Beats}/{BeatType}";
}

public enum BarStyle { Regular, LightLight, LightHeavy, HeavyLight, Heavy, None }

public sealed class ScoreMeasure
{
    public int Number { get; set; }
    public bool Implicit { get; set; }                 // pickup / partial measure
    public ClefSpec?[] Clefs { get; set; } = new ClefSpec?[2];   // per staff, only when (re)stated in this measure
    public int? KeyFifths { get; set; }
    public TimeSpec? Time { get; set; }
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
    public int Duration { get; set; }                 // divisions
    public string Type { get; set; } = "quarter";     // MusicXML note type
    public int Dots { get; set; }
    public bool? StemUp { get; set; }
    public List<ScoreNote> Notes { get; } = new();    // chord when > 1
    public BeamState[] Beams { get; set; } = Array.Empty<BeamState>();   // index = beam level - 1
    public List<string> Articulations { get; } = new();
    public bool Fermata { get; set; }
    public List<int> SlurStarts { get; } = new();
    public List<int> SlurStops { get; } = new();
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
