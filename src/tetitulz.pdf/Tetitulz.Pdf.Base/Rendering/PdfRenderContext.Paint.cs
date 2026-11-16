// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using SkiaSharp;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;

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
        private SKPaint BuildFillPaint(bool evenOdd)
        {
            var color = FillPaintColor();
            // SMask compositing is handled by DrawWithSoftMask (SaveLayer + DstIn) — no alpha hack here.

            // PDF spec §11.7.4: overprint is only defined for DeviceCMYK separations.
            // For a screen RGB renderer, applying DstATop makes black content invisible
            // over an opaque backdrop (src*(1-αDst) = 0 when αDst=1). Ignore overprint
            // flags here and let normal SrcOver compositing produce visually correct output.
            var paint = new SKPaint
            {
                Style       = SKPaintStyle.Fill,
                Color       = color,
                IsAntialias = true,
            };
            ApplyElementBlend(paint, _gs.BlendMode);
            _currentPath.FillType = evenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
            return paint;
        }

        /// <summary>
        /// The stroke paint for the current graphics state: every §8.4.3.2 stroke parameter in one
        /// place, painted with <paramref name="shader"/> when one is given and with the graphics
        /// state's stroke colour otherwise.
        /// </summary>
        /// <param name="shader">
        /// A shader to stroke with — a tiling or shading pattern. <c>null</c> strokes with
        /// <c>_gs.StrokeColor</c> under <c>_gs.StrokeAlpha</c>.
        /// </param>
        /// <remarks>
        /// <para><b>The shader case is not a second copy</b>: a separate <see cref="SKPaint"/> with the
        /// same eight stroke properties would drift — a pattern-stroked path drawing solid where the
        /// file asked for dashes. Every stroke property is written once, so the next parameter added cannot reach one path and miss
        /// the other.</para>
        ///
        /// <para>Shader and colour are set after the initializer rather than through a conditional
        /// inside it, because leaving <c>Color</c> at its default while a shader is set is not the
        /// same as setting both: Skia multiplies the shader by the paint colour's alpha, so a
        /// colour assigned "harmlessly" alongside a shader would silently scale it.</para>
        /// </remarks>
        private SKPaint BuildStrokePaint(SKShader shader = null)
        {
            // PDF spec §8.4.3.2: stroke width is in user space and must be scaled by the CTM.
            // The canvas already has Scale(DpiScale, -DpiScale) applied, so DpiScale is handled
            // automatically when DrawPath is called — do NOT multiply by DpiScale here.
            float ctmScale = StrokeCtmScale();

            var paint = new SKPaint
            {
                Style       = SKPaintStyle.Stroke,
                StrokeWidth = _gs.LineWidth * ctmScale,
                IsAntialias = true,
                StrokeCap   = LineCap(),
                StrokeJoin  = LineJoin(),
                StrokeMiter = _gs.MiterLimit,
            };

            if (shader != null) paint.Shader = shader;
            else paint.Color = StrokePaintColor();
            ApplyElementBlend(paint, _gs.BlendMode);

            if (_gs.DashPattern != null && _gs.DashPattern.Length > 0)
            {
                paint.PathEffect = SKPathEffect.CreateDash(
                    BuildEvenDash(_gs.DashPattern, ctmScale),
                    NormaliseDashPhase(_gs.DashPhase, _gs.DashPattern) * ctmScale);
            }
            return paint;
        }

        /// <summary>The colour a non-stroking paint lays down: the fill colour under <c>ca</c> and the transfer function, or its tint on the <see cref="Plate"/>.</summary>
        private SKColor FillPaintColor() => PaintColor(stroke: false);

        /// <summary>The colour a stroking paint lays down: the stroke colour under <c>CA</c> and the transfer function, or its tint on the <see cref="Plate"/>.</summary>
        private SKColor StrokePaintColor() => PaintColor(stroke: true);

        /// <summary>
        /// The one read of a paint's colour. On a separation plate it is the plate's grey for the
        /// colour's tint — transparent when the paint leaves the plate alone (overprint, or a
        /// <c>/None</c> colorant), which composites to nothing whatever the blend mode.
        /// </summary>
        private SKColor PaintColor(bool stroke)
        {
            SKColor color = stroke ? _gs.StrokeColor : _gs.FillColor;
            float alpha = stroke ? _gs.StrokeAlpha : _gs.FillAlpha;
            if (Plate == null) return ApplyAlpha(color, alpha);

            NotePlateStateApproximations();
            if (!Plate.TryTint(stroke ? _gs.StrokeSepTints : _gs.FillSepTints,
                               stroke ? _gs.StrokeCmyk : _gs.FillCmyk, color,
                               stroke ? _gs.OverprintStroke : _gs.OverprintFill, _gs.OverprintMode,
                               out float tint, stroke ? _gs.StrokeDeviceGray : _gs.FillDeviceGray,
                               _gs.Intent, stroke ? _gs.StrokeColorSpaceArray : _gs.FillColorSpaceArray,
                               stroke ? _gs.StrokeDeviceRgb : _gs.FillDeviceRgb, _gs.BlackGeneration))
                return SKColors.Transparent;
            return SeparationPlate.PlateColor(tint, PdfColor.Channel(alpha));
        }

        /// <summary>
        /// The colour a stencil mask's painted samples take (§8.9.6.2: the current fill colour) —
        /// opaque, since the image draw applies <c>ca</c>. On a plate, the plate's grey, or
        /// transparent when the fill leaves the plate alone.
        /// </summary>
        internal SKColor StencilColor()
        {
            if (Plate == null) return _gs.FillColor;
            return Plate.TryTint(_gs.FillSepTints, _gs.FillCmyk, _gs.FillColor, _gs.OverprintFill, _gs.OverprintMode, out float tint,
                                 _gs.FillDeviceGray, _gs.Intent, _gs.FillColorSpaceArray,
                                 _gs.FillDeviceRgb, _gs.BlackGeneration)
                ? SeparationPlate.PlateColor(tint, 255)
                : SKColors.Transparent;
        }

        /// <summary>
        /// The graphics-state parameters one plate cannot express, noted on it: a transfer function
        /// (§10.4 maps device components, which a plate's tint is not routed through here) and a
        /// non-separable blend mode (§11.3.5.3 mixes the colorants, which one plate cannot see).
        /// </summary>
        private void NotePlateStateApproximations()
        {
            if (_gs.TransferLut != null) Plate.NoteApproximation("a transfer function");
            if (IsNonSeparableBlend(_gs.BlendMode)) Plate.NoteApproximation($"the non-separable blend mode {_gs.BlendMode}");
        }

        private static bool IsNonSeparableBlend(string mode) =>
            mode == PdfBlendModes.Hue || mode == PdfBlendModes.Saturation
            || mode == PdfBlendModes.Color || mode == PdfBlendModes.Luminosity;

        private SKColor ApplyAlpha(SKColor color, float alpha)
        {
            byte a = PdfColor.Channel(alpha);
            // Apply the graphics-state transfer function (/TR, /TR2) to fill/stroke colours.
            // PDF §10.4: transfer functions remap component values at paint time. The LUTs are
            // precomputed in the interpreter when /TR is set (null = identity).
            return TransferFunctionLut.Apply(_gs.TransferLut, color).WithAlpha(a);
        }

        // §8.4.3.6 lives in DashPattern — the one implementation, which the SVG writer also
        // calls; these forward to it.
        private static float[] BuildEvenDash(float[] pattern, float scale)
            => DashPattern.ToEvenLengthScaled(pattern, scale);

        private static float EffectiveDashPeriod(float[] pattern)
            => DashPattern.EffectiveDashPeriod(pattern);

        private static float NormaliseDashPhase(float phase, float[] pattern)
            => DashPattern.NormalisePhase(phase, pattern);

        private static SKBlendMode MapBlendMode(string pdfBM)
        {
            switch (pdfBM)
            {
                case PdfBlendModes.Multiply:   return SKBlendMode.Multiply;
                case PdfBlendModes.Screen:     return SKBlendMode.Screen;
                case PdfBlendModes.Overlay:    return SKBlendMode.Overlay;
                case PdfBlendModes.Darken:     return SKBlendMode.Darken;
                case PdfBlendModes.Lighten:    return SKBlendMode.Lighten;
                case PdfBlendModes.ColorDodge: return SKBlendMode.ColorDodge;
                case PdfBlendModes.ColorBurn:  return SKBlendMode.ColorBurn;
                case PdfBlendModes.HardLight:  return SKBlendMode.HardLight;
                case PdfBlendModes.SoftLight:  return SKBlendMode.SoftLight;
                case PdfBlendModes.Difference: return SKBlendMode.Difference;
                case PdfBlendModes.Exclusion:  return SKBlendMode.Exclusion;
                case PdfBlendModes.Hue:        return SKBlendMode.Hue;
                case PdfBlendModes.Saturation: return SKBlendMode.Saturation;
                case PdfBlendModes.Color:      return SKBlendMode.Color;
                case PdfBlendModes.Luminosity: return SKBlendMode.Luminosity;
                default:                       return SKBlendMode.SrcOver; // Normal
            }
        }

        private SKStrokeCap LineCap() => (PdfLineCap)_gs.LineCap switch
        {
            PdfLineCap.Round            => SKStrokeCap.Round,
            PdfLineCap.ProjectingSquare => SKStrokeCap.Square,
            _                           => SKStrokeCap.Butt
        };

        private SKStrokeJoin LineJoin() => (PdfLineJoin)_gs.LineJoin switch
        {
            PdfLineJoin.Round => SKStrokeJoin.Round,
            PdfLineJoin.Bevel => SKStrokeJoin.Bevel,
            _                 => SKStrokeJoin.Miter
        };

        // ----------------------------------------------------------------
        //  Miter-join curve flattening (PDF §8.4.3.5)
        // ----------------------------------------------------------------
        //
        // SkiaSharp computes a miter join from its *flattened* approximation of
        // an adjoining Bézier rather than from the analytic tangent at the shared
        // vertex.  The first/last sub-segment of Skia's flattening points less
        // steeply than the true tangent, so at an acute line↔curve join the miter
        // is clamped far below the requested limit and the long "spike" the spec
        // mandates barely appears (see raw/LargeMitreLimit-Beziers.pdf).  When a
        // miter join is active we therefore pre-flatten Béziers ourselves to a
        // fine tolerance: within a smooth curve consecutive segments are
        // near-collinear (miter ratio ≈ 1, so no spurious spikes), while the
        // segment adjacent to a real path vertex now carries a far truer tangent
        // and Skia honours the miter limit.  Round/bevel joins never form spikes,
        // so they keep the faster, higher-quality native curve stroke.
        private const float MiterFlattenTolerancePx = 0.1f;

        // Path handed to the stroker.  Returns a flattened *copy* (which the
        // caller must dispose) only for a miter join over a path that actually
        // contains curves; otherwise returns _currentPath unchanged.
        private SKPath GetStrokePathForJoin()
        {
            if ((PdfLineJoin)_gs.LineJoin != PdfLineJoin.Miter) return _currentPath;
            // No interior join, nothing for a miter to do. A path of at most two verbs is a Move
            // plus a single open segment: its two ends are CAPS, not joins, and a Close would be a
            // third verb — so this cannot skip a closed subpath, which does join to itself.
            // Cheap enough to be worth asking first: VerbCount is O(1) where the flattening walks
            // every segment and emits many.
            if (_currentPath.VerbCount <= 2) return _currentPath;

            const SKPathSegmentMask curveMask =
                SKPathSegmentMask.Quad | SKPathSegmentMask.Conic | SKPathSegmentMask.Cubic;
            if ((_currentPath.SegmentMasks & curveMask) == 0) return _currentPath;
            return FlattenCurvesForMiter(_currentPath);
        }

        private static SKPath FlattenCurvesForMiter(SKPath src)
        {
            using var dst = new SKPathBuilder { FillType = src.FillType };
            var pts = new SKPoint[4];
            using var it = src.CreateRawIterator();
            SKPathVerb verb;
            while ((verb = it.Next(pts)) != SKPathVerb.Done)
            {
                switch (verb)
                {
                    case SKPathVerb.Move:  dst.MoveTo(pts[0]); break;
                    case SKPathVerb.Line:  dst.LineTo(pts[1]); break;
                    case SKPathVerb.Quad:  FlattenQuad(dst, pts[0], pts[1], pts[2], 0); break;
                    case SKPathVerb.Conic: FlattenConic(dst, pts[0], pts[1], pts[2], it.ConicWeight()); break;
                    case SKPathVerb.Cubic: FlattenCubic(dst, pts[0], pts[1], pts[2], pts[3], 0); break;
                    case SKPathVerb.Close: dst.Close(); break;
                }
            }
            // Snapshot: the caller disposes what it is handed (and compares it by reference
            // against _currentPath to decide whether to), so it must own an independent path.
            return dst.Snapshot();
        }

        private static SKPoint Mid(SKPoint a, SKPoint b) =>
            new SKPoint((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);

        // Recursive de Casteljau flatten.  The depth cap is a DoS guard against
        // adversarial (e.g. NaN-adjacent) control points; normal curves flatten
        // well under it.
        private static void FlattenCubic(SKPathBuilder dst, SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3, int depth)
        {
            if (depth >= 18 || CubicIsFlat(p0, p1, p2, p3))
            {
                dst.LineTo(p3);
                return;
            }
            SKPoint p01 = Mid(p0, p1), p12 = Mid(p1, p2), p23 = Mid(p2, p3);
            SKPoint p012 = Mid(p01, p12), p123 = Mid(p12, p23), p0123 = Mid(p012, p123);
            FlattenCubic(dst, p0, p01, p012, p0123, depth + 1);
            FlattenCubic(dst, p0123, p123, p23, p3, depth + 1);
        }

        // Standard cubic flatness test: max squared distance of the control
        // points from the chord, compared against (4·tol)².
        private static bool CubicIsFlat(SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3)
        {
            float ux = 3f * p1.X - 2f * p0.X - p3.X; ux *= ux;
            float uy = 3f * p1.Y - 2f * p0.Y - p3.Y; uy *= uy;
            float vx = 3f * p2.X - p0.X - 2f * p3.X; vx *= vx;
            float vy = 3f * p2.Y - p0.Y - 2f * p3.Y; vy *= vy;
            if (ux < vx) ux = vx;
            if (uy < vy) uy = vy;
            return ux + uy <= 16f * MiterFlattenTolerancePx * MiterFlattenTolerancePx;
        }

        private static void FlattenQuad(SKPathBuilder dst, SKPoint p0, SKPoint p1, SKPoint p2, int depth)
        {
            if (depth >= 18 || QuadIsFlat(p0, p1, p2))
            {
                dst.LineTo(p2);
                return;
            }
            SKPoint p01 = Mid(p0, p1), p12 = Mid(p1, p2), p012 = Mid(p01, p12);
            FlattenQuad(dst, p0, p01, p012, depth + 1);
            FlattenQuad(dst, p012, p12, p2, depth + 1);
        }

        private static bool QuadIsFlat(SKPoint p0, SKPoint p1, SKPoint p2)
        {
            float ux = p1.X * 2f - p0.X - p2.X;
            float uy = p1.Y * 2f - p0.Y - p2.Y;
            return ux * ux + uy * uy <= 16f * MiterFlattenTolerancePx * MiterFlattenTolerancePx;
        }

        // Conics never arise from PDF content streams (only cubics and lines),
        // but a defensive uniform sampling keeps the flattener total.  Step count
        // scales with the control-polygon length so high-DPI conics stay smooth.
        private static void FlattenConic(SKPathBuilder dst, SKPoint p0, SKPoint p1, SKPoint p2, float w)
        {
            float poly = Dist(p0, p1) + Dist(p1, p2);
            int n = SaturatingCast.ToInt(poly / 3f) + 4;
            if (n < 8) n = 8; else if (n > 256) n = 256;
            for (int i = 1; i <= n; i++)
            {
                float t = (float)i / n;
                float u = 1f - t;
                float b0 = u * u, b1 = 2f * u * t * w, b2 = t * t;
                float denom = b0 + b1 + b2;
                if (denom == 0f) { dst.LineTo(p2); return; }
                dst.LineTo((b0 * p0.X + b1 * p1.X + b2 * p2.X) / denom,
                           (b0 * p0.Y + b1 * p1.Y + b2 * p2.Y) / denom);
            }
        }
    }
}
