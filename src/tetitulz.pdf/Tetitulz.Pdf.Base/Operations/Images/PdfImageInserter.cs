// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Util;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Operations.Documents;

namespace Tetitulz.Pdf.Base.Operations.Images
{
    /// <summary>
    /// Inserts a raster image onto a specific page at a given position.
    /// </summary>
    public static class PdfImageInserter
    {
        /// <summary>Both entry points emit 8-bit samples, so every image written here is 8 bits per component.</summary>
        private const int BitsPerComponent = 8;

        /// <summary>
        /// Inserts a JPEG image onto the specified page.
        /// </summary>
        /// <remarks>
        /// <para><b>The image dictionary is derived from the codestream, not from the caller.</b>
        /// <c>/ColorSpace</c> comes from the SOF component count and <c>/Width</c>/<c>/Height</c>
        /// from the SOF dimensions, so the dictionary cannot contradict the bytes it describes.
        /// A fixed <c>/DeviceRGB</c> would make a grayscale JPEG an XObject declaring three
        /// components per pixel over a one-component frame, which a
        /// conforming reader that honours the declared space reports as damaged — invisible in
        /// this library's own renderer, because it trusts the codestream, and visible only in
        /// third-party viewers.</para>
        /// <para>A four-component (CMYK / YCCK) JPEG is emitted with
        /// <c>/Decode [1 0 1 0 1 0 1 0]</c> when it carries an Adobe APP14 marker, since Adobe
        /// writes those components inverted, and is <b>refused</b> when it does not — nothing in
        /// the codestream says which way round the values are, and a wrong guess yields a
        /// colour-negative image that still opens fine. Same rule
        /// <see cref="ReplacementImage.Jpeg"/> applies.</para>
        /// </remarks>
        /// <param name="input">Source PDF stream.</param>
        /// <param name="output">Output PDF stream.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="jpegBytes">Raw JPEG image bytes.</param>
        /// <param name="imageWidthPx">Image width in pixels; must match the codestream's SOF.</param>
        /// <param name="imageHeightPx">Image height in pixels; must match the codestream's SOF.</param>
        /// <param name="xPt">X position in PDF points from left edge.</param>
        /// <param name="yPt">Y position in PDF points from bottom edge.</param>
        /// <param name="widthPt">Rendered width in PDF points.</param>
        /// <param name="heightPt">Rendered height in PDF points.</param>
        /// <exception cref="ArgumentException">
        /// The bytes carry no readable JPEG frame header, use an unsupported component count, are a
        /// four-component frame with no Adobe marker, or the supplied pixel dimensions disagree
        /// with the frame's.
        /// </exception>
        public static void InsertJpeg(
            Stream input, Stream output,
            int pageIndex,
            byte[] jpegBytes,
            int imageWidthPx, int imageHeightPx,
            double xPt, double yPt,
            double widthPt, double heightPt)
        {
            if (jpegBytes == null) throw new ArgumentNullException(nameof(jpegBytes));

            // The colour space, the Adobe-marker rule and the CMYK /Decode live in
            // JpegImageDictionary, because three writers embed a JPEG and this is the decision
            // that is right in one copy and forgotten in the next.
            if (!JpegImageDictionary.TryDescribe(
                    jpegBytes, out var frame, out string colorSpace, out double[] decode, out string why))
                throw new ArgumentException(why, nameof(jpegBytes));

            if (imageWidthPx != frame.Width || imageHeightPx != frame.Height)
                throw new ArgumentException(
                    $"The supplied pixel size {imageWidthPx}x{imageHeightPx} disagrees with the " +
                    $"JPEG's own frame header ({frame.Width}x{frame.Height}). The dictionary is " +
                    "written from the codestream, so a mismatch would silently corrupt the page " +
                    "geometry rather than be honoured.",
                    nameof(imageWidthPx));

            InsertImage(input, output, pageIndex,
                jpegBytes, frame.Width, frame.Height,
                PdfName.DCTDecode.Value, colorSpace, BitsPerComponent,
                xPt, yPt, widthPt, heightPt, decode);
        }

