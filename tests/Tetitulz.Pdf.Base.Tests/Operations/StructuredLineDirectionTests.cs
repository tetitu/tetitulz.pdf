// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Under bidi the structured extractor takes a drawn line's direction as the glyph overload and
/// Core's extractor do (<c>UnicodeBidi.VisualLineDirection</c>): its ends' strong characters when
/// they agree, else the more of its strong characters. A line drawn X, Y, then three Hebrew
/// letters is a Hebrew line that ends in "XY", where the first strong character of the visual
/// string made it a Latin line ending in Hebrew.
/// </summary>
public class StructuredLineDirectionTests
{
    [Fact]
    public void ALineWithMoreHebrewThanLatin_ReadsAsAHebrewLine()
    {
        var page = PdfStructuredTextExtractor.Extract(new MemoryStream(Pdf()), new WordAssemblyOptions { ApplyBidi = true })[0];
        string line = string.Concat(page.Lines.Select(l => l.Text));

        Assert.Equal("אבג XY", line);
        Assert.Equal("אבג XY", TextDirection.ToReadingOrder(page.Chars.ToList(), applyBidi: true));
    }

    /// <summary>
    /// X at 0, Y at 8, then ג ב א at 40, 48 and 56 — the three letters drawn in visual order, so
    /// the Hebrew word reads אבג — through a composite font whose ToUnicode names them.
    /// </summary>
    private static byte[] Pdf()
    {
        const string content = "BT /F0 10 Tf 1 0 0 1 0 700 Tm <00010002> Tj 1 0 0 1 40 700 Tm <000300040005> Tj ET";
        const string toUnicode = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /T def " +
            "1 begincodespacerange <0000> <FFFF> endcodespacerange " +
            "5 beginbfchar <0001> <0058> <0002> <0059> <0003> <05D2> <0004> <05D1> <0005> <05D0> endbfchar " +
            "endcmap CMapName currentdict /CMap defineresource pop end end";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F0 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding /Identity-H /DescendantFonts [6 0 R] /ToUnicode 8 0 R >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Test /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
            "/FontDescriptor 7 0 R /DW 800 >>",
            "<< /Type /FontDescriptor /FontName /Test /Flags 4 /ItalicAngle 0 /Ascent 880 /Descent -120 /CapHeight 700 /StemV 80 /FontBBox [0 -120 1000 880] >>",
            "<< /Length " + toUnicode.Length + " >>\nstream\n" + toUnicode + "\nendstream",
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
