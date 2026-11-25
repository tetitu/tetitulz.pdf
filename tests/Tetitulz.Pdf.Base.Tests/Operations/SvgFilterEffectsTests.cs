// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Base.Operations;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgFilterEffects"/>' primitives on small images, each value worked by hand from Filter
/// Effects 1 §9: premultiplied RGBA, the first row at the top.
/// </summary>
public class SvgFilterEffectsTests
{
    /// <summary>
    /// A deviation of 1 is a box of d = ⌊1·3·√(2π)/4 + 0.5⌋ = 2, even: boxes of 2, 2 and 3. One opaque
    /// pixel in a line of 9 spreads to 1/2·1/2·1/3 each way, 1/12 at ±2 and a total kept at 1.
    /// </summary>
    [Fact]
    public void ABlurOfOneIsTheSpecificationsThreeBoxes()
    {
        var image = Line(9, 4);
        var result = Run(image, new SvgFilterEffects.Blur { StdX = 1, StdY = 0, Linear = false });

        float[] alpha = Alphas(result);
        Assert.Equal(1f, Sum(alpha), 4);
        Assert.Equal(1f / 12, alpha[2], 4);
        Assert.Equal(1f / 12, alpha[6], 4);
        Assert.Equal(0f, alpha[1], 4);
        Assert.Equal(0f, alpha[7], 4);
        Assert.Equal(1f / 3, alpha[4], 4);
    }

    /// <summary>A deviation of 2 is d = 4: its spread reaches ±5 from the pixel and no further.</summary>
    [Fact]
    public void ABlurOfTwoReachesItsBoxes()
    {
        var result = Run(Line(21, 10), new SvgFilterEffects.Blur { StdX = 2, Linear = false });

        float[] alpha = Alphas(result);
        Assert.Equal(1f, Sum(alpha), 4);
        Assert.True(alpha[5] > 0 && alpha[15] > 0, "the spread stops short");
        Assert.Equal(0f, alpha[4], 5);
        Assert.Equal(0f, alpha[16], 5);
    }

    /// <summary>A deviation of zero, or below it, passes its input on (§9.13).</summary>
    [Fact]
    public void AZeroDeviationPassesItsInputOn()
    {
        var result = Run(Line(5, 2), new SvgFilterEffects.Blur { StdX = 0, StdY = -1, Linear = false });

        Assert.Equal(new[] { 0f, 0, 1, 0, 0 }, Alphas(result));
    }

    /// <summary>A blur along x leaves y alone: a column of three, blurred along x only, keeps its rows.</summary>
    [Fact]
    public void ABlurAlongOneAxisLeavesTheOther()
    {
        var image = new SvgFilterEffects.Image(1, 3, linear: false);
        Set(image, 0, 1, 1, 1, 1, 1);
        var result = Run(image, new SvgFilterEffects.Blur { StdX = 0, StdY = 1, Linear = false });
        var across = Run(image, new SvgFilterEffects.Blur { StdX = 1, StdY = 0, Linear = false });

        Assert.True(Alphas(result)[0] > 0, "the vertical blur did not spread");   // the potency control
        Assert.Equal(0f, Alphas(across)[0]);
        Assert.Equal(0f, Alphas(across)[2]);
    }

    [Fact]
    public void AnOffsetMovesByWholePixels()
    {
        var image = new SvgFilterEffects.Image(3, 3, linear: false);
        Set(image, 0, 0, 1, 0, 0, 1);
        var result = Run(image, new SvgFilterEffects.Offset { Dx = 2, Dy = 1, Linear = false });

        Assert.Equal(1f, result.Px[(1 * 3 + 2) * 4]);
        Assert.Equal(0f, result.Px[0]);
    }

    /// <summary>A flood is its colour premultiplied by its opacity, everywhere; in linearRGB the colour is linear.</summary>
    [Fact]
    public void AFloodFillsTheImage()
    {
        var srgb = Run(Pixel(0, 0, 0, 0), new SvgFilterEffects.Flood { R = 0.5f, G = 0, B = 1, A = 0.5f, Linear = false });
        var linear = Run(Pixel(0, 0, 0, 0), new SvgFilterEffects.Flood { R = 0.5f, G = 0, B = 1, A = 0.5f, Linear = true });

        Assert.Equal(new[] { 0.25f, 0, 0.5f, 0.5f }, srgb.Px);
        // Back in sRGB at the end: the same colour, through the conversion and its inverse.
        Assert.Equal(0.25f, linear.Px[0], 3);
        Assert.Equal(0.5f, linear.Px[3], 5);
    }

    /// <summary>Each Porter-Duff operator's weights on one pixel: A half-opaque red, B opaque blue.</summary>
    [Theory]
    [InlineData("over", 0.5f, 0, 0.5f, 1)]
    [InlineData("in", 0.5f, 0, 0, 0.5f)]
    [InlineData("out", 0f, 0, 0, 0)]
    [InlineData("atop", 0.5f, 0, 0.5f, 1)]
    [InlineData("xor", 0f, 0, 0.5f, 0.5f)]
    [InlineData("lighter", 0.5f, 0, 1, 1)]
    public void ACompositesWeightsAreItsOperators(string op, float r, float g, float b, float a)
    {
        var result = Two(Pixel(0.5f, 0, 0, 0.5f), Pixel(0, 0, 1, 1), new SvgFilterEffects.Composite { Operator = op, Linear = false });

        Assert.Equal(new[] { r, g, b, a }, result.Px);
    }

    /// <summary>arithmetic: k1·A·B + k2·A + k3·B + k4, clamped, the colour no more than the alpha.</summary>
    [Fact]
    public void AnArithmeticCompositeIsItsSum()
    {
        var result = Two(Pixel(0.5f, 0, 0, 0.5f), Pixel(0, 0, 1, 1),
            new SvgFilterEffects.Composite { Operator = "arithmetic", K1 = 0, K2 = 1, K3 = 0.5f, K4 = 0.1f, Linear = false });

        Assert.Equal(0.6f, result.Px[0], 5);
        Assert.Equal(0.1f, result.Px[1], 5);
        Assert.Equal(0.6f, result.Px[2], 5);
        Assert.Equal(1f, result.Px[3], 5);
    }

    /// <summary>A merge puts each node over those before it: the second node on top.</summary>
    [Fact]
    public void AMergeStacksItsNodesInOrder()
    {
        var first = new SvgFilterEffects.Flood { R = 1, A = 1, Result = "red", Linear = false };
        var second = new SvgFilterEffects.Flood { B = 1, A = 0.5f, Result = "blue", Linear = false };
        var merge = new SvgFilterEffects.Merge { Linear = false };
        merge.Nodes.Add("red");
        merge.Nodes.Add("blue");

        var result = SvgFilterEffects.Run(new SvgFilterEffects.Primitive[] { first, second, merge }, Pixel(0, 0, 0, 0), _ => { });

        Assert.Equal(new[] { 0.5f, 0, 0.5f, 1 }, result.Px);
    }

    /// <summary>The colour matrix is on straight colour: a matrix moving red into green, on half-opaque red.</summary>
    [Fact]
    public void AColourMatrixIsOnStraightColour()
    {
        float[] m =
        {
            0, 0, 0, 0, 0,
            1, 0, 0, 0, 0,
            0, 0, 0, 0, 0,
            0, 0, 0, 1, 0,
        };
        var result = Run(Pixel(0.5f, 0, 0, 0.5f), new SvgFilterEffects.ColorMatrix { Matrix = m, Linear = false });

        Assert.Equal(new[] { 0f, 0.5f, 0, 0.5f }, result.Px);
    }

