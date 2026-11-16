// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The SVG exporter's shading decisions the <c>Base/Rendering</c> sweep's batch 9
/// (<c>.claude/sweep-rendering-9.ps1</c>) found no test for.
/// </summary>
public class SvgShadingDecisionTests
{
    private const string Ramp = "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >>";

    /// <summary>A radial shading's start radius is the gradient's focal radius, <c>fr</c>.</summary>
    [Fact]
    public void ARadialShadingsStartRadiusIsTheFocalRadius()
    {
        var gradient = Single(Svg("q 0 0 400 400 re W n /Sh0 sh Q",
            "/Shading << /Sh0 5 0 R >>",
            "<< /ShadingType 3 /ColorSpace /DeviceRGB /Coords [200 200 10 200 200 100] " + Ramp + " /Extend [true true] >>"),
            "radialGradient");

        Assert.Equal("10", gradient.Attribute("fr")?.Value);
    }

    /// <summary>
    /// An <c>/Indexed</c> shading is read in its base space, which is what such files' functions
    /// output (the reading both renderers document). A Lab base tells that apart from reading the
    /// components by count: L* 50, a* 80 converts to a Lab red, where clamping the same three
    /// numbers as RGB gives yellow.
    /// </summary>
    [Fact]
    public void AnIndexedShadingIsReadInItsBaseSpace()
    {
        var gradient = Single(Svg("q 0 0 400 400 re W n /Sh0 sh Q",
            "/Shading << /Sh0 5 0 R >>",
            "<< /ShadingType 2 /ColorSpace [/Indexed [/Lab << /WhitePoint [0.9505 1 1.089] >>] 1 <000000FFFFFF>]"
            + " /Coords [0 0 400 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [50 80 0] /C1 [50 80 0] /N 1 >>"
            + " /Extend [true true] >>"),
            "linearGradient");

        string colour = Stops(gradient)[0].Attribute("stop-color")?.Value;
        Assert.Equal(PdfColor.ToCssRgb(PdfColor.LabToSRgb(50f, 80f, 0f,
            new Tetitulz.Pdf.Core.Objects.PdfDictionary
            {
                [Tetitulz.Pdf.Core.Objects.PdfName.WhitePoint] = new Tetitulz.Pdf.Core.Objects.PdfArray
                {
                    new Tetitulz.Pdf.Core.Objects.PdfNumber(0.9505), new Tetitulz.Pdf.Core.Objects.PdfNumber(1),
                    new Tetitulz.Pdf.Core.Objects.PdfNumber(1.089)
                }
            })), colour);
        Assert.NotEqual("rgb(255,255,0)", colour);
    }

    /// <summary>A shading pattern's <c>/Matrix</c> reaches the gradient's transform.</summary>
    [Fact]
    public void AShadingPatternsMatrixReachesTheGradient()
    {
        var gradient = Single(Svg("/Pattern cs /P0 scn 0 0 400 400 re f",
            "/Pattern << /P0 5 0 R >>",
            "<< /PatternType 2 /Matrix [2 0 0 2 0 0] /Shading << /ShadingType 2 /ColorSpace /DeviceRGB"
            + " /Coords [0 0 100 0] " + Ramp + " /Extend [true true] >> >>"),
            "linearGradient");

        Assert.Equal("matrix(2,0,0,2,0,0)", gradient.Attribute("gradientTransform")?.Value);
    }

