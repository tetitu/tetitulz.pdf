// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Base.Operations.Forms;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="InvoiceValueParser"/> on the strings Hebrew invoices actually carry: abbreviations
/// set with gershayim or curly quotes, a VAT line holding a rate and an amount, credit amounts with
/// the minus after the number, dates with a time or a month name beside them.
/// </summary>
public class InvoiceValueParserHebrewTests
{
    // ================================================================
    //  Punctuation and matching normalisation
    // ================================================================

    /// <summary>
    /// The three spellings of one abbreviation — ASCII quote, gershayim, curly quote, two
    /// apostrophes — fold to one, and the apostrophe look-alikes likewise.
    /// </summary>
    [Theory]
    [InlineData("סה\"כ", "סה\"כ")]
    [InlineData("סה״כ", "סה\"כ")]
    [InlineData("סה”כ", "סה\"כ")]
    [InlineData("סה''כ", "סה\"כ")]
    [InlineData("סה׳׳כ", "סה\"כ")]
    [InlineData("מס׳", "מס'")]
    [InlineData("מס’", "מס'")]
    [InlineData("דמי־משלוח", "דמי-משלוח")]
    [InlineData("−5", "-5")]
    public void QuoteApostropheAndHyphenLookAlikesFoldToOneForm(string text, string expected)
    {
        Assert.Equal(expected, InvoiceValueParser.NormalizePunctuation(text));
    }

    /// <summary>An en dash is a range between numbers, not a minus, and is left alone.</summary>
    [Fact]
    public void AnEnDashIsNotFolded()
    {
        Assert.Equal("1–5", InvoiceValueParser.NormalizePunctuation("1–5"));
    }

    /// <summary>
    /// Matching ignores what a page adds to letters: points, a presentation-form letter for a
    /// letter with its point, a ligature, case.
    /// </summary>
    [Theory]
    [InlineData("שָׁלוֹם", "שלום")]        // points removed
    [InlineData("שׁלום", "שלום")]     // shin with shin dot (presentation form) → shin
    [InlineData("ﬁnal", "final")]     // fi ligature
    [InlineData("TOTAL", "total")]
    public void MatchingFormRemovesWhatAPageAddsToLetters(string text, string expected)
    {
        Assert.Equal(expected, InvoiceValueParser.NormalizeForMatching(text));
    }

    // ================================================================
    //  Which number is the amount
    // ================================================================

    /// <summary>A percentage is never the amount: a VAT line's rate is skipped for its amount.</summary>
    [Theory]
    [InlineData("18% 180", 180)]
    [InlineData("(18%) 180", 180)]
    [InlineData("%18 180", 180)]
    public void APercentageIsNeverTheAmount(string text, double expected)
    {
        Assert.True(InvoiceValueParser.TryParseAmount(text, out decimal value, out _));
        Assert.Equal((decimal)expected, value);
    }

    /// <summary>
    /// The number a currency stands beside wins over a count — and the control, the same string
    /// with no currency and no decimals, is refused rather than guessed.
    /// </summary>
    [Fact]
    public void TheNumberBesideACurrencyWinsOverACount()
    {
        Assert.True(InvoiceValueParser.TryParseAmount("2 x ₪ 50", out decimal value, out string currency));
        Assert.Equal(50m, value);
        Assert.Equal("ILS", currency);

        Assert.False(InvoiceValueParser.TryParseAmount("2 x 50", out _, out _));
    }

    /// <summary>With no currency, the number written with two decimals is the amount.</summary>
    [Fact]
    public void TheNumberWithTwoDecimalsWinsWhenNoCurrencyIsNamed()
    {
        Assert.True(InvoiceValueParser.TryParseAmount("3 פריטים 1,234.56", out decimal value, out _));
        Assert.Equal(1234.56m, value);
    }

    /// <summary>Two different amounts that look alike are refused; the same amount twice is not ambiguous.</summary>
    [Fact]
    public void TwoDifferentAmountsAreRefusedButOneAmountTwiceIsRead()
    {
        Assert.False(InvoiceValueParser.TryParseAmount("50.00 = 100.00", out decimal refused, out _));
        Assert.Equal(0m, refused);

        Assert.True(InvoiceValueParser.TryParseAmount("100.00 100.00", out decimal value, out _));
        Assert.Equal(100m, value);
    }

    // ================================================================
    //  Negative amounts as credit invoices print them
    // ================================================================

    /// <summary>
    /// A trailing minus — where the bidi algorithm puts a leading one in a right-to-left line —
    /// and accounting parentheses make an amount negative.
    /// </summary>
    [Theory]
    [InlineData("1,234.56-", -1234.56)]
    [InlineData("1,234.56 -", 1234.56)]       // control: a minus after a space is not the number's
    [InlineData("(1,234.56)", -1234.56)]
    [InlineData("(₪1,234.56)", -1234.56)]
    [InlineData("₪ -1,234.56", -1234.56)]
    [InlineData("1,234.56−", -1234.56)]        // U+2212 MINUS SIGN
    public void ACreditAmountIsNegativeHoweverItsSignIsWritten(string text, double expected)
    {
        Assert.True(InvoiceValueParser.TryParseAmount(text, out decimal value, out _));
        Assert.Equal((decimal)expected, value);
    }

