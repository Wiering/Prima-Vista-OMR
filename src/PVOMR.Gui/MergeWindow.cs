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
    /// <summary>Pitches corrected in the chosen bar (neither version right).</summary>
    public List<NoteOverride>? Overrides { get; set; }
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
    private sealed record Slot(PageView View, TextBlock Header, TextBlock Bar, Border Border);
    private readonly List<Slot> _pageViews = new();
    /// <summary>Views that showed a bar already: later bars are only centred, the user's zoom stays.</summary>
    private readonly HashSet<PageView> _zoomed = new();
    private readonly double _dpi;
    private readonly Action<List<(int page, SymbolEdit edit)>, string>? _saveEdits;
    private readonly WrapPanel _choiceButtons = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly CheckBox _restsByLength = new() { Content = "Rests by total length", IsChecked = true };
    private readonly CheckBox _onlyOpen = new() { Content = "Only bars to review", IsChecked = true };
    private readonly TextBlock _progress = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Opacity = 0.8 };
    private readonly StackPanel _noteEditor = new() { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _noteLabel = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _pitchBox = new() { Width = 70 };
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
        _toEdits = new Button { Content = "Save choices as edits of A and exit",IsEnabled = false, IsVisible = saveEdits != null };
        ToolTip.SetTip(_toEdits, "The bars where another version was chosen become corrections of A; back to editing A in the main window");
        _toEdits.Click += (_, _) => SaveAsEdits();
        var open = new Button { Content = "Open project…" };
        open.Click += async (_, _) => await OpenProjectAsync();
        var save = new Button { Content = "Save project…" };
        save.Click += async (_, _) => await SaveProjectAsync(ask: _projectPath == null);
        var acceptAll = new Button { Content = "Accept all suggestions" };
        ToolTip.SetTip(acceptAll, "Take the suggested version for every bar not reviewed yet");
        acceptAll.Click += (_, _) => AcceptWhere(_ => true);
        var acceptFitting = new Button { Content = "Accept where only the suggestion adds up" };
        ToolTip.SetTip(acceptFitting, "Take the suggested version for every open bar where it adds up and the other versions do not");
        acceptFitting.Click += (_, _) => AcceptWhere(OnlySuggestionFits);
        _restsByLength.IsCheckedChanged += (_, _) => _project.RestsByLength = _restsByLength.IsChecked == true;
        ToolTip.SetTip(_restsByLength, "A dotted quarter rest and a quarter plus an 8th rest count as the same (takes effect at the next Compare)");
        _onlyOpen.IsCheckedChanged += (_, _) => FillList(SelectedEntry);
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        foreach (var c in new Control[] { open, save, add, _compare, _restsByLength, acceptAll, acceptFitting, _export, _toEdits, _status }) { c.Margin = new Thickness(0, 0, 6, 4); bar.Children.Add(c); }

        var sources = new Expander { Header = "Sources (part and staff to take; staff 0 = the whole part)", IsExpanded = true, Content = _sourcesPanel, Margin = new Thickness(6, 0, 6, 4) };

        // left: the differences and the choice; right: the pages side by side
        _list.SelectionChanged += (_, _) => ShowEntry();
        var left = new DockPanel { Width = 440, Margin = new Thickness(6, 0, 6, 6) };
        var help = new TextBlock { Text = "Select a difference: the pages show the bar with only the differing notes drawn. Choose the right version (keys 1, 2 …; Enter accepts the suggestion).",
                                   TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(help, Dock.Top); left.Children.Add(help);
        var openRow = new DockPanel();
        DockPanel.SetDock(_progress, Dock.Right); openRow.Children.Add(_progress);
        openRow.Children.Add(_onlyOpen);
        DockPanel.SetDock(openRow, Dock.Top); left.Children.Add(openRow);
        // a corrected pitch when neither version is right
        Button Small(string text, string tip, Action act) { var b = new Button { Content = text, MinWidth = 30 }; ToolTip.SetTip(b, tip); b.Click += (_, _) => act(); return b; }
        _pitchBox.KeyDown += (_, k) => { if (k.Key == Key.Enter) { UseCorrectedPitch(); k.Handled = true; } };
        foreach (var c in new Control[] { Small("▼", "a step down", () => NudgePitch(-1, null)), Small("▲", "a step up", () => NudgePitch(1, null)), _pitchBox,
                                          Small("♭", "flat", () => NudgePitch(0, -1)), Small("♮", "natural", () => NudgePitch(0, 0)), Small("♯", "sharp", () => NudgePitch(0, 1)),
                                          Small("Use this pitch", "Take the chosen version with this note corrected", UseCorrectedPitch) })
            _noteEditor.Children.Add(c);
        var editorPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        editorPanel.Children.Add(_noteLabel); editorPanel.Children.Add(_noteEditor);
        DockPanel.SetDock(editorPanel, Dock.Bottom); left.Children.Add(editorPanel);
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
            FillList(_entries.FirstOrDefault(), nextOpen: true);
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
            var border = new Border { Child = view, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
            var panel = new DockPanel { Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0) };
            DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
            DockPanel.SetDock(barText, Dock.Top); panel.Children.Add(barText);
            panel.Children.Add(border);
            Grid.SetColumn(panel, i);
            _views.Children.Add(panel);
            _pageViews.Add(new Slot(view, header, barText, border));
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

    /// <summary>Fills the list and selects an entry: the preferred one if it is listed, else (with nextOpen) the first open
    /// one after it.</summary>
    private void FillList(Entry? preferred = null, bool nextOpen = false)
    {
        var shown = _entries.Where(e => _onlyOpen.IsChecked != true || !_reviewed.Contains(e.Row)).ToList();
        var items = shown.Select(e =>
        {
            var row = _rows[e.Row];
            var bar = row.Candidates[e.Base] ?? row.Candidates[e.Other];
            bool done = _reviewed.Contains(e.Row);
            var item = new TextBlock { Text = $"{(done ? $"✓ {Letter(row.Chosen)}  " : "")}bar {bar?.Number}  {Letter(e.Base)} vs {Letter(e.Other)}  {Describe(e.Diff)}", Tag = e, TextWrapping = TextWrapping.Wrap };
            if (done) item.Opacity = 0.6;
            return item;
        }).ToList();
        _list.ItemsSource = items;
        var rowsToReview = _entries.Select(e => e.Row).Distinct().ToList();
        _progress.Text = $"{rowsToReview.Count(_reviewed.Contains)} / {rowsToReview.Count} done";
        int open = Enumerable.Range(0, _rows.Count).Count(i => !_reviewed.Contains(i));
        Status($"{_rows.Count} bars: {_rows.Count(r => r.Status == RowStatus.Unanimous)} the same in every version; {open} to review ({_entries.Count(e => !_reviewed.Contains(e.Row))} differences)");
        if (preferred == null) return;
        Entry? target = nextOpen ? null : preferred;
        if (target == null || !shown.Contains(target))
        {
            int from = Math.Max(0, _entries.IndexOf(preferred));
            target = _entries.Skip(from).Concat(_entries.Take(from)).FirstOrDefault(x => shown.Contains(x) && (!nextOpen || !_reviewed.Contains(x.Row)));
        }
        int at = target == null ? -1 : items.FindIndex(t => t.Tag == target);
        if (at >= 0) { _list.SelectedIndex = at; _list.ScrollIntoView(at); }
        else if (nextOpen) Status("every bar is reviewed");
    }

    private Entry? SelectedEntry => (_list.SelectedItem as TextBlock)?.Tag as Entry;

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

    /// <summary>The page and page measure of a bar of a source (the staff taken from the part; a grand staff: the upper one).</summary>
    private (PageResult page, Measure measure, List<Measure> measures)? Locate(int s, MergeBar? bar)
    {
        var src = _sources[s];
        if (bar?.SourceMeasure is not ScoreMeasure sm || !src.Measures.TryGetValue(sm, out var measures) || measures.Count == 0) return null;
        var measure = measures.OrderBy(m => m.Staff.Index).ElementAtOrDefault(src.Strand.Staff is int st0 ? Math.Min(st0 - 1, measures.Count - 1) : 0) ?? measures[0];
        var page = src.Pages.FirstOrDefault(p => p.Key.Staves.Contains(measure.Staff)).Value;
        return page == null ? null : (page, measure, measures);
    }

    private void ShowEntry()
    {
        if (SelectedEntry is not Entry e) return;
        var row = _rows[e.Row];
        for (int s = 0; s < _sources.Count && s < _pageViews.Count; s++)
        {
            var slot = _pageViews[s];
            var view = slot.View;
            var bar = row.Candidates[s];
            var src = _sources[s];
            // the difference in bold: its events on the two sides compared; a third version: what differs from the suggested one
            var third = s != e.Base && s != e.Other && bar != null && row.Candidates[e.Base] is MergeBar b0 ? Comparer.Diff(b0, bar).Select(d => d.EventB) : null;
            ShowBarText(slot.Bar, bar, s == e.Base ? e.Diff.EventA : s == e.Other ? e.Diff.EventB : null, third);
            // the suggested (or chosen) version: a thick outline; a version that does not add up: a thick red one
            bool chosen = s == row.Chosen;
            // (red only where the rhythm differs from a version that adds up: with the same rhythm, adding up decides nothing)
            int si = s;
            bool red = bar != null && !bar.Fits && row.Candidates.Where((c, i) => i != si && c != null && c.Fits).Any(c => RhythmDiffers(bar, c!));
            slot.Border.BorderBrush = red ? Brushes.Red : chosen ? new SolidColorBrush(Color.FromRgb(0, 150, 0)) : Brushes.Gray;
            slot.Border.BorderThickness = new Thickness(red || chosen ? 4 : 1);
            string name = $"{Letter(s)}: {Path.GetFileName(src.Strand.Source)}";
            view.Labels = null; view.Marker = null;
            var at = Locate(s, bar);
            if (at == null)
            {
                // a missing bar: the page where it would be, after the bar before it (or before the bar after it)
                if (bar == null && Neighbour(e.Row, s) is var (nb, after) && Locate(s, nb) is var (np, nm, _))
                {
                    slot.Header.Text = $"{name}  — bar missing: shown {(after ? "after" : "before")} its bar {nb.Number} (page {np.PageNumber})";
                    view.OnlySymbols = Array.Empty<MusicSymbol>();
                    view.SetPage(np, false);
                    double x = after ? nm.X1 : nm.X0, ny = (nm.Staff.TopY(x) + nm.Staff.BottomY(x)) / 2;
                    view.Marker = new Rect((int)(x - nm.Staff.Space), (int)(nm.Staff.TopY(x) - nm.Staff.Space), (int)(x + nm.Staff.Space), (int)(nm.Staff.BottomY(x) + nm.Staff.Space));
                    Frame(view, nm, x, ny, null);
                    continue;
                }
                slot.Header.Text = name + (bar == null ? "  — bar missing" : "  — no page image (MusicXML)");
                view.OnlySymbols = null;
                view.SetPage(null, false);
                continue;
            }
            var (page, measure, measures) = at.Value;
            string corrected = chosen && row.Overrides.Count > 0 ? $", corrected: {string.Join(", ", row.Overrides.Select(o => o.Pitch))}" : "";
            slot.Header.Text = $"{name}  — page {page.PageNumber}, bar {bar!.Number}" + (bar.Fits ? "" : " (does not add up)")
                               + (chosen ? (_reviewed.Contains(e.Row) ? $"  ✓ chosen{corrected}" : "  (suggested)") : "");
            var ev = s == e.Base ? e.Diff.EventA : s == e.Other ? e.Diff.EventB : null;
            view.SetPage(page, false);
            var found = ev == null ? new List<(MusicSymbol sym, ScoreEvent se)>() : SymbolsFor(ev, measures);
            view.OnlySymbols = found.Select(f => f.sym).Distinct().ToList();
            var labels = new Dictionary<MusicSymbol, SymbolLabel>();
            foreach (var (sym, se) in found) labels.TryAdd(sym, LabelFor(s, bar, ev!, se, e.Diff.Kind));
            view.Labels = labels;
            // a note this version lacks: where it would be, at the same fraction of the bar as in the other version
            var otherEv = s == e.Base ? e.Diff.EventB : s == e.Other ? e.Diff.EventA : null;
            if (ev == null && otherEv?.Sources.FirstOrDefault() is ScoreEvent ose && Locate(s == e.Base ? e.Other : e.Base, row.Candidates[s == e.Base ? e.Other : e.Base]) is var (_, om, _))
            {
                double f = Math.Clamp((ose.X - om.X0) / Math.Max(1.0, om.X1 - om.X0), 0.02, 0.98), x = measure.X0 + f * (measure.X1 - measure.X0);
                var st = measure.Staff;
                view.Marker = new Rect((int)(x - 0.8 * st.Space), (int)(st.TopY(x) - st.Space), (int)(x + 0.8 * st.Space), (int)(st.BottomY(x) + st.Space));
            }
            double cx = (measure.X0 + measure.X1) / 2;
            Frame(view, measure, cx, (measure.Staff.TopY(cx) + measure.Staff.BottomY(cx)) / 2, view.OnlySymbols);
        }
        ShowChoiceButtons(e.Row);
        ShowNoteEditor(e);
    }

    /// <summary>The first time: zoomed to the bar; after that only centred on the difference (the user's zoom stays).</summary>
    private void Frame(PageView view, Measure measure, double x, double y, IReadOnlyCollection<MusicSymbol>? shown)
    {
        var staff = measure.Staff;
        double ss = staff.Space, cx = (measure.X0 + measure.X1) / 2, w = measure.X1 - measure.X0;
        if (_zoomed.Add(view))
            view.ZoomTo(new Rect((int)(measure.X0 - 0.6 * w), (int)(staff.TopY(cx) - 5 * ss), (int)(measure.X1 + 0.6 * w), (int)(staff.BottomY(cx) + 5 * ss)));
        else if (shown is { Count: > 0 })
            view.CenterOn(shown.Average(o => o.Bounds.CenterX), shown.Average(o => o.Bounds.CenterY));
        else if (view.Marker is Rect mk) view.CenterOn(mk.CenterX, mk.CenterY);
        else view.CenterOn(x, y);
    }

    /// <summary>The nearest bar of the source before the row (after: true), else after it.</summary>
    private (MergeBar bar, bool after)? Neighbour(int rowIndex, int s)
    {
        for (int r = rowIndex - 1; r >= 0; r--) if (_rows[r].Candidates[s] is MergeBar b) return (b, true);
        for (int r = rowIndex + 1; r < _rows.Count; r++) if (_rows[r].Candidates[s] is MergeBar b) return (b, false);
        return null;
    }

    /// <summary>The text around a differing symbol: pitch (bold for a pitch difference), length (bold for a length
    /// difference), and the semitones from the note before and to the note after.</summary>
    private SymbolLabel LabelFor(int s, MergeBar bar, BarEvent ev, ScoreEvent se, DiffKind kind)
    {
        string pitch = se.IsRest ? "rest" : string.Join("+", se.Notes.Select(n => n.Pitch).OrderBy(StrandReader.Midi));
        string length = (se.Type == "eighth" ? "8th" : se.Type) + PageView.DotsText(se.Dots) + (se.TupletActual > 0 ? $" ({se.TupletActual}:{se.TupletNormal})" : "");
        bool pitchBold = kind is DiffKind.Pitch or DiffKind.Accidental or DiffKind.NoteRest or DiffKind.ExtraNote or DiffKind.MissingNote;
        bool lengthBold = kind is DiffKind.Duration or DiffKind.NoteRest or DiffKind.ExtraNote or DiffKind.MissingNote;
        string? before = null, after = null;
        if (!se.IsRest && se.Notes.Count > 0)
        {
            int top = se.Notes.Max(n => StrandReader.Midi(n.Pitch));
            var (prev, next) = NeighbourNotes(s, bar, ev);
            if (prev != null) before = Interval(top - prev.Pitches.Max(StrandReader.Midi));
            if (next != null) after = Interval(next.Pitches.Max(StrandReader.Midi) - top);
        }
        return new SymbolLabel(pitch, length, pitchBold, lengthBold, before, after);
    }

    private static string Interval(int semitones) => semitones > 0 ? $"+{semitones}" : semitones.ToString();

    /// <summary>The notes before and after an event in its voice (rests skipped), looking into the bars around it.</summary>
    private (BarEvent? prev, BarEvent? next) NeighbourNotes(int s, MergeBar bar, BarEvent ev)
    {
        int voice = bar.Voices.FindIndex(v => v.Contains(ev));
        if (voice < 0) return (null, null);
        var bars = _sources[s].Strand.Bars;
        List<BarEvent> VoiceOf(int i) => i < 0 || i >= bars.Count || bars[i].Voices.Count == 0 ? new() : bars[i].Voices[Math.Min(voice, bars[i].Voices.Count - 1)];
        var line = VoiceOf(bar.Index - 1).Concat(bar.Voices[voice]).Concat(VoiceOf(bar.Index + 1)).ToList();
        int k = line.IndexOf(ev);
        var prev = line.Take(k).LastOrDefault(x => !x.IsRest);
        var next = line.Skip(k + 1).FirstOrDefault(x => !x.IsRest);
        return (prev, next);
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

    /// <summary>The recognised symbols (a note of a beamed group on its own) of the event's source events, found by their
    /// heads (rests by their centre) in the page measures of the bar.</summary>
    private static List<(MusicSymbol sym, ScoreEvent se)> SymbolsFor(BarEvent ev, List<Measure> measures)
    {
        var found = new List<(MusicSymbol, ScoreEvent)>();
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
            if (hit != null) found.Add((hit, se));
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

    // ------------------------------------------------------------------ correcting a note (neither version right)

    /// <summary>The note the editor corrects: in the chosen version, the event of the selected difference and its first
    /// head that differs from the other version.</summary>
    private (int row, int strand, int eventIndex, int head, BarEvent ev)? EditorTarget()
    {
        if (SelectedEntry is not Entry e) return null;
        var row = _rows[e.Row];
        var ev = row.Chosen == e.Base ? e.Diff.EventA : row.Chosen == e.Other ? e.Diff.EventB : null;
        var other = row.Chosen == e.Base ? e.Diff.EventB : e.Diff.EventA;
        if (ev == null || ev.IsRest || row.ChosenBar is not MergeBar bar) return null;
        int index = bar.Voices.SelectMany(v => v).ToList().FindIndex(x => ReferenceEquals(x, ev));
        if (index < 0) return null;
        int head = 0;
        if (other != null)
            for (int k = 0; k < ev.Pitches.Count; k++)
                if (k >= other.Pitches.Count || StrandReader.Midi(ev.Pitches[k]) != StrandReader.Midi(other.Pitches[k])) { head = k; break; }
        return (e.Row, row.Chosen, index, head, ev);
    }

    private void ShowNoteEditor(Entry e)
    {
        var t = EditorTarget();
        _noteEditor.IsEnabled = t != null;
        if (t is not var (r, strand, index, head, ev)) { _noteLabel.Text = "Correct a note: (select a note difference)"; _pitchBox.Text = ""; return; }
        var o = _rows[r].Overrides.FirstOrDefault(x => x.Event == index && x.Head == head);
        _noteLabel.Text = $"Neither right? Correct the note in {Letter(strand)}:";
        _pitchBox.Text = (o?.Pitch ?? ev.Pitches[head]).ToString();
    }

    private static PitchSpec? ParsePitch(string? text)
    {
        var m = System.Text.RegularExpressions.Regex.Match((text ?? "").Trim(), @"^([A-Ga-g])(bb|b|#|x|n)?(-?\d)$");
        if (!m.Success) return null;
        int alter = m.Groups[2].Value switch { "#" => 1, "x" => 2, "b" => -1, "bb" => -2, _ => 0 };
        return new PitchSpec(m.Groups[1].Value.ToUpperInvariant(), alter, int.Parse(m.Groups[3].Value));
    }

    /// <summary>A step up or down (the accidental the key gives the new note), or an accidental set.</summary>
    private void NudgePitch(int steps, int? alter)
    {
        if (ParsePitch(_pitchBox.Text) is not PitchSpec p || SelectedEntry is not Entry e) return;
        if (steps != 0)
        {
            int d = StrandReader.Diatonic(p) + steps;
            string letter = "CDEFGAB"[((d % 7) + 7) % 7].ToString();
            int fifths = _rows[e.Row].ChosenBar?.KeyFifths ?? 0;
            int keyAlter = fifths > 0 ? ("FCGDAEB".IndexOf(letter[0]) < fifths ? 1 : 0) : fifths < 0 ? ("BEADGCF".IndexOf(letter[0]) < -fifths ? -1 : 0) : 0;
            p = new PitchSpec(letter, keyAlter, (int)Math.Floor(d / 7.0));
        }
        if (alter is int a) p = p with { Alter = a };
        _pitchBox.Text = p.ToString();
    }

    private void UseCorrectedPitch()
    {
        if (EditorTarget() is not var (r, strand, index, head, ev)) return;
        if (ParsePitch(_pitchBox.Text) is not PitchSpec p) { Status($"\"{_pitchBox.Text}\" is no pitch (write e.g. F#4, Bb3, E5)"); return; }
        var row = _rows[r];
        row.Overrides.RemoveAll(o => o.Event == index && o.Head == head);
        if (StrandReader.Midi(p) != StrandReader.Midi(ev.Pitches[head]) || p != ev.Pitches[head]) row.Overrides.Add(new NoteOverride(index, head, p));
        Choose(r, strand, keepOverrides: true);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;
        if (SelectedEntry is not Entry entry) return;
        int pick = e.Key switch { Key.D1 or Key.NumPad1 => 0, Key.D2 or Key.NumPad2 => 1, Key.D3 or Key.NumPad3 => 2, Key.D4 or Key.NumPad4 => 3, Key.Enter => _rows[entry.Row].Chosen, _ => -1 };
        if (pick < 0 || pick >= _rows[entry.Row].Candidates.Length || _rows[entry.Row].Candidates[pick] == null) return;
        Choose(entry.Row, pick);
        e.Handled = true;
    }

    private void Choose(int rowIndex, int strand, bool keepOverrides = false)
    {
        var row = _rows[rowIndex];
        // corrections belong to the version they were made in
        if (!keepOverrides || row.Chosen != strand) row.Overrides.Clear();
        row.Chosen = strand;
        Record(rowIndex);
        FillList(SelectedEntry, nextOpen: true);
        if (_projectPath != null) _ = SaveProjectAsync(ask: false);
    }

    /// <summary>Marks a row as reviewed with its current choice (kept in the project).</summary>
    private void Record(int rowIndex)
    {
        var row = _rows[rowIndex];
        if (row.Chosen < 0) return;
        row.UserChoice = true;
        _reviewed.Add(rowIndex);
        _project.Choices.RemoveAll(c => c.Row == rowIndex);
        _project.Choices.Add(new MergeChoice { Row = rowIndex, Source = row.Chosen, Key = row.Candidates[row.Chosen]!.Key, Overrides = row.Overrides.ToList() });
    }

    /// <summary>Accepts the suggestion of every open row the condition holds for.</summary>
    private void AcceptWhere(Func<MergeRow, bool> condition)
    {
        var current = SelectedEntry;
        for (int i = 0; i < _rows.Count; i++)
            if (!_reviewed.Contains(i) && _rows[i].Chosen >= 0 && condition(_rows[i])) Record(i);
        FillList(current, nextOpen: true);
        if (_projectPath != null) _ = SaveProjectAsync(ask: false);
    }

    /// <summary>The suggested version adds up and every other version of the bar does not, because of a different rhythm
    /// (a version differing only in pitches says nothing about which one is right).</summary>
    private static bool OnlySuggestionFits(MergeRow row)
    {
        if (row.ChosenBar is not MergeBar b || !b.Fits) return false;
        var others = row.Candidates.Where((c, i) => i != row.Chosen && c != null).ToList();
        return others.Count > 0 && others.All(c => !c!.Fits && RhythmDiffers(b, c));
    }

    /// <summary>The two bars differ in a way that changes whether they add up: a length, a note more or less.</summary>
    private static bool RhythmDiffers(MergeBar x, MergeBar y) => Comparer.Diff(x, y).Any(d =>
        d.Kind is DiffKind.Duration or DiffKind.ExtraNote or DiffKind.MissingNote
        || d.Kind == DiffKind.NoteRest && d.EventA != null && d.EventB != null && d.EventA.Length.CompareTo(d.EventB.Length) != 0);

    /// <summary>The user's earlier choices, where the row still holds the same bar.</summary>
    private void ApplyChoices()
    {
        foreach (var c in _project.Choices)
            if (c.Row < _rows.Count && c.Source < _rows[c.Row].Candidates.Length && _rows[c.Row].Candidates[c.Source]?.Key == c.Key)
            {
                var row = _rows[c.Row];
                row.Chosen = c.Source; row.UserChoice = true; _reviewed.Add(c.Row);
                row.Overrides.Clear();
                if (c.Overrides != null) row.Overrides.AddRange(c.Overrides);
            }
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
