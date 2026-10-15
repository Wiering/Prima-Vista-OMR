using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PVOMR.Core.Analysis;
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
    /// <summary>Consecutive rests compared by their total length (a dotted quarter rest = a quarter and an 8th rest).</summary>
    public bool RestsByLength { get; set; } = true;
}

/// <summary>
/// Merges several versions of the same music. Every version is recognised (or read), the bars are aligned and the best
/// version of each is suggested. The differences are listed one by one; selecting one shows the pages of the versions
/// side by side, zoomed to the bar, with only the recognised notes that differ drawn on them, so that a misreading and
/// a real difference between the editions are told apart at a glance. The user confirms the right version per bar; the
/// merged music is exported as MusicXML.
/// </summary>
public sealed partial class MergeWindow : Window
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>One entry of the list: a difference in a row between the suggested version (Base) and another.</summary>
    private sealed record Entry(int Row, int Base, int Other, BarDiff Diff);

    private readonly Func<RecognitionEngine> _engine;
    private MergeProject _project = new();
    private string? _projectPath;
    private readonly StackPanel _sourcesPanel = new() { Spacing = 4 };
    private readonly ListBox _list = new();
    private readonly Grid _views = new();
    private readonly List<(PageView view, TextBlock header, TextBlock bar)> _pageViews = new();
    /// <summary>Views that showed a bar already: later bars are only centred, the user's zoom stays.</summary>
    private readonly HashSet<PageView> _zoomed = new();
    private readonly double _dpi;
    private readonly Action<List<(int page, SymbolEdit edit)>, string>? _saveEdits;
    private readonly WrapPanel _choiceButtons = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly CheckBox _restsByLength = new() { Content = "Rests by total length", IsChecked = true };
    private readonly CheckBox _onlyOpen = new() { Content = "Only bars to review", IsChecked = true };
    private readonly Button _compare, _export, _toEdits;
    private readonly List<MergeData> _sources = new();
    private List<MergeRow> _rows = new();
    private readonly HashSet<int> _reviewed = new();
    private List<Entry> _entries = new();

    /// <param name="openFile">The score being edited in the main window: it becomes source A.</param>
    /// <param name="saveEdits">Takes the choices as corrections of source A (to the main window, which records them).</param>
    public MergeWindow(Func<RecognitionEngine> engine, string? openFile = null, double dpi = 300, Action<List<(int page, SymbolEdit edit)>, string>? saveEdits = null)
    {
        _engine = engine;
        _dpi = dpi;
        _saveEdits = saveEdits;
        if (openFile != null) _project.Sources.Add(new MergeSource { File = openFile });
        Title = "PVOMR – merge versions";
        Width = 1600; Height = 950;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var add = new Button { Content = "Add source…" };
        add.Click += async (_, _) => await AddSourceAsync();
        _compare = new Button { Content = "Compare" };
        _compare.Click += async (_, _) => await CompareAsync();
        _export = new Button { Content = "Export merged MusicXML…", IsEnabled = false };
        _export.Click += async (_, _) => await ExportAsync();
        _toEdits = new Button { Content = "Save choices as edits of A", IsEnabled = false, IsVisible = saveEdits != null };
        ToolTip.SetTip(_toEdits, "The bars where another version was chosen become corrections of A; back to editing A in the main window");
        _toEdits.Click += (_, _) => SaveAsEdits();
        var open = new Button { Content = "Open project…" };
        open.Click += async (_, _) => await OpenProjectAsync();
        var save = new Button { Content = "Save project…" };
        save.Click += async (_, _) => await SaveProjectAsync(ask: _projectPath == null);
        var acceptAll = new Button { Content = "Accept all suggestions" };
        ToolTip.SetTip(acceptAll, "Take the suggested version for every bar not reviewed yet");
        acceptAll.Click += (_, _) => { for (int i = 0; i < _rows.Count; i++) if (_rows[i].Chosen >= 0) _reviewed.Add(i); FillList(); };
        _restsByLength.IsCheckedChanged += (_, _) => _project.RestsByLength = _restsByLength.IsChecked == true;
        ToolTip.SetTip(_restsByLength, "A dotted quarter rest and a quarter plus an 8th rest count as the same (takes effect at the next Compare)");
        _onlyOpen.IsCheckedChanged += (_, _) => FillList();
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        foreach (var c in new Control[] { open, save, add, _compare, _restsByLength, acceptAll, _export, _toEdits, _status }) { c.Margin = new Thickness(0, 0, 6, 4); bar.Children.Add(c); }

        var sources = new Expander { Header = "Sources (part and staff to take; staff 0 = the whole part)", IsExpanded = true, Content = _sourcesPanel, Margin = new Thickness(6, 0, 6, 4) };

        // left: the differences and the choice; right: the pages side by side
        _list.SelectionChanged += (_, _) => ShowEntry();
        var left = new DockPanel { Width = 440, Margin = new Thickness(6, 0, 6, 6) };
        var help = new TextBlock { Text = "Select a difference: the pages show the bar with only the differing notes drawn. Choose the right version (keys 1, 2 …; Enter accepts the suggestion).",
                                   TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(help, Dock.Top); left.Children.Add(help);
        DockPanel.SetDock(_onlyOpen, Dock.Top); left.Children.Add(_onlyOpen);
        DockPanel.SetDock(_choiceButtons, Dock.Bottom); _choiceButtons.Margin = new Thickness(0, 6, 0, 0); left.Children.Add(_choiceButtons);
        left.Children.Add(_list);
        var main = new DockPanel();
        DockPanel.SetDock(left, Dock.Left); main.Children.Add(left);
        main.Children.Add(_views);

        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); dock.Children.Add(bar);
        DockPanel.SetDock(sources, Dock.Top); dock.Children.Add(sources);
        dock.Children.Add(main);
        Content = dock;
        AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ShowSources();
        Status("Add two or more versions of the same music (PDF or MusicXML), then Compare.");
    }

    private void Status(string text) => _status.Text = text;

    private static string Letter(int i) => ((char)('A' + i)).ToString();

    // ------------------------------------------------------------------ sources

    private void ShowSources()
    {
        _sourcesPanel.Children.Clear();
        for (int i = 0; i < _project.Sources.Count; i++)
        {
            var src = _project.Sources[i];
            int idx = i;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock { Text = Letter(i), FontWeight = FontWeight.Bold, Width = 16, VerticalAlignment = VerticalAlignment.Center });
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
            _sources.Clear();
            foreach (var src in _project.Sources)
            {
                _sources.Add(await MergeData.LoadAsync(_engine, src, _dpi, _project.RestsByLength, Status));
            }
            _rows = Merger.Align(_sources.Select(s => s.Strand).ToList());
            Merger.Choose(_rows);
            _reviewed.Clear();
            for (int i = 0; i < _rows.Count; i++) if (_rows[i].Status == RowStatus.Unanimous) _reviewed.Add(i);
            ApplyChoices();
            BuildViews();
            BuildEntries();
            FillList();
            if (_list.ItemCount > 0) _list.SelectedIndex = 0;
            _export.IsEnabled = true;
            _toEdits.IsEnabled = _sources[0].ByNumber.Count > 0;
        }
        catch (Exception ex) { Status($"failed: {ex.Message}"); }
        finally { _compare.IsEnabled = true; }
    }

    private void BuildViews()
    {
        _views.Children.Clear();
        _views.ColumnDefinitions.Clear();
        _pageViews.Clear();
        _zoomed.Clear();
        for (int i = 0; i < _sources.Count; i++)
        {
            _views.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var header = new TextBlock { Text = $"{Letter(i)}: {Path.GetFileName(_sources[i].Strand.Source)}", FontWeight = FontWeight.Bold, Margin = new Thickness(4, 0, 0, 2), TextWrapping = TextWrapping.Wrap };
            var barText = new TextBlock { Margin = new Thickness(4, 0, 0, 4), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            var view = new PageView { ShowFits = false };
            var panel = new DockPanel { Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0) };
            DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
            DockPanel.SetDock(barText, Dock.Top); panel.Children.Add(barText);
            panel.Children.Add(new Border { Child = view, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) });
            Grid.SetColumn(panel, i);
            _views.Children.Add(panel);
            _pageViews.Add((view, header, barText));
        }
    }

    /// <summary>The list entries: per row not settled by all versions agreeing, the differences of each other version
    /// against the suggested one.</summary>
    private void BuildEntries()
    {
        _entries = new();
        for (int r = 0; r < _rows.Count; r++)
        {
            var row = _rows[r];
            if (row.Status == RowStatus.Unanimous || row.Chosen < 0) continue;
            var baseBar = row.Candidates[row.Chosen]!;
            for (int s = 0; s < row.Candidates.Length; s++)
            {
                if (s == row.Chosen) continue;
                if (row.Candidates[s] is not MergeBar other)
                {
                    _entries.Add(new Entry(r, row.Chosen, s, new BarDiff(DiffKind.ExtraBar, $"bar missing in {Letter(s)}")));
                    continue;
                }
                if (other.Key == baseBar.Key) continue;
                var diffs = Comparer.Diff(baseBar, other);
                if (diffs.Count == 0) diffs.Add(new BarDiff(DiffKind.Duration, "the bars differ"));
                foreach (var d in diffs) _entries.Add(new Entry(r, row.Chosen, s, d));
            }
        }
    }

    private void FillList()
    {
        var shown = _entries.Where(e => _onlyOpen.IsChecked != true || !_reviewed.Contains(e.Row)).ToList();
        _list.ItemsSource = shown.Select(e =>
        {
            var row = _rows[e.Row];
            string state = _reviewed.Contains(e.Row) ? $"✓ {Letter(row.Chosen)}" : $"suggested {Letter(row.Chosen)}";
            var bar = row.Candidates[e.Base] ?? row.Candidates[e.Other];
            var item = new TextBlock { Text = $"bar {bar?.Number}  {Letter(e.Base)} vs {Letter(e.Other)}  {Describe(e.Diff)}   [{state}]", Tag = e, TextWrapping = TextWrapping.Wrap };
            if (_reviewed.Contains(e.Row)) item.Opacity = 0.6;
            return item;
        }).ToList();
        int open = Enumerable.Range(0, _rows.Count).Count(i => !_reviewed.Contains(i));
        Status($"{_rows.Count} bars: {_rows.Count(r => r.Status == RowStatus.Unanimous)} the same in every version; {open} to review ({_entries.Count(e => !_reviewed.Contains(e.Row))} differences)");
    }

    private static string Describe(BarDiff d) => d.Kind switch
    {
        DiffKind.Accidental => "accidental: " + d.Text,
        DiffKind.Pitch => (d.StepOffset != 0 ? $"pitch ({d.StepOffset:+0;-0} steps): " : "pitch: ") + d.Text,
        DiffKind.Duration => "length: " + d.Text,
        DiffKind.ExtraNote or DiffKind.MissingNote => "note missing: " + d.Text,
        DiffKind.NoteRest => "note/rest: " + d.Text,
        _ => d.Text,
    };

    // ------------------------------------------------------------------ showing a difference

    private void ShowEntry()
    {
        if (_list.SelectedItem is not TextBlock { Tag: Entry e }) return;
        var row = _rows[e.Row];
        for (int s = 0; s < _sources.Count && s < _pageViews.Count; s++)
        {
            var (view, header, barText) = _pageViews[s];
            var bar = row.Candidates[s];
            var src = _sources[s];
            // the difference in bold: its events on the two sides compared; a third version: what differs from the suggested one
            var third = s != e.Base && s != e.Other && bar != null && row.Candidates[e.Base] is MergeBar b0 ? Comparer.Diff(b0, bar).Select(d => d.EventB) : null;
            ShowBarText(barText, bar, s == e.Base ? e.Diff.EventA : s == e.Other ? e.Diff.EventB : null, third);
            string name = $"{Letter(s)}: {Path.GetFileName(src.Strand.Source)}";
            if (bar?.SourceMeasure is not ScoreMeasure sm || !src.Measures.TryGetValue(sm, out var measures) || measures.Count == 0)
            {
                header.Text = name + (bar == null ? "  — bar missing" : "  — no page image (MusicXML)");
                view.OnlySymbols = null;
                view.SetPage(null, false);
                continue;
            }
            // the staff taken from the part (a grand staff: the upper one for the view)
            var measure = measures.OrderBy(m => m.Staff.Index).ElementAtOrDefault(src.Strand.Staff is int st0 ? Math.Min(st0 - 1, measures.Count - 1) : 0) ?? measures[0];
            var page = src.Pages.FirstOrDefault(p => p.Key.Staves.Contains(measure.Staff)).Value;
            if (page == null) { view.SetPage(null, false); continue; }
            header.Text = $"{name}  — page {page.PageNumber}, bar {bar.Number}" + (bar.Fits ? "" : " (does not add up)")
                          + (s == row.Chosen ? (_reviewed.Contains(e.Row) ? "  ✓ chosen" : "  (suggested)") : "");
            var events = s == e.Base ? e.Diff.EventA : s == e.Other ? e.Diff.EventB : null;
            view.SetPage(page, false);
            view.OnlySymbols = events == null ? Array.Empty<MusicSymbol>() : SymbolsFor(events, measures);
            // the bar with some context on each side, the staff with room above and below
            var staff = measure.Staff;
            double ss = staff.Space, cx = (measure.X0 + measure.X1) / 2, w = measure.X1 - measure.X0;
            // the first time zoomed to the bar; after that only centred on the difference (the user's zoom stays)
            if (_zoomed.Add(view))
                view.ZoomTo(new Rect((int)(measure.X0 - 0.6 * w), (int)(staff.TopY(cx) - 5 * ss), (int)(measure.X1 + 0.6 * w), (int)(staff.BottomY(cx) + 5 * ss)));
            else if (view.OnlySymbols is { Count: > 0 } shown)
                view.CenterOn(shown.Average(o => o.Bounds.CenterX), shown.Average(o => o.Bounds.CenterY));
            else view.CenterOn(cx, (staff.TopY(cx) + staff.BottomY(cx)) / 2);
        }
        ShowChoiceButtons(e.Row);
    }

    /// <summary>The bar as text, voice by voice, with the events of the difference in bold.</summary>
    private static void ShowBarText(TextBlock tb, MergeBar? bar, BarEvent? marked, IEnumerable<BarEvent?>? alsoMarked)
    {
        var inlines = new InlineCollection();
        if (bar != null)
        {
            var bold = new HashSet<BarEvent>();
            if (marked != null) bold.Add(marked);
            if (alsoMarked != null) foreach (var m in alsoMarked) if (m != null) bold.Add(m);
            for (int v = 0; v < bar.Voices.Count; v++)
            {
                if (v > 0) inlines.Add(new Run("  |  "));
                for (int k = 0; k < bar.Voices[v].Count; k++)
                {
                    var ev = bar.Voices[v][k];
                    if (k > 0) inlines.Add(new Run(" "));
                    var run = new Run(ev.ToString());
                    if (bold.Contains(ev)) { run.FontWeight = FontWeight.Bold; run.Foreground = Brushes.OrangeRed; }
                    inlines.Add(run);
                }
            }
        }
        tb.Inlines = inlines;
    }

    /// <summary>The recognised symbols (a note of a beamed group on its own) of the events, found by their heads (rests by
    /// their centre) in the page measures of the bar.</summary>
    private static List<MusicSymbol> SymbolsFor(BarEvent ev, List<Measure> measures)
    {
        var found = new List<MusicSymbol>();
        var symbols = measures.SelectMany(m => m.Symbols).SelectMany(s => s.Children.Count > 0 ? s.Children.Prepend(s) : new[] { s }).ToList();
        foreach (var se in ev.Sources)
        {
            MusicSymbol? hit;
            if (se.IsRest)
                hit = symbols.Where(s => s.Kind == SymbolKind.Rest).OrderBy(s => Math.Abs(s.Bounds.CenterX - se.X) + Math.Abs(s.Bounds.CenterY - se.Y)).FirstOrDefault();
            else
            {
                var heads = se.Notes.Select(n => (n.SourceX, n.SourceY)).ToList();
                hit = symbols.Where(s => s.Kind is SymbolKind.Note or SymbolKind.Chord && s.Notes.Any(h => heads.Any(p => Math.Abs(h.HeadX - p.SourceX) <= 3 && Math.Abs(h.HeadY - p.SourceY) <= 3)))
                             .OrderBy(s => s.Notes.Count).FirstOrDefault();
            }
            if (hit != null && !found.Contains(hit)) found.Add(hit);
        }
        return found;
    }

    private void ShowChoiceButtons(int rowIndex)
    {
        _choiceButtons.Children.Clear();
        var row = _rows[rowIndex];
        for (int s = 0; s < row.Candidates.Length; s++)
        {
            if (row.Candidates[s] == null) continue;
            int strand = s;
            var b = new Button { Content = $"{s + 1}: {Letter(s)} is right", Margin = new Thickness(0, 0, 6, 4) };
            if (row.Chosen == s) b.FontWeight = FontWeight.Bold;
            b.Click += (_, _) => Choose(rowIndex, strand);
            _choiceButtons.Children.Add(b);
        }
        var accept = new Button { Content = $"Enter: accept {Letter(row.Chosen)}", Margin = new Thickness(0, 0, 6, 4) };
        accept.Click += (_, _) => Choose(rowIndex, row.Chosen);
        _choiceButtons.Children.Add(accept);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (_list.SelectedItem is not TextBlock { Tag: Entry entry }) return;
        int pick = e.Key switch { Key.D1 or Key.NumPad1 => 0, Key.D2 or Key.NumPad2 => 1, Key.D3 or Key.NumPad3 => 2, Key.D4 or Key.NumPad4 => 3, Key.Enter => _rows[entry.Row].Chosen, _ => -1 };
        if (pick < 0 || pick >= _rows[entry.Row].Candidates.Length || _rows[entry.Row].Candidates[pick] == null) return;
        Choose(entry.Row, pick);
        e.Handled = true;
    }

    private void Choose(int rowIndex, int strand)
    {
        var row = _rows[rowIndex];
        row.Chosen = strand; row.UserChoice = true;
        _reviewed.Add(rowIndex);
        _project.Choices.RemoveAll(c => c.Row == rowIndex);
        _project.Choices.Add(new MergeChoice { Row = rowIndex, Source = strand, Key = row.Candidates[strand]!.Key });
        var current = (_list.SelectedItem as TextBlock)?.Tag as Entry;
        FillList();
        // on to the next open difference (after this one, else from the top)
        int from = current == null ? 0 : Math.Max(0, _entries.IndexOf(current));
        var next = _entries.Skip(from).Concat(_entries.Take(from)).FirstOrDefault(x => !_reviewed.Contains(x.Row));
        int at = next == null ? -1 : _list.Items.OfType<TextBlock>().ToList().FindIndex(t => t.Tag == next);
        if (at >= 0) { _list.SelectedIndex = at; _list.ScrollIntoView(at); }
        else if (next == null) Status("every bar is reviewed");
        if (_projectPath != null) _ = SaveProjectAsync(ask: false);
    }

    /// <summary>The user's earlier choices, where the row still holds the same bar.</summary>
    private void ApplyChoices()
    {
        foreach (var c in _project.Choices)
            if (c.Row < _rows.Count && c.Source < _rows[c.Row].Candidates.Length && _rows[c.Row].Candidates[c.Source]?.Key == c.Key)
            { _rows[c.Row].Chosen = c.Source; _rows[c.Row].UserChoice = true; _reviewed.Add(c.Row); }
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
            MusicXmlWriter.Write(Merger.BuildScore(_rows, _sources.Select(s => s.Strand).ToList(), Path.GetFileNameWithoutExtension(path)), path);
            int open = Enumerable.Range(0, _rows.Count).Count(i => !_reviewed.Contains(i));
            Status($"wrote {Path.GetFileName(path)}: {_rows.Count(r => r.Chosen >= 0)} bars" + (open > 0 ? $" ({open} not reviewed: their suggested version was used)" : ""));
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
            _restsByLength.IsChecked = _project.RestsByLength;
            ShowSources();
            await CompareAsync();
        }
        catch (Exception ex) { Status($"could not open the project: {ex.Message}"); }
    }
}
