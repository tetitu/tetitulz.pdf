# Tetitulz.Pdf.Plugins.HtmlScript

Runs a page's own JavaScript for Tetitulz.Pdf's **managed** HTML-to-PDF engine, so what is laid
out is the page as its scripts left it — a jQuery ready handler moving a cell, adding a class,
writing a value.

`Tetitulz.Pdf.HtmlToPdf` defines the contract (`IHtmlScriptPlugin`) and contains no script engine.
This package supplies one — AngleSharp's DOM driven by the Jint interpreter — which is what keeps a
JavaScript engine out of the HTML converter's package graph. **Without it installed, the managed
engine lays out the page as written and reports that its scripts did not run.**

## Registering it

```csharp
PdfPluginHost.Instance.Register(new JintHtmlScriptPlugin());
```

Only this explicit registration turns scripts on: plugin discovery (`RegisterFromAssembly`,
`RegisterFromLoadedAssemblies`, `RegisterFromDirectory`) passes the plugin over, as it passes over the
document-JavaScript engine, so a host that discovers plugins for other features runs no page's code.

Scripts then run whenever `HtmlToPdfOptions.JavaScriptEnabled` is set (the default) and the page
has one. Set it to `false` to lay a page out as written even with the plugin registered.

## Page scripts are untrusted code

A page's scripts run with **no network and no file system of their own**: a `<script src>` is
loaded only through the converter's own rules — `HtmlToPdfOptions.RemoteResourceLoader` (use
`GuardedRemoteResourceLoader` for HTML you do not control) for remote sources, the base directory
for local ones. `XMLHttpRequest` and `fetch` are not provided. Every script runs under statement,
memory, recursion and time limits, and a page that hits one is laid out as its scripts had left it
at that point, with the limit reported.

A script that throws is the page's own error: it is skipped and reported, and the scripts after it
still run, as in a browser.

## What it cannot do

There is no layout while scripts run, so `offsetWidth`, `clientHeight`, `scrollTop` and the rest
answer 0 — reported once per page, naming the ones read — and `getBoundingClientRect` is not
provided (a call throws, reported like any throwing script); and there is no `<canvas>` drawing. A page that needs either — or anything else a real browser does —
needs `HtmlRenderEngine.Browser` and `Tetitulz.Pdf.Plugins.HtmlBrowser`.

## Targets

`net10.0` and `netstandard2.0`.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file).

This package contains a vendored copy of **AngleSharp.Js 1.1.0** (MIT, © AngleSharp) with an
added hook, so that the limits above can be set at all, in its own namespace
(`Tetitulz.Pdf.Plugins.HtmlScript.AngleSharpJs`), so that an application may also reference the
official AngleSharp.Js package without a name seen twice — see `VENDORED.md` in the repository. It
depends on Jint (BSD 2-Clause), AngleSharp and AngleSharp.Css (MIT). See the repository's
third-party notices.
