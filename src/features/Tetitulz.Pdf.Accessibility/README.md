# Tetitulz.Pdf.Accessibility

Audits, tags and remediates PDFs against **PDF/UA-1** (ISO 14289-1), with WCAG 2.1 and
Matterhorn-Protocol cross-references on every finding. `Audit` also judges against **PDF/UA-2**
(ISO 14289-2) on request — see *Choosing the conformance target* — while tagging and remediation
produce UA-1 output.

**Targets:** `net10.0` and `netstandard2.0` (in step with `Tetitulz.Pdf.Plugins.Conformance`)
**Package dependencies:** none
**Project dependencies:** `Tetitulz.Pdf.Base`, `Tetitulz.Pdf.Plugins.Conformance`

> The reference to `Tetitulz.Pdf.Plugins.Conformance` is the **second sanctioned architecture
> exception** in [`CLAUDE.md`](../../../CLAUDE.md): this feature reuses the plugin's PDF/UA-1
> validation engine rather than duplicating it. The edge is one-way and acyclic
> (`Accessibility` → `Conformance` → `Core`/`Base`); Conformance must never reference a feature.

---

## Read this first

**A clean audit is not a conformance certificate.** `AccessibilityAuditReport.IsConforming` is
literally `ErrorCount == 0` — *"no blocking issue was **detected** by the checks this library
implements"*. Its `ToString()` deliberately prints `NO BLOCKING ISSUES DETECTED (not a PDF/UA-1
conformance claim)` rather than "CONFORMING", because that string is the one a human reads.

Three classes of requirement sit outside it, permanently:

| | What is not covered | Why |
|---|---|---|
| 1 | `Warning` and `Info` findings | They do not clear `IsConforming` even though a stricter validator may fail the file for them. Use `IsFullyConforming` (errors **and** warnings zero) when that matters. |
| 2 | Requirements static analysis cannot see | Glyph-complete Unicode coverage, whether a `<Table>`'s header/scope association is *semantically* right, whether a `<L>` really is a list. Some are partially covered (UA12, UA20/UA26); none is fully. |
| 3 | Requirements no machine can decide | Whether `/Alt` text is *accurate*, whether heading levels reflect the real document outline, whether the reading order is the *intended* one. ISO 14289-1 mandates human review for exactly this reason. |

**Validate with an independent PDF/UA checker, and have a human review the file, before publishing any
conformance claim.** What a clean report does prove is narrower and still useful: every issue it
*does* report is a real defect, and fixing all of them is *necessary* (not sufficient) for
PDF/UA-1.

A clean report also assumes every check *ran*. When one could not, the audit says so as an
`Info`-severity **`UA99`** naming the page and the checks that were lost — and `IsConforming`
ignores `Info`, by design. **Read `UA99` before reading the verdict.**

Six further constraints that change what these APIs can do for you:

- **Remediation reaches `IsConforming == true` only if you supply two things it cannot invent:**
  a document **title** (`RemediationOptions.Title` — closes UA04) and a font **program**
  (`RemediationOptions.EmbedMissingFonts` + `FontEmbedding.FontProvider` — closes UA21/UA25).
  Given both, an untagged `/Helvetica` document with a link annotation goes from 10 errors to 0.
  Without them the residual is exactly UA04 and/or UA21+UA25, reported rather than glossed over.
  See [Remediate](#remediate--remediatewithreport) and [Embed fonts](#embed-fonts).
- **Encrypted documents can be audited *and* remediated, in place.** Both checkers audit one in
  full — including the structural checks — and name what they could not attempt. **Every writer
  has a `Plan`**, so the whole nine-step pipeline runs against an
  encrypted document through `RemediationOptions.EncryptedRevisionWriter`, keeping the original
  bytes byte-identical (a signature over them still verifies; the owner password survives). A
  **direct** call to a writer's `Stream`→`Stream` overload still passes an encrypted document
  through unchanged — the `Plan` overloads are the encrypted entry point. See
  [Encryption](#encrypted-documents).
- **The auto-tagger is a heuristic.** It emits `P`, `H1`–`H3` and pagination artifacts, inferred
  from font-size ratios and margin bands, plus `L`/`LI`/`LBody` when `AutoTagOptions.InferLists` is
  set, `Table`/`TR`/`TH`/`TD` when `AutoTagOptions.InferTables` is, and `Figure` for each image
  `AutoTagOptions.FigureAlternateText` supplies a description for. It infers no links.
  (It *does* place every non-exempt annotation in the tree via `/OBJR`.)
- **Pages that already carry marked content are skipped, never re-tagged**, as are pages whose
  decoded content stream exceeds 4 MiB and pages the scanner cannot reason about. Every skip is
  named in `AutoTagResult.SkippedPageReasons`.
- **Reading-order repair declines rather than guesses.** `FixReadingOrder` permutes a `/K` array
  only when it can prove the children are one top-to-bottom column; a table row, a two-column
  section or any set of blocks sharing vertical space keeps the order it had.
- **Every writer's count is a fact about the output.** The incremental writer never throws — on a
  failure it reports and leaves your stream holding just the original bytes — so a count assembled
  *before* the write describes a document that may never have been produced. Every reporting write
  site verifies through `Base.Operations.VerifiedWrite` that the appended revision actually reached
  your output stream, and reports **zero with a warning** when it did not. The invariant is
  **a reported count > 0 implies the output differs from the input**: `AutoTag`,
  `MarkHeadersAndFooters`, all seven `PdfStructureTreeEditor` operations, `FixReadingOrder` and
  `EmbedMissingFonts`. `RemediateWithReport` additionally re-audits the bytes it produced.
  The warning channel is the result object's `Warnings` where it has one, and
  `PdfStructureTreeEditor.LastWriteError` (`[ThreadStatic]`) for the operations that return a bare
  `int` / `bool`.

---

## What "tagging" means here, and why both halves ship together

A tagged PDF is **two** things, and only the pair is useful:

1. **Marked content in the page stream** — `/P <</MCID 3>> BDC … EMC` around each run of real
   content, `/Artifact <</Type /Pagination /Subtype /Header>> BDC … EMC` around page furniture.
2. **A logical structure tree that points at it** — `/StructTreeRoot` → elements whose `/K` lists
   those MCIDs, a `/ParentTree` number tree mapping each page's `/StructParents` key back to the
   owning elements, and `/MarkInfo <</Marked true>>`.

A structure tree with no MCIDs is invisible to assistive technology and fails independent checkers
outright, so this feature never emits one alone. `AutoTagOptions.RewriteContentStreams = false`
suppresses half (1) — and then deliberately writes no structure tree either, because that
combination *is* the hollow-tagging defect. `AutoTagResult.ContentMarked` is the flag to check:
it is true only when at least one page's content stream really was re-marked.

On the audit side the same defect is caught by three checks that work at different scopes:
**UA14** (no `/ParentTree`, or `/StructParents` that do not resolve), **UA16** (a hollow *leaf*
element) and **UA30** (a whole *subtree* that reaches no content at any depth — the case UA16
structurally cannot see, because every node in it does have an element child).

---

## Public API

Everything is reachable from the static facade `Tetitulz.Pdf.Accessibility.PdfAccessibility`.
The underlying classes are public too, for callers that want the richer result types.

### Audit

```csharp
AccessibilityAuditReport PdfAccessibility.Audit(string pdfPath);
AccessibilityAuditReport PdfAccessibility.Audit(Stream input);   // read in full, never modified

// The deep half on its own, for callers that do not want the plugin's dictionary checks:
IReadOnlyList<AccessibilityIssue> PdfDeepAccessibilityChecker.Check(string pdfPath);
IReadOnlyList<AccessibilityIssue> PdfDeepAccessibilityChecker.Check(Stream input);

// …and the same two with explicit content-size ceilings:
IReadOnlyList<AccessibilityIssue> PdfDeepAccessibilityChecker.Check(
    string pdfPath, int maxPageContentBytes, long maxDocumentContentBytes);
IReadOnlyList<AccessibilityIssue> PdfDeepAccessibilityChecker.Check(
    Stream input,   int maxPageContentBytes, long maxDocumentContentBytes);
```

Runs the plugin's `PdfUaChecker` (UA00–UA09, UA25–UA29, UA50–UA61, UA99) **and** this assembly's
`PdfDeepAccessibilityChecker` (UA10–UA24 and UA30–UA35, plus its own UA00/UA06/UA99) over the same bytes
and merges the findings. Three of the plugin's codes are **dropped unconditionally** from the merge
(`SupersededByDeepCheck`): **UA28/UA29** in favour of the deep checker's better-located UA14/UA16,
and **UA50** in favour of UA16–UA18. A fourth, **UA06**, is dropped **only when the deep half
actually produced one** — see "Encrypted documents" below for why that difference matters.
Everything else the plugin reports is
mapped to **Error** severity — except its `UA99`, which maps to **Info**. Issues are sorted into a
total order — severity, then page (document-level first), then code, then object number, then
message — so two audits of the same bytes always produce the same sequence.

A document that cannot be parsed at all is reported as a single Error-severity `UA00` rather than
throwing: the caller of an audit wants a report, and "this file is unreadable" is the most
important finding there is.

**An audit that could not run a check says so.** Every content-level check passes through one gate
that records the page, the object number, the cause and the name of each check that was lost, and
those become `Info`-severity `UA99` issues — one flavour for a page whose content stream would not
decode at all, another for a page scanned only in part (an unreadable Form XObject, or a `BDC`
naming a property list absent from `/Properties`), which also names the checks that ran on input
known to be short. **Before treating a clean report as clean, look for `UA99`:** a page named there
was not examined, and `IsConforming` deliberately ignores `Info` findings.

**The audit is bounded, and says which page it refused.** A page whose `/Contents` decodes past
`DefaultMaxPageContentBytes` (8 MiB) is abandoned while it is being assembled, and a document is
abandoned once decoded content passes `DefaultMaxDocumentContentBytes` (128 MiB) in total. Both are
`public const`s and both are overridable through the three-argument `Check` overloads (pass `0` /
`0L` to disable). A refused page goes through the same coverage gate as any other unreadable page,
so it becomes an `Info` `UA99` naming the page, the object number, the limit and the checks that
did not run — the rest of the document is audited normally. Underneath, `ContentStreamScanner`
enforces its own `DefaultMaxScanBytes` (32 MiB) and `DefaultMaxTokens` (4 000 000); a refusal there
is allocation-free and returns **no** tokens (`ContentScanResult.LimitExceeded`), never a partial
list, so no caller can mistake a truncated scan for a complete one.

`AccessibilityAuditReport` exposes `Issues`, `ErrorCount`/`WarningCount`/`InfoCount`,
`TotalInstanceCount` (aggregated issues counted by their real instance count), `IsConforming`,
`IsFullyConforming`, `HasBlockingIssues`, and the groupings `ByCategory` / `ByPage` / `ByCode` /
`ByMatterhornCheckpoint` / `ByWcagCriterion` / `DocumentLevelIssues` / `PagesWithIssues`.

#### Choosing the conformance target

```csharp
AccessibilityAuditReport PdfAccessibility.Audit(string pdfPath, AuditOptions options);
AccessibilityAuditReport PdfAccessibility.Audit(Stream input,   AuditOptions options);

