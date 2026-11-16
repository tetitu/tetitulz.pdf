// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
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
        private void BeginText()
        {
            _gs.Tm  = Identity();
            _gs.Tlm = Identity();
            _textClipAccum = null;
            _textClipUsed = false;
            _textKnockoutPending = true;
        }

        /// <summary>
        /// Set at <c>BT</c>; the knockout group opens at the first glyph that paints, from the state
        /// then in force — a <c>Tr</c> or <c>gs</c> between <c>BT</c> and the first show is part of
        /// what the group must stand for, and a text object that paints nothing needs no group.
        /// </summary>
        private bool _textKnockoutPending;

        private void EnsureTextKnockout()
        {
            if (!_textKnockoutPending) return;
            if (!(((TextRenderMode)_gs.TextRenderMode).Fills() || ((TextRenderMode)_gs.TextRenderMode).Strokes())) return;   // stays pending
            _textKnockoutPending = false;
            BeginTextKnockout();
        }

        /// <summary>
        /// The text-knockout group open for this text object: its alpha, and the fill and stroke
        /// alphas the state held at <c>BT</c>, which the glyphs give up to the group while it is
        /// open (they paint opaque) and get back when it closes. Null when no group is open.
        /// </summary>
        private (float Alpha, float Fill, float Stroke)? _textKnockout;

        /// <summary>
        /// §9.3.8: <c>/TK true</c> makes the text object a knockout group. SVG has no knockout
        /// compositing model, but the common case needs none: glyphs that all paint at one alpha
        /// <c>a</c>, Normal blending, no soft mask. Knocked out, each glyph replaces the ones before
        /// it at alpha <c>a</c>, so the object is the union of its glyphs at <c>a</c> with the
        /// latest on top — exactly the glyphs painted opaque inside <c>&lt;g opacity="a"&gt;</c>,
        /// which is what is emitted. It is also the raster path's model (a layer at α = 1, each
        /// glyph painted Src at its alpha). Anything else is reported.
        /// </summary>
        /// <remarks>
        /// <para><b>"One alpha" is the alpha of each paint the render mode uses</b>: <c>/ca</c>
        /// alone for filled text, <c>/CA</c> alone for stroked, both — and equal — for fill and
        /// stroke. Invisible and clip-only text paints nothing and needs no group.</para>
        /// <para><b>A soft mask or a blend mode</b> would need the group to carry what differs
        /// per paint, and is reported instead. Inside the text object, a <c>gs</c> that sets an
        /// alpha, a blend mode or a soft mask, or a <c>Tr</c> bringing in a paint at another alpha,
        /// closes the group there and is reported (<see cref="EndTextKnockoutEarly"/>).</para>
        /// </remarks>
        private void BeginTextKnockout()
        {
            if (!TextKnockoutIsObservable()) return;

            float? alpha = GroupAlpha(_gs.TextRenderMode, _gs.FillAlpha, _gs.StrokeAlpha);
            if (alpha == null || _gs.SvgMaskId != null || _gs.BlendMode != null)
            {
                ReportIgnoredParameter(IgnoredGraphicsStateParameter.TextKnockout);
                return;
            }

            _sb.Append("<g opacity=\"").Append(F(alpha.Value)).Append("\">\n");
            _unclosedGroups++;
            _textKnockout = (alpha.Value, _gs.FillAlpha, _gs.StrokeAlpha);
            _gs.FillAlpha = _gs.StrokeAlpha = 1f;
        }

        /// <summary>The one alpha every paint of <paramref name="mode"/> uses, or null when they differ.</summary>
        private static float? GroupAlpha(int mode, float fill, float stroke)
        {
            bool fills = ((TextRenderMode)mode).Fills(), strokes = ((TextRenderMode)mode).Strokes();
            if (fills && strokes) return Math.Abs(fill - stroke) <= OpaqueAlphaEpsilon ? fill : null;
            return fills ? fill : stroke;
        }

        private bool _textFillUnderStrokeReported;

        /// <summary>A text run's fill paint and opacity (the opacity left out at 1), or none.</summary>
        private string TextFillAttributes(bool fill, string fillVal)
            => !fill ? " fill=\"none\""
             : Math.Abs(_gs.FillAlpha - 1f) > OpaqueAlphaEpsilon ? $" fill=\"{fillVal}\" fill-opacity=\"{F(_gs.FillAlpha)}\""
             : $" fill=\"{fillVal}\"";

        /// <summary>A text run's stroke opacity, left out at 1.</summary>
        private string TextStrokeOpacity()
            => Math.Abs(_gs.StrokeAlpha - 1f) > OpaqueAlphaEpsilon ? $" stroke-opacity=\"{F(_gs.StrokeAlpha)}\"" : "";

        /// <summary>
        /// A text run's paint, both glyph routes: <paramref name="element"/> makes the run's
        /// element (a glyph-outline <c>&lt;g&gt;</c> or a <c>&lt;text&gt;</c>) carrying the paint
        /// attributes it is handed; this decides how many and which.
        /// </summary>
        /// <remarks>
        /// <para>Text in rendering mode 2 or 6 is one object to §11.7.4.4, decided as for a path
        /// (<see cref="FillStrokeCompositing"/>, <see cref="EmitFillKnockedOutByStroke"/>): in the
        /// knockout case the fill is masked by the glyphs filled white and stroked black, then the
        /// stroke is its own element; in the overprint case the run is painted at alpha 1 inside a
        /// group at the alpha and blend mode. With both a soft mask and a blend mode the run stays
        /// one element, since a masked group would isolate the fill's blend, and that is reported.</para>
        /// <para><paramref name="fillAttrs"/> and <paramref name="strokeAttrs"/> read the state when
        /// called, so the overprint group's alphas reach them; <paramref name="strokeShape"/> is the
        /// stroke's width and geometry alone, for the mask.</para>
        /// </remarks>
        private void EmitTextPaint(Func<string, string> element, Func<string> fillAttrs, Func<string> strokeAttrs,
                                   string strokeShape, bool fill, bool stroke)
        {
            FillStrokeComposite mode = fill && stroke ? FillStrokeMode() : FillStrokeComposite.Direct;
            string mask = SoftMaskAttribute();
            if (mode == FillStrokeComposite.Knockout && mask.Length > 0 && _gs.BlendMode != null)
            {
                mode = FillStrokeComposite.Direct;
                if (!_textFillUnderStrokeReported)
                {
                    _textFillUnderStrokeReported = true;
                    RenderDiagnostics.Report(RenderDiagnosticKind.TransparencyGroupApproximated,
                        "SVG: text filled and stroked (rendering mode 2 or 6) under both a soft mask " +
                        "and a blend mode is exported as one element, so its stroke composites over " +
                        "its fill (§11.7.4.4 keeps the fill out from under it).");
                }
            }

            switch (mode)
            {
                case FillStrokeComposite.OverprintGroup:
                {
                    float alpha = _gs.StrokeAlpha, fillAlpha = _gs.FillAlpha;
                    string blend = _gs.BlendMode;
                    _sb.Append($"<g opacity=\"{F(alpha)}\"{PaintStyleAttribute(true, true)}>\n");
                    _gs.FillAlpha = _gs.StrokeAlpha = 1f;
                    _gs.BlendMode = null;
                    _sb.Append(element(fillAttrs() + strokeAttrs() + PaintStyleAttribute(true, true) + mask)).Append('\n');
                    _sb.Append("</g>\n");
                    _gs.FillAlpha = fillAlpha;
                    _gs.StrokeAlpha = alpha;
                    _gs.BlendMode = blend;
                    break;
                }
                case FillStrokeComposite.Knockout:
                {
                    string id = "fsko" + NextId();
                    _sb.Append($"<defs><mask id=\"{id}\">{element(" fill=\"white\" stroke=\"none\"")}" +
                               $"{element(" fill=\"none\" stroke=\"black\"" + strokeShape)}</mask></defs>\n");
                    string fillElement = element(fillAttrs() + " stroke=\"none\"" + PaintStyleAttribute(true, false) +
                                                 $" mask=\"url(#{id})\"");
                    if (mask.Length > 0) _sb.Append($"<g{mask}>{fillElement}</g>\n");
                    else _sb.Append(fillElement).Append('\n');
                    _sb.Append(element(" fill=\"none\"" + strokeAttrs() + PaintStyleAttribute(false, true) + mask)).Append('\n');
                    break;
                }
                default:
                    _sb.Append(element(fillAttrs() + strokeAttrs() + PaintStyleAttribute(fill, stroke) + mask)).Append('\n');
                    break;
            }
        }

        /// <summary>Closes the knockout group at <c>ET</c>, giving the state its alphas back.</summary>
        private void EndTextKnockout()
        {
            if (_textKnockout is not { } open) return;
            CloseTextKnockoutGroup();
            _gs.FillAlpha = open.Fill;
            _gs.StrokeAlpha = open.Stroke;
        }

        /// <summary>
        /// Something inside the text object ended what the group's one alpha can stand for: a
        /// <c>gs</c> setting an alpha, blend mode or soft mask, or a <c>Tr</c> whose paints do not
        /// share the group's alpha. The group closes here and the glyphs after it paint at their
        /// own alphas, non-knockout. Called after the operator wrote its values, so only the
        /// alphas it did not set are given back.
        /// </summary>
        private void EndTextKnockoutEarly(bool strokeSet = false, bool fillSet = false)
        {
            if (_textKnockout is not { } open) return;
            CloseTextKnockoutGroup();
            if (!strokeSet) _gs.StrokeAlpha = open.Stroke;
            if (!fillSet)   _gs.FillAlpha   = open.Fill;
            ReportIgnoredParameter(IgnoredGraphicsStateParameter.TextKnockout);
        }

        /// <summary><c>Tr</c> inside an open knockout group: kept only while the new mode's paints share its alpha.</summary>
        private void OnTextRenderModeChanged()
        {
            if (_textKnockout is not { } open) return;
            int mode = _gs.TextRenderMode;
            if (!(((TextRenderMode)mode).Fills() || ((TextRenderMode)mode).Strokes())) return;   // paints nothing
            if (GroupAlpha(mode, open.Fill, open.Stroke) is float a && Math.Abs(a - open.Alpha) <= OpaqueAlphaEpsilon)
                return;
            EndTextKnockoutEarly();
        }

        private void CloseTextKnockoutGroup()
        {
            _sb.Append("</g>\n");
            _unclosedGroups--;
            _textKnockout = null;
        }

        /// <summary>The raster path's test: knockout and non-knockout differ only under these.</summary>
        /// <remarks>
        /// <para><b>Asked on the state at the text object's first painting glyph, rather than at
        /// the <c>gs</c> that sets <c>/TK</c>.</b> <c>/TK</c>'s initial value is <b>true</b>
        /// (Table 52), so a file that never writes the key still asks for knockout: gating on an
        /// explicit token would act only for files redundantly restating the default.</para>
        /// <para>With opaque paint, no soft mask and Normal blending the knockout and non-knockout
        /// models are pixel-identical, so no group is opened and nothing is reported for ordinary
        /// text — the "presence is not deviation" rule.</para>
        /// </remarks>
        private bool TextKnockoutIsObservable()
            => _gs.TextKnockout
            && (Math.Abs(_gs.FillAlpha   - 1f) > OpaqueAlphaEpsilon
             || Math.Abs(_gs.StrokeAlpha - 1f) > OpaqueAlphaEpsilon
             || _gs.SvgMaskId != null
             || _gs.BlendMode != null);

        private void EndText()
        {
            // Before the text clip below: its group stays open to the enclosing Q, this one ends here.
            _textKnockoutPending = false;
            EndTextKnockout();

            // Flush an accumulated text clip (modes 4-7): emit one <clipPath> with the union
            // of all glyph runs and open a <g clip-path> scoped to the enclosing q/Q (via
            // ClipGroupCount, the same mechanism OpClip/OpQ use). An empty accumulator clips
            // out everything (PDF §9.4.3), matching the bitmap renderer.
            if (!_textClipUsed) return;
            _textClipUsed = false;
            string clipId = "txtclip" + NextId();
            _sb.Append("<defs><clipPath id=\"").Append(clipId).Append("\">");
            _sb.Append(_textClipAccum?.ToString() ?? string.Empty);
            _sb.Append("</clipPath></defs>\n");
            _sb.Append($"<g clip-path=\"url(#{clipId})\">\n");
            _gs.ClipGroupCount++; _unclosedGroups++;
            _textClipAccum = null;
        }

        private void OpTf(ContentStreamOperation op)
        {
            if (op.Operands.Length >= 2)
            {
                _gs.FontName = (op.Operands[0] as PdfName)?.Value;
                _gs.FontSize = Flt(op, 1);
            }
        }

        private void OpTd(ContentStreamOperation op, bool updateLeading)
        {
            if (updateLeading) _gs.Leading = -Flt(op, 1);
            float tx = Flt(op, 0), ty = Flt(op, 1);
            float[] move = { 1, 0, 0, 1, tx, ty };
            _gs.Tlm = Compose(move, _gs.Tlm);
            _gs.Tm  = CopyArr(_gs.Tlm);
        }

        private void OpTm(ContentStreamOperation op)
        {
            if (op.Operands.Length < 6) return;
            float[] m = { Flt(op,0), Flt(op,1), Flt(op,2), Flt(op,3), Flt(op,4), Flt(op,5) };
            // Same guard, same reason as OpCm — ContentStreamInterpreter.OpTm has had it and this
            // side did not, so a non-finite Tm reached the export as literal NaN/Infinity.
            if (!MatrixGuard.AllFinite(m))
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                    "Tm ignored: non-finite matrix operand");
                return;
            }
            _gs.Tm  = m;
            _gs.Tlm = CopyArr(_gs.Tm);
        }

        private void OpTStar()
        {
            float[] move = { 1, 0, 0, 1, 0, -_gs.Leading };
            _gs.Tlm = Compose(move, _gs.Tlm);
            _gs.Tm  = CopyArr(_gs.Tlm);
        }

        private void OpTj(ContentStreamOperation op)
        {
            if (op.Operands.Length < 1) return;
            byte[] bytes = op.Operands[0] is PdfString ps ? ps.RawBytes : Array.Empty<byte>();
            DrawTextBytes(bytes);
        }

        private void OpTJ(ContentStreamOperation op)
        {
            if (op.Operands.Length < 1 || op.Operands[0] is not PdfArray arr) return;
            bool vertical = Fonts.GetWMode(_gs.FontName, _resources, _doc) == 1;
            foreach (var item in arr)
            {
                if (item is PdfString ps)
                    DrawTextBytes(ps.RawBytes);
                else if (item is PdfNumber kn)
                    // Along the writing direction — down in vertical writing (TextAdjustment).
                    _gs.Tm = Compose(TextAdjustment.Of(kn.DoubleValue, _gs.FontSize, _gs.HorizScale, vertical), _gs.Tm);
            }
        }

        // Returns true when the current font resource is a Type 3 font.
        private bool IsType3Font()
        {
            if (_gs.FontName == null || _resources == null) return false;
            var fontCat = _doc.ResolveObject(_resources[PdfName.Font]) as PdfDictionary;
            if (fontCat == null) return false;
            var fontDict = _doc.ResolveObject(fontCat[new PdfName(_gs.FontName)]) as PdfDictionary;
            if (fontDict == null) return false;
            return (PdfResolve.Name(_doc, fontDict, PdfName.Subtype))?.Value == PdfName.Type3.Value;
        }

        // Type 3 font keys with no entry in the PdfName cache.


        // Renders Type 3 glyph bytes by executing each CharProc content stream
        // in a child SvgContentWriter and wrapping the output in a <g transform> element.
        private void DrawType3GlyphsSvg(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return;
            if (Depth >= PdfLimits.MaxRenderRecursionDepth) return;
            var fontCat  = _doc.ResolveObject(_resources?[PdfName.Font]) as PdfDictionary;
            if (fontCat == null) return;
            var fontDict = _doc.ResolveObject(fontCat[new PdfName(_gs.FontName)]) as PdfDictionary;
            if (fontDict == null) return;

            // /FontMatrix: a corrupt one falls back to the default, and is said.
            float[] fm = Type3Encoding.FontMatrixOf(_doc, fontDict, out bool fmReplaced);
            if (fmReplaced)
                RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                    "SVG: Type 3 /FontMatrix is non-finite or singular; using the default " +
                    "[0.001 0 0 0.001 0 0].");

            // /CharProcs dict
            var charProcsObj  = fontDict[PdfName.CharProcs];
            var charProcsDict = _doc.ResolveObject(charProcsObj) as PdfDictionary;
            if (charProcsDict == null) return;

            // /Encoding → code → /CharProcs glyph name, through the one implementation the raster
            // interpreter also calls. This side built its own /Differences dictionary and had its
            // own base-encoding table; the two disagreed twice, in opposite directions, before they
            // were reconciled — and reconciled copies are what drift next.
            var encObj  = fontDict[PdfName.Encoding];
            var encDict = _doc.ResolveObject(encObj) as PdfDictionary;
            var diffMap = Type3Encoding.BuildMap(_doc, encDict);

            // /Widths + /FirstChar for advance-width fallback
            var widthsArr = PdfResolve.Array(_doc, fontDict, PdfName.Widths);
            int firstChar = PdfResolve.Number(_doc, fontDict, PdfName.FirstChar)?.IntValue ?? 0;

            // Th in the `a` slot (§9.4.4) — it scales the glyph, not only the advance.
            float[] textScale = { _gs.FontSize * _gs.HorizScale, 0, 0, _gs.FontSize, 0, _gs.TextRise };
            float[] tmFull    = Compose(textScale, Compose(_gs.Tm, _gs.CTM));

            // §9.3.6: Type 3 text in mode 3 or 7 is not painted, as the raster path leaves it; the
            // pen moves by /Widths, and mode 7's clip is not accumulated from a glyph procedure.
            var renderMode = (TextRenderMode)_gs.TextRenderMode;
            bool invisible = renderMode.IsInvisible() || renderMode == TextRenderMode.Clip;
            if (renderMode == TextRenderMode.Clip)
                RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                    "SVG: Type 3 text in render mode 7 adds nothing to the clipping path; the text is not painted and the clip is left as it was.");

            foreach (byte b in bytes)
            {
                if (invisible)
                {
                    AdvanceType3(b, widthsArr, firstChar, fm);
                    tmFull = Compose(textScale, Compose(_gs.Tm, _gs.CTM));
                    continue;
                }

                // A code the encoding does not define has NO glyph (§9.6.6.2), so the notdef branch
                // below is reached directly rather than through a synthetic "." + code name. That
                // name was not merely a roundabout route to the same place: a font whose
                // /CharProcs happened to contain an entry called /.1 had it DRAWN here and not by
                // the raster path. Deleting the copy deletes that for free.
                if (!diffMap.TryGetValue(b, out string glyphName))
                {
                    AdvanceType3(b, widthsArr, firstChar, fm);
                    tmFull = Compose(textScale, Compose(_gs.Tm, _gs.CTM));
                    continue;
                }

                var cpObj    = charProcsDict[new PdfName(glyphName)];
                var cpStream = _doc.ResolveObject(cpObj) as PdfStream;
                if (cpStream == null)
                {
                    // §9.6.6.2: a Type 3 code whose name is absent from /CharProcs draws NOTHING —
                    // the font simply does not define that glyph. §9.6.4.3's notdef applies to
                    // *simple* fonts backed by a font program that has a .notdef glyph; a Type 3
                    // font has no glyph but the CharProcs the file supplies.
                    //
                    // No hairline box to make the slot "visible": that would put ink on the page for a
                    // glyph the document never asked to be drawn, where the raster path draws nothing. The advance is still consumed below, which is the part that matters:
                    // the following glyphs must land where the file expects.
                    AdvanceType3(b, widthsArr, firstChar, fm);
                    tmFull = Compose(textScale, Compose(_gs.Tm, _gs.CTM));
                    continue;
                }

                byte[] cpData;
                try { cpData = cpStream.GetDecodedData(); }
                catch (Exception ex)
                {
                    // The raster path reports this; swallowing it here made a Type 3 glyph that
                    // failed to decode indistinguishable from one the font does not define.
                    RenderDiagnostics.Report(RenderDiagnosticKind.Type3GlyphDecodeFailed,
                        "SVG: Type 3 CharProc stream could not be decoded; glyph skipped", ex);

                    // ADVANCE ANYWAY, as the raster path does. Skipping the advance does not lose
                    // the glyph that failed — it loses the POSITION of every glyph after it, and
                    // the error accumulates across the run. Invisible in the failed glyph itself,
                    // which is why comparing rendered pixels never surfaced it.
                    AdvanceType3(b, widthsArr, firstChar, fm);
                    tmFull = Compose(textScale, Compose(_gs.Tm, _gs.CTM));
                    continue;
                }

                // The glyph procedure is charged to the page's invocation budget, as the raster side
                // charges it (DrawType3Bytes): the depth cap bounds one branch, not a glyph that shows
                // four of its own font. Past the budget the glyph advances and draws nothing.
                if (Shared?.FormBudget != null && !Shared.FormBudget.TryChargeFormInvocation(cpData.Length))
                {
                    AdvanceType3(b, widthsArr, firstChar, fm);
                    tmFull = Compose(textScale, Compose(_gs.Tm, _gs.CTM));
                    continue;
                }

                // Full glyph transform: fontMatrix * textState * CTM
                float[] glyphM = Compose(fm, tmFull);

                // A Type 3 font's own /Resources take precedence for its CharProcs (§9.6.5) —
                // the page resources are only the fallback. Passing the page dictionary
                // unconditionally would make a glyph referencing a font-local XObject, pattern or
                // font draw nothing.
                var glyphRes = _doc.ResolveObject(fontDict[PdfName.Resources]) as PdfDictionary
                               ?? _resources;

                var glyphSb = new StringBuilder();
                var child   = new SvgContentWriter(glyphSb, _doc, glyphRes, _pageW, _pageH);
                child.InheritFrom(this);
                child._inCharProc = true;
                child._gs = _gs.Clone();
                // Clear Tm/Tlm/CTM: glyph coords are in font space, not page space.
                child._gs.CTM = (float[])glyphM.Clone();
                child._gs.Tm  = Identity();
                child._gs.Tlm = Identity();
                // §8.7.3.1: a pattern the glyph uses maps through the glyph's space. The pattern
                // selection is kept, as the raster's glyph interpreter keeps the state's: a d1
                // glyph's fill is the text's fill, which may be a pattern (resolved against the
                // glyph's own resources, the page's when the font names none, as the raster looks
                // it up), and a d0 glyph starts from it as any content starts from its state.
                child._gs.PatternBase = (float[])glyphM.Clone();
                child.Execute(cpData);

                string inner = glyphSb.ToString().Trim();
                if (inner.Length > 0)
                    _sb.Append($"<g>{inner}</g>\n");

                // Determine advance width: d0/d1 > /Widths fallback.
                float advW;
                if (!float.IsNaN(child._t3AdvanceWidth))
                {
                    advW = child._t3AdvanceWidth;
                }
                else if (widthsArr != null)
                {
                    // Type3Encoding.GlyphWidth returns TEXT space (already times fm[0]), so undo
                    // that here — the expression below applies fm[0] itself, and the d0/d1 branch
                    // above supplies a glyph-space width. Routing through the shared reader is what
                    // makes an INDIRECT /Widths element resolve; GetArrFloat, an operand helper,
                    // does not follow references and read it as 0.
                    advW = fm[0] != 0f
                        ? Type3Encoding.GlyphWidth(b, widthsArr, firstChar, fm, _doc) / fm[0]
                        : 0f;
                }
                else
                {
                    // ZERO, not `FontSize * 0.6`. Every other branch feeding `advW` is a
                    // GLYPH-space width, which the expression below multiplies by
                    // `fm[0] * FontSize` to reach text space; a value already in points went
                    // through that scaling as well, so the advance came out proportional to
                    // FontSize SQUARED and — at the default /FontMatrix — about 0.0007 em, i.e.
                    // effectively nothing anyway. /Widths is required for a Type 3 font
                    // (Table 112), so this is the malformed case, and the raster path's
                    // GetType3Width returns 0 for it.
                    advW = 0f;
                    RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                        "SVG: Type 3 font has no /Widths and the glyph set no width with d0/d1; " +
                        "advancing by 0 (§9.6.5 Table 112 requires /Widths).");
                }

                // §9.4.4's tx, in the Type3Encoding leaf. This was the FOURTH copy of the formula —
                // the two skip paths and the raster drawn path were the other three — and the one a
                // test actually reaches, since it is the advance between glyphs that DRAW.
                float scaledAdv = Type3Encoding.AdvanceTx(
                    advW * fm[0], b,
                    _gs.FontSize, _gs.CharSpacing, _gs.WordSpacing, _gs.HorizScale);
                _gs.Tm = Compose(new float[] { 1, 0, 0, 1, scaledAdv, 0 }, _gs.Tm);

                // Recompute full matrix for next glyph.
                tmFull = Compose(textScale, Compose(_gs.Tm, _gs.CTM));
            }
        }

        /// <summary>
        /// Advances <c>Tm</c> by one Type 3 glyph's width, for a code that drew nothing — absent
        /// from <c>/CharProcs</c>, or whose CharProc would not decode.
        /// </summary>
        /// <remarks>
        /// <para><b>The width rule is the raster path's</b>
        /// (<c>ContentStreamInterpreter.GetType3Width</c>): <b>0</b> when <c>/Widths</c> is absent
        /// or the code falls outside <c>/FirstChar…/LastChar</c>. This side used <c>0.5</c> em in
        /// both those cases. At the conventional <c>/FontMatrix [0.001 0 0 0.001 0 0]</c> that
        /// difference is 0.0005·FontSize points and effectively invisible, which is how it
        /// survived — but §9.6.5 lets a Type 3 font declare any matrix, and at
        /// <c>[1 0 0 1 0 0]</c> it is <b>half the font size per glyph</b>. Our own PostScript
        /// converter emits both: <c>PdfResourceCollector</c> writes <c>/Widths</c> covering only
        /// the glyphs it built and copies the font's matrix verbatim.</para>
        /// <para>One method for both callers so the two cannot re-split; the decode-failure branch
        /// advances too, or every glyph after it would lose its position rather than the glyph that
        /// failed.</para>
        /// </remarks>
        private void AdvanceType3(int code, PdfArray widthsArr, int firstChar, float[] fm)
        {
            // §9.4.4's tx, in the Type3Encoding leaf — one implementation, shared with the raster
            // path. This copy distributed /Th across the three terms rather than scaling their sum;
            // algebraically equal, and nothing tied the two together.
            float tx = Type3Encoding.AdvanceTx(
                Type3Encoding.GlyphWidth(code, widthsArr, firstChar, fm, _doc), code,
                _gs.FontSize, _gs.CharSpacing, _gs.WordSpacing, _gs.HorizScale);
            _gs.Tm = Compose(new float[] { 1, 0, 0, 1, tx, 0 }, _gs.Tm);
        }


        /// <summary>
        /// Resolves a named pattern to an SVG paint value, or <c>"none"</c> - <b>never</b> the
        /// graphics state's solid colour - reporting the loss when it cannot be built.
        /// </summary>
        /// <remarks>
        /// <b>One decision, four sites.</b> The two path sites (fill and stroke) were changed to
        /// paint <c>none</c> and report, with the note that "falling through to _gs.FillColor
        /// paints whatever solid colour the graphics state happened to be left holding, which is a
        /// confidently wrong result rather than a visibly missing one" - and the two <b>text</b>
        /// sites went on doing exactly that. <c>/Pattern cs /P1 scn BT (Hello) Tj ET</c> with an
        /// unbuildable /P1 renders as nothing and exported as solid black text, because the Pattern
        /// colour space sets no fill colour and the state still holds 8.4.1's initial black.
        /// Extracting the decision is what stops the four drifting again.
        /// </remarks>
        private string ResolvePatternPaintOrNone(string patternName, string what,
                                                 float[] elementInverse = null)
        {
            string paint = ResolveAnyPatternFill(patternName, elementInverse);
            if (paint != null) return paint;

            // Past the budget every later pattern resolves to null for one already-reported reason.
            // RenderBudget reports once per page on purpose; reporting again per paint would send a
            // host tens of thousands of copies of one message, which is a denial of service against
            // its logging rather than a report to it.
            if (Shared?.FormBudget?.Exhausted == true) return "none";

            RenderDiagnostics.Report(RenderDiagnosticKind.PatternFillUnresolved,
                $"SVG: {what} pattern /{patternName} could not be resolved; nothing painted.");
            return "none";
        }

        private void DrawTextBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return;
            EnsureTextKnockout();

            // Delegate Type 3 glyph rendering to the dedicated method.
            if (IsType3Font()) { DrawType3GlyphsSvg(bytes); return; }

            // Decode bytes to Unicode; read PDF width table for per-glyph advance.
            var fc = Fonts;
            string text  = fc.DecodeText(_gs.FontName, bytes, _resources, _doc);
            float totalAdvance = ComputePdfAdvance(fc, bytes);
            int wMode    = fc.GetWMode(_gs.FontName, _resources, _doc);
            float verticalAdvance = 0f;

            // Text position: compose font-size scale onto Tm, then through CTM.
            //
            // Vertical writing does NOT rotate the glyphs. §9.7.4.3 changes the writing
            // DIRECTION — glyphs stack downward — but each glyph is still drawn upright; a
            // rotated form is selected by the font's own vertical variants, not by a matrix.
            // Not [0, -Tfs, Tfs, 0], which with the element's -d Y-flip would produce a mirrored 90°
            // frame, vertical CJK rotated AND mirrored. The raster path draws them upright
            // (VerticalCidTextTests.VerticalCidFont_SingleGlyph_
            // DrawsUprightNotRotated).
            //
            // Th belongs in the `a` slot either way (§9.4.4).
            float[] glyphScale =
                { _gs.FontSize * _gs.HorizScale, 0f, 0f, _gs.FontSize, 0f, _gs.TextRise };
            float[] scaledTm = Compose(glyphScale, _gs.Tm);
            float[] fullM = Compose(scaledTm, _gs.CTM);

            // SVG matrix: a b c d e f → matrix(a,b,c,d,e,f)
            // Note: The outer <g> already flips Y; text itself must be re-flipped
            // so characters don't appear mirrored.
            float a = fullM[0], b_  = fullM[1], c = fullM[2], d = fullM[3],
                  e = fullM[4], fv  = fullM[5];
            // Apply extra Y-flip at text element level.
            string matrix = $"matrix({F(a)},{F(b_)},{F(c)},{F(-d)},{F(e)},{F(fv)})";

            // Text rendering mode (PDF §9.3.6). Modes 4-7 are modes 0-3 PLUS "add the glyphs
            // to the clipping path at ET", so masking off the clip bit leaves the paint mode.
            // SVG cannot accumulate a clip path across multiple text runs, but we approximate
            // per-run clipping by wrapping the text in <clipPath> + <text clip-path="..."/>.
            var trm = (TextRenderMode)_gs.TextRenderMode;
            bool addToClip = trm >= TextRenderMode.FillAndClip;
            var paintMode = (TextRenderMode)((int)trm & 3);
            if (paintMode.IsInvisible() && !addToClip) // pure invisible: just advance
            {
                // A vertical run advances DOWNWARD. This moved Tm by the horizontal advance
                // whatever the writing mode, so invisible text — the OCR layer over a scanned
                // CJK page, exactly where Tr 3 is used — left the pen in the wrong place and
                // displaced everything after it.
                float[] invisM = wMode == 1
                    ? new[] { 1f, 0f, 0f, 1f, 0f, -VerticalRunAdvance(fc, bytes) }
                    : new[] { 1f, 0f, 0f, 1f, totalAdvance, 0f };
                _gs.Tm = Compose(invisM, _gs.Tm);
                return;
            }

            // Overprint is handled where the element is emitted — PaintStyleAttribute on the <text>
            // below and on the glyph-outline <g> — on the same terms as a path, so an overprinting
            // run is approximated with multiply rather than reported as a gap this exporter has.

            // Prefer viewer-independent vector glyph outlines for embedded fonts (the actual
            // embedded glyph shapes, pixel-identical to the raster renderer), plus an invisible
            // <text> overlay so the text stays selectable and searchable. Non-embedded base-14 /
            // system-substitute fonts fall through to the <text> path below, where the viewer's
            // own matching font renders them and text remains selectable. Vertical writing mode
            // (wMode == 1) also falls through to the existing <text> path.
            if (wMode != 1 && TryEmitGlyphOutlines(fc, bytes, text, paintMode, addToClip))
                return;

            bool textFill   = paintMode.Fills();
            bool textStroke = paintMode.Strokes();

            // WHICH font. Without one there is nothing for the viewer's "own matching font" to match
            // on, and a Times document comes out in the viewer's sans-serif default. Resolved BEFORE the tag
            // is opened, because it may emit an @font-face <defs> of its own and a <defs> between
            // "<text" and ">" is not an element at all — and before the clip and the vertical
            // placement, because a glyph-indexed program changes the characters they are built on.
            SvgFontFace fontFace = FontFaceOf();

            // A program the embedder synthesised (a bare CFF, a converted Type 1) addresses its
            // glyphs by index, so the visible run is written as those code points and the
            // document's text goes into the invisible overlay the outline path also uses.
            string visibleText = text;
            SvgFontEmbedder.Program indexedProgram = default;
            bool glyphIndexed = _gs.FontName != null
                && _glyphIndexedPrograms.TryGetValue(_gs.FontName, out indexedProgram)
                && indexedProgram.GlyphIndexed;
            if (glyphIndexed) visibleText = GlyphIndexedText(fc, bytes, indexedProgram);

            // Vertical writing: place each glyph individually (§9.7.4.3). Horizontal runs keep
            // the single-origin form — the font's own advances are what the PDF /Widths already
            // agree with there, and ComputePdfAdvance moves Tm on. Placed before the clip copy is
            // written, which carries the same lists: a clip laid out horizontally clipped a
            // vertical column to its first glyph's row.
            string verticalPlacement = null;
            if (wMode == 1)
            {
                (verticalPlacement, verticalAdvance) = BuildVerticalPlacement(
                    fc, bytes, visibleText, new[] { a, b_, c, -d, e, fv }, Compose(_gs.Tm, _gs.CTM));
            }

            if (addToClip)
            {
                // Accumulate this run's glyph outline into the BT..ET text-clip path; it is
                // flushed into a single <clipPath> + clip <g> at ET (see EndText).
                _textClipAccum ??= new StringBuilder();
                _textClipAccum.Append($"<text transform=\"{matrix}\"{verticalPlacement} font-size=\"1\"{fontFace.ToAttributes()}>{XmlEsc(visibleText)}</text>");
                _textClipUsed = true;
            }

            // Pattern/shading-filled text → fill="url(#id)". For a shading gradient the
            // <text> carries its own transform, so the gradient (userSpaceOnUse) must be
            // pre-composed with the inverse text matrix to resolve in page space.
            string textFillVal = FillPaint;
            string textFillPattern = SelectedPatternName(stroke: false);
            if (textFill && textFillPattern != null)
            {
                float[] textInverse = PdfMatrix.Invert(new[] { a, b_, c, -d, e, fv });
                textFillVal = ResolvePatternPaintOrNone(textFillPattern, "text fill", textInverse);
            }

            string head = $"<text transform=\"{matrix}\"{verticalPlacement} font-size=\"1\"" // size baked into matrix
                        + fontFace.ToAttributes();
            string body = $">{XmlEsc(visibleText)}</text>";
            // stroke-width resolves in the element's OWN user space, and this <text> carries
            // `matrix` — font size, text matrix and CTM all baked in — so the page-space width has
            // to be divided back out by that matrix's scale. Dividing by the font size alone would be
            // right only while Tm and the CTM are both unscaled; a `2 0 0 2 0 0 cm` or a scaled Tm
            // would make the stroke half as thick as the raster path draws it. The §8.4.3.2 hairline
            // is a page-unit length and takes the same division, and so does the rest of §8.4.3's
            // stroke geometry, which this element carries so a dashed or round-capped text stroke
            // exports as drawn.
            string textStrokeShape = textStroke
                ? $" stroke-width=\"{F(StrokeWidthOrHairline(_gs.LineWidth, CtmScale(_gs.CTM)) / CtmScale(fullM))}\""
                  + StrokeGeometryAttributes(CtmScale(fullM))
                : "";
            // EmitTextPaint puts the active soft mask on it too. Every other painting element
            // carries it, and this one once did not, so text that took the plain <text> path (a
            // base-14 or any non-embedded font, the common case for ordinary Latin text) rendered
            // completely unmasked while the same run in an embedded font was masked.
            EmitTextPaint(attrs => head + attrs + body,
                () => TextFillAttributes(textFill, textFillVal),
                () => textStroke ? $" stroke=\"{StrokePaint}\"{textStrokeShape}{TextStrokeOpacity()}" : "",
                textStrokeShape, textFill, textStroke);

            // The glyph-indexed run shows Private Use code points; the text a reader selects or
            // searches is the document's, in the same zero-opacity overlay the outline path writes.
            if (glyphIndexed && !string.IsNullOrEmpty(text)) EmitInvisibleTextOverlay(text, _gs.Tm);

            // Advance Tm by total PDF-derived width so downstream text ops
            // line up.  In vertical writing mode the advance is along -Y per
            // PDF §9.4.4: each glyph moves the text matrix downward.
            float[] adv = wMode == 1
                ? new[] { 1f, 0f, 0f, 1f, 0f, -verticalAdvance }
                : new[] { 1f, 0f, 0f, 1f, totalAdvance, 0f };
            _gs.Tm = Compose(adv, _gs.Tm);
        }

        /// <summary>
        /// The downward advance of a vertical run, without building any placement — for the
        /// paths that must move the pen but draw nothing (text render mode 3).
        /// </summary>
        private float VerticalRunAdvance(FontCache fc, byte[] bytes)
        {
            var codes = GlyphIdResolver.DecodeCodes(fc, _gs.FontName, _resources, _doc, bytes);
            float total = 0f;
            for (int i = 0; i < codes.Count; i++)
                total += fc.GetVerticalMetrics(_gs.FontName, codes[i].Cid, _resources, _doc, 1f)
                             .Step(_gs.FontSize, _gs.CharSpacing, _gs.WordSpacing, codes[i].IsWordSpaceCode);
            return total;
        }

        /// <summary>
        /// Positions every glyph of a <b>vertical</b> run and returns the run's total downward
        /// advance.
        ///
        /// <para>The advance is the sum of <see cref="VerticalGlyphMetrics.Step"/> over the run's
        /// <b>glyphs</b>, each <c>w1y</c> from <c>/W2</c> or <c>/DW2</c> (§9.7.4.3 Table 117).
        /// <b>Not <c>bytes.Length * FontSize</c></b>, where two errors would compound: the
        /// length is a BYTE count, so an Identity-V CJK run — two bytes per glyph, the normal
        /// case — would advance exactly twice as far as it should; and a fixed one em ignores
        /// <c>/DW2</c> and <c>/W2</c> entirely.</para>
        ///
        /// <para><b>What this adds is the position vector <c>v</c>.</b> §9.7.4.3 draws a
        /// vertical glyph at <c>currentPoint − v</c>, where <c>v</c> maps the glyph's
        /// horizontal origin onto its vertical one: it centres a CJK glyph on the column (its
        /// x half-width) and drops it to the vertical baseline (<c>/DW2[0]</c>, default 880).
        /// Only <c>w1y</c> was read, so the pen advanced correctly and every glyph was then
        /// drawn on its HORIZONTAL origin — offset up and to the side of its column. The raster
        /// path applies it in <c>ContentStreamInterpreter</c>'s vertical branch, which renders
        /// the run glyph by glyph for exactly this reason.</para>
        ///
        /// <para>The SVG <c>&lt;text&gt;</c> path emits a whole run as ONE element, so the
        /// per-glyph shift becomes an <c>x</c>/<c>y</c> attribute <i>list</i> — one coordinate
        /// pair per addressable character, which is SVG's own mechanism for exactly this and
        /// keeps the run a single selectable, searchable element. The coordinates are in the
        /// element's own space, so each glyph's text-space target is mapped to page space
        /// through <c>Tm ∘ CTM</c> and then back through the inverse of the matrix the element
        /// carries. Going via page space is what makes it independent of the shape of
        /// <c>glyphScale</c> and of the Y-flip that matrix applies.</para>
        ///
        /// <para>Returns a <c>null</c> placement — leaving the run at one origin, as before —
        /// when the character count and the glyph count disagree (a <c>/ToUnicode</c> mapping a
        /// code to a string, say: the lists would silently desynchronise) or when the element
        /// matrix is singular. Both are reported. The advance is always returned.</para>
        /// </summary>
        private (string placement, float totalAdvance) BuildVerticalPlacement(
            FontCache fc, byte[] bytes, string text, float[] elementMatrix, float[] tmCtm)
        {
            var codes = GlyphIdResolver.DecodeCodes(fc, _gs.FontName, _resources, _doc, bytes);
            if (codes.Count == 0) return (null, 0f);

            var metrics = new VerticalGlyphMetrics[codes.Count];
            float total = 0f;
            for (int i = 0; i < codes.Count; i++)
            {
                metrics[i] = fc.GetVerticalMetrics(
                    _gs.FontName, codes[i].Cid, _resources, _doc, 1f);
                total += metrics[i].Step(_gs.FontSize, _gs.CharSpacing, _gs.WordSpacing, codes[i].IsWordSpaceCode);
            }

            // Characters are code points, not UTF-16 units: a glyph-indexed run is written in
            // Plane 15, where every character is a surrogate pair, and SVG's x/y lists count
            // characters as a browser does.
            int characters = CodePointCount(text);
            float[] inverse = PdfMatrix.Invert(elementMatrix);
            if (inverse == null || text == null || characters != codes.Count)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.RenderParameterIgnored,
                    $"SVG: vertical run of {codes.Count} glyph(s) decoded to " +
                    $"{characters} character(s)" +
                    (inverse == null ? " (and its text matrix is singular)" : "") +
                    "; the §9.7.4.3 position vector was not applied and the glyphs are drawn " +
                    "on their horizontal origins.");
                return (null, total);
            }

            var xs = new StringBuilder();
            var ys = new StringBuilder();
            float advanced = 0f;
            for (int i = 0; i < codes.Count; i++)
            {
                // currentPoint − v, in TEXT space. TextRise is where glyphScale puts the
                // origin, so it is the baseline this offsets from.
                float tsx = -metrics[i].PositionXEm * _gs.FontSize;
                float tsy = _gs.TextRise - advanced - metrics[i].PositionYEm * _gs.FontSize;

                var (px, py) = TxPtWith(tmCtm, tsx, tsy);
                var (lx, ly) = TxPtWith(inverse, px, py);

                if (i > 0) { xs.Append(' '); ys.Append(' '); }
                xs.Append(F(lx));
                ys.Append(F(ly));

                advanced += metrics[i].Step(_gs.FontSize, _gs.CharSpacing, _gs.WordSpacing, codes[i].IsWordSpaceCode);
            }

            return ($" x=\"{xs}\" y=\"{ys}\"", total);
        }

        /// <summary>Code points in <paramref name="s"/>: a surrogate pair is one.</summary>
        private static int CodePointCount(string s)
        {
            if (s == null) return 0;
            int n = 0;
            for (int i = 0; i < s.Length; i++)
                if (!char.IsLowSurrogate(s[i])) n++;
            return n;
        }

        /// <summary>
        /// The run's glyphs as the code points a glyph-indexed program's <c>cmap</c> maps them from:
        /// <see cref="CffOtfWrapper.GlyphIndexedCmapBase"/> + glyph, one per decoded code — a
        /// converted Type 1 program's glyph through its name, a CFF's through the glyph source's
        /// code-to-glyph map, so the viewer draws exactly the glyph the raster renderer does.
        /// </summary>
        private string GlyphIndexedText(FontCache fc, byte[] bytes, in SvgFontEmbedder.Program program)
        {
            var codes = GlyphIdResolver.DecodeCodes(fc, _gs.FontName, _resources, _doc, bytes);
            CffGlyphSource cff = fc.GetGlyphSource(_gs.FontName, _resources, _doc);
            Type1GlyphSource type1 = fc.GetType1GlyphSource(_gs.FontName, _resources, _doc);

            var sb = new StringBuilder(codes.Count * 2);
            foreach (CMapCodec.Code code in codes)
            {
                int gid = 0;
                if (program.GlyphNameToGid != null)
                {
                    string name = type1?.GetGlyphName(code.Cid);
                    if (name == null || !program.GlyphNameToGid.TryGetValue(name, out gid)) gid = 0;
                }
                else if (cff != null)
                {
                    gid = cff.GetGid(code.Cid);
                }
                else
                {
                    gid = code.Cid;   // a CID-keyed program with no source: CID is the best answer left
                }

                if (gid < 0 || gid > CffOtfWrapper.GlyphIndexedCmapMaxGlyph) gid = 0;
                sb.Append(char.ConvertFromUtf32(CffOtfWrapper.GlyphIndexedCmapBase + gid));
            }
            return sb.ToString();
        }

        // Sums per-character advances from the PDF /W table (fallback: DefaultGlyphWidth).
        // Output is already in page points, with CharSpacing, WordSpacing, and
        // HorizScale applied — matching the raster renderer's totalPdfAdvance.
        private float ComputePdfAdvance(FontCache fc, byte[] bytes)
        {
            var pdfFont = fc.GetPdfFont(_gs.FontName, _resources, _doc);
            var codes = GlyphIdResolver.DecodeCodes(fc, _gs.FontName, _resources, _doc, bytes);

            float total = 0f;
            for (int i = 0; i < codes.Count; i++)
            {
                int cid = codes[i].Cid;
                double w = GlyphWidth.Units(pdfFont, cid);
                float advPts = (float)(w / PdfUnits.GlyphSpaceUnitsPerEm) * _gs.FontSize;

                float wordAdv = codes[i].IsWordSpaceCode ? _gs.WordSpacing : 0f;
                total += (advPts + _gs.CharSpacing + wordAdv) * _gs.HorizScale;
            }
            return total;
        }

        // ---- Vector glyph outlines (embedded fonts) ----

        /// <summary>
        /// Attempts to render the text run as vector glyph &lt;path&gt; outlines (the actual
        /// embedded glyph shapes, identical to the raster renderer) plus an invisible
        /// &lt;text&gt; overlay for selection/search.  Returns <c>false</c> WITHOUT mutating
        /// graphics state when the font is not outlineable here (non-embedded base-14 /
        /// system-substitute fonts — which the SVG viewer already has, so they keep the plain
        /// &lt;text&gt; path).  When it returns <c>true</c> it has emitted all output and
        /// advanced the text matrix exactly as the raster renderer does.
        /// </summary>
        private bool TryEmitGlyphOutlines(FontCache fc, byte[] bytes, string text,
                                          TextRenderMode paintMode, bool addToClip)
        {
            if (Depth > PdfLimits.MaxRenderRecursionDepth) return false;

            var glyphSource = fc.GetGlyphSource(_gs.FontName, _resources, _doc);
            var type1Source = fc.GetType1GlyphSource(_gs.FontName, _resources, _doc);
            bool isOtfCff   = glyphSource != null && fc.IsOtfCff(_gs.FontName, _resources, _doc);

            // Split via the font's /Encoding CMap (see FontCache.GetEncodingCodec) so the SVG
            // path selects exactly the same glyphs the raster path does.
            var codes = GlyphIdResolver.DecodeCodes(fc, _gs.FontName, _resources, _doc, bytes);
            if (codes.Count == 0) return false;

            // A program whose glyphs our evaluator finds heavy is not handed to Skia through its
            // OTF wrapper, which would run them under no budget of ours (CffGlyphSource) — the
            // raster path's rule, so a subroutine bomb costs the export what it costs the render.
            if (isOtfCff && glyphSource.IsTooHeavyForHinting(codes)) isOtfCff = false;

            // Select the outline source, mirroring ContentStreamInterpreter.DrawTextBytes:
            //   0 = Type 1 charstring source · 1 = CFF charstring source
            //   2 = OTF-wrapped CFF typeface  · 3 = embedded TrueType/OTF typeface
            //   4 = our evaluator over a CFF program Skia loaded itself, found too heavy for it
            SKTypeface typeface = null;
            CffGlyphSource probe = null;
            ushort[] probeIds = null;
            int srcMode;
            if (type1Source != null) srcMode = 0;
            else if (glyphSource != null && !isOtfCff) srcMode = 1;
            else if (isOtfCff)
            {
                typeface = fc.GetTypeface(_gs.FontName, _resources, _doc);
                if (typeface == null) return false;
                srcMode = 2;
            }
            else
            {
                // Standard typeface path — outline only embedded programs the viewer lacks.
                if (!fc.IsEmbedded(_gs.FontName, _resources, _doc)) return false;
                if (fc.IsSystemFallbackFont(_gs.FontName, _resources, _doc)) return false;
                typeface = fc.GetTypeface(_gs.FontName, _resources, _doc);
                if (typeface == null) return false;
                srcMode = 3;

                // An OpenType CFF program Skia loaded itself: the glyph ids it would draw are asked
                // of our evaluator first, and a heavy program is drawn from our outlines (F15).
                probe = fc.GetHintingProbe(_gs.FontName, _resources, _doc);
                if (probe != null)
                {
                    probeIds = GlyphIdResolver.Resolve(fc, _gs.FontName, _resources, _doc, codes, text, typeface);
                    if (probe.IsTooHeavyForHinting(probeIds)) srcMode = 4;
                    else probe = null;
                }
            }

            // Committed: from here we always emit output, advance Tm, and return true.
            float[] tmStart = CopyArr(_gs.Tm);

            // Scale of the glyph→page matrix that every <use> in this run carries. Constant
            // across the run: Tm only translates between glyphs, so only the font size, the
            // outline space's own scale and the CTM contribute. It is what stroke-width has to
            // be divided by, because the attribute resolves in the transformed element's user
            // space — a charstring outline is in 1/1000 em, so an undivided page width came out
            // roughly eighty times too thin and stroked text was effectively invisible.
            float outlineScale = srcMode switch
            {
                0 => type1Source.FontMatrixScale,
                1 => glyphSource.FontMatrixScale,
                4 => probe.FontMatrixScale,
                _ => 1f,   // typeface outlines are already in em units
            };
            if ((srcMode <= 1 || srcMode == 4) && (!(outlineScale > 0f)
                                 || float.IsNaN(outlineScale) || float.IsInfinity(outlineScale)))
                outlineScale = PdfUnits.GlyphSpaceScale;   // same sanitisation the emitters use
            float glyphMatrixScale = CtmScale(Compose(
                Compose(new float[] { _gs.FontSize * _gs.HorizScale, 0, 0, _gs.FontSize, 0, _gs.TextRise }, tmStart),
                _gs.CTM)) * outlineScale;
            if (!(glyphMatrixScale > 0f)) glyphMatrixScale = 1f;

            bool fill    = paintMode.Fills();
            bool stroke  = paintMode.Strokes();
            bool visible = !paintMode.IsInvisible();  // mode 7 (clip-only) paints nothing
            bool clipMode = addToClip;               // render modes 4-7

            var glyphSb = new StringBuilder();    // visible outline <path> elements
            StringBuilder clipSb = null;
            if (clipMode) { _textClipAccum ??= new StringBuilder(); clipSb = _textClipAccum; }
            StringBuilder vis = visible ? glyphSb : null;

            switch (srcMode)
            {
                case 3: // embedded TrueType / OTF typeface — GIDs via shared resolver, PDF /W advances.
                {
                    ushort[] gids = probeIds ?? GlyphIdResolver.Resolve(
                        fc, _gs.FontName, _resources, _doc, codes, text, typeface);
                    var pdfFont = fc.GetPdfFont(_gs.FontName, _resources, _doc);
                    using var font = new SKFont(typeface, 1f);
                    for (int gi = 0; gi < codes.Count; gi++)
                    {
                        if (gi < gids.Length)
                            AppendTypefaceGlyphOutline(vis, clipSb, font, gids[gi]);
                        int cid = codes[gi].Cid;
                        double w = GlyphWidth.Units(pdfFont, cid);
                        AdvanceTm((float)(w / PdfUnits.GlyphSpaceUnitsPerEm) * _gs.FontSize, codes[gi]);
                    }
                    break;
                }
                case 4: // a heavy OpenType CFF program: our outlines by the ids Skia would draw, PDF /W advances.
                {
                    var pdfFont = fc.GetPdfFont(_gs.FontName, _resources, _doc);
                    for (int gi = 0; gi < codes.Count; gi++)
                    {
                        var o = gi < probeIds.Length ? probe.GetOutlineForGid(probeIds[gi]) : null;
                        AppendCharspaceGlyphOutline(vis, clipSb, o.HasValue ? o.Value.Path : null, probe.FontMatrixScale);
                        double w = GlyphWidth.Units(pdfFont, codes[gi].Cid);
                        AdvanceTm((float)(w / PdfUnits.GlyphSpaceUnitsPerEm) * _gs.FontSize, codes[gi]);
                    }
                    break;
                }
                case 2: // OTF-wrapped CFF: GID from the CFF source, outline via the typeface.
                {
                    using var font = new SKFont(typeface, 1f);
                    for (int gi = 0; gi < codes.Count; gi++)
                    {
                        int cid = codes[gi].Cid;
                        AppendTypefaceGlyphOutline(vis, clipSb, font, glyphSource.GetGid(cid));
                        AdvanceTm(SanitizeAdv(glyphSource.GetAdvanceWidth(cid) * _gs.FontSize
                                                  / (float)PdfUnits.GlyphSpaceUnitsPerEm,
                                              _gs.FontSize), codes[gi]);
                    }
                    break;
                }
                default: // 0 = Type 1, 1 = CFF — direct charstring outlines (Y-up glyph space).
                {
                    for (int gi = 0; gi < codes.Count; gi++)
                    {
                        int cid = codes[gi].Cid;
                        SKPath outline;
                        float pathScale, advRaw;
                        if (srcMode == 0)
                        {
                            var o = type1Source.GetOutline(cid);
                            outline   = o.HasValue ? o.Value.Path : null;
                            pathScale = type1Source.FontMatrixScale;
                            advRaw    = type1Source.GetAdvanceWidth(cid);
                        }
                        else
                        {
                            var o = glyphSource.GetOutline(cid);
                            if ((!o.HasValue || o.Value.Path == null || o.Value.Path.IsEmpty)
                                && gi < text.Length)
                            {
                                // Latin-ligature component fallback (matches DrawCffRun).
                                int comp = GlyphIdResolver.LigatureFirstComponentCode(text[gi]);
                                if (comp > 0)
                                {
                                    var alt = glyphSource.GetOutline(comp);
                                    if (alt.HasValue && alt.Value.Path != null && !alt.Value.Path.IsEmpty)
                                        o = alt;
                                }
                            }
                            outline   = o.HasValue ? o.Value.Path : null;
                            pathScale = glyphSource.FontMatrixScale;
                            advRaw    = glyphSource.GetAdvanceWidth(cid);
                        }
                        AppendCharspaceGlyphOutline(vis, clipSb, outline, pathScale);
                        AdvanceTm(SanitizeAdv(advRaw * _gs.FontSize / (float)PdfUnits.GlyphSpaceUnitsPerEm,
                                              _gs.FontSize), codes[gi]);
                    }
                    break;
                }
            }

            if (clipMode) _textClipUsed = true;

            // Emit the visible outline group with the run's fill / stroke attributes.
            if (visible && glyphSb.Length > 0)
            {
                string outlinePattern = SelectedPatternName(stroke: false);
                // Divided by the glyph→page scale the <use> children carry — see glyphMatrixScale
                // above. The page-space width itself is the CTM-scaled §8.4.3.2 value, exactly as
                // for a path stroke; the geometry is divided by the same matrix (on a charstring
                // outline it includes the 1/1000 em, so an undivided dash array would be about a
                // thousand times too long).
                string strokeShape = stroke
                    ? $" stroke-width=\"{F(StrokeWidthOrHairline(_gs.LineWidth, CtmScale(_gs.CTM)) / glyphMatrixScale)}\""
                      + StrokeGeometryAttributes(glyphMatrixScale)
                    : "";
                string glyphs = glyphSb.ToString();
                if (fill && outlinePattern != null)
                {
                    // A pattern fill is painted through the glyphs as a clip, as the raster paints
                    // it through their captured outlines: a userSpaceOnUse paint server resolves in
                    // the painted element's own space, which for a <use> is the glyph's, so one
                    // fill on the group would scale and flip the pattern by each glyph's matrix.
                    string patternPaint = ResolvePatternPaintOrNone(outlinePattern, "glyph-outline fill");
                    string clipId = "gclip" + NextId();
                    _sb.Append($"<defs><clipPath id=\"{clipId}\">{glyphs}</clipPath></defs>\n");
                    float pageX = Shared?.PageX ?? 0f, pageY = Shared?.PageY ?? 0f;
                    EmitTextPaint(attrs => $"<rect x=\"{F(pageX)}\" y=\"{F(pageY)}\" width=\"{F(_pageW)}\" height=\"{F(_pageH)}\"" +
                                           $" clip-path=\"url(#{clipId})\"{attrs}/>",
                        () => TextFillAttributes(true, patternPaint), () => " stroke=\"none\"", "", true, false);
                    if (stroke)
                        EmitTextPaint(attrs => $"<g{attrs}>{glyphs}</g>", () => " fill=\"none\"",
                            () => $" stroke=\"{StrokePaint}\"{strokeShape}{TextStrokeOpacity()}", strokeShape, false, true);
                }
                else
                {
                    string fillVal = fill ? FillPaint : null;
                    EmitTextPaint(attrs => $"<g{attrs}>{glyphs}</g>",
                        () => TextFillAttributes(fill, fillVal),
                        () => stroke ? $" stroke=\"{StrokePaint}\"{strokeShape}{TextStrokeOpacity()}" : " stroke=\"none\"",
                        strokeShape, fill, stroke);
                }
            }

            // Invisible, selectable/searchable text overlay (uses the run-start matrix).
            if (visible && text.Length > 0)
                EmitInvisibleTextOverlay(text, tmStart);

            return true;
        }

        // Advances Tm by one glyph's horizontal advance (text-space points), applying
        // CharSpacing, WordSpacing and HorizScale — matching the raster renderer's step.
        // Tw is added only for the single-byte code 32 (PDF §9.3.3); see
        // ContentStreamInterpreter.AdvanceTm for why a 2-byte 0x0020 test would be wrong.
        private void AdvanceTm(float advPts, CMapCodec.Code code)
        {
            float wordAdv = code.IsWordSpaceCode ? _gs.WordSpacing : 0f;
            float tx = (advPts + _gs.CharSpacing + wordAdv) * _gs.HorizScale;
            _gs.Tm = Compose(new float[] { 1, 0, 0, 1, tx, 0 }, _gs.Tm);
        }

        // Emits a CFF / Type 1 glyph outline (glyph-space units, Y-up). The outline is serialized
        // once in its own (untransformed) glyph space and referenced via <use>; the per-glyph
        // glyph→page matrix (PdfRenderContext.DrawGlyphPath's charspace→page transform, minus the
        // canvas Y-flip which the page-level group performs) rides on the <use>.
        private void AppendCharspaceGlyphOutline(StringBuilder visibleDst, StringBuilder clipDst,
                                                 SKPath glyphPath, float pathScale)
        {
            if (glyphPath == null || glyphPath.IsEmpty) return;
            if (!(pathScale > 0f) || float.IsNaN(pathScale) || float.IsInfinity(pathScale))
                pathScale = PdfUnits.GlyphSpaceScale;

            // §9.4.4: the horizontal scaling Th in the `a` slot stretches the glyph, as
            // AppendTypefaceGlyphOutline and the raster's glyph matrix put it; without it an
            // outlined glyph drew at full width on a pitch Tz had scaled.
            float[] trm = Compose(
                Compose(new float[] { _gs.FontSize * _gs.HorizScale, 0, 0, _gs.FontSize, 0, _gs.TextRise }, _gs.Tm),
                _gs.CTM);
            float[] charToPage = Compose(new float[] { pathScale, 0, 0, pathScale, 0, 0 }, trm);

            AppendGlyphUse(visibleDst, clipDst, SkPathToSvgData(glyphPath), charToPage);
        }

        // Emits a typeface glyph outline (SkiaSharp glyph space, em units, Y-down). The outline is
        // serialized once in em space; the per-glyph matrix (which Y-flips Skia's Y-down glyph
        // space to PDF Y-up and scales by font size through Tm·CTM) rides on the <use>.
        private void AppendTypefaceGlyphOutline(StringBuilder visibleDst, StringBuilder clipDst,
                                                SKFont font, ushort gid)
        {
            using var gp = font.GetGlyphPath(gid);
            if (gp == null || gp.IsEmpty) return;

            // §9.4.4: the horizontal scaling Th belongs in the `a` slot, so it stretches the
            // GLYPH — matching ContentStreamInterpreter's glyphScaleMat and the two other
            // text-matrix sites in this file — the one SVG path that builds its own matrix — or an
            // embedded font under `50 Tz` would advance at half width while every outline drew at
            // full width.
            float[] emToPage = Compose(
                Compose(new float[]
                { _gs.FontSize * _gs.HorizScale, 0, 0, _gs.FontSize, 0, _gs.TextRise }, _gs.Tm),
                _gs.CTM);
            float[] m = Compose(new float[] { 1, 0, 0, -1, 0, 0 }, emToPage); // Y-down em → Y-up

            AppendGlyphUse(visibleDst, clipDst, SkPathToSvgData(gp), m);
        }

        // Registers a glyph outline (in its own space) as a shared <defs><path> the first time it
        // is seen, then appends a <use> with the per-glyph page matrix to the visible and/or clip
        // targets — so each distinct glyph shape is serialized once no matter how often it recurs.
        private void AppendGlyphUse(StringBuilder visibleDst, StringBuilder clipDst,
                                    string unitPathData, float[] matrix)
        {
            if (string.IsNullOrEmpty(unitPathData)) return;
            string mat = $"matrix({F(matrix[0])},{F(matrix[1])},{F(matrix[2])}," +
                         $"{F(matrix[3])},{F(matrix[4])},{F(matrix[5])})";

            if (Shared != null)
            {
                if (!Shared.GlyphDefs.TryGetValue(unitPathData, out string id))
                {
                    id = "g" + (++Shared.IdCounter);
                    Shared.GlyphDefs[unitPathData] = id;
                    _sb.Append("<defs><path id=\"").Append(id).Append("\" d=\"")
                       .Append(unitPathData).Append("\"/></defs>\n");
                }
                string use = $"<use xlink:href=\"#{id}\" transform=\"{mat}\"/>";
                visibleDst?.Append(use);
                clipDst?.Append(use);
                return;
            }

            // No shared registry (defensive): emit the transformed outline inline.
            string el = $"<path transform=\"{mat}\" d=\"{unitPathData}\"/>";
            visibleDst?.Append(el);
            clipDst?.Append(el);
        }

        /// <summary>The CSS font selectors for the run being drawn.</summary>
        /// <remarks>
        /// Memoised per resource name because a page draws thousands of runs from a handful of
        /// fonts, and the answer depends on nothing else: the descriptor and the base font are
        /// properties of the resource, not of the run.
        /// </remarks>
        private SvgFontFace FontFaceOf()
        {
            string name = _gs.FontName;
            if (name != null && _fontFaces.TryGetValue(name, out SvgFontFace cached)) return cached;

            PdfFont font = Fonts.GetPdfFont(name, _resources, _doc);
            PdfDictionary descriptor = Fonts.GetFontDescriptor(name, _resources, _doc);

            SvgFontFace face = SvgFontFace.Describe(font?.BaseFont, descriptor, _doc)
                .WithEmbeddedFamily(EmbeddedFamilyFor(name, descriptor));

            if (name != null) _fontFaces[name] = face;
            return face;
        }

        /// <summary>
        /// The CSS family of an <c>@font-face</c> carrying this resource's own embedded program,
        /// emitting the rule the first time the page needs it; null when there is nothing to embed.
        /// </summary>
        /// <remarks>
        /// <para><b>Reaching here at all means outlining declined.</b> An embedded font is normally
        /// drawn as vector outlines and never sees this method. What is left is a vertical
        /// writing-mode run and a program SkiaSharp would not load — and in the second case the
        /// document still holds bytes a browser may well read, which is strictly more than the
        /// family name alone.</para>
        /// <para>The rule goes in a <c>&lt;defs&gt;&lt;style&gt;</c> emitted inline, like every
        /// other def this writer mints. CSS is not positional, so a rule written next to its first
        /// use applies to the whole document.</para>
        /// </remarks>
        private string EmbeddedFamilyFor(string resourceName, PdfDictionary descriptor)
        {
            if (descriptor == null || _doc == null) return null;

            SvgFontEmbedder.Program program;
            if (Shared == null || !Shared.FontPrograms.TryGetValue(descriptor, out program))
            {
                program = SvgFontEmbedder.TryRead(descriptor, _doc, resourceName);
                if (Shared != null) Shared.FontPrograms[descriptor] = program;
            }
            if (!program.IsUsable) return null;
            if (program.GlyphIndexed && resourceName != null) _glyphIndexedPrograms[resourceName] = program;

            // Per PAGE, not per writer: a form XObject or annotation appearance is a child writer,
            // and a megabyte of base64 re-emitted once per child is how an export stops opening.
            // A writer with no registry (the defensive case) keeps its own, exactly as NextId does.
            if (Shared != null && Shared.FontFaceDefs.TryGetValue(program.ObjectNumber,
                                                                 out string existing))
                return existing;

            string family = "pdffont" + NextId().ToString(CultureInfo.InvariantCulture);
            Shared?.FontFaceDefs.Add(program.ObjectNumber, family);

            _sb.Append("<defs><style type=\"text/css\">")
               .Append(XmlEsc(SvgFontEmbedder.FaceRule(family, program)))
               .Append("</style></defs>\n");

            return family;
        }

        private readonly Dictionary<string, SvgFontFace> _fontFaces =
            new Dictionary<string, SvgFontFace>(StringComparer.Ordinal);

        /// <summary>
        /// Resource name → the glyph-indexed program its <c>@font-face</c> carries, for the runs
        /// that must be written as glyph code points. Filled by <see cref="EmbeddedFamilyFor"/>.
        /// </summary>
        private readonly Dictionary<string, SvgFontEmbedder.Program> _glyphIndexedPrograms =
            new Dictionary<string, SvgFontEmbedder.Program>(StringComparer.Ordinal);

        // Emits a zero-opacity <text> element so SVG viewers can select / search the text that
        // the vector outlines drew.  Uses the same matrix the plain <text> path uses (font size
        // baked in; the inner d component is negated to undo the page-level Y-flip for glyphs).
        private void EmitInvisibleTextOverlay(string text, float[] tm)
        {
            float[] glyphScale = { _gs.FontSize, 0, 0, _gs.FontSize, 0, _gs.TextRise };
            float[] fullM = Compose(Compose(glyphScale, tm), _gs.CTM);
            string matrix = $"matrix({F(fullM[0])},{F(fullM[1])},{F(fullM[2])}," +
                            $"{F(-fullM[3])},{F(fullM[4])},{F(fullM[5])})";
            _sb.Append("<text transform=\"").Append(matrix)
               .Append("\" font-size=\"1\" fill=\"#000\" fill-opacity=\"0\">")
               .Append(XmlEsc(text)).Append("</text>\n");
        }

        // Serialises an SKPath (already transformed into PDF page space) to SVG path data.
        // Uses the raw iterator so the original curve verbs are preserved. Conics (rare in
        // glyph outlines) are approximated by a single quadratic.
        private static string SkPathToSvgData(SKPath path)
        {
            if (path == null || path.IsEmpty) return string.Empty;
            var sb = new StringBuilder();
            var pts = new SKPoint[4];
            using var it = path.CreateRawIterator();
            SKPathVerb verb;
            while ((verb = it.Next(pts)) != SKPathVerb.Done)
            {
                switch (verb)
                {
                    case SKPathVerb.Move:
                        sb.Append('M').Append(F(pts[0].X)).Append(',').Append(F(pts[0].Y)).Append(' ');
                        break;
                    case SKPathVerb.Line:
                        sb.Append('L').Append(F(pts[1].X)).Append(',').Append(F(pts[1].Y)).Append(' ');
                        break;
                    case SKPathVerb.Quad:
                    case SKPathVerb.Conic:
                        sb.Append('Q').Append(F(pts[1].X)).Append(',').Append(F(pts[1].Y)).Append(' ')
                          .Append(F(pts[2].X)).Append(',').Append(F(pts[2].Y)).Append(' ');
                        break;
                    case SKPathVerb.Cubic:
                        sb.Append('C').Append(F(pts[1].X)).Append(',').Append(F(pts[1].Y)).Append(' ')
                          .Append(F(pts[2].X)).Append(',').Append(F(pts[2].Y)).Append(' ')
                          .Append(F(pts[3].X)).Append(',').Append(F(pts[3].Y)).Append(' ');
                        break;
                    case SKPathVerb.Close:
                        sb.Append('Z');
                        break;
                }
            }
            return sb.ToString();
        }

        // The half-em fallback for a non-finite advance — one implementation in
        // MatrixGuard.SanitizeAdvance, shared with the raster interpreter. This copy wrote the
        // finiteness predicate out twice rather than calling MatrixGuard.IsFinite as the raster
        // copy already did, so the two could drift on the predicate and on the fallback separately.
        private static float SanitizeAdv(float advancePts, float fontSize)
            => MatrixGuard.SanitizeAdvance(advancePts, fontSize);

        // ---- XObjects ----
    }
}
