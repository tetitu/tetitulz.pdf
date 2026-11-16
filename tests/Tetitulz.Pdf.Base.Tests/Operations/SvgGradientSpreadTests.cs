// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> applies a gradient's <c>spreadMethod</c> (SVG 1.1 §13.2.2):
/// <c>repeat</c> starts the gradient over past each end, <c>reflect</c> runs it back and forth. A PDF
/// shading only carries its end colours on, so the shading's axis is stretched to cover the shape in
/// whole cycles, and its function steps through one copy of the stops per cycle.
/// </summary>
/// <remarks>
/// Both were drawn as <c>pad</c> and reported. The gradients below run red to blue over 50 user
/// units, from x = 0, across a 200-wide rect: four cycles.
/// </remarks>
public class SvgGradientSpreadTests
{
    private static string Gradient(string spread, string x1 = "0", string x2 = "50")
        => $@"<linearGradient id=""g"" gradientUnits=""userSpaceOnUse"" x1=""{x1}"" x2=""{x2}"" spreadMethod=""{spread}"">" +
           @"<stop offset=""0"" stop-color=""#ff0000""/><stop offset=""1"" stop-color=""#0000ff""/></linearGradient>";

    private const string Rect = @"<rect width=""200"" height=""100"" fill=""url(#g)""/>";