var options = new AuditOptions { Target = PdfUaVersion.Ua2 };   // Ua1 (default), Ua2, Auto
```

`AuditOptions.Target` names which part of ISO 14289 to judge against. `Ua1` is the default, and a
`null` or default `AuditOptions` is exactly the two-checker UA-1 merge described above.

- **`Ua2`** runs `PdfUa2Checker`'s Clause 5 plus the deep `PdfUa2StructureChecker`, and **no UA-1
  checker at all** — the two standards' rules differ, so mixing them would report a UA-1 violation
  against a document that never claimed UA-1. Findings arrive as `UA2-<clause>` codes through the
  same report, with `UA2-99` mapped to **Info** for the same reason this side's `UA99` is.
- **`Auto`** reads the document's declared `pdfuaid:part`: `2` selects UA-2, and anything else — `1`,
  no XMP packet, an unreadable one — selects UA-1, which is also what a caller naming no target gets.
- `AccessibilityAuditReport.ConformanceTarget` names the part that **was** applied, so it is never
  `Auto`; the two-argument constructor rejects that value rather than storing an unanswered question.

`AuditOptions` also carries `MaxPageContentBytes` / `MaxDocumentContentBytes`, the deep checker's
content-size ceilings, so a caller can set a target and those alike.

**Authoring PDF/UA-2.** `RemediationOptions.PdfUaPart = 2` makes the pipeline write PDF/UA-2 output:
the auto-tagger tags for that part (`AutoTagOptions.Target`, the PDF 2.0 standard structure namespace
on every element and declared in `/Namespaces`), the identifier step writes `pdfuaid:rev` 2024 beside
`pdfuaid:part` and the catalog's `/Version` 2.0 (a PDF/UA-2 file is a PDF 2.0 file), and the
before/after audits judge PDF/UA-2 (`RemediationOptions.Target` follows the part unless set). An
untagged page comes out conforming under the PDF/UA-2 audit given a title and a font program, as under
PDF/UA-1. The roles PDF/UA-2 adds — `Aside`, `Title`, `FENote` — are not inferred: the tagger reads
geometry and type size, not meaning.

### AutoTag

```csharp
AutoTagResult PdfAccessibility.AutoTag(string inputPath, string outputPath, AutoTagOptions o = null);
AutoTagResult PdfAccessibility.AutoTag(Stream input, Stream output, AutoTagOptions o = null);
```

Rewrites each page's content stream with real marked content **and** writes the structure tree
that points at it, all in one appended revision. Catalog wiring (`/StructTreeRoot`,
`/MarkInfo <</Marked true>>`, `/Lang` when absent, `/ViewerPreferences <</DisplayDocTitle true>>`)
comes with it.

Layout is derived from the **content stream itself** — baseline Y and effective font size from
`Tm`/`Td`/`TD`/`T*`/`Tf`/`cm` — not from extracted text, because extracted text gives no byte
offsets and a splice derived from it could only approximate where a run begins. Text extraction is
used only to estimate the document-wide median body font size, and its failure is tolerated.

An existing structure tree is **replaced, not merged**: the generated tree supersedes it, and the
`/StructParent(s)` keys the new `/ParentTree` no longer defines are purged so the output stays
valid (ISO 32000-1 §14.7.4.4). Each removal is reported in `AutoTagResult.Warnings`. Annotations
the tagger is about to re-key are exempt from that purge, so only genuinely dangling references
are removed.

**Annotations go into the tree too** (`AutoTagResult.AnnotationsTagged`). Each non-exempt
annotation gets, as one unit: a wrapper element — `/Link` for a `/Link`, `/Form` for a `/Widget`,
`/Annot` otherwise — whose `/K` is an `/OBJR` naming the annotation, a `/Pg` on the element, a
`/StructParent` on the annotation, and a **direct reference** (not an MCID array) under that key
in the `/ParentTree`. Both key kinds share one ascending `/Nums` and one `/ParentTreeNextKey`
counter. If the annotation's revision cannot be registered it gets *neither* key nor `/OBJR`, so
no dangling half-link is ever written. The wrapper is placed under the block whose vertical band
contains the `/Rect` centre, else under `/Document` — a wrong guess costs reading *position*,
never reachability. Exempt, exactly as the checker has it: `/Popup`, `/PrinterMark`, and `/F`
Hidden (bit 2) or NoView (bit 32). Annotations on pages that could **not** be re-marked are still
included: an `/OBJR` names an object, not an MCID. A `/Link` with no `/Contents` is still tagged,
with a warning that the description is caller-supplied and UA22 will keep reporting it.

**Pages are skipped, never forced.** A skipped page is left byte-for-byte untouched and named in
`SkippedPageReasons`. The reasons, all of them real refusals rather than failures:

| Skip reason | Why |
|---|---|
| Decoded content stream is larger than **4 MiB** (`ContentStreamTagger.DefaultMaxContentBytes`) | The pass builds three full parallel representations of the same bytes, so peak cost is a large *multiple* of the stream (~120× in working set, ~460× in total allocation) and linear. Core lets one stream inflate to 512 MiB from a few kilobytes of Flate, so without this ceiling a ~2.5 MiB PDF drove one page to tens of gigabytes — and paid the bill even for a page with no text. Checked **first**, before a byte is examined, because that is the only point at which refusing is free. 4 MiB is far above any real text page; pages past it are vector art or scans. Override per call with the public `AutoTagOptions.MaxPageContentBytes` (`0` disables it). The `SkipReason` names both numbers. |
| Decoded content stream is larger than **`MaxConcatenatedContentBytes`** (default 512 MiB) | The earlier ceiling: `/Contents` may be an array of arbitrarily many streams, each separately allowed to inflate to Core's 512 MiB, so the concatenation is otherwise unbounded from a tiny file. Consulted **before** `GetDecodedData()` whenever the decoded size is knowable for free — a `/DL` hint over the ceiling, or an unfiltered stream, whose decoded length *is* its raw length. A filtered stream that declares nothing cannot be predicted and is inflated once (capped by Core at 512 MiB) before being refused. The two refusals read differently: *"refused before being decoded"* vs *"measured after decoding"*. Override with `AutoTagOptions.MaxConcatenatedContentBytes`. |
| Page already contains marked content (`BDC`/`BMC`) | A second set of MCIDs would collide with the existing ones and mis-resolve the `/ParentTree`. |
| Content stream could not be decoded, or would not scan safely | Splicing into bytes that were not understood corrupts the page. |
| Inline image whose payload length cannot be computed exactly | The `EI` terminator is only certain with `/L` or for an unfiltered image; the fallback heuristic is provably wrong on all-ASCII payloads, so the page is refused rather than spliced at a guessed offset. |
| `BX`/`EX` compatibility section | The operators inside it are not necessarily standard. |
| Byte scanner disagrees with the production content-stream parser | Two independent readers must agree before the page is rewritten. |
| A text object mixes several roles while pushing/popping graphics state, or has unbalanced `q`/`Q` | Wrapping it in `BDC`/`EMC` would not be safely reversible. |
| Page draws no text | Nothing to tag. |
| Spliced stream failed its post-splice balance check | Fail closed: the original is kept. |

If **no** page could be marked, no structure tree is written at all and `ContentMarked` is
`false` — check it before treating the output as tagged. In that case **`/MarkInfo /Marked` is
not written either**, even when artifacts were marked: per ISO 32000-1 §14.7.1 `/Marked true`
asserts Tagged PDF conformance, which requires a structure tree, and every checker decides "does
this document claim to be tagged" from `/StructTreeRoot != null || /MarkInfo /Marked`. Writing it
would hide the true finding (UA01/UA05, "not tagged") and switch on the structure-dependent
checks, so an annotation on that page would be reported UA23 — remediation manufacturing a
violation while concealing the real one.

Text drawn inside a **Form XObject** *is* marked, with the form's own `/StructParents`,
its own MCID space, and structure elements whose `/K` is an `/MCR` carrying **`/Stm`**. A form that
**several pages share** is duplicated — one copy per invoking page, the first page keeping the
original object — because one stream dictionary carries one `/StructParents` and a shared form's
content could otherwise belong to only one page's parent tree. That is on by default;
`AutoTagOptions.DuplicateSharedFormXObjects` turns it off, and
`AutoTagResult.SharedFormsDuplicated` reports when it happened.

Duplication is **all-or-nothing**: a form any of whose pages cannot be repointed is left alone
entirely, because tagging the original while another page still drew it would give that page marked
content owned by a different page's structure. So these are still reported rather than tagged — a
form reached through *another* form; one whose stream will not decode or is over
`MaxPageContentBytes`; one already carrying marked content; one whose copies would exceed the
document's duplication budget; and one drawn by a page the tagger itself skipped, which has no
`/StructParents` for a copy's content to belong to.

Pages still holding unmarked form text are counted in `PagesPartiallyTagged`, warned about
individually, and called out in `ToString()`. The checker side *does* follow forms (cycle-guarded,
depth-capped), so **UA17** reports whatever the tagger could not reach and **UA24** does not
mislabel a page whose only text lives inside a form as image-only.

### Remediate / RemediateWithReport

```csharp
RemediationReport         PdfAccessibility.RemediateWithReport(Stream input, Stream output, RemediationOptions o = null);
AccessibilityAuditReport  PdfAccessibility.Remediate(Stream input, Stream output, RemediationOptions o = null);
```

`Remediate` is a thin wrapper that returns `RemediationReport.AfterAudit` and therefore discards
the per-step log, the warnings and the before/after comparison. Prefer `RemediateWithReport`.

> ### What the pipeline needs from you to reach zero errors
>
> Two findings cannot be closed from inside the document, so the pipeline asks for them:
>
> - **UA04 — document title.** Set `RemediationOptions.Title`. Nothing in the file can supply a
>   title that is not there, and the step will not invent one; a document whose title is in `/Info`
>   alone has it carried to the XMP `dc:title`, where ISO 14289-1 7.1 reads it.
> - **UA21 / UA25 — font embedding.** ISO 14289-1 §7.21.4.1 requires *every* font to be embedded and
>   grants the **standard 14 no exemption**, so a `/Helvetica` document — the common case for
>   machine-generated PDFs — fails until a font *program* is supplied. Set
>   `EmbedMissingFonts = true` and provide `FontEmbedding.FontProvider`.
>
> Given both, a measured end-to-end run on an untagged one-page `/Helvetica` document carrying a
> link annotation went **10 errors → 0**, with `IsConformingAfter == true` (and zero warnings, so
> `IsFullyConforming` too). Given neither, the residual is exactly UA04 + UA21 + UA25 and every
> one of them is reported.
>
> **Zero errors still is not a conformance certificate** — see the caveat at the top of this file.
> Alt-text *quality*, heading *meaning* and reading-order *sense* are not machine-checkable.

> ### Licensing: embedding a font redistributes it
>
> The font program travels inside every copy of the PDF. Many fonts installed on a machine —
> Arial and Times New Roman among them — are licensed under terms that do not permit that, and
> this library cannot read a font's licence, so it cannot decide for you.
> `FontEmbedding.FontProvider` is therefore the primary path: the caller knowingly chooses the
> program that ships. `FontEmbedding.AllowSystemFontSubstitution` — which authorises embedding a
> font installed on the host — **defaults to `false`**. **You are responsible for holding
> redistribution rights to whatever you supply or authorise.**
>
> With no font source at all the step **skips with that reason** and warns that UA21/UA25 remain
> open. It never reports success it did not achieve.

**Seven passes, in order** (nine named steps — the layout pass covers three). A "pass" is one
appended PDF revision. Every pass is skipped outright when it provably cannot change the document,
so a typical untagged file costs **two** revisions (AutoTag + XMP) and the worst case is seven
(nine with `SingleTextExtraction` off).

| # | Pass | Runs when |
|---|---|---|
| 1 | **Layout** — AutoTag, MarkArtifacts, FixReadingOrder | The only text-extracting pass. With `SingleTextExtraction` (default `true`) the three are tried in that order and only the first enabled *and* applicable one runs; the others are skipped with a stated reason. On an untagged document nothing is lost — AutoTag already emits header/footer artifacts and builds the tree in visual reading order. **A "superseded" skip is a claim, and it is made only from evidence:** `MarkArtifacts` and `FixReadingOrder` are told AutoTag did their work only when `AutoTagResult.ContentMarked` is true (a page's content stream really was re-marked), and the weaker "the single extraction was spent" skip requires a result at all, `PagesProcessed > 0`, and `RewriteContentStreams` not turned off. AutoTag returning without touching a page — unreadable page tree, no pages, `RewriteContentStreams = false`, or a step that threw — no longer silently costs the caller the other two steps. AutoTag itself is skipped when a `/StructTreeRoot` already exists, however hollow; the skip reason says so and names the escape hatch (`PdfAccessibility.AutoTag` called directly *does* supersede an existing tree). |
| 2 | **Document metadata** — `/Lang`, `/Info /Title`, `/ViewerPreferences /DisplayDocTitle`, `/MarkInfo /Marked` | One revision for all four. Entries already holding the wanted value are not rewritten. `/MarkInfo /Marked true` is written **only** when the structure tree reaches page content (the same `StructTreeReachesContent` test pass 7 uses) — claiming a document is tagged when it is not is worse than the violation it hides. |
| 3 | **Figure alt text** | Only when the structure tree contains a Figure that would be given text. |
| 4 | **Heading hierarchy** | Only when replaying the renumbering would change at least one level. |
| 5 | **Page tab order** — `/Tabs /S` (step name `SetTabOrder`, option `FixTabOrder`) | Only when at least one page carries annotations *and* does not already declare `/Tabs /S`. **This is what closes UA09**; before it existed the audit reported UA09 and no pipeline step wrote the key, so an annotated document failed that check for ever. Pages without annotations are never touched — PDF/UA-1 §7.18.3 requires the key only where there is a tab order to define — so a document with no annotations costs no revision. A page carrying the legacy `/TabOrder /S` this library once wrote counts as **not** fixed and gets a real `/Tabs /S`; the legacy key is left in place rather than deleted. |
| 6 | **Embed fonts** (step name `EmbedFonts`, option `EmbedMissingFonts`, default **off**) | Only when turned on *and* the document has a font carrying no program. Retrofits `/FontFile2` / `/FontFile3` onto the existing font dictionaries — **this is what closes UA21/UA25**. With no font source it skips with that reason and warns. See [Embed fonts](#embed-fonts). |
| 7 | **PDF/UA identifier (XMP)** | Last, so no later pass can invalidate it. Skipped when the existing packet is already byte-identical to the one it would write. **Refused outright, regardless of the option, in two cases:** the document has no structure tree, *or* it has one that **reaches no page content** (no `/ParentTree`, or no element holding an MCID, `/MCR` or `/OBJR`). A `pdfuaid:part` declaration is the document asserting conformance; stamping it on an untagged — or hollow-tagged — file replaces a true UA03 with a false claim, switches on the structure-dependent checks so remediation starts *introducing* findings, and bakes a conformance assertion into a file the same call audits as non-conformant. `/MarkInfo /Marked` in pass 2 is gated identically, by the same `StructTreeReachesContent` test. Both refusals record a skip reason **and** a warning. **Without this pass the pipeline can never produce a conformant file** — UA03 is unreachable by any structural fix. |

**Failure handling.** A step that throws is recorded as a failed `RemediationStepResult`, the
pre-step document is kept, and the pipeline carries on. The output document is **always** written,
even when every step is skipped. A step that produces fewer bytes than it was given cannot be an
incremental update, so it is treated as a failure and the previous revision is kept.

**A step turned off in options warns when that leaves a check open.** `AutoTagIfUntagged`,
`SetFigureAltText`, `FixTabOrder` and friends record not just *"not requested"* but, when the
snapshot shows the document actually needed it, a warning naming the check that stays open (UA05,
UA07, UA09 …) and the option to flip. `SkippedSteps` is therefore worth reading, not just counting.

`RemediationReport` exposes `BeforeAudit`/`AfterAudit`, `Steps` split into
`AppliedSteps`/`SkippedSteps`/`FailedSteps`, `AllWarnings`, `TotalItemsChanged`, `ErrorsFixed`,
`WarningsFixed`, `IsImproved`, `IsConformingAfter`, `InputLength`/`OutputLength`, plus
`ToString()` (one line) and `ToDetailedString()` (per-step log + remaining issues).

**File growth.** Each revision costs its rewritten objects plus a few hundred bytes of xref and
trailer. The XMP pass dominates for small files — a conformant packet with its spec-recommended
padding is roughly 3 KB regardless of document size — so a one-page document grows by a few
kilobytes and a real document by well under a percent.

### MarkHeadersAndFooters

```csharp
ArtifactMarkResult PdfAccessibility.MarkHeadersAndFooters(
    Stream input, Stream output, float headerMarginPt = 50f, float footerMarginPt = 50f);

