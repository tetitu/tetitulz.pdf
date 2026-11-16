// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="InvoiceValueParser"/> on what producers and hostile files send beside the plain
/// cases: signs a currency stands between, typographic group spaces, digits of another script,
/// invisible marks, characters normalisation refuses, and runs long enough to make a backtracking
/// expression hang. Invisible and look-alike characters are written as code points.
/// </summary>
public class InvoiceValueParserInputTests
{
    private const char ThinSpace = (char)0x2009, NarrowNoBreakSpace = (char)0x202F, FigureSpace = (char)0x2007;
    private const char NoBreakSpace = (char)0x00A0;

    private static decimal? Amount(string s) => InvoiceValueParser.TryParseAmount(s, out decimal v, out _) ? v : (decimal?)null;

    // ================================================================
    //  Signs
    // ================================================================

    /// <summary>
    /// A leading minus with a currency between it and the number is the number's sign, and so is
    /// one after a currency code; a minus a space separates from the currency is not, as after the
    /// number; a hyphen after a Hebrew prefix letter ("in 1,170") or inside an invoice code is not.
    /// </summary>
    [Theory]
    [InlineData("-₪1,234.56", -1234.56)]
    [InlineData("-₪ 1,234.56", -1234.56)]
    [InlineData("- ₪ 1,234.56", 1234.56)]
    [InlineData("-ILS 500.00", -500)]
    [InlineData("ILS-500.00", -500)]
    [InlineData("ב-1,170.00", 1170)]
    [InlineData("INV-1170", 1170)]
    public void ALeadingMinusIsTheSignWhereNothingButACurrencyStandsBetween(string text, double expected)
    {
        Assert.Equal((decimal)expected, Amount(text));
    }

    // ================================================================
    //  Grouping and digits
    // ================================================================

    /// <summary>
    /// A thin, narrow no-break or figure space before a group of three is grouping; a plain or
    /// no-break space between one to three digits and a three-digit number could be grouping or two
    /// numbers, and is refused.
    /// </summary>
    [Fact]
    public void ATypographicGroupSpaceIsGroupingAndAPlainSpaceIsRefused()
    {
        foreach (char space in new[] { ThinSpace, NarrowNoBreakSpace, FigureSpace })
            Assert.Equal(1234.56m, Amount("1" + space + "234.56"));

        Assert.Null(Amount("1 234.56"));
        Assert.Null(Amount("1" + NoBreakSpace + "234.56"));
        Assert.Equal(1234.56m, Amount("1234.56"));
    }

    /// <summary>
    /// "0,500" is half — a thousands group never starts with zero — and a run of such groups is
    /// neither a decimal nor thousands, and is refused.
    /// </summary>
    [Fact]
    public void AGroupStartingWithZeroIsADecimalComma()
    {
        Assert.Equal(0.5m, Amount("0,500"));
        Assert.Null(Amount("0,500,000"));
        Assert.Equal(1500m, Amount("1,500"));
    }

    /// <summary>
    /// Arabic-Indic and extended Arabic-Indic digits are digits: an invoice printed in them yields
    /// its amount, not another number on the line.
    /// </summary>
    [Fact]
    public void ArabicIndicDigitsAreRead()
    {
        string arabic = new string(new[] { (char)0x0661, (char)0x0662, (char)0x0663, (char)0x0664 }) + "." +
                        new string(new[] { (char)0x0665, (char)0x0660 });
        string extended = new string(new[] { (char)0x06F1, (char)0x06F2, (char)0x06F3 });

        Assert.Equal(1234.5m, Amount(arabic));
        Assert.Equal(123m, Amount(extended));
    }

    // ================================================================
    //  Percentages and currencies
    // ================================================================

    /// <summary>
    /// A percentage is read in either order — "%17" is how some right-to-left producers store
    /// "17%" — and two different percentages are refused, one written twice is not.
    /// </summary>
    [Theory]
    [InlineData("17%", 17)]
    [InlineData("%17", 17)]
    [InlineData("מע\"מ %18 180.00", 18)]
    [InlineData("18% 18%", 18)]
    public void APercentageIsReadInEitherOrder(string text, double expected)
    {
        Assert.True(InvoiceValueParser.TryParsePercent(text, out decimal p));
        Assert.Equal((decimal)expected, p);
    }

    [Fact]
    public void TwoDifferentPercentagesAreRefused()
    {
        Assert.False(InvoiceValueParser.TryParsePercent("%17 %18", out _));
    }

    /// <summary>
    /// The shekel's abbreviation with a Hebrew prefix letter — "in shekels", "to shekels" — names
    /// the shekel; with another letter before it, it is part of a word and names nothing.
    /// </summary>
    [Theory]
    [InlineData("1,234.56 בש\"ח", "ILS")]
    [InlineData("1,234.56 לש\"ח", "ILS")]
    [InlineData("1,234.56 גש\"ח", null)]
    public void TheShekelWithAPrefixLetterIsTheShekel(string text, string expected)
    {
        Assert.True(InvoiceValueParser.TryParseAmount(text, out decimal v, out string currency));
        Assert.Equal(1234.56m, v);
        Assert.Equal(expected, currency);
    }

