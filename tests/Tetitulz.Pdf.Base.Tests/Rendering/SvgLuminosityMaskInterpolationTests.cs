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
/// A luminosity soft mask's value is the luminance of its group's device colours with no gamma
/// compensation (§11.6.5.2), which the raster takes as BT.601 luma of the sRGB values; the SVG
/// <c>&lt;mask&gt;</c> therefore interpolates in sRGB, where <c>linearRGB</c> made a mid-grey mask
/// about a fifth instead of a half.
/// </summary>
public class SvgLuminosityMaskInterpolationTests
{
    [Fact]
    public void ALuminosityMask_IsNotInterpolatedInLinearLight()
    {
        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(Pdf()), 0);
        XElement mask = Assert.Single(XDocument.Parse(svg).Descendants().Where(e => e.Name.LocalName == "mask"));

        string interpolation = mask.Attribute("color-interpolation")?.Value;
        Assert.True(interpolation == null || interpolation == "sRGB", "the mask interpolates in " + interpolation);
        Assert.DoesNotContain("mask-type:alpha", mask.Attribute("style")?.Value ?? "");

        // And the raster masks the red square to about half.
        RenderedPage page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(Pdf()), 0, 72);
        int at = (page.Height - 1 - 50) * page.Stride + 50 * (page.Stride / page.Width);
        Assert.InRange(page.Pixels[at + 1], 100, 156);   // green: white backdrop through half of red
    }

    private static byte[] Pdf()
    {
        const string content = "/GS gs 1 0 0 rg 0 0 100 100 re f";
        const string group = "0.5 g 0 0 100 100 re f";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /ExtGState << /GS 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /ExtGState /SMask << /S /Luminosity /G 6 0 R >> >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Group << /S /Transparency /CS /DeviceGray >> /Length " + group.Length + " >>\nstream\n" + group + "\nendstream",
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
