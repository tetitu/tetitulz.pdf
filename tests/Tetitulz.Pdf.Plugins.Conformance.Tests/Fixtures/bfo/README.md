# BFO PDF/A test suite

Files from BFO's PDF/A test suite, <https://github.com/bfosupport/pdfa-testsuite> (commit `c22d802`),
© BFO, under the Creative Commons Attribution 3.0 licence,
<https://creativecommons.org/licenses/by/3.0/legalcode>. The files and `description.txt` are unmodified.

**What a name says.** `pdfa2-6-1-13-bfo-t04-fail.pdf` targets ISO 19005-2 (`pdfa2`), clause 6.1.13, and is
expected to fail; `-pass` files are expected to conform. `description.txt` gives each file's reason. The
suite's one PDF/A-1 file is published without its `.pdf` extension, and its name and its description
disagree about its verdict, so it is not taken.

**How it is used.** `SecondConformanceCorpusTests` validates every file against the part its name gives and compares the
verdict with its name, as `ConformanceCorpusTests` does for veraPDF's corpus; `known-disagreements.tsv` lists the
files whose verdict the checker does not yet match — `miss`, a rule not implemented, or `stricter`, a
deliberate difference — and a listed file that starts agreeing fails the test until its line is removed.
