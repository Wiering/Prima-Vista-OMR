using Avalonia.Controls;
using Avalonia.Input;
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
    private static readonly string[] BarKinds = Enum.GetNames<BarlineKind>().Where(n => n != nameof(BarlineKind.SystemStart)).ToArray();
    private const string Auto = "(automatic)";

    private RecognitionEngine? _engine;
    private string? _file;
    private CorrectionStore? _store;
    private int _pageCount, _page;
    private readonly Dictionary<int, PageResult> _pages = new();
    private PageResult? _current;
    private MusicSymbol? _selSym;
    private Staff? _selStaff;
    private Barline? _selBar;
    private Measure? _selMeasure;
    private List<MusicSymbol> _measureItems = new();
    private List<(Staff staff, Measure measure, MeasureFit fit)> _fitItems = new();
    private Dictionary<Measure, ScoreMeasure> _measureMap = new();
    private bool _busy, _updating;
    private string _exportWarnings = "";

    public string? InitialFile { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        KindBox.ItemsSource = Enum.GetNames<SymbolKind>();
        DurationBox.ItemsSource = Durations;
        StemBox.ItemsSource = Stems;
        BarKindBox.ItemsSource = BarKinds;
        StaffClefBox.ItemsSource = new[] { Auto }.Concat(Enum.GetNames<ClefKind>().Where(n => n != "Unknown")).ToArray();
        StaffKeyBox.ItemsSource = new[] { Auto }.Concat(Enumerable.Range(-7, 15).Select(KeyLabel)).ToArray();

        OpenButton.Click += async (_, _) => await OpenAsync();
        PrevButton.Click += async (_, _) => await GoToPageAsync(_page - 1);
        NextButton.Click += async (_, _) => await GoToPageAsync(_page + 1);
        RecognizeButton.Click += async (_, _) => await AnalyzeAsync(_page, force: true);
        RecognizeAllButton.Click += async (_, _) => await RecognizeAllAsync();
        // jump straight to a page (it is recognised on its own; the previous page, when recognised, seeds clef, key and time)
        PageBox.ValueChanged += async (_, _) => { if (!_updating && PageBox.Value is decimal v && (int)v != _page) await GoToPageAsync((int)v); };
        UndoButton.Click += async (_, _) => await UndoAsync();
        FitsCheck.IsCheckedChanged += (_, _) => { View.ShowFits = FitsCheck.IsChecked == true; View.InvalidateVisual(); };
        FitList.SelectionChanged += (_, _) =>
        {
            if (_updating || FitList.SelectedIndex < 0 || FitList.SelectedIndex >= _fitItems.Count) return;
            var (staff, measure, _) = _fitItems[FitList.SelectedIndex];
            ShowMeasure(staff, measure);
        };
        KeyDown += OnKey;
        ExportButton.Click += async (_, _) => await ExportAsync();
        PlayButton.Click += (_, _) => Play();
        StopButton.Click += (_, _) => _player.Stop();
        _player.MeasureChanged += m => Dispatcher.UIThread.Post(() =>
        {
            // the sounding measure on the page: its measures in every part, found back through the measure map
            // (in the music as last assembled, which the map belongs to: pages recognised while playing add measures after
            // the ones before, so the measure is at the same place)
            var sc = _score ?? _playing;
            var sounding = m < 0 || sc == null ? null : sc.Parts.Where(p => m < p.Measures.Count).Select(p => p.Measures[m]).ToHashSet();
            var onPages = sounding == null ? new List<Measure>() : _measureMap.Where(kv => sounding.Contains(kv.Value)).Select(kv => kv.Key).ToList();
            int? page = sounding?.Select(sm => (int?)sm.Page).FirstOrDefault(p => p > 0);
            string number = m >= 0 && sc != null && sc.Parts.Count > 0 && m < sc.Parts[0].Measures.Count ? sc.Parts[0].Measures[m].Number.ToString() : (m + 1).ToString();
            Status(m < 0 ? "playback stopped" : page is int pg ? $"playing page {pg}, measure {number}" : $"playing measure {number}");
            // tinted only where they are on the page shown
            View.PlayingMeasures = page == _page ? onPages : null;
            View.InvalidateVisual();
        });
        Closed += (_, _) => _player.Dispose();
        // at the end of what was recognised when playback started: on with the pages recognised since, if any
        _player.ReachedEnd += end => Dispatcher.UIThread.Post(() =>
        {
            ComputeFits();
            if (_score != null && _score.Parts.Count > 0 && _score.Parts.Max(p => p.Measures.Count) > end)
            {
                _playing = _score;
                _player.Play(_score, end, 0, (double)(TempoBox.Value ?? 100));
            }
        });
        // the open score becomes version A; its choices can come back as corrections of it
        MergeButton.Click += (_, _) => new MergeWindow(() => _engine ??= new RecognitionEngine(), _file, (double)(DpiBox.Value ?? 300),
                                                       _file == null ? null : (edits, summary) => _ = TakeMergeEditsAsync(edits, summary)).Show(this);
        FitButton.Click += (_, _) => View.FitPage();
        FitWidthButton.Click += (_, _) => View.FitWidth();
        Zoom100Button.Click += (_, _) => View.SetZoom(1);
        StaffLinesCheck.IsCheckedChanged += (_, _) => { View.ShowStaffLines = StaffLinesCheck.IsChecked == true; View.InvalidateVisual(); };
        BarlinesCheck.IsCheckedChanged += (_, _) => { View.ShowBarlines = BarlinesCheck.IsChecked == true; View.InvalidateVisual(); };
        BoxesCheck.IsCheckedChanged += (_, _) => { View.ShowBoxes = BoxesCheck.IsChecked == true; View.InvalidateVisual(); };
        LabelsCheck.IsCheckedChanged += (_, _) => { View.ShowLabels = LabelsCheck.IsChecked == true; View.InvalidateVisual(); };
        SelectMode.IsCheckedChanged += (_, _) => { if (SelectMode.IsChecked == true) View.Mode = ViewMode.Select; };
        AddNoteMode.IsCheckedChanged += (_, _) => { if (AddNoteMode.IsChecked == true) View.Mode = ViewMode.AddNote; };
        AddRestMode.IsCheckedChanged += (_, _) => { if (AddRestMode.IsChecked == true) View.Mode = ViewMode.AddRest; };
        AddBarlineMode.IsCheckedChanged += (_, _) => { if (AddBarlineMode.IsChecked == true) View.Mode = ViewMode.AddBarline; };
        MoveMode.IsCheckedChanged += (_, _) => { if (MoveMode.IsChecked == true) View.Mode = ViewMode.Move; };
        View.SelectionChanged += (s, st) => { _selSym = s; _selStaff = st; _selBar = null; UpdateInspector(); };
        View.BarlineSelected += (b, st) => { _selSym = null; _selStaff = st; _selBar = b; UpdateInspector(); };
        ApplyBarButton.Click += (_, _) => ApplyBarline();
        DeleteBarButton.Click += (_, _) => DeleteBarline();
        View.AddRequested += AddSymbol;
        View.MoveFinished += (s, dx, dy) => RecordOnly(new SymbolEdit { Op = "move", Dx = dx, Dy = dy }, s);
        ApplyStaffButton.Click += (_, _) => ApplyStaff(clear: false);
        ClearStaffButton.Click += (_, _) => ApplyStaff(clear: true);
        ApplySymbolButton.Click += (_, _) => ApplySymbol();
        DeleteSymbolButton.Click += (_, _) => { if (_selSym != null) { var s = _selSym; _selSym = null; View.Select(null, _selStaff); Edit(new SymbolEdit { Op = "delete" }, s); } };
        DiscardPageButton.Click += async (_, _) => { if (_store != null) { _store.Clear(_page); await AnalyzeAsync(_page, force: true); } };
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
            _player.Stop();
            _score = null;
            _file = path;
            _pageCount = await Task.Run(() => RecognitionEngine.PageCount(path));
            _pages.Clear();
            _exportWarnings = "";
            _store = CorrectionStore.Load(path);
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
        if (_file == null || page < 1 || page > _pageCount || _busy)
        {
            _updating = true; PageBox.Value = Math.Max(1, _page); _updating = false;
            return;
        }
        _page = page;
        PageLabel.Text = $"of {_pageCount}";
        _updating = true; PageBox.Maximum = Math.Max(1, _pageCount); PageBox.Value = page; _updating = false;
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
            Status($"rendering page {page}…");
            var image = await Task.Run(() => RecognitionEngine.Render(file, page, dpi, out _));
            if (page == _page) View.SetPreview(image);
            Status(_engine == null ? $"page {page}: loading models and fonts…" : $"recognising page {page}…");
            _pages.TryGetValue(PVOMR.Core.Analysis.PageOrder.Previous(file, page), out var previous);
            var result = await Task.Run(() =>
            {
                _engine ??= new RecognitionEngine();
                return _engine.Analyze(file, page, dpi, previous, image);
            });
            int applied = _store?.ApplyAll(result) ?? 0;
            if (applied > 0) RecognitionEngine.Reinterpret(result, previous);
            _pages[page] = result;
            if (page == _page) ShowPage(result, fit: true);
            var a = result.Analysis;
            Status($"page {page}: {a.Staves.Count} staves, {a.Systems.Count} systems, {a.Staves.Sum(s => s.Measures.Count)} measures, staff space {a.StaffSpace:0.0} px, {a.Elapsed.TotalSeconds:0.0} s"
                   + (applied > 0 ? $"; {applied} stored correction(s) applied" : ""));
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
        View.SetPage(result, fit);
        ComputeFits();
        UpdateInspector();
        UpdateNotes();
        UpdateButtons();
    }

    private async Task ExportAsync()
    {
        if (_file == null || _busy) return;
        var choice = await ExportDialog.ShowAsync(this, _pages.Keys.ToList(), _pageCount);
        if (choice == null) return;
        var (options, wanted) = choice.Value;
        // pages of the range that are not recognised yet are recognised first, in order (each seeds the next)
        var missing = wanted.Where(p => !_pages.ContainsKey(p)).ToList();
        if (missing.Count > 0)
        {
            int keep = _page;
            foreach (var p in missing)
            {
                _page = p; _updating = true; PageBox.Value = p; _updating = false;
                await AnalyzeAsync(p, force: false);
                if (!_pages.ContainsKey(p)) { Status($"page {p} could not be recognised; export cancelled"); return; }
            }
            if (keep != _page) await GoToPageAsync(keep);
        }
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
            var pages = _pages.Where(p => wanted.Contains(p.Key)).OrderBy(p => p.Key).Select(p => p.Value.Analysis).ToList();
            var score = MusicAssembler.Build(pages, new MusicAssembler.Options { Title = Path.GetFileNameWithoutExtension(_file) });
            MusicXmlWriter.Write(score, path, options);
            _exportWarnings = score.Warnings.Count == 0 ? "" : "Export warnings:\n" + string.Join("\n", score.Warnings.Take(200));
            UpdateNotes();
            Status($"wrote {Path.GetFileName(path)} ({options}): {score.Parts.Count} part(s), {score.Parts.Sum(p => p.Measures.Count)} measures from {pages.Count} page(s), {score.Warnings.Count} warnings");
        }
        catch (Exception ex)
        {
            Status($"export failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ edits (all go through the correction store)

    private SymbolEdit Identify(SymbolEdit e, MusicSymbol s)
    {
        e.Staff = s.Staff?.Index ?? -1;
        e.Bounds = _current != null && _current.Origins.TryGetValue(s, out var o) ? o : s.Bounds;
        return e;
    }

    /// <summary>Applies an edit to the selected symbol, records it and refreshes.</summary>
    private void Edit(SymbolEdit e, MusicSymbol target)
    {
        if (_current == null || _store == null) return;
        Identify(e, target);
        if (!CorrectionStore.Apply(_current, e)) { Status("the edit could not be applied"); return; }
        _store.Record(_page, e);
        Changed();
    }

    /// <summary>Records an edit that the view already applied (dragging).</summary>
    private void RecordOnly(SymbolEdit e, MusicSymbol target)
    {
        if (_current == null || _store == null) return;
        Identify(e, target);
        target.Attributes["corrected"] = "1";
        _store.Record(_page, e);
        Changed();
    }

    /// <summary>Records and applies a bar line edit (identified by its staff and position) and refreshes.</summary>
    private void BarEdit(SymbolEdit e, string failure)
    {
        if (_current == null || _store == null) return;
        if (!CorrectionStore.Apply(_current, e)) { Status(failure); return; }
        _store.Record(_page, e);
        Changed();
    }

    private void ApplyBarline()
    {
        if (_selBar == null || _selStaff == null || BarKindBox.SelectedItem is not string kind) return;
        if (kind == _selBar.Kind.ToString()) { Status("nothing changed"); return; }
        BarEdit(new SymbolEdit { Op = "setBar", Staff = _selStaff.Index, X0 = _selBar.X0, X1 = _selBar.X1, Kind = kind }, "the bar line could not be changed");
    }

    private void DeleteBarline()
    {
        if (_selBar == null || _selStaff == null) return;
        var (bar, staff) = (_selBar, _selStaff);
        _selBar = null;
        View.Select(null, staff);
        BarEdit(new SymbolEdit { Op = "deleteBar", Staff = staff.Index, X0 = bar.X0, X1 = bar.X1 }, "this bar line cannot be deleted (first or last line of the staff)");
    }

    private void AddSymbol(Avalonia.Point p, Staff? staff)
    {
        if (_current == null || _store == null) return;
        if (staff == null) { Status("click inside a staff to add a symbol"); return; }
        if (View.Mode == ViewMode.AddBarline)
        {
            int bx = (int)Math.Round(p.X), half = (int)Math.Max(1, Math.Round(staff.LineThickness / 2));
            BarEdit(new SymbolEdit { Op = "addBar", Staff = staff.Index, X0 = bx - half, X1 = bx + half, Kind = nameof(BarlineKind.Single) },
                    "no bar line can be added here (too close to another one)");
            var newBar = MeasureSplitterBarAt(staff, bx);
            if (newBar != null) { _selBar = newBar; _selStaff = staff; View.Select(null, staff, newBar); UpdateInspector(); }
            return;
        }
        double ss = staff.Space;
        bool rest = View.Mode == ViewMode.AddRest;
        var e = new SymbolEdit { Op = "add", Staff = staff.Index, Kind = rest ? "Rest" : "Note", Duration = "quarter" };
        if (rest)
        {
            double cy = (staff.TopY(p.X) + staff.BottomY(p.X)) / 2;
            e.Bounds = new Rect((int)Math.Round(p.X - 0.5 * ss), (int)Math.Round(cy - ss), (int)Math.Round(p.X + 0.5 * ss), (int)Math.Round(cy + ss));
        }
        else
        {
            int step = (int)Math.Round(staff.StepAt(p.X, p.Y));
            double hy = staff.BottomY(p.X) - step * ss / 2;
            e.Step = step;
            e.Stem = step >= 4 ? "down" : "up";
            e.Bounds = new Rect((int)Math.Round(p.X - 0.6 * ss), (int)Math.Round(hy - 0.5 * ss), (int)Math.Round(p.X + 0.6 * ss), (int)Math.Round(hy + 0.5 * ss));
        }
        if (!CorrectionStore.Apply(_current, e)) { Status("no measure at this position"); return; }
        _store.Record(_page, e);
        var added = _current.Origins.FirstOrDefault(kv => kv.Value == e.Bounds && kv.Key.Attributes.ContainsKey("added")).Key;
        _selSym = added; _selStaff = staff;
        View.Select(added, staff);
        Changed();
    }

    private Barline? MeasureSplitterBarAt(Staff staff, double x)
    {
        // the staff object may have been replaced by the re-interpretation: look it up by index
        var st = _current != null && staff.Index < _current.Analysis.Staves.Count ? _current.Analysis.Staves[staff.Index] : staff;
        return PVOMR.Core.Analysis.MeasureSplitter.BarlineAt(st, x);
    }

    private void ApplyStaff(bool clear)
    {
        var staff = _selStaff ?? _selSym?.Staff;
        if (staff == null || _current == null || _store == null) return;
        if (clear) { staff.ForcedClef = null; staff.ForcedKey = null; staff.ForcedTime = null; }
        else
        {
            staff.ForcedClef = StaffClefBox.SelectedItem is string c && c != Auto && Enum.TryParse<ClefKind>(c, out var ck) ? ck : null;
            staff.ForcedKey = StaffKeyBox.SelectedItem is string k && k != Auto ? Enumerable.Range(-7, 15).First(f => KeyLabel(f) == k) : null;
            string t = (StaffTimeBox.Text ?? "").Trim();
            staff.ForcedTime = t.Length == 0 ? null : t.Equals("C", StringComparison.OrdinalIgnoreCase) ? "C (4/4)" : t;
        }
        _store.SetStaffOverride(_page, staff.Index, staff.ForcedClef, staff.ForcedKey, staff.ForcedTime);
        Changed();
    }

    private void ApplySymbol()
    {
        var s = _selSym;
        if (s == null) return;
        var e = new SymbolEdit { Op = "set" };
        if (KindBox.SelectedItem is string kind && kind != s.Kind.ToString()) e.Kind = kind;
        if (DurationBox.SelectedItem is string d && d != s.Duration) e.Duration = d;
        int dots = (int)(DotsBox.Value ?? 0);
        if (dots != s.Dots) e.Dots = dots;
        string stem = StemBox.SelectedItem as string ?? "(none)";
        string current = s.StemUp == true ? "up" : s.StemUp == false ? "down" : "(none)";
        if (stem != current) e.Stem = stem == "(none)" ? "none" : stem;
        string time = (SymTimeBox.Text ?? "").Trim();
        if (time.Length > 0 && time != CorrectionStore.TimeText(s))
        {
            if (!CorrectionStore.ValidTime(time)) { Status($"\"{time}\" is no time signature (use e.g. 3/8, C or C|)"); return; }
            e.Time = time; e.Kind = null; e.Duration = null; e.Dots = null; e.Stem = null;
        }
        if (e.Kind == null && e.Duration == null && e.Dots == null && e.Stem == null && e.Time == null) { Status("nothing changed"); return; }
        Edit(e, s);
    }

    /// <summary>Corrections made in the merge window: recorded, and the pages recognised again with them.</summary>
    private async Task TakeMergeEditsAsync(List<(int page, SymbolEdit edit)> edits, string summary)
    {
        if (_store == null || _file == null) return;
        foreach (var (page, e) in edits) _store.Record(page, e);
        _pages.Clear();
        await AnalyzeAsync(_page, force: true);
        Activate();
        Status(summary);
    }

    /// <summary>After any edit: recompute the interpretation of the page and refresh everything.</summary>
    private void Changed()
    {
        if (_current == null) return;
        _current.Modified = true;
        _pages.TryGetValue(PVOMR.Core.Analysis.PageOrder.Previous(_file ?? "", _page), out var previousPage);
        RecognitionEngine.Reinterpret(_current, previousPage);
        View.SetAnalysis(_current.Analysis);
        ComputeFits();
        UpdateInspector();
        UpdateNotes();
        Status($"page re-interpreted; {_store?.Count ?? 0} correction(s) saved to {Path.GetFileName(_store?.Path ?? "")}");
    }

    // ------------------------------------------------------------------ duration check, undo, batch, keys

    /// <summary>Assembles the pages recognised so far (cheap) to learn, per measure, whether its durations fit the time signature.</summary>
    private void ComputeFits()
    {
        _measureMap = new Dictionary<Measure, ScoreMeasure>();
        _measureStaff = new Dictionary<Measure, int>();
        if (_pages.Count == 0 || _file == null) { View.Fits = null; return; }
        try
        {
            var pages = _pages.OrderBy(p => p.Key).Select(p => p.Value.Analysis).ToList();
            var score = MusicAssembler.Build(pages, new MusicAssembler.Options { Title = Path.GetFileNameWithoutExtension(_file), MeasureMap = _measureMap, MeasureStaff = _measureStaff });
            _score = score;
            _barChecks = score.BarNumbers.Where(b => b.Page == _page).ToList();
            View.BarNumberProblems = _barChecks.Where(b => b.Agrees == false)
                                               .ToDictionary(b => b.Staff, b => $"{b.PrintedSpan} bars since bar {b.Since}, {b.CountedSpan} counted");
        }
        catch (Exception ex) { Status($"duration check failed: {ex.Message}"); }
        View.Fits = _measureMap.ToDictionary(kv => kv.Key, kv => FitOf(kv.Key));
        View.InvalidateVisual();
    }

    private Dictionary<Measure, int> _measureStaff = new();
    /// <summary>The music assembled from the pages recognised so far (for playback).</summary>
    private Score? _score;
    private readonly MidiPlayer _player = new();
    /// <summary>The score being played (the measure map points into it).</summary>
    private Score? _playing;

    /// <summary>
    /// Plays the music recognised so far: from the selected symbol (its measure, from the symbol's place in its voice),
    /// else from the first measure of this page.
    /// </summary>
    private void Play()
    {
        if (_player.Playing) { _player.Stop(); return; }
        ComputeFits();
        if (_score == null || _score.Parts.Count == 0) { Status("nothing to play: recognise a page first"); return; }
        ScoreMeasure? sm = null; int offset = 0;
        if (_selMeasure != null && _measureMap.TryGetValue(_selMeasure, out var selected))
        {
            sm = selected;
            if (_selSym != null)
            {
                // where the selected symbol starts in its voice
                int staffNo = _measureStaff.GetValueOrDefault(_selMeasure, 1);
                double sx = _selSym.Kind == SymbolKind.Rest || _selSym.Notes.Count == 0 ? _selSym.Bounds.CenterX : _selSym.Notes.Min(n => n.HeadX);
                var ev = sm.Events.Where(e => e.Staff == staffNo && !e.Grace).OrderBy(e => Math.Abs(e.X - sx)).FirstOrDefault();
                if (ev != null) offset = sm.Events.Where(e => e.Voice == ev.Voice && !e.Grace && e.X < ev.X).Sum(e => e.Duration);
            }
        }
        else if (_current != null)
        {
            // the first measure of this page
            var first = _current.Analysis.Staves.SelectMany(st => st.Measures).Where(m => _measureMap.ContainsKey(m)).OrderBy(m => m.Staff.Index).ThenBy(m => m.X0).FirstOrDefault();
            if (first != null) sm = _measureMap[first];
        }
        int index = 0;
        if (sm != null)
            foreach (var part in _score.Parts) { int i = part.Measures.IndexOf(sm); if (i >= 0) { index = i; break; } }
        _playing = _score;
        _player.Play(_score, index, offset, (double)(TempoBox.Value ?? 100));
    }

    /// <summary>What a symbol adds to its measure's total, in quarters ("+0.5 q"), from the assembled score (after the
    /// duration check: dots taken, notes dropped); empty for symbols that take no time.</summary>
    private string Contribution(MusicSymbol s, Measure? m)
    {
        const string blank = "            ";
        if (m == null || s.Kind is not (SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup or SymbolKind.Rest)) return blank;
        if (!_measureMap.TryGetValue(m, out var sm)) return blank;
        int staffNo = _measureStaff.GetValueOrDefault(m, 1);
        static int X(MusicSymbol x) => (int)Math.Round(x.Kind == SymbolKind.Rest || x.Notes.Count == 0 ? x.Bounds.CenterX : x.Notes.Min(n => n.HeadX));
        var parts = s.Kind == SymbolKind.NoteGroup && s.Children.Count > 0 ? s.Children : new List<MusicSymbol> { s };
        int total = 0, found = 0; var voices = new SortedSet<int>();
        foreach (var p in parts)
        {
            var ev = sm.Events.FirstOrDefault(e => e.Staff == staffNo && e.IsRest == (p.Kind == SymbolKind.Rest) && Math.Abs(e.X - X(p)) <= 2);
            if (ev == null) continue;
            found++; voices.Add(ev.Voice);
            if (!ev.Grace) total += ev.Duration;
        }
        if (found == 0) return "not counted ";
        string voice = sm.Events.Select(e => e.Voice).Distinct().Count() > 1 ? $" v{string.Join(",", voices)}" : "";
        return ($"+{Quarters(total)} q{voice}").PadRight(blank.Length);
    }

    private List<BarNumberCheck> _barChecks = new();

    /// <summary>The printed bar numbers of this page against the measure count: where they disagree a bar line or a
    /// multi-measure rest was missed (or the number was misread).</summary>
    private string BarNumberText()
    {
        if (_barChecks.Count == 0) return "";
        var checkd = _barChecks.Where(b => b.Agrees != null).ToList();
        var bad = checkd.Where(b => b.Agrees == false).ToList();
        return $"{Environment.NewLine}Bar numbers: {_barChecks.Count} read on this page, {checkd.Count - bad.Count} of {checkd.Count} agree with the count."
               + string.Concat(bad.Select(b => $"{Environment.NewLine}  bars {b.Since}-{b.Printed} (up to staff {b.Staff + 1}): {b.PrintedSpan} printed, {b.CountedSpan} counted"));
    }
    private MeasureFit FitOf(Measure m) => _measureMap[m].Fit.ForStaff(_measureStaff.GetValueOrDefault(m, 1));

    private static string Quarters(int divisions) => (divisions / (double)Score.Divisions).ToString("0.##");

    private static string FitText(MeasureFit fit)
    {
        if (fit.Expected == null) return "no time signature known";
        var parts = fit.Sums.Select(s => $"{(fit.Sums.Count > 1 ? $"v{s.Voice}: " : "")}{Quarters(s.Sum)}");
        string sums = fit.Sums.Count == 0 ? "empty" : string.Join(", ", parts);
        if (fit.Dropped > 0) return $"{sums} of {Quarters(fit.Expected.Value)} quarters: {fit.Dropped} uncertain note(s) dropped to make it fit, check them";
        string state = fit.Fits ? "fits" : fit.TooLong ? "too long" : "too short";
        return $"{sums} of {Quarters(fit.Expected.Value)} quarters: {state}";
    }

    private void UpdateFitList()
    {
        _updating = true;
        try
        {
            _fitItems = new();
            if (_current != null)
                foreach (var st in _current.Analysis.Staves)
                    foreach (var m in st.Measures)
                        if (_measureMap.ContainsKey(m)) { var f = FitOf(m); if (!f.Fits || f.Dropped > 0) _fitItems.Add((st, m, f)); }
            FitList.ItemsSource = _fitItems.Select(f => $"{f.staff.Index + 1}.{f.measure.Index + 1}: {FitText(f.fit)}").ToList();
            int total = _current?.Analysis.Staves.Sum(s => s.Measures.Count) ?? 0;
            FitSummary.Text = (total == 0 ? "" : _fitItems.Count == 0 ? $"All {total} measures fit their time signature." : $"{_fitItems.Count} of {total} measures do not add up (red: too long, orange: too short, purple: notes dropped). Click to jump.")
                              + BarNumberText();
        }
        finally { _updating = false; }
    }

    private async Task UndoAsync()
    {
        if (_store == null || _busy) return;
        if (!_store.UndoLast(_page)) { Status("nothing to undo on this page"); return; }
        await AnalyzeAsync(_page, force: true);
        Status($"undone; {_store.Count} correction(s) remain");
    }

    private async Task RecognizeAllAsync()
    {
        if (_file == null || _busy) return;
        for (int p = 1; p <= _pageCount; p++)
        {
            if (_pages.ContainsKey(p)) continue;
            _page = p; PageLabel.Text = $"of {_pageCount}"; _updating = true; PageBox.Value = p; _updating = false;
            await AnalyzeAsync(p, force: false);
        }
        Status($"all {_pageCount} pages recognised; export covers the whole file");
    }

    private async void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox || _busy) return;
        switch (e.Key)
        {
            case Key.Delete when _selBar != null:
                DeleteBarline(); e.Handled = true; break;
            case Key.Delete when _selSym != null:
            {
                var s = _selSym; _selSym = null; View.Select(null, _selStaff);
                Edit(new SymbolEdit { Op = "delete" }, s); e.Handled = true; break;
            }
            case Key.Up or Key.Down when _selSym != null && CorrectionStore.Heads(_selSym).Count == 1:
            {
                var head = CorrectionStore.Heads(_selSym)[0];
                Edit(new SymbolEdit { Op = "head", Head = 0, Step = head.Step + (e.Key == Key.Up ? 1 : -1) }, _selSym); e.Handled = true; break;
            }
            case Key.Z when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                await UndoAsync(); e.Handled = true; break;
            case Key.PageDown: await GoToPageAsync(_page + 1); e.Handled = true; break;
            case Key.PageUp: await GoToPageAsync(_page - 1); e.Handled = true; break;
            case Key.Escape: SelectMode.IsChecked = true; e.Handled = true; break;
            case Key.Space: Play(); e.Handled = true; break;
        }
    }

    // ------------------------------------------------------------------ inspector

    private void UpdateInspector(bool keepList = false, Measure? measure = null)
    {
        _updating = true;
        try
        {
            var staff = _selStaff ?? _selSym?.Staff;
            if (staff != null)
            {
                StaffInfo.Text = $"Staff {staff.Index + 1} (system {staff.SystemIndex + 1}){(staff.InstrumentName != null ? $", \"{staff.InstrumentName}\"" : "")}: clef {staff.Clef}, key {KeyLabel(staff.KeyFifths)}, time {staff.TimeSignature ?? "?"}, {staff.Measures.Count} measures, staff space {staff.Space:0.0} px"
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

            if (_selBar != null)
            {
                BarlineInfo.Text = $"{_selBar.Kind} bar line at x={_selBar.X:0} on staff {(staff?.Index ?? -1) + 1}" + (_selBar.Spanning ? $" (spans staves {_selBar.StaffFrom + 1}-{_selBar.StaffTo + 1})" : "");
                BarKindBox.SelectedItem = BarKinds.Contains(_selBar.Kind.ToString()) ? _selBar.Kind.ToString() : null;
            }
            else
            {
                BarlineInfo.Text = "Click a bar line to select it; 'Add bar line' adds one where you click.";
                BarKindBox.SelectedItem = null;
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
                SymTimeBox.Text = CorrectionStore.TimeText(s);
            }
            else
            {
                SymbolInfo.Text = "(none)";
                KindBox.SelectedItem = null; DurationBox.SelectedItem = null; DotsBox.Value = 0; StemBox.SelectedItem = null;
                SymTimeBox.Text = "";
            }
            BuildHeadsPanel(s);

            if (!keepList)
            {
                _selMeasure = measure ?? (s != null && staff != null ? staff.Measures.FirstOrDefault(m => m.Symbols.Contains(s) || m.Symbols.Any(p => p.Children.Contains(s))) : null);
                _measureItems = _selMeasure?.Symbols.OrderBy(x => x.Bounds.X0).ToList() ?? new List<MusicSymbol>();
                double ss = staff?.Space ?? 20;
                MeasureList.ItemsSource = _measureItems.Select(x => Contribution(x, _selMeasure) + TextReport.Describe(x, ss, false)).ToList();
            }
            MeasureList.SelectedIndex = s != null ? _measureItems.IndexOf(s) : -1;
            MeasureFitText.Text = _selMeasure != null && _measureMap.ContainsKey(_selMeasure)
                ? $"Measure {_selMeasure.Staff.Index + 1}.{_selMeasure.Index + 1}: {FitText(FitOf(_selMeasure))}" : "";
            UpdateFitList();
            CorrectionsInfo.Text = _store == null ? "" : _store.Count == 0
                ? "No corrections yet. They are saved next to the score and replayed when a page is recognised again."
                : $"{_store.Count} correction(s) saved in {Path.GetFileName(_store.Path)}" + (_store.HasPage(_page) ? "" : " (none on this page)");
            RefreshCorrections();
        }
        finally { _updating = false; }
        UpdateButtons();
    }

    /// <summary>Shows a measure: selected, scrolled into view, its symbols listed.</summary>
    private void ShowMeasure(Staff staff, Measure measure, MusicSymbol? symbol = null)
    {
        _selSym = symbol; _selStaff = staff; _selMeasure = measure;
        View.Select(symbol, staff);
        View.EnsureVisible(new Rect((int)measure.X0, (int)(staff.TopY(measure.X0) - 2 * staff.Space), (int)measure.X1, (int)(staff.BottomY(measure.X0) + 2 * staff.Space)));
        UpdateInspector(measure: measure);
    }

    // ------------------------------------------------------------------ the list of corrected measures

    /// <summary>The corrections of one measure (Measure -1: the page is not recognised yet, or outside the measures; -2: staff settings).</summary>
    private sealed record CorrectionGroup(int Page, int Staff, int Measure, string Where, List<SymbolEdit> Edits, List<StaffOverride> Staves);

    private string _correctionsShown = "";

    private List<CorrectionGroup> CorrectionGroups()
    {
        var groups = new List<CorrectionGroup>();
        if (_store == null) return groups;
        foreach (var pc in _store.Pages)
        {
            _pages.TryGetValue(pc.Page, out var pr);
            var byKey = new Dictionary<(int staff, int measure), CorrectionGroup>();
            foreach (var e in pc.Edits)
            {
                int mi = -1; string where;
                if (e.Staff < 0) where = "outside the staves";
                else if (pr != null && e.Staff < pr.Analysis.Staves.Count && CorrectionStore.MeasureAt(pr.Analysis.Staves[e.Staff], (e.X0 + e.X1) / 2.0) is Measure m)
                { mi = m.Index; where = $"{e.Staff + 1}.{m.Index + 1}"; }
                else where = $"staff {e.Staff + 1}";
                if (!byKey.TryGetValue((e.Staff, mi), out var g)) byKey[(e.Staff, mi)] = g = new CorrectionGroup(pc.Page, e.Staff, mi, where, new(), new());
                g.Edits.Add(e);
            }
            foreach (var so in pc.Staves)
            {
                if (!byKey.TryGetValue((so.Staff, -2), out var g)) byKey[(so.Staff, -2)] = g = new CorrectionGroup(pc.Page, so.Staff, -2, $"staff {so.Staff + 1} settings", new(), new());
                g.Staves.Add(so);
            }
            groups.AddRange(byKey.Values.OrderBy(g => g.Staff).ThenBy(g => g.Measure));
        }
        return groups;
    }

    /// <summary>The corrected measures, each clickable (shows it) with a button that removes its corrections.</summary>
    private void RefreshCorrections()
    {
        string signature = _store == null ? "" : $"{_store.Path}|{_store.Count}|{_store.Pages.Sum(p => p.Edits.Sum(e => e.GetHashCode()))}|{string.Join(",", _pages.Keys.OrderBy(k => k))}|{_page}";
        if (signature == _correctionsShown) return;
        _correctionsShown = signature;
        CorrectionsList.Children.Clear();
        foreach (var g in CorrectionGroups())
        {
            var parts = g.Edits.GroupBy(e => e.Op).Select(o => o.Count() > 1 ? $"{OpName(o.Key)} ×{o.Count()}" : OpName(o.Key))
                         .Concat(g.Staves.Select(so => string.Join(" ", new[] { so.Clef, so.Key is int k ? $"key {k}" : null, so.Time }.Where(x => x != null))));
            var details = g.Edits.Select(CorrectionStore.Describe).Concat(g.Staves.Select(so => $"clef {so.Clef ?? "-"}, key {so.Key?.ToString() ?? "-"}, time {so.Time ?? "-"}"));
            var link = new Button
            {
                Content = $"p.{g.Page}  {g.Where}:  {string.Join(", ", parts)}", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left, Background = Avalonia.Media.Brushes.Transparent, Padding = new Avalonia.Thickness(4, 1), FontSize = 11,
                FontWeight = g.Page == _page ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal,
            };
            ToolTip.SetTip(link, string.Join(Environment.NewLine, details));
            var group = g;
            link.Click += async (_, _) => await ShowCorrectionAsync(group);
            var remove = new Button { Content = "✕", Padding = new Avalonia.Thickness(6, 1), FontSize = 11 };
            ToolTip.SetTip(remove, "Remove these corrections (the page is recognised again)");
            remove.Click += async (_, _) => await RemoveCorrectionsAsync(group);
            var row = new DockPanel();
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            row.Children.Add(link);
            CorrectionsList.Children.Add(row);
        }
    }

    private static string OpName(string op) => op switch
    {
        "set" => "changed", "head" => "pitch", "removeHead" => "head removed", "accidental" => "accidental", "add" => "added", "delete" => "deleted",
        "move" => "moved", "addBar" => "bar line added", "deleteBar" => "bar line removed", "setBar" => "bar line kind", _ => op,
    };

    private async Task ShowCorrectionAsync(CorrectionGroup g)
    {
        if (_busy) return;
        if (g.Page != _page) await GoToPageAsync(g.Page);
        if (_current == null || g.Staff < 0 || g.Staff >= _current.Analysis.Staves.Count) return;
        var staff = _current.Analysis.Staves[g.Staff];
        var first = g.Edits.FirstOrDefault();
        var measure = first != null ? CorrectionStore.MeasureAt(staff, (first.X0 + first.X1) / 2.0) : staff.Measures.FirstOrDefault();
        if (measure == null) return;
        // the corrected symbol itself, when it is still there (not for a deletion)
        var symbol = g.Edits.Where(e => e.Op is not ("delete" or "addBar" or "deleteBar" or "setBar")).Select(e => CorrectionStore.Find(_current, e)).FirstOrDefault(x => x != null);
        ShowMeasure(staff, measure, symbol);
        Status($"page {g.Page}, measure {g.Where}: " + string.Join("; ", g.Edits.Select(CorrectionStore.Describe)));
    }

    private async Task RemoveCorrectionsAsync(CorrectionGroup g)
    {
        if (_store == null || _busy) return;
        _store.Remove(g.Page, g.Edits, g.Staves);
        _pages.Remove(g.Page);
        if (g.Page == _page) await AnalyzeAsync(g.Page, force: true);
        else RefreshCorrections();
        Status($"removed {g.Edits.Count + g.Staves.Count} correction(s) of page {g.Page}, measure {g.Where}; {_store.Count} remain");
    }

    private void BuildHeadsPanel(MusicSymbol? s)
    {
        HeadsPanel.Children.Clear();
        if (s == null) return;
        var notes = CorrectionStore.Heads(s);
        for (int i = 0; i < notes.Count; i++)
        {
            var note = notes[i]; int index = i;
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(new TextBlock { Text = $"{note.Pitch ?? "?"}  (step {note.Step}, {note.HeadType})", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, MinWidth = 170 });
            var up = new Button { Content = "▲", Padding = new Avalonia.Thickness(6, 2) };
            var down = new Button { Content = "▼", Padding = new Avalonia.Thickness(6, 2) };
            up.Click += (_, _) => Edit(new SymbolEdit { Op = "head", Head = index, Step = note.Step + 1 }, s);
            down.Click += (_, _) => Edit(new SymbolEdit { Op = "head", Head = index, Step = note.Step - 1 }, s);
            row.Children.Add(up); row.Children.Add(down);
            if (notes.Count > 1)
            {
                var remove = new Button { Content = "✕", Padding = new Avalonia.Thickness(6, 2) };
                remove.Click += (_, _) => Edit(new SymbolEdit { Op = "removeHead", Head = index }, s);
                row.Children.Add(remove);
            }
            HeadsPanel.Children.Add(row);
        }
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
        RecognizeAllButton.IsEnabled = hasFile && _pages.Count < _pageCount;
        UndoButton.IsEnabled = hasFile && _store != null && _store.HasPage(_page);
        ExportButton.IsEnabled = _file != null && !_busy;
        OpenButton.IsEnabled = !_busy;
        DiscardPageButton.IsEnabled = hasFile && _store != null && _store.HasPage(_page);
        bool hasStaff = (_selStaff ?? _selSym?.Staff) != null;
        ApplyStaffButton.IsEnabled = hasStaff; ClearStaffButton.IsEnabled = hasStaff;
        ApplySymbolButton.IsEnabled = _selSym != null; DeleteSymbolButton.IsEnabled = _selSym != null;
        ApplyBarButton.IsEnabled = _selBar != null; DeleteBarButton.IsEnabled = _selBar != null;
    }

    private void SetBusy(bool busy) { _busy = busy; UpdateButtons(); }

    private void Status(string text) => Dispatcher.UIThread.Post(() => StatusText.Text = text);
}
