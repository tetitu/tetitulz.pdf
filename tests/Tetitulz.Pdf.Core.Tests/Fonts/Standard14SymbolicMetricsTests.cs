// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// The AFM metrics of Symbol and ZapfDingbats. Without them — <c>GetWidthByUnicode</c> answering 0
/// for every character — a <c>/Symbol</c> font with no <c>/Widths</c> would measure
/// <c>/MissingWidth</c> and every glyph land on one point, and the PostScript plugin would advance
/// its pen by nothing for <c>(abg) show</c>.
///
/// <para>The values are the published AFMs' (read back through the reference renderer's metric-compatible URW
/// clones): Symbol <c>alpha</c> 631, <c>Omega</c> 768, <c>universal</c> 713, <c>space</c> 250;
/// ZapfDingbats <c>a1</c> 974, <c>a191</c> 918, <c>space</c> 278.</para>
/// </summary>
public class Standard14SymbolicMetricsTests
{
    [Theory]
    [InlineData(StandardFonts.Symbol, 0x61, "alpha", 631)]
    [InlineData(StandardFonts.Symbol, 0x57, "Omega", 768)]
    [InlineData(StandardFonts.Symbol, 0x22, "universal", 713)]
    [InlineData(StandardFonts.Symbol, 0x20, "space", 250)]
    [InlineData(StandardFonts.ZapfDingbats, 0x21, "a1", 974)]
    [InlineData(StandardFonts.ZapfDingbats, 0xFE, "a191", 918)]
    [InlineData(StandardFonts.ZapfDingbatsAbbrev, 0x21, "a1", 974)]
    public void BuiltInGlyph_IsNamedAndMeasuredByCode(string font, int code, string glyph, double width)
    {
        Assert.True(Standard14Metrics.TryGetBuiltInGlyph(font, code, out string name, out double w));
        Assert.Equal(glyph, name);
        Assert.Equal(width, w);
    }

    [Theory]
    [InlineData(StandardFonts.Helvetica, 0x61)]     // a Latin face has no built-in table here
    [InlineData(StandardFonts.Symbol, 0x00)]        // an empty code
    [InlineData(StandardFonts.Symbol, 0xFF)]        // the AFM's last code is 254
    [InlineData(StandardFonts.ZapfDingbats, 0xF0)]  // empty in Dingbats too
    public void BuiltInGlyph_IsAbsentOutsideTheTable(string font, int code)
    {
        Assert.False(Standard14Metrics.TryGetBuiltInGlyph(font, code, out string name, out double w));
        Assert.Null(name);
        Assert.Equal(0, w);
    }

    [Theory]
    [InlineData(StandardFonts.Symbol, "alpha", 631)]
    [InlineData(StandardFonts.Symbol, "radicalex", 500)]
    [InlineData(StandardFonts.ZapfDingbats, "a191", 918)]
    [InlineData(StandardFonts.ZapfDingbats, "space", 278)]
    public void WidthByGlyphName_AnswersTheAfm(string font, string glyph, double width)
    {
        Assert.True(Standard14Metrics.TryGetWidthByGlyphName(font, glyph, out double w));
        Assert.Equal(width, w);
    }

    [Theory]
    [InlineData(StandardFonts.Symbol, "a1")]         // a Dingbats name is not a Symbol glyph
    [InlineData(StandardFonts.Symbol, "A")]          // Symbol has Alpha, not Latin A
    [InlineData(StandardFonts.Helvetica, "alpha")]   // no name table for a Latin face
    [InlineData(StandardFonts.Symbol, null)]
    public void WidthByGlyphName_RefusesANameOutsideTheFont(string font, string glyph)
    {
        Assert.False(Standard14Metrics.TryGetWidthByGlyphName(font, glyph, out double w));
        Assert.Equal(0, w);
    }

