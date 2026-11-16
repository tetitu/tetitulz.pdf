// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Encryption;
using Xunit;
using L = Tetitulz.Pdf.Base.Tests.Operations.InvoiceLayouts;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="IsraeliInvoiceExtractor"/> over a whole Hebrew invoice: title, numbers, dates,
/// totals and the item table, from layouts and from a PDF.
/// </summary>
public class IsraeliInvoiceExtractorTests
{
    internal const string Supplier = "514286558";
    internal const string Customer = "300056785";

    // Item-table columns, right to left: description, quantity, unit price, amount.
    private static readonly float[] Columns = { 560f, 440f, 360f, 260f };

    /// <summary>A two-item tax invoice of 10,000 + 18% VAT dated 2026-07-15.</summary>
    internal static PdfPageTextLayout InvoicePage(bool withAllocation = true, string total = "11,800.00")
    {
        var lines = new List<PdfTextLine>
        {
            L.Rtl(780, 560, "חשבונית", "מס"),
            L.Rtl(760, 560, "דוגמה", "בע\"מ", L.Column, "ע.מ.", Supplier),
            L.Rtl(730, 560, "לכבוד:", "לקוח", "בע\"מ"),
            L.Rtl(716, 560, "ח.פ.", Customer),
            L.Rtl(690, 560, "מס'", "חשבונית:", "20457", L.Column, "תאריך:", "15/07/2026"),
        };
        if (withAllocation) lines.Add(L.Rtl(676, 560, "מספר", "הקצאה:", "123456789"));
        lines.AddRange(new[]
        {
            L.RtlRow(640, Columns, "תיאור", "כמות", "מחיר", "סה\"כ"),
            L.RtlRow(626, Columns, "ייעוץ עסקי", "2", "3,000.00", "6,000.00"),
            L.RtlRow(612, Columns, "פיתוח", "1", "4,000.00", "4,000.00"),
            L.Rtl(570, 560, "סה\"כ", "לפני", "מע\"מ", L.Column, "10,000.00"),
            L.Rtl(556, 560, "מע\"מ", "18%", L.Column, "1,800.00"),
            L.Rtl(542, 560, "סה\"כ", "לתשלום", L.Column, total),
        });
        return L.Page(0, lines.ToArray());
    }

    // ================================================================
    //  The fields
    // ================================================================

    /// <summary>Every field of a tax invoice is read, and the invoice validates clean.</summary>
    [Fact]
    public void EveryFieldOfATaxInvoiceIsRead()
    {
        var inv = IsraeliInvoiceExtractor.Extract(new[] { InvoicePage() });

        Assert.Equal(IsraeliDocumentType.TaxInvoice, inv.DocumentType);
        Assert.Equal("20457", inv.InvoiceNumber);
        Assert.Equal(new DateTime(2026, 7, 15), inv.Date);
        Assert.Equal(Supplier, inv.SupplierId);
        Assert.Equal(Customer, inv.CustomerId);
        Assert.Equal("123456789", inv.AllocationNumber);
        Assert.Equal(10000m, inv.Subtotal);
        Assert.Equal(18m, inv.VatRate);
        Assert.Equal(1800m, inv.Vat);
        Assert.Equal(11800m, inv.Total);
        Assert.Equal(InvoiceTextSource.TextLayer, inv.TextSource);

        Assert.DoesNotContain(inv.Findings, f => f.Severity != InvoiceFindingSeverity.Info);
        Assert.Contains(inv.Findings, f => f.Code == InvoiceFindingCodes.NotDigitallySigned);
    }

