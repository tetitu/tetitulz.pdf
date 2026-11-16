// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>Objects the A55 comparisons may visit across one document before the rest are left undecided.</summary>
        private const int MaxEquivalenceNodes = 100_000;

        /// <summary>Decoded bytes one tint-transform stream may take when compared.</summary>
        private const long MaxEquivalenceStreamBytes = 16L * 1024 * 1024;

        /// <summary>Decoded bytes the A55 comparisons may compare across one document, every pair of streams charged.</summary>
        private const long MaxEquivalenceBytes = 256L * 1024 * 1024;

        /// <summary>
        /// PDF/A-2 to -4's spot colour rules (ISO 19005-2/3/4 6.2.4.4; rules 6.2.4.4-1/-2). A54 —
        /// every spot colorant a DeviceN space names (any but the process colorants and <c>None</c>) has
        /// an entry in its attributes' <c>/Colorants</c>. A55 — every Separation of one name, the
        /// <c>/Colorants</c> entries included, has the same tint transform and alternate space, compared
        /// as PDF objects (§6.2.4.4: not by what they compute; compression and indirection ignored).
        /// The colour spaces every resource dictionary a page reaches names, an image's and a
        /// shading's, and each one's bases.
        /// </summary>
        private static void CheckSpotColours(PdfDocument doc, PdfAConformancePart part, List<PdfAViolation> v)
        {
            if (part == PdfAConformancePart.Part1 || doc?.Pages == null) return;
            var separations = new Dictionary<string, (PdfObject Tint, PdfObject Alternate)>(StringComparer.Ordinal);
            var missing = new Occurrences();
            var inconsistent = new Occurrences();
            var seenSpaces = new HashSet<PdfObject>(PdfReferenceEquality.Instance);
            var seenResources = new HashSet<PdfDictionary>();
            long documentBytes = 0;

            // One node budget and one byte budget across the document, and a verdict remembered per
            // pair of objects compared: a file naming the first Separation's tint once and another
            // ten thousand times compares the pair once, not ten thousand times at a stream's full
            // decoded length each. What the budgets leave undecided is said, never guessed.
            int nodeBudget = MaxEquivalenceNodes;
            long byteBudget = MaxEquivalenceBytes;
            bool undecided = false;
            var verdicts = new Dictionary<PdfObject, Dictionary<PdfObject, PdfObjectEquivalence.Result>>(PdfReferenceEquality.Instance);
            PdfObjectEquivalence.Result Compare(PdfObject a, PdfObject b)
            {
                a = doc.ResolveObject(a) ?? PdfNull.Instance;
                b = doc.ResolveObject(b) ?? PdfNull.Instance;
                if (!verdicts.TryGetValue(a, out var row))
                    verdicts[a] = row = new Dictionary<PdfObject, PdfObjectEquivalence.Result>(PdfReferenceEquality.Instance);
                if (row.TryGetValue(b, out var known)) return known;
                var verdict = PdfObjectEquivalence.Compare(doc, a, b, ref nodeBudget, MaxEquivalenceStreamBytes, ref byteBudget);
                row[b] = verdict;
                return verdict;
            }

            void Visit(PdfObject space, int depth)
            {
                if (depth > PdfLimits.MaxResourceDepth) return;
                var resolved = doc.ResolveObject(space);
                if (!(resolved is PdfArray array) || array.Count < 2 || !seenSpaces.Add(array)) return;
                string family = (doc.ResolveObject(array[0]) as PdfName)?.Value;
                switch (family)
                {
                    case PdfColorSpaceNames.Separation:
                        if (array.Count >= 4 && doc.ResolveObject(array[1]) is PdfName name && name.Value != "All" && name.Value != "None")
                        {
                            if (separations.TryGetValue(name.Value, out var first))
                            {
                                var tint = Compare(first.Tint, array[3]);
                                var alternate = tint == PdfObjectEquivalence.Result.Different ? tint : Compare(first.Alternate, array[2]);
                                if (tint == PdfObjectEquivalence.Result.Different || alternate == PdfObjectEquivalence.Result.Different)
                                    inconsistent.Add("/" + name.Value);
                                else if (tint == PdfObjectEquivalence.Result.Undecided || alternate == PdfObjectEquivalence.Result.Undecided)
                                    undecided = true;
                            }
                            else
                            {
                                separations.Add(name.Value, (array[3], array[2]));
                            }
                        }
                        if (array.Count >= 3) Visit(array[2], depth + 1);
                        break;
                    case PdfColorSpaceNames.DeviceN:
                        if (array.Count < 4) break;
                        var colorants = doc.ResolveObject(doc.ResolveObject(array.Count > 4 ? array[4] : null) is PdfDictionary attributes
                            ? attributes[PdfName.Colorants] : null) as PdfDictionary;
                        if (doc.ResolveObject(array[1]) is PdfArray names)
                            for (int i = 0; i < names.Count; i++)
                                if (doc.ResolveObject(names[i]) is PdfName colorant && !IsProcessColorant(colorant.Value)
                                    && colorants?[colorant] == null)
                                    missing.Add("/" + colorant.Value);
                        if (colorants != null)
                            foreach (var kvp in colorants) Visit(kvp.Value, depth + 1);
                        Visit(array[2], depth + 1);
                        break;
                    case PdfColorSpaceNames.Indexed:
                    case PdfColorSpaceNames.Pattern:
                        Visit(array[1], depth + 1);
                        break;
                }
            }

            for (int p = 0; p < doc.Pages.Count; p++)
            {
                var scan = PdfXValidator.ScanPage(doc, doc.Pages[p],
                    PdfXValidator.DefaultMaxPageContentBytes, ref documentBytes,
                    PdfXValidator.DefaultMaxDocumentContentBytes, collectContent: false);
                if (scan.Incomplete != null)
                    ReportPartial($"Page {p + 1}: the A54-A55 spot-colour checks did not run in full — {scan.Incomplete}.", "A54", "A55");
                foreach (var resources in scan.Resources)
                {
                    if (!ScanReuse.FirstVisit(seenResources, resources)) continue;
                    if (doc.ResolveObject(resources[PdfName.ColorSpace]) is PdfDictionary spaces)
                        foreach (var kvp in spaces) Visit(kvp.Value, 0);
                    if (doc.ResolveObject(resources[PdfName.XObject]) is PdfDictionary xobjects)
                        foreach (var kvp in xobjects)
                            if (doc.ResolveObject(kvp.Value) is PdfStream xobject) Visit(xobject[PdfName.ColorSpace], 0);
                    if (doc.ResolveObject(resources[PdfName.Shading]) is PdfDictionary shadings)
                        foreach (var kvp in shadings)
                            if (doc.ResolveObject(kvp.Value) is PdfDictionary shading) Visit(shading[PdfName.ColorSpace], 0);
                }
            }

            if (undecided)
                ReportPartial("The A55 spot-colour comparison could not compare every definition — a tint transform " +
                              "would not decode, nested too deeply, or the comparison budget ran out.", "A55");

            string iso = $"ISO 19005-{(int)part} 6.2.4.4";
            if (missing.Count > 0)
                v.Add(new PdfAViolation("A54",
                    $"A DeviceN colour space names a spot colorant its /Colorants dictionary has no entry for: {missing} " +
                    $"({iso}; rule 6.2.4.4-1)."));
            if (inconsistent.Count > 0)
                v.Add(new PdfAViolation("A55",
                    $"Separation colour spaces of one name differ in tint transform or alternate space: {inconsistent} " +
                    $"({iso}; rule 6.2.4.4-2)."));
        }

        private static bool IsProcessColorant(string name)
            => name == "Cyan" || name == "Magenta" || name == "Yellow" || name == "Black" || name == "None";
    }
}
