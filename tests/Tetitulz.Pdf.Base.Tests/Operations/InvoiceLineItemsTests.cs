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
/// The item table's decisions: where the items end, which header cell is which column, how a
/// table continues onto the next page, and when reading stops — each beside its control.
/// </summary>
public class InvoiceLineItemsTests
{
    // Right edges of a four-column Hebrew table: description, quantity, unit price, amount.
    private static readonly float[] Four = { 560f, 440f, 360f, 260f };
    // Right edges of a five-column one: description, quantity, unit price, discount, amount.
    private static readonly float[] Five = { 560f, 460f, 380f, 300f, 220f };

    private static IList<InvoiceLineItem> Items(params PdfPageTextLayout[] pages)
        => IsraeliInvoiceExtractor.Extract(pages).LineItems;

    private static IList<InvoiceLineItem> Items(params PdfTextLine[] lines) => Items(L.Page(0, lines));

    // ================================================================
    //  Where the items end
    // ================================================================

    /// <summary>
    /// A description that contains a totals word — "vat" in "Private" and "Renovation", <c>מע"מ</c>
    /// in "preparing the VAT report" — is an item. A row whose label opens with "Total" ends the
    /// items, the control.
    /// </summary>
    [Fact]
    public void ADescriptionContainingATotalsWordIsAnItem()
    {
        float[] two = { 72f, 300f };
        var english = Items(
            L.LtrRow(640, two, "Description", "Amount"),
            L.LtrRow(626, two, "Private lesson", "200.00"),
            L.LtrRow(612, two, "Renovation works", "5,000.00"),
            L.LtrRow(598, two, "Total", "5,200.00"));
        Assert.Equal(new[] { "Private lesson", "Renovation works" }, english.Select(i => i.Description));

        var hebrew = Items(
            L.RtlRow(640, Four, "תיאור", "כמות", "מחיר", "סה\"כ"),
            L.RtlRow(626, Four, "הכנת דוח מע\"מ", "", "", "800.00"),
            L.RtlRow(612, Four, "ייעוץ", "1", "200.00", "200.00"),
            L.RtlRow(598, Four, "סה\"כ", "", "", "1,000.00"));
        Assert.Equal(new[] { "הכנת דוח מע\"מ", "ייעוץ" }, hebrew.Select(i => i.Description));
        Assert.Equal(800m, hebrew[0].Amount);
    }

    /// <summary>
    /// A header's line wrapped onto a second row — <c>ליחידה</c> under the price, <c>כולל מע"מ</c>
    /// under the total; "(incl. VAT)" under "Amount" — is the header's, not the totals: the items
    /// under it are read.
    /// </summary>
    [Fact]
    public void AHeaderWrappedOntoASecondLineIsNoTotalsRow()
    {
        var hebrew = Items(
            L.RtlRow(640, Four, "תיאור", "כמות", "מחיר", "סה\"כ"),
            L.RtlRow(628, Four, "", "", "ליחידה", "כולל מע\"מ"),
            L.RtlRow(612, Four, "ייעוץ", "2", "100.00", "236.00"),
            L.RtlRow(598, Four, "פיתוח", "1", "50.00", "59.00"));
        Assert.Equal(new decimal?[] { 236m, 59m }, hebrew.Select(i => i.Amount));

        // "Total" over "VAT incl.": the wrapped line opens with a totals word, and is still the header's.
        float[] four = { 72f, 250f, 330f, 430f };
        var english = Items(
            L.LtrRow(640, four, "Description", "Qty", "Unit price", "Total"),
            L.LtrRow(628, four, "", "", "(excl. VAT)", "VAT incl."),
            L.LtrRow(612, four, "Consulting", "2", "100.00", "236.00"),
            L.LtrRow(598, four, "Development", "1", "50.00", "59.00"));
        Assert.Equal(new decimal?[] { 236m, 59m }, english.Select(i => i.Amount));
    }

    /// <summary>
    /// A subtotal carried to the next page (<c>סה"כ להעברה</c>) does not end the items, and the one
    /// brought from the last (<c>סה"כ מהעברה</c>) under a repeated header is no item: three items,
    /// once each. The real totals on page 2 still end them.
    /// </summary>
    [Fact]
    public void ACarriedSubtotalNeitherEndsNorJoinsTheItems()
    {
        var page1 = L.Page(0,
            L.RtlRow(640, Four, "תיאור", "כמות", "מחיר", "סה\"כ"),
            L.RtlRow(626, Four, "ייעוץ", "2", "100.00", "200.00"),
            L.RtlRow(612, Four, "פיתוח", "1", "50.00", "50.00"),
            L.RtlRow(598, Four, "סה\"כ להעברה", "", "", "250.00"));
        var page2 = L.Page(1,
            L.RtlRow(640, Four, "תיאור", "כמות", "מחיר", "סה\"כ"),
            L.RtlRow(626, Four, "סה\"כ מהעברה", "", "", "250.00"),
            L.RtlRow(612, Four, "הדרכה", "3", "10.00", "30.00"),
            L.RtlRow(598, Four, "סה\"כ", "", "", "280.00"),
            L.RtlRow(584, Four, "הובלה", "1", "99.00", "99.00"));

        var items = Items(page1, page2);

        Assert.Equal(new[] { "ייעוץ", "פיתוח", "הדרכה" }, items.Select(i => i.Description));
    }

    // ================================================================
    //  Which header cell is which column
    // ================================================================

    /// <summary>
    /// "Total after discount" is the amount, not the discount; the discount column beside it is
    /// the discount. A "total discount" column alone stays the discount, the control.
    /// </summary>
    [Fact]
    public void AnAmountAfterDiscountIsTheAmountNotTheDiscount()
    {
        var after = Items(
            L.RtlRow(640, Five, "תיאור", "כמות", "מחיר", "הנחה %", "סה\"כ לאחר הנחה"),
            L.RtlRow(626, Five, "ייעוץ", "2", "100.00", "10%", "180.00"),
            L.RtlRow(612, Five, "פיתוח", "1", "50.00", "0%", "50.00"));
        Assert.Equal(new decimal?[] { 180m, 50m }, after.Select(i => i.Amount));
        Assert.Equal("10%", after[0].Discount);

        // Alone, with no discount column to take that role first, it is still the amount.
        var alone = Items(
            L.RtlRow(640, Four, "תיאור", "כמות", "מחיר", "סה\"כ לאחר הנחה"),
            L.RtlRow(626, Four, "ייעוץ", "2", "100.00", "180.00"),
            L.RtlRow(612, Four, "פיתוח", "1", "50.00", "50.00"));
        Assert.Equal(new decimal?[] { 180m, 50m }, alone.Select(i => i.Amount));
        Assert.Null(alone[0].Discount);

        var discount = Items(
            L.RtlRow(640, Five, "תיאור", "כמות", "מחיר", "סה\"כ הנחה", "סה\"כ"),
            L.RtlRow(626, Five, "ייעוץ", "2", "100.00", "20.00", "180.00"),
            L.RtlRow(612, Five, "פיתוח", "1", "50.00", "0.00", "50.00"));
        Assert.Equal("20.00", discount[0].Discount);
        Assert.Equal(180m, discount[0].Amount);
    }

