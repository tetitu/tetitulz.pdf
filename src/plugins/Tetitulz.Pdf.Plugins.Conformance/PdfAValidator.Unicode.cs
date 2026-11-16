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
        /// A77 — level A of PDF/A-1, levels A and U of PDF/A-2 and -3: every font used for rendering
        /// maps its character codes to Unicode (ISO 19005-1 6.3.8, 19005-2/3 6.2.11.7.2; rules
        /// 6.3.8-1, 6.2.11.7.2-1) — <see cref="FontUnicodeMapping.Maps"/>, the answer PDF/UA's UA26
        /// reads too. Used is <see cref="PdfUaFontUse.PaintedFonts(PdfDocument)"/>'s answer, every named font when it
        /// has none.
        /// </summary>
        private static void CheckFontUnicodeMapping(PdfDocument doc, PdfAConformancePart part, string level, List<PdfAViolation> v)
        {
            bool applies = part == PdfAConformancePart.Part1
                ? level == "A"
                : (part == PdfAConformancePart.Part2 || part == PdfAConformancePart.Part3) && (level == "A" || level == "U");
            if (!applies || doc?.Pages == null) return;

            HashSet<PdfDictionary> painted = PdfUaFontUse.PaintedFonts(doc,
                PdfXValidator.DefaultMaxPageContentBytes, PdfXValidator.DefaultMaxDocumentContentBytes);
            var seen = new HashSet<PdfDictionary>();
            var seenResources = new HashSet<PdfDictionary>();
            var unmapped = new Occurrences();
            long documentBytes = 0;
            for (int p = 0; p < doc.Pages.Count; p++)
            {
                var scan = PdfXValidator.ScanPage(doc, doc.Pages[p],
                    PdfXValidator.DefaultMaxPageContentBytes, ref documentBytes,
                    PdfXValidator.DefaultMaxDocumentContentBytes, collectContent: false);
                if (scan.Incomplete != null)
                    ReportPartial($"Page {p + 1}: the A77 Unicode-mapping check did not run in full — {scan.Incomplete}.", "A77");
                foreach (var resources in scan.Resources)
                {
                    if (!ScanReuse.FirstVisit(seenResources, resources)) continue;
                    if (!(doc.ResolveObject(resources[PdfName.Font]) is PdfDictionary fonts)) continue;
                    foreach (var kvp in fonts)
                    {
                        if (!(doc.ResolveObject(kvp.Value) is PdfDictionary font) || !seen.Add(font)) continue;
                        if (painted != null && !painted.Contains(font)) continue;
                        if (!FontUnicodeMapping.Maps(doc, font))
                            unmapped.Add((doc.ResolveObject(font[PdfName.BaseFont]) as PdfName)?.Value
                                         ?? (doc.ResolveObject(font[PdfName.Name]) as PdfName)?.Value
                                         ?? "/" + kvp.Key.Value);
                    }
                }
            }

            if (unmapped.Count > 0)
                v.Add(new PdfAViolation("A77",
                    $"A font used for rendering does not map its character codes to Unicode: {unmapped} (" +
                    (part == PdfAConformancePart.Part1 ? "ISO 19005-1 6.3.8; rule 6.3.8-1"
                        : $"ISO 19005-{((int)part).ToString(CultureInfo.InvariantCulture)} 6.2.11.7.2; rule 6.2.11.7.2-1") + ")."));
        }
    }
}
