using System.Text;
using PVOMR.Core.Score;

namespace PVOMR.Core.Eval;

/// <summary>Compares a recognised score with the ground truth and produces precision/recall style metrics.</summary>
public sealed class ComparisonResult
{
    public string Name { get; set; } = "";
    public int TruthMeasures, RecognisedMeasures;
    public int TruthNotes, RecognisedNotes;           // note heads (chord members count individually)
    public int MatchedPitch;                           // aligned events with identical pitch set
    public int MatchedPitchAndDuration;                // ... and identical type+dots
    public int TruthRests, RecognisedRests, MatchedRests;
    public int MatchedDurationOnly;                    // aligned, same duration but different pitch
    public int TruthGraces, RecognisedGraces, MatchedGraces;   // grace notes, compared per measure by pitch
    public bool ClefCorrect, KeyCorrect, TimeCorrect;
    public int MeasuresExactlyRight;                   // all events of the measure aligned with pitch+duration
    public List<string> Details { get; } = new();

    public double NotePrecision => RecognisedNotes == 0 ? 0 : MatchedPitch / (double)RecognisedNotes;
    public double NoteRecall => TruthNotes == 0 ? 0 : MatchedPitch / (double)TruthNotes;
    public double NoteF1 => NotePrecision + NoteRecall == 0 ? 0 : 2 * NotePrecision * NoteRecall / (NotePrecision + NoteRecall);
    public double DurationAccuracy => MatchedPitch == 0 ? 0 : MatchedPitchAndDuration / (double)MatchedPitch;
    public double RestRecall => TruthRests == 0 ? 1 : MatchedRests / (double)TruthRests;
    public double RestPrecision => RecognisedRests == 0 ? (TruthRests == 0 ? 1 : 0) : MatchedRests / (double)RecognisedRests;
    public double MeasureAccuracy => TruthMeasures == 0 ? 0 : MeasuresExactlyRight / (double)TruthMeasures;

    public void Add(ComparisonResult o)
    {
        TruthMeasures += o.TruthMeasures; RecognisedMeasures += o.RecognisedMeasures; TruthNotes += o.TruthNotes; RecognisedNotes += o.RecognisedNotes;
        MatchedPitch += o.MatchedPitch; MatchedPitchAndDuration += o.MatchedPitchAndDuration; TruthRests += o.TruthRests; RecognisedRests += o.RecognisedRests;
        MatchedRests += o.MatchedRests; MatchedDurationOnly += o.MatchedDurationOnly;
        TruthGraces += o.TruthGraces; RecognisedGraces += o.RecognisedGraces; MatchedGraces += o.MatchedGraces; MeasuresExactlyRight += o.MeasuresExactlyRight;
    }

    public string Summary() =>
        $"measures {RecognisedMeasures}/{TruthMeasures} ({MeasureAccuracy:P0} exact), notes P {NotePrecision:P1} R {NoteRecall:P1} F1 {NoteF1:P1}, duration {DurationAccuracy:P1} of matched, rests P {RestPrecision:P0} R {RestRecall:P0}";
}

public static class ScoreComparer
{
    private sealed class Ev
    {
        public ScoreEvent E = null!;
        public string PitchKey = "";     // sorted pitch names, "R" for rests
        public string DurKey = "";
        public int Position;
    }

