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
/// The geometry <see cref="PdfSpatialQuery"/> answers with: which words a rectangle contains, and
/// which word lies on a given side of another.
/// </summary>
/// <remarks>
/// <para><b>Every layout here is constructed, not parsed.</b> These are assertions about
/// coordinates, and a fixture that reaches them through text extraction carries the extractor's own
/// rounding, word assembly and baseline grouping into the answer — so a failure would not say which
/// layer produced it.</para>
///
/// <para><b>The separating fixture is one that is outside on exactly ONE axis.</b> The existing
/// region test excludes a word that sits outside the rectangle horizontally <i>and</i> vertically,
/// so each half of the intersection predicate is individually sufficient and either may be deleted
/// with the suite green. That is the shape CLAUDE.md names: a fixture violating a rule twice over
/// proves the rule fires, never which clause fired it.</para>
/// </remarks>
public class SpatialQueryGeometryTests
{
    // ==================================================================
    //  Region queries — both axes, and the edges
    // ==================================================================

    /// <summary>
    /// A word inside the rectangle's <b>x</b> range but above it is excluded, which is the only
    /// input that tests the predicate's y half.
    /// </summary>
    [Fact]
    public void AWordSharingTheRectanglesColumnButNotItsRowIsExcluded()
    {
        var page = Page(
            Word("inside", 10f, 10f),
            Word("above", 10f, 500f));   // same x, far above

        var hits = PdfSpatialQuery.WordsInRect(page, 0f, 0f, 100f, 100f);

        Assert.Equal(new[] { "inside" }, hits.Select(w => w.Text).ToArray());
    }

    /// <summary>
    /// A word inside the rectangle's <b>y</b> range but to the right of it is excluded — the
    /// counterpart, and the only input that tests the x half.
    /// </summary>
    [Fact]
    public void AWordSharingTheRectanglesRowButNotItsColumnIsExcluded()
    {
        var page = Page(
            Word("inside", 10f, 10f),
            Word("right", 500f, 10f));   // same y, far right

        var hits = PdfSpatialQuery.WordsInRect(page, 0f, 0f, 100f, 100f);

        Assert.Equal(new[] { "inside" }, hits.Select(w => w.Text).ToArray());
    }

    /// <summary>
    /// <b>Touching is not intersecting.</b> A word whose right edge lands exactly on the rectangle's
    /// left edge shares no area with it, and the predicate is strict for that reason.
    /// </summary>
    /// <remarks>
    /// Asserted with an overlapping neighbour in the same layout, so the test cannot pass on a
    /// predicate that has stopped matching anything at all.
    /// </remarks>
    [Fact]
    public void AWordWhoseEdgeExactlyMeetsTheRectangleIsNotInsideIt()
    {
        var page = Page(
            Word("touching", 80f, 10f, width: 20f),  // spans 80..100; the rect starts at 100
            Word("overlapping", 95f, 40f, width: 20f));

        var hits = PdfSpatialQuery.WordsInRect(page, 100f, 0f, 100f, 100f);

        Assert.Equal(new[] { "overlapping" }, hits.Select(w => w.Text).ToArray());
    }

    /// <summary>Lines are filtered by the same predicate as words.</summary>
    [Fact]
    public void LinesInRectAppliesBothAxesToo()
    {
        var page = Page(
            Word("inside", 10f, 10f),
            Word("above", 10f, 500f),
            Word("right", 500f, 10f));

        var lines = PdfSpatialQuery.LinesInRect(page, 0f, 0f, 100f, 100f);

        Assert.Equal(new[] { "inside" }, lines.Select(l => l.Text).ToArray());
    }

