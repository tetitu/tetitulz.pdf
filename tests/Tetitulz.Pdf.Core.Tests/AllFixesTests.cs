// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Core.Tests.Helpers;

namespace Tetitulz.Pdf.Core.Tests;

/// <summary>
/// Tests for all fixes applied across another reader, another library, another library, and another library sessions.
/// Each test targets a specific fix and verifies it works correctly.
/// </summary>
public class AllFixesTests
{
    // ================================================================
    //  another reader fixes
    // ================================================================

    #region 1. CMapParser: surrogate pair handling

    [Fact]
    public void CMapParser_SurrogatePair_DecodedCorrectly()
    {
        // A ToUnicode CMap with a surrogate pair destination (U+1F600 = D83D DE00)
        byte[] data = Encoding.ASCII.GetBytes(
            "beginbfchar\n<0001> <D83DDE00>\nendbfchar\n");
        var map = CMapParser.Parse(data);
        Assert.True(map.TryGetValue(1, out string s));
        // U+1F600 is a supplementary character (emoji)
        Assert.Equal(char.ConvertFromUtf32(0x1F600), s);
    }

    [Fact]
    public void CMapParser_NonSurrogateMultiByte_DecodedCorrectly()
    {
        // Two BMP code points (not surrogates): ff = U+0066 U+0066
        byte[] data = Encoding.ASCII.GetBytes(
            "beginbfchar\n<0001> <00660066>\nendbfchar\n");
        var map = CMapParser.Parse(data);
        Assert.Equal("ff", map[1]);
    }

    #endregion

    #region 2. CMapParser: codespace range extraction

    [Fact]
    public void CMapParser_CodespaceRanges_Extracted()
    {
        byte[] data = Encoding.ASCII.GetBytes(
            "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
            "beginbfchar\n<41> <0041>\nendbfchar\n");
        var map = CMapParser.Parse(data, out int maxBytes,
            out List<(byte[] low, byte[] high)> ranges);
        Assert.Equal(1, maxBytes);
        Assert.Single(ranges);
        Assert.Equal(new byte[] { 0x00 }, ranges[0].low);
        Assert.Equal(new byte[] { 0xFF }, ranges[0].high);
    }

