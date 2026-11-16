// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Objects;

/// <summary>
/// <see cref="PdfRealFormat.TryFormat(double, out string)"/> computes its trailing-zero trim in one
/// pass instead of chaining two <c>TrimEnd</c> calls. This asserts the two agree <b>byte for
/// byte</b>, over far more values than a hand-written table could name.
/// </summary>
/// <remarks>
/// <para><b>Why an oracle rather than expected literals.</b> The change is a pure
/// allocation-reduction: the output must not move by a single character, because these bytes go
/// into content streams, <c>/MediaBox</c> arrays and every number this library writes. A table of
/// expected strings tests the cases whoever wrote the table thought of; comparing against the
/// <i>previous implementation</i> tests the property that actually matters — that nothing
/// changed.</para>
///
/// <para><b>The case that makes this non-obvious.</b> The old code was
/// <c>TrimEnd('0').TrimEnd('.')</c>, and folding those into a single "strip trailing zeros" pass
/// would turn <c>"100.000000"</c> into <c>"1"</c>. The separator is what stops the scan, and it
/// only reliably exists because <c>"F6"</c> always emits a point with six digits after it. The
/// oracle below is the literal old expression, so that trap is checked rather than reasoned
/// about.</para>
///
/// <para>The oracle is a copy of deleted code on purpose — the one case where duplicating an
/// implementation is correct, because its whole job is to disagree if the survivor drifts.</para>
/// </remarks>
public class RealFormatTrimEquivalenceTests
{
    /// <summary>The implementation this replaced, verbatim, as the comparison oracle.</summary>
    private static string Previous(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return PdfRealFormat.NonFiniteSubstitute;
        string s = v.ToString("F6", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        return s.Length == 0 || s == "-" ? PdfRealFormat.NonFiniteSubstitute : s;
    }

    /// <summary>
    /// Values chosen for the boundaries the trim actually has: a bare integer (all six decimals
    /// stripped, then the point), a trailing zero inside the fraction, a value with no trim at all,
    /// underflow below half the sixth decimal in both signs, and the <c>"100.000000"</c> shape that
    /// a single-pass zero strip would destroy.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    [InlineData(100.0)]          // "100.000000" — the trap: must stay "100", never "1"
    [InlineData(1000000.0)]
    [InlineData(-100.0)]
    [InlineData(0.5)]
    [InlineData(1.5)]
    [InlineData(1.25)]
    [InlineData(1.234567)]       // no trimming at all — the fast path that returns s unchanged
    [InlineData(1.2345678)]      // rounds at six places
    [InlineData(0.0000001)]      // underflows to zero
    [InlineData(-0.0000001)]     // underflows to "-0", which is deliberately NOT normalised
    [InlineData(-0.0)]
    [InlineData(0.1)]
    [InlineData(612.0)]
    [InlineData(841.89)]
    [InlineData(double.Epsilon)]
    [InlineData(-double.Epsilon)]
    [InlineData(1e-7)]
    [InlineData(123456789.123456)]
    public void MatchesThePreviousImplementation(double v)
        => Assert.Equal(Previous(v), PdfRealFormat.Format(v));

    /// <summary>
    /// The same equivalence over a broad deterministic sweep, which is where a boundary nobody
    /// named would show up. Fixed seed: a failure has to be reproducible.
    /// </summary>
    [Fact]
    public void MatchesThePreviousImplementationAcrossASweep()
    {
        var rng = new Random(20260829);

        for (int i = 0; i < 20000; i++)
        {
            // Spread across magnitudes, because the trim behaves differently either side of the
            // sixth decimal and either side of 1.
            double magnitude = Math.Pow(10, rng.Next(-9, 10));
            double v = (rng.NextDouble() - 0.5) * 2 * magnitude;

            string expected = Previous(v);
            string actual = PdfRealFormat.Format(v);
            if (expected != actual)
                Assert.Fail($"diverged at v={v:R}: previous='{expected}' current='{actual}'");
        }

        // Exact tenths and quarters land on trailing-zero boundaries far more often than random
        // doubles do, so sweep them deliberately rather than hoping the generator hits them.
        for (int n = -2000; n <= 2000; n++)
        {
            foreach (double v in new[] { n / 4.0, n / 10.0, n / 1000.0, n * 1000.0 })
            {
                string expected = Previous(v);
                string actual = PdfRealFormat.Format(v);
                if (expected != actual)
                    Assert.Fail($"diverged at v={v:R}: previous='{expected}' current='{actual}'");
            }
        }
    }

    /// <summary>
    /// A floor for the oracle itself. If <see cref="Previous"/> and the current implementation had
    /// both been broken into returning the same constant, every assertion above would pass while
    /// checking nothing — so pin the handful of outputs whose exact text is load-bearing.
    /// </summary>
    [Fact]
    public void TheOracleProducesRealFormattedOutput()
    {
        Assert.Equal("100", PdfRealFormat.Format(100.0));
        Assert.Equal("1.5", PdfRealFormat.Format(1.5));
        Assert.Equal("0", PdfRealFormat.Format(0.0));
        Assert.Equal("1.234567", PdfRealFormat.Format(1.234567));
        Assert.Equal("-100", PdfRealFormat.Format(-100.0));
    }

    private static string Appended(double v)
    {
        var sb = new System.Text.StringBuilder();
        PdfRealFormat.AppendTo(sb, v);
        return sb.ToString();
    }

    /// <summary>
    /// <see cref="PdfRealFormat.AppendTo"/> writes exactly what <see cref="PdfRealFormat.Format"/>
    /// returns, on the same boundaries.
    /// </summary>
    /// <remarks>
    /// <b>This is the only thing holding the two halves together.</b> <c>AppendTo</c> repeats the
    /// trim against a stack buffer instead of a string — it has to, since materialising the string
    /// is the cost it exists to avoid — so the duplication is deliberate and unguarded by the
    /// compiler. A divergence here is silently wrong coordinates in every content stream the fast
    /// path touches.
    /// </remarks>
    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    [InlineData(100.0)]
    [InlineData(1000000.0)]
    [InlineData(-100.0)]
    [InlineData(0.5)]
    [InlineData(1.25)]
    [InlineData(1.234567)]
    [InlineData(1.2345678)]
    [InlineData(0.0000001)]
    [InlineData(-0.0000001)]
    [InlineData(-0.0)]
    [InlineData(0.1)]
    [InlineData(612.0)]
    [InlineData(841.89)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MaxValue)]        // "F6" runs to ~317 chars: the stack buffer must give up
    [InlineData(double.MinValue)]        //  and fall back, not truncate
    [InlineData(1e17)]
    [InlineData(-1e17)]
    public void AppendToMatchesFormat(double v)
        => Assert.Equal(PdfRealFormat.Format(v), Appended(v));

