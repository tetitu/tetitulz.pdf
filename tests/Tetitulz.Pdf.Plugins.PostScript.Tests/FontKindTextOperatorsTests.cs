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
/// The text operators other than <c>show</c> against a font that is not a standard face.
///
/// <para><c>show</c> dispatched on the font's kind — a Type 3 font's glyph procedures are run, a
/// composite font's bytes pass into a Type0 — and <c>xshow</c>/<c>yshow</c>/<c>xyshow</c>,
/// <c>glyphshow</c>, <c>stringwidth</c> and <c>charpath</c> did not: each took the standard-14 path
/// with whatever base name the state held, so a Type 3 font's <c>xshow</c> drew Helvetica's letters
/// silently, its <c>stringwidth</c> measured Helvetica, and a composite font's bytes were measured
/// and split one byte at a time. Every assertion below reads the resource the text was shown in or
/// the width the program read back.</para>
/// </summary>
public sealed class FontKindTextOperatorsTests
{
    static FontKindTextOperatorsTests()
    {
        new AdobeCMapPlugin().Initialize();
    }

    private static PostScriptConversionResult Convert(string ps)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps));

    /// <summary>A Type 3 font whose one glyph, 'A', is 750 units wide.</summary>
    private const string SquareFont =
        "8 dict dup begin /FontType 3 def /FontMatrix [.001 0 0 .001 0 0] def " +
        "/FontBBox [0 0 1000 1000] def /Encoding 256 array def Encoding 65 /square put " +
        "/CharProcs 1 dict def " +
        "CharProcs /square { 750 0 0 0 750 750 setcachedevice 0 0 750 750 rectfill } put " +
        "/BuildGlyph { exch begin CharProcs exch get exec end } def end " +
        "/T3 exch definefont pop /T3 findfont 12 scalefont setfont ";

    private const string Cjk = "/KozMinPro-Regular-90ms-RKSJ-H findfont 10 scalefont setfont ";

    /// <summary>Puts the top two numbers of the stack into an <c>m</c>.</summary>
    private const string ShowTopTwo = " newpath moveto 0 0 lineto stroke ";

    private static double[] LastOperandsOf(byte[] content, string op)
        => ContentStreamAssert.Parse(content).Last(o => o.Operator == op).Operands
               .Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private static PdfDictionary OnlyFont(PostScriptConversionResult r)
    {
        var fonts = (PdfDictionary)r.Resources[PdfName.Font];
        Assert.Single(fonts);
        foreach (var kv in fonts) return (PdfDictionary)kv.Value;
        return null;
    }

    // ==================================================================
    //  Type 3
    // ==================================================================

    [Fact]
    public void XShow_Type3_DrawsTheType3Font()
    {
        var r = Convert(SquareFont + "0 0 moveto (AA) [ 20 20 ] xshow");

        Assert.Equal("Type3", ((PdfName)OnlyFont(r)[PdfName.Subtype]).Value);
        Assert.Equal(2, ContentStreamAssert.Count(r.Content, "Tj"));
    }

    /// <summary>The second glyph is placed by the displacement, not by the glyph's own width.</summary>
    [Fact]
    public void XShow_Type3_PlacesByTheDisplacements()
    {
        var r = Convert(SquareFont + "0 0 moveto (AA) [ 20 20 ] xshow");

        var tms = ContentStreamAssert.Parse(r.Content).Where(o => o.Operator == "Tm").ToList();
        Assert.Equal(2, tms.Count);
        Assert.Equal("20", tms[1].Operands[4]);
    }

    [Fact]
    public void GlyphShow_Type3_DrawsTheNamedGlyphProcedure()
    {
        var r = Convert(SquareFont + "0 0 moveto /square glyphshow");

        var font = OnlyFont(r);
        Assert.Equal("Type3", ((PdfName)font[PdfName.Subtype]).Value);
        Assert.NotNull(((PdfDictionary)font[PdfName.CharProcs])[new PdfName("square")]);
        Assert.Equal(1, ContentStreamAssert.Count(r.Content, "Tj"));
    }

    /// <summary>750 glyph units through the 0.001 glyph matrix at size 12, twice: 18 points.</summary>
    [Fact]
    public void StringWidth_Type3_MeasuresTheGlyphProceduresDeclaredWidths()
    {
        var r = Convert(SquareFont + "(AA) stringwidth" + ShowTopTwo);

        double[] m = LastOperandsOf(r.Content, "m");
        Assert.Equal(18.0, m[0], 6);
        Assert.Equal(0.0, m[1], 6);
    }

    /// <summary>The outline <c>charpath</c> appends is as wide as the Type 3 glyphs.</summary>
    [Fact]
    public void CharPath_Type3_OutlineIsAsWideAsTheGlyphs()
    {
        var r = Convert(SquareFont + "newpath 0 0 moveto (AA) false charpath pathbbox " +
                        "/ury exch def /urx exch def pop pop urx ury" + ShowTopTwo);

        Assert.Equal(18.0, LastOperandsOf(r.Content, "m")[0], 6);
    }

    /// <summary>The control: a standard face still measures through its AFM table (Helvetica 'A' is 667).</summary>
    [Fact]
    public void StringWidth_StandardFont_StillMeasuresTheAfmWidth()
    {
        var r = Convert("/Helvetica findfont 12 scalefont setfont (AA) stringwidth" + ShowTopTwo);

        Assert.Equal(16.008, LastOperandsOf(r.Content, "m")[0], 3);
    }

    // ==================================================================
    //  Composite
    // ==================================================================

    /// <summary>Four bytes under a mixed one/two-byte CMap are two codes at /DW 1000: 20 points at size 10.</summary>
    [Fact]
    public void StringWidth_Composite_CountsCodesNotBytes()
    {
        var r = Convert(Cjk + "<82A082A0> stringwidth" + ShowTopTwo);

        Assert.Equal(20.0, LastOperandsOf(r.Content, "m")[0], 6);
    }

    /// <summary>Each code, not each byte, is one positioned glyph against the Type0 font.</summary>
    [Fact]
    public void XShow_Composite_SplitsTheStringIntoCodes()
    {
        var r = Convert(Cjk + "0 0 moveto <82A082A0> [ 30 30 ] xshow");

        Assert.Equal("Type0", ((PdfName)OnlyFont(r)[PdfName.Subtype]).Value);
        Assert.Equal(2, ContentStreamAssert.Count(r.Content, "Tj"));
    }

    /// <summary>A single-byte code in a mixed CMap is one code too.</summary>
    [Fact]
    public void XShow_Composite_SingleByteCodes_AreOneGlyphEach()
    {
        var r = Convert(Cjk + "0 0 moveto <4182A0> [ 30 30 ] xshow");

        Assert.Equal(2, ContentStreamAssert.Count(r.Content, "Tj"));
    }
}