    [Fact]
    public void CMapParser_TwoByteCodespace_MaxBytes2()
    {
        byte[] data = Encoding.ASCII.GetBytes(
            "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        CMapParser.Parse(data, out int maxBytes);
        Assert.Equal(2, maxBytes);
    }

    #endregion

    #region 3. PdfParser: xref recovery for damaged PDFs

    [Fact]
    public void PdfParser_DamagedXref_RecoversByScanning()
    {
        // Build a PDF with a corrupted startxref pointer
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        int obj1Off = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int obj2Off = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int obj3Off = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

        // Write a valid trailer but with WRONG startxref offset
        pdf.Append("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        pdf.Append("startxref\n99999\n%%EOF\n");

        byte[] bytes = Encoding.ASCII.GetBytes(pdf.ToString());
        using var ms = new MemoryStream(bytes);
        // Should not throw — recovery mode scans for "obj" patterns
        using var doc = PdfDocument.Open(ms);
        Assert.NotNull(doc.Catalog);
        Assert.Single(doc.Pages);
    }

    #endregion

    #region 4. LzwDecodeFilter: TIFF predictor support

    /// <summary>
    /// TIFF Predictor 2 is horizontal differencing: each byte in a row is stored as its difference
    /// from the previous one, so decoding must accumulate them back. Here the LZW payload decodes
    /// to <c>10, 5, 5, 5</c> and the predictor must turn that into <c>10, 15, 20, 25</c>.
    ///
    /// <para>The predecessor fed in a payload that decodes to a single byte and asserted only that
    /// the result was non-null — with one byte there is no <i>previous</i> byte, so the differencing
    /// step is a no-op and the test could not tell "predictor applied" from "predictor ignored".</para>
    /// </summary>
    [Fact]
    public void LzwDecode_TiffPredictor_AccumulatesHorizontalDifferences()
    {
        // ClearTable(256), 10, 5, 5, 5, EndOfData(257) as 9-bit codes.
        byte[] lzwData = { 0x80, 0x02, 0x80, 0xA0, 0x50, 0x2C, 0x04 };

        // Without a predictor the payload is the raw difference stream...
        byte[] undifferenced = new LzwDecodeFilter().Decode(lzwData, new PdfDictionary());
        Assert.Equal(new byte[] { 10, 5, 5, 5 }, undifferenced);

        // ...and with Predictor 2 it accumulates across the row.
        var parms = new PdfDictionary();
        parms.Set(PdfName.Predictor, new PdfNumber(2));
        parms.Set(PdfName.Columns, new PdfNumber(4));
        parms.Set(PdfName.Colors, new PdfNumber(1));

        byte[] result = new LzwDecodeFilter().Decode(lzwData, parms);

        Assert.Equal(new byte[] { 10, 15, 20, 25 }, result);
    }

    #endregion

    #region 5. FlateDecodeFilter: PNG predictor remaining data

    [Fact]
    public void FlateDecode_PngPredictor_HandlesPartialRow()
    {
        // Create data with PNG predictor (filter byte + row data)
        // 2 complete rows (1 col, 1 color, 8 bpc) + 1 extra byte
        // Row = filter(1 byte) + data(1 byte) = 2 bytes per row
        byte[] raw = new byte[] { 0, 0x42, 0, 0x43, 0, 0x44 }; // 3 rows, filter=None

        // Compress with zlib
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            ms.WriteByte(0x78); ms.WriteByte(0x9C);
            using (var deflate = new System.IO.Compression.DeflateStream(
                ms, System.IO.Compression.CompressionLevel.Optimal, true))
                deflate.Write(raw, 0, raw.Length);
            compressed = ms.ToArray();
        }

        var parms = new PdfDictionary();
        parms.Set(PdfName.Predictor, new PdfNumber(10)); // PNG None
        parms.Set(PdfName.Columns, new PdfNumber(1));

        var filter = new FlateDecodeFilter();
        byte[] result = filter.Decode(compressed, parms);
        // Should produce 3 bytes (one per row): 0x42, 0x43, 0x44
        Assert.Equal(3, result.Length);
        Assert.Equal(0x42, result[0]);
        Assert.Equal(0x43, result[1]);
        Assert.Equal(0x44, result[2]);
    }

    #endregion

    #region 6. StandardEncodings: MacExpertEncoding + uniXXXX convention

    [Fact]
    /// <summary>
    /// MacExpertEncoding must be a table of its own with real code→character entries — not an empty
    /// dictionary and not an alias of another encoding. "Not null and non-empty" would be satisfied
    /// by a one-entry stub, or by handing back WinAnsi.
    /// </summary>
    public void StandardEncodings_MacExpertEncoding_MapsCodesAndIsNotAnAliasOfAnotherTable()
    {
        var enc = StandardEncodings.MacExpertEncoding;

        Assert.NotEmpty(enc);
        Assert.Equal(' ', enc[32]);
        Assert.Equal('0', enc[48]);
        Assert.Equal('9', enc[57]);

        // The letter codes are the *small-capital* variants ("Asmall" … "Zsmall"), which this
        // table approximates with the lower-case Unicode letter — NOT the capital. That is the
        // whole point of an expert set, and asserting it is what distinguishes MacExpert from a
        // copy of a normal text encoding.
        Assert.Equal('a', enc[65]);
        Assert.Equal('z', enc[90]);

        // A distinct table, not another encoding under a second name.
        Assert.NotSame(StandardEncodings.WinAnsiEncoding, enc);
        Assert.NotSame(StandardEncodings.AdobeStandardEncoding, enc);

        // ...and genuinely partial: the lower-case ASCII codes are unmapped, because the
        // small-caps glyphs already occupy the upper-case slots.
        Assert.False(enc.ContainsKey(97), "MacExpert should not map code 97");
    }

    [Fact]
    public void TryGetGlyphUnicode_UniXXXX_Convention()
    {
        Assert.True(StandardEncodings.TryGetGlyphUnicode("uni0041", out char c));
        Assert.Equal('A', c);
    }

    [Fact]
    public void TryGetGlyphUnicode_UniXXXX_Hebrew()
    {
        Assert.True(StandardEncodings.TryGetGlyphUnicode("uni05D0", out char c));
        Assert.Equal('\u05D0', c); // Alef
    }

    [Fact]
    public void TryGetGlyphUnicode_Adieresis_Mapped()
    {
        Assert.True(StandardEncodings.TryGetGlyphUnicode("Adieresis", out char c));
        Assert.Equal('\u00C4', c);
    }

    [Fact]
    public void TryGetGlyphUnicode_ntilde_Mapped()
    {
        Assert.True(StandardEncodings.TryGetGlyphUnicode("ntilde", out char c));
        Assert.Equal('\u00F1', c);
    }

    [Fact]
    public void TryGetGlyphUnicode_Euro_Mapped()
    {
        Assert.True(StandardEncodings.TryGetGlyphUnicode("Euro", out char c));
        Assert.Equal('\u20AC', c);
    }

    [Fact]
    public void TryGetGlyphUnicode_fi_Ligature_Mapped()
    {
        Assert.True(StandardEncodings.TryGetGlyphUnicode("fi", out char c));
        Assert.Equal('\uFB01', c);
    }

    [Fact]
    public void TryGetGlyphUnicode_ffi_Ligature_Mapped()
    {
        Assert.True(StandardEncodings.TryGetGlyphUnicode("ffi", out char c));
        Assert.Equal('\uFB03', c);
    }

    [Fact]
    public void TryGetGlyphUnicode_uXXXX_Convention()
    {
        Assert.True(StandardEncodings.TryGetGlyphUnicode("u0042", out char c));
        Assert.Equal('B', c);
    }

    [Fact]
    public void TryGetGlyphUnicode_UnknownName_ReturnsFalse()
    {
        Assert.False(StandardEncodings.TryGetGlyphUnicode("zzUnknownGlyph", out _));
    }

    #endregion

    // ================================================================
    //  another library fixes
    // ================================================================

    #region 7. PdfWriter: number formatting (no scientific notation)

    [Fact]
    public void PdfWriter_SmallFloat_NoScientificNotation()
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        writer.WriteObject(new PdfNumber(0.000001));
        string output = Encoding.ASCII.GetString(ms.ToArray());
        Assert.DoesNotContain("E", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0.000001", output);
    }

    [Fact]
    public void PdfWriter_ZeroFloat_WritesZero()
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        writer.WriteObject(new PdfNumber(0.0));
        string output = Encoding.ASCII.GetString(ms.ToArray());
        Assert.Equal("0", output);
    }

