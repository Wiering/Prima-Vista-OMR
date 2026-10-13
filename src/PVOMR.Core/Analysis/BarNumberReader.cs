using System.Text.RegularExpressions;
using PVOMR.Core.Imaging;
using PVOMR.Core.Model;

namespace PVOMR.Core.Analysis;

/// <summary>
/// Reads the bar numbers printed above the start of each system (plain or in a box, as in many orchestral parts). The
/// assembler checks its own measure count against them and starts its numbering from the first one.
/// </summary>
public static class BarNumberReader
{
    private static readonly Regex Number = new(@"^\D{0,2}(\d{1,4})\D{0,2}$");

    public static void Read(GrayImage gray, PageAnalysis page, ITextRecognizer ocr)
    {
        foreach (var sys in page.Systems)
        {
            var st = sys.Staves[0];
            double ss = st.Space;
            int x0 = (int)Math.Max(0, st.Left - 1.5 * ss), x1 = (int)Math.Min(gray.Width - 1, st.Left + 6 * ss);
            int y1 = (int)Math.Round(st.TopY(st.Left) - 0.6 * ss), y0 = (int)Math.Max(0, y1 - 4.5 * ss);
            if (y1 - y0 < ss) continue;
            // the number sits over the clef: only ink clear of the staff (ledger lines and high notes stay out of the band)
            foreach (double scale in new[] { 40.0 / ss, 25.0 / ss, 60.0 / ss })
            {
                var lines = InstrumentNameReader.Recognize(gray, ocr, new Rect(x0, y0, x1, y1), Math.Max(1, scale), pad: true);
                var m = lines.Select(l => Number.Match(l.Text.Replace(" ", "").Replace("O", "0").Replace("o", "0").Replace("l", "1").Replace("I", "1")))
                             .FirstOrDefault(mm => mm.Success);
                if (m == null) continue;
                int n = int.Parse(m.Groups[1].Value);
                if (n < 1 || n > 5000) continue;
                st.BarNumber = n;
                page.Notes.Add($"staff {st.Index + 1}: bar number {n}");
                break;
            }
        }
    }

    /// <summary>The region above the start of a staff where its bar number is printed.</summary>
    private static Rect Region(Staff st)
    {
        double ss = st.Space;
        int y1 = (int)Math.Round(st.TopY(st.Left) - 0.6 * ss);
        return new Rect((int)(st.Left - 1.5 * ss), (int)(y1 - 4.5 * ss), (int)(st.Left + 6 * ss), y1);
    }

    private static readonly Regex DigitName = new(@"^(?:tuplet|timeSig)(\d)$");

    /// <summary>
    /// After symbol analysis: systems whose bar number the OCR missed (a lone digit in a box often is) take it from the
    /// digits the glyph classifier found there (read as tuplet or time-signature digits), left to right.
    /// </summary>
    public static void FromSymbols(PageAnalysis page)
    {
        foreach (var sys in page.Systems)
        {
            var st = sys.Staves[0];
            if (st.BarNumber != null) { Replace(page, st); continue; }
            var r = Region(st);
            var digits = st.Measures.SelectMany(m => m.Symbols).Concat(page.PageSymbols)
                           .Where(s => DigitName.IsMatch(s.Name ?? "") && s.Bounds.CenterX >= r.X0 && s.Bounds.CenterX <= r.X1 && s.Bounds.CenterY >= r.Y0 && s.Bounds.CenterY <= r.Y1)
                           .OrderBy(s => s.Bounds.X0).ToList();
            if (digits.Count == 0 || digits.Count > 3) continue;
            // one number: digits side by side at one height
            if (digits.Any(d => Math.Abs(d.Bounds.CenterY - digits[0].Bounds.CenterY) > 0.5 * st.Space)) continue;
            int n = int.Parse(string.Concat(digits.Select(d => DigitName.Match(d.Name!).Groups[1].Value)));
            if (n < 1) continue;
            st.BarNumber = n;
            page.Notes.Add($"staff {st.Index + 1}: bar number {n} (from digit glyphs)");
            Replace(page, st);
        }
    }

    /// <summary>The digits (and the box) of a bar number were classified as tuplet or time-signature digits, breath
    /// marks, text or unknown symbols: they become one symbol, "bar n".</summary>
    private static void Replace(PageAnalysis page, Staff st)
    {
        var r = Region(st);
        bool Part(MusicSymbol s) => s.Kind is SymbolKind.Tuplet or SymbolKind.TimeSignature or SymbolKind.Breath or SymbolKind.Text or SymbolKind.Unknown
                                              or SymbolKind.Number or SymbolKind.Articulation or SymbolKind.Dot
                                    && s.Bounds.X0 >= r.X0 - st.Space && s.Bounds.X1 <= r.X1 && s.Bounds.Y0 >= r.Y0 - st.Space && s.Bounds.Y1 <= r.Y1 + 0.3 * st.Space;
        var parts = new List<(List<MusicSymbol> list, MusicSymbol s)>();
        foreach (var m in st.Measures) foreach (var s in m.Symbols.Where(Part)) parts.Add((m.Symbols, s));
        foreach (var s in page.PageSymbols.Where(Part)) parts.Add((page.PageSymbols, s));
        if (parts.Count == 0) return;
        foreach (var (list, s) in parts) list.Remove(s);
        var bounds = parts.Skip(1).Aggregate(parts[0].s.Bounds, (a, p) => a.Union(p.s.Bounds));
        var number = new MusicSymbol { Kind = SymbolKind.Number, Name = "barNumber", Staff = st, Bounds = bounds, Confidence = 0.9, Duration = $"bar {st.BarNumber}" };
        number.Attributes["bar"] = st.BarNumber!.Value.ToString();
        (st.Measures.Count > 0 ? st.Measures[0].Symbols : page.PageSymbols).Add(number);
    }
}
