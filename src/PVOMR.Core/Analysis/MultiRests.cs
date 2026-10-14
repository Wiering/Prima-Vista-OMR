using System.Text.RegularExpressions;
using PVOMR.Core.Model;

namespace PVOMR.Core.Analysis;

/// <summary>
/// Multi-measure rests in parts: a measure holding only rests, with the number of measures printed above it, or the
/// old "church rest" blocks (a block filling one space between two lines = 2 measures, two spaces = 4, a whole rest
/// = 1; "3" is a 2-block and a whole rest). The measure gets <see cref="Measure.MultiRest"/>; the assembler writes
/// that many measures.
/// </summary>
public static class MultiRests
{
    private static readonly Regex DigitName = new(@"^(?:tuplet|timeSig)(\d)$");

    public static int Detect(PageAnalysis page)
    {
        int found = 0;
        foreach (var sys in page.Systems.Where(s => s.Staves.Count == 1))
        {
            var st = sys.Staves[0];
            double ss = st.Space;
            foreach (var m in st.Measures)
            {
                if (m.Symbols.Any(s => s.Kind is SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup)) continue;
                double cx(MusicSymbol s) => s.Bounds.CenterX;
                // rest signs stand after the clef, key and time signature of the measure
                double signsEnd = m.Symbols.Where(s => s.Kind is SymbolKind.Clef or SymbolKind.KeySignature or SymbolKind.TimeSignature
                                                       && s.Bounds.CenterY > st.TopY(s.Bounds.CenterX) && s.Bounds.CenterY < st.BottomY(s.Bounds.CenterX))
                                           .Select(s => (double)s.Bounds.X1).DefaultIfEmpty(double.MinValue).Max();
                bool Inside(MusicSymbol s) => s.Bounds.X0 > signsEnd && s.Bounds.Y0 >= st.TopY(cx(s)) - 0.3 * ss && s.Bounds.Y1 <= st.BottomY(cx(s)) + 0.3 * ss;
                // blocks: solid bars between two lines (one space: 2 measures, two spaces: 4)
                var blocks = m.Symbols.Where(s => s.Kind is not (SymbolKind.Clef or SymbolKind.KeySignature or SymbolKind.TimeSignature or SymbolKind.Barline)
                                                  && Inside(s) && s.Bounds.Width >= 0.3 * ss && s.Bounds.Width <= 1.1 * ss
                                                  && (Between(s.Bounds.Height, 0.8, 1.3, ss) || Between(s.Bounds.Height, 1.8, 2.35, ss))).ToList();
                var wholes = m.Symbols.Where(s => s.Kind == SymbolKind.Rest && s.Duration == "whole").ToList();
                // a modern multi-rest: a thick horizontal bar over the middle line, often with serifs at its ends reaching
                // from the second to the fourth line (the H shape)
                var hbar = m.Symbols.Where(s => Inside(s) && s.Bounds.Width >= 2.0 * ss && s.Bounds.Height >= 0.35 * ss && s.Bounds.Height <= 2.4 * ss
                                                && (s.Bounds.Height <= 1.3 * ss || Math.Abs(s.Bounds.CenterY - st.CenterY) <= 0.5 * ss)
                                                && s.Kind is SymbolKind.Unknown or SymbolKind.Ledger or SymbolKind.Rest or SymbolKind.Text).ToList();
                // the number above the staff over the rest
                var digits = m.Symbols.Where(s => DigitName.IsMatch(s.Name ?? "") && s.Bounds.CenterY < st.TopY(cx(s)) - 0.2 * ss
                                                  && s.Bounds.CenterY > st.TopY(cx(s)) - 4 * ss).OrderBy(s => s.Bounds.X0).ToList();
                if (blocks.Count + wholes.Count + hbar.Count == 0) continue;
                // anything else that sounds (a rest of another value) means this is an ordinary measure
                if (m.Symbols.Any(s => s.Kind == SymbolKind.Rest && s.Duration != "whole" && !blocks.Contains(s) && !hbar.Contains(s))) continue;
                int n = 0;
                if (digits.Count is >= 1 and <= 3 && digits.All(d => Math.Abs(d.Bounds.CenterY - digits[0].Bounds.CenterY) < 0.6 * ss))
                    n = int.Parse(string.Concat(digits.Select(d => DigitName.Match(d.Name!).Groups[1].Value)));
                int fromSigns = blocks.Sum(b => b.Bounds.Height >= 1.6 * ss ? 4 : 2) + wholes.Count;
                if (n < 2) n = hbar.Count > 0 ? 0 : fromSigns;
                if (n < 2) continue;
                m.MultiRest = n;
                var parts = blocks.Concat(wholes).Concat(hbar).Concat(digits).ToList();
                foreach (var s in parts) m.Symbols.Remove(s);
                // one symbol stands for the whole rest, so that it shows (and can be inspected) in the GUI
                var all = parts.Where(s => !digits.Contains(s)).Select(s => s.Bounds).ToList();
                var marker = new MusicSymbol { Kind = SymbolKind.Rest, Name = "multiRest", Staff = st, Confidence = 0.9, Duration = $"{n} bars",
                                               Bounds = all.Skip(1).Aggregate(all[0], (a, r) => a.Union(r)) };
                marker.Attributes["measures"] = n.ToString();
                m.Symbols.Add(marker);
                page.Notes.Add($"staff {st.Index + 1} measure {m.Index + 1}: rest of {n} measures");
                found++;
            }
        }
        return found;
    }

    private static bool Between(double h, double lo, double hi, double ss) => h >= lo * ss && h <= hi * ss;
}
