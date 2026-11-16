// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.Core.Tests.Constants;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts.Cff;

/// <summary>
/// <see cref="CffFontSet.Outline"/> and <see cref="CffFontSet.CidOutline"/>: a glyph's charstring run
/// by the renderers' Type 2 evaluator (<see cref="Type2Charstrings"/>) into a <see cref="GlyphOutline"/>
/// in the 1000-unit glyph space a PDF font uses, through the font matrix.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public class CffFontSetOutlineTests
{
    private static byte[] N(int v)
    {
        if (v >= -107 && v <= 107) return new[] { (byte)(v + 139) };
        if (v >= 108 && v <= 1131) { int a = v - 108; return new[] { (byte)((a >> 8) + 247), (byte)a }; }
        if (v >= -1131 && v <= -108) { int a = -v - 108; return new[] { (byte)((a >> 8) + 251), (byte)a }; }
        return new byte[] { 28, (byte)(v >> 8), (byte)v };
    }

    private static byte[] Cs(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>A square of side 400 at the origin, the width operand <paramref name="width"/> (nominalWidthX is 0).</summary>
    private static byte[] Box(int width)
        => Cs(N(width), N(0), N(0), new byte[] { 21 },
              N(0), N(400), N(400), N(0), N(0), N(-400), new byte[] { 5 }, new byte[] { 14 });

    private static CffFontSet NameKeyed(double[] fontMatrix = null)
    {
        var font = new CffFontWriter.Font { NominalWidthX = 0, DefaultWidthX = 500, FontMatrix = fontMatrix };
        font.GlyphNames.Add(".notdef"); font.CharStrings.Add(new byte[] { 14 });
        font.GlyphNames.Add("A");       font.CharStrings.Add(Box(600));
        font.GlyphNames.Add("C");       font.CharStrings.Add(Box(500));
        CffFontSet set = CffFontSet.TryRead(CffFontWriter.Build(font), out string reason);
        Assert.True(set != null, reason);
        return set;
    }

    /// <summary>Each segment's kind and end point, to three decimals: the font matrix's scale is a <c>float</c>.</summary>
    private static (GlyphOutlineSegmentKind Kind, double X, double Y)[] Points(GlyphOutline outline)
        => outline.Segments.Select(s => (s.Kind, System.Math.Round(s.X3, 3), System.Math.Round(s.Y3, 3))).ToArray();

    /// <summary>
    /// The outline is the charstring's path through the font matrix into thousandths: at
    /// <c>[0.0005 0 0 0.0005 0 0]</c> the 400-unit square is 200 on a side and the 600-unit width 300.
    /// </summary>
    [Fact]
    public void AnOutline_IsTheCharstringsPath_InThousandths()
    {
        GlyphOutline outline = NameKeyed(new[] { 0.0005, 0, 0, 0.0005, 0, 0 }).Outline("A");

        Assert.NotNull(outline);
        Assert.Equal(new (GlyphOutlineSegmentKind, double, double)[]
        {
            (GlyphOutlineSegmentKind.MoveTo, 0, 0), (GlyphOutlineSegmentKind.LineTo, 0, 200),
            (GlyphOutlineSegmentKind.LineTo, 200, 200), (GlyphOutlineSegmentKind.LineTo, 200, 0),
            (GlyphOutlineSegmentKind.ClosePath, 0, 0),
        }, Points(outline));
        Assert.Equal(300, outline.Advance, 3);
    }

    /// <summary>A name the charset lacks has no outline, nor does a CID in a font that is not CID-keyed; the font's own glyph does.</summary>
    [Fact]
    public void AGlyphTheFontLacks_HasNoOutline()
    {
        CffFontSet set = NameKeyed();
        Assert.Null(set.Outline("B"));
        Assert.Null(set.Outline(null));
        Assert.Null(set.CidOutline(1));
        Assert.Null(set.CidOutline(65));
        Assert.NotNull(set.Outline("A"));
    }

    /// <summary>
    /// A CID-keyed font's glyph for a CID through its charset: CIDs 10, 20 and 30 are glyphs 1 to 3,
    /// each a 400-unit square 500 wide; CID 99 has no glyph.
    /// </summary>
    [Fact]
    public void ACidOutline_IsTheGlyphTheCharsetGivesTheCid()
    {
        CffFontSet set = CffFontSet.TryRead(MinimalOpenTypeCff.BuildCidKeyed(new[] { 10, 20, 30 }), out string reason);
        Assert.True(set != null, reason);

        GlyphOutline outline = set.CidOutline(20);
        Assert.NotNull(outline);
        Assert.Equal(500, outline.Advance, 3);
        var lines = Points(outline).Where(p => p.Kind == GlyphOutlineSegmentKind.LineTo).Select(p => (p.X, p.Y)).Take(3).ToArray();
        Assert.Equal(new (double, double)[] { (0, 400), (400, 400), (400, 0) }, lines);
        Assert.Null(set.CidOutline(99));
    }

    /// <summary>A glyph's charstring is run once: asked again, the font answers the outline it kept.</summary>
    [Fact]
    public void AGlyph_IsRunOnce()
    {
        CffFontSet set = NameKeyed();
        Assert.Same(set.Outline("A"), set.Outline("A"));
    }

    /// <summary>
    /// The runs spend the font's own charstring budget: with <see cref="PdfLimits.MaxCharstringStepsPerFont"/>
    /// below the square's steps the glyph is stopped short and has no outline; at the default it has one.
    /// </summary>
    [Fact]
    public void TheFontsBudget_StopsAGlyphShort_AndItHasNoOutline()
    {
        try
        {
            PdfLimits.MaxCharstringStepsPerFont = 5;
            Assert.Null(NameKeyed().Outline("A"));
        }
        finally { PdfLimits.ResetToDefaults(); }

        Assert.NotNull(NameKeyed().Outline("A"));
    }

    /// <summary>
    /// The font's budget is spent across its glyphs, not granted afresh to each: at 20 steps the
    /// first 12-step square runs and leaves too few for the second, which has no outline.
    /// </summary>
    [Fact]
    public void TheFontsBudget_IsSpentAcrossItsGlyphs()
    {
        try
        {
            PdfLimits.MaxCharstringStepsPerFont = 20;
            CffFontSet set = NameKeyed();
            Assert.NotNull(set.Outline("A"));
            Assert.Null(set.Outline("C"));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }
}
