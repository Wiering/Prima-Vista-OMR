using PVOMR.Core.Score;

namespace PVOMR.Merge;

/// <summary>
/// One line of music from one source, bar by bar: a part of a MusicXML file, or one staff of it (a part taken from a
/// full score, one player of a duet). Several strands of the same music are compared and merged.
/// </summary>
public sealed class Strand
{
    public string Source { get; init; } = "";
    public string Label { get; init; } = "";
    public string PartName { get; init; } = "";
    /// <summary>The staff taken from the part (null = all of it).</summary>
    public int? Staff { get; init; }
    /// <summary>Staves of the strand as written out: 1 for a single staff taken from a part.</summary>
    public int StaffCount { get; init; } = 1;
    public List<MergeBar> Bars { get; } = new();
    public override string ToString() => $"{Label} ({Bars.Count} bars)";
}

/// <summary>A bar of a strand, normalised for comparison: sounding pitches, durations in whole notes.</summary>
public sealed class MergeBar
{
    public int Index { get; init; }
    /// <summary>The bar number written in the source (its own numbering).</summary>
    public int Number { get; init; }
    public int Page { get; init; }
    public int Line { get; init; }
    public int KeyFifths { get; init; }
    public string? Time { get; init; }
    public TimeSpec? TimeSpec { get; init; }
    /// <summary>The clef in force per staff of the strand (one entry for a single staff).</summary>
    public ClefSpec?[] Clefs { get; init; } = new ClefSpec?[2];
    /// <summary>Length of the bar by its time signature, in whole notes (null without one).</summary>
    public Fraction? Expected { get; init; }
    public bool Pickup { get; init; }
    /// <summary>Events per voice, each in time order.</summary>
    public List<List<BarEvent>> Voices { get; } = new();
    /// <summary>Every voice fills the bar exactly (or the bar is a pickup).</summary>
    public bool Fits { get; init; }
    /// <summary>A compact, order-preserving fingerprint: equal for bars that are the same music.</summary>
    public string Key { get; init; } = "";
    /// <summary>The source's own measure (for links back to the page: page, x range).</summary>
    public ScoreMeasure? SourceMeasure { get; init; }
    public override string ToString() => $"bar {Number} (p{Page} line {Line}): {Key}";
    internal MergeBar WithVoices(IEnumerable<List<BarEvent>> voices) { Voices.AddRange(voices); return this; }
}

/// <summary>A note, chord or rest: sounding pitches (empty for a rest) and the length in whole notes.</summary>
public sealed record BarEvent(IReadOnlyList<PitchSpec> Pitches, Fraction Length, bool TieToNext)
{
    /// <summary>The events of the source this stands for (one; several rests counted as one rest).</summary>
    public IReadOnlyList<ScoreEvent> Sources { get; init; } = Array.Empty<ScoreEvent>();
    public bool IsRest => Pitches.Count == 0;
    public override string ToString() => (IsRest ? "r" : string.Join("+", Pitches)) + ":" + Length;
}

/// <summary>An exact rational number (note lengths in whole notes).</summary>
public readonly record struct Fraction(long Num, long Den) : IComparable<Fraction>
{
    public static Fraction Of(long num, long den)
    {
        if (den < 0) { num = -num; den = -den; }
        long g = Gcd(Math.Abs(num), den);
        return g == 0 ? new Fraction(0, 1) : new Fraction(num / g, den / g);
    }
    private static long Gcd(long a, long b) { while (b != 0) (a, b) = (b, a % b); return a; }
    public static Fraction operator +(Fraction a, Fraction b) => Of(a.Num * b.Den + b.Num * a.Den, a.Den * b.Den);
    public int CompareTo(Fraction o) => (Num * o.Den).CompareTo(o.Num * Den);
    public override string ToString() => Den == 1 ? $"{Num}" : $"{Num}/{Den}";
}