    [Fact]
    public void PdfWriter_Integer_WritesPlainNumber()
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        writer.WriteObject(new PdfNumber(42));
        Assert.Equal("42", Encoding.ASCII.GetString(ms.ToArray()));
    }

    #endregion

    #region 8. PdfWriter: stream /Length auto-update

    [Fact]
    public void PdfWriter_Stream_LengthAutoUpdated()
    {
        var stream = new PdfStream();
        stream.RawData = new byte[] { 1, 2, 3, 4, 5 };
        stream.Set(PdfName.Length, new PdfNumber(999)); // wrong length

        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        writer.WriteIndirectObject(1, 0, stream);

        // After writing, /Length should be 5
        var length = stream[PdfName.Length] as PdfNumber;
        Assert.NotNull(length);
        Assert.Equal(5, length.IntValue);
    }

    #endregion

    #region 9. PdfPageTree: inherited MediaBox/CropBox/Rotate

    [Fact]
    public void PdfPage_InheritsMediaBox_FromParent()
    {
        // Build a PDF where MediaBox is only on the Pages node, not on the Page
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        int obj1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int obj2 = pdf.Length;
        // MediaBox is here on the parent Pages node
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 595 842] >>\nendobj\n");

        int obj3 = pdf.Length;
        // Page has NO MediaBox — should inherit from parent
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /Resources << >> >>\nendobj\n");

        int xref = pdf.Length;
        pdf.Append("xref\n0 4\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", obj1);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj2);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj3);
        pdf.Append("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        byte[] bytes = Encoding.ASCII.GetBytes(pdf.ToString());
        using var ms = new MemoryStream(bytes);
        using var doc = PdfDocument.Open(ms);

        var page = doc.Pages[0];
        Assert.NotNull(page.MediaBox);
        Assert.Equal(4, page.MediaBox.Count);
        // Should be [0 0 595 842] inherited from parent
        Assert.Equal(595, (page.MediaBox[2] as PdfNumber)?.IntValue);
        Assert.Equal(842, (page.MediaBox[3] as PdfNumber)?.IntValue);
    }

    [Fact]
    public void PdfPage_InheritsRotate_FromParent()
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        int obj1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int obj2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] /Rotate 90 >>\nendobj\n");

        int obj3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R >>\nendobj\n");

        int xref = pdf.Length;
        pdf.Append("xref\n0 4\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", obj1);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj2);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj3);
        pdf.Append("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        byte[] bytes = Encoding.ASCII.GetBytes(pdf.ToString());
        using var ms = new MemoryStream(bytes);
        using var doc = PdfDocument.Open(ms);

        Assert.Equal(90, doc.Pages[0].Rotate);
    }

    #endregion

    // ================================================================
    //  another library fixes
    // ================================================================

    #region 10. PdfFont: width tables

    [Fact]
    public void PdfType1Font_GetWidth_ReadsWidthsArray()
    {
        // Build a minimal PDF with a font that has /Widths, then read it back
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        int obj1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int obj2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>\nendobj\n");

        int obj3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R ");
        pdf.Append("/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

        string content = "BT /F1 12 Tf 72 700 Td ( !) Tj ET";
        int obj4 = pdf.Length;
        pdf.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            content.Length, content);

        int obj5 = pdf.Length;
        pdf.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica ");
        pdf.Append("/FirstChar 32 /LastChar 33 /Widths [250 333] >>\nendobj\n");

        int xref = pdf.Length;
        pdf.Append("xref\n0 6\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", obj1);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj2);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj3);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj4);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj5);
        pdf.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        byte[] bytes = Encoding.ASCII.GetBytes(pdf.ToString());
        using var ms = new MemoryStream(bytes);
        using var doc = PdfDocument.Open(ms);

        // Load the font through the normal path
        var page = doc.Pages[0];
        var resources = page.Resources;
        var fontRes = doc.ResolveObject(resources[PdfName.Font]) as PdfDictionary;
        var fontObj = doc.ResolveObject(fontRes[new PdfName("F1")]) as PdfDictionary;
        var font = PdfFont.Create(fontObj, doc);

        Assert.Equal(250, font.GetWidth(32));  // space
        Assert.Equal(333, font.GetWidth(33));  // !
        Assert.Equal(0, font.GetWidth(65));    // not in range
    }

    #endregion

    // ================================================================
    //  another library fixes
    // ================================================================

    #region 11. PdfNumber.ToString: no scientific notation

    [Fact]
    public void PdfNumber_SmallDouble_FixedPointNotation()
    {
        var num = new PdfNumber(0.000001);
        string s = num.ToString();
        Assert.DoesNotContain("E", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PdfNumber_Integer_NoDecimalPoint()
    {
        var num = new PdfNumber(42);
        Assert.Equal("42", num.ToString());
    }

    [Fact]
    public void PdfNumber_Half_CompactRepresentation()
    {
        var num = new PdfNumber(0.5);
        Assert.Equal("0.5", num.ToString());
    }

    #endregion

    #region 12. PdfContentStreamBuilder: CurveTo/ClosePath/ClipPath

    [Fact]
    public void ContentStreamBuilder_CurveTo_ProducesCOperator()
    {
        var b = new PdfContentStreamBuilder();
        b.CurveTo(1, 2, 3, 4, 5, 6);
        string output = Encoding.GetEncoding("iso-8859-1").GetString(b.Build());
        Assert.Contains("1 2 3 4 5 6 c", output);
    }

    [Fact]
    public void ContentStreamBuilder_ClosePath_ProducesHOperator()
    {
        var b = new PdfContentStreamBuilder();
        b.ClosePath();
        string output = Encoding.GetEncoding("iso-8859-1").GetString(b.Build());
        Assert.Contains("h", output);
    }

    [Fact]
    public void ContentStreamBuilder_ClipNonzero_ProducesWnOperator()
    {
        var b = new PdfContentStreamBuilder();
        b.ClipNonzero();
        string output = Encoding.GetEncoding("iso-8859-1").GetString(b.Build());
        Assert.Contains("W n", output);
    }

    [Fact]
    public void ContentStreamBuilder_NoScientificNotation()
    {
        var b = new PdfContentStreamBuilder();
        b.Transform(0.000001, 0, 0, 0.000001, 100, 200);
        string output = Encoding.GetEncoding("iso-8859-1").GetString(b.Build());
        Assert.DoesNotContain("E", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cm", output);
    }

    #endregion

    #region 13. PdfPage: BleedBox/TrimBox/ArtBox/UserUnit

    [Fact]
    public void PdfPage_BleedBox_DefaultsToCropBox()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("test");
        using var ms = new MemoryStream(pdfBytes);
        using var doc = PdfDocument.Open(ms);
        var page = doc.Pages[0];
        // BleedBox defaults to CropBox which defaults to MediaBox
        Assert.NotNull(page.BleedBox);
        Assert.Equal(page.CropBox, page.BleedBox);
    }

    [Fact]
    /// <summary>
    /// §14.11.2: an absent /TrimBox defaults to the CropBox. The name says so; assert it, rather
    /// than that some array came back — a TrimBox defaulting to the wrong rectangle is non-null too.
    /// </summary>
    public void PdfPage_TrimBox_DefaultsToCropBox()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("test");
        using var ms = new MemoryStream(pdfBytes);
        using var doc = PdfDocument.Open(ms);
        var page = doc.Pages[0];

        Assert.Same(page.CropBox, page.TrimBox);
        AssertRect(page.TrimBox, 0, 0, 612, 792);
    }

    [Fact]
    /// <summary>Same rule for /ArtBox (§14.11.2).</summary>
    public void PdfPage_ArtBox_DefaultsToCropBox()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("test");
        using var ms = new MemoryStream(pdfBytes);
        using var doc = PdfDocument.Open(ms);
        var page = doc.Pages[0];

        Assert.Same(page.CropBox, page.ArtBox);
        AssertRect(page.ArtBox, 0, 0, 612, 792);
    }

    /// <summary>Asserts a PDF rectangle array holds exactly these four numbers.</summary>
    private static void AssertRect(PdfArray box, double x0, double y0, double x1, double y1)
    {
        Assert.NotNull(box);
        Assert.Equal(4, box.Count);
        Assert.Equal(x0, ((PdfNumber)box[0]).DoubleValue, 3);
        Assert.Equal(y0, ((PdfNumber)box[1]).DoubleValue, 3);
        Assert.Equal(x1, ((PdfNumber)box[2]).DoubleValue, 3);
        Assert.Equal(y1, ((PdfNumber)box[3]).DoubleValue, 3);
    }

    [Fact]
    public void PdfPage_UserUnit_DefaultsTo1()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("test");
        using var ms = new MemoryStream(pdfBytes);
        using var doc = PdfDocument.Open(ms);
        Assert.Equal(1.0, doc.Pages[0].UserUnit);
    }

    // ================================================================
    //  Inline image abbreviation fixes
    // ================================================================

    #region Inline image: null and nested dict preserved in array

    [Fact]
    public void ContentStreamParser_InlineArray_NullPreservedAsElement()
    {
        // [null << /Predictor 15 >>] must parse to a 2-element array:
        // element 0 = PdfNull, element 1 = PdfDictionary.
        // Before the fix, PdfTokenType.Null hit default:break so null was dropped,
        // collapsing the array to [<< /Predictor 15 >>] and misaligning parms for
        // multi-filter inline images (AHx + Fl: parms[1] for Fl would be null).
        byte[] cs = Encoding.ASCII.GetBytes(
            "BI\n/W 1\n/H 1\n/BPC 8\n/CS /G\n/F /AHx\n" +
            "/DP [null << /Predictor 15 >>]\nID\n00>\nEI\n");
        var ops = ContentStreamParser.Parse(cs);
        Assert.Single(ops);
        var stream = ops[0].Operands[0] as PdfStream;
        Assert.NotNull(stream);
        var dp = stream[new PdfName("DP")] as PdfArray;
        Assert.NotNull(dp);
        Assert.Equal(2, dp.Count);
        Assert.IsType<PdfNull>(dp[0]);
        var predDict = dp[1] as PdfDictionary;
        Assert.NotNull(predDict);
        Assert.Equal(15, (predDict[new PdfName("Predictor")] as PdfNumber)?.IntValue);
    }

    [Fact]
    public void ContentStreamParser_InlineArray_TwoNullsPreservedAsElements()
    {
        // [null null] must parse to a 2-element array of PdfNull, not an empty array.
        byte[] cs = Encoding.ASCII.GetBytes(
            "BI\n/W 1\n/H 1\n/BPC 8\n/CS /G\n/F /AHx\n" +
            "/DP [null null]\nID\n00>\nEI\n");
        var ops = ContentStreamParser.Parse(cs);
        var stream = ops[0].Operands[0] as PdfStream;
        Assert.NotNull(stream);
        var dp = stream[new PdfName("DP")] as PdfArray;
        Assert.NotNull(dp);
        Assert.Equal(2, dp.Count);
        Assert.IsType<PdfNull>(dp[0]);
        Assert.IsType<PdfNull>(dp[1]);
    }

    [Fact]
    public void ContentStreamParser_ConcatenatedOps_DoQQ_SplitsIntoThreeOperators()
    {
        // Some PDF generators omit whitespace between operators, e.g.
        // "/Im1 DoQQ" instead of "/Im1 Do Q Q".  ContentStreamParser must
        // split the merged keyword into the correct individual operators.
        byte[] cs = Encoding.ASCII.GetBytes("/Im1 DoQQ");
        var ops = ContentStreamParser.Parse(cs);

        // "DoQQ" → Do(operands=[/Im1]), Q(), Q()
        Assert.Equal(3, ops.Count);
        Assert.Equal("Do",  ops[0].Operator);
        Assert.Equal("Q",   ops[1].Operator);
        Assert.Equal("Q",   ops[2].Operator);

        // The Do operator must have received the /Im1 name operand.
        Assert.Single(ops[0].Operands);
        Assert.Equal("Im1", (ops[0].Operands[0] as PdfName)?.Value);

        // The Q operators get no operands.
        Assert.Empty(ops[1].Operands);
        Assert.Empty(ops[2].Operands);
    }

    [Fact]
    public void ContentStreamParser_ConcatenatedOps_TjETq_SplitsCorrectly()
    {
        // Another common concatenation: TjETq (show-text + end-text + save-state).
        byte[] cs = Encoding.ASCII.GetBytes("(hello)TjETq");
        var ops = ContentStreamParser.Parse(cs);

        Assert.Equal(3, ops.Count);
        Assert.Equal("Tj", ops[0].Operator);
        Assert.Equal("ET", ops[1].Operator);
        Assert.Equal("q",  ops[2].Operator);
    }

    #endregion

    // ================================================================
    //  afii57xxx Hebrew glyph names
    // ================================================================

    #region afii57xxx Hebrew glyph name resolution

    [Fact]
    public void TryGetGlyphUnicode_Afii57664_IsAlef()
    {
        // afii57664 is the AGL name for Hebrew alef (U+05D0).
        // Used in /Differences arrays of Hebrew PDFs; previously not resolved,
        // causing WinAnsiEncoding fallback and wrong/missing glyph rendering.
        Assert.True(StandardEncodings.TryGetGlyphUnicode("afii57664", out char c));
        Assert.Equal('א', c);
    }

    [Fact]
    public void TryGetGlyphUnicode_Afii57690_IsTav()
    {
        Assert.True(StandardEncodings.TryGetGlyphUnicode("afii57690", out char c));
        Assert.Equal('ת', c);
    }

    [Fact]
    public void TryGetGlyphUnicode_Afii57636_IsNewShekel()
    {
        Assert.True(StandardEncodings.TryGetGlyphUnicode("afii57636", out char c));
        Assert.Equal('₪', c);
    }

    [Fact]
    public void TryGetGlyphUnicode_Afii57793_IsHiriq()
    {
        // Official AGL: afii57793 = U+05B4 (hiriq), not sheva.
        // afii57799 = U+05B0 (sheva).
        Assert.True(StandardEncodings.TryGetGlyphUnicode("afii57793", out char c));
        Assert.Equal('ִ', c);
    }

    [Fact]
    public void TryGetGlyphUnicode_Afii57799_IsSheva()
    {
        Assert.True(StandardEncodings.TryGetGlyphUnicode("afii57799", out char c));
        Assert.Equal('ְ', c);
    }

    [Fact]
    public void TryGetGlyphUnicode_GereshHebrew_IsGeresh()
    {
        Assert.True(StandardEncodings.TryGetGlyphUnicode("gereshhebrew", out char c));
        Assert.Equal('׳', c);
    }

    [Fact]
    public void TryGetGlyphUnicode_Afii299_IsLrm()
    {
        Assert.True(StandardEncodings.TryGetGlyphUnicode("afii299", out char c));
        Assert.Equal('‎', c);
    }

    #endregion

    #endregion

    #region 14. PdfDocumentBuilder: roundtrip

    [Fact]
    public void PdfDocumentBuilder_CreatesValidPdf()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(612, 792);
        string f1 = page.AddStandardFont("Helvetica");
        page.BeginText().SetFont(f1, 12).SetTextPosition(72, 700).ShowText("Hello Test").EndText();

        using var ms = new MemoryStream();
        builder.Build(ms);

        // Should be parseable
        ms.Position = 0;
        using var doc = PdfDocument.Open(ms);
        Assert.Single(doc.Pages);
        string text = PdfTextExtractor.ExtractText(doc);
        Assert.Contains("Hello Test", text);
    }

    #endregion

    #region 15. Text extraction: cm operator + Form XObject recursion

    [Fact]
    public void TextExtractor_HandlesFormXObject()
    {
        // Build a PDF with text inside a Form XObject
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        // Form XObject content: "FormText"
        string formContent = "BT /F1 12 Tf 0 0 Td (FormText) Tj ET";

        int obj1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int obj2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>\nendobj\n");

        int obj3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R ");
        pdf.Append("/Contents 4 0 R ");
        pdf.Append("/Resources << /Font << /F1 5 0 R >> /XObject << /Fm1 6 0 R >> >> >>\n");
        pdf.Append("endobj\n");

        // Page content: draw text + invoke Form XObject
        string pageContent = "BT /F1 12 Tf 100 700 Td (PageText) Tj ET /Fm1 Do";
        int obj4 = pdf.Length;
        pdf.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            pageContent.Length, pageContent);

        int obj5 = pdf.Length;
        pdf.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int obj6 = pdf.Length;
        pdf.AppendFormat("6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 50] ");
        pdf.AppendFormat("/Resources << /Font << /F1 5 0 R >> >> /Length {0} >>\n", formContent.Length);
        pdf.AppendFormat("stream\n{0}\nendstream\nendobj\n", formContent);

        int xref = pdf.Length;
        pdf.Append("xref\n0 7\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", obj1);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj2);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj3);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj4);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj5);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj6);
        pdf.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        byte[] bytes = Encoding.ASCII.GetBytes(pdf.ToString());
        using var ms = new MemoryStream(bytes);
        using var doc = PdfDocument.Open(ms);

        string text = PdfTextExtractor.ExtractText(doc);
        Assert.Contains("PageText", text);
        Assert.Contains("FormText", text);
    }

    #endregion

    #region 16. IncrementalUpdateWriter: no object-0 re-declaration

    [Fact]
    public void IncrementalUpdate_NoObject0InXref()
    {
        byte[] original = MinimalPdfBuilder.CreateMinimalPdf("Original");
        using var input = new MemoryStream(original);
        using var output = new MemoryStream();

        // The document's REAL previous section, not the 0 this fixture used to pass as a
        // don't-care. Object 0 is omitted because the appended section chains to a previous one
        // that already carries the free-list head; a section with no /Prev is standalone and must
        // carry its own, so passing 0 here asserts the opposite of what this test is named for.
        long previousXref;
        using (var probe = new MemoryStream(original))
        using (var doc = PdfDocument.Open(probe))
        {
            previousXref = doc.Parser.StartXrefOffset;
            Assert.True(previousXref > 0, "fixture: the document must have a nameable xref section");
        }

        // Write a trivial incremental update
        var obj = new PdfDictionary();
        obj.Set(new PdfName("TestKey"), new PdfString("TestValue"));
        obj.ObjectNumber = 99;
        obj.GenerationNumber = 0;

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, new PdfNumber(100));
        trailer.Set(PdfName.Root, new PdfReference(1, 0));

        IncrementalUpdateWriter.WriteUpdate(
            input, output, new List<IndirectObject> { new IndirectObject(99, 0, obj) },
            trailer, previousXref, 100);

        // Check the appended xref doesn't contain "0000000000 65535 f"
        output.Position = original.Length;
        byte[] appended = new byte[output.Length - original.Length];
        output.Read(appended, 0, appended.Length);
        string appendedText = Encoding.ASCII.GetString(appended);

        // The incremental xref should NOT have object 0
        // It should start with "99 1" (only the modified object)
        Assert.Contains("99 1", appendedText);
        Assert.DoesNotContain("65535 f", appendedText);
    }

    #endregion
}
