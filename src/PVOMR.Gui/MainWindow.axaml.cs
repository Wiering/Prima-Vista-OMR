using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PVOMR.Core.Model;
using PVOMR.Core.Output;
using PVOMR.Core.Score;

namespace PVOMR.Gui;

public partial class MainWindow : Window
{
    private static readonly string[] Durations = { "whole", "half", "quarter", "8th", "16th", "32nd", "64th" };
    private static readonly string[] Stems = { "(none)", "up", "down" };
    private const string Auto = "(automatic)";

    private RecognitionEngine? _engine;
    private string? _file;
    private int _pageCount, _page;
    private readonly Dictionary<int, PageResult> _pages = new();
    private PageResult? _current;
    private MusicSymbol? _selSym;
    private Staff? _selStaff;
    private Measure? _selMeasure;
    private List<MusicSymbol> _measureItems = new();
    private bool _busy, _updating;
    private string _exportWarnings = "";

    public string? InitialFile { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        KindBox.ItemsSource = Enum.GetNames<SymbolKind>();
        DurationBox.ItemsSource = Durations;
        StemBox.ItemsSource = Stems;
        StaffClefBox.ItemsSource = new[] { Auto }.Concat(Enum.GetNames<ClefKind>().Where(n => n != "Unknown")).ToArray();
        StaffKeyBox.ItemsSource = new[] { Auto }.Concat(Enumerable.Range(-7, 15).Select(KeyLabel)).ToArray();

        OpenButton.Click += async (_, _) => await OpenAsync();
        PrevButton.Click += async (_, _) => await GoToPageAsync(_page - 1);
        NextButton.Click += async (_, _) => await GoToPageAsync(_page + 1);
        RecognizeButton.Click += async (_, _) => await AnalyzeAsync(_page, force: true);
        ExportButton.Click += async (_, _) => await ExportAsync();
        FitButton.Click += (_, _) => View.FitPage();
        FitWidthButton.Click += (_, _) => View.FitWidth();
        Zoom100Button.Click += (_, _) => View.SetZoom(1);
        StaffLinesCheck.IsCheckedChanged += (_, _) => { View.ShowStaffLines = StaffLinesCheck.IsChecked == true; View.InvalidateVisual(); };
        BarlinesCheck.IsCheckedChanged += (_, _) => { View.ShowBarlines = BarlinesCheck.IsChecked == true; View.InvalidateVisual(); };
        BoxesCheck.IsCheckedChanged += (_, _) => { View.ShowBoxes = BoxesCheck.IsChecked == true; View.InvalidateVisual(); };
        LabelsCheck.IsCheckedChanged += (_, _) => { View.ShowLabels = LabelsCheck.IsChecked == true; View.InvalidateVisual(); };
        View.SelectionChanged += (s, st) => { _selSym = s; _selStaff = st; UpdateInspector(); };
        ApplyStaffButton.Click += (_, _) => ApplyStaff(clear: false);
        ClearStaffButton.Click += (_, _) => ApplyStaff(clear: true);
        ApplySymbolButton.Click += (_, _) => ApplySymbol();
        DeleteSymbolButton.Click += (_, _) => DeleteSymbol();
        MeasureList.SelectionChanged += (_, _) =>
        {
            if (_updating || MeasureList.SelectedIndex < 0 || MeasureList.SelectedIndex >= _measureItems.Count) return;
            _selSym = _measureItems[MeasureList.SelectedIndex];
            View.Select(_selSym, _selStaff);
            View.EnsureVisible(_selSym.Bounds);
            UpdateInspector(keepList: true);
        };
        Opened += async (_, _) => { if (InitialFile != null) await LoadFileAsync(InitialFile); };
        UpdateButtons();
    }

    private static string KeyLabel(int fifths) => fifths switch
    {
        0 => "0 (C major / A minor)",
        > 0 => $"{fifths} sharp{(fifths > 1 ? "s" : "")}",
        _ => $"{-fifths} flat{(fifths < -1 ? "s" : "")}",
    };

    // ------------------------------------------------------------------ files and pages

