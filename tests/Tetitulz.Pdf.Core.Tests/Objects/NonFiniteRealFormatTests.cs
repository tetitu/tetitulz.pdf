// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Objects;

/// <summary>
/// A non-finite value must never reach a content stream as the bare keyword <c>NaN</c>,
/// <c>Infinity</c> or <c>-Infinity</c> — none of which is a §7.3.3 number.
/// </summary>
/// <remarks>
/// <para><b>The consequence is bigger than one bad operand.</b> A strict reader that hits a token it
/// cannot parse may abandon the whole content stream, so a single divide-by-zero in a layout can
/// blank a page rather than misplace one rectangle.</para>
///
/// <para><b>Nine private one-line formatters across Base, Builder and the signing appearance
/// generator each did <c>v.ToString(fmt, InvariantCulture)</c> with no finiteness test.</b> They sit
/// at six different precisions, and those differences are deliberate — the output bytes are what
/// this project has shipped for its whole history. So the consolidation shares the
/// <i>finiteness rule</i> and leaves precision alone, which is what
/// <see cref="PdfRealFormat.TryFormat(double, string, out string)"/> exists for.</para>
///
/// <para><b>Why an upstream guard is not enough</b>, and why this is not paranoia: NaN propagates
/// instead of trapping, and every comparison against it is false — so the common
/// <c>if (width &lt;= 0) return;</c> passes NaN straight through. <see cref="PdfRealFormat"/>'s own
/// remarks already recorded that; the nine copies were written as if they had not.</para>
/// </remarks>
public class NonFiniteRealFormatTests
{
    /// <summary>
    /// <b>The defect.</b> Every non-finite value, at every precision the nine call sites use,
    /// becomes the substitute rather than a keyword.
    /// </summary>
    [Theory]
    [InlineData("0.##")]
    [InlineData("0.###")]
    [InlineData("0.####")]
    [InlineData("0.######")]
    [InlineData("F2")]
    [InlineData("F3")]
    [InlineData("F4")]
    public void ANonFiniteValueIsNeverWrittenAsAKeyword(string format)
    {
        foreach (double v in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            string text = PdfRealFormat.Format(v, format);

            Assert.Equal(PdfRealFormat.NonFiniteSubstitute, text);
            Assert.DoesNotContain("NaN", text);
            Assert.DoesNotContain("Infinity", text);
        }
    }

    /// <summary>
    /// And the caller can tell it happened, which is what lets a writer count coercions rather than
    /// hide them.
    /// </summary>
    [Fact]
    public void TheCoercionIsReported()
    {
        Assert.False(PdfRealFormat.TryFormat(double.NaN, "F4", out string nan));
        Assert.Equal(PdfRealFormat.NonFiniteSubstitute, nan);

        Assert.True(PdfRealFormat.TryFormat(1.5, "F4", out string finite));
        Assert.Equal("1.5000", finite);
    }

    /// <summary>
    /// <b>The floor, and the reason this consolidation is safe.</b> For a finite value the shared
    /// helper must produce byte-identical output to the <c>ToString</c> call it replaced — otherwise
    /// it silently rewrites every file these nine writers emit.
    /// </summary>
    [Theory]
    [InlineData(0.0, "F4")]
    [InlineData(1.0, "F4")]
    [InlineData(-1.5, "F4")]
    [InlineData(0.123456789, "F4")]
    [InlineData(1234.5678, "0.##")]
    [InlineData(0.5, "0.###")]
    [InlineData(-0.00001, "0.####")]
    [InlineData(12345.6789, "0.######")]
    [InlineData(3.14159, "F2")]
    [InlineData(2.71828, "F3")]
    [InlineData(1e30, "F2")]
    [InlineData(-1e30, "0.###")]
    public void AFiniteValueIsFormattedExactlyAsBefore(double v, string format)
        => Assert.Equal(v.ToString(format, CultureInfo.InvariantCulture),
                        PdfRealFormat.Format(v, format));

    /// <summary>
    /// The parameterless form keeps its own contract — <c>F6</c> then trim — so the new overload
    /// cannot be mistaken for having changed it.
    /// </summary>
    [Theory]
    [InlineData(1.5, "1.5")]
    [InlineData(1.0, "1")]
    [InlineData(0.0, "0")]
    [InlineData(0.1234567, "0.123457")]
    public void TheParameterlessFormIsUnchanged(double v, string expected)
        => Assert.Equal(expected, PdfRealFormat.Format(v));
}
