// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Base.Operations.Forms;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="InvoiceValueParser"/>'s decisions about what a value string means — the ones a
/// mutation sweep found nothing reached.
/// </summary>
/// <remarks>
/// These parsers exist for text pulled off an <b>RTL invoice</b>, where a number arrives wrapped in
/// invisible bidi controls the producer laid down to keep it reading left-to-right inside a Hebrew
/// line. Every decision below is about a string a Latin-only fixture never produces.
/// </remarks>
public class InvoiceValueParserDecisionTests
{
    /// <summary>
    /// Every bidi control is stripped, not just the first. A producer wraps a number in an
    /// isolate (U+2066–U+2069) or an embedding (U+202A–U+202E) as readily as in an LRM, and one
    /// left in the middle of the digits stops the number parsing at all.
    /// </summary>
    [Theory]
    [InlineData("‎1,234.56‏")]     // LRM / RLM
    [InlineData("⁦" + "1,234.56" + "⁩")]   // LRI / PDI
    [InlineData("‭" + "1,234.56" + "‬")]   // LRO / PDF
    [InlineData("؜" + "1,234.56")]      // Arabic letter mark
    [InlineData("﻿" + "1,234.56")]      // BOM / ZWNBSP
    public void EveryBidiControlIsStrippedFromAnAmount(string text)
    {
        Assert.True(InvoiceValueParser.TryParseAmount(text, out decimal value, out _));
        Assert.Equal(1234.56m, value);
    }

    /// <summary>
    /// A non-breaking space becomes an ordinary one. Producers use it between a currency symbol and
    /// its number, and left alone it lands inside the matched number text.
    /// </summary>
    [Fact]
    public void ANonBreakingSpaceBecomesAnOrdinarySpace()
    {
        Assert.Equal("1 234", InvoiceValueParser.NormalizeDigits("1 234"));
    }

    /// <summary>
    /// A currency is recognised from its ISO code as well as its symbol — an invoice that writes
    /// "ILS" rather than ₪ is naming the same currency.
    /// </summary>
    [Theory]
    [InlineData("1,234.56 ILS", "ILS")]
    [InlineData("1,234.56 USD", "USD")]
    [InlineData("1,234.56 EUR", "EUR")]
    [InlineData("1,234.56", null)]
    public void ACurrencyCodeIsRecognisedAsWellAsItsSymbol(string text, string expected)
    {
        Assert.True(InvoiceValueParser.TryParseAmount(text, out _, out string currency));
        Assert.Equal(expected, currency);
    }

