// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Forms;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="IsraeliTaxRules"/>' dated figures and <see cref="IsraeliInvoiceValidator"/>'s
/// findings — each finding beside the control that does not raise it.
/// </summary>
public class IsraeliInvoiceValidatorTests
{
    // Nine-digit numbers with a valid check digit (and one without).
    private const string Supplier = "514286558";
    private const string Customer = "300056785";
    private const string BadCheckDigit = "514286557";

    /// <summary>A consistent tax invoice of 10,000 + 18% dated 2026-07-15, with an allocation number.</summary>
    private static IsraeliInvoice Sound() => new IsraeliInvoice
    {
        DocumentType = IsraeliDocumentType.TaxInvoice,
        InvoiceNumber = "20457",
        Date = new DateTime(2026, 7, 15),
        SupplierId = Supplier,
        CustomerId = Customer,
        AllocationNumber = "123456789",
        Subtotal = 10000m,
        VatRate = 18m,
        Vat = 1800m,
        Total = 11800m,
        IsDigitallySigned = true,
    };

    private static List<string> Codes(IsraeliInvoice inv) => IsraeliInvoiceValidator.Validate(inv).Select(f => f.Code).ToList();

    private static List<string> Codes(IsraeliInvoice inv, IsraeliInvoiceValidationOptions options)
        => IsraeliInvoiceValidator.Validate(inv, options).Select(f => f.Code).ToList();

    // ================================================================
    //  Tables a caller sets: a figure the law changes after this release
    // ================================================================

    // The default table with a rate the law might set later: 19% from 2027-01-01 — listed out of
    // order, as a configuration file might.
    private static List<IsraeliDatedFigure> WithANewRate()
        => new[] { new IsraeliDatedFigure(new DateTime(2027, 1, 1), 19m) }.Concat(IsraeliTaxRules.DefaultVatRates).ToList();

    /// <summary>
    /// A VAT rate the law sets after this release is the caller's table, not a code change: the
    /// default table answers 18% on a date after the new rate, the caller's the new rate, and both
    /// the same before it — whatever order the caller lists the entries in.
    /// </summary>
    [Fact]
    public void ANewVatRateIsACallersTable()
    {
        var after = new DateTime(2027, 3, 1);

        Assert.Equal(18m, IsraeliTaxRules.VatRateOn(after));
        Assert.Equal(19m, IsraeliTaxRules.VatRateOn(after, WithANewRate()));
        Assert.Equal(18m, IsraeliTaxRules.VatRateOn(new DateTime(2026, 3, 1), WithANewRate()));
        Assert.Equal(18m, IsraeliTaxRules.VatRateOn(after, new List<IsraeliDatedFigure>()));
    }

    /// <summary>
    /// An invoice carrying the new rate is reported against the default table and accepted
    /// against one that holds it.
    /// </summary>
    [Fact]
    public void AnInvoiceAtANewRateIsJudgedByTheCallersTable()
    {
        var inv = Sound();
        inv.Date = new DateTime(2027, 3, 1);
        inv.VatRate = 19m;
        inv.Vat = 1900m;
        inv.Total = 11900m;

        var byDefault = Codes(inv);
        Assert.Contains(InvoiceFindingCodes.PrintedVatRateMismatch, byDefault);
        Assert.Contains(InvoiceFindingCodes.VatMismatch, byDefault);

        var byTable = Codes(inv, new IsraeliInvoiceValidationOptions { VatRates = WithANewRate() });
        Assert.DoesNotContain(InvoiceFindingCodes.PrintedVatRateMismatch, byTable);
        Assert.DoesNotContain(InvoiceFindingCodes.VatMismatch, byTable);
    }