    public static ComparisonResult Compare(Score.Score truth, Score.Score recognised, string name)
    {
        var r = new ComparisonResult { Name = name };
        int partCount = Math.Max(truth.Parts.Count, recognised.Parts.Count);
        for (int pi = 0; pi < truth.Parts.Count; pi++)
        {
            var tp = truth.Parts[pi];
            var rp = pi < recognised.Parts.Count ? recognised.Parts[pi] : null;
            r.TruthMeasures += tp.Measures.Count;
            if (rp == null) { r.TruthNotes += tp.Measures.Sum(m => m.Events.Where(e => !e.IsRest).Sum(e => e.Notes.Count)); r.TruthRests += tp.Measures.Sum(m => m.Events.Count(e => e.IsRest)); continue; }
            r.RecognisedMeasures += rp.Measures.Count;
            // attributes of the first measure
            if (pi == 0)
            {
                var tm0 = tp.Measures.FirstOrDefault(); var rm0 = rp.Measures.FirstOrDefault();
                var tc = tm0?.Clefs[0] ?? ClefSpec.Treble; var rc = rm0?.Clefs[0] ?? ClefSpec.Treble;
                r.ClefCorrect = tc.Sign == rc.Sign && tc.Line == rc.Line;
                r.KeyCorrect = (tm0?.KeyFifths ?? 0) == (rm0?.KeyFifths ?? 0);
                var tt = tm0?.Time; var rt = rm0?.Time;
                r.TimeCorrect = tt != null && rt != null && tt.Beats == rt.Beats && tt.BeatType == rt.BeatType;
            }
            // align measure sequences (identity when counts agree, otherwise by similarity)
            var pairs = AlignMeasures(tp.Measures, rp.Measures);
            var pairedRecognised = new HashSet<ScoreMeasure>();
            foreach (var (tm, rm) in pairs)
            {
                var tev = Flatten(tm); var rev = rm != null ? Flatten(rm) : new List<Ev>();
                if (rm != null) pairedRecognised.Add(rm);
                // grace notes take no time: compared separately, as a multiset of pitches per measure
                var tg = tm.Events.Where(e => e.Grace).SelectMany(e => e.Notes.Select(x => x.Pitch.ToString())).ToList();
                var rg = rm?.Events.Where(e => e.Grace).SelectMany(e => e.Notes.Select(x => x.Pitch.ToString())).ToList() ?? new List<string>();
                r.TruthGraces += tg.Count; r.RecognisedGraces += rg.Count;
                foreach (var p in tg) { int k = rg.IndexOf(p); if (k >= 0) { r.MatchedGraces++; rg.RemoveAt(k); } }
                CountMeasure(r, tev, rev, tm.Number);
            }
            foreach (var rm in rp.Measures.Where(m => !pairedRecognised.Contains(m)))
            {
                var rev = Flatten(rm);
                r.RecognisedNotes += rev.Where(e => !e.E.IsRest).Sum(e => e.E.Notes.Count);
                r.RecognisedRests += rev.Count(e => e.E.IsRest);
            }
        }
        for (int pi = truth.Parts.Count; pi < recognised.Parts.Count; pi++)
        {
            r.RecognisedMeasures += recognised.Parts[pi].Measures.Count;
            r.RecognisedNotes += recognised.Parts[pi].Measures.Sum(m => m.Events.Where(e => !e.IsRest).Sum(e => e.Notes.Count));
        }
        return r;
    }

    private static List<Ev> Flatten(ScoreMeasure m)
    {
        var list = new List<Ev>();
        foreach (var voice in m.Events.Where(e => !e.Grace).GroupBy(e => e.Voice).OrderBy(g => g.Key))
        {
            int pos = 0;
            foreach (var e in voice.OrderBy(e => e.X))
            {
                list.Add(new Ev { E = e, PitchKey = e.IsRest ? "R" : string.Join("+", e.Notes.Select(n => n.Pitch.ToString()).OrderBy(x => x)), DurKey = $"{e.Type}{e.Dots}", Position = pos });
                pos += e.Duration;
            }
        }
        return list;
    }

