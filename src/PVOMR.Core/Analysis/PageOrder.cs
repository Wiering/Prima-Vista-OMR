namespace PVOMR.Core.Analysis;

/// <summary>
/// Which page a page continues from (its clef, key and time carry over). Piano four-hands editions often print the two
/// players on facing pages, Secondo left and Primo right, both covering the same bars: there a page continues from the
/// page two before it. For now such scores are recognised by the file name: "4h", "Pf4h", "4 hands", "piano_4_hands",
/// "four hands".
/// </summary>
public static class PageOrder
{
    public static bool FacingPages(string file) =>
        System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(file), @"(?<![0-9])4[\s_-]*h(ands?)?(?![A-Za-z0-9])|four[\s_-]*hands", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static int Previous(string file, int page) => page - (FacingPages(file) ? 2 : 1);
}
