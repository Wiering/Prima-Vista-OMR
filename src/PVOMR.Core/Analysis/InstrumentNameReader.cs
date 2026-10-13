using PVOMR.Core.Imaging;
using PVOMR.Core.Model;

namespace PVOMR.Core.Analysis;

/// <summary>
/// Reads the instrument names printed left of the staves (full names on the first system, abbreviations later) and
/// the text above the first system (title, composer, and the instrument of a single part) with an OCR engine.
/// </summary>
public static class InstrumentNameReader
{
    public static void Read(GrayImage gray, PageAnalysis page, ITextRecognizer ocr)
    {
        if (page.Systems.Count == 0) return;
        double ss = page.StaffSpace > 0 ? page.StaffSpace : 20;
        // small print is read more reliably when enlarged, but the best size depends on the print: the margin is read
        // at about 25 and 35 px per staff space and per staff the reading that names an instrument wins
        double Scale(double target) => ss < target - 3 ? Math.Min(4, target / ss) : 1;
        var scales = new[] { Scale(25), Scale(35) }.Distinct().ToArray();
        bool dump = Environment.GetEnvironmentVariable("PVOMR_OCRDUMP") == "1";

        foreach (var sys in page.Systems)
        {
            double left = sys.Staves.Min(s => s.Left);
            int x1 = (int)Math.Round(left - 0.4 * ss);
            if (x1 < 2 * ss) continue;   // no margin to the left of the staves
            int y0 = (int)Math.Max(0, sys.Staves[0].TopY(left) - 2.0 * ss);
            int y1 = (int)Math.Min(gray.Height - 1, sys.Staves[^1].BottomY(left) + 2.0 * ss);
            var candidates = sys.Staves.ToDictionary(s => s, _ => new List<string>());
            foreach (double scale in scales)
            {
                var lines = Recognize(gray, ocr, new Rect(0, y0, x1, y1), scale);
                if (dump) foreach (var l in lines) page.Notes.Add($"ocr system {sys.Index + 1} x{scale:0.0}: \"{l.Text}\" at {l.Bounds}");
                // each line goes to the staff whose middle is nearest (a name centred between a braced pair goes to the upper one)
                var assigned = new Dictionary<Staff, List<TextLine>>();
                foreach (var line in lines)
                {
                    string text = Clean(line.Text);
                    if (text.Length < 1 || !text.Any(char.IsLetter)) continue;
                    double cy = line.Bounds.CenterY;
                    Staff best = sys.Staves.OrderBy(s => Math.Abs((s.TopY(left) + s.BottomY(left)) / 2 - cy)).First();
                    int bi = sys.Staves.IndexOf(best);
                    // between the staves of a braced pair ("Piano" centred on the brace): the upper staff names the part
                    if (bi > 0 && sys.Staves[bi - 1].BraceToNext && cy < best.TopY(left) - 0.5 * ss) best = sys.Staves[bi - 1];
                    // a line far from every staff (a system heading, a page number) is not a name
                    if (cy < best.TopY(left) - 2.5 * ss || cy > best.BottomY(left) + 2.5 * ss)
                        if (!(best.BraceToNext && cy < sys.Staves[bi + 1 < sys.Staves.Count ? bi + 1 : bi].TopY(left))) continue;
                    if (!assigned.TryGetValue(best, out var list)) assigned[best] = list = new List<TextLine>();
                    list.Add(line with { Text = text });
                }
                foreach (var (staff, list) in assigned)
                    candidates[staff].Add(string.Join(" ", list.OrderBy(l => l.Bounds.Y0).Select(l => l.Text)));
            }
            // short names ("Fl.", "Cl.") are often skipped in the large margin crop: read each unnamed staff's own band
            for (int k = 0; k < sys.Staves.Count; k++)
            {
                var st = sys.Staves[k];
                if (candidates[st].Any(c => Score.Instruments.Match(c) != null) || k > 0 && sys.Staves[k - 1].BraceToNext) continue;
                double top = st.TopY(left), bot = st.BottomY(left);
                double upper = k > 0 ? (sys.Staves[k - 1].BottomY(left) + top) / 2 : top - 2 * ss;
                double lower = k + 1 < sys.Staves.Count && !st.BraceToNext ? (bot + sys.Staves[k + 1].TopY(left)) / 2 : bot + 2 * ss;
                if (st.BraceToNext && k + 1 < sys.Staves.Count) lower = sys.Staves[k + 1].BottomY(left);
                int xb = (int)Math.Round(left - 1.8 * ss);   // short of brackets and braces
                if (xb < 2 * ss) continue;
                var band = new Rect(0, (int)Math.Max(0, Math.Max(upper, top - 2 * ss)), xb, (int)Math.Min(gray.Height - 1, Math.Min(lower, bot + 2 * ss + (st.BraceToNext ? 20 * ss : 0))));
                foreach (var t in Recognize(gray, ocr, band, scales[^1], pad: true).Select(l => Clean(l.Text)).Where(t => t.Any(char.IsLetter)))
                {
                    if (dump) page.Notes.Add($"ocr staff {st.Index + 1}: \"{t}\"");
                    candidates[st].Add(t);
                }
            }
            // per staff: a reading that names an instrument exactly, then one that needs OCR tolerance, then the longest
            foreach (var (staff, list) in candidates)
            {
                if (list.Count == 0) continue;
                int Rank(string t) => Score.Instruments.Match(t, out _, out bool fz) == null ? 0 : fz ? 1 : 2;
                staff.InstrumentName = list.OrderByDescending(Rank).ThenByDescending(t => t.Length).First();
            }
        }

        // the text above the first system
        var first = page.Systems[0];
        double topY = first.Staves[0].TopY(first.Staves[0].Left);
        int headerBottom = (int)Math.Round(topY - 2.5 * ss);
        if (headerBottom > 3 * ss)
            foreach (var line in Recognize(gray, ocr, new Rect(0, 0, gray.Width - 1, headerBottom), scales[0]))
            {
                string text = Clean(line.Text);
                if (text.Any(char.IsLetter)) page.HeaderText.Add(text);
            }
    }

