// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// §9.7.5/§9.7.6.2: a composite font's show-string bytes are split into character codes by its
/// <b>/Encoding</b> CMap. <c>/ToUnicode</c> is an optional extraction aid whose codespace may
/// disagree, and which says nothing about code→CID.
/// </summary>
/// <remarks>
/// <para>The splitting existed <b>twice</b>: <see cref="CMapCodec"/> (driven by /Encoding, used by
/// both renderers) and a second copy inside <c>PdfType0Font.Decode</c> that took its codespace
/// ranges from <c>/ToUnicode</c> and otherwise fell back to a fixed 2-byte stride. On a font whose
/// two CMaps disagree the renderers split one way and extraction the other, so the extracted text
/// was not the text that was drawn.</para>
///
/// <para><b>Three numbers, not one.</b> The width fixture below gives CID 100 and CID 101 real
/// <c>/W</c> entries <i>and</i> gives the raw code values 65/66 different ones, so the measured
/// advance separates all three candidate implementations: splitting by /Encoding and looking up by
/// CID (9.5), splitting by /Encoding but looking up by raw code (0.3), and the /ToUnicode split
/// that sees one 2-byte code with no <c>/W</c> entry at all (10.0 from <c>/DW</c>).</para>
///
/// <para><b>The two numbers must not be conflated.</b> A code's <i>value</i> is what /ToUnicode is
/// keyed by; its <i>CID</i> is what /W and the glyph selection are keyed by. They are equal only
/// under Identity-H, which is why conflating them survives most fixtures.</para>
/// </remarks>
public class CompositeCodeSplittingTests
{
    // ------------------------------------------------------------------
    //  CMaps
    // ------------------------------------------------------------------

    /// <summary>1-byte codespace; codes 0x41..0x5A ('A'..'Z') map to CIDs 100..125.</summary>
    private const string OneByteEncoding =
        "/CIDInit /ProcSet findresource begin\n" +
        "12 dict begin\nbegincmap\n" +
        "/CMapName /Test-OneByte def\n/CMapType 1 def\n" +
        "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
        "1 begincidrange\n<41> <5A> 100\nendcidrange\n" +
        "endcmap\nend\nend\n";

    /// <summary>
    /// A 2-byte /ToUnicode over a 1-byte /Encoding — the disagreement. It carries an entry for the
    /// concatenated 2-byte code (0x4142), for each single-byte code (0x41, 0x42), and for 0x64,
    /// which is the <b>CID</b> code 0x41 maps to. Each wrong split or wrong key yields a different
    /// string.
    /// </summary>
    private const string DisagreeingToUnicode =
        "/CIDInit /ProcSet findresource begin\n" +
        "12 dict begin\nbegincmap\n" +
        "/CMapName /Test-ToUni def\n/CMapType 2 def\n" +
        "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
        "4 beginbfchar\n" +
        "<4142> <2603>\n" +   // the 2-byte code the /ToUnicode split produces -> U+2603 SNOWMAN
        "<41> <0058>\n" +     // raw code 0x41 -> 'X'
        "<42> <0059>\n" +     // raw code 0x42 -> 'Y'
        "<64> <005A>\n" +     // 0x64 == 100 == the CID of code 0x41 -> 'Z'
        "endbfchar\n" +
        "endcmap\nend\nend\n";

    /// <summary>Mixed-width codespace, RKSJ-shaped: 1 byte below 0x81, 2 bytes from 0x8140.</summary>
    private const string MixedWidthEncoding =
        "/CIDInit /ProcSet findresource begin\n" +
        "12 dict begin\nbegincmap\n" +
        "/CMapName /Test-Mixed def\n/CMapType 1 def\n" +
        "2 begincodespacerange\n<00> <80>\n<8140> <9FFC>\nendcodespacerange\n" +
        "2 begincidrange\n<20> <7E> 1\n<8140> <817E> 633\nendcidrange\n" +
        "endcmap\nend\nend\n";

