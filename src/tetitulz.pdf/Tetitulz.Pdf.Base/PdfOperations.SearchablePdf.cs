// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Base
{
    public static partial class PdfOperations
    {
        /// <summary>
        /// What <see cref="MakeSearchable(Stream, Stream, int)"/> did, and what it could not do.
        /// </summary>
        public sealed class SearchablePdfResult
        {
            internal readonly List<string> WarningList = new List<string>();

            /// <summary>Pages the recogniser was run over.</summary>
            public int PagesProcessed { get; internal set; }

            /// <summary>Pages that gained a text layer.</summary>
            public int PagesWithText { get; internal set; }

            /// <summary>Words written across the whole document.</summary>
            public int WordsWritten { get; internal set; }

            /// <summary>
            /// One entry per page that produced no layer, saying why.
            /// </summary>
            /// <remarks>
            /// <b>An empty layer and a page that was never recognised are opposite conclusions
            /// about a scan</b>, and a caller cannot tell them apart from the counts alone. A page
            /// of blank paper legitimately yields no words; so does a page the engine refused, and
            /// only one of those means "this document has no text on it".
            /// </remarks>
            public IReadOnlyList<string> Warnings => WarningList;

            /// <summary>True when at least one page gained a text layer.</summary>
            public bool AnyTextWritten => PagesWithText > 0;
        }

        /// <summary>
        /// Recognises the words on one page and reports each with its rectangle <b>in PDF user
        /// space</b> — points, origin bottom-left — ready for
        /// <see cref="PdfOcrLayerWriter"/>.
        /// </summary>
        /// <param name="input">The PDF to read. Not disposed.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="dpi">Rasterisation resolution; higher improves recognition.</param>
        /// <returns>
        /// The words in the engine's reading order, an <b>empty list</b> when it ran and found
        /// nothing, or <c>null</c> when it could not run — <see cref="LastOcrError"/> says which.
        /// </returns>
        /// <remarks>
        /// <para><b>Needs an <see cref="IOcrWordPlugin"/>, not just an <see cref="IOcrPlugin"/>.</b>
        /// A text-only engine cannot say where anything is, so there is nothing to place; that is
        /// reported rather than silently treated as "no text".</para>
        /// <para>The order the engine gave is preserved. See
        /// <see cref="PdfOcrLayerWriter"/> for why sorting it would break right-to-left pages.</para>
        /// </remarks>
        public static IReadOnlyList<PdfOcrWord> RecognizeWordsWithOcr(
            Stream input, int pageIndex, int dpi = DefaultOcrDpi)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi), "DPI must be positive.");

            SetLastOcrError(null);
            return RecognizeWordsCore(input, pageIndex, dpi, out _);
        }

        /// <summary>
        /// <see cref="RecognizeWordsWithOcr(Stream, int, int)"/> for an encrypted document.
        /// </summary>
        /// <param name="input">The PDF to read. Not disposed.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="password">User or owner password.</param>
        /// <param name="dpi">Rasterisation resolution.</param>
        /// <remarks>
        /// A scanned document is exactly the kind that gets encrypted before it is filed, so this
        /// is the overload most of them need. It decrypts through the registered
        /// <c>PdfDecryptorHolder</c>, the same route <see cref="RecognizeTextWithOcr(Stream, int, string, int)"/>
        /// takes — reference <c>Tetitulz.Pdf.Encryption</c> and call
        /// <c>PdfEncryptionOperations.Register()</c> once at startup, or the document will not open.
        /// </remarks>
        public static IReadOnlyList<PdfOcrWord> RecognizeWordsWithOcr(
            Stream input, int pageIndex, string password, int dpi = DefaultOcrDpi)
            => WithDecrypted(input, password, s => RecognizeWordsWithOcr(s, pageIndex, dpi));

        /// <summary>
        /// Turns a scanned PDF into a searchable one: every page is rasterised, recognised, and
        /// given an <b>invisible</b> text layer at the recognised word positions. The visible page
        /// is untouched — this adds selectable and searchable text over the scan, it does not
        /// replace it.
        /// </summary>
        /// <param name="input">The PDF to read. Not disposed.</param>
        /// <param name="output">Where the searchable PDF is written. Not disposed.</param>
        /// <param name="dpi">Rasterisation resolution; higher improves recognition and costs time.</param>
        /// <returns>What was written, and one warning per page that produced nothing.</returns>
        /// <remarks>
        /// <para><b>The whole document is written as ONE incremental update</b>, so a 200-page scan
        /// gains one revision rather than 200.</para>
        /// <para><b>A document that recognises nothing is copied verbatim</b> rather than rewritten
        /// with empty layers — and the result says so, so "the scan is blank" and "no engine was
        /// registered" do not look alike.</para>
        /// <para><b>Requires a registered <see cref="IOcrWordPlugin"/></b> — reference
        /// <c>Tetitulz.Pdf.Plugins.Ocr</c> and register it, directly or through
        /// <c>PdfPluginHost.Instance.RegisterFromLoadedAssemblies()</c>.</para>
        /// </remarks>
        public static SearchablePdfResult MakeSearchable(
            Stream input, Stream output, int dpi = DefaultOcrDpi)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi), "DPI must be positive.");

            SetLastOcrError(null);

            var source = Core.IO.SeekableStream.Ensure(input, rewind: true, out bool owned);
            try
            {
                RefuseEncryptedTarget(source);
                var result = new SearchablePdfResult();
                var wordsByPage = new Dictionary<int, IReadOnlyList<PdfOcrWord>>();

                // Every page's size from one load and every page's image through one render session
                // (OcrPages): a one-shot render opens the whole document, and two a page made a scan
                // of many pages cost the square of its length.
                using (var ocr = new OcrPages(source, dpi))
                for (int page = 0; page < ocr.PageCount; page++)
                {
                    result.PagesProcessed++;

                    IReadOnlyList<PdfOcrWord> words = ocr.Recognize(page, clearLastError: false, out string why);
                    if (words == null)
                    {
                        result.WarningList.Add($"page {page}: {why}");
                        continue;
                    }
                    if (words.Count == 0)
                    {
                        result.WarningList.Add($"page {page}: the engine ran and found no text");
                        continue;
                    }

                    wordsByPage[page] = words;
                    result.PagesWithText++;
                    result.WordsWritten += words.Count;
                }

                source.Position = 0;
                PdfOcrLayerWriter.AddLayers(source, wordsByPage, output);
                return result;
            }
            finally
            {
                if (owned) source.Dispose();
            }
        }

        /// <summary>
        /// The shared body: rasterise, recognise, and convert pixel rectangles to PDF user space.
        /// </summary>
        /// <param name="input">The PDF to read; rewound internally per page.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="dpi">Rasterisation resolution.</param>
        /// <param name="reason">
        /// Why no words could be produced. Set whenever the return is <c>null</c>; also mirrored
        /// into <see cref="LastOcrError"/>, which the single-page entry point's callers read.
        /// </param>
        private static IReadOnlyList<PdfOcrWord> RecognizeWordsCore(
            Stream input, int pageIndex, int dpi, out string reason)
        {
            // The engine before the page is read at all, as RecognizeRendered asks it first too.
            if (PdfPluginHost.Instance.FindOcrWordPlugin(
                    Images.OcrSampleBuffer.Components, Images.OcrSampleBuffer.BitsPerComponent) == null)
                return RecognizeRendered(null, pageIndex, dpi, () => null, out reason);

            var size = Rendering.PdfPageRenderer.GetRenderSize(input, pageIndex, dpi);
            return RecognizeRendered(size, pageIndex, dpi, () =>
            {
                input.Position = 0;
                return Rendering.PdfPageRenderer.RenderPage(input, pageIndex, dpi);
            }, out reason);
        }

        /// <summary>
        /// A document's pages recognised one after another: every page's size from one load, and
        /// their images through one render session opened at the first page that needs one. Asked
        /// page by page through the one-shot doors instead, each page opened the whole document two
        /// or three times — a scan of many pages cost the square of its length.
        /// </summary>
        /// <remarks>
        /// The source must be seekable and outlive the reader; it is read, never written or
        /// disposed. <see cref="LastOcrError"/> is cleared at a page's recognition when the caller asks,
        /// as the single-page door clears it.
        /// </remarks>
        internal sealed class OcrPages : IDisposable
        {
            private readonly Stream _source;
            private readonly int _dpi;
            private IReadOnlyList<Rendering.PageRenderSize> _sizes;
            private Rendering.PdfRenderSession _session;

            internal OcrPages(Stream source, int dpi)
            {
                _source = source ?? throw new ArgumentNullException(nameof(source));
                _dpi = dpi;
            }

            /// <summary>Render sessions opened, for tests: one however many pages are recognised.</summary>
            internal int SessionsOpened { get; private set; }

            internal int PageCount => Sizes().Count;

            /// <summary>Page <paramref name="pageIndex"/>'s extent and rotation, or null outside the document.</summary>
            internal Rendering.PageRenderSize Size(int pageIndex)
            {
                var sizes = Sizes();
                return pageIndex >= 0 && pageIndex < sizes.Count ? sizes[pageIndex] : null;
            }

            /// <summary>
            /// The words of page <paramref name="pageIndex"/>, as <see cref="RecognizeWordsWithOcr(Stream, int, int)"/>
            /// answers them — the engine asked before the page is read.
            /// </summary>
            /// <param name="pageIndex">Zero-based page index.</param>
            /// <param name="reason">Why no words could be produced, when the return is <c>null</c>.</param>
            /// <param name="clearLastError">Clear <see cref="LastOcrError"/> first, as the single-page door does;
            /// a whole-document run clears it once, so a page's failure outlives the pages after it.</param>
            internal IReadOnlyList<PdfOcrWord> Recognize(int pageIndex, bool clearLastError, out string reason)
            {
                if (clearLastError) SetLastOcrError(null);
                if (PdfPluginHost.Instance.FindOcrWordPlugin(
                        Images.OcrSampleBuffer.Components, Images.OcrSampleBuffer.BitsPerComponent) == null)
                    return RecognizeRendered(null, pageIndex, _dpi, () => null, out reason);

                return RecognizeRendered(Size(pageIndex), pageIndex, _dpi,
                    () => Session().RenderPage(pageIndex, _dpi), out reason);
            }

            private IReadOnlyList<Rendering.PageRenderSize> Sizes()
            {
                if (_sizes == null)
                {
                    _source.Position = 0;
                    _sizes = Rendering.PdfPageRenderer.GetRenderSizes(_source, _dpi);
                }
                return _sizes;
            }

            private Rendering.PdfRenderSession Session()
            {
                if (_session == null)
                {
                    _source.Position = 0;
                    _session = Rendering.PdfRenderSession.Open(_source);
                    SessionsOpened++;
                }
                return _session;
            }

            public void Dispose() => _session?.Dispose();
        }

        /// <summary>
        /// Recognises page <paramref name="pageIndex"/> of extent <paramref name="size"/>, its image
        /// drawn by <paramref name="render"/> — the one-shot renderer for a single page, a render
        /// session for a whole document.
        /// </summary>
        private static IReadOnlyList<PdfOcrWord> RecognizeRendered(
            Rendering.PageRenderSize size, int pageIndex, int dpi, Func<byte[]> render, out string reason)
        {
            reason = null;

            // The component count, the bit depth, the size test and the packing are one decision,
            // in Images.OcrSampleBuffer, shared with RecognizeTextWithOcr — the constants a plugin
            // is SELECTED by have to be the constants its bytes are WRITTEN under.
            const int components = Images.OcrSampleBuffer.Components;
            const int bitsPerComponent = Images.OcrSampleBuffer.BitsPerComponent;

            // The engine first: it needs nothing from the render, and without one every page of a
            // scan was rasterised, encoded and decoded to learn that nothing could read it.
            var plugin = PdfPluginHost.Instance.FindOcrWordPlugin(components, bitsPerComponent);
            if (plugin == null)
                return Fail(out reason,
                    $"no registered IOcrWordPlugin accepts {components} components at " +
                    $"{bitsPerComponent} bits, so no engine was consulted. Reference " +
                    "Tetitulz.Pdf.Plugins.Ocr and register it (directly, or via " +
                    "PdfPluginHost.Instance.RegisterFromLoadedAssemblies()).");

            // The extent is the page's in POINTS. Its pixels cannot say: the renderer rounds every
            // axis up to at least one pixel, so a zero-area page measured as 1x1 pixels, reached
            // the engine, and came back as words of no width at the origin.
            if (size == null || !(size.WidthPoints > 0) || !(size.HeightPoints > 0))
                return Fail(out reason, $"page {pageIndex} has no measurable extent, so it was not recognised");

            byte[] png = render();
            if (png == null || png.Length == 0)
                return Fail(out reason, $"page {pageIndex} rendered no image data, so nothing was recognised");

            using var bitmap = SkiaSharp.SKBitmap.Decode(png);
            if (bitmap == null)
                return Fail(out reason,
                    $"the rendered image of page {pageIndex} could not be decoded, so nothing was recognised");

            byte[] pixels = Images.OcrSampleBuffer.TryPack(bitmap, out long required);
            if (pixels == null)
                return Fail(out reason,
                    $"page {pageIndex} is {bitmap.Width}x{bitmap.Height} at {dpi} dpi, whose " +
                    $"{components}-component buffer needs {required} bytes and cannot be " +
                    "allocated; recognise at a lower dpi");

            IReadOnlyList<OcrWordBox> boxes =
                plugin.RecognizeWords(pixels, bitmap.Width, bitmap.Height, components);
            if (boxes == null)
                return Fail(out reason, $"{plugin.Name} ran over page {pageIndex} and returned no result");

            // The engine saw the ROTATED image; the layer is written in UNROTATED user space. Undo
            // the renderer's rotation first, then apply the one mapping from engine pixels to user space.
            // The unrotated pixel extent is the image's, transposed for a quarter turn.
            bool quarterTurn = size.Rotation == 90 || size.Rotation == 270;
            int rawWidth = quarterTurn ? bitmap.Height : bitmap.Width;
            int rawHeight = quarterTurn ? bitmap.Width : bitmap.Height;

            return ToUserSpace(
                UnrotateBoxes(boxes, size.Rotation, rawWidth, rawHeight),
                rawWidth, rawHeight, size.WidthPoints, size.HeightPoints);
        }

        private static IReadOnlyList<PdfOcrWord> Fail(out string reason, string message)
        {
            reason = message;
            SetLastOcrError(message);
            return null;
        }

        /// <summary>
        /// Undoes the renderer's <c>/Rotate</c>, taking boxes in rendered-image pixels to boxes in
        /// the pixels of the same page rendered unrotated.
        /// </summary>
        /// <param name="boxes">What the engine reported, in rendered-image pixels.</param>
        /// <param name="rotation">The page's <c>/Rotate</c>, normalised to 0, 90, 180 or 270.</param>
        /// <param name="rawWidth">Unrotated pixel width — the image's, transposed for a quarter turn.</param>
        /// <param name="rawHeight">Unrotated pixel height.</param>
        /// <remarks>
        /// <para><b>This is the inverse of the renderer's own rotation, not a fresh derivation.</b>
        /// <c>PdfPageRenderer</c> composes <c>Translate(out/2) · RotateDegrees(r) · Translate(-raw/2)</c>
        /// and blits the unrotated bitmap through it; what follows inverts exactly that. Deriving a
        /// rotation independently is how two halves of one transform end up transposed relative to
        /// each other, and a text layer that is wrong in that way is invisible — it looks correct
        /// until somebody selects a word.</para>
        ///
        /// <para><b>The box's axes swap on a quarter turn</b>, so a word that is wide in the image
        /// is tall in unrotated space. Mapping the corners and taking their extent gets that right
        /// without a case for it; mapping the origin and keeping the width and height does not.</para>
        /// </remarks>
        internal static IReadOnlyList<OcrWordBox> UnrotateBoxes(
            IReadOnlyList<OcrWordBox> boxes, int rotation, int rawWidth, int rawHeight)
        {
            if (rotation == 0) return boxes;

            var undone = new List<OcrWordBox>(boxes.Count);
            foreach (var b in boxes)
            {
                if (b == null) continue;

                var (x0, y0) = Unrotate(b.X, b.Y, rotation, rawWidth, rawHeight);
                var (x1, y1) = Unrotate(b.X + b.Width, b.Y + b.Height, rotation, rawWidth, rawHeight);

                undone.Add(new OcrWordBox(
                    b.Text,
                    Math.Min(x0, x1), Math.Min(y0, y1),
                    Math.Abs(x1 - x0), Math.Abs(y1 - y0),
                    b.Confidence));
            }

            return undone;
        }

        /// <summary>One rendered-image pixel as the unrotated-image pixel it came from.</summary>
        private static (float X, float Y) Unrotate(
            float x, float y, int rotation, int rawWidth, int rawHeight)
        {
            switch (rotation)
            {
                case 90:  return (y, rawHeight - x);
                case 180: return (rawWidth - x, rawHeight - y);
                case 270: return (rawWidth - y, x);
                default:  return (x, y);
            }
        }

        /// <summary>
        /// Converts recognised rectangles from image pixels (top-left origin, y down) to PDF user
        /// space (points, origin bottom-left).
        /// </summary>
        /// <remarks>
        /// <para><b>The scale comes from the image the engine actually saw, not from the dpi
        /// asked for.</b> A page whose pixel extent would exceed the renderer's safety limits is
        /// rasterised at a lower effective resolution, so <c>points / pixels</c> is the only ratio
        /// that is right for every page; computing <c>72 / dpi</c> instead places every word on
        /// such a page proportionally wrong, and nothing visible reveals it.</para>
        /// <para>The y flip takes the box's <b>bottom</b> edge — <c>y + height</c> in a
        /// top-left-origin space — because PDF positions text by its baseline-ish origin at the
        /// bottom left. Flipping the top edge instead puts every word one line-height too high.</para>
        /// </remarks>
        internal static IReadOnlyList<PdfOcrWord> ToUserSpace(
            IReadOnlyList<OcrWordBox> boxes, int imageWidthPx, int imageHeightPx,
            float pageWidthPt, float pageHeightPt)
        {
            var words = new List<PdfOcrWord>(boxes.Count);
            if (imageWidthPx <= 0 || imageHeightPx <= 0) return words;

            float sx = pageWidthPt / imageWidthPx;
            float sy = pageHeightPt / imageHeightPx;

            foreach (var b in boxes)
            {
                if (b == null || string.IsNullOrEmpty(b.Text)) continue;
                words.Add(new PdfOcrWord(
                    b.Text,
                    b.X * sx,
                    pageHeightPt - (b.Y + b.Height) * sy,
                    b.Width * sx,
                    b.Height * sy));
            }
            return words;
        }

        /// <summary>
        /// Refuses to write a text layer into an encrypted document.
        /// </summary>
        /// <remarks>
        /// The layer's strings and content stream would be written in <b>clear text</b> where the
        /// reader expects per-object ciphertext, so it would extract as noise — a document that
        /// reports a searchable text layer and cannot be searched. This assembly cannot encrypt, so
        /// the honest answer is to refuse; the same rule, and the same wording, as
        /// <c>PdfFormFiller.Fill</c>.
        /// <para>Recognising words from an encrypted document is a different question and is
        /// supported — see the password overload of
        /// <see cref="RecognizeWordsWithOcr(Stream, int, string, int)"/>. A caller can recognise
        /// there and write the layer through the encryption assembly's revision writer.</para>
        /// </remarks>
        private static void RefuseEncryptedTarget(Stream source)
        {
            source.Position = 0;
            using var doc = Core.Document.PdfDocument.Open(source);
            if (!doc.IsEncrypted) return;

            throw new Core.PdfException(
                "Cannot write an OCR text layer into an encrypted document here: the layer would " +
                "be written in clear text where the reader expects per-object ciphertext, and this " +
                "assembly cannot encrypt. Recognise the words with the password overload of " +
                "RecognizeWordsWithOcr and write the revision through Tetitulz.Pdf.Encryption.");
        }

        /// <summary>
        /// Writes <paramref name="message"/> to the OCR diagnostic slot the text-recognition entry
        /// point already publishes, so a caller has one place to read a reason from.
        /// </summary>
        private static void SetLastOcrError(string message) => _lastOcrError = message;
    }
}