    /// <summary>
    /// The item table is read under its header — wrapped across two words, a description stays
    /// one — and the totals below it are not items.
    /// </summary>
    [Fact]
    public void TheItemTableIsReadAndEndsAtTheTotals()
    {
        var inv = IsraeliInvoiceExtractor.Extract(new[] { InvoicePage() });

        Assert.Equal(2, inv.LineItems.Count);
        Assert.Equal(1, inv.LineItems[0].Line);
        Assert.Equal("ייעוץ עסקי", inv.LineItems[0].Description);
        Assert.Equal(2m, inv.LineItems[0].Quantity);
        Assert.Equal(3000m, inv.LineItems[0].UnitPrice);
        Assert.Equal(6000m, inv.LineItems[0].Amount);
        Assert.Equal("פיתוח", inv.LineItems[1].Description);
        Assert.Equal(4000m, inv.LineItems[1].Amount);
    }

    /// <summary>
    /// A description wrapped onto a row of its own continues the item above it, and a catalogue
    /// column is read as the catalogue number, not as the description.
    /// </summary>
    [Fact]
    public void AWrappedDescriptionContinuesItsItemAndTheCatalogueColumnIsRead()
    {
        float[] columns = { 560f, 470f, 380f, 300f, 200f };
        var page = L.Page(0,
            L.RtlRow(640, columns, "מק\"ט", "תיאור", "כמות", "מחיר", "סה\"כ"),
            L.RtlRow(626, columns, "A-17", "ייעוץ", "2", "100.00", "200.00"),
            L.RtlRow(612, columns, "", "בשעות הערב", "", "", ""),
            L.RtlRow(598, columns, "B-4", "פיתוח", "1", "50.00", "50.00"));

        var items = IsraeliInvoiceExtractor.Extract(new[] { page }).LineItems;

        Assert.Equal(2, items.Count);
        Assert.Equal("A-17", items[0].CatalogNumber);
        Assert.Equal("ייעוץ בשעות הערב", items[0].Description);
        Assert.Equal("B-4", items[1].CatalogNumber);
        Assert.Equal(50m, items[1].Amount);
    }

    /// <summary>
    /// A totals row printed inside the item table's grid — its label in the description column,
    /// its amount under the items' amounts — ends the items rather than becoming one.
    /// </summary>
    /// <remarks>
    /// The stop is reached only by a totals row the table detector keeps: one whose label fits
    /// its column. A label wider than the description column — <c>סה"כ לפני מע"מ</c> — crosses the
    /// column separator, which the detector reads as prose, and it ends the table itself; that is
    /// why <see cref="InvoicePage"/>'s totals never tested the stop, and a mutation removing it
    /// survived the first sweep.
    /// </remarks>
    [Fact]
    public void ATotalsRowInsideTheGridEndsTheItems()
    {
        var page = L.Page(0,
            L.RtlRow(640, Columns, "תיאור", "כמות", "מחיר", "סה\"כ"),
            L.RtlRow(626, Columns, "ייעוץ", "2", "3,000.00", "6,000.00"),
            L.RtlRow(612, Columns, "פיתוח", "1", "4,000.00", "4,000.00"),
            L.RtlRow(598, Columns, "סה\"כ", "", "", "10,000.00"));

        var items = IsraeliInvoiceExtractor.Extract(new[] { page }).LineItems;

        Assert.Equal(new[] { "ייעוץ", "פיתוח" }, items.Select(i => i.Description));
        Assert.Equal(new decimal?[] { 6000m, 4000m }, items.Select(i => i.Amount));
    }

    /// <summary>
    /// The validation sees what was read: a total the subtotal and VAT do not make is an error,
    /// and an invoice above the threshold with no allocation number is warned about.
    /// </summary>
    [Fact]
    public void WhatWasReadIsValidated()
    {
        var wrongTotal = IsraeliInvoiceExtractor.Extract(new[] { InvoicePage(total: "11,900.00") });
        Assert.Contains(wrongTotal.Findings, f => f.Code == InvoiceFindingCodes.TotalMismatch);

        var noAllocation = IsraeliInvoiceExtractor.Extract(new[] { InvoicePage(withAllocation: false) });
        Assert.Null(noAllocation.AllocationNumber);
        Assert.Contains(noAllocation.Findings, f => f.Code == InvoiceFindingCodes.AllocationNumberMissing);
    }

