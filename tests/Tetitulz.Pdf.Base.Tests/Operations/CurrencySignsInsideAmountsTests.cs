// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Forms;
using Xunit;
using L = Tetitulz.Pdf.Base.Tests.Operations.InvoiceLayouts;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Amounts Word draws with the shekel sign inside them — <c>3 ₪,539.43</c> for <c>₪ 3,539.43</c> —
/// read whole, each beside the same amount as Chromium draws it, the control.
/// </summary>
public class CurrencySignsInsideAmountsTests
{
    // A word of glyphs given left to right, each 6 pt wide from x.
    private static PdfTextWord Glyphs(float x, float y, params string[] glyphs)
    {
        bool rtl = glyphs.Any(g => g.Any(c => c >= 'א' && c <= 'ת'));
        return L.VisualWord(string.Concat(rtl ? glyphs.Reverse() : glyphs), x, y, rtl, glyphs);
    }

    private static PdfTextWord Hebrew(string text, float x, float y)
        => Glyphs(x, y, text.Reverse().Select(c => c.ToString()).ToArray());

    private static PdfTextLine Line(float y, bool rtl, params PdfTextWord[] words)
    {
        float left = words.Min(w => w.X), right = words.Max(w => w.X + w.Width);
        return new PdfTextLine(TextDirection.ToReadingOrder(words), left, y, right - left, L.Height, words, rtl);
    }

    private static string[] Digits(string s) => s.Select(c => c.ToString()).ToArray();

    /// <summary>
    /// Word's totals block — the subtotal "3 ₪,025.15" (a group separator after the sign), the VAT
    /// "514 ₪.28" beside its rate, the total "77 ₪.54" in a right-to-left line — gives each amount
    /// whole, in shekels. Chromium's "₪ 3,025.15" reads the same.
    /// </summary>
    [Fact]
    public void WordsTotalsReadAsChromiumsDo()
    {
        PdfPageTextLayout Page(bool word) => L.Page(0,
            Line(700, false, (word
                    ? new[] { Glyphs(282.4f, 700, "3"), Glyphs(292.4f, 700, Digits("₪,025.15")) }
                    : new[] { Glyphs(276.4f, 700, "₪"), Glyphs(286.4f, 700, Digits("3,025.15")) })
                .Concat(new[] { Hebrew("מע\"מ", 437f, 700), Hebrew("לפני", 465f, 700), Hebrew("סה\"כ", 493f, 700) }).ToArray()),
            Line(684, false, (word
                    ? new[] { Glyphs(282.4f, 684, "5", "1", "4"), Glyphs(304.4f, 684, Digits("₪.28")) }
                    : new[] { Glyphs(272.4f, 684, "₪"), Glyphs(282.4f, 684, Digits("514.28")) })
                .Concat(new[] { Glyphs(429.5f, 684, "1", "7", "%"), Hebrew("מע\"מ", 454.3f, 684), Hebrew("מתוכו", 482f, 684) }).ToArray()),
            Line(668, true, (word
                    ? new[] { Glyphs(282.4f, 668, "7", "7"), Glyphs(298.4f, 668, Digits("₪.54")) }
                    : new[] { Glyphs(272.4f, 668, "₪"), Glyphs(282.4f, 668, Digits("77.54")) })
                .Concat(new[] { Hebrew("לתשלום", 440f, 668), Hebrew("סה\"כ", 482f, 668) }).ToArray()));

        foreach (bool word in new[] { true, false })
        {
            var invoice = IsraeliInvoiceExtractor.Extract(new[] { Page(word) });
            Assert.Equal(((decimal?)3025.15m, (decimal?)514.28m, (decimal?)17m, (decimal?)77.54m),
                         (invoice.Subtotal, invoice.Vat, invoice.VatRate, invoice.Total));
            Assert.Equal("ILS", invoice.Currency);
        }
    }

