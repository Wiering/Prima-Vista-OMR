using System.Globalization;
using System.Text;

namespace PVOMR.Core.Score;

/// <summary>An instrument known by name: English name, General MIDI program (1-128) and whether it is a voice.</summary>
public sealed record Instrument(string Id, string Name, int Program, bool Voice = false);

/// <summary>
/// Recognises instrument names as printed in scores (Italian, German, French, English, Dutch; full names, plurals
/// and the usual abbreviations, OCR-tolerant: diacritics, dots and numbers are ignored) and maps them to a General
/// MIDI program for playback.
/// </summary>
public static class Instruments
{
    private sealed record Entry(Instrument Instrument, string[] Stems, string[] Exact, (string, string)[] Pairs);

    private static Entry E(string id, string name, int program, string[] stems, string[] exact, (string, string)[]? pairs = null, bool voice = false)
        => new(new Instrument(id, name, program, voice), stems, exact, pairs ?? Array.Empty<(string, string)>());

    // order matters: specific instruments (cor anglais, bass clarinet, contrabassoon, piccolo, cello) come before the
    // general ones whose stems they share
    private static readonly Entry[] Table =
    {
        E("english-horn", "English Horn", 70, new[] { "englischhorn", "anglais", "ingles", "ingl" }, new[] { "ci", "eh" }, new[] { ("corno", "inglese"), ("cor", "anglais"), ("englisch", "horn") }),
        E("bass-clarinet", "Bass Clarinet", 72, new[] { "bassklar", "clarone", "bassclar" }, new[] { "bkl", "bcl" }, new[] { ("clarinetto", "basso"), ("clarinette", "basse"), ("bass", "clarinet"), ("cl", "b") }),
        E("contrabassoon", "Contrabassoon", 71, new[] { "contrafag", "kontrafag", "contrebasson", "contrabassoon", "contrafagot" }, new[] { "cfg", "kfg", "cbsn" }),
        E("piccolo", "Piccolo", 73, new[] { "piccolo", "ottavino", "petite flute" }, new[] { "picc", "pic", "ott", "kl fl" }, new[] { ("kleine", "flote"), ("flauto", "piccolo"), ("petite", "flute") }),
        E("recorder", "Recorder", 75, new[] { "blockflot", "recorder", "blokfluit" }, Array.Empty<string>(), new[] { ("flauto", "dolce") }),
        E("violone", "Contrabass", 44, Array.Empty<string>(), new[] { "violone", "violoni" }),
        E("cello", "Violoncello", 43, new[] { "violoncel", "cello", "celli", "violonc" }, new[] { "vc", "vcl", "vlc", "vcs", "vcelli", "vcello" }),
        E("viola", "Viola", 42, new[] { "viola", "viole", "violen", "bratsch", "altviool" }, new[] { "vla", "vle", "vlas", "va", "br", "bratsche" }),
        E("violin", "Violin", 41, new[] { "violin", "violon", "geige", "viool", "violino" }, new[] { "vl", "vln", "vni", "vi", "vio", "viol", "vno", "vlni", "v" }),
        E("contrabass", "Contrabass", 44, new[] { "contrab", "kontrab", "contreb", "doublebass", "double bass", "bassi" }, new[] { "cb", "kb", "db", "cbs", "cbassi", "basso", "bass", "b" }),
        E("flute", "Flute", 74, new[] { "flaut", "flot", "flute", "fluit", "flauti" }, new[] { "fl", "fll", "fls", "gr fl" }),
        E("oboe", "Oboe", 69, new[] { "oboe", "oboi", "hautbois", "hoboe", "hobo" }, new[] { "ob", "obs", "hb", "hob" }),
        E("clarinet", "Clarinet", 72, new[] { "clarinet", "klarinet", "clarinett", "clarinetti" }, new[] { "cl", "clar", "kl", "klar", "clt", "cls" }),
        E("bassoon", "Bassoon", 71, new[] { "fagott", "fagot", "bassoon", "basson" }, new[] { "fg", "fag", "fgt", "bsn", "bn", "fgg" }),
        E("saxophone", "Saxophone", 66, new[] { "saxo", "sax" }, new[] { "sax", "asax", "tsax" }),
        E("horn", "Horn", 61, new[] { "corni", "corno", "horner", "horn", "hoorn" }, new[] { "cor", "cors", "hn", "hr", "hrn", "cr", "hns" }),
        E("cornet", "Cornet", 57, new[] { "cornet", "pistons", "kornett" }, new[] { "crt", "cnt" }),
        E("trumpet", "Trumpet", 57, new[] { "tromba", "trombe", "trompet", "trumpet", "trompete", "trompette" }, new[] { "tr", "trp", "tpt", "trpt", "trbe", "tpts" }),
        E("trombone", "Trombone", 58, new[] { "trombon", "posaun", "trombone" }, new[] { "trb", "tbn", "pos", "trbn", "tromb", "trbni", "tbns" }),
        E("tuba", "Tuba", 59, new[] { "tuba", "tuben" }, new[] { "tb", "tba" }),
        E("timpani", "Timpani", 48, new[] { "timpan", "pauke", "pauken", "timbale", "kettledrum" }, new[] { "timp", "pk", "pkn", "timb", "tmp" }),
        E("glockenspiel", "Glockenspiel", 10, new[] { "glockensp", "campanelli", "carillon" }, new[] { "glsp", "glock", "camp" }),
        E("xylophone", "Xylophone", 14, new[] { "xylo", "xilo" }, new[] { "xyl" }),
        E("celesta", "Celesta", 9, new[] { "celest" }, new[] { "cel", "cela" }),
        E("harp", "Harp", 47, new[] { "arpa", "harfe", "harp", "harpe" }, new[] { "hfe", "hp", "hrp", "arp" }),
        E("harpsichord", "Harpsichord", 7, new[] { "cembalo", "clavecin", "harpsichord", "klavecimbel" }, new[] { "cemb" }),
        E("organ", "Organ", 20, new[] { "organo", "orgel", "organ", "orgue" }, new[] { "org" }),
        E("piano", "Piano", 1, new[] { "piano", "pianoforte", "klavier", "clavier", "fortepiano" }, new[] { "pf", "pno", "pfte", "klav", "pft" }),
        E("guitar", "Guitar", 25, new[] { "guitar", "gitarr", "chitarr", "guitare", "gitaar" }, new[] { "git", "gtr", "guit" }),
        E("soprano", "Soprano", 53, new[] { "soprano", "sopran", "soprani", "canto", "discant" }, new[] { "s", "sop" }, voice: true),
        E("alto", "Alto", 53, new[] { "alto", "contralto", "alti" }, new[] { "alt", "a" }, voice: true),
        E("tenor", "Tenor", 53, new[] { "tenor", "tenore", "tenori" }, new[] { "t", "ten" }, voice: true),
        E("baritone", "Baritone", 53, new[] { "bariton" }, new[] { "bar" }, voice: true),
        E("voice", "Voice", 54, new[] { "voice", "voce", "singstimme", "gesang", "chor", "choir", "coro", "stimme", "zang", "mezzo" }, Array.Empty<string>(), voice: true),
    };

