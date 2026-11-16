// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript.Graphics;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Emit
{
    /// <summary>One built glyph: its CharProc bytes and the width its procedure declared.</summary>
    internal sealed class PsBuiltGlyph
    {
        public byte[] Content { get; init; } = System.Array.Empty<byte>();

        /// <summary>
        /// The advance, in <b>glyph space</b> — the raw <c>wx</c> from <c>setcachedevice</c> or
        /// <c>setcharwidth</c>, <b>not</b> divided by 1000.
        ///
        /// <para>Table 112 puts a Type 3 font's <c>/Widths</c> in glyph space, and both of this
        /// library's renderers multiply by <c>FontMatrix[0]</c> themselves. This is the one place
        /// where the Type 1 habit of thousandths gives exactly the wrong instinct: /1000-scaled
        /// widths make every advance 1000× too small.</para>
        /// </summary>
        public double Wx { get; init; }

        /// <summary>
        /// False when the procedure declared no width at all. PLRM requires one; a font that omits
        /// it gets zero <b>and a report</b>, because an invented advance piles every glyph on one
        /// point just as silently.
        /// </summary>
        public bool WidthDeclared { get; init; }

        /// <summary>The paths the glyph paints, for <c>charpath</c> (<see cref="PsType3Outlines"/>), once read.</summary>
        public Core.Fonts.GlyphOutline Outline { get; set; }

        /// <summary>Whether <see cref="Outline"/> has been read — null is an answer too.</summary>
        public bool OutlineRead { get; set; }
    }

    /// <summary>
    /// State a glyph build carries while its procedure runs — the slot
    /// <c>setcachedevice</c> / <c>setcharwidth</c> write into, and the nesting depth.
    /// </summary>
    internal sealed class PsType3Build
    {
        public int Depth { get; init; }
        public double Wx, Wy;
        public bool WidthDeclared;

        /// <summary>
        /// True for <c>setcachedevice</c> (PDF's <c>d1</c>: shape only, the glyph paints in the
        /// text fill colour); false for <c>setcharwidth</c> (<c>d0</c>: the glyph carries its own
        /// colour).
        /// </summary>
        public bool ShapeOnly;

        /// <summary>The <c>d1</c> bounding box, when <c>setcachedevice</c> declared one.</summary>
        public double[] CacheBox;
    }

    /// <summary>
    /// A Type 3 (user-defined) font: glyphs are PostScript <b>procedures</b>, not a font program.
    ///
    /// <para>Without reading <c>/FontType</c>, such a font would fall through to the standard-14
    /// substitution and draw <b>Helvetica's letters</b> with nothing reported. Type 3 fonts carry
    /// logos, symbol sets, barcodes and bitmap fonts.</para>
    ///
    /// <para><b>Drawing them is plumbing, not a new capability</b> — which is why it is
    /// proportionate here where <c>charpath</c> outlines are not: both of this library's renderers
    /// draw PDF Type 3 fonts completely, and this hands them one. (The same argument carries
    /// <c>shfill</c>, as PDF's <c>sh</c>.)</para>
    /// </summary>
    internal sealed class PsType3Font
    {
        /// <summary>PLRM's default when a font dictionary declares no <c>/FontMatrix</c>.</summary>
        public static readonly PsMatrix DefaultGlyphMatrix = new(0.001, 0, 0, 0.001, 0, 0);

        public PsDict FontDict { get; init; }
        public PsMatrix GlyphMatrix { get; init; } = DefaultGlyphMatrix;
        public double[] FontBBox { get; init; }
        public PsDict CharProcs { get; init; }
        public PsObject BuildGlyph { get; init; }
        public PsObject BuildChar { get; init; }

        /// <summary>
        /// Code to glyph name, from the font's own <c>/Encoding</c>.
        ///
        /// <para><b>Not built by <c>EncodingDifferences</c></b>, which returns null when every slot
        /// is <c>.notdef</c> — correct for a re-encoded Type 1 and fatal here: a Type 3 font naming
        /// one glyph and leaving 255 <c>.notdef</c> would emit no <c>/Differences</c> at all, and a
        /// renderer would then resolve every code through the base encoding to a name that has no
        /// CharProc.</para>
        /// </summary>
        public IReadOnlyDictionary<int, string> Encoding { get; init; }

        /// <summary>Resources the built glyph procedures named, or null. See AddType3Font.</summary>
        public PdfDictionary GlyphResources { get; set; }

        /// <summary>The collector every glyph procedure of this font registers its resources in, once one has run.</summary>
        public PdfResourceCollector GlyphCollector { get; set; }

        /// <summary>The resource name this font was registered under, once it has been.</summary>
        public string ResourceName { get; set; }

        /// <summary>Built glyphs by character code. Memoised: a procedure runs once per code.</summary>
        public Dictionary<int, PsBuiltGlyph> Built { get; } = new();

        /// <summary>
        /// The glyph name for a code — the <b>single</b> decision, used for the CharProcs key and
        /// the <c>/Differences</c> entry alike.
        ///
        /// <para>They must come from one place: a renderer resolves code → name → CharProc, so a
        /// name in <c>/Differences</c> that CharProcs does not carry draws nothing at all.</para>
        /// </summary>
        public string GlyphNameFor(int code)
        {
            if (Encoding != null && Encoding.TryGetValue(code, out string name)) return name;

            // Deterministic, so a conversion stays byte-reproducible.
            return "g" + code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Recognises a Type 3 font dictionary, or returns false.</summary>
        public static bool TryRecognise(PsDict font, out PsType3Font type3)
        {
            type3 = null;
            if (font == null) return false;
            if (!font.TryGetName("FontType", out var t) || !t.IsNumber || t.AsInt() != 3) return false;

            type3 = new PsType3Font
            {
                FontDict    = font,
                GlyphMatrix = ReadMatrix(font),
                FontBBox    = ReadBBox(font),
                CharProcs   = font.TryGetName("CharProcs", out var cp) && cp.Type == PsType.Dictionary
                              ? cp.AsDict() : null,
                BuildGlyph  = font.TryGetName("BuildGlyph", out var bg) ? bg : PsObject.Null,
                BuildChar   = font.TryGetName("BuildChar",  out var bc) ? bc : PsObject.Null,
                Encoding    = ReadEncoding(font)
            };
            return true;
        }

        /// <summary>
        /// The key the glyph matrix is parked under once <c>definefont</c> has seen it.
        ///
        /// <para>It has to move: the interpreter's own size matrix is keyed literally
        /// <c>"FontMatrix"</c>, so <c>definefont</c> resets that slot to identity and
        /// <c>scalefont</c> then accumulates the point size into it. Reading <c>/FontMatrix</c>
        /// afterwards yields <b>the size</b> — 12, not 0.001 — which is the same collision seen from
        /// the other side.</para>
        /// </summary>
        public const string GlyphMatrixKey = "TetitulzGlyphMatrix";

        /// <summary>Parks the glyph matrix before the size-matrix slot is reset.</summary>
        public static void PreserveGlyphMatrix(PsDict font)
        {
            if (font == null || font.TryGetName(GlyphMatrixKey, out _)) return;
            if (font.TryGetName("FontMatrix", out var value) && value.Type == PsType.Array)
                font.PutName(GlyphMatrixKey, value);
        }

        private static PsMatrix ReadMatrix(PsDict font)
        {
            // The parked copy first: by the time this runs, "FontMatrix" holds the size matrix.
            if (!font.TryGetName(GlyphMatrixKey, out var value) || value.Type != PsType.Array)
                if (!font.TryGetName("FontMatrix", out value) || value.Type != PsType.Array)
                    return DefaultGlyphMatrix;

            // The non-throwing form, deliberately: a font DICTIONARY carrying a malformed
            // /FontMatrix must fall back to the default rather than kill the object that defines
            // it, where an operator handed a bad matrix must raise a catchable error. That is why
            // PsMatrix exposes both and neither wraps the other in a try/catch.
            return PsMatrix.TryFromOperands(value.AsArray(), out var matrix)
                ? matrix
                : DefaultGlyphMatrix;
        }

        private static double[] ReadBBox(PsDict font)
        {
            if (!font.TryGetName("FontBBox", out var value) || value.Type != PsType.Array) return null;

            var a = value.AsArray();
            if (a.Length < 4) return null;

            var v = new double[4];
            for (int k = 0; k < 4; k++)
            {
                if (!a[k].IsNumber) return null;
                v[k] = a[k].Num;
            }
            return v;
        }

        private static Dictionary<int, string> ReadEncoding(PsDict font)
        {
            if (!font.TryGetName("Encoding", out var value) || value.Type != PsType.Array) return null;

            var a = value.AsArray();
            var map = new Dictionary<int, string>();

            for (int code = 0; code < a.Length && code < 256; code++)
                if (a[code].Type == PsType.Name)
                {
                    string name = a[code].AsName();
                    if (!string.IsNullOrEmpty(name) && name != ".notdef") map[code] = name;
                }

            return map.Count == 0 ? null : map;
        }

        /// <summary>The PDF <c>/FontMatrix</c> this font must carry (Table 112).</summary>
        public PdfArray BuildPdfFontMatrix()
        {
            var a = new PdfArray();
            a.Add(new PdfNumber(GlyphMatrix.A));  a.Add(new PdfNumber(GlyphMatrix.B));
            a.Add(new PdfNumber(GlyphMatrix.C));  a.Add(new PdfNumber(GlyphMatrix.D));
            a.Add(new PdfNumber(GlyphMatrix.Tx)); a.Add(new PdfNumber(GlyphMatrix.Ty));
            return a;
        }

        /// <summary>The PDF <c>/FontBBox</c>, defaulting to the all-zero box §9.6.5 permits.</summary>
        public PdfArray BuildPdfFontBBox()
        {
            var a = new PdfArray();
            for (int k = 0; k < 4; k++) a.Add(new PdfNumber(FontBBox == null ? 0 : FontBBox[k]));
            return a;
        }
    }
}
