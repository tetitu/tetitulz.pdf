// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Both extractors take a Type 3 glyph's width through the font's <c>/FontMatrix</c>, as the
/// renderers do (§9.6.5): a width of 50 under <c>[0.01 0 0 0.01 0 0]</c> is half a text unit, where
/// read as thousandths it was a twentieth — so a glyph drawn right after it read as a word apart.
/// </summary>
public class Type3WidthExtractionTests
{
    [Fact]
    public void AType3Width_IsTakenThroughTheFontMatrix()
    {
        byte[] pdf = Pdf();

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.Equal("AB", PdfTextExtractor.ExtractText(doc).Trim());

        var chars = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf))[0].Chars;
        Assert.Equal(5f, chars.First(c => c.Char == "A").Width, 2);
        Assert.Equal("AB", string.Concat(PdfStructuredTextExtractor.Extract(new MemoryStream(pdf))[0].Lines.Select(l => l.Text)).Trim());
    }

    /// <summary>
    /// A Type 3 font at a 100-unit glyph space whose A and B are 50 units wide (5 points at 10 pt);
    /// B is drawn 5 points after A's origin, so the two touch.
    /// </summary>
    private static byte[] Pdf()
    {
        const string content = "BT /F3 10 Tf 100 700 Td (A) Tj 5 0 Td (B) Tj ET\n";
        const string glyph = "50 0 d0 0 0 40 80 re f";
        const string cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /X def " +
                            "1 begincodespacerange <00> <FF> endcodespacerange 2 beginbfchar <41> <0041> <42> <0042> endbfchar " +
                            "endcmap CMapName currentdict /CMap defineresource pop end end";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F3 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 100 100] /FontMatrix [0.01 0 0 0.01 0 0] " +
            "/CharProcs << /A 6 0 R /B 6 0 R >> /Encoding << /Type /Encoding /Differences [65 /A /B] >> " +
            "/FirstChar 65 /LastChar 66 /Widths [50 50] /Resources << >> /ToUnicode 7 0 R >>",
            "<< /Length " + glyph.Length + " >>\nstream\n" + glyph + "\nendstream",
            "<< /Length " + cmap.Length + " >>\nstream\n" + cmap + "\nendstream",
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
