// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Accessibility.Internal
{
    /// <summary>
    /// Decides which of a page's lines are list items, from the marker each begins with and the
    /// left edge they share.
    /// </summary>
    /// <remarks>
    /// <para><b>Two signals, and both are needed.</b> A marker alone is not a list — a sentence may
    /// begin "1. " and a price list begins with a dash — and a shared left edge alone is every
    /// paragraph on the page. What identifies a list is <i>several consecutive lines</i> that each
    /// begin with a marker <i>and</i> start at the same X. The horizontal half only became
    /// available when <see cref="RunCandidate.X"/> did; before that no rule of this shape could be
    /// written at all.</para>
    ///
    /// <para><b>It refuses far more than it accepts, deliberately.</b> A wrongly-inferred list is
    /// worse than none: it puts an <c>/L</c> into the tree that a screen reader announces as a list
    /// of the wrong length, and this repository's own deep checker then reports the shape as a
    /// finding — remediation manufacturing the violation it exists to remove. So the run must be at
    /// least two lines, the left edges must agree within a fraction of the type size, and a line
    /// whose marker is a bare number is required to sit in a sequence that actually counts.</para>
    ///
    /// <para><b>Nothing here is a claim about nesting.</b> A deeper item is detected as an ordinary
    /// item of its own list, because the run is broken by the change of left edge; the tagger emits
    /// two sibling lists rather than one nested pair. That is the honest answer for a heuristic
    /// that cannot see whether the second list belongs to an item of the first, and it is
    /// conformant — a shallow list is not a wrong list.</para>
    /// </remarks>
    internal static class ListLineDetector
    {
        /// <summary>
        /// Lines shorter than this cannot be an item: a marker plus a space plus something.
        /// </summary>
        private const int MinimumItemLength = 3;

        /// <summary>
        /// How many consecutive marked lines make a list.
        /// </summary>
        /// <remarks>
        /// <b>Two, not one.</b> A single line beginning "1." is a sentence far more often than it
        /// is a list of one, and a list of one is not worth the risk of being wrong about it —
        /// PDF/UA is satisfied by a paragraph either way.
        /// </remarks>
        private const int MinimumItems = 2;

        /// <summary>
        /// How far two items' left edges may differ, as a fraction of the line's type size.
        /// </summary>
        /// <remarks>
        /// Relative to the type size rather than absolute, because the tolerance that is right for
        /// 8 pt text is not the one for a 24 pt heading — and because a producer that places every
        /// item with the same operand still lands them a rounding step apart after the matrix.
        /// </remarks>
        private const float LeftEdgeToleranceEms = 0.35f;

        /// <summary>Bullet characters a producer draws an unordered item with.</summary>
        /// <remarks>
        /// <para>The hyphen and the asterisk are here because plain-text-derived documents use them,
        /// and they are the two that most need the second signal: a line beginning "- " is a list
        /// item in a list and a dash in prose, and only the shared left edge of its neighbours
        /// separates the two.</para>
        ///
        /// <para><b>A single-byte-encoded bullet is NOT detected, and that is a limit of the text
        /// this reads rather than of the list below.</b> The tagger's line text comes from
        /// <c>PdfString.GetText()</c>, which decodes a show operand's bytes through PDFDocEncoding
        /// <i>without consulting the font's own <c>/Encoding</c></i> — documented as
        /// best-effort, because nothing about the tagging depends on it. A <c>/WinAnsiEncoding</c>
        /// bullet is byte <c>0x95</c>, and PDFDocEncoding reads that as U+0141, <c>Ł</c>. So
        /// the character that reaches here is a Polish letter.</para>
        ///
        /// <para><b>Adding U+0141 would be guessing at an encoding, and this refuses to.</b> It is a
        /// real letter in real text, and the byte means something different again under
        /// MacRomanEncoding (<c>0xA5</c> → U+00A5, <c>¥</c>) — so a table keyed on
        /// what one encoding happens to produce would create false positives in another. Detecting
        /// these needs the show string decoded through the font, which is a change to what the
        /// tagger records rather than to this list; until then an ASCII marker and a Unicode bullet
        /// are what a run can be built from. <c>ListInferenceTests</c> pins the miss so it stays a
        /// known limit rather than becoming a surprise.</para>
        /// </remarks>
        private static readonly char[] Bullets =
        {
            '\u2022',   // •  BULLET
            '\u25e6',   // ◦  WHITE BULLET
            '\u25aa',   // ▪  BLACK SMALL SQUARE
            '\u25cf',   // ●  BLACK CIRCLE
            '\u2023',   // ‣  TRIANGULAR BULLET
            '\u2043',   // ⁃  HYPHEN BULLET
            '\u00b7',   // ·  MIDDLE DOT
            '-', '*',
        };

        /// <summary>
        /// Marks the lines that belong to a list, leaving every other entry false.
        /// </summary>
        /// <param name="texts">Each line's best-effort text, in page order.</param>
        /// <param name="lefts">Each line's left edge; <see cref="float.NaN"/> when unknown.</param>
        /// <param name="sizes">Each line's effective type size.</param>
        /// <returns>One flag per line.</returns>
        internal static bool[] Detect(
            IReadOnlyList<string> texts, IReadOnlyList<float> lefts, IReadOnlyList<float> sizes)
        {
            int count = texts?.Count ?? 0;
            var isItem = new bool[count];
            if (count < MinimumItems) return isItem;

            int runStart = -1;
            for (int i = 0; i <= count; i++)
            {
                bool marked = i < count && HasMarker(texts[i]) && IsFinite(lefts[i]);

                // A run continues only while the left edge holds. A change of indentation is a
                // different list — or a wrapped continuation line, which is not an item — and
                // either way the two must not be counted as one sequence.
                if (marked && runStart >= 0 && !SameLeftEdge(lefts[runStart], lefts[i], sizes[i]))
                    marked = false;

                if (marked)
                {
                    if (runStart < 0) runStart = i;
                    continue;
                }

                if (runStart >= 0 && i - runStart >= MinimumItems)
                    for (int k = runStart; k < i; k++) isItem[k] = true;

                // The line that broke the run may itself start the next one.
                runStart = i < count && HasMarker(texts[i]) && IsFinite(lefts[i]) ? i : -1;
            }

            return isItem;
        }

        private static bool SameLeftEdge(float a, float b, float size)
        {
            float tolerance = LeftEdgeToleranceEms * (IsFinite(size) && size > 0f ? size : 12f);
            return Math.Abs(a - b) <= tolerance;
        }

        /// <summary>Whether a line begins with a list marker and something after it.</summary>
        internal static bool HasMarker(string text)
        {
            if (text == null) return false;

            int at = 0;
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
            if (text.Length - at < MinimumItemLength) return false;

            if (Array.IndexOf(Bullets, text[at]) >= 0)
                return at + 1 < text.Length && char.IsWhiteSpace(text[at + 1]);

            return HasOrdinalMarker(text, at);
        }

        /// <summary>
        /// Whether the text at <paramref name="at"/> is an ordinal followed by <c>.</c> or
        /// <c>)</c> and a space.
        /// </summary>
        /// <remarks>
        /// <b>The trailing space is what keeps a decimal out.</b> "3.14 is pi" begins with a digit,
        /// a dot and more digits; requiring whitespace after the punctuation rejects it, and
        /// rejects a version number and a section reference with it. The ordinal itself is limited
        /// to a few characters so that a sentence beginning with a long number is not read as an
        /// item of a list numbered in the thousands.
        /// </remarks>
        private static bool HasOrdinalMarker(string text, int at)
        {
            const int MaxOrdinalLength = 4;

            int end = at;
            while (end < text.Length && end - at < MaxOrdinalLength && IsOrdinalChar(text[end])) end++;
            if (end == at) return false;

            if (end >= text.Length || (text[end] != '.' && text[end] != ')')) return false;

            return end + 1 < text.Length && char.IsWhiteSpace(text[end + 1]);
        }

        /// <summary>A digit, or a letter an alphabetic or roman sequence would use.</summary>
        /// <remarks>
        /// Any ASCII letter, not just the roman numerals: <c>a)</c>, <c>b)</c>, <c>c)</c> is an
        /// ordinary alphabetic list, and restricting to <c>ivxlcdm</c> would accept its third item
        /// and reject its first two — a list detected from the middle is worse than one not
        /// detected at all.
        /// </remarks>
        private static bool IsOrdinalChar(char c)
            => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
