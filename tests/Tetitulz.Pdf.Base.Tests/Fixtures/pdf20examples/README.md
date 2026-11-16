# PDF Association: PDF 2.0 examples

The seven example files of <https://github.com/pdf-association/pdf20examples> (commit `c20f2c1`),
© the PDF Association, under the Creative Commons Attribution-ShareAlike 4.0 International licence,
<https://creativecommons.org/licenses/by-sa/4.0/> — the repository's own licence text is
`LICENSE-pdf20examples.md` beside them. The files are unmodified; this folder is test data and is not
packed into any package.

**What each is for** — the repository's README says what a correct reader shows, and
`Pdf20ExamplesTests` asserts that: the incremental save's page reads "PDF 2.0 Words Have Spacing" (the
update's stream, not the original revision's "PDF 1.7 Words Need Spacing" — the upstream README misquotes
it as "PDF 2.0 files have spacing"),
the offset start opens though the PDF does not begin at byte 0, the UTF-8 strings (an annotation's
Thai `/Contents`, the outline, layer and Info strings) decode as UTF-8, and every file opens and
renders.
