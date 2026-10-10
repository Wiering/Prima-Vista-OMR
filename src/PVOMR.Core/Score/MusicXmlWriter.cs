using System.Text;
using System.Xml;

namespace PVOMR.Core.Score;

/// <summary>Writes the score model as MusicXML 4.0 (score-partwise).</summary>
public static class MusicXmlWriter
{
    public static void Write(Score score, string path)
    {
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", Encoding = new UTF8Encoding(false) };
        using var w = XmlWriter.Create(path, settings);
        w.WriteStartDocument();
        w.WriteDocType("score-partwise", "-//Recordare//DTD MusicXML 4.0 Partwise//EN", "http://www.musicxml.org/dtds/partwise.dtd", null);
        w.WriteStartElement("score-partwise");
        w.WriteAttributeString("version", "4.0");

        if (!string.IsNullOrEmpty(score.Title))
        {
            w.WriteStartElement("work"); w.WriteElementString("work-title", score.Title); w.WriteEndElement();
        }
        w.WriteStartElement("identification");
        w.WriteStartElement("encoding");
        w.WriteElementString("software", "PVOMR");
        w.WriteElementString("encoding-date", DateTime.Now.ToString("yyyy-MM-dd"));
        w.WriteEndElement();
        if (!string.IsNullOrEmpty(score.Source)) w.WriteElementString("source", Path.GetFileName(score.Source));
        w.WriteEndElement();

        w.WriteStartElement("part-list");
        foreach (var p in score.Parts)
        {
            w.WriteStartElement("score-part");
            w.WriteAttributeString("id", p.Id);
            w.WriteElementString("part-name", p.Name);
            w.WriteEndElement();
        }
        w.WriteEndElement();

        foreach (var p in score.Parts) WritePart(w, p, score);
        w.WriteEndElement();
        w.WriteEndDocument();
    }

