# Tetitulz.Pdf.Plugins.PageEdits

Page-level editing for Tetitulz.Pdf: **merge**, **split**, and **true redaction** that removes
content rather than covering it.

## Merging

```csharp
using var output = File.Create("merged.pdf");
using var a = File.OpenRead("a.pdf");
using var b = File.OpenRead("b.pdf");

PdfMerger.Merge(output, a, b);
```

The merge carries `/AcroForm /Fields`, inherited page attributes, `/Outlines`, the catalog `/Names`
trees, the **tag tree**, `/OCProperties`, page labels and output intents across. Anything that could
not be carried faithfully is reported rather than dropped silently — `PdfMerger.LastFormWarning`,
`LastNameTreeWarning`, `LastStructureWarning`, `LastOptionalContentWarning`, `LastPageLabelWarning`
and `LastOutputIntentWarning` each name their own concern.

## Splitting carries what the kept pages show

```csharp
using var input = File.OpenRead("report.pdf");
using var output = File.Create("pages-1-and-3.pdf");

PdfSplitter.ExtractPages(input, output, new[] { 1, 3 });
string? carriedWhole = PdfSplitter.LastResourceWarning;   // null: only what pages 1 and 3 draw
```

An extract is written for sharing fewer pages, so it carries what its pages reach and nothing of the
others: no outline item, named destination, form field or annotation that belongs to a dropped page,
and — although producers commonly give every page one resource dictionary — no image, form, font or
other resource that only a dropped page draws. What a kept page draws is read from its content, its
annotations' appearances and every form, Type 3 glyph, pattern and soft-mask group drawn from them.
Where that cannot all be read, the resources are carried whole and `LastResourceWarning` says so;
each other `Last*Warning` names its own concern.

## Redaction removes; covering is a different call

Two operations, deliberately not one:

| Call | What it does |
|---|---|
| `PdfRedactor.RedactRegions` | **removes** the content under the regions and rewrites the file |
| `PdfRedactor.CoverRegions` | draws opaque boxes; the content is still there |

```csharp
PdfRedactor.RedactRegions(input, output, new[]
{
    new RedactionRegion { PageIndex = 0, X = 72, Y = 600, Width = 200, Height = 24 },
});
```

`FindTextMatches` locates text first, so you can review what a pattern would remove before removing
it — redaction is irreversible and a pattern is easy to get wrong. The search reads the page's own
content, not what an annotation draws (a filled field, a FreeText, a stamp): redact those by region.

### Two things redaction cannot do at once

**Preserve a signature *or* remove the data, never both.** Redaction rewrites the document in a
single revision, so a signature over the old bytes cannot survive. (The opposite trade is
`Base`'s `PdfImageReplacer`, which supersedes an image in place and keeps signatures valid.)

**A shared Form XObject keeps its original.** If a form is drawn from a page you did not redact, its
original object must survive for that page — so the content stays in the file, reachable from the
pages you did not name. Check `PdfRedactionEngine.LastSharedFormWarning` afterwards; it names them.
To clear it everywhere, redact every page that draws the form.

Encrypted documents are refused rather than half-redacted: the engine is plaintext-only by design.

## Targets

`net10.0` and `netstandard2.0`. No third-party dependency.

## Licence and provenance

Tetitulz.Pdf Attribution License 1.0 (LICENSE.txt in the package): the MIT License plus conditions on every use of this plugin, of the whole or any part — credit "Uses Tetitulz.Pdf by Tamir Mordo", report the use on https://github.com/tetitu/tetitulz.pdf, and report any security problem privately with the fix if there is one. Copyright (c) 2026 Tamir Mordo.

See the repository for the full guide, the open-gap register and the third-party notices.
