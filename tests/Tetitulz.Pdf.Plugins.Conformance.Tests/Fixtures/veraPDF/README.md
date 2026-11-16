# veraPDF corpus subset

Files from veraPDF's test corpus, <https://github.com/veraPDF/veraPDF-corpus> (commit `bb75f4f`),
© the veraPDF Consortium, under the Creative Commons Attribution 4.0 International licence in
`LICENSE` beside this file. The contents are unmodified; the names are shortened.

**What was taken.** veraPDF's own folders for PDF/A-1a, -1b, -2a, -2b, -2u, -3b, -4, -4e, -4f (files of
8 KB or less) and PDF/UA-1 (40 KB or less — those files embed fonts), each file flattened to
`<folder>/<name>` with the corpus's `veraPDF test suite ` prefix dropped, so the clause stays in the
name: `PDF_A-2b/6-1-2-t01-fail-a.pdf` was `PDF_A-2b/6.1 File structure/6.1.2 File header/veraPDF test
suite 6-1-2-t01-fail-a.pdf`. The corpus's Isartor and TWG folders come from other suites, under their
own terms, and are not included.

**What a name says.** `-pass-` files conform to the folder's standard and `-fail-` files break the
clause the name gives; each file's outline documents what it tests.

**How it is used.** `ConformanceCorpusTests` validates every file against its folder's part, at the
folder's level (`PDF_A-2u` at level U, `PDF_A-4` at `None`, as veraPDF runs each flavour's profile), and compares
the verdict with its name. `known-disagreements.tsv` lists the files whose verdict the checkers do not
yet match — `miss`, a rule not implemented (the clause folder named), `stricter`, a deliberate
difference, or `deep`, a PDF/UA-1 rule the deep checker owns, with the code the audit fails the file
with — and a listed file that starts agreeing fails the test until its line is removed.
`UaConformanceCorpusTests` in the Accessibility tests holds the PDF/UA-1 files to the audit, both
checkers together, with its own list.

**Refreshing.** `tools/verapdf-corpus/` holds the probe, the staging script and the list generator,
with the steps.
