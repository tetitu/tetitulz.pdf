// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;

namespace Tetitulz.Pdf.Plugins.PageEdits
{
    /// <summary>
    /// Works out <b>which pages go in which output</b> — every-N, at bookmarks, or under a byte
    /// ceiling — leaving the extraction itself to <see cref="PdfSplitter"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>A planner, not a second splitter.</b> <c>PdfSplitter</c> takes explicit ranges and
    /// nothing else; all five of its entry points funnel into one engine that rebuilds the catalog,
    /// filters the name trees, prunes the outline and scopes the AcroForm fields. This decides the
    /// boundaries, and returns page groups in
    /// <c>PdfSplitter</c>'s own <b>one-based</b> numbering and the caller loops
    /// <see cref="PdfSplitter.ExtractPages(Stream, Stream, IReadOnlyList{int})"/>.</para>
    ///
    /// <para><b>The two numbering systems collide.</b> <c>PdfBookmark.PageNumber</c> is
    /// <b>zero</b>-based while <c>PdfSplitter</c> is <b>one</b>-based; forwarding an index unchanged
    /// would make one entry point throw on the first page and another return the page before the
    /// one asked for. The conversion happens here, at the boundary, exactly once.</para>
    /// </remarks>
    public static class PdfSplitPlanner
    {
        [ThreadStatic] private static string _lastPlanWarning;

        /// <summary>
        /// Why the plan is not the boundary set the source implies — or null when it is. Cleared at
        /// the start of every call, on this thread.
        /// </summary>
        public static string LastPlanWarning => _lastPlanWarning;

        /// <summary>
        /// Consecutive runs of <paramref name="pagesPerChunk"/> pages; the last run is short.
        /// </summary>
        /// <param name="input">The PDF to plan over. Not disposed; position is not preserved.</param>
        /// <param name="pagesPerChunk">Pages per output. Must be positive.</param>
        public static IReadOnlyList<IReadOnlyList<int>> PlanEveryNPages(Stream input, int pagesPerChunk)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (pagesPerChunk < 1)
                throw new ArgumentOutOfRangeException(nameof(pagesPerChunk), "At least one page per chunk.");

            _lastPlanWarning = null;
            using var doc = OpenForPlan(input);
            return GroupRuns(BoundariesEveryN(doc.Pages.Count, pagesPerChunk), doc.Pages.Count);
        }

        /// <summary>
        /// The document a plan reads, refused when there is nothing to plan: an encrypted one, whose
        /// page tree is not loaded so it reports no pages, and one with no pages. Each gave an empty
        /// plan with <see cref="LastPlanWarning"/> null, which a caller's loop wrote nothing from and
        /// reported as success — the refusals <see cref="PdfSplitter"/>'s <c>Split</c> makes.
        /// </summary>
        private static PdfDocument OpenForPlan(Stream input)
        {
            var doc = PdfDocument.Open(input);
            try
            {
                PdfSplitter.RefuseEncrypted(doc);
                if (doc.Pages.Count == 0)
                    throw new Core.PdfException(
                        "The document exposes no pages, so there is nothing to plan; an empty plan looks " +
                        "exactly like success. Check the input for a damaged or absent page tree.");
                return doc;
            }
            catch
            {
                doc.Dispose();
                throw;
            }
        }

        /// <summary>
        /// One chunk per bookmark at depth &lt;= <paramref name="maxDepth"/>.
        /// </summary>
        /// <param name="input">The PDF to plan over. Not disposed; position is not preserved.</param>
        /// <param name="maxDepth">0 = top-level bookmarks only; 1 also splits at their children.</param>
        /// <remarks>
        /// <para>Pages before the first bookmark form a leading chunk rather than being dropped or
        /// folded into the first chapter — a title page and a table of contents are exactly that,
        /// and silently attaching them to chapter 1 is a decision the caller did not make.</para>
        ///
        /// <para>Two bookmarks on the same page produce <b>one</b> boundary: a chunk cannot start
        /// mid-page, and emitting an empty chunk for the second would be a file with no pages.
        /// <see cref="LastPlanWarning"/> says when that happened.</para>
        /// </remarks>
        public static IReadOnlyList<IReadOnlyList<int>> PlanAtBookmarks(Stream input, int maxDepth = 0)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (maxDepth < 0) throw new ArgumentOutOfRangeException(nameof(maxDepth));