    /// <summary>
    /// An English header's "VAT Rate" column is no unit price, whichever side of "Unit Price" it
    /// sits: the unit price is the price column's.
    /// </summary>
    [Fact]
    public void AnEnglishHeadersVatRateIsNoPrice()
    {
        float[] five = { 72f, 220f, 280f, 380f, 460f };
        var items = Items(
            L.LtrRow(640, five, "Description", "Qty", "Unit Price", "VAT Rate", "Total"),
            L.LtrRow(626, five, "Consulting", "2", "100.00", "18%", "236.00"),
            L.LtrRow(612, five, "Development", "1", "50.00", "18%", "59.00"));

        Assert.Equal(new decimal?[] { 100m, 50m }, items.Select(i => i.UnitPrice));
        Assert.Equal(new decimal?[] { 236m, 59m }, items.Select(i => i.Amount));
    }

    /// <summary>
    /// <c>פרטים</c> (details) is a description header: the description is read and a wrapped line
    /// joins it; with only an amount column beside it, it still makes the header.
    /// </summary>
    [Fact]
    public void TheHebrewHeaderDetailsIsADescription()
    {
        var items = Items(
            L.RtlRow(640, Four, "פרטים", "כמות", "מחיר", "סה\"כ"),
            L.RtlRow(626, Four, "ייעוץ", "2", "100.00", "200.00"),
            L.RtlRow(612, Four, "בשעות הערב", "", "", ""),
            L.RtlRow(598, Four, "פיתוח", "1", "50.00", "50.00"));
        Assert.Equal(new[] { "ייעוץ בשעות הערב", "פיתוח" }, items.Select(i => i.Description));

        float[] two = { 560f, 300f };
        var amountOnly = Items(
            L.RtlRow(640, two, "פרטים", "סכום"),
            L.RtlRow(626, two, "ייעוץ", "200.00"),
            L.RtlRow(612, two, "פיתוח", "50.00"));
        Assert.Equal(new decimal?[] { 200m, 50m }, amountOnly.Select(i => i.Amount));
    }

    // ================================================================
    //  A row's cells, whatever runs through them
    // ================================================================

    // Right edges of a wide four-column Hebrew table.
    private static readonly float[] Wide = { 560f, 400f, 330f, 250f };

    // A line of words laid at the positions given, glyphs and all.
    private static PdfTextLine Row(float y, bool rtl, params (string Text, float X)[] words)
    {
        var laid = words.Select(w => L.Word(w.Text, w.X, y)).ToList();
        float left = laid.Min(w => w.X), right = laid.Max(w => w.X + w.Width);
        return new PdfTextLine(string.Join(" ", words.Select(w => w.Text)), left, y, right - left, L.Height, laid, rtl);
    }

    /// <summary>
    /// A block of fields above the table — a row of labels over a row of values — puts a column
    /// separator through the item table's description column: through a word of
    /// "שירותי אינטגרציה" and in the word space of "אפיון ובינה עסקית". The descriptions are read
    /// whole, as the same table alone reads them, the control.
    /// </summary>
    [Fact]
    public void FieldsAboveTheTableSplitNoDescription()
    {
        float[] fields = { 560f, 452f };   // gaps centred on x = 494
        PdfTextLine[] Table() => new[]
        {
            L.RtlRow(680, Wide, "תיאור", "כמות", "מחיר", "סה\"כ"),
            L.RtlRow(666, Wide, "שירותי אינטגרציה", "2", "100.00", "200.00"),   // אינטגרציה spans 466–520
            L.RtlRow(652, Wide, "אפיון ובינה עסקית", "1", "50.00", "50.00"),     // a word space at 492–496
            L.RtlRow(638, Wide, "סה\"כ", "", "", "250.00"),
        };
        var whole = new[] { "שירותי אינטגרציה", "אפיון ובינה עסקית" };
        Assert.Equal(whole, Items(Table()).Select(i => i.Description));

        var under = Items(new[] { L.RtlRow(740, fields, "מסמך", "תאריך"), L.RtlRow(726, fields, "3721", "26/09/25") }
            .Concat(Table()).ToArray());
        Assert.Equal(whole, under.Select(i => i.Description));
        Assert.Equal(new decimal?[] { 200m, 50m }, under.Select(i => i.Amount));
    }

    /// <summary>
    /// The English twin: a separator through "Consulting" and "Development" leaves each
    /// description whole — the reader finds the cell that covers the description's column,
    /// wherever its first column is.
    /// </summary>
    [Fact]
    public void FieldsAboveAnEnglishTableSplitNoDescription()
    {
        float[] fields = { 72f, 150f };   // gaps centred on x = 129
        float[] four = { 72f, 250f, 330f, 430f };
        PdfTextLine[] Table() => new[]
        {
            L.LtrRow(680, four, "Item", "Qty", "Price", "Total"),
            L.LtrRow(666, four, "Consulting services", "2", "100.00", "200.00"),
            L.LtrRow(652, four, "Development hours", "1", "50.00", "50.00"),
            L.LtrRow(638, four, "Total", "", "", "250.00"),
        };
        var whole = new[] { "Consulting services", "Development hours" };
        Assert.Equal(whole, Items(Table()).Select(i => i.Description));

        var under = Items(new[] { L.LtrRow(740, fields, "Number", "Date"), L.LtrRow(726, fields, "372100", "26/09/25") }
            .Concat(Table()).ToArray());
        Assert.Equal(whole, under.Select(i => i.Description));
    }

    /// <summary>
    /// A price header overhanging the unit column beside it ("מחיר יח'" over "יח'") keeps to the
    /// price's column: the unit column's own header holds the other. The unit prices are read, as
    /// under a header that does not overhang, the control. With no header of its own, the unit
    /// column is the overhanging header's too, and the price is the cell most under the header.
    /// </summary>
    [Fact]
    public void AnOverhangingHeaderKeepsItsColumn()
    {
        float[] values = { 560f, 440f, 380f, 300f, 230f };
        float[] overhanging = { 560f, 440f, 380f, 336f, 230f };
        PdfTextLine[] Table(float[] header, string unit = "יח'") => new[]
        {
            L.RtlRow(680, header, "תיאור", "כמות", unit, "מחיר יח'", "סה\"כ"),
            L.RtlRow(666, values, "ייעוץ", "2", "שעה", "100.00", "200.00"),
            L.RtlRow(652, values, "פיתוח", "1", "חודש", "50.00", "50.00"),
            L.RtlRow(638, values, "סה\"כ", "", "", "", "250.00"),
        };

        Assert.Equal(new decimal?[] { 100m, 50m }, Items(Table(values)).Select(i => i.UnitPrice));
        Assert.Equal(new decimal?[] { 100m, 50m }, Items(Table(overhanging)).Select(i => i.UnitPrice));
        Assert.Equal(new decimal?[] { 100m, 50m }, Items(Table(overhanging, unit: "")).Select(i => i.UnitPrice));
    }

