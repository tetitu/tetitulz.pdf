# tests/ — how this suite is put together, and the guards that police it

This directory holds **28 test projects** plus one non-test helper library (`tests/Tetitulz.Pdf.TestHelpers/`), 29 `.csproj` in all. All but one are in `tetitulz.pdf.portable.slnf` and run on the Linux CI job; the only one absent is `tests/Tetitulz.Pdf.WinForms.PdfViewer.Tests/`, which targets `net10.0-windows`. CI enforces the shape rather than trusting it: one step fails the build if a library under `src/` has no `tests/&lt;name&gt;.Tests/&lt;name&gt;.Tests.csproj`, and its `KnownGaps` exemption list is **empty** — re-populating it is always the wrong fix.

The rules that change what you write are in `CLAUDE.md`; this file explains the machinery those rules run on.

---

## The guards, and why they live where they do

Nine tests fail the build when a banned pattern reappears anywhere in the tree. They are ordinary xUnit `[Fact]`s — there is no separate CI step for them, so `dotnet test tetitulz.pdf.sln` is what runs them, and so does the Linux job.

**All nine live in `tests/Tetitulz.Pdf.Base.Tests/TestInfrastructure/`, and most of them scan trees they do not belong to.** A hardcoded path committed in `tests/Tetitulz.Pdf.Plugins.Ocr.Tests/` fails `Tetitulz.Pdf.Base.Tests`, naming the offending file and line. That is confusing exactly once; the alternative is fifteen copies of each guard, which is the drift shape this repository keeps finding.

| Guard | Scans | Fails on |
|---|---|---|
| `NoUnfalsifiableAssertionsTests` | all of `tests/` | a test whose assertions all hold with the feature deleted (banned pattern 3) |
| `NoHardcodedPathsTests` | all of `tests/` | an absolute path into a checkout of this repo; `if (!File.Exists(p)) return;` (patterns 1 and 2) |
| `NoRedundantPdfNameAllocationTests` | all of `src/` | `new PdfName("X")` where `PdfName.X` already exists |
| `NoInlineCmykConversionTests` | `Tetitulz.Pdf.Base` only | a file other than `PdfColor.cs` inlining `r = (1−c)(1−k)` |
| `NoRawNulBytesInSourceTests` | all of `src/` | a `.cs` file containing a literal `0x00` byte |
| `DocumentationReferenceTests` | every `.md` under version control, plus `src/**/*.cs` and `tests/**/*.cs` | a cited repo-relative path that does not exist; a `GAPS.md §N` reference with no matching heading; `GAPS.md` sections out of order |
| `DiagnosticSlotResetTests` | all of `src/` | a `Last*` diagnostic slot that is written but never cleared in its own file |
| `AssemblyLayeringTests` | every `src/**/*.csproj` | a reference cycle; a plugin → plugin edge; a core-ring project referencing a feature or plugin; a banned package returning; an undeclared transitive package |
| `ApiBaselineCoverageTests` | every packable `src/` project | a shipping package with no `tests/ApiSnapshots/<name>.txt`, so its public API could change unnoticed. Any burn-down list it carries is **self-cleaning**: an entry that gains a baseline fails the staleness fact, so the list can only shrink |

### `NoUnfalsifiableAssertionsTests` — the one with no allow-list

The archetype it bans is `byte[] png = Render(pdf); Assert.NotNull(png); Assert.NotEmpty(png);`. A blank page encodes to a perfectly valid non-empty PNG, so that test passes with the renderer stubbed out. It shipped with a `KnownOffenders` set plus a staleness `[Fact]` that failed when an entry stopped naming a real offender, so the set could only shrink; it reached zero and **both were deleted, not emptied**. An empty exemption set is an invitation; no exemption set is a rule.

Three mechanics are worth knowing before you argue with it:

