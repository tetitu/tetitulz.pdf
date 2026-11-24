// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <see cref="DisplayListIndex"/>'s Y-range query in isolation,
/// against hand-built <see cref="DisplayListEntry"/> fixtures rather than a rendered page — the
/// index answers a purely geometric question and does not need a content stream to prove it.
/// </summary>
public sealed class DisplayListIndexTests
{
    private static DisplayListEntry Entry(int opIndex, float top, float bottom, float left = 0f, float right = 10f)
        => new DisplayListEntry(opIndex, new SKRect(left, top, right, bottom));

    [Fact]
    public void AnEntryWhollyInsideTheBand_IsReturned()
    {
        var index = new DisplayListIndex(new[] { Entry(0, top: 10, bottom: 20) });

        var hits = index.Query(bandTop: 0, bandBottom: 30);

        Assert.Equal(new[] { 0 }, hits);
    }

    [Fact]
    public void AnEntryEntirelyAboveTheBand_IsExcluded()
    {
        var index = new DisplayListIndex(new[] { Entry(0, top: 0, bottom: 5) });

        var hits = index.Query(bandTop: 10, bandBottom: 20);

        Assert.Empty(hits);
    }

    [Fact]
    public void AnEntryEntirelyBelowTheBand_IsExcluded()
    {
        var index = new DisplayListIndex(new[] { Entry(0, top: 25, bottom: 30) });

        var hits = index.Query(bandTop: 10, bandBottom: 20);

        Assert.Empty(hits);
    }

    [Fact]
    public void ATallEntryStartingAboveTheBandButReachingIntoIt_IsReturned()
    {
        // The regression this test exists for: a naive "binary-search Top >= bandTop, scan
        // forward" index would skip this entry entirely, because its Top sorts well before every
        // entry between it and the band -- exactly the case DisplayListIndex's running-max-Bottom
        // lower bound (see the type's remarks) exists to still find.
        var index = new DisplayListIndex(new[]
        {
            Entry(0, top: 0, bottom: 100),   // starts at the very top of the page, reaches past every band
            Entry(1, top: 5, bottom: 6),
            Entry(2, top: 6, bottom: 7),
            Entry(3, top: 7, bottom: 8),
        });

        var hits = index.Query(bandTop: 50, bandBottom: 60);

        Assert.Equal(new[] { 0 }, hits);
    }

    [Fact]
    public void TheBandsUpperEdgeIsInclusive_TheLowerEdgeIsNot()
    {
        // [bandTop, bandBottom) is half-open: an entry that ends exactly at bandTop does not
        // paint into the band; an entry that starts exactly at bandTop does.
        var index = new DisplayListIndex(new[]
        {
            Entry(0, top: 0, bottom: 10),    // ends exactly at bandTop -- excluded
            Entry(1, top: 10, bottom: 20),   // starts exactly at bandTop -- included
        });

        var hits = index.Query(bandTop: 10, bandBottom: 20);

        Assert.Equal(new[] { 1 }, hits);
    }

    [Fact]
    public void AnEntryStartingExactlyAtTheBandsLowerEdge_IsExcluded()
    {
        var index = new DisplayListIndex(new[] { Entry(0, top: 20, bottom: 30) });

        var hits = index.Query(bandTop: 10, bandBottom: 20);

        Assert.Empty(hits);
    }

    [Fact]
    public void TwoEntriesFromTheSameOperator_CollapseToOneHit()
    {
        // Models B (fill-and-stroke): one op index, two PaintBoundsRecorder calls with different
        // bounds. The index must not hand a consumer the same op twice.
        var index = new DisplayListIndex(new[]
        {
            Entry(0, top: 10, bottom: 15),
            Entry(0, top: 8, bottom: 17),
        });

        var hits = index.Query(bandTop: 0, bandBottom: 30);

        Assert.Equal(new[] { 0 }, hits);
    }

    [Fact]
    public void HitsAreReturnedInAscendingOpIndexOrder_RegardlessOfInsertionOrPaintOrder()
    {
        var index = new DisplayListIndex(new[]
        {
            Entry(5, top: 12, bottom: 14),
            Entry(1, top: 10, bottom: 11),
            Entry(3, top: 16, bottom: 18),
        });

        var hits = index.Query(bandTop: 0, bandBottom: 30);

        Assert.Equal(new[] { 1, 3, 5 }, hits);
    }

    [Fact]
    public void AnEmptyDisplayList_QueriesToNoHits()
    {
        var index = new DisplayListIndex(new List<DisplayListEntry>());

        var hits = index.Query(bandTop: 0, bandBottom: 1000);

        Assert.Empty(hits);
    }

    [Fact]
    public void ManyShortEntriesBetweenTwoTallOnes_AllReachingIntoTheBandAreFound()
    {
        // Exercises "the scan cannot stop at the first miss": entry 0 is tall and reaches the
        // band, entries 1..8 are short misses scattered between it and the band, entry 9 is a
        // second tall one. A scan that broke on the first short miss would report only {0}.
        var entries = new List<DisplayListEntry> { Entry(0, top: 0, bottom: 60) };
        for (int i = 1; i <= 8; i++)
            entries.Add(Entry(i, top: i, bottom: i + 1));
        entries.Add(Entry(9, top: 55, bottom: 65));

        var index = new DisplayListIndex(entries);

        var hits = index.Query(bandTop: 50, bandBottom: 60);

        Assert.Equal(new[] { 0, 9 }, hits);
    }

    /// <summary>
    /// The rectangle query keeps the band's entries that also overlap the
    /// half-open column <c>[left, right)</c> — one left of it, one right of it and one touching only
    /// its right edge are out; one straddling its left edge and one touching only its left edge from
    /// inside are in.
    /// </summary>
    [Fact]
    public void TheRectangleQueryKeepsOnlyTheColumnsEntries()
    {
        var index = new DisplayListIndex(new[]
        {
            Entry(0, top: 10, bottom: 20, left: 0, right: 40),     // left of the column [50, 100)
            Entry(1, top: 10, bottom: 20, left: 120, right: 140),  // right of it
            Entry(2, top: 10, bottom: 20, left: 100, right: 110),  // starts exactly at its right edge: out
            Entry(3, top: 10, bottom: 20, left: 30, right: 60),    // straddles its left edge
            Entry(4, top: 10, bottom: 20, left: 40, right: 50),    // ends exactly at its left edge: out
            Entry(5, top: 10, bottom: 20, left: 99, right: 100),   // its last point inside
        });

        Assert.Equal(new[] { 3, 5 }, index.Query(0, 30, left: 50, right: 100));
        // The band form is the rectangle at unbounded width: every entry the band meets.
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, index.Query(0, 30));
    }

    /// <summary>The rectangle query still honours the band: an entry in the column but above the band is out.</summary>
    [Fact]
    public void TheRectangleQueryStillHonoursTheBand()
    {
        var index = new DisplayListIndex(new[]
        {
            Entry(0, top: 0, bottom: 5, left: 60, right: 70),
            Entry(1, top: 12, bottom: 18, left: 60, right: 70),
        });

        Assert.Equal(new[] { 1 }, index.Query(10, 20, left: 50, right: 100));
    }
}
