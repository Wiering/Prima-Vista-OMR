using System.Xml.Linq;

namespace PVOMR.Core.Score;

/// <summary>Reads the subset of MusicXML (score-partwise) needed for evaluation into the score model.</summary>
public static class MusicXmlReader
{
    public static Score Read(string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root ?? throw new IOException("empty MusicXML");
        if (root.Name.LocalName != "score-partwise") throw new NotSupportedException("only score-partwise MusicXML is supported");
        var score = new Score { Source = path, Title = root.Element("work")?.Element("work-title")?.Value };
        var names = root.Element("part-list")?.Elements("score-part").ToDictionary(e => e.Attribute("id")?.Value ?? "", e => e.Element("part-name")?.Value ?? "") ?? new();
        foreach (var partEl in root.Elements("part"))
        {
            var part = new Part { Id = partEl.Attribute("id")?.Value ?? $"P{score.Parts.Count + 1}" };
            part.Name = names.GetValueOrDefault(part.Id, part.Id);
            int divisions = 1;
            foreach (var mEl in partEl.Elements("measure"))
            {
                var m = new ScoreMeasure { Number = int.TryParse(mEl.Attribute("number")?.Value, out int n) ? n : part.Measures.Count + 1, Implicit = mEl.Attribute("implicit")?.Value == "yes" };
                var attr = mEl.Element("attributes");
                if (attr != null)
                {
                    if (int.TryParse(attr.Element("divisions")?.Value, out int d) && d > 0) divisions = d;
                    if (int.TryParse(attr.Element("key")?.Element("fifths")?.Value, out int f)) m.KeyFifths = f;
                    var t = attr.Element("time");
                    if (t != null && int.TryParse(t.Element("beats")?.Value, out int beats) && int.TryParse(t.Element("beat-type")?.Value, out int bt)) m.Time = new TimeSpec(beats, bt, t.Attribute("symbol")?.Value);
                    if (int.TryParse(attr.Element("staves")?.Value, out int staves)) part.StaffCount = Math.Max(part.StaffCount, staves);
                    foreach (var c in attr.Elements("clef"))
                    {
                        int num = int.TryParse(c.Attribute("number")?.Value, out int cn) ? cn : 1;
                        int line = int.TryParse(c.Element("line")?.Value, out int cl) ? cl : 2;
                        int oct = int.TryParse(c.Element("clef-octave-change")?.Value, out int co) ? co : 0;
                        if (num >= 1 && num <= 2) m.Clefs[num - 1] = new ClefSpec(c.Element("sign")?.Value ?? "G", line, oct);
                    }
                }
                int position = 0;
                ScoreEvent? last = null;
                foreach (var el in mEl.Elements())
                {
                    switch (el.Name.LocalName)
                    {
                        case "backup": position -= Scale(el, divisions); break;
                        case "forward": position += Scale(el, divisions); break;
                        case "note":
                        {
                            bool chord = el.Element("chord") != null;
                            bool grace = el.Element("grace") != null;
                            if (grace) break;
                            int dur = Scale(el, divisions);
                            if (chord && last != null)
                            {
                                var pn = ReadPitch(el);
                                if (pn != null) last.Notes.Add(pn);
                                break;
                            }
                            var ev = new ScoreEvent
                            {
                                IsRest = el.Element("rest") != null,
                                MeasureRest = el.Element("rest")?.Attribute("measure")?.Value == "yes",
                                Duration = dur,
                                Voice = int.TryParse(el.Element("voice")?.Value, out int v) ? v : 1,
                                Staff = int.TryParse(el.Element("staff")?.Value, out int st) ? st : 1,
                                Type = el.Element("type")?.Value ?? TypeFromDuration(dur),
                                Dots = el.Elements("dot").Count(),
                                X = position,
                            };
                            var stem = el.Element("stem")?.Value;
                            if (stem == "up") ev.StemUp = true; else if (stem == "down") ev.StemUp = false;
                            if (!ev.IsRest)
                            {
                                var p = ReadPitch(el);
                                if (p != null) ev.Notes.Add(p);
                            }
                            m.Events.Add(ev);
                            last = ev;
                            position += dur;
                            break;
                        }
                        case "barline":
                        {
                            string loc = el.Attribute("location")?.Value ?? "right";
                            var style = el.Element("bar-style")?.Value switch { "light-light" => BarStyle.LightLight, "light-heavy" => BarStyle.LightHeavy, "heavy-light" => BarStyle.HeavyLight, "heavy" => BarStyle.Heavy, "none" => BarStyle.None, _ => BarStyle.Regular };
                            if (loc == "left") m.LeftBar = style; else m.RightBar = style;
                            var rep = el.Element("repeat")?.Attribute("direction")?.Value;
                            if (rep == "forward") m.RepeatForward = true; else if (rep == "backward") m.RepeatBackward = true;
                            break;
                        }
                        case "direction":
                        {
                            var dyn = el.Element("direction-type")?.Element("dynamics")?.Elements().FirstOrDefault();
                            if (dyn != null) m.Directions.Add(new Direction { Kind = DirectionKind.Dynamics, Text = dyn.Name.LocalName, X = position });
                            break;
                        }
                    }
                }
                part.Measures.Add(m);
            }
            score.Parts.Add(part);
        }
        return score;
    }

    private static int Scale(XElement el, int divisions)
    {
        int d = int.TryParse(el.Element("duration")?.Value, out int v) ? v : 0;
        return (int)Math.Round(d * (double)Score.Divisions / divisions);
    }

    private static ScoreNote? ReadPitch(XElement note)
    {
        var p = note.Element("pitch");
        if (p == null) return null;
        string step = p.Element("step")?.Value ?? "C";
        int alter = (int)Math.Round(double.TryParse(p.Element("alter")?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double a) ? a : 0);
        int octave = int.TryParse(p.Element("octave")?.Value, out int o) ? o : 4;
        return new ScoreNote { Pitch = new PitchSpec(step, alter, octave), AccidentalName = note.Element("accidental")?.Value };
    }

    private static string TypeFromDuration(int d) => d switch
    {
        >= 4 * Score.Divisions => "whole", >= 2 * Score.Divisions => "half", >= Score.Divisions => "quarter", >= Score.Divisions / 2 => "eighth", >= Score.Divisions / 4 => "16th", _ => "32nd",
    };
}