    /// <summary>2-byte /ToUnicode for the mixed font — deliberately NOT mixed, so it disagrees.</summary>
    private const string MixedFontToUnicode =
        "/CIDInit /ProcSet findresource begin\n" +
        "12 dict begin\nbegincmap\n/CMapType 2 def\n" +
        "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
        "2 beginbfchar\n" +
        "<41> <0041>\n" +     // 1-byte code 0x41 -> 'A'
        "<8140> <30A2>\n" +   // 2-byte code 0x8140 -> 'ア'
        "endbfchar\n" +
        "endcmap\nend\nend\n";

    /// <summary>2-byte /ToUnicode for the Identity-H control.</summary>
    private const string IdentityToUnicode =
        "/CIDInit /ProcSet findresource begin\n" +
        "12 dict begin\nbegincmap\n/CMapType 2 def\n" +
        "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
        "2 beginbfchar\n<0041> <0058>\n<0042> <0059>\nendbfchar\n" +
        "endcmap\nend\nend\n";

    /// <summary>
    /// CID 100 → 700, CID 101 → 250; the raw code values 65/66 carry deliberately different
    /// widths so a CID/code confusion is visible in the advance.
    /// </summary>
    private const string DistinguishingWidths = "[65 [10] 66 [20] 100 [700] 101 [250]]";

    // ------------------------------------------------------------------
    //  The defect
    // ------------------------------------------------------------------

    /// <summary>
    /// <b>The renderers' splitter.</b> Establishes what the correct split of these bytes is, from
    /// the same /Encoding CMap the font carries: two one-byte codes, mapping to CIDs 100 and 101.
    /// </summary>
    [Fact]
    public void TheEncodingCMapSplitsTheBytesIntoTwoSingleByteCodes()
    {
        var codes = CMapCodec.Parse(Encoding.ASCII.GetBytes(OneByteEncoding))
                             .Decode(new byte[] { 0x41, 0x42 });

        Assert.Equal(2, codes.Count);
        Assert.Equal(new[] { 1, 1 }, new[] { codes[0].ByteLength, codes[1].ByteLength });
        Assert.Equal(new[] { 0x41, 0x42 }, new[] { codes[0].Value, codes[1].Value });
        Assert.Equal(new[] { 100, 101 }, new[] { codes[0].Cid, codes[1].Cid });
    }

    /// <summary>
    /// <b>The defect.</b> Extraction must split on the same /Encoding codespace the renderers use.
    /// Splitting on /ToUnicode's 2-byte codespace instead yields the single code 0x4142 and
    /// extracts "☃"; keying /ToUnicode by the CID instead of the code value yields "Ze".
    /// </summary>
    [Fact]
    public void ExtractionSplitsOnTheEncodingCodespaceNotTheToUnicodeOne()
    {
        Assert.Equal("XY", Extract(BuildPdf("7 0 R", OneByteEncoding, DisagreeingToUnicode,
            DistinguishingWidths, "4142")));
    }

    /// <summary>
    /// <b>The care note.</b> /ToUnicode is keyed by the character <i>code</i>, /W by the <i>CID</i>.
    /// Code 0x41 maps to CID 100 (= 0x64), and the fixture maps both — so an implementation that
    /// looked /ToUnicode up by CID would extract 'Z' here instead of 'X'.
    /// </summary>
    [Fact]
    public void ToUnicodeIsKeyedByTheCodeValueAndNotByTheCid()
    {
        Assert.Equal("X", Extract(BuildPdf("7 0 R", OneByteEncoding, DisagreeingToUnicode,
            DistinguishingWidths, "41")));
    }

    /// <summary>
    /// A mixed-width /Encoding (1-byte ASCII + 2-byte CJK) splits per code. A fixed stride cannot
    /// represent it at all: the 2-byte /ToUnicode codespace pairs the ASCII byte with the CJK lead
    /// byte and extracts "䆁@".
    /// </summary>
    [Fact]
    public void AMixedWidthEncodingSplitsOneByteAndTwoByteCodesInOneString()
    {
        Assert.Equal("Aア", Extract(BuildPdf("7 0 R", MixedWidthEncoding, MixedFontToUnicode,
            "[]", "418140")));
    }