    /// <summary>
    /// A header over two columns — "מחיר", crossed by a separator the fields above put right of the
    /// prices — reads the values under either: the prices sit in its second column. Without the
    /// fields, the header is one column's, the control.
    /// </summary>
    [Fact]
    public void AHeaderOverTwoColumnsReadsTheValuesUnderEither()
    {
        float[] values = { 560f, 440f, 350f, 260f };
        PdfTextLine[] Table() => new[]
        {
            L.RtlRow(680, Four, "תיאור", "כמות", "מחיר", "סה\"כ"),   // מחיר spans 336–360
            L.RtlRow(666, values, "ייעוץ", "2", "100.00", "200.00"),
            L.RtlRow(652, values, "פיתוח", "1", "50.00", "50.00"),
            L.RtlRow(638, values, "סה\"כ", "", "", "250.00"),
        };
        // Two rows whose gaps are centred on x = 355.
        var fields = new[] { Row(740, true, ("מסמך", 360), ("תאריך", 320)), Row(726, true, ("3721", 360), ("26/09/25", 302)) };

        Assert.Equal(new decimal?[] { 100m, 50m }, Items(Table()).Select(i => i.UnitPrice));
        Assert.Equal(new decimal?[] { 100m, 50m }, Items(fields.Concat(Table()).ToArray()).Select(i => i.UnitPrice));
    }

    /// <summary>
    /// A note across the table between two items — "* המחיר כולל משלוח … בתוך 30 ימי עסקים" — is
    /// no item: a number is read from no cell that covers another role's columns too, nor from a
    /// sentence. The two items keep their amounts.
    /// </summary>
    [Fact]
    public void ANoteAcrossTheTableIsNoItem()
    {
        var items = Items(
            L.RtlRow(680, Four, "תיאור", "כמות", "מחיר", "סה\"כ"),
            L.RtlRow(666, Four, "ייעוץ", "2", "100.00", "200.00"),
            L.Rtl(652, 560, "*", "המחיר", "כולל", "משלוח", "לכל", "הארץ", "בתוך", "30", "ימי", "עסקים"),
            L.RtlRow(638, Four, "פיתוח", "1", "50.00", "50.00"),
            L.RtlRow(624, Four, "סה\"כ", "", "", "250.00"));

        Assert.Equal(new decimal?[] { 200m, 50m }, items.Select(i => i.Amount));
        Assert.Equal(new decimal?[] { 2m, 1m }, items.Select(i => i.Quantity));
    }

    /// <summary>
    /// Each half of that rule on its own: a quantity and a price set a word space apart are one cell
    /// across both columns, and neither is read from it — "2 1,150.00" would give 1,150 for both; a
    /// remark in the amount's column, "ראה הערה 3" — two words with letters — is no amount of 3.
    /// The rest of each row is read.
    /// </summary>
    [Fact]
    public void ANumberIsReadFromNoCellAcrossTwoRolesNorFromAPhrase()
    {
        var items = Items(
            L.RtlRow(680, Four, "תיאור", "כמות", "מחיר", "סה\"כ"),
            Row(666, true, ("ייעוץ", 530), ("2", 434), ("1,150.00", 382), ("2,300.00", 212)),
            L.RtlRow(652, Four, "פיתוח", "1", "50.00", "ראה הערה 3"),
            L.RtlRow(638, Four, "הדרכה", "3", "10.00", "30.00"),
            L.RtlRow(624, Four, "סה\"כ", "", "", "2,380.00"));

        Assert.Equal(new[] { "ייעוץ", "פיתוח", "הדרכה" }, items.Select(i => i.Description));
        Assert.Equal(new decimal?[] { null, 1m, 3m }, items.Select(i => i.Quantity));
        Assert.Equal(new decimal?[] { null, 50m, 10m }, items.Select(i => i.UnitPrice));
        Assert.Equal(new decimal?[] { 2300m, null, 30m }, items.Select(i => i.Amount));
    }

    /// <summary>
    /// A cell mixing Hebrew and Latin reads in its table's direction, not its own first letter's:
    /// "שירותי DevOps" (as many letters of each), "הטמעת מערכת CRM", and "רישיון Microsoft
    /// Business Premium" (mostly Latin, its Latin run laid left to right as a renderer lays it) read
    /// right to left in a Hebrew table.
    /// </summary>
    [Fact]
    public void ACellMixingHebrewAndLatinReadsInTheTablesDirection()
    {
        float[] columns = { 560f, 300f, 220f, 140f };
        // "רישיון" at 524–560, then the Latin run 368–520, then the quantity, price and amount.
        var latinHeavy = Row(638, true, ("רישיון", 524), ("Microsoft", 368), ("Business", 426), ("Premium", 478),
                             ("1", 294), ("300.00", 184), ("300.00", 104));

        var items = Items(
            L.RtlRow(680, columns, "תיאור", "כמות", "מחיר", "סה\"כ"),
            L.RtlRow(666, columns, "שירותי DevOps", "2", "100.00", "200.00"),
            L.RtlRow(652, columns, "הטמעת מערכת CRM", "1", "50.00", "50.00"),
            latinHeavy,
            L.RtlRow(624, columns, "סה\"כ", "", "", "550.00"));

        Assert.Equal(new[] { "שירותי DevOps", "הטמעת מערכת CRM", "רישיון Microsoft Business Premium" },
                     items.Select(i => i.Description));

        // An English table's mixed cell reads left to right, Hebrew and all.
        float[] four = { 72f, 250f, 330f, 430f };
        var english = Items(
            L.LtrRow(680, four, "Description", "Qty", "Price", "Total"),
            L.LtrRow(666, four, "ACME שירותים", "2", "100.00", "200.00"),
            L.LtrRow(652, four, "Consulting", "1", "50.00", "50.00"),
            L.LtrRow(638, four, "Total", "", "", "250.00"));
        Assert.Equal(new[] { "ACME שירותים", "Consulting" }, english.Select(i => i.Description));
    }

    /// <summary>
    /// A ruled table's cells read in its direction too: "שירותי DevOps" in a Hebrew grid.
    /// </summary>
    [Fact]
    public void ACellOfARuledTableReadsInTheTablesDirection()
    {
        var grid = new PdfPageGraphics(0,
            new[] { new HRuling(100, 560, 630, 1), new HRuling(100, 560, 616, 1), new HRuling(100, 560, 602, 1), new HRuling(100, 560, 589, 1) },
            new[] { new VRuling(100, 589, 630, 1), new VRuling(300, 589, 630, 1), new VRuling(560, 589, 630, 1) },
            null);
        float[] two = { 555f, 295f };

        var items = InvoiceLineItems.Read(new[] { L.Page(0,
            L.RtlRow(619, two, "תיאור", "סכום"),
            L.RtlRow(605, two, "שירותי DevOps", "100.00"),
            L.RtlRow(592, two, "סה\"כ", "100.00")) }, new[] { grid });

        Assert.Equal("שירותי DevOps", Assert.Single(items).Description);
    }

