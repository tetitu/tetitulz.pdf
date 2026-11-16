# Tetitulz.Pdf.Builder

A code-first document model — `DocumentBuilder` → `Document` → exporters for **PDF, DOCX, HTML and
ODT**. You describe sections, paragraphs, runs, tables, lists and images once; each exporter decides
how to say that in its own format.

**Dependencies:** `Tetitulz.Pdf.Core` only. No SkiaSharp, no UI.

---

## The invariant that matters most

> **Builder must NEVER reference another feature.**

Builder is the one **documented exception** to the rule that features may depend only on
Core/Base: `Tetitulz.Pdf.DocxToPdf` and `Tetitulz.Pdf.HtmlToPdf` are allowed to reference it,
because it owns the shared document model they both target. That makes the dependency

```
DocxToPdf  ─┐
            ├─→  Builder  ─→  Core
HtmlToPdf  ─┘
```

one-way and acyclic. A reference **from** Builder to any feature — most obviously back to one of
the converters — closes the cycle and breaks the build for everyone. If Builder appears to need
something from a converter, the thing it needs belongs in Builder or in Core, not in the converter.

The second consequence is quieter: **Builder may not use SkiaSharp**, because it may only
reference Core. That is why text shaping on the producer side goes through the pure-managed
`Core.Text.OpenTypeShaper` rather than a native shaping engine, and why raster work is handed out
through `PdfExportOptions` seams instead of being done here.

---

## Getting started

```csharp
var doc = new DocumentBuilder()
    .AddSection(s => s
        .AddParagraph(p => p.AddText("Quarterly report").Bold().FontSize(20))
        .AddParagraph(p => p.AddText("Revenue rose 12% against the prior quarter."))
        .AddTable(t => t
            .AddRow(r => r.AddCell("Region").Bold().AddCell("Revenue").Bold())
            .AddRow(r => r.AddCell("EMEA").AddCell("€1.2M"))
            .AddRow(r => r.AddCell("APAC").AddCell("€0.9M"))))
    .Build();

// SaveAsPdf / SaveAsDocx take a Stream — there is no file-path overload.
using (var pdf  = File.Create("report.pdf"))  doc.SaveAsPdf(pdf);
using (var docx = File.Create("report.docx")) doc.SaveAsDocx(docx);
```

`ToPdf()` returns the bytes instead of writing a file. Both have overloads taking
`PdfExportOptions`.

---

## The fluent chain is one producer of the model, not the only one

```
DocumentBuilder (this file)  ─┐
HtmlToBuilderMapper           ─┼─→  Model.Document  ─→  PdfExporter / Docx / Html / Odt
DocxToBuilderMapper           ─┘
```

Both converters build `Model.*` objects directly and never call `DocumentBuilder`. Two consequences
worth knowing before adding anything:

- **A rendering capability reaches HTML→PDF and DOCX→PDF only through `Model` + `PdfExporter`.**
  Adding a fluent method alone changes nothing for either converter.
- **The chain deliberately mirrors the model, and it is the caller's job to say so when it cannot.**
  It reaches everything the model can express except `Paragraph.BackgroundImage`
  sub-properties, which take the model type directly. Anything it *cannot* express is a gap to
  close, not a design.

New members are **overloads, never new optional parameters** — this ships as a NuGet package, so an
added optional parameter changes the compiled signature and a consumer built against the previous
version takes a `MissingMethodException`.

### Beyond the getting-started sample

```csharp
var doc = new DocumentBuilder()
    .Tagged().Language("he-IL")                       // real tagged PDF from the fluent path
    .AddSection(s => s
        .PageNumberStart(1).TitlePage()
        .FirstPageHeader(h => h.AddParagraph(p => p.AddText("Cover")))
        .AddTableOfContents(maxLevel: 2)                  // one entry per heading, with its page
        .AddParagraph(p => p.AddText("Overview").Role("H1").Opacity(0.9))
        .AddParagraph(p => p
            .AddText("See ").AddText("the site").Link("https://example.com").Underline()
            .AddFootnote("Drawn at the foot of the page the mark lands on.")
            .AddEndnote("Drawn after the last page's content, numbered in the endnotes' own sequence."))
        .AddTable(t => t
            .ColumnWidths(120, 0).RepeatHeaderRow().OuterBorderOnly()
            .AddRow(r => r.Header().AddCell(c => c
                .Header().HeaderScope(TableHeaderScope.Column)
                .AddParagraph(p => p.AddText("Region"))))
            .AddRow(r => r.AddCell(c => c                       // nested table inside a cell
                .AddParagraph(p => p.AddText("EMEA"))
                .AddTable(inner => inner.AddRow(ir => ir.AddCell("detail")))))))
    .Build();
```