    private static void WritePart(XmlWriter w, Part part, Score score)
    {
        w.WriteStartElement("part");
        w.WriteAttributeString("id", part.Id);
        bool first = true;
        foreach (var m in part.Measures)
        {
            w.WriteStartElement("measure");
            w.WriteAttributeString("number", m.Number.ToString());
            if (m.Implicit) w.WriteAttributeString("implicit", "yes");

            if (!first && (m.NewSystem || m.NewPage))
            {
                w.WriteStartElement("print");
                if (m.NewPage) w.WriteAttributeString("new-page", "yes"); else w.WriteAttributeString("new-system", "yes");
                w.WriteEndElement();
            }

            bool anyAttr = first || m.KeyFifths.HasValue || m.Time != null || m.Clefs.Any(c => c != null);
            if (anyAttr)
            {
                w.WriteStartElement("attributes");
                if (first) w.WriteElementString("divisions", Score.Divisions.ToString());
                if (m.KeyFifths.HasValue || first)
                {
                    w.WriteStartElement("key"); w.WriteElementString("fifths", (m.KeyFifths ?? 0).ToString()); w.WriteEndElement();
                }
                if (m.Time != null)
                {
                    w.WriteStartElement("time");
                    if (m.Time.Symbol != null) w.WriteAttributeString("symbol", m.Time.Symbol);
                    w.WriteElementString("beats", m.Time.Beats.ToString());
                    w.WriteElementString("beat-type", m.Time.BeatType.ToString());
                    w.WriteEndElement();
                }
                if (first && part.StaffCount > 1) w.WriteElementString("staves", part.StaffCount.ToString());
                for (int s = 0; s < part.StaffCount; s++)
                {
                    var c = m.Clefs[s] ?? (first ? (s == 1 ? ClefSpec.Bass : ClefSpec.Treble) : null);
                    if (c == null) continue;
                    w.WriteStartElement("clef");
                    if (part.StaffCount > 1) w.WriteAttributeString("number", (s + 1).ToString());
                    w.WriteElementString("sign", c.Sign);
                    w.WriteElementString("line", c.Line.ToString());
                    if (c.OctaveChange != 0) w.WriteElementString("clef-octave-change", c.OctaveChange.ToString());
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }

            if (m.LeftBar != BarStyle.None || m.RepeatForward) WriteBarline(w, "left", m.LeftBar, m.RepeatForward ? "forward" : null);

            // events by voice; directions are emitted before the event nearest to them (voice 1 pass)
            var voices = m.Events.GroupBy(e => e.Voice).OrderBy(g => g.Key).ToList();
            var directions = m.Directions.OrderBy(d => d.X).ToList();
            int position = 0; bool firstVoice = true;
            foreach (var voice in voices)
            {
                if (!firstVoice && position > 0)
                {
                    w.WriteStartElement("backup"); w.WriteElementString("duration", position.ToString()); w.WriteEndElement();
                }
                position = 0;
                var evs = voice.OrderBy(e => e.X).ToList();
                for (int i = 0; i < evs.Count; i++)
                {
                    var e = evs[i];
                    if (firstVoice)
                    {
                        double nextX = i + 1 < evs.Count ? (e.X + evs[i + 1].X) / 2.0 : double.MaxValue;
                        foreach (var d in directions.Where(d => d.X < nextX).ToList()) { WriteDirection(w, d, part.StaffCount); directions.Remove(d); }
                    }
                    WriteEvent(w, e, part.StaffCount);
                    position += e.Duration;
                }
                if (firstVoice) foreach (var d in directions) WriteDirection(w, d, part.StaffCount);
                directions.Clear();
                firstVoice = false;
            }
            if (voices.Count == 0) foreach (var d in directions) WriteDirection(w, d, part.StaffCount);

            if (m.RightBar != BarStyle.Regular || m.RepeatBackward) WriteBarline(w, "right", m.RightBar, m.RepeatBackward ? "backward" : null);
            w.WriteEndElement();
            first = false;
        }
        w.WriteEndElement();
    }

    private static void WriteBarline(XmlWriter w, string location, BarStyle style, string? repeat)
    {
        w.WriteStartElement("barline");
        w.WriteAttributeString("location", location);
        string s = style switch { BarStyle.LightLight => "light-light", BarStyle.LightHeavy => "light-heavy", BarStyle.HeavyLight => "heavy-light", BarStyle.Heavy => "heavy", BarStyle.None => "none", _ => "regular" };
        w.WriteElementString("bar-style", s);
        if (repeat != null) { w.WriteStartElement("repeat"); w.WriteAttributeString("direction", repeat); w.WriteEndElement(); }
        w.WriteEndElement();
    }

    private static void WriteDirection(XmlWriter w, Direction d, int staffCount)
    {
        w.WriteStartElement("direction");
        w.WriteAttributeString("placement", d.Above ? "above" : "below");
        w.WriteStartElement("direction-type");
        switch (d.Kind)
        {
            case DirectionKind.Dynamics:
                w.WriteStartElement("dynamics");
                if (IsStandardDynamic(d.Text)) w.WriteStartElement(d.Text); else { w.WriteStartElement("other-dynamics"); w.WriteString(d.Text); }
                w.WriteEndElement();
                w.WriteEndElement();
                break;
            case DirectionKind.WedgeStart:
                w.WriteStartElement("wedge"); w.WriteAttributeString("type", d.Crescendo ? "crescendo" : "diminuendo"); w.WriteEndElement();
                break;
            case DirectionKind.WedgeStop:
                w.WriteStartElement("wedge"); w.WriteAttributeString("type", "stop"); w.WriteEndElement();
                break;
            default:
                w.WriteElementString("words", d.Text);
                break;
        }
        w.WriteEndElement();
        if (staffCount > 1) w.WriteElementString("staff", d.Staff.ToString());
        w.WriteEndElement();
    }

    private static readonly HashSet<string> StandardDynamics = new() { "p", "pp", "ppp", "pppp", "f", "ff", "fff", "ffff", "mp", "mf", "sf", "sfp", "sfpp", "fp", "rf", "rfz", "sfz", "sffz", "fz", "n", "pf", "sfzp" };
    private static bool IsStandardDynamic(string t) => StandardDynamics.Contains(t);

    private static void WriteEvent(XmlWriter w, ScoreEvent e, int staffCount)
    {
        if (e.IsRest)
        {
            w.WriteStartElement("note");
            w.WriteStartElement("rest");
            if (e.MeasureRest) w.WriteAttributeString("measure", "yes");
            w.WriteEndElement();
            w.WriteElementString("duration", e.Duration.ToString());
            w.WriteElementString("voice", e.Voice.ToString());
            if (!e.MeasureRest) { w.WriteElementString("type", e.Type); for (int i = 0; i < e.Dots; i++) w.WriteStartElement("dot"); for (int i = 0; i < e.Dots; i++) w.WriteEndElement(); }
            if (staffCount > 1) w.WriteElementString("staff", e.Staff.ToString());
            if (e.Fermata) { w.WriteStartElement("notations"); w.WriteElementString("fermata", ""); w.WriteEndElement(); }
            w.WriteEndElement();
            return;
        }
        for (int ni = 0; ni < e.Notes.Count; ni++)
        {
            var n = e.Notes[ni];
            w.WriteStartElement("note");
            if (ni > 0) w.WriteElementString("chord", "");
            w.WriteStartElement("pitch");
            w.WriteElementString("step", n.Pitch.Step);
            if (n.Pitch.Alter != 0) w.WriteElementString("alter", n.Pitch.Alter.ToString());
            w.WriteElementString("octave", n.Pitch.Octave.ToString());
            w.WriteEndElement();
            w.WriteElementString("duration", e.Duration.ToString());
            if (n.TieStart) { w.WriteStartElement("tie"); w.WriteAttributeString("type", "start"); w.WriteEndElement(); }
            if (n.TieStop) { w.WriteStartElement("tie"); w.WriteAttributeString("type", "stop"); w.WriteEndElement(); }
            w.WriteElementString("voice", e.Voice.ToString());
            w.WriteElementString("type", e.Type);
            for (int i = 0; i < e.Dots; i++) w.WriteElementString("dot", "");
            if (n.AccidentalName != null) w.WriteElementString("accidental", n.AccidentalName);
            if (e.StemUp.HasValue) w.WriteElementString("stem", e.StemUp.Value ? "up" : "down");
            if (staffCount > 1) w.WriteElementString("staff", e.Staff.ToString());
            if (ni == 0)
                for (int b = 0; b < e.Beams.Length; b++)
                {
                    if (e.Beams[b] == BeamState.None) continue;
                    w.WriteStartElement("beam");
                    w.WriteAttributeString("number", (b + 1).ToString());
                    w.WriteString(e.Beams[b] switch { BeamState.Begin => "begin", BeamState.Continue => "continue", BeamState.End => "end", BeamState.ForwardHook => "forward hook", _ => "backward hook" });
                    w.WriteEndElement();
                }
            bool notations = n.TieStart || n.TieStop || (ni == 0 && (e.Articulations.Count > 0 || e.Fermata || e.SlurStarts.Count > 0 || e.SlurStops.Count > 0));
            if (notations)
            {
                w.WriteStartElement("notations");
                if (n.TieStart) { w.WriteStartElement("tied"); w.WriteAttributeString("type", "start"); w.WriteEndElement(); }
                if (n.TieStop) { w.WriteStartElement("tied"); w.WriteAttributeString("type", "stop"); w.WriteEndElement(); }
                if (ni == 0)
                {
                    foreach (var s in e.SlurStops) { w.WriteStartElement("slur"); w.WriteAttributeString("type", "stop"); w.WriteAttributeString("number", s.ToString()); w.WriteEndElement(); }
                    foreach (var s in e.SlurStarts) { w.WriteStartElement("slur"); w.WriteAttributeString("type", "start"); w.WriteAttributeString("number", s.ToString()); w.WriteEndElement(); }
                    if (e.Articulations.Count > 0)
                    {
                        w.WriteStartElement("articulations");
                        foreach (var a in e.Articulations) w.WriteElementString(a, "");
                        w.WriteEndElement();
                    }
                    if (e.Fermata) w.WriteElementString("fermata", "");
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
    }
}
