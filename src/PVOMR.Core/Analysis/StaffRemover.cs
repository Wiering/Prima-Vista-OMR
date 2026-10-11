using PVOMR.Core.Imaging;
using PVOMR.Core.Model;

namespace PVOMR.Core.Analysis;

public static class StaffRemover
{
    /// <summary>
    /// Returns a copy of the binary image with staff lines erased. At each column of each line, the vertical ink run
    /// through the line is erased when it is not much thicker than the line itself (i.e. no symbol crosses here).
    /// </summary>
    public static BinaryImage Remove(BinaryImage bin, IEnumerable<Staff> staves)
    {
        var outp = bin.Clone();
        foreach (var staff in staves)
        {
            // a run up to twice the line thickness is the line's own wobble, unless the ink beyond the line band is a wide
            // body (a half rest sitting on the line, a whole rest hanging from it): wide bodies survive
            int maxRun = (int)Math.Max(2, Math.Round(staff.LineThickness * 2.0 + 1));
            int sureRun = (int)Math.Max(2, Math.Round(staff.LineThickness * 1.5 + 1));
            int wide = (int)Math.Round(0.7 * staff.Space);
            int tol = (int)Math.Max(1, Math.Round(staff.LineThickness)) + 1;
            int x0 = (int)Math.Max(0, Math.Floor(staff.Left) - 2);
            int x1 = (int)Math.Min(bin.Width - 1, Math.Ceiling(staff.Right) + 2);
            foreach (var line in staff.Lines)
            {
                // pass 1: the vertical ink run through the line band at every column
                int n = x1 - x0 + 1;
                var runTop = new int[n]; var runLen = new int[n];
                for (int x = x0; x <= x1; x++)
                {
                    int yc = (int)Math.Round(line.YAt(x));
                    int bestLen = 0, bestTop = 0;
                    for (int dy = -tol; dy <= tol; dy++)
                    {
                        int y = yc + dy;
                        if (!bin[x, y]) continue;
                        int len = bin.VerticalRunAt(x, y, out int top);
                        if (len > bestLen) { bestLen = len; bestTop = top; }
                        dy = top + len - yc;
                    }
                    runTop[x - x0] = bestTop; runLen[x - x0] = bestLen;
                }
                // pass 2: erase runs that are the line's own thickness or wobble; keep runs up to twice the thickness when
                // their neighbours are thick as well (a beam or a rest lying along the line), and anything thicker
                for (int x = x0; x <= x1; x++)
                {
                    int i = x - x0, len = runLen[i];
                    if (len == 0 || len > maxRun) continue;
                    bool erase = len <= sureRun;
                    if (!erase)
                    {
                        int thick = 1;
                        for (int j = i - 1; j >= 0 && runLen[j] > sureRun; j--) thick++;
                        for (int j = i + 1; j < n && runLen[j] > sureRun; j++) thick++;
                        erase = thick < wide;
                    }
                    if (erase)
                        for (int yy = runTop[i]; yy < runTop[i] + len; yy++) outp[x, yy] = false;
                }
            }
        }
        return outp;
    }
}
