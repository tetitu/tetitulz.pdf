# Tetitulz.Pdf.HtmlToPdf

Converts HTML content, files, or URLs to PDF via **two independent engines** — a headless
browser (Playwright) or a fully managed engine with no browser and no native dependency.

**Targets:** `net10.0` and `netstandard2.0`. On .NET Framework it needs a `System.Memory` binding
redirect. An SDK-style executable generates it (`AutoGenerateBindingRedirects`, on by default); a
host that generates none, such as a `web.config`, adds
`<bindingRedirect oldVersion="0.0.0.0-4.0.5.0" newVersion="4.0.5.0" />` for `System.Memory`
(`PublicKeyToken` `cc7b13ffcd2ddd51`).
**Package dependencies:** `AngleSharp` — Playwright is **not** referenced here; it lives in
`Tetitulz.Pdf.Plugins.HtmlBrowser`, which supplies the browser engine through `IHtmlBrowserPlugin`.
Page JavaScript for the managed engine is likewise a plugin, `Tetitulz.Pdf.Plugins.HtmlScript`,
through `IHtmlScriptPlugin`.
**Project dependencies:** `Tetitulz.Pdf.Core`, `Tetitulz.Pdf.Builder`

> **The managed engine is Builder's document model, not a second PDF writer.** It maps HTML to a
> `Tetitulz.Pdf.Builder.Model.Document` and hands it to Builder's `PdfExporter`; this assembly
> contains no PDF syntax at all. So a rendering capability is added in `Builder.Model` +
> `PdfExporter` and then *mapped* here — never here alone. (It does **not** go through Builder's
> fluent `DocumentBuilder`, which is a separate producer of the same model.)

---

## Render engines

Select with `HtmlToPdfOptions.RenderEngine` (`Engine` picks *which browser* within the browser
path):

| Engine | What it needs | When to use |
|---|---|---|
| `HtmlRenderEngine.Managed` (default) | Nothing — pure managed, no browser, no SkiaSharp by default | Servers and containers where installing a browser is impractical; deterministic, in-process conversion. |
| `HtmlRenderEngine.Browser` | The `Tetitulz.Pdf.Plugins.HtmlBrowser` plugin, Playwright, and a downloaded browser (~300 MB, out-of-process) | Full CSS fidelity: grid, positioning, JavaScript, web fonts, remote resources. |

The two are a deliberate pair and the caller chooses per call: managed is faster and lighter,
the browser is more capable and costs a great deal more in resources. Managed is the default
because the cheap option should be the one you get without asking. **Neither falls back to the
other** — each refuses what it cannot do rather than silently switching.

`RenderEngine.Managed` is honoured by **every** HTML- and file-input entry point, synchronous and
asynchronous alike — including through `IHtmlToPdfConverter`. The URL entry points refuse it, since
the managed engine does not navigate: it fetches nothing of its own, and subresources only through
the opt-in `RemoteResourceLoader` (below). They throw rather than silently launching a browser.

The **managed** engine supports the `<style>` cascade (tag/class/id/descendant selectors with
approximate specificity, property inheritance, `!important`, `@media` for print or screen), `@supports`
flattening, serif font mapping, `em`/`rem`/percentage lengths, block margin/padding/line-height/
text-indent, `hsl()`/`hsla()`/`rgba()` colours, the `font` shorthand, `<colgroup>`/`<col>` and
explicit column widths, `hidden`/`display:none`/`visibility:hidden`, CSS custom properties, and
forced page breaks. Tables keep their structure — including tables nested inside a cell — and
`<thead>`/`<tfoot>` render in the right place regardless of where they are authored; no column is
narrower than its longest word while the table can hold every column's minimum. A bordered block's
padding is inside its border, and an inline element with padding or a border on every side (a
label, a badge, a button) is drawn in its own box. A block whose content is blocks (a panel, a
well, a grid row) draws its margins, border, padding and background round them, across a page
break if it has to — sliced there, with no border at the cut. A negative horizontal margin widens
it, as Bootstrap's `.row` does to cancel `.container`'s padding; a negative vertical one is read
as 0.

