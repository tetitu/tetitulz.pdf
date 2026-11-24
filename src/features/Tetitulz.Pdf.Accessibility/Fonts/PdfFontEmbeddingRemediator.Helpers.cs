// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Accessibility.Editing;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Accessibility.Fonts
{
    public static partial class PdfFontEmbeddingRemediator
    {
        // ================================================================
        //  Small helpers
        // ================================================================

        // One implementation, in Core.IO.StreamBytes. It rewinds a seekable stream before
        // reading, where a copy reading from the current position would see only the rest.
        private static byte[] ReadAll(Stream s) => StreamBytes.ReadAll(s);

        /// <summary>
        /// Returns the clone of <paramref name="objectNumber"/> already staged for this revision, or
        /// makes one. Two edits to the same dictionary (widths and encoding, say) must land on one
        /// clone, or the second silently discards the first.
        /// </summary>
        private static PdfDictionary TakeClone(
            Dictionary<int, PdfObject> modified, int objectNumber, PdfDictionary source)
            => modified.TryGetValue(objectNumber, out PdfObject existing) && existing is PdfDictionary staged
                ? staged
                : CloneDictionary(source);

        /// <summary>
        /// Clones <paramref name="source"/>, or starts a new dictionary when it is <c>null</c>.
        ///
        /// <para><b>The null case is load-bearing here and nowhere else.</b> This tolerates a null
        /// source on purpose — <c>StageOrClone</c> calls it for a font that has not been staged yet and may
        /// have no dictionary to copy, and an empty one is the correct starting point. The shared
        /// <see cref="PdfDictionary.CloneShallow"/> throws instead, deliberately, because for the
        /// other callers a null source is a bug that would otherwise surface as a
        /// <see cref="System.NullReferenceException"/> much later. Naming the tolerance is the
        /// difference between an exception this code has decided about and one it has not.</para>
        /// </summary>
        private static PdfDictionary CloneDictionary(PdfDictionary source)
            => source == null ? new PdfDictionary() : PdfDictionary.CloneShallow(source);

        // One implementation, in Internal.Resolve, so a damaged font dictionary is reported and
        // skipped rather than thrown out of the remediation pipeline. There is no direct-value fast
        // path, which looks like a defect and is not: PdfParser.ResolveObject begins `while (obj is
        // PdfReference …)`, so a direct object is returned unchanged.
        private static PdfDictionary ResolveDictionary(PdfDocument doc, PdfObject obj)
            => Internal.Resolve.Dict(doc, obj, "PdfFontEmbeddingRemediator.ResolveDictionary");

        private static PdfArray ResolveArray(PdfDocument doc, PdfObject obj)
            => Internal.Resolve.Array(doc, obj, "PdfFontEmbeddingRemediator.ResolveArray");

        private static PdfName ResolveName(PdfDocument doc, PdfObject obj)
            => Internal.Resolve.Name(doc, obj, "PdfFontEmbeddingRemediator.ResolveName");

        private static int? GetInteger(PdfDocument doc, PdfObject obj)
            => doc?.ResolveObject(obj) is PdfNumber number ? number.IntValue : (int?)null;
        /// <summary>
        /// Every font a <c>/Resources</c> graph can reach, in one traversal:
        /// <c>/Font</c> (recursing into a Type 3 font's own <c>/Resources</c>),
        /// <c>/XObject</c> filtered to <c>/Subtype /Form</c>, <c>/Pattern</c> filtered to
        /// <c>/PatternType 1</c>, and <c>/ExtGState</c> through both <c>/Font</c> and
        /// <c>/SMask /G</c>.
        /// </summary>
        /// <remarks>
        /// <b>One walk for the collector and the usage scan.</b> The collector decides which fonts
        /// get embedded; the usage scan decides which fonts are known to have a complete code set. A
        /// font a second walk omitted would keep <c>UsedTextIsComplete</c> true and be
        /// <b>subsetted against a partial code set</b> — glyphs silently missing from a document the
        /// remediator reports as fixed.
        ///
        /// <para><b>The visited set and the budget stay parameters, because their scoping differs on
        /// purpose.</b> The collector passes ONE set for the whole document — re-walking a shared
        /// graph from every page bought nothing — while the usage scan passes a fresh set at each of
        /// its five call sites, because it is invoked per unreadable page and must not have an
        /// earlier page's visit suppress this one's. Unifying them would be a behaviour change.</para>
        ///
        /// <para>Dedupe stays with the caller too: the collector wants first-page-wins over the
        /// document, the usage scan wants every occurrence. The walk simply reports each font it
        /// reaches.</para>
        ///
        /// <para><b>A null budget is tolerated</b>, which is the usage scan's spelling rather than
        /// the collector's unguarded dereference. No call site passes null today; taking the
        /// guarded form costs nothing and removes the one way the merged walk could throw where
        /// neither original did.</para>
        /// </remarks>
        /// <param name="onFont">
        /// Called for each font reached, with the resource name it was found under — <c>null</c>
        /// for a font named only by an <c>/ExtGState /Font</c> array, which §8.4.5 says sets the
        /// text font exactly as <c>Tf</c> does but which is not a named font resource.
        /// </param>
        /// <param name="doc">The document, for resolving indirect references.</param>
        /// <param name="resources">The <c>/Resources</c> dictionary to walk.</param>
        /// <param name="visitedResources">
        /// Resource dictionaries already walked, so a cycle through nested XObjects terminates.
        /// </param>
        /// <param name="depth">Current nesting depth, against the recursion cap.</param>
        /// <param name="budget">Aggregate work budget spanning the whole walk, not just this branch.</param>
        private static void WalkFontResources(
            PdfDocument doc,
            PdfDictionary resources,
            HashSet<PdfObject> visitedResources,
            int depth,
            WalkBudget budget,
            Action<PdfDictionary, PdfName> onFont)
        {
            if (resources == null || depth > MaxResourceDepth) return;
            if (budget != null && !budget.Take()) return;

            // Resource dictionaries can be shared, and a Form XObject can reference its own.
            if (!visitedResources.Add(resources)) return;

            PdfDictionary fonts = ResolveDictionary(doc, resources[PdfName.Font]);
            if (fonts != null)
            {
                foreach (KeyValuePair<PdfName, PdfObject> kvp in fonts)
                {
                    if (budget != null && !budget.Take()) return;

                    PdfDictionary fontDict = ResolveDictionary(doc, kvp.Value);
                    if (fontDict == null) continue;

                    onFont(fontDict, kvp.Key);

                    // A Type 3 font's glyphs are content streams that draw with the font's own
                    // /Resources; a font reachable only from there still shows text on the page.
                    if (ResolveName(doc, fontDict[PdfName.Subtype])?.Value == "Type3")
                        WalkFontResources(doc, ResolveDictionary(doc, fontDict[PdfName.Resources]),
                            visitedResources, depth + 1, budget, onFont);
                }
            }

            PdfDictionary xobjects = ResolveDictionary(doc, resources[PdfName.XObject]);
            if (xobjects != null)
            {
                foreach (KeyValuePair<PdfName, PdfObject> kvp in xobjects)
                {
                    if (budget != null && !budget.Take()) return;
                    if (!(doc.ResolveObject(kvp.Value) is PdfStream xobj)) continue;
                    if (ResolveName(doc, xobj[PdfName.Subtype])?.Value != "Form") continue;

                    WalkFontResources(doc, ResolveDictionary(doc, xobj[PdfName.Resources]),
                        visitedResources, depth + 1, budget, onFont);
                }
            }

            // A tiling pattern (/PatternType 1) is a content stream with resources of its own.
            PdfDictionary patterns = ResolveDictionary(doc, resources[PdfName.Pattern]);
            if (patterns != null)
            {
                foreach (KeyValuePair<PdfName, PdfObject> kvp in patterns)
                {
                    if (budget != null && !budget.Take()) return;
                    if (!(doc.ResolveObject(kvp.Value) is PdfStream tiling)) continue;
                    if (GetInteger(doc, tiling[PdfName.PatternType]) != 1) continue;

                    WalkFontResources(doc, ResolveDictionary(doc, tiling[PdfName.Resources]),
                        visitedResources, depth + 1, budget, onFont);
                }
            }

            PdfDictionary extStates = ResolveDictionary(doc, resources[PdfName.ExtGState]);
            if (extStates == null) return;

            foreach (KeyValuePair<PdfName, PdfObject> kvp in extStates)
            {
                if (budget != null && !budget.Take()) return;

                PdfDictionary gs = ResolveDictionary(doc, kvp.Value);
                if (gs == null) continue;

                // ISO 32000-1 §8.4.5: /Font is [font size] and sets the text font exactly as `Tf`
                // does, so a font named only here is still a font the page draws with.
                PdfArray gsFont = ResolveArray(doc, gs[PdfName.Font]);
                if (gsFont != null && gsFont.Count > 0)
                {
                    PdfDictionary gsFontDict = ResolveDictionary(doc, gsFont[0]);
                    if (gsFontDict != null) onFont(gsFontDict, null);
                }

                // A soft mask's /G group is a Form XObject executed by `gs`, never by `Do`.
                PdfDictionary smask = ResolveDictionary(doc, gs[PdfName.SMask]);
                if (smask != null && doc.ResolveObject(smask[PdfName.G]) is PdfStream group)
                    WalkFontResources(doc, ResolveDictionary(doc, group[PdfName.Resources]),
                        visitedResources, depth + 1, budget, onFont);
            }
        }

    }
}


