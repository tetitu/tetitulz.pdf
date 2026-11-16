// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Objects;

/// <summary>
/// <see cref="PdfNumber.FlagWord"/>: the one reading of a PDF bit field — the encryption
/// dictionary's <c>/P</c> and an annotation's <c>/F</c>.
/// </summary>
public class FlagWordTests
{
    [Theory]
    [InlineData(4L, 4)]
    [InlineData(-3904L, -3904)]
    [InlineData(4294963392L, -3904)]           // /P's unsigned spelling of -3904: the low 32 bits agree
    [InlineData(2147483648L, int.MinValue)]    // bit 32 alone — no named flag
    [InlineData(2147483652L, int.MinValue | 4)] // bit 32 plus Print
    public void AnIntegerIsReadAsItsLow32Bits(long value, int expected)
    {
        Assert.Equal(expected, new PdfNumber(value).FlagWord);
    }

    /// <summary>
    /// The case that made readers disagree: <see cref="PdfNumber.IntValue"/> saturates bit 32 to
    /// <c>int.MaxValue</c>, which is every named flag. The flag word keeps bit 32 and nothing else.
    /// </summary>
    [Fact]
    public void Bit32IsNotReadAsEveryFlag()
    {
        var n = new PdfNumber(2147483648L);

        Assert.Equal(int.MaxValue, n.IntValue);              // the saturation this replaces
        Assert.Equal(int.MinValue, n.FlagWord);
        Assert.False(PdfAnnotationFlags.IsHiddenOnScreen(n.FlagWord!.Value));
    }

    /// <summary>A whole number written as a real is still an integer; one with a fraction, or one outside <c>long</c>, is not a flag word.</summary>
    [Theory]
    [InlineData(4.0, 4)]
    [InlineData(6.5, null)]
    [InlineData(1e30, null)]
    [InlineData(double.NaN, null)]
    public void ARealIsAFlagWordOnlyWhenItIsAWholeNumberInRange(double value, int? expected)
    {
        Assert.Equal(expected, new PdfNumber(value).FlagWord);
    }

    /// <summary>
    /// <c>/P</c> goes through the same rule. <c>1e30</c> used to be cast unchecked and saturate to
    /// all ones — every permission granted, from a number no producer writes.
    /// </summary>
    [Fact]
    public void APermissionValueThatIsNotAnIntegerIsUnreadable_NotEveryPermission()
    {
        Assert.Null(PdfPermissionValue.FromNumber(new PdfNumber(1e30)));
        Assert.Equal(-3904, PdfPermissionValue.FromNumber(new PdfNumber(4294963392L)));   // the control
    }

    [Fact]
    public void AnAnnotationFlagThatIsNotANumberIsNull()
    {
        Assert.Null(PdfAnnotationFlags.FromNumber(new PdfName("Print")));
        Assert.Null(PdfAnnotationFlags.FromNumber(null));
        Assert.Equal(4, PdfAnnotationFlags.FromNumber(new PdfNumber(4)));                  // the control
    }
}
