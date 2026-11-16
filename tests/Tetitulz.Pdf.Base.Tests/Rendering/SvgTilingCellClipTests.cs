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
/// An SVG tiling cell is clipped to its <c>/BBox</c> when the step exceeds the box (§8.7.3.1): the
/// tile is the step, so content the cell draws between the box and the step showed, where the
/// raster's cell bitmap is the box. The control, a box the size of its step, needs no clip.
/// </summary>
public class SvgTilingCellClipTests
{
    [Theory]
    [InlineData("[0 0 4 4]", true)]
    [InlineData("[0 0 10 10]", false)]
    public void ACellSmallerThanItsStep_IsClippedToItsBox(string bbox, bool clipped)
    {
        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(Pdf(bbox)), 0);
        XElement pattern = Assert.Single(XDocument.Parse(svg).Descendants().Where(e => e.Name.LocalName == "pattern"));

        var clipRects = pattern.Descendants().Where(e => e.Name.LocalName == "clipPath")
            .SelectMany(c => c.Elements().Where(r => r.Name.LocalName == "rect")).ToList();
        if (clipped)
        {
            XElement rect = Assert.Single(clipRects);
            Assert.Equal("4", rect.Attribute("width")?.Value);
            Assert.Equal("4", rect.Attribute("height")?.Value);
            Assert.Contains(pattern.Descendants(), e => e.Name.LocalName == "g" && e.Attribute("clip-path") != null);
        }
        else
        {
            Assert.Empty(clipRects);
        }
        // The cell's own path is there either way.
        Assert.Contains(pattern.Descendants(), e => e.Name.LocalName == "path");
    }

    private static byte[] Pdf(string bbox)
    {
        const string content = "/Pattern cs /P1 scn 0 0 100 100 re f";
        const string cell = "0 0 8 8 re f";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /Pattern << /P1 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /PatternType 1 /PaintType 1 /TilingType 1 /BBox " + bbox + " /XStep 10 /YStep 10 /Resources << >> /Length " + cell.Length + " >>\nstream\n" + cell + "\nendstream",
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