    /// <summary>saturate 0 is grey by the BT.709-like weights; hueRotate 180 turns red to cyan's side.</summary>
    [Fact]
    public void SaturateAndHueRotateAreTheirMatrices()
    {
        var grey = Run(Pixel(1, 0, 0, 1), new SvgFilterEffects.ColorMatrix { Matrix = SvgFilterEffects.ColorMatrix.Saturate(0), Linear = false });
        var turned = Run(Pixel(1, 0, 0, 1), new SvgFilterEffects.ColorMatrix { Matrix = SvgFilterEffects.ColorMatrix.HueRotate(180), Linear = false });
        var luminance = Run(Pixel(0, 1, 0, 1), new SvgFilterEffects.ColorMatrix { Matrix = SvgFilterEffects.ColorMatrix.LuminanceToAlpha(), Linear = false });

        Assert.Equal(new[] { 0.213f, 0.213f, 0.213f, 1 }, grey.Px);
        Assert.Equal(0f, turned.Px[0], 3);                  // 0.213 − 0.787 clamped
        Assert.Equal(0.426f, turned.Px[1], 3);              // 0.213 + 0.213
        Assert.Equal(new[] { 0f, 0, 0, 0.7154f }, luminance.Px);
    }

    /// <summary>Each blend mode on half-grey A over opaque B = (1, 0.5, 0).</summary>
    [Theory]
    [InlineData("normal", 0.5f, 0.5f, 0.5f)]
    [InlineData("multiply", 0.5f, 0.25f, 0)]
    [InlineData("screen", 1f, 0.75f, 0.5f)]
    [InlineData("darken", 0.5f, 0.5f, 0)]
    [InlineData("lighten", 1f, 0.5f, 0.5f)]
    public void ABlendIsItsMode(string mode, float r, float g, float b)
    {
        var result = Two(Pixel(0.5f, 0.5f, 0.5f, 1), Pixel(1, 0.5f, 0, 1), new SvgFilterEffects.Blend { Mode = mode, Linear = false });

        Assert.Equal(new[] { r, g, b, 1 }, result.Px);
    }

    /// <summary>
    /// Every mode's B(Cb, Cs) (Compositing and Blending 1 §9), worked by hand: a quarter-grey source
    /// (0.25, 0.25, 0.25) over the backdrop (1, 0.5, 0). The three non-separable modes that take the
    /// backdrop's luminosity 0.595 land on that grey; luminosity takes the source's 0.25 and is
    /// clipped about it.
    /// </summary>
    [Theory]
    [InlineData("overlay", 1f, 0.25f, 0f)]
    [InlineData("color-dodge", 1f, 0.6667f, 0f)]
    [InlineData("color-burn", 1f, 0f, 0f)]
    [InlineData("hard-light", 0.5f, 0.25f, 0f)]
    [InlineData("soft-light", 1f, 0.375f, 0f)]
    [InlineData("difference", 0.75f, 0.25f, 0.25f)]
    [InlineData("exclusion", 0.75f, 0.5f, 0.25f)]
    [InlineData("hue", 0.595f, 0.595f, 0.595f)]
    [InlineData("saturation", 0.595f, 0.595f, 0.595f)]
    [InlineData("color", 0.595f, 0.595f, 0.595f)]
    [InlineData("luminosity", 0.4202f, 0.2101f, 0f)]
    public void TheOtherBlendModesAreTheirFormulas(string mode, float r, float g, float b)
    {
        var result = Two(Pixel(0.25f, 0.25f, 0.25f, 1), Pixel(1, 0.5f, 0, 1), new SvgFilterEffects.Blend { Mode = mode, Linear = false });

        Assert.Equal(r, result.Px[0], 3);
        Assert.Equal(g, result.Px[1], 3);
        Assert.Equal(b, result.Px[2], 3);
        Assert.Equal(1f, result.Px[3], 5);
    }

    /// <summary>Soft-light above a half uses D(Cb): a source of 0.75 over (1, 0.5, 0) is (1, 0.5 + 0.5·(√0.5 − 0.5), 0).</summary>
    [Fact]
    public void SoftLightAboveAHalfUsesD()
    {
        var result = Two(Pixel(0.75f, 0.75f, 0.75f, 1), Pixel(1, 0.5f, 0, 1), new SvgFilterEffects.Blend { Mode = "soft-light", Linear = false });

        Assert.Equal(1f, result.Px[0], 3);
        Assert.Equal(0.6036f, result.Px[1], 3);
        Assert.Equal(0f, result.Px[2], 3);
    }

    /// <summary>
    /// Hue takes the source's hue at the backdrop's saturation and luminosity: (0, 0.5, 1) over
    /// (1, 0.5, 0) keeps its blue-to-green order — SetSat keeps the middle channel in proportion —
    /// at luminosity 0.595, clipped at the top: (0.3193, 0.6597, 1).
    /// </summary>
    [Fact]
    public void HueKeepsTheSourcesChannelOrder()
    {
        var result = Two(Pixel(0, 0.5f, 1, 1), Pixel(1, 0.5f, 0, 1), new SvgFilterEffects.Blend { Mode = "hue", Linear = false });

        Assert.Equal(0.3193f, result.Px[0], 3);
        Assert.Equal(0.6597f, result.Px[1], 3);
        Assert.Equal(1f, result.Px[2], 3);
    }

    /// <summary>
    /// Saturation takes the source's saturation at the backdrop's hue and luminosity — the mode
    /// that <c>color</c> is not. A half-saturated source (0.5, 0.25, 0.25) over (1, 0.5, 0): SetSat
    /// of the backdrop to 0.25 is (0.25, 0.125, 0), luminosity 0.14875, lifted by 0.44625 to the
    /// backdrop's 0.595: (0.696, 0.571, 0.446). Color would put the source's own hue there instead,
    /// the source lifted from its 0.325 by 0.27: (0.77, 0.52, 0.52).
    /// </summary>
    [Fact]
    public void SaturationKeepsTheBackdropsHue()
    {
        var saturation = Two(Pixel(0.5f, 0.25f, 0.25f, 1), Pixel(1, 0.5f, 0, 1), new SvgFilterEffects.Blend { Mode = "saturation", Linear = false });
        var color = Two(Pixel(0.5f, 0.25f, 0.25f, 1), Pixel(1, 0.5f, 0, 1), new SvgFilterEffects.Blend { Mode = "color", Linear = false });

        Assert.Equal(0.6963f, saturation.Px[0], 3);
        Assert.Equal(0.5713f, saturation.Px[1], 3);
        Assert.Equal(0.4463f, saturation.Px[2], 3);
        Assert.Equal(0.77f, color.Px[0], 3);
        Assert.Equal(0.52f, color.Px[1], 3);
        Assert.Equal(0.52f, color.Px[2], 3);
    }

    /// <summary>A half-transparent top blends only as far as it covers: normal over opaque B.</summary>
    [Fact]
    public void ABlendWeighsByTheAlphas()
    {
        var result = Two(Pixel(0.5f, 0, 0, 0.5f), Pixel(0, 0, 1, 1), new SvgFilterEffects.Blend { Mode = "multiply", Linear = false });

        // A(1 − αB) + B(1 − αA) + αA·αB·(a·b): red 0 + 0 + 0.5·(1·0) = 0; blue 0 + 0.5 + 0.
        Assert.Equal(new[] { 0f, 0, 0.5f, 1 }, result.Px);
    }

