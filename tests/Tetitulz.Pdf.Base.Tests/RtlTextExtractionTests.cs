// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>Tests for RTL (Hebrew/Arabic) text extraction.</summary>
public class RtlTextExtractionTests
{
    // Build a minimal PDF whose content stream places Hebrew characters at absolute X positions.
    // Each char is placed with an individual BT/Tm block so X positions are exact.
    // Hebrew "שלום" = shin(U+05E9) lamed(U+05DC) vav(U+05D5) mem(U+05DE)
    // Visual order on page (right-to-left): shin at x=120, lamed at x=110, vav at x=100, mem at x=90
    private static byte[] BuildHebrewPdf()
    {
        // Use a ToUnicode CMap to map bytes → Hebrew Unicode
        // Byte 0x01=shin, 0x02=lamed, 0x03=vav, 0x04=mem(final=0x05DD but let's use mem)
        string cmap = @"
/CIDInit /ProcSet findresource begin
12 dict begin
begincmap
/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def
/CMapName /Test-UTF16 def
/CMapType 2 def
4 beginbfchar
<01> <05E9>
<02> <05DC>
<03> <05D5>
<04> <05DE>
endbfchar
endcmap
CMap currentdict end end";

        // Content stream: each byte placed at a specific X via Tm
        // shin at x=120, lamed at x=110, vav at x=100, mem at x=90 (visual RTL order)
        string cs =
            "BT /F1 10 Tf\n" +
            "120 700 Td <01> Tj\n" + // shin
            "-10 0 Td <02> Tj\n" +   // lamed at 110
            "-10 0 Td <03> Tj\n" +   // vav at 100
            "-10 0 Td <04> Tj\n" +   // mem at 90
            "ET\n";

        // But actually we need absolute positions. Use Tm instead:
        cs =
            "BT /F1 10 Tf\n" +
            "1 0 0 1 120 700 Tm <01> Tj\n" +
            "1 0 0 1 110 700 Tm <02> Tj\n" +
            "1 0 0 1 100 700 Tm <03> Tj\n" +
            "1 0 0 1 90 700 Tm <04> Tj\n" +
            "ET\n";

        return BuildRawPdf(cs, cmap);
    }

    private static byte[] BuildRawPdf(string contentStream, string cmap = null)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int nextObj = 1;

        int catalogObj = nextObj++;   // 1
        int pagesObj   = nextObj++;   // 2
        int pageObj    = nextObj++;   // 3
        int contentObj = nextObj++;   // 4
        int fontObj    = nextObj++;   // 5
        int cmapObj    = cmap != null ? nextObj++ : -1; // 6

        var offsets = new int[nextObj];

        // Catalog
        offsets[catalogObj] = sb.Length;
        sb.AppendFormat("{0} 0 obj\n<< /Type /Catalog /Pages {1} 0 R >>\nendobj\n", catalogObj, pagesObj);

        // Pages
        offsets[pagesObj] = sb.Length;
        sb.AppendFormat("{0} 0 obj\n<< /Type /Pages /Kids [{1} 0 R] /Count 1 >>\nendobj\n", pagesObj, pageObj);

        // Page
        offsets[pageObj] = sb.Length;
        sb.AppendFormat("{0} 0 obj\n<< /Type /Page /Parent {1} 0 R /MediaBox [0 0 612 792]" +
                        " /Contents {2} 0 R /Resources << /Font << /F1 {3} 0 R >> >> >>\nendobj\n",
                        pageObj, pagesObj, contentObj, fontObj);

        // Content stream
        offsets[contentObj] = sb.Length;
        sb.AppendFormat("{0} 0 obj\n<< /Length {1} >>\nstream\n{2}\nendstream\nendobj\n",
                        contentObj, contentStream.Length, contentStream);

        // Font with ToUnicode CMap
        offsets[fontObj] = sb.Length;
        if (cmapObj > 0)
            sb.AppendFormat("{0} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode {1} 0 R >>\nendobj\n",
                            fontObj, cmapObj);
        else
            sb.AppendFormat("{0} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n", fontObj);

        // CMap stream
        if (cmapObj > 0)
        {
            offsets[cmapObj] = sb.Length;
            sb.AppendFormat("{0} 0 obj\n<< /Length {1} >>\nstream\n{2}\nendstream\nendobj\n",
                            cmapObj, cmap.Length, cmap);
        }

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n", nextObj);
        sb.Append("0000000000 65535 f \n");
        for (int i = 1; i < nextObj; i++)
            sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.AppendFormat("trailer\n<< /Size {0} /Root {1} 0 R >>\n", nextObj, catalogObj);
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ------------------------------------------------------------------
    //  IsRtl flag tests (using data model directly)
    // ------------------------------------------------------------------

    [Fact]
    public void PdfTextLine_IsRtl_TrueWhenSetInConstructor()
    {
        var line = new PdfTextLine("\u05E9\u05DC\u05D5\u05DE", 90, 700, 40, 10, System.Array.Empty<PdfTextWord>(), isRtl: true);
        Assert.True(line.IsRtl);
    }

