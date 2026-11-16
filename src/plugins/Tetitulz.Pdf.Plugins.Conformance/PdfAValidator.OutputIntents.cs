// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>
        /// The output intent rules (ISO 19005-1 6.2.2, 19005-2/3/4 6.2.3; rules 6.2.2-1, 6.2.3-1
        /// to -4). A47 — a destination profile is an output (<c>prtr</c>) or monitor (<c>mntr</c>)
        /// profile of RGB, CMYK or grey, below version 3 in PDF/A-1 and version 5 after, with a header
        /// that reads at all. PDF/A-2 onwards: A48 — no
        /// <c>/DestOutputProfileRef</c>: in a PDF/X output intent for PDF/A-2 and -3, in any for
        /// PDF/A-4. A49 — PDF/A-4: at most one PDF/A output intent in an <c>/OutputIntents</c> array.
        /// The catalog's array is read, and from PDF/A-4 each page's too. A15 (the intents share one
        /// profile) runs for every part through <see cref="CheckMultipleOutputIntentsShareProfile"/>.
        /// </summary>
        private static void CheckOutputIntents(PdfDocument doc, PdfAConformancePart part, List<PdfAViolation> v)
        {
            bool part1 = part == PdfAConformancePart.Part1;
            bool part4 = part == PdfAConformancePart.Part4;
            string iso = part1 ? "ISO 19005-1 6.2.2" : $"ISO 19005-{(int)part} 6.2.3";
            int versionBelow = part1 ? 3 : 5;

            // Each array once, named where it is first met: pages sharing one array would otherwise
            // read it, and report it, once a page — pages × intents, both counts the file's.
            var arrays = new List<(string Where, PdfArray Intents)>();
            var listed = new HashSet<object>(PdfReferenceEquality.Instance);
            if (doc.ResolveObject(doc.Catalog?.Dictionary?[PdfName.OutputIntents]) is PdfArray catalogIntents
                && listed.Add(catalogIntents))
                arrays.Add(("the catalog", catalogIntents));
            if (part4 && doc.Pages != null)
                for (int p = 0; p < doc.Pages.Count; p++)
                    if (doc.ResolveObject(doc.Pages[p].Dictionary?[PdfName.OutputIntents]) is PdfArray pageIntents
                        && listed.Add(pageIntents))
                        arrays.Add(("page " + (p + 1).ToString(CultureInfo.InvariantCulture), pageIntents));

            var profiles = new Occurrences();
            var references = new Occurrences();
            var multiple = new Occurrences();
            var judged = new HashSet<PdfStream>();
            foreach (var (where, intents) in arrays)
            {
                int pdfaIntents = 0;
                for (int i = 0; i < intents.Count; i++)
                {
                    if (!(doc.ResolveObject(intents[i]) is PdfDictionary intent)) continue;
                    string subtype = (doc.ResolveObject(intent[PdfName.S]) as PdfName)?.Value;
                    if (subtype == "GTS_PDFA1") pdfaIntents++;

                    if (!part1 && intent[PdfName.DestOutputProfileRef] != null && (part4 || subtype == "GTS_PDFX"))
                        references.Add($"{where}, intent {i.ToString(CultureInfo.InvariantCulture)} (/{subtype})");

                    if (doc.ResolveObject(intent[PdfName.DestOutputProfile]) is PdfStream profile && judged.Add(profile))
                    {
                        string problem = OutputProfileProblem(profile, versionBelow);
                        if (problem != null) profiles.Add($"{where}, intent {i.ToString(CultureInfo.InvariantCulture)}: {problem}");
                    }
                }
                if (part4 && pdfaIntents > 1)
                    multiple.Add($"{where} holds {pdfaIntents.ToString(CultureInfo.InvariantCulture)}");
            }

            if (profiles.Count > 0)
                v.Add(new PdfAViolation("A47",
                    $"An output intent's destination profile is not an output or monitor profile of RGB, CMYK or " +
                    $"grey below version {versionBelow.ToString(CultureInfo.InvariantCulture)}: {profiles} ({iso}; rule " +
                    (part1 ? "6.2.2-1" : "6.2.3-1") + ")."));
            if (references.Count > 0)
                v.Add(new PdfAViolation("A48",
                    $"An output intent has /DestOutputProfileRef: {references} ({iso}; rule 6.2.3-3)."));
            if (multiple.Count > 0)
                v.Add(new PdfAViolation("A49",
                    $"An /OutputIntents array holds more than one PDF/A output intent: {multiple} ({iso}; rule 6.2.3-4)."));
        }

        /// <summary>What is wrong with an output intent's profile header, or <see langword="null"/>.</summary>
        private static string OutputProfileProblem(PdfStream profile, int versionBelow)
        {
            byte[] bytes;
            try
            {
                bytes = profile.GetDecodedData();
            }
            catch (System.Exception ex)
            {
                return "its stream does not decode (" + ex.GetType().Name + ")";
            }
            if (!IccProfileComponents.TryGetHeaderFields(bytes, out string deviceClass, out string colorSpace, out int majorVersion))
                return "its header is not an ICC header";
            if (deviceClass != "prtr" && deviceClass != "mntr") return $"device class '{deviceClass}'";
            if (colorSpace != "RGB " && colorSpace != "CMYK" && colorSpace != "GRAY") return $"colour space '{colorSpace}'";
            if (majorVersion >= versionBelow) return $"version {majorVersion.ToString(CultureInfo.InvariantCulture)}";
            return null;
        }
    }
}