    /// <summary>
    /// A grid ruling a car lessor's fields line and its item header into one row joins each header
    /// cell to the field above it — "מחיר 54123", "סה"כ 31/03/2026" — and a header cell holding a
    /// figure names nothing, so the grid gives no item. The page's text lines are then read as rows:
    /// the items are read. The page with no rulings reads the same, the control.
    /// </summary>
    [Fact]
    public void AGridRulingFieldsWithTheHeaderIsReadByItsLines()
    {
        // Columns ruled at 565 | 450 | 370 | 280 | 150; rows at 712 | 680 | 666 | 652 | 638 — the
        // fields' line and the header's in the one ruled row 712–680.
        float[] cols = { 565f, 450f, 370f, 280f, 150f };
        float[] rows = { 712f, 680f, 666f, 652f, 638f };
        var box = new PdfPageGraphics(0,
            rows.Select(y => new HRuling(150, 565, y, 1)).ToArray(),
            cols.Select(x => new VRuling(x, 638, 712, 1)).ToArray(),
            null);
        var page = L.Page(0,
            L.RtlRow(700, Four, "לכבוד", "", "54123", "31/03/2026"),
            L.RtlRow(686, Four, "תיאור", "כמות", "מחיר", "סה\"כ"),
            L.RtlRow(669, Four, "ליסינג", "1", "1,500.00", "1,500.00"),
            L.RtlRow(655, Four, "ביטוח", "1", "320.00", "320.00"),
            L.RtlRow(641, Four, "סה\"כ", "", "", "1,820.00"));

        var ruled = InvoiceLineItems.Read(new[] { page }, new[] { box });
        Assert.Equal(new decimal?[] { 1500m, 320m }, ruled.Select(i => i.Amount));
        Assert.Equal(new[] { "ליסינג", "ביטוח" }, ruled.Select(i => i.Description));

        var plain = InvoiceLineItems.Read(new[] { page }, null);
        Assert.Equal(new decimal?[] { 1500m, 320m }, plain.Select(i => i.Amount));
    }

    /// <summary>
    /// On a ruled page, a header word whose centre lies 2 pt from a vertical line — a box's side,
    /// elsewhere on the page, that the grid takes for a column — is in one cell, so the description
    /// is read, not also in the sliver beside the line, which the reader would take. A header
    /// clear of the line reads the same, the control.
    /// </summary>
    [Fact]
    public void AWordNearARulingIsReadInOneCell()
    {
        var grid = new PdfPageGraphics(0,
            new[] { new HRuling(136, 456, 630, 1), new HRuling(136, 456, 616, 1), new HRuling(136, 456, 602, 1), new HRuling(136, 456, 589, 1) },
            new[] { new VRuling(136, 589, 630, 1), new VRuling(456, 589, 630, 1), new VRuling(332, 640, 660, 1), new VRuling(441, 640, 660, 1) },
            null);
        float[] two = { 454f, 260f };
        List<InvoiceLineItem> Read(float headerRight) => InvoiceLineItems.Read(new[] { L.Page(0,
            L.RtlRow(619, new[] { headerRight, 260f }, "תיאור", "סכום"),
            L.RtlRow(605, two, "תשלומים עבור נסיעות", "107.24"),
            L.RtlRow(592, two, "סה\"כ", "107.24")) }, new[] { grid });

        Assert.Equal("תשלומים עבור נסיעות", Assert.Single(Read(448f)).Description);   // centre 433

        var item = Assert.Single(Read(454f));                                         // centre 439
        Assert.Equal("תשלומים עבור נסיעות", item.Description);
        Assert.Equal(107.24m, item.Amount);
    }

    /// <summary>
    /// A grid ruled between its rows and not between its columns is one column to the lines: the
    /// text says where its columns are, and the item is read. The grid with its column rule reads
    /// the same, the control.
    /// </summary>
    [Fact]
    public void ARuledGridOfOneColumnIsReadByItsText()
    {
        PdfPageGraphics Grid(params float[] columnRules) => new PdfPageGraphics(0,
            new[] { new HRuling(100, 560, 630, 1), new HRuling(100, 560, 616, 1), new HRuling(100, 560, 602, 1), new HRuling(100, 560, 589, 1) },
            columnRules.Select(x => new VRuling(x, 589, 630, 1)).ToArray(),
            null);
        float[] two = { 555f, 295f };
        List<InvoiceLineItem> Read(PdfPageGraphics grid) => InvoiceLineItems.Read(new[] { L.Page(0,
            L.RtlRow(619, two, "תיאור", "סכום"),
            L.RtlRow(605, two, "ייעוץ", "100.00"),
            L.RtlRow(592, two, "סה\"כ", "100.00")) }, new[] { grid });

        var ruled = Assert.Single(Read(Grid(100, 300, 560)));
        Assert.Equal(("ייעוץ", (decimal?)100m), (ruled.Description, ruled.Amount));
        var rows = Assert.Single(Read(Grid(100, 560)));
        Assert.Equal(("ייעוץ", (decimal?)100m), (rows.Description, rows.Amount));
    }

    // ================================================================
    //  Two tables on a page, and headers set apart from their numbers
    // ================================================================

    // A line of words each drawn across the box a page measured for it.
    private static PdfTextLine Measured(float y, params (string Text, float Left, float Right)[] words)
    {
        var laid = new List<PdfTextWord>();
        foreach (var (text, left, right) in words)
        {
            bool rtl = L.IsRtlText(text);
            float glyph = (right - left) / text.Length;
            var chars = text.Select((c, i) => new PdfTextChar(c.ToString(), left + (rtl ? text.Length - 1 - i : i) * glyph,
                                                              y, glyph, L.Height, rtl)).ToList();
            laid.Add(new PdfTextWord(text, left, y, right - left, L.Height, chars, rtl));
        }
        float min = laid.Min(w => w.X), max = laid.Max(w => w.X + w.Width);
        return new PdfTextLine(string.Join(" ", words.Select(w => w.Text)), min, y, max - min, L.Height, laid, isRtl: true);
    }

    // A garage's parts table as a browser printed it, its headers set right over numbers set left.
    private static PdfTextLine[] Parts() => new[]
    {
        Measured(582, ("מק\"ט", 526.3f, 550.5f), ("תיאור", 413.8f, 438.2f), ("כמות", 297.5f, 319.2f),
                      ("מחיר", 218.7f, 240.1f), ("יח'", 204f, 215.8f), ("סה\"כ", 102f, 125f)),
        Measured(564, ("147299", 448.2f, 483.3f), ("מגבים", 413.2f, 438.2f), ("1", 250.1f, 256f),
                      ("79.90", 135f, 161.3f), ("79.90", 44.5f, 70.8f)),
        Measured(545, ("139461", 448.2f, 483.3f), ("מסנן", 419.6f, 438.2f), ("שמן", 400.5f, 416.7f),
                      ("2", 250.1f, 256f), ("42.50", 135f, 161.3f), ("85.00", 44.5f, 70.8f)),
    };

    /// <summary>
    /// A garage's labour table under its parts table, its columns elsewhere than theirs: the
    /// page's separators, drawn from both tables, lay each header a column off its numbers and run
    /// the labour's <c>תעריף</c> and <c>סה"כ</c> into one cell. Each header is its numbers' by its
    /// place among its row's cells — the run-together cell read as two — and the labour's own
    /// header names the labour's columns: every item is read with its quantity, price and amount.
    /// The parts table alone reads the same, the control.
    /// </summary>
    [Fact]
    public void ALabourTableUnderItsPartsReadsEveryItemsPrice()
    {
        var labour = new[]
        {
            Measured(515, ("עבודה", 528.7f, 555.5f)),
            Measured(486, ("תיאור", 526.1f, 550.5f), ("עבודה", 496.4f, 523.2f), ("שעות", 333.8f, 357f),
                          ("תעריף", 238.5f, 265.9f), ("סה\"כ", 127.2f, 150.2f)),
            Measured(468, ("אבחון", 527.5f, 550.5f), ("ממוחשב", 490.1f, 524.5f), ("2.0", 275.9f, 290.5f),
                          ("290.00", 160.2f, 192.3f), ("580.00", 44.5f, 76.6f)),
            Measured(450, ("סה\"כ", 529.6f, 550.5f), ("חלקים", 500.9f, 526.7f), ("₪", 311.8f, 320.3f), ("164.90", 276.7f, 308.8f)),
            Measured(434, ("סה\"כ", 529.6f, 550.5f), ("עבודה", 501.2f, 526.7f), ("₪", 311.8f, 320.3f), ("580.00", 276.7f, 308.8f)),
        };
        var total = new[] { labour[3] };

        var both = Items(Parts().Concat(labour).ToArray());
        Assert.Equal(new[] { "מגבים", "מסנן שמן", "אבחון ממוחשב" }, both.Select(i => i.Description));
        Assert.Equal(new decimal?[] { 1m, 2m, 2m }, both.Select(i => i.Quantity));
        Assert.Equal(new decimal?[] { 79.9m, 42.5m, 290m }, both.Select(i => i.UnitPrice));
        Assert.Equal(new decimal?[] { 79.9m, 85m, 580m }, both.Select(i => i.Amount));

        var alone = Items(Parts().Concat(total).ToArray());
        Assert.Equal(new decimal?[] { 79.9m, 42.5m }, alone.Select(i => i.UnitPrice));
        Assert.Equal(new decimal?[] { 79.9m, 85m }, alone.Select(i => i.Amount));
    }

