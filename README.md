# Tetitulz.Pdf

A **pure-managed .NET PDF library**: parse, render, extract, edit, sign, encrypt, validate and
convert PDFs with no native PDF engine underneath. Rendering draws through
SkiaSharp; everything that reads or writes a PDF is managed code this repository owns, including the
JPEG 2000, JBIG2 and CCITT codecs, the PostScript interpreter, the ICC evaluator and the OpenType
shaper.

There are **29 packages**. All but two multi-target `net10.0` and `netstandard2.0`, so the same
assemblies serve modern .NET and .NET Framework hosts; the WinForms viewer targets
`net462;net10.0-windows` and `Printing`, which drives the Windows spooler, `net10.0-windows` alone.

> **Status.** The packages and two zipped apps are attached to each
> [GitHub release](https://github.com/tetitu/tetitulz.pdf/releases); they are not on nuget.org yet.
> The API is not frozen. See [Versioning](#versioning).

---

## What it does

Each row names the public door to start from. `PdfOperations` (in `Tetitulz.Pdf.Base`) is the
facade for the common operations; signing and encryption live in their own assemblies so that `Base`
does not carry the crypto stack into every consumer.

| Capability | Entry point | Package |
|---|---|---|
| Open and inspect the object model, recover damaged files | `PdfDocument.Open`, `PdfRepair` | `Core`, `Plugins.Maintenance` |
| Render pages to PNG, JPEG, WebP, raw pixels, bands, tiles or SVG | `PdfOperations.RenderPageToPng`, `RenderPageToPixels`, `RenderPageToSvg`, `PdfRenderSession` | `Base` |
| Render separations (CMYK and spot plates), simulate overprint, soft-proof against an ICC profile | `PdfOperations.RenderPageSeparations`, `PdfRenderOptions.SimulateOverprint`, `ProofProfile` | `Base` |
| Extract text: plain, structured with geometry, tagged in reading order | `PdfOperations.ExtractText`, `ExtractStructuredText`, `PdfTaggedTextExtractor` | `Base` |
| Extract tables, spatial queries, label-to-value fields, Israeli invoices | `PdfOperations.FindTables`, `PdfSpatialQuery`, `ExtractFields`, `IsraeliInvoiceExtractor` | `Base` |
| Extract images, links, graphics, attachments, bookmarks, metadata, revisions | `PdfOperations.ExtractImages`, `ExtractLinks`, `GetBookmarks`, `GetMetadata`, `GetRevisions` | `Base` |
| Forms: read, fill, flatten, create fields; FDF and XFDF in and out | `PdfFormFiller`, `PdfOperations.FlattenForm`, `PdfFormFieldBuilder`, `PdfFormDataExchange` | `Base` |
| Static XFA filling; a dynamic XFA form's content or its designed layout as a document | `PdfXfaFormFiller`, `Builder.Import.XfaContent`, `Builder.Import.XfaLayout` | `Base`, `Builder` |
| OCR a scan into a searchable PDF | `PdfOperations.MakeSearchable` | `Base` + `Plugins.Ocr` |
| Images: replace in place (signature kept), convert colour spaces, optimise, image to PDF | `PdfOperations.ReplaceImage`, `PdfColorSpaceConverter`, `PdfOptimizer`, `CreatePdfFromImages` | `Base`, `Plugins.Maintenance` |
| Annotations with appearances; flatten or remove them | `PdfAnnotationWriter`, `PdfOperations.FlattenAnnotations`, `RemoveAnnotations` | `Base` |
| Redact by region or by text search, removing the content | `PdfRedactor.RedactRegions`, `RedactText`, `FindTextMatches` | `Plugins.PageEdits` |
| Remove a content class (text, images or vector graphics) from every page | `PdfOperations.RemoveContent` | `Base` |
| Replace literal text, or re-break the paragraph around a longer replacement | `PdfTemplateEngine.ReplaceText`, `PdfTextReflow.Replace` | `Base` |
| Fill a template: AcroForm fields and `{{token}}` page text, Hebrew and RTL included | `PdfTemplateFiller.Fill` | `Plugins.Templating` |
| Merge, split, rotate, reorder, N-up, watermark, Bates numbers, page labels, layers | `PdfMerger`, `PdfSplitter`, `PdfPageManipulator`, `PdfWatermarker`, `PdfBatesNumbering`, `PdfOcgManager` | `Plugins.PageEdits`, `Base` |
| Sanitize: strip JavaScript, launch and URI actions, embedded files | `PdfSanitizer` | `Core` |
| Optimise, linearise, repair, compare (text and pixels) | `PdfOptimizer`, `PdfLinearizer`, `PdfRepair`, `PdfOperations.ComparePdfText`, `ComparePdfVisual` | `Plugins.Maintenance`, `Base` |
| Progressive reading of a linearised file from a byte-range source | `PdfProgressivePageReader`, `PdfLinearizationHintReader` | `Base` |
| Encrypt and decrypt with passwords or certificates; run any operation on an encrypted file | `PdfEncryptor`, `PdfDecryptor`, `PdfPublicKeyEncryptor`, `PdfEncryptedRoundTrip.Run` | `Encryption` |
| PDF MAC (ISO/TS 32004): write and verify | `PdfEncryptor` (`EnablePdfMac`), `PdfMacVerifier.Verify` | `Encryption` |
| Sign and verify: PAdES B-B to B-LTA, LTV, timestamps, counter-signatures, DocMDP, usage rights, ML-DSA | `PdfSigner`, `PdfSignatureVerifier`, `PdfSigningOperations` | `Signing` |
| Sign from the OS key store, a smart card or an HSM; anchor a signature on Ethereum | `CertStoreExternalSigner`, `PdfBlockchainSigner` | `Plugins.CertStoreSign`, `Plugins.EthereumSign` |
| Validate and convert to PDF/A, PDF/X and PDF/UA | `PdfAValidator`, `PdfXValidator`, `PdfUaChecker`, `PdfAConverter`, `PdfXConverter` | `Plugins.Conformance` |
| Audit, auto-tag and remediate for PDF/UA-1 and PDF/UA-2 | `PdfAccessibility` | `Accessibility` |
| Build a document in code and export it as PDF, DOCX, HTML or ODT | `DocumentBuilder`, `PdfExporter` | `Builder` |
| DOCX to PDF; PDF to DOCX, XLSX, PPTX | `DocxToPdfConverter`, `PdfToDocxConverter` | `DocxToPdf`, `Plugins.Docx` |
| HTML or Markdown to PDF, with a managed engine or a browser; fillable forms; page JavaScript | `HtmlToPdfConverter`, `MarkdownToPdfConverter`, `HtmlFillableForms` | `HtmlToPdf`, `Plugins.Html`, `Plugins.HtmlBrowser`, `Plugins.HtmlScript` |
| PDF to HTML, text or SVG | `PdfHtmlConverter`, `PdfOperations.Export` | `Plugins.Html`, `Plugins.Text`, `Plugins.Svg` |
| SVG to PDF | `SvgToPdfConverter` | `Base` |
| PostScript and EPS to PDF; embedded PostScript rendered | `PostScriptConversion`, `PdfOperations.Import` | `Plugins.PostScript` |
| Barcodes: generate (as a PDF or a content stream) and read | `PdfOperations.GenerateBarcodePdf`, `PdfBarcodeWriter`, `PdfBarcodeReader` | `Base` |
| Run document JavaScript in a sandbox | `PdfOperations.ExecuteJavaScript` | `Plugins.JavaScript` |
| Fonts for a host that has none (Liberation, Noto Sans Hebrew and Arabic, OFL) | `BundledFonts.Register` | `Plugins.Fonts` |
| Predefined CJK CMaps | `Plugins.CMap` | `Plugins.CMap` |
| An embeddable WinForms viewer | `PdfViewerControl` | `WinForms.PdfViewer` |

What the library does **not** do is stated in each package's README, beside its contract and limits.

---

## Getting started

The packages are not on NuGet yet. Build from source and reference the projects you need:

```bash
git clone <repository-url>
cd tetitulz.pdf
dotnet build tetitulz.pdf.sln -c Release
dotnet test  tetitulz.pdf.sln
dotnet add reference path/to/src/tetitulz.pdf/Tetitulz.Pdf.Base/Tetitulz.Pdf.Base.csproj
```

Every API takes a `Stream` and leaves ownership with the caller. Nothing is disposed for you.

### Build a PDF

```csharp
using Tetitulz.Pdf.Builder;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;

var doc = new DocumentBuilder()
    .WithTitle("Quarterly Report")
    .AddSection(s => s
        .PageSize(595, 842)                 // A4, in points
        .Margins(72, 72, 72, 72)
        .Header(h => h.AddParagraph(p => p.AddText("Company Name").Bold().FontSize(10)))
        .AddParagraph(p => p
            .Alignment(TextAlignment.Center)
            .AddText("Quarterly Report").Bold().FontSize(24).Color("#0078D4"))
        .AddParagraph(p => p.AddText("Revenue grew 12% this quarter.").FontSize(12))
        .AddTable(t => t
            .AddRow(r => r.AddCell("Item").Bold().AddCell("Price").Bold())
            .AddRow(r => r.AddCell("Widget").AddCell("$10")))
        .AddListItem("First point")
        .AddListItem("Second point", numbered: true))
    .Build();

using var output = File.Create("report.pdf");
new PdfExporter().Export(doc, output);
```

The same `Document` exports through `DocxExporter`, `HtmlExporter` or `OdtExporter`. A formatting
call styles the run before it: `.AddText("Total: ").AddText("$1,240").Bold()` bolds only the amount,
and `.Bold()` with no run in front of it throws rather than producing plain text. Right-to-left text
is first class: `.Rtl()` on a section or paragraph, or `.Direction(TextDirection.RightToLeft)`.

### Render, extract, sign

```csharp
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;

byte[] png = PdfOperations.RenderPageToPng(File.OpenRead("input.pdf"), pageIndex: 0, dpi: 150);

string text = PdfOperations.ExtractText(File.OpenRead("invoice.pdf"));
var layout  = PdfOperations.ExtractStructuredText(File.OpenRead("invoice.pdf"), pageIndex: 0);
foreach (var line in layout.Lines) Console.WriteLine($"{line.Y:F1}: {line.Text}");

new PdfSigner().Sign(File.OpenRead("contract.pdf"), File.Create("contract-signed.pdf"),
    new SignatureParameters
    {
        CertificateStream   = File.OpenRead("signer.pfx"),
        CertificatePassword = "…",
        Reason              = "Approved",
        DigestAlgorithm     = "SHA-256",
    });
```

Verification is two layers deep. `IsValid` says the bytes match the signature; trust needs
`SignatureVerificationOptions.TrustAnchors`. Read [Security](#security) before showing a tick.

### Edit under a signature, or recover what an edit hid

```csharp
using Tetitulz.Pdf.Base.Operations.Images;

var result = PdfOperations.ReplaceImageByName(File.OpenRead("signed.pdf"), File.Create("updated.pdf"),
    pageIndex: 0, "Im0", ReplacementImage.Jpeg(File.ReadAllBytes("new-logo.jpg")));
foreach (string warning in result.Warnings) Console.WriteLine(warning);

foreach (var r in PdfOperations.GetRevisions(File.OpenRead("updated.pdf")))
    Console.WriteLine($"revision {r.Index}: {r.Length} bytes");
PdfOperations.ExtractRevision(File.OpenRead("updated.pdf"), 0, File.Create("as-signed.pdf"));
```

Every signature-preserving edit appends a revision; the earlier bytes stay in the file, which is why
the signature over them still verifies and why `ExtractRevision` can hand them back. A replaced image
is hidden, not deleted. To remove content, redact (`PdfRedactor.RedactRegions`), which rewrites the
file and invalidates every signature. The two are opposite trades and cannot be combined.

### Audit and remediate for accessibility

```csharp
using Tetitulz.Pdf.Accessibility;

var report = PdfAccessibility.RemediateWithReport(File.OpenRead("report.pdf"), File.Create("report-ua.pdf"),
    new RemediationOptions { Title = "Q3 Financial Report" });
Console.WriteLine(report.ToDetailedString());
```

The pipeline auto-tags, marks running headers and footers as artifacts, repairs reading order and
writes the PDF/UA identifier. Two things only the caller can supply: a title, and a font program for
embedding (`EmbedMissingFonts` with `FontEmbedding.FontProvider`). Without them the residual is
exactly UA04 and UA21/UA25, reported rather than glossed over. A clean audit is not a conformance
certificate: alt-text accuracy and reading-order sense are not machine-decidable, and ISO 14289
requires human review. The full check table and the encrypted-document matrix are in
[`src/features/Tetitulz.Pdf.Accessibility/README.md`](src/features/Tetitulz.Pdf.Accessibility/README.md).

### Lay out a dynamic XFA form

```csharp
using Tetitulz.Pdf.Builder.Import;

var laid = XfaLayout.Read(File.OpenRead("dynamic-form.pdf"));
if (laid == null) Console.WriteLine(XfaLayout.LastError);
else File.WriteAllBytes("laid-out.pdf", laid.Document.ToPdf());
```

A dynamic form has no pages until an XFA processor lays them out, so other readers show the
producer's placeholder. `XfaLayout` lays the `template` packet out with the `datasets` packet;
`XfaContent` reads the values alone. What either approximates is listed on the result.

---

## Platforms

| | Status |
|---|---|
| **Windows** x64, x86, arm64 | Supported. The full solution builds, tests and packs here. |
| **Linux** x64, arm64 (glibc and musl) | Supported. Rendering and standard-14 substitution are verified on Ubuntu by pixel assertion; the portable subset (`tetitulz.pdf.portable.slnf`) builds and tests green. |
| **macOS** | Native assets ship and the code is platform-neutral, but no run has been recorded yet. Treat it as unverified. |
| Windows-only by construction | `Tetitulz.Pdf.WinForms.PdfViewer`, and everything under `apps/` and `demo/`. Nothing in the library depends on them. |

`Tetitulz.Pdf.Core` has no native dependency at all. `Tetitulz.Pdf.Base` references SkiaSharp's
native assets for all three desktop platforms; there is no GDI+, DirectWrite or WIC anywhere. The
library makes no P/Invoke of its own (guarded by a test); native code runs only inside SkiaSharp and,
optionally, Tesseract.

**On Linux**, install `libfontconfig1` (SkiaSharp links against it) and `fonts-liberation`, the
metric-compatible stand-ins for Arial, Times New Roman and Courier New that the standard 14 fonts map
onto. The core packages ship no fonts. `Tetitulz.Pdf.Plugins.Fonts` is the one package that does,
under the SIL Open Font License: `BundledFonts.Register()` writes Liberation and Noto Sans Hebrew and
Arabic once and puts their directory first in the search path, so a container with no fonts renders
predictably. `TETITULZ_PDF_FONTDIR` replaces the OS font directories for byte-reproducible output.

**A reader does not shape text; a producer does.** The renderers draw the glyphs the file chose
(a non-embedded Arabic PDF already stores presentation forms). The Builder, DocxToPdf, HtmlToPdf and
the appearance generators shape through the managed `OpenTypeShaper` (joining, ligatures, mark
positioning, kerning) and record the typed letters in `/ToUnicode`, so search and copy return what
the author wrote.

---

## Hosting checklist

What a service does once, at startup. Each item is something a host has got wrong silently.

1. **Fonts.** Install `fonts-liberation` or call `BundledFonts.Register()`. A host with no fonts does
   not fail; a page of text renders blank.
2. **Encrypted input.** Read-only operations take a password directly; whole-file writers refuse an
   encrypted file rather than corrupt it. Go through `PdfEncryptedRoundTrip.Run`, or a writer's `Plan`
   with `PdfEncryptedIncrementalUpdate` to keep a signature and the owner password. A host using the
   ambient decryptor must call `PdfEncryptionOperations.Register()` once; **a project reference to
   `Tetitulz.Pdf.Encryption` decrypts nothing by itself**, and the failure is silent (`GetPageCount`
   answers 0).
3. **Plugins.** OCR, embedded PostScript, JavaScript, the browser HTML engine and the bundled fonts
   are plugins. Register each through `PdfPluginHost.Instance.Register(...)` or the package's own
   door; writing your own is in
   [`src/plugins/README.md`](src/plugins/README.md). With none registered a feature degrades the way its README says, never by throwing from an
   unrelated operation.
4. **Limits.** Every `PdfLimits` ceiling is a settable static, process-global, set before the first
   document is opened. The defaults let a legitimate 400-page document through; a public upload
   endpoint should lower the ones that matter to it. Raising one is a security decision.
5. **Threads.** Documents are not shared across threads. Render each page from one thread, or use
   `PdfRenderSession` for tiles of one page. The `Last*` diagnostic slots are per thread and are read
   on the thread that ran the operation.
6. **Diagnostics.** Subscribe to `RenderDiagnostics.OnEvent` in a staging run: everything a render
   approximates rather than draws as the file asks is reported there, once, with the clause it
   deviates from. A silent render did what the file said.
7. **Network.** Revocation fetches, timestamping and HTML's remote resources go through one guarded
   fetcher: private, loopback and link-local addresses are refused, redirects are re-checked per hop,
   and a credential rides only to the host it was scoped to. Verification asks online only when
   `SignatureVerificationOptions.OnlineRevocation` is set; a failed fetch is a warning, never a verdict.

---

## Security

The library parses untrusted input by design: cumulative decompression caps, recursion and step
budgets, work budgets shared across a page's decoders, XXE closed at every XML entry point, and the
address guard on every outbound fetch. The readers are fuzzed: coverage-guided over the public doors,
structurally with hostile object graphs and hostile markup, and over public corpora.

```csharp
using Tetitulz.Pdf.Core.Constants;

PdfLimits.MaxDecompressedBytes = 32L * 1024 * 1024;   // default 512 MB; process-global, set at startup
PdfLimits.MaxObjectGraphDepth  = 64;                   // default 256
PdfLimits.ResetToDefaults();
```

Three things to know before relying on a signature:

- `IsValid` is **integrity only**. A trusted signature is `IsFullyTrusted`, judged against an
  explicit `TrustAnchors` set. Without anchors nothing can ever be `Trusted`.
- Revocation is judged from the document's embedded `/DSS` by default, so verification is
  deterministic and offline. The online fallback is opt-in.
- `CoversEntireDocument` tells a signature over the current bytes from one over an earlier revision.
  A counter-signed document legitimately has signatures that stop short of the end; so does a shadow
  attack. Deciding what the later revisions changed is the application's call.

Whole-file rewrites (sanitize, optimize, linearize, redact, convert) fail closed on encrypted input.
`PdfEncryptedRoundTrip.Run` decrypts, runs the operation over the plaintext and re-encrypts; it cannot
preserve the owner password (the file carries only one-way derivations of it), refuses a signed
document unless told to invalidate the signature, and fails with a reason on a scheme it cannot
reproduce. A certificate-protected document takes a `Credential` and the recipients to re-encrypt to.

The threat model, what is in and out of scope, and how to report: [`SECURITY.md`](SECURITY.md).

---

## Repository layout

```
src/tetitulz.pdf/    Core, Base, Encryption, Signing, WinForms.PdfViewer
src/features/        HtmlToPdf, DocxToPdf, Builder, Accessibility, Printing
src/plugins/         19 plugins: CertStoreSign, CMap, Conformance, Docx, EthereumSign, Fonts,
                     Html, HtmlBrowser, HtmlScript, JavaScript, Maintenance, Ocr, PageEdits,
                     PostScript, Renderers, SignatureVerify, Svg, Templating, Text
tests/               One test project per production assembly, plus TestHelpers
apps/, demo/         Sample hosts and diagnostic tools (never packed)
docs/                The documentation index
```

The project graph is a directed acyclic graph with `Core` at the bottom: the object model, parsing,
filters, writing and the signing primitives, with no SkiaSharp. `Base` adds the operations and the
two renderers (raster and SVG, held at parity by tests that compare them). Features depend on Core and
Base; a plugin earns its place by keeping a dependency out of the core graph. The plugin contracts
live in `Tetitulz.Pdf.Core.Plugins`, so a host that wants only the interfaces references `Core`. The
rules are in [`CLAUDE.md`](CLAUDE.md), with their reasons in [`CONTRIBUTING.md`](CONTRIBUTING.md).

---

## Testing

```bash
dotnet test tetitulz.pdf.sln                                      # everything
dotnet test tetitulz.pdf.sln --filter "Category!=Corpus"          # without the developer-local corpus
TETITULZ_PDF_CORPUS=/path/to/pdfs dotnet test tetitulz.pdf.sln   # with it
```

Tests build minimal PDFs in memory. The corpus-gated ones report **Skipped**, visibly, until a corpus
is pointed at. A test that cannot fail is a build error here:
[`NoUnfalsifiableAssertionsTests`](tests/Tetitulz.Pdf.Base.Tests/TestInfrastructure/NoUnfalsifiableAssertionsTests.cs)
rejects any test whose assertions hold with the feature deleted, with no allow-list. Every new test is
proved by mutating the code it covers. [`tests/README.md`](tests/README.md) explains the machinery.

---

## Versioning

Versions come from git tags through MinVer; an untagged build is a prerelease. A `v*` tag that
passes CI on Windows, Linux and macOS becomes a GitHub release with the packages and the zipped WPF
viewer and SignCli attached. A minor version may still break the API: defects are still surfacing at
a steady rate. `dotnet pack tetitulz.pdf.sln -c Release -o artifacts` produces all 29 packages;
none is on nuget.org.

---

## Documentation

| Document | What it covers |
|---|---|
| [`docs/README.md`](docs/README.md) | The index: which document owns what, per-project READMEs, getting set up. |
| [`src/plugins/README.md`](src/plugins/README.md) | Extending the library: plugins, hooks and seams, with worked examples. |
| [`CLAUDE.md`](CLAUDE.md) | The rulebook digest: invariants, shared decision points, conventions, testing rules. |
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | The rulebook's long form, each rule with its reasoning, and the key-file map. |
| [`SECURITY.md`](SECURITY.md) | Trust boundary, threat model, scope, reporting. |
| [`NOTICE.md`](NOTICE.md) | Third-party components and their licences. |

## Roadmap

**No external dependencies.** The direction is a library that is managed code from end to end, with
nothing native and nothing third-party underneath it. SkiaSharp goes first: version 2 draws pages,
decodes and encodes images and rasterizes glyphs with this repository's own code, so the packages
deploy as managed assemblies alone and a pixel is the same on Windows, Linux, macOS and WebAssembly.
The other third-party packages follow,
each replaced where an in-house implementation can be held to the same tests.

## How you can help

The library is written and maintained by one person. Three things would move it faster than code
contributions alone:

- **Claude usage.** Development leans on Claude, paid for from a personal account whose limits set
  the pace. Sponsoring a larger plan or API credit buys directly more reviewed, tested work.
- **A code-signing certificate.** The Windows apps and installers are unsigned, so Windows warns
  before running them. A certificate, or the cost of one, lets the releases be signed.
- **The PDF/A and PDF/X standards.** The conformance checkers are written against ISO 19005
  (PDF/A) and ISO 15930 (PDF/X), which are sold per part. Funding licensed copies of the parts not
  yet held, or access through an organization entitled to share them, closes the clauses still
  checked from public sources alone.

To offer any of these, open an issue on
[github.com/tetitu/tetitulz.pdf](https://github.com/tetitu/tetitulz.pdf). Bug reports, test files
that break a reader and pull requests are welcome too; [`CONTRIBUTING.md`](CONTRIBUTING.md) has the
rules, and a security problem goes privately as [`SECURITY.md`](SECURITY.md) describes.

## License

Copyright (c) 2026 Tamir Mordo. Two licences, by package:

| Packages | Licence |
|---|---|
| Everything except the three below: Core, Base, Signing, Encryption, the WinForms viewer, the features and sixteen of the plugins | [Apache License 2.0](LICENSE), with the credit in [`NOTICE`](NOTICE) |
| `Plugins.PostScript`, `Plugins.Maintenance`, `Plugins.PageEdits` | [Tetitulz.Pdf Attribution License 1.0](LICENSE-ATTRIBUTION.txt) |

**Apache 2.0.** Anyone distributing the library, or a work built from it, passes on [`NOTICE`](NOTICE), whose credit
— "Uses Tetitulz.Pdf by Tamir Mordo" — then appears wherever that work lists its third-party notices.

**The Attribution License** is the MIT License with conditions that bind **every use of those three plugins, of the
whole or of any part however small**: running or depending on them, changing, forking, porting or deriving from them,
using them or their output as an oracle, reference or benchmark, offering them through an API, SDK, plugin or an
interface for AI models and agents (the one offering the interface owes the conditions, not its callers), and learning
from them (a work based on studying them, training a model on them, or AI output reproducing them):

- **Recognition** — state "Uses Tetitulz.Pdf by Tamir Mordo" where that work's credits belong; for an API or an AI
  tool, also in the description its callers and the AI model are shown.
- **Report the use** — open an issue titled "Used by: &lt;name of the work&gt;" on
  [github.com/tetitu/tetitulz.pdf](https://github.com/tetitu/tetitulz.pdf) within 30 days of the first use, naming who
  uses it, the work, the plugins, the kinds of use and the parts used, and where the recognition appears.
- **Report security problems, with the fix** — a vulnerability found in them, or in a derived work's copy of them, is
  reported privately as [`SECURITY.md`](SECURITY.md) describes within 14 days, with the fix if there is one; neither
  is published until a fix is released or 90 days have passed.

**For the Apache-licensed packages these three are requests, not conditions** — please credit the library in the
same places (in an API's or an AI tool's description too), open a "Used by" issue, and report a security problem
privately with its fix if you have one.

Third-party components keep their own licences (MIT, BSD, Apache 2.0 or OFL); see [`NOTICE.md`](NOTICE.md). The CMap,
Fonts and HtmlScript packages carry such material and declare both licences (`Apache-2.0 AND BSD-3-Clause`,
`Apache-2.0 AND OFL-1.1`, `Apache-2.0 AND MIT`).