    [Fact]
    public void RepeatStartsOverEachCycle()
    {
        var at = Render(Gradient("repeat") + Rect);

        AssertRedToBlue(at(5, 50), 0.1);
        AssertRedToBlue(at(45, 50), 0.9);
        AssertRedToBlue(at(55, 50), 0.1);
        AssertRedToBlue(at(195, 50), 0.9);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// A gradient whose transform scales its space far down still repeats: covering the shape takes
    /// the transform's inverse, and a determinant of 1e-10 (a scale of 1e-5 on each axis) inverts in
    /// the converter, where a renderer's floor for a float inverse would have written it as pad —
    /// one cycle over 0 to 5 000 000, where the shape's 200 units are four (a float's rounding of
    /// the inverse may make the cover a fifth).
    /// </summary>
    [Fact]
    public void RepeatCoversTheShapeUnderAFarScaledDownTransform()
    {
        using var doc = Open(@"<linearGradient id=""g"" gradientUnits=""userSpaceOnUse"" x1=""0"" x2=""5000000"" " +
                             @"gradientTransform=""scale(0.00001)"" spreadMethod=""repeat"">" +
                             @"<stop offset=""0"" stop-color=""#ff0000""/><stop offset=""1"" stop-color=""#0000ff""/></linearGradient>" + Rect);
        var shading = Assert.IsType<PdfDictionary>(doc.ResolveObject(Pattern(doc, "P0")[new PdfName("Shading")]));
        var function = Assert.IsType<PdfDictionary>(doc.ResolveObject(shading[new PdfName("Function")]));

        double[] coords = Numbers(doc, shading[new PdfName("Coords")]);
        Assert.Equal(0, coords[0]);
        Assert.InRange(coords[2], 20_000_000, 25_000_000);
        Assert.Equal(coords[2] / 5_000_000, Assert.IsType<PdfArray>(doc.ResolveObject(function[new PdfName("Functions")])).Count);
    }

    [Fact]
    public void ReflectRunsBackEveryOtherCycle()
    {
        var at = Render(Gradient("reflect") + Rect);

        AssertRedToBlue(at(45, 50), 0.9);
        AssertRedToBlue(at(55, 50), 0.9);
        AssertRedToBlue(at(95, 50), 0.1);
        AssertRedToBlue(at(105, 50), 0.1);
    }

    /// <summary>
    /// The shading covers the shape in whole cycles: its axis runs 0–200, and its function has one
    /// piece per cycle, reversed on alternate ones for <c>reflect</c>.
    /// </summary>
    [Theory]
    [InlineData("repeat", "0 1 0 1 0 1 0 1")]
    [InlineData("reflect", "0 1 1 0 0 1 1 0")]
    public void TheShadingSpansTheShapeInWholeCycles(string spread, string encode)
    {
        using var doc = Open(Gradient(spread) + Rect);
        var shading = Assert.IsType<PdfDictionary>(doc.ResolveObject(Pattern(doc, "P0")[new PdfName("Shading")]));
        var function = Assert.IsType<PdfDictionary>(doc.ResolveObject(shading[new PdfName("Function")]));

        Assert.Equal(new[] { 0.0, 0, 200, 0 }, Numbers(doc, shading[new PdfName("Coords")]));
        Assert.Equal(4, Assert.IsType<PdfArray>(doc.ResolveObject(function[new PdfName("Functions")])).Count);
        Assert.Equal(new[] { 0.25, 0.5, 0.75 }, Numbers(doc, function[new PdfName("Bounds")]));
        Assert.Equal(encode, string.Join(" ", Numbers(doc, function[new PdfName("Encode")])));
    }

    /// <summary>
    /// Cycles before the gradient's start count too: from x = 100 to 150, the rect's left half is two
    /// cycles back, so x = 25 and x = 75 are each half way through one — where pad was red.
    /// </summary>
    [Fact]
    public void CyclesBeforeTheStartRepeatToo()
    {
        var at = Render(Gradient("repeat", "100", "150") + Rect);

        AssertRedToBlue(at(25, 50), 0.5);
        AssertRedToBlue(at(75, 50), 0.5);
        AssertRedToBlue(at(145, 50), 0.9);
    }

    /// <summary>
    /// A radial gradient repeats outward: white to black over 50 around the rect's middle, and 60
    /// out is a fifth of the way into the second ring — light grey, where pad was black.
    /// </summary>
    [Fact]
    public void ARadialGradientRepeatsOutward()
    {
        var at = Render(@"<radialGradient id=""g"" gradientUnits=""userSpaceOnUse"" cx=""100"" cy=""50"" r=""50"" spreadMethod=""repeat"">" +
                        @"<stop offset=""0"" stop-color=""#ffffff""/><stop offset=""1"" stop-color=""#000000""/></radialGradient>" + Rect);

        var (r, g, b) = at(160, 50);
        Assert.InRange(r, 180, 225);
        Assert.InRange(g, 180, 225);
    }

    /// <summary>
    /// A shape inside one reflected cycle, the second (50–100), runs it backwards: at x = 65,
    /// three tenths into it, the colour is seven tenths of the way to blue.
    /// </summary>
    [Fact]
    public void AShapeInsideOneReflectedCycleIsReversed()
    {
        var at = Render(Gradient("reflect") + @"<rect x=""60"" width=""30"" height=""100"" fill=""url(#g)""/>");

        AssertRedToBlue(at(65, 50), 0.7);
    }

    /// <summary>
    /// With its focal point on the circle, a radial gradient's rings all pass through it, and 140 out
    /// along the axis is 1.4 rings: four tenths into the second, grey, where pad was black.
    /// </summary>
    [Fact]
    public void ARadialGradientWithItsFocusOnTheCircleRepeats()
    {
        var at = Render(@"<radialGradient id=""g"" gradientUnits=""userSpaceOnUse"" cx=""100"" cy=""50"" r=""50"" fx=""50"" fy=""50"" spreadMethod=""repeat"">" +
                        @"<stop offset=""0"" stop-color=""#ffffff""/><stop offset=""1"" stop-color=""#000000""/></radialGradient>" + Rect);

        var (r, _, _) = at(190, 50);
        Assert.InRange(r, 120, 185);
    }

    /// <summary>A faded gradient's mask repeats with it: the grey function is stitched the same way.</summary>
    [Fact]
    public void AMaskRepeatsWithItsGradient()
    {
        using var doc = Open(@"<linearGradient id=""g"" gradientUnits=""userSpaceOnUse"" x1=""0"" x2=""50"" spreadMethod=""repeat"">" +
                             @"<stop offset=""0"" stop-color=""#ff0000""/><stop offset=""1"" stop-color=""#0000ff"" stop-opacity=""0""/></linearGradient>" + Rect);
        var patterns = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[new PdfName("Pattern")]));

