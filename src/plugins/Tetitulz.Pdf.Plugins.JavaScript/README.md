# Tetitulz.Pdf.Plugins.JavaScript

Runs a document's embedded JavaScript — form field calculations, validation and format scripts,
document-level actions — for Tetitulz.Pdf.

`Tetitulz.Pdf.Base` defines the contract (`IJavaScriptPlugin`) and one dispatch point, and contains
no script engine. This package supplies the engine, which is what keeps Jint out of the core package
graph. **Without it installed, a document's scripts simply do not run** — which is a safe default,
not a missing feature.

## Registering it

```csharp
var plugin = new JintJavaScriptPlugin();
plugin.Initialize();
PdfPluginHost.Instance.Register(plugin);
```

Only this registration, by name, enables it: plugin discovery (`RegisterFromLoadedAssemblies`,
`RegisterFromDirectory`) passes the engine over, so referencing the package exposes a host to no
document's script.

## What a script sees

The form object model of Adobe's *JavaScript for Acrobat API Reference*, as Acrobat DC answers it
(the recorded answers are in the test fixtures): `app`, the document as the global object, its
fields (`getField`, with each field's type, flags, items, captions and font), `event`, `util`,
`color`, `console`, `global`, the constant objects, `Matrix2D` and the `AF` functions. From the
document it reads the `/Info` and the XMP packet (`info`, `info.Authors`, `metadata`), each page's
rotation and boxes, the annotations (`getAnnots`), the optional content groups (`getOCGs`), a page's
words (`getPageNthWord`) and — only when the execution context sets `ExposeAttachments` — the
attachments (`dataObjects`, `getDataObjectContents`), each decoded under 1 MiB. A document's
scripts share one environment (`PdfJsDocumentScope`), the document-level scripts running first.

## The sandbox is a resource sandbox, not a permission model

Scripts run under a **32 MiB memory cap and a 5-second timeout per execution, 256 MiB across a
document's scripts, and a recursion guard**, and the engine exposes only the object model above —
not CLR types, the filesystem or the network. What would reach outside the process (`launchURL`,
`submitForm`, `mailDoc`, `openDoc`, the timers, `saveAs`, `print`) is recorded on
`PdfJsEngine.LastUnsupportedRequest` and never done.

Two limits worth knowing before you rely on it:

- **The per-call budgets multiply by the script count, so a document has one of its own.** The
  `ExecuteDocumentScripts` overloads run a document's scripts under one shared budget
  (`PluginJavaScriptRunner.DefaultDocumentBudget`, 30 s); a caller driving scripts one at a time
  opens one with `PluginJavaScriptRunner.BeginDocumentBudget(total)` and can read
  `RemainingDocumentBudget`. Outside a document budget, N field scripts get N × the per-call limits.
- **AcroForm field protections are recorded, not applied.** A script asking for `readOnly`, `hidden`
  or `setFocus` is reported through `PdfJsEngine.LastUnsupportedRequest` and **not enforced**, so a
  document's own form protections are not a security control here.

More generally: this library parses untrusted PDFs and never acts on them. Actions are surfaced to
your handlers rather than performed, and validating a `javascript:`, `file:` or `smb:` target is the
handler's job. The sandbox above bounds *resources*; it does not decide what a script is allowed to
mean.

## Targets

`net10.0` and `netstandard2.0`.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file).

See the repository for the full guide, the open-gap register and the third-party notices, including
Jint's.
