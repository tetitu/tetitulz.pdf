# Tetitulz.Pdf.Plugins.Text

Exposes Tetitulz.Pdf's text extraction as an `IExportPlugin`, for hosts that pick an output format
by string key.

```csharp
var plugin = new TextExportPlugin();
plugin.Initialize();
PdfPluginHost.Instance.Register(plugin);

IExportPlugin text = PdfPluginHost.Instance.FindExportPlugin("txt");
text.Export(doc, output, new ExportOptions());
```

`PdfOperations.ExtractText` and `ExtractStructuredText` in `Tetitulz.Pdf.Base` are the direct route;
this package is the adapter.

## An empty result is usually correct

Most PDFs that yield no text really contain no text - a scan is an image. If you need to tell
"this page has no text" from "extraction failed", read `PdfOperations.LastExtractionError`,
and use `Tetitulz.Pdf.Plugins.Ocr` for scanned input.

## Targets

`net10.0` and `netstandard2.0`. No third-party dependency.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file).

See the repository for the full guide, the open-gap register and the third-party notices.
