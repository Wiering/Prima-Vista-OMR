using System.Runtime.InteropServices.WindowsRuntime;
using PVOMR.Core.Analysis;
using PVOMR.Core.Model;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace PVOMR.Ocr;

/// <summary>Text recognition with the OCR engine built into Windows 10/11 (no download; uses an installed OCR language).</summary>
public sealed class WindowsTextRecognizer : ITextRecognizer
{
    private readonly OcrEngine _engine;
    private readonly object _lock = new();

    private WindowsTextRecognizer(OcrEngine engine) { _engine = engine; }

    public string Language => _engine.RecognizerLanguage.LanguageTag;

    /// <summary>
    /// An engine for a Latin-script language that is installed, preferring English (instrument names are Italian,
    /// German, French and English), or null when Windows has no OCR language at all.
    /// </summary>
    public static WindowsTextRecognizer? TryCreate(string? languageTag = null)
    {
        try
        {
            var tags = languageTag != null ? new[] { languageTag } : new[] { "en-US", "en-GB", "nl-NL", "de-DE", "fr-FR", "it-IT" };
            foreach (var tag in tags)
            {
                var lang = new Language(tag);
                if (OcrEngine.IsLanguageSupported(lang) && OcrEngine.TryCreateFromLanguage(lang) is OcrEngine e) return new WindowsTextRecognizer(e);
            }
            return OcrEngine.TryCreateFromUserProfileLanguages() is OcrEngine u ? new WindowsTextRecognizer(u) : null;
        }
        catch (Exception) { return null; }
    }

    public IReadOnlyList<TextLine> Recognize(byte[] gray, int width, int height)
    {
        int max = (int)OcrEngine.MaxImageDimension;
        if (width > max || height > max)
        {
            // too large for the engine: recognise the part that fits (instrument margins and headers are small)
            int w = Math.Min(width, max), h = Math.Min(height, max);
            var part = new byte[w * h];
            for (int y = 0; y < h; y++) Array.Copy(gray, y * width, part, y * w, w);
            gray = part; width = w; height = h;
        }
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Gray8, width, height);
        bitmap.CopyFromBuffer(gray.AsBuffer());
        using var bgra = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8);
        OcrResult result;
        lock (_lock) result = _engine.RecognizeAsync(bgra).AsTask().GetAwaiter().GetResult();
        var lines = new List<TextLine>();
        foreach (var line in result.Lines)
        {
            var words = line.Words.Select(w => (w.Text, ToRect(w.BoundingRect))).ToList();
            if (words.Count == 0) continue;
            var bounds = words.Select(w => w.Item2).Aggregate((a, b) => a.Union(b));
            lines.Add(new TextLine(line.Text, bounds, words));
        }
        return lines;
    }

    private static Rect ToRect(Windows.Foundation.Rect r) =>
        new((int)Math.Floor(r.X), (int)Math.Floor(r.Y), (int)Math.Ceiling(r.X + r.Width) - 1, (int)Math.Ceiling(r.Y + r.Height) - 1);
}
