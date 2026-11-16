// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>
        /// PDF/A-2, -3 and -4 permit transparency, on two conditions. A40 — every <c>/BM</c> names a
        /// blend mode ISO 32000 defines: a graphics state's, and from PDF/A-4 an annotation's too
        /// (ISO 19005-2/3 6.2.10, rule 6.2.10-1; ISO 19005-4 6.2.9, rule 6.2.9-1). A41 — a page
        /// that contains transparency, in a file with no PDF/A output intent, says what colour space it
        /// blends in: a page group's <c>/CS</c>, or from PDF/A-4 a page-level PDF/A output intent
        /// (rules 6.2.10-2, 6.2.9-2).
        /// </summary>
        private static void CheckBlending(PdfDocument doc, PdfAConformancePart part, List<PdfAViolation> v)
        {
            if (part == PdfAConformancePart.Part1 || doc?.Pages == null) return;
            bool part4 = part == PdfAConformancePart.Part4;
            string clause = part4 ? "ISO 19005-4 6.2.9; rule 6.2.9" : $"ISO 19005-{(int)part} 6.2.10; rule 6.2.10";

            var unknown = new Occurrences();
            var seen = new HashSet<PdfDictionary>();
            var seenResources = new HashSet<PdfDictionary>();
            long documentBytes = 0;
            for (int p = 0; p < doc.Pages.Count; p++)
            {
                string page = "page " + (p + 1).ToString(CultureInfo.InvariantCulture);
                var scan = PdfXValidator.ScanPage(doc, doc.Pages[p],
                    PdfXValidator.DefaultMaxPageContentBytes, ref documentBytes,
                    PdfXValidator.DefaultMaxDocumentContentBytes, collectContent: false);
                if (scan.Incomplete != null)
                    ReportPartial($"Page {p + 1}: the A40 blend-mode check did not run in full — {scan.Incomplete}.", "A40");

                foreach (var resources in scan.Resources)
                {
                    if (!ScanReuse.FirstVisit(seenResources, resources)) continue;
                    if (!(doc.ResolveObject(resources[PdfName.ExtGState]) is PdfDictionary states)) continue;
                    foreach (var kvp in states)
                        if (doc.ResolveObject(kvp.Value) is PdfDictionary gs && seen.Add(gs))
                            NoteUndefinedBlendModes(doc, gs[PdfName.BM], page + " /" + kvp.Key.Value, unknown);
                }

                if (part4 && doc.ResolveObject(doc.Pages[p].Dictionary?[PdfName.Annots]) is PdfArray annots
                    && ScanReuse.AdmitAnnotations(annots))
                    for (int a = 0; a < annots.Count; a++)
                        if (doc.ResolveObject(annots[a]) is PdfDictionary annot)
                            NoteUndefinedBlendModes(doc, annot[PdfName.BM], page + " annotation " + a.ToString(CultureInfo.InvariantCulture), unknown);
            }
            if (unknown.Count > 0)
                v.Add(new PdfAViolation("A40",
                    $"A blend mode ISO 32000 does not define: {unknown} ({clause}-1)."));

            if (HasPdfAOutputIntent(doc, doc.Catalog?.Dictionary)) return;
            var transparentPages = new SortedSet<int>();
            foreach (var finding in PdfTransparencyFlattener.FindTransparency(doc))
                if (finding.Kind != TransparencyKind.PageGroup) transparentPages.Add(finding.PageIndex);

            var unblended = new Occurrences();
            var intentsAnswered = new Dictionary<object, bool>(PdfReferenceEquality.Instance);
            foreach (int p in transparentPages)
            {
                var pageDict = doc.Pages[p].Dictionary;
                if (doc.ResolveObject(pageDict?[PdfName.Group]) is PdfDictionary group && group[PdfName.CS] != null) continue;
                if (part4 && HasPdfAOutputIntent(doc, pageDict, intentsAnswered)) continue;
                unblended.Add("page " + (p + 1).ToString(CultureInfo.InvariantCulture));
            }
            if (unblended.Count > 0)
                v.Add(new PdfAViolation("A41",
                    $"A page contains transparency, the file has no PDF/A output intent, and the page names no " +
                    $"blending colour space (a page group's /CS{(part4 ? " or a page-level PDF/A output intent" : "")}): " +
                    $"{unblended} ({clause}-2)."));
        }

        /// <summary>Each name of a <c>/BM</c> value — the name, or each of an array's — that ISO 32000 does not define.</summary>
        private static void NoteUndefinedBlendModes(PdfDocument doc, PdfObject bm, string where, Occurrences unknown)
        {
            var value = doc.ResolveObject(bm);
            if (value is PdfName name)
            {
                if (!PdfBlendModes.IsStandard(name.Value)) unknown.Add(where + " /BM /" + name.Value);
                return;
            }
            if (value is PdfArray modes)
                for (int i = 0; i < modes.Count; i++)
                    if (doc.ResolveObject(modes[i]) is PdfName mode && !PdfBlendModes.IsStandard(mode.Value))
                        unknown.Add(where + " /BM /" + mode.Value);
        }

        /// <summary>
        /// Whether <paramref name="owner"/>'s <c>/OutputIntents</c> — the catalog's, or from PDF/A-4 a
        /// page's — holds a PDF/A output intent with its profile (the profile's <c>gOutputCS</c> is that
        /// profile's colour space).
        /// </summary>
        /// <remarks>
        /// <paramref name="answered"/>, when given, keeps each array's answer by its identity, so pages
        /// that share one array read it once, not once a page.
        /// </remarks>
        private static bool HasPdfAOutputIntent(PdfDocument doc, PdfDictionary owner, Dictionary<object, bool> answered = null)
        {
            if (!(doc.ResolveObject(owner?[PdfName.OutputIntents]) is PdfArray intents)) return false;
            if (answered != null && answered.TryGetValue(intents, out bool known)) return known;
            OutputIntentArraysWalked++;
            bool has = false;
            for (int i = 0; i < intents.Count && !has; i++)
                has = doc.ResolveObject(intents[i]) is PdfDictionary intent
                    && (doc.ResolveObject(intent[PdfName.S]) as PdfName)?.Value == "GTS_PDFA1"
                    && doc.ResolveObject(intent[PdfName.DestOutputProfile]) is PdfStream;
            if (answered != null) answered[intents] = has;
            return has;
        }
    }
}
