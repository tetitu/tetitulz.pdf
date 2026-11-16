// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Symbol and ZapfDingbats — the two standard faces a reader draws through their own built-in
/// encoding whatever <c>/Encoding</c> says (ISO 32000-1 §9.6.6.4) — shown from PostScript (Wave 17
/// R10.3).
///
/// <para>Until 2026-10-16 the plugin measured them through WinAnsi against a width table Core did
/// not have: <c>(ab) show</c> in Symbol advanced the pen by <b>nothing</b>, reported both bytes as
/// having "no glyph", and declared <c>/Encoding /WinAnsiEncoding</c> on a font whose glyphs are
/// αβ — the right picture (a reader ignores that declaration) behind a report blaming the program,
/// and every later string drawn on top of it.</para>
/// </summary>
public sealed class SymbolFontTests
{
    private static PostScriptConversionResult Convert(string ps)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(ps));

    /// <summary>Puts the pen position into the stream as the operands of an <c>m</c>.</summary>
    private const string ReadBackPen = " currentpoint newpath moveto 0 0 lineto stroke";

    private static PdfDictionary OnlyFont(PostScriptConversionResult r)
    {
        var fonts = Assert.IsType<PdfDictionary>(r.Resources?[PdfName.Font]);
        PdfDictionary only = null;
        foreach (var kv in fonts)
        {
            Assert.Null(only);
            only = (PdfDictionary)kv.Value;
        }
        Assert.NotNull(only);
        return only;
    }

    /// <summary>Symbol 'a' is alpha (631) and 'b' beta (549): 11.80 at size 10, nothing reported.</summary>
    [Fact]
    public void Symbol_ShowAdvancesByTheBuiltInWidthsAndIsClean()
    {
        var r = Convert("/Symbol findfont 10 scalefont setfont newpath 0 0 moveto (ab) show" + ReadBackPen);

        double[] pen = ContentStreamAssert.FirstOperandsOf(r.Content, "m");
        Assert.Equal(11.80, pen[0], 2);
        Assert.Equal(0, pen[1], 2);
        Assert.True(r.IsClean, r.LastError);
    }

    /// <summary>ZapfDingbats '!' is a1 (974).</summary>
    [Fact]
    public void ZapfDingbats_ShowAdvancesByTheBuiltInWidths()
    {
        var r = Convert("/ZapfDingbats findfont 10 scalefont setfont newpath 0 0 moveto (!) show" + ReadBackPen);

        Assert.Equal(9.74, ContentStreamAssert.FirstOperandsOf(r.Content, "m")[0], 2);
        Assert.True(r.IsClean, r.LastError);
    }

    /// <summary>
    /// The emitted font declares no <c>/Encoding</c>: the built-in one is what draws, and WinAnsi
    /// would misdescribe the font. The control: Helvetica still declares WinAnsiEncoding.
    /// </summary>
    [Theory]
    [InlineData("/Symbol", null)]
    [InlineData("/ZapfDingbats", null)]
    [InlineData("/Helvetica", "WinAnsiEncoding")]
    public void ABuiltInEncodedFont_DeclaresNoEncoding(string psFont, string expectedEncoding)
    {
        var r = Convert($"{psFont} findfont 10 scalefont setfont newpath 0 0 moveto (a) show");

        var font = OnlyFont(r);
        Assert.Equal(expectedEncoding, (font[PdfName.Encoding] as PdfName)?.Value);
    }

    /// <summary>
    /// <c>glyphshow</c> by name reaches a Symbol glyph through the built-in encoding — byte 0x61
    /// for alpha, in the same font resource as <c>show</c> — rather than minting a one-glyph
    /// <c>/Differences</c> font for a glyph the font already encodes.
    /// </summary>
    [Fact]
    public void Symbol_GlyphShowUsesTheBuiltInCode()
    {
        var r = Convert("/Symbol findfont 10 scalefont setfont newpath 0 0 moveto (b) show /alpha glyphshow"
                        + ReadBackPen);

        ContentStreamAssert.Contains(r.Content, "(a) Tj");
        OnlyFont(r);                                                    // one resource, not two
        Assert.Equal(11.80, ContentStreamAssert.FirstOperandsOf(r.Content, "m")[0], 2);
    }

    /// <summary>
    /// A Symbol glyph the built-in encoding leaves empty is reported — the one case the old
    /// report was right about — with the built-in encoding named as the cause, not WinAnsi.
    /// </summary>
    [Fact]
    public void Symbol_AnEmptyCode_IsReportedAgainstTheBuiltInEncoding()
    {
        var r = Convert("/Symbol findfont 10 scalefont setfont newpath 0 0 moveto <7F> show");

        Assert.Contains("built-in encoding", r.LastError);
        Assert.DoesNotContain("WinAnsi", r.LastError);
    }

    /// <summary>
    /// A re-encoded Symbol: the <c>/Differences</c> sit on the font's own base — no
    /// <c>/BaseEncoding /WinAnsiEncoding</c> — and the glyph named there is measured by name.
    /// Omega is 768.
    /// </summary>
    [Fact]
    public void ReEncodedSymbol_DifferencesCarryNoWinAnsiBaseAndMeasureByName()
    {
        var r = Convert("/EncodingArray 256 array def 0 1 255 { EncodingArray exch /.notdef put } for " +
                        "EncodingArray 65 /Omega put " +
                        "/Symbol findfont dup length dict begin " +
                        "{1 index /FID ne {def} {pop pop} ifelse} forall " +
                        "/Encoding EncodingArray def " +
                        "currentdict end /GreekCaps exch definefont pop " +
                        "/GreekCaps findfont 10 scalefont setfont newpath 0 0 moveto (A) show" + ReadBackPen);

        var encoding = Assert.IsType<PdfDictionary>(OnlyFont(r)[PdfName.Encoding]);
        Assert.Null(encoding[PdfName.BaseEncoding]);
        var differences = Assert.IsType<PdfArray>(encoding[PdfName.Differences]);
        Assert.Equal("Omega", ((PdfName)differences[1]).Value);

        Assert.Equal(7.68, ContentStreamAssert.FirstOperandsOf(r.Content, "m")[0], 2);
        Assert.True(r.IsClean, r.LastError);
    }
}
