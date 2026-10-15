using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using PVOMR.Core.Analysis;
using PVOMR.Core.Imaging;
using PVOMR.Core.Model;
using PVOMR.Core.Score;
using PVOMR.Merge;
using Rect = PVOMR.Core.Model.Rect;

namespace PVOMR.Gui;

/// <summary>One version of the music: a PDF (recognised here, with its stored corrections) or a MusicXML file, and the
/// part and staff taken from it.</summary>
public sealed class MergeSource
{
    public string File { get; set; } = "";
    public int Part { get; set; } = 1;
    /// <summary>0 = the whole part.</summary>
    public int Staff { get; set; }
}

/// <summary>A choice made by the user: the row and the bar chosen in it (by its content, so it is dropped when the bar changes).</summary>
public sealed class MergeChoice
{
    public int Row { get; set; }
    public int Source { get; set; }
    public string Key { get; set; } = "";
}

public sealed class MergeProject
{
    public List<MergeSource> Sources { get; set; } = new();
    public List<MergeChoice> Choices { get; set; } = new();
}

/// <summary>
/// Merges several versions of the same music: every version is recognised (or read), the bars are aligned and the best
/// version of each is chosen automatically; bars without a clear choice are shown with every version side by side, as a
/// crop of the scanned page where there is one, to be chosen by a click. The merged music is exported as MusicXML.
/// </summary>
public sealed class MergeWindow : Window
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly Func<RecognitionEngine> _engine;
    private MergeProject _project = new();
    private string? _projectPath;
    private readonly StackPanel _sourcesPanel = new() { Spacing = 4 };
    private readonly StackPanel _rowsPanel = new() { Spacing = 10, Margin = new Thickness(0, 6, 12, 6) };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly CheckBox _showAll = new() { Content = "Show all bars that differ" };
    private readonly Button _compare, _export;
    private List<Strand> _strands = new();
    private List<MergeRow> _rows = new();
    /// <summary>Per strand: the page image and place on it of every bar, for the crops.</summary>
    private readonly List<Dictionary<ScoreMeasure, (GrayImage page, Rect area)>> _crops = new();

    public MergeWindow(Func<RecognitionEngine> engine)
    {
        _engine = engine;
        Title = "PVOMR – merge versions";
        Width = 1400; Height = 900;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var add = new Button { Content = "Add source…" };
        add.Click += async (_, _) => await AddSourceAsync();
        _compare = new Button { Content = "Compare" };
        _compare.Click += async (_, _) => await CompareAsync();
        _export = new Button { Content = "Export merged MusicXML…", IsEnabled = false };
        _export.Click += async (_, _) => await ExportAsync();
        var open = new Button { Content = "Open project…" };
        open.Click += async (_, _) => await OpenProjectAsync();
        var save = new Button { Content = "Save project…" };
        save.Click += async (_, _) => await SaveProjectAsync(ask: _projectPath == null);
        var next = new Button { Content = "Next flagged" };
        next.Click += (_, _) => ScrollToNextFlagged();
        _showAll.IsCheckedChanged += (_, _) => ShowRows();
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        foreach (var c in new Control[] { open, save, add, _compare, next, _showAll, _export, _status }) { c.Margin = new Thickness(0, 0, 6, 4); bar.Children.Add(c); }
        var top = new StackPanel { Margin = new Thickness(6, 0, 6, 0) };
        top.Children.Add(new TextBlock { Text = "Sources (part and staff to take; staff 0 = the whole part)", FontWeight = FontWeight.Bold });
        top.Children.Add(_sourcesPanel);
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); dock.Children.Add(bar);
        DockPanel.SetDock(top, Dock.Top); dock.Children.Add(top);
        dock.Children.Add(new ScrollViewer { Content = _rowsPanel, Margin = new Thickness(6, 0, 0, 0) });
        Content = dock;
        ShowSources();
        Status("Add two or more versions of the same music (PDF or MusicXML), then Compare.");
    }

    private void Status(string text) => _status.Text = text;

    // ------------------------------------------------------------------ sources

    private void ShowSources()
    {
        _sourcesPanel.Children.Clear();
        for (int i = 0; i < _project.Sources.Count; i++)
        {
            var src = _project.Sources[i];
            int idx = i;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock { Text = $"{(char)('A' + i)}", FontWeight = FontWeight.Bold, Width = 16, VerticalAlignment = VerticalAlignment.Center });
            var name = new TextBlock { Text = Path.GetFileName(src.File), Width = 420, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(name, src.File);
            row.Children.Add(name);
            row.Children.Add(new TextBlock { Text = "part", VerticalAlignment = VerticalAlignment.Center });
            var part = new NumericUpDown { Minimum = 1, Maximum = 99, Value = src.Part, Width = 110, FormatString = "0" };
            part.ValueChanged += (_, e) => src.Part = (int)(e.NewValue ?? 1);
            row.Children.Add(part);
            row.Children.Add(new TextBlock { Text = "staff", VerticalAlignment = VerticalAlignment.Center });
            var staff = new NumericUpDown { Minimum = 0, Maximum = 2, Value = src.Staff, Width = 110, FormatString = "0" };
            staff.ValueChanged += (_, e) => src.Staff = (int)(e.NewValue ?? 0);
            row.Children.Add(staff);
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) => { _project.Sources.RemoveAt(idx); ShowSources(); };
            row.Children.Add(remove);
            _sourcesPanel.Children.Add(row);
        }
    }

    private async Task AddSourceAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add a version of the music",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("Scores (PDF, MusicXML)") { Patterns = new[] { "*.pdf", "*.musicxml", "*.xml" } } },
        });
        foreach (var f in files)
            if (f.TryGetLocalPath() is string p) _project.Sources.Add(new MergeSource { File = p });
        ShowSources();
    }

    // ------------------------------------------------------------------ comparing

    private async Task CompareAsync()
    {
        if (_project.Sources.Count < 2) { Status("at least two sources are needed"); return; }
        _compare.IsEnabled = false;
        try
        {
            _strands.Clear(); _crops.Clear();
            foreach (var src in _project.Sources)
            {
                var crops = new Dictionary<ScoreMeasure, (GrayImage, Rect)>();
                Score score;
                if (src.File.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) score = await RecogniseAsync(src.File, crops);
                else score = MusicXmlReader.Read(src.File);
                _strands.Add(StrandReader.FromScore(score, src.File, src.Part - 1, src.Staff == 0 ? null : src.Staff));
                _crops.Add(crops);
            }
            _rows = Merger.Align(_strands);
            Merger.Choose(_rows);
            ApplyChoices();
            ShowRows();
            _export.IsEnabled = true;
        }
        catch (Exception ex) { Status($"failed: {ex.Message}"); }
        finally { _compare.IsEnabled = true; }
    }

    /// <summary>Recognises every page of a PDF (with its stored corrections) and assembles the score; keeps the page
    /// images and the place of every bar for the crops.</summary>
    private async Task<Score> RecogniseAsync(string file, Dictionary<ScoreMeasure, (GrayImage, Rect)> crops)
    {
        int count = RecognitionEngine.PageCount(file);
        var store = CorrectionStore.Load(file);
        var pages = new Dictionary<int, PageResult>();
        var images = new Dictionary<PageAnalysis, GrayImage>();
        var analyses = new List<PageAnalysis>();
        for (int p = 1; p <= count; p++)
        {
            Status($"{Path.GetFileName(file)}: recognising page {p} of {count}…");
            pages.TryGetValue(PageOrder.Previous(file, p), out var previous);
            int page = p;
            var result = await Task.Run(() => _engine().Analyze(file, page, 300, previous));
            if (store.ApplyAll(result) > 0) RecognitionEngine.Reinterpret(result, previous);
            pages[p] = result;
            images[result.Analysis] = result.Context.Gray;
            analyses.Add(result.Analysis);
            // only the page image is kept (the rest of the context is large)
            pages.Remove(p - 3);   // (a page continues from at most two before it)
        }
        var map = new Dictionary<Measure, ScoreMeasure>();
        var score = MusicAssembler.Build(analyses, new MusicAssembler.Options { Title = Path.GetFileNameWithoutExtension(file), MeasureMap = map });
        foreach (var (measure, sm) in map)
        {
            var staff = measure.Staff;
            var page = analyses.FirstOrDefault(a => a.Staves.Contains(staff));
            if (page == null || crops.ContainsKey(sm)) continue;
            double ss = staff.Space, cx = (measure.X0 + measure.X1) / 2;
            var area = new Rect((int)Math.Max(0, measure.X0 - 0.5 * ss), (int)Math.Max(0, staff.TopY(cx) - 3.5 * ss),
                                (int)(measure.X1 + 0.5 * ss), (int)(staff.BottomY(cx) + 3.5 * ss));
            crops[sm] = (images[page], area);
        }
        return score;
    }

    // ------------------------------------------------------------------ rows

    private void ShowRows()
    {
        _rowsPanel.Children.Clear();
        int flagged = _rows.Count(r => r.Status == RowStatus.Flagged && !r.UserChoice);
        Status($"{_rows.Count} bars: " + string.Join(", ", _rows.GroupBy(r => r.Status).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"))
               + $"; {_rows.Count(r => r.UserChoice)} chosen by you; {flagged} still to choose");
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            bool show = _showAll.IsChecked == true ? row.Status != RowStatus.Unanimous : row.Status == RowStatus.Flagged || row.UserChoice;
            if (show) _rowsPanel.Children.Add(RowView(i));
        }
        if (_rowsPanel.Children.Count == 0) _rowsPanel.Children.Add(new TextBlock { Text = "No bars to choose: every bar was decided automatically." });
    }

    private Control RowView(int index)
    {
        var row = _rows[index];
        var panel = new StackPanel { Spacing = 4, Tag = index };
        string state = row.UserChoice ? "chosen by you" : row.Status.ToString().ToLowerInvariant();
        panel.Children.Add(new TextBlock { Text = $"Bar {index + 1} — {state}", FontWeight = FontWeight.Bold });
        var versions = new WrapPanel { Orientation = Orientation.Horizontal };
        for (int s = 0; s < row.Candidates.Length; s++)
        {
            if (row.Candidates[s] is not MergeBar bar) continue;
            int strand = s;
            bool chosen = row.Chosen == s;
            var content = new StackPanel { Spacing = 2, MaxWidth = 640 };
            content.Children.Add(new TextBlock { Text = $"{(char)('A' + s)}  page {bar.Page}, line {bar.Line}, bar {bar.Number}" + (bar.Fits ? "" : "  (does not add up)"), FontSize = 12 });
            if (bar.SourceMeasure != null && _crops[s].TryGetValue(bar.SourceMeasure, out var crop))
                content.Children.Add(new Image { Source = Crop(crop.page, crop.area), Height = 120, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left });
            content.Children.Add(new TextBlock { Text = bar.Key, FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
            var button = new Button
            {
                Content = content, Margin = new Thickness(0, 0, 8, 4), Padding = new Thickness(6),
                BorderThickness = new Thickness(chosen ? 3 : 1), BorderBrush = chosen ? Brushes.SeaGreen : Brushes.Gray,
                Background = chosen ? new SolidColorBrush(Color.FromArgb(40, 46, 139, 87)) : Brushes.Transparent,
            };
            button.Click += (_, _) => Choose(index, strand);
            versions.Children.Add(button);
        }
        panel.Children.Add(versions);
        return new Border { Child = panel, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 6) };
    }

    private void Choose(int index, int strand)
    {
        var row = _rows[index];
        row.Chosen = strand; row.UserChoice = true;
        _project.Choices.RemoveAll(c => c.Row == index);
        _project.Choices.Add(new MergeChoice { Row = index, Source = strand, Key = row.Candidates[strand]!.Key });
        // the row view only: a full refresh would lose the scroll position
        var view = _rowsPanel.Children.OfType<Border>().FirstOrDefault(b => b.Child is StackPanel p && p.Tag is int t && t == index);
        if (view != null) _rowsPanel.Children[_rowsPanel.Children.IndexOf(view)] = RowView(index);
        if (_projectPath != null) _ = SaveProjectAsync(ask: false);
    }

    /// <summary>The user's earlier choices, where the row still holds the same bar.</summary>
    private void ApplyChoices()
    {
        foreach (var c in _project.Choices)
            if (c.Row < _rows.Count && c.Source < _rows[c.Row].Candidates.Length && _rows[c.Row].Candidates[c.Source]?.Key == c.Key)
            { _rows[c.Row].Chosen = c.Source; _rows[c.Row].UserChoice = true; }
    }

    private void ScrollToNextFlagged()
    {
        foreach (var b in _rowsPanel.Children.OfType<Border>())
            if (b.Child is StackPanel p && p.Tag is int t && _rows[t].Status == RowStatus.Flagged && !_rows[t].UserChoice)
            { b.BringIntoView(); return; }
        Status("no flagged bar left to choose");
    }

    private static WriteableBitmap Crop(GrayImage g, Rect r)
    {
        int x0 = Math.Clamp(r.X0, 0, g.Width - 1), y0 = Math.Clamp(r.Y0, 0, g.Height - 1);
        int w = Math.Max(1, Math.Min(r.X1, g.Width - 1) - x0 + 1), h = Math.Max(1, Math.Min(r.Y1, g.Height - 1) - y0 + 1);
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bmp.Lock();
        var line = new byte[w * 4];
        for (int y = 0; y < h; y++)
        {
            int src = (y0 + y) * g.Width + x0;
            for (int x = 0; x < w; x++) { byte v = g.Data[src + x]; int o = x * 4; line[o] = v; line[o + 1] = v; line[o + 2] = v; line[o + 3] = 255; }
            Marshal.Copy(line, 0, fb.Address + y * fb.RowBytes, line.Length);
        }
        return bmp;
    }

    // ------------------------------------------------------------------ files

    private async Task ExportAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export merged MusicXML", SuggestedFileName = "merged.musicxml", DefaultExtension = "musicxml",
            FileTypeChoices = new[] { new FilePickerFileType("MusicXML") { Patterns = new[] { "*.musicxml", "*.xml" } } },
        });
        if (file?.TryGetLocalPath() is not string path) return;
        try
        {
            MusicXmlWriter.Write(Merger.BuildScore(_rows, _strands, Path.GetFileNameWithoutExtension(path)), path);
            Status($"wrote {Path.GetFileName(path)}: {_rows.Count(r => r.Chosen >= 0)} bars");
        }
        catch (Exception ex) { Status($"export failed: {ex.Message}"); }
    }

    private async Task SaveProjectAsync(bool ask)
    {
        if (ask || _projectPath == null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save merge project", SuggestedFileName = "merge.pvmerge.json", DefaultExtension = "json",
                FileTypeChoices = new[] { new FilePickerFileType("PVOMR merge project") { Patterns = new[] { "*.pvmerge.json" } } },
            });
            if (file?.TryGetLocalPath() is not string p) return;
            _projectPath = p;
        }
        await File.WriteAllTextAsync(_projectPath!, JsonSerializer.Serialize(_project, JsonOpts));
    }

    private async Task OpenProjectAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open merge project",
            FileTypeFilter = new[] { new FilePickerFileType("PVOMR merge project") { Patterns = new[] { "*.pvmerge.json", "*.json" } } },
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not string path) return;
        try
        {
            _project = JsonSerializer.Deserialize<MergeProject>(await File.ReadAllTextAsync(path), JsonOpts) ?? new();
            _projectPath = path;
            ShowSources();
            await CompareAsync();
        }
        catch (Exception ex) { Status($"could not open the project: {ex.Message}"); }
    }
}
