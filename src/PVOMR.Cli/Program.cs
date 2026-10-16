using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PVOMR.Core.Analysis;
using PVOMR.Core.Imaging;
using PVOMR.Core.Input;
using PVOMR.Core.Model;
using PVOMR.Core.Output;

static int Usage()
{
    Console.Error.WriteLine("""
        PVOMR - optical music recognition (analysis stage)

        usage: pvomr <input.pdf | image ...> [options]

        options:
          --pages <spec>      pages to analyse for PDFs, e.g. 1-3,7 (default: all)
          --dpi <n>           render resolution for PDFs (default 300; raised automatically for tiny staves)
          --out <dir>         write report (.txt), JSON (.json) and debug overlays (.png) to this directory
          --debug             write debug overlay images (requires --out)
          --json              also write a JSON file with everything detected (requires --out)
          --musicxml <file>   assemble all analysed pages into a score and write MusicXML
          --omit <list>       leave these out of the MusicXML: dynamics,articulations,slurs,ties,text,repeats,tuplets,beams
          --grand-staff       force two-staff systems to be one piano part (default: automatic)
          --fonts <dir>       directory with SMuFL fonts (default: ./fonts or D:\OMR\fonts)
          --model <file>      ONNX glyph classifier (default: models/glyphs.onnx if present; "none" = templates only)
          --binarize <m>      auto | otsu | sauvola (default auto)
          --no-deskew         skip skew correction
          --no-ocr            do not read instrument names and header text (Windows OCR)
          --quiet             only print the summary line per page
          --augment <spec>    degrade the image first, e.g. blur=1.2,noise=0.08,skew=1.5,scale=0.6,contrast=0.6,speckle=0.002
          --verbose           print analysis notes and bounding boxes
          --fit               per page: how many measures add up to the time signature (the misfits with --verbose)

        sub-commands:
          pvomr gen --out <dir> [--count 12] [--seed 1] [--measures 16] [--piano|--single] [--style hard|tuplets|ornaments] [--musescore <exe>]
                              generate synthetic pieces (MusicXML ground truth + PDF rendered by MuseScore)
          pvomr crops --dir <synth dir> [--dir ...] --out <crops dir> [--fonts <dir>] [--variants 30] [--no-augment]
                              labelled training crops from synthetic pages (needs the SVG export) and from the SMuFL fonts
          pvomr eval --dir <dir> [--augment <spec>] [--details] [--debug] [--out report.txt] [--model <file>|none]
          pvomr eval <input> <truth.musicxml>
                              recognise and compare with the ground truth; prints precision/recall metrics
          pvomr compare <a.musicxml> <b.musicxml> [--part n] [--staff n] [--part-b n] [--staff-b n] [--all]
                              compare two versions of the same music bar by bar (sounding pitches)
          pvomr merge <source> <source> [...] [--out merged.musicxml] [--all]   (source: file[@part[:staff]])
                              merge versions of the same music: the best version of every bar, doubtful bars listed
        """);
    return 2;
}

if (args.Length > 0 && args[0] == "gen") return EvalCommands.Gen(args);
if (args.Length > 0 && args[0] == "compare") return CompareCommand.Run(args);
if (args.Length > 0 && args[0] == "merge") return MergeCommand.Run(args);
if (args.Length > 0 && args[0] == "crops") return EvalCommands.Crops(args);
// pvomr render <pdf> <page> <out.png> [dpi]: the page image as recognition sees it before deskewing
if (args.Length >= 4 && args[0] == "render")
{
    var img = PageSource.RenderPdfPage(args[1], int.Parse(args[2]), args.Length > 4 ? double.Parse(args[4]) : 300);
    ImageIO.SavePng(ImageIO.ToBitmap(img), args[3]);
    return 0;
}
if (args.Length > 0 && args[0] == "eval")
{
    var evalLog = new List<string>();
    string? fontDir = null;
    for (int i = 1; i + 1 < args.Length; i++) if (args[i] == "--fonts") fontDir = args[i + 1];
    return EvalCommands.Eval(args.Where((a, i) => !(a == "--fonts" || (i > 0 && args[i - 1] == "--fonts"))).ToArray(), GlyphLibrary.Load(fontDir, evalLog));
}

var inputs = new List<string>();
string? pages = null, outDir = null, fonts = null, musicXml = null, augment = null, model = null, omit = null;
bool? grandStaff = null;
double dpi = 300;
bool debug = false, json = false, quiet = false, verbose = false, deskew = true, diag = false, fit = false, ocr = true;
var binMethod = BinarizationMethod.Auto;
for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"missing value for {a}");
    switch (a)
    {
        case "--pages": pages = Next(); break;
        case "--dpi": dpi = double.Parse(Next()); break;
        case "--out": outDir = Next(); break;
        case "--debug": debug = true; break;
        case "--json": json = true; break;
        case "--musicxml": musicXml = Next(); break;
        case "--omit": omit = Next(); break;
        case "--augment": augment = Next(); break;
        case "--model": model = Next(); break;
        case "--grand-staff": grandStaff = true; break;
        case "--separate-staves": grandStaff = false; break;
        case "--fonts": fonts = Next(); break;
        case "--binarize": binMethod = Enum.Parse<BinarizationMethod>(Next(), true); break;
        case "--no-deskew": deskew = false; break;
        case "--no-ocr": ocr = false; break;
        case "--quiet": quiet = true; break;
        case "--verbose": verbose = true; break;
        case "--diag": diag = true; break;
        case "--fit": fit = true; break;
        case "-h": case "--help": return Usage();
        default:
            if (a.StartsWith("--")) { Console.Error.WriteLine($"unknown option {a}"); return Usage(); }
            inputs.Add(a); break;
    }
}
if (inputs.Count == 0) return Usage();

