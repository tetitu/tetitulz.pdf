// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Forms;
using Xunit;
using L = Tetitulz.Pdf.Base.Tests.Operations.InvoiceLayouts;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfFieldExtractor"/>'s decisions on a Hebrew invoice's lines: which of several
/// lines carrying a label is the field, where its value ends, and that the value comes back in
/// reading order wherever it was found.
/// </summary>
public class FieldExtractorRankingTests
{
    private static FieldResult One(IReadOnlyList<PdfPageTextLayout> pages, FieldQuery q)
        => PdfFieldExtractor.Extract(pages, new[] { q })[0];

    private static FieldResult One(PdfPageTextLayout page, FieldQuery q) => One(new[] { page }, q);

    // ================================================================
    //  The label, however its quote is set
    // ================================================================

    /// <summary>
    /// A label typed with an ASCII quote finds a page that sets it with the gershayim or a curly
    /// quote, and the other way round.
    /// </summary>
    [Theory]
    [InlineData("סה\"כ", "סה״כ")]
    [InlineData("סה״כ", "סה\"כ")]
    [InlineData("סה\"כ", "סה”כ")]
    public void ALabelMatchesWhicheverQuoteThePageSetsItWith(string query, string onPage)
    {
        var page = L.Page(0, L.Rtl(700, 500, onPage, L.Column, "1,180.00"));

        var r = One(page, new FieldQuery("total", query));

        Assert.Equal("1,180.00", r.Value);
        Assert.Equal(1f, r.Confidence);
    }

    // ================================================================
    //  Which line is the field
    // ================================================================

    /// <summary>
    /// Of the lines that carry <c>מע"מ</c>, the one the label accounts for most of is the VAT
    /// line — not "total before VAT", which comes first. The control drops the VAT line: the
    /// query then takes what is left, which shows the choice was a ranking, not an exclusion.
    /// </summary>
    [Fact]
    public void TheLineTheLabelAccountsForMostOfWins()
    {
        var subtotal = L.Rtl(700, 500, "סה\"כ", "לפני", "מע\"מ", L.Column, "1,000.00");
        var vat = L.Rtl(680, 500, "מע\"מ", L.Column, "18%", "180.00");
        var total = L.Rtl(660, 500, "סה\"כ", L.Column, "1,180.00");

        Assert.Equal("18% 180.00", One(L.Page(0, subtotal, vat, total), new FieldQuery("vat", "מע\"מ")).Value);
        Assert.Equal("1,180.00", One(L.Page(0, subtotal, vat, total), new FieldQuery("total", "סה\"כ")).Value);

        Assert.Equal("1,000.00", One(L.Page(0, subtotal, total), new FieldQuery("vat", "מע\"מ")).Value);
    }

    /// <summary>
    /// An excluded phrase takes away only the label occurrences inside it: on one line holding
    /// the subtotal and the VAT, the VAT query skips "לפני מע"מ" and reads the VAT. Without the
    /// exclusion the first occurrence wins and the subtotal comes back.
    /// </summary>
    [Fact]
    public void AnExclusionRemovesOnlyTheOccurrencesInsideIt()
    {
        var page = L.Page(0, L.Rtl(700, 560, "סה\"כ", "לפני", "מע\"מ:", "1,000.00", L.Column, "מע\"מ:", "180.00"));

        Assert.Equal("1,000.00", One(page, new FieldQuery("vat", "מע\"מ")).Value);
        Assert.Equal("180.00", One(page, new FieldQuery("vat", "מע\"מ") { ExcludeLabels = new[] { "לפני מע\"מ" } }).Value);
    }

    /// <summary>
    /// Equally good matches are settled by <see cref="FieldOccurrence"/>: the first page's total
    /// or the last page's.
    /// </summary>
    [Fact]
    public void OccurrenceChoosesBetweenEquallyGoodMatches()
    {
        var pages = L.Pages(
            L.Page(0, L.Ltr(700, 72, "Total", L.Column, "10.00")),
            L.Page(1, L.Ltr(700, 72, "Total", L.Column, "20.00")));

        Assert.Equal("10.00", One(pages, new FieldQuery("t", "Total")).Value);
        var last = One(pages, new FieldQuery("t", "Total") { Occurrence = FieldOccurrence.Last });
        Assert.Equal("20.00", last.Value);
        Assert.Equal(1, last.PageIndex);
    }

