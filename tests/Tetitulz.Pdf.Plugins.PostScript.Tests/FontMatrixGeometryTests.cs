// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Where the pen goes after text under a font matrix that rotates or skews (Wave 17 R10.1).
///
/// <para>PLRM §5.3: <c>show</c> advances the current point by each glyph's width vector
/// <i>transformed through the font matrix</i>, so a font made with
/// <c>[0 10 -10 0 0 0] makefont</c> writes upward and the next string starts above the first,
/// not to its right. Every site that moved the pen read only the matrix's A term — <c>show</c>
/// and its <c>ashow</c>/<c>widthshow</c> family, <c>xshow</c>'s own-width fallback,
/// <c>glyphshow</c>, a Type 3 font, <c>stringwidth</c> and <c>charpath</c>'s metric box — so a
/// rotated line's strings piled up along x, and a program that positions the rest of its page by
/// <c>currentpoint</c> or <c>stringwidth</c> laid it out from the wrong place.</para>
///
/// <para>The advance is read back through the language: <c>currentpoint</c> after the show, fed
/// to <c>moveto</c>, puts the pen position into the emitted stream as the <c>m</c> operands.
/// Helvetica 'A' and 'V' are 667/1000 each, so (AV) at size 10 is 13.34 points along the
/// baseline — and the baseline of the rotated font is the y axis.</para>
/// </summary>
public sealed class FontMatrixGeometryTests
{
    private static byte[] Content(string ps) =>
        PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(ps)).Content;

    /// <summary>Helvetica at size 10, rotated a quarter turn: the baseline runs up the page.</summary>
    private const string Rotated = "/Helvetica findfont [0 10 -10 0 0 0] makefont setfont ";

    private const string Upright = "/Helvetica findfont 10 scalefont setfont ";

    /// <summary>Puts the pen position into the stream as the operands of an <c>m</c>.</summary>
    private const string ReadBackPen = " currentpoint newpath moveto 0 0 lineto stroke";

    private static double[] PenAfter(string ps)
        => ContentStreamAssert.FirstOperandsOf(Content(ps), "m");

    [Fact]
    public void Show_AdvancesAlongARotatedBaseline()
    {
        double[] pen = PenAfter(Rotated + "newpath 0 0 moveto (AV) show" + ReadBackPen);

        Assert.Equal(0, pen[0], 2);
        Assert.Equal(13.34, pen[1], 2);
    }

    /// <summary>The control: an upright font still advances along x and not at all along y.</summary>
    [Fact]
    public void Show_AdvancesAlongXForAnUprightFont()
    {
        double[] pen = PenAfter(Upright + "newpath 0 0 moveto (AV) show" + ReadBackPen);

        Assert.Equal(13.34, pen[0], 2);
        Assert.Equal(0, pen[1], 2);
    }

    /// <summary>
    /// <c>ashow</c>'s adjustment is a <b>user-space</b> displacement (PLRM: "not in the character
    /// coordinate system"), so under the rotated font it still moves the pen along x while the
    /// glyph widths move it along y.
    /// </summary>
    [Fact]
    public void AShow_AddsItsSpacingInUserSpaceWhileTheWidthsFollowTheFontMatrix()
    {
        double[] pen = PenAfter(Rotated + "newpath 0 0 moveto 2 0 (AV) ashow" + ReadBackPen);

        Assert.Equal(4, pen[0], 2);        // two characters, 2 points each, along user x
        Assert.Equal(13.34, pen[1], 2);    // the widths, along the rotated baseline
    }

    /// <summary>
    /// <c>stringwidth</c> answers the width <i>vector</i>: (0, 13.34) for the rotated font, which
    /// <c>moveto</c> then consumes as a point.
    /// </summary>
    [Fact]
    public void StringWidth_ReturnsTheWidthVectorThroughTheFontMatrix()
    {
        double[] pen = PenAfter(Rotated + "(AV) stringwidth newpath moveto 0 0 lineto stroke");

        Assert.Equal(0, pen[0], 2);
        Assert.Equal(13.34, pen[1], 2);
    }

    /// <summary>
    /// The metric box <c>charpath</c> appends when no face has outlines for the text is laid along
    /// the font's own axes: for 'A' (667) the
    /// box's text-space corners (0, −0.21), (0.667, −0.21), (0.667, 0.72), (0, 0.72) land, through
    /// [0 10 −10 0], at (2.1, 0), (2.1, 6.67), (−7.2, 6.67), (−7.2, 0).
    /// </summary>
    [Fact]
    public void CharPath_WithNoFace_LaysItsMetricBoxAlongTheFontMatrix()
    {
        byte[] content = PostScriptConversion.ConvertFragment(
            Encoding.ASCII.GetBytes(Rotated + "newpath 0 0 moveto (A) true charpath fill"),
            new PostScriptConversionOptions { OutlineFontProvider = _ => null }).Content;

        var lines = ContentStreamAssert.Parse(content).Where(o => o.Operator == "l").ToList();
        Assert.Equal(3, lines.Count);
        Assert.Equal(2.1, Number(lines[0], 0), 2);
        Assert.Equal(6.67, Number(lines[0], 1), 2);
        Assert.Equal(-7.2, Number(lines[1], 0), 2);
        Assert.Equal(6.67, Number(lines[1], 1), 2);
    }

    /// <summary>
    /// <c>xshow</c> with fewer displacements than glyphs falls back to each remaining glyph's own
    /// width — which also follows the font matrix. The first glyph is displaced 5 along user x;
    /// the second ('V', 667) advances 6.67 along the rotated baseline.
    /// </summary>
    [Fact]
    public void XShow_OwnWidthFallbackFollowsTheFontMatrix()
    {
        double[] pen = PenAfter(Rotated + "newpath 0 0 moveto (AV) [5] xshow" + ReadBackPen);

        Assert.Equal(5, pen[0], 2);
        Assert.Equal(6.67, pen[1], 2);
    }

    [Fact]
    public void GlyphShow_AdvancesAlongTheFontMatrix()
    {
        double[] pen = PenAfter(Rotated + "newpath 0 0 moveto /A glyphshow" + ReadBackPen);

        Assert.Equal(0, pen[0], 2);
        Assert.Equal(6.67, pen[1], 2);
    }

    /// <summary>
    /// A Type 3 glyph's width is in glyph space (750 under a 0.001 matrix is 0.75 text units), and
    /// a <c>makefont</c> turning the font a quarter turn sends that 0.75 × 12 = 9 up the page.
    /// </summary>
    [Fact]
    public void Type3Show_AdvancesAlongTheFontMatrix()
    {
        const string squareFont =
            "8 dict dup begin /FontType 3 def /FontMatrix [.001 0 0 .001 0 0] def " +
            "/FontBBox [0 0 1000 1000] def /Encoding 256 array def Encoding 65 /square put " +
            "/CharProcs 1 dict def " +
            "CharProcs /square { 750 0 0 0 750 750 setcachedevice 0 0 750 750 rectfill } put " +
            "/BuildGlyph { exch begin CharProcs exch get exec end } def end " +
            "/T3 exch definefont pop ";

        double[] pen = PenAfter(squareFont + "/T3 findfont [0 12 -12 0 0 0] makefont setfont " +
                                "newpath 0 0 moveto (A) show" + ReadBackPen);

        Assert.Equal(0, pen[0], 2);
        Assert.Equal(9, pen[1], 2);
    }

    /// <summary>
    /// <c>makefont</c> concatenates the font's existing matrix with the operand — PLRM: the new
    /// FontMatrix is "the original FontMatrix concatenated with matrix", so a glyph is transformed
    /// by the existing matrix first and by the new one second. Two non-commuting transforms show
    /// the order: a horizontal stretch then a skew is <c>[2 0 1 1]</c>; the other order gives
    /// <c>[2 0 2 1]</c>, which is what the operand-first composition produced.
    /// </summary>
    [Fact]
    public void MakeFont_ConcatenatesTheExistingMatrixFirst()
    {
        byte[] content = Content("/Helvetica findfont [2 0 0 1 0 0] makefont [1 0 1 1 0 0] makefont " +
                                 "setfont newpath 0 0 moveto (A) show");

        double[] tm = ContentStreamAssert.FirstOperandsOf(content, "Tm");
        Assert.Equal(2, tm[0], 6);
        Assert.Equal(0, tm[1], 6);
        Assert.Equal(1, tm[2], 6);
        Assert.Equal(1, tm[3], 6);
    }

    private static double Number(ContentOp op, int index)
        => double.Parse(op.Operands[index], System.Globalization.CultureInfo.InvariantCulture);
}