    [Fact]
    public void PdfTextWord_IsRtl_TrueWhenSetInConstructor()
    {
        var word = new PdfTextWord("\u05E9\u05DC\u05D5\u05DE", 90, 700, 40, 10, System.Array.Empty<PdfTextChar>(), isRtl: true);
        Assert.True(word.IsRtl);
    }

    [Fact]
    public void PdfTextChar_IsRtl_TrueWhenSetInConstructor()
    {
        var ch = new PdfTextChar("\u05E9", 120, 700, 8, 10, isRtl: true);
        Assert.True(ch.IsRtl);
    }

    [Fact]
    public void PdfTextLine_IsRtl_DefaultFalse()
    {
        var line = new PdfTextLine("hello", 0, 700, 40, 10, System.Array.Empty<PdfTextWord>());
        Assert.False(line.IsRtl);
    }

    // ------------------------------------------------------------------
    //  RTL line ordering: Hebrew chars at X=120,110,100,90 should produce
    //  text in reading order (rightmost=first) not reversed
    // ------------------------------------------------------------------

    [Fact]
    public void Extract_HebrewLine_IsRtlTrue()
    {
        byte[] pdf = BuildHebrewPdf();
        using var ms = new MemoryStream(pdf);
        var pages = PdfStructuredTextExtractor.Extract(ms);
        Assert.NotEmpty(pages);
        // At least one line should be RTL
        bool found = false;
        foreach (var line in pages[0].Lines)
            if (line.IsRtl) { found = true; break; }
        Assert.True(found, "Expected at least one RTL line");
    }

    [Fact]
    public void Extract_HebrewChars_OrderedRightToLeft()
    {
        byte[] pdf = BuildHebrewPdf();
        using var ms = new MemoryStream(pdf);
        var pages = PdfStructuredTextExtractor.Extract(ms);
        Assert.NotEmpty(pages);

        // Not `if (rtlLine == null) return;` — a silent pass whenever the ToUnicode CMap fails
        // to decode, i.e. exactly when this test should go red.
        PdfTextLine rtlLine = null;
        foreach (var line in pages[0].Lines)
            if (line.IsRtl) { rtlLine = line; break; }
        Assert.True(rtlLine != null, "no RTL line was extracted; the ToUnicode CMap did not decode");

        // Reading order is right-to-left, so the rightmost glyph (shin at X=120) comes first
        // and the leftmost (mem at X=90) comes last.
        Assert.StartsWith("\u05E9", rtlLine.Text);
        Assert.EndsWith("\u05DE", rtlLine.Text);
    }

    [Fact]
    public void Extract_LtrLine_IsRtlFalse()
    {
        const string cs = "BT /F1 10 Tf 50 700 Td (Hello World) Tj ET\n";
        byte[] pdf = BuildRawPdf(cs);
        using var ms = new MemoryStream(pdf);
        var pages = PdfStructuredTextExtractor.Extract(ms);
        if (pages.Count == 0 || pages[0].Lines.Count == 0) return;

        bool anyRtl = false;
        foreach (var line in pages[0].Lines)
            if (line.IsRtl) { anyRtl = true; break; }
        Assert.False(anyRtl);
    }

    // ------------------------------------------------------------------
    //  Mixed RTL/LTR: numbers within Hebrew text stay in logical position
    // ------------------------------------------------------------------

    [Fact]
    public void Extract_MixedLine_NumbersPreservedInLogicalPosition()
    {
        // Hebrew "שלום" at x=200-170, then "123" at x=120-100, then "עולם" at x=80-50
        // Reading order: שלום 123 עולם
        string cmap = @"
/CIDInit /ProcSet findresource begin
12 dict begin
begincmap
/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def
/CMapName /Test2 def
/CMapType 2 def
2 beginbfchar
<01> <05E9>
<02> <05E2>
endbfchar
endcmap
CMap currentdict end end";

        string cs =
            "BT /F1 10 Tf\n" +
            "1 0 0 1 200 700 Tm <01> Tj\n" +  // shin at x=200
            "1 0 0 1 120 700 Tm (1) Tj\n" +   // digit at x=120
            "1 0 0 1 80 700 Tm <02> Tj\n" +   // ayin at x=80
            "ET\n";

        byte[] pdf = BuildRawPdf(cs, cmap);
        using var ms = new MemoryStream(pdf);
        var pages = PdfStructuredTextExtractor.Extract(ms);

        var line = Assert.Single(pages[0].Lines);
        var words = line.Words;

        // The claim in the name: the digit keeps its LOGICAL position — between the two Hebrew
        // letters — rather than being flushed to either end of the line, which is the classic bidi
        // failure for a neutral run embedded in RTL text.
        Assert.Equal(3, words.Count);
        Assert.Equal("\u05E9", words[0].Text);   // shin, rightmost on the page
        Assert.Equal("1",       words[1].Text);   // the number, still in the middle
        Assert.Equal("\u05E2", words[2].Text);   // ayin, leftmost

        // ...and reading order really is right-to-left, so X strictly decreases along the line.
        Assert.True(words[0].X > words[1].X && words[1].X > words[2].X,
            $"expected descending X for RTL order, got {words[0].X}, {words[1].X}, {words[2].X}");
    }
}