    // ================================================================
    //  Where a value ends
    // ================================================================

    /// <summary>
    /// Two fields on one right-to-left line, a word space apart: each value ends where the next
    /// field begins — not at a gap, for there is none. The invoice number is not
    /// "1001 תאריך: 10/06/2026". Asked alone, the number query knows no date label, and the colon
    /// ends its value.
    /// </summary>
    [Fact]
    public void AValueEndsAtTheNextFieldsLabel()
    {
        var page = L.Page(0, L.Rtl(700, 560, "מס'", "חשבונית:", "1001", "תאריך:", "10/06/2026"));
        var queries = new[] { new FieldQuery("no", "מס' חשבונית"), new FieldQuery("date", "תאריך") };

        var r = PdfFieldExtractor.Extract(new[] { page }, queries);

        Assert.Equal("1001", r[0].Value);
        Assert.Equal("10/06/2026", r[1].Value);
        Assert.Equal("1001", One(page, queries[0]).Value);
    }

    /// <summary>
    /// On a right-to-left line the colon closes a two-word label at its far end, so the run
    /// toward it meets the label's first word — which has no colon — first. A label of another
    /// query in the same call ends the run there. Asked alone, the date query has no such
    /// knowledge and takes the word, which is the control.
    /// </summary>
    [Fact]
    public void ALabelWithoutAColonEndsAValueWhenAnotherQueryNamesIt()
    {
        var page = L.Page(0,
            L.Rtl(700, 560, "תאריך:", L.Column, "מס'", "חשבונית:"),
            L.Rtl(686, 560, "10/06/2026", L.Column, "1001"));
        var date = new FieldQuery("date", "תאריך");
        var number = new FieldQuery("no", "מס' חשבונית");

        var both = PdfFieldExtractor.Extract(new[] { page }, new[] { date, number });
        Assert.Equal("10/06/2026", both[0].Value);

        Assert.Equal("מס'", One(page, date).Value);
    }

    /// <summary>
    /// A gap as wide as a column ends a value, and a word space does not: "42 ₪" is one value,
    /// the page number across the gap is not part of it.
    /// </summary>
    [Fact]
    public void AColumnWideGapEndsAValueAndAWordSpaceDoesNot()
    {
        var page = L.Page(0, L.Ltr(700, 72, "Total", L.Column, "42", "ILS", L.Column, "Page", "1"));

        Assert.Equal("42 ILS", One(page, new FieldQuery("t", "Total")).Value);
    }

    /// <summary>
    /// An amount reads on past the gap after a rate printed by its label, to the amount a column
    /// away — and stops at the next gap after it. As text, the value is the rate alone: the
    /// control.
    /// </summary>
    [Fact]
    public void AnAmountReadsPastAGapUntilItHasItsNumber()
    {
        var page = L.Page(0, L.Rtl(700, 560, "מע\"מ", "18%", L.Column, "1,800.00", L.Column, "עמוד", "1"));

        Assert.Equal("18%", One(page, new FieldQuery("vat", "מע\"מ")).Value);
        Assert.Equal("18% 1,800.00",
            One(page, new FieldQuery("vat", "מע\"מ") { ValueKind = FieldValueKind.Amount }).Value);
    }

    // The allocation heading followed by the words given.
    private static FieldResult Allocation(params PdfTextWord[] extra)
        => Allocation(extra, "(חשבוניות", "ישראל):", "123456789");

    private static FieldResult Allocation(PdfTextWord[] extra, params string[] after)
    {
        var line = L.Rtl(700, 560, new[] { "מספר", "הקצאה" }.Concat(after).ToArray());
        var words = line.Words.Concat(extra).ToList();
        return One(L.Page(0, new PdfTextLine(line.Text, line.X, line.Y, line.Width, line.Height, words, line.IsRtl)),
                   new FieldQuery("allocation", "מספר הקצאה"));
    }

