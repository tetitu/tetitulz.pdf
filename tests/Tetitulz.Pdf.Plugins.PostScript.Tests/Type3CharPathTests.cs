// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// <c>charpath</c> on a Type 3 font appends the paths its glyph procedures paint (PLRM §5.7), read
/// back from each glyph's built content stream, through the glyph matrix and the font matrix at the
/// pen. A glyph that paints no path keeps its metric box, and that alone is reported.
/// </summary>
public sealed class Type3CharPathTests
{
    /// <summary>A Type 3 font at size 12 under <paramref name="matrix"/>; 'A' is <paramref name="glyph"/>, 750 wide.</summary>
    private static string Font(string glyph, string matrix = "[.001 0 0 .001 0 0]") =>
        $"8 dict dup begin /FontType 3 def /FontMatrix {matrix} def " +
        "/FontBBox [0 0 1000 1000] def /Encoding 256 array def Encoding 65 /g put " +
        "/CharProcs 1 dict def " +
        $"CharProcs /g {{ 750 0 setcharwidth {glyph} }} put " +
        "/BuildGlyph { exch begin CharProcs exch get exec end } def end " +
        "/T3 exch definefont pop /T3 findfont 12 scalefont setfont ";

    private static PostScriptConversionResult Convert(string ps)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps));

    /// <summary>The path a painter reads: a move another move follows, or that ends the path, paints nothing.</summary>
    private static List<ContentOp> Path(byte[] content)
    {
        var ops = ContentStreamAssert.Parse(content).Where(o => o.Operator is "m" or "l" or "c" or "h").ToList();
        return ops.Where((o, k) => o.Operator != "m" || (k + 1 < ops.Count && ops[k + 1].Operator != "m")).ToList();
    }

    private static double[] Numbers(ContentOp op)
        => op.Operands.Select(t => System.Math.Round(double.Parse(t, System.Globalization.CultureInfo.InvariantCulture), 6)).ToArray();

    /// <summary>A filled square of 750 glyph units is a 9-point square at size 12, at the pen.</summary>
    [Fact]
    public void CharPath_Type3_AppendsThePathTheGlyphFills()
    {
        var path = Path(Convert(Font("100 0 moveto 700 0 lineto 700 750 lineto closepath fill") +
                                "newpath 10 20 moveto (A) true charpath fill").Content);

        Assert.Equal(new[] { "m", "l", "l", "h" }, path.Select(o => o.Operator));
        Assert.Equal(new[] { 11.2, 20.0 }, Numbers(path[0]));
        Assert.Equal(new[] { 18.4, 29.0 }, Numbers(path[2]));
    }

    /// <summary>The second glyph starts one advance on: 750 glyph units, 9 points.</summary>
    [Fact]
    public void CharPath_Type3_AdvancesByTheGlyphsWidth()
    {
        var moves = Path(Convert(Font("0 0 750 750 rectfill") + "newpath 0 0 moveto (AA) true charpath fill").Content)
            .Where(o => o.Operator == "m").ToList();

        Assert.Equal(new[] { 0.0, 9.0 }, moves.Select(o => Numbers(o)[0]));
    }

    /// <summary>A clip the glyph takes paints nothing, so its path is not the outline; the fill after it is.</summary>
    [Fact]
    public void CharPath_Type3_LeavesOutAClipsPath()
    {
        var path = Path(Convert(Font("0 0 moveto 100 0 lineto 100 100 lineto closepath clip newpath 0 0 750 750 rectfill") +
                                "newpath 0 0 moveto (A) true charpath fill").Content);

        Assert.Single(path, o => o.Operator == "m");
        Assert.Contains(path, o => o.Operator == "l" && Numbers(o).SequenceEqual(new[] { 9.0, 9.0 }));
    }

    /// <summary>A stroked path is the outline as it is stroked, a curve kept a curve.</summary>
    [Fact]
    public void CharPath_Type3_AppendsAStrokedCurve()
    {
        var path = Path(Convert(Font("0 0 moveto 0 500 750 500 750 0 curveto stroke") +
                                "newpath 0 0 moveto (A) true charpath fill").Content);

        Assert.Equal(new[] { 0.0, 6.0, 9.0, 6.0, 9.0, 0.0 }, Numbers(path.Single(o => o.Operator == "c")));
    }

    /// <summary>
    /// The glyph matrix applies to the outline and the advance alike: at [.002 0 0 .002 0 0] the
    /// 750-unit square is 18 points and the second glyph starts 18 on.
    /// </summary>
    [Fact]
    public void CharPath_Type3_GoesThroughTheGlyphMatrix()
    {
        var path = Path(Convert(Font("0 0 750 750 rectfill", "[.002 0 0 .002 0 0]") +
                                "newpath 0 0 moveto (AA) true charpath fill").Content);

        Assert.Contains(path, o => o.Operator == "l" && Numbers(o).SequenceEqual(new[] { 18.0, 18.0 }));
        Assert.Equal(new[] { 0.0, 18.0 }, path.Where(o => o.Operator == "m").Select(o => Numbers(o)[0]));
    }

    /// <summary>
    /// A glyph that scales inside a <c>gsave</c> is written <c>q … cm … Q</c>: the scaled square reaches
    /// 200 glyph units (2.4 points), and the square after the <c>grestore</c> is back at 1:1, from 300
    /// (3.6 points) — not 600.
    /// </summary>
    [Fact]
    public void CharPath_Type3_FollowsTheGlyphsTransformAndItsRestore()
    {
        var path = Path(Convert(Font("gsave 2 2 scale 0 0 100 100 rectfill grestore 300 0 100 100 rectfill") +
                                "newpath 0 0 moveto (A) true charpath fill").Content);

        Assert.Contains(path, o => o.Operator == "l" && Numbers(o).SequenceEqual(new[] { 2.4, 2.4 }));
        Assert.Equal(new[] { 0.0, 3.6 }, path.Where(o => o.Operator == "m").Select(o => Numbers(o)[0]));
    }

    /// <summary>
    /// A code the font has no glyph for keeps a box at the pen and is reported; the glyph before it is
    /// still its outline.
    /// </summary>
    [Fact]
    public void CharPath_Type3_ACodeWithNoGlyph_KeepsABox()
    {
        var r = Convert(Font("0 0 750 750 rectfill") + "newpath 0 0 moveto (AB) true charpath fill");

        var moves = Path(r.Content).Where(o => o.Operator == "m").ToList();
        Assert.Equal(2, moves.Count);
        Assert.Equal(new[] { 0.0, 0.0 }, Numbers(moves[0]));
        Assert.Equal(9.0, Numbers(moves[1])[0], 6);
        Assert.Contains("metric box", r.LastError ?? string.Empty, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A font with <c>/CharProcs</c> alone (no <c>/BuildGlyph</c> or <c>/BuildChar</c>) builds no glyph
    /// at all for a code its procedures lack: that code keeps a box, after the outline of the one it has.
    /// </summary>
    [Fact]
    public void CharPath_Type3_ACodeTheCharProcsLack_KeepsABox()
    {
        string font =
            "8 dict dup begin /FontType 3 def /FontMatrix [.001 0 0 .001 0 0] def " +
            "/FontBBox [0 0 1000 1000] def /Encoding 256 array def Encoding 65 /g put Encoding 66 /h put " +
            "/CharProcs 1 dict def CharProcs /g { 750 0 setcharwidth 0 0 750 750 rectfill } put end " +
            "/T3 exch definefont pop /T3 findfont 12 scalefont setfont ";
        var r = Convert(font + "newpath 0 0 moveto (AB) true charpath fill");

        var moves = Path(r.Content).Where(o => o.Operator == "m").ToList();
        Assert.Equal(2, moves.Count);
        Assert.Equal(9.0, Numbers(moves[1])[0], 6);
        Assert.Contains("metric box", r.LastError ?? string.Empty, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A glyph that paints only an image keeps its metric box and is reported; the control, a glyph
    /// that fills a path, is not.
    /// </summary>
    [Fact]
    public void CharPath_Type3_AGlyphOfAnImageAlone_KeepsItsMetricBox()
    {
        var image = Convert(Font("750 750 scale 1 1 true [1 0 0 1 0 0] <80> imagemask") +
                            "newpath 0 0 moveto (A) true charpath fill");
        var filled = Convert(Font("0 0 750 750 rectfill") + "newpath 0 0 moveto (A) true charpath fill");

        Assert.Contains("metric box", image.LastError ?? string.Empty, System.StringComparison.Ordinal);
        Assert.DoesNotContain("metric box", filled.LastError ?? string.Empty, System.StringComparison.Ordinal);
    }
}
