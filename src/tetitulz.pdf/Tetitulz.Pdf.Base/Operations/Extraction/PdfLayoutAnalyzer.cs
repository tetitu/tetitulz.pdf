// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Operations.Extraction
{
    /// <summary>
    /// Higher-level layout analysis built on top of the per-character extraction layer.
    /// </summary>
    public static class PdfLayoutAnalyzer
    {
        [ThreadStatic] private static string _lastAnalysisWarning;

        /// <summary>
        /// The coverage grid kept for this thread's next page, so a document's pages do not each
        /// allocate one on the large-object heap (a page-wide grid is some 440 KB; structured
        /// extraction runs this on every page). Grids above <see cref="MaxRetainedGridCells"/> are
        /// not kept. Cleared before each use.
        /// </summary>
        [ThreadStatic] private static int[] _coverageGrid;

        /// <summary>The largest grid kept between pages: 1 Mi cells (4 MB) — every page of up to about 5 000 points wide.</summary>
        private const int MaxRetainedGridCells = 1 << 20;

        /// <summary>How many coverage grids this thread has allocated — the evidence that pages reuse one.</summary>
        [ThreadStatic] private static int _coverageGridsMade;

        internal static int CoverageGridsMade => _coverageGridsMade;

        /// <summary>A zeroed grid of at least <paramref name="cells"/> cells: this thread's kept one when it is large enough.</summary>
        private static int[] RentCoverageGrid(int cells)
        {
            int[] grid = _coverageGrid;
            if (grid != null && grid.Length >= cells)
            {
                Array.Clear(grid, 0, cells);
                return grid;
            }
            _coverageGridsMade++;
            grid = new int[cells];
            if (cells <= MaxRetainedGridCells) _coverageGrid = grid;
            return grid;
        }

        /// <summary>
        /// Why the most recent <see cref="DetectColumns"/> call on this thread returned a
        /// fallback rather than a real analysis, or <c>null</c> when it completed. Reset at the
        /// start of every call.
        /// </summary>
        /// <remarks>
        /// <para>A single whole-page zone is also the honest answer for a page that genuinely has
        /// no columns, so without this a caller cannot tell "one column" from "gave up". Read it on
        /// the thread that made the call (Diagnostics Pattern, CLAUDE.md).</para>
        ///
        /// <para><b>Three outcomes set it, and one deliberately does not.</b> Too few characters to
        /// analyse, a page with no area, and rivers that consume the page are all give-ups that
        /// return a zone list a caller cannot distinguish from a real answer. <i>No rivers found</i>
        /// is not one of them — that is the genuine one-column result, and reporting it would make
        /// the slot fire on the commonest conforming input, which is worse than never reporting.</para>
        ///
        /// <para><b>A slot whose only assignment is its reset can never be non-null</b>, and every
        /// <c>Assert.Null</c> on it passes vacuously. <c>DiagnosticSlotResetTests</c> asks both
        /// whether a slot is cleared and whether something writes it.</para>
        /// </remarks>
        public static string LastAnalysisWarning => _lastAnalysisWarning;

        // ----------------------------------------------------------------
        //  4.1  Multi-column layout detection
        // ----------------------------------------------------------------

        /// <summary>
        /// Detects vertical whitespace rivers and returns column zones ordered left-to-right.
        /// Each zone is a (X, Y, Width, Height) tuple in PDF page coordinates.
        /// Returns a single zone covering all text when no columns are detected.
        /// </summary>
        /// <param name="layout">Page layout from PdfStructuredTextExtractor.</param>
        /// <param name="minRiverWidth">
        /// Minimum horizontal gap width in pts for a gap to be considered a column separator.
        /// Default: 10 pt.
        /// </param>
        /// <param name="minHeightFraction">
        /// Fraction of page height a gap must span to count as a river.
        /// Default: 0.5 (50 %).
        /// </param>
        public static IReadOnlyList<(float X, float Y, float Width, float Height)> DetectColumns(
            PdfPageTextLayout layout,
            float minRiverWidth    = 10f,
            float minHeightFraction = 0.5f)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            _lastAnalysisWarning = null;
            var chars = layout.Chars;

            if (chars.Count < 4)
            {
                // An empty zone list is not "this page has no columns" — it is "this page was not
                // analysed", and the two are the same value to a caller.
                _lastAnalysisWarning =
                    $"the page carries {chars.Count} characters, too few to look for a column " +
                    "river; no zones were produced.";
                return Array.Empty<(float, float, float, float)>();
            }

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var c in chars)
            {
                if (c.X             < minX) minX = c.X;
                if (c.X + c.Width   > maxX) maxX = c.X + c.Width;
                if (c.Y             < minY) minY = c.Y;
                if (c.Y + c.Height  > maxY) maxY = c.Y + c.Height;
            }

            float pageWidth  = maxX - minX;
            float pageHeight = maxY - minY;
            if (pageWidth <= 0 || pageHeight <= 0)
            {
                // Every character shares one point, one column or one row, so there is no area to
                // divide. The zone returned is that degenerate extent, not a column.
                _lastAnalysisWarning =
                    $"the text occupies no area ({pageWidth} x {pageHeight} pt), so no column " +
                    "river can exist; the single zone returned is that extent.";
                return new[] { (minX, minY, pageWidth, pageHeight) };
            }

            // Build a 2-D coverage grid: xBuckets × yBands.
            // Both dimensions are clamped to a fixed resolution: pageWidth is
            // attacker-controlled (a single glyph at a huge X coordinate makes
            // maxX ≈ 1e9), so an unclamped xBuckets ≈ pageWidth would allocate a
            // multi-gigabyte grid → OOM on the default extraction path. We bucketise
            // X into at most MaxXBuckets columns, mirroring the yBands clamp.
            const int MaxXBuckets = 5000;
            int xBuckets = Math.Max(1, Math.Min(MaxXBuckets, SaturatingCast.ToInt(pageWidth) + 1));
            int yBands   = Math.Max(1, Math.Min(200, SaturatingCast.ToInt(pageHeight / 5f)));

            float bandW = pageWidth  / xBuckets;
            float bandH = pageHeight / yBands;

            // Coverage is accumulated as a 2-D DIFFERENCE array rather than by painting each
            // glyph's cells.
            //
            // Clamping the grid bounds the cell COUNT; it does not bound the FILL. A glyph whose
            // advance width and height span the page marks all 1e6 cells, and the glyph count is
            // file-controlled, so painting made the cost O(chars x cells) — measured at ~337M
            // marks/s, 50 000 such glyphs cost ~148 s for ONE page, inside what the caller believes
            // is a text extraction (DetectColumns is not opt-in: PdfPageTextLayout's constructor
            // runs it on every extracted page). A ceiling was added to bound that, and it did, by
            // TRUNCATING the analysis — the answer got cheaper by getting worse.
            //
            // Four increments per glyph and one prefix-sum pass over the fixed grid give the
            // identical coverage for O(chars + cells): the amplification is gone by construction,
            // so no ceiling and no truncated answer. The array is one row and one column larger
            // than the grid so the closing -1s of an edge-spanning rectangle have somewhere to
            // land instead of needing a bounds test per glyph.
            //
            // Flat rather than jagged: one allocation instead of one per x-bucket (5 001 of them on
            // a wide page), for the same cells. Measured, the two are within noise on wall clock —
            // the jagged form keeps its small arrays in gen 0 while this one is large enough for the
            // LOH — so this is chosen for being one object and one bounds-checked array, not for a
            // GC win. The change that matters is above it; do not read this line as the point.
            //
            // int, not a narrower type: the counts are rectangle starts, and a fixture with 50 000
            // glyphs sharing an origin puts 50 000 in one cell, which overflows short.
            int stride = yBands + 1;
            int[] coverage = RentCoverageGrid((xBuckets + 1) * stride);

            foreach (var c in chars)
            {
                int xStart = Math.Max(0, SaturatingCast.ToInt((c.X - minX)            / bandW));
                int xEnd   = Math.Min(xBuckets - 1, SaturatingCast.ToInt((c.X + c.Width  - minX) / bandW));
                int yStart = Math.Max(0, SaturatingCast.ToInt((c.Y - minY)            / bandH));
                int yEnd   = Math.Min(yBands   - 1, SaturatingCast.ToInt((c.Y + c.Height - minY) / bandH));

                // A glyph whose extent falls entirely outside the clamped grid inverts the pair.
                if (xEnd < xStart || yEnd < yStart) continue;

                int left = xStart * stride, right = (xEnd + 1) * stride;
                coverage[left  + yStart]++;
                coverage[right + yStart]--;
                coverage[left  + yEnd + 1]--;
                coverage[right + yEnd + 1]++;
            }

            // Prefix-sum down each axis: coverage[x, y] then holds the number of glyph rectangles
            // covering that cell, so "> 0" is exactly what the painted boolean grid held.
            for (int x = 0; x <= xBuckets; x++)
            {
                int b = x * stride;
                for (int y = 1; y <= yBands; y++) coverage[b + y] += coverage[b + y - 1];
            }
            for (int x = 1; x <= xBuckets; x++)
            {
                int b = x * stride, p = b - stride;
                for (int y = 0; y <= yBands; y++) coverage[b + y] += coverage[p + y];
            }

            // For each x-bucket, measure the fraction of y-bands that are empty.
            bool[] isRiverX = new bool[xBuckets];
            for (int x = 0; x < xBuckets; x++)
            {
                int b = x * stride;
                int empty = 0;
                for (int y = 0; y < yBands; y++)
                    if (coverage[b + y] <= 0) empty++;
                isRiverX[x] = (float)empty / yBands >= minHeightFraction;
            }

            // Collect contiguous river runs that meet the minimum width.
            // Bucket indices are converted back to PDF points via bandW so the
            // minRiverWidth / X comparisons stay in user-space units regardless
            // of the bucket resolution.
            var rivers = new List<(float X, float Width)>();
            int gStart = -1;
            for (int x = 0; x <= xBuckets; x++)
            {
                bool isRiver = x < xBuckets && isRiverX[x];
                if (isRiver  && gStart < 0) { gStart = x; }
                if (!isRiver && gStart >= 0)
                {
                    float rw = (x - gStart) * bandW;
                    if (rw >= minRiverWidth)
                        rivers.Add((minX + gStart * bandW, rw));
                    gStart = -1;
                }
            }

            // Deliberately NOT reported: no river means one column, which is the honest answer and
            // the commonest one. A diagnostic that fires on ordinary conforming input is one a
            // caller learns to ignore, which costs more than never having reported.
            if (rivers.Count == 0)
                return new[] { (minX, minY, pageWidth, pageHeight) };

            // Build column zones from the gaps between rivers.
            var cols = new List<(float X, float Y, float Width, float Height)>();
            float prev = minX;
            foreach (var (rx, rw) in rivers.OrderBy(r => r.X))
            {
                if (rx > prev + 1f)
                    cols.Add((prev, minY, rx - prev, pageHeight));
                prev = rx + rw;
            }
            if (maxX > prev + 1f)
                cols.Add((prev, minY, maxX - prev, pageHeight));

            if (cols.Count == 0)
            {
                // Rivers were found and between them they covered the page, leaving no column. A
                // sparse page reaches this: with the default half-height threshold, every x-bucket
                // of a page holding a few small glyphs is mostly whitespace and so is a river.
                _lastAnalysisWarning =
                    $"the {rivers.Count} river(s) found cover the page, leaving no column between " +
                    "them; the single zone returned is the page itself.";
                return new[] { (minX, minY, pageWidth, pageHeight) };
            }

            return cols;
        }
    }
}
