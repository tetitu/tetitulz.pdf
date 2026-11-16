# Tetitulz.Pdf.DocxToPdf

Converts DOCX to PDF in process: `DocumentFormat.OpenXml` reads the package, `Tetitulz.Pdf.Core`
writes the PDF, and nothing native is involved. Two external engines — LibreOffice and Microsoft
Word — are reachable through the same API for a deployment that has them.

**Targets:** `net10.0` and `netstandard2.0` (a .NET Framework host can convert DOCX; the
LibreOffice engine works there too).
**Package dependencies:** `DocumentFormat.OpenXml`.
**Project dependencies:** `Tetitulz.Pdf.Core`, `Tetitulz.Pdf.Builder` — the one sanctioned
feature-to-feature reference (the Builder invariant in `CLAUDE.md`).

---

## Calling it

```csharp
using Tetitulz.Pdf.DocxToPdf;

DocxToPdfConverter.Convert("input.docx", "output.pdf");

DocxToPdfConverter.Convert("input.docx", "output.pdf", new DocxToPdfOptions
{
    PageWidth = PdfPageSizes.A4Width, PageHeight = PdfPageSizes.A4Height,
    MarginTop = 50, MarginBottom = 50,
});

using var input  = File.OpenRead("input.docx");
using var output = File.Create("output.pdf");
DocxToPdfConverter.Convert(input, output);

byte[] pdf = DocxToPdfConverter.ConvertToBytes("input.docx");
```

`DocxToPdfConverter` is static; `DocxToPdfConverterService` implements `IDocxToPdfConverter` with
the same four overloads for a host that injects it. `DocxToPdfException` (a `PdfException`) is
thrown for a conversion that cannot proceed; a tolerated failure — a shape it reserves and reports,
a font it declines — goes to `DocxToPdfConverter.LastDiagnostic` (`[ThreadStatic]`, read on the
calling thread) and to the `DiagnosticReported` event.

## Options

| Property | Default | What it decides |
|---|---|---|
| `Engine` | `BuiltIn` | `BuiltIn` (this library, cross-platform), `LibreOffice` (`soffice --headless`, cross-platform, LibreOffice installed), `MicrosoftWord` (COM automation, Windows with Word). The page and margin options below apply to `BuiltIn` only. |
| `LibreOfficePath` | `null` | The `soffice` executable; `null` searches the `PATH` and the usual install locations. |
| `PageWidth`, `PageHeight` | 612 × 792 pt (US Letter) | The page when the DOCX's own section geometry is not used. |
| `MarginTop/Bottom/Left/Right` | 72 pt | Likewise. |
| `UseDocumentMargins` | `true` | Take the page size and margins from the DOCX's `w:sectPr`. |
| `DefaultFontSize` | 12 | When the DOCX names none. |
| `EmbedImages` | `true` | Embed the DOCX's images (JPEG, PNG including interlaced, with alpha as an `/SMask`). |
| `CompressStreams` | `true` | Flate on content streams. |
| `UseLegacyRenderer` | `true` | `true` is the in-house OOXML renderer with full fidelity (drawings, charts, SmartArt, shapes and text boxes, two-dimensional maths, link annotations). `false` maps the DOCX onto the Builder model (`DocxToBuilderMapper`) and renders through Builder's exporter, which also opens the same document to DOCX, HTML and ODT export. Both paths lay out every section's geometry, headers and footers, tables, lists, hyperlinks, footnotes and endnotes, and evaluate `PAGE` and `NUMPAGES` as simple and as complex fields (any other field shows its cached result). The Builder path draws pictures alone among the drawings, linearises an Office Math equation to text, and drops an inline it has no case for — each reported through `DiagnosticReported`. |
| `MaxPages` | `0` | The most pages the conversion writes, on either path; `0` for no limit. A longer document ends at the last whole page within the limit, its headers, footers and footnotes drawn, and `DiagnosticReported` says so. A negative value is refused. |
| `AllowRestrictedSystemFonts` | `false` | Embed a host font whose `OS/2 fsType` forbids embedding. A font embedded in the DOCX itself is never gated: its author put it there. |
| `TaggedPdf` | `false` | Tagged output from the DOCX's own structure: `/P` and `/H1`–`/H6` with a parent tree. Tables, lists, images and header/footer content are not yet in the tree (`GAPS.md` §3a). |

## What converts

- **Text**: paragraph alignment, spacing, indentation, tabs, line and page breaks; runs with bold,
  italic, size, family, colour, highlight, character scale (`w:w`) and tracking (`w:spacing`);
  numbered and bulleted lists from `numbering.xml` (decimal, letter, roman, the OOXML page-number
  formats through one formatter); headers and footers; footnotes; fields.
- **Scripts**: Hebrew, Arabic and every other script the DOCX's fonts cover. Text is shaped through
  `Core.Text.TextRunComposer` (joining, marks by the font's `GPOS` anchors, ligatures where the
  document asks with `w14:ligatures`), written through `GlyphRunShowText`, and recorded in
  `/ToUnicode` as the letters typed. A shaped run wider than its table cell wraps inside it.