    /// <summary>
    /// A label's own bracketed qualifier, closed by the label's colon — "מספר הקצאה (חשבוניות
    /// ישראל): 123456789" — is label text, and the number after it is the value: in round or square
    /// brackets, and a qualifier of one word, "(ש"ח):". Without the opening bracket the colon word
    /// is the next label and ends the value, the control.
    /// </summary>
    [Fact]
    public void ALabelsOwnBracketedQualifierIsPartOfTheLabel()
    {
        var none = System.Array.Empty<PdfTextWord>();
        Assert.Equal("123456789", Allocation(none, "(חשבוניות", "ישראל):", "123456789").Value);
        Assert.Equal("123456789", Allocation(none, "[חשבוניות", "ישראל]:", "123456789").Value);
        Assert.Equal("1,180.00", One(L.Page(0, L.Rtl(700, 560, "סה\"כ", "(ש\"ח):", "1,180.00")),
                                     new FieldQuery("total", "סה\"כ") { ValueKind = FieldValueKind.Amount }).Value);

        Assert.Equal("חשבוניות", Allocation(none, "חשבוניות", "ישראל):", "123456789").Value);
    }

    /// <summary>
    /// A word printed over the label, as a stamp is, an empty word and a word of a blank glyph are
    /// passed over on the way to the qualifier. A word of text there is no stamp: the qualifier
    /// must come first, the control.
    /// </summary>
    [Fact]
    public void AStampOverTheLabelDoesNotHideItsQualifier()
    {
        // "הקצאה" spans 502–532 and "(חשבוניות" ends at 498; the stamp reaches into the label from
        // the left, the others sit in the space between.
        var stamp = L.Word("A", 499, 700);
        var empty = new PdfTextWord("", 499, 700, 2, L.Height, System.Array.Empty<PdfTextChar>());
        var blank = new PdfTextWord(" ", 499, 700, 2, L.Height, new[] { new PdfTextChar(" ", 499, 700, 2, L.Height) });
        var text = new PdfTextWord("x", 499, 700, 2, L.Height, System.Array.Empty<PdfTextChar>());

        Assert.Equal("123456789", Allocation(stamp).Value);
        Assert.Equal("123456789", Allocation(empty).Value);
        Assert.Equal("123456789", Allocation(blank).Value);
        Assert.NotEqual("123456789", Allocation(text).Value);
    }

    /// <summary>
    /// What is no qualifier: a bracket closed with no colon, though a later label carries a
    /// qualifier of its own; a bracket a later label's colon reaches before it closes; and a value
    /// set in the label's own word, which comes first.
    /// </summary>
    [Fact]
    public void ABracketIsNoQualifierUnlessItClosesAtTheLabelsColon()
    {
        var name = new FieldQuery("name", "שם");
        Assert.Equal("ישראל ישראלי", One(L.Page(0, L.Rtl(700, 560,
            "שם:", "(ישראל", "ישראלי)", L.Column, "טלפון", "(נייד):", "050-1234567")), name).Value.Trim('(', ')'));
        Assert.Equal("ישראל ישראלי", One(L.Page(0, L.Rtl(700, 560,
            "שם:", "(ישראל", "ישראלי", "תאריך:", "10/06/2026")), name).Value.Trim('(', ')'));
        Assert.Equal("118.00", One(L.Page(0, L.Ltr(700, 72, "Total:118.00", "(net):", "7")),
                                   new FieldQuery("t", "Total")).Value);
    }

    /// <summary>
    /// A qualifier holding a phrase the query excludes makes the label another field's: to a total
    /// that excludes "לפני מע"מ", "סה"כ (לפני מע"מ): 1,000.00" is no total, though
    /// "סה"כ (כולל מע"מ): 1,170.00" is. Without the exclusion the first is taken too, the control.
    /// </summary>
    [Fact]
    public void AQualifierTheQueryExcludesIsNoPartOfTheLabel()
    {
        var before = L.Page(0, L.Rtl(700, 560, "סה\"כ", "(לפני", "מע\"מ):", "1,000.00"));
        var including = L.Page(0, L.Rtl(700, 560, "סה\"כ", "(כולל", "מע\"מ):", "1,170.00"));
        var total = new FieldQuery("total", "סה\"כ") { ValueKind = FieldValueKind.Amount };
        var excluding = new FieldQuery("total", "סה\"כ") { ValueKind = FieldValueKind.Amount, ExcludeLabels = new[] { "לפני מע\"מ" } };

        Assert.Equal(PdfFieldExtractor.NoAmountConfidence, One(before, excluding).Confidence);
        Assert.Equal("1,170.00", One(including, excluding).Value);
        Assert.Equal("1,000.00", One(before, total).Value);
    }