    /// <summary>
    /// The default input is the previous result, and a name no primitive gave falls back to it too
    /// (§9.2). SourceAlpha is black with the source's alpha.
    /// </summary>
    [Fact]
    public void InputsResolveAsTheSpecificationSays()
    {
        var source = Pixel(1, 0, 0, 1);
        var flood = new SvgFilterEffects.Flood { B = 1, A = 1, Linear = false };
        var fromUnknown = new SvgFilterEffects.Offset { In = "nothing-by-this-name", Linear = false };
        var result = SvgFilterEffects.Run(new SvgFilterEffects.Primitive[] { flood, fromUnknown }, source, _ => { });
        var alpha = SvgFilterEffects.Run(new SvgFilterEffects.Primitive[] { new SvgFilterEffects.Offset { In = "SourceAlpha", Linear = false } }, source, _ => { });

        Assert.Equal(new[] { 0f, 0, 1, 1 }, result.Px);     // the flood's, not the source's
        Assert.Equal(new[] { 0f, 0, 0, 1 }, alpha.Px);
    }

    /// <summary>An input this does not model is transparent black, and said so.</summary>
    [Fact]
    public void AnUnmodelledInputIsTransparentAndReported()
    {
        var said = new List<string>();
        var result = SvgFilterEffects.Run(
            new SvgFilterEffects.Primitive[] { new SvgFilterEffects.Offset { In = "BackgroundImage", Linear = false } }, Pixel(1, 0, 0, 1), said.Add);

        Assert.Equal(new[] { 0f, 0, 0, 0 }, result.Px);
        Assert.Equal(new[] { "BackgroundImage" }, said);
    }

    /// <summary>A subregion clips the primitive's result: outside it, transparent.</summary>
    [Fact]
    public void ASubregionClipsTheResult()
    {
        var result = Run(new SvgFilterEffects.Image(4, 1, linear: false),
            new SvgFilterEffects.Flood { R = 1, A = 1, Subregion = new[] { 1, 0, 3, 1 }, Linear = false });

        Assert.Equal(new[] { 0f, 1, 1, 0 }, Alphas(result));
    }

    /// <summary>
    /// Linear light and back: sRGB 0.5 is 0.214 linear, and a primitive in linearRGB returns sRGB. A
    /// saturate-0 of red is 0.213 linear, √-ish 0.5 in sRGB: the space is visible in the result.
    /// </summary>
    [Fact]
    public void TheColourSpaceIsThePrimitives()
    {
        Assert.Equal(0.2140f, SvgFilterEffects.ToLinear(0.5f), 3);
        Assert.Equal(0.5f, SvgFilterEffects.ToSrgb(SvgFilterEffects.ToLinear(0.5f)), 4);

        var linear = Run(Pixel(1, 0, 0, 1), new SvgFilterEffects.ColorMatrix { Matrix = SvgFilterEffects.ColorMatrix.Saturate(0), Linear = true });
        var srgb = Run(Pixel(1, 0, 0, 1), new SvgFilterEffects.ColorMatrix { Matrix = SvgFilterEffects.ColorMatrix.Saturate(0), Linear = false });

        Assert.Equal(SvgFilterEffects.ToSrgb(0.213f), linear.Px[0], 3);
        Assert.Equal(0.213f, srgb.Px[0], 3);
    }

    /// <summary>A drop shadow is the source's alpha moved and flooded, the source over it: red at 1, blue at 3.</summary>
    [Fact]
    public void ADropShadowFallsBesideItsSource()
    {
        var image = new SvgFilterEffects.Image(5, 1, linear: false);
        Set(image, 1, 0, 1, 0, 0, 1);
        var result = Run(image, new SvgFilterEffects.DropShadow { Dx = 2, B = 1, A = 1, Linear = false });

        Assert.Equal(new[] { 1f, 0, 0, 1 }, result.Px.Skip(4).Take(4));
        Assert.Equal(new[] { 0f, 0, 1, 1 }, result.Px.Skip(12).Take(4));
        Assert.Equal(new[] { 0f, 1, 0, 1, 0 }, Alphas(result));
    }

    /// <summary>
    /// Under a half-transparent source the shadow shows through: shadow alpha 0.5 × flood 0.5 = 0.25,
    /// kept at 1 − 0.5 — blue 0.125, alpha 0.5 + 0.125.
    /// </summary>
    [Fact]
    public void ADropShadowShowsThroughItsSource()
    {
        var result = Run(Pixel(0.5f, 0, 0, 0.5f), new SvgFilterEffects.DropShadow { B = 1, A = 0.5f, Linear = false });

        Assert.Equal(0.5f, result.Px[0], 5);
        Assert.Equal(0.125f, result.Px[2], 5);
        Assert.Equal(0.625f, result.Px[3], 5);
    }

    /// <summary>A drop shadow's blur is the blur's: moved clear of its source, its alpha sums to 1 over ±2.</summary>
    [Fact]
    public void ADropShadowIsBlurred()
    {
        var result = Run(Line(12, 2), new SvgFilterEffects.DropShadow { StdX = 1, Dx = 6, A = 1, Linear = false });

        float[] alpha = Alphas(result);
        Assert.Equal(1f, alpha.Skip(6).Take(5).Sum(), 4);
        Assert.Equal(1f / 12, alpha[6], 4);
        Assert.Equal(1f / 3, alpha[8], 4);
    }

    /// <summary>Each transfer function type on hand-worked values (§9.8).</summary>
    [Theory]
    [InlineData("table", new[] { 0f, 1, 0 }, 0.25f, 0.5f)]
    [InlineData("table", new[] { 0f, 1, 0 }, 0.75f, 0.5f)]
    [InlineData("table", new[] { 0f, 1, 0 }, 1f, 0f)]
    [InlineData("table", new[] { 0.3f }, 0.9f, 0.3f)]
    [InlineData("discrete", new[] { 0.2f, 0.8f }, 0.4f, 0.2f)]
    [InlineData("discrete", new[] { 0.2f, 0.8f }, 0.6f, 0.8f)]
    [InlineData("discrete", new[] { 0.2f, 0.8f }, 1f, 0.8f)]
    [InlineData("table", new float[0], 0.4f, 0.4f)]
    [InlineData("identity", new float[0], 0.4f, 0.4f)]
    public void ATableFunctionIsItsType(string type, float[] table, float c, float expected)
    {
        var fn = new SvgFilterEffects.TransferFunction { Type = type, Table = table };

        Assert.Equal(expected, fn.Apply(c), 5);
    }

    [Fact]
    public void LinearAndGammaAreTheirFormulas()
    {
        Assert.Equal(0.5f, new SvgFilterEffects.TransferFunction { Type = "linear", Slope = 0.5f, Intercept = 0.25f }.Apply(0.5f), 5);
        Assert.Equal(0.6f, new SvgFilterEffects.TransferFunction { Type = "gamma", Amplitude = 2, Exponent = 2, Offset = 0.1f }.Apply(0.5f), 5);
    }

    /// <summary>
    /// A component transfer maps straight colour and alpha: half red, its red set to 1 and its green to
    /// 0.5, stays half opaque — (0.5, 0.25, 0, 0.5) premultiplied; doubling the alpha makes it opaque.
    /// </summary>
    [Fact]
    public void AComponentTransferMapsStraightChannels()
    {
        var transfer = new SvgFilterEffects.ComponentTransfer { Linear = false };
        transfer.Functions[0] = new SvgFilterEffects.TransferFunction { Type = "linear", Slope = 0, Intercept = 1 };
        transfer.Functions[1] = new SvgFilterEffects.TransferFunction { Type = "linear", Slope = 0, Intercept = 0.5f };
        var kept = Run(Pixel(0.5f, 0, 0, 0.5f), transfer);
        transfer.Functions[3] = new SvgFilterEffects.TransferFunction { Type = "linear", Slope = 2 };
        var opaque = Run(Pixel(0.5f, 0, 0, 0.5f), transfer);

        Assert.Equal(new[] { 0.5f, 0.25f, 0, 0.5f }, kept.Px);
        Assert.Equal(new[] { 1f, 0.5f, 0, 1 }, opaque.Px);
    }