ArtifactMarkDetail PdfArtifactMarker.MarkHeadersAndFootersDetailed(
    Stream input, Stream output, float headerMarginPt = 50f, float footerMarginPt = 50f);
```

Wraps text inside the top/bottom margin bands in
`/Artifact <</Type /Pagination /Subtype /Header|/Footer>> BDC … EMC` **in the content stream**.

**No structure elements are created.** An artifact is by definition content that is *not* part of
the logical structure (ISO 32000-1 §14.8.2.2), so an `/S /Artifact` structure element inverts its
meaning — assistive technology would then read out the page furniture it is supposed to skip.
Artifacts consume no MCID and never appear in the `/ParentTree`. `Artifact` is likewise absent
from the checker's set of standard structure roles, so a `/RoleMap` pointing at it is reported
(UA13).

Body content is left exactly as it was: tagging it is the auto-tagger's job.
`/MarkInfo <</Marked true>>` is set **only** when the document's structure tree actually reaches
page content — the same `StructTreeReachesContent` test the remediation pipeline uses, not merely
"a `/StructTreeRoot` resolves" — and a warning says so when it is withheld, so this pass and the
pipeline never gate one flag on different questions. Pages are skipped for the same reasons as AutoTag, plus a rotation
whose user-space top/bottom edges are not the visual ones, and margin bands that would swallow the
whole page.

Use the **`Detailed`** overload in anything user-facing: the narrow one reports a count, so "no
headers or footers were found" and "three pages were refused" both read as `0`.

**The counts describe the document that reached your output stream.**
`Core.Writing.IncrementalUpdateWriter` never throws for a recoverable condition — it reports and falls
back to emitting the original bytes — so the marker checks that the revision actually landed
(by counting the bytes written) before it reports anything. If it did not,
`ArtifactsMarked` and `PagesChanged` are `0`, `DocumentChanged` is `false`, the lost pages move
into `PagesSkipped` (so `PagesChanged + PagesSkipped + PagesWithNothingToMark == PagesProcessed`
still holds), and a warning names the cause. Read `Warnings` as well as the counts: it is where
"the revision never reached the output stream" and "`/MarkInfo /Marked` was withheld" are said.

### SetPdfUaIdentifier

```csharp
bool PdfAccessibility.SetPdfUaIdentifier(
    Stream input, Stream output, int part = 1, string title = null, string language = null);

// PdfUaXmpWriter adds file-path overloads and a modifyDate parameter:
bool PdfUaXmpWriter.SetPdfUaIdentifier(Stream in, Stream out, int part, string title, string language, DateTimeOffset? modifyDate);
bool PdfUaXmpWriter.SetPdfUaIdentifier(string inPath, string outPath, int part = 1, string title = null, string language = null);
string PdfUaXmpWriter.BuildPdfUaXmpPacket(string existingXmp, int part = 1, string title = null, string language = null, DateTimeOffset? modifyDate = null);
```

Writes the PDF/UA identification schema (`pdfuaid:part`) into the document's XMP metadata as an
appended revision, optionally setting `dc:title` and `dc:language` too. An existing packet is
**merged, not clobbered**; a packet that is not well-formed XML is replaced with a fresh one and a
diagnostic is reported, because refusing to write would leave the violation unfixable.

**This is the only operation that can close UA03.** No amount of structure-tree work satisfies it.

Output is deterministic — no timestamp — unless you pass `modifyDate`, so remediation output can
be compared byte for byte in tests. On failure the source bytes are copied through unchanged and
`false` is returned; read `PdfUaXmpWriter.LastError`. The revision matches the **form** of the
section it supersedes — classic table in, classic table out; xref stream in, `/Type /XRef` stream
out — which is why this routes through `Core.Writing.IncrementalUpdateWriter` rather than the legacy
whole-trailer copy: that path would append a classic table under a trailer carrying `/Type /XRef`,
`/W` and a stale `/Length`, chained by `/Prev` to an xref stream.

**It is idempotent — a second run costs nothing.** When the packet it would write is byte-for-byte
the one already in the file, the input is copied straight through and no revision is appended, so
re-running it over its own output produces byte-identical bytes instead of growing the file by a
no-op revision each time. The `bool` return still answers *"does the output declare
`pdfuaid:part`?"* (so it is `true` in that case); use **`PdfUaXmpWriter.LastCallAppendedRevision`**
to tell the two apart — `false` after a `true` return means "already correct, nothing written",
`false` after a `false` return means the write failed and `LastError` says why. Both are
`[ThreadStatic]`. `RemediateWithReport` makes the same comparison one level up and skips the step
with *"already satisfied: the document's XMP packet is byte-identical to the one this step would
write"*.

### Embed fonts

```csharp
FontEmbeddingResult PdfAccessibility.EmbedMissingFonts(
    Stream input, Stream output, FontEmbeddingOptions options = null);

