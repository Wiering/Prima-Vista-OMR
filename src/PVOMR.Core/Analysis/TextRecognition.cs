using PVOMR.Core.Model;

namespace PVOMR.Core.Analysis;

/// <summary>One recognised line of text; bounds in the coordinates of the image passed to the recogniser.</summary>
public sealed record TextLine(string Text, Rect Bounds, IReadOnlyList<(string Text, Rect Bounds)> Words);

/// <summary>
/// Optical character recognition for the text around the music (instrument names, headers). The core library only
/// defines the interface; the Windows implementation lives in PVOMR.Ocr, and without one no text is read.
/// </summary>
public interface ITextRecognizer
{
    /// <summary>Recognises the text in an 8-bit grey image (0 = black ink, 255 = paper).</summary>
    IReadOnlyList<TextLine> Recognize(byte[] gray, int width, int height);
}