- **`Assert.Null(Record.Exception(…))` counts as "did not throw", not as a strong assertion.** That was the hole the guard shipped with, and it covered almost exactly the features that had no other coverage — 217 sites across 41 files, one cluster of sixteen sitting on top of an SVG→PDF converter that drew nothing at all. The rule keys on `Record.Exception` **and nothing else**: a blanket "`Assert.Null` is weak" rule was tried and correctly removed, because `LastError_IsClearedByASubsequentSuccessfulScan` makes the slot non-null first and is perfectly falsifiable.
- **The hostile-input exemption keys on the input, not on the assertion.** A method name containing one of `HostileInputNameTokens` (`Malformed`, `Adversarial`, `Corrupt`, `Cycle`, `Depth`, `Budget`, `Overflow`, `DoesNotHang`, `Empty`, `Negative`, `Oversized`, `Huge`, `Absurd`, `RandomBytes`, `Unbounded`, …) is exempt, because there the non-throw genuinely *is* the behaviour. `DoesNotThrow`, `NoException` and `RendersSuccessfully` are deliberately **absent** — they describe the assertion. `Nested`/`Nesting` were removed for naming a document's *shape* rather than its hostility. The source is the authority on the current set.
- **A standalone call to a helper whose type name ends in `Assert` counts as delegating**, but the same helper used as an *argument* does not — `Assert.NotNull(ContentStreamAssert.First(…))` is a weak assertion on a projection, and two PostScript tests hid behind precisely that.

If it flags your test, the fix is in the test.

### `NoRawNulBytesInSourceTests` — the guard nothing else documents

A `.cs` file containing a raw `0x00` byte is **binary to ripgrep and to `git diff`**. It does not appear in content searches as a non-match; it does not appear at all. Three files in this repository were in that state, and the way it was found was a grep that answered `binary file matches` and returned nothing.

That matters here more than it would elsewhere. This codebase's stated method for preventing its most-repeated defect — two implementations of one spec rule drifting apart — is to search the tree for the other copy, and the guards in this very folder are themselves text scans over sources. **A file invisible to a text scan is silently exempt from every one of them, and nothing reports that it was skipped.** It is the same shape as banned pattern 2: a check that quietly does not run looks exactly like a check that ran and found nothing.

The three offenders were all *deliberate* uses, not corruption — `PdfTemplateEngine.PositionKey` and `PdfResourceCollector` used NUL as a composite-key separator (a good technique, since it cannot occur in the joined text), and `PsFontMapper` used it for the undefined slots of the WinAnsi 0x80–0x9F table. The idiom was right; writing the byte literally rather than as `"\0"` was what cost the searchability. The escape is byte-identical at runtime.

### `DocumentationReferenceTests` — and a warning about this file

It exists because prose decays unless something checks it. A documentation review once found a **deleted** type cited fourteen times across five documents, two of those citations being *instructions*: `CLAUDE.md` told a reader not to consolidate two writers into one, preserving a structure that no longer existed.

It checks paths and `GAPS.md §N` section numbers only — both mechanical, both with no false positives. It deliberately does not verify that every backticked identifier exists, because that needs a token index and leaves a residue of legitimate non-matches (foreign APIs, spec operators, names cited *as removed*).

Two consequences for anyone editing docs, including this one:

- **Any `src/…`, `tests/…`, `docs/…`, `benchmarks/…`, `apps/…`, `demo/…`, `build/…` or `.github/…` path you write in a markdown file must exist**, or the build goes red. A brace-expansion shorthand (`…/{VerifiedWrite,PdfObjectIndex}.cs`) and a glob are both skipped, since they name several files rather than asserting one.
- A line whose prose says the file was **deleted / removed / renamed / superseded / moved to** is exempt, keyed on the sentence rather than on an allow-list — so silencing a real breakage requires stating the disposal in prose a reader will see.

Every exemption is scoped to a **folder** — build output, `.claude/` agent scratch, restored NuGet packages, `node_modules/` — rather than to a list of files, so none of them can quietly grow. There is deliberately no exemption for repository documentation.

### Every guard has a floor, and you must write one too

A source-scanning guard that reads nothing passes. Each of the nine therefore carries an explicit test that it is reading something real:

