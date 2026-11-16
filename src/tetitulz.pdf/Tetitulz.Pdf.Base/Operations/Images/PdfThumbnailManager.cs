// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Images
{
    /// <summary>Read and generate page /Thumb streams (PDF 2.0 §12.3.4).</summary>
    public static class PdfThumbnailManager
    {
        /// <summary>Default thumbnail rasterization resolution, in dots per inch
        /// (≈100 px wide for an A4 page).</summary>
        public const int DefaultThumbnailDpi = 24;

        /// <summary>Lowest accepted thumbnail resolution, in dots per inch.</summary>
        public const int MinThumbnailDpi = 8;

        /// <summary>Highest accepted thumbnail resolution, in dots per inch — a thumbnail
        /// past this point is a full-page render, not a thumbnail.</summary>
        public const int MaxThumbnailDpi = 300;

        [ThreadStatic] private static string _lastError;

        /// <summary>
        /// Why the last <see cref="GenerateThumbnails"/> call on <b>this thread</b> attached fewer
        /// thumbnails than the document has pages, or <c>null</c> when it attached one to every
        /// page it rendered; and, after <see cref="ReadThumbnail"/>, what a JPEG 2000 thumbnail's
        /// <see cref="ThumbnailInfo"/> could not carry — a <c>/ColorSpace</c> that is an array
        /// (the codestream is returned as it is), an opacity channel <c>/SMaskInData</c> named
        /// (not part of the samples). Reset at the start of every call, on every path.
        ///
        /// <para><b>Why this exists.</b> When a page's object number could not be resolved the loop
        /// <c>continue</c>d — <i>after</i> writing that page's /Thumb image object — so the output
        /// grew an orphaned image XObject that no page referenced while the return value still
        /// counted it. A caller was told "12 thumbnails generated" for a file that displays none,
        /// and the only trace was a <c>Debug.WriteLine</c>, which does not exist in a Release
        /// build.</para>
        /// </summary>
        public static string LastError => _lastError;

        public sealed class ThumbnailInfo
        {
            public int PageIndex { get; }
            public int Width { get; }
            public int Height { get; }
            public byte[] PixelData { get; }
            public string ColorSpace { get; }

            public ThumbnailInfo(int pageIndex, int width, int height, byte[] data, string cs)
            {
                PageIndex = pageIndex;
                Width = width;
                Height = height;
                PixelData = data;
                ColorSpace = cs;
            }
        }

        /// <summary>
        /// Reads the /Thumb stream for the given page, or null if none present. A JPEG 2000
        /// thumbnail is decoded like any other image: its samples come back 8 bits each in the
        /// space the file or the dictionary defines, and <see cref="ThumbnailInfo.ColorSpace"/>
        /// names that space.
        /// </summary>
        /// <exception cref="Core.PdfEncryptedDocumentException">The document is encrypted; read it
        /// through <c>PdfOperations.ReadPageThumbnail(input, pageIndex, password)</c>.</exception>
        public static ThumbnailInfo ReadThumbnail(Stream input, int pageIndex)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            _lastError = null;
            using var doc = PdfDocument.Open(input);
            RefuseEncrypted(doc, "read a thumbnail", "PdfOperations.ReadPageThumbnail(input, pageIndex, password)");
            if (pageIndex < 0 || pageIndex >= doc.Pages.Count)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));

            var page = doc.Pages[pageIndex];
            var thumbObj = page.Dictionary[PdfName.Thumb];
            if (thumbObj == null) return null;
            var stream = doc.ResolveObject(thumbObj) as PdfStream;
            if (stream == null) return null;

            // Resolved: a thumbnail is an image XObject (§12.3.4), whose /Width, /Height and
            // /ColorSpace may each be written as their own object; read raw they gave 0x0 "Unknown".
            // PdfResolve.Int, not a cast of the double: a file-supplied number is not an integer
            // until PdfNumber says so, and (int) of an out-of-range double is int.MinValue.
            int w = Rendering.PdfResolve.Int(doc, stream, PdfName.Width, 0);
            int h = Rendering.PdfResolve.Int(doc, stream, PdfName.Height, 0);
            string cs = "Unknown";
            if (Rendering.PdfResolve.Name(doc, stream, PdfName.ColorSpace) is PdfName csName) cs = csName.Value;

            byte[] pixels = stream.GetDecodedData();
            // JPXDecode passes the codestream through the filter pipeline; the pixels come from
            // the in-house decoder, with the JP2 colour rules of ISO 32000-2 §7.4.9 —
            // a raw codestream's device fallback, a JP2 file's own colour specification, or the
            // dictionary's /ColorSpace when it has one. A thumbnail whose samples this cannot
            // name as a device space (CIELab, a stencil) keeps the codestream, as before.
            if (Rendering.ContentStreamInterpreter.DeclaresJpxFilter(stream, doc)
                && Base.Images.JpxImageDecoder.TryDecodeSamples(pixels, stream, doc, out var image, out _))
            {
                // Said rather than silently kept or dropped: a
                // /ColorSpace array names no space ThumbnailInfo.ColorSpace can carry, and the
                // samples' opacity channel is not among the samples.
                bool arraySpace = Rendering.PdfResolve.Value(doc, stream[PdfName.ColorSpace]) is PdfArray;
                if (!arraySpace && TryDeviceFamily(image, cs, out string family))
                {
                    pixels = image.Colour;
                    w = image.Width;
                    h = image.Height;
                    cs = family;
                    if (image.Opacity != null)
                        _lastError = $"page {pageIndex}: the JPEG 2000 thumbnail's opacity channel (/SMaskInData) is not part of ThumbnailInfo.PixelData";
                }
                else
                {
                    _lastError = arraySpace
                        ? $"page {pageIndex}: the JPEG 2000 thumbnail's /ColorSpace is an array, which ThumbnailInfo.ColorSpace cannot name; the codestream is returned undecoded"
                        : $"page {pageIndex}: the JPEG 2000 thumbnail's samples are {image.ColourModel}, which no device colour space names; the codestream is returned undecoded";
                }
            }

            return new ThumbnailInfo(pageIndex, w, h, pixels, cs);
        }

        /// <summary>
        /// Refuses an encrypted document by name. Opened without its password it loads no pages, so
        /// the reader answered for a document it could not see: <see cref="ReadThumbnail"/> said the
        /// page index was out of range, and <see cref="ListPagesWithThumbnails"/> that no page has a
        /// thumbnail. A document decrypted on the way in carries no <c>/Encrypt</c> and is not refused.
        /// </summary>
        private static void RefuseEncrypted(PdfDocument doc, string what, string route)
        {
            if (!doc.IsEncrypted) return;
            throw new Core.PdfEncryptedDocumentException(
                $"Cannot {what}: the document is encrypted, and opened without its password it has no " +
                "pages to read." + (route != null ? $" Use {route}." : " Decrypt it first."));
        }

        /// <summary>
        /// The PDF colour-space name a decoded JPEG 2000 thumbnail's samples are in — the
        /// dictionary's own when it governs, else the device space the file's colour resolves to.
        /// False for samples no device name describes.
        /// </summary>
        private static bool TryDeviceFamily(Core.Imaging.Jpx.JpxPdfImage image, string declared, out string family)
        {
            switch (image.ColourModel)
            {
                case Core.Imaging.Jpx.JpxColourModel.ImageDictionary: family = declared; return true;
                case Core.Imaging.Jpx.JpxColourModel.Gray: family = PdfColorSpaceNames.DeviceGray; return true;
                case Core.Imaging.Jpx.JpxColourModel.Rgb: family = PdfColorSpaceNames.DeviceRGB; return true;
                case Core.Imaging.Jpx.JpxColourModel.Cmyk: family = PdfColorSpaceNames.DeviceCMYK; return true;
                default: family = null; return false;
            }
        }

        /// <summary>Lists all pages that carry a /Thumb stream.</summary>
        /// <exception cref="Core.PdfEncryptedDocumentException">The document is encrypted.</exception>
        public static IReadOnlyList<int> ListPagesWithThumbnails(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var doc = PdfDocument.Open(input);
            RefuseEncrypted(doc, "list its thumbnails", null);
            var list = new List<int>();
            for (int i = 0; i < doc.Pages.Count; i++)
                if (doc.Pages[i].Dictionary[PdfName.Thumb] != null)
                    list.Add(i);
            return list;
        }

        /// <summary>
        /// Writes the PDF with generated /Thumb streams attached to each page.
        /// Thumbnails are rendered via <see cref="PdfPageRenderer"/> at the
        /// requested DPI (see <see cref="DefaultThumbnailDpi"/>) and embedded as
        /// DeviceRGB XObjects.
        /// </summary>
        /// <returns>
        /// The number of pages that actually carry a <c>/Thumb</c> in the output — never a count of
        /// images written. See <see cref="LastError"/> when it is lower than the page count.
        /// </returns>
        /// <remarks>
        /// <paramref name="output"/> needs neither to be seekable nor to be positioned at 0: the
        /// revision is appended through <see cref="IncrementalUpdateWriter"/>, which owns the offset
        /// bookkeeping. A writer reading <c>output.Position</c> for every xref entry would make a
        /// non-seekable destination (a server streaming the result) throw
        /// <see cref="NotSupportedException"/> from a method documenting only
        /// <see cref="ArgumentNullException"/>/<see cref="ArgumentOutOfRangeException"/>, and a
        /// destination already holding bytes produced an xref whose every offset was shifted by the
        /// starting position — a file that will not reopen, with no exception at all.
        /// </remarks>
        public static int GenerateThumbnails(Stream input, Stream output, int dpi = DefaultThumbnailDpi)
        {
            _lastError = null;
            if (input == null)  throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (dpi < MinThumbnailDpi || dpi > MaxThumbnailDpi) throw new ArgumentOutOfRangeException(nameof(dpi));

            // Capture input bytes once — renderer needs its own stream, incremental
            // writer needs its own stream.
            using var bufIn = new MemoryStream();
            input.CopyTo(bufIn);
            byte[] originalBytes = bufIn.ToArray();

            // Pre-render all thumbnails so we can assemble the incremental update.
            var thumbsRgb = new List<(int pageIndex, int w, int h, byte[] rgbBytes)>();
            using (var probeIn = new MemoryStream(originalBytes, writable: false))
            using (var doc = PdfDocument.Open(probeIn))
            {
                // Refused before anything is rendered. The thumbnails would be written in clear text
                // into a file whose reader decrypts every stream with its object's key, so each
                // would decode to noise. This wrote them and reported the count.
                if (doc.IsEncrypted)
                    throw new Core.PdfEncryptedDocumentException(
                        "Cannot add thumbnails to an encrypted document: the images would be written in " +
                        "clear text where the reader expects per-object ciphertext, and this assembly " +
                        "cannot encrypt. Decrypt first (Tetitulz.Pdf.Encryption.PdfDecryptor), add them, " +
                        "then re-encrypt — or use Tetitulz.Pdf.Encryption.PdfEncryptedRoundTrip.Run.");

                int pageCount = doc.Pages.Count;
                for (int i = 0; i < pageCount; i++)
                {
                    // Pixels, not PNG. PNG encoding is ~97% of a render (RenderedPage's remarks), and
                    // this encoded every page only to decode it on the next line and then read it a
                    // GetPixel at a time.
                    using var rs = new MemoryStream(originalBytes, writable: false);
                    RenderedPage page = PdfPageRenderer.RenderPageToPixels(rs, i, dpi);
                    thumbsRgb.Add((i, page.Width, page.Height, ToRgb(page)));
                }
            }

            // Emit an incremental update that adds a /Thumb ref to each page.
            return WriteIncrementalWithThumbs(originalBytes, output, thumbsRgb);
        }

        /// <summary>
        /// The page's RGB samples, dropping alpha.
        /// </summary>
        /// <remarks>
        /// <see cref="RenderedPage.Pixels"/> is premultiplied RGBA, and premultiplied is straight
        /// only where alpha is 255. It is here: a render with default options paints the page on an
        /// opaque white background, so every pixel of a thumbnail is opaque. Compositing over white
        /// would change nothing, and was left out rather than written as a line that cannot matter.
        /// </remarks>
        private static byte[] ToRgb(RenderedPage page)
        {
            int w = page.Width, h = page.Height;
            byte[] rgb = new byte[(long)w * h * 3];
            int o = 0;
            for (int y = 0; y < h; y++)
            {
                int row = y * page.Stride;
                for (int x = 0; x < w; x++)
                {
                    int p = row + x * 4;
                    rgb[o++] = page.Pixels[p];
                    rgb[o++] = page.Pixels[p + 1];
                    rgb[o++] = page.Pixels[p + 2];
                }
            }
            return rgb;
        }

        /// <summary>
        /// Appends one revision carrying every thumbnail image and the pages that reference it.
        ///
        /// <para><b>Nothing is emitted for a page that cannot be patched.</b> The page's object
        /// number is resolved <i>first</i>; only then is its image object allocated and added. The
        /// predecessor wrote the image and then <c>continue</c>d on a failed lookup, leaving an
        /// orphan in the output and counting it as a success.</para>
        /// </summary>
        private static int WriteIncrementalWithThumbs(
            byte[] originalBytes, Stream output,
            List<(int pageIndex, int w, int h, byte[] rgbBytes)> thumbs)
        {
            if (thumbs.Count == 0)
            {
                output.Write(originalBytes, 0, originalBytes.Length);
                return 0;
            }

            using var doc = PdfDocument.Open(new MemoryStream(originalBytes, writable: false));

            // PdfObjectIndex resolves a page's object number from one pass over the
            // cross-reference table (and falls back to the number the parser stamped on the object)
            // instead of the O(pages x objects) ReferenceEquals scan this replaces, and it owns the
            // collision-free allocator for the new image objects.
            var index = PdfObjectIndex.Build(doc);

            var objects = new List<IndirectObject>();
            var unpatched = new List<int>();

            foreach (var t in thumbs)
            {
                int pageObjNum = index.GetPageObjectNumber(t.pageIndex);
                if (pageObjNum <= 0) { unpatched.Add(t.pageIndex); continue; }

                int thumbObjNum = index.AllocateObjectNumber();
                objects.Add(new IndirectObject(thumbObjNum, 0, BuildThumbObject(t.w, t.h, t.rgbBytes)));
                objects.Add(new IndirectObject(pageObjNum, index.GetGeneration(pageObjNum),
                    ClonePageWithThumb(doc.Pages[t.pageIndex].Dictionary, thumbObjNum)));
            }

            if (unpatched.Count > 0)
                _lastError =
                    $"{unpatched.Count} page(s) could not be located in the cross-reference table " +
                    $"and carry no /Thumb (page index {string.Join(", ", unpatched)}).";

            if (objects.Count == 0)
            {
                output.Write(originalBytes, 0, originalBytes.Length);
                return 0;
            }

            IncrementalUpdateWriter.WriteUpdate(originalBytes, output, objects,
                new IncrementalUpdateOptions
                {
                    Document = doc,
                    OriginalTrailer = doc.Trailer,
                });

            return objects.Count / 2;   // one image + one patched page per attached thumbnail
        }

        private static PdfDictionary ClonePageWithThumb(PdfDictionary src, int thumbObjNum)
        {
            var d = PdfDictionary.CloneShallow(src);
            d.Set(PdfName.Thumb, new PdfReference(thumbObjNum, 0));
            return d;
        }

        /// <summary>
        /// The thumbnail image, deflated: written raw, three bytes a pixel for every page would add
        /// about 16 MB of samples to a hundred-page A4 document at the default 24 dpi.
        /// </summary>
        private static PdfStream BuildThumbObject(int w, int h, byte[] rgb)
        {
            byte[] data = Core.IO.Filters.FlateEncodeFilter.Encode(rgb);
            var s = new PdfStream();
            s.Set(PdfName.Type, PdfName.XObject);
            s.Set(PdfName.Subtype, PdfName.Image);
            s.Set(PdfName.Width, new PdfNumber(w));
            s.Set(PdfName.Height, new PdfNumber(h));
            s.Set(PdfName.ColorSpace, PdfName.DeviceRGB);
            s.Set(PdfName.BitsPerComponent, new PdfNumber(8));
            s.Set(PdfName.Filter, PdfName.FlateDecode);
            s.Set(PdfName.Length, new PdfNumber(data.Length));
            s.RawData = data;
            return s;
        }
    }
}
