// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The structured extractor measures a composite font's glyph as the renderers do: word spacing
/// on the single-byte code 32 alone (§9.3.3), never on a two-byte code that decodes to a space,
/// and the width by the code's CID (§9.7.4.3), not by the raw code, which differ under any
/// encoding CMap but Identity.
/// </summary>
public class StructuredTextCompositeWidthTests
{
    /// <summary>A two-byte code mapped to U+0020 is no word-space code: Tw does not widen it.</summary>
    [Fact]
    public void WordSpacing_IsNotAppliedToATwoByteCodeThatDecodesToASpace()
    {
        byte[] pdf = IdentityPdf("BT /F0 10 Tf 5 Tw 100 700 Td <000100030002> Tj ET");

        var chars = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf))[0].Chars;

        // 500 + 250 units at 10 pt: the third glyph starts 7.5 pt along, not 12.5.
        Assert.Equal(3, chars.Count);
        Assert.Equal(107.5f, chars[2].X, 2);
    }

    /// <summary>The control: the single-byte code 32 of a simple font takes Tw.</summary>
    [Fact]
    public void WordSpacing_IsAppliedToTheSingleByteCode32()
    {
        byte[] pdf = SimplePdf("BT /F1 10 Tf 5 Tw 100 700 Td (A B) Tj ET");

        var chars = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf))[0].Chars;
        var b = chars.First(c => c.Char == "B");

        // A 500, space 500, plus the 5 pt of Tw: B starts at 100 + 5 + 5 + 5.
        Assert.Equal(115f, b.X, 2);
    }

    /// <summary>Under a CMap that is not Identity the width is the CID's, not the code's.</summary>
    [Fact]
    public void AWidth_IsReadByTheCodesCid_NotByTheRawCode()
    {
        byte[] pdf = CMapPdf("BT /F0 10 Tf 100 700 Td <4142> Tj ET");

        var chars = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf))[0].Chars;

        Assert.Equal(new[] { "A", "B" }, chars.Select(c => c.Char));
        Assert.Equal(102.5f, chars[1].X, 2);   // CID 34 is 250 units, where code 65 is 900
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>Identity-H; CIDs 1, 2, 3 are 500, 500 and 250 units; CID 3 decodes to a space.</summary>
    private static byte[] IdentityPdf(string content)
    {
        const string toUnicode = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /T def " +
            "1 begincodespacerange <0000> <FFFF> endcodespacerange 3 beginbfchar <0001> <0041> <0002> <0042> <0003> <0020> endbfchar " +
            "endcmap CMapName currentdict /CMap defineresource pop end end";
        return Build(content,
            "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding /Identity-H /DescendantFonts [6 0 R] /ToUnicode 8 0 R >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Test /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
            "/FontDescriptor 7 0 R /DW 1000 /W [1 [500 500 250]] >>",
            toUnicode, encodingCMap: null);
    }

    /// <summary>An embedded one-byte CMap maps codes 0x41 and 0x42 to CIDs 34 and 35, 250 units each; CID 65 is 900.</summary>
    private static byte[] CMapPdf(string content)
    {
        const string toUnicode = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /T def " +
            "1 begincodespacerange <00> <FF> endcodespacerange 2 beginbfchar <41> <0041> <42> <0042> endbfchar " +
            "endcmap CMapName currentdict /CMap defineresource pop end end";
        const string encoding = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap " +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> def /CMapName /E def /CMapType 1 def " +
            "1 begincodespacerange <00> <FF> endcodespacerange 1 begincidrange <41> <42> 34 endcidrange " +
            "endcmap CMapName currentdict /CMap defineresource pop end end";
        return Build(content,
            "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding 9 0 R /DescendantFonts [6 0 R] /ToUnicode 8 0 R >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Test /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
            "/FontDescriptor 7 0 R /DW 1000 /W [34 [250 250] 65 [900]] >>",
            toUnicode, encoding);
    }

    private static byte[] SimplePdf(string content) => Build(content,
        "<< /Type /Font /Subtype /TrueType /BaseFont /Test /FirstChar 32 /LastChar 66 /Widths [500 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 500 500] /Encoding /WinAnsiEncoding /FontDescriptor 7 0 R >>",
        "null", null, null, fontName: "F1");

    private static byte[] Build(string content, string font, string descendant, string toUnicode, string encodingCMap, string fontName = "F0")
    {
        var bodies = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /" + fontName + " 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            font,
            descendant,
            "<< /Type /FontDescriptor /FontName /Test /Flags 4 /ItalicAngle 0 /Ascent 880 /Descent -120 /CapHeight 700 /StemV 80 /FontBBox [0 -120 1000 880] >>",
            toUnicode == null ? "null" : "<< /Length " + toUnicode.Length + " >>\nstream\n" + toUnicode + "\nendstream",
            encodingCMap == null ? "null" : "<< /Type /CMap /CMapName /E /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /Length " + encodingCMap.Length + " >>\nstream\n" + encodingCMap + "\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