    /// <summary>
    /// The allocation threshold is the caller's table too: an invoice of 10,000 with no allocation
    /// number needs one under the default table's 5,000 and not under a threshold of 20,000.
    /// </summary>
    [Fact]
    public void AnAllocationThresholdIsACallersTable()
    {
        var inv = Sound();
        inv.AllocationNumber = null;
        var higher = new[] { new IsraeliDatedFigure(new DateTime(2024, 5, 5), 20000m) };

        Assert.Contains(InvoiceFindingCodes.AllocationNumberMissing, Codes(inv));
        Assert.DoesNotContain(InvoiceFindingCodes.AllocationNumberMissing,
                              Codes(inv, new IsraeliInvoiceValidationOptions { AllocationThresholds = higher }));
    }

    // ================================================================
    //  The dated figures
    // ================================================================

    [Theory]
    [InlineData(2005, 8, 31, null)]
    [InlineData(2005, 9, 1, "16.5")]
    [InlineData(2006, 6, 30, "16.5")]
    [InlineData(2006, 7, 1, "15.5")]
    [InlineData(2009, 6, 30, "15.5")]
    [InlineData(2009, 7, 1, "16.5")]
    [InlineData(2009, 12, 31, "16.5")]
    [InlineData(2010, 1, 1, "16")]
    [InlineData(2012, 8, 31, "16")]
    [InlineData(2012, 9, 1, "17")]
    [InlineData(2013, 6, 1, "17")]
    [InlineData(2013, 6, 2, "18")]
    [InlineData(2015, 9, 30, "18")]
    [InlineData(2015, 10, 1, "17")]
    [InlineData(2024, 12, 31, "17")]
    [InlineData(2025, 1, 1, "18")]
    public void TheVatRateIsTheOneInForceOnTheDate(int y, int m, int d, string expected)
    {
        decimal? rate = expected == null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(rate, IsraeliTaxRules.VatRateOn(new DateTime(y, m, d)));
    }

    [Theory]
    [InlineData(2024, 5, 4, null)]
    [InlineData(2024, 5, 5, 25000)]
    [InlineData(2024, 12, 31, 25000)]
    [InlineData(2025, 1, 1, 20000)]
    [InlineData(2025, 12, 31, 20000)]
    [InlineData(2026, 1, 1, 10000)]
    [InlineData(2026, 5, 31, 10000)]
    [InlineData(2026, 6, 1, 5000)]
    public void TheAllocationThresholdIsTheOneInForceOnTheDate(int y, int m, int d, int? expected)
    {
        Assert.Equal(expected, (int?)IsraeliTaxRules.AllocationThresholdOn(new DateTime(y, m, d)));
    }

    /// <summary>
    /// For a month after a change an invoice may still carry the old rate — the rate follows the
    /// transaction — and after that it may not.
    /// </summary>
    [Fact]
    public void TheOldRateIsPlausibleForAMonthAfterAChange()
    {
        Assert.Equal(new[] { 18m, 17m }, IsraeliTaxRules.PlausibleVatRates(new DateTime(2025, 1, 15)));
        Assert.Equal(new[] { 18m }, IsraeliTaxRules.PlausibleVatRates(new DateTime(2025, 2, 15)));
    }

    /// <summary>
    /// Every rate the table has held, once each, in the order they came into force: the rates a
    /// label naming its rate is built from.
    /// </summary>
    [Fact]
    public void EveryStandardRateIsListedOnce()
    {
        Assert.Equal(new[] { 16.5m, 15.5m, 16m, 17m, 18m }, IsraeliTaxRules.StandardVatRates(null));
    }

    // ================================================================
    //  A sound invoice
    // ================================================================

    /// <summary>A consistent, signed tax invoice raises nothing — the control for every test below.</summary>
    [Fact]
    public void ASoundInvoiceRaisesNothing()
    {
        Assert.Empty(Codes(Sound()));
    }

    // ================================================================
    //  Arithmetic
    // ================================================================

    /// <summary>
    /// Subtotal and VAT that do not make the total are an error; within the tolerance they are
    /// not.
    /// </summary>
    [Theory]
    [InlineData(11800.05, false)]
    [InlineData(11800.06, true)]
    [InlineData(11900, true)]
    public void ATotalThatIsNotSubtotalPlusVatIsAnError(double total, bool raised)
    {
        var inv = Sound();
        inv.Total = (decimal)total;

        var found = IsraeliInvoiceValidator.Validate(inv);

        Assert.Equal(raised, found.Any(f => f.Code == InvoiceFindingCodes.TotalMismatch && f.Severity == InvoiceFindingSeverity.Error));
    }

