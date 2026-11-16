// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Caller-supplied SVG must not be able to put the bare keyword <c>NaN</c> or <c>Infinity</c> into
/// the emitted PDF.
/// </summary>
/// <remarks>
/// <para><b>This is the reachable case behind the register's "nine private formatters" row.</b>
/// <c>SvgToPdfConverter</c>'s <c>viewBox</c> path is guarded (<c>vbW &gt; 0 &amp;&amp; vbH &gt; 0</c>)
/// but a <c>transform</c> is not: <c>scale(1e40)</c> parses through <c>float.TryParse</c> to
/// +Infinity, and a zero-width rectangle then computes <c>0 * Infinity</c> = NaN, which the private
/// <c>F(float)</c> rendered literally into a <c>re</c> operator.</para>
///
/// <para><b>Asserted on the emitted bytes, not on "it did not throw".</b> The converter never threw
/// — it produced a perfectly plausible file containing a token no reader accepts, and a strict
/// reader may abandon the whole content stream rather than one operand. A non-throw assertion here
/// would be banned test pattern 3.</para>
/// </remarks>
public class SvgNonFiniteOperandTests
{
    /// <summary>
    /// <b>The defect.</b> An infinite transform scale over a zero-extent rectangle produced NaN
    /// operands.
    /// </summary>
    [Theory]
    [InlineData("scale(1e40)")]
    [InlineData("scale(1e40, 1e40)")]
    [InlineData("matrix(1e40 0 0 1e40 0 0)")]
    public void AnInfiniteTransformNeverEmitsANonFiniteKeyword(string transform)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>"
            + $"<g transform='{transform}'><rect width='0' height='10'/></g></svg>");

        AssertNoNonFiniteKeyword(pdf);
    }

    /// <summary>
    /// A degenerate scale on its own, without the zero extent — the other route to the same
    /// arithmetic.
    /// </summary>
    [Theory]
    [InlineData("scale(0)")]
    [InlineData("scale(1e40) scale(0)")]
    public void ADegenerateTransformNeverEmitsANonFiniteKeyword(string transform)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>"
            + $"<g transform='{transform}'><rect width='10' height='10' fill='red'/></g></svg>");

        AssertNoNonFiniteKeyword(pdf);
    }

    /// <summary>
    /// <b>The control.</b> An ordinary SVG still converts to a file with real content — so the
    /// theories above cannot be passing because the converter started emitting nothing at all.
    /// </summary>
    [Fact]
    public void AnOrdinarySvgStillProducesDrawingOperators()
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>"
            + "<rect x='10' y='10' width='50' height='30' fill='red'/></svg>");

        AssertNoNonFiniteKeyword(pdf);
        Assert.Contains(" re", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
    }

    private static void AssertNoNonFiniteKeyword(byte[] pdf)
    {
        Assert.NotNull(pdf);
        string text = Encoding.Latin1.GetString(pdf);

        Assert.DoesNotContain("NaN", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", text, StringComparison.Ordinal);
        Assert.DoesNotContain("∞", text, StringComparison.Ordinal);
    }
}
