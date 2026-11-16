// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// An <c>/Indexed</c> shading's colours that arrive as one palette index go through the palette,
/// in both renderers.
/// </summary>
/// <remarks>
/// <para><b>Both renderers read an <c>/Indexed</c> shading in its base space</b>, because producers'
/// functions for types 1 to 3 output base components (<c>SvgShadingDecisionTests</c> pins that
/// reading). But §8.7.4.5.1 lets a mesh of types 4 to 7 use an <c>/Indexed</c> space with no
/// <c>/Function</c>, and each vertex then carries one value — the index (its <c>/Decode</c> gives one
/// component) — and a conforming function-driven file outputs one index too. Read in the base space,
/// that one value became the base's first component: palette entry 1 of a red/blue palette painted
/// some shade of red. It is looked up now (<c>IndexedPalette.ShadingColor</c>), and the SVG export,
/// which rasterises meshes through the raster interpreter and colours gradient stops itself, agrees.</para>
/// <para>The palette here is entry 0 red, entry 1 blue, so "blue" can only come from the lookup.</para>
/// </remarks>
public class IndexedShadingPaletteTests
{
    private const string Palette = "[/Indexed /DeviceRGB 1 <FF00000000FF>]";

    /// <summary>A free-form triangle mesh whose vertices all carry index 1 paints blue.</summary>
    [Fact]
    public void AnIndexedMeshsVerticesAreLookedUpInThePalette()
    {
        byte[] pdf = MeshPdf();

        using var raster = PixelAssert.Render(pdf);
        Assert.Equal(new SKColor(0, 0, 255), PixelAssert.At(raster, 20, 20));

        using var exported = ExportedImage(pdf);
        Assert.Equal(new SKColor(0, 0, 255), exported.GetPixel(20, exported.Height - 1 - 20));
    }

    /// <summary>
    /// An axial shading whose function outputs one value — the index, as §8.7.4.5.1 has it — paints
    /// the palette entry; the gradient stops say the same. 0.6 rounds to entry 1 (blue), as `sc`
    /// rounds an index; truncating would give entry 0 (red).
    /// </summary>
    [Fact]
    public void AnIndexedAxialShadingWithAOneOutputFunctionIsLookedUp()
    {
        byte[] pdf = Pdf("/Sh0 sh", "/Shading << /Sh0 5 0 R >>",
            "<< /ShadingType 2 /ColorSpace " + Palette + " /Coords [0 0 100 0]"
            + " /Function << /FunctionType 2 /Domain [0 1] /C0 [0.6] /C1 [0.6] /N 1 >> /Extend [true true] >>");

        using var raster = PixelAssert.Render(pdf);
        Assert.Equal(new SKColor(0, 0, 255), PixelAssert.At(raster, 50, 50));

        using var ms = new MemoryStream(pdf);
        var stop = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(ms, 0)).Descendants()
            .First(e => e.Name.LocalName == "stop");
        Assert.Equal("rgb(0,0,255)", stop.Attribute("stop-color")?.Value);
    }

    /// <summary>
    /// The control: a three-output function in the same space keeps the base reading, so red
    /// components paint red, not palette entry 1.
    /// </summary>
    [Fact]
    public void AThreeOutputFunctionKeepsTheBaseReading()
    {
        byte[] pdf = Pdf("/Sh0 sh", "/Shading << /Sh0 5 0 R >>",
            "<< /ShadingType 2 /ColorSpace " + Palette + " /Coords [0 0 100 0]"
            + " /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [1 0 0] /N 1 >> /Extend [true true] >>");

        using var raster = PixelAssert.Render(pdf);
        Assert.Equal(new SKColor(255, 0, 0), PixelAssert.At(raster, 50, 50));
    }

    /// <summary>
    /// A grey base takes one component too, so a one-output function there is a grey value, not an
    /// index: 0.25 paints a dark grey, where palette entry 0 would be black.
    /// </summary>
    [Fact]
    public void AGreyBaseKeepsTheBaseReading()
    {
        byte[] pdf = Pdf("/Sh0 sh", "/Shading << /Sh0 5 0 R >>",
            "<< /ShadingType 2 /ColorSpace [/Indexed /DeviceGray 1 <00FF>] /Coords [0 0 100 0]"
            + " /Function << /FunctionType 2 /Domain [0 1] /C0 [0.25] /C1 [0.25] /N 1 >> /Extend [true true] >>");

        using var raster = PixelAssert.Render(pdf);
        SKColor c = PixelAssert.At(raster, 50, 50);
        Assert.InRange(c.Red, 40, 90);
        Assert.Equal(c.Red, c.Blue);
    }

    // ---------------------------------------------------------------- fixture

    /// <summary>The SVG export's rasterised mesh, decoded back from its data URI.</summary>
    private static SKBitmap ExportedImage(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        var image = Assert.Single(XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(ms, 0)).Descendants()
            .Where(e => e.Name.LocalName == "image"));
        string href = image.Attributes().First(a => a.Name.LocalName == "href").Value;
        byte[] png = Convert.FromBase64String(href.Substring(href.IndexOf(',') + 1));
        return SKBitmap.Decode(png);
    }

    /// <summary>
    /// A 100 × 100 page with a type 4 mesh in the palette space and no /Function: one triangle
    /// covering the lower-left half, every vertex index 1 (sample 255 decoded over [0 1]).
    /// </summary>
    private static byte[] MeshPdf()
    {
        byte[] vertices =
        {
            0, 0, 0, 255,
            0, 255, 0, 255,
            0, 0, 255, 255,
        };
        string data = Encoding.GetEncoding("ISO-8859-1").GetString(vertices);
        return Pdf("/Sh0 sh", "/Shading << /Sh0 5 0 R >>",
            "<< /ShadingType 4 /ColorSpace " + Palette + " /BitsPerCoordinate 8 /BitsPerComponent 8"
            + " /BitsPerFlag 8 /Decode [0 100 0 100 0 1] /Length " + vertices.Length.ToString(CultureInfo.InvariantCulture)
            + " >>\nstream\n" + data + "\nendstream");
    }

    private static byte[] Pdf(string content, string resources, params string[] extra)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << " + resources + " >> >>",
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