- `PdfName_cache_is_populated` asserts the declaration regex finds **more than 200** cached names, so a change of declaration style surfaces as "this guard stopped working" rather than as silence.
- `The_guards_are_reading_a_substantial_number_of_documents` asserts **at least 15** markdown files, that `CLAUDE.md` and `GAPS.md` are among them, and **at least 100** path citations across them.
- `Every_GAPS_section_reference_resolves_to_a_heading` asserts it recognised **more than 10** headings before checking any reference.
- `NoRawNulBytesInSourceTests.TheDetectorFiresOnANulByteAndNotOnOrdinarySource` validates the counter against synthetic bytes — a file-based fixture would have to *contain* the byte the guard bans, which is a contradiction.
- `NoInlineCmykConversionTests` runs its detector over the four real shapes the nine drifted copies were written in, **and** over two geometry expressions it must not flag. It validates the detector rather than asserting that `PdfColor.cs` matches the pattern: the first draft did the latter and was wrong within the hour, because `CmykToRgb` factors `1 − k` into a local and so does not have two parenthesised subtractions on one line.
- `PublicApiSurfaceTests` asserts the renderer emits **more than 500 lines** for `Tetitulz.Pdf.Base`, because an empty render compares equal to an empty baseline and passes forever.

Two design notes worth copying. `NoInlineCmykConversionTests` gets to run **without an exemption list** only because its discriminator is sharp: `(1−a)(1−b)` is also Coons-patch and Bézier-basis arithmetic in `ContentStreamInterpreter.cs`, and what separates a *colour* conversion is that the same line scales to a channel (`255` or `MaxChannelValueF`). Widening the pattern would force an exemption list, which is the thing to avoid. And `NoRedundantPdfNameAllocationTests` scans `src/` **only, on purpose**: roughly 240 call sites under `tests/` allocate cached names and should keep doing so, because a fixture built with `new PdfName("Type")` states the literal bytes it expects independently of the production constant — using `PdfName.Type` would let a typo in the constant's *value* change the fixture and the assertion together.

---

## Pins, which are not guards

Two files in `TestInfrastructure/` look like guards and are not.

**A guard encodes a rule.** A failure means you broke the rule, and the fix is always in your change. There is no legitimate way to make a guard accept a violation.

**A pin encodes current behaviour.** A failure means something changed, and the fix is a decision: either the change was unintended and you revert it, or it was intended and you update the pin **in the same commit**, so the diff becomes the record of what moved.

`PublicApiSurfaceTests` — **one per package, in that package's own test project** (`tests/Tetitulz.Pdf.Base.Tests/PublicApiSurfaceTests.cs`, `tests/Tetitulz.Pdf.Accessibility.Tests/PublicApiSurfaceTests.cs`, and 23 more) — pins each assembly's exported surface against its checked-in baseline. It is the instrument that makes a large refactor reviewable: splitting a 6 000-line type into partials, promoting a nested class or moving a file between folders are all supposed to leave the public API untouched, and without this "supposed to" is the whole of the evidence.

> **One pin per package, never a grouped one.** A grouped pin makes a one-member API change fail twice and be read twice. Removing one is safe for a checkable reason rather than by inspection:
> `ApiBaselineCoverageTests.Every_package_baseline_is_actually_compared_against` fails for any package whose baseline nothing reads.

`DuplicatedHelperBehaviourTests` pins the behaviour of helper clusters that existed in several drifted copies — literal-string escaping (8 copies, 4 of them weak), dictionary cloning (15 copies, divergent on null), stream seek-normalisation, image magic-byte sniffing (6 copies that disagreed), PDF date formatting. **Several of its assertions originally pinned defects on purpose**, each labelled as such, so that consolidation produced a visible reviewable diff rather than a silent change; most have since flipped to pinning the fix, in the commit that fixed them. One of its assertions is neither: `The_two_seek_helpers_have_different_contracts_for_a_seekable_stream` pins a **genuine contract difference that must not be merged** — `PdfHelpers.EnsureSeekable` rewinds and returns the caller's own stream, `PdfMetadataWriter.EnsureSeekable` always returns a fresh `MemoryStream` because the incremental writer reads the source bytes twice. The colliding *name* is what makes them look mergeable.

---

## Visible skips: the attribute set, and why there are so many

**An early `return` from a `[Fact]` reports Passed.** That is banned pattern 2, and it is how roughly 40 results in this suite were once green regardless of what the renderer did. A test whose precondition is absent must report **Skipped**, which is the honest outcome, and the only way to do that in xUnit is to set `Skip` from the attribute's constructor — the decision has to be made before the test body runs.

