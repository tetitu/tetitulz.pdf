# Tetitulz.Pdf.Plugins.CMap

The Adobe **predefined CMap** data — the tables that map character codes to CIDs for CJK encodings
such as `UniJIS-UCS2-H`, `GBK-EUC-H` and `KSCms-UHC-H`.

A PDF may name one of these instead of embedding its own CMap. The *mechanism* for using them lives
in `Tetitulz.Pdf.Core` (`PredefinedCMapRegistry`); this package carries the embedded **data** and
registers it. Install it if you render or extract text from CJK documents that use predefined
encodings.

## Registering it

```csharp
var plugin = new AdobeCMapPlugin();
plugin.Initialize();          // registers the CMap tables into Core's registry
PdfPluginHost.Instance.Register(plugin);
```

`Initialize()` is what populates the registry, so call it before opening such a document. Without
it, a document naming a predefined CMap falls back to whatever Core can derive, which for CJK text
usually means the wrong CIDs.

## Why this is a separate package

**Mechanism in Core, payload in the plugin.** The CMap tables are bulk data that most documents
never need, and shipping them in the core package would make every consumer pay for them. This is
the cleanest example of what a plugin is for in this library: it isolates a *cost*, and the code
that uses the data stays where everything else can reach it.

It is also why `Tetitulz.Pdf.Plugins.PostScript` and this package share
`Core.Fonts.PredefinedCMapRegistry` rather than referencing each other — plugin-to-plugin references
are banned, and there is always a home for shared code further down.

## Targets

`net10.0` and `netstandard2.0`. No third-party dependency.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file).

The CMap data is Adobe's; see the repository's third-party notices for its terms, alongside the full
guide and the open-gap register.
