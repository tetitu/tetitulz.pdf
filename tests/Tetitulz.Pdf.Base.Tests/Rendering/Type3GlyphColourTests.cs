// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

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
/// §9.6.5: a Type 3 glyph whose procedure begins with <c>d1</c> is uncoloured — its colour operators
/// are ignored and it paints in the colour of the text that showed it; a <c>d0</c> glyph keeps its
/// own. Both renderers, through <c>Type3GlyphColour</c>.
/// </summary>
/// <remarks>
/// The glyph sets red and the text is shown in blue. Neither renderer had the rule, so the
/// <c>d1</c> glyph painted red — the colour the font is not allowed to choose.
/// </remarks>
public class Type3GlyphColourTests
{
    [Fact]
    public void AD1GlyphPaintsInTheTextsColour()
    {
        byte[] pdf = Pdf("100 0 0 0 100 100 d1 1 0 0 rg 0 0 100 100 re f");

        using var bmp = PixelAssert.Render(pdf);
        Assert.Equal(new SKColor(0, 0, 255), PixelAssert.At(bmp, 35, 35));
        Assert.Contains("rgb(0,0,255)", SvgFills(pdf));
        Assert.DoesNotContain("rgb(255,0,0)", SvgFills(pdf));
    }

    /// <summary>The control: a <c>d0</c> glyph is coloured, and its own red stands in both.</summary>
    [Fact]
    public void AD0GlyphKeepsItsOwnColour()
    {
        byte[] pdf = Pdf("100 0 d0 1 0 0 rg 0 0 100 100 re f");

        using var bmp = PixelAssert.Render(pdf);
        Assert.Equal(new SKColor(255, 0, 0), PixelAssert.At(bmp, 35, 35));
        Assert.Contains("rgb(255,0,0)", SvgFills(pdf));
    }

    private static string[] SvgFills(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(ms, 0)).Descendants()
            .Select(e => e.Attribute("fill")?.Value).Where(v => v != null).ToArray();
    }

    /// <summary>A 200 × 100 page showing glyph /a of a Type 3 font in blue, at 50 pt from (10, 10).</summary>
    private static byte[] Pdf(string charProc)
    {
        const string content = "0 0 1 rg BT /T3 50 Tf 10 10 Td (A) Tj ET";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R /Resources << /Font << /T3 5 0 R >> >> >>",
            Stream(content),
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 100 100] /FontMatrix [0.01 0 0 0.01 0 0]"
                + " /CharProcs << /a 6 0 R >> /Encoding << /Type /Encoding /Differences [65 /a] >>"
                + " /FirstChar 65 /LastChar 65 /Widths [100] >>",
            Stream(charProc),
        };

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
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Stream(string data) =>
        string.Format(CultureInfo.InvariantCulture, "<< /Length {0} >>\nstream\n{1}\nendstream", data.Length, data);
}
