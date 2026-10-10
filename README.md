# PVOMR – optical music recognition (analysis stage)

PVOMR is a C# (.NET 9) console application that analyses scanned or engraved sheet music.
It takes a PDF or image files, and for every page detects the staves, systems, bar lines and
the musical symbols inside each measure, then prints what it found. The long-term goal is a
complete PDF → MusicXML converter; this stage concentrates on the recognition itself.

```
pvomr <input.pdf | image ...> [options]

  --pages <spec>      pages to analyse for PDFs, e.g. 1-3,7 (default: all)
  --dpi <n>           render resolution for PDFs (default 300; raised automatically for tiny staves)
  --out <dir>         write report (.txt), JSON (.json) and debug overlays (.png) to this directory
  --debug             write debug overlay images (requires --out)
  --json              also write a JSON file with everything detected (requires --out)
  --musicxml <file>   assemble all analysed pages into a score and write MusicXML
  --grand-staff       force two-staff systems to be one piano part (default: automatic)
  --separate-staves   never merge staves into a grand staff
  --fonts <dir>       directory with SMuFL fonts (default: ./fonts or D:\OMR\fonts)
  --binarize <m>      auto | otsu | sauvola (default auto)
  --no-deskew         skip skew correction
  --quiet             only print the summary line per page
  --verbose           print analysis notes and bounding boxes
```

Example:

```
dotnet run --project src/PVOMR.Cli -c Release -- "D:\OMR\Scores\Bach - Cello Suite No 1 in G.pdf" --pages 1 --out out --debug
```

## MusicXML export

`--musicxml <file>` assembles all analysed pages into a score and writes MusicXML 4.0 (score-partwise).
The `Score` namespace holds a semantic model (parts, measures, voices, events with durations in
divisions, directions, slurs/ties), `MusicAssembler` builds it from the page analyses (staff-to-part
mapping, automatic grand staff for two-staff systems, voice assignment by stem direction, beams,
dots, accidentals, fermatas, articulations, dynamics, hairpins, slurs vs ties, barline styles and
repeats, measure numbering across systems and pages, pickup measures) and validates every voice
against the time signature: a missing time signature is inferred from the most common measure
length, and uncertain events (isolated note heads, unsure durations) are dropped when that makes a
too-long measure add up. Remaining inconsistencies are reported as warnings. `MusicXmlWriter`
writes the file; it opens in MuseScore 4 (`MuseScore4.exe -o out.pdf in.musicxml` renders it).

## What the report contains

For every page: image size, resolution, skew, binarisation method, staff space and line
thickness, the number of staves, systems, bar lines and measures. Then per system and staff:
the staff geometry, the detected clef, key signature and time signature, the bar lines (with
their kind: single, double, final, repeat, system start) and, per measure, every symbol sorted by
x position:

* notes and chords with pitch names (from clef, key signature and accidentals), duration
  (from head type, flags and beams), stem direction, dots and staccato
* beamed groups listed stem by stem
* rests, clefs, key signature accidentals, time signature digits, dynamics, articulations,
  fermatas, ornaments, slurs/ties, hairpins, dotted lines, text runs and unclassified ink
  (with the closest glyph and a size in staff spaces)

With `--debug` an overlay PNG per page shows the detected staff lines (red), bar lines (blue),
systems (green boxes), measure numbers and symbol boxes with labels; `--verbose` also writes the
staff-less binary image.

## How it works (src/PVOMR.Core)

1. **Input** (`Input/PageSource`): PDF pages are rasterised with PDFium (PDFtoImage); images are
   decoded with SkiaSharp (TIFF via LibTiff). Oddly sized PDF pages are rescaled to a sensible
   pixel size, and pages whose staff space is too small are re-rendered at a higher DPI.
2. **Pre-processing** (`Imaging/`): grey conversion, Otsu or Sauvola binarisation (chosen
   automatically; bilevel sources use a fixed threshold), dark scan borders removed, skew
   estimated from the projection profile and refined from the detected staff slope, 90° rotation
   fallback for sideways pages.
