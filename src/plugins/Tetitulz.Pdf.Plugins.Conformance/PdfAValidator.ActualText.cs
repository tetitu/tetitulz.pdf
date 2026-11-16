// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>
        /// A78 — PDF/A-4: no <c>/ActualText</c> holds a Private Use code point (ISO 19005-4 6.2.10.8,
        /// rule 6.2.10.8-1) — U+E000 to U+F8FF and planes 15 and 16 — in a structure element
        /// (through <see cref="StructureTreeWalk"/>) or a marked-content property list the pages draw,
        /// inline or named from <c>/Properties</c>.
        /// </summary>
        private static void CheckActualText(PdfDocument doc, PdfAConformancePart part, List<PdfAViolation> v)
        {
            if (part != PdfAConformancePart.Part4 || doc?.Catalog?.Dictionary == null) return;
            var problems = new Occurrences();

            if (doc.ResolveObject(doc.Catalog.Dictionary[PdfName.StructTreeRoot]) is PdfDictionary root)
            {
                var walk = new StructureTreeWalk(doc)
                {
                    Enter = node =>
                    {
                        if (HasPrivateUse(doc.ResolveObject(node.Dictionary?[PdfName.ActualText]) as PdfString, out int cp))
                            problems.Add($"the /{node.Role ?? "StructElem"} element" +
                                         (node.ObjectNumber > 0 ? " (object " + node.ObjectNumber.ToString(CultureInfo.InvariantCulture) + ")" : "") +
                                         $" holds U+{cp:X4}");
                    },
                };
                walk.Run(root);
                if (walk.FirstCeiling != null)
                    ReportPartial($"The A78 ActualText check did not walk the whole structure tree ({walk.FirstCeiling}).", "A78");
            }

            var seenStreams = new HashSet<PdfStream>();
            long documentBytes = 0;
            for (int p = 0; doc.Pages != null && p < doc.Pages.Count; p++)
            {
                string page = "page " + (p + 1).ToString(CultureInfo.InvariantCulture);
                var scan = PdfXValidator.ScanPage(doc, doc.Pages[p],
                    PdfXValidator.DefaultMaxPageContentBytes, ref documentBytes,
                    PdfXValidator.DefaultMaxDocumentContentBytes);
                if (scan.Incomplete != null)
                    ReportPartial($"Page {p + 1}: the A78 ActualText check did not run in full — {scan.Incomplete}.", "A78");
                bool skip = false;
                PdfDictionary resources = null;
                WalkDrawnOperations(doc, scan,
                    (i, initialState) =>
                    {
                        var source = scan.ContentStreamSources[i];
                        skip = source != null && !seenStreams.Add(source);
                        resources = scan.ContentStreamResources[i];
                        return true;
                    },
                    (i, op) =>
                    {
                        if (skip || op.Operator != PdfOperators.BeginMarkedContentProps || op.Operands.Length < 2) return true;
                        var properties = op.Operands[1] as PdfDictionary;
                        if (properties == null && op.Operands[1] is PdfName named
                            && doc.ResolveObject(resources?[PdfName.Properties]) is PdfDictionary byName)
                            properties = doc.ResolveObject(byName[named]) as PdfDictionary;
                        if (HasPrivateUse(doc.ResolveObject(properties?[PdfName.ActualText]) as PdfString, out int cp))
                            problems.Add($"{page}: a marked-content sequence holds U+{cp:X4}");
                        return true;
                    });
            }

            if (problems.Count > 0)
                v.Add(new PdfAViolation("A78",
                    $"An /ActualText holds a Private Use code point: {problems} (ISO 19005-4 6.2.10.8; rule 6.2.10.8-1)."));
        }

        /// <summary>Whether a text string holds a Private Use code point (the BMP's area, or planes 15 and 16), and the first.</summary>
        private static bool HasPrivateUse(PdfString text, out int codePoint)
        {
            codePoint = 0;
            string s = text?.GetText();
            if (s == null) return false;
            for (int i = 0; i < s.Length; i++)
            {
                int cp = s[i];
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    cp = char.ConvertToUtf32(s[i], s[++i]);
                if ((cp >= 0xE000 && cp <= 0xF8FF) || (cp >= 0xF0000 && cp <= 0xFFFFD) || (cp >= 0x100000 && cp <= 0x10FFFD))
                {
                    codePoint = cp;
                    return true;
                }
            }
            return false;
        }
    }
}
