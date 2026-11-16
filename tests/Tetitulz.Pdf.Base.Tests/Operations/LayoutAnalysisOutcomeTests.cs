// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// What <see cref="PdfLayoutAnalyzer.DetectColumns"/> answers, and — for the answers that are not
/// answers — whether it says so.
/// </summary>
/// <remarks>
/// <para><b>Three different outcomes return one whole-page zone, and a caller cannot tell them
/// apart from the zone list alone.</b> "This page has one column" is the honest result; "the text
/// occupies no area" and "the rivers covered the page" are give-ups. That distinction is the entire
/// purpose of <see cref="PdfLayoutAnalyzer.LastAnalysisWarning"/>, and the slot spent a long time
/// unable to make it: the ceiling it originally reported was removed when the coverage grid became a
/// difference array, leaving the reset as the field's only assignment. The property could never be
/// non-null, and the three tests asserting it <i>was</i> null passed with the feature deleted.</para>
///
/// <para><b>So these tests come in pairs on purpose.</b> Each give-up asserts a warning is present;
/// each real answer asserts it is absent. Neither half is worth anything without the other — a slot
/// that always reports and a slot that never reports each satisfy one of them.</para>
/// </remarks>
public class LayoutAnalysisOutcomeTests
{
    // ==================================================================
    //  The give-ups
    // ==================================================================