    /// <summary>
    /// The same garage as Word printed it: the separator through <c>מחיר יח'</c> has a free
    /// stretch on either side, parting as many rows each way. The header before it was passed on
    /// its left, so this one is too — the nearer stretch, on its right, would cut the labour's
    /// rate (179–212) from its hours, and the labour would be read without them.
    /// </summary>
    [Fact]
    public void AHeaderIsPassedTheWayTheOneBeforeItWas()
    {
        var items = Items(
            Measured(503, ("מק\"ט", 476.9f, 501.5f), ("תיאור", 388.2f, 412.5f), ("כמות", 291.4f, 313f),
                          ("מחיר", 228.2f, 249.8f), ("יח'", 213.3f, 225.1f), ("סה\"כ", 133.7f, 157.3f)),
            Measured(485, ("825779", 421.3f, 457.1f), ("נוזל", 397.2f, 412.2f), ("קירור", 373.2f, 394f),
                          ("1", 259.1f, 264.9f), ("35.00", 167.5f, 194.3f), ("35.00", 95.4f, 122.2f)),
            Measured(467, ("887998", 421.3f, 457.1f), ("מסנן", 394.2f, 412.5f), ("שמן", 374.7f, 390.3f),
                          ("1", 259.1f, 264.9f), ("42.50", 167.5f, 194.3f), ("42.50", 95.4f, 122.2f)),
            Measured(435, ("עבודה", 476.1f, 506.7f)),
            Measured(406, ("תיאור", 477.5f, 501.8f), ("עבודה", 446.8f, 474.2f), ("שעות", 296.6f, 319.9f),
                          ("תעריף", 226.8f, 254.1f), ("סה\"כ", 145.7f, 169.3f)),
            Measured(388, ("החלפת", 472.2f, 501.8f), ("רפידות", 440.7f, 468.6f), ("בלם", 420.5f, 436.7f),
                          ("1.0", 263.6f, 278.4f), ("290.00", 179.5f, 212.3f), ("290.00", 95.4f, 128.1f)),
            Measured(370, ("סה\"כ", 481.3f, 501.6f), ("חלקים", 452.8f, 477.9f), ("₪", 311.6f, 320.2f), ("77.50", 282.4f, 309.1f)));

        Assert.Equal(new[] { "נוזל קירור", "מסנן שמן", "החלפת רפידות בלם" }, items.Select(i => i.Description));
        Assert.Equal(new decimal?[] { 1m, 1m, 1m }, items.Select(i => i.Quantity));
        Assert.Equal(new decimal?[] { 35m, 42.5m, 290m }, items.Select(i => i.UnitPrice));
    }

    /// <summary>
    /// A toll bill's <c>סכום</c> set right over amounts set left, a column off them on a page whose
    /// separators also come from the vehicles' table below: the header is its amounts' by its place
    /// among its row's two cells. The header over its amounts reads the same, the control.
    /// </summary>
    [Fact]
    public void AHeaderAColumnOffItsAmountsIsTheirsByItsPlace()
    {
        PdfTextLine[] Page(float headerLeft, float headerRight) => new[]
        {
            Measured(600, ("תיאור", 526.1f, 550.5f), ("סכום", headerLeft, headerRight)),
            Measured(582, ("תשלומים", 513.9f, 550.5f), ("עבור", 491.8f, 510.9f), ("נסיעות", 461.6f, 488.8f), ("310.72", 44.5f, 76.6f)),
            Measured(564, ("סה\"כ", 529.6f, 550.5f), ("סכומים", 498f, 526.7f), ("מע\"מ", 440.1f, 462f), ("310.72", 276.7f, 308.8f)),
            Measured(548, ("סה\"כ", 529.6f, 550.5f), ("מע\"מ", 504.8f, 526.7f), ("55.93", 276.7f, 303f)),
            Measured(475, ("מספר", 525.8f, 550.5f), ("רכב", 506.2f, 522.9f), ("מספר", 346.6f, 371.3f), ("נסיעות", 314.7f, 343.7f), ("סכום", 144.1f, 165f)),
            Measured(456, ("86-719-90", 381.3f, 429.2f), ("6", 175f, 180.8f), ("111.90", 44.5f, 75.1f)),
            Measured(438, ("18-662-18", 381.3f, 429.2f), ("13", 175f, 186.7f), ("128.44", 44.5f, 76.6f)),
            Measured(419, ("26-517-74", 381.3f, 429.2f), ("6", 175f, 180.8f), ("70.38", 44.5f, 70.8f)),
        };

        var item = Assert.Single(Items(Page(167.7f, 188.6f)));
        Assert.Equal(("תשלומים עבור נסיעות", (decimal?)310.72m), (item.Description, item.Amount));
        var over = Assert.Single(Items(Page(55.7f, 76.6f)));
        Assert.Equal(("תשלומים עבור נסיעות", (decimal?)310.72m), (over.Description, over.Amount));
    }

    /// <summary>
    /// A header cell naming two roles over rows with no cell more — <c>כמות מחיר</c> over the
    /// count alone — is the role it took first: the count is the quantity and no price, not both.
    /// </summary>
    [Fact]
    public void ACellNamingTwoRolesOverOneValueIsOneRole()
    {
        float[] three = { 560f, 400f, 250f };
        var items = Items(
            L.RtlRow(680, three, "תיאור", "כמות מחיר", "סה\"כ"),
            L.RtlRow(666, three, "ייעוץ", "2", "200.00"),
            L.RtlRow(652, three, "פיתוח", "3", "150.00"),
            L.RtlRow(638, three, "סה\"כ", "", "350.00"));

        Assert.Equal(new decimal?[] { 2m, 3m }, items.Select(i => i.Quantity));
        Assert.Equal(new decimal?[] { null, null }, items.Select(i => i.UnitPrice));
        Assert.Equal(new decimal?[] { 200m, 150m }, items.Select(i => i.Amount));
    }

