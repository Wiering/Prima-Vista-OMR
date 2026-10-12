using PVOMR.Core.Score;

namespace PVOMR.Core.Eval;

/// <summary>Generates random but musically plausible short pieces as score models (ground truth for evaluation).</summary>
public sealed class SyntheticScoreGenerator
{
    private readonly Random _rnd;

    public sealed class Settings
    {
        public int Measures { get; set; } = 16;
        public bool Piano { get; set; }
        public string? Clef { get; set; }           // treble, bass, alto, tenor or null = random
        public int? KeyFifths { get; set; }
        public string? Time { get; set; }           // "4/4", "3/4", "2/4", "6/8", "C"
        public double ChordProbability { get; set; } = 0.1;
        public double RestProbability { get; set; } = 0.12;
        public double AccidentalProbability { get; set; } = 0.08;
        public double DynamicsProbability { get; set; } = 0.15;
        public double SlurProbability { get; set; } = 0.15;
        public int MinType { get; set; } = 4;       // fastest note value used: 1 = whole ... 4 = 8th, 5 = 16th
        /// <summary>Weight of short values (8ths/16ths): 1 = uniform, higher = denser beamed rhythms.</summary>
        public double Density { get; set; } = 1.0;
        /// <summary>Probability that a staff gets a second voice (stems down) in a measure.</summary>
        public double SecondVoiceProbability { get; set; } = 0.0;
        public double TieProbability { get; set; } = 0.0;
        /// <summary>Probability that a quarter-note slot becomes a triplet of 8ths.</summary>
        public double TripletProbability { get; set; } = 0.0;
        /// <summary>Probability that an 8th becomes four 32nds (or 32nds + a 16th) and a 16th four 64ths.</summary>
        public double FastProbability { get; set; } = 0.0;
        /// <summary>Probability of grace notes before a note: one slashed 8th (acciaccatura) or two or three 16ths.</summary>
        public double GraceProbability { get; set; } = 0.0;
    }

    public SyntheticScoreGenerator(int seed) { _rnd = new Random(seed); }

    public Score.Score Generate(Settings s, string title)
    {
        var score = new Score.Score { Title = title };
        var time = s.Time != null ? MusicAssembler.ParseTime(s.Time)! : Pick(new TimeSpec(4, 4), new TimeSpec(3, 4), new TimeSpec(2, 4), new TimeSpec(6, 8), new TimeSpec(4, 4, "common"));
        int fifths = s.KeyFifths ?? _rnd.Next(-4, 5);
        if (s.Piano)
        {
            var part = new Part { Id = "P1", Name = "Piano", StaffCount = 2 };
            score.Parts.Add(part);
            FillPart(part, s, time, fifths, new[] { ClefSpec.Treble, ClefSpec.Bass });
        }
        else
        {
            var clef = s.Clef switch { "bass" => ClefSpec.Bass, "alto" => ClefSpec.Alto, "tenor" => ClefSpec.Tenor, "treble" => ClefSpec.Treble, _ => Pick(ClefSpec.Treble, ClefSpec.Treble, ClefSpec.Bass, ClefSpec.Alto) };
            var part = new Part { Id = "P1", Name = "Part", StaffCount = 1 };
            score.Parts.Add(part);
            FillPart(part, s, time, fifths, new[] { clef });
        }
        return score;
    }

    private void FillPart(Part part, Settings s, TimeSpec time, int fifths, ClefSpec[] clefs)
    {
        int[] stepPos = clefs.Select(c => c.Sign == "F" ? 4 : 6).ToArray(); // current staff step (random walk), 0 = bottom line
        for (int mi = 0; mi < s.Measures; mi++)
        {
            var m = new ScoreMeasure { Number = mi + 1 };
            if (mi == 0) { m.Time = time; m.KeyFifths = fifths; for (int c = 0; c < clefs.Length; c++) m.Clefs[c] = clefs[c]; }
            if (mi == s.Measures - 1) m.RightBar = BarStyle.LightHeavy;
            else if (_rnd.NextDouble() < 0.06) m.RightBar = BarStyle.LightLight;
            for (int st = 0; st < clefs.Length; st++)
            {
                int voice = st * 2 + 1;
                bool twoVoices = _rnd.NextDouble() < s.SecondVoiceProbability;
                var events = FillVoice(s, time, clefs[st], ref stepPos[st], fifths, twoVoices ? 1 : 0);
                foreach (var e in events) { e.Staff = st + 1; e.Voice = voice; }
                m.Events.AddRange(events);
                if (twoVoices)
                {
                    int low = Math.Max(-4, stepPos[st] - 5);
                    var events2 = FillVoice(s, time, clefs[st], ref low, fifths, -1);
                    foreach (var e in events2) { e.Staff = st + 1; e.Voice = voice + 1; }
                    m.Events.AddRange(events2);
                }
                if (st == 0 && _rnd.NextDouble() < s.DynamicsProbability)
                    m.Directions.Add(new Direction { Kind = DirectionKind.Dynamics, Text = Pick("p", "f", "mf", "pp", "ff", "mp"), Staff = 1, X = 0 });
            }
            part.Measures.Add(m);
        }
    }

