// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using SkiaSharp;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Wraps an <see cref="SKCanvas"/> and manages the PDF graphics-state stack.
    /// Coordinate-system note: PDF origin is bottom-left (Y-up); SkiaSharp origin
    /// is top-left (Y-down).  The initial canvas transform flips Y so that all
    /// subsequent drawing uses PDF point coordinates directly.
    /// </summary>
    internal sealed partial class PdfRenderContext : IDisposable
    {
        // ---- Backing canvas ----
        private readonly SKCanvas _canvas;

        // ---- Graphics-state stack (q / Q) ----
        private readonly Stack<GraphicsState> _stateStack = new Stack<GraphicsState>();
        private GraphicsState _gs = new GraphicsState();

        // ---- Current path ----
        private SKPath _currentPath = new SKPath();

        // ---- Text clipping path accumulator (PDF §9.3.6 modes 4-7) ----
        // Glyph outlines in device-pixel space are added here during BT..ET;
        // at ET or q/Q the accumulated path is intersected with the clip region.
        private SKPathBuilder _textClipPath;
        // True once a text-clipping render mode (4-7) has been exercised in the current
        // text object, even if all glyph outlines were empty. Lets EndTextClip distinguish
        // "no clip requested" (leave clip unchanged) from "clip requested but empty"
        // (clip out everything per PDF §9.4.3).
        private bool _textClipModeUsed;
        // When non-null, the glyph-draw methods append their (page-point, Y-down) outlines
        // here INSTEAD of painting. Used to fill text glyphs with a shading/tiling pattern:
        // the captured outline becomes the path that the pattern is painted into.
        private SKPathBuilder _glyphCapturePath;
        private bool   _fffdReported;  // one-shot guard for the U+FFFD ToUnicode-skip diagnostic

        // The bitmap _canvas draws into, when the caller supplied it. Read only while
        // _layerDepth == 0, to seed a non-isolated transparency group's backdrop.
        private readonly SKBitmap _rootBitmap;

        // ---- Page geometry (PDF points) ----
        public float PageWidth  { get; }
        public float PageHeight { get; }
        public float DpiScale   { get; }    // pixels per PDF-point

        /// <summary>
        /// PDF page space (y up) to canvas space (y down), before the canvas's own
        /// <see cref="DpiScale"/>: y mirrored about <see cref="PageHeight"/>. The one construction
        /// the shading shaders, the tiling pattern's bridge and the glyph outlines compose with.
        /// </summary>
        internal SKMatrix PageFlip => new SKMatrix
        {
            ScaleX = 1f, SkewX  = 0f,  TransX = 0f,
            SkewY  = 0f, ScaleY = -1f, TransY = PageHeight,
            Persp2 = 1f
        };

        // ---- Expose GS for interpreter ----
        internal GraphicsState GS => _gs;

        /// <summary>
        /// Optional ink accumulator for spot-colour transparency groups.
        /// When non-null, Separation/DeviceN colour stamps deposit into the
        /// accumulator alongside the primary RGBA composite, so callers
        /// building print-preview pipelines can flatten the ink coverage
        /// later via the colorant-specific tint transform.  Default: null
        /// (overprint stays at the existing <c>SKBlendMode.DstATop</c>
        /// approximation per GAPS 21.9).
        /// </summary>
        public InkAccumulator InkAccumulator { get; set; }

        /// <summary>
        /// When set, every paint call that reports a bound also invokes this with the operation's
        /// bound in <b>page-point space</b> (see <see cref="DisplayListEntry.PageBounds"/>) before
        /// painting — the seam the display list is built from. Null (the overwhelming default) is a pure
        /// no-op: every site that reports a bound does it through
        /// <c>PaintBoundsRecorder?.Invoke(...)</c>, so an ordinary render pays nothing for it.
        /// </summary>
        /// <remarks>
        /// <b>Only the geometry already computed to paint, reused, never recomputed.</b> Each site
        /// reports a bound derived from the same geometry it is about to hand to <c>_canvas</c> —
        /// for most sites that geometry IS the report; <see cref="DrawBitmap"/> and the text-blob
        /// site additionally run it through <see cref="UnscaleByDpi"/>, because their canvas
        /// geometry is in real device pixels and every other site's is not (see that method's
        /// remarks). Either way, a display list built this way cannot disagree with what actually
        /// paints, because it is derived from the same values that do, not recomputed
        /// independently. <b>Painting still happens</b> when this is set: nothing here skips a
        /// canvas call, because
        /// several of the methods that would need to (<c>FillPath</c>/<c>StrokePath</c> clear
        /// <c>_currentPath</c> as a side effect of painting) have state
        /// obligations beyond the pixels, and skipping paint safely needs each one audited on its
        /// own — not yet done. A caller building a display list pays one real render's cost to get
        /// it, same as it would to render the page once; the return is not paying that cost again
        /// per band (Stage D).
        /// </remarks>
        internal Action<SKRect> PaintBoundsRecorder { get; set; }

        /// <summary>
        /// When set and it returns <c>true</c> for the operation currently dispatching, skips the
        /// canvas-touching step of <see cref="DrawBitmap"/>, the private <c>DrawTextBlob</c>,
        /// <see cref="DrawGlyphPath"/>, <see cref="DrawPatternPath"/> and <see cref="DrawShading"/>.
        /// <c>FillPath</c>/<c>StrokePath</c> are deliberately
        /// not gated here: <see cref="IsCurrentPathOutsideClip"/> already culls them for free
        /// against the live canvas clip, before this predicate would even run, and gating them too
        /// would only add a second, redundant check.
        /// </summary>
        /// <remarks>
        /// <para><b>Every other effect of the operator still happens</b> — this is checked only
        /// immediately before each method's own expensive step, never in place of dispatching the
        /// operator. <c>DrawPatternPath</c>'s <c>finally { NewPath(); }</c> still runs; text-clip
        /// accumulation (<c>AddGlyphToTextClip</c>/<c>AddGlyphRunToTextClip</c>) and glyph capture
        /// for a pattern fill still run; the text-position advance
        /// <see cref="DrawGlyphRunWithWidths"/> returns is computed before this gate is ever
        /// consulted and is unaffected by it. An ops-list filter that skipped dispatching an
        /// operator entirely instead of gating only its paint would lose exactly these things,
        /// which is why the gate lives here and not in <c>ExecuteOps</c>.</para>
        /// <para><b>Not yet a decode-skipping gate for images.</b> <see cref="DrawBitmap"/> receives
        /// an already-decoded <see cref="SKBitmap"/> — decoding happens in the caller, before this
        /// method is even reached — so suppressing here still saves the <c>SKImage.FromBitmap</c>
        /// call and the canvas composite, but not the decode itself. Skipping the decode too would
        /// need this same predicate consulted earlier, in the interpreter's own image-<c>Do</c>
        /// handling — a further increment, not assumed here.</para>
        /// </remarks>
        internal Func<bool> PaintSuppressed { get; set; }

        /// <summary>
        /// How many hidden optional-content sections (§8.11.3.2) are open across the interpreters
        /// drawing into this context. While one is, nothing paints and no bound is reported, but
        /// every graphics-state operator still applies and the text position still moves — the
        /// clause's own words — so the content after the section lands where a reader puts it.
        /// Counted on the context, since a hidden section can enclose a form's or a glyph's whole
        /// interpreter.
        /// </summary>
        internal int HiddenContentDepth { get; set; }

        /// <summary>The paint gates, in one place: a hidden section, or the caller's predicate.</summary>
        private bool PaintIsSuppressed => HiddenContentDepth > 0 || (PaintSuppressed != null && PaintSuppressed());

        /// <summary>
        /// The page's budget, which every paint on this context charges its device area against
        /// (<see cref="RenderBudget.TryChargePaint"/>). The interpreter running into the context sets it
        /// to its own <c>FormBudget</c>, so an offscreen context — a pattern cell, a soft-mask group —
        /// spends the page's budget rather than one of its own. Null leaves painting unbudgeted.
        /// </summary>
        internal RenderBudget PaintBudget { get; set; }

        /// <summary>
        /// Charges a paint whose bound is <paramref name="pageBounds"/>, in this canvas's local space,
        /// <paramref name="weight"/> times the device area it covers inside the clip. False when the
        /// page's paint work is spent: the caller paints nothing.
        /// </summary>
        private bool ChargePaint(SKRect pageBounds, int weight)
            => PaintBudget == null || PaintBudget.TryChargePaint(DeviceAreaInClip(_canvas.TotalMatrix.MapRect(pageBounds)) * weight);

        /// <summary>As <see cref="ChargePaint"/>, for a bound already in device pixels.</summary>
        private bool ChargeDevicePaint(SKRect deviceBounds, int weight)
            => PaintBudget == null || PaintBudget.TryChargePaint(DeviceAreaInClip(deviceBounds) * weight);

        /// <summary>
        /// The current path's stroke charged at <paramref name="weight"/>: the smaller of its bounds'
        /// area inside the clip and its length times the pen's width, so a hairline across the page
        /// costs its ink, not the page. The length is measured only when the bounds alone would be dear.
        /// </summary>
        private bool ChargeStroke(int weight = RenderBudget.StrokePaintWeight)
        {
            if (PaintBudget == null) return true;
            float margin = StrokeCullMargin();
            SKRect bounds = _currentPath.Bounds;
            bounds.Inflate(margin, margin);
            SKMatrix m = _canvas.TotalMatrix;
            double area = DeviceAreaInClip(m.MapRect(bounds));
            if (area > StrokeLengthMeasureThreshold)
            {
                double length = 0;
                using (var measure = new SKPathMeasure(_currentPath, false, 1f))
                {
                    do { length += measure.Length; } while (measure.NextContour());
                }
                double scale = Math.Sqrt(Math.Abs((double)m.ScaleX * m.ScaleY - (double)m.SkewX * m.SkewY));
                double ink = length * scale * 2 * margin * scale;
                if (!double.IsNaN(ink) && ink < area) area = ink;
            }
            return PaintBudget.TryChargePaint(area * weight);
        }

        /// <summary>A stroke's bounds below this many device pixels are charged as they are, unmeasured.</summary>
        private const double StrokeLengthMeasureThreshold = 1 << 16;

        /// <summary>
        /// The area of <paramref name="device"/> inside the canvas's device clip, in pixels. A bound with
        /// a NaN edge is charged the whole clip: a paint never covers more, and NaN must not be free.
        /// </summary>
        private double DeviceAreaInClip(SKRect device)
        {
            SKRectI clip = _canvas.DeviceClipBounds;
            if (float.IsNaN(device.Left) || float.IsNaN(device.Top) || float.IsNaN(device.Right) || float.IsNaN(device.Bottom))
                return (double)clip.Width * clip.Height;
            double left   = Math.Max(Math.Min(device.Left, device.Right), clip.Left);
            double right  = Math.Min(Math.Max(device.Left, device.Right), clip.Right);
            double top    = Math.Max(Math.Min(device.Top, device.Bottom), clip.Top);
            double bottom = Math.Min(Math.Max(device.Top, device.Bottom), clip.Bottom);
            return right > left && bottom > top ? (right - left) * (bottom - top) : 0;
        }

        /// <summary>
        /// The plate a separation render is drawing (<see cref="SeparationPlate"/>), or null for an
        /// ordinary render. Every paint's colour becomes its tint on this plate; an image, a
        /// shading or a shading pattern, which arrive as sRGB pixels, go through the plate's
        /// <see cref="SeparationPlate.SrgbFilter"/>. A soft mask's group is rendered without it —
        /// its luminosity is a colour question, not an ink one.
        /// </summary>
        internal SeparationPlate Plate { get; set; }

        /// <summary>
        /// Converts a bound already multiplied by <see cref="DpiScale"/> back to the page-point,
        /// Y-down space every other <see cref="PaintBoundsRecorder"/> report uses — straight from
        /// <c>_currentPath</c>/<c>canvasPath</c>, which <see cref="TransformPoint"/> never scales
        /// by DPI. Only <see cref="DrawBitmap"/> and <c>DrawTextBlob</c> need this: both build an
        /// absolute screen matrix for <c>DrawWithOptionalSoftMask</c> that bakes DpiScale
        /// in (the `s` factor in <see cref="TryBuildImageMatrix"/> and in the text sites'
        /// `screenMatrix`), because that matrix has to land on real device pixels to paint
        /// correctly — reporting it unscaled would put every image and text bound in a different
        /// coordinate system from every fill, stroke and glyph-outline bound.
        /// </summary>
        private SKRect UnscaleByDpi(SKRect deviceRect)
        {
            float inv = DpiScale > 0f ? 1f / DpiScale : 1f;
            return new SKRect(deviceRect.Left * inv, deviceRect.Top * inv,
                               deviceRect.Right * inv, deviceRect.Bottom * inv);
        }

        // ----------------------------------------------------------------
        //  Constructor
        // ----------------------------------------------------------------

        public PdfRenderContext(SKCanvas canvas, float pageWidthPts, float pageHeightPts, float dpiScale)
            : this(canvas, pageWidthPts, pageHeightPts, dpiScale, null)
        {
        }

        /// <summary>
        /// As the four-argument constructor, additionally taking the bitmap
        /// <paramref name="canvas"/> draws into.
        /// </summary>
        /// <param name="rootBitmap">
        /// The canvas's backing bitmap, or null when the caller has none to offer. Supplying it is
        /// what lets a <b>non-isolated</b> transparency group seed its layer with the real backdrop
        /// (see <see cref="BeginTransparencyGroup(float,string,bool,bool)"/>); with null, such a
        /// group renders isolated and says so through <see cref="RenderDiagnostics"/>. It is read
        /// only while no compositing layer is open, when its pixels are the canvas's own output.
        /// </param>
        /// <param name="canvas">Target canvas. Its transform is set to flip Y into PDF space.</param>
        /// <param name="pageWidthPts">Render box width in PDF points.</param>
        /// <param name="pageHeightPts">Render box height in PDF points.</param>
        /// <param name="dpiScale">
        /// Device pixels per PDF point — <c>dpi / 72</c>. Everything drawn is in points; this is the
        /// only place resolution enters.
        /// </param>
        public PdfRenderContext(SKCanvas canvas, float pageWidthPts, float pageHeightPts,
            float dpiScale, SKBitmap rootBitmap)
        {
            _canvas     = canvas;
            _rootBitmap = rootBitmap;
            PageWidth   = pageWidthPts;
            PageHeight  = pageHeightPts;
            DpiScale    = dpiScale;

            // Y-flip is applied in TransformPoint (PDF y-up → screen y-down),
            // so the canvas only needs a uniform positive scale.
            // This preserves the canvas orientation (det = s² > 0) so that
            // Skia's path-stroker correctly computes miter join directions —
            // a canvas with negative determinant (Scale(s,-s)) caused Skia to
            // place miter spikes on the wrong side of hairpin turns.
            canvas.Save();
            canvas.Scale(dpiScale, dpiScale);
        }

        // ----------------------------------------------------------------
        //  Graphics-state stack
        // ----------------------------------------------------------------

        // Upper bound on the q/Q (graphics-state) nesting depth.  A malformed content stream
        // with pathological or unbalanced `q` operators would otherwise grow the state stack
        // (and the canvas save stack) without limit and OOM the host.  Real documents nest only
        // a handful of levels; the shared cap is far above any legitimate use.
        private static int MaxStateStackDepth => PdfLimits.MaxGraphicsStateStackDepth;
        private int  _skippedPushes;       // q's refused over the cap; each skips a matching Q
        private bool _stateDepthReported;

        public void PushState()
        {
            if (_stateStack.Count >= MaxStateStackDepth)
            {
                // Refuse the push (no canvas Save) and remember to skip the matching Q so the
                // state and canvas stacks stay balanced.
                _skippedPushes++;
                if (!_stateDepthReported)
                {
                    _stateDepthReported = true;
                    RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                        $"Graphics-state (q) nesting exceeded {MaxStateStackDepth} levels; " +
                        "further q operators ignored to bound memory.");
                }
                return;
            }
            _stateStack.Push(_gs.Clone());
            _canvas.Save();
        }

        public void PopState()
        {
            if (_skippedPushes > 0) { _skippedPushes--; return; } // matches a refused push
            if (_stateStack.Count == 0) return;
            // PDF spec §9.3.1: text matrices (Tm, Tlm) are NOT part of the graphics state
            // and must not be restored by Q.  Preserve them across the state pop.
            var tm  = _gs.Tm;
            var tlm = _gs.Tlm;
            var dyingMask = _gs.ActiveSoftMask;
            _gs = _stateStack.Pop();
            _gs.Tm  = tm;
            _gs.Tlm = tlm;
            // The mask the discarded state was using may or may not survive the pop: a `gs /SMask`
            // INSIDE this q…Q block is going away with it, while one set BEFORE the q is the very
            // mask the restored frame carries. Only the first may be freed.
            ReleaseSoftMaskIfUnreferenced(dyingMask);
            _canvas.Restore();
            ApplyCTMToCanvas();
        }

        // ----------------------------------------------------------------
        //  Soft-mask ownership
        // ----------------------------------------------------------------

        /// <summary>
        /// Image pixels → device pixels for an image occupying the unit square under the current
        /// CTM. Returns <c>false</c> for a degenerate (zero-area) mapping.
        /// </summary>
        /// <remarks>
        /// Extracted from <see cref="DrawBitmap"/> when overprint simulation needed to rasterise
        /// an image's colorant channels under <b>the same</b> transform the image itself is drawn
        /// with. A second copy of this arithmetic would be free to disagree about rotation or skew,
        /// and the two rasters would then be offset from each other — visible as a colour fringe
        /// along one edge, and very hard to attribute.
        /// </remarks>
        private bool TryBuildImageMatrix(int bw, int bh, float x, float y, float w, float h,
                                         out SKMatrix m)
        {
            float s = DpiScale;
            var (tlx, tly) = TransformPoint(x,     y + h);
            var (trx, trY) = TransformPoint(x + w, y + h);
            var (blx, blY) = TransformPoint(x,     y);
            tlx *= s; tly *= s; trx *= s; trY *= s; blx *= s; blY *= s;

            m = new SKMatrix
            {
                ScaleX = (trx - tlx) / bw, SkewX  = (blx - tlx) / bh, TransX = tlx,
                SkewY  = (trY - tly) / bw, ScaleY = (blY - tly) / bh, TransY = tly,
                Persp0 = 0f, Persp1 = 0f, Persp2 = 1f,
            };

            float det = m.ScaleX * m.ScaleY - m.SkewX * m.SkewY;
            return Math.Abs(det) > 1e-9f;
        }

        /// <param name="bitmap">The decoded image, in sRGB.</param>
        /// <param name="x">Left of the unit square the image fills, in user space.</param>
        /// <param name="y">Bottom of that square.</param>
        /// <param name="w">Its width.</param>
        /// <param name="h">Its height.</param>
        /// <param name="interpolate">The image's <c>/Interpolate</c>.</param>
        /// <param name="plateReady">
        /// True when the pixels are already a separation plate's grey (a stencil painted in
        /// <see cref="StencilColor"/>), so the <see cref="Plate"/>'s sRGB filter must not convert
        /// them a second time.
        /// </param>
        public void DrawBitmap(SKBitmap bitmap, float x, float y, float w, float h, bool interpolate = true, bool plateReady = false)
        {
            if (bitmap == null) return;
            int bw = bitmap.Width, bh = bitmap.Height;
            if (bw <= 0 || bh <= 0) return;

            // Per PDF §8.9.4 and the default image matrix, the image occupies the unit
            // square under the current CTM, with image pixel-row 0 at the TOP:
            //   image pixel (0, 0)  → user (x,     y + h)   [top-left]
            //   image pixel (bw, 0) → user (x + w, y + h)   [top-right]
            //   image pixel (0, bh) → user (x,     y)       [bottom-left]
            // Map all three corners through the CTM to screen-pixel space and fit an
            // affine matrix to them.  This honours rotation AND skew in the CTM — the
            // previous implementation collapsed the image to an axis-aligned SKRect, which
            // dropped any rotation (e.g. credit-card artwork flattened into many rotated
            // image tiles rendered as a torn mosaic).  A pure scale/translate or 90°/180°/
            // 270° rotation still maps to the same geometry as before; vertical/horizontal
            // flips are handled automatically by the sign of the fitted axes.
            if (!TryBuildImageMatrix(bw, bh, x, y, w, h, out SKMatrix m)) return;

            // See PaintBoundsRecorder's remarks. m maps bitmap-pixel space (0,0)-(bw,bh) onto the
            // quad the image actually paints into, but in REAL device pixels — TryBuildImageMatrix
            // bakes DpiScale into it (the `s` above). PaintBoundsRecorder's other callers
            // (StrokePath, FillPath, DrawGlyphPath) report page-point bounds, straight from
            // _currentPath/canvasPath, which TransformPoint never multiplies by DpiScale. Dividing
            // out DpiScale here is what makes the two comparable; at DpiScale 1 the difference is
            // numerically invisible.
            if (HiddenContentDepth == 0)
                PaintBoundsRecorder?.Invoke(UnscaleByDpi(m.MapRect(new SKRect(0, 0, bw, bh))));

            // See PaintSuppressed's remarks: the decode already happened in the caller, so this
            // skips only the composite (SKImage.FromBitmap plus the canvas draw), not the decode.
            if (PaintIsSuppressed) return;
            if (!ChargeDevicePaint(m.MapRect(new SKRect(0, 0, bw, bh)),
                    interpolate ? RenderBudget.InterpolatedImagePaintWeight : RenderBudget.ImagePaintWeight))
                return;

            SKSamplingOptions sampling = SamplingFor(interpolate);
            // An image is a non-stroking paint: it is drawn at the alpha constant ca and in the blend
            // mode (§11.6.4.4, §11.3.5), as DrawShading's flood is. The paint carried neither, so an
            // image — a Do, an inline image, a stencil mask — under `ca 0.5` or /BM /Multiply painted
            // opaque and Normal, and the SVG export agreed with it (SvgContentWriter.ImagePaintAttributes).
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color       = SKColors.Black.WithAlpha(PdfColor.Channel(_gs.FillAlpha)),
            };
            // An image with an alpha channel lays down partial alpha its paint colour does not show.
            ApplyElementBlend(paint, _gs.BlendMode, translucent: bitmap.AlphaType != SKAlphaType.Opaque);
            if (Plate != null && !plateReady) ApplyPlateFilter(paint, "an image");
            using var img = SKImage.FromBitmap(bitmap);

            // Through the soft-mask wrapper, like every other painting operation. §11.6.4 applies
            // the graphics-state /SMask to all of them; this one drew straight onto the canvas, so
            // an image under a `gs` soft mask rendered unmasked while the SVG exporter emitted the
            // mask reference — one file, two answers.
            if (TryDrawImageWithMaskShader(img, m, sampling, paint)) return;
            DrawWithOptionalSoftMask(new ScreenImageOp(img, m, sampling, paint));
        }

        // FillPathWithShader and StrokePathWithShader — shader fill and stroke entry points — used
        // to live here and are DELETED rather than left unreferenced. Pattern painting goes through
        // DrawPatternPath, which has been their only caller's replacement for some time; a grep of
        // src/ and tests/ found neither name outside its own declaration.
        //
        // Leaving them would have been worse than dead weight, because they had already drifted
        // from the live method in ways a reader would copy back: the fill built its own SKPaint and
        // so dropped BOTH the /ca alpha constant and the §11.6.4 soft mask, and the stroke dropped
        // the same two. That is the shape this repository keeps finding — a plausible near-copy of
        // the real thing, one graphics-state property short.
        //
        // Their deletion is what surfaced the live defect above: the large-miter workaround's
        // `shader` parameter existed only for StrokePathWithShader, so removing it would have
        // orphaned a capability the pattern route needed and did not have. DrawPatternPath now
        // takes it, which is why that parameter is still read.

        // ----------------------------------------------------------------
        //  Clip path
        // ----------------------------------------------------------------

        public void ClipPath(bool evenOdd = false)
        {
            var fillType = evenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
            using var clip = new SKPath(_currentPath) { FillType = fillType };
            _canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
            NoteInkClip(clip);
            // Do NOT clear the path here. For combined W+paint (e.g. W f, W S),
            // the same path is used for both the clip and the paint operation.
            // The terminal operator (f, S, n, etc.) is responsible for clearing the path.
        }

        /// <summary>
        /// Intersects the clip with a <b>quadrilateral</b> whose four corners are already in
        /// page-point space (the space <see cref="TransformPoint"/> produces). Used to honour a
        /// shading's <c>/BBox</c> (§8.7.4.3).
        /// </summary>
        /// <remarks>
        /// <para><b>A quad and not a rect, because an affine CTM maps a box to a parallelogram.</b>
        /// An axis-aligned <c>SKRect</c> fitted to two opposite corners is only the right answer when
        /// the matrix has no rotation or skew: at 45° the two transformed corners of a square share an
        /// x, so the fitted rect would have <i>zero width</i> and the shading <b>paint nothing at
        /// all</b>.</para>
        ///
        /// <para><b>The SVG writer already did this and wrote down why</b> — its <c>EmitBBoxClip</c>
        /// emits a closed quadrilateral and its remarks say a rotated or skewed matrix maps the box
        /// to a parallelogram an <c>x/y/width/height</c> rect cannot express. The reasoning was in
        /// the tree and the raster half never got it, which is this repository's most-repeated
        /// rendering defect.</para>
        ///
        /// <para>Antialiased, unlike the rect clip it replaces: a rotated edge that is not
        /// pixel-aligned needs it, and an axis-aligned quad is unaffected either way.</para>
        /// </remarks>
        public void ClipToPageQuad(float ax, float ay, float bx, float by,
                                   float cx, float cy, float dx, float dy)
        {
            // Through SKPathBuilder, not the obsolete SKPath mutators: the five deliberate CS0618
            // sites in this tree are all _currentPath mutators kept for reads SKPathBuilder cannot
            // do, and this write has no such excuse.
            using var builder = new SKPathBuilder();
            builder.MoveTo(ax, ay);
            builder.LineTo(bx, by);
            builder.LineTo(cx, cy);
            builder.LineTo(dx, dy);
            builder.Close();
            using SKPath quad = builder.Snapshot();

            _canvas.ClipPath(quad, SKClipOperation.Intersect, antialias: true);
            if (TrackingInk) NoteInkClip(quad);
        }

        // ----------------------------------------------------------------
        //  Soft-mask compositing
        // ----------------------------------------------------------------

        /// <summary>Fills or strokes the current path with the given shader (for tiling patterns).</summary>
        /// <remarks>
        /// <para>The stroke case builds its paint from <see cref="BuildStrokePaint"/> so line width,
        /// cap, join, miter and dash all apply — <c>Style = Stroke</c> alone would leave SKPaint's
        /// default 0 stroke width, every pattern-stroked path a hairline regardless of the <c>w</c>
        /// operator.</para>
        ///
        /// <para><b>And it takes both §8.4.3.5 miter workarounds.</b> A pattern stroke is a stroke: the same acute join that makes Skia drop the spike
        /// for a solid pen makes it drop the spike for a patterned one. Under a `333 M` limit at a
        /// ratio near 300, a 1-point solid stroke drew a 131-point spike and the identical pattern
        /// stroke drew <b>none</b> — measured, in the same file.</para>
        ///
        /// <para>The capability was not missing, only unreachable. <c>TryStrokeLargeMiters</c> has
        /// always taken a <c>shader</c> and carried a soft-mask branch commented for "a
        /// pattern-stroked path" — written for <c>StrokePathWithShader</c>, which lost its last
        /// caller when pattern painting moved here. Live code reachable only from dead code, so
        /// deleting the dead method is what surfaced it.</para>
        /// </remarks>
        /// <param name="shader">The pattern's shader.</param>
        /// <param name="stroke">Stroke the path rather than fill it.</param>
        /// <param name="evenOdd">Fill by the even-odd rule.</param>
        /// <param name="plateReady">
        /// True when the shader's pixels are already a separation plate's grey — a tiling cell
        /// rendered under the <see cref="Plate"/> — so its sRGB filter must not convert them again.
        /// A shading pattern's shader is sRGB and is converted.
        /// </param>
        public void DrawPatternPath(SKShader shader, bool stroke, bool evenOdd = false, bool plateReady = false)
        {
            // See PaintBoundsRecorder's remarks and ReportedPathBounds' — a pattern-filled or
            // -stroked path is still exactly _currentPath's geometry (the shader only supplies
            // colour), so this reports the same bound FillPath/StrokePath would for this path,
            // through the one shared implementation rather than a third copy of it. A tiling
            // pattern's own /BBox tiling past the path is a cosmetic edge not chased here.
            if (PaintBoundsRecorder != null && HiddenContentDepth == 0)
                PaintBoundsRecorder(ReportedPathBounds(stroke));

            try
            {
                // See PaintSuppressed's remarks. NewPath() below still runs on this branch (it is
                // in the finally, not here) -- the path must clear exactly as it would if this
                // painted, or the next path-construction operator accumulates onto stale geometry.
                if (PaintIsSuppressed) return;
                // A pattern paints through a shader, a pixel at a time: the shading's price.
                if (_currentPath != null
                    && !(stroke ? ChargeStroke(RenderBudget.ShadingPaintWeight)
                                : ChargePaint(_currentPath.Bounds, RenderBudget.ShadingPaintWeight)))
                    return;

                using var paint = stroke ? BuildStrokePaint() : BuildFillPaint(evenOdd);
                // The shader supplies the colour; keep only the geometry/alpha from the built paint.
                paint.Shader = shader;
                paint.Color  = paint.Color.WithAlpha(
                    PdfColor.Channel(stroke ? _gs.StrokeAlpha : _gs.FillAlpha));
                // Decided again now that the paint carries a shader — a pattern cell may be partly
                // transparent, which the colour the paint was built with cannot show.
                ApplyElementBlend(paint, _gs.BlendMode, translucent: true);
                if (Plate != null && !plateReady) ApplyPlateFilter(paint, "a shading pattern");
                else if (Plate != null) NotePlateStateApproximations();

                if (!stroke)
                {
                    if (!TryDrawPathWithMaskShader(_currentPath, paint))
                        DrawWithOptionalSoftMask(SoftMaskLayerBounds(_currentPath, paint),
                                                 new PathDrawOp(_currentPath, paint));
                    return;
                }

                // The manual large-miter stroke paints the whole thing itself, mask included.
                if (TryStrokeLargeMiters(paint, fillColor: null, shader: shader)) return;

                // The other half of the pair: pre-flatten Béziers so Skia derives an acute
                // line-to-curve miter from a truer tangent. Both are what StrokePath does.
                SKPath drawPath = GetStrokePathForJoin();
                try
                {
                    if (!TryDrawPathWithMaskShader(drawPath, paint))
                        DrawWithOptionalSoftMask(SoftMaskLayerBounds(drawPath, paint),
                                                 new PathDrawOp(drawPath, paint));
                }
                finally
                {
                    if (!ReferenceEquals(drawPath, _currentPath)) drawPath.Dispose();
                }
            }
            finally
            {
                NewPath();
            }
        }

        /// <summary>Fills the current clip region with the given shader.</summary>
        /// <remarks>
        /// The <c>sh</c> operator paints with the current alpha constant and soft mask
        /// (PDF §11.6.4.2), so a shading under <c>ca 0.5</c> or an ExtGState <c>/SMask</c> is not
        /// painted fully opaque and unmasked.
        /// </remarks>
        /// <param name="shader">The shading's shader.</param>
        /// <param name="plateReady">True when the shader's colours are already the <see cref="Plate"/>'s greys (the shading was sampled for it), so its sRGB filter must not convert them again.</param>
        public void DrawShading(SKShader shader, bool plateReady = false)
        {
            // See PaintBoundsRecorder's remarks. sh has no geometry of its own — PaintFloodOp
            // paints the whole canvas, clipped by whatever W/W* (or the page's own edge) already
            // set — so its bound IS the current clip, exactly, not an approximation of it.
            // _canvas.LocalClipBounds is page-point space: ClipPath/ClipRect build the canvas's
            // clip straight from _currentPath (see ClipPath above), the same page-point geometry
            // FillPath/StrokePath/DrawGlyphPath report from, so — unlike DrawBitmap and
            // DrawTextBlob's absolute screen matrices — no UnscaleByDpi is needed here either.
            if (HiddenContentDepth == 0) PaintBoundsRecorder?.Invoke(_canvas.LocalClipBounds);

            // sh has no persistent state to clean up either way -- unlike DrawPatternPath, this
            // gate can sit before anything else is built.
            if (PaintIsSuppressed) return;
            if (!ChargeDevicePaint(_canvas.DeviceClipBounds, RenderBudget.ShadingPaintWeight)) return;

            using var paint = new SKPaint
            {
                Shader      = shader,
                IsAntialias = true,
                Color       = SKColors.Black.WithAlpha(PdfColor.Channel(_gs.FillAlpha)),
            };
            ApplyElementBlend(paint, _gs.BlendMode);
            if (Plate != null && !plateReady) ApplyPlateFilter(paint, "a shading");
            else if (Plate != null) NotePlateStateApproximations();
            if (!TryDrawWithMaskShader(paint, _canvas.TotalMatrix, new PaintFloodOp(paint)))
                DrawWithOptionalSoftMask(new PaintFloodOp(paint));
        }

        /// <summary>
        /// Converts a paint's sRGB pixels to the <see cref="Plate"/>'s grey and notes the
        /// approximation: an image, a shading or a shading pattern has no components left by the
        /// time it is composited (<see cref="SeparationPlate.NoteApproximation"/>).
        /// </summary>
        private void ApplyPlateFilter(SKPaint paint, string what)
        {
            paint.ColorFilter = Plate.SrgbFilter;
            Plate.NoteApproximation(what);
        }

        // ----------------------------------------------------------------
        //  Paint builders
        // ----------------------------------------------------------------

        private static float Dist(SKPoint a, SKPoint b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        // ----------------------------------------------------------------
        //  Large-miter joins drawn manually (PDF §8.4.3.5)
        // ----------------------------------------------------------------
        //
        // SkiaSharp caps the miter join internally: it renders miters faithfully
        // up to a ratio of roughly 50, then abruptly drops the spike for sharper
        // joins *regardless of the requested miter limit* (verified directly —
        // limits of 10/50/333/5000 all produce the same stub once the join is
        // acute enough).  raw/LargeMitreLimit.pdf (line↔line) and
        // raw/LargeMitreLimit-Beziers.pdf (line↔curve) both rely on a 333 miter
        // limit producing very long spikes that Skia simply will not draw.
        //
        // When a path actually contains such a join — the miter is permitted by
        // the limit yet sharper than Skia draws — we render the whole stroke
        // ourselves: stroke the body with a *bevel* join (Skia never attempts the
        // unreliable miter), then graft an explicit triangle onto each bevel that
        // extends it to the true miter apex.  The triangle's base is exactly the
        // bevel chord and its sides run along the stroke edges, so the union is
        // seamless.  Everything is unioned into one fill path and painted once, so
        // a translucent stroke (this file uses CA 0.6) composites correctly.
        //
        // This only engages when the miter limit is large (> ~30); ordinary
        // documents keep the default limit of 10, where any join sharper than the
        // limit simply bevels and Skia handles it natively.
        private const float SafeSkiaMiterRatio = 30f;

        private readonly struct MiterJoin
        {
            public readonly SKPoint V, InDir, OutDir;
            public MiterJoin(SKPoint v, SKPoint inDir, SKPoint outDir) { V = v; InDir = inDir; OutDir = outDir; }
        }

        private bool TryStrokeLargeMiters(SKPaint strokePaint, SKColor? fillColor, SKShader shader)
        {
            if ((PdfLineJoin)_gs.LineJoin != PdfLineJoin.Miter) return false;   // miter join only
            float limit = _gs.MiterLimit;
            if (limit <= SafeSkiaMiterRatio) return false;       // limit too small to permit a Skia-defeating miter
            float halfW = strokePaint.StrokeWidth * 0.5f;
            if (halfW <= 0f) return false;

            // A miter join needs two adjoining segments, so it needs at least three points.
            // Without this, every stroke of a straight line or a single curve walks the whole path
            // and allocates a join list to discover there is nothing to do — and it does so for the
            // *default* miter limit of 10, which is above SafeSkiaMiterRatio and so never takes the
            // cheap exit above. Measured on a page of 2 400 single-segment strokes.
            if (_currentPath == null || _currentPath.PointCount < 3) return false;

            var joins = CollectMiterJoins();
            if (joins.Count == 0) return false;

            var triangles = new System.Collections.Generic.List<(SKPoint b1, SKPoint apex, SKPoint b2)>();
            bool needManual = false;
            foreach (var j in joins)
            {
                if (!TryComputeMiter(j, halfW, out SKPoint b1, out SKPoint apex, out SKPoint b2, out float ratio))
                    continue;                                     // straight / degenerate / infinite miter
                if (ratio > limit + 1e-3f) continue;              // miter exceeds limit → bevel (Skia draws it)
                if (ratio > SafeSkiaMiterRatio) needManual = true;
                triangles.Add((b1, apex, b2));
            }
            if (!needManual) return false;

            // Body outline with a bevel join (Skia never attempts the miter here).
            using var bevelPaint = strokePaint.Clone();
            bevelPaint.StrokeJoin = SKStrokeJoin.Bevel;
            // GetFillPath's obsolete overload filled an SKPath; the current one fills a
            // builder, so the stroke outline is snapshotted once here and everything below
            // (Op unions, PointCount, disposal) keeps working on an ordinary SKPath.
            SKPath outline;
            using (var outlineBuilder = new SKPathBuilder())
            {
                if (!bevelPaint.GetFillPath(_currentPath, outlineBuilder))
                    return false;
                outline = outlineBuilder.Snapshot();
            }

            try
            {
                if (outline.PointCount == 0)
                    return false;

                foreach (var (b1, apex, b2) in triangles)
                {
                    using var triBuilder = new SKPathBuilder();
                    triBuilder.MoveTo(b1);
                    triBuilder.LineTo(apex);
                    triBuilder.LineTo(b2);
                    triBuilder.Close();
                    using var tri = triBuilder.Snapshot();
                    SKPath merged = outline.Op(tri, SKPathOp.Union);
                    if (merged == null) continue;
                    outline.Dispose();
                    outline = merged;
                }

                using var fill = new SKPaint
                {
                    Style       = SKPaintStyle.Fill,
                    IsAntialias = true,
                };
                if (shader != null)
                {
                    fill.Shader = shader;
                    // A shader supplies the colour, but the paint's own alpha still modulates it —
                    // and a pattern stroke's /CA is carried in the stroke paint's colour. Leaving
                    // this at SKPaint's default opaque black painted the spike fully opaque under
                    // `CA 0.5` while the stroke body beside it was half transparent.
                    fill.Color = fill.Color.WithAlpha(strokePaint.Color.Alpha);
                }
                else fill.Color = fillColor ?? strokePaint.Color;
                // The stroke paint's own blend, decided again for this paint's colour and shader
                // (the stroke paint was built from the same graphics state).
                ApplyElementBlend(fill, _gs.BlendMode);

                if (_gs.ActiveSoftMask != null)
                {
                    // `fill` may carry a shader (a pattern-stroked path); it composes with the mask too.
                    if (!TryDrawPathWithMaskShader(outline, fill))
                        DrawWithSoftMask(SoftMaskLayerBounds(outline, fill),
                                         new PathDrawOp(outline, fill));
                }
                else
                    _canvas.DrawPath(outline, fill);
            }
            finally
            {
                outline.Dispose();
            }
            return true;
        }

        // Walks the *original* current path (curve verbs intact) and returns each
        // interior join with the true unit tangents arriving at and leaving the
        // vertex.  Curve endpoints use the analytic tangent, not a flattened chord.
        private System.Collections.Generic.List<MiterJoin> CollectMiterJoins()
        {
            var joins = new System.Collections.Generic.List<MiterJoin>();
            var pts = new SKPoint[4];
            using var it = _currentPath.CreateRawIterator();
            SKPoint subStart = default, cur = default, inDir = default, firstOutDir = default;
            bool haveIn = false, haveFirstOut = false;
            SKPathVerb verb;
            while ((verb = it.Next(pts)) != SKPathVerb.Done)
            {
                switch (verb)
                {
                    case SKPathVerb.Move:
                        subStart = pts[0]; cur = pts[0]; haveIn = false; haveFirstOut = false;
                        break;
                    case SKPathVerb.Line:
                        AddSegment(joins, ref cur, ref inDir, ref haveIn, ref firstOutDir, ref haveFirstOut,
                                   Sub(pts[1], cur), Sub(pts[1], cur), pts[1]);
                        break;
                    case SKPathVerb.Quad:
                        AddSegment(joins, ref cur, ref inDir, ref haveIn, ref firstOutDir, ref haveFirstOut,
                                   FirstNonZero(Sub(pts[1], cur), Sub(pts[2], cur)),
                                   FirstNonZero(Sub(pts[2], pts[1]), Sub(pts[2], cur)), pts[2]);
                        break;
                    case SKPathVerb.Conic:
                        AddSegment(joins, ref cur, ref inDir, ref haveIn, ref firstOutDir, ref haveFirstOut,
                                   FirstNonZero(Sub(pts[1], cur), Sub(pts[2], cur)),
                                   FirstNonZero(Sub(pts[2], pts[1]), Sub(pts[2], cur)), pts[2]);
                        break;
                    case SKPathVerb.Cubic:
                        AddSegment(joins, ref cur, ref inDir, ref haveIn, ref firstOutDir, ref haveFirstOut,
                                   FirstNonZero(Sub(pts[1], cur), FirstNonZero(Sub(pts[2], cur), Sub(pts[3], cur))),
                                   FirstNonZero(Sub(pts[3], pts[2]), FirstNonZero(Sub(pts[3], pts[1]), Sub(pts[3], cur))),
                                   pts[3]);
                        break;
                    case SKPathVerb.Close:
                        if (TryUnit(Sub(subStart, cur), out SKPoint closeDir))
                        {
                            if (haveIn) joins.Add(new MiterJoin(cur, inDir, closeDir));
                            if (haveFirstOut) joins.Add(new MiterJoin(subStart, closeDir, firstOutDir));
                            inDir = closeDir; haveIn = true;
                        }
                        cur = subStart;
                        break;
                }
            }
            return joins;
        }

        private static void AddSegment(
            System.Collections.Generic.List<MiterJoin> joins,
            ref SKPoint cur, ref SKPoint inDir, ref bool haveIn,
            ref SKPoint firstOutDir, ref bool haveFirstOut,
            SKPoint startTangent, SKPoint endTangent, SKPoint end)
        {
            if (TryUnit(startTangent, out SKPoint outDir))
            {
                if (haveIn) joins.Add(new MiterJoin(cur, inDir, outDir));
                if (!haveFirstOut) { firstOutDir = outDir; haveFirstOut = true; }
            }
            if (TryUnit(endTangent, out SKPoint endDir)) { inDir = endDir; haveIn = true; }
            cur = end;
        }

        // Computes the outer bevel corners (b1,b2), the miter apex, and the miter
        // ratio (miter length / line width = 1/sin(θ/2)) for one join.
        private static bool TryComputeMiter(MiterJoin j, float halfW,
            out SKPoint b1, out SKPoint apex, out SKPoint b2, out float ratio)
        {
            b1 = b2 = apex = default; ratio = 0f;
            if (!TryUnit(Sub(j.InDir, j.OutDir), out SKPoint mdir))
                return false;                                     // collinear → no miter

            SKPoint nIn  = new SKPoint(-j.InDir.Y,  j.InDir.X);
            SKPoint nOut = new SKPoint(-j.OutDir.Y, j.OutDir.X);
            if (Dot(nIn,  mdir) < 0f) nIn  = new SKPoint(-nIn.X,  -nIn.Y);   // outward normal of incoming edge
            if (Dot(nOut, mdir) < 0f) nOut = new SKPoint(-nOut.X, -nOut.Y);  // outward normal of outgoing edge

            float denom = 1f + Dot(nIn, nOut);
            if (denom < 1e-6f) return false;                      // true straight reversal → infinite miter, leave to bevel

            SKPoint mv = new SKPoint((nIn.X + nOut.X) / denom, (nIn.Y + nOut.Y) / denom); // (apex-V)/halfW
            ratio = (float)Math.Sqrt(mv.X * mv.X + mv.Y * mv.Y);
            b1   = new SKPoint(j.V.X + nIn.X  * halfW, j.V.Y + nIn.Y  * halfW);
            b2   = new SKPoint(j.V.X + nOut.X * halfW, j.V.Y + nOut.Y * halfW);
            apex = new SKPoint(j.V.X + mv.X   * halfW, j.V.Y + mv.Y   * halfW);
            return true;
        }

        private static SKPoint Sub(SKPoint a, SKPoint b) => new SKPoint(a.X - b.X, a.Y - b.Y);
        private static float Dot(SKPoint a, SKPoint b) => a.X * b.X + a.Y * b.Y;
        private static SKPoint FirstNonZero(SKPoint a, SKPoint b) =>
            (a.X * a.X + a.Y * a.Y) > 1e-12f ? a : b;
        private static bool TryUnit(SKPoint v, out SKPoint u)
        {
            float l = (float)Math.Sqrt(v.X * v.X + v.Y * v.Y);
            if (l < 1e-6f) { u = default; return false; }
            u = new SKPoint(v.X / l, v.Y / l);
            return true;
        }

        // ----------------------------------------------------------------
        //  Coordinate helper
        // ----------------------------------------------------------------

        /// <summary>Transforms a PDF-space point through the current CTM.</summary>
        public (float x, float y) TransformPoint(float x, float y)
        {
            var (cx, cy) = GraphicsState.Transform(_gs.CTM, x, y);
            // Convert from PDF user-space (Y up) to screen space (Y down).
            return (cx, PageHeight - cy);
        }

        // ----------------------------------------------------------------
        //  IDisposable
        // ----------------------------------------------------------------

        public void Dispose()
        {
            _currentPath?.Dispose();
            // A malformed/truncated content stream can open BT and add clip-mode
            // glyphs (Tr 4-7) but never reach ET, leaving _textClipPath allocated.
            _textClipPath?.Dispose();
            _glyphScratchPath?.Dispose();
            // A clip-mode text object abandoned mid-capture (the stream ended, or a refusal) leaves
            // the capture builder open; a completed capture hands it off and nulls the field.
            _glyphCapturePath?.Dispose();
            // Dispose every soft-mask bitmap still attached to a graphics state — the live one
            // (a `gs /SMask` that was never popped before the stream ended) and any left on the
            // stack by an unbalanced `q`. Distinct instances only: after §8.4.2 Table 52 made the
            // mask survive q/Q, several frames routinely share one bitmap, and disposing an
            // SKBitmap twice is an error.
            var seen = new List<SKBitmap>();
            void ReleaseOnce(SKBitmap m)
            {
                if (m == null) return;
                foreach (var s in seen) if (ReferenceEquals(s, m)) return;
                seen.Add(m);
                m.Dispose();
            }
            ReleaseOnce(_gs?.ActiveSoftMask);
            foreach (var saved in _stateStack) ReleaseOnce(saved.ActiveSoftMask);

            // Ink clip paths are owned here for the same reason soft masks are: a q leaves one
            // SKPath referenced by two frames, so no frame may free it.
            DisposeInkClipPaths();

            if (_imageCache != null)
            {
                foreach (var bmp in _imageCache.Values) bmp?.Dispose();
                _imageCache.Clear();
            }

            // Wraps a mask bitmap this context does not own; only the wrapper is released here.
            _maskShaderBase?.Dispose();
            _maskShaderBase = null;
            _maskShaderSource = null;

            _canvas.Restore(); // restore the initial page-flip save
        }

        // ----------------------------------------------------------------
        //  Decoded-image cache
        // ----------------------------------------------------------------

        /// <summary>
        /// Fully post-processed bitmaps, keyed by the image XObject's stream <b>instance</b>.
        /// </summary>
        /// <remarks>
        /// <para><b>Why here.</b> There was no image cache anywhere, so an XObject drawn N times
        /// on a page was decoded N times — N full Flate/LZW/CCITT inflates plus N
        /// sample→<c>SKColor[]</c>→<c>SKBitmap</c> conversions and N runs of the five post-decode
        /// passes. A logo, a watermark or a rule drawn in every table row is the normal case.
        /// The render context is the right owner: it is shared by nested interpreters (forms,
        /// Type 3 glyphs, tiling patterns all receive the parent <c>_ctx</c>), and it already
        /// owns bitmap lifetimes.</para>
        /// <para><b>Reference identity, not value equality.</b> <c>PdfStream</c> derives from
        /// <c>PdfDictionary</c>; keying on anything but the instance risks two different images
        /// with equal dictionaries colliding. The object number is not usable either — an inline
        /// image has none.</para>
        /// <para><b>Bounded.</b> Caching is abandoned once the total exceeds
        /// <see cref="MaxImageCacheBytes"/>: entries already held stay (they are the ones proven
        /// to repeat), new ones are simply not added. No eviction policy, because a wrong one is
        /// worse than none and the ceiling exists to bound memory, not to maximise hit rate.</para>
        /// </remarks>
        private Dictionary<PdfStream, SKBitmap> _imageCache;
        private long _imageCacheBytes;

        /// <summary>
        /// The streams whose decode failed in this render — an image XObject, a tiling cell, a form or
        /// group drawn offscreen — so a later reference draws nothing without paying the decode again.
        /// The bitmap cache remembers only successes, so without this a 64-byte JBIG2 image whose
        /// decode fails would be decoded at every one of 845 <c>Do</c>s, up to 50 ms each. By reference identity, like the bitmap cache; one entry a stream, so bounded by
        /// the document; shared with the offscreen contexts a render opens, like <see cref="Operations"/>.
        /// </summary>
        internal HashSet<PdfStream> FailedDecodes
        {
            get => _failedDecodes ??= new HashSet<PdfStream>(Core.Objects.PdfReferenceEquality.Instance);
            set => _failedDecodes = value;
        }
        private HashSet<PdfStream> _failedDecodes;

        /// <summary>Whether <paramref name="stream"/>'s decode already failed in this render.</summary>
        internal bool DecodeFailedBefore(PdfStream stream) => stream != null && _failedDecodes != null && _failedDecodes.Contains(stream);

        /// <summary>Remembers that <paramref name="stream"/>'s decode failed; the first attempt has reported it.</summary>
        internal void NoteDecodeFailed(PdfStream stream) { if (stream != null) FailedDecodes.Add(stream); }

        /// <summary>
        /// Ceiling on cached decoded images, per render. Deliberately well under the ~400 MB a
        /// single page bitmap may already occupy at the <c>MaxImagePixelArea</c> cap, so the
        /// cache cannot become the memory-exhaustion vector the DoS limits exist to prevent.
        /// </summary>
        internal const long MaxImageCacheBytes = 64L * 1024 * 1024;

        /// <summary>Returns a previously decoded bitmap for this stream, or <c>null</c>.</summary>
        /// <remarks>The context retains ownership — callers must not dispose the result.</remarks>
        public SKBitmap TryGetCachedImage(PdfStream stream)
        {
            if (stream == null || _imageCache == null) return null;
            return _imageCache.TryGetValue(stream, out var bmp) ? bmp : null;
        }

        /// <summary>
        /// Offers a decoded bitmap for reuse. Returns true when the context took ownership; the
        /// caller must then <b>not</b> dispose it. Returns false when the cache is full, leaving
        /// disposal to the caller.
        /// </summary>
        public bool TryCacheImage(PdfStream stream, SKBitmap bitmap)
        {
            if (stream == null || bitmap == null) return false;

            long bytes = (long)bitmap.RowBytes * bitmap.Height;
            if (bytes <= 0 || _imageCacheBytes + bytes > MaxImageCacheBytes) return false;

            // Reference identity for the stream keys: PdfDictionary may compare by value.
            _imageCache ??= new Dictionary<PdfStream, SKBitmap>(Core.Objects.PdfReferenceEquality.Instance);
            if (_imageCache.ContainsKey(stream)) return false; // already held; caller keeps theirs

            _imageCache[stream] = bitmap;
            _imageCacheBytes += bytes;
            return true;
        }

        // ----------------------------------------------------------------
        //  Parsed content-stream operations (render-scoped, bounded)
        // ----------------------------------------------------------------

        /// <summary>
        /// Parsed content-stream operations, memoised so a form drawn N times and a Type 3 glyph
        /// shown N times tokenise once. See <see cref="ContentOperationCache"/>.
        /// </summary>
        /// <remarks>
        /// <para>It hangs off the <i>context</i> rather than the interpreter because nested content
        /// builds a new interpreter each time — form XObjects, Type 3 CharProcs and transparency
        /// groups all construct one — while they share this context. Content rendered offscreen (a
        /// tiling cell, a soft-mask group) gets a context of its own, and that context is handed this
        /// cache (<c>ContentStreamInterpreter.RenderToBitmap</c>), so such a render does not
        /// re-tokenise its content.</para>
        /// <para><b>Assign before rendering starts to widen the lifetime.</b> Left alone, one is
        /// created per render, which is the safe default. <see cref="PdfRenderSession"/> assigns
        /// its own so that repeated renders and tiles of the same page share it; that is the only
        /// way tiles avoid re-tokenising the page, since each tile is otherwise its own render.</para>
        /// </remarks>
        internal ContentOperationCache Operations { get; set; }

        /// <summary>
        /// Returns the parsed operations for <paramref name="data"/>, parsing on first use.
        /// Never returns null; the result is shared and must not be modified.
        /// </summary>
        public List<Core.Text.ContentStreamOperation> GetOrParseOperations(byte[] data)
            => (Operations ??= new ContentOperationCache()).GetOrParse(data);
    }
}

