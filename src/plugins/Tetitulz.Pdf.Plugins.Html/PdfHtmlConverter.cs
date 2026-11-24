// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Plugins.Html
{
    /// <summary>Options for PDF → HTML conversion.</summary>
    public sealed class HtmlConversionOptions
    {
        /// <summary>Embed page images as base-64 data URIs (default true).</summary>
        public bool EmbedImages  { get; set; } = true;
        /// <summary>Overlay invisible selectable text on each page image (default true).</summary>
        public bool IncludeText  { get; set; } = true;
        /// <summary>Image resolution in DPI (default 96).</summary>
        public int  ImageDpi     { get; set; } = 96;
        /// <summary>
        /// Optional HTML title. <b>Null means "use the default"</b> — the writer reads it as
        /// <c>Title ?? "PDF Document"</c>, so this must stay nullable rather than being defaulted to
        /// <c>""</c>: an empty string is a *chosen* empty title and suppresses the fallback.
        /// </summary>
        public string? Title      { get; set; }

        /// <summary>
        /// Cumulative budget for base-64 page images, in bytes (default 512 MiB). Defence against
        /// a multi-gigabyte single-file HTML on a large scanned PDF.
        ///
        /// <para>Exceeding it is a <b>degradation</b>, not a failure: the remaining pages carry no
        /// image, and <see cref="PdfHtmlConverter.LastConversionWarning"/> says so. A caller who
        /// needs single-file output for a big document raises it here.</para>
        ///
        /// <para><b>It applies to <see cref="PdfHtmlConverter.ConvertToLayoutHtml(Stream, HtmlConversionOptions)"/>
        /// as well</b>, through the same accounting as the raster conversion.</para>
        /// </summary>
        public long MaxEmbeddedImageBytes { get; set; } = 512L * 1024 * 1024;
    }

    /// <summary>
    /// Converts PDF pages to a self-contained HTML document.
    ///
    /// Strategy: each page is rasterized via <see cref="PdfPageRenderer"/> and
    /// embedded as a PNG data URI.  When <see cref="HtmlConversionOptions.IncludeText"/>
    /// is true, extracted text words are overlaid as invisible positioned spans so
    /// the page is searchable and copyable.
    /// </summary>
    public static class PdfHtmlConverter
    {
        [ThreadStatic] private static string? _lastConversionWarning;

        /// <summary>
        /// How the last conversion on this thread fell short of a complete single-file document,
        /// or <c>null</c> when it did not.
        ///
        /// <para><b>Why this exists.</b> The conversion returns a <see cref="string"/> that always
        /// looks complete. When a page's image is not embedded — because
        /// <see cref="HtmlConversionOptions.EmbedImages"/> is false, or because
        /// <see cref="HtmlConversionOptions.MaxEmbeddedImageBytes"/> was exceeded part-way through
        /// a large document — the markup for those pages carries no image at all. Without this
        /// channel a 300-page export whose last 120 pages lost their images is indistinguishable
        /// from one that embedded every page.</para>
        ///
        /// <para>Per-thread (CLAUDE.md), and reset at the start of <b>every</b> conversion, on every
        /// path, so a value read here always belongs to the call this thread just made.</para>
        /// </summary>
        public static string? LastConversionWarning => _lastConversionWarning;

        [ThreadStatic] private static int _lastRenderSessionsOpened;

        /// <summary>The render sessions the last conversion on this thread opened for its page
        /// images — one however many pages: the evidence that pages are not each re-parsed.</summary>
        internal static int LastRenderSessionsOpened => _lastRenderSessionsOpened;

        // ================================================================
        //  Rasterized page + invisible text overlay
        // ================================================================

        /// <summary>Converts a PDF file to a self-contained HTML string.</summary>
        public static string ConvertToHtml(string inputPath, HtmlConversionOptions? options = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return ConvertToHtml(fs, options);
        }

        /// <summary>Converts a PDF stream to a self-contained HTML string.</summary>
        public static string ConvertToHtml(Stream input, HtmlConversionOptions? options = null)
        {
            var sb = new StringBuilder();
            using (var writer = new StringWriter(sb, CultureInfo.InvariantCulture))
                WriteRasterHtml(input, writer, options, externalImagesWritten: false);
            return sb.ToString();
        }

        /// <summary>
        /// Converts a PDF stream to HTML, written straight to <paramref name="output"/> as UTF-8.
        /// </summary>
        /// <remarks>
        /// <b>One copy of the document in memory, not three.</b> The string-returning overloads build
        /// the markup in a <see cref="StringBuilder"/>, materialise it as a <see cref="string"/>, and
        /// leave the caller to encode that to bytes — so at the default 512 MiB image budget the
        /// builder's chunks, the string and the byte array are all live at once. This path writes as
        /// it goes and encodes each embedded page through one 64 KiB buffer.
        /// </remarks>
        /// <param name="input">Stream holding the PDF.</param>
        /// <param name="output">Destination for the UTF-8 markup. Not closed by this method.</param>
        /// <param name="options">Conversion options, or <see langword="null"/> for the defaults.</param>
        public static void ConvertToHtml(Stream input, Stream output,
            HtmlConversionOptions? options = null)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            using var writer = NewUtf8Writer(output);
            WriteRasterHtml(input, writer, options, externalImagesWritten: false);
        }

        /// <summary>
        /// The one raster implementation. <paramref name="externalImagesWritten"/> says whether the
        /// caller is also writing <c>page_N.png</c> beside the HTML.
        ///
        /// <para>It has to be a parameter rather than a guess. A relative <c>page_N.png</c> is
        /// correct from <see cref="ConvertToHtmlFiles"/>, which writes exactly those files into
        /// exactly that directory — and would be a broken-image icon from the stream and path entry
        /// points, which have no directory to write to.</para>
        /// </summary>
        private static void WriteRasterHtml(Stream input, TextWriter sb,
            HtmlConversionOptions? options, bool externalImagesWritten)
        {
            var conv = BeginConversion(input, options);
            byte[] source = conv.PdfBytes;
            var opts = conv.Options;
            var pageSizes = conv.PageSizes;

            IReadOnlyList<PdfPageTextLayout>? textLayouts = opts.IncludeText
                ? ExtractText(source)
                : null;

            float scale = PdfUnits.DpiScale(opts.ImageDpi);
            string title = opts.Title ?? "PDF Document";

            sb.WriteLine("<!DOCTYPE html>");
            sb.WriteLine("<html lang=\"en\">");
            sb.WriteLine("<head>");
            sb.WriteLine("  <meta charset=\"utf-8\"/>");
            sb.Write("  <title>"); WriteHtmlEncoded(sb, title); sb.WriteLine("</title>");
            sb.WriteLine("  <style>");
            sb.WriteLine("    body { margin:0; background:#888; display:flex; flex-direction:column; align-items:center; gap:16px; padding:16px; }");
            sb.WriteLine("    .page { position:relative; box-shadow:0 2px 8px rgba(0,0,0,.4); }");
            sb.WriteLine("    .page img { display:block; }");
            sb.WriteLine("    .page .text-layer { position:absolute; top:0; left:0; width:100%; height:100%; overflow:hidden; }");
            sb.WriteLine("    .page .text-layer span { position:absolute; white-space:nowrap; color:transparent; user-select:text; cursor:text; }");
            sb.WriteLine("    .page .page-image-missing { display:flex; align-items:center; justify-content:center; width:100%; height:100%; background:#fff; color:#666; font:14px sans-serif; }");
            sb.WriteLine("  </style>");
            sb.WriteLine("</head>");
            sb.WriteLine("<body>");

            using var budget = new ImageBudget(opts);

            for (int i = 0; i < pageSizes.Count; i++)
            {
                var size  = pageSizes[i];
                float pdfH = size.HeightPoints;
                int pixW = size.WidthPixels;
                int pixH = size.HeightPixels;
                // The scale the render actually used, which is not `ImageDpi / 72` for a /UserUnit
                // page or for one the resolution clamp reduced. The text overlay is positioned in
                // the same pixels as the image, so it has to use the image's own scale or the words
                // drift off the glyphs by exactly the factor the container was wrong by.
                float textScale = size.HeightPoints > 0f ? pixH / size.HeightPoints : scale;

                // Render page to PNG — but ONLY when the result can actually be used: otherwise a
                // full rasterize plus PNG encode would be spent on bytes dropped one statement later.
                // Encoding is roughly half the cost of a real page render (CLAUDE.md).
                byte[]? png = budget.TryRenderPage(source, i, opts.ImageDpi);

                sb.Write("  <div class=\"page\" style=\"width:");
                sb.Write(pixW.ToString(CultureInfo.InvariantCulture));
                sb.Write("px;height:");
                sb.Write(pixH.ToString(CultureInfo.InvariantCulture));
                sb.WriteLine("px\">");

                if (png != null)
                {
                    sb.Write("    <img src=\"data:image/png;base64,");
                    WriteBase64(sb, png);
                    sb.Write("\" width=\""); sb.Write(pixW.ToString(CultureInfo.InvariantCulture));
                    sb.Write("\" height=\""); sb.Write(pixH.ToString(CultureInfo.InvariantCulture));
                    sb.Write("\" alt=\"Page "); sb.Write((i + 1).ToString(CultureInfo.InvariantCulture));
                    sb.WriteLine("\"/>");
                }
                else if (externalImagesWritten)
                {
                    // Only the caller that writes those files may name them.
                    sb.Write("    <img src=\"page_");
                    sb.Write((i + 1).ToString(CultureInfo.InvariantCulture));
                    sb.Write(".png\" width=\""); sb.Write(pixW.ToString(CultureInfo.InvariantCulture));
                    sb.Write("\" height=\""); sb.Write(pixH.ToString(CultureInfo.InvariantCulture));
                    sb.Write("\" alt=\"Page "); sb.Write((i + 1).ToString(CultureInfo.InvariantCulture));
                    sb.WriteLine("\"/>");
                }
                else
                {
                    // No image, and nothing on disk to point at. A visible placeholder rather than
                    // an <img> whose src resolves to nothing: a broken-image icon reads as a
                    // corrupt export, while this says what happened.
                    budget.CountPageWithoutImage();
                    sb.Write("    <div class=\"page-image-missing\">Page ");
                    sb.Write((i + 1).ToString(CultureInfo.InvariantCulture));
                    sb.WriteLine(" image not embedded</div>");
                }

                if (opts.IncludeText && textLayouts != null && i < textLayouts.Count)
                {
                    sb.WriteLine("    <div class=\"text-layer\">");
                    var layout = textLayouts[i];
                    foreach (var line in layout.Lines)
                    {
                        foreach (var word in line.Words)
                        {
                            // Convert PDF coords (bottom-left, Y-up) to CSS (top-left, Y-down).
                            float cssX = word.X * textScale;
                            float cssY = (pdfH - word.Y - word.Height) * textScale;
                            float cssW = word.Width  * textScale;
                            float cssH = word.Height * textScale;
                            float fontSize = Math.Max(1f, cssH);
                            // CSS numbers are culture-INVARIANT (CSS Values §4.1: a <number> uses
                            // '.'), so every length here goes through InvariantCulture. An
                            // interpolated string formats with CultureInfo.CurrentCulture, so on
                            // de-DE / fr-FR / ru-RU this emitted `left:12,3px` — which a browser's
                            // CSS parser discards, dropping the absolute position of every span and
                            // collapsing the whole selectable-text layer into the page's top-left
                            // corner. Invisible on an en-US developer machine.
                            sb.Write(string.Format(CultureInfo.InvariantCulture,
                                "      <span style=\"left:{0:F1}px;top:{1:F1}px;width:{2:F1}px;height:{3:F1}px;font-size:{4:F1}px\">",
                                cssX, cssY, cssW, cssH, fontSize));
                            WriteHtmlEncoded(sb, word.Text);
                            sb.WriteLine("</span>");
                        }
                    }
                    sb.WriteLine("    </div>");
                }

                sb.WriteLine("  </div>");
            }

            sb.WriteLine("</body>");
            sb.WriteLine("</html>");

            _lastConversionWarning = budget.BuildWarning(pageSizes.Count);
        }

        /// <summary>
        /// Converts each page to its own PNG file and writes an index HTML to a directory.
        /// </summary>
        /// <remarks>
        /// <b>This is the one export path that can be parallel.</b> It writes each page out and
        /// keeps none, so it goes through <see cref="PdfPageRenderer.RenderAllPagesToImages(Stream, PdfImageFormat, Action{int, byte[]}, int, int, PdfRenderOptions, System.Threading.CancellationToken)"/>
        /// — one document and one font cache per worker, and nothing retained. Measured on a
        /// 36-page document: 530 ms rendering page by page against 327 ms here, with allocations
        /// falling from 205 MiB to 118 MiB.
        /// <para>The embedding path deliberately stays sequential.
        /// <see cref="HtmlConversionOptions.MaxEmbeddedImageBytes"/> is accounted in page order and
        /// stops the render as soon as it trips; a batch render either completes pages the budget
        /// would have skipped, or holds every page's PNG to reorder them — which is the very thing
        /// the budget exists to prevent.</para>
        /// </remarks>
        public static void ConvertToHtmlFiles(string inputPath, string outputDir, HtmlConversionOptions? options = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            if (outputDir == null) throw new ArgumentNullException(nameof(outputDir));
            options ??= new HtmlConversionOptions();

            // Force images to be external files.
            var opts = new HtmlConversionOptions
            {
                EmbedImages = false,
                IncludeText = options.IncludeText,
                ImageDpi    = options.ImageDpi,
                Title       = options.Title,
                MaxEmbeddedImageBytes = options.MaxEmbeddedImageBytes,
            };

            Directory.CreateDirectory(outputDir);

            // Read once, for both the image pass and the markup pass.
            byte[] pdfBytes = File.ReadAllBytes(inputPath);

            using (var ms = new MemoryStream(pdfBytes, writable: false))
                PdfPageRenderer.RenderAllPagesToImages(ms, PdfImageFormat.Png,
                    (index, png) => File.WriteAllBytes(
                        Path.Combine(outputDir, $"page_{index + 1}.png"), png),
                    opts.ImageDpi);

            // externalImagesWritten: this is the one path that writes page_N.png, so it is the one
            // path allowed to reference them.
            using (var ms = new MemoryStream(pdfBytes, writable: false))
            using (var fsHtml = File.Create(Path.Combine(outputDir, "index.html")))
            using (var writer = NewUtf8Writer(fsHtml))
                WriteRasterHtml(ms, writer, opts, externalImagesWritten: true);
        }

        // ================================================================
        //  Layout-preserving HTML conversion (Chilkat-style)
        // ================================================================

        /// <summary>
        /// Converts a PDF to layout-preserving HTML with CSS-positioned text elements.
        /// Unlike the rasterized approach, this produces semantic HTML where text is
        /// actual DOM content (selectable, searchable, reflowable) with absolute
        /// positioning matching the original PDF layout.
        /// Images are optionally embedded as base-64 PNGs.
        /// </summary>
        public static string ConvertToLayoutHtml(string inputPath, HtmlConversionOptions? options = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return ConvertToLayoutHtml(fs, options);
        }

        /// <summary>
        /// Converts a PDF stream to layout-preserving HTML.
        /// </summary>
        public static string ConvertToLayoutHtml(Stream input, HtmlConversionOptions? options = null)
        {
            var sb = new StringBuilder();
            using (var writer = new StringWriter(sb, CultureInfo.InvariantCulture))
                WriteLayoutHtml(input, writer, options);
            return sb.ToString();
        }

        /// <summary>
        /// Converts a PDF stream to layout-preserving HTML, written straight to
        /// <paramref name="output"/> as UTF-8.
        /// </summary>
        /// <param name="input">Stream holding the PDF.</param>
        /// <param name="output">Destination for the UTF-8 markup. Not closed by this method.</param>
        /// <param name="options">Conversion options, or <see langword="null"/> for the defaults.</param>
        public static void ConvertToLayoutHtml(Stream input, Stream output,
            HtmlConversionOptions? options = null)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            using var writer = NewUtf8Writer(output);
            WriteLayoutHtml(input, writer, options);
        }

        private static void WriteLayoutHtml(Stream input, TextWriter sb, HtmlConversionOptions? options)
        {
            var conv = BeginConversion(input, options);
            byte[] source = conv.PdfBytes;
            var opts = conv.Options;
            var pageSizes = conv.PageSizes;
            var layouts = ExtractText(source);

            sb.Write("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<title>");
            WriteHtmlEncoded(sb, opts.Title ?? "PDF Document");
            sb.Write("</title>\n");
            sb.Write("<style>\n");
            sb.Write("body { margin: 0; background: #e0e0e0; font-family: sans-serif; }\n");
            sb.Write(".pdf-page { position: relative; background: white; margin: 20px auto; ");
            sb.Write("box-shadow: 0 2px 8px rgba(0,0,0,0.3); overflow: hidden; }\n");
            sb.Write(".pdf-line { position: absolute; white-space: nowrap; }\n");
            sb.Write(".pdf-word { display: inline; }\n");
            sb.Write(".pdf-img { position: absolute; }\n");
            sb.Write("</style>\n</head>\n<body>\n");

            using var budget = new ImageBudget(opts);

            for (int pi = 0; pi < pageSizes.Count; pi++)
            {
                // The renderer's geometry, for the reasons on BeginConversion: an inverted page box
                // (§7.9.5) and /UserUnit are read as the renderer reads them, around the image it sizes.
                var size = pageSizes[pi];
                float pageH = size.HeightPoints;
                int cssW = size.WidthPixels;
                int cssH = size.HeightPixels;
                float scale = pageH > 0f ? cssH / pageH : PdfUnits.DpiScale(opts.ImageDpi);

                sb.Write(string.Format(CultureInfo.InvariantCulture,
                    "<div class=\"pdf-page\" style=\"width:{0}px;height:{1}px\">\n", cssW, cssH));

                // Optional: render page as background image for graphics/non-text content.
                // Budgeted through the SAME accountant as the raster path, so MaxEmbeddedImageBytes
                // holds here too: the budget is an object both paths hold rather than a local
                // variable one of them keeps.
                byte[]? png = budget.TryRenderPage(source, pi, opts.ImageDpi);
                if (png != null)
                {
                    sb.Write(string.Format(CultureInfo.InvariantCulture,
                        "<img class=\"pdf-img\" style=\"width:{0}px;height:{1}px\" ", cssW, cssH));
                    sb.Write("src=\"data:image/png;base64,");
                    WriteBase64(sb, png);
                    sb.Write("\">\n");
                }
                else if (budget.Tripped)
                {
                    // Only a budget trip is a degradation here. EmbedImages=false is the layout
                    // conversion working as intended — positioned semantic text with no raster
                    // underlay — so warning about it would fire on the ordinary way to use it.
                    budget.CountPageWithoutImage();
                }

                // Overlay actual text lines with CSS positioning
                if (pi < layouts.Count)
                {
                    var layout = layouts[pi];
                    foreach (var line in layout.Lines)
                    {
                        // PDF coordinates: Y-up from bottom. CSS: Y-down from top.
                        float cssX = line.X * scale;
                        float cssY = (pageH - line.Y - line.Height) * scale;
                        float fontSize = line.Height * scale * 0.85f; // approximate

                        sb.Write(string.Format(CultureInfo.InvariantCulture,
                            "<div class=\"pdf-line\" style=\"left:{0:F1}px;top:{1:F1}px;font-size:{2:F1}px",
                            cssX, cssY, fontSize));

                        // Detect RTL lines
                        bool isRtl = false;
                        foreach (var word in line.Words)
                        {
                            if (word.IsRtl) { isRtl = true; break; }
                        }
                        if (isRtl) sb.Write(";direction:rtl;unicode-bidi:bidi-override");
                        sb.Write("\">");

                        for (int wi = 0; wi < line.Words.Count; wi++)
                        {
                            if (wi > 0) sb.Write(' ');
                            sb.Write("<span class=\"pdf-word\">");
                            WriteHtmlEncoded(sb, line.Words[wi].Text);
                            sb.Write("</span>");
                        }
                        sb.Write("</div>\n");
                    }
                }

                sb.Write("</div>\n");
            }

            sb.Write("</body>\n</html>");

            _lastConversionWarning = budget.BuildWarning(pageSizes.Count);
        }

        // ================================================================
        //  Shared conversion machinery
        // ================================================================

        /// <summary>
        /// The prologue both conversions share: clear the per-thread warning slot, default the
        /// options, buffer the input, and resolve every page's geometry from <b>one</b> document
        /// load.
        /// </summary>
        /// <remarks>
        /// <para><b>Page geometry comes from the renderer that is about to draw these pages</b>, not
        /// from a second reading of the page box. A conversion deriving its own extent from
        /// <c>CropBox ?? MediaBox</c> would disagree with the PNG it wraps: §7.9.5 lets a rectangle
        /// be written from either corner pair, so an inverted box subtracts to a NEGATIVE width, and
        /// <c>/UserUnit</c> scales the page. The renderer knows <c>/CropBox</c> selection,
        /// <c>/UserUnit</c>, <c>/Rotate</c> and the resolution clamp.</para>
        /// <para><b>One load, not one per page.</b> <c>PdfPageRenderer.GetRenderSize</c> over a fresh
        /// stream is a whole document parse per call; on a 36-page document at 96 dpi a loop of them
        /// measured 132 ms and 116 MiB — 13% of the export's wall clock and 26% of its allocations.</para>
        /// </remarks>
        private static Conversion BeginConversion(Stream input, HtmlConversionOptions? options)
        {
            // Cleared here rather than in either conversion, so neither path can be the one that
            // forgets — which is exactly how the layout path came to report the raster path's
            // warning as its own.
            _lastConversionWarning = null;
            _lastRenderSessionsOpened = 0;

            if (input == null) throw new ArgumentNullException(nameof(input));
            var resolved = options ?? new HtmlConversionOptions();

            // Buffer so we can seek multiple times (geometry + render + text extract).
            byte[] pdfBytes = StreamBytes.ReadAll(input);

            // Refused as the export plugin refuses it. The text extractor does not decrypt, and
            // the renderer only does when a decryptor is registered, so this wrote either pages
            // with no text layer or no pages at all — an HTML file that looks finished either way.
            using (var ms = new MemoryStream(pdfBytes, writable: false))
            using (var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms))
                Tetitulz.Pdf.Base.Operations.EncryptedRead.RefuseConversion(doc, "HTML");

            IReadOnlyList<PageRenderSize> pageSizes;
            using (var ms = new MemoryStream(pdfBytes, writable: false))
                pageSizes = PdfPageRenderer.GetRenderSizes(ms, resolved.ImageDpi);

            return new Conversion(pdfBytes, resolved, pageSizes);
        }

        /// <summary>
        /// What both conversions need before they can emit anything: the buffered file, the
        /// resolved options and every page's geometry.
        /// </summary>
        /// <remarks>
        /// A type rather than a <c>ref</c>/<c>out</c> pair so the non-nullness of
        /// <see cref="Options"/> survives into the caller — nullable analysis cannot carry it
        /// through a <c>ref HtmlConversionOptions?</c>.
        /// </remarks>
        private readonly struct Conversion
        {
            internal readonly byte[] PdfBytes;
            internal readonly HtmlConversionOptions Options;
            internal readonly IReadOnlyList<PageRenderSize> PageSizes;

            internal Conversion(byte[] pdfBytes, HtmlConversionOptions options,
                                IReadOnlyList<PageRenderSize> pageSizes)
            {
                PdfBytes  = pdfBytes;
                Options   = options;
                PageSizes = pageSizes;
            }
        }

        private static IReadOnlyList<PdfPageTextLayout> ExtractText(byte[] pdfBytes)
        {
            using var ms = new MemoryStream(pdfBytes, writable: false);
            return PdfStructuredTextExtractor.Extract(ms);
        }

        /// <summary>
        /// Accounts the cumulative base-64 image budget and renders the pages that fit inside it.
        /// </summary>
        /// <remarks>
        /// <b>An object rather than four locals</b>, so both conversions hold the one accounting and
        /// report through it; a third conversion cannot be added without it.
        /// </remarks>
        private sealed class ImageBudget : IDisposable
        {
            private readonly HtmlConversionOptions _options;
            private long _cumulativeDataUriBytes;
            private bool _exhausted;
            private int _degradedFromPage = -1;
            private int _pagesWithoutImage;

            // One render session for the conversion's pages, opened at the first render: rendering
            // each page from a fresh stream would mean a document parse, page-tree walk and font
            // cache per page.
            private MemoryStream? _sessionInput;
            private PdfRenderSession? _session;

            internal ImageBudget(HtmlConversionOptions options) => _options = options;

            public void Dispose()
            {
                _session?.Dispose();
                _sessionInput?.Dispose();
            }

            /// <summary>True once the budget has been exceeded and no further page will be embedded.</summary>
            internal bool Tripped => _exhausted;

            /// <summary>
            /// Renders page <paramref name="pageIndex"/> when embedding is on and the budget still
            /// allows it, or returns <see langword="null"/>.
            /// </summary>
            /// <remarks>
            /// The render is skipped, not performed and discarded, once the budget has tripped:
            /// bounding the output is worth little if the work that produces it still happens.
            /// Counting the page is the caller's job — the two conversions differ on whether a page
            /// with no image is a degradation at all, so this must not decide it for them.
            /// </remarks>
            internal byte[]? TryRenderPage(byte[] pdfBytes, int pageIndex, int dpi)
            {
                if (!_options.EmbedImages || _exhausted) return null;

                if (_session == null)
                {
                    _sessionInput = new MemoryStream(pdfBytes, writable: false);
                    _session = PdfRenderSession.Open(_sessionInput);
                    _lastRenderSessionsOpened++;
                }
                byte[] png = _session.RenderPage(pageIndex, dpi);

                long b64Len = ((long)png.Length + 2) / 3 * 4;
                if (_cumulativeDataUriBytes + b64Len > _options.MaxEmbeddedImageBytes)
                {
                    _exhausted = true;
                    _degradedFromPage = pageIndex + 1;
                    return null;
                }

                _cumulativeDataUriBytes += b64Len;
                return png;
            }

            /// <summary>Records that one page carries no image.</summary>
            internal void CountPageWithoutImage() => _pagesWithoutImage++;

            /// <summary>The warning for this conversion, or <c>null</c> when nothing degraded.</summary>
            internal string? BuildWarning(int pageCount)
            {
                if (_pagesWithoutImage <= 0) return null;

                static string Inv(long v) => v.ToString(CultureInfo.InvariantCulture);

                if (_degradedFromPage > 0)
                    return $"The cumulative embedded-image budget of {Inv(_options.MaxEmbeddedImageBytes)} " +
                           $"bytes was exceeded at page {Inv(_degradedFromPage)}; " +
                           $"{Inv(_pagesWithoutImage)} of {Inv(pageCount)} pages carry no image. " +
                           "Raise HtmlConversionOptions.MaxEmbeddedImageBytes, or use " +
                           "ConvertToHtmlFiles(inputPath, outputDir) to write the images as files.";

                return $"EmbedImages is false and this entry point writes no image files, so all " +
                       $"{Inv(_pagesWithoutImage)} pages carry no image. " +
                       "Use ConvertToHtmlFiles(inputPath, outputDir) to get page_N.png files beside the HTML.";
            }
        }

        // ================================================================
        //  Output primitives
        // ================================================================

        /// <summary>A UTF-8 writer that leaves <paramref name="output"/> open and writes no BOM.</summary>
        private static TextWriter NewUtf8Writer(Stream output) =>
            new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                             bufferSize: 16 * 1024, leaveOpen: true);

        /// <summary>Base-64 input consumed per encode step: a multiple of 3, so no step pads.</summary>
        private const int Base64ChunkBytes = 3 * 8192;

        /// <summary>
        /// Writes <paramref name="data"/> as base-64 without materialising the encoded string.
        /// </summary>
        /// <remarks>
        /// <c>Convert.ToBase64String</c> allocates 4/3 of the input — for a rendered page that is
        /// several hundred KB, over the large-object threshold, once per page, and immediately
        /// dropped after being copied into the output. The chunk size keeps the reusable buffer at
        /// 64 KiB (below that threshold) and is a multiple of 3 so no chunk emits padding in the
        /// middle of the stream.
        /// </remarks>
        private static void WriteBase64(TextWriter writer, byte[] data)
        {
            if (data.Length <= Base64ChunkBytes)
            {
                writer.Write(Convert.ToBase64String(data));
                return;
            }

            char[] buffer = new char[(Base64ChunkBytes + 2) / 3 * 4];
            for (int offset = 0; offset < data.Length; offset += Base64ChunkBytes)
            {
                int take = Math.Min(Base64ChunkBytes, data.Length - offset);

                // ToBase64CharArray rather than TryToBase64Chars: the latter is .NET Core 2.1+ and
                // this assembly also builds for netstandard2.0. Same optimisation — the encode goes
                // straight into the reused buffer, with no 4/3-sized string per chunk.
                //
                // It throws where the Try form returned false, so the size is checked first, keeping
                // the original's defensive fallback. Unreachable: the buffer is sized for a full
                // chunk. Falling back rather than throwing keeps a conversion that would otherwise
                // succeed from failing on an arithmetic slip.
                if ((take + 2) / 3 * 4 > buffer.Length)
                {
                    writer.Write(Convert.ToBase64String(data, offset, take));
                    continue;
                }

                int written = Convert.ToBase64CharArray(data, offset, take, buffer, 0);
                writer.Write(buffer, 0, written);
            }
        }

        /// <summary>
        /// Writes <paramref name="s"/> with the five markup-significant characters escaped.
        /// </summary>
        /// <remarks>
        /// Single-pass, and it writes the input verbatim when nothing needs escaping — which is the
        /// overwhelmingly common case for an extracted word. The chained
        /// <c>Replace("&amp;").Replace("&lt;")…</c> this replaces allocated up to four intermediate
        /// strings for every word of every page.
        /// </remarks>
        private static void WriteHtmlEncoded(TextWriter writer, string? s)
        {
            if (string.IsNullOrEmpty(s)) return;

            int start = 0;
            for (int i = 0; i < s!.Length; i++)
            {
                string? entity = s[i] switch
                {
                    '&' => "&amp;",
                    '<' => "&lt;",
                    '>' => "&gt;",
                    '"' => "&quot;",
                    '\'' => "&#39;",
                    _ => null,
                };
                if (entity == null) continue;

                if (i > start) writer.Write(s.Substring(start, i - start));
                writer.Write(entity);
                start = i + 1;
            }

            if (start == 0) writer.Write(s);
            else if (start < s.Length) writer.Write(s.Substring(start));
        }
    }
}