    /// <summary>
    /// A channel with no function keeps its straight value when the alpha changes: half red, its alpha
    /// doubled, is opaque red (1, 0, 0, 1), not the premultiplied half kept as though it were straight.
    /// </summary>
    [Fact]
    public void AnUnmappedChannelKeepsItsStraightValue()
    {
        var transfer = new SvgFilterEffects.ComponentTransfer { Linear = false };
        transfer.Functions[3] = new SvgFilterEffects.TransferFunction { Type = "linear", Slope = 2 };

        Assert.Equal(new[] { 1f, 0, 0, 1 }, Run(Pixel(0.5f, 0, 0, 0.5f), transfer).Px);
    }

    /// <summary>A dilate of radius 1 grows one pixel to three along x; a radius of 0 on y leaves y alone.</summary>
    [Fact]
    public void ADilateGrowsByItsRadius()
    {
        var result = Run(Line(7, 3), new SvgFilterEffects.Morphology { Dilate = true, RadiusX = 1, RadiusY = 0, Linear = false });

        Assert.Equal(new[] { 0f, 0, 1, 1, 1, 0, 0 }, Alphas(result));
    }

    /// <summary>An erode of radius 1 shrinks a bar of three to one, and eats a bar that reaches the edge: outside is transparent.</summary>
    [Fact]
    public void AnErodeShrinksAndEatsTheEdge()
    {
        var bar = new SvgFilterEffects.Image(7, 1, linear: false);
        for (int x = 2; x <= 4; x++) Set(bar, x, 0, 1, 1, 1, 1);
        var full = new SvgFilterEffects.Image(5, 1, linear: false);
        for (int x = 0; x < 5; x++) Set(full, x, 0, 1, 1, 1, 1);
        var erode = new SvgFilterEffects.Morphology { RadiusX = 1, RadiusY = 0, Linear = false };

        Assert.Equal(new[] { 0f, 0, 0, 1, 0, 0, 0 }, Alphas(Run(bar, erode)));
        Assert.Equal(new[] { 0f, 1, 1, 1, 0 }, Alphas(Run(full, erode)));
    }

    /// <summary>A dilate is two-dimensional: a centre pixel with radius 1 each way fills a 3 × 3.</summary>
    [Fact]
    public void ADilateFillsBothAxes()
    {
        var image = new SvgFilterEffects.Image(3, 3, linear: false);
        Set(image, 1, 1, 1, 0, 0, 1);
        var result = Run(image, new SvgFilterEffects.Morphology { Dilate = true, RadiusX = 1, RadiusY = 1, Linear = false });

        Assert.All(Alphas(result), a => Assert.Equal(1f, a));
        Assert.Equal(1f, result.Px[0]);   // red, the colour carried
    }

    /// <summary>A zero radius passes the input on, erode and dilate alike.</summary>
    [Fact]
    public void AZeroRadiusPassesTheInputOn()
    {
        Assert.Equal(new[] { 0f, 0, 1, 0, 0 }, Alphas(Run(Line(5, 2), new SvgFilterEffects.Morphology { Linear = false })));
        Assert.Equal(new[] { 0f, 0, 1, 0, 0 }, Alphas(Run(Line(5, 2), new SvgFilterEffects.Morphology { Dilate = true, Linear = false })));
    }

    /// <summary>
    /// A convolution's kernel is turned 180° about the target (§9.11): a 1 at the kernel's right reads
    /// the pixel to the left, so the image moves right; the identity kernel keeps it.
    /// </summary>
    [Fact]
    public void AConvolutionsKernelIsTurned()
    {
        var shift = new SvgFilterEffects.ConvolveMatrix { OrderX = 3, OrderY = 3, TargetX = 1, TargetY = 1, Kernel = new float[] { 0, 0, 0, 0, 0, 1, 0, 0, 0 }, EdgeMode = "none", Linear = false };
        var identity = new SvgFilterEffects.ConvolveMatrix { OrderX = 3, OrderY = 3, TargetX = 1, TargetY = 1, Kernel = new float[] { 0, 0, 0, 0, 1, 0, 0, 0, 0 }, Linear = false };

        Assert.Equal(new[] { 0f, 0, 1, 0, 0 }, Alphas(Run(Line(5, 1), shift)));
        Assert.Equal(new[] { 0f, 1, 0, 0, 0 }, Alphas(Run(Line(5, 1), identity)));
    }

    /// <summary>The divisor scales the sum and the bias adds a share of the pixel's own alpha: a 3-box over one pixel.</summary>
    [Fact]
    public void TheDivisorAndBiasApply()
    {
        var box = new SvgFilterEffects.ConvolveMatrix { OrderX = 3, OrderY = 1, TargetX = 1, TargetY = 0, Kernel = new float[] { 1, 1, 1 }, Divisor = 3, Linear = false };
        var biased = new SvgFilterEffects.ConvolveMatrix { OrderX = 3, OrderY = 1, TargetX = 1, TargetY = 0, Kernel = new float[] { 1, 1, 1 }, Divisor = 3, Bias = 0.5f, Linear = false };

        float[] plain = Alphas(Run(Line(3, 1), box)), withBias = Alphas(Run(Line(3, 1), biased));
        Assert.Equal(1f / 3, plain[0], 4);
        Assert.Equal(1f / 3, plain[1], 4);
        Assert.Equal(1f / 3, withBias[0], 4);   // alpha 0 here: no bias
        Assert.Equal(1f / 3 + 0.5f, withBias[1], 4);
    }

    /// <summary>
    /// Past the edge: <c>none</c> reads nothing, <c>wrap</c> reads round to the first pixel,
    /// <c>duplicate</c> the edge pixel itself. The last pixel reads past the edge; with the one
    /// opaque pixel at the start it tells wrap apart, with it at the end, duplicate.
    /// </summary>
    [Theory]
    [InlineData("none", 0f, 0f)]
    [InlineData("wrap", 1f, 0f)]
    [InlineData("duplicate", 0f, 1f)]
    public void TheEdgeModeSaysWhatIsPastTheEdge(string mode, float withStartPixel, float withEndPixel)
    {
        // A 1 at the kernel's left reads the pixel to the right: the last pixel reads past the edge.
        var left = new SvgFilterEffects.ConvolveMatrix { OrderX = 3, OrderY = 1, TargetX = 1, TargetY = 0, Kernel = new float[] { 1, 0, 0 }, EdgeMode = mode, Linear = false };

        Assert.Equal(withStartPixel, Alphas(Run(Line(3, 0), left))[2]);
        Assert.Equal(withEndPixel, Alphas(Run(Line(3, 2), left))[2]);
    }

