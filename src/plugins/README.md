# Extending Tetitulz.Pdf

How a host adds its own behaviour to the library: a plugin, a hook, or a seam. For calling the
library, start at the [repository README](../../README.md).

## What can be extended, and what cannot

The library is extended by **implementing an interface and registering the instance**, not by
inheriting from its classes.

- The operations are static classes (`PdfOperations`, `PdfFormFiller`, `PdfPageManipulator`), and
  their option and result classes are sealed. There is nothing to subclass and no step to override.
- That is on purpose. Most internal steps are security rules: what a signature covers, what a
  redaction removes, which address a fetch may connect to. An overridable step would let a subclass
  switch a rule off without anyone seeing it.
- To change what an operation does, replace the capability with a plugin, observe it with a hook, or
  call the operation from your own wrapper.

The plugin contracts are in `Tetitulz.Pdf.Core.Plugins`, in the Core package. A plugin references
Core, and Base only if it calls operations.

## Register a plugin

One host per process: `PdfPluginHost.Instance`. Register at startup, before the first document.

```csharp
using Tetitulz.Pdf.Core.Plugins;

var host = PdfPluginHost.Instance;
host.Register(new PageTextLogger());            // one instance you constructed
bool there = host.IsRegistered("Contoso.PageTextLogger");
host.Unregister("Contoso.PageTextLogger");      // calls Shutdown()
```

- `Register` calls the plugin's `Initialize()`. If that throws, the plugin is not registered.
- A second plugin with the same `Name` throws `DuplicatePluginException`, which derives from
  `PdfException`.
- The host is safe to call from several threads.

Three ways to register many at once. **Each runs the plugin's code in your process, so load only
assemblies you trust.**

| Call | What it loads |
|---|---|
| `RegisterFromAssembly(assembly)` | Every public type with a parameterless constructor that implements `IPdfPlugin`. |
| `RegisterFromLoadedAssemblies()` | The same, over the assemblies the application references whose name starts with `Tetitulz.Pdf.Plugins.` |
| `RegisterFromDirectory(path)` | Every DLL in the folder that carries `[assembly: PdfPluginAssembly]`. Treat the folder as a folder of executables. |

A scan skips a type that fails and goes on. The reason is in `PdfPluginHost.LastRegistrationError`,
read on the thread that ran the scan. A plugin marked `[PdfPluginExplicitRegistration]` is passed
over by all three scans and is registered only by `Register`; the two JavaScript engines are marked
so.

## Which contracts the library calls

Only the contracts in the first table run by themselves. Registering one of the others changes
nothing until your host asks for it.

**Called by the library**

| Contract | When |
|---|---|
| `ITextExtractionHook` | Once a page, during `PdfOperations.ExtractText`. |
| `IRenderHook` | After each page is rendered. |
| `IRendererPlugin` | In place of the built-in renderer, on the PNG paths, for a page its `CanRender` accepts. |
| `IImageDecodePlugin` | For an image whose `/Filter` it lists, when the built-in decoder declines. |
| `IPostScriptPlugin` | For an embedded PostScript XObject or `PS` operator, during rendering. |
| `IExportPlugin`, `IImportPlugin` | `PdfOperations.Export` and `Import`, chosen by format key. |
| `IValidationPlugin` | `PdfOperations.ValidateConformance`, chosen by conformance level. |
| `IConformanceConversionPlugin` | `PdfOperations.ConvertToConformance`. |
| `IOcrPlugin`, `IOcrWordPlugin` | `PdfOperations.RecognizeTextWithOcr`. Never implicitly. |

**Discovery contracts: the host calls them**

`IPdfProcessingHook`, `ISanitizePlugin`, `IOptimizePlugin`, `ILinearizePlugin`, `IMergePlugin`,
`ISplitPlugin`, `IRedactPlugin`, `IWatermarkPlugin`, `IRepairPlugin` and `IAccessibilityPlugin`.
They let a host reach a capability without referencing the assembly
that implements it. No `PdfOperations` call runs them.

`IPdfProcessingHook` is the one to read twice. It looks like "run my code before and after every
operation", and it is not: a hook registered to clean every document before it is written registers
without error and never runs. Call it from your own pipeline (the last example below).

## Example: a hook the library calls

```csharp
using System;
using System.Threading;
using Tetitulz.Pdf.Core.Plugins;

public sealed class PageTextLogger : ITextExtractionHook
{
    public string Name => "Contoso.PageTextLogger";
    public string Description => "Counts the characters extracted from each page.";
    public Version Version => new Version(1, 0, 0);

    public int Characters { get; private set; }

    public void Initialize() { }
    public void Shutdown() { }

    public void OnTextExtracted(int pageIndex, string text, CancellationToken cancellationToken = default)
        => Characters += text.Length;
}
```