    /// <summary>
    /// A qualifier's words name no role of their own: <c>סה"כ לפני מע"מ</c> is the amount, not the
    /// amount and the tax, so a row holding a cell more than the header — a unit column with no
    /// heading — is not read as if the header had five places, and each value is the one under
    /// its header.
    /// </summary>
    [Fact]
    public void AQualifiersWordNamesNoSecondColumn()
    {
        var items = Items(
            L.RtlRow(680, Five, "תיאור", "כמות", "", "מחיר", "סה\"כ לפני מע\"מ"),
            L.RtlRow(666, Five, "ייעוץ", "2", "שעה", "100.00", "200.00"),
            L.RtlRow(652, Five, "פיתוח", "1", "חודש", "50.00", "50.00"),
            L.RtlRow(638, Five, "סה\"כ", "", "", "", "250.00"));

        Assert.Equal(new decimal?[] { 100m, 50m }, items.Select(i => i.UnitPrice));
        Assert.Equal(new decimal?[] { 200m, 50m }, items.Select(i => i.Amount));
    }

    /// <summary>
    /// A section's title in the catalog number's column — <c>עבודה</c> between a garage's parts —
    /// is no catalog number, which has a digit: the parts around it keep theirs, and the title adds
    /// no item.
    /// </summary>
    [Fact]
    public void ASectionTitleInTheCatalogColumnIsNoItem()
    {
        var items = Items(
            L.RtlRow(640, Four, "מק\"ט", "תיאור", "כמות", "סה\"כ"),
            L.RtlRow(626, Four, "147299", "מגבים", "1", "79.90"),
            L.RtlRow(612, Four, "עבודה", "", "", ""),
            L.RtlRow(598, Four, "139461", "מסנן", "2", "85.00"),
            L.RtlRow(584, Four, "סה\"כ", "", "", "164.90"));

        Assert.Equal(new[] { "147299", "139461" }, items.Select(i => i.CatalogNumber));
        Assert.Equal(new decimal?[] { 79.9m, 85m }, items.Select(i => i.Amount));
    }

    /// <summary>
    /// A toll bill's <c>מקטע נסיעה</c> (trip segment) folds to hold <c>מקט</c>, a catalog number's
    /// heading: a remark across it with a date — "לתקופה שמסתיימת ב 20/01/25" — is no catalog
    /// number, which is a code, and adds no item. A code in the same column is one, the control.
    /// </summary>
    [Fact]
    public void ARemarkInTheCatalogColumnIsNoCatalogNumber()
    {
        float[] three = { 560f, 400f, 200f };
        IList<InvoiceLineItem> Read(string under) => Items(
            L.RtlRow(640, three, "תאריך", "מקטע נסיעה", "סכום"),
            L.RtlRow(626, three, "01/01/2025", "יקנעם - סומך", "12.50"),
            L.RtlRow(612, three, "", under, ""),
            L.RtlRow(598, three, "02/01/2025", "סומך - יקנעם", "12.50"),
            L.RtlRow(584, three, "סה\"כ", "", "25.00"));

        Assert.Equal(new decimal?[] { 12.5m, 12.5m }, Read("לתקופה שמסתיימת ב 20/01/25").Select(i => i.Amount));
        Assert.Equal(new[] { null, "A17", null }, Read("A17").Select(i => i.CatalogNumber));
    }

    /// <summary>
    /// A totals row whose sum stands in the quantity's column — "₪ 99.00" under <c>כמות</c> — ends
    /// the items: a count carries no currency. The items above keep their quantities.
    /// </summary>
    [Fact]
    public void ASumInTheQuantitysColumnIsNoQuantity()
    {
        var items = Items(
            L.RtlRow(640, Four, "תיאור", "כמות", "מחיר", "סה\"כ"),
            L.RtlRow(626, Four, "מנוי", "1", "49.50", "49.50"),
            L.RtlRow(612, Four, "התקנה", "1", "49.50", "49.50"),
            L.RtlRow(598, Four, "סה\"כ לתשלום", "₪ 99.00", "", ""));

        Assert.Equal(new[] { "מנוי", "התקנה" }, items.Select(i => i.Description));
        Assert.Equal(new decimal?[] { 1m, 1m }, items.Select(i => i.Quantity));
    }

    /// <summary>
    /// A statement of uses — a parking operator's — has a date column and no description: each use
    /// is described by its date and the words beside it, not by the amount it read ("22.50 ש"ח").
    /// Under a header naming no date the table names nothing an item is, and is read as none, the
    /// control.
    /// </summary>
    [Fact]
    public void AStatementOfUsesIsDescribedByEachUsesDate()
    {
        float[] three = { 560f, 440f, 300f };
        IList<InvoiceLineItem> Read(string dateHeader) => Items(
            L.RtlRow(640, three, dateHeader, "חניון", "סכום"),
            L.RtlRow(626, three, "05/08/2024", "הבימה", "15.00"),
            L.RtlRow(612, three, "12/08/2024", "גבעון", "22.50 ש\"ח"),
            L.RtlRow(598, three, "סה\"כ", "", "37.50"));

        var uses = Read("תאריך");
        Assert.Equal(new[] { "05/08/2024 הבימה", "12/08/2024 גבעון" }, uses.Select(i => i.Description));
        Assert.Equal(new decimal?[] { 15m, 22.5m }, uses.Select(i => i.Amount));
        Assert.Empty(Read("יום"));
    }

    // ================================================================
    //  Which column is the line's amount
    // ================================================================

    /// <summary>
    /// A toll bill's totals open with <c>סכומים</c> (amounts) — "סכומים חייבים" — and end the
    /// items: the one line of trips is read, the totals' sum is no second item. A description that
    /// holds the word further in ("החזר סכומים") is an item, the control.
    /// </summary>
    [Fact]
    public void ATotalsRowOpeningWithAmountsEndsTheItems()
    {
        float[] two = { 560f, 300f };
        var toll = Items(
            L.RtlRow(640, two, "תיאור", "סכום"),
            L.RtlRow(626, two, "נסיעות בכביש", "310.00"),
            L.RtlRow(612, two, "סכומים חייבים", "310.00"),
            L.RtlRow(598, two, "סה\"כ מע\"מ", "55.80"));
        var item = Assert.Single(toll);
        Assert.Equal(("נסיעות בכביש", (decimal?)310m), (item.Description, item.Amount));

        var refund = Items(
            L.RtlRow(640, two, "תיאור", "סכום"),
            L.RtlRow(626, two, "נסיעות בכביש", "310.00"),
            L.RtlRow(612, two, "החזר סכומים", "20.00"),
            L.RtlRow(598, two, "סה\"כ", "330.00"));
        Assert.Equal(new decimal?[] { 310m, 20m }, refund.Select(i => i.Amount));
    }

    /// <summary>
    /// A fuel summary's <c>סה"כ מע"מ</c> beside its <c>אחוז מע"מ</c> is the VAT's total, the tax's or
    /// nobody's, never the line's amount: the amount is <c>סה"כ כולל מע"מ</c>, and the item says it
    /// holds its VAT. A plain <c>סה"כ</c> is the amount without it, the control.
    /// </summary>
    [Fact]
    public void AColumnNamingTheTaxIsNoLinesAmount()
    {
        var withVat = Items(
            L.RtlRow(640, Five, "שם הפריט", "כמות", "אחוז מע\"מ", "סה\"כ מע\"מ", "סה\"כ כולל מע\"מ"),
            L.RtlRow(626, Five, "בנזין 95", "120.5", "18.00", "180.00", "1,180.00"),
            L.RtlRow(612, Five, "סולר", "10", "18.00", "18.00", "118.00"),
            L.RtlRow(598, Five, "סה\"כ", "", "", "198.00", "1,298.00"));
        Assert.Equal(new decimal?[] { 1180m, 118m }, withVat.Select(i => i.Amount));
        Assert.All(withVat, i => Assert.True(i.AmountIncludesVat));

        var plain = Items(
            L.RtlRow(640, Four, "שם הפריט", "כמות", "אחוז מע\"מ", "סה\"כ"),
            L.RtlRow(626, Four, "בנזין 95", "120.5", "18.00", "1,000.00"),
            L.RtlRow(612, Four, "סולר", "10", "18.00", "100.00"),
            L.RtlRow(598, Four, "סה\"כ", "", "", "1,100.00"));
        Assert.Equal(new decimal?[] { 1000m, 100m }, plain.Select(i => i.Amount));
        Assert.All(plain, i => Assert.False(i.AmountIncludesVat));
    }

