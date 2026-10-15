using PVOMR.Merge;

/// <summary>"pvomr compare": two versions of the same music (two editions, or a part and its staff in the score) bar by bar.</summary>
static class CompareCommand
{
    public static int Run(string[] args)
    {
        var files = new List<string>();
        int partA = 0, partB = 0; int? staffA = null, staffB = null; bool all = false;
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"missing value for {a}");
            switch (a)
            {
                case "--part": partA = partB = int.Parse(Next()) - 1; break;
                case "--part-b": partB = int.Parse(Next()) - 1; break;
                case "--staff": staffA = staffB = int.Parse(Next()); break;
                case "--staff-b": staffB = int.Parse(Next()); break;
                case "--all": all = true; break;
                default:
                    if (a.StartsWith("--")) { Console.Error.WriteLine($"unknown option {a}"); return 2; }
                    files.Add(a); break;
            }
        }
        if (files.Count != 2) { Console.Error.WriteLine("usage: pvomr compare <a.musicxml> <b.musicxml> [--part n] [--staff n] [--part-b n] [--staff-b n] [--all]"); return 2; }
        var sa = StrandReader.FromMusicXml(files[0], partA, staffA);
        var sb = StrandReader.FromMusicXml(files[1], partB, staffB);
        var rows = Comparer.Compare(sa, sb);
        int same = rows.Count(r => r.Same), aligned = rows.Count(r => r.A != null && r.B != null);
        Console.WriteLine($"A: {sa}\nB: {sb}");
        Console.WriteLine($"{aligned} bars aligned, {same} identical, {aligned - same} differ; {rows.Count(r => r.B == null)} only in A, {rows.Count(r => r.A == null)} only in B");
        var kinds = rows.SelectMany(r => r.Diffs).GroupBy(d => d.Kind).OrderByDescending(g => g.Count());
        Console.WriteLine("differences: " + string.Join(", ", kinds.Select(g => $"{g.Key} {g.Count()}")));
        foreach (var (from, to, steps) in Comparer.ShiftedRuns(rows))
            Console.WriteLine($"pitches {steps:+0;-0} steps over {Where(rows[from])} .. {Where(rows[to])} (a clef read differently?)");
        foreach (var r in rows.Where(r => !r.Same))
        {
            if (!all && r.Diffs.All(d => d.Kind is DiffKind.Key or DiffKind.Time)) continue;
            Console.WriteLine($"{Where(r)}: {string.Join("; ", r.Diffs.Select(d => d.Text))}");
            if (all)
            {
                if (r.A != null) Console.WriteLine($"    A: {r.A.Key}");
                if (r.B != null) Console.WriteLine($"    B: {r.B.Key}");
            }
        }
        return 0;
    }

    private static string Where(ComparisonRow r) =>
        $"bar {r.A?.Number ?? r.B?.Number} (A {(r.A == null ? "-" : $"p{r.A.Page}.{r.A.Line}{(r.A.Fits ? "" : "!")}")}, B {(r.B == null ? "-" : $"p{r.B.Page}.{r.B.Line}{(r.B.Fits ? "" : "!")}")})";
}

/// <summary>"pvomr merge": several versions of the same music into one, the best version of every bar.</summary>
static class MergeCommand
{
    public static int Run(string[] args)
    {
        var sources = new List<string>();
        string? output = null; bool listAll = false;
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--out" && i + 1 < args.Length) output = args[++i];
            else if (a == "--all") listAll = true;
            else if (a.StartsWith("--")) { Console.Error.WriteLine($"unknown option {a}"); return 2; }
            else sources.Add(a);
        }
        if (sources.Count < 2) { Console.Error.WriteLine("usage: pvomr merge <source> <source> [...] [--out merged.musicxml] [--all]   (source: file[@part[:staff]])"); return 2; }
        var strands = sources.Select(Parse).ToList();
        var rows = Merger.Align(strands);
        Merger.Choose(rows);
        for (int s = 0; s < strands.Count; s++)
            Console.WriteLine($"{(char)('A' + s)}: {strands[s]}, {strands[s].Bars.Count(b => b.Fits)} add up");
        Console.WriteLine($"{rows.Count} bars: " + string.Join(", ", rows.GroupBy(r => r.Status).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
        var used = rows.Where(r => r.Chosen >= 0).GroupBy(r => r.Chosen).OrderBy(g => g.Key).Select(g => $"{(char)('A' + g.Key)} {g.Count()}");
        Console.WriteLine("taken from: " + string.Join(", ", used));
        foreach (var r in rows.Where(r => listAll ? r.Status != RowStatus.Unanimous : r.Status == RowStatus.Flagged))
        {
            Console.WriteLine($"{r.Status} bar {rows.IndexOf(r) + 1}, chosen {(char)('A' + r.Chosen)}:");
            for (int s = 0; s < strands.Count; s++)
                if (r.Candidates[s] is MergeBar b)
                    Console.WriteLine($"    {(char)('A' + s)} p{b.Page}.{b.Line} bar {b.Number}{(b.Fits ? "" : " (does not add up)")}: {b.Key}");
        }
        if (output != null)
        {
            PVOMR.Core.Score.MusicXmlWriter.Write(Merger.BuildScore(rows, strands, Path.GetFileNameWithoutExtension(output)), output);
            Console.WriteLine($"written {output}");
        }
        return 0;
    }

    private static Strand Parse(string spec)
    {
        int at = spec.LastIndexOf('@');
        string file = at > 0 ? spec[..at] : spec;
        int part = 0; int? staff = null;
        if (at > 0)
        {
            var bits = spec[(at + 1)..].Split(':');
            part = int.Parse(bits[0]) - 1;
            if (bits.Length > 1) staff = int.Parse(bits[1]);
        }
        return StrandReader.FromMusicXml(file, part, staff);
    }
}
