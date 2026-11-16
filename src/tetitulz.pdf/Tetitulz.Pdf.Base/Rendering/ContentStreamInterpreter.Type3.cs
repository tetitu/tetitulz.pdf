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
        /// <summary>
        /// Renders each byte in <paramref name="bytes"/> as a Type 3 glyph by executing
        /// its /CharProcs stream under the font's /FontMatrix and current text matrix.
        /// d0 and d1 operators (setcharwidth / setcachedevice) provide the advance width.
        /// </summary>
        private void DrawType3Bytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return;
            if (RenderDepth >= PdfLimits.MaxRenderRecursionDepth)
            {
                if (FormBudget?.FirstDepthRefusal() ?? true)
                    RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                        $"Type 3 glyph nesting exceeds {PdfLimits.MaxRenderRecursionDepth} levels; possible recursive font.", null);
                return;
            }

            // Resolve font dictionary.
            var fontRes = _resources?[PdfName.Font];
            if (fontRes == null) return;
            var fontResDict = _doc.ResolveObject(fontRes) as PdfDictionary;
            if (fontResDict == null) return;
            var entry = fontResDict[new PdfName(_ctx.GS.FontResourceName)];
            if (entry == null) return;
            var fontDict = _doc.ResolveObject(entry) as PdfDictionary ?? entry as PdfDictionary;
            if (fontDict == null) return;

            // /FontMatrix: glyph space to text space.
            float[] fontMatrix = Type3Encoding.FontMatrixOf(_doc, fontDict, out _);

            // /CharProcs dictionary.
            var charProcsObj = fontDict[PdfName.CharProcs];
            if (charProcsObj == null) return;
            var charProcs = _doc.ResolveObject(charProcsObj) as PdfDictionary;
            if (charProcs == null) return;

            // /Encoding for this Type 3 font — maps char code → glyph name.
            var encObj = fontDict[PdfName.Encoding];
            var encDict = encObj != null
                ? _doc.ResolveObject(encObj) as PdfDictionary ?? encObj as PdfDictionary
                : null;

            // /Widths array (indexed by char code - FirstChar).
            var widthsObj = fontDict[PdfName.Widths];
            var widthsArr = widthsObj != null
                ? _doc.ResolveObject(widthsObj) as PdfArray ?? widthsObj as PdfArray
                : null;
            // Resolved: SvgContentWriter.Text reads the same key through PdfResolve.Number, so an
            // indirect /FirstChar gave the two renderers different advances for the same file.
            int firstChar = PdfResolve.Number(_doc, fontDict, PdfName.FirstChar)?.IntValue ?? 0;

            // /Resources for CharProc streams (fall back to page resources).
            var resObj = fontDict[PdfName.Resources];
            var type3Resources = resObj != null
                ? _doc.ResolveObject(resObj) as PdfDictionary ?? _resources
                : _resources;

            // §9.3.6: Type 3 text in mode 3 or 7 is not painted; the pen still moves by /Widths,
            // without running a procedure whose every paint would ink the page. Mode 7's clip is
            // not accumulated from a glyph procedure, and is said.
            var renderMode = (TextRenderMode)_ctx.GS.TextRenderMode;
            bool invisible = renderMode.IsInvisible() || renderMode == TextRenderMode.Clip;
            if (renderMode == TextRenderMode.Clip)
                RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                    "Type 3 text in render mode 7 adds nothing to the clipping path; the text is not painted and the clip is left as it was.");

            for (int i = 0; i < bytes.Length; i++)
            {
                int charCode = bytes[i];
                if (invisible) { AdvanceType3(charCode, widthsArr, firstChar, fontMatrix); continue; }

                // Resolve char code → glyph name via /Encoding.
                string glyphName = ResolveType3GlyphName(encDict, charCode);
                if (glyphName == null) { AdvanceType3(charCode, widthsArr, firstChar, fontMatrix); continue; }

                // Look up CharProcs entry.
                var cpObj = charProcs[glyphName];   // internal string indexer — per Type 3 glyph
                if (cpObj == null) { AdvanceType3(charCode, widthsArr, firstChar, fontMatrix); continue; }
                var cpStream = _doc.ResolveObject(cpObj) as PdfStream ?? cpObj as PdfStream;
                if (cpStream == null) { AdvanceType3(charCode, widthsArr, firstChar, fontMatrix); continue; }

                byte[] cpData;
                try { cpData = cpStream.GetDecodedData(); }
                catch (Exception ex)
                {
                    RenderDiagnostics.Report(RenderDiagnosticKind.Type3GlyphDecodeFailed,
                        $"Type 3 glyph charCode={charCode} CharProc decode failed", ex);
                    AdvanceType3(charCode, widthsArr, firstChar, fontMatrix);
                    continue;
                }
                if (cpData == null || cpData.Length == 0) { AdvanceType3(charCode, widthsArr, firstChar, fontMatrix); continue; }

                // A glyph procedure is an invocation like a form's, charged to the same page budget
                // before any state is pushed: the depth cap above bounds one branch, and a glyph that
                // shows four glyphs of its own font is 4^24 executions under it — the structure
                // fuzzer's first finding. Past the budget the glyph still advances, so the text after
                // it lands where the file expects.
                if (FormBudget != null && !FormBudget.TryChargeFormInvocation(cpData.Length))
                {
                    AdvanceType3(charCode, widthsArr, firstChar, fontMatrix);
                    continue;
                }

                // Push state and apply: FontMatrix * [fontSize 0 0 fontSize 0 textRise] * Tm * CTM
                // Save Tm first: PopState() preserves the CURRENT Tm per PDF §9.3.1 (q/Q do not
                // save/restore text state). We reset Tm to identity inside the CharProc context,
                // so without this save/restore every glyph after the first would use Tm=identity
                // instead of the accumulated text matrix and render off-screen.
                var savedTm = _ctx.GS.Tm;
                _ctx.PushState();
                // Th in the `a` slot, as above (§9.4.4).
                float[] textScale = { _ctx.GS.FontSize * _ctx.GS.HorizScaling, 0, 0, _ctx.GS.FontSize, 0, _ctx.GS.TextRise };
                float[] glyphToCTM = GraphicsState.Compose(
                    fontMatrix,
                    GraphicsState.Compose(
                        textScale,
                        GraphicsState.Compose(_ctx.GS.Tm, _ctx.GS.CTM)));
                // Replace CTM with glyphToCTM; reset Tm to identity since it's now baked in.
                _ctx.GS.CTM = glyphToCTM;
                _ctx.GS.Tm  = new float[] { 1, 0, 0, 1, 0, 0 };
                // §8.7.3.1: a pattern the glyph procedure uses maps through the glyph's space.
                _ctx.GS.PatternBase = (float[])glyphToCTM.Clone();

                // Execute CharProc stream. d0/d1 are handled inside the interpreter.
                var inner = new ContentStreamInterpreter(_ctx, _doc, type3Resources, _fontCache) { HiddenOcgs = HiddenOcgs, RenderDepth = RenderDepth + 1, CancellationToken = CancellationToken, FormBudget = FormBudget };
                inner.ExecuteType3CharProc(cpData, out float advanceFromD);

                _ctx.PopState();
                // Restore the accumulated text matrix (PopState preserved Tm=identity, not savedTm).
                _ctx.GS.Tm = savedTm;

                // Advance Tm: §9.6.5 prefers the glyph procedure's own d0/d1 advance over /Widths.
                // The tx formula itself is Type3Encoding's — this was the third copy of it.
                float w0 = !float.IsNaN(advanceFromD)
                    ? advanceFromD * fontMatrix[0]
                    : Type3Encoding.GlyphWidth(charCode, widthsArr, firstChar, fontMatrix, _doc);

                float tx = Type3Encoding.AdvanceTx(
                    w0, charCode,
                    _ctx.GS.FontSize, _ctx.GS.CharSpacing, _ctx.GS.WordSpacing, _ctx.GS.HorizScaling);
                float[] stepMat = { 1, 0, 0, 1, tx, 0 };
                _ctx.GS.Tm = GraphicsState.Compose(stepMat, _ctx.GS.Tm);
            }
        }

        /// <summary>
        /// The <c>/CharProcs</c> glyph name for a character code (§9.6.6.2).
        /// </summary>
        /// <remarks>
        /// One implementation, in <see cref="Type3Encoding"/>, reached from here and from the SVG
        /// writer. The private 95-entry <c>StandardType3Encoding</c> table that stood beside this
        /// went with it — a fourth copy of the ASCII glyph-name list, and a static field on this
        /// type whose initializer is the one the SVG writer must never acquire an edge on.
        /// </remarks>
        private string ResolveType3GlyphName(PdfDictionary encDict, int charCode)
            => Type3Encoding.GlyphName(_doc, encDict, charCode);


        // §9.4.4's tx, in the Type3Encoding leaf — one implementation, shared with the SVG writer,
        // which had the same formula distributed differently across /Th. The width is resolved
        // there; both copies read it non-resolving through an operand helper.
        private void AdvanceType3(int charCode, PdfArray widthsArr, int firstChar, float[] fm)
        {
            float tx = Type3Encoding.AdvanceTx(
                Type3Encoding.GlyphWidth(charCode, widthsArr, firstChar, fm, _doc), charCode,
                _ctx.GS.FontSize, _ctx.GS.CharSpacing, _ctx.GS.WordSpacing, _ctx.GS.HorizScaling);
            float[] stepMat = { 1, 0, 0, 1, tx, 0 };
            _ctx.GS.Tm = GraphicsState.Compose(stepMat, _ctx.GS.Tm);
        }

        // ---- XObjects (Do) ----
    }
}
