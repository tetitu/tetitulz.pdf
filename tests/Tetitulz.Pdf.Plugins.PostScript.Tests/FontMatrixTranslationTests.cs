// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// A font matrix's translation (PLRM §5.3): a glyph's point goes through the whole font matrix and is
/// then put at the current point, so <c>[10 0 0 10 5 7] makefont</c> draws its text 5 across and 7 up
/// from the pen. Every text operator wrote the matrix's linear part and the pen alone as the
/// <c>Tm</c>, and the translation was lost. The advance is a difference and takes none.
/// </summary>
public sealed class FontMatrixTranslationTests
{
    private const string Translated = "/Helvetica findfont [10 0 0 10 5 7] makefont setfont ";

    private static PostScriptConversionResult Convert(string ps, System.Func<string, byte[]> provider = null)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps),
            new PostScriptConversionOptions { OutlineFontProvider = provider ?? (_ => null) });

    private static double[] Numbers(ContentOp op)
        => op.Operands.Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private static double[] Last(byte[] content, string op) => Numbers(ContentStreamAssert.Parse(content).Last(o => o.Operator == op));

    [Theory]
    [InlineData("0 0 moveto (A) show")]
    [InlineData("0 0 moveto (A) [6] xshow")]
    [InlineData("0 0 moveto /A glyphshow")]
    public void Show_AMakefontTranslation_MovesTheText(string show)
    {
        double[] tm = Last(Convert(Translated + show).Content, "Tm");

        Assert.Equal(new[] { 10.0, 0, 0, 10, 5, 7 }, tm.Select(v => System.Math.Round(v, 6)));
    }

    /// <summary>The control: an untranslated matrix puts the text at the pen.</summary>
    [Fact]
    public void Show_AnUntranslatedMatrix_IsAtThePen()
    {
        double[] tm = Last(Convert("/Helvetica findfont 10 scalefont setfont 3 4 moveto (A) show").Content, "Tm");

        Assert.Equal(3.0, tm[4], 6);
        Assert.Equal(4.0, tm[5], 6);
    }

    /// <summary>The pen moves by the width vector, which takes no translation: 'A' is 6.67 at size 10.</summary>
    [Fact]
    public void Show_AMakefontTranslation_DoesNotMoveThePen()
    {
        double[] m = Last(Convert(Translated + "0 0 moveto (A) show currentpoint newpath moveto 0 0 lineto stroke").Content, "m");

        Assert.Equal(6.67, m[0], 6);
        Assert.Equal(0.0, m[1], 6);
    }

    /// <summary>The metric box <c>charpath</c> appends is translated with its text: its first corner at (5, 7 − 2.1).</summary>
    [Fact]
    public void CharPath_AMakefontTranslation_MovesTheMetricBox()
    {
        var ops = ContentStreamAssert.Parse(Convert(Translated + "newpath 0 0 moveto (A) true charpath fill").Content)
                                     .Where(o => o.Operator is "m" or "l").ToList();
        double[] m = Numbers(ops.Where((o, k) => o.Operator == "m" && k + 1 < ops.Count && ops[k + 1].Operator == "l").First());

        Assert.Equal(5.0, m[0], 6);
        Assert.Equal(7 - 2.1, m[1], 6);
    }

    /// <summary>And a face's outline: the box glyph's first point, x = 100 at size 10, is at (1 + 5, 7).</summary>
    [Fact]
    public void CharPath_AMakefontTranslation_MovesTheOutline()
    {
        byte[] face = OutlineTrueType.Build(new[]
        {
            OutlineTrueType.Simple(new[]
            {
                new OutlineTrueType.Point(100, 0), new OutlineTrueType.Point(500, 0),
                new OutlineTrueType.Point(500, 700), new OutlineTrueType.Point(100, 700),
            }),
        });
        var ops = ContentStreamAssert.Parse(Convert(Translated + "newpath 0 0 moveto (A) true charpath fill", _ => face).Content)
                                     .Where(o => o.Operator is "m" or "l").ToList();
        var start = ops.Where((o, k) => o.Operator == "m" && k + 1 < ops.Count && ops[k + 1].Operator == "l").First();

        Assert.Equal(new[] { 6.0, 7.0 }, Numbers(start).Select(v => System.Math.Round(v, 6)));
    }

    /// <summary>
    /// A curve's control points are points too: the quadratic hump raised to its cubic, (100, 200),
    /// (200, 200), (300, 0) in thousandths, lands at (6, 9), (7, 9), (8, 7) at size 10 translated by (5, 7).
    /// </summary>
    [Fact]
    public void CharPath_AMakefontTranslation_MovesACurvesControlPoints()
    {
        byte[] face = OutlineTrueType.Build(new[]
        {
            OutlineTrueType.Simple(new[]
            {
                new OutlineTrueType.Point(0, 0), new OutlineTrueType.Point(150, 300, false), new OutlineTrueType.Point(300, 0),
            }),
        });
        var curve = ContentStreamAssert.Parse(Convert(Translated + "newpath 0 0 moveto (A) true charpath fill", _ => face).Content)
                                       .Single(o => o.Operator == "c");

        Assert.Equal(new[] { 6.0, 9, 7, 9, 8, 7 }, Numbers(curve).Select(v => System.Math.Round(v, 6)));
    }

    /// <summary>
    /// A Type 1 program's own matrix with a translation: 0.01 of an em at size 100 is one point. Taking
    /// the program's matrix into thousandths after it rather than before would make it 1000.
    /// </summary>
    [Fact]
    public void Show_AProgramsMatrixTranslation_StaysInTextSpace()
    {
        var (program, _, _) = MinimalType1.Build(
            new List<(string, byte[])> { (".notdef", MinimalType1.AdvanceOnly(250)), ("A", MinimalType1.AdvanceOnly(600)) },
            new Dictionary<int, string> { [65] = "A" },
            fontMatrix: "[0.001 0 0 0.001 0.01 0]");
        var all = new MemoryStream();
        void Text(string s) { byte[] b = Encoding.Latin1.GetBytes(s); all.Write(b, 0, b.Length); }
        Text("12 dict begin\n");
        all.Write(program, 0, program.Length);
        Text("\n" + new string('0', 512) + "\ncleartomark\n");
        Text("/ProbeFont exch definefont pop /ProbeFont findfont 100 scalefont setfont 0 0 moveto (A) show");

        double[] tm = Last(PostScriptConversion.ConvertFragment(all.ToArray()).Content, "Tm");

        Assert.Equal(100.0, tm[0], 6);
        Assert.Equal(1.0, tm[4], 6);
    }
}
