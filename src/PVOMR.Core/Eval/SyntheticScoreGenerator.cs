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
        public int MinType { get; set; } = 4;       // 1 = whole ... 4 = 16th: fastest note value used
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
                var events = FillVoice(s, time, clefs[st], ref stepPos[st], fifths);
                foreach (var e in events) { e.Staff = st + 1; e.Voice = voice; }
                m.Events.AddRange(events);
                if (st == 0 && _rnd.NextDouble() < s.DynamicsProbability)
                    m.Directions.Add(new Direction { Kind = DirectionKind.Dynamics, Text = Pick("p", "f", "mf", "pp", "ff", "mp"), Staff = 1, X = 0 });
            }
            part.Measures.Add(m);
        }
    }

    private static readonly (string type, int div)[] Types = { ("whole", 384), ("half", 192), ("quarter", 96), ("eighth", 48), ("16th", 24) };

    private List<ScoreEvent> FillVoice(Settings s, TimeSpec time, ClefSpec clef, ref int stepPos, int fifths)
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
            // prefer shorter notes a bit more in common time, rhythmic pairs for dotted values
            var pick = choices[_rnd.Next(choices.Count)];
            if (pick.type == "whole" && time.Length != 384) continue;
            if (pick.dots == 1 && _rnd.NextDouble() < 0.5) continue;
            int dur = pick.div;
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
                ev.StemUp = stepPos < 4;
            }
            events.Add(ev);
            remaining -= dur; x += dur;
        }
        if (remaining > 0) events.Add(new ScoreEvent { IsRest = true, Type = TypeFor(remaining), Duration = remaining, X = x });
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
