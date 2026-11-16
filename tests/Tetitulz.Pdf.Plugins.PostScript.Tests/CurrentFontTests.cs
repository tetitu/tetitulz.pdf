// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.CMap;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// <c>currentfont</c> returns the font dictionary in the graphics state (PLRM §8.2) — the same
/// object <c>setfont</c> was given — so <c>currentfont 0.8 scalefont setfont</c>, the idiom for a
/// superscript or a footnote, keeps everything the font was: its <c>/Encoding</c>, its Type 3
/// procedures, its CMap, its embedded program. It used to build a fresh two-entry dictionary
/// holding the base name and the matrix, so every one of those was lost at the first
/// <c>currentfont</c> and the text after it drew in plain Helvetica.
/// </summary>
public sealed class CurrentFontTests
{
    static CurrentFontTests()
    {
        new AdobeCMapPlugin().Initialize();
    }

    private static PostScriptConversionResult Convert(string ps)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps));

    private static PdfDictionary ShownFont(PostScriptConversionResult r)
    {
        var tf = ContentStreamAssert.Parse(r.Content).Last(o => o.Operator == "Tf");
        var fonts = (PdfDictionary)r.Resources[PdfName.Font];
        return (PdfDictionary)fonts[new PdfName(tf.Operands[0].TrimStart('/'))];
    }

    private static double[] LastTm(PostScriptConversionResult r)
        => ContentStreamAssert.Parse(r.Content).Last(o => o.Operator == "Tm").Operands
               .Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private const string GreyForTrue  = " { 0.25 } { 0.75 } ifelse setgray ";
    private const string PaintTriangle = " newpath 0 0 moveto 10 0 lineto 10 10 lineto fill ";

    /// <summary>It is the same object, by <c>eq</c>.</summary>
    [Fact]
    public void CurrentFont_IsTheDictionarySetFontWasGiven()
    {
        var r = Convert("/F /Helvetica findfont 12 scalefont def F setfont currentfont F eq" +
                        GreyForTrue + PaintTriangle);

        Assert.Equal(0.25, ContentStreamAssert.FirstOperandsOf(r.Content, "g")[0], 6);
    }

    /// <summary>A re-encoded font rescaled through <c>currentfont</c> keeps its <c>/Differences</c>.</summary>
    [Fact]
    public void CurrentFont_RescaledReEncodedFont_KeepsItsEncoding()
    {
        var slots = new string[256];
        for (int i = 0; i < 256; i++) slots[i] = "/.notdef";
        slots[0xE1] = "/alpha";

        var r = Convert("/EncodingArray [ " + string.Join(" ", slots) + " ] def " +
                        "/Helvetica findfont dup length dict begin " +
                        "{1 index /FID ne {def} {pop pop} ifelse} forall " +
                        "/Encoding EncodingArray def currentdict end /CustomFont exch definefont pop " +
                        "/CustomFont findfont 12 scalefont setfont " +
                        "currentfont 2 scalefont setfont newpath 0 0 moveto (\\341) show");

        var font = ShownFont(r);
        Assert.True(font[PdfName.Encoding] is PdfDictionary enc && enc[PdfName.Differences] != null,
                    "the re-encoding was lost through currentfont");
        Assert.Equal(24.0, LastTm(r)[0], 6);
    }

    /// <summary>A Type 3 font rescaled through <c>currentfont</c> is still a Type 3 font.</summary>
    [Fact]
    public void CurrentFont_RescaledType3Font_IsStillType3()
    {
        var r = Convert(
            "8 dict dup begin /FontType 3 def /FontMatrix [.001 0 0 .001 0 0] def " +
            "/FontBBox [0 0 1000 1000] def /Encoding 256 array def Encoding 65 /square put " +
            "/CharProcs 1 dict def " +
            "CharProcs /square { 750 0 0 0 750 750 setcachedevice 0 0 750 750 rectfill } put " +
            "/BuildGlyph { exch begin CharProcs exch get exec end } def end " +
            "/T3 exch definefont pop " +
            "/T3 findfont 12 scalefont setfont currentfont 2 scalefont setfont 0 0 moveto (A) show");

        Assert.Equal("Type3", ((PdfName)ShownFont(r)[PdfName.Subtype]).Value);
        Assert.Equal(24.0, LastTm(r)[0], 6);
    }

    /// <summary>A composite font rescaled through <c>currentfont</c> is still a Type0 font.</summary>
    [Fact]
    public void CurrentFont_RescaledCompositeFont_IsStillType0()
    {
        var r = Convert("/KozMinPro-Regular-90ms-RKSJ-H findfont 12 scalefont setfont " +
                        "currentfont 2 scalefont setfont 0 0 moveto <82A0> show");

        Assert.Equal("Type0", ((PdfName)ShownFont(r)[PdfName.Subtype]).Value);
        Assert.Equal(24.0, LastTm(r)[0], 6);
    }

    /// <summary>The control: a plain standard face through <c>currentfont</c> is unchanged too.</summary>
    [Fact]
    public void CurrentFont_RescaledStandardFont_KeepsItsFace()
    {
        var r = Convert("/Times-Bold findfont 12 scalefont setfont " +
                        "currentfont 2 scalefont setfont 0 0 moveto (A) show");

        Assert.Equal("Times-Bold", ((PdfName)ShownFont(r)[PdfName.BaseFont]).Value);
        Assert.Equal(24.0, LastTm(r)[0], 6);
    }

    /// <summary>
    /// <c>showpage</c> in a fragment resets the graphics state the way <c>initgraphics</c> does, and
    /// that leaves the font alone: the second string is still Times-Bold at 24.
    /// </summary>
    [Fact]
    public void ShowPageInAFragment_KeepsTheCurrentFont()
    {
        var r = Convert("/Times-Bold findfont 24 scalefont setfont newpath 0 0 moveto (a) show " +
                        "showpage newpath 0 0 moveto (b) show");

        var tfs = ContentStreamAssert.Parse(r.Content).Where(o => o.Operator == "Tf").ToList();
        Assert.Equal(2, tfs.Count);
        Assert.Equal(tfs[0].Operands[0], tfs[1].Operands[0]);
        Assert.Equal("Times-Bold", ((PdfName)ShownFont(r)[PdfName.BaseFont]).Value);
        Assert.Equal(24.0, LastTm(r)[0], 6);
    }

    /// <summary>With no font set, <c>currentfont</c> still answers with a usable dictionary.</summary>
    [Fact]
    public void CurrentFont_WithNoFontSet_AnswersWithADictionary()
    {
        var r = Convert("currentfont type /dicttype eq" + GreyForTrue + PaintTriangle);

        Assert.Equal(0.25, ContentStreamAssert.FirstOperandsOf(r.Content, "g")[0], 6);
    }
}