    /// <summary>
    /// The amounts of a statement's table — a toll road's passages, "12 ₪.70" each — are read whole
    /// as the items' amounts.
    /// </summary>
    [Fact]
    public void AStatementsAmountsAreReadWhole()
    {
        PdfTextLine Use(float y, string date) => Line(y, false,
            Glyphs(95.4f, y, "1", "2"), Glyphs(109.4f, y, Digits("₪.70")),
            Glyphs(421.3f, y, Digits(date)));
        var items = IsraeliInvoiceExtractor.Extract(new[] { L.Page(0,
            Line(720, true, Hebrew("סכום", 104f, 720), Hebrew("תאריך", 445f, 720)),
            Use(702, "27/08/2024"),
            Use(684, "20/08/2024"),
            Line(666, true, Glyphs(95.4f, 666, Digits("25.40")), Hebrew("סה\"כ", 445f, 666))) }).LineItems;

        Assert.Equal(new decimal?[] { 12.70m, 12.70m }, items.Select(i => i.Amount));
    }

    /// <summary>
    /// Only digits, a word space, a currency sign and a separator with digits after it are taken
    /// apart. Left as drawn — the same line, unchanged: an amount followed by its sign alone
    /// ("5 ₪"); a sign the digits stand more than a word space from; letters before the sign
    /// ("A3"); a letter after it ("₪,025.1A"); no separator after it ("₪025.15"); no digit after
    /// the separator ("₪,.25"); a letter where the sign would be ("x,025.15"). Word's
    /// "3 ₪,025.15" is taken apart, the control.
    /// </summary>
    [Fact]
    public void OnlyASignInsideAnAmountIsTakenApart()
    {
        PdfTextLine Two(PdfTextWord a, PdfTextWord b) => Line(700, false, a, b);
        var cases = new[]
        {
            Two(Glyphs(282.4f, 700, "5"), Glyphs(292.4f, 700, "₪")),
            Two(Glyphs(282.4f, 700, "3"), Glyphs(310f, 700, Digits("₪,025.15"))),
            Two(Glyphs(282.4f, 700, "A", "3"), Glyphs(298.4f, 700, Digits("₪,025.15"))),
            Two(Glyphs(282.4f, 700, "3"), Glyphs(292.4f, 700, Digits("₪,025.1A"))),
            Two(Glyphs(282.4f, 700, "3"), Glyphs(292.4f, 700, Digits("₪025.15"))),
            Two(Glyphs(282.4f, 700, "3"), Glyphs(292.4f, 700, Digits("₪,.25"))),
            Two(Glyphs(282.4f, 700, "3"), Glyphs(292.4f, 700, Digits("x,025.15"))),
        };
        foreach (var line in cases)
            Assert.Same(line, Assert.Single(CurrencySignsInsideAmounts.MovedOut(new[] { L.Page(0, line) })).Lines[0]);

        var word = Two(Glyphs(282.4f, 700, "3"), Glyphs(292.4f, 700, Digits("₪,025.15")));
        var moved = Assert.Single(CurrencySignsInsideAmounts.MovedOut(new[] { L.Page(0, word) })).Lines[0];
        Assert.Equal(new[] { "₪", "3,025.15" }, moved.Words.Select(w => w.Text));
        Assert.True(moved.Words[0].X + moved.Words[0].Width <= moved.Words[1].X);
    }

    /// <summary>
    /// The sign goes before the amount only where nothing else is: a word ending a hair before the
    /// integer part leaves the line as drawn. With room before the digits it is taken apart, the
    /// control.
    /// </summary>
    [Fact]
    public void TheSignGoesBeforeTheAmountOnlyIntoFreeSpace()
    {
        PdfTextLine Drawn(float neighbourEnd) => Line(700, false,
            Glyphs(neighbourEnd - 12f, 700, "X", "Y"), Glyphs(282.4f, 700, "3"), Glyphs(292.4f, 700, Digits("₪,025.15")));

        var crowded = Drawn(280f);
        Assert.Same(crowded, CurrencySignsInsideAmounts.MovedOut(new[] { L.Page(0, crowded) })[0].Lines[0]);

        var roomy = CurrencySignsInsideAmounts.MovedOut(new[] { L.Page(0, Drawn(260f)) })[0].Lines[0];
        Assert.Equal(new[] { "XY", "₪", "3,025.15" }, roomy.Words.Select(w => w.Text));
    }
}