The enforcers committed this offence themselves: `NoHardcodedPathsTests` and `NoUnfalsifiableAssertionsTests` both opened with `if (TestsRoot == null) return;` while `TestSourceScanner`'s own documentation claimed that was a visible skip. `RepoSourceFactAttribute` exists because of that.

| Attribute | Skips when | Lives in |
|---|---|---|
| `CorpusFact` / `CorpusTheory` | the developer-local PDF corpus (or a named file in it) is absent; every such test also carries `Category=Corpus`, so `--filter "Category!=Corpus"` leaves them out | `tests/Tetitulz.Pdf.TestHelpers/CorpusFactAttribute.cs` |
| `SystemFontFact` / `SystemFontTheory` | a required font **family** is not installed | `tests/Tetitulz.Pdf.Base.Tests/TestInfrastructure/SystemFontFactAttribute.cs` |
| `RepoSourceFact` | the repository's **`tests/`** directory is not on disk | `tests/Tetitulz.Pdf.Base.Tests/TestInfrastructure/RepoSourceFactAttribute.cs` |
| `RepoSourcesFact` | the repository's **`src/`** directory is not on disk | `tests/Tetitulz.Pdf.Base.Tests/TestInfrastructure/NoRedundantPdfNameAllocationTests.cs` |
| `NonUtcHostFact` | the host's local time is already UTC | `tests/Tetitulz.Pdf.Base.Tests/TestInfrastructure/DuplicatedHelperBehaviourTests.cs` |
| `EmbeddableFontFact("sample")` | no installed TrueType **file** covers every character of the sample | `tests/Tetitulz.Pdf.Base.Tests/TestInfrastructure/EmbeddableFontProbe.cs` |
| `JpxCorpusFact` | the JPEG 2000 corpus (a sparse clone of the public JPEG 2000 conformance data, `TETITULZ_JPX_CORPUS` or `tests/jpx/openjpeg-data`) is absent | `tests/Tetitulz.Pdf.Core.Tests/TestInfrastructure/JpxCorpusFactAttribute.cs` |
| `ApiBaselineFact` | the repo root is unreachable, so the API baseline cannot be read | `tests/Tetitulz.Pdf.TestHelpers/ApiBaselineFactAttribute.cs` |
| `HebrewFontFact` / `HebrewFontTheory` | no font on the host covers U+05D0–U+05EA | `tests/Tetitulz.Pdf.TestHelpers/HebrewFontFactAttribute.cs` |
| `EpsCorpusFact` | no developer-local `.ps` / `.eps` samples | `tests/Tetitulz.Pdf.Plugins.PostScript.Tests/TestInfrastructure/EpsCorpusFactAttribute.cs` |

**`RepoSourceFact` and `RepoSourcesFact` differ by one letter and by which tree they check.** Singular gates on `tests/`; plural gates on `src/`. Pick by what your guard reads, not by autocomplete.

Three of these deserve their reasoning stated, because the naive version of each is wrong:

- **`SystemFontFact` cannot be a null check.** `SKTypeface.FromFamilyName` never reports failure: asked for a family it does not have, DirectWrite returns Segoe UI and fontconfig returns DejaVu Sans, both non-null. A test gated on `!= null` believes every font in the world is installed. `SystemFontProbe` compares the returned `FamilyName` with what was asked for — the same rule `FontCache.IsFamilyMatch` applies in production. The tests it gates assert that a PostScript base font name resolves to the *derived* family rather than to the platform substitute, which is unobservable on a stock Ubuntu runner where "ArialMT" and a nonsense name legitimately share one fallback face. That is a fact about the host, not a reason to loosen the assertion.
- **`EmbeddableFontFact` asks a different question from `SystemFontFact`.** It needs bytes it can hand to `TrueTypeFontProgram.Parse` and real `cmap` coverage of specific code points; a font that resolves by name but has no Hebrew block is useless to it. It scans at most 400 font files, since a font server can expose thousands.
- **`HebrewFontFact` runs through the production `SystemFontResolver`** rather than a private copy — it is the type under test in the sibling file, and a test-only font search that disagreed with the shipping one would gate the tests on the wrong question.

### A gate more than one project needs lives in `TestHelpers`