- **Fonts**: TrueType and OpenType embedded as subsets, including fonts embedded in the DOCX
  (`w:fontTable`, ODTTF de-obfuscation); CFF outlines subset through `CffSubsetter`; WinAnsi body
  text in a family the standard 14 stand for maps to them, everything else goes on the Identity-H
  glyph path. `fsType` is honoured for host fonts (above).
- **Tables**: grid columns, borders, cell shading, conditional banding (`w:tblStylePr`), merged
  cells, nested tables.
- **Images**: inline and anchored (`wp:anchor`, floating and text-wrapped; `wrapTight`/`wrapThrough`
  approximate to the bounding box and a two-sided wrap reserves the larger side), EXIF orientation,
  transparency as a soft mask.
- **Drawings** (DrawingML): about thirty preset geometries with real outlines plus literal
  `a:custGeom` contours; solid fills including theme colours and their transforms, linear and radial
  gradients as PDF shadings, outlines; shape text in any script. A geometry outside the set draws its
  bounding rectangle and reports; pattern, picture and group fills, `arcTo`/`quadBezTo` subpaths,
  effects, 3-D and arrowheads are reported, not approximated.
- **Charts**: bar and column (clustered and stacked), line, area, scatter, pie and doughnut, with
  axes, gridlines, tick and category labels, title, legend and value labels, values from the chart
  part's own `c:numCache` (the embedded workbook is never opened). 3-D variants draw flat and report;
  radar, stock and the `chartex` family (waterfall, treemap, sunburst, funnel) reserve their box and
  report; secondary axes, markers, smoothing, error bars and trendlines are absent.
- **SmartArt**: from the drawing Word persists (`word/diagrams/drawingN.xml`); a producer that omits
  that part gets a reserved box and a report.
- **OMML maths**, typeset in two dimensions in body paragraphs and table cells: stacked fractions,
  scripts, radicals, `m:deg`, `m:acc`, `m:bar`, named functions, delimiters grown to their content
  (capped at 3×), n-ary operators with limits, `m:limLow`/`m:limUpp`, matrices and equation arrays on
  a shared grid; display equations on their own line, justified by `m:jc`, scaled to the column
  (floored at 50 %, reported past it). Bars and the surd are paths, so no maths font is needed.
  `m:groupChr` is centred, not stretched (a real under-brace needs the OpenType MATH table);
  `m:sPre`, `m:box` and `m:phant` stay linear and report.
- **Sections**: multi-column flow (`w:cols`, greedy fill — the last page is not balanced), per-section
  page size and margins, page breaks.

## Hostile input

A DOCX is a ZIP whose declared sizes are the attacker's. **How much of any part the converter will
inflate is bounded before the package is opened** (`PdfLimits.MaxDocxPartBytes`, 256 MiB by default,
settable); the two image readers stream under the same figure. Every image the converter writes is
held to the authored-image size rule (`Core.Writing.AuthoredImageSize`) before it is decoded or
written. The in-house `ProcessArgumentEscaper` the LibreOffice engine uses on the legacy leg is
security-sensitive and mutation-tested against the three escaping rules; `net10.0` keeps
`ArgumentList`.

**The two external engines run another program on the document, and this library's guards stop at
that program's door.** Each is handed a copy of the package whose relationships leading out of it (an
attached template, an external image, a subdocument, a frame) point nowhere: Word fetches those with
macros off and links not updated. A hyperlink keeps its target only when hyperlink elements alone name
its relationship, since an engine looks a relationship up by its id whatever type it declares. The
conversion's files, the converted PDF among them, stay in a directory of its own, removed after it. A file that is not a DOCX package, and one importing
a document whole (`altChunk`), are refused, since each holds references of its own this cannot reach. What
each is told first:

- **Microsoft Word** is started with macros disabled (`AutomationSecurity` =
  `msoAutomationSecurityForceDisable`; an automated Word otherwise starts with every macro
  enabled, the document's and those of a template it names) and with links not updated at open, and
  a document is opened read-only under a password it does not have, so a protected one fails
  instead of waiting at a prompt no one sees. A Word that will not take the macro setting stops the
  conversion. There is no time limit: Word is a separate process this library has no handle to end.
- **LibreOffice** runs on a profile of the conversion's own (`-env:UserInstallation`, in a
  directory made for the conversion and removed after it) whose settings ask for macros off, links
  from untrusted documents blocked and a text document's links not updated; it is ended, with the
  processes it started where the runtime can, after two minutes. Those settings are LibreOffice's
  to honour, and what it fetches while converting is outside this library's address guard and file
  containment.

For documents from people you do not know, `BuiltIn` is the engine that runs nothing and fetches
nothing; an external engine belongs in a process confined by the host (no network, a throwaway
account or container).

## Limits

The two that are inherent are `GAPS.md` §3: `m:groupChr` is centred rather than stretched, and a
defect in a shaped run's *measurement* is invisible to a rendering test on fonts whose positional
forms share an advance with their base letter. Everything else the converter cannot draw reserves
its space and says so.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file). See the repository for the full guide, the open-gap register and the third-party notices.
