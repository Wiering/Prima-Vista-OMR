"""Convert a simple monophonic LilyPond \\relative block (as in Mutopia's BWV 1007 prelude) into a MusicXML truth file.

usage: python tools/lilypond_to_truth.py <file.ly> <out.musicxml> --measures 1-19 --clef F --key 1 --time 4/4

Supports: relative pitch (c d e f g a b with is/es, ' and ,), durations with dots, ties (~), rests (r), slurs and
articulations are ignored, bar lines '|', comments. Chords and polyphony (<< >>) are not supported: stop before them.
"""
import argparse, re, sys

STEPS = "cdefgab"
DIV = 4  # divisions per quarter


def parse(text, start_octave=4):
    # strip comments and whole command lines (	ime, \key, \set ...), keep note lines
    text = re.sub(r"%.*", "", text)
    body = text[text.index("{") + 1:]
    body = chr(10).join(l for l in body.splitlines() if not l.strip().startswith(chr(92)))
    body = body.replace("!", "").replace("?", "")   # forced / cautionary accidental marks do not change the pitch
    tokens = re.findall(r"<<|>>|\\[a-zA-Z]+|\"[^\"]*\"|[a-g](?:is|es)*[',]*\d*\.*~?|r\d*\.*|s\d*\.*|\||[()\[\]]|[^\s]", body)
    notes, measures, cur = [], [], []
    prev_step, prev_oct = 0, start_octave  # relative to c'
    dur = 4  # quarter
    dots = 0
    tie_pending = False
    for t in tokens:
        if t in ("<<", ">>"):
            raise SystemExit("polyphony reached; reduce --measures")
        if t == "|":
            measures.append(cur); cur = []
            continue
        if t.startswith("\\") or t in "()[]" or t.startswith('"'):
            continue
        m = re.match(r"([a-g])((?:is|es)*)([',]*)(\d*)(\.*)(~?)$", t)
        if m:
            step = STEPS.index(m.group(1)); acc = m.group(2); mods = m.group(3)
            if m.group(4): dur = int(m.group(4)); dots = len(m.group(5))
            # relative octave: nearest within a fourth
            diff = step - prev_step
            octv = prev_oct
            if diff > 3: octv -= 1
            elif diff < -3: octv += 1
            octv += mods.count("'") - mods.count(",")
            alter = acc.count("is") - acc.count("es")
            ev = {"step": STEPS[step].upper(), "alter": alter, "octave": octv, "dur": dur, "dots": dots, "tie_stop": tie_pending, "tie_start": m.group(6) == "~"}
            tie_pending = ev["tie_start"]
            cur.append(ev); prev_step, prev_oct = step, octv
            continue
        m = re.match(r"([rs])(\d*)(\.*)$", t)
        if m:
            if m.group(2): dur = int(m.group(2)); dots = len(m.group(3))
            cur.append({"rest": True, "dur": dur, "dots": dots, "spacer": m.group(1) == "s"})
            continue
    if cur: measures.append(cur)
    return measures


def duration_divs(dur, dots):
    d = DIV * 4 // dur
    total, add = d, d
    for _ in range(dots):
        add //= 2; total += add
    return total


TYPES = {1: "whole", 2: "half", 4: "quarter", 8: "eighth", 16: "16th", 32: "32nd"}


def write(measures, out, clef, key, time):
    beats, beat_type = time.split("/")
    sign, line = (clef, 4) if clef == "F" else (clef, 2) if clef == "G" else ("C", 3)
    lines = ['<?xml version="1.0" encoding="UTF-8"?>',
             '<!DOCTYPE score-partwise PUBLIC "-//Recordare//DTD MusicXML 4.0 Partwise//EN" "http://www.musicxml.org/dtds/partwise.dtd">',
             '<score-partwise version="4.0"><part-list><score-part id="P1"><part-name>Part</part-name></score-part></part-list><part id="P1">']
    for i, m in enumerate(measures):
        lines.append(f'<measure number="{i + 1}">')
        if i == 0:
            lines.append(f'<attributes><divisions>{DIV}</divisions><key><fifths>{key}</fifths></key><time><beats>{beats}</beats><beat-type>{beat_type}</beat-type></time><clef><sign>{sign}</sign><line>{line}</line></clef></attributes>')
        for ev in m:
            if ev.get("spacer"):
                lines.append(f'<forward><duration>{duration_divs(ev["dur"], ev["dots"])}</duration></forward>'); continue
            d = duration_divs(ev["dur"], ev["dots"])
            if ev.get("rest"):
                lines.append(f'<note><rest/><duration>{d}</duration><voice>1</voice><type>{TYPES[ev["dur"]]}</type>{"<dot/>" * ev["dots"]}</note>')
            else:
                alter = f'<alter>{ev["alter"]}</alter>' if ev["alter"] else ""
                ties = ('<tie type="stop"/>' if ev["tie_stop"] else "") + ('<tie type="start"/>' if ev["tie_start"] else "")
                lines.append(f'<note><pitch><step>{ev["step"]}</step>{alter}<octave>{ev["octave"]}</octave></pitch><duration>{d}</duration>{ties}<voice>1</voice><type>{TYPES[ev["dur"]]}</type>{"<dot/>" * ev["dots"]}</note>')
        lines.append('</measure>')
    lines.append('</part></score-partwise>')
    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("ly"); ap.add_argument("out")
    ap.add_argument("--measures", default=None, help="range like 1-19")
    ap.add_argument("--clef", default="G"); ap.add_argument("--key", type=int, default=0); ap.add_argument("--time", default="4/4")
    ap.add_argument("--start-octave", type=int, default=4, help="octave of c' (4 for \\relative c')")
    args = ap.parse_args()
    text = open(args.ly, encoding="utf-8").read()
    # cut before polyphony
    cut = text.find("<<")
    if cut > 0: text = text[:cut] + "}"
    measures = parse(text, args.start_octave)
    if args.measures:
        a, b = args.measures.split("-"); measures = measures[int(a) - 1:int(b)]
    write(measures, args.out, args.clef, args.key, args.time)
    print(f"{len(measures)} measures, {sum(len(m) for m in measures)} events -> {args.out}")
    for i, m in enumerate(measures[:2]):
        print(f"  m{i + 1}:", " ".join(f'{e["step"]}{"#" if e.get("alter") == 1 else "b" if e.get("alter") == -1 else ""}{e["octave"]}' if not e.get("rest") else "R" for e in m))


if __name__ == "__main__":
    main()