    /// <summary>
    /// VAT that is not the rate of the subtotal is reported — a warning, since an invoice mixing
    /// exempt items carries less — and the old rate is accepted in the month after a change.
    /// </summary>
    [Fact]
    public void VatThatIsNotTheRateOfTheSubtotalIsReported()
    {
        var inv = Sound();
        inv.Vat = 1700m;
        inv.Total = 11700m;
        inv.VatRate = null;
        Assert.Contains(InvoiceFindingCodes.VatMismatch, Codes(inv));

        inv.Date = new DateTime(2025, 1, 10);
        Assert.DoesNotContain(InvoiceFindingCodes.VatMismatch, Codes(inv));

        inv.Date = new DateTime(2025, 3, 10);
        Assert.Contains(InvoiceFindingCodes.VatMismatch, Codes(inv));
    }

    /// <summary>A printed rate that is not the rate on the date is reported; the right one is not.</summary>
    [Fact]
    public void APrintedRateThatIsNotTheStatutoryRateIsReported()
    {
        var inv = Sound();
        inv.VatRate = 17m;
        Assert.Contains(InvoiceFindingCodes.PrintedVatRateMismatch, Codes(inv));
    }

    /// <summary>No VAT at all is noted, and is not checked against the rate.</summary>
    [Fact]
    public void ZeroVatIsNotedAndNotCheckedAgainstTheRate()
    {
        var inv = Sound();
        inv.Vat = 0m;
        inv.Total = 10000m;
        inv.VatRate = null;
        var codes = Codes(inv);
        Assert.Contains(InvoiceFindingCodes.ZeroVat, codes);
        Assert.DoesNotContain(InvoiceFindingCodes.VatMismatch, codes);
    }

    /// <summary>Line items that do not make the subtotal are reported; ones that do, are not.</summary>
    [Fact]
    public void LineItemsThatDoNotMakeTheSubtotalAreReported()
    {
        var inv = Sound();
        inv.LineItems = new List<InvoiceLineItem>
        {
            new InvoiceLineItem { Quantity = 2, UnitPrice = 3000m },   // amount from quantity x price
            new InvoiceLineItem { Amount = 4000m },
        };
        Assert.DoesNotContain(InvoiceFindingCodes.LineItemsMismatch, Codes(inv));

        inv.LineItems[1].Amount = 3000m;
        Assert.Contains(InvoiceFindingCodes.LineItemsMismatch, Codes(inv));
    }

    /// <summary>
    /// Line items whose amounts hold their VAT — read under "סה"כ כולל מע"מ" — add up to the total,
    /// not the subtotal: no mismatch. The same amounts not marked so are measured against the
    /// subtotal and reported, the control.
    /// </summary>
    [Fact]
    public void LineItemsHoldingTheirVatMakeTheTotal()
    {
        IsraeliInvoice WithItems(bool includesVat)
        {
            var inv = Sound();
            inv.LineItems = new List<InvoiceLineItem>
            {
                new InvoiceLineItem { Amount = inv.Total - 1000m, AmountIncludesVat = includesVat },
                new InvoiceLineItem { Amount = 1000m, AmountIncludesVat = includesVat },
            };
            return inv;
        }

        Assert.DoesNotContain(InvoiceFindingCodes.LineItemsMismatch, Codes(WithItems(true)));
        Assert.Contains(InvoiceFindingCodes.LineItemsMismatch, Codes(WithItems(false)));
    }

    // ================================================================
    //  The allocation number
    // ================================================================

