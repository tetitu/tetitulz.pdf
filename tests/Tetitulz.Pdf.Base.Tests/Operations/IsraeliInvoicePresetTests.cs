// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Constants;
using Xunit;
using L = Tetitulz.Pdf.Base.Tests.Operations.InvoiceLayouts;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="IsraeliInvoiceExtractor"/>'s preset on the wordings producers print: which number,
/// date and total a page means, which document it is, whose identification number is whose, and
/// what an allocation number is — each beside the control that reads the other way.
/// </summary>
public class IsraeliInvoicePresetTests
{
    private const string Supplier = IsraeliInvoiceExtractorTests.Supplier;
    private const string Customer = IsraeliInvoiceExtractorTests.Customer;

    private static IsraeliInvoice Read(params PdfTextLine[] lines)
        => IsraeliInvoiceExtractor.Extract(new[] { L.Page(0, lines) });

    private static List<string> Codes(IsraeliInvoice inv) => inv.Findings.Select(f => f.Code).ToList();

    // The fixture invoice's lines, with more added.
    private static IsraeliInvoice ReadInvoiceWith(bool withAllocation, params PdfTextLine[] more)
        => IsraeliInvoiceExtractor.Extract(new[]
        {
            L.Page(0, IsraeliInvoiceExtractorTests.InvoicePage(withAllocation).Lines.Concat(more).ToArray()),
        });

    // ================================================================
    //  The invoice number
    // ================================================================

    /// <summary>
    /// A credit invoice's own number follows its title; the invoice it credits is a reference,
    /// and is not its number. Without the title's number the reference is still not taken.
    /// </summary>
    [Fact]
    public void ACreditInvoicesNumberIsItsOwnNotTheOneItCredits()
    {
        var reference = L.Rtl(740, 560, "זיכוי", "עבור", "חשבונית", "מס'", "1200");

        // A receipt number below: the title's own label must find the title's number first.
        var inv = Read(L.Rtl(780, 560, "חשבונית", "מס", "זיכוי", "מס'", "55"), reference,
                       L.Rtl(700, 560, "קבלה", "מס'", "77"));
        Assert.Equal(IsraeliDocumentType.CreditInvoice, inv.DocumentType);
        Assert.Equal("55", inv.InvoiceNumber);

        Assert.Null(Read(L.Rtl(780, 560, "חשבונית", "מס", "זיכוי"), reference).InvoiceNumber);
    }

    /// <summary>
    /// A number printed straight after the title, with no label, is the invoice's number; a date
    /// there is not.
    /// </summary>
    [Fact]
    public void ANumberAfterTheTitleIsTheInvoicesNumber()
    {
        Assert.Equal("10049", Read(L.Rtl(780, 560, "חשבונית", "מס/קבלה", "10049")).InvoiceNumber);
        Assert.Null(Read(L.Rtl(780, 560, "חשבונית", "מס", "15/07/2026")).InvoiceNumber);

        // Beside the title only: a number on the line below it, under its last word, is not taken
        // for the title's.
        Assert.Null(Read(L.Rtl(780, 560, "חשבונית", "מס"), L.Rtl(766, 516, "1001")).InvoiceNumber);
    }

    // ================================================================
    //  The date
    // ================================================================

    /// <summary>
    /// "בתאריך" ("on the date") in a note is no date label: the header's date stays the
    /// invoice's, though the note's line is shorter.
    /// </summary>
    [Fact]
    public void ADateInANoteIsNotTheInvoicesDate()
    {
        var inv = ReadInvoiceWith(true, L.Rtl(500, 560, "סופק", "בתאריך", "01/07/2026"));

        Assert.Equal(new DateTime(2026, 7, 15), inv.Date);

        // With no date label at all, the note is still no label: the page's first date is taken.
        var unlabelled = Read(
            L.Rtl(780, 560, "חשבונית", "מס"),
            L.Rtl(760, 560, "15/07/2026"),
            L.Rtl(500, 560, "סופק", "בתאריך", "01/07/2026"));
        Assert.Equal(new DateTime(2026, 7, 15), unlabelled.Date);
    }

    // ================================================================
    //  The totals
    // ================================================================

    /// <summary>
    /// "Total including VAT", Hebrew and English: the VAT label inside the total's label does not
    /// end the total's value before its amount.
    /// </summary>
    [Fact]
    public void ATotalIncludingVatIsRead()
    {
        var hebrew = Read(
            L.Rtl(570, 560, "סה\"כ", "לפני", "מע\"מ", L.Column, "10,000.00"),
            L.Rtl(556, 560, "מע\"מ", "18%", L.Column, "1,800.00"),
            L.Rtl(542, 560, "סה\"כ", "לתשלום", "כולל", "מע\"מ", L.Column, "11,800.00"));
        Assert.Equal(11800m, hebrew.Total);
        Assert.DoesNotContain(InvoiceFindingCodes.TotalMismatch, Codes(hebrew));

        var english = Read(
            L.Ltr(570, 72, "Subtotal", L.Column, "10,000.00"),
            L.Ltr(556, 72, "VAT", "18%", L.Column, "1,800.00"),
            L.Ltr(542, 72, "Total", "incl.", "VAT", L.Column, "11,800.00"));
        Assert.Equal(11800m, english.Total);
        Assert.Equal(1800m, english.Vat);
    }

    /// <summary>
    /// A paid invoice prints "Total Due 0.00" after its total. Of the totals, the one the subtotal
    /// and the VAT add up to is the invoice's. With nothing paid, "Total Due" alone is the total,
    /// the control.
    /// </summary>
    [Fact]
    public void ABalanceAfterPaymentIsNotTheTotal()
    {
        var paid = Read(
            L.Ltr(570, 72, "Subtotal", L.Column, "1,000.00"),
            L.Ltr(556, 72, "VAT", "18%", L.Column, "180.00"),
            L.Ltr(542, 72, "Total", L.Column, "1,180.00"),
            L.Ltr(528, 72, "Amount", "Paid", L.Column, "1,180.00"),
            L.Ltr(514, 72, "Total", "Due", L.Column, "0.00"));
        Assert.Equal(1180m, paid.Total);
        Assert.DoesNotContain(InvoiceFindingCodes.TotalMismatch, Codes(paid));

        var unpaid = Read(
            L.Ltr(570, 72, "Subtotal", L.Column, "1,000.00"),
            L.Ltr(556, 72, "VAT", "18%", L.Column, "180.00"),
            L.Ltr(542, 72, "Total", "Due", L.Column, "1,180.00"));
        Assert.Equal(1180m, unpaid.Total);
    }

    /// <summary>
    /// An invoice mixing taxable and exempt items: the taxable subtotal, the exempt part, the VAT
    /// on the taxable part, and a total of all three — read apart, and agreeing.
    /// </summary>
    [Fact]
    public void AnInvoiceWithAnExemptPartIsReadAndAgrees()
    {
        var inv = Read(
            L.Rtl(780, 560, "חשבונית", "מס"),
            L.Rtl(570, 560, "סה\"כ", "חייב", "במע\"מ", L.Column, "1,000.00"),
            L.Rtl(556, 560, "סה\"כ", "פטור", "ממע\"מ", L.Column, "200.00"),
            L.Rtl(542, 560, "מע\"מ", "18%", L.Column, "180.00"),
            L.Rtl(528, 560, "סה\"כ", "לתשלום", L.Column, "1,380.00"));

        Assert.Equal(1000m, inv.Subtotal);
        Assert.Equal(200m, inv.ExemptAmount);
        Assert.Equal(180m, inv.Vat);
        Assert.Equal(1380m, inv.Total);
        Assert.DoesNotContain(InvoiceFindingCodes.TotalMismatch, Codes(inv));
        Assert.DoesNotContain(InvoiceFindingCodes.VatMismatch, Codes(inv));
    }

    /// <summary>
    /// "VAT" inside "Private" is no VAT label: a zero-rated export with no VAT line reads no VAT,
    /// and its total is not contradicted. A real VAT line is read, the control.
    /// </summary>
    [Fact]
    public void VatInsideAnEnglishWordIsNoVat()
    {
        var export = Read(
            L.Ltr(600, 72, "Private", "cloud", "hosting", L.Column, "12,000.00"),
            L.Ltr(570, 72, "Subtotal", L.Column, "12,000.00"),
            L.Ltr(542, 72, "Total", L.Column, "12,000.00"));
        Assert.Null(export.Vat);
        Assert.Contains(InvoiceFindingCodes.ZeroVat, Codes(export));
        Assert.DoesNotContain(InvoiceFindingCodes.TotalMismatch, Codes(export));

        var taxed = Read(
            L.Ltr(600, 72, "Private", "cloud", "hosting", L.Column, "12,000.00"),
            L.Ltr(570, 72, "Subtotal", L.Column, "12,000.00"),
            L.Ltr(556, 72, "VAT", "18%", L.Column, "2,160.00"),
            L.Ltr(542, 72, "Total", L.Column, "14,160.00"));
        Assert.Equal(2160m, taxed.Vat);
    }

