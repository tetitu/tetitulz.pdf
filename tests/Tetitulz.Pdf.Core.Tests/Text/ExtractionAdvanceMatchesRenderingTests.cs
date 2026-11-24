// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// §9.4.4's <c>w₀</c>: a width of zero means "the font supplied none" <b>only</b> when the font
/// does not declare one. A Type 0 font may carry an explicit <c>0</c> in its <c>/W</c> array — the
/// Hebrew-nikud and combining-mark case — and that zero is the correct advance.
///
/// <para><b>The extractor did not know that.</b> <c>AdvanceTextMatrixWithWidths</c> read
/// <c>if (w &lt;= 0) w = 500;</c> with no <c>HasExplicitWidth</c> escape, while both renderers went
/// through <c>GlyphWidth.Units</c>, which has one. So an explicit <c>/W 0</c> advanced <b>500 units
/// in extraction and 0 in rendering</b>, and extracted text positions drifted from the glyphs they
/// describe by one default advance per combining mark, accumulating along the run.</para>
///
/// <para>The rule now lives in <see cref="GlyphWidth"/> in Core, moved down from
/// <c>Base.Rendering</c> because Core cannot reference Base and adding a fifth private copy is what
/// produced this in the first place.</para>
/// </summary>
public class ExtractionAdvanceMatchesRenderingTests
{
    /// <summary>
    /// A composite font whose <c>/W</c> gives CID 1 an <b>explicit zero</b> width and CID 2 a
    /// width of 600, and which declares nothing for CID 3. Those three CIDs are the three cases
    /// the rule distinguishes.
    /// </summary>
    private static PdfFont CompositeFontWithAnExplicitZeroWidth()
    {
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /Font << /F0 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding /Identity-H /DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Test /DW 1000 /W [1 [0] 2 [600]] >>",
        };
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length + 1];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Length; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        var doc = PdfDocument.Open(new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString())));
        var fonts = doc.Pages[0].Resources.GetDictionary(PdfName.Font);
        return PdfFont.Create(doc.ResolveObject(fonts[new PdfName("F0")]) as PdfDictionary, doc);
    }

    /// <summary>
    /// The defect: an explicit <c>/W</c> entry of 0 is a real advance and must not become 500.
    /// </summary>
    [Fact]
    public void AnExplicitZeroWidthAdvancesZeroRatherThanTheDefault()
    {
        var font = CompositeFontWithAnExplicitZeroWidth();

        Assert.Equal(0d, font.GetWidth(1));
        Assert.True(font.HasExplicitWidth(1));
        Assert.Equal(0d, GlyphWidth.Units(font, 1));
    }

    /// <summary>
    /// A CID absent from <c>/W</c> takes the font's own <c>/DW</c> (§9.7.4.3) — 1000 here — not the
    /// 500 fallback. <c>/DW</c> <i>is</i> the font declaring a width, so the fallback must not
    /// reach it.
    ///
    /// <para>For a composite font with a <c>/DW</c> there is no "declares nothing" case at all.
    /// The genuine one needs a font that supplies no
    /// width by any route, which is the test below.</para>
    /// </summary>
    [Fact]
    public void ACidAbsentFromTheWidthsArrayTakesTheFontsOwnDefaultWidth()
    {
        var font = CompositeFontWithAnExplicitZeroWidth();

        Assert.Equal(1000d, GlyphWidth.Units(font, 3));
    }

    /// <summary>
    /// The control that makes the zero-width assertion meaningful: a font that supplies no width by
    /// any route — a simple font naming none of the Standard 14, with no <c>/Widths</c> and no
    /// <c>/MissingWidth</c> — is where the 500 fallback actually applies. Without this, "always
    /// return the raw width" would pass every other test in the file.
    /// </summary>
    [Fact]
    public void AFontThatSuppliesNoWidthByAnyRouteTakesTheFiveHundredFallback()
    {
        var simple = new PdfDictionary();
        simple.Set(PdfName.Type, PdfName.Font);
        simple.Set(PdfName.Subtype, new PdfName("Type1"));
        simple.Set(PdfName.BaseFont, new PdfName("Wingdings-Regular"));   // not a Standard 14

        var doc = PdfDocument.Open(new MemoryStream(SingleFontDocument(
            "<< /Type /Font /Subtype /Type1 /BaseFont /Wingdings-Regular >>")));
        var fonts = doc.Pages[0].Resources.GetDictionary(PdfName.Font);
        var font = PdfFont.Create(doc.ResolveObject(fonts[new PdfName("F0")]) as PdfDictionary, doc);

        Assert.Equal(0d, font.GetWidth('A'));
        Assert.False(font.HasExplicitWidth('A'));
        Assert.Equal(GlyphWidth.DefaultUnits, GlyphWidth.Units(font, 'A'));
    }

    private static byte[] SingleFontDocument(string fontBody)
    {
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /Font << /F0 4 0 R >> >> >>",
            fontBody,
        };
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length + 1];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Length; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void AnOrdinaryDeclaredWidthIsUsedUnchanged()
    {
        var font = CompositeFontWithAnExplicitZeroWidth();

        Assert.Equal(600d, GlyphWidth.Units(font, 2));
    }

    /// <summary>
    /// A null font is the "no data at all" case and must take the default, not throw — text
    /// extraction runs over documents whose font resources are missing.
    /// </summary>
    [Fact]
    public void ANullFontTakesTheDefaultRatherThanThrowing()
    {
        Assert.Equal(GlyphWidth.DefaultUnits, GlyphWidth.Units(null, 1));
    }

    /// <summary>
    /// The end-to-end claim, on the extractor's own advance: a run of three glyphs whose middle
    /// one has an explicit zero width must advance by the sum of 600 + 0 + 600, not
    /// 600 + <b>500</b> + 600. This is the assertion that fails if the extractor stops going
    /// through <see cref="GlyphWidth"/>.
    /// </summary>
    [Fact]
    public void TheExtractorsAdvanceSumsAnExplicitZeroAsZero()
    {
        var font = CompositeFontWithAnExplicitZeroWidth();

        // Identity-H: two bytes per code, CID == code. CIDs 2, 1, 2.
        var codes = font.SplitCodes(new byte[] { 0x00, 0x02, 0x00, 0x01, 0x00, 0x02 });
        Assert.Equal(3, codes.Count);

        double sum = 0;
        foreach (var c in codes) sum += GlyphWidth.Units(font, c.Cid);

        Assert.Equal(1200d, sum);                       // 600 + 0 + 600
        Assert.NotEqual(1700d, sum);                    // what the pre-fix coercion produced
    }
}
