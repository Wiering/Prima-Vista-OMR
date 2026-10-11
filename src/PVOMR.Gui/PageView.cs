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
public enum ViewMode { Select, AddNote, AddRest, Move }

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

    public event Action<MusicSymbol?, Staff?>? SelectionChanged;
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
            Cursor = value switch { ViewMode.AddNote or ViewMode.AddRest => new Cursor(StandardCursorType.Cross), ViewMode.Move => new Cursor(StandardCursorType.SizeAll), _ => Cursor.Default };
        }
    }

    /// <summary>Duration check per measure (from the assembler); measures that do not fit are tinted.</summary>
    public Dictionary<Measure, PVOMR.Core.Score.MeasureFit>? Fits { get; set; }
    public bool ShowFits { get; set; } = true;

    public bool ShowStaffLines { get; set; } = true;
    public bool ShowBoxes { get; set; } = true;
    public bool ShowLabels { get; set; } = true;
    public bool ShowBarlines { get; set; } = true;

    public MusicSymbol? Selected { get => _selected; set { _selected = value; InvalidateVisual(); } }
    public Staff? SelectedStaff => _selectedStaff;
    public double Zoom => _zoom;

    public PageView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public void SetPage(PageResult? page, bool fit)
    {
        _page = page;
        _bitmap = page == null ? null : ToBitmap(page.Context.Gray);
        _analysis = page?.Analysis;
        _selected = null; _selectedStaff = null;
        if (fit) FitPage();
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
        if (_mode is ViewMode.AddNote or ViewMode.AddRest) AddRequested?.Invoke(img, StaffAt(img.X, img.Y));
        else SelectAt(img);
    }

    private Staff? StaffAt(double x, double y) => _analysis?.Staves.FirstOrDefault(st => x >= st.Left - 2 * st.Space && x <= st.Right + 2 * st.Space && y >= st.TopY(x) - 3 * st.Space && y <= st.BottomY(x) + 3 * st.Space);

    private void SelectAt(Point img)
    {
        if (_analysis == null) return;
        int x = (int)Math.Round(img.X), y = (int)Math.Round(img.Y);
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

    public void Select(MusicSymbol? symbol, Staff? staff)
    {
        _selected = symbol; _selectedStaff = staff;
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
        if (_selected == null && _selectedStaff != null)
        {
            var st = _selectedStaff;
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 140, 0)), 2 * thin);
            context.DrawRectangle(null, pen, new Avalonia.Rect(st.Left - st.Space, st.TopY(st.Left) - st.Space, st.Right - st.Left + 2 * st.Space, st.BottomY(st.Left) - st.TopY(st.Left) + 2 * st.Space));
        }
    }

    private void DrawSelection(DrawingContext context, PVOMR.Core.Model.Rect r, double thin)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 140, 0)), 3 * thin);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 140, 0)), pen, new Avalonia.Rect(r.X0 - 2, r.Y0 - 2, r.Width + 4, r.Height + 4));
    }

    private void DrawSymbol(DrawingContext context, MusicSymbol s, float ss, Typeface typeface, double thin)
    {
        var r = s.Bounds;
        var color = ColorFor(s.Kind);
        if (ShowBoxes)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(200, color.R, color.G, color.B)), 1.5 * thin);
            context.DrawRectangle(null, pen, new Avalonia.Rect(r.X0, r.Y0, r.Width, r.Height));
        }
        if (s == _selected) DrawSelection(context, r, thin);
        var textBrush = new SolidColorBrush(Color.FromRgb(200, 0, 120));
        if (s.Notes.Count > 0 || s.Children.Count > 0)
        {
            var headBrush = new SolidColorBrush(Color.FromArgb(90, 0, 200, 0));
            var notes = s.Notes.Concat(s.Children.SelectMany(c => c.Notes));
            foreach (var n in notes)
            {
                if (ShowBoxes) context.DrawEllipse(headBrush, null, new Point(n.Head.CenterX + 0.5, n.Head.CenterY + 0.5), n.Head.Width / 2.0 + 0.5, n.Head.Height / 2.0 + 0.5);
                if (ShowLabels && n.Pitch != null)
                {
                    var ft = new FormattedText(n.Pitch, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, Math.Max(5, ss * 0.6), textBrush);
                    context.DrawText(ft, new Point(n.Head.X1 + 2, n.Head.Y0 - ss * 0.2));
                }
            }
            if (ShowLabels)
            {
                string label = s.Kind == SymbolKind.NoteGroup ? (s.Attributes.TryGetValue("tuplet", out var t) ? $"group tuplet {t.Split('@')[0]}" : "group") : s.Duration ?? "";
                if (label.Length > 0)
                {
                    var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, Math.Max(5, ss * 0.6), textBrush);
                    context.DrawText(ft, new Point(r.X0, r.Y1 + ss * 0.2));
                }
            }
        }
        else if (ShowLabels)
        {
            string label = s.Kind switch
            {
                SymbolKind.Rest => $"rest {s.Duration}",
                SymbolKind.Unknown => "?",
                SymbolKind.Text => "txt",
                SymbolKind.Clef => s.Attributes.GetValueOrDefault("clef", "clef"),
                SymbolKind.KeySignature => "key",
                SymbolKind.TimeSignature => s.Attributes.GetValueOrDefault("time", "time"),
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