    /// <summary>
    /// The interim sum — "סכום ביניים (לפני מע"מ)", "סה"כ ביניים" — is the subtotal.
    /// </summary>
    [Theory]
    [InlineData("סכום", "ביניים", "(לפני", "מע\"מ)")]
    [InlineData("סה\"כ", "ביניים")]
    public void AnInterimSumIsTheSubtotal(params string[] label)
    {
        var inv = Read(
            L.Rtl(780, 560, "חשבונית", "מס"),
            L.Rtl(570, 560, label.Concat(new[] { L.Column, "22,950.00" }).ToArray()),
            L.Rtl(556, 560, "מע\"מ", "18%", L.Column, "4,131.00"),
            L.Rtl(542, 560, "סה\"כ", "לתשלום", L.Column, "27,081.00"));

        Assert.Equal(22950m, inv.Subtotal);
        Assert.Equal(4131m, inv.Vat);
        Assert.Equal(27081m, inv.Total);
        Assert.DoesNotContain(InvoiceFindingCodes.TotalMismatch, Codes(inv));
    }

    // A toll road's tax invoice's totals: the sum taxed at a rate the label names — its word with
    // a digit drawn as a right-to-left line draws it — the VAT line, and the total.
    private static IsraeliInvoice ReadTaxedAtRate(string rate, PdfTextLine vatLine)
        => Read(
            L.Rtl(780, 560, "חשבונית", "מס"),
            L.Rtl(760, 560, "תאריך:", "20/01/2023"),
            L.Drawn(L.Rtl(600, 560, "סה\"כ", "סכומים", "ב-" + rate + "%", "מע\"מ", L.Column, "107.24")),
            vatLine,
            L.Rtl(572, 560, "סה\"כ", "כולל", "מע\"מ", L.Column, "125.47"));