        /// <summary>
        /// Inserts a raw RGB image onto the specified page.
        /// </summary>
        /// <param name="input">Source PDF stream.</param>
        /// <param name="output">Output PDF stream.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="rgbBytes">Raw RGB bytes (3 bytes per pixel, row-major).</param>
        /// <param name="imageWidthPx">Image width in pixels.</param>
        /// <param name="imageHeightPx">Image height in pixels.</param>
        /// <param name="xPt">X position in PDF points from left edge.</param>
        /// <param name="yPt">Y position in PDF points from bottom edge.</param>
        /// <param name="widthPt">Rendered width in PDF points.</param>
        /// <param name="heightPt">Rendered height in PDF points.</param>
        /// <exception cref="ArgumentException">
        /// <paramref name="rgbBytes"/> is not exactly <c>width * height * 3</c> bytes.
        /// </exception>
        public static void InsertRaw(
            Stream input, Stream output,
            int pageIndex,
            byte[] rgbBytes,
            int imageWidthPx, int imageHeightPx,
            double xPt, double yPt,
            double widthPt, double heightPt)
        {
            // Checked here, under the parameter's own name: InsertImage's check names `imageBytes`,
            // which is not a parameter of this method.
            if (rgbBytes == null) throw new ArgumentNullException(nameof(rgbBytes));
            InsertImage(input, output, pageIndex,
                rgbBytes, imageWidthPx, imageHeightPx,
                filter: null, PdfColorSpaceNames.DeviceRGB, BitsPerComponent,
                xPt, yPt, widthPt, heightPt, rawComponents: 3);
        }

        private static void InsertImage(
            Stream input, Stream output,
            int pageIndex,
            byte[] imageBytes,
            int imageWidthPx, int imageHeightPx,
            string filter, string colorSpace, int bitsPerComponent,
            double xPt, double yPt,
            double widthPt, double heightPt,
            double[] decode = null,
            int rawComponents = 0)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (imageBytes == null) throw new ArgumentNullException(nameof(imageBytes));
            if (imageWidthPx <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidthPx));
            if (imageHeightPx <= 0) throw new ArgumentOutOfRangeException(nameof(imageHeightPx));
            // Finite first: NaN is neither <= 0 nor > 0, so the size checks let it through, and a
            // NaN or an infinity was written into the content stream as a token no reader parses.
            NumericArguments.RequireFinite(xPt, nameof(xPt));
            NumericArguments.RequireFinite(yPt, nameof(yPt));
            NumericArguments.RequireFinite(widthPt, nameof(widthPt));
            NumericArguments.RequireFinite(heightPt, nameof(heightPt));
            if (widthPt <= 0) throw new ArgumentOutOfRangeException(nameof(widthPt));
            if (heightPt <= 0) throw new ArgumentOutOfRangeException(nameof(heightPt));

            // The declared pixel size is bounded HERE, in the one core both entry points reach,
            // rather than at either of them. InsertJpeg takes the dimensions from the JPEG's own
            // frame header — it checks that the caller AGREES with the header, which is not the
            // same as checking the header — so a 65535 x 65535 SOF would otherwise be written
            // straight into the produced XObject. The rule is AuthoredImageSize's, shared with
            // PdfImageToPdf, which did not have it.
            string refusal = Core.Writing.AuthoredImageSize.Refusal(imageWidthPx, imageHeightPx);
            if (refusal != null)
                throw new ArgumentOutOfRangeException(
                    imageHeightPx > PdfLimits.MaxImageDimension && imageWidthPx <= PdfLimits.MaxImageDimension
                        ? nameof(imageHeightPx) : nameof(imageWidthPx),
                    refusal);

            // Raw samples must be the raster they claim to be, checked after the size is known to
            // be writable. A short buffer was written as an image stream shorter than its /Width x
            // /Height declares: this library's renderer pads the missing rows, and another reader
            // reports the file damaged or draws whatever follows.
            if (rawComponents > 0
                && imageBytes.LongLength != (long)imageWidthPx * imageHeightPx * rawComponents)
                throw new ArgumentException(
                    $"The samples are {imageBytes.LongLength} bytes; a {imageWidthPx}x{imageHeightPx} " +
                    $"raster of {rawComponents} bytes a pixel is exactly " +
                    $"{(long)imageWidthPx * imageHeightPx * rawComponents}.",
                    "rgbBytes");

            PdfPageContentAppender.AppendContent(
                input, output, pageIndex,
                builder =>
                {
                    string imgRes = builder.AddImage(
                        imageBytes, imageWidthPx, imageHeightPx,
                        filter, colorSpace, bitsPerComponent, decode, softMask: null);

                    // DrawImage already wraps in q/Q and applies the CTM.
                    builder.DrawImage(imgRes, xPt, yPt, widthPt, heightPt);
                },
                compressStream: false);
        }

    }
}
