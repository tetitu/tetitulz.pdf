// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

namespace Tetitulz.Pdf.Base.Operations.Extraction
{
    /// <summary>
    /// Spatial / geometric queries over an already-extracted <see cref="PdfPageTextLayout"/>.
    /// No re-parsing of the PDF is required — these operate purely on the word/line bounding boxes
    /// in PDF page coordinates (origin bottom-left, Y-up, units = points).
    ///
    /// <para>This is the backbone of label to value field extraction (see
    /// <see cref="Forms.PdfFieldExtractor"/>): find the word in a region, or the value word adjacent to a
    /// label.  The adjacency helpers are RTL-aware — for a right-to-left label the value sits to its
    /// <em>left</em>, for a left-to-right label it sits to its <em>right</em>.</para>
    /// </summary>
    public static class PdfSpatialQuery
    {
        /// <summary>
        /// Last non-fatal issue (e.g. a null anchor or no candidate found). <c>null</c> after a query
        /// that returned a result.
        /// <para>
        /// Backed by a <c>[ThreadStatic]</c> field, so a concurrent query on another thread
        /// cannot overwrite it — read it on the thread that ran the query (see the Diagnostics
        /// Pattern section of CLAUDE.md).
        /// </para>
        /// </summary>
        public static string LastError
        {
            get => _lastError;
            private set => _lastError = value;
        }

        [System.ThreadStatic]
        private static string _lastError;

        // ----------------------------------------------------------------
        //  Region queries
        // ----------------------------------------------------------------

        /// <summary>Returns every word whose bounding box intersects the given rectangle.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="width"/> or
        /// <paramref name="height"/> is negative.</exception>
        public static IReadOnlyList<PdfTextWord> WordsInRect(
            PdfPageTextLayout page, float x, float y, float width, float height)
        {
            RequireNonNegativeExtent(width, height);
            var result = new List<PdfTextWord>();
            if (page == null) { LastError = "page is null"; return result; }
            LastError = null;
            foreach (var w in AllWords(page))
                if (RectIntersects(w.X, w.Y, w.Width, w.Height, x, y, width, height))
                    result.Add(w);
            return result;
        }

        /// <summary>Returns every line whose bounding box intersects the given rectangle.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="width"/> or
        /// <paramref name="height"/> is negative.</exception>
        public static IReadOnlyList<PdfTextLine> LinesInRect(
            PdfPageTextLayout page, float x, float y, float width, float height)
        {
            RequireNonNegativeExtent(width, height);
            var result = new List<PdfTextLine>();
            if (page == null) { LastError = "page is null"; return result; }
            LastError = null;
            foreach (var line in TextLayouts.LinesOf(page))
                if (RectIntersects(line.X, line.Y, line.Width, line.Height, x, y, width, height))
                    result.Add(line);
            return result;
        }

        // ----------------------------------------------------------------
        //  Adjacency queries
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns the nearest word that lies to the right of <paramref name="anchor"/> on the same
        /// baseline (within <paramref name="yTol"/>) and no farther than <paramref name="maxGap"/>
        /// points away, or <c>null</c> if there is none.
        /// </summary>
        public static PdfTextWord WordToTheRightOf(
            PdfPageTextLayout page, PdfTextWord anchor, float maxGap = 200f, float yTol = 4f)
        {
            if (page == null || anchor == null) { LastError = "page or anchor is null"; return null; }
            float anchorRight = anchor.X + anchor.Width;
            PdfTextWord best = null;
            float bestGap = float.MaxValue;
            foreach (var w in AllWords(page))
            {
                if (ReferenceEquals(w, anchor)) continue;
                if (Math.Abs(w.Y - anchor.Y) > yTol) continue;
                float gap = w.X - anchorRight;
                if (gap < -0.5f || gap > maxGap) continue; // must be to the right, within range
                if (gap < bestGap) { bestGap = gap; best = w; }
            }
            LastError = best == null ? "no word to the right" : null;
            return best;
        }

        /// <summary>
        /// Returns the nearest word that lies to the left of <paramref name="anchor"/> on the same
        /// baseline (within <paramref name="yTol"/>) and no farther than <paramref name="maxGap"/>
        /// points away, or <c>null</c> if there is none.
        /// </summary>
        public static PdfTextWord WordToTheLeftOf(
            PdfPageTextLayout page, PdfTextWord anchor, float maxGap = 200f, float yTol = 4f)
        {
            if (page == null || anchor == null) { LastError = "page or anchor is null"; return null; }
            PdfTextWord best = null;
            float bestGap = float.MaxValue;
            foreach (var w in AllWords(page))
            {
                if (ReferenceEquals(w, anchor)) continue;
                if (Math.Abs(w.Y - anchor.Y) > yTol) continue;
                float gap = anchor.X - (w.X + w.Width);
                if (gap < -0.5f || gap > maxGap) continue; // must be to the left, within range
                if (gap < bestGap) { bestGap = gap; best = w; }
            }
            LastError = best == null ? "no word to the left" : null;
            return best;
        }

