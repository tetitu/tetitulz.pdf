// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// Font-dictionary metrics written as <b>indirect references</b> must be read.
/// </summary>
/// <remarks>
/// <para><b><c>/FirstChar</c> is the sharp one, because its fallback is not a neutral sentinel.</b>
/// <c>fontDict.GetInteger(PdfName.FirstChar) ?? 0</c> tests the raw value's type, so a legal
/// <c>/FirstChar 12 0 R</c> read as absent and <c>_firstChar</c> became <c>0</c> — <b>the array
/// origin</b>. <c>GetWidth</c> computes <c>charCode - _firstChar</c>, so a font whose real
/// <c>/FirstChar</c> is 32 returns the width 32 entries early: not a default, not a zero, but
/// <b>a real width belonging to a different glyph</b>. <c>HasExplicitWidth</c> returns
/// <see langword="true"/> for it, so nothing downstream can tell. Both renderers and the text
/// extractor read those numbers and the error accumulates along the line.</para>
///
/// <para><b>The tell was already in the file.</b> <c>/Widths</c> is read one line later with an
/// explicit <c>ResolveObject</c> fallback, and in the Type 0 font <c>/W</c> is resolved two lines
/// after <c>/DW</c> is not. The resolve had been added where somebody noticed it missing, and not
/// to its neighbour.</para>
///
/// <para><b>Asserted on the advance, not on the glyph.</b> A page rendered with the wrong widths
/// contains all the right glyphs — only their positions are wrong — so any assertion about
/// characters present would pass with the defect intact. These read the width back through the
/// font.</para>
/// </remarks>
public class IndirectFontMetricTests
{
    /// <summary>
    /// The two spellings must give the same advance for the same code. The direct one is the
    /// control: it always worked, so a fixture built on it could never have gone red.
    /// </summary>
    [Theory]
    [InlineData(true)]   // /FirstChar 7 0 R
    [InlineData(false)]  // /FirstChar 32
    public void FirstChar_IsReadWhicheverWayItIsSpelled(bool indirect)
    {
        double width = WidthOfCode(SimpleFontPdf(indirect), 'A');

        // /FirstChar is 32 and /Widths starts at the space, so 'A' (65) is entry 33 — the value
        // this fixture sets to 777. With /FirstChar read as 0 the lookup lands on entry 65, which
        // this fixture deliberately gives a DIFFERENT real value, so the two cannot coincide.
        Assert.Equal(777, width);
    }

    /// <summary>
    /// <c>/BaseFont</c> is read whichever way it is spelled, and with it the Standard-14 AFM
    /// fallback that is keyed on the name.
    /// </summary>
    /// <remarks>
    /// A Type 1 Helvetica with no <c>/Widths</c> measures from the AFM tables — 667 for 'A'. With
    /// the name read as absent there is no Standard-14 to look up, and 'A' measured 0.
    /// </remarks>
    [Theory]
    [InlineData(true)]   // /BaseFont 6 0 R
    [InlineData(false)]  // /BaseFont /Helvetica
    public void BaseFont_IsReadWhicheverWayItIsSpelled(bool indirect)
    {
        using var doc = PdfDocument.Open(new MemoryStream(Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 5 0 R >> >> >>",
            "null",
            "<< /Type /Font /Subtype /Type1 /BaseFont " + (indirect ? "6 0 R" : "/Helvetica")
                + " /Encoding /WinAnsiEncoding >>",
            "/Helvetica")));

        var font = PdfFont.Create(Assert.IsAssignableFrom<PdfDictionary>(doc.GetObject(5)), doc);

