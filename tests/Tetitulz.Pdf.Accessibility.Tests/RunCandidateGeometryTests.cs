// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Accessibility.Internal;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// A line handed to the classifier carries where it starts horizontally, not only how far down the
/// page it is.
/// </summary>
/// <remarks>
/// <para><b>It carried no X at all.</b> <c>RecordShow</c> built the full text-to-device matrix and
/// read only its <c>F</c> component; <c>E</c> — the horizontal translation — was one field away
/// and no classifier could see it. That was defensible while the vocabulary was headings and
/// paragraphs, which a font size decides, and it is what made every richer inference impossible:
/// an indented list item, a centred heading and a table cell are distinguished by where a line
/// starts and by nothing else a line-level tagger can observe.</para>
///
/// <para><b>The tests drive the real pipeline.</b> A classifier is injected purely to capture the
/// candidates the tagger builds, so the geometry asserted is the geometry a caller's classifier
/// would be handed — not a value computed a second way by the test.</para>
/// </remarks>
public sealed class RunCandidateGeometryTests
{
    /// <summary>A line's X is where its text is placed.</summary>
    [Fact]
    public void ALinesXIsWhereItsTextIsPlaced()
    {
        IReadOnlyList<RunCandidate> lines = Candidates(
            "BT /F1 12 Tf 72 700 Td (left) Tj ET\n"
            + "BT /F1 12 Tf 144 680 Td (indented) Tj ET\n");

        Assert.Equal(2, lines.Count);
        Assert.Equal(72f, lines[0].X, 3);
        Assert.Equal(144f, lines[1].X, 3);
    }

    /// <summary>The CTM moves it, because the classifier reads page coordinates.</summary>
    /// <remarks>
    /// <b>Reading <c>Td</c>'s operand instead would be the wrong number.</b> A producer that
    /// translates the whole page with a <c>cm</c> and then places every line at <c>0</c> is
    /// ordinary output, and a classifier comparing the raw operand against a margin would see every
    /// line flush left. What a layout heuristic needs is where the ink lands.
    /// </remarks>
    [Fact]
    public void TheCtmIsAppliedSoTheValueIsAPageCoordinate()
    {
        IReadOnlyList<RunCandidate> lines = Candidates(
            "q 1 0 0 1 100 0 cm BT /F1 12 Tf 20 700 Td (shifted) Tj ET Q\n");

        Assert.Single(lines);
        Assert.Equal(120f, lines[0].X, 3);
    }

    /// <summary>A horizontal scale is applied too, and it is not the vertical one.</summary>
    /// <remarks>
    /// The two scales differ under any non-uniform <c>cm</c>, which is exactly when a heuristic
    /// most needs the right one: an indent of 40 points under <c>0.5 0 0 1 0 0 cm</c> is 20 points
    /// on the page, and a classifier told 40 sees an indentation that is not there.
    /// </remarks>
    [Fact]
    public void ANonUniformScaleUsesTheHorizontalFactor()
    {
        IReadOnlyList<RunCandidate> lines = Candidates(
            "q 0.5 0 0 1 0 0 cm BT /F1 12 Tf 80 700 Td (squeezed) Tj ET Q\n");

        Assert.Single(lines);
        Assert.Equal(40f, lines[0].X, 3);

        // The vertical placement is untouched by the horizontal factor: reading one scale for both
        // axes is the mistake this discriminates.
        Assert.Equal(700f, lines[0].Y, 3);
    }

    /// <summary>A line's X is its leftmost show, not its first.</summary>
    /// <remarks>
    /// <b>A line is assembled from shows in content order, which is not left-to-right order.</b> A
    /// producer may place a hanging marker after the text it belongs to, or lay a right-to-left
    /// line down in logical order. The left edge is where the ink starts, whichever show put it
    /// there — and taking the first show's X instead would report the indent of a list item as the
    /// position of its body.
    /// </remarks>
    [Theory]
    [InlineData(200f, 100f)]   // the leftmost show comes last
    [InlineData(100f, 200f)]   // the leftmost show comes first
    public void ALinesXIsItsLeftmostShowWhicheverOrderTheyAreWrittenIn(float first, float second)
    {
        IReadOnlyList<RunCandidate> lines = Candidates(
            $"BT /F1 12 Tf 1 0 0 1 {F(first)} 700 Tm (a) Tj 1 0 0 1 {F(second)} 700 Tm (b) Tj ET\n");

        Assert.Single(lines);
        Assert.Equal(100f, lines[0].X, 3);
    }

    /// <summary>Text rise displaces Y and leaves X alone.</summary>
    /// <remarks>
    /// Rise is a vertical displacement in text space, so under an unrotated matrix it contributes
    /// to Y and not to X. Applying it to both — the obvious way to write the second line by copying
    /// the first — would move every superscript sideways.
    /// </remarks>
    [Fact]
    public void TextRiseMovesTheBaselineAndNotTheLeftEdge()
    {
        IReadOnlyList<RunCandidate> lines = Candidates(
            "BT /F1 12 Tf 72 700 Td 5 Ts (raised) Tj ET\n");

        Assert.Single(lines);
        Assert.Equal(72f, lines[0].X, 3);
        Assert.Equal(705f, lines[0].Y, 3);
    }

