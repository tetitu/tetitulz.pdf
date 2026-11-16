// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Structure;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Review-fix regressions for the Operations layer:
///   • Named-destination numeric page index is range-validated (out-of-range → -1).
///   • Ligature / multi-char ToUnicode clusters keep the byte cursor aligned
///     (no width/charCode desync).
///   • Text rise (Ts) shifts a glyph's Y origin.
/// All PDFs are built in-memory.
/// </summary>
public class ReviewFixOps_StructuredTextTests
{
    // ================================================================
    //  Named-destination numeric page index validation
    // ================================================================

    // Builds a PDF whose /Names /Dests name tree contains one destination whose
    // first array element is a NUMERIC page index (not a page ref). The caller
    // chooses the numeric value so we can exercise in-range and out-of-range.
    private static byte[] BuildPdfWithNumericDest(int numericPageIndex)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int obj1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Names << /Dests 6 0 R >> >>\nendobj\n");

        int obj2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int obj3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

        const string cs = "BT /F1 12 Tf 100 700 Td (Hi) Tj ET";
        int obj4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            Encoding.ASCII.GetByteCount(cs), cs);

        int obj5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        // Name-tree leaf: /Names [(jump) [<numeric> /XYZ null null null]]
        int obj6 = sb.Length;
        sb.AppendFormat("6 0 obj\n<< /Names [(jump) [{0} /XYZ null null null]] >>\nendobj\n",
            numericPageIndex);

        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", obj1);
        sb.AppendFormat("{0:D10} 00000 n \n", obj2);
        sb.AppendFormat("{0:D10} 00000 n \n", obj3);
        sb.AppendFormat("{0:D10} 00000 n \n", obj4);
        sb.AppendFormat("{0:D10} 00000 n \n", obj5);
        sb.AppendFormat("{0:D10} 00000 n \n", obj6);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void NamedDest_NumericIndexInRange_Resolves()
    {
        using var ms = new MemoryStream(BuildPdfWithNumericDest(0)); // single page → 0 is valid
        var dests = PdfNamedDestinations.ListDestinations(ms);
        var jump = dests.Single(d => d.Name == "jump");
        Assert.Equal(0, jump.PageIndex);
    }

    [Fact]
    public void NamedDest_NumericIndexOutOfRange_YieldsMinusOne()
    {
        // Numeric index 999 — the document has only one page.
        using var ms = new MemoryStream(BuildPdfWithNumericDest(999));
        var dests = PdfNamedDestinations.ListDestinations(ms);
        var jump = dests.Single(d => d.Name == "jump");
        Assert.Equal(-1, jump.PageIndex);   // clamped, not stored verbatim
    }

    [Fact]
    public void NamedDest_NumericIndexNegative_YieldsMinusOne()
    {
        using var ms = new MemoryStream(BuildPdfWithNumericDest(-5));
        var dests = PdfNamedDestinations.ListDestinations(ms);
        var jump = dests.Single(d => d.Name == "jump");
        Assert.Equal(-1, jump.PageIndex);
    }

    // ================================================================
    //  Ligature / multi-char ToUnicode cluster alignment
    // ================================================================

    // Builds a one-page PDF whose font maps byte 0x01 → "fi" (a ligature, two
    // UTF-16 units from one source code) and byte 0x02 → "x".
    private static byte[] BuildLigaturePdf(string contentStream)
    {
        string cmap =
            "/CIDInit /ProcSet findresource begin\n" +
            "12 dict begin\nbegincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
            "/CMapName /Test-UTF16 def\n/CMapType 2 def\n" +
            "2 beginbfchar\n" +
            "<01> <00660069>\n" +   // "fi"
            "<02> <0078>\n" +       // "x"
            "endbfchar\nendcmap\nCMap currentdict end end";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int obj1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int obj2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int obj3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

        int obj4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            Encoding.ASCII.GetByteCount(contentStream), contentStream);

        int obj5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 6 0 R >>\nendobj\n");

        int obj6 = sb.Length;
        sb.AppendFormat("6 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            Encoding.ASCII.GetByteCount(cmap), cmap);

        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", obj1);
        sb.AppendFormat("{0:D10} 00000 n \n", obj2);
        sb.AppendFormat("{0:D10} 00000 n \n", obj3);
        sb.AppendFormat("{0:D10} 00000 n \n", obj4);
        sb.AppendFormat("{0:D10} 00000 n \n", obj5);
        sb.AppendFormat("{0:D10} 00000 n \n", obj6);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void Ligature_OneCodeTwoChars_KeepsByteCursorAligned()
    {
        // Two source codes on one baseline: 0x01 → "fi", 0x02 → "x".
        // The per-source-code loop emits ONE PdfTextChar per code, so the "fi"
        // ligature stays a single cluster (Char == "fi") and "x" is a distinct,
        // correctly-mapped second cluster. Pre-fix, the loop walked the decoded
        // string "fix" char-by-char, splitting the ligature and desyncing the
        // byte cursor for every following code.
        const string cs = "BT /F1 12 Tf 100 700 Td <0102> Tj ET";
        using var ms = new MemoryStream(BuildLigaturePdf(cs));
        var pages = PdfStructuredTextExtractor.Extract(ms);

        var chars = pages[0].Lines
            .SelectMany(l => l.Words)
            .SelectMany(w => w.Chars)
            .ToList();

        // Exactly two source codes → exactly two emitted clusters.
        Assert.Equal(2, chars.Count);
        Assert.Equal("fi", chars[0].Char);   // ligature kept intact as one cluster
        Assert.Equal("x",  chars[1].Char);   // trailing code correctly mapped, not dropped

        string all = string.Concat(pages[0].Lines.Select(l => l.Text));
        Assert.Contains("fi", all);
        Assert.Contains("x", all);
    }

    // ================================================================
    //  Text rise (Ts) shifts the glyph Y origin
    // ================================================================

    // Builds a one-page PDF and lets the caller inject a content stream.
    private static byte[] BuildSimpleTextPdf(string contentStream)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int obj1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int obj2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int obj3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

        int obj4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            Encoding.ASCII.GetByteCount(contentStream), contentStream);

        int obj5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", obj1);
        sb.AppendFormat("{0:D10} 00000 n \n", obj2);
        sb.AppendFormat("{0:D10} 00000 n \n", obj3);
        sb.AppendFormat("{0:D10} 00000 n \n", obj4);
        sb.AppendFormat("{0:D10} 00000 n \n", obj5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void TextRise_ShiftsGlyphYOrigin()
    {
        // Same baseline (Td 100 600) for both glyphs, but the second uses Ts 8 to
        // raise it (superscript). The extracted Y of the risen glyph must differ.
        const string baseCs  = "BT /F1 12 Tf 100 600 Td (A) Tj ET";
        const string riseCs  = "BT /F1 12 Tf 100 600 Td 8 Ts (A) Tj ET";

        using var baseMs = new MemoryStream(BuildSimpleTextPdf(baseCs));
        using var riseMs = new MemoryStream(BuildSimpleTextPdf(riseCs));

        var baseChar = PdfStructuredTextExtractor.Extract(baseMs)[0]
            .Lines.SelectMany(l => l.Words).SelectMany(w => w.Chars).First();
        var riseChar = PdfStructuredTextExtractor.Extract(riseMs)[0]
            .Lines.SelectMany(l => l.Words).SelectMany(w => w.Chars).First();

        // Rise of +8 pt must lift the glyph Y above the un-risen baseline.
        Assert.True(riseChar.Y > baseChar.Y + 1f,
            $"Expected risen glyph Y ({riseChar.Y}) to exceed base Y ({baseChar.Y}) by the rise amount.");
    }

    [Fact]
    public void TextRise_Zero_LeavesYUnchanged()
    {
        const string cs = "BT /F1 12 Tf 100 600 Td 0 Ts (A) Tj ET";
        using var ms = new MemoryStream(BuildSimpleTextPdf(cs));
        var ch = PdfStructuredTextExtractor.Extract(ms)[0]
            .Lines.SelectMany(l => l.Words).SelectMany(w => w.Chars).First();

        // Td places the origin at y=600; a zero rise leaves it there.
        Assert.True(System.Math.Abs(ch.Y - 600f) < 1f,
            $"Expected Y≈600 with zero rise, got {ch.Y}.");
    }
}