    /// <summary>Each document title is recognised, the most specific first.</summary>
    [Theory]
    [InlineData(IsraeliDocumentType.TaxInvoiceReceipt, "חשבונית", "מס", "/", "קבלה")]
    [InlineData(IsraeliDocumentType.TaxInvoiceReceipt, "חשבונית", "מס", "קבלה")]
    [InlineData(IsraeliDocumentType.CreditInvoice, "חשבונית", "מס", "זיכוי")]
    [InlineData(IsraeliDocumentType.PeriodicTaxInvoice, "חשבונית", "ריכוז")]
    [InlineData(IsraeliDocumentType.TaxInvoice, "חשבונית", "מס", "מס'", "88")]
    [InlineData(IsraeliDocumentType.TransactionInvoice, "חשבון", "עסקה")]
    [InlineData(IsraeliDocumentType.Receipt, "קבלה", "מס'", "12")]
    [InlineData(IsraeliDocumentType.Unknown, "הצעת", "מחיר")]
    public void EachDocumentTitleIsRecognised(IsraeliDocumentType expected, params string[] title)
    {
        var page = L.Page(0, L.Rtl(780, 560, title), L.Rtl(700, 560, "סה\"כ", L.Column, "100.00"));

        Assert.Equal(expected, IsraeliInvoiceExtractor.Extract(new[] { page }).DocumentType);
    }

    /// <summary>
    /// A word that merely contains <c>קבלה</c> ("was received") does not make a document a
    /// receipt.
    /// </summary>
    [Fact]
    public void AWordContainingReceiptIsNotAReceiptTitle()
    {
        var page = L.Page(0, L.Rtl(780, 560, "התשלום", "התקבלה"));

        Assert.Equal(IsraeliDocumentType.Unknown, IsraeliInvoiceExtractor.Extract(new[] { page }).DocumentType);
    }

    // ================================================================
    //  Identification numbers
    // ================================================================

    /// <summary>
    /// With no customer cue, a second valid number is the customer's; an invalid second number
    /// is not taken.
    /// </summary>
    [Fact]
    public void WithoutACueASecondValidNumberIsTheCustomers()
    {
        var page = L.Page(0,
            L.Rtl(760, 560, "ע.מ.", Supplier),
            L.Rtl(700, 560, "ח.פ.", Customer));
        var inv = IsraeliInvoiceExtractor.Extract(new[] { page });
        Assert.Equal(Supplier, inv.SupplierId);
        Assert.Equal(Customer, inv.CustomerId);

        var invalid = L.Page(0,
            L.Rtl(760, 560, "ע.מ.", Supplier),
            L.Rtl(700, 560, "ח.פ.", "300056784"));
        Assert.Null(IsraeliInvoiceExtractor.Extract(new[] { invalid }).CustomerId);
    }

    /// <summary>
    /// A customer block printed above the supplier's number is still the customer's: the cue
    /// decides, not the order.
    /// </summary>
    [Fact]
    public void TheCustomerCueDecidesNotTheOrder()
    {
        var page = L.Page(0,
            L.Rtl(760, 560, "לכבוד:", "לקוח", "בע\"מ"),
            L.Rtl(746, 560, "ח.פ.", Customer),
            L.Rtl(700, 560, "ספק", "בע\"מ"),
            L.Rtl(686, 560, "ע.מ.", Supplier));
        var inv = IsraeliInvoiceExtractor.Extract(new[] { page });

        Assert.Equal(Customer, inv.CustomerId);
        Assert.Equal(Supplier, inv.SupplierId);
    }

