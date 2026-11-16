// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A stencil mask is decoded with the fill colour in force, so its bitmap is not one the image
/// cache may hand to a later draw under another colour: one <c>/ImageMask</c> drawn red, then
/// blue, draws blue the second time, where the cached first decode drew it red again.
/// </summary>
public class StencilImageCacheTests
{
    [Fact]
    public void AStencilDrawnTwiceInTwoColours_TakesEachColour()
    {
        RenderedPage page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(Pdf()), 0, 72);

        // The channel order is not assumed: the first draw is red by construction, so the bright
        // channel there is red's, and the second draw must be bright in the other outer channel.
        var first = At(page, 20, 20);
        var second = At(page, 60, 20);
        int redIndex = first.c0 > first.c2 ? 0 : 2, blueIndex = 2 - redIndex;

        Assert.True(Channel(first, redIndex) > 200 && Channel(first, blueIndex) < 60 && first.c1 < 60,
            $"the first draw is red: ({first.c0},{first.c1},{first.c2})");
        Assert.True(Channel(second, blueIndex) > 200 && Channel(second, redIndex) < 60 && second.c1 < 60,
            $"the second draw is blue, not the first draw's red: ({second.c0},{second.c1},{second.c2})");
    }

    private static (byte c0, byte c1, byte c2) At(RenderedPage page, int x, int yFromBottom)
    {
        int bytes = page.Stride / page.Width;
        int at = (page.Height - 1 - yFromBottom) * page.Stride + x * bytes;
        return (page.Pixels[at], page.Pixels[at + 1], page.Pixels[at + 2]);
    }

    private static byte Channel((byte c0, byte c1, byte c2) p, int index) => index == 0 ? p.c0 : index == 1 ? p.c1 : p.c2;

    /// <summary>An 8 × 8 all-ink stencil drawn at (10,10) in red and at (50,10) in blue, 20 points each.</summary>
    private static byte[] Pdf()
    {
        const string content = "1 0 0 rg q 20 0 0 20 10 10 cm /M Do Q 0 0 1 rg q 20 0 0 20 50 10 cm /M Do Q";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /XObject << /M 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 8 /Height 8 /ImageMask true /BitsPerComponent 1 /Filter /ASCIIHexDecode /Length 17 >>\nstream\n0000000000000000>\nendstream",
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