            _lastPlanWarning = null;

            var seekable = Base.Operations.Metadata.PdfMetadataWriter.EnsureSeekable(input);
            var bookmarks = PdfBookmarksManager.ReadBookmarks(seekable);

            seekable.Position = 0;
            using var doc = OpenForPlan(seekable);
            int pageCount = doc.Pages.Count;

            var boundaries = new SortedSet<int>();
            int collapsed = 0;
            CollectBoundaries(bookmarks, 0, maxDepth, pageCount, boundaries, ref collapsed);

            if (boundaries.Count == 0)
            {
                _lastPlanWarning =
                    "the document has no bookmarks at depth " + maxDepth + " or above, so the whole " +
                    "document is one chunk";
                return GroupRuns(new SortedSet<int> { 1 }, pageCount);
            }

            if (collapsed > 0)
                _lastPlanWarning =
                    collapsed.ToString(CultureInfo.InvariantCulture) + " bookmark(s) share a page with " +
                    "an earlier one, so they did not each start a chunk — a chunk cannot begin " +
                    "mid-page.";

            return GroupRuns(boundaries, pageCount);
        }

        /// <summary>
        /// Greedy runs whose <b>extracted</b> output stays at or below <paramref name="maxBytes"/>.
        /// </summary>
        /// <param name="input">The PDF to plan over. Not disposed; position is not preserved.</param>
        /// <param name="maxBytes">Ceiling per output file, in bytes. Must be positive.</param>
        /// <remarks>
        /// <para><b>It extracts to measure, and it has to.</b> The splitter rebuilds one shared
        /// object graph per chunk — two pages sharing a font are <i>one</i> copy in a joint chunk and
        /// <i>two</i> in separate ones — so summing per-page sizes is wrong in both directions. Each
        /// chunk's end is found by doubling the run and bisecting, so the cost is O(log pages) trial
        /// extractions per chunk — each a whole-chunk extract into memory — which is why this is the
        /// one planner that is not free. <see cref="LastTrialExtractions"/> says how many it made.</para>
        ///
        /// <para><b>A single page that exceeds the ceiling on its own becomes its own chunk</b> and
        /// is named in <see cref="LastPlanWarning"/>: a page cannot be split, so the alternative is
        /// an empty plan or an infinite loop.</para>
        /// </remarks>
        public static IReadOnlyList<IReadOnlyList<int>> PlanUnderByteCeiling(Stream input, long maxBytes)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));

            _lastPlanWarning = null;
            LastTrialExtractions = 0;

            var seekable = Base.Operations.Metadata.PdfMetadataWriter.EnsureSeekable(input);
            using var doc = OpenForPlan(seekable);
            int pageCount = doc.Pages.Count;

            var plan = new List<IReadOnlyList<int>>();
            var oversized = new List<int>();

            int start = 1;
            while (start <= pageCount)
            {
                if (Measure(seekable, start, start) > maxBytes)
                {
                    // One page over the ceiling by itself. It cannot be split, so it ships alone and
                    // the caller is told rather than left to discover an over-size output.
                    oversized.Add(start);
                    plan.Add(new[] { start });
                    start++;
                    continue;
                }

                // The longest run from `start` that fits: double the run until it breaks the ceiling
                // or reaches the last page, then bisect between the last fit and the first break. A
                // run's extract holds every object of its prefix's, so the size only grows with the
                // pages and the ceiling is crossed once — which is what makes the bisection exact.
                // Growing the run one page at a time would measure every prefix of every chunk: a
                // document that fits the ceiling whole would cost n extracts of 1, 2, … n pages.
                int fit = start;
                int bust = 0;
                for (int span = 2; bust == 0 && fit < pageCount; span *= 2)
                {
                    int probe = Math.Min(start + span - 1, pageCount);
                    if (Measure(seekable, start, probe) <= maxBytes) fit = probe;
                    else bust = probe;
                }
                while (bust != 0 && bust - fit > 1)
                {
                    int mid = fit + (bust - fit) / 2;
                    if (Measure(seekable, start, mid) <= maxBytes) fit = mid;
                    else bust = mid;
                }

                var chunk = new int[fit - start + 1];
                for (int i = 0; i < chunk.Length; i++) chunk[i] = start + i;
                plan.Add(chunk);
                start = fit + 1;
            }

            if (oversized.Count > 0)
                _lastPlanWarning =
                    "page(s) " + string.Join(", ", oversized) + " exceed " + maxBytes +
                    " bytes on their own and were emitted as single-page chunks over the ceiling; " +
                    "a page cannot be split.";

            return plan;
        }

        // ----------------------------------------------------------------

        /// <summary>
        /// How many trial extractions the last <see cref="PlanUnderByteCeiling"/> on this thread
        /// made — the evidence that each chunk's end is found by doubling and bisection rather than
        /// one page at a time, which no assertion on the plan itself can see (both searches find the
        /// same chunks). Reset at the start of every call.
        /// </summary>
        [ThreadStatic] internal static int LastTrialExtractions;

        /// <summary>The size the run <paramref name="first"/>..<paramref name="last"/> would be.</summary>
        private static long Measure(Stream source, int first, int last)
        {
            var pages = new int[last - first + 1];
            for (int i = 0; i < pages.Length; i++) pages[i] = first + i;
            return MeasureExtract(source, pages);
        }

        /// <summary>The size the chunk would be, measured by extracting it into memory.</summary>
        private static long MeasureExtract(Stream source, IReadOnlyList<int> pages)
        {
            LastTrialExtractions++;
            source.Position = 0;
            using var probe = new MemoryStream();
            PdfSplitter.ExtractPages(source, probe, pages);
            return probe.Length;
        }

        private static SortedSet<int> BoundariesEveryN(int pageCount, int pagesPerChunk)
        {
            var boundaries = new SortedSet<int>();
            for (int page = 1; page <= pageCount; page += pagesPerChunk) boundaries.Add(page);
            return boundaries;
        }

        /// <summary>
        /// Walks the bookmark tree to <paramref name="maxDepth"/>, converting each bookmark's
        /// <b>zero</b>-based page to the splitter's <b>one</b>-based numbering.
        /// </summary>
        private static void CollectBoundaries(
            IReadOnlyList<PdfBookmark> bookmarks, int depth, int maxDepth, int pageCount,
            SortedSet<int> boundaries, ref int collapsed)
        {
            if (bookmarks == null || depth > maxDepth) return;

            for (int i = 0; i < bookmarks.Count; i++)
            {
                var bookmark = bookmarks[i];

                // Zero-based here, one-based in PdfSplitter. PageNumber is NULLABLE: a named destination that did
                // not resolve has none, and such a bookmark starts no chunk rather than starting one
                // at page 1, which is where a `?? 0` would silently put every unresolvable entry.
                int? zeroBased = bookmark.PageNumber;
                if (zeroBased.HasValue && zeroBased.Value >= 0 && zeroBased.Value < pageCount)
                {
                    if (!boundaries.Add(zeroBased.Value + 1)) collapsed++;
                }

                CollectBoundaries(bookmark.Children, depth + 1, maxDepth, pageCount, boundaries, ref collapsed);
            }
        }

        /// <summary>
        /// Turns a set of one-based start pages into contiguous page runs covering the document.
        /// </summary>
        /// <remarks>
        /// Page 1 is always a boundary: pages before the first bookmark are a chunk of their own —
        /// a title page and a table of contents are exactly that, and folding them into chapter 1
        /// is a decision the caller did not make.
        /// </remarks>
        private static IReadOnlyList<IReadOnlyList<int>> GroupRuns(SortedSet<int> boundaries, int pageCount)
        {
            var plan = new List<IReadOnlyList<int>>();
            if (pageCount <= 0) return plan;

            var starts = new List<int>(boundaries);
            if (starts.Count == 0 || starts[0] != 1) starts.Insert(0, 1);

            for (int i = 0; i < starts.Count; i++)
            {
                int from = starts[i];
                int to = (i + 1 < starts.Count ? starts[i + 1] : pageCount + 1) - 1;
                if (to < from) continue;

                var pages = new int[to - from + 1];
                for (int p = 0; p < pages.Length; p++) pages[p] = from + p;
                plan.Add(pages);
            }

            return plan;
        }
    }
}