    /// <summary>
    /// An identification number is eight or nine digits, dashes allowed; the one passing the
    /// check digit is preferred over a phone number beside it.
    /// </summary>
    [Theory]
    [InlineData("51-428655-8", "51-428655-8")]
    [InlineData("03-1234567 514286558", "514286558")]
    [InlineData("1234567", null)]
    public void AnIdentifierIsEightOrNineDigitsAndTheValidOneWins(string value, string expected)
    {
        Assert.Equal(expected, IsraeliInvoiceExtractor.IdentifierIn(value));
    }

    // ================================================================
    //  Text in a legacy encoding
    // ================================================================

    /// <summary>
    /// Hebrew decoded as Latin-1 — runs of à to ú — is recognised, and so is such Hebrew split into
    /// pieces of one and two letters, by its majority; French with its accents is not — it clears
    /// the floor of six, and makes no run and no majority — and real Hebrew is not.
    /// </summary>
    [Theory]
    [InlineData("ùìåí òåìí", true)]
    [InlineData("çùáåðéú îñ", true)]
    [InlineData("ùì åí òå ìí", true)]
    [InlineData("café à la crème brûlée", false)]
    [InlineData("élève à côté de café crème déjà", false)]
    [InlineData("שלום עולם", false)]
    public void HebrewReadAsLatinLettersIsRecognised(string text, bool expected)
    {
        var line = L.Ltr(700, 72, text.Split(' '));
        Assert.Equal(expected, IsraeliInvoiceExtractor.LooksLikeLegacyHebrew(L.Page(0, line)));
    }

    // ================================================================
    //  From a PDF
    // ================================================================

    /// <summary>The same reading through a PDF's own text layer.</summary>
    [Fact]
    public void AnInvoicePdfIsReadThroughItsTextLayer()
    {
        var inv = IsraeliInvoiceExtractor.Extract(new MemoryStream(InvoicePdfs.Simple()),
                                                  new IsraeliInvoiceOptions { Source = "simple.pdf" });

        Assert.Equal("simple.pdf", inv.Source);
        Assert.Equal(IsraeliDocumentType.TaxInvoice, inv.DocumentType);
        Assert.Equal("20457", inv.InvoiceNumber);
        Assert.Equal(new DateTime(2026, 7, 15), inv.Date);
        Assert.Equal(Supplier, inv.SupplierId);
        Assert.Equal(10000m, inv.Subtotal);
        Assert.Equal(1800m, inv.Vat);
        Assert.Equal(11800m, inv.Total);
        Assert.Equal(InvoiceTextSource.TextLayer, inv.TextSource);
        Assert.False(inv.IsDigitallySigned);
        Assert.DoesNotContain(inv.Findings, f => f.Severity == InvoiceFindingSeverity.Error);
    }

    /// <summary>
    /// Hebrew read as Latin letters is decoded as Windows-1255: a legacy font draws the letters as
    /// shown, so each word is read from its right, a line of such words is right to left, and a
    /// number among them stays as it is.
    /// </summary>
    [Fact]
    public void LegacyHebrewIsDecodedAsWindows1255_InVisualOrder()
    {
        // "סמ" and "תינובשח" as shown left to right: "חשבונית מס" read from the right; 12 stays.
        var decoded = IsraeliInvoiceExtractor.DecodeLegacyHebrew(L.Page(0, L.Ltr(700, 72, "12", "ñî", "úéðåáùç")));

        var line = Assert.Single(decoded.Lines);
        Assert.True(line.IsRtl);
        Assert.Equal(new[] { "12", "מס", "חשבונית" }, line.Words.Select(w => w.Text));
        Assert.StartsWith("חשבונית מס", line.Text, StringComparison.Ordinal);
        Assert.False(line.Words[0].IsRtl);
        Assert.True(line.Words[2].IsRtl);
        Assert.All(line.Words[2].Chars, c => Assert.True(c.IsRtl));
        Assert.False(IsraeliInvoiceExtractor.LooksLikeLegacyHebrew(decoded));
    }

