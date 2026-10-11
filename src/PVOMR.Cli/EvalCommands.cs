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
        string outDir = "synth"; int count = 12, seed = 1, measures = 16; bool? piano = null; string? font = null; string style = "normal";
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
                case "--font": font = Next(); break;
                case "--style": style = Next(); break;      // normal | dense | voices | hard
                default: Console.Error.WriteLine($"unknown option {a}"); return 2;
            }
        }
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(Path.Combine(outDir, "svg"));
        string? stylePath = null;
        if (font != null)
        {
            stylePath = Path.Combine(outDir, "style.mss");
            File.WriteAllText(stylePath, $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<museScore version=\"4.70\"><Style><musicalSymbolFont>{font}</musicalSymbolFont><musicalTextFont>{font} Text</musicalTextFont></Style></museScore>\n");
        }
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
            if (style is "dense" or "hard") { settings.Density = 3.0; settings.MinType = 4; settings.TieProbability = 0.12; settings.AccidentalProbability = 0.14; settings.ChordProbability += 0.08; }
            if (style is "voices" or "hard") { settings.SecondVoiceProbability = style == "hard" ? 0.35 : 0.6; }
            if (style is "tuplets" or "hard") { settings.TripletProbability = style == "tuplets" ? 0.35 : 0.15; }
            string name = $"synth_{i + 1:00}";
            var score = gen.Generate(settings, name);
            string xml = Path.Combine(outDir, name + ".musicxml");
            MusicXmlWriter.Write(score, xml);
            jobs.Add(new { @in = Path.GetFullPath(xml), @out = new[] { Path.GetFullPath(Path.Combine(outDir, name + ".pdf")), Path.GetFullPath(Path.Combine(outDir, "svg", name + ".svg")) } });
            Console.WriteLine($"{name}: {(isPiano ? "piano" : settings.Clef)} {settings.Time} key {settings.KeyFifths} {measures} measures, {score.Parts.Sum(p => p.Measures.Sum(m => m.Events.Count))} events");
        }
        if (!File.Exists(musescore)) { Console.Error.WriteLine($"MuseScore not found at {musescore}; MusicXML files written, render them to PDF yourself (--musescore <path>)"); return 1; }
        string job = Path.Combine(outDir, "render-job.json");
        File.WriteAllText(job, JsonSerializer.Serialize(jobs));
        string styleArg = stylePath != null ? $" -S \"{Path.GetFullPath(stylePath)}\"" : "";
        var psi = new ProcessStartInfo(musescore, $"-j \"{Path.GetFullPath(job)}\"{styleArg}") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi)!;
        proc.StandardOutput.ReadToEnd(); proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        int rendered = Directory.GetFiles(outDir, "synth_*.pdf").Length;
        Console.WriteLine($"MuseScore rendered {rendered} PDF(s) in {sw.Elapsed.TotalSeconds:0}s (exit code {proc.ExitCode})");
        return rendered == count ? 0 : 1;
    }

    public static int Crops(string[] args)
    {
        var dirs = new List<string>(); string outDir = "crops"; string? fontDir = null; int variants = 30; bool augment = true; int seed = 1; bool heads = false; double dpi = 300;
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"missing value for {a}");
            switch (a)
            {
                case "--dir": dirs.Add(Next()); break;
                case "--out": outDir = Next(); break;
                case "--fonts": fontDir = Next(); break;
                case "--variants": variants = int.Parse(Next()); break;
                case "--no-augment": augment = false; break;
                case "--seed": seed = int.Parse(Next()); break;
                case "--heads": heads = true; break;
                case "--dpi": dpi = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;   // lower values give small-staff crops
                default: Console.Error.WriteLine($"unknown option {a}"); return 2;
            }
        }
        var log = new List<string>();
        var glyphs = GlyphLibrary.Load(null, log);
        if (heads)
        {
            var an = new PageAnalyzer(new AnalysisOptions { Dpi = dpi }, glyphs, null);
            var rnd2 = new Random(seed);
            var tot = new CropExtractor.Stats();
            foreach (var d in dirs)
            {
                var st = CropExtractor.ExtractHeadWindows(d, outDir, an, dpi, rnd2, augment);
                Console.WriteLine($"{d}: {st.Pages} pages, {st.Counts.Values.Sum()} windows");
                foreach (var kv in st.Counts) tot.Counts[kv.Key] = tot.Counts.GetValueOrDefault(kv.Key) + kv.Value;
            }
            foreach (var kv in tot.Counts.OrderByDescending(kv => kv.Value)) Console.WriteLine($"  {kv.Key,-16} {kv.Value}");
            return 0;
        }
        var analyzer = new PageAnalyzer(new AnalysisOptions { Dpi = dpi }, glyphs, null);
        var rnd = new Random(seed);
        var total = new CropExtractor.Stats();
        var sw = Stopwatch.StartNew();
        foreach (var d in dirs)
        {
            var st = CropExtractor.ExtractFromPages(d, outDir, analyzer, dpi, rnd, augment);
            Console.WriteLine($"{d}: {st.Pages} pages, {st.Counts.Values.Sum()} crops");
            foreach (var kv in st.Counts) total.Counts[kv.Key] = total.Counts.GetValueOrDefault(kv.Key) + kv.Value;
        }
        if (fontDir != null || Directory.Exists(@"D:\OMR\fonts"))
        {
            var st = CropExtractor.ExtractFromFonts(fontDir ?? @"D:\OMR\fonts", outDir, variants, rnd);
            Console.WriteLine($"fonts: {st.Counts.Values.Sum()} crops");
            foreach (var kv in st.Counts) total.Counts[kv.Key] = total.Counts.GetValueOrDefault(kv.Key) + kv.Value;
        }
        Console.WriteLine($"total {total.Counts.Values.Sum()} crops in {sw.Elapsed.TotalSeconds:0}s:");
        foreach (var kv in total.Counts.OrderByDescending(kv => kv.Value)) Console.WriteLine($"  {kv.Key,-16} {kv.Value}");
        return 0;
    }

    public static int Eval(string[] args, GlyphLibrary? glyphs)
    {
        string? dir = null, augment = null, outFile = null, single = null, truthFile = null, model = null, pageSpec = null, measureRange = null;
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
                case "--model": model = Next(); break;
                case "--pages": pageSpec = Next(); break;
                case "--measures": measureRange = Next(); break;
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

        var log = new List<string>();
        var classifier = model == "none" ? null : GlyphClassifier.Load(model, log);
        var headVerifier = model == "none" || model == "glyphs-only" ? null : GlyphClassifier.Load(null, log, "heads");
        if (model == "glyphs-only") classifier = GlyphClassifier.Load(null, log);
        foreach (var l in log) Console.WriteLine("note: " + l);
        var analyzer = new PageAnalyzer(new AnalysisOptions { Dpi = dpi }, glyphs, classifier, headVerifier);
        var results = new List<ComparisonResult>();
        var sw = Stopwatch.StartNew();
        foreach (var (name, input, truth) in pairs)
        {
            var pages = new List<PageAnalysis>();
            foreach (var page in PageSource.Enumerate(input, pageSpec, dpi))
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
            if (measureRange != null)
            {
                var mr = measureRange.Split('-');
                int ma = int.Parse(mr[0]), mb = int.Parse(mr[1]);
                foreach (var part in recognised.Parts)
                {
                    var keep = part.Measures.Skip(ma - 1).Take(mb - ma + 1).ToList();
                    part.Measures.Clear(); part.Measures.AddRange(keep);
                }
            }
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