    private async Task OpenAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a score",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Scores (PDF, images)") { Patterns = new[] { "*.pdf", "*.png", "*.jpg", "*.jpeg", "*.tif", "*.tiff", "*.bmp", "*.webp" } },
                FilePickerFileTypes.All,
            },
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path != null) await LoadFileAsync(path);
    }

    private async Task LoadFileAsync(string path)
    {
        try
        {
            _file = path;
            _pageCount = await Task.Run(() => RecognitionEngine.PageCount(path));
            _pages.Clear();
            _exportWarnings = "";
            Title = $"PVOMR – {Path.GetFileName(path)}";
            await GoToPageAsync(1);
        }
        catch (Exception ex)
        {
            Status($"cannot open {path}: {ex.Message}");
        }
    }

    private async Task GoToPageAsync(int page)
    {
        if (_file == null || page < 1 || page > _pageCount || _busy) return;
        _page = page;
        PageLabel.Text = $"page {_page} / {_pageCount}";
        await AnalyzeAsync(page, force: false);
    }

    private async Task AnalyzeAsync(int page, bool force)
    {
        if (_file == null || _busy) return;
        if (!force && _pages.TryGetValue(page, out var cached)) { ShowPage(cached, fit: true); return; }
        SetBusy(true);
        try
        {
            string file = _file;
            double dpi = (double)(DpiBox.Value ?? 300);
            Status(_engine == null ? "loading models and fonts…" : $"recognising page {page}…");
            var result = await Task.Run(() =>
            {
                _engine ??= new RecognitionEngine();
                return _engine.Analyze(file, page, dpi);
            });
            _pages[page] = result;
            ShowPage(result, fit: true);
            var a = result.Analysis;
            Status($"page {page}: {a.Staves.Count} staves, {a.Systems.Count} systems, {a.Staves.Sum(s => s.Measures.Count)} measures, staff space {a.StaffSpace:0.0} px, {a.Elapsed.TotalSeconds:0.0} s");
        }
        catch (Exception ex)
        {
            Status($"recognition failed: {ex.Message}");
        }
        finally { SetBusy(false); }
    }

    private void ShowPage(PageResult result, bool fit)
    {
        _current = result;
        _selSym = null; _selStaff = null; _selMeasure = null;
        View.SetPage(result.Context.Gray, result.Analysis, fit);
        UpdateInspector();
        UpdateNotes();
        UpdateButtons();
    }

    private async Task ExportAsync()
    {
        if (_pages.Count == 0 || _file == null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export MusicXML",
            SuggestedFileName = Path.GetFileNameWithoutExtension(_file) + ".musicxml",
            DefaultExtension = "musicxml",
            FileTypeChoices = new[] { new FilePickerFileType("MusicXML") { Patterns = new[] { "*.musicxml", "*.xml" } } },
        });
        var path = file?.TryGetLocalPath();
        if (path == null) return;
        try
        {
            var pages = _pages.OrderBy(p => p.Key).Select(p => p.Value.Analysis).ToList();
            var score = MusicAssembler.Build(pages, new MusicAssembler.Options { Title = Path.GetFileNameWithoutExtension(_file) });
            MusicXmlWriter.Write(score, path);
            _exportWarnings = score.Warnings.Count == 0 ? "" : "Export warnings:\n" + string.Join("\n", score.Warnings.Take(200));
            UpdateNotes();
            Status($"wrote {Path.GetFileName(path)}: {score.Parts.Count} part(s), {score.Parts.Sum(p => p.Measures.Count)} measures from {pages.Count} page(s), {score.Warnings.Count} warnings");
        }
        catch (Exception ex)
        {
            Status($"export failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ inspector

    private void UpdateInspector(bool keepList = false)
    {
        _updating = true;
        try
        {
            var staff = _selStaff ?? _selSym?.Staff;
            if (staff != null)
            {
                StaffInfo.Text = $"Staff {staff.Index + 1} (system {staff.SystemIndex + 1}): clef {staff.Clef}, key {KeyLabel(staff.KeyFifths)}, time {staff.TimeSignature ?? "?"}, {staff.Measures.Count} measures, staff space {staff.Space:0.0} px"
                                 + (staff.ForcedClef != null || staff.ForcedKey != null || staff.ForcedTime != null ? "  [corrected]" : "");
                StaffClefBox.SelectedItem = staff.ForcedClef?.ToString() ?? Auto;
                StaffKeyBox.SelectedItem = staff.ForcedKey is int k ? KeyLabel(k) : Auto;
                StaffTimeBox.Text = staff.ForcedTime ?? "";
            }
            else
            {
                StaffInfo.Text = "Click a staff or a symbol.";
                StaffClefBox.SelectedItem = Auto; StaffKeyBox.SelectedItem = Auto; StaffTimeBox.Text = "";
            }

            var s = _selSym;
            if (s != null)
            {
                double ss = staff?.Space ?? _current?.Analysis.StaffSpace ?? 20;
                SymbolInfo.Text = TextReport.Describe(s, ss, true);
                KindBox.SelectedItem = s.Kind.ToString();
                DurationBox.SelectedItem = s.Duration != null && Durations.Contains(s.Duration.TrimEnd('?')) ? s.Duration.TrimEnd('?') : null;
                DotsBox.Value = s.Dots;
                StemBox.SelectedItem = s.StemUp == true ? "up" : s.StemUp == false ? "down" : "(none)";
            }
            else
            {
                SymbolInfo.Text = "(none)";
                KindBox.SelectedItem = null; DurationBox.SelectedItem = null; DotsBox.Value = 0; StemBox.SelectedItem = null;
            }
            BuildHeadsPanel(s);

            if (!keepList)
            {
                _selMeasure = s != null && staff != null ? staff.Measures.FirstOrDefault(m => m.Symbols.Contains(s) || m.Symbols.Any(p => p.Children.Contains(s))) : null;
                _measureItems = _selMeasure?.Symbols.OrderBy(x => x.Bounds.X0).ToList() ?? new List<MusicSymbol>();
                double ss = staff?.Space ?? 20;
                MeasureList.ItemsSource = _measureItems.Select(x => TextReport.Describe(x, ss, false)).ToList();
            }
            MeasureList.SelectedIndex = s != null ? _measureItems.IndexOf(s) : -1;
        }
        finally { _updating = false; }
        UpdateButtons();
    }

    private void BuildHeadsPanel(MusicSymbol? s)
    {
        HeadsPanel.Children.Clear();
        if (s == null) return;
        var notes = s.Notes.Count > 0 ? s.Notes : s.Children.SelectMany(c => c.Notes).ToList();
        foreach (var n in notes)
        {
            var note = n;
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(new TextBlock { Text = $"{note.Pitch ?? "?"}  (step {note.Step}, {note.HeadType})", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, MinWidth = 170 });
            var up = new Button { Content = "▲", Padding = new Avalonia.Thickness(6, 2) };
            var down = new Button { Content = "▼", Padding = new Avalonia.Thickness(6, 2) };
            up.Click += (_, _) => { note.Step++; Changed(); };
            down.Click += (_, _) => { note.Step--; Changed(); };
            row.Children.Add(up); row.Children.Add(down);
            if (notes.Count > 1)
            {
                var remove = new Button { Content = "✕", Padding = new Avalonia.Thickness(6, 2) };
                remove.Click += (_, _) =>
                {
                    s.Notes.Remove(note);
                    foreach (var c in s.Children) c.Notes.Remove(note);
                    Changed();
                };
                row.Children.Add(remove);
            }
            HeadsPanel.Children.Add(row);
        }
    }

    private void ApplyStaff(bool clear)
    {
        var staff = _selStaff ?? _selSym?.Staff;
        if (staff == null || _current == null) return;
        if (clear) { staff.ForcedClef = null; staff.ForcedKey = null; staff.ForcedTime = null; }
        else
        {
            staff.ForcedClef = StaffClefBox.SelectedItem is string c && c != Auto && Enum.TryParse<ClefKind>(c, out var ck) ? ck : null;
            staff.ForcedKey = StaffKeyBox.SelectedItem is string k && k != Auto ? Enumerable.Range(-7, 15).First(f => KeyLabel(f) == k) : null;
            string t = (StaffTimeBox.Text ?? "").Trim();
            staff.ForcedTime = t.Length == 0 ? null : t.Equals("C", StringComparison.OrdinalIgnoreCase) ? "C (4/4)" : t;
        }
        Changed();
    }

    private void ApplySymbol()
    {
        var s = _selSym;
        if (s == null) return;
        if (KindBox.SelectedItem is string kind && Enum.TryParse<SymbolKind>(kind, out var k)) s.Kind = k;
        if (DurationBox.SelectedItem is string d) s.Duration = d;
        s.Dots = (int)(DotsBox.Value ?? 0);
        foreach (var n in s.Notes) n.Dots = s.Dots;
        s.StemUp = StemBox.SelectedItem switch { "up" => true, "down" => false, _ => null };
        s.Attributes["corrected"] = "1";
        Changed();
    }

    private void DeleteSymbol()
    {
        var s = _selSym;
        if (s == null || _current == null) return;
        foreach (var st in _current.Analysis.Staves)
            foreach (var m in st.Measures)
            {
                if (m.Symbols.Remove(s)) goto done;
                foreach (var p in m.Symbols) if (p.Children.Remove(s)) goto done;
            }
        _current.Analysis.PageSymbols.Remove(s);
        done:
        _selSym = null;
        View.Select(null, _selStaff);
        Changed();
    }

    /// <summary>After any edit: recompute the interpretation of the page and refresh everything.</summary>
    private void Changed()
    {
        if (_current == null) return;
        _current.Modified = true;
        RecognitionEngine.Reinterpret(_current);
        View.SetAnalysis(_current.Analysis);
        UpdateInspector();
        UpdateNotes();
        Status("page re-interpreted after the correction (export to see the result)");
    }

    private void UpdateNotes()
    {
        var notes = _current?.Analysis.Notes ?? new List<string>();
        NotesBox.Text = string.Join("\n", notes) + (_exportWarnings.Length > 0 ? "\n\n" + _exportWarnings : "");
    }

    private void UpdateButtons()
    {
        bool hasFile = _file != null && !_busy;
        PrevButton.IsEnabled = hasFile && _page > 1;
        NextButton.IsEnabled = hasFile && _page < _pageCount;
        RecognizeButton.IsEnabled = hasFile;
        ExportButton.IsEnabled = _pages.Count > 0 && !_busy;
        OpenButton.IsEnabled = !_busy;
        bool hasStaff = (_selStaff ?? _selSym?.Staff) != null;
        ApplyStaffButton.IsEnabled = hasStaff; ClearStaffButton.IsEnabled = hasStaff;
        ApplySymbolButton.IsEnabled = _selSym != null; DeleteSymbolButton.IsEnabled = _selSym != null;
    }

    private void SetBusy(bool busy) { _busy = busy; UpdateButtons(); }

    private void Status(string text) => Dispatcher.UIThread.Post(() => StatusText.Text = text);
}
