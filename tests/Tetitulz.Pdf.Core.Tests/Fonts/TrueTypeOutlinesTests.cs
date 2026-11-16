// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Tests.Constants;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="TrueTypeOutlines"/>: a program's <c>glyf</c> contours as segments in 1000-unit glyph
/// space. The programs are <see cref="MinimalTrueType"/> fonts with a hand-built <c>glyf</c> and
/// <c>loca</c> (<see cref="OutlineTrueType"/>), so each glyph's points are exactly the test's.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public sealed class TrueTypeOutlinesTests
{
    private static TrueTypeOutlines Read(IReadOnlyList<byte[]> glyphs, int unitsPerEm = 1000, bool longLoca = false)
        => TrueTypeOutlines.TryRead(Program(glyphs, unitsPerEm, longLoca), out _);

    private static TrueTypeFontProgram Program(IReadOnlyList<byte[]> glyphs, int unitsPerEm = 1000, bool longLoca = false)
        => TrueTypeFontProgram.Parse(OutlineTrueType.Build(glyphs, unitsPerEm, longLoca));

    private static byte[] Simple(params OutlineTrueType.Point[][] contours) => OutlineTrueType.Simple(contours);

    private static byte[] Composite(params byte[][] records) => OutlineTrueType.Composite(records);

    private static byte[] Component(int glyph, int arg1, int arg2, int extraFlags = 0, bool pointMatching = false, params double[] transform)
        => OutlineTrueType.Component(glyph, arg1, arg2, extraFlags, pointMatching, transform);

    private static OutlineTrueType.Point P(int x, int y, bool on = true) => new(x, y, on);

    private static readonly byte[] Box = Simple(new[] { P(100, 0), P(500, 0), P(500, 700), P(100, 700) });

    private static GlyphOutline Glyph(TrueTypeOutlines outlines, int id)
    {
        Assert.True(outlines.TryGetGlyph(id, out GlyphOutline glyph));
        return glyph;
    }

    private static GlyphOutlineSegmentKind[] Kinds(GlyphOutline g) => g.Segments.Select(s => s.Kind).ToArray();

    private static (double, double) End(GlyphOutlineSegment s) => (System.Math.Round(s.X3, 6), System.Math.Round(s.Y3, 6));

    private const GlyphOutlineSegmentKind M = GlyphOutlineSegmentKind.MoveTo, L = GlyphOutlineSegmentKind.LineTo,
                                          C = GlyphOutlineSegmentKind.CurveTo, H = GlyphOutlineSegmentKind.ClosePath;

    // ==================================================================
    //  Simple glyphs
    // ==================================================================

    /// <summary>Four on-curve points: a move, three lines, and the close that draws the fourth side.</summary>
    [Fact]
    public void TryGetGlyph_ABox_IsAMoveThreeLinesAndAClose()
    {
        GlyphOutline g = Glyph(Read(new[] { Box }), 1);

        Assert.Equal(new[] { M, L, L, L, H }, Kinds(g));
        Assert.Equal((100.0, 0.0), End(g.Segments[0]));
        Assert.Equal((500.0, 700.0), End(g.Segments[2]));
        Assert.Equal(600, g.Advance);
    }

    /// <summary>
    /// A quadratic raised to the cubic with the same curve: the controls two thirds of the way from
    /// each end to the quadratic's control point.
    /// </summary>
    [Fact]
    public void TryGetGlyph_AQuadratic_IsTheEquivalentCubic()
    {
        GlyphOutline g = Glyph(Read(new[] { Simple(new[] { P(0, 0), P(150, 300, false), P(300, 0) }) }), 1);

        Assert.Equal(new[] { M, C, H }, Kinds(g));
        GlyphOutlineSegment c = g.Segments[1];
        Assert.Equal(new[] { 100.0, 200.0, 200.0, 200.0, 300.0, 0.0 },
                     new[] { c.X1, c.Y1, c.X2, c.Y2, c.X3, c.Y3 }.Select(v => System.Math.Round(v, 6)));
    }

    /// <summary>Two off-curve points in a row imply the on-curve point midway between them.</summary>
    [Fact]
    public void TryGetGlyph_TwoOffCurvePoints_ImplyTheirMidpoint()
    {
        GlyphOutline g = Glyph(Read(new[] { Simple(new[]
        {
            P(0, 0), P(0, 100, false), P(100, 100, false), P(100, 0),
        }) }), 1);

        Assert.Equal(new[] { M, C, C, H }, Kinds(g));
        Assert.Equal((50.0, 100.0), End(g.Segments[1]));
        Assert.Equal((100.0, 0.0), End(g.Segments[2]));
    }

    /// <summary>A contour with no on-curve point starts at the midpoint of its last and first points.</summary>
    [Fact]
    public void TryGetGlyph_AContourOfOffCurvePoints_StartsBetweenItsLastAndFirst()
    {
        GlyphOutline g = Glyph(Read(new[] { Simple(new[]
        {
            P(0, 100, false), P(100, 100, false), P(100, 0, false), P(0, 0, false),
        }) }), 1);

        Assert.Equal((0.0, 50.0), End(g.Segments[0]));
        Assert.Equal(new[] { M, C, C, C, C, H }, Kinds(g));
    }

    /// <summary>Each contour is its own closed subpath.</summary>
    [Fact]
    public void TryGetGlyph_TwoContours_AreTwoClosedSubpaths()
    {
        GlyphOutline g = Glyph(Read(new[] { Simple(
            new[] { P(0, 0), P(10, 0), P(10, 10) },
            new[] { P(20, 0), P(30, 0), P(30, 10) }) }), 1);

        Assert.Equal(new[] { M, L, L, H, M, L, L, H }, Kinds(g));
        Assert.Equal((20.0, 0.0), End(g.Segments[4]));
    }

    /// <summary>Design units are taken to 1000 per em: at 2000 units every coordinate and the advance halve.</summary>
    [Fact]
    public void TryGetGlyph_At2000UnitsPerEm_IsInThousandths()
    {
        GlyphOutline g = Glyph(Read(new[] { Box }, unitsPerEm: 2000), 1);

        Assert.Equal((250.0, 350.0), End(g.Segments[2]));
        Assert.Equal(300, g.Advance);
    }

    /// <summary>Long <c>loca</c> offsets locate the same glyphs.</summary>
    [Fact]
    public void TryGetGlyph_LongLoca_LocatesTheGlyphs()
    {
        GlyphOutline g = Glyph(Read(new[] { Box, Box }, longLoca: true), 2);

        Assert.Equal((500.0, 700.0), End(g.Segments[2]));
    }

    /// <summary>An empty glyph — a space — is an outline with no segments, and is not refused.</summary>
    [Fact]
    public void TryGetGlyph_AnEmptyGlyph_HasNoSegments()
    {
        GlyphOutline g = Glyph(Read(new[] { Box }), 0);

        Assert.Empty(g.Segments);
    }

    [Fact]
    public void TryGetGlyph_AnIdPastTheProgram_IsFalse_AndOneWithinTrue()
    {
        TrueTypeOutlines outlines = Read(new[] { Box });

        Assert.False(outlines.TryGetGlyph(2, out _));
        Assert.False(outlines.TryGetGlyph(-1, out _));
        Assert.True(outlines.TryGetGlyph(1, out _));
    }

    /// <summary>A glyph is read once: the second ask answers the same outline.</summary>
    [Fact]
    public void TryGetGlyph_AskedTwice_AnswersTheKeptOutline()
    {
        TrueTypeOutlines outlines = Read(new[] { Box });

        Assert.Same(Glyph(outlines, 1), Glyph(outlines, 1));
    }

    /// <summary>
    /// Coordinates as fonts write them — a byte delta with its sign in the flag, "same" with no bytes,
    /// runs of equal flags repeated, instructions before the flags — read as the same points as word
    /// deltas: a shape that goes right, up, left by bytes, holds an axis still, and goes down by a word.
    /// </summary>
    [Fact]
    public void TryGetGlyph_CompactCoordinatesAndInstructions_ReadAsTheSamePoints()
    {
        OutlineTrueType.Point[] shape = { P(10, 0), P(60, 0), P(60, 0, false), P(110, 0), P(110, 40), P(20, 40), P(20, 340), P(10, 0) };
        byte[] compact = OutlineTrueType.Simple(new[] { shape }, new byte[] { 0xB0, 0x01, 0x2C }, compact: true);
        byte[] words = OutlineTrueType.Simple(shape);

        GlyphOutline a = Glyph(Read(new[] { compact }), 1), b = Glyph(Read(new[] { words }), 1);

        Assert.True(compact.Length < words.Length);
        Assert.Equal(b.Segments.Select(End), a.Segments.Select(End));
        Assert.Equal(Kinds(b), Kinds(a));
        Assert.Equal((20.0, 340.0), End(a.Segments.Single(s => s.Kind == L && End(s) == (20.0, 340.0))));
    }

    /// <summary>A flag's repeat count that runs past the glyph's points refuses the glyph, not the buffer.</summary>
    [Fact]
    public void TryGetGlyph_ARepeatPastThePoints_IsRefused()
    {
        OutlineTrueType.Point[] shape = { P(0, 0), P(10, 0), P(10, 10) };
        byte[] glyph = OutlineTrueType.Simple(new[] { shape }, null, compact: true);
        int flagsAt = 10 + 2 + 2;                                   // header, one end point, no instructions
        byte[] hostile = glyph.Take(flagsAt + 1).Concat(new byte[] { 200 }).Concat(glyph.Skip(flagsAt + 1)).ToArray();
        hostile[flagsAt] |= 0x08;                                   // the first flag "repeats" 200 times

        Assert.False(Read(new[] { hostile }).TryGetGlyph(1, out _));
        Assert.True(Read(new[] { glyph }).TryGetGlyph(1, out _));
    }

    // ==================================================================
    //  Composites
    // ==================================================================

    /// <summary>A component record the glyph cuts short after its index refuses the composite.</summary>
    [Fact]
    public void TryGetGlyph_AComponentCutShort_IsRefused()
    {
        byte[] whole = Composite(Component(1, 10, 0));
        byte[] cut = whole.Take(whole.Length - 4).ToArray();          // the two argument words gone

        Assert.False(Read(new[] { Box, cut }).TryGetGlyph(2, out _));
        Assert.True(Read(new[] { Box, whole }).TryGetGlyph(2, out _));
    }

    /// <summary>A component placed by its offsets.</summary>
    [Fact]
    public void TryGetGlyph_AComponentWithOffsets_IsMovedByThem()
    {
        GlyphOutline g = Glyph(Read(new[] { Box, Composite(Component(1, 1000, 50)) }), 2);

        Assert.Equal(new[] { M, L, L, L, H }, Kinds(g));
        Assert.Equal((1100.0, 50.0), End(g.Segments[0]));
    }

    /// <summary>A component's scale applies to its points, and its offsets stay unscaled.</summary>
    [Fact]
    public void TryGetGlyph_AScaledComponent_ScalesItsPointsNotItsOffset()
    {
        GlyphOutline g = Glyph(Read(new[] { Box, Composite(Component(1, 10, 0, transform: 0.5)) }), 2);

        Assert.Equal((60.0, 0.0), End(g.Segments[0]));
        Assert.Equal((260.0, 350.0), End(g.Segments[2]));
    }

    /// <summary>Under <c>SCALED_COMPONENT_OFFSET</c> the offset goes through the transform too.</summary>
    [Fact]
    public void TryGetGlyph_ScaledComponentOffset_ScalesTheOffset()
    {
        GlyphOutline g = Glyph(Read(new[] { Box, Composite(Component(1, 10, 0, extraFlags: 0x0800, transform: 0.5)) }), 2);

        Assert.Equal((55.0, 0.0), End(g.Segments[0]));
    }

    /// <summary>The 2 × 2: x' = a·x + c·y, y' = b·x + d·y — here a quarter turn, (x, y) → (−y, x).</summary>
    [Fact]
    public void TryGetGlyph_ATwoByTwo_IsAppliedAsWritten()
    {
        GlyphOutline g = Glyph(Read(new[] { Box, Composite(Component(1, 0, 0, transform: new[] { 0.0, 1.0, -1.0, 0.0 })) }), 2);

        Assert.Equal((0.0, 100.0), End(g.Segments[0]));
        Assert.Equal((-700.0, 500.0), End(g.Segments[2]));
    }

    /// <summary>
    /// Point matching: the second copy's point 0 is put on the composite's point 2 — the first copy's
    /// (500, 700) — so the second box starts there.
    /// </summary>
    [Fact]
    public void TryGetGlyph_PointMatching_PutsTheComponentsPointOnTheComposites()
    {
        GlyphOutline g = Glyph(Read(new[] { Box, Composite(Component(1, 0, 0), Component(1, 2, 0, pointMatching: true)) }), 2);

        Assert.Equal((500.0, 700.0), End(g.Segments[5]));
        Assert.Equal((900.0, 700.0), End(g.Segments[6]));
    }

    /// <summary>
    /// A point-matched component finds the composite's point by a binary search over the contours
    /// placed so far. The search ran from the first contour each time: twenty thousand one-contour
    /// components, each matched to the last point the one before placed, were two hundred million
    /// contour visits. Each 10-unit segment starts where the one before ended.
    /// </summary>
    [Fact]
    public void AdversarialChainOfPointMatchedComponents_IsSearchedNotScanned()
    {
        const int components = 20_000;
        byte[] segment = Simple(new[] { P(0, 0), P(10, 0) });
        var records = new List<byte[]> { Component(1, 0, 0) };
        for (int k = 1; k < components; k++) records.Add(Component(1, 2 * k - 1, 0, pointMatching: true));
        TrueTypeOutlines outlines = Read(new[] { segment, Composite(records.ToArray()) }, longLoca: true);

        TrueTypeOutlines.MatchedPointSearchSteps = 0;
        Assert.True(outlines.TryGetGlyph(2, out GlyphOutline g));

        Assert.InRange(TrueTypeOutlines.MatchedPointSearchSteps, components, 16L * components);   // log2(20 000) < 15
        Assert.Equal(10.0 * components, g.Segments.Max(s => End(s).Item1));
    }

    /// <summary>
    /// A matched point the composite does not have refuses the glyph — one just past its last point (4,
    /// of points 0 to 3) as well as one far past it; the last point itself (3) is matched.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(9)]
    public void TryGetGlyph_PointMatchingPastThePoints_IsRefused(int point)
    {
        TrueTypeOutlines outlines = Read(new[] { Box, Composite(Component(1, 0, 0), Component(1, point, 0, pointMatching: true)) });
        TrueTypeOutlines last = Read(new[] { Box, Composite(Component(1, 0, 0), Component(1, 3, 0, pointMatching: true)) });

        Assert.False(outlines.TryGetGlyph(2, out _));
        Assert.True(last.TryGetGlyph(2, out _));
    }

    // ==================================================================
    //  Hostile programs
    // ==================================================================

    /// <summary>A contour's end point that does not ascend refuses the glyph rather than reading a negative contour.</summary>
    [Fact]
    public void TryGetGlyph_EndPointsThatDoNotAscend_AreRefused()
    {
        byte[] bad = Simple(new[] { P(0, 0), P(10, 0) }, new[] { P(20, 0), P(30, 0) });
        bad[12] = 0; bad[13] = 0;                                   // the second end point: 3 → 0

        TrueTypeOutlines outlines = Read(new[] { bad, Box });

        Assert.False(outlines.TryGetGlyph(1, out _));
        Assert.True(outlines.TryGetGlyph(2, out _));
    }

    /// <summary>A glyph whose data runs past its end is refused.</summary>
    [Fact]
    public void TryGetGlyph_TruncatedCoordinates_AreRefused()
    {
        byte[] whole = Simple(new[] { P(0, 0), P(10, 0), P(10, 10) });
        byte[] cut = whole.Take(whole.Length - 8).ToArray();      // past the table's 4-byte padding

        Assert.False(Read(new[] { cut }).TryGetGlyph(1, out _));
    }

    /// <summary>
    /// A composite that names itself recurses until the depth cap and is refused — the stack is not
    /// what stops it.
    /// </summary>
    [Fact]
    public void TryGetGlyph_ACompositeNamingItself_StopsAtTheDepthCap()
    {
        TrueTypeOutlines outlines = Read(new[] { Composite(Component(1, 0, 0)) });

        Assert.False(outlines.TryGetGlyph(1, out _));
    }

    /// <summary>
    /// Composites multiply: two levels each naming the four-point box ten times expand to 400 points.
    /// Under a cap of 399 the glyph is refused; at 400 it is read — the cap, not the shape, decides.
    /// </summary>
    [Fact]
    public void TryGetGlyph_ACompositeExpandingPastThePointCap_IsRefused()
    {
        byte[][] ten(int glyph) => Enumerable.Range(0, 10).Select(_ => Component(glyph, 0, 0)).ToArray();
        byte[][] glyphs = { Box, Composite(ten(1)), Composite(ten(2)) };
        try
        {
            PdfLimits.MaxGlyphOutlinePoints = 399;
            Assert.False(Read(glyphs).TryGetGlyph(3, out _));
            PdfLimits.MaxGlyphOutlinePoints = 400;
            Assert.Equal(100 * 5, Glyph(Read(glyphs), 3).Segments.Count);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>
    /// Composites multiply when their leaves have no points too: two levels each naming an empty glyph
    /// ten times are 111 glyph visits and no point. The visits are bounded by the same setting —
    /// refused under 110, read at 111 — or a few kilobytes of records nested to the depth cap ran for
    /// hours (twenty thousand records a level, three levels, 8 × 10¹² visits).
    /// </summary>
    [Fact]
    public void TryGetGlyph_ACompositeOfEmptyGlyphs_IsBoundedByItsVisits()
    {
        byte[][] ten(int glyph) => Enumerable.Range(0, 10).Select(_ => Component(glyph, 0, 0)).ToArray();
        byte[][] glyphs = { System.Array.Empty<byte>(), Composite(ten(1)), Composite(ten(2)) };
        try
        {
            PdfLimits.MaxGlyphOutlinePoints = 110;
            Assert.False(Read(glyphs).TryGetGlyph(3, out _));
            PdfLimits.MaxGlyphOutlinePoints = 111;
            Assert.Empty(Glyph(Read(glyphs), 3).Segments);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>The depth cap is the setting's: a chain three composites deep is read at 3 and refused at 2.</summary>
    [Fact]
    public void TryGetGlyph_ACompositeChain_IsBoundedByTheDepthSetting()
    {
        byte[][] glyphs = { Box, Composite(Component(1, 0, 0)), Composite(Component(2, 0, 0)), Composite(Component(3, 0, 0)) };
        try
        {
            PdfLimits.MaxCompositeGlyphDepth = 3;
            Assert.True(Read(glyphs).TryGetGlyph(4, out _));
            PdfLimits.MaxCompositeGlyphDepth = 2;
            Assert.False(Read(glyphs).TryGetGlyph(4, out _));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    // ==================================================================
    //  Programs without outlines
    // ==================================================================

    [Fact]
    public void TryRead_ACffProgram_IsRefusedWithAReason()
    {
        TrueTypeFontProgram cff = TrueTypeFontProgram.Parse(MinimalOpenTypeCff.Build("A"));

        Assert.Null(TrueTypeOutlines.TryRead(cff, out string reason));
        Assert.Contains("CFF", reason, System.StringComparison.Ordinal);
        Assert.NotNull(TrueTypeOutlines.TryRead(Program(new[] { Box }), out _));
    }

    [Fact]
    public void TryRead_NoProgram_IsRefusedWithAReason()
    {
        Assert.Null(TrueTypeOutlines.TryRead(null, out string reason));
        Assert.NotNull(reason);
    }
}
