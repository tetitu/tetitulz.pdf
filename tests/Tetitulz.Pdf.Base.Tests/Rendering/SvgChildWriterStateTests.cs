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
/// A child writer the SVG export opens for a soft-mask group or an uncoloured pattern cell starts
/// with the parent's alpha, blend mode and mask cleared, as the raster's offscreen render does: the
/// alpha is applied once, where the mask or the pattern is used, where the mask's own content under
/// <c>/ca 0.5</c> carried <c>fill-opacity</c> too and masked at a quarter. A stray <c>d1</c> on a
/// page marks nothing uncoloured: only a Type 3 glyph's child drops the colour operators after it.
/// </summary>
public class SvgChildWriterStateTests
{
    [Fact]
    public void ASoftMasksContent_CarriesNoAlphaOfItsOwn()
    {
        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(SoftMaskPdf()), 0);
        XDocument doc = XDocument.Parse(svg);

        XElement mask = Assert.Single(doc.Descendants().Where(e => e.Name.LocalName == "mask"));
        Assert.All(mask.Descendants(), e =>
            Assert.True(Opaque(e), "the mask's content carries an opacity of its own: " + e));

        // The control: the page's own fill under /ca 0.5 does carry it.
        Assert.Contains(doc.Descendants().Where(e => e.Name.LocalName == "path" && !mask.Descendants().Contains(e)),
            e => e.Attribute("fill-opacity")?.Value == "0.5" || e.Attribute("opacity")?.Value == "0.5");
    }

    [Fact]
    public void AnUncolouredPatternCell_CarriesNoAlphaOfItsOwn()
    {
        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(UncolouredPatternPdf()), 0);
        XDocument doc = XDocument.Parse(svg);

        XElement pattern = Assert.Single(doc.Descendants().Where(e => e.Name.LocalName == "pattern"));
        Assert.All(pattern.Descendants(), e =>
            Assert.True(Opaque(e), "the cell carries an opacity of its own: " + e));
        Assert.Contains(doc.Descendants().Where(e => e.Name.LocalName == "path" && !pattern.Descendants().Contains(e)),
            e => e.Attribute("fill-opacity")?.Value == "0.5" || e.Attribute("opacity")?.Value == "0.5");
    }

    [Fact]
    public void AStrayCacheDeviceOnAPage_DoesNotDropTheColourOperatorsAfterIt()
    {
        byte[] pdf = PagePdf("0 0 1 rg 1000 0 0 0 1 1 d1 1 0 0 rg 0 0 100 100 re f", "/Resources << >>");
        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0);

        XElement path = XDocument.Parse(svg).Descendants().First(e => e.Name.LocalName == "path" && e.Attribute("fill") != null);
        Assert.Equal("rgb(255,0,0)", path.Attribute("fill")!.Value);

        // The raster, which the export must agree with: red, whichever way round its channels are.
        RenderedPage page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);
        int at = (page.Height - 1 - 50) * page.Stride + 50 * (page.Stride / page.Width);
        byte c0 = page.Pixels[at], c1 = page.Pixels[at + 1], c2 = page.Pixels[at + 2];
        Assert.True(c1 < 60 && ((c0 > 200 && c2 < 60) || (c2 > 200 && c0 < 60)), $"the raster paints ({c0},{c1},{c2}), not red");
    }

    /// <summary>No opacity of its own: the attribute absent, or 1.</summary>
    private static bool Opaque(XElement e) =>
        (e.Attribute("fill-opacity")?.Value ?? "1") == "1" && (e.Attribute("opacity")?.Value ?? "1") == "1"
        && (e.Attribute("stroke-opacity")?.Value ?? "1") == "1";

    // ------------------------------------------------------------------ fixtures

    private static byte[] SoftMaskPdf() => PagePdf(
        "/GS gs 1 0 0 rg 0 0 100 100 re f",
        "/Resources << /ExtGState << /GS 5 0 R >> >>",
        "<< /Type /ExtGState /ca 0.5 /SMask << /S /Luminosity /G 6 0 R >> >>",
        "<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Group << /S /Transparency /CS /DeviceGray >> /Length 20 >>\nstream\n1 g 0 0 100 100 re f\nendstream");

    private static byte[] UncolouredPatternPdf() => PagePdf(
        "/GS gs /Pattern cs 1 0 0 /P1 scn 0 0 100 100 re f",
        "/Resources << /ExtGState << /GS 5 0 R >> /Pattern << /P1 6 0 R >> /ColorSpace << /Cs [/Pattern /DeviceRGB] >> >>",
        "<< /Type /ExtGState /ca 0.5 >>",
        "<< /PatternType 1 /PaintType 2 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << >> /Length 16 >>\nstream\n0 0 5 5 re f     \nendstream");

    private static byte[] PagePdf(string content, string resources, params string[] extra)
    {
        var bodies = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R " + resources + " >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
        };
        bodies.AddRange(extra);
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
