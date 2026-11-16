// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// A page-box coordinate that is finite as a <c>double</c> but overflows a <c>float</c> is refused,
/// rather than returned as <c>±Infinity</c>.
/// </summary>
/// <remarks>
/// <para><c>PdfArtifactMarker</c> and <c>ContentStreamTagger</c> both read <c>/CropBox</c> else
/// <c>/MediaBox</c> for the page's vertical extent, and disagreed on one step: whether the value is
/// still finite <b>after</b> the cast to <c>float</c>. The tagger re-tested and refused; the marker
/// stopped at <c>double.IsInfinity</c> and returned <c>true</c> with an infinite result. One rule,
/// two answers — now one, in <see cref="PageBoxNumber"/>, on the stricter side.</para>
///
/// <para><b>What is asserted here is the rule, not a page-level outcome, and that is deliberate.</b>
/// The register predicted that an infinite extent would let the marker process the page and silently
/// classify no headers while still marking footers. Two attempts to demonstrate that through
/// <c>MarkHeadersAndFootersDetailed</c> left the mutant green: the page ends up unchanged either
/// way, because whatever survives the box read is refused further down. Asserting a page-level
/// outcome would therefore have been a test that cannot fail. The rule itself does fail when
/// broken, so that is what these assert.</para>
///
/// <para><b>The register's separating input was also unreachable.</b> It gave <c>1e40</c>, but PDF
/// numbers have no exponent syntax (§7.3.3: a real value "shall not use exponential notation"), so
/// a tokenizer reads that as the number <c>1</c> followed by a keyword <c>e40</c> — a 1 pt page,
/// never an overflow. Only a plain decimal carries the value into a file, which is what the
/// constant below is.</para>
/// </remarks>
public sealed class PageBoxOverflowTests
{
    /// <summary>10^40 written out in full — the only form of this value a real PDF can carry.</summary>
    private const string OverflowingHeight = "10000000000000000000000000000000000000000";

    /// <summary>The rule: finite as a double, infinite as a float, therefore refused.</summary>
    [Fact]
    public void ACoordinateTooLargeForAFloatIsRefused()
    {
        double d = double.Parse(OverflowingHeight, CultureInfo.InvariantCulture);

        bool ok = PageBoxNumber.TryResolveFinite(
            null, new PdfNumber(d), "test", "", "/MediaBox", out float value);

        Assert.False(ok);
        Assert.Equal(0f, value);
    }

    /// <summary>
    /// <b>The control.</b> An ordinary coordinate still resolves — without this the case above
    /// would pass on a resolver that refused everything.
    /// </summary>
    [Fact]
    public void AnOrdinaryCoordinateStillResolves()
    {
        bool ok = PageBoxNumber.TryResolveFinite(
            null, new PdfNumber(792d), "test", "", "/MediaBox", out float value);

        Assert.True(ok);
        Assert.Equal(792f, value);
    }

    /// <summary>
    /// The premise, and the reason the pre-cast guard the marker already had could never have
    /// caught this: the value passes <c>double.IsInfinity</c> and fails <c>float.IsInfinity</c>.
    /// </summary>
    [Fact]
    public void TheFixtureValueIsFiniteAsADoubleAndInfiniteAsAFloat()
    {
        double d = double.Parse(OverflowingHeight, CultureInfo.InvariantCulture);

        Assert.False(double.IsInfinity(d));
        Assert.True(float.IsInfinity((float)d));
    }

    /// <summary>
    /// A NaN is refused too. A NaN comparison is false in both directions, so a naive range test
    /// would let it through and leave the band undefined rather than closed.
    /// </summary>
    [Fact]
    public void ANotANumberCoordinateIsRefused()
    {
        bool ok = PageBoxNumber.TryResolveFinite(
            null, new PdfNumber(double.NaN), "test", "", "/MediaBox", out float value);

        Assert.False(ok);
        Assert.Equal(0f, value);
    }
}
