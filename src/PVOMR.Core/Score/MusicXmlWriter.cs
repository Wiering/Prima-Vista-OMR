using System.Text;
using System.Xml;

namespace PVOMR.Core.Score;

/// <summary>Writes the score model as MusicXML 4.0 (score-partwise).</summary>
/// <summary>What besides notes and rests goes into the MusicXML. Everything is on by default.</summary>
public sealed class ExportOptions
{
    /// <summary>Dynamic marks and hairpins (crescendo/diminuendo wedges).</summary>
    public bool Dynamics { get; set; } = true;
    /// <summary>Staccato and the other articulation marks, fermatas.</summary>
    public bool Articulations { get; set; } = true;
    public bool Slurs { get; set; } = true;
    public bool Ties { get; set; } = true;
    /// <summary>Text directions such as tempo words.</summary>
    public bool Text { get; set; } = true;
    /// <summary>Repeat signs and special bar line styles (double, final); off writes plain bar lines, so playback runs straight through.</summary>
    public bool Repeats { get; set; } = true;
    /// <summary>Tuplet brackets and numbers (the durations keep their tuplet ratio either way).</summary>
    public bool TupletBrackets { get; set; } = true;
    /// <summary>Beam groups (visual only; without them the notes are flagged).</summary>
    public bool Beams { get; set; } = true;

    public static readonly string[] Names = { "dynamics", "articulations", "slurs", "ties", "text", "repeats", "tuplets", "beams" };

    /// <summary>Turns off the named items ("dynamics,slurs"); unknown names are returned.</summary>
    public List<string> Omit(string list)
    {
        var unknown = new List<string>();
        foreach (var raw in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            switch (raw.ToLowerInvariant())
            {
                case "dynamics": Dynamics = false; break;
                case "articulations": Articulations = false; break;
                case "slurs": Slurs = false; break;
                case "ties": Ties = false; break;
                case "text": Text = false; break;
                case "repeats": Repeats = false; break;
                case "tuplets": TupletBrackets = false; break;
                case "beams": Beams = false; break;
                default: unknown.Add(raw); break;
            }
        return unknown;
    }

    public override string ToString()
    {
        var off = new List<string>();
        if (!Dynamics) off.Add("dynamics"); if (!Articulations) off.Add("articulations"); if (!Slurs) off.Add("slurs"); if (!Ties) off.Add("ties");
        if (!Text) off.Add("text"); if (!Repeats) off.Add("repeats"); if (!TupletBrackets) off.Add("tuplets"); if (!Beams) off.Add("beams");
        return off.Count == 0 ? "everything" : "without " + string.Join(", ", off);
    }
}

public static class MusicXmlWriter
{
    private static ExportOptions _o = new();

