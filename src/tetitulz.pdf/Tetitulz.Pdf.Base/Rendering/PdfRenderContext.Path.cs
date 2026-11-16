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
        /// <summary>Concatenates <paramref name="m"/> onto the current CTM.</summary>
        public void ConcatCTM(float[] m)
        {
            _gs.CTM = GraphicsState.Compose(m, _gs.CTM);
            ApplyCTMToCanvas();
        }

        private void ApplyCTMToCanvas()
        {
            // The canvas coordinate system already has the page-flip applied via
            // the Save/Scale/Translate in the constructor.  We additionally apply
            // the graphics state CTM so that path drawing lands in the right place.
            // We cannot set the matrix absolutely here because the base flip is baked
            // in via Save(); instead we allow the path-drawing helpers to transform
            // their points manually through the combined CTM.
            // (We keep the canvas in "base flip" space and transform points ourselves.)
        }

        // ----------------------------------------------------------------
        //  Path construction
        // ----------------------------------------------------------------

        // THE FIVE CS0618 WARNINGS BELOW ARE DELIBERATE, and this is the note that stops the
        // next sweep from 'finishing' the SKPathBuilder migration here.
        //
        // Every other deprecated path site in Base was converted, because each built a path once
        // and handed it on. `_currentPath` does not: it is mutated by these operators and READ
        // between them - `FillType` (Paint), `VerbCount` and `SegmentMasks` (the miter check),
        // `Bounds` and `QuickReject` (culling), `SKPathMeasure` (dashing), copy-construction
        // (the q/Q save), and it is handed out as `CurrentPath`. `SKPathBuilder` offers none of
        // those, so converting it means a `Snapshot()` at every read.
        //
        // That is not a style question: `ResetPath` below is `Reset` rather than
        // dispose-and-allocate precisely because this runs once per painting operator and once
        // per `n`, thousands of times on a vector-heavy page. A snapshot per read would put an
        // allocation back on the same path, to remove a warning.
        //
        // If SkiaSharp ever makes `SKPath` genuinely immutable, this becomes a real port with a
        // measurement attached, not a rename.

        public void MoveTo(float x, float y)
        {
            var (tx, ty) = TransformPoint(x, y);
            _currentPath.MoveTo(tx, ty);
        }

        public void LineTo(float x, float y)
        {
            var (tx, ty) = TransformPoint(x, y);
            _currentPath.LineTo(tx, ty);
        }

        public void CurveTo(float x1, float y1, float x2, float y2, float x3, float y3)
        {
            var (tx1, ty1) = TransformPoint(x1, y1);
            var (tx2, ty2) = TransformPoint(x2, y2);
            var (tx3, ty3) = TransformPoint(x3, y3);
            _currentPath.CubicTo(tx1, ty1, tx2, ty2, tx3, ty3);
        }

        public void ClosePath()
        {
            _currentPath.Close();
        }

        public void AppendRect(float x, float y, float w, float h)
        {
            // PDF rect: (x,y) is bottom-left corner; height goes upward.
            // We add all four corners to ensure correct orientation after Y-flip.
            MoveTo(x, y);
            LineTo(x + w, y);
            LineTo(x + w, y + h);
            LineTo(x, y + h);
            _currentPath.Close();
        }

        public SKPath CurrentPath => _currentPath;

        /// <summary>Starts a fresh path, discarding whatever the current one held.</summary>
        /// <remarks>
        /// <c>Reset</c> rather than dispose-and-allocate. This runs once per path-painting
        /// operator and once per <c>n</c>, so a vector-heavy page does it thousands of times, and
        /// each <c>new SKPath()</c> is a native allocation plus a finalizable managed wrapper.
        /// <para><b>Nothing may be holding the old path</b>: a caller retaining one across this call
        /// has no contract to keep, so reusing the object is safe.</para>
        /// </remarks>
        public void NewPath()
        {
            if (_currentPath == null) _currentPath = new SKPath();
            else _currentPath.Reset();
        }

        /// <summary>
        /// Runs <paramref name="paint"/> against a copy of the current path and restores the
        /// original afterwards, for a paint operator that has a second half still to draw.
        /// </summary>
        /// <remarks>
        /// Needed because a tiling- or shading-pattern paint <b>consumes</b> the path: every exit
        /// from the interpreter's <c>RenderTilingPattern</c> ends in <see cref="NewPath"/>, the
        /// early refusals included. That is right for <c>f</c> and <c>S</c>, and wrong for
        /// <c>B</c>/<c>b</c>, which fill and stroke <i>one</i> path (§8.5.3.1) — the fill half left
        /// the stroke half nothing to draw, so a pattern-stroked <c>B</c> painted its interior and
        /// no border while the SVG export emitted both paints on one element.
        /// <para>The copy, rather than suppressing the clear, is deliberate: <c>BuildFillPaint</c>
        /// writes the even-odd rule onto the path it is handed, and the stroke half must not
        /// inherit it.</para>
        /// </remarks>
        public void PaintPreservingPath(Action paint)
        {
            if (paint == null) return;
            var saved = _currentPath;
            _currentPath = new SKPath(saved);
            try { paint(); }
            finally
            {
                _currentPath?.Dispose();
                _currentPath = saved;
            }
        }

        // ----------------------------------------------------------------
        //  Painting
        // ----------------------------------------------------------------

        /// <summary>
        /// True when the current path cannot touch the canvas's clip, so painting it would be
        /// wasted work.
        /// </summary>
        /// <param name="strokeMargin">
        /// Extra device-space margin for a stroked path. <see cref="SKCanvas.QuickReject(SKPath)"/>
        /// tests the path's <i>geometric</i> bounds, which say nothing about how far the pen
        /// spreads — so a stroke must widen them or a line just outside the clip, whose width or
        /// miter spike reaches inside it, would be culled and silently vanish.
        /// </param>
        /// <remarks>
        /// <para>This exists for tiles. Rendering a page as a 4x4 grid runs the whole content
        /// stream once per tile, so roughly 15/16 of every tile's drawing operations fall outside
        /// it; Skia rejects them eventually, but only after this code has built an
        /// <see cref="SKPaint"/>, resolved colours, and in the stroke case flattened the path for
        /// join handling.</para>
        /// <para><b>Culling must be conservative, because being wrong here deletes artwork and
        /// nothing throws.</b> The margin below uses the miter limit, which bounds how far a spike
        /// may extend past the join, and adds a pixel for antialiasing.</para>
        /// </remarks>
        private bool IsCurrentPathOutsideClip(float strokeMargin = 0f)
        {
            if (_currentPath == null) return false;

            if (strokeMargin <= 0f) return _canvas.QuickReject(_currentPath);

            SKRect bounds = _currentPath.Bounds;
            bounds.Inflate(strokeMargin, strokeMargin);
            return _canvas.QuickReject(bounds);
        }

        /// <summary>
        /// The CTM's uniform scale factor — the geometric mean of its two axis lengths (§8.4.3.2).
        /// </summary>
        /// <remarks>
        /// Three copies of this existed in this file with <b>two different degenerate-CTM guards</b>
        /// (<c>Math.Max(0.001f, …)</c> in one, <c>&lt;= 0 ? 1f</c> in the others). The maths is
        /// shared here; each caller keeps its own guard, because collapsing them would change what
        /// a zero-scale CTM renders — centralising a value is not the same as unifying it.
        /// </remarks>
        private float RawCtmScale() => PdfMatrix.UniformScaleRaw(_gs.CTM);

        /// <summary>The scale <see cref="BuildStrokePaint"/> applies to the pen width.</summary>
        private float StrokeCtmScale()
        {
            float s = RawCtmScale();
            return s <= 0 ? 1f : s;
        }

        /// <summary>Half the pen width, widened by the miter limit, plus a pixel for antialiasing.</summary>
        /// <remarks>
        /// <b>The width must be the CTM-scaled one.</b> <c>BuildStrokePaint</c> paints with
        /// <c>LineWidth × StrokeCtmScale()</c> (§8.4.3.2 — line width is in user space), so a
        /// margin computed from the raw <c>LineWidth</c> is too small under any magnifying CTM.
        /// Under <c>10 0 0 10 0 0 cm</c> a 2 pt line paints 20 pt wide, and a cull margin of 2 pt
        /// would discard a stroke whose pen still reaches 10 pt inside the clip — artwork deleted,
        /// nothing thrown.
        /// </remarks>
        private float StrokeCullMargin()
            => Math.Max(_gs.LineWidth * StrokeCtmScale(), 1f) * Math.Max(1f, _gs.MiterLimit) * 0.5f + 1f;

        /// <summary>
        /// <c>_currentPath.Bounds</c>, padded by <see cref="StrokeCullMargin"/> when
        /// <paramref name="stroke"/> is true — the one implementation of "what bound does this
        /// path's fill or stroke need to report", shared by <see cref="FillPath"/>,
        /// <see cref="StrokePath"/> and <see cref="DrawPatternPath"/> (a pattern-filled or
        /// -stroked path is still exactly <c>_currentPath</c>'s geometry; only the paint's colour
        /// source differs; a tiling pattern's own <c>/BBox</c> is not chased here), so no paint
        /// method carries its own copy of "path bounds, optionally inflated".
        /// </summary>
        private SKRect ReportedPathBounds(bool stroke)
        {
            SKRect bounds = _currentPath.Bounds;
            if (stroke)
            {
                float margin = StrokeCullMargin();
                bounds.Inflate(margin, margin);
            }
            return bounds;
        }

        /// <summary>
        /// <c>B</c>, <c>B*</c>, <c>b</c>, <c>b*</c>: the path filled, then stroked, as ISO 32000-2
        /// §11.7.4.4 composites them — one object, a non-isolated <b>knockout</b> group, so the
        /// stroke composites with the backdrop rather than with the fill.
        /// </summary>
        /// <remarks>
        /// <para><b>The group is not opened; its result is drawn directly.</b> In a non-isolated
        /// knockout group each element composites with the group's initial backdrop — here the page
        /// beneath — and the group then lands at alpha 1, Normal. So the fill, kept out of the
        /// stroke's outline, and then the stroke are each composited onto the page as they are:
        /// where they do not overlap that is the group exactly, and the overlap is the stroke's.</para>
        /// <para><b>Only where it shows</b> (<see cref="StrokeKnocksOutFill"/>): an opaque Normal
        /// stroke with no soft mask covers the fill whichever way it is composited, so the outline
        /// is not computed for ordinary paths.</para>
        /// <para><b>Any two paints of the current path</b>, a pattern-painted half included:
        /// <paramref name="paintFill"/> must leave the path for <paramref name="paintStroke"/>,
        /// which consumes it. A tiling pattern's half is knocked out by the area the stroke covers,
        /// as a solid one is: the stroke outline, whatever the pattern cell paints inside it.</para>
        /// </remarks>
        public void FillThenStroke(Action paintFill, Action paintStroke)
        {
            if (IsOverprintFillStrokeGroup())
            {
                InOverprintFillStrokeGroup(() =>
                {
                    paintFill();
                    paintStroke();
                });
                return;
            }
            using (SKPath outline = StrokeKnocksOutFill() ? StrokeOutline() : null)
            {
                bool clipped = ClipOutStroke(outline, _canvas.TotalMatrix);
                try { paintFill(); }
                finally { if (clipped) _canvas.Restore(); }
            }
            paintStroke();
        }

        /// <summary>
        /// Saves the canvas and clips out <paramref name="strokeOutline"/>, given in the space
        /// <paramref name="matrix"/> maps to the device, so the fill drawn next keeps out from under
        /// its stroke. Returns whether it saved — the caller restores after the fill.
        /// </summary>
        private bool ClipOutStroke(SKPath strokeOutline, SKMatrix matrix)
        {
            if (strokeOutline == null || strokeOutline.IsEmpty) return false;
            _canvas.Save();
            SKMatrix current = _canvas.TotalMatrix;
            _canvas.SetMatrix(matrix);
            _canvas.ClipPath(strokeOutline, SKClipOperation.Difference, antialias: true);
            _canvas.SetMatrix(current);   // the clip is kept in device space; the matrix goes back
            return true;
        }

        /// <summary>How a combined fill and stroke composites under the current state — <see cref="FillStrokeCompositing"/>.</summary>
        private FillStrokeComposite FillStrokeMode()
            => FillStrokeCompositing.Of(_gs.OverprintFill, _gs.OverprintStroke, _gs.FillAlpha, _gs.StrokeAlpha,
                                        PdfBlendModeEntry.IsNormal(_gs.BlendMode), _gs.ActiveSoftMask != null);

        /// <summary>The knockout case of §11.7.4.4: the fill is kept out from under the stroke.</summary>
        private bool StrokeKnocksOutFill() => FillStrokeMode() == FillStrokeComposite.Knockout;

        /// <summary>§11.7.4.4's overprint case, where a group at the alpha and blend mode changes the result.</summary>
        private bool IsOverprintFillStrokeGroup() => FillStrokeMode() == FillStrokeComposite.OverprintGroup;

        /// <summary>
        /// §11.7.4.4's overprint case: a non-isolated, <b>non-knockout</b> group in which the fill
        /// and then the stroke are painted at alpha 1 — so the stroke overprints the fill — and the
        /// group then composited at the original alpha and blend mode.
        /// </summary>
        /// <remarks>
        /// <para>Reached only where the group changes the result (at alpha 1 and Normal blending
        /// the mode is <see cref="FillStrokeComposite.Direct"/>): a layer carries the alpha and the
        /// blend mode, and the two paints inside it go opaque and Normal. The overprinting itself is
        /// the plates' (on a separation plate, a stroke that leaves a colorant alone leaves the fill
        /// beneath it); on the composite it is the paint, as everywhere else.</para>
        /// <para>Paths and text runs alike: <paramref name="paintFillThenStroke"/> paints the two
        /// halves, and inside the group the mode is <see cref="FillStrokeComposite.Direct"/>.</para>
        /// </remarks>
        private void InOverprintFillStrokeGroup(Action paintFillThenStroke)
        {
            float alpha = _gs.StrokeAlpha;
            string blend = _gs.BlendMode;
            float fillAlpha = _gs.FillAlpha;
            using (var layerPaint = new SKPaint { Color = SKColors.Black.WithAlpha(PdfColor.Channel(alpha)) })
            {
                ApplyElementBlend(layerPaint, blend, translucent: true);
                _canvas.SaveLayer(layerPaint);
            }
            _layerDepth++;
            try
            {
                _gs.FillAlpha = _gs.StrokeAlpha = 1f;
                _gs.BlendMode = Core.Constants.PdfBlendModes.Normal;
                paintFillThenStroke();
            }
            finally
            {
                _gs.FillAlpha = fillAlpha;
                _gs.StrokeAlpha = alpha;
                _gs.BlendMode = blend;
                _layerDepth--;
                _canvas.Restore();
            }
        }

        /// <summary>The area the current path's stroke covers, in canvas space, or null when it cannot be computed.</summary>
        private SKPath StrokeOutline()
        {
            using SKPaint strokePaint = BuildStrokePaint();
            SKPath strokePath = GetStrokePathForJoin();
            try
            {
                using var builder = new SKPathBuilder();
                return strokePaint.GetFillPath(strokePath, builder) ? builder.Snapshot() : null;
            }
            finally
            {
                if (!ReferenceEquals(strokePath, _currentPath)) strokePath.Dispose();
            }
        }

        public void StrokePath(bool clearAfter = true)
        {
            // Reported before the clip-cull check below, deliberately: a display-list build pass
            // (PaintBoundsRecorder set) wants the operator's true geometric bound regardless of
            // whether this particular pass's own canvas clip happens to reject it.
            if (PaintBoundsRecorder != null && _currentPath != null && HiddenContentDepth == 0)
                PaintBoundsRecorder(ReportedPathBounds(stroke: true));
            // A hidden section paints nothing (§8.11.3.2); the path still clears, as a painted one does.
            if (HiddenContentDepth > 0 || IsCurrentPathOutsideClip(StrokeCullMargin()) || (_currentPath != null && !ChargeStroke()))
            {
                if (clearAfter) NewPath();
                return;
            }
            // Simulation is the overprint preview's; here, only the report when it is off.
            ReportOverprintUnavailable(stroke: true);
            using var paint = BuildStrokePaint();
            // Very acute miter joins (large miter limit) are drawn manually because
            // SkiaSharp caps the miter internally and drops the spike — see
            // TryStrokeLargeMiters / raw/LargeMitreLimit*.pdf.
            if (!TryStrokeLargeMiters(paint, paint.Color, shader: null))
            {
                SKPath drawPath = GetStrokePathForJoin();
                try
                {
                    if (_gs.ActiveSoftMask != null)
                    {
                        // Strokes take the same shader path as fills: the paint carries the
                        // geometry (width, cap, join, dash) and the mask rides on its shader.
                        if (!TryDrawPathWithMaskShader(drawPath, paint))
                            DrawWithSoftMask(SoftMaskLayerBounds(drawPath, paint),
                                             new PathDrawOp(drawPath, paint));
                    }
                    else
                        _canvas.DrawPath(drawPath, paint);
                }
                finally
                {
                    if (!ReferenceEquals(drawPath, _currentPath)) drawPath.Dispose();
                }
            }
            // PDF spec: end caps apply at every dash endpoint, including when the path
            // ends at a near-zero ON segment (the "end-before-bend" degenerate case).
            // Skia skips sub-pixel ON segments so the cap never renders — draw it explicitly.
            if ((PdfLineCap)_gs.LineCap != PdfLineCap.Butt)
                DrawDashEndpointCaps();
            if (clearAfter) NewPath();
        }

        // Draws a degenerate (zero-length) stroke at each subpath endpoint whose dash
        // phase is in the ON range but shorter than half a PDF user-space unit.  This
        // catches the "path ends at phase≈0" case (e.g. coordinates like 79.99999 that
        // place the path end at exactly the start of an ON period).
        private void DrawDashEndpointCaps()
        {
            if (_gs.DashPattern == null || _gs.DashPattern.Length == 0) return;
            float period = EffectiveDashPeriod(_gs.DashPattern);
            if (period <= 0f) return;

            // Skia's CreateDash resets the dash phase at the start of every contour,
            // so each subpath's terminal phase is measured from this same start phase.
            float startPhase = NormaliseDashPhase(_gs.DashPhase, _gs.DashPattern) % period;
            // A terminal dash phase below this many PDF user-space units means the trailing ON
            // segment is sub-pixel even at 72 DPI (1 unit = 1 pixel), so Skia drops it entirely
            // and we must paint the end cap ourselves.
            const float SubPixelOnSegmentUnits = 0.5f;

            float ctmScale = Math.Max(0.001f, RawCtmScale());

            using var measure = new SKPathMeasure(_currentPath, false, 1f);
            do
            {
                float len = measure.Length;
                if (len <= 1e-3f) continue;          // skip empty / lone-moveto contours
                // Dash phase at the subpath's terminal point.  A value at (or just
                // past) an ON-element start means the path ends inside an ON dash
                // whose visible length is sub-pixel — Skia drops it, so we paint the
                // end cap ourselves.  phase == 0 MUST count: coordinates like 79.99999
                // push the contour length a hair past a dash-period multiple, but that
                // excess is below float epsilon at these magnitudes (600.00001f →
                // 600.0f), collapsing the phase to exactly 0.  The previous
                // `phaseAtEnd > 0f` guard therefore hid the end caps for every such
                // "end before bend" path (PDF 2.0 §8.4.3.6).
                float phaseAtEnd = (startPhase + len) % period;
                if (phaseAtEnd < SubPixelOnSegmentUnits)
                {
                    // Get the path endpoint and direction.
                    if (measure.GetPositionAndTangent(len, out SKPoint pos, out _))
                    {
                        var color     = StrokePaintColor();
                        float halfW   = _gs.LineWidth * ctmScale / 2f;

                        if (LineCap() == SKStrokeCap.Round)
                        {
                            // Skia does not render zero-length strokes with round caps.
                            // Draw a filled circle instead (equivalent to the PDF round cap dot).
                            using var capPaint = new SKPaint
                            {
                                Style     = SKPaintStyle.Fill,
                                Color     = color,
                                IsAntialias = true,
                            };
                            ApplyElementBlend(capPaint, _gs.BlendMode);
                            _canvas.DrawCircle(pos.X, pos.Y, halfW, capPaint);
                        }
                        else
                        {
                            // Square cap: zero-length stroke renders correctly in Skia.
                            using var capBuilder = new SKPathBuilder();
                            capBuilder.MoveTo(pos.X, pos.Y);
                            capBuilder.LineTo(pos.X, pos.Y);
                            using var capPath = capBuilder.Snapshot();
                            using var capPaint = new SKPaint
                            {
                                Style       = SKPaintStyle.Stroke,
                                Color       = color,
                                StrokeWidth = _gs.LineWidth * ctmScale,
                                StrokeCap   = SKStrokeCap.Square,
                                IsAntialias = true,
                            };
                            ApplyElementBlend(capPaint, _gs.BlendMode);
                            _canvas.DrawPath(capPath, capPaint);
                        }
                    }
                }
            }
            while (measure.NextContour());
        }

        public void FillPath(bool evenOdd = false, bool clearAfter = true)
        {
            // Reported before the clip-cull check below — see StrokePath's identical remark.
            if (PaintBoundsRecorder != null && _currentPath != null && HiddenContentDepth == 0)
                PaintBoundsRecorder(ReportedPathBounds(stroke: false));
            // A fill never paints outside the path, so its geometric bounds are exact — no margin.
            // A hidden section paints nothing (§8.11.3.2); the path still clears, as a painted one does.
            if (HiddenContentDepth > 0 || IsCurrentPathOutsideClip()
                || (_currentPath != null && !ChargePaint(_currentPath.Bounds, RenderBudget.FillPaintWeight)))
            {
                if (clearAfter) NewPath();
                return;
            }
            // Simulation is the overprint preview's; here, only the report when it is off.
            ReportOverprintUnavailable(stroke: false);
            if (_gs.ActiveSoftMask != null)
            {
                // BuildFillPaint also sets _currentPath.FillType, so build it before taking
                // bounds — and a fill never paints outside the path, so no stroke margin.
                using var p = BuildFillPaint(evenOdd);
                if (!TryDrawPathWithMaskShader(_currentPath, p))
                    DrawWithSoftMask(SoftMaskLayerBounds(_currentPath, p),
                                     new PathDrawOp(_currentPath, p));
            }
            else
            {
                using var paint = BuildFillPaint(evenOdd);
                _canvas.DrawPath(_currentPath, paint);
            }
            if (clearAfter) NewPath();
        }

        public void FillAndStrokePath(bool evenOdd = false)
        {
            FillPath(evenOdd, clearAfter: false);
            StrokePath(clearAfter: true);
        }

        // ----------------------------------------------------------------
        //  Text rendering
        // ----------------------------------------------------------------
    }
}