    private static readonly (string type, int div)[] Types = { ("whole", 384), ("half", 192), ("quarter", 96), ("eighth", 48), ("16th", 24) };

    private List<ScoreEvent> FillVoice(Settings s, TimeSpec time, ClefSpec clef, ref int stepPos, int fifths, int forcedStem = 0)
    {
        var events = new List<ScoreEvent>();
        int remaining = time.Length;
        int beat = time.BeatType == 8 ? 144 : 96;   // 6/8: dotted-quarter beats
        int x = 0;
        while (remaining > 0)
        {
            // choose a duration that fits the remaining time and does not cross a beat too oddly
            var choices = new List<(string type, int div, int dots)>();
            foreach (var (type, div) in Types)
            {
                if (Types.ToList().FindIndex(t => t.type == type) + 1 > s.MinType) continue;
                if (div <= remaining) choices.Add((type, div, 0));
                if (div * 3 / 2 <= remaining && div >= 48 && (remaining % beat == 0 || div * 3 / 2 <= remaining % beat || remaining % beat == 0)) choices.Add((type, div * 3 / 2, 1));
            }
            if (choices.Count == 0) break;
            // density > 1 favours 8ths and 16ths (beamed runs)
            var pick = choices[_rnd.Next(choices.Count)];
            if (s.Density > 1 && _rnd.NextDouble() < 1 - 1 / s.Density)
            {
                var shortOnes = choices.Where(c => c.div <= 72).ToList();
                if (shortOnes.Count > 0) pick = shortOnes[_rnd.Next(shortOnes.Count)];
            }
            if (pick.type == "whole" && time.Length != 384) continue;
            if (pick.dots == 1 && _rnd.NextDouble() < 0.5) continue;
            int dur = pick.div;
            if (pick.type == "quarter" && pick.dots == 0 && s.TripletProbability > 0 && _rnd.NextDouble() < s.TripletProbability)
            {
                // a triplet of three 8ths in the time of one quarter
                for (int k = 0; k < 3; k++)
                {
                    var te = new ScoreEvent { Type = "eighth", Duration = 32, X = x + k * 32, TupletActual = 3, TupletNormal = 2, TupletStart = k == 0, TupletStop = k == 2 };
                    stepPos = Math.Clamp(stepPos + _rnd.Next(-2, 3), -4, 14);
                    te.Notes.Add(MakeNote(clef, stepPos, fifths, s));
                    te.StemUp = forcedStem > 0 ? true : forcedStem < 0 ? false : stepPos < 4;
                    events.Add(te);
                }
                remaining -= 96; x += 96;
                continue;
            }
            if (pick.dots == 0 && pick.type is "eighth" or "16th" && s.FastProbability > 0 && _rnd.NextDouble() < s.FastProbability)
            {
                // a run of 32nds in place of an 8th, or of 64ths in place of a 16th
                var runTypes = pick.type == "16th" ? new[] { ("64th", 6), ("64th", 6), ("64th", 6), ("64th", 6) }
                             : _rnd.NextDouble() < 0.6 ? new[] { ("32nd", 12), ("32nd", 12), ("32nd", 12), ("32nd", 12) } : new[] { ("32nd", 12), ("32nd", 12), ("16th", 24) };
                int rx = x;
                foreach (var (rt, rd) in runTypes)
                {
                    var re = new ScoreEvent { Type = rt, Duration = rd, X = rx };
                    stepPos = Math.Clamp(stepPos + _rnd.Next(-2, 3), -4, 14);
                    re.Notes.Add(MakeNote(clef, stepPos, fifths, s));
                    re.StemUp = forcedStem > 0 ? true : forcedStem < 0 ? false : stepPos < 4;
                    events.Add(re); rx += rd;
                }
                remaining -= dur; x += dur;
                continue;
            }
            if (s.GraceProbability > 0 && pick.div >= 48 && _rnd.NextDouble() < s.GraceProbability && !(forcedStem != 0))
            {
                // grace notes before the coming note: they take no time
                int count = _rnd.NextDouble() < 0.6 ? 1 : _rnd.Next(2, 4);
                for (int k = 0; k < count; k++)
                {
                    var ge = new ScoreEvent { Grace = true, GraceSlash = count == 1, Type = count == 1 ? "eighth" : "16th", Duration = 0, X = x };
                    int gs = Math.Clamp(stepPos + _rnd.Next(-2, 3), -3, 13);
                    ge.Notes.Add(MakeNote(clef, gs, fifths, s));
                    ge.StemUp = true;
                    events.Add(ge);
                }
            }
            var ev = new ScoreEvent { Type = pick.type, Dots = pick.dots, Duration = dur, X = x };
            if (_rnd.NextDouble() < s.RestProbability && pick.type != "whole") ev.IsRest = true;
            else
            {
                // random walk of the staff step within a readable range
                stepPos += _rnd.Next(-3, 4);
                stepPos = Math.Clamp(stepPos, -4, 14);
                ev.Notes.Add(MakeNote(clef, stepPos, fifths, s));
                if (_rnd.NextDouble() < s.ChordProbability)
                {
                    int second = stepPos + Pick(2, 3, 4, 5, 7);
                    if (second <= 14) ev.Notes.Add(MakeNote(clef, second, fifths, s));
                }
                ev.StemUp = forcedStem > 0 ? true : forcedStem < 0 ? false : stepPos < 4;
                if (forcedStem > 0) stepPos = Math.Max(stepPos, 4); else if (forcedStem < 0) stepPos = Math.Min(stepPos, 3);
            }
            events.Add(ev);
            remaining -= dur; x += dur;
        }
        if (remaining > 0) events.Add(new ScoreEvent { IsRest = true, Type = TypeFor(remaining), Duration = remaining, X = x });
        // ties between consecutive notes of equal pitch (same voice)
        if (s.TieProbability > 0)
            for (int i = 0; i + 1 < events.Count; i++)
            {
                var a = events[i]; var b = events[i + 1];
                if (a.IsRest || b.IsRest || a.Notes.Count != 1 || b.Notes.Count != 1) continue;
                if (_rnd.NextDouble() < s.TieProbability) { b.Notes[0].Pitch = a.Notes[0].Pitch; b.Notes[0].AccidentalName = null; a.Notes[0].TieStart = true; b.Notes[0].TieStop = true; }
            }
        // occasional slur over 2-4 notes
        if (_rnd.NextDouble() < s.SlurProbability)
        {
            var notes = events.Where(e => !e.IsRest).ToList();
            if (notes.Count >= 2)
            {
                int a = _rnd.Next(notes.Count - 1); int b = Math.Min(notes.Count - 1, a + _rnd.Next(1, 4));
                notes[a].SlurStarts.Add(1); notes[b].SlurStops.Add(1);
            }
        }
        return events;
    }