// namespace Tetitulz.Pdf.Accessibility.Fonts
FontEmbeddingResult PdfFontEmbeddingRemediator.EmbedMissingFonts(Stream, Stream, FontEmbeddingOptions);
```

Retrofits a font program onto **existing** font dictionaries — which is what UA21/UA25 need, and
what `Base.Operations.PdfFontEmbedder` (which only ever added a *new* resource) could not do. It
writes `/FontFile2` for TrueType, `/FontFile3 /OpenType` for a CFF sfnt and `/FontFile3 /Type1C`
for a bare CFF, as one appended revision.

```csharp
byte[] program = File.ReadAllBytes("DejaVuSans.ttf");   // a font YOU may redistribute
var options = new RemediationOptions {
    Title             = "Quarterly Report",             // closes UA04
    EmbedMissingFonts = true,
    FontEmbedding     = new FontEmbeddingOptions {
        FontProvider        = request => program,       // closes UA21/UA25
        SubsetEmbeddedFonts = true,
    },
};
RemediationReport report = PdfAccessibility.RemediateWithReport(input, output, options);
// report.IsConformingAfter == true
```

**Glyphs never move.** An existing `/Widths` array is left exactly as it is, and a standard-14
font relying on the built-in AFM metrics gets a `/Widths` synthesised from those **original**
metrics (`Core.Fonts.Standard14Metrics`) — never from the substitute, because the content stream
was laid out against the originals. A font with no `/Encoding` is given `/WinAnsiEncoding` **and**
widths computed against that same table, so the two can never disagree; the resulting 0x27/0x60
glyph change is warned about. An existing `/Encoding` or `/ToUnicode` is never disturbed.

**Subsetting** (`SubsetEmbeddedFonts`, default `true`) runs only when the character set is
*provably* complete — every content stream that can reach the font was decoded and tokenised
without incident, Form XObjects followed cycle-guarded — and the dictionary can carry the
`ABCDEF+` subset tag (§9.6.4). Otherwise the whole program is embedded and a warning is recorded.
A subset missing a glyph the document shows is never emitted.

**Refusals, each with a stated reason in `FontEmbeddingResult.SkippedFonts`:** Symbol and
ZapfDingbats and any Symbolic-flagged font are never substituted (their codes address a built-in
encoding no text font reproduces); TrueType Collections (`ttcf`) and Type 1 PFA/PFB are refused by
format; a font with no `/Widths` that is not one of the standard 14 is refused rather than shifted.
An encrypted or unparseable document is copied through with a diagnostic and a warning.

See the [licensing note](#licensing-embedding-a-font-redistributes-it) — this is the one API here
whose output redistributes third-party material.

### Structure-tree editing and reading order

```csharp
SetAltTextResult PdfAccessibility.SetFigureAltText(Stream in, Stream out, Func<FigureContext,string> provider);
SetAltTextResult PdfAccessibility.SetFigureAltText(Stream in, Stream out, string altText);
int              PdfAccessibility.SetActualText(Stream in, Stream out, string elementType, Func<int,string,string> provider);
bool             PdfAccessibility.SetDocumentLanguage(Stream in, Stream out, string languageTag);
bool             PdfAccessibility.SetDocumentTitle(Stream in, Stream out, string title);
int              PdfAccessibility.FixHeadingHierarchy(Stream in, Stream out);   // H1→H3 becomes H1→H2
bool             PdfAccessibility.SetFieldTooltip(Stream in, Stream out, string fieldPartialName, string tooltip);
int              PdfAccessibility.SetStructureTabOrder(Stream in, Stream out);   // /Tabs /S on annotated pages; closes UA09

