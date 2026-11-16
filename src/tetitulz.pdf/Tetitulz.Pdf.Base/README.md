# Tetitulz.Pdf.Base

Rendering and the high-level document operations, built on `Tetitulz.Pdf.Core`. **This is the
package most applications install** — `PdfOperations` is the façade, and it covers rendering, text
and table extraction, forms, redaction, annotations, bookmarks, metadata and image editing.

Rendering is SkiaSharp, which brings native assets for Windows, Linux and macOS. "Pure-managed"
means no native PDF engine; it does not mean rendering is free of native code.

## Rendering a page

```csharp
using var input = File.OpenRead("input.pdf");
byte[] png = PdfOperations.RenderPageToPng(input, pageIndex: 0, dpi: 150);
```

For a render loop, `RenderPageToPixels` returns raw RGBA8888 with **no PNG encode** — worth knowing,
because PNG encoding is roughly half the cost of rendering a real page and almost all of a blank
one. Its signature carries no SkiaSharp type on purpose, so it cannot pin you to a SkiaSharp version.

## Print and colour

A page renders as its separations — a plate per process and spot ink — and from them as a Photoshop
document, or screened to one bit through the page's own
halftone for a plate-setter or a grey device:

```csharp
using var input = File.OpenRead("input.pdf");
byte[] psd = PdfOperations.RenderPageSeparations(input, 0, 300).ToPsd();
input.Position = 0;
byte[] g4 = PdfOperations.RenderPageHalftoned(input, 0, 600).ToTiff();
```

`PdfRenderOptions` carries the colour engine: `DestinationProfile` renders for a display or printer
profile other than sRGB (and embeds it in the PNG, JPEG or WebP), `NamedColorProfile` draws spot inks
in an ICC named-colour library's colours, and `ProofProfile` soft-proofs through a press. A whole
document's colour converts into a press's CMYK, or a PDF/A intent's RGB:

```csharp
using var source = File.OpenRead("input.pdf");
using var output = File.Create("press.pdf");
var converted = PdfOperations.ConvertToCmyk(source, output,
    new CmykConversionOptions { OutputIccProfile = File.ReadAllBytes("press.icc") });
```

## Extracting text

```csharp
using var input = File.OpenRead("input.pdf");
string text = PdfOperations.ExtractText(input);

// Structured, with line and word geometry:
var pages = PdfOperations.ExtractStructuredText(input);
```

## Two renderers, one answer

There is a raster renderer (SkiaSharp) and a vector one (`RenderPageToSvg`). They read the same
files and are asserted against each other pixel-for-pixel and geometry-for-geometry, because every
"silently wrong output" defect this library has found was one of them lagging the other.

## Encrypted input

Read-only entry points take a password:

```csharp
string text = PdfOperations.ExtractText(input, password: "s3cret");
```

Operations that **rewrite** a whole file — sanitize, optimize, linearize, redact, template-fill —
fail closed on an encrypted document rather than emitting a clear-text file where the reader expects
ciphertext. Add `Tetitulz.Pdf.Encryption` and use its encrypted incremental update or round-trip.

A few read-only entry points take no password by design (byte-level ones such as `GetRevisions`,
and the two-document comparisons). Those **refuse** an encrypted document rather than answering
about one they could not read.

## Redaction removes, it does not cover

`PdfTextRedactor` and the PageEdits plugin's `PdfRedactor` both route to one engine that rewrites
the document in a single revision — so the removed bytes are gone, and any signature over them is
necessarily invalid. Check `PdfRedactionEngine.LastSharedFormWarning` afterwards: a Form XObject
drawn from a page you did not name keeps its original, and that is the one case where the data stays
in the file. A search-and-redact also removes its matches from the document information, the
outline's titles and the XMP packet (`PdfTextRedactor.LastDocumentTextRemoved` names where). It
does not read what an annotation draws — a filled field's value, a FreeText's words, a stamp's caption
— so it finds no match there; redact those by region (`GAPS.md` §17e). A region
over something painted through a pattern or soft mask that draws an image is refused: a cover box
would hide that image's pixels and the file would keep them.

## Targets

`net10.0` and `netstandard2.0`.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file).

See the repository for the full guide, the open-gap register and the third-party notices.
