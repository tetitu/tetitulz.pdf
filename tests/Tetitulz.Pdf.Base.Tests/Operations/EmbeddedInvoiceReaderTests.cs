// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Forms;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="EmbeddedInvoiceReader"/>: the XML half of a hybrid invoice, in both syntaxes, found
/// in a PDF's attachments and taken over the page by <see cref="IsraeliInvoiceExtractor"/>.
/// </summary>
public class EmbeddedInvoiceReaderTests
{
    internal const string Cii = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<rsm:CrossIndustryInvoice xmlns:rsm=""urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100""
    xmlns:ram=""urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100""
    xmlns:udt=""urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100"">
  <rsm:ExchangedDocument>
    <ram:ID>INV-2026-001</ram:ID>
    <ram:TypeCode>380</ram:TypeCode>
    <ram:IssueDateTime><udt:DateTimeString format=""102"">20260715</udt:DateTimeString></ram:IssueDateTime>
  </rsm:ExchangedDocument>
  <rsm:SupplyChainTradeTransaction>
    <ram:IncludedSupplyChainTradeLineItem>
      <ram:AssociatedDocumentLineDocument><ram:LineID>1</ram:LineID></ram:AssociatedDocumentLineDocument>
      <ram:SpecifiedTradeProduct><ram:SellerAssignedID>A-1</ram:SellerAssignedID><ram:Name>Consulting</ram:Name></ram:SpecifiedTradeProduct>
      <ram:SpecifiedLineTradeAgreement><ram:NetPriceProductTradePrice><ram:ChargeAmount>500.00</ram:ChargeAmount></ram:NetPriceProductTradePrice></ram:SpecifiedLineTradeAgreement>
      <ram:SpecifiedLineTradeDelivery><ram:BilledQuantity unitCode=""H87"">2</ram:BilledQuantity></ram:SpecifiedLineTradeDelivery>
      <ram:SpecifiedLineTradeSettlement><ram:SpecifiedTradeSettlementLineMonetarySummation><ram:LineTotalAmount>1000.00</ram:LineTotalAmount></ram:SpecifiedTradeSettlementLineMonetarySummation></ram:SpecifiedLineTradeSettlement>
    </ram:IncludedSupplyChainTradeLineItem>
    <ram:ApplicableHeaderTradeAgreement>
      <ram:SellerTradeParty>
        <ram:Name>Seller GmbH</ram:Name>
        <ram:SpecifiedTaxRegistration><ram:ID schemeID=""FC"">123/456</ram:ID></ram:SpecifiedTaxRegistration>
        <ram:SpecifiedTaxRegistration><ram:ID schemeID=""VA"">DE123456789</ram:ID></ram:SpecifiedTaxRegistration>
      </ram:SellerTradeParty>
      <ram:BuyerTradeParty><ram:Name>Buyer Ltd</ram:Name></ram:BuyerTradeParty>
    </ram:ApplicableHeaderTradeAgreement>
    <ram:ApplicableHeaderTradeSettlement>
      <ram:InvoiceCurrencyCode>USD</ram:InvoiceCurrencyCode>
      <ram:ApplicableTradeTax><ram:RateApplicablePercent>19.00</ram:RateApplicablePercent></ram:ApplicableTradeTax>
      <ram:SpecifiedTradeSettlementHeaderMonetarySummation>
        <ram:LineTotalAmount>1000.00</ram:LineTotalAmount>
        <ram:TaxBasisTotalAmount>1000.00</ram:TaxBasisTotalAmount>
        <ram:TaxTotalAmount currencyID=""EUR"">175.00</ram:TaxTotalAmount>
        <ram:TaxTotalAmount currencyID=""USD"">190.00</ram:TaxTotalAmount>
        <ram:GrandTotalAmount>1190.00</ram:GrandTotalAmount>
        <ram:DuePayableAmount>1190.00</ram:DuePayableAmount>
      </ram:SpecifiedTradeSettlementHeaderMonetarySummation>
    </ram:ApplicableHeaderTradeSettlement>
  </rsm:SupplyChainTradeTransaction>
</rsm:CrossIndustryInvoice>";