ReadingOrderAnalysis  PdfAccessibility.AnalyzeReadingOrder(string pdfPath);
ReadingOrderAnalysis  PdfAccessibility.AnalyzeReadingOrder(Stream input);
ReadingOrderFixResult PdfAccessibility.FixReadingOrder(Stream input, Stream output);
```

Each writes an incremental update, so they chain stream to stream. Text strings go out as
**UTF-16BE**, so a Hebrew or CJK title survives.

`SetStructureTabOrder` writes `/Tabs /S` on exactly the pages `PdfUaChecker`'s UA09 flags — those
with a non-empty `/Annots` — so a checker and a fixer can never disagree about scope. `/Annots` is
not inheritable, so the page's own entry is the whole answer. A page already carrying `/Tabs /S` is
skipped; a page carrying only the legacy `/TabOrder /S` (not a PDF key — no reader honours it) is
treated as unfixed and gets a real `/Tabs /S` written alongside, with the legacy entry left in
place. When no page needs the key, no revision is appended and the output is byte-identical to the
input. It is called for you by `RemediateWithReport` (`RemediationOptions.FixTabOrder`, default
`true`); call it directly only when driving the steps yourself.

**On `AnalyzeReadingOrder`, check `Analyzed` and `IsPartial` before trusting `IsOrdered`.** An
element is placed at the baseline Y of the first text-showing operator inside its marked-content
sequence; one whose MCID cannot be resolved is *excluded* from the comparison rather than guessed
at. A document where 2 of 100 000 elements resolved would otherwise report "ordered" — which is
"I examined 0.002% of your document and it looked fine". `ElementsPlaced` / `ElementsTotal` give
the real coverage. The order comparison itself is Y-descending with a ±1-slot tolerance, so a
single adjacent transposition (routinely produced by a line-based tagger) is not flagged.

**Neither `Analyze` nor `Fix` will rank a set it cannot prove is one column.** A bare baseline Y
cannot tell one column from two: the left and right columns of a two-column page interleave
perfectly in Y, and so do the cells of a table row with staggered baselines (a vertically centred
cell beside a two-line one). Ranking either by Y turns a **correct** `/K` order into a wrong one —
and `Fix` writes the result, so the wrong order becomes the one assistive technology announces.
Each element is therefore measured as a *band* (highest and lowest baseline, leftmost and
rightmost show origin), and a set is judged only when all three hold:

1. the parent's `/RoleMap`-resolved role is not one whose child order is semantic rather than
   visual — `Table`, `TR`, `L`, `LI`, … (a row's cells run left-to-right by meaning; an `LI`'s
   `Lbl` precedes its `LBody` on the same baseline);
2. no two children that share vertical space sit on different baselines (1 pt tolerance) —
   overlapping bands are content side by side;
3. the proposed order never steps back to the right after moving left — the signature of
   column-by-column content (1 pt tolerance).

A set that fails any of them **keeps the order it already had**, and the reason is reported through
`AccessibilityDiagnostics` and `PdfReadingOrderAnalyzer.LastError`. Declining costs a repair that
might have been right; permuting on insufficient evidence manufactures a defect in a file that was
correct. Recovering the *intended* sequence of a genuine multi-column page needs a layout-analysis
engine and is not implemented.

`FixReadingOrder` permutes `/K` arrays in place, pinning entries it cannot position and `/OBJR`s to
the slots they already occupy; it never adds, removes or re-parents an element.
`ReadingOrderFixResult.ElementsReordered` counts slots whose occupant actually changed — and only
once the appended revision has reached your output stream, so a non-zero count means the produced
bytes really are permuted. A document already in order is copied through **byte-identical**: no
revision is appended, and `LastError` carries no decline phrase, so "unchanged" is distinguishable
from "gave up". `ReadingOrderReadBackTests` asserts all three by re-opening the produced PDF and
reading the `/K` array back out.

### Diagnostics

```csharp
string PdfAccessibility.LastDiagnostic;              // [ThreadStatic]; null when the last op was clean
event Action<string> PdfAccessibility.DiagnosticReported;
```

Read `LastDiagnostic` after a "nothing happened" result — `0` figures updated, `false`, an empty
issue list — to tell *"there was nothing to do"* from *"something went wrong and was tolerated"*.
Subscribe to `DiagnosticReported` when one call can tolerate several failures, since the slot keeps
only the last. A subscriber that throws is caught and discarded: these events fire from inside
failure-tolerant paths, and a throwing logger must not turn a tolerated failure into a failed
operation.

`PdfUaXmpWriter.LastError` and `PdfReadingOrderAnalyzer.LastError` are the per-class
`[ThreadStatic]` slots for those two.

---

## Options

### AutoTagOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `Language` | `string` | `"en-US"` | `/Lang` to inject when the catalog has none. |
| `Title` | `string` | `null` | `/Info /Title`. Null leaves any existing title. |
| `H1Ratio` | `float` | `1.8` | Font size ÷ document median at or above which a run becomes `H1`. |
| `H2Ratio` | `float` | `1.4` | Same for `H2`. |
| `H3Ratio` | `float` | `1.15` | Same for `H3`. |
| `ParagraphGapThreshold` | `float` | `6` | Max vertical gap (user units) for consecutive `P` lines to merge into one paragraph. |
| `TagHeadersFootersAsArtifacts` | `bool` | `true` | Mark runs in the margin bands as `/Pagination` artifacts. |
| `HeaderFooterMargin` | `float` | `50` | Band depth in points from the top and bottom page edges. |
| `RewriteContentStreams` | `bool` | `true` | **This is what tagging means.** `false` writes no marked content *and* no structure tree. |
| `EmitActualText` | `bool` | `false` | Writes `/ActualText` on every element. Off by default because `/ActualText` **overrides** extracted text, and the text recoverable from raw string operands is correct only for simply-encoded Latin fonts — so this can replace correct text with mojibake. |
| `CompressRewrittenStreams` | `bool` | `true` | Flate-compress rewritten content. `false` leaves it readable for inspection. |
| `MaxPageContentBytes` | `int` | `4194304` (4 MiB) | Ceiling on the **decoded** content stream of a single page the tagging pass will *analyse*. On exceed the page is skipped **whole** — its bytes are left exactly as found, no MCID is minted, and `SkippedPageReasons` names the page, its size and this limit. `0` or less disables it. |
| `MaxConcatenatedContentBytes` | `long` | `536870912` (512 MiB) | Ceiling on the total decoded content *assembled* for one page — the earlier of the two, since `/Contents` may be an array of arbitrarily many streams each separately allowed to inflate to 512 MiB. Set it **below** `MaxPageContentBytes` to cap what is decoded, not just what is analysed: a stream that declares `/DL` or carries no filter is then refused *before* being decompressed. `0` or less means "use the default"; there is deliberately no way to ask for an unbounded concatenation. |

### RemediationOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `Language` | `string` | `"en-US"` | `/Lang` + XMP `dc:language`. Null/empty = leave alone. |
| `Title` | `string` | `null` | `/Info /Title` + XMP `dc:title`. `/DisplayDocTitle` is only set once a title exists. |
| `AutoTagIfUntagged` | `bool` | `true` | Run AutoTag when there is no structure tree. |
| `AutoTagOptions` | `AutoTagOptions` | defaults | Passed to the tagger. |
| `SetFigureAltText` | `bool` | `true` | Describe undescribed figures. |
| `AltTextFactory` | `Func<FigureContext,string>` | `null` | With no factory the step writes `"Figure 1"`, `"Figure 2"`, … **and raises a warning naming exactly that**: the placeholder closes UA07 structurally while describing nothing to a screen-reader user, so a document whose only real fault was an undescribed image must not come back `IsConformingAfter == true` with nothing said. The text is still written (an undescribed figure is a hard PDF/UA failure, and a placeholder at least carries the author's numbering) — never silently. When a factory *is* supplied the step runs for **every** Figure, including ones that already have text: only the factory can decide whether to replace a description. |
| `FixHeadingHierarchy` | `bool` | `true` | Renumber `H1`–`H6` so levels never skip. |
| `MarkArtifacts` | `bool` | `true` | Mark running headers/footers. |
| `HeaderMarginPt` / `FooterMarginPt` | `float` | `50` | Margin bands for artifact detection. |
| `FixReadingOrder` | `bool` | `true` | Reorder `/K` to match visual order. |
| `FixTabOrder` | `bool` | `true` | Write `/Tabs /S` on annotated pages that lack it — **the only step that closes UA09**. Pages with no annotations are never touched, so this costs nothing on a document without them. |
| `EmbedMissingFonts` | `bool` | **`false`** | Embed a program into every font the document does not carry — **the only step that closes UA21/UA25**. Off by default because embedding *redistributes* the font; see [licensing](#licensing-embedding-a-font-redistributes-it). Useless without `FontEmbedding.FontProvider` or `AllowSystemFontSubstitution`, and says so when skipped. |
| `FontEmbedding` | `FontEmbeddingOptions` | `null` | Font source and subsetting settings. Null means no font source is available, so every non-embedded font is reported as skipped with that reason. |
| `SetPdfUaIdentifier` | `bool` | `true` | The only step that closes UA03. Refused regardless of this flag when the document has no structure tree, or has one that reaches no page content — see pass 7. Turn it off when the structure is machine-generated and unreviewed: declaring conformance you have not verified is a stronger misstatement than omitting the identifier. |
| `PdfUaPart` | `int` | `1` | 1 or 2. Any other value fails the step rather than writing a meaningless declaration. |
| `XmpModifyDate` | `DateTimeOffset?` | `null` | Null keeps output byte-for-byte reproducible. |
| `SingleTextExtraction` | `bool` | `true` | Run only the first applicable layout step. Extraction is by far the most expensive thing the pipeline does. |

---

## Check codes

Severity drives conformance: **Error** clears `IsConforming`, **Warning** clears only
`IsFullyConforming`, **Info** is advisory.

**Which assembly owns which code, and the one trap.** The plugin owns `UA00`–`UA09`, `UA25`–`UA29`,
`UA50`–`UA61` and its own `UA99`; this assembly's deep checker owns `UA10`–`UA24` and `UA30`–`UA35`, plus its
own `UA00`, `UA06` and `UA99`. `UA35` is the deep checker's last code and new deep codes are allocated
upward from there, leaving `UA36`–`UA49` free; the plugin allocates its own checks
from `UA50` up, which is why `UA50` and not `UA30`. There is no gap-filler at `UA30` on the plugin
side — the band was reserved above the plugin's block precisely so the two never collide. The two
font checks exist on both sides **in opposite order**:

| Concern | Deep checker (this assembly) | Plugin (`PdfUaChecker`) |
|---|---|---|
| Glyph → Unicode mapping | **`UA20`** | **`UA26`** |
| Font embedding | **`UA21`** | **`UA25`** |

`UA28`, `UA29` and `UA50` are the only codes suppressed in a combined `Audit`; the font pair is
**not** de-duplicated, so a non-embedded font legitimately produces both `UA21` and `UA25`.

### Mostly dictionary-level — `Tetitulz.Pdf.Plugins.Conformance.PdfUaChecker`

All of these but `UA50` read dictionaries only; `UA50` decodes the page's own content stream, which
a plugin can do because it needs nothing beyond `PdfStream.GetDecodedData()` and the public
`Core.Text.ContentStreamParser`. The plugin ships and is validated standalone. In a combined
`Audit` they are surfaced as **Error** — **except this side's `UA99`, which is mapped to `Info`.**
`UA99` is not an ISO 14289-1 requirement: it reports that a check did not run. Sending it out as an
Error flipped `IsConforming` on a report that was merely *incomplete*, and its remediation text told
the reader to consult "ISO 14289-1 for check UA99", a clause that does not exist. The deep checker
emits the same code as `Info` for the same reason, and one combined report cannot apply two rules to
one construct. **Read a report carrying `UA99` as incomplete, whichever side raised it.**

| Code | Meaning |
|---|---|
| `UA00` | No document catalog — the file is malformed. |
| `UA01` | Not tagged: catalog `/MarkInfo /Marked` is not `true`. Indirect `/MarkInfo` references resolve correctly. |
| `UA02` | Catalog declares no natural language (`/Lang`). |
| `UA03` | XMP metadata does not declare `pdfuaid:part`. **Only `SetPdfUaIdentifier` closes this.** |
| `UA04` | No non-empty XMP `dc:title` in the catalog's Metadata stream (ISO 14289-1 7.1, which has a conforming reader ignore `/Info`, so an `/Info /Title` alone does not satisfy it; a `title` element of another namespace is not `dc:title`), or `/ViewerPreferences /DisplayDocTitle` is not true. On an **encrypted** document whose `/Metadata` packet cannot be decoded or parsed, the title half downgrades to `UA99` rather than asserting a violation about bytes the checker never read; the `DisplayDocTitle` half is still asserted. |
| `UA05` | Catalog has no `/StructTreeRoot`. |
| `UA06` | The security handler denies content extraction for accessibility. Encryption *itself* is permitted by PDF/UA-1; the remaining checks continue for an encrypted-but-accessible file. |
| `UA07` | Figure structure elements without `/Alt` or `/ActualText`. |
| `UA08` | **Terminal** interactive form fields without `/TU`. `/FT` is resolved through the `/Parent` chain (ISO 32000-1 Table 220 marks it inheritable), and a non-terminal group node is not counted — `/TU` is not inheritable, so a tooltip there reaches nobody. `PdfAccessibilityFixer.FixFieldsTooltips` uses the identical walk, so the checker and the fixer agree on which fields are in scope. |
| `UA09` | Pages carrying annotations without `/Tabs /S`. The key is **`/Tabs`** (ISO 32000-1 Table 30); `/TabOrder` is not a PDF key and no reader honours it, so a page carrying **only** the legacy spelling is **reported**, not excused — the message names how many such pages there are so the finding is actionable. `SetStructureTabOrder` writes the real key on exactly these pages. |
| `UA25` | A font used by the document is not embedded (§7.21.4.1). PDF/UA grants the standard 14 no exemption. Closed by the `EmbedFonts` pass — **only** when you supply a font program; see [Embed fonts](#embed-fonts). Deep-checker counterpart: `UA21`. |
| `UA26` | A font maps no glyphs to Unicode — no `/ToUnicode` and no Unicode-capable encoding (§7.21.7). Deep-checker counterpart: `UA20`. |
| `UA27` | An annotation has no accessible description. Two findings share the code: a visible annotation other than `/Link`, `/Popup`, `/Widget` and `/PrinterMark` with no `/Contents`; and a `/Link` with neither `/Contents` nor an enclosing `<Link>` structure element carrying `/Alt`. `/Widget` is covered by UA08's `/TU`; `/PrinterMark` is exempt (Matterhorn 28-004), and that exemption set is deliberately identical to `PdfDeepAccessibilityChecker.EnumerateAnnotations` and `PdfAutoTagger.BelongsInStructureTree`. Hidden / NoView annotations are skipped. |
| `UA28` | The structure tree has no `/ParentTree`, so no marked content resolves to an element. |
| `UA29` | Structure elements that reference no content. |
| `UA50` | A structure element names a marked-content identifier its page's content stream never defines, so the element describes nothing. The plugin's **only** content-decoding check, and the only direction that is unambiguous: the converse — text no MCID covers — needs form XObjects followed, so it stays with the deep checker's `UA17`. `/Pg` is inherited down the structure walk (Table 323); references carrying `/Stm` name another stream and are skipped and counted; a page that will not decode or exceeds the 8 MiB `MaxMcidScanBytesPerPage` ceiling raises `UA99` instead. Dropped from a combined `Audit`. |
| `UA51` | A TrueType font used for rendering breaks ISO 14289-1 7.21.6: a symbolic font with `/Encoding`, a non-symbolic one not MacRoman or WinAnsi or with `/Differences` names outside the Adobe Glyph List, a program `cmap` without the subtables asked. |
| `UA52` | A TrapNet annotation a reader is presented with (7.18.2). |
| `UA53` | A reference XObject: a form with `/Ref` (7.20). |
| `UA54` | An embedded CIDFontType2 whose `/CIDToGIDMap` is neither `/Identity` nor a stream (7.21.3.2). |
| `UA55` | A `/ToUnicode` value of U+0000, U+FEFF or U+FFFE in a font used for rendering (7.21.7). |
| `UA56` | A font subset whose `/CharSet` does not list exactly its program's glyphs, or whose `/CIDSet` leaves out a CID its program holds (7.21.4.2). |
| `UA57` | A font used for rendering paints a glyph its embedded program does not define (7.21.4.1). |
| `UA58` | An optional content configuration without a non-empty `/Name` once `/OCProperties /Configs` holds a configuration — the default included — or with an `/AS` entry, whatever `/Configs` holds (7.10). The same sentence as PDF/UA-2's §8.7, read from one place (`OptionalContentConfigurations`). |
| `UA59` | An embedded file's specification (one with `/EF`) without both `/F` and `/UF` (7.11), in the `/EmbeddedFiles` name tree or a file attachment annotation's `/FS` (7.18.7) — a hidden attachment too, since 7.11 is no annotation rule. The set PDF/A's A67 reads. |
| `UA60` | A dynamic XFA form: the config packet's `dynamicRender` element (`config/acrobat/acrobat7`) says `required` (7.15). A static XFA form is permitted. A packet that cannot be read, an encrypted one among them, raises `UA99`. |
| `UA61` | A media clip data dictionary (`/S /MCD`) without `/CT` and an `/Alt` multi-language text array (7.18.6.2: "In the media clip data dictionary, the optional CT and Alt keys … are required"). Every one the file holds is judged, whatever reaches it, by a bounded scan of its objects; dictionaries are never ciphertext, so an encrypted file is judged as a clear one is. |
| `UA99` | A check could not be completed; its findings are missing. |

`UA28`, `UA29` and `UA50` are **dropped unconditionally** from a combined `Audit` — `UA28`/`UA29` in
favour of the deep checker's `UA14`/`UA16`, `UA50` in favour of `UA16`–`UA18` — each of which
reports the same defect with a page, an object number and a structure path attached
(`SupersededByDeepCheck`). They are the only three suppressed on the code alone. `UA06` is
suppressed too, but **conditionally**: only when the deep half produced its own, since both halves
apply the same `/P` bit 10 rule and either may be the report's only copy. Read any of them directly
from `PdfUaChecker.Validate` if you call the plugin standalone.

### Content-level — `PdfDeepAccessibilityChecker` (this assembly)

| Code | Severity | Meaning |
|---|---|---|
| `UA10` | Error / Warning | Heading hierarchy, for both the numbered (`H1`…`H6`) and the PDF/UA "strong" (`<H>` nested in `<Sect>`) models — the mixed-model failure, a numbered skip, a first numbered heading other than `H1` (ISO 14289-1 7.4.2: "H1 shall be the first") and more than one `H` under any node (7.4.4) are errors; the strong model's depth skip, which no clause states, a warning. |
| `UA11` | Error / Warning | List structure: `L` → `LI` → `Lbl` + `LBody`, in that order (PDF/UA-1 7.6). |
| `UA12` | Error / Warning | Table structure: `Table` > `TR` > `TD`/`TH` nesting, `TH` addressability (`/Scope` or `/Headers`), and table **regularity** once `/ColSpan` and `/RowSpan` are resolved (Matterhorn 15-003). A `TH` with neither `/Scope` nor a referenced `/ID` is an error where some `TD` names no headers (7.5: the structure is then not determinable), a warning where every `TD` does. |
| `UA13` | Error / Warning | Role mapping: every `/S` must be standard or resolve through `/RoleMap` chains to a standard type, and standard types must not be remapped — an ISO 32000-1 type remapped, itself as the value included, is an error (7.1); a type only ISO 32000-2 made standard (`Title`), a custom type in a PDF/UA-1 file, a warning. `Artifact` is deliberately **not** a standard role. |
| `UA14` | Error / Info | `/ParentTree` coverage: every page's `/StructParents` and every annotation's `/StructParent` must exist in the tree and resolve to the right kind of object. |
| `UA15` | Warning | Figure alt-text *quality*: empty or placeholder `/Alt`. (Missing `/Alt` is UA07.) |
| `UA16` | Error | A **leaf** structure element references no content at all — no child element, no MCID, no `/MCR`, no `/OBJR`. Purely local: it says nothing about an element that *has* an element child, however hollow that child's subtree is — that is `UA30`. `TD`, `TH`, `Lbl`, `Private` and `Artifact` are exempt here, because an empty cell is the standard way to keep a grid rectangular. |
| `UA17` | Error | Text drawn inside **no** marked-content sequence — neither tagged nor marked as an artifact (§7.1). Counted per text-showing operator, not per page, so one artifact sequence cannot immunise a hundred untagged runs beside it. Follows Form XObjects. |
| `UA18` | Error | A page with structural content items but no `/StructParents`, in a document that claims to be tagged. A page whose content is entirely artifacts correctly has none and is not reported. |
| `UA19` | Error | `/MarkInfo /Suspects` is true — the document itself declares its tagging may not reflect the content. |
| `UA20` | Error / Warning | Text that can produce no Unicode: no `/ToUnicode` and no usable encoding. Plugin counterpart: `UA26` (**not** UA25). |
| `UA21` | Error | A font is not embedded — the standard 14 included, for which §7.21.4.1 grants no exemption. Plugin counterpart: `UA25` (**not** UA26). Closed by the `EmbedFonts` pass, but **only** when you supply a font program (`FontEmbedding.FontProvider`, or `AllowSystemFontSubstitution` at your own licensing risk); with no font source the step skips and this stays open. See [Embed fonts](#embed-fonts). On an encrypted document the exemption for a font drawn only in an invisible text-rendering mode cannot be applied, so this check is *weakened* rather than skipped. |
| `UA22` | Error | A Link annotation with no `/Contents` alternate description (Matterhorn 28-005). An `/Alt` on the enclosing Link *structure element* is a different entry and does **not** satisfy this — matching independent checkers, and deliberately stricter than the plugin's UA27. |
| `UA23` | Error | An annotation with no `/StructParent`, or not referenced by any `/OBJR`, so it has no position in the reading order. |
| `UA24` | Warning | A page draws images and no text at all — almost certainly a scan. Skipped rather than reported when any part of the content could not be read, so the finding never rests on a count known to be incomplete. |
| `UA30` | Error | A structure **subtree** that reaches no content anywhere beneath it — no MCID, no `/MCR`, no `/OBJR`, at any depth. This is the transitive question `UA16` cannot ask: a `Document → Sect → Sect …` chain, or two elements whose `/K` point at each other, has an element child at every node and reaches content at none, so it looks fully populated in a structure viewer and describes nothing. Only the **topmost** element of each hollow subtree is reported, with the element count of the branch in `InstanceCount`; a hollow *leaf* is left to `UA16`. The `RolesAllowedToBeEmpty` exemptions do **not** apply — a `<TR>` of nothing but empty cells is a row describing no content. |
| `UA31` | Error | Structure element nesting: the parent and child rules ISO 14289-1 7.2 takes from ISO 32000-1 Tables 337–340 — `TR`, `THead`, `TBody`, `TFoot`, `TH`, `TD`, `LI`, `LBody` and `TOCI` under their own parents; `Table`, the row groups, `TOC` and `L` holding only their own children. A non-standard child is left to `UA13`. |
| `UA32` | Error | Annotation nesting: a widget annotation's `/OBJR` is held by an element that means `Form`, a link annotation's by one that means `Link` (ISO 14289-1 7.18.4, 7.18.5) — the role resolved through `/RoleMap`, hidden and off-page annotations out of scope as for `UA23`, which keeps an annotation no `/OBJR` names. |
| `UA33` | Error | An element that means `Formula` without an `/Alt` that says something (ISO 14289-1 7.7: "shall have an Alt attribute"). 7.7 names `/Alt` alone, so an `/ActualText` does not satisfy it. On an encrypted document a present `/Alt` is accepted and the report says its emptiness was not judged. |
| `UA34` | Error | An element that means `Note` whose `/ID` is missing, not a byte string, empty, or carried by another structure element too (7.9: "Each note tag shall have a unique entry in the ID key"). On an encrypted document a present `/ID` is accepted and the report says why: one identifier in two objects encrypts to different bytes. |
| `UA35` | Warning | A link annotation a reader is shown whose URI action — its `/A` or one on its `/Next` chain — has `/IsMap true` (7.18.5). A warning: the clause permits it when the same function is provided elsewhere without IsMap, which a person must confirm. |
| `UA99` | Info | **A check did not run, so its findings are missing from this report.** Never a statement about the document. Four distinct producers: (a) a check that threw; (b) the encrypted-document coverage notice — one issue naming exactly what encryption cost *this* document; (c) a page whose content stream could not be decoded, naming the page, the object number, the cause and each check lost (`UA14`'s MCID mapping, `UA17`, `UA18`, `UA24`); (d) a page scanned only in part — an unreadable Form XObject, or a `BDC` naming a property list absent from `/Properties` — which additionally names the checks that *did* run "from counts that are lower bounds". Info, not Error, because the document has not been shown to be wrong, only not fully examined. **A clean result for a page named here means "not established", not "nothing wrong".** |

Up to 8 occurrences of one finding are reported individually, fully located (page, object number,
structure path). Beyond that they collapse into one issue carrying `InstanceCount`, so a
pathological document cannot flood the report while `TotalInstanceCount` still reflects reality.

Every issue carries `MatterhornCheckpoint` (for lining up with a Matterhorn-based checker) and `WcagCriterion` where the
mapping is unambiguous, plus `PageNumber`, `ObjectNumber` and `StructPath`
(`"Document[1]/Sect[2]/Table[1]/TR[3]/TD[1]"`) so a remediation UI can navigate to the defect.

---

## Worked remediation example

```csharp
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Models;

