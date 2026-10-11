using System.Text;
using PVOMR.Core.Model;

namespace PVOMR.Core.Output;

public static class TextReport
{
    public static string Write(PageAnalysis p, bool verbose)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== {Path.GetFileName(p.Source)} - page {p.PageNumber}: {p.Width}x{p.Height}px @ {p.Dpi:0}dpi, skew {p.SkewDegrees:+0.00;-0.00}deg{(p.Rotated90 ? ", rotated 90deg" : "")}, {p.Binarization}, {p.Elapsed.TotalSeconds:0.0}s");
        if (p.Staves.Count == 0)
        {
            sb.AppendLine("  No staves detected on this page.");
            if (verbose) foreach (var n in p.Notes) sb.AppendLine("  note: " + n);
            return sb.ToString();
        }
        double ss = p.StaffSpace;
        sb.AppendLine($"  staff space {ss:0.0}px, line thickness {p.LineThickness:0.0}px, {p.Staves.Count} staves, {p.Systems.Count} systems, {p.Staves.Sum(s => s.Barlines.Count)} barlines, {p.Staves.Sum(s => s.Measures.Count)} measures");
        if (verbose) foreach (var n in p.Notes) sb.AppendLine("  note: " + n);

        foreach (var sys in p.Systems)
        {
            sb.AppendLine();
            sb.AppendLine($"  System {sys.Index + 1}: {sys.Staves.Count} staff/staves, x {sys.Bounds.X0}-{sys.Bounds.X1}, y {sys.Bounds.Y0}-{sys.Bounds.Y1}, {sys.MeasureBoundaries.Count} barline positions: {string.Join(" ", sys.MeasureBoundaries.Select(b => ((int)b).ToString()))}");
            foreach (var st in sys.Staves)
            {
                double xm = (st.Left + st.Right) / 2;
                string bars = string.Join(", ", st.Barlines.Select(b => $"{BarName(b.Kind)}@{b.X:0}{(b.Spanning ? $"(to staff {b.StaffTo + 1})" : "")}"));
                sb.AppendLine($"    Staff {st.Index + 1}: x {st.Left:0}-{st.Right:0}, lines y {st.TopY(xm):0}..{st.BottomY(xm):0}, space {st.Space:0.0}px, clef {st.Clef}, key {Analysis.MusicInterpreter.KeyName(st.KeyFifths)}, time {st.TimeSignature ?? "?"}, {st.Barlines.Count} barlines [{bars}]");
                foreach (var m in st.Measures)
                {
                    var syms = m.Symbols.OrderBy(s => s.Kind is SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup && s.Notes.Count > 0 ? s.Notes.Min(n => n.HeadX) : s.Bounds.CenterX).ToList();
                    int notes = syms.Where(s => s.Kind is SymbolKind.Note or SymbolKind.Chord).Count() + syms.Where(s => s.Kind == SymbolKind.NoteGroup).Sum(s => s.Children.Count);
                    int rests = syms.Count(s => s.Kind == SymbolKind.Rest);
                    sb.AppendLine($"      Measure {m.Index + 1} [x {m.X0:0}-{m.X1:0}] left {Describe(m.LeftBarline)}, right {Describe(m.RightBarline)}: {syms.Count} symbols, {notes} notes/chords, {rests} rests");
                    foreach (var s in syms) sb.AppendLine("        " + Describe(s, ss, verbose));
                }
            }
        }
        if (p.PageSymbols.Count > 0)
        {
            var big = p.PageSymbols.Where(s => s.Bounds.Width > 2 * ss || s.Bounds.Height > 2 * ss).ToList();
            sb.AppendLine();
            sb.AppendLine($"  Outside staves: {p.PageSymbols.Count} ink components (text, titles, ...), {big.Count} larger than 2 staff spaces");
        }
        return sb.ToString();
    }

    private static string Describe(Barline? b) => b == null ? "none" : BarName(b.Kind);

    public static string BarName(BarlineKind k) => k switch
    {
        BarlineKind.Single => "single", BarlineKind.Double => "double", BarlineKind.Final => "final", BarlineKind.RepeatStart => "repeat-start",
        BarlineKind.RepeatEnd => "repeat-end", BarlineKind.RepeatBoth => "repeat-both", BarlineKind.Thick => "thick", BarlineKind.SystemStart => "system-start", _ => k.ToString(),
    };

    public static string Describe(MusicSymbol s, double ss, bool verbose)
    {
        var sb = new StringBuilder();
        sb.Append($"x={s.Bounds.X0,5} ");
        switch (s.Kind)
        {
            case SymbolKind.Note:
            case SymbolKind.Chord:
                sb.Append(NoteText(s));
                break;
            case SymbolKind.NoteGroup:
                sb.Append($"{s.Name} ({s.Children.Count} stems{(s.Attributes.TryGetValue("tuplet", out var tup) ? $", tuplet {tup}" : "")}): ");
                sb.Append(string.Join(" | ", s.Children.Select(NoteText)));
                break;
            case SymbolKind.Rest:
                sb.Append($"rest {s.Duration ?? s.Name}");
                break;
            case SymbolKind.Clef:
                sb.Append($"clef {s.Attributes.GetValueOrDefault("clef", s.Name)} ({s.Name})");
                break;
            case SymbolKind.KeySignature:
                sb.Append($"key signature {ShortAcc(s.Name)} step {s.Step} -> {s.Attributes.GetValueOrDefault("key", "?")}");
                break;
            case SymbolKind.Accidental:
                sb.Append($"accidental {ShortAcc(s.Name)} (unattached) step {s.Step}");
                break;
            case SymbolKind.TimeSignature:
                sb.Append($"time signature glyph {s.Name} -> {s.Attributes.GetValueOrDefault("time", "?")}");
                break;
            case SymbolKind.Dynamic:
                sb.Append($"dynamic {s.Attributes.GetValueOrDefault("text", s.Name.Replace("dynamic", ""))}");
                break;
            case SymbolKind.Text:
                sb.Append($"text ({s.Attributes.GetValueOrDefault("glyphs", "1")} glyphs, {s.Bounds.Width / ss:0.0}x{s.Bounds.Height / ss:0.0}ss)");
                break;
            case SymbolKind.Unknown:
                sb.Append($"unknown {s.Bounds.Width / ss:0.0}x{s.Bounds.Height / ss:0.0}ss{(s.Attributes.TryGetValue("best", out var b) ? $" (closest glyph {b})" : "")}");
                break;
            default:
                sb.Append($"{s.Kind.ToString().ToLowerInvariant()} {s.Name}");
                break;
        }
        if (s.Kind != SymbolKind.Note && s.Kind != SymbolKind.Chord && s.Kind != SymbolKind.NoteGroup && s.Confidence > 0) sb.Append($" [conf {s.Confidence:0.00}]");
        foreach (var kv in s.Attributes)
            if (kv.Key is not ("clef" or "key" or "time" or "glyphs" or "best" or "alt" or "text" or "stemx" or "tuplet")) sb.Append($" {kv.Key}={kv.Value}");
        if (verbose)
        {
            sb.Append($"  bbox {s.Bounds}");
            var heads = s.Notes.Concat(s.Children.SelectMany(c => c.Notes)).ToList();
            if (heads.Count > 0) sb.Append(" heads=" + string.Join(",", heads.Select(n => $"{n.Pitch ?? "?"}@{n.Head.CenterX:0},{n.Head.CenterY:0}s{n.Step}")));
        }
        return sb.ToString();
    }

    private static string ShortAcc(string name) => name switch
    {
        "accidentalSharp" => "sharp", "accidentalFlat" => "flat", "accidentalNatural" => "natural", "accidentalDoubleSharp" => "double sharp", "accidentalDoubleFlat" => "double flat", _ => name
    };

    private static string NoteText(MusicSymbol s)
    {
        var sb = new StringBuilder();
        sb.Append(s.Kind == SymbolKind.Chord ? "chord" : "note");
        sb.Append(' ');
        sb.Append(string.Join("+", s.Notes.Select(n => (n.Pitch ?? $"step{n.Step}") + (n.AccidentalName != null ? $"({ShortAcc(n.AccidentalName)})" : "") + new string('.', n.Dots))));
        sb.Append(' ').Append(s.Duration ?? "?");
        if (s.StemUp.HasValue) sb.Append(s.StemUp.Value ? " stem-up" : " stem-down");
        if (s.Flags > 0) sb.Append($" ({s.Flags} flag/beam)");
        if (s.Attributes.TryGetValue("articulation", out var art)) sb.Append(' ').Append(art);
        return sb.ToString();
    }
}
