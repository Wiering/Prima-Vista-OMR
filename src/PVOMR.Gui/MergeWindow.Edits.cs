using PVOMR.Core.Analysis;
using PVOMR.Core.Model;
using PVOMR.Core.Score;
using PVOMR.Merge;
using Rect = PVOMR.Core.Model.Rect;

namespace PVOMR.Gui;

/// <summary>A version of the music as loaded for merging: its strand, and for a PDF the recognised pages (kept light: the
/// page image and the recognition) and the page measures of every bar.</summary>
public sealed class MergeData
{
    public string File = "";
    public Strand Strand = null!;
    public Dictionary<PageAnalysis, PageResult> Pages = new();
    public Dictionary<ScoreMeasure, List<Measure>> Measures = new();
    public Dictionary<int, PageResult> ByNumber = new();

    public static async Task<MergeData> LoadAsync(Func<RecognitionEngine> engine, MergeSource src, double dpi, bool restsByLength, Action<string> status)
    {
        var data = new MergeData { File = src.File };
        Score score = src.File.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? await data.RecogniseAsync(engine, dpi, status) : MusicXmlReader.Read(src.File);
        data.Strand = StrandReader.FromScore(score, src.File, src.Part - 1, src.Staff == 0 ? null : src.Staff, restsByLength);
        return data;
    }

    /// <summary>Recognises every page of the PDF (with its stored corrections) and assembles the score.</summary>
    private async Task<Score> RecogniseAsync(Func<RecognitionEngine> engine, double dpi, Action<string> status)
    {
        string file = File;
        int count = RecognitionEngine.PageCount(file);
        var store = CorrectionStore.Load(file);
        var analyses = new List<PageAnalysis>();
        for (int p = 1; p <= count; p++)
        {
            status($"{Path.GetFileName(file)}: recognising page {p} of {count}…");
            ByNumber.TryGetValue(PageOrder.Previous(file, p), out var previous);
            int page = p;
            var result = await Task.Run(() => engine().Analyze(file, page, dpi, previous));
            if (store.ApplyAll(result) > 0) RecognitionEngine.Reinterpret(result, previous);
            var light = new PageResult { PageNumber = p, Analysis = result.Analysis, Context = new AnalysisContext { Gray = result.Context.Gray } };
            foreach (var (sym, origin) in result.Origins) light.Origins[sym] = origin;
            ByNumber[p] = light;
            Pages[result.Analysis] = light;
            analyses.Add(result.Analysis);
        }
        var map = new Dictionary<Measure, ScoreMeasure>();
        var score = MusicAssembler.Build(analyses, new MusicAssembler.Options { Title = Path.GetFileNameWithoutExtension(file), MeasureMap = map });
        foreach (var (measure, sm) in map)
        {
            if (!Measures.TryGetValue(sm, out var list)) Measures[sm] = list = new();
            list.Add(measure);
        }
        return score;
    }
}

/// <summary>
/// The merge choices as corrections of version A (the score open in the main window): in every reviewed bar where another
/// version was chosen, A's notes are changed into the chosen ones: lengths set, heads moved, notes deleted or added, and
/// accidentals set where a head does not sound as chosen after re-interpretation. What cannot be expressed as an edit
/// (a key, a missing bar, a chord head to add) is reported.
/// </summary>
public static class MergeEdits
{
    private sealed class EditRun
    {
        public MergeData A = null!;
        public List<(int page, SymbolEdit edit)> Edits = new();
        public List<string> Skipped = new();
        /// <summary>Heads that must sound as given (checked after re-interpretation).</summary>
        public List<(PageResult page, MusicSymbol sym, Note note, PitchSpec target, int fifths)> Heads = new();
    }