    /// <summary>
    /// The public call does not know what a value is for, so it reads the value below a label as it
    /// is, though a column of such values goes on under it: passing a table's heading over needs to
    /// know which values the caller takes.
    /// </summary>
    [Fact]
    public void ThePublicCallReadsTheValueBelowALabelAsItIs()
    {
        var page = L.Page(0, L.Rtl(735, 560, "תאריך"), L.Rtl(722, 560, "08/07/2026"), L.Rtl(709, 560, "13/07/2026"));

        var r = One(page, new FieldQuery("date", "תאריך"));

        Assert.Equal("08/07/2026", r.Value);
        Assert.Equal(0.6f, r.Confidence);
    }

    /// <summary>
    /// A bracketed rate before the colon — "מע"מ (17%): 170.00" — is no qualifier: its colon follows
    /// no word with letters, so it never ended the value, and the rate stays in the value with the
    /// amount.
    /// </summary>
    [Fact]
    public void ABracketedRateBeforeTheColonStaysInTheValue()
    {
        var r = One(L.Page(0, L.Rtl(700, 560, "מע\"מ", "(17%):", L.Column, "170.00")),
                    new FieldQuery("vat", "מע\"מ") { ValueKind = FieldValueKind.Amount });

        Assert.True(InvoiceValueParser.TryParsePercent(r.Value, out decimal rate));
        Assert.Equal(17m, rate);
        Assert.True(InvoiceValueParser.TryParseAmount(r.Value, out decimal amount, out _));
        Assert.Equal(170m, amount);
    }

    // ================================================================
    //  Reading order of a value found beside or below its label
    // ================================================================

    /// <summary>
    /// A number found below its label, on a right-to-left line, is stored the way that line
    /// sorts its glyphs — right to left, "65.4321" — and comes back as it reads, "1234.56".
    /// </summary>
    [Fact]
    public void AValueFoundBelowItsLabelComesBackInReadingOrder()
    {
        // The number's glyphs are drawn left to right, as a number is; its stored text is what a
        // right-to-left line's glyph sort makes of them.
        var number = L.Word("1234.56", 300, 680);
        var stored = new PdfTextWord("65.4321", number.X, number.Y, number.Width, number.Height, number.Chars, false);
        var hebrew = L.Word("לתשלום", 420, 680);
        var below = new PdfTextLine("לתשלום 65.4321", 300, 680, 156, L.Height, new[] { hebrew, stored }, isRtl: true);

        var page = L.Page(0, L.Rtl(700, 360, "סה\"כ"), below);

        var r = One(page, new FieldQuery("t", "סה\"כ") { Side = FieldValueSide.Below });

        Assert.Equal("1234.56", r.Value);
        Assert.Equal(0.6f, r.Confidence);
    }

    /// <summary>
    /// A value below its label is the whole value — "1,180.00 ₪" — not only its first word.
    /// </summary>
    [Fact]
    public void AValueFoundBelowItsLabelKeepsItsWholeRun()
    {
        var page = L.Page(0,
            L.Ltr(700, 72, "Total"),
            L.Ltr(686, 72, "1,180.00", "ILS"));

        Assert.Equal("1,180.00 ILS", One(page, new FieldQuery("t", "Total") { Side = FieldValueSide.Below }).Value);
    }

    /// <summary>
    /// A label alone on its line in a header row, with the next field's label beside it, reads
    /// its value from below rather than taking the neighbouring label.
    /// </summary>
    [Fact]
    public void ANeighbouringLabelIsNotTakenAsAValue()
    {
        var page = L.Page(0,
            L.Rtl(700, 560, "תאריך:", L.Column, "אסמכתא:"),
            L.Rtl(686, 560, "10/06/2026", L.Column, "77"));

        Assert.Equal("10/06/2026", One(page, new FieldQuery("date", "תאריך")).Value);
    }

