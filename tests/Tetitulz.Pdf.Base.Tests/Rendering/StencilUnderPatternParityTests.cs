// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §8.9.6.2: a stencil mask's painted samples take the fill, a pattern included — one answer for
/// the image XObject and the inline image, in both renderers. The raster paints the pattern
/// through the stencil (its painted column is the pattern's red, its clear column stays white);
/// the SVG export paints a rect in the pattern through a luminance mask for the inline stencil as
/// it does for the XObject, where it baked the stale solid colour into the inline one's PNG.
/// </summary>
public sealed class StencilUnderPatternParityTests
{
    /// <summary>A 4 × 4 stencil whose rows are 1010: under the default sense a 0 bit paints, so columns 1 and 3 are painted.</summary>
    private const string Rows = "A0A0A0A0>";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheRaster_PaintsThePatternThroughTheStencil(bool inline)
    {
        using SKBitmap bmp = PixelAssert.Render(Pdf(inline));
        SKColor painted = PixelAssert.At(bmp, 37.5f, 50f);
        SKColor clear = PixelAssert.At(bmp, 12.5f, 50f);
        Assert.True(painted.Red > 200 && painted.Green < 60 && painted.Blue < 60, $"a painted sample is not the pattern's red: {painted}");
        Assert.True(PixelAssert.IsWhite(clear), $"a clear sample is painted: {clear}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheSvg_PaintsThePatternThroughAMask(bool inline)
    {
        XDocument svg = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(Pdf(inline)), 0));
        XElement rect = Assert.Single(svg.Descendants().Where(e => e.Name.LocalName == "rect" && e.Attribute("mask") != null));
        Assert.StartsWith("url(#", rect.Attribute("fill")!.Value);
        Assert.Contains(svg.Descendants(), e => e.Name.LocalName == "pattern");
        Assert.DoesNotContain(svg.Descendants(), e => e.Name.LocalName == "image" && e.Parent?.Name.LocalName != "mask");
    }

    /// <summary>A 100 × 100 page under a red tiling pattern, the stencil drawn over the page, as an XObject or inline.</summary>
    private static byte[] Pdf(bool inline)
    {
        string draw = inline
            ? "BI /W 4 /H 4 /IM true /BPC 1 /F /AHx ID " + Rows + " EI"
            : "/Im1 Do";
        string content = "/Pattern cs /P0 scn q 100 0 0 100 0 0 cm " + draw + " Q";
        const string cell = "1 0 0 rg 0 0 100 100 re f";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /Pattern << /P0 5 0 R >> /XObject << /Im1 6 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 100 100] /XStep 100 /YStep 100 /Resources << >> /Length " + cell.Length + " >>\nstream\n" + cell + "\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 4 /Height 4 /ImageMask true /BitsPerComponent 1 /Filter /ASCIIHexDecode /Length " + Rows.Length + " >>\nstream\n" + Rows + "\nendstream",
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