    /// <summary>An oversized matrix yields no X rather than a nonsense one.</summary>
    /// <remarks>
    /// <para><b>A non-finite X is worse than an absent one, and silently so.</b> Every comparison
    /// against NaN is false in both directions, so a heuristic written <c>if (x &gt; margin)</c>
    /// and one written <c>if (x &lt;= margin)</c> take opposite branches on the same line —
    /// a classifier cannot be written correctly against a value that does that unnoticed.
    /// <c>RunCandidate.X</c> therefore documents NaN as its "unknown", which is the same contract
    /// the baseline already has.</para>
    ///
    /// <para>Named with <c>Overflow</c> so the unfalsifiable-assertion guard reads the shape
    /// correctly: the input is hostile and not producing a wrong number is the behaviour.</para>
    /// </remarks>
    [Fact]
    public void AnOverflowingMatrixLeavesTheLeftEdgeUnknown()
    {
        IReadOnlyList<RunCandidate> lines = Candidates(
            "q 1e39 0 0 1e39 0 0 cm BT /F1 12 Tf 72 700 Td (huge) Tj ET Q\n");

        foreach (RunCandidate line in lines)
            Assert.False(IsFinite(line.X) && Math.Abs(line.X) > 1e30f,
                $"a non-finite matrix produced X = {line.X}, which a heuristic cannot compare");
    }

    /// <summary>Under a rotation the rise moves the left edge, and through the right term.</summary>
    /// <remarks>
    /// <b>Every unrotated fixture is blind to this.</b> Rise reaches X through the matrix's
    /// <c>B</c> component, which is zero unless the text is rotated or skewed — so dropping
    /// the term entirely changes nothing on an ordinary page, and a suite of ordinary pages cannot
    /// tell a correct implementation from one that ignores rise on this axis. A 90° rotation
    /// turns the vertical displacement into a horizontal one, where it is the whole difference.
    /// </remarks>
    [Fact]
    public void UnderARotationTheRiseMovesTheLeftEdge()
    {
        // 0 1 -1 0 : a 90-degree rotation, so text-space +Y becomes page +X.
        IReadOnlyList<RunCandidate> raised = Candidates(
            "q 0 1 -1 0 400 100 cm BT /F1 12 Tf 0 0 Td 10 Ts (raised) Tj ET Q\n");
        IReadOnlyList<RunCandidate> flat = Candidates(
            "q 0 1 -1 0 400 100 cm BT /F1 12 Tf 0 0 Td (flat) Tj ET Q\n");

        Assert.Single(raised);
        Assert.Single(flat);

        // The rise displaces the run along the page's X axis under this matrix, by exactly its
        // own magnitude — so a version that dropped the term reports the same X for both.
        Assert.Equal(flat[0].X + 10f, raised[0].X, 3);
    }

    /// <summary>A page with no text yields no candidate, so nothing invents geometry.</summary>
    /// <remarks>
    /// The acceptance control for the whole file: every assertion above is about the value of X on
    /// a line, and all of them would hold vacuously if the tagger stopped producing lines.
    /// </remarks>
    [Fact]
    public void APageWithNoTextYieldsNoCandidate()
        => Assert.Empty(Candidates("0 0 1 rg 10 10 20 20 re f\n"));

    // ==================================================================== helpers

    /// <summary>A float as a PDF number — no exponent, because §7.3.3's grammar has none.</summary>
    /// <remarks>
    /// <b>Worth stating, because it defeated a fixture.</b> A test written with <c>1e39</c> in a
    /// <c>Tm</c> looked like it was overflowing the matrix and was in fact writing three tokens the
    /// operator could not use, so the <c>Tm</c> was ignored and the show kept the previous
    /// position. The overflow fixture that does work puts the value in a <c>cm</c>, where a plain
    /// decimal string reaches the parser.
    /// </remarks>
    private static string F(float value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool IsFinite(float value)
        => !float.IsNaN(value) && !float.IsInfinity(value);

    /// <summary>
    /// The candidates the tagger hands its classifier for <paramref name="content"/>.
    /// </summary>
    /// <remarks>
    /// The classifier both captures and classifies: returning <c>Unmarked</c> would make the tagger
    /// refuse the page before it finished, so each line is classified as a paragraph and the
    /// captured candidates are the real ones.
    /// </remarks>
    private static IReadOnlyList<RunCandidate> Candidates(string content)
    {
        var captured = new List<RunCandidate>();

        var options = new PageTagOptions
        {
            PageHeight = 792f,
            Classifier = candidate =>
            {
                captured.Add(candidate);
                return RunClassification.Content(StructureTypes.Paragraph);
            },
        };

        ContentStreamTagger.TagPage(Encoding.ASCII.GetBytes(content), options);
        return captured;
    }
}
