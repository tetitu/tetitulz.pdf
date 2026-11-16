// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>
        /// A34 — an XMP property in no schema the XMP Specification or ISO 19005 predefines and no
        /// extension schema the packet describes; A35 — a property whose value is not of the type its
        /// schema declares (ISO 19005-1 6.7.9, rules 6.7.9-2/-3; ISO 19005-2/3 6.6.2.3.1,
        /// rules 6.6.2.3.1-1/-2); A36 — an extension schema description that is not well formed. PDF/A-4
        /// dropped the rules. The catalog's packet is read.
        /// </summary>
        private static void CheckXmpProperties(PdfAIdentification id, List<PdfAViolation> v, PdfAConformancePart part)
        {
            if (id?.Packet == null || part == PdfAConformancePart.Part4) return;
            bool part1 = part == PdfAConformancePart.Part1;
            var findings = XmpPropertyRules.Check(id.Packet, XmpPredefinedSchemas.ForPart(part));
            string edition = part1 ? "January 2004" : "June 2005";
            string clause = part1 ? "ISO 19005-1 6.7.9" : $"ISO 19005-{(int)part} 6.6.2.3.1";

            if (findings.Undefined.Count > 0)
                v.Add(new PdfAViolation("A34",
                    $"XMP property {XmpPropertyRules.Describe(findings.Undefined)} is in no schema the XMP " +
                    $"Specification ({edition}) or ISO 19005 predefines, and in no extension schema the packet " +
                    $"describes ({clause}; rule {(part1 ? "6.7.9-2" : "6.6.2.3.1-1")})."));
            if (findings.Mistyped.Count > 0)
                v.Add(new PdfAViolation("A35",
                    $"XMP property {XmpPropertyRules.Describe(findings.Mistyped)} does not have the value type " +
                    $"its schema declares ({clause}; rule {(part1 ? "6.7.9-3" : "6.6.2.3.1-2")})."));
            if (findings.ExtensionProblems.Count > 0)
                v.Add(new PdfAViolation("A36",
                    $"The XMP packet's extension schema description is not well formed: " +
                    $"{XmpPropertyRules.Describe(findings.ExtensionProblems)} (" +
                    (part1 ? "ISO 19005-1 6.7.8; rules 6.7.8-1 to -19"
                           : $"ISO 19005-{(int)part} 6.6.2.3.2 and 6.6.2.3.3; rules 6.6.2.3.2-1, 6.6.2.3.3-1 to -18") + ")."));
            if (findings.Truncated)
                ReportPartial($"An XMP property's value nests deeper than {XmpPropertyRules.MaxValueDepth} levels; " +
                              "it was not judged.", "A34", "A35", "A36");
        }
    }
}