    private static void CountMeasure(ComparisonResult r, List<Ev> t, List<Ev> rec, int number)
    {
        int truthNotes = t.Where(e => !e.E.IsRest).Sum(e => e.E.Notes.Count);
        int recNotes = rec.Where(e => !e.E.IsRest).Sum(e => e.E.Notes.Count);
        r.TruthNotes += truthNotes; r.RecognisedNotes += recNotes;
        r.TruthRests += t.Count(e => e.E.IsRest); r.RecognisedRests += rec.Count(e => e.E.IsRest);
        if (rec.Count == 0) { r.Details.Add($"m{number}: missing ({t.Count} events in truth)"); return; }
        // sequence alignment on events (Needleman-Wunsch): match scores reward pitch and duration agreement
        int n = t.Count, m = rec.Count;
        var dp = new double[n + 1, m + 1];
        for (int i = 1; i <= n; i++) dp[i, 0] = -i;
        for (int j = 1; j <= m; j++) dp[0, j] = -j;
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
            {
                double s = Similarity(t[i - 1], rec[j - 1]);
                dp[i, j] = Math.Max(Math.Max(dp[i - 1, j] - 1, dp[i, j - 1] - 1), dp[i - 1, j - 1] + s);
            }
        // traceback
        int a = n, b = m; bool perfect = n == m;
        var mismatches = new List<string>();
        while (a > 0 && b > 0)
        {
            double s = Similarity(t[a - 1], rec[b - 1]);
            if (Math.Abs(dp[a, b] - (dp[a - 1, b - 1] + s)) < 1e-9)
            {
                var te = t[a - 1]; var re = rec[b - 1];
                bool pitch = te.PitchKey == re.PitchKey;
                bool dur = te.E.Duration == re.E.Duration;   // strict: a triplet 8th is not an 8th
                if (te.E.IsRest && re.E.IsRest) { if (dur) r.MatchedRests++; else perfect = false; }
                else if (!te.E.IsRest && !re.E.IsRest)
                {
                    // chord members: count shared pitches
                    var shared = te.E.Notes.Select(x => x.Pitch.ToString()).Intersect(re.E.Notes.Select(x => x.Pitch.ToString())).Count();
                    r.MatchedPitch += shared;
                    if (dur) r.MatchedPitchAndDuration += shared; else { if (shared > 0) mismatches.Add($"{te.PitchKey} {te.DurKey}->{re.DurKey}"); }
                    if (shared == 0 && dur) r.MatchedDurationOnly++;
                    if (shared < te.E.Notes.Count || shared < re.E.Notes.Count || !dur) perfect = false;
                    if (shared == 0) mismatches.Add($"{te.PitchKey}->{re.PitchKey}");
                }
                else perfect = false;
                a--; b--;
            }
            else if (Math.Abs(dp[a, b] - (dp[a - 1, b] - 1)) < 1e-9) { mismatches.Add($"missing {t[a - 1].PitchKey}"); perfect = false; a--; }
            else { mismatches.Add($"extra {rec[b - 1].PitchKey}"); perfect = false; b--; }
        }
        if (a > 0 || b > 0) perfect = false;
        if (perfect) r.MeasuresExactlyRight++;
        else if (mismatches.Count > 0) r.Details.Add($"m{number}: {string.Join(", ", mismatches.Take(6))}{(mismatches.Count > 6 ? " ..." : "")}");
    }

    private static double Similarity(Ev a, Ev b)
    {
        if (a.E.IsRest != b.E.IsRest) return -0.5;
        bool dur = a.E.Duration == b.E.Duration;
        if (a.E.IsRest) return dur ? 1 : 0.5;
        var shared = a.E.Notes.Select(x => x.Pitch.ToString()).Intersect(b.E.Notes.Select(x => x.Pitch.ToString())).Count();
        double pitch = shared / (double)Math.Max(a.E.Notes.Count, b.E.Notes.Count);
        return pitch * 0.7 + (dur ? 0.3 : 0) + (pitch == 0 && !dur ? -0.3 : 0);
    }

