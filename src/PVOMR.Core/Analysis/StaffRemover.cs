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
            int maxRun = (int)Math.Max(2, Math.Round(staff.LineThickness * 2.0 + 1));
            int tol = (int)Math.Max(1, Math.Round(staff.LineThickness)) + 1;
            int x0 = (int)Math.Max(0, Math.Floor(staff.Left) - 2);
            int x1 = (int)Math.Min(bin.Width - 1, Math.Ceiling(staff.Right) + 2);
            foreach (var line in staff.Lines)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int yc = (int)Math.Round(line.YAt(x));
                    // collect runs touching [yc-tol, yc+tol]
                    for (int dy = -tol; dy <= tol; dy++)
                    {
                        int y = yc + dy;
                        if (!bin[x, y]) continue;
                        int len = bin.VerticalRunAt(x, y, out int top);
                        if (len <= maxRun)
                            for (int yy = top; yy < top + len; yy++) outp[x, yy] = false;
                        dy = top + len - yc; // skip past this run
                    }
                }
            }
        }
        return outp;
    }
}
