// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Rendering;

namespace Tetitulz.Pdf.Base.Operations.Images
{
    /// <summary>Extracts embedded raster images from PDF pages.</summary>
    public static class PdfImageExporter
    {
        public sealed class PdfExtractedImage
        {
            public byte[] Data { get; }
            public string MimeType { get; }
            public int Width { get; }
            public int Height { get; }

            public PdfExtractedImage(byte[] data, string mimeType, int width, int height)
            {
                Data = data;
                MimeType = mimeType;
                Width = width;
                Height = height;
            }
        }

        /// <summary>Counts image XObjects referenced from the given page's Resources.</summary>
        public static int CountImages(Stream input, int pageIndex)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var doc = PdfDocument.Open(input);
            EncryptedRead.Refuse(doc, "the images", null);
            return CollectPageImages(doc, pageIndex).Count;
        }

        /// <summary>
        /// Extracts the N-th image XObject referenced from the given page.
        /// For DCTDecode (JPEG) and JPXDecode (JPEG2000) streams, the original
        /// compressed bytes are returned unchanged. For all other filters the
        /// pixel data is decoded and re-encoded as PNG. <see cref="ExtractImageAsPng"/> is the
        /// route to a JPEG or JPEG 2000 image's pixels.
        /// </summary>
        public static PdfExtractedImage ExtractImage(Stream input, int pageIndex, int imageIndex)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (imageIndex < 0) throw new ArgumentOutOfRangeException(nameof(imageIndex));

            using var doc = PdfDocument.Open(input);
            EncryptedRead.Refuse(doc, "the images", null);
            PdfStream stream = FindImage(doc, pageIndex, imageIndex, out int w, out int h);

            // The codestream, decoded through whatever wraps it. DCT and JPX pass through this
            // library's filter pipeline, so decoding the stream yields the JPEG or JPEG 2000 bytes
            // themselves. RawData is the codestream only when it is the one filter: under
            // /Filter [/ASCII85Decode /DCTDecode] it is the ASCII85 text, which would be handed back
            // as "image/jpeg".
            string filter = GetPrimaryFilter(stream);
            if (string.Equals(filter, PdfFilterNames.DctDecode, StringComparison.Ordinal))
                return new PdfExtractedImage(Rendering.ContentStreamInterpreter.DecodeImageStream(stream, w), "image/jpeg", w, h);
            if (string.Equals(filter, PdfFilterNames.JpxDecode, StringComparison.Ordinal))
                return new PdfExtractedImage(Rendering.ContentStreamInterpreter.DecodeImageStream(stream, w), "image/jp2", w, h);