    /// <summary>The tokens that are a contrabass in an orchestra but the bass voice in a vocal score.</summary>
    private static readonly HashSet<string> BassWords = new() { "basso", "bass", "b", "bassi" };

    public static Instrument Bass { get; } = new("bass", "Bass", 53, true);

    /// <summary>Lower-case letter words without diacritics; dots, digits and other marks separate words.</summary>
    public static List<string> Tokens(string text)
    {
        var norm = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(norm.Length);
        foreach (char ch in norm)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetter(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>The instrument a printed name stands for, or null. <paramref name="bassWord"/> is set when the match
    /// rests on a word that may also be the bass voice (decided by the caller from the other parts).</summary>
    public static Instrument? Match(string? text, out bool bassWord) => Match(text, out bassWord, out _);

    /// <summary>As <see cref="Match(string?, out bool)"/>; <paramref name="fuzzy"/> is set when only an OCR-tolerant
    /// (edit distance) comparison matched, i.e. the printed name was misread.</summary>
    public static Instrument? Match(string? text, out bool bassWord, out bool fuzzy)
    {
        bassWord = false; fuzzy = false;
        if (string.IsNullOrWhiteSpace(text)) return null;
        var tokens = Tokens(text);
        if (tokens.Count == 0) return null;
        // "in B", "in F", "in Es": the key of a transposing instrument is no instrument word
        var words = new List<string>();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] == "in" && i + 1 < tokens.Count && tokens[i + 1].Length <= 3) { i++; continue; }
            if (tokens[i] is "i" or "ii" or "iii" or "iv" or "e" or "und" or "and" or "et" or "en" or "solo" or "tutti" or "div" or "unis" or "a" or "il") continue;
            words.Add(tokens[i]);
        }
        if (words.Count == 0) words = tokens;
        // two-word names first ("Corno inglese", "Clarinetto basso", "Flauto piccolo")
        foreach (var e in Table)
            foreach (var (p, q) in e.Pairs)
                if (words.Contains(p) && words.Contains(q)) return e.Instrument;
        // then the first word that names an instrument ("Violoncello e Basso" is a cello part, "Trombone basso e
        // Tuba" a trombone part); within one word the table order decides (cello before violin)
        foreach (var w in words)
        {
            // one-letter words are abbreviations only when the whole name is that letter (S, A, T, B, V)
            if (w.Length == 1 && words.Count > 1) continue;
            foreach (var e in Table)
                if (e.Stems.Any(st => !st.Contains(' ') && w.Length >= 4 && w.StartsWith(st)) || e.Exact.Contains(w))
                {
                    bassWord = e.Instrument.Id == "contrabass" && BassWords.Contains(w);
                    return e.Instrument;
                }
        }
        // OCR slips ("Tronube" for "Trombe"): a long word within a small edit distance of a full-name stem
        foreach (var e in Table)
            foreach (var w in words.Where(w => w.Length >= 5))
                foreach (var st in e.Stems.Where(st => !st.Contains(' ') && st.Length >= 5))
                {
                    string head = w.Length > st.Length + 2 ? w[..(st.Length + 2)] : w;
                    int d = Math.Min(Distance(head, st), Distance(w, st));
                    int allowed = st.Length >= 6 ? (w.Length >= 7 && w[0] == st[0] ? 3 : 2) : 1;
                    if (d <= allowed) { fuzzy = true; return e.Instrument; }
                }
        return null;
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    public static Instrument? Match(string? text) => Match(text, out _);

