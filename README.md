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
mapping, automatic grand staves, voice assignment by stem direction, beams,
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
   are validated by line continuity, which rejects equally spaced text lines. Stacks of ledger lines are
   not staves: a short candidate near a longer staff is dropped, and so is a wider one (up to 85 % of the
   longer staff) hanging about one space below or above it, as under the very low notes of a four-hand secondo.
4. **Staff removal, bar lines, systems** (`StaffRemover`, `BarlineDetector`, `SystemBuilder`):
   lines are erased where no symbol crosses them: a vertical ink run through the line up to one and a half
   times its thickness is the line itself, a run up to twice the thickness is erased only when its neighbouring
   columns are not thick as well (a beam or a rest lying along the line survives, the line's wobble does not),
   and thicker runs are symbols; bar lines are thin vertical runs spanning the
   staff height that stay thin over their whole length (stems with heads, flags or beams are
   rejected: a run whose end is touched by a head-sized blob is a stem, and a run that stops inside the gap
   to the next staff while its component goes on is a spanning bar line, not an end to test); bar line pairs
   and repeat dots give the kind; staves are grouped into systems by
   spanning bar lines and by vertical connectors (brackets, braces, system bar lines) left of
   the staves; measure boundaries are the bar line positions shared by the system.
5. **Symbols** (`SymbolAnalyzer`, `GlyphLibrary`): connected components of the staff-less image
   are assigned to the nearest staff. Text lines outside the staff are grouped first. Components
   with stems are analysed structurally: note heads are found by scoring an elliptical template on
   both sides of every stem (filled and hollow), beams/flags are counted at the free stem end, and
   detached flags, augmentation dots, staccato dots and accidentals are attached afterwards.
   A beamed group is then repaired with the knowledge that all its stems end at one (sloping) beam: a stem that
   stops short of the fitted beam line lost its lower beams to a gap in the ink and takes the group's majority
   beam count; two thin head-less strokes right before a head are a sharp (or, with less vertical overlap, a
   natural) whose cross strokes must not count as beams; a "head" found at the beam end of a stem that also has
   a normal head is the beam's end blob, not a chord member; a wide gap between stems is searched for a head whose
   stem broke off; a stem without any head is searched again a little further out; and a stemless black head
   lying inside a group's span joins the group. Two neighbouring stems that extend to the same side of a shared
   head (a flag's vertical part, a split thick stem) are merged; a hollow "head" near the beam line is the curl of
   a flag or the thick end of a beam and is dropped, as is a stem whose heads are all hollow inside a group of
   black heads (a sharp or natural taken for a note); a head candidate that is not part of the stem's component
   needs a very confident detector. A hollow head with beams found with little confidence is dropped (ledger
   lines around a stem), heads outside the staff need a ledger line on every path (classifier, hollow-head shape
   test and template matching; the letters of "dolce" or "cresc." under a staff were read as low half notes), a
   hollow-looking blob the classifier confidently calls a digit or text is no stemless head (fingerings), whole and half rests must be solid
   bars at least 0.4 spaces thick inside the staff (slurs sliced by the staff lines and ledger remnants are not),
   and different digits over one beamed group are fingerings rather than tuplet numbers. Beams are counted only
   from bands at least 0.3 spaces thick (ledger lines crossing a stem are thinner); on small staves a rest-shaped
   component the classifier calls a fragment is still template matched, and a solid bar hanging from or sitting on
   a staff line is a whole or half rest even when the classifier is unsure. Flagged rests must be tall (an 8th rest
   spans two spaces, a quarter rest nearly three), augmentation and staccato dots never sit on a staff line (a blob
   on a line is a line remnant), a chord gets one dot per head with double dots only side by side, a quarter note
   whose stem end lies on a neighbour's beam with the beam ink reaching its stem takes that beam count, and thin
   flag spines or blobs next to a stem end reattach as flags. Hollow heads that broke off a shared stem are
   reattached as one chord, and a stemless head that coincides with a head of a stemmed note is the same head
   seen twice and is dropped. Voices on a staff: an up-stem and a down-stem note at the same x start two voices,
   split by stem direction (rests by their height); when the measure length is known it arbitrates among a few
   candidate splits (single voice, by direction with the rests on either side, flipped, long notes against short
   ones) and the one whose main voice fills the measure without any voice overflowing wins; a split that keeps every
   note beats one that only fits after dropping uncertain notes, and a stemless whole or half note struck together
   with a note of the moving line (a held open-string double stop) may form the second voice on its own. A second voice may
   stop early, as a held bass note under a moving line has no written rests; the duration check accepts that and
   flags only an overflowing voice or a main voice that does not fill the measure. Cross-staff beaming (piano arpeggios running from one staff into the other under
   one beam) is handled in two ways: a head found at the far end of a stem inside the neighbouring staff of the
   system is accepted as a cross-staff head, and when one beam carries heads on both sides of it, the heads two
   spaces or more beyond this staff on the far side belong to the neighbouring staff. Such heads keep their place
   in the beamed voice but take their pitch from the other staff's clef and key and are written on that staff in
   the MusicXML. A dot beside a head shared by two voices belongs to the longer note, and a quarter note inside a
   beamed group whose stem points the other way is the second voice, not a beam-count miss.
   Everything else is classified by template matching against glyphs rendered from the SMuFL
   fonts in `D:\OMR\fonts` (Bravura, Leland, Gootville, Finale Maestro, MScore, Petaluma, Eugene,
   MuseJazz): the templates and the component are normalised to 32×32 bit masks and compared with
   a tolerant overlap measure, gated by the absolute size in staff spaces. Fragments that stay
   unknown are merged with close neighbours and matched again (F clef plus its dots, broken glyphs).
   Clefs, key signatures and time signatures at the start of a staff get a dedicated pass that works on the
   *unremoved* binary image: on small staves (15 px staff space, thick lines) staff removal shreds the glyphs,
   so instead the font templates are placed at the position the staff dictates (G clef on line 2, the n-th
   flat/sharp of the key signature at its fixed step for the clef, time-signature digits in the upper and
   lower half) and scored by tolerant overlap with the staff-line rows and bar-line/bracket columns treated
   as unknown. The key signature grows accidental by accidental until a placement scores too low, is explained
   better by a time-signature digit, or breaks the even spacing; the time signature is accepted only when
   both digits (or the common/cut glyph) score very high and no stem crosses the digit's edge. Clefs are first
   assembled from components and fall back to the same template placement (with stretched C clefs for old
   editions). The placement pass runs for staves up to 40 px staff space: larger glyphs survive staff removal,
   and the spurious "staves" found on text pages (spacings of 100 px and more) would make it take hours.
6. **Interpretation** (`MusicInterpreter`): clefs, key signatures (accidental runs at the start),
   time signatures and pitch names with per-measure accidental memory; cross-staff heads are pitched on the
   staff they lie in. Within a system, staves without a key
   or time signature adopt the majority reading (the time only when at least a quarter of the staves have one);
   across the systems of a page, a staff position whose key signature lost accidentals on one system (same
   kind, fewer of them) is completed to the fuller reading. Partial time readings ("7/?") are discarded, and
   digits that give no valid signature do not cancel an inherited one. When pages are analysed in order (the
   CLI with a page range, the GUI when the previous page is already recognised), the last system of the previous
   page seeds clef, key and time of the first system, so continuation pages without a time signature are read
   against the right measure length; the `--fit` report assembles the pages analysed so far for the same reason.
   The assembler then checks every time signature against the music under it: when fewer than a fifth of the
   measures of a page fit it while at least 30% agree on another plausible length (twice as many as fit the
   reading), the signature was misread and is replaced by the agreed length, with a warning; a page without any
   signature gets the length 40% of its measures agree on. A C or cut-C glyph read on the same page is trusted and
   never replaced, but a value carried from an earlier page is open to replacement (the Shostakovich violin part
   turns from the 4/4 Nocturne to the 3/8 Scherzo without a new signature being read). This repaired the pages that
   failed on every measure in the corpus run (a "5/4" over 3/8 music, a "1/4" carried along a whole cello part, an
   "8/4"; the Mozart Divertimento cello part went from 1% to 93%, the Shostakovich Scherzo pages from 2% to 50%).
   At the interpreter level a numerator of 1 is rejected outright: no metre has a single beat, so a "1" above the
   line is a misread digit.

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
`pvomr crops --dpi 200` renders the training pages smaller (13 to 14 px staff space, the size of full-score staves)
so the models also see small glyphs. `--model none` disables both models (template matching and the ellipse
scorer are used instead).

Retraining 2026-10-11 (145 training pieces: the 72 original, 45 hard-style and 28 tuplet-style ones, crops at 300
and 200 dpi, 84,000 glyph crops and 114,000 head windows): the new glyph model reads the 15 px Beethoven staves
clearly better (page 7: 88% -> 94% of measures adding up) at a small cost on the synthetic sets (clean note
precision 99.5% -> 99.2%, hard 90.8% -> 90.4%); it is the shipped `models/glyphs.onnx`. The retrained head verifier
lost ground on the real pages (Bach page 2: 92% -> 85% exact, Gounod 46% -> 41%) and was not adopted; the old
`models/heads.onnx` stays.

Effect on the 12 test pieces (trained on 72 other pieces in Leland, Bravura and Petaluma):

| condition | measures exact | note P | note R | rest P/R |
|---|---|---|---|---|
| clean, templates only | 50% | 93.6% | 84.3% | 84% / 85% |
| clean, both models | 77% | 96.2% | 96.0% | 83% / 85% |
| clean, both models, after the real-page fixes | 80% | 96.4% | 96.8% | 83% / 85% |
| degraded, templates only | 22% | 84.7% | 74.8% | 64% / 50% |
| degraded, both models | 45% | 81.6% | 85.1% | 78% / 82% |
| clean, both models, after the small-staff key/time pass | 86% | 98.6% | 98.9% | 84% / 85% |
| degraded, both models, after the small-staff key/time pass | 68% | 91.7% | 97.3% | 80% / 81% |
| degraded, both models, after the beamed-group repairs | 68% | 91.8% | 97.3% | 80% / 81% |
| clean, both models, after the second round of group repairs | 88% | 99.2% | 98.9% | 85% / 85% |
| degraded, both models, after the second round of group repairs | 78% | 97.3% | 97.1% | 81% / 81% |

| clean, both models, after the staff-remover and piano-page round | 89% | 99.5% | 99.1% | 85% / 85% |
| degraded, both models, after the staff-remover and piano-page round | 81% | 97.9% | 97.6% | 82% / 83% |
| clean, retrained glyph model, after the piano bar-line round | 89% | 99.5% | 99.1% | 85% / 85% |
| degraded, retrained glyph model, after the piano bar-line round | 81% | 98.6% | 98.2% | 83% / 83% |

Hard set after these rounds: 192/192 measures, 69% exact, note P 93.5% / R 93.7% (the time-arbitrated voice split
alone brought it from 92.5% / 92.7%); tuplet set 99.8% / 99.8%.

The key/time pass helps the synthetic pages too because a misread key signature changes every pitch of a staff:
keys and time signatures are now right on all 12 clean, degraded and hard pieces and the 8 tuplet pieces
(hard set: 190/192 measures, note P 89.2% / R 90.6%; tuplets: P 99.0% / R 99.2%).

## Full scores over several pages

`--omit dynamics,slurs` (also articulations, ties, text, repeats, tuplets, beams) leaves those out of the MusicXML
while the notes and rests stay; without dynamics the playback in MuseScore keeps one volume, which makes wrong
soft notes audible. The GUI asks the same question in a dialog before saving and remembers the answer.
`--pages 7-9 --musicxml out.musicxml` assembles all analysed pages into one score with continuous measure numbers
and `new-page`/`new-system` marks. Parts are the staves of the fullest system; a system with fewer staves
(instruments omitted) is aligned to it monotonically by clef kind, and a part absent from a system receives
whole-measure rests so every part keeps the same measure count. Within a system all staves share the majority
time signature and the key signature read on the other staves. Beethoven 5 pages 7–9 export as 12 parts with
67 measures each in 2/4 and E♭ major (the clarinets in B♭ correctly with one flat) and open in MuseScore; the
Debussy Petite Suite score, whose systems have 2 to 12 staves, exports as 12 parts of 35 measures. On the
15 px staves of these scores the clefs (including alto and tenor), key signatures and time signatures are
read by the template-placement pass described above; Beethoven 3 page 2 (jagged bilevel scan), Brahms 4 page 2
and the Dover Bach Suite 3 page 3 read the right key on every staff but one. Instrument names are not read
(see "Instrument names" below).

Grand staves: the system builder looks for a curly brace left of every pair of adjacent staves. Per row the ink
nearest to the staves is traced over the whole stroke (which may overshoot the staves): a brace has its tips to
the right of its middle cusp and its arms bulge out to the right of the thin middle, while a bracket or system
line is straight and a stroke that runs on past the pair belongs to a bracket over more staves
(`PVOMR_BRACEDUMP=1` prints the measurements). A braced pair becomes one two-staff part (named Piano) when it
is keyboard-like: any braced pair in a system of up to four staves (violin or clarinet and piano, songs, four-hand
piano with both players on one page), but in larger systems only treble over bass, because orchestral editions
also brace cellos and basses. A pair counts as braced when any system with the full staff count shows the brace,
so one broken brace stroke does not split the part. Scores of two-staff systems keep the earlier rule (one piano
part when the clefs look like a grand staff or a brace is seen); `--grand-staff` and `--separate-staves` force
either way. Cross-staff notes and voices then work as for solo piano, and the measure-fit check counts the two
staves of a piano measure separately (notes dropped on one staff no longer mark the other one).

## Instrument names

The instrument names printed left of the staves are read with the OCR engine built into Windows 10/11
(`src/PVOMR.Ocr`, `Windows.Media.Ocr`; nothing to download, it uses an installed OCR language, English
preferred). The core library only defines the `ITextRecognizer` interface and stays platform-neutral; the
command line and the GUI therefore target `net9.0-windows10.0.19041.0` (the executable is now under
`bin/Release/net9.0-windows10.0.19041.0/`). `--no-ocr` switches it off.

* Per system the margin left of the staves is read twice (enlarged to about 25 and 35 px per staff space;
  each size wins on different print) and every text line goes to the staff whose middle is nearest; a name
  between the staves of a braced pair names the pair. Staves still without a name are read once more on
  their own (short abbreviations are often skipped in a large crop). Per staff the reading that names an
  instrument wins.
* The text above the first system is read too (`PageAnalysis.HeaderText`); a single part without a name in
  the margin takes an instrument named in a short header line ("CELLO", "Violini I").
* `Score/Instruments` knows the usual names, plurals and abbreviations in Italian, German, French, English and
  Dutch (Flauti, Hautbois, Klarinette, Fagotti, Corni, Trombe, Posaunen, Pauken, Arpa, Violini, Bratsche,
  Violoncello, C.-Bassi, Soprano ... Basso), skips "in B", numerals and "e"/"und", takes the first instrument
  word ("Violoncello e Basso" is a cello part, "Trombone basso e Tuba" a trombone part) and tolerates OCR slips
  by edit distance ("Tronube" is a trumpet). "Basso" is the bass voice when the other parts are voices.
* Parts get the printed full name (the longest reading, OCR slips replaced by the instrument's name plus its
  numeral), a short abbreviation when later systems print one, and a MusicXML `score-instrument` and
  `midi-instrument` with the General MIDI program, so MuseScore plays a violin part as a violin.
* Systems that leave out instruments are aligned to the full system by name where names were read, and by
  clef otherwise; a named staff is not aligned to an unnamed reference staff while the reference names that
  instrument elsewhere (Debussy Petite Suite: the harp no longer lands on the clarinet part).
* Results: Beethoven 5, Mozart 34 and the Tchaikovsky Swan Lake suite get nearly all parts named with the
  right sound; Debussy's small italic abbreviations ("Htb", "Fl.") are only partly readable by the engine.
  `PVOMR_OCRDUMP=1` (with `--verbose`) prints every OCR line and the staff readings; `PVOMR_OCRSAVE=<dir>`
  writes the crops handed to the engine as PGM images.

## Correction GUI (src/PVOMR.Gui)

`dotnet run --project src/PVOMR.Gui -c Release -- "score.pdf"` (or start `pvomr-gui.exe`, optionally with a file)
opens an Avalonia desktop application for checking and correcting the recognition before export:

* the deskewed page is shown with the detected staff lines, bar lines, measure numbers and a coloured box per
  symbol (green notes, orange rests, purple clefs, blue key signatures, magenta time signatures, red unknown);
  the mouse wheel zooms around the pointer, dragging pans, Shift/Ctrl + wheel scroll, and the Fit / Fit width /
  100% buttons reset the view; the overlays can be switched off individually;
* clicking selects the smallest symbol under the pointer (or the staff when there is none); the inspector shows the
  symbol's description, kind, duration, dots, stem direction and its note heads, which can be moved up or down a
  step or removed; a symbol can be deleted; the "Symbols in this measure" list navigates within the measure;
* a staff's clef, key signature and time signature can be forced ("Apply to staff") when the automatic reading is
  wrong; the overrides are stored on the staff (`ForcedClef`, `ForcedKey`, `ForcedTime`) and honoured by the
  interpreter, including the system-wide key and time sharing;
* the toolbar modes "Add note" and "Add rest" insert a quarter note (at the clicked staff step) or a quarter rest
  into the measure under the pointer; "Move" drags the selected symbol, its note steps follow and it changes
  measure when it crosses a bar line; "Select" is the normal mode;
* every edit re-runs the musical interpretation of the page (pitch names, keys, times), so the labels update at
  once; "Export MusicXML…" assembles all pages recognised in this session (pages are analysed when first shown
  and cached); the export dialog lets you leave out dynamics and hairpins, articulations, slurs, ties, text,
  repeat signs, tuplet brackets or beams (the notes and rests always go), and remembers the choice in
  `%APPDATA%\PVOMR\export.json`. User-added or corrected notes are never dropped by the measure-fitting step of the
  assembler;
* the duration check tints every measure whose recognised durations do not add up to the time signature (red:
  too long, orange: too short, purple: the assembler had to drop uncertain notes to make it fit) and lists them
  under "Measures to check"; clicking an entry jumps to the measure. The check is the assembler's own validation,
  run over all pages recognised so far, so it is exactly what the export would do;
* Undo (Ctrl+Z) takes back the last correction of the page (the page is recognised again and the remaining log
  replayed); Delete removes the selected symbol, the arrow keys move a single note head a step, Page Up/Down turn
  pages, Escape returns to Select mode; "Recognise all pages" analyses the remaining pages so the export covers
  the whole file;
* corrections are saved automatically to `<score>.pvomr.json` next to the score as an edit log (staff overrides
  plus one entry per edit: delete, set, head, removeHead, add, move) and replayed whenever a page is recognised
  again, including in a later session; a symbol is identified by its staff and the bounds it had at recognition
  (nearest box with IoU ≥ 0.6 when the recognition shifted slightly). "Discard this page's corrections" removes
  the page's log entries and recognises the page afresh.

## Tuplets

Triplets (and other tuplets) are supported end to end: the generator (`--style tuplets`, also part of `hard`)
writes 8th-note triplets with `time-modification`, the score model, writer and reader carry the tuplet ratio and
start/stop notations, the recogniser finds the small tuplet digit above or below a beamed group (tuplet digit
templates; the models were not trained on them, so the template test runs even when the glyph model says
"fragment") and the assembler applies the ratio to the run of notes centred under the digit, so measures add up
again. A digit only counts as a tuplet when it sits on the beam side of the group and divides its note count, which
keeps the fingering numbers of the Bach edition from becoming triplets. Tuplet test set (8 pieces,
`--style tuplets`, seed 5): 128/128 measures, note P 98.4% / R 98.6%, durations 94% with strict duration
comparison, 84% of measures entirely right. Rests recognised twice from a split glyph are merged.

## Harder synthetic set and real pages

`pvomr gen --style hard` adds dense beamed rhythms (8ths/16ths), a second voice with stems down, ties, more
chords and accidentals; `dense` and `voices` select one of the two. `testdata/` holds exact MusicXML truth for two
real scanned pages, derived from the public-domain LilyPond source of BWV 1007 (Mutopia) with
`tools/lilypond_to_truth.py` (a small parser for monophonic `
elative` LilyPond; it stops before polyphony):

```
pvomr eval "D:\OMR\Scores\Bach - Cello Suite No 1 in G.pdf" testdata/bach-bwv1007-prelude-m1-19.musicxml --pages 1
# 2026-10-11: page 1 19/19 measures exact, notes P 100% R 100%; page 2 (first 13 measures) 92% exact, P/R 99.5%
pvomr eval "D:\OMR\Scores\Bach - Cello Suite No 1 in G.pdf" testdata/bach-bwv1007-prelude-m20-32.musicxml --pages 2 --measures 1-13
```

Results (both models, 2026-10-10, after the fixes driven by these pages): page 1 (19 measures) note P 98.7% / R 100%,
79% of measures entirely right; page 2 measures 20–32: note P 96.6% / R 97.6%, durations 99.5%, 62% of measures
entirely right (before the fixes: 93.6% / 92.2%). The fixes: the beam veto for head candidates now requires beam-like
thickness per column (it had been triggered by accidentals and ledger lines next to the head), heads outside the
staff must have a ledger line (text letters below the staff were read as low notes), head centres are refined by
a probability-weighted mean (ledger-line pitches off by one), a geometric fallback when the verifier finds no head
at a stem, and a stricter "simultaneous" test for two voices. Remaining errors: a few heads next to accidentals,
accidentals not attached to their note, and isolated false heads in dense text. Hard synthetic set (12 pieces,
`--style hard`, seed 3): 190/192 measures, note P 86.9% / R 88.2% clean. Retraining the models with 45 hard-style
pieces added did not move these numbers; the gap is structural (two voices, dense beams).

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
  letters vs dynamics), chord heads next to beams can be missed, an accidental right after the key
  signature at the next key-signature position is taken as part of it, and durations of beamed notes
  depend on beam counting at the stem.
* Lyrics, tuplet numbers, fingering and most text are reported only as "text".
* Scans that were upscaled from a low resolution (doubled staff lines, e.g. "Full Scores/Dvorak -
  Symphony No. 8.pdf") still lose some staves.

## Diagnostics

* The corpus run of 2026-10-12 (first six pages of each of the 416 unique files, 2267 pages) finished without a
  crash or timeout; the median page took 7.6 s and the slowest 53 s. Over the 1856 pages with at least eight
  measures the median share of measures adding up was 56% (full scores 65%, chamber music 67%, parts 56%, piano
  arrangements 49%, vocal scores 39%, the "Challenges" folder 29%), before the time-signature sanity check.
* `python tools/corpus_run.py --root D:\OMR\Scores [--max-pages 6] [--report]` runs the recogniser over every
  PDF under a folder (the first pages of each, with a time limit per page) and keeps a manifest
  (`<root>/.pvomr-corpus.json`) keyed by the MD5 of each file, so duplicate files under other names are processed
  once and a later run only adds what is new; `--report` prints the ranking of the worst and slowest pages.
* `--fit` prints per page how many measures add up to their time signature (the assembler's own check) and, with
  `--verbose`, lists the measures that do not with their summed durations. It needs no ground truth, so it ranks
  real pages by recognition quality (for a two-staff part each staff is checked on its own): Bach Cello Suite No 1
  pages 3 to 6 read 46%, 82%, 69% and 75% of measures adding up (the Prelude pages 1 and 2 with ground truth:
  100% and 92% exact), Beethoven 5 pages 7 to 9 (15 px staves) 94%, 84% and 81%, Bartok "For Children" page 3
  (thick-lined piano scan) 92%, Brahms 4 page 2 72%. A scan of page 2 of the 63 scores added on 2026-10-11 ran without a failure;
  the weakest pages were solo piano: the Gounod Pastorale read 49% because the bar lines of the upper piano staff
  were rejected (the run scan stopped inside the gap to the lower staff and found an articulation mark there) and
  a chord's stem was taken for a repeat bar line; with both fixed it reads 81%. With the pages in sequence, Bach
  suite page 6 (no time signature after its first system) is checked in full: 48 measures, 69% adding up, instead
  of 24 checked and 12 unknown. Debussy's Clair de lune page 2 (cross-staff arpeggios under one beam, a dotted
  bass note sharing the first head) moved from 27% to 32% with the cross-staff handling and to 45% with the
  time-arbitrated voice split, which also lifted Beethoven 5 pages 7 and 8 to 96% and 87%, the Pathétique page 2
  to 82% and the Fauré Ballade pages to 43% and 18%. The Fauré Ballade has no time
  signature on the page, and the Handel Chaconne's upper staff is in 9/8 against 3/4 below (per-staff time
  signatures are not supported). The retrained glyph model costs a little on the Bach suite pages 3 to 6
  (46%, 80%, 69%, 64%). When a page carries no time signature the length is inferred from the most common
  measure sum, measures holding only a whole rest excluded (they take any length).
* `--diag` prints, per staff, the modelled line positions against the actual ink runs at three x positions.
* `PVOMR_STRIPDUMP=<strip index>` (with `--verbose`) prints the candidate line segments and 5-line groups of
  one detection strip; `PVOMR_ROWS=<y0>-<y1>` additionally prints the thin-ink row projection of that strip.
* The GUI (`src/PVOMR.Gui`) shows the same overlays interactively, see "Correction GUI".
* `--verbose --debug --out dir` also writes the staff-less binary image (`*_nostaff.png`).
* `PVOMR_BARDUMP=1` prints every bar line candidate with the reason it was accepted or rejected, the bar lines
  per staff after kind classification and at the end.
* `PVOMR_KEYDUMP=1` prints every clef/key/time placement decision of the start-of-staff pass (hypothesis,
  score, competing digit score, position); `PVOMR_KEYSCAN=1` adds the per-column scores of the scan.
* `PVOMR_VOICEDUMP=1` prints, per staff-measure with a known length, the events (type, `?` = uncertain, x, stem
  direction) and which voice split won at what cost.
* The models were trained on MuseScore renders only; on old engravings they occasionally see heads in beam ends
  (vetoed geometrically) or rests in text dashes (size/position checks). Real labelled pages would fix this at the source.
* Rhythm reconstruction is per voice and measure; tuplets, grace notes and cross-staff beaming are
  not handled (tuplets: only beamed groups with a digit), lyrics are not exported, and slur endpoints are only
  matched within one measure.
