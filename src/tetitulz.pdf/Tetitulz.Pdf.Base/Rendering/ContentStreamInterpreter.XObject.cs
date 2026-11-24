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
        private void OpDo(ContentStreamOperation op)
        {
            if (op.Operands.Length < 1) return;
            string name = GetName(op, 0);

            var xobjDict = ResolveResourceDict(PdfName.XObject, name);
            if (xobjDict == null) return;

            var resolved = _doc.ResolveObject(xobjDict) as PdfDictionary ?? xobjDict;

            // Optional Content: if /OC references a hidden OCG, skip this XObject entirely.
            if (IsOcHidden(resolved)) return;

            string subtype = (PdfResolve.Name(_doc, resolved, PdfName.Subtype))?.Value;

            if (subtype == PdfName.Image.Value)
                RenderImageXObject(resolved);
            else if (subtype == PdfName.Form.Value)
                RenderFormXObject(resolved);
            else if (subtype == PdfName.PS.Value)
                RenderPostScriptXObject(resolved);
            // Unknown subtype — silently ignored, as viewers do.
        }

        /// <summary>
        /// A <c>/Subtype /PS</c> XObject: offer it to a registered <c>IPostScriptPlugin</c> first,
        /// and only draw the placeholder when nobody handles it.
        /// </summary>
        private void RenderPostScriptXObject(PdfDictionary psDict)
        {
            if (RenderDepth >= PdfLimits.MaxRenderRecursionDepth)
            {
                if (FormBudget?.FirstDepthRefusal() ?? true)
                    RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                        $"PostScript XObject nesting exceeds {PdfLimits.MaxRenderRecursionDepth} levels; " +
                        "possible recursive/cyclic XObject.", null);
                return;
            }

            if (TryRenderPostScriptViaPlugin(psDict)) return;

            // The reason is reported by PluginPostScriptTranslator, which is the only place that
            // can tell "no plugin is registered" from "every registered plugin declined" — the two
            // states one generic message would merge, sending an operator whose plugin had correctly
            // declined off to re-check registration. Both renderers share that helper, so
            // both get the distinction.
            RenderPostScriptXObjectPlaceholder(psDict);
        }

        /// <summary>
        /// Runs the PS XObject through a registered plugin and executes what it returns as a form
        /// XObject body. Returns false when no plugin handled it, so the caller draws the
        /// placeholder.
        /// </summary>
        private bool TryRenderPostScriptViaPlugin(PdfDictionary psDict)
        {
            var psStream = psDict as PdfStream ?? _doc.ResolveObject(psDict) as PdfStream;
            if (psStream == null) return false;

            byte[] ps;
            try { ps = psStream.GetDecodedData(); }
            catch (Exception ex)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                    "PostScript XObject stream decode failed", ex);
                return false;
            }

            if (!PluginPostScriptTranslator.TryTranslate(
                    PostScriptSource.XObject, ps, psStream, _resources, _doc,
                    _ctx.GS.CTM, _ctx.PageWidth, _ctx.PageHeight, _ctx.DpiScale,
                    out byte[] content, out _, out PdfDictionary res))
                return false;

            // No /Matrix: Table 91 defines none for a PS XObject, so the plugin's stream runs in
            // the CTM already in force at the Do. /BBox is OPTIONAL here — when present we clip
            // like a form so untrusted marks cannot escape the box the document declared.
            RunContentStreamAsForm(content, res,
                                   matrixArr: null,
                                   bboxArr: PdfResolve.Array(_doc, psStream, PdfName.BBox),
                                   isTransparencyGroup: false,
                                   groupIsolated: false, groupKnockout: false);
            return true;
        }

        /// <summary>
        /// The legacy inline <c>(...) PS</c> operator. A registered <c>IPostScriptPlugin</c> can
        /// execute it; otherwise it is a no-op.
        /// </summary>
        private void OpPs(ContentStreamOperation op)
        {
            if (RenderDepth >= PdfLimits.MaxRenderRecursionDepth) return;

            byte[] ps = (op.Operands.Length >= 1 ? op.Operands[0] as PdfString : null)?.RawBytes;
            if (ps == null || ps.Length == 0) return;

            if (PluginPostScriptTranslator.TryTranslate(
                    PostScriptSource.InlineOperator, ps, xobject: null, _resources, _doc,
                    _ctx.GS.CTM, _ctx.PageWidth, _ctx.PageHeight, _ctx.DpiScale,
                    out byte[] content, out _, out PdfDictionary res))
            {
                // No /Matrix and no /BBox — the operator paints in the CTM in force. Running it
                // through the form path anyway gives the q/Q isolation a passthrough must have:
                // a PS fragment must not leak graphics state into the rest of the page.
                RunContentStreamAsForm(content, res,
                                       matrixArr: null, bboxArr: null,
                                       isTransparencyGroup: false,
                                       groupIsolated: false, groupKnockout: false);
                return;
            }

            // Nothing to do and nothing to say here: PluginPostScriptTranslator has already
            // reported the reason, and it is the only place that can tell "no plugin is
            // registered" (once per thread, with the package hint) from "every registered plugin
            // declined" (per occurrence, naming them). The once-per-stream flag this method used
            // to keep said neither, so a stream of `PS` operators produced one diagnostic that
            // could not be acted on — do not reintroduce it.
        }

        // PDF 1.1 allowed /Subtype /PS XObjects to carry raw PostScript for the RIP to
        // execute.  The subtype was deprecated in PDF 1.2 and viewers stopped honouring
        // it some years later, but legacy PDFs still contain them.  Without a registered
        // IPostScriptPlugin we cannot run the PostScript code — but we can draw a hatched
        // placeholder over the XObject's bounding box so the user sees that content
        // was present and was not lost silently.
        private void RenderPostScriptXObjectPlaceholder(PdfDictionary psDict)
        {
            // Prefer /BBox (Form-XObject style) else fall back to a no-op.
            var bboxArr = (psDict as PdfStream)?.GetArray(PdfName.BBox)
                       ?? PdfResolve.Array(_doc, psDict, PdfName.BBox);
            if (bboxArr == null || bboxArr.Count < 4) return;

            float x0 = GetArrayFloat(bboxArr, 0);
            float y0 = GetArrayFloat(bboxArr, 1);
            float x1 = GetArrayFloat(bboxArr, 2);
            float y1 = GetArrayFloat(bboxArr, 3);
            float w  = Math.Abs(x1 - x0), h = Math.Abs(y1 - y0);
            if (w <= 0 || h <= 0) return;

            // Outline rect at 50% opacity grey so it doesn't dominate the page.
            _ctx.PushState();
            _ctx.GS.StrokeColor = new SKColor(0x80, 0x80, 0x80, 0xC0);
            _ctx.GS.FillColor   = new SKColor(0xE0, 0xE0, 0xE0, 0x40);
            _ctx.GS.LineWidth   = 1f;
            _ctx.GS.DashPattern = new float[] { 4f, 2f };

            _ctx.NewPath();
            _ctx.AppendRect(Math.Min(x0, x1), Math.Min(y0, y1), w, h);
            _ctx.FillPath(evenOdd: false, clearAfter: false);
            _ctx.StrokePath();

            // Diagonal cross so the placeholder is visually distinct from real content.
            _ctx.NewPath();
            _ctx.MoveTo(x0, y0); _ctx.LineTo(x1, y1);
            _ctx.MoveTo(x0, y1); _ctx.LineTo(x1, y0);
            _ctx.StrokePath();

            _ctx.PopState();
        }

        private void RenderFormXObject(PdfDictionary formDict)
        {
            if (RenderDepth >= PdfLimits.MaxRenderRecursionDepth)
            {
                // Once a page: every branch the invocation ceiling admits can end here, a thousand
                // times a form.
                if (FormBudget?.FirstDepthRefusal() ?? true)
                    RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                        $"Form XObject nesting exceeds {PdfLimits.MaxRenderRecursionDepth} levels; possible recursive/cyclic XObject.", null);
                return;
            }

            var formStream = formDict as PdfStream
                          ?? _doc.ResolveObject(formDict) as PdfStream;
            if (formStream == null) return;

            byte[] data;
            try { data = formStream.GetDecodedData(); }
            catch (Exception ex)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                    "Form XObject stream decode failed", ex);
                return;
            }

            // Charged here, before any state is pushed: the depth check above bounds one branch,
            // this bounds how many branches the page may have — and, by the decoded length, how much
            // content they run in all. Returning rather than throwing keeps the graphics-state stack
            // balanced and survives Execute's blanket catch.
            if (FormBudget != null && !FormBudget.TryChargeFormInvocation(data.Length)) return;

            // Form XObjects have their own resource dictionary; fall back to page resources.
            var formResources = formDict[PdfName.Resources] is PdfObject ro
                ? _doc.ResolveObject(ro) as PdfDictionary ?? _resources
                : _resources;

            // Optional /Matrix and /BBox.
            var matrixArr = PdfResolve.Array(_doc, formDict, PdfName.Matrix);
            var bboxArr   = PdfResolve.Array(_doc, formDict, PdfName.BBox);
            if (MatrixGuard.HasNonFiniteNumber(matrixArr, _doc) || MatrixGuard.HasNonFiniteNumber(bboxArr, _doc))
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                    "form ignored: a /Matrix or /BBox number past the range of a float");
                return;
            }

            // PDF §11.6.6 — a form XObject carrying /Group << /S /Transparency >> is composited
            // as a unit: its contents render off-screen and the RESULT is painted onto the
            // backdrop once, using the alpha constant, blend mode and soft mask in effect at the
            // Do. Everything inside then starts from ca = CA = 1, Normal, no mask.
            //
            // Plain (non-group) forms do not go through the layer: for those, ca/CA apply to each
            // object inside individually. And the layer alpha is the fill alpha alone — Do is a
            // non-stroking paint operation, so ca alone applies; Math.Min(FillAlpha, StrokeAlpha)
            // would fade the fills of `ca 1 CA 0.3` to 30% as well.
            var groupDict = PdfResolve.Dict(_doc, formDict, PdfName.Group);
            bool isTransparencyGroup = groupDict != null
                && (PdfResolve.Name(_doc, groupDict, PdfName.S)?.Value ?? "Transparency") == "Transparency";

            // §11.6.6 Table 147: /I (isolated) and /K (knockout) both default to false. Reading
            // them is what lets the context seed a non-isolated group's backdrop, rather than every
            // group compositing as isolated.
            bool groupIsolated = isTransparencyGroup
                && (PdfResolve.Bool(_doc, groupDict, PdfName.I)?.Value ?? false);
            bool groupKnockout = isTransparencyGroup
                && (PdfResolve.Bool(_doc, groupDict, PdfName.K)?.Value ?? false);

            // Table 147 /CS: the space the group's contents are converted into and composited in.
            // A grey or CMYK group is composited in its space (GroupBlenders, through the context's
            // blend-space stack); a space §11.3.4 does not admit is reported and the group inherits
            // its parent's, as it does when the entry is absent.
            //
            // Scoped to Do-invoked groups deliberately. A soft mask's /G group reaches the bitmap
            // through ApplySMask, never through here, and §11.6.5.2 says a LUMINOSITY mask group
            // should be /DeviceGray — reporting that would fire on essentially every soft mask in
            // existence and drown the one case this is for.
            GroupBlendSpace? groupSpace = null;
            if (isTransparencyGroup)
            {
                PdfObject groupCs = PdfResolve.Value(_doc, groupDict[PdfName.CS]);
                if (!PdfColor.TryGetGroupBlendSpace(groupCs, _doc, out groupSpace, out string groupSpaceDetail))
                    RenderDiagnostics.Report(RenderDiagnosticKind.TransparencyGroupApproximated,
                                             groupSpaceDetail);
                // An admitted ICCBased grey or CMYK space is composited in the device family, not
                // through its profile: honoured in arithmetic, approximated in colour, and said so.
                else if (PdfColor.GroupBlendSpaceApproximation(groupCs, _doc) is string approximated)
                    RenderDiagnostics.Report(RenderDiagnosticKind.TransparencyGroupApproximated, approximated);
            }

            RunContentStreamAsForm(data, formResources, matrixArr, bboxArr,
                                   isTransparencyGroup, groupIsolated, groupKnockout, groupSpace);
        }

        /// <summary>
        /// Runs <paramref name="data"/> as a form XObject body: optional transparency-group layer,
        /// optional <c>/Matrix</c> concat, optional <c>/BBox</c> clip, then a child interpreter one
        /// recursion level deeper.
        ///
        /// <para>Extracted from <see cref="RenderFormXObject"/> so the PostScript-plugin path
        /// (<see cref="RenderPostScriptXObject"/>, <see cref="OpPs"/>) executes the plugin's content
        /// stream through EXACTLY the same machinery — no duplicated drawing code, and no way for
        /// the two paths to drift on recursion depth, clipping or optional-content propagation.</para>
        /// </summary>
        private void RunContentStreamAsForm(byte[] data, PdfDictionary resources,
                                            PdfArray matrixArr, PdfArray bboxArr,
                                            bool isTransparencyGroup,
                                            bool groupIsolated, bool groupKnockout,
                                            GroupBlendSpace? groupSpace = null)
        {
            _ctx.PushState();

            // The layer is skipped when the group would composite identically without one — at
            // ca 1, Normal, no mask, an isolated group's result lands on the page exactly as its
            // contents would have. A KNOCKOUT group is the exception: /K changes how the group's
            // elements composite with *each other*, which has nothing to do with how the result
            // reaches the page, so it needs the layer whatever the state at the Do. Without this
            // the /K flag was read, passed down, and could never take effect. A group whose /CS
            // CHANGES the blending space is the other: its elements convert into and composite in
            // that space (§11.6.6), which only a layer of its own can hold.
            bool changesSpace = groupSpace.HasValue && groupSpace.Value != _ctx.CurrentBlendSpace;
            bool useOpacityLayer = isTransparencyGroup &&
                                   (_ctx.GS.FillAlpha < 1f - OpacityEpsilon
                                    || _ctx.GS.ActiveSoftMask != null
                                    || groupKnockout
                                    || changesSpace
                                    || (_ctx.GS.BlendMode != null && _ctx.GS.BlendMode != "Normal"));

            // Borrowed, not owned: the saved q frame pushed above still references this bitmap, so
            // the ClearSoftMask below cannot free it and EndTransparencyGroup can safely read it.
            var groupSoftMask = useOpacityLayer ? _ctx.GS.ActiveSoftMask : null;
            if (useOpacityLayer)
            {
                _ctx.BeginTransparencyGroup(_ctx.GS.FillAlpha, _ctx.GS.BlendMode,
                    groupIsolated, groupKnockout, groupSpace);
                _ctx.GS.FillAlpha      = 1f;
                _ctx.GS.StrokeAlpha    = 1f;
                _ctx.GS.BlendMode      = null;
                // The mask is applied to the group as a whole when the layer is composited, so it
                // must not also apply to each element inside it.
                _ctx.ClearSoftMask();
            }

            if (matrixArr != null && matrixArr.Count >= 6)
            {
                float[] m =
                {
                    GetArrayFloat(matrixArr, 0), GetArrayFloat(matrixArr, 1),
                    GetArrayFloat(matrixArr, 2), GetArrayFloat(matrixArr, 3),
                    GetArrayFloat(matrixArr, 4), GetArrayFloat(matrixArr, 5)
                };
                _ctx.ConcatCTM(m);
            }
            // §8.7.3.1: a pattern used in the form maps through the form's space, not the page's.
            _ctx.GS.PatternBase = (float[])_ctx.GS.CTM.Clone();

            // Clip to /BBox (required by PDF spec for Form XObjects).
            if (bboxArr != null && bboxArr.Count >= 4)
            {
                float bx0 = GetArrayFloat(bboxArr, 0), by0 = GetArrayFloat(bboxArr, 1);
                float bx1 = GetArrayFloat(bboxArr, 2), by1 = GetArrayFloat(bboxArr, 3);
                _ctx.AppendRect(Math.Min(bx0, bx1), Math.Min(by0, by1),
                                Math.Abs(bx1 - bx0), Math.Abs(by1 - by0));
                _ctx.ClipPath();
                _ctx.NewPath(); // Clear BBox rect; form content stream starts with a clean path.
            }

            var inner = new ContentStreamInterpreter(_ctx, _doc, resources, _fontCache) { HiddenOcgs = HiddenOcgs, RenderDepth = RenderDepth + 1, CancellationToken = CancellationToken, FormBudget = FormBudget };
            inner.Execute(data);

            if (useOpacityLayer)
                _ctx.EndTransparencyGroup(groupSoftMask);

            _ctx.PopState();
        }

        // ================================================================
        //  Resource resolution helpers
        // ================================================================

        // scaleX / scaleY: device pixels per point for the offscreen render, 0 for the page's own
        // scale. A tiling cell passes the tile's: the tile is one step in whole pixels, so its scale
        // differs from the page's by the rounding — up to a whole pixel per step, half the period at
        // a two-pixel step — and a cell drawn at the page's scale into a bitmap sized by the tile's
        // left its top rows empty and its content short of the tile.
        private SKBitmap RenderToBitmap(PdfDictionary xobjDict, int pixW, int pixH,
            SKColor? initialFillColor = null, SKColor? initialStrokeColor = null,
            float innerWidth = 0f, float innerHeight = 0f,
            float bboxOriginX = 0f, float bboxOriginY = 0f,
            float[] initialCtm = null,
            bool honourFormMatrixAndBBox = false,
            bool inheritPlate = false,
            float scaleX = 0f, float scaleY = 0f)
        {
            if (pixW <= 0 || pixH <= 0) return null;
            if (RenderDepth >= PdfLimits.MaxRenderRecursionDepth)
            {
                if (FormBudget?.FirstDepthRefusal() ?? true)
                    RenderDiagnostics.Report(RenderDiagnosticKind.TilingPatternDecodeFailed,
                        $"Offscreen XObject/pattern nesting exceeds {PdfLimits.MaxRenderRecursionDepth} levels; possible recursion.", null);
                return null;
            }

            var stream = xobjDict as PdfStream ?? _doc.ResolveObject(xobjDict) as PdfStream;
            if (stream == null) return null;
            if (_ctx.DecodeFailedBefore(stream)) return null;   // failed earlier in this render, reported then

            byte[] data;
            try { data = stream.GetDecodedData(); }
            catch (Exception ex)
            {
                _ctx.NoteDecodeFailed(stream);
                RenderDiagnostics.Report(RenderDiagnosticKind.TilingPatternDecodeFailed,
                    "Tiling pattern / offscreen XObject decode failed", ex);
                return null;
            }

            // An offscreen render is a form invocation that also allocates a bitmap, so it is the
            // more expensive way to spend the budget, not a cheaper one; charged its decoded length —
            // and its area, which is the price it has before any of that content runs: a cell that
            // fills with itself twice doubles inside the invocation count with a bitmap at every step.
            if (FormBudget != null && !FormBudget.TryChargeFormInvocation(data.Length)) return null;
            if (FormBudget != null && !FormBudget.TryChargeOffscreenPixels((long)pixW * pixH)) return null;

            var resources = xobjDict[PdfName.Resources] is PdfObject ro
                ? _doc.ResolveObject(ro) as PdfDictionary ?? _resources
                : _resources;

            var bitmap = new SKBitmap(pixW, pixH, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);

            float scale = scaleX > 0f ? scaleX : _ctx.DpiScale;
            // The context has one scale; a tile whose y scale differs from its x scale (the two
            // steps round independently) takes the ratio as a CTM scale on y, with the inner height
            // stretched to match, so y = 0 still lands on the bitmap's bottom row and y = innerHeight
            // on its top: device y = pixH − y·scaleY.
            float yRatio = scaleX > 0f && scaleY > 0f ? scaleY / scaleX : 1f;
            // For tiling patterns, use the BBox dimensions as the inner page size so that
            // PDF y=by0 maps to the bitmap bottom and PDF y=by1 maps to the bitmap top.
            // For full-page groups (no inner dims supplied), fall back to the page size.
            float iW = innerWidth  > 0f ? innerWidth  : _ctx.PageWidth;
            float iH = (innerHeight > 0f ? innerHeight : _ctx.PageHeight) * yRatio;
            using var renderCtx = new PdfRenderContext(canvas, iW, iH, scale);
            // The page's parsed operations, not an empty cache: a tiling cell or a mask group is
            // rendered here once per paint that uses it, and a fresh context re-tokenised the cell's
            // content every time: a 3 KB file of pattern-filled text nested to the depth cap would
            // spend about half its 2.9 s render in ContentStreamParser.Parse.
            renderCtx.Operations = _ctx.Operations ??= new ContentOperationCache();
            renderCtx.FailedDecodes = _ctx.FailedDecodes;   // one memo of failed decodes for the whole render
            // The y ratio is concatenated first so that it applies after the origin shift below
            // (cm semantics: the matrix concatenated later is applied to a point earlier).
            if (yRatio != 1f)
                renderCtx.ConcatCTM(new float[] { 1f, 0f, 0f, yRatio, 0f, 0f });
            // If the BBox origin is non-zero, shift the coordinate system so the BBox
            // bottom-left maps to inner-context origin.
            if (bboxOriginX != 0f || bboxOriginY != 0f)
                renderCtx.ConcatCTM(new float[] { 1f, 0f, 0f, 1f, -bboxOriginX, -bboxOriginY });

            // PDF §11.6.5.2: a soft-mask transparency group is painted with the CTM
            // that is in effect when the mask is established (the `gs` operator).
            // Apply that outer CTM so the rendered mask lands in the same device
            // pixels as the content it will later modulate.  Without this the mask
            // is rendered with an identity CTM and a page that uses a non-trivial
            // base CTM (e.g. a `0.24 0 0 -0.24 0 H cm` print scale) mis-scales and
            // mis-positions the mask — making, for instance, a drop-shadow render
            // too dark and offset from the box it belongs to.
            if (initialCtm != null)
                renderCtx.ConcatCTM(initialCtm);

            // §11.6.5.2 makes a soft mask's /G an ordinary form XObject, so §8.10.2 applies to it:
            // its /Matrix maps form space into the space established above, and its /BBox clips the
            // result. Both were ignored, so a mask group with either — common, since a producer
            // usually authors the mask in its own coordinate system — was drawn at the wrong place
            // or scale, and painted outside its declared box.
            //
            // Opt-in, because the OTHER caller must not do this: a tiling pattern's /Matrix is
            // applied by the shader's local matrix (see RenderTilingPattern's "Do NOT include
            // gsCTM" construction), and honouring it here as well would apply it twice.
            if (honourFormMatrixAndBBox)
            {
                var formMatrix = PdfResolve.Array(_doc, xobjDict, PdfName.Matrix);
                if (formMatrix != null && formMatrix.Count >= 6)
                {
                    var m = new float[6];
                    for (int i = 0; i < 6; i++) m[i] = GetArrayFloat(formMatrix, i);
                    renderCtx.ConcatCTM(m);
                }

                var formBBox = PdfResolve.Array(_doc, xobjDict, PdfName.BBox);
                if (formBBox != null && formBBox.Count >= 4)
                {
                    float bx0 = GetArrayFloat(formBBox, 0), by0 = GetArrayFloat(formBBox, 1);
                    float bx1 = GetArrayFloat(formBBox, 2), by1 = GetArrayFloat(formBBox, 3);
                    // §7.9.5 allows either corner order.
                    renderCtx.NewPath();
                    renderCtx.AppendRect(Math.Min(bx0, bx1), Math.Min(by0, by1),
                                         Math.Abs(bx1 - bx0), Math.Abs(by1 - by0));
                    renderCtx.ClipPath(false);
                    renderCtx.NewPath();
                }
            }
            // §8.7.3.1: a pattern used inside the cell or the mask group maps through its space.
            renderCtx.GS.PatternBase = (float[])renderCtx.GS.CTM.Clone();

            // Uncoloured patterns (PaintType 2): inherit not just fill/stroke
            // colour but the full graphics state — line width, dash, blend mode,
            // overprint, miter limit — per PDF §8.7.3.1.  Spec requires "all
            // graphics state parameters except colour" to pass through.
            if (initialFillColor.HasValue || initialStrokeColor.HasValue)
            {
                var src = _ctx.GS;
                var dst = renderCtx.GS;
                dst.LineWidth        = src.LineWidth;
                dst.LineCap          = src.LineCap;
                dst.LineJoin         = src.LineJoin;
                dst.MiterLimit       = src.MiterLimit;
                dst.DashPattern      = src.DashPattern;
                dst.DashPhase        = src.DashPhase;
                dst.RenderingIntent  = src.RenderingIntent;
                dst.OverprintFill    = src.OverprintFill;
                dst.OverprintStroke  = src.OverprintStroke;
                dst.OverprintMode    = src.OverprintMode;
                dst.AlphaIsShape     = src.AlphaIsShape;
                dst.TextKnockout     = src.TextKnockout;
                dst.BlackGeneration  = src.BlackGeneration;
            }
            if (initialFillColor.HasValue)   renderCtx.GS.FillColor   = initialFillColor.Value;
            if (initialStrokeColor.HasValue) renderCtx.GS.StrokeColor = initialStrokeColor.Value;

            // A tiling cell is drawn on the plate it tiles onto; a soft mask's group is not — its
            // luminosity or alpha is a colour, whichever plate the masked paint lands on. An
            // uncoloured cell paints in the current colour, so on a plate it needs that colour's
            // components too, not only its sRGB.
            if (inheritPlate && _ctx.Plate != null)
            {
                renderCtx.Plate = _ctx.Plate;
                if (initialFillColor.HasValue)
                {
                    renderCtx.GS.FillCmyk = _ctx.GS.FillCmyk;
                    renderCtx.GS.FillSepTints = _ctx.GS.FillSepTints;
                    renderCtx.GS.StrokeCmyk = _ctx.GS.StrokeCmyk;
                    renderCtx.GS.StrokeSepTints = _ctx.GS.StrokeSepTints;
                    renderCtx.GS.FillDeviceGray = _ctx.GS.FillDeviceGray;
                    renderCtx.GS.StrokeDeviceGray = _ctx.GS.StrokeDeviceGray;
                    renderCtx.GS.FillDeviceRgb = _ctx.GS.FillDeviceRgb;
                    renderCtx.GS.StrokeDeviceRgb = _ctx.GS.StrokeDeviceRgb;
                }
            }
            // Propagate RenderDepth (matching the Form XObject and Type 3 glyph paths) so the
            // MaxRenderRecursionDepth guard actually fires for offscreen/tiling/soft-mask rendering.
            // Without this, content rendered into a tiling-pattern cell or soft-mask group
            // always restarts at depth 0, so a pattern whose cell re-references itself recurses
            // unbounded (StackOverflow) — a fresh interpreter per level defeats the cap.
            var inner = new ContentStreamInterpreter(renderCtx, _doc, resources, _fontCache)
                { HiddenOcgs = HiddenOcgs, RenderDepth = RenderDepth + 1, CancellationToken = CancellationToken, FormBudget = FormBudget };
            inner.Execute(data);
            return bitmap;
        }

        private void RenderTilingPattern(string name, bool stroke, bool evenOdd = false)
        {
            // Reported before any tile is built, and per paint side: the ink a pattern lays down is
            // the cell's own colour, not the current colour the overprint compositor works from.
            // A separation plate composites the cell per colorant, exactly.
            if ((stroke ? _ctx.GS.OverprintStroke : _ctx.GS.OverprintFill) && _ctx.Plate == null)
                PdfRenderContext.ReportOverprintSkipped(OverprintReportReason.Pattern);

            // Look up the pattern dict in /Pattern resource.
            if (_resources == null) { _ctx.NewPath(); return; }
            var patCat = _resources[PdfName.Pattern];
            if (patCat == null) { _ctx.NewPath(); return; }
            var patDict = _doc.ResolveObject(patCat) as PdfDictionary;
            if (patDict == null) { _ctx.NewPath(); return; }
            var patEntry = patDict[name];   // internal string indexer
            if (patEntry == null) { _ctx.NewPath(); return; }
            var pattern = _doc.ResolveObject(patEntry) as PdfDictionary;
            if (pattern == null) { _ctx.NewPath(); return; }
            if (MatrixGuard.PatternGeometryNotFinite(pattern, _doc))
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.PatternFillUnresolved,
                    "pattern ignored: a /Matrix, /BBox or step past the range of a float");
                _ctx.NewPath();
                return;
            }

            long patternType = (PdfResolve.Number(_doc, pattern, PdfName.PatternType))?.LongValue ?? 0;

            // Type 2 — shading pattern: the pattern dict contains a /Shading sub-dict.
            // Delegate directly to BuildShader, then paint the current path.
            if (patternType == 2)
            {
                var shadingEntry = _doc.ResolveObject(pattern[PdfName.Shading]);
                var shadingDict  = shadingEntry as PdfDictionary;
                if (shadingDict == null) { _ctx.NewPath(); return; }

                // §8.7.3.1: the pattern /Matrix maps pattern space to the default space of the content
                // stream the pattern is used in (GraphicsState.PatternBase), not to the space in force
                // at the paint — the path is already in device space, so the CTM the shader is built
                // under is replaced for the paint, never composed with. Under the CTM at the paint, an
                // earlier `cm` moved and scaled the gradient along with the shape it fills.
                float[] pm = { 1, 0, 0, 1, 0, 0 };
                var matArr2 = PdfResolve.Array(_doc, pattern, PdfName.Matrix);
                if (matArr2 != null && matArr2.Count >= 6)
                    for (int i = 0; i < 6; i++) pm[i] = GetArrayFloat(matArr2, i);
                _ctx.PushState();
                _ctx.GS.CTM = GraphicsState.Compose(pm, _ctx.GS.PatternBaseOrIdentity);

                var shader2 = BuildShader(shadingDict, forShadingOperator: false);
                if (shader2 != null)
                    // Built from the plate's tints when there is a plate (GetShadingSpace).
                    using (shader2) _ctx.DrawPatternPath(shader2, stroke, evenOdd, plateReady: _ctx.Plate != null);
                else
                    _ctx.NewPath();

                _ctx.PopState();
                return;
            }

            // Type 1 (tiling) — handled below.
            if (patternType != 1)
            { _ctx.NewPath(); return; }

            var bboxArr = PdfResolve.Array(_doc, pattern, PdfName.BBox);
            if (bboxArr == null || bboxArr.Count < 4) { _ctx.NewPath(); return; }
            float bx0 = GetArrayFloat(bboxArr, 0), by0 = GetArrayFloat(bboxArr, 1);
            float bx1 = GetArrayFloat(bboxArr, 2), by1 = GetArrayFloat(bboxArr, 3);
            float cellW = Math.Abs(bx1 - bx0), cellH = Math.Abs(by1 - by0);
            if (cellW <= 0 || cellH <= 0) { _ctx.NewPath(); return; }

            // XStep/YStep may be larger than BBox — the extra area is transparent — or smaller,
            // so that neighbouring cells overlap (§8.7.3.1); TilingLattice wraps the cell either way.
            float xStep = (float?)((PdfResolve.Number(_doc, pattern, PdfName.XStep))?.DoubleValue) ?? cellW;
            float yStep = (float?)((PdfResolve.Number(_doc, pattern, PdfName.YStep))?.DoubleValue) ?? cellH;
            if (xStep <= 0) xStep = cellW;
            if (yStep <= 0) yStep = cellH;

            // PaintType 2 = uncolored: inherit current fill/stroke colors.
            long paintType = (long?)((PdfResolve.Number(_doc, pattern, PdfName.PaintType))?.LongValue) ?? 1L;
            bool isUncolored = paintType == 2;

            // Cap tile pixel dimensions: a degenerate XStep/YStep can produce a tile
            // that is millions of pixels wide. SKBitmap silently fails to allocate that
            // memory and returns an empty bitmap, which then causes an SEHException in
            // sk_bitmap_make_shader. 4096 px is generous for any real tiling pattern.
            const int MaxTilePixels = 4096;

            // The tile is one period, XStep by YStep, in whole pixels; its own scale (sx, sy) maps
            // it onto exactly one step, so the period does not drift by the rounding.
            var lattice = TilingLattice.For(cellW, cellH, xStep, yStep);
            int pixTileW = Math.Max(1, Math.Min(MaxTilePixels, SaturatingCast.ToInt(Math.Ceiling(lattice.StepX * _ctx.DpiScale))));
            int pixTileH = Math.Max(1, Math.Min(MaxTilePixels, SaturatingCast.ToInt(Math.Ceiling(lattice.StepY * _ctx.DpiScale))));
            float sx = pixTileW / lattice.StepX, sy = pixTileH / lattice.StepY;
            if (lattice.Approximated && (FormBudget?.FirstTilingApproximation() ?? true))
                ReportTilingOverlapApproximated(lattice);

            int pixW = Math.Max(1, Math.Min(MaxTilePixels, SaturatingCast.ToInt(Math.Ceiling(cellW * sx))));
            int pixH = Math.Max(1, Math.Min(MaxTilePixels, SaturatingCast.ToInt(Math.Ceiling(cellH * sy))));

            float _minBx = Math.Min(bx0, bx1), _minBy = Math.Min(by0, by1);
            // Drawn at the tile's own scale (sx, sy), the one the bitmap was sized by and the shader's
            // matrix below maps with; at the page's scale the cell fell short of the tile by the
            // step's rounding — at a one-point step the tile was the cell's empty top rows, and a
            // fine hatch at a step of five and a half pixels gapped by a tenth of its period.
            SKBitmap cellBitmap = isUncolored
                ? RenderToBitmap(pattern, pixW, pixH, _ctx.GS.FillColor, _ctx.GS.StrokeColor,
                    innerWidth: cellW, innerHeight: cellH, bboxOriginX: _minBx, bboxOriginY: _minBy, inheritPlate: true,
                    scaleX: sx, scaleY: sy)
                : RenderToBitmap(pattern, pixW, pixH,
                    innerWidth: cellW, innerHeight: cellH, bboxOriginX: _minBx, bboxOriginY: _minBy, inheritPlate: true,
                    scaleX: sx, scaleY: sy);
            if (cellBitmap == null) { _ctx.NewPath(); return; }

            // The tile: the cell where the step is larger (padded with transparency), and every
            // cell of the lattice that reaches into the period where it is smaller — so the part of
            // a cell that overlaps its neighbour's tile is drawn there, as §8.7.3.1 allows.
            SKBitmap tileBitmap;
            if (lattice.Copies.Count == 1 && pixTileW == pixW && pixTileH == pixH)
            {
                tileBitmap = cellBitmap;
            }
            else
            {
                // The composition is charged as the cell was: each copy is a blit of at most the
                // tile's area, and the cell's pixels are read into an image once.
                if (FormBudget != null
                    && !FormBudget.TryChargeOffscreenPixels((long)pixTileW * pixTileH * lattice.Copies.Count))
                {
                    cellBitmap.Dispose();
                    _ctx.NewPath();
                    return;
                }
                tileBitmap = new SKBitmap(pixTileW, pixTileH, SKColorType.Rgba8888, SKAlphaType.Premul);
                using (var tc = new SKCanvas(tileBitmap))
                // One image for every copy: SKCanvas.DrawBitmap makes an image of the bitmap on each
                // call, which for sixty-four copies of a 4096-pixel cell was a gigabyte of copying per
                // pattern fill before a pixel of it was clipped away.
                using (var cellImage = SKImage.FromBitmap(cellBitmap))
                {
                    tc.Clear(SKColors.Transparent);
                    // The tile's bottom row is the cell's bottom edge (flipCell below) and pixel
                    // rows run down, so a cell shifted by (dx, dy) — whole steps, so whole tiles of
                    // pixels — sits bottom-aligned, dy·sy rows higher; drawn at its own size, unscaled
                    // (a 1:1 blit: SKSamplingOptions.Default is the obsolete overload's sampling).
                    foreach (var (dx, dy) in lattice.Copies)
                        tc.DrawImage(cellImage, (float)Math.Round(dx * sx),
                                     (float)Math.Round(pixTileH - pixH - dy * sy), SKSamplingOptions.Default);
                }
                cellBitmap.Dispose();
            }

            try
            {
                // Guard: Skia throws an SEHException from sk_bitmap_make_shader
                // when the bitmap has no pixel data (empty allocation or 0 dimensions).
                if (tileBitmap.Width <= 0 || tileBitmap.Height <= 0 || tileBitmap.IsEmpty)
                {
                    _ctx.NewPath();
                    return;
                }

                // Apply pattern /Matrix if present.
                float[] patMatrix = { 1, 0, 0, 1, 0, 0 };
                var matArr = PdfResolve.Array(_doc, pattern, PdfName.Matrix);
                if (matArr != null && matArr.Count >= 6)
                {
                    for (int i = 0; i < 6; i++)
                        patMatrix[i] = GetArrayFloat(matArr, i);
                }

                // Build the shader local matrix for SKShader.CreateBitmap.
                // Skia's local matrix maps FROM bitmap space TO canvas-local space.
                // Inversion gives us canvas-local → bitmap.
                //
                // Coordinate spaces:
                //  • canvas-local — the space _currentPath lives in.  TransformPoint
                //    bakes in the PDF→screen Y-flip (PageHeight − cy), so this is
                //    SCREEN space (Y-down), NOT PDF page space.  (The canvas base
                //    transform is a plain Scale(dpi,dpi); the Y-flip lives in
                //    TransformPoint, for miter joins — which is why the flip must be
                //    re-introduced here.)
                //  • pattern /Matrix maps pattern space → PDF page space (Y-up).
                //  • flipCell maps pattern space → bitmap pixels (Y-flip + DpiScale).
                //
                // So: screen(Y-down) --pageFlip--> page(Y-up) --patMatrix⁻¹--> pattern
                //     --flipCell--> bitmap.  pageFlip = [1 0 0 -1 0 PageHeight].
                // skLocal is the inverse (bitmap → screen) for Skia's convention.
                //
                // Do NOT include gsCTM⁻¹: the path already carries the CTM, so adding
                // gsCTM⁻¹ would double-invert it, mirroring patterns rendered inside
                // a CTM with a non-identity scale or reflection (e.g. -1 0 0 1 ... cm).
                // What IS included is the pattern base (§8.7.3.1, GraphicsState.PatternBase): the
                // /Matrix maps pattern space to the default space of the content stream the pattern
                // is used in — a form's space at its Do, a glyph's, a cell's — so a pattern used in
                // a scaled form keeps the form's period, where it kept the page's.
                // The tile's own scale, sx by sy, not DpiScale: a tile of whole pixels is one step.
                // It is anchored, as the SVG writer's <pattern> is, at the cell's bottom-left
                // corner — its top row one step above the bottom edge — so the two share one lattice.
                float _tileTopY = Math.Min(by0, by1) + lattice.StepY;
                float _leftX = Math.Min(bx0, bx1);
                var flipCell = new SKMatrix
                {
                    ScaleX = sx,   SkewX  = 0f,  TransX = -_leftX * sx,
                    SkewY  = 0f,   ScaleY = -sy,  TransY = _tileTopY * sy,
                    Persp2 = 1f
                };
                // Bridge screen space (Y-down, where _currentPath lives) to PDF page
                // space (Y-up, where the pattern base is defined).
                var pageFlip = _ctx.PageFlip;
                var patSKM = GraphicsState.ToSKMatrix(patMatrix);
                if (!patSKM.TryInvert(out var patInv))
                    patInv = SKMatrix.Identity;
                if (!GraphicsState.ToSKMatrix(_ctx.GS.PatternBaseOrIdentity).TryInvert(out var baseInv))
                    baseInv = SKMatrix.Identity;
                // canvas-local(screen) → bitmap:  flipCell ∘ patInv ∘ baseInv ∘ pageFlip
                var canvasToBitmap = SKMatrix.Concat(flipCell, SKMatrix.Concat(patInv, SKMatrix.Concat(baseInv, pageFlip)));
                if (!canvasToBitmap.TryInvert(out var skLocal))
                    skLocal = SKMatrix.Identity;

                SKShader shader;
                try
                {
                    shader = SKShader.CreateBitmap(
                        tileBitmap,
                        SKShaderTileMode.Repeat,
                        SKShaderTileMode.Repeat,
                        skLocal);
                }
                catch (Exception ex)
                {
                    RenderDiagnostics.Report(RenderDiagnosticKind.TilingPatternShaderFailed, $"SKShader.CreateBitmap failed ({tileBitmap.Width}x{tileBitmap.Height})", ex);
                    _ctx.NewPath();
                    return;
                }

                using (shader) _ctx.DrawPatternPath(shader, stroke, evenOdd, plateReady: true);
            }
            finally
            {
                tileBitmap.Dispose();
            }
        }

        /// <summary>A tiling pattern whose overlap needs more cell copies than one tile is built from (<see cref="TilingLattice"/>).</summary>
        private static void ReportTilingOverlapApproximated(TilingLattice lattice)
            => RenderDiagnostics.Report(RenderDiagnosticKind.PatternApproximated,
                   $"tiling pattern cells overlap so far (step {lattice.StepX}x{lattice.StepY}) that one tile " +
                   $"would need more than {TilingLattice.MaxCopies} copies of the cell; each cell is drawn " +
                   "without its overlap.");
    }
}
