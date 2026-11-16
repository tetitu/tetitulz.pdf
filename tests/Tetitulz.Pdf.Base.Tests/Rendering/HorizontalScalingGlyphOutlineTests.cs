// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <c>Tz</c> (horizontal scaling) on the raster paths for an <b>embedded</b> font:
/// <c>DrawGlyphPath</c> (CFF and Type 1 outlines, still one glyph at a time) and
/// <c>DrawOtfCffRun</c> (OTF-wrapped CFF, now one <c>SKTextBlob</c> per run).
///
/// <para><b>What was wrong.</b> §9.4.4 builds the text rendering matrix as
/// <c>[Tfs·Th 0 0 ; 0 Tfs 0 ; 0 Trise 1] × Tm × CTM</c> — the horizontal scaling <c>Th</c> sits in
/// the <c>a</c> slot, so it stretches the <i>glyph</i> and not merely the pen step.
/// <c>DrawStandardRun</c> (the TrueType/system path) has composed it there since the 2026-08-07
/// round, and <c>SvgContentWriter</c> composes it at all three of its text-matrix sites. The two
/// per-glyph methods composed <c>{ Tfs, 0, 0, Tfs, 0, Trise }</c> instead, so an embedded CFF or
/// Type 1 font advanced at half width under <c>50 Tz</c> and drew every glyph at full width.</para>
///
/// <para><b>Why a single glyph.</b> With a run, the advance halves whether or not the glyph does,
/// so the run's total ink width moves either way and the assertion is a weak one. With exactly one
/// glyph there is no advance in the measurement at all: the ink is the glyph, and its width is
/// <c>Th</c> times the unscaled width or it is not.</para>
///
/// <para><b>And why the height is asserted too.</b> "The ink got narrower" is also true of a
/// renderer that scaled the whole glyph down, which would be a different bug. Asserting that the
/// height does <i>not</i> move is what separates "stretched horizontally" from "scaled" — the same
/// split <c>UncoveredRenderFeatureTests.HorizontalScaling_StretchesTheGlyphs_NotOnlyTheAdvances</c>
/// makes for the batched path.</para>
///
/// <para><b>Which of the two methods this actually reaches, stated because the first run of these
/// tests did not establish it.</b> They pin <c>DrawOtfCffRun</c>. A bare-CFF <c>/FontFile3</c> is
/// wrapped into an OTF envelope by <c>FontCache</c> (<c>CffOtfWrapper.TryBuild</c>) so Skia
/// applies Type 2 hinting, which sets <c>IsOtfCff</c> and routes the run to <c>DrawOtfCffRun</c> —
/// there is no fixture shape that reaches <c>DrawGlyphPath</c> through an embedded CFF while the
/// wrap succeeds. Mutating <c>DrawOtfCffRun</c>'s size matrix reddens
/// <c>…IsDrawnAtHalfWidth</c>; mutating <c>DrawGlyphPath</c>'s <b>survives</b>, and that survival
/// is a fact about this fixture rather than about the fix. (The size matrix used to live in
/// <c>PdfRenderContext.DrawGlyphById</c>, which the run-batching change deleted; the expression
/// itself moved into <c>DrawOtfCffRun</c> unchanged.)</para>
///
/// <para><c>DrawGlyphPath</c> carries the identical correction and is reached only by a Type 1
/// <c>/FontFile</c> or by a CFF whose OTF wrap fails — neither of which this fixture can produce
/// (a Type 1 program needs eexec-encrypted Type 1 charstrings, a different charstring format from
/// the Type 2 ones built below). <b>It is therefore fixed but not covered here</b>, which is
/// recorded rather than papered over; a Type 1 fixture is the thing to add.</para>
/// </summary>
public sealed class HorizontalScalingGlyphOutlineTests
{
    // The fixture glyph is a 400x400 box on a 1000-unit em, so at 100 pt it is exactly 40x40 pt
    // and at 50 Tz it must be 20 pt wide and still 40 pt tall.
    private const float FontSizePt = 100f;
    private const float ExpectedFullWidthPt = 40f;
    private const float ExpectedHeightPt = 40f;

    /// <summary>Rasterised at 1 px per point, so ink bounds are directly comparable to points.</summary>
    private static (float width, float height) GlyphInkExtent(int horizontalScalingPercent)
    {
        using SKBitmap bmp = PixelAssert.Render(EmbeddedCffGlyphPage(horizontalScalingPercent));
        var bounds = PixelAssert.InkBounds(bmp);
        Assert.True(bounds.HasValue,
            $"The page rendered blank at {horizontalScalingPercent} Tz, so the fixture never " +
            "reached the embedded-CFF glyph path and nothing below would be measuring Tz.");

        var b = bounds.Value;
        return (b.right - b.left + 1, b.bottom - b.top + 1);
    }

    /// <summary>
    /// The control: at <c>100 Tz</c> the glyph must be the size its charstring and font size say.
    /// Without this the halving test below could be satisfied by a renderer that drew the glyph at
    /// the wrong size in both runs.
    /// </summary>
    [Fact]
    public void EmbeddedCffGlyph_AtFullScale_IsTheSizeItsCharstringDeclares()
    {
        var (width, height) = GlyphInkExtent(100);

        Assert.Equal(ExpectedFullWidthPt, width, tolerance: 2f);
        Assert.Equal(ExpectedHeightPt, height, tolerance: 2f);
    }

    [Fact]
    public void EmbeddedCffGlyph_UnderHorizontalScaling_IsDrawnAtHalfWidth()
    {
        var (fullWidth, _) = GlyphInkExtent(100);
        var (halfWidth, _) = GlyphInkExtent(50);

        Assert.Equal(fullWidth / 2f, halfWidth, tolerance: 2f);
    }

    /// <summary>
    /// <c>Tz</c> is horizontal scaling, so the glyph's height must be untouched by it.
    /// </summary>
    [Fact]
    public void EmbeddedCffGlyph_UnderHorizontalScaling_KeepsItsHeight()
    {
        var (_, fullHeight) = GlyphInkExtent(100);
        var (_, halfHeight) = GlyphInkExtent(50);

        Assert.Equal(fullHeight, halfHeight, tolerance: 2f);
    }

    // ================================================================
    //  Fixture
    // ================================================================

    /// <summary>
    /// One page drawing a single <c>'A'</c> from an embedded bare-CFF font (<c>/FontFile3</c>
    /// <c>/Subtype /Type1C</c>).
    ///
    /// <para><b>This reaches the OTF-CFF run path, not <c>DrawGlyphPath</c></b> — see the class
    /// summary and <see cref="EmbeddedCffFixture"/>, which now owns the font bytes because a
    /// second test class needs the same ones.</para>
    /// </summary>
    private static byte[] EmbeddedCffGlyphPage(int horizontalScalingPercent) =>
        EmbeddedCffFixture.Page(
            $"BT /F1 {FontSizePt:0} Tf {horizontalScalingPercent} Tz 100 300 Td (A) Tj ET");
}