    private const string Ubl = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<Invoice xmlns=""urn:oasis:names:specification:ubl:schema:xsd:Invoice-2""
    xmlns:cac=""urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2""
    xmlns:cbc=""urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"">
  <cbc:ID>U-77</cbc:ID>
  <cbc:IssueDate>2026-07-15</cbc:IssueDate>
  <cbc:InvoiceTypeCode>380</cbc:InvoiceTypeCode>
  <cbc:DocumentCurrencyCode>ILS</cbc:DocumentCurrencyCode>
  <cac:AccountingSupplierParty><cac:Party>
    <cac:PartyName><cbc:Name>ספק בע""מ</cbc:Name></cac:PartyName>
    <cac:PartyTaxScheme><cbc:CompanyID>514286558</cbc:CompanyID></cac:PartyTaxScheme>
  </cac:Party></cac:AccountingSupplierParty>
  <cac:AccountingCustomerParty><cac:Party>
    <cac:PartyLegalEntity><cbc:RegistrationName>Buyer</cbc:RegistrationName></cac:PartyLegalEntity>
  </cac:Party></cac:AccountingCustomerParty>
  <cac:TaxTotal>
    <cbc:TaxAmount currencyID=""ILS"">180.00</cbc:TaxAmount>
    <cac:TaxSubtotal><cac:TaxCategory><cbc:Percent>18</cbc:Percent></cac:TaxCategory></cac:TaxSubtotal>
  </cac:TaxTotal>
  <cac:LegalMonetaryTotal>
    <cbc:LineExtensionAmount>1000.00</cbc:LineExtensionAmount>
    <cbc:TaxExclusiveAmount>1000.00</cbc:TaxExclusiveAmount>
    <cbc:TaxInclusiveAmount>1180.00</cbc:TaxInclusiveAmount>
    <cbc:PayableAmount>1180.00</cbc:PayableAmount>
  </cac:LegalMonetaryTotal>
  <cac:InvoiceLine>
    <cbc:ID>1</cbc:ID>
    <cbc:InvoicedQuantity unitCode=""C62"">4</cbc:InvoicedQuantity>
    <cbc:LineExtensionAmount currencyID=""ILS"">1000.00</cbc:LineExtensionAmount>
    <cac:Item><cbc:Name>שירות</cbc:Name><cac:SellersItemIdentification><cbc:ID>S-9</cbc:ID></cac:SellersItemIdentification></cac:Item>
    <cac:Price><cbc:PriceAmount currencyID=""ILS"">250.00</cbc:PriceAmount></cac:Price>
  </cac:InvoiceLine>
</Invoice>";

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>A PDF carrying <paramref name="xml"/> as the attachment <paramref name="name"/>.</summary>
    internal static byte[] HybridPdf(byte[] page, string name, string xml)
    {
        using var input = new MemoryStream(page, writable: false);
        using var output = new MemoryStream();
        PdfAttachmentManager.AddAttachment(input, output, name, Utf8(xml), "text/xml", "Alternative");
        return output.ToArray();
    }

    // ================================================================
    //  Parsing
    // ================================================================

