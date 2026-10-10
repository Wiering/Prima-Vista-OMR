using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PVOMR.Core.Analysis;
using PVOMR.Core.Eval;
using PVOMR.Core.Imaging;
using PVOMR.Core.Input;
using PVOMR.Core.Model;
using PVOMR.Core.Score;

/// <summary>`pvomr gen` and `pvomr eval` sub-commands: synthetic ground truth and metrics.</summary>
static class EvalCommands
{
    public static int Gen(string[] args)
    {
        string outDir = "synth"; int count = 12, seed = 1, measures = 16; bool? piano = null;
        string musescore = @"C:\Program Files\MuseScore 4\bin\MuseScore4.exe";
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"missing value for {a}");
            switch (a)
            {
                case "--out": outDir = Next(); break;
                case "--count": count = int.Parse(Next()); break;
                case "--seed": seed = int.Parse(Next()); break;
                case "--measures": measures = int.Parse(Next()); break;
                case "--piano": piano = true; break;
                case "--single": piano = false; break;
                case "--musescore": musescore = Next(); break;
                default: Console.Error.WriteLine($"unknown option {a}"); return 2;
            }
        }
        Directory.CreateDirectory(outDir);
        var jobs = new List<object>();
        string[] clefs = { "treble", "bass", "alto", "treble", "bass" };
        string[] times = { "4/4", "3/4", "2/4", "6/8", "C", "4/4" };
        for (int i = 0; i < count; i++)
        {
            var gen = new SyntheticScoreGenerator(seed * 1000 + i);
            bool isPiano = piano ?? (i % 4 == 3);
            var settings = new SyntheticScoreGenerator.Settings
            {
                Measures = measures, Piano = isPiano, Clef = clefs[i % clefs.Length], Time = times[i % times.Length], KeyFifths = (i % 9) - 4,
                MinType = i % 3 == 0 ? 3 : 4, ChordProbability = isPiano ? 0.2 : 0.08,
            };
            string name = $"synth_{i + 1:00}";
            var score = gen.Generate(settings, name);
            string xml = Path.Combine(outDir, name + ".musicxml");
            MusicXmlWriter.Write(score, xml);
            jobs.Add(new { @in = Path.GetFullPath(xml), @out = Path.GetFullPath(Path.Combine(outDir, name + ".pdf")) });
            Console.WriteLine($"{name}: {(isPiano ? "piano" : settings.Clef)} {settings.Time} key {settings.KeyFifths} {measures} measures, {score.Parts.Sum(p => p.Measures.Sum(m => m.Events.Count))} events");
        }
        if (!File.Exists(musescore)) { Console.Error.WriteLine($"MuseScore not found at {musescore}; MusicXML files written, render them to PDF yourself (--musescore <path>)"); return 1; }
        string job = Path.Combine(outDir, "render-job.json");
        File.WriteAllText(job, JsonSerializer.Serialize(jobs));
        var psi = new ProcessStartInfo(musescore, $"-j \"{Path.GetFullPath(job)}\"") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi)!;
        proc.StandardOutput.ReadToEnd(); proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        int rendered = Directory.GetFiles(outDir, "synth_*.pdf").Length;
        Console.WriteLine($"MuseScore rendered {rendered} PDF(s) in {sw.Elapsed.TotalSeconds:0}s (exit code {proc.ExitCode})");
        return rendered == count ? 0 : 1;
    }

    public static int Eval(string[] args, GlyphLibrary? glyphs)
    {
        string? dir = null, augment = null, outFile = null, single = null, truthFile = null;
        double dpi = 300; bool details = false, debug = false; int seed = 1;
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"missing value for {a}");
            switch (a)
            {
                case "--dir": dir = Next(); break;
                case "--augment": augment = Next(); break;
                case "--dpi": dpi = double.Parse(Next()); break;
                case "--out": outFile = Next(); break;
                case "--details": details = true; break;
                case "--debug": debug = true; break;
                case "--seed": seed = int.Parse(Next()); break;
                default:
                    if (a.StartsWith("--")) { Console.Error.WriteLine($"unknown option {a}"); return 2; }
                    if (single == null) single = a; else truthFile = a;
                    break;
            }
        }
        var pairs = new List<(string name, string input, string truth)>();
        if (dir != null)
            foreach (var xml in Directory.GetFiles(dir, "*.musicxml").OrderBy(f => f))
            {
                string stem = Path.Combine(dir, Path.GetFileNameWithoutExtension(xml));
                string? input = new[] { ".pdf", ".png", ".jpg", ".tif" }.Select(e => stem + e).FirstOrDefault(File.Exists);
                if (input != null) pairs.Add((Path.GetFileNameWithoutExtension(xml), input, xml));
            }
        else if (single != null && truthFile != null) pairs.Add((Path.GetFileNameWithoutExtension(single), single, truthFile));
        else { Console.Error.WriteLine("usage: pvomr eval --dir <dir> [--augment spec] [--details] | pvomr eval <input> <truth.musicxml>"); return 2; }

        var analyzer = new PageAnalyzer(new AnalysisOptions { Dpi = dpi }, glyphs);
        var results = new List<ComparisonResult>();
        var sw = Stopwatch.StartNew();
        foreach (var (name, input, truth) in pairs)
        {
            var pages = new List<PageAnalysis>();
            foreach (var page in PageSource.Enumerate(input, null, dpi))
            {
                var img = page.Image;
                if (augment != null) img = Augment.Apply(img, augment, seed + page.PageNumber);
                Func<double, GrayImage>? rerender = page.FromPdf && augment == null ? d => PageSource.RenderPdfPage(page.Source, page.PageNumber, d) : null;
                var res = analyzer.Analyze(img, page.Source, page.PageNumber, page.Dpi, rerender, out var ctx);
                pages.Add(res);
                if (debug) PVOMR.Core.Output.DebugOverlay.Save(res, ctx, Path.Combine(Path.GetDirectoryName(input) ?? ".", $"{name}_p{page.PageNumber:000}_debug.png"));
            }
            var truthScore = MusicXmlReader.Read(truth);
            var recognised = MusicAssembler.Build(pages, new MusicAssembler.Options { GrandStaff = truthScore.Parts.Count == 1 && truthScore.Parts[0].StaffCount == 2 ? true : null });
            var r = ScoreComparer.Compare(truthScore, recognised, name);
            results.Add(r);
            Console.WriteLine($"{name}: {r.Summary()}");
            if (details) foreach (var d in r.Details.Take(30)) Console.WriteLine("    " + d);
        }
        Console.WriteLine();
        string table = ScoreComparer.Table(results);
        Console.Write(table);
        Console.WriteLine($"{pairs.Count} piece(s) in {sw.Elapsed.TotalSeconds:0.0}s{(augment != null ? $", augment: {augment}" : "")}");
        if (outFile != null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# eval {DateTime.Now:yyyy-MM-dd HH:mm} augment={augment ?? "none"} dpi={dpi}");
            sb.Append(table);
            File.AppendAllText(outFile, sb.ToString() + Environment.NewLine);
        }
        return 0;
    }
}
