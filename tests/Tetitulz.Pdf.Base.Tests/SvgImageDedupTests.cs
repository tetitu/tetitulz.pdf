// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests that a repeated image XObject is emitted once into &lt;defs&gt; and referenced via
/// &lt;use&gt; thereafter, instead of re-encoding the full base-64 bitmap on every draw.
/// </summary>
public sealed class SvgImageDedupTests
{
    [Fact]
    public void RepeatedImageXObject_DefinedOnce_ReferencedTwice()
    {
        byte[] pdf = BuildPdfWithImageDrawnTwice();
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        // One inline data URI (the def), referenced by two <use>s.
        Assert.Equal(1, CountOccurrences(svg, "data:image/png;base64,"));
        Assert.Equal(1, CountOccurrences(svg, "<defs><image id=\"img"));
        Assert.Equal(2, CountOccurrences(svg, "<use transform="));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    // 2×2 DeviceRGB image (red, green, blue, white) drawn at two different positions.
    private static byte[] BuildPdfWithImageDrawnTwice()
    {
        const string hex = "FF000000FF000000FFFFFFFF>"; // 12 RGB bytes, ASCIIHexDecode
        const string content =
            "q 100 0 0 100 50 50 cm /Im1 Do Q q 100 0 0 100 300 300 cm /Im1 Do Q";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o5 = sb.Length;
        sb.AppendFormat(
            "5 0 obj\n<< /Type /XObject /Subtype /Image /Width 2 /Height 2" +
            " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /ASCIIHexDecode" +
            " /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", hex.Length, hex);
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /XObject << /Im1 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            content.Length, content);
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
