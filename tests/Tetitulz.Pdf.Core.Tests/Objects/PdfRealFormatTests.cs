// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Objects;

/// <summary>
/// §7.3.3's real-number syntax, and the three writers that must agree about it:
/// <c>PdfNumber.ToString</c>, <c>PdfContentStreamBuilder.FormatNumber</c> and <c>PdfWriter</c>'s
/// number branch, all through <c>PdfRealFormat</c>.
///
/// <para>The assertions are on the emitted <b>token</b>: a bare <c>NaN</c> / <c>Infinity</c>
/// keyword — which §7.3.3 has no syntax for and no reader accepts — is a perfectly
/// valid-looking string that is not a number.</para>
/// </summary>
public sealed class PdfRealFormatTests
{
    // ── non-finite values ─────────────────────────────────────────────────

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteValues_NeverReachTheOutputAsAKeyword(double v)
    {
        Assert.Equal("0", PdfRealFormat.Format(v));
        Assert.Equal("0", new PdfNumber(v).ToString());
        Assert.Equal("0", PdfContentStreamBuilder.FormatNumber(v));
    }

    /// <summary>
    /// The coercion is reported rather than hidden, so a writer can count what it silently changed.
    /// </summary>
    [Fact]
    public void TryFormat_ReportsTheCoercion_AndOnlyForNonFiniteValues()
    {
        Assert.False(PdfRealFormat.TryFormat(double.NaN, out string nan));
        Assert.Equal("0", nan);

        Assert.True(PdfRealFormat.TryFormat(1.5, out string ok));
        Assert.Equal("1.5", ok);

        // Zero is not a coercion — it is a number that happens to format as the same text.
        Assert.True(PdfRealFormat.TryFormat(0.0, out string zero));
        Assert.Equal("0", zero);
    }

    /// <summary>
    /// End to end: a <c>/MediaBox</c> holding NaN must not produce a file whose token stream
    /// contains <c>NaN</c>, and the writer must say it happened.
    /// </summary>
    [Fact]
    public void PdfWriter_CoercesANonFiniteNumber_AndCountsIt()
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);

        var box = new PdfArray();
        box.Add(new PdfNumber(0));
        box.Add(new PdfNumber(0));
        box.Add(new PdfNumber(double.NaN));
        box.Add(new PdfNumber(842));
        writer.WriteObject(box);

        string text = Encoding.ASCII.GetString(ms.ToArray());
        Assert.DoesNotContain("NaN", text);
        Assert.Contains("842", text);
        Assert.Equal(1, writer.NonFiniteNumbersCoercedToZero);
    }

    // ── the parts that were already right, pinned so the move did not change them ──

    [Theory]
    [InlineData(1.5, "1.5")]
    [InlineData(-1.5, "-1.5")]
    [InlineData(0.0, "0")]
    [InlineData(0.1, "0.1")]
    [InlineData(1.0000005, "1.000001")]   // rounds at the sixth decimal
    [InlineData(0.0000001, "0")]          // below the sixth decimal — trims to nothing, not to ""
    // …and a negative underflow keeps its sign, because "F6" always emits the leading digit.
    // "-0" is a legal §7.3.3 real; it is pinned here so nobody "tidies" it and changes output bytes.
    [InlineData(-0.0000001, "-0")]
    [InlineData(100.0, "100")]            // trailing zeros stripped, and the '.' with them
    public void FixedPointFormatting_IsUnchanged(double v, string expected)
        => Assert.Equal(expected, PdfRealFormat.Format(v));

    /// <summary>
    /// Never scientific notation. <c>1e-7</c> and <c>1e20</c> are where the "R"/"G" formats would
    /// emit an exponent, which is not PDF syntax.
    /// </summary>
    [Theory]
    [InlineData(1e-7)]
    [InlineData(1e20)]
    [InlineData(-1e20)]
    public void FormattingIsNeverScientific(double v)
    {
        string s = PdfRealFormat.Format(v);
        Assert.DoesNotContain("e", s, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A decimal comma makes a content stream unparseable, so the culture must not be the thread's.
    /// Asserted under a culture that actually uses one — the invariant-culture call is correct in
    /// the source either way, and only running it under <c>de-DE</c> can catch a regression to
    /// <c>ToString(v)</c>.
    /// </summary>
    [Fact]
    public void FormattingIgnoresTheThreadCulture()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1.5", PdfRealFormat.Format(1.5));
            Assert.Equal("1.5", new PdfNumber(1.5).ToString());
            Assert.Equal("1.5", PdfContentStreamBuilder.FormatNumber(1.5));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    /// <summary>
    /// An integer-valued <see cref="PdfNumber"/> keeps its integer spelling — <c>/Length 12</c>,
    /// not <c>/Length 12.0</c> — which is a separate branch the consolidation had to leave alone.
    /// </summary>
    [Fact]
    public void IntegerNumbers_KeepTheirIntegerSpelling()
    {
        Assert.Equal("12", new PdfNumber(12L).ToString());
        Assert.Equal("-3", new PdfNumber(-3L).ToString());
    }
}
