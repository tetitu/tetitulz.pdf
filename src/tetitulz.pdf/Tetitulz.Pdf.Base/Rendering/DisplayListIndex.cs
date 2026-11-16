// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// A spatial index over a <see cref="DisplayListEntry"/> list for the two queries the session
    /// asks: a horizontal Y-range (<see cref="PdfRenderSession.RenderPageBands"/>) and a rectangle
    /// (<see cref="PdfRenderSession.RenderPageTileToPixels"/>).
    /// The rectangle narrows by Y first, through the same search, and filters the candidates by X —
    /// a full 2-D structure (quadtree, R-tree) would not pay for itself at a page's entry counts.
    /// </summary>
    /// <remarks>
    /// <b>Entries sorted once by <see cref="DisplayListEntry.PageBounds"/>.Top, not Bottom.</b>
    /// Because a later-painted, taller entry can start earlier and reach further down than
    /// entries between it and the band, <see cref="Query(float, float, float, float)"/> cannot simply binary-search for the
    /// first entry with <c>Top &gt;= bandTop</c> — a scan starting there would silently skip a
    /// tall entry that started above the band and still paints into it. Instead the constructor
    /// also builds a running maximum of <c>Bottom</c> over the Top-sorted order; that prefix is
    /// monotonically non-decreasing, so a binary search over it finds the first index before
    /// which <b>no</b> entry — however early it starts — can reach past <c>bandTop</c>. From
    /// there the scan is linear and cannot stop at the first individual miss (entries are not
    /// sorted by Bottom, so one short entry can sit between two tall ones); it stops only at the
    /// first <c>Top</c> at or past <c>bandBottom</c>, since nothing starting there or later can
    /// intersect a half-open band ending at <c>bandBottom</c>. Correct, and cheap, because a
    /// content stream's entries number in the thousands here, not the millions <c>PdfLimits</c>
    /// already caps elsewhere.
    /// </remarks>
    internal sealed class DisplayListIndex
    {
        private readonly DisplayListEntry[] _byTop;
        private readonly float[] _runningMaxBottom;

        public DisplayListIndex(IReadOnlyList<DisplayListEntry> entries)
        {
            _byTop = new DisplayListEntry[entries.Count];
            for (int i = 0; i < entries.Count; i++) _byTop[i] = entries[i];
            Array.Sort(_byTop, (a, b) => a.PageBounds.Top.CompareTo(b.PageBounds.Top));

            _runningMaxBottom = new float[_byTop.Length];
            float runningMax = float.NegativeInfinity;
            for (int i = 0; i < _byTop.Length; i++)
            {
                float bottom = _byTop[i].PageBounds.Bottom;
                if (bottom > runningMax) runningMax = bottom;
                _runningMaxBottom[i] = runningMax;
            }
        }

        /// <summary>
        /// Every operation index whose reported bound could paint into the half-open band
        /// <c>[bandTop, bandBottom)</c> — page-point space, the same convention
        /// <see cref="DisplayListEntry.PageBounds"/> and <see cref="PdfRenderSession"/>'s own band
        /// geometry both use. Ascending, with no duplicate op index even when the same operator
        /// reported more than one bound (e.g. <c>B</c>'s fill and stroke).
        /// </summary>
        public IReadOnlyList<int> Query(float bandTop, float bandBottom)
            => Query(bandTop, bandBottom, float.NegativeInfinity, float.PositiveInfinity);

        /// <summary>
        /// The same for the half-open rectangle <c>[left, right) × [top, bottom)</c>, page-point
        /// space: an entry is a hit when it overlaps the band <i>and</i> the column. The band form is
        /// this one at an unbounded width.
        /// </summary>
        public IReadOnlyList<int> Query(float bandTop, float bandBottom, float left, float right)
        {
            List<int> hits = null;
            int start = LowerBound(bandTop);
            for (int i = start; i < _byTop.Length; i++)
            {
                var entry = _byTop[i];
                if (entry.PageBounds.Top >= bandBottom) break;
                if (entry.PageBounds.Bottom > bandTop && entry.PageBounds.Left < right && entry.PageBounds.Right > left)
                    (hits ??= new List<int>()).Add(entry.OpIndex);
            }
            if (hits == null) return Array.Empty<int>();

            hits.Sort();
            int write = 1;
            for (int read = 1; read < hits.Count; read++)
            {
                if (hits[read] != hits[write - 1]) hits[write++] = hits[read];
            }
            hits.RemoveRange(write, hits.Count - write);
            return hits;
        }

        /// <summary>
        /// First index in Top order before which no entry's running-maximum <c>Bottom</c> exceeds
        /// <paramref name="bandTop"/> — see the type's remarks for why this, not a search on
        /// <c>Top</c> alone, is the correct starting point.
        /// </summary>
        private int LowerBound(float bandTop)
        {
            int lo = 0, hi = _runningMaxBottom.Length;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (_runningMaxBottom[mid] > bandTop) hi = mid;
                else lo = mid + 1;
            }
            return lo;
        }
    }
}