    // ================================================================
    //  Dates
    // ================================================================

    /// <summary>
    /// An English month is a whole word, written in full, abbreviated or abbreviated with a full
    /// stop; a longer word that starts like one — "Marketing", "Decorations" — is no month.
    /// </summary>
    [Theory]
    [InlineData("15 Sept. 2026", true)]
    [InlineData("Sep 15, 2026", true)]
    [InlineData("15 September 2026", true)]
    [InlineData("15 Marketing 2026", false)]
    [InlineData("Decorations 15, 2026", false)]
    public void AnEnglishMonthIsAWholeWord(string text, bool found)
    {
        Assert.Equal(found, InvoiceValueParser.TryFindDate(text, out DateTime date));
        if (found) Assert.Equal(new DateTime(2026, 9, 15), date);
    }

    // ================================================================
    //  Invisible marks and characters normalisation refuses
    // ================================================================

    /// <summary>
    /// One list of invisible layout marks — the marks, the embeddings, overrides and isolates, the
    /// BOM — and every reader of a number skips each: the value parser and the identifier check
    /// alike. A visible character in the same place is not skipped, the control.
    /// </summary>
    [Fact]
    public void EveryReaderOfANumberSkipsTheSameInvisibleMarks()
    {
        int[] marks = { 0x200E, 0x200F, 0x061C, 0xFEFF, 0x202A, 0x202B, 0x202C, 0x202D, 0x202E, 0x2066, 0x2067, 0x2068, 0x2069 };
        foreach (int code in marks)
        {
            char mark = (char)code;
            Assert.True(UnicodeBidi.IsInvisibleLayoutMark(mark));
            Assert.Equal("1234", InvoiceValueParser.NormalizeDigits("12" + mark + "34"));
            Assert.Equal(1234m, Amount("12" + mark + "34"));
            Assert.True(IsraeliIdentifiers.IsValid("51428655" + mark + "8"));
        }

        Assert.False(UnicodeBidi.IsInvisibleLayoutMark('x'));
        Assert.False(IsraeliIdentifiers.IsValid("51428655x8"));
    }

    /// <summary>
    /// A character normalisation refuses — U+FFFE, which a font's map can produce, or a lone
    /// surrogate — is kept as written rather than aborting the document's extraction.
    /// </summary>
    [Fact]
    public void ACharacterNormalisationRefusesIsKept()
    {
        string withNonCharacter = "ab" + (char)0xFFFE + "c";
        string withLoneSurrogate = "ab" + (char)0xD800 + "c";

        Assert.Equal(withNonCharacter, InvoiceValueParser.NormalizeForMatching(withNonCharacter));
        Assert.Equal(withLoneSurrogate, InvoiceValueParser.NormalizeForMatching(withLoneSurrogate));
        Assert.Equal("ab" + (char)0xFFFE + "c", InvoiceValueParser.LabelKey("AB" + (char)0xFFFE + "C"));
    }

    /// <summary>
    /// A label key drops every whitespace character and colon the folding leaves — a thin space, a
    /// full-width colon — as it drops the plain ones, and an astral letter is folded as a letter
    /// in the basic plane is.
    /// </summary>
    [Fact]
    public void ALabelKeyFoldsWhatThePlainFormsFold()
    {
        Assert.Equal(InvoiceValueParser.LabelKey("Total Due:"),
                     InvoiceValueParser.LabelKey("Total" + ThinSpace + "Due" + (char)0xFF1A));

        // Whitespace the folding leaves as it is — a tab, a line separator — goes too.
        Assert.Equal(InvoiceValueParser.LabelKey("Total Due"), InvoiceValueParser.LabelKey("Total" + (char)9 + "Due"));
        Assert.Equal(InvoiceValueParser.LabelKey("Total Due"), InvoiceValueParser.LabelKey("Total" + (char)0x2028 + "Due"));

        string boldTotal = string.Concat(new[] { 0x1D413, 0x1D428, 0x1D42D, 0x1D41A, 0x1D425 }.Select(char.ConvertFromUtf32));
        Assert.Equal("total", InvoiceValueParser.LabelKey(boldTotal));
    }

    // ================================================================
    //  Hostile runs
    // ================================================================

    /// <summary>
    /// A digit run tens of thousands long, and a million blanks after a month's name: each was
    /// matched by an expression that backtracked over the run quadratically — the blanks split
    /// every way between two optional runs of spaces, minutes of work. Both are answered — a number
    /// too long for a decimal is no percentage, a month with no year is no date.
    /// </summary>
    [Fact]
    public void AdversarialLongRunsAreAnsweredNotBacktrackedOver()
    {
        Assert.False(InvoiceValueParser.TryParsePercent(new string('1', 40000) + "%", out _));
        Assert.False(InvoiceValueParser.TryFindDate("15 ביוני" + new string(' ', 1000000) + "x", out _));
        Assert.True(InvoiceValueParser.TryFindDate("15 ביוני 2026", out _));
    }
}