    /// <summary>
    /// <b>A negative extent is refused by name, as <c>Crop</c> refuses it.</b>
    /// </summary>
    /// <remarks>
    /// The two share <c>RectIntersects</c> and the contract around it. A negative width does not
    /// simply return nothing: it inverts the interval, and
    /// <c>bx + bw &gt; x &amp;&amp; bx &lt; x + w</c> still holds for a word that <i>spans</i> the
    /// gap — so the caller would be handed words from a region it never described.
    /// </remarks>
    [Theory]
    [InlineData(-10f, 100f, "width")]
    [InlineData(100f, -10f, "height")]
    public void ARectangleWithANegativeExtentIsRefusedByName(float w, float h, string expected)
    {
        var page = Page(Word("spanning", 90f, 90f, width: 30f, height: 30f));

        var words = Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfSpatialQuery.WordsInRect(page, 100f, 100f, w, h));
        var lines = Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfSpatialQuery.LinesInRect(page, 100f, 100f, w, h));

        Assert.Equal(expected, words.ParamName);
        Assert.Equal(expected, lines.ParamName);
    }

    /// <summary>
    /// The acceptance control for the refusal above: the same word and the same rectangle, with the
    /// extent positive, really is found — so the test pins the sign rather than the fixture.
    /// </summary>
    [Fact]
    public void TheSameRectangleWithAPositiveExtentStillFindsTheWord()
    {
        var page = Page(Word("spanning", 90f, 90f, width: 30f, height: 30f));

        var hits = PdfSpatialQuery.WordsInRect(page, 100f, 100f, 10f, 10f);

        Assert.Equal("spanning", Assert.Single(hits).Text);
    }

    // ==================================================================
    //  Adjacency — the side, the gap, and which candidate wins
    // ==================================================================

    /// <summary>
    /// <b>The gap to a word on the right is measured from the anchor's right edge</b>, not its
    /// origin — otherwise a wide label's own width counts as distance and a value just past it falls
    /// outside <c>maxGap</c>.
    /// </summary>
    [Fact]
    public void TheGapToTheRightIsMeasuredFromTheAnchorsRightEdge()
    {
        var anchor = Word("Invoice-Number", 0f, 100f, width: 90f);
        var value = Word("42", 95f, 100f, width: 10f);
        var page = Page(anchor, value);

        // 5 pt past the anchor's right edge, but 95 pt past its origin.
        var found = PdfSpatialQuery.WordToTheRightOf(page, anchor, maxGap: 20f);

        Assert.Same(value, found);
    }

    /// <summary>
    /// <b>The gap to a word on the left is measured to that word's right edge.</b> Measuring to its
    /// origin counts the candidate's own width as distance, so a long value is refused where a short
    /// one at the same position is accepted.
    /// </summary>
    [Fact]
    public void TheGapToTheLeftIsMeasuredFromTheCandidatesRightEdge()
    {
        var anchor = Word("Total", 200f, 100f, width: 30f);
        var value = Word("1,234,567.89", 100f, 100f, width: 95f);
        var page = Page(anchor, value);

        // The candidate ends 5 pt before the anchor, but starts 100 pt before it.
        var found = PdfSpatialQuery.WordToTheLeftOf(page, anchor, maxGap: 20f);

        Assert.Same(value, found);
    }

    /// <summary>A word on the wrong side is not a neighbour, in either direction.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AWordOnTheWrongSideOfTheAnchorIsNotReturned(bool askForTheRight)
    {
        var anchor = Word("anchor", 100f, 100f, width: 20f);
        var onTheLeft = Word("left", 40f, 100f, width: 20f);
        var onTheRight = Word("right", 160f, 100f, width: 20f);
        var page = Page(anchor, onTheLeft, onTheRight);

        var found = askForTheRight
            ? PdfSpatialQuery.WordToTheRightOf(page, anchor)
            : PdfSpatialQuery.WordToTheLeftOf(page, anchor);

        Assert.Same(askForTheRight ? onTheRight : onTheLeft, found);
    }

    /// <summary>
    /// <b>A word off the anchor's baseline is not a neighbour</b>, however close it is horizontally.
    /// </summary>
    [Fact]
    public void AWordOffTheAnchorsBaselineIsNotItsNeighbour()
    {
        var anchor = Word("label", 0f, 100f, width: 20f);
        var sameLine = Word("value", 60f, 100f, width: 20f);
        var nextLine = Word("other", 25f, 80f, width: 20f);   // nearer in x, 20 pt below
        var page = Page(anchor, sameLine, nextLine);

        Assert.Same(sameLine, PdfSpatialQuery.WordToTheRightOf(page, anchor, yTol: 4f));
    }

    /// <summary>
    /// <b>The nearest candidate wins — not the first seen, and not the last.</b>
    /// </summary>
    /// <remarks>
    /// Asserted in both orders, because a single ordering only rules out one of the two ways the
    /// comparison can be wrong: a scan that keeps its first hit and one that keeps its last are
    /// each correct on the layout that happens to present the near word in that position.
    /// </remarks>
    [Theory]
    [InlineData(true)]   // the near word arrives first
    [InlineData(false)]  // the near word arrives last
    public void TheNearestCandidateWinsWhicheverOrderTheyArriveIn(bool nearFirst)
    {
        var anchor = Word("anchor", 0f, 100f, width: 10f);
        var far = Word("far", 150f, 100f, width: 10f);
        var near = Word("near", 40f, 100f, width: 10f);
        var page = nearFirst ? Page(anchor, near, far) : Page(anchor, far, near);

        Assert.Same(near, PdfSpatialQuery.WordToTheRightOf(page, anchor));
    }

    /// <summary>
    /// <b>The anchor is never its own neighbour — and the only anchor for which that needs saying
    /// is one of zero width.</b>
    /// </summary>
    /// <remarks>
    /// For an ordinary anchor the identity check is redundant: the gap to itself is minus its own
    /// width, which the "must be on this side" test already rejects. At zero width that gap is
    /// exactly 0, the side test admits it, and the identity check is the only thing left — so a
    /// fixture built from a normal word proves nothing about this line. A word can have no width:
    /// it is what an empty run, or one of glyphs whose advances are all zero, assembles to.
    /// </remarks>
    [Fact]
    public void AZeroWidthAnchorIsNotReturnedAsItsOwnNeighbour()
    {
        var anchor = Word("", 0f, 100f, width: 0f);
        var page = Page(anchor);

        Assert.Null(PdfSpatialQuery.WordToTheRightOf(page, anchor));
        Assert.Null(PdfSpatialQuery.WordToTheLeftOf(page, anchor));
        Assert.NotNull(PdfSpatialQuery.LastError);
    }

    // ==================================================================
    //  WordBelow, and the fallback that uses it
    // ==================================================================

    /// <summary>
    /// <b><c>WordBelow</c> looks down, not up.</b> In PDF user space y grows upward, so "below"
    /// means a smaller y — the direction a caller cannot verify from the method name.
    /// </summary>
    [Fact]
    public void WordBelowLooksDownwardsInPdfUserSpace()
    {
        var anchor = Word("anchor", 100f, 500f, width: 20f);
        var below = Word("below", 100f, 480f, width: 20f);
        var above = Word("above", 100f, 520f, width: 20f);
        var page = Page(anchor, above, below);

        Assert.Same(below, PdfSpatialQuery.WordBelow(page, anchor));
    }

    /// <summary>
    /// A word below the anchor but in another column is not "below" it — the horizontal-overlap
    /// test is what stops a two-column page pairing a label with the next column's value.
    /// </summary>
    [Fact]
    public void AWordBelowButHorizontallyDisjointIsNotReturned()
    {
        var anchor = Word("anchor", 100f, 500f, width: 20f);
        var otherColumn = Word("other", 400f, 480f, width: 20f);
        var page = Page(anchor, otherColumn);

        Assert.Null(PdfSpatialQuery.WordBelow(page, anchor, xTol: 20f));
        Assert.NotNull(PdfSpatialQuery.LastError);
    }

    /// <summary>
    /// <b><c>ValueWordFor</c> falls back to the word below when its own side is empty.</b> A label
    /// above its value is an ordinary form layout, and without the fallback it reports nothing.
    /// </summary>
    [Fact]
    public void ValueWordForFallsBackToTheWordBelowWhenItsSideIsEmpty()
    {
        var label = Word("Amount", 100f, 500f, width: 40f);
        var value = Word("42.00", 100f, 480f, width: 30f);
        var page = Page(label, value);

        Assert.Same(value, PdfSpatialQuery.ValueWordFor(page, label));
    }

    // ==================================================================
    //  NearestWord
    // ==================================================================

    /// <summary>
    /// <b>Nearest is measured from each word's centre</b>, not its origin — so a wide word whose
    /// origin is far away but whose body sits under the point wins.
    /// </summary>
    [Fact]
    public void NearestWordMeasuresFromTheWordsCentre()
    {
        // The point sits at x=100. `wide` spans 60..140 (centre 100); `narrow` starts at 118.
        var wide = Word("wide", 60f, 100f, width: 80f);
        var narrow = Word("narrow", 118f, 100f, width: 4f);
        var page = Page(wide, narrow);

        Assert.Same(wide, PdfSpatialQuery.NearestWord(page, 100f, 105f));
    }

    // ==================================================================
    //  Construction
    // ==================================================================

    private static PdfTextWord Word(string text, float x, float y,
                                    float width = 10f, float height = 10f, bool rtl = false)
        => new PdfTextWord(text, x, y, width, height, Array.Empty<PdfTextChar>(), rtl);

    /// <summary>
    /// One line per word, each line's box equal to its word's, so line-level and word-level queries
    /// see the same geometry.
    /// </summary>
    private static PdfPageTextLayout Page(params PdfTextWord[] words)
    {
        var lines = words
            .Select(w => new PdfTextLine(w.Text, w.X, w.Y, w.Width, w.Height, new[] { w }, w.IsRtl))
            .ToList();
        return new PdfPageTextLayout(0, lines);
    }
}