---

## Opacity

`Paragraph.Opacity` fades everything a paragraph paints — shading, background image, borders and
its runs; `TextRun.Opacity` fades one run further, and the two multiply. Emitted as PDF constant
alpha (`/ExtGState /ca`, `/CA` for strokes) and reachable fluently as `Opacity(...)` /
`TextOpacity(...)`.

**Full opacity emits nothing at all** — no `gs` operator and no `/ExtGState` resource — so a
document that never asks for it is byte-identical to one produced before the feature existed.

**It is constant alpha, not CSS group opacity.** CSS composites an element once as a unit; `/ca`
composites each object separately, so a paragraph that paints a background *behind its own text*
draws that text over an already-faded backdrop rather than fading the pair together. That case —
and only that case, since with nothing behind the text the two agree exactly — raises
`BuilderDiagnosticKind.ContentApproximated`. Matching CSS exactly needs a transparency-group form
XObject. A `TableCell` has no opacity of its own; a paragraph inside one still honours its.

DOCX and ODT have no property for it and say so through `UnsupportedByExportFormat`; the HTML
exporter round-trips it as `opacity:`.

---

## `PdfExportOptions`

| Option | Default | What it decides |
|---|---|---|
| `FontProvider` | `null` | `Func<BuilderFontRequest, byte[]>` — supply a font program for a family/weight/style. Consulted **before** the system search, so output is reproducible on a machine with no fonts installed. |
| `AllowSystemFontSubstitution` | **`true`** | Search the host's installed fonts when the provider declines. Defaults *on* here, unlike the Accessibility feature, because that is Builder's long-standing behaviour. Turn it **off** for byte-reproducible output. |
| `UnrenderableText` | `Report` | What to do when no font covers a run: `Report` keeps today's pixels and raises a diagnostic, `Omit` drops the run, `Throw` raises `UnrenderableTextException`. `Throw` is not the default because it would break every host lacking a Hebrew or CJK font. |
| `OnDiagnostic` | `null` | Receives every `BuilderDiagnostics` event the export raises — font not resolved, glyph missing, font format rejected, image decode failed, content approximated or not expressible, the page limit reached. |
| `MaxPages` | `0` | The most pages the export writes; `0` for no limit. A longer document ends at the last whole page within the limit (its footer drawn, its page count the pages written) and raises `PageLimitReached`. A negative value is refused. |
| `MaxUnsubsettableFontBytes` | ~4 MB | A CFF/OTF program `CffSubsetter` cannot subset is embedded **whole**, so one over this size is declined with a diagnostic naming the reason rather than silently producing a 20 MB PDF. A program it can subset costs only its used glyphs, whatever its size. |

Diagnostics are also readable after the fact through `BuilderDiagnostics.LastError`, which is
`[ThreadStatic]` — read it on the thread that ran the export.

---

## Right-to-left text

Hebrew and Arabic are supported end to end. Three things are worth knowing:

- **Bidi is resolved per line, not per token.** Levels are a paragraph property; reordering is a
  line operation. Measurement stays in logical order so line breaking is unaffected, and UAX #9
  L1/L2 run once the break is chosen.
- **`TextDirection.Auto`** detects the base direction from the first strong character (P2/P3).
  `TextAlignment.Start`/`End` resolve against it, while `Left`/`Right`/`Center` stay physical, as
  in CSS.
- **Arabic is shaped** through `Core.Text.OpenTypeShaper`, and `/ToUnicode` records the **base**
  letters, so copy-paste and search return what a keyboard produces rather than presentation forms.