**CLAUDE.md bans cross-project test dependencies**, so `Tetitulz.Pdf.Accessibility.Tests` cannot reference `Tetitulz.Pdf.Base.Tests`; the one shared place is `Tetitulz.Pdf.TestHelpers`. `ApiBaselineFact` (27 copies) and `HebrewFontFact` (3) were copied per project while that library had no xunit reference. It has had one since the corpus attributes moved there, so both are single types in it now — **put a new shared gate there, never a copy.** `EpsCorpusFact` has one user and stays in `Plugins.PostScript.Tests`. `Builder.Tests`' `HebrewCapableFontFact` is not a copy: it asks for Hebrew **and** Latin and a space glyph, a different question.

---

## `tests/Tetitulz.Pdf.TestHelpers/`

A plain `net10.0` library: SkiaSharp, xunit (for the shared skip gates), BouncyCastle (for `RsaTestKeys`) and a project reference to `Core` (for `HebrewFontProbe`). It is not a test project (`IsTestProject` false). Most test projects reference it; the rest build their fixtures locally.

| Type | What it is for |
|---|---|
| `MinimalPdfBuilder` | 18 factory methods producing hand-assembled single- and multi-page PDFs with correct xref offsets — red rectangle, grey fill, CMYK fill, stroked rect, rotation, annotations, a structure tree, duplicate objects, an unused object, embedded JavaScript |
| `MinimalTrueType` | builds a **genuine sfnt** with `.notdef` plus one glyph per requested character, and reads a format-4 cmap back out; `Build(chars, platformId, encodingId)` reaches the (3, 0) Windows-Symbol table that marlett/symbol/wingdings ship |
| `PdfContentProbe` | reads geometry back out of a generated PDF's content streams — `DrawnTexts`, `DrawnGlyphRuns`, `XOf`/`YOf`, `StrokedSegments`, `RectangleFillCount`, plus `NormalizedRaw` which masks the per-build trailer `/ID` |
| `TestCorpus` | resolves the developer-local corpus; see below |
| `PublicApiSnapshot` | renders and compares the API baselines; `ApiBaselineFact` gates on it |
| `HebrewFontProbe` | the host font covering Hebrew, through the production `SystemFontResolver`; `HebrewFontFact` / `HebrewFontTheory` gate on it |
| `TempFiles` | a GUID-named path under the OS temp directory |

`MinimalTrueType` exists so tests never touch the filesystem and never depend on installed fonts **while still exercising the real metric parser and the real subsetter** — a hand-waved byte array would make every assertion about `/Length1`, the descriptor or the subset cmap meaningless.

`PdfContentProbe` reads coordinates rather than extracted text, and the reason is a trap worth internalising: `Core.Text.PdfTextExtractor.ExtractText` re-applies bidi reordering per line, **so text drawn in the wrong visual order extracts to the right logical string.** An ordering assertion built on extraction passes with the renderer's bidi deleted — it is unfalsifiable for the thing it appears to test. The probes read the `Tm` coordinates the renderer actually emitted.

### `MinimalPdfBuilder` is forked twice, and both forks are older and smaller

- `tests/Tetitulz.Pdf.TestHelpers/MinimalPdfBuilder.cs` — `public`, 18 factory methods.
- `tests/Tetitulz.Pdf.Core.Tests/Helpers/MinimalPdfBuilder.cs` — `internal`, 2 methods.
- `tests/Tetitulz.Pdf.Plugins.EthereumSign.Tests/Helpers/MinimalPdfBuilder.cs` — `internal`, 1 method.

Neither forking project references `TestHelpers`, so the copies are not currently a correctness risk — but they are three implementations of "a valid one-page PDF" that can disagree about xref offsets, and this repository's recurring defect is exactly that shape. **Do not start a fourth.** If you need a fixture in a project that does not reference `TestHelpers`, add the reference rather than the copy.

---

## `tests/ApiSnapshots/`

One checked-in baseline per packable `src/` project. One renderer produces all of them (`PublicApiSnapshot.Render`), so the test projects that consume them cannot drift in format. A `.txt.actual` file beside a baseline is a mismatch report from the last run, and is gitignored.