    /// <summary>
    /// preserveAlpha keeps the alpha and convolves straight colour: half red doubled is clamped red at
    /// alpha 0.5; without it the premultiplied colour and alpha are both doubled, to opaque red. The
    /// straight colour goes back under the alpha: a half-red at alpha 0.5, red 0.5 straight, stays
    /// (0.25, 0, 0, 0.5) through the identity — not 0.5, the alpha it would be capped at.
    /// </summary>
    [Fact]
    public void PreserveAlphaKeepsTheAlpha()
    {
        var kept = new SvgFilterEffects.ConvolveMatrix { OrderX = 1, OrderY = 1, Kernel = new float[] { 2 }, PreserveAlpha = true, Linear = false };
        var all = new SvgFilterEffects.ConvolveMatrix { OrderX = 1, OrderY = 1, Kernel = new float[] { 2 }, Linear = false };
        var identity = new SvgFilterEffects.ConvolveMatrix { OrderX = 1, OrderY = 1, Kernel = new float[] { 1 }, PreserveAlpha = true, Linear = false };

        Assert.Equal(new[] { 0.5f, 0, 0, 0.5f }, Run(Pixel(0.5f, 0, 0, 0.5f), kept).Px);
        Assert.Equal(new[] { 1f, 0, 0, 1 }, Run(Pixel(0.5f, 0, 0, 0.5f), all).Px);
        Assert.Equal(new[] { 0.25f, 0, 0, 0.5f }, Run(Pixel(0.25f, 0, 0, 0.5f), identity).Px);
    }

    /// <summary>
    /// A tile repeats its input's subregion across the image from where it is: pixels 1 and 2 of a
    /// line of four, red and green, tile to green, red, green, red. An input with no subregion
    /// tiles as itself.
    /// </summary>
    [Fact]
    public void ATileRepeatsTheInputsSubregion()
    {
        var image = new SvgFilterEffects.Image(4, 1, linear: false);
        Set(image, 1, 0, 1, 0, 0, 1);
        Set(image, 2, 0, 0, 1, 0, 1);
        var clipped = new SvgFilterEffects.Offset { Subregion = new[] { 1, 0, 3, 1 }, Result = "t", Linear = false };
        var tile = new SvgFilterEffects.Tile { In = "t", Linear = false };
        var whole = new SvgFilterEffects.Tile { Linear = false };

        float[] px = SvgFilterEffects.Run(new SvgFilterEffects.Primitive[] { clipped, tile }, image, _ => { }).Px;
        Assert.Equal(new[] { 0f, 1, 0, 1 }, px.Take(4));          // x 0 reads x 2: green
        Assert.Equal(new[] { 1f, 0, 0, 1 }, px.Skip(4).Take(4));  // x 1: red
        Assert.Equal(new[] { 0f, 1, 0, 1 }, px.Skip(8).Take(4));
        Assert.Equal(new[] { 1f, 0, 0, 1 }, px.Skip(12).Take(4)); // x 3 reads x 1: red
        Assert.Equal(image.Px, Run(image, whole).Px);
    }

    /// <summary>The rows tile from the subregion's own place too: rows 1 and 2 of a column of four, red and green, tile to green, red, green, red.</summary>
    [Fact]
    public void ATileRepeatsTheRowsFromTheSubregion()
    {
        var image = new SvgFilterEffects.Image(1, 4, linear: false);
        Set(image, 0, 1, 1, 0, 0, 1);
        Set(image, 0, 2, 0, 1, 0, 1);
        var clipped = new SvgFilterEffects.Offset { Subregion = new[] { 0, 1, 1, 3 }, Result = "t", Linear = false };
        var tile = new SvgFilterEffects.Tile { In = "t", Linear = false };

        float[] px = SvgFilterEffects.Run(new SvgFilterEffects.Primitive[] { clipped, tile }, image, _ => { }).Px;
        Assert.Equal(new[] { 0f, 1, 0, 1 }, px.Take(4));           // row 0 reads row 2: green
        Assert.Equal(new[] { 1f, 0, 0, 1 }, px.Skip(12).Take(4));  // row 3 reads row 1: red
    }

    /// <summary>A picture is its rendered image, whatever the input; none is transparent.</summary>
    [Fact]
    public void APictureIsItsRenderedImage()
    {
        var rendered = Pixel(0, 0, 1, 1);
        Assert.Equal(new[] { 0f, 0, 1, 1 }, Run(Pixel(1, 0, 0, 1), new SvgFilterEffects.Picture { Rendered = rendered, Linear = false }).Px);
        Assert.Equal(new[] { 0f, 0, 0, 0 }, Run(Pixel(1, 0, 0, 1), new SvgFilterEffects.Picture { Linear = false }).Px);
    }

    /// <summary>
    /// The noise is the specification's reference algorithm: one pixel's four channels agree, to four
    /// places, with an independent port of the reference C (Python) at the same seed,
    /// frequencies, octaves and point — a pixel's centre, moved into user space by <c>ToUser</c>.
    /// </summary>
    [Theory]
    [InlineData(0, 0.05f, 0.05f, 1, true, 10, 20, 0.4562f, 0.4498f, 0.4262f, 0.3115f)]
    [InlineData(0, 0.05f, 0.05f, 1, false, 10, 20, 0.0876f, 0.1005f, 0.1477f, 0.3769f)]
    [InlineData(7, 0.1f, 0.02f, 3, true, 33, 3, 0.6432f, 0.4409f, 0.5380f, 0.4964f)]
    [InlineData(-5, 0.2f, 0.2f, 4, false, 3, 3, 0.2685f, 0.2950f, 0.4276f, 0.4543f)]
    [InlineData(0, 1f, 1f, 1, true, 5, 250, 0.3775f, 0.4097f, 0.6768f, 0.4437f)]  // y 250 reads the lattice's doubled half
    public void ATurbulenceIsTheReferenceNoise(long seed, float fx, float fy, int octaves, bool fractal, double px, double py, float r, float g, float b, float a)
    {
        var noise = new SvgFilterEffects.Turbulence
        {
            Seed = seed, BaseFreqX = fx, BaseFreqY = fy, Octaves = octaves, FractalNoise = fractal,
            ToUser = new double[] { 1, 0, 0, 1, px, py }, Linear = false,
        };

        float[] result = Run(Pixel(0, 0, 0, 0), noise).Px;
        Assert.Equal(a, result[3], 4);
        Assert.Equal(r, result[0] / result[3], 4);
        Assert.Equal(g, result[1] / result[3], 4);
        Assert.Equal(b, result[2] / result[3], 4);
    }

    /// <summary>No octaves is no noise: fractal noise's half, turbulence's zero. A frequency of 0 is the lattice origin, noise 0 too.</summary>
    [Fact]
    public void NoOctavesOrNoFrequencyIsNoNoise()
    {
        Assert.Equal(new[] { 0.25f, 0.25f, 0.25f, 0.5f },
            Run(Pixel(0, 0, 0, 0), new SvgFilterEffects.Turbulence { Octaves = 0, FractalNoise = true, Linear = false }).Px);
        Assert.Equal(new[] { 0f, 0, 0, 0 },
            Run(Pixel(0, 0, 0, 0), new SvgFilterEffects.Turbulence { Octaves = 0, Linear = false }).Px);
        Assert.Equal(new[] { 0.25f, 0.25f, 0.25f, 0.5f },
            Run(Pixel(0, 0, 0, 0), new SvgFilterEffects.Turbulence { BaseFreqX = 0, BaseFreqY = 0, Octaves = 3, FractalNoise = true, Linear = false }).Px);
    }

