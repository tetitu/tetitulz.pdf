// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Base.Operations.Forms;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Forms;

/// <summary>
/// <see cref="DefaultAppearanceString"/>, the one reading of a <c>/DA</c> string (§12.7.3.3) behind the
/// form appearance generator, the renderers' fallback and the annotation appearance builder.
/// </summary>
public class DefaultAppearanceStringTests
{
    [Fact]
    public void TheFontItsSizeAndAnRgbFillAreRead()
    {
        var da = DefaultAppearanceString.Parse("/Helv 9 Tf 0 0 1 rg");

        Assert.Equal("Helv", da.FontResourceName);
        Assert.Equal(9f, da.FontSize);
        Assert.Equal(new[] { 0.0, 0.0, 1.0 }, da.Fill);
        Assert.Equal("0 0 1 rg", da.FillOperator());
    }

    /// <summary>CMYK is a fill colour too. The renderers' copy read only <c>g</c> and <c>rg</c>.</summary>
    [Fact]
    public void ACmykFillIsRead()
    {
        var da = DefaultAppearanceString.Parse("/Helv 12 Tf 1 0 0 0 k");

        Assert.Equal(new[] { 1.0, 0.0, 0.0, 0.0 }, da.Fill);
        Assert.Equal("1 0 0 0 k", da.FillOperator());
    }

    [Fact]
    public void AGrayFillIsRead_AndWrittenByThePdfFormatter()
    {
        var da = DefaultAppearanceString.Parse("/Helv 12 Tf .5 g");

        Assert.Equal(new[] { 0.5 }, da.Fill);
        Assert.Equal("0.5 g", da.FillOperator());
    }

    /// <summary>
    /// A colour whose operands are not all numbers is not a colour, and the one set before it
    /// stands. Two of the copies this replaced pasted such operands into a content stream as found.
    /// </summary>
    [Fact]
    public void AColourWithANonNumericOperandIsIgnored_AndTheEarlierOneStands()
    {
        var da = DefaultAppearanceString.Parse("/Helv 12 Tf 0 0 1 rg (x) g");

        Assert.Equal(new[] { 0.0, 0.0, 1.0 }, da.Fill);
    }

    /// <summary>The last colour set wins, as it would in a content stream.</summary>
    [Fact]
    public void TheLastColourWins()
    {
        var da = DefaultAppearanceString.Parse("0 g /Helv 12 Tf 1 0 0 rg");

        Assert.Equal(new[] { 1.0, 0.0, 0.0 }, da.Fill);
    }

    [Fact]
    public void ANonNumericSizeIsNoSize_AndZeroIsKeptForAutoSize()
    {
        Assert.Null(DefaultAppearanceString.Parse("/Helv x Tf").FontSize);
        Assert.Equal(0f, DefaultAppearanceString.Parse("/Helv 0 Tf").FontSize);
    }

    [Fact]
    public void WithNoTfThereIsNoFont_AndWithNoColourThereIsNoFill()
    {
        var colourOnly = DefaultAppearanceString.Parse("0 g");
        Assert.Null(colourOnly.FontResourceName);
        Assert.Null(colourOnly.FontSize);
        Assert.Equal(new[] { 0.0 }, colourOnly.Fill);

        var fontOnly = DefaultAppearanceString.Parse("/Helv 12 Tf");
        Assert.Null(fontOnly.Fill);
        Assert.Null(fontOnly.FillOperator());

        // A size with no slash-name before it is a size, but names no font.
        Assert.Null(DefaultAppearanceString.Parse("Helv 12 Tf").FontResourceName);
        Assert.Equal(12f, DefaultAppearanceString.Parse("Helv 12 Tf").FontSize);
    }

    /// <summary>
    /// A non-finite number is not a number: <c>NaN</c> and <c>Infinity</c> parse as doubles, and a
    /// colour or size built from one would reach a content stream or a quantiser as garbage.
    /// </summary>
    [Fact]
    public void NonFiniteOperandsAreNotNumbers()
    {
        Assert.Null(DefaultAppearanceString.Parse("/Helv 12 Tf NaN g").Fill);
        Assert.Null(DefaultAppearanceString.Parse("/Helv 12 Tf Infinity 0 0 rg").Fill);
        Assert.Null(DefaultAppearanceString.Parse("/Helv NaN Tf").FontSize);
    }

    [Fact]
    public void NullAndEmptyReadAsNothing()
    {
        foreach (string da in new[] { null, "" })
        {
            var parsed = DefaultAppearanceString.Parse(da);
            Assert.Null(parsed.FontResourceName);
            Assert.Null(parsed.FontSize);
            Assert.Null(parsed.Fill);
        }
    }
}
