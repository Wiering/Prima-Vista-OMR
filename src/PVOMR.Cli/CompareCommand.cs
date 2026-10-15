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
