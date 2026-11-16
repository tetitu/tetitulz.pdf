# Tetitulz.Pdf.Plugins.Docx

Office interchange for Tetitulz.Pdf, in both directions:

- **DOCX to PDF** - exposes the `Tetitulz.Pdf.DocxToPdf` feature as an `IImportPlugin`.
- **PDF to DOCX, XLSX and PPTX** - `PdfToDocxConverter`, `PdfToExcelConverter`,
  `PdfToPptxConverter`, implemented here.

Isolates DocumentFormat.OpenXml from the core package graph.

```csharp
using var input = File.OpenRead("report.pdf");
byte[] docx = PdfToDocxConverter.Convert(input);
File.WriteAllBytes("report.docx", docx);
```

`PdfToDocxConverter.LastError` carries the reason when a conversion degrades, on the calling thread.

## What a PDF-to-Office conversion can and cannot be

A PDF describes marks on a page; a DOCX describes a document. The conversion recovers structure that
the PDF makes recoverable - text runs, lines, tables where the geometry supports it - and cannot
invent what was never recorded. A tagged PDF converts far better than an untagged one, and a scan
converts to an empty document — the converters write no images, and `PdfToDocxConverter.LastError`
says the pages yielded no text — unless you run OCR first (`Tetitulz.Pdf.Plugins.Ocr`).

Treat the output as a good starting document, not a faithful round trip.

## Targets

`net10.0` and `netstandard2.0`.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file).

See the repository for the full guide, the open-gap register and the third-party notices, including
DocumentFormat.OpenXml's.