    /// <summary>
    /// Over the threshold on its date, with VAT, to a registered dealer, a tax invoice without an
    /// allocation number is warned about.
    /// </summary>
    [Fact]
    public void AnInvoiceThatNeedsAnAllocationNumberAndHasNoneIsWarnedAbout()
    {
        var inv = Sound();
        inv.AllocationNumber = null;

        var found = IsraeliInvoiceValidator.Validate(inv);

        var f = Assert.Single(found, x => x.Code == InvoiceFindingCodes.AllocationNumberMissing);
        Assert.Equal(InvoiceFindingSeverity.Warning, f.Severity);
    }

    /// <summary>Each condition of the rule, taken away alone, takes the warning away.</summary>
    [Theory]
    [InlineData("threshold not exceeded")]
    [InlineData("before the regime")]
    [InlineData("no VAT")]
    [InlineData("no customer number")]
    [InlineData("customer deducts no VAT")]
    [InlineData("a receipt")]
    [InlineData("a credit invoice")]
    [InlineData("a proforma")]
    [InlineData("VAT not read, total equals subtotal")]
    public void EachConditionOfTheAllocationRuleIsNeeded(string removed)
    {
        var inv = Sound();
        inv.AllocationNumber = null;
        switch (removed)
        {
            // 10,000 does not EXCEED the 10,000 threshold of May 2026.
            case "threshold not exceeded": inv.Date = new DateTime(2026, 5, 15); break;
            case "before the regime": inv.Date = new DateTime(2024, 4, 1); inv.Subtotal = 100000m; inv.Vat = 17000m; inv.Total = 117000m; inv.VatRate = 17m; break;
            case "no VAT": inv.Vat = 0m; inv.Total = 10000m; inv.VatRate = null; break;
            case "no customer number": inv.CustomerId = null; break;
            case "customer deducts no VAT": inv.CustomerId = IsraeliTaxRules.NonDeductingCustomerNumber; break;
            case "a receipt": inv.DocumentType = IsraeliDocumentType.Receipt; break;
            case "a credit invoice": inv.DocumentType = IsraeliDocumentType.CreditInvoice; break;
            case "a proforma": inv.DocumentType = IsraeliDocumentType.Proforma; break;
            // "VAT: exempt" reads no amount; the total equal to the subtotal says the VAT is zero.
            case "VAT not read, total equals subtotal": inv.Vat = null; inv.Total = inv.Subtotal; inv.VatRate = null; break;
        }

        Assert.DoesNotContain(InvoiceFindingCodes.AllocationNumberMissing, Codes(inv));
    }

    /// <summary>
    /// An agent's tax invoice (345) is one the rule applies to; the tax invoice is the control.
    /// </summary>
    [Theory]
    [InlineData(IsraeliDocumentType.AgentInvoice)]
    [InlineData(IsraeliDocumentType.TaxInvoice)]
    [InlineData(IsraeliDocumentType.TaxInvoiceReceipt)]
    [InlineData(IsraeliDocumentType.PeriodicTaxInvoice)]
    public void EveryTaxInvoiceTypeNeedsAnAllocationNumber(IsraeliDocumentType type)
    {
        var inv = Sound();
        inv.AllocationNumber = null;
        inv.DocumentType = type;

        Assert.Contains(InvoiceFindingCodes.AllocationNumberMissing, Codes(inv));
    }

    /// <summary>
    /// An invoice that says its input VAT may not be deducted carries no number by design: it is
    /// noted, not warned about. Without the statement the same invoice is warned about.
    /// </summary>
    [Fact]
    public void AnInvoiceStatingItsInputVatIsNotDeductibleIsNotWarnedAbout()
    {
        var inv = Sound();
        inv.AllocationNumber = null;
        inv.StatesInputVatNotDeductible = true;

        var codes = Codes(inv);
        Assert.Contains(InvoiceFindingCodes.InputVatNotDeductible, codes);
        Assert.DoesNotContain(InvoiceFindingCodes.AllocationNumberMissing, codes);

        inv.StatesInputVatNotDeductible = false;
        Assert.Contains(InvoiceFindingCodes.AllocationNumberMissing, Codes(inv));
    }

