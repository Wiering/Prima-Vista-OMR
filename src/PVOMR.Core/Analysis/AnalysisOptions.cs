using PVOMR.Core.Imaging;

namespace PVOMR.Core.Analysis;

public sealed class AnalysisOptions
{
    public double Dpi { get; set; } = 300;
    public bool Deskew { get; set; } = true;
    public BinarizationMethod Binarization { get; set; } = BinarizationMethod.Auto;
    public string? FontDirectory { get; set; }
    public bool Verbose { get; set; }
    /// <summary>Minimum staff space (px) we are comfortable with; a PDF page is re-rendered at a higher DPI when below this.</summary>
    public double MinStaffSpace { get; set; } = 14;
    public bool DebugImages { get; set; }
    /// <summary>Path of the ONNX glyph classifier (null = search the default locations).</summary>
    public string? ModelPath { get; set; }
}