    /// <summary>OCR of a region (enlarged by <paramref name="scale"/>); bounds are returned in page coordinates.</summary>
    internal static IReadOnlyList<TextLine> Recognize(GrayImage gray, ITextRecognizer ocr, Rect r, double scale, bool pad = false)
    {
        if (pad)
        {
            // a lone word: crop to its ink (dark pixels) so the engine sees a line of text rather than an empty page
            int ix0 = int.MaxValue, iy0 = int.MaxValue, ix1 = -1, iy1 = -1;
            for (int y = r.Y0; y <= r.Y1; y++)
                for (int x = r.X0; x <= r.X1; x++)
                    if (gray.Data[y * gray.Width + x] < 128) { ix0 = Math.Min(ix0, x); ix1 = Math.Max(ix1, x); iy0 = Math.Min(iy0, y); iy1 = Math.Max(iy1, y); }
            if (ix1 < 0) return Array.Empty<TextLine>();
            r = new Rect(ix0, iy0, ix1, iy1);
        }
        int w0 = r.Width, h0 = r.Height;
        if (w0 < 4 || h0 < 6) return Array.Empty<TextLine>();
        // a white border around a small crop helps the engine find a lone short word
        int border = pad ? Math.Max(16, h0 / 2) : 0;
        int w = w0 + 2 * border, h = h0 + 2 * border;
        var crop = new GrayImage(w, h);
        if (border > 0) Array.Fill(crop.Data, (byte)255);
        for (int y = 0; y < h0; y++) Array.Copy(gray.Data, (r.Y0 + y) * gray.Width + r.X0, crop.Data, (y + border) * w + border, w0);
        r = new Rect(r.X0 - border, r.Y0 - border, r.X1 + border, r.Y1 + border);
        var img = scale > 1.01 ? crop.Resize((int)Math.Round(w * scale), (int)Math.Round(h * scale)) : crop;
        if (Environment.GetEnvironmentVariable("PVOMR_OCRSAVE") is string dir && dir.Length > 0)
        {
            var hdr = System.Text.Encoding.ASCII.GetBytes($"P5 {img.Width} {img.Height} 255 ");
            File.WriteAllBytes(Path.Combine(dir, $"ocr_{r.X0}_{r.Y0}_{img.Width}x{img.Height}.pgm"), hdr.Concat(img.Data).ToArray());
        }
        IReadOnlyList<TextLine> lines;
        try { lines = ocr.Recognize(img.Data, img.Width, img.Height); }
        catch (Exception) { return Array.Empty<TextLine>(); }
        Rect Back(Rect b) => new((int)(b.X0 / scale) + r.X0, (int)(b.Y0 / scale) + r.Y0, (int)(b.X1 / scale) + r.X0, (int)(b.Y1 / scale) + r.Y0);
        return lines.Select(l => new TextLine(l.Text, Back(l.Bounds), l.Words.Select(wd => (wd.Text, Back(wd.Bounds))).ToList())).ToList();
    }

    /// <summary>Drops brace and bracket glyphs read as characters and stray punctuation at the ends.</summary>
    private static string Clean(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(p => p.Trim('{', '}', '[', ']', '|', '(', ')', '/', '\\', '_', '~', '-', ',', ';', ':', '\'', '"'))
                        .Where(p => p.Length > 0 && !(p.Length == 1 && !char.IsLetterOrDigit(p[0])));
        var words = parts.ToList();
        if (words.Count >= 2)
        {
            // a Roman numeral the OCR read as letters or digits after the name ("Violino u." for "Violino II.")
            string last = words[^1].TrimEnd('.');
            string? roman = last switch { "u" or "ll" or "Il" or "lI" or "n" or "11" or "U" => "II", "l" or "1" => "I", "lll" or "111" or "Ill" => "III", _ => null };
            if (roman != null && words[^2].Count(char.IsLetter) >= 3) words[^1] = roman + (words[^1].EndsWith('.') ? "." : "");
        }
        // a full word keeps no trailing full stop ("Flauti." -> "Flauti"); abbreviations ("Fl.", "Vc.") keep theirs
        if (words.Count > 0 && words[^1].EndsWith('.'))
        {
            string w = words[^1].TrimEnd('.');
            if (w.Count(char.IsLetter) >= 5 || w.Count(char.IsLetter) == 4 && !Score.Instruments.IsAbbreviation(w)) words[^1] = w;
        }
        return string.Join(" ", words).Trim();
    }
}
