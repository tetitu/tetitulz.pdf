// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Security.Cryptography;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §8.4.3.4: the line join applies where two segments meet. A path with no join must therefore
/// render the same whatever <c>j</c> says.
/// </summary>
/// <remarks>
/// <para><b>It did not.</b> <c>GetStrokePathForJoin</c> pre-flattened Beziers whenever the join was
/// miter — the PDF default — so Skia would honour a large miter limit at an acute line-curve join
/// (see the remarks there). Flattening replaces the curve with line segments, and the stroker then
/// takes the end CAP direction from the last of those segments rather than from the curve's true
/// tangent. On a path with no joins that is pure loss: nothing needed the flattening, and the caps
/// came out fractionally rotated.</para>
///
/// <para>So a single open cubic stroked with <c>0 j</c> rendered differently from the same cubic
/// with <c>1 j</c>, which is not something the line join is allowed to do. Skipping the flattening
/// for a path of at most two verbs — a move plus one open segment, a close being a third — fixes
/// the caps and removes the work at once.</para>
///
/// <para><b>Paths that do have joins are untouched</b>, which the second case pins: a multi-segment
/// curve and a closed single curve both still flatten, so miter and round stay distinguishable.</para>
/// </remarks>
public class JoinlessStrokeCapTests
{
    /// <summary>The join style must not change a stroke that has no joins.</summary>
    [Fact]
    public void AJoinlessCurveRendersTheSameUnderMiterAndRoundJoins()
    {
        string miter = Render("40 40 m 120 200 200 -40 280 80 c S");
        string round = Render("1 j 40 40 m 120 200 200 -40 280 80 c S");
        Assert.Equal(round, miter);
    }

    /// <summary>
    /// The control, and the reason the skip is bounded at two verbs: a path that <i>does</i> join
    /// must still see the flattening, so miter and round remain different renderings.
    /// </summary>
    [Theory]
    [InlineData("40 40 m 120 200 200 -40 280 80 c 360 200 420 -20 500 80 c S")]
    [InlineData("40 40 m 120 200 200 -40 280 80 c h S")]
    public void APathWithJoinsStillDistinguishesTheJoinStyle(string ops)
    {
        Assert.NotEqual(Render("1 j " + ops), Render(ops));
    }

    /// <summary>A wide stroke with square caps, so a cap-direction change is visible in pixels.</summary>
    private static string Render(string ops)
    {
        // 8 w: wide enough that a fractional cap rotation moves pixels. 2 J: projecting square
        // caps, which extend along the tangent and so carry its direction. 10 M: the DEFAULT miter
        // limit, i.e. no large-limit setup — this is the ordinary case, not the one the flattening
        // was written for.
        string content = "q 8 w 2 J 10 M 0 0 0 RG\n" + ops + "\nQ\n";
        byte[] pdf = EmbeddedCffFixture.SinglePage(content, "", null);
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf, false), 0, 150);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return Convert.ToHexString(SHA256.HashData(bmp.GetPixelSpan().ToArray()));
    }
}