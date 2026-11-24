// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Util;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Images
{
    /// <summary>How each image's page is sized.</summary>
    public enum ImagePageSizing
    {
        /// <summary>
        /// The page is exactly the size the image is at <see cref="ImageToPdfOptions.Dpi"/> — the
        /// right default for a scan, where the paper size is what the scanner already decided.
        /// </summary>
        MatchImage,

        /// <summary>
        /// Every page is <see cref="ImageToPdfOptions.PageWidthPoints"/> by
        /// <see cref="ImageToPdfOptions.PageHeightPoints"/>, and the image is scaled to fit inside
        /// the margins without distorting its aspect ratio.
        /// </summary>
        FitToPage,
    }

    /// <summary>Policy for <see cref="PdfImageToPdf"/>.</summary>
    public sealed class ImageToPdfOptions
    {
        /// <summary>How each page is sized. Defaults to <see cref="ImagePageSizing.MatchImage"/>.</summary>
        public ImagePageSizing Sizing { get; set; } = ImagePageSizing.MatchImage;

        /// <summary>
        /// Resolution used to turn pixels into points under <see cref="ImagePageSizing.MatchImage"/>.
        /// Defaults to 96, the CSS reference resolution — a screenshot then comes out the size it
        /// looked. A scan usually wants the resolution it was scanned at.
        /// </summary>
        public double Dpi { get; set; } = 96;

        /// <summary>Page width in points under <see cref="ImagePageSizing.FitToPage"/>. Defaults to A4.</summary>
        public double PageWidthPoints { get; set; } = PdfPageSizes.A4Width;

        /// <summary>Page height in points under <see cref="ImagePageSizing.FitToPage"/>. Defaults to A4.</summary>
        public double PageHeightPoints { get; set; } = PdfPageSizes.A4Height;

        /// <summary>Margin in points under <see cref="ImagePageSizing.FitToPage"/>.</summary>
        public double MarginPoints { get; set; }

        /// <summary>
        /// Whether an image that does not fill its page is centred rather than placed at the
        /// bottom-left corner. On by default; ignored under <see cref="ImagePageSizing.MatchImage"/>,
        /// where the image fills the page exactly.
        /// </summary>
        public bool Center { get; set; } = true;

        /// <summary>
        /// Whether an image that cannot be embedded stops the whole conversion. Off by default: a
        /// batch of scans with one unreadable file should still produce the rest, with the failure
        /// reported. Turn it on when a partial document would be worse than none.
        /// </summary>
        public bool StopOnUnreadableImage { get; set; }
    }

    /// <summary>What a conversion produced, and what it could not.</summary>
    public sealed class ImageToPdfResult
    {
        internal readonly List<string> WarningList = new List<string>();

        /// <summary>Pages written — one per image that was embedded.</summary>
        public int PagesWritten { get; internal set; }

        /// <summary>One entry per image that was skipped, saying which and why.</summary>
        /// <remarks>
        /// <b>An empty document and a document of unreadable images are different facts</b>, and
        /// the page count alone cannot tell them apart.
        /// </remarks>
        public IReadOnlyList<string> Warnings => WarningList;
    }

    /// <summary>
    /// Wraps raster images as PDF pages — one page per image.
    /// </summary>
    /// <remarks>
    /// <para>The reverse of this library's thorough PDF → image side, and the natural companion to
    /// <c>PdfOperations.MakeSearchable</c>: scan-to-PDF is this plus a text layer.
    /// <c>PdfImageInserter</c> is a different operation — it places an image on an <i>existing</i>
    /// page.</para>
    /// <para><b>A JPEG is embedded byte-for-byte, never re-encoded.</b> Its codestream is already a
    /// PDF image filter (<c>/DCTDecode</c>), so passing it through costs nothing and loses nothing;
    /// decoding and re-encoding a scan would be a second generation of loss for no gain. Everything
    /// else is decoded to samples and Flate-compressed, which is lossless.</para>
    /// <para><b>A TIFF is every page it carries</b> (<c>Base.Images.TiffImageDecoder</c>), decoded
    /// to samples like any non-JPEG — a scanner writes one image per sheet, so one input can be
    /// many pages. A page the decoder cannot read is skipped and the rest kept; a TIFF with no
    /// readable page is refused with the decoder's reason. This paragraph once said TIFF was refused
    /// outright, long after the decoder landed.</para>
    /// </remarks>
    public static class PdfImageToPdf
    {
        /// <summary>Bits per component of everything written here.</summary>
        private const int BitsPerComponent = 8;

        /// <summary>
        /// Writes a PDF with one page per image.
        /// </summary>
        /// <param name="images">The raster images, in page order.</param>
        /// <param name="output">Where the PDF is written. Not disposed.</param>
        /// <param name="options">Sizing and failure policy; null uses the defaults.</param>
        /// <exception cref="PdfException">
        /// No image could be embedded, or one could not and
        /// <see cref="ImageToPdfOptions.StopOnUnreadableImage"/> is set.
        /// </exception>
        public static ImageToPdfResult Create(
            IEnumerable<byte[]> images, Stream output, ImageToPdfOptions options = null)
        {
            if (images == null) throw new ArgumentNullException(nameof(images));
            if (output == null) throw new ArgumentNullException(nameof(output));
            options ??= new ImageToPdfOptions();
            // Layout clamps each of these with Math.Max — which returns NaN for a NaN operand, so a
            // NaN page size or margin made a page with no size and a successful result.
            NumericArguments.RequireFinite(options.Dpi, nameof(options), nameof(options.Dpi));
            NumericArguments.RequireFinite(options.PageWidthPoints, nameof(options), nameof(options.PageWidthPoints));
            NumericArguments.RequireFinite(options.PageHeightPoints, nameof(options), nameof(options.PageHeightPoints));
            NumericArguments.RequireFinite(options.MarginPoints, nameof(options), nameof(options.MarginPoints));

            var result = new ImageToPdfResult();
            var builder = new PdfDocumentBuilder();
            int index = 0;

            foreach (byte[] image in images)
            {
                int number = index++;
                var skipped = new List<string>();
                bool addedAny = TryAddPages(builder, image, options, out int added, out string why, skipped);
                // A multi-page TIFF's page that could not be read is said, as a whole image is: twenty by
                // name and the rest counted, since the file chooses how many.
                for (int k = 0; k < skipped.Count && k < 20; k++) result.WarningList.Add($"image {number}: {skipped[k]}");
                if (skipped.Count > 20)
                    result.WarningList.Add($"image {number}: {skipped.Count - 20} more TIFF pages skipped");
                if (!addedAny)
                {
                    if (options.StopOnUnreadableImage)
                        throw new PdfException($"image {number}: {why}");
                    result.WarningList.Add($"image {number}: {why}");
                    continue;
                }

                // One INPUT can be several pages: a multi-page TIFF is one file holding a sheaf of
                // scans, so counting inputs here would under-report a document by however many
                // sheets each file carried.
                result.PagesWritten += added;
            }

            // A PDF must have at least one page (§7.7.3.2), so an empty result is not a document
            // to write — and writing a zero-page file would turn "nothing could be read" into a
            // file the caller has to open to discover that.
            if (result.PagesWritten == 0)
                throw new PdfException(
                    "No image could be embedded, so there is no document to write. " +
                    string.Join("; ", result.WarningList));

            builder.Build(output);
            return result;
        }

        /// <summary>
        /// Writes a PDF with one page per image file.
        /// </summary>
        /// <param name="imagePaths">Paths to the images, in page order.</param>
        /// <param name="output">Where the PDF is written. Not disposed.</param>
        /// <param name="options">Sizing and failure policy; null uses the defaults.</param>
        public static ImageToPdfResult Create(
            IEnumerable<string> imagePaths, Stream output, ImageToPdfOptions options = null)
        {
            if (imagePaths == null) throw new ArgumentNullException(nameof(imagePaths));
            return Create(ReadAll(imagePaths), output, options);
        }

        /// <summary>
        /// Reads each file as it is needed rather than loading the whole batch first: a hundred
        /// scans is a hundred multi-megabyte buffers, and only one is in use at a time.
        /// </summary>
        private static IEnumerable<byte[]> ReadAll(IEnumerable<string> paths)
        {
            foreach (string path in paths)
                yield return path == null ? null : File.ReadAllBytes(path);
        }

        // ----------------------------------------------------------------
        //  One image → one page
        // ----------------------------------------------------------------

        /// <summary>
        /// Adds every page one input carries, which is one for all formats but TIFF.
        /// </summary>
        /// <remarks>
        /// <b>A TIFF's images are a linked list of IFDs and a scanner writes one per sheet</b>, so
        /// "the first image" is the wrong answer here in a way it is not for an animated GIF. The
        /// count is handed back rather than assumed, because the caller's page tally is otherwise a
        /// count of files.
        /// </remarks>
        private static bool TryAddPages(PdfDocumentBuilder builder, byte[] image,
            ImageToPdfOptions options, out int pagesAdded, out string why, List<string> skipped = null)
        {
            pagesAdded = 0;
            why = null;

            if (Base.Images.TiffImageDecoder.IsTiff(image))
            {
                int pages = Base.Images.TiffImageDecoder.CountPages(image);
                if (pages <= 0)
                {
                    why = Base.Images.TiffImageDecoder.LastError ?? "the TIFF holds no pages";
                    return false;
                }

                // Every page of one file: a page reading bytes an earlier page's strips read is refused,
                // so ten thousand directories over one small strip are one page, not ten thousand.
                var earlierPages = new Base.Images.TiffImageDecoder.StripLedger();
                for (int p = 0; p < pages; p++)
                {
                    if (!Base.Images.TiffImageDecoder.TryDecodePage(
                            image, p, earlierPages, out byte[] rgb, out int tw, out int th))
                    {
                        // A page this decoder cannot read does not condemn the ones it can: a
                        // hundred-sheet scan with one JPEG-compressed page is ninety-nine usable
                        // pages, and refusing the file would lose them.
                        if (pagesAdded == 0 && p == pages - 1)
                        {
                            why = "TIFF page " + p.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                  + ": " + (Base.Images.TiffImageDecoder.LastError ?? "could not be decoded");
                            return false;
                        }
                        skipped?.Add("TIFF page " + p.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                     + " skipped: " + (Base.Images.TiffImageDecoder.LastError ?? "could not be decoded"));
                        continue;
                    }

                    if (!EmitRgbPage(builder, rgb, tw, th, null, options, out why)) return false;
                    pagesAdded++;
                }

                // At least one page was added: a last page that failed with none before it returned
                // above, and a TIFF with no pages was refused before the loop. A "no page could be
                // decoded" check here would be unreachable, covering for the pages <= 0 guard.
                return true;
            }

            if (!TryAddPage(builder, image, options, out why)) return false;
            pagesAdded = 1;
            return true;
        }

        private static bool TryAddPage(
            PdfDocumentBuilder builder, byte[] image, ImageToPdfOptions options, out string why)
        {
            why = null;

            if (image == null || image.Length == 0) { why = "no bytes were supplied"; return false; }

            if (!RasterImageInspector.TryInspect(image, out RasterImageInfo info))
            {
                why = "the bytes are not a raster image this library recognises";
                return false;
            }

            byte[] data;
            string filter;
            string colorSpace;
            double[] decode = null;
            int widthPx, heightPx;
            byte[] alpha = null;
            int bitsPerComponent = BitsPerComponent;
            int smaskInData = 0;

            if (info.Format == RasterImageFormat.Jpeg2000)
            {
                // Passed through like a JPEG: a JP2 file or codestream IS a PDF image filter's data.
                // The dictionary's shape -- which keys a JPEG 2000 file wants and which it must not
                // have -- is JpxImageDictionary's, the one rule every embedder of one follows.
                if (!Core.Imaging.Jpx.JpxImageDictionary.TryDescribe(image, out widthPx, out heightPx,
                        out bitsPerComponent, out colorSpace, out smaskInData, out why))
                    return false;
                data = image;
                filter = PdfFilterNames.JpxDecode;
            }
            else if (info.Format == RasterImageFormat.Jpeg)
            {
                // Passed through: a JPEG codestream IS a PDF image filter, so re-encoding it would
                // be a second generation of loss for nothing.
                if (!JpegImageDictionary.TryDescribe(image, out var frame, out colorSpace, out decode, out why))
                    return false;

                data = image;
                filter = PdfName.DCTDecode.Value;
                widthPx = frame.Width;
                heightPx = frame.Height;
            }
            else
            {
                // Refused on what the header claims, before a pixel is allocated: the decode below
                // would otherwise size its surface from the same claim.
                string refusal = Core.Writing.AuthoredImageSize.Refusal(info.Width, info.Height);
                if (refusal != null) { why = refusal; return false; }

                if (!TryDecodeToRgb(image, out byte[] samples, out widthPx, out heightPx, out alpha, out why))
                    return false;

                // Raw, with no filter: PdfContentStreamBuilder.AddImage deflates unfiltered samples
                // itself, so compressing them here would compress them twice.
                data = samples;
                filter = null;
                colorSpace = PdfColorSpaceNames.DeviceRGB;
            }

            return Emit(builder, data, widthPx, heightPx, filter, colorSpace, decode, alpha,
                        options, out why, bitsPerComponent, smaskInData);
        }

        /// <summary>Adds one page holding already-decoded 8-bit RGB samples, which <c>AddImage</c> deflates.</summary>
        private static bool EmitRgbPage(PdfDocumentBuilder builder, byte[] samples,
            int widthPx, int heightPx, byte[] alpha, ImageToPdfOptions options, out string why)
            => Emit(builder, samples, widthPx, heightPx, filter: null, PdfColorSpaceNames.DeviceRGB,
                    null, alpha, options, out why);

        private static bool Emit(PdfDocumentBuilder builder, byte[] data, int widthPx, int heightPx,
            string filter, string colorSpace, double[] decode, byte[] alpha,
            ImageToPdfOptions options, out string why, int bitsPerComponent = BitsPerComponent, int smaskInData = 0)
        {
            why = null;

            if (widthPx <= 0 || heightPx <= 0)
            {
                why = $"the image reports an empty size ({widthPx}x{heightPx})";
                return false;
            }

            // Every page's image, whatever its route: a JPEG's frame header is written into the
            // page's image dictionary verbatim, so a 65535 x 65535 SOF would reach the output as is.
            why = Core.Writing.AuthoredImageSize.Refusal(widthPx, heightPx);
            if (why != null) return false;

            (double pageW, double pageH, double x, double y, double drawW, double drawH) =
                Layout(widthPx, heightPx, options);

            var content = builder.AddPage(pageW, pageH);
            // One call for both: the eight-argument overload owns the /Decode array and the soft
            // mask, including the "a fully opaque mask is not written" rule -- so this file does
            // not re-decide it.
            // A JPEG 2000 file's dictionary may leave /ColorSpace and /BitsPerComponent to the file
            // and ask for its opacity channel; the builder has an entry for exactly that shape.
            string name = filter == PdfFilterNames.JpxDecode
                ? content.AddJpxImage(data, widthPx, heightPx, colorSpace, bitsPerComponent, smaskInData)
                : content.AddImage(data, widthPx, heightPx, filter, colorSpace, bitsPerComponent, decode, alpha);

            content.DrawImage(name, x, y, drawW, drawH);
            return true;
        }

        /// <summary>
        /// The page size and where the image sits on it.
        /// </summary>
        /// <remarks>
        /// <b>The aspect ratio is preserved under <see cref="ImagePageSizing.FitToPage"/>.</b>
        /// Scaling each axis independently to fill the page is the obvious implementation and
        /// produces a stretched image on every page whose proportions differ from the paper's —
        /// which is most of them.
        /// </remarks>
        internal static (double pageW, double pageH, double x, double y, double drawW, double drawH)
            Layout(int widthPx, int heightPx, ImageToPdfOptions options)
        {
            if (options.Sizing == ImagePageSizing.MatchImage)
            {
                double dpi = options.Dpi > 0 ? options.Dpi : 96;
                double w = widthPx * PdfUnits.PointsPerInch / dpi;
                double h = heightPx * PdfUnits.PointsPerInch / dpi;
                return (w, h, 0, 0, w, h);
            }

            double pageW = Math.Max(options.PageWidthPoints, 1);
            double pageH = Math.Max(options.PageHeightPoints, 1);
            double margin = Math.Max(options.MarginPoints, 0);

            double availW = Math.Max(pageW - margin * 2, 1);
            double availH = Math.Max(pageH - margin * 2, 1);

            double scale = Math.Min(availW / widthPx, availH / heightPx);
            double drawW = widthPx * scale;
            double drawH = heightPx * scale;

            double x = options.Center ? (pageW - drawW) / 2 : margin;
            double y = options.Center ? (pageH - drawH) / 2 : margin;
            return (pageW, pageH, x, y, drawW, drawH);
        }

        /// <summary>
        /// Decodes a non-JPEG raster to 8-bit RGB samples, plus an 8-bit alpha plane when the image
        /// has one.
        /// </summary>
        /// <remarks>
        /// Uses the SkiaSharp codec Base already depends on for rendering, so the formats this
        /// accepts are exactly the formats this library can already read — no second opinion about
        /// which images exist, which is the divergence <see cref="RasterImageInspector"/>'s own
        /// remarks record.
        /// </remarks>
        private static bool TryDecodeToRgb(
            byte[] image, out byte[] rgb, out int width, out int height, out byte[] alpha, out string why)
        {
            rgb = null;
            alpha = null;
            width = height = 0;
            why = null;

            // The renderers' capped decode, not SKBitmap.Decode, which allocates the codec's real
            // frame before anything can check it. The header was checked above, but the codec is the
            // authority on what it will allocate, and it can disagree with the inspector.
            SkiaSharp.SKBitmap bitmap = Rendering.ContentStreamInterpreter.SafeDecodeEncodedImage(image);
            if (bitmap == null)
            {
                why = "the image could not be decoded, or its frame exceeds the image size limits";
                return false;
            }

            using (bitmap)
            {
                width = bitmap.Width;
                height = bitmap.Height;
                if (width <= 0 || height <= 0)
                {
                    why = $"the decoded image is empty ({width}x{height})";
                    return false;
                }

                long rgbBytes = (long)width * height * 3;
                if (rgbBytes > int.MaxValue)
                {
                    why = $"the image is {width}x{height}, whose RGB samples exceed the largest " +
                          "array this runtime can allocate";
                    return false;
                }

                // The buffer, a row at a time, through the one straight-colour reader — not GetPixel
                // per pixel, which unpremultiplies in float32 and cost about three times as long
                // (as OcrSampleBuffer measured). The alpha plane is the stored byte either way; a
                // colour under partial alpha may differ from GetPixel's by one level, Skia's integer
                // table against its float rounding (StraightPixelLayout's remarks).
                byte[] samples = new byte[rgbBytes];
                byte[] a = new byte[(long)width * height];
                int w = width;
                Base.Images.BitmapPixels.Read(bitmap, (row, y) =>
                {
                    int o = y * w * 3, ao = y * w;
                    foreach (SkiaSharp.SKColor c in row)
                    {
                        samples[o++] = c.Red;
                        samples[o++] = c.Green;
                        samples[o++] = c.Blue;
                        a[ao++] = c.Alpha;
                    }
                });

                // Handed over unconditionally: PdfContentStreamBuilder.AddImage drops a fully
                // opaque mask itself, on the RAW alpha, and deciding it here as well would be a
                // second opinion that can only ever disagree.
                rgb = samples;
                alpha = a;
                return true;
            }
        }
    }
}