    /// <summary>The same equivalence over the broad sweep, where an unnamed boundary would show.</summary>
    [Fact]
    public void AppendToMatchesFormatAcrossASweep()
    {
        var rng = new Random(20260829);

        for (int i = 0; i < 20000; i++)
        {
            double magnitude = Math.Pow(10, rng.Next(-9, 10));
            double v = (rng.NextDouble() - 0.5) * 2 * magnitude;
            if (PdfRealFormat.Format(v) != Appended(v))
                Assert.Fail($"AppendTo diverged at v={v:R}: "
                          + $"Format='{PdfRealFormat.Format(v)}' AppendTo='{Appended(v)}'");
        }

        for (int n = -2000; n <= 2000; n++)
        {
            foreach (double v in new[] { n / 4.0, n / 10.0, n / 1000.0, n * 1000.0 })
                if (PdfRealFormat.Format(v) != Appended(v))
                    Assert.Fail($"AppendTo diverged at v={v:R}: "
                              + $"Format='{PdfRealFormat.Format(v)}' AppendTo='{Appended(v)}'");
        }
    }

    /// <summary>
    /// <see cref="PdfRealFormat.AppendTo"/> appends to what is already there rather than replacing
    /// it — the property every call site depends on and none would notice losing, because a builder
    /// that resets itself still produces a plausible-looking number.
    /// </summary>
    [Fact]
    public void AppendToAppendsRatherThanReplaces()
    {
        var sb = new System.Text.StringBuilder("q 1 0 0 1 ");
        PdfRealFormat.AppendTo(sb, 12.5);
        sb.Append(' ');
        PdfRealFormat.AppendTo(sb, 800.0);
        Assert.Equal("q 1 0 0 1 12.5 800", sb.ToString());
    }
}