    /// <summary>
    /// A CII invoice's header and lines are read; of two tax registrations the VAT one (scheme
    /// VA) is the seller's number, and of two tax totals the one in the invoice currency.
    /// </summary>
    [Fact]
    public void ACiiInvoiceIsRead()
    {
        Assert.True(EmbeddedInvoiceReader.TryParse(Utf8(Cii), "factur-x.xml", out EmbeddedInvoice e));

        Assert.Equal("CII", e.Syntax);
        Assert.Equal("factur-x.xml", e.FileName);
        Assert.Equal("INV-2026-001", e.InvoiceNumber);
        Assert.Equal("380", e.TypeCode);
        Assert.Equal(new DateTime(2026, 7, 15), e.IssueDate);
        Assert.Equal("USD", e.Currency);
        Assert.Equal("Seller GmbH", e.SellerName);
        Assert.Equal("DE123456789", e.SellerTaxId);
        Assert.Equal("Buyer Ltd", e.BuyerName);
        Assert.Equal(1000m, e.TaxBasisTotal);
        Assert.Equal(190m, e.TaxTotal);
        Assert.Equal(1190m, e.GrandTotal);
        Assert.Equal(19m, e.TaxRatePercent);

        var line = Assert.Single(e.Lines);
        Assert.Equal("Consulting", line.Description);
        Assert.Equal("A-1", line.CatalogNumber);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(500m, line.UnitPrice);
        Assert.Equal(1000m, line.Amount);
    }

    /// <summary>A UBL invoice's header and lines are read, Hebrew names included.</summary>
    [Fact]
    public void AUblInvoiceIsRead()
    {
        Assert.True(EmbeddedInvoiceReader.TryParse(Utf8(Ubl), "invoice.xml", out EmbeddedInvoice e));

        Assert.Equal("UBL", e.Syntax);
        Assert.Equal("U-77", e.InvoiceNumber);
        Assert.Equal(new DateTime(2026, 7, 15), e.IssueDate);
        Assert.Equal("ILS", e.Currency);
        Assert.Equal("ספק בע\"מ", e.SellerName);
        Assert.Equal("514286558", e.SellerTaxId);
        Assert.Equal("Buyer", e.BuyerName);
        Assert.Equal(180m, e.TaxTotal);
        Assert.Equal(1180m, e.GrandTotal);
        Assert.Equal(18m, e.TaxRatePercent);
        var line = Assert.Single(e.Lines);
        Assert.Equal("שירות", line.Description);
        Assert.Equal(4m, line.Quantity);
        Assert.Equal(250m, line.UnitPrice);
    }

    /// <summary>
    /// A DTD is refused — it is how an entity is declared, and so how an expansion bomb or an
    /// external entity arrives — and the reason is reported.
    /// </summary>
    [Fact]
    public void AHostileDocumentTypeDeclarationIsRefused()
    {
        const string bomb = @"<?xml version=""1.0""?>
<!DOCTYPE Invoice [<!ENTITY a ""aaaaaaaaaa""><!ENTITY b ""&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;"">]>
<Invoice><ID>&b;</ID></Invoice>";

        Assert.False(EmbeddedInvoiceReader.TryParse(Utf8(bomb), "x.xml", out EmbeddedInvoice e));
        Assert.Null(e);
        Assert.Contains("not readable XML", EmbeddedInvoiceReader.LastError);
    }

    /// <summary>XML that is neither syntax is not an invoice; malformed XML is not either.</summary>
    [Fact]
    public void XmlThatIsNotAnInvoiceIsNotRead()
    {
        Assert.False(EmbeddedInvoiceReader.TryParse(Utf8("<notes><n>hi</n></notes>"), "notes.xml", out _));
        Assert.False(EmbeddedInvoiceReader.TryParse(Utf8("<Invoice><ID>"), "broken.xml", out _));
        Assert.True(EmbeddedInvoiceReader.TryParse(Utf8(Ubl), "ok.xml", out _));
    }

    // ================================================================
    //  In a PDF
    // ================================================================

    /// <summary>A hybrid PDF's attachment is found and read; a PDF without one reads nothing.</summary>
    [Fact]
    public void AHybridPdfsInvoiceIsFound()
    {
        byte[] plain = InvoicePdfs.Simple();
        Assert.False(EmbeddedInvoiceReader.TryRead(new MemoryStream(plain), out _));

        byte[] hybrid = HybridPdf(plain, "factur-x.xml", Cii);
        Assert.True(EmbeddedInvoiceReader.TryRead(new MemoryStream(hybrid), out EmbeddedInvoice e));
        Assert.Equal("INV-2026-001", e.InvoiceNumber);
        Assert.Equal("factur-x.xml", e.FileName);
    }

