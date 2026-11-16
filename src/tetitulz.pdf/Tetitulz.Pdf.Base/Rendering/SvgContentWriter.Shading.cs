// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;
using HashSetInt = System.Collections.Generic.HashSet<int>;
using SKPath = SkiaSharp.SKPath;
using SKColor = SkiaSharp.SKColor;
using SKFont = SkiaSharp.SKFont;
using SKTypeface = SkiaSharp.SKTypeface;
using SKPoint = SkiaSharp.SKPoint;
using SKPathVerb = SkiaSharp.SKPathVerb;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Interprets a PDF content stream into SVG elements — the vector counterpart of
    /// <see cref="ContentStreamInterpreter"/>, which does the same job onto a Skia canvas.
    ///
    /// <para><b>A type of its own, split as the raster interpreter is</b> — .Path, .Text,
    /// .Shading, .Images and the rest — so a change to one renderer and not the other is a
    /// two-file diff. A silently wrong output is, as a rule, one renderer lagging the other.</para>
    ///
    /// <para>It stays <c>internal</c>. Base's three <c>InternalsVisibleTo</c> friends are all
    /// in-repo — the test project, FontDiag and the benchmarks — so no CONSUMER could see it
    /// before and none can now; promoting it changed its nesting, not its visibility.</para>
    /// </summary>