```csharp
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Core.Plugins;

var logger = new PageTextLogger();
PdfPluginHost.Instance.Register(logger);

using var input = File.OpenRead("invoice.pdf");
string text = PdfOperations.ExtractText(input);
Console.WriteLine($"{logger.Characters} characters");
```

A hook is an observer. An exception it throws is swallowed so that it cannot stop the extraction;
only cancellation passes through. Do not put a check that must not fail silently in a hook.

## Example: a new export format

```csharp
using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;

public sealed class UpperCaseTextExport : IExportPlugin
{
    public string Name => "Contoso.UpperCaseTextExport";
    public string Description => "Writes the document's text in upper case.";
    public Version Version => new Version(1, 0, 0);

    public string FormatKey => "upper-txt";
    public string TargetMimeType => "text/plain";

    public void Initialize() { }
    public void Shutdown() { }

    public void Export(PdfDocument document, Stream output, ExportOptions options)
    {
        // options may be null: treat that as every default.
        byte[] bytes = Encoding.UTF8.GetBytes(PdfTextExtractor.ExtractText(document).ToUpperInvariant());
        output.Write(bytes, 0, bytes.Length);
    }
}
```

```csharp
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Core.Plugins;

PdfPluginHost.Instance.Register(new UpperCaseTextExport());

using var input = File.OpenRead("report.pdf");
using var output = File.Create("report.txt");
PdfOperations.Export(input, output, "upper-txt");
```

The format key is matched without regard to case. With no plugin registered for the key, `Export`
throws `NotSupportedException`. The caller owns both streams. Format-specific settings travel in
`ExportOptions.Extra`, a dictionary the plugin reads by its own keys.

## Example: a discovery contract in your own pipeline

```csharp
using Tetitulz.Pdf.Core.Plugins;

byte[] pdf = File.ReadAllBytes("input.pdf");
var host = PdfPluginHost.Instance;

// Your pipeline runs the processing hooks; the library does not.
pdf = host.InvokeBeforeProcess("stamp", pdf);

// A capability by its contract, whichever assembly supplies it.
IWatermarkPlugin watermark = host.GetFirstPlugin<IWatermarkPlugin>();
if (watermark != null)
{
    using var input = new MemoryStream(pdf);
    using var output = new MemoryStream();
    watermark.AddWatermark(input, output, new PluginWatermarkOptions { Text = "DRAFT", Opacity = 0.2 });
    pdf = output.ToArray();
}

pdf = host.InvokeAfterProcess("stamp", pdf);
```

The processing hooks run in registration order, each receiving the bytes the one before returned.

## Other seams

Not everything is a plugin. These are single registration calls:

| To supply | Call |
|---|---|
| A directory of fonts, searched before the system's | `FontSearchPath.Add` (the `Plugins.Fonts` package does this for its bundled fonts through `BundledFonts.Register()`) |
| A predefined CMap | `PredefinedCMapRegistry.Register` |
| Your own ICC colour engine | `IccTransformProvider.Register` with an `IIccTransformProvider` |
| Decryption of encrypted input through the ambient holder | `PdfEncryptionOperations.Register()` |
| A private key on a smart card or HSM, for a certificate-encrypted file | an `IExternalDecryptor` |
| Trusted root certificates for verification | `TrustAnchorLoader`, into `SignatureVerificationOptions.TrustAnchors` |

## Rules for a plugin you ship

- **A unique `Name`**, prefixed with your company. It is the key for `Unregister` and `IsRegistered`.
- **Throw `PdfException` for a bad file**, or a subclass of it. A host catches that one type for
  everything the library refuses.
- **Leave the streams open.** The caller owns every stream it passes.
- **Treat the file as hostile.** A document's counts, lengths and nesting are chosen by whoever made
  it. Bound what you allocate and how deep you recurse. `PdfLimits` holds the library's own ceilings.
- **A document is not shared across threads.** Do not keep the `PdfDocument` an `Export` call hands
  you after the call returns.
- **`Initialize` and `Shutdown` run once each**, at `Register` and at `Unregister` or
  `ShutdownAll`. Keep `Initialize` cheap; it may throw to refuse registration.

The packages in this folder are working examples. `Tetitulz.Pdf.Plugins.Text` is a small export
plugin; `Tetitulz.Pdf.Plugins.CMap` shows the preferred shape, where the mechanism is in Core and
the plugin carries only the data.