    /// <summary>
    /// A minus between two numbers is neither number's sign, and a count in parentheses with
    /// words beside it is not a negative amount.
    /// </summary>
    [Theory]
    [InlineData("10-20.00", 20)]
    [InlineData("(3 פריטים) 10.00", 10)]
    [InlineData("(3 פריטים)", 3)]          // the count alone: its sign is the answer
    public void AMinusOrParenthesisThatIsNotTheNumbersOwnLeavesItPositive(string text, double expected)
    {
        Assert.True(InvoiceValueParser.TryParseAmount(text, out decimal value, out _));
        Assert.Equal((decimal)expected, value);
    }

    // ================================================================
    //  Separators
    // ================================================================

    /// <summary>
    /// A lone comma groups thousands only in groups of three; one comma before one or two digits
    /// is a decimal comma; several dots in groups of three are thousands. A sentence's full stop
    /// after the number is not part of it.
    /// </summary>
    [Theory]
    [InlineData("1,234", 1234)]
    [InlineData("1,234,567", 1234567)]
    [InlineData("12,50", 12.5)]
    [InlineData("12,5", 12.5)]
    [InlineData("1.234.567", 1234567)]
    [InlineData("12.50", 12.5)]
    [InlineData("1,234.56.", 1234.56)]
    public void SeparatorsAreReadByTheirGrouping(string text, double expected)
    {
        Assert.True(InvoiceValueParser.TryParseAmount(text, out decimal value, out _));
        Assert.Equal((decimal)expected, value);
    }

    /// <summary>A grouping that is neither thousands nor a decimal comma is refused, not guessed.</summary>
    [Theory]
    [InlineData("1,2345")]
    [InlineData("1,23,456")]
    [InlineData("1.23.45")]
    public void AMalformedGroupingIsRefused(string text)
    {
        Assert.False(InvoiceValueParser.TryParseAmount(text, out _, out _));
    }

    // ================================================================
    //  Currency
    // ================================================================

    /// <summary>
    /// The shekel by its sign, its Hebrew abbreviation in any quote, and both its codes — and a
    /// word that merely contains a code names nothing.
    /// </summary>
    [Theory]
    [InlineData("1,234.56 ₪", "ILS")]
    [InlineData("1,234.56 ש\"ח", "ILS")]
    [InlineData("1,234.56 ש״ח", "ILS")]
    [InlineData("1,234.56 ש”ח", "ILS")]
    [InlineData("1,234.56 NIS", "ILS")]
    [InlineData("US$ 1,234.56", "USD")]
    [InlineData("DETAILS 1,234.56", null)]
    public void TheShekelIsRecognisedInEveryForm(string text, string expected)
    {
        Assert.True(InvoiceValueParser.TryParseAmount(text, out decimal value, out string currency));
        Assert.Equal(1234.56m, value);
        Assert.Equal(expected, currency);
    }

    // ================================================================
    //  Finding a date
    // ================================================================

    /// <summary>A date is found whatever stands beside it, in every form invoices print.</summary>
    [Theory]
    [InlineData("10/06/2026 14:32", 2026, 6, 10)]
    [InlineData("תאריך: 10.6.26", 2026, 6, 10)]
    [InlineData("2026-06-10", 2026, 6, 10)]
    [InlineData("15 באוקטובר 2026", 2026, 10, 15)]
    [InlineData("1 במאי 2026", 2026, 5, 1)]
    [InlineData("3 מרס, 2026", 2026, 3, 3)]
    [InlineData("15 Oct 2026", 2026, 10, 15)]
    [InlineData("October 15, 2026", 2026, 10, 15)]
    [InlineData("10/06/2026 10/06/2026", 2026, 6, 10)]
    public void ADateIsFoundInEveryForm(string text, int y, int m, int d)
    {
        Assert.True(InvoiceValueParser.TryFindDate(text, out DateTime date));
        Assert.Equal(new DateTime(y, m, d), date);
    }

    /// <summary>
    /// Two different dates — a billing period — do not say which is meant; an impossible date and
    /// no date at all are refused too.
    /// </summary>
    [Theory]
    [InlineData("01/01/2026 - 31/01/2026")]
    [InlineData("31/02/2026")]
    [InlineData("14:32")]
    [InlineData("")]
    public void TwoDatesAnImpossibleDateAndNoDateAreRefused(string text)
    {
        Assert.False(InvoiceValueParser.TryFindDate(text, out DateTime date));
        Assert.Equal(default, date);
    }

    /// <summary>
    /// A numeric date is read day-first — the 3rd of April, not March the 4th — and the strict
    /// parser still refuses what the finder accepts.
    /// </summary>
    [Fact]
    public void ANumericDateIsDayFirstAndTheStrictParserIsUnchanged()
    {
        Assert.True(InvoiceValueParser.TryFindDate("03/04/2026", out DateTime date));
        Assert.Equal(new DateTime(2026, 4, 3), date);

        Assert.False(InvoiceValueParser.TryParseIsraeliDate("10/06/2026 14:32", out _));
        Assert.True(InvoiceValueParser.TryFindDate("10/06/2026 14:32", out _));
    }
}