    private static string TypeFor(int d) => d switch { 384 => "whole", 192 => "half", 96 => "quarter", 48 => "eighth", 24 => "16th", 144 => "half", 72 => "quarter", _ => "16th" };

    private static readonly string[] Letters = { "C", "D", "E", "F", "G", "A", "B" };
    private static readonly string[] SharpOrder = { "F", "C", "G", "D", "A", "E", "B" };
    private static readonly string[] FlatOrder = { "B", "E", "A", "D", "G", "C", "F" };

    private ScoreNote MakeNote(ClefSpec clef, int step, int fifths, Settings s)
    {
        int baseIdx = clef.Sign == "F" ? 2 * 7 + 4 : clef.Sign == "C" && clef.Line == 3 ? 3 * 7 + 3 : clef.Sign == "C" ? 3 * 7 + 1 : 4 * 7 + 2;
        int idx = baseIdx + step;
        string letter = Letters[idx % 7]; int octave = idx / 7;
        int alter = fifths > 0 ? (Array.IndexOf(SharpOrder, letter) < fifths ? 1 : 0) : fifths < 0 ? (Array.IndexOf(FlatOrder, letter) < -fifths ? -1 : 0) : 0;
        string? acc = null;
        if (_rnd.NextDouble() < s.AccidentalProbability)
        {
            int newAlter = Pick(-1, 0, 1);
            if (newAlter != alter) { alter = newAlter; acc = newAlter switch { 1 => "sharp", -1 => "flat", _ => "natural" }; }
        }
        return new ScoreNote { Pitch = new PitchSpec(letter, alter, octave), AccidentalName = acc };
    }

    private T Pick<T>(params T[] items) => items[_rnd.Next(items.Length)];
}