    /// <summary>
    /// Stitched, the noise repeats with the tile: a tile 32 wide at a frequency of two cells a tile
    /// gives the same pixel at x 0.5 and x 32.5 (0.4394, 0.6737, 0.5463, 0.3585 by the reference);
    /// unstitched, x 32.5 is another point of the lattice (0.5700, 0.3259, 0.4613, 0.5293).
    /// </summary>
    [Fact]
    public void StitchedTilesMeetAtTheirEdge()
    {
        SvgFilterEffects.Turbulence Noise(bool stitch) => new SvgFilterEffects.Turbulence
        {
            Seed = 3, BaseFreqX = 1 / 16f, BaseFreqY = 1 / 16f, Octaves = 2, FractalNoise = true, Stitch = stitch,
            TileX = 0, TileY = 0, TileWidth = 32, TileHeight = 32, ToUser = new double[] { 1, 0, 0, 1, 0, 4 }, Linear = false,
        };
        var blank = new SvgFilterEffects.Image(33, 1, linear: false);

        float[] stitched = Run(blank, Noise(true)).Px;
        float[] loose = Run(blank, Noise(false)).Px;
        float[] Straight(float[] px, int x) => new[] { px[x * 4] / px[x * 4 + 3], px[x * 4 + 1] / px[x * 4 + 3], px[x * 4 + 2] / px[x * 4 + 3], px[x * 4 + 3] };
        Assert.Equal(new[] { 0.4394f, 0.6737f, 0.5463f, 0.3585f }, Straight(stitched, 0).Select(v => (float)Math.Round(v, 4)));
        Assert.Equal(Straight(stitched, 0), Straight(stitched, 32));
        Assert.Equal(Straight(stitched, 0), Straight(loose, 0));
        Assert.Equal(new[] { 0.5700f, 0.3259f, 0.4613f, 0.5293f }, Straight(loose, 32).Select(v => (float)Math.Round(v, 4)));
        // x 20.5's second octave sits between the first octave's wrap point and the doubled one:
        // only the wrap point moved on with the octave gives the reference's value.
        Assert.Equal(new[] { 0.4070f, 0.3447f, 0.5852f, 0.4772f }, Straight(stitched, 20).Select(v => (float)Math.Round(v, 4)));
    }

    /// <summary>
    /// A frequency at which the tile is not a whole number of cells — 0.1 over 32 is 3.2 — is moved to
    /// the nearer whole one, 3/32: the stitched noise at 0.1 is the reference's at 3/32 (0.4508,
    /// 0.6803, 0.5078, 0.3677) and x 0.5 and 32.5 agree; unstitched at 0.1 they do not (32.5 is
    /// 0.4539, 0.4655, 0.7522, 0.5357).
    /// </summary>
    [Fact]
    public void AStitchedFrequencyIsMovedToWholeCells()
    {
        SvgFilterEffects.Turbulence Noise(bool stitch) => new SvgFilterEffects.Turbulence
        {
            Seed = 3, BaseFreqX = 0.1f, BaseFreqY = 0.1f, Octaves = 2, FractalNoise = true, Stitch = stitch,
            TileX = 0, TileY = 0, TileWidth = 32, TileHeight = 32, ToUser = new double[] { 1, 0, 0, 1, 0, 4 }, Linear = false,
        };
        var blank = new SvgFilterEffects.Image(33, 1, linear: false);

        float[] stitched = Run(blank, Noise(true)).Px;
        float[] loose = Run(blank, Noise(false)).Px;
        float[] Straight(float[] px, int x) => new[] { px[x * 4] / px[x * 4 + 3], px[x * 4 + 1] / px[x * 4 + 3], px[x * 4 + 2] / px[x * 4 + 3], px[x * 4 + 3] };
        Assert.Equal(new[] { 0.4508f, 0.6803f, 0.5078f, 0.3677f }, Straight(stitched, 0).Select(v => (float)Math.Round(v, 4)));
        Assert.Equal(Straight(stitched, 0), Straight(stitched, 32));
        Assert.Equal(new[] { 0.4539f, 0.4655f, 0.7522f, 0.5357f }, Straight(loose, 32).Select(v => (float)Math.Round(v, 4)));
    }

    /// <summary>A hostile frequency, on either axis or both, puts the lattice coordinate past an int: noise 0, half grey, not undefined.</summary>
    [Theory]
    [InlineData(1e30f, 1e30f)]
    [InlineData(1e30f, 0.05f)]
    [InlineData(0.05f, 1e30f)]
    public void AHostileFrequencyPastTheLatticeIsNoiseZero(float fx, float fy)
    {
        var noise = new SvgFilterEffects.Turbulence { BaseFreqX = fx, BaseFreqY = fy, FractalNoise = true, Linear = false };

        Assert.Equal(new[] { 0.25f, 0.25f, 0.25f, 0.5f }, Run(Pixel(0, 0, 0, 0), noise).Px);
    }

    /// <summary>
    /// A displacement reads the source from where the map sends it: two pixels per unit of R − ½ along
    /// x, a red map (R 1) reads one to the right — green, blue, then nothing; the G channel (0) reads
    /// one to the left — nothing, red, green; the A channel of a half-transparent map reads in place.
    /// </summary>
    [Theory]
    [InlineData(0, 1f, 1, 2, -1)]
    [InlineData(1, 1f, -1, 0, 1)]
    [InlineData(3, 0.5f, 0, 1, 2)]
    public void ADisplacementReadsWhereTheMapSends(int channel, float mapAlpha, int at0, int at1, int at2)
    {
        var source = new SvgFilterEffects.Image(3, 1, linear: false);
        Set(source, 0, 0, 1, 0, 0, 1);
        Set(source, 1, 0, 0, 1, 0, 1);
        Set(source, 2, 0, 0, 0, 1, 1);
        var map = new SvgFilterEffects.Flood { R = 1, G = 0, B = 0, A = mapAlpha, Result = "m", Linear = false };
        var displace = new SvgFilterEffects.DisplacementMap { In = "SourceGraphic", In2 = "m", XDx = 2, XChannel = channel, Linear = false };

        float[] px = SvgFilterEffects.Run(new SvgFilterEffects.Primitive[] { map, displace }, source, _ => { }).Px;
        int[] reads = { at0, at1, at2 };
        for (int x = 0; x < 3; x++)
        {
            float[] expected = reads[x] < 0 ? new float[4] : source.Px.Skip(reads[x] * 4).Take(4).ToArray();
            Assert.Equal(expected, px.Skip(x * 4).Take(4));
        }
    }

    /// <summary>
    /// The map is read straight: a red flood at alpha ½ is R 1, not the premultiplied ½ — the
    /// displacement is a full pixel, not none.
    /// </summary>
    [Fact]
    public void ADisplacementMapIsReadStraight()
    {
        var source = new SvgFilterEffects.Image(2, 1, linear: false);
        Set(source, 0, 0, 1, 0, 0, 1);
        Set(source, 1, 0, 0, 1, 0, 1);
        var map = new SvgFilterEffects.Flood { R = 1, A = 0.5f, Result = "m", Linear = false };
        var displace = new SvgFilterEffects.DisplacementMap { In = "SourceGraphic", In2 = "m", XDx = 2, XChannel = 0, Linear = false };

        float[] px = SvgFilterEffects.Run(new SvgFilterEffects.Primitive[] { map, displace }, source, _ => { }).Px;
        Assert.Equal(new[] { 0f, 1, 0, 1 }, px.Take(4));
    }

