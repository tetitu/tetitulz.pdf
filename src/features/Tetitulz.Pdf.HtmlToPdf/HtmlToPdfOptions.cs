// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.HtmlToPdf
{
    public sealed class HtmlToPdfOptions
    {
        /// <summary>
        /// Which rendering engine to use. <see cref="HtmlRenderEngine.Managed"/> (the default) is the pure-managed Builder pipeline — no browser, no JavaScript, no network.
        /// <see cref="HtmlRenderEngine.Browser"/> drives Playwright for full CSS/JS fidelity and
        /// requires the Playwright browsers to be installed.
        /// </summary>
        /// <remarks>
        /// <para><b>The default changed, and it is a behaviour change for every caller that did not
        /// set this.</b> Three reasons, in order of weight:</para>
        /// <list type="number">
        ///   <item><b>The managed engine produces <i>tagged</i> PDF and the browser path does not.</b>
        ///     Tagging is what makes the output accessible and remediable; it is not recoverable
        ///     afterwards.</item>
        ///   <item><b>It needs nothing installed.</b> The browser default failed on any machine
        ///     without <c>playwright install</c> — a container, a build agent, a fresh clone — and
        ///     failed at conversion time rather than at startup.</item>
        ///   <item><b>It is deterministic and offline.</b> No Chromium version drift silently
        ///     changing an invoice, and no outbound requests from a document.</item>
        /// </list>
        /// <para><b>What you give up by not setting <see cref="HtmlRenderEngine.Browser"/>:</b>
        /// JavaScript, remote <c>http(s)</c> subresources, <c>grid</c>,
        /// <c>position</c>/<c>z-index</c>, web fonts, and text re-flowing below a float. The
        /// managed engine covers tables, lists, <c>display:flex</c>, <c>float</c>/<c>clear</c>,
        /// RTL with correct bidi, <c>@font-face</c> and inline CSS — the layout a generated
        /// business document uses. Anything it approximates is reported through
        /// <see cref="OnDiagnostic"/>, so a caller can detect the cases that need the browser
        /// rather than guess at them.</para>
        /// <para><b>Worth knowing before reverting the default:</b> the browser path has <i>no</i>
        /// coverage in this repository's test suite, because it needs browsers installed. Every
        /// HtmlToPdf test drives the managed engine. Making the tested path the default is part of
        /// the reason for the change.</para>
        /// </remarks>
        public HtmlRenderEngine RenderEngine { get; set; } = HtmlRenderEngine.Managed;

        /// <summary>
        /// Browser engine to use when <see cref="RenderEngine"/> is
        /// <see cref="HtmlRenderEngine.Browser"/>. Default: Chromium.
        /// </summary>
        public BrowserEngine Engine { get; set; } = BrowserEngine.Chromium;

        /// <summary>
        /// A common paper size, written to <see cref="PageWidth"/> and <see cref="PageHeight"/> — set
        /// <c>PageSize = HtmlPageSize.A4</c> rather than spelling the two out. Read back, it is the size
        /// those two hold, or <c>null</c> for a custom size; the default reads <see cref="HtmlPageSize.Letter"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>The two strings stay the one source of the size.</b> Setting this writes them
        /// (portrait — <see cref="Landscape"/> turns it), and setting either string afterwards makes a
        /// custom size, which this then reads as <c>null</c>; whichever is set last wins. Setting
        /// <c>null</c> changes nothing. A value outside <see cref="HtmlPageSize"/> is refused.</para>
        /// <para>The sizes are written in millimetres and inches, which both engines read: the managed
        /// one through its CSS lengths, the browser one as Chromium's page size.</para>
        /// </remarks>
        public HtmlPageSize? PageSize
        {
            get
            {
                foreach (var (size, width, height) in PageSizes)
                    if (string.Equals(PageWidth, width, StringComparison.Ordinal)
                        && string.Equals(PageHeight, height, StringComparison.Ordinal))
                        return size;
                return null;
            }
            set
            {
                if (!value.HasValue) return;
                foreach (var (size, width, height) in PageSizes)
                {
                    if (size != value.Value) continue;
                    PageWidth = width;
                    PageHeight = height;
                    return;
                }
                throw new ArgumentOutOfRangeException(nameof(value), value.Value, "Not a member of HtmlPageSize.");
            }
        }

        /// <summary>Each <see cref="HtmlPageSize"/>'s portrait width and height, in units both engines read.</summary>
        private static readonly (HtmlPageSize Size, string Width, string Height)[] PageSizes =
        {
            (HtmlPageSize.A3, "297mm", "420mm"),
            (HtmlPageSize.A4, "210mm", "297mm"),
            (HtmlPageSize.A5, "148mm", "210mm"),
            (HtmlPageSize.B4, "250mm", "353mm"),
            (HtmlPageSize.B5, "176mm", "250mm"),
            (HtmlPageSize.Letter, "8.5in", "11in"),
            (HtmlPageSize.Legal, "8.5in", "14in"),
            (HtmlPageSize.Tabloid, "11in", "17in"),
            (HtmlPageSize.Executive, "7.25in", "10.5in"),
        };

        /// <summary>
        /// Page width (e.g., "8.5in", "210mm", "612px") — any size; <see cref="PageSize"/> sets a common
        /// one. Default: "8.5in" (US Letter).
        /// </summary>
        public string PageWidth { get; set; } = "8.5in";

        /// <summary>
        /// Page height (e.g., "11in", "297mm", "792px"). Default: "11in" (US Letter).
        /// </summary>
        public string PageHeight { get; set; } = "11in";

        /// <summary>
        /// Top margin (e.g., "1in", "25mm", "72px"). Default: "0.4in".
        /// </summary>
        public string MarginTop { get; set; } = "0.4in";

        /// <summary>
        /// Bottom margin. Default: "0.4in".
        /// </summary>
        public string MarginBottom { get; set; } = "0.4in";

        /// <summary>
        /// Left margin. Default: "0.4in".
        /// </summary>
        public string MarginLeft { get; set; } = "0.4in";

        /// <summary>
        /// Right margin. Default: "0.4in".
        /// </summary>
        public string MarginRight { get; set; } = "0.4in";

        /// <summary>
        /// Whether to use landscape orientation. Default: false.
        /// </summary>
        public bool Landscape { get; set; }

        /// <summary>
        /// Whether to print background graphics. Default: true. Honoured by <b>both</b> engines.
        ///
        /// <para>Under <see cref="HtmlRenderEngine.Managed"/>, <c>false</c> removes paragraph and
        /// table-cell background fills and run highlights — including the ones the UA stylesheet
        /// supplies for <c>&lt;pre&gt;</c>, <c>&lt;mark&gt;</c> and form controls, which is what
        /// the browser does. <b>Borders and rules are not backgrounds</b> and are kept.</para>
        /// </summary>
        public bool PrintBackground { get; set; } = true;

        /// <summary>
        /// Scale of the webpage rendering (0.1 to 2.0). Default: 1.0. Out-of-range values are
        /// clamped into that band and <c>NaN</c> is treated as 1.0.
        ///
        /// <para>Honoured by <b>both</b> engines. Under
        /// <see cref="HtmlRenderEngine.Managed"/> the paper stays at the nominal
        /// <see cref="PageWidth"/>/<see cref="PageHeight"/> and the content shrinks — so a value
        /// below 1 fits more content per page, exactly as the browser does it.</para>
        /// </summary>
        public double Scale { get; set; } = 1.0;

        /// <summary>
        /// The width, in CSS pixels, that <c>@media</c> width features (<c>min-width</c>,
        /// <c>max-width</c>, <c>width</c> and their range forms) are evaluated against under
        /// <see cref="HtmlRenderEngine.Managed"/>. <c>null</c> (the default) uses the page's content
        /// width — the page less its margins, divided by <see cref="Scale"/> — which is what a
        /// browser's print evaluates them against.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a page-sized default changes a responsive page's layout.</b> US Letter and
        /// A4 in portrait are about 720–740 CSS pixels of content, so a responsive stylesheet sees a
        /// narrow screen: Bootstrap's <c>col-sm-*</c> and <c>col-md-*</c> columns stack and its
        /// <c>hidden-xs</c> elements hide — exactly what printing the page from a browser does. Set
        /// a desktop width here (for example <c>1200</c>) to print the wide layout instead; the
        /// paper size does not change.</para>
        /// <para>The browser engine lays print out at the page width and ignores this.</para>
        /// </remarks>
        public double? ViewportWidth { get; set; }

        /// <summary>
        /// The medium the page's stylesheets are applied for. <see cref="HtmlMediaType.Print"/> (the
        /// default) is what a browser's "Save as PDF" does; <see cref="HtmlMediaType.Screen"/> renders
        /// the page as it looks in the browser window, on the same paper. Honoured by <b>both</b>
        /// engines — the browser one through Chromium's media emulation.
        /// </summary>
        /// <remarks>
        /// A site's print stylesheet can strip what a document needs. Bootstrap 3's removes every
        /// background with <c>background: transparent !important</c>, so a checklist whose ticks are
        /// background images prints with none — from a browser as much as from here. Screen keeps
        /// them.
        /// </remarks>
        public HtmlMediaType MediaType { get; set; } = HtmlMediaType.Print;

        /// <summary>
        /// HTML template for the print header.
        /// Supports Playwright template variables: date, title, url, pageNumber, totalPages.
        /// Null means no header.
        /// </summary>
        public string? HeaderTemplate { get; set; }

        /// <summary>
        /// HTML template for the print footer.
        /// Null means no footer.
        /// </summary>
        public string? FooterTemplate { get; set; }

        /// <summary>
        /// Whether to display header and footer. Default: false.
        /// </summary>
        public bool DisplayHeaderFooter { get; set; }

        /// <summary>
        /// Value <see cref="NavigationTimeoutMs"/> starts at, and the fallback the browser engine
        /// applies when it has been set to a non-positive value.
        /// </summary>
        internal const int DefaultNavigationTimeoutMs = 30_000;

        /// <summary>
        /// Timeout in milliseconds for page load. Default: 30000 (30 seconds).
        /// </summary>
        public int NavigationTimeoutMs { get; set; } = DefaultNavigationTimeoutMs;

        /// <summary>
        /// Whether to wait for 'networkidle' before generating PDF. Default: true.
        /// </summary>
        public bool WaitForNetworkIdle { get; set; } = true;

        /// <summary>
        /// Whether to allow JavaScript execution in the browser context.
        /// Default: <c>true</c> for URL conversions (pages may require JS to render correctly).
        /// When converting untrusted HTML strings, set this to <c>false</c> to prevent
        /// arbitrary script execution within the Playwright browser process.
        /// </summary>
        public bool JavaScriptEnabled { get; set; } = true;

        /// <summary>
        /// SSRF protection for the browser engine. When <c>true</c> (default), the browser is
        /// launched behind a proxy of the plugin's own, which connects only to hosts whose every
        /// address is public — so the top-level navigation, every subresource, redirect, beacon
        /// and WebSocket that would reach a loopback, private, link-local or cloud-metadata
        /// (169.254.169.254) address is refused where the connection is made — and the page's
        /// requests are held to http(s) and to local files inside the base directory.
        /// This prevents untrusted HTML/URLs from reaching internal services or reading local
        /// files. Set to <c>false</c> only when you trust the input and need to render internal
        /// resources (e.g. an intranet dashboard): with it off, neither restriction applies.
        /// </summary>
        public bool BlockPrivateNetworkAccess { get; set; } = true;

        /// <summary>
        /// Whether the browser engine's Chromium runs inside its operating-system sandbox. Default:
        /// <c>true</c>. <see cref="BlockPrivateNetworkAccess"/>'s proxy and the request routes bound
        /// what an intact renderer does; a renderer a page compromises acts with this process's own
        /// file and network access unless the sandbox holds it. Set to <c>false</c> only where
        /// Chromium cannot start sandboxed — a Linux host running as root, or a container without
        /// user namespaces. Firefox and WebKit are not affected.
        /// </summary>
        public bool ChromiumSandbox { get; set; } = true;

        /// <summary>
        /// Base directory the <see cref="HtmlRenderEngine.Managed"/> engine resolves local
        /// subresources against — <c>&lt;img&gt;</c> sources, <c>&lt;link rel="stylesheet"&gt;</c>
        /// hrefs, <c>@font-face</c> and <c>background-image</c> urls. When set, relative and rooted
        /// paths (and <c>file:</c> URIs) are read from disk, but only files resolving *inside* this
        /// directory are loaded (path-traversal outside the base is refused). Remote
        /// <c>http(s)</c> resources are a separate opt-in, <see cref="RemoteResourceLoader"/>;
        /// without one they are declined and reported on <see cref="OnDiagnostic"/> whatever this
        /// is set to.
        ///
        /// <para><b><c>null</c> does not mean "no file I/O" for every entry point.</b> It disables
        /// disk reads for <c>ConvertHtml*</c> only, which then embeds <c>data:</c> URI images and
        /// nothing else. <c>ConvertFile</c> / <c>ConvertFileToBytes</c>(<c>Async</c>) supply the
        /// <b>input file's own directory</b> as an implicit base when this is left null, so a
        /// converted <c>.html</c> file <i>does</i> read images, stylesheets and fonts sitting
        /// beside it. That is deliberate engine parity — the browser path already confines
        /// <c>file://</c> subresources to the same directory — and the rationale is on
        /// <c>HtmlToBuilderMapper._implicitBasePath</c>. The security consequence is the part to
        /// plan for: converting an <b>untrusted</b> uploaded <c>.html</c> from a shared upload
        /// directory lets it embed any other file in that directory into the output PDF. Traversal
        /// <i>above</i> the directory is still refused, and the caller's options instance is never
        /// mutated, so the implicit base cannot leak into a later <c>ConvertHtml</c> call.</para>
        ///
        /// <para>Opt-in: set it only when you trust the HTML and need local subresources. To
        /// convert an untrusted file with no disk access at all, read the file yourself and use
        /// <c>ConvertHtmlToBytes</c>, which has no implicit base.</para>
        /// </summary>
        public string? BasePath { get; set; }

        /// <summary>
        /// Opt-in fetching of remote <c>http(s)</c> subresources for the
        /// <see cref="HtmlRenderEngine.Managed"/> engine — <c>&lt;img&gt;</c> and <c>srcset</c>,
        /// <c>background-image</c>, <c>&lt;link rel="stylesheet"&gt;</c> and <c>@font-face</c>.
        /// <c>null</c> (default) fetches nothing: each is declined and reported on
        /// <see cref="OnDiagnostic"/>.
        ///
        /// <para><b>For HTML you do not control, use <see cref="GuardedRemoteResourceLoader"/></b>,
        /// which refuses private and reserved addresses, re-checks every redirect and bounds each
        /// fetch — and read its remarks on the <c>netstandard2.0</c> leg, where the address check
        /// cannot be tied to the connection.</para>
        ///
        /// <para>A scheme-relative URL (<c>//cdn.example/x.png</c>) is fetched as <c>https</c>.
        /// Relative <c>url()</c>s inside a fetched stylesheet resolve against the stylesheet's own
        /// URL, as in a browser — never against <see cref="BasePath"/>, so a remote stylesheet
        /// cannot name a local file.</para>
        /// </summary>
        public IRemoteResourceLoader? RemoteResourceLoader { get; set; }

        /// <summary>
        /// The most distinct remote URLs one conversion asks <see cref="RemoteResourceLoader"/> for.
        /// Default <see cref="DefaultMaxRemoteResources"/>. Each fetch can take up to the loader's
        /// own timeout, so this bounds how long a document listing thousands of URLs can hold a
        /// conversion; the URLs past it are declined and reported. A repeated URL is fetched once.
        /// </summary>
        public int MaxRemoteResources { get; set; } = DefaultMaxRemoteResources;

        /// <summary>The built-in value of <see cref="MaxRemoteResources"/>.</summary>
        public const int DefaultMaxRemoteResources = 32;

        /// <summary>
        /// The most markup the managed engine converts, in UTF-8 bytes — the page as given, and the
        /// page after its own scripts ran. Past it the document is refused with an
        /// <see cref="HtmlToPdfException"/> before it is parsed. Default <see cref="DefaultMaxHtmlBytes"/>
        /// (16 MiB): the engine's peak memory is some 55 times the markup (AngleSharp's DOM alone 30
        /// times; a 31 MB table of a million cells took 1.7 GB and 26 s inside every other ceiling),
        /// so the default holds a conversion under a gigabyte. A host that converts larger reports
        /// raises it; the browser engine is not bounded by it.
        /// </summary>
        public long MaxHtmlBytes { get; set; } = DefaultMaxHtmlBytes;

        /// <summary>The built-in value of <see cref="MaxHtmlBytes"/>.</summary>
        public const long DefaultMaxHtmlBytes = 16L * 1024 * 1024;

        /// <summary>
        /// Opt-in inline-<c>&lt;svg&gt;</c> rasterizer for the <see cref="HtmlRenderEngine.Managed"/>
        /// engine. Given the SVG element's outer markup, returns PNG (or JPEG) bytes to embed, or
        /// <c>null</c> to skip that SVG. <c>null</c> (default) = <c>&lt;svg&gt;</c> is dropped, and
        /// the managed engine takes no SkiaSharp dependency. To enable SVG, set this to a
        /// rasterizer — e.g. <c>Tetitulz.Pdf.Base.Operations.Images.SvgRasterizer.RasterizeToPng</c>, which
        /// renders the SVG via the pure-managed SVG→PDF converter and SkiaSharp. The delegate must
        /// not throw (exceptions are caught and treated as a skip).
        /// </summary>
        public Func<string, byte[]>? SvgRasterizer { get; set; }

        // ------------------------------------------------- managed engine: text direction

        /// <summary>
        /// Base writing direction for content that declares no <c>dir</c> or CSS
        /// <c>direction</c> anywhere. Default <see cref="Tetitulz.Pdf.Builder.Model.TextDirection.Auto"/>
        /// — the first strong character decides, per UAX&#160;#9 P2/P3.
        ///
        /// <para>A browser defaults to <c>ltr</c> instead. The difference is narrower than it
        /// looks: with line-level bidi in place, a Hebrew paragraph orders its words correctly
        /// under either setting. What <c>Auto</c> additionally buys is the right <i>margin</i> —
        /// Hebrew with no declared direction is right-aligned rather than left-aligned. Set this
        /// to <see cref="Tetitulz.Pdf.Builder.Model.TextDirection.LeftToRight"/> for strict
        /// browser parity.</para>
        ///
        /// <para>Managed engine only; the browser path gets this from the browser.</para>
        /// </summary>
        public Tetitulz.Pdf.Builder.Model.TextDirection DefaultTextDirection { get; set; }
            = Tetitulz.Pdf.Builder.Model.TextDirection.Auto;

        // ------------------------------------------------- managed engine: fonts

        /// <summary>
        /// Supplies a font program for text the standard-14 WinAnsi fonts cannot encode — Hebrew,
        /// Arabic, CJK. Return <c>null</c> to decline and let resolution continue. Accepts sfnt
        /// TrueType (<c>glyf</c>) and OpenType (CFF) bytes.
        ///
        /// <para>This is the deterministic path, and the one a server wants: without it the
        /// engine takes whichever installed font happens to cover the characters, so the same
        /// HTML produces different output on different hosts — and on a host with no covering
        /// font at all, every non-Latin-1 character is written as <c>'?'</c>.</para>
        ///
        /// <para><b>Licensing:</b> whatever this returns is embedded in the output and therefore
        /// redistributed. The caller is responsible for holding redistribution rights.</para>
        ///
        /// <para>Resolution order is <c>@font-face</c> (when <see cref="BasePath"/> permits
        /// reading the file), then this, then the host's fonts.</para>
        /// </summary>
        public Func<Tetitulz.Pdf.Builder.Export.BuilderFontRequest, byte[]?>? FontProvider { get; set; }

        /// <summary>
        /// Fall back to fonts installed on the host when neither <c>@font-face</c> nor
        /// <see cref="FontProvider"/> supplies one. Default <c>true</c> — it is the engine's
        /// long-standing behaviour, so turning it off by default would stop existing callers from
        /// rendering Hebrew or Arabic at all.
        /// </summary>
        public bool AllowSystemFontSubstitution { get; set; } = true;

        /// <summary>
        /// What to do when no font can render a run: report and fall back (the default, which
        /// writes <c>'?'</c>), omit the run, or throw. See
        /// <see cref="Tetitulz.Pdf.Builder.Export.UnrenderableTextPolicy"/>.
        /// </summary>
        public Tetitulz.Pdf.Builder.Export.UnrenderableTextPolicy UnrenderableText { get; set; }
            = Tetitulz.Pdf.Builder.Export.UnrenderableTextPolicy.Report;

        /// <summary>
        /// Receives every failure the managed engine tolerated — a font it could not resolve, a
        /// <c>@font-face</c> format it will not read. Handlers must not throw.
        /// </summary>
        public Action<Tetitulz.Pdf.Builder.Export.BuilderDiagnostic>? OnDiagnostic { get; set; }

        /// <summary>
        /// The most pages the managed engine writes; <b>0, the default, for no limit</b>. A page that would
        /// run longer ends at the last whole page within it, reported through <see cref="OnDiagnostic"/> as
        /// <see cref="Tetitulz.Pdf.Builder.Export.BuilderDiagnosticKind.PageLimitReached"/>
        /// (<see cref="Tetitulz.Pdf.Builder.Export.PdfExportOptions.MaxPages"/>). A host converting markup it
        /// was handed sets the ceiling it can afford: the pages are the markup's to choose. A negative value
        /// is refused. The browser engine paginates itself and does not read it.
        /// </summary>
        public int MaxPages
        {
            get => _maxPages;
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value), value, "MaxPages is 0 for no limit, or a positive page count.");
                _maxPages = value;
            }
        }

        private int _maxPages;

        /// <summary>
        /// The PDF version the managed engine's header names; <b><c>null</c>, the default, for the lowest
        /// version the content needs, never below 1.4</b>. A version below what the content uses is raised
        /// to it and reported through <see cref="OnDiagnostic"/> as
        /// <see cref="Tetitulz.Pdf.Builder.Export.BuilderDiagnosticKind.VersionRaised"/>
        /// (<see cref="Tetitulz.Pdf.Builder.Export.PdfExportOptions.Version"/>); a version no edition of the
        /// standard publishes is refused. The browser engine writes its own header and does not read it.
        /// </summary>
        public Tetitulz.Pdf.Core.Document.PdfVersion? Version
        {
            get => _version;
            set
            {
                if (value.HasValue && !value.Value.IsPublished)
                    throw new ArgumentOutOfRangeException(nameof(value), value.Value.ToString(),
                        "A PDF version is 1.0 to 1.7, or 2.0.");
                _version = value;
            }
        }

        private Tetitulz.Pdf.Core.Document.PdfVersion? _version;

        // ------------------------------------------------- managed engine: tagged output

        /// <summary>
        /// Emit tagged (accessible) PDF from the managed engine: headings keep their level, table
        /// header cells become <c>/TH</c> with a scope, images carry their <c>alt</c>, and page
        /// furniture is marked as artifacts. Default <c>true</c>.
        ///
        /// <para>Default-on here — unlike <c>Builder.Model.Document.Tagged</c> — because the HTML
        /// mapper genuinely knows the semantics: it has the tag names. A converter that did not
        /// would emit a valid tree of undifferentiated paragraphs, which passes structural checks
        /// while telling a screen reader nothing.</para>
        ///
        /// <para><b>This does not make the output PDF/UA-conforming.</b> Standard-14 fonts are
        /// never embedded, so a Latin-only document still fails the font-embedding checks; supply
        /// a font through <see cref="FontProvider"/> to close that. Tagging is a prerequisite for
        /// conformance, not a claim of it — and nothing here writes a conformance identifier.</para>
        /// </summary>
        public bool Tagged { get; set; } = true;

        /// <summary>
        /// Natural language to record when the document declares none — the catalog
        /// <c>/Lang</c>. <c>null</c> (default) writes nothing.
        ///
        /// <para>There is deliberately no built-in default. A wrong <c>/Lang</c> makes a screen
        /// reader pronounce Hebrew with English phonemes while the conformance check reports
        /// clean, which is worse than an absent one — an honest gap beats a confident error.</para>
        /// </summary>
        public string? DefaultLanguage { get; set; }

        /// <summary>
        /// The value substituted for a <c>class="date"</c> span in a header/footer template.
        ///
        /// <para><c>null</c> (default) substitutes nothing. There is no built-in <c>DateTime.Now</c>
        /// here on purpose: it would make the same input produce different bytes on every run,
        /// which breaks reproducible builds and any output-comparison test a caller writes. Pass
        /// the date you want.</para>
        /// </summary>
        public string? RunningDate { get; set; }

        public static HtmlToPdfOptions Default => new HtmlToPdfOptions();
    }
}
