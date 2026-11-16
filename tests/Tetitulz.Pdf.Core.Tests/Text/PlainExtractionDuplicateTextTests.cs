// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// Plain extraction returns text drawn twice at nearly the same place once.
/// </summary>
/// <remarks>
/// <para><b>Structured extraction already did; this path returned both copies.</b> A layout program
/// strokes and then fills its slug line at one position, "fake bold" repaints a run a fraction of a
/// point aside, and an invisible text layer can repeat the visible text over it. <c>ExtractText</c>
/// read <c>PEC_CH00_FM_2PP.indd 1PEC_CH00_FM_2PP.indd 1</c>.</para>
/// <para><b>The rule is the structured extractor's</b> — an independent implementation's
/// <c>suppressDuplicateOverlappingText</c> — applied to what this extractor has: one show operator's
/// text at a time. A later operator is a copy when an earlier one drew the same text starting within
/// a third of a character's advance on both axes.</para>
/// <para>Every glyph in these fixtures is 500 units wide, so a 12 pt character advances 6 pt and the
/// tolerance is 2 pt.</para>
/// </remarks>
public sealed class PlainExtractionDuplicateTextTests
{
    private static string Extract(string content)
    {
        var widths = new StringBuilder();
        for (int c = 32; c <= 126; c++) widths.Append("500 ");

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[6];
        void Obj(int num, string body)
        {
            offsets[num] = sb.Length;
            sb.Append(num).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
               "/Resources << /Font << /F1 5 0 R >> >> >>");
        Obj(4, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding " +
               $"/FirstChar 32 /LastChar 126 /Widths [{widths}] >>");

        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n").AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        using var doc = PdfDocument.Open(new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString())));
        return PdfTextExtractor.ExtractText(doc.Pages[0]);
    }

    private static string Twice(string first, string second, string show = "(PEC_CH00) Tj")
        => $"BT /F1 12 Tf {first} {show} ET\nBT /F1 12 Tf {second} {show} ET";

    [Fact]
    public void TextStrokedThenFilledAtOnePosition_IsExtractedOnce()
        => Assert.Equal("PEC_CH00", Extract(Twice("1 Tr 72 700 Td", "0 Tr 72 700 Td")));

    [Fact]
    public void AnInvisibleCopyDrawnOverVisibleText_IsExtractedOnce()
        => Assert.Equal("PEC_CH00", Extract(Twice("0 Tr 72 700 Td", "3 Tr 72 700 Td")));

    [Theory]
    [InlineData("73.9 700 Td")]     // 1.9 pt aside
    [InlineData("70.1 700 Td")]     // 1.9 pt the other way
    [InlineData("72 701.9 Td")]     // 1.9 pt above
    [InlineData("72 698.1 Td")]     // 1.9 pt below
    public void TextRepaintedWithinAThirdOfACharacter_IsExtractedOnce(string second)
        => Assert.Equal("PEC_CH00", Extract(Twice("72 700 Td", second)));

    [Theory]
    [InlineData("74.1 700 Td")]     // 2.1 pt aside
    [InlineData("72 702.1 Td")]     // 2.1 pt above
    [InlineData("72 680 Td")]       // the next line
    public void TheSameTextFurtherAway_IsKept(string second)
        => Assert.Equal(2, Occurrences(Extract(Twice("72 700 Td", second)), "PEC_CH00"));

    [Fact]
    public void DifferentTextAtOnePosition_IsKept()
    {
        string text = Extract("BT /F1 12 Tf 72 700 Td (AB) Tj ET\nBT /F1 12 Tf 72 700 Td (CD) Tj ET");

        Assert.Contains("AB", text, StringComparison.Ordinal);
        Assert.Contains("CD", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ATextArrayDrawnTwice_IsExtractedOnceWithItsWordSpace()
        => Assert.Equal("Hello World", Extract(Twice("72 700 Td", "72 700 Td", "[(Hello) -300 (World)] TJ")));

    [Theory]
    [InlineData("(PEC_CH00) '", "0 0 (PEC_CH00) \"")]
    [InlineData("0 0 (PEC_CH00) \"", "(PEC_CH00) '")]
    public void TheLineOperatorsDrawnTwice_AreExtractedOnce(string first, string second)
    {
        // ' and " move to the next line before they show, so both copies start from one line matrix.
        // Both orders, because only the later drawing is ever compared.
        string text = Extract($"BT /F1 12 Tf 14 TL 72 700 Td (A) Tj {first} ET\n" +
                              $"BT /F1 12 Tf 14 TL 72 700 Td (A) Tj {second} ET");

        Assert.Equal(1, Occurrences(text, "PEC_CH00"));
    }

    [Fact]
    public void TextDrawnTwiceUnderARotation_IsExtractedOnce()
    {
        // A quarter turn puts the advance on the page's Y axis, so a tolerance built from its X
        // component alone would be zero.
        string text = Extract("q 0 1 -1 0 300 100 cm\n" + Twice("72 700 Td", "72 700 Td") + "\nQ");

        Assert.Equal(1, Occurrences(text, "PEC_CH00"));
    }

    [Fact]
    public void ARunWithAnInfiniteAdvance_IsNotTakenForACopy()
    {
        // A character spacing of 1e308 overflows the second run's two-character advance to infinity.
        // No tolerance comes from that, so the run is compared with nothing — rather than found
        // within an infinite distance of every earlier "AA". PDF numbers have no exponent, and an
        // integer that long does not fit a long, so the value is written out as a real.
        //
        // Under a shear, because under the identity the end's other coordinate is infinity × 0 —
        // NaN — and a NaN tolerance finds nothing whether or not anything checks for it.
        string huge = "1" + new string('0', 308) + ".0";
        string text = Extract($"q 2 1 1 2 0 0 cm\nBT /F1 12 Tf 72 700 Td (AA) Tj ET\n" +
                              $"BT /F1 12 Tf {huge} Tc 72 700 Td (AA) Tj ET\nQ");

        Assert.Equal(2, Occurrences(text, "AA"));
    }

    private static int Occurrences(string text, string value)
    {
        int count = 0;
        for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