    /// <summary>
    /// With both separators present, the <b>right-most</b> is the decimal point — which is what
    /// tells the European <c>1.234,56</c> from the Anglo <c>1,234.56</c>. Assuming either one
    /// misreads the other by a factor of a hundred.
    /// </summary>
    [Theory]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("1.234.567,89", 1234567.89)]
    public void TheRightmostSeparatorIsTheDecimalPoint(string text, double expected)
    {
        Assert.True(InvoiceValueParser.TryParseAmount(text, out decimal value, out _));
        Assert.Equal((decimal)expected, value);
    }

    /// <summary>A leading minus is part of the number — a credit note is not a positive amount.</summary>
    [Theory]
    [InlineData("-1,234.56", -1234.56)]
    [InlineData("+1,234.56", 1234.56)]
    public void ASignedAmountKeepsItsSign(string text, double expected)
    {
        Assert.True(InvoiceValueParser.TryParseAmount(text, out decimal value, out _));
        Assert.Equal((decimal)expected, value);
    }

    /// <summary>
    /// A date is trimmed before it is matched: extraction hands back the cell's text, and a cell
    /// has spaces around its content more often than not.
    /// </summary>
    [Theory]
    [InlineData("  20/09/2026  ")]
    [InlineData("20.09.2026")]
    [InlineData("20-09-2026")]
    public void ADateIsTrimmedAndAcceptsEverySeparator(string text)
    {
        Assert.True(InvoiceValueParser.TryParseIsraeliDate(text, out DateTime date));
        Assert.Equal(new DateTime(2026, 9, 20), date);
    }

    /// <summary>A fractional percentage keeps its fraction.</summary>
    [Theory]
    [InlineData("17%", 17)]
    [InlineData("17.5 %", 17.5)]
    public void AFractionalPercentageIsNotTruncated(string text, double expected)
    {
        Assert.True(InvoiceValueParser.TryParsePercent(text, out decimal percent));
        Assert.Equal((decimal)expected, percent);
    }

    /// <summary>
    /// A rate whose sign a right-to-left producer stores first, a space apart — <c>% 18.00</c>, as a
    /// fuel retailer's invoices print it beside the subtotal — is a rate, never the amount; the
    /// number after it is. A sign a number before it owns (<c>18% 180.00</c>) marks that number
    /// alone.
    /// </summary>
    [Theory]
    [InlineData("% 18.00 1,862.40", 1862.40)]
    [InlineData("(% 18.0 ): 2,045.62", 2045.62)]
    [InlineData("%  17 1,000.00", 1000.00)]
    [InlineData("18% 180.00", 180.00)]
    [InlineData("18 % 180.00", 180.00)]
    [InlineData("18%12,345.60", 12345.60)]
    [InlineData("%18 180.00", 180.00)]
    public void ASignWrittenBeforeItsRateSpacesApartStillMarksTheRate(string text, double expected)
    {
        Assert.True(InvoiceValueParser.TryParseAmount(text, out decimal value, out _));
        Assert.Equal((decimal)expected, value);
    }

    /// <summary>
    /// A rate standing alone, its sign first, holds no amount: the field reads on to the amount
    /// rather than stopping at the rate. The same string is the rate the VAT's percentage reader
    /// takes.
    /// </summary>
    [Fact]
    public void ARateAloneWithItsSignFirstIsNoAmount()
    {
        Assert.False(InvoiceValueParser.TryParseAmount("% 18.00", out _, out _));
        Assert.True(InvoiceValueParser.TryParsePercent("% 18.00", out decimal rate));
        Assert.Equal(18m, rate);
        // The potency control: the same number without the sign is an amount.
        Assert.True(InvoiceValueParser.TryParseAmount("18.00", out decimal plain, out _));
        Assert.Equal(18m, plain);
    }

    /// <summary>Input with no number at all fails closed rather than guessing zero.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no digits here")]
    public void InputWithNoNumberIsRefused(string text)
    {
        Assert.False(InvoiceValueParser.TryParseAmount(text, out decimal value, out _));
        Assert.Equal(0m, value);
    }

    /// <summary>
    /// An amount a value is — no letter after its number but a currency's, none touching it but a
    /// currency's — against one a sentence or a code only holds. Where the value rule takes a
    /// string it takes what <see cref="InvoiceValueParser.TryParseAmount"/> takes; where it refuses
    /// one the plain parser still finds a number, so the refusal is the rule's.
    /// </summary>
    [Theory]
    [InlineData("1,180.00", true)]
    [InlineData("1,180.00 ש\"ח", true)]
    [InlineData("1,180.00ש\"ח", true)]
    [InlineData("(ש\"ח) 125.47", true)]
    [InlineData("18% 1,800.00", true)]
    [InlineData("1,870.20- 18%", true)]
    [InlineData("₪1,180.00", true)]
    [InlineData("NIS 42", true)]
    [InlineData("NIS42", true)]
    [InlineData("1,000.00 GBP", true)]
    [InlineData("1,000.00 דולר", true)]
    [InlineData("1,180.00 בשקלים", true)]
    [InlineData("1,180.00 שח", true)]
    [InlineData("1,000.00 יורו", true)]
    [InlineData("הפיצוי בתוך 30 ימים, ישלם", false)]
    [InlineData("17. מקום שיפוט", false)]
    [InlineData("PCN874", false)]
    [InlineData("37.2תביעה", false)]
    [InlineData("4,250 ש\"ח ומעלה", false)]
    [InlineData("INV1001", false)]
    [InlineData("לחיוב ......: 24,518.40", true)]
    [InlineData("לזיכוי ₪ 555.19-", true)]
    [InlineData("בש\"ח כולל 1,180.00", true)]
    [InlineData("המחיר הסופי עבור ליטר סולר ללקוח הינו 6.39 ₪", false)]
    [InlineData("יעמוד על סך 23.98 ש\"ח", false)]
    public void AnAmountValueEndsAtItsAmount(string text, bool isValue)
    {
        Assert.True(InvoiceValueParser.TryParseAmount(text, out decimal plain, out string plainCurrency));

        Assert.Equal(isValue, InvoiceValueParser.TryParseAmountValue(text, out decimal value, out string currency));
        if (!isValue) return;
        Assert.Equal(plain, value);
        Assert.Equal(plainCurrency, currency);
    }

    /// <summary>
    /// The currencies' Hebrew names, with a prefix letter or without, and the other common codes
    /// name the currency — the one vocabulary the amount and its value rule share. A code is a whole
    /// word: "VAT" is none. A code apart from the number still names it.
    /// </summary>
    [Theory]
    [InlineData("1,180.00 שקלים", "ILS")]
    [InlineData("בשקלים 1,180.00", "ILS")]
    [InlineData("1,180.00 שח", "ILS")]
    [InlineData("1,000.00 דולרים", "USD")]
    [InlineData("1,000.00 אירו", "EUR")]
    [InlineData("1,000.00 EURO", "EUR")]
    [InlineData("1,000.00 CHF", "CHF")]
    [InlineData("GBP: 1,000.00", "GBP")]
    [InlineData("Total VAT 1,180.00", null)]
    public void AHebrewNameOrAnotherCodeNamesTheCurrency(string text, string? expected)
    {
        Assert.True(InvoiceValueParser.TryParseAmount(text, out _, out string currency));
        Assert.Equal(expected, currency);
    }
}