    /// <summary>
    /// A currency closing its label with the label's colon — <c>סה"כ לתשלום כולל מע"מ בש"ח:</c>,
    /// "in shekels" — is no next label: the value on its line is read. A lettered word with a
    /// colon that is no currency still ends the value, as the control shows.
    /// </summary>
    [Fact]
    public void ACurrencyClosingItsLabelIsNoNextLabel()
    {
        var withCurrency = L.Page(0, L.Rtl(700, 560, "סה\"כ", "לתשלום", "בש\"ח:", "14,236.80"));
        var withLabel = L.Page(0, L.Rtl(700, 560, "סה\"כ", "לתשלום", "הערה:", "14,236.80"));

        var r = One(withCurrency, AmountQuery("סה\"כ לתשלום"));
        Assert.Equal("14,236.80", r.Value);
        Assert.Equal(1f, r.Confidence);
        Assert.Equal(string.Empty, One(withLabel, AmountQuery("סה\"כ לתשלום")).Value);
    }

    /// <summary>
    /// A rate whose percent sign is a word of its own, first — <c>סה"כ חייב במע"מ % 18.00 1,862.40</c>,
    /// a fuel retailer's subtotal line — is no amount the value ends at: the value reads on to the
    /// amount. With the sign touching its number the same line read so already.
    /// </summary>
    [Theory]
    [InlineData("%", "18.00")]
    [InlineData("%18.00")]
    public void ARateWhoseSignIsAWordOfItsOwnIsNoAmount(params string[] rate)
    {
        var tokens = new[] { "סה\"כ", "חייב", "במע\"מ" }.Concat(rate).Concat(new[] { L.Column, "1,862.40" }).ToArray();
        var page = L.Page(0, L.Rtl(700, 560, tokens));

        var r = One(page, AmountQuery("סה\"כ חייב במע\"מ"));

        Assert.True(InvoiceValueParser.TryParseAmount(r.Value, out decimal subtotal, out _));
        Assert.Equal(1862.40m, subtotal);
    }

    /// <summary>
    /// A currency closing a label does not make its line less specific than a bare label's line:
    /// <c>סה"כ לתשלום בש"ח:</c> printed first ties with a later <c>סה"כ:</c>, and the first match in
    /// reading order wins the tie. Counted as the label's line's own letters, the currency made the
    /// bare total line win.
    /// </summary>
    [Fact]
    public void ACurrencyClosingItsLabelDoesNotMakeItsLineLessSpecific()
    {
        var page = L.Page(0,
            L.Rtl(720, 560, "סה\"כ", "לתשלום", "בש\"ח:", "118.00"),
            L.Rtl(700, 560, "סה\"כ:", "100.00"));

        var r = One(page, new FieldQuery("t", "סה\"כ לתשלום", "סה\"כ") { ValueKind = FieldValueKind.Amount });

        Assert.Equal("118.00", r.Value);
    }

    // ================================================================
    //  A row of column totals
    // ================================================================

    private static FieldQuery AmountQuery(string label)
        => new FieldQuery("t", label) { ValueKind = FieldValueKind.Amount };

    /// <summary>
    /// A leasing invoice's totals row — rent, maintenance and exempt columns, then their sum —
    /// reads as its last column, which is the invoice's total; the first column is only the rent's.
    /// The row is the same whichever way the page lays the table out: right to left, or with its
    /// Hebrew label at the left edge, as one leasing company prints it.
    /// </summary>
    [Fact]
    public void ARowOfColumnTotalsReadsAsItsTotalColumn()
    {
        string[] amounts = { L.Column, "20,140.50", L.Column, "3,612.25", L.Column, "1,847.25", L.Column, "25,600.00" };
        // A left-to-right line draws its Hebrew label's words in visual order: the label's last word leftmost.
        var rtl = L.Rtl(700, 560, new[] { "סה\"כ", "כולל", "מע\"מ" }.Concat(amounts).ToArray());
        var ltr = L.Ltr(700, 40, new[] { "מע\"מ", "כולל", "סה\"כ" }.Concat(amounts).ToArray());

        Assert.Equal("25,600.00", One(L.Page(0, rtl), AmountQuery("סה\"כ כולל מע\"מ")).Value);
        Assert.Equal("25,600.00", One(L.Page(0, ltr), AmountQuery("סה\"כ כולל מע\"מ")).Value);
    }