    /// <summary>
    /// An XML attachment that is not an invoice is passed over without a word; one under an
    /// invoice's name that is not an invoice is reported, and the extractor says the page's values
    /// stand alone.
    /// </summary>
    [Fact]
    public void AnXmlAttachmentThatIsNotAnInvoiceIsPassedOver()
    {
        byte[] pdf = HybridPdf(InvoicePdfs.Simple(), "notes.xml", "<notes/>");

        Assert.False(EmbeddedInvoiceReader.TryRead(new MemoryStream(pdf), out _));
        Assert.Null(EmbeddedInvoiceReader.LastError);

        byte[] named = HybridPdf(InvoicePdfs.Simple(), "factur-x.xml", "<notes/>");
        Assert.False(EmbeddedInvoiceReader.TryRead(new MemoryStream(named), out _));
        Assert.Contains("neither a CII nor a UBL", EmbeddedInvoiceReader.LastError, StringComparison.Ordinal);

        var inv = IsraeliInvoiceExtractor.Extract(new MemoryStream(named));
        Assert.Contains(inv.ExtractionFindings, f => f.Code == InvoiceFindingCodes.EmbeddedInvoiceUnreadable);
    }

    // ================================================================
    //  Hostile attachments
    // ================================================================

    // A PDF of one page whose /EmbeddedFiles hold each (name, dictionary entries, body) given, in
    // name order; the body's characters are its bytes (Latin-1).
    internal static byte[] PdfWithAttachments(bool directRoot, params (string Name, string Entries, string Body)[] files)
    {
        var bodies = new System.Collections.Generic.List<string>();
        var names = new StringBuilder();
        int next = 4;
        foreach (var f in files)
        {
            names.Append('(').Append(f.Name).Append(") ").Append(next).Append(" 0 R ");
            next += 2;
        }
        string catalog = "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [" + names + "] >> >> >>";
        bodies.Add(catalog);
        bodies.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        int obj = 4;
        foreach (var f in files)
        {
            bodies.Add($"<< /Type /Filespec /F ({f.Name}) /UF ({f.Name}) /EF << /F {obj + 1} 0 R >> >>");
            bodies.Add($"<< /Type /EmbeddedFile {f.Entries} /Length {f.Body.Length} >>\nstream\n{f.Body}\nendstream");
            obj += 2;
        }

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Count + 1];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Count; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1)
          .Append(directRoot ? " /Root " + catalog + " >>\n" : " /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    internal const string TinyUbl = "<Invoice><ID>U-9</ID></Invoice>";

    /// <summary>
    /// A corrupt attachment — a Flate stream that does not inflate — is passed over with its
    /// reason, and the next is read: the Try method does not throw. The extractor reads the same
    /// document through.
    /// </summary>
    [Fact]
    public void ACorruptAttachmentIsPassedOverAndTheNextIsRead()
    {
        string corrupt = new string((char)0xFF, 4);
        byte[] pdf = PdfWithAttachments(false,
            ("a.xml", "/Filter /FlateDecode", corrupt),
            ("b.xml", "", TinyUbl));

        Assert.True(EmbeddedInvoiceReader.TryRead(new MemoryStream(pdf), out EmbeddedInvoice e));
        Assert.Equal("U-9", e.InvoiceNumber);
        Assert.Contains("a.xml", EmbeddedInvoiceReader.LastError, StringComparison.Ordinal);

        byte[] alone = PdfWithAttachments(false, ("a.xml", "/Filter /FlateDecode", corrupt));
        Assert.False(EmbeddedInvoiceReader.TryRead(new MemoryStream(alone), out _));
        Assert.Contains("not decoded", EmbeddedInvoiceReader.LastError, StringComparison.Ordinal);
    }

    /// <summary>
    /// An invoice after eight other XML attachments is past the reader's limit and is never tried;
    /// the reader says so, beside why the one it tried under an invoice's name failed, rather than
    /// answer as if the PDF carried none. Among the first eight it is read.
    /// </summary>
    [Fact]
    public void AnAttachmentPastTheCandidateLimitIsReportedAsNotTried()
    {
        var others = Enumerable.Range(2, EmbeddedInvoiceReader.MaxCandidates - 1)
                               .Select(i => ($"a{i}.xml", "", "<notes/>"))
                               .Append(("factur-x.xml", "", "<notes/>"))   // an invoice's name, no invoice
                               .ToList();
        byte[] past = PdfWithAttachments(false, others.Append(("z.xml", "", TinyUbl)).ToArray());

        Assert.False(EmbeddedInvoiceReader.TryRead(new MemoryStream(past), out _));
        Assert.Contains("neither a CII nor a UBL", EmbeddedInvoiceReader.LastError, StringComparison.Ordinal);
        Assert.Contains("1 more .xml attachment(s) not tried", EmbeddedInvoiceReader.LastError, StringComparison.Ordinal);

        byte[] within = PdfWithAttachments(false, others.Skip(1).Append(("z.xml", "", TinyUbl)).ToArray());
        Assert.True(EmbeddedInvoiceReader.TryRead(new MemoryStream(within), out EmbeddedInvoice e));
        Assert.Equal("U-9", e.InvoiceNumber);
    }

    /// <summary>
    /// A malformed document whose trailer names no catalog: the Try method answers false, with
    /// the reason, instead of throwing out of the extraction that called it.
    /// </summary>
    [Fact]
    public void AMalformedCatalogIsAnsweredNotThrown()
    {
        byte[] pdf = Encoding.ASCII.GetBytes(
            "%PDF-1.7\n1 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n" +
            "xref\n0 2\n0000000000 65535 f \n0000000009 00000 n \ntrailer\n<< /Size 2 >>\nstartxref\n59\n%%EOF\n");

        Assert.False(EmbeddedInvoiceReader.TryRead(new MemoryStream(pdf), out EmbeddedInvoice e));
        Assert.Null(e);
        Assert.Contains("could not be read", EmbeddedInvoiceReader.LastError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A catalog written directly in the trailer is read, not refused: its attachment is found.
    /// The attachment listing used to demand a reference and throw out of the Try method.
    /// </summary>
    [Fact]
    public void ACatalogInTheTrailerIsReadNotRefused()
    {
        byte[] pdf = PdfWithAttachments(true, ("b.xml", "", TinyUbl));

        Assert.True(EmbeddedInvoiceReader.TryRead(new MemoryStream(pdf), out EmbeddedInvoice e));
        Assert.Equal("U-9", e.InvoiceNumber);
    }

    /// <summary>
    /// A deflate bomb under an invoice's name, with no declared size: the inflate stops at the
    /// reader's ceiling of 32 MiB instead of Core's 512 MiB, which the allocation shows — 400 MiB
    /// of zeros would otherwise be inflated in full before the size was looked at.
    /// </summary>
    [Fact]
    public void ADeflateBombAttachmentIsDecodedOnlyToTheCeiling()
    {
        byte[] bomb;
        using (var packed = new MemoryStream())
        {
            using (var z = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            {
                var zeros = new byte[1 << 20];
                for (int i = 0; i < 400; i++) z.Write(zeros, 0, zeros.Length);
            }
            bomb = packed.ToArray();
        }
        byte[] pdf = PdfWithAttachments(false, ("factur-x.xml", "/Filter /FlateDecode", Encoding.Latin1.GetString(bomb)));

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.False(EmbeddedInvoiceReader.TryRead(new MemoryStream(pdf), out _));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Contains("not decoded", EmbeddedInvoiceReader.LastError, StringComparison.Ordinal);
        Assert.InRange(allocated, 0, 200L * 1024 * 1024);
    }

    /// <summary>
    /// XML nested thirty thousand deep under a value the reader takes: refused by its depth before
    /// the tree is built — reading the value walked the subtree recursively, and a million levels
    /// overflowed the stack. Nesting an invoice's shape can have is read, the control.
    /// </summary>
    [Fact]
    public void AdversarialDeeplyNestedXmlIsRefusedByDepth()
    {
        int levels = 30000;
        string deep = "<Invoice><ID>" + string.Concat(Enumerable.Repeat("<a>", levels)) + "x"
                      + string.Concat(Enumerable.Repeat("</a>", levels)) + "</ID></Invoice>";

        Assert.False(EmbeddedInvoiceReader.TryParse(Utf8(deep), "deep.xml", out _));
        Assert.Contains("nested deeper", EmbeddedInvoiceReader.LastError, StringComparison.Ordinal);

        string shallow = "<Invoice><ID>U-9</ID><Note>" + string.Concat(Enumerable.Repeat("<a>", 50))
                         + string.Concat(Enumerable.Repeat("</a>", 50)) + "</Note></Invoice>";
        Assert.True(EmbeddedInvoiceReader.TryParse(Utf8(shallow), "ok.xml", out EmbeddedInvoice e));
        Assert.Equal("U-9", e.InvoiceNumber);
    }

    /// <summary>
    /// A value is its element's own text: an element nested inside it is not appended, as reading
    /// the whole subtree did.
    /// </summary>
    [Fact]
    public void AValueIsItsElementsOwnText()
    {
        Assert.True(EmbeddedInvoiceReader.TryParse(Utf8("<Invoice><ID>U-9<x>junk</x></ID></Invoice>"), "x.xml", out EmbeddedInvoice e));
        Assert.Equal("U-9", e.InvoiceNumber);
    }

    // ================================================================
    //  Taken over the page
    // ================================================================

    private const string IsraeliUbl = @"<Invoice>
  <ID>U-80</ID><IssueDate>2026-07-15</IssueDate><DocumentCurrencyCode>ILS</DocumentCurrencyCode>
  <AccountingSupplierParty><Party><PartyTaxScheme><CompanyID>IL514286558</CompanyID></PartyTaxScheme></Party></AccountingSupplierParty>
  <AccountingCustomerParty><Party><PartyTaxScheme><CompanyID>IL300056785</CompanyID></PartyTaxScheme></Party></AccountingCustomerParty>
  <TaxTotal><TaxAmount currencyID=""ILS"">1800.00</TaxAmount></TaxTotal>
  <LegalMonetaryTotal><TaxExclusiveAmount>10000.00</TaxExclusiveAmount><TaxInclusiveAmount>11800.00</TaxInclusiveAmount></LegalMonetaryTotal>
</Invoice>";

    /// <summary>
    /// An Israeli VAT number in the XML carries the country prefix EN 16931 requires; it is taken
    /// as its digits, so the customer is still a registered dealer and the allocation rule still
    /// applies to the page that carries no number.
    /// </summary>
    [Fact]
    public void AnIsraeliPrefixedVatNumberIsReadAsItsDigits()
    {
        byte[] pdf = HybridPdf(InvoicePdfs.Simple(), "invoice.xml", IsraeliUbl);

        var inv = IsraeliInvoiceExtractor.Extract(new MemoryStream(pdf));

        Assert.Equal("514286558", inv.SupplierId);
        Assert.Equal("300056785", inv.CustomerId);
        Assert.Contains(inv.Findings, f => f.Code == InvoiceFindingCodes.AllocationNumberMissing);
    }

    /// <summary>
    /// A credit note states its amounts positive; a credit invoice's page prints them negative.
    /// They agree — no mismatch is reported — and the page's sign is kept.
    /// </summary>
    [Fact]
    public void ACreditNotesPositiveAmountsAgreeWithANegativePage()
    {
        byte[] page = new InvoicePdfBuilder()
            .Rtl("חשבונית מס זיכוי", 500, 760)
            .Rtl("סה\"כ לפני מע\"מ", 500, 640).AsciiRun("10,000.00-", 300, 640, step: 4)
            .Rtl("מע\"מ", 500, 620).AsciiRun("1,800.00-", 300, 620, step: 4)
            .Rtl("סה\"כ לתשלום", 500, 600).AsciiRun("11,800.00-", 300, 600, step: 4)
            .Build();
        string creditNote = IsraeliUbl.Replace("<Invoice>", "<CreditNote>").Replace("</Invoice>", "</CreditNote>");

        var inv = IsraeliInvoiceExtractor.Extract(new MemoryStream(HybridPdf(page, "invoice.xml", creditNote)));

        Assert.Equal(IsraeliDocumentType.CreditInvoice, inv.DocumentType);
        Assert.Equal(-11800m, inv.Total);
        Assert.Equal(-1800m, inv.Vat);
        Assert.DoesNotContain(inv.ExtractionFindings, f => f.Code == InvoiceFindingCodes.EmbeddedInvoiceMismatch);
    }

    /// <summary>
    /// The extractor takes the embedded invoice's values over the page's, says so, and reports
    /// each total the two disagree on.
    /// </summary>
    [Fact]
    public void TheExtractorPrefersTheEmbeddedInvoiceAndReportsDisagreement()
    {
        byte[] pdf = HybridPdf(InvoicePdfs.Simple(), "invoice.xml", Ubl);   // the page says 11,800; the XML 1,180

        var inv = IsraeliInvoiceExtractor.Extract(new MemoryStream(pdf));

        Assert.NotNull(inv.EmbeddedInvoice);
        Assert.Equal("U-77", inv.InvoiceNumber);
        Assert.Equal(1180m, inv.Total);
        Assert.Equal(1, inv.LineItems.Count);
        Assert.Contains(inv.ExtractionFindings, f => f.Code == InvoiceFindingCodes.EmbeddedStructuredInvoice);
        Assert.Equal(3, inv.ExtractionFindings.Count(f => f.Code == InvoiceFindingCodes.EmbeddedInvoiceMismatch));

        var off = IsraeliInvoiceExtractor.Extract(new MemoryStream(pdf), new IsraeliInvoiceOptions { ReadEmbeddedInvoice = false });
        Assert.Null(off.EmbeddedInvoice);
        Assert.Equal(11800m, off.Total);
    }
}

/// <summary>
/// The embedded-invoice reader under a <c>PdfLimits</c> value set for the test, in the collection
/// that serialises every test writing one.
/// </summary>
[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public class EmbeddedInvoiceReaderLimitTests
{
    /// <summary>
    /// The attachment name tree cut short by its entry limit: the invoice past the cut is never
    /// listed, and the reader says the listing was partial, rather than answer as if the PDF
    /// carried none. Under the default limit the same PDF's invoice is read.
    /// </summary>
    [Fact]
    public void AListingCutShortByTheNameTreeLimitIsReported()
    {
        byte[] pdf = EmbeddedInvoiceReaderTests.PdfWithAttachments(false,
            ("a.xml", "", "<notes/>"), ("b.xml", "", EmbeddedInvoiceReaderTests.TinyUbl));
        try
        {
            Tetitulz.Pdf.Core.Constants.PdfLimits.MaxNameTreeEntries = 1;
            Assert.False(EmbeddedInvoiceReader.TryRead(new MemoryStream(pdf), out _));
            Assert.Contains("listed only in part", EmbeddedInvoiceReader.LastError, StringComparison.Ordinal);
        }
        finally
        {
            Tetitulz.Pdf.Core.Constants.PdfLimits.ResetToDefaults();
        }

        Assert.True(EmbeddedInvoiceReader.TryRead(new MemoryStream(pdf), out EmbeddedInvoice e));
        Assert.Equal("U-9", e.InvoiceNumber);
    }
}