3. **Staff detection** (`Analysis/StaffDetector`): the page is cut into vertical strips; in each
   strip the horizontal projection of *thin* ink (short vertical runs, so beams, stems and note
   heads do not count) yields candidate line rows, grouped into five equidistant lines (one line
   may be missing). Groups are linked across strips into chains, fragments are merged, each line
   is refined at full resolution and stored as a polyline, so curved book scans work. Candidates
   are validated by line continuity, which rejects equally spaced text lines.
4. **Staff removal, bar lines, systems** (`StaffRemover`, `BarlineDetector`, `SystemBuilder`):
   lines are erased where no symbol crosses them; bar lines are thin vertical runs spanning the
   staff height that stay thin over their whole length (stems with heads, flags or beams are
   rejected); bar line pairs and repeat dots give the kind; staves are grouped into systems by
   spanning bar lines and by vertical connectors (brackets, braces, system bar lines) left of
   the staves; measure boundaries are the bar line positions shared by the system.
5. **Symbols** (`SymbolAnalyzer`, `GlyphLibrary`): connected components of the staff-less image
   are assigned to the nearest staff. Text lines outside the staff are grouped first. Components
   with stems are analysed structurally: note heads are found by scoring an elliptical template on
   both sides of every stem (filled and hollow), beams/flags are counted at the free stem end, and
   detached flags, augmentation dots, staccato dots and accidentals are attached afterwards.
   Everything else is classified by template matching against glyphs rendered from the SMuFL
   fonts in `D:\OMR\fonts` (Bravura, Leland, Gootville, Finale Maestro, MScore, Petaluma, Eugene,
   MuseJazz): the templates and the component are normalised to 32×32 bit masks and compared with
   a tolerant overlap measure, gated by the absolute size in staff spaces. Fragments that stay
   unknown are merged with close neighbours and matched again (F clef plus its dots, broken glyphs).
6. **Interpretation** (`MusicInterpreter`): clefs, key signatures (accidental runs at the start),
   time signatures and pitch names with per-measure accidental memory.

## Trained classifiers (models/)

Two small CNNs (PyTorch, exported to ONNX, run with ONNX Runtime) replace the weakest hand-written tests:

* `models/glyphs.onnx`: classifies an isolated component (40×40 ink grid plus width/height in staff spaces)
  into 26 classes (note head types, rest types, clefs, accidentals, digits, dot, dynamic, text, slur, line,
  beam, flag, fragment, composite, other). Used for isolated glyphs, fragment unions and the clef at the staff start.
* `models/heads.onnx`: verifies note-head candidates: a 2×2 staff-space window around a position beside a stem is
  classified as black head, half head or none. Windows are scanned along both sides of every stem in one batch,
  followed by non-maximum suppression and a geometric beam veto.

Training data comes from the synthetic pages: `pvomr gen` also writes MuseScore's SVG export, whose elements
(`Note`, `Rest`, `Clef`, `KeySig`, `Stem`, ...) give exact boxes; head and rest types are taken from the MusicXML
truth measure by measure. `pvomr crops --dir <gen dir>... --out crops` writes labelled component crops (plus glyphs
rendered from the SMuFL fonts with random degradations); `pvomr crops --heads ...` writes head windows and
negatives. `python tools/train_glyphs.py --crops crops --out models/glyphs.onnx` and
`python tools/train_glyphs.py --crops heads --out models/heads.onnx --labels head_black,head_half,none` train and
export (CPU, a few minutes each). `pvomr gen --font Bravura|Petaluma|...` renders with another engraving font.
`--model none` disables both models (template matching and the ellipse scorer are used instead).

Effect on the 12 test pieces (trained on 72 other pieces in Leland, Bravura and Petaluma):

