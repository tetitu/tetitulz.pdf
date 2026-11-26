// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    internal sealed class RunRenderer
    {
        internal RunRenderResult Render(Run run, Paragraph paragraph, DocxRenderContext context)
        {
            var resolver = context.StyleResolver;
            var text = new StringBuilder();

            // Hidden text (w:vanish) is left out of print and PDF, its breaks with it.
            if (resolver.ResolveVanish(run, paragraph)) return new RunRenderResult { Text = "" };

            foreach (var child in run.ChildElements)
            {
                if (child is Text t)
                {
                    text.Append(t.Text);
                }
                else if (child is Break br)
                {
                    if (br.Type?.Value == BreakValues.Page)
                    {
                        return new RunRenderResult { Text = text.ToString(), PageBreak = true };
                    }
                    else if (br.Type?.Value == BreakValues.Column)
                    {
                        return new RunRenderResult { Text = text.ToString(), ColumnBreak = true };
                    }
                    else
                    {
                        // Line break
                        text.Append('\n');
                    }
                }
                else if (child is TabChar)
                {
                    text.Append('\t'); // a real tab — positioned to the next stop at layout time
                }
                else
                {
                    switch (RunContent.Classify(child, out string stands))
                    {
                        case RunContent.Kind.Text: text.Append(stands); break;
                        case RunContent.Kind.LineBreak: text.Append('\n'); break;
                        case RunContent.Kind.Tab: text.Append('\t'); break;
                    }
                }
            }

            var fontSize = resolver.ResolveFontSizeInPoints(run, paragraph, context.Options.DefaultFontSize);
            var isBold = resolver.ResolveIsBold(run, paragraph);
            var isItalic = resolver.ResolveIsItalic(run, paragraph);
            var color = resolver.ResolveColor(run, paragraph);
            bool underline = resolver.ResolveUnderline(run, paragraph);
            bool strike = resolver.ResolveStrike(run, paragraph);
            string highlight = resolver.ResolveHighlight(run, paragraph);
            int vertAlign = resolver.ResolveVertAlign(run, paragraph);
            // w:caps draws the run in capitals; w:smallCaps draws its lowercase letters as smaller
            // capitals, cut into pieces by FlushRuns on the standard path, and as capitals of the
            // run's size in an embedded font.
            bool caps = resolver.ResolveCaps(run, paragraph);
            bool smallCaps = !caps && resolver.ResolveSmallCaps(run, paragraph);
            string textStr = caps ? text.ToString().ToUpperInvariant() : text.ToString();

            // Sub/superscript: reduce size and offset the baseline. w:position adds
            // an explicit baseline shift; w:spacing/w:w set inter-char spacing / scale.
            double drawSize = vertAlign != 0 ? fontSize * DocxLayoutMetrics.VertAlignFontScale : fontSize;
            double baselineShift = vertAlign == 1 ? fontSize * DocxLayoutMetrics.SuperscriptRiseFactor
                : (vertAlign == -1 ? -fontSize * DocxLayoutMetrics.SubscriptDropFactor : 0);
            baselineShift += resolver.ResolveTextPosition(run, paragraph);
            double charSpacing = resolver.ResolveCharacterSpacing(run, paragraph);
            int textScale = resolver.ResolveTextScale(run, paragraph);
            bool kerning = resolver.ResolveKerning(run, paragraph, fontSize);
            bool contextualAlternates = resolver.ResolveContextualAlternates(run, paragraph);
            bool ligatures = resolver.ResolveLigatures(run, paragraph);

            // Non-WinAnsi text (Hebrew, Arabic, CJK, …) needs an embedded Unicode
            // font drawn as Identity-H glyphs, otherwise it would be dropped to spaces.
            // (IsCjkText is defined at the bottom of this class.)
            if (UnicodeText.NeedsUnicode(textStr))
            {
                // CJK text prefers the @eastAsia family; other complex scripts use @cs.
                var csFamily = IsCjkText(textStr)
                    ? (resolver.ResolveEastAsiaFontName(run, paragraph) ?? resolver.ResolveComplexScriptFontName(run, paragraph))
                    : resolver.ResolveComplexScriptFontName(run, paragraph);
                var cps = UnicodeText.CodePointsToCover(textStr);
                // Prefer a font embedded in the DOCX itself; fall back to an OS font.
                var program = context.ResolveEmbeddedFont(csFamily, isBold, isItalic, cps)
                              ?? context.ResolveHostFont(csFamily, isBold, isItalic, cps);
                if (program != null)
                {
                    // Defer shaping to wrap time so a long RTL/complex run can be
                    // broken into lines (each line is then bidi-reordered + shaped).
                    var entry = context.RegisterEmbeddedFont(program);
                    return new RunRenderResult
                    {
                        Text = smallCaps ? textStr.ToUpperInvariant() : textStr,
                        IsUnicode = true,
                        Program = program,
                        FontEntry = entry,
                        FontSize = drawSize,
                        Color = color,
                        Underline = underline,
                        Strike = strike,
                        Highlight = highlight,
                        BaselineShift = baselineShift,
                        CharSpacing = charSpacing,
                        TextScale = textScale,
                        Kerning = kerning,
                        ContextualAlternates = contextualAlternates,
                        Ligatures = ligatures,
                        PageBreak = false,
                    };
                }

                // Nothing on the machine, and nothing embedded in the DOCX, covers this text.
                // Execution falls through to the standard-14 path below, where
                // EncodeWinAnsiLiteral maps every unrepresentable character to a space — so a
                // Hebrew or CJK document converted on a bare container produced correctly-sized
                // BLANK lines. Report before falling through: a page of spaces is otherwise
                // indistinguishable from a document that really contained none.
                ReportNoCoveringFont("RunRenderer.NoCoveringFont", csFamily, textStr);
            }

            var fontName = resolver.ResolveFontName(run, paragraph);
            var pdfFontName = resolver.MapToPdfFontName(fontName, isBold, isItalic);

            return new RunRenderResult
            {
                Text = textStr,
                SmallCaps = smallCaps,
                PdfFontName = pdfFontName,
                FontSize = drawSize,
                Color = color,
                Underline = underline,
                Strike = strike,
                Highlight = highlight,
                BaselineShift = baselineShift,
                CharSpacing = charSpacing,
                TextScale = textScale,
                PageBreak = false
            };
        }

        /// <summary>
        /// Reports text that no available font covers, naming the family that was asked for and
        /// the first few code points that could not be represented.
        ///
        /// <para>
        /// Shared by <see cref="Render"/> and <c>TableRenderer.WrapParagraph</c>, which had the
        /// identical silent fall-through — one message, so a body run and a cell run cannot report
        /// the loss differently. Code points, not UTF-16 units: naming "U+D835" for an emoji would
        /// point the reader at a value that is not a character.
        /// </para>
        /// </summary>
        internal static void ReportNoCoveringFont(string operation, string family, string text)
        {
            var uncovered = UnicodeText.CodePointsToCover(text)
                .Where(cp => !IsWinAnsiCodePoint(cp))
                .Distinct()
                .Take(4)
                .Select(cp => "U+" + cp.ToString("X4", CultureInfo.InvariantCulture));

            DocxDiagnostics.Report(operation, string.Concat(
                "no available font covers ", string.Join(", ", uncovered),
                " for the family '", family ?? "(unspecified)",
                "'; those characters were drawn as blanks"));
        }

        private static bool IsWinAnsiCodePoint(int cp)
            => cp == '\t' || cp == '\n'
               || (cp >= 0x20 && cp < 0x7F) || (cp >= 0xA0 && cp <= 0xFF)
               || (cp <= 0xFFFF && IsCp1252High((char)cp));

        /// <summary>
        /// Escapes a PDF literal string AND transcodes it to WinAnsi (CP1252), for a stream that
        /// declares <c>/WinAnsiEncoding</c>.
        ///
        /// <para><b>Deliberately not routed through <c>Core.Objects.PdfStringEscape</c></b>, which
        /// every other literal-string escaper shares. This one is not an escaper:
        /// it is an escaper plus a single-byte encoder, and the canonical would emit UTF-16 units
        /// where this emits CP1252 bytes, turning every smart quote into mojibake. Its name does
        /// not answer a search for the thing it is not.</para>
        /// </summary>
        internal static string EncodeWinAnsiLiteral(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var sb = new StringBuilder(text.Length + 10);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '(': sb.Append("\\("); break;
                    case ')': sb.Append("\\)"); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    default:
                        if (c >= 32 && c < 127)
                            sb.Append(c);                 // ASCII == WinAnsi
                        else if (c >= 0xA0 && c <= 0xFF)
                            sb.Append(c);                 // Latin-1 range == WinAnsi byte (é, ü, ñ, …)
                        else if (_cp1252Highs.TryGetValue(c, out byte b))
                            sb.Append((char)b);           // CP1252 0x80-0x9F (smart quotes, –, —, €, …)
                        else
                            sb.Append(' ');               // unrepresentable in WinAnsi (CJK, Hebrew, …)
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>True for the CP1252 0x80-0x9F typographic characters (smart quotes, dashes…).</summary>
        internal static bool IsCp1252High(char c) => _cp1252Highs.ContainsKey(c);

        // True if the text contains CJK ideographs / kana / hangul (selects the @eastAsia font).
        private static bool IsCjkText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
            {
                if ((c >= 0x4E00 && c <= 0x9FFF)   // CJK Unified Ideographs
                    || (c >= 0x3400 && c <= 0x4DBF) // Extension A
                    || (c >= 0x3040 && c <= 0x30FF) // Hiragana + Katakana
                    || (c >= 0xAC00 && c <= 0xD7AF)) // Hangul syllables
                    return true;
            }
            return false;
        }

        // Unicode → WinAnsi (CP1252) byte for the 0x80-0x9F range, where the byte
        // value differs from the code point. The fonts are written with
        // /Encoding /WinAnsiEncoding and the content stream is emitted as Latin-1,
        // so appending (char)byte renders the right glyph. Covers the common
        // typographic characters Word produces (curly quotes, dashes, ellipsis…).
        private static readonly System.Collections.Generic.Dictionary<char, byte> _cp1252Highs =
            new System.Collections.Generic.Dictionary<char, byte>
            {
                ['€'] = 0x80, ['‚'] = 0x82, ['ƒ'] = 0x83, ['„'] = 0x84,
                ['…'] = 0x85, ['†'] = 0x86, ['‡'] = 0x87, ['ˆ'] = 0x88,
                ['‰'] = 0x89, ['Š'] = 0x8A, ['‹'] = 0x8B, ['Œ'] = 0x8C,
                ['Ž'] = 0x8E, ['‘'] = 0x91, ['’'] = 0x92, ['“'] = 0x93,
                ['”'] = 0x94, ['•'] = 0x95, ['–'] = 0x96, ['—'] = 0x97,
                ['˜'] = 0x98, ['™'] = 0x99, ['š'] = 0x9A, ['›'] = 0x9B,
                ['œ'] = 0x9C, ['ž'] = 0x9E, ['Ÿ'] = 0x9F,
            };

        /// <summary>
        /// Appends the non-stroking colour operator for <paramref name="hex"/> (OOXML RRGGBB).
        /// </summary>
        /// <remarks>
        /// <b>An unparseable value paints BLACK.</b> Guarding only null (<c>hex ?? "000000"</c>)
        /// and skipping the append when <see cref="TryParseHexColor"/> fails would leave the
        /// PREVIOUS colour operator in force for a malformed value, and the shape would draw in the
        /// last thing painted's colour rather than in a wrong-but-obvious one. The live route is the
        /// theme palette: <c>StyleResolver.LoadThemeColors</c> stores the theme accent from
        /// theme1.xml unvalidated and <c>ChartLayout.PaletteAt</c> hands it straight here, so a theme
        /// part whose accent is not six hex digits would draw one chart series in the previous
        /// series' colour.
        /// Black is what the null guard already intended and what OOXML "automatic" means.
        /// </remarks>
        internal static void AppendFill(StringBuilder ops, string hex)
        {
            ToRgb(hex, out double r, out double g, out double b);
            ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F3} {1:F3} {2:F3} rg\n", r, g, b);
        }

        /// <summary>
        /// Appends the stroking colour operator for <paramref name="hex"/> and the line width.
        /// Same black fallback as <see cref="AppendFill"/>.
        /// </summary>
        internal static void AppendStroke(StringBuilder ops, string hex, double widthPt)
        {
            ToRgb(hex, out double r, out double g, out double b);
            ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F3} {1:F3} {2:F3} RG\n{3:F2} w\n",
                r, g, b, widthPt);
        }

        /// <summary>The 0..1 components of an OOXML hex colour; black when it will not parse.</summary>
        private static void ToRgb(string hex, out double r, out double g, out double b)
        {
            if (!TryParseHexColor(hex, out int ri, out int gi, out int bi)) ri = gi = bi = 0;
            r = ri / 255.0; g = gi / 255.0; b = bi / 255.0;
        }

        /// <summary>
        /// <paramref name="width"/> adjusted for the run's <c>w:w</c> scale (emitted as <c>Tz</c>)
        /// and <c>w:spacing</c> tracking (emitted as <c>Tc</c>).
        /// </summary>
        /// <param name="width">The measured width, before either attribute.</param>
        /// <param name="text">The text being measured; null skips the per-character tracking.</param>
        /// <param name="textScale">Horizontal scale percent; 100 is unscaled.</param>
        /// <param name="charSpacing">Inter-character spacing in points.</param>
        /// <remarks>
        /// <para><b>One definition, because the body and the table path drew the same run at
        /// different widths.</b> A paragraph honoured both attributes when measuring and when
        /// emitting; a table cell read neither, so the same 200%-scaled run was laid out at half
        /// the width it should occupy and a tracked run lost its tracking entirely — the same
        /// document, two answers, depending only on whether the text sat inside a cell.</para>
        ///
        /// <para>The tracking is added per character rather than per gap, matching what <c>Tc</c>
        /// actually does: PDF applies it after every glyph, including the last.</para>
        /// </remarks>
        internal static double WithRunSpacing(double width, string text, int textScale, double charSpacing)
        {
            if (textScale != 100) width *= textScale / 100.0;
            if (charSpacing != 0 && text != null) width += charSpacing * text.Length;
            return width;
        }

        /// <summary>
        /// The <c>Tc</c> a run is drawn with so that its <c>w:spacing</c> lands on the page as the
        /// absolute pitch <see cref="WithRunSpacing"/> measured: the spacing divided by the run's
        /// <c>w:w</c> scale factor, because PDF scales <c>Tc</c> by <c>Tz</c> and Word does not.
        /// </summary>
        /// <param name="charSpacing">The run's <c>w:spacing</c> in points.</param>
        /// <param name="textScale">The run's <c>w:w</c> percent; 100 is unscaled.</param>
        /// <remarks>
        /// <para><b>Two specifications, one on each side of the operator.</b> ISO/IEC 29500-1
        /// §17.3.2.35 (<c>spacing</c>) is "the amount of character pitch which shall be added or
        /// removed after each character", in twentieths of a point — an absolute distance; and
        /// §17.3.2.43 (<c>w</c>) stretches or compresses "each character in this run", "as opposed to
        /// the <c>spacing</c> element, which expands or compresses the text by adding additional
        /// character pitch but not changing the width of the actual characters". ISO 32000-2 §9.3.4
        /// has horizontal scaling "also affect the spacing parameters <c>Tc</c> and <c>Tw</c>": a
        /// run drawn with <c>Tz</c> and <c>Tc</c> advances (width + Tc·n)·Th. So the emitter writes
        /// Tc = spacing ÷ Th, and the drawn advance is width·Th + spacing·n — the measurement.</para>
        /// <para><b>Every emitter, including the Builder path.</b> The body paragraph, the table
        /// cell and <c>DocxToBuilderMapper</c> each hand the run's spacing on; the Builder's
        /// <c>TextRun.CharSpacing</c> is PDF's <c>Tc</c> (scaled by its <c>HorizontalScale</c> in
        /// its measurement too), so the mapper divides here before handing it over.</para>
        /// <para>A scale of 0 or less is not Word's (1–600) and would divide by nothing; the
        /// spacing is passed through unchanged.</para>
        /// </remarks>
        internal static double EmittedCharSpacing(double charSpacing, int textScale)
        {
            if (charSpacing == 0 || textScale == 100 || textScale <= 0) return charSpacing;
            return charSpacing * 100.0 / textScale;
        }

        internal static string FormatColorOperator(string hexColor)
        {
            if (!TryParseHexColor(hexColor, out int r, out int g, out int b))
                return null;

            return string.Format(CultureInfo.InvariantCulture,
                "{0:F3} {1:F3} {2:F3} rg\n",
                r / 255.0, g / 255.0, b / 255.0);
        }

        /// <summary>
        /// An OOXML RRGGBB colour (<c>ST_HexColorRGB</c>: <c>w:color/@w:val</c>, <c>w:shd/@w:fill</c>,
        /// DrawingML's <c>a:srgbClr/@val</c>) — exactly six ASCII hex digits; false for anything else ("auto",
        /// a theme name, malformed data), so a single bad attribute can't throw and abort the whole conversion.
        /// </summary>
        /// <remarks>
        /// The one reader. The run renderer read the first six characters of a longer value, and both it and
        /// the drawing reader let <c>NumberStyles.HexNumber</c>'s surrounding white space through, each its own
        /// way — two answers to one question.
        /// </remarks>
        internal static bool TryParseHexColor(string hexColor, out int r, out int g, out int b)
        {
            r = g = b = 0;
            if (hexColor == null || hexColor.Length != 6) return false;
            int v = 0;
            foreach (char c in hexColor)
            {
                int nibble = c >= '0' && c <= '9' ? c - '0'
                           : c >= 'a' && c <= 'f' ? c - 'a' + 10
                           : c >= 'A' && c <= 'F' ? c - 'A' + 10
                           : -1;
                if (nibble < 0) return false;
                v = (v << 4) | nibble;
            }
            r = (v >> 16) & 0xFF; g = (v >> 8) & 0xFF; b = v & 0xFF;
            return true;
        }
    }

    internal sealed class RunRenderResult
    {
        public string Text { get; set; }
        public string PdfFontName { get; set; }
        public double FontSize { get; set; }
        public string Color { get; set; }
        public bool PageBreak { get; set; }
        public bool ColumnBreak { get; set; }

        /// <summary>
        /// A typeset equation. When set, this result is an atomic box rather than a run of text —
        /// see <c>MathTypesetting</c>.
        /// </summary>
        public MathTypesetting.MathBox MathBox { get; set; }

        /// <summary>
        /// A bidi control a <c>w:dir</c> or <c>w:bdo</c> stands for, its one character in
        /// <see cref="Text"/>: ordered by the line's bidi pass (<c>LineBidi</c>), never drawn.
        /// </summary>
        public bool IsDirectionControl { get; set; }

        /// <summary>
        /// <c>w:smallCaps</c> on a standard-face run: <see cref="Text"/> keeps its case, and the line
        /// layout draws each lowercase letter as its capital at <see cref="DocxLayoutMetrics.SmallCapsScale"/>
        /// of <see cref="FontSize"/>.
        /// </summary>
        public bool SmallCaps { get; set; }

        // Set when the run is drawn with an embedded Unicode font (Identity-H glyphs).
        // Shaping is deferred to wrap time, so the run carries its font program + entry.
        public bool IsUnicode { get; set; }
        public Tetitulz.Pdf.Core.Fonts.TrueTypeFontProgram Program { get; set; }
        public DocxRenderContext.EmbeddedFontEntry FontEntry { get; set; }

        // Decorations / positioning.
        public bool Underline { get; set; }
        public bool Strike { get; set; }
        public string Highlight { get; set; }   // RRGGBB hex or null
        public double BaselineShift { get; set; } // + up (superscript), - down (subscript)
        public double CharSpacing { get; set; }  // inter-character spacing, points (Tc)
        public int TextScale { get; set; } = 100;   // horizontal scale percent (Tz)
        public bool Kerning { get; set; }        // w:kern applies at this size (embedded-font runs)
        public bool ContextualAlternates { get; set; }   // w14:cntxtAlts: the font's calt (embedded-font runs)
        public bool Ligatures { get; set; }              // w14:ligatures: the font's liga (embedded-font runs)
        public string LinkUri { get; set; }     // hyperlink target, or null
    }
}
