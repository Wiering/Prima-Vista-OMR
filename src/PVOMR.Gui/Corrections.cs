using System.Text.Json;
using System.Text.Json.Serialization;
using PVOMR.Core.Model;

namespace PVOMR.Gui;

/// <summary>Corrections of one score, saved next to it as "&lt;score&gt;.pvomr.json" and replayed after every recognition.</summary>
public sealed class CorrectionFile
{
    public int Version { get; set; } = 1;
    public string Source { get; set; } = "";
    public List<PageCorrections> Pages { get; set; } = new();
}

public sealed class PageCorrections
{
    public int Page { get; set; }
    public List<StaffOverride> Staves { get; set; } = new();
    public List<SymbolEdit> Edits { get; set; } = new();
}

public sealed class StaffOverride
{
    public int Staff { get; set; }
    public string? Clef { get; set; }
    public int? Key { get; set; }
    public string? Time { get; set; }
}

/// <summary>
/// One edit. The symbol is identified by the staff index and the bounds it had when it was first seen (at recognition,
/// or the bounds it was added with), so the log stays valid when the same page is recognised again.
/// </summary>
public sealed class SymbolEdit
{
    /// <summary>delete | set | head | removeHead | add | move</summary>
    public string Op { get; set; } = "";
    /// <summary>Staff index on the page, -1 for symbols outside the staves.</summary>
    public int Staff { get; set; } = -1;
    public int X0 { get; set; }
    public int Y0 { get; set; }
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public string? Kind { get; set; }
    public string? Duration { get; set; }
    public string? Stem { get; set; }
    public int? Dots { get; set; }
    public int? Head { get; set; }
    public int? Step { get; set; }
    public int? Dx { get; set; }
    public int? Dy { get; set; }

    [JsonIgnore] public Rect Bounds { get => new(X0, Y0, X1, Y1); set { X0 = value.X0; Y0 = value.Y0; X1 = value.X1; Y1 = value.Y1; } }

    public override string ToString() => $"{Op} staff {Staff + 1} {Bounds}";
}

