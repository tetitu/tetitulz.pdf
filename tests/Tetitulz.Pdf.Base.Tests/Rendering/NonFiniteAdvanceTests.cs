// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <see cref="MatrixGuard.SanitizeAdvance"/> — the half-em fallback both renderers use when a
/// glyph advance comes out non-finite.
/// </summary>
/// <remarks>
/// <para><b>Why this is one function.</b> The raster interpreter and the SVG writer each carried
/// the rule. The raster copy already reached <see cref="MatrixGuard.IsFinite"/>; the SVG copy wrote
/// the predicate out twice, so the two could drift on the predicate and on the fallback
/// independently — and neither copy had a test, so a drift would have been silent.</para>
///
/// <para><b>What it prevents.</b> A corrupt charstring yields NaN or Infinity, and composing that
/// into <c>Tm</c> poisons the text matrix for the rest of the run: every later glyph draws at
/// non-finite coordinates and is silently invisible. The fallback does not try to recover a correct
/// advance — the file supplied none — only to keep the run addressable.</para>
///
/// <para><b>These assertions are on the rule, not on a render.</b> Reaching it through either
/// renderer needs an <i>embedded</i> CFF or Type 1 program whose advance is non-finite: all three
/// raster call sites are `DrawOtfCffRun`, `DrawType1Run` and the CFF path run, and the standard-14
/// path does not use it. No fixture in the suite has such a font, which GAPS.md §9 records.</para>
/// </remarks>
public class NonFiniteAdvanceTests
{
    /// <summary>A finite advance is returned unchanged, whatever the size beside it.</summary>
    [Theory]
    [InlineData(0f, 12f)]
    [InlineData(6.5f, 12f)]
    [InlineData(-3f, 12f)]
    [InlineData(600f, 0f)]
    public void AFiniteAdvanceIsKept(float advance, float fontSize)
        => Assert.Equal(advance, MatrixGuard.SanitizeAdvance(advance, fontSize));

    /// <summary>A non-finite advance becomes half the font size.</summary>
    /// <remarks>
    /// Half an em is the substitute; asserting the exact value is what distinguishes this from a
    /// fallback of the size itself, or of zero, either of which would still be "finite".
    /// </remarks>
    [Theory]
    [InlineData(float.NaN, 12f, 6f)]
    [InlineData(float.PositiveInfinity, 12f, 6f)]
    [InlineData(float.NegativeInfinity, 10f, 5f)]
    [InlineData(float.NaN, 1f, 0.5f)]
    public void ANonFiniteAdvanceBecomesHalfTheSize(float advance, float fontSize, float expected)
        => Assert.Equal(expected, MatrixGuard.SanitizeAdvance(advance, fontSize));

    /// <summary>
    /// When the size is unusable too, the fallback is half a point — not half of a poisoned number.
    /// </summary>
    /// <remarks>
    /// This is the case an overflowing <c>Tf</c> produces: the size is what made the advance
    /// non-finite, so deriving the fallback from it would return a non-finite value and poison
    /// <c>Tm</c> anyway. A zero or negative size is unusable for the same reason a viewer cannot
    /// draw at it.
    /// </remarks>
    [Theory]
    [InlineData(float.NaN, float.PositiveInfinity)]
    [InlineData(float.NaN, float.NaN)]
    [InlineData(float.PositiveInfinity, float.PositiveInfinity)]
    [InlineData(float.NaN, 0f)]
    [InlineData(float.NaN, -12f)]
    public void AnUnusableSizeFallsBackToHalfAPoint(float advance, float fontSize)
        => Assert.Equal(0.5f, MatrixGuard.SanitizeAdvance(advance, fontSize));

    /// <summary>
    /// Whatever the inputs, the answer is finite — the property the call sites depend on.
    /// </summary>
    [Theory]
    [InlineData(float.NaN, float.NaN)]
    [InlineData(float.PositiveInfinity, float.NegativeInfinity)]
    [InlineData(float.NegativeInfinity, 0f)]
    [InlineData(float.NaN, float.Epsilon)]
    public void TheAnswerIsAlwaysFinite(float advance, float fontSize)
        => Assert.True(MatrixGuard.IsFinite(MatrixGuard.SanitizeAdvance(advance, fontSize)),
                       "a non-finite fallback would poison Tm, which is the whole point of the rule");
}
