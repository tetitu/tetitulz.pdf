// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// <see cref="IsraeliIdentifiers"/> — the nine-digit check digit shared by ת"ז, ח"פ and עמותה
/// numbers.
/// </summary>
/// <remarks>
/// <para><b>The expected values are derived, not looked up.</b> Each valid fixture is a chosen
/// prefix plus the one final digit that makes the weighted sum a multiple of ten, and the invalid
/// one beside it is the same prefix with a different final digit — so the pair differs in exactly
/// the thing under test. Quoting a number from elsewhere would test this implementation against
/// whatever produced that number.</para>
///
/// <para><b>123456782 is the public cross-check.</b> It is the value cited everywhere as a valid
/// Israeli identity number, and it is what the derivation above independently produces for the
/// prefix 12345678 — so the algorithm here agrees with the one the outside world uses, which no
/// amount of internal consistency could establish.</para>
/// </remarks>
public sealed class IsraeliIdentifierTests
{
    // 1 2 3 4 5 6 7 8 weighted 1,2,1,2,1,2,1,2 with the >9 fold sums to 38, so the ninth digit
    // must be 2.
    private const string Valid = "123456782";
    private const string WrongCheckDigit = "123456789";

    // 0 2 3 4 5 6 7 8 sums to 37, so 3 completes it — and it is written with its leading zero
    // dropped, which is how these are printed on a form.
    private const string ValidEightDigits = "23456783";

    [Theory]
    [InlineData(Valid)]
    [InlineData(ValidEightDigits)]
    [InlineData("12345678-2")]      // the hyphenated spelling
    [InlineData("123 456 782")]     // spaced
    [InlineData("18")]              // 000000018: short, and valid once padded
    public void ValidNumbersAreAccepted(string value) => Assert.True(IsraeliIdentifiers.IsValid(value));

    [Theory]
    [InlineData(WrongCheckDigit)]
    [InlineData("123456780")]
    [InlineData("023456782")]       // the eight-digit fixture with the WRONG leading digit
    public void WrongCheckDigitIsRejected(string value)
        => Assert.False(IsraeliIdentifiers.IsValid(value));

    /// <summary>
    /// Every one of the ten possible final digits is tried, and exactly one is accepted.
    /// </summary>
    /// <remarks>
    /// This is the assertion that pins the check digit as a <i>check</i> digit. A validator that
    /// accepted two of the ten — or all ten — would satisfy every other test in this file, since
    /// each of those only ever offers one candidate per prefix.
    /// </remarks>
    [Fact]
    public void ExactlyOneFinalDigitCompletesAPrefix()
    {
        int accepted = 0;
        for (char d = '0'; d <= '9'; d++)
            if (IsraeliIdentifiers.IsValid("12345678" + d)) accepted++;

        Assert.Equal(1, accepted);
    }

    /// <summary>
    /// The weights are positional, so the same digits in a different order are a different number.
    /// </summary>
    /// <remarks>
    /// A validator that merely summed the digits — the shape this algorithm superficially
    /// resembles — would accept every permutation, and every other test here would still pass.
    /// </remarks>
    [Fact]
    public void ThePositionOfEachDigitMatters()
    {
        Assert.True(IsraeliIdentifiers.IsValid(Valid));
        Assert.False(IsraeliIdentifiers.IsValid("213456782"));   // first two swapped
    }

    /// <summary>
    /// Input that is not one of these numbers is rejected rather than salvaged.
    /// </summary>
    /// <remarks>
    /// <b>Two of these cases exist because a mutation survived without them</b>, and both survived
    /// the same way: the earlier fixtures were rejected by the check digit rather than by the rule
    /// under test, so removing the rule changed nothing. A ten-digit number is only interesting if
    /// its first nine are VALID, and a labelled number only if the digits inside the label are.
    /// Each pair below is the weak fixture and the one that discriminates.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1234567890")]        // ten digits, and its first nine are invalid anyway
    [InlineData("1234567820")]        // ten digits whose FIRST NINE are valid: only the cap rejects it
    [InlineData("12345678A")]         // a letter, and these digits are invalid anyway
    [InlineData("ID123456782")]       // a label around a VALID number: only the rule rejects it
    [InlineData("\u05EA.\u05D6. 123456782")]   // the same, labelled in Hebrew, as a form prints it
    [InlineData("- -")]               // separators only, no digits
    public void MalformedInputIsRejected(string value)
        => Assert.False(IsraeliIdentifiers.IsValid(value));

    /// <summary>
    /// Normalising reports the shape and pads it; it says nothing about the check digit.
    /// </summary>
    [Fact]
    public void NormalisingPadsToNineAndIsNotValidation()
    {
        Assert.True(IsraeliIdentifiers.TryNormalize("12345678-9", out string normalized));
        Assert.Equal("123456789", normalized);
        Assert.False(IsraeliIdentifiers.IsValid("12345678-9"));

        Assert.True(IsraeliIdentifiers.TryNormalize("18", out string padded));
        Assert.Equal("000000018", padded);
    }

    /// <summary>
    /// The invisible bidi marks a number picks up inside Hebrew text are ignored rather than
    /// making it unreadable.
    /// </summary>
    /// <remarks>
    /// <para>These characters are in the string because of how the text was laid out, not because
    /// of what it says - the same reason <c>InvoiceValueParser.NormalizeDigits</c> strips the
    /// identical set. A caller pulling a number out of an RTL paragraph gets them whether they want
    /// them or not.</para>
    ///
    /// <para><b>Written as escapes, and that is not style.</b> Every character in this theory is
    /// invisible. As a literal, an editor that stripped one - or a careless edit that deleted it -
    /// would turn the case into a duplicate of the plain one, and the test would keep passing while
    /// asserting nothing. An escape cannot be lost without the compiler noticing.</para>
    /// </remarks>
    [Theory]
    [InlineData("\u200E123456782")]              // LRM before
    [InlineData("123456782\u200F")]              // RLM after
    [InlineData("\u2066123456782\u2069")]        // LRI ... PDI
    [InlineData("1234\u202B56782\u202C")]        // an embedding opened mid-number
    [InlineData("\uFEFF123456782")]              // BOM / zero-width no-break space
    [InlineData("123\u00A0456\u00A0782")]        // no-break spaces
    [InlineData("\u061C123456782")]              // Arabic letter mark
    [InlineData("\u202E123456782\u202C")]        // RLO, the top of the embedding/override range
    [InlineData("\u202A123456782\u202D")]        // LRE ... LRO, its bottom and next-to-top
    public void BidiMarksAndNoBreakSpacesAreIgnored(string value)
        => Assert.True(IsraeliIdentifiers.IsValid(value));

    /// <summary>
    /// Nothing here distinguishes a ת"ז from a ח"פ: the leading digit is registration practice, not
    /// part of the number.
    /// </summary>
    /// <remarks>
    /// Asserted so a later reader does not "improve" this by rejecting a leading 5 for an identity
    /// number, or requiring one for a company number. Both are legal nine-digit values and the
    /// validator must accept both; a caller that needs the distinction has to get it from the label
    /// beside the number.
    /// </remarks>
    [Fact]
    public void CompanyAndIdentityNumbersShareOneValidator()
    {
        // 5 1 2 3 4 5 6 7 weighted 1,2,1,2,1,2,1,2 sums to 5+2+2+6+4+1+6+5 = 31, so 9 completes it.
        Assert.True(IsraeliIdentifiers.IsValid("512345679"));
        Assert.True(IsraeliIdentifiers.IsValid(Valid));
    }
}