using var input  = File.OpenRead("scanned-report.pdf");
using var output = File.Create("remediated.pdf");

var report = PdfAccessibility.RemediateWithReport(input, output, new RemediationOptions
{
    Language = "en-US",
    Title    = "Q3 Financial Report",
    // Write a real description instead of the default "Figure N".
    AltTextFactory = ctx => $"Chart {ctx.FigureIndex + 1} on page {ctx.PageIndex + 1}",
    // SetPdfUaIdentifier is true by default and is what actually closes UA03.
});

Console.WriteLine(report);                      // before -> after, one line
Console.WriteLine(report.ToDetailedString());   // per-step log + remaining issues

// A step that did nothing always says why. Read these: "skipped" is not "succeeded".
foreach (var step in report.SkippedSteps)
    Console.WriteLine($"skipped  {step.Name}: {step.SkipReason}");

foreach (var step in report.FailedSteps)
    Console.WriteLine($"FAILED   {step.Name}: {step.SkipReason}");

// What is still wrong, with the fix for each.
foreach (var issue in report.AfterAudit.Issues)
    Console.WriteLine($"{issue.Severity} {issue.Code} p{issue.PageNumber}: {issue.Message}\n  -> {issue.Remediation}");
```

`ToDetailedString()` on an untagged single-page Helvetica document — a heading, two sub-headings,
body text, a running header and footer, and one link annotation — with a title and a font provider
supplied. This is real measured output, not a sketch:

```
Remediation: 10 -> 0 error(s), 0 -> 0 warning(s); 5 step(s) applied, 4 skipped, 0 failed; 12 item(s) changed; 1376 -> 48837 bytes

Steps:
  AutoTag: applied, 7 item(s) changed
  MarkArtifacts: skipped (superseded: AutoTag already marked running headers and footers as artifacts in the same pass)
  FixReadingOrder: skipped (superseded: AutoTag already built the structure tree in visual reading order)
  SetDocumentMetadata: applied, 1 item(s) changed
  SetFigureAltText: skipped (not applicable: the structure tree contains no Figure elements)
  FixHeadingHierarchy: skipped (already satisfied: the 3 numbered heading(s) already form an unbroken level sequence)
  SetTabOrder: applied, 1 item(s) changed
  EmbedFonts: applied, 2 item(s) changed, 2 warning(s)
      ! Font /F1 (Helvetica) [Type1] on page 1 declared no /Encoding, so its implicit encoding was made
        explicit as /WinAnsiEncoding when the program was embedded. The synthesised /Widths were computed
        against that same encoding, so advance widths are unchanged; codes 0x27 and 0x60 select
        quotesingle and grave rather than quoteright and quoteleft.
      ! Font /F2 (Helvetica-Bold) [Type1] on page 1 ... (same)
  SetPdfUaIdentifier: applied, 1 item(s) changed

Remaining issues (Accessibility Audit: 0 error(s), 0 warning(s), 0 info — NO BLOCKING ISSUES DETECTED (not a PDF/UA-1 conformance claim)):
  (none)
```

Five steps ran, not nine: a step that provably cannot change the document costs no revision at all.
The file grew from 1.4 KB to 48 KB because two font programs were embedded — subsetted, from a
1 MB source face.

**Drop `Title` and the residual is UA04. Drop `FontEmbedding` and it is UA21 + UA25.** Both are
reported, and the `EmbedFonts` step says exactly why it could not run. **Do not stop at a clean
report either:** review the generated structure in a tag-tree viewer or an independent checker and have a human
check that the alt text, heading levels and reading order actually say what the document means —
the checker cannot. The banner above says `NO BLOCKING ISSUES DETECTED`, not `CONFORMING`, for
this reason.

### Driving the steps yourself

Each operation writes an incremental update, so they chain stream to stream:

```csharp
using var ms1 = new MemoryStream();
PdfAccessibility.AutoTag(File.OpenRead("input.pdf"), ms1);

ms1.Position = 0;
using var ms2 = new MemoryStream();
PdfAccessibility.SetFigureAltText(ms1, ms2, ctx => $"Figure {ctx.FigureIndex + 1}");

ms2.Position = 0;
using var ms3 = new MemoryStream();
PdfAccessibility.SetPdfUaIdentifier(ms2, ms3, part: 1, title: "Quarterly Report", language: "en-US");