    /// <summary>
    /// Each map channel moves along both pixel axes as the converter maps it — a rotated element's x is
    /// a pixel y: two per unit of R − ½ along y reads the row below; and YC reads by its own vector.
    /// </summary>
    [Fact]
    public void ADisplacementFollowsBothAxesOfEachChannel()
    {
        var source = new SvgFilterEffects.Image(1, 2, linear: false);
        Set(source, 0, 0, 1, 0, 0, 1);
        Set(source, 0, 1, 0, 1, 0, 1);
        var map = new SvgFilterEffects.Flood { R = 1, G = 0, A = 1, Result = "m", Linear = false };
        var byX = new SvgFilterEffects.DisplacementMap { In = "SourceGraphic", In2 = "m", XDy = 2, XChannel = 0, YChannel = 1, Linear = false };
        var byY = new SvgFilterEffects.DisplacementMap { In = "SourceGraphic", In2 = "m", YDy = -2, XChannel = 0, YChannel = 1, Linear = false };

        Assert.Equal(new[] { 0f, 1, 0, 1 }, SvgFilterEffects.Run(new SvgFilterEffects.Primitive[] { map, byX }, source, _ => { }).Px.Take(4));
        Assert.Equal(new[] { 0f, 1, 0, 1 }, SvgFilterEffects.Run(new SvgFilterEffects.Primitive[] { map, byY }, source, _ => { }).Px.Take(4));
    }

