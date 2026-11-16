# Tetitulz.Pdf.Plugins.Svg

Exposes Tetitulz.Pdf's **vector** SVG page export as an `IExportPlugin`, for hosts that pick an
output format by string key.

```csharp
var plugin = new SvgExportPlugin();
plugin.Initialize();
PdfPluginHost.Instance.Register(plugin);

IExportPlugin svg = PdfPluginHost.Instance.FindExportPlugin("svg");
svg.Export(doc, output, new ExportOptions());
```

`PdfOperations.RenderPageToSvg` in `Tetitulz.Pdf.Base` is the direct route and does the same thing;
this package is the adapter.

## Vector, not a rasterised page

The SVG output is real geometry - paths, text and images - not a PNG in a wrapper. That is why it is
worth having beside the raster renderer, and why the two are asserted against each other: pixels for
appearance, and geometry separately, because a wrong-sized export is still internally consistent and
a pixel comparison cannot see it.

## Targets

`net10.0` and `netstandard2.0`. No third-party dependency.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file).

See the repository for the full guide, the open-gap register and the third-party notices.
