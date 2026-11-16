// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// The dated Israeli tax figures an invoice is checked against: the VAT rate, and the amount
    /// above which a tax invoice needs the Tax Authority's allocation number.
    /// </summary>
    /// <remarks>
    /// <para><b>Every table is dated, and an invoice is judged by its own date.</b> The rate
    /// changed eight times since 2005 and the allocation threshold four times since 2024; a check
    /// against today's figures fails every invoice issued before the last change.</para>
    /// <para><b>VAT</b> (standard rate): 16.5% from 2005-09-01, 15.5% from 2006-07-01, 16.5% from
    /// 2009-07-01, 16% from 2010-01-01, 17% from 2012-09-01, 18% from 2013-06-02, 17% from
    /// 2015-10-01, 18% from 2025-01-01. No rate is answered before 2005-09-01. The rate follows the
    /// transaction's tax-liability date, which an invoice may follow by up to fourteen days — so an
    /// invoice dated just after a change may carry the rate before it.</para>
    /// <para><b>Allocation numbers</b> (חשבוניות ישראל): required for a tax invoice whose amount
    /// <b>before VAT exceeds</b> 25,000 from 2024-05-05, 20,000 from 2025-01-01, 10,000 from
    /// 2026-01-01, and 5,000 from 2026-06-01 — the accelerated schedule that replaced the 2024
    /// plan of 15,000 / 10,000 / 5,000 in 2026–2028. An invoice mixing taxable with exempt or
    /// zero-rated items is measured by its <b>VAT</b> instead: 4,250, 3,600, 1,800 and 900 on the
    /// same dates. Only when the invoice carries VAT and the customer is a registered dealer who
    /// asked for one; see <see cref="IsraeliInvoiceValidator"/>.</para>
    /// <para><b>The tables are defaults, not law.</b> A rate or a threshold the law changes after
    /// this release is a caller's setting, not a code change:
    /// <see cref="IsraeliInvoiceValidationOptions.VatRates"/>,
    /// <see cref="IsraeliInvoiceValidationOptions.AllocationThresholds"/> and
    /// <see cref="IsraeliInvoiceValidationOptions.AllocationVatThresholds"/> replace them — a table
    /// loaded from configuration, say, the defaults with a new entry appended. Each method here takes
    /// a table, and reads the default without one.</para>
    /// </remarks>
    public static class IsraeliTaxRules
    {
        /// <summary>
        /// The customer number an invoice carries for a customer who does not deduct input VAT
        /// (<c>999999998</c>): no allocation number is requested for it.
        /// </summary>
        public const string NonDeductingCustomerNumber = "999999998";

        /// <summary>
        /// The standard VAT rates, in percent, by the date each came into force: 18% since
        /// 2025-01-01, and every rate before it back to 2005, which invoices of their dates carry.
        /// </summary>
        public static IReadOnlyList<IsraeliDatedFigure> DefaultVatRates { get; } = new[]
        {
            new IsraeliDatedFigure(new DateTime(2005, 9, 1), 16.5m),
            new IsraeliDatedFigure(new DateTime(2006, 7, 1), 15.5m),
            new IsraeliDatedFigure(new DateTime(2009, 7, 1), 16.5m),
            new IsraeliDatedFigure(new DateTime(2010, 1, 1), 16m),
            new IsraeliDatedFigure(new DateTime(2012, 9, 1), 17m),
            new IsraeliDatedFigure(new DateTime(2013, 6, 2), 18m),
            new IsraeliDatedFigure(new DateTime(2015, 10, 1), 17m),
            new IsraeliDatedFigure(new DateTime(2025, 1, 1), 18m),
        };

        /// <summary>
        /// The pre-VAT amounts, in shekels, above which a tax invoice needs an allocation number, by
        /// the date each came into force.
        /// </summary>
        public static IReadOnlyList<IsraeliDatedFigure> DefaultAllocationThresholds { get; } = new[]
        {
            new IsraeliDatedFigure(new DateTime(2024, 5, 5), 25000m),
            new IsraeliDatedFigure(new DateTime(2025, 1, 1), 20000m),
            new IsraeliDatedFigure(new DateTime(2026, 1, 1), 10000m),
            new IsraeliDatedFigure(new DateTime(2026, 6, 1), 5000m),
        };

        /// <summary>
        /// The VAT amounts, in shekels, above which a tax invoice mixing taxable with exempt or
        /// zero-rated items needs an allocation number, by the date each came into force.
        /// </summary>
        public static IReadOnlyList<IsraeliDatedFigure> DefaultAllocationVatThresholds { get; } = new[]
        {
            new IsraeliDatedFigure(new DateTime(2024, 5, 5), 4250m),
            new IsraeliDatedFigure(new DateTime(2025, 1, 1), 3600m),
            new IsraeliDatedFigure(new DateTime(2026, 1, 1), 1800m),
            new IsraeliDatedFigure(new DateTime(2026, 6, 1), 900m),
        };

        /// <summary>
        /// How long after a rate change an invoice dated after it may still carry the old rate:
        /// the rate follows the transaction, and an invoice for December's work is often dated in
        /// January.
        /// </summary>
        internal static readonly TimeSpan RateChangeGrace = TimeSpan.FromDays(31);

        /// <summary>The standard VAT rate in percent on <paramref name="date"/>, or <c>null</c> before 2005-09-01.</summary>
        public static decimal? VatRateOn(DateTime date) => VatRateOn(date, null);

        /// <summary>
        /// The VAT rate <paramref name="rates"/> (<see cref="DefaultVatRates"/> when <c>null</c> or
        /// empty) holds on <paramref name="date"/>, or <c>null</c> before its first entry.
        /// </summary>
        public static decimal? VatRateOn(DateTime date, IReadOnlyList<IsraeliDatedFigure> rates)
            => Lookup(OrDefault(rates, DefaultVatRates), date.Date);

        /// <summary>
        /// Every standard rate the table has held, each once, in the order they came into force:
        /// the rates a label naming its rate (<c>סה"כ סכומים ב-17% מע"מ</c>) may print.
        /// </summary>
        internal static IEnumerable<decimal> StandardVatRates(IReadOnlyList<IsraeliDatedFigure> rates)
            => Ordered(OrDefault(rates, DefaultVatRates)).Select(v => v.Value).Distinct();

        /// <summary>
        /// The pre-VAT amount, in shekels, a tax invoice dated <paramref name="date"/> must exceed
        /// to need an allocation number, or <c>null</c> before 2024-05-05 (no requirement).
        /// </summary>
        public static decimal? AllocationThresholdOn(DateTime date) => AllocationThresholdOn(date, null);

        /// <summary>
        /// The pre-VAT threshold <paramref name="thresholds"/> (<see cref="DefaultAllocationThresholds"/>
        /// when <c>null</c> or empty) holds on <paramref name="date"/>, or <c>null</c> before its first entry.
        /// </summary>
        public static decimal? AllocationThresholdOn(DateTime date, IReadOnlyList<IsraeliDatedFigure> thresholds)
            => Lookup(OrDefault(thresholds, DefaultAllocationThresholds), date.Date);

        /// <summary>
        /// The VAT, in shekels, a tax invoice mixing taxable with exempt or zero-rated items and
        /// dated <paramref name="date"/> must exceed to need an allocation number, or <c>null</c>
        /// before 2024-05-05.
        /// </summary>
        public static decimal? AllocationVatThresholdOn(DateTime date) => AllocationVatThresholdOn(date, null);

        /// <summary>
        /// The VAT threshold <paramref name="thresholds"/> (<see cref="DefaultAllocationVatThresholds"/>
        /// when <c>null</c> or empty) holds on <paramref name="date"/>, or <c>null</c> before its first entry.
        /// </summary>
        public static decimal? AllocationVatThresholdOn(DateTime date, IReadOnlyList<IsraeliDatedFigure> thresholds)
            => Lookup(OrDefault(thresholds, DefaultAllocationVatThresholds), date.Date);

        /// <summary>
        /// Whether a document of <paramref name="type"/> is one the allocation rule applies to: a
        /// tax invoice (305), a consolidated one (310), a tax invoice and receipt (320), or an
        /// agent's (345). Not a credit invoice, which the service gives no number, and not a
        /// proforma, whose advance approval is not an allocation number.
        /// </summary>
        public static bool NeedsAllocationNumber(IsraeliDocumentType type)
            => type == IsraeliDocumentType.TaxInvoice
               || type == IsraeliDocumentType.PeriodicTaxInvoice
               || type == IsraeliDocumentType.TaxInvoiceReceipt
               || type == IsraeliDocumentType.AgentInvoice;

        /// <summary>
        /// The rates an invoice dated <paramref name="date"/> may plausibly carry: the rate in force,
        /// and within <see cref="RateChangeGrace"/> after a change also the one before it.
        /// </summary>
        internal static IReadOnlyList<decimal> PlausibleVatRates(DateTime date, IReadOnlyList<IsraeliDatedFigure> table = null)
        {
            var rates = new List<decimal>();
            decimal? now = VatRateOn(date, table);
            if (now == null) return rates;
            rates.Add(now.Value);
            decimal? before = VatRateOn(date.Date - RateChangeGrace, table);
            if (before != null && before.Value != now.Value) rates.Add(before.Value);
            return rates;
        }

        private static IReadOnlyList<IsraeliDatedFigure> OrDefault(IReadOnlyList<IsraeliDatedFigure> table,
                                                                    IReadOnlyList<IsraeliDatedFigure> defaults)
            => table == null || table.Count == 0 ? defaults : table;

        // A caller's table in the order its entries came into force; null entries passed over.
        private static IEnumerable<IsraeliDatedFigure> Ordered(IReadOnlyList<IsraeliDatedFigure> table)
            => table.Where(f => f != null).OrderBy(f => f.From);

        private static decimal? Lookup(IReadOnlyList<IsraeliDatedFigure> table, DateTime date)
        {
            decimal? value = null;
            foreach (var figure in Ordered(table))
            {
                if (date < figure.From) break;
                value = figure.Value;
            }
            return value;
        }
    }

    /// <summary>
    /// A tax figure and the date it came into force — a VAT rate in percent, an allocation
    /// threshold in shekels — one entry of a table <see cref="IsraeliTaxRules"/> reads.
    /// </summary>
    public sealed class IsraeliDatedFigure
    {
        /// <summary>A figure in force from <paramref name="from"/> (its date; the time is dropped).</summary>
        public IsraeliDatedFigure(DateTime from, decimal value)
        {
            From = from.Date;
            Value = value;
        }

        /// <summary>The first day the figure is in force.</summary>
        public DateTime From { get; }

        /// <summary>The figure: a rate in percent, or an amount in shekels.</summary>
        public decimal Value { get; }
    }
}
