// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    /// <summary>
    /// The DOCX renderer's adapter onto <see cref="TextRunComposer"/>: which runs need an embedded
    /// font, which code points one has to cover, and the visual-order glyph run to emit as
    /// Identity-H <c>&lt;GID …&gt; Tj</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>The shape → bidi → glyph-id pipeline itself lives in Core</b>
    /// (<see cref="TextRunComposer.Compose"/>), shared with <c>Builder.Export.RtlText</c>, so both
    /// get the <b>code-point</b> walk (a supplementary-plane character is one glyph, not two lone
    /// surrogates) and the caller-supplied <see cref="BaseDirection"/>.</para>
    /// <para><b>What stays here is what is genuinely DOCX-shaped:</b> <see cref="NeedsUnicode"/> /
    /// <see cref="IsWinAnsi"/> ask which <i>encoding</i> to write, not which glyphs exist — folding
    /// them into the Latin-1 test the Builder side uses would change which font is picked for
    /// CP1252 text — and <see cref="CodePoints"/> answers a coverage question that is asked before
    /// any font has been resolved, so it cannot come out of a composed run.</para>
    /// </remarks>
    internal static class UnicodeText
    {
        internal sealed class GlyphRun
        {
            public ushort[] Gids;
            /// <summary>Where each glyph is drawn — the font's mark attachment — or <c>null</c>.</summary>
            public IReadOnlyList<GlyphPlacement> Placements;
            public List<(ushort gid, int codepoint)> Used = new List<(ushort, int)>();
            /// <summary>Each ligature glyph drawn and the letters it stands for, for <c>/ToUnicode</c>.</summary>
            public List<(ushort gid, string text)> UsedText = new List<(ushort, string)>();
            public double WidthPts;

            /// <summary>Records the run's glyphs into a font entry's registers.</summary>
            public void RecordInto(Dictionary<ushort, int> gidToUnicode, Dictionary<ushort, string> gidToText)
            {
                foreach (var (gid, cp) in Used) gidToUnicode[gid] = cp;
                foreach (var (gid, text) in UsedText) gidToText[gid] = text;
            }
        }

        /// <summary>
        /// The show-text operators for <paramref name="run"/> after a <c>Tf</c> of
        /// <paramref name="fontSizePts"/> written to two decimals, as every site here writes it —
        /// <c>&lt;…&gt; Tj</c>, or the placed form a run with mark attachment needs
        /// (<see cref="GlyphRunShowText"/>). No trailing line break.
        /// </summary>
        internal static string ShowText(GlyphRun run, double fontSizePts)
            => GlyphRunShowText.Operators(run.Gids, run.Placements, System.Math.Round(fontSizePts, 2));

        /// <summary>Returns true when text has a character that WinAnsi can't represent.</summary>
        internal static bool NeedsUnicode(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
                if (!IsWinAnsi(c)) return true;
            return false;
        }

        private static bool IsWinAnsi(char c)
            // '\t' / '\n' are structural (split out as tab / line-break segments), so they
            // must not force the embedded-Unicode path for otherwise-WinAnsi text.
            => c == '\t' || c == '\n' || (c >= 0x20 && c < 0x7F) || (c >= 0xA0 && c <= 0xFF) || RunRenderer.IsCp1252High(c);

        /// <summary>
        /// The real Unicode code points of <paramref name="text"/> — a well-formed surrogate pair
        /// yields <b>one</b> value, not two.
        /// </summary>
        /// <remarks>
        /// Code points, not UTF-16 code <i>units</i>: by units, every supplementary-plane character — an
        /// emoji, a CJK ext-B ideograph, the U+1D400 mathematical alphanumerics Word's equation
        /// editor writes straight into <c>m:t</c> — would ask every font on the machine to
        /// cover U+D835 and U+DC65, code points that do not exist as characters, so coverage could
        /// never succeed, and the run would be dropped with the diagnostic naming the lone surrogates.
        /// <para>An unpaired surrogate is passed through as itself, so malformed input still
        /// terminates and is still reported (under its own value, which is the honest one).</para>
        /// <para><b>This is what the text IS.</b> Choosing a font asks what the drawing NEEDS, which
        /// is <see cref="CodePointsToCover"/> — the shaped run's code points — so every font
        /// resolution here goes through that instead. This stays as the source-text reading the
        /// surrogate-pair tests pin.</para>
        /// </remarks>
        internal static IEnumerable<int> CodePoints(string text)
        {
            string s = text ?? "";
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    yield return char.ConvertToUtf32(s[i], s[i + 1]);
                    i++;
                }
                else yield return s[i];
            }
        }

        /// <summary>
        /// The code points a font must cover to draw <paramref name="text"/> — the shaped run's,
        /// through <see cref="TextRunComposer.CodePointsToCover"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="CodePoints"/> answers what the text <i>is</i>, which is the right question for
        /// "does this need an embedded font at all"; this answers what the drawing <i>needs</i>,
        /// which is the right one for choosing that font. Resolved by the first, a font covering
        /// Arabic letters but not their presentation forms was selected and every glyph of the run
        /// then came out as a space.
        /// </remarks>
        internal static IEnumerable<int> CodePointsToCover(string text)
            => TextRunComposer.CodePointsToCover(text);

        /// <summary>
        /// Advance width of <paramref name="text"/> in the font — measured over the <b>shaped</b>
        /// glyphs, so it agrees with what <see cref="Shape"/> will actually draw.
        /// </summary>
        /// <remarks>
        /// The shaped run, not the raw characters. For Arabic those are a different set of glyphs
        /// with different advances (a medial form is typically narrower than the isolated one), so
        /// measuring the raw characters, <c>ParagraphRenderer</c> would compare a line's accumulated
        /// <i>shaped</i> width against an <i>unshaped</i> word width — two units in one comparison —
        /// and break Arabic lines in the wrong place. Latin and Hebrew are unaffected: neither is substituted, so shaping is the
        /// identity and the measurement is byte-for-byte what it was.
        /// <para>It now runs the <b>same</b> pass <see cref="Shape"/> does — see
        /// <see cref="Compose"/> — so the agreement is structural rather than two walks that were
        /// written to match. Reordering cannot change the sum of a run's advances, so the widths are
        /// what they were.</para>
        /// </remarks>
        internal static double MeasureWidth(string text, TrueTypeFontProgram font, double fontSizePts, bool kerning = false,
                                            bool contextualAlternates = false, bool ligatures = false)
        {
            if (string.IsNullOrEmpty(text) || font == null) return 0;
            return Compose(text, font, kerning, contextualAlternates, ligatures).WidthAt(fontSizePts);
        }

        /// <summary>
        /// <paramref name="text"/> shaped and ordered for drawing. The direction is
        /// <see cref="BaseDirection.Auto"/> where the caller has none to give; the line's bidi pass
        /// gives a one-level piece its level's, so a piece of neutrals at an odd level is drawn
        /// right to left and mirrored — and, for a piece inside a <c>w:bdo</c>, the override, which
        /// draws its characters in that direction whatever their own.
        /// </summary>
        internal static GlyphRun Shape(string text, TrueTypeFontProgram font, double fontSizePts, bool kerning = false,
                                       bool contextualAlternates = false, bool ligatures = false,
                                       BaseDirection direction = BaseDirection.Auto, bool directionalOverride = false)
        {
            var run = new GlyphRun();
            if (string.IsNullOrEmpty(text) || font == null) { run.Gids = System.Array.Empty<ushort>(); return run; }

            var composed = Compose(text, font, kerning, contextualAlternates, ligatures, direction, directionalOverride);
            run.Gids = composed.GlyphIds;
            run.Placements = composed.Placements;
            // /ToUnicode gets the BASE letter, not the presentation form that was drawn: a reader
            // searching for بيت must find the word that is visibly on the page, and copy-paste must
            // not yield U+FE70-block code points no keyboard produces. GlyphMapping.CodePoint is
            // already that letter — and a ligature glyph's Text the letters it joined.
            foreach (var glyph in composed.Glyphs)
            {
                run.Used.Add((glyph.GlyphId, glyph.CodePoint));
                if (glyph.Text != null) run.UsedText.Add((glyph.GlyphId, glyph.Text));
            }
            run.WidthPts = composed.WidthAt(fontSizePts);
            return run;
        }

        /// <summary>
        /// The one shape → bidi → glyph-id pass <see cref="Shape"/> and <see cref="MeasureWidth"/>
        /// share, so a line's accumulated width and the glyphs finally drawn cannot be two
        /// different things.
        /// </summary>
        /// <remarks>
        /// <para><see cref="BaseDirection.Auto"/> is the default: a run measured for wrapping has no
        /// line yet, so no level. The direction is given only by the line's bidi pass
        /// (<c>ParagraphRenderer.InVisualOrder</c>), which re-shapes each one-level piece of a line
        /// in its level's direction.</para>
        /// <para><see cref="MissingGlyphPolicy.SubstituteSpace"/> reproduces this renderer's
        /// historical output for an uncovered code point — a space — while making the substitution
        /// visible on <see cref="TextRunComposer.LastCoverageError"/> instead of silent.</para>
        /// <para>Measuring now reorders as well as shapes, which the previous split did not. It
        /// cannot change a width (reordering is a permutation), and it is what makes the agreement
        /// structural rather than a property two walks happen to share.</para>
        /// </remarks>
        private static ShapedGlyphRun Compose(string text, TrueTypeFontProgram font, bool kerning, bool contextualAlternates,
                                              bool ligatures, BaseDirection direction = BaseDirection.Auto,
                                              bool directionalOverride = false)
            => TextRunComposer.Compose(text, font, new TextRunOptions
            {
                BaseDirection = direction,
                DirectionalOverride = directionalOverride,
                MissingGlyphPolicy = MissingGlyphPolicy.SubstituteSpace,
                Kerning = kerning,
                ContextualAlternates = contextualAlternates,
                Ligatures = ligatures,
            });

        /// <summary>
        /// Splits a run that will be shaped into the pieces a line wrap lays down: the words that
        /// fit on one line, with the spaces between them as written, each piece to be <b>shaped as
        /// one unit</b>.
        /// </summary>
        /// <param name="text">The run's text, in logical order.</param>
        /// <param name="width">The drawn width of a candidate string under the run's style —
        /// shaped, scaled and tracked, so the wrap and the page measure the same thing.</param>
        /// <param name="lineWidth">Width already on the line this run starts on.</param>
        /// <param name="lineHasContent">Whether that line holds anything yet.</param>
        /// <param name="maxWidth">The line's width.</param>
        /// <returns>
        /// Each piece with <c>BreakBefore</c> set when the caller must end the current line before
        /// laying it down.
        /// </returns>
        /// <remarks>
        /// <para><b>Why a piece and not a word.</b> The caller draws a line's segments left to right
        /// in list order. One shaped piece per line is already in visual order, because bidi ordered
        /// the whole piece; a segment per <i>word</i> would put the first word leftmost and reverse
        /// every right-to-left line. So a table cell wraps by pieces too, never by per-word
        /// segments.</para>
        /// <para><b>One implementation for the body and the cell.</b> The body paragraph had this loop
        /// inline and the cell had none; a second copy is how the two came to measure differently
        /// in the first place. A word wider than the whole line is not split — breaking a joined
        /// script between characters changes its shaping — so it gets a line of its own and
        /// overflows, as it does in a body paragraph.</para>
        /// </remarks>
        internal static List<(string Text, bool BreakBefore)> WrapWords(
            string text, System.Func<string, double> width,
            double lineWidth, bool lineHasContent, double maxWidth)
        {
            var pieces = new List<(string, bool)>();
            if (string.IsNullOrEmpty(text)) return pieces;

            double spaceWidth = width(" ");
            double SpacesWidth(string spaces) => spaces.Length == 0 ? 0 : spaces.Length == 1 ? spaceWidth : width(spaces);
            var piece = new System.Text.StringBuilder();
            double pieceWidth = 0;
            bool breakBefore = false;

            foreach (var (spaces, token) in SpacedWords(text))
            {
                if (token.Length == 0) { piece.Append(spaces); break; }   // spaces closing the run stay with it

                double wordWidth = width(token);
                double withSpace = SpacesWidth(spaces) + wordWidth;

                if ((lineHasContent || piece.Length > 0) && lineWidth + pieceWidth + withSpace > maxWidth)
                {
                    if (piece.Length > 0) pieces.Add((piece.ToString(), breakBefore));
                    // The line is full, whether it was filled by this run or by what preceded it.
                    // lineHasContent is not reset: from here on `piece` is never empty, so the
                    // condition above no longer reads it. The spaces at the break are dropped.
                    breakBefore = true;
                    lineWidth = 0;
                    piece.Clear().Append(token);
                    pieceWidth = wordWidth;
                }
                else
                {
                    piece.Append(spaces).Append(token);
                    pieceWidth += withSpace;
                }
            }

            if (piece.Length > 0) pieces.Add((piece.ToString(), breakBefore));
            return pieces;
        }

        /// <summary>
        /// <paramref name="text"/>'s words in order, each with the spaces before it exactly as
        /// written; spaces closing the text come last, with an empty word. The one tokenizer of
        /// the legacy renderer's three wraps — a shaped run's, and a standard-14 run's in a body
        /// paragraph and in a cell.
        /// </summary>
        /// <remarks>
        /// Each wrap rebuilt a run from <c>Split(' ')</c>'s words, dropping the spaces before the
        /// first: "Hello" + " World" drew "HelloWorld", a run of nothing but a space vanished, and
        /// a shaped run's double spaces became one. A wrap now drops only the spaces a line break
        /// takes.
        /// </remarks>
        internal static IEnumerable<(string Spaces, string Word)> SpacedWords(string text)
        {
            string spaces = "";
            for (int pos = 0; pos < text.Length;)
            {
                int end = pos;
                bool isSpace = text[pos] == ' ';
                while (end < text.Length && (text[end] == ' ') == isSpace) end++;
                string token = text.Substring(pos, end - pos);
                pos = end;
                if (isSpace) spaces = token;
                else { yield return (spaces, token); spaces = ""; }
            }
            if (spaces.Length > 0) yield return (spaces, "");
        }
    }
}
