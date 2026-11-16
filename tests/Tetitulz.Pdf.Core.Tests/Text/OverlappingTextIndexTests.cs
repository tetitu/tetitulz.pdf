// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// The duplicate-overlap rule's index, asked directly: an independent implementation's rule — a later drawing of the same
/// text within a third of its per-character advance on both axes is a copy — with the window's edges
/// pinned (X inclusive, Y strict), and the grid that answers it taking both of its routes.
/// </summary>
public class OverlappingTextIndexTests
{
    [Fact]
    public void TheSameTextWithinATHirdOfItsAdvance_IsACopy_AndTheFirstDrawingIsKept()
    {
        var index = new OverlappingTextIndex<string>();
        Assert.False(index.IsCopyOfEarlier("fox", 100, 500, advance: 30, characters: 3));

        // Tolerance 30 / 3 / 3 = 3.333: inside on both axes.
        Assert.True(index.IsCopyOfEarlier("fox", 102, 502, 30, 3));
        // The same place, a different text: not a copy.
        Assert.False(index.IsCopyOfEarlier("dog", 100, 500, 30, 3));
    }

    [Fact]
    public void TheWindow_IsInclusiveInX_AndStrictInY()
    {
        var index = new OverlappingTextIndex<string>();
        index.IsCopyOfEarlier("a", 100, 500, advance: 9, characters: 1);   // tolerance 3

        Assert.True(index.IsCopyOfEarlier("a", 103, 500, 9, 1));           // X at the edge: inside
        Assert.False(index.IsCopyOfEarlier("a", 100, 503, 9, 1));          // Y at the edge: outside
        Assert.False(index.IsCopyOfEarlier("a", 103.01, 500, 9, 1));       // X past the edge
        Assert.False(index.IsCopyOfEarlier("a", 100, 496.9, 9, 1));        // Y past the edge, below
    }

    [Fact]
    public void ADrawingFarAwayInX_IsNotACopy_AndIsRememberedItself()
    {
        var index = new OverlappingTextIndex<string>();
        index.IsCopyOfEarlier("a", 100, 500, 9, 1);

        Assert.False(index.IsCopyOfEarlier("a", 200, 500, 9, 1));
        Assert.True(index.IsCopyOfEarlier("a", 201, 500, 9, 1));           // of the second drawing
        Assert.True(index.IsCopyOfEarlier("a", 99, 500, 9, 1));            // of the first
    }

    /// <summary>
    /// A window wider than the cells a query scans reads the key's whole list instead, and still
    /// finds the copy; a narrow window reads only its cells. The scan count is the evidence of the
    /// route, which the answer alone cannot show.
    /// </summary>
    [Fact]
    public void AWideWindow_ScansEveryPositionOfItsText_ANarrowOneOnlyItsCells()
    {
        var index = new OverlappingTextIndex<string>();
        for (int i = 0; i < 1000; i++) index.IsCopyOfEarlier("x", i * 10, 500, 9, 1);   // 1000 drawings, 10 apart

        // Tolerance 3 000 / 3 = 1 000 units: thousands of cells, so the whole list is read — all of it,
        // since nothing lies within the tolerance in Y.
        Assert.False(index.IsCopyOfEarlier("x", 5000, 9000, advance: 3000, characters: 1));
        Assert.Equal(1000, index.LastPositionsScanned);

        // Tolerance 3: the cells of [4 997, 5 003] hold two drawings at 5 000 — the wide query's own, far
        // off in Y and tried first as the newest, and the original, which is the copy.
        Assert.True(index.IsCopyOfEarlier("x", 5000.5, 500, 9, 1));
        Assert.Equal(2, index.LastPositionsScanned);
    }

    /// <summary>Two drawings of one text in one cell are both remembered: a copy of the older one is still a copy when a newer one sits in front of it.</summary>
    [Fact]
    public void TwoDrawingsInOneCell_AreBothRemembered()
    {
        var index = new OverlappingTextIndex<string>();
        index.IsCopyOfEarlier("a", 100.2, 500, 9, 1);
        index.IsCopyOfEarlier("a", 100.8, 900, 9, 1);   // the same cell, a different line

        Assert.True(index.IsCopyOfEarlier("a", 100.5, 500, 9, 1));
        Assert.True(index.IsCopyOfEarlier("a", 100.5, 900, 9, 1));
        Assert.False(index.IsCopyOfEarlier("a", 100.5, 700, 9, 1));
    }

    /// <summary>
    /// A column of one text at one X is one cell, and each drawing reads every one before it: 300
    /// drawings are 44 850 comparisons. Under a budget of 1 000 the rule stops — a query past it
    /// compares nothing, so the total stays within the budget and one query — and says so; a copy
    /// is then not looked for. The control: the same column under a budget it fits finds its copy.
    /// </summary>
    [Fact]
    public void AColumnOfOneText_IsBoundedByTheComparisonBudget()
    {
        static OverlappingTextIndex<string> Column(Tetitulz.Pdf.Core.Util.WorkBudget budget)
        {
            var index = new OverlappingTextIndex<string> { Comparisons = budget };
            for (int i = 0; i < 300; i++) index.IsCopyOfEarlier("a", 100, i * 10, 9, 1);
            return index;
        }

        var small = new Tetitulz.Pdf.Core.Util.WorkBudget(1000, "test");
        OverlappingTextIndex<string> stopped = Column(small);
        Assert.True(stopped.CeilingReached);
        Assert.InRange(small.Spent, 1000, 1000 + 300);
        Assert.False(stopped.IsCopyOfEarlier("a", 100, 0, 9, 1));   // the first drawing's copy, not looked for
        Assert.Equal(0, stopped.LastPositionsScanned);

        var ample = new Tetitulz.Pdf.Core.Util.WorkBudget(1_000_000, "test");
        OverlappingTextIndex<string> whole = Column(ample);
        Assert.False(whole.CeilingReached);
        Assert.Equal(300L * 299 / 2, ample.Spent);
        Assert.True(whole.IsCopyOfEarlier("a", 100, 0, 9, 1));
    }

    [Fact]
    public void ANonFiniteTolerance_IsNeverACopy()
    {
        var index = new OverlappingTextIndex<string>();
        index.IsCopyOfEarlier("a", 100, 500, 9, 1);

        Assert.False(index.IsCopyOfEarlier("a", 100, 500, double.PositiveInfinity, 1));
        Assert.False(index.IsCopyOfEarlier("a", 100, 500, double.NaN, 1));
        Assert.False(index.IsCopyOfEarlier("a", 100, 500, 9, 0));
    }

    /// <summary>A glyph an overflowing matrix leaves at infinity is indexed and compared like any other, and nothing throws.</summary>
    [Fact]
    public void ADrawingAtInfinity_IsIndexed_AndACopyThereIsStillACopy()
    {
        var index = new OverlappingTextIndex<string>();
        Assert.False(index.IsCopyOfEarlier("a", double.NegativeInfinity, 500, 9, 1));
        Assert.True(index.IsCopyOfEarlier("a", double.NegativeInfinity, 500, 9, 1));
        Assert.False(index.IsCopyOfEarlier("a", 100, 500, 9, 1));
        Assert.False(index.IsCopyOfEarlier("a", double.NaN, 500, 9, 1));
    }
}
