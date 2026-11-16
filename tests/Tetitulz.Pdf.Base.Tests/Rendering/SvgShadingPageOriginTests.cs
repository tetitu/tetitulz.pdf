// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A rasterised shading — <c>sh</c> and a shading-pattern fill alike — is painted into a bitmap
/// covering the page box, whose corner need not be (0, 0), and placed in the SVG at that corner:
/// content sits at raw PDF coordinates under the root group's origin shift, and an image at (0, 0)
/// lay outside a cropped page's view, while a shading inside the box rasterised outside the bitmap
/// and was taken for empty. The control, a page whose box starts at the origin, is placed at (0, 0).
/// </summary>
public class SvgShadingPageOriginTests
{
    /// <summary>A function shading filling the cropped box: the image covers the box, at its corner.</summary>
    [Theory]
    [InlineData("/CropBox [100 100 200 200]", 100f, 200f)]
    [InlineData("", 0f, 200f)]
    public void AShadingFill_IsPlacedAtThePageBoxCorner(string cropBox, float corner, float flipAt)
    {
        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(Pdf(cropBox, "/Sh0 sh")), 0);
        XElement image = XDocument.Parse(svg).Descendants().Single(e => e.Name.LocalName == "image");

        Assert.Equal($"matrix(1,0,0,-1,{corner},{flipAt})", image.Attribute("transform")!.Value);
        Assert.Equal("0", image.Attribute("x")!.Value);
        Assert.Equal(200f - corner, float.Parse(image.Attribute("width")!.Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>A shading pattern's tile sits at the box's corner too.</summary>
    [Theory]
    [InlineData("/CropBox [100 100 200 200]", "100")]
    [InlineData("", "0")]
    public void AShadingPatternTile_SitsAtThePageBoxCorner(string cropBox, string corner)
    {
        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(Pdf(cropBox, "/Pattern cs /P0 scn 100 100 100 100 re f")), 0);
        XElement pattern = XDocument.Parse(svg).Descendants().Single(e => e.Name.LocalName == "pattern");

        Assert.Equal(corner, pattern.Attribute("x")!.Value);
        Assert.Equal(corner, pattern.Attribute("y")!.Value);
        Assert.Single(pattern.Elements().Where(e => e.Name.LocalName == "image"));
    }

    /// <summary>
    /// A 200 × 200 page with <paramref name="cropBox"/>; a type 1 shading painting red over
    /// [100, 200]², as a shading and as a pattern.
    /// </summary>
    private static byte[] Pdf(string cropBox, string content)
    {
        const string shading = "<< /ShadingType 1 /ColorSpace /DeviceRGB /Domain [0 1 0 1] /Matrix [100 0 0 100 100 100] " +
                               "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [1 0 0] /N 1 >> >>";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] {cropBox} /Contents 4 0 R " +
            "/Resources << /Shading << /Sh0 5 0 R >> /Pattern << /P0 6 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            shading,
            "<< /PatternType 2 /Shading 5 0 R >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