    /// <summary>
    /// The decoding table is the code page's: every byte Windows-1255 maps into the Hebrew block is
    /// decoded to that character, and no other byte is, held to the platform's own code page.
    /// </summary>
    [Fact]
    public void TheWindows1255Table_IsTheCodePages()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding cp1255 = Encoding.GetEncoding(1255);
        int hebrewBytes = 0;
        for (int b = 0x80; b <= 0xFF; b++)
        {
            char expected = cp1255.GetString(new[] { (byte)b })[0];
            bool hebrew = expected >= '֐' && expected <= '׿';
            Assert.Equal(hebrew, IsraeliInvoiceExtractor.Windows1255Hebrew((char)b, out char decoded));
            if (hebrew) { Assert.Equal(expected, decoded); hebrewBytes++; }
        }
        Assert.True(hebrewBytes >= 27 + 20, $"{hebrewBytes} Hebrew positions");   // the 27 letters and the points
    }

    /// <summary>
    /// A page whose text is Hebrew in a legacy encoding is decoded and reported, with recognition
    /// off: the label is read and its value found; the text layer is not reported missing, because
    /// it is there.
    /// </summary>
    [Fact]
    public void LegacyEncodedTextIsDecodedAndReported()
    {
        var b = new InvoicePdfBuilder(winAnsi: true);
        LegacyRtl(b, "מספר חשבונית", 500, 730).AsciiRun("20457", 360, 730, step: 5);

        var inv = IsraeliInvoiceExtractor.Extract(new MemoryStream(b.Build()), new IsraeliInvoiceOptions { Ocr = InvoiceOcrMode.Never });

        Assert.Equal("20457", inv.InvoiceNumber);
        Assert.Equal(InvoiceTextSource.TextLayer, inv.TextSource);
        Assert.Contains(inv.ExtractionFindings, f => f.Code == InvoiceFindingCodes.LegacyHebrewEncoding);
        Assert.DoesNotContain(inv.ExtractionFindings, f => f.Code == InvoiceFindingCodes.NoTextLayer);
    }

    /// <summary>
    /// Draws <paramref name="logical"/> as a legacy font does: each Hebrew letter as the Latin-1
    /// character of its Windows-1255 byte, laid out right to left from <paramref name="xRight"/>.
    /// </summary>
    private static InvoicePdfBuilder LegacyRtl(InvoicePdfBuilder b, string logical, int xRight, int y, int step = 7, int space = 10)
    {
        int x = xRight;
        foreach (char c in logical)
        {
            if (c == ' ') { x -= space; continue; }
            b.Ascii(c >= 'א' && c <= 'ת' ? (char)(0xE0 + (c - 'א')) : c, x, y);
            x -= step;
        }
        return b;
    }

    /// <summary>
    /// A page with no text and recognition off is reported as unread — in the findings and in
    /// <see cref="IsraeliInvoiceExtractor.LastError"/>, which a page with text leaves empty.
    /// </summary>
    [Fact]
    public void APageWithNoTextIsReportedWhenRecognitionIsOff()
    {
        byte[] pdf = new InvoicePdfBuilder().Build();

        var inv = IsraeliInvoiceExtractor.Extract(new MemoryStream(pdf), new IsraeliInvoiceOptions { Ocr = InvoiceOcrMode.Never });

        Assert.Equal(InvoiceTextSource.None, inv.TextSource);
        Assert.Contains(inv.ExtractionFindings, f => f.Code == InvoiceFindingCodes.NoTextLayer);
        Assert.Contains(inv.Findings, f => f.Code == InvoiceFindingCodes.NoTextLayer);
        Assert.Contains("recognition is off", IsraeliInvoiceExtractor.LastError, StringComparison.Ordinal);

        IsraeliInvoiceExtractor.Extract(new MemoryStream(InvoicePdfs.Simple()), new IsraeliInvoiceOptions { Ocr = InvoiceOcrMode.Never });
        Assert.Null(IsraeliInvoiceExtractor.LastError);
    }
}

