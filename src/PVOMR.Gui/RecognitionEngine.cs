using PVOMR.Core.Analysis;
using PVOMR.Core.Imaging;
using PVOMR.Core.Input;
using PVOMR.Core.Model;

namespace PVOMR.Gui;

/// <summary>One analysed page: the recognition result plus the images it was computed on (deskewed, so symbol coordinates match).</summary>
public sealed class PageResult
{
    public required int PageNumber { get; init; }
    public required PageAnalysis Analysis { get; init; }
    public required AnalysisContext Context { get; init; }
    public bool Modified { get; set; }
    /// <summary>Bounds of every symbol when it was first seen (recognition or addition): the identity used by the correction log.</summary>
    public Dictionary<MusicSymbol, Rect> Origins { get; } = new();
}

/// <summary>Wraps the PVOMR.Core pipeline for the GUI: models and fonts are loaded once, pages are analysed on demand.</summary>
public sealed class RecognitionEngine
{
    private readonly PageAnalyzer _analyzer;
    public List<string> Log { get; } = new();

    public RecognitionEngine(string? fontDir = null, string? modelPath = null)
    {
        var options = new AnalysisOptions { FontDirectory = fontDir, TextRecognizer = PVOMR.Ocr.WindowsTextRecognizer.TryCreate() };
        var glyphs = GlyphLibrary.Load(fontDir, Log);
        var classifier = modelPath == "none" ? null : GlyphClassifier.Load(modelPath, Log);
        var heads = modelPath == "none" ? null : GlyphClassifier.Load(null, Log, "heads");
        _analyzer = new PageAnalyzer(options, glyphs, classifier, heads);
    }

    public static int PageCount(string file) => PageSource.IsPdf(file) ? PageSource.GetPdfPageCount(file) : 1;

    /// <summary>The page image as recognition will see it (before deskewing): shown at once while the page is recognised.</summary>
    public static GrayImage Render(string file, int pageNumber, double dpi, out double effectiveDpi)
    {
        effectiveDpi = dpi;
        if (!PageSource.IsPdf(file)) return ImageIO.Load(file);
        effectiveDpi = PageSource.EffectiveDpi(file, pageNumber, dpi);
        return PageSource.RenderPdfPage(file, pageNumber, effectiveDpi);
    }

    public PageResult Analyze(string file, int pageNumber, double dpi, PageResult? previous = null, GrayImage? rendered = null)
    {
        double effectiveDpi;
        var image = rendered != null && PageSource.IsPdf(file) ? rendered : Render(file, pageNumber, dpi, out effectiveDpi);
        effectiveDpi = PageSource.IsPdf(file) ? PageSource.EffectiveDpi(file, pageNumber, dpi) : dpi;
        Func<double, GrayImage>? rerender = PageSource.IsPdf(file) ? d => PageSource.RenderPdfPage(file, pageNumber, d) : null;
        var analysis = _analyzer.Analyze(image, file, pageNumber, effectiveDpi, rerender, out var ctx, previous?.Analysis);
        var result = new PageResult { PageNumber = pageNumber, Analysis = analysis, Context = ctx };
        CorrectionStore.RecordOrigins(result);
        return result;
    }

    /// <summary>Recomputes clefs, keys, times and pitch names after the symbols of a page were edited.</summary>
    public static void Reinterpret(PageResult page, PageResult? previous = null) => MusicInterpreter.Interpret(page.Analysis, previous?.Analysis);
}
