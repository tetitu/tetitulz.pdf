// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Interprets a PDF content stream, executing drawing operators against a
    /// <see cref="PdfRenderContext"/> backed by SkiaSharp.
    ///
    /// <para>Supported operator families:</para>
    /// <list type="bullet">
    ///   <item>Path construction: m l c v y h re</item>
    ///   <item>Path painting: S s F f f* B B* b b* n</item>
    ///   <item>Color: g G rg RG k K cs CS sc SC scn SCN</item>
    ///   <item>Graphics state: q Q cm w J j M d ri i gs</item>
    ///   <item>Text: BT ET Tf Td TD Tm T* Tj TJ ' " Tc Tw Tz TL Tr Ts</item>
    ///   <item>XObject: Do (Form XObjects recursed; Image XObjects rasterized)</item>
    ///   <item>Clipping: W W*</item>
    /// </list>
    /// </summary>
    internal sealed partial class ContentStreamInterpreter
    {
        private void OpSh(ContentStreamOperation op)
        {
            if (op.Operands.Length < 1) return;
            string name = GetName(op, 0);

            // Reported at the operator, before the shader is built: a shading's colour varies per
            // pixel, so there is no single source colorant set the compositor could preserve.
            // A separation plate composites it per colorant, exactly.
            if (_ctx.GS.OverprintFill && _ctx.Plate == null)
                PdfRenderContext.ReportOverprintSkipped(OverprintReportReason.Shading);

            if (_resources?[PdfName.Shading] is not PdfObject shadingRes) return;
            if (_doc.ResolveObject(shadingRes) is not PdfDictionary shadingCat) return;
            var shadingEntry = shadingCat[name];   // internal string indexer — no PdfName alloc
            if (shadingEntry == null) return;
            if (_doc.ResolveObject(shadingEntry) is not PdfDictionary shadingDict) return;

            var shader = BuildShader(shadingDict, forShadingOperator: true);
            if (shader == null) return;

            // A shading /BBox (in the current user space) limits the painted region (PDF §8.7.4.3).
            var bboxArr = PdfResolve.Array(_doc, shadingDict, PdfName.BBox);
            bool bboxClip = bboxArr != null && bboxArr.Count >= 4;
            if (bboxClip)
            {
                _ctx.PushState();
                // ALL FOUR corners, and clipped as a quadrilateral. Transforming two opposite
                // corners and fitting an axis-aligned rect to them is only right when the CTM has
                // no rotation or skew: at 45° the two transformed corners of a square share an x,
                // the fitted rect had zero width, and the shading painted NOTHING. The SVG twin's
                // EmitBBoxClip already emitted a closed quad and its remarks already said why.
                float lx = Math.Min(GetArrayFloat(bboxArr, 0), GetArrayFloat(bboxArr, 2));
                float ly = Math.Min(GetArrayFloat(bboxArr, 1), GetArrayFloat(bboxArr, 3));
                float hx = Math.Max(GetArrayFloat(bboxArr, 0), GetArrayFloat(bboxArr, 2));
                float hy = Math.Max(GetArrayFloat(bboxArr, 1), GetArrayFloat(bboxArr, 3));

                var (ax, ay) = _ctx.TransformPoint(lx, ly);
                var (bx, by) = _ctx.TransformPoint(hx, ly);
                var (cx, cy) = _ctx.TransformPoint(hx, hy);
                var (dx, dy) = _ctx.TransformPoint(lx, hy);
                _ctx.ClipToPageQuad(ax, ay, bx, by, cx, cy, dx, dy);
            }
            // Built from the plate's tints when there is a plate (GetShadingSpace).
            using (shader) _ctx.DrawShading(shader, plateReady: _ctx.Plate != null);
            if (bboxClip) _ctx.PopState();
        }

        /// <param name="shading">The shading dictionary.</param>
        /// <param name="forShadingOperator">
        /// True for <c>sh</c>, which paints the shading's own extent and ignores its
        /// <c>/Background</c> (§8.7.4.3 Table 78: the entry applies to a shading pattern alone, where
        /// it fills the painted region outside the shading); false for a shading pattern.
        /// </param>
        private SKShader BuildShader(PdfDictionary shading, bool forShadingOperator)
        {
            // §8.6.5.6: a shading's device /ColorSpace is the current resources' default for it —
            // `sh` and a shading pattern alike.
            shading = DefaultColorSpaces.WithDefaults(shading, _resources, _doc);
            // Every entry here is read through Resolved*: PdfDictionary's indexer and Get*
            // accessors return the raw stored value, so an indirect /ShadingType or /Coords
            // (legal, and emitted by some producers) silently degraded to "no shading painted".
            int shadingType = ResolvedNumber(shading[PdfName.ShadingType]) is PdfNumber st
                              ? st.IntValue : 0;

            // Type 1: Function-based shading — sample PdfFunctionEvaluator on a grid.
            if (shadingType == 1)
                return BuildFunctionShader(shading);

            // Types 4 and 5: Gouraud triangle meshes — triangulation suffices. They differ in how
            // the topology is expressed, which is why the type is threaded through: type 4 carries
            // a per-vertex edge flag, type 5 carries none and derives its rows from
            // /VerticesPerRow (§8.7.4.5.6).
            if (shadingType == 4 || shadingType == 5)
                return BuildMeshShader(shading, shadingType, background: !forShadingOperator);

            // Types 6 (Coons patches) and 7 (tensor patches) — proper surface
            // evaluation on an adaptive (u, v) grid.  Falls back to the Gouraud
            // approximation when the patch stream can't be parsed cleanly.
            if (shadingType == 6 || shadingType == 7)
                return BuildPatchShader(shading, shadingType) ?? BuildMeshShader(shading, 4, background: !forShadingOperator);

            if (ResolvedArray(shading[PdfName.Coords]) is not PdfArray coords) return null;

            SKColor c0 = SKColors.Black, c1 = SKColors.White;
            if (_doc.ResolveObject(shading[PdfName.Function]) is PdfDictionary fn)
                ResolveGradientColors(fn, shading, out c0, out c1);

            // Sample the shading's colour function into a multi-stop gradient. This is the
            // authoritative colour path: it evaluates ANY function type (0/2/3/4) and a
            // /Function array, so a stitching (Type 3) rainbow renders all of its segments.
            // The two-colour c0/c1 above is only a fallback for when sampling yields nothing.
            var stops = SampleShadingStops(shading, RasterGradientStopCount);
            SKColor[] stopColors = stops?.colors ?? new[] { c0, c1 };
            float[]   stopPositions = stops?.positions; // null ⇒ even spacing for c0/c1

            // /Extend array: [extendStart extendEnd] — whether the gradient continues beyond
            // the domain at each end (PDF §8.7.4.5.3).
            var extendArr = ResolvedArray(shading[PdfName.Extend]);
            bool extStart = extendArr != null && extendArr.Count >= 1 && extendArr[0] is PdfBoolean eb0 && eb0.Value;
            bool extEnd = extendArr != null && extendArr.Count >= 2 && extendArr[1] is PdfBoolean eb1 && eb1.Value;

            // Skia has one tile mode for both ends: Clamp extends both, Decal extends neither.
            // A PARTIAL extend ([true false] / [false true]) falling back to Clamp would extend the
            // end the PDF asked to leave unpainted. Emulate it by pinning a
            // fully-transparent stop at the non-extended end and clamping: Skia treats two stops
            // sharing a position as a hard stop, so the gradient ends exactly at the domain edge
            // and clamps to transparent past it.
            SKShaderTileMode tileMode;
            if (extStart == extEnd)
            {
                tileMode = extStart ? SKShaderTileMode.Clamp : SKShaderTileMode.Decal;
            }
            else
            {
                tileMode = SKShaderTileMode.Clamp;
                PadForPartialExtend(ref stopColors, ref stopPositions, extStart, extEnd);
            }

            if (shadingType == 2 && coords.Count >= 4) // Axial
            {
                // Build the gradient in shading space and let Skia carry it through the CTM
                // (see ShadingLocalMatrix).  Transforming the two endpoints instead is only
                // equivalent under a similarity transform: a skew or non-uniform scale must
                // stop the iso-lines being perpendicular to the axis, and a pair of mapped
                // endpoints cannot express that.
                return SKShader.CreateLinearGradient(
                    new SKPoint(GetArrayFloat(coords, 0), GetArrayFloat(coords, 1)),
                    new SKPoint(GetArrayFloat(coords, 2), GetArrayFloat(coords, 3)),
                    stopColors, stopPositions, tileMode, ShadingLocalMatrix());
            }

            if (shadingType == 3 && coords.Count >= 6) // Radial
            {
                float cx0 = GetArrayFloat(coords, 0), cy0 = GetArrayFloat(coords, 1),
                      r0  = GetArrayFloat(coords, 2);
                float cx1 = GetArrayFloat(coords, 3), cy1 = GetArrayFloat(coords, 4),
                      r1  = GetArrayFloat(coords, 5);
                // Centres and radii both stay in shading space; the CTM is carried by the
                // shader's local matrix (see ShadingLocalMatrix).  A radius is a scalar, so
                // any anisotropy in the CTM cannot be baked into it — the previous code
                // averaged the two axis lengths and then re-stretched the result, which
                // reproduced an axis-aligned non-uniform scale but not a rotation or skew
                // (a rotated ellipse came out axis-aligned).  Deferring the whole transform
                // to Skia is exact for every affine CTM.
                return SKShader.CreateTwoPointConicalGradient(
                    new SKPoint(cx0, cy0), r0,
                    new SKPoint(cx1, cy1), r1,
                    stopColors, stopPositions, tileMode, ShadingLocalMatrix());
            }

            return null;
        }

        /// <summary>
        /// Maps shading space to canvas-local space (Y-down page points): the current CTM
        /// followed by the same page Y-flip that
        /// <see cref="PdfRenderContext.TransformPoint"/> applies to path geometry.
        /// </summary>
        /// <remarks>
        /// Handed to Skia as a gradient's local matrix so the *gradient* is transformed rather
        /// than its control points, which is what keeps a rotated, skewed or non-uniformly
        /// scaled CTM exact (PDF §8.7.4.5.3–4).  A shading <i>pattern</i>'s <c>/Matrix</c> has
        /// already been concatenated into the CTM by the caller, and axial/radial shadings have
        /// no <c>/Matrix</c> entry of their own (that is a type-1 key, Table 79), so the CTM is
        /// the whole transform.  Mirrors the chain the function-based path builds for its
        /// bitmap shader.
        /// </remarks>
        private SKMatrix ShadingLocalMatrix()
            => SKMatrix.Concat(_ctx.PageFlip, GraphicsState.ToSKMatrix(_ctx.GS.CTM));

        /// <summary>
        /// Adds a transparent stop at whichever gradient end is NOT extended, so a partial
        /// <c>/Extend</c> renders correctly under <see cref="SKShaderTileMode.Clamp"/>.
        /// Positions are materialised first when the caller was relying on even spacing.
        /// </summary>
        internal static void PadForPartialExtend(
            ref SKColor[] colors, ref float[] positions, bool extendStart, bool extendEnd)
        {
            if (colors == null || colors.Length == 0) return;

            // Skia requires explicit positions once the array stops being evenly spaced.
            if (positions == null || positions.Length != colors.Length)
            {
                positions = new float[colors.Length];
                for (int i = 0; i < colors.Length; i++)
                    positions[i] = colors.Length == 1 ? 0f : i / (float)(colors.Length - 1);
            }

            int extra = (extendStart ? 0 : 1) + (extendEnd ? 0 : 1);
            if (extra == 0) return;

            var newColors = new SKColor[colors.Length + extra];
            var newPos    = new float[positions.Length + extra];
            int w = 0;

            if (!extendStart)
            {
                newColors[w] = SKColors.Transparent;
                newPos[w]    = positions[0];
                w++;
            }
            for (int i = 0; i < colors.Length; i++, w++)
            {
                newColors[w] = colors[i];
                newPos[w]    = positions[i];
            }
            if (!extendEnd)
            {
                newColors[w] = SKColors.Transparent;
                newPos[w]    = positions[positions.Length - 1];
            }

            colors    = newColors;
            positions = newPos;
        }

        /// <summary>Resolves <paramref name="obj"/> and returns it as a <see cref="PdfArray"/>,
        /// or <c>null</c>. Needed because the dictionary accessors do not follow references.</summary>
        private PdfArray ResolvedArray(PdfObject obj)
            => (_doc != null ? _doc.ResolveObject(obj) as PdfArray : null) ?? obj as PdfArray;

        /// <summary>Resolves <paramref name="obj"/> and returns it as a <see cref="PdfNumber"/>,
        /// or <c>null</c>.</summary>
        private PdfNumber ResolvedNumber(PdfObject obj)
            => (_doc != null ? _doc.ResolveObject(obj) as PdfNumber : null) ?? obj as PdfNumber;

        /// <summary>Reads a shading's optional /Background colour (in the shading's colour
        /// space), or <c>null</c> when absent.</summary>
        private SKColor? ReadShadingBackground(PdfDictionary shading, ShadingSpace space)
        {
            if (PdfResolve.Array(_doc, shading, PdfName.Background) is not PdfArray bg || bg.Count < 1) return null;
            var comps = new float[bg.Count];
            for (int i = 0; i < bg.Count; i++) comps[i] = GetArrayFloat(bg, i);
            return space.ToColor(comps);
        }

        /// <summary>
        /// Samples an axial/radial shading's colour function across its domain into
        /// <paramref name="sampleCount"/> evenly-spaced gradient stops.  Uses
        /// <see cref="Core.Functions.PdfFunctionEvaluator"/> so EVERY PDF function type
        /// (0 sampled, 2 exponential, 3 stitching, 4 PostScript) and a /Function array
        /// (one 1-out function per colour component) are evaluated correctly — including
        /// the interior colours of a multi-segment stitching gradient, which the old
        /// two-endpoint <see cref="ResolveGradientColors"/> path discarded (and which it
        /// rendered as a flat black→white ramp for any non-Type-2 function).
        /// Returns <c>null</c> when no usable colour could be produced.
        /// </summary>
        private (SKColor[] colors, float[] positions)? SampleShadingStops(PdfDictionary shading, int sampleCount)
        {
            var fnObj = _doc.ResolveObject(shading[PdfName.Function]);
            if (fnObj == null) return null;

            var space = GetShadingSpace(shading);

            // Shading /Domain is [t0 t1] for axial/radial (default [0 1]).
            float t0 = 0f, t1 = 1f;
            if (ResolvedArray(shading[PdfName.Domain]) is PdfArray dom && dom.Count >= 2)
            { t0 = GetArrayFloat(dom, 0); t1 = GetArrayFloat(dom, 1); }

            int n = Math.Max(2, sampleCount);
            var colors = new SKColor[n];
            var positions = new float[n];
            bool any = false;
            bool funcGaveUp = false; // stop re-evaluating a function that exhausted its work budget
            for (int i = 0; i < n; i++)
            {
                float u = i / (float)(n - 1);
                positions[i] = u;
                float t = t0 + (t1 - t0) * u;
                if (funcGaveUp) { colors[i] = SKColors.Black; continue; }
                try
                {
                    float[] comps = EvaluateShadingColorFunction(fnObj, t);
                    if (comps != null && comps.Length > 0)
                    {
                        colors[i] = space.ToColor(comps);
                        any = true;
                    }
                    else colors[i] = SKColors.Black;
                }
                catch (Exception ex)
                {
                    colors[i] = SKColors.Black;
                    // An adversarial colour function (work-budget/recursion-depth bomb) would be
                    // re-evaluated — and re-exhausted — for every remaining sample: a
                    // RasterGradientStopCount× amplification here, and up to 512×512 for a
                    // Type-1 shading's grid. Stop sampling it after the first failure.
                    if (ex is WorkBudgetExceededException || ex is InvalidOperationException)
                    {
                        funcGaveUp = true;
                        RenderDiagnostics.Report(RenderDiagnosticKind.MeshShaderFailed,
                            "Shading colour function aborted (work/recursion budget); remaining samples skipped", ex);
                    }
                }
            }
            return any ? (colors, positions) : null;
        }

        /// <summary>
        /// The colour of one mesh or patch vertex.
        ///
        /// <para>With <c>/Function</c> present, a type 4-7 shading's vertex data carries a
        /// <b>single parametric value t</b> rather than n colour components (§8.7.4.5.5), and the
        /// colour is <c>Function(t)</c> in the shading's colour space — handed raw to
        /// <see cref="ComponentsToColor"/>, a DeviceRGB mesh with a function would render as a
        /// red→black ramp (one component landing on the red channel) instead of the gradient the
        /// file describes.</para>
        /// </summary>
        /// <param name="gaveUp">
        /// Set once the function has failed on a budget/recursion abort, after which the raw
        /// components are used for the remaining vertices. Without it an adversarial function is
        /// re-evaluated and re-exhausted up to <c>PdfLimits.MaxMeshVertices</c> times — the same
        /// amplification <see cref="SampleShadingStops"/> guards against.
        /// </param>
        /// <param name="comps">
        /// The vertex's raw components as read from the mesh stream — function inputs when
        /// <paramref name="meshFn"/> is present, colour components otherwise.
        /// </param>
        /// <param name="space">The shading's colour space, used to turn components into a colour.</param>
        /// <param name="meshFn">
        /// The shading's <c>/Function</c>, or null when vertices carry colour directly.
        /// </param>
        private SKColor MeshVertexColor(float[] comps, ShadingSpace space, object meshFn, ref bool gaveUp)
        {
            if (meshFn == null || gaveUp || comps == null || comps.Length == 0)
                return space.ToColor(comps);

            try
            {
                float[] mapped = EvaluateShadingColorFunction(meshFn, comps[0]);
                if (mapped != null && mapped.Length > 0)
                    return space.ToColor(mapped);
            }
            catch (Exception ex)
            {
                gaveUp = true;
                RenderDiagnostics.Report(RenderDiagnosticKind.MeshShaderFailed,
                    "Mesh/patch colour function failed; remaining vertices use raw components", ex);
            }
            return space.ToColor(comps);
        }

        /// <summary>
        /// Evaluates a shading's colour function at a single domain value <paramref name="t"/>.
        /// Handles a single function object (dict or stream) and a /Function array (one
        /// 1-input/1-output function per colour component, concatenated).
        /// </summary>
        private float[] EvaluateShadingColorFunction(object fnObj, float t)
        {
            // §8.7.4.5.2's "a function, or an array of n 1-out functions" is one rule with one
            // implementation, shared with the SVG writer and with both Type 1 paths.
            return Core.Functions.PdfFunctionEvaluator.EvaluateEntry(
                fnObj as PdfObject, new[] { t }, _doc.ResolveObject);
        }

        // Function-based shading (Type 1).
        private SKShader BuildFunctionShader(PdfDictionary shading)
        {
            // Not `as PdfDictionary`: §8.7.4.5.2 permits an ARRAY of n 1-out functions here, and
            // narrowing to a dictionary made a Type 1 shading in that form draw nothing at all.
            var fnObj = _doc.ResolveObject(shading[PdfName.Function]);
            if (!Core.Functions.PdfFunctionEvaluator.IsFunctionEntry(fnObj, _doc.ResolveObject)) return null;

            // Domain: default [0 1 0 1]
            float xMin = 0, xMax = 1, yMin = 0, yMax = 1;
            if (ResolvedArray(shading[PdfName.Domain]) is PdfArray dom && dom.Count >= 4)
            {
                xMin = GetArrayFloat(dom, 0); xMax = GetArrayFloat(dom, 1);
                yMin = GetArrayFloat(dom, 2); yMax = GetArrayFloat(dom, 3);
            }

            var space = GetShadingSpace(shading);

            // Sample on an adaptive grid: larger domain ranges use more samples
            // so smooth gradients don't appear blocky.  We also factor in the
            // current DPI scale — at print resolution (DPI > 1) the shader is
            // bitmap-tiled across many output pixels, so a minimum-size grid
            // produces visible quantisation steps; raise the cap on high-DPI
            // renders.  Clamped to MinSamples..MaxSamples per axis.
            const int MinSamples = 32;   // also the samples-per-unit-of-domain rate
            const int MaxSamples = 512;
            float xRange = Math.Abs(xMax - xMin);
            float yRange = Math.Abs(yMax - yMin);
            int dpiBoost = Math.Max(1, SaturatingCast.ToInt(Math.Ceiling(_ctx.DpiScale)));
            int W = Math.Max(MinSamples, Math.Min(MaxSamples, SaturatingCast.ToInt(xRange * MinSamples * dpiBoost + 0.5f)));
            int H = Math.Max(MinSamples, Math.Min(MaxSamples, SaturatingCast.ToInt(yRange * MinSamples * dpiBoost + 0.5f)));
            using var bitmap = new SKBitmap(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);

            // Fill an SKColor[] and assign Pixels once. SetPixel is a marshalled interop call per
            // pixel — the same reason DecodeIndexedBitmap and the image paths already do it this
            // way — and this grid is up to 512x512 = 262 144 of them.
            var grid = new SKColor[W * H];
            bool funcGaveUp = false; // an exhausted function would otherwise be re-run for every one of W×H pixels
            var input = new float[2];
            // Filled through the bitmap's own buffer. `funcGaveUp` still abandons the rest of the
            // grid — every later row returns immediately — and because each row arrives cleared,
            // the cells never reached stay transparent black exactly as the zero-initialised
            // SKColor[W*H] left them.
            Images.BitmapPixels.Fill(bitmap, (cells, row) =>
            {
                if (funcGaveUp) return;
                float y = yMin + (yMax - yMin) * row / (H - 1);
                for (int col = 0; col < W; col++)
                {
                    float x = xMin + (xMax - xMin) * col / (W - 1);
                    try
                    {
                        // The evaluator copies its inputs, so one reusable array serves the whole
                        // grid instead of a float[2] per pixel.
                        input[0] = x; input[1] = y;
                        float[] result = Core.Functions.PdfFunctionEvaluator.EvaluateEntry(fnObj, input, _doc.ResolveObject);
                        cells[col] = space.ToColor(result);
                    }
                    catch (Exception ex)
                    {
                        RenderDiagnostics.Report(RenderDiagnosticKind.MeshShaderFailed,
                            $"Mesh pixel ({col},{row}) failed", ex);
                        cells[col] = SKColors.Black;
                        if (ex is WorkBudgetExceededException || ex is InvalidOperationException)
                        { funcGaveUp = true; return; } // stop the W×H amplification on a budget/recursion bomb
                    }
                }
            });

            // The sample grid is in DOMAIN space; without a local matrix Skia would place it
            // at device (0,0) at one bitmap pixel per output pixel and clamp everywhere else,
            // so a type-1 shading painted a small patch plus a smear of the edge colour.
            // Build bitmap → canvas-local explicitly:
            //
            //   bitmap px --gridToDomain--> domain --/Matrix--> shading space
            //             --CTM--> PDF user space --pageFlip--> canvas-local (Y-down)
            //
            // /Matrix is the type-1 shading's own entry (PDF §8.7.4.5.2 Table 79) — distinct
            // from the shading *pattern* /Matrix handled in RenderTilingPattern.
            float gx = W > 1 ? (xMax - xMin) / (W - 1) : (xMax - xMin);
            float gy = H > 1 ? (yMax - yMin) / (H - 1) : (yMax - yMin);
            var gridToDomain = new SKMatrix
            {
                ScaleX = gx, SkewX  = 0f, TransX = xMin,
                SkewY  = 0f, ScaleY = gy, TransY = yMin,
                Persp2 = 1f
            };

            var shMatrix = SKMatrix.Identity;
            var shMatrixArr = PdfResolve.Array(_doc, shading, PdfName.Matrix);
            if (shMatrixArr != null && shMatrixArr.Count >= 6)
            {
                float[] sm = new float[6];
                for (int i = 0; i < 6; i++) sm[i] = GetArrayFloat(shMatrixArr, i);
                shMatrix = GraphicsState.ToSKMatrix(sm);
            }

            var ctmSKM   = GraphicsState.ToSKMatrix(_ctx.GS.CTM);

            // Skia's local matrix maps bitmap space → canvas-local, i.e. exactly this chain.
            var localMatrix = SKMatrix.Concat(_ctx.PageFlip,
                                SKMatrix.Concat(ctmSKM,
                                  SKMatrix.Concat(shMatrix, gridToDomain)));

            // Decal: §8.7.4.5.2 paints the shading in its /Domain and nowhere else, and a clamped
            // bitmap flooded the clip with the domain's edge colours.
            return SKShader.CreateBitmap(bitmap,
                SKShaderTileMode.Decal, SKShaderTileMode.Decal, localMatrix);
        }

        // Mesh/patch shadings (Types 4–7).
        /// <param name="shadingType">
        /// 4 (free-form) or 5 (lattice-form). **Type 5 has no per-vertex edge flag** — its topology
        /// comes from <c>/VerticesPerRow</c> (§8.7.4.5.6), so reading a flag field would consume the
        /// first bits of the x coordinate, desynchronise the whole stream and misparse every lattice
        /// mesh.
        /// </param>
        /// <param name="shading">
        /// The shading dictionary, which must be a <c>PdfStream</c> — types 4–7 carry their vertex
        /// data in the stream body.
        /// </param>
        /// <param name="background">
        /// Whether the region the mesh leaves uncovered takes the shading's <c>/Background</c> — a
        /// shading pattern's does (§8.7.4.3); <c>sh</c> leaves it unpainted.
        /// </param>
        private SKShader BuildMeshShader(PdfDictionary shading, int shadingType, bool background)
        {
            if (!TryReadMeshStreamHeader(shading, RenderDiagnosticKind.MeshShaderFailed,
                                         "Mesh shading stream decode failed", out MeshStreamHeader header))
                return null;

            byte[] data = header.Data;
            int bpc = header.BitsPerCoordinate, bpcomp = header.BitsPerComponent, bpflag = header.BitsPerFlag;
            float xMin = header.XMin, xMax = header.XMax, yMin = header.YMin, yMax = header.YMax;
            int nComp = header.ComponentCount;
            float[] compMin = header.ComponentMin, compMax = header.ComponentMax;
            var space = header.Space;

            // /Function — when present the vertices carry one parametric t, not n components.
            var meshFn = _doc?.ResolveObject(shading[PdfName.Function]);
            bool meshFnGaveUp = false;

            // Determine canvas pixel bounds. Cap each axis so a page with many
            // shading operators cannot allocate (and churn) unbounded full-page
            // RGBA bitmaps; the shader is sampled then tiled, so the cap is
            // visually ample. Mirrors the tiling-pattern MaxTilePixels guard.
            int pixW = Math.Max(1, Math.Min(PdfLimits.MaxShadingDimension, SaturatingCast.ToInt(_ctx.PageWidth  * _ctx.DpiScale)));
            int pixH = Math.Max(1, Math.Min(PdfLimits.MaxShadingDimension, SaturatingCast.ToInt(_ctx.PageHeight * _ctx.DpiScale)));

            using var bitmap = new SKBitmap(pixW, pixH, SKColorType.Rgba8888, SKAlphaType.Premul);
            // /Background (PDF §8.7.4.3): areas of the mesh's bounding region not covered by
            // any triangle take this colour under a shading pattern; default transparent, and
            // transparent under `sh`, which Table 78 says ignores the entry.
            bitmap.Erase((background ? ReadShadingBackground(shading, space) : null) ?? SKColors.Transparent);

            // Type 5 (lattice-form) carries NO per-vertex flag; its rows are /VerticesPerRow long.
            bool lattice = shadingType == 5;
            int verticesPerRow = 0;
            if (lattice)
            {
                verticesPerRow = PdfResolve.Number(_doc, shading, PdfName.VerticesPerRow)?.IntValue ?? 0;
                if (verticesPerRow < 2)
                {
                    RenderDiagnostics.Report(RenderDiagnosticKind.MeshShaderFailed,
                        $"Type 5 shading rejected: /VerticesPerRow is {verticesPerRow} (§8.7.4.5.6 requires ≥ 2).");
                    return null;
                }
                bpflag = 0;   // no flag field in the stream
            }

            // Parse vertices from bitstream.
            var vertices = new System.Collections.Generic.List<(float x, float y, SKColor c, int flag)>();
            int bitsPerVertex = bpflag + bpc * 2 + bpcomp * nComp;
            int bytesPerVertex = (bitsPerVertex + 7) / 8;
            // BitsPer* are attacker-controlled. With all of them ≤ 0, bytesPerVertex == 0 and the
            // loop below never advances `pos` — an infinite loop appending vertices forever (hang
            // + OOM). bpc/bpcomp ≤ 0 also make maxCoord/maxComp 0 → NaN geometry. Per PDF §8.7.4
            // these fields are positive and ≤ 32; reject the shading otherwise. A value > 32 makes
            // `1L << bpc` shift past the long width (the shift count is masked to 6 bits), so e.g.
            // bpc = 64 yields maxCoord = (1L << 0) - 1 = 0 → divide-by-zero → NaN vertex coords
            // feeding SKVertices. Mirror the patch decoder's n > 32 cap. Also cap the vertex count
            // so a large (but ≥1-byte/vertex) stream cannot amplify into a multi-GB vertex list.
            if (bpc <= 0 || bpc > MaxSampleBitWidth || bpcomp <= 0 || bpcomp > MaxSampleBitWidth
                || bpflag < 0 || bpflag > MaxSampleBitWidth || bytesPerVertex <= 0)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.MeshShaderFailed,
                    $"Mesh shading rejected: out-of-range bit widths (bpc={bpc}, bpcomp={bpcomp}, bpflag={bpflag})");
                return null;
            }
            int pos = 0;
            long maxCoord = (1L << bpc) - 1;
            long maxComp = (1L << bpcomp) - 1;
            long maxFlag = (1L << bpflag) - 1;
            // Defensive: even with the ≤ 32 caps above, bail if a derived divisor collapsed to 0
            // so the per-vertex division can never produce NaN/Inf coordinates.
            if (maxCoord == 0 || maxComp == 0)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.MeshShaderFailed,
                    $"Mesh shading rejected: degenerate coordinate range (maxCoord={maxCoord}, maxComp={maxComp})");
                return null;
            }

            while (pos + bytesPerVertex <= data.Length)
            {
                if (vertices.Count >= PdfLimits.MaxMeshVertices)
                {
                    // Said, as the patch meshes' stop at the same ceiling is: the rest of the stream
                    // is not drawn.
                    RenderDiagnostics.Report(RenderDiagnosticKind.MeshShaderFailed,
                        $"Mesh shading stopped at {PdfLimits.MaxMeshVertices} vertices (PdfLimits.MaxMeshVertices); " +
                        "the later vertices were not drawn");
                    break;
                }
                long bits = 0; int bitsRead = 0;
                int bytePos = pos;
                // Read bitsPerVertex bits.
                Func<int, long> readBits = (n) =>
                {
                    // Defensive: reject n outside (0, MaxSampleBitWidth] (matches
                    // BuildPatchShader.ReadBits). bpc/bpcomp/bpflag are already capped above,
                    // but guard here too so `1L << n` can never shift past the long width and
                    // yield a bogus mask.
                    if (n <= 0 || n > MaxSampleBitWidth) return 0;
                    while (bitsRead < n && bytePos < data.Length)
                    {
                        bits = (bits << 8) | data[bytePos++];
                        bitsRead += 8;
                    }
                    bitsRead -= n;
                    return (bits >> bitsRead) & ((1L << n) - 1);
                };

                int flag = (int)readBits(bpflag);
                float px = xMin + (xMax - xMin) * readBits(bpc) / (float)maxCoord;
                float py = yMin + (yMax - yMin) * readBits(bpc) / (float)maxCoord;
                float[] comps = new float[nComp];
                for (int i = 0; i < nComp; i++)
                    comps[i] = compMin[i] + (compMax[i] - compMin[i]) * readBits(bpcomp) / (float)maxComp;

                var (cx, cy) = _ctx.TransformPoint(px, py);
                vertices.Add((cx, cy, MeshVertexColor(comps, space, meshFn, ref meshFnGaveUp), flag));
                pos = bytePos;
            }

            using var canvas = new SKCanvas(bitmap);

            if (lattice)
            {
                // Type 5 (§8.7.4.5.6): the vertices form a row-major lattice and every cell of it
                // is two triangles. There are no flags and no strips/fans to reconstruct.
                int rows = vertices.Count / verticesPerRow;
                long latticeTriangles = 2L * Math.Max(0, rows - 1) * (verticesPerRow - 1);
                var latticeBatch = new GouraudBatch(latticeTriangles);
                for (int r = 0; r + 1 < rows; r++)
                    for (int c = 0; c + 1 < verticesPerRow; c++)
                    {
                        int i00 = r * verticesPerRow + c;
                        int i01 = i00 + 1;
                        int i10 = (r + 1) * verticesPerRow + c;
                        int i11 = i10 + 1;
                        latticeBatch.Add(canvas,
                            vertices[i00].x, vertices[i00].y, vertices[i00].c,
                            vertices[i01].x, vertices[i01].y, vertices[i01].c,
                            vertices[i10].x, vertices[i10].y, vertices[i10].c);
                        latticeBatch.Add(canvas,
                            vertices[i01].x, vertices[i01].y, vertices[i01].c,
                            vertices[i11].x, vertices[i11].y, vertices[i11].c,
                            vertices[i10].x, vertices[i10].y, vertices[i10].c);
                    }
                latticeBatch.Flush(canvas);

                if (rows < 2)
                    RenderDiagnostics.Report(RenderDiagnosticKind.MeshShaderFailed,
                        $"Type 5 shading has {vertices.Count} vertices for /VerticesPerRow " +
                        $"{verticesPerRow} — fewer than the two rows a lattice needs; nothing drawn.");

                return SKShader.CreateBitmap(bitmap,
                    SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            }

            // Rasterize triangles using the flag field (type 4: free-form Gouraud).
            // Per PDF spec §8.7.4.5.5 the flag belongs to the *first* vertex of each
            // logical triangle, not to every vertex — so we walk the list with an
            // explicit cursor and remember the previous triangle's a/b/c to honour
            // strip (flag 1: reuse prev b,c) and fan (flag 2: reuse prev a,c) topology.
            //
            // vertices.Count is an exact upper bound on the triangles that walk can emit: an
            // independent triangle consumes three vertices and a strip/fan continuation one, so no
            // iteration ever emits more than one triangle per vertex consumed. Over-reserving costs
            // one array; the batch caps itself either way.
            var freeFormBatch = new GouraudBatch(vertices.Count);
            int prevA = -1, prevB = -1, prevC = -1;
            int cur = 0;
            while (cur < vertices.Count)
            {
                int flag = vertices[cur].flag;
                int ia, ib, ic;

                if (flag == 0 || prevC < 0)
                {
                    // Independent triangle: consume 3 vertices.
                    if (cur + 2 >= vertices.Count) break;
                    ia = cur; ib = cur + 1; ic = cur + 2;
                    cur += 3;
                }
                else if (flag == 1)
                {
                    // Strip: edge (b,c) of previous becomes (a,b) of new; this vertex is c.
                    ia = prevB; ib = prevC; ic = cur;
                    cur += 1;
                }
                else if (flag == 2)
                {
                    // Fan: edge (a,c) of previous becomes (a,b) of new; this vertex is c.
                    ia = prevA; ib = prevC; ic = cur;
                    cur += 1;
                }
                else
                {
                    // Unknown flag — skip this vertex defensively.
                    cur += 1;
                    continue;
                }

                var (xa, ya, ca, _) = vertices[ia];
                var (xb, yb, cb, _) = vertices[ib];
                var (xc, yc, cc, _) = vertices[ic];
                freeFormBatch.Add(canvas, xa, ya, ca, xb, yb, cb, xc, yc, cc);

                prevA = ia; prevB = ib; prevC = ic;
            }
            freeFormBatch.Flush(canvas);

            return SKShader.CreateBitmap(bitmap,
                SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        }

        /// <summary>
        /// Accumulates Gouraud triangles and issues them as one <c>DrawVertices</c> call, the way
        /// <see cref="DrawSurfaceGrid"/> already does per patch.
        /// </summary>
        /// <remarks>
        /// <para>Not a draw call per triangle — a position array, a colour array, an
        /// <c>SKVertices</c> and an <c>SKPaint</c> for three points. A mesh is thousands of
        /// triangles.</para>
        /// <para><b>Draw order is preserved</b>, which is not incidental: Skia rasterises the
        /// triangles of one call in array order, so a type&#160;4 mesh whose fan overlaps its own
        /// strip keeps exactly the occlusion it had when each triangle was a separate call.</para>
        /// <para>Bounded by <see cref="PdfLimits.MaxMeshTrianglesPerBatch"/> — see that constant for
        /// why an unbounded batch is a DoS surface rather than a simplification.</para>
        /// </remarks>
        private sealed class GouraudBatch
        {
            private readonly SKPoint[] _positions;
            private readonly SKColor[] _colors;
            private int _count;
            private bool _reportedSplit;

            public GouraudBatch(long expectedTriangles)
            {
                long tri = Math.Max(1, Math.Min(expectedTriangles, PdfLimits.MaxMeshTrianglesPerBatch));
                _positions = new SKPoint[tri * 3];
                _colors = new SKColor[tri * 3];
                _count = 0;
                _reportedSplit = false;
            }

            public void Add(SKCanvas canvas,
                float x0, float y0, SKColor c0,
                float x1, float y1, SKColor c1,
                float x2, float y2, SKColor c2)
            {
                if (_count + 3 > _positions.Length)
                {
                    // The caller's estimate was low, or the mesh is larger than one batch. Draw what
                    // we have and carry on — never drop a triangle.
                    if (!_reportedSplit)
                    {
                        _reportedSplit = true;
                        RenderDiagnostics.Report(RenderDiagnosticKind.MeshBatchSplit,
                            $"Mesh shading exceeds {PdfLimits.MaxMeshTrianglesPerBatch} triangles per " +
                            "draw call; rasterised in multiple batches. Every triangle is drawn, in " +
                            "order, but triangles either side of a batch boundary are composited " +
                            "separately.");
                    }
                    Flush(canvas);
                }

                _positions[_count] = new SKPoint(x0, y0); _colors[_count++] = c0;
                _positions[_count] = new SKPoint(x1, y1); _colors[_count++] = c1;
                _positions[_count] = new SKPoint(x2, y2); _colors[_count++] = c2;
            }

            public void Flush(SKCanvas canvas)
            {
                if (_count == 0) return;
                // CreateCopy takes the whole array, so hand it the exact prefix that was filled —
                // trailing default SKPoints would rasterise as degenerate triangles at the origin.
                var positions = _positions;
                var colors = _colors;
                if (_count != _positions.Length)
                {
                    positions = new SKPoint[_count];
                    colors = new SKColor[_count];
                    Array.Copy(_positions, positions, _count);
                    Array.Copy(_colors, colors, _count);
                }
                _count = 0;

                // Per-vertex colour interpolation via SkiaSharp vertex shading (Gouraud).
                using var verts = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, null, colors);
                // Blend mode Dst, not SrcOver. In Skia's DrawVertices the blend mode combines the
                // per-vertex colours ("dst") with the PAINT's colour/shader ("src"). With a
                // shader-less paint, SrcOver therefore resolves to the paint colour — opaque black by
                // default — and every mesh shading painted as a flat black silhouette with the vertex
                // colours computed, uploaded and then discarded. Dst keeps the interpolated vertex
                // colours, which is the whole point of a Gouraud mesh.
                using var paint = new SKPaint { IsAntialias = true };
                canvas.DrawVertices(verts, SKBlendMode.Dst, paint);
            }
        }

        // ==================================================================
        //  Coons / Tensor patch shading (PDF spec §8.7.4.5.7-8)
        //
        //  Replaces the triangulated approximation used by BuildMeshShader
        //  for ShadingType 6 and 7 with a proper Bezier-patch surface
        //  evaluation on an adaptive (u, v) grid.  Each patch delivers:
        //    • 12 control points (Coons) or 16 control points (tensor)
        //    • 4 corner colours (bilinear-interpolated over (u, v))
        //  The surface is sampled on an N×N grid (N = PdfLimits.BezierFlattenSteps)
        //  and rendered as a pair of triangles per grid cell, giving proper
        //  bicubic geometry with smooth colour gradients instead of the banded
        //  Gouraud output.
        // ==================================================================

        private SKShader BuildPatchShader(PdfDictionary shading, int shadingType)
        {
            if (!TryReadMeshStreamHeader(shading, RenderDiagnosticKind.PatchShaderFailed,
                                         $"Patch shading (type {shadingType}) decode failed",
                                         out MeshStreamHeader header))
                return null;

            byte[] data = header.Data;
            int bpc = header.BitsPerCoordinate, bpcomp = header.BitsPerComponent, bpflag = header.BitsPerFlag;
            float xMin = header.XMin, xMax = header.XMax, yMin = header.YMin, yMax = header.YMax;
            int nComp = header.ComponentCount;
            float[] compMin = header.ComponentMin, compMax = header.ComponentMax;
            var space = header.Space;

            // /Function — as in BuildMeshShader, a patch's corner colours are then a single
            // parametric value each, not n components (§8.7.4.5.5).
            var patchFn = _doc?.ResolveObject(shading[PdfName.Function]);
            bool patchFnGaveUp = false;

            // Cap each axis (see BuildMeshShader) so many patch shadings can't
            // allocate unbounded full-page bitmaps.
            int pixW = Math.Max(1, Math.Min(PdfLimits.MaxShadingDimension, SaturatingCast.ToInt(_ctx.PageWidth  * _ctx.DpiScale)));
            int pixH = Math.Max(1, Math.Min(PdfLimits.MaxShadingDimension, SaturatingCast.ToInt(_ctx.PageHeight * _ctx.DpiScale)));
            var bitmap = new SKBitmap(pixW, pixH, SKColorType.Rgba8888, SKAlphaType.Premul);
            try
            {
                bitmap.Erase(SKColors.Transparent);
                using var canvas = new SKCanvas(bitmap);

                // Each patch: flag(bpflag) + either 12 or 16 control points
                // + 4 (or 2 when flag reuses neighbour) colours.
                int pointsPerPatch = shadingType == 6 ? 12 : 16;
                long maxCoord = (1L << bpc) - 1;
                long maxComp  = (1L << bpcomp) - 1;

                int pos = 0; int bitsRead = 0; long bitsBuf = 0;
                int ReadBits(int n)
                {
                    // Defensive: cap n at MaxSampleBitWidth.  Patch streams from
                    // legitimate encoders never exceed 32-bit fields; adversarial
                    // input with n >= 64 would hit undefined-behaviour on `1L << n`.
                    if (n <= 0 || n > MaxSampleBitWidth) return -1;
                    while (bitsRead < n && pos < data.Length)
                    {
                        bitsBuf = (bitsBuf << 8) | data[pos++];
                        bitsRead += 8;
                    }
                    if (bitsRead < n) return -1;
                    bitsRead -= n;
                    int v = (int)((bitsBuf >> bitsRead) & ((1L << n) - 1));
                    return v;
                }

                bool havePrev = false;
                var prevCps = new (float x, float y)[pointsPerPatch];
                var prevColors = new SKColor[4];

                // Each patch evaluates its surface on a grid, so a stream of many tiny patches is
                // charged by the grid points it would draw, at a mesh vertex's price, against the same
                // ceiling as the triangle meshes' vertices: it had none, and a few bytes a patch drew
                // hundreds of triangles each.
                int steps = PdfLimits.BezierFlattenSteps;
                long pointsPerSurface = (long)(steps + 1) * (steps + 1);
                long surfacePoints = 0;

                while (pos < data.Length || bitsRead >= bpflag)
                {
                    int flag = ReadBits(bpflag);
                    if (flag < 0) break;
                    // §8.7.4.5.7: only the flag's two low bits are used.
                    flag &= 3;
                    bool continues = havePrev && flag > 0;

                    // A continuing patch takes its first edge — four points and two corner colours —
                    // from the edge of the previous one its flag names (Table 84, Table 85 alike:
                    // the twelve boundary points are in the same order for both types) — never the
                    // previous patch's last four points, which are no edge at all.
                    int cpsToRead = continues ? pointsPerPatch - 4 : pointsPerPatch;
                    int colorsToRead = continues ? 2 : 4;

                    var cps = new (float x, float y)[pointsPerPatch];
                    int cpOff = pointsPerPatch - cpsToRead;
                    if (continues)
                    {
                        int edge = 3 * flag;    // f = 1: points 3–6, f = 2: 6–9, f = 3: 9, 10, 11, 0
                        for (int i = 0; i < 4; i++) cps[i] = prevCps[(edge + i) % 12];
                    }
                    for (int i = 0; i < cpsToRead; i++)
                    {
                        int cx = ReadBits(bpc); int cy = ReadBits(bpc);
                        if (cx < 0 || cy < 0) goto doneAll;
                        float x = xMin + (xMax - xMin) * cx / (float)maxCoord;
                        float y = yMin + (yMax - yMin) * cy / (float)maxCoord;
                        var (px, py) = _ctx.TransformPoint(x, y);
                        cps[cpOff + i] = (px, py);
                    }

                    var colors = new SKColor[4];
                    int colorOff = 4 - colorsToRead;
                    if (continues)
                    {
                        // The edge's two corners: f = 1 the previous c2 and c3, f = 2 c3 and c4,
                        // f = 3 c4 and c1 (one-based, as Table 84 writes them).
                        for (int i = 0; i < 2; i++) colors[i] = prevColors[(flag + i) % 4];
                    }
                    for (int i = 0; i < colorsToRead; i++)
                    {
                        var comps = new float[nComp];
                        for (int c = 0; c < nComp; c++)
                        {
                            int v = ReadBits(bpcomp);
                            if (v < 0) goto doneAll;
                            comps[c] = compMin[c] + (compMax[c] - compMin[c]) * v / (float)maxComp;
                        }
                        colors[colorOff + i] = MeshVertexColor(comps, space, patchFn, ref patchFnGaveUp);
                    }

                    if (surfacePoints + pointsPerSurface > PdfLimits.MaxMeshVertices)
                    {
                        RenderDiagnostics.Report(RenderDiagnosticKind.PatchShaderFailed,
                            $"Patch shading (type {shadingType}) stopped at {PdfLimits.MaxMeshVertices} surface points " +
                            "(PdfLimits.MaxMeshVertices); the later patches were not drawn");
                        break;
                    }
                    surfacePoints += pointsPerSurface;

                    // Render the patch: evaluate the surface on the N×N flattening grid
                    // and emit triangles cell-by-cell with bilinear-interpolated colour. The four
                    // colours belong to the corners at points 0, 3, 6 and 9 (§8.7.4.5.7), which the
                    // surface puts at (u, v) = (0, 0), (0, 1), (1, 1) and (1, 0); the grid's colour
                    // corners are (0, 0), (1, 0), (0, 1), (1, 1).
                    var corners = new[] { colors[0], colors[3], colors[1], colors[2] };
                    if (shadingType == 6)
                        RenderCoonsPatch(canvas, cps, corners);
                    else
                        RenderTensorPatch(canvas, cps, corners);

                    Array.Copy(cps, prevCps, pointsPerPatch);
                    Array.Copy(colors, prevColors, 4);
                    havePrev = true;
                }
                doneAll: ;
                return SKShader.CreateBitmap(bitmap,
                    SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            }
            catch (Exception ex)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.PatchShaderFailed,
                    "Patch shader (Coons/tensor) bitmap construction failed", ex);
                bitmap.Dispose();
                return null;
            }
        }

        // Evaluates a Coons patch on an NxN (u, v) grid and fills cells.
        private static void RenderCoonsPatch(SKCanvas canvas,
            (float x, float y)[] cps, SKColor[] cornerColors)
        {
            int N = PdfLimits.BezierFlattenSteps;
            // The four boundary curves, as §8.7.4.5.7 and Figure 45 lay the twelve points out: D1
            // (u = 0) is points 0–3, C2 (v = 1) points 3–6, D2 (u = 1) points 9 down to 6, C1 (v = 0)
            // points 0, 11, 10, 9. The corners: (0, 0) point 0, (0, 1) point 3, (1, 1) point 6,
            // (1, 0) point 9.
            var P00 = cps[0]; var P01 = cps[3]; var P11 = cps[6]; var P10 = cps[9];
            var grid = new (float x, float y)[N + 1, N + 1];

            for (int iv = 0; iv <= N; iv++)
            {
                float v = iv / (float)N;
                for (int iu = 0; iu <= N; iu++)
                {
                    float u = iu / (float)N;

                    var c1 = CubicBezier(cps[0], cps[11], cps[10], cps[9], u);
                    var c2 = CubicBezier(cps[3], cps[4], cps[5], cps[6], u);
                    var d1 = CubicBezier(cps[0], cps[1], cps[2], cps[3], v);
                    var d2 = CubicBezier(cps[9], cps[8], cps[7], cps[6], v);

                    // S = SC + SD − SB.
                    float sx = (1 - v) * c1.x + v * c2.x
                             + (1 - u) * d1.x + u * d2.x
                             - ((1 - v) * ((1 - u) * P00.x + u * P10.x) + v * ((1 - u) * P01.x + u * P11.x));
                    float sy = (1 - v) * c1.y + v * c2.y
                             + (1 - u) * d1.y + u * d2.y
                             - ((1 - v) * ((1 - u) * P00.y + u * P10.y) + v * ((1 - u) * P01.y + u * P11.y));
                    grid[iu, iv] = (sx, sy);
                }
            }

            DrawSurfaceGrid(canvas, grid, cornerColors, N);
        }

        // Where the data stream holds each p_ij (§8.7.4.5.8, the spiral of Figure 47: the twelve
        // boundary points in the Coons order, then p11, p12, p22, p21), indexed i × 4 + j — column
        // i, row j.
        private static readonly int[] TensorStreamIndex =
        {
            0, 1, 2, 3,
            11, 12, 13, 4,
            10, 15, 14, 5,
            9, 8, 7, 6,
        };

        // Evaluates a tensor-product Bezier patch on an NxN grid: S(u, v) = Σ p_ij B_i(u) B_j(v).
        // The points were read as a row-major matrix, which is not the order the stream holds them.
        private static void RenderTensorPatch(SKCanvas canvas,
            (float x, float y)[] cps, SKColor[] cornerColors)
        {
            int N = PdfLimits.BezierFlattenSteps;
            var grid = new (float x, float y)[N + 1, N + 1];
            for (int iv = 0; iv <= N; iv++)
            {
                float v = iv / (float)N;
                for (int iu = 0; iu <= N; iu++)
                {
                    float u = iu / (float)N;
                    float sx = 0, sy = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        float bi = Bernstein3(i, u);
                        for (int j = 0; j < 4; j++)
                        {
                            float bj = Bernstein3(j, v);
                            var p = cps[TensorStreamIndex[i * 4 + j]];
                            sx += bi * bj * p.x;
                            sy += bi * bj * p.y;
                        }
                    }
                    grid[iu, iv] = (sx, sy);
                }
            }
            DrawSurfaceGrid(canvas, grid, cornerColors, N);
        }

        private static float Bernstein3(int i, float t) => i switch
        {
            0 => (1 - t) * (1 - t) * (1 - t),
            1 => 3 * t * (1 - t) * (1 - t),
            2 => 3 * t * t * (1 - t),
            _ => t * t * t
        };

        private static (float x, float y) CubicBezier(
            (float x, float y) p0, (float x, float y) p1,
            (float x, float y) p2, (float x, float y) p3, float t)
        {
            float mt = 1 - t;
            float b0 = mt * mt * mt;
            float b1 = 3 * mt * mt * t;
            float b2 = 3 * mt * t * t;
            float b3 = t * t * t;
            return (b0 * p0.x + b1 * p1.x + b2 * p2.x + b3 * p3.x,
                    b0 * p0.y + b1 * p1.y + b2 * p2.y + b3 * p3.y);
        }

        // Draws the sampled surface as a triangle mesh; corner colours are
        // bilinearly interpolated over (u, v) across every cell.
        private static void DrawSurfaceGrid(SKCanvas canvas,
            (float x, float y)[,] grid, SKColor[] corner, int N)
        {
            // Bilinear colour per (u, v).
            SKColor ColorAt(float u, float v)
            {
                float mu = 1 - u, mv = 1 - v;
                float r = mu * mv * corner[0].Red + u * mv * corner[1].Red
                        + mu * v * corner[2].Red + u * v * corner[3].Red;
                float g = mu * mv * corner[0].Green + u * mv * corner[1].Green
                        + mu * v * corner[2].Green + u * v * corner[3].Green;
                float b = mu * mv * corner[0].Blue + u * mv * corner[1].Blue
                        + mu * v * corner[2].Blue + u * v * corner[3].Blue;
                // ClampChannel, not a bare cast: these are already in the 0..255 domain, and the
                // cast TRUNCATED where every other colour site in the assembly rounds — a
                // half-level bias on every Coons (type 6) and tensor (type 7) patch. It is the
                // divergence GAPS §2b records for /Decode, in the one place a source scan cannot
                // reach it: there is no 255 and no clamp idiom on the line to key on.
                return new SKColor(
                    PdfColor.ClampChannel(r), PdfColor.ClampChannel(g), PdfColor.ClampChannel(b));
            }

            int cells = N;
            var positions = new SKPoint[cells * cells * 6];
            var colors    = new SKColor[cells * cells * 6];
            int k = 0;
            for (int iv = 0; iv < cells; iv++)
            {
                float v0 = iv / (float)N, v1 = (iv + 1) / (float)N;
                for (int iu = 0; iu < cells; iu++)
                {
                    float u0 = iu / (float)N, u1 = (iu + 1) / (float)N;
                    var p00 = grid[iu,     iv];
                    var p10 = grid[iu + 1, iv];
                    var p01 = grid[iu,     iv + 1];
                    var p11 = grid[iu + 1, iv + 1];
                    var c00 = ColorAt(u0, v0);
                    var c10 = ColorAt(u1, v0);
                    var c01 = ColorAt(u0, v1);
                    var c11 = ColorAt(u1, v1);

                    // Two triangles per cell.
                    positions[k] = new SKPoint(p00.x, p00.y); colors[k++] = c00;
                    positions[k] = new SKPoint(p10.x, p10.y); colors[k++] = c10;
                    positions[k] = new SKPoint(p11.x, p11.y); colors[k++] = c11;

                    positions[k] = new SKPoint(p00.x, p00.y); colors[k++] = c00;
                    positions[k] = new SKPoint(p11.x, p11.y); colors[k++] = c11;
                    positions[k] = new SKPoint(p01.x, p01.y); colors[k++] = c01;
                }
            }
            using var verts = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, null, colors);
            // Blend mode Dst, not SrcOver. In Skia's DrawVertices the blend mode combines the
            // per-vertex colours ("dst") with the PAINT's colour/shader ("src"). With a
            // shader-less paint, SrcOver therefore resolves to the paint colour — opaque black by
            // default — and every mesh shading painted as a flat black silhouette with the vertex
            // colours computed, uploaded and then discarded. Dst keeps the interpolated vertex
            // colours, which is the whole point of a Gouraud mesh.
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawVertices(verts, SKBlendMode.Dst, paint);
        }

        /// <summary>
        /// The colour space a shading's components are expressed in, as a name
        /// <see cref="PdfColor.ComponentsToColor"/> accepts.
        /// </summary>
        /// <remarks>
        /// §8.7.4.3 forbids a Pattern space here and an <c>/Indexed</c> space is meaningless for
        /// the continuous colours a shading interpolates — but files carry both, so an Indexed
        /// shading is read in its <b>base</b> space, which is what its <c>/Function</c> outputs
        /// actually are — a base array too: an <c>[/Indexed [/CalRGB …] …]</c> shading reports its
        /// base family, not "Indexed", which no converter understands.
        /// </remarks>
        private string GetShadingColorSpace(PdfDictionary shading)
        {
            var rcs = PdfColor.ResolveSpace(shading?[PdfName.ColorSpace], _doc);
            if (rcs.Family == PdfColorSpaceNames.Indexed && rcs.Array != null && rcs.Array.Count > 1)
                return PdfColor.ResolveSpace(rcs.Array[1], _doc).DeviceFamily;
            return rcs.DeviceFamily;
        }

        /// <summary>
        /// The §8.7.4.5 header every mesh and patch shading stream carries: the decoded bytes, the
        /// three field widths, the <c>/Decode</c> ranges and the colour space.
        /// </summary>
        private readonly struct MeshStreamHeader
        {
            public MeshStreamHeader(byte[] data, int bpc, int bpcomp, int bpflag,
                                    float xMin, float xMax, float yMin, float yMax,
                                    int nComp, float[] compMin, float[] compMax, ShadingSpace space)
            {
                Data = data;
                BitsPerCoordinate = bpc; BitsPerComponent = bpcomp; BitsPerFlag = bpflag;
                XMin = xMin; XMax = xMax; YMin = yMin; YMax = yMax;
                ComponentCount = nComp; ComponentMin = compMin; ComponentMax = compMax;
                Space = space;
            }

            public byte[] Data { get; }
            public int BitsPerCoordinate { get; }
            public int BitsPerComponent { get; }
            public int BitsPerFlag { get; }
            public float XMin { get; }
            public float XMax { get; }
            public float YMin { get; }
            public float YMax { get; }
            public int ComponentCount { get; }
            public float[] ComponentMin { get; }
            public float[] ComponentMax { get; }
            public ShadingSpace Space { get; }
        }

        /// <summary>
        /// Reads the header shared by shading types 4–7, or reports <paramref name="kind"/> and
        /// returns false.
        /// </summary>
        /// <remarks>
        /// <para><b>Why one reader for two shading families.</b> <c>BuildMeshShader</c> (types 4–5)
        /// and <c>BuildPatchShader</c> (types 6–7) both read it, because §8.7.4.5 gives all four
        /// types the same stream header — Tables 80 and 84/85 list the same entries. Two copies of
        /// one spec table drift: how <c>/BitsPerFlag</c> defaults, or an indirect <c>/Decode</c>,
        /// would have to be got right twice.</para>
        ///
        /// <para><b>The diagnostic stays a parameter</b> rather than being unified. A caller that
        /// subscribes to <see cref="RenderDiagnostics"/> is told whether a <i>mesh</i> or a
        /// <i>patch</i> shading failed, and collapsing <see cref="RenderDiagnosticKind.MeshShaderFailed"/>
        /// and <see cref="RenderDiagnosticKind.PatchShaderFailed"/> into one code would throw away
        /// information the consolidation has no business touching.</para>
        ///
        /// <para><b>The <c>/Decode</c> fallback is the spec's, not a guess.</b> A stream with no
        /// usable <c>/Decode</c> array gets the identity ranges — 0…1 per component over three
        /// components — which is what the two copies did and is preserved exactly here, including
        /// the detail that a present-but-short array (fewer than four entries) is ignored whole
        /// rather than partially applied.</para>
        /// </remarks>
        private bool TryReadMeshStreamHeader(
            PdfDictionary shading, RenderDiagnosticKind kind, string failureDetail,
            out MeshStreamHeader header)
        {
            header = default;
            if (!(shading is PdfStream stream)) return false;

            byte[] data;
            try { data = stream.GetDecodedData(); }
            catch (Exception ex)
            {
                RenderDiagnostics.Report(kind, failureDetail, ex);
                return false;
            }
            if (data == null || data.Length == 0) return false;

            int bpc    = ResolvedNumber(shading[PdfName.BitsPerCoordinate]) is PdfNumber bpcN ? bpcN.IntValue : DefaultBitsPerCoordinate;
            int bpcomp = ResolvedNumber(shading[PdfName.BitsPerComponent]) is PdfNumber bpcompN ? bpcompN.IntValue : DefaultBitsPerComponent;
            int bpflag = ResolvedNumber(shading[PdfName.BitsPerFlag]) is PdfNumber bpfN ? bpfN.IntValue : DefaultBitsPerFlag;

            // Decode range: [xMin xMax yMin yMax c0Min c0Max …]
            float xMin = 0, xMax = 1, yMin = 0, yMax = 1;
            int nComp = 3;
            float[] compMin = null, compMax = null;
            if (ResolvedArray(shading[PdfName.Decode]) is PdfArray dec && dec.Count >= 4)
            {
                xMin = GetArrayFloat(dec, 0); xMax = GetArrayFloat(dec, 1);
                yMin = GetArrayFloat(dec, 2); yMax = GetArrayFloat(dec, 3);
                nComp = (dec.Count - 4) / 2;
                compMin = new float[nComp]; compMax = new float[nComp];
                for (int i = 0; i < nComp; i++)
                {
                    compMin[i] = GetArrayFloat(dec, 4 + i * 2);
                    compMax[i] = GetArrayFloat(dec, 5 + i * 2);
                }
            }
            if (compMin == null) { compMin = new float[nComp]; compMax = new float[nComp]; for (int i = 0; i < nComp; i++) compMax[i] = 1f; }

            header = new MeshStreamHeader(data, bpc, bpcomp, bpflag, xMin, xMax, yMin, yMax,
                                          nComp, compMin, compMax, GetShadingSpace(shading));
            return true;
        }

        /// <summary>
        /// A shading's colour space as something that can actually CONVERT — which a family name
        /// alone cannot, because a <c>/Separation</c> or <c>/DeviceN</c> shading's function outputs
        /// are <b>tints</b> that must go through the space's tint transform before they mean
        /// anything (ISO 32000-1 §8.7.4.3, §8.6.6.4).
        /// </summary>
        /// <remarks>
        /// Fed straight to the component converter, a tint would be wrong in different ways in the
        /// two renderers: a single tint on the red channel here, a grey level in the SVG writer — a
        /// shared gap no parity comparison shows.
        /// </remarks>
        private ShadingSpace GetShadingSpace(PdfDictionary shading)
        {
            var space = ShadingSpaceWithoutPlate(shading);
            // On a separation plate each sample becomes the plate's tint from the shading's own
            // components (§8.6.7: the overprint mode does not apply to shadings, overprint does).
            if (_ctx.Plate != null)
                space.OnPlate(_ctx.Plate, PdfColor.ResolveSpace(shading?[PdfName.ColorSpace], _doc), _ctx.GS.OverprintFill);
            return space;
        }

        private ShadingSpace ShadingSpaceWithoutPlate(PdfDictionary shading)
        {
            ShadingSpace space = BaseShadingSpace(shading);
            // A colour that arrives as one palette index — a mesh vertex with no /Function, a
            // conforming one-output function — goes through the palette (IndexedPalette.ForShading).
            space.UseIndexedPalette(
                IndexedPalette.ForShading(_doc, shading?[PdfName.ColorSpace], CurrentIntent, out int baseComponents),
                baseComponents);
            return space;
        }

        private ShadingSpace BaseShadingSpace(PdfDictionary shading)
        {
            var rcs = PdfColor.ResolveSpace(shading?[PdfName.ColorSpace], _doc);

            // An /Indexed shading space is legal though rare; convert through its base.
            if (rcs.Family == PdfColorSpaceNames.Indexed && rcs.Array != null && rcs.Array.Count > 1)
                rcs = PdfColor.ResolveSpace(rcs.Array[1], _doc);

            PdfArray spot = NamedSpotColors.Substitute(_doc, rcs.Array);   // a spot the named-colour profile names
            if ((rcs.Family == PdfColorSpaceNames.Separation || rcs.Family == PdfColorSpaceNames.DeviceN)
                && spot != null && spot.Count > 3
                && _doc?.ResolveObject(spot[3]) is PdfDictionary tintFn)
            {
                string alternate = PdfColor.ResolveSpace(spot[2], _doc).DeviceFamily;
                // The alternate object travels too: when it is [/ICCBased …] the tint transform's
                // output belongs to that profile, not to the device family it collapses onto.
                return new ShadingSpace(alternate, tintFn, _doc, spot[2], CurrentIntent);
            }

            // ...and for a direct [/ICCBased …] shading space, the components themselves do.
            return new ShadingSpace(rcs.DeviceFamily, null, _doc, rcs.Array, CurrentIntent);
        }

        /// <summary>
        /// A gradient endpoint (<c>/C0</c>, <c>/C1</c>) through the shading's colour space, with a
        /// fallback for an absent or unusable array.
        /// </summary>
        private SKColor ShadingEndpointColor(PdfArray arr, ShadingSpace space, SKColor fallback)
        {
            if (arr == null || arr.Count == 0) return fallback;
            var comps = new float[arr.Count];
            for (int i = 0; i < arr.Count; i++) comps[i] = GetArrayFloat(arr, i);
            return space.ToColor(comps);
        }

        /// <summary>
        /// A shading's colour space, reduced to the one operation the shading code needs:
        /// components in, sRGB out.
        /// </summary>
        private sealed class ShadingSpace
        {
            private readonly string _family;
            private readonly PdfDictionary _tintFn;
            private readonly PdfDocument _doc;
            /// <summary>
            /// The colour-space object whose profile (if any) applies to the components handed to
            /// <see cref="PdfColor.ComponentsToColor"/> — the space itself when there is no tint
            /// transform, the <c>alternateSpace</c> when there is.
            /// </summary>
            private readonly PdfObject _spaceObj;

            /// <summary>
            /// The rendering intent in force when the shading was invoked. Captured at
            /// construction because a shading samples its colour long after the graphics state is
            /// out of reach here, and §8.7.4.3 evaluates it under the state current at the paint.
            /// The SVG mirror, <see cref="SvgShadingSpace"/>, carries the same field.
            /// </summary>
            private readonly IccRenderingIntent _intent;

            private bool _tintGaveUp;
            private SKColor[] _palette;
            private int _paletteBaseComponents;

            private SeparationPlate _plate;
            private PdfColor.ResolvedColorSpace _source;
            private bool _overprint;

            /// <summary>
            /// Makes <see cref="ToColor"/> answer <paramref name="plate"/>'s grey for each sample:
            /// a <c>/Separation</c> or <c>/DeviceN</c> shading's named tints, a DeviceCMYK one's
            /// four components, a DeviceGray one on black, any other through its sRGB colour (or
            /// the separation's output profile) — <see cref="SeparationPlate.TryTint"/>'s rules for
            /// a solid paint, with the overprint mode at 0. A sample that leaves the plate alone is
            /// transparent.
            /// </summary>
            public void OnPlate(SeparationPlate plate, PdfColor.ResolvedColorSpace source, bool overprint)
            {
                _plate = plate; _source = source; _overprint = overprint;
            }

            private SKColor PlateColor(float[] comps, SKColor rgb)
            {
                IReadOnlyDictionary<string, float> sepTints = null;
                float[] cmyk = null;
                bool grey = false;
                if ((_source.Family == PdfColorSpaceNames.Separation || _source.Family == PdfColorSpaceNames.DeviceN) && _source.Array != null)
                    sepTints = SeparationPlate.ColorantTints(_source.Array, comps);
                else if (_source.Family == PdfColorSpaceNames.DeviceCMYK && comps != null && comps.Length >= 4)
                    cmyk = new[] { PdfColor.Clamp01(comps[0]), PdfColor.Clamp01(comps[1]), PdfColor.Clamp01(comps[2]), PdfColor.Clamp01(comps[3]) };
                else if (_source.Family == PdfColorSpaceNames.DeviceGray)
                    grey = true;
                // §8.6.7: "shall not … apply to the painting of images or shadings" — mode 0.
                return _plate.TryTint(sepTints, cmyk, rgb, _overprint, 0, out float tint, grey, _intent, _source.Array)
                    ? SeparationPlate.PlateColor(tint, 255)
                    : SKColors.Transparent;
            }

            public ShadingSpace(string family, PdfDictionary tintFn, PdfDocument doc,
                                PdfObject spaceObj = null,
                                IccRenderingIntent intent = IccRenderingIntents.Default)
            {
                _family = family; _tintFn = tintFn; _doc = doc; _spaceObj = spaceObj;
                _intent = intent;
            }

            /// <summary>Components → sRGB, through an ICC profile when the space carries one.</summary>
            private SKColor Convert(float[] comps) =>
                IccColorTransform.ResolveAlternateColor(_doc, _spaceObj, comps, _family, _intent);

            /// <summary>The device family the converted components land in.</summary>
            public string Family => _family;

            public SKColor ToColor(float[] comps)
            {
                SKColor rgb = ToSrgb(comps);
                return _plate == null ? rgb : PlateColor(comps, rgb);
            }

            /// <summary>The palette of an <c>/Indexed</c> space, for colours that arrive as an index.</summary>
            public void UseIndexedPalette(SKColor[] palette, int baseComponents)
            {
                _palette = palette; _paletteBaseComponents = baseComponents;
            }

            private SKColor ToSrgb(float[] comps)
            {
                if (IndexedPalette.ShadingColor(_palette, _paletteBaseComponents, comps, out SKColor indexed))
                    return indexed;
                if (_tintFn == null || _tintGaveUp || comps == null || comps.Length == 0)
                    return Convert(comps);

                try
                {
                    float[] alt = PdfFunctionEvaluator.Evaluate(_tintFn, comps, _doc.ResolveObject);
                    if (alt != null && alt.Length > 0)
                        return Convert(alt);
                }
                catch (Exception ex)
                {
                    // Give up ONCE. A shading samples its colour hundreds of times (a type-1 grid is
                    // up to 512x512), so an adversarial tint transform would otherwise be
                    // re-evaluated and re-exhausted per sample — the amplification guard
                    // SampleShadingStops already applies to the shading function itself.
                    _tintGaveUp = true;
                    RenderDiagnostics.Report(RenderDiagnosticKind.MeshShaderFailed,
                        "Shading tint transform failed; remaining samples use the raw components", ex);
                }
                return Convert(comps);
            }
        }

        private static string DeviceSpaceForComponentCount(int n)
            => PdfColor.DeviceSpaceForComponentCount(n);

        // Colour science lives in PdfColor (shared with PdfSvgRenderer). These
        // forwarders keep the many internal call sites in this file unchanged.
        internal static SKColor ComponentsToColor(float[] comps, string cs)
            => PdfColor.ComponentsToColor(comps, cs);

        internal static SKColor LabToSRgb(float L, float a, float b)
            => PdfColor.LabToSRgb(L, a, b);

        private static SKColor CalRgbToSRgb(float[] comps, PdfDictionary paramDict)
            => PdfColor.CalRgbToSRgb(comps, paramDict);

        private void ResolveGradientColors(PdfDictionary fn, PdfDictionary shading,
            out SKColor c0, out SKColor c1)
        {
            c0 = SKColors.Black; c1 = SKColors.White;

            if (ResolvedNumber(fn[PdfName.FunctionType]) is not PdfNumber fnType || fnType.IntValue != 2)
                return;

            var c0Arr = PdfResolve.Array(_doc, fn, PdfName.C0);
            var c1Arr = PdfResolve.Array(_doc, fn, PdfName.C1);

            // The same question GetShadingColorSpace answers, asked of it, so an [/ICCBased …]
            // shading collapses to the device space its /N names.
            var space = GetShadingSpace(shading);

            // Through ShadingSpace, not the family name: /C0 and /C1 are the function's outputs, so
            // for a Separation/DeviceN shading they are TINTS and need the transform too.
            c0 = ShadingEndpointColor(c0Arr, space, SKColors.Black);
            c1 = ShadingEndpointColor(c1Arr, space, SKColors.White);
        }

        /// <summary>
        /// <see cref="PdfColor.ComponentsToColor"/> for components that arrive as a
        /// <see cref="PdfArray"/>, with <paramref name="fallback"/> for a missing array.
        ///
        /// <para>Not a <b>second, independent converter</b> with its own dispatch rule (on operand
        /// count rather than on the declared space) and its own Pattern case, which would make
        /// <see cref="ResolveSeparationColor"/> and the image path answer differently for the same
        /// <c>/Separation</c> space. Both
        /// behaviours now live in the one converter; the only thing left here is the array→float[]
        /// adaptation and the null-array fallback.</para>
        /// </summary>
        private static SKColor ArrayToColor(PdfArray arr, string cs, SKColor fallback)
        {
            if (arr == null) return fallback;
            var comps = new float[arr.Count];
            for (int i = 0; i < arr.Count; i++)
                comps[i] = (float)((arr[i] as PdfNumber)?.DoubleValue ?? 0.0);
            return PdfColor.ComponentsToColor(comps, cs);
        }

        // ---- Color space selection ----
    }
}
