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
/// §9.3.6: Type 3 text in render mode 3 or 7 is not painted. Both renderers advance past such a
/// glyph without running its procedure, where they painted it: the first glyph, in mode 3, leaves
/// its square white, and the second, in mode 0, is drawn where the first advanced to.
/// </summary>
public class Type3InvisibleTextTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    public void AType3GlyphInAnInvisibleMode_IsNotPainted_AndStillAdvances(int mode)
    {
        byte[] pdf = Pdf(mode);

        RenderedPage page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);
        Assert.True(Dark(page, 35, 55), "the second glyph, in mode 0, is painted at the advanced position");
        Assert.False(Dark(page, 15, 55), "the first glyph, in an invisible mode, is painted");

        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0);
        var paths = XDocument.Parse(svg).Descendants().Where(e => e.Name.LocalName == "path" && (e.Attribute("fill")?.Value ?? "none") != "none").ToList();
        Assert.Single(paths);
    }

    private static bool Dark(RenderedPage page, int x, int yFromBottom)
    {
        int bytes = page.Stride / page.Width;
        int at = (page.Height - 1 - yFromBottom) * page.Stride + x * bytes;
        return page.Pixels[at] < 128 && page.Pixels[at + 1] < 128 && page.Pixels[at + 2] < 128;
    }

    /// <summary>A Type 3 font whose A fills its 1000-unit em; shown twice at 20 pt, the first in <paramref name="mode"/>.</summary>
    private static byte[] Pdf(int mode)
    {
        string content = $"BT /T3 20 Tf {mode} Tr 10 50 Td (A) Tj 0 Tr (A) Tj ET";
        const string glyph = "1000 0 0 0 1000 1000 d1 0 0 1000 1000 re f";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /Font << /T3 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] " +
            "/CharProcs << /A 6 0 R >> /Encoding << /Type /Encoding /Differences [65 /A] >> /FirstChar 65 /LastChar 65 /Widths [1000] /Resources << >> >>",
            "<< /Length " + glyph.Length + " >>\nstream\n" + glyph + "\nendstream",
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
