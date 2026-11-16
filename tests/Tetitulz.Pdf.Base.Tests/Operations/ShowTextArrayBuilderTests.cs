// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Base.Operations.Documents;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="ShowTextArrayBuilder"/>'s refusal to emit a kern it cannot compute — the one claim
/// about this class that no fixture reaching it through an engine can make.
/// </summary>
/// <remarks>
/// <para><b>Why this is a direct test and not a template fill.</b> The arithmetic itself is already
/// pinned hard from both engines: flipping the kern's sign reddens 8 tests in <c>Base.Tests</c> and
/// 26 in <c>Plugins.Templating.Tests</c>, dropping the glyph-space factor 10 and 26, and ignoring
/// the horizontal scale 2. What none of them reach is the <b>degenerate</b> case, because a text run
/// scaled to nothing draws nothing and no engine fixture has a reason to contain one.</para>
///
/// <para><b>Two guards stand here, and they overlap only at an exact zero.</b> With <c>Tfs · Th</c>
/// at zero the compensation is <c>-a · 1000 / 0</c>: the denominator test skips it, and the finite
/// test would discard the infinity if it did not — so at zero either one alone suffices, and the
/// original fixture (zeros only) could kill neither. Away from zero they part: a scale that is tiny
/// but not zero gives a <i>finite</i> kern of hundreds of billions, which only the denominator test
/// stops; and a dropped advance that is itself NaN or infinite passes the denominator test and is
/// stopped only by the finite one. Each has its own fixture below.</para>
///
/// <para><b>And the stake is a meaningless element, not a malformed file</b> — which was worth
/// measuring rather than assuming. Removing <i>both</i> guards emits <c>"0"</c>, not
/// <c>"Infinity"</c>, because <c>PdfRealFormat.Format</c> carries its own non-finite check and is
/// the backstop that keeps the output well-formed. So this is a third layer over a second one, and
/// the honest claim is that a run scaled to nothing contributes nothing to the array.</para>
/// </remarks>
public class ShowTextArrayBuilderTests
{
    /// <summary>
    /// A run scaled to nothing has no visible position to preserve, so the dropped advance is
    /// swallowed rather than turned into a kern of infinity.
    /// </summary>
    [Theory]
    [InlineData(0f, 1f)]      // Tf 0
    [InlineData(12f, 0f)]     // Tz 0
    [InlineData(0f, 0f)]      // both
    [InlineData(1e-7f, 1f)]   // tiny but not zero: finite, and only the denominator test stops it
    [InlineData(12f, 1e-8f)]
    public void ADegenerateTextScaleEmitsNoKernAtAll(float fontSize, float horizScale)
    {
        var b = new ShowTextArrayBuilder(fontSize, horizScale, "0.####");
        b.Keep((byte)'A');
        b.Drop(10f);              // an advance that would need compensating, if it could be
        b.Keep((byte)'B');

        IReadOnlyList<string> elements = b.Build();

        Assert.Equal(new[] { "<41>", "<42>" }, elements);
        Assert.DoesNotContain(elements, e => e.Contains("Infinity") || e.Contains("NaN"));
    }

    /// <summary>
    /// The control that gives the refusal its meaning: at an ordinary scale the same drop <b>is</b>
    /// compensated, and with the sign and the glyph-space factor the spec calls for. Without this,
    /// a builder that never emitted a kern at all would satisfy the test above.
    /// </summary>
    [Fact]
    public void AnOrdinaryScaleCompensatesTheDroppedAdvance()
    {
        var b = new ShowTextArrayBuilder(fontSize: 10f, horizScale: 1f, numberFormat: "0.####");
        b.Keep((byte)'A');
        b.Drop(5f);
        b.Keep((byte)'B');

        // 5 pt removed at a 10 pt em is half an em. A POSITIVE TJ number moves text left, so
        // putting back what was removed is a NEGATIVE one: -0.5 em = -500 thousandths.
        Assert.Equal(new[] { "<41>", "-500", "<42>" }, b.Build());
    }

    /// <summary>
    /// The horizontal scale divides the compensation: at 50 % an em is half as wide, so the same
    /// removed advance is twice as many thousandths of one.
    /// </summary>
    [Fact]
    public void TheHorizontalScaleDividesTheCompensation()
    {
        var b = new ShowTextArrayBuilder(fontSize: 10f, horizScale: 0.5f, numberFormat: "0.####");
        b.Keep((byte)'A');
        b.Drop(5f);
        b.Keep((byte)'B');

        Assert.Equal(new[] { "<41>", "-1000", "<42>" }, b.Build());
    }

    /// <summary>
    /// A dropped advance that is not a number contributes no element — at an ordinary scale, where
    /// the denominator test passes and only the finite test stands.
    /// </summary>
    /// <remarks>
    /// Without it the formatter's own non-finite backstop turns the NaN or infinity into <c>"0"</c>:
    /// well-formed, but an element the original array never had, standing between two kept runs.
    /// </remarks>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ANonFiniteDroppedAdvanceEmitsNoKern(float advance)
    {
        var b = new ShowTextArrayBuilder(fontSize: 10f, horizScale: 1f, numberFormat: "0.####");
        b.Keep((byte)'A');
        b.Drop(advance);
        b.Keep((byte)'B');

        Assert.Equal(new[] { "<41>", "<42>" }, b.Build());
    }

    /// <summary>
    /// <b>A code that runs past the end of its string keeps only the bytes that exist</b> — a
    /// two-byte code read over an odd-length string, for instance.
    /// </summary>
    [Fact]
    public void AKeepPastTheEndOfTheSourceIsClamped()
    {
        var b = new ShowTextArrayBuilder(fontSize: 10f, horizScale: 1f, numberFormat: "0.####");
        byte[] source = { 0x41, 0x42, 0x43 };

        b.Keep(source, 0, 2);
        b.Keep(source, 2, 2);     // one byte left of a two-byte code

        Assert.Equal(new[] { "<414243>" }, b.Build());
    }
}
