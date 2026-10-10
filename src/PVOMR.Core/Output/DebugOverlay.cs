using PVOMR.Core.Analysis;
using PVOMR.Core.Imaging;
using PVOMR.Core.Model;
using SkiaSharp;

namespace PVOMR.Core.Output;

public static class DebugOverlay
{
    public static void Save(PageAnalysis p, AnalysisContext ctx, string path)
    {
        using var gray = ImageIO.ToBitmap(ctx.Gray);
        using var bmp = new SKBitmap(gray.Width, gray.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bmp);
        canvas.DrawBitmap(gray, 0, 0);
        float ss = (float)Math.Max(8, p.StaffSpace);
        using var font = new SKFont(SKTypeface.Default, Math.Max(10, ss * 0.8f));
        using var textPaint = new SKPaint { Color = new SKColor(200, 0, 120), IsAntialias = true };
        using var linePaint = new SKPaint { Color = new SKColor(255, 0, 0, 160), StrokeWidth = 1, Style = SKPaintStyle.Stroke, IsAntialias = true };
        using var barPaint = new SKPaint { Color = new SKColor(0, 0, 255, 200), StrokeWidth = 2, Style = SKPaintStyle.Stroke };
        using var measurePaint = new SKPaint { Color = new SKColor(0, 120, 0), IsAntialias = true };

        foreach (var st in p.Staves)
        {
            foreach (var line in st.Lines)
            {
                using var skPath = new SKPath();
                bool first = true;
                for (double x = st.Left; x <= st.Right; x += 8)
                {
                    float y = (float)line.YAt(x);
                    if (first) { skPath.MoveTo((float)x, y); first = false; } else skPath.LineTo((float)x, y);
                }
                canvas.DrawPath(skPath, linePaint);
            }
            foreach (var b in st.Barlines)
            {
                float yt = (float)st.TopY(b.X) - ss * 0.5f, yb = (float)st.BottomY(b.X) + ss * 0.5f;
                if (b.Spanning) yb = (float)p.Staves[b.StaffTo].BottomY(b.X) + ss * 0.5f;
                barPaint.Color = b.Kind == BarlineKind.Single ? new SKColor(0, 0, 255, 200) : new SKColor(255, 0, 255, 220);
                canvas.DrawRect(b.X0 - 1, yt, b.X1 - b.X0 + 3, yb - yt, barPaint);
            }
            foreach (var m in st.Measures)
            {
                canvas.DrawText($"S{st.Index + 1}.{m.Index + 1}", (float)m.X0 + 3, (float)st.TopY(m.X0) - ss * 1.2f, SKTextAlign.Left, font, measurePaint);
                foreach (var s in m.Symbols) DrawSymbol(canvas, s, ss, font, textPaint);
            }
        }
        using var sysPaint = new SKPaint { Color = new SKColor(0, 160, 0, 120), StrokeWidth = 3, Style = SKPaintStyle.Stroke };
        foreach (var sys in p.Systems)
            canvas.DrawRect(sys.Bounds.X0 - ss * 2, sys.Bounds.Y0 - ss * 2, sys.Bounds.Width + ss * 4, sys.Bounds.Height + ss * 4, sysPaint);
        canvas.Flush();
        ImageIO.SavePng(bmp, path);
    }

    private static void DrawSymbol(SKCanvas canvas, MusicSymbol s, float ss, SKFont font, SKPaint textPaint)
    {
        var color = s.Kind switch
        {
            SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup => new SKColor(0, 150, 0),
            SymbolKind.Rest => new SKColor(200, 120, 0),
            SymbolKind.Clef => new SKColor(120, 0, 200),
            SymbolKind.KeySignature or SymbolKind.Accidental => new SKColor(0, 150, 200),
            SymbolKind.TimeSignature => new SKColor(200, 0, 200),
            SymbolKind.Dynamic or SymbolKind.Text => new SKColor(120, 120, 120),
            SymbolKind.Unknown => new SKColor(255, 0, 0),
            _ => new SKColor(150, 150, 0),
        };
        using var paint = new SKPaint { Color = color.WithAlpha(200), StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke };
        var r = s.Bounds;
        canvas.DrawRect(r.X0, r.Y0, r.Width, r.Height, paint);
        if (s.Notes.Count > 0)
        {
            using var headPaint = new SKPaint { Color = new SKColor(0, 200, 0, 90), Style = SKPaintStyle.Fill };
            using var small = new SKFont(SKTypeface.Default, Math.Max(8, ss * 0.6f));
            foreach (var n in s.Notes)
            {
                canvas.DrawOval(new SKRect(n.Head.X0, n.Head.Y0, n.Head.X1 + 1, n.Head.Y1 + 1), headPaint);
                if (n.Pitch != null) canvas.DrawText(n.Pitch, n.Head.X1 + 2, n.Head.Y0 + ss * 0.5f, SKTextAlign.Left, small, textPaint);
            }
            string label = s.Kind == SymbolKind.NoteGroup ? $"{s.Name}" : $"{s.Duration}";
            canvas.DrawText(label, r.X0, r.Y1 + ss, SKTextAlign.Left, small, textPaint);
        }
        else
        {
            string label = s.Kind switch
            {
                SymbolKind.Rest => $"rest {s.Duration}",
                SymbolKind.Unknown => "?",
                SymbolKind.Text => "txt",
                SymbolKind.Clef => s.Attributes.GetValueOrDefault("clef", "clef"),
                SymbolKind.KeySignature => "key",
                _ => s.Name.Length > 14 ? s.Kind.ToString() : s.Name,
            };
            canvas.DrawText(label, r.X0, r.Y0 - 2, SKTextAlign.Left, font, textPaint);
        }
    }
}