var log = new List<string>();
var options = new AnalysisOptions { Dpi = dpi, Deskew = deskew, Binarization = binMethod, FontDirectory = fonts, Verbose = verbose, DebugImages = debug,
                                   TextRecognizer = ocr ? PVOMR.Ocr.WindowsTextRecognizer.TryCreate() : null };
var glyphs = GlyphLibrary.Load(fonts, log);
var classifier = model == "none" ? null : GlyphClassifier.Load(model, log);
var headVerifier = model == "none" ? null : GlyphClassifier.Load(null, log, "heads");
if (verbose) foreach (var l in log) Console.WriteLine("note: " + l);
if (outDir != null) Directory.CreateDirectory(outDir);
var analyzer = new PageAnalyzer(options, glyphs, classifier, headVerifier);
var jsonOptions = new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, ReferenceHandler = ReferenceHandler.IgnoreCycles, Converters = { new JsonStringEnumConverter() } };

int exit = 0;
var allPages = new List<PageAnalysis>();
foreach (var input in inputs)
{
    if (!File.Exists(input)) { Console.Error.WriteLine($"not found: {input}"); exit = 1; continue; }
    var reportSb = new StringBuilder();
    var pageResults = new List<PageAnalysis>();
    var byPage = new Dictionary<int, PageAnalysis>();
    try
    {
        foreach (var page in PageSource.Enumerate(input, pages, dpi))
        {
            var img = page.Image;
            if (augment != null) img = Augment.Apply(img, augment, page.PageNumber);
            Func<double, GrayImage>? rerender = page.FromPdf && augment == null ? d => PageSource.RenderPdfPage(page.Source, page.PageNumber, d) : null;
            var result = analyzer.Analyze(img, page.Source, page.PageNumber, page.Dpi, rerender, out var ctx, byPage.GetValueOrDefault(PageOrder.Previous(input, page.PageNumber)));
            byPage[page.PageNumber] = result;
            if (diag) Diagnostics.DumpStaves(result, ctx);
            string text = TextReport.Write(result, verbose);
            if (quiet) Console.WriteLine(text.Split('\n')[0] + (result.Staves.Count > 0 ? $"  staves={result.Staves.Count} systems={result.Systems.Count} measures={result.Staves.Sum(s => s.Measures.Count)}" : "  (no music)"));
            else Console.Write(text);
            if (fit) Diagnostics.PrintFit(pageResults.Append(result).ToList(), result, verbose);
            reportSb.Append(text).AppendLine();
            pageResults.Add(result);
            allPages.Add(result);
            if (outDir != null && debug)
            {
                string name = $"{Path.GetFileNameWithoutExtension(input)}_p{page.PageNumber:000}";
                DebugOverlay.Save(result, ctx, Path.Combine(outDir, name + "_debug.png"));
                if (verbose) ImageIO.SavePng(ImageIO.ToBitmap(ctx.NoStaff), Path.Combine(outDir, name + "_nostaff.png"));
            }
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error processing {input}: {ex}");
        exit = 1;
    }
    if (outDir != null)
    {
        string baseName = Path.GetFileNameWithoutExtension(input);
        File.WriteAllText(Path.Combine(outDir, baseName + ".txt"), reportSb.ToString());
        if (json) File.WriteAllText(Path.Combine(outDir, baseName + ".json"), JsonSerializer.Serialize(pageResults, jsonOptions));
    }
}
if (musicXml != null)
{
    var score = PVOMR.Core.Score.MusicAssembler.Build(allPages, new PVOMR.Core.Score.MusicAssembler.Options { GrandStaff = grandStaff, Title = Path.GetFileNameWithoutExtension(inputs[0]) });
    var exportOptions = new PVOMR.Core.Score.ExportOptions();
    if (omit != null) foreach (var u in exportOptions.Omit(omit)) Console.Error.WriteLine($"unknown --omit item '{u}' (known: {string.Join(", ", PVOMR.Core.Score.ExportOptions.Names)})");
    PVOMR.Core.Score.MusicXmlWriter.Write(score, musicXml, exportOptions);
    Console.WriteLine($"MusicXML written to {musicXml}: {score.Parts.Count} part(s), {score.Parts.Sum(p => p.Measures.Count)} measures, {score.Parts.Sum(p => p.Measures.Sum(m => m.Events.Count))} events, {score.Warnings.Count} warnings");
    if (!quiet) foreach (var wmsg in score.Warnings.Take(verbose ? int.MaxValue : 40)) Console.WriteLine("  warning: " + wmsg);
    if (!quiet && !verbose && score.Warnings.Count > 40) Console.WriteLine($"  ... {score.Warnings.Count - 40} more warnings (use --verbose)");
}
return exit;
