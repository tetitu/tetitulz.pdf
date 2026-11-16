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
/// A Type 3 glyph's fill is the text's fill, a pattern included (§9.6.5: a d1 glyph takes the
/// colour from the graphics state). The raster paints the glyph in the pattern's green; the SVG
/// export's glyph path is filled with the pattern, where its child writer cleared the selection
/// and painted the solid colour the state held before the pattern was selected — black.
/// </summary>
public sealed class Type3UnderPatternSvgTests
{
    [Fact]
    public void AType3Glyph_IsFilledWithTheTextsPattern_InBothRenderers()
    {
        byte[] pdf = Pdf();

        using SKBitmap bmp = PixelAssert.Render(pdf);
        SKColor centre = PixelAssert.At(bmp, 35, 35);
        Assert.True(centre.Green > 200 && centre.Red < 60, $"the raster glyph is not the pattern's green: {centre}");

        XDocument svg = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0));
        // The glyph's paths: the pattern's own cell, inside its <pattern>, is painted green.
        var glyphPaths = svg.Descendants().Where(e => e.Name.LocalName == "path" && (e.Attribute("fill")?.Value ?? "none") != "none"
                                                      && !e.Ancestors().Any(a => a.Name.LocalName == "pattern")).ToList();
        Assert.NotEmpty(glyphPaths);
        Assert.All(glyphPaths, p => Assert.StartsWith("url(#", p.Attribute("fill")!.Value));
    }

    /// <summary>A Type 3 font with no /Resources whose glyph fills its em, shown at 50 pt under a green tiling pattern.</summary>
    private static byte[] Pdf()
    {
        const string content = "/Pattern cs /P1 scn BT /T3 50 Tf 10 10 Td (a) Tj ET";
        const string glyph = "1000 0 0 0 1000 1000 d1 0 0 1000 1000 re f";
        const string cell = "0 1 0 rg 0 0 100 100 re f";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /Font << /T3 5 0 R >> /Pattern << /P1 7 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /a 6 0 R >> " +
            "/Encoding << /Type /Encoding /Differences [97 /a] >> /FirstChar 97 /LastChar 97 /Widths [1000] >>",
            "<< /Length " + glyph.Length + " >>\nstream\n" + glyph + "\nendstream",
            "<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 100 100] /XStep 100 /YStep 100 /Resources << >> /Length " + cell.Length + " >>\nstream\n" + cell + "\nendstream",
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
