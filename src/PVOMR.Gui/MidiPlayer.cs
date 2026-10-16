using System.Diagnostics;
using System.Runtime.InteropServices;
using PVOMR.Core.Score;

namespace PVOMR.Gui;

/// <summary>
/// Plays a recognised score on the Windows MIDI synthesizer, to hear what was read: every part on a channel of its
/// own with its instrument's program, transposing instruments at sounding pitch, voices in step, ties held. Played
/// from a measure (and a point in it) to the end of the music assembled so far.
/// </summary>
public sealed class MidiPlayer : IDisposable
{
    [DllImport("winmm.dll")] private static extern int midiOutOpen(out IntPtr handle, int deviceId, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] private static extern int midiOutShortMsg(IntPtr handle, int message);
    [DllImport("winmm.dll")] private static extern int midiOutReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int midiOutClose(IntPtr handle);

    private IntPtr _out;
    private CancellationTokenSource? _cts;

    public bool Playing => _cts != null;

    /// <summary>The measure (its index in the first part) now sounding; -1 when playback stops.</summary>
    public event Action<int>? MeasureChanged;

    private readonly record struct Message(double Ms, int Data, int Measure);

    /// <summary>Starts playing from measure index <paramref name="fromMeasure"/>, skipping what comes before
    /// <paramref name="fromOffset"/> (in divisions from the measure's start), at <paramref name="quarterPerMinute"/>.</summary>
    public void Play(Score score, int fromMeasure, int fromOffset, double quarterPerMinute)
    {
        Stop();
        var messages = Schedule(score, fromMeasure, fromOffset, quarterPerMinute);
        if (messages.Count == 0) return;
        if (_out == IntPtr.Zero && midiOutOpen(out _out, -1, IntPtr.Zero, IntPtr.Zero, 0) != 0) { _out = IntPtr.Zero; return; }
        var cts = new CancellationTokenSource();
        _cts = cts;
        var programs = score.Parts.Select((p, i) => (channel: Channel(i), program: Math.Clamp((p.Instrument?.Program ?? 1) - 1, 0, 127))).ToList();
        Task.Run(() =>
        {
            foreach (var (ch, prog) in programs) midiOutShortMsg(_out, 0xC0 | ch | (prog << 8));
            var sw = Stopwatch.StartNew();
            int measure = -1;
            foreach (var m in messages)
            {
                while (!cts.IsCancellationRequested && sw.Elapsed.TotalMilliseconds < m.Ms)
                {
                    double wait = m.Ms - sw.Elapsed.TotalMilliseconds;
                    if (wait > 2) Thread.Sleep((int)Math.Min(wait - 1, 20)); else Thread.SpinWait(50);
                }
                if (cts.IsCancellationRequested) break;
                if (m.Measure != measure) { measure = m.Measure; MeasureChanged?.Invoke(measure); }
                midiOutShortMsg(_out, m.Data);
            }
            midiOutReset(_out);
            if (_cts == cts) _cts = null;
            MeasureChanged?.Invoke(-1);
        });
    }

    public void Stop()
    {
        var cts = _cts;
        if (cts == null) return;
        cts.Cancel();
        _cts = null;
        if (_out != IntPtr.Zero) midiOutReset(_out);
    }

    public void Dispose()
    {
        Stop();
        if (_out != IntPtr.Zero) { midiOutReset(_out); midiOutClose(_out); _out = IntPtr.Zero; }
    }

    // channel 10 (index 9) is the drum kit
    private static int Channel(int part) { int c = part % 15; return c >= 9 ? c + 1 : c; }

    private static readonly Dictionary<string, int> StepBase = new() { ["C"] = 0, ["D"] = 2, ["E"] = 4, ["F"] = 5, ["G"] = 7, ["A"] = 9, ["B"] = 11 };

    private static List<Message> Schedule(Score score, int fromMeasure, int fromOffset, double qpm)
    {
        var list = new List<Message>();
        if (score.Parts.Count == 0) return list;
        double msPerDivision = 60000.0 / qpm / Score.Divisions;
        int count = score.Parts.Max(p => p.Measures.Count);
        // the measures one after the other: each as long as its time signature (or its longest voice)
        long start = 0;
        var measureStart = new long[count + 1];
        for (int i = 0; i < count; i++)
        {
            measureStart[i] = start;
            int length = 0;
            foreach (var p in score.Parts)
            {
                if (i >= p.Measures.Count) continue;
                var m = p.Measures[i];
                foreach (var v in m.Events.Where(e => !e.Grace).GroupBy(e => e.Voice)) length = Math.Max(length, v.Sum(e => e.Duration));
                if (m.Fit.Expected is int exp) length = Math.Max(length, exp);
            }
            start += Math.Max(length, 1);
        }
        measureStart[count] = start;
        long origin = (fromMeasure < count ? measureStart[fromMeasure] : 0) + fromOffset;
        for (int pi = 0; pi < score.Parts.Count; pi++)
        {
            var part = score.Parts[pi];
            int ch = Channel(pi);
            int shift = part.Transpose is { } t ? t.Chromatic + 12 * t.Octave : 0;
            var held = new HashSet<int>();   // pitches tied over into the next note
            for (int i = Math.Max(0, fromMeasure); i < part.Measures.Count; i++)
            {
                var m = part.Measures[i];
                foreach (var voice in m.Events.Where(e => !e.Grace).GroupBy(e => e.Voice))
                {
                    long t0 = measureStart[i];
                    foreach (var e in voice.OrderBy(e => e.X))
                    {
                        long on = t0, off = t0 + e.Duration;
                        t0 = off;
                        if (e.IsRest || on < origin) continue;
                        foreach (var n in e.Notes)
                        {
                            int key = Math.Clamp(12 * (n.Pitch.Octave + 1) + StepBase.GetValueOrDefault(n.Pitch.Step, 0) + n.Pitch.Alter + shift, 0, 127);
                            bool tiedIn = held.Remove(key) && n.TieStop;
                            if (!tiedIn) list.Add(new Message((on - origin) * msPerDivision, 0x90 | ch | (key << 8) | (80 << 16), i));
                            if (n.TieStart) held.Add(key);
                            else list.Add(new Message(Math.Max(0, (off - origin) * msPerDivision - 15), 0x80 | ch | (key << 8), i));
                        }
                    }
                }
            }
            // a tie into nothing: released at the end
            foreach (int key in held) list.Add(new Message((measureStart[count] - origin) * msPerDivision, 0x80 | ch | (key << 8), count - 1));
        }
        // note-offs before note-ons at the same moment (a repeated note sounds again)
        return list.OrderBy(m => m.Ms).ThenBy(m => (m.Data & 0xF0) == 0x90 ? 1 : 0).ToList();
    }
}