ms3.Position = 0;
Console.WriteLine(PdfAccessibility.Audit(ms3));
```

---

## Encrypted documents

| Operation | Encrypted input |
|---|---|
| `PdfUaChecker.Validate` (UA00–UA09, UA25–UA29, UA50–UA61, UA99) | **Runs every check but `UA50`, which is a known hole (GAPS.md §5c): it iterates `doc.Pages`, which is empty for an encrypted document, so it reports neither a finding nor a `UA99`.** Dictionary structure is never encrypted — only string and stream *contents* — so the structural checks stay meaningful. It raises `UA06` **only when the handler cannot be shown to permit accessibility extraction** (standard handler, `/P` bit 10; `/R` ≤ 2 and `/R` ≥ 6 are permitted by rule, and a non-standard `/Filter` or an unreadable `/P` is reported because it cannot be verified). An encrypted-but-accessible document gets **no** UA06 from this side. **UA03 and UA04 are both downgraded, on the same rule.** The XMP packet is ciphertext, so a check that cannot be decided from it is reported as `UA99` ("could not be verified") rather than as a violation. The exemption is deliberately narrow and identical for both: **only** a decode failure or an `XmlException` counts as unreadable. A missing `/Metadata` entry and an empty packet are facts about dictionary structure and length, which are never ciphertext, so they still yield a plain UA03/UA04 — encryption is not a blanket amnesty. The `/ViewerPreferences /DisplayDocTitle` half of UA04 is a boolean and is always asserted. UA58, UA59 and UA61 read dictionaries and run in full; UA60's XFA config packet is a stream, so on an encrypted document it is `UA99` ("could not be verified"). |
| `Remediate` / `RemediateWithReport`, and every writer | **All nine steps run in place** when `RemediationOptions.EncryptedRevisionWriter` is set — one lambda mapping to `Encryption.PdfEncryptedIncrementalUpdate.Run`. This assembly still references no crypto and contains no occurrence of "password": the *operation* travels to Encryption, not the other way round. Without a writer the pipeline skips every step with a reason naming the option, exactly as before. **One pipeline, not two** — only the step *action* is substituted, so an encrypted run takes the same requested/applicable/superseded branches as a clear-text one, and the suite asserts the two agree on which steps applied. Each step is its own decrypt/append cycle: composing nine plans against one decrypted copy would need steps 2..9 to see changes that exist only as staged objects, and several revise the same catalog. |
| `PdfDeepAccessibilityChecker.Check` (UA10–UA24, UA30–UA35) | **Runs every check that does not need plaintext.** It applies the **same `UA06` rule as the plugin, in the same order** — the two implementations are documented as "change together" — and continues the audit either way. Because `PdfDocument.Load` loads neither catalog nor pages for an encrypted file, it resolves the catalog from the trailer `/Root` and walks `/Pages` by hand (depth- and node-capped, cycle-guarded); without that every page-scoped check would silently have seen a zero-page document. UA33 and UA34 judge only that a Formula's `/Alt` and a Note's `/ID` are present, since an encrypted string's emptiness and equality depend on the cipher, and the report's encryption notice says so; UA35 reads booleans and names and runs in full. |
| `AutoTag`, `MarkHeadersAndFooters`, `FixReadingOrder`, every `PdfStructureTreeEditor` method, `SetPdfUaIdentifier`, `EmbedMissingFonts`, `RemediateWithReport` | **Refused.** The document is passed through unchanged and the reason is recorded — in `Warnings`, in `RemediationReport.SkippedSteps`, and on the diagnostics sink. |

**What the deep checker cannot do on an encrypted file is named once, in a single `Info` issue
(`UA99`)**, built from what actually happened to *that* document: **UA15** (alt-text quality),
**UA17** (text outside marked content), **UA18** (`/StructParents` coverage), **UA20** (Unicode
mapping) and **UA24** (image-only pages) are not attempted, and two checks are weakened —
**UA21**'s exemption for a font drawn only in an invisible rendering mode cannot be applied, and
**UA12**'s `/Headers`↔`/ID` matching cannot hold because strings are encrypted per object. Read a
clean encrypted report as *"no structural defects found"*, not *"no defects found"*.

**The `UA06` pair is collapsed, conditionally.** Both halves *produce* one for a document that
**denies** extraction — the deep checker is a public API in its own right and cannot assume the
plugin ran — and a combined `Audit` reports **exactly one**: the deep checker's, which alone carries
the WCAG criterion and a remediation naming the permission to grant. The collapse is **not** a
`SupersededByDeepCheck` entry, and that is the point: that predicate sees only a code, so it would
drop the plugin's `UA06` even on a run where the deep half never reached its encryption verdict,
turning a double-count into a lost finding. `AuditBytes` drops it only where the deep half really
produced one. Calling `PdfDeepAccessibilityChecker.Check` or `PdfUaChecker.Validate` on its own is
unaffected — each still reports its own.

Every editor here appends a **clear-text incremental update**, which would not match the
document's encryption dictionary. **To remediate an encrypted PDF, use the round trip** — it
decrypts, runs the whole pipeline over the plaintext and re-encrypts:

```csharp
using Tetitulz.Pdf.Encryption;

var result = PdfEncryptedRoundTrip.Run(input, output,
    new EncryptedRoundTripOptions { OpenPassword = "…" },
    (plain, produced) => PdfAccessibility.RemediateWithReport(
        plain, produced, new RemediationOptions { Title = "Quarterly Report" }));
```

The fourth argument is an `Action<Stream, Stream>`, so `RemediateWithReport` needs the lambda; the
bare method group will not convert, because it returns a `RemediationReport`.

**Do not hand-roll `PdfDecryptor` → remediate → `PdfEncryptor`.** That is what this call does, and
doing it yourself gets all three of its limits wrong silently: the **owner password is not
preserved** unless you choose `ReEncryptionPolicy.KeepOriginal` (`RoundTripResult.OwnerPasswordChanged`
says which happened), a **signed** document is
refused unless you set `AllowSignatureInvalidation`, and an encryption scheme the library cannot
reproduce **fails with a reason** rather than falling back to the `AES128` default.
`EncryptedRemediationRoundTripTests` pins the whole path end to end.

Encrypting the appended revision **in place** — which is what lets a remediated signature and the
owner password survive — *is* wired up: every writer exposes a `Plan` that stages objects instead of
writing them, and all nine `RemediateWithReport` steps run against an encrypted document through
`RemediationOptions.EncryptedRevisionWriter`. **Choose between the two on what you need to keep:**
the round trip above rewrites every byte, so it preserves neither the signature nor the owner
password; the in-place route preserves both but appends one revision per step. A direct call to a
writer's `Stream`→`Stream` overload still passes an encrypted document through unchanged — the
`Plan` overloads are the encrypted entry point. See [`GAPS.md`](../../../GAPS.md) §5a and §5c.

---

## Internal architecture

Most of these are `internal` to this assembly (`namespace Tetitulz.Pdf.Accessibility.Internal`),
with `InternalsVisibleTo("Tetitulz.Pdf.Accessibility.Tests")`. Three rows are not. `PdfObjectIndex`
and `VerifiedWrite` are **public**, in `Tetitulz.Pdf.Base.Operations` — they moved *down* out of this
assembly so the Conformance plugin can reach them, since `Accessibility` → `Conformance` already
exists and the reverse edge would close a cycle. `IncrementalUpdateWriter` is **Core''s** and always
was; see the section below for what actually left this assembly.

| Type | Role |
|---|---|
| `AccessibilityDiagnostics` | The feature's diagnostics sink: `[ThreadStatic] LastError`, `OnEvent`, `Report(op, detail, ex)`, `Clear()`. Surfaced publicly on `PdfAccessibility`. |
| `PdfObjectIndex` | One build per operation turns "which object number is this?" from an xref walk into a dictionary probe — an xref walk would be O(objects) *inside* per-page loops. Also allocates/reserves new object numbers and maps page index ↔ object number. |
| `StructTreeWalker` / `StructNode` | The single cycle-safe structure-tree traversal (global visited set, depth and node caps). Unguarded, a `/K` cycle with branching factor 2 would cost 2^depth visits — a hang, not a slowdown. Owns `GetRole`, `IsHeading`, `HeadingLevel`. |
| `ContentStreamScanner` | Tokenises a content stream into operators, operands and **byte ranges**, and reports what it found: marked content, compatibility sections, inline images (including the uncertain-length case), XObject invocations, text objects. |
| `ContentStreamTagger` | Classifies runs and splices `BDC`/`EMC` at the scanner's byte offsets. Owns `DefaultClassifier`, the per-page size ceilings (`DefaultMaxContentBytes` = 4 MiB for the tagging pass; `MaxConcatenatedContentBytes` = Core's `PdfLimits.MaxDecompressedBytes` for the decode — both now surfaced publicly on `AutoTagOptions`) and `ConcatenatePageContent` (the assembly's single `/Contents` decoder — one stream or an array, with the mandated whitespace between parts, refusing an over-ceiling stream *before* decoding it whenever `/DL` or an absent filter makes the size knowable). |
| `Base.Operations.VerifiedWrite` (**public**, in Base) | The single "did the appended revision actually reach the output?" answer: it performs the incremental write through a byte-counting wrapper and returns whether more than the original bytes were accepted, plus the cause when not. Every reporting write site consults it, which is what makes *reported count > 0 implies output != input* hold uniformly. It never composes or reports a message — each caller owns its own wording and its own warning channel. |
| `PageContentRewriter` | Applies a tagged stream to a page: new content object, `/StructParents`, and the modified/new object sets the writer will emit. |
| `Core.Writing.IncrementalUpdateWriter` | **Core's** incremental writer, and the library's only one. Handles streams, real generation numbers, `/ID` (preserve or synthesise), `/Prev` from the parser's start-xref offset, a whitelisted trailer (`/Root`, `/Info`, `/ID`, `/Encrypt`), and emits an xref **stream** when the original used one. |

### There is one incremental writer, in Core

`Core.Writing.IncrementalUpdateWriter` serves this assembly (`PdfAutoTagger`, `PdfArtifactMarker`,
`PdfStructureTreeEditor`, `PdfUaXmpWriter`), the ~25 `Tetitulz.Pdf.Base.Operations` call sites, and
the Conformance plugin's `PdfAccessibilityFixer` / `PdfAConverter`. `grep -c "class
IncrementalUpdateWriter"` over the tree returns **1**.

> **Why it lives in Core rather than here.** The Conformance plugin cannot call an `Accessibility`
> type, because `Accessibility` → `Conformance` already exists and the reverse edge would close a
> **cycle**. That is a fact about this one pair, not a general ban on plugin → feature references —
> see CLAUDE.md, "The reference invariant is acyclicity, not direction". The shared code went *down*
> into Core, which is what made one writer possible.

**No hand-rolled copy.** A private incremental writer is where the classic defects live: a
`PdfStream` value serialised as the literal text `null`, generation 0 hardcoded in the object header
and the xref entry, `/ID` omitted, strings encoded through Latin-1 so Hebrew and CJK `/TU` tooltips
and `/Alt` text become `?`, a byte-scanned previous `startxref` writing `/Prev 0` when the scan fails.
Every path here serialises through `Core.Writing.PdfWriter` and takes `/Prev` from
`PdfDocument.Parser.StartXrefOffset`.

**The revision keeps the document's format.** `Core.Writing.IncrementalUpdateWriter`'s format-aware
overload — `WriteUpdate(byte[], Stream, IReadOnlyList<IndirectObject>, IncrementalUpdateOptions)` —
classifies the section at the previous `startxref` and appends a revision **in the same form**:
classic in → classic table out, xref-stream in → `/Type /XRef` stream out (`/W [1 n 2]`, subsection
`/Index`, Flate with an uncompressed fallback when compression would inflate), plus a whitelisted
`/Root /Info /ID /Encrypt` trailer and a deterministic SHA-256-derived `/ID`. The revision is buffered
and copied out only once complete, so a mid-write failure leaves the original intact. The
six-argument `WriteUpdate` writes a classic table, and `PdfAccessibilityFixer` uses the format-aware
overload, so it appends a valid xref-stream revision to an xref-stream document.

So both the feature and the plugin reach the same writer, through Core, and neither carries a copy.

