// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// Where text has been drawn so far, to recognise the same text drawn again at nearly the same
    /// place.
    /// </summary>
    /// <remarks>
    /// <para><b>Visible text is often drawn twice.</b> A layout program strokes and then fills its
    /// slug line at one position, "fake bold" repaints a run a fraction of a point to the side, and an
    /// invisible layer can repeat the visible text over it. A reader wants one copy.</para>
    /// <para><b>The rule is the established text stripper's</b>
    /// <c>suppressDuplicateOverlappingText</c>, which is on by default there: a later drawing is a copy
    /// when an earlier drawing with the same text lies within a third of its per-character advance on
    /// both axes. The earlier one is kept.</para>
    /// <para><b>Both extractors apply it, through this one implementation</b> — structured extraction
    /// per glyph, keyed on the glyph's text and marked-content sequence, and plain extraction per show
    /// operator, keyed on the text it shows.</para>
    /// <para><b>The index is a grid, not a sorted set, for the allocation.</b> A <c>SortedSet</c> of positions
    /// per key, asked for a view over the X window per glyph, would cost a tree node and a view
    /// object per glyph — the largest per-glyph allocation plain extraction would have, with a
    /// code's text from the font's cache (GAPS.md §13b). Each key holds its
    /// positions in one list and a hash from a one-unit cell of X to the chain of positions in it;
    /// a query scans the cells the window covers (a handful, at a glyph's tolerance) and a drawing
    /// costs the list's growth and one dictionary entry. The predicate is the same: X within the
    /// tolerance inclusive, Y within it strictly.</para>
    /// <para><b>The comparisons are counted against <see cref="Comparisons"/></b>
    /// (<see cref="Constants.PdfLimits.MaxOverlappingTextComparisons"/> a page). A column of one
    /// text at one X puts every drawing in one cell, and each query then reads all the earlier ones:
    /// quadratic in the page's glyphs. Past the budget a query compares nothing and answers "not a
    /// copy", and <see cref="CeilingReached"/> says the rule stopped.</para>
    /// </remarks>
    /// <typeparam name="TKey">What must be equal for two drawings to be the same text.</typeparam>
    internal sealed class OverlappingTextIndex<TKey>
    {
        /// <summary>
        /// What the queries' comparisons are charged to: the index's own budget for a page, made
        /// at construction and at <see cref="Reset"/>, or one a caller shares among the indexes of a
        /// page and its forms.
        /// </summary>
        internal Util.WorkBudget Comparisons { get; set; } = NewComparisonBudget();

        /// <summary>Whether a query found <see cref="Comparisons"/> spent, so later copies were not looked for.</summary>
        internal bool CeilingReached { get; private set; }

        /// <summary>A page's comparison budget, under <see cref="Constants.PdfLimits.MaxOverlappingTextComparisons"/>.</summary>
        internal static Util.WorkBudget NewComparisonBudget() => new Util.WorkBudget(
            Constants.PdfLimits.MaxOverlappingTextComparisons,
            "the duplicate-text comparisons of a page (PdfLimits.MaxOverlappingTextComparisons)");

        /// <summary>The X extent of one cell, in the caller's (device) units.</summary>
        internal const double CellSize = 1.0;

        /// <summary>
        /// Cells a query scans before it reads the key's whole list instead: a window wider than
        /// this is a glyph several hundred units across, of which a page has few, and the scan of
        /// every position of its text is cheaper than the cells.
        /// </summary>
        internal const int MaxCellsScanned = 256;

        /// <summary>The cell a non-finite, or astronomically large, X falls in.</summary>
        private const long OutOfRangeCell = long.MinValue;

        private readonly Dictionary<TKey, Entry> _drawn = new Dictionary<TKey, Entry>();

        /// <summary>Positions scanned by the last query — the evidence, for a test, of which route it took.</summary>
        internal int LastPositionsScanned { get; private set; }

        private sealed class Entry
        {
            internal readonly List<(double X, double Y)> Positions = new List<(double X, double Y)>();
            /// <summary>The index into <see cref="Positions"/> of the newest drawing in each cell.</summary>
            internal readonly Dictionary<long, int> CellHead = new Dictionary<long, int>();
            /// <summary>For each position, the index of the previous drawing in its cell, or -1.</summary>
            internal readonly List<int> NextInCell = new List<int>();
        }

        internal bool IsCopyOfEarlier(TKey key, double x, double y, double advance, int characters)
        {
            double tolerance = Math.Abs(advance) / characters / 3;

            // An overflowing text matrix can leave a glyph at X = -infinity with an infinite advance.
            // A drawing at a finite place with an infinite advance would otherwise be a copy of every
            // earlier drawing of its text. And no characters divides by zero, into one or the other.
            if (double.IsNaN(tolerance) || double.IsInfinity(tolerance)) return false;

            LastPositionsScanned = 0;
            if (!_drawn.TryGetValue(key, out Entry entry))
            {
                _drawn[key] = entry = new Entry();
            }
            else if (IsNear(entry, x, y, tolerance))
            {
                return true;
            }

            Record(entry, x, y);
            return false;
        }

        private bool IsNear(Entry entry, double x, double y, double tolerance)
        {
            Util.WorkBudget budget = Comparisons;
            if (budget != null && budget.IsExhausted)
            {
                CeilingReached = true;
                return false;
            }
            bool near = Scan(entry, x, y, tolerance);
            if (budget != null && LastPositionsScanned > 0)
            {
                try { budget.Tick(LastPositionsScanned); }
                catch (Util.WorkBudgetExceededException) { CeilingReached = true; }
            }
            return near;
        }

        private bool Scan(Entry entry, double x, double y, double tolerance)
        {
            long first = CellOf(x - tolerance), last = CellOf(x + tolerance);
            bool scanWhole = first == OutOfRangeCell || last == OutOfRangeCell || last - first >= MaxCellsScanned;

            if (scanWhole)
            {
                List<(double X, double Y)> positions = entry.Positions;
                for (int i = 0; i < positions.Count; i++)
                {
                    LastPositionsScanned++;
                    if (Within(positions[i], x, y, tolerance)) return true;
                }
                return false;
            }

            for (long cell = first; cell <= last; cell++)
            {
                if (!entry.CellHead.TryGetValue(cell, out int i)) continue;
                while (i >= 0)
                {
                    LastPositionsScanned++;
                    if (Within(entry.Positions[i], x, y, tolerance)) return true;
                    i = entry.NextInCell[i];
                }
            }
            return false;
        }

        /// <summary>
        /// X within the tolerance inclusive, Y within it strictly — the sorted set's inclusive view and
        /// the strict Y test, as before. Two drawings at the same infinity are at the same X (their
        /// difference is NaN, which no comparison admits), as the view's inclusive bounds had them.
        /// </summary>
        private static bool Within((double X, double Y) p, double x, double y, double tolerance)
            => (p.X == x || Math.Abs(p.X - x) <= tolerance) && Math.Abs(p.Y - y) < tolerance;

        /// <summary>Keys an index keeps across a <see cref="Reset"/>; past it the index is not reused.</summary>
        internal const int MaxRetainedKeys = 1024;

        /// <summary>Positions an index may have held and still be reused, so a huge page's lists are not kept.</summary>
        internal const int MaxRetainedPositions = 1 << 16;

        /// <summary>
        /// Empties the index for another page, keeping its keys and its lists' capacity — so a
        /// document's pages, which draw the same few dozen glyph texts, stop growing a list per key
        /// from empty on every page. <c>false</c> (and nothing kept) when the index has grown past
        /// what is worth keeping.
        /// </summary>
        internal bool Reset()
        {
            LastPositionsScanned = 0;
            CeilingReached = false;
            Comparisons = NewComparisonBudget();
            if (_drawn.Count > MaxRetainedKeys) return false;
            int positions = 0;
            foreach (Entry entry in _drawn.Values) positions += entry.Positions.Capacity;
            if (positions > MaxRetainedPositions) return false;

            foreach (Entry entry in _drawn.Values)
            {
                entry.Positions.Clear();
                entry.CellHead.Clear();
                entry.NextInCell.Clear();
            }
            return true;
        }

        private static void Record(Entry entry, double x, double y)
        {
            int index = entry.Positions.Count;
            entry.Positions.Add((x, y));
            long cell = CellOf(x);
            entry.NextInCell.Add(entry.CellHead.TryGetValue(cell, out int previous) ? previous : -1);
            entry.CellHead[cell] = index;
        }

        private static long CellOf(double x)
        {
            double cell = Math.Floor(x / CellSize);
            // NaN fails both comparisons and lands out of range with the infinities.
            return cell > -1e15 && cell < 1e15 ? SaturatingCast.ToLong(cell) : OutOfRangeCell;
        }
    }
}
