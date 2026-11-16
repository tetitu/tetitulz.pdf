// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// Greedy word wrapping for <see cref="PdfTextReflow"/>: the step that decides how many lines a
    /// paragraph's text becomes.
    /// </summary>
    /// <remarks>
    /// <para><b>Greedy, not optimal, and that is the right choice here.</b> Knuth-Plass minimises
    /// raggedness across the whole paragraph and is what a typesetter wants when it owns the page.
    /// This does not own the page: it is re-breaking one paragraph inside a document somebody else
    /// laid out, and the only thing that matters is that the result fits the measure it was given.
    /// A better algorithm would move words the caller did not ask about for an aesthetic gain
    /// nobody requested.</para>
    ///
    /// <para><b>Measuring is the caller's, because only it knows the font.</b> The width of a
    /// candidate line depends on the shaped run — kerning, ligatures, bidi — which
    /// <c>TextRunComposer</c> answers and this file must not second-guess. Passing a delegate keeps
    /// the one measurement in the one place that can do it correctly, and keeps this function pure
    /// enough to test against a measure that counts characters.</para>
    ///
    /// <para><b>A word wider than the measure gets its own line and overflows it.</b> Inventing break
    /// points inside a word is how a URL or a part number becomes two wrong strings. Overflowing is
    /// visible; a wrong break is not. Hyphenation, when the caller asks, breaks only where the
    /// patterns say an English word may break (<see cref="Core.Text.Hyphenation.EnglishHyphenation"/>),
    /// and a URL or a part number has no such point.</para>
    /// </remarks>
    internal static class ReflowLineBreaker
    {
        /// <summary>
        /// Breaks <paramref name="text"/> into lines no wider than <paramref name="maxWidth"/>.
        /// </summary>
        /// <param name="text">The paragraph's text, already rewritten.</param>
        /// <param name="measure">The width of a candidate line, in the same unit as <paramref name="maxWidth"/>.</param>
        /// <param name="maxWidth">The measure to fit. Non-positive means "one line", since a
        /// measure that admits nothing would otherwise put every word on a line of its own.</param>
        /// <param name="hyphenate">Break a word that does not fit at the last of its hyphenation
        /// points whose head, with a hyphen, still fits the line; the rest starts the next.</param>
        internal static List<string> Break(string text, Func<string, double> measure, double maxWidth,
                                           bool hyphenate = false)
        {
            if (measure == null) throw new ArgumentNullException(nameof(measure));

            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;

            string[] words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return lines;

            if (maxWidth <= 0)
            {
                lines.Add(string.Join(" ", words));
                return lines;
            }

            var line = new StringBuilder();
            foreach (string word in words)
            {
                if (line.Length == 0)
                {
                    // The first word goes on unconditionally: if it does not fit, no break point
                    // exists that would help, and an empty line followed by the same problem is
                    // strictly worse than one overflowing line.
                    line.Append(word);
                    continue;
                }

                // Measured as the whole candidate rather than as "current + space + word" summed
                // separately: shaping is not additive — kerning happens across the join, and a
                // ligature can form over one — so adding measured pieces is a different number
                // from measuring the piece that will actually be drawn.
                string candidate = line + " " + word;
                if (measure(candidate) <= maxWidth)
                {
                    line.Append(' ').Append(word);
                    continue;
                }

                int at = hyphenate ? LastFittingBreak(line.ToString(), word, measure, maxWidth) : 0;
                if (at > 0) line.Append(' ').Append(word, 0, at).Append('-');
                lines.Add(line.ToString());
                line.Clear();
                line.Append(word, at, word.Length - at);
            }

            if (line.Length > 0) lines.Add(line.ToString());
            return lines;
        }

        /// <summary>
        /// The last hyphenation point of <paramref name="word"/> at which <paramref name="line"/>, a
        /// space, the word's head and a hyphen fit <paramref name="maxWidth"/> — measured whole, as every
        /// candidate is; 0 when none does.
        /// </summary>
        private static int LastFittingBreak(string line, string word, Func<string, double> measure, double maxWidth)
        {
            var points = Core.Text.Hyphenation.EnglishHyphenation.BreakPointsWithin(word);
            for (int i = points.Count - 1; i >= 0; i--)
                if (measure(line + " " + word.Substring(0, points[i]) + "-") <= maxWidth) return points[i];
            return 0;
        }
    }
}
