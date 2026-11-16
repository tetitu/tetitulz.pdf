// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// A structured invoice embedded in a PDF — the XML half of a hybrid invoice (Factur-X,
    /// ZUGFeRD, XRechnung in a PDF, or a UBL invoice attached to one).
    /// </summary>
    public sealed class EmbeddedInvoice
    {
        /// <summary>The attachment's name.</summary>
        public string FileName { get; set; }

        /// <summary>"CII" (UN/CEFACT Cross Industry Invoice — Factur-X, ZUGFeRD) or "UBL".</summary>
        public string Syntax { get; set; }

        /// <summary>The invoice number.</summary>
        public string InvoiceNumber { get; set; }

        /// <summary>The UNTDID 1001 document type code: 380 an invoice, 381 a credit note.</summary>
        public string TypeCode { get; set; }

        /// <summary>The issue date.</summary>
        public DateTime? IssueDate { get; set; }

        /// <summary>The invoice currency (ISO 4217).</summary>
        public string Currency { get; set; }

        /// <summary>The seller's name.</summary>
        public string SellerName { get; set; }

        /// <summary>The seller's tax registration (VAT) number.</summary>
        public string SellerTaxId { get; set; }

        /// <summary>The buyer's name.</summary>
        public string BuyerName { get; set; }

        /// <summary>The buyer's tax registration (VAT) number.</summary>
        public string BuyerTaxId { get; set; }

        /// <summary>The sum of the lines' net amounts.</summary>
        public decimal? LineTotal { get; set; }

        /// <summary>The amount tax is computed on (before tax).</summary>
        public decimal? TaxBasisTotal { get; set; }

        /// <summary>The tax, in the invoice currency.</summary>
        public decimal? TaxTotal { get; set; }

        /// <summary>The total, tax included.</summary>
        public decimal? GrandTotal { get; set; }

        /// <summary>The amount due for payment.</summary>
        public decimal? DuePayable { get; set; }

        /// <summary>The first tax rate stated, in percent.</summary>
        public decimal? TaxRatePercent { get; set; }

        /// <summary>The invoice lines.</summary>
        public IList<InvoiceLineItem> Lines { get; set; } = new List<InvoiceLineItem>();
    }

    /// <summary>
    /// Reads the structured invoice a hybrid PDF carries as an attachment.
    /// </summary>
    /// <remarks>
    /// <para><b>Read this before the page.</b> A hybrid invoice's XML is the data its page is a
    /// picture of: no layout heuristic, no OCR, no bidi. <see cref="IsraeliInvoiceExtractor"/> takes
    /// its values over the page's and reports where the two disagree.</para>
    /// <para><b>Where it is looked for:</b> an attachment named <c>factur-x.xml</c>,
    /// <c>zugferd-invoice.xml</c>, <c>ZUGFeRD-invoice.xml</c> or <c>xrechnung.xml</c> first, then any
    /// other <c>.xml</c> attachment whose root is a CII <c>CrossIndustryInvoice</c> or a UBL
    /// <c>Invoice</c> / <c>CreditNote</c> — at most <see cref="MaxCandidates"/>, each at most
    /// <see cref="MaxXmlBytes"/>.</para>
    /// <para><b>Elements are found by local name</b>, so the profiles and versions of each syntax
    /// (ZUGFeRD 1 and 2, Factur-X, EN 16931, UBL 2.x) read alike. Only the header values and the
    /// lines are read; nothing is validated against a schema.</para>
    /// <para><b>The XML is hostile input</b>: a DTD is refused (which refuses entity expansion with
    /// it), nothing is resolved, an attachment is decoded only as far as
    /// <see cref="MaxXmlBytes"/>, and a document nested deeper than <see cref="MaxXmlDepth"/> is
    /// refused before its tree is built.</para>
    /// <para><b>A Try method answers by its result.</b> A corrupt attachment, one too large, a
    /// catalog that cannot be walked: each is passed over with its reason in
    /// <see cref="LastError"/>, never thrown — but for an encrypted document, which is refused as
    /// documented. When none is read, <see cref="LastError"/> also says what was never tried: the
    /// <c>.xml</c> attachments past <see cref="MaxCandidates"/>, and a listing the attachment name
    /// tree's limit cut short.</para>
    /// </remarks>
    public static class EmbeddedInvoiceReader
    {
        /// <summary>The attachment names hybrid-invoice standards give their XML.</summary>
        public static IReadOnlyList<string> KnownFileNames { get; } =
            new[] { "factur-x.xml", "zugferd-invoice.xml", "ZUGFeRD-invoice.xml", "xrechnung.xml" };

        /// <summary>The most attachments tried as XML invoices.</summary>
        internal const int MaxCandidates = 8;

        /// <summary>The largest attachment decoded and parsed as an XML invoice.</summary>
        internal const int MaxXmlBytes = 32 * 1024 * 1024;

        /// <summary>
        /// The deepest element nesting read. CII and UBL invoices nest about ten deep; reading a
        /// value walks its element's subtree, and a million levels of it overflowed the stack —
        /// which no handler catches.
        /// </summary>
        internal const int MaxXmlDepth = 64;

        /// <summary>
        /// Why the last read on this thread found an invoice-like attachment it could not read, or
        /// left attachments untried, or <c>null</c>. <c>[ThreadStatic]</c>: read it on the calling
        /// thread.
        /// </summary>
        public static string LastError => _lastError;

        [ThreadStatic] private static string _lastError;

        /// <summary>
        /// Finds and reads the structured invoice <paramref name="pdf"/> carries.
        /// </summary>
        /// <returns>
        /// <c>false</c> when the PDF carries none this reader can read; <see cref="LastError"/>
        /// then says why, when there was a candidate.
        /// </returns>
        /// <exception cref="PdfEncryptedDocumentException">
        /// The PDF is encrypted: its attachments are ciphertext. Decrypt it first.
        /// </exception>
        public static bool TryRead(Stream pdf, out EmbeddedInvoice invoice)
        {
            _lastError = null;
            invoice = null;
            if (pdf == null) throw new ArgumentNullException(nameof(pdf));

            Stream source = Core.IO.SeekableStream.Ensure(pdf, rewind: true, out bool owned);
            try
            {
                source.Position = 0;
                using var doc = PdfDocument.Open(source);
                var files = PdfAttachmentManager.EmbeddedFileStreams(doc);
                string listedInPart = PdfAttachmentManager.LastListError;   // a name tree cut short

                var known = new HashSet<string>(KnownFileNames, StringComparer.OrdinalIgnoreCase);
                var xmlFiles = files
                    .Where(f => f.Name != null && f.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => known.Contains(f.Name) ? 0 : 1)
                    .ToList();

                // Two file specifications naming one stream decode it once.
                var decoded = new HashSet<object>(PdfReferenceEquality.Instance);
                foreach (var (name, stream) in xmlFiles.Take(MaxCandidates))
                {
                    if (stream == null) { _lastError = $"{name}: its file specification holds no embedded file"; continue; }
                    if (!decoded.Add(stream)) continue;
                    if (!stream.TryGetDecodedData(MaxXmlBytes, out byte[] xml, out string reason))
                    {
                        _lastError = $"{name}: not decoded ({reason})";
                        continue;
                    }
                    if (Parse(xml, name, known.Contains(name), out invoice)) return true;
                }

                // None read: what was never tried is said beside why the tried ones failed.
                if (xmlFiles.Count > MaxCandidates)
                    Note($"{xmlFiles.Count - MaxCandidates} more .xml attachment(s) not tried: at most {MaxCandidates} are");
                if (listedInPart != null)
                    Note($"the attachments were listed only in part: {listedInPart}");
                return false;
            }
            catch (PdfEncryptedDocumentException)
            {
                throw;
            }
            catch (Exception ex) when (ex is PdfException || ex is InvalidOperationException)
            {
                _lastError = $"the attachments could not be read: {ex.Message}";
                invoice = null;
                return false;
            }
            finally
            {
                if (owned) source.Dispose();
            }
        }

        // Adds a reason to LastError, after any already there.
        private static void Note(string reason)
            => _lastError = _lastError == null ? reason : _lastError + "; " + reason;

        /// <summary>
        /// Reads a CII or UBL invoice from its XML bytes.
        /// </summary>
        /// <returns><c>false</c> for XML that is malformed, nested too deep, or neither syntax.</returns>
        public static bool TryParse(byte[] xml, string fileName, out EmbeddedInvoice invoice)
        {
            _lastError = null;
            return Parse(xml, fileName, expected: true, out invoice);
        }

        // TryParse's body, which leaves LastError alone on success so that TryRead reports the
        // last candidate that failed, not the last one tried. A root of neither syntax is a
        // failure worth reporting only where an invoice was expected — a known name, or a caller's
        // explicit parse — and not for any other .xml attachment.
        private static bool Parse(byte[] xml, string fileName, bool expected, out EmbeddedInvoice invoice)
        {
            invoice = null;
            if (xml == null || xml.Length == 0)
            {
                if (expected) _lastError = $"{fileName}: empty";
                return false;
            }

            XDocument doc;
            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    IgnoreComments = true,
                    IgnoreProcessingInstructions = true,
                    MaxCharactersInDocument = MaxXmlBytes,
                };

                // The depth first, in one streaming pass: the tree is built, and read, only for a
                // document an invoice's shape can have.
                using (var probeBytes = new MemoryStream(xml, writable: false))
                using (var probe = XmlReader.Create(probeBytes, settings))
                {
                    while (probe.Read())
                    {
                        if (probe.Depth > MaxXmlDepth)
                        {
                            _lastError = $"{fileName}: XML nested deeper than {MaxXmlDepth} levels";
                            return false;
                        }
                    }
                }

                using var ms = new MemoryStream(xml, writable: false);
                using var reader = XmlReader.Create(ms, settings);
                doc = XDocument.Load(reader);
            }
            catch (XmlException ex)
            {
                _lastError = $"{fileName}: not readable XML ({ex.Message})";
                return false;
            }

            XElement root = doc.Root;
            switch (root?.Name.LocalName)
            {
                case "CrossIndustryInvoice":
                    invoice = ReadCii(root);
                    break;
                case "Invoice":
                case "CreditNote":
                    invoice = ReadUbl(root);
                    break;
                default:
                    if (expected)
                        _lastError = $"{fileName}: neither a CII nor a UBL invoice (root <{root?.Name.LocalName}>)";
                    return false;
            }
            invoice.FileName = fileName;
            return true;
        }

        // ----------------------------------------------------------------
        //  UN/CEFACT Cross Industry Invoice (Factur-X, ZUGFeRD 2, XRechnung CII)
        // ----------------------------------------------------------------

        private static EmbeddedInvoice ReadCii(XElement root)
        {
            var document = Child(root, "ExchangedDocument");
            var transaction = Child(root, "SupplyChainTradeTransaction");
            var agreement = Child(transaction, "ApplicableHeaderTradeAgreement");
            var settlement = Child(transaction, "ApplicableHeaderTradeSettlement");
            var summation = Child(settlement, "SpecifiedTradeSettlementHeaderMonetarySummation");
            var seller = Child(agreement, "SellerTradeParty");
            var buyer = Child(agreement, "BuyerTradeParty");
            string currency = Text(Child(settlement, "InvoiceCurrencyCode"));

            var invoice = new EmbeddedInvoice
            {
                Syntax = "CII",
                InvoiceNumber = Text(Child(document, "ID")),
                TypeCode = Text(Child(document, "TypeCode")),
                IssueDate = CiiDate(Child(Child(document, "IssueDateTime"), "DateTimeString")),
                Currency = currency,
                SellerName = Text(Child(seller, "Name")),
                SellerTaxId = CiiTaxId(seller),
                BuyerName = Text(Child(buyer, "Name")),
                BuyerTaxId = CiiTaxId(buyer),
                LineTotal = Amount(Child(summation, "LineTotalAmount")),
                TaxBasisTotal = Amount(Child(summation, "TaxBasisTotalAmount")),
                TaxTotal = AmountInCurrency(Children(summation, "TaxTotalAmount"), currency),
                GrandTotal = Amount(Child(summation, "GrandTotalAmount")),
                DuePayable = Amount(Child(summation, "DuePayableAmount")),
                TaxRatePercent = Amount(Child(Child(settlement, "ApplicableTradeTax"), "RateApplicablePercent")),
            };

            foreach (var line in Children(transaction, "IncludedSupplyChainTradeLineItem"))
            {
                var product = Child(line, "SpecifiedTradeProduct");
                invoice.Lines.Add(new InvoiceLineItem
                {
                    Line = invoice.Lines.Count + 1,
                    CatalogNumber = Text(Child(product, "SellerAssignedID")),
                    Description = Text(Child(product, "Name")),
                    Quantity = Amount(Child(Child(line, "SpecifiedLineTradeDelivery"), "BilledQuantity")),
                    UnitPrice = Amount(Child(Child(Child(line, "SpecifiedLineTradeAgreement"),
                                                   "NetPriceProductTradePrice"), "ChargeAmount")),
                    Amount = Amount(Child(Child(Child(line, "SpecifiedLineTradeSettlement"),
                                                "SpecifiedTradeSettlementLineMonetarySummation"), "LineTotalAmount")),
                });
            }
            return invoice;
        }

        // The VAT registration (schemeID "VA") before any other tax registration.
        private static string CiiTaxId(XElement party)
        {
            var registrations = Children(party, "SpecifiedTaxRegistration").Select(r => Child(r, "ID")).Where(i => i != null).ToList();
            var vat = registrations.FirstOrDefault(i => (string)i.Attribute("schemeID") == "VA");
            return Text(vat ?? registrations.FirstOrDefault());
        }

        // Format 102 is CCYYMMDD; anything else is tried as an ISO date.
        private static DateTime? CiiDate(XElement e)
        {
            string s = Text(e);
            if (s == null) return null;
            if (DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d)) return d;
            return IsoDate(s);
        }

        // ----------------------------------------------------------------
        //  OASIS UBL 2.x Invoice / CreditNote
        // ----------------------------------------------------------------

        private static EmbeddedInvoice ReadUbl(XElement root)
        {
            bool credit = root.Name.LocalName == "CreditNote";
            var supplier = Child(Child(root, "AccountingSupplierParty"), "Party");
            var customer = Child(Child(root, "AccountingCustomerParty"), "Party");
            var totals = Child(root, "LegalMonetaryTotal");
            string currency = Text(Child(root, "DocumentCurrencyCode"));
            var taxTotals = Children(root, "TaxTotal").ToList();

            var invoice = new EmbeddedInvoice
            {
                Syntax = "UBL",
                InvoiceNumber = Text(Child(root, "ID")),
                TypeCode = Text(Child(root, credit ? "CreditNoteTypeCode" : "InvoiceTypeCode")) ?? (credit ? "381" : "380"),
                IssueDate = IsoDate(Text(Child(root, "IssueDate"))),
                Currency = currency,
                SellerName = UblPartyName(supplier),
                SellerTaxId = Text(Child(Child(supplier, "PartyTaxScheme"), "CompanyID")),
                BuyerName = UblPartyName(customer),
                BuyerTaxId = Text(Child(Child(customer, "PartyTaxScheme"), "CompanyID")),
                LineTotal = Amount(Child(totals, "LineExtensionAmount")),
                TaxBasisTotal = Amount(Child(totals, "TaxExclusiveAmount")),
                TaxTotal = AmountInCurrency(taxTotals.Select(t => Child(t, "TaxAmount")), currency),
                GrandTotal = Amount(Child(totals, "TaxInclusiveAmount")),
                DuePayable = Amount(Child(totals, "PayableAmount")),
                TaxRatePercent = Amount(taxTotals.Select(t => Child(Child(Child(t, "TaxSubtotal"), "TaxCategory"), "Percent"))
                                                 .FirstOrDefault(p => p != null)),
            };

            foreach (var line in Children(root, credit ? "CreditNoteLine" : "InvoiceLine"))
            {
                var item = Child(line, "Item");
                invoice.Lines.Add(new InvoiceLineItem
                {
                    Line = invoice.Lines.Count + 1,
                    CatalogNumber = Text(Child(Child(item, "SellersItemIdentification"), "ID")),
                    Description = Text(Child(item, "Name")),
                    Quantity = Amount(Child(line, credit ? "CreditedQuantity" : "InvoicedQuantity")),
                    UnitPrice = Amount(Child(Child(line, "Price"), "PriceAmount")),
                    Amount = Amount(Child(line, "LineExtensionAmount")),
                });
            }
            return invoice;
        }

        private static string UblPartyName(XElement party)
            => Text(Child(Child(party, "PartyName"), "Name"))
               ?? Text(Child(Child(party, "PartyLegalEntity"), "RegistrationName"));

        // ----------------------------------------------------------------
        //  Local-name navigation
        // ----------------------------------------------------------------

        private static XElement Child(XElement e, string localName)
            => e?.Elements().FirstOrDefault(x => x.Name.LocalName == localName);

        private static IEnumerable<XElement> Children(XElement e, string localName)
            => e?.Elements().Where(x => x.Name.LocalName == localName) ?? Enumerable.Empty<XElement>();

        // The element's own text, from its direct text children. XElement.Value would gather the
        // whole subtree, recursively.
        private static string Text(XElement e)
        {
            if (e == null) return null;
            string s = string.Concat(e.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
            return s.Length == 0 ? null : s;
        }

        private static decimal? Amount(XElement e)
        {
            string s = Text(e);
            return s != null && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal v)
                ? v : (decimal?)null;
        }

        // A tax total is stated once per currency when the accounting currency differs; the one in
        // the invoice currency is the invoice's.
        private static decimal? AmountInCurrency(IEnumerable<XElement> amounts, string currency)
        {
            var list = amounts.Where(a => a != null).ToList();
            var match = list.FirstOrDefault(a => currency != null && (string)a.Attribute("currencyID") == currency);
            return Amount(match ?? list.FirstOrDefault());
        }

        private static DateTime? IsoDate(string s)
            => s != null && DateTime.TryParseExact(s.Length >= 10 ? s.Substring(0, 10) : s, "yyyy-MM-dd",
                                                   CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d)
                ? d : (DateTime?)null;
    }
}
