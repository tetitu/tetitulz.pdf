# Tetitulz.Pdf.Plugins.Ocr

OCR for Tetitulz.Pdf: recognises text on scanned pages, and writes an **invisible searchable text
layer** over the existing image so the page looks unchanged but selects, searches and copies.

Isolates Tesseract from the core package graph. Without this package installed, the OCR entry points
on `PdfOperations` report that no engine is available rather than returning an empty result — an
empty extraction is the correct answer for most PDFs, so it must not double as "no engine".

## Registering it

```csharp
var plugin = new TesseractOcrPlugin();
plugin.Initialize();
PdfPluginHost.Instance.Register(plugin);
```

Tesseract needs its native binaries and language data (`tessdata`) present at run time; that is a
deployment step, not a NuGet one.

### Hebrew

No language data ships with the package, Hebrew's included. Put `heb.traineddata` and
`eng.traineddata` (from the `tesseract-ocr/tessdata` repositories) in the tessdata folder and ask for
both — a Hebrew document prints Latin part codes, e-mail addresses and English labels beside the
Hebrew, and a one-language model reads the other script poorly:

```csharp
var plugin = new TesseractOcrPlugin(tessDataPath: "/usr/share/tesseract-ocr/5/tessdata", language: "heb+eng");
```

Scan at 300 dpi or more; the Hebrew model is reported weakest on pointed text (niqqud).

## Making a scan searchable

```csharp
using var input  = File.OpenRead("scan.pdf");
using var output = File.Create("scan-searchable.pdf");

var result = PdfOperations.MakeSearchable(input, output);
Console.WriteLine(result.AnyTextWritten);
```

## Why the layer looks the way it does

The text layer is **invisible** (render mode 3), so its font is Type0/Identity-H with UTF-16 code
units, an identity `/ToUnicode` and **no embedded font program**. That is right here and wrong for
any visible writer: nothing draws these glyphs, so no program is needed, and the `/ToUnicode` is
what makes the text extractable.

Words are emitted in **visual** order, and a rotated page is handled: the OCR engine sees the page
as displayed, so its boxes are mapped back through the inverse of the renderer's own rotation, and
the baseline turns with the page rather than running across it.

## Targets

`net10.0` and `netstandard2.0`.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file).

See the repository for the full guide, the open-gap register and the third-party notices, including
Tesseract's.
