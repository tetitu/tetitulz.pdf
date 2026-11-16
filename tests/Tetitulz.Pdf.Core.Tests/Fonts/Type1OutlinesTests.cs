// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Fonts.Type1;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="Type1Outlines"/>: a program's charstrings run into outlines and advances, read from a
/// real eexec-encrypted program built by <see cref="MinimalType1"/>.
/// </summary>
public sealed class Type1OutlinesTests
{
    private static byte[] Op(params object[] parts)
    {
        var bytes = new List<byte>();
        foreach (object p in parts)
        {
            if (p is int n) bytes.AddRange(MinimalType1.Num(n));
            else bytes.Add((byte)p);
        }
        return bytes.ToArray();
    }

    private const byte Hsbw = 13, RMoveTo = 21, RLineTo = 5, RRCurveTo = 8, ClosePath = 9, EndChar = 14, Escape = 12, Seac = 6;

    private static Type1Outlines Read(params (string, byte[])[] glyphs)
    {
        var (program, length1, length2) = MinimalType1.Build(glyphs);
        Type1Outlines outlines = Type1Outlines.TryRead(program, length1, length2, out string reason);
        Assert.Null(reason);
        Assert.NotNull(outlines);
        return outlines;
    }

    /// <summary>'A': 600 wide, a box from x = 100.</summary>
    private static readonly (string, byte[]) Box =
        ("A", Op(0, 600, Hsbw, 100, 0, RMoveTo, 400, 0, RLineTo, 0, 700, RLineTo, -400, 0, RLineTo, ClosePath, EndChar));

    /// <summary>'acute': its own sidebearing 10, a bar from there.</summary>
    private static readonly (string, byte[]) Acute =
        ("acute", Op(10, 300, Hsbw, 0, 0, RMoveTo, 50, 0, RLineTo, ClosePath, EndChar));

    [Fact]
    public void TryGetGlyph_ALineGlyph_IsItsPointsAndItsWidth()
    {
        Assert.True(Read(Box).TryGetGlyph("A", out GlyphOutline glyph));

        Assert.Equal(600, glyph.Advance);
        Assert.Equal(
            new[] { GlyphOutlineSegmentKind.MoveTo, GlyphOutlineSegmentKind.LineTo, GlyphOutlineSegmentKind.LineTo,
                    GlyphOutlineSegmentKind.LineTo, GlyphOutlineSegmentKind.ClosePath },
            glyph.Segments.Select(s => s.Kind));
        Assert.Equal((100.0, 0.0), (glyph.Segments[0].X3, glyph.Segments[0].Y3));
        Assert.Equal((500.0, 700.0), (glyph.Segments[2].X3, glyph.Segments[2].Y3));
    }

    [Fact]
    public void TryGetGlyph_ACurve_KeepsItsControlPoints()
    {
        var curve = ("B", Op(0, 500, Hsbw, 0, 0, RMoveTo, 100, 0, 100, 100, 0, 100, RRCurveTo, ClosePath, EndChar));

        Assert.True(Read(curve).TryGetGlyph("B", out GlyphOutline glyph));

        GlyphOutlineSegment c = glyph.Segments.Single(s => s.Kind == GlyphOutlineSegmentKind.CurveTo);
        Assert.Equal(new[] { 100.0, 0.0, 200.0, 100.0, 200.0, 200.0 }, new[] { c.X1, c.Y1, c.X2, c.Y2, c.X3, c.Y3 });
    }

    /// <summary>
    /// A subpath the charstring leaves open is closed before the next starts and at the end: every
    /// subpath of an outline font's glyph is closed (Type 1 Font Format §6.4).
    /// </summary>
    [Fact]
    public void TryGetGlyph_AnOpenSubpath_IsClosed()
    {
        var open = ("O", Op(0, 500, Hsbw, 0, 0, RMoveTo, 100, 0, RLineTo, 0, 100, RMoveTo, 100, 0, RLineTo, EndChar));

        Assert.True(Read(open).TryGetGlyph("O", out GlyphOutline glyph));

        Assert.Equal(
            new[] { GlyphOutlineSegmentKind.MoveTo, GlyphOutlineSegmentKind.LineTo, GlyphOutlineSegmentKind.ClosePath,
                    GlyphOutlineSegmentKind.MoveTo, GlyphOutlineSegmentKind.LineTo, GlyphOutlineSegmentKind.ClosePath },
            glyph.Segments.Select(s => s.Kind));
    }

