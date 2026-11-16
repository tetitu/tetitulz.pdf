# Acrobat's answers

The form JavaScript object model is Adobe's, and its `AF*` functions are in no published reference, so
the evidence for what a script expects is what Acrobat itself does. These files record it.

| File | What it is |
|---|---|
| `probe.js` | The first probe: several hundred cases, each written as `id<TAB>type:value` into the field `results`. |
| `AcrobatProbe.pdf` | The first probe as a document (its open action), saved by Acrobat after running it — the field holds the answers. |
| `answers-26.2.tsv` | The first probe's answers, one line per case, read out of that field: Acrobat DC 26.2.22031, Windows. |
| `known-disagreements.tsv` | The cases `PdfJsEngine` answers otherwise than Acrobat, each a choice with its reason (Acrobat is the baseline; where it fails the engine is tolerant). A `*` matches any run of characters; a third column limits a row to Acrobat's answer. It only shrinks. |
| `probe2.js` | The second probe: `Matrix2D`, dates, `printf`, the AF functions' alerts, each field type, captions, `/Info`, data objects, timers. |
| `AcrobatProbe2.pdf` | The second probe as a document — a field of each type, an `/Info`, an attachment — built by `build-probe2.py` from `probe2.js`. Saved by Acrobat after running it. |
| `answers2-26.2.tsv` | The second probe's answers, read out of that file's `results` field: Acrobat DC 26.2.22031, Windows. Three ids the probe writes twice (`F.rb.exportValues`, `F.rb.page`, `F.co.value`) stand twice here too; the parity test numbers a repeat `#2` on both sides, so each row is compared. |
| `known-disagreements2.tsv` | The second round's choices, as `known-disagreements.tsv` is the first's. |
| `read-results.py` | Reads every text field out of a saved probe document with no PDF library — `--field results` writes the answers as saved, `--compare <tsv>` checks that a TSV is that field byte for byte (both answers files are). |

`AcrobatParityTests` opens each saved probe document as a viewer does and compares every answer with
the round's answers. No test runs Acrobat or the Python scripts; a round is run by hand in Acrobat
(open the document, dismiss the alerts, save), its answers written from the saved field with
`read-results.py --field results` and checked with `--compare`. What the two rounds did not reach is
settled from Adobe's published reference and worked examples instead (GAPS.md §25b).
