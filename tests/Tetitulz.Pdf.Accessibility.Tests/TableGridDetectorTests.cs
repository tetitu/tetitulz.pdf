// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.Accessibility.Internal;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <see cref="TableGridDetector"/> on its own, as a grid of numbers.
/// </summary>
/// <remarks>
/// <para><b>Three claims cannot be reached through a PDF, and a mutation run is what showed
/// it.</b> <see cref="TableInferenceTests"/> builds real content streams, and a real content stream
/// draws its cells left to right with finite coordinates — so the sort that makes the column set
/// order-independent, the disqualification of a line whose shows are unknown, and the rejection of
/// a non-finite edge were all satisfied by fixtures that never put the detector in the state they
/// guard. Deleting each of the three left the whole suite green.</para>
///
/// <para><b>These are unit tests of an internal contract, deliberately.</b> The detector takes
/// three parallel arrays and the caller in the tagger fills them one way; the guards exist for the
/// inputs it does not. Testing them here says what <c>Detect</c> promises, rather than what one
/// caller happens to hand it.</para>
/// </remarks>
public sealed class TableGridDetectorTests
{
    private const float Tolerance = 1f;

    /// <summary>A three-column, two-row grid is one table.</summary>
    /// <remarks>
    /// The acceptance control every refusal below is measured against: without it, a detector that
    /// answered "no table" to everything would pass this whole file.
    /// </remarks>
    [Fact]
    public void AnAlignedGridIsOneTable()
    {
        TableGridDetector.Grid grid = Detect(
            new[] { 700f, 670f },
            new[] { 12f, 12f },
            new[] { new[] { 72f, 240f, 400f }, new[] { 72f, 240f, 400f } });

        Assert.Equal(1, grid.TableCount);
        Assert.Equal(new[] { 0, 0 }, grid.TableOfLine);
        Assert.Equal(new[] { 0, 1 }, grid.RowOfLine);
        Assert.Equal(new[] { 3, 3 }, grid.ColumnsOfLine);
    }

    /// <summary>A row whose cells are drawn right to left is the same grid.</summary>
    /// <remarks>
    /// <b>The claim the sort exists for, and the one no PDF fixture reaches.</b> A row's column set
    /// is a set — where its edges are, not the order the stream visits them — so a producer that
    /// draws the rightmost cell first must produce the same table as one that does not. Compared
    /// position by position without sorting, the two rows above disagree in every entry and the run
    /// is refused.
    /// <para>
    /// The <em>cells</em> still read in content order; only the grid is order-independent. That
    /// separation is the whole reason the column index is carried per show rather than inferred
    /// from position.
    /// </para>
    /// </remarks>
    [Fact]
    public void ARowDrawnRightToLeftIsStillTheSameGrid()
    {
        TableGridDetector.Grid grid = Detect(
            new[] { 700f, 670f },
            new[] { 12f, 12f },
            new[] { new[] { 72f, 240f, 400f }, new[] { 400f, 240f, 72f } });

        Assert.Equal(1, grid.TableCount);

        // And the columns are read off the X, so the reversed row's shows carry 2, 1, 0 — the
        // cells stay in the order the stream drew them and still know where they belong.
        Assert.Equal(new[] { 0, 1, 2 }, grid.ColumnOfShow[0]);
        Assert.Equal(new[] { 2, 1, 0 }, grid.ColumnOfShow[1]);
    }

    /// <summary>A line whose shows are unknown disqualifies its row.</summary>
    /// <remarks>
    /// <b>A null entry means "this line's positions were not recorded", which is not the same as
    /// "this line has no cells".</b> Letting it through would build the row's column set out of
    /// the lines that did report, and claim a grid for a row half of which was never measured. The
    /// tagger's own call never passes null — <c>ShowXs</c> always returns an array — so this is a
    /// contract of <c>Detect</c> rather than a path that caller reaches, and it is stated here
    /// because nothing else can state it.
    /// </remarks>
    [Fact]
    public void ALineWithNoRecordedShowsDisqualifiesItsRow()
    {
        TableGridDetector.Grid grid = Detect(
            new[] { 700f, 700f, 670f },
            new[] { 12f, 12f, 12f },
            new[] { new[] { 72f, 240f }, null, new[] { 72f, 240f } });

        // Lines 0 and 1 share a baseline, so they are one ROW — and it is the row that is
        // disqualified, not the line that reported nothing. One usable row is left, which is
        // fewer than a table needs.
        Assert.Equal(0, grid.TableCount);
        Assert.Equal(new[] { -1, -1, -1 }, grid.TableOfLine);
    }

