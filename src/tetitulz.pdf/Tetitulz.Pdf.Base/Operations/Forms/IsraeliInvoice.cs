// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// The kind of document an Israeli invoice says it is, valued by the Israel Tax Authority's
    /// document-type codes: the uniform structure's <c>סוג מסמך</c> (instructions version 1.31,
    /// appendix 1), and the Israel Invoices service's for the codes it added (332).
    /// </summary>
    public enum IsraeliDocumentType
    {
        /// <summary>No title the extractor recognises.</summary>
        Unknown = 0,

        /// <summary>
        /// <c>חשבונית עסקה</c> — officially <c>חשבונית / חשבונית עסקה</c>, and printed as
        /// <c>חשבון עסקה</c> too: a demand for payment, not a tax invoice. No input VAT is deducted
        /// on it.
        /// </summary>
        TransactionInvoice = 300,

        /// <summary><c>חשבונית מס</c>.</summary>
        TaxInvoice = 305,

        /// <summary><c>חשבונית ריכוז</c> / <c>חשבונית מס ריכוז</c> — one tax invoice for several delivery notes.</summary>
        PeriodicTaxInvoice = 310,

        /// <summary><c>חשבונית מס / קבלה</c> — a tax invoice and its receipt in one document.</summary>
        TaxInvoiceReceipt = 320,

        /// <summary><c>חשבונית זיכוי</c> / <c>חשבונית מס זיכוי</c> — reverses all or part of an earlier invoice.</summary>
        CreditInvoice = 330,

        /// <summary>
        /// <c>חשבון עסקה / פרופורמה</c> — a preliminary document before the tax invoice, not a tax
        /// invoice itself. The Tax Authority may approve it in advance; that approval number is not
        /// an allocation number, and the document says so.
        /// </summary>
        Proforma = 332,

        /// <summary><c>חשבונית סוכן</c> / <c>חשבונית מס סוכן</c> — a tax invoice an agent issues for its principal.</summary>
        AgentInvoice = 345,

        /// <summary><c>קבלה</c> — confirms a payment; not a tax invoice.</summary>
        Receipt = 400,

        /// <summary><c>קבלה על תרומה</c> — a nonprofit's receipt for a donation; not a tax invoice.</summary>
        DonationReceipt = 405,
    }

    /// <summary>Where an <see cref="IsraeliInvoice"/>'s values were read from.</summary>
    public enum InvoiceTextSource
    {
        /// <summary>Nothing could be read: no text layer, and no OCR result.</summary>
        None,
        /// <summary>The PDF's own text.</summary>
        TextLayer,
        /// <summary>Optical character recognition of the rendered pages, for one page or more.</summary>
        Ocr,
    }

    /// <summary>How much a validation finding matters.</summary>
    public enum InvoiceFindingSeverity
    {
        /// <summary>Worth knowing; nothing is wrong.</summary>
        Info,
        /// <summary>Something a reviewer should look at: possibly wrong, or not checkable.</summary>
        Warning,
        /// <summary>The document contradicts itself or a rule it must meet.</summary>
        Error,
    }

    /// <summary>One thing <see cref="IsraeliInvoiceValidator"/> found.</summary>
    public sealed class InvoiceFinding
    {
        public InvoiceFinding(InvoiceFindingSeverity severity, string code, string message)
        {
            Severity = severity;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
        }

        /// <summary>How much it matters.</summary>
        public InvoiceFindingSeverity Severity { get; }

        /// <summary>A stable identifier — one of the <see cref="InvoiceFindingCodes"/>.</summary>
        public string Code { get; }

        /// <summary>What was found, in English, with the numbers that show it.</summary>
        public string Message { get; }

        public override string ToString() => $"{Severity} {Code}: {Message}";
    }

    /// <summary>The codes an <see cref="InvoiceFinding"/> carries.</summary>
    public static class InvoiceFindingCodes
    {
        /// <summary>No invoice number was found.</summary>
        public const string MissingInvoiceNumber = "MissingInvoiceNumber";
        /// <summary>No date was found.</summary>
        public const string MissingDate = "MissingDate";
        /// <summary>No total was found.</summary>
        public const string MissingTotal = "MissingTotal";
        /// <summary>No supplier identification number was found.</summary>
        public const string MissingSupplierId = "MissingSupplierId";
        /// <summary>The supplier's number fails the nine-digit check digit.</summary>
        public const string InvalidSupplierId = "InvalidSupplierId";
        /// <summary>The customer's number fails the nine-digit check digit.</summary>
        public const string InvalidCustomerId = "InvalidCustomerId";
        /// <summary>The supplier and the customer carry the same number.</summary>
        public const string SupplierIsCustomer = "SupplierIsCustomer";
        /// <summary>The subtotal and the VAT do not add up to the total.</summary>
        public const string TotalMismatch = "TotalMismatch";
        /// <summary>
        /// The VAT is not the statutory rate of the amount before VAT — a misread, or an invoice
        /// mixing exempt and taxable items.
        /// </summary>
        public const string VatMismatch = "VatMismatch";
        /// <summary>The printed VAT rate is not the statutory rate on the invoice's date.</summary>
        public const string PrintedVatRateMismatch = "PrintedVatRateMismatch";
        /// <summary>The invoice carries no VAT: zero-rated (export, Eilat) or exempt.</summary>
        public const string ZeroVat = "ZeroVat";
        /// <summary>No statutory rate is known for the invoice's date, so the VAT was not checked.</summary>
        public const string VatRateUnknown = "VatRateUnknown";
        /// <summary>The line items do not add up to the subtotal.</summary>
        public const string LineItemsMismatch = "LineItemsMismatch";
        /// <summary>The invoice appears to need an allocation number and carries none.</summary>
        public const string AllocationNumberMissing = "AllocationNumberMissing";
        /// <summary>The allocation number is not nine digits.</summary>
        public const string AllocationNumberMalformed = "AllocationNumberMalformed";
        /// <summary>The document is not a tax invoice: no input VAT is deducted on it.</summary>
        public const string NotATaxInvoice = "NotATaxInvoice";
        /// <summary>The document's type was not recognised.</summary>
        public const string UnknownDocumentType = "UnknownDocumentType";
        /// <summary>The document carries no digital signature.</summary>
        public const string NotDigitallySigned = "NotDigitallySigned";
        /// <summary>The caller's verification found the signature invalid.</summary>
        public const string SignatureInvalid = "SignatureInvalid";
        /// <summary>A page has no text layer, and OCR produced nothing for it.</summary>
        public const string NoTextLayer = "NoTextLayer";
        /// <summary>
        /// A page's text looked like Hebrew in a legacy 8-bit encoding read as Latin letters (the
        /// fonts carry no Unicode mapping); it was decoded as Windows-1255 and read in visual order.
        /// </summary>
        public const string LegacyHebrewEncoding = "LegacyHebrewEncoding";
        /// <summary>Values were taken from an embedded structured invoice (Factur-X, ZUGFeRD, UBL).</summary>
        public const string EmbeddedStructuredInvoice = "EmbeddedStructuredInvoice";
        /// <summary>The embedded structured invoice and the visible page disagree.</summary>
        public const string EmbeddedInvoiceMismatch = "EmbeddedInvoiceMismatch";
        /// <summary>
        /// The PDF carries an invoice-like attachment that could not be read — corrupt, too large,
        /// nested too deep, or neither CII nor UBL — or attachments the reader never tried (past
        /// its limit, or a listing cut short), so the page's values stand alone.
        /// </summary>
        public const string EmbeddedInvoiceUnreadable = "EmbeddedInvoiceUnreadable";
        /// <summary>
        /// The document says its input VAT may not be deducted (<c>אין לנכות מס תשומות בגין
        /// חשבונית זו</c>): issued without an allocation number, after a refusal. No number is
        /// expected on it.
        /// </summary>
        public const string InputVatNotDeductible = "InputVatNotDeductible";
        /// <summary>
        /// The amounts are in a foreign currency and no exchange rate was supplied, so the
        /// allocation threshold, which is in shekels, could not be judged.
        /// </summary>
        public const string AllocationThresholdNotJudged = "AllocationThresholdNotJudged";
        /// <summary>The amounts are too large for a check's arithmetic, which was skipped.</summary>
        public const string AmountOutOfRange = "AmountOutOfRange";
        /// <summary>
        /// The field search stopped at its work budget (<c>PdfLimits.MaxFieldExtractionWork</c>),
        /// so some label matches were never examined.
        /// </summary>
        public const string ExtractionIncomplete = "ExtractionIncomplete";
        /// <summary>
        /// A page holds text a reader does not see — invisible beside visible text, off the page, or
        /// under a point high — and it was read with the rest.
        /// </summary>
        public const string TextNotShown = "TextNotShown";
    }

    /// <summary>One line of an invoice's item table.</summary>
    public sealed class InvoiceLineItem
    {
        /// <summary>One-based position among the invoice's line items.</summary>
        public int Line { get; set; }

        /// <summary>Zero-based page the line was read from (-1 when it came from embedded XML).</summary>
        public int PageIndex { get; set; } = -1;

        /// <summary>The catalogue number (<c>מק"ט</c>), as printed.</summary>
        public string CatalogNumber { get; set; }

        /// <summary>The description, in reading order; a description wrapped onto further rows is joined.</summary>
        public string Description { get; set; }

        /// <summary>The quantity, when the line states one.</summary>
        public decimal? Quantity { get; set; }

        /// <summary>The unit price, when the line states one.</summary>
        public decimal? UnitPrice { get; set; }

        /// <summary>The discount column as printed (an amount or a percentage).</summary>
        public string Discount { get; set; }

        /// <summary>The line's amount, when the line states one.</summary>
        public decimal? Amount { get; set; }

        /// <summary>
        /// Whether <see cref="Amount"/> holds the line's VAT — its column's header says so
        /// (<c>סה"כ כולל מע"מ</c>, "incl. VAT") — so the lines add up to the invoice's total, not
        /// its subtotal.
        /// </summary>
        public bool AmountIncludesVat { get; set; }

        public override string ToString() => $"{Line}: {Description} {Quantity} x {UnitPrice} = {Amount}";
    }

    /// <summary>An amount taxed at one VAT rate: one part of <see cref="IsraeliInvoice.RatedSubtotals"/>.</summary>
    public sealed class IsraeliRatedAmount
    {
        /// <summary>The amount taxed at <paramref name="rate"/> percent.</summary>
        public IsraeliRatedAmount(decimal rate, decimal amount)
        {
            Rate = rate;
            Amount = amount;
        }

        /// <summary>The VAT rate, in percent.</summary>
        public decimal Rate { get; }

        /// <summary>The amount before VAT taxed at the rate.</summary>
        public decimal Amount { get; }
    }

    /// <summary>
    /// What <see cref="IsraeliInvoiceExtractor"/> read from an invoice, and what
    /// <see cref="IsraeliInvoiceValidator"/> found about it.
    /// </summary>
    /// <remarks>
    /// Every value is settable, so a reviewer's correction can be applied and the invoice
    /// validated again with <see cref="IsraeliInvoiceValidator.Validate"/>. A value that was not
    /// found is <c>null</c>; nothing is defaulted.
    /// </remarks>
    public sealed class IsraeliInvoice
    {
        /// <summary>Where the invoice came from — a file name, or whatever the caller sets.</summary>
        public string Source { get; set; }

        /// <summary>The document's type, from its title.</summary>
        public IsraeliDocumentType DocumentType { get; set; }

        /// <summary>The invoice (document) number, as printed.</summary>
        public string InvoiceNumber { get; set; }

        /// <summary>The invoice's date.</summary>
        public DateTime? Date { get; set; }

        /// <summary>The supplier's identification number (<c>עוסק מורשה</c> / <c>ח.פ.</c>), as printed.</summary>
        public string SupplierId { get; set; }

        /// <summary>The customer's identification number, as printed.</summary>
        public string CustomerId { get; set; }

        /// <summary>The Tax Authority's allocation number (<c>מספר הקצאה</c>), as printed.</summary>
        public string AllocationNumber { get; set; }

        /// <summary>
        /// The ISO 4217 code of the totals' currency: "ILS", "USD" or "EUR" from a sign or code on
        /// the page, any code from an embedded invoice; <c>null</c> when nothing names one.
        /// </summary>
        public string Currency { get; set; }

        /// <summary>
        /// The amount before VAT. On an invoice that prints its exempt or zero-rated part apart
        /// (<see cref="ExemptAmount"/>) this may be the taxable part alone; the validation accepts
        /// either reading.
        /// </summary>
        public decimal? Subtotal { get; set; }

        /// <summary>
        /// The part exempt from VAT or zero-rated (<c>סה"כ פטור ממע"מ</c>), when the invoice prints
        /// it apart from the taxable part — as one mixing produce and taxable goods must.
        /// </summary>
        public decimal? ExemptAmount { get; set; }

        /// <summary>
        /// Whether the document states that its input VAT may not be deducted
        /// (<c>אין לנכות מס תשומות בגין חשבונית זו</c>) — a tax invoice issued without an
        /// allocation number after a refusal.
        /// </summary>
        public bool StatesInputVatNotDeductible { get; set; }

        /// <summary>
        /// The VAT rate the invoice prints, in percent; <c>null</c> on an invoice taxed at several
        /// rates (<see cref="RatedSubtotals"/>).
        /// </summary>
        public decimal? VatRate { get; set; }

        /// <summary>
        /// The amounts taxed at each rate, when the invoice states more than one — a bill spanning a
        /// rate change prints <c>סה"כ סכומים ב-17% מע"מ</c> and <c>ב-18%</c> on lines of their own;
        /// <see cref="Subtotal"/> is then their sum and the VAT is checked as the sum of each at its
        /// rate. Empty for an invoice at one rate.
        /// </summary>
        public IList<IsraeliRatedAmount> RatedSubtotals { get; set; } = new List<IsraeliRatedAmount>();

        /// <summary>The VAT amount.</summary>
        public decimal? Vat { get; set; }

        /// <summary>The total to pay, VAT included.</summary>
        public decimal? Total { get; set; }

        /// <summary>The item table's lines, in order.</summary>
        public IList<InvoiceLineItem> LineItems { get; set; } = new List<InvoiceLineItem>();

        /// <summary>Whether the document carries a digital signature with a value.</summary>
        public bool IsDigitallySigned { get; set; }

        /// <summary>
        /// The caller's verdict on the signature, from <c>Tetitulz.Pdf.Signing</c>'s verification:
        /// <c>true</c> valid, <c>false</c> invalid, <c>null</c> not verified. The extractor never
        /// sets it — Base does not verify signatures.
        /// </summary>
        public bool? SignatureValid { get; set; }

        /// <summary>Where the text the values were read from came from.</summary>
        public InvoiceTextSource TextSource { get; set; }

        /// <summary>The structured invoice embedded in the PDF, when it carries one.</summary>
        public EmbeddedInvoice EmbeddedInvoice { get; set; }

        /// <summary>The label matches the values were read from, one per field searched for.</summary>
        public IList<FieldResult> Fields { get; set; } = new List<FieldResult>();

        /// <summary>
        /// What the extractor itself noticed about the document — a page with no text, text in a
        /// legacy encoding, an embedded structured invoice. Kept apart from validation so that
        /// validating again does not lose it.
        /// </summary>
        public IList<InvoiceFinding> ExtractionFindings { get; set; } = new List<InvoiceFinding>();

        /// <summary>
        /// Everything found: <see cref="ExtractionFindings"/> followed by the validation's own.
        /// Replaced by each <see cref="IsraeliInvoiceValidator.Validate"/>.
        /// </summary>
        public IList<InvoiceFinding> Findings { get; set; } = new List<InvoiceFinding>();
    }
}
