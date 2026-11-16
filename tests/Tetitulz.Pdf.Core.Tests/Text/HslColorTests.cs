// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// <see cref="HslColor"/>, CSS Color 4 §7's model: the primaries at their hue, grey with no
/// saturation, the hue wrapping, and the round trip through <see cref="HslColor.FromRgb"/>.
/// </summary>
public sealed class HslColorTests
{
    /// <summary>The three channels to nine places, so two computations compare as arrays.</summary>
    private static double[] Rounded((double R, double G, double B) c)
        => new[] { Math.Round(c.R, 9), Math.Round(c.G, 9), Math.Round(c.B, 9) };

    [Fact]
    public void ToRgb_ThePrimaries_AreAtAThirdOfATurnApart()
    {
        Assert.Equal(new[] { 1.0, 0, 0 }, Rounded(HslColor.ToRgb(0, 1, 0.5)));
        Assert.Equal(new[] { 0, 1.0, 0 }, Rounded(HslColor.ToRgb(1.0 / 3, 1, 0.5)));
        Assert.Equal(new[] { 0, 0, 1.0 }, Rounded(HslColor.ToRgb(2.0 / 3, 1, 0.5)));
    }

    /// <summary>CSS's own example: <c>hsl(120 75% 25%)</c> is rgb(16 112 16), to the nearest byte.</summary>
    [Fact]
    public void ToRgb_ASaturatedDarkGreen_IsCssColorsValue()
    {
        var (r, g, b) = HslColor.ToRgb(120.0 / 360, 0.75, 0.25);
        Assert.Equal(new[] { 16, 112, 16 }, new[] { (int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255) });
    }

    [Fact]
    public void ToRgb_NoSaturation_IsGrey_AndTheHueWraps()
    {
        Assert.Equal(new[] { 0.4, 0.4, 0.4 }, Rounded(HslColor.ToRgb(0.7, 0, 0.4)));
        Assert.Equal(Rounded(HslColor.ToRgb(0.75, 0.6, 0.5)), Rounded(HslColor.ToRgb(-0.25, 0.6, 0.5)));
        Assert.Equal(Rounded(HslColor.ToRgb(0.1, 0.6, 0.5)), Rounded(HslColor.ToRgb(2.1, 0.6, 0.5)));
        Assert.NotEqual(Rounded(HslColor.ToRgb(0.1, 0.6, 0.5)), Rounded(HslColor.ToRgb(0.75, 0.6, 0.5)));
    }

    [Theory]
    [InlineData(0.2, 0.5, 0.7)]
    [InlineData(0.9, 0.1, 0.3)]
    [InlineData(0.6, 0.6, 0.0)]
    public void FromRgb_RoundTripsThroughToRgb(double r, double g, double b)
    {
        var (h, s, l) = HslColor.FromRgb(r, g, b);
        Assert.Equal(new[] { r, g, b }, Rounded(HslColor.ToRgb(h, s, l)));
    }

    [Fact]
    public void FromRgb_AGrey_HasNoHueOrSaturation()
    {
        Assert.Equal((0.0, 0.0, 0.5), HslColor.FromRgb(0.5, 0.5, 0.5));
    }
}
