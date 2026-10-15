using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PVOMR.Core.Imaging;
using PVOMR.Core.Model;

namespace PVOMR.Gui;

/// <summary>
/// Shows the (deskewed) page image with the recognised staves, bar lines and symbols drawn over it. Wheel zooms around
/// the pointer, dragging pans, a click selects the smallest symbol under the pointer.
/// </summary>
public enum ViewMode { Select, AddNote, AddRest, AddBarline, Move }

public sealed class PageView : Control
{
    private WriteableBitmap? _bitmap;
    private PageAnalysis? _analysis;
    private PageResult? _page;
    private ViewMode _mode = ViewMode.Select;
    private bool _moving;
    private double _accX, _accY;
    private int _movedX, _movedY;
    private double _zoom = 1;
    private Point _offset;
    private Point _pressPos, _lastDrag;
    private bool _dragging, _moved;
    private MusicSymbol? _selected;
    private Staff? _selectedStaff;
    private Barline? _selectedBarline;

    public event Action<MusicSymbol?, Staff?>? SelectionChanged;
    /// <summary>A bar line was clicked (with its staff).</summary>
    public event Action<Barline, Staff>? BarlineSelected;
    /// <summary>A click in an Add mode: image coordinates and the staff under the pointer.</summary>
    public event Action<Point, Staff?>? AddRequested;
    /// <summary>A symbol was dragged in Move mode by (dx, dy) image pixels (already applied to the symbol).</summary>
    public event Action<MusicSymbol, int, int>? MoveFinished;