    /// <summary>
    /// Pattern-filled text carries its own element transform, so its gradient is mapped back through
    /// that transform's inverse; a path filled with the same pattern keeps the pattern's own space.
    /// </summary>
    [Fact]
    public void PatternFilledTextMapsTheGradientThroughItsElementsInverse()
    {
        var doc = XDocument.Parse(Svg(
            "/Pattern cs /P0 scn 0 0 100 100 re f BT /F1 24 Tf 72 300 Td (Hi) Tj ET",
            "/Pattern << /P0 5 0 R >> /Font << /F1 6 0 R >>",
            "<< /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceRGB"
            + " /Coords [0 0 100 0] " + Ramp + " /Extend [true true] >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));

        string[] transforms = doc.Descendants().Where(e => e.Name.LocalName == "linearGradient")
            .Select(e => e.Attribute("gradientTransform")?.Value).ToArray();

        Assert.Equal(2, transforms.Length);
        Assert.Equal("matrix(1,0,0,1,0,0)", transforms[0]);
        Assert.NotEqual(transforms[0], transforms[1]);
    }

    /// <summary>A rasterised shading under constant alpha carries it on its image.</summary>
    [Fact]
    public void ARasterisedShadingCarriesTheConstantAlpha()
    {
        var doc = XDocument.Parse(Svg("/GS1 gs q 0 0 200 200 re W n /Sh0 sh Q",
            "/Shading << /Sh0 5 0 R >> /ExtGState << /GS1 6 0 R >>",
            "<< /ShadingType 1 /ColorSpace /DeviceRGB /Domain [0 1 0 1] /Matrix [200 0 0 200 0 0]"
            + " /Function << /FunctionType 2 /Domain [0 1 0 1] /C0 [1 0 0] /C1 [1 0 0] /N 1 >> >>",
            "<< /Type /ExtGState /ca 0.5 >>"));

        var image = Assert.Single(doc.Descendants().Where(e => e.Name.LocalName == "image"));
        Assert.Equal("0.5", image.Attribute("opacity")?.Value);
    }

    /// <summary>
    /// A mesh shading that rasterises to nothing paints nothing — it does not fall through to the
    /// flat-colour approximation, which is for a shading that could not be rasterised at all.
    /// </summary>
    [Fact]
    public void AMeshShadingThatRastersToNothingPaintsNothing()
    {
        var doc = XDocument.Parse(Svg("/Sh0 sh",
            "/Shading << /Sh0 5 0 R >>",
            "<< /ShadingType 4 /ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 8"
            + " /BitsPerFlag 8 /Decode [0 100 0 100 0 1 0 1 0 1] /BBox [0 0 100 100] /Length 0 >>\nstream\n\nendstream"));

        Assert.DoesNotContain(doc.Descendants(), e => e.Attribute("data-sh-type") != null);
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "image");
    }

    /// <summary>
    /// An <c>/Indexed</c> mesh with no <c>/Function</c> carries one palette index per vertex
    /// (§8.7.4.5.1): every vertex here is index 1, blue in a green/blue palette. Both renderers read
    /// an <c>/Indexed</c> shading in its base space, and fed the index to DeviceRGB as its one
    /// component; the SVG export rasterises meshes through the raster path, so the pixel is both.
    /// </summary>
    [Fact]
    public void AnIndexedMeshWithoutAFunctionLooksItsVerticesUpInThePalette()
    {
        // flag x y index, one byte each; Decode maps x and y to 0..100 and the index byte to 0..1.
        string vertices = "\0\0\0\u00FF" + "\0\u00FF\0\u00FF" + "\0\0\u00FF\u00FF";
        byte[] pdf = Pdf("/Sh0 sh", "/Shading << /Sh0 5 0 R >>",
            "<< /ShadingType 4 /ColorSpace [/Indexed /DeviceRGB 1 <00FF000000FF>] /BitsPerCoordinate 8"
            + " /BitsPerComponent 8 /BitsPerFlag 8 /Decode [0 100 0 100 0 1] /Length 12 >>\nstream\n"
            + vertices + "\nendstream");

        using var bmp = PixelAssert.Render(pdf);
        var c = PixelAssert.At(bmp, 20, 20);
        Assert.True(c.Blue > 200 && c.Red < 60 && c.Green < 60, $"expected palette blue, got {c}");
    }

    /// <summary>
    /// A one-output function on an <c>/Indexed</c> RGB base outputs an index, the reading the
    /// standard gives, and both renderers look it up: index 1 is blue in the gradient and on the page.
    /// </summary>
    [Fact]
    public void AnIndexedShadingWithAOneOutputFunctionLooksTheIndexUp()
    {
        byte[] pdf = Pdf("q 0 0 400 400 re W n /Sh0 sh Q", "/Shading << /Sh0 5 0 R >>",
            "<< /ShadingType 2 /ColorSpace [/Indexed /DeviceRGB 1 <00FF000000FF>] /Coords [0 0 400 0]"
            + " /Function << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [1] /N 1 >> /Extend [true true] >>");

        using var ms = new MemoryStream(pdf);
        var gradient = Single(PdfSvgRenderer.RenderPageToSvg(ms, 0), "linearGradient");
        Assert.Equal("rgb(0,0,255)", Stops(gradient)[0].Attribute("stop-color")?.Value);

        using var bmp = PixelAssert.Render(pdf);
        var c = PixelAssert.At(bmp, 200, 200);
        Assert.True(c.Blue > 200 && c.Red < 60 && c.Green < 60, $"expected palette blue, got {c}");
    }

    // ---------------------------------------------------------------- fixture

    private static XElement Single(string svg, string gradient)
        => Assert.Single(XDocument.Parse(svg).Descendants().Where(e => e.Name.LocalName == gradient));

    private static XElement[] Stops(XElement gradient)
        => gradient.Elements().Where(e => e.Name.LocalName == "stop").ToArray();

    /// <summary>One 400 × 400 page: <paramref name="content"/> under <paramref name="resources"/>, objects 5 onward as given.</summary>
    private static string Svg(string content, string resources, params string[] extra)
    {
        using var ms = new MemoryStream(Pdf(content, resources, extra));
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    /// <summary>The page <see cref="Svg"/> renders, as bytes (Latin-1, so a binary stream survives).</summary>
    private static byte[] Pdf(string content, string resources, params string[] extra)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Contents 4 0 R /Resources << " + resources + " >> >>",
            string.Format(CultureInfo.InvariantCulture, "<< /Length {0} >>\nstream\n{1}\nendstream", content.Length, content),
        };
        objects.AddRange(extra);

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.GetEncoding("ISO-8859-1").GetBytes(sb.ToString());
    }
}