        Assert.Equal("Helvetica", font.BaseFont);
        Assert.Equal(667, font.GetWidth('A'), 3);
    }

    /// <summary>An element of <c>/Widths</c> written as a reference is that element's width.</summary>
    [Theory]
    [InlineData(true)]   // entry 33 is 8 0 R
    [InlineData(false)]  // entry 33 is 777
    public void AWidthsElement_IsReadWhicheverWayItIsSpelled(bool indirect)
    {
        Assert.Equal(777, WidthOfCode(SimpleFontPdf(indirect: false, indirectWidthEntry: indirect), 'A'));
    }

    /// <summary>
    /// A composite font's <c>/BaseFont</c>, and a <c>/W</c> whose run and whose range width are
    /// references, are read whichever way they are spelled.
    /// </summary>
    /// <remarks>
    /// <c>/W [65 [777] 70 72 555]</c> against <c>/W [11 0 R 9 0 R 70 72 10 0 R]</c>, where the run
    /// 9 is itself <c>[12 0 R]</c> — so the first CID, the run, a width inside the run and a range
    /// width are each a reference, and each has a case that fails without its own resolve. The raw
    /// parse skipped every one of them as "not a number", so those CIDs took <c>/DW</c> — 1000.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompositeBaseFontAndWidths_AreReadWhicheverWayTheyAreSpelled(bool indirect)
    {
        using var doc = PdfDocument.Open(new MemoryStream(Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 5 0 R >> >> >>",
            "null",
            "<< /Type /Font /Subtype /Type0 /BaseFont " + (indirect ? "8 0 R" : "/SampleCid")
                + " /Encoding /Identity-H /DescendantFonts [6 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /SampleCid "
                + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
                + "/W " + (indirect ? "[11 0 R 9 0 R 70 72 10 0 R]" : "[65 [777] 70 72 555]") + " >>",
            "null",
            "/SampleCid",
            "[12 0 R]",
            "555",
            "65",
            "777")));

        var font = PdfFont.Create(Assert.IsAssignableFrom<PdfDictionary>(doc.GetObject(5)), doc);

        Assert.Equal("SampleCid", font.BaseFont);
        Assert.True(font.HasExplicitWidth(65));
        Assert.Equal(777, font.GetWidth(65), 3);
        Assert.Equal(555, font.GetWidth(71), 3);
    }

    // ------------------------------------------------------------------

    /// <summary>The advance the font reports for one character code.</summary>
    private static double WidthOfCode(byte[] pdf, char code)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var page = doc.Pages[0];
        var resources = doc.ResolveObject(page.Dictionary[PdfName.Resources]) as PdfDictionary;
        Assert.NotNull(resources);
        var fonts = doc.ResolveObject(resources![PdfName.Font]) as PdfDictionary;
        Assert.NotNull(fonts);
        var fontDict = doc.ResolveObject(fonts![new PdfName("F1")]) as PdfDictionary;
        Assert.NotNull(fontDict);

        var font = PdfFont.Create(fontDict!, doc);
        Assert.True(font.HasExplicitWidth(code), "the fixture's /Widths does not cover the probe code");
        return font.GetWidth(code);
    }

    /// <summary>
    /// A simple TrueType font with <c>/FirstChar 32</c> and a <c>/Widths</c> array long enough that
    /// index 65 also exists and holds a <b>different</b> value — so reading <c>/FirstChar</c> as 0
    /// returns a real width rather than falling off the end.
    /// </summary>
    private static byte[] SimpleFontPdf(bool indirect, bool indirectWidthEntry = false)
    {
        var widths = new StringBuilder("[");
        for (int i = 0; i <= 100; i++)
            widths.Append(' ').Append(i == 33 ? (indirectWidthEntry ? "8 0 R" : "777") : (i == 65 ? "111" : "500"));
        widths.Append(" ]");

        string firstChar = indirect ? "7 0 R" : "32";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R" +
            " /Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObj("BT /F1 12 Tf (A) Tj ET\n"),
            "<< /Type /Font /Subtype /TrueType /BaseFont /Helvetica /FirstChar " + firstChar +
            " /LastChar 132 /Widths 6 0 R >>",
            widths.ToString(),
            "32",
            "777");
    }

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "endstream";

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
