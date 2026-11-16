// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Forms;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>Tests for <see cref="InvoiceValueParser"/> (amounts, percentages, dates, digit normalisation).</summary>
public class InvoiceValueParserTests
{
    private static readonly string Shekel = ((char)0x20AA).ToString();

    /// <summary>
    /// The patterns run under a cap on the value (CLAUDE.md's rule for a regular expression over a
    /// file's text): at <see cref="InvoiceValueParser.MaxValueLength"/> an amount, a percentage, a date
    /// and an allocation number are read; one character past it, none is — the field is not a value.
    /// </summary>
    [Fact]
    public void AValuePastTheCap_IsNotRead_AndOneAtItIs()
    {
        static string Padded(string value, int length) => new string(' ', length - value.Length) + value;
        int cap = InvoiceValueParser.MaxValueLength;

        Assert.True(InvoiceValueParser.TryParseAmount(Padded("1,234.56", cap), out decimal amount, out _));
        Assert.Equal(1234.56m, amount);
        Assert.False(InvoiceValueParser.TryParseAmount(Padded("1,234.56", cap + 1), out _, out _));

        Assert.True(InvoiceValueParser.TryParsePercent(Padded("17%", cap), out _));
        Assert.False(InvoiceValueParser.TryParsePercent(Padded("17%", cap + 1), out _));

        Assert.True(InvoiceValueParser.TryFindDate(Padded("10/06/2026", cap), out _));
        Assert.False(InvoiceValueParser.TryFindDate(Padded("10/06/2026", cap + 1), out _));

        Assert.Equal("123456789", IsraeliInvoiceExtractor.AllocationNumberIn(Padded("123456789", cap)));
        Assert.Null(IsraeliInvoiceExtractor.AllocationNumberIn(Padded("123456789", cap + 1)));
    }

    [Fact]
    public void TryParseAmount_ShekelPrefix_ParsesValueAndCurrency()
    {
        Assert.True(InvoiceValueParser.TryParseAmount(Shekel + " 1,234.56", out var v, out var cur));
        Assert.Equal(1234.56m, v);
        Assert.Equal("ILS", cur);
    }

    [Fact]
    public void TryParseAmount_ShekelSuffix_Parses()
    {
        Assert.True(InvoiceValueParser.TryParseAmount("1234.56 " + Shekel, out var v, out var cur));
        Assert.Equal(1234.56m, v);
        Assert.Equal("ILS", cur);
    }

    [Fact]
    public void TryParseAmount_DollarThousands_ParsesUsd()
    {
        Assert.True(InvoiceValueParser.TryParseAmount("$1,000", out var v, out var cur));
        Assert.Equal(1000m, v);
        Assert.Equal("USD", cur);
    }

    [Fact]
    public void TryParseAmount_StripsBidiControlChars()
    {
        // RLM (U+200F) embedded in front of the number — must be stripped before parsing.
        string s = ((char)0x200F) + "42.50";
        Assert.True(InvoiceValueParser.TryParseAmount(s, out var v, out _));
        Assert.Equal(42.50m, v);
    }

    [Fact]
    public void TryParseAmount_PlainInteger_NoCurrency()
    {
        Assert.True(InvoiceValueParser.TryParseAmount("1234", out var v, out var cur));
        Assert.Equal(1234m, v);
        Assert.Null(cur);
    }

    [Fact]
    public void TryParseAmount_NoNumber_ReturnsFalse()
    {
        Assert.False(InvoiceValueParser.TryParseAmount("abc", out var v, out _));
        Assert.Equal(0m, v);
    }

    [Theory]
    [InlineData("17%", 17)]
    [InlineData("17.5 %", 17.5)]
    public void TryParsePercent_Parses(string input, double expected)
    {
        Assert.True(InvoiceValueParser.TryParsePercent(input, out var p));
        Assert.Equal((decimal)expected, p);
    }

    [Fact]
    public void TryParsePercent_HebrewVatLabel_Parses()
    {
        // 'מע"מ 17%'
        string vat = InvoicePdfBuilder.Hb(0x05DE, 0x05E2, 0x05DE) + " 17%";
        Assert.True(InvoiceValueParser.TryParsePercent(vat, out var p));
        Assert.Equal(17m, p);
    }

    [Theory]
    [InlineData("10/06/2026", 2026, 6, 10)]
    [InlineData("10.06.2026", 2026, 6, 10)]
    [InlineData("5.6.26", 2026, 6, 5)]
    [InlineData("31-12-2025", 2025, 12, 31)]
    public void TryParseIsraeliDate_Parses(string input, int y, int m, int d)
    {
        Assert.True(InvoiceValueParser.TryParseIsraeliDate(input, out var date));
        Assert.Equal(new DateTime(y, m, d), date.Date);
    }

    [Fact]
    public void TryParseIsraeliDate_IsoFormat_ReturnsFalse()
    {
        // Year-first ISO is intentionally not an Israeli day-first format.
        Assert.False(InvoiceValueParser.TryParseIsraeliDate("2026-06-10", out _));
    }

    [Fact]
    public void NormalizeDigits_RemovesControlChars_AndNbsp()
    {
        string s = ((char)0x200E) + "1" + ((char)0x00A0) + "2" + ((char)0xFEFF);
        Assert.Equal("1 2", InvoiceValueParser.NormalizeDigits(s));
    }
}
