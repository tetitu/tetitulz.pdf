// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> measures a container's bounding box (SVG 1.1 §7.11) — the union of
/// what it draws, each shape's, text's or image's box taken through its own transform into the
/// container's user space, markers left out — so a clip or a mask in <c>objectBoundingBox</c> units,
/// or a mask's region, applies to a group as it does to a shape.
/// </summary>
/// <remarks>
/// On a group these were reported and not applied (batch 46): only a shape's or a text's box was
/// collected. The clip and mask below keep the left half of the box; the red rects cover 100–200.
/// </remarks>
public class SvgGroupBoxTests
{
    private const string HalfClip = @"<clipPath id=""c"" clipPathUnits=""objectBoundingBox""><rect width=""0.5"" height=""1""/></clipPath>";
    private const string HalfMask = @"<mask id=""m"" maskContentUnits=""objectBoundingBox""><rect width=""0.5"" height=""1"" fill=""white""/></mask>";
    private const string TwoRects = @"<rect x=""100"" width=""50"" height=""100"" fill=""#ff0000""/><rect x=""150"" width=""50"" height=""100"" fill=""#ff0000""/>";

    [Theory]
    [InlineData(HalfClip, @"clip-path=""url(#c)""")]
    [InlineData(HalfMask, @"mask=""url(#m)""")]
    public void AGroupsBoxIsItsChildrensUnion(string definition, string reference)
    {
        var at = Render(definition + $@"<g {reference}>{TwoRects}</g>");

        AssertColour((255, 0, 0), at(125, 50));
        AssertColour((255, 255, 255), at(175, 50));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// Each child's box is taken through its transform, and the group's own: a group moved 50 right
    /// holds rects at 50–100 and, moved 50 further, at 50–100 again — the union 100–200 on the page.
    /// </summary>
    [Fact]
    public void TransformsAreTakenIntoTheGroupsSpace()
    {
        var at = Render(HalfClip + @"<g transform=""translate(50 0)"" clip-path=""url(#c)"">" +
                        @"<rect x=""50"" width=""50"" height=""100"" fill=""#ff0000""/>" +
                        @"<g transform=""translate(50 0)""><rect x=""50"" width=""50"" height=""100"" fill=""#ff0000""/></g></g>");

        AssertColour((255, 0, 0), at(125, 50));
        AssertColour((255, 255, 255), at(175, 50));
    }

    /// <summary>An image's box counts: a group of one image, 100–200, keeps 100–150.</summary>
    [Fact]
    public void AnImagesBoxCounts()
    {
        var at = Render(HalfClip + $@"<g clip-path=""url(#c)""><image href=""{RedPng()}"" x=""100"" width=""100"" height=""100""/></g>");

        AssertColour((255, 0, 0), at(125, 50));
        AssertColour((255, 255, 255), at(175, 50));
    }

    /// <summary>
    /// A marker is not the group's geometry: a square path over 100–150 whose markers reach 200
    /// leaves the box 100–150, so the clip's left half is 100–125: the path beyond it is cut, and so is
    /// the marker at (150, 0), which covers 150–200 above y = 50.
    /// </summary>
    [Fact]
    public void AMarkerDoesNotWidenTheBox()
    {
        var at = Render(HalfClip +
            @"<marker id=""k"" markerWidth=""50"" markerHeight=""100"" refX=""0"" refY=""50"" markerUnits=""userSpaceOnUse""><rect width=""50"" height=""100"" fill=""#0000ff""/></marker>" +
            @"<g clip-path=""url(#c)""><path d=""M 100 0 H 150 V 100 H 100 Z"" fill=""#ff0000"" marker-mid=""url(#k)""/></g>");

        AssertColour((255, 0, 0), at(115, 20));
        AssertColour((255, 255, 255), at(135, 20));
        AssertColour((255, 255, 255), at(175, 20));
    }

    /// <summary>
    /// A child's own mask content is not the group's geometry: a rect over 100–150 masked by a white
    /// square over the whole page leaves the box 100–150, so the clip keeps 100–125.
    /// </summary>
    [Fact]
    public void MaskContentDoesNotWidenTheBox()
    {
        var at = Render(HalfClip + @"<mask id=""all"" maskUnits=""userSpaceOnUse"" x=""0"" y=""0"" width=""200"" height=""200""><rect width=""200"" height=""200"" fill=""white""/></mask>" +
                        @"<g clip-path=""url(#c)""><rect x=""100"" width=""50"" height=""100"" fill=""#ff0000"" mask=""url(#all)""/></g>");

        AssertColour((255, 0, 0), at(115, 50));
        AssertColour((255, 255, 255), at(135, 50));
    }

    /// <summary>An image's own clip in its box: the image over 100–200, clipped to its left half.</summary>
    [Fact]
    public void AnImagesOwnBoxServesItsClip()
    {
        var at = Render(HalfClip + $@"<image href=""{RedPng()}"" x=""100"" width=""100"" height=""100"" clip-path=""url(#c)""/>");

        AssertColour((255, 0, 0), at(125, 50));
        AssertColour((255, 255, 255), at(175, 50));
    }

    /// <summary>
    /// An image's mask is in the image's user space, its transform included: moved 100 right, the
    /// image shows on 100–150, where the mask is white, and not on 150–200.
    /// </summary>
    [Fact]
    public void AnImagesMaskIsInItsUserSpace()
    {
        var at = Render(@"<mask id=""m"" maskUnits=""userSpaceOnUse"" x=""0"" y=""0"" width=""200"" height=""200""><rect width=""50"" height=""100"" fill=""white""/></mask>" +
                        $@"<image href=""{RedPng()}"" width=""100"" height=""100"" transform=""translate(100 0)"" mask=""url(#m)""/>");

        AssertColour((255, 0, 0), at(125, 50));
        AssertColour((255, 255, 255), at(175, 50));
    }

    /// <summary>A link is a container too: its box is its children's union, as a group's is.</summary>
    [Fact]
    public void ALinksBoxIsItsChildrensUnion()
    {
        var at = Render(HalfClip + $@"<a href=""https://example.org/"" clip-path=""url(#c)"">{TwoRects}</a>");

        AssertColour((255, 0, 0), at(125, 50));
        AssertColour((255, 255, 255), at(175, 50));
    }

    /// <summary>A group that draws nothing has no box: its clip and mask in the box are reported, not applied.</summary>
    [Theory]
    [InlineData(HalfClip, @"clip-path=""url(#c)""", "objectBoundingBox")]
    [InlineData(HalfMask, @"mask=""url(#m)""", "maskContentUnits")]
    public void AnEmptyGroupsBoxIsReported(string definition, string reference, string reason)
    {
        Render(definition + $@"<g {reference}><desc>nothing drawn</desc></g>");

        Assert.Contains(reason, SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>A group's region in the box clips its mask's content, as a shape's does.</summary>
    [Fact]
    public void AGroupsMaskRegionIsInItsBox()
    {
        var at = Render(@"<mask id=""m"" x=""0"" width=""0.5""><rect width=""200"" height=""200"" fill=""white""/></mask>" +
                        $@"<g mask=""url(#m)"">{TwoRects}</g>");

        AssertColour((255, 0, 0), at(125, 50));
        AssertColour((255, 255, 255), at(175, 50));
    }

    // ------------------------------------------------------------------ helpers

    private static string RedPng()
    {
        using var bmp = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bmp.SetPixel(0, 0, new SKColor(255, 0, 0, 255));
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return "data:image/png;base64," + System.Convert.ToBase64String(data.ToArray());
    }

    private static void AssertColour((int R, int G, int B) expected, (int R, int G, int B) actual)
    {
        Assert.InRange(actual.R, expected.R - 12, expected.R + 12);
        Assert.InRange(actual.G, expected.G - 12, expected.G + 12);
        Assert.InRange(actual.B, expected.B - 12, expected.B + 12);
    }

    private static Func<int, int, (int, int, int)> Render(string body)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });
        var page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);
        return (x, y) => { int i = y * page.Stride + x * 4; return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]); };
    }
}