    /// <summary>
    /// <c>GetWidthByUnicode</c> — the twelve Latin faces' entry point — reaches the two symbolic
    /// fonts through the Adobe Glyph List, so a reader holding a character rather than a name
    /// gets the AFM width too. Latin 'A' is not a Symbol glyph (Symbol has Alpha, U+0391).
    /// </summary>
    [Theory]
    [InlineData(StandardFonts.Symbol, 'α', 631)]        // alpha
    [InlineData(StandardFonts.Symbol, '∀', 713)]        // universal
    [InlineData(StandardFonts.Symbol, 'A', 0)]
    [InlineData(StandardFonts.ZapfDingbats, '✁', 974)]  // a1
    [InlineData(StandardFonts.Helvetica, 'A', 667)]          // the Latin control
    public void WidthByUnicode_ReachesTheSymbolicFontsThroughTheGlyphList(string font, char c, double width)
        => Assert.Equal(width, Standard14Metrics.GetWidthByUnicode(font, c));

    [Theory]
    [InlineData(StandardFonts.Symbol, "alpha", 0x61)]
    [InlineData(StandardFonts.ZapfDingbats, "a191", 0xFE)]
    [InlineData(StandardFonts.Symbol, "A", -1)]
    [InlineData(StandardFonts.Helvetica, "A", -1)]   // WinAnsi is not a built-in encoding
    public void BuiltInCodeOf_InvertsTheEncoding(string font, string glyph, int code)
        => Assert.Equal(code, Standard14Metrics.BuiltInCodeOf(font, glyph));

    [Theory]
    [InlineData(StandardFonts.Symbol, true)]
    [InlineData(StandardFonts.ZapfDingbats, true)]
    [InlineData(StandardFonts.ZapfDingbatsAbbrev, true)]
    [InlineData(StandardFonts.Helvetica, false)]
    [InlineData(StandardFonts.Courier, false)]
    [InlineData(null, false)]
    public void HasBuiltInEncoding_NamesExactlyTheTwo(string font, bool expected)
        => Assert.Equal(expected, Standard14Metrics.HasBuiltInEncoding(font));

    /// <summary>The AFMs carry 190 and 202 glyphs; the tables carry exactly those.</summary>
    [Theory]
    [InlineData(StandardFonts.Symbol, 190)]
    [InlineData(StandardFonts.ZapfDingbats, 202)]
    public void Tables_HoldTheAfmsGlyphCounts(string font, int expected)
    {
        int count = 0;
        for (int code = 0; code < 256; code++)
            if (Standard14Metrics.TryGetBuiltInGlyph(font, code, out _, out _)) count++;
        Assert.Equal(expected, count);
    }

    // ------------------------------------------------------------------
    //  The reader: a /Symbol font with no /Widths
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>PdfSimpleFont.GetWidth</c>: a Standard-14 font with no <c>/Widths</c> measures its AFM
    /// — and for Symbol and ZapfDingbats that is by code through the built-in encoding, whatever
    /// <c>/Encoding</c> the dictionary carries (§9.6.6.4) — not <c>/MissingWidth</c>, which is 0
    /// here.
    /// </summary>
    [Theory]
    [InlineData(StandardFonts.Symbol, 0x61, 631)]
    [InlineData(StandardFonts.Symbol, 0x57, 768)]
    [InlineData(StandardFonts.ZapfDingbats, 0x21, 974)]
    [InlineData(StandardFonts.Symbol, 0x7F, 0)]       // empty in the encoding: /MissingWidth
    [InlineData(StandardFonts.Helvetica, 0x41, 667)]  // the Latin control, through WinAnsi
    public void ASymbolicStandard14FontWithNoWidths_MeasuresItsAfmByCode(string font, int code, double width)
    {
        var pdfFont = LoadSimpleFont($"<< /Type /Font /Subtype /Type1 /BaseFont /{font} /Encoding /WinAnsiEncoding >>");
        Assert.Equal(width, pdfFont.GetWidth(code));
    }

    private static PdfFont LoadSimpleFont(string fontDictionary)
    {
        // A one-page document whose only font is the dictionary under test — the same shape
        // Standard14WidthFallbackTests builds: no /Widths, no /FontDescriptor, so /MissingWidth is 0.
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 4 0 R >> >> >>",
            fontDictionary,
        };

        var offsets = new int[bodies.Length + 1];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Length; i++)
            sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        var doc = PdfDocument.Open(new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString())));
        var fonts = doc.Pages[0].Resources.GetDictionary(PdfName.Font);
        var fontDict = doc.ResolveObject(fonts[new PdfName("F1")]) as PdfDictionary;
        return PdfFont.Create(fontDict, doc);
    }
}