    /// <summary>
    /// The threshold is in shekels. A dollar invoice of 4,000 before VAT, below 5,000, is not
    /// judged without a rate — it is noted — and with a rate of 3.65 it is 14,600 shekels and
    /// warned about. In shekels the same 4,000 raises neither, the control.
    /// </summary>
    [Fact]
    public void TheAllocationThresholdIsJudgedInShekels()
    {
        IsraeliInvoice Dollars(string currency) => new IsraeliInvoice
        {
            DocumentType = IsraeliDocumentType.TaxInvoice, InvoiceNumber = "1", Date = new DateTime(2026, 7, 15),
            SupplierId = Supplier, CustomerId = Customer, Currency = currency,
            Subtotal = 4000m, VatRate = 18m, Vat = 720m, Total = 4720m, IsDigitallySigned = true,
        };

        var unjudged = Codes(Dollars("USD"));
        Assert.Contains(InvoiceFindingCodes.AllocationThresholdNotJudged, unjudged);
        Assert.DoesNotContain(InvoiceFindingCodes.AllocationNumberMissing, unjudged);

        var rated = IsraeliInvoiceValidator.Validate(Dollars("USD"),
            new IsraeliInvoiceValidationOptions { ShekelRate = (c, d) => c == "USD" ? 3.65m : null });
        Assert.Contains(rated, f => f.Code == InvoiceFindingCodes.AllocationNumberMissing && f.Message.Contains("14,600.00"));

        var shekels = Codes(Dollars("ILS"));
        Assert.DoesNotContain(InvoiceFindingCodes.AllocationThresholdNotJudged, shekels);
        Assert.DoesNotContain(InvoiceFindingCodes.AllocationNumberMissing, shekels);
    }

    /// <summary>
    /// An invoice mixing taxable and exempt items is measured by its VAT. 10,000 before VAT of
    /// which 6,000 is exempt carries 720 of VAT, under the 900 of July 2026: no number needed —
    /// though 10,000 exceeds the 5,000 an unmixed invoice is measured against, which the control
    /// without the exempt part shows. With 1,080 of VAT it is needed.
    /// </summary>
    [Fact]
    public void AMixedInvoiceIsMeasuredByItsVat()
    {
        IsraeliInvoice Mixed(decimal? exempt, decimal vat) => new IsraeliInvoice
        {
            DocumentType = IsraeliDocumentType.TaxInvoice, InvoiceNumber = "1", Date = new DateTime(2026, 7, 15),
            SupplierId = Supplier, CustomerId = Customer, Subtotal = 10000m, ExemptAmount = exempt,
            Vat = vat, Total = 10000m + vat, IsDigitallySigned = true,
        };

        Assert.DoesNotContain(InvoiceFindingCodes.AllocationNumberMissing, Codes(Mixed(6000m, 720m)));
        Assert.Contains(InvoiceFindingCodes.AllocationNumberMissing, Codes(Mixed(null, 720m)));
        Assert.Contains(InvoiceFindingCodes.AllocationNumberMissing, Codes(Mixed(4000m, 1080m)));

        // The subtotal here is the whole: its VAT is the rate of the subtotal less the exempt part.
        Assert.DoesNotContain(InvoiceFindingCodes.VatMismatch, Codes(Mixed(6000m, 720m)));
        Assert.Contains(InvoiceFindingCodes.VatMismatch, Codes(Mixed(null, 720m)));
    }