    public ViewMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            Cursor = value switch { ViewMode.AddNote or ViewMode.AddRest or ViewMode.AddBarline => new Cursor(StandardCursorType.Cross), ViewMode.Move => new Cursor(StandardCursorType.SizeAll), _ => Cursor.Default };
        }
    }

    /// <summary>Duration check per measure (from the assembler); measures that do not fit are tinted.</summary>
    public Dictionary<Measure, PVOMR.Core.Score.MeasureFit>? Fits { get; set; }
    public bool ShowFits { get; set; } = true;
    /// <summary>Printed bar numbers that disagree with the measure count, by staff index: what the check found.</summary>
    public Dictionary<int, string>? BarNumberProblems { get; set; }

    public bool ShowStaffLines { get; set; } = true;
    public bool ShowBoxes { get; set; } = true;
    public bool ShowLabels { get; set; } = true;
    public bool ShowBarlines { get; set; } = true;

    public MusicSymbol? Selected { get => _selected; set { _selected = value; InvalidateVisual(); } }
    /// <summary>When set, only these recognised symbols are drawn (framed), nothing else: the merge view shows just the
    /// notes that differ between versions.</summary>
    public IReadOnlyCollection<MusicSymbol>? OnlySymbols { get; set; }
    /// <summary>With OnlySymbols: the text around each symbol (pitch above, length below, intervals on either side).</summary>
    public IReadOnlyDictionary<MusicSymbol, SymbolLabel>? Labels { get; set; }
    /// <summary>With OnlySymbols: where a note missing in this version would be (image coordinates).</summary>
    public PVOMR.Core.Model.Rect? Marker { get; set; }
    public Staff? SelectedStaff => _selectedStaff;
    public Barline? SelectedBarline => _selectedBarline;
    public double Zoom => _zoom;

    public PageView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public void SetPage(PageResult? page, bool fit)
    {
        // the same page again (another bar on it): the image is kept
        if (page == null || page != _page || _bitmap == null) _bitmap = page == null ? null : ToBitmap(page.Context.Gray);
        _page = page;
        _analysis = page?.Analysis;
        _selected = null; _selectedStaff = null; _selectedBarline = null;
        if (fit) FitPage();
        InvalidateVisual();
    }

    /// <summary>Shows a page image without recognition results (while the page is being recognised).</summary>
    public void SetPreview(GrayImage image)
    {
        _page = null; _analysis = null;
        _bitmap = ToBitmap(image);
        _selected = null; _selectedStaff = null; _selectedBarline = null;
        FitPage();
        InvalidateVisual();
    }

    public void SetAnalysis(PageAnalysis? analysis)
    {
        _analysis = analysis;
        InvalidateVisual();
    }

    public void FitPage()
    {
        if (_bitmap == null || Bounds.Width <= 0 || Bounds.Height <= 0) { _zoom = 1; _offset = default; return; }
        double zx = Bounds.Width / _bitmap.PixelSize.Width, zy = Bounds.Height / _bitmap.PixelSize.Height;
        _zoom = Math.Min(zx, zy) * 0.98;
        _offset = new Point((Bounds.Width - _bitmap.PixelSize.Width * _zoom) / 2, (Bounds.Height - _bitmap.PixelSize.Height * _zoom) / 2);
        InvalidateVisual();
    }

    public void FitWidth()
    {
        if (_bitmap == null || Bounds.Width <= 0) return;
        _zoom = Bounds.Width / _bitmap.PixelSize.Width * 0.98;
        _offset = new Point((Bounds.Width - _bitmap.PixelSize.Width * _zoom) / 2, 4);
        InvalidateVisual();
    }

    public void SetZoom(double zoom)
    {
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        ZoomAround(centre, zoom / _zoom);
    }

    /// <summary>Zooms and scrolls so that the rectangle (image coordinates) fills the view.</summary>
    public void ZoomTo(PVOMR.Core.Model.Rect r)
    {
        if (_bitmap == null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        _zoom = Math.Min(Bounds.Width / Math.Max(1, r.Width), Bounds.Height / Math.Max(1, r.Height)) * 0.95;
        _offset = new Point(Bounds.Width / 2 - r.CenterX * _zoom, Bounds.Height / 2 - r.CenterY * _zoom);
        InvalidateVisual();
    }

    /// <summary>Scrolls so that the point (image coordinates) is in the middle of the view; the zoom stays.</summary>
    public void CenterOn(double x, double y)
    {
        if (_bitmap == null) return;
        _offset = new Point(Bounds.Width / 2 - x * _zoom, Bounds.Height / 2 - y * _zoom);
        InvalidateVisual();
    }

    /// <summary>Scrolls so that the rectangle (image coordinates) is visible, zooming in when it is tiny on screen.</summary>
    public void EnsureVisible(PVOMR.Core.Model.Rect r)
    {
        if (_bitmap == null) return;
        if (r.Width * _zoom < 40) ZoomAround(new Point(Bounds.Width / 2, Bounds.Height / 2), Math.Min(4, 60.0 / Math.Max(1, r.Width * _zoom)));
        var c = ToScreen(new Point(r.CenterX, r.CenterY));
        if (c.X < 0 || c.X > Bounds.Width || c.Y < 0 || c.Y > Bounds.Height)
            _offset += new Point(Bounds.Width / 2 - c.X, Bounds.Height / 2 - c.Y);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => availableSize;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_bitmap != null && e.PreviousSize.Width <= 0) FitPage();
    }

    private Point ToImage(Point screen) => new((screen.X - _offset.X) / _zoom, (screen.Y - _offset.Y) / _zoom);
    private Point ToScreen(Point image) => new(image.X * _zoom + _offset.X, image.Y * _zoom + _offset.Y);

    private void ZoomAround(Point screen, double factor)
    {
        double newZoom = Math.Clamp(_zoom * factor, 0.05, 16);
        factor = newZoom / _zoom;
        _offset = new Point(screen.X - (screen.X - _offset.X) * factor, screen.Y - (screen.Y - _offset.Y) * factor);
        _zoom = newZoom;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { _offset += new Point(e.Delta.Y * 60, 0); InvalidateVisual(); }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) { _offset += new Point(0, e.Delta.Y * 60); InvalidateVisual(); }
        else ZoomAround(e.GetPosition(this), e.Delta.Y > 0 ? 1.2 : 1 / 1.2);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        _pressPos = _lastDrag = e.GetPosition(this);
        _dragging = true; _moved = false; _moving = false;
        if (_mode == ViewMode.Move && _selected != null && _page != null)
        {
            var img = ToImage(_pressPos);
            var r = _selected.Bounds;
            int pad = (int)Math.Max(3, 4 / _zoom);
            if (img.X >= r.X0 - pad && img.X <= r.X1 + pad && img.Y >= r.Y0 - pad && img.Y <= r.Y1 + pad) { _moving = true; _accX = _accY = 0; _movedX = _movedY = 0; }
        }
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        var p = e.GetPosition(this);
        if (!_moved && (Math.Abs(p.X - _pressPos.X) > 4 || Math.Abs(p.Y - _pressPos.Y) > 4)) _moved = true;
        if (!_moved) return;
        if (_moving && _selected != null && _page != null)
        {
            _accX += (p.X - _lastDrag.X) / _zoom; _accY += (p.Y - _lastDrag.Y) / _zoom;
            int dx = (int)Math.Truncate(_accX), dy = (int)Math.Truncate(_accY);
            if (dx != 0 || dy != 0)
            {
                CorrectionStore.Shift(_page, _selected, dx, dy);
                _accX -= dx; _accY -= dy; _movedX += dx; _movedY += dy;
            }
        }
        else _offset += p - _lastDrag;
        _lastDrag = p;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        if (_moved)
        {
            if (_moving && _selected != null && (_movedX != 0 || _movedY != 0)) MoveFinished?.Invoke(_selected, _movedX, _movedY);
            _moving = false;
            return;
        }
        var img = ToImage(e.GetPosition(this));
        if (_mode is ViewMode.AddNote or ViewMode.AddRest or ViewMode.AddBarline) AddRequested?.Invoke(img, StaffAt(img.X, img.Y));
        else SelectAt(img);
    }

    private Staff? StaffAt(double x, double y) => _analysis?.Staves.FirstOrDefault(st => x >= st.Left - 2 * st.Space && x <= st.Right + 2 * st.Space && y >= st.TopY(x) - 3 * st.Space && y <= st.BottomY(x) + 3 * st.Space);

    private void SelectAt(Point img)
    {
        if (_analysis == null) return;
        int x = (int)Math.Round(img.X), y = (int)Math.Round(img.Y);
        // a bar line (when shown) under the pointer, within a few screen pixels of its ink
        if (ShowBarlines)
        {
            double tol = Math.Max(2, 4 / _zoom);
            foreach (var st in _analysis.Staves)
            {
                if (y < st.TopY(x) - 0.5 * st.Space || y > st.BottomY(x) + 0.5 * st.Space) continue;
                var bar = st.Barlines.Where(b => x >= b.X0 - tol && x <= b.X1 + tol).OrderBy(b => Math.Abs(b.X - x)).FirstOrDefault();
                if (bar == null) continue;
                _selected = null; _selectedStaff = st; _selectedBarline = bar;
                InvalidateVisual();
                BarlineSelected?.Invoke(bar, st);
                return;
            }
        }
        _selectedBarline = null;
        MusicSymbol? best = null; Staff? bestStaff = null; long bestArea = long.MaxValue;
        void Consider(MusicSymbol s, Staff? staff)
        {
            var r = s.Bounds;
            int pad = 2;
            if (x < r.X0 - pad || x > r.X1 + pad || y < r.Y0 - pad || y > r.Y1 + pad) return;
            long area = (long)r.Width * r.Height;
            if (area < bestArea) { bestArea = area; best = s; bestStaff = staff; }
        }
        foreach (var st in _analysis.Staves)
            foreach (var m in st.Measures)
                foreach (var s in m.Symbols)
                {
                    Consider(s, st);
                    foreach (var ch in s.Children) Consider(ch, st);
                }
        foreach (var s in _analysis.PageSymbols) Consider(s, null);
        // no symbol: select the staff under the pointer
        if (best == null) bestStaff = StaffAt(x, y);
        _selected = best; _selectedStaff = bestStaff;
        InvalidateVisual();
        SelectionChanged?.Invoke(best, bestStaff);
    }

    public void Select(MusicSymbol? symbol, Staff? staff, Barline? barline = null)
    {
        _selected = symbol; _selectedStaff = staff; _selectedBarline = barline;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(64, 64, 68)), new Avalonia.Rect(Bounds.Size));
        if (_bitmap == null) return;
        var matrix = Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_offset.X, _offset.Y);
        using (context.PushTransform(matrix))
        {
            var full = new Avalonia.Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height);
            // zoomed in: pixels as they are (nearest neighbour), so that the ink the recogniser saw can be judged;
            // zoomed out: smooth scaling
            var mode = _zoom >= 1 ? Avalonia.Media.Imaging.BitmapInterpolationMode.None : Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality;
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
                context.DrawImage(_bitmap, full, full);
            if (_analysis != null) DrawOverlay(context);
        }
    }

    private static readonly Dictionary<SymbolKind, Color> KindColors = new()
    {
        [SymbolKind.Note] = Color.FromRgb(0, 150, 0), [SymbolKind.Chord] = Color.FromRgb(0, 150, 0), [SymbolKind.NoteGroup] = Color.FromRgb(0, 110, 0),
        [SymbolKind.Rest] = Color.FromRgb(210, 120, 0), [SymbolKind.Clef] = Color.FromRgb(120, 0, 200),
        [SymbolKind.KeySignature] = Color.FromRgb(0, 150, 200), [SymbolKind.Accidental] = Color.FromRgb(0, 150, 200),
        [SymbolKind.TimeSignature] = Color.FromRgb(200, 0, 200), [SymbolKind.Dynamic] = Color.FromRgb(120, 120, 120),
        [SymbolKind.Text] = Color.FromRgb(140, 140, 140), [SymbolKind.Unknown] = Color.FromRgb(255, 0, 0),
    };

    public static Color ColorFor(SymbolKind k) => KindColors.TryGetValue(k, out var c) ? c : Color.FromRgb(150, 150, 0);

    private void DrawOverlay(DrawingContext context)
    {
        var a = _analysis!;
        double thin = 1.0 / _zoom;
        if (OnlySymbols != null)
        {
            var tf = new Typeface("Inter");
            foreach (var s in OnlySymbols)
            {
                float sp = (float)(s.Staff?.Space ?? a.StaffSpace);
                DrawSymbol(context, s, sp, tf, thin, labels: Labels == null);
                DrawSelection(context, s.Bounds, thin);
                if (Labels != null && Labels.TryGetValue(s, out var label)) DrawLabel(context, s.Bounds, label, sp);
            }
            if (Marker is PVOMR.Core.Model.Rect mk)
            {
                var pen = new Pen(new SolidColorBrush(Color.FromRgb(230, 0, 0)), 3 * thin, new DashStyle(new double[] { 3, 2 }, 0));
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 0, 0)), pen, new Avalonia.Rect(mk.X0, mk.Y0, mk.Width, mk.Height));
                OutlinedText(context, "missing here?", new Point(mk.CenterX, mk.Y0 - 4), a.StaffSpace * 0.75, true, Color.FromRgb(220, 0, 0), centre: true, above: true);
            }
            return;
        }
        if (ShowFits && Fits != null)
        {
            var longBrush = new SolidColorBrush(Color.FromArgb(55, 255, 0, 0));
            var shortBrush = new SolidColorBrush(Color.FromArgb(55, 255, 160, 0));
            var droppedBrush = new SolidColorBrush(Color.FromArgb(45, 160, 0, 200));
            foreach (var st in a.Staves)
                foreach (var m in st.Measures)
                {
                    if (!Fits.TryGetValue(m, out var fit) || (fit.Fits && fit.Dropped == 0)) continue;
                    var brush = fit.TooLong ? longBrush : fit.TooShort ? shortBrush : droppedBrush;
                    double top = st.TopY(m.X0) - 2.5 * st.Space, bottom = st.BottomY(m.X0) + 2.5 * st.Space;
                    context.FillRectangle(brush, new Avalonia.Rect(m.X0, top, Math.Max(1, m.X1 - m.X0), bottom - top));
                }
        }
        if (ShowStaffLines)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 0, 0)), thin);
            foreach (var st in a.Staves)
                foreach (var line in st.Lines)
                    for (int i = 1; i < line.Points.Count; i++)
                        context.DrawLine(pen, new Point(line.Points[i - 1].X, line.Points[i - 1].Y), new Point(line.Points[i].X, line.Points[i].Y));
        }
        if (ShowBarlines)
        {
            foreach (var st in a.Staves)
                foreach (var b in st.Barlines)
                {
                    var brush = new SolidColorBrush(b.Kind == BarlineKind.Single ? Color.FromArgb(120, 0, 0, 255) : Color.FromArgb(150, 255, 0, 255));
                    double yt = st.TopY(b.X), yb = st.BottomY(b.X);
                    context.FillRectangle(brush, new Avalonia.Rect(b.X0 - 1, yt, b.X1 - b.X0 + 3, yb - yt));
                    if (b == _selectedBarline)
                        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(255, 200, 0)), 2.5 / _zoom), new Avalonia.Rect(b.X0 - 4, yt - 0.6 * st.Space, b.X1 - b.X0 + 9, yb - yt + 1.2 * st.Space));
                }
        }
        var typeface = new Typeface("Inter");
        foreach (var st in a.Staves)
        {
            float ss = (float)st.Space;
            if (ShowLabels)
            {
                var measureBrush = new SolidColorBrush(Color.FromRgb(0, 120, 0));
                foreach (var m in st.Measures)
                {
                    var ft = new FormattedText($"{st.Index + 1}.{m.Index + 1}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, Math.Max(6, ss * 0.7), measureBrush);
                    context.DrawText(ft, new Point(m.X0 + 3, st.TopY(m.X0) - ss * 2.0));
                }
            }
            foreach (var m in st.Measures)
                foreach (var s in m.Symbols)
                {
                    DrawSymbol(context, s, ss, typeface, thin);
                    foreach (var ch in s.Children) if (ShowBoxes && ch == _selected) DrawSelection(context, ch.Bounds, thin);
                }
        }
        foreach (var s in a.PageSymbols) if (ShowBoxes) DrawSymbol(context, s, (float)a.StaffSpace, typeface, thin);
        if (_selected == null && _selectedStaff != null && _selectedBarline == null)
        {
            var st = _selectedStaff;
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 140, 0)), 2 * thin);
            context.DrawRectangle(null, pen, new Avalonia.Rect(st.Left - st.Space, st.TopY(st.Left) - st.Space, st.Right - st.Left + 2 * st.Space, st.BottomY(st.Left) - st.TopY(st.Left) + 2 * st.Space));
        }
    }

    /// <summary>The pitch above the symbol, its length below and the intervals from the note before and to the note after
    /// left and right, in text with a white outline (readable over ink).</summary>
    private static void DrawLabel(DrawingContext context, PVOMR.Core.Model.Rect r, SymbolLabel l, double ss)
    {
        double size = Math.Max(6, ss * 0.8), gap = 4 + ss * 0.15;
        var normal = Color.FromRgb(150, 0, 110); var strong = Color.FromRgb(220, 0, 0);
        if (l.Pitch.Length > 0) OutlinedText(context, l.Pitch, new Point(r.CenterX, r.Y0 - gap), size, l.PitchBold, l.PitchBold ? strong : normal, centre: true, above: true);
        if (l.Duration.Length > 0) OutlinedText(context, l.Duration, new Point(r.CenterX, r.Y1 + gap), size, l.DurationBold, l.DurationBold ? strong : normal, centre: true);
        double mid = r.CenterY - size * 0.65;
        if (l.Before != null) OutlinedText(context, l.Before, new Point(r.X0 - gap, mid), size * 0.9, false, Color.FromRgb(0, 90, 200), right: true);
        if (l.After != null) OutlinedText(context, l.After, new Point(r.X1 + gap, mid), size * 0.9, false, Color.FromRgb(0, 90, 200));
    }

    private static void OutlinedText(DrawingContext context, string text, Point at, double size, bool bold, Color color, bool centre = false, bool above = false, bool right = false)
    {
        var tf = new Typeface("Inter", FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal);
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, new SolidColorBrush(color));
        double x = centre ? at.X - ft.Width / 2 : right ? at.X - ft.Width : at.X;
        double y = above ? at.Y - ft.Height : at.Y;
        var geometry = ft.BuildGeometry(new Point(x, y));
        if (geometry == null) { context.DrawText(ft, new Point(x, y)); return; }
        context.DrawGeometry(null, new Pen(Brushes.White, size * 0.28, lineJoin: PenLineJoin.Round), geometry);
        context.DrawGeometry(new SolidColorBrush(color), null, geometry);
    }

    /// <summary>A symbol's length as written: "half dot" for a dotted half.</summary>
    public static string DurationLabel(MusicSymbol s)
    {
        int dots = s.Dots > 0 ? s.Dots : s.Notes.Select(n => n.Dots).DefaultIfEmpty(0).Max();
        return (s.Duration ?? "") + DotsText(dots);
    }

    /// <summary>" dot" for a dotted value, " double dot" for a double-dotted one.</summary>
    public static string DotsText(int dots) => dots switch { <= 0 => "", 1 => " dot", 2 => " double dot", _ => $" {dots} dots" };

    private void DrawSelection(DrawingContext context, PVOMR.Core.Model.Rect r, double thin)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 140, 0)), 3 * thin);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 140, 0)), pen, new Avalonia.Rect(r.X0 - 2, r.Y0 - 2, r.Width + 4, r.Height + 4));
    }

    /// <summary>Head colours by duration: grey whole, amber half, green quarter, cyan 8th, blue 16th, purple 32nd, pink 64th.</summary>
    public static Color DurationColor(string? duration) => (duration ?? "").TrimEnd('?') switch
    {
        "whole" or "breve" => Color.FromRgb(130, 130, 130),
        "half" => Color.FromRgb(230, 170, 0),
        "quarter" => Color.FromRgb(0, 180, 0),
        "8th" => Color.FromRgb(0, 170, 210),
        "16th" => Color.FromRgb(40, 70, 235),
        "32nd" => Color.FromRgb(170, 40, 210),
        "64th" => Color.FromRgb(235, 40, 130),
        _ => Color.FromRgb(0, 180, 0),
    };

    private static readonly Dictionary<string, IBrush> HeadBrushes = new();
    private static IBrush HeadBrush(string? duration)
    {
        string key = (duration ?? "").TrimEnd('?');
        if (!HeadBrushes.TryGetValue(key, out var b)) { var c = DurationColor(key); HeadBrushes[key] = b = new SolidColorBrush(Color.FromArgb(120, c.R, c.G, c.B)); }
        return b;
    }

    private void DrawSymbol(DrawingContext context, MusicSymbol s, float ss, Typeface typeface, double thin, bool labels = true)
    {
        bool showLabels = ShowLabels && labels;
        var r = s.Bounds;
        var color = ColorFor(s.Kind);
        if (ShowBoxes)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(200, color.R, color.G, color.B)), 1.5 * thin);
            context.DrawRectangle(null, pen, new Avalonia.Rect(r.X0, r.Y0, r.Width, r.Height));
        }
        if (s == _selected) DrawSelection(context, r, thin);
        var textBrush = new SolidColorBrush(Color.FromRgb(200, 0, 120));
        // a printed bar number the count disagrees with: a missed or extra bar line before it (or a misread number)
        if (s.Kind == SymbolKind.Number && s.Name == "barNumber" && s.Staff != null && BarNumberProblems != null && BarNumberProblems.TryGetValue(s.Staff.Index, out var problem))
        {
            var red = new SolidColorBrush(Color.FromRgb(230, 0, 0));
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(60, 255, 0, 0)), new Pen(red, 3 * thin), new Avalonia.Rect(r.X0 - 3, r.Y0 - 3, r.Width + 6, r.Height + 6));
            var pt = new FormattedText(problem, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, Math.Max(6, ss * 0.7), red);
            context.DrawText(pt, new Point(r.X1 + ss * 0.5, r.Y0));
            return;
        }
        if (s.Notes.Count > 0 || s.Children.Count > 0)
        {
            // heads coloured by duration, so that a wrong value stands out without clicking every note
            var notes = s.Children.Count > 0 ? s.Children.SelectMany(c => c.Notes.Select(n => (n, dur: c.Duration))) : s.Notes.Select(n => (n, dur: s.Duration));
            foreach (var (n, dur) in notes)
            {
                if (ShowBoxes) context.DrawEllipse(HeadBrush(dur), null, new Point(n.Head.CenterX + 0.5, n.Head.CenterY + 0.5), n.Head.Width / 2.0 + 0.5, n.Head.Height / 2.0 + 0.5);
                if (showLabels && n.Pitch != null)
                {
                    var ft = new FormattedText(n.Pitch, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, Math.Max(5, ss * 0.6), textBrush);
                    context.DrawText(ft, new Point(n.Head.X1 + 2, n.Head.Y0 - ss * 0.2));
                }
            }
            if (showLabels)
            {
                string label = s.Kind == SymbolKind.NoteGroup ? (s.Attributes.TryGetValue("tuplet", out var t) ? $"group tuplet {t.Split('@')[0]}" : "group") : DurationLabel(s);
                if (label.Length > 0)
                {
                    var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, Math.Max(5, ss * 0.6), textBrush);
                    context.DrawText(ft, new Point(r.X0, r.Y1 + ss * 0.2));
                }
            }
        }
        else if (showLabels)
        {
            string label = s.Kind switch
            {
                SymbolKind.Rest => $"rest {DurationLabel(s)}",
                SymbolKind.Unknown => "?",
                SymbolKind.Text => "txt",
                SymbolKind.Clef => s.Attributes.GetValueOrDefault("clef", "clef"),
                SymbolKind.KeySignature => "key",
                SymbolKind.TimeSignature => s.Attributes.GetValueOrDefault("time", "time"),
                SymbolKind.Number when s.Name == "barNumber" => $"bar {s.Attributes.GetValueOrDefault("bar", "?")}",
                _ => s.Name.Length > 14 ? s.Kind.ToString() : s.Name,
            };
            var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, Math.Max(5, ss * 0.7), textBrush);
            context.DrawText(ft, new Point(r.X0, r.Y0 - ss * 0.9));
        }
    }

    private static WriteableBitmap ToBitmap(GrayImage g)
    {
        var bmp = new WriteableBitmap(new PixelSize(g.Width, g.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bmp.Lock();
        var row = new byte[g.Width * 4];
        for (int y = 0; y < g.Height; y++)
        {
            int src = y * g.Width;
            for (int x = 0; x < g.Width; x++)
            {
                byte v = g.Data[src + x];
                int o = x * 4;
                row[o] = v; row[o + 1] = v; row[o + 2] = v; row[o + 3] = 255;
            }
            Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        }
        return bmp;
    }
}

/// <summary>Text shown around a symbol in the merge view; the differing part in bold.</summary>
public sealed record SymbolLabel(string Pitch, string Duration, bool PitchBold, bool DurationBold, string? Before, string? After);
