// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Tetitulz.Pdf.DocxToPdf.Rendering.Drawings;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// <see cref="DrawingColorResolver"/> read directly: the HSL transforms on a colour that is not grey,
/// the hue wrap, the theme placeholder
/// <c>phClr</c>, the background fill list, presets, and the opacity threshold.
/// </summary>
public class DrawingColorResolverTests
{
    private const string ANs = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private static OpenXmlElement Fill(string colorXml)
        => new DocumentFormat.OpenXml.Drawing.SolidFill(
            $"<a:solidFill xmlns:a=\"{ANs}\">{colorXml}</a:solidFill>");

    private static DrawingRgba Resolve(string colorXml, string phClr = null)
    {
        Assert.True(new DrawingColorResolver(null).TryResolve(Fill(colorXml), phClr, out var rgba));
        return rgba;
    }

    private static void AssertRgb(double r, double g, double b, DrawingRgba c)
    {
        Assert.Equal(r, c.R, 6);
        Assert.Equal(g, c.G, 6);
        Assert.Equal(b, c.B, 6);
    }

    /// <summary>
    /// <c>lumMod</c> 75% on orange (FF8000: saturation 1, luminance ½) keeps the hue and
    /// saturation and scales luminance to ⅜: (0.75, 0.75 × 128/255, 0). Converting to HSL with the
    /// wrong minimum turns it grey.
    /// </summary>
    [Fact]
    public void LuminanceModulationKeepsTheHue()
    {
        AssertRgb(0.75, 0.75 * 128 / 255, 0, Resolve("<a:srgbClr val=\"FF8000\"><a:lumMod val=\"75000\"/></a:srgbClr>"));
    }

    /// <summary>
    /// A hue pushed past a full turn wraps: magenta (300°) with <c>hueMod</c> 250% is 750°, which
    /// is 30° — orange, (1, 0.5, 0).
    /// </summary>
    [Fact]
    public void HueWrapsPastAFullTurn()
    {
        AssertRgb(1, 0.5, 0, Resolve("<a:srgbClr val=\"FF00FF\"><a:hueMod val=\"250000\"/></a:srgbClr>"));
    }

    /// <summary>
    /// <c>schemeClr val="phClr"</c> is the placeholder a style reference fills in with its own
    /// colour; outside a reference there is none to give, and it does not resolve.
    /// </summary>
    [Fact]
    public void ThePlaceholderTakesTheReferencesColour()
    {
        AssertRgb(0x12 / 255.0, 0x34 / 255.0, 0x56 / 255.0, Resolve("<a:schemeClr val=\"phClr\"/>", "123456"));
        Assert.False(new DrawingColorResolver(null).TryResolve(Fill("<a:schemeClr val=\"phClr\"/>"), null, out _));
    }

    /// <summary>
    /// An unknown preset colour is black, and preset names are matched without regard to case.
    /// </summary>
    [Fact]
    public void PresetsFallBackToBlackAndIgnoreCase()
    {
        AssertRgb(0, 0, 0, Resolve("<a:prstClr val=\"chartreuse\"/>"));
        AssertRgb(0xA9 / 255.0, 0xA9 / 255.0, 0xA9 / 255.0, Resolve("<a:prstClr val=\"DARKGRAY\"/>"));
    }

    /// <summary>An alpha of 99.9% counts as opaque; 99.8% does not.</summary>
    [Fact]
    public void AlphaOfNinetyNinePointNineIsOpaque()
    {
        Assert.True(Resolve("<a:srgbClr val=\"000000\"><a:alpha val=\"99900\"/></a:srgbClr>").IsOpaque);
        Assert.False(Resolve("<a:srgbClr val=\"000000\"><a:alpha val=\"99800\"/></a:srgbClr>").IsOpaque);
    }

    /// <summary>
    /// A <c>fillRef idx</c> of 1001 or more indexes the theme's background fill list from 1:
    /// 1001 is its first entry, 1002 its second (ECMA-376 §20.1.4.1.15).
    /// </summary>
    [Theory]
    [InlineData(1001, 0, 1, 0)]
    [InlineData(1002, 0, 0, 1)]
    [InlineData(1, 1, 0, 0)]
    public void BackgroundFillReferencesIndexTheBackgroundListFromOne(int idx, double r, double g, double b)
    {
        string theme =
            $"<a:theme xmlns:a=\"{ANs}\" name=\"T\"><a:themeElements>" +
            "<a:clrScheme name=\"c\"/><a:fontScheme name=\"f\"/><a:fmtScheme name=\"x\">" +
            "<a:fillStyleLst><a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill></a:fillStyleLst>" +
            "<a:lnStyleLst/><a:effectStyleLst/>" +
            "<a:bgFillStyleLst><a:solidFill><a:srgbClr val=\"00FF00\"/></a:solidFill>" +
            "<a:solidFill><a:srgbClr val=\"0000FF\"/></a:solidFill></a:bgFillStyleLst>" +
            "</a:fmtScheme></a:themeElements></a:theme>";

        using var ms = new MemoryStream();
        using var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var themePart = main.AddNewPart<ThemePart>();
        using (var s = new MemoryStream(Encoding.UTF8.GetBytes(theme))) themePart.FeedData(s);

        var style = new DocumentFormat.OpenXml.Office2010.Word.DrawingShape.ShapeStyle(
            $"<wps:style xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" xmlns:a=\"{ANs}\">" +
            $"<a:fillRef idx=\"{idx}\"><a:srgbClr val=\"808080\"/></a:fillRef></wps:style>");

        var resolver = new DrawingColorResolver(new StyleResolver(doc));
        Assert.True(resolver.TryResolveFillRef(style, out var rgba));
        AssertRgb(r, g, b, rgba);
    }
}
