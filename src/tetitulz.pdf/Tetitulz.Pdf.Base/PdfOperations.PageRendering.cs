// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Layout;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Base.Signing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base
{
    public static partial class PdfOperations
    {
        /// <summary>
        /// Default rasterization resolution, in dots per inch, for every page-rendering
        /// entry point on this facade (PNG, JPEG and tile). Roughly 2× the PDF user-space
        /// resolution of <see cref="Core.Constants.PdfUnits.PointsPerInch"/>, which keeps
        /// text legible without producing very large bitmaps.
        /// </summary>
        public const int DefaultRenderDpi = 150;

        /// <summary>
        /// Default rasterization resolution, in dots per inch, used by
        /// <see cref="RecognizeTextWithOcr(Stream, int, int)"/>. Higher than
        /// <see cref="DefaultRenderDpi"/> because OCR accuracy is resolution-bound.
        /// </summary>
        public const int DefaultOcrDpi = 300;

        /// <summary>
        /// Renders a single page of a PDF file to a PNG image using the built-in
        /// pure C# renderer (no native PDF engine required).
        /// </summary>
        /// <param name="inputPath">Path to the source PDF file.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="dpi">Output resolution (dots per inch). Defaults to <see cref="DefaultRenderDpi"/>.</param>
        /// <param name="renderOptions">
        /// Render options, or <see langword="null"/> for <see cref="Rendering.PdfRenderOptions.Default"/>.
        /// </param>
        /// <returns>PNG-encoded bytes of the rendered page.</returns>
        public static byte[] RenderPageToPng(string inputPath, int pageIndex, int dpi = DefaultRenderDpi,
            Rendering.PdfRenderOptions renderOptions = null)
            => NotifyPageRendered(
                Rendering.PdfPageRenderer.RenderPage(inputPath, pageIndex, dpi,
                    renderOptions ?? Rendering.PdfRenderOptions.Default),
                pageIndex, jpeg: false);

        /// <summary>
        /// Renders a single page to raw pixels, skipping image encoding entirely.
        /// </summary>
        /// <remarks>
        /// <para>Prefer this over <see cref="RenderPageToPng(Stream,int,int,Rendering.PdfRenderOptions)"/>
        /// whenever the result is going to be drawn or processed rather than stored or
        /// transmitted. PNG encoding measured at roughly <b>97%</b> of a single-page render —
        /// ~79 ms of an ~82 ms blank page at 150 dpi — so a caller that would only decode the
        /// PNG back into a bitmap saves nearly all of it.</para>
        /// <para>Pixels are RGBA8888 with premultiplied alpha; see
        /// <see cref="Rendering.RenderedPage"/> for the layout and the swizzle that
        /// <c>System.Drawing</c> and WPF both need.</para>
        /// </remarks>
        public static Rendering.RenderedPage RenderPageToPixels(Stream input, int pageIndex,
            int dpi = DefaultRenderDpi, Rendering.PdfRenderOptions renderOptions = null)
            => Rendering.PdfPageRenderer.RenderPageToPixels(input, pageIndex, dpi,
                renderOptions ?? Rendering.PdfRenderOptions.Default);

        /// <summary>Renders a single page of a PDF file to raw pixels, skipping image encoding.</summary>
        public static Rendering.RenderedPage RenderPageToPixels(string inputPath, int pageIndex,
            int dpi = DefaultRenderDpi, Rendering.PdfRenderOptions renderOptions = null)
            => Rendering.PdfPageRenderer.RenderPageToPixels(inputPath, pageIndex, dpi,
                renderOptions ?? Rendering.PdfRenderOptions.Default);

        /// <summary>
        /// Renders a single page as colour separations: a plate of ink amounts for Cyan, Magenta,
        /// Yellow and Black, then one for every spot ink the page paints with. See
        /// <see cref="Rendering.PdfSeparationRenderer"/> for what is exact and what is reported as
        /// approximated.
        /// </summary>
        public static Rendering.PdfSeparations RenderPageSeparations(Stream input, int pageIndex,
            int dpi = DefaultRenderDpi, Rendering.PdfRenderOptions renderOptions = null)
            => Rendering.PdfSeparationRenderer.RenderPage(input, pageIndex, dpi, renderOptions);

        /// <summary>
        /// Renders a single page as colour separations under <paramref name="separationOptions"/> —
        /// through a CMYK output profile, with black-point compensation, when it names one.
        /// </summary>
        public static Rendering.PdfSeparations RenderPageSeparations(Stream input, int pageIndex, int dpi,
            Rendering.PdfRenderOptions renderOptions, Rendering.PdfSeparationOptions separationOptions)
            => Rendering.PdfSeparationRenderer.RenderPage(input, pageIndex, dpi, renderOptions, separationOptions);

        /// <summary>Renders a single page of a PDF file as colour separations.</summary>
        public static Rendering.PdfSeparations RenderPageSeparations(string inputPath, int pageIndex,
            int dpi = DefaultRenderDpi, Rendering.PdfRenderOptions renderOptions = null)
            => Rendering.PdfSeparationRenderer.RenderPage(inputPath, pageIndex, dpi, renderOptions);

        /// <summary>
        /// Renders a single page halftoned to one bit for a grey device — ink or paper a pixel, screened through
        /// the page's own halftone (<c>/HT</c>) or the device default. See <see cref="Rendering.PdfHalftoning"/>.
        /// </summary>
        public static Rendering.PdfBilevelImage RenderPageHalftoned(Stream input, int pageIndex,
            int dpi = DefaultRenderDpi, Rendering.PdfHalftoneOptions halftoneOptions = null,
            Rendering.PdfRenderOptions renderOptions = null)
            => Rendering.PdfHalftoning.RenderPage(input, pageIndex, dpi, halftoneOptions, renderOptions);

        /// <summary>
        /// Renders a single page's colour separations each halftoned to one bit, as a plate-setter receives them.
        /// See <see cref="Rendering.PdfHalftoning"/>.
        /// </summary>
        public static Rendering.PdfHalftonedSeparations RenderPageSeparationsHalftoned(Stream input, int pageIndex,
            int dpi = DefaultRenderDpi, Rendering.PdfHalftoneOptions halftoneOptions = null,
            Rendering.PdfRenderOptions renderOptions = null, Rendering.PdfSeparationOptions separationOptions = null)
            => Rendering.PdfHalftoning.RenderSeparations(input, pageIndex, dpi, halftoneOptions, renderOptions, separationOptions);

        /// <summary>
        /// Renders a single page to <paramref name="format"/> — PNG, JPEG or WebP.
        /// </summary>
        /// <remarks>
        /// <see cref="Rendering.PdfImageFormat.WebpLossless"/> measured roughly <b>9x smaller than
        /// PNG</b> on a rendered page, losslessly. Read <see cref="Rendering.PdfImageFormat"/>
        /// before assuming JPEG is the small one — for text pages it is larger than both.
        /// </remarks>
        public static byte[] RenderPageToImage(Stream input, int pageIndex,
            Rendering.PdfImageFormat format, int dpi = DefaultRenderDpi,
            int quality = Rendering.PdfPageRenderer.DefaultJpegQuality,
            Rendering.PdfRenderOptions renderOptions = null,
            System.Threading.CancellationToken cancellationToken = default)
            => NotifyPageRendered(
                Rendering.PdfPageRenderer.RenderPageToImage(input, pageIndex, format, dpi, quality,
                    renderOptions, cancellationToken),
                pageIndex, format);

        /// <summary>Renders a single page of a PDF file to <paramref name="format"/>.</summary>
        public static byte[] RenderPageToImage(string inputPath, int pageIndex,
            Rendering.PdfImageFormat format, int dpi = DefaultRenderDpi,
            int quality = Rendering.PdfPageRenderer.DefaultJpegQuality,
            Rendering.PdfRenderOptions renderOptions = null,
            System.Threading.CancellationToken cancellationToken = default)
            => NotifyPageRendered(
                Rendering.PdfPageRenderer.RenderPageToImage(inputPath, pageIndex, format, dpi, quality,
                    renderOptions, cancellationToken),
                pageIndex, format);

        /// <summary>Renders every page to <paramref name="format"/>.</summary>
        public static byte[][] RenderAllPagesToImages(Stream input,
            Rendering.PdfImageFormat format, int dpi = DefaultRenderDpi,
            int quality = Rendering.PdfPageRenderer.DefaultJpegQuality,
            Rendering.PdfRenderOptions renderOptions = null,
            System.Threading.CancellationToken cancellationToken = default)
            => NotifyPagesRendered(
                Rendering.PdfPageRenderer.RenderAllPagesToImages(input, format, dpi, quality,
                    renderOptions, cancellationToken),
                format);

        /// <summary>
        /// Renders every page to <paramref name="format"/> and hands each one to
        /// <paramref name="onPage"/> as it finishes, holding none of them.
        /// </summary>
        /// <returns>The number of pages rendered.</returns>
        public static int RenderAllPagesToImages(Stream input, Rendering.PdfImageFormat format,
            Action<int, byte[]> onPage, int dpi = DefaultRenderDpi,
            int quality = Rendering.PdfPageRenderer.DefaultJpegQuality,
            Rendering.PdfRenderOptions renderOptions = null,
            System.Threading.CancellationToken cancellationToken = default)
            => Rendering.PdfPageRenderer.RenderAllPagesToImages(input, format,
                (i, bytes) =>
                {
                    NotifyPageRendered(bytes, i, format);
                    onPage?.Invoke(i, bytes);
                },
                dpi, quality, renderOptions, cancellationToken);

        /// <summary>Renders a single page straight into <paramref name="output"/>.</summary>
        /// <remarks>
        /// When at least one plugin is registered the image is buffered so the page-rendered hook
        /// can carry its pixel extent; with none registered the renderer writes straight through,
        /// exactly as before.
        /// </remarks>
        public static void RenderPageToStream(Stream input, int pageIndex, Stream output,
            Rendering.PdfImageFormat format = Rendering.PdfImageFormat.Png,
            int dpi = DefaultRenderDpi,
            int quality = Rendering.PdfPageRenderer.DefaultJpegQuality,
            Rendering.PdfRenderOptions renderOptions = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (PdfPluginHost.Instance.GetPlugins().Count == 0)
            {
                Rendering.PdfPageRenderer.RenderPageToStream(input, pageIndex, output, format, dpi,
                    quality, renderOptions, cancellationToken);
                return;
            }

            using var buffer = new MemoryStream();
            Rendering.PdfPageRenderer.RenderPageToStream(input, pageIndex, buffer, format, dpi,
                quality, renderOptions, cancellationToken);
            byte[] bytes = buffer.ToArray();
            NotifyPageRendered(bytes, pageIndex, format);
            output.Write(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// Renders a page scaled to fit a pixel box, preserving aspect ratio — the thumbnail and
        /// preview shape. Pass 0 for an unconstrained dimension.
        /// </summary>
        public static byte[] RenderPageToFit(Stream input, int pageIndex,
            int maxWidthPixels, int maxHeightPixels,
            Rendering.PdfImageFormat format = Rendering.PdfImageFormat.Png,
            int quality = Rendering.PdfPageRenderer.DefaultJpegQuality,
            Rendering.PdfRenderOptions renderOptions = null,
            System.Threading.CancellationToken cancellationToken = default)
            => NotifyPageRendered(
                Rendering.PdfPageRenderer.RenderPageToFit(input, pageIndex, maxWidthPixels,
                    maxHeightPixels, format, quality, renderOptions, cancellationToken),
                pageIndex, format);

        /// <summary>
        /// The pixel size a page will render at, without rendering it — for sizing a control,
        /// allocating a buffer or laying out a contact sheet.
        /// </summary>
        public static Rendering.PageRenderSize GetRenderSize(Stream input, int pageIndex,
            int dpi = DefaultRenderDpi, Rendering.PdfRenderOptions renderOptions = null)
            => Rendering.PdfPageRenderer.GetRenderSize(input, pageIndex, dpi, renderOptions);

        /// <summary>The pixel size a page of a PDF file will render at.</summary>
        public static Rendering.PageRenderSize GetRenderSize(string inputPath, int pageIndex,
            int dpi = DefaultRenderDpi, Rendering.PdfRenderOptions renderOptions = null)
            => Rendering.PdfPageRenderer.GetRenderSize(inputPath, pageIndex, dpi, renderOptions);

        /// <summary>
        /// Renders <paramref name="pageIndex"/> and returns an
        /// <see cref="Rendering.InkAccumulator"/> containing per-pixel ink-coverage
        /// maps for every Separation and DeviceN (spot) colorant found on the page.
        /// Useful for print-preview and ink-cost estimation.
        /// </summary>
        public static Rendering.InkAccumulator ExtractInkCoverage(
            Stream input, int pageIndex, float dpi = Core.Constants.PdfUnits.PointsPerInch)
            => Rendering.PdfPageRenderer.ExtractInkCoverage(input, pageIndex, dpi);

        /// <summary>
        /// As <see cref="ExtractInkCoverage(Stream, int, float)"/>, with render options — a
        /// password for an encrypted document, or an explicit page-box selection.
        /// </summary>
        /// <param name="renderOptions">
        /// <see cref="Rendering.PdfRenderOptions.Password"/> applies — an encrypted document used to
        /// report <b>no inks at all</b> here, because its content streams were interpreted while
        /// still encrypted — and so does the page-box selection, since the geometry now comes from
        /// the same <c>MeasurePage</c> the renderer uses. See <c>PdfPageRenderer.ExtractInkCoverage</c>.
        /// </param>
        /// <remarks>
        /// A separate overload rather than a fourth optional parameter on the one above, which is
        /// how this shipped and was a binary break. See PdfSvgRenderer's block for the rule.
        /// </remarks>
        /// <param name="input">Stream holding the PDF.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="dpi">
        /// Resolution of the coverage map in dots per inch, which sets the accumulator's cell size.
        /// </param>
        public static Rendering.InkAccumulator ExtractInkCoverage(
            Stream input, int pageIndex, Rendering.PdfRenderOptions renderOptions,
            float dpi = Core.Constants.PdfUnits.PointsPerInch)
            => Rendering.PdfPageRenderer.ExtractInkCoverage(input, pageIndex, renderOptions, dpi);

        /// <summary>
        /// Renders a single page from a PDF stream to a PNG image.
        /// </summary>
        public static byte[] RenderPageToPng(Stream input, int pageIndex, int dpi = DefaultRenderDpi,
            Rendering.PdfRenderOptions renderOptions = null)
        {
            byte[] png = Rendering.PdfPageRenderer.RenderPage(input, pageIndex, dpi,
                renderOptions ?? Rendering.PdfRenderOptions.Default);
            return NotifyPageRendered(png, pageIndex, jpeg: false);
        }

        /// <summary>
        /// Fires <c>IPdfPlugin</c>'s page-rendered hook for one rendered image, and returns that
        /// image so call sites stay one-liners.
        /// </summary>
        /// <remarks>
        /// <para><b>The four copies of this block only ever ran on the <c>Stream</c> overloads.</b>
        /// Every <c>string</c> overload delegated straight to the renderer, so a plugin observing
        /// renders saw the stream entry points and was blind to the file ones — with nothing in the
        /// signatures, the docs or the plugin contract to suggest the two differed.</para>
        /// <para><b>The same defect then recurred, one layer out.</b> Six further entry points —
        /// <c>RenderPageToImage</c> (×2), <c>RenderAllPagesToImages</c> (×2),
        /// <c>RenderPageToStream</c> and <c>RenderPageToFit</c> — were added notifying nothing, and
        /// <c>RenderPageToImage(…, PdfImageFormat.Png, …)</c> reaches the <i>identical</i> renderer
        /// call as <c>RenderPageToPng</c> and produces byte-identical output. A host metering pages
        /// through <c>IRenderHook</c> lost every one of them by switching between two overloads
        /// that differ in nothing a caller can see. <b>Every facade entry point that returns or
        /// writes an encoded page image routes through here</b>; do not add one that does not.
        /// <c>RenderPageToPixels</c> deliberately does not — it returns no encoded image, and there
        /// is nothing to read a dimension out of that the caller does not already hold.</para>
        /// <para>A null image (no such page) notifies nothing; the dimension readers are format
        /// specific because a PNG's IHDR, a JPEG's SOF and a WebP's RIFF chunk carry the size in
        /// three different places.</para>
        /// </remarks>
        private static byte[] NotifyPageRendered(byte[] image, int pageIndex, bool jpeg) =>
            NotifyPageRendered(image, pageIndex,
                jpeg ? Rendering.PdfImageFormat.Jpeg : Rendering.PdfImageFormat.Png);

        /// <summary><see cref="NotifyPageRendered(byte[],int,bool)"/>, keyed by the output format.</summary>
        private static byte[] NotifyPageRendered(byte[] image, int pageIndex,
            Rendering.PdfImageFormat format)
        {
            var host = PdfPluginHost.Instance;
            if (image == null || host.GetPlugins().Count == 0) return image;

            // One reader for every format, identified from the BYTES rather than from the caller's
            // `format`: a switch on `format` would send a value not listed into the PNG arm, which
            // reports whatever sits at offsets 16 and 20, the moment a member is added to
            // PdfImageFormat.
            Core.Imaging.RasterImageInspector.TryInspect(image, out var info);
            host.InvokePageRendered(pageIndex, info.Width, info.Height);
            return image;
        }

        /// <summary><see cref="NotifyPageRendered(byte[],int,bool)"/> for a whole batch, indexed by page.</summary>
        private static byte[][] NotifyPagesRendered(byte[][] pages, bool jpeg) =>
            NotifyPagesRendered(pages,
                jpeg ? Rendering.PdfImageFormat.Jpeg : Rendering.PdfImageFormat.Png);

        private static byte[][] NotifyPagesRendered(byte[][] pages, Rendering.PdfImageFormat format)
        {
            if (pages == null) return pages;
            for (int i = 0; i < pages.Length; i++) NotifyPageRendered(pages[i], i, format);
            return pages;
        }

        /// <summary>
        /// Renders every page of a PDF file to PNG images.
        /// </summary>
        /// <returns>Array where each element is the PNG bytes of one page.</returns>
        public static byte[][] RenderAllPagesToPng(string inputPath, int dpi = DefaultRenderDpi,
            Rendering.PdfRenderOptions renderOptions = null)
            => NotifyPagesRendered(
                Rendering.PdfPageRenderer.RenderAllPages(inputPath, dpi, renderOptions), jpeg: false);

        /// <summary>
        /// Renders every page of a PDF stream to PNG images.
        /// </summary>
        public static byte[][] RenderAllPagesToPng(Stream input, int dpi = DefaultRenderDpi,
            Rendering.PdfRenderOptions renderOptions = null)
        {
            return NotifyPagesRendered(
                Rendering.PdfPageRenderer.RenderAllPages(input, dpi, renderOptions), jpeg: false);
        }

        /// <summary>
        /// Renders a single page of a PDF file to a JPEG image using the built-in
        /// pure C# renderer (no native PDF engine required).
        /// </summary>
        /// <param name="inputPath">Path to the source PDF file.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="dpi">Output resolution (dots per inch). Defaults to <see cref="DefaultRenderDpi"/>.</param>
        /// <param name="quality">JPEG quality, 1–100. Default 85.</param>
        /// <param name="renderOptions">
        /// Render options, or <see langword="null"/> for <see cref="Rendering.PdfRenderOptions.Default"/>.
        /// </param>
        /// <returns>JPEG-encoded bytes of the rendered page.</returns>
        public static byte[] RenderPageToJpg(string inputPath, int pageIndex, int dpi = DefaultRenderDpi,
            int quality = Rendering.PdfPageRenderer.DefaultJpegQuality,
            Rendering.PdfRenderOptions renderOptions = null)
            => NotifyPageRendered(
                Rendering.PdfPageRenderer.RenderPageAsJpg(inputPath, pageIndex, dpi, quality, renderOptions),
                pageIndex, jpeg: true);

        /// <summary>
        /// Renders a single page from a PDF stream to a JPEG image.
        /// </summary>
        public static byte[] RenderPageToJpg(Stream input, int pageIndex, int dpi = DefaultRenderDpi,
            int quality = Rendering.PdfPageRenderer.DefaultJpegQuality,
            Rendering.PdfRenderOptions renderOptions = null)
        {
            return NotifyPageRendered(
                Rendering.PdfPageRenderer.RenderPageAsJpg(input, pageIndex, dpi, quality, renderOptions),
                pageIndex, jpeg: true);
        }

        /// <summary>
        /// Renders every page of a PDF file to JPEG images.
        /// </summary>
        public static byte[][] RenderAllPagesToJpg(string inputPath, int dpi = DefaultRenderDpi,
            int quality = Rendering.PdfPageRenderer.DefaultJpegQuality,
            Rendering.PdfRenderOptions renderOptions = null)
            => NotifyPagesRendered(
                Rendering.PdfPageRenderer.RenderAllPagesAsJpg(inputPath, dpi, quality, renderOptions),
                jpeg: true);

        /// <summary>
        /// Renders every page of a PDF stream to JPEG images.
        /// </summary>
        public static byte[][] RenderAllPagesToJpg(Stream input, int dpi = DefaultRenderDpi,
            int quality = Rendering.PdfPageRenderer.DefaultJpegQuality,
            Rendering.PdfRenderOptions renderOptions = null)
        {
            return NotifyPagesRendered(
                Rendering.PdfPageRenderer.RenderAllPagesAsJpg(input, dpi, quality, renderOptions),
                jpeg: true);
        }


        // Cancellation arrives through a separate, wider overload of each — never by appending an
        // optional parameter to the shipped signature, which is a binary break. The rule, the
        // reason, and why the token precedes `options` are on PdfSvgRenderer's own block.

        /// <summary>
        /// Renders a single page of a PDF file to SVG markup (vector output).
        /// </summary>
        public static string RenderPageToSvg(string inputPath, int pageIndex,
                                            Rendering.PdfRenderOptions options = null)
            => Rendering.PdfSvgRenderer.RenderPageToSvg(inputPath, pageIndex, options);

        /// <summary>
        /// Renders a single page of a PDF file to SVG markup, abandoning it on cancellation.
        /// </summary>
        public static string RenderPageToSvg(string inputPath, int pageIndex,
                                            System.Threading.CancellationToken cancellationToken,
                                            Rendering.PdfRenderOptions options = null)
            => Rendering.PdfSvgRenderer.RenderPageToSvg(inputPath, pageIndex, cancellationToken, options);

        /// <summary>
        /// Renders a single page of a PDF stream to SVG markup (vector output).
        /// </summary>
        public static string RenderPageToSvg(Stream input, int pageIndex,
                                            Rendering.PdfRenderOptions options = null)
            => Rendering.PdfSvgRenderer.RenderPageToSvg(input, pageIndex, options);

        /// <summary>
        /// Renders a single page of a PDF stream to SVG markup, abandoning it on cancellation.
        /// </summary>
        public static string RenderPageToSvg(Stream input, int pageIndex,
                                            System.Threading.CancellationToken cancellationToken,
                                            Rendering.PdfRenderOptions options = null)
            => Rendering.PdfSvgRenderer.RenderPageToSvg(input, pageIndex, cancellationToken, options);

        /// <summary>
        /// Renders all pages of a PDF file to SVG markup strings.
        /// </summary>
        public static string[] RenderAllPagesToSvg(string inputPath,
                                                   Rendering.PdfRenderOptions options = null)
            => Rendering.PdfSvgRenderer.RenderAllPagesToSvg(inputPath, options);

        /// <summary>
        /// Renders all pages of a PDF file to SVG markup strings, abandoning on cancellation.
        /// </summary>
        public static string[] RenderAllPagesToSvg(string inputPath,
                                                   System.Threading.CancellationToken cancellationToken,
                                                   Rendering.PdfRenderOptions options = null)
            => Rendering.PdfSvgRenderer.RenderAllPagesToSvg(inputPath, cancellationToken, options);

        /// <summary>
        /// Renders all pages of a PDF stream to SVG markup strings.
        /// </summary>
        public static string[] RenderAllPagesToSvg(Stream input,
                                                   Rendering.PdfRenderOptions options = null)
            => Rendering.PdfSvgRenderer.RenderAllPagesToSvg(input, options);

        /// <summary>
        /// Renders all pages of a PDF stream to SVG markup strings, abandoning on cancellation.
        /// </summary>
        public static string[] RenderAllPagesToSvg(Stream input,
                                                   System.Threading.CancellationToken cancellationToken,
                                                   Rendering.PdfRenderOptions options = null)
            => Rendering.PdfSvgRenderer.RenderAllPagesToSvg(input, cancellationToken, options);
    }
}