    /// <summary>
    /// A subtotal that is the taxable part alone: subtotal, exempt part and VAT make the total, the
    /// VAT is the rate of the subtotal, and the line items make subtotal and exempt part together.
    /// Without the exempt part the same numbers do not add up, the control.
    /// </summary>
    [Fact]
    public void AnExemptPartMakesUpTheTotalBesideATaxableSubtotal()
    {
        IsraeliInvoice Mixed(decimal? exempt) => new IsraeliInvoice
        {
            DocumentType = IsraeliDocumentType.TaxInvoice, InvoiceNumber = "1", Date = new DateTime(2026, 7, 15),
            SupplierId = Supplier, AllocationNumber = "123456789", Subtotal = 1000m, ExemptAmount = exempt,
            Vat = 180m, Total = 1380m, IsDigitallySigned = true,
            LineItems = new List<InvoiceLineItem> { new InvoiceLineItem { Amount = 1000m }, new InvoiceLineItem { Amount = 200m } },
        };

        var codes = Codes(Mixed(200m));
        Assert.DoesNotContain(InvoiceFindingCodes.TotalMismatch, codes);
        Assert.DoesNotContain(InvoiceFindingCodes.VatMismatch, codes);
        Assert.DoesNotContain(InvoiceFindingCodes.LineItemsMismatch, codes);

        var unexplained = Codes(Mixed(null));
        Assert.Contains(InvoiceFindingCodes.TotalMismatch, unexplained);
        Assert.Contains(InvoiceFindingCodes.LineItemsMismatch, unexplained);
    }

    /// <summary>
    /// Amounts too large to add are a hostile file's, not an invoice's: every arithmetic check is
    /// skipped and says so, and nothing throws. The sound invoice raises no such finding.
    /// </summary>
    [Fact]
    public void HugeAmountsSkipTheArithmeticAndSaySo()
    {
        var inv = Sound();
        inv.Subtotal = decimal.MaxValue;
        inv.Vat = decimal.MaxValue;
        inv.Total = 1m;
        inv.LineItems = new List<InvoiceLineItem> { new InvoiceLineItem { Amount = decimal.MaxValue }, new InvoiceLineItem { Amount = decimal.MaxValue } };

        var found = IsraeliInvoiceValidator.Validate(inv);

        Assert.Contains(found, f => f.Code == InvoiceFindingCodes.AmountOutOfRange && f.Message.Contains("totals"));
        Assert.Contains(found, f => f.Code == InvoiceFindingCodes.AmountOutOfRange && f.Message.Contains("line items"));
        Assert.DoesNotContain(InvoiceFindingCodes.AmountOutOfRange, Codes(Sound()));
    }

    /// <summary>
    /// The VAT-amount threshold for an invoice mixing taxable and exempt items: 18% of the
    /// pre-VAT threshold on each date (17% in 2024).
    /// </summary>
    [Theory]
    [InlineData(2024, 5, 4, null)]
    [InlineData(2024, 5, 5, 4250)]
    [InlineData(2025, 1, 1, 3600)]
    [InlineData(2026, 1, 1, 1800)]
    [InlineData(2026, 5, 31, 1800)]
    [InlineData(2026, 6, 1, 900)]
    public void TheMixedInvoiceVatThresholdIsTheOneInForceOnTheDate(int y, int m, int d, int? expected)
    {
        Assert.Equal(expected, (int?)IsraeliTaxRules.AllocationVatThresholdOn(new DateTime(y, m, d)));
    }

    /// <summary>An allocation number that is not nine digits is reported.</summary>
    [Theory]
    [InlineData("12345", true)]
    [InlineData("1234567890", true)]
    [InlineData("12345678A", true)]
    [InlineData("123456789A", true)]     // nine digits and a letter: only "digits only" can raise it
    [InlineData("123-456-789", true)]    // nine digits with separators
    [InlineData(" 123456789 ", false)]   // surrounding space is trimmed, not reported
    [InlineData("123456789", false)]
    public void AnAllocationNumberMustBeNineDigits(string number, bool raised)
    {
        var inv = Sound();
        inv.AllocationNumber = number;
        Assert.Equal(raised, Codes(inv).Contains(InvoiceFindingCodes.AllocationNumberMalformed));
    }

    // ================================================================
    //  Identifiers, type, signature
    // ================================================================