| condition | measures exact | note P | note R | rest P/R |
|---|---|---|---|---|
| clean, templates only | 50% | 93.6% | 84.3% | 84% / 85% |
| clean, both models | 77% | 96.2% | 96.0% | 83% / 85% |
| degraded, templates only | 22% | 84.7% | 74.8% | 64% / 50% |
| degraded, both models | 45% | 81.6% | 85.1% | 78% / 82% |

## Ground truth and metrics

`pvomr gen --out <dir> [--count 12] [--seed 1] [--measures 16] [--piano|--single]` generates random pieces
with the score model (single staves in treble/bass/alto clef and two-staff piano, various keys and
time signatures, chords, rests, dots, accidentals, dynamics, slurs), writes them as MusicXML and has
MuseScore 4 render them to PDF, so every rendered page has an exact ground truth.

`pvomr eval --dir <dir> [--augment <spec>] [--details] [--debug] [--out report.txt]` recognises each
piece, assembles the score and compares it with the truth (`Eval/ScoreComparer`): measures are
aligned (by index, or by similarity when the counts differ), events within a measure are aligned
with a sequence alignment, and the table reports measures found/true and the share of measures
that are entirely right, note precision/recall (pitch), duration accuracy of the matched notes,
rest precision/recall, and whether clef, key and time signature of the first measure are right.
`--details` lists the mismatches per measure. `pvomr eval <input> <truth.musicxml>` works for a
single real score with a known transcription.

`--augment` (also available for normal analysis) degrades the rendered page before recognition:
`blur=1.2,noise=0.08,skew=1.5,scale=0.6,contrast=0.6,speckle=0.002,thin=1,thick=1`.

Baseline (12 generated pieces, 192 measures), 2026-10-10:

| condition | measures found | measures exact | note P | note R | duration | rest P/R | clef/key/time |
|---|---|---|---|---|---|---|---|
| clean MuseScore render (first baseline) | 207/192 | 35% | 82.7% | 80.3% | 97% | 79% / 80% | 12 / 11 / 12 |
| degraded (blur 1.0, noise 0.06, skew 0.8°, scale 0.7), first baseline | 208/192 | 11% | 64.2% | 66.2% | 87% | 64% / 50% | 12 / 5 / 8 |
| clean, after fragment reuniting / stem re-attachment / barline fixes | 192/192 | 50% | 93.6% | 84.3% | 99% | 84% / 85% | 12 / 11 / 11 |
| degraded, same | 198/192 | 22% | 84.7% | 74.8% | 96% | 64% / 50% | 12 / 7 / 9 |

## Status and known limitations

* Staff/system/bar line detection is robust on all tested material (clean parts, old engravings,
  book scans with curvature, dense piano music, full scores, noisy copies, sideways pages).
* Symbol recognition is heuristic and is the area to improve next: template matching confuses
  similar glyphs in old engravings (e.g. flags vs clef fragments, breath marks vs 8th rests, text
  letters vs dynamics), chord heads next to beams can be missed, key signatures are sometimes
  read as note accidentals, and durations of beamed notes depend on beam counting at the stem.
* Lyrics, tuplet numbers, fingering and most text are reported only as "text".
* Scans that were upscaled from a low resolution (doubled staff lines, e.g. "Full Scores/Dvorak -
  Symphony No. 8.pdf") still lose some staves.

## Diagnostics

* `--diag` prints, per staff, the modelled line positions against the actual ink runs at three x positions.
* `PVOMR_STRIPDUMP=<strip index>` (with `--verbose`) prints the candidate line segments and 5-line groups of
  one detection strip; `PVOMR_ROWS=<y0>-<y1>` additionally prints the thin-ink row projection of that strip.
* `--verbose --debug --out dir` also writes the staff-less binary image (`*_nostaff.png`).
* The models were trained on MuseScore renders only; on old engravings they occasionally see heads in beam ends
  (vetoed geometrically) or rests in text dashes (size/position checks). Real labelled pages would fix this at the source.
* Rhythm reconstruction is per voice and measure; tuplets, grace notes and cross-staff beaming are
  not handled, lyrics are not exported, and slur endpoints are only matched within one measure.
