// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A userSpaceOnUse paint server resolves in the painted element's own space, so an element that
/// carries a transform — a <c>&lt;text&gt;</c> with its text matrix, a stencil's rect with the CTM
/// — takes a tiling pattern composed with that transform's inverse, as the gradient branch already
/// took it: a 24 pt text's pattern carries 1/24, a stencil under <c>72 0 0 72 0 0 cm</c> 1/72. A
/// path, in page space, takes the pattern's own matrix. Glyph outlines, whose <c>&lt;use&gt;</c>
/// children each carry a glyph matrix, are painted through a clip instead, in page space.
/// </summary>
public sealed class SvgTransformedElementPatternTests
{
    [Fact]
    public void ATextRun_TakesThePatternThroughItsMatrixInverse()
    {
        float[] m = PatternTransform(Pdf("/Pattern cs /P1 scn BT /F1 24 Tf 10 50 Td (Hi) Tj ET"));
        Assert.Equal(1f / 24f, m[0], 4);
        Assert.Equal(-1f / 24f, m[3], 4);
    }

    [Fact]
    public void AStencil_TakesThePatternThroughTheCtmInverse()
    {
        float[] m = PatternTransform(Pdf("/Pattern cs /P1 scn q 72 0 0 72 0 0 cm /Stencil Do Q"));
        Assert.Equal(1f / 72f, m[0], 4);
        Assert.Equal(1f / 72f, m[3], 4);
    }

    [Fact]
    public void APath_TakesThePatternsOwnMatrix()
    {
        float[] m = PatternTransform(Pdf("/Pattern cs /P1 scn 0 0 72 72 re f"));
        Assert.Equal(new[] { 1f, 0f, 0f, 1f, 0f, 0f }, m);
    }

    private static float[] PatternTransform(byte[] pdf)
    {
        XDocument svg = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0));
        XElement pattern = Assert.Single(svg.Descendants().Where(e => e.Name.LocalName == "pattern"));
        return Regex.Match(pattern.Attribute("patternTransform")!.Value, @"matrix\(([^)]*)\)").Groups[1].Value
            .Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
    }

    /// <summary>A 72 × 72 page: Helvetica, a 2 × 1 stencil and an 8-unit green tiling pattern.</summary>
    private static byte[] Pdf(string content)
    {
        const string cell = "0 1 0 rg 0 0 8 8 re f";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> /XObject << /Stencil 6 0 R >> /Pattern << /P1 7 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /ImageMask true /BitsPerComponent 1 /Filter /ASCIIHexDecode /Length 3 >>\nstream\n80>\nendstream",
            "<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 8 8] /XStep 8 /YStep 8 /Resources << >> /Length " + cell.Length + " >>\nstream\n" + cell + "\nendstream",
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