        /// <summary>
        /// Returns the nearest word below <paramref name="anchor"/> (smaller Y) that horizontally
        /// overlaps it within <paramref name="xTol"/>, no farther than <paramref name="maxGap"/>
        /// points below, or <c>null</c> if there is none.
        /// </summary>
        public static PdfTextWord WordBelow(
            PdfPageTextLayout page, PdfTextWord anchor, float maxGap = 40f, float xTol = 20f)
        {
            if (page == null || anchor == null) { LastError = "page or anchor is null"; return null; }
            PdfTextWord best = null;
            float bestGap = float.MaxValue;
            foreach (var w in AllWords(page))
            {
                if (ReferenceEquals(w, anchor)) continue;
                float vGap = anchor.Y - w.Y;
                if (vGap <= 0.5f || vGap > maxGap) continue; // must be below, within range
                // Require horizontal proximity of the left edges (or overlap).
                bool overlaps = w.X + w.Width > anchor.X - xTol && w.X < anchor.X + anchor.Width + xTol;
                if (!overlaps) continue;
                if (vGap < bestGap) { bestGap = vGap; best = w; }
            }
            LastError = best == null ? "no word below" : null;
            return best;
        }

        /// <summary>
        /// Returns the value word associated with a label word, choosing the reading-direction-aware
        /// side: the word to the <em>left</em> for an RTL label, to the <em>right</em> for an LTR
        /// label, falling back to the word directly below.  <c>null</c> when nothing is found.
        /// </summary>
        public static PdfTextWord ValueWordFor(
            PdfPageTextLayout page, PdfTextWord label, float maxGap = 200f, float yTol = 4f)
        {
            if (page == null || label == null) { LastError = "page or label is null"; return null; }
            var value = label.IsRtl
                ? WordToTheLeftOf(page, label, maxGap, yTol)
                : WordToTheRightOf(page, label, maxGap, yTol);
            if (value == null)
                value = WordBelow(page, label);
            LastError = value == null ? "no value word for label" : null;
            return value;
        }

        /// <summary>Returns the word whose centre is closest to the given point, or <c>null</c>.</summary>
        public static PdfTextWord NearestWord(PdfPageTextLayout page, float x, float y)
        {
            if (page == null) { LastError = "page is null"; return null; }
            PdfTextWord best = null;
            float bestDist = float.MaxValue;
            foreach (var w in AllWords(page))
            {
                float cx = w.X + w.Width * 0.5f;
                float cy = w.Y + w.Height * 0.5f;
                float dx = cx - x, dy = cy - y;
                float dist = dx * dx + dy * dy;
                if (dist < bestDist) { bestDist = dist; best = w; }
            }
            LastError = best == null ? "no words on page" : null;
            return best;
        }

        // ----------------------------------------------------------------
        //  Helpers
        // ----------------------------------------------------------------

        // A layout is public and constructible: its null lines and words are passed over by the one
        // rule every layout walk shares.
        private static IEnumerable<PdfTextWord> AllWords(PdfPageTextLayout page)
            => TextLayouts.LinesOf(page).SelectMany(TextLayouts.WordsOf);

        /// <summary>
        /// Refuses a rectangle with a negative extent, as <c>PdfPageTextLayout.Crop</c> does.
        /// </summary>
        /// <remarks>
        /// <b>The predicate was shared; the contract was not.</b> Crop filters characters through
        /// <see cref="RectIntersects"/> and refuses a negative width by name, while these queries
        /// accepted one — and a negative width does not yield nothing. It inverts the interval, so
        /// <c>bx + bw &gt; x &amp;&amp; bx &lt; x + w</c> still holds for a word that <i>spans</i>
        /// the gap, and the caller is handed words from a region it never described. That is a
        /// confidently wrong answer where the other caller of the same predicate refuses.
        /// </remarks>
        private static void RequireNonNegativeExtent(float width, float height)
        {
            if (width < 0f)
                throw new ArgumentOutOfRangeException(nameof(width), "width must be non-negative");
            if (height < 0f)
                throw new ArgumentOutOfRangeException(nameof(height), "height must be non-negative");
        }

        /// <summary>
        /// Axis-aligned bounding-box intersection: the one predicate, reached from the region
        /// queries above and from <c>PdfPageTextLayout.Crop</c>.
        /// </summary>
        /// <remarks>
        /// Crop asks it too, rather than open-coding "the same predicate".
        /// <para><b>Strict on every edge.</b> A box whose right edge lands exactly on the
        /// rectangle's left edge shares no area with it, so touching is not intersecting.</para>
        /// </remarks>
        public static bool RectIntersects(
            float bx, float by, float bw, float bh,
            float x, float y, float w, float h)
            => bx + bw > x && bx < x + w && by + bh > y && by < y + h;
    }
}
