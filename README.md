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
* Rhythm reconstruction is per voice and measure; tuplets, grace notes and cross-staff beaming are
  not handled, lyrics are not exported, and slur endpoints are only matched within one measure.