public sealed class CorrectionStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly CorrectionFile _file;

    public string Path { get; }

    private CorrectionStore(string path, CorrectionFile file) { Path = path; _file = file; }

    public static string SidecarPath(string scoreFile) => scoreFile + ".pvomr.json";

    public static CorrectionStore Load(string scoreFile)
    {
        string path = SidecarPath(scoreFile);
        CorrectionFile? file = null;
        if (File.Exists(path))
        {
            try { file = JsonSerializer.Deserialize<CorrectionFile>(File.ReadAllText(path), JsonOpts); }
            catch { file = null; }
        }
        file ??= new CorrectionFile { Source = System.IO.Path.GetFileName(scoreFile) };
        return new CorrectionStore(path, file);
    }

    public int Count => _file.Pages.Sum(p => p.Edits.Count + p.Staves.Count);
    public bool HasPage(int page) => _file.Pages.Any(p => p.Page == page && (p.Edits.Count > 0 || p.Staves.Count > 0));

    public PageCorrections PageFor(int page)
    {
        var pc = _file.Pages.FirstOrDefault(p => p.Page == page);
        if (pc == null) { pc = new PageCorrections { Page = page }; _file.Pages.Add(pc); _file.Pages.Sort((a, b) => a.Page.CompareTo(b.Page)); }
        return pc;
    }

    public void Record(int page, SymbolEdit edit) { PageFor(page).Edits.Add(edit); Save(); }

    public void SetStaffOverride(int page, int staffIndex, ClefKind? clef, int? key, string? time)
    {
        var pc = PageFor(page);
        pc.Staves.RemoveAll(s => s.Staff == staffIndex);
        if (clef != null || key != null || time != null) pc.Staves.Add(new StaffOverride { Staff = staffIndex, Clef = clef?.ToString(), Key = key, Time = time });
        Save();
    }

    public void Clear(int page) { _file.Pages.RemoveAll(p => p.Page == page); Save(); }

    public void Save()
    {
        _file.Pages.RemoveAll(p => p.Edits.Count == 0 && p.Staves.Count == 0);
        if (_file.Pages.Count == 0) { if (File.Exists(Path)) File.Delete(Path); return; }
        File.WriteAllText(Path, JsonSerializer.Serialize(_file, JsonOpts));
    }

    // ------------------------------------------------------------------ replay

    /// <summary>Applies the stored staff overrides and edits of a page to a fresh recognition result.</summary>
    public int ApplyAll(PageResult page)
    {
        var pc = _file.Pages.FirstOrDefault(p => p.Page == page.PageNumber);
        if (pc == null) return 0;
        int applied = 0, failed = 0;
        foreach (var so in pc.Staves)
        {
            if (so.Staff < 0 || so.Staff >= page.Analysis.Staves.Count) { failed++; continue; }
            var st = page.Analysis.Staves[so.Staff];
            st.ForcedClef = so.Clef != null && Enum.TryParse<ClefKind>(so.Clef, out var ck) ? ck : null;
            st.ForcedKey = so.Key;
            st.ForcedTime = so.Time;
            applied++;
        }
        foreach (var e in pc.Edits)
        {
            if (Apply(page, e)) applied++; else failed++;
        }
        if (failed > 0) page.Analysis.Notes.Add($"{failed} stored correction(s) could not be applied (symbol not found after recognition)");
        return applied;
    }

    /// <summary>Note heads of a symbol in inspector order: its own heads, or those of its children (beamed groups).</summary>
    public static List<Note> Heads(MusicSymbol s) => s.Notes.Count > 0 ? s.Notes : s.Children.SelectMany(c => c.Notes).ToList();

    public static MusicSymbol? Find(PageResult page, SymbolEdit e)
    {
        var target = e.Bounds;
        IEnumerable<MusicSymbol> candidates;
        if (e.Staff >= 0 && e.Staff < page.Analysis.Staves.Count)
            candidates = page.Analysis.Staves[e.Staff].Measures.SelectMany(m => m.Symbols).SelectMany(s => new[] { s }.Concat(s.Children));
        else if (e.Staff < 0) candidates = page.Analysis.PageSymbols;
        else return null;
        MusicSymbol? best = null; double bestIou = 0.6;
        foreach (var s in candidates)
        {
            var origin = page.Origins.TryGetValue(s, out var o) ? o : s.Bounds;
            if (origin == target) return s;
            double iou = Iou(origin, target);
            if (iou > bestIou) { bestIou = iou; best = s; }
        }
        return best;
    }

    private static double Iou(Rect a, Rect b)
    {
        int ix = Math.Min(a.X1, b.X1) - Math.Max(a.X0, b.X0) + 1, iy = Math.Min(a.Y1, b.Y1) - Math.Max(a.Y0, b.Y0) + 1;
        if (ix <= 0 || iy <= 0) return 0;
        double inter = (double)ix * iy;
        return inter / ((double)a.Width * a.Height + (double)b.Width * b.Height - inter);
    }

    /// <summary>Performs one edit on the page. Returns false when its symbol cannot be found.</summary>
    public static bool Apply(PageResult page, SymbolEdit e)
    {
        var a = page.Analysis;
        if (e.Op == "add")
        {
            if (e.Staff < 0 || e.Staff >= a.Staves.Count) return false;
            var staff = a.Staves[e.Staff];
            var bounds = e.Bounds;
            var sym = new MusicSymbol { Bounds = bounds, Staff = staff, Confidence = 1, Duration = e.Duration ?? "quarter" };
            sym.Attributes["added"] = "1";
            if (e.Kind == "Rest") { sym.Kind = SymbolKind.Rest; sym.Name = "rest" + RestSuffix(sym.Duration!); }
            else
            {
                sym.Kind = SymbolKind.Note; sym.Name = "note"; sym.StemUp = e.Stem != "down";
                int step = e.Step ?? (int)Math.Round(staff.StepAt(bounds.CenterX, bounds.CenterY));
                sym.Notes.Add(new Note { Head = bounds, Step = step, HeadType = sym.Duration == "whole" ? "whole" : sym.Duration == "half" ? "half" : "black" });
            }
            sym.Dots = e.Dots ?? 0;
            var measure = MeasureAt(staff, bounds.CenterX);
            if (measure == null) return false;
            measure.Symbols.Add(sym);
            page.Origins[sym] = bounds;
            return true;
        }
        var s = Find(page, e);
        if (s == null) return false;
        switch (e.Op)
        {
            case "delete":
                foreach (var st in a.Staves)
                    foreach (var m in st.Measures)
                    {
                        if (m.Symbols.Remove(s)) return true;
                        foreach (var p in m.Symbols) if (p.Children.Remove(s)) return true;
                    }
                return a.PageSymbols.Remove(s);
            case "set":
                if (e.Kind != null && Enum.TryParse<SymbolKind>(e.Kind, out var k)) s.Kind = k;
                if (e.Duration != null) { s.Duration = e.Duration; if (s.Kind == SymbolKind.Rest) s.Name = "rest" + RestSuffix(e.Duration); }
                if (e.Dots != null) { s.Dots = e.Dots.Value; foreach (var n in s.Notes) n.Dots = e.Dots.Value; }
                if (e.Stem != null) s.StemUp = e.Stem switch { "up" => true, "down" => false, _ => null };
                s.Attributes["corrected"] = "1";
                return true;
            case "head":
            {
                var heads = Heads(s);
                if (e.Head is not int i || i < 0 || i >= heads.Count || e.Step == null) return false;
                heads[i].Step = e.Step.Value;
                s.Attributes["corrected"] = "1";
                return true;
            }
            case "removeHead":
            {
                var heads = Heads(s);
                if (e.Head is not int i || i < 0 || i >= heads.Count) return false;
                var n = heads[i];
                s.Notes.Remove(n);
                foreach (var c in s.Children) c.Notes.Remove(n);
                s.Attributes["corrected"] = "1";
                return true;
            }
            case "move":
                Shift(page, s, e.Dx ?? 0, e.Dy ?? 0);
                s.Attributes["corrected"] = "1";
                return true;
            default:
                return false;
        }
    }

    private static string RestSuffix(string duration) => duration switch
    {
        "whole" => "Whole", "half" => "Half", "quarter" => "Quarter", "8th" => "8th", "16th" => "16th", "32nd" => "32nd", "64th" => "64th", _ => "Quarter",
    };

    public static Measure? MeasureAt(Staff staff, double x)
    {
        if (staff.Measures.Count == 0) return null;
        return staff.Measures.FirstOrDefault(m => x >= m.X0 && x <= m.X1) ?? staff.Measures.OrderBy(m => Math.Min(Math.Abs(x - m.X0), Math.Abs(x - m.X1))).First();
    }

    /// <summary>Moves a symbol (with its heads and children) by whole pixels; note steps follow the new head positions and the symbol changes measure when it crosses a bar line.</summary>
    public static void Shift(PageResult page, MusicSymbol s, int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        static Rect Moved(Rect r, int dx, int dy) => new(r.X0 + dx, r.Y0 + dy, r.X1 + dx, r.Y1 + dy);
        void ShiftOne(MusicSymbol m)
        {
            m.Bounds = Moved(m.Bounds, dx, dy);
            foreach (var n in m.Notes)
            {
                n.Head = Moved(n.Head, dx, dy);
                if (m.Staff != null) n.Step = (int)Math.Round(m.Staff.StepAt(n.Head.CenterX, n.Head.CenterY));
            }
            if (m.Step != null && m.Staff != null) m.Step = (int)Math.Round(m.Staff.StepAt(m.Bounds.CenterX, m.Bounds.CenterY));
        }
        ShiftOne(s);
        foreach (var c in s.Children) ShiftOne(c);
        var staff = s.Staff;
        if (staff == null || dx == 0) return;
        var from = staff.Measures.FirstOrDefault(m => m.Symbols.Contains(s));
        var to = MeasureAt(staff, s.Bounds.CenterX);
        if (from != null && to != null && from != to) { from.Symbols.Remove(s); to.Symbols.Add(s); }
    }

    /// <summary>Records the bounds every symbol has right after recognition: the identity used by the edit log.</summary>
    public static void RecordOrigins(PageResult page)
    {
        page.Origins.Clear();
        foreach (var st in page.Analysis.Staves)
            foreach (var m in st.Measures)
                foreach (var s in m.Symbols)
                {
                    page.Origins[s] = s.Bounds;
                    foreach (var c in s.Children) page.Origins[c] = c.Bounds;
                }
        foreach (var s in page.Analysis.PageSymbols) page.Origins[s] = s.Bounds;
    }
}
