// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Extraction;

/// <summary>
/// §9.7.4.3: a vertical font's glyphs stand down a column, each drawn at the pen less its position
/// vector and the pen moving down by <c>w1</c>; two columns of three are six glyphs at two x
/// positions and three y positions each, where a layout along x put all six on one line, interleaved.
/// </summary>
public sealed class VerticalWritingExtractionTests
{
    [Fact]
    public void AVerticalRun_StandsDownItsColumn()
    {
        var layout = PdfStructuredTextExtractor.Extract(new MemoryStream(Pdf()), 0);
        var chars = layout.Chars.OrderBy(c => c.Char, System.StringComparer.Ordinal).ToList();
        Assert.Equal(6, chars.Count);

        // The first column: 一 二 三, 10 pt apart down the page, centred on x = 500 (half the
        // 1000-unit advance back from the pen), the first 8.8 pt below the pen's y (Table 117's
        // default position vector).
        var one = Char(layout, "一"); var two = Char(layout, "二"); var three = Char(layout, "三");
        Assert.InRange(one.X, 494, 496);
        Assert.InRange(one.Y, 700 - 8.8f - 0.5f, 700 - 8.8f + 0.5f);
        Assert.InRange(one.Y - two.Y, 9.5f, 10.5f);
        Assert.InRange(two.Y - three.Y, 9.5f, 10.5f);
        Assert.Equal(one.X, two.X, 2);

        // The second column stands 12 pt to the left, not after the first on one line.
        var four = Char(layout, "四");
        Assert.InRange(four.X, 482, 484);
        Assert.InRange(four.Y, one.Y - 0.5f, one.Y + 0.5f);
        Assert.DoesNotContain(layout.Lines, l => l.Text.Contains("一") && l.Text.Contains("四"));
    }

    private static PdfTextChar Char(PdfPageTextLayout layout, string c) => layout.Chars.Single(ch => ch.Char == c);

    /// <summary>Two columns of three CJK glyphs through an Identity-V font with <c>/DW 1000</c> and no <c>/DW2</c>.</summary>
    private static byte[] Pdf()
    {
        const string toUnicode = "/CIDInit /ProcSet findresource begin begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange " +
                                 "6 beginbfchar <0001> <4E00> <0002> <4E8C> <0003> <4E09> <0004> <56DB> <0005> <4E94> <0006> <516D> endbfchar endcmap end end";
        const string content = "BT /F1 10 Tf 1 0 0 1 500 700 Tm <000100020003> Tj 1 0 0 1 488 700 Tm <000400050006> Tj ET";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Vert /Encoding /Identity-V /DescendantFonts [6 0 R] /ToUnicode 7 0 R >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Vert /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 1000 " +
            "/FontDescriptor << /Type /FontDescriptor /FontName /Vert /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 880 /Descent -120 /CapHeight 700 /StemV 80 >> >>",
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