/// <summary>Invoice PDFs drawn glyph by glyph with <see cref="InvoicePdfBuilder"/>.</summary>
internal static class InvoicePdfs
{
    /// <summary>
    /// Writes <paramref name="logical"/> right to left ending at <paramref name="xRight"/>: Hebrew
    /// letters through the builder's Hebrew font, the quote, apostrophe and colon as ASCII between
    /// them, a space as a word gap — <paramref name="step"/> and <paramref name="space"/> apart, for
    /// the builder's 10 pt unless a larger size is set.
    /// </summary>
    internal static InvoicePdfBuilder Rtl(this InvoicePdfBuilder b, string logical, int xRight, int y,
                                          int step = 7, int space = 10)
    {
        int x = xRight;
        foreach (char c in logical)
        {
            if (c == ' ') { x -= space; continue; }
            if (c >= 'א' && c <= 'ת') b.Heb(c, x, y);
            else b.Ascii(c, x, y);
            x -= step;
        }
        return b;
    }

    /// <summary>The invoice of <see cref="IsraeliInvoiceExtractorTests.InvoicePage"/>, without its item table.</summary>
    internal static byte[] Simple()
        => new InvoicePdfBuilder()
            .Rtl("חשבונית מס", 500, 760)
            .Rtl("מספר חשבונית", 500, 730).AsciiRun("20457", 360, 730, step: 5)
            .Rtl("תאריך", 500, 710).AsciiRun("15/07/2026", 340, 710, step: 4)
            .Rtl("עוסק מורשה", 500, 690).AsciiRun(IsraeliInvoiceExtractorTests.Supplier, 340, 690, step: 5)
            .Rtl("סה\"כ לפני מע\"מ", 500, 640).AsciiRun("10,000.00", 300, 640, step: 4)
            .Rtl("מע\"מ", 500, 620).AsciiRun("1,800.00", 300, 620, step: 4)
            .Rtl("סה\"כ לתשלום", 500, 600).AsciiRun("11,800.00", 300, 600, step: 4)
            .Build();
}

/// <summary>
/// An encrypted invoice is decrypted through the registered decryptor: with the empty password
/// for the permissions-only protection invoicing software applies, and with the user password
/// when one is set.
/// </summary>
[Collection(TestInfrastructure.DecryptorHolderCollection.Name)]
public class IsraeliInvoiceEncryptedTests
{
    public IsraeliInvoiceEncryptedTests() => PdfEncryptionOperations.Register();

    private static byte[] Encrypt(byte[] plain, string userPassword)
    {
        using var input = new MemoryStream(plain, writable: false);
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = userPassword,
            OwnerPassword = "owner-pw",
            Algorithm = EncryptionAlgorithm.AES128,
            Permissions = PdfPermissions.All,
        });
        return output.ToArray();
    }

    [Fact]
    public void APermissionsOnlyEncryptedInvoiceIsReadWithTheEmptyPassword()
    {
        var inv = IsraeliInvoiceExtractor.Extract(new MemoryStream(Encrypt(InvoicePdfs.Simple(), "")));

        Assert.Equal(11800m, inv.Total);
        Assert.Equal("20457", inv.InvoiceNumber);
    }

    [Fact]
    public void AnInvoiceWithAUserPasswordIsReadWithIt()
    {
        var inv = IsraeliInvoiceExtractor.Extract(new MemoryStream(Encrypt(InvoicePdfs.Simple(), "user-pw")), "user-pw", null);

        Assert.Equal(11800m, inv.Total);
    }
}

