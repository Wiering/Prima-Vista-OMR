namespace PVOMR.Core.Model;

public readonly record struct Rect(int X0, int Y0, int X1, int Y1)
{
    public int Width => X1 - X0 + 1;
    public int Height => Y1 - Y0 + 1;
    public double CenterX => (X0 + X1) / 2.0;
    public double CenterY => (Y0 + Y1) / 2.0;
    public bool Contains(int x, int y) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1;
    public Rect Union(Rect o) => new(Math.Min(X0, o.X0), Math.Min(Y0, o.Y0), Math.Max(X1, o.X1), Math.Max(Y1, o.Y1));
    public override string ToString() => $"({X0},{Y0})-({X1},{Y1})";
}

/// <summary>A (possibly curved) staff line stored as a polyline y(x).</summary>
public sealed class StaffLine
{
    public List<(double X, double Y)> Points { get; } = new();
    public double Thickness { get; set; }

    public double YAt(double x)
    {
        var p = Points;
        if (p.Count == 0) return 0;
        if (p.Count == 1 || x <= p[0].X) return p[0].Y + (p.Count > 1 && x < p[0].X ? Slope(0) * (x - p[0].X) : 0);
        if (x >= p[^1].X) return p[^1].Y + Slope(p.Count - 2) * (x - p[^1].X);
        int lo = 0, hi = p.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (p[mid].X <= x) lo = mid; else hi = mid;
        }
        double t = (x - p[lo].X) / (p[hi].X - p[lo].X);
        return p[lo].Y + t * (p[hi].Y - p[lo].Y);
    }

    private double Slope(int i)
    {
        var p = Points;
        if (p.Count < 2) return 0;
        double dx = p[i + 1].X - p[i].X;
        return dx == 0 ? 0 : (p[i + 1].Y - p[i].Y) / dx;
    }
}

public enum ClefKind { Unknown, Treble, Bass, Alto, Tenor, Percussion, Treble8vb, Bass8vb }

public sealed class Staff
{
    public int Index { get; set; }
    public List<StaffLine> Lines { get; } = new();
    public double Left { get; set; }
    public double Right { get; set; }
    /// <summary>Distance between adjacent staff lines (pixels).</summary>
    public double Space { get; set; }
    public double LineThickness { get; set; }
    public int SystemIndex { get; set; } = -1;
    public List<Barline> Barlines { get; } = new();
    public List<Measure> Measures { get; } = new();
    public ClefKind Clef { get; set; } = ClefKind.Unknown;
    public int KeyFifths { get; set; }
    public string? TimeSignature { get; set; }
    /// <summary>User corrections: when set they override what the interpreter reads from the symbols.</summary>
    /// <summary>The instrument name printed left of the staff (OCR; null when none was read).</summary>
    public string? InstrumentName { get; set; }
    /// <summary>A curly brace joins this staff and the next one of the system: the two are one keyboard (or harp) part.</summary>
    public bool BraceToNext { get; set; }
    /// <summary>The bar number printed above the start of the staff (OCR; null when none was read).</summary>
    public int? BarNumber { get; set; }
    public ClefKind? ForcedClef { get; set; }
    public int? ForcedKey { get; set; }
    public string? ForcedTime { get; set; }

    public int LineCount => Lines.Count;
    public double TopY(double x) => Lines[0].YAt(x);
    public double BottomY(double x) => Lines[^1].YAt(x);
    public double Height => Space * (Lines.Count - 1);
    public double CenterY => (TopY((Left + Right) / 2) + BottomY((Left + Right) / 2)) / 2;

    /// <summary>Staff step at y: 0 = bottom line, +1 = first space, +2 = second line ... (half staff spaces). Negative below the staff.</summary>
    public double StepAt(double x, double y) => (BottomY(x) - y) / (Space / 2.0);
}

public enum BarlineKind { Single, Double, Final, RepeatStart, RepeatEnd, RepeatBoth, Thick, SystemStart }

