// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>
    /// Shared right-to-left / bidirectional helpers for the text-extraction operations.
    ///
    /// <para>Centralises the strong-RTL character classification and the visual to logical reading-order
    /// reconstruction reused by the structured extractor and <see cref="PdfTableExtractor"/>,
    /// so the bidi behaviour has a single source of truth (CLAUDE.md DRY rule).</para>
    /// </summary>
    internal static class TextDirection
    {
        /// <summary>Returns true when the Unicode codepoint is a strong RTL character (bidi class R or AL).</summary>
        /// <remarks>
        /// <para><b>Forwards to the classifier the reorder itself uses.</b> Ranges listed by hand
        /// would miss parts of the <c>0700–08FF</c> block <c>UnicodeBidi</c> classifies as <c>AL</c> —
        /// Syriac (U+0710), Thaana (U+0780), NKo, Samaritan, Mandaic and Arabic Extended-B would be
        /// strong RTL
        /// to Core and left-to-right here.</para>
        ///
        /// <para><b>The two answers meet inside one method</b>, which is what made it a defect
        /// rather than an inconsistency: <c>PdfStructuredTextExtractor.ExtractLines</c> picks its
        /// glyph sort direction and stamps <c>PdfTextWord.IsRtl</c> from <see cref="IsRtlGroup"/>,
        /// and then builds the line text through <see cref="ToReadingOrder(IReadOnlyList{PdfTextWord})"/>, which routes to
        /// <c>UnicodeBidi.Reorder</c>. A Syriac line came out with bidi-reordered line text beside
        /// visual-order words all reporting <c>IsRtl == false</c> — and with the extractor's default
        /// <c>ApplyBidi = false</c>, simply reversed.</para>
        /// </remarks>
        internal static bool IsRtlChar(char c) => UnicodeBidi.IsStrongRtl(c);

        /// <summary>
        /// Returns true when more than half the (non-space) characters in the group are strong RTL.
        /// </summary>
        internal static bool IsRtlGroup(IEnumerable<PdfTextChar> chars)
        {
            int total = 0, rtl = 0;
            foreach (var tc in chars)
            {
                if (tc?.Char == null) continue;
                foreach (char ch in tc.Char)
                {
                    if (ch == ' ') continue;
                    total++;
                    if (IsRtlChar(ch)) rtl++;
                }
            }
            return total > 0 && rtl * 2 > total;
        }

        /// <summary>
        /// Reconstructs the logical reading-order text for a set of glyphs laid out on a single line.
        /// Word spacing is synthesised from the horizontal gaps between glyphs (blank glyphs are not
        /// retained by the extractor, so the gaps are the only spacing signal).
        ///
        /// <para>The glyphs are first put into their physical left-to-right (visual) order, which is
        /// geometric ground truth on the page. When <paramref name="applyBidi"/> is true that visual
        /// string is run through the full Unicode bidi algorithm (<see cref="UnicodeBidi.Reorder(string)"/>);
        /// for a single line this restores logical order while keeping embedded LTR runs
        /// (numbers, part-codes, currency) intact rather than naively reversing them. When false a
        /// simpler heuristic is used: a majority-RTL group reads right-to-left, otherwise left-to-right
        /// (this reproduces the legacy structured-extractor ordering for callers that opt out of bidi).</para>
        /// </summary>
        internal static string ToReadingOrder(IReadOnlyList<PdfTextChar> chars, bool applyBidi)
        {
            if (chars == null || chars.Count == 0) return string.Empty;

            // Physical left-to-right (visual) order — independent of the order chars were supplied in.
            var visual = chars.Where(c => !string.IsNullOrEmpty(c?.Char))
                              .OrderBy(c => c.X)
                              .ToList();
            if (visual.Count == 0) return string.Empty;

            if (applyBidi)
            {
                // Feed the visual-order (display) string through the bidi algorithm. For a single line
                // this recovers logical order; embedded numeric / Latin runs are preserved by the
                // W/N/I rules rather than being reversed by a naive geometric sort.
                string drawn = BuildSpaced(visual, rtl: false, wordIndex: null, visualGlyphs: true);
                return UnicodeBidi.Reorder(drawn, UnicodeBidi.VisualLineDirection(drawn));
            }

            bool rtl = IsRtlGroup(visual);
            var ordered = rtl ? Enumerable.Reverse(visual).ToList() : visual;
            return BuildSpaced(ordered, rtl, wordIndex: null, visualGlyphs: false);
        }

        /// <summary>
        /// Reconstructs the logical reading order of words through the full Unicode bidi
        /// algorithm, with one space wherever two words meet.
        /// </summary>
        /// <remarks>
        /// <para><b>Where words meet is taken from the words, not worked out again from gaps.</b>
        /// The glyph overload puts a space only where a gap beats 0.8 of a glyph's advance, and a
        /// 12 pt space does not. Word assembly also ends a word at a space glyph the content stream
        /// drew, and its options can merge or split differently. A second rule here would disagree
        /// with the very words it was handed.</para>
        ///
        /// <para><b>Visual order is by word, then by glyph within each word.</b> Sorting every glyph by
        /// X would interleave two words that share a horizontal range, such as the lines of a
        /// wrapped table cell.</para>
        /// </remarks>
        internal static string ToReadingOrder(IReadOnlyList<PdfTextWord> words)
            => ToReadingOrder(words, BaseDirection.Auto);

        /// <summary>
        /// <see cref="ToReadingOrder(IReadOnlyList{PdfTextWord})"/> under a known paragraph
        /// direction — the direction of the line the words were taken from.
        /// </summary>
        /// <remarks>
        /// <para><b>Words taken out of a right-to-left line keep its direction.</b> Two numbers from a
        /// Hebrew line — a VAT rate and its amount — have no strong character between them, so
        /// <see cref="BaseDirection.Auto"/> reads them left to right and hands them back in the
        /// opposite order to the one the line reads them in.</para>
        /// <para><b>A word built without its glyphs reads as its text</b>, at its box: a caller's
        /// layout may hold such words, and a value found in one does not come back empty.</para>
        /// </remarks>
        internal static string ToReadingOrder(IReadOnlyList<PdfTextWord> words, BaseDirection direction)
            => ReadingOrder(words, direction);

        /// <summary>
        /// The words in reading order as part of something that reads right to left — a Hebrew
        /// line, a Hebrew table — when <paramref name="rightToLeft"/>; otherwise under the
        /// direction their own first strong character gives (<see cref="BaseDirection.Auto"/>).
        /// </summary>
        internal static string ToReadingOrder(IReadOnlyList<PdfTextWord> words, bool rightToLeft)
            => ReadingOrder(words, rightToLeft ? BaseDirection.RightToLeft : BaseDirection.Auto);

        /// <summary>
        /// The words of one drawn line in reading order when nothing names the line's paragraph: the
        /// direction is <see cref="UnicodeBidi.VisualLineDirection"/>'s — the line's ends when they
        /// agree, else the more of its strong characters — as the glyph overload takes it, where
        /// <see cref="BaseDirection.Auto"/> reads the first strong character of the visual string,
        /// the end of a right-to-left line.
        /// </summary>
        internal static string ToReadingOrderOfLine(IReadOnlyList<PdfTextWord> words)
            => ReadingOrder(words, null);

        /// <summary>Reorders the words under <paramref name="direction"/>, or under the visual line's own when null.</summary>
        private static string ReadingOrder(IReadOnlyList<PdfTextWord> words, BaseDirection? direction)
        {
            if (words == null || words.Count == 0) return string.Empty;

            var visual = new List<PdfTextChar>();
            var wordIndex = new List<int>();
            int index = 0;
            foreach (var word in words.Where(w => w != null).OrderBy(w => w.X))
            {
                var glyphs = VisualGlyphs(word);
                if (glyphs.Count == 0) continue;
                foreach (var c in glyphs)
                {
                    visual.Add(c);
                    wordIndex.Add(index);
                }
                index++;
            }
            if (visual.Count == 0) return string.Empty;

            string drawn = BuildSpaced(visual, rtl: false, wordIndex, visualGlyphs: true);
            return UnicodeBidi.Reorder(drawn, direction ?? UnicodeBidi.VisualLineDirection(drawn));
        }

        /// <summary>
        /// A word's glyphs in visual (left-to-right) order; for a word built without glyphs, one
        /// glyph standing for its text at the word's box; for a word with neither, none.
        /// </summary>
        /// <remarks>
        /// The one rule for what a word contributes to a visual line — the reading order above and
        /// the field extractor's label keys alike. Were the two to differ, a label could be found in a
        /// word's text and its value read from the same word's glyphs, of which it has none.
        /// </remarks>
        internal static List<PdfTextChar> VisualGlyphs(PdfTextWord word)
        {
            var glyphs = word.Chars?.Where(c => !string.IsNullOrEmpty(c?.Char)).OrderBy(c => c.X).ToList();
            if (glyphs != null && glyphs.Count > 0) return glyphs;
            return string.IsNullOrEmpty(word.Text)
                ? new List<PdfTextChar>()
                : new List<PdfTextChar> { new PdfTextChar(word.Text, word.X, word.Y, word.Width, word.Height, word.IsRtl) };
        }

        /// <summary>Whether <paramref name="word"/> has glyphs of its own (see <see cref="VisualGlyphs"/>).</summary>
        internal static bool HasOwnGlyphs(PdfTextWord word)
            => word.Chars != null && word.Chars.Any(c => !string.IsNullOrEmpty(c?.Char));

        // Concatenate the glyph strings with a single space between words. With `wordIndex` a word
        // ends where the index changes; without it, wherever the physical gap between adjacent glyphs
        // exceeds ~80% of the preceding glyph's advance (the gap half of WordAssemblyOptions'
        // word-boundary rule). `visualGlyphs` when the string is a visual line the bidi algorithm will
        // reverse: a glyph of several characters — a ligature, a letter with its point — is then
        // appended through UnicodeBidi.InVisualOrder, so the reversal gives it back in logical order.
        private static string BuildSpaced(IReadOnlyList<PdfTextChar> ordered, bool rtl, IReadOnlyList<int> wordIndex,
                                          bool visualGlyphs)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < ordered.Count; i++)
            {
                if (i > 0)
                {
                    bool boundary;
                    if (wordIndex != null)
                        boundary = wordIndex[i] != wordIndex[i - 1];
                    else
                    {
                        var prev = ordered[i - 1];
                        var cur  = ordered[i];
                        float gap = rtl
                            ? prev.X - (cur.X + cur.Width)   // descending order: prev sits to the right
                            : cur.X - (prev.X + prev.Width); // ascending order:  prev sits to the left
                        float threshold = prev.Width > 0f
                            ? prev.Width * 0.8f
                            : Math.Max(prev.Height * 0.25f, 1f);
                        boundary = gap > threshold;
                    }
                    if (boundary) sb.Append(' ');
                }
                sb.Append(visualGlyphs ? VisualText(ordered[i]) : ordered[i].Char);
            }
            return sb.ToString();
        }

        /// <summary>
        /// A glyph's text as it goes into a visual line the bidi algorithm will reorder: its
        /// characters in visual order (<see cref="UnicodeBidi.InVisualOrder"/>), and, drawn inside a
        /// <c>/ReversedChars</c> run, its brackets as drawn (<see cref="UnicodeBidi.AsDrawnInReversedRun"/>).
        /// </summary>
        /// <remarks>
        /// The one rule for both builders of a visual line — the reading order here and the field
        /// extractor's label keys — so a bracket a Chromium-made Hebrew line draws reads the same way
        /// round in the line's text and in its keys.
        /// </remarks>
        internal static string VisualText(PdfTextChar glyph)
            => UnicodeBidi.InVisualOrder(glyph.InReversedRun ? UnicodeBidi.AsDrawnInReversedRun(glyph.Char) : glyph.Char);
    }
}
