// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="Standard14Kerning"/>: the Core14 AFMs' <c>KPX</c> pairs, and the <c>TJ</c>
/// <see cref="PdfContentStreamBuilder"/> writes them as. The expected values are the AFM files' own
/// lines (Helvetica <c>KPX A V -70</c>, <c>KPX T o -120</c>; Helvetica-Bold <c>KPX A V -80</c>;
/// Times-Roman <c>KPX A V -135</c>, <c>KPX space A -55</c>).
/// </summary>
public class Standard14KerningTests
{
    [Theory]
    [InlineData(StandardFonts.Helvetica, "AV", -70)]
    [InlineData(StandardFonts.HelveticaOblique, "AV", -70)]          // an oblique kerns as its upright
    [InlineData(StandardFonts.HelveticaBold, "AV", -80)]
    [InlineData(StandardFonts.HelveticaBoldOblique, "AV", -80)]
    [InlineData(StandardFonts.TimesRoman, "AV", -135)]
    [InlineData(StandardFonts.TimesRoman, " A", -55)]
    [InlineData(StandardFonts.Helvetica, "To", -120)]
    public void APairIsTheAfmsKpxValue(string font, string pair, double expected)
        => Assert.Equal(new[] { expected }, Standard14Kerning.Adjustments(font, pair));

    [Fact]
    public void EachGapTakesItsOwnPair_AndAGapWithNoneIsZero()
        => Assert.Equal(new double[] { -120, 0, -70 }, Standard14Kerning.Adjustments(StandardFonts.Helvetica, "ToAV"));

    [Theory]
    [InlineData(StandardFonts.Courier, "AV")]          // no pairs in Courier's AFM
    [InlineData(StandardFonts.Symbol, "AV")]
    [InlineData(StandardFonts.Helvetica, "ab")]        // no pair applies
    [InlineData(StandardFonts.Helvetica, "A")]         // no gap
    [InlineData(StandardFonts.Helvetica, "")]
    [InlineData(StandardFonts.Helvetica, "AŖ")]   // drawn as A?, no pair; U+0156 as itself would alias A–V (0x41 << 8 | 0x156 = A, V)
    [InlineData("Arial", "AV")]                        // not a standard face
    public void NoPairApplies_IsNull(string font, string text)
        => Assert.Null(Standard14Kerning.Adjustments(font, text));

    /// <summary>The AFMs travel with Adobe's notice, as their licence requires, and keep their copyright line.</summary>
    [Fact]
    public void TheAfmsShipWithTheirNoticeAndCopyright()
    {
        var assembly = typeof(Standard14Kerning).Assembly;
        using (var notice = assembly.GetManifestResourceStream("Tetitulz.Pdf.Core.Fonts.Afm.MustRead.html"))
        {
            Assert.NotNull(notice);
            string text = new StreamReader(notice!, Encoding.ASCII).ReadToEnd();
            Assert.Contains("may be used, copied, and distributed for any purpose", text, System.StringComparison.Ordinal);
        }
        using var afm = assembly.GetManifestResourceStream("Tetitulz.Pdf.Core.Fonts.Afm.Helvetica.afm.gz");
        Assert.NotNull(afm);
        string head = new StreamReader(new System.IO.Compression.GZipStream(afm!, System.IO.Compression.CompressionMode.Decompress), Encoding.ASCII).ReadToEnd();
        Assert.StartsWith("StartFontMetrics", head, System.StringComparison.Ordinal);
        Assert.Contains("Copyright (c) 1985, 1987, 1989, 1990, 1997 Adobe Systems Incorporated", head, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The writer: each nonzero adjustment cuts the string and is written negated (a TJ number moves
    /// the next glyph left); escapes are kept in every piece; no adjustments is a plain Tj.
    /// </summary>
    [Fact]
    public void KernedTextIsWrittenAsTJ_TheAdjustmentNegated()
    {
        string Shown(string text, double[] adjustments)
            => Encoding.Latin1.GetString(new PdfContentStreamBuilder().ShowText(text, adjustments).Build());

        Assert.Contains("[(T) 120 (oA) 70 (V)] TJ", Shown("ToAV", new double[] { -120, 0, -70 }), System.StringComparison.Ordinal);
        Assert.Contains("[(\\() -20 (A)] TJ", Shown("(A", new double[] { 20 }), System.StringComparison.Ordinal);
        Assert.Contains("(AV) Tj", Shown("AV", null), System.StringComparison.Ordinal);
    }
}