What a baseline contains: every exported type, one member per line, sorted with `StringComparer.Ordinal` so the file is stable across runs, machines and reflection orderings. **Protected members are included** — they are part of the contract for anyone subclassing an exported type, so dropping one is a break even though it is not `public`. `private protected` is excluded (invisible outside the assembly) while `protected internal` is included. Property and event accessors are excluded because the property line already covers them. A literal constant's *value* is rendered, because it is baked into every consumer at their compile time and changing one silently desynchronises already-built callers.

**Accepting an intended change.** On mismatch the comparer writes the current surface beside the baseline as `<Assembly>.txt.actual` (gitignored) and the failure message tells you to replace the baseline with it. Do it **in the same commit as the API change**, so the diff becomes the migration table — written by reflection rather than typed by hand. If a baseline is missing entirely, the same file is produced and the test tells you to review and rename it.

Line endings are normalised before comparison: the repo has `core.autocrlf=true`, so a checked-in LF baseline arrives in the worktree as CRLF and a byte comparison would fail for no reason.

---

## The corpus, which is deliberately not committed

`tests/pdf/` and `raw/` are both gitignored — the first holds sample PDFs of uncertain provenance, the second ad-hoc customer files. Nothing in the suite may depend on them being present.

`TestCorpus` resolves each root in order: the `TETITULZ_PDF_CORPUS` / `TETITULZ_PDF_RAW` environment variable if it is set and exists, else `<repo>/tests/pdf` and `<repo>/raw`, where the repo root is found by walking up at most 12 directories from the test assembly looking for `tetitulz.pdf.sln`. **There is not one hardcoded absolute path in the type, by design** — `NoHardcodedPathsTests` fails the build if a drive-letter literal naming this project reappears anywhere under `tests/`.

```bash
TETITULZ_PDF_CORPUS=/path/to/pdfs dotnet test tetitulz.pdf.sln
```

Without it, the corpus-gated tests report **Skipped** with a message saying which file they wanted. `CorpusTheory` additionally covers the case where a corpus-driven `[MemberData]` source yields no rows: xUnit treats an empty theory as a failure ("No data found for…"), so without the attribute the smoke tests break a clean checkout outright.

---

## Parallelism and process-wide state

xUnit runs test classes in parallel across collections. Three kinds of shared state break under that, and the first two have bitten:

**`PdfPluginHost` is a process-wide singleton.** Two classes registering, unregistering or calling `ResetForTesting()` concurrently corrupt each other — a plugin registered by one disappears mid-test when another resets the registry. It surfaced as a genuinely intermittent failure: the plugin-dispatch tests passed alone and failed roughly **one run in six** as part of the full suite. `PluginHostCollection` (`[CollectionDefinition(…, DisableParallelization = true)]`) serialises them. **Any new test that registers a plugin must join it.**

**Diagnostic `Last*` slots are `[ThreadStatic]`, and a negative assertion over a shared sink is still a statement about the whole process.** `Assert.Empty(diagnostics)` against a process-wide sink passes alone and fails beside a neighbour that legitimately reported one. Filter the handler to the calling thread; do not serialise the collection, which hides the coupling instead of removing it. Per-thread isolation itself is pinned by `tests/Tetitulz.Pdf.Base.Tests/Diagnostics/ThreadStaticDiagnosticIsolationTests.cs` and `tests/Tetitulz.Pdf.Core.Tests/Diagnostics/ThreadStaticDiagnosticIsolationTests.cs`.

**`PdfLimits` and `PsLimits` are process-global settings.** Every DoS ceiling became a settable property so a consumer can tune it, which means a test that *lowers* one lowers it for every test running beside it — and the failure would appear in a class that never mentions limits. `PdfLimitsCollection` serialises the classes that write one, and `PdfLimits.ResetToDefaults()` in a `finally` puts them back. **Reading a limit needs none of this**, which is what every other test does; membership is narrow on purpose, because widening it would serialise work that has no reason to be serial. This one is prospective rather than scarred: the collection existed before the first test that needed it.

---

## The three banned patterns, in brief

The rule is in `CLAUDE.md` under "Three banned patterns", and the history that makes it stick is under the same heading in `CONTRIBUTING.md`. In short:

