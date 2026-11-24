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
internal sealed partial class SvgContentWriter
    {
        private void OpDo(ContentStreamOperation op)
        {
            if (op.Operands.Length < 1) return;
            string name = (op.Operands[0] as PdfName)?.Value;
            if (name == null) return;

            var cat     = _resources?[PdfName.XObject];
            var catDict = _doc.ResolveObject(cat) as PdfDictionary;
            if (catDict == null) return;
            var entry   = catDict[new PdfName(name)];
            // The indirect-reference object number gives the XObject a stable identity so a
            // repeated image can be emitted once and reused (−1 = inline dict, not shareable).
            long objNum = entry is PdfReference r ? r.ReferenceObjectNumber : -1;
            var xobj    = _doc.ResolveObject(entry);

            if (xobj is PdfStream xStream)
            {
                // Optional Content: an XObject whose /OC names a hidden OCG (or an OCMD that
                // resolves to hidden) must be skipped entirely — §8.11.3.3. The raster path has
                // always done this; without it a watermark or draft stamp the viewer hides was
                // still written into the exported SVG. Same shared evaluator, and the entry is
                // passed RAW because an OCG is identified by its object number.
                if (ContentStreamInterpreter.IsOcEntryHidden(xStream[PdfName.OC], HiddenOcgs, _doc))
                    return;

                string sub = (PdfResolve.Name(_doc, xStream, PdfName.Subtype))?.Value;
                if (sub == PdfName.Image.Value)
                    RenderImageXObject(xStream, objNum);
                else if (sub == PdfName.Form.Value)
                    RenderFormXObject(xStream);
                else if (sub == PdfName.PS.Value)
                    RenderPostScriptXObject(xStream);
                // Unknown subtype — silently ignored.
            }
        }

        /// <summary>
        /// A <c>/Subtype /PS</c> XObject: offer it to a registered <c>IPostScriptPlugin</c>
        /// first, and only emit the placeholder when nobody handles it.
        /// </summary>
        private void RenderPostScriptXObject(PdfStream psStream)
        {
            if (Depth >= PdfLimits.MaxRenderRecursionDepth)
            {
                if (Shared?.FormBudget?.FirstDepthRefusal() ?? true)
                    RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                        $"SVG PostScript XObject nesting exceeds {PdfLimits.MaxRenderRecursionDepth} " +
                        "levels; possible recursion.", null);
                return;
            }

            if (TryRenderPostScriptViaPlugin(psStream)) return;

            // The reason comes from PluginPostScriptTranslator — see the raster counterpart.
            RenderPostScriptPlaceholder(psStream);
        }

        private bool TryRenderPostScriptViaPlugin(PdfStream psStream)
        {
            byte[] ps;
            try { ps = psStream.GetDecodedData(); }
            catch (Exception ex)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                    "SVG PostScript XObject stream decode failed", ex);
                return false;
            }

            // deviceScale 0 — SVG output is resolution-independent, so a plugin that can only
            // rasterize must decline rather than guess a DPI.
            if (!PluginPostScriptTranslator.TryTranslate(
                    PostScriptSource.XObject, ps, psStream, _resources, _doc,
                    _gs.CTM, _pageW, _pageH, deviceScale: 0f,
                    out byte[] content, out _, out PdfDictionary res))
                return false;

            RunContentStreamAsForm(content, res,
                                   matrixArr: null,
                                   bboxArr: PdfResolve.Array(_doc, psStream, PdfName.BBox),
                                   groupDict: null, isTransparencyGroup: false);
            return true;
        }

        /// <summary>
        /// The legacy inline <c>(...) PS</c> operator. A registered <c>IPostScriptPlugin</c>
        /// can execute it; otherwise it is a no-op.
        /// </summary>
        private void OpPs(ContentStreamOperation op)
        {
            if (Depth >= PdfLimits.MaxRenderRecursionDepth) return;

            byte[] ps = (op.Operands.Length >= 1 ? op.Operands[0] as PdfString : null)?.RawBytes;
            if (ps == null || ps.Length == 0) return;

            if (PluginPostScriptTranslator.TryTranslate(
                    PostScriptSource.InlineOperator, ps, xobject: null, _resources, _doc,
                    _gs.CTM, _pageW, _pageH, deviceScale: 0f,
                    out byte[] content, out _, out PdfDictionary res))
            {
                RunContentStreamAsForm(content, res,
                                       matrixArr: null, bboxArr: null,
                                       groupDict: null, isTransparencyGroup: false);
                return;
            }

            // See the raster counterpart: the reason comes from the shared translator, so there is
            // deliberately no per-writer report and no once-per-stream flag here either.
        }

        // Legacy PDF 1.1 /Subtype /PS XObjects carry raw PostScript for the RIP.
        // We emit a dashed-outline rectangle with diagonal cross so content isn't
        // lost silently.  `data-postscript-placeholder` is a consumer-friendly hook
        // for tools that want to flag these regions.
        private void RenderPostScriptPlaceholder(PdfStream psStream)
        {
            var bboxArr = PdfResolve.Array(_doc, psStream, PdfName.BBox);
            if (bboxArr == null || bboxArr.Count < 4) return;

            float x0 = GetArrFloat(bboxArr, 0);
            float y0 = GetArrFloat(bboxArr, 1);
            float x1 = GetArrFloat(bboxArr, 2);
            float y1 = GetArrFloat(bboxArr, 3);
            float xl = Math.Min(x0, x1), yb = Math.Min(y0, y1);
            float wx = Math.Abs(x1 - x0), hy = Math.Abs(y1 - y0);
            if (wx <= 0 || hy <= 0) return;

            float[] m = _gs.CTM;
            string matrix = $"matrix({F(m[0])},{F(m[1])},{F(m[2])},{F(m[3])},{F(m[4])},{F(m[5])})";
            _sb.Append($"<g transform=\"{matrix}\" data-postscript-placeholder=\"true\">\n");
            _sb.Append($"<rect x=\"{F(xl)}\" y=\"{F(yb)}\" width=\"{F(wx)}\" height=\"{F(hy)}\"" +
                       $" fill=\"rgb(224,224,224)\" fill-opacity=\"0.25\"" +
                       $" stroke=\"rgb(128,128,128)\" stroke-opacity=\"0.75\"" +
                       $" stroke-dasharray=\"4,2\"/>\n");
            _sb.Append($"<line x1=\"{F(xl)}\" y1=\"{F(yb)}\" x2=\"{F(xl + wx)}\" y2=\"{F(yb + hy)}\"" +
                       $" stroke=\"rgb(128,128,128)\" stroke-opacity=\"0.75\"" +
                       $" stroke-dasharray=\"4,2\"/>\n");
            _sb.Append($"<line x1=\"{F(xl)}\" y1=\"{F(yb + hy)}\" x2=\"{F(xl + wx)}\" y2=\"{F(yb)}\"" +
                       $" stroke=\"rgb(128,128,128)\" stroke-opacity=\"0.75\"" +
                       $" stroke-dasharray=\"4,2\"/>\n");
            _sb.Append("</g>\n");
        }

        private void RenderFormXObject(PdfStream formStream)
        {
            if (Depth >= PdfLimits.MaxRenderRecursionDepth)
            {
                // Once a page, as in the raster renderer.
                if (Shared?.FormBudget?.FirstDepthRefusal() ?? true)
                    RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                        $"SVG Form XObject nesting exceeds {PdfLimits.MaxRenderRecursionDepth} levels; possible recursion.", null);
                return;
            }

            // Same charge and limit as the raster path. This was the ONLY site that charged, while
            // raster also charges inside RenderToBitmap — so mask groups and pattern cells ran
            // unbudgeted here; they now charge in BuildSvgSoftMask and ResolvePatternFill.
            // Asserted by RenderBudgetChargeParityTests, which compares what each renderer CHARGES.
            byte[] data;
            try { data = formStream.GetDecodedData(); }
            catch (Exception ex)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                    "SVG Form XObject stream decode failed", ex);
                return;
            }

            // Charged after the decode so the length counts: this writer caches no parse, and a form
            // of two thousand Do operators drawing itself ran whole at every invocation for the
            // fuzzer's full window while the invocation count was still inside its ceiling.
            if (Shared?.FormBudget != null && !Shared.FormBudget.TryChargeFormInvocation(data.Length)) return;

            var formRes = formStream[PdfName.Resources] is PdfObject ro
                ? _doc.ResolveObject(ro) as PdfDictionary ?? _resources
                : _resources;

            // PDF spec §11.6.6: a Form XObject with /Group << /S /Transparency >> composites
            // as a single image with the surrounding page using the parent's alpha and blend
            // mode (in _gs.FillAlpha / _gs.BlendMode from gs/CA/ca/BM).
            //
            // /I decides whether that image starts from a transparent backdrop (isolated) or
            // from the page (non-isolated), and it defaults to FALSE, so `isolation:isolate` is not
            // emitted unconditionally. SVG's default for a group is already non-isolated, so
            // honouring /I is a matter of emitting the property only when it is true.
            var groupObj  = formStream[PdfName.Group];
            var groupDict = _doc.ResolveObject(groupObj) as PdfDictionary
                            ?? groupObj as PdfDictionary;
            // An ABSENT /S defaults to "Transparency", matching the raster path. Table 96 makes
            // /S required and defines exactly one value, so a /Group carrying /CS, /I or /K but no
            // /S can only have meant a transparency group -- and producers omit it. Requiring it
            // here (which this line used to) meant such a form was not treated as a group at all
            // in SVG while the raster path composited it as one: no /CS approximation reported, /I
            // and /K unread, a silent divergence of exactly the kind the parity suite exists for.
            // Every fixture wrote /S explicitly, so no test could see it.
            bool isTransparencyGroup =
                groupDict != null
                && (PdfResolve.Name(_doc, groupDict, PdfName.S)?.Value ?? "Transparency") == "Transparency";

            // Table 147 /CS: the space the group's contents are composited in. An SVG document
            // composites in sRGB, so anything that is not a three-component additive RGB family is
            // an approximation here — the raster path composites a grey or CMYK group in its space
            // (GroupBlenders), which SVG has no way to say. The predicate is PdfColor's, shared with
            // the raster path — the question is the same one at both `Do` sites and this
            // repository's recurring defect is one renderer answering it and the other not.
            //
            // Do-invoked groups only. A soft mask's /G group is built by BuildSvgSoftMask, never
            // here, and §11.6.5.2 wants a luminosity mask group in /DeviceGray — reporting that
            // would fire on essentially every soft mask in existence.
            if (isTransparencyGroup)
                ReportGroupSpaceApproximation(PdfResolve.Value(_doc, groupDict[PdfName.CS]));

            var matArr = PdfResolve.Array(_doc, formStream, PdfName.Matrix);

            // §8.10.2: a form XObject's /BBox clips its content, and it is REQUIRED, as in the raster
            // path. RunContentStreamAsForm maps the box through /Matrix ∘ CTM.
            RunContentStreamAsForm(data, formRes, matArr,
                                   PdfResolve.Array(_doc, formStream, PdfName.BBox),
                                   groupDict, isTransparencyGroup);
        }

        /// <summary>
        /// Runs an annotation's <c>/AP</c> <c>/N</c> stream (§12.5.5) under
        /// <paramref name="ctm"/> — the Algorithm 8.1 fit matrix the annotation pass computed.
        ///
        /// <para>The body goes through <see cref="RunContentStreamAsForm"/> because an
        /// appearance stream <i>is</i> a form XObject: the same <c>/Matrix</c> concatenation and
        /// the same <c>/BBox</c> clip apply, so routing it here is what stops the two from
        /// drifting. <c>/Group</c> is deliberately not honoured, matching the raster path, which
        /// executes an appearance stream directly rather than as a transparency group.</para>
        /// </summary>
        internal void RunAppearanceStream(byte[] data, PdfDictionary resources,
                                          PdfArray matrixArr, PdfArray bboxArr, float[] ctm)
        {
            _gs.CTM = ctm;
            RunContentStreamAsForm(data, resources, matrixArr, bboxArr,
                                   groupDict: null, isTransparencyGroup: false);
        }

        /// <summary>
        /// Reports a transparency group's <c>/CS</c> (<paramref name="csObj"/>) the SVG export
        /// cannot composite in: a grey or CMYK space, which the raster renderer honours and this
        /// writer composites in sRGB, or a space §11.3.4 does not admit. The page's own group and
        /// a <c>Do</c>-invoked group alike.
        /// </summary>
        /// <param name="csObj">The group's <c>/CS</c> entry, resolved; null for none.</param>
        internal void ReportGroupSpaceApproximation(PdfObject csObj)
        {
            if (!PdfColor.TryGetGroupBlendSpace(csObj, _doc, out GroupBlendSpace? space, out string detail))
                RenderDiagnostics.Report(RenderDiagnosticKind.TransparencyGroupApproximated, detail);
            else if (space.HasValue && space.Value != GroupBlendSpace.Rgb)
                RenderDiagnostics.Report(RenderDiagnosticKind.TransparencyGroupApproximated,
                    $"transparency group /CS names a {(space.Value == GroupBlendSpace.Gray ? "grey" : "CMYK")} " +
                    "blending space (§11.6.6): the SVG export composites in sRGB, so blend modes, " +
                    "alpha and knockout inside the group operate on RGB components; the raster " +
                    "renderer composites this group in its own space.");
        }

        /// <summary>
        /// Runs <paramref name="data"/> as a form XObject body in a child writer one recursion
        /// level deeper: optional transparency-group wrapper, optional <c>/BBox</c> clip,
        /// optional <c>/Matrix</c> concat.
        ///
        /// <para>Extracted from <see cref="RenderFormXObject"/> so the PostScript-plugin path
        /// emits through the same code.</para>
        ///
        /// <para><paramref name="bboxArr"/> is mapped through <c>/Matrix ∘ CTM</c>, so both the
        /// real form path and the PostScript path pass their box and both are clipped.</para>
        /// </summary>
        private void RunContentStreamAsForm(byte[] data, PdfDictionary resources,
                                            PdfArray matrixArr, PdfArray bboxArr,
                                            PdfDictionary groupDict, bool isTransparencyGroup)
        {
            if (MatrixGuard.HasNonFiniteNumber(matrixArr, _doc) || MatrixGuard.HasNonFiniteNumber(bboxArr, _doc))
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                    "form ignored: a /Matrix or /BBox number past the range of a float");
                return;
            }
            if (isTransparencyGroup)
            {
                bool isolated = PdfResolve.Bool(_doc, groupDict, PdfName.I)?.Value ?? false;
                bool knockout = PdfResolve.Bool(_doc, groupDict, PdfName.K)?.Value ?? false;
                bool hasOpacity = Math.Abs(_gs.FillAlpha - 1f) > OpaqueAlphaEpsilon;

                // CSS `opacity` below 1 creates a stacking context of its own, which isolates
                // the group whatever we say here. That combination is therefore inexpressible
                // in SVG — report it rather than let the export quietly disagree with the
                // raster render, which can seed the backdrop and gets this right.
                // `mask` does the same, so a masked non-isolated group has the same limit; it
                // is named separately because the cause a host would look for is different.
                if (!isolated && hasOpacity)
                    RenderDiagnostics.Report(RenderDiagnosticKind.TransparencyGroupApproximated,
                        "non-isolated group exported to SVG with opacity < 1; CSS opacity forms " +
                        "its own stacking context, so the group is isolated in the SVG output.");
                if (!isolated && _gs.SvgMaskId != null)
                    RenderDiagnostics.Report(RenderDiagnosticKind.TransparencyGroupApproximated,
                        "non-isolated group exported to SVG under a soft mask; `mask` forms its " +
                        "own stacking context, so the group is isolated in the SVG output.");
                // Unlike /I, this is not a case of one renderer being ahead of the other by
                // accident: SVG has no knockout compositing model at all, so there is nothing
                // to emit. The raster path emulates it whenever the group's initial backdrop is
                // transparent (PdfRenderContext.ElementBlendMode), so a caller that needs knockout
                // has somewhere to go. That condition is BROADER than "isolated": an isolated
                // group's backdrop is transparent by definition, but a non-isolated one's often is
                // too.
                if (knockout)
                    RenderDiagnostics.Report(RenderDiagnosticKind.TransparencyGroupApproximated,
                        "knockout group (/K true) exported to SVG as non-knockout; SVG has no " +
                        "knockout compositing model. The raster renderer emulates knockout " +
                        "wherever the group's initial backdrop is transparent.");

                var styleParts = new System.Collections.Generic.List<string>(2);
                if (isolated) styleParts.Add("isolation:isolate");
                if (_gs.BlendMode != null)
                    styleParts.Add($"mix-blend-mode:{_gs.BlendMode}");

                _sb.Append("<g");
                if (styleParts.Count > 0)
                {
                    _sb.Append(" style=\"");
                    _sb.Append(string.Join(";", styleParts));
                    _sb.Append('"');
                }
                if (hasOpacity)
                    _sb.Append($" opacity=\"{F(_gs.FillAlpha)}\"");
                // §11.6.5.2 / §11.4.5: the soft mask applies to the group's COMPOSITED result,
                // not to each element inside it. The raster path does exactly this — a layer is
                // opened, ClearSoftMask stops the mask reaching the elements, and
                // EndTransparencyGroup(groupSoftMask) applies it once at composite time
                // (ContentStreamInterpreter.RunFormXObject). Here the mask was inherited by the
                // child writer instead, so every element carried its own `mask=` and two
                // overlapping shapes under a half-transparent mask each lost half their alpha
                // — 0.25 where they overlap instead of the 0.5 one masked group gives.
                _sb.Append(SoftMaskAttribute());
                _sb.Append(">\n");
            }

            // The /BBox is expressed in FORM space, so it must be mapped through the form's
            // /Matrix as well as the current CTM (§8.10.2). Building that matrix up front is
            // what lets the clip be emitted for a real form XObject and not only for the
            // PostScript path, which never has a /Matrix.
            float[] formCtm = _gs.CTM;
            if (matrixArr != null && matrixArr.Count >= 6)
            {
                float[] fm =
                {
                    GetArrFloat(matrixArr,0), GetArrFloat(matrixArr,1),
                    GetArrFloat(matrixArr,2), GetArrFloat(matrixArr,3),
                    GetArrFloat(matrixArr,4), GetArrFloat(matrixArr,5)
                };
                formCtm = Compose(fm, formCtm);
            }

            // /BBox clip, emitted in page space so the <path d> needs no transform attribute.
            // One emitter, in EmitBBoxClip. The box is in FORM space, so it goes through
            // /Matrix ∘ CTM rather than the CTM alone.
            bool clipped = EmitBBoxClip(bboxArr, formCtm, "psclip", openGroup: true) != null;

            var child = new SvgContentWriter(_sb, _doc, resources, _pageW, _pageH);
            child.InheritFrom(this);
            child._gs         = _gs.Clone();
            if (isTransparencyGroup)
            {
                // Inside an isolated group, opacity/blendmode collapse onto the
                // wrapper — reset child state so child elements don't double-apply.
                child._gs.FillAlpha   = 1f;
                child._gs.StrokeAlpha = 1f;
                child._gs.BlendMode   = null;
                // Same reasoning for the mask, which now rides on the wrapper above. Mirrors
                // the raster path's ClearSoftMask inside the layer.
                child._gs.SvgMaskId   = null;
            }

            // Copied, not aliased: with no /Matrix, formCtm IS the parent's array, and the
            // child must not be able to reach back into it.
            child._gs.CTM = CopyArr(formCtm);   // already carries /Matrix ∘ CTM
            // §8.7.3.1: a pattern used in the form maps through the form's space, not the page's.
            child._gs.PatternBase = CopyArr(formCtm);
            child.Execute(data);

            if (clipped)             _sb.Append("</g>\n");
            if (isTransparencyGroup) _sb.Append("</g>\n");
        }

        // ---- Inline image (BI) ----
    }
}
