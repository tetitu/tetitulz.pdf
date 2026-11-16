// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>
        /// The graphics state rules. A56 — no <c>/TR</c> at all, and <c>/TR2</c> only as
        /// <c>/Default</c> (ISO 19005-1 6.2.8, 19005-2/3/4 6.2.5; rules 6.2.8-1/-2, 6.2.5-1/-2);
        /// stricter than <see cref="TransferFunctionEntries"/>, which PDF/X and PDF 2.0 read and which
        /// lets an identity function pass. A57 — PDF/A-2 onwards: no <c>/HTP</c> (<c>/HTO</c> in
        /// PDF/A-4), every halftone of type 1 or 5 and without a <c>/HalftoneName</c>, and in PDF/A-2
        /// and -3 a <c>/TransferFunction</c> only where ISO 32000-1 requires one — on a type 5
        /// halftone's component for a colorant other than the primaries, never on another
        /// (6.2.5-3 to -6).
        /// </summary>
        private static void CheckGraphicsStates(PdfDocument doc, PdfAConformancePart part, List<PdfAViolation> v)
        {
            if (doc?.Pages == null) return;
            bool part1 = part == PdfAConformancePart.Part1;
            bool part4 = part == PdfAConformancePart.Part4;
            var transfer = new Occurrences();
            var halftones = new Occurrences();
            var seenStates = new HashSet<PdfDictionary>();
            var seenResources = new HashSet<PdfDictionary>();
            var seenHalftones = new Halftones.Seen();
            long documentBytes = 0;

            for (int p = 0; p < doc.Pages.Count; p++)
            {
                var scan = PdfXValidator.ScanPage(doc, doc.Pages[p],
                    PdfXValidator.DefaultMaxPageContentBytes, ref documentBytes,
                    PdfXValidator.DefaultMaxDocumentContentBytes, collectContent: false);
                if (scan.Incomplete != null)
                    ReportPartial($"Page {p + 1}: the A56-A57 graphics-state checks did not run in full — {scan.Incomplete}.", "A56", "A57");
                foreach (var resources in scan.Resources)
                {
                    if (!ScanReuse.FirstVisit(seenResources, resources)) continue;
                    if (!(doc.ResolveObject(resources[PdfName.ExtGState]) is PdfDictionary states)) continue;
                    foreach (var kvp in states)
                    {
                        if (!(doc.ResolveObject(kvp.Value) is PdfDictionary gs) || !seenStates.Add(gs)) continue;
                        string where = "/" + kvp.Key.Value;
                        if (gs[PdfName.TR] != null) transfer.Add(where + " /TR");
                        if (gs[PdfName.TR2] != null && !((doc.ResolveObject(gs[PdfName.TR2]) as PdfName)?.Value == "Default"))
                            transfer.Add(where + " /TR2");
                        if (part1) continue;

                        var phase = part4 ? PdfName.HTO : PdfName.HTP;
                        if (gs[phase] != null) halftones.Add(where + " /" + phase.Value);
                        Halftones.Visit(doc, gs[PdfName.HT], where + " /HT", seenHalftones,
                            (halftone, at, colorant) => CheckHalftone(doc, halftone, at, colorant, part4, halftones));
                    }
                }
            }

            string iso = part1 ? "ISO 19005-1 6.2.8" : $"ISO 19005-{(int)part} 6.2.5";
            if (transfer.Count > 0)
                v.Add(new PdfAViolation("A56",
                    $"A graphics state sets a transfer function: {transfer} ({iso}; rules " +
                    (part1 ? "6.2.8-1/-2" : "6.2.5-1/-2") + ")."));
            if (halftones.Count > 0)
                v.Add(new PdfAViolation("A57",
                    $"A graphics state's halftone is not one the part admits: {halftones} ({iso}; rules 6.2.5-3 to -6)."));
        }

        /// <summary>One halftone <see cref="Halftones.Visit"/> reached: its type, its name, its transfer function.</summary>
        private static void CheckHalftone(PdfDocument doc, PdfDictionary halftone, string where, string colorant, bool part4,
            Occurrences problems)
        {
            int? type = Halftones.Type(doc, halftone);
            if (type != 1 && type != 5)
                problems.Add($"{where} /HalftoneType {(type?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing")}");
            if (halftone[PdfName.HalftoneName] != null) problems.Add(where + " /HalftoneName");

            if (!part4)
            {
                bool hasTransfer = halftone[PdfName.TransferFunction] != null;
                bool? required = Halftones.TransferFunctionRequired(colorant);
                if (required == false && hasTransfer) problems.Add(where + " /TransferFunction (a primary colorant's)");
                else if (required == true && !hasTransfer) problems.Add(where + " no /TransferFunction (a non-primary colorant's)");
            }
        }
    }
}
