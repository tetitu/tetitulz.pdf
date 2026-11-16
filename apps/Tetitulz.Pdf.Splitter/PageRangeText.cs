// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Tetitulz.Pdf.Splitter
{
    /// <summary>
    /// The printer-dialog page-range grammar: <c>"1-3, 7"</c> in both directions.
    /// </summary>
    /// <remarks>
    /// <para>Pure and total — no I/O, no state, no exceptions on bad input. That is deliberate: this
    /// is the part of the app most likely to carry an off-by-one, and it is the part that can be
    /// tested without a window. <see cref="TryParse"/> reports a reason instead of throwing because
    /// its caller is a <c>TextChanged</c> handler firing on every keystroke, where half-typed input
    /// is normal rather than exceptional.</para>
    /// <para>Page numbers are <b>1-based</b> throughout, matching what the user types, what the
    /// thumbnails are labelled, and what <c>PdfSplitter.ExtractPages</c> takes.</para>
    /// </remarks>
    internal static class PageRangeText
    {
        /// <summary>Both are in common use, and accepting both costs one array element.</summary>
        private static readonly char[] PartSeparators = { ',', ';' };

        /// <summary>
        /// Parses a range expression against a document of <paramref name="pageCount"/> pages.
        /// </summary>
        /// <param name="pages">Ascending, de-duplicated, 1-based. Empty when the text is blank.</param>
        /// <param name="error">Why it was rejected; <c>null</c> on success.</param>
        /// <returns><c>false</c> only for input that cannot mean anything.</returns>
        /// <remarks>
        /// Blank text is a <b>success</b> returning no pages, not a failure: "nothing selected" is a
        /// state the user is entitled to be in, and reporting it as an error would paint the box red
        /// the moment they cleared it.
        /// </remarks>
        public static bool TryParse(string text, int pageCount,
                                    out IReadOnlyList<int> pages, out string error)
        {
            pages = new List<int>();
            if (!TryParseGroups(text, pageCount, out IReadOnlyList<IReadOnlyList<int>> groups, out error))
                return false;

            var result = new SortedSet<int>();
            foreach (IReadOnlyList<int> group in groups)
                foreach (int page in group) result.Add(page);
            pages = new List<int>(result);
            return true;
        }

        /// <summary>
        /// Parses the same grammar as <see cref="TryParse"/>, keeping each comma-separated part as a
        /// group of its own: <c>"1-3, 7, 9-"</c> is three groups, one output file each when the user
        /// splits by group.
        /// </summary>
        /// <param name="groups">
        /// One entry per non-empty part, in the order typed; each ascending, 1-based. A page may be in
        /// more than one group. Empty when the text is blank.
        /// </param>
        /// <param name="error">Why it was rejected; <c>null</c> on success.</param>
        public static bool TryParseGroups(string text, int pageCount,
                                          out IReadOnlyList<IReadOnlyList<int>> groups, out string error)
        {
            error = null;
            var list = new List<IReadOnlyList<int>>();
            groups = list;

            if (pageCount < 0) pageCount = 0;
            if (string.IsNullOrWhiteSpace(text)) return true;

            foreach (string rawPart in text.Split(PartSeparators))
            {
                string part = rawPart.Trim();

                // "1,,2", a trailing comma, and "1, " are all someone mid-keystroke.
                if (part.Length == 0) continue;

                int dash = part.IndexOf('-');
                if (dash < 0)
                {
                    int only;
                    if (!TryPage(part, pageCount, out only, out error)) return false;
                    list.Add(new[] { only });
                    continue;
                }

                string leftText = part.Substring(0, dash).Trim();
                string rightText = part.Substring(dash + 1).Trim();

                if (rightText.IndexOf('-') >= 0)
                {
                    error = "'" + part + "' has more than one '-'.";
                    return false;
                }

                int first, last;
                if (leftText.Length == 0 && rightText.Length == 0)
                {
                    error = "'-' on its own does not name a range.";
                    return false;
                }

                if (leftText.Length == 0)
                {
                    // "-4" is "up to 4", the open-start form printers accept.
                    first = 1;
                    if (!TryPage(rightText, pageCount, out last, out error)) return false;
                }
                else if (rightText.Length == 0)
                {
                    // "9-" is "9 to the end".
                    if (!TryPage(leftText, pageCount, out first, out error)) return false;
                    last = pageCount;
                }
                else
                {
                    if (!TryPage(leftText, pageCount, out first, out error)) return false;
                    if (!TryPage(rightText, pageCount, out last, out error)) return false;
                }

                // "12-9" is a range typed backwards, not a mistake worth refusing.
                if (first > last)
                {
                    int swap = first;
                    first = last;
                    last = swap;
                }

                var run = new int[last - first + 1];
                for (int p = first; p <= last; p++) run[p - first] = p;
                list.Add(run);
            }

            return true;
        }

        /// <summary>
        /// Renders a page set the way <see cref="TryParse"/> reads it: ascending, comma-separated,
        /// with every run of two or more consecutive pages collapsed to <c>a-b</c>.
        /// </summary>
        public static string Format(IEnumerable<int> pages)
        {
            if (pages == null) return string.Empty;

            var sorted = new SortedSet<int>(pages);
            if (sorted.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            int runStart = 0, previous = 0;
            bool inRun = false;

            foreach (int page in sorted)
            {
                if (!inRun)
                {
                    runStart = page;
                    previous = page;
                    inRun = true;
                    continue;
                }

                if (page == previous + 1)
                {
                    previous = page;
                    continue;
                }

                AppendRun(sb, runStart, previous);
                runStart = page;
                previous = page;
            }

            AppendRun(sb, runStart, previous);
            return sb.ToString();
        }

        private static void AppendRun(StringBuilder sb, int first, int last)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(first.ToString(CultureInfo.InvariantCulture));
            if (last != first) sb.Append('-').Append(last.ToString(CultureInfo.InvariantCulture));
        }

        /// <remarks>
        /// <see cref="NumberStyles.None"/> rather than the default: it rejects a leading sign,
        /// thousands separators and surrounding whitespace, so "+3", "1,000" and "3.5" are refused
        /// as text rather than silently becoming a page number nobody typed.
        /// </remarks>
        private static bool TryPage(string text, int pageCount, out int page, out string error)
        {
            error = null;

            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out page))
            {
                error = "'" + text + "' is not a page number.";
                return false;
            }

            if (pageCount == 0)
            {
                error = "The document has no pages.";
                return false;
            }

            if (page < 1 || page > pageCount)
            {
                error = "Page " + page.ToString(CultureInfo.InvariantCulture) +
                        " is out of range (1-" + pageCount.ToString(CultureInfo.InvariantCulture) + ").";
                return false;
            }

            return true;
        }
    }
}