public sealed class Barline
{
    public double X { get; set; }
    public int X0 { get; set; }
    public int X1 { get; set; }
    public BarlineKind Kind { get; set; } = BarlineKind.Single;
    public int StaffFrom { get; set; }
    public int StaffTo { get; set; }
    public bool Spanning => StaffTo > StaffFrom;
    /// <summary>The ink of this barline continues up into the staff above (bottom staff of a system).</summary>
    public bool ConnectedUp { get; set; }
    public override string ToString() => $"{Kind}@x={X:0}";
}

public sealed class Measure
{
    public int Index { get; set; }
    public double X0 { get; set; }
    public double X1 { get; set; }
    public Staff Staff { get; set; } = null!;
    public Barline? LeftBarline { get; set; }
    public Barline? RightBarline { get; set; }
    public List<MusicSymbol> Symbols { get; } = new();
    /// <summary>A multi-measure rest: the number of measures this one stands for (0 = an ordinary measure).</summary>
    public int MultiRest { get; set; }
}

public sealed class StaffSystem
{
    public int Index { get; set; }
    public List<Staff> Staves { get; } = new();
    public List<double> MeasureBoundaries { get; } = new();
    public Rect Bounds { get; set; }
}

public enum SymbolKind
{
    Unknown, Clef, Accidental, KeySignature, TimeSignature, Note, Chord, NoteGroup, Rest, Dot, Articulation,
    Dynamic, Fermata, Ornament, Slur, Hairpin, Text, Number, Tuplet, Barline, Brace, Bracket, Ledger, Beam, Flag, Noise, Lyric, Pedal, Breath, Other
}

public sealed class Note
{
    public Rect Head { get; set; }
    public double HeadX => Head.CenterX;
    public double HeadY => Head.CenterY;
    public string HeadType { get; set; } = "black"; // black, half, whole, x, diamond
    public int Step { get; set; }                   // staff step (0 = bottom line)
    public string? Pitch { get; set; }
    public int Dots { get; set; }
    public int Accidental { get; set; } = int.MinValue;
    public string? AccidentalName { get; set; }
    public int LedgerLines { get; set; }
    /// <summary>Head detector confidence (1 when not available).</summary>
    public double Confidence { get; set; } = 1;
    /// <summary>The staff this head actually lies in when it is not the stem's staff (cross-staff beaming); Step is relative to it.</summary>
    public Staff? CrossStaff { get; set; }
    public override string ToString() => $"{Pitch ?? "?"} step={Step}";
}

public sealed class MusicSymbol
{
    public SymbolKind Kind { get; set; }
    public string Name { get; set; } = "";       // e.g. gClef, accidentalSharp, noteheadBlack, "note", "chord"
    public Rect Bounds { get; set; }
    public double Confidence { get; set; }
    public Staff? Staff { get; set; }
    public int? Step { get; set; }
    public string? Pitch { get; set; }
    public string? Duration { get; set; }        // whole, half, quarter, 8th, 16th ...
    public int Dots { get; set; }
    public bool? StemUp { get; set; }
    public int Flags { get; set; }               // flags/beams count
    public List<Note> Notes { get; } = new();    // noteheads of a note/chord/group
    public List<MusicSymbol> Children { get; } = new();
    public Dictionary<string, string> Attributes { get; } = new();
    public string? Description { get; set; }

    public override string ToString() => $"{Kind}:{Name} {Bounds}";
}

public sealed class PageAnalysis
{
    public string Source { get; set; } = "";
    public int PageNumber { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Dpi { get; set; }
    public double SkewDegrees { get; set; }
    public bool Rotated90 { get; set; }
    public string Binarization { get; set; } = "";
    public double StaffSpace { get; set; }
    public double LineThickness { get; set; }
    public List<Staff> Staves { get; } = new();
    public List<StaffSystem> Systems { get; } = new();
    public List<MusicSymbol> PageSymbols { get; } = new();  // symbols not attached to a staff (titles, text ...)
    /// <summary>Lines of text above the first system (OCR): title, composer, the instrument of a part.</summary>
    public List<string> HeaderText { get; } = new();
    public List<string> Notes { get; } = new();
    public TimeSpan Elapsed { get; set; }
}
