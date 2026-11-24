// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    // Findings no public source settles (GAPS.md §12b): what ISO 15930 says of them is in the parts
    // that are not available, so a check written from recollection could fail a conforming file or
    // pass a broken one. Each is reported on PdfXValidationResult.Informational — the
    // file holds this, and the rule about it was not read — and never affects
    // PdfXValidationResult.IsConforming.
    public static partial class PdfXValidator
    {
        /// <summary>
        /// X26 — a halftone's <c>/TransferFunction</c>, in a graphics state a print element reaches (every
        /// level). It is a transfer curve too, which X14 forbids in the graphics state, but ISO 32000-1
        /// Table 130 <i>requires</i> one on a type 5 halftone's component for a non-primary colorant, so
        /// the brochure's "no transfer curves" cannot say which a PDF/X part forbids. Each is named with
        /// whether ISO 32000-1 requires it there (<see cref="Halftones.TransferFunctionRequired"/>, which
        /// PDF/A's A57 judges by).
        /// </summary>
        private static void CheckHalftoneTransferFunctions(PdfDocument doc, List<PdfXViolation> info,
            int maxPageContentBytes, long maxDocumentContentBytes)
        {
            long documentBytes = 0;
            var seenStates = new HashSet<PdfDictionary>();
            var seenResources = new HashSet<PdfDictionary>();   // a dictionary pages share is read once
            var seenHalftones = new Halftones.Seen();           // and a halftone states share
            var found = new List<string>();
            int total = 0;
            for (int p = 0; p < doc.Pages.Count; p++)
            {
                var scan = ScanPage(doc, doc.Pages[p], maxPageContentBytes,
                    ref documentBytes, maxDocumentContentBytes, collectContent: false, printElementsOnly: true);
                if (scan.Incomplete != null)
                    info.Add(new PdfXViolation("X99",
                        $"Page {p + 1}: the X26 halftone transfer-function report did not run in full — {scan.Incomplete}."));

                foreach (var res in scan.Resources)
                {
                    if (!seenResources.Add(res)) continue;
                    var states = Resolve<PdfDictionary>(doc, res[PdfName.ExtGState]);
                    if (states == null) continue;
                    foreach (var kvp in states)
                    {
                        var gs = Resolve<PdfDictionary>(doc, kvp.Value);
                        if (gs == null || !seenStates.Add(gs)) continue;
                        string page = (p + 1).ToString(CultureInfo.InvariantCulture);
                        Halftones.Visit(doc, gs[PdfName.HT], $"page {page} /ExtGState /{kvp.Key.Value} /HT", seenHalftones, (halftone, where, colorant) =>
                        {
                            if (halftone[PdfName.TransferFunction] == null) return;
                            total++;
                            if (found.Count < MaxNamedFindings)
                                found.Add(where + (Halftones.TransferFunctionRequired(colorant) == true
                                    ? " (one ISO 32000-1 Table 130 requires, for a non-primary colorant)"
                                    : " (one ISO 32000-1 does not require)"));
                        });
                    }
                }
            }

            if (total > 0)
                info.Add(new PdfXViolation("X26",
                    "A halftone carries a /TransferFunction, which PDF/X's rule on transfer curves may or may not " +
                    "forbid — the rule is in ISO 15930, which was not available, so this is not judged: " +
                    string.Join("; ", found) +
                    (total > found.Count ? $" and {(total - found.Count).ToString(CultureInfo.InvariantCulture)} more" : "") + "."));
        }

        /// <summary>How many locations an informational finding names before it counts the rest.</summary>
        private const int MaxNamedFindings = 8;

        /// <summary>
        /// X27 — the optional content configurations (X-4 and X-6), read against the conditions PDF/A's
        /// sibling part sets them, because PDF/X's own are in ISO 15930-7 6.24 and 15930-9 6.15, which
        /// were not available. At X-4: PDF/A-2's 6.9 — a non-empty <c>/Name</c>, unique, no <c>/AS</c> —
        /// but not its <c>/Order</c> rule, which ISO 15930-7:2010's foreword says that edition removed. At
        /// X-6: PDF/A-4's 6.10 — a <c>/Name</c>, unique, an <c>/Order</c> naming every group — since the
        /// X-6 Application Notes (§4.1.6) say PDF/X-6, PDF/A-4 and PDF/UA "share major text portions".
        /// The reading is <see cref="OptionalContentConfigurations"/>', which PDF/A's A70–A72 judge by.
        /// </summary>
        private static void CheckOptionalContentConfigurations(PdfDocument doc, PdfXConformanceLevel level,
            List<PdfXViolation> info)
        {
            var ocProperties = OptionalContentConfigurations.Properties(doc);
            if (ocProperties == null) return;
            bool x6 = level == PdfXConformanceLevel.X6;
            var configurations = OptionalContentConfigurations.Read(doc, ocProperties);
            var groups = OptionalContentConfigurations.Groups(doc, ocProperties);

            var found = new List<string>();
            int total = 0;
            void Found(string what) { total++; if (found.Count < MaxNamedFindings) found.Add(what); }
            int work = OptionalContentConfigurations.MaxOrderWork;
            bool orderPartial = false;
            foreach (var c in configurations)
            {
                if (c.Name == null) Found(c.Where + " has no /Name");
                else if (!x6 && c.Name.Length == 0) Found(c.Where + " has an empty /Name");
                if (!x6 && c.Dictionary[PdfName.AS] != null) Found(c.Where + " has an /AS entry");
                if (x6)
                {
                    int? missing = OptionalContentConfigurations.GroupsMissingFromOrder(doc, c.Dictionary, groups, ref work, out bool complete);
                    if (!complete)
                        orderPartial = true;
                    else if (missing > 0)
                        Found($"{c.Where}'s /Order leaves out {missing.Value.ToString(CultureInfo.InvariantCulture)} of " +
                                  $"{groups.Count.ToString(CultureInfo.InvariantCulture)} groups");
                }
            }
            if (orderPartial)
                info.Add(new PdfXViolation("X99", "The X27 report did not read every configuration's /Order in full."));
            foreach (string name in OptionalContentConfigurations.DuplicateNames(configurations))
                Found($"the /Name ({name}) is not unique");

            if (total > 0)
                info.Add(new PdfXViolation("X27",
                    "An optional content configuration does not meet the conditions " +
                    (x6 ? "PDF/A-4 sets (ISO 19005-4 6.10)" : "PDF/A-2 sets (ISO 19005-2 6.9, without its /Order rule)") +
                    ". PDF/X's own rule (ISO 15930-" + (x6 ? "9 6.15" : "7 6.24") + ") was not available, so this is " +
                    "not judged: " + string.Join("; ", found) +
                    (total > found.Count ? $" and {(total - found.Count).ToString(CultureInfo.InvariantCulture)} more" : "") + "."));
        }
    }
}