    /// <summary>A hostile displacement — infinite, or not a number — reads nothing: transparent, no exception.</summary>
    [Theory]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NaN)]
    public void AHostileInfiniteDisplacementIsTransparent(float scale)
    {
        var map = new SvgFilterEffects.Flood { R = 1, A = 1, Result = "m", Linear = false };
        var displace = new SvgFilterEffects.DisplacementMap { In = "SourceGraphic", In2 = "m", XDx = scale, XChannel = 0, Linear = false };

        float[] px = SvgFilterEffects.Run(new SvgFilterEffects.Primitive[] { map, displace }, Pixel(1, 0, 0, 1), _ => { }).Px;
        Assert.Equal(new[] { 0f, 0, 0, 0 }, px);
    }

    /// <summary>A flat surface under a distant light: N·L is sin(elevation), times kd; opaque, the lighting colour.</summary>
    [Theory]
    [InlineData(90f, 1f, 1f)]
    [InlineData(30f, 1f, 0.5f)]
    [InlineData(0f, 1f, 0f)]
    [InlineData(30f, 2f, 1f)]
    public void ADiffuseLightOnAFlatSurfaceIsTheElevationsSine(float elevation, float kd, float expected)
    {
        var flat = new SvgFilterEffects.Image(2, 2, linear: false);
        var light = new SvgFilterEffects.Lighting { Elevation = elevation, Azimuth = 45, Constant = kd, R = 1, G = 0.5f, B = 0, Linear = false };

        float[] px = Run(flat, light).Px;
        Assert.Equal(expected, px[0], 4);
        Assert.Equal(expected * 0.5f, px[1], 4);
        Assert.Equal(0f, px[2], 4);
        Assert.Equal(1f, px[3]);
    }

    /// <summary>
    /// The normal is the specification's kernels with its factors: a 3 × 3 surface stepping up from
    /// its left column (alpha 0, 1, 1), lit from the left along the plane (azimuth 180), has Nx −2
    /// at its left edge (½ × 4) and corners (⅔ × 3), −1 in the interior (¼ × 4) and top row (⅓ × 3),
    /// 0 at the right: N·L is 2/√5, 1/√2, 0. Lit from the right (azimuth 0) it is in shadow.
    /// </summary>
    [Theory]
    [InlineData(0, 1, 0.8944f)]  // left column
    [InlineData(0, 0, 0.8944f)]  // top-left corner
    [InlineData(1, 1, 0.7071f)]  // interior
    [InlineData(1, 0, 0.7071f)]  // top row
    [InlineData(2, 1, 0f)]       // right column: flat
    public void TheNormalTakesTheSpecificationsFactorsAcrossAStep(int x, int y, float expected)
    {
        var step = new SvgFilterEffects.Image(3, 3, linear: false);
        for (int row = 0; row < 3; row++) { Set(step, 1, row, 0, 0, 0, 1); Set(step, 2, row, 0, 0, 0, 1); }
        var fromLeft = new SvgFilterEffects.Lighting { Azimuth = 180, Elevation = 0, Linear = false };
        var fromRight = new SvgFilterEffects.Lighting { Azimuth = 0, Elevation = 0, Linear = false };

        Assert.Equal(expected, Run(step, fromLeft).Px[(y * 3 + x) * 4], 4);
        Assert.Equal(0f, Run(step, fromRight).Px[(y * 3 + x) * 4], 4);
    }

    /// <summary>The same step down the rows (alpha 0, 1, 1 by row), lit from above the image (azimuth 270): the row factors — ½ across the top row, ⅓ along the left column.</summary>
    [Theory]
    [InlineData(1, 0, 0.8944f)]  // top row
    [InlineData(0, 0, 0.8944f)]  // top-left corner
    [InlineData(1, 1, 0.7071f)]  // interior
    [InlineData(0, 1, 0.7071f)]  // left column
    [InlineData(1, 2, 0f)]       // bottom row: flat
    public void TheNormalTakesTheSpecificationsFactorsDownAStep(int x, int y, float expected)
    {
        var step = new SvgFilterEffects.Image(3, 3, linear: false);
        for (int col = 0; col < 3; col++) { Set(step, col, 1, 0, 0, 0, 1); Set(step, col, 2, 0, 0, 0, 1); }
        var fromAbove = new SvgFilterEffects.Lighting { Azimuth = 270, Elevation = 0, Linear = false };

        Assert.Equal(expected, Run(step, fromAbove).Px[(y * 3 + x) * 4], 4);
    }

    /// <summary>
    /// A point light's vector runs from the surface to it: over the first pixel's centre at height 1,
    /// that pixel is lit full, the next at cos 45°; a surface of height ½ (alpha 1, surfaceScale ½)
    /// brings the light nearer the plane, cos 63.4°. surfaceScale is not applied to a flat alpha 0.
    /// </summary>
    [Fact]
    public void APointLightFallsOffWithItsAngleToTheSurface()
    {
        var flat = new SvgFilterEffects.Image(2, 1, linear: false);
        var raised = new SvgFilterEffects.Image(2, 1, linear: false);
        Set(raised, 0, 0, 0, 0, 0, 1); Set(raised, 1, 0, 0, 0, 0, 1);
        var light = new SvgFilterEffects.Lighting { Kind = SvgFilterEffects.Lighting.LightKind.Point, X = 0.5, Y = 0.5, Z = 1, SurfaceScale = 0.5f, Linear = false };

        float[] px = Run(flat, light).Px;
        Assert.Equal(1f, px[0], 4);
        Assert.Equal(0.7071f, px[4], 4);
        Assert.Equal(0.4472f, Run(raised, light).Px[4], 4);
    }

    /// <summary>
    /// A spot light pointing straight down at the first pixel: that pixel full; the next, 45° off
    /// its axis, is cos 45° of the light times the surface's cos 45° — ½ with no cone, ½ within a
    /// 60° cone, nothing within a 30° one, and ½·cos 45° at a focus of 2.
    /// </summary>
    [Theory]
    [InlineData(double.NaN, 1.0, 0.5f)]
    [InlineData(60.0, 1.0, 0.5f)]
    [InlineData(30.0, 1.0, 0f)]
    [InlineData(double.NaN, 2.0, 0.3536f)]
    public void ASpotLightLightsItsCone(double cone, double focus, float expected)
    {
        var flat = new SvgFilterEffects.Image(2, 1, linear: false);
        var light = new SvgFilterEffects.Lighting
        {
            // Pointing at a target two below: the axis is a direction, its length nothing.
            Kind = SvgFilterEffects.Lighting.LightKind.Spot, X = 0.5, Y = 0.5, Z = 1, PointsAtX = 0.5, PointsAtY = 0.5, PointsAtZ = -1,
            ConeAngle = cone, Focus = focus, Linear = false,
        };

        float[] px = Run(flat, light).Px;
        Assert.Equal(1f, px[0], 4);
        Assert.Equal(expected, px[4], 4);
    }

    /// <summary>
    /// Specular: N·H with H halfway between the light and the eye. From straight above, 1; from the
    /// horizon, cos 45°, squared by an exponent of 2 — ½ at alpha ½, the largest channel; a red light
    /// leaves green and blue 0 and alpha its red; ks doubles it, clipped at 1.
    /// </summary>
    [Theory]
    [InlineData(90f, 1f, 1f, 1f, 1f)]
    [InlineData(0f, 2f, 1f, 0.5f, 0.5f)]
    [InlineData(0f, 2f, 2f, 1f, 1f)]
    public void ASpecularLightIsThePhongHighlight(float elevation, float exponent, float ks, float expectedRed, float expectedAlpha)
    {
        var flat = new SvgFilterEffects.Image(2, 2, linear: false);
        var light = new SvgFilterEffects.Lighting { Specular = true, Elevation = elevation, Exponent = exponent, Constant = ks, R = 1, G = 0, B = 0, Linear = false };

        float[] px = Run(flat, light).Px;
        Assert.Equal(expectedRed, px[0], 4);
        Assert.Equal(0f, px[1], 4);
        Assert.Equal(0f, px[2], 4);
        Assert.Equal(expectedAlpha, px[3], 4);
    }

    /// <summary>The lighting colour is sRGB, taken into the primitive's space: a linear primitive lit with sRGB ½ is linear 0.2140.</summary>
    [Fact]
    public void TheLightingColourIsTakenIntoTheLinearSpace()
    {
        var flat = new SvgFilterEffects.Image(2, 2, linear: true);
        var light = new SvgFilterEffects.Lighting { Elevation = 90, R = 0.5f, G = 0.5f, B = 0.5f, Linear = true };
        var results = SvgFilterEffects.Run(new SvgFilterEffects.Primitive[] { light, new SvgFilterEffects.Offset { Linear = true } }, flat, _ => { });

        // Run hands the last result back in sRGB: 0.2140 linear is 0.5 sRGB again.
        Assert.Equal(0.5f, results.Px[0], 3);
        Assert.Equal(0.2140f, SvgFilterEffects.ToLinear(0.5f), 4);
    }

    /// <summary>
    /// A hostile surface scale or exponent — not a number, infinite — darkens the slope it reaches,
    /// never an exception or a NaN pixel: the pixel beside the raised one is transparent black for a
    /// specular light, whose alpha is its brightest channel (a flat pixel under a NaN exponent stays
    /// lit: 1 to any power is 1). At scale and exponent 1 that pixel is lit, the control.
    /// </summary>
    [Theory]
    [InlineData(float.NaN, 1f)]
    [InlineData(float.PositiveInfinity, 1f)]
    [InlineData(1f, float.NaN)]
    public void AHostileSurfaceScaleOrExponentIsDark(float surfaceScale, float exponent)
    {
        var step = new SvgFilterEffects.Image(3, 3, linear: false);
        Set(step, 2, 1, 0, 0, 0, 1);
        var light = new SvgFilterEffects.Lighting { Specular = true, SurfaceScale = surfaceScale, Exponent = exponent, Elevation = 90, Linear = false };
        var sane = new SvgFilterEffects.Lighting { Specular = true, Elevation = 90, Linear = false };

        float[] px = Run(step, light).Px;
        Assert.All(px, v => Assert.False(float.IsNaN(v)));
        Assert.Equal(new[] { 0f, 0, 0, 0 }, px.Skip((1 * 3 + 1) * 4).Take(4));
        Assert.InRange(Run(step, sane).Px[(1 * 3 + 1) * 4 + 3], 0.5f, 1f);
    }

    /// <summary>
    /// A slope lit from straight above is foreshortened by the normal's z: the interior of the step
    /// (Nx −1) is lit 1/√2, its left column (Nx −2) 1/√5, the flat right column 1.
    /// </summary>
    [Fact]
    public void ASlopeLitFromAboveIsForeshortened()
    {
        var step = new SvgFilterEffects.Image(3, 3, linear: false);
        for (int row = 0; row < 3; row++) { Set(step, 1, row, 0, 0, 0, 1); Set(step, 2, row, 0, 0, 0, 1); }
        var fromAbove = new SvgFilterEffects.Lighting { Elevation = 90, Linear = false };

        float[] px = Run(step, fromAbove).Px;
        Assert.Equal(0.7071f, px[(1 * 3 + 1) * 4], 4);
        Assert.Equal(0.4472f, px[(1 * 3 + 0) * 4], 4);
        Assert.Equal(1f, px[(1 * 3 + 2) * 4], 4);
    }

    /// <summary>An image one pixel wide has no slope to take: flat, lit as such.</summary>
    [Fact]
    public void AOnePixelWideSurfaceIsFlat()
    {
        var column = new SvgFilterEffects.Image(1, 3, linear: false);
        Set(column, 0, 1, 0, 0, 0, 1);
        var light = new SvgFilterEffects.Lighting { Azimuth = 270, Elevation = 30, Linear = false };

        float[] px = Run(column, light).Px;
        Assert.Equal(0.5f, px[0], 4);
        Assert.Equal(0.5f, px[4], 4);
        Assert.Equal(0.5f, px[8], 4);
    }

    // ------------------------------------------------------------------ helpers

    private static SvgFilterEffects.Image Run(SvgFilterEffects.Image source, SvgFilterEffects.Primitive primitive)
        => SvgFilterEffects.Run(new[] { primitive }, source, _ => { });

    /// <summary>A two-input primitive: <paramref name="a"/> is <c>in</c> (a flood's result named "a"), <paramref name="b"/> the source.</summary>
    private static SvgFilterEffects.Image Two(SvgFilterEffects.Image a, SvgFilterEffects.Image b, SvgFilterEffects.Primitive primitive)
    {
        var top = new SvgFilterEffects.Flood { R = a.Px[3] > 0 ? a.Px[0] / a.Px[3] : 0, G = a.Px[3] > 0 ? a.Px[1] / a.Px[3] : 0,
                                              B = a.Px[3] > 0 ? a.Px[2] / a.Px[3] : 0, A = a.Px[3], Result = "a", Linear = false };
        primitive.In = "a";
        primitive.In2 = "SourceGraphic";
        return SvgFilterEffects.Run(new[] { top, primitive }, b, _ => { });
    }

    private static SvgFilterEffects.Image Pixel(float r, float g, float b, float a)
    {
        var image = new SvgFilterEffects.Image(1, 1, linear: false);
        Set(image, 0, 0, r, g, b, a);
        return image;
    }

    /// <summary>A line of <paramref name="length"/> transparent pixels, the one at <paramref name="at"/> opaque white.</summary>
    private static SvgFilterEffects.Image Line(int length, int at)
    {
        var image = new SvgFilterEffects.Image(length, 1, linear: false);
        Set(image, at, 0, 1, 1, 1, 1);
        return image;
    }

    private static void Set(SvgFilterEffects.Image image, int x, int y, float r, float g, float b, float a)
    {
        int i = (y * image.Width + x) * 4;
        image.Px[i] = r; image.Px[i + 1] = g; image.Px[i + 2] = b; image.Px[i + 3] = a;
    }

    private static float[] Alphas(SvgFilterEffects.Image image)
    {
        var alpha = new float[image.Width * image.Height];
        for (int i = 0; i < alpha.Length; i++) alpha[i] = image.Px[i * 4 + 3];
        return alpha;
    }

    private static float Sum(float[] values)
    {
        float sum = 0;
        foreach (float v in values) sum += v;
        return sum;
    }
}