### Page breaks

| CSS | Effect |
|---|---|
| `break-before` / `page-break-before`, `break-after` / `page-break-after`: `always`, `page`, `left`, `right` | A page break before / after the element. |
| `break-inside` / `page-break-inside`: `avoid`, `avoid-page` | The element starts on the next page if it would otherwise straddle a page boundary. An element taller than a page flows normally — `avoid` is a preference. |
| `break-after` / `page-break-after`: `avoid` | The element stays on the same page as the start of what follows it (a heading with its paragraph's first lines — as many as its `orphans` — or its table's first row, or a bordered or padded box's first block). A bordered or padded element is kept whole, both for this and for `break-inside: avoid`. |
| `orphans` / `widows` | A paragraph breaking across a page keeps at least that many lines at the foot of the page / the top of the next — 2 unless declared, as in a browser. They inherit. Where no break can satisfy both and the paragraph already starts a page, it breaks where its lines run out. |
| `<thead>` rows | Repeated at the top of every page the table continues onto. In a table with no `<thead>`, a leading row of nothing but `<th>` is the header. A `<th>` further down labels its row and is not repeated. A header taller than half a page is not repeated. |

Table rows are never split across pages, so `tr { page-break-inside: avoid }` needs nothing.

### Right-to-left text (Hebrew, Arabic)

Fully supported, and `dir` **inherits**: the usual `<div dir="rtl">…</div>` wrapper applies to
everything inside it. `dir="ltr"` overrides an enclosing RTL context, and `dir="auto"` detects the
direction from the content (UAX #9 P2/P3). On an **inline** element `dir` also isolates, as HTML's
own stylesheet says — `<span dir="ltr">24/09/2026 07:28</span>` inside Hebrew keeps its written
order — unless the author's CSS declares a `unicode-bidi` of its own. Bidi is resolved per **line**, so a Latin phrase
embedded in Hebrew keeps its own reading order while sitting correctly relative to the Hebrew.
Arabic is contextually shaped, and the text is recorded in `/ToUnicode` as **base letters**, so
search and copy-paste return what a keyboard produces.

`HtmlToPdfOptions.DefaultTextDirection` decides what happens when nothing declares a direction.
It defaults to `Auto` — undeclared Hebrew is detected and right-aligned. Set it to `LeftToRight`
for strict browser parity.

`<bdo dir="rtl">` overrides the algorithm outright, `<bdi>` isolates content of unknown direction
from the text around it, and CSS `unicode-bidi` (`bidi-override`, `embed`, `isolate`, `plaintext`,
`isolate-override`) is honoured. The control characters that express these — U+202A–U+202E,
U+2066–U+2069 — change the level resolution but are **stripped before drawing**, so they never
appear on the page or in `/ToUnicode` and copy-paste stays clean.

> **Fonts are the thing that actually bites here.** Hebrew and Arabic need a font the standard-14
> set cannot provide. Left to itself the engine searches the host's installed fonts, which makes
> output depend on the machine — and on a container with none, every non-Latin-1 character is
> written as `?`. For deterministic output, supply one:
>
> ```csharp
> options.FontProvider = _ => File.ReadAllBytes("fonts/NotoSansHebrew-Regular.ttf");
> options.AllowSystemFontSubstitution = false;   // fail loudly instead of guessing
> options.OnDiagnostic = d => logger.LogWarning("{Detail}", d.Detail);
> ```
>
> `@font-face` works too — for fonts inside `BasePath`, `data:` URIs, and with a
> `RemoteResourceLoader` remote ones. Whatever you supply is **embedded and
> therefore redistributed** — you are responsible for holding the rights to it.

### Tagged (accessible) PDF

On by default for the managed engine (`HtmlToPdfOptions.Tagged`). Heading levels become
`/H1`–`/H6`, `<th>` becomes `/TH` with a `/Scope`, `<caption>` becomes `/Caption`, `alt` becomes a
`/Figure`'s `/Alt`, `<html lang>` becomes the catalog `/Lang`, and page furniture (rules, borders,
shading, running heads) is marked as artifacts. `alt=""` is treated as HTML defines it — decorative
— and becomes an artifact rather than a figure with no description.

**This is not a PDF/UA conformance claim, and nothing writes a conformance identifier.** The
standard-14 fonts are never embedded, so a Latin-only document still fails the font-embedding
checks; supply a font through `FontProvider` to close that. A document whose structure tree reached
no content writes neither `/StructTreeRoot` nor `/MarkInfo` — it will not claim to be tagged when
it is not.

### Opt-in extras

The first four default to off, because each widens what the converter will read. The last two are not opt-in: `UnrenderableText` defaults to `Report`, and `BlockPrivateNetworkAccess` defaults to **on** — it *narrows* what the browser engine may reach, and turning it off is the opt-in.

| Option | Effect |
|---|---|
| `HtmlToPdfOptions.BasePath` | Enables local-file images, `<link rel="stylesheet">` and `@font-face` sources. Paths are confined to the base directory. **Set it only for HTML you trust** — see the security note below. `ConvertFile` implies the input file's own directory. |
| `HtmlToPdfOptions.RemoteResourceLoader` | Fetches remote `http(s)` images, backgrounds, stylesheets and `@font-face` sources. Use `GuardedRemoteResourceLoader` for HTML you do not control — see *Remote resources* below. `MaxRemoteResources` (default 32) bounds how many distinct URLs one conversion fetches. |
| `HtmlToPdfOptions.FontProvider` | Supplies font programs for non-Latin-1 text. See above. |
| `HtmlToPdfOptions.SvgRasterizer` | Renders inline `<svg>` — and `data:image/svg+xml` images — as a raster. Supply e.g. `Tetitulz.Pdf.Base.Operations.Images.SvgRasterizer.RasterizeToPng`. Left `null`, SVG is skipped and the engine stays SkiaSharp-free. |
| `HtmlToPdfOptions.UnrenderableText` | `Report` (default, draws `?` and warns), `Omit`, or `Throw`. |
| `HtmlToPdfOptions.BlockPrivateNetworkAccess` | Browser path only. Refuses connections to loopback/private/link-local/metadata addresses, and local files outside the base directory. |

### Images

`data:` URIs (base64 and percent-encoded) and — with `BasePath` set — local files. **PNG
(including interlaced), JPEG, GIF and BMP** are embedded. An image the engine cannot decode is
*not* drawn and reserves no space: it reports through `OnDiagnostic`, naming the `src`, rather
than leaving a correctly-sized blank hole. Remote `http(s)` images are fetched only through a
`RemoteResourceLoader`. An image whose **file** is SVG — local or remote — is reported as
undecodable; only a `data:image/svg+xml` source reaches `SvgRasterizer`.

**Transparency is real.** PNG alpha and a GIF's transparent index become a PDF soft mask
(`/SMask`), so a transparent logo composites against whatever is behind it. Flattening over white
would be right only when the background *is* white — over a shaded paragraph or a coloured table
cell it would leave a white halo around every transparent edge.

**CMYK JPEG** embeds when it carries an Adobe APP14 marker, with the inverted `/Decode` those files
need. A four-component JPEG *without* the marker has no identifiable colour space and is refused
rather than guessed at — guessing wrong inverts the whole image.

`srcset` and `<picture>` are honoured: the highest-resolution candidate wins (print has no
viewport), `<source media>` is evaluated for the print medium, and a `<source type>` this engine
cannot decode is skipped so the fallback is reached.

Not embedded: WebP and TIFF.

### Form controls

Rendered as **static graphics** by default, which is what Chromium's own `page.pdf()` produces — so
the two engines agree. `<input>` shows its value (a `password` as bullets, never in clear text),
checkboxes and radios show their state, `<select>` shows the selected option only, and
`<textarea>` shows its value. `<form>`, `<fieldset>` and `<label>` are ordinary containers.

**Fillable forms are opt-in.** `Tetitulz.Pdf.Plugins.Html.HtmlFillableForms.ConvertHtmlToFillablePdf`
makes the controls into real AcroForm fields in one step:

```csharp
byte[] pdf = HtmlFillableForms.ConvertHtmlToFillablePdf(html,
    new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed });
```

Text-like inputs, `password` (never with a value), `checkbox`, `<textarea>`, `<select>` (a combo
box, or a list box with `multiple` or `size` above one) and `radio` become fields named by `name`,
else `id`. **Radios sharing a `name` are one radio-group field**, each button's `value` its export
value (HTML's default `on` when it has none), and the last `checked` wins as in a browser. A
repeated name gets a numeric suffix and a period becomes an underscore; within a radio group a
repeated value, or the value `Off` (reserved by the PDF for "none chosen"), gets a suffix too — all
reported, since the submitted names and values then differ from the markup's. Buttons and
`file`/`image` inputs stay drawings, reported once per kind. A `<textarea>` becomes a multi-line
field one line tall.

It is two steps because the layout here may reference only Core and Builder, while the field builder
and its appearance generation are in Base: `HtmlToPdfConverter.ConvertHtmlWithFormFields` returns the
PDF with shaded placeholders plus where each one landed, and `PdfFormFieldBuilder.AddFields` makes
them fields in one appended revision. The plugin does both.

### Headers and footers

`HeaderTemplate` / `FooterTemplate` work in the managed engine when `DisplayHeaderFooter` is set,
including Playwright's substitution classes — `pageNumber`, `totalPages`, `title`, `date`. The page
number is resolved per page and the total is back-filled once pagination finishes.

Three differences from the browser path worth knowing: enabling a running block **grows the page
margin** to make room for it (the block is drawn 0.5 in from the page edge, which is outside the
default 0.4 in margin), `date` comes from `HtmlToPdfOptions.RunningDate` rather than the system
clock — a built-in `DateTime.Now` would make the same input produce different bytes on every run —
and a running block **scales with `Scale`**, because the managed engine models it as page content
and one page-level matrix scales all of it. Chromium's print system draws its header and footer
outside the scaled content, at a fixed physical offset.

### Opacity

CSS `opacity` is honoured on blocks and inline elements, as PDF constant alpha (`/ca`, `/CA`). It
compounds down the tree the way CSS does — `opacity:.5` inside `opacity:.5` draws at 0.25 — and
percentages work.

**It is constant alpha, not group opacity, and the difference is reported rather than hidden.**
CSS composites an element once as a unit; PDF's `/ca` fades each object separately. The two agree
until an element paints something *behind its own content*, at which point the text is drawn over
an already-faded background instead of the pair being faded together. That case raises
`BuilderDiagnosticKind.ContentApproximated` through `OnDiagnostic`; an ordinary faded paragraph
raises nothing, because there the two models agree exactly. Matching CSS in that last case needs a
transparency-group form XObject — use the browser engine if it matters.

**What the managed engine lays out**, measured against Chrome on Bootstrap 3 pages, plain-CSS pages
and an HTML e-mail: `flex` (`flex-wrap`, `order`, `justify-content`,
`flex-shrink`, growers and shrinkers by their factors), `grid` (explicit placement, spans,
`grid-template-areas`, named and negative lines, implicit tracks, `grid-auto-flow: column` and
`dense`, `repeat(auto-fill | auto-fit)`, `minmax()`, `fit-content()`, `min()`, `max()`, `clamp()`),
multi-column blocks balanced by measured height with `column-gap`, `column-rule` and
`column-span: all`, `float`/`clear` (a paragraph, a box of blocks, a run of floats, a floated inline
element — in a table cell or after a `<br>` it stays in the text, reported), `position` against the
nearest positioned ancestor or the page with `position: fixed` on every page, `transform` about its
`transform-origin`, `box-shadow` with blur and spread, `border-radius` per corner, `opacity` as a
transparency group, a stylesheet's `@import`, `::before`/`::after`, Selectors 4 specificity. Each
approximation is reported through `OnDiagnostic` as `BuilderDiagnosticKind.ContentApproximated`;
the list of what stays approximate — a positioned block inside a list item, a float or a flex item
holding only tables nested past the layout's cap — is `GAPS.md` §4b.

**JavaScript runs when `Tetitulz.Pdf.Plugins.HtmlScript` is registered** (`JintHtmlScriptPlugin`:
AngleSharp's DOM driven by Jint, sandboxed, no network of its own) and `JavaScriptEnabled` is set,
so the page laid out is the one its scripts produced. Without the plugin the page is laid out as
written and the engine reports that its scripts did not run.

**Still the browser engine's:** `<canvas>` drawing, `<iframe>`, `<video>`, and layout-dependent
script (`offsetWidth` answers 0, `getBoundingClientRect` is not provided) — each reported.
`::first-line` and `::marker` are not mapped. `GAPS.md` §4 and §8b list the declines with the
reason for each.

### `@media` and the viewport

`@media` width, height and orientation features — `min-width`, `max-width`, the range syntax
`(width >= 768px)` — are evaluated against the page's **content width in CSS pixels**, which is what
a browser's print evaluates them against: US Letter or A4 in portrait is about 720–740px, so a
responsive page prints its narrow layout (Bootstrap's `col-sm-*` columns stack and `hidden-xs`
hides), exactly as printing it from a browser does. `Landscape`, a larger page, or `Scale` below 1
widens it. To print the wide layout on the same paper, set `ViewportWidth`:

```csharp
options.ViewportWidth = 1200;   // CSS px; the paper size is unchanged
```

`<picture><source media>` is evaluated against the same viewport.

### Print or screen — `MediaType`

By default the page is styled for **print**, as a browser's "Save as PDF" styles it: `@media print`
rules and `<link media="print">` sheets apply, `screen` ones do not. A site's print stylesheet can
strip what a document needs — Bootstrap 3's removes every background, so a checklist whose ticks are
background images prints without them, from a browser too. Set `MediaType` to render the page as it
looks on screen, on the same paper:

```csharp
options.MediaType = HtmlMediaType.Screen;   // both engines; the browser one through media emulation
```

A page whose layout its own JavaScript finishes — moving elements, drawing a signature on a
`<canvas>` — needs the browser engine as well: the managed engine runs no script.

### Remote resources

Off by default: with no loader, every remote `http(s)` image, background, stylesheet and
`@font-face` source is declined and reported through `OnDiagnostic`. To fetch them:

```csharp
using var loader = new GuardedRemoteResourceLoader();   // reuse one: it owns an HttpClient
options.RemoteResourceLoader = loader;
```

`GuardedRemoteResourceLoader` refuses loopback, private, link-local and cloud-metadata addresses,
follows redirects by hand re-checking every hop, and bounds each fetch — 16 MiB and 15 s by
default, redirects and body included. A repeated URL is fetched once per conversion, and
`MaxRemoteResources` bounds how many distinct URLs a document can make a conversion wait on.
A scheme-relative URL (`//cdn.example/x.png`) is fetched as `https`.

**Relative `url()`s inside a fetched stylesheet resolve against the stylesheet's URL**, as in a
browser — never against `BasePath`, so another origin's stylesheet cannot name your local files.
Nor can it name them outright: in a fetched sheet a `url()` that is not `http(s)` or `data:` (a `file:`
URL, a drive path) names nothing, and an `@import` of anything not remote is declined and reported.
A local sheet keeps its `file:` references, still read only inside `BasePath`.

> **The connection dials the very address that was checked** — the client's connect callback on
> .NET 5 and later, the library's own transport on .NET Framework — so a DNS-rebinding name that
> answers differently a second time gains nothing. **Through a proxy the check is all there is**:
> the proxy resolves the host, so behind one (a system proxy that applies to the URL counts)
> implement `IRemoteResourceLoader` yourself with an **allow-list of hosts**, which no DNS answer
> can move.

Implement `IRemoteResourceLoader` whenever the host knows something the library cannot — an
allow-list, a cache, credentials. Return `null` with a `failure` reason to decline; the reason is
what `OnDiagnostic` reports.

**Security note on `BasePath`:** containment is path-based (`Path.GetFullPath` + relative-path
check), which normalises `..` but does **not** resolve symlinks or NTFS junctions. A symlink
placed inside the base directory can therefore point outside it. Do not set `BasePath` to a
directory that untrusted parties can write to.

---

## Prerequisites for the browser engine

The managed engine needs nothing. The browser engine needs the `Tetitulz.Pdf.Plugins.HtmlBrowser`
package registered, and Playwright's browser binaries installed on the machine — a deployment step,
not a NuGet one:

```csharp
PlaywrightHtmlBrowserPlugin.InstallBrowsers();                 // once, or as part of provisioning
PdfPluginHost.Instance.Register(new PlaywrightHtmlBrowserPlugin());
```

`HtmlToPdfConverter.InstallBrowsers()` is kept for source compatibility and throws, naming the
plugin's `InstallBrowsers` as the replacement.

---

## Public API

### BrowserEngine (enum)

```csharp
BrowserEngine.Chromium  // 0 - Default. Best PDF fidelity.
BrowserEngine.Firefox   // 1 - Good standards compliance.
BrowserEngine.WebKit    // 2 - Lightweight (Safari engine).
```

**Note:** `Page.PdfAsync()` is fully supported on Chromium. Firefox/WebKit may have limitations.

### HtmlToPdfConverter (static class)

#### Sync - File Paths

```csharp
// HTML file to PDF file
HtmlToPdfConverter.ConvertFile(string inputHtmlPath, string outputPdfPath, HtmlToPdfOptions options = null);

// HTML string to PDF file
HtmlToPdfConverter.ConvertHtml(string htmlContent, string outputPdfPath, HtmlToPdfOptions options = null);

// URL to PDF file
HtmlToPdfConverter.ConvertUrl(string url, string outputPdfPath, HtmlToPdfOptions options = null);
```

#### Sync - Byte Arrays

```csharp
byte[] pdf = HtmlToPdfConverter.ConvertFileToBytes(string inputHtmlPath, HtmlToPdfOptions options = null);
byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(string htmlContent, HtmlToPdfOptions options = null);
byte[] pdf = HtmlToPdfConverter.ConvertUrlToBytes(string url, HtmlToPdfOptions options = null);
```

#### Sync - Streams

```csharp
HtmlToPdfConverter.ConvertHtml(string htmlContent, Stream output, HtmlToPdfOptions options = null);
HtmlToPdfConverter.ConvertUrl(string url, Stream output, HtmlToPdfOptions options = null);
```

#### Async

```csharp
Task<byte[]> ConvertFileToBytesAsync(string inputHtmlPath, HtmlToPdfOptions options = null);
Task<byte[]> ConvertHtmlToBytesAsync(string htmlContent, HtmlToPdfOptions options = null);
Task<byte[]> ConvertUrlToBytesAsync(string url, HtmlToPdfOptions options = null);
```

#### Browser Installation

```csharp
// The browser engine's binaries: once, or as part of provisioning (Tetitulz.Pdf.Plugins.HtmlBrowser).
PlaywrightHtmlBrowserPlugin.InstallBrowsers();
```

`HtmlToPdfConverter.InstallBrowsers(BrowserEngine? engine = null)` is `[Obsolete]` and throws, naming the
plugin's call as the replacement.

### HtmlToPdfOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `Engine` | `BrowserEngine` | `Chromium` | Browser engine to use |
| `PageSize` | `HtmlPageSize?` | `Letter` | A common size — `A3`, `A4`, `A5`, `B4`, `B5`, `Letter`, `Legal`, `Tabloid`, `Executive` — written to `PageWidth` and `PageHeight` (portrait; `Landscape` turns it). Reads `null` for a custom size; whichever is set last wins. |
| `PageWidth` | `string` | `"8.5in"` | Page width with units (in, mm, px) — any size |
| `PageHeight` | `string` | `"11in"` | Page height with units |
| `MarginTop` | `string` | `"0.4in"` | Top margin with units |
| `MarginBottom` | `string` | `"0.4in"` | Bottom margin with units |
| `MarginLeft` | `string` | `"0.4in"` | Left margin with units |
| `MarginRight` | `string` | `"0.4in"` | Right margin with units |
| `Landscape` | `bool` | `false` | Landscape orientation |
| `PrintBackground` | `bool` | `true` | Print background graphics. Both engines. `false` drops paragraph/cell fills and run highlights, incl. the UA-stylesheet ones on `<pre>`, `<mark>` and form controls; **borders and rules are kept** |
| `Scale` | `double` | `1.0` | Rendering scale, clamped to 0.1 – 2.0 (`NaN` → 1.0). Both engines. The paper stays at `PageWidth`/`PageHeight`; the content shrinks, so a value below 1 fits more per page |
| `ViewportWidth` | `double?` | `null` | Managed engine: the width in CSS px `@media` width features are evaluated against. `null` = the page's content width ÷ `Scale`, as a browser's print uses |
| `MediaType` | `HtmlMediaType` | `Print` | The medium the stylesheets are applied for: `@media` blocks, `<link media>`/`<style media>`. `Screen` renders the page as the browser window shows it. Both engines |
| `HeaderTemplate` | `string` | `null` | HTML template for header |
| `FooterTemplate` | `string` | `null` | HTML template for footer |
| `DisplayHeaderFooter` | `bool` | `false` | Show header/footer |
| `NavigationTimeoutMs` | `int` | `30000` | Browser engine: page load timeout in ms |
| `WaitForNetworkIdle` | `bool` | `true` | Browser engine: wait for network idle before the PDF |
| `RenderEngine` | `HtmlRenderEngine` | `Managed` | Which engine: `Managed` (in-process) or `Browser` (the HtmlBrowser plugin) |
| `JavaScriptEnabled` | `bool` | `true` | Browser engine: script in the page. Managed engine: run the page's scripts when the HtmlScript plugin is registered |
| `BlockPrivateNetworkAccess` | `bool` | `true` | Browser engine: refuse loopback, private, link-local and metadata addresses — the browser is launched behind the plugin's own proxy, which connects only to an address it has vetted |
| `ChromiumSandbox` | `bool` | `true` | Browser engine: run Chromium inside its operating-system sandbox, which holds a renderer a page compromised. Turn it off only where Chromium cannot start sandboxed — a Linux host running as root, a container without user namespaces. Firefox and WebKit are not affected |
| `BasePath` | `string` | `null` | Managed engine: the directory local images, stylesheets and fonts may be read from |
| `RemoteResourceLoader` | `IRemoteResourceLoader` | `null` | Managed engine: fetches remote `http(s)` subresources; `GuardedRemoteResourceLoader` for untrusted HTML |
| `MaxRemoteResources` | `int` | 32 | Managed engine: distinct URLs one conversion may fetch |
| `MaxHtmlBytes` | `long` | 16 MiB | Managed engine: the most markup (UTF-8 bytes, the page as given and after its scripts) converted; past it the page is refused before it is parsed, since the engine's peak memory is some 55× the markup. 0 is off. |
| `SvgRasterizer` | `Func<string, byte[]>` | `null` | Managed engine: renders inline SVG and `data:image/svg+xml` to a raster |
| `DefaultTextDirection` | `TextDirection` | `Auto` | Managed engine: the base direction when nothing declares one |
| `FontProvider` | `Func<BuilderFontRequest, byte[]>` | `null` | Managed engine: supplies font programs by family, weight and style |
| `AllowSystemFontSubstitution` | `bool` | `true` | Managed engine: search the host's fonts when the provider declines |
| `UnrenderableText` | `UnrenderableTextPolicy` | `Report` | Managed engine: `Report` draws `?` and warns, `Omit`, or `Throw` |
| `OnDiagnostic` | `Action<BuilderDiagnostic>` | `null` | Managed engine: every approximation, declined resource and font problem |
| `MaxPages` | `int` | `0` | Managed engine: the most pages written, `0` for no limit; a longer page ends at the last whole page within it, reported through `OnDiagnostic` as `PageLimitReached`. Negative is refused. The browser engine paginates itself. |
| `Version` | `PdfVersion?` | `null` | Managed engine: the PDF version the header names — `PdfVersion.Pdf17`, `PdfVersion.Pdf20`; `null` for the lowest the content needs, never below 1.4. A version below what the content uses is raised to it and reported through `OnDiagnostic` as `VersionRaised`; an unpublished version is refused. The browser engine writes its own header. |
| `Tagged` | `bool` | `true` | Managed engine: write tagged PDF |
| `DefaultLanguage` | `string` | `null` | Managed engine: the catalog `/Lang` when `<html lang>` is absent |
| `RunningDate` | `string` | `null` | Managed engine: the text the `date` substitution class prints |

### HtmlToPdfException

Extends `Tetitulz.Pdf.Core.PdfException`, as every public exception in the library does. Thrown on
conversion or browser launch failures.

---

## Usage Examples

```csharp
using Tetitulz.Pdf.HtmlToPdf;

// Basic HTML string to PDF
HtmlToPdfConverter.ConvertHtml("<h1>Hello World</h1>", "output.pdf");

// URL to PDF with Firefox
HtmlToPdfConverter.ConvertUrl("https://example.com", "output.pdf", new HtmlToPdfOptions
{
    Engine = BrowserEngine.Firefox
});

// HTML to PDF with WebKit, A4 landscape
HtmlToPdfConverter.ConvertHtml(html, "report.pdf", new HtmlToPdfOptions
{
    Engine = BrowserEngine.WebKit,
    PageWidth = "210mm",
    PageHeight = "297mm",
    Landscape = true,
    PrintBackground = true
});

// With headers and footers
HtmlToPdfConverter.ConvertHtml(html, "document.pdf", new HtmlToPdfOptions
{
    DisplayHeaderFooter = true,
    HeaderTemplate = "<div style='font-size:10px; text-align:center; width:100%;'>My Report</div>",
    FooterTemplate = "<div style='font-size:10px; text-align:center; width:100%;'>Page <span class='pageNumber'></span> of <span class='totalPages'></span></div>"
});

// Async usage
byte[] pdfBytes = await HtmlToPdfConverter.ConvertHtmlToBytesAsync("<h1>Async</h1>");

// Stream-based
using var output = File.Create("output.pdf");
HtmlToPdfConverter.ConvertUrl("https://example.com", output);

// To byte array
byte[] pdf = HtmlToPdfConverter.ConvertUrlToBytes("https://example.com");
```

---

## Internal Design

### The browser engine

The browser engine lives in `Tetitulz.Pdf.Plugins.HtmlBrowser`; how it drives Playwright — one browser per
conversion, the engine each `BrowserEngine` names, the options it hands to `page.pdf()` — is in that
package's README.

### Sync/Async Bridge

Sync methods call async methods via `.GetAwaiter().GetResult()`. All async methods use `.ConfigureAwait(false)` to avoid deadlocks.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file). See the repository for the full guide, the open-gap register and the third-party notices.
