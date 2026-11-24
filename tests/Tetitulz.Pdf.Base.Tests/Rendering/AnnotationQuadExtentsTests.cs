// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <see cref="AnnotationQuadPoints"/>' extents are taken over <b>all four</b> corners of a
/// <c>/QuadPoints</c> quad (§12.5.6.10), not over the two the spec nominates as an edge.
/// </summary>
/// <remarks>
/// <para><b>This is what the class exists for.</b> §12.5.6.10's corner order has been written
/// inconsistently by real producers for years — a common viewer's own output has swapped the last
/// two — so extents from only the two corners assumed to be the bottom edge give a quad in another
/// winding a rule of the wrong length, in the wrong place, or both.</para>
///
/// <para>Without this test, reverting <c>Left</c> to <c>Math.Min(x0, x1)</c> — the two-corner form —
/// leaves every other Base test green: a reader "simplifying" the four-corner min/max would
/// reintroduce it silently.</para>
///
/// <para><b>Why there are five windings and not one.</b> A given corner order only discriminates for
/// the extent its first two corners get wrong — the canonical order spans the full width, so
/// <c>min(x0,x1)</c> and the four-corner minimum agree and it detects nothing about <c>Left</c> or
/// <c>Right</c>. Each row puts a different edge first, so between them every one of the four extents
/// has a row that fails if it reverts to the two-corner form; one winding would miss exactly
/// that.</para>
/// </remarks>
public class AnnotationQuadExtentsTests
{
    private static PdfDictionary AnnotWithQuad(params double[] coords)
    {
        var arr = new PdfArray();
        foreach (double c in coords) arr.Add(new PdfNumber(c));

        var annot = new PdfDictionary();
        annot.Set(PdfName.QuadPoints, arr);
        return annot;
    }

    /// <summary>
    /// <b>The extents are invariant under corner order.</b> Every row below is the same 10..100 by
    /// 10..20 quad with its four corners listed in a different order, and all four must produce the
    /// same rectangle — which is the entire reason the code takes min/max over all four rather than
    /// trusting §12.5.6.10's nominal ordering.
    /// </summary>
    /// <remarks>
    /// <b>One winding is not enough, and the first draft of this file proved it.</b> It used only
    /// the edge-first order, whose first two corners share x = 10 — so <c>Math.Min(x0, x1)</c> and
    /// the four-corner minimum agree, and reverting <c>Left</c> to the two-corner form still passed.
    /// A fixture only discriminates for the extent whose value the first two corners get wrong, so
    /// covering all four extents needs the windings that put each edge first.
    /// </remarks>
    [Theory]
    // canonical: top edge, then bottom edge
    [InlineData(new double[] { 10, 20, 100, 20, 10, 10, 100, 10 })]
    // A common viewer's swapped variant: top edge first, so the first two corners share the MAXIMUM y
    // and Bottom is the extent that breaks
    [InlineData(new double[] { 10, 20, 100, 20, 100, 10, 10, 10 })]
    // left edge first — first two corners share the MINIMUM x, so Right is what breaks
    [InlineData(new double[] { 10, 20, 10, 10, 100, 20, 100, 10 })]
    // right edge first — first two corners share the MAXIMUM x, so Left is what breaks
    [InlineData(new double[] { 100, 20, 100, 10, 10, 20, 10, 10 })]
    // bottom edge first — first two corners share the MINIMUM y, so Top is what breaks
    [InlineData(new double[] { 10, 10, 100, 10, 10, 20, 100, 20 })]
    public void TheExtentsAreTheSameWhicheverCornerOrderTheProducerUsed(double[] coords)
    {
        List<AnnotationQuadPoints.Quad> quads =
            AnnotationQuadPoints.Read(null, AnnotWithQuad(coords), out bool present);

        Assert.True(present);
        AnnotationQuadPoints.Quad q = Assert.Single(quads);

        Assert.Equal(10f,  q.Left,   3);
        Assert.Equal(100f, q.Right,  3);
        Assert.Equal(10f,  q.Bottom, 3);
        Assert.Equal(20f,  q.Top,    3);
    }

    /// <summary>
    /// The rule positions derived from those extents: an Underline sits at the bottom, a StrikeOut
    /// halfway up. Both are wrong the moment the extents are, and this is what a reader sees.
    /// </summary>
    [Fact]
    public void TheRulePositionsFollowTheFullHeight()
    {
        AnnotationQuadPoints.Quad q = Assert.Single(
            AnnotationQuadPoints.Read(null, AnnotWithQuad(10, 10, 100, 10, 10, 20, 100, 20), out _));

        Assert.Equal(10f, q.RuleY(0f),   3);
        Assert.Equal(15f, q.RuleY(0.5f), 3);
        Assert.Equal(20f, q.RuleY(1f),   3);
    }
}
