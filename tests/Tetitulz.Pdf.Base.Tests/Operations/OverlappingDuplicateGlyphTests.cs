// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Text drawn twice at (nearly) the same place is extracted once.
/// </summary>
/// <remarks>
/// <para><b>Found in the local corpus.</b> An InDesign slug line strokes and then fills its text at
/// the same position, and structured extraction interleaved the two copies once sorted by X:
/// <c>PEC_CH00_FM_2PP.indd</c> came back as <c>PPEECC__CCHH0000__FFMM__22PPPP..iinndddd</c>. "Fake
/// bold" repaints a run a fraction of a point to the side and does the same.</para>
///
/// <para>The rule is an independent implementation's <c>suppressDuplicateOverlappingText</c>, on by default there: a later
/// glyph is dropped when an earlier one with the same text lies within a third of its per-character
/// advance on both axes. Here the two must also share a marked-content sequence.</para>
///
/// <para>Every glyph advances 600 units, so at 12 pt it is 7.2 pt wide and the tolerance is 2.4 pt.</para>
/// </remarks>
public sealed class OverlappingDuplicateGlyphTests
{
    private static readonly string Uniform =
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 32 /LastChar 126 /Widths [" +
        string.Join(" ", Enumerable.Repeat(600, 95)) + "] >>";

    private static PdfPageTextLayout Page(string contentStream)
    {
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(contentStream)} >>\nstream\n{contentStream}\nendstream",
            Uniform,
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.AppendFormat("{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        using var ms = new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString()), writable: false);
        return PdfStructuredTextExtractor.Extract(ms)[0];
    }

    private static string[] Words(PdfPageTextLayout page) =>
        page.Lines.SelectMany(l => l.Words).Select(w => w.Text).ToArray();

    private static int Count(PdfPageTextLayout page, string ch) =>
        page.Lines.SelectMany(l => l.Words).SelectMany(w => w.Chars).Count(c => c.Char == ch);

    [Fact]
    public void ARunStrokedThenFilledAtTheSamePlace_IsExtractedOnce()
    {
        var page = Page(
            "BT /F1 12 Tf 1 Tr 100 700 Td (PEC_CH00.indd) Tj ET\n" +
            "BT /F1 12 Tf 0 Tr 100 700 Td (PEC_CH00.indd) Tj ET\n");

        Assert.Equal(new[] { "PEC_CH00.indd" }, Words(page));
    }

    [Fact]
    public void ARunRepaintedAFractionOfAPointAside_IsExtractedOnce()
    {
        var page = Page(
            "BT /F1 12 Tf 100 700 Td (Bold) Tj ET\n" +
            "BT /F1 12 Tf 100.4 700.3 Td (Bold) Tj ET\n");

        Assert.Equal(new[] { "Bold" }, Words(page));
    }

    [Fact]
    public void RepeatedLettersSideBySide_AreAllKept()
    {
        var page = Page("BT /F1 12 Tf 100 700 Td (AAAA) Tj ET\n");

        Assert.Equal(new[] { "AAAA" }, Words(page));
    }

    [Theory]
    [InlineData(2.5, 0)]
    [InlineData(0, 2.5)]
    public void TheSameLetterJustBeyondAThirdOfItsAdvance_IsKept(double dx, double dy)
    {
        string second = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "BT /F1 12 Tf {0} {1} Td (A) Tj ET\n", 100 + dx, 700 + dy);
        var page = Page("BT /F1 12 Tf 100 700 Td (A) Tj ET\n" + second);

        Assert.Equal(2, Count(page, "A"));
    }

    [Theory]
    [InlineData(2.3, 0)]
    [InlineData(0, 2.3)]
    public void TheSameLetterJustWithinAThirdOfItsAdvance_IsDropped(double dx, double dy)
    {
        string second = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "BT /F1 12 Tf {0} {1} Td (A) Tj ET\n", 100 + dx, 700 + dy);
        var page = Page("BT /F1 12 Tf 100 700 Td (A) Tj ET\n" + second);

        Assert.Equal(1, Count(page, "A"));
    }

    [Fact]
    public void DifferentLettersAtTheSamePlace_AreBothKept()
    {
        var page = Page(
            "BT /F1 12 Tf 100 700 Td (O) Tj ET\n" +
            "BT /F1 12 Tf 100 700 Td (/) Tj ET\n");

        Assert.Equal(1, Count(page, "O"));
        Assert.Equal(1, Count(page, "/"));
    }

    [Fact]
    public void OverflowingGlyphGeometry_DoesNotCostThePageItsText()
    {
        // A 3e38 text-matrix scale makes each "A" infinitely wide, and moving two units left puts
        // both at X = -infinity. A range search around such a glyph has inverted bounds and throws,
        // which used to cost the whole page its text.
        var page = Page(
            "BT /F1 12 Tf 300000000000000000000000000000000000000.0 0 0 1 0 0 Tm -2 0 Td " +
            "(A) Tj 0 0 Td (A) Tj ET\n" +
            "BT /F1 12 Tf 100 700 Td (Hello) Tj ET\n");

        Assert.Contains("Hello", page.Lines.Select(l => l.Text));
        Assert.Null(PdfStructuredTextExtractor.LastError);
    }

    [Fact]
    public void TheToleranceIsAThirdOfEachCharactersShareOfAMultiCharacterGlyph()
    {
        // One glyph extracted as "fi" is 7.2 pt wide, so each character's share is 3.6 pt and the
        // tolerance 1.2 pt. A copy 2 pt aside is further than that and is kept.
        const string toUnicode =
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n" +
            "1 begincodespacerange <00> <FF> endcodespacerange\n" +
            "1 beginbfchar\n<41> <00660069>\nendbfchar\nendcmap end end\n";
        var page = PageWithToUnicode(
            "BT /F1 12 Tf 100 700 Td (A) Tj ET\n" +
            "BT /F1 12 Tf 102 700 Td (A) Tj ET\n", toUnicode);

        Assert.Equal(2, Count(page, "fi"));
    }

    private static PdfPageTextLayout PageWithToUnicode(string contentStream, string toUnicode)
    {
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(contentStream)} >>\nstream\n{contentStream}\nendstream",
            Uniform.Replace(" >>", " /ToUnicode 6 0 R >>"),
            $"<< /Length {toUnicode.Length} >>\nstream\n{toUnicode}\nendstream",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.AppendFormat("{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        using var ms = new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString()), writable: false);
        return PdfStructuredTextExtractor.Extract(ms)[0];
    }

    [Fact]
    public void ACopyInAnotherMarkedContentSequence_IsKept()
    {
        // Keeping the earlier copy could drop the one a structure element owns.
        var page = Page(
            "/Artifact BMC BT /F1 12 Tf 100 700 Td (A) Tj ET EMC\n" +
            "/P <</MCID 0>> BDC BT /F1 12 Tf 100 700 Td (A) Tj ET EMC\n");

        Assert.Equal(2, Count(page, "A"));
    }
}