    /// <summary>
    /// "סה"כ סכומים ב-17% מע"מ" — the sum taxed at a rate — is the subtotal, at each rate the VAT
    /// has had, and the rate it names is the invoice's when the VAT's own line names none.
    /// </summary>
    [Theory]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("16.5")]
    public void TheSumTaxedAtARateIsTheSubtotalAndNamesTheRate(string rate)
    {
        var inv = ReadTaxedAtRate(rate, L.Rtl(586, 560, "סה\"כ", "מע\"מ", L.Column, "18.23"));

        Assert.Equal(107.24m, inv.Subtotal);
        Assert.Equal(decimal.Parse(rate, CultureInfo.InvariantCulture), inv.VatRate);
        Assert.Equal(18.23m, inv.Vat);
        Assert.Equal(125.47m, inv.Total);
    }

    /// <summary>
    /// A sum taxed at a rate the VAT never had matches no label: no subtotal, no rate — and the
    /// VAT is still read, so the page was.
    /// </summary>
    [Fact]
    public void ASumTaxedAtARateTheVatNeverHadIsNoSubtotal()
    {
        string rate = (IsraeliTaxRules.StandardVatRates(null).Max() + 1).ToString(CultureInfo.InvariantCulture);

        var inv = ReadTaxedAtRate(rate, L.Rtl(586, 560, "סה\"כ", "מע\"מ", L.Column, "18.23"));

        Assert.Null(inv.Subtotal);
        Assert.Null(inv.VatRate);
        Assert.Equal(18.23m, inv.Vat);
    }

    /// <summary>
    /// A rate printed on the VAT's own line is the invoice's, before the one a subtotal's label
    /// names.
    /// </summary>
    [Fact]
    public void TheVatLinesRateComesBeforeTheSubtotalLabelsRate()
    {
        var inv = ReadTaxedAtRate("17", L.Rtl(586, 560, "מע\"מ", "18%", L.Column, "19.30"));

        Assert.Equal(18m, inv.VatRate);
        Assert.Equal(19.30m, inv.Vat);
        Assert.Equal(107.24m, inv.Subtotal);
    }

    /// <summary>
    /// The VAT word inside the sum taxed at a rate is no VAT label: the VAT printed below its own
    /// label is the VAT, though a value on a label's own line ranks above one found below it.
    /// </summary>
    [Fact]
    public void TheTaxedSumsVatWordIsNoVatLabel()
    {
        var inv = Read(
            L.Rtl(780, 560, "חשבונית", "מס"),
            L.Drawn(L.Rtl(600, 560, "סה\"כ", "סכומים", "ב-17%", "מע\"מ", L.Column, "107.24")),
            L.Rtl(586, 560, "סה\"כ", "מע\"מ"),
            L.Rtl(572, 532, "18.23"));

        Assert.Equal(18.23m, inv.Vat);
        Assert.Equal(107.24m, inv.Subtotal);
    }

    /// <summary>
    /// A VAT line that names its rate before the VAT word — "17% מע"מ 170.00" — is still the VAT:
    /// only the taxed sum's phrase, with its "ב-", is excluded.
    /// </summary>
    [Fact]
    public void AVatLineNamingItsRateIsStillTheVat()
    {
        var inv = Read(
            L.Rtl(780, 560, "חשבונית", "מס"),
            L.Rtl(570, 560, "סה\"כ", "לפני", "מע\"מ", L.Column, "1,000.00"),
            L.Rtl(556, 560, "17%", "מע\"מ", L.Column, "170.00"),
            L.Rtl(542, 560, "סה\"כ", "לתשלום", L.Column, "1,170.00"));

        Assert.Equal(170m, inv.Vat);
        Assert.DoesNotContain(InvoiceFindingCodes.TotalMismatch, Codes(inv));
    }

    /// <summary>
    /// A total whose qualifier is bracketed before its colon — "סה"כ (כולל מע"מ): 1,170.00" — is
    /// read: the colon closes the label, not the value. "סה"כ (לפני מע"מ):" holds a phrase the total
    /// query excludes, and gives no total.
    /// </summary>
    [Fact]
    public void ATotalsBracketedQualifierIsPartOfItsLabel()
    {
        var inv = Read(
            L.Rtl(780, 560, "חשבונית", "מס"),
            L.Rtl(570, 560, "סה\"כ", "(לפני", "מע\"מ):", L.Column, "1,000.00"),
            L.Rtl(556, 560, "מע\"מ", "17%", L.Column, "170.00"),
            L.Rtl(542, 560, "סה\"כ", "(כולל", "מע\"מ):", L.Column, "1,170.00"));

        Assert.Equal(1170m, inv.Total);
        Assert.Equal(170m, inv.Vat);
    }

    /// <summary>
    /// A total's label inside a sentence reads the sentence on: "לתשלום הפיצוי בתוך 30 ימים" is no
    /// total of 30. An amount with its currency after it, or before it, is a total, the controls.
    /// </summary>
    [Fact]
    public void ANumberReadOutOfASentenceIsNoTotal()
    {
        Assert.Null(Read(L.Rtl(700, 560, "לו", "על", "התנגדותו", "לתשלום", "הפיצוי", "בתוך", "30", "ימים,",
                               "ישלם", "לצד", "השלישי")).Total);

        Assert.Equal(1180m, Read(L.Rtl(700, 560, "סה\"כ", "לתשלום", L.Column, "1,180.00", "ש\"ח")).Total);
        Assert.Equal(125.47m, Read(L.Rtl(700, 560, "סה\"כ", "כולל", "מע\"מ", "(ש\"ח)", L.Column, "125.47")).Total);
    }

    /// <summary>
    /// A total's label at the end of a line of text takes nothing from the next line's section
    /// number: "17. מקום שיפוט" is no total. A VAT rate by its label with the amount under it is
    /// still read, the control.
    /// </summary>
    [Fact]
    public void ASectionNumberUnderALabelInTextIsNoTotal()
    {
        Assert.Null(Read(L.Rtl(700, 560, "לתשלום", "תגמולי", "ביטוח", "בשל", "מקרה", "ביטוח."),
                         L.Rtl(686, 560, "17.", "מקום", "שיפוט")).Total);

        var vat = Read(L.Rtl(700, 500, "מע\"מ", "17%"), L.Rtl(686, 500, "170.00"));
        Assert.Equal(170m, vat.Vat);
        Assert.Equal(17m, vat.VatRate);
    }

    /// <summary>
    /// A number inside a code, or in a sentence after the VAT's label, is no VAT: "PCN874", and
    /// "סכום המע"מ הוא 4,250 ש"ח ומעלה". A VAT line is read, the control.
    /// </summary>
    [Fact]
    public void ANumberInsideACodeOrASentenceIsNoVat()
    {
        Assert.Null(Read(L.Rtl(700, 560, "בדוח", "מע\"מ", "מקוון", "PCN874")).Vat);
        Assert.Null(Read(L.Rtl(700, 560, "סכום", "המע\"מ", "הוא", "4,250", "ש\"ח", "ומעלה")).Vat);

        Assert.Equal(170m, Read(L.Rtl(700, 560, "מע\"מ", "17%", L.Column, "170.00")).Vat);
    }

    /// <summary>
    /// A price "plus VAT" — "רכב מסחרי: 40 ₪ + מע"מ" — names no VAT amount, though the price beside
    /// the VAT word is one; written "בתוספת מע"מ", the price has a word after it, and no amount that
    /// is a value does. The VAT word as a label over 40 ₪ reads it, the control.
    /// </summary>
    [Fact]
    public void APricePlusVatNamesNoVat()
    {
        Assert.Null(Read(L.Rtl(700, 560, "רכב", "מסחרי:", "40", "₪", "+", "מע\"מ")).Vat);
        Assert.Null(Read(L.Rtl(700, 560, "רכב", "מסחרי:", "40", "₪", "בתוספת", "מע\"מ")).Vat);

        Assert.Equal(40m, Read(L.Rtl(700, 560, "מע\"מ", L.Column, "40", "₪")).Vat);
    }

    // ================================================================
    //  A row of headings, and a table's
    // ================================================================

    /// <summary>
    /// A row of headings over a row of values — "מספר מסמך | תאריך | לכבוד" — reads the date under
    /// its heading, though the page printed another date first; the heading a column away is no
    /// date. The headings set as three lines at one height read the same.
    /// </summary>
    [Fact]
    public void ARowOfLabelsReadsItsValuesBelow()
    {
        float[] columns = { 560f, 470f, 380f };
        var printed = L.Rtl(800, 560, "הודפס", "01/01/2026");
        var title = L.Rtl(780, 560, "חשבונית", "מס");
        var values = L.RtlRow(722, columns, "37212", "26/09/2025", "דוגמה בע\"מ");

        var row = Read(printed, title, L.RtlRow(735, columns, "מספר מסמך", "תאריך", "לכבוד"), values);
        Assert.Equal(new DateTime(2025, 9, 26), row.Date);
        var date = row.Fields.Single(f => f.Key == IsraeliInvoiceExtractor.DateKey);
        Assert.Equal("תאריך", date.Label);
        Assert.Equal(0.6f, date.Confidence);
        Assert.Equal("37212", row.InvoiceNumber);

        var lines = Read(printed, title, L.Rtl(735, 560, "מספר", "מסמך"), L.Rtl(735, 470, "תאריך"),
                         L.Rtl(735, 380, "לכבוד"), values);
        Assert.Equal(new DateTime(2025, 9, 26), lines.Date);

        // What is under the heading is still asked whether it is a date: "בהמתנה" (pending) is not.
        var pending = Read(printed, title, L.RtlRow(735, columns, "מספר מסמך", "תאריך", "לכבוד"),
                           L.RtlRow(722, columns, "37212", "בהמתנה", "דוגמה בע\"מ"));
        Assert.Equal(new DateTime(2026, 1, 1), pending.Date);
        Assert.Equal(string.Empty, pending.Fields.Single(f => f.Key == IsraeliInvoiceExtractor.DateKey).Value);
    }

    /// <summary>
    /// A heading over a column of dates is a table's, no date label: the statement's date is the
    /// page's first. With the table one row long the date under the heading is read, the control;
    /// a heading alone on its line over the same column is passed over too.
    /// </summary>
    [Fact]
    public void AColumnHeadingIsNoFieldLabel()
    {
        float[] columns = { 560f, 470f, 300f };
        var printed = L.Rtl(800, 560, "הופק", "02/07/2026");
        var heading = L.RtlRow(735, columns, "תאריך", "תיאור התנועה", "סכום");
        var first = L.RtlRow(722, columns, "08/07/2026", "החזר הלוואה", "4,124.59");
        var second = L.RtlRow(709, columns, "13/07/2026", "משיכת מזומן", "5,915.29");

        var table = Read(printed, heading, first, second);
        Assert.Equal(new DateTime(2026, 7, 2), table.Date);
        Assert.Equal(string.Empty, table.Fields.Single(f => f.Key == IsraeliInvoiceExtractor.DateKey).Value);

        Assert.Equal(new DateTime(2026, 7, 8), Read(printed, heading, first).Date);

        Assert.Equal(new DateTime(2026, 7, 2), Read(printed, L.Rtl(735, 560, "תאריך"), first, second).Date);
    }

    /// <summary>
    /// A label whose own value is a blank to fill in reads nothing below it: under "תאריך: ______" a
    /// print stamp is no date, and the header's date stands; under "מספר הקצאה: אין" a bank account
    /// is no allocation number.
    /// </summary>
    [Fact]
    public void AFillInBlankBesideItsLabelIsNotReadBelow()
    {
        var signed = Read(
            L.Rtl(780, 560, "חשבונית", "מס"),
            L.Rtl(760, 560, "15/07/2026"),
            L.Rtl(300, 560, "חתימת", "המקבל:", "______", L.Column, "תאריך:", "______"),
            L.Rtl(286, 560, "הופק", "בתאריך", "20/07/2026", "10:32"));
        Assert.Equal(new DateTime(2026, 7, 15), signed.Date);

        var none = Read(
            L.Rtl(780, 560, "חשבונית", "מס"),
            L.Rtl(676, 560, "מספר", "הקצאה:", "אין"),
            L.Rtl(662, 560, "חשבון", "בנק", "12-345-678901"));
        Assert.Null(none.AllocationNumber);
    }

    /// <summary>
    /// The customer's address under the row of values, reaching under the number's column, does not
    /// make the number a column's: only the words under the number's own box count — "תל אביב",
    /// with no number — not the house number just beside it. A second row of numbers there does,
    /// the control.
    /// </summary>
    [Fact]
    public void AnAddressReachingUnderAValueDoesNotMakeItAColumn()
    {
        float[] columns = { 560f, 460f, 360f };
        var title = L.Rtl(780, 560, "חשבונית", "מס");
        var heading = L.RtlRow(735, columns, "לכבוד", "מספר חשבונית", "תאריך");
        var values = L.RtlRow(722, columns, "ישראל ישראלי", "37212", "26/09/2025");

        // "37212" spans 430–460; "15," lands at 462–480, beside it, and "תל אביב" under it.
        var address = L.Rtl(709, 560, "רחוב", "בן-יהודה", "15,", "תל", "אביב");
        Assert.Equal("37212", Read(title, heading, values, address).InvoiceNumber);

        Assert.Null(Read(title, heading, values, L.RtlRow(709, columns, "לקוח אחר", "37213", "27/09/2025")).InvoiceNumber);
    }

    /// <summary>
    /// Reading a value from under its heading is for text: a total refused a column from its label
    /// takes no amount from under it.
    /// </summary>
    [Fact]
    public void ARefusedAmountIsNotReadBelowItsLabel()
    {
        Assert.Null(Read(L.Rtl(700, 560, "לתשלום", L.Column, "הפיצוי", "בתוך", "30", "ימים"),
                         L.Rtl(686, 560, "1,000.00")).Total);
    }

    // ================================================================
    //  The document's type
    // ================================================================

    /// <summary>
    /// A title is whole words: "חשבונית מספר" (invoice number) and "חשבונית מסחרית" (commercial
    /// invoice) are no tax invoice, and an en or em dash joins a title as a slash does.
    /// </summary>
    [Theory]
    [InlineData(IsraeliDocumentType.TaxInvoiceReceipt, "חשבונית", "מס", "\u2013", "קבלה")]
    [InlineData(IsraeliDocumentType.CreditInvoice, "חשבונית", "מס", "\u2014", "זיכוי")]
    [InlineData(IsraeliDocumentType.TaxInvoice, "חשבונית-מס")]
    [InlineData(IsraeliDocumentType.Proforma, "חשבון", "עסקה", "/", "פרופורמה")]
    [InlineData(IsraeliDocumentType.AgentInvoice, "חשבונית", "מס", "סוכן")]
    [InlineData(IsraeliDocumentType.DonationReceipt, "קבלה", "על", "תרומה")]
    [InlineData(IsraeliDocumentType.Unknown, "חשבונית", "מספר", "1045")]
    [InlineData(IsraeliDocumentType.Unknown, "חשבונית", "מסחרית")]
    public void ATitleIsMatchedWordForWord(IsraeliDocumentType expected, params string[] title)
    {
        Assert.Equal(expected, Read(L.Rtl(780, 560, title)).DocumentType);
    }

    /// <summary>
    /// "שעות קבלה" (reception hours) in a letterhead above the title does not make the document a
    /// receipt: a receipt word counts only where it stands as a title, and only when no title is
    /// found.
    /// </summary>
    [Fact]
    public void AReceiptWordInsideALineIsNoTitle()
    {
        var inv = Read(
            L.Rtl(790, 560, "שעות", "קבלה:", "08:00-16:00"),
            L.Rtl(770, 560, "חשבונית", "מס/קבלה"));

        Assert.Equal(IsraeliDocumentType.TaxInvoiceReceipt, inv.DocumentType);

        // Alone, it is still no title: the document is unknown, not a receipt.
        Assert.Equal(IsraeliDocumentType.Unknown, Read(L.Rtl(790, 560, "שעות", "קבלה:", "08:00-16:00")).DocumentType);
    }

    /// <summary>
    /// A receipt's title set a column away from the supplier's name, on the name's line, opens a
    /// block of that line and is read — an English one too, whose block a Hebrew line reads left to
    /// right. The same word a word's gap from the name, inside its block, is not: the controls.
    /// </summary>
    [Fact]
    public void AReceiptTitleBesideTheSuppliersNameIsRead()
    {
        var name = L.Rtl(790, 560, "אופק", "שירותים");

        Assert.Equal(IsraeliDocumentType.Receipt,
                     Read(L.Rtl(790, 560, "אופק", "שירותים", L.Column, "קבלה", "מס'", "1045")).DocumentType);
        Assert.Equal(IsraeliDocumentType.Unknown, Read(L.Rtl(790, 560, "אופק", "שירותים", "קבלה")).DocumentType);

        Assert.Equal(IsraeliDocumentType.Receipt,
                     Read(Beside(name, L.Ltr(790, 300, "Receipt", "No.", "1045"))).DocumentType);
        Assert.Equal(IsraeliDocumentType.Unknown, Read(Beside(name, L.Ltr(790, 300, "Paid", "Receipt"))).DocumentType);
    }

    /// <summary>
    /// A title phrase inside a sentence is no title: a word before it in its block of the line
    /// (<c>רשאי להוציא חשבונית מס</c>), more than two words after it, or a full stop ending the
    /// block. Nor is the number after it the invoice's — which the same title, standing, reads:
    /// the control.
    /// </summary>
    [Fact]
    public void ATitleInsideASentenceIsNoTitle()
    {
        var sentence = Read(L.Rtl(700, 560, "רשאי", "להוציא", "חשבונית", "מס", "בתיק", "מספר", "513456789"));
        Assert.Equal(IsraeliDocumentType.Unknown, sentence.DocumentType);
        Assert.Null(sentence.InvoiceNumber);
        Assert.Null(Read(L.Rtl(700, 560, "רשאי", "להוציא", "חשבונית", "מס", "2146")).InvoiceNumber);

        Assert.Equal(IsraeliDocumentType.Unknown, Read(L.Rtl(700, 560, "•", "חשבונית", "מס", "חייבת", "במע\"מ.")).DocumentType);
        Assert.Equal(IsraeliDocumentType.Unknown, Read(L.Rtl(700, 560, "חשבונית", "מס", "תישלח", "אליך", "בדואר")).DocumentType);
        Assert.Equal(IsraeliDocumentType.Unknown,
                     Read(L.Ltr(700, 72, "Please", "find", "attached", "the", "Tax", "Invoice", "for", "June")).DocumentType);
        Assert.Equal(IsraeliDocumentType.Unknown,
                     Read(L.Rtl(700, 560, "קבלה", "תינתן", "לכל", "לקוח", "בסיום", "העבודה")).DocumentType);

        var standing = Read(L.Rtl(700, 560, "חשבונית", "מס", "העתק", "2146"));
        Assert.Equal(IsraeliDocumentType.TaxInvoice, standing.DocumentType);
        Assert.Equal("2146", standing.InvoiceNumber);
    }

    /// <summary>
    /// A title with its qualifiers still stands: a period or a date after it, the export it is,
    /// a copy's marks before or after it — bracketed or not, as many as are printed — the number
    /// of the invoice a credit invoice credits, and the words of the next block a column away,
    /// which are not the title's.
    /// </summary>
    [Theory]
    [InlineData(IsraeliDocumentType.TaxInvoice, "חשבונית", "מס", "לחודש", "ינואר", "2026")]
    [InlineData(IsraeliDocumentType.TaxInvoice, "חשבונית", "מס", "לתקופה", "01/2026")]
    [InlineData(IsraeliDocumentType.TaxInvoice, "חשבונית", "מס", "ייצוא")]
    [InlineData(IsraeliDocumentType.TaxInvoice, "חשבונית", "מס", "2146", "תאריך", "01/01/2026")]
    [InlineData(IsraeliDocumentType.TaxInvoice, "חשבונית", "מס", L.Column, "שם", "הלקוח", "המלא")]
    [InlineData(IsraeliDocumentType.TaxInvoice, "העתק", "נאמן", "למקור", "חשבונית", "מס")]
    [InlineData(IsraeliDocumentType.TaxInvoice, "(העתק)", "חשבונית", "מס")]
    [InlineData(IsraeliDocumentType.TaxInvoice, "חשבונית", "מס", "העתק", "נאמן", "למקור")]
    [InlineData(IsraeliDocumentType.TaxInvoiceReceipt, "מקור", "-", "חשבונית", "מס/קבלה")]
    [InlineData(IsraeliDocumentType.TaxInvoiceReceipt, "חשבונית", "מס/קבלה", "(העתק)", "2146")]
    [InlineData(IsraeliDocumentType.CreditInvoice, "חשבונית", "מס", "זיכוי", "מס'", "55", "עבור", "חשבונית", "מס'", "1200")]
    public void ATitleWithItsQualifiersStands(IsraeliDocumentType expected, params string[] title)
    {
        Assert.Equal(expected, Read(L.Rtl(780, 560, title)).DocumentType);
    }

    /// <summary>
    /// A full stop ends a sentence only after a word: "No." ending a title's block, its number set a
    /// column away, is the number's word, and "2146." is a number. The title stands. (On a line read
    /// left to right, where the stop stays after what it follows.)
    /// </summary>
    [Theory]
    [InlineData("No.", L.Column, "2146")]
    [InlineData("2146.")]
    public void AFullStopAfterANumberOrItsWordEndsNoSentence(params string[] after)
    {
        var line = L.Ltr(700, 72, new[] { "Tax", "Invoice" }.Concat(after).ToArray());

        Assert.Equal(IsraeliDocumentType.TaxInvoice, Read(line).DocumentType);
    }

    /// <summary>
    /// A note above the title that names one (<c>המקור היווה חשבונית-מס בהתאם לחוק.</c>) does not
    /// decide the type; the title under it does. A header's title a column from the logo, the
    /// copy's marks under it, stands: the control.
    /// </summary>
    [Fact]
    public void ANoteNamingATitleDoesNotDecideTheType()
    {
        Assert.Equal(IsraeliDocumentType.TaxInvoiceReceipt,
                     Read(L.Rtl(760, 560, "המקור", "היווה", "חשבונית-מס", "בהתאם", "לחוק."),
                          L.Rtl(740, 560, "חשבונית", "מס", "קבלה", "2146")).DocumentType);
        Assert.Equal(IsraeliDocumentType.TaxInvoiceReceipt,
                     Read(L.Rtl(786, 560, "לפ", L.Column, "חשבונית", "מס", "/", "קבלה"),
                          L.Rtl(772, 140, "מקור", "/", "נאמן", "למקור")).DocumentType);
    }

    // One right-to-left line of two blocks: `right`, and a column to its left, `left`.
    private static PdfTextLine Beside(PdfTextLine right, PdfTextLine left)
    {
        var words = left.Words.Concat(right.Words).ToList();
        float x = words.Min(w => w.X);
        return new PdfTextLine(right.Text + " " + left.Text, x, right.Y, words.Max(w => w.X + w.Width) - x,
                               right.Height, words, isRtl: true);
    }

    /// <summary>
    /// A proforma's number under the allocation heading is its advance approval, no allocation
    /// number, and the document is no tax invoice.
    /// </summary>
    [Fact]
    public void AProformasApprovalIsNoAllocationNumber()
    {
        var inv = Read(
            L.Rtl(780, 560, "חשבון", "עסקה/פרופורמה"),
            L.Rtl(760, 560, "מספר", "הקצאה:", "123456789"));

        Assert.Equal(IsraeliDocumentType.Proforma, inv.DocumentType);
        Assert.Null(inv.AllocationNumber);
        Assert.Contains(InvoiceFindingCodes.NotATaxInvoice, Codes(inv));
    }

    /// <summary>
    /// The allocation query passes over an approval ("אישור הקצאה") on any document; the allocation
    /// heading is read, the control.
    /// </summary>
    [Fact]
    public void TheAllocationQueryPassesOverAnApproval()
    {
        var queries = IsraeliInvoiceExtractor.CreateQueries();
        var allocation = queries.Single(q => q.Key == IsraeliInvoiceExtractor.AllocationNumberKey);

        var approval = L.Page(0, L.Rtl(760, 560, "אישור", "הקצאה:", "123456789"));
        Assert.Equal(string.Empty, PdfFieldExtractor.Extract(new[] { approval }, new[] { allocation })[0].Value);

        var heading = L.Page(0, L.Rtl(760, 560, "מספר", "הקצאה:", "123456789"));
        Assert.Equal("123456789", PdfFieldExtractor.Extract(new[] { heading }, new[] { allocation })[0].Value);
    }

    /// <summary>
    /// The approval's number a tax invoice prints — "מס' אישור הקצאה", "מספר אישור הקצאה" — is its
    /// allocation number; the approval alone is passed over, the control.
    /// </summary>
    [Fact]
    public void TheApprovalsNumberIsTheAllocationNumber()
    {
        foreach (string number in new[] { "מס'", "מספר" })
        {
            var inv = Read(
                L.Rtl(780, 560, "חשבונית", "מס"),
                L.Rtl(760, 560, number, "אישור", "הקצאה", "487722494"));
            Assert.Equal("487722494", inv.AllocationNumber);
        }

        var approval = Read(
            L.Rtl(780, 560, "חשבונית", "מס"),
            L.Rtl(760, 560, "אישור", "הקצאה:", "487722494"));
        Assert.Null(approval.AllocationNumber);
    }

    /// <summary>
    /// A receipt printing no total is for what it says was paid: "סה"כ שולם ש"ח 1,300.00". On a tax
    /// invoice the same line is the part paid so far and no total, the control.
    /// </summary>
    [Fact]
    public void AReceiptsAmountPaidIsItsTotal()
    {
        IsraeliInvoice Paid(params string[] title) => Read(
            L.Rtl(780, 560, title),
            L.Rtl(740, 560, "סה\"כ", "שולם", "ש\"ח", "1,300.00"));

        var receipt = Paid("קבלה");
        Assert.Equal(IsraeliDocumentType.Receipt, receipt.DocumentType);
        Assert.Equal(1300m, receipt.Total);

        var invoice = Paid("חשבונית", "מס");
        Assert.Equal(IsraeliDocumentType.TaxInvoice, invoice.DocumentType);
        Assert.Null(invoice.Total);
    }

    // ================================================================
    //  The allocation number
    // ================================================================

    /// <summary>
    /// The Tax Authority's heading "הקצאה מספר:" (the reverse of the common "מספר הקצאה:") is read.
    /// </summary>
    [Fact]
    public void TheTaxAuthoritysAllocationHeadingIsRead()
    {
        var inv = ReadInvoiceWith(false, L.Rtl(676, 560, "הקצאה", "מספר:", "123456789"));

        Assert.Equal("123456789", inv.AllocationNumber);
        Assert.DoesNotContain(InvoiceFindingCodes.AllocationNumberMissing, Codes(inv));
    }

    /// <summary>
    /// The allocation number is the last nine digits of the long confirmation number, printed
    /// whole or in two runs in either order — the run opening with the year is the head; a run of
    /// nine is the number; a shorter run is kept for the validator to report.
    /// </summary>
    [Theory]
    [InlineData("20231220144357929354010074", "354010074")]
    [InlineData("202401111 22828322011010774", "011010774")]
    [InlineData("22828322011010774 202401111", "011010774")]
    [InlineData("123456789", "123456789")]
    [InlineData("1234567", "1234567")]
    [InlineData("12345", null)]
    public void TheAllocationNumberIsTheConfirmationsLastNineDigits(string value, string expected)
    {
        Assert.Equal(expected, IsraeliInvoiceExtractor.AllocationNumberIn(value));
    }

    /// <summary>
    /// "מספר הקצאה (חשבוניות ישראל): 136429430": the colon after the parenthesis closes the
    /// heading, and the number after it is read — whichever way round the producer's brackets
    /// read.
    /// </summary>
    [Theory]
    [InlineData("(חשבוניות", "ישראל):")]
    [InlineData(")חשבוניות", "ישראל(:")]
    public void AnAllocationHeadingsParenthesisIsPartOfTheLabel(string open, string close)
    {
        var inv = ReadInvoiceWith(false, L.Rtl(676, 560, "מספר", "הקצאה", open, close, "123456789"));

        Assert.Equal("123456789", inv.AllocationNumber);
        Assert.DoesNotContain(InvoiceFindingCodes.AllocationNumberMissing, Codes(inv));
    }

    /// <summary>
    /// The same heading the way Chromium draws it — the right-to-left run in a
    /// <c>/ReversedChars</c> sequence, each glyph decoding to its logical character, and the number
    /// in a run of its own — read from the PDF.
    /// </summary>
    [Fact]
    public void AnAllocationHeadingFromAChromiumRunIsRead()
    {
        byte[] pdf = new InvoicePdfBuilder()
            .ReversedChars(b => b.Rtl("מספר הקצאה (חשבוניות ישראל):", 500, 700))
            .AsciiRun("123456789", 240, 700, step: 5)
            .Build();

        var inv = IsraeliInvoiceExtractor.Extract(new MemoryStream(pdf));

        Assert.Equal("123456789", inv.AllocationNumber);
    }

    /// <summary>
    /// An invoice issued without an allocation number says its input VAT may not be deducted; it
    /// is noted, not warned about. The same invoice without the note is warned about.
    /// </summary>
    [Fact]
    public void TheInputVatNoteIsRead()
    {
        var noted = ReadInvoiceWith(false, L.Rtl(450, 560, "אין", "לנכות", "מס", "תשומות", "בגין", "חשבונית", "זו"));
        Assert.True(noted.StatesInputVatNotDeductible);
        Assert.Contains(InvoiceFindingCodes.InputVatNotDeductible, Codes(noted));
        Assert.DoesNotContain(InvoiceFindingCodes.AllocationNumberMissing, Codes(noted));

        var plain = ReadInvoiceWith(false);
        Assert.False(plain.StatesInputVatNotDeductible);
        Assert.Contains(InvoiceFindingCodes.AllocationNumberMissing, Codes(plain));
    }

    // ================================================================
    //  Identification numbers
    // ================================================================

    /// <summary>
    /// A foreign customer's VAT number is kept whole and is not checked as Israeli; an Israeli
    /// customer number with a wrong check digit is, the control.
    /// </summary>
    [Fact]
    public void AForeignCustomersNumberIsNotCheckedAsIsraeli()
    {
        IsraeliInvoice WithCustomer(string number) => Read(
            L.Ltr(760, 72, "VAT", "No:", Supplier),
            L.Ltr(730, 72, "Bill", "to:", "Example", "GmbH"),
            L.Ltr(716, 72, "VAT", "No:", number));

        var foreign = WithCustomer("DE123456789");
        Assert.Equal(Supplier, foreign.SupplierId);
        Assert.Equal("DE123456789", foreign.CustomerId);
        Assert.DoesNotContain(InvoiceFindingCodes.InvalidCustomerId, Codes(foreign));

        Assert.Contains(InvoiceFindingCodes.InvalidCustomerId, Codes(WithCustomer("300056784")));
    }

    /// <summary>
    /// One dealer's number printed with and without its leading zero is one number: no customer,
    /// and no "supplier is customer" error.
    /// </summary>
    [Fact]
    public void ANumberWithoutItsLeadingZeroIsTheSameNumber()
    {
        var inv = Read(
            L.Rtl(760, 560, "ע.מ.", "012345674"),
            L.Rtl(500, 560, "עוסק", "מורשה", "12345674"));

        Assert.Equal("012345674", inv.SupplierId);
        Assert.Null(inv.CustomerId);
        Assert.DoesNotContain(InvoiceFindingCodes.SupplierIsCustomer, Codes(inv));
    }

    /// <summary>
    /// A second line naming the customer ("מס' לקוח", the customer's number with the supplier)
    /// does not swallow the supplier's number under it once the customer is known.
    /// </summary>
    [Fact]
    public void ASecondCustomerCueDoesNotTakeTheSuppliersNumber()
    {
        var inv = Read(
            L.Rtl(760, 560, "לכבוד:", "לקוח", "בע\"מ"),
            L.Rtl(746, 560, "ח.פ.", Customer),
            L.Rtl(720, 560, "מס'", "לקוח:", "4711"),
            L.Rtl(706, 560, "ע.מ.", Supplier));

        Assert.Equal(Customer, inv.CustomerId);
        Assert.Equal(Supplier, inv.SupplierId);
    }

    /// <summary>
    /// "חתימת לקוח" (customer's signature) is no customer block: the supplier's number under it
    /// stays the supplier's.
    /// </summary>
    [Fact]
    public void ASignatureLineIsNoCustomerCue()
    {
        var inv = Read(
            L.Rtl(500, 560, "חתימת", "לקוח:"),
            L.Rtl(486, 560, "דוגמה", "בע\"מ", L.Column, "ע.מ.", Supplier));

        Assert.Equal(Supplier, inv.SupplierId);
        Assert.Null(inv.CustomerId);
    }

    /// <summary>
    /// The labels a fuel retailer and a car leasing company print their numbers under: a public
    /// company's <c>ח.צ</c> and a customer's <c>מס' תאגיד</c>, and <c>ח.פ לקוח</c>, the customer's
    /// company number, whose own word makes its line the customer's.
    /// </summary>
    [Fact]
    public void ACompanysAndACustomersOwnLabelsNameTheirNumbers()
    {
        var fuel = Read(
            L.Rtl(780, 560, "ח.צ:", Supplier),
            L.Rtl(760, 560, "לכבוד:"),
            L.Rtl(746, 560, "מס'", "תאגיד:", Customer));
        Assert.Equal(Supplier, fuel.SupplierId);
        Assert.Equal(Customer, fuel.CustomerId);

        var leasing = Read(
            L.Rtl(780, 560, "ע.מ.", Supplier),
            L.Rtl(700, 560, "ח.פ", "לקוח", L.Column, Customer));
        Assert.Equal(Supplier, leasing.SupplierId);
        Assert.Equal(Customer, leasing.CustomerId);
    }

    /// <summary>
    /// A toll road prints its own number in its contact line — post-office box, telephone, fax —
    /// under the customer's block, within the cue's reach: the number there is the supplier's, and
    /// the customer's is the one under its own label further down. Without the contact words the
    /// same line's number is claimed by the cue, the control; and "טל" alone is a name, no
    /// telephone.
    /// </summary>
    [Theory]
    [InlineData("ת.ד.", "טל׳:", Supplier, Customer)]
    [InlineData("רחוב", "טל", Customer, Supplier)]
    public void AnIssuersContactLineIsNotTheCustomers(string box, string phone, string supplier, string customer)
    {
        var inv = Read(
            L.Rtl(780, 560, "לכבוד:", "דוגמה", "בע\"מ"),
            L.Rtl(766, 560, "כתובת:", "הגפן", "4"),
            L.Rtl(752, 560, "כביש", "לדוגמה", "בע\"מ"),
            L.Rtl(738, 560, box, "12", "ח.פ.", "מס׳", Supplier, phone, "03-5550000"),
            L.Rtl(700, 560, "מס'", "זהות/תאגיד:", Customer));

        Assert.Equal(supplier, inv.SupplierId);
        Assert.Equal(customer, inv.CustomerId);
    }

    /// <summary>
    /// A telephone a column away is another block's: a fuel retailer prints the customer's empty
    /// <c>טל: -</c> beside its <c>מס' תאגיד:</c> on one line, and the number there is still the
    /// customer's. The supplier's number comes after it, so no second number stands in for a
    /// customer the cue failed to claim.
    /// </summary>
    [Fact]
    public void ATelephoneAColumnAwayIsAnotherBlocks()
    {
        var inv = Read(
            L.Rtl(760, 560, "לכבוד:"),
            L.Rtl(746, 560, "דוגמה", "בע\"מ"),
            L.Rtl(732, 560, "טל:", "-", L.Column, L.Column, "מס'", "תאגיד:", Customer),
            L.Rtl(600, 560, "ח.צ:", Supplier));

        Assert.Equal(Supplier, inv.SupplierId);
        Assert.Equal(Customer, inv.CustomerId);
    }

    /// <summary>
    /// A parking operator prints its name, the customer's block and the date side by side, and its
    /// own number the line after, under its name: that number is the supplier's, and the
    /// customer's is the one under the cue. A block beside the cue that names no company — the
    /// control — claims nothing, and the cue takes the first number as before.
    /// </summary>
    [Theory]
    [InlineData("ספק בע\"מ", Supplier, Customer)]
    [InlineData("רחוב הגפן", Customer, Supplier)]
    public void ANumberUnderTheIssuersNameBesideTheCueIsTheIssuers(string issuer, string supplier, string customer)
    {
        float[] columns = { 560, 380, 200 };
        var inv = Read(
            L.RtlRow(760, columns, issuer, "לכבוד: דוגמה בע\"מ", "תאריך: 01/01/2026"),
            L.RtlRow(746, columns, "ח.פ " + Supplier, "כתובת:"),
            L.RtlRow(732, columns, "רחוב 1", "ח.פ: " + Customer));

        Assert.Equal(supplier, inv.SupplierId);
        Assert.Equal(customer, inv.CustomerId);
    }

    /// <summary>
    /// A company's name after the addressee's cue in reading order is the customer's own, printed in
    /// the next column: the number under it is still the customer's.
    /// </summary>
    [Fact]
    public void ACompanysNameAfterTheCueIsTheCustomers()
    {
        float[] columns = { 560, 380 };
        var inv = Read(
            L.RtlRow(760, columns, "לכבוד:", "דוגמה בע\"מ"),
            L.RtlRow(746, columns, "", "ח.פ: " + Customer),
            L.Rtl(700, 560, "ע.מ.", Supplier));

        Assert.Equal(Customer, inv.CustomerId);
        Assert.Equal(Supplier, inv.SupplierId);
    }

    /// <summary>
    /// A label naming a rate the default table never held — <c>סה"כ סכומים ב-19% מע"מ</c>, a rate
    /// the law might set — is a subtotal's label when the caller's table holds the rate, and the
    /// rate is read from it. With the default table, the control, the label is no subtotal's and
    /// names no rate.
    /// </summary>
    [Fact]
    public void ARateLabelFollowsTheCallersVatTable()
    {
        var page = L.Page(0,
            L.Drawn(L.Rtl(740, 560, "סה\"כ", "סכומים", "ב-19%", "מע\"מ", L.Column, "100.00")),
            L.Rtl(720, 560, "סה\"כ", "מע\"מ", L.Column, "19.00"),
            L.Rtl(700, 560, "סה\"כ", "כולל", "מע\"מ", L.Column, "119.00"));
        var rates = new[] { new IsraeliDatedFigure(new DateTime(2027, 1, 1), 19m) }
            .Concat(IsraeliTaxRules.DefaultVatRates).ToList();

        var byTable = IsraeliInvoiceExtractor.Extract(new[] { page },
            new IsraeliInvoiceOptions { Validation = new IsraeliInvoiceValidationOptions { VatRates = rates } });
        var byDefault = IsraeliInvoiceExtractor.Extract(new[] { page });

        Assert.Equal((100.00m, 19m), (byTable.Subtotal, byTable.VatRate));
        Assert.Null(byDefault.Subtotal);
        Assert.Null(byDefault.VatRate);
    }

    /// <summary>
    /// A toll road's newer bills print the rate in the subtotal's label with its decimals —
    /// <c>סה"כ סכומים ב-18.00% מע"מ</c> — and it is the same label as <c>ב-18%</c>: the subtotal and
    /// its rate are read either way.
    /// </summary>
    [Theory]
    [InlineData("ב-18%")]
    [InlineData("ב-18.00%")]
    public void ARateInASubtotalsLabelMayCarryItsDecimals(string atRate)
    {
        var inv = Read(
            L.Drawn(L.Rtl(740, 560, "סה\"כ", "סכומים", atRate, "מע\"מ", L.Column, "1,000.00")),
            L.Rtl(720, 560, "סה\"כ", "מע\"מ", L.Column, "180.00"),
            L.Rtl(700, 560, "סה\"כ", "כולל", "מע\"מ", L.Column, "1,180.00"));

        Assert.Equal((1000.00m, 18m), (inv.Subtotal, inv.VatRate));
    }

    /// <summary>
    /// A bill across a rate change prints its sums at each rate on lines of their own: the subtotal
    /// is their sum, in parts, the invoice has no one rate, nothing is taken for an exempt part, and
    /// the VAT is checked as each part's at its rate. A VAT that is not, the control, is reported.
    /// </summary>
    [Theory]
    [InlineData("53.00", "353.00", false)]
    [InlineData("54.00", "354.00", true)]
    public void SubtotalsAtTwoRatesAreOneSubtotalInParts(string vat, string total, bool mismatch)
    {
        var inv = Read(
            L.Rtl(780, 560, "חשבונית", "מס", "מספר", "1001"),
            L.Drawn(L.Rtl(760, 560, "סה\"כ", "סכומים", "ב-17%", "מע\"מ", L.Column, "100.00")),
            L.Drawn(L.Rtl(740, 560, "סה\"כ", "סכומים", "ב-18%", "מע\"מ", L.Column, "200.00")),
            L.Rtl(720, 560, "סה\"כ", "מע\"מ", L.Column, vat),
            L.Rtl(700, 560, "סה\"כ", "כולל", "מע\"מ", L.Column, total));

        Assert.Equal(300.00m, inv.Subtotal);
        Assert.Equal(new[] { (17m, 100.00m), (18m, 200.00m) },
                     inv.RatedSubtotals.Select(p => (p.Rate, p.Amount)).OrderBy(p => p.Rate).ToArray());
        Assert.Null(inv.VatRate);
        Assert.Null(inv.ExemptAmount);
        Assert.Equal(mismatch, Codes(inv).Contains(InvoiceFindingCodes.VatMismatch));
    }

    // ================================================================
    //  An exempt part printed under no label
    // ================================================================

    /// <summary>
    /// A leasing invoice sums its taxed and its untaxed columns in one row and labels only the
    /// taxed sum: subtotal and VAT fall short of the total by the untaxed part, and the page prints
    /// the sum before VAT. The shortfall is the exempt part then — and only then: without the sum
    /// before VAT on the page, the control, nothing is made up.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnExemptPartIsTheShortfallThePageStates(bool printsSumBeforeVat)
    {
        var lines = new List<PdfTextLine>
        {
            L.Rtl(780, 560, "חשבונית", "מס", "1001"),
            L.Rtl(740, 560, "סה\"כ", "חייבים", "במע\"מ:", L.Column, "800.00"),
            L.Rtl(720, 560, "מע\"מ", "(18%)", L.Column, "144.00"),
            L.Rtl(700, 560, "סה\"כ", "לתשלום", "כולל", "מע\"מ", L.Column, "1,144.00"),
        };
        if (printsSumBeforeVat) lines.Insert(1, L.Rtl(760, 560, "רכב", "1", L.Column, "800.00", L.Column, "200.00", L.Column, "1,000.00"));

        var inv = Read(lines.ToArray());

        Assert.Equal((800.00m, 144.00m, 1144.00m), (inv.Subtotal, inv.Vat, inv.Total));
        Assert.Equal(printsSumBeforeVat ? 200.00m : (decimal?)null, inv.ExemptAmount);
    }

    /// <summary>
    /// A parking operator's totals row holds a taxed and an exempt column, its VAT and total on
    /// lines of their own: the column the VAT's rate fits is the subtotal, the other the exempt
    /// part. Without the rate printed, the control, the columns cannot be told apart and neither is
    /// read.
    /// </summary>
    [Theory]
    [InlineData("(18%)", 1000.00, 300.00)]
    [InlineData("", null, null)]
    public void ATaxedAndAnExemptColumnAreToldApartByTheRate(string rate, double? subtotal, double? exempt)
    {
        var vatLine = rate.Length > 0
            ? L.Rtl(720, 560, "מע\"מ", rate, L.Column, "180.00", "₪")
            : L.Rtl(720, 560, "מע\"מ", L.Column, "180.00", "₪");
        var inv = Read(
            L.Rtl(780, 560, "חשבונית", "מס", "1001"),
            L.Rtl(740, 560, "סה\"כ", L.Column, "1,000.00", "₪", L.Column, "300.00", "₪"),
            vatLine,
            L.Rtl(700, 560, "סה\"כ", "לתשלום", L.Column, "1,480.00", "₪"));

        Assert.Equal(1480.00m, inv.Total);
        Assert.Equal(subtotal == null ? null : (decimal?)(decimal)subtotal.Value, inv.Subtotal);
        Assert.Equal(exempt == null ? null : (decimal?)(decimal)exempt.Value, inv.ExemptAmount);
    }

    // ================================================================
    //  Amounts under column headings
    // ================================================================

    /// <summary>
    /// A monthly report heads its columns with the amounts' labels and states them only in its
    /// totals row, under its item rows: the subtotal, the VAT and the total are the totals row's
    /// cells under their headings — not an item row's — and the rate the one number its rate column
    /// prints, when the subtotal at that rate is the VAT. A totals row that does not add up, the
    /// control, is read for neither amount (its rate is not asserted: <c>NaN</c>); a rate column
    /// that is not the VAT's rate, or prints two rates, gives no rate (<c>-1</c>).
    /// </summary>
    [Theory]
    [InlineData("180.00", "18.00", "18.00", 1000.00, 180.00, 18.0)]
    [InlineData("170.00", "18.00", "18.00", null, null, double.NaN)]
    [InlineData("180.00", "17.00", "17.00", 1000.00, 180.00, -1.0)]
    [InlineData("180.00", "17.00", "18.00", 1000.00, 180.00, -1.0)]
    public void AmountsUnderColumnHeadingsAreReadInTheTotalsRow(string vatInTotals, string rate1, string rate2,
                                                                double? subtotal, double? vat, double rate)
    {
        float[] columns = { 560, 470, 380, 300, 220, 140 };
        var inv = Read(
            L.Rtl(800, 560, "דוח", "ריכוז", "חודשי"),
            L.RtlRow(760, columns, "שם הפריט", "כמות", "סה\"כ לפני מע\"מ", "אחוז מע\"מ", "סה\"כ מע\"מ", "סה\"כ כולל מע\"מ"),
            L.RtlRow(746, columns, "בנזין 95", "60.00", "600.00", rate1, "108.00", "708.00"),
            L.RtlRow(732, columns, "סולר", "40.00", "400.00", rate2, "72.00", "472.00"),
            L.RtlRow(718, columns, "סה\"כ כללי לתשלום:", "", "1,000.00", "", vatInTotals, "1,180.00"));

        Assert.Equal(subtotal == null ? null : (decimal?)(decimal)subtotal.Value, inv.Subtotal);
        Assert.Equal(vat == null ? null : (decimal?)(decimal)vat.Value, inv.Vat);
        if (rate == -1) Assert.Null(inv.VatRate);
        else if (!double.IsNaN(rate)) Assert.Equal((decimal)rate, inv.VatRate);
    }

    /// <summary>
    /// A rate's label names the rate, not the VAT: on a page printing no VAT amount, the
    /// <c>18.00</c> under <c>אחוז מע"מ</c> is no VAT. The subtotal and total around it are read,
    /// the potency control.
    /// </summary>
    [Fact]
    public void ARatesLabelIsNoVat()
    {
        var inv = Read(
            L.Rtl(740, 560, "סה\"כ", "לפני", "מע\"מ", L.Column, "200.00"),
            L.Rtl(726, 560, "אחוז", "מע\"מ", L.Column, "18.00"),
            L.Rtl(712, 560, "סה\"כ", "כולל", "מע\"מ", L.Column, "236.00"));

        Assert.Null(inv.Vat);
        Assert.Equal(200.00m, inv.Subtotal);
        Assert.Equal(236.00m, inv.Total);
    }

    // ================================================================
    //  A subtotal the VAT completes
    // ================================================================

    /// <summary>
    /// An invoice stacking <c>סה"כ</c>, <c>מע"מ</c> and <c>כולל מע"מ</c> prints its subtotal under a
    /// total's label: the line the VAT completes to the total is the subtotal. When the VAT does
    /// not complete it, no subtotal is made up.
    /// </summary>
    [Theory]
    [InlineData("18.00", 100.00)]
    [InlineData("17.00", null)]
    public void ATotalsLineTheVatCompletesIsTheSubtotal(string vat, double? expected)
    {
        var inv = Read(
            L.Rtl(740, 560, "סה\"כ", L.Column, "100.00"),
            L.Rtl(720, 560, "מע\"מ", "18%", L.Column, vat),
            L.Rtl(700, 560, "כולל", "מע\"מ", L.Column, "118.00"));

        Assert.Equal(118.00m, inv.Total);
        Assert.Equal(expected == null ? null : (decimal?)(decimal)expected.Value, inv.Subtotal);
    }
    /// number beside it on either side; a number a Latin prefix or suffix touches is foreign and
    /// kept whole; Israel's own prefix is dropped.
    /// </summary>
    [Theory]
    [InlineData("514286558 03-1234567", "514286558")]
    [InlineData("514286558 03 1234567", "514286558")]
    [InlineData("514 286 558", "514 286 558")]
    [InlineData("VAT No: DE123456789", "DE123456789")]
    [InlineData("ATU12345678", "ATU12345678")]
    [InlineData("NL123456789B01", "NL123456789B01")]
    [InlineData("IL514286558", "514286558")]
    [InlineData("514286558 VAT No DE123456789", "514286558")]
    [InlineData("12345678.90", null)]
    [InlineData("1234567890", null)]
    public void TheIdentifierInAValueIsFoundAmongItsNeighbours(string value, string expected)
    {
        Assert.Equal(expected, IsraeliInvoiceExtractor.IdentifierIn(value));
    }

    // ================================================================
    //  A file holding several documents
    // ================================================================

    // A month's file from a fuel retailer: a summary page naming its documents, a tax invoice a
    // page each, and the fuel report after them, whose own VAT line is the file's last.
    private static IReadOnlyList<PdfPageTextLayout> MonthsFile(bool withFirstInvoice)
    {
        var pages = new List<PdfPageTextLayout>
        {
            L.Page(0,
                L.Rtl(780, 560, "ריכוז", "רכישות"),
                L.Rtl(760, 560, "חיובים", "לפי", "חשבונית", "מס", "מספר", "1001", L.Column, "118.00"),
                L.Rtl(740, 560, "סה\"כ", L.Column, "51,295.28")),
        };
        if (withFirstInvoice)
            pages.Add(L.Page(1,
                L.Rtl(780, 560, "חשבונית", "מס", "1001"),
                L.Rtl(740, 560, "סה\"כ", "חייב", "במע\"מ", L.Column, "100.00"),
                L.Rtl(720, 560, "מע\"מ", "18%", L.Column, "18.00"),
                L.Rtl(700, 560, "סה\"כ", "כולל", "מע\"מ", L.Column, "118.00")));
        pages.Add(L.Page(2,
            L.Rtl(780, 560, "חשבונית", "מס", "1002"),
            L.Rtl(740, 560, "סה\"כ", "חייב", "במע\"מ", L.Column, "200.00"),
            L.Rtl(720, 560, "מע\"מ", "18%", L.Column, "36.00"),
            L.Rtl(700, 560, "סה\"כ", "כולל", "מע\"מ", L.Column, "236.00")));
        pages.Add(L.Page(3,
            L.Rtl(780, 560, "דוח", "ריכוז", "דלק"),
            L.Rtl(740, 560, "מע\"מ", L.Column, "9,999.99"),
            L.Rtl(700, 560, "ע.מ.", Supplier)));
        return pages;
    }

    /// <summary>
    /// A file of several documents is read for the first one whose amounts add up on its own page:
    /// the first invoice's subtotal, VAT, total and number together — not the last subtotal, the
    /// fuel report's VAT and the summary's total that the best match for each across the file
    /// would mix. Without the first invoice the second is the first that balances, which shows
    /// the page is chosen by its sums, not by its place.
    /// </summary>
    [Fact]
    public void AFileOfSeveralDocumentsIsReadForTheFirstThatBalances()
    {
        var inv = IsraeliInvoiceExtractor.Extract(MonthsFile(withFirstInvoice: true));
        Assert.Equal((100.00m, 18.00m, 118.00m), (inv.Subtotal, inv.Vat, inv.Total));
        Assert.Equal("1001", inv.InvoiceNumber);

        var second = IsraeliInvoiceExtractor.Extract(MonthsFile(withFirstInvoice: false));
        Assert.Equal((200.00m, 36.00m, 236.00m), (second.Subtotal, second.Vat, second.Total));
        Assert.Equal("1002", second.InvoiceNumber);
    }

    /// <summary>
    /// Every document of the file, one invoice each, in page order: the first invoice with the
    /// summary page before it, the second with the fuel report after it — each its own number and
    /// amounts, the second's VAT not the fuel report's. The pages after the last document's totals
    /// are its own: the number the fuel report prints is the second's, and not the first's.
    /// </summary>
    [Fact]
    public void EveryDocumentOfAFileIsReadOnItsOwn()
    {
        var invoices = IsraeliInvoiceExtractor.ExtractAll(MonthsFile(withFirstInvoice: true));

        Assert.Equal(2, invoices.Count);
        Assert.Equal(("1001", 100.00m, 18.00m, 118.00m),
                     (invoices[0].InvoiceNumber, invoices[0].Subtotal, invoices[0].Vat, invoices[0].Total));
        Assert.Equal(("1002", 200.00m, 36.00m, 236.00m),
                     (invoices[1].InvoiceNumber, invoices[1].Subtotal, invoices[1].Vat, invoices[1].Total));
        Assert.Equal(Supplier, invoices[1].SupplierId);
        Assert.Null(invoices[0].SupplierId);
    }

    /// <summary>
    /// A subtotal taxed at 0% beside the one at 18% is zero-rated, the exempt part: the page adds
    /// up with it, and its amount is the exempt one — so a file holding such an invoice and a
    /// second one is read as two documents. Without the zero-rated line, the control, the total is
    /// short and the page does not add up.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASubtotalAtZeroPercentIsTheExemptPart(bool zeroRatedLine)
    {
        var lines = new List<PdfTextLine> { L.Rtl(780, 560, "חשבונית", "מס", "1001") };
        if (zeroRatedLine) lines.Add(L.Rtl(760, 560, "סה\"כ", "חייב", "במע\"מ", "%", "0", L.Column, "50.00"));
        lines.Add(L.Rtl(740, 560, "סה\"כ", "חייב", "במע\"מ", "%", "18.00", L.Column, "100.00"));
        lines.Add(L.Rtl(720, 560, "מע\"מ", "%", "18.00", L.Column, "18.00"));
        lines.Add(L.Rtl(700, 560, "סה\"כ", "כולל", "מע\"מ", L.Column, "168.00"));
        var second = L.Page(1,
            L.Rtl(780, 560, "חשבונית", "מס", "1002"),
            L.Rtl(740, 560, "סה\"כ", "חייב", "במע\"מ", L.Column, "200.00"),
            L.Rtl(720, 560, "מע\"מ", "18%", L.Column, "36.00"),
            L.Rtl(700, 560, "סה\"כ", "כולל", "מע\"מ", L.Column, "236.00"));

        var invoices = IsraeliInvoiceExtractor.ExtractAll(new[] { L.Page(0, lines.ToArray()), second });

        if (zeroRatedLine)
        {
            Assert.Equal(2, invoices.Count);
            Assert.Equal((100.00m, 50.00m, 18.00m, 168.00m),
                         (invoices[0].Subtotal, invoices[0].ExemptAmount, invoices[0].Vat, invoices[0].Total));
        }
        else
        {
            Assert.Single(invoices);
        }
    }

    /// <summary>
    /// A file of one document — one page that adds up — is one invoice, the one <see
    /// cref="IsraeliInvoiceExtractor.Extract(IReadOnlyList{PdfPageTextLayout}, IsraeliInvoiceOptions)"/>
    /// reads.
    /// </summary>
    [Fact]
    public void AFileOfOneDocumentIsOneInvoice()
    {
        var pages = MonthsFile(withFirstInvoice: false);

        var all = IsraeliInvoiceExtractor.ExtractAll(pages);
        var one = IsraeliInvoiceExtractor.Extract(pages);

        Assert.Single(all);
        Assert.Equal((one.InvoiceNumber, one.Subtotal, one.Vat, one.Total),
                     (all[0].InvoiceNumber, all[0].Subtotal, all[0].Vat, all[0].Total));
        Assert.Equal("1002", all[0].InvoiceNumber);
    }

    /// <summary>
    /// The number is the one on the page the amounts are read from, before one a summary page
    /// names: the second invoice's file still names the first invoice's number on its summary.
    /// </summary>
    [Fact]
    public void TheNumberIsTheOneOnThePageTheAmountsAreReadFrom()
    {
        var pages = MonthsFile(withFirstInvoice: false);

        var inv = IsraeliInvoiceExtractor.Extract(pages);

        Assert.Equal("1002", inv.InvoiceNumber);
        // The summary's sentence alone names 1001: read from it, the number would be that.
        Assert.Equal("1001", IsraeliInvoiceExtractor.Extract(new[] { pages[0] }).InvoiceNumber);
    }
}

/// <summary>
/// The field search's budget reaches the invoice extractor, which says it stopped short.
/// </summary>
[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public class IsraeliInvoiceBudgetTests
{
    [Fact]
    public void AnExtractionStoppedByTheBudgetSaysSo()
    {
        var page = IsraeliInvoiceExtractorTests.InvoicePage();
        try
        {
            PdfLimits.MaxFieldExtractionWork = 50;
            var cut = IsraeliInvoiceExtractor.Extract(new[] { page });
            Assert.Contains(cut.ExtractionFindings, f => f.Code == InvoiceFindingCodes.ExtractionIncomplete);
            Assert.Contains("MaxFieldExtractionWork", IsraeliInvoiceExtractor.LastError, StringComparison.Ordinal);

            PdfLimits.ResetToDefaults();
            var whole = IsraeliInvoiceExtractor.Extract(new[] { page });
            Assert.DoesNotContain(whole.ExtractionFindings, f => f.Code == InvoiceFindingCodes.ExtractionIncomplete);
            Assert.Null(IsraeliInvoiceExtractor.LastError);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }
}