---

## Tagged output

`Document.Tagged` (default `false`) emits real tagged PDF: marked content, a `/ParentTree`, and
per-page `/StructParents`. `Paragraph.Role` takes a structure type (`P`, `H1`, `Sect`,
`BlockQuote`, …); `Paragraph.IsArtifact` marks page furniture, and is deliberately **separate**
from `Role` because `/S /Artifact` is exactly what a PDF/UA checker's UA13 exists to catch.

Elements are created lazily and the tree is pruned before writing, because the model legitimately
produces empty paragraphs (a horizontal rule, a vertical-merge placeholder). An empty `/P` is UA16
and a subtree of them is UA30 — hollow tagging is the defect the accessibility feature exists to
find, and emitting it here would be self-inflicted.

**Tagging is not a conformance claim.** The standard-14 fonts are never embedded, so a Latin-only
tagged document still fails UA21/UA25, and nothing here writes a conformance identifier.

---

## Importing a dynamic XFA form

A dynamic XFA form has no pages until an XFA processor lays them out, so every other reader shows
the producer's "Please wait…" placeholder. Two importers read the form instead of the placeholder:

- `Import.XfaContent.Read(stream)` — the **content**: the `datasets` packet's values, grouped by
  subform path under the designer's field names. No layout.
- `Import.XfaLayout.Read(stream, options)` — the **designed layout**: the `template` packet laid out
  with the data. Page areas become sections, positioned subforms place their fields and draws, flowing
  subforms stack, tables and `occur` repetition follow the bound data, `breakBefore`/`breakAfter`
  start a section on the named page area. What the reader approximates (scripts are counted, never
  run; prototypes, arcs, barcodes, rich text) is listed in `XfaLayoutResult.Approximations`, and
  `XfaLayout.LastError` says why a document could not be read (no `/XFA`, a single XDP stream, no
  `template` packet, XML that is not well-formed).

```csharp
var laid = XfaLayout.Read(File.OpenRead("dynamic-form.pdf"));
if (laid == null) Console.WriteLine(XfaLayout.LastError);
else File.WriteAllBytes("laid-out.pdf", laid.Document.ToPdf());
```

Both return a `Document`, so the same form can be written as PDF, DOCX, HTML or ODT. The template is
hostile input: no DTD is processed, no external resource fetched, and the tree's depth, a repeating
subform's instances and the whole layout's blocks each have a ceiling, each refusal counted.

## Limits

- **A CFF program is subsetted where `Core.Fonts.Cff.CffSubsetter` can read it** (charstrings
  blanked, the font's other data kept); one it cannot — overlapping charstring ranges, a shape the
  reader refuses — is embedded whole up to `MaxUnsubsettableFontBytes` and declined past it, with
  a diagnostic saying why. A Type 1 program is subsetted through conversion to CFF.
- **No general box tree.** Layout is paragraph-by-paragraph over a page's flow, with the
  constructs the converters needed added to it: a float (a paragraph, or a `BoxStart` box of
  blocks, the text returning to the full measure below), a positioned block (`AbsolutePlacement`
  against its containing box or the page, `position: fixed` on every page), a multi-column block
  balanced by its blocks' measured heights, a box of blocks with a border, padding and background
  that breaks across pages. Each approximation the exporter makes is reported through
  `BuilderDiagnosticKind.ContentApproximated`; the remaining declines are `GAPS.md` §4b.
- **The four exporters are not equal.** PDF is the most complete; DOCX and ODT round-trip the
  model's structure; HTML is a readable projection. **A new model field must be handled — or
  explicitly ignored — in all four**, or the model quietly means different things per format.
- **Every measurement is validated once**, before any exporter reads it (`Model.ModelMeasurements`):
  a size below zero, or any NaN or infinite margin, indent, spacing or offset, is refused naming the
  element's path and the property — not in a setter, not in one exporter.

See [`GAPS.md`](../../../GAPS.md) §5 for the tracked remainder.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file). See the repository for the full guide, the open-gap register and the third-party notices.