    /// <summary>
    /// The VAT row of the same table keeps its rate before the total column's amount, so the rate
    /// is still read from the value.
    /// </summary>
    [Fact]
    public void ARowOfColumnTotalsKeepsTheRateBeforeItsTotal()
    {
        var page = L.Page(0, L.Rtl(700, 560, "מע\"מ", L.Column, "18%", "3,131.48", L.Column, "602.52", L.Column, "3,734.00"));

        var r = One(page, AmountQuery("מע\"מ"));

        Assert.True(InvoiceValueParser.TryParseAmount(r.Value, out decimal vat, out _));
        Assert.Equal(3734.00m, vat);
        Assert.True(InvoiceValueParser.TryParsePercent(r.Value, out decimal rate));
        Assert.Equal(18m, rate);
    }

    /// <summary>
    /// What is not a row of totals keeps its first amount: a last column that is no sum of the
    /// others, whether one column follows the first or several, and a label between the amounts
    /// (two fields on one line).
    /// </summary>
    [Theory]
    [InlineData("100.00", "|", "18.00", "|", "200.00")]
    [InlineData("100.00", "|", "18.00")]
    [InlineData("100.00", "|", "מע\"מ:", "18.00", "|", "118.00")]
    public void ARowWhoseLastColumnIsNoSumKeepsItsFirstAmount(params string[] after)
    {
        var page = L.Page(0, L.Rtl(700, 560, new[] { "סה\"כ:" }.Concat(after).ToArray()));

        Assert.Equal("100.00", One(page, AmountQuery("סה\"כ")).Value);
    }

    /// <summary>
    /// A row padded with a filler's marks — <c>*** סה"כ ********** 17,304.10 ...</c>, as a leasing
    /// company prints its totals — is still a row of amounts, before its first amount and between
    /// its columns.
    /// </summary>
    [Theory]
    [InlineData("**********", "|", "100.00", "|", "18.00", "|", "118.00")]
    [InlineData("100.00", "|", "**********", "|", "18.00", "|", "118.00")]
    public void AFillerInARowOfColumnTotalsIsPassedOver(params string[] after)
    {
        var page = L.Page(0, L.Rtl(700, 560, new[] { "סה\"כ", L.Column }.Concat(after).ToArray()));

        Assert.True(InvoiceValueParser.TryParseAmount(One(page, AmountQuery("סה\"כ")).Value, out decimal total, out _));
        Assert.Equal(118.00m, total);
    }

    /// <summary>
    /// Word sets a narrow table's amounts a word space apart: a second amount with decimals beside
    /// the first is the next column all the same — the row of totals reads as its sum column, a row
    /// whose last is no sum keeps its first amount, and a rate before its amount is still one value.
    /// </summary>
    [Theory]
    [InlineData(new[] { "מע\"מ", "18%", "2,477.32", "448.43", "2,925.75" }, "מע\"מ", 2925.75)]
    [InlineData(new[] { "סה\"כ:", "100.00", "18.00", "200.00" }, "סה\"כ", 100.00)]
    [InlineData(new[] { "מע\"מ", "18%", "180.00" }, "מע\"מ", 180.00)]
    public void AmountsAWordSpaceApartAreColumns(string[] tokens, string label, double expected)
    {
        var page = L.Page(0, L.Rtl(700, 560, tokens));

        Assert.True(InvoiceValueParser.TryParseAmount(One(page, AmountQuery(label)).Value, out decimal value, out _));
        Assert.Equal((decimal)expected, value);
    }

    /// <summary>The control for the theory above: the same line whose last column is the sum reads as it.</summary>
    [Fact]
    public void ARowWhoseLastColumnIsTheSumReadsAsIt()
    {
        var page = L.Page(0, L.Rtl(700, 560, "סה\"כ:", "100.00", L.Column, "18.00", L.Column, "118.00"));

        Assert.Equal("118.00", One(page, AmountQuery("סה\"כ")).Value);
        Assert.Equal("100.00", One(page, new FieldQuery("t", "סה\"כ")).Value);
    }
}
