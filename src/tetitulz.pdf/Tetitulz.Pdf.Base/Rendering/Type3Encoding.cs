// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// A Type 3 font's character code → <c>/CharProcs</c> glyph name (§9.6.6.2), shared by both
    /// renderers.
    /// </summary>
    /// <remarks>
    /// <para><b>Why one implementation.</b> The two copies disagreed twice, in opposite directions,
    /// before <c>3f4dea3</c> reconciled them: on a duplicate <c>/Differences</c> code the raster
    /// path returned the FIRST match where §9.6.6.1 makes the last one replace it (the SVG side,
    /// which builds a dictionary, was right), and where <c>/Differences</c> covered only part of a
    /// font's codes the SVG side synthesised a name matching nothing and drew notdef boxes for the
    /// rest (the raster side was right). Same font, two answers, twice.</para>
    ///
    /// <para><b>They agree today</b> — every branch pair was checked: no <c>/Encoding</c>,
    /// <c>/Encoding</c> as a name rather than a dictionary, every <c>/BaseEncoding</c>, duplicate and
    /// partial <c>/Differences</c>, non-name array items, an indirect <c>/Differences</c>. So this is
    /// a merge whose assertion is the merge; the existing Type 3 tests of both call sites are what
    /// prove the survivor serves both.</para>
    ///
    /// <para><b>It removes a type initializer rather than adding one.</b> The raster copy carried a
    /// private 95-entry <c>StandardType3Encoding</c> array — a fourth copy of the ASCII glyph-name
    /// table, and a <c>static readonly</c> field on <c>ContentStreamInterpreter</c>, whose type
    /// initializer is the one this repository records as having crashed the Base test host when the
    /// SVG writer was pointed at it. This class has <b>no static fields</b>: the tables it needs
    /// already live on <see cref="FontCache"/>, and <i>both</i> renderers already read them, so
    /// calling through here introduces no edge that was not there.</para>
    /// </remarks>
    internal static class Type3Encoding
    {
        /// <summary>Lowest and highest code the standard table defines (ASCII printable).</summary>
        private const int StandardFirst = 32;
        private const int StandardLast  = 126;

        /// <summary>
        /// A Type 3 font's <c>/FontMatrix</c>, glyph space to text space — the one reading,
        /// <c>Core.Fonts.Type3FontMatrix</c>, which both extractors read too: a missing one is the
        /// default 1000-unit glyph space, a corrupt one the default and <paramref name="replaced"/>.
        /// </summary>
        internal static float[] FontMatrixOf(PdfDocument doc, PdfDictionary font, out bool replaced)
            => Core.Fonts.Type3FontMatrix.Of(font, doc == null ? null : doc.ResolveObject, out replaced);

        /// <summary>
        /// §9.4.4's horizontal displacement for one Type 3 glyph:
        /// <c>tx = (w0 · Tfs + Tc + Tw) · Th</c>, where <c>w0</c> is the <c>/Widths</c> entry taken
        /// into text space by the font matrix's <c>a</c>. The caller composes
        /// <c>[1 0 0 1 tx 0]</c> onto <c>Tm</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>One implementation, both renderers.</b> The two were algebraically equal but
        /// factored differently — the raster path scaled the sum by <c>Th</c>, the SVG path
        /// distributed <c>Th</c> across three terms — so they could differ in the last ulp and,
        /// more to the point, nothing tied them together. CLAUDE.md singles this class out: **a
        /// defect in an advance is invisible to a pixel-comparing parity suite**, because each
        /// renderer stays internally self-consistent while every glyph after the bad one moves.
        /// The spec's own factoring is kept.</para>
        ///
        /// <para><b>The width is RESOLVED, which neither copy did.</b> Both read the element through
        /// <c>ContentOperand.ArrayFloat</c>, which is deliberately non-resolving because it exists
        /// for content-stream <i>operands</i> — and an operand cannot be an indirect reference. A
        /// <c>/Widths</c> entry is a font-dictionary array element and legally can be, so an
        /// indirect width advanced <b>0</b> in both renderers and silently overlapped every
        /// following glyph. Not a divergence — a shared gap, which is the kind a parity suite is
        /// structurally unable to find.</para>
        /// </remarks>
        /// <param name="textSpaceWidth">
        /// The glyph's width already in text space — <see cref="GlyphWidth"/> for the
        /// <c>/Widths</c> route, or the glyph procedure's own <c>d0</c>/<c>d1</c> advance times the
        /// font matrix's <c>a</c>, which §9.6.5 prefers when the procedure supplies one. Taking it
        /// as a parameter is what lets all three sites share the formula.
        /// </param>
        /// <param name="charCode">The byte shown; 32 additionally takes word spacing (§9.3.3).</param>
        /// <param name="fontSize">The text state's <c>Tfs</c>.</param>
        /// <param name="charSpacing">The text state's <c>Tc</c>, added once per glyph.</param>
        /// <param name="wordSpacing">The text state's <c>Tw</c>, added only for code 32.</param>
        /// <param name="horizScale">
        /// The text state's <c>Th</c> as a factor, scaling the whole sum. The SVG copy distributed
        /// it across the three terms instead; algebraically equal, and the spec's factoring is the
        /// one kept.
        /// </param>
        internal static float AdvanceTx(
            float textSpaceWidth, int charCode,
            float fontSize, float charSpacing, float wordSpacing, float horizScale)
        {
            float word = charCode == 0x20 ? wordSpacing : 0f;
            return (textSpaceWidth * fontSize + charSpacing + word) * horizScale;
        }

        /// <summary>
        /// The <c>/Widths</c> entry for <paramref name="charCode"/>, taken into text space by the
        /// font matrix's <c>a</c>. Zero when the font has no <c>/Widths</c> or the code falls
        /// outside it — §9.6.5 gives a Type 3 font no <c>/MissingWidth</c> to fall back on.
        /// </summary>
        /// <remarks>
        /// <b>The element is RESOLVED, which neither copy did.</b> Both read it through
        /// <c>ContentOperand.ArrayFloat</c>, which is deliberately non-resolving because it exists
        /// for content-stream <i>operands</i> — and an operand cannot be an indirect reference. A
        /// <c>/Widths</c> entry is a font-dictionary array element and legally can be, so an
        /// indirect width advanced <b>0</b> in both renderers and silently overlapped every
        /// following glyph. Not a divergence — a shared gap, which is the kind a parity suite is
        /// structurally unable to find.
        /// </remarks>
        internal static float GlyphWidth(
            int charCode, PdfArray widths, int firstChar, float[] fontMatrix, PdfDocument doc)
        {
            if (widths == null) return 0f;
            int idx = charCode - firstChar;
            if (idx < 0 || idx >= widths.Count) return 0f;
            float raw = (float)(PdfResolve.Number(doc, widths[idx])?.DoubleValue ?? 0.0);
            return raw * fontMatrix[0];
        }

        /// <summary>
        /// The glyph name for <paramref name="charCode"/>, or <c>null</c> when the font's encoding
        /// defines none — in which case the font simply has no glyph for that code (§9.6.6.2) and
        /// the caller draws nothing.
        /// </summary>
        /// <param name="encDict">
        /// The font's <c>/Encoding</c> <b>dictionary</b>, or null. A bare <c>/Encoding /WinAnsiEncoding</c>
        /// name is deliberately not consulted: only the dictionary's own <c>/BaseEncoding</c> is,
        /// which is what both copies did and what keeps them agreeing.
        /// </param>
        /// <param name="doc">The document, for resolving the <c>/Differences</c> array.</param>
        /// <param name="charCode">The character code from the content stream, 0–255.</param>
        internal static string GlyphName(PdfDocument doc, PdfDictionary encDict, int charCode)
        {
            if (encDict != null)
            {
                string mapped = FromDifferences(doc, encDict, charCode);
                if (mapped != null) return mapped;
            }

            string baseEncName = PdfResolve.Name(doc, encDict, PdfName.BaseEncoding)?.Value
                                 ?? "StandardEncoding";
            return FromBaseEncoding(baseEncName, charCode);
        }

        /// <summary>
        /// The <c>/Differences</c> mapping for one code, or null.
        /// </summary>
        /// <remarks>
        /// <b>The walk keeps going after a match.</b> §9.6.6.1 processes the array sequentially and
        /// a later entry for a code REPLACES an earlier one, so <c>/Differences [65 /A 65 /B]</c>
        /// maps 65 to B. Returning on the first match — which one copy did — drew A while the other,
        /// building a dictionary where the last write wins, drew B.
        /// </remarks>
        private static string FromDifferences(PdfDocument doc, PdfDictionary encDict, int charCode)
        {
            var diffObj = encDict[PdfName.Differences];
            if (diffObj == null) return null;

            var diffs = doc?.ResolveObject(diffObj) as PdfArray ?? diffObj as PdfArray;
            if (diffs == null) return null;

            int cur = 0;
            string mapped = null;
            foreach (var item in diffs)
            {
                if (item is PdfNumber n) cur = n.IntValue;
                else if (item is PdfName pn) { if (cur == charCode) mapped = pn.Value; cur++; }
            }
            return mapped;
        }

        /// <summary>
        /// The base encoding's name for a code, or null outside what it defines.
        /// </summary>
        /// <remarks>
        /// The standard table is <see cref="FontCache.WinAnsiNames"/> restricted to 32–126, which
        /// over that range <i>is</i> Adobe StandardEncoding character for character — both spell code
        /// 39 <c>quotesingle</c> and code 96 <c>grave</c>, the ASCII names rather than
        /// StandardEncoding's <c>quoteright</c>/<c>quoteleft</c>.
        /// </remarks>
        private static string FromBaseEncoding(string baseEncName, int charCode)
        {
            if (charCode < 0 || charCode > 255) return null;
            if (baseEncName == "WinAnsiEncoding")  return FontCache.WinAnsiNames[charCode];
            if (baseEncName == "MacRomanEncoding") return FontCache.MacRomanNames[charCode];
            return charCode >= StandardFirst && charCode <= StandardLast
                ? FontCache.WinAnsiNames[charCode]
                : null;
        }

        /// <summary>
        /// Builds the whole code → glyph-name map a text run needs, for the caller that walks bytes.
        /// </summary>
        internal static Dictionary<int, string> BuildMap(PdfDocument doc, PdfDictionary encDict)
        {
            var map = new Dictionary<int, string>();
            for (int code = 0; code < 256; code++)
            {
                string name = GlyphName(doc, encDict, code);
                if (name != null) map[code] = name;
            }
            return map;
        }
    }
}
