// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// <see cref="CssLength"/>: CSS absolute units in points and in CSS pixels, with each form equal,
/// bit for bit, to the expression its caller used before the table was shared.
/// </summary>
public class CssLengthTests
{
    /// <remarks>
    /// 511.139 and 387.14 are values where chained arithmetic rounds differently from a single
    /// multiplication (<c>n * 96 / 72 * 12</c> against <c>n * 16</c>). Without them the pica
    /// mutation survived: every other value here happens to round the same either way.
    /// </remarks>
    public static readonly double[] Values = { 0, 1, 1.5, 2.54, 10, 72, 123.456, -3.25, 1e-3, 511.139, 387.14 };

    /// <summary>
    /// The points form is HtmlToPdf's <c>ParseLength</c>, operation for operation.
    /// </summary>
    [Fact]
    public void ThePointsFormIsTheHtmlMappersArithmetic()
    {
        foreach (double n in Values)
        {
            Assert.Equal(n, Points(n, "pt"));
            Assert.Equal(n * (72.0 / 96.0), Points(n, "px"));
            Assert.Equal(n * 12.0, Points(n, "pc"));
            Assert.Equal(n * 72.0, Points(n, "in"));
            Assert.Equal(n * 72.0 / 2.54, Points(n, "cm"));
            Assert.Equal(n * 72.0 / 25.4, Points(n, "mm"));
            Assert.Equal(n * 72.0 / 101.6, Points(n, "q"));
        }
    }

    /// <summary>
    /// The pixels form is HtmlToPdf's <c>ParseMediaLength</c>, operation for operation. Its pica arm
    /// was <c>n * 16</c>, one rounding, which is why the table holds the pixels per pica as its own
    /// constant rather than chaining <c>n * 96 / 72 * 12</c>.
    /// </summary>
    [Fact]
    public void ThePixelsFormIsTheMediaQueryArithmetic()
    {
        foreach (double n in Values)
        {
            Assert.Equal(n, Pixels(n, "px"));
            Assert.Equal(n * 96 / 72, Pixels(n, "pt"));
            Assert.Equal(n * 16, Pixels(n, "pc"));
            Assert.Equal(n * 96, Pixels(n, "in"));
            Assert.Equal(n * 96 / 2.54, Pixels(n, "cm"));
            Assert.Equal(n * 96 / 25.4, Pixels(n, "mm"));
            Assert.Equal(n * 96 / 101.6, Pixels(n, "q"));
        }
    }

    /// <summary>The two forms describe one length: an inch is 72 points and 96 pixels, whatever its unit.</summary>
    [Theory]
    [InlineData(1, "in")]
    [InlineData(2.54, "cm")]
    [InlineData(25.4, "mm")]
    [InlineData(101.6, "q")]
    [InlineData(6, "pc")]
    [InlineData(72, "pt")]
    [InlineData(96, "px")]
    [InlineData(96, "PX")]
    public void AnInchIsSeventyTwoPointsAndNinetySixPixels(double value, string unit)
    {
        Assert.Equal(72, Points(value, unit), 9);
        Assert.Equal(96, Pixels(value, unit), 9);
    }

    /// <summary>A relative unit, or none, is not an absolute length; the caller resolves it.</summary>
    [Theory]
    [InlineData("em")]
    [InlineData("rem")]
    [InlineData("ex")]
    [InlineData("%")]
    [InlineData("")]
    [InlineData("furlong")]
    [InlineData(null)]
    public void ARelativeOrUnknownUnitIsRefused(string unit)
    {
        Assert.False(CssLength.TryToPoints(1, unit, out _));
        Assert.False(CssLength.TryToPixels(1, unit, out _));
    }

    private static double Points(double n, string unit)
    {
        Assert.True(CssLength.TryToPoints(n, unit, out double pt), unit);
        return pt;
    }

    private static double Pixels(double n, string unit)
    {
        Assert.True(CssLength.TryToPixels(n, unit, out double px), unit);
        return px;
    }
}