1. **No absolute path into a local checkout.** Unrelated absolute literals (a `C:\Program Files\LibreOffice\…` option value, a deliberately-missing `Z:\` path) are fine; only paths that bake in where *this repo* lives are rejected.
2. **No `if (!File.Exists(path)) return;`.** It reports Passed while executing nothing. Use a skip attribute.
3. **No assertion that cannot fail.** A test named for a behaviour must assert that behaviour — decoded pixels, a colour at a point, ink inside a rect and not outside it, a difference against a baseline, or the parsed object the code produced.

And a fourth failure mode no guard can catch: **a test whose name and body disagree.** Two JBIG2 tests asserted the exact opposite of their own names, under a comment narrating the relaxation, and kept green coverage over a real defect for the feature's whole life. Both assertions were perfectly falsifiable — they falsified the wrong claim. When a behaviour changes, rename the test or restore the behaviour; never edit the assertion and leave the name. A comment explaining why the assertion no longer matches the name is the tell.

**When you write or rewrite a test, mutate the code and check the test goes red.** Read a surviving mutation as a finding about the test. Four ways a mutation survives, all observed here: the fixture never reached the feature (a mesh-shading control supplied two vertices where a triangle needs three, so it drew nothing and its `NotNull` was measuring a rectangle painted separately); the fixture did not exercise the case (the "special characters" were *balanced* parens, which §7.3.4.2 allows unescaped); the assertion was weaker than the claim; or the mutation was accidentally equivalent.

---

## Diagnosing a red whole-solution run

Four rules, each extracted from a real investigation. `CLAUDE.md` carries the full set under "Diagnosing a red whole-solution run" (with the investigations behind them in `CONTRIBUTING.md`); these are the parts that change what you do next.

1. **`dotnet test --blame` is the diagnostic, not the assertion text.** It runs sequentially. If it passes everything while the ordinary run aborts, the cause is concurrency, and the entire "which assertion is wrong" search is ruled out before it starts.
2. **A negative assertion that reads shared state is asserting something about the whole process.** See the diagnostics note above.
3. **A test count that disagrees with the assembly's total means a stale binary, not a flake.** Check the totals before diagnosing anything else, and after any consolidation commit compare the count to the previous run. A green run is not evidence; a green run *with the same count* is. A type-initialization crash once reported `Passed!` while silently dropping about 190 tests.
4. **A DoS-guard test asserts the guard's own evidence** — a typed exception, a reported reason, a refusal — never a wall clock. A guard test that finishes fast has quite possibly not run.

One structural cause behind rule 3 is worth repeating because it constrains how you deduplicate: **share downward into a leaf, never sideways between peers.** Pointing `PdfSvgRenderer` at a static on `ContentStreamInterpreter` (which has a static initializer) created a type-initialization edge that crashed the Base test host part-way through parallel runs. Extracting the helper into a leaf type does not.

---

## Adding a test

- One test project per production project, mirroring the name: `tests/Tetitulz.Pdf.Xxx.Tests/`. CI fails if a `src/` library has none. **No cross-project test dependencies** — the only shared library is `Tetitulz.Pdf.TestHelpers`.
- Build the fixture in memory. Never read a file from disk unless the test is corpus-gated.
- Name the file `tests/Tetitulz.Pdf.Xxx.Tests/<Feature>Tests.cs`. Use `[Fact]` / `[Theory]`; no mocks unless unavoidable.
- Any DoS or recursion guard needs an explicit adversarial-input test that would have hung or stack-overflowed before the fix, asserting the guard's evidence rather than elapsed time.
- If the test touches `PdfPluginHost`, join `PluginHostCollection`. If it **writes** a `PdfLimits` or `PsLimits` value, join `PdfLimitsCollection` and restore with `ResetToDefaults()` in a `finally`.
- If the test depends on the host — a font, a codec, a time zone, the corpus, the repo sources — gate it with a skip attribute, and if none fits, write one whose constructor sets `Skip`.
- A rendering fix belongs in a shared helper or in both renderers, with a parity assertion in `tests/Tetitulz.Pdf.Base.Tests/Rendering/SvgRasterParityTests.cs`. Every "silently wrong output" defect this repo has found in rendering was one renderer lagging the other.
- Run `dotnet test tetitulz.pdf.sln`. Zero failures before the change is done. The CI run is the only authority on the test count; **do not write one into a document** — four different counts once appeared across the root docs and none matched the tree.