    /// <summary>A non-finite column edge is not a column.</summary>
    /// <remarks>
    /// <b>Every comparison against NaN is false in both directions, so the guard cannot be left to
    /// the arithmetic.</b> Two rows of infinities "align" — <c>|∞ − ∞|</c> is NaN and
    /// <c>NaN &gt; tolerance</c> is false — and two rows of NaN do too, so a
    /// <c>1000…0 0 0 1 0 0 cm</c> that overflows only the horizontal axis would invent a table out
    /// of geometry that has no positions at all. The vertical axis is guarded separately, which is
    /// why this needs its own case: a fixture that overflows both never gets this far.
    /// </remarks>
    [Theory]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(float.NaN)]
    public void ANonFiniteColumnEdgeIsRefused(float bad)
    {
        TableGridDetector.Grid grid = Detect(
            new[] { 700f, 670f },
            new[] { 12f, 12f },
            new[] { new[] { bad, bad }, new[] { bad, bad } });

        Assert.Equal(0, grid.TableCount);
        Assert.All(grid.TableOfLine, t => Assert.Equal(-1, t));
    }

    /// <summary>A non-finite baseline is on no row.</summary>
    /// <remarks>
    /// The counterpart of the edge guard, on the axis that groups rather than the one that
    /// separates. A NaN baseline answered "same row" as whatever was open, which folded the rest of
    /// the page into it.
    /// </remarks>
    [Fact]
    public void ANonFiniteBaselineIsOnNoRow()
    {
        TableGridDetector.Grid grid = Detect(
            new[] { float.NaN, float.NaN },
            new[] { 12f, 12f },
            new[] { new[] { 72f, 240f }, new[] { 72f, 240f } });

        Assert.Equal(0, grid.TableCount);
    }

    /// <summary>Two cells at the same X make a row the grid cannot read.</summary>
    /// <remarks>
    /// Two shows one tolerance apart are two cells nothing can tell apart. Merging them would give
    /// this row one fewer column than its neighbours, which is the ragged table UA12 reports as an
    /// Error; refusing the row is the only answer that is neither a merge nor a raggedness.
    /// </remarks>
    [Fact]
    public void TwoCellsAtTheSameEdgeRefuseTheRow()
    {
        TableGridDetector.Grid grid = Detect(
            new[] { 700f, 670f },
            new[] { 12f, 12f },
            new[] { new[] { 72f, 72.5f }, new[] { 72f, 72.5f } });

        Assert.Equal(0, grid.TableCount);
    }

    /// <summary>A row assembled from several lines on one baseline is one row.</summary>
    /// <remarks>
    /// The shape a word processor emits — a text object per cell — reaches the detector as three
    /// lines sharing a Y. All three carry the same row index, and each knows its own column.
    /// </remarks>
    [Fact]
    public void LinesSharingABaselineFormOneRow()
    {
        TableGridDetector.Grid grid = Detect(
            new[] { 700f, 700f, 670f, 670f },
            new[] { 12f, 12f, 12f, 12f },
            new[] { new[] { 72f }, new[] { 240f }, new[] { 72f }, new[] { 240f } });

        Assert.Equal(1, grid.TableCount);
        Assert.Equal(new[] { 0, 0, 1, 1 }, grid.RowOfLine);
        Assert.Equal(new[] { 2, 2, 2, 2 }, grid.ColumnsOfLine);
        Assert.Equal(new[] { 0 }, grid.ColumnOfShow[0]);
        Assert.Equal(new[] { 1 }, grid.ColumnOfShow[1]);
    }

    /// <summary>The tolerance is a fraction of the type size, not an absolute.</summary>
    /// <remarks>
    /// A producer that places every row with the same operand still lands them a rounding step
    /// apart after the text matrix, and the step that is negligible at 24 pt is not at 6 pt. Both
    /// halves are asserted: the same absolute drift is inside the tolerance for large type and
    /// outside it for small.
    /// </remarks>
    [Fact]
    public void TheColumnToleranceScalesWithTheTypeSize()
    {
        const float Drift = 2f;

        Assert.Equal(1, Detect(
            new[] { 700f, 670f },
            new[] { 24f, 24f },
            new[] { new[] { 72f, 240f }, new[] { 72f + Drift, 240f + Drift } }).TableCount);

        Assert.Equal(0, Detect(
            new[] { 700f, 670f },
            new[] { 6f, 6f },
            new[] { new[] { 72f, 240f }, new[] { 72f + Drift, 240f + Drift } }).TableCount);
    }

    /// <summary>An empty page has no grid and does not throw.</summary>
    [Fact]
    public void AnEmptyPageYieldsNoGrid()
    {
        TableGridDetector.Grid grid = TableGridDetector.Detect(
            new float[0], new float[0], new float[0][], Tolerance);

        Assert.Equal(0, grid.TableCount);
        Assert.True(grid.IsEmpty);
        Assert.Empty(grid.TableOfLine);
    }

    /// <summary>A page of a thousand aligned rows is one table, and a bounded amount of work.</summary>
    /// <remarks>
    /// The alignment comparison is per row against the run's first row, so a long page is linear;
    /// this pins that a large but ordinary input is answered rather than refused. The column
    /// ceiling is the other half — a single line of more shows than any table has is not a row —
    /// and is asserted beside it because the two bounds are easy to confuse.
    /// </remarks>
    [Fact]
    public void AnOversizedPageIsAnsweredWithinItsBounds()
    {
        const int Rows = 1000;

        var ys = new float[Rows];
        var sizes = new float[Rows];
        var xs = new float[Rows][];
        for (int i = 0; i < Rows; i++)
        {
            // Twelve points apart, not one: two baselines exactly the tolerance apart are the
            // same row, and a first draft spaced at 1f collapsed all thousand lines into one row
            // of three thousand columns — which the column ceiling then refused. The fixture was
            // measuring the ceiling while claiming to measure the row scan.
            ys[i] = 700f - (i * 12f);
            sizes[i] = 12f;
            xs[i] = new[] { 72f, 240f, 400f };
        }

        TableGridDetector.Grid grid = Detect(ys, sizes, xs);
        Assert.Equal(1, grid.TableCount);
        Assert.Equal(Rows - 1, grid.RowOfLine[Rows - 1]);

        // A line with more shows than any real table has columns is not a row of one.
        var wide = new float[2][];
        for (int r = 0; r < 2; r++)
        {
            wide[r] = new float[600];
            for (int c = 0; c < wide[r].Length; c++) wide[r][c] = c * 10f;
        }

        Assert.Equal(0, Detect(new[] { 700f, 670f }, new[] { 12f, 12f }, wide).TableCount);
    }

    private static TableGridDetector.Grid Detect(float[] ys, float[] sizes, float[][] showXs)
        => TableGridDetector.Detect(ys, sizes, showXs, Tolerance);
}