    /// <summary>
    /// A number failing the check digit is an error, the same number on both sides is an error,
    /// and a foreign VAT number is not checked against the Israeli rule at all.
    /// </summary>
    [Fact]
    public void IdentifiersAreCheckedWhenTheyAreIsraeli()
    {
        var bad = Sound();
        bad.SupplierId = BadCheckDigit;
        Assert.Contains(InvoiceFindingCodes.InvalidSupplierId, Codes(bad));

        var same = Sound();
        same.CustomerId = Supplier;
        Assert.Contains(InvoiceFindingCodes.SupplierIsCustomer, Codes(same));

        var foreign = Sound();
        foreign.SupplierId = "DE123456789";
        var codes = Codes(foreign);
        Assert.DoesNotContain(InvoiceFindingCodes.InvalidSupplierId, codes);
        Assert.DoesNotContain(InvoiceFindingCodes.MissingSupplierId, codes);
    }

    /// <summary>
    /// A transaction invoice, a proforma, a receipt and a donation receipt are noted as not tax
    /// invoices; an agent's invoice is one; an unknown title is noted.
    /// </summary>
    [Theory]
    [InlineData(IsraeliDocumentType.TransactionInvoice, true)]
    [InlineData(IsraeliDocumentType.Proforma, true)]
    [InlineData(IsraeliDocumentType.Receipt, true)]
    [InlineData(IsraeliDocumentType.DonationReceipt, true)]
    [InlineData(IsraeliDocumentType.AgentInvoice, false)]
    [InlineData(IsraeliDocumentType.TaxInvoice, false)]
    public void ADocumentThatIsNotATaxInvoiceIsNoted(IsraeliDocumentType type, bool noted)
    {
        var inv = Sound();
        inv.DocumentType = type;
        Assert.Equal(noted, Codes(inv).Contains(InvoiceFindingCodes.NotATaxInvoice));
    }

    [Fact]
    public void AnUnknownTitleIsNoted()
    {
        var inv = Sound();
        inv.DocumentType = IsraeliDocumentType.Unknown;
        Assert.Contains(InvoiceFindingCodes.UnknownDocumentType, Codes(inv));
    }

    /// <summary>
    /// An unsigned invoice is noted; a signature the caller found invalid is an error; one not yet
    /// verified is neither.
    /// </summary>
    [Fact]
    public void TheSignatureVerdictIsTheCallers()
    {
        var unsigned = Sound();
        unsigned.IsDigitallySigned = false;
        Assert.Contains(InvoiceFindingCodes.NotDigitallySigned, Codes(unsigned));

        var invalid = Sound();
        invalid.SignatureValid = false;
        Assert.Contains(InvoiceFindingCodes.SignatureInvalid, Codes(invalid));

        var unverified = Sound();
        unverified.SignatureValid = null;
        Assert.DoesNotContain(InvoiceFindingCodes.SignatureInvalid, Codes(unverified));
    }

    /// <summary>A missing number, date, total and supplier are each reported.</summary>
    [Fact]
    public void MissingValuesAreReported()
    {
        var inv = new IsraeliInvoice { DocumentType = IsraeliDocumentType.TaxInvoice, IsDigitallySigned = true };
        var codes = Codes(inv);
        Assert.Contains(InvoiceFindingCodes.MissingInvoiceNumber, codes);
        Assert.Contains(InvoiceFindingCodes.MissingDate, codes);
        Assert.Contains(InvoiceFindingCodes.MissingTotal, codes);
        Assert.Contains(InvoiceFindingCodes.MissingSupplierId, codes);
    }

    // ================================================================
    //  Findings bookkeeping
    // ================================================================

    /// <summary>
    /// The invoice's findings are its extraction findings followed by the validation's, and
    /// validating again replaces the validation's rather than adding to them.
    /// </summary>
    [Fact]
    public void ValidatingAgainReplacesTheValidationsFindings()
    {
        var inv = Sound();
        inv.Total = 1m;
        var note = new InvoiceFinding(InvoiceFindingSeverity.Warning, InvoiceFindingCodes.NoTextLayer, "page 2");
        inv.ExtractionFindings.Add(note);

        IsraeliInvoiceValidator.Validate(inv);
        IsraeliInvoiceValidator.Validate(inv);

        Assert.Same(note, inv.Findings[0]);
        Assert.Single(inv.Findings, f => f.Code == InvoiceFindingCodes.TotalMismatch);
    }
}