    /// <summary>
    /// <b>Too few characters is not "no columns" — it is "not analysed".</b> The empty zone list it
    /// returns is the same value a page genuinely without columns would produce if the analysis had
    /// run and found none.
    /// </summary>
    [Fact]
    public void APageWithTooFewCharactersReportsThatItWasNotAnalysed()
    {
        var layout = Layout(
            new PdfTextChar("a", 0f, 0f, 10f, 10f),
            new PdfTextChar("b", 20f, 0f, 10f, 10f),
            new PdfTextChar("c", 40f, 0f, 10f, 10f));

        var cols = PdfLayoutAnalyzer.DetectColumns(layout);

        Assert.Empty(cols);
        Assert.NotNull(PdfLayoutAnalyzer.LastAnalysisWarning);
        Assert.Contains("too few", PdfLayoutAnalyzer.LastAnalysisWarning, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Text occupying no area yields a degenerate zone, not a column.</b> Every character sharing
    /// one point gives a page of zero width, and the single zone returned describes that extent.
    /// </summary>
    [Fact]
    public void TextOccupyingNoAreaReportsThatNoRiverCanExist()
    {
        var chars = Enumerable.Range(0, 6)
            .Select(_ => new PdfTextChar("x", 100f, 100f, 0f, 0f))
            .ToArray();

        var cols = PdfLayoutAnalyzer.DetectColumns(Layout(chars));

        Assert.Single(cols);
        Assert.NotNull(PdfLayoutAnalyzer.LastAnalysisWarning);
        Assert.Contains("no area", PdfLayoutAnalyzer.LastAnalysisWarning, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Rivers that consume the page leave no column, and that is a give-up wearing the shape of
    /// an answer.</b>
    /// </summary>
    /// <remarks>
    /// A few small glyphs spread over a tall page make every x-bucket more than half whitespace, so
    /// every bucket is a river by the default <c>minHeightFraction</c>, the single river spans the
    /// page, and no column is left between rivers. The caller receives one whole-page zone that is
    /// indistinguishable from a single-column page.
    /// </remarks>
    [Fact]
    public void RiversCoveringTheWholePageAreReportedRatherThanPassedOffAsOneColumn()
    {
        var cols = PdfLayoutAnalyzer.DetectColumns(Layout(
            new PdfTextChar("a", 0f, 0f, 1f, 1f),
            new PdfTextChar("b", 100f, 0f, 1f, 1f),
            new PdfTextChar("c", 200f, 0f, 1f, 1f),
            new PdfTextChar("d", 300f, 1000f, 1f, 1f)));

        Assert.Single(cols);
        Assert.NotNull(PdfLayoutAnalyzer.LastAnalysisWarning);
        Assert.Contains("cover the page", PdfLayoutAnalyzer.LastAnalysisWarning,
            StringComparison.Ordinal);
    }

    // ==================================================================
    //  The real answers — which must stay silent
    // ==================================================================

    /// <summary>
    /// <b>One column is an answer, not a give-up, and must not report.</b> A diagnostic that fires
    /// on the commonest conforming input is one a caller learns to ignore.
    /// </summary>
    [Fact]
    public void AnOrdinarySingleColumnPageReportsNothing()
    {
        var chars = new List<PdfTextChar>();
        for (int row = 0; row < 40; row++)
            for (int i = 0; i < 40; i++)
                chars.Add(new PdfTextChar("x", 60f + i * 6f, 700f - row * 15f, 6f, 10f));

        var cols = PdfLayoutAnalyzer.DetectColumns(Layout(chars.ToArray()));

        Assert.Single(cols);
        Assert.Null(PdfLayoutAnalyzer.LastAnalysisWarning);
    }

    /// <summary>A two-column page is analysed, reports nothing, and finds both columns.</summary>
    [Fact]
    public void AnOrdinaryTwoColumnPageReportsNothing()
    {
        var chars = new List<PdfTextChar>();
        for (int row = 0; row < 40; row++)
        {
            float y = 700f - row * 15f;
            for (int i = 0; i < 20; i++) chars.Add(new PdfTextChar("x", 60f + i * 6f, y, 6f, 10f));
            for (int i = 0; i < 20; i++) chars.Add(new PdfTextChar("x", 330f + i * 6f, y, 6f, 10f));
        }

        var cols = PdfLayoutAnalyzer.DetectColumns(Layout(chars.ToArray()));

        Assert.Equal(2, cols.Count);
        Assert.Null(PdfLayoutAnalyzer.LastAnalysisWarning);
    }

    /// <summary>
    /// <b>The slot is cleared by the next call.</b> Without the reset, the give-up above is reported
    /// against every later page that analysed cleanly.
    /// </summary>
    [Fact]
    public void TheWarningIsClearedByTheNextCall()
    {
        PdfLayoutAnalyzer.DetectColumns(Layout(new PdfTextChar("a", 0f, 0f, 10f, 10f)));
        Assert.NotNull(PdfLayoutAnalyzer.LastAnalysisWarning);

        var chars = new List<PdfTextChar>();
        for (int row = 0; row < 40; row++)
            for (int i = 0; i < 40; i++)
                chars.Add(new PdfTextChar("x", 60f + i * 6f, 700f - row * 15f, 6f, 10f));
        PdfLayoutAnalyzer.DetectColumns(Layout(chars.ToArray()));

        Assert.Null(PdfLayoutAnalyzer.LastAnalysisWarning);
    }

    // ==================================================================
    //  Zone geometry
    // ==================================================================

    /// <summary>
    /// <b>The column after the last river is not dropped, and the zones come out left to right.</b>
    /// </summary>
    /// <remarks>
    /// <b>Three columns, not two.</b> The zones are built by walking the rivers in order and taking
    /// the space before each one, then the space after the last — so with a single river the walk
    /// has nothing to order and the sort beneath it is free to be wrong. Two rivers are the smallest
    /// fixture in which the ordering is observable.
    /// </remarks>
    [Fact]
    public void TheColumnsComeOutLeftToRightWithTheTrailingOneIncluded()
    {
        var chars = new List<PdfTextChar>();
        for (int row = 0; row < 40; row++)
        {
            float y = 700f - row * 15f;
            for (int i = 0; i < 15; i++) chars.Add(new PdfTextChar("x", 60f + i * 6f, y, 6f, 10f));
            for (int i = 0; i < 15; i++) chars.Add(new PdfTextChar("x", 250f + i * 6f, y, 6f, 10f));
            for (int i = 0; i < 15; i++) chars.Add(new PdfTextChar("x", 440f + i * 6f, y, 6f, 10f));
        }

        var cols = PdfLayoutAnalyzer.DetectColumns(Layout(chars.ToArray()));

        Assert.Equal(3, cols.Count);
        // Strictly increasing and disjoint: each zone starts at or after the previous one ends.
        for (int i = 1; i < cols.Count; i++)
        {
            Assert.True(cols[i - 1].X < cols[i].X, "the zones are not ordered left to right");
            Assert.True(cols[i - 1].X + cols[i - 1].Width <= cols[i].X, "the zones overlap");
        }
    }

    /// <summary>
    /// <b>A page narrower than a single point is one column, not a give-up.</b>
    /// </summary>
    /// <remarks>
    /// The "no rivers" early return looks redundant — the trailing-column step below it builds the
    /// same whole-page zone — and for an ordinary page it is. It stops being redundant when the page
    /// is narrower than the one-point tolerance that step applies: the trailing column is then not
    /// added at all, no zone is built, and the analysis reports that its rivers covered the page
    /// when it found none. The zone list is identical either way; only the diagnostic differs.
    /// </remarks>
    [Fact]
    public void APageNarrowerThanAPointIsOneColumnRatherThanAReportedGiveUp()
    {
        // Half a point wide and a hundred tall, with ink over the full height, so no bucket is
        // mostly whitespace and no river is found.
        var chars = Enumerable.Range(0, 4)
            .Select(_ => new PdfTextChar("x", 0f, 0f, 0.5f, 100f))
            .ToArray();

        var cols = PdfLayoutAnalyzer.DetectColumns(Layout(chars));

        Assert.Single(cols);
        Assert.Null(PdfLayoutAnalyzer.LastAnalysisWarning);
    }

    /// <summary>
    /// <b>A gap narrower than <c>minRiverWidth</c> is not a river.</b> Ordinary inter-word spacing
    /// would otherwise split every line into columns.
    /// </summary>
    [Fact]
    public void AGapNarrowerThanTheMinimumRiverWidthDoesNotSplitThePage()
    {
        var chars = new List<PdfTextChar>();
        for (int row = 0; row < 40; row++)
        {
            float y = 700f - row * 15f;
            for (int i = 0; i < 20; i++) chars.Add(new PdfTextChar("x", 60f + i * 6f, y, 6f, 10f));
            // A 4 pt gap, well under the 10 pt default.
            for (int i = 0; i < 20; i++) chars.Add(new PdfTextChar("x", 184f + i * 6f, y, 6f, 10f));
        }

        var layout = Layout(chars.ToArray());

        Assert.Single(PdfLayoutAnalyzer.DetectColumns(layout, minRiverWidth: 10f));
        // The control: the same gap IS a river once the threshold is below it, so the fixture
        // really does contain a gap and the test above pins the threshold rather than the page.
        Assert.Equal(2, PdfLayoutAnalyzer.DetectColumns(layout, minRiverWidth: 2f).Count);
    }

    /// <summary>
    /// <b><c>minHeightFraction</c> decides what counts as a river.</b> A gap interrupted for part of
    /// the page's height — a table straddling two columns — is a river under a lenient fraction and
    /// not under a strict one.
    /// </summary>
    [Fact]
    public void TheHeightFractionDecidesWhetherAnInterruptedGapIsARiver()
    {
        var chars = new List<PdfTextChar>();
        for (int row = 0; row < 40; row++)
        {
            float y = 700f - row * 15f;
            for (int i = 0; i < 20; i++) chars.Add(new PdfTextChar("x", 60f + i * 6f, y, 6f, 10f));
            for (int i = 0; i < 20; i++) chars.Add(new PdfTextChar("x", 330f + i * 6f, y, 6f, 10f));
            // The top third of the page spans the gap.
            if (row < 14)
                for (int i = 0; i < 25; i++) chars.Add(new PdfTextChar("x", 180f + i * 6f, y, 6f, 10f));
        }

        var layout = Layout(chars.ToArray());

        // The gap is clear for roughly two thirds of the height.
        Assert.Equal(2, PdfLayoutAnalyzer.DetectColumns(layout, minHeightFraction: 0.5f).Count);
        Assert.Single(PdfLayoutAnalyzer.DetectColumns(layout, minHeightFraction: 0.95f));
    }

    // ==================================================================
    //  Hostile coordinates — the two guards around the coverage grid
    // ==================================================================

    /// <summary>
    /// <b>A glyph at an unbounded x coordinate is bucketed, not allocated for.</b> The grid's width
    /// is clamped because the page extent is attacker-controlled: one glyph at 1e9 makes the
    /// unclamped bucket count a billion, and the flat coverage array is
    /// <c>(xBuckets + 1) * (yBands + 1)</c> ints — which overflows <see cref="int"/> and is rejected
    /// as a negative length before it can even fail as an allocation.
    /// </summary>
    /// <remarks>
    /// <b>The existing DoS fixture builds this through the extractor and does not reach the
    /// guard</b> — mutating the clamp away leaves it green, so the glyph it places at 1e9 never
    /// arrives in <c>Chars</c>. Constructing the layout directly is what puts the coordinate where
    /// the clamp can see it. The assertion is the guard's own evidence — a bounded answer — not a
    /// clock.
    /// </remarks>
    [Fact]
    public void AGlyphAtAnUnboundedCoordinateIsBucketedRatherThanAllocatedFor()
    {
        var cols = PdfLayoutAnalyzer.DetectColumns(Layout(
            new PdfTextChar("a", 0f, 0f, 10f, 10f),
            new PdfTextChar("b", 20f, 0f, 10f, 10f),
            new PdfTextChar("c", 40f, 0f, 10f, 10f),
            new PdfTextChar("d", 1e9f, 0f, 10f, 10f)));

        // At most one zone per bucket, and the bucket count is what the clamp bounds.
        Assert.InRange(cols.Count, 1, 5000);
    }

    /// <summary>
    /// <b>A glyph whose extent runs backwards is skipped, not accumulated in reverse.</b>
    /// </summary>
    /// <remarks>
    /// <para>A negative advance width — a corrupt charstring, or a <c>Tz</c> that inverts the text
    /// matrix — makes a glyph's end bucket precede its start bucket. The coverage grid is a
    /// difference array, so a rectangle entered with its corners transposed does not cover less: it
    /// adds where it should subtract, and the prefix sums that follow carry the error across the
    /// whole page, turning covered buckets into rivers and rivers into columns.</para>
    ///
    /// <para><b>Two earlier fixtures did not reach this, for two different reasons.</b> A zero-width
    /// glyph at the page's maximum x is the exact boundary case, and whether it inverts comes down
    /// to whether <c>pageWidth / (pageWidth / xBuckets)</c> lands just above or just below
    /// <c>xBuckets</c> in <see cref="float"/> — it landed below, so the guard was never entered. A
    /// short backwards glyph <i>does</i> invert the pair, but the negative accumulation then landed
    /// inside the river, on buckets that were already empty, so the answer did not move.</para>
    ///
    /// <para><b>The corruption has to land on ink that is covered exactly once.</b> A page of
    /// ordinary 6 pt glyphs does not work: neighbouring glyphs share a bucket at each boundary, so
    /// those buckets hold a coverage of two, survive the subtraction with one left, and break the
    /// spurious river into runs shorter than <c>minRiverWidth</c>. This fixture uses four large
    /// blocks instead, so every bucket of each column is covered exactly once and the subtraction
    /// empties it — the right column then reads as part river, and its zone comes back a third of
    /// its true width.</para>
    /// </remarks>
    [Fact]
    public void AGlyphWithAMalformedBackwardsExtentIsSkippedRatherThanAccumulatedInReverse()
    {
        // Two columns, each two blocks stacked, so coverage is uniform and gap-free.
        var clean = new List<PdfTextChar>
        {
            new PdfTextChar("a", 0f, 0f, 50f, 50f),
            new PdfTextChar("b", 0f, 50f, 50f, 50f),
            new PdfTextChar("c", 200f, 0f, 50f, 50f),
            new PdfTextChar("d", 200f, 50f, 50f, 50f),
        };

        var expected = PdfLayoutAnalyzer.DetectColumns(Layout(clean.ToArray()));
        Assert.Equal(2, expected.Count);   // the control really is a two-column page

        // Backwards across the right column, full height, and inside the page's existing extent —
        // so the grid it is measured against is the same one the control produced.
        var withBadGlyph = new List<PdfTextChar>(clean)
        {
            new PdfTextChar("x", 250f, 0f, -30f, 100f),
        };
        var actual = PdfLayoutAnalyzer.DetectColumns(Layout(withBadGlyph.ToArray()));

        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].X, actual[i].X, 3);
            Assert.Equal(expected[i].Width, actual[i].Width, 3);
        }
    }

    // ==================================================================
    //  Construction
    // ==================================================================

    /// <summary>
    /// A layout carrying characters and no lines. <see cref="PdfLayoutAnalyzer"/> reads
    /// <c>Chars</c> only, and building the fixture directly keeps the extractor's own rounding and
    /// word assembly out of an assertion about coordinates.
    /// </summary>
    private static PdfPageTextLayout Layout(params PdfTextChar[] chars)
        => new PdfPageTextLayout(0, Array.Empty<PdfTextLine>(), chars);
}
