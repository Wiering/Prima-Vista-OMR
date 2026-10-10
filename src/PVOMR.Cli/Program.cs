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
          --fonts <dir>       directory with SMuFL fonts (default: ./fonts or D:\OMR\fonts)
          --binarize <m>      auto | otsu | sauvola (default auto)
          --no-deskew         skip skew correction
          --quiet             only print the summary line per page
          --verbose           print analysis notes and bounding boxes
        """);
    return 2;
}

var inputs = new List<string>();
string? pages = null, outDir = null, fonts = null;
double dpi = 300;
bool debug = false, json = false, quiet = false, verbose = false, deskew = true, diag = false;
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
        case "--fonts": fonts = Next(); break;
        case "--binarize": binMethod = Enum.Parse<BinarizationMethod>(Next(), true); break;
        case "--no-deskew": deskew = false; break;
        case "--quiet": quiet = true; break;
        case "--verbose": verbose = true; break;
        case "--diag": diag = true; break;
        case "-h": case "--help": return Usage();
        default:
            if (a.StartsWith("--")) { Console.Error.WriteLine($"unknown option {a}"); return Usage(); }
            inputs.Add(a); break;
    }
}
if (inputs.Count == 0) return Usage();

var log = new List<string>();
var options = new AnalysisOptions { Dpi = dpi, Deskew = deskew, Binarization = binMethod, FontDirectory = fonts, Verbose = verbose, DebugImages = debug };
var glyphs = GlyphLibrary.Load(fonts, log);
if (verbose) foreach (var l in log) Console.WriteLine("note: " + l);
if (outDir != null) Directory.CreateDirectory(outDir);
var analyzer = new PageAnalyzer(options, glyphs);
var jsonOptions = new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, ReferenceHandler = ReferenceHandler.IgnoreCycles, Converters = { new JsonStringEnumConverter() } };

int exit = 0;
foreach (var input in inputs)
{
    if (!File.Exists(input)) { Console.Error.WriteLine($"not found: {input}"); exit = 1; continue; }
    var reportSb = new StringBuilder();
    var pageResults = new List<PageAnalysis>();
    try
    {
        foreach (var page in PageSource.Enumerate(input, pages, dpi))
        {
            Func<double, GrayImage>? rerender = page.FromPdf ? d => PageSource.RenderPdfPage(page.Source, page.PageNumber, d) : null;
            var result = analyzer.Analyze(page.Image, page.Source, page.PageNumber, page.Dpi, rerender, out var ctx);
            if (diag) Diagnostics.DumpStaves(result, ctx);
            string text = TextReport.Write(result, verbose);
            if (quiet) Console.WriteLine(text.Split('\n')[0] + (result.Staves.Count > 0 ? $"  staves={result.Staves.Count} systems={result.Systems.Count} measures={result.Staves.Sum(s => s.Measures.Count)}" : "  (no music)"));
            else Console.Write(text);
            reportSb.Append(text).AppendLine();
            pageResults.Add(result);
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
return exit;
