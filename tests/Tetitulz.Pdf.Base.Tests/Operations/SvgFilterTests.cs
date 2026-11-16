// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> applies <c>filter</c> (Filter Effects 1): the element is rendered
/// over the filter region, its pixels run through the primitives, and the result drawn back as an
/// image, before the element's clip, mask and opacity.
/// </summary>
/// <remarks>
/// It was reported and the element drawn without it: a drop shadow, a glow or a blur came out as the
/// bare shape. Every case draws on a white 200 × 200 page; the red square is x and y 50–100 unless
/// it says otherwise.
/// </remarks>
[Collection(PdfLimitsCollection.Name)]
public class SvgFilterTests
{
    private const string Square = @"<rect x=""50"" y=""50"" width=""50"" height=""50"" fill=""#ff0000"" filter=""url(#f)""/>";

    /// <summary>A blur spreads the square's edge: 3 past it is part-way, 20 past it is white again.</summary>
    [Fact]
    public void ABlurSpreadsTheEdge()
    {
        var at = Render(@"<defs><filter id=""f""><feGaussianBlur stdDeviation=""4""/></filter></defs>" + Square);

        AssertRed(at(75, 75));
        var edge = at(102, 75);
        Assert.InRange(edge.G, 20, 235);   // part red, part the white page
        Assert.InRange(edge.R, 250, 255);
        AssertWhite(at(104, 30));          // beyond the region's 10%
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>The classic drop shadow: SourceAlpha offset and blurred, merged under SourceGraphic.</summary>
    [Fact]
    public void ADropShadowFallsUnderTheShape()
    {
        var at = Render(
            @"<defs><filter id=""f"" x=""-50%"" y=""-50%"" width=""200%"" height=""200%"">" +
            @"<feOffset in=""SourceAlpha"" dx=""10"" dy=""10"" result=""o""/><feGaussianBlur in=""o"" stdDeviation=""1"" result=""b""/>" +
            @"<feMerge><feMergeNode in=""b""/><feMergeNode in=""SourceGraphic""/></feMerge></filter></defs>" + Square);

        AssertRed(at(75, 75));
        var shadow = at(105, 105);
        Assert.InRange(shadow.R, 0, 40);   // black, where only the shadow is
        Assert.InRange(shadow.G, 0, 40);
        AssertWhite(at(55, 105));          // below the square, left of the shadow
        AssertWhite(at(105, 55));          // right of the square, above the shadow
    }

    /// <summary>A flood composited in the source recolours the shape; around it the page stays white.</summary>
    [Fact]
    public void AFloodInTheSourceRecoloursTheShape()
    {
        var at = Render(
            @"<defs><filter id=""f""><feFlood flood-color=""#0000ff""/><feComposite in2=""SourceGraphic"" operator=""in""/></filter></defs>" + Square);

        var inside = at(75, 75);
        Assert.InRange(inside.B, 250, 255);
        Assert.InRange(inside.R, 0, 5);
        AssertWhite(at(47, 75));           // inside the region, outside the shape
    }

    /// <summary>A flood alone fills the region: −10% to 120% of the square, 45 to 105.</summary>
    [Fact]
    public void AFloodFillsTheRegion()
    {
        var at = Render(@"<defs><filter id=""f""><feFlood flood-color=""#0000ff""/></filter></defs>" + Square);

        Assert.InRange(at(47, 47).B, 250, 255);
        Assert.InRange(at(47, 47).R, 0, 5);
        Assert.InRange(at(103, 103).R, 0, 5);
        AssertWhite(at(43, 75));
        AssertWhite(at(107, 75));
    }

    /// <summary>
    /// saturate 0 greys red by luminance in linearRGB — 0.213, drawn as about 127 — and in sRGB
    /// when the filter asks, 0.213 as about 54. The space is the primitive's.
    /// </summary>
    [Theory]
    [InlineData("", 120, 134)]
    [InlineData(@" color-interpolation-filters=""sRGB""", 48, 60)]
    public void ASaturateIsInTheFiltersColourSpace(string space, int min, int max)
    {
        var at = Render($@"<defs><filter id=""f""{space}><feColorMatrix type=""saturate"" values=""0""/></filter></defs>" + Square);

        var grey = at(75, 75);
        Assert.InRange(grey.R, min, max);
        Assert.InRange(grey.G, min, max);
        Assert.InRange(grey.B, min, max);
    }

    /// <summary>A blend screens the flood onto the source: blue on red is magenta, blue alone where the source is not.</summary>
    [Fact]
    public void ABlendMixesItsInputs()
    {
        var at = Render(
            @"<defs><filter id=""f""><feFlood flood-color=""#0000ff"" result=""blue""/>" +
            @"<feBlend in=""blue"" in2=""SourceGraphic"" mode=""screen""/></filter></defs>" + Square);

        var both = at(75, 75);
        Assert.InRange(both.R, 250, 255);
        Assert.InRange(both.G, 0, 5);
        Assert.InRange(both.B, 250, 255);
        var blue = at(47, 75);
        Assert.InRange(blue.R, 0, 5);
        Assert.InRange(blue.B, 250, 255);
    }

    /// <summary>
    /// The element's opacity applies after the filter: a half-opaque square over its own shadow is
    /// half red, (255, 128, 128), where applied before it the shadow showed through, (191, 64, 64).
    /// </summary>
    [Fact]
    public void TheOpacityAppliesToTheFiltersResult()
    {
        var at = Render(
            @"<defs><filter id=""f"" x=""-50%"" y=""-50%"" width=""200%"" height=""200%"">" +
            @"<feOffset in=""SourceAlpha"" dx=""10"" dy=""10"" result=""o""/>" +
            @"<feMerge><feMergeNode in=""o""/><feMergeNode in=""SourceGraphic""/></feMerge></filter></defs>" +
            @"<rect x=""50"" y=""50"" width=""50"" height=""50"" fill=""#ff0000"" opacity=""0.5"" filter=""url(#f)""/>");

        var over = at(90, 90);
        Assert.InRange(over.R, 250, 255);
        Assert.InRange(over.G, 120, 136);
    }

    /// <summary>A region in user space cuts the result: here at x 70, through the square.</summary>
    [Fact]
    public void AUserSpaceRegionClipsTheResult()
    {
        var at = Render(
            @"<defs><filter id=""f"" filterUnits=""userSpaceOnUse"" x=""0"" y=""0"" width=""70"" height=""200"">" +
            @"<feOffset dx=""0""/></filter></defs>" + Square);

        AssertRed(at(65, 75));
        AssertWhite(at(75, 75));
    }

    /// <summary>The default region is the box, 10% more each way: an offset of 30 is cut at 105.</summary>
    [Fact]
    public void TheDefaultRegionClipsAnOffset()
    {
        var at = Render(@"<defs><filter id=""f""><feOffset dx=""30""/></filter></defs>" + Square);

        AssertRed(at(100, 75));
        AssertWhite(at(108, 75));
        AssertWhite(at(60, 75));           // the square moved away from here
    }

    /// <summary>A group's filter takes what the group draws as one: a flood in two squares' union.</summary>
    [Fact]
    public void AGroupsFilterTakesItsUnion()
    {
        var at = Render(
            @"<defs><filter id=""f""><feFlood flood-color=""#0000ff""/><feComposite in2=""SourceGraphic"" operator=""in""/></filter></defs>" +
            @"<g filter=""url(#f)""><rect x=""20"" y=""20"" width=""20"" height=""20"" fill=""#ff0000""/>" +
            @"<rect x=""120"" y=""20"" width=""20"" height=""20"" fill=""#ff0000""/></g>");

        Assert.InRange(at(30, 30).B, 250, 255);
        Assert.InRange(at(130, 30).B, 250, 255);
        AssertWhite(at(80, 30));
    }

    /// <summary>A filter in box units on an image uses its place; primitiveUnits in the box scale a deviation.</summary>
    [Fact]
    public void PrimitiveUnitsInTheBoxScaleTheDeviation()
    {
        var user = Render(@"<defs><filter id=""f""><feGaussianBlur stdDeviation=""4""/></filter></defs>" + Square);
        var box = Render(@"<defs><filter id=""f"" primitiveUnits=""objectBoundingBox""><feGaussianBlur stdDeviation=""0.08""/></filter></defs>" + Square);

        Assert.InRange(Math.Abs(user(102, 75).G - box(102, 75).G), 0, 12);   // 0.08 of 50 is 4
    }

    /// <summary>An empty filter draws nothing (Filter Effects 1 §7), and says nothing.</summary>
    [Fact]
    public void AnEmptyFilterDrawsNothing()
    {
        var at = Render(@"<defs><filter id=""f""/></defs>" + Square + @"<rect x=""120"" y=""50"" width=""20"" height=""20"" fill=""#ff0000""/>");

        AssertWhite(at(75, 75));
        AssertRed(at(130, 60));            // the page still draws
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>A primitive this does not know leaves the element unfiltered, reported by name.</summary>
    [Fact]
    public void AnUnknownPrimitiveIsReported()
    {
        var at = Render(@"<defs><filter id=""f""><feCustom/></filter></defs>" + Square);

        AssertRed(at(75, 75));
        Assert.Contains("<feCustom>", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A diffuse light over the flat square is the lighting colour over the whole region: green,
    /// opaque, from straight above; at 30° up, N·L is ½, half the light — linear ½ is sRGB 188.
    /// </summary>
    [Fact]
    public void ADiffuseLightIsTheLightingColourOverTheRegion()
    {
        var above = Render(@"<defs><filter id=""f""><feDiffuseLighting lighting-color=""#00ff00""><feDistantLight elevation=""90""/></feDiffuseLighting></filter></defs>" + Square);
        var low = Render(@"<defs><filter id=""f""><feDiffuseLighting lighting-color=""#00ff00""><feDistantLight azimuth=""45"" elevation=""30""/></feDiffuseLighting></filter></defs>" + Square);

        foreach (var p in new[] { above(75, 75), above(47, 75) })
        {
            Assert.InRange(p.R, 0, 3);
            Assert.InRange(p.G, 252, 255);
            Assert.InRange(p.B, 0, 3);
        }
        AssertWhite(above(30, 75));
        Assert.InRange(low(75, 75).G, 185, 191);
        Assert.InRange(low(75, 75).R, 0, 3);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// A specular light is as opaque as its brightest channel: blue from straight above is opaque
    /// blue; from the horizon, N·H is cos 45°, blue 0.7071 at alpha 0.7071 — straight blue 1 at
    /// alpha 0.7071, which over white is (75, 75, 255): the alpha shows in red and green.
    /// </summary>
    [Fact]
    public void ASpecularLightIsAsOpaqueAsItsBrightestChannel()
    {
        var above = Render(@"<defs><filter id=""f""><feSpecularLighting lighting-color=""#0000ff"" specularExponent=""1""><feDistantLight elevation=""90""/></feSpecularLighting></filter></defs>" + Square);
        var glancing = Render(@"<defs><filter id=""f""><feSpecularLighting lighting-color=""#0000ff"" specularExponent=""1""><feDistantLight elevation=""0""/></feSpecularLighting></filter></defs>" + Square);

        Assert.InRange(above(75, 75).B, 252, 255);
        Assert.InRange(above(75, 75).R, 0, 3);
        var p = glancing(75, 75);
        Assert.InRange(p.R, 71, 79);
        Assert.InRange(p.G, 71, 79);
        Assert.InRange(p.B, 252, 255);
    }

    /// <summary>
    /// A specular exponent below 1 is an error (§9.11), read as 1: at ½ the glancing light would be
    /// √cos 45°, alpha 0.84 and red 41; at 1 it is the same (75, 75, 255) as an exponent of 1 gives.
    /// </summary>
    [Fact]
    public void ASpecularExponentBelowOneIsReadAsOne()
    {
        var at = Render(@"<defs><filter id=""f""><feSpecularLighting lighting-color=""#0000ff"" specularExponent=""0.5""><feDistantLight elevation=""0""/></feSpecularLighting></filter></defs>" + Square);

        Assert.InRange(at(75, 75).R, 71, 79);
        Assert.InRange(at(75, 75).G, 71, 79);
    }

    /// <summary>
    /// A point light's place is in user units, its height too: the square at half its coordinates
    /// under <c>scale(2)</c>, lit from twice the height, is the same picture as the plain square;
    /// and the pixel under the light is brighter than one off to the side.
    /// </summary>
    [Fact]
    public void APointLightIsPlacedInUserUnits()
    {
        string Filter(string x, string y, string z) => $@"<defs><filter id=""f""><feDiffuseLighting lighting-color=""#00ff00""><fePointLight x=""{x}"" y=""{y}"" z=""{z}""/></feDiffuseLighting></filter></defs>";
        var plain = Render(Filter("75", "75", "20") + Square);
        var scaled = Render(Filter("37.5", "37.5", "10") + @"<g transform=""scale(2)""><rect x=""25"" y=""25"" width=""25"" height=""25"" fill=""#ff0000"" filter=""url(#f)""/></g>");

        Assert.True(plain(75, 75).G > plain(50, 50).G + 20, $"under the light {plain(75, 75).G}, aside {plain(50, 50).G}");
        var points = new[] { (50, 50), (60, 75), (75, 75), (90, 60), (100, 100) };
        Assert.All(points, p => Assert.Equal(plain(p.Item1, p.Item2), scaled(p.Item1, p.Item2)));
    }

    /// <summary>
    /// A fractal noise at frequency 0 is the reference algorithm's lattice at its origin: noise 0, so
    /// every channel (0 + 1) / 2 — half grey at half alpha, linear, over the whole region: sRGB 188 at
    /// alpha 128 over white is 221. A turbulence at frequency 0 is |0|: transparent, nothing drawn.
    /// </summary>
    [Fact]
    public void AZeroFrequencyNoiseIsHalfGreyOrNothing()
    {
        var fractal = Render(@"<defs><filter id=""f""><feTurbulence type=""fractalNoise"" baseFrequency=""0""/></filter></defs>" + Square);
        var turbulence = Render(@"<defs><filter id=""f""><feTurbulence baseFrequency=""0""/></filter></defs>" + Square);

        foreach (var p in new[] { fractal(75, 75), fractal(47, 75) })
        {
            Assert.InRange(p.R, 218, 224);
            Assert.InRange(p.G, 218, 224);
            Assert.InRange(p.B, 218, 224);
        }
        AssertWhite(fractal(30, 75));          // outside the region
        AssertWhite(turbulence(75, 75));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>A turbulence at a frequency, its one octave the default, replaces the square with noise: not red, not uniform, not reported.</summary>
    [Fact]
    public void ATurbulenceIsDrawnInPlaceOfTheElement()
    {
        var at = Render(@"<defs><filter id=""f""><feTurbulence baseFrequency=""0.05"" seed=""3""/></filter></defs>" + Square);

        var samples = new[] { at(50, 50), at(60, 75), at(75, 75), at(90, 60), at(100, 100) };
        Assert.All(samples, p => Assert.True(p.G > 5 || p.R < 250, "the square is drawn red, unfiltered"));
        Assert.True(samples.Distinct().Count() > 1, "the noise is uniform");
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// The noise is sampled in the element's user space, not on the region's pixels: a square drawn
    /// at half its coordinates under <c>scale(2)</c>, at twice the frequency, is the same picture as
    /// the plain square — the same user points at the same lattice — where the plain square at twice
    /// the frequency is another picture.
    /// </summary>
    [Fact]
    public void ANoiseIsSampledInUserSpace()
    {
        string Filter(string frequency) => $@"<defs><filter id=""f""><feTurbulence baseFrequency=""{frequency}"" seed=""3""/></filter></defs>";
        var plain = Render(Filter("0.05") + Square);
        var scaled = Render(Filter("0.1") + @"<g transform=""scale(2)""><rect x=""25"" y=""25"" width=""25"" height=""25"" fill=""#ff0000"" filter=""url(#f)""/></g>");
        var doubled = Render(Filter("0.1") + Square);

        var points = new[] { (50, 50), (60, 75), (75, 75), (90, 60), (100, 100) };
        Assert.All(points, p => Assert.Equal(plain(p.Item1, p.Item2), scaled(p.Item1, p.Item2)));
        Assert.Contains(points, p => plain(p.Item1, p.Item2) != doubled(p.Item1, p.Item2));
    }

    /// <summary>
    /// A hostile octave count is a pass over the pixels each: two primitives of two thousand million
    /// octaves would drive the resolution below a dot an inch, so the element is drawn without its
    /// filter, reported — in the time a report takes.
    /// </summary>
    [Fact]
    public void AHostileOctaveCountIsNotFiltered()
    {
        var at = Render(@"<defs><filter id=""f""><feTurbulence numOctaves=""2000000000""/><feTurbulence numOctaves=""1e30""/></filter></defs>" + Square);

        AssertRed(at(75, 75));
        Assert.Contains("was not filtered", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A displacement map reads the source from where the map's channels send it: a red flood as the
    /// map, R for x and G for y at a scale of 20, reads 10 to the right and 10 up — the square moves
    /// left and down by 10 within the region.
    /// </summary>
    [Fact]
    public void ADisplacementMapMovesTheSquareByItsChannels()
    {
        var at = Render(
            @"<defs><filter id=""f""><feFlood flood-color=""#ff0000"" result=""m""/>" +
            @"<feDisplacementMap in=""SourceGraphic"" in2=""m"" scale=""20"" xChannelSelector=""R"" yChannelSelector=""G""/></filter></defs>" + Square);

        AssertRed(at(47, 75));     // x 45..90 is red now
        AssertWhite(at(95, 75));   // 90..100 no longer
        AssertRed(at(75, 62));     // y 60..105
        AssertWhite(at(75, 57));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>The scale is a user-space length: under a scale of one half, 40 user units are the same 10 points.</summary>
    [Fact]
    public void ADisplacementIsInUserUnits()
    {
        var at = Render(
            @"<defs><filter id=""f""><feFlood flood-color=""#ff0000"" result=""m""/>" +
            @"<feDisplacementMap in=""SourceGraphic"" in2=""m"" scale=""40"" xChannelSelector=""R"" yChannelSelector=""G""/></filter></defs>" +
            @"<g transform=""scale(0.5)""><rect x=""100"" y=""100"" width=""100"" height=""100"" fill=""#ff0000"" filter=""url(#f)""/></g>");

        AssertRed(at(47, 75));
        AssertWhite(at(95, 75));
        AssertRed(at(75, 62));
        AssertWhite(at(75, 57));
    }

    /// <summary>A CSS filter function this does not know, or one it cannot read, is reported and the element drawn without it.</summary>
    [Theory]
    [InlineData("bogus(1)", "bogus() is not a filter function")]
    [InlineData("blur(-2px)", "blur(-2px) is negative")]
    [InlineData("blur(2em)", "cannot read")]
    [InlineData("grayscale(-1)", "grayscale(-1) is negative")]
    [InlineData("hue-rotate(1px)", "cannot read")]
    [InlineData("drop-shadow(red)", "is not a colour with two or three lengths")]
    [InlineData("blur(1px) url(#nothing)", "a url() among filter functions")]
    [InlineData("url(#nothing) blur(1px)", "names no filter")]
    [InlineData("blur(2px", "is not a list of filter functions")]
    [InlineData("2px", "is not a list of filter functions")]
    public void ACssFilterFunctionThisCannotReadIsReported(string function, string reported)
    {
        var at = Render($@"<rect x=""50"" y=""50"" width=""50"" height=""50"" fill=""#ff0000"" style=""filter: {function}""/>");

        AssertRed(at(75, 75));
        Assert.Contains(reported, SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Each CSS filter function is its equivalent primitive (Filter Effects 1 §10.1), in sRGB: the
    /// red square's centre becomes the colour worked from the specification's matrix or transfer.
    /// grayscale(1) of red is 0.2126 — sRGB 54, where linearRGB would have given 128.
    /// </summary>
    [Theory]
    [InlineData("grayscale(1)", 54, 54, 54)]
    [InlineData("grayscale(100%)", 54, 54, 54)]
    [InlineData("grayscale()", 54, 54, 54)]
    [InlineData("grayscale(0.5)", 155, 27, 27)]
    [InlineData("saturate(0)", 54, 54, 54)]
    [InlineData("sepia(1)", 100, 89, 69)]
    [InlineData("invert(1)", 0, 255, 255)]
    [InlineData("invert(50%)", 128, 128, 128)]
    [InlineData("opacity(0.5)", 255, 128, 128)]
    [InlineData("brightness(0.5)", 128, 0, 0)]
    [InlineData("contrast(0.5)", 191, 64, 64)]
    [InlineData("hue-rotate(120deg)", 0, 113, 0)]
    [InlineData("hue-rotate(0.3333turn)", 0, 113, 0)]
    [InlineData("hue-rotate(2.0944rad)", 0, 113, 0)]
    [InlineData("hue-rotate(133.33grad)", 0, 113, 0)]
    [InlineData("brightness(0.5) invert(1)", 128, 255, 255)]
    [InlineData("invert(1) brightness(0.5)", 0, 128, 128)]
    public void ACssFilterFunctionIsItsPrimitive(string functions, int r, int g, int b)
    {
        var at = Render($@"<rect x=""50"" y=""50"" width=""50"" height=""50"" fill=""#ff0000"" style=""filter: {functions}""/>");

        var p = at(75, 75);
        Assert.InRange(p.R, r - 3, r + 3);
        Assert.InRange(p.G, g - 3, g + 3);
        Assert.InRange(p.B, b - 3, b + 3);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// blur(4px) softens the square's edge (mixed pixels either side of x 50) and reaches past the
    /// default region: x 44 is outside the −10% region and is tinted all the same, since the region
    /// grows by three deviations.
    /// </summary>
    [Fact]
    public void ACssBlurSoftensTheEdgeAndGrowsTheRegion()
    {
        var at = Render(@"<rect x=""50"" y=""50"" width=""50"" height=""50"" fill=""#ff0000"" style=""filter: blur(4px)""/>");

        AssertRed(at(75, 75));
        Assert.InRange(at(48, 75).G, 100, 250);
        Assert.InRange(at(52, 75).G, 5, 150);
        Assert.InRange(at(44, 75).G, 100, 245);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// drop-shadow(20px 0 blue): a blue shadow 20 to the right, under the square — beyond the default
    /// region, which grows by the offset; the colour before the lengths or after them alike, and the
    /// element's <c>color</c> when none is given.
    /// </summary>
    [Theory]
    [InlineData("drop-shadow(20px 0 blue)")]
    [InlineData("drop-shadow(20px 0px 0 rgb(0, 0, 255))")]
    [InlineData("drop-shadow(#0000ff 20 0)")]
    [InlineData("drop-shadow(20px 0)")]
    public void ACssDropShadowIsOffsetInItsColour(string function)
    {
        var at = Render($@"<rect x=""50"" y=""50"" width=""50"" height=""50"" fill=""#ff0000"" color=""blue"" style=""filter: {function}""/>");

        AssertRed(at(75, 75));
        var shadow = at(112, 75);
        Assert.InRange(shadow.B, 250, 255);
        Assert.InRange(shadow.R, 0, 5);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>blur() with no argument is a blur of 0: the edge stays sharp on both sides of x 50.</summary>
    [Fact]
    public void ACssBlurWithNoArgumentIsNone()
    {
        var at = Render(@"<rect x=""50"" y=""50"" width=""50"" height=""50"" fill=""#ff0000"" style=""filter: blur()""/>");

        AssertWhite(at(49, 75));
        AssertRed(at(51, 75));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>drop-shadow(0 20px blue) falls below the square, not beside it: the second length is dy.</summary>
    [Fact]
    public void ACssDropShadowsSecondLengthIsDown()
    {
        var at = Render(@"<rect x=""50"" y=""50"" width=""50"" height=""50"" fill=""#ff0000"" style=""filter: drop-shadow(0 20px blue)""/>");

        Assert.InRange(at(75, 112).B, 250, 255);
        Assert.InRange(at(75, 112).R, 0, 5);
        AssertWhite(at(112, 75));
    }

    /// <summary>
    /// A filtered svg's opacity applies once, to the filter's result: at opacity ½ through an identity
    /// filter the square is half red over white, (255, 128, 128) — not a quarter.
    /// </summary>
    [Fact]
    public void AFilteredSvgsOpacityAppliesOnce()
    {
        var at = Render(
            @"<defs><filter id=""f""><feOffset/></filter></defs>" +
            @"<svg x=""50"" y=""50"" width=""50"" height=""50"" opacity=""0.5"" filter=""url(#f)""><rect width=""50"" height=""50"" fill=""#ff0000""/></svg>");

        var p = at(75, 75);
        Assert.InRange(p.R, 250, 255);
        Assert.InRange(p.G, 124, 132);
        Assert.InRange(p.B, 124, 132);
    }

    /// <summary>
    /// A filter on a nested svg filters what it draws, its box its children's in the outer space: a
    /// blue flood over the svg's region, which is the square's; and on the root, over the picture.
    /// </summary>
    [Fact]
    public void AFilterOnAnSvgFiltersWhatItDraws()
    {
        var nested = Render(
            @"<defs><filter id=""f""><feFlood flood-color=""#0000ff""/></filter></defs>" +
            @"<svg x=""50"" y=""50"" width=""50"" height=""50"" filter=""url(#f)""><rect width=""50"" height=""50"" fill=""#ff0000""/></svg>");
        byte[] root = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"" style=""filter: grayscale(1)"">" +
            @"<rect x=""50"" y=""50"" width=""50"" height=""50"" fill=""#ff0000""/></svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });
        var page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(root), 0, 72);
        (int R, int G, int B) At(int x, int y) { int i = y * page.Stride + x * 4; return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]); }

        Assert.InRange(nested(75, 75).B, 250, 255);
        Assert.InRange(nested(75, 75).R, 0, 5);
        Assert.InRange(nested(47, 75).B, 250, 255);   // the −10% region
        AssertWhite(nested(30, 75));
        Assert.InRange(At(75, 75).R, 51, 57);
        Assert.InRange(At(75, 75).G, 51, 57);
        AssertWhite(At(30, 75));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// Past <see cref="PdfLimits.MaxSvgFilteredElements"/> the rest are drawn without their filters,
    /// reported: the first square is flooded blue, the second stays red.
    /// </summary>
    [Fact]
    public void PastTheFilteredElementLimitTheRestAreUnfiltered()
    {
        try
        {
            PdfLimits.MaxSvgFilteredElements = 1;
            var at = Render(
                @"<defs><filter id=""f""><feFlood flood-color=""#0000ff""/><feComposite in2=""SourceGraphic"" operator=""in""/></filter></defs>" +
                Square + @"<rect x=""120"" y=""50"" width=""50"" height=""50"" fill=""#ff0000"" filter=""url(#f)""/>");

            Assert.InRange(at(75, 75).B, 250, 255);
            AssertRed(at(145, 75));
            Assert.Contains("filtered elements", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// <see cref="PdfLimits.MaxSvgFilterPixels"/> lowers the resolution: the filter's image is no
    /// more than that many pixels. The region is 60 points square, 120 pixels at two a point.
    /// </summary>
    [Fact]
    public void ThePixelLimitLowersTheResolution()
    {
        byte[] full = Convert(@"<defs><filter id=""f""><feOffset/></filter></defs>" + Square);
        try
        {
            PdfLimits.MaxSvgFilterPixels = 900;
            byte[] limited = Convert(@"<defs><filter id=""f""><feOffset/></filter></defs>" + Square);

            Assert.InRange(ImageWidth(full), 118, 122);
            Assert.InRange(ImageWidth(limited), 1, 30);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// A hostile region too large to render even at one dot an inch is not filtered: drawn without,
    /// reported. A limit of one pixel stands in for a region of millions of points.
    /// </summary>
    [Fact]
    public void AHostileRegionPastTheLimitIsDrawnUnfiltered()
    {
        try
        {
            PdfLimits.MaxSvgFilterPixels = 0;
            var at = Render(@"<defs><filter id=""f""><feFlood flood-color=""#0000ff""/></filter></defs>" + Square);

            AssertRed(at(75, 75));
            Assert.Contains("one dot an inch", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>A filter inside a clipPath's shapes is no paint: the clip is the shape's geometry.</summary>
    [Fact]
    public void AFilterInAClipPathIsNotApplied()
    {
        var at = Render(
            @"<defs><filter id=""f"" filterUnits=""userSpaceOnUse""><feFlood flood-color=""#0000ff""/></filter>" +
            @"<clipPath id=""c""><rect x=""50"" y=""50"" width=""50"" height=""50"" filter=""url(#f)""/></clipPath></defs>" +
            @"<rect width=""200"" height=""200"" fill=""#ff0000"" clip-path=""url(#c)""/>");

        AssertRed(at(75, 75));
        AssertWhite(at(47, 75));
    }

    /// <summary>
    /// An image in a clipPath is no part of the clip, and its filter is not applied there either: no
    /// image is written, and the clip is the rect. In clip mode nothing has a box, so only a region in
    /// user space could be filtered, and only an image reaches the filter at all: the clip-mode guard
    /// is what keeps it out of the clip's path.
    /// </summary>
    [Fact]
    public void AnImagesFilterInAClipPathIsNotApplied()
    {
        using var bmp = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(1, 1, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul));
        bmp.SetPixel(0, 0, new SkiaSharp.SKColor(0, 0, 255));
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        string href = "data:image/png;base64," + System.Convert.ToBase64String(data.ToArray());
        string body =
            @"<defs><filter id=""f"" filterUnits=""userSpaceOnUse""><feFlood flood-color=""#0000ff""/></filter>" +
            @"<clipPath id=""c""><rect x=""50"" y=""50"" width=""50"" height=""50""/>" +
            $@"<image href=""{href}"" x=""0"" y=""0"" width=""10"" height=""10"" filter=""url(#f)""/></clipPath></defs>" +
            @"<rect width=""200"" height=""200"" fill=""#ff0000"" clip-path=""url(#c)""/>";

        Assert.DoesNotContain("/Subtype /Image", Encoding.Latin1.GetString(Convert(body)), StringComparison.Ordinal);
        var at = Render(body);
        AssertRed(at(75, 75));             // the clip still works
        AssertWhite(at(47, 75));
    }

    /// <summary>
    /// A result is read by name from further back than the previous primitive: the flood named
    /// "blue", two primitives before the composite that reads it, recolours the shape.
    /// </summary>
    [Fact]
    public void ANamedResultIsReadFromFurtherBack()
    {
        var at = Render(
            @"<defs><filter id=""f""><feFlood flood-color=""#0000ff"" result=""blue""/>" +
            @"<feOffset in=""SourceGraphic"" result=""shape""/>" +
            @"<feComposite in=""blue"" in2=""shape"" operator=""in""/></filter></defs>" + Square);

        Assert.InRange(at(75, 75).B, 250, 255);
        Assert.InRange(at(75, 75).R, 0, 5);
        AssertWhite(at(47, 75));
    }

    /// <summary>feDropShadow is the drop shadow in one primitive: black under and beside the square.</summary>
    [Fact]
    public void ADropShadowPrimitiveCastsAShadow()
    {
        var at = Render(
            @"<defs><filter id=""f"" x=""-50%"" y=""-50%"" width=""200%"" height=""200%"">" +
            @"<feDropShadow dx=""10"" dy=""10"" stdDeviation=""1""/></filter></defs>" + Square);

        AssertRed(at(75, 75));
        var shadow = at(105, 105);
        Assert.InRange(shadow.R, 0, 40);
        Assert.InRange(shadow.G, 0, 40);
        AssertWhite(at(55, 105));
        AssertWhite(at(105, 55));
    }

    /// <summary>
    /// A drop shadow's dx and dy are 2 by default, unlike an offset's 0: with no blur its blue shadow
    /// covers 52 to 102 each way, beyond the square's right and bottom edges and not left of 52.
    /// </summary>
    [Fact]
    public void ADropShadowsOffsetIsTwoByDefault()
    {
        var at = Render(@"<defs><filter id=""f""><feDropShadow stdDeviation=""0"" flood-color=""#0000ff""/></filter></defs>" + Square);

        Assert.InRange(at(101, 90).B, 250, 255);
        Assert.InRange(at(101, 90).R, 0, 5);
        Assert.InRange(at(53, 101).B, 250, 255);   // below the square the shadow starts at 52…
        AssertWhite(at(51, 101));                  // …not at 50
        AssertRed(at(75, 75));
    }

    /// <summary>
    /// A drop shadow's deviation is 2 by default: moved 20 right, its edge at 120 is soft, part blue
    /// just past it; with no blur it would stop sharp there.
    /// </summary>
    [Fact]
    public void ADropShadowsDeviationIsTwoByDefault()
    {
        var at = Render(
            @"<defs><filter id=""f"" x=""-50%"" y=""-50%"" width=""200%"" height=""200%"">" +
            @"<feDropShadow dx=""20"" dy=""0"" flood-color=""#0000ff""/></filter></defs>" + Square);

        Assert.InRange(at(121, 75).R, 20, 235);
    }

    /// <summary>
    /// A deviation negative on either axis disables the blur (§9.13) — both axes, so a "-1 4" blur
    /// leaves the bottom edge sharp — in a blur and a drop shadow alike.
    /// </summary>
    [Theory]
    [InlineData(@"<feGaussianBlur stdDeviation=""-1 4""/>", 102)]
    [InlineData(@"<feDropShadow dx=""0"" dy=""0"" stdDeviation=""-1 4"" flood-color=""#ff0000""/>", 102)]
    public void ANegativeDeviationDisablesTheBlur(string primitive, int y)
    {
        var at = Render($@"<defs><filter id=""f"">{primitive}</filter></defs>" + Square);

        AssertWhite(at(75, y));
        AssertRed(at(75, 98));
    }

    /// <summary>
    /// A transfer function's attributes default to the identity: slope 1, amplitude 1, exponent 1,
    /// intercept and offset 0 — grey 128 stays grey through a bare linear and a bare gamma.
    /// </summary>
    [Fact]
    public void ATransferFunctionsDefaultsAreTheIdentity()
    {
        var at = Render(
            @"<defs><filter id=""f"" color-interpolation-filters=""sRGB""><feComponentTransfer>" +
            @"<feFuncR type=""linear""/><feFuncG type=""gamma""/></feComponentTransfer></filter></defs>" +
            @"<rect x=""50"" y=""50"" width=""50"" height=""50"" fill=""#808080"" filter=""url(#f)""/>");

        var p = at(75, 75);
        Assert.InRange(p.R, 122, 134);
        Assert.InRange(p.G, 122, 134);
    }

    /// <summary>A drop shadow's flood opacity is its alpha: half black beside the square is mid-grey.</summary>
    [Fact]
    public void ADropShadowsOpacityIsItsFloods()
    {
        var at = Render(@"<defs><filter id=""f"" color-interpolation-filters=""sRGB""><feDropShadow stdDeviation=""0"" flood-opacity=""0.5""/></filter></defs>" + Square);

        Assert.InRange(at(101, 90).R, 120, 136);
    }

    /// <summary>
    /// A component transfer maps each channel: red keeps its red, green becomes 0.5 by a table, blue 1
    /// by a line — (255, 128, 255) in sRGB. The last function for a channel is the one applied.
    /// </summary>
    [Fact]
    public void AComponentTransferMapsEachChannel()
    {
        var at = Render(
            @"<defs><filter id=""f"" color-interpolation-filters=""sRGB""><feComponentTransfer>" +
            @"<feFuncR type=""linear"" slope=""0""/><feFuncR type=""identity""/>" +
            @"<feFuncG type=""table"" tableValues=""0.5 0.5""/><feFuncB type=""linear"" intercept=""1""/>" +
            @"</feComponentTransfer></filter></defs>" + Square);

        var p = at(75, 75);
        Assert.InRange(p.R, 250, 255);
        Assert.InRange(p.G, 120, 136);
        Assert.InRange(p.B, 250, 255);
    }

    /// <summary>A discrete alpha function halves an opaque square: half red over white.</summary>
    [Fact]
    public void AComponentTransferMapsAlpha()
    {
        var at = Render(
            @"<defs><filter id=""f""><feComponentTransfer><feFuncA type=""discrete"" tableValues=""0 0.5""/></feComponentTransfer></filter></defs>" + Square);

        var p = at(75, 75);
        Assert.InRange(p.R, 250, 255);
        Assert.InRange(p.G, 120, 136);
    }

    /// <summary>A dilate of 4 grows the square each way, within the region's 45–105; an erode of 3 shrinks it.</summary>
    [Fact]
    public void AMorphologyGrowsOrShrinksTheShape()
    {
        var grown = Render(@"<defs><filter id=""f""><feMorphology operator=""dilate"" radius=""4""/></filter></defs>" + Square);
        var shrunk = Render(@"<defs><filter id=""f""><feMorphology radius=""3""/></filter></defs>" + Square);

        AssertRed(grown(47, 75));
        AssertRed(grown(75, 47));
        AssertRed(grown(75, 75));
        AssertWhite(shrunk(51, 75));
        AssertWhite(shrunk(75, 51));
        AssertRed(shrunk(55, 75));
    }

    /// <summary>A 3 × 3 box kernel softens the square's edge; the identity kernel leaves it sharp.</summary>
    [Fact]
    public void AConvolutionSoftensTheEdge()
    {
        var soft = Render(@"<defs><filter id=""f""><feConvolveMatrix order=""3"" kernelMatrix=""1 1 1 1 1 1 1 1 1""/></filter></defs>" + Square);
        var sharp = Render(@"<defs><filter id=""f""><feConvolveMatrix order=""3"" kernelMatrix=""0 0 0 0 1 0 0 0 0""/></filter></defs>" + Square);

        Assert.InRange(soft(50, 75).G, 20, 235);
        AssertRed(soft(75, 75));
        AssertRed(sharp(50, 75));
        AssertWhite(sharp(49, 75));
    }

    /// <summary>
    /// A convolution's kernel counts against the pixel limit, its pixels read for each of the image's:
    /// a 25 × 25 kernel over the 120-pixel region asks for 625 × 14 400, past the default, so the
    /// image is drawn smaller than a 3 × 3 kernel's.
    /// </summary>
    [Fact]
    public void ALargeKernelCountsAgainstThePixelLimit()
    {
        string small = string.Join(" ", System.Linq.Enumerable.Repeat("0", 9)).Remove(8, 1).Insert(8, "1");
        string large = string.Join(" ", System.Linq.Enumerable.Repeat("0", 625)).Remove(624, 1).Insert(624, "1");
        byte[] three = Convert($@"<defs><filter id=""f""><feConvolveMatrix order=""3"" kernelMatrix=""{small}""/></filter></defs>" + Square);
        byte[] big = Convert($@"<defs><filter id=""f""><feConvolveMatrix order=""25"" kernelMatrix=""{large}""/></filter></defs>" + Square);

        Assert.InRange(ImageWidth(three), 118, 122);
        Assert.InRange(ImageWidth(big), 100, 116);
    }

    /// <summary>
    /// A flood over a 10-square subregion, tiled, fills the whole filter region (45–105) blue — in
    /// the flood's colour space, or in the other, since the subregion goes with the image through
    /// the conversion.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(@" color-interpolation-filters=""sRGB""")]
    public void ATileFillsTheRegionWithItsInputsSubregion(string tileSpace)
    {
        var at = Render(
            $@"<defs><filter id=""f""><feFlood flood-color=""#0000ff"" x=""50"" y=""50"" width=""10"" height=""10"" result=""t""/><feTile in=""t""{tileSpace}/></filter></defs>" + Square);

        Assert.InRange(at(47, 47).B, 250, 255);
        Assert.InRange(at(47, 47).R, 0, 5);
        Assert.InRange(at(100, 100).B, 250, 255);
        AssertWhite(at(40, 40));
    }

    /// <summary>
    /// An feImage of a data URI fits the picture into the filter region by default — a 1 × 1 red
    /// pixel covers 45–105 — or into its own subregion.
    /// </summary>
    [Fact]
    public void AnImagePrimitiveDrawsItsPicture()
    {
        string href = RedPixel();
        var whole = Render($@"<defs><filter id=""f""><feImage href=""{href}""/></filter></defs>" + Blue);
        var part = Render($@"<defs><filter id=""f""><feImage href=""{href}"" x=""50"" y=""50"" width=""10"" height=""10""/></filter></defs>" + Blue);

        AssertRed(whole(47, 47));
        AssertRed(whole(103, 103));
        AssertWhite(whole(110, 75));
        AssertRed(part(55, 55));
        AssertWhite(part(75, 75));   // the source is replaced by the picture
    }

    /// <summary>An feImage of an element draws that element where it is: a rect at 60–80, inside the region; the source is gone.</summary>
    [Fact]
    public void AnImagePrimitiveDrawsAnElementWhereItIs()
    {
        var at = Render(
            @"<defs><rect id=""r"" x=""60"" y=""60"" width=""20"" height=""20"" fill=""#0000ff""/>" +
            @"<filter id=""f""><feImage href=""#r""/></filter></defs>" + Square);

        Assert.InRange(at(70, 70).B, 250, 255);
        Assert.InRange(at(70, 70).R, 0, 5);
        AssertWhite(at(90, 90));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// The named element is drawn as a use draws it: hung under the feImage, so it inherits from the
    /// filter — a rect with no fill of its own takes the filter's blue, not black.
    /// </summary>
    [Fact]
    public void AnImagePrimitivesElementInheritsFromTheFilter()
    {
        var at = Render(
            @"<defs><rect id=""r"" x=""60"" y=""60"" width=""20"" height=""20""/>" +
            @"<filter id=""f"" fill=""#0000ff""><feImage href=""#r""/></filter></defs>" + Square);

        Assert.InRange(at(70, 70).B, 250, 255);
        Assert.InRange(at(70, 70).R, 0, 5);
    }

    /// <summary>An feImage that names nothing is transparent, reported; the square it filters is gone with it.</summary>
    [Fact]
    public void AnImagePrimitiveNamingNothingIsReported()
    {
        var at = Render(@"<defs><filter id=""f""><feImage href=""#nothing""/></filter></defs>" + Square);

        AssertWhite(at(75, 75));
        Assert.Contains("feImage names no element", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A hostile feImage that names the element it filters draws that element, filter and all, again
    /// at each level; the nesting ceiling or the filtered-element limit ends it, reported.
    /// </summary>
    [Fact]
    public void AHostileSelfPicturingFilterEnds()
    {
        Convert(@"<defs><filter id=""f""><feImage href=""#s""/></filter></defs><rect id=""s"" x=""50"" y=""50"" width=""50"" height=""50"" fill=""#ff0000"" filter=""url(#f)""/>");

        Assert.Matches("nesting|filtered elements", SvgToPdfConverter.LastError ?? "");
    }

    private const string Blue = @"<rect x=""50"" y=""50"" width=""50"" height=""50"" fill=""#0000ff"" filter=""url(#f)""/>";

    private static string RedPixel()
    {
        using var bmp = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(1, 1, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul));
        bmp.SetPixel(0, 0, new SkiaSharp.SKColor(255, 0, 0));
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return "data:image/png;base64," + System.Convert.ToBase64String(data.ToArray());
    }

    /// <summary>A difference blend of a blue flood onto the red square is magenta inside it, blue beside it.</summary>
    [Fact]
    public void ADifferenceBlendMixesTheColours()
    {
        var at = Render(
            @"<defs><filter id=""f"" color-interpolation-filters=""sRGB""><feFlood flood-color=""#0000ff"" result=""blue""/>" +
            @"<feBlend in=""blue"" in2=""SourceGraphic"" mode=""difference""/></filter></defs>" + Square);

        var both = at(75, 75);
        Assert.InRange(both.R, 250, 255);
        Assert.InRange(both.G, 0, 5);
        Assert.InRange(both.B, 250, 255);
        Assert.InRange(at(47, 75).B, 250, 255);
    }

    /// <summary>A blur spreads the bottom edge too: the deviation is taken along both axes.</summary>
    [Fact]
    public void ABlurSpreadsBothAxes()
    {
        var at = Render(@"<defs><filter id=""f""><feGaussianBlur stdDeviation=""4""/></filter></defs>" + Square);

        Assert.InRange(at(75, 102).G, 20, 235);
        Assert.InRange(at(102, 75).G, 20, 235);
    }

    /// <summary>A flood's opacity is its alpha: half blue over white is (127, 127, 255).</summary>
    [Fact]
    public void AFloodsOpacityIsItsAlpha()
    {
        var at = Render(@"<defs><filter id=""f""><feFlood flood-color=""#0000ff"" flood-opacity=""0.5""/></filter></defs>" + Square);

        var half = at(75, 75);
        Assert.InRange(half.R, 120, 136);
        Assert.InRange(half.B, 250, 255);
    }

    /// <summary>arithmetic with k2 0.5 halves the source: half red over white is (255, 128, 128).</summary>
    [Fact]
    public void AnArithmeticCompositeReadsItsKs()
    {
        var at = Render(
            @"<defs><filter id=""f""><feComposite in=""SourceGraphic"" in2=""SourceGraphic"" operator=""arithmetic"" k2=""0.5""/></filter></defs>" + Square);

        var half = at(75, 75);
        Assert.InRange(half.R, 250, 255);
        Assert.InRange(half.G, 120, 136);
    }

    /// <summary>A primitive's subregion clips its result: the flood covers x and y 50–60 only.</summary>
    [Fact]
    public void APrimitivesSubregionClipsIt()
    {
        var at = Render(@"<defs><filter id=""f""><feFlood flood-color=""#0000ff"" x=""50"" y=""50"" width=""10"" height=""10""/></filter></defs>" + Square);

        Assert.InRange(at(55, 55).B, 250, 255);
        Assert.InRange(at(55, 55).R, 0, 5);
        AssertWhite(at(75, 75));
    }

    /// <summary>
    /// hueRotate 180 in sRGB turns red to (0, 0.426, 0.426), about (0, 109, 109); luminanceToAlpha
    /// makes green black at 0.7154, about 73 over white; a matrix of the wrong count passes red on.
    /// </summary>
    [Theory]
    [InlineData(@"type=""hueRotate"" values=""180""", "#ff0000", 0, 5, 100, 118)]
    [InlineData(@"type=""luminanceToAlpha""", "#00ff00", 65, 80, 65, 80)]
    [InlineData(@"type=""matrix"" values=""1 0 0 0 0""", "#ff0000", 250, 255, 0, 5)]
    public void AColourMatrixIsItsType(string attributes, string fill, int rMin, int rMax, int gMin, int gMax)
    {
        var at = Render(
            $@"<defs><filter id=""f"" color-interpolation-filters=""sRGB""><feColorMatrix {attributes}/></filter></defs>" +
            $@"<rect x=""50"" y=""50"" width=""50"" height=""50"" fill=""{fill}"" filter=""url(#f)""/>");

        var p = at(75, 75);
        Assert.InRange(p.R, rMin, rMax);
        Assert.InRange(p.G, gMin, gMax);
    }

    /// <summary>
    /// Under a rotation the region is not axis-aligned: its bounds are rendered, and the region
    /// clips them. A rotated square's filter region is a diamond on the page, and a corner of its
    /// bounds stays white.
    /// </summary>
    [Fact]
    public void ARotatedRegionClipsToItself()
    {
        var at = Render(
            @"<defs><filter id=""f""><feFlood flood-color=""#0000ff""/></filter></defs>" +
            @"<rect x=""50"" y=""50"" width=""50"" height=""50"" fill=""#ff0000"" transform=""rotate(45 75 75)"" filter=""url(#f)""/>");

        Assert.InRange(at(75, 75).B, 250, 255);
        AssertWhite(at(42, 42));
    }

    /// <summary>An image is filtered as a shape is: a flood in it recolours its pixels.</summary>
    [Fact]
    public void AnImageIsFiltered()
    {
        using var bmp = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(1, 1, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul));
        bmp.SetPixel(0, 0, new SkiaSharp.SKColor(255, 0, 0));
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        string href = "data:image/png;base64," + System.Convert.ToBase64String(data.ToArray());

        var at = Render(
            @"<defs><filter id=""f""><feFlood flood-color=""#0000ff""/><feComposite in2=""SourceGraphic"" operator=""in""/></filter></defs>" +
            $@"<image href=""{href}"" x=""50"" y=""50"" width=""50"" height=""50"" preserveAspectRatio=""none"" filter=""url(#f)""/>");

        Assert.InRange(at(75, 75).B, 250, 255);
        Assert.InRange(at(75, 75).R, 0, 5);
    }

    /// <summary>Every primitive, operator, type and mode applied is taken without a report; the others are reported by name.</summary>
    [Theory]
    [InlineData(@"<feComposite operator=""over""/>", null)]
    [InlineData(@"<feComposite operator=""in""/>", null)]
    [InlineData(@"<feComposite operator=""out""/>", null)]
    [InlineData(@"<feComposite operator=""atop""/>", null)]
    [InlineData(@"<feComposite operator=""xor""/>", null)]
    [InlineData(@"<feComposite operator=""lighter""/>", null)]
    [InlineData(@"<feComposite operator=""arithmetic""/>", null)]
    [InlineData(@"<feComposite operator=""bogus""/>", "<feComposite operator=\"bogus\">")]
    [InlineData(@"<feColorMatrix type=""matrix""/>", null)]
    [InlineData(@"<feColorMatrix type=""saturate""/>", null)]
    [InlineData(@"<feColorMatrix type=""hueRotate""/>", null)]
    [InlineData(@"<feColorMatrix type=""luminanceToAlpha""/>", null)]
    [InlineData(@"<feColorMatrix type=""bogus""/>", "<feColorMatrix type=\"bogus\">")]
    [InlineData(@"<feBlend mode=""normal""/>", null)]
    [InlineData(@"<feBlend mode=""multiply""/>", null)]
    [InlineData(@"<feBlend mode=""screen""/>", null)]
    [InlineData(@"<feBlend mode=""darken""/>", null)]
    [InlineData(@"<feBlend mode=""lighten""/>", null)]
    [InlineData(@"<feBlend mode=""overlay""/><feBlend mode=""color-dodge""/><feBlend mode=""color-burn""/><feBlend mode=""hard-light""/><feBlend mode=""soft-light""/><feBlend mode=""difference""/><feBlend mode=""exclusion""/><feBlend mode=""hue""/><feBlend mode=""saturation""/><feBlend mode=""color""/><feBlend mode=""luminosity""/>", null)]
    [InlineData(@"<feBlend mode=""plus-lighter""/>", "<feBlend mode=\"plus-lighter\">")]
    [InlineData(@"<feGaussianBlur/><feOffset/><feFlood/><feMerge/><desc>a filter</desc><title>t</title><metadata/>", null)]
    [InlineData(@"<feDropShadow/><feComponentTransfer><feFuncR type=""gamma""/></feComponentTransfer>", null)]
    [InlineData(@"<feMorphology/><feMorphology operator=""dilate""/>", null)]
    [InlineData(@"<feMorphology operator=""open""/>", "<feMorphology operator=\"open\">")]
    [InlineData(@"<feConvolveMatrix kernelMatrix=""0 0 0 0 1 0 0 0 0""/>", null)]
    [InlineData(@"<feConvolveMatrix order=""3"" kernelMatrix=""1 2""/>", "2 kernel values")]
    [InlineData(@"<feConvolveMatrix order=""30"" kernelMatrix=""1 2""/>", "1 to 25")]
    [InlineData(@"<feConvolveMatrix order=""1"" kernelMatrix=""1"" targetX=""1""/>", "target off its kernel")]
    [InlineData(@"<feConvolveMatrix order=""1"" kernelMatrix=""1"" edgeMode=""mirror""/>", "<feConvolveMatrix edgeMode=\"mirror\">")]
    [InlineData(@"<feTile/><desc id=""d""/><feImage href=""#d""/>", null)]
    [InlineData(@"<feImage/>", "<feImage> with no href")]
    [InlineData(@"<feTurbulence/><feTurbulence type=""fractalNoise"" stitchTiles=""stitch""/><feDisplacementMap/><feDisplacementMap xChannelSelector=""R"" yChannelSelector=""B""/>", null)]
    [InlineData(@"<feTurbulence type=""bogus""/>", "<feTurbulence type=\"bogus\">")]
    [InlineData(@"<feTurbulence stitchTiles=""maybe""/>", "<feTurbulence stitchTiles=\"maybe\">")]
    [InlineData(@"<feDisplacementMap yChannelSelector=""X""/>", "<feDisplacementMap> with a channel selector")]
    [InlineData(@"<feDiffuseLighting><feDistantLight/></feDiffuseLighting><feSpecularLighting><fePointLight/></feSpecularLighting><feDiffuseLighting><feSpotLight/></feDiffuseLighting>", null)]
    [InlineData(@"<feDiffuseLighting/>", "<feDiffuseLighting> with no light source")]
    [InlineData(@"<feSpecularLighting><desc/></feSpecularLighting>", "<feSpecularLighting> with no light source")]
    [InlineData(@"<feCustom/>", "<feCustom>")]
    public void WhatIsAppliedIsTakenAndTheRestReported(string primitives, string reported)
    {
        Convert($@"<defs><filter id=""f"">{primitives}</filter></defs>" + Square);

        if (reported == null) Assert.Null(SvgToPdfConverter.LastError);
        else Assert.Contains(reported, SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Every node of a merge is an image read, and counts against the pixel limit: four nodes at a
    /// limit of 900 pixels leave 225 an image, 15 a side for the 60-point region.
    /// </summary>
    [Fact]
    public void EachMergeNodeCountsAgainstThePixelLimit()
    {
        try
        {
            PdfLimits.MaxSvgFilterPixels = 900;
            byte[] pdf = Convert(
                @"<defs><filter id=""f""><feMerge><feMergeNode/><feMergeNode/><feMergeNode/><feMergeNode/></feMerge></filter></defs>" + Square);

            Assert.InRange(ImageWidth(pdf), 1, 16);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// A hostile region two hundred thousand points wide is rendered no further from the page than a
    /// page's size: 600 points, 1 200 pixels at two a point, where the whole region would have
    /// driven the resolution below a pixel a point.
    /// </summary>
    [Fact]
    public void AHostileHugeRegionIsKeptNearThePage()
    {
        byte[] pdf = Convert(
            @"<defs><filter id=""f"" filterUnits=""userSpaceOnUse"" x=""-100000"" y=""0"" width=""200000"" height=""200""><feFlood flood-color=""#0000ff""/></filter></defs>" + Square);

        Assert.InRange(ImageWidth(pdf), 1195, 1205);
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertRed((int R, int G, int B) p)
    {
        Assert.InRange(p.R, 250, 255);
        Assert.InRange(p.G, 0, 5);
    }

    private static void AssertWhite((int R, int G, int B) p)
    {
        Assert.InRange(p.R, 250, 255);
        Assert.InRange(p.G, 250, 255);
    }

    private static byte[] Convert(string body)
        => SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });

    private static Func<int, int, (int R, int G, int B)> Render(string body)
    {
        var page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(Convert(body)), 0, 72);
        return (x, y) => { int i = y * page.Stride + x * 4; return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]); };
    }

    /// <summary>The <c>/Width</c> of the first image with a soft mask in the file.</summary>
    private static int ImageWidth(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int at = text.IndexOf("/ColorSpace /DeviceRGB /Width ", StringComparison.Ordinal);
        Assert.True(at >= 0, "no image was written");
        string rest = text.Substring(at + "/ColorSpace /DeviceRGB /Width ".Length);
        return int.Parse(new string(rest.TakeWhile(char.IsAsciiDigit).ToArray()), System.Globalization.CultureInfo.InvariantCulture);
    }
}
