using PVOMR.Core.Model;

namespace PVOMR.Core.Analysis;

/// <summary>Assigns clefs, key signatures, time signatures and pitch names on top of the detected symbols.</summary>
public static class MusicInterpreter
{
    private static readonly string[] Letters = { "C", "D", "E", "F", "G", "A", "B" };
    private static readonly string[] SharpOrder = { "F", "C", "G", "D", "A", "E", "B" };
    private static readonly string[] FlatOrder = { "B", "E", "A", "D", "G", "C", "F" };

    public static void Interpret(PageAnalysis page)
    {
        // Carry the clef/key from one system to the next for the same staff position (parts) is a guess; we only carry within a staff.
        foreach (var sys in page.Systems)
        {
            for (int k = 0; k < sys.Staves.Count; k++)
            {
                var staff = sys.Staves[k];
                // inherit from the staff at the same position in the previous system when nothing is found
                Staff? prev = sys.Index > 0 && page.Systems[sys.Index - 1].Staves.Count == sys.Staves.Count ? page.Systems[sys.Index - 1].Staves[k] : null;
                InterpretStaff(staff, prev, null);
            }
            // staves of one system share the key signature: a staff whose key signature was not recognised adopts the
            // key read on the other staves (the majority of those that have one), then its pitches are recomputed
            var keyed = sys.Staves.Where(st => st.Measures.Any(m => m.Symbols.Any(sy => sy.Kind == SymbolKind.KeySignature))).Select(st => st.KeyFifths).ToList();
            if (keyed.Count > 0)
            {
                int key = keyed.GroupBy(f => f).OrderByDescending(g => g.Count()).First().Key;
                for (int k = 0; k < sys.Staves.Count; k++)
                {
                    var staff = sys.Staves[k];
                    if (staff.Measures.Any(m => m.Symbols.Any(sy => sy.Kind == SymbolKind.KeySignature)) || staff.KeyFifths == key || staff.ForcedKey != null) continue;
                    Staff? prev = sys.Index > 0 && page.Systems[sys.Index - 1].Staves.Count == sys.Staves.Count ? page.Systems[sys.Index - 1].Staves[k] : null;
                    InterpretStaff(staff, prev, key);
                }
            }
            // staves of one system share the time signature: adopt the majority reading for staves without one
            var times = sys.Staves.Where(st => st.TimeSignature != null && !st.TimeSignature.Contains('?')).Select(st => st.TimeSignature!).ToList();
            if (times.Count > 0 && sys.Staves.Count > 1 && times.Count * 4 >= sys.Staves.Count)
            {
                // all staves of a system carry the same time signature: the majority reading overrules misread digits
                string time = times.GroupBy(t => t).OrderByDescending(g => g.Count()).First().Key;
                foreach (var st in sys.Staves) if (st.ForcedTime == null) st.TimeSignature = time;
            }
        }
        CompleteKeysAcrossSystems(page);
    }

    private static int? SystemMajorityKey(StaffSystem sys)
    {
        var keyed = sys.Staves.Where(HasKeySignature).Select(st => st.KeyFifths).ToList();
        return keyed.Count == 0 ? null : keyed.GroupBy(f => f).OrderByDescending(g => g.Count()).First().Key;
    }

    private static bool HasKeySignature(Staff st) => st.Measures.Any(m => m.Symbols.Any(sy => sy.Kind == SymbolKind.KeySignature));