            // Everything else: decoded as the renderer draws it, and encoded as PNG.
            byte[] pngBytes = EncodeAsPng(doc, stream);
            return new PdfExtractedImage(pngBytes, "image/png", w, h);
        }

        /// <summary>
        /// Extracts the N-th image XObject referenced from the given page as a PNG, whatever its
        /// filter: decoded as the renderer draws it — every bit depth, colour space, ICC profile
        /// and mask, a JPEG or JPEG 2000 codestream included, the latter through the in-house
        /// decoder with the JP2 colour rules of ISO 32000-2 §7.4.9 — and encoded losslessly.
        /// </summary>
        /// <remarks>
        /// <see cref="ExtractImage"/> hands a JPEG or JPEG 2000 image back as its own codestream,
        /// which is the faithful export of the file but leaves the pixels to a codec the caller
        /// may not have. This is the route to them; the <c>Width</c> and <c>Height</c> reported
        /// are the dictionary's, as there.
        /// </remarks>
        public static PdfExtractedImage ExtractImageAsPng(Stream input, int pageIndex, int imageIndex)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (imageIndex < 0) throw new ArgumentOutOfRangeException(nameof(imageIndex));

            using var doc = PdfDocument.Open(input);
            EncryptedRead.Refuse(doc, "the images", null);
            PdfStream stream = FindImage(doc, pageIndex, imageIndex, out int w, out int h);
            return new PdfExtractedImage(EncodeAsPng(doc, stream), "image/png", w, h);
        }

        /// <summary>The N-th image of a page (<see cref="CollectPageImages"/>'s order) and its declared geometry.</summary>
        private static PdfStream FindImage(PdfDocument doc, int pageIndex, int imageIndex, out int width, out int height)
        {
            var images = CollectPageImages(doc, pageIndex);
            if (imageIndex >= images.Count)
                throw new ArgumentOutOfRangeException(nameof(imageIndex),
                    $"Page {pageIndex} has {images.Count} images; requested index {imageIndex}.");

            var stream = images[imageIndex].Stream;
            // Resolved: /Width 12 0 R is legal (§7.3.10), and read raw it exported as 0x0 — a
            // refusal for a raw image, and wrong metadata on a JPEG or JPEG 2000 one.
            width = Rendering.PdfResolve.Int(doc, stream, PdfName.Width, 0);
            height = Rendering.PdfResolve.Int(doc, stream, PdfName.Height, 0);
            return stream;
        }

        /// <summary>
        /// The image XObjects a page's <c>/Resources /XObject</c> reaches, paired with the
        /// resource name that reaches them, in the order the resource dictionary enumerates.
        ///
        /// <para><b>This is the definition of "image N on page P" for the whole assembly.</b>
        /// <see cref="ExtractImage"/> and <see cref="PdfImageReplacer"/> both index into it, so
        /// the image a caller extracts is by construction the image it replaces — a private
        /// second enumeration in either one could silently drift.</para>
        /// </summary>
        internal static List<(string ResourceName, PdfStream Stream)> CollectPageImages(
            PdfDocument doc, int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= doc.Pages.Count)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));
            var page = doc.Pages[pageIndex];
            var resources = page.Resources;
            var list = new List<(string, PdfStream)>();
            if (resources == null) return list;

            var xobj = doc.ResolveObject(resources[PdfName.XObject]) as PdfDictionary;
            if (xobj == null) return list;

            foreach (var kvp in xobj)
            {
                var resolved = doc.ResolveObject(kvp.Value) as PdfStream;
                if (resolved == null) continue;
                var subtype = PdfResolve.Name(doc, resolved, PdfName.Subtype);
                if (subtype == null || subtype.Value != PdfName.Image.Value) continue;
                list.Add((kvp.Key.Value, resolved));
            }
            return list;
        }

        /// <summary>
        /// The filter a decoder applies <b>last</b>, which is the image's own codec. §7.4 lists
        /// filters in decoding order, so the first is the outermost encoding and the last the
        /// innermost. The comment here once called the last "the outermost", and the code built on
        /// it returned the raw, still-wrapped bytes as the codestream.
        /// </summary>
        /// <remarks>
        /// Read through the indexer: <c>PdfParser</c> resolves an indirect <c>/Filter</c> in place
        /// when it builds the stream, because a filter runs in Core with no document to resolve
        /// against (<c>ContentStreamInterpreter.DecodeImageStream</c>'s remarks).
        /// </remarks>
        private static string GetPrimaryFilter(PdfDictionary stream)
        {
            var f = stream[PdfName.Filter];
            if (f is PdfName n) return n.Value;
            if (f is PdfArray arr && arr.Count > 0)
            {
                if (arr[arr.Count - 1] is PdfName last) return last.Value;
            }
            return null;
        }

        private static byte[] EncodeAsPng(PdfDocument doc, PdfStream stream)
        {
            // Decoded as the raster renderer draws the image: every bit depth (1, 2, 4, 8, 16, CCITT
            // and JBIG2 included), /Decode, every colour space and ICC profile, the masks — and the
            // renderer's own guards, which is why nothing is checked here first. Its decode refuses
            // a /Width or /Height beyond MaxImageDimension or an area beyond MaxImagePixelArea
            // before allocating, and tries an embedded JPEG, PNG or JPEG 2000 through the capped
            // SafeDecodeEncodedImage. This method did both itself, and a mutation sweep found each
            // copy changed nothing: the callee answered first.
            //
            // This exporter had a decoder of its own — 8 bits per component, five colour spaces, no
            // /Decode, no masks — and refused everything else; GAPS.md §6 recorded that the answer
            // was the renderer's decode, not a fourth one.
            using var bitmap = Rendering.ContentStreamInterpreter.DecodeImageXObject(doc, stream);
            if (bitmap == null)
                throw new NotSupportedException(
                    "The image could not be decoded; RenderDiagnostics.OnEvent reports why.");
            return PdfImageEncoder.Encode(bitmap, PdfImageFormat.Png, 100, null);
        }
    }
}
