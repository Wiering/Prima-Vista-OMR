using System.Globalization;
using System.Text.RegularExpressions;

namespace PVOMR.Core.Eval;

/// <summary>An element of a MuseScore SVG export: class name and bounding box in page pixels.</summary>
public sealed class SvgElement
{
    public string Class = "";
    public double X0, Y0, X1, Y1;
    public int SubPaths;
    public double Width => X1 - X0;
    public double Height => Y1 - Y0;
    public double CenterX => (X0 + X1) / 2;
    public double CenterY => (Y0 + Y1) / 2;
    public override string ToString() => $"{Class} ({X0:0},{Y0:0})-({X1:0},{Y1:0})";
}

public sealed class SvgStaff
{
    public double[] LineY = new double[5];
    public double Left, Right;
    public double Space => (LineY[4] - LineY[0]) / 4;
    public double Top => LineY[0];
    public double Bottom => LineY[4];
    public List<double> Barlines { get; } = new();
}

/// <summary>Parses MuseScore SVG exports (one file per page) into element boxes, staves and barline positions.</summary>
public sealed class SvgTruth
{
    public List<SvgElement> Elements { get; } = new();
    public List<SvgStaff> Staves { get; } = new();
    public double Scale { get; private set; } = 0.25;   // page px per SVG unit

    private static readonly Regex ElementRx = new("<(path|polyline|polygon)\\s+class=\"([A-Za-z]*)\"[^>]*?(?:\\sd=\"([^\"]*)\"|\\spoints=\"([^\"]*)\")[^>]*/>", RegexOptions.Compiled);
    private static readonly Regex NumRx = new("-?\\d+(?:\\.\\d+)?", RegexOptions.Compiled);
    private static readonly Regex ViewBoxRx = new("viewBox=\"0 0 (\\d+(?:\\.\\d+)?) (\\d+(?:\\.\\d+)?)\"", RegexOptions.Compiled);
    private static readonly Regex WidthRx = new("<svg[^>]*\\swidth=\"([\\d.]+)mm\"", RegexOptions.Compiled);

    public static SvgTruth Load(string path, double dpi)
    {
        string text = File.ReadAllText(path);
        var t = new SvgTruth();
        var vb = ViewBoxRx.Match(text); var wm = WidthRx.Match(text);
        if (vb.Success && wm.Success)
        {
            double units = double.Parse(vb.Groups[1].Value, CultureInfo.InvariantCulture);
            double mm = double.Parse(wm.Groups[1].Value, CultureInfo.InvariantCulture);
            t.Scale = mm / 25.4 * dpi / units;
        }
        var lines = new List<(double y, double x0, double x1)>();
        foreach (Match m in ElementRx.Matches(text))
        {
            string cls = m.Groups[2].Value;
            string data = m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
            var nums = NumRx.Matches(data).Select(n => double.Parse(n.Value, CultureInfo.InvariantCulture)).ToList();
            if (nums.Count < 4) continue;
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            for (int i = 0; i + 1 < nums.Count; i += 2)
            {
                x0 = Math.Min(x0, nums[i]); x1 = Math.Max(x1, nums[i]); y0 = Math.Min(y0, nums[i + 1]); y1 = Math.Max(y1, nums[i + 1]);
            }
            var e = new SvgElement { Class = cls, X0 = x0 * t.Scale, Y0 = y0 * t.Scale, X1 = x1 * t.Scale, Y1 = y1 * t.Scale, SubPaths = data.Count(c => c == 'M') };
            if (cls == "StaffLines") { lines.Add((e.CenterY, e.X0, e.X1)); continue; }
            if (cls == "") continue;
            t.Elements.Add(e);
        }
        // staves: groups of five lines with equal spacing and equal extent
        lines.Sort((a, b) => a.y.CompareTo(b.y));
        for (int i = 0; i + 4 < lines.Count; i++)
        {
            double d = lines[i + 1].y - lines[i].y;
            bool ok = d > 2;
            for (int k = 1; k < 4 && ok; k++) ok = Math.Abs(lines[i + k + 1].y - lines[i + k].y - d) < 0.2 * d && Math.Abs(lines[i + k].x0 - lines[i].x0) < 2;
            if (!ok) continue;
            var st = new SvgStaff { Left = lines[i].x0, Right = lines[i].x1 };
            for (int k = 0; k < 5; k++) st.LineY[k] = lines[i + k].y;
            t.Staves.Add(st);
            i += 4;
        }
        foreach (var b in t.Elements.Where(e => e.Class == "BarLine"))
            foreach (var st in t.Staves)
                if (b.Y0 <= st.Bottom + st.Space && b.Y1 >= st.Top - st.Space && b.CenterX >= st.Left - st.Space && b.CenterX <= st.Right + st.Space)
                    st.Barlines.Add(b.CenterX);
        foreach (var st in t.Staves) { st.Barlines.Sort(); }
        return t;
    }

    public SvgStaff? StaffAt(double x, double y)
    {
        SvgStaff? best = null; double bd = double.MaxValue;
        foreach (var st in Staves)
        {
            if (x < st.Left - 3 * st.Space || x > st.Right + 3 * st.Space) continue;
            double d = y < st.Top ? st.Top - y : y > st.Bottom ? y - st.Bottom : 0;
            if (d < bd) { bd = d; best = st; }
        }
        return best;
    }
}