    /// <summary>
    /// <b>The floor.</b> Identity-H — the overwhelmingly common case, where code == CID and both
    /// splitters already agreed — must be unchanged.
    /// </summary>
    [Fact]
    public void IdentityHStillSplitsIntoTwoByteCodes()
    {
        Assert.Equal("XY", Extract(BuildPdf("/Identity-H", "", IdentityToUnicode,
            "[65 [10] 66 [20]]", "00410042")));
    }

    /// <summary>
    /// <b>The two splitters are one.</b> The codes the extractor's font hands out for these bytes
    /// are the codes <see cref="CMapCodec"/> — the renderers' splitter — produces from the same
    /// /Encoding CMap: same count, same values, same CIDs, same widths.
    /// </summary>
    [Fact]
    public void TheFontSplitsExactlyAsTheRenderersSplitterDoes()
    {
        byte[] bytes = { 0x41, 0x42 };
        using var ms = new MemoryStream(BuildPdf("7 0 R", OneByteEncoding, DisagreeingToUnicode,
            DistinguishingWidths, "4142"));
        using var doc = PdfDocument.Open(ms);

        var viaFont = LoadFont(doc).SplitCodes(bytes);
        var viaCodec = CMapCodec.Parse(Encoding.ASCII.GetBytes(OneByteEncoding)).Decode(bytes);

        Assert.Equal(viaCodec.Count, viaFont.Count);
        for (int i = 0; i < viaCodec.Count; i++)
        {
            Assert.Equal(viaCodec[i].Value, viaFont[i].Value);
            Assert.Equal(viaCodec[i].Cid, viaFont[i].Cid);
            Assert.Equal(viaCodec[i].ByteLength, viaFont[i].ByteLength);
        }
    }

    /// <summary>A simple font's codes are single bytes, and its code value is its CID (§9.6.6).</summary>
    [Fact]
    public void ASimpleFontSplitsOneCodePerByte()
    {
        var codes = new FallbackFont().SplitCodes(new byte[] { 0x41, 0x81 });

        Assert.Equal(2, codes.Count);
        Assert.Equal(new[] { 1, 1 }, new[] { codes[0].ByteLength, codes[1].ByteLength });
        Assert.Equal(new[] { 0x41, 0x81 }, new[] { codes[0].Value, codes[1].Value });
        Assert.Equal(new[] { 0x41, 0x81 }, new[] { codes[0].Cid, codes[1].Cid });
    }

    /// <summary>
    /// An <c>/Encoding</c> naming a predefined CMap the host never registered still has to produce
    /// <i>some</i> split, and Identity is the only available guess — but "the file said nothing"
    /// and "the file named a CMap we do not have" are then indistinguishable in the output, so the
    /// fallback is reported. Identity-H is honoured for real and reports nothing.
    /// </summary>
    [Fact]
    public void AnUnregisteredPredefinedCMapFallsBackToIdentityAndSaysSo()
    {
        using (var ms = new MemoryStream(BuildPdf("/UniJIS-UCS2-H-NOTREGISTERED", "",
            IdentityToUnicode, "[]", "0041")))
        using (var doc = PdfDocument.Open(ms))
        {
            LoadFont(doc);
            Assert.Contains("UniJIS-UCS2-H-NOTREGISTERED", CMapCodec.LastEncodingError);
        }

        using (var ms = new MemoryStream(BuildPdf("/Identity-H", "", IdentityToUnicode, "[]", "0041")))
        using (var doc = PdfDocument.Open(ms))
        {
            LoadFont(doc);
            Assert.Null(CMapCodec.LastEncodingError);
        }
    }

    // ------------------------------------------------------------------
    //  Measured advances
    // ------------------------------------------------------------------

    /// <summary>
    /// <b>Three implementations, three numbers.</b> /W is a CID table, so codes 0x41/0x42 advance
    /// by the widths of CIDs 100 and 101 — 9.5 pt at 10 pt type. Keying /W by the raw code value
    /// instead gives 0.3; the /ToUnicode split sees one 2-byte code with no /W entry and falls back
    /// to /DW for 10.0.
    /// </summary>
    [Fact]
    public void TheAdvanceUsesTheCidWidthAndNotTheRawCodeWidth()
    {
        Assert.Equal(9.5, Advance(BuildPdf("7 0 R", OneByteEncoding, DisagreeingToUnicode,
            DistinguishingWidths, "4142"), new byte[] { 0x41, 0x42 }), 6);
    }

