# Tetitulz.Pdf.Plugins.Renderers

Registers the Tetitulz.Pdf SkiaSharp page renderer for **string-keyed, run-time discovery**.

`Tetitulz.Pdf.Base` already renders pages directly through `PdfOperations`. This package exists for
hosts that choose a renderer at run time rather than at compile time - a plugin gallery, a pipeline
configured from a file, an application that may later add a second renderer.

```csharp
var plugin = new SkiaSharpRendererPlugin();
plugin.Initialize();
PdfPluginHost.Instance.Register(plugin);

// Later, wherever the renderer is selected indirectly:
IRendererPlugin renderer = PdfPluginHost.Instance.FindRendererPlugin(page, doc);
byte[] png = renderer.RenderPage(page, doc, dpi: 150);
```

`CanRender` and `Priority` are how the host picks between several registered renderers.

## It isolates nothing, and that is fine

Most plugins in this library exist to keep a third-party dependency out of the core package graph.
This one has no third-party dependency: it is an **adapter**, not an extension point. Do not read
its existence as evidence that rendering "belongs in" the plugin ring - it does not. If you are
rendering directly, use `Tetitulz.Pdf.Base` and skip this package.

## Targets

`net10.0` and `netstandard2.0`.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file).

See the repository for the full guide, the open-gap register and the third-party notices.