    /// <summary>
    /// The same staff position holds the same instrument on every system of a page (when the staff counts agree). A key
    /// signature read with fewer accidentals of the same kind than on another system lost some to fragments: it is
    /// completed to the fuller reading (the majority, larger signature on a tie) and the pitches are recomputed.
    /// </summary>
    private static void CompleteKeysAcrossSystems(PageAnalysis page)
    {
        if (page.Systems.Count < 2) return;
        int count = page.Systems[0].Staves.Count;
        var systems = page.Systems.Where(s => s.Staves.Count == count).ToList();
        if (systems.Count < 2) return;
        for (int k = 0; k < count; k++)
        {
            var readings = systems.Select(s => s.Staves[k]).Where(HasKeySignature).Select(st => st.KeyFifths).ToList();
            if (readings.Count < 2) continue;
            // ties (two systems) go to the reading that agrees with its own system's majority, then to the larger signature
            int target = readings.GroupBy(f => f)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => systems.Count(s => HasKeySignature(s.Staves[k]) && s.Staves[k].KeyFifths == g.Key && SystemMajorityKey(s) == g.Key))
                .ThenByDescending(g => Math.Abs(g.Key)).First().Key;
            foreach (var sys in systems)
            {
                var staff = sys.Staves[k];
                int own = staff.KeyFifths;
                if (own == target || staff.ForcedKey != null) continue;
                bool fix = HasKeySignature(staff) ? Math.Sign(own) == Math.Sign(target) && Math.Abs(own) < Math.Abs(target)
                                                  : sys.Index > 0;   // no signature of its own: inherited an incomplete reading
                if (!fix) continue;
                Staff? prev = sys.Index > 0 && page.Systems[sys.Index - 1].Staves.Count == count ? page.Systems[sys.Index - 1].Staves[k] : null;
                InterpretStaff(staff, prev, target, forcedOverrides: true);
                page.Notes.Add($"system {sys.Index + 1} staff {k + 1}: key {own} completed to {target} from the other systems");
            }
        }
    }

    private static void InterpretStaff(Staff staff, Staff? prev, int? forcedKey, bool forcedOverrides = false)
    {
        var clef = staff.ForcedClef ?? prev?.Clef ?? ClefKind.Unknown;
        int fifths = staff.ForcedKey ?? forcedKey ?? prev?.KeyFifths ?? 0;
        string? time = staff.ForcedTime ?? prev?.TimeSignature;
        bool keyFromThisStaff = false;
        var measureAccidentals = new Dictionary<int, int>();

        for (int mi = 0; mi < staff.Measures.Count; mi++)
        {
            var m = staff.Measures[mi];
            measureAccidentals.Clear();
            var ordered = m.Symbols.OrderBy(s => s.Bounds.X0).ToList();
            // 1. clef
            bool seenNote = false;
            var leadingAccidentals = new List<MusicSymbol>();
            var timeDigits = new List<MusicSymbol>();
            foreach (var s in ordered)
            {
                if (s.Kind == SymbolKind.Clef)
                {
                    var newClef = ClefFrom(s);
                    if (newClef != ClefKind.Unknown) clef = newClef;
                    if (staff.ForcedClef != null) clef = staff.ForcedClef.Value;
                    s.Attributes["clef"] = clef.ToString();
                    if (!seenNote) { leadingAccidentals.Clear(); }
                    continue;
                }
                if (s.Kind is SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup or SymbolKind.Rest) seenNote = true;
                if (s.Kind is SymbolKind.Accidental or SymbolKind.KeySignature && !seenNote && (mi == 0 || ordered.Any(c => c.Kind == SymbolKind.Clef && c.Bounds.X0 < s.Bounds.X0) || leadingAccidentals.Count > 0)) leadingAccidentals.Add(s);
                if (s.Kind == SymbolKind.TimeSignature && !seenNote)
                {
                    double cy = s.Bounds.CenterY, cx = s.Bounds.CenterX;
                    if (cy > staff.TopY(cx) - 0.3 * staff.Space && cy < staff.BottomY(cx) + 0.3 * staff.Space) timeDigits.Add(s);
                    else s.Attributes["time"] = "outside staff (ignored)";
                }
            }
            // 2. key signature: a run of >= 1 accidentals of one type before the first note (first measure of the system, or after a clef)
            // the key signature is the leading run of accidentals of one kind (sharps or flats); a differing one ends the run
            // accidentals found by the key-signature zone pass are the key signature even when a slur or an early note sorts between them
            var zoneAcc = ordered.Where(s => s.Kind is SymbolKind.Accidental or SymbolKind.KeySignature && s.Attributes.ContainsKey("keyzone")).ToList();
            if (zoneAcc.Count > 0) leadingAccidentals = zoneAcc;
            if (leadingAccidentals.Count > 0 && leadingAccidentals[0].Name is "accidentalSharp" or "accidentalFlat")
            {
                int runLen = 1;
                while (runLen < leadingAccidentals.Count && leadingAccidentals[runLen].Name == leadingAccidentals[0].Name) runLen++;
                leadingAccidentals = leadingAccidentals.Take(runLen).ToList();
                bool plausible = true;
                var steps = leadingAccidentals.Select(a => a.Step ?? int.MinValue).ToList();
                if (steps.Any(s => s == int.MinValue)) plausible = false;
                if (plausible && mi == 0 || plausible && ordered.Any(c => c.Kind == SymbolKind.Clef))
                {
                    int n = leadingAccidentals.Count;
                    if (!(forcedOverrides && forcedKey != null) && staff.ForcedKey == null)
                        fifths = leadingAccidentals[0].Name == "accidentalSharp" ? n : leadingAccidentals[0].Name == "accidentalFlat" ? -n : 0;
                    keyFromThisStaff = true;
                    foreach (var a in leadingAccidentals) { a.Kind = SymbolKind.KeySignature; a.Attributes["key"] = KeyName(fifths); }
                }
            }
            // 3. time signature
            if (timeDigits.Count > 0)
            {
                var common = timeDigits.FirstOrDefault(t => t.Name is "timeSigCommon" or "timeSigCutCommon");
                var stack = timeDigits.FirstOrDefault(t => t.Attributes.ContainsKey("stack"));
                if (stack != null) time = stack.Attributes["stack"];
                else if (common != null) time = common.Name == "timeSigCommon" ? "C (4/4)" : "cut C (2/2)";
                else
                {
                    // stack digits: upper (numerator) and lower (denominator) by vertical position; several columns for multi-digit numbers
                    double mid = staff.CenterY;
                    var upper = timeDigits.Where(t => t.Bounds.CenterY < mid).OrderBy(t => t.Bounds.X0).Select(Digit).ToList();
                    var lower = timeDigits.Where(t => t.Bounds.CenterY >= mid).OrderBy(t => t.Bounds.X0).Select(Digit).ToList();
                    if (upper.Count > 2) upper = upper.Take(2).ToList();
                    if (lower.Count > 2) lower = lower.Take(2).ToList();
                    if (upper.Count > 0 && lower.Count > 0) time = string.Concat(upper) + "/" + string.Concat(lower);
                    else if (upper.Count + lower.Count > 0) time = string.Concat(upper.Concat(lower)) + "/?";
                }
                // a partial reading (digits missing or unidentified) is no time signature: the system's other staves may supply one
                if (time != null && time.Contains('?')) time = null;
                if (staff.ForcedTime != null) time = staff.ForcedTime;
                foreach (var t in timeDigits) t.Attributes["time"] = time ?? "?";
            }
            // 4. pitches
            foreach (var s in ordered)
            {
                if (s.Kind is not (SymbolKind.Note or SymbolKind.Chord or SymbolKind.NoteGroup)) continue;
                foreach (var n in s.Notes)
                {
                    n.Pitch = PitchName(clef, fifths, n, measureAccidentals);
                    n.LedgerLines = n.Step < 0 ? (-n.Step) / 2 : n.Step > 8 ? (n.Step - 8) / 2 : 0;
                }
                foreach (var ch in s.Children) ch.Pitch = string.Join(" ", ch.Notes.Select(n => n.Pitch));
                s.Pitch = s.Kind == SymbolKind.NoteGroup ? null : string.Join(" ", s.Notes.Select(n => n.Pitch));
            }
        }
        staff.Clef = clef;
        staff.KeyFifths = fifths;
        staff.TimeSignature = time;
        if (!keyFromThisStaff && forcedKey == null && prev != null) staff.KeyFifths = prev.KeyFifths;
    }

    private static string Digit(MusicSymbol s) => s.Name.StartsWith("timeSig") && s.Name.Length == 8 && char.IsDigit(s.Name[7]) ? s.Name[7].ToString() : "?";

    private static ClefKind ClefFrom(MusicSymbol s)
    {
        if (s.Name == "gClef") return ClefKind.Treble;
        if (s.Name == "fClef") return ClefKind.Bass;
        if (s.Name == "cClef") return s.Attributes.TryGetValue("clef", out var v) && v == "tenor" ? ClefKind.Tenor : ClefKind.Alto;
        if (s.Name == "unpitchedPercussionClef1") return ClefKind.Percussion;
        return ClefKind.Unknown;
    }

    public static string KeyName(int fifths)
    {
        string[] majors = { "Cb", "Gb", "Db", "Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "C#" };
        int i = fifths + 7;
        return i >= 0 && i < majors.Length ? $"{majors[i]} major ({Math.Abs(fifths)}{(fifths < 0 ? "b" : "#")})" : "?";
    }

    /// <summary>Diatonic index (0 = C0) of staff step 0 (the bottom line) for a clef.</summary>
    private static int BottomLineIndex(ClefKind clef) => clef switch
    {
        ClefKind.Treble => 4 * 7 + 2,      // E4
        ClefKind.Treble8vb => 3 * 7 + 2,   // E3
        ClefKind.Bass => 2 * 7 + 4,        // G2
        ClefKind.Bass8vb => 1 * 7 + 4,     // G1
        ClefKind.Alto => 3 * 7 + 3,        // F3
        ClefKind.Tenor => 3 * 7 + 1,       // D3
        _ => -1,
    };

    private static string PitchName(ClefKind clef, int fifths, Note n, Dictionary<int, int> measureAccidentals)
    {
        int baseIdx = BottomLineIndex(clef);
        if (baseIdx < 0) return $"step{n.Step}";
        int idx = baseIdx + n.Step;
        if (idx < 0 || idx > 7 * 10) return $"step{n.Step}";
        int octave = idx / 7; string letter = Letters[idx % 7];
        int alter;
        if (n.Accidental != int.MinValue) { alter = n.Accidental; measureAccidentals[n.Step] = alter; }
        else if (measureAccidentals.TryGetValue(n.Step, out var a)) alter = a;
        else alter = KeyAlter(letter, fifths);
        string acc = alter switch { 1 => "#", -1 => "b", 2 => "x", -2 => "bb", _ => "" };
        return $"{letter}{acc}{octave}";
    }

    private static int KeyAlter(string letter, int fifths)
    {
        if (fifths > 0) return Array.IndexOf(SharpOrder, letter) < fifths ? 1 : 0;
        if (fifths < 0) return Array.IndexOf(FlatOrder, letter) < -fifths ? -1 : 0;
        return 0;
    }
}
