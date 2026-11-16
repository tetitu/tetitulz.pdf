// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations.Extraction;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    // ------------------------------------------------------------------
    //  Data model
    // ------------------------------------------------------------------

    /// <summary>Type of difference between two PDF documents.</summary>
    public enum PdfDiffType
    {
        /// <summary>Text present in the left document but not the right.</summary>
        Deleted,
        /// <summary>Text present in the right document but not the left.</summary>
        Inserted,
        /// <summary>Text present in both documents (context).</summary>
        Unchanged
    }

    /// <summary>A single text difference between two PDF documents.</summary>
    public sealed class PdfTextDiff
    {
        /// <summary>Nature of this difference.</summary>
        public PdfDiffType Type { get; }
        /// <summary>The text segment involved.</summary>
        public string Text { get; }
        /// <summary>Zero-based page index (from whichever document contained this text).</summary>
        public int PageIndex { get; }

        public PdfTextDiff(PdfDiffType type, string text, int pageIndex)
        {
            Type      = type;
            Text      = text ?? string.Empty;
            PageIndex = pageIndex;
        }

        public override string ToString()
        {
            string prefix = Type == PdfDiffType.Deleted   ? "- " :
                            Type == PdfDiffType.Inserted  ? "+ " : "  ";
            return $"[p{PageIndex + 1}] {prefix}{Text}";
        }
    }

    /// <summary>Result of comparing two PDF documents.</summary>
    public sealed class PdfDiffResult
    {
        /// <summary>All text differences, in document order.</summary>
        public IReadOnlyList<PdfTextDiff> TextDiffs { get; }

        /// <summary>True when the two documents have identical text on all pages.</summary>
        public bool AreTextEqual { get; }

        /// <summary>Number of pages in the left document.</summary>
        public int LeftPageCount { get; }

        /// <summary>Number of pages in the right document.</summary>
        public int RightPageCount { get; }

        internal PdfDiffResult(
            IReadOnlyList<PdfTextDiff> diffs,
            int leftPages, int rightPages)
        {
            TextDiffs     = diffs;
            LeftPageCount = leftPages;
            RightPageCount = rightPages;
            bool allUnchanged = true;
            foreach (var d in diffs)
                if (d.Type != PdfDiffType.Unchanged) { allUnchanged = false; break; }
            AreTextEqual = allUnchanged && leftPages == rightPages;
        }
    }

    // ------------------------------------------------------------------
    //  Comparer
    // ------------------------------------------------------------------

    /// <summary>
    /// Compares two PDF documents and reports text-level differences.
    ///
    /// <para>Algorithm:</para>
    /// <list type="number">
    ///   <item>Extract plain text from each page of both documents using
    ///         <see cref="PdfStructuredTextExtractor"/>.</item>
    ///   <item>Split each page into lines.</item>
    ///   <item>Apply the Myers diff algorithm (LCS-based) to produce an edit script.</item>
    ///   <item>Return a <see cref="PdfDiffResult"/> containing all <see cref="PdfTextDiff"/> entries.</item>
    /// </list>
    /// </summary>
    public static class PdfComparer
    {
        // ------------------------------------------------------------------
        //  Public API
        // ------------------------------------------------------------------

        /// <summary>
        /// Refuses an encrypted side rather than comparing what it cannot read.
        /// </summary>
        /// <remarks>
        /// <para><b>An encrypted document reads as zero pages, so two different ones compared
        /// "equal".</b> That is the failure this exists for: not a missing feature but a confident
        /// wrong verdict, returned for the one question this method is asked.</para>
        ///
        /// <para><b>It takes no password on purpose, and refusing is the consequence rather than a
        /// substitute.</b> Two documents can carry two different passwords, or one can be encrypted
        /// and the other not, so a single <c>password</c> parameter is the wrong shape — the reason
        /// recorded against this method in <c>FacadePasswordCoverageTests.DeliberateExclusions</c>.
        /// Declining the parameter is right; answering anyway was not.</para>
        /// </remarks>
        private static void RefuseEncrypted(Stream side, string which)
        {
            if (!PdfHelpers.IsEncryptedDocument(side)) return;

            throw new Core.PdfException(
                "The " + which + " document is encrypted, so its text cannot be read and comparing " +
                "it would report two different documents as identical. This method takes no " +
                "password because two documents can need two different ones: decrypt each side " +
                "through Tetitulz.Pdf.Base.Encryption.PdfDecryptedInput and compare the results.");
        }

        /// <summary>
        /// Refuses a side whose extraction could not read a page: the extractor hands such a page
        /// back empty and says so in its <c>LastError</c>, and an empty page diffs as equal to
        /// another, so two documents neither of which can be read would compare identical.
        /// </summary>
        private static void RefuseUnread(string which, string extractionError)
        {
            if (extractionError == null) return;

            throw new Core.PdfException(
                "The " + which + " document has a page whose text could not be read (" + extractionError +
                "), so comparing it would read that page as empty and could report two different documents as identical.");
        }

        public static PdfDiffResult CompareText(string leftPath, string rightPath)
        {
            if (leftPath  == null) throw new ArgumentNullException(nameof(leftPath));
            if (rightPath == null) throw new ArgumentNullException(nameof(rightPath));
            using var l = File.OpenRead(leftPath);
            using var r = File.OpenRead(rightPath);
            return CompareText(l, r);
        }

        public static PdfDiffResult CompareText(Stream left, Stream right)
        {
            if (left  == null) throw new ArgumentNullException(nameof(left));
            if (right == null) throw new ArgumentNullException(nameof(right));

            _lastApproximation = null;
            RefuseEncrypted(left, "left");
            RefuseEncrypted(right, "right");

            var leftPages  = PdfStructuredTextExtractor.Extract(left);
            RefuseUnread("left", PdfStructuredTextExtractor.LastError);
            var rightPages = PdfStructuredTextExtractor.Extract(right);
            RefuseUnread("right", PdfStructuredTextExtractor.LastError);

            var diffs = new List<PdfTextDiff>();

            int maxPages = Math.Max(leftPages.Count, rightPages.Count);
            var run = new DiffRun();

            for (int pi = 0; pi < maxPages; pi++)
            {
                string[] leftLines  = pi < leftPages.Count
                    ? SplitLines(leftPages[pi])
                    : Array.Empty<string>();
                string[] rightLines = pi < rightPages.Count
                    ? SplitLines(rightPages[pi])
                    : Array.Empty<string>();

                var pageDiffs = DiffLines(leftLines, rightLines, pi, run);
                diffs.AddRange(pageDiffs);
            }

            if (run.CoarsePages > 0)
                _lastApproximation = run.CoarsePages + " page(s), the first page " + (run.FirstCoarsePage + 1) +
                    ", compared line by line in order rather than by the longest common subsequence: a page " +
                    "above " + MaxCellsPerPage + " line-table cells, or the comparison past " +
                    "PdfLimits.MaxTextCompareCells (" + Core.Constants.PdfLimits.MaxTextCompareCells + ").";
            return new PdfDiffResult(diffs, leftPages.Count, rightPages.Count);
        }

        [ThreadStatic] private static string _lastApproximation;

        /// <summary>
        /// Why the last <see cref="CompareText(Stream, Stream)"/> on this thread compared some pages line by
        /// line in order instead of by the longest common subsequence — a page too large for the line table,
        /// or the comparison past <see cref="Core.Constants.PdfLimits.MaxTextCompareCells"/>; <c>null</c> when
        /// every page was diffed exactly.
        /// </summary>
        public static string LastApproximation => _lastApproximation;

        /// <summary>The cells a page's table may have (its own cap; the run's is <c>MaxTextCompareCells</c>).</summary>
        private const long MaxCellsPerPage = 25_000_000L;

        /// <summary>
        /// One comparison's shared state: the cells charged so far, the line table reused page to page, and
        /// the pages that went coarse.
        /// </summary>
        private sealed class DiffRun
        {
            internal long CellsCharged;
            internal int[] Table = Array.Empty<int>();
            internal int CoarsePages;
            internal int FirstCoarsePage = -1;
        }

        // ------------------------------------------------------------------
        //  Text helpers
        // ------------------------------------------------------------------

        private static string[] SplitLines(PdfPageTextLayout page)
        {
            var lines = new List<string>();
            foreach (var line in page.Lines)
            {
                string t = line.Text.Trim();
                if (t.Length > 0) lines.Add(t);
            }
            return lines.ToArray();
        }

        // ------------------------------------------------------------------
        //  Myers diff (LCS-based)
        // ------------------------------------------------------------------

        private static List<PdfTextDiff> DiffLines(
            string[] left, string[] right, int pageIndex, DiffRun run)
        {
            // Build LCS table
            int m = left.Length, n = right.Length;
            // The dynamic-programming table is O(m*n). Both line counts come from
            // (untrusted) input PDFs; two large pages would request a multi-GB
            // 2-D array (and (m+1)*(n+1) can overflow int). Bound the product and
            // fall back to a coarse all-replaced diff above the cap — per page, and
            // across the run, since a page count multiplies the page's cap.
            long cells = (long)(m + 1) * (n + 1);
            if (cells > MaxCellsPerPage || run.CellsCharged + cells > Core.Constants.PdfLimits.MaxTextCompareCells)
            {
                run.CoarsePages++;
                if (run.FirstCoarsePage < 0) run.FirstCoarsePage = pageIndex;
                return CoarseDiff(left, right, pageIndex);
            }
            run.CellsCharged += cells;

            // Each distinct line becomes one number, so a cell compares two ints: comparing the strings
            // there cost a cell their shared prefix, and lines of a thousand characters sharing all but
            // the last made a page's table 2.5e10 character comparisons (07's DoS hunt, F5).
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            int[] a = LineIds(left, ids), b = LineIds(right, ids);

            int w = n + 1;
            if (run.Table.Length < cells) run.Table = new int[cells];
            int[] dp = run.Table;
            Array.Clear(dp, 0, (int)cells);
            for (int i = m - 1; i >= 0; i--)
                for (int j = n - 1; j >= 0; j--)
                    dp[i * w + j] = a[i] == b[j]
                        ? dp[(i + 1) * w + j + 1] + 1
                        : Math.Max(dp[(i + 1) * w + j], dp[i * w + j + 1]);

            // Trace back
            var result = new List<PdfTextDiff>();
            int li = 0, ri = 0;
            while (li < m || ri < n)
            {
                if (li < m && ri < n && a[li] == b[ri])
                {
                    result.Add(new PdfTextDiff(PdfDiffType.Unchanged, left[li], pageIndex));
                    li++; ri++;
                }
                // A tie goes to the deletion, so a replaced line reads "- old" then "+ new" — the
                // order CoarseDiff emits too. It went to the insertion, so the two paths answering
                // one question put a replacement's halves in opposite orders.
                else if (ri < n && (li >= m || dp[li * w + ri + 1] > dp[(li + 1) * w + ri]))
                {
                    result.Add(new PdfTextDiff(PdfDiffType.Inserted, right[ri], pageIndex));
                    ri++;
                }
                else
                {
                    result.Add(new PdfTextDiff(PdfDiffType.Deleted, left[li], pageIndex));
                    li++;
                }
            }
            return result;
        }

        private static int[] LineIds(string[] lines, Dictionary<string, int> ids)
        {
            var result = new int[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                if (!ids.TryGetValue(lines[i], out int id)) ids.Add(lines[i], id = ids.Count);
                result[i] = id;
            }
            return result;
        }

        // Linear, memory-bounded fallback used when the O(m*n) LCS table would be
        // too large. Compares line-by-line in order: equal lines are Unchanged,
        // otherwise the left line is Deleted and the right line Inserted. Less
        // precise than LCS but cannot exhaust memory on adversarial input.
        private static List<PdfTextDiff> CoarseDiff(string[] left, string[] right, int pageIndex)
        {
            var result = new List<PdfTextDiff>();
            int max = Math.Max(left.Length, right.Length);
            for (int i = 0; i < max; i++)
            {
                string l = i < left.Length ? left[i] : null;
                string r = i < right.Length ? right[i] : null;
                if (l != null && r != null && string.Equals(l, r, StringComparison.Ordinal))
                {
                    result.Add(new PdfTextDiff(PdfDiffType.Unchanged, l, pageIndex));
                }
                else
                {
                    if (l != null) result.Add(new PdfTextDiff(PdfDiffType.Deleted, l, pageIndex));
                    if (r != null) result.Add(new PdfTextDiff(PdfDiffType.Inserted, r, pageIndex));
                }
            }
            return result;
        }
    }
}
