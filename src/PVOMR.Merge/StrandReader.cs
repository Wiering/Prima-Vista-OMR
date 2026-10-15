using PVOMR.Core.Score;

namespace PVOMR.Merge;

/// <summary>Builds strands from scores: a whole part, or one staff of a part (a grand staff split into its hands).</summary>
public static class StrandReader
{
    public static Strand FromMusicXml(string path, int partIndex = 0, int? staff = null)
        => FromScore(MusicXmlReader.Read(path), path, partIndex, staff);

    public static Strand FromScore(Score score, string source, int partIndex = 0, int? staff = null)
    {
        if (partIndex < 0 || partIndex >= score.Parts.Count) throw new ArgumentOutOfRangeException(nameof(partIndex), $"the score has {score.Parts.Count} part(s)");
        var part = score.Parts[partIndex];
        var strand = new Strand { Source = source, Label = $"{Path.GetFileNameWithoutExtension(source)} / {part.Name}{(staff is int s ? $" staff {s}" : "")}",
                                  PartName = part.Name, Staff = staff, StaffCount = staff == null ? Math.Max(1, part.StaffCount) : 1 };
        int key = 0, page = 1, line = 1;
        var clefs = new ClefSpec?[2];
        TimeSpec? time = null;
        foreach (var m in part.Measures)
        {
            if (m.NewPage) { page++; line = 1; }
            else if (m.NewSystem) line++;
            if (m.KeyFifths is int k) key = k;
            if (m.Time != null) time = m.Time;
            for (int c = 0; c < 2; c++) if (m.Clefs[c] != null) clefs[c] = m.Clefs[c];
            var events = m.Events.Where(e => !e.Grace && (staff == null || e.Staff == staff)).ToList();
            var voices = events.GroupBy(e => e.Voice).OrderBy(g => g.Key)
                               .Select(g => g.OrderBy(e => e.X).Select(ToEvent).ToList()).Where(v => v.Count > 0).ToList();
            Fraction? expected = time == null ? null : Fraction.Of(time.Length, 4L * Score.Divisions);
            bool fits = expected is Fraction len && voices.Count > 0
                        && (m.Implicit || voices.All(v => v.Aggregate(Fraction.Of(0, 1), (a, e) => a + e.Length).CompareTo(len) == 0)
                            || voices.Count == 1 && voices[0].Count == 1 && voices[0][0].IsRest);
            var bar = new MergeBar
            {
                Index = strand.Bars.Count, Number = m.Number, Page = page, Line = line, KeyFifths = key, Time = time?.ToString(), TimeSpec = time,
                Clefs = staff is int st ? new[] { clefs[Math.Clamp(st - 1, 0, 1)], null } : (ClefSpec?[])clefs.Clone(),
                Expected = expected, Pickup = m.Implicit, Fits = fits, SourceMeasure = m,
                Key = string.Join(" | ", voices.Select(v => string.Join(" ", v))),
            };
            bar.Voices.AddRange(voices);
            strand.Bars.Add(bar);
        }
        return strand;
    }

    private static BarEvent ToEvent(ScoreEvent e)
    {
        var len = Fraction.Of(e.Duration, 4L * Score.Divisions);
        // a whole-bar rest is a rest of the bar, whatever its written length
        var pitches = e.IsRest ? Array.Empty<PitchSpec>() : e.Notes.Select(n => n.Pitch).OrderBy(p => Midi(p)).ToArray();
        return new BarEvent(pitches, len, e.Notes.Any(n => n.TieStart));
    }

    private static readonly Dictionary<string, int> StepBase = new() { ["C"] = 0, ["D"] = 2, ["E"] = 4, ["F"] = 5, ["G"] = 7, ["A"] = 9, ["B"] = 11 };

    /// <summary>The sounding pitch as a MIDI number (enharmonic spellings coincide).</summary>
    public static int Midi(PitchSpec p) => 12 * (p.Octave + 1) + StepBase.GetValueOrDefault(p.Step, 0) + p.Alter;

    /// <summary>The diatonic position (step and octave, ignoring alterations).</summary>
    public static int Diatonic(PitchSpec p) => 7 * p.Octave + "CDEFGAB".IndexOf(p.Step[0]);
}
