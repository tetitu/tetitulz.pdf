// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Fonts.Type1;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts.Type1;

/// <summary>
/// <see cref="Type1CharstringFontBuilder.Add(byte[], IReadOnlyList{byte[]}, double)"/>: a glyph from a
/// glyph space of another size, written into the font's — each point and the advance scaled and
/// rounded to the font's units, the outline it answers the one the CFF draws.
/// </summary>
public class Type1CharstringFontBuilderTests
{
    private const byte Hsbw = 13, RMoveTo = 21, RLineTo = 5, ClosePath = 9, EndChar = 14;

    private static byte[] N(int v)
    {
        if (v >= -107 && v <= 107) return new[] { (byte)(v + 139) };
        if (v >= 108 && v <= 1131) { int a = v - 108; return new[] { (byte)((a >> 8) + 247), (byte)a }; }
        if (v >= -1131 && v <= -108) { int a = -v - 108; return new[] { (byte)((a >> 8) + 251), (byte)a }; }
        return new byte[] { 255, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    }

    private static byte[] Cs(params object[] parts)
        => parts.SelectMany(p => p is int n ? N(n) : new[] { (byte)p }).ToArray();

    /// <summary>A run of unit steps: a move to (1, 1), three lines one unit right, 333 wide.</summary>
    private static readonly byte[] Steps = Cs(0, 333, Hsbw, 1, 1, RMoveTo, 1, 0, RLineTo, 1, 0, RLineTo, 1, 0, RLineTo, ClosePath, EndChar);

    private static double[] EndXs(GlyphOutline outline)
        => outline.Segments.Where(s => s.Kind is GlyphOutlineSegmentKind.MoveTo or GlyphOutlineSegmentKind.LineTo).Select(s => Math.Round(s.X3, 3)).ToArray();

    private static CffFontSet Built(Type1CharstringFontBuilder builder)
    {
        var set = CffFontSet.TryRead(builder.Build(out string reason), out string readReason);
        Assert.True(set != null, reason ?? readReason);
        return set;
    }

    /// <summary>
    /// At 1.5 each point is scaled and rounded where it lies — 1.5, 3, 4.5, 6 to 2, 3, 5, 6, the advance
    /// 499.5 to 500 — and the CFF draws the same points: rounding each step instead would carry 0.5
    /// into every later point and end at 8.
    /// </summary>
    [Fact]
    public void AScaledGlyph_IsRoundedPointByPoint_AndTheCffDrawsIt()
    {
        var builder = new Type1CharstringFontBuilder("Scaled");
        builder.Add(Cs(0, 250, Hsbw, EndChar), Array.Empty<byte[]>());
        GlyphOutline added = builder.Add(Steps, Array.Empty<byte[]>(), 1.5);

        Assert.Equal(new[] { 2.0, 3, 5, 6 }, EndXs(added));
        Assert.Equal(500.0, added.Advance);

        CffFontSet cff = Built(builder);
        Assert.Equal(new[] { 2.0, 3, 5, 6 }, EndXs(cff.Outline("g1")));
        Assert.True(cff.TryGetAdvance("g1", out double advance));
        Assert.Equal(500.0, advance, 2);
    }

    /// <summary>At 1 the glyph is as the charstring drew it, through the same overload.</summary>
    [Fact]
    public void AnUnscaledGlyph_IsAsDrawn()
    {
        var builder = new Type1CharstringFontBuilder("Unscaled");
        Assert.Equal(new[] { 1.0, 2, 3, 4 }, EndXs(builder.Add(Steps, Array.Empty<byte[]>(), 1)));
        Assert.Equal(new[] { 1.0, 2, 3, 4 }, EndXs(builder.Add(Steps, Array.Empty<byte[]>())));
    }

    /// <summary>
    /// A point scaled past <see cref="Type1CharstringFontBuilder.MaxCoordinate"/> refuses the glyph —
    /// an empty one added in its place — where the same charstring unscaled is kept.
    /// </summary>
    [Fact]
    public void APointScaledPastTheOperandRange_RefusesTheGlyph()
    {
        byte[] far = Cs(0, 500, Hsbw, 9000, 0, RMoveTo, 10, 0, RLineTo, ClosePath, EndChar);
        var builder = new Type1CharstringFontBuilder("Far");
        Assert.NotNull(builder.Add(far, Array.Empty<byte[]>(), 1));
        Assert.Null(builder.Add(far, Array.Empty<byte[]>(), 2));
        Assert.Equal(2, builder.Count);

        CffFontSet cff = Built(builder);
        Assert.Equal(new[] { 9000.0, 9010 }, EndXs(cff.Outline(".notdef")));   // glyph 0, the kept one
        Assert.Empty(cff.Outline("g1").Segments);                               // glyph 1, the refused one
    }

    /// <summary>
    /// A curve's control point scaled past the range refuses the glyph, though the curve ends where it
    /// began: its second point is 9000 out.
    /// </summary>
    [Fact]
    public void ACurvesControlPointScaledPastTheOperandRange_RefusesTheGlyph()
    {
        const byte RRCurveTo = 8;
        byte[] reach = Cs(0, 500, Hsbw, 0, 0, RMoveTo, 0, 0, 9000, 10, -9000, 10, RRCurveTo, ClosePath, EndChar);
        var builder = new Type1CharstringFontBuilder("Reach");
        Assert.NotNull(builder.Add(reach, Array.Empty<byte[]>(), 1));
        Assert.Null(builder.Add(reach, Array.Empty<byte[]>(), 2));
    }

    /// <summary>
    /// A curve is scaled at every point, its end included: at 2 the curve from (10, 0) through (30, 10)
    /// and (50, 10) to (60, 0) runs from (20, 0) through (60, 20) and (100, 20) to (120, 0), in the
    /// outline answered and in the CFF.
    /// </summary>
    [Fact]
    public void AScaledCurve_IsScaledAtEveryPoint()
    {
        const byte RRCurveTo = 8;
        byte[] arc = Cs(0, 500, Hsbw, 10, 0, RMoveTo, 20, 10, 20, 0, 10, -10, RRCurveTo, ClosePath, EndChar);
        var builder = new Type1CharstringFontBuilder("Arc");
        GlyphOutline added = builder.Add(arc, Array.Empty<byte[]>(), 2);
        CffFontSet cff = Built(builder);

        foreach (GlyphOutline outline in new[] { added, cff.Outline(".notdef") })
        {
            GlyphOutlineSegment curve = outline.Segments.Single(s => s.Kind == GlyphOutlineSegmentKind.CurveTo);
            Assert.Equal(new[] { 60.0, 20, 100, 20, 120, 0 },
                         new[] { curve.X1, curve.Y1, curve.X2, curve.Y2, curve.X3, curve.Y3 }.Select(v => Math.Round(v, 3)));
        }
    }

    /// <summary>An advance scaled past the range refuses the glyph too.</summary>
    [Fact]
    public void AnAdvanceScaledPastTheOperandRange_RefusesTheGlyph()
    {
        byte[] wide = Cs(0, 9000, Hsbw, EndChar);
        var builder = new Type1CharstringFontBuilder("Wide");
        Assert.NotNull(builder.Add(wide, Array.Empty<byte[]>(), 1));
        Assert.Null(builder.Add(wide, Array.Empty<byte[]>(), 2));
    }

    /// <summary>A scale that is not finite and positive is the caller's error.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AScaleThatIsNotFiniteAndPositive_IsRefused(double scale)
    {
        var builder = new Type1CharstringFontBuilder("Bad");
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Add(Steps, Array.Empty<byte[]>(), scale));
        Assert.Equal(0, builder.Count);
    }
}