    /// <summary>
    /// A total outranks a sum: under <c>סכום בסיסי</c> (a lease's base amount) and <c>סה"כ</c> the
    /// line's amount is the total's, whichever comes first. A sum alone is the amount, the control.
    /// </summary>
    [Fact]
    public void ATotalOutranksASum()
    {
        float[] three = { 560f, 400f, 250f };
        var both = Items(
            L.RtlRow(640, three, "תיאור", "סכום בסיסי", "סה\"כ"),
            L.RtlRow(626, three, "קורולה", "2,000.00", "2,150.40"),
            L.RtlRow(612, three, "סנטרה", "2,100.00", "2,310.75"),
            L.RtlRow(598, three, "סה\"כ", "", "4,461.15"));
        Assert.Equal(new decimal?[] { 2150.40m, 2310.75m }, both.Select(i => i.Amount));

        float[] two = { 560f, 400f };
        var sum = Items(
            L.RtlRow(640, two, "תיאור", "סכום בסיסי"),
            L.RtlRow(626, two, "קורולה", "2,000.00"),
            L.RtlRow(612, two, "סה\"כ", "2,000.00"));
        Assert.Equal(2000m, Assert.Single(sum).Amount);
    }

    /// <summary>
    /// A header set on two lines — <c>סה"כ</c> on the upper line over the total's column, the other
    /// headings on the lower — heads the column the lower line leaves empty: the total is the line's
    /// amount. Without the upper line the base amount is, the control.
    /// </summary>
    [Fact]
    public void TheLineAboveAHeaderHeadsTheColumnsItLeavesEmpty()
    {
        float[] four = { 560f, 440f, 330f, 220f };
        PdfTextLine[] Table(bool upper) => new[]
        {
            upper ? L.RtlRow(654, four, "", "", "", "סה\"כ") : null,
            L.RtlRow(640, four, "מתאריך", "סכום בסיסי", "השכרה", ""),
            L.RtlRow(626, four, "01/02/2025", "2,000.00", "1,700.20", "2,150.40"),
            L.RtlRow(612, four, "01/02/2025", "2,100.00", "1,830.55", "2,310.75"),
            L.RtlRow(598, four, "סה\"כ", "", "", "4,461.15"),
        }.Where(l => l != null).ToArray();

        Assert.Equal(new decimal?[] { 2150.40m, 2310.75m }, Items(Table(upper: true)).Select(i => i.Amount));
        Assert.Equal(new decimal?[] { 2000m, 2100m }, Items(Table(upper: false)).Select(i => i.Amount));
    }

    /// <summary>
    /// A parking operator's columns liable to VAT and free of it (<c>חייב מע"מ</c>, <c>לא חייב
    /// מע"מ</c>) are the parts of each line's amount: a line holding one is that amount, a line
    /// holding both their sum. The same table under a plain <c>סכום</c> reads its one column, the
    /// control.
    /// </summary>
    [Fact]
    public void ColumnsLiableToVatAndFreeOfItArePartsOfTheAmount()
    {
        float[] three = { 560f, 400f, 250f };
        var parts = Items(
            L.RtlRow(640, three, "פירוט", "חייב מע\"מ", "לא חייב מע\"מ"),
            L.RtlRow(626, three, "דמי חניה - חניונים", "900.00 ₪", ""),
            L.RtlRow(612, three, "דמי חניה - כחול לבן", "", "250.00 ₪"),
            L.RtlRow(598, three, "מנוי", "100.00 ₪", "15.00 ₪"),
            L.RtlRow(584, three, "סה\"כ", "1,000.00 ₪", "265.00 ₪"));
        Assert.Equal(new[] { "דמי חניה - חניונים", "דמי חניה - כחול לבן", "מנוי" }, parts.Select(i => i.Description));
        Assert.Equal(new decimal?[] { 900m, 250m, 115m }, parts.Select(i => i.Amount));

        var one = Items(
            L.RtlRow(640, three, "פירוט", "סכום", ""),
            L.RtlRow(626, three, "דמי חניה - חניונים", "900.00 ₪", ""),
            L.RtlRow(612, three, "סה\"כ", "900.00 ₪", ""));
        Assert.Equal(900m, Assert.Single(one).Amount);
    }

    /// <summary>
    /// A cell across both parts of the amount — a figure set in the gap between their columns — is
    /// the line's amount once, not once for each part. The rows whose parts stand apart sum them,
    /// the control.
    /// </summary>
    [Fact]
    public void ACellAcrossBothPartsIsCountedOnce()
    {
        float[] three = { 560f, 400f, 250f };
        var items = Items(
            L.RtlRow(640, three, "פירוט", "חייב מע\"מ", "לא חייב מע\"מ"),
            L.RtlRow(626, three, "חניונים", "100.00", "15.00"),
            L.RtlRow(612, three, "כחול לבן", "200.00", "25.00"),
            Row(598, true, ("מנוי", 530), ("300.00", 280)),   // 280–316, across the gap at 250–364
            L.RtlRow(584, three, "סה\"כ", "600.00", "40.00"));

        Assert.Equal(new decimal?[] { 115m, 225m, 300m }, items.Select(i => i.Amount));
    }

    /// <summary>
    /// A table carried to the next page keeps its header's word that the amount holds its VAT: the
    /// continuation's items are marked as the first page's are. Under a plain total none is, the
    /// control.
    /// </summary>
    [Fact]
    public void AContinuationKeepsTheAmountsVat()
    {
        IList<InvoiceLineItem> Read(string amountHeader) => Items(
            L.Page(0,
                L.RtlRow(640, Four, "תיאור", "כמות", "מחיר", amountHeader),
                L.RtlRow(626, Four, "ייעוץ", "2", "100.00", "236.00"),
                L.RtlRow(612, Four, "פיתוח", "1", "50.00", "59.00")),
            L.Page(1,
                L.RtlRow(740, Four, "הדרכה", "3", "10.00", "35.40"),
                L.RtlRow(726, Four, "הובלה", "1", "99.00", "116.82")));

        var withVat = Read("סה\"כ כולל מע\"מ");
        Assert.Equal(4, withVat.Count);
        Assert.All(withVat, i => Assert.True(i.AmountIncludesVat));
        var plain = Read("סה\"כ");
        Assert.Equal(4, plain.Count);
        Assert.All(plain, i => Assert.False(i.AmountIncludesVat));
    }

