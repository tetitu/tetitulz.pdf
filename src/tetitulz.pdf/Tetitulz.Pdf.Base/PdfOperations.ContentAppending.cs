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
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Base.Signing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base
{
    public static partial class PdfOperations
    {
        /// <summary>Appends new content to the specified page.</summary>
        public static void AppendContent(Stream input, Stream output, int pageIndex,
            Action<Tetitulz.Pdf.Core.Writing.PdfContentStreamBuilder> buildContent)
            => PdfPageContentAppender.AppendContent(input, output, pageIndex, buildContent);

        /// <summary>Adds a header and/or footer to all pages.</summary>
        public static void AddHeaderFooter(Stream input, Stream output, HeaderFooterOptions options)
            => PdfHeaderFooterWriter.AddHeaderFooter(input, output, options);

        /// <summary>Inserts a JPEG image onto the specified page.</summary>
        public static void InsertImage(Stream input, Stream output,
            int pageIndex, byte[] jpegBytes, int widthPx, int heightPx,
            double x, double y, double w, double h)
            => PdfImageInserter.InsertJpeg(input, output, pageIndex,
                jpegBytes, widthPx, heightPx, x, y, w, h);

        /// <summary>
        /// Creates a PDF with <b>one page per image</b> — the reverse of this library's PDF → image
        /// side, and half of scan-to-PDF (the other half is
        /// <see cref="MakeSearchable(Stream, Stream, int)"/>).
        /// </summary>
        /// <param name="images">The raster images, in page order.</param>
        /// <param name="output">Where the PDF is written. Not disposed.</param>
        /// <param name="options">Sizing and failure policy; null uses the defaults.</param>
        /// <remarks>
        /// A JPEG is embedded byte-for-byte rather than re-encoded — its codestream already is a
        /// PDF image filter. This is not <see cref="InsertImage"/>, which puts an image on an
        /// <i>existing</i> page.
        /// </remarks>
        public static ImageToPdfResult CreatePdfFromImages(
            IEnumerable<byte[]> images, Stream output, ImageToPdfOptions options = null)
            => PdfImageToPdf.Create(images, output, options);

        /// <summary>
        /// Creates a PDF with one page per image file. See
        /// <see cref="CreatePdfFromImages(IEnumerable{byte[]}, Stream, ImageToPdfOptions)"/>.
        /// </summary>
        /// <param name="imagePaths">Paths to the images, in page order.</param>
        /// <param name="output">Where the PDF is written. Not disposed.</param>
        /// <param name="options">Sizing and failure policy; null uses the defaults.</param>
        public static ImageToPdfResult CreatePdfFromImages(
            IEnumerable<string> imagePaths, Stream output, ImageToPdfOptions options = null)
            => PdfImageToPdf.Create(imagePaths, output, options);

        /// <summary>
        /// Encodes raw samples as JPEG 2000 — a JP2 file (or a bare codestream) for a
        /// <c>/JPXDecode</c> image, lossless by default, or lossy at a quality, or fitted to a size.
        /// <paramref name="samples"/> are row-major, <paramref name="components"/> per pixel (1 grey,
        /// 3 RGB, 4 CMYK), one byte each up to 8 bits and two big-endian bytes above.
        /// </summary>
        /// <remarks>The one encoder is <see cref="JpxImageEncoding.Encode"/>; this is its door on the facade.</remarks>
        public static byte[] EncodeJpeg2000(byte[] samples, int width, int height, int components, int bitsPerComponent,
            JpxEncodingOptions options = null)
            => JpxImageEncoding.Encode(samples, width, height, components, bitsPerComponent, options);

        /// <summary>
        /// Rewrites every HTJ2K image (ITU-T T.814) the document holds as Part 1 JPEG 2000 — the form ISO 32000's
        /// <c>JPXDecode</c> and PDF/A ask for, and the one every viewer reads — losslessly, by an incremental
        /// update that keeps each image's object number and dictionary. An approval signature stays valid; a
        /// DocMDP certification refuses unless <paramref name="allowCertifiedDocument"/>; an encrypted document
        /// is refused. With nothing to transcode <paramref name="output"/> receives the input unchanged.
        /// </summary>
        /// <remarks>The transcoder is <see cref="JpxTranscoding"/>; this is its door for a whole document (<see cref="PdfJpxTranscoder"/>).</remarks>
        public static JpxTranscodeResult TranscodeHtj2kImagesToPart1(Stream input, Stream output, bool allowCertifiedDocument = false)
            => PdfJpxTranscoder.TranscodeHighThroughputToPart1(input, output, allowCertifiedDocument);

        /// <summary>
        /// Replaces an existing image's pixels in place, keeping its object number, via an
        /// incremental update — so an approval signature on the input stays cryptographically
        /// valid and the output can be signed again. <paramref name="imageIndex"/> is the index
        /// <see cref="ExtractImage(Stream,int,int)"/> and <see cref="CountImagesOnPage(Stream,int)"/> use.
        ///
        /// <para>Refuses an encrypted document, and a DocMDP-certified one unless
        /// <see cref="ImageReplacementOptions.AllowCertifiedDocument"/> is set. Note the old
        /// pixels remain recoverable from the earlier revision — see
        /// <see cref="PdfImageReplacer"/>.</para>
        /// </summary>
        public static ImageReplacementResult ReplaceImage(Stream input, Stream output,
            int pageIndex, int imageIndex, ReplacementImage image,
            ImageReplacementOptions options = null)
            => PdfImageReplacer.Replace(input, output, pageIndex, imageIndex, image, options);

        /// <summary>
        /// As <see cref="ReplaceImage"/>, but selects the image by the resource name the page's
        /// content stream draws it with (e.g. <c>Im0</c>) rather than by index.
        /// </summary>
        public static ImageReplacementResult ReplaceImageByName(Stream input, Stream output,
            int pageIndex, string resourceName, ReplacementImage image,
            ImageReplacementOptions options = null)
            => PdfImageReplacer.ReplaceByName(input, output, pageIndex, resourceName, image, options);

        /// <summary>
        /// Every image XObject in the document, one entry per object, with the pages and resource
        /// names that reach it. Descends into form XObjects, so it finds images
        /// <see cref="CountImagesOnPage(Stream,int)"/> and <see cref="ExtractImage(Stream,int,int)"/> cannot see. Pair with
        /// <see cref="ReplaceImageByObjectNumber"/> to change a shared logo everywhere at once.
        /// </summary>
        public static IReadOnlyList<DocumentImageInfo> FindImages(Stream input)
            => FindImages(input, null);

        /// <summary>
        /// As <see cref="FindImages(Stream)"/>, decrypting first with <paramref name="password"/>
        /// when the document is encrypted.
        /// </summary>
        /// <remarks>
        /// <c>PdfImageReplacer.FindImages(Stream, string)</c> is the read-only encrypted-input path
        /// the library documents, and it was unreachable from this facade: the no-password form
        /// opened the still-encrypted document and returned an empty or garbled list with no
        /// exception and no diagnostic. Read <see cref="LastExtractionError"/> after the call to
        /// tell "encrypted, could not read" from "no images present".
        /// </remarks>
        public static IReadOnlyList<DocumentImageInfo> FindImages(Stream input, string password)
            => WithDecrypted(input, password, s =>
            {
                // A document still encrypted is refused below; this facade's contract is the empty
                // list with LastExtractionError saying why, which WithDecrypted has already set.
                try { return PdfImageReplacer.FindImages(s); }
                catch (Core.PdfEncryptedDocumentException) when (_lastExtractionError != null) { return Array.Empty<DocumentImageInfo>(); }
            });

        /// <summary>
        /// As <see cref="ReplaceImage"/>, but selects the image by object number — the identifier
        /// <see cref="FindImages(Stream)"/> returns. Not scoped to a page, so it reaches an image drawn
        /// from inside a form XObject or shared across many pages.
        /// </summary>
        public static ImageReplacementResult ReplaceImageByObjectNumber(Stream input, Stream output,
            int objectNumber, ReplacementImage image, ImageReplacementOptions options = null)
            => PdfImageReplacer.ReplaceByObjectNumber(input, output, objectNumber, image, options);
    }
}
