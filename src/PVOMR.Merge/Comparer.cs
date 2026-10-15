using PVOMR.Core.Score;

namespace PVOMR.Merge;

public enum DiffKind { Pitch, Accidental, Duration, ExtraNote, MissingNote, NoteRest, Key, Time, MissingBar, ExtraBar }

/// <summary>One difference between two versions of a bar (A and B).</summary>
public sealed record BarDiff(DiffKind Kind, string Text, int StepOffset = 0)
{
    /// <summary>The events concerned in A and in B (null where the note is missing on that side).</summary>
    public BarEvent? EventA { get; init; }
    public BarEvent? EventB { get; init; }
}

/// <summary>A bar of the music as found in two strands (either may be missing) and how they differ.</summary>
public sealed class ComparisonRow
{
    public MergeBar? A { get; init; }
    public MergeBar? B { get; init; }
    public List<BarDiff> Diffs { get; } = new();
    public bool Same => A != null && B != null && Diffs.Count == 0;
}

/// <summary>
/// Compares two strands of the same music: the bars are aligned (dynamic programming over a bar similarity, the bar
/// numbers as a hint) and each pair of aligned bars is compared note by note. Pitches are compared as sounding pitches,
/// so an accidental already in the key or a reminder accidental makes no difference.
/// </summary>
public static class Comparer
{
    public static List<ComparisonRow> Compare(Strand a, Strand b)
    {
        int n = a.Bars.Count, m = b.Bars.Count;
        const double Gap = -0.6;
        var score = new double[n + 1, m + 1];
        var move = new byte[n + 1, m + 1];   // 0 diag, 1 up (bar only in A), 2 left (bar only in B)
        for (int i = 1; i <= n; i++) { score[i, 0] = i * Gap; move[i, 0] = 1; }
        for (int j = 1; j <= m; j++) { score[0, j] = j * Gap; move[0, j] = 2; }
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
            {
                var ba = a.Bars[i - 1]; var bb = b.Bars[j - 1];
                double sim = Similarity(ba, bb) + (ba.Number == bb.Number ? 0.15 : 0);
                double d = score[i - 1, j - 1] + 2 * sim - 0.5, u = score[i - 1, j] + Gap, l = score[i, j - 1] + Gap;
                if (d >= u && d >= l) { score[i, j] = d; move[i, j] = 0; }
                else if (u >= l) { score[i, j] = u; move[i, j] = 1; }
                else { score[i, j] = l; move[i, j] = 2; }
            }
        var rows = new List<ComparisonRow>();
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            byte mv = move[i, j];
            if (mv == 0) { rows.Add(Row(a.Bars[i - 1], b.Bars[j - 1])); i--; j--; }
            else if (mv == 1) { var r = new ComparisonRow { A = a.Bars[i - 1] }; r.Diffs.Add(new BarDiff(DiffKind.ExtraBar, "bar only in A")); rows.Add(r); i--; }
            else { var r = new ComparisonRow { B = b.Bars[j - 1] }; r.Diffs.Add(new BarDiff(DiffKind.MissingBar, "bar only in B")); rows.Add(r); j--; }
        }
        rows.Reverse();
        return rows;
    }

    private static List<BarEvent> Flat(MergeBar b) => b.Voices.SelectMany(v => v).ToList();

    /// <summary>0..1: how much of the two bars' events match (same pitches and length 1, one of the two 0.6).</summary>
    public static double Similarity(MergeBar a, MergeBar b)
    {
        if (a.Key == b.Key) return 1;
        var ea = Flat(a); var eb = Flat(b);
        if (ea.Count + eb.Count == 0) return 1;
        var (matched, _) = AlignEvents(ea, eb);
        return 2 * matched / (ea.Count + eb.Count);
    }

    private static double EventSim(BarEvent x, BarEvent y)
    {
        bool samePitch = x.Pitches.Select(StrandReader.Midi).SequenceEqual(y.Pitches.Select(StrandReader.Midi));
        bool sameLen = x.Length.CompareTo(y.Length) == 0;
        if (samePitch && sameLen) return 1;
        if (x.IsRest != y.IsRest) return sameLen ? 0.3 : 0;
        if (sameLen) return x.Pitches.Count == y.Pitches.Count && !x.IsRest ? 0.6 : 0.4;
        return samePitch ? 0.6 : 0.2;
    }

    /// <summary>Best monotone matching of two event sequences: total similarity and the matched index pairs (-1 = none).</summary>
    private static (double total, List<(int i, int j)> pairs) AlignEvents(List<BarEvent> ea, List<BarEvent> eb)
    {
        int n = ea.Count, m = eb.Count;
        var s = new double[n + 1, m + 1];
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
                s[i, j] = Math.Max(Math.Max(s[i - 1, j], s[i, j - 1]), s[i - 1, j - 1] + EventSim(ea[i - 1], eb[j - 1]));
        var pairs = new List<(int, int)>();
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            if (i > 0 && j > 0 && Math.Abs(s[i, j] - (s[i - 1, j - 1] + EventSim(ea[i - 1], eb[j - 1]))) < 1e-9 && EventSim(ea[i - 1], eb[j - 1]) > 0) { pairs.Add((i - 1, j - 1)); i--; j--; }
            else if (i > 0 && (j == 0 || Math.Abs(s[i, j] - s[i - 1, j]) < 1e-9)) { pairs.Add((i - 1, -1)); i--; }
            else { pairs.Add((-1, j - 1)); j--; }
        }
        pairs.Reverse();
        return (s[n, m], pairs);
    }

    /// <summary>The differences between two versions of a bar.</summary>
    public static List<BarDiff> Diff(MergeBar a, MergeBar b) => Row(a, b).Diffs;

    private static ComparisonRow Row(MergeBar a, MergeBar b)
    {
        var row = new ComparisonRow { A = a, B = b };
        if (a.KeyFifths != b.KeyFifths) row.Diffs.Add(new BarDiff(DiffKind.Key, $"key {a.KeyFifths} vs {b.KeyFifths}"));
        if (a.Time != b.Time) row.Diffs.Add(new BarDiff(DiffKind.Time, $"time {a.Time ?? "?"} vs {b.Time ?? "?"}"));
        if (a.Key == b.Key) return row;
        var ea = Flat(a); var eb = Flat(b);
        var (_, pairs) = AlignEvents(ea, eb);
        int pos = 0;
        foreach (var (i, j) in pairs)
        {
            pos++;
            if (j < 0) { row.Diffs.Add(new BarDiff(DiffKind.ExtraNote, $"note {pos}: {ea[i]} only in A") { EventA = ea[i] }); continue; }
            if (i < 0) { row.Diffs.Add(new BarDiff(DiffKind.MissingNote, $"note {pos}: {eb[j]} only in B") { EventB = eb[j] }); continue; }
            BarEvent x = ea[i], y = eb[j];
            if (x.IsRest != y.IsRest) { row.Diffs.Add(new BarDiff(DiffKind.NoteRest, $"note {pos}: {x} vs {y}") { EventA = x, EventB = y }); continue; }
            if (x.Length.CompareTo(y.Length) != 0) row.Diffs.Add(new BarDiff(DiffKind.Duration, $"note {pos}: {x} vs {y}") { EventA = x, EventB = y });
            if (x.IsRest) continue;
            var px = x.Pitches; var py = y.Pitches;
            if (px.Count != py.Count) { row.Diffs.Add(new BarDiff(DiffKind.Pitch, $"note {pos}: {x} vs {y}") { EventA = x, EventB = y }); continue; }
            for (int k = 0; k < px.Count; k++)
            {
                if (StrandReader.Midi(px[k]) == StrandReader.Midi(py[k])) continue;
                int steps = StrandReader.Diatonic(py[k]) - StrandReader.Diatonic(px[k]);
                row.Diffs.Add(steps == 0 ? new BarDiff(DiffKind.Accidental, $"note {pos}: {px[k]} vs {py[k]}") { EventA = x, EventB = y }
                                         : new BarDiff(DiffKind.Pitch, $"note {pos}: {px[k]} vs {py[k]}", steps) { EventA = x, EventB = y });
            }
        }
        return row;
    }

    /// <summary>A run of consecutive differing rows whose pitches are all off by the same number of steps: one cause
    /// (a clef read wrongly), reported once.</summary>
    public static List<(int from, int to, int steps)> ShiftedRuns(List<ComparisonRow> rows)
    {
        var runs = new List<(int, int, int)>();
        int start = -1, steps = 0;
        for (int r = 0; r <= rows.Count; r++)
        {
            int s = r < rows.Count ? CommonShift(rows[r]) : 0;
            if (s != 0 && s == steps && start >= 0) continue;
            if (start >= 0 && r - start >= 2) runs.Add((start, r - 1, steps));
            start = s != 0 ? r : -1; steps = s;
        }
        return runs;
    }

    private static int CommonShift(ComparisonRow row)
    {
        var p = row.Diffs.Where(d => d.Kind == DiffKind.Pitch && d.StepOffset != 0).ToList();
        if (p.Count < 2 || p.Count < row.Diffs.Count(d => d.Kind is DiffKind.Pitch or DiffKind.Accidental)) return 0;
        return p.All(d => d.StepOffset == p[0].StepOffset) ? p[0].StepOffset : 0;
    }
}