    /// <summary>
    /// A header cell holding a figure names no column, word by word as whole: a lessor's headings
    /// run together with the document's own figures (<c>כמות סכום 7</c>) are no header, and no item
    /// is read where the amount could be anybody's. The same cell without the figure is read as two
    /// headings, the control.
    /// </summary>
    [Fact]
    public void AHeaderCellHoldingAFigureNamesNoColumn()
    {
        float[] three = { 560f, 400f, 250f };
        PdfPageTextLayout Page(string run) => L.Page(0,
            L.RtlRow(640, three, "תאריך", run, ""),
            L.RtlRow(626, three, "01/03/2026", "2", "120.40"),
            L.RtlRow(612, three, "02/03/2026", "1", "75.10"));
        IList<InvoiceLineItem> Read(string run) => Items(Page(run));

        Assert.Empty(Read("כמות סכום 7"));
        Assert.Equal(new decimal?[] { 120.40m, 75.10m }, Read("כמות סכום").Select(i => i.Amount));
    }

    // ================================================================
    //  Across pages
    // ================================================================

    /// <summary>
    /// A continuation page whose discount column is empty detects one column fewer; its cells are
    /// still given the header's roles by where they sit, so its items are read with the right
    /// quantity, price and amount.
    /// </summary>
    [Fact]
    public void AContinuationPageWithAnEmptyColumnIsReadByPosition()
    {
        var page1 = L.Page(0,
            L.RtlRow(640, Five, "תיאור", "כמות", "מחיר", "הנחה", "סה\"כ"),
            L.RtlRow(626, Five, "ייעוץ", "2", "100.00", "20.00", "180.00"),
            L.RtlRow(612, Five, "פיתוח", "1", "50.00", "5.00", "45.00"));
        var page2 = L.Page(1,
            L.RtlRow(740, Five, "הדרכה", "3", "10.00", "", "30.00"),
            L.RtlRow(726, Five, "הובלה", "1", "99.00", "", "99.00"));

        var items = Items(page1, page2);

        Assert.Equal(4, items.Count);
        Assert.Equal("הדרכה", items[2].Description);
        Assert.Equal(3m, items[2].Quantity);
        Assert.Equal(10m, items[2].UnitPrice);
        Assert.Equal(30m, items[2].Amount);
        Assert.Null(items[2].Discount);
    }

    /// <summary>
    /// An invoice printed as original and copy holds its table twice: once the first has reached
    /// its totals, the second is not read again. A table whose header repeats on page 2 before
    /// its totals still continues, the control.
    /// </summary>
    [Fact]
    public void AnOriginalAndItsCopyAreReadOnce()
    {
        PdfPageTextLayout Copy(int page) => L.Page(page,
            L.RtlRow(640, Four, "תיאור", "כמות", "מחיר", "סה\"כ"),
            L.RtlRow(626, Four, "ייעוץ", "2", "2,000.00", "6,000.00"),
            L.RtlRow(612, Four, "פיתוח", "1", "4,000.00", "4,000.00"),
            L.RtlRow(598, Four, "סה\"כ", "", "", "10,000.00"));
        Assert.Equal(2, Items(Copy(0), Copy(1)).Count);

        var first = L.Page(0,
            L.RtlRow(640, Four, "תיאור", "כמות", "מחיר", "סה\"כ"),
            L.RtlRow(626, Four, "ייעוץ", "2", "2,000.00", "6,000.00"),
            L.RtlRow(612, Four, "פיתוח", "1", "4,000.00", "4,000.00"));
        var second = L.Page(1,
            L.RtlRow(640, Four, "תיאור", "כמות", "מחיר", "סה\"כ"),
            L.RtlRow(626, Four, "הדרכה", "3", "10.00", "30.00"),
            L.RtlRow(612, Four, "סה\"כ", "", "", "10,030.00"));
        Assert.Equal(new[] { "ייעוץ", "פיתוח", "הדרכה" }, Items(first, second).Select(i => i.Description));
    }

    /// <summary>
    /// Malformed layouts — a null page, a page without lines, a line without words — are passed
    /// over by the item table as by the fields: the invoice's items are still read.
    /// </summary>
    [Fact]
    public void MalformedLayoutsAreSkippedByTheItemTable()
    {
        var page = IsraeliInvoiceExtractorTests.InvoicePage();
        var withHole = L.Page(0, page.Lines.Concat(new[] { new PdfTextLine("", 0, 0, 0, 0, null) }).ToArray());

        var inv = IsraeliInvoiceExtractor.Extract(new[] { withHole, null, new PdfPageTextLayout(2, null) });

        Assert.Equal(2, inv.LineItems.Count);
        Assert.Equal(11800m, inv.Total);
    }

    /// <summary>
    /// The table extractor's own door passes over the same holes — a null page, a page without
    /// lines, a line without words, a null word — and still finds the table on the page that has
    /// one, at its own page index.
    /// </summary>
    [Fact]
    public void MalformedLayoutsAreSkippedByTheTableExtractor()
    {
        var rows = new List<PdfTextLine>
        {
            L.RtlRow(640, Four, "תיאור", "כמות", "מחיר", "סה\"כ"),
            L.RtlRow(626, Four, "ייעוץ", "2", "100.00", "200.00"),
            L.RtlRow(612, Four, "פיתוח", "1", "50.00", "50.00"),
            new PdfTextLine("", 0, 0, 0, 0, null),
            new PdfTextLine("x", 72, 500, 6, L.Height, new[] { null, L.Word("x", 72, 500) }),
        };
        var pages = new[] { null, new PdfPageTextLayout(1, null), L.Page(2, rows.ToArray()) };

        var tables = PdfTableExtractor.ExtractFromLayouts(pages, null, new TableExtractionOptions { RightToLeftColumns = true });

        var table = Assert.Single(tables);
        Assert.Equal(2, table.PageIndex);
        Assert.True(table.Rows.Count >= 3);
    }

    /// <summary>
    /// The same holes on a ruled page — a null line, a line without words, a null word — where the
    /// grid strategy collects the words: the table is read from the rulings, each cell holding its
    /// word. The test above passes no graphics, so its page goes to the text strategy only.
    /// </summary>
    [Fact]
    public void MalformedLayoutsAreSkippedByTheRuledGrid()
    {
        var lines = new[]
        {
            new PdfTextLine("A B", 60, 685, 110, L.Height, new[] { L.Word("A", 60, 685), null, L.Word("B", 160, 685) }),
            null,
            new PdfTextLine("", 0, 0, 0, 0, null),
            new PdfTextLine("C D", 60, 665, 110, L.Height, new[] { L.Word("C", 60, 665), L.Word("D", 160, 665) }),
        };
        var grid = new PdfPageGraphics(0,
            new[] { new HRuling(50, 250, 700, 1), new HRuling(50, 250, 680, 1), new HRuling(50, 250, 660, 1) },
            new[] { new VRuling(50, 660, 700, 1), new VRuling(150, 660, 700, 1), new VRuling(250, 660, 700, 1) },
            null);

        var tables = PdfTableExtractor.ExtractFromLayouts(new[] { L.Page(0, lines) }, new[] { grid },
            new TableExtractionOptions { Strategy = TableStrategy.Lines });

        var table = Assert.Single(tables);
        Assert.Equal(new[] { "A", "B" }, table.Rows[0].Cells.Select(c => c.Text));
        Assert.Equal(new[] { "C", "D" }, table.Rows[1].Cells.Select(c => c.Text));
    }
}
