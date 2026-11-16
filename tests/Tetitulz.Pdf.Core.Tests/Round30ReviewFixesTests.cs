// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests;

/// <summary>
/// Regression tests for the code-review remediation round 30,
/// Core-side fixes.
/// </summary>
public class Round30ReviewFixesTests
{
    // §68 Low — PdfNumber flagged any whole-valued double as IsInteger, then the
    // integer-formatting / IntValue / LongValue casts truncated values outside
    // long range (e.g. 1e30 → long.MinValue). A double that does not fit long
    // must NOT be treated as an integer.
    [Fact]
    public void PdfNumber_LargeWholeDouble_NotFlaggedInteger()
    {
        var n = new PdfNumber(1e30);
        Assert.False(n.IsInteger);
        // ToString must not emit a wrapped/garbage integer; the fixed-point path
        // is used instead. The value round-trips through DoubleValue.
        Assert.Equal(1e30, n.DoubleValue);
        string s = n.ToString();
        Assert.False(s.Contains("-9223372036854775808")); // not the wrapped long.MinValue
    }

    [Fact]
    public void PdfNumber_InRangeWholeDouble_StillInteger()
    {
        var n = new PdfNumber(42.0);
        Assert.True(n.IsInteger);
        Assert.Equal("42", n.ToString());
    }

    [Fact]
    public void PdfNumber_ExplicitLong_IsInteger()
    {
        var n = new PdfNumber(123456789L);
        Assert.True(n.IsInteger);
        Assert.Equal("123456789", n.ToString());
    }
}
