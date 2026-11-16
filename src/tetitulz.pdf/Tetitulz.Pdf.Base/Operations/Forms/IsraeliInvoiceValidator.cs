// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>Tolerances and outside knowledge for <see cref="IsraeliInvoiceValidator.Validate"/>.</summary>
    public sealed class IsraeliInvoiceValidationOptions
    {
        /// <summary>
        /// The largest difference, in the invoice's currency, two amounts that should agree may
        /// show and still agree. Default 0.05 — rounding a total to ten agorot moves it by at most
        /// that. A sum over line items also allows half an agora per line.
        /// </summary>
        public decimal AmountTolerance { get; set; } = 0.05m;

        /// <summary>
        /// The value of one unit of a currency in shekels on a date — <c>(currency, date) =&gt;
        /// rate</c>, <c>null</c> when unknown — for judging the allocation threshold, which is in
        /// shekels, on an invoice in another currency. Without it such an invoice is reported as
        /// not judged.
        /// </summary>
        public Func<string, DateTime, decimal?> ShekelRate { get; set; }

        /// <summary>
        /// The standard VAT rates by the date each came into force, when the law changes after this
        /// release — read for the VAT check and for the labels naming a rate (<c>סה"כ סכומים ב-17%
        /// מע"מ</c>). <c>null</c> or empty: <see cref="IsraeliTaxRules.DefaultVatRates"/> (18% since
        /// 2025). To add a rate, append it to the default table.
        /// </summary>
        public IReadOnlyList<IsraeliDatedFigure> VatRates { get; set; }

        /// <summary>
        /// The allocation number's pre-VAT thresholds by date. <c>null</c> or empty:
        /// <see cref="IsraeliTaxRules.DefaultAllocationThresholds"/>.
        /// </summary>
        public IReadOnlyList<IsraeliDatedFigure> AllocationThresholds { get; set; }

        /// <summary>
        /// The allocation number's VAT thresholds (an invoice mixing taxable with exempt items) by
        /// date. <c>null</c> or empty: <see cref="IsraeliTaxRules.DefaultAllocationVatThresholds"/>.
        /// </summary>
        public IReadOnlyList<IsraeliDatedFigure> AllocationVatThresholds { get; set; }
    }

    /// <summary>
    /// Checks an <see cref="IsraeliInvoice"/> against itself and against the dated Israeli rules
    /// (<see cref="IsraeliTaxRules"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>The arithmetic is the strongest check there is.</b> Extraction is heuristic, but
    /// when the subtotal and the VAT add up to the total, and the VAT is the statutory rate of the
    /// subtotal, all three were almost certainly read right — three independent misreads that
    /// still agree are not a realistic failure. A disagreement is reported with the numbers. An
    /// invoice that prints its exempt part apart may call either the taxable part or the whole
    /// the subtotal, and both readings are accepted.</para>
    /// <para><b>The allocation-number rule is reported as a warning, never an error.</b> A number
    /// is required only when all four conditions hold: the amount before VAT exceeds the threshold
    /// on the invoice's date (the VAT, on an invoice mixing taxable and exempt items), the invoice
    /// carries VAT, the customer is a registered dealer, and the customer asked for one. The last
    /// is not printed anywhere. An invoice that says its input VAT may not be deducted expects no
    /// number.</para>
    /// <para><b>An identifier is checked only when it is Israeli-shaped</b> — eight or nine
    /// digits. A foreign VAT number (<c>DE123456789</c>) is not one, and is not reported.</para>
    /// <para><b>Amounts are read from untrusted files</b>, and a sum of two near
    /// <see cref="decimal.MaxValue"/> overflows: a check whose arithmetic overflows is skipped and
    /// reported as <see cref="InvoiceFindingCodes.AmountOutOfRange"/>.</para>
    /// </remarks>
    public static class IsraeliInvoiceValidator
    {
        /// <summary>
        /// Validates <paramref name="invoice"/>, replaces its <see cref="IsraeliInvoice.Findings"/>
        /// with its extraction findings followed by these, and returns the validation's own.
        /// </summary>
        public static IReadOnlyList<InvoiceFinding> Validate(
            IsraeliInvoice invoice, IsraeliInvoiceValidationOptions options = null)
        {
            if (invoice == null) throw new ArgumentNullException(nameof(invoice));
            options ??= new IsraeliInvoiceValidationOptions();

            var found = new List<InvoiceFinding>();
            CheckPresence(invoice, found);
            CheckDocumentType(invoice, found);
            CheckIdentifiers(invoice, found);
            Guarded("totals", found, () => CheckTotals(invoice, options, found));
            Guarded("VAT", found, () => CheckVat(invoice, options, found));
            Guarded("line items", found, () => CheckLineItems(invoice, options, found));
            Guarded("allocation number", found, () => CheckAllocationNumber(invoice, options, found));
            CheckSignature(invoice, found);

            var all = new List<InvoiceFinding>(invoice.ExtractionFindings ?? new List<InvoiceFinding>());
            all.AddRange(found);
            invoice.Findings = all;
            return found;
        }

        // Runs one arithmetic check; an amount too large for its sums skips it, and says so.
        private static void Guarded(string check, List<InvoiceFinding> found, Action run)
        {
            try { run(); }
            catch (OverflowException)
            {
                Add(found, InvoiceFindingSeverity.Warning, InvoiceFindingCodes.AmountOutOfRange,
                    $"The {check} check was skipped: the amounts are too large for its arithmetic.");
            }
        }

        private static void CheckPresence(IsraeliInvoice inv, List<InvoiceFinding> found)
        {
            if (string.IsNullOrWhiteSpace(inv.InvoiceNumber))
                Add(found, InvoiceFindingSeverity.Warning, InvoiceFindingCodes.MissingInvoiceNumber,
                    "No invoice number was found.");
            if (inv.Date == null)
                Add(found, InvoiceFindingSeverity.Warning, InvoiceFindingCodes.MissingDate,
                    "No date was found.");
            if (inv.Total == null)
                Add(found, InvoiceFindingSeverity.Warning, InvoiceFindingCodes.MissingTotal,
                    "No total was found.");
            if (string.IsNullOrWhiteSpace(inv.SupplierId))
                Add(found, InvoiceFindingSeverity.Warning, InvoiceFindingCodes.MissingSupplierId,
                    "No supplier identification number was found.");
        }

        private static void CheckDocumentType(IsraeliInvoice inv, List<InvoiceFinding> found)
        {
            switch (inv.DocumentType)
            {
                case IsraeliDocumentType.Unknown:
                    Add(found, InvoiceFindingSeverity.Info, InvoiceFindingCodes.UnknownDocumentType,
                        "The document's title names no Israeli document type the extractor recognises.");
                    break;
                case IsraeliDocumentType.TransactionInvoice:
                case IsraeliDocumentType.Proforma:
                case IsraeliDocumentType.Receipt:
                case IsraeliDocumentType.DonationReceipt:
                    Add(found, InvoiceFindingSeverity.Info, InvoiceFindingCodes.NotATaxInvoice,
                        $"A {Describe(inv.DocumentType)} is not a tax invoice: no input VAT is deducted on it.");
                    break;
            }
        }

        private static void CheckIdentifiers(IsraeliInvoice inv, List<InvoiceFinding> found)
        {
            bool supplierShaped = IsIsraeliShaped(inv.SupplierId);
            bool customerShaped = IsIsraeliShaped(inv.CustomerId);

            if (supplierShaped && !IsraeliIdentifiers.IsValid(inv.SupplierId))
                Add(found, InvoiceFindingSeverity.Error, InvoiceFindingCodes.InvalidSupplierId,
                    $"The supplier number {inv.SupplierId} fails the check digit.");
            if (customerShaped && !IsraeliIdentifiers.IsValid(inv.CustomerId))
                Add(found, InvoiceFindingSeverity.Error, InvoiceFindingCodes.InvalidCustomerId,
                    $"The customer number {inv.CustomerId} fails the check digit.");

            if (supplierShaped && customerShaped
                && IsraeliIdentifiers.TryNormalize(inv.SupplierId, out string s)
                && IsraeliIdentifiers.TryNormalize(inv.CustomerId, out string c)
                && s == c)
                Add(found, InvoiceFindingSeverity.Error, InvoiceFindingCodes.SupplierIsCustomer,
                    $"The supplier and the customer both carry the number {inv.SupplierId}.");
        }

        private static void CheckTotals(IsraeliInvoice inv, IsraeliInvoiceValidationOptions o,
                                        List<InvoiceFinding> found)
        {
            if (inv.Subtotal == null || inv.Vat == null || inv.Total == null) return;
            decimal sum = inv.Subtotal.Value + inv.Vat.Value;
            if (Agrees(sum, inv.Total.Value, o.AmountTolerance)) return;

            // The subtotal was the taxable part, and the exempt part makes up the rest.
            if (inv.ExemptAmount != null && Agrees(sum + inv.ExemptAmount.Value, inv.Total.Value, o.AmountTolerance))
                return;

            Add(found, InvoiceFindingSeverity.Error, InvoiceFindingCodes.TotalMismatch,
                $"Subtotal {Money(inv.Subtotal)} + VAT {Money(inv.Vat)} = {Money(sum)}" +
                (inv.ExemptAmount != null ? $" (+ exempt {Money(inv.ExemptAmount)} = {Money(sum + inv.ExemptAmount.Value)})" : "") +
                $", but the total is {Money(inv.Total)}.");
        }

        private static void CheckVat(IsraeliInvoice inv, IsraeliInvoiceValidationOptions o,
                                     List<InvoiceFinding> found)
        {
            decimal? net = PreVatAmount(inv, o.VatRates);
            decimal? vat = EffectiveVat(inv);

            if (vat != null && vat.Value == 0m)
            {
                Add(found, InvoiceFindingSeverity.Info, InvoiceFindingCodes.ZeroVat,
                    "The invoice carries no VAT: zero-rated (an export, Eilat) or exempt.");
                return;
            }

            // An invoice taxed at several rates: its VAT is each part's at its own rate.
            if (inv.RatedSubtotals != null && inv.RatedSubtotals.Count >= 2)
            {
                if (vat == null) return;
                decimal expected;
                try { expected = inv.RatedSubtotals.Sum(p => p.Amount * p.Rate / 100m); }
                catch (OverflowException) { return; }
                if (Agrees(expected, vat.Value, LineTolerance(inv, o) * inv.RatedSubtotals.Count)) return;
                Add(found, InvoiceFindingSeverity.Warning, InvoiceFindingCodes.VatMismatch,
                    $"VAT {Money(vat)} is not the sum of its parts at their rates ({Money(Math.Round(expected, 2))}: " +
                    string.Join(", ", inv.RatedSubtotals.Select(p => $"{Percent(p.Rate)} of {Money(p.Amount)}")) + ").");
                return;
            }

            IReadOnlyList<decimal> statutory = inv.Date != null
                ? IsraeliTaxRules.PlausibleVatRates(inv.Date.Value, o.VatRates)
                : Array.Empty<decimal>();

            if (inv.VatRate != null && statutory.Count > 0 && !statutory.Contains(inv.VatRate.Value))
                Add(found, InvoiceFindingSeverity.Warning, InvoiceFindingCodes.PrintedVatRateMismatch,
                    $"The invoice prints VAT at {Percent(inv.VatRate.Value)}; the rate on " +
                    $"{inv.Date.Value:yyyy-MM-dd} is {Percent(statutory[0])}.");

            if (vat == null || net == null) return;

            IReadOnlyList<decimal> rates = statutory.Count > 0
                ? statutory
                : (inv.VatRate != null ? new[] { inv.VatRate.Value } : Array.Empty<decimal>());
            if (rates.Count == 0)
            {
                Add(found, InvoiceFindingSeverity.Info, InvoiceFindingCodes.VatRateUnknown,
                    "No VAT rate is known for this invoice (no date, or a date before 2005-09-01), " +
                    "so the VAT was not checked against the subtotal.");
                return;
            }

            // The VAT is the rate of the taxable part: the subtotal, or the subtotal less the
            // exempt part when the subtotal is the whole.
            var bases = new List<decimal> { net.Value };
            if (inv.ExemptAmount != null) bases.Add(net.Value - inv.ExemptAmount.Value);

            decimal tolerance = LineTolerance(inv, o);
            if (rates.Any(r => bases.Any(b => Agrees(b * r / 100m, vat.Value, tolerance)))) return;

            // A warning, not an error: an invoice mixing exempt and taxable items legitimately
            // carries less VAT than the rate of its whole subtotal.
            Add(found, InvoiceFindingSeverity.Warning, InvoiceFindingCodes.VatMismatch,
                $"VAT {Money(vat)} is not {Percent(rates[0])} of {Money(net)} " +
                $"({Money(Math.Round(net.Value * rates[0] / 100m, 2))}).");
        }

        private static void CheckLineItems(IsraeliInvoice inv, IsraeliInvoiceValidationOptions o,
                                           List<InvoiceFinding> found)
        {
            if (inv.LineItems == null || inv.LineItems.Count == 0) return;

            decimal sum = 0m;
            foreach (var item in inv.LineItems)
            {
                if (item == null) continue;
                decimal? amount = item.Amount
                    ?? (item.Quantity != null && item.UnitPrice != null ? item.Quantity * item.UnitPrice : null);
                if (amount == null) return;   // a line without an amount: the sum says nothing
                sum += amount.Value;
            }

            decimal? target = inv.Subtotal;
            string what = "the subtotal";
            decimal? vat = EffectiveVat(inv);
            // Amounts that hold their VAT add up to the total.
            bool withVat = inv.LineItems.All(i => i == null || i.AmountIncludesVat || i.Amount == null)
                           && inv.LineItems.Any(i => i != null && i.AmountIncludesVat);
            if (withVat || (target == null && (vat == null || vat.Value == 0m)))
            {
                target = inv.Total;
                what = "the total";
            }
            if (target == null) return;

            decimal tolerance = LineTolerance(inv, o);
            if (Agrees(sum, target.Value, tolerance)) return;
            if (inv.Subtotal != null && inv.ExemptAmount != null
                && Agrees(sum, inv.Subtotal.Value + inv.ExemptAmount.Value, tolerance)) return;

            Add(found, InvoiceFindingSeverity.Warning, InvoiceFindingCodes.LineItemsMismatch,
                $"The {inv.LineItems.Count(i => i != null)} line items add up to {Money(sum)}, but {what} is {Money(target)}.");
        }

        private static void CheckAllocationNumber(IsraeliInvoice inv, IsraeliInvoiceValidationOptions o,
                                                  List<InvoiceFinding> found)
        {
            if (!string.IsNullOrWhiteSpace(inv.AllocationNumber))
            {
                string number = inv.AllocationNumber.Trim();
                if (number.Length != 9 || !number.All(AsciiDigits.Is))
                    Add(found, InvoiceFindingSeverity.Warning, InvoiceFindingCodes.AllocationNumberMalformed,
                        $"The allocation number '{inv.AllocationNumber}' is not nine digits.");
                return;
            }

            if (!IsraeliTaxRules.NeedsAllocationNumber(inv.DocumentType) || inv.Date == null) return;

            decimal? vat = EffectiveVat(inv);
            if (vat != null && vat.Value == 0m) return;

            // A customer must be a registered dealer: an Israeli-shaped number, and not the
            // number that says the customer deducts no VAT.
            if (!IsIsraeliShaped(inv.CustomerId)) return;
            if (IsraeliIdentifiers.TryNormalize(inv.CustomerId, out string customer)
                && customer == IsraeliTaxRules.NonDeductingCustomerNumber) return;

            // An invoice mixing taxable and exempt items is measured by its VAT; any other by its
            // amount before VAT.
            DateTime date = inv.Date.Value;
            bool mixed = inv.ExemptAmount != null && inv.ExemptAmount.Value != 0m;
            decimal? threshold = mixed ? IsraeliTaxRules.AllocationVatThresholdOn(date, o.AllocationVatThresholds)
                                       : IsraeliTaxRules.AllocationThresholdOn(date, o.AllocationThresholds);
            decimal? amount = mixed ? vat : PreVatAmount(inv, o.VatRates);
            if (threshold == null || amount == null) return;
            string measure = mixed ? "The VAT" : "The amount before VAT";

            // The thresholds are in shekels.
            decimal shekels = amount.Value;
            if (inv.Currency != null && !string.Equals(inv.Currency, "ILS", StringComparison.OrdinalIgnoreCase))
            {
                decimal? rate = o.ShekelRate?.Invoke(inv.Currency, date);
                if (rate == null)
                {
                    Add(found, InvoiceFindingSeverity.Info, InvoiceFindingCodes.AllocationThresholdNotJudged,
                        $"{measure}, {Money(amount)} {inv.Currency}, was not compared with the " +
                        $"{Money(threshold)} shekel allocation threshold: no exchange rate was supplied.");
                    return;
                }
                shekels = amount.Value * rate.Value;
            }
            if (shekels <= threshold.Value) return;

            if (inv.StatesInputVatNotDeductible)
            {
                Add(found, InvoiceFindingSeverity.Info, InvoiceFindingCodes.InputVatNotDeductible,
                    "The invoice states that its input VAT may not be deducted, so it carries no " +
                    "allocation number by design.");
                return;
            }

            Add(found, InvoiceFindingSeverity.Warning, InvoiceFindingCodes.AllocationNumberMissing,
                $"{measure}, {Money(shekels)} in shekels, exceeds the {Money(threshold)} threshold on " +
                $"{date:yyyy-MM-dd} and the customer is a registered dealer, but no allocation " +
                "number was found. Without one the customer cannot deduct the input VAT (unless the " +
                "customer did not ask for a number).");
        }

        private static void CheckSignature(IsraeliInvoice inv, List<InvoiceFinding> found)
        {
            if (!inv.IsDigitallySigned)
            {
                Add(found, InvoiceFindingSeverity.Info, InvoiceFindingCodes.NotDigitallySigned,
                    "The document carries no digital signature; a digital invoice is expected to be signed.");
                return;
            }
            if (inv.SignatureValid == false)
                Add(found, InvoiceFindingSeverity.Error, InvoiceFindingCodes.SignatureInvalid,
                    "The document's digital signature did not verify.");
        }

        // ----------------------------------------------------------------

        /// <summary>
        /// The amount before VAT: the subtotal; else the total less the VAT; else the total less
        /// the VAT the rate on the invoice's date implies.
        /// </summary>
        internal static decimal? PreVatAmount(IsraeliInvoice inv, IReadOnlyList<IsraeliDatedFigure> vatRates = null)
        {
            if (inv.Subtotal != null) return inv.Subtotal;
            if (inv.Total != null && inv.Vat != null) return inv.Total - inv.Vat;
            if (inv.Total != null && inv.Date != null)
            {
                decimal? rate = inv.VatRate ?? IsraeliTaxRules.VatRateOn(inv.Date.Value, vatRates);
                if (rate != null) return Math.Round(inv.Total.Value / (1m + rate.Value / 100m), 2);
            }
            return null;
        }

        /// <summary>
        /// The VAT the invoice carries: as read, else the total less the subtotal — the one answer
        /// the VAT, line-item and allocation checks all ask for. A "VAT: exempt" line reads no
        /// amount, and its total equal to its subtotal says zero.
        /// </summary>
        internal static decimal? EffectiveVat(IsraeliInvoice inv)
            => inv.Vat ?? (inv.Subtotal != null && inv.Total != null ? inv.Total - inv.Subtotal : null);

        private static bool Agrees(decimal a, decimal b, decimal tolerance) => Math.Abs(a - b) <= tolerance;

        private static decimal LineTolerance(IsraeliInvoice inv, IsraeliInvoiceValidationOptions o)
            => Math.Max(o.AmountTolerance, 0.005m * Math.Max(1, inv.LineItems?.Count ?? 0));

        private static bool IsIsraeliShaped(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            if (!IsraeliIdentifiers.TryNormalize(id, out _)) return false;
            int digits = id.Count(AsciiDigits.Is);
            return digits >= 8 && digits <= 9;
        }

        private static string Describe(IsraeliDocumentType type) => type switch
        {
            IsraeliDocumentType.TransactionInvoice => "transaction invoice",
            IsraeliDocumentType.Proforma => "proforma",
            IsraeliDocumentType.Receipt => "receipt",
            IsraeliDocumentType.DonationReceipt => "donation receipt",
            _ => type.ToString(),
        };

        private static string Money(decimal? v)
            => v == null ? "?" : v.Value.ToString("#,0.00", CultureInfo.InvariantCulture);

        private static string Percent(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture) + "%";

        private static void Add(List<InvoiceFinding> found, InvoiceFindingSeverity s, string code, string message)
            => found.Add(new InvoiceFinding(s, code, message));
    }
}