internal sealed partial class SvgContentWriter
    {
        private void OpSh(ContentStreamOperation op)
        {
            if (op.Operands.Length < 1) return;
            string name = (op.Operands[0] as PdfName)?.Value;
            if (name == null) return;

            // Reported at the operator, as on the raster side: a shading's colour varies per pixel,
            // so there is no single source colorant set the overprint rule could preserve.
            if (_gs.OverprintFill) ReportOverprintSkipped(OverprintReportReason.Shading);

            var cat     = _resources?[PdfName.Shading];
            var catDict = _doc.ResolveObject(cat) as PdfDictionary;
            if (catDict == null) return;
            var shadingObj = _doc.ResolveObject(catDict[new PdfName(name)]) as PdfDictionary;
            if (shadingObj == null) return;
            // §8.6.5.6, as the raster BuildShader reads it.
            shadingObj = DefaultColorSpaces.WithDefaults(shadingObj, _resources, _doc);

            int shType = PdfResolve.Number(_doc, shadingObj, PdfName.ShadingType)?.IntValue ?? 0;

            // §8.7.4.3: a shading's /BBox, expressed in the CURRENT user space, limits the
            // region `sh` paints. The raster path clips to it (ContentStreamInterpreter.OpSh
            // pushes a state and calls ClipToPageRect); this writer painted straight over it,
            // so any shading with a /BBox flooded past its declared bounds — for the gradient
            // case, across the whole 65536-unit rect below.
            bool bboxClip = BeginShadingBBoxClip(shadingObj);

            // try/finally, not a straight-line close: Execute catches per operator, so a throw
            // out of an emitter would otherwise leave the clip group open and the document
            // no longer well-formed XML — which is how the unbalanced-group defect this
            // writer already had once got in.
            try
            {
                if (shType == 2 || shType == 3)
                {
                    // Axial / radial: native SVG gradients keep the output scalable.
                    EmitSvgGradientShading(shadingObj, shType);
                }
                else if (shType == 1 || (shType >= 4 && shType <= 7))
                {
                    // Function (1) and mesh / Coons / tensor (4-7) shadings have no faithful
                    // SVG primitive. Rasterize them through the full (hardened) raster shading
                    // pipeline and embed the result as an <image>; fall back to the coarse
                    // approximation only when rasterization produces nothing.
                    if (!TryEmitRasterizedShading(shadingObj))
                    {
                        if (shType == 1) EmitSvgFunctionShading(shadingObj);
                        else             EmitSvgMeshShadingApprox(shadingObj, shType);
                    }
                }
            }
            finally
            {
                if (bboxClip) _sb.Append("</g>\n");
            }
        }

        /// <summary>
        /// Opens a <c>&lt;g clip-path&gt;</c> for a shading's <c>/BBox</c> (§8.7.4.3), mapped
        /// through the current CTM as the raster path maps it through
        /// <c>PdfRenderContext.TransformPoint</c>. Returns false — emitting nothing — when the
        /// shading declares no usable box, so the caller knows whether to close a group.
        /// </summary>
        /// <remarks>
        /// Emitted here rather than inside each shading emitter so all four shading kinds are
        /// covered by one clip; the rasterized path is additionally clipped by the raster
        /// interpreter's own <c>OpSh</c>, and intersecting the same box twice is a no-op.
        /// </remarks>
        private bool BeginShadingBBoxClip(PdfDictionary shadingObj)
        {
            // One emitter, in EmitBBoxClip — this was one of three byte-identical copies.
            // The shading's /BBox is already in the current space, so the matrix is the CTM,
            // which is exactly what TxPt used here.
            var bboxArr = PdfResolve.Array(_doc, shadingObj, PdfName.BBox);
            return EmitBBoxClip(bboxArr, _gs.CTM, "shclip", openGroup: true) != null;
        }

        /// <summary>
        /// Rasterizes a single shading by reusing the raster <see cref="ContentStreamInterpreter"/>
        /// (which evaluates function / Gouraud-mesh / Coons- and tensor-patch shadings exactly),
        /// then embeds the resulting bitmap as an SVG &lt;image&gt;.  Returns <c>false</c> when
        /// nothing was painted (so the caller can fall back to its vector approximation).
        ///
        /// <para>The shading is rendered with the writer's current CTM into a page-sized bitmap in
        /// device space (PDF user space flipped about the page height); a <c>matrix(1,0,0,-1,0,H)</c>
        /// on the &lt;image&gt; undoes that flip so it composites in the SVG's PDF-Y-up content
        /// space, after which the page-level group applies the SVG flip / crop / rotation.</para>
        /// </summary>
        private bool TryEmitRasterizedShading(PdfDictionary shadingObj)
        {
            string dataUri = RasterizeShadingToDataUri(shadingObj, _gs.CTM, out _, out _,
                                                       out bool renderedEmpty);
            // "Rendered, and it covers nothing" is HANDLED, not failed: returning true suppresses
            // the caller's coarse rectangle so the export matches the raster output (nothing).
            if (renderedEmpty) return true;
            if (dataUri == null) return false;

            string maskAttr  = SoftMaskAttribute();
            string styleAttr = _gs.BlendMode != null ? $" style=\"mix-blend-mode:{_gs.BlendMode}\"" : "";
            // §11.6.4.2 again: the rasterized shading is painted into a fresh render context
            // whose alpha is 1, so the graphics state's fill alpha has to be re-applied to the
            // element. `opacity` rather than `fill-opacity` — an <image> has no fill.
            string alphaAttr = Math.Abs(_gs.FillAlpha - 1f) > OpaqueAlphaEpsilon
                ? $" opacity=\"{F(_gs.FillAlpha)}\"" : "";
            // Page units, not bitmap pixels — the bitmap is capped at MaxShadingDimension, so on
            // a page larger than the cap the two differ and the shading would land short. The
            // bitmap's corner is the page box's (RasterizeShadingToDataUri), so the image is placed
            // at the box's origin: content sits at raw PDF coordinates under the root group's
            // translate(-pdfX,-pdfY), and an image at (0,0) lay outside a cropped page's view.
            float pageX = Shared?.PageX ?? 0f, pageY = Shared?.PageY ?? 0f;
            _sb.Append($"<image transform=\"matrix(1,0,0,-1,{F(pageX)},{F(_pageH + pageY)})\"");
            _sb.Append($" x=\"0\" y=\"0\" width=\"{F(_pageW)}\" height=\"{F(_pageH)}\"");
            _sb.Append($" preserveAspectRatio=\"none\"{alphaAttr}{styleAttr}{maskAttr}");
            _sb.Append($" xlink:href=\"{dataUri}\"/>\n");
            return true;
        }

        /// <summary>
        /// Paints one shading into a page-sized bitmap through the raster
        /// <see cref="ContentStreamInterpreter"/> — which evaluates function, Gouraud-mesh and
        /// Coons/tensor-patch shadings exactly — and returns it as a PNG data URI, or
        /// <c>null</c> when nothing was painted or rasterization failed.
        /// </summary>
        /// <param name="ctm">
        /// The matrix to paint under. <c>sh</c> passes the graphics-state CTM; a shading
        /// <em>pattern</em> passes its <c>/Matrix</c> composed with it.
        /// </param>
        /// <param name="shadingObj">The shading dictionary to paint.</param>
        /// <param name="pw">Width in pixels of the rasterized bitmap.</param>
        /// <param name="ph">Height in pixels of the rasterized bitmap.</param>
        /// <param name="renderedEmpty">
        /// True when rasterization succeeded but painted nothing. Distinguished from failure so a
        /// caller can tell "the shading covers no pixels here" from "it could not be evaluated".
        /// </param>
        private string RasterizeShadingToDataUri(PdfDictionary shadingObj, float[] ctm,
                                                 out int pw, out int ph, out bool renderedEmpty)
        {
            pw = ph = 0;
            renderedEmpty = false;
            if (Depth > PdfLimits.MaxRenderRecursionDepth) return null;
            if (!(_pageW > 0f) || !(_pageH > 0f)) return null;

            // Shared shading-bitmap cap — the raster interpreter reads the same constant.
            pw = Math.Max(1, Math.Min(PdfLimits.MaxShadingDimension, SaturatingCast.ToInt(Math.Ceiling(_pageW))));
            ph = Math.Max(1, Math.Min(PdfLimits.MaxShadingDimension, SaturatingCast.ToInt(Math.Ceiling(_pageH))));

            // The bitmap is an offscreen render, charged to the page's pixels as the raster
            // renderer charges its own. Each `sh` and each shading-pattern fill allocated, cleared,
            // painted and encoded a page-sized bitmap — up to 64 MB — with nothing counting them,
            // so a content stream repeating one `sh` was that many. A refusal is handled and
            // paints nothing, as the raster side's refused render does; the budget reports it
            // once. The vector approximation is not the fallback here: it would move the
            // amplification into the document's size.
            if (Shared?.FormBudget != null && !Shared.FormBudget.TryChargeOffscreenPixels((long)pw * ph))
            {
                renderedEmpty = true;
                return null;
            }

            SkiaSharp.SKBitmap bitmap = null;
            try
            {
                bitmap = new SkiaSharp.SKBitmap(pw, ph,
                    SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
                using (var canvas = new SkiaSharp.SKCanvas(bitmap))
                {
                    canvas.Clear(SkiaSharp.SKColors.Transparent);
                    using var ctx = new PdfRenderContext(canvas, _pageW, _pageH, 1f);
                    // The bitmap covers the page box, whose corner need not be (0,0): the page's own
                    // render shifts its content by the box origin, and so does this one, or a shading
                    // inside a cropped box rasterised outside the bitmap and was taken for empty.
                    ctx.GS.CTM = Compose(ctm, new float[] { 1, 0, 0, 1, -(Shared?.PageX ?? 0f), -(Shared?.PageY ?? 0f) });

                    // Synthetic resources holding just this shading under a fixed name.
                    var shadingCat = new PdfDictionary();
                    shadingCat.Set(new PdfName("S0"), shadingObj);
                    var res = new PdfDictionary();
                    res.Set(PdfName.Shading, shadingCat);

                    using var fc = new FontCache();
                    var interp = new ContentStreamInterpreter(ctx, _doc, res, fc) { HiddenOcgs = HiddenOcgs };
                    interp.Execute(System.Text.Encoding.ASCII.GetBytes("/S0 " + PdfOperators.ShadingFill));
                }

                // Rasterisation SUCCEEDED and the shading covers nothing. That is a real answer,
                // not a failure, and it must not be confused with "could not rasterise" - the
                // caller's fallback paints a solid rectangle, so conflating the two made the SVG
                // export invent grey where the raster renderer draws nothing. Signalled separately.
                if (!HasVisiblePixels(bitmap)) { renderedEmpty = true; return null; }

                using var img = SkiaSharp.SKImage.FromBitmap(bitmap);
                using var enc = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
                if (enc == null) return null;
                return SvgSyntax.DataUri("image/png", enc.ToArray());
            }
            catch (Exception ex)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.MeshShaderFailed,
                    "SVG shading rasterization failed; falling back to approximation", ex);
                return null;
            }
            finally
            {
                bitmap?.Dispose();
            }
        }

        // True when the bitmap has at least one non-transparent pixel (Rgba8888: alpha = byte 3).
        private static bool HasVisiblePixels(SkiaSharp.SKBitmap bmp)
        {
            var span = bmp.GetPixelSpan();
            for (int i = 3; i < span.Length; i += 4)
                if (span[i] != 0) return true;
            return false;
        }

        /// <summary>Approximates a Type-1 (function) shading as an N×N grid of coloured rects
        /// (N = <see cref="PdfLimits.BezierFlattenSteps"/>).
        /// SVG lacks direct support for arbitrary 2-D colour functions; this gives a visually
        /// close result for smooth functions.</summary>
        private void EmitSvgFunctionShading(PdfDictionary sh)
        {
            var domainArr = PdfResolve.Array(_doc, sh, PdfName.Domain);
            float x0 = GetArrFloatOr(domainArr, 0, 0), x1 = GetArrFloatOr(domainArr, 1, 1);
            float y0 = GetArrFloatOr(domainArr, 2, 0), y1 = GetArrFloatOr(domainArr, 3, 1);
            // Not `as PdfDictionary`: §8.7.4.5.2 permits an ARRAY of n 1-out functions here, and
            // narrowing to a dictionary made a Type 1 shading in that form export as nothing.
            var fn = _doc.ResolveObject(sh[PdfName.Function]);
            if (!PdfFunctionEvaluator.IsFunctionEntry(fn, _doc.ResolveObject)) return;

            int steps = PdfLimits.BezierFlattenSteps;
            _sb.Append("<g>");
            for (int iy = 0; iy < steps; iy++)
                for (int ix = 0; ix < steps; ix++)
                {
                    float fx = x0 + (x1 - x0) * (ix + 0.5f) / steps;
                    float fy = y0 + (y1 - y0) * (iy + 0.5f) / steps;
                    string col;
                    try
                    {
                        float[] r = PdfFunctionEvaluator.EvaluateEntry(fn, new[] { fx, fy }, _doc.ResolveObject);
                        col = r?.Length >= 3 ? RgbColor(r[0], r[1], r[2])
                            : r?.Length == 1 ? GrayColor(r[0]) : RgbColor(0, 0, 0);
                    }
                    catch { col = RgbColor(0, 0, 0); }
                    float wpx = (x1 - x0) / steps, hpx = (y1 - y0) / steps;
                    float rx = x0 + ix * wpx, ry = y0 + iy * hpx;
                    _sb.Append($"<rect x=\"{F(rx)}\" y=\"{F(ry)}\" width=\"{F(wpx)}\" height=\"{F(hpx)}\" fill=\"{col}\"/>");
                }
            _sb.Append("</g>\n");
        }

        /// <summary>Mesh / Coons-patch shadings (types 4–7) have no direct SVG equivalent.
        /// We emit a single averaged-colour rectangle at the shading domain as a
        /// visually-plausible fallback — this is better than rendering nothing.</summary>
        private void EmitSvgMeshShadingApprox(PdfDictionary sh, int shType)
        {
            var bgArr = PdfResolve.Array(_doc, sh, PdfName.Background);
            string col = "rgb(200,200,200)";
            if (bgArr != null && bgArr.Count >= 3)
                col = RgbColor(GetArrFloat(bgArr, 0), GetArrFloat(bgArr, 1), GetArrFloat(bgArr, 2));
            else if (bgArr != null && bgArr.Count == 1)
                col = GrayColor(GetArrFloat(bgArr, 0));

            var bbox = PdfResolve.Array(_doc, sh, PdfName.BBox);
            if (bbox != null && bbox.Count >= 4)
            {
                float x = GetArrFloat(bbox, 0), y = GetArrFloat(bbox, 1);
                float w = GetArrFloat(bbox, 2) - x, h = GetArrFloat(bbox, 3) - y;
                _sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" fill=\"{col}\" data-sh-type=\"{shType}\"/>\n");

                // Reported HERE, at the point of approximation, rather than at each way of
                // arriving: RasterizeShadingToDataUri has five return-null paths, and a flat rectangle
                // must not replace a mesh with nothing said. A caller comparing the two renderers needs to know the export is not
                // describing the shading, which is precisely what PatternApproximated is for.
                RenderDiagnostics.Report(RenderDiagnosticKind.PatternApproximated,
                    $"SVG export: shading type {shType} could not be rasterized; " +
                    "emitted a single flat-colour rectangle over its /BBox.");
            }
        }

        private float GetArrFloatOr(PdfArray arr, int idx, float fallback)
        {
            if (arr == null || idx >= arr.Count) return fallback;
            return GetArrFloat(arr, idx);
        }

        private void EmitSvgGradientShading(PdfDictionary sh, int shType)
        {
            // sh operator: paint the axial/radial gradient across the current clip region.
            string id = EmitGradientDef(sh, shType, _gs.CTM);
            if (id == null) return;

            // §11.6.4.2: `sh` paints under the current alpha and soft mask, exactly as a fill
            // does — the raster path builds its paint with
            // `Color = Black.WithAlpha(FillAlpha * 255)` and routes through
            // DrawWithOptionalSoftMask. Both were dropped here, so a shading set to `/ca 0.3`
            // exported fully opaque and a masked one exported unmasked.
            string maskAttr  = SoftMaskAttribute();
            string styleAttr = _gs.BlendMode != null ? $" style=\"mix-blend-mode:{_gs.BlendMode}\"" : "";
            _sb.Append($"<rect x=\"-32768\" y=\"-32768\" width=\"65536\" height=\"65536\"" +
                       $" fill=\"url(#{id})\" fill-opacity=\"{F(_gs.FillAlpha)}\"" +
                       $"{styleAttr}{maskAttr}/>\n");
        }

        /// <summary>
        /// Emits a &lt;linearGradient&gt;/&lt;radialGradient&gt; &lt;defs&gt; for an axial (2) or
        /// radial (3) shading, sampling the colour function into N stops (mirrors the bitmap
        /// <c>SampleShadingStops</c>, so stitching/sampled functions render their full colour
        /// range, not a two-endpoint ramp). Returns the gradient id (without "url(#…)") or
        /// <c>null</c>. <paramref name="gradCtm"/> is the userSpaceOnUse gradientTransform.
        /// </summary>
        private string EmitGradientDef(PdfDictionary sh, int shType, float[] gradCtm)
        {
            var coordsArr = PdfResolve.Array(_doc, sh, PdfName.Coords);
            if (coordsArr == null || coordsArr.Count < 4) return null;
            int gid = NextId();
            string transform = $"matrix({F(gradCtm[0])},{F(gradCtm[1])},{F(gradCtm[2])},{F(gradCtm[3])},{F(gradCtm[4])},{F(gradCtm[5])})";
            // /Extend (§8.7.4.5.3) is a pair of booleans saying whether the shading continues
            // beyond each end of its axis; it defaults to [false false], which paints NOTHING
            // outside the axis. SVG's `pad` repeats the first and last STOP beyond the ends, so an
            // end that does not extend gets a fully transparent stop pinned at its offset, beside
            // the real one: two stops at one offset are a hard edge, and pad then continues the
            // transparent one. That is the raster path's own emulation of a partial extend (a
            // transparent stop at the non-extended end, clamped), so all four combinations export
            // as drawn.
            var extend = PdfResolve.Array(_doc, sh, PdfName.Extend);
            bool e0 = extend != null && extend.Count >= 1 && PdfResolve.Value(_doc, extend[0]) is PdfBoolean b0 && b0.Value;
            bool e1 = extend != null && extend.Count >= 2 && PdfResolve.Value(_doc, extend[1]) is PdfBoolean b1 && b1.Value;
            const string clearStop = " stop-color=\"black\" stop-opacity=\"0\"/>";
            string stops = (e0 ? "" : "<stop offset=\"0\"" + clearStop)
                         + EmitGradientStops(sh)
                         + (e1 ? "" : "<stop offset=\"1\"" + clearStop);

            if (shType == 2)
            {
                float x1 = GetArrFloat(coordsArr, 0), y1 = GetArrFloat(coordsArr, 1);
                float x2 = GetArrFloat(coordsArr, 2), y2 = GetArrFloat(coordsArr, 3);
                _sb.Append($"<defs><linearGradient id=\"sg{gid}\" x1=\"{F(x1)}\" y1=\"{F(y1)}\"" +
                           $" x2=\"{F(x2)}\" y2=\"{F(y2)}\" gradientUnits=\"userSpaceOnUse\"" +
                           $" spreadMethod=\"pad\"" +
                           $" gradientTransform=\"{transform}\">{stops}</linearGradient></defs>\n");
            }
            else
            {
                float cx0 = GetArrFloat(coordsArr, 0), cy0 = GetArrFloat(coordsArr, 1),
                      r0v = GetArrFloat(coordsArr, 2),
                      cx1 = GetArrFloat(coordsArr, 3), cy1 = GetArrFloat(coordsArr, 4),
                      r1v = coordsArr.Count >= 6 ? GetArrFloat(coordsArr, 5) : 0f;
                _sb.Append($"<defs><radialGradient id=\"sg{gid}\" cx=\"{F(cx1)}\" cy=\"{F(cy1)}\"" +
                           $" r=\"{F(r1v)}\" fx=\"{F(cx0)}\" fy=\"{F(cy0)}\" fr=\"{F(r0v)}\"" +
                           $" gradientUnits=\"userSpaceOnUse\"" +
                           $" spreadMethod=\"pad\"" +
                           $" gradientTransform=\"{transform}\">{stops}</radialGradient></defs>\n");
            }
            return $"sg{gid}";
        }

        /// <summary>Samples the shading's colour function into N &lt;stop&gt; elements (handles
        /// function types 0/2/3/4, a /Function array, and the shading's colour space).</summary>
        private string EmitGradientStops(PdfDictionary sh)
        {
            var fnObj = _doc.ResolveObject(sh[PdfName.Function]);
            var space = ShadingSpaceOf(sh);
            float t0 = 0f, t1 = 1f;
            if (PdfResolve.Array(_doc, sh, PdfName.Domain) is PdfArray dom && dom.Count >= 2)
            { t0 = GetArrFloat(dom, 0); t1 = GetArrFloat(dom, 1); }
            int N = PdfLimits.SvgGradientStopCount;
            var sb = new StringBuilder();
            for (int i = 0; i < N; i++)
            {
                float u = i / (float)(N - 1);
                string col;
                try { col = ColorFromShadingFunction(fnObj, t0 + (t1 - t0) * u, space); }
                catch (Exception ex) when (ex is WorkBudgetExceededException || ex is InvalidOperationException)
                {
                    // Adversarial colour function (budget/recursion bomb): re-evaluating it
                    // for every remaining stop would be an N× amplification. Fill the rest black.
                    RenderDiagnostics.Report(RenderDiagnosticKind.MeshShaderFailed,
                        "SVG shading colour function aborted (budget/recursion); remaining stops black", ex);
                    for (; i < N; i++)
                        sb.Append($"<stop offset=\"{F(i / (float)(N - 1))}\" stop-color=\"black\"/>");
                    break;
                }
                sb.Append($"<stop offset=\"{F(u)}\" stop-color=\"{col}\"/>");
            }
            return sb.ToString();
        }

        private string ColorFromShadingFunction(PdfObject fnObj, float t, SvgShadingSpace space)
        {
            float[] comps = null;
            try
            {
                // Shared with the raster path: one implementation of §8.7.4.5.2's
                // "a function, or an array of n 1-out functions".
                comps = PdfFunctionEvaluator.EvaluateEntry(fnObj, new[] { t }, _doc.ResolveObject);
            }
            catch (Exception ex) when (!(ex is WorkBudgetExceededException || ex is InvalidOperationException))
            {
                // Budget/recursion-bomb exceptions propagate so the per-stop caller can bail
                // out of the N× amplification; other (benign) failures fall back to black here.
                RenderDiagnostics.Report(RenderDiagnosticKind.MeshShaderFailed,
                    "SVG shading stop-colour evaluation failed", ex);
            }
            if (comps == null || comps.Length == 0) return "black";
            return space.ToCss(comps);
        }

        /// <summary>
        /// A shading's colour space as something that can CONVERT, mirroring the raster path's
        /// <c>ShadingSpace</c>.
        /// </summary>
        /// <remarks>
        /// A local switch handling only DeviceGray/CalGray and DeviceCMYK would read <c>/Lab</c> as
        /// RGB (L=100 saturated red instead of white) and a <c>/Separation</c> tint as a grey level,
        /// and the raster path would get it wrong <i>differently</i> — a single tint on the red
        /// channel — which is why neither
        /// renderer's error showed up in a parity comparison. §8.7.4.3, §8.6.6.4.
        /// </remarks>

        private SvgShadingSpace ShadingSpaceOf(PdfDictionary sh)
        {
            // The raster ShadingSpace's rule: a colour arriving as one palette index goes through it.
            SKColor[] palette = IndexedPalette.ForShading(_doc, sh?[PdfName.ColorSpace], CurrentIntent,
                                                          out int paletteBaseComponents);
            var rcs = PdfColor.ResolveSpace(sh?[PdfName.ColorSpace], _doc);

            if (rcs.Family == PdfColorSpaceNames.Indexed && rcs.Array != null && rcs.Array.Count > 1)
                rcs = PdfColor.ResolveSpace(rcs.Array[1], _doc);

            PdfArray spot = NamedSpotColors.Substitute(_doc, rcs.Array);   // a spot the named-colour profile names
            if ((rcs.Family == PdfColorSpaceNames.Separation || rcs.Family == PdfColorSpaceNames.DeviceN)
                && spot != null && spot.Count > 3
                && _doc?.ResolveObject(spot[3]) is PdfDictionary tintFn)
            {
                var alt = PdfColor.ResolveSpace(spot[2], _doc);
                return new SvgShadingSpace(alt.Family, tintFn, _doc, spot[2],
                                           CurrentIntent, palette, paletteBaseComponents);
            }

            return new SvgShadingSpace(rcs.Family, null, _doc, rcs.Array,
                                       CurrentIntent, palette, paletteBaseComponents);
        }

        /// <summary>Resolves a fill pattern to an SVG paint url, dispatching on /PatternType:
        /// tiling (1, a stream) → &lt;pattern&gt;; shading (2, a dict) → gradient.</summary>
        private string ResolveAnyPatternFill(string patternName, float[] elementInverse = null)
        {
            var patDict = _doc.ResolveObject(_resources?[PdfName.Pattern]) as PdfDictionary;
            var patObj  = _doc.ResolveObject(patDict?[new PdfName(patternName)]);
            if (MatrixGuard.PatternGeometryNotFinite(patObj as PdfDictionary, _doc))
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.PatternFillUnresolved,
                    "pattern ignored: a /Matrix, /BBox or step past the range of a float");
                return null;
            }
            if (patObj is PdfDictionary pd && patObj is not PdfStream
                && (PdfResolve.Number(_doc, pd, PdfName.PatternType)?.IntValue ?? 0) == 2)
                return ResolveShadingPatternFill(pd, elementInverse);
            return ResolvePatternFill(patternName, elementInverse); // tiling (PatternType 1, stream)
        }

        private string ResolveShadingPatternFill(PdfDictionary pattern, float[] elementInverse)
        {
            var sh = _doc.ResolveObject(pattern[PdfName.Shading]) as PdfDictionary;
            if (sh == null) return null;
            sh = DefaultColorSpaces.WithDefaults(sh, _resources, _doc);   // §8.6.5.6
            int shType = PdfResolve.Number(_doc, sh, PdfName.ShadingType)?.IntValue ?? 0;
            // §8.7.3.1: the pattern /Matrix maps pattern space to the default space of the content
            // stream the pattern is used in (SvgGs.PatternBase), never to the CTM at the paint —
            // the paths carry that already, so under it an earlier cm moved the gradient twice.
            float[] pm = ReadMatrix(PdfResolve.Value(_doc, pattern?[PdfName.Matrix]));
            float[] shadingCtm = Compose(pm ?? Identity(), _gs.PatternBase ?? Identity());

            if (shType == 2 || shType == 3)
            {
                // For an element that carries its own transform (text), pre-compose the inverse so
                // the userSpaceOnUse gradient still resolves in page space after the element transform.
                float[] gradCtm = elementInverse != null
                    ? Compose(shadingCtm, elementInverse)
                    : shadingCtm;
                string id = EmitGradientDef(sh, shType, gradCtm);
                return id != null ? $"url(#{id})" : null;
            }

            // Type 1 (function) and 4-7 (mesh / Coons / tensor) have no SVG gradient primitive.
            // The `sh` operator rasterizes them through the raster interpreter; a shading PATTERN
            // returning null here would leave the caller painting whatever solid colour the
            // graphics state happened to hold — a confidently wrong fill, with no diagnostic. Rasterize into a <pattern> so the fill is the shading itself.
            if (shType == 1 || (shType >= 4 && shType <= 7))
                return BuildRasterizedShadingPatternFill(sh, shadingCtm, elementInverse);

            return null;
        }

        /// <summary>
        /// Wraps a rasterized shading in a page-sized <c>&lt;pattern&gt;</c> so it can serve as a
        /// <c>fill</c> value. Unlike the <c>sh</c> case the image is not emitted inline, because
        /// the paint has to be attached to the path being filled.
        /// </summary>
        /// <param name="elementInverse">
        /// Applied as <c>patternTransform</c> rather than folded into the raster CTM: the bitmap
        /// is painted in page space, so the element's own transform has to be undone at
        /// reference time, exactly as the gradient branch does with its pre-composed inverse.
        /// </param>
        /// <param name="shading">The shading dictionary to paint.</param>
        /// <param name="shadingCtm">
        /// The pattern's <c>/Matrix</c> composed with the graphics-state CTM — the matrix the raster
        /// is painted under.
        /// </param>
        private string BuildRasterizedShadingPatternFill(PdfDictionary shading, float[] shadingCtm,
                                                         float[] elementInverse)
        {
            // This caller has no coarse fallback - a null return simply means "no pattern fill" -
            // so the empty case needs no separate handling here beyond being discarded.
            string dataUri = RasterizeShadingToDataUri(shading, shadingCtm, out _, out _, out _);
            if (dataUri == null) return null;

            int id = NextId();   // shared across every writer on the page — see NextId
            string patTransform = elementInverse != null
                ? $" patternTransform=\"matrix({F(elementInverse[0])},{F(elementInverse[1])}," +
                  $"{F(elementInverse[2])},{F(elementInverse[3])},{F(elementInverse[4])},{F(elementInverse[5])})\""
                : "";

            // The tile and the image must describe the SAME rectangle. The bitmap is capped at
            // PdfLimits.MaxShadingDimension, so its pixel size is not the page size on a large
            // page; both are therefore expressed in PAGE units and the bitmap is stretched over
            // them (preserveAspectRatio="none"). Sizing either one in bitmap pixels separates
            // the two as soon as the cap bites.
            // The tile sits at the page box's corner, where the bitmap's is (RasterizeShadingToDataUri).
            _sb.Append($"<defs><pattern id=\"shpat{id}\" x=\"{F(Shared?.PageX ?? 0f)}\" y=\"{F(Shared?.PageY ?? 0f)}\"" +
                       $" width=\"{F(_pageW)}\" height=\"{F(_pageH)}\"" +
                       $" patternUnits=\"userSpaceOnUse\"{patTransform}>");
            _sb.Append($"<image transform=\"matrix(1,0,0,-1,0,{F(_pageH)})\"" +
                       $" x=\"0\" y=\"0\" width=\"{F(_pageW)}\" height=\"{F(_pageH)}\"" +
                       $" preserveAspectRatio=\"none\" xlink:href=\"{dataUri}\"/>");
            _sb.Append("</pattern></defs>\n");
            return $"url(#shpat{id})";
        }

        private float[] ReadMatrix(PdfObject o)
        {
            var a = (_doc.ResolveObject(o) ?? o) as PdfArray;
            if (a == null || a.Count < 6) return null;
            var m = new float[6];
            for (int i = 0; i < 6; i++) m[i] = GetArrFloat(a, i);
            return m;
        }


        // ---- Marked content ----
    }
}