Public sub-namespaces: `Checking` (`PdfDeepAccessibilityChecker`), `Tagging` (`PdfAutoTagger`),
`Editing` (`PdfStructureTreeEditor`), `Artifacts` (`PdfArtifactMarker`), `ReadingOrder`
(`PdfReadingOrderAnalyzer`), `Fonts` (`PdfFontEmbeddingRemediator`, `FontEmbeddingOptions`,
`FontEmbeddingResult`, `FontEmbeddingSkip`, `FontEmbeddingRequest`), `Metadata`
(`PdfUaXmpWriter`), `Models`.

---

## Limitations

Beyond the six headline constraints at the top:

- **Font embedding needs a font program from you.** `EmbedFonts` closes `UA21`/`UA25`, but only
  from a program supplied through `FontEmbedding.FontProvider` (or, at your own licensing risk,
  `AllowSystemFontSubstitution`). With no font source the step skips and says UA21/UA25 remain
  open. See [Embed fonts](#embed-fonts).
- **Subsetting covers sfnt TrueType, OpenType with CFF outlines (`OTTO`) and bare CFF.** A Type0
  composite font is embedded whole (its codes are CIDs, so the glyph set cannot be read from the
  content stream here), as is any font whose used-text set is not provably complete or whose
  dictionary is not an indirect object (no `ABCDEF+` tag could be written); each case is noted in
  `FontEmbeddingResult`.
- **`AllowSystemFontSubstitution` resolves through SkiaSharp**, so what it finds depends on the
  host's installed fonts; a non-standard-14 family is refused unless the resolved family name
  matches the request, so an unknown family can never silently embed the platform default.
- **Form XObjects are re-tagged, and shared ones are duplicated per page.** What is still not
  reached: a form invoked from *inside another form* by any of its pages, one whose copies would
  exceed the duplication budget, and one drawn by a page the tagger skipped. The checker *does*
  follow forms (cycle-guarded, depth-capped), so it reports whatever is left.
- **No link or `H4`–`H6` inference.** The classifier's vocabulary is `P`, `H1`–`H3`,
  `/Pagination` artifacts and — opt-in — `LBody`, `TH`, `TD` and `Figure`.
- **Lists are inferred only when asked for** (`AutoTagOptions.InferLists`, default off). A run of
  consecutive lines that each begin with a marker and share a left edge becomes `/L` → `/LI`
  → `/LBody`; `AutoTagResult.ListsInferred` reports how many. It is off by default because a
  wrongly-inferred list puts an `/L` into the tree that a screen reader announces as a list of the
  wrong length, and the deep checker in this same feature then reports the shape — remediation
  manufacturing the violation it exists to remove.
  - **No `/Lbl`.** Splitting the marker from the body means splitting one line's marked content
    inside a show operator, which the splicer will not do; §7.6 is satisfied by an `/LI`
    carrying a body alone, so the marker stays in the body text.
  - **No nesting.** A deeper run has a different left edge, so it becomes a second list beside the
    first rather than one inside it. A shallow list is not a wrong list.
  - **A single-byte-encoded bullet is missed.** The line text is decoded through PDFDocEncoding
    without consulting the font's `/Encoding`, so a `/WinAnsiEncoding` bullet (byte `0x95`) arrives
    as U+0141. Matching that would be guessing at an encoding — it is a real letter, and the
    byte means something else under MacRoman. Ordinals and ASCII markers are unaffected.
- **Tables are inferred only when asked for** (`AutoTagOptions.InferTables`, default off). Two or
  more consecutive rows whose cells start at the same set of X positions become `/Table` → `/TR` →
  `/TH`|`/TD`; `AutoTagResult.TablesInferred` reports how many. Off by default for a sharper version
  of the reason lists are: a `/TR` may contain only `/TD` and `/TH` and every row must span the same
  number of columns, and the deep checker reports a table that fails either as an **Error**.
  - **Marked content becomes finer than a line, which nothing else here does.** A row of three cells
    drawn in one `BT…ET` is one line to every other pass; it is split into one sequence per show
    operator. A page whose text objects mix cells with `q`/`Q` can therefore hit the existing
    "several structure roles and also pushes/pops the graphics state" refusal where the same page
    tagged as paragraphs would not.
  - **A cell drawn with two show operators is not detected.** Its second show sits at the first's X
    plus the width of the text between, so it lands somewhere different on every row and the column
    set stops matching. The run breaks and no table is claimed.
  - **A row with a missing cell is refused, not padded.** Padding invents a cell; not padding makes
    the table ragged, which is the Error above.
  - **The first row becomes `/TH` with `/Scope /Column`.** Nothing recorded distinguishes a header
    row from a data row, but a table with no `/TH` is a finding and a `/TH` with no `/Scope` is
    another. `RunCandidate.TableRowIndex` and `TableColumnIndex` reach the classifier, so a caller
    who knows their corpus can answer otherwise.
  - **A two-column table of contents is a known false positive.** A caption and a page number
    repeated down the page is a grid by every measure available here, and becomes a `/Table` rather
    than a `/TOC`. Valid and navigable; poorer semantics.
- **Figures are inferred only when the caller can describe them**
  (`AutoTagOptions.FigureAlternateText`, default null). Each image on the page is offered as a
  `FigureCandidate` — page index, resource name, pixel width and height — and the string returned
  becomes the `/Figure`'s `/Alt`. `AutoTagResult.FiguresTagged` and `FiguresDeclined` report both
  halves.
  - **A declined image also raises a warning**, so it reaches
    `RemediateWithReport`'s step report as well as `FiguresDeclined` — the report surfaces warnings,
    not the `AutoTagResult`. It never fires for a caller who did not supply the callback.
  - **Returning null or empty means "do not tag this image", and that is the designed answer.**
    ISO 14289-1 §7.3 requires a `/Figure` to carry an `/Alt`, and an empty one is its own UA15
    finding — so a tagger that emitted the element regardless would manufacture the violation it
    exists to remove. An undescribed image stays untagged, which is where it started.
  - **Asked once per occurrence, not once per resource.** The same logo in a header and a footer
    arrives twice, because those may want different text or one of them may want declining. Cache
    on the (`PageIndex`, `XObjectName`) pair if you want one answer per picture — the name alone is
    only unique within a page.
  - **`FigureCandidate` carries no `PdfDictionary`.** This assembly's public surface names no type
    from the object model, and a callback about a picture should not make its author compile against
    the parser. A caller that wants the samples has the input document and
    `PdfImageReplacer.FindImages`.
  - **A callback that throws costs its own image and nothing else.** It runs mid-splice, so letting
    the exception out would abandon every paragraph on the page too. It is reported through
    `PdfAccessibility.LastDiagnostic`.
  - **Only `/Subtype /Image`.** A Form XObject is a container of more content, not a picture; one
    whose `/Subtype` cannot be read is not treated as an image either, because leaving content
    untagged is recoverable and describing a page of text as a picture is not.
  - **Images inside a Form XObject are offered too**, resolved against the form's own
    `/Resources` — falling back to the page's when the form declares none, which §8.10.1 makes the
    conformant reading.
  - **A form several pages draw reports `PageIndex == -1` and every page in `PageIndexes`.** It is
    tagged once and its bytes shared by every copy, so there is no single page to name; naming the
    first invoking page would be false rather than absent. `PageIndexes` lists them all, so a caller
    keying a cache on (page, name) can enter the picture under each.
  - **A form several pages draw AND declaring no `/Resources` is offered the names its pages
    agree on.** §8.10.1 makes its names resolve against whichever page draws it; a name every
    drawing page binds to the same object (equal references, or resources inherited from the page
    tree) has one answer and is offered, and a name two pages bind to different images, or one a
    page lacks, has none — it is left out and reported.
  - **The page's own resources are read through `PdfPage.Resources`**, which walks the `/Pages`
    inheritance §7.7.3.4 allows. Reading `page.Dictionary[/Resources]` instead finds nothing on a
    page that inherits, and offers no images at all.
  - **An image already inside somebody's marked content, or inside a `BT`…`ET`, is not offered.**
    The first is already tagged; the second shares bytes with text runs being spliced by offset, and
    a second sequence there could not be guaranteed properly nested.
- **A page that already carries marked content is completed rather than skipped**, when its
  marked content is readable exactly and the document's structure tree claims none of it. The pass
  starts numbering one past the greatest MCID already on the page, leaves the existing sequences
  untouched, and writes an element for each so nothing it adopted becomes an orphan. An adopted
  sequence keeps the tag its producer wrote — §14.6.1 makes that the structure type intended, so a
  heuristic must not overrule it. That holds even for the types this pass can infer: a guess
  must not displace a statement, and both inferences are opt-in, so on a default run the
  classifier's answer for that content would be `/P` anyway.

  Still refused, each **by name** in `AutoTagResult.SkippedPageReasons`: nested sequences, an
  unbalanced `EMC`, a duplicate MCID, a property list `/Resources /Properties` does not define, a
  non-artifact sequence with no MCID, and a tag that is not a standard structure type. And the
  whole document is refused when its existing tree claims any content — adopting means claiming an
  MCID, and two claims on one range is worse than none.

- **It still *replaces* an existing tree, never merges into one.** A document whose tree does reach
  content keeps it, and its already-marked pages stay untagged.
- **`RemediateWithReport` tags a document whose structure elements name no page content**, and
  keeps any tree whose elements do — the gate is not the mere presence of a `/StructTreeRoot`, so the
  one input class the pipeline is most likely to meet, a hollow tree from a broken exporter, is
  repaired rather than described. The supersession is reported.

  > **Two gates, deliberately different, and collapsing them either way is a defect.** *Replacing*
  > the tree is gated on whether any element names content at all (`StructTreeNamesContent`);
  > *claiming* the document is tagged — `/MarkInfo /Marked true` and the `pdfuaid:part` declaration
  > — is gated on that **and** a resolvable `/ParentTree` (`StructTreeReachesContent`). A tree with
  > element-to-MCID links but no parent tree is therefore kept and earns no claim: the missing
  > parent tree is a repairable omission, while the links are unrecoverable once overwritten.
- **`/S /Artifact` is reported (UA13) even in a PDF 2.0 file**, where ISO 32000-2 admits it.
  Judging correctly means reading `/NS` against the document's `/Namespaces` — PDF/UA-2 territory.
  This checker validates PDF/UA-1, where excluding it is right.
- **UA22 and the plugin's UA27 disagree** on whether an `/OBJR` `/Alt` substitutes for a Link
  annotation's `/Contents`. Deliberate: UA22 matches independent checkers; UA27 keeps the laxer
  historical rule so a shipped package's verdicts do not change.
- **Reading order is knowingly partial, and multi-column pages are *declined*, not repaired** — see
  `Analyzed` / `IsPartial` and the proof rules above. Recovering the intended sequence of a genuine
  two-column page needs a layout-analysis engine.
- **A page over 4 MiB of decoded content is never tagged.** The ceiling is mandatory (the pass costs
  a large multiple of the stream), and raising it means making the pass streaming.
- **A tooltip generated from a field's `/T` is a placeholder, not an accessible name.**
  `PdfAccessibilityFixer` writes one and warns for every field it touches; a field with no `/T` at
  all is skipped unless you opt in with
  `AccessibilityFixOptions.AddPlaceholderTooltipsForUnnamedFields`. Closing UA08 this way silences
  the check without helping anyone — a human still has to write the real names.

The authoritative list of open gaps is [`GAPS.md`](../../../GAPS.md) §5c.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file). See the repository for the full guide, the open-gap register and the third-party notices.