    public static void Write(Score score, string path, ExportOptions? options = null)
    {
        _o = options ?? new ExportOptions();
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
        int channel = 1;
        foreach (var p in score.Parts)
        {
            w.WriteStartElement("score-part");
            w.WriteAttributeString("id", p.Id);
            w.WriteElementString("part-name", p.Name);
            if (p.Abbreviation != null) w.WriteElementString("part-abbreviation", p.Abbreviation);
            if (p.Instrument != null)
            {
                // the sound for playback: MuseScore and others pick the instrument from the General MIDI program
                string iid = p.Id + "-I1";
                w.WriteStartElement("score-instrument"); w.WriteAttributeString("id", iid);
                w.WriteElementString("instrument-name", p.Instrument.Name);
                w.WriteEndElement();
                w.WriteStartElement("midi-instrument"); w.WriteAttributeString("id", iid);
                w.WriteElementString("midi-channel", channel.ToString());
                w.WriteElementString("midi-program", p.Instrument.Program.ToString());
                w.WriteEndElement();
                channel = channel % 16 + 1;
                if (channel == 10) channel = 11;   // channel 10 is percussion in General MIDI
            }
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

            if (_o.Repeats && (m.LeftBar != BarStyle.None || m.RepeatForward)) WriteBarline(w, "left", m.LeftBar, m.RepeatForward ? "forward" : null);

            // events by voice; directions are emitted before the event nearest to them (voice 1 pass)
            var voices = m.Events.GroupBy(e => e.Voice).OrderBy(g => g.Key).ToList();
            var directions = m.Directions.Where(d => d.Kind switch
            {
                DirectionKind.Dynamics or DirectionKind.WedgeStart or DirectionKind.WedgeStop => _o.Dynamics,
                _ => _o.Text,
            }).OrderBy(d => d.X).ToList();
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

            if (_o.Repeats && (m.RightBar != BarStyle.Regular || m.RepeatBackward)) WriteBarline(w, "right", m.RightBar, m.RepeatBackward ? "backward" : null);
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

    private static void WriteTimeModification(XmlWriter w, ScoreEvent e)
    {
        if (e.TupletActual <= 0 || e.TupletNormal <= 0) return;
        w.WriteStartElement("time-modification");
        w.WriteElementString("actual-notes", e.TupletActual.ToString());
        w.WriteElementString("normal-notes", e.TupletNormal.ToString());
        w.WriteEndElement();
    }

    private static void WriteTupletNotation(XmlWriter w, ScoreEvent e)
    {
        if (e.TupletStart) { w.WriteStartElement("tuplet"); w.WriteAttributeString("type", "start"); w.WriteAttributeString("bracket", "no"); w.WriteEndElement(); }
        if (e.TupletStop) { w.WriteStartElement("tuplet"); w.WriteAttributeString("type", "stop"); w.WriteEndElement(); }
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
            WriteTimeModification(w, e);
            if (staffCount > 1) w.WriteElementString("staff", (e.NotatedStaff ?? e.Staff).ToString());
            bool restFermata = e.Fermata && _o.Articulations, restTuplet = (e.TupletStart || e.TupletStop) && _o.TupletBrackets;
            if (restFermata || restTuplet)
            {
                w.WriteStartElement("notations");
                if (restTuplet) WriteTupletNotation(w, e);
                if (restFermata) w.WriteElementString("fermata", "");
                w.WriteEndElement();
            }
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
            bool tieStart = n.TieStart && _o.Ties, tieStop = n.TieStop && _o.Ties;
            if (tieStart) { w.WriteStartElement("tie"); w.WriteAttributeString("type", "start"); w.WriteEndElement(); }
            if (tieStop) { w.WriteStartElement("tie"); w.WriteAttributeString("type", "stop"); w.WriteEndElement(); }
            w.WriteElementString("voice", e.Voice.ToString());
            w.WriteElementString("type", e.Type);
            for (int i = 0; i < e.Dots; i++) w.WriteElementString("dot", "");
            if (n.AccidentalName != null) w.WriteElementString("accidental", n.AccidentalName);
            WriteTimeModification(w, e);
            if (e.StemUp.HasValue) w.WriteElementString("stem", e.StemUp.Value ? "up" : "down");
            if (staffCount > 1) w.WriteElementString("staff", (e.NotatedStaff ?? e.Staff).ToString());
            if (ni == 0 && _o.Beams)
                for (int b = 0; b < e.Beams.Length; b++)
                {
                    if (e.Beams[b] == BeamState.None) continue;
                    w.WriteStartElement("beam");
                    w.WriteAttributeString("number", (b + 1).ToString());
                    w.WriteString(e.Beams[b] switch { BeamState.Begin => "begin", BeamState.Continue => "continue", BeamState.End => "end", BeamState.ForwardHook => "forward hook", _ => "backward hook" });
                    w.WriteEndElement();
                }
            bool artic = _o.Articulations && (e.Articulations.Count > 0 || e.Fermata);
            bool slurs = _o.Slurs && (e.SlurStarts.Count > 0 || e.SlurStops.Count > 0);
            bool tuplet = _o.TupletBrackets && (e.TupletStart || e.TupletStop);
            bool notations = tieStart || tieStop || (ni == 0 && (artic || slurs || tuplet));
            if (notations)
            {
                w.WriteStartElement("notations");
                if (tieStart) { w.WriteStartElement("tied"); w.WriteAttributeString("type", "start"); w.WriteEndElement(); }
                if (tieStop) { w.WriteStartElement("tied"); w.WriteAttributeString("type", "stop"); w.WriteEndElement(); }
                if (ni == 0)
                {
                    if (tuplet) WriteTupletNotation(w, e);
                    if (slurs)
                    {
                        foreach (var s in e.SlurStops) { w.WriteStartElement("slur"); w.WriteAttributeString("type", "stop"); w.WriteAttributeString("number", s.ToString()); w.WriteEndElement(); }
                        foreach (var s in e.SlurStarts) { w.WriteStartElement("slur"); w.WriteAttributeString("type", "start"); w.WriteAttributeString("number", s.ToString()); w.WriteEndElement(); }
                    }
                    if (artic && e.Articulations.Count > 0)
                    {
                        w.WriteStartElement("articulations");
                        foreach (var a in e.Articulations) w.WriteElementString(a, "");
                        w.WriteEndElement();
                    }
                    if (artic && e.Fermata) w.WriteElementString("fermata", "");
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
    }
}
