// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>
        /// The optional content rules, PDF/A-2 onwards (ISO 19005-2/3 6.9, ISO 19005-4 6.10; the
        /// rules of those numbers). Every configuration dictionary — <c>/OCProperties /D</c> and each of
        /// <c>/Configs</c> — has a <c>/Name</c>, non-empty before PDF/A-4 (6.9-1, 6.10-1), and no two
        /// share one (6.9-2, 6.10-2): A70. A configuration's <c>/Order</c>, when it has one, names every
        /// group <c>/OCProperties /OCGs</c> lists (6.9-3, 6.10-3): A71. Before PDF/A-4 no configuration
        /// has an <c>/AS</c> (6.9-4; PDF/A-4 has no such rule): A72. PDF/A-1 is PDF 1.4, which has no
        /// optional content. The configurations are <see cref="OptionalContentConfigurations"/>'s
        /// reading, which PDF/X's X27 shares.
        /// </summary>
        private static void CheckOptionalContent(PdfDocument doc, PdfAConformancePart part, List<PdfAViolation> v)
        {
            if (part == PdfAConformancePart.Part1) return;
            var ocProperties = OptionalContentConfigurations.Properties(doc);
            if (ocProperties == null) return;

            bool part4 = part == PdfAConformancePart.Part4;
            string clause = part4 ? "ISO 19005-4 6.10" : $"ISO 19005-{(int)part} 6.9";
            string rule = part4 ? "6.10" : "6.9";
            var configurations = OptionalContentConfigurations.Read(doc, ocProperties);
            var groups = OptionalContentConfigurations.Groups(doc, ocProperties);

            var names = new Occurrences();
            var orders = new Occurrences();
            var alternates = new Occurrences();
            int work = OptionalContentConfigurations.MaxOrderWork;
            bool orderPartial = false;
            foreach (var c in configurations)
            {
                if (c.Name == null) names.Add(c.Where + " has no /Name");
                else if (!part4 && c.Name.Length == 0) names.Add(c.Where + " has an empty /Name");

                int? missing = OptionalContentConfigurations.GroupsMissingFromOrder(doc, c.Dictionary, groups, ref work, out bool complete);
                if (!complete)
                    orderPartial = true;
                else if (missing > 0)
                    orders.Add($"{c.Where}'s /Order leaves out {missing.Value.ToString(CultureInfo.InvariantCulture)} of " +
                               $"{groups.Count.ToString(CultureInfo.InvariantCulture)} groups");

                if (!part4 && c.Dictionary[PdfName.AS] != null) alternates.Add(c.Where + " /AS");
            }
            if (orderPartial)
                ReportPartial("The A71 /Order check did not read every configuration's /Order in full.", "A71");
            foreach (string name in OptionalContentConfigurations.DuplicateNames(configurations))
                names.Add($"the /Name ({name}) is not unique");

            if (names.Count > 0)
                v.Add(new PdfAViolation("A70",
                    $"An optional content configuration's /Name: {names} ({clause}; rules {rule}-1/-2)."));
            if (orders.Count > 0)
                v.Add(new PdfAViolation("A71",
                    $"An optional content configuration's /Order does not name every group: {orders} ({clause}; rule {rule}-3)."));
            if (alternates.Count > 0)
                v.Add(new PdfAViolation("A72",
                    $"An optional content configuration has an /AS entry: {alternates} ({clause}; rule 6.9-4)."));
        }
    }
}
