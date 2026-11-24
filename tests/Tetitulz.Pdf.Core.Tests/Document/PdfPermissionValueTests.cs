// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// Reading <c>/P</c> (§7.6.3, Table 22) — the encryption dictionary's 32-bit permission bitmask.
///
/// <para><b>Four readers, all wrong the same way.</b> <c>/P</c> is defined signed, but producers
/// write the unsigned spelling of the same bit pattern, and every reader went through
/// <c>PdfDictionary.GetInteger</c> — whose <c>(int)</c> cast on a <c>double</c> <b>saturates</b> to
/// <c>int.MaxValue</c> on .NET Core 3.0+ rather than truncating. A bitmask withholding most
/// permissions therefore read as one granting all of them.</para>
///
/// <para>The assertions are on the <b>bit pattern</b>, not on "a number came back": saturation
/// returns a perfectly plausible integer, which is why four separate call sites accepted it.</para>
/// </summary>
public sealed class PdfPermissionValueTests
{
    /// <summary>
    /// The case that breaks everything: <c>4294963392</c> is <c>0xFFFFF0C0</c>, which as a signed
    /// int is <c>-3904</c> — print and extraction withheld. Through <c>GetInteger</c> it is
    /// <c>int.MaxValue</c>: every bit set.
    /// </summary>
    [Fact]
    public void UnsignedSpelling_ReadsAsTheSameBitPatternAsTheSignedOne()
    {
        Assert.Equal(-3904, Read(4294963392L));
        Assert.Equal(-3904, Read(-3904L));
    }

    /// <summary>
    /// Stated as the defect, so the test says what would go wrong: an <c>(int)</c> cast
    /// saturates, and saturation sets every permission bit.
    /// </summary>
    [Fact]
    public void TheOldRoute_Saturated_AndThatGrantedEveryPermission()
    {
        var enc = new PdfDictionary();
        enc.Set(PdfName.P, new PdfNumber(4294963392L));

        Assert.Equal(int.MaxValue, enc.GetInteger(PdfName.P));   // the defect, pinned
        Assert.NotEqual(enc.GetInteger(PdfName.P), PdfPermissionValue.Read(enc));

        // Bit 10 (0x200) is "extract for accessibility"; bit 3 (0x4) is "print".
        Assert.Equal(0x200, int.MaxValue & 0x200);               // saturated: permitted
        Assert.Equal(0, PdfPermissionValue.Read(enc).Value & 0x4); // real value: print withheld
    }

    [Theory]
    [InlineData(-1L, -1)]                    // everything granted, signed spelling
    [InlineData(4294967295L, -1)]            // …and its unsigned spelling
    [InlineData(-3904L, -3904)]
    [InlineData(0L, 0)]
    [InlineData(-4L, -4)]                    // the forged "grant everything" value
    [InlineData(4294967292L, -4)]
    public void LowThirtyTwoBits_AreTheAnswerForEitherSpelling(long written, int expected)
        => Assert.Equal(expected, Read(written));

    /// <summary>
    /// Absent, indirect or non-numeric is a <b>third</b> answer. Defaulting it to 0 would make
    /// every permission look withheld and would make the <c>/Perms</c> comparison announce
    /// tampering on a file that simply did not say.
    /// </summary>
    [Fact]
    public void UnreadablePermissionEntry_IsNullRatherThanZero()
    {
        Assert.Null(PdfPermissionValue.Read(new PdfDictionary()));
        Assert.Null(PdfPermissionValue.Read(null));

        var indirect = new PdfDictionary();
        indirect.Set(PdfName.P, new PdfReference(7, 0));
        Assert.Null(PdfPermissionValue.Read(indirect));

        var wrongType = new PdfDictionary();
        wrongType.Set(PdfName.P, new PdfName("Nonsense"));
        Assert.Null(PdfPermissionValue.Read(wrongType));
    }

    /// <summary>
    /// <c>FromNumber</c> is the overload for a caller that resolved the reference itself — the
    /// PDF/UA checker does, and <c>Read</c> deliberately does not, having no document to resolve
    /// against.
    /// </summary>
    [Fact]
    public void FromNumber_AppliesTheSameRuleToAnAlreadyResolvedValue()
    {
        Assert.Equal(-3904, PdfPermissionValue.FromNumber(new PdfNumber(4294963392L)));
        Assert.Null(PdfPermissionValue.FromNumber(null));
        Assert.Null(PdfPermissionValue.FromNumber(new PdfName("P")));
    }

    private static int? Read(long written)
    {
        var enc = new PdfDictionary();
        enc.Set(PdfName.P, new PdfNumber(written));
        return PdfPermissionValue.Read(enc);
    }
}
