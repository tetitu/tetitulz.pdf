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
        // Depth of the knockout layer opened for the current text object (PDF §9.3.8), so an
        // unbalanced BT cannot leave a canvas save behind. A text object cannot nest — q/Q, Do
        // and BT are all outside the text-object operator category — so this is a flag, not a
        // stack; it is an int only so ET and the end-of-stream sweep can share one close.
        private bool _textKnockoutLayerOpen;

        private void BeginText()
        {
            // Reset text matrices per spec (BT).
            _ctx.GS.Tm  = new float[] { 1, 0, 0, 1, 0, 0 };
            _ctx.GS.Tlm = new float[] { 1, 0, 0, 1, 0, 0 };
            // Initialise text-clip accumulator (PDF §9.3.6).
            _ctx.BeginTextClip();
            BeginTextKnockout();
        }

        private void EndText()
        {
            // ORDER IS LOAD-BEARING. The knockout layer closes FIRST, then the text clip is
            // applied. EndTextClip calls SKCanvas.ClipPath, which modifies the canvas state that
            // is current when it runs — and closing the layer is an SKCanvas.Restore, which would
            // pop that clip straight back off again. Applying the clip first therefore silently
            // discarded it for every text object that opened a layer, i.e. exactly the translucent
            // and blended ones: render modes 4-7 would have painted their glyphs and then failed
            // to clip anything, letting whatever follows ET (commonly a gradient meant to show
            // through the letters) cover the page. The clip belongs to the state AFTER the text
            // object, which is the outer one.
            EndTextKnockout();
            // Flush accumulated text-clip path (modes 4-7) into graphics state clip.
            _ctx.EndTextClip();
        }

        /// <summary>
        /// Opens the knockout group that <c>/TK true</c> asks for (PDF §9.3.8), when the graphics
        /// state makes it observable.
        /// </summary>
        /// <remarks>
        /// <para><b>The default is the deviating case, which is why this is not an opt-in.</b>
        /// <c>/TK</c> defaults to <b>true</b> (Table 52), meaning the glyphs of one text object
        /// composite as a knockout group: where two glyphs overlap the later one <i>replaces</i>
        /// the earlier rather than painting over it. Painting each glyph independently — what this
        /// renderer did unconditionally — is the <c>/TK false</c> model, so every text object with
        /// overlapping glyphs and <c>ca &lt; 1</c> rendered its overlaps too dark, on files that
        /// say nothing about <c>/TK</c> at all.</para>
        /// <para><b>Only when it can be seen.</b> With opaque fill, no soft mask and Normal
        /// blending the two models are pixel-identical — an opaque glyph over an opaque glyph is
        /// the same colour either way — so no layer is opened for ordinary text, and the common
        /// page pays nothing. That matters: the layer is unbounded (the extent of a text object is
        /// not known at <c>BT</c>), and §13b records what page-sized work per operation costs.</para>
        /// <para><b>Alpha stays on the elements, not on the layer.</b> The group is opened at
        /// α = 1 and each glyph keeps its own <c>ca</c>/<c>CA</c>, painting with
        /// <see cref="SKBlendMode.Src"/> through the knockout stack. Baking the alpha into the
        /// layer instead would be wrong the moment a <c>gs</c> inside the text object changes it —
        /// legal, since <c>gs</c> <i>is</i> in the text-object operator category — because the new
        /// alpha would then be multiplied by the one already captured at <c>BT</c>.</para>
        /// </remarks>
        private void BeginTextKnockout()
        {
            if (_textKnockoutLayerOpen) return;   // unbalanced BT BT; the first one owns the layer
            if (!_ctx.GS.TextKnockout) return;

            bool observable = _ctx.GS.FillAlpha   < 1f - OpacityEpsilon
                           || _ctx.GS.StrokeAlpha < 1f - OpacityEpsilon
                           || _ctx.GS.ActiveSoftMask != null
                           || (_ctx.GS.BlendMode != null
                               && _ctx.GS.BlendMode != PdfBlendModes.Normal
                               && _ctx.GS.BlendMode != PdfBlendModes.Compatible);
            if (!observable) return;

            // Isolated: §9.3.8 makes the text object a knockout group, and the elements inside it
            // are knocked out against a transparent initial backdrop — which is the precondition
            // PdfRenderContext.ElementBlendMode needs to express knockout as a blend mode.
            _ctx.BeginTransparencyGroup(1f, _ctx.GS.BlendMode, isolated: true, knockout: true);
            _textKnockoutLayerOpen = true;
        }

        /// <summary>Closes the layer <see cref="BeginTextKnockout"/> opened, if any.</summary>
        private void EndTextKnockout()
        {
            if (!_textKnockoutLayerOpen) return;
            _textKnockoutLayerOpen = false;
            _ctx.EndTransparencyGroup(null);
        }

        private void OpTf(ContentStreamOperation op)
        {
            if (op.Operands.Length < 2) return;
            _ctx.GS.FontResourceName = GetName(op, 0);
            _ctx.GS.FontSize         = GetFloat(op, 1);
        }

        private void OpTd(ContentStreamOperation op)
        {
            float tx = GetFloat(op, 0), ty = GetFloat(op, 1);
            // A NaN/Inf offset would poison Tlm/Tm and make every later glyph in the
            // text object render at NaN coordinates (silently invisible). Ignore it.
            if (!IsFiniteF(tx) || !IsFiniteF(ty))
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                    "Td ignored: non-finite offset operand");
                return;
            }
            // Tlm = [1 0 0 1 tx ty] * Tlm
            float[] move = { 1, 0, 0, 1, tx, ty };
            _ctx.GS.Tlm = GraphicsState.Compose(move, _ctx.GS.Tlm);
            _ctx.GS.Tm  = (float[])_ctx.GS.Tlm.Clone();
        }

        private void OpTD(ContentStreamOperation op)
        {
            // Only commit the leading if it's finite — a NaN/Inf leading would poison
            // T* for the rest of the text object. OpTd separately guards the move.
            float ty = GetFloat(op, 1);
            if (IsFiniteF(ty)) _ctx.GS.Leading = -ty;
            OpTd(op);
        }

        private void OpTm(ContentStreamOperation op)
        {
            if (op.Operands.Length < 6) return;
            float[] m =
            {
                GetFloat(op,0), GetFloat(op,1),
                GetFloat(op,2), GetFloat(op,3),
                GetFloat(op,4), GetFloat(op,5)
            };
            // A NaN/Inf operand would poison Tm/Tlm and silently blank the rest of the
            // text object. Ignore the bad operator (keep the previous matrix).
            if (!AllFinite(m))
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                    "Tm ignored: non-finite matrix operand");
                return;
            }
            _ctx.GS.Tm  = m;
            _ctx.GS.Tlm = (float[])m.Clone();
        }

        private void OpTStar()
        {
            // Move to start of next line.
            float[] move = { 1, 0, 0, 1, 0, -_ctx.GS.Leading };
            _ctx.GS.Tlm = GraphicsState.Compose(move, _ctx.GS.Tlm);
            _ctx.GS.Tm  = (float[])_ctx.GS.Tlm.Clone();
        }

        private void OpTj(ContentStreamOperation op)
        {
            if (op.Operands.Length < 1) return;
            byte[] bytes = GetStringBytes(op.Operands[0]);
            DrawTextBytes(bytes);
        }

        private void OpTJ(ContentStreamOperation op)
        {
            // TJ array: alternating strings and kerning numbers.
            if (op.Operands.Length < 1) return;
            var arr = op.Operands[0] as PdfArray;
            if (arr == null) return;

            bool vertical = _fontCache.GetWMode(_ctx.GS.FontResourceName, _resources, _doc) == 1;
            foreach (var item in arr)
            {
                if (item is PdfString ps)
                {
                    DrawTextBytes(ps.RawBytes);
                }
                else if (item is PdfNumber pn)
                {
                    // Thousandths of a unit of text space, along the writing direction — down in
                    // vertical writing (TextAdjustment, shared with the SVG writer).
                    float[] adv = TextAdjustment.Of(pn.DoubleValue, _ctx.GS.FontSize, _ctx.GS.HorizScaling, vertical);
                    _ctx.GS.Tm = GraphicsState.Compose(adv, _ctx.GS.Tm);
                }
            }
        }

        private void OpQuote(ContentStreamOperation op)
        {
            OpTStar();
            OpTj(op);
        }

        private void OpDoubleQuote(ContentStreamOperation op)
        {
            if (op.Operands.Length >= 3)
            {
                _ctx.GS.WordSpacing = GetFloat(op, 0);
                _ctx.GS.CharSpacing = GetFloat(op, 1);
                // Third operand is the string.
                var stringOp = new ContentStreamOperation(
                    PdfOperators.NextLineShowText, [op.Operands[2]]);
                OpQuote(stringOp);
            }
        }

        private void DrawTextBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return;

            // Type 3 fonts: each glyph is a mini content stream in /CharProcs.
            // Detect and delegate before attempting typeface loading.
            if (_fontCache.IsType3Font(_ctx.GS.FontResourceName, _resources, _doc))
            {
                DrawType3Bytes(bytes);
                return;
            }

            // PostScript text rendering modes: Invisible (3) is used for OCR overlay layers.
            // Modes 4-7 add glyphs to the clipping path (handled in DrawGlyphPath/DrawText).
            // We must still advance the text matrix even for invisible text.
            var renderMode = (TextRenderMode)_ctx.GS.TextRenderMode;
            bool invisible = renderMode.IsInvisible();

            var typeface     = invisible ? null : _fontCache.GetTypeface(_ctx.GS.FontResourceName, _resources, _doc);
            var glyphSource  = invisible ? null : _fontCache.GetGlyphSource(_ctx.GS.FontResourceName, _resources, _doc);
            var type1Source  = invisible ? null : _fontCache.GetType1GlyphSource(_ctx.GS.FontResourceName, _resources, _doc);
            bool isOtfCff    = glyphSource != null && !invisible
                               && _fontCache.IsOtfCff(_ctx.GS.FontResourceName, _resources, _doc);

            // Decode bytes to string using the font's encoding.
            string text = _fontCache.DecodeText(_ctx.GS.FontResourceName, bytes, _resources, _doc);

            // Split the string into character codes using the font's ENCODING CMap. A single
            // stride cannot represent a mixed-width CMap, and /ToUnicode is neither required nor
            // authoritative (see FontCache.GetEncodingCodec).
            var codes = GlyphIdResolver.DecodeCodes(
                _fontCache, _ctx.GS.FontResourceName, _resources, _doc, bytes);
            if (codes.Count == 0) return;

            // A program whose glyphs our evaluator finds heavy is not handed to Skia through
            // its OTF wrapper, which would run them under no budget of ours (CffGlyphSource).
            if (isOtfCff && glyphSource.IsTooHeavyForHinting(codes)) isOtfCff = false;

            // Pattern-filled text: when the fill colour is a shading/tiling Pattern and the
            // render mode paints a fill (0/2/4/6), the glyphs must be filled with the pattern,
            // not the stale solid FillColor.  Capture the run's glyph outlines (delegating to
            // the normal run methods, so advance/positioning is identical) and paint the
            // pattern clipped to those outlines via the existing RenderTilingPattern.
            bool fillMode = renderMode.Fills();
            string textFillPattern = SelectedPatternName(stroke: false);
            if (!invisible && fillMode &&
                textFillPattern != null && PatternResourceExists(textFillPattern))
            {
                DrawPatternTextRun(codes, typeface, glyphSource, type1Source, isOtfCff, text);
                return;
            }

            // Spot-coloured type, when a host has asked for ink coverage. Captured the same way the
            // two branches above capture, so advance and positioning stay identical to a normal run
            // — the outlines are what the deposit measures, and a second layout pass would be a
            // second chance to disagree with the one that paints.
            if (!invisible && fillMode && _ctx.InkAccumulator != null && _ctx.GS.FillSepTints != null)
            {
                // A mode that also strokes puts ink under the pen too, and the pen's colorants are
                // the stroking snapshot this branch does not carry. Say so rather than let the
                // stroked half read as no ink.
                if (renderMode.Strokes())
                    _ctx.ReportInkNotMeasured("the stroking half of a text render mode");

                DrawInkMeasuredTextRun(codes, typeface, glyphSource, type1Source, isOtfCff, text);
                return;
            }
            if (!invisible && renderMode.Strokes() && !fillMode &&
                _ctx.InkAccumulator != null && _ctx.GS.StrokeSepTints != null)
                _ctx.ReportInkNotMeasured("stroked-only text");

            // A run the file set to overprint paints normally: simulation is the plate-based
            // preview's (OverprintPreview), so reaching here means it is off. The context reports
            // that per paint side — the same call a path fill or stroke makes, so text and paths
            // cannot give different answers.
            if (!invisible)
            {
                if (fillMode)             _ctx.ReportOverprintUnavailable(stroke: false);
                if (renderMode.Strokes()) _ctx.ReportOverprintUnavailable(stroke: true);
            }

            if (invisible)           { DrawInvisibleRun(codes); return; }
            if (isOtfCff)            { DrawOtfCffRun(codes, glyphSource, typeface); return; }
            if (type1Source != null) { DrawType1Run(codes, type1Source); return; }
            if (glyphSource != null) { DrawCffRun(codes, glyphSource, text); return; }

            DrawStandardRun(codes, typeface, text);
        }

        /// <summary>True when the named pattern exists in the current /Pattern resource dict.</summary>
        private bool PatternResourceExists(string name)
        {
            if (_resources == null || name == null) return false;
            var patCat = _doc.ResolveObject(_resources[PdfName.Pattern]) as PdfDictionary;
            return patCat != null && patCat[name] != null;   // internal string indexer
        }

        /// <summary>
        /// Renders a text run whose fill colour is a pattern: captures the run's glyph outlines
        /// (via the normal per-font run methods, which append outlines instead of painting while
        /// capture is active) then paints the current fill pattern into the glyph-shaped path.
        /// </summary>
        private void DrawPatternTextRun(IReadOnlyList<CMapCodec.Code> codes, SKTypeface typeface,
            CffGlyphSource glyphSource, Type1GlyphSource type1Source, bool isOtfCff, string text)
        {
            _ctx.BeginGlyphCapture();
            if (isOtfCff)                 DrawOtfCffRun(codes, glyphSource, typeface);
            else if (type1Source != null) DrawType1Run(codes, type1Source);
            else if (glyphSource != null) DrawCffRun(codes, glyphSource, text);
            else                          DrawStandardRun(codes, typeface, text);

            using SKPath glyphs = _ctx.EndGlyphCapture();
            if (glyphs == null || glyphs.IsEmpty) return; // spaces / empty glyphs — nothing to paint

            string patternName = _ctx.GS.FillPatternName;
            // A Tj does NOT consume the pattern (unlike a path fill), so FillIsPattern is left set.
            _ctx.FillGlyphsWithPattern(glyphs, () => RenderTilingPattern(patternName, stroke: false));
        }

        /// <summary>
        /// Renders a spot-coloured text run and records the ink its glyphs lay down, for a host
        /// that attached an <see cref="InkAccumulator"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>The glyphs are measured, then painted — not painted twice.</b> The capture pass
        /// is the run: the per-font run methods append outlines instead of drawing while capture is
        /// active, and the text matrix advances there exactly as it always does. Re-running them to
        /// paint would advance the matrix a second time and lay the run down twice, offset.</para>
        /// <para><b>Only fill modes are measured.</b> A mode that also strokes (1, 2, 5, 6) puts ink
        /// under the pen as well, and the pen's colorants are the stroking ones — a separate
        /// snapshot this branch does not carry. That half is reported by the ladder below rather
        /// than silently counted as zero.</para>
        /// </remarks>
        private void DrawInkMeasuredTextRun(IReadOnlyList<CMapCodec.Code> codes, SKTypeface typeface,
            CffGlyphSource glyphSource, Type1GlyphSource type1Source, bool isOtfCff, string text)
        {
            _ctx.BeginGlyphCapture();
            if (isOtfCff)                 DrawOtfCffRun(codes, glyphSource, typeface);
            else if (type1Source != null) DrawType1Run(codes, type1Source);
            else if (glyphSource != null) DrawCffRun(codes, glyphSource, text);
            else                          DrawStandardRun(codes, typeface, text);

            using SKPath glyphs = _ctx.EndGlyphCapture();
            if (glyphs == null || glyphs.IsEmpty) return;   // spaces / empty glyphs

            _ctx.DepositGlyphInk(glyphs);
            _ctx.FillCapturedGlyphs(glyphs);
        }

        // ---- Per-font-path text rendering (extracted from DrawTextBytes) ----

        // Invisible text (render mode 3, e.g. OCR overlay layers): no glyphs are drawn,
        // but the text matrix must still advance using the PDF /W widths.
        private void DrawInvisibleRun(IReadOnlyList<CMapCodec.Code> codes)
        {
            var invisFont = _fontCache.GetPdfFont(_ctx.GS.FontResourceName, _resources, _doc);

            // §9.4.4: in writing mode 1 the run advances DOWNWARD — AdvanceTm composes
            // [1 0 0 1 tx 0] only horizontally. Otherwise a vertical CJK page carrying an OCR overlay
            // (an Identity-V font shown under `3 Tr`) would lay that invisible run out left-to-right
            // here, top-to-bottom in the SVG export, and
            // top-to-bottom in this same renderer's VISIBLE path — the renderer disagreed with
            // itself between render mode 3 and render mode 0 for one font and one string.
            //
            // Invisible or not, the positions matter: an OCR overlay exists to be selected and
            // searched, so a drifting one puts the selection somewhere the reader did not click.
            // And because every glyph stays internally correct and only the ones after it move,
            // SvgRasterParityTests structurally cannot see this.
            if (_fontCache.GetWMode(_ctx.GS.FontResourceName, _resources, _doc) == 1)
            {
                float totalVy = 0f;
                for (int i = 0; i < codes.Count; i++)
                {
                    float emAdv = _fontCache.GetVerticalAdvance(
                        _ctx.GS.FontResourceName, codes[i].Cid, _resources, _doc, 1f);
                    totalVy += emAdv * _ctx.GS.FontSize + _ctx.GS.CharSpacing;
                }
                _ctx.GS.Tm = GraphicsState.Compose(new float[] { 1, 0, 0, 1, 0, -totalVy }, _ctx.GS.Tm);
                return;
            }

            for (int i = 0; i < codes.Count; i++)
            {
                // One rule, in GlyphWidth. This site coerced ANY non-positive width to the
                // default with no HasExplicitWidth escape, so a Type 0 font's explicit /W 0 (the
                // Hebrew-nikud case) advanced 500 units here and 0 everywhere else -- the overlay
                // drifted from the glyphs it overlays, and this renderer disagreed with itself
                // between render mode 3 and render mode 0.
                double w = GlyphWidth.Units(invisFont, codes[i].Cid);
                float advPts = (float)(w / PdfUnits.GlyphSpaceUnitsPerEm) * _ctx.GS.FontSize;
                AdvanceTm(advPts, codes[i]);
            }
        }

        /// <summary>
        /// OTF-wrapped CFF: render by GID so Skia applies Type 2 hinting.
        /// </summary>
        /// <remarks>
        /// <para><b>One <c>SKTextBlob</c> for the whole run, not one per glyph.</b> A draw per code
        /// builds a text CTM, a screen matrix, an <c>SKFont</c>, an <c>SKTextBlobBuilder</c>, a blob,
        /// an <c>SKPaint</c> and a closure <i>per glyph</i>. The batched shape is
        /// <see cref="PdfRenderContext.DrawGlyphRunWithWidths"/>, which <see cref="DrawStandardRun"/>
        /// uses too — not a second batched copy, since a hand-copied size matrix is where a missing
        /// <c>Th</c> (§9.4.4) hides.</para>
        /// <para>The arithmetic is <see cref="DrawStandardRun"/>'s exactly, substituting the CFF
        /// program's advance for the PDF <c>/Widths</c> one: <c>Th</c> is baked into the text
        /// matrix so it stretches the glyph, and is therefore deliberately absent from the
        /// positions handed to Skia, which the screen matrix built from that same matrix already
        /// carries.</para>
        /// <para>Positions are exact rather than approximate: <c>Tm</c> only ever translates along
        /// its own x-axis between glyphs, so n successive translations of <c>tx</c> and one
        /// translation of Σ<c>tx</c> are the same matrix.</para>
        /// </remarks>
        private void DrawOtfCffRun(IReadOnlyList<CMapCodec.Code> codes, CffGlyphSource glyphSource, SKTypeface typeface)
        {
            if (VerticalWriting)
            {
                DrawVerticalRun(codes, i =>
                {
                    _ctx.GS.Tm = GraphicsState.Compose(GlyphScaleMatrix(), _ctx.GS.Tm);
                    _ctx.DrawGlyphRunWithWidths(new[] { glyphSource.GetGid(codes[i].Cid) }, typeface, new[] { 0f });
                });
                return;
            }

            // §9.4.4: Trm = [Tfs·Th 0 0 ; 0 Tfs 0 ; 0 Trise 1] × Tm × CTM. DrawGlyphRunWithWidths
            // builds its screen matrix from Tm × CTM alone, so the size matrix is composed into Tm
            // here — the same hand-off DrawStandardRun makes.
            float[] glyphScaleMat =
            { _ctx.GS.FontSize * _ctx.GS.HorizScaling, 0, 0, _ctx.GS.FontSize, 0, _ctx.GS.TextRise };
            float[] scaledTm = GraphicsState.Compose(glyphScaleMat, _ctx.GS.Tm);
            float[] textCTM  = GraphicsState.Compose(scaledTm, _ctx.GS.CTM);

            // The em is the Y axis: Th rides in the X axis (see DrawStandardRun, where reading X
            // scaled advances by Th³).
            float sz = (float)Math.Sqrt(textCTM[2] * textCTM[2] + textCTM[3] * textCTM[3]);
            if (sz <= 0f) sz = _ctx.GS.FontSize > 0 ? _ctx.GS.FontSize : 1f;
            float widthScale = _ctx.GS.FontSize > 0 ? sz / _ctx.GS.FontSize : 1f;

            var gids      = new ushort[codes.Count];
            var pdfWidths = new float[codes.Count];
            float tmAdvance = 0f;
            for (int i = 0; i < codes.Count; i++)
            {
                int cid = codes[i].Cid;
                gids[i] = glyphSource.GetGid(cid);

                float advancePts = SanitizeAdvance(
                    glyphSource.GetAdvanceWidth(cid) * _ctx.GS.FontSize
                        / (float)PdfUnits.GlyphSpaceUnitsPerEm, _ctx.GS.FontSize);

                // Tw applies to the single-byte code 32 only (§9.3.3) — see AdvanceTm, whose
                // per-glyph arithmetic this reproduces in one pass.
                float wordAdv = codes[i].IsWordSpaceCode ? _ctx.GS.WordSpacing : 0f;
                float unscaledAdv = advancePts + _ctx.GS.CharSpacing + wordAdv;

                pdfWidths[i] = unscaledAdv * widthScale;   // no Th: the screen matrix supplies it
                tmAdvance   += unscaledAdv * _ctx.GS.HorizScaling;
            }

            var savedTm = _ctx.GS.Tm;
            _ctx.GS.Tm = scaledTm;
            // unicodeText stays null deliberately. Phase 2 of DrawGlyphRunWithWidths re-resolves
            // GID 0 through a system font, which is right for a substituted face and wrong here:
            // in an embedded subset GID 0 is a legitimate .notdef, and DrawGlyphById — the method
            // this replaces — never had that phase.
            _ctx.DrawGlyphRunWithWidths(gids, typeface, pdfWidths);
            _ctx.GS.Tm = savedTm;

            float[] adv = { 1, 0, 0, 1, tmAdvance, 0 };
            _ctx.GS.Tm = GraphicsState.Compose(adv, _ctx.GS.Tm);
        }

        // Type 1 path-based rendering: /Encoding → glyph name → Type 1 charstring path.
        private void DrawType1Run(IReadOnlyList<CMapCodec.Code> codes, Type1GlyphSource type1Source)
        {
            if (VerticalWriting)
            {
                DrawVerticalRun(codes, i =>
                {
                    var o = type1Source.GetOutline(codes[i].Cid);
                    if (o.HasValue && !o.Value.Path.IsEmpty)
                        _ctx.DrawGlyphPath(o.Value.Path, _ctx.GS.FontSize, type1Source.FontMatrixScale);
                });
                return;
            }

            for (int i = 0; i < codes.Count; i++)
            {
                int cid = codes[i].Cid;
                var outline = type1Source.GetOutline(cid);
                if (outline.HasValue && !outline.Value.Path.IsEmpty)
                    _ctx.DrawGlyphPath(outline.Value.Path, _ctx.GS.FontSize, type1Source.FontMatrixScale);

                float advancePts = SanitizeAdvance(
                    type1Source.GetAdvanceWidth(cid) * _ctx.GS.FontSize
                        / (float)PdfUnits.GlyphSpaceUnitsPerEm, _ctx.GS.FontSize);
                AdvanceTm(advancePts, codes[i]);
            }
        }

        // CFF path-based rendering: draw each character as an SKPath, with a Latin-ligature
        // component fallback when a subset font lacks the combined glyph (fi/ffi but not ff/fl).
        private void DrawCffRun(IReadOnlyList<CMapCodec.Code> codes, CffGlyphSource glyphSource, string text)
        {
            if (VerticalWriting)
            {
                DrawVerticalRun(codes, i =>
                {
                    var o = glyphSource.GetOutline(codes[i].Cid);
                    if (o.HasValue && !o.Value.Path.IsEmpty)
                        _ctx.DrawGlyphPath(o.Value.Path, _ctx.GS.FontSize, glyphSource.FontMatrixScale);
                });
                return;
            }

            for (int i = 0; i < codes.Count; i++)
            {
                int cid = codes[i].Cid;
                var outline = glyphSource.GetOutline(cid);

                if ((!outline.HasValue || outline.Value.Path.IsEmpty) && i < text.Length)
                {
                    int compCode = LigatureFirstComponentCode(text[i]);
                    if (compCode > 0)
                    {
                        var alt = glyphSource.GetOutline(compCode);
                        if (alt.HasValue && !alt.Value.Path.IsEmpty)
                            outline = alt;
                    }
                }

                if (outline.HasValue && !outline.Value.Path.IsEmpty)
                    _ctx.DrawGlyphPath(outline.Value.Path, _ctx.GS.FontSize, glyphSource.FontMatrixScale);

                float advancePts = SanitizeAdvance(
                    glyphSource.GetAdvanceWidth(cid) * _ctx.GS.FontSize
                        / (float)PdfUnits.GlyphSpaceUnitsPerEm, _ctx.GS.FontSize);
                AdvanceTm(advancePts, codes[i]);
            }
        }

        // A heavy CFF program Skia would have drawn: each glyph id the typeface resolved, drawn from our
        // outline (nothing for one the page's budget refused), advanced by the PDF's widths as DrawStandardRun
        // would have.
        private void DrawProbeRun(IReadOnlyList<CMapCodec.Code> codes, ushort[] glyphIds, CffGlyphSource probe, PdfFont pdfFont)
        {
            if (VerticalWriting)
            {
                DrawVerticalRun(codes, i =>
                {
                    var o = i < glyphIds.Length ? probe.GetOutlineForGid(glyphIds[i]) : null;
                    if (o.HasValue && !o.Value.Path.IsEmpty)
                        _ctx.DrawGlyphPath(o.Value.Path, _ctx.GS.FontSize, probe.FontMatrixScale);
                });
                return;
            }

            for (int i = 0; i < codes.Count; i++)
            {
                var outline = i < glyphIds.Length ? probe.GetOutlineForGid(glyphIds[i]) : null;
                if (outline.HasValue && !outline.Value.Path.IsEmpty)
                    _ctx.DrawGlyphPath(outline.Value.Path, _ctx.GS.FontSize, probe.FontMatrixScale);

                float advancePts = SanitizeAdvance(
                    (float)(GlyphWidth.Units(pdfFont, codes[i].Cid) / PdfUnits.GlyphSpaceUnitsPerEm) * _ctx.GS.FontSize,
                    _ctx.GS.FontSize);
                AdvanceTm(advancePts, codes[i]);
            }
        }

        /// <summary>Whether the current font writes vertically (<c>/WMode 1</c>, §9.7.4.3).</summary>
        private bool VerticalWriting =>
            _fontCache.GetWMode(_ctx.GS.FontResourceName, _resources, _doc) == 1;

        /// <summary>
        /// Places a vertical run one glyph at a time — §9.7.4.3's rule, for every font path: each
        /// glyph is drawn at <c>currentPoint − v</c> (the position vector that centres it on the
        /// column), then the point steps down by <see cref="VerticalGlyphMetrics.Step"/>.
        /// <paramref name="drawGlyph"/> draws glyph <c>i</c> at the shifted <c>Tm</c> and does not
        /// advance it.
        /// </summary>
        /// <remarks>
        /// The standard-font run had this; the CFF and Type 1 run paths did not, and advanced every
        /// glyph horizontally whatever the writing mode — an embedded CID-keyed CFF program, the
        /// common case for Japanese, laid a vertical line out as a horizontal one.
        /// </remarks>
        private void DrawVerticalRun(IReadOnlyList<CMapCodec.Code> codes, Action<int> drawGlyph)
        {
            float[] vertTm = _ctx.GS.Tm;
            for (int i = 0; i < codes.Count; i++)
            {
                var vm = _fontCache.GetVerticalMetrics(
                    _ctx.GS.FontResourceName, codes[i].Cid, _resources, _doc, 1f);
                float[] originShift =
                {
                    1, 0, 0, 1,
                    -vm.PositionXEm * _ctx.GS.FontSize,
                    -vm.PositionYEm * _ctx.GS.FontSize,
                };
                _ctx.GS.Tm = GraphicsState.Compose(originShift, vertTm);
                drawGlyph(i);

                // The advance is measured from the horizontal origin, which the shift never touched.
                float advY = vm.Step(_ctx.GS.FontSize, _ctx.GS.CharSpacing, _ctx.GS.WordSpacing, codes[i].IsWordSpaceCode);
                vertTm = GraphicsState.Compose(new float[] { 1, 0, 0, 1, 0, -advY }, vertTm);
            }
            _ctx.GS.Tm = vertTm;
        }

        /// <summary>The glyph-space-to-Tm size matrix of §9.4.4, <c>[Tfs·Th 0 0 Tfs 0 Trise]</c>.</summary>
        private float[] GlyphScaleMatrix() =>
            new float[] { _ctx.GS.FontSize * _ctx.GS.HorizScaling, 0, 0, _ctx.GS.FontSize, 0, _ctx.GS.TextRise };

        /// <summary>
        /// Advances Tm by one glyph: <c>tx = (w + Tc + Tw) × Th</c> (PDF §9.4.4).
        /// </summary>
        /// <remarks>
        /// Word spacing is added only for <see cref="CMapCodec.Code.IsWordSpaceCode"/> — the
        /// single-byte code 32. PDF §9.3.3 explicitly excludes "occurrences of the byte value 32
        /// in multiple-byte codes", so a loop testing
        /// <c>bytes[i] == 0x00 &amp;&amp; bytes[i+1] == 0x20</c> would apply Tw to the 2-byte code
        /// 0x0020 — under Identity-H glyph 32, rarely a space — and a non-zero Tw would insert gaps
        /// at arbitrary positions in CJK/Hebrew runs.
        /// </remarks>
        private void AdvanceTm(float advancePts, CMapCodec.Code code)
        {
            float wordAdv = code.IsWordSpaceCode ? _ctx.GS.WordSpacing : 0f;
            float tx = (advancePts + _ctx.GS.CharSpacing + wordAdv) * _ctx.GS.HorizScaling;

            // Composing [1 0 0 1 tx 0] with Tm reduces to translating Tm's origin along its own
            // x-axis: the left matrix is a pure translation, so a/b/c/d come through unchanged
            // and only e/f move. Written out rather than routed through GraphicsState.Compose,
            // which allocated the operand array as well as the result — two float[6] per glyph,
            // on every text run of every font path.
            //
            // A NEW array, not an in-place update, and deliberately. Tm is shared by reference:
            // GraphicsState.Clone does not deep-copy it (§9.3.1 — the text matrices are not part
            // of the saved graphics state), and DrawStandardRun aliases it into `vertTm` for the
            // first iteration of the vertical-CJK loop. Mutating in place would write through
            // those aliases. Halving the allocation is provably safe; removing it is not.
            float[] tm = _ctx.GS.Tm;
            _ctx.GS.Tm = new float[] { tm[0], tm[1], tm[2], tm[3], tx * tm[0] + tm[4], tx * tm[1] + tm[5] };
        }

        // Resolves the GID for each char code: /CIDToGIDMap, Identity, or Unicode cmap with
        // a four-stage fallback (decoded codepoint → raw ASCII → Latin ligature → Windows PUA F0xx).
        // Delegates to the shared GlyphIdResolver (also used by PdfSvgRenderer) so raster and
        // SVG output select identical glyphs.
        private ushort[] ResolveGlyphIds(IReadOnlyList<CMapCodec.Code> codes, string text, SKTypeface typeface)
            => GlyphIdResolver.Resolve(_fontCache, _ctx.GS.FontResourceName, _resources, _doc,
                                       codes, text, typeface);

        // Standard per-glyph rendering using the PDF /W width table for positioning.
        // KEY FIX (Hebrew/RTL): glyph placement uses the PDF font's /W widths (PdfFont.GetWidth
        // reads /W and /DW from the CID dict), NOT the system font's measured metrics, which
        // differ for Type0 fonts and misplace Hebrew glyphs.
        private void DrawStandardRun(IReadOnlyList<CMapCodec.Code> codes, SKTypeface typeface, string text)
        {
            var pdfFont = _fontCache.GetPdfFont(_ctx.GS.FontResourceName, _resources, _doc);

            // A CFF program Skia loaded itself runs its charstrings there under no budget of ours:
            // the glyphs it would draw are asked of our evaluator first, and a heavy program is drawn
            // from our outlines instead (F15).
            var probe = typeface == null ? null : _fontCache.GetHintingProbe(_ctx.GS.FontResourceName, _resources, _doc);
            if (probe != null)
            {
                ushort[] probeIds = ResolveGlyphIds(codes, text, typeface);
                if (probe.IsTooHeavyForHinting(probeIds)) { DrawProbeRun(codes, probeIds, probe, pdfFont); return; }
            }

            bool isVertical = _fontCache.GetWMode(_ctx.GS.FontResourceName, _resources, _doc) == 1;
            // Vertical fonts: Tm already encodes the writing direction, so no rotation is added.
            // §9.4.4: Trm = [Tfs·Th 0 0 ; 0 Tfs 0 ; 0 Trise 1] × Tm × CTM — the horizontal scaling
            // Th belongs in the `a` slot, so it stretches the GLYPH, not only the advance. It was
            // applied to advances alone, so `50 Tz` spaced the text at half width while drawing every
            // glyph at full width. (DrawGlyphRunWithWidths had to stop taking the em size from the
            // x-axis first, or this is cancelled on the way out.)
            float[] glyphScaleMat = new float[]
            { _ctx.GS.FontSize * _ctx.GS.HorizScaling, 0, 0, _ctx.GS.FontSize, 0, _ctx.GS.TextRise };
            float[] scaledTm = GraphicsState.Compose(glyphScaleMat, _ctx.GS.Tm);
            float[] textCTM  = GraphicsState.Compose(scaledTm, _ctx.GS.CTM);
            // sz = 1-em height in canvas space; keeps pdfWidths sane when the CTM scales.
            //
            // The Y axis, for the same reason DrawGlyphRunWithWidths takes its em size from there:
            // §9.4.4 puts the horizontal scaling Th in the matrix's `a` slot, so the X axis carries
            // Th and the Y axis does not. Reading X here multiplied Th into widthScale — and since
            // glyphAdv below carries it too and the draw-time screen matrix applies it a third time,
            // an embedded font's advances came out scaled by Th³. At `100 Tz` every factor is 1,
            // which is why a full-scale render looked right and only `50 Tz` collapsed the run to a
            // fifth of its width. A substituted font never reached it (pdfWidths is -1 there).
            float sz = (float)Math.Sqrt(textCTM[2] * textCTM[2] + textCTM[3] * textCTM[3]);
            if (sz <= 0f) sz = _ctx.GS.FontSize > 0 ? _ctx.GS.FontSize : 1f;
            float widthScale = _ctx.GS.FontSize > 0 ? sz / _ctx.GS.FontSize : 1f;

            // System-fallback fonts: the PDF /Widths belong to the original font and would
            // mis-space the substitute's glyphs; signal DrawGlyphRunWithWidths to measure via
            // SkiaSharp by passing -1 per entry (totalPdfAdvance is then derived from its return).
            bool useSkiaWidths = _fontCache.IsSystemFallbackFont(_ctx.GS.FontResourceName, _resources, _doc);

            float totalPdfAdvance = 0f;
            float widthsAdvance = 0f;
            var pdfWidths = new float[codes.Count];
            // Tc/Tw for the measured path only. Skia measures a glyph, not a text state, so
            // §9.4.4's "+ Tc + Tw" has to travel separately or it is lost for every substituted
            // font — which is every standard-14 document.
            var extraSpacing = useSkiaWidths ? new float[codes.Count] : null;
            for (int gi = 0; gi < codes.Count; gi++)
            {
                if (useSkiaWidths)
                {
                    // NaN, not -1: a NEGATIVE advance is legal (§9.4.4 — `tx` carries Tc and
                    // Tw, and both may be negative), so a negative magic value collides with
                    // real data. NaN cannot be produced by the width arithmetic.
                    pdfWidths[gi] = float.NaN;
                    float tw = codes[gi].IsWordSpaceCode ? _ctx.GS.WordSpacing : 0f;
                    // No Th here, exactly as for pdfWidths below: the screen matrix applies it.
                    extraSpacing[gi] = (_ctx.GS.CharSpacing + tw) * widthScale;
                    // What §9.4.4 says the pen moves by, from the font's own widths: the pen is
                    // moved by the substitute's measure below, and the difference is kept for a
                    // caller that writes the file's own advance (AdvanceBeyondWidths).
                    if (pdfFont != null)
                        widthsAdvance += ((float)(GlyphWidth.Units(pdfFont, codes[gi].Cid) / PdfUnits.GlyphSpaceUnitsPerEm) * _ctx.GS.FontSize
                                          + _ctx.GS.CharSpacing + tw) * _ctx.GS.HorizScaling;
                    continue;
                }

                int cid = codes[gi].Cid;
                double w = GlyphWidth.Units(pdfFont, cid);
                float advPts = (float)(w / PdfUnits.GlyphSpaceUnitsPerEm) * _ctx.GS.FontSize;

                // Tw applies to the single-byte code 32 only (PDF §9.3.3) — see AdvanceTm.
                float wordAdv = codes[gi].IsWordSpaceCode ? _ctx.GS.WordSpacing : 0f;
                // §9.4.4: tx = ((w0 − Tj/1000)·Tfs + Tc + Tw) · Th — Th multiplies the whole term.
                float unscaledAdv = advPts + _ctx.GS.CharSpacing + wordAdv;
                float glyphAdv = unscaledAdv * _ctx.GS.HorizScaling;

                // pdfWidths are glyph POSITIONS handed to Skia, and the screen matrix built from
                // this same text matrix already carries Th — so they must be the advance WITHOUT
                // it, or Th lands on the spacing twice over.
                pdfWidths[gi] = unscaledAdv * widthScale;
                totalPdfAdvance += glyphAdv; // text-space units for Tm advancement
            }
            var savedTm = _ctx.GS.Tm;
            _ctx.GS.Tm = scaledTm;

            ushort[] glyphIds = ResolveGlyphIds(codes, text, typeface);

            // Vertical CJK: one glyph at a time at its vertical origin (§9.7.4.3) — DrawVerticalRun,
            // shared with the CFF and Type 1 paths. Only w1y was read once, so the pen advanced
            // correctly and every glyph was drawn on the horizontal origin, up and to the right of
            // its column; the position vector is what centres it.
            if (isVertical)
            {
                _ctx.GS.Tm = savedTm;
                DrawVerticalRun(codes, gi =>
                {
                    if (gi >= glyphIds.Length) return;
                    _ctx.GS.Tm = GraphicsState.Compose(glyphScaleMat, _ctx.GS.Tm);
                    _ctx.DrawGlyphRunWithWidths(
                        new[] { glyphIds[gi] }, typeface, new[] { 0f },
                        gi < text.Length ? text[gi].ToString() : " ");
                });
                return;
            }

            float canvasAdvance = _ctx.DrawGlyphRunWithWidths(
                glyphIds, typeface, pdfWidths, text, extraSpacing);

            _ctx.GS.Tm = savedTm;

            // Advance text matrix using PDF widths (not SkiaSharp widths).
            int wMode = _fontCache.GetWMode(_ctx.GS.FontResourceName, _resources, _doc);
            if (wMode == 1)
            {
                // Vertical CJK fallback (wMode==1 but not handled above — shouldn't normally reach here).
                float totalVy = 0f;
                for (int gi = 0; gi < codes.Count; gi++)
                {
                    float emAdv = _fontCache.GetVerticalAdvance(
                        _ctx.GS.FontResourceName, codes[gi].Cid, _resources, _doc, 1f);
                    totalVy += emAdv * _ctx.GS.FontSize + _ctx.GS.CharSpacing;
                }
                float[] adv = { 1, 0, 0, 1, 0, -totalVy };
                _ctx.GS.Tm = GraphicsState.Compose(adv, _ctx.GS.Tm);
            }
            else
            {
                // For system-fallback fonts the PDF /Widths were not used; derive the
                // text-space advance from the SkiaSharp-measured canvas advance instead.
                //
                // × HorizScaling because canvasAdvance is measured in the same Th-free space as
                // pdfWidths — the screen matrix supplies Th when drawing, but nothing supplies it
                // to Tm, so without this a second Tj on the same line started at the wrong x
                // whenever Tz was in effect.
                float tmAdv = useSkiaWidths && widthScale > 0f
                    ? canvasAdvance / widthScale * _ctx.GS.HorizScaling
                    : totalPdfAdvance;
                if (useSkiaWidths && widthScale > 0f && pdfFont != null) _advanceBeyondWidths += tmAdv - widthsAdvance;
                float[] adv = { 1, 0, 0, 1, tmAdv, 0 };
                _ctx.GS.Tm = GraphicsState.Compose(adv, _ctx.GS.Tm);
            }
        }

        // ---- Type 3 font rendering ----
    }
}