    /// <summary>The edits (applied to A's pages as they are made) and a summary of what was and was not done.</summary>
    public static (List<(int page, SymbolEdit edit)> edits, string summary) Make(IReadOnlyList<MergeData> sources, List<MergeRow> rows, ISet<int> reviewed)
    {
        var run = new EditRun { A = sources[0] };
        int bars = 0;
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            if (!reviewed.Contains(r) || row.Chosen <= 0 || row.ChosenBar is not MergeBar c) continue;
            if (row.Candidates[0] is not MergeBar a) { run.Skipped.Add($"bar {c.Number}: missing in A"); continue; }
            if (a.Key == c.Key) continue;
            bars++;
            try { EditBar(run, a, c, sources[row.Chosen]); }
            catch (Exception ex) { run.Skipped.Add($"bar {a.Number}: {ex.Message}"); }
        }
        ReinterpretAll(run.A);
        // accidentals: a head that does not sound as chosen gets one (first by dropping an accidental the key makes
        // superfluous, then by printing the chosen one)
        for (int pass = 0; pass < 2; pass++)
        {
            bool any = false;
            foreach (var h in run.Heads)
            {
                if (h.note.Pitch == h.target.ToString()) continue;
                bool drop = pass == 0 && h.note.Accidental != int.MinValue && h.target.Alter == KeyAlter(h.target.Step, h.fifths);
                var e = new SymbolEdit { Op = "accidental", Head = CorrectionStore.Heads(h.sym).IndexOf(h.note), Alter = drop ? null : h.target.Alter };
                any |= Record(run, h.page, h.sym, e);
            }
            if (!any) break;
            ReinterpretAll(run.A);
        }
        foreach (var h in run.Heads.Where(h => h.note.Pitch != h.target.ToString()))
            run.Skipped.Add($"a head reads {h.note.Pitch} instead of {h.target}");
        string summary = $"merge: {run.Edits.Count} edit(s) in {bars} bar(s) taken from the other versions"
                         + (run.Skipped.Count > 0 ? $"; not done: {string.Join("; ", run.Skipped.Take(8))}{(run.Skipped.Count > 8 ? " …" : "")}" : "");
        return (run.Edits, summary);
    }

    private static void EditBar(EditRun run, MergeBar a, MergeBar c, MergeData chosen)
    {
        if (a.SourceMeasure is not ScoreMeasure sm || !run.A.Measures.TryGetValue(sm, out var ms)) { run.Skipped.Add($"bar {a.Number}: not on a page of A"); return; }
        var diffs = Comparer.Diff(a, c);
        foreach (var d in diffs.Where(d => d.Kind is DiffKind.Key or DiffKind.Time))
            run.Skipped.Add($"bar {a.Number}: {d.Text} (set it on the staff)");
        var pairs = diffs.Where(d => d.Kind is not (DiffKind.Key or DiffKind.Time)).Select(d => (x: d.EventA, y: d.EventB)).Distinct().ToList();
        foreach (var (x, y) in pairs)
        {
            if (y == null) { foreach (var se in x!.Sources) Delete(run, ms, se, a); continue; }
            if (x == null) { foreach (var te in y.Sources) Add(run, a, ms, te, PlaceX(run.A, ms, te, chosen, c)); continue; }
            if (x.IsRest != y.IsRest || x.IsRest && x.Sources.Count != y.Sources.Count)
            {
                // replaced: the new events where the old ones stood
                var xs = x.Sources.Select(se => SymbolOf(run.A, ms, se)?.sym.Bounds.CenterX).Where(v => v != null).Select(v => v!.Value).ToList();
                foreach (var se in x.Sources) Delete(run, ms, se, a);
                double x0 = xs.Count > 0 ? xs.Min() : PlaceX(run.A, ms, y.Sources[0], chosen, c), x1 = xs.Count > 0 ? xs.Max() : x0;
                for (int k = 0; k < y.Sources.Count; k++)
                    Add(run, a, ms, y.Sources[k], y.Sources.Count == 1 ? (x0 + x1) / 2 : x0 + (x1 - x0) * k / (y.Sources.Count - 1));
                continue;
            }
            if (x.IsRest)
            {
                for (int k = 0; k < x.Sources.Count; k++)
                    if (SymbolOf(run.A, ms, x.Sources[k]) is var (page, sym)) SetLength(run, page, sym, y.Sources[k], a);
                continue;
            }
            EditNote(run, a, ms, x.Sources[0], y.Sources[0]);
        }
    }

    /// <summary>A note (or chord) of A made into the chosen one: its length and its heads.</summary>
    private static void EditNote(EditRun run, MergeBar a, List<Measure> ms, ScoreEvent se, ScoreEvent te)
    {
        var pa = se.Notes.OrderBy(n => StrandReader.Midi(n.Pitch)).ToList();
        var pb = te.Notes.OrderBy(n => StrandReader.Midi(n.Pitch)).ToList();
        if (pa.Count == 0 || pb.Count == 0) return;
        if ((se.Type != te.Type || se.Dots != te.Dots) && FindHead(run.A, ms, pa[0]) is var (pg, sym, _)) SetLength(run, pg, sym, te, a);
        if (pa.Count == pb.Count)
        {
            for (int k = 0; k < pa.Count; k++)
            {
                if (StrandReader.Midi(pa[k].Pitch) == StrandReader.Midi(pb[k].Pitch)) continue;
                if (FindHead(run.A, ms, pa[k]) is not var (page, s, note)) { run.Skipped.Add($"bar {a.Number}: head {pa[k].Pitch} not found"); continue; }
                int steps = StrandReader.Diatonic(pb[k].Pitch) - StrandReader.Diatonic(pa[k].Pitch);
                if (steps != 0) Record(run, page, s, new SymbolEdit { Op = "head", Head = CorrectionStore.Heads(s).IndexOf(note), Step = note.Step + steps });
                run.Heads.Add((page, s, note, pb[k].Pitch, a.KeyFifths));
            }
            return;
        }
        // a chord with heads too many: those the chosen one lacks are removed
        var keep = pb.Select(n => StrandReader.Midi(n.Pitch)).ToHashSet();
        if (!keep.IsSubsetOf(pa.Select(n => StrandReader.Midi(n.Pitch)))) { run.Skipped.Add($"bar {a.Number}: a chord head to add ({string.Join("+", pb.Select(n => n.Pitch))})"); return; }
        foreach (var n in pa.Where(n => !keep.Contains(StrandReader.Midi(n.Pitch))))
            if (FindHead(run.A, ms, n) is var (page, s, note))
                Record(run, page, s, new SymbolEdit { Op = "removeHead", Head = CorrectionStore.Heads(s).IndexOf(note) });
    }

    private static void SetLength(EditRun run, PageResult page, MusicSymbol sym, ScoreEvent te, MergeBar a)
    {
        string d = SymbolDuration(te.Type);
        var e = new SymbolEdit { Op = "set" };
        if (sym.Duration != d) e.Duration = d;
        if (sym.Dots != te.Dots) e.Dots = te.Dots;
        if (e.Duration == null && e.Dots == null) { run.Skipped.Add($"bar {a.Number}: a length that differs otherwise (a tuplet?)"); return; }
        Record(run, page, sym, e);
    }

    private static void Delete(EditRun run, List<Measure> ms, ScoreEvent se, MergeBar a)
    {
        if (SymbolOf(run.A, ms, se) is var (page, sym)) Record(run, page, sym, new SymbolEdit { Op = "delete" });
        else run.Skipped.Add($"bar {a.Number}: a symbol to delete was not found");
    }

    /// <summary>Adds the chosen event to A's bar at x (a chord: its lowest head only, the rest is reported).</summary>
    private static void Add(EditRun run, MergeBar a, List<Measure> ms, ScoreEvent te, double x)
    {
        var m = MeasureFor(run.A, ms, te.Staff);
        var page = PageOf(run.A, m);
        if (m == null || page == null || double.IsNaN(x)) { run.Skipped.Add($"bar {a.Number}: no place for an added {(te.IsRest ? "rest" : "note")}"); return; }
        var staff = m.Staff;
        double ss = staff.Space;
        var e = new SymbolEdit { Op = "add", Staff = staff.Index, Kind = te.IsRest ? "Rest" : "Note", Duration = SymbolDuration(te.Type), Dots = te.Dots };
        PitchSpec? target = null;
        if (te.IsRest)
        {
            double cy = (staff.TopY(x) + staff.BottomY(x)) / 2;
            e.Bounds = new Rect((int)Math.Round(x - 0.5 * ss), (int)Math.Round(cy - ss), (int)Math.Round(x + 0.5 * ss), (int)Math.Round(cy + ss));
        }
        else
        {
            var heads = te.Notes.OrderBy(n => StrandReader.Midi(n.Pitch)).ToList();
            target = heads[0].Pitch;
            if (heads.Count > 1) run.Skipped.Add($"bar {a.Number}: only the lowest head of {string.Join("+", heads.Select(n => n.Pitch))} added");
            if (StepFor(run.A, a, te.Staff, target) is not int step) { run.Skipped.Add($"bar {a.Number}: no note to take the clef from"); return; }
            double hy = staff.BottomY(x) - step * ss / 2;
            e.Step = step;
            e.Stem = te.StemUp == false || te.StemUp == null && step >= 4 ? "down" : "up";
            e.Bounds = new Rect((int)Math.Round(x - 0.6 * ss), (int)Math.Round(hy - 0.5 * ss), (int)Math.Round(x + 0.6 * ss), (int)Math.Round(hy + 0.5 * ss));
        }
        if (!CorrectionStore.Apply(page, e)) { run.Skipped.Add($"bar {a.Number}: could not add"); return; }
        run.Edits.Add((page.PageNumber, e));
        var added = page.Origins.FirstOrDefault(kv => kv.Value == e.Bounds && kv.Key.Attributes.ContainsKey("added")).Key;
        if (target != null && added != null && added.Notes.Count > 0) run.Heads.Add((page, added, added.Notes[0], target, a.KeyFifths));
    }

    /// <summary>Records an edit of a symbol of A (identified by its staff and first bounds) and performs it.</summary>
    private static bool Record(EditRun run, PageResult page, MusicSymbol sym, SymbolEdit e)
    {
        e.Staff = page.Analysis.Staves.FirstOrDefault(st => st.Measures.Any(m => m.Symbols.Contains(sym) || m.Symbols.Any(p => p.Children.Contains(sym))))?.Index ?? sym.Staff?.Index ?? -1;
        e.Bounds = page.Origins.TryGetValue(sym, out var o) ? o : sym.Bounds;
        if (!CorrectionStore.Apply(page, e)) { run.Skipped.Add($"an edit ({e.Op}) could not be applied"); return false; }
        run.Edits.Add((page.PageNumber, e));
        return true;
    }

    private static void ReinterpretAll(MergeData src)
    {
        foreach (var (n, page) in src.ByNumber.OrderBy(kv => kv.Key))
            RecognitionEngine.Reinterpret(page, src.ByNumber.GetValueOrDefault(PageOrder.Previous(src.File, n)));
    }

    private static IEnumerable<MusicSymbol> AllSymbols(Measure m) => m.Symbols.SelectMany(s => s.Children.Prepend(s));

    private static PageResult? PageOf(MergeData src, Measure? m) => m == null ? null : src.ByNumber.Values.FirstOrDefault(p => p.Analysis.Staves.Contains(m.Staff));

    /// <summary>The head of a note in A's bar, with the symbol that holds it.</summary>
    private static (PageResult page, MusicSymbol sym, Note note)? FindHead(MergeData src, List<Measure> ms, ScoreNote n)
    {
        foreach (var m in ms)
            foreach (var s in AllSymbols(m))
                foreach (var h in s.Notes)
                    if (Math.Abs(h.HeadX - n.SourceX) <= 3 && Math.Abs(h.HeadY - n.SourceY) <= 3 && PageOf(src, m) is PageResult p)
                        return (p, s, h);
        return null;
    }

    /// <summary>The symbol of an event of A's bar: a note's by its first head, a rest's by its centre.</summary>
    private static (PageResult page, MusicSymbol sym)? SymbolOf(MergeData src, List<Measure> ms, ScoreEvent se)
    {
        if (!se.IsRest) return se.Notes.Count > 0 && FindHead(src, ms, se.Notes[0]) is var (p, s, _) ? (p, s) : null;
        var best = ms.SelectMany(m => AllSymbols(m).Where(s => s.Kind == SymbolKind.Rest).Select(s => (m, s)))
                     .OrderBy(t => Math.Abs(t.s.Bounds.CenterX - se.X) + Math.Abs(t.s.Bounds.CenterY - se.Y)).FirstOrDefault();
        if (best.s == null || Math.Abs(best.s.Bounds.CenterX - se.X) > 3 * best.m.Staff.Space) return null;
        return PageOf(src, best.m) is PageResult page ? (page, best.s) : null;
    }

    /// <summary>The page measure of A's bar on the event's staff.</summary>
    private static Measure? MeasureFor(MergeData src, List<Measure> ms, int eventStaff)
    {
        var ordered = ms.OrderBy(m => m.Staff.Index).ToList();
        if (ordered.Count == 0) return null;
        int k = src.Strand.Staff is int st ? st - 1 : eventStaff - 1;
        return ordered[Math.Clamp(k, 0, ordered.Count - 1)];
    }

    /// <summary>Where an event of the chosen version goes in A's bar: at the same fraction of the bar's width.</summary>
    private static double PlaceX(MergeData a0, List<Measure> ms, ScoreEvent te, MergeData chosen, MergeBar c)
    {
        var mA = MeasureFor(a0, ms, te.Staff);
        if (mA == null || c.SourceMeasure is not ScoreMeasure sc || !chosen.Measures.TryGetValue(sc, out var mc) || MeasureFor(chosen, mc, te.Staff) is not Measure mC)
            return double.NaN;
        double f = Math.Clamp((te.X - mC.X0) / Math.Max(1.0, mC.X1 - mC.X0), 0.02, 0.98);
        return mA.X0 + f * (mA.X1 - mA.X0);
    }

    /// <summary>The staff step of a pitch in A, from a note of A nearby on the same staff (whose clef it shares).</summary>
    private static int? StepFor(MergeData src, MergeBar a, int eventStaff, PitchSpec target)
    {
        var bars = src.Strand.Bars;
        for (int d = 0; d <= 8; d++)
            foreach (int i in new[] { a.Index - d, a.Index + d }.Distinct())
            {
                if (i < 0 || i >= bars.Count || bars[i].SourceMeasure is not ScoreMeasure sm || !src.Measures.TryGetValue(sm, out var ms)) continue;
                foreach (var ev in sm.Events.Where(e => !e.IsRest && e.Staff == eventStaff))
                    foreach (var n in ev.Notes)
                        if (FindHead(src, ms, n) is var (_, _, head))
                            return head.Step + StrandReader.Diatonic(target) - StrandReader.Diatonic(n.Pitch);
            }
        return null;
    }

    private static string SymbolDuration(string type) => type switch { "eighth" => "8th", _ => type };

    private static int KeyAlter(string letter, int fifths)
    {
        if (fifths > 0) return "FCGDAEB".IndexOf(letter[0]) is int i && i >= 0 && i < fifths ? 1 : 0;
        if (fifths < 0) return "BEADGCF".IndexOf(letter[0]) is int j && j >= 0 && j < -fifths ? -1 : 0;
        return 0;
    }
}

public sealed partial class MergeWindow
{
    private void SaveAsEdits()
    {
        if (_saveEdits == null || _sources.Count == 0 || _sources[0].ByNumber.Count == 0) return;
        var (edits, summary) = MergeEdits.Make(_sources, _rows, _reviewed);
        _saveEdits(edits, summary);
        Close();
    }
}