    private static List<(ScoreMeasure t, ScoreMeasure? r)> AlignMeasures(List<ScoreMeasure> truth, List<ScoreMeasure> rec)
    {
        var pairs = new List<(ScoreMeasure, ScoreMeasure?)>();
        if (truth.Count == rec.Count)
        {
            for (int i = 0; i < truth.Count; i++) pairs.Add((truth[i], rec[i]));
            return pairs;
        }
        // Needleman-Wunsch on measures with a cheap similarity (note count and pitch overlap)
        int n = truth.Count, m = rec.Count;
        var dp = new double[n + 1, m + 1];
        for (int i = 1; i <= n; i++) dp[i, 0] = -i * 0.5;
        for (int j = 1; j <= m; j++) dp[0, j] = -j * 0.5;
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
                dp[i, j] = Math.Max(Math.Max(dp[i - 1, j] - 0.5, dp[i, j - 1] - 0.5), dp[i - 1, j - 1] + MeasureSimilarity(truth[i - 1], rec[j - 1]));
        int a = n, b = m;
        var rev = new List<(ScoreMeasure, ScoreMeasure?)>();
        while (a > 0 || b > 0)
        {
            if (a > 0 && b > 0 && Math.Abs(dp[a, b] - (dp[a - 1, b - 1] + MeasureSimilarity(truth[a - 1], rec[b - 1]))) < 1e-9) { rev.Add((truth[a - 1], rec[b - 1])); a--; b--; }
            else if (a > 0 && (b == 0 || Math.Abs(dp[a, b] - (dp[a - 1, b] - 0.5)) < 1e-9)) { rev.Add((truth[a - 1], null)); a--; }
            else b--;
        }
        rev.Reverse();
        return rev;
    }

    private static double MeasureSimilarity(ScoreMeasure t, ScoreMeasure r)
    {
        var tp = t.Events.Where(e => !e.IsRest).SelectMany(e => e.Notes.Select(n => n.Pitch.ToString())).ToList();
        var rp = r.Events.Where(e => !e.IsRest).SelectMany(e => e.Notes.Select(n => n.Pitch.ToString())).ToList();
        if (tp.Count == 0 && rp.Count == 0) return 0.5;
        int shared = tp.Intersect(rp).Count();
        return shared / (double)Math.Max(1, Math.Max(tp.Count, rp.Count));
    }

    public static string Table(IEnumerable<ComparisonResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{"piece",-28} {"meas",8} {"exact",6} {"noteP",7} {"noteR",7} {"dur",6} {"restP",6} {"restR",6} clef key time");
        var total = new ComparisonResult { Name = "TOTAL" };
        int clefOk = 0, keyOk = 0, timeOk = 0, n = 0;
        foreach (var r in results)
        {
            sb.AppendLine($"{Trunc(r.Name, 28),-28} {r.RecognisedMeasures + "/" + r.TruthMeasures,8} {r.MeasureAccuracy,6:P0} {r.NotePrecision,7:P1} {r.NoteRecall,7:P1} {r.DurationAccuracy,6:P0} {r.RestPrecision,6:P0} {r.RestRecall,6:P0} {(r.ClefCorrect ? "ok" : "X"),4} {(r.KeyCorrect ? "ok" : "X"),3} {(r.TimeCorrect ? "ok" : "X"),4}");
            total.Add(r); n++; if (r.ClefCorrect) clefOk++; if (r.KeyCorrect) keyOk++; if (r.TimeCorrect) timeOk++;
        }
        sb.AppendLine($"{"TOTAL",-28} {total.RecognisedMeasures + "/" + total.TruthMeasures,8} {total.MeasureAccuracy,6:P0} {total.NotePrecision,7:P1} {total.NoteRecall,7:P1} {total.DurationAccuracy,6:P0} {total.RestPrecision,6:P0} {total.RestRecall,6:P0} {clefOk}/{n} {keyOk}/{n} {timeOk}/{n}");
        if (total.TruthGraces + total.RecognisedGraces > 0)
            sb.AppendLine($"grace notes: {total.MatchedGraces} matched of {total.TruthGraces} in the truth, {total.RecognisedGraces} recognised (P {(total.RecognisedGraces == 0 ? 0 : total.MatchedGraces / (double)total.RecognisedGraces):P0}, R {(total.TruthGraces == 0 ? 0 : total.MatchedGraces / (double)total.TruthGraces):P0})");
        return sb.ToString();
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "~";
}