    /// <summary>
    /// <c>seac</c>: the base glyph as it is, the accent moved by adx − asb + the composite's sbx —
    /// 200 − 10 + 20 = 210 across and 300 up, so the accent's bar, at its own sidebearing 10, starts at
    /// (220, 300). The composite's own width.
    /// </summary>
    [Fact]
    public void TryGetGlyph_ASeacComposite_PlacesTheAccentByItsOffsets()
    {
        // asb adx ady bchar achar: 'A' is 65 in StandardEncoding, 'acute' 0xC2.
        var composite = ("Aacute", Op(20, 650, Hsbw, 10, 200, 300, 65, 0xC2, Escape, Seac));

        Assert.True(Read(Box, Acute, composite).TryGetGlyph("Aacute", out GlyphOutline glyph));

        Assert.Equal(650, glyph.Advance);
        Assert.Equal(8, glyph.Segments.Count);
        Assert.Equal((100.0, 0.0), (glyph.Segments[0].X3, glyph.Segments[0].Y3));
        Assert.Equal(GlyphOutlineSegmentKind.MoveTo, glyph.Segments[5].Kind);
        Assert.Equal((220.0, 300.0), (glyph.Segments[5].X3, glyph.Segments[5].Y3));
        Assert.Equal((270.0, 300.0), (glyph.Segments[6].X3, glyph.Segments[6].Y3));
    }

    /// <summary>A composite whose accent the program lacks is no glyph — not the base alone.</summary>
    [Fact]
    public void TryGetGlyph_ASeacWithAMissingAccent_IsRefused()
    {
        var composite = ("Aacute", Op(20, 650, Hsbw, 10, 200, 300, 65, 0xC2, Escape, Seac));

        Assert.False(Read(Box, composite).TryGetGlyph("Aacute", out GlyphOutline glyph));
        Assert.Null(glyph);
    }

    /// <summary>
    /// <c>seac</c>'s base and accent are themselves plain glyphs (Type 1 Font Format §6.4): a
    /// composite whose base is a composite is refused, not followed — the control is the same base
    /// one level up, which is drawn.
    /// </summary>
    [Fact]
    public void TryGetGlyph_ASeacWhoseBaseIsASeac_IsRefused()
    {
        // 'A' at 65 is here a composite of 'B' (66) and 'acute'; 'Aacute' is a composite of 'A'.
        var plainB = ("B", Op(0, 600, Hsbw, 0, 0, RMoveTo, 10, 0, RLineTo, ClosePath, EndChar));
        var compositeA = ("A", Op(0, 600, Hsbw, 10, 0, 0, 66, 0xC2, Escape, Seac));
        var outer = ("Aacute", Op(0, 650, Hsbw, 10, 200, 300, 65, 0xC2, Escape, Seac));
        Type1Outlines outlines = Read(plainB, Acute, compositeA, outer);

        Assert.True(outlines.TryGetGlyph("A", out _));
        Assert.False(outlines.TryGetGlyph("Aacute", out _));
    }

    /// <summary>A charstring that never sets its width (no <c>hsbw</c>) is no glyph; the control has one.</summary>
    [Fact]
    public void TryGetGlyph_AGlyphWithNoWidth_IsRefused()
    {
        var noWidth = ("N", Op(0, 0, RMoveTo, 10, 0, RLineTo, ClosePath, EndChar));

        Type1Outlines outlines = Read(Box, noWidth);

        Assert.False(outlines.TryGetGlyph("N", out _));
        Assert.True(outlines.TryGetGlyph("A", out _));
    }

    [Fact]
    public void TryGetGlyph_AGlyphTheProgramLacks_IsFalse_AndAPresentOneTrue()
    {
        Type1Outlines outlines = Read(Box);

        Assert.False(outlines.TryGetGlyph("Z", out _));
        Assert.False(outlines.TryGetGlyph(null, out _));
        Assert.True(outlines.TryGetGlyph("A", out _));
    }

    /// <summary>A glyph is run once: the second ask answers the same outline.</summary>
    [Fact]
    public void TryGetGlyph_AskedTwice_AnswersTheKeptOutline()
    {
        Type1Outlines outlines = Read(Box);

        Assert.True(outlines.TryGetGlyph("A", out GlyphOutline first));
        Assert.True(outlines.TryGetGlyph("A", out GlyphOutline second));
        Assert.Same(first, second);
    }

    [Fact]
    public void TryRead_NotAProgram_IsRefusedWithAReason()
    {
        byte[] junk = Encoding.ASCII.GetBytes("%!PS-AdobeFont-1.0: X\nnot a font at all\n");

        Assert.Null(Type1Outlines.TryRead(junk, junk.Length, 0, out string reason));
        Assert.NotNull(reason);
    }
}