/// <summary>
/// A scanned invoice — a page with no text — is recognised through the registered word engine
/// and read from the recognised words. A stub engine stands in for Tesseract, so what is asserted
/// is this library's plumbing, not an engine's accuracy.
/// </summary>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public sealed class IsraeliInvoiceOcrTests : IDisposable
{
    /// <summary>Returns the words of a small invoice, placed on the page it is handed.</summary>
    private sealed class InvoiceWordEngine : IOcrWordPlugin
    {
        // Text, and its box in PDF points (x, y bottom, width, height) on a 612 x 792 page.
        private static readonly (string Text, float X, float Y, float W, float H)[] Words =
        {
            ("חשבונית", 358, 760, 42, 10), ("מס", 330, 760, 14, 10),
            ("סה\"כ", 476, 600, 24, 10), ("לתשלום", 424, 600, 42, 10), ("11,800.00", 300, 600, 54, 10),
        };

        internal int Calls { get; private set; }

        public string Name => "Tetitulz.Tests.InvoiceWordEngine";
        public string Description => "Returns a fixed invoice's words.";
        public Version Version => new Version(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }
        public bool CanProcess(int components, int bitsPerComponent) => components == 3 && bitsPerComponent == 8;
        public string RecognizeText(byte[] pixels, int width, int height, int components)
            => string.Join(" ", Words.Select(w => w.Text));

        public IReadOnlyList<OcrWordBox> RecognizeWords(byte[] pixels, int width, int height, int components)
        {
            Calls++;
            float sx = width / 612f, sy = height / 792f;
            return Words.Select(w => new OcrWordBox(w.Text, w.X * sx, (792f - w.Y - w.H) * sy, w.W * sx, w.H * sy, 0.9f))
                        .ToList();
        }
    }

    private readonly InvoiceWordEngine _engine = new InvoiceWordEngine();

    public IsraeliInvoiceOcrTests() => PdfPluginHost.Instance.Register(_engine);

    public void Dispose() => PdfPluginHost.Instance.Unregister(_engine.Name);

    [Fact]
    public void AScannedInvoiceIsReadFromTheRecognisedWords()
    {
        byte[] scan = new InvoicePdfBuilder().Build();   // a page with no text: a stand-in for a scan

        var inv = IsraeliInvoiceExtractor.Extract(new MemoryStream(scan), new IsraeliInvoiceOptions { OcrDpi = 72 });

        Assert.Equal(1, _engine.Calls);
        Assert.Equal(InvoiceTextSource.Ocr, inv.TextSource);
        Assert.Equal(IsraeliDocumentType.TaxInvoice, inv.DocumentType);
        Assert.Equal(11800m, inv.Total);
        Assert.DoesNotContain(inv.ExtractionFindings, f => f.Code == InvoiceFindingCodes.NoTextLayer);
    }

    /// <summary>
    /// A scan turned a quarter in the viewer (<c>/Rotate 90</c>): the engine reads the displayed
    /// page, and its words come back in unrotated user space, where every displayed line is a
    /// column. Laid out as the page is displayed, the invoice reads as it does upright.
    /// </summary>
    [Fact]
    public void AQuarterTurnedScanIsReadAsDisplayed()
    {
        byte[] scan = new InvoicePdfBuilder().Rotated(90).Build();

        var inv = IsraeliInvoiceExtractor.Extract(new MemoryStream(scan), new IsraeliInvoiceOptions { OcrDpi = 72 });

        Assert.Equal(1, _engine.Calls);
        Assert.Equal(InvoiceTextSource.Ocr, inv.TextSource);
        Assert.Equal(IsraeliDocumentType.TaxInvoice, inv.DocumentType);
        Assert.Equal(11800m, inv.Total);
    }

    /// <summary>
    /// A renderer that draws nothing itself (so the built-in renderer does) and records the document
    /// each page was rendered from.
    /// </summary>
    private sealed class DocumentRecordingRenderer : IRendererPlugin
    {
        internal List<PdfDocument> Documents { get; } = new();
        public string Name => "Tetitulz.Tests.InvoiceDocumentRecordingRenderer";
        public string Description => "Records the document of each render.";
        public Version Version => new Version(1, 0, 0);
        public int Priority => 100;
        public void Initialize() { }
        public void Shutdown() { }
        public bool CanRender(PdfPage page, PdfDocument document) => true;
        public byte[] RenderPage(PdfPage page, PdfDocument document, int dpi) { Documents.Add(document); return null; }
    }

    /// <summary>
    /// The pages a scan sends to the engine are rendered from one open document: each was a
    /// one-shot open of the whole document, three of them a page.
    /// </summary>
    [Fact]
    public void EveryRecognisedPageOfAScan_IsRenderedFromOneOpenDocument()
    {
        var recorder = new DocumentRecordingRenderer();
        PdfPluginHost.Instance.Register(recorder);
        try
        {
            IsraeliInvoiceExtractor.Extract(new MemoryStream(BlankPages(3)), new IsraeliInvoiceOptions { OcrDpi = 72 });

            Assert.Equal(3, _engine.Calls);
            Assert.Equal(3, recorder.Documents.Count);
            Assert.Single(recorder.Documents.Distinct());
        }
        finally
        {
            PdfPluginHost.Instance.Unregister(recorder.Name);
        }
    }

    /// <summary>An engine that fails on its first page and finds nothing on its second.</summary>
    private sealed class FailThenNothingEngine : IOcrWordPlugin
    {
        private int _calls;
        public string Name => "Tetitulz.Tests.FailThenNothingEngine";
        public string Description => "Fails, then finds nothing.";
        public Version Version => new Version(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }
        public bool CanProcess(int components, int bitsPerComponent) => components == 3 && bitsPerComponent == 8;
        public string RecognizeText(byte[] pixels, int width, int height, int components) => null;
        public IReadOnlyList<OcrWordBox> RecognizeWords(byte[] pixels, int width, int height, int components)
            => _calls++ == 0 ? null : Array.Empty<OcrWordBox>();
    }

    /// <summary>
    /// Each recognised page's reason is its own: a page the engine failed on, then a page it found
    /// nothing on, report "failed" and "found no text" — not the first page's failure twice.
    /// </summary>
    [Fact]
    public void EachRecognisedPage_ReportsItsOwnReason()
    {
        PdfPluginHost.Instance.Unregister(_engine.Name);
        var engine = new FailThenNothingEngine();
        PdfPluginHost.Instance.Register(engine);
        try
        {
            var inv = IsraeliInvoiceExtractor.Extract(new MemoryStream(BlankPages(2)), new IsraeliInvoiceOptions { OcrDpi = 72 });

            var noText = inv.ExtractionFindings.Where(f => f.Code == InvoiceFindingCodes.NoTextLayer).Select(f => f.Message).ToList();
            Assert.Equal(2, noText.Count);
            Assert.Contains("returned no result", noText[0], StringComparison.Ordinal);
            Assert.Contains("recognition found no text", noText[1], StringComparison.Ordinal);
        }
        finally
        {
            PdfPluginHost.Instance.Unregister(engine.Name);
            PdfPluginHost.Instance.Register(_engine);
        }
    }

    private static byte[] BlankPages(int pages)
    {
        var kids = string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{4 + i} 0 R"));
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{kids}] /Count {pages} /MediaBox [0 0 612 792] >>",
            "<< /Length 0 >>\nstream\n\nendstream",
        };
        for (int i = 0; i < pages; i++) bodies.Add("<< /Type /Page /Parent 2 0 R /Contents 3 0 R >>");
        var sb = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++) { offsets.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>A page that has its own text is not sent to the engine.</summary>
    [Fact]
    public void APageWithTextIsNotRecognised()
    {
        var inv = IsraeliInvoiceExtractor.Extract(new MemoryStream(InvoicePdfs.Simple()));

        Assert.Equal(0, _engine.Calls);
        Assert.Equal(InvoiceTextSource.TextLayer, inv.TextSource);
    }
}