    /// <summary>
    /// A mixed-width string advances by both of its codes' CID widths: CID 34 (600) for the
    /// one-byte code and CID 633 (1000) for the two-byte one, 16 pt at 10 pt type.
    /// </summary>
    [Fact]
    public void TheAdvanceOfAMixedWidthStringSumsBothCodesWidths()
    {
        Assert.Equal(16.0, Advance(BuildPdf("7 0 R", MixedWidthEncoding, MixedFontToUnicode,
            "[34 [600] 633 [1000]]", "418140"), new byte[] { 0x41, 0x81, 0x40 }), 6);
    }

    /// <summary>
    /// §9.3.3: word spacing applies to the single-byte code 32 and to nothing else. With
    /// <c>Tw = 7</c> the one-byte code 0x20 advances 5 + 7 = 12 pt.
    /// </summary>
    [Fact]
    public void WordSpacingAppliesToASingleByteCode32()
    {
        Assert.Equal(12.0, Advance(BuildPdf("7 0 R", OneByteEncoding, IdentityToUnicode,
            "[32 [500]]", "20"), new byte[] { 0x20 }, wordSpacing: 7), 6);
    }

    /// <summary>
    /// The other half of §9.3.3, and the one that was wrong: the <b>two-byte</b> code 0x0020 takes
    /// no word spacing, so it advances 5 pt with <c>Tw = 7</c> in force, not 12.
    /// </summary>
    [Fact]
    public void WordSpacingDoesNotApplyToATwoByteCode32()
    {
        Assert.Equal(5.0, Advance(BuildPdf("/Identity-H", "", IdentityToUnicode,
            "[32 [500]]", "0020"), new byte[] { 0x00, 0x20 }, wordSpacing: 7), 6);
    }

    // ------------------------------------------------------------------
    //  Fixture
    // ------------------------------------------------------------------

    /// <summary>The advance, in points, that showing <paramref name="show"/> adds to the text matrix.</summary>
    private static double Advance(byte[] pdf, byte[] show, double fontSize = 10, double wordSpacing = 0)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var font = LoadFont(doc);

        var state = new TextState { FontSize = fontSize, WordSpacing = wordSpacing };
        PdfTextExtractor.AdvanceTextMatrixWithWidths(state, font.SplitCodes(show), font);
        return state.TextMatrix.E;
    }

    private static PdfFont LoadFont(PdfDocument doc)
    {
        var fonts = doc.ResolveObject(doc.Pages[0].Resources[PdfName.Font]) as PdfDictionary;
        return PdfFont.Create(doc.ResolveObject(fonts[new PdfName("F0")]) as PdfDictionary, doc);
    }

    private static string Extract(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return PdfTextExtractor.ExtractText(doc.Pages[0]).Trim();
    }

    /// <summary>
    /// One page showing <paramref name="hexShow"/> through a Type 0 font whose /Encoding is
    /// <paramref name="encodingValue"/> (a name, or "7 0 R" for the embedded CMap stream).
    /// </summary>
    private static byte[] BuildPdf(string encodingValue, string encodingCMap, string toUnicodeCMap,
        string w, string hexShow)
    {
        string content = $"BT /F0 10 Tf 50 700 Td <{hexShow}> Tj ET";

        var objs = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 800] /Contents 4 0 R "
                + "/Resources << /Font << /F0 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /Test "
                + $"/Encoding {encodingValue} /DescendantFonts [6 0 R] /ToUnicode 8 0 R >>\nendobj\n",
            "6 0 obj\n<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Test "
                + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
                + $"/DW 1000 /W {w} >>\nendobj\n",
            $"7 0 obj\n<< /Type /CMap /Length {encodingCMap.Length} >>\nstream\n{encodingCMap}\nendstream\nendobj\n",
            $"8 0 obj\n<< /Length {toUnicodeCMap.Length} >>\nstream\n{toUnicodeCMap}\nendstream\nendobj\n",
        };
        return Build(objs);
    }

    private static byte[] Build(List<string> objects)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));

        Emit("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(acc.Count); Emit(o); }

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
