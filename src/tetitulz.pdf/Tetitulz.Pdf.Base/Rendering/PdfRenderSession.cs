// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Core.Document;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// An open document plus the state that is worth reusing across renders of it — the parsed
    /// document, a font cache, the parsed-operation cache and the hidden optional-content set.
    /// </summary>
    /// <remarks>
    /// <para>The one-shot entry points (<see cref="PdfPageRenderer.RenderPage(Stream,int,int,PdfRenderOptions)"/>
    /// and friends) parse the document, build a <see cref="FontCache"/> and compute the hidden-OCG
    /// set on every call, because each call is self-contained. That is right for rendering a page
    /// once and wrong for the two shapes that dominate interactive use:</para>
    /// <list type="bullet">
    ///   <item><description><b>Tiles.</b> A 4x4 grid measured <b>4.1x the time and 12.4x the
    ///   allocation</b> of the same page rendered whole, and roughly two-thirds of that is
    ///   re-tokenising the page's content stream once per tile.</description></item>
    ///   <item><description><b>A viewer.</b> Scrolling or zooming re-renders the same pages of the
    ///   same document repeatedly, re-parsing it every time.</description></item>
    /// </list>
    ///
    /// <para><b>Not thread-safe, deliberately.</b> The parser's object cache is a plain
    /// <c>Dictionary</c>, so one session belongs to one thread. Render pages in parallel by giving
    /// each thread its own session (which is what <c>RenderAllPages</c> effectively does), or
    /// synchronise externally. Making the parser concurrency-safe is tracked separately; pretending
    /// this type is safe would be the worse of the two options.</para>
    ///
    /// <para><b>Lifetime is a real trade, not a free win.</b> Everything the session holds stays
    /// alive until <see cref="Dispose"/>: the parsed document, every typeface the font cache
    /// resolved, parsed operations up to <see cref="ContentOperationCache.MaxBytes"/>, and each
    /// rendered page's decoded content up to
    /// <see cref="SharedState.DefaultMaxPageContentBytes"/>. For a one-shot render that is worse
    /// than the existing entry points, which is why they are unchanged and remain the
    /// default.</para>
    ///
    /// <para><b>Never let a typeface out of the session.</b> <see cref="FontCache.Dispose"/>
    /// disposes every typeface it handed out, so a caller holding one past
    /// <see cref="Dispose"/> reads freed memory. On Windows that usually survives because Skia's
    /// font manager holds a second reference; on Linux a <c>FromData</c> typeface has no second
    /// owner and it segfaults the process. This has happened here before.</para>
    /// </remarks>
    public sealed class PdfRenderSession : IDisposable
    {
        /// <summary>The reusable state, handed to the renderer's internals as one parameter.</summary>
        internal sealed class SharedState
        {
            /// <summary>
            /// Default ceiling on memoised page content — see
            /// <see cref="PdfPageRenderer.PageContent"/>. Matched to
            /// <see cref="ContentOperationCache.MaxBytes"/> because the two hold the same content
            /// in two forms, and the parsed form is the larger of them.
            /// </summary>
            internal const long DefaultMaxPageContentBytes = ContentOperationCache.MaxBytes;

            public FontCache Fonts;
            public ContentOperationCache Operations;
            public HashSet<int> HiddenOcgs;

            /// <summary>
            /// Decoded page content, memoised for the session's life so that the parsed-operation
            /// cache — which keys on array reference identity — can hit across tiles. Keyed on the
            /// <see cref="PdfPage"/> instance, which is reference-compared: <c>PdfPage</c> does not
            /// override <c>Equals</c>, and <c>PdfDocument.Pages</c> hands out the same instances for
            /// the session's whole life. See <see cref="PdfPageRenderer.PageContent"/> for why it
            /// exists at all.
            /// </summary>
            public Dictionary<PdfPage, byte[]> PageContent;

            /// <summary>Bytes charged to <see cref="PageContent"/>.</summary>
            public long PageContentBytes;

            /// <summary>
            /// Per-instance ceiling on <see cref="PageContentBytes"/>, so a test can lower it
            /// without mutating process-wide state — a shared static would couple concurrent test
            /// classes, which this repository has already paid for once.
            /// </summary>
            public long MaxPageContentBytes = DefaultMaxPageContentBytes;

            /// <summary>True for a batch render's page worker: its JPEG 2000 decodes run on the worker's thread, since the batch already parallelises by page.</summary>
            public bool DecodeOnCallingThread;

            /// <summary>
            /// Each page's <see cref="DisplayListIndex"/>, built once (a throwaway render at scale 1 —
            /// the index is DPI-independent) and reused by every one of <see cref="RenderPageBands"/>'s bands. Keyed on
            /// <see cref="PdfPage"/> like <see cref="PageContent"/>, for the same reason: reference
            /// identity, stable for the session's life. Built by
            /// <see cref="PdfPageRenderer.GetOrBuildDisplayListIndex"/>; nothing else populates it.
            /// </summary>
            public Dictionary<PdfPage, DisplayListIndex> DisplayListIndexes;
        }

        /// <summary>
        /// Builds the reusable state for a caller that already holds an open document and renders
        /// every page of it on one thread — <see cref="PdfPageRenderer.RenderAllPages(Stream,int,PdfRenderOptions)"/>.
        /// </summary>
        /// <remarks>
        /// The caller owns the result and <b>must dispose <see cref="SharedState.Fonts"/></b>:
        /// <see cref="FontCache.Dispose"/> destroys every typeface it handed out, so nothing may
        /// hold one past that point. It exists so the hidden-OCG computation has one home; the
        /// constructor below uses it too.
        /// </remarks>
        internal static SharedState CreateSharedState(PdfDocument doc) => new SharedState
        {
            Fonts      = new FontCache(),
            Operations = new ContentOperationCache(),
            // Computed once. The one-shot paths recompute it per page and per tile.
            HiddenOcgs = PdfPageRenderer.BuildHiddenOcgSet(doc),
        };

        private readonly PdfDocument _doc;
        private readonly Stream _owned;              // the stream we opened and must close, if any
        private readonly Stream _decrypted;          // the plaintext buffer, when the input was encrypted
        private readonly SharedState _shared;
        private readonly PdfRenderOptions _options;
        private bool _disposed;

        private PdfRenderSession(PdfDocument doc, Stream owned, Stream decrypted,
                                 PdfRenderOptions options)
        {
            _doc       = doc;
            _owned     = owned;
            _decrypted = decrypted;
            _options = options ?? PdfRenderOptions.Default;
            _shared  = CreateSharedState(doc);
        }

        /// <summary>Opens a session over a PDF stream. The stream must outlive the session.</summary>
        /// <remarks>
        /// An encrypted document is decrypted once here, using
        /// <see cref="PdfRenderOptions.Password"/>, exactly as the one-shot entry points do per
        /// call; a session that accepts a password and does not use it would render every encrypted
        /// document differently from <c>PdfPageRenderer</c>.
        /// </remarks>
        public static PdfRenderSession Open(Stream input, PdfRenderOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            Stream seekable = Tetitulz.Pdf.Core.IO.SeekableStream.Ensure(input, rewind: true, out bool ownsSeekable);
            Stream owned    = ownsSeekable ? seekable : null;
            Stream source   = null;
            try
            {
                source = PdfPageRenderer.EnsureDecryptedStream(
                    seekable, options?.Password, out bool ownsSource);

                var doc = PdfDocument.Open(source);
                // The session must outlive both: the decrypted buffer backs every later render.
                return new PdfRenderSession(doc, owned, ownsSource ? source : null, options);
            }
            catch
            {
                if (!ReferenceEquals(source, seekable)) source?.Dispose();
                owned?.Dispose();
                throw;
            }
        }

        /// <summary>Opens a session over a PDF file.</summary>
        public static PdfRenderSession Open(string inputPath, PdfRenderOptions options = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));

            var fs = File.OpenRead(inputPath);
            try
            {
                Stream source = PdfPageRenderer.EnsureDecryptedStream(
                    fs, options?.Password, out bool ownsSource);

                var doc = PdfDocument.Open(source);
                return new PdfRenderSession(doc, fs, ownsSource ? source : null, options);
            }
            catch { fs.Dispose(); throw; }
        }

        /// <summary>Number of pages in the open document.</summary>
        public int PageCount
        {
            get { ThrowIfDisposed(); return _doc.Pages.Count; }
        }

        /// <summary>
        /// Renders a rectangular tile of a page, reusing the session's caches. Coordinates are in
        /// PDF points; the result is PNG bytes, identical to
        /// <see cref="PdfPageRenderer.RenderPageTile(Stream,int,float,float,float,float,int,PdfRenderOptions)"/>.
        /// </summary>
        public byte[] RenderPageTile(int pageIndex, float clipX, float clipY,
                                     float clipW, float clipH, int dpi = 150)
            => RenderPageTileToImage(pageIndex, clipX, clipY, clipW, clipH,
                                     PdfImageFormat.Png, dpi);

        /// <summary>
        /// Renders a rectangular tile of a page to <paramref name="format"/>, reusing the session's
        /// caches.
        /// </summary>
        public byte[] RenderPageTileToImage(int pageIndex, float clipX, float clipY,
                                            float clipW, float clipH,
                                            PdfImageFormat format = PdfImageFormat.Png,
                                            int dpi = 150,
                                            int quality = PdfPageRenderer.DefaultJpegQuality,
                                            System.Threading.CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi));
            PdfPageRenderer.ValidateClip(clipX, clipY, clipW, clipH);
            if (pageIndex < 0 || pageIndex >= _doc.Pages.Count)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));

            return PdfPageRenderer.RenderTileWithSharedState(
                _doc, pageIndex, clipX, clipY, clipW, clipH, dpi, _shared, _options,
                format, quality, cancellationToken);
        }

        /// <summary>
        /// Renders a whole page to PNG bytes, reusing the session's caches. Identical output to
        /// <see cref="PdfPageRenderer.RenderPage(Stream,int,int,PdfRenderOptions)"/> - including a
        /// registered <see cref="Core.Plugins.IRendererPlugin"/>.
        /// </summary>
        /// <remarks>
        /// <b>The unencoded-pixel paths are the exception, and deliberately so.</b>
        /// <see cref="RenderPageToPixels(int,int)"/> and <c>PdfOperations.RenderPageToPixels</c> - which is
        /// what the WinForms viewer draws with - never consult a renderer plugin: the interface
        /// hands back encoded PNG bytes, so serving them as pixels would mean decoding what was
        /// just encoded on every repaint.
        /// </remarks>
        public byte[] RenderPage(int pageIndex, int dpi = 150)
            => RenderPageToImage(pageIndex, PdfImageFormat.Png, dpi);

        /// <summary>
        /// Renders a whole page to <paramref name="format"/>, reusing the session's caches.
        /// </summary>
        /// <remarks>
        /// JPEG and WebP too, so a caller wanting them for the same document need not leave the
        /// session and re-parse — the cost the session exists to remove.
        /// </remarks>
        public byte[] RenderPageToImage(int pageIndex, PdfImageFormat format,
                                        int dpi = 150,
                                        int quality = PdfPageRenderer.DefaultJpegQuality,
                                        System.Threading.CancellationToken cancellationToken = default)
        {
            ValidatePageAndDpi(pageIndex, dpi);
            return PdfPageRenderer.RenderPageWithSharedState(_doc, pageIndex, dpi, _shared, _options,
                                                             format, quality, cancellationToken);
        }

        /// <summary>
        /// Renders a whole page to raw RGBA8888 premultiplied pixels, reusing the session's caches
        /// and skipping the PNG encode entirely.
        /// </summary>
        /// <remarks>
        /// This is the pairing a viewer wants: PNG encoding is ~97% of a raster render, and a
        /// viewer that encodes only to decode again immediately pays it twice. Combined with the
        /// session's reuse of the parsed document and font cache, it removes essentially all the
        /// fixed cost from a repaint.
        /// </remarks>
        public RenderedPage RenderPageToPixels(int pageIndex, int dpi = 150)
            => RenderPageToPixels(pageIndex, dpi, System.Threading.CancellationToken.None);

        /// <summary>Renders a whole page to raw pixels, with cancellation.</summary>
        public RenderedPage RenderPageToPixels(int pageIndex, int dpi,
            System.Threading.CancellationToken cancellationToken)
        {
            ValidatePageAndDpi(pageIndex, dpi);
            return PdfPageRenderer.RenderPagePixelsWithSharedState(_doc, pageIndex, dpi, _shared,
                                                                   _options, cancellationToken);
        }

        /// <summary>
        /// Renders a rectangular tile of a page to raw pixels, reusing the session's caches.
        /// </summary>
        /// <param name="pageIndex">Zero-based page.</param>
        /// <param name="clipX">Left edge, in points from the render box's own origin.</param>
        /// <param name="clipY">Bottom edge, in points from the render box's own origin — PDF's
        /// y axis points up, so 0 is the foot of the page.</param>
        /// <param name="clipW">Width in points.</param>
        /// <param name="clipH">Height in points.</param>
        /// <param name="dpi">Resolution.</param>
        /// <param name="cancellationToken">Polled inside the content stream, not only at entry.</param>
        /// <remarks>
        /// <b>The tile counterpart of <see cref="RenderPageToPixels(int,int)"/>.</b> A tile could
        /// already be had as encoded bytes and a whole page as raw pixels, but not a tile as raw
        /// pixels — so streaming a large page out region by region meant encoding each region and
        /// decoding it back. See <see cref="RenderPageBands"/> for the case that motivates it.
        /// <para><b>Only the operations whose bound meets the tile paint</b>: the page's display-list index — built once per page and shared with
        /// <see cref="RenderPageBands"/> — is asked for the tile's rectangle, and every other
        /// operation still runs (its state changes are the next one's) but skips its image draw,
        /// glyph run, pattern or shading. Fills and strokes are already culled against the clip.</para>
        /// </remarks>
        public RenderedPage RenderPageTileToPixels(int pageIndex, float clipX, float clipY,
            float clipW, float clipH, int dpi = 150,
            System.Threading.CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ValidatePageAndDpi(pageIndex, dpi);
            PdfPageRenderer.ValidateClip(clipX, clipY, clipW, clipH);

            // The index is page-point space, Y down from the box's top and X from its left; the
            // tile is Y up from the box's foot — the same flip RenderPageBands makes.
            PageRenderSize size = GetRenderSize(pageIndex, dpi);
            DisplayListIndex index =
                PdfPageRenderer.GetOrBuildDisplayListIndex(_doc.Pages[pageIndex], _doc, size, _shared);
            float top = size.HeightPoints - (clipY + clipH), bottom = size.HeightPoints - clipY;
            var paintGate = new PaintGate(index.Query(top, bottom, clipX, clipX + clipW));

            return PdfPageRenderer.RenderTilePixelsWithSharedState(
                _doc, pageIndex, clipX, clipY, clipW, clipH, dpi, _shared, _options,
                cancellationToken, paintGate);
        }

        /// <summary>
        /// Renders a page in horizontal bands, top to bottom, handing each to
        /// <paramref name="onBand"/> as raw pixels.
        /// </summary>
        /// <param name="pageIndex">Zero-based page.</param>
        /// <param name="dpi">Resolution.</param>
        /// <param name="bandHeightPixels">
        /// Rows per band. The last band is shorter when the page does not divide evenly.
        /// </param>
        /// <param name="onBand">
        /// Called once per band with its index and its pixels. The band is valid only for the
        /// duration of the call — copy anything you need to keep.
        /// </param>
        /// <param name="cancellationToken">Polled inside each band's content stream.</param>
        /// <returns>How many bands were produced.</returns>
        /// <remarks>
        /// <para><b>What this buys, stated exactly: bounded peak memory, not less work.</b> A page
        /// at 600 dpi is a 250 MB bitmap; in bands of 256 rows it is 5 MB at a time, and a caller
        /// writing TIFF strips or a scrolling viewport never needs the whole thing. Every band still
        /// runs the page's whole operation list — state is sequential — but the page's display-list
        /// index tells each band which operations' ink can reach it, and
        /// the rest skip their image draw, glyph run, pattern or shading; fills and strokes are
        /// culled against the clip as before. Tokenising is shared through this session's cache.
        /// What is measured is less allocation, not a time figure (GAPS §11f).</para>
        ///
        /// <para><b>Bands are cut in pixels and converted to points</b>, so they tile the page
        /// exactly: cutting in points and rounding each band independently leaves seams where two
        /// neighbouring <c>ceil</c>s disagree by a row.</para>
        /// </remarks>
        public int RenderPageBands(int pageIndex, int dpi, int bandHeightPixels,
            Action<int, RenderedPage> onBand,
            System.Threading.CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ValidatePageAndDpi(pageIndex, dpi);
            if (bandHeightPixels <= 0)
                throw new ArgumentOutOfRangeException(nameof(bandHeightPixels));
            if (onBand == null) throw new ArgumentNullException(nameof(onBand));

            PageRenderSize size = GetRenderSize(pageIndex, dpi);
            int heightPixels = size.HeightPixels;
            if (heightPixels <= 0) return 0;

            // Points per pixel, from the measurement rather than from dpi/72: the page's own
            // /UserUnit and any clamping are already folded into HeightPixels, and deriving the
            // scale independently is how a tile ends up a row taller than the page it came from.
            double pointsPerPixel = (double)size.HeightPoints / heightPixels;
            int bands = 0;

            // Built once per page, reused by every band below.
            // See GetOrBuildDisplayListIndex's own remarks for why this is safe to share across
            // dpi and rotation the way PageContent already is.
            DisplayListIndex index =
                PdfPageRenderer.GetOrBuildDisplayListIndex(_doc.Pages[pageIndex], _doc, size, _shared);

            for (int top = 0; top < heightPixels; top += bandHeightPixels)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int rows = Math.Min(bandHeightPixels, heightPixels - top);

                // PDF's y axis points up and bands run down the page, so the band nearest the top
                // of the image is the one furthest up in user space.
                float clipH = (float)(rows * pointsPerPixel);
                float clipY = (float)((heightPixels - top - rows) * pointsPerPixel);

                // The index's own coordinate space is page-point, Y-down, measured from the box's
                // own top (DisplayListEntry.PageBounds' contract) — the same space this band's
                // clipY/clipH (Y-up, measured from the box's bottom) is a simple flip of, using the
                // same size.HeightPoints both this loop and GetOrBuildDisplayListIndex share.
                float bandTop    = size.HeightPoints - (clipY + clipH);
                float bandBottom = size.HeightPoints - clipY;
                var paintGate = new PaintGate(index.Query(bandTop, bandBottom));

                onBand(bands++, PdfPageRenderer.RenderTilePixelsWithSharedState(
                    _doc, pageIndex, 0f, clipY, size.WidthPoints, clipH, dpi, _shared, _options,
                    cancellationToken, paintGate));
            }

            return bands;
        }

        /// <summary>
        /// The size a page will render at, from the session's already-open document.
        /// </summary>
        public PageRenderSize GetRenderSize(int pageIndex, int dpi = 150)
        {
            ValidatePageAndDpi(pageIndex, dpi);
            // The renderer's own construction, so the two answers cannot drift apart.
            return PdfPageRenderer.ToRenderSize(_doc.Pages[pageIndex], _doc, dpi, _options);
        }

        private void ValidatePageAndDpi(int pageIndex, int dpi)
        {
            ThrowIfDisposed();
            if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi), "DPI must be positive.");
            if (pageIndex < 0 || pageIndex >= _doc.Pages.Count)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Order matters only in that the font cache owns typefaces; nothing here holds one.
            _shared.Fonts?.Dispose();
            _doc?.Dispose();
            _decrypted?.Dispose();
            _owned?.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PdfRenderSession));
        }
    }
}
