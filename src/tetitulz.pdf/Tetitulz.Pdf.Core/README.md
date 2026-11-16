# Tetitulz.Pdf.Core

The bottom of the library: the PDF object model, the parser, the stream filters, the font machinery
and the writing primitives. **No SkiaSharp, no UI, no native dependency** — every other
`Tetitulz.Pdf.*` package is built on this one.

Most applications install `Tetitulz.Pdf.Base` (which brings this in) rather than referencing Core
directly. Reference Core alone when you want to read or write PDF *structure* without pulling in a
rendering stack.

## What is in here

| Area | Types |
|---|---|
| Object model | `PdfDictionary`, `PdfArray`, `PdfName`, `PdfStream`, `PdfString`, `PdfNumber` |
| Parsing | `PdfParser`, `PdfDocument`, `PdfTokenizer`, `ContentStreamParser` |
| Filters | Flate, LZW, ASCII85, ASCIIHex, RunLength, CCITT, DCT, **JBIG2** (written in-house) |
| Writing | `PdfWriter`, `PdfDocumentBuilder`, `IncrementalUpdateWriter` |
| Sanitizing | `PdfSanitizer` (strips JavaScript, launch and URI actions, embedded files, XFA), `SanitizeOptions`; `PdfSignatureDetection` |
| Fonts | sfnt assembly and subsetting (TrueType `glyf`, CFF charstrings), CMap decoding, system-font resolution, `fsType` licence checks |
| Text | `TextRunComposer` (shape → bidi → glyphs), `UnicodeBidi`, `HebrewSearchNormalizer`, `IsraeliIdentifiers` |
| Trees | `PdfNameTree` and `PdfNumberTree` (§7.9.6/§7.9.7), one traversal behind both |
| Constants | `PdfName`, `PdfLimits`, `PdfOperators`, `PdfUnits`, `PdfPageSizes`, digest and EKU OIDs |

## Reading a document

```csharp
using var stream = File.OpenRead("input.pdf");
using var doc    = PdfDocument.Open(stream);

Console.WriteLine(doc.Pages.Count);
var box = doc.Pages[0].Dictionary[PdfName.MediaBox];
```

`PdfDocument.Open` seeks for `startxref` itself, so the stream's position does not matter, and it
buffers a non-seekable stream rather than failing.

## Two things worth knowing

**An encrypted document parses to an unreadable catalog, not to an error.** `Pages.Count` answers
`0` and every read below it returns nothing. Add `Tetitulz.Pdf.Encryption` and call
`PdfEncryptionOperations.Register()` once at startup, or the emptiness is indistinguishable from a
document that genuinely has no pages.

**`PdfDictionary`'s indexer does not follow an indirect reference.** `dict[PdfName.X] as PdfArray`
is `null` both when the key is absent and when the producer wrote `X` as `6 0 R`, which is legal.
Resolve through the document — `doc.ResolveObject(dict[PdfName.X])` — for anything a producer might
write indirectly.

## Targets

`net10.0` and `netstandard2.0`, so a .NET Framework 4.6.2+ host can use it. Three BCL constructs are
missing on the legacy leg and Core supplies the answers: `Core.Text.Latin1Text` (`Encode`/`Decode`)
and `Core.Text.Latin1Encoding` for `Encoding.Latin1` — never `Encoding.GetEncoding("iso-8859-1")`,
which compiles and then throws on a .NET Core host with no code-pages provider;
`Core.Objects.PdfReferenceEquality` for `ReferenceEqualityComparer`; and `!double.IsNaN(x) &&
!double.IsInfinity(x)` where `double.IsFinite` would be written. The first two are public for that
reason.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file).

See the repository for the full guide, the open-gap register and the third-party notices.