    /// <summary>
    /// A display name from an OCR reading: leading fragments before the instrument word ("c. Corni in") and a
    /// dangling "in" go; a misread name ("N iolino I.") is replaced by the instrument's name plus its numeral.
    /// </summary>
    public static string DisplayName(string text, Instrument inst, bool fuzzy)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (fuzzy || text.Contains('�'))
        {
            string last = words.Count > 1 ? words[^1].TrimEnd('.') : "";
            string numeral = last is "I" or "II" or "III" or "IV" or "1" or "2" or "3" or "4" ? " " + last : "";
            return inst.Name + numeral;
        }
        // drop short fragments in front of the first word that names this instrument
        int first = words.FindIndex(w => Match(w) is Instrument m && m.Id == inst.Id);
        if (first > 0 && words.Take(first).All(w => w.Count(char.IsLetter) <= 2)) words.RemoveRange(0, first);
        if (words.Count > 1 && words[^1] is "in" or "In") words.RemoveAt(words.Count - 1);
        return string.Join(" ", words);
    }

    /// <summary>
    /// Whether an OCR reading that names no known instrument still looks like printed words ("Drums", "Secondo")
    /// rather than misread ink ("I III", "fbmbOne b", a publisher's line): one to three words of mostly letters,
    /// each word in a single case pattern (Title, lower or UPPER).
    /// </summary>
    public static bool LooksLikeName(string text)
    {
        if (text.Contains('�')) return false;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Length > 3) return false;
        int letters = text.Count(char.IsLetter), others = text.Count(c => !char.IsWhiteSpace(c) && !char.IsLetter(c) && c != '.');
        if (letters < 3 || others * 4 > letters) return false;
        foreach (var w0 in words)
        {
            string w = w0.TrimEnd('.');
            if (w.Length == 0) continue;
            bool upper = w.All(c => !char.IsLetter(c) || char.IsUpper(c)), lower = w.All(c => !char.IsLetter(c) || char.IsLower(c));
            bool title = char.IsUpper(w[0]) && w.Skip(1).All(c => !char.IsLetter(c) || char.IsLower(c));
            if (!(upper || lower || title)) return false;
        }
        // at least one real word: three letters or more and not a Roman numeral
        return words.Any(w => w.Count(char.IsLetter) >= 3 && !w.TrimEnd('.').All(c => c is 'I' or 'V' or 'X' or 'L'));
    }

    /// <summary>A word that is one of the table's abbreviations ("Clar", "Picc", "Timp").</summary>
    public static bool IsAbbreviation(string word)
    {
        var t = Tokens(word);
        return t.Count == 1 && Table.Any(e => e.Exact.Contains(t[0]));
    }
}
