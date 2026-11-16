// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <c>PdfColor.ApplyDecode</c> — §8.9.5.2's per-component <c>/Decode</c> mapping, now the one
/// implementation both renderers use.
/// </summary>
/// <remarks>
/// <para><b>Written because the merge was thinly covered.</b> The mapper existed twice —
/// <c>ContentStreamInterpreter.ApplyDecodeF01</c> and <c>SvgContentWriter.ApplyDecodeF</c>, the
/// latter's summary asserting in prose that it matched the former. They did match, apart from a
/// <c>comp &lt; 0</c> guard, and nothing enforced it. After consolidating, swapping the two bounds
/// in the shared helper reddened a single test in the whole Base suite: live, but not enough to
/// call the rule pinned.</para>
///
/// <para>These assert the mapping directly rather than through a render, so they pin the arithmetic
/// rather than any one call site's use of it — and unlike a post-merge "both sides agree" test,
/// which after a merge is a tautology, they can still fail.</para>
/// </remarks>
public class ApplyDecodeTests
{
    /// <summary>No array is the identity — the sample passes through unchanged.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(0.25f)]
    [InlineData(1f)]
    public void AnAbsentArrayIsTheIdentity(float v)
        => Assert.Equal(v, PdfColor.ApplyDecode(v, null, 0), 5);

    /// <summary><c>[1 0]</c> inverts: the canonical case, and the one a CMYK JPEG carries.</summary>
    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(0.25f, 0.75f)]
    [InlineData(1f, 0f)]
    public void AnInvertingPairMapsVToOneMinusV(float input, float expected)
        => Assert.Equal(expected, PdfColor.ApplyDecode(input, new[] { 1f, 0f }, 0), 5);

    /// <summary><c>[0 0.5]</c> halves the range — a non-identity, non-inverting pair.</summary>
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.5f, 0.25f)]
    [InlineData(1f, 0.5f)]
    public void APartialRangeScalesIntoIt(float input, float expected)
        => Assert.Equal(expected, PdfColor.ApplyDecode(input, new[] { 0f, 0.5f }, 0), 5);

    /// <summary>
    /// The component index selects its own pair, so a multi-component array does not read
    /// component 0's bounds for every channel.
    /// </summary>
    [Fact]
    public void EachComponentUsesItsOwnPair()
    {
        // Component 0 identity, component 1 inverting.
        var decode = new[] { 0f, 1f, 1f, 0f };

        Assert.Equal(0.25f, PdfColor.ApplyDecode(0.25f, decode, 0), 5);
        Assert.Equal(0.75f, PdfColor.ApplyDecode(0.25f, decode, 1), 5);
    }

    /// <summary>
    /// A component the array has no pair for falls back to the identity rather than reading past
    /// the end — and a negative index does too. The negative guard came from the SVG copy and was
    /// kept as the safer of the two; no caller passes one, so it is defensive.
    /// </summary>
    [Theory]
    [InlineData(2)]    // array holds one pair
    [InlineData(-1)]
    public void AnOutOfRangeComponentIsTheIdentity(int comp)
        => Assert.Equal(0.4f, PdfColor.ApplyDecode(0.4f, new[] { 1f, 0f }, comp), 5);

    /// <summary>
    /// The result is clamped to [0,1]. A <c>/Decode</c> outside that range is not legal for the
    /// spaces reaching this helper, so this pins the clamp rather than a real document shape.
    /// </summary>
    [Theory]
    [InlineData(1f, 1f)]
    [InlineData(0f, 0f)]
    public void TheResultIsClampedToTheUnitInterval(float input, float expected)
        => Assert.Equal(expected, PdfColor.ApplyDecode(input, new[] { -2f, 3f }, 0), 5);
}
