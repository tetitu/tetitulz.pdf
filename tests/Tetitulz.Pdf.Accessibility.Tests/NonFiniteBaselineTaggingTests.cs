// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Accessibility.Internal;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// What <see cref="ContentStreamTagger"/> does when a content stream drives the CTM out of
/// <c>float</c> range, so a glyph's device baseline comes out NaN.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect these pin.</b> The line-break test was <c>Math.Abs(line.Y - y) &gt; tolerance</c>,
/// and every comparison against NaN is false — so a NaN baseline answered "this is the same line"
/// and the show folded into whichever group was already open, inheriting its classification. On the
/// fixture below that means body text is emitted inside the preceding line's
/// <c>/Artifact /Pagination /Footer</c> sequence: no MCID, no structure element, unreachable by
/// assistive technology. The same false-in-every-direction comparison also let a NaN font size past
/// <c>if (size &lt;= 0f)</c>, the fallback written for exactly the "no usable size" case.
/// </para>
/// <para>
/// <b>Why the assertions are shaped like this.</b> Nothing throws and nothing renders wrong, so the
/// only evidence is the run list and the spliced bytes: how many marked-content sequences were
/// emitted, which text landed in which one, and whether an MCID was handed out at all. A test that
/// merely tagged the page and checked the output was non-empty passes with the defect in place.
/// </para>
/// </remarks>
public class NonFiniteBaselineTaggingTests
{
    /// <summary>
    /// 1e39 in the only notation a content-stream number may use — plain digits, since
    /// <c>ContentStreamScanner.ReadNumber</c> accepts no exponent. Narrowed to <c>float</c> by
    /// <c>ContentOperand.TryTrailingNumbers</c> it becomes +Infinity, which is what puts
    /// <c>0 &#215; Infinity</c> into the matrix multiply.
    /// </summary>
    private static readonly string OverflowOperand = "1" + new string('0', 39);

    private static PageTagOptions Options() => new PageTagOptions
    {
        PageHeight     = 792f,
        PageBottom     = 0f,
        MedianFontSize = 12f,
    };

    /// <summary>
    /// A footer line, then a <c>cm</c> that overflows the matrix, then a second line of body text.
    /// </summary>
    private static string Content(string scaleOperand) =>
        "BT /F1 12 Tf 20 40 Td (Footer) Tj "
        + scaleOperand + " 0 0 " + scaleOperand + " 0 0 cm "
        + "0 -14 Td (Ghost) Tj ET\n";

    /// <summary>
    /// The fixture really does reach the overflow: the operand narrows to +Infinity in
    /// <c>float</c>, which is the precondition for every assertion below.
    /// </summary>
    [Fact]
    public void TheOverflowOperandNarrowsToInfinityInFloat()
    {
        double asDouble = double.Parse(OverflowOperand, CultureInfo.InvariantCulture);

        Assert.True(double.IsFinite(asDouble), "the operand must be a finite double, or the scanner would reject it");
        Assert.True(float.IsPositiveInfinity((float)asDouble));
    }

    /// <summary>
    /// The show after the overflow gets its own marked-content sequence and its own MCID, instead
    /// of being swallowed by the footer artifact that precedes it.
    /// </summary>
    [Fact]
    public void AShowWhoseBaselineIsNotFiniteStartsItsOwnRunInsteadOfJoiningTheFooterArtifact()
    {
        byte[] original = Encoding.Latin1.GetBytes(Content(OverflowOperand));

        PageTagResult result = ContentStreamTagger.TagPage(original, Options());

        Assert.Null(result.SkipReason);
        Assert.True(result.Changed);
        Assert.Equal(2, result.Runs.Count);

        Assert.True(result.Runs[0].IsArtifact);
        Assert.Equal("Footer", result.Runs[0].ArtifactSubtype);
        Assert.Equal("Footer", result.Runs[0].Text);
        Assert.Equal(-1, result.Runs[0].Mcid);

        Assert.False(result.Runs[1].IsArtifact);
        Assert.Equal("P", result.Runs[1].Role);
        Assert.Equal("Ghost", result.Runs[1].Text);
        Assert.Equal(0, result.Runs[1].Mcid);
        Assert.Equal(1, result.NextMcid);

        // …and the bytes say the same thing: (Ghost) sits between the /P BDC and its EMC.
        string spliced = Encoding.Latin1.GetString(result.NewContent);
        int bdc   = spliced.IndexOf("/P <</MCID 0>> BDC", StringComparison.Ordinal);
        int ghost = spliced.IndexOf("(Ghost)", StringComparison.Ordinal);
        int emc   = spliced.IndexOf("EMC", ghost, StringComparison.Ordinal);
        Assert.InRange(bdc, 0, ghost);
        Assert.InRange(ghost, bdc, emc);
    }

    /// <summary>
    /// The control that gives the case above its meaning: with an identity <c>cm</c> in the same
    /// place, both baselines are finite and 14 pt apart, so the two shows are already two lines and
    /// two runs. Without this, "two runs" could be a property of the fixture rather than of the
    /// non-finite baseline.
    /// </summary>
    [Fact]
    public void TheSameStreamWithAFiniteMatrixAlsoProducesTwoRuns()
    {
        byte[] original = Encoding.Latin1.GetBytes(Content("1"));

        PageTagResult result = ContentStreamTagger.TagPage(original, Options());

        Assert.Null(result.SkipReason);
        Assert.Equal(2, result.Runs.Count);
        Assert.Equal("Footer", result.Runs[0].Text);
        Assert.Equal("Ghost", result.Runs[1].Text);

        // Both land in the 50 pt footer band (y = 40 and y = 26), so both are artifacts here —
        // which is precisely why the overflow case's second run being a /P with an MCID is
        // evidence about the baseline and not about the band.
        Assert.True(result.Runs[0].IsArtifact);
        Assert.True(result.Runs[1].IsArtifact);
    }

    /// <summary>
    /// A run whose effective font size worked out to NaN reports the document median instead —
    /// the fallback the <c>size &lt;= 0f</c> test was written for and that NaN slipped past.
    /// </summary>
    [Fact]
    public void ARunWhoseEffectiveFontSizeIsNotFiniteFallsBackToTheMedian()
    {
        byte[] original = Encoding.Latin1.GetBytes(Content(OverflowOperand));

        PageTagResult result = ContentStreamTagger.TagPage(original, Options());

        Assert.Equal(2, result.Runs.Count);
        Assert.Equal(12f, result.Runs[0].FontSize);   // finite CTM: 12 pt Tf, unscaled
        Assert.Equal(12f, result.Runs[1].FontSize);   // overflowed CTM: the median, not NaN
    }
}
