// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// <see cref="UnicodeBidi"/>'s classes are the Unicode Character Database's, from a table
/// <c>tools/unicode-bidi/Generate-BidiClasses.ps1</c> generates. Each test names a class the
/// hand-written classifier before it got wrong, through what the reorder does with it.
/// </summary>
public class UnicodeBidiCharacterDatabaseTests
{
    /// <summary>
    /// The shekel sign is a European terminator (ET), as every currency sign is: a Hebrew line
    /// whose first character is ₪ is a right-to-left paragraph. Typed strong L, the sign decided
    /// the paragraph was left to right. The control: a Latin letter first does decide.
    /// </summary>
    [Theory]
    [InlineData("₪ 2,400.00 שירותי ייעוץ")]
    [InlineData("₹ 100 שירות")]
    [InlineData("₽ 100 שירות")]
    public void ACurrencySignBeforeHebrewLeavesTheParagraphRightToLeft(string text)
    {
        Assert.True(UnicodeBidi.IsRightToLeftParagraph(text, BaseDirection.Auto));
        Assert.False(UnicodeBidi.IsRightToLeftParagraph("A " + text, BaseDirection.Auto));
    }

    /// <summary>
    /// A Hebrew invoice row as a page draws it, left to right — its amount and ₪ at the visual
    /// left — reads back in logical order: the description first, the amount after. With ₪ strong
    /// L the row resolved left to right and came back amount first, as the structured extractor
    /// read every item row of a Chromium-made invoice.
    /// </summary>
    [Fact]
    public void AnInvoiceRowDrawnWithTheShekelAtItsLeftReadsBackInLogicalOrder()
        => Assert.Equal("שירותי אינטגרציה 4 שעה 2,400.00 ₪",
                        UnicodeBidi.Reorder("₪ 2,400.00 העש 4 היצרגטניא יתוריש"));

    /// <summary>
    /// Arabic-Indic digits are Arabic numbers (AN), drawn left to right inside a right-to-left
    /// line. Typed AL, as letters, a number was reversed like a word.
    /// </summary>
    [Fact]
    public void ArabicIndicDigitsKeepTheirOrderInARightToLeftLine()
        => Assert.Equal("١٢٣ ددع", UnicodeBidi.Reorder("عدد ١٢٣"));

    /// <summary>
    /// The no-break space is a common separator (CS): between two digits it joins one number, which
    /// keeps its order in a right-to-left line. Typed WS, it split the number, and the halves
    /// swapped.
    /// </summary>
    [Fact]
    public void ANoBreakSpaceInsideANumberKeepsTheNumberWhole()
    {
        string nbsp = ((char)0xA0).ToString();
        Assert.Equal("1" + nbsp + "234 םוכס", UnicodeBidi.Reorder("סכום 1" + nbsp + "234"));
    }

    /// <summary>
    /// A Hebrew point is a non-spacing mark (NSM), which takes its letter's class: after a digit it
    /// stays with the digit. Typed R, a point after a number was a right-to-left letter of its own
    /// and split the number from its following digits' run.
    /// </summary>
    [Fact]
    public void AHebrewPointAfterADigitStaysWithTheNumber()
    {
        string point = ((char)0x05B0).ToString();   // sheva
        Assert.Equal("12" + point + "3 םוכס", UnicodeBidi.Reorder("סכום 12" + point + "3"));
    }

    /// <summary>
    /// The first code point of a supplementary range — U+10800, where the historic right-to-left
    /// scripts begin — has that range's class, not the one before it, which ends at the code point
    /// before (the control, left to right): an astral character is looked up by binary search over
    /// the generated ranges.
    /// </summary>
    [Fact]
    public void AnAstralRangesFirstCodePointHasTheRangesClass()
    {
        Assert.True(UnicodeBidi.ResolveParagraph(char.ConvertFromUtf32(0x10800), BaseDirection.Auto).IsRightToLeft);
        Assert.False(UnicodeBidi.ResolveParagraph(char.ConvertFromUtf32(0x107FF), BaseDirection.Auto).IsRightToLeft);
    }

    /// <summary>
    /// A paragraph of an astral right-to-left script — two Adlam letters and a number — is right
    /// to left to the paragraph query and is reordered under <see cref="BaseDirection.Auto"/>. The
    /// query and the reorder's has-anything-right-to-left test classified each code unit of a
    /// surrogate pair, as strong L: the query answered left to right, and the reorder handed the
    /// text back unchanged.
    /// </summary>
    [Fact]
    public void AnAstralRightToLeftParagraphIsSeenAndReordered()
    {
        string adlam = char.ConvertFromUtf32(0x1E900) + char.ConvertFromUtf32(0x1E921);

        Assert.True(UnicodeBidi.IsRightToLeftParagraph(adlam + " 12", BaseDirection.Auto));
        Assert.Equal("12 " + char.ConvertFromUtf32(0x1E921) + char.ConvertFromUtf32(0x1E900),
                     UnicodeBidi.Reorder(adlam + " 12"));
    }

    /// <summary>
    /// The bidi table and the script table come from one Character Database: they are regenerated
    /// together.
    /// </summary>
    [Fact]
    public void TheBidiAndScriptTablesShareOneUnicodeVersion()
        => Assert.Equal(UnicodeScripts.UnicodeVersion, UnicodeBidi.BidiClassUnicodeVersion);

    /// <summary>
    /// A paired bracket is one rule N0 pairs, opening or closing; the mirrored characters that pair
    /// nothing — the angle signs, the guillemets — are not, and neither is the colon after one.
    /// </summary>
    [Fact]
    public void APairedBracketIsOneRuleN0Pairs()
    {
        foreach (char c in new[] { '(', ')', '[', ']', '{', '}', (char)0x300C, (char)0x300D })
            Assert.True(UnicodeBidi.IsPairedBracket(c), ((int)c).ToString("X4"));
        foreach (char c in new[] { '<', '>', (char)0x00AB, (char)0x00BB, ':', 'x' })
            Assert.False(UnicodeBidi.IsPairedBracket(c), ((int)c).ToString("X4"));
    }
}
