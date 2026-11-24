// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Util;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Pure C# PDF page rasterizer built on SkiaSharp.
    ///
    /// <para>
    /// Renders a PDF page to a PNG or JPEG image without any native PDF library
    /// dependency (no native PDF engine).  The renderer implements the PDF content
    /// stream interpreter from scratch, handling:
    /// <list type="bullet">
    ///   <item>Path construction and painting (fill, stroke, clip)</item>
    ///   <item>Text rendering with embedded TrueType/OpenType fonts or Standard-14 substitutes</item>
    ///   <item>Image XObjects (JPEG, PNG, raw RGB/Gray/CMYK pixel streams)</item>
    ///   <item>Form XObjects (recursed)</item>
    ///   <item>Color spaces: DeviceGray, DeviceRGB, DeviceCMYK</item>
    ///   <item>Graphics state: CTM, line style, q/Q stack</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class PdfPageRenderer
    {
        /// <summary>
        /// Default JPEG quality used by the <c>*AsJpg</c> overloads when no explicit
        /// quality is supplied.  85 is the encoder's visual-lossless threshold for natural images.
        /// </summary>
        /// <remarks>
        /// <para><b>JPEG does not keep "files ~4–6× smaller than PNG" for the pages this renderer
        /// produces.</b> Measured on a
        /// 1275x1650 coloured-text page at 150 dpi: JPEG at this quality is <b>399 KB</b> against
        /// <b>280 KB</b> for PNG at <see cref="PngCompression.Fast"/> and <b>32 KB</b> for
        /// <see cref="PdfImageFormat.WebpLossless"/> — so the lossy format is 1.4× larger than
        /// lossless PNG and 12× larger than lossless WebP. That figure is true of photographs,
        /// and a rendered page is mostly white with hard-edged text, which is the shape JPEG is
        /// worst at.</para>
        /// <para>JPEG remains the right choice for a scanned or photographic page. For anything
        /// else prefer <see cref="PdfImageFormat.WebpLossless"/> or PNG, and if JPEG is required
        /// for compatibility, set <see cref="PdfRenderOptions.JpegSubsampling"/> to
        /// <see cref="JpegSubsampling.Chroma444"/> — the default 4:2:0 measured a worst-case
        /// channel error of 190 on coloured text.</para>
        /// </remarks>
        public const int DefaultJpegQuality = 85;

        /// <summary>
        /// Default output resolution, in dots per inch, for every render overload that does not
        /// take an explicit <c>dpi</c>.  150&#160;dpi is roughly twice the PDF user-space unit
        /// (<see cref="Core.Constants.PdfUnits.PointsPerInch"/>), which reads cleanly on screen
        /// without quadrupling the bitmap the way 300&#160;dpi would.
        /// </summary>
        internal const int DefaultDpi = 150;

        /// <summary>
        /// Maximum single-axis pixel dimension for a rendered page bitmap.  Also the
        /// largest texture dimension SkiaSharp accepts on most backends.
        /// </summary>
        internal static int MaxPixelDimension => PdfLimits.MaxImageDimension;

        /// <summary>
        /// Maximum total pixel area for a rendered page bitmap.  The per-axis cap alone
        /// still permits 16384×16384×4 ≈ 1 GiB (doubled by the /Rotate path) from a single
        /// malformed /MediaBox.  Bounding the area to 100 MP keeps a 4-byte RGBA bitmap
        /// under ~400 MB (~800 MB peak through the rotation path) while leaving legitimate
        /// large-format renders intact (e.g. A3 at 600 DPI ≈ 70 MP).  Mirrors the area cap
        /// in PdfOptimizer.LoadBitmapFromRawPixels.
        /// </summary>
        internal static long MaxPixelArea => PdfLimits.MaxImagePixelArea;

        /// <summary>
        /// Scales <paramref name="width"/>/<paramref name="height"/> (and the render
        /// <paramref name="scale"/>) down proportionally so neither axis exceeds
        /// <see cref="MaxPixelDimension"/> and the total area stays within
        /// <see cref="MaxPixelArea"/>.  Aspect ratio is preserved, so an absurd page box
        /// yields a lower-resolution image instead of an out-of-memory allocation.
        /// </summary>
        /// <remarks>
        /// <b>A reduction is reported through
        /// <see cref="RenderDiagnosticKind.RenderResolutionReduced"/>.</b> The clamp is correct; doing
        /// it silently would not be. A caller asking for A4 at 1200 DPI (139 MP)
        /// receives ≈1017 DPI, and a reduced-resolution PNG is still a valid non-empty PNG — so
        /// without the report there is no way to tell.
        /// </remarks>
        internal static void ClampRenderDimensions(ref int width, ref int height, ref float scale)
        {
            int requestedWidth = width, requestedHeight = height;
            float requestedScale = scale;

            // Per-axis clamp.
            if (width > MaxPixelDimension || height > MaxPixelDimension)
            {
                float axisScale = Math.Min((float)MaxPixelDimension / width,
                                           (float)MaxPixelDimension / height);
                width  = Math.Max(1, SaturatingCast.ToInt(width  * axisScale));
                height = Math.Max(1, SaturatingCast.ToInt(height * axisScale));
                scale *= axisScale;
            }
            // Total-area clamp.
            if ((long)width * height > MaxPixelArea)
            {
                float areaScale = (float)Math.Sqrt((double)MaxPixelArea / ((double)width * height));
                width  = Math.Max(1, SaturatingCast.ToInt(width  * areaScale));
                height = Math.Max(1, SaturatingCast.ToInt(height * areaScale));
                scale *= areaScale;
            }

            // Compared on the dimensions rather than on which branch ran: the area clamp can leave
            // a dimension unchanged, and Math.Max(1, …) can leave one unchanged at absurd inputs.
            if (width != requestedWidth || height != requestedHeight)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.RenderResolutionReduced,
                    $"requested {requestedWidth}x{requestedHeight} px " +
                    $"(scale {requestedScale.ToString("0.###", CultureInfo.InvariantCulture)}), " +
                    $"rendered {width}x{height} px " +
                    $"(scale {scale.ToString("0.###", CultureInfo.InvariantCulture)}); " +
                    $"limits are {MaxPixelDimension} px per axis and {MaxPixelArea} px total area");
            }
        }

        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>
        /// Renders the specified page of a PDF file to a PNG image.
        /// </summary>
        /// <param name="inputPath">Path to the PDF file.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="dpi">Output resolution in dots per inch (default <see cref="DefaultDpi"/>).</param>
        /// <returns>PNG-encoded image bytes.</returns>
        public static byte[] RenderPage(string inputPath, int pageIndex, int dpi = DefaultDpi)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return RenderPage(fs, pageIndex, dpi);
        }

        /// <summary>
        /// Renders the specified page of a PDF file to a PNG image with
        /// explicit rendering options (Screen vs Print, box selection).
        /// </summary>
        public static byte[] RenderPage(string inputPath, int pageIndex, int dpi, PdfRenderOptions options)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return RenderPage(fs, pageIndex, dpi, options);
        }

        /// <summary>
        /// Renders the specified page of a PDF stream to a PNG image.
        /// </summary>
        /// <param name="input">PDF data stream (seekable or will be buffered).</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="dpi">Output resolution in dots per inch (default <see cref="DefaultDpi"/>).</param>
        /// <returns>PNG-encoded image bytes.</returns>
        public static byte[] RenderPage(Stream input, int pageIndex, int dpi = DefaultDpi)
            => RenderPage(input, pageIndex, dpi, PdfRenderOptions.Default);

        /// <summary>
        /// Returns the PDF bytes decrypted with <paramref name="password"/> (empty string by
        /// default).  If the PDF is not encrypted the original array is returned unchanged.
        /// Throws if the PDF is encrypted but the password is wrong.
        /// </summary>
        public static byte[] EnsureDecrypted(byte[] pdfBytes, string password = "")
        {
            if (pdfBytes == null) throw new ArgumentNullException(nameof(pdfBytes));

            // Stops at the trailer rather than building a whole PdfDocument to read one boolean —
            // the duplicate full load that HasEncryptEntry exists to avoid, which the stream paths
            // stopped doing and this one kept.
            bool isEncrypted;
            using (var probe = new MemoryStream(pdfBytes, writable: false))
                isEncrypted = HasEncryptEntry(probe);
            if (!isEncrypted) return pdfBytes;
            if (PdfDecryptorHolder.Current == null) return pdfBytes;

            var output = new MemoryStream();
            using (var src = new MemoryStream(pdfBytes, writable: false))
                PdfDecryptorHolder.Current.Decrypt(src, output, password);
            return output.ToArray();
        }

        /// <summary>
        /// Returns a stream of decrypted bytes when <paramref name="seekable"/> holds an encrypted
        /// document, else <paramref name="seekable"/> itself. <paramref name="ownsResult"/> is true
        /// when the caller must dispose the returned stream.
        /// </summary>
        /// <remarks>
        /// <para><b>Every stream entry point must go through this.</b> Decryption inline in one path
        /// only would let <see cref="RenderPageAsJpg(Stream,int,int,int,PdfRenderOptions)"/>, the
        /// <c>RenderAllPages*</c> family and the tile paths render still-encrypted content streams —
        /// the same file producing a correct PNG and
        /// a blank/garbled JPEG.</para>
        /// <para>A wrong password throws out of the decryptor rather than silently rendering
        /// nothing, which is why the result is never "fall back to the encrypted bytes" once a
        /// decryptor is present.</para>
        /// </remarks>
        /// <remarks>
        /// <b>Internal, not private, because <see cref="PdfSvgRenderer"/> is a stream entry point
        /// too</b> and did not go through it: an encrypted document exported to SVG could not read
        /// its own page tree and threw "document has 0 page(s)", reporting an encrypted file as an
        /// empty one. One decryption decision, both renderers — a second copy is how the two drift.
        /// </remarks>
        internal static Stream EnsureDecryptedStream(Stream seekable, string password, out bool ownsResult)
            => EnsureDecryptedStream(seekable, password, out ownsResult, out _);

        /// <summary>
        /// As above, and additionally reports through <paramref name="stillEncrypted"/> that the
        /// returned stream is <b>the encrypted original</b> because no <c>IPdfDecryptor</c> was
        /// registered.
        /// </summary>
        /// <remarks>
        /// <para><c>ownsResult == false</c> cannot answer that question: it is equally true of an
        /// unencrypted document, which is why the callers that need to tell the two apart could not.
        /// A reader handed a still-encrypted stream parses an unreadable catalog and returns
        /// "nothing found" — indistinguishable from a document that genuinely has no bookmarks, no
        /// links and no metadata. <c>PdfOperations.WithDecrypted</c> uses this to say which it
        /// was.</para>
        /// <para>The renderers deliberately keep the three-argument form: with no decryptor present
        /// they draw whatever the still-encrypted content streams yield, which is the documented
        /// pre-existing behaviour on <see cref="PdfRenderOptions.Password"/>.</para>
        /// </remarks>
        internal static Stream EnsureDecryptedStream(Stream seekable, string password,
            out bool ownsResult, out bool stillEncrypted)
        {
            ownsResult = false;

            seekable.Position = 0;
            bool encrypted = HasEncryptEntry(seekable);

            seekable.Position = 0;
            if (!encrypted || PdfDecryptorHolder.Current == null)
            {
                stillEncrypted = encrypted;
                return seekable;
            }

            var decrypted = new MemoryStream();
            PdfDecryptorHolder.Current.Decrypt(seekable, decrypted, password ?? string.Empty);
            decrypted.Position = 0;
            ownsResult = true;
            stillEncrypted = false;
            return decrypted;
        }

        /// <summary>
        /// Whether the document's trailer carries an <c>/Encrypt</c> entry — the exact rule
        /// <see cref="PdfDocument.IsEncrypted"/> applies, reached without building a document.
        /// </summary>
        /// <remarks>
        /// <para>Not <c>PdfDocument.Open(seekable)</c> to read one boolean, which with every caller
        /// then opening the document for real would be two full loads per render. Stopping at the
        /// trailer skips resolving the catalog, the <c>/Info</c> dictionary and
        /// the AcroForm.</para>
        /// <para><b>The win is smaller than it looks</b>, and the measurement is worth recording
        /// so nobody re-litigates it: the bulk of a load is <c>ParseCrossReferences</c>, which
        /// still runs here. A full parse of a 50-page document is ~345 µs against a ~116 ms
        /// render, so the whole duplicate probe was under 1% of a render — a real waste, but not
        /// the reason rendering was slow. That was PNG encoding; see
        /// <see cref="PngCompression"/>.</para>
        /// <para>Failure behaviour is unchanged: a file whose header or cross-reference table
        /// cannot be parsed throws here exactly as it did through <c>Open</c>. A file that parses
        /// but has no usable catalog now throws slightly later, from the caller's own
        /// <c>Open</c>, with the same exception.</para>
        /// </remarks>
        private static bool HasEncryptEntry(Stream seekable)
        {
            var parser = new Core.Parsing.PdfParser(seekable);
            parser.ParseHeader();
            parser.ParseCrossReferences();
            return parser.Trailer != null && parser.Trailer.ContainsKey(PdfName.Encrypt);
        }

        /// <summary>
        /// Byte-array counterpart of <see cref="EnsureDecryptedStream(Stream,string,out bool)"/>, for the buffer-once
        /// <c>RenderAllPages*</c> paths.
        /// </summary>
        private static byte[] EnsureDecryptedBytes(byte[] pdfBytes, string password)
            => EnsureDecrypted(pdfBytes, password ?? string.Empty);

        /// <summary>
        /// Opens <paramref name="input"/> as a decrypted <see cref="PdfDocument"/>, runs
        /// <paramref name="body"/> over it, and releases everything this call owns.
        /// </summary>
        /// <remarks>
        /// <para><b>The single stream prologue.</b> Seek-normalise, decrypt, open, dispose in the
        /// right order — ten entry points in this file open-coded that sequence and
        /// <see cref="PdfSvgRenderer"/> two more. Two of the twelve had already drifted: the SVG
        /// pair disposed the decrypted stream but never the buffer <c>EnsureSeekable</c> allocates
        /// for a non-seekable input, leaking it on every export from a network or compressed
        /// stream.</para>
        /// <para>Ownership is the part that keeps being got wrong, so it is stated once here:
        /// <c>EnsureSeekable</c> returns the caller's own stream when it is already seekable and a
        /// fresh buffer otherwise, and <see cref="EnsureDecryptedStream(Stream,string,out bool)"/> returns its argument
        /// unchanged for an unencrypted document. Both are therefore disposed conditionally — the
        /// reference test, never the flag alone, because a non-seekable <i>and</i> encrypted input
        /// produces two distinct owned streams.</para>
        /// </remarks>
        internal static T WithDecryptedDocument<T>(Stream input, string password,
            Func<PdfDocument, T> body)
        {
            Stream seekable = Tetitulz.Pdf.Core.IO.SeekableStream.Ensure(input, rewind: true, out bool ownsSeekable);
            Stream source   = EnsureDecryptedStream(seekable, password, out bool ownsSource);
            try
            {
                using var doc = PdfDocument.Open(source);
                return body(doc);
            }
            finally
            {
                if (ownsSource) source.Dispose();
                if (ownsSeekable) seekable.Dispose();
            }
        }

        /// <summary>
        /// <see cref="WithDecryptedDocument{T}"/> for one page, with the range check applied.
        /// </summary>
        internal static T WithDecryptedPage<T>(Stream input, int pageIndex, string password,
            Func<PdfPage, PdfDocument, T> body)
            => WithDecryptedDocument(input, password, doc =>
            {
                ValidatePageIndex(pageIndex, doc);
                return body(doc.Pages[pageIndex], doc);
            });

        /// <summary>
        /// Returns the number of pages in a PDF byte array, transparently decrypting
        /// with an empty password when the file is encrypted.
        /// </summary>
        public static int GetPageCount(byte[] pdfBytes)
        {
            if (pdfBytes == null) throw new ArgumentNullException(nameof(pdfBytes));
            byte[] src = EnsureDecrypted(pdfBytes);
            using var ms  = new MemoryStream(src, writable: false);
            using var doc = PdfDocument.Open(ms);
            return doc.Pages.Count;
        }

        /// <summary>
        /// Renders the specified page of a PDF stream to a PNG image with
        /// explicit rendering options.  Encrypted PDFs with an empty user password
        /// are decrypted transparently.
        /// </summary>
        public static byte[] RenderPage(Stream input, int pageIndex, int dpi, PdfRenderOptions options)
            => RenderPage(input, pageIndex, dpi, options, System.Threading.CancellationToken.None);

        /// <summary>
        /// Renders the specified page of a PDF stream to a PNG image, with cancellation.
        /// </summary>
        /// <remarks>
        /// The token is checked inside the content-stream loop, not only before the render starts,
        /// so a single very large page can be abandoned. A cancelled render throws
        /// <see cref="OperationCanceledException"/> and returns nothing — there is no partial image.
        /// </remarks>
        public static byte[] RenderPage(Stream input, int pageIndex, int dpi,
            PdfRenderOptions options, System.Threading.CancellationToken cancellationToken)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (dpi   <= 0)   throw new ArgumentOutOfRangeException(nameof(dpi), "DPI must be positive.");

            return WithDecryptedPage(input, pageIndex, options?.Password,
                (page, doc) => RenderPage(page, doc, dpi, options, null, cancellationToken));
        }

        /// <summary>
        /// The tile rectangle every tile entry point accepts (the one-shot entries here and
        /// <see cref="PdfRenderSession"/>'s): finite on all four sides, and a positive extent.
        /// </summary>
        /// <remarks>
        /// The four sites tested <c>clipW &lt;= 0</c> alone, which NaN passes: a NaN extent reached
        /// <c>(int)Math.Ceiling(NaN)</c> and <c>Math.Max(1, …)</c> as a 1×1 tile, a NaN origin went
        /// into the shift matrix and painted nothing — a blank image for an argument error, where
        /// a zero extent throws.
        /// </remarks>
        internal static void ValidateClip(float clipX, float clipY, float clipW, float clipH)
        {
            NumericArguments.RequireFinite(clipX, nameof(clipX));
            NumericArguments.RequireFinite(clipY, nameof(clipY));
            NumericArguments.RequireFinite(clipW, nameof(clipW));
            NumericArguments.RequireFinite(clipH, nameof(clipH));
            if (clipW <= 0 || clipH <= 0) throw new ArgumentOutOfRangeException(nameof(clipW), "Clip dimensions must be positive.");
        }

        /// <summary>
        /// The one page-index range check, so every entry point reports the same way. Several used
        /// to throw a bare <see cref="ArgumentOutOfRangeException"/> with no page count in it.
        /// </summary>
        private static void ValidatePageIndex(int pageIndex, PdfDocument doc)
        {
            if (pageIndex < 0 || pageIndex >= doc.Pages.Count)
                throw new ArgumentOutOfRangeException(nameof(pageIndex),
                    $"Page index {pageIndex} is out of range (document has {doc.Pages.Count} page(s)).");
        }

        /// <summary>
        /// Renders every page of a PDF file to PNG, honouring an external cancellation token so
        /// hosted environments (ASP.NET request abort, IHostedService shutdown) can break out of a
        /// long render.
        /// </summary>
        /// <remarks>
        /// Cancellation is checked at page boundaries <b>and inside the content-stream loop</b>, so
        /// a single enormous page does not run to completion after the token trips. Partial
        /// bitmaps in flight are discarded.
        /// </remarks>
        public static byte[][] RenderAllPages(string inputPath, int dpi,
            System.Threading.CancellationToken cancellationToken,
            PdfRenderOptions renderOptions = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            cancellationToken.ThrowIfCancellationRequested();

            // Decrypt before the page count is read: an encrypted document must be resolved
            // once, up front, or every worker below renders encrypted content streams.
            byte[] pdfBytes = EnsureDecryptedBytes(File.ReadAllBytes(inputPath), renderOptions?.Password);
            return RenderAllPagesCore(pdfBytes, dpi, PdfImageFormat.Png, DefaultJpegQuality,
                renderOptions, cancellationToken, sink: null);
        }

        /// <summary>Renders every page of a PDF file to PNG.</summary>
        public static byte[][] RenderAllPages(string inputPath, int dpi = DefaultDpi,
            PdfRenderOptions renderOptions = null)
            => RenderAllPages(inputPath, dpi, System.Threading.CancellationToken.None, renderOptions);

        /// <summary>
        /// Renders every page of a PDF stream and returns each page as PNG bytes.
        /// </summary>
        /// <remarks>
        /// <para><b>This is parallel, and each worker owns its own document and caches</b> — see
        /// <see cref="RenderAllPagesCore"/>, which every batch entry point here shares.</para>
        /// <para><b>One document and state per worker</b>: one <see cref="PdfDocument"/> and one
        /// <see cref="PdfRenderSession.SharedState"/> shared across every page cannot be touched by
        /// two threads because the parser's object cache and those caches are plain
        /// dictionaries. That is no reason to render on one thread, only a reason not to share
        /// <i>one</i> state across many. Giving each worker its own state keeps the sharing win
        /// within a worker and takes the parallelism too: measured on a 50-page document, 1089 ms
        /// sequential against 174 ms across 12 workers, and 301 ms re-opening the document per
        /// <i>page</i> on four threads.</para>
        /// <para><b>Two consequences of running off the calling thread, neither hidden.</b>
        /// <see cref="RenderDiagnostics"/> events arrive on worker threads, and the
        /// <c>[ThreadStatic]</c> <c>Last*</c> diagnostic slots a caller might read afterwards are
        /// written on those threads rather than this one. A host that needs either — or that wants
        /// a deterministic single-threaded profile — sets
        /// <see cref="PdfRenderOptions.MaxRenderThreads"/> to 1, which renders on the calling
        /// thread with one shared state.</para>
        /// <para><b>Memory is proportional to the worker count.</b> Each worker retains a parsed
        /// document, every typeface its pages resolved, and up to
        /// <see cref="ContentOperationCache.MaxBytes"/> of parsed operations, until the batch
        /// finishes. A caller that needs a tight ceiling renders page by page through
        /// <see cref="RenderPage(Stream,int,int,PdfRenderOptions)"/>, or caps
        /// <see cref="PdfRenderOptions.MaxRenderThreads"/>.</para>
        /// </remarks>
        public static byte[][] RenderAllPages(Stream input, int dpi = DefaultDpi,
            PdfRenderOptions renderOptions = null)
            => RenderAllPages(input, dpi, System.Threading.CancellationToken.None, renderOptions);

        /// <summary>
        /// Renders every page of a PDF stream to PNG, with cancellation.
        /// </summary>
        public static byte[][] RenderAllPages(Stream input, int dpi,
            System.Threading.CancellationToken cancellationToken,
            PdfRenderOptions renderOptions = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (dpi   <= 0)   throw new ArgumentOutOfRangeException(nameof(dpi));

            return RenderAllPagesCore(BufferPdf(input, renderOptions), dpi, PdfImageFormat.Png,
                DefaultJpegQuality, renderOptions, cancellationToken, sink: null);
        }

        // ----------------------------------------------------------------
        //  Batch core — one implementation behind every RenderAllPages* entry point
        // ----------------------------------------------------------------

        /// <summary>
        /// Buffers a PDF stream into a decrypted byte array, disposing the seekable copy if one had
        /// to be made.
        /// </summary>
        /// <remarks>
        /// <b>The disposal is the point.</b> <c>ReadAllBytes(PdfHelpers.EnsureSeekable(input))</c>
        /// inline would copy a non-seekable stream into a fresh <see cref="MemoryStream"/> never
        /// disposed, leaving a second full copy
        /// of the file alive until a collection. The single-page paths always got this right; the
        /// batch ones did not, and one shared helper is what stops them differing again.
        /// </remarks>
        private static byte[] BufferPdf(Stream input, PdfRenderOptions options)
        {
            Stream seekable = Tetitulz.Pdf.Core.IO.SeekableStream.Ensure(input, rewind: true, out bool ownsSeekable);
            try
            {
                return EnsureDecryptedBytes(ReadAllBytes(seekable), options?.Password);
            }
            finally
            {
                if (ownsSeekable) seekable.Dispose();
            }
        }

        /// <summary>
        /// How many pages to render concurrently.
        /// </summary>
        /// <remarks>
        /// <para><b>Not <c>Math.Min(ProcessorCount, 4)</c>: the 4 costs 1.7×.</b>
        /// Measured on a 12-core machine over the same 50-page document: 804 ms at one worker,
        /// 403 at two, 301 at four, 186 at eight, 174 at twelve. Nothing in the render path
        /// contends — each worker owns its document, caches and bitmap — so the ceiling was pure
        /// loss on any machine with more than four cores.</para>
        /// <para>Never more workers than pages: a two-page document gains nothing from twelve
        /// documents parsed in parallel, and each one costs a full parse.</para>
        /// </remarks>
        internal static int RenderDegree(PdfRenderOptions options, int pageCount)
        {
            int requested = options?.MaxRenderThreads ?? 0;
            // A negative value is refused, never silently reinterpreted as "use the default", the
            // one reading a caller who typed it cannot have meant. 0 keeps its documented meaning.
            if (requested < 0)
                throw new ArgumentOutOfRangeException(nameof(PdfRenderOptions.MaxRenderThreads),
                    requested, "MaxRenderThreads must be 0 (choose automatically) or positive.");
            int degree    = requested > 0 ? requested : Environment.ProcessorCount;
            return Math.Max(1, Math.Min(degree, Math.Max(1, pageCount)));
        }

        /// <summary>
        /// Renders every page of an already-decrypted document to <paramref name="format"/>.
        /// </summary>
        /// <param name="pdfBytes">The complete, already-decrypted PDF file.</param>
        /// <param name="dpi">Output resolution in dots per inch.</param>
        /// <param name="format">Encoded output format for every page.</param>
        /// <param name="quality">
        /// Encoder quality, 1–100, for <see cref="PdfImageFormat.Jpeg"/> and the lossy WebP form.
        /// Ignored by the lossless formats.
        /// </param>
        /// <param name="options">
        /// Render options, or <see langword="null"/> for <see cref="PdfRenderOptions.Default"/>.
        /// <see cref="PdfRenderOptions.MaxRenderThreads"/> is read from here.
        /// </param>
        /// <param name="cancellationToken">Observed once per page, before that page is rendered.</param>
        /// <param name="sink">
        /// When non-null, each page's bytes are handed over as it finishes and <b>not</b> retained
        /// in the returned array — which is how a caller renders a thousand-page document without
        /// holding a thousand images. Invoked under a lock, so the callback need not be
        /// thread-safe, and page order is <b>not</b> the completion order.
        /// </param>
        /// <remarks>
        /// <para><b>Per-worker state, not per-page and not one for all.</b> Each worker opens its
        /// own <see cref="PdfDocument"/> and builds one <see cref="PdfRenderSession.SharedState"/>,
        /// so an embedded font is decoded once per worker rather than once per page, and no
        /// dictionary is touched by two threads. The two shapes this replaces each had one half of
        /// that: the file-path overloads re-opened the document per page, and the stream overloads
        /// shared one state across every page on one thread.</para>
        /// <para><b>One worker means the calling thread</b>, with no <c>Parallel.For</c> at all —
        /// so <see cref="PdfRenderOptions.MaxRenderThreads"/> = 1 restores thread-affine
        /// diagnostics exactly, rather than approximately.</para>
        /// </remarks>
        internal static byte[][] RenderAllPagesCore(byte[] pdfBytes, int dpi, PdfImageFormat format,
            int quality, PdfRenderOptions options,
            System.Threading.CancellationToken cancellationToken, Action<int, byte[]> sink)
        {
            if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi));
            cancellationToken.ThrowIfCancellationRequested();

            int pageCount;
            using (var probe = new MemoryStream(pdfBytes, writable: false))
            using (var doc   = PdfDocument.Open(probe))
                pageCount = doc.Pages.Count;

            var results = new byte[pageCount][];
            if (pageCount == 0) return results;

            int degree = RenderDegree(options, pageCount);
            if (degree == 1)
            {
                RenderPageRange(pdfBytes, 0, pageCount, null, dpi, format, quality, options,
                    cancellationToken, results, sink, gate: null);
                return results;
            }

            var gate = new object();
            var parallelOptions = new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = degree,
                CancellationToken      = cancellationToken,
            };

            // localInit/localFinally give one worker one document and one set of caches for every
            // page it happens to take, which is the whole point — a plain body lambda would rebuild
            // both per page.
            System.Threading.Tasks.Parallel.For(0, pageCount, parallelOptions,
                () => new RenderWorker(pdfBytes, decodeOnCallingThread: true),
                (i, _, worker) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] bytes = RenderOne(worker.Document, i, dpi, format, quality, options,
                                             worker.Shared, cancellationToken);
                    if (sink == null) results[i] = bytes;
                    else lock (gate) sink(i, bytes);
                    return worker;
                },
                worker => worker.Dispose());

            return results;
        }

        /// <summary>Renders a contiguous run of pages on the calling thread, sharing one document.</summary>
        private static void RenderPageRange(byte[] pdfBytes, int start, int end,
            RenderWorker reuse, int dpi, PdfImageFormat format, int quality,
            PdfRenderOptions options, System.Threading.CancellationToken cancellationToken,
            byte[][] results, Action<int, byte[]> sink, object gate)
        {
            RenderWorker worker = reuse ?? new RenderWorker(pdfBytes);
            try
            {
                for (int i = start; i < end; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] bytes = RenderOne(worker.Document, i, dpi, format, quality, options,
                                             worker.Shared, cancellationToken);
                    if (sink == null) results[i] = bytes;
                    else if (gate == null) sink(i, bytes);
                    else lock (gate) sink(i, bytes);
                }
            }
            finally
            {
                if (reuse == null) worker.Dispose();
            }
        }

        /// <summary>Renders one page of an open document to the requested format.</summary>
        /// <remarks>
        /// PNG keeps its <see cref="Core.Plugins.IRendererPlugin"/> first refusal; the other formats
        /// cannot have it, because that interface's contract is to return encoded <b>PNG</b> bytes.
        /// That asymmetry predates this method and is documented on the interface.
        /// </remarks>
        private static byte[] RenderOne(PdfDocument doc, int pageIndex, int dpi,
            PdfImageFormat format, int quality, PdfRenderOptions options,
            PdfRenderSession.SharedState shared, System.Threading.CancellationToken cancellationToken)
        {
            if (format == PdfImageFormat.Png)
                return RenderPage(doc.Pages[pageIndex], doc, dpi, options, shared, cancellationToken);

            using var bitmap = RenderPageToBitmap(doc.Pages[pageIndex], doc, dpi, options, shared,
                                                  cancellationToken);
            return Encode(bitmap, format, quality, options);
        }

        /// <summary>
        /// One batch worker's private document and caches. Disposed when the worker retires, which
        /// is what makes the font cache's lifetime provably shorter than the encoded results'.
        /// </summary>
        private sealed class RenderWorker : IDisposable
        {
            public readonly MemoryStream Stream;
            public readonly PdfDocument Document;
            public readonly PdfRenderSession.SharedState Shared;

            public RenderWorker(byte[] pdfBytes, bool decodeOnCallingThread = false)
            {
                Stream = new MemoryStream(pdfBytes, writable: false);
                try
                {
                    Document = PdfDocument.Open(Stream);
                    Shared   = PdfRenderSession.CreateSharedState(Document);
                    Shared.DecodeOnCallingThread = decodeOnCallingThread;
                }
                catch { Stream.Dispose(); throw; }
            }

            public void Dispose()
            {
                // FontCache.Dispose destroys every typeface it handed out. Nothing may hold one
                // past this point, and nothing does: every result is encoded bytes.
                Shared?.Fonts?.Dispose();
                Document?.Dispose();
                Stream?.Dispose();
            }
        }

        // ----------------------------------------------------------------
        //  Tile rendering
        // ----------------------------------------------------------------

        /// <summary>
        /// Renders a rectangular tile of a PDF page.
        /// </summary>
        /// <remarks>
        /// The clip is measured in default user-space units <b>from the render box's own origin</b>
        /// — the box <paramref name="renderOptions"/> selects, the same one
        /// <see cref="RenderPage(Stream,int,int,PdfRenderOptions)"/> would render — so a tile of
        /// (0, 0, boxWidth, boxHeight) reproduces the whole page and a grid of tiles reassembles
        /// it. Only the y axis followed that rule before: the x origin was dropped, so a page whose
        /// box does not start at x = 0 tiled wrongly along one axis and correctly along the other.
        /// </remarks>
        /// <param name="inputPath">Path to the PDF file.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="clipX">Left edge of the clip region, from the render box's origin.</param>
        /// <param name="clipY">Bottom edge of the clip region, from the render box's origin.</param>
        /// <param name="clipW">Width of the clip region in default user-space units.</param>
        /// <param name="clipH">Height of the clip region in default user-space units.</param>
        /// <param name="dpi">Output resolution.</param>
        /// <param name="renderOptions">
        /// Optional rendering parameters. A tile honours the same ones a whole-page render does —
        /// which box to render, <c>SimulateOverprint</c>, <c>DownsampleImagesToTarget</c> and the
        /// PNG effort.
        /// </param>
        public static byte[] RenderPageTile(string inputPath, int pageIndex,
            float clipX, float clipY, float clipW, float clipH, int dpi = DefaultDpi,
            PdfRenderOptions renderOptions = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return RenderPageTile(fs, pageIndex, clipX, clipY, clipW, clipH, dpi, renderOptions);
        }

        /// <summary>Renders a rectangular tile of a PDF page from a stream.</summary>
        public static byte[] RenderPageTile(Stream input, int pageIndex,
            float clipX, float clipY, float clipW, float clipH, int dpi = DefaultDpi,
            PdfRenderOptions renderOptions = null)
            => RenderPageTileToImage(input, pageIndex, clipX, clipY, clipW, clipH,
                                     PdfImageFormat.Png, dpi, DefaultJpegQuality, renderOptions);

        /// <summary>
        /// Renders a rectangular tile of a PDF page to <paramref name="format"/>.
        /// </summary>
        /// <remarks>
        /// The tile family had PNG and JPEG as two methods with two bodies, and the JPEG one
        /// silently lost the provenance stamp its whole-page sibling applied. Both come through
        /// here now, so a third format could not reintroduce that.
        /// </remarks>
        public static byte[] RenderPageTileToImage(Stream input, int pageIndex,
            float clipX, float clipY, float clipW, float clipH,
            PdfImageFormat format = PdfImageFormat.Png, int dpi = DefaultDpi,
            int quality = DefaultJpegQuality, PdfRenderOptions renderOptions = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (dpi   <= 0)   throw new ArgumentOutOfRangeException(nameof(dpi));
            ValidateClip(clipX, clipY, clipW, clipH);
            ValidateJpegQuality(quality);

            return WithDecryptedPage(input, pageIndex, renderOptions?.Password, (page, doc) =>
            {
                using var bitmap = RenderPageTileToBitmap(page, doc,
                    clipX, clipY, clipW, clipH, dpi, renderOptions, null, cancellationToken);
                return Encode(bitmap, format, quality, renderOptions);
            });
        }

        /// <summary>
        /// Renders one tile from an already-open document, reusing the session's caches. The only
        /// entry point <see cref="PdfRenderSession"/> needs from here.
        /// </summary>
        internal static byte[] RenderTileWithSharedState(PdfDocument doc, int pageIndex,
            float clipX, float clipY, float clipW, float clipH, int dpi,
            PdfRenderSession.SharedState shared, PdfRenderOptions renderOptions,
            PdfImageFormat format = PdfImageFormat.Png, int quality = DefaultJpegQuality,
            System.Threading.CancellationToken cancellationToken = default)
        {
            using var bitmap = RenderPageTileToBitmap(doc.Pages[pageIndex], doc,
                clipX, clipY, clipW, clipH, dpi, renderOptions, shared, cancellationToken);
            return Encode(bitmap, format, quality, renderOptions);
        }

        /// <summary>Renders a whole page from an already-open document, reusing the session's caches.</summary>
        internal static byte[] RenderPageWithSharedState(PdfDocument doc, int pageIndex, int dpi,
            PdfRenderSession.SharedState shared, PdfRenderOptions renderOptions,
            PdfImageFormat format = PdfImageFormat.Png, int quality = DefaultJpegQuality,
            System.Threading.CancellationToken cancellationToken = default)
        {
            var page = doc.Pages[pageIndex];

            // PNG goes through the chokepoint, so a registered IRendererPlugin keeps first refusal.
            // It did not, and the session's own doc comment promised output "identical to
            // PdfPageRenderer.RenderPage" - so a host that registered a colour-managed renderer got
            // the plugin's pixels from the one call and the built-in renderer's from the other,
            // silently, with no diagnostic (the bypass report only fires at the chokepoint this
            // path never reached).
            //
            // JPEG and WebP still do not: IRendererPlugin returns encoded PNG bytes, so there is
            // nothing to re-encode without a decode, which is the scope the interface documents.
            if (format == PdfImageFormat.Png)
                return RenderPage(page, doc, dpi, renderOptions, shared, cancellationToken);

            using var bitmap = RenderPageToBitmap(page, doc, dpi, renderOptions,
                                                  shared, cancellationToken);
            return Encode(bitmap, format, quality, renderOptions);
        }

        /// <summary>
        /// As <see cref="RenderTileWithSharedState"/>, but returning unencoded pixels.
        /// </summary>
        /// <remarks>
        /// <para><b>The combination that was missing.</b> There was a tile path that encodes and a
        /// whole-page path that does not, so a caller wanting a <i>region's</i> raw pixels — to
        /// stream a large page out band by band, or to write TIFF strips — had to encode each region
        /// and decode it again. Both halves already existed; only this composition did not.</para>
        /// <para><b><c>paintGate</c></b>: when non-null,
        /// the operations allowed to paint; every other op still dispatches exactly as always
        /// (state, clip, resources, text position — nothing about that changes), only its ink is
        /// skipped if it lands on one of <see cref="PdfRenderContext"/>'s five gated paint methods.
        /// <see cref="PdfRenderSession.RenderPageBands"/> and
        /// <see cref="PdfRenderSession.RenderPageTileToPixels"/> pass one; the result's
        /// <see cref="RenderedPage.SuppressedPaints"/> says how many paints it skipped. Null renders
        /// unfiltered.</para>
        /// </remarks>
        internal static RenderedPage RenderTilePixelsWithSharedState(PdfDocument doc, int pageIndex,
            float clipX, float clipY, float clipW, float clipH, int dpi,
            PdfRenderSession.SharedState shared, PdfRenderOptions renderOptions,
            System.Threading.CancellationToken cancellationToken = default,
            PaintGate paintGate = null)
        {
            using var bitmap = RenderPageTileToBitmap(doc.Pages[pageIndex], doc,
                clipX, clipY, clipW, clipH, dpi, renderOptions, shared, cancellationToken,
                paintGate);
            RenderedPage pixels = ToRenderedPage(bitmap);
            pixels.SuppressedPaints = paintGate?.Suppressed ?? 0;
            return pixels;
        }

        /// <summary>As <see cref="RenderPageWithSharedState"/>, but returning unencoded pixels.</summary>
        internal static RenderedPage RenderPagePixelsWithSharedState(PdfDocument doc, int pageIndex,
            int dpi, PdfRenderSession.SharedState shared, PdfRenderOptions renderOptions,
            System.Threading.CancellationToken cancellationToken = default)
        {
            using var bitmap = RenderPageToBitmap(doc.Pages[pageIndex], doc, dpi, renderOptions,
                                                  shared, cancellationToken);
            return ToRenderedPage(bitmap);
        }

        /// <summary>
        /// The page's decoded content bytes, memoised on <paramref name="shared"/> when there is
        /// one. Called by both the whole-page and the tile path, so they cannot diverge on it.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the memo exists.</b> <see cref="ContentOperationCache"/> keys on the
        /// <i>reference identity</i> of the decoded array, which is exactly right for a single
        /// content stream: <c>PdfStream.GetDecodedData</c> memoises one instance and every caller
        /// gets it. A page whose <c>/Contents</c> is an <b>array</b> of streams has no such
        /// instance — <see cref="PdfPage.GetContentStreamData"/> concatenates a fresh array on
        /// every call — so sixteen tiles of one multi-stream page missed the cache sixteen times
        /// and re-tokenised the whole page each time, which is the exact cost the session exists to
        /// remove. Memoising the concatenation is what makes the identity stable.</para>
        /// <para><b>Never memoised without a session.</b> With <paramref name="shared"/> null this
        /// is a plain pass-through: a per-render memo would retain the page content past the
        /// render, and "entries die with the render" is what the one-shot entry points promise.</para>
        /// <para>A page with no <c>/Contents</c> memoises as <c>null</c>. Storing the null rather
        /// than treating it as a cache miss is what stops such a page re-resolving its absent
        /// content on every tile.</para>
        /// </remarks>
        internal static byte[] PageContent(PdfPage page, PdfRenderSession.SharedState shared)
        {
            if (page   == null) return null;
            if (shared == null) return page.GetContentStreamData();

            // Reference-keyed by construction: PdfPage does not override Equals, and one session
            // holds one document, whose Pages list hands out the same instances throughout.
            var memo = shared.PageContent ??= new Dictionary<PdfPage, byte[]>();
            if (memo.TryGetValue(page, out byte[] hit)) return hit;

            byte[] content = page.GetContentStreamData();

            // Bounded like every other session-lifetime cache here: this holds whole decoded pages
            // for as long as the session lives, and a thousand-page document would otherwise retain
            // all of them. Past the ceiling nothing new is added and what is held stays — the pages
            // already present are the ones proven to repeat, and a wrong eviction policy is worse
            // than none.
            long cost = content?.LongLength ?? 0;
            if (shared.PageContentBytes + cost <= shared.MaxPageContentBytes)
            {
                memo[page] = content;
                shared.PageContentBytes += cost;
            }
            return content;
        }

        /// <summary>
        /// The page's <see cref="DisplayListIndex"/>, memoised on <paramref name="shared"/> for the
        /// session's life. Only
        /// <see cref="PdfRenderSession.RenderPageBands"/> calls this; <paramref name="shared"/> is
        /// never null there (every session builds one unconditionally), so unlike
        /// <see cref="PageContent"/> this has no standalone, session-less mode to fall back to.
        /// </summary>
        /// <remarks>
        /// <para><b>One throwaway execution, at scale 1.</b> Building the index means running the
        /// page's content once through <see cref="ContentStreamInterpreter.BuildDisplayList"/>,
        /// which paints for real (see that method's own remarks for why) into a bitmap this method
        /// discards immediately. <see cref="DisplayListEntry.PageBounds"/> is DPI-independent by
        /// construction, so the throwaway pass's own scale
        /// does not need to match the caller's target <c>dpi</c> — using 1 keeps the discarded
        /// bitmap small regardless of how high the real bands will render. Setting
        /// <see cref="PdfRenderContext.Operations"/> from <paramref name="shared"/> before parsing
        /// means this pass tokenises nothing the first real band would not have tokenised anyway —
        /// <see cref="ContentOperationCache"/> keys on the content array's reference identity, and
        /// <see cref="PageContent"/> is what makes that identity stable across calls.</para>
        /// <para><b>Rotation-oblivious, matching the tile family it feeds.</b> The index is built
        /// in <paramref name="size"/>'s own box coordinate space (<c>WidthPoints</c>/<c>HeightPoints</c>,
        /// pre-rotation) — the same space <see cref="PdfRenderSession.RenderPageBands"/>'s own
        /// <c>clipY</c>/<c>clipH</c> already use, and the same scope boundary
        /// <see cref="RenderPageTileToBitmap"/>'s remarks record ("a tile honours neither /Rotate
        /// nor the page's annotations"). This does not make a rotated page's banding any more or
        /// less correct than it already was; it reuses the coordinate convention already in force
        /// rather than introducing a second one.</para>
        /// </remarks>
        internal static DisplayListIndex GetOrBuildDisplayListIndex(
            PdfPage page, PdfDocument doc, PageRenderSize size, PdfRenderSession.SharedState shared)
        {
            var memo = shared.DisplayListIndexes ??= new Dictionary<PdfPage, DisplayListIndex>();
            if (memo.TryGetValue(page, out DisplayListIndex hit)) return hit;

            List<DisplayListEntry> entries = new List<DisplayListEntry>();
            byte[] content = PageContent(page, shared);
            if (content != null && content.Length > 0)
            {
                int w = Math.Max(1, SaturatingCast.ToInt(Math.Ceiling(size.WidthPoints)));
                int h = Math.Max(1, SaturatingCast.ToInt(Math.Ceiling(size.HeightPoints)));
                using var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var canvas = new SKCanvas(bitmap);
                using var renderCtx =
                    new PdfRenderContext(canvas, size.WidthPoints, size.HeightPoints, 1f, bitmap);
                renderCtx.Operations = shared.Operations;

                var interpreter =
                    new ContentStreamInterpreter(renderCtx, doc, page.Resources, shared.Fonts)
                    { FormBudget = new RenderBudget() };
                interpreter.HiddenOcgs = shared.HiddenOcgs ?? BuildHiddenOcgSet(doc);

                var ops = renderCtx.GetOrParseOperations(content);
                entries = interpreter.BuildDisplayList(ops);
            }

            var index = new DisplayListIndex(entries);
            memo[page] = index;
            return index;
        }

        /// <param name="options">
        /// The same rendering parameters the whole-page path takes — see the remarks.
        /// </param>
        /// <param name="shared">
        /// Optional per-session state — font cache, parsed-operation cache, hidden-OCG set and the
        /// page-content memo — reused across calls. Null means this tile owns its own, the standalone
        /// behaviour.
        /// </param>
        /// <remarks>
        /// <para><b>Why <paramref name="shared"/> exists.</b> A 4x4 tile grid measured 4.1x the time
        /// and 12.4x the allocation of the same page rendered whole, and roughly two-thirds of that
        /// penalty is re-tokenising the page's content stream once per tile. The parsed-operation
        /// cache alone cannot fix it: each tile is its own render, so a render-scoped cache never
        /// spans them. Passing one in is what closes it — see <see cref="PdfRenderSession"/>.</para>
        /// <para><b>The tile is the page's other renderer, and it takes the page's options.</b> A
        /// tile mosaic agrees with the whole-page render on five decisions: the box
        /// (<see cref="ResolveRenderBox"/>, with <c>UseTrimBox</c> and friends and the document's
        /// <c>/ViewerPreferences</c>, not <c>CropBox ?? MediaBox</c>), <c>/UserUnit</c>, the
        /// background (<see cref="PdfRenderOptions.BackgroundArgb"/>, not hardcoded white),
        /// <see cref="PdfRenderOptions.SimulateOverprint"/> and
        /// <see cref="PdfRenderOptions.DownsampleImagesToTarget"/>. Two paths reading one file and
        /// quietly producing different pixels is the defect class to avoid; each of the five is
        /// pinned by a test comparing a tile mosaic against the whole page.</para>
        /// <para><b>Still deliberately different from the page path, and not an oversight:</b> a
        /// tile honours neither <c>/Rotate</c> nor the page's annotations. Both are behaviour
        /// changes for every existing tile caller rather than bug fixes — honouring <c>/Rotate</c>
        /// inside a tile changes what the caller's clip rectangle means (pre- or post-rotation
        /// space), which is an API semantics question. They are tracked as their own entry.</para>
        /// <para><b>Two things the four options do <i>not</i> promise.</b> Overprint composites
        /// against this tile's own bitmap, which is per-pixel exact — the whole content stream still
        /// runs for every tile, so a pixel inside the tile sees the same backdrop it would on the
        /// whole page — but it does mean a tile is not cheaper for an overprinting page. And under
        /// <see cref="PdfRenderOptions.DownsampleImagesToTarget"/> a mosaic is <b>not</b>
        /// byte-identical to the whole page: the chosen decode size depends on the device extent an
        /// image covers, which is per tile by definition. Compare colours, not bytes, under that
        /// flag.</para>
        /// </remarks>
        /// <summary>
        /// Produces an <see cref="SKBitmap"/> of a rectangular tile of the page.
        /// The caller owns and must dispose the returned bitmap.
        /// </summary>
        /// <param name="page">The page to render.</param>
        /// <param name="doc">The open document <paramref name="page"/> belongs to.</param>
        /// <param name="clipX">Tile left edge, in PDF points from the render box's origin.</param>
        /// <param name="clipY">Tile top edge, in PDF points from the render box's origin.</param>
        /// <param name="clipW">Tile width in PDF points.</param>
        /// <param name="clipH">Tile height in PDF points.</param>
        /// <param name="dpi">Output resolution in dots per inch.</param>
        /// <param name="cancellationToken">Observed before the content stream is executed.</param>
        /// <param name="paintGate">See <see cref="RenderTilePixelsWithSharedState"/>'s own
        /// remarks — null paints unfiltered.</param>
        /// <param name="plate">The separation plate a pass of <see cref="OverprintPreview"/> draws, or null for an ordinary tile.</param>
        private static SKBitmap RenderPageTileToBitmap(PdfPage page, PdfDocument doc,
            float clipX, float clipY, float clipW, float clipH, int dpi,
            PdfRenderOptions options = null, PdfRenderSession.SharedState shared = null,
            System.Threading.CancellationToken cancellationToken = default,
            PaintGate paintGate = null, SeparationPlate plate = null)
        {
            options ??= PdfRenderOptions.Default;
            cancellationToken.ThrowIfCancellationRequested();
            NamedSpotColors.Attach(doc, options);

            // Overprint simulation of a page that overprints: the tile's plates, recombined — as
            // the whole page does it (OverprintPreview), so a mosaic and the page agree.
            if (plate == null && options.SimulateOverprint && OverprintPreview.PageOverprints(page, doc))
            {
                SKBitmap preview = OverprintPreview.Preview(
                    p => RenderPageTileToBitmap(page, doc, clipX, clipY, clipW, clipH, dpi, options, shared, cancellationToken, paintGate, p),
                    dpi, doc, options, out bool pressProofed);
                return FinishSurface(preview, options, null, pressProofed);
            }

            // The same box the whole-page path would render, chosen the same way.
            var box = ResolveRenderBox(page, doc, options);
            // §7.9.5's normalisation, in PageBoxMetrics. Only the origin is needed here; the extent comes
            // from the caller's tile rectangle.
            PageBoxMetrics tileBox = PageBoxMetrics.Of(box);
            float pdfX = tileBox.X;
            float pdfY = tileBox.Y;

            // §14.11.1: one default-user-space unit is /UserUnit points, so it belongs in the
            // device scale — and it has to be folded in BEFORE the pixel extent is computed, or a
            // tile covering the whole page comes out a different pixel size from the page itself.
            // Note this also moves where ClampRenderDimensions trips for a large /UserUnit, which
            // is the same trade the page path already makes.
            float scale     = dpi / PdfUnits.PointsPerInch * ResolveUserUnit(page);
            int   pixWidth  = Math.Max(1, SaturatingCast.ToInt(Math.Ceiling(clipW * scale)));
            int   pixHeight = Math.Max(1, SaturatingCast.ToInt(Math.Ceiling(clipH * scale)));

            ClampRenderDimensions(ref pixWidth, ref pixHeight, ref scale);

            var bitmap = new SKBitmap(pixWidth, pixHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
            try
            {
                using (var canvas = new SKCanvas(bitmap))
                {
                    // The caller's background, not a hardcoded white — the fifth decision on which
                    // a tile mosaic silently disagreed with the whole page. A caller compositing a
                    // transparent render over their own canvas got a transparent page and opaque
                    // white tiles, with nothing reported.
                    // A plate starts with no ink, whatever background the caller would composite on.
                    canvas.Clear(plate != null ? SKColors.White : BackgroundColor(options));

                    // The session owns its FontCache for its whole life; a standalone call owns one
                    // for this tile. Disposing a borrowed cache would destroy every typeface it has
                    // handed out to the session's other tiles — the use-after-dispose this
                    // repository has already hit once, on Linux, where it segfaulted the test host.
                    FontCache ownFonts  = shared == null ? new FontCache() : null;
                    FontCache fontCache = shared?.Fonts ?? ownFonts;
                    try
                    {
                        // A tile is a render in its own right, configured exactly as the whole
                        // page is — see NewPageContext for the five decisions that drifted here.
                        using var renderCtx = NewPageContext(canvas, clipW, clipH, scale, bitmap, shared, options, plate, page, doc);

                        // Shift content so the clip origin aligns with the bitmap origin. The clip
                        // is measured from the render box's own origin — which is why pdfX is
                        // added, and it used not to be: the x term was a bare -clipX while the y
                        // term already carried the box origin, so a page whose box does not start
                        // at x = 0 (a /TrimBox render, or a /MediaBox like [100 100 300 300]) put
                        // its content in the wrong tile horizontally and in the right one
                        // vertically. Choosing the box origin as the datum is what makes a tile of
                        // (0, 0, boxW, boxH) reproduce the whole page.
                        float[] shift = { 1, 0, 0, 1, -(pdfX + clipX), -(pdfY + clipY) };
                        renderCtx.ConcatCTM(shift);
                        // §8.7.3.1: the page's default space, the tile's shift included, is what a
                        // pattern's /Matrix maps to — so a tile and the whole page tile it alike.
                        renderCtx.GS.PatternBase = (float[])renderCtx.GS.CTM.Clone();

                        var resources  = page.Resources;
                        byte[] content = PageContent(page, shared);
                        if (content != null && content.Length > 0)
                        {
                            var interpreter = new ContentStreamInterpreter(renderCtx, doc, resources, fontCache)
                            { FormBudget = new RenderBudget() };
                            interpreter.HiddenOcgs = shared?.HiddenOcgs ?? BuildHiddenOcgSet(doc);
                            // The token was checked once at entry and then never reached the
                            // interpreter, so a tile of a million-operator page ran to completion
                            // however early the host aborted - precisely the case the public
                            // overload's cancellation contract advertises.
                            interpreter.CancellationToken = cancellationToken;

                            // Every operator still dispatches --
                            // nothing about state changes -- only a gated paint method's own
                            // canvas-touching step is skipped when its op index misses the set.
                            // CurrentOpIndex is read from the closure at call time, not captured
                            // by value, so this stays correct across the whole Execute below.
                            if (paintGate != null)
                                renderCtx.PaintSuppressed = () => paintGate.Suppresses(interpreter.CurrentOpIndex);

                            interpreter.Execute(content);
                        }
                    }
                    finally { ownFonts?.Dispose(); }
                }
                // Proofed and converted as the whole page is — so a mosaic and the page agree.
                return FinishSurface(bitmap, options, plate);
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        // ----------------------------------------------------------------
        //  JPEG Public API
        // ----------------------------------------------------------------

        /// <summary>
        /// Renders the specified page of a PDF file to a JPEG image.
        /// </summary>
        /// <param name="inputPath">Path to the PDF file.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="dpi">Output resolution in dots per inch (default <see cref="DefaultDpi"/>).</param>
        /// <param name="quality">JPEG quality, 1–100 (default 85).</param>
        /// <param name="renderOptions">
        /// Render options, or <see langword="null"/> for <see cref="PdfRenderOptions.Default"/>.
        /// </param>
        /// <returns>JPEG-encoded image bytes.</returns>
        public static byte[] RenderPageAsJpg(string inputPath, int pageIndex, int dpi = DefaultDpi,
            int quality = DefaultJpegQuality, PdfRenderOptions renderOptions = null)
            => RenderPageAsJpg(inputPath, pageIndex, dpi, quality, renderOptions,
                               System.Threading.CancellationToken.None);

        /// <summary>Renders a page of a PDF file to JPEG, with cancellation.</summary>
        public static byte[] RenderPageAsJpg(string inputPath, int pageIndex, int dpi,
            int quality, PdfRenderOptions renderOptions,
            System.Threading.CancellationToken cancellationToken)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return RenderPageAsJpg(fs, pageIndex, dpi, quality, renderOptions, cancellationToken);
        }

        /// <summary>
        /// Renders the specified page of a PDF stream to a JPEG image.
        /// </summary>
        public static byte[] RenderPageAsJpg(Stream input, int pageIndex, int dpi = DefaultDpi,
            int quality = DefaultJpegQuality, PdfRenderOptions renderOptions = null)
            => RenderPageAsJpg(input, pageIndex, dpi, quality, renderOptions,
                               System.Threading.CancellationToken.None);

        /// <summary>Renders a page of a PDF stream to JPEG, with cancellation.</summary>
        public static byte[] RenderPageAsJpg(Stream input, int pageIndex, int dpi,
            int quality, PdfRenderOptions renderOptions,
            System.Threading.CancellationToken cancellationToken)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (dpi   <= 0)   throw new ArgumentOutOfRangeException(nameof(dpi), "DPI must be positive.");
            ValidateJpegQuality(quality);

            return WithDecryptedPage(input, pageIndex, renderOptions?.Password, (page, doc) =>
            {
                using var bitmap = RenderPageToBitmap(page, doc, dpi, renderOptions, null, cancellationToken);
                return EncodeJpeg(bitmap, quality, renderOptions);
            });
        }

        // ----------------------------------------------------------------
        //  Unencoded output (see RenderedPage for why this exists)
        // ----------------------------------------------------------------

        /// <summary>
        /// Renders a page to raw pixels, skipping image encoding entirely.
        /// </summary>
        /// <remarks>
        /// <para>Use this when the pixels are going to be drawn or processed rather than stored
        /// or transmitted. PNG encoding is roughly <b>97%</b> of what
        /// <see cref="RenderPage(Stream,int,int)"/> costs — ~79 ms of an ~82 ms blank page at
        /// 150 dpi — so a caller that would only decode the result again saves nearly all of
        /// it.</para>
        /// <para>Unlike the PNG path this does <b>not</b> consult a registered
        /// <c>IRendererPlugin</c>: that interface's contract is to return encoded PNG bytes, so
        /// a plugin has no pixels to hand over. A document that a plugin would have rendered is
        /// rendered here by the built-in renderer instead.</para>
        /// </remarks>
        /// <param name="input">The PDF stream.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="dpi">Output resolution.</param>
        /// <param name="renderOptions">Optional rendering parameters.</param>
        public static RenderedPage RenderPageToPixels(Stream input, int pageIndex,
            int dpi = DefaultDpi, PdfRenderOptions renderOptions = null)
            => RenderPageToPixels(input, pageIndex, dpi, renderOptions,
                                  System.Threading.CancellationToken.None);

        /// <summary>Renders a page to raw pixels, with cancellation.</summary>
        public static RenderedPage RenderPageToPixels(Stream input, int pageIndex,
            int dpi, PdfRenderOptions renderOptions,
            System.Threading.CancellationToken cancellationToken)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (dpi   <= 0)   throw new ArgumentOutOfRangeException(nameof(dpi), "DPI must be positive.");

            return WithDecryptedPage(input, pageIndex, renderOptions?.Password, (page, doc) =>
            {
                using var bitmap = RenderPageToBitmap(page, doc, dpi, renderOptions, null, cancellationToken);
                return ToRenderedPage(bitmap);
            });
        }

        /// <summary>
        /// Renders one page to PNG bytes using the <b>built-in</b> renderer, without consulting any
        /// registered <see cref="Core.Plugins.IRendererPlugin"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This exists for renderer plugins themselves.</b> An <c>IRendererPlugin</c> is
        /// dispatched from <see cref="RenderPage(Stream,int,int,PdfRenderOptions)"/>, so a plugin
        /// that wants to delegate to (or post-process) the built-in output must not call that
        /// entry point back — doing so re-enters dispatch, selects the same plugin again, and
        /// recurses until the stack overflows. <c>SkiaSharpRendererPlugin</c> did exactly that; the
        /// crash was invisible for as long as a higher-priority plugin kept it from ever being
        /// selected. See GAPS.md §15e.
        /// </para>
        /// <para>
        /// It deliberately reuses the same <c>RenderPageToBitmap</c> + <c>EncodePng</c> pair as the
        /// dispatching path, so a caller cannot end up with different PNG bytes (or a different
        /// <see cref="PdfRenderOptions.PngCompression"/> default) than the built-in fallback would
        /// have produced. Rendering to <see cref="RenderPageToPixels(Stream,int,int,PdfRenderOptions)"/>
        /// and encoding independently would reintroduce exactly that divergence.
        /// </para>
        /// </remarks>
        public static byte[] RenderPageWithoutPlugins(Stream input, int pageIndex,
            int dpi = DefaultDpi, PdfRenderOptions renderOptions = null)
            => RenderPageWithoutPlugins(input, pageIndex, dpi, renderOptions,
                                        System.Threading.CancellationToken.None);

        /// <summary>As above, with cancellation.</summary>
        public static byte[] RenderPageWithoutPlugins(Stream input, int pageIndex,
            int dpi, PdfRenderOptions renderOptions,
            System.Threading.CancellationToken cancellationToken)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (dpi   <= 0)   throw new ArgumentOutOfRangeException(nameof(dpi), "DPI must be positive.");

            return WithDecryptedPage(input, pageIndex, renderOptions?.Password, (page, doc) =>
            {
                using var bitmap = RenderPageToBitmap(page, doc, dpi, renderOptions, null, cancellationToken);
                return EncodePng(bitmap, renderOptions);
            });
        }

        /// <summary>Renders a page of a PDF file to raw pixels.</summary>
        public static RenderedPage RenderPageToPixels(string inputPath, int pageIndex,
            int dpi = DefaultDpi, PdfRenderOptions renderOptions = null)
            => RenderPageToPixels(inputPath, pageIndex, dpi, renderOptions,
                                  System.Threading.CancellationToken.None);

        /// <summary>Renders a page of a PDF file to raw pixels, with cancellation.</summary>
        public static RenderedPage RenderPageToPixels(string inputPath, int pageIndex,
            int dpi, PdfRenderOptions renderOptions,
            System.Threading.CancellationToken cancellationToken)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return RenderPageToPixels(fs, pageIndex, dpi, renderOptions, cancellationToken);
        }

        /// <summary>
        /// Copies a rendered surface out into a <see cref="RenderedPage"/>. The copy is what
        /// keeps SkiaSharp out of this library's public API — see the type's remarks.
        /// </summary>
        private static RenderedPage ToRenderedPage(SKBitmap bitmap)
        {
            // RenderedPage.Pixels documents RGBA8888 premultiplied. Every surface this renderer
            // creates already is that (Rgba8888/Premul at :423, :772, :821), so the conversion
            // below never runs today — it is here so the documented contract holds by
            // construction rather than by every allocation site happening to agree. A comment
            // asserting that agreement is not enforcement.
            if (bitmap.ColorType != SKColorType.Rgba8888 || bitmap.AlphaType != SKAlphaType.Premul)
            {
                var info = new SKImageInfo(bitmap.Width, bitmap.Height,
                                           SKColorType.Rgba8888, SKAlphaType.Premul);
                using var converted = new SKBitmap(info);
                if (bitmap.CopyTo(converted, SKColorType.Rgba8888))
                    return CopyOut(converted);

                RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                    $"Could not convert a {bitmap.ColorType}/{bitmap.AlphaType} surface to " +
                    "RGBA8888 premultiplied; RenderedPage.Pixels carries the source layout instead.");
            }

            return CopyOut(bitmap);
        }

        private static RenderedPage CopyOut(SKBitmap bitmap)
        {
            int stride = bitmap.RowBytes;
            var pixels = new byte[checked(stride * bitmap.Height)];

            // GetPixelSpan is a view onto the native surface; one bulk copy, no per-pixel
            // marshalling. (SKBitmap.Pixels would allocate an SKColor[] and convert per pixel.)
            bitmap.GetPixelSpan().CopyTo(pixels);

            return new RenderedPage(bitmap.Width, bitmap.Height, stride, pixels);
        }

        /// <summary>
        /// Renders all pages of a PDF file and returns each page as JPEG bytes.
        /// </summary>
        /// <remarks>
        /// Shares <see cref="RenderAllPagesCore"/> with every other batch entry point, so it opens
        /// no <see cref="PdfDocument"/> or <see cref="FontCache"/> per page.
        /// </remarks>
        public static byte[][] RenderAllPagesAsJpg(string inputPath, int dpi = DefaultDpi,
            int quality = DefaultJpegQuality, PdfRenderOptions renderOptions = null)
            => RenderAllPagesAsJpg(inputPath, dpi, quality, renderOptions,
                                   System.Threading.CancellationToken.None);

        /// <summary>Renders every page of a PDF file to JPEG, with cancellation.</summary>
        public static byte[][] RenderAllPagesAsJpg(string inputPath, int dpi, int quality,
            PdfRenderOptions renderOptions,
            System.Threading.CancellationToken cancellationToken)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            ValidateJpegQuality(quality);

            // Buffer + decrypt once rather than re-opening the path per worker: an encrypted
            // file has to be resolved before any page's content stream is meaningful.
            byte[] pdfBytes = EnsureDecryptedBytes(File.ReadAllBytes(inputPath), renderOptions?.Password);
            return RenderAllPagesCore(pdfBytes, dpi, PdfImageFormat.Jpeg, quality, renderOptions,
                cancellationToken, sink: null);
        }

        /// <summary>
        /// Renders all pages of a PDF stream and returns each page as JPEG bytes.
        /// </summary>
        /// <remarks>
        /// <b>The same core as <see cref="RenderAllPages(Stream,int,PdfRenderOptions)"/></b>, one
        /// <see cref="PdfDocument"/> and <see cref="FontCache"/> shared across the pages and the work
        /// spread over workers. Measured on a 50-page document in one run: 174 ms, against 1695 ms
        /// for a sequential loop opening a fresh document and font cache for every page. Two paths
        /// doing the same work by different means is the defect class this repository keeps finding,
        /// so there is one implementation.
        /// </remarks>
        public static byte[][] RenderAllPagesAsJpg(Stream input, int dpi = DefaultDpi,
            int quality = DefaultJpegQuality, PdfRenderOptions renderOptions = null)
            => RenderAllPagesAsJpg(input, dpi, quality, renderOptions,
                                   System.Threading.CancellationToken.None);

        /// <summary>Renders every page of a PDF stream to JPEG, with cancellation.</summary>
        public static byte[][] RenderAllPagesAsJpg(Stream input, int dpi, int quality,
            PdfRenderOptions renderOptions,
            System.Threading.CancellationToken cancellationToken)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (dpi   <= 0)   throw new ArgumentOutOfRangeException(nameof(dpi));
            ValidateJpegQuality(quality);

            return RenderAllPagesCore(BufferPdf(input, renderOptions), dpi, PdfImageFormat.Jpeg,
                quality, renderOptions, cancellationToken, sink: null);
        }

        // ----------------------------------------------------------------
        //  Format-agnostic entry points (PNG, JPEG, WebP)
        // ----------------------------------------------------------------

        /// <summary>
        /// Renders one page to <paramref name="format"/>.
        /// </summary>
        /// <remarks>
        /// The per-format methods above remain, and delegate here; this is the entry point that
        /// makes <see cref="PdfImageFormat.WebpLossless"/> — measured ~9x smaller than PNG on a
        /// rendered page — reachable without a fourth family of overloads.
        /// </remarks>
        /// <param name="input">Stream holding the PDF. Seek-normalised and decrypted internally.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="dpi">Output resolution in dots per inch.</param>
        /// <param name="format">Encoded output format.</param>
        /// <param name="renderOptions">
        /// Render options, or <see langword="null"/> for <see cref="PdfRenderOptions.Default"/>.
        /// </param>
        /// <param name="cancellationToken">Observed before the page is rendered.</param>
        /// <param name="quality">1–100, used by <see cref="PdfImageFormat.Jpeg"/> and
        /// <see cref="PdfImageFormat.WebpLossy"/> and ignored by the lossless formats.</param>
        public static byte[] RenderPageToImage(Stream input, int pageIndex,
            PdfImageFormat format, int dpi = DefaultDpi, int quality = DefaultJpegQuality,
            PdfRenderOptions renderOptions = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (dpi   <= 0)   throw new ArgumentOutOfRangeException(nameof(dpi), "DPI must be positive.");
            ValidateJpegQuality(quality);

            if (format == PdfImageFormat.Png)
                return RenderPage(input, pageIndex, dpi, renderOptions, cancellationToken);

            return WithDecryptedPage(input, pageIndex, renderOptions?.Password, (page, doc) =>
            {
                using var bitmap = RenderPageToBitmap(page, doc, dpi, renderOptions, null, cancellationToken);
                return Encode(bitmap, format, quality, renderOptions);
            });
        }

        /// <summary>Renders one page of a PDF file to <paramref name="format"/>.</summary>
        public static byte[] RenderPageToImage(string inputPath, int pageIndex,
            PdfImageFormat format, int dpi = DefaultDpi, int quality = DefaultJpegQuality,
            PdfRenderOptions renderOptions = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return RenderPageToImage(fs, pageIndex, format, dpi, quality, renderOptions, cancellationToken);
        }

        /// <summary>Renders every page to <paramref name="format"/>.</summary>
        public static byte[][] RenderAllPagesToImages(Stream input, PdfImageFormat format,
            int dpi = DefaultDpi, int quality = DefaultJpegQuality,
            PdfRenderOptions renderOptions = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (dpi   <= 0)   throw new ArgumentOutOfRangeException(nameof(dpi));
            ValidateJpegQuality(quality);

            return RenderAllPagesCore(BufferPdf(input, renderOptions), dpi, format, quality,
                renderOptions, cancellationToken, sink: null);
        }

        /// <summary>
        /// Renders every page to <paramref name="format"/> and hands each one to
        /// <paramref name="onPage"/> as it finishes, holding none of them.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this exists.</b> Every other batch entry point returns
        /// <c>byte[][]</c> — the whole document, encoded, resident. At 150 dpi a page is a few
        /// hundred KB, so a thousand-page document is hundreds of MB held only so the caller can
        /// write each page out and drop it. This streams instead.</para>
        /// <para><paramref name="onPage"/> is invoked under a lock, so it need not be thread-safe,
        /// and it receives the page index because <b>completion order is not page order</b>.</para>
        /// </remarks>
        /// <returns>The number of pages rendered.</returns>
        public static int RenderAllPagesToImages(Stream input, PdfImageFormat format,
            Action<int, byte[]> onPage, int dpi = DefaultDpi, int quality = DefaultJpegQuality,
            PdfRenderOptions renderOptions = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (input  == null) throw new ArgumentNullException(nameof(input));
            if (onPage == null) throw new ArgumentNullException(nameof(onPage));
            if (dpi    <= 0)    throw new ArgumentOutOfRangeException(nameof(dpi));
            ValidateJpegQuality(quality);

            return RenderAllPagesCore(BufferPdf(input, renderOptions), dpi, format, quality,
                renderOptions, cancellationToken, onPage).Length;
        }

        /// <summary>
        /// Renders one page straight into <paramref name="output"/>.
        /// </summary>
        /// <remarks>
        /// Saves the caller's copy of the image: every other entry point returns a
        /// <c>byte[]</c> that a caller writing to a file or a response body then copies again.
        /// The image is still buffered once inside, because the PNG and JPEG provenance stamps
        /// insert a header into a finished file.
        /// </remarks>
        public static void RenderPageToStream(Stream input, int pageIndex, Stream output,
            PdfImageFormat format = PdfImageFormat.Png, int dpi = DefaultDpi,
            int quality = DefaultJpegQuality, PdfRenderOptions renderOptions = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            byte[] bytes = RenderPageToImage(input, pageIndex, format, dpi, quality,
                                             renderOptions, cancellationToken);
            output.Write(bytes, 0, bytes.Length);
        }

        /// <summary>Renders a rectangular tile of a PDF page to JPEG.</summary>
        public static byte[] RenderPageTileAsJpg(string inputPath, int pageIndex,
            float clipX, float clipY, float clipW, float clipH,
            int dpi = DefaultDpi, int quality = DefaultJpegQuality,
            PdfRenderOptions renderOptions = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return RenderPageTileAsJpg(fs, pageIndex, clipX, clipY, clipW, clipH, dpi, quality, renderOptions);
        }

        /// <summary>Renders a rectangular tile of a PDF page to JPEG.</summary>
        public static byte[] RenderPageTileAsJpg(Stream input, int pageIndex,
            float clipX, float clipY, float clipW, float clipH,
            int dpi = DefaultDpi, int quality = DefaultJpegQuality,
            PdfRenderOptions renderOptions = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (dpi   <= 0)   throw new ArgumentOutOfRangeException(nameof(dpi));
            ValidateClip(clipX, clipY, clipW, clipH);
            ValidateJpegQuality(quality);

            return RenderPageTileToImage(input, pageIndex, clipX, clipY, clipW, clipH,
                PdfImageFormat.Jpeg, dpi, quality, renderOptions);
        }

        // ----------------------------------------------------------------
        //  Core render
        // ----------------------------------------------------------------

        /// <summary>Renders one page to PNG bytes, consulting a registered renderer plugin first.</summary>
        /// <param name="page">The page to render.</param>
        /// <param name="doc">The document it belongs to.</param>
        /// <param name="dpi">Output resolution.</param>
        /// <param name="options">Optional rendering parameters.</param>
        /// <param name="shared">
        /// Optional per-run state — font cache, parsed-operation cache, hidden-OCG set and the
        /// page-content memo. Null (the default) means this render owns its own, which is what
        /// every per-page and parallel entry point wants; <see cref="RenderAllPages(Stream,int,PdfRenderOptions)"/>
        /// passes one because it renders every page of one document on one thread.
        /// </param>
        /// <param name="cancellationToken">Observed before the content stream is executed.</param>
        private static byte[] RenderPage(PdfPage page, PdfDocument doc, float dpi,
            PdfRenderOptions options = null, PdfRenderSession.SharedState shared = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            // Give a registered IRendererPlugin first refusal. The host returns the
            // highest-Priority plugin whose CanRender accepted this page; the built-in
            // SkiaSharp renderer below is the effective priority-0 fallback, exactly as
            // IRendererPlugin documents.
            //
            // This is the ONLY dispatch point for IRendererPlugin: it is the single chokepoint
            // through which every PNG render path flows. (The JPEG and tile paths call
            // RenderPageToBitmap directly and cannot use a plugin, because IRendererPlugin
            // returns encoded PNG bytes rather than an SKBitmap — see the interface remarks.)
            //
            // A fractional DPI — which only RenderPageToFit produces — is not offered to a plugin
            // at all: IRendererPlugin.RenderPage takes an int, so it would round, and an image one
            // pixel off the requested box silently breaks the one guarantee that entry point
            // makes. Rounding into the plugin and hoping is the wrong half of that trade.
            bool integralDpi = dpi > 0 && Math.Abs(dpi - (float)Math.Round(dpi)) < 1e-4f;

            // The same trade the DPI check above makes, for the same reason. IRendererPlugin takes
            // no PdfRenderOptions and cannot (it is a Core contract; the options type is in Base),
            // so a plugin renders with defaults for the page box, background, intent, overprint and
            // downsampling. Dispatching anyway meant registering a plugin silently discarded
            // whatever the caller asked for — UseTrimBox came back as the CropBox, with no error.
            var effective = options ?? PdfRenderOptions.Default;
            bool pluginCanServe = !effective.RequestsAnythingAPluginCannotHonour();

            // Looked up ONCE. The predicate decides whether it may serve; the same instance is
            // then either used or reported as bypassed, instead of the plugin host being searched
            // twice for the same page.
            var candidate = integralDpi
                ? Core.Plugins.PdfPluginHost.Instance.FindRendererPlugin(page, doc)
                : null;
            var plugin = pluginCanServe ? candidate : null;

            if (candidate != null && !pluginCanServe)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.RenderParameterIgnored,
                    "A registered IRendererPlugin was bypassed because the caller supplied render " +
                    "options it cannot receive (page box, background, intent, overprint, pixel " +
                    "format or image downsampling); the built-in renderer was used so those " +
                    "options take effect.");
            }
            if (plugin != null)
            {
                byte[] pluginPng;
                try
                {
                    pluginPng = plugin.RenderPage(page, doc, SaturatingCast.ToInt(Math.Round(dpi)));
                }
                catch (Exception ex)
                {
                    // A failing renderer plugin must not take down the render: report it and
                    // fall back to the built-in renderer.
                    RenderDiagnostics.Report(RenderDiagnosticKind.PluginFailure,
                        $"IRendererPlugin '{plugin.Name}' threw; falling back to the built-in renderer.", ex);
                    pluginPng = null;
                }

                if (pluginPng != null && pluginPng.Length > 0)
                    return pluginPng;

                if (pluginPng != null)
                    RenderDiagnostics.Report(RenderDiagnosticKind.PluginFailure,
                        $"IRendererPlugin '{plugin.Name}' returned empty output; falling back to the built-in renderer.");
            }

            using var bitmap = RenderPageToBitmap(page, doc, dpi, options, shared, cancellationToken);
            return EncodePng(bitmap, options);
        }

        /// <summary>
        /// A render context configured the way <b>every</b> page render in this class configures
        /// one: the session's parsed-operation cache when there is a session, the per-render
        /// overprint re-arm, and the two options that must reach the context.
        /// </summary>
        /// <remarks>
        /// <para><b>The whole-page path and the tile path both take it.</b> Configured twice, each
        /// decision — the background clear, the overprint re-arm — would be one on which a tile
        /// mosaic could silently disagree with the whole page.</para>
        ///
        /// <para><b>Why the overprint reset is here and not in the caller.</b> The one-shot
        /// reporting slots are <c>[ThreadStatic]</c>; re-armed only by tests, they would leave the
        /// second and every later page rendered on a server host's thread silent about overprint it
        /// had not simulated. A slot a caller reads after an operation is reset at the
        /// start of that operation — and "the operation" is a render, which is exactly this method.</para>
        ///
        /// <para><b>Deliberately not used by <c>ExtractInkCoverage</c>.</b> That pass builds a
        /// context too, but it is a measurement rather than a render: it clears to white rather than
        /// to the caller's background, owns its font cache, and takes no session. Folding it in
        /// would give it three settings it has no use for and one — the caller's background — that
        /// would change what it measures.</para>
        /// </remarks>
        private static PdfRenderContext NewPageContext(
            SKCanvas canvas, float widthPoints, float heightPoints, float scale, SKBitmap bitmap,
            PdfRenderSession.SharedState shared, PdfRenderOptions options, SeparationPlate plate,
            PdfPage page, PdfDocument doc)
        {
            var ctx = new PdfRenderContext(canvas, widthPoints, heightPoints, scale, bitmap);

            if (shared != null) ctx.Operations = shared.Operations;

            // §11.6.6: the page group's /CS is honoured whatever its /I — the page is imposed on the
            // medium, so it is isolated regardless — and it is the space every group that names
            // none inherits. A page group of /DeviceGray renders the page grey; one of /DeviceCMYK
            // composites every translucent or blended paint in ink.
            PdfObject pageGroupCs = PdfColor.TransparencyGroupColorSpace(page.Dictionary, doc);
            if (!PdfColor.TryGetGroupBlendSpace(pageGroupCs, doc, out GroupBlendSpace? pageSpace, out string pageSpaceDetail))
                RenderDiagnostics.Report(RenderDiagnosticKind.TransparencyGroupApproximated, "page group: " + pageSpaceDetail);
            else if (PdfColor.GroupBlendSpaceApproximation(pageGroupCs, doc) is string pageApproximated)
                RenderDiagnostics.Report(RenderDiagnosticKind.TransparencyGroupApproximated, "page group: " + pageApproximated);
            ctx.RootBlendSpace = pageSpace ?? GroupBlendSpace.Rgb;

            PdfRenderContext.ResetOverprintReporting();
            // A plate decides overprint per colorant itself; the simulation reads sRGB pixels back
            // as CMYK, which a plate's grey is not.
            ctx.Plate                    = plate;
            ctx.SimulateOverprint        = options.SimulateOverprint && plate == null;
            ctx.DownsampleImagesToTarget = options.DownsampleImagesToTarget;
            ctx.JpxDecodeThreads         = JpxDecodeThreads(shared, options);
            // Was missing, and nothing noticed: PdfRenderOptions.ImageResampler reached no pixel,
            // so a caller choosing Nearest to keep a scan sharp silently got Mitchell wherever the
            // image carried /Interpolate true.
            ctx.ImageResampler           = options.ImageResampler;

            return ctx;
        }

        /// <summary>
        /// The thread budget a page's JPEG 2000 decodes get: one thread inside a batch render's
        /// worker (the batch parallelises by page, and N workers each starting the decoder's
        /// automatic degree would run N times up to sixteen threads),
        /// else <see cref="PdfRenderOptions.MaxRenderThreads"/> as the page render's cap, 0 meaning
        /// the decoder chooses.
        /// </summary>
        internal static int JpxDecodeThreads(PdfRenderSession.SharedState shared, PdfRenderOptions options) =>
            shared != null && shared.DecodeOnCallingThread ? 1 : options.MaxRenderThreads;

        /// <summary>The page background as a Skia colour, from <see cref="PdfRenderOptions.BackgroundArgb"/>.</summary>
        private static SKColor BackgroundColor(PdfRenderOptions options)
        {
            uint argb = (options ?? PdfRenderOptions.Default).BackgroundArgb;
            PdfColor.UnpackArgb(argb, out byte a, out byte r, out byte g, out byte b);
            return new SKColor(red: r, green: g, blue: b, alpha: a);
        }

        /// <summary>
        /// Everything the geometry of a render is decided by: which box, how big in points, how
        /// big in pixels, and what rotation is applied afterwards.
        /// </summary>
        /// <remarks>
        /// <b>Extracted so three callers cannot disagree.</b> The pixel extent was computed inside
        /// <see cref="RenderPageToBitmap"/>, so <see cref="GetRenderSize(Stream,int,int,PdfRenderOptions)"/>
        /// and <see cref="RenderPageToFit(Stream,int,int,int,PdfImageFormat,int,PdfRenderOptions,System.Threading.CancellationToken)"/>
        /// would each have had to re-derive it — including the <c>/UserUnit</c> fold-in, the
        /// inverted-rectangle normalisation and the resolution clamp, every one of which has been a
        /// defect here before.
        /// </remarks>
        internal sealed class PageMetrics
        {
            /// <summary>Render-box origin in PDF user space, normalised for an inverted rectangle.</summary>
            public float OriginX, OriginY;
            /// <summary>Render-box extent in PDF points.</summary>
            public float PointsWidth, PointsHeight;
            /// <summary>Device scale actually used, after the resolution clamp.</summary>
            public float Scale;
            /// <summary>Pixel extent before <c>/Rotate</c> is applied.</summary>
            public int RawWidthPixels, RawHeightPixels;
            /// <summary>Normalised <c>/Rotate</c>, 0/90/180/270.</summary>
            public int Rotation;
            // Through PdfPageRotation.SwapsAxes, which exists BECAUSE this test was written out at
            // each site: at the sites that had not quantised first it silently answered "no" for
            // every illegal angle, which is how a 45-degree page ended up drawn rotated inside a
            // portrait bitmap. Rotation is already normalised here, so this pair was correct — but
            // being correct is not the same as being the one implementation.
            /// <summary>Pixel extent of the finished image, with <c>/Rotate</c> applied.</summary>
            public int WidthPixels  => PdfPageRotation.SwapsAxes(Rotation) ? RawHeightPixels : RawWidthPixels;
            /// <summary>Pixel extent of the finished image, with <c>/Rotate</c> applied.</summary>
            public int HeightPixels => PdfPageRotation.SwapsAxes(Rotation) ? RawWidthPixels  : RawHeightPixels;
        }

        /// <summary>Resolves the geometry of a render without performing one.</summary>
        internal static PageMetrics MeasurePage(PdfPage page, PdfDocument doc, float dpi,
            PdfRenderOptions options)
        {
            options ??= PdfRenderOptions.Default;

            // Resolve the page box to honour per /ViewerPreferences + explicit flags.
            var box = ResolveRenderBox(page, doc, options);
            // §7.9.5's normalisation and the "no usable page box" fallback, in PageBoxMetrics.
            // Subtracting blindly gave a negative width for an inverted /MediaBox, which
            // Math.Max(1, Ceiling(negative)) then turned into a 1x1-pixel page.
            PageBoxMetrics metrics = PageBoxMetrics.Of(box);
            float pdfX = metrics.X;
            float pdfY = metrics.Y;
            float pdfW = metrics.Width;
            float pdfH = metrics.Height;

            // 1 PDF point = 1/72 inch.
            //
            // PDF 1.6+ /UserUnit (§14.11.1) scales the DEFAULT USER SPACE: one unit is UserUnit
            // points, so it belongs in the device scale: multiplying pdfW/pdfH instead would size the
            // bitmap correctly and leave the drawing transform at 1x — the page at its original size
            // in a UserUnit-times-larger canvas, with the rest blank.
            float userUnit  = ResolveUserUnit(page);
            float scale     = dpi / PdfUnits.PointsPerInch * userUnit;
            int   rawWidth  = Math.Max(1, SaturatingCast.ToInt(Math.Ceiling(pdfW * scale)));
            int   rawHeight = Math.Max(1, SaturatingCast.ToInt(Math.Ceiling(pdfH * scale)));

            // Handle page rotation (/Rotate), through the one reader, which checks §7.7.3.3's "shall
            // be a multiple of 90": normalising mod 360 alone, /Rotate 45 would reach
            // canvas.RotateDegrees(45) while the axis-swap test (`== 90 || == 270`) said
            // "portrait", drawing the page at an arbitrary angle inside an un-swapped bitmap with
            // its corners clipped off. The SVG writer did the same into an un-swapped viewBox, and
            // the WinForms viewer's PageGeometry quantised the same value to 0 — three answers to
            // one sentence of the spec.
            int rotate = PdfPageRotation.Normalize(page.Rotate, out bool rotateMalformed);
            if (rotateMalformed)
                RenderDiagnostics.Report(RenderDiagnosticKind.RenderParameterIgnored,
                    $"/Rotate {page.Rotate} is not a multiple of 90 (§7.7.3.3); the page is " +
                    "rendered upright.");

            // Guard against malformed PDFs with absurd MediaBox dimensions, bounding both
            // per-axis pixel count and total bitmap area (see ClampRenderDimensions).
            ClampRenderDimensions(ref rawWidth, ref rawHeight, ref scale);

            return new PageMetrics
            {
                OriginX = pdfX, OriginY = pdfY,
                PointsWidth = pdfW, PointsHeight = pdfH,
                Scale = scale,
                RawWidthPixels = rawWidth, RawHeightPixels = rawHeight,
                Rotation = rotate,
            };
        }

        /// <summary>
        /// The size a page will render at, without rendering it.
        /// </summary>
        /// <remarks>
        /// <para>There was no way to ask this. A caller sizing a control, allocating a buffer or
        /// laying out a contact sheet had to render the page and measure the result, or re-derive
        /// the geometry itself from <c>/MediaBox</c> — and get <c>/CropBox</c> selection,
        /// <c>/UserUnit</c>, <c>/Rotate</c> and the resolution clamp right, all four of which have
        /// been defects in this file.</para>
        /// <para><see cref="PageRenderSize.EffectiveDpi"/> is the honest one: a page whose
        /// requested pixel extent exceeds the safety limits is clamped, so it can come back lower
        /// than the <paramref name="dpi"/> asked for.</para>
        /// </remarks>
        public static PageRenderSize GetRenderSize(Stream input, int pageIndex, int dpi = DefaultDpi,
            PdfRenderOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (dpi   <= 0)   throw new ArgumentOutOfRangeException(nameof(dpi), "DPI must be positive.");

            return WithDecryptedPage(input, pageIndex, options?.Password,
                (page, doc) => ToRenderSize(page, doc, dpi, options));
        }

        /// <summary>The size a page of a PDF file will render at.</summary>
        public static PageRenderSize GetRenderSize(string inputPath, int pageIndex,
            int dpi = DefaultDpi, PdfRenderOptions options = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return GetRenderSize(fs, pageIndex, dpi, options);
        }

        /// <summary>
        /// The size <b>every</b> page will render at, from a single document load.
        /// </summary>
        /// <remarks>
        /// <para><b>The batch form exists because the per-page one is a whole document load.</b>
        /// <see cref="GetRenderSize(Stream,int,int,PdfRenderOptions)"/> goes through
        /// <c>WithDecryptedDocument</c>, so asking N pages for their size in a loop parses the file
        /// N times — and every caller that wants all of them is about to render them, parsing it N
        /// times more. Measured on a 36-page document at 96 dpi, the size loop alone cost 132 ms
        /// and 116 MiB of allocation, 13% of the wall clock and 26% of the allocations of the
        /// export that contained it, to answer a question one load answers.</para>
        /// <para>Each entry is what <see cref="GetRenderSize(Stream,int,int,PdfRenderOptions)"/>
        /// would return for that page — the same <see cref="MeasurePage"/> call behind the same
        /// <see cref="ToRenderSize"/> conversion, so the two cannot report different geometry for
        /// one page.</para>
        /// </remarks>
        /// <param name="input">Stream holding the PDF. Seek-normalised and decrypted internally.</param>
        /// <param name="dpi">Output resolution in dots per inch.</param>
        /// <param name="options">
        /// Render options, or <see langword="null"/> for <see cref="PdfRenderOptions.Default"/>.
        /// </param>
        /// <returns>One entry per page, in page order. Empty for a document with no pages.</returns>
        public static IReadOnlyList<PageRenderSize> GetRenderSizes(Stream input,
            int dpi = DefaultDpi, PdfRenderOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (dpi   <= 0)   throw new ArgumentOutOfRangeException(nameof(dpi), "DPI must be positive.");

            return WithDecryptedDocument(input, options?.Password, doc =>
            {
                var sizes = new PageRenderSize[doc.Pages.Count];
                for (int i = 0; i < sizes.Length; i++)
                    sizes[i] = ToRenderSize(doc.Pages[i], doc, dpi, options);
                return (IReadOnlyList<PageRenderSize>)sizes;
            });
        }

        /// <summary>The size one already-loaded page will render at.</summary>
        internal static PageRenderSize ToRenderSize(PdfPage page, PdfDocument doc, int dpi,
            PdfRenderOptions options)
        {
            var m = MeasurePage(page, doc, dpi, options);
            // Divided by the RESOLVED /UserUnit, not the raw one: a /UserUnit 0 page reported
            // EffectiveDpi = NaN, so a caller sizing a buffer from it got NaN.
            float userUnit = ResolveUserUnit(page);
            return new PageRenderSize(m.WidthPixels, m.HeightPixels, m.PointsWidth,
                m.PointsHeight, m.Rotation, m.Scale * PdfUnits.PointsPerInch / userUnit)
            {
                UserUnit = userUnit
            };
        }

        /// <summary>
        /// Renders a page scaled to fit a pixel box — the thumbnail and preview shape — rather than
        /// at a resolution.
        /// </summary>
        /// <remarks>
        /// <para>Every other entry point takes an integer DPI, so a caller wanting "800 px wide"
        /// had to compute a DPI from the page box, which needs <c>/CropBox</c> selection,
        /// <c>/UserUnit</c> and <c>/Rotate</c> to be right, and then still lands on an integer that
        /// misses the target by up to a percent. This computes the exact fractional scale
        /// internally.</para>
        /// <para>Aspect ratio is preserved and neither bound is exceeded. Pass 0 for a dimension
        /// that is unconstrained; passing 0 for both is an error rather than a full-size render,
        /// because "fit nothing" has no useful reading.</para>
        /// </remarks>
        /// <param name="input">Stream holding the PDF. Seek-normalised and decrypted internally.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="maxWidthPixels">Upper bound on output width, or 0 for unconstrained.</param>
        /// <param name="maxHeightPixels">Upper bound on output height, or 0 for unconstrained.</param>
        /// <param name="format">Encoded output format.</param>
        /// <param name="quality">
        /// Encoder quality, 1–100, for the lossy formats. Ignored by the lossless ones.
        /// </param>
        /// <param name="options">
        /// Render options, or <see langword="null"/> for <see cref="PdfRenderOptions.Default"/>.
        /// </param>
        /// <param name="cancellationToken">Observed before the page is rendered.</param>
        public static byte[] RenderPageToFit(Stream input, int pageIndex,
            int maxWidthPixels, int maxHeightPixels, PdfImageFormat format = PdfImageFormat.Png,
            int quality = DefaultJpegQuality, PdfRenderOptions options = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (maxWidthPixels < 0)  throw new ArgumentOutOfRangeException(nameof(maxWidthPixels));
            if (maxHeightPixels < 0) throw new ArgumentOutOfRangeException(nameof(maxHeightPixels));
            if (maxWidthPixels == 0 && maxHeightPixels == 0)
                throw new ArgumentOutOfRangeException(nameof(maxWidthPixels),
                    "At least one of maxWidthPixels and maxHeightPixels must be positive.");
            ValidateJpegQuality(quality);

            return WithDecryptedPage(input, pageIndex, options?.Password, (page, doc) =>
            {
                // Measure at a known DPI, then scale that DPI by the ratio the caller wants. Going
                // through the same metrics the render uses is what makes the result land on the
                // requested pixel count rather than near it.
                var at72 = MeasurePage(page, doc, PdfUnits.PointsPerInch, options);
                float fit = float.MaxValue;
                if (maxWidthPixels  > 0) fit = Math.Min(fit, maxWidthPixels  / (float)at72.WidthPixels);
                if (maxHeightPixels > 0) fit = Math.Min(fit, maxHeightPixels / (float)at72.HeightPixels);

                float dpi = PdfUnits.PointsPerInch * fit;
                using var bitmap = RenderPageToBitmap(page, doc, dpi, options, null, cancellationToken);
                return Encode(bitmap, format, quality, options);
            });
        }

        /// <summary>Renders a page of a PDF file scaled to fit a pixel box.</summary>
        public static byte[] RenderPageToFit(string inputPath, int pageIndex,
            int maxWidthPixels, int maxHeightPixels, PdfImageFormat format = PdfImageFormat.Png,
            int quality = DefaultJpegQuality, PdfRenderOptions options = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return RenderPageToFit(fs, pageIndex, maxWidthPixels, maxHeightPixels, format, quality,
                                   options, cancellationToken);
        }

        /// <summary>
        /// What every finished surface goes through, the page's, a tile's and an overprint recombination's
        /// alike, in place: soft proofing (<see cref="PdfRenderOptions.ProofProfile"/>), then the conversion into
        /// the destination (<see cref="PdfRenderOptions.DestinationProfile"/>). Never a plate pass; an overprint
        /// preview already made through the press (<see cref="PressProof"/>, <paramref name="pressProofed"/>) is
        /// not proofed again.
        /// </summary>
        private static SKBitmap FinishSurface(SKBitmap bitmap, PdfRenderOptions options, SeparationPlate plate = null,
            bool pressProofed = false)
            => RenderDestination.Apply(pressProofed ? bitmap : SoftProof.Apply(bitmap, options, plate), options, plate);

        /// <summary>
        /// Produces an <see cref="SKBitmap"/> of the rendered page.  Ownership of
        /// the returned bitmap transfers to the caller — callers must dispose it.
        /// </summary>
        /// <param name="page">The page to render.</param>
        /// <param name="doc">The open document <paramref name="page"/> belongs to.</param>
        /// <param name="dpi">Output resolution in dots per inch.</param>
        /// <param name="options">
        /// Render options, or <see langword="null"/> for <see cref="PdfRenderOptions.Default"/>.
        /// </param>
        /// <param name="shared">
        /// Optional per-session state reused across calls — see <see cref="PdfRenderSession"/>.
        /// Null means this render owns its own font cache, parsed-operation cache and hidden-OCG
        /// set, which is the standalone behaviour and is unchanged.
        /// </param>
        /// <param name="cancellationToken">Observed before the content stream is executed.</param>
        /// <param name="plate">
        /// The separation plate to draw (<see cref="PdfSeparationRenderer"/>), on a white surface
        /// whatever the options' background; null for an ordinary render.
        /// </param>
        internal static SKBitmap RenderPageToBitmap(PdfPage page, PdfDocument doc, float dpi,
            PdfRenderOptions options = null, PdfRenderSession.SharedState shared = null,
            System.Threading.CancellationToken cancellationToken = default, SeparationPlate plate = null)
        {
            options ??= PdfRenderOptions.Default;
            cancellationToken.ThrowIfCancellationRequested();
            NamedSpotColors.Attach(doc, options);

            // Overprint simulation of a page that overprints: its separation plates, recombined —
            // every case a plate composites exactly (OverprintPreview). Not inside a plate pass.
            if (plate == null && options.SimulateOverprint && OverprintPreview.PageOverprints(page, doc))
            {
                SKBitmap preview = OverprintPreview.Render(page, doc, dpi, options, cancellationToken, out bool pressProofed);
                return FinishSurface(preview, options, null, pressProofed);
            }

            var metrics = MeasurePage(page, doc, dpi, options);
            float pdfX = metrics.OriginX, pdfY = metrics.OriginY;
            float pdfW = metrics.PointsWidth, pdfH = metrics.PointsHeight;
            float scale = metrics.Scale;
            int rawWidth = metrics.RawWidthPixels, rawHeight = metrics.RawHeightPixels;
            int rotate = metrics.Rotation;

            // Render into the pre-rotation (unrotated) bitmap.
            // All drawing — paths, text, and images — uses the unrotated coordinate system
            // because text/image helpers call SetMatrix directly (bypassing any canvas
            // rotation).  The page rotation is applied to the final bitmap so every element
            // type is transformed consistently.
            SKBitmap rawBitmap = null;
            try
            {
                rawBitmap = new SKBitmap(rawWidth, rawHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(rawBitmap))
                {
                    // A plate starts with no ink, whatever background the caller would composite on.
                    canvas.Clear(plate != null ? SKColors.White : BackgroundColor(options));

                    // A borrowed FontCache belongs to the session and must outlive this render:
                    // FontCache.Dispose destroys every typeface it handed out.
                    FontCache ownFonts  = shared == null ? new FontCache() : null;
                    FontCache fontCache = shared?.Fonts ?? ownFonts;
                    try
                    {
                    using var renderCtx = NewPageContext(canvas, pdfW, pdfH, scale, rawBitmap, shared, options, plate, page, doc);

                    // If the MediaBox origin is non-zero, the CTM must account for it.
                    if (pdfX != 0f || pdfY != 0f)
                    {
                        float[] originShift = { 1, 0, 0, 1, -pdfX, -pdfY };
                        renderCtx.ConcatCTM(originShift);
                    }
                    // §8.7.3.1: a pattern's /Matrix maps to the page's default space — the one in
                    // force here, the box origin included.
                    renderCtx.GS.PatternBase = (float[])renderCtx.GS.CTM.Clone();

                    var resources  = page.Resources;
                    byte[] content = PageContent(page, shared);
                    var hiddenOcgs = shared?.HiddenOcgs ?? BuildHiddenOcgSet(doc);

                    // ONE budget for the whole page — the content stream AND every annotation
                    // appearance — because MaxFormXObjectInvocationsPerPage is a per-PAGE bound and
                    // an /AP stream is page content reached by another route. Allocated fresh inside
                    // the annotation loop, a page with N annotations would get N times the allowance
                    // the constant names and N copies of the refusal, while the SVG renderer holds
                    // exactly one budget per page on SvgSharedState and
                    // shared it with content and annotations alike. The two refused different
                    // documents — the one-renderer-lagging-the-other defect, in the guard added to
                    // close a denial of service.
                    var formBudget = new RenderBudget();

                    if (content != null && content.Length > 0)
                    {
                        var interpreter = new ContentStreamInterpreter(renderCtx, doc, resources, fontCache)
                            { FormBudget = formBudget };
                        interpreter.HiddenOcgs = hiddenOcgs;
                        // Checked between operators, so a page with a million of them can be
                        // abandoned. Passing the token at the boundary only would leave the one page that actually needs it
                        // running to completion.
                        interpreter.CancellationToken = cancellationToken;
                        // Isolate the page content's graphics state. Real-world PDFs frequently
                        // leave the state dirty — an unbalanced q/Q or a top-level cm — and that
                        // leftover CTM/clip would otherwise be inherited by the annotation pass,
                        // mis-placing and mis-sizing signature/stamp appearance streams (PDF §7.8.2).
                        // Push a base frame, run, then unwind back to the clean page base.
                        renderCtx.PushState();
                        interpreter.Execute(content);
                        renderCtx.ResetToBaseState();
                    }

                    // Render annotation appearance streams (/Annots on the page) — unless only
                    // the content is wanted (PdfRenderOptions.ContentOnly).
                    if (options?.ContentOnly != true)
                        RenderAnnotations(page, doc, renderCtx, fontCache, hiddenOcgs, options,
                                          cancellationToken, formBudget);
                    }
                    finally { ownFonts?.Dispose(); }
                }

                // The finished surface, content and annotations alike, before the rotation that only
                // moves pixels. Never a plate pass.
                FinishSurface(rawBitmap, options, plate);

                if (rotate == 0)
                {
                    var result = rawBitmap;
                    rawBitmap  = null;   // transfer ownership to caller
                    return result;
                }

                // Rotate the rendered bitmap by the page /Rotate angle.
                bool swap     = rotate == 90 || rotate == 270;
                int outWidth  = swap ? rawHeight : rawWidth;
                int outHeight = swap ? rawWidth  : rawHeight;
                var rotated   = new SKBitmap(outWidth, outHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
                try
                {
                    using (var canvas = new SKCanvas(rotated))
                    {
                        // The same background as the unrotated surface. Clearing this one to white
                        // unconditionally would have made a transparent render opaque for exactly
                        // the pages that carry /Rotate — a difference no caller could predict.
                        // Transparent, and the blit REPLACES rather than composites.
                        //
                        // The raw surface already carries the background and covers the whole
                        // rotated extent after the rotation, so clearing this one to the same
                        // colour and drawing SrcOver over it composited a translucent background
                        // TWICE: 50% red came back at 75% alpha on a /Rotate 90 page and at 50% on
                        // an otherwise identical /Rotate 0 one, while the SVG exporter emitted a
                        // single rect at 50% for the same request. One option, three answers.
                        canvas.Clear(SKColors.Transparent);
                        canvas.Translate(outWidth  / 2f, outHeight / 2f);
                        canvas.RotateDegrees(rotate);
                        canvas.Translate(-rawWidth / 2f, -rawHeight / 2f);
                        using (var blit = new SKPaint { BlendMode = SKBlendMode.Src })
                            canvas.DrawBitmap(rawBitmap, 0, 0, SKSamplingOptions.Default, blit);
                    }
                    return rotated;
                }
                catch
                {
                    rotated.Dispose();
                    throw;
                }
            }
            finally
            {
                rawBitmap?.Dispose();  // no-op on success (null after ownership transfer or after rotation)
            }
        }

        // ----------------------------------------------------------------
        //  Encoders (shared by PNG/JPEG paths)
        // ----------------------------------------------------------------

        /// <summary>
        /// Encodes the page surface, honouring every encoding option the caller set.
        /// </summary>
        /// <remarks>
        /// The encoders themselves live in <see cref="PdfImageEncoder"/>, which is reached from
        /// every path here — whole page, tile, batch and each format — so each makes the same
        /// decisions (the tile JPEG path keeps the provenance stamp) and a format is added once.
        /// </remarks>
        private static byte[] Encode(SKBitmap bitmap, PdfImageFormat format, int quality,
            PdfRenderOptions options)
            => PdfImageEncoder.Encode(bitmap, format, quality, options ?? PdfRenderOptions.Default);

        /// <summary>Encodes to PNG, honouring <see cref="PdfRenderOptions.PngCompression"/>.</summary>
        private static byte[] EncodePng(SKBitmap bitmap, PdfRenderOptions options)
            => Encode(bitmap, PdfImageFormat.Png, DefaultJpegQuality, options);

        /// <summary>Encodes to JPEG, honouring <see cref="PdfRenderOptions.JpegSubsampling"/>.</summary>
        private static byte[] EncodeJpeg(SKBitmap bitmap, int quality, PdfRenderOptions options)
            => Encode(bitmap, PdfImageFormat.Jpeg, quality, options);

        private static void ValidateJpegQuality(int quality)
        {
            if (quality < 1 || quality > 100)
                throw new ArgumentOutOfRangeException(nameof(quality),
                    "JPEG quality must be between 1 and 100.");
        }

        // ----------------------------------------------------------------
        //  Annotation rendering (PDF §12.5)
        // ----------------------------------------------------------------

        /// <summary>
        /// Renders the /Annots array of <paramref name="page"/> by executing each
        /// annotation's /AP (appearance stream) Normal entry as a Form XObject.
        /// Only annotations that have an /AP /N entry are rendered; all others are ignored.
        /// </summary>
        /// <param name="page">The page whose <c>/Annots</c> are drawn.</param>
        /// <param name="doc">The open document <paramref name="page"/> belongs to.</param>
        /// <param name="renderCtx">Target context; annotations draw into the page's own CTM.</param>
        /// <param name="fontCache">Font cache for the appearance streams' resources.</param>
        /// <param name="hiddenOcgs">
        /// Optional-content groups resolved hidden for this render, so an annotation whose
        /// <c>/OC</c> names one is skipped (§8.11.4).
        /// </param>
        /// <param name="options">
        /// Render options, or <see langword="null"/> for <see cref="PdfRenderOptions.Default"/>.
        /// </param>
        /// <param name="cancellationToken">Observed once per annotation.</param>
        /// <param name="formBudget">
        /// The page's form-XObject budget, shared with the page content stream. Null mints one for
        /// this pass, which is correct only when the caller renders no page content.
        /// </param>
        private static void RenderAnnotations(
            PdfPage page, PdfDocument doc,
            PdfRenderContext renderCtx, FontCache fontCache,
            HashSet<int> hiddenOcgs = null, PdfRenderOptions options = null,
            System.Threading.CancellationToken cancellationToken = default,
            RenderBudget formBudget = null)
        {
            // One budget for the whole annotation pass, not one per annotation.
            formBudget ??= new RenderBudget();
            var annotsObj = page.Dictionary[PdfName.Annots];
            if (annotsObj == null) return;
            var annots = doc.ResolveObject(annotsObj) as PdfArray ?? annotsObj as PdfArray;
            if (annots == null || annots.Count == 0) return;

            // §12.7.3.3 — document-scoped, so it is resolved once rather than per annotation.
            // The SVG exporter reads the same predicate at the same point in its own loop.
            bool needAppearances = AnnotationFallbackModel.FormNeedsAppearances(doc);

            foreach (var annotObj in annots)
            {
                var annot = doc.ResolveObject(annotObj) as PdfDictionary ?? annotObj as PdfDictionary;
                if (annot == null) continue;

                // PDF §12.5.3, plus §12.7.5.5's rule for a signature widget — one implementation,
                // AnnotationVisibility, shared with the SVG exporter. The block it replaces was
                // written out in both loops, and the signature rule was in neither. The rule is
                // not the same for the two targets: printing is opt-in via /Print, and NoView
                // means "print but do not show" — except for a signature, which it hides outright.
                if (AnnotationVisibility.IsHidden(annot, doc, options)) continue;

                // An annotation may carry /OC (PDF §12.5.2) naming an OCG or OCMD. The
                // XObject and BDC paths already honour optional content; annotations did
                // not, so a stamp/watermark on a hidden layer still drew.
                // No shortcut on an empty hidden set: the SVG loop has none, and every group ON still
                // hides an annotation whose OCMD shows it only while a group is off.
                if (ContentStreamInterpreter.IsAnnotationOcHidden(annot, hiddenOcgs, doc))
                    continue;

                // /Rect is needed by both the AP-stream path and the fallback — resolve early.
                var rectObj = annot[PdfName.Rect];
                var rect = doc.ResolveObject(rectObj) as PdfArray ?? rectObj as PdfArray;
                // A /Rect past float's range draws nothing, in both renderers (MatrixGuard).
                if (MatrixGuard.HasNonFiniteNumber(rect, doc))
                {
                    RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                        "annotation ignored: a /Rect number past the range of a float");
                    continue;
                }

                // Get the /AP (appearance dictionary) → /N (normal appearance).
                var apObj = annot[PdfName.AP];
                if (apObj == null)
                {
                    // No appearance stream — synthesise from annotation styling keys.
                    if (rect != null && rect.Count >= 4)
                        RenderAnnotationFallback(annot, rect, doc, renderCtx);
                    continue;
                }
                // An /AP that does not resolve to a dictionary, or one carrying no /N, is the same
                // situation the `apStream == null` arm below already handles: the annotation
                // CLAIMED an appearance and cannot supply one. A `continue` would make the annotation
                // vanish while the SVG exporter falls through to the synthesised /C + /IC drawing and
                // paints it — a `/AP << /D 8 0 R >>` (down-only, which real producers write)
                // rendering as nothing and exporting as a red-stroked blue square.
                //
                // Drawing nothing is the worse of the two answers: the file says a square is there,
                // /F says to print it, and §12.5.5 makes /N REQUIRED in an appearance dictionary —
                // so a missing one is malformed input, not an instruction to omit the annotation.
                var ap = doc.ResolveObject(apObj) as PdfDictionary ?? apObj as PdfDictionary;
                var nObj = ap?[PdfName.N];
                if (ap == null || nObj == null)
                {
                    if (rect != null && rect.Count >= 4)
                        RenderAnnotationFallback(annot, rect, doc, renderCtx);
                    continue;
                }

                var nResolved = doc.ResolveObject(nObj);

                // /N can be a stream (single appearance) or a dict of sub-states.
                PdfDictionary apStream = null;
                if (nResolved is PdfStream ns) apStream = ns;
                else if (nResolved is PdfDictionary nd)
                {
                    // Select the current appearance state (/AS key on the annotation).
                    // A state /N does not carry draws nothing (AnnotationAppearance.TryNamedState).
                    if (AnnotationAppearance.TryNamedState(doc, annot, nd, out var named))
                    {
                        if (named == null) continue;
                        var sub = doc.ResolveObject(named);
                        apStream = sub as PdfStream ?? sub as PdfDictionary;
                    }
                    // With no /AS, the first entry, so the annotation still renders.
                    else
                    {
                        foreach (var kv in nd)
                        {
                            apStream = doc.ResolveObject(kv.Value) as PdfStream
                                    ?? doc.ResolveObject(kv.Value) as PdfDictionary;
                            if (apStream != null) break;
                        }
                    }
                }

                if (rect == null || rect.Count < 4) { if (apStream == null) continue; }

                if (apStream == null)
                {
                    // /AP present but /N failed to resolve — fall back to synthesised appearance.
                    if (rect != null && rect.Count >= 4)
                        RenderAnnotationFallback(annot, rect, doc, renderCtx);
                    continue;
                }

                // §12.7.3.3: the form may be asking for this appearance to be CONSTRUCTED rather
                // than drawn. The predicate is shared with the SVG exporter and PdfFormFlattener,
                // and is given the stream selected above, so no caller re-does the /AS choice to
                // answer it. It returns the reason so each caller can report to its own sink.
                string staleReason = rect != null && rect.Count >= 4
                    ? AnnotationFallbackModel.WidgetAppearanceIsStale(
                          annot, doc, apStream, needAppearances)
                    : null;
                if (staleReason != null)
                {
                    RenderDiagnostics.Report(
                        RenderDiagnosticKind.FormFieldAppearanceRegenerated, staleReason);
                    RenderAnnotationFallback(annot, rect, doc, renderCtx);
                    continue;
                }

                // PDF §12.5.5 Algorithm 8.1 — map the appearance stream onto /Rect: the /BBox
                // corners through the stream's /Matrix, the bounding box of those, and the matrix A
                // scaling and moving that box onto /Rect; the content runs under Matrix × A. One
                // reading (AnnotationAppearance.TryFitToRect) for both renderers, the imposer and
                // the text extractor; /Rect's elements resolved, as PdfLinkExtractor resolves them.
                // A /Rect with no extent draws nothing.
                if (!AnnotationAppearance.TryFitToRect(doc, rect, apStream, out float[] fit, out float[] apMatrix, out PdfArray bboxArr))
                    continue;
                bool hasBBox = bboxArr != null;

                // §12.5.5 (ISO 32000-2, 38926-38928): the appearance's transparency group "shall be
                // composited with a backdrop ... using the values of the BM, ca and CA entries in
                // the annotation dictionary" — /CA, /ca and /BM alike, or a half-transparent stamp
                // with an /AP would paint opaque, and a /Multiply stamp over text opaque Normal in
                // both renderers, which comparing them could never surface. Which
                // alpha a group composite takes (/ca, else /CA) and which mode it blends with are
                // decided once, in AnnotationAppearance, for both renderers.
                //
                // A LAYER, not a per-element alpha or blend: /ca is a group opacity and /BM a group
                // blend mode, so two overlapping strokes inside one appearance must not
                // double-darken or double-blend where they cross. Setting the graphics state
                // instead would do exactly that, and would also be overwritten by the appearance's
                // own `gs` operators. The layer is opened isolated, as the alpha-only layer it
                // replaces always was: §12.5.5's non-isolated default is observable only through
                // blend modes used INSIDE the appearance, and the SVG twin composites the group as a
                // whole too.
                float annotAlpha = AnnotationAppearance.CompositeAlpha(annot, doc);
                string annotBlend = AnnotationAppearance.BlendMode(annot, doc);
                bool annotLayered = annotAlpha < 1f || !PdfBlendModeEntry.IsNormal(annotBlend);
                if (annotLayered) renderCtx.BeginTransparencyGroup(annotAlpha, annotBlend);

                renderCtx.PushState();
                // Step 3: A maps the transformed-BBox extent onto /Rect.
                renderCtx.ConcatCTM(fit);

                // No rotation step. §12.5.5's algorithm (ISO 32000-2, 38886-38903) is the /BBox
                // through /Matrix, a fit A onto /Rect, then AA = Matrix × A — and nothing else.
                // The fitted appearance is not turned a second time by a widget's /MK /R (or a
                // non-standard /Rotate on the annotation): /MK is "additional information for
                // constructing the annotation's appearance stream" (41198-41200), so Table 192's
                // /R is an instruction to the generator, and the producer's /Matrix already carries
                // the turn. Applied again, the appearance came out sideways and spilled outside
                // /Rect — in both renderers, so comparing them showed nothing. The synthesised
                // fallback never read /MK /R and still does not; a generator that honours it is
                // where that belongs.

                // Step 4: the stream's own /Matrix, inside the fit.
                if (apMatrix != null)
                    renderCtx.ConcatCTM(apMatrix);
                // §8.7.3.1: a pattern the appearance uses maps through the appearance's space.
                renderCtx.GS.PatternBase = (float[])renderCtx.GS.CTM.Clone();

                // /BBox clips the appearance (PDF §8.10.2) — it is in pre-/Matrix form
                // XObject space, so clip after the /Matrix concat. Without this, content
                // larger than the BBox bled outside the annotation rect.
                if (hasBBox)
                {
                    float cbx0 = PdfResolve.ArrayFloat(doc, bboxArr, 0), cby0 = PdfResolve.ArrayFloat(doc, bboxArr, 1);
                    float cbx1 = PdfResolve.ArrayFloat(doc, bboxArr, 2), cby1 = PdfResolve.ArrayFloat(doc, bboxArr, 3);
                    renderCtx.AppendRect(Math.Min(cbx0, cbx1), Math.Min(cby0, cby1),
                                         Math.Abs(cbx1 - cbx0), Math.Abs(cby1 - cby0));
                    renderCtx.ClipPath();
                    renderCtx.NewPath();
                }

                // §12.7.3.3, in AnnotationAppearance — the same ladder the SVG renderer runs, which
                // was written out verbatim in both. A value decision, so it gets one implementation.
                PdfDictionary apResources =
                    AnnotationAppearance.ResolveResources(apStream as PdfDictionary, doc, page);

                byte[] apData;
                try { apData = (apStream as PdfStream)?.GetDecodedData(); }
                catch (Exception ex)
                {
                    // Reported, not swallowed. The SVG exporter already reports this, so the same
                    // damaged file told a host "annotation dropped" on export and nothing at all on
                    // render — and an annotation that silently fails to draw is indistinguishable
                    // from one the file never had. Same kind and wording as the SVG site, minus its
                    // "SVG:" prefix, so a host can correlate the two.
                    RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                        "annotation /AP /N stream could not be decoded; the annotation draws nothing.", ex);
                    renderCtx.PopState(); if (annotLayered) renderCtx.EndTransparencyGroup(null); continue;
                }
                if (apData == null || apData.Length == 0)
                { renderCtx.PopState(); if (annotLayered) renderCtx.EndTransparencyGroup(null); continue; }

                // An appearance is an invocation like a form's, charged to the page's budget before
                // it is run: a page whose /Annots repeats one entry runs its stream that many times,
                // and nothing else counts the entries. Past the budget the annotation draws nothing.
                if (!formBudget.TryChargeFormInvocation(apData.Length))
                { renderCtx.PopState(); if (annotLayered) renderCtx.EndTransparencyGroup(null); continue; }

                var interp = new ContentStreamInterpreter(renderCtx, doc, apResources, fontCache)
                    { FormBudget = formBudget };
                // The page-content interpreter was the only site wired for cancellation, so a page
                // carrying one enormous annotation /AP stream honoured the token for its content
                // and ignored it for its annotations.
                interp.CancellationToken = cancellationToken;
                interp.Execute(apData);
                renderCtx.PopState();
                if (annotLayered) renderCtx.EndTransparencyGroup(null);

                // Emit a per-annotation completion event so downstream
                // consumers can correlate rendered appearances with logical
                // annotation IDs (/NM) — the only stable identifier for an
                // annotation across signing rounds.  Subtype is included in
                // the detail so listeners can filter cheaply.
                string nm = (PdfResolve.Str(doc, annot, PdfName.NM))?.GetText();
                string subtype = (PdfResolve.Name(doc, annot, PdfName.Subtype))?.Value ?? "?";
                RenderDiagnostics.Report(RenderDiagnosticKind.AnnotationRendered,
                    $"NM={nm ?? "(none)"} Subtype={subtype}");
            }
        }

        // ----------------------------------------------------------------
        //  Annotation fallback synthesis
        // ----------------------------------------------------------------

        /// <summary>
        /// Synthesises a minimal appearance for annotations that have no /AP stream,
        /// using /C (border colour), /IC (interior colour), /BS (border style),
        /// /CA (opacity), and /QuadPoints (for markup subtypes).
        /// </summary>
        private static void RenderAnnotationFallback(
            PdfDictionary annot, PdfArray rect,
            PdfDocument doc, PdfRenderContext ctx)
        {
            string subtype = (PdfResolve.Name(doc, annot, PdfName.Subtype))?.Value ?? "";

            // Resolved per element, as in the /AP path above.
            float rx0 = Math.Min(RectFloat(doc, rect, 0), RectFloat(doc, rect, 2));
            float ry0 = Math.Min(RectFloat(doc, rect, 1), RectFloat(doc, rect, 3));
            float rx1 = Math.Max(RectFloat(doc, rect, 0), RectFloat(doc, rect, 2));
            float ry1 = Math.Max(RectFloat(doc, rect, 1), RectFloat(doc, rect, 3));

            // /C — border / title-bar colour (0 comps=transparent, 1=grey, 3=RGB, 4=CMYK).
            SKColor borderColor = ReadAnnotColor(annot, doc, PdfName.C, SKColors.Black);
            // /IC — interior fill colour.
            SKColor interiorColor = ReadAnnotColor(annot, doc, PdfName.IC, SKColors.Transparent);

            // /BS — border style: /W (width), /D (dash array); else the legacy
            // /Border [Hr Vr W [Dash]]. Read once for both renderers.
            float borderWidth = AnnotationFallbackModel.BorderWidth(annot, doc, out float[] dashArray);

            // /CA — annotation opacity.
            float opacity = 1f;
            if (PdfResolve.Number(doc, annot, PdfName.CA) is PdfNumber caNum)
                opacity = Math.Max(0f, Math.Min(1f, (float)caNum.DoubleValue));

            byte alphaB = PdfColor.Channel(opacity);

            // Table 166's /BM (ISO 32000-2, 38548-38554) is "the blend mode that shall be used when
            // painting the annotation onto the page" — for a synthesised appearance as much as for a
            // stream. A group, as on the /AP path and as the SVG exporter's group attribute is, so a
            // stroke over a fill inside one fallback does not double-blend where they overlap.
            string blendMode = AnnotationAppearance.BlendMode(annot, doc);
            bool blended = !PdfBlendModeEntry.IsNormal(blendMode);
            if (blended) ctx.BeginTransparencyGroup(1f, blendMode);

            ctx.PushState();
            ctx.GS.StrokeColor = borderColor.WithAlpha(alphaB);
            ctx.GS.FillColor   = interiorColor.WithAlpha(alphaB);
            ctx.GS.LineWidth   = borderWidth;
            if (dashArray != null && dashArray.Length > 0)
            {
                ctx.GS.DashPattern = dashArray;
                ctx.GS.DashPhase   = 0f;
            }
            ctx.GS.StrokeAlpha = opacity;
            ctx.GS.FillAlpha   = opacity;

            switch (subtype)
            {
                case PdfAnnotationSubtypes.Square:
                    ctx.NewPath();
                    ctx.AppendRect(rx0, ry0, rx1 - rx0, ry1 - ry0);
                    if (interiorColor.Alpha > 0) ctx.FillPath(evenOdd: false, clearAfter: false);
                    ctx.StrokePath();
                    break;

                case PdfAnnotationSubtypes.Circle:
                    // Approximate circle via four Bézier arcs.
                    ctx.NewPath();
                    AppendEllipse(ctx, rx0, ry0, rx1, ry1);
                    if (interiorColor.Alpha > 0) ctx.FillPath(evenOdd: false, clearAfter: false);
                    ctx.StrokePath();
                    break;

                case PdfAnnotationSubtypes.Line:
                    // /L [x1 y1 x2 y2] — line endpoints.
                    var lArr = PdfResolve.Array(doc, annot, PdfName.L);
                    if (lArr != null && lArr.Count >= 4)
                    {
                        ctx.NewPath();
                        ctx.MoveTo(PdfResolve.ArrayFloat(doc, lArr, 0), PdfResolve.ArrayFloat(doc, lArr, 1));
                        ctx.LineTo(PdfResolve.ArrayFloat(doc, lArr, 2), PdfResolve.ArrayFloat(doc, lArr, 3));
                        ctx.StrokePath();
                    }
                    break;

                case PdfAnnotationSubtypes.Text:
                {
                    // Sticky-note Text annotations are conventionally yellow when /C is absent.
                    SKColor noteColor  = AnnotationFallbackModel.NoteColor(annot, borderColor);
                    ctx.GS.FillColor   = noteColor.WithAlpha(alphaB);
                    ctx.GS.StrokeColor = noteColor.WithAlpha(alphaB);
                    // Sticky notes render at a standard icon size; /Rect on Text annotations is often tiny.
                    ctx.NewPath();
                    ctx.AppendRect(rx0, ry0,
                        AnnotationFallbackModel.NoteIconWidth, AnnotationFallbackModel.NoteIconHeight);
                    ctx.FillPath(evenOdd: false, clearAfter: false);
                    ctx.StrokePath();
                    break;
                }

                case PdfAnnotationSubtypes.Highlight:
                    // PDF §12.5.6.10: highlights use Multiply blend at full opacity so text
                    // stays readable (dark text stays dark; white areas take the highlight colour).
                    ctx.GS.BlendMode = PdfBlendModes.Multiply;
                    ctx.GS.FillAlpha = opacity;
                    ctx.GS.FillColor = borderColor.WithAlpha(alphaB);
                    if (!RenderQuadPoints(annot, doc, ctx, fill: true, stroke: false))
                    {
                        // No /QuadPoints — fill the /Rect directly.
                        ctx.NewPath();
                        ctx.AppendRect(rx0, ry0, rx1 - rx0, ry1 - ry0);
                        ctx.FillPath(evenOdd: false, clearAfter: true);
                    }
                    break;

                case PdfAnnotationSubtypes.Underline:
                    RenderQuadPoints(annot, doc, ctx, fill: false, stroke: true,
                        lineYFactor: 0f); // bottom edge
                    break;

                case PdfAnnotationSubtypes.StrikeOut:
                    RenderQuadPoints(annot, doc, ctx, fill: false, stroke: true,
                        lineYFactor: 0.5f); // mid-height
                    break;

                case PdfAnnotationSubtypes.Squiggly:
                    ctx.GS.DashPattern = new float[] { 2f, 1f };
                    ctx.GS.DashPhase   = 0f;
                    RenderQuadPoints(annot, doc, ctx, fill: false, stroke: true,
                        lineYFactor: 0f);
                    break;

                case PdfAnnotationSubtypes.Ink:
                    // /InkList [[x y x y…] …] — polylines. Which strokes are drawable is decided
                    // once, in AnnotationPointList, so the SVG exporter cannot disagree about it.
                    foreach (PdfArray pts in AnnotationPointList.InkStrokes(doc, annot))
                    {
                        ctx.NewPath();
                        ctx.MoveTo(PdfResolve.ArrayFloat(doc, pts, 0), PdfResolve.ArrayFloat(doc, pts, 1));
                        for (int i = 2; i + 1 < pts.Count; i += 2)
                            ctx.LineTo(PdfResolve.ArrayFloat(doc, pts, i), PdfResolve.ArrayFloat(doc, pts, i + 1));
                        ctx.StrokePath();
                    }
                    break;

                case PdfAnnotationSubtypes.PrinterMark:
                    // PDF/X printer marks (crop marks, registration, colour bars) are
                    // prepress artifacts with no screen-rendering semantics of their
                    // own. If the annot carries its own /AP, the caller used that
                    // before reaching this fallback; otherwise we draw nothing so
                    // that screen-preview output stays clean.
                    break;

                case PdfAnnotationSubtypes.FreeText:
                    RenderFreeTextFallback(annot, doc, rx0, ry0, rx1, ry1, ctx,
                        interiorColor, borderColor, borderWidth, alphaB);
                    break;

                case PdfAnnotationSubtypes.Stamp:
                    RenderStampFallback(annot, doc, rx0, ry0, rx1, ry1, ctx,
                        borderColor, borderWidth, alphaB);
                    break;

                case PdfAnnotationSubtypes.Widget:
                    RenderWidgetFallback(annot, rx0, ry0, rx1, ry1, ctx, doc, borderWidth, alphaB);
                    break;

                case PdfAnnotationSubtypes.ThreeD:
                    Render3DAnnotationPlaceholder(rx0, ry0, rx1, ry1, ctx, borderColor, alphaB);
                    break;

                case PdfAnnotationSubtypes.PolyLine:
                case PdfAnnotationSubtypes.Polygon:
                {
                    // /Vertices [x1 y1 x2 y2 …] — open (PolyLine) or closed (Polygon).
                    var vertices = AnnotationPointList.Vertices(doc, annot);
                    if (vertices != null)
                    {
                        ctx.NewPath();
                        ctx.MoveTo(PdfResolve.ArrayFloat(doc, vertices, 0), PdfResolve.ArrayFloat(doc, vertices, 1));
                        for (int i = 2; i + 1 < vertices.Count; i += 2)
                            ctx.LineTo(PdfResolve.ArrayFloat(doc, vertices, i), PdfResolve.ArrayFloat(doc, vertices, i + 1));
                        if (subtype == PdfAnnotationSubtypes.Polygon)
                        {
                            ctx.ClosePath();
                            if (interiorColor.Alpha > 0)
                                ctx.FillPath(evenOdd: false, clearAfter: false);
                        }
                        ctx.StrokePath();
                    }
                    break;
                }

                case PdfAnnotationSubtypes.Caret:
                {
                    // Caret: small inverted-V indicator at the rect bottom.  Per PDF
                    // §12.5.6.11 the caret's position is the /RD-padded /Rect.
                    if (rx1 > rx0 && ry1 > ry0)
                    {
                        float cx = (rx0 + rx1) * 0.5f;
                        float h2 = ry1 - ry0;
                        ctx.NewPath();
                        ctx.MoveTo(rx0, ry0);
                        ctx.LineTo(cx,  ry0 + h2 * AnnotationFallbackModel.CaretApexFactor);
                        ctx.LineTo(rx1, ry0);
                        ctx.ClosePath();
                        if (interiorColor.Alpha > 0)
                            ctx.FillPath(evenOdd: false, clearAfter: false);
                        ctx.StrokePath();
                    }
                    break;
                }

                case PdfAnnotationSubtypes.Link:
                    // Visible link annotations historically render an underline under
                    // the linked text or a thin border.  We honour /Border [Hr Vr W]
                    // when present; otherwise draw a hairline rect so the link is
                    // discoverable in screenshots.
                    RenderLinkFallback(annot, doc, rx0, ry0, rx1, ry1, ctx, borderColor, alphaB);
                    break;

                case PdfAnnotationSubtypes.Popup:
                case PdfAnnotationSubtypes.Sound:
                case PdfAnnotationSubtypes.Movie:
                case PdfAnnotationSubtypes.FileAttachment:
                case PdfAnnotationSubtypes.Screen:
                    // These are interactive annotations whose visible appearance is
                    // entirely defined by /AP — when /AP is absent a small placeholder
                    // rectangle is the standard substitute.
                    if (rx1 > rx0 && ry1 > ry0)
                    {
                        ctx.NewPath();
                        ctx.AppendRect(rx0, ry0, rx1 - rx0, ry1 - ry0);
                        ctx.StrokePath();
                    }
                    break;

                default:
                    RenderDiagnostics.Report(RenderDiagnosticKind.AnnotationSubtypeUnsupported,
                        $"Annotation subtype '{subtype}' has no per-subtype fallback; drew placeholder rect.");
                    if (rx1 > rx0 && ry1 > ry0)
                    {
                        ctx.NewPath();
                        ctx.AppendRect(rx0, ry0, rx1 - rx0, ry1 - ry0);
                        ctx.StrokePath();
                    }
                    break;
            }

            ctx.PopState();
            if (blended) ctx.EndTransparencyGroup(null);
        }

        private static void RenderLinkFallback(
            PdfDictionary annot, PdfDocument doc, float rx0, float ry0, float rx1, float ry1,
            PdfRenderContext ctx, SKColor borderColor, byte alphaB)
        {
            // §12.5.4: /BS /W, else /Border [Hr Vr W [Dash]], else 1; a width of 0 draws no
            // border. The SVG renderer's link reads the same.
            float borderW = AnnotationFallbackModel.BorderWidth(annot, doc, out _);
            if (!(borderW > 0f)) return;
            ctx.GS.LineWidth = borderW;
            ctx.GS.StrokeColor = borderColor.WithAlpha(alphaB);
            ctx.NewPath();
            ctx.AppendRect(rx0, ry0, rx1 - rx0, ry1 - ry0);
            ctx.StrokePath();
        }

        // ------------------------------------------------------------------
        //  Per-subtype fallback helpers (FreeText / Stamp / Widget)
        //
        //  Every DECISION these make — the /DA parse and its inheritable lookups, the
        //  /Contents-then-/RC choice, the stamp palette, the placement arithmetic — lives in
        //  AnnotationFallbackModel, because the SVG exporter draws the same seven subtypes and
        //  must land them in the same place. What stays here is the emit step.
        // ------------------------------------------------------------------

        // Renders /Contents of a FreeText annotation inside /Rect, with optional
        // /BS border + /IC fill.  Text colour/size taken from /DA (default appearance).
        private static void RenderFreeTextFallback(
            PdfDictionary annot, PdfDocument doc, float rx0, float ry0, float rx1, float ry1,
            PdfRenderContext ctx, SKColor interiorColor, SKColor borderColor,
            float borderWidth, byte alphaB)
        {
            if (rx1 <= rx0 || ry1 <= ry0) return;

            // Border + interior fill (optional).
            if (interiorColor.Alpha > 0)
            {
                ctx.NewPath();
                ctx.AppendRect(rx0, ry0, rx1 - rx0, ry1 - ry0);
                ctx.FillPath(evenOdd: false, clearAfter: true);
            }
            if (borderColor.Alpha > 0 && borderWidth > 0f)
            {
                ctx.NewPath();
                ctx.AppendRect(rx0, ry0, rx1 - rx0, ry1 - ry0);
                ctx.StrokePath();
            }

            // /Contents, else /RC (an XHTML-lite rich-text payload) stripped to plain text, so a
            // rich-text-only annotation renders something instead of being dropped.
            string contents = AnnotationFallbackModel.FreeTextContents(annot, doc);
            if (string.IsNullOrEmpty(contents)) return;

            string da = (PdfResolve.Str(doc, annot, PdfName.DA))?.GetText();
            var (fontSize, textColor) = AnnotationFallbackModel.ParseDefaultAppearance(da);

            // Place the baseline one line-height down from the rect top, padded 2pts in.
            AnnotationFallbackModel.FreeTextBaseline(
                rx0, ry1, fontSize, out float baselineX, out float baselineY);

            ctx.GS.Tm = new float[] { fontSize, 0f, 0f, fontSize, baselineX, baselineY };
            ctx.GS.FillColor = textColor.WithAlpha(alphaB);
            ctx.GS.TextRenderMode = (int)TextRenderMode.Fill;
            var tf = SKTypeface.FromFamilyName(StandardFonts.Helvetica) ?? SKTypeface.Default;
            ctx.DrawText(contents, tf);
        }

        // Renders a Stamp annotation as a coloured outlined rectangle with the
        // stamp name as caption.  Colour is derived from /Name (Approved=green,
        // NotApproved/Confidential/Draft=red, everything else=blue).
        private static void RenderStampFallback(
            PdfDictionary annot, PdfDocument doc, float rx0, float ry0, float rx1, float ry1,
            PdfRenderContext ctx, SKColor borderColorOverride, float borderWidth, byte alphaB)
        {
            if (rx1 <= rx0 || ry1 <= ry0) return;

            string name = AnnotationFallbackModel.StampName(annot, doc);

            // If /C is explicitly set, honour it; otherwise pick by stamp name.
            SKColor stampColor = AnnotationFallbackModel.StampColor(name, borderColorOverride)
                                                        .WithAlpha(alphaB);

            // Rounded-rect approximation: regular rect at thicker stroke.
            ctx.GS.StrokeColor = stampColor;
            ctx.GS.LineWidth   = Math.Max(borderWidth, AnnotationFallbackModel.StampMinLineWidth);
            ctx.NewPath();
            ctx.AppendRect(rx0, ry0, rx1 - rx0, ry1 - ry0);
            ctx.StrokePath();

            // Centre the stamp name in the rect.
            if (!AnnotationFallbackModel.StampCaption(name, rx0, ry0, rx1, ry1,
                    out float fontSize, out float baselineX, out float baselineY))
                return;

            ctx.GS.Tm = new float[] { fontSize, 0f, 0f, fontSize, baselineX, baselineY };
            ctx.GS.FillColor = stampColor;
            ctx.GS.TextRenderMode = (int)TextRenderMode.Fill;
            var tf = SKTypeface.FromFamilyName(StandardFonts.Helvetica, SKFontStyle.Bold) ?? SKTypeface.Default;
            ctx.DrawText(name, tf);
        }

        // Renders a Widget (form-field) annotation without /AP.  The border is the widget's own
        // (/MK /BC at the /BS width); then for Tx (text) fields the /V value, and for Btn
        // (checkbox/radio) a centred tick when /V is non-Off.
        private static void RenderWidgetFallback(
            PdfDictionary annot, float rx0, float ry0, float rx1, float ry1,
            PdfRenderContext ctx, PdfDocument doc, float borderWidth, byte alphaB)
        {
            if (rx1 <= rx0 || ry1 <= ry0) return;

            // The frame, if the widget declares one. This stroked /C — a markup/link colour,
            // defaulting to black — at a width floored to 1 pt, so every no-/AP widget, an unsigned
            // signature field included, was boxed in black. The two rules that replace it (Table
            // 192's /MK /BC, and a width of 0 means no border) are AnnotationFallbackModel's,
            // shared with the SVG exporter. The dash, if any, is already on the graphics state.
            if (AnnotationFallbackModel.WidgetBorderColor(annot, doc, borderWidth) is SKColor frame)
            {
                ctx.GS.LineWidth   = borderWidth;
                ctx.GS.StrokeColor = frame.WithAlpha(alphaB);
                ctx.NewPath();
                ctx.AppendRect(rx0, ry0, rx1 - rx0, ry1 - ry0);
                ctx.StrokePath();
            }

            // Field type — /FT is inheritable through the /Parent chain (Table 220).
            //
            // A widget whose type will not resolve draws its border and nothing else. `ft ??= "Tx"`
            // would turn "I could not determine the type" into "the type is text" with no
            // diagnostic — a check box whose /FT is out of reach would typeset the word "Yes" where a
            // tick belongs, and raising the cap alone would only move the boundary of a silent lie.
            string ft = (AnnotationFallbackModel.ResolveWidgetEntry(
                annot, doc, PdfName.FT, o => o is PdfName) as PdfName)?.Value;
            if (ft == null)
            {
                RenderDiagnostics.Report(
                    RenderDiagnosticKind.FormFieldTypeUnresolved,
                    "no /FT was reachable from this widget within "
                    + PdfLimits.MaxFieldInheritanceDepth + " /Parent hops; "
                    + "the field's value was not drawn");
                return;
            }

            // Current value — /V on the widget, or inherited from parent — as the widget shows it: a
            // password field's characters as asterisks (Table 229 bit 14).
            string valueText = AnnotationFallbackModel.DisplayedWidgetValue(annot, doc);
            if (string.IsNullOrEmpty(valueText)) return;

            string da = AnnotationFallbackModel.InheritedDefaultAppearance(annot, doc);
            var (daSize, textColor) = AnnotationFallbackModel.ParseDefaultAppearance(da);

            // Table 192 /MK /R: the value is placed in the rectangle turned back upright — a box of
            // the swapped dimensions for 90 and 270 — and each placement is turned onto the
            // rectangle by the same coefficients a generated appearance writes as its /Matrix
            // (Base.Operations.Forms.WidgetRotation), so this fallback and a written appearance agree.
            float rw = rx1 - rx0, rh = ry1 - ry0;
            int rotation = Base.Operations.Forms.WidgetRotation.Read(annot, doc.ResolveObject);
            var (boxW, boxH) = Base.Operations.Forms.WidgetRotation.LayoutBox(rotation, rw, rh);
            double[] turn = Base.Operations.Forms.WidgetRotation.Coefficients(rotation, rw, rh);
            // Auto-size when /DA specified 0 → use the box height.
            float fontSize = AnnotationFallbackModel.WidgetFontSize(daSize, 0f, (float)boxH);

            if (ft == "Btn")
            {
                // Checkbox / radio: draw a tick when /V is "On" (any non-Off value).
                string v = valueText;
                if (v == "Off" || string.IsNullOrEmpty(v)) return;
                AnnotationFallbackModel.CheckMarkPlacement(0f, 0f, (float)boxW, (float)boxH,
                    out float markSize, out float markX, out float markY);
                ctx.GS.Tm = TurnedTextMatrix(turn, markSize, rx0, ry0,
                    Base.Operations.Forms.WidgetRotation.ToRectangle(rotation, rw, rh, markX, markY));
                ctx.GS.FillColor = textColor.WithAlpha(alphaB);
                ctx.GS.TextRenderMode = (int)TextRenderMode.Fill;
                var tfB = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
                ctx.DrawText(AnnotationFallbackModel.CheckMark, tfB);
                return;
            }

            // Text / Choice field: place baseline 2pt inset from left, vertically centred.
            //
            // /Q is inheritable (§12.7.3.3), with a form-wide default on /AcroForm, and this draws
            // left-aligned regardless, because the fallback measures no text width. Reported rather than dropped — a wrong alignment
            // looks exactly like a file that never asked. Only 1 (centre) and 2 (right) are
            // reported: /Q 0 IS left-aligned, and a channel that fires on conformant input is one a
            // caller learns to ignore.
            int quadding = AnnotationFallbackModel.InheritedQuadding(annot, doc) ?? 0;
            if (quadding != 0)
            {
                RenderDiagnostics.Report(
                    RenderDiagnosticKind.FormFieldQuaddingIgnored,
                    "/Q " + quadding + " asked for "
                    + (quadding == 1 ? "centred" : "right-aligned")
                    + " text; the no-/AP fallback drew it left-aligned");
            }

            AnnotationFallbackModel.WidgetValueBaseline(
                0f, 0f, (float)boxH, fontSize, out float baselineXT, out float baselineYT);

            ctx.GS.Tm = TurnedTextMatrix(turn, fontSize, rx0, ry0,
                Base.Operations.Forms.WidgetRotation.ToRectangle(rotation, rw, rh, baselineXT, baselineYT));
            ctx.GS.FillColor = textColor.WithAlpha(alphaB);
            ctx.GS.TextRenderMode = (int)TextRenderMode.Fill;
            var tf = SKTypeface.FromFamilyName(StandardFonts.Helvetica) ?? SKTypeface.Default;
            ctx.DrawText(valueText, tf);
        }

        /// <summary>
        /// A text matrix of <paramref name="size"/> carrying <paramref name="turn"/>'s rotation
        /// (its first four coefficients), placed at the rectangle's origin plus a rectangle-relative
        /// point. The identity turn gives exactly the untransformed matrix.
        /// </summary>
        private static float[] TurnedTextMatrix(
            double[] turn, float size, float rx0, float ry0, (double X, double Y) at)
            => new[]
            {
                (float)(turn[0] * size), (float)(turn[1] * size),
                (float)(turn[2] * size), (float)(turn[3] * size),
                rx0 + (float)at.X, ry0 + (float)at.Y,
            };

        // Renders a dashed-outline rectangle with a diagonal cross and a "3D"
        // caption in the centre.  The 3D content (U3D / PRC) itself needs a full
        // 3D engine to visualise — out of scope for a static PDF renderer — so
        // we at least flag that content was present rather than silently
        // dropping it.  Mirrors the PostScript-XObject placeholder pattern.
        private static void Render3DAnnotationPlaceholder(
            float rx0, float ry0, float rx1, float ry1,
            PdfRenderContext ctx, SKColor borderColorOverride, byte alphaB)
        {
            if (rx1 <= rx0 || ry1 <= ry0) return;

            // Use the annotation's /C border colour when set, else a soft blue so
            // the placeholder reads as "3D viewport".
            SKColor outline = AnnotationFallbackModel.ThreeDOutlineColor(borderColorOverride)
                                                     .WithAlpha(alphaB);

            ctx.GS.StrokeColor = outline;
            ctx.GS.FillColor   = outline.WithAlpha((byte)(alphaB / 4));
            ctx.GS.LineWidth   = 1f;
            ctx.GS.DashPattern = AnnotationFallbackModel.ThreeDDashPattern();

            float x = Math.Min(rx0, rx1), y = Math.Min(ry0, ry1);
            float w = Math.Abs(rx1 - rx0), h = Math.Abs(ry1 - ry0);

            ctx.NewPath();
            ctx.AppendRect(x, y, w, h);
            ctx.FillPath(evenOdd: false, clearAfter: false);
            ctx.StrokePath();

            ctx.NewPath();
            ctx.MoveTo(x, y);         ctx.LineTo(x + w, y + h);
            ctx.MoveTo(x, y + h);     ctx.LineTo(x + w, y);
            ctx.StrokePath();

            // "3D" caption in the centre.
            if (!AnnotationFallbackModel.ThreeDCaptionPlacement(x, y, w, h,
                    out float fontSize, out float baselineX, out float baselineY))
                return;

            // Clear dash so the text isn't dashed.
            ctx.GS.DashPattern = null;
            ctx.GS.FillColor = outline;
            ctx.GS.TextRenderMode = (int)TextRenderMode.Fill;
            ctx.GS.Tm = new float[] { fontSize, 0f, 0f, fontSize, baselineX, baselineY };
            var tf = SKTypeface.FromFamilyName(StandardFonts.Helvetica, SKFontStyle.Bold) ?? SKTypeface.Default;
            ctx.DrawText(AnnotationFallbackModel.ThreeDCaption, tf);
        }

        /// <summary>
        /// Renders markup annotation geometry from /QuadPoints.
        /// Each group of 8 floats is [x1 y1 x2 y2 x3 y3 x4 y4] (four corners of one quad).
        /// <paramref name="lineYFactor"/> controls where the horizontal line is drawn:
        /// 0 = bottom edge, 0.5 = centre, 1 = top edge.
        /// </summary>
        /// <returns><c>true</c> if at least one quad was rendered; <c>false</c> if /QuadPoints was absent or empty.</returns>
        private static bool RenderQuadPoints(
            PdfDictionary annot, PdfDocument doc,
            PdfRenderContext ctx,
            bool fill, bool stroke,
            float lineYFactor = 0f)
        {
            // Read once, for both renderers: AnnotationQuadPoints owns the sentinel guard and the
            // all-four-corner extents, so the SVG side derives them the same way.
            var quads = AnnotationQuadPoints.Read(doc, annot, out bool declared);
            if (!declared) return false;

            foreach (var q in quads)
            {
                if (fill)
                {
                    ctx.NewPath();
                    ctx.MoveTo(q.X0, q.Y0);
                    ctx.LineTo(q.X1, q.Y1);
                    ctx.LineTo(q.X3, q.Y3);
                    ctx.LineTo(q.X2, q.Y2);
                    ctx.ClosePath();
                    ctx.FillPath(evenOdd: false, clearAfter: true);
                }

                if (stroke)
                {
                    float yLine = q.RuleY(lineYFactor);
                    ctx.NewPath();
                    ctx.MoveTo(q.Left,  yLine);
                    ctx.LineTo(q.Right, yLine);
                    ctx.StrokePath();
                }
            }
            return true;
        }

        /// <summary>
        /// Reads an annotation colour array (/C or /IC); the rule is
        /// <see cref="AnnotationFallbackModel.AnnotationColor"/>, which the annotation appearance
        /// builder also reaches.
        /// </summary>
        private static SKColor ReadAnnotColor(
            PdfDictionary annot, PdfDocument doc, PdfName key, SKColor fallback)
            => AnnotationFallbackModel.AnnotationColor(annot, doc, key, fallback);

        /// <summary>Appends an ellipse inscribed in [x0,y0]-[x1,y1] via four cubic Bézier arcs.</summary>
        private static void AppendEllipse(PdfRenderContext ctx, float x0, float y0, float x1, float y1)
        {
            float cx = (x0 + x1) * 0.5f, cy = (y0 + y1) * 0.5f;
            float rx = (x1 - x0) * 0.5f, ry = (y1 - y0) * 0.5f;
            const float k = 0.5522847498f; // magic constant for circle approximation
            float kx = k * rx, ky = k * ry;
            ctx.MoveTo(cx,      cy + ry);
            ctx.CurveTo(cx - kx, cy + ry, cx - rx, cy + ky, cx - rx, cy);
            ctx.CurveTo(cx - rx, cy - ky, cx - kx, cy - ry, cx,      cy - ry);
            ctx.CurveTo(cx + kx, cy - ry, cx + rx, cy - ky, cx + rx, cy);
            ctx.CurveTo(cx + rx, cy + ky, cx + kx, cy + ry, cx,      cy + ry);
            ctx.ClosePath();
        }

        // ----------------------------------------------------------------
        //  Helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Builds the set of OCG object numbers that are hidden according to the document's
        /// /OCProperties default configuration (/D), per PDF §8.11.4.3.
        /// Returns null when the document has no OCG configuration (all content visible).
        /// </summary>
        /// <remarks>
        /// <para>Catalog resolution plus a call: the §8.11.4.3 rule itself lives in
        /// <c>Base.Operations.Structure.PdfOptionalContentRefs.BuildHiddenSet</c>, because
        /// <c>PdfOcgManager.CollectOcgs</c> — behind <c>PdfOperations.ListLayers</c> — asks the
        /// same question and must answer it the same way: reading <c>/OFF</c> and never
        /// <c>/BaseState</c>, a <c>/BaseState /OFF</c> document would report every layer visible
        /// while this method hides them all. Core is the leaf both sides already reference;
        /// having <c>PdfOcgManager</c> call <b>this</b> static instead would have been the sideways
        /// call into a file with a type initializer that CLAUDE.md forbids.</para>
        ///
        /// <para>Every entry read is resolved through <see cref="PdfDocument.ResolveObject"/>.
        /// <c>PdfDictionary</c>'s indexer returns the raw stored value, so an <c>as PdfDictionary</c>
        /// on an indirect reference yields <c>null</c> — and common producers write the default config
        /// indirectly (<c>/OCProperties &lt;&lt; /OCGs [...] /D 12 0 R &gt;&gt;</c>). Reading /D
        /// unresolved therefore disabled layer hiding on the majority of real-world PDFs.</para>
        /// </remarks>
        internal static HashSet<int> BuildHiddenOcgSet(PdfDocument doc)
        {
            var catalog = doc?.Catalog?.Dictionary;
            if (catalog == null) return null;

            return Base.Operations.Structure.PdfOptionalContentRefs.BuildHiddenSet(
                doc, Resolve<PdfDictionary>(doc, catalog[PdfName.OCProperties]));
        }

        /// <summary>
        /// Axis-aligned bounding box of the rectangle (x0,y0)-(x1,y1) after transformation by
        /// the 6-element PDF matrix <paramref name="m"/> — step 1/2 of §12.5.5 Algorithm 8.1.
        /// All four corners are mapped, so a rotating or skewing /Matrix widens the box
        /// correctly instead of being ignored.
        /// </summary>
        internal static void TransformedBBox(
            float[] m, float x0, float y0, float x1, float y1,
            out float outX0, out float outY0, out float outX1, out float outY1)
        {
            // One implementation, in the PdfMatrix leaf — shared with the SVG renderer (which
            // already called this) and with ContentStreamInterpreter, which had the same four-corner
            // walk written as a local closure. It lives in PdfMatrix rather than here because the
            // interpreter calling a static on THIS type would give it a type-initialization edge on
            // the renderer; sharing downward into a leaf is what avoids that.
            PdfMatrix.TransformedBounds(m, x0, y0, x1, y1, out outX0, out outY0, out outX1, out outY1);
        }

        /// <summary>Resolves <paramref name="obj"/> through the document and casts it to
        /// <typeparamref name="T"/>, falling back to a direct cast when it is not a reference.</summary>
        private static T Resolve<T>(PdfDocument doc, PdfObject obj) where T : PdfObject
            => (doc != null ? doc.ResolveObject(obj) as T : null) ?? obj as T;

        /// <summary>
        /// Element <paramref name="idx"/> of a box / point array as float, 0 when absent.
        /// Thin alias over the shared operand coercion so this renderer, the SVG renderer and
        /// the graphics extractor cannot drift apart on out-of-range handling.
        /// </summary>
        private static float GetBoxFloat(PdfArray arr, int idx)
            => Core.Text.ContentOperand.ArrayFloat(arr, idx);

        /// <summary>
        /// Element <paramref name="idx"/> of an annotation <c>/Rect</c>, <b>resolving</b> the
        /// element itself. Same alias role as <see cref="GetBoxFloat"/>, one rule stronger.
        /// </summary>
        /// <remarks>
        /// A <c>/Rect</c> lives in a dictionary, where §7.3.10 permits an indirect element; a
        /// content-stream operand — which is what <c>ContentOperand.ArrayFloat</c> reads and
        /// correctly does not resolve — cannot be one. Reading the first through the second yields
        /// <c>0</c> for an indirect coordinate, which is a plausible number rather than a
        /// failure, and it put the drawn annotation 100 pt away from the rectangle
        /// <c>PdfLinkExtractor</c> reports as clickable for the same file. The SVG exporter reads
        /// it through the identical helper so the two cannot diverge again.
        /// </remarks>
        private static float RectFloat(PdfDocument doc, PdfArray arr, int idx)
            => PdfResolve.ArrayFloat(doc, arr, idx);

        /// <summary>
        /// The page's <c>/UserUnit</c> (§14.11.1), sanitised — the single decision point both
        /// renderers and the tile path use.
        /// </summary>
        /// <remarks>
        /// <para><b>Internal and shared for the same reason as <see cref="ResolveRenderBox"/>.</b>
        /// The SVG exporter, <see cref="MeasurePage"/> and <see cref="RenderPageTileToBitmap"/> read
        /// the same attacker-controlled entry, and <c>PdfPage.UserUnit</c> validates nothing.
        /// Unguarded, a page carrying <c>/UserUnit 0</c> would <b>render as a 1x1-pixel image</b>
        /// while exporting at full size: the device scale would go to zero,
        /// <c>Math.Max(1, Ceiling(w * 0))</c> collapse both extents to one pixel, and
        /// <see cref="ClampRenderDimensions"/> never fire because 1x1 is under every limit — so the
        /// page would vanish with no diagnostic. A negative value would do the same, and
        /// <see cref="GetRenderSize(Stream,int,int,PdfRenderOptions)"/> return
        /// <c>EffectiveDpi = NaN</c> from the division by it.</para>
        /// <para>The refusal is reported rather than silently applied: at the pixel level "the
        /// file asked for a scale we ignored" is indistinguishable from "the file never asked".</para>
        /// </remarks>
        internal static float ResolveUserUnit(PdfPage page)
        {
            float userUnit = (float)(page?.UserUnit ?? 1.0);
            if (userUnit > 0f && !float.IsInfinity(userUnit)) return userUnit;   // NaN fails this too

            RenderDiagnostics.Report(RenderDiagnosticKind.RenderParameterIgnored,
                $"/UserUnit {userUnit} is not a usable positive scale (§14.11.1); rendering at 1.0.");
            return 1f;
        }

        /// <summary>
        /// Picks the page box to render based on <see cref="PdfRenderOptions"/>
        /// and the document's /ViewerPreferences.  Precedence:
        ///   1. Explicit UseTrimBox / UseBleedBox / UseArtBox flag on <paramref name="options"/>.
        ///   2. ViewerPreferences /PrintArea (Print intent) or /ViewArea (Screen intent),
        ///      resolved to the named page box.
        ///   3. Fallback: CropBox (else MediaBox).
        /// </summary>
        /// <remarks>
        /// <b>Internal, not private, because the SVG exporter is the other renderer.</b> Reading
        /// <c>CropBox ?? MediaBox</c> directly, it would ignore <see cref="PdfRenderOptions.UseTrimBox"/>
        /// and friends, the document's <c>/ViewerPreferences</c> box choice, and a <c>/CropBox</c>
        /// larger than the <c>/MediaBox</c> (§14.11.2) — three ways one file would export at a
        /// different size from the size it renders.
        /// </remarks>
        internal static PdfArray ResolveRenderBox(PdfPage page, PdfDocument doc, PdfRenderOptions options)
        {
            options ??= PdfRenderOptions.Default;

            // Explicit box flags win.
            if (options.UseTrimBox)   return ClipToMedia(page.TrimBox  ?? page.CropBox, page);
            if (options.UseBleedBox)  return ClipToMedia(page.BleedBox ?? page.CropBox, page);
            if (options.UseArtBox)    return ClipToMedia(page.ArtBox   ?? page.CropBox, page);

            // Next, honour /ViewerPreferences for the current intent.
            var prefs = doc?.Catalog?.GetViewerPreferences();
            string prefName = options.Intent == RenderIntent.Print
                ? prefs?.PrintArea
                : prefs?.ViewArea;
            if (!string.IsNullOrEmpty(prefName))
            {
                var resolved = BoxByName(page, prefName);
                if (resolved != null) return ClipToMedia(resolved, page);
            }

            // Default: CropBox, fall back to MediaBox.
            return ClipToMedia(page.CropBox, page);
        }

        /// <summary>
        /// Intersects a sub-box with the page's <c>/MediaBox</c>, per §14.11.2: every other boundary
        /// box "shall be contained within" it, and a viewer that finds one extending outside is to
        /// use the intersection. Returns the MediaBox when <paramref name="box"/> is null, and
        /// <paramref name="box"/> unchanged when there is no MediaBox to clip against.
        /// </summary>
        /// <remarks>
        /// Both rectangles are normalised first: §7.9.5 lets either corner pair come first, so a
        /// straight min/max on the stored order can invert the result.
        /// </remarks>
        private static PdfArray ClipToMedia(PdfArray box, PdfPage page)
        {
            var media = page.MediaBox;
            if (box == null) return media;
            if (media == null || box.Count < 4 || media.Count < 4) return box;

            float bx0 = Math.Min(GetBoxFloat(box, 0), GetBoxFloat(box, 2));
            float by0 = Math.Min(GetBoxFloat(box, 1), GetBoxFloat(box, 3));
            float bx1 = Math.Max(GetBoxFloat(box, 0), GetBoxFloat(box, 2));
            float by1 = Math.Max(GetBoxFloat(box, 1), GetBoxFloat(box, 3));

            float mx0 = Math.Min(GetBoxFloat(media, 0), GetBoxFloat(media, 2));
            float my0 = Math.Min(GetBoxFloat(media, 1), GetBoxFloat(media, 3));
            float mx1 = Math.Max(GetBoxFloat(media, 0), GetBoxFloat(media, 2));
            float my1 = Math.Max(GetBoxFloat(media, 1), GetBoxFloat(media, 3));

            float x0 = Math.Max(bx0, mx0), y0 = Math.Max(by0, my0);
            float x1 = Math.Min(bx1, mx1), y1 = Math.Min(by1, my1);

            // Disjoint (or degenerate) boxes mean the file is self-contradictory; the MediaBox is
            // the one rectangle the spec guarantees, so fall back to it rather than to an empty page.
            if (!(x1 > x0) || !(y1 > y0)) return media;

            var clipped = new PdfArray();
            clipped.Add(new PdfNumber(x0));
            clipped.Add(new PdfNumber(y0));
            clipped.Add(new PdfNumber(x1));
            clipped.Add(new PdfNumber(y1));
            return clipped;
        }

        private static PdfArray BoxByName(PdfPage page, string name) => name switch
        {
            "MediaBox" => page.MediaBox,
            "CropBox"  => page.CropBox,
            "BleedBox" => page.BleedBox,
            "TrimBox"  => page.TrimBox,
            "ArtBox"   => page.ArtBox,
            _          => null
        };

        // ----------------------------------------------------------------
        //  R9: Per-pixel ink-coverage extraction (Separation / DeviceN spot inks)
        // ----------------------------------------------------------------

        /// <summary>
        /// Renders <paramref name="pageIndex"/> at <paramref name="dpi"/> and returns
        /// an <see cref="InkAccumulator"/> whose per-channel coverage maps reflect
        /// every Separation and DeviceN ink deposited during rendering.
        /// The accumulator's Width × Height match the rendered bitmap dimensions.
        /// </summary>
        /// <remarks>
        /// <b>This entry point opened the document directly</b> and so never decrypted, in defiance
        /// of the rule stated on <see cref="EnsureDecryptedStream(Stream,string,out bool)"/> that every stream entry point
        /// must go through it — and it took no password at all, so an encrypted document could not
        /// be measured even by a caller who had one. The failure was the silent kind: an encrypted
        /// content stream does not inflate, so nothing was interpreted and the accumulator came back
        /// with <b>no channels</b>, which is exactly what a page carrying no spot colour returns.
        /// A prepress caller asking "which inks does this page use?" was told "none".
        /// </remarks>
        public static InkAccumulator ExtractInkCoverage(
            Stream input, int pageIndex, float dpi = PdfUnits.PointsPerInch)
            => ExtractInkCoverage(input, pageIndex, null, dpi);

        /// <summary>
        /// As <see cref="ExtractInkCoverage(Stream, int, float)"/>, with render options.
        /// </summary>
        /// <param name="renderOptions">
        /// <see cref="PdfRenderOptions.Password"/> and the page-box selection
        /// (<see cref="PdfRenderOptions.UseTrimBox"/> and friends, plus the document's
        /// <c>/ViewerPreferences</c>) are honoured, because the geometry comes from the same
        /// <c>MeasurePage</c> the renderer uses. This path encodes nothing, so the output-format
        /// options have nothing to act on here.
        /// </param>
        /// <remarks>
        /// A separate overload rather than a fourth optional parameter, which is how this shipped
        /// and was a binary break — an optional-argument default is baked in at the call site, so
        /// widening a shipped member breaks an already-compiled consumer at run time while still
        /// compiling from source.
        /// </remarks>
        /// <param name="input">Stream holding the PDF. Seek-normalised and decrypted internally.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="dpi">
        /// Resolution of the coverage map in dots per inch. This sets the accumulator's cell size,
        /// so it trades map resolution against memory rather than output quality.
        /// </param>
        public static InkAccumulator ExtractInkCoverage(
            Stream input, int pageIndex, PdfRenderOptions renderOptions,
            float dpi = PdfUnits.PointsPerInch)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            // The one float dpi on the surface: 0, a negative or NaN would go through
            // MeasurePage's Math.Max(1, …) and come back as a 1×1 map with no channels — the same
            // answer as a page with no spot ink.
            NumericArguments.RequirePositive(dpi, nameof(dpi));
            return WithDecryptedPage(input, pageIndex, renderOptions?.Password, (page, doc) =>
            {
                // The same geometry the renderer uses, for the reasons on MeasurePage. Computing it
                // here instead cost this path four decisions: an inverted /MediaBox (§7.9.5) came
                // out negative, /UserUnit was ignored, ClampRenderDimensions never ran — so a
                // hostile page box allocated an unbounded accumulator AND overflowed
                // InkAccumulator's `width * height` — and the box origin was computed and then
                // never applied, so every page whose box does not start at (0,0) attributed its
                // ink to the wrong pixels and dropped whatever fell off the far edge.
                var m = MeasurePage(page, doc, dpi, renderOptions);
                int pixWidth = m.RawWidthPixels, pixHeight = m.RawHeightPixels;

                var acc = new InkAccumulator(pixWidth, pixHeight);

                using (var bitmap = new SKBitmap(pixWidth, pixHeight, SKColorType.Rgba8888, SKAlphaType.Premul))
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Clear(SKColors.White);
                    using var fontCache = new FontCache();
                    using var renderCtx = new PdfRenderContext(canvas, m.PointsWidth, m.PointsHeight,
                                                               m.Scale, bitmap);
                    renderCtx.InkAccumulator = acc;

                    // RenderPageToBitmap does exactly this; leaving it out is what shifted the map.
                    if (m.OriginX != 0f || m.OriginY != 0f)
                        renderCtx.ConcatCTM(new float[] { 1, 0, 0, 1, -m.OriginX, -m.OriginY });
                    renderCtx.GS.PatternBase = (float[])renderCtx.GS.CTM.Clone();

                    var resources = page.Resources;
                    byte[] content = page.GetContentStreamData();
                    if (content != null && content.Length > 0)
                    {
                        var interpreter = new ContentStreamInterpreter(renderCtx, doc, resources, fontCache)
                            { FormBudget = new RenderBudget() };
                        interpreter.HiddenOcgs = BuildHiddenOcgSet(doc);
                        interpreter.Execute(content);
                    }
                }
                return acc;
            });
        }

        /// <summary>
        /// The bound each top-level operation of <paramref name="page"/>'s content paints — the union
        /// of every paint call it makes, a form's, a pattern's and a soft mask's included — in default
        /// user space, from one render through <see cref="ContentStreamInterpreter.BuildDisplayList"/>;
        /// and the operation list the indices refer to. An operation that paints nothing has no entry.
        /// </summary>
        /// <remarks>
        /// Conservative, as the display list is: a bound is the geometry's, never narrowed by the clip.
        /// No optional content is hidden, so a bound covers what any configuration shows. A nested
        /// form runs in an interpreter of its own on the same context, so what it paints is reported
        /// while the outer interpreter is still on the <c>Do</c> that drew it.
        /// </remarks>
        internal static Dictionary<int, SKRect> MeasureOperations(PdfPage page, PdfDocument doc,
            PdfRenderOptions options, out IReadOnlyList<Core.Text.ContentStreamOperation> ops)
            => MeasureOperations(page, doc, options, out ops, null);

        /// <summary>
        /// <see cref="MeasureOperations(PdfPage, PdfDocument, PdfRenderOptions, out IReadOnlyList{Core.Text.ContentStreamOperation})"/>,
        /// and into <paramref name="textAdvances"/>, when given, how far each top-level text-showing
        /// operation moved the pen (<see cref="ShownTextAdvance"/>) — one that did not run, hidden by
        /// optional content, moved it nowhere.
        /// </summary>
        internal static Dictionary<int, SKRect> MeasureOperations(PdfPage page, PdfDocument doc,
            PdfRenderOptions options, out IReadOnlyList<Core.Text.ContentStreamOperation> ops,
            Dictionary<int, ShownTextAdvance> textAdvances)
            => MeasureOperations(page, doc, options, out ops, textAdvances, null, hideOptionalContent: false);

        /// <summary>
        /// As above, and into <paramref name="placements"/>, when given, where each top-level
        /// operation that paints stood (<see cref="OperationPlacement"/>): a text-showing operation,
        /// a path's paint, <c>sh</c>, an inline image, a <c>Do</c>. With
        /// <paramref name="hideOptionalContent"/>, what the document's default configuration hides is
        /// not run, so it has no bound: the answer is what a reader shows, not what any
        /// configuration could.
        /// </summary>
        internal static Dictionary<int, SKRect> MeasureOperations(PdfPage page, PdfDocument doc,
            PdfRenderOptions options, out IReadOnlyList<Core.Text.ContentStreamOperation> ops,
            Dictionary<int, ShownTextAdvance> textAdvances, Dictionary<int, OperationPlacement> placements,
            bool hideOptionalContent)
        {
            var bounds = new Dictionary<int, SKRect>();
            ops = Array.Empty<Core.Text.ContentStreamOperation>();
            byte[] content = page.GetContentStreamData();
            if (content == null || content.Length == 0) return bounds;

            var m = MeasurePage(page, doc, PdfUnits.PointsPerInch, options);
            using var bitmap = new SKBitmap(m.RawWidthPixels, m.RawHeightPixels, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            using var fontCache = new FontCache();
            using var renderCtx = new PdfRenderContext(canvas, m.PointsWidth, m.PointsHeight, m.Scale, bitmap);
            if (m.OriginX != 0f || m.OriginY != 0f)
                renderCtx.ConcatCTM(new float[] { 1, 0, 0, 1, -m.OriginX, -m.OriginY });
            renderCtx.GS.PatternBase = (float[])renderCtx.GS.CTM.Clone();

            var interpreter = new ContentStreamInterpreter(renderCtx, doc, page.Resources, fontCache)
                { FormBudget = new RenderBudget() };
            if (hideOptionalContent) interpreter.HiddenOcgs = BuildHiddenOcgSet(doc);
            var list = renderCtx.GetOrParseOperations(content);
            ops = list;
            if (textAdvances != null || placements != null)
            {
                // The pen before an operation is the pen after the one before it. A ' or a " moves
                // to the next line first, so what it shows starts at the line matrix it leaves.
                float[] penBefore = (float[])renderCtx.GS.Tm.Clone();
                // The context's matrix leads to the render box's corner; default user space is the page's own.
                float[] toDefaultUserSpace = { 1, 0, 0, 1, m.OriginX, m.OriginY };
                // Text in a substituted face moves this renderer's pen by the substitute's measure.
                // What it moved beyond the font's own widths is kept apart, a line at a time, so
                // that what is recorded is the advance the file defines (§9.4.4), on any host.
                float beyondOnLine = 0f;
                interpreter.OperationExecuted = i =>
                {
                    GraphicsState gs = renderCtx.GS;
                    string op = list[i].Operator;
                    bool fromLineStart = op == PdfOperators.NextLineShowText || op == PdfOperators.NextLineSetSpacingShowText;
                    bool shows = fromLineStart || op == PdfOperators.ShowText || op == PdfOperators.ShowTextArray;
                    float[] penStart = fromLineStart ? gs.Tlm : penBefore;
                    float beyond = interpreter.TakeAdvanceBeyondWidths();
                    // Each of these puts the pen back on the line matrix, where nothing is beyond.
                    if (fromLineStart || op == PdfOperators.BeginText || op == PdfOperators.SetTextMatrix || op == PdfOperators.TextMove
                        || op == PdfOperators.TextMoveSetLeading || op == PdfOperators.NextLine)
                        beyondOnLine = 0f;
                    float beyondAtStart = beyondOnLine;
                    beyondOnLine += beyond;

                    if (shows && textAdvances != null)
                        textAdvances[i] = ShownTextAdvance.Between(penStart, gs.Tm, gs.FontSize, gs.HorizScaling).Less(beyond);
                    if (placements != null && (shows || op == PdfOperators.InvokeXObject || op == PdfOperators.BeginInlineImage
                                               || op == PdfOperators.ShadingFill || Operations.Documents.ContentOperationRemoval.PaintsPath(op)))
                        placements[i] = new OperationPlacement
                        {
                            Ctm = PdfMatrix.Compose(gs.CTM, toDefaultUserSpace),
                            PenStart = shows ? (float[])penStart.Clone() : null,
                            LineAfter = shows ? (float[])gs.Tlm.Clone() : null,
                            PenAfter = shows ? (float[])gs.Tm.Clone() : null,
                            BeyondWidthsAtStart = beyondAtStart,
                            BeyondWidthsAfter = beyondOnLine,
                            LineWidth = gs.LineWidth,
                            FontSize = gs.FontSize,
                            HorizontalScaling = gs.HorizScaling,
                        };
                    Array.Copy(gs.Tm, penBefore, 6);
                };
            }
            foreach (var entry in interpreter.BuildDisplayList(list))
            {
                SKRect r = entry.PageBounds;
                if (entry.OpIndex < 0 || !IsFinite(r.Left) || !IsFinite(r.Top) || !IsFinite(r.Right) || !IsFinite(r.Bottom))
                    continue;
                // Page points, Y down from the box's top-left (DisplayListEntry.PageBounds), to user space.
                var user = new SKRect(m.OriginX + r.Left, m.OriginY + m.PointsHeight - r.Bottom,
                                      m.OriginX + r.Right, m.OriginY + m.PointsHeight - r.Top);
                bounds[entry.OpIndex] = bounds.TryGetValue(entry.OpIndex, out SKRect had) ? SKRect.Union(had, user) : user;
            }
            return bounds;

            static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        }

        /// <summary>
        /// The whole stream as bytes, through <see cref="StreamBytes.ReadAll"/> — the one
        /// implementation of that.
        /// </summary>
        /// <remarks>
        /// The shared reader rewinds only what can be rewound, so a caller handing this a network
        /// or compressed stream gets its bytes from where it is; an unconditional
        /// <c>Position = 0</c> would <b>throw</b> on such a stream.
        /// </remarks>
        private static byte[] ReadAllBytes(Stream s) => StreamBytes.ReadAll(s);
    }
}
