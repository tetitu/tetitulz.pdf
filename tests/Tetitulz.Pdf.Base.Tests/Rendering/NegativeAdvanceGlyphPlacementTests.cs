// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using SkiaSharp;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A negative per-glyph advance — the ordinary result of negative <c>Tc</c> or <c>Tw</c> (§9.4.4
/// folds both into <c>tx</c>) — must place glyphs to the <b>left</b>, on the embedded-CFF text
/// path as much as anywhere else.
/// </summary>
/// <remarks>
/// <para>This exists because of a magic-value collision. The per-glyph width array carries a
/// sentinel meaning "this font is a system substitute, measure the glyph with Skia instead", and
/// that sentinel was <i>any negative value</i>. A genuinely negative advance therefore selected
/// the measure branch, which returns Skia's always-positive advance — so the glyph was POSITIONED
/// as if the file had asked for a positive step while the caller's text-matrix advance still
/// accumulated the negative one. The run's internal pitch and its end position disagreed, and
/// every glyph after the first negative step sat too far right.</para>
/// <para>The sentinel is now <c>NaN</c>, which the width arithmetic cannot produce.</para>
/// </remarks>
public class NegativeAdvanceGlyphPlacementTests
{
    // The fixture glyph is a filled box 400/1000 em wide on a 500/1000 em advance. At size 100
    // that is a 40 pt box stepping 50 pt. With Tc -60 the step becomes 50 - 60 = -10 pt, so the
    // three glyphs march left from the pen: boxes at x 300..340, 290..330 and 280..320.
    private const float FontSize = 100f;
    private const float StartX = 300f;
    private const float BaselineY = 400f;

    private static byte[] NegativeCharSpacingPage() => EmbeddedCffFixture.Page(
        $"BT /F1 {FontSize} Tf -60 Tc {StartX} {BaselineY} Td (AAA) Tj ET");

    /// <summary>
    /// Ink appears left of the pen start, which can only happen if the negative step was honoured.
    /// </summary>
    [Fact]
    public void NegativeCharSpacing_MovesGlyphsLeftOfThePenStart()
    {
        using SKBitmap bmp = PixelAssert.Render(NegativeCharSpacingPage());

        // The third glyph's box spans x 280..320; sample inside it but left of the first box's
        // left edge (300), so only a leftward step can put ink here.
        Assert.True(PixelAssert.IsInk(PixelAssert.At(bmp, 288f, BaselineY + 20f)),
                    "no ink left of the pen start: the negative advance was dropped");
    }

    /// <summary>
    /// The complement, and the assertion that actually fails when the sentinel collides: with the
    /// bug the glyphs step +50 pt each and reach x 440, far right of anything the file asked for.
    /// </summary>
    [Fact]
    public void NegativeCharSpacing_DoesNotStepRightward()
    {
        using SKBitmap bmp = PixelAssert.Render(NegativeCharSpacingPage());

        // With the negative step honoured, the rightmost ink is the first glyph's right edge at
        // x = 340. A rightward march puts the third box at 400..440.
        Assert.True(PixelAssert.IsWhite(PixelAssert.At(bmp, 420f, BaselineY + 20f)),
                    "ink at x=420: the glyphs stepped right, so a positive measured width was " +
                    "substituted for the file's negative advance");
    }

    /// <summary>
    /// The run's total width must follow the same negative step, so the text that follows on the
    /// line starts left of where this run began — the text-matrix half of the same arithmetic.
    /// </summary>
    /// <remarks>
    /// Positioning and the <c>Tm</c> advance are computed from the same value but consumed by
    /// different code, which is exactly how the defect stayed invisible: <c>Tm</c> was always
    /// right, so a test that only checked where the NEXT run landed would have passed throughout.
    /// <b>Measured, not assumed</b> — restoring the old sentinel reddens the other two tests in
    /// this file and leaves this one green. It is kept because it pins the half that was correct:
    /// a later change that "fixes" placement by negating the <c>Tm</c> step instead would pass
    /// those two and fail this one.
    /// </remarks>
    [Fact]
    public void NegativeCharSpacing_AdvancesTheTextMatrixLeftward()
    {
        // Three steps of -10 pt from x=300 leaves the pen at 270, so a following glyph's box
        // spans 270..310. Sample at 275, left of every box drawn by the first run.
        using SKBitmap bmp = PixelAssert.Render(EmbeddedCffFixture.Page(
            $"BT /F1 {FontSize} Tf -60 Tc {StartX} {BaselineY} Td (AAA) Tj 0 Tc (A) Tj ET"));

        Assert.True(PixelAssert.IsInk(PixelAssert.At(bmp, 275f, BaselineY + 20f)),
                    "the following run did not start left of the first: Tm was not advanced by " +
                    "the negative width");
    }
}