        foreach (var key in patterns.Keys)
        {
            var shading = Assert.IsType<PdfDictionary>(doc.ResolveObject(((PdfDictionary)doc.ResolveObject(patterns[key]))[new PdfName("Shading")]));
            var function = Assert.IsType<PdfDictionary>(doc.ResolveObject(shading[new PdfName("Function")]));
            Assert.Equal(4, Assert.IsType<PdfArray>(doc.ResolveObject(function[new PdfName("Functions")])).Count);
        }
        Assert.Equal(2, patterns.Keys.Count);   // the colour and the grey
    }

    /// <summary>
    /// Hostile input: a repeating gradient 0.001 long over a 200-wide shape asks for 200 000 cycles.
    /// They stop at <see cref="PdfLimits.MaxSvgGradientCycles"/>, reported, the rest padded.
    /// </summary>
    [Fact]
    public void ATinyRepeatingGradientIsCapped()
    {
        using var doc = Open(Gradient("repeat", "0", "0.001") + Rect);
        var shading = Assert.IsType<PdfDictionary>(doc.ResolveObject(Pattern(doc, "P0")[new PdfName("Shading")]));
        var function = Assert.IsType<PdfDictionary>(doc.ResolveObject(shading[new PdfName("Function")]));

        Assert.Equal(PdfLimits.MaxSvgGradientCycles, Assert.IsType<PdfArray>(doc.ResolveObject(function[new PdfName("Functions")])).Count);
        Assert.Contains("spreadMethod", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary><c>pad</c>, and a value that is none of the three, carry the end colours on: no cycles.</summary>
    [Theory]
    [InlineData("pad")]
    [InlineData("sideways")]
    public void PadIsOneCycle(string spread)
    {
        using var doc = Open(Gradient(spread) + Rect);
        var shading = Assert.IsType<PdfDictionary>(doc.ResolveObject(Pattern(doc, "P0")[new PdfName("Shading")]));

        Assert.Equal(new[] { 0.0, 0, 50, 0 }, Numbers(doc, shading[new PdfName("Coords")]));
        Assert.Equal(2, ((PdfNumber)Assert.IsType<PdfDictionary>(doc.ResolveObject(shading[new PdfName("Function")]))[new PdfName("FunctionType")]).DoubleValue);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A pixel a fraction <paramref name="t"/> of the way from red to blue, within 0.1.</summary>
    private static void AssertRedToBlue((int R, int G, int B) pixel, double t)
    {
        Assert.InRange(pixel.R, 255 * (1 - t) - 26, 255 * (1 - t) + 26);
        Assert.InRange(pixel.B, 255 * t - 26, 255 * t + 26);
    }

    private static PdfDictionary Pattern(PdfDocument doc, string name)
    {
        var patterns = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[new PdfName("Pattern")]));
        return Assert.IsType<PdfDictionary>(doc.ResolveObject(patterns[new PdfName(name)]));
    }

    private static double[] Numbers(PdfDocument doc, PdfObject array)
        => Assert.IsType<PdfArray>(doc.ResolveObject(array)).Select(o => Math.Round(((PdfNumber)doc.ResolveObject(o)).DoubleValue, 4)).ToArray();

    private static PdfDocument Open(string body) => PdfDocument.Open(new MemoryStream(Convert(body)));

    private static byte[] Convert(string body)
        => SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });

    private static Func<int, int, (int, int, int)> Render(string body)
    {
        var page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(Convert(body)), 0, 72);
        return (x, y) => { int i = y * page.Stride + x * 4; return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]); };
    }
}
