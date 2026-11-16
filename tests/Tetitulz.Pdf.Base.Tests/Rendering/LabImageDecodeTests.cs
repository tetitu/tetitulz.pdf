// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A Lab image's explicit <c>/Decode</c> (Table 89) maps its samples: a sample of 255 under
/// <c>/Decode [0 50 …]</c> is L* 50, a mid grey, where the default decode made it white. The
/// control, no <c>/Decode</c>, is white.
/// </summary>
public class LabImageDecodeTests
{
    [Theory]
    [InlineData("/Decode [0 50 -128 127 -128 127]", 90, 150)]
    [InlineData("", 250, 255)]
    public void ALabImagesDecode_MapsItsSamples(string decode, int min, int max)
    {
        RenderedPage page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(Pdf(decode)), 0, 72);

        int bytes = page.Stride / page.Width;
        int at = (page.Height - 1 - 50) * page.Stride + 50 * bytes;
        Assert.InRange(page.Pixels[at], min, max);
        Assert.InRange(page.Pixels[at + 1], min, max);
        Assert.InRange(page.Pixels[at + 2], min, max);
    }

    /// <summary>A 1×1 Lab image with samples FF 80 80 drawn over the 100 × 100 page.</summary>
    private static byte[] Pdf(string decode)
    {
        const string content = "q 100 0 0 100 0 0 cm /Im1 Do Q";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /XObject << /Im1 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 " +
            "/ColorSpace [/Lab << /WhitePoint [0.9642 1 0.8249] /Range [-128 127 -128 127] >>] " + decode +
            " /Filter /ASCIIHexDecode /Length 7 >>\nstream\nFF8080>\nendstream",
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
