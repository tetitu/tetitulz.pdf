# Tetitulz.Pdf.Plugins.Conformance

Standards validation and conversion for Tetitulz.Pdf: **PDF/A**, **PDF/X** and **PDF/UA**, plus the
dictionary-level accessibility checks and a fixer for the mechanical ones.

## Validating

```csharp
using var input = File.OpenRead("report.pdf");

PdfAValidationResult a  = PdfAValidator.Validate(input, PdfAConformancePart.Part2);
PdfUaValidationResult ua = PdfUaChecker.Validate(input);

foreach (var v in a.Violations) Console.WriteLine($"{v.Code}: {v.Message}");
Console.WriteLine(a.IsConformant);
if (a.IsPartial)
    foreach (var i in a.Informational) Console.WriteLine($"{i.Code}: {i.Message}");
```

**PDF/A is judged at the level the file declares** (`pdfaid:conformance`) **unless you require one.**
`PdfAValidator.Validate(input, PdfAConformancePart.Part4, PdfAConformanceLevel.E)` asks whether the
file is a PDF/A-4e file: the level's own rules run, and a file whose declared letter that level does not
admit is an A07. The levels nest as the published profiles read them — level B admits a file declaring
B, U or A, level U one declaring U or A — and PDF/A-4's are `None` (PDF/A-4 itself, no letter), `E` and
`F`. A level the part does not have is refused with `ArgumentOutOfRangeException`. Through the plugin
host, `new PdfAValidationPlugin(part, level)` judges at that level under a name of its own
(`…PdfAValidation.Part4.E`), beside the declared-level plugin of the same part.

**A check that did not run in full reports no violation**, so it must not be confused with a
document that passed. PDF/A and PDF/X name every such check on `Informational` — **A99** and **X99**,
with `IsPartial` as the one-line summary — and PDF/UA as an Info `UA99`. None of them affects
`IsConformant`: they say a check did not run, not that the document is wrong. Both converters
withhold the conformance identifier over one. Each validator also keeps a `LastValidationError` slot,
which holds a single message; the `Informational` list is the complete record.

**PDF/X also names what it does not judge.** Where the rule is in a part of ISO 15930 that was not
available, the finding is reported on `Informational` and never as a violation: **X26** (a
halftone's `/TransferFunction`) and **X27** (an optional content configuration, read against PDF/A's
sibling rule). **PDF/X-6** (`PdfXConformanceLevel.X6`) is checked in part: its violations are the rules
the standard's public preview and the *Application Notes for PDF/X-6* state, PDF/X-4's other rules
are carried as informational under their own codes, and an **X29** entry names the clauses not read —
so an X-6 result is always `IsPartial`, and `PdfXConverter` will not convert to it. What the validator
names without judging, the converter leaves out of what it writes: every graphics state's halftone
(`RemoveHalftones`) and every optional content configuration's missing or shared `/Name` and its
`/AS` (`RepairOptionalContentConfigurations`), both on by default, counted on the result.

## The content-scan ceilings are yours

Both validators read page content streams, so both bound how much they will decode — per page and
across the document — and report through `X99` / `UA99` when a ceiling stops a check rather than
letting an unread page look like a clean one:

```csharp
PdfUaValidationResult ua = PdfUaChecker.Validate(input,
    maxPageContentBytes:     2 * 1024 * 1024,     // default 8 MiB
    maxDocumentContentBytes: 32L * 1024 * 1024);  // default 128 MiB

PdfXValidationResult x = PdfXValidator.Validate(input, PdfXConformanceLevel.X4,
    maxPageContentBytes: 2 * 1024 * 1024, maxDocumentContentBytes: 32L * 1024 * 1024);
```

A per-page cap alone does not bound a 50 000-page file, which is why there are two. A non-positive
value is refused by name rather than quietly meaning *unlimited*; pass `long.MaxValue` if that is
what you want.

## Fixing what can be fixed mechanically

```csharp
AccessibilityFixResult fixes = PdfAccessibilityFixer.Fix(input, output, new AccessibilityFixOptions
{
    AddLanguage         = true,
    SetDisplayDocTitle  = true,
    AddMissingTooltips  = true,
});
```

These are the repairs that need no judgement — a document language, the `/DisplayDocTitle`
viewer preference, a missing form-field tooltip. Anything requiring a human decision about
*meaning* (what a figure's alt text should say, whether a run is a heading) is reported, not
invented.

## Which side owns which PDF/UA check

This package owns the mostly-dictionary checks (UA00–UA09, UA25–UA29, UA50+).
`Tetitulz.Pdf.Accessibility` owns the deep, content-walking ones (UA10–UA24, UA30–UA35) and reuses this
package's engine rather than duplicating it — that feature-to-plugin reference is deliberate and
one-way. New deep codes are numbered from UA30 up, new plugin codes from UA50 up; the font pair is
numbered oppositely on the two sides (deep UA20/UA21, plugin UA25/UA26) and is not de-duplicated.

If you want a single verdict over both, call `PdfAccessibility.Audit` in the Accessibility package:
it merges the two and drops exactly three of this plugin's codes (UA28, UA29, UA50) in favour of the
deeper equivalents. The same `Audit` judges against **PDF/UA-2** (ISO 14289-2) on request; the
PDF/UA-2 checker, Well-Tagged PDF 1.0 and the PDF Declarations reader live in this package too.

## Targets

`net10.0` and `netstandard2.0`. No third-party dependency. `Accessibility` and this package must
stay in step on target frameworks (a mismatch is `NU1201`, not a compile error).

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file).

See the repository for the full guide, the open-gap register and the third-party notices. **No PDF/X
check is written from recollection of the standard**: the rules come from public sources (the PDF
Association's *PDF/X in a Nutshell*, the ISO 15930 committee's own application notes, preflight vendors'
published checks and JPEG 2000 conditions for X20–X23). What still reads too little —
a halftone's `/TransferFunction`, `/OCProperties /D` beyond presence, page-level output intents — is
`GAPS.md` §12b, with the stopping rule.
