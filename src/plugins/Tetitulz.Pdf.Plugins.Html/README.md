# Tetitulz.Pdf.Plugins.Html

Two directions, one package:

- **HTML to PDF** - exposes `Tetitulz.Pdf.HtmlToPdf` as an `IImportPlugin`, for hosts that select a
  converter by string key.
- **PDF to HTML** - `PdfHtmlConverter`, which is implemented here.

```csharp
using var input = File.OpenRead("report.pdf");
string html = PdfHtmlConverter.ConvertToHtml(input, new HtmlConversionOptions
{
    Title        = "Report",
    IncludeText  = true,
    EmbedImages  = true,       // data: URIs rather than side files
    ImageDpi     = 150,
});
```

`MaxEmbeddedImageBytes` bounds what embedding will inline, so one large scan cannot turn a small
document into an unusable HTML file.

## Fillable PDF forms from HTML

```csharp
byte[] pdf = HtmlFillableForms.ConvertHtmlToFillablePdf(html,
    new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed });
```

The form controls become AcroForm fields instead of drawings of them. This lives here because it is
two steps in two assemblies that cannot reference each other: HtmlToPdf's managed layout knows where
each control landed but may reference only Core, and the field builder is in Base. What is mapped,
and what stays a drawing (buttons, `file` and `image` inputs), is in the HtmlToPdf README.

## The plugin-to-feature edge is deliberate

This package references the `Tetitulz.Pdf.HtmlToPdf` feature. That direction is allowed precisely
because the converter references no plugin, so no cycle can form - and exposing the converter
through the plugin interfaces is the whole point of this package. What is banned in this library is
plugin-to-plugin.

## For pages that need a browser

`PdfHtmlConverter` and the HtmlToPdf feature lay HTML out themselves, which is fast, deterministic
and has no external dependency. For pages that need a real layout engine and script execution, use
`Tetitulz.Pdf.Plugins.HtmlBrowser` instead.

## Targets

`net10.0` and `netstandard2.0`. No third-party dependency of its own.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file).

See the repository for the full guide, the open-gap register and the third-party notices.
