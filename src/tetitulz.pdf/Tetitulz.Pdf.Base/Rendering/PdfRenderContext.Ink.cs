// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using SkiaSharp;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Ink deposition for <c>/Separation</c> and <c>/DeviceN</c> colorants — the half of
    /// <see cref="InkAccumulator"/> that decides <b>which pixels</b> an ink reaches.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the geometry lives here and not in the interpreter.</b> Where the ink lands is
    /// the same question as where the paint lands, and this class already owns every term of that
    /// answer: the current path, the clip, the stroke pen's real width under the CTM, and the page
    /// transform. The interpreter's copy re-derived the last of those and got it wrong — the current
    /// path is already in Y-down page-point space, and it applied the page flip a second time, so
    /// every coverage map came back mirrored about the page's horizontal centre line. A fixture
    /// whose artwork is a full-page rectangle cannot see that, which is why it survived.</para>
    ///
    /// <para><b>Coverage is rasterised, not approximated by a bounding box.</b> The path's bounds
    /// at full tint are the right answer only for an unclipped axis-aligned rectangle: a diagonal
    /// hairline would report the whole page as inked, a circle its square, a clipped fill everything
    /// outside the clip. For an ink
    /// <i>inventory</i> those are tolerable; for the ink <i>estimate</i> a prepress caller wants,
    /// they are the whole measurement.</para>
    ///
    /// <para><b>A stroke is rasterised as a stroke</b>, under the pen the paint will actually use.
    /// The path's bounds know nothing of the pen, so an axis-aligned rule — most of the vector content
    /// of an invoice — would be recorded exactly one pixel wide however wide the line was set. Note
    /// that <see cref="SKRect.IsEmpty"/> is <b>false</b> for a zero-height rectangle in SkiaSharp, so
    /// such a rule would not be dropped outright; it would be recorded,
    /// and recorded far too thin, which is the harder of the two to notice.</para>
    ///
    /// <para><b>None of this runs on an ordinary render.</b> Every entry point returns immediately
    /// when no <see cref="InkAccumulator"/> is attached, and one is attached only by
    /// <c>PdfPageRenderer.ExtractInkCoverage</c>. The clip tracking below is gated on the same
    /// test, so a normal page pays neither the path intersections nor the allocation.</para>
    /// </remarks>
    internal sealed partial class PdfRenderContext : IDisposable
    {
        /// <summary>
        /// True when a host has asked for ink coverage, which is the single gate on every cost in
        /// this file.
        /// </summary>
        private bool TrackingInk => InkAccumulator != null;

        /// <summary>
        /// Clip paths minted by <see cref="NoteInkClip"/>, held so they can be disposed with the
        /// context.
        /// </summary>
        /// <remarks>
        /// A <see cref="SKPath"/> is native memory behind a finalizer, and a q/Q-nested page can
        /// mint one per clip operator. The graphics state cannot own them — after a <c>q</c> the
        /// same instance is referenced by both the saved frame and the live one, so no single frame
        /// may free it. That is the identical ownership rule <c>ActiveSoftMask</c> carries, and it
        /// is resolved the same way: the context owns the lifetime.
        /// </remarks>
        private List<SKPath> _inkClipPaths;

        /// <summary>
        /// Intersects the tracked ink clip with <paramref name="clip"/> (page-point space, Y-down),
        /// mirroring a clip the canvas has just taken.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a parallel clip at all.</b> Skia will hand back the clip's <i>bounds</i>
        /// and never its shape, so a coverage raster built from <c>DeviceClipBounds</c> alone would
        /// report a circular clip as its square — a confidently wrong number in place of the
        /// previous obviously-missing one. Tracking the path costs nothing on a normal render
        /// because it is gated on <see cref="TrackingInk"/>.</para>
        /// <para>The result is stored on the graphics state, so <c>q</c> saves it and <c>Q</c>
        /// restores it with no bookkeeping of its own — the clip's lifetime <i>is</i> the state
        /// stack's, and pairing this with the canvas's own save stack by hand is exactly the kind
        /// of two-implementations-must-agree arrangement that drifts.</para>
        /// </remarks>
        private void NoteInkClip(SKPath clip)
        {
            if (!TrackingInk || clip == null) return;

            SKPath current = _gs.InkClipPath;
            SKPath merged;
            if (current == null)
            {
                merged = new SKPath(clip);
            }
            else
            {
                merged = current.Op(clip, SKPathOp.Intersect);
                // Op returns null when Skia cannot compute the intersection. Keeping the previous
                // clip is the conservative direction: it can only over-report coverage, whereas an
                // empty path would silently drop every later deposit on the page.
                if (merged == null) return;
            }

            (_inkClipPaths ??= new List<SKPath>()).Add(merged);
            _gs.InkClipPath = merged;
        }

        /// <summary>
        /// Deposits the current path's ink into the accumulator, for a fill, a stroke or both.
        /// </summary>
        /// <param name="fill">Deposit the non-stroking colorants over the path's interior.</param>
        /// <param name="stroke">Deposit the stroking colorants under the pen.</param>
        /// <param name="evenOdd">The fill rule the paint operator selected.</param>
        internal void DepositPathInk(bool fill, bool stroke, bool evenOdd)
        {
            if (!TrackingInk || _currentPath == null || _currentPath.IsEmpty) return;

            if (fill)   DepositInk(_currentPath, _gs.FillSepTints,   stroke: false, evenOdd: evenOdd);
            if (stroke) DepositInk(_currentPath, _gs.StrokeSepTints, stroke: true,  evenOdd: false);
        }

        /// <summary>
        /// Deposits a captured glyph outline's ink, as produced by
        /// <see cref="BeginGlyphCapture"/>/<see cref="EndGlyphCapture"/>.
        /// </summary>
        /// <remarks>
        /// Text is the least visible hole a coverage map can have, because the colorant is still
        /// <i>registered</i> when the colour is set: a page whose only spot content is type would come
        /// back with the channel present and every pixel of it zero, which
        /// reads as "this ink is selected and never used" rather than as "not measured". Glyph
        /// outlines arrive in the same Y-down page-point space as <c>_currentPath</c>, so they need
        /// no transform of their own — that is what makes this the same code as a path fill.
        /// </remarks>
        internal void DepositGlyphInk(SKPath glyphs)
        {
            if (!TrackingInk || glyphs == null || glyphs.IsEmpty) return;
            DepositInk(glyphs, _gs.FillSepTints, stroke: false, evenOdd: false);
        }

        /// <summary>
        /// Rasterises <paramref name="geometry"/> and records, for each colorant in
        /// <paramref name="tints"/>, tint × coverage at every pixel it reaches.
        /// </summary>
        /// <remarks>
        /// <b>Anti-aliased coverage scales the tint</b>, rather than being rounded to ink/no-ink.
        /// A half-covered edge pixel carries half the ink, which is what an ink-cost estimate is
        /// measuring; quantising it to "inked" would inflate every thin rule and every glyph stem
        /// by its whole perimeter.
        /// </remarks>
        private void DepositInk(SKPath geometry, IReadOnlyDictionary<string, float> tints,
                                bool stroke, bool evenOdd)
        {
            InkAccumulator acc = InkAccumulator;
            if (acc == null || tints == null || tints.Count == 0) return;
            if (geometry == null || geometry.IsEmpty) return;

            if (!TryInkDeviceBounds(geometry, stroke, acc, out SKRectI bounds)) return;

            try
            {
                using SKBitmap coverage =
                    RasterisePathCoverage(geometry, stroke, evenOdd, bounds, _gs.InkClipPath);
                if (coverage == null) return;

                foreach (KeyValuePair<string, float> kv in tints)
                {
                    float tint = kv.Value;
                    if (tint <= 0f) continue;
                    byte[] channel = acc.GetOrCreateChannel(kv.Key);
                    AccumulateChannel(channel, acc.Width, acc.Height, coverage, bounds, tint);
                }
            }
            catch (Exception ex)
            {
                // A failed measurement must not fail the render it is measuring. The accumulator
                // keeps whatever it already holds, and the caller is told the map is short rather
                // than being handed a silently incomplete one.
                RenderDiagnostics.Report(RenderDiagnosticKind.InkCoverageIncomplete,
                    "ink coverage could not be rasterised for one paint operation; " +
                    "the affected area is missing from the coverage map.", ex);
            }
        }

        /// <summary>
        /// The device-pixel rectangle a paint can reach: the geometry's bounds widened by the pen,
        /// intersected with the canvas clip and the accumulator. False when nothing is reachable.
        /// </summary>
        private bool TryInkDeviceBounds(SKPath geometry, bool stroke, InkAccumulator acc,
                                        out SKRectI bounds)
        {
            bounds = default;
            SKRect pb = geometry.Bounds;

            // The stroke margin comes from the shared cull implementation, which pays the CTM
            // scale §8.4.3.2 requires — a 2 pt pen under `10 0 0 10 0 0 cm` is 20 pt wide, and a
            // margin built from the raw LineWidth would clip the pen's own footprint out of its
            // own measurement. One point of slack on a fill covers the anti-aliased edge.
            float pad = stroke ? StrokeCullMargin() : 1f;

            var rect = new SKRectI(
                SaturatingCast.ToInt(Math.Floor((pb.Left   - pad) * DpiScale)),
                SaturatingCast.ToInt(Math.Floor((pb.Top    - pad) * DpiScale)),
                SaturatingCast.ToInt(Math.Ceiling((pb.Right  + pad) * DpiScale)),
                SaturatingCast.ToInt(Math.Ceiling((pb.Bottom + pad) * DpiScale)));

            // A zero-height or zero-width path is not nothing — a horizontal rule is exactly that —
            // and the margin above is what gives it a body to rasterise into, whatever the pen.
            SKRectI clip = _canvas.DeviceClipBounds;
            if (!rect.IntersectsWith(clip)) return false;
            rect = SKRectI.Intersect(rect, clip);

            var page = SKRectI.Create(0, 0, acc.Width, acc.Height);
            if (!rect.IntersectsWith(page)) return false;
            bounds = SKRectI.Intersect(rect, page);

            return bounds.Width > 0 && bounds.Height > 0;
        }

        /// <summary>
        /// Rasterises <paramref name="geometry"/> into an <see cref="SKColorType.Alpha8"/> bitmap
        /// covering <paramref name="deviceBounds"/> of the page's device grid, under the same
        /// transform and pen the real paint would use. The caller owns the result.
        /// </summary>
        /// <param name="geometry">Path in Y-down page-point space — <c>_currentPath</c>'s space.</param>
        /// <param name="stroke">Rasterise the pen's footprint rather than the interior.</param>
        /// <param name="evenOdd">Fill rule, ignored when <paramref name="stroke"/> is set.</param>
        /// <param name="deviceBounds">
        /// The region of the device grid to rasterise. The bitmap is this size and its top-left
        /// pixel is this rectangle's, so a caller reading it back adds the origin.
        /// </param>
        /// <param name="clipPath">
        /// An additional clip in the same page-point space, or null. Overprint passes null because
        /// it refuses a non-rectangular clip up front and bounds its write instead; ink coverage
        /// passes the tracked clip, because reporting a circular clip as its square would be wrong.
        /// </param>
        /// <remarks>
        /// <b>One rasteriser, two consumers.</b> Overprint (§11.7.4.2) and ink coverage both need
        /// "which device pixels does this paint cover, and how much of each", and they had answered
        /// it separately — which is two places for the pen width, the anti-aliasing and the base
        /// transform to disagree. They cannot now.
        /// </remarks>
        private SKBitmap RasterisePathCoverage(SKPath geometry, bool stroke, bool evenOdd,
                                               SKRectI deviceBounds, SKPath clipPath)
        {
            if (deviceBounds.Width <= 0 || deviceBounds.Height <= 0) return null;

            var coverage = new SKBitmap(new SKImageInfo(
                deviceBounds.Width, deviceBounds.Height, SKColorType.Alpha8, SKAlphaType.Premul));
            try
            {
                using var canvas = new SKCanvas(coverage);
                canvas.Clear(SKColors.Transparent);

                // Device origin first, then the base page transform: the bitmap covers a window on
                // the device grid, and everything below this line is in the same page-point space
                // the path and the clip are expressed in.
                canvas.Translate(-deviceBounds.Left, -deviceBounds.Top);
                canvas.Scale(DpiScale, DpiScale);

                if (clipPath != null) canvas.ClipPath(clipPath, SKClipOperation.Intersect, antialias: true);

                using SKPaint paint = stroke ? BuildStrokePaint() : BuildFillPaint(evenOdd);
                // Pure geometry. Constant alpha and the source colour belong to whoever consumes
                // the coverage, so baking either in here would apply it twice.
                paint.Color     = SKColors.Black;
                paint.BlendMode = SKBlendMode.Src;
                canvas.DrawPath(geometry, paint);

                return coverage;
            }
            catch
            {
                coverage.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Folds one colorant's tint × coverage into its channel, under the accumulator's
        /// max-coverage rule.
        /// </summary>
        private static void AccumulateChannel(byte[] channel, int accWidth, int accHeight,
                                              SKBitmap coverage, SKRectI bounds, float tint)
        {
            ReadOnlySpan<byte> pixels = coverage.GetPixelSpan();
            int rowBytes = coverage.RowBytes;
            if (pixels.Length < rowBytes * bounds.Height) return;

            for (int row = 0; row < bounds.Height; row++)
            {
                int y = bounds.Top + row;
                if ((uint)y >= (uint)accHeight) continue;

                int srcRow = row * rowBytes;
                int dstRow = y * accWidth;

                for (int col = 0; col < bounds.Width; col++)
                {
                    byte alpha = pixels[srcRow + col];
                    if (alpha == 0) continue;

                    int x = bounds.Left + col;
                    if ((uint)x >= (uint)accWidth) continue;

                    // One quantisation rule, PdfColor.Channel — the same clamp-and-round every
                    // other 8-bit conversion in the renderer goes through.
                    byte value = PdfColor.Channel(tint * (alpha / 255f));
                    int idx = dstRow + x;
                    // The accumulator owns how two deposits combine — see InkAccumulator.Combine.
                    channel[idx] = InkAccumulator.Combine(channel[idx], value);
                }
            }
        }

        /// <summary>
        /// Reports that spot ink reached the page by a route this measurement does not cover, so
        /// the map is short there rather than genuinely empty.
        /// </summary>
        /// <param name="what">
        /// The uncovered route, e.g. "a /Separation image". Used as the de-duplication key.
        /// </param>
        /// <remarks>
        /// <para><b>Silent when nobody is measuring.</b> An ordinary render has no accumulator, and
        /// a `/Separation` image on such a render is not an anomaly — it is Tuesday. The channel
        /// only means something to a caller who asked for coverage.</para>
        /// <para><b>Once per route per content stream</b>, following the rule
        /// <c>IgnoredParameterReporting</c> sets: a page of spot-coloured images would otherwise
        /// emit one event per image, and a channel that floods is one a caller filters out.</para>
        /// </remarks>
        internal void ReportInkNotMeasured(string what)
        {
            if (!TrackingInk || what == null) return;
            _inkNotMeasured ??= new HashSet<string>(StringComparer.Ordinal);
            if (!_inkNotMeasured.Add(what)) return;

            RenderDiagnostics.Report(RenderDiagnosticKind.InkCoverageIncomplete,
                what + " lays down /Separation or /DeviceN ink that is not measured; " +
                "the coverage map is short there, not empty.");
        }

        private HashSet<string> _inkNotMeasured;

        /// <summary>Disposes every clip path this context minted for ink tracking.</summary>
        private void DisposeInkClipPaths()
        {
            if (_inkClipPaths == null) return;
            foreach (SKPath p in _inkClipPaths) p?.Dispose();
            _inkClipPaths.Clear();
            _inkClipPaths = null;
        }
    }
}
