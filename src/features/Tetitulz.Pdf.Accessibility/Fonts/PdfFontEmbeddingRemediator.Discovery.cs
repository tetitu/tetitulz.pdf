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
        //  Font discovery
        // ================================================================

        /// <summary>One font dictionary reached from the page resources, with everything the
        /// embedder needs to revise it.</summary>
        private sealed class FontSite
        {
            internal PdfDictionary FontDict;
            internal int           FontObjectNumber = -1;
            internal string        ResourceName;
            internal int           PageIndex;
            internal string        Subtype;
            internal string        BaseFont;

            /// <summary>The dictionary that owns <c>/FontDescriptor</c>: the font itself, or —
            /// for a Type0 font — its descendant CIDFont.</summary>
            internal PdfDictionary DescriptorOwner;
            internal int           DescriptorOwnerObjectNumber = -1;

            /// <summary>The descendant CIDFont's <c>/Subtype</c> — <c>CIDFontType0</c> (CFF
            /// outlines) or <c>CIDFontType2</c> (glyf outlines) — for a Type0 font; null otherwise.
            /// ISO 32000-1 Table 126 makes it the key that decides which <c>/FontFile</c> flavour
            /// the descriptor may legally carry.</summary>
            internal string DescendantSubtype;

            internal PdfDictionary Descriptor;
            internal int           DescriptorObjectNumber = -1;

            internal bool IsType0;
            internal bool IsType3;
            internal bool AlreadyEmbedded;
            internal bool IsSymbolic;
            internal string Standard14Name;
        }

        /// <summary>
        /// Collects every font dictionary the document can draw with: the page resources, the Form
        /// XObjects they invoke, the <b>annotation appearance streams</b> drawn over the page, the
        /// tiling patterns and soft-mask groups reached from a resource dictionary, and a Type 3
        /// font's own resources.
        ///
        /// <para>A font reached only from a widget's <c>/AP</c> — the standard AcroForm <c>Helv</c>
        /// — is as unembedded as one on the page, and the text it draws is as invisible to assistive
        /// technology, so it must be found here or it is never fixed.</para>
        /// </summary>
        /// <param name="incompleteness">
        /// Null when every page and every resource dictionary was read; otherwise a short phrase
        /// naming the first thing the walk could not reach. The returned list is then a
        /// <b>lower bound</b>, and — this is the point of the parameter — an empty one means "the
        /// walk fell over", not "this document has no unembedded font". A caller that reports the
        /// second when the first happened is telling the user the file is clean when nobody looked.
        /// </param>
        /// <param name="warnings">Optional; receives a sentence when the walk had to stop early.</param>
        /// <param name="doc">The document to walk.</param>
        /// <param name="index">Object index for <paramref name="doc"/>.</param>
        private static List<FontSite> CollectFontSites(
            PdfDocument doc, PdfObjectIndex index, out string incompleteness, List<string> warnings = null)
        {
            incompleteness = null;

            var order = new List<FontSite>();
            var seen  = new HashSet<PdfObject>(PdfObjectIndex.ReferenceIdentityComparer);

            IReadOnlyList<PdfPage> pages;
            try
            {
                pages = doc.Pages;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(Op,
                    "the page tree could not be read; no font was examined", ex);
                incompleteness = "the page tree could not be read (" + ex.Message + ")";
                return order;
            }

            if (pages == null)
            {
                incompleteness = "the document exposes no page list";
                return order;
            }

            // One visited set for the *document*, not one per page. A resource dictionary's fonts
            // only need collecting once — `seen` already keeps the first page a font is found on —
            // so re-walking a shared graph from every page bought nothing and cost O(pages x forms).
            var visitedResources = NewIdentitySet();
            var budget           = new WalkBudget(MaxResourceWalkSteps);

            for (int i = 0; i < pages.Count; i++)
            {
                PdfDictionary pageDict;
                PdfDictionary resources;
                try
                {
                    pageDict  = pages[i].Dictionary;
                    resources = pages[i].Resources;
                }
                catch (Exception ex)
                {
                    AccessibilityDiagnostics.Report(Op,
                        "page " + (i + 1).ToString(CultureInfo.InvariantCulture) +
                        " resources could not be read; its fonts are not examined", ex);
                    incompleteness = incompleteness ??
                        "page " + (i + 1).ToString(CultureInfo.InvariantCulture) +
                        "'s resources could not be read";
                    continue;
                }

                if (resources != null)
                    CollectFontsFrom(doc, index, resources, i, seen, order, visitedResources, 0, budget);

                // An appearance stream is a Form XObject that is drawn over the page without any
                // `Do` in the page content, so the resource walk above can never reach it.
                List<PdfStream> appearances =
                    CollectAppearanceStreams(doc, pageDict, budget, out bool appearancesComplete);

                if (!appearancesComplete)
                    incompleteness = incompleteness ??
                        "page " + (i + 1).ToString(CultureInfo.InvariantCulture) +
                        "'s annotation appearances could not be enumerated in full";

                for (int a = 0; a < appearances.Count; a++)
                {
                    PdfDictionary apResources =
                        ResolveDictionary(doc, appearances[a][PdfName.Resources]) ?? resources;
                    CollectFontsFrom(doc, index, apResources, i, seen, order, visitedResources, 0, budget);
                }

                if (budget.Exhausted) break;
            }

            if (budget.Exhausted)
            {
                incompleteness = incompleteness ??
                    "the resource graph exceeded this library's traversal budget of " +
                    MaxResourceWalkSteps.ToString(CultureInfo.InvariantCulture) + " steps";

                AccessibilityDiagnostics.Report(Op,
                    "the resource walk exceeded its traversal budget of " +
                    MaxResourceWalkSteps.ToString(CultureInfo.InvariantCulture) +
                    " steps; fonts beyond that point were not examined and are not embedded");
                warnings?.Add(
                    "The document's resource graph exceeded this library's traversal budget, so some " +
                    "fonts were not examined and remain unembedded; UA21/UA25 may stay open for them.");
            }

            return order;
        }

        /// <summary>
        /// Every font the page's resource graph reaches, in first-seen order.
        /// </summary>
        /// <remarks>
        /// The traversal itself is <see cref="WalkFontResources"/>, shared with the usage scan --
        /// the two had a copy each, and a font the OTHER one missed gets subsetted against a
        /// partial code set. What stays here is what is genuinely this caller's: first-page-wins
        /// dedupe, and one visited set for the whole DOCUMENT rather than one per page.
        /// </remarks>
        private static void CollectFontsFrom(
            PdfDocument doc,
            PdfObjectIndex index,
            PdfDictionary resources,
            int pageIndex,
            HashSet<PdfObject> seenFonts,
            List<FontSite> order,
            HashSet<PdfObject> visitedResources,
            int depth,
            WalkBudget budget)
        {
            WalkFontResources(doc, resources, visitedResources, depth, budget,
                (fontDict, resourceName) =>
                {
                    if (!seenFonts.Add(fontDict)) return;   // first page a font is seen on wins
                    order.Add(BuildSite(doc, index, fontDict, resourceName?.Value, pageIndex));
                });
        }

        /// <summary>
        /// Every appearance stream the annotations on <paramref name="pageDict"/> can present:
        /// <c>/AP /N</c>, <c>/D</c> and <c>/R</c>, each of which is either a Form XObject directly
        /// or a dictionary of appearance states (<c>/Off</c>, <c>/Yes</c>, …) whose values are.
        ///
        /// <para>All three are collected, not just <c>/N</c>: a down or rollover appearance is drawn
        /// by the viewer too, and a subset that dropped its glyphs would blank it.</para>
        /// </summary>
        /// <param name="complete">False when <c>/Annots</c> could not be read, so the returned list
        /// is a lower bound rather than the answer.</param>
        /// <param name="doc">The document, for resolving indirect references.</param>
        /// <param name="pageDict">The page whose <c>/Annots</c> are collected from.</param>
        /// <param name="budget">Aggregate work budget spanning the whole discovery pass.</param>
        private static List<PdfStream> CollectAppearanceStreams(
            PdfDocument doc, PdfDictionary pageDict, WalkBudget budget, out bool complete)
        {
            complete = true;
            var streams = new List<PdfStream>();
            if (pageDict == null) return streams;

            PdfArray annots;
            try
            {
                annots = ResolveArray(doc, pageDict[PdfName.Annots]);
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(Op,
                    "a page's /Annots array could not be read; the fonts its annotations draw with " +
                    "are not examined", ex);
                complete = false;
                return streams;
            }

            if (annots == null) return streams;

            var seenAppearances = NewIdentitySet();

            for (int i = 0; i < annots.Count; i++)
            {
                if (!budget.Take()) { complete = false; return streams; }

                PdfDictionary annot = ResolveDictionary(doc, annots[i]);
                PdfDictionary ap    = annot == null ? null : ResolveDictionary(doc, annot[PdfName.AP]);
                if (ap == null) continue;

                if (!AddAppearance(doc, ap[PdfName.N], streams, seenAppearances, budget)
                 || !AddAppearance(doc, ap[PdfName.D], streams, seenAppearances, budget)
                 || !AddAppearance(doc, ap[PdfName.R], streams, seenAppearances, budget))
                {
                    complete = false;
                    return streams;
                }
            }

            return streams;
        }

        /// <summary>Adds one <c>/AP</c> entry's stream(s). Returns false when the budget ran out.</summary>
        private static bool AddAppearance(
            PdfDocument doc,
            PdfObject entry,
            List<PdfStream> streams,
            HashSet<PdfObject> seen,
            WalkBudget budget)
        {
            if (entry == null) return true;
            if (!budget.Take()) return false;

            PdfObject resolved = doc.ResolveObject(entry);

            // PdfStream derives from PdfDictionary, so the stream case has to be tested first.
            if (resolved is PdfStream direct)
            {
                if (seen.Add(direct)) streams.Add(direct);
                return true;
            }

            if (!(resolved is PdfDictionary states)) return true;

            foreach (KeyValuePair<PdfName, PdfObject> kvp in states)
            {
                if (!budget.Take()) return false;
                if (doc.ResolveObject(kvp.Value) is PdfStream state && seen.Add(state))
                    streams.Add(state);
            }

            return true;
        }

        private static FontSite BuildSite(
            PdfDocument doc, PdfObjectIndex index, PdfDictionary fontDict, string resourceName, int pageIndex)
        {
            // `index` is null when the caller only wants the count (see CountNonEmbeddedFonts):
            // object numbers are needed to *write*, not to decide whether a program is missing.
            var site = new FontSite
            {
                FontDict         = fontDict,
                FontObjectNumber = index?.GetObjectNumber(fontDict) ?? -1,
                ResourceName     = resourceName,
                PageIndex        = pageIndex,
                Subtype          = ResolveName(doc, fontDict[PdfName.Subtype])?.Value,
                BaseFont         = ResolveName(doc, fontDict[PdfName.BaseFont])?.Value,
            };

            site.IsType0 = site.Subtype == "Type0";
            site.IsType3 = site.Subtype == "Type3";
            site.Standard14Name = CanonicalStandard14Name(site.BaseFont);

            // A Type 3 font's glyphs ARE content streams held in /CharProcs; there is no program
            // to embed and the requirement does not apply to it.
            if (site.IsType3)
            {
                site.AlreadyEmbedded = true;
                return site;
            }

            site.DescriptorOwner = fontDict;

            if (site.IsType0)
            {
                PdfDictionary descendant = null;
                if (doc.ResolveObject(fontDict[PdfName.DescendantFonts]) is PdfArray descendants
                    && descendants.Count > 0)
                    descendant = ResolveDictionary(doc, descendants[0]);

                // No readable descendant: nothing can own a descriptor, so nothing can be embedded.
                site.DescriptorOwner   = descendant;
                site.DescendantSubtype = descendant == null
                    ? null
                    : ResolveName(doc, descendant[PdfName.Subtype])?.Value;
            }

            if (site.DescriptorOwner != null)
            {
                site.DescriptorOwnerObjectNumber = index?.GetObjectNumber(site.DescriptorOwner) ?? -1;
                site.Descriptor = ResolveDictionary(doc, site.DescriptorOwner[PdfName.FontDescriptor]);
                if (site.Descriptor != null)
                    site.DescriptorObjectNumber = index?.GetObjectNumber(site.Descriptor) ?? -1;
            }

            site.AlreadyEmbedded = site.Descriptor != null
                && (site.Descriptor[PdfName.FontFile]  != null
                 || site.Descriptor[PdfName.FontFile2] != null
                 || site.Descriptor[PdfName.FontFile3] != null);

            int? flags = GetInteger(doc, site.Descriptor?[PdfName.Flags]);
            site.IsSymbolic = (flags.HasValue && (flags.Value & 4) != 0)
                           || site.Standard14Name == StandardFonts.Symbol
                           || site.Standard14Name == StandardFonts.ZapfDingbats;

            return site;
        }
    }
}
