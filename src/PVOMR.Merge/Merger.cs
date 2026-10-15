using PVOMR.Core.Score;

namespace PVOMR.Merge;

public enum RowStatus
{
    /// <summary>Every version is the same.</summary>
    Unanimous,
    /// <summary>Most versions agree (and add up) against the rest.</summary>
    Majority,
    /// <summary>The versions differ, but only one of them adds up.</summary>
    Fitting,
    /// <summary>Only one source has this bar.</summary>
    Single,
    /// <summary>No clear choice: the user decides (the best guess is used until then).</summary>
    Flagged,
}

/// <summary>A bar of the music with its version from every strand (null where a strand lacks it) and the choice.</summary>
public sealed class MergeRow
{
    public MergeBar?[] Candidates { get; init; } = Array.Empty<MergeBar?>();
    public int Chosen { get; set; } = -1;
    public RowStatus Status { get; set; }
    /// <summary>The choice was made by the user (kept over automatic choices).</summary>
    public bool UserChoice { get; set; }
    public MergeBar? ChosenBar => Chosen >= 0 ? Candidates[Chosen] : null;
}

/// <summary>
/// Merges several strands of the same music: the bars are aligned to a reference strand (the one with most bars that
/// add up), each row's best version is chosen (agreement with the others first, then adding up), and the chosen bars
/// are written out as one score.
/// </summary>
public static class Merger
{
    public static List<MergeRow> Align(IReadOnlyList<Strand> strands)
    {
        if (strands.Count == 0) return new();
        int refIdx = Enumerable.Range(0, strands.Count).OrderByDescending(i => strands[i].Bars.Count(b => b.Fits)).First();
        var reference = strands[refIdx];
        var rows = reference.Bars.Select(b => { var c = new MergeBar?[strands.Count]; c[refIdx] = b; return new MergeRow { Candidates = c }; }).ToList();
        // the row holding each reference bar (rows of other strands' extra bars get inserted between)
        var rowOfRef = Enumerable.Range(0, reference.Bars.Count).ToArray();
        for (int s = 0; s < strands.Count; s++)
        {
            if (s == refIdx) continue;
            int after = -1;
            foreach (var cr in Comparer.Compare(reference, strands[s]))
            {
                if (cr.A != null)
                {
                    after = rowOfRef[cr.A.Index];
                    if (cr.B != null) rows[after].Candidates[s] = cr.B;
                    continue;
                }
                // a bar only this strand has: a row of its own, after the last aligned one
                var c = new MergeBar?[strands.Count]; c[s] = cr.B;
                rows.Insert(after + 1, new MergeRow { Candidates = c });
                for (int k = 0; k < rowOfRef.Length; k++) if (rowOfRef[k] > after) rowOfRef[k]++;
                after++;
            }
        }
        return rows;
    }

    /// <summary>Chooses each row's version (rows chosen by the user stay as they are).</summary>
    public static void Choose(List<MergeRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.UserChoice) continue;
            var cands = Enumerable.Range(0, row.Candidates.Length).Where(i => row.Candidates[i] != null).ToList();
            if (cands.Count == 0) continue;
            if (cands.Count == 1) { row.Chosen = cands[0]; row.Status = RowStatus.Single; continue; }
            int Agree(int i) => cands.Count(j => j != i && row.Candidates[j]!.Key == row.Candidates[i]!.Key);
            double Score(int i)
            {
                var b = row.Candidates[i]!;
                double sim = cands.Where(j => j != i).Sum(j => Comparer.Similarity(b, row.Candidates[j]!));
                return 2.0 * Agree(i) + (b.Fits ? 1.5 : 0) + 0.5 * sim;
            }
            int best = cands.OrderByDescending(Score).ThenBy(i => i).First();
            row.Chosen = best;
            var bb = row.Candidates[best]!;
            int agree = Agree(best);
            if (agree == cands.Count - 1) row.Status = RowStatus.Unanimous;
            else if (agree >= 1 && 2 * (agree + 1) > cands.Count && bb.Fits) row.Status = RowStatus.Majority;
            else if (bb.Fits && cands.Count(i => row.Candidates[i]!.Fits && row.Candidates[i]!.Key != bb.Key) == 0) row.Status = RowStatus.Fitting;
            else row.Status = RowStatus.Flagged;
        }
    }

    /// <summary>The merged music as a score with one part: the chosen bars, with key, time and clef stated where they change.</summary>
    public static Score BuildScore(List<MergeRow> rows, IReadOnlyList<Strand> strands, string? title = null)
    {
        var score = new Score { Title = title };
        int staves = strands.Select(s => s.StaffCount).DefaultIfEmpty(1).Min();
        var part = new Part { Id = "P1", Name = strands.Count > 0 ? strands[0].PartName : "Part 1", StaffCount = staves };
        int? key = null; TimeSpec? time = null; var clefs = new ClefSpec?[2];
        int number = 0;
        foreach (var row in rows)
        {
            var bar = row.ChosenBar;
            if (bar?.SourceMeasure is not ScoreMeasure src) continue;
            var strand = strands[row.Chosen];
            var m = new ScoreMeasure
            {
                Number = bar.Pickup && number == 0 ? 0 : ++number, Implicit = bar.Pickup,
                LeftBar = src.LeftBar, RightBar = src.RightBar, RepeatForward = src.RepeatForward, RepeatBackward = src.RepeatBackward,
            };
            if (key != bar.KeyFifths) { m.KeyFifths = bar.KeyFifths; key = bar.KeyFifths; }
            if (bar.TimeSpec != null && bar.TimeSpec != time) { m.Time = bar.TimeSpec; time = bar.TimeSpec; }
            for (int c = 0; c < staves; c++)
                if (bar.Clefs[c] != null && bar.Clefs[c] != clefs[c]) { m.Clefs[c] = bar.Clefs[c]; clefs[c] = bar.Clefs[c]; }
            foreach (var e in src.Events.Where(e => strand.Staff == null || e.Staff == strand.Staff))
                m.Events.Add(strand.Staff == null ? e : Restaffed(e));
            foreach (var d in src.Directions.Where(d => strand.Staff == null || d.Staff == strand.Staff)) m.Directions.Add(d);
            part.Measures.Add(m);
        }
        score.Parts.Add(part);
        return score;
    }

    /// <summary>A copy of the event on staff 1 (a staff taken out of a part becomes a part of its own).</summary>
    private static ScoreEvent Restaffed(ScoreEvent e)
    {
        var c = new ScoreEvent
        {
            IsRest = e.IsRest, MeasureRest = e.MeasureRest, Voice = e.Voice, Staff = 1, GroupId = e.GroupId, Duration = e.Duration, Type = e.Type,
            Dots = e.Dots, StemUp = e.StemUp, Beams = e.Beams, Fermata = e.Fermata, TremoloMarks = e.TremoloMarks, TremoloType = e.TremoloType,
            Grace = e.Grace, GraceSlash = e.GraceSlash, TupletActual = e.TupletActual, TupletNormal = e.TupletNormal,
            TupletStart = e.TupletStart, TupletStop = e.TupletStop, X = e.X, Y = e.Y, Confidence = e.Confidence,
        };
        c.Notes.AddRange(e.Notes);
        c.Articulations.AddRange(e.Articulations);
        c.SlurStarts.AddRange(e.SlurStarts);
        c.SlurStops.AddRange(e.SlurStops);
        return c;
    }
}
