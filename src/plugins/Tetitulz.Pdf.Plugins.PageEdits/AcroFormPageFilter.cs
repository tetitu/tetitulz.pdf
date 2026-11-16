// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.PageEdits
{
    /// <summary>
    /// The plan an extraction follows for the catalog <c>/AcroForm</c>: which field nodes may travel
    /// with it, and which entry of each <c>/Fields</c> and <c>/Kids</c> array survives.
    /// </summary>
    internal sealed class AcroFormPageFilterResult
    {
        /// <summary>
        /// Every surviving field node, in discovery order. The extraction collects each node's own
        /// graph from this list, which is what keeps a dropped field's <c>/V</c> — and its
        /// appearance streams — out of the output entirely rather than merely unreferenced.
        /// </summary>
        internal List<PdfDictionary> KeptNodes { get; } = new List<PdfDictionary>();

        /// <summary>Reference-identity membership test over <see cref="KeptNodes"/>.</summary>
        internal HashSet<PdfDictionary> Kept { get; } = new HashSet<PdfDictionary>();

        /// <summary>
        /// Surviving child indices, keyed by the <b>source</b> dictionary that owns the array — the
        /// AcroForm for <c>/Fields</c>, a field node for <c>/Kids</c>.
        /// </summary>
        /// <remarks>
        /// Indices rather than object numbers, because <c>DeepClone</c> preserves array order and
        /// count, so the same index selects the same entry on both sides. That is what lets one rule
        /// cover a field written as an indirect reference (what §12.7.2 Table 218 requires) and one
        /// written as a direct dictionary (what a real producer sometimes emits): the direct one has
        /// no object number to filter by, but it does have a position.
        /// </remarks>
        internal Dictionary<PdfDictionary, List<int>> KeptChildIndices { get; } =
            new Dictionary<PdfDictionary, List<int>>();

        /// <summary>
        /// <c>true</c> when at least one top-level field has a widget on a page being extracted. When
        /// this is <c>false</c> the extraction writes no <c>/AcroForm</c> at all: a form whose every
        /// field is gone is not a form, and carrying the shell would leave <c>/DR</c> and <c>/DA</c>
        /// describing fields the document no longer has.
        /// </summary>
        internal bool AnyFieldSurvives { get; set; }

        /// <summary>
        /// Why the plan is not a faithful answer, or <c>null</c> when it is. Set only for an anomaly
        /// in the <i>source</i> — a <c>/Kids</c> cycle, or a walk stopped by the depth cap or the
        /// node budget. A field dropped because its widgets are all on pages the caller did not ask
        /// for is the <b>point</b> of the operation, not an anomaly, and is deliberately silent —
        /// the same judgement <c>PdfSplitter.LastNameTreeWarning</c> already makes about a named
        /// destination whose page is out of range.
        /// </summary>
        internal string Warning { get; set; }

        /// <summary>
        /// How much of the edge budget the plan spent: the walk's own evidence of how much of the
        /// field tree it examined, which a plan's answer cannot show.
        /// </summary>
        internal int EdgesExamined { get; set; }
    }

    /// <summary>
    /// Decides which AcroForm fields may travel with an extracted page range: those with at least one
    /// widget on a page being extracted.
    ///
    /// <para>
    /// <b>Why this exists.</b> The catalog <c>/AcroForm /Fields</c> array is not reached from any
    /// page, so the splitter collects it explicitly. Collected whole, a field whose widgets all land
    /// on dropped pages would travel with its <c>/V</c> intact: split page 1 of a filled form to
    /// share it, and the recipient would get the answers written on pages 2 onward, invisible in a
    /// viewer and readable with any PDF object dump. Dropping the widget's <c>/P</c> keeps the field
    /// from dragging a whole page in; this keeps its value out.
    /// </para>
    ///
    /// <para>
    /// <b>Field-tree semantics are borrowed, not invented.</b> A terminal field can be the widget
    /// itself (a merged field/widget dictionary) or a parent with <c>/Kids</c>, and <c>/FT</c> is
    /// inheritable through <c>/Parent</c> — the determination
    /// <c>Tetitulz.Pdf.Plugins.Conformance.AcroFormFieldWalk</c> owns for the PDF/UA checker and
    /// fixer. This file matches its reading (same <c>MaxFieldDepth</c>, same per-iteration budget
    /// accounting, same reference-identity guard) rather than starting a third one; it cannot
    /// <i>call</i> it, because a plugin → plugin reference is banned outright by the ring model and
    /// there is no downward home for a walk neither Base nor Core needs. It asks a different
    /// question in any case: not "is this field terminal and un-named" but "does anything under it
    /// appear on a page we are keeping", which needs no <c>/FT</c> at all.
    /// </para>
    /// </summary>
    internal static class AcroFormPageFilter
    {
        /// <summary>
        /// Maximum <c>/Kids</c> nesting. Same value as
        /// <c>Conformance.AcroFormFieldWalk.MaxFieldDepth</c>, so a field tree one of them refuses is
        /// refused by both.
        /// </summary>
        /// <remarks>
        /// <para><b>It pins the <c>/Kids</c> DESCENT and only that.</b> The symbol it names means
        /// two things on the Conformance side — that constant bounds a descent <i>and</i> the
        /// <c>/Parent</c> hops of an inheritable-attribute climb — and this file mirrors only the
        /// first. Saying so matters because the two are free to move apart: raising the climb is a
        /// reasonable thing to want, and doing it through the shared symbol would move this cap as a
        /// side effect nobody wrote down.</para>
        ///
        /// <para><b>The pin is one-sided, which is why it now has a test.</b>
        /// <c>PdfSplitter</c> reads <i>this</i> constant by symbol, so it can never drift from
        /// PageEdits; nothing whatever connects PageEdits to Conformance except the sentence above.
        /// Raising Conformance's alone makes the checker accept field trees the splitter still
        /// refuses, silently. <c>CrossPluginConstantInvariantTests</c> in
        /// <c>Tetitulz.Pdf.Base.Tests</c> compares the two declarations on disk — neither plugin's
        /// test project can see both <c>internal</c>s, and referencing both from one would invert
        /// the ring model.</para>
        /// </remarks>
        internal const int MaxFieldDepth = 50;

        /// <summary>
        /// Ceiling on edges examined. Spent per <i>iteration</i>, never per distinct node reached: a
        /// visited set stops re-expansion but does nothing about the cost of one expansion, and N
        /// fields that all name one shared N-entry <c>/Kids</c> array cost N² resolve-and-probe
        /// operations while a per-node counter moves only N times.
        /// </summary>
        internal const int MaxFieldNodes = 200_000;

        /// <summary>
        /// Builds the plan for <paramref name="acroForm"/> against the pages being extracted.
        /// </summary>
        /// <param name="acroForm">The source form dictionary, already resolved.</param>
        /// <param name="doc">The source document, used only to resolve references.</param>
        /// <param name="keptPageObjNums">Object numbers of the pages being extracted.</param>
        /// <param name="keptPages">Those pages, for their <c>/Annots</c> arrays.</param>
        /// <param name="nodeBudget">Edge ceiling; lowering it is how a test exercises the budget
        /// guard without building a 200 000-node file.</param>
        /// <param name="index">
        /// The form's field tree as <see cref="AcroFormFieldIndex.TryBuild"/> read it, or <c>null</c>.
        /// With one, the walk visits only the nodes that can reach a kept page, and the plan is the
        /// one the whole walk would make; see <see cref="AcroFormFieldIndex"/> for why it is.
        /// </param>
        internal static AcroFormPageFilterResult Filter(
            PdfDictionary acroForm, PdfDocument doc,
            HashSet<int> keptPageObjNums, IReadOnlyList<PdfPage> keptPages,
            int nodeBudget = MaxFieldNodes, AcroFormFieldIndex index = null)
        {
            var result = new AcroFormPageFilterResult();
            if (acroForm == null || doc == null) return result;

            var state = new State(doc, keptPageObjNums, nodeBudget);
            CollectPlacedAnnotations(keptPages, doc, state);
            // Which pages list each annotation is a reading of the whole document: taken from the index
            // when there is one (once a split), else read here (pages × annotations, once a call).
            bool indexed = index != null && ReferenceEquals(index.Form, acroForm);
            if (indexed) state.ListingIndex = index;
            else CollectAnnotationsListedElsewhere(doc, state);

            if (!(doc.ResolveObject(acroForm[PdfName.Fields]) is PdfArray fields))
            {
                result.EdgesExamined = state.Spent;
                return result;
            }

            // The whole walk spends at most the index's fixed spend beyond the annotations, so when
            // that fits it can neither stop short nor report anything, and narrowing it to the nodes
            // that reach a kept page cannot change its plan.
            if (indexed && !state.BudgetExhausted && (long)state.Spent + index.FixedSpend <= nodeBudget)
                state.Reach = index.ReachOf(state);

            // Two passes over the same memo. The first answers "does anything under this node appear
            // on a kept page?" for every node; the second records the survivors and the index lists,
            // and needs the first pass's answer for EVERY sibling before it can filter an array.
            var keptTopLevel = new List<int>();
            var topPositions = state.Reach?.TopLevel;
            int topCount = topPositions?.Count ?? fields.Count;
            for (int k = 0; k < topCount; k++)
            {
                int i = topPositions?[k] ?? k;
                if (!state.Spend()) break;
                if (doc.ResolveObject(fields[i]) is PdfDictionary node && Survives(node, state, 0))
                    keptTopLevel.Add(i);
            }

            foreach (int i in keptTopLevel)
                if (doc.ResolveObject(fields[i]) is PdfDictionary node)
                    Record(node, state, result, 0);

            result.KeptChildIndices[acroForm] = keptTopLevel;
            result.AnyFieldSurvives = keptTopLevel.Count > 0;
            result.Warning = state.BuildWarning();
            result.EdgesExamined = state.Spent;
            return result;
        }

        /// <summary>
        /// Whether this node, or anything under its <c>/Kids</c>, is placed on a page being
        /// extracted.
        /// </summary>
        /// <remarks>
        /// The node's own placement is tested <b>before</b> its kids, which is what makes the cycle
        /// handling correct as well as fast: a node that is itself on a kept page never recurses, so
        /// a back edge can only be reached from a node with no placement of its own. Its kids are
        /// asked later, by <see cref="Record"/>, when it is kept — and a back edge then meets the
        /// placed node's own answer before it can meet the in-progress set.
        /// <para>
        /// A back edge answers <c>false</c> — the ancestor's own contribution is already being
        /// computed by the frame that owns it. The one case this is pessimistic about is a node whose
        /// <i>only</i> route to a kept page runs through an ancestor of itself, which is dropped. A
        /// <c>/Kids</c> cycle is malformed (§12.7.3.1 describes a tree), dropping is the safe
        /// direction for a filter whose job is to withhold data, and the cycle is reported.
        /// </para>
        /// </remarks>
        private static bool Survives(PdfDictionary node, State state, int depth)
        {
            if (state.Memo.TryGetValue(node, out bool known)) return known;
            if (depth > MaxFieldDepth) { state.DepthExceeded = true; return false; }
            if (!state.InProgress.Add(node)) { state.CycleSeen = true; return false; }

            try
            {
                if (state.IsPlacedOnKeptPage(node))
                {
                    state.Memo[node] = true;
                    return true;
                }

                bool any = false;
                if (state.Resolve(node[PdfName.Kids]) is PdfArray kids)
                {
                    var positions = state.Reach?.KidsOf(node);
                    int count = positions?.Count ?? kids.Count;
                    for (int k = 0; k < count; k++)
                    {
                        int i = positions?[k] ?? k;
                        if (!state.Spend()) break;
                        // No early exit on the first survivor: the second pass needs every sibling's
                        // answer to filter /Kids, and stopping here would leave the rest unmemoised.
                        if (state.Resolve(kids[i]) is PdfDictionary kid && Survives(kid, state, depth + 1))
                            any = true;
                    }
                }

                state.Memo[node] = any;
                return any;
            }
            finally
            {
                state.InProgress.Remove(node);
            }
        }

        /// <summary>
        /// Records a surviving node and the indices of its surviving <c>/Kids</c>.
        /// </summary>
        private static void Record(
            PdfDictionary node, State state, AcroFormPageFilterResult result, int depth)
        {
            // A field reached twice — a /Kids diamond, or a cycle the first pass already bounded —
            // is one object with one filter, so recording it once is both correct and terminating.
            if (!result.Kept.Add(node)) return;
            result.KeptNodes.Add(node);
            if (depth > MaxFieldDepth) { state.DepthExceeded = true; return; }

            if (!(state.Resolve(node[PdfName.Kids]) is PdfArray kids)) return;

            var keptKids = new List<int>();
            var positions = state.Reach?.KidsOf(node);
            int count = positions?.Count ?? kids.Count;
            for (int k = 0; k < count; k++)
            {
                int i = positions?[k] ?? k;
                if (!state.Spend()) break;
                if (!(state.Resolve(kids[i]) is PdfDictionary kid)) continue;
                // A node placed on a kept page answers without reading its kids, so a kid met only
                // below one has no answer yet. It is asked here: taking the missing answer for "no"
                // kept a parent field that carries its own /P and dropped every widget under it,
                // the one on the kept page included.
                if (!state.Memo.TryGetValue(kid, out bool survives))
                    survives = Survives(kid, state, depth + 1);
                if (!survives) continue;
                keptKids.Add(i);
                Record(kid, state, result, depth + 1);
            }

            result.KeptChildIndices[node] = keptKids;
        }

        /// <summary>
        /// The object numbers of every field node the form's <c>/Fields</c> reaches through
        /// <c>/Kids</c>, each node read once; <paramref name="complete"/> is <c>false</c> when
        /// <paramref name="nodeBudget"/> entries were read before the tree ended.
        /// </summary>
        /// <remarks>
        /// What the extraction's collection walk refuses to enter: a reference from a kept page's
        /// action into a dropped field. Breadth first with a visited set, so a cycle or a deep chain
        /// costs its entries and nothing more.
        /// </remarks>
        internal static HashSet<int> FieldNodeNumbers(
            PdfDictionary acroForm, PdfDocument doc, out bool complete, int nodeBudget = MaxFieldNodes)
        {
            var numbers = new HashSet<int>();
            complete = true;
            if (acroForm == null || doc == null ||
                !(doc.ResolveObject(acroForm[PdfName.Fields]) is PdfArray fields))
                return numbers;

            var seen = new HashSet<PdfDictionary>();
            var pending = new Queue<PdfArray>();
            pending.Enqueue(fields);
            int budget = nodeBudget;
            while (pending.Count > 0)
            {
                var array = pending.Dequeue();
                for (int i = 0; i < array.Count; i++)
                {
                    if (budget-- <= 0) { complete = false; return numbers; }
                    if (!(doc.ResolveObject(array[i]) is PdfDictionary node) || !seen.Add(node)) continue;
                    if (node.ObjectNumber > 0) numbers.Add(node.ObjectNumber);
                    if (doc.ResolveObject(node[PdfName.Kids]) is PdfArray kids) pending.Enqueue(kids);
                }
            }
            return numbers;
        }

        /// <summary>
        /// Whether <paramref name="dict"/> reads as a field node — a field type, kids, a partial name
        /// under a parent, or a widget — for when <see cref="FieldNodeNumbers"/> could not finish.
        /// </summary>
        internal static bool LooksLikeField(PdfDictionary dict, PdfDocument doc) =>
            dict[PdfName.FT] != null || dict[PdfName.Kids] != null ||
            (dict[PdfName.T] != null && dict[PdfName.Parent] != null) ||
            doc.ResolveObject(dict[PdfName.Subtype]) as PdfName == PdfName.Widget;

        /// <summary>
        /// Indexes the annotations of every page being extracted, by object number and by reference
        /// identity, so a widget can be recognised without trusting its <c>/P</c>.
        /// </summary>
        /// <remarks>
        /// §12.5.6.19's <c>/P</c> is optional, and §12.5.2 makes the page's <c>/Annots</c> the
        /// definitive statement of what appears on it. Testing both is what stops the filter dropping
        /// a field the recipient can see.
        /// </remarks>
        private static void CollectPlacedAnnotations(
            IReadOnlyList<PdfPage> keptPages, PdfDocument doc, State state)
        {
            if (keptPages == null) return;

            for (int p = 0; p < keptPages.Count; p++)
            {
                var annots = keptPages[p]?.GetAnnotations();
                if (annots == null) continue;

                for (int i = 0; i < annots.Count; i++)
                {
                    if (!state.Spend()) return;

                    if (annots[i] is PdfReference reference)
                    {
                        state.PlacedObjNums.Add(reference.ReferenceObjectNumber);
                        continue;
                    }
                    if (doc.ResolveObject(annots[i]) is PdfDictionary direct)
                        state.PlacedDictionaries.Add(direct);
                }
            }
        }

        /// <summary>
        /// Indexes the annotations of every page <b>not</b> being extracted, so a widget a dropped page
        /// lists is known to be placed there whatever its <c>/P</c> says: <c>/P</c> is a back-pointer,
        /// consulted only for a widget no page lists, and a stale one naming a kept page carried the
        /// dropped page's field value into the extract.
        /// </summary>
        private static void CollectAnnotationsListedElsewhere(PdfDocument doc, State state)
        {
            IReadOnlyList<PdfPage> pages = doc.Pages;
            if (pages == null) return;

            for (int p = 0; p < pages.Count; p++)
            {
                PdfPage page = pages[p];
                int pageNumber = page?.Dictionary?.ObjectNumber ?? 0;
                if (pageNumber > 0 && state.KeptPageObjNums.Contains(pageNumber)) continue;

                var annots = page?.GetAnnotations();
                if (annots == null) continue;

                for (int i = 0; i < annots.Count; i++)
                {
                    if (!state.Spend()) return;

                    if (annots[i] is PdfReference reference)
                    {
                        state.ListedElsewhereObjNums.Add(reference.ReferenceObjectNumber);
                        continue;
                    }
                    if (doc.ResolveObject(annots[i]) is PdfDictionary direct)
                        state.ListedElsewhereDictionaries.Add(direct);
                }
            }
        }

        internal sealed class State
        {
            private readonly PdfDocument _doc;
            private readonly HashSet<int> _keptPageObjNums;
            private readonly int _nodeBudget;
            private int _budget;

            internal State(PdfDocument doc, HashSet<int> keptPageObjNums, int nodeBudget)
            {
                _doc = doc;
                _keptPageObjNums = keptPageObjNums ?? new HashSet<int>();
                _nodeBudget = nodeBudget;
                _budget = nodeBudget;
            }

            internal readonly Dictionary<PdfDictionary, bool> Memo =
                new Dictionary<PdfDictionary, bool>();
            internal readonly HashSet<PdfDictionary> InProgress = new HashSet<PdfDictionary>();
            internal readonly HashSet<int> PlacedObjNums = new HashSet<int>();
            internal readonly HashSet<PdfDictionary> PlacedDictionaries = new HashSet<PdfDictionary>();

            /// <summary>The annotations a page not being extracted lists; their <c>/P</c> is not consulted.</summary>
            internal readonly HashSet<int> ListedElsewhereObjNums = new HashSet<int>();
            internal readonly HashSet<PdfDictionary> ListedElsewhereDictionaries = new HashSet<PdfDictionary>();

            /// <summary>
            /// When set, the document-wide reading of which pages list each annotation, taken once a
            /// split, answers <see cref="IsListedElsewhere"/> in place of the two sets above.
            /// </summary>
            internal AcroFormFieldIndex ListingIndex;

            /// <summary>Whether a page not being extracted lists <paramref name="node"/> among its annotations.</summary>
            internal bool IsListedElsewhere(PdfDictionary node)
            {
                if (ListingIndex != null) return ListingIndex.IsListedByAPageOutside(node, _keptPageObjNums);
                return (node.ObjectNumber > 0 && ListedElsewhereObjNums.Contains(node.ObjectNumber))
                       || ListedElsewhereDictionaries.Contains(node);
            }

            internal bool CycleSeen;
            internal bool DepthExceeded;
            internal bool BudgetExhausted;

            /// <summary>
            /// The positions the walk visits when an index narrowed it, or <c>null</c> for every
            /// position of every array.
            /// </summary>
            internal AcroFormFieldIndex.Reach Reach;

            internal HashSet<int> KeptPageObjNums => _keptPageObjNums;

            /// <summary>Units of the edge budget spent so far.</summary>
            internal int Spent => _nodeBudget - Math.Max(_budget, 0);

            internal PdfObject Resolve(PdfObject obj) => _doc.ResolveObject(obj);

            /// <summary>Consumes one unit of the edge budget; <c>false</c> once it is spent.</summary>
            internal bool Spend()
            {
                if (_budget <= 0) { BudgetExhausted = true; return false; }
                _budget--;
                return true;
            }

            internal bool IsPlacedOnKeptPage(PdfDictionary node)
            {
                if (node.ObjectNumber > 0 && PlacedObjNums.Contains(node.ObjectNumber)) return true;
                if (PlacedDictionaries.Contains(node)) return true;

                // /P is a back-pointer, so it is consulted only after /Annots — a widget listed by a
                // kept page is on that page whatever its /P claims, and one a dropped page lists is on
                // that page whatever its /P claims too; the fallback is for a widget no page lists.
                if (IsListedElsewhere(node)) return false;
                return node[PdfName.P] is PdfReference page
                    && _keptPageObjNums.Contains(page.ReferenceObjectNumber);
            }

            internal string BuildWarning()
            {
                var reasons = new List<string>();
                if (CycleSeen) reasons.Add("a /Kids cycle");
                if (DepthExceeded)
                    reasons.Add("nesting deeper than " +
                                MaxFieldDepth.ToString(CultureInfo.InvariantCulture) + " levels");
                if (BudgetExhausted)
                    reasons.Add("the " + _nodeBudget.ToString(CultureInfo.InvariantCulture) +
                                "-edge traversal budget");
                if (reasons.Count == 0) return null;

                return "The AcroForm field walk hit " + string.Join(" and ", reasons) +
                       ", so part of the field tree could not be proven to appear on an extracted " +
                       "page. Everything unproven was dropped from the extract, which is the safe " +
                       "direction for a filter whose job is to withhold the other pages' values, " +
                       "but it means the extracted form may be missing fields the source placed on " +
                       "a page you kept.";
            }
        }
    }

    /// <summary>
    /// A form's field tree, read once, so that a document extracted page by page examines for each
    /// extraction only the fields that can reach a kept page.
    /// </summary>
    /// <remarks>
    /// <para><see cref="AcroFormPageFilter.Filter"/> walked the whole tree for every extraction, so
    /// splitting a form with a field on every page into single pages examined pages × fields nodes.
    /// Given this index it visits only the placed nodes and the nodes that list them, each at the
    /// positions that hold one of those.</para>
    /// <para><b>Why the plan is the same.</b> A node the narrowed walk skips reaches no kept page, and
    /// neither does anything below it, so the whole walk answers <c>false</c> for it, keeps none of it,
    /// and learns nothing there about any node it keeps. What a skipped node can change is the walk's
    /// bookkeeping: a cycle or a path past <see cref="AcroFormPageFilter.MaxFieldDepth"/> among them
    /// is reported, and the budget spent on them can cut the walk short. So a tree has an index only
    /// when it has neither, and <c>Filter</c> narrows an extraction's walk only when the whole walk's
    /// spend — <see cref="FixedSpend"/> beyond the kept pages' annotations — fits its budget. A tree
    /// that fails either is walked whole each time, as before.</para>
    /// <para>Nodes are held by reference identity across extractions, which is sound because the
    /// parser's object cache never evicts: a later extraction resolves the same instances.</para>
    /// </remarks>
    internal sealed class AcroFormFieldIndex
    {
        private AcroFormFieldIndex(PdfDictionary form) { Form = form; }

        /// <summary>The form the index was read from.</summary>
        internal PdfDictionary Form { get; }

        /// <summary>
        /// The most a whole walk spends beyond the kept pages' annotations: one unit per
        /// <c>/Fields</c> entry, two per <c>/Kids</c> entry of every node, since each of the two
        /// passes reads a node's kids at most once, and one per annotation every page lists, which a
        /// walk without the index reads for itself.
        /// </summary>
        internal long FixedSpend { get; private set; }

        // Every annotation a page lists — by object number, or by identity for a direct one — with the
        // pages listing it, so an extraction learns which widgets some other page places without
        // reading every page's /Annots again.
        private readonly Dictionary<int, List<int>> _pagesListingNumber = new Dictionary<int, List<int>>();
        private readonly Dictionary<PdfDictionary, List<int>> _pagesListingDirect = new Dictionary<PdfDictionary, List<int>>();

        /// <summary>
        /// Whether a page outside <paramref name="keptPageObjNums"/> lists <paramref name="node"/>
        /// among its annotations.
        /// </summary>
        internal bool IsListedByAPageOutside(PdfDictionary node, HashSet<int> keptPageObjNums)
        {
            List<int> pages = null;
            if (node.ObjectNumber > 0) _pagesListingNumber.TryGetValue(node.ObjectNumber, out pages);
            if (pages == null) _pagesListingDirect.TryGetValue(node, out pages);
            if (pages == null) return false;
            foreach (int page in pages)
                if (!keptPageObjNums.Contains(page)) return true;
            return false;
        }

        // Reads every page's /Annots once, one unit of the budget an entry.
        private bool ReadAnnotationListings(PdfDocument doc, ref long spend, int nodeBudget)
        {
            IReadOnlyList<PdfPage> pages = doc.Pages;
            if (pages == null) return true;

            for (int p = 0; p < pages.Count; p++)
            {
                PdfPage page = pages[p];
                int pageNumber = page?.Dictionary?.ObjectNumber ?? 0;
                var annots = page?.GetAnnotations();
                if (pageNumber <= 0 || annots == null) continue;

                spend += annots.Count;
                if (spend > nodeBudget) return false;

                for (int i = 0; i < annots.Count; i++)
                {
                    List<int> listing;
                    if (annots[i] is PdfReference reference)
                    {
                        if (!_pagesListingNumber.TryGetValue(reference.ReferenceObjectNumber, out listing))
                            _pagesListingNumber[reference.ReferenceObjectNumber] = listing = new List<int>();
                    }
                    else if (doc.ResolveObject(annots[i]) is PdfDictionary direct)
                    {
                        if (!_pagesListingDirect.TryGetValue(direct, out listing))
                            _pagesListingDirect[direct] = listing = new List<int>();
                    }
                    else continue;
                    listing.Add(pageNumber);
                }
            }
            return true;
        }

        // Every field node, with every place it is listed: the node whose /Kids holds it (null for
        // /Fields) and the position there.
        private readonly Dictionary<PdfDictionary, List<(PdfDictionary Owner, int Position)>> _listedAt =
            new Dictionary<PdfDictionary, List<(PdfDictionary Owner, int Position)>>();

        private readonly Dictionary<int, PdfDictionary> _byObjectNumber = new Dictionary<int, PdfDictionary>();
        private readonly Dictionary<int, List<PdfDictionary>> _byPageReference = new Dictionary<int, List<PdfDictionary>>();

        /// <summary>The object numbers of every indirect field node.</summary>
        internal IEnumerable<int> NodeNumbers => _byObjectNumber.Keys;

        /// <summary>
        /// Reads the field tree of <paramref name="acroForm"/>, or answers <c>null</c> when a whole
        /// walk of it could stop short or report something: a <c>/Kids</c> cycle, a path longer than
        /// <see cref="AcroFormPageFilter.MaxFieldDepth"/>, more entries than
        /// <paramref name="nodeBudget"/> pays for, or two nodes under one object number.
        /// </summary>
        internal static AcroFormFieldIndex TryBuild(
            PdfDictionary acroForm, PdfDocument doc, int nodeBudget = AcroFormPageFilter.MaxFieldNodes)
        {
            if (acroForm == null || doc == null) return null;

            var index = new AcroFormFieldIndex(acroForm);
            long spend = 0;
            if (!index.ReadAnnotationListings(doc, ref spend, nodeBudget)) return null;
            if (!(doc.ResolveObject(acroForm[PdfName.Fields]) is PdfArray fields))
            {
                index.FixedSpend = spend;
                return index;
            }

            spend += fields.Count;
            if (spend > nodeBudget) return null;

            // Heights are the longest path down from each node; a node is complete once it has one.
            var heights = new Dictionary<PdfDictionary, int>();
            var inProgress = new HashSet<PdfDictionary>();
            for (int i = 0; i < fields.Count; i++)
            {
                if (!(doc.ResolveObject(fields[i]) is PdfDictionary node)) continue;
                if (!index.Enter(node, null, i, doc, heights, inProgress, 0, ref spend, nodeBudget) ||
                    heights[node] > AcroFormPageFilter.MaxFieldDepth)
                    return null;
            }

            index.FixedSpend = spend;
            return index;
        }

        // Lists `node` at (owner, position), and reads its kids the first time it is met. The depth
        // test bounds the recursion; the caller's height test catches a long path that runs through a
        // node already read, which the recursion does not descend again.
        private bool Enter(
            PdfDictionary node, PdfDictionary owner, int position, PdfDocument doc,
            Dictionary<PdfDictionary, int> heights, HashSet<PdfDictionary> inProgress, int depth,
            ref long spend, int nodeBudget)
        {
            if (!_listedAt.TryGetValue(node, out var listed))
            {
                _listedAt[node] = listed = new List<(PdfDictionary Owner, int Position)>();
                if (node.ObjectNumber > 0)
                {
                    if (_byObjectNumber.ContainsKey(node.ObjectNumber)) return false;
                    _byObjectNumber[node.ObjectNumber] = node;
                }
                if (node[PdfName.P] is PdfReference page)
                {
                    if (!_byPageReference.TryGetValue(page.ReferenceObjectNumber, out var onPage))
                        _byPageReference[page.ReferenceObjectNumber] = onPage = new List<PdfDictionary>();
                    onPage.Add(node);
                }
            }
            listed.Add((owner, position));

            if (heights.ContainsKey(node)) return true;
            if (depth > AcroFormPageFilter.MaxFieldDepth || !inProgress.Add(node)) return false;

            int height = 0;
            if (doc.ResolveObject(node[PdfName.Kids]) is PdfArray kids)
            {
                spend += 2L * kids.Count;
                if (spend > nodeBudget) return false;

                for (int i = 0; i < kids.Count; i++)
                {
                    if (!(doc.ResolveObject(kids[i]) is PdfDictionary kid)) continue;
                    if (!Enter(kid, node, i, doc, heights, inProgress, depth + 1, ref spend, nodeBudget))
                        return false;
                    height = Math.Max(height, heights[kid] + 1);
                }
            }

            inProgress.Remove(node);
            heights[node] = height;
            return true;
        }

        /// <summary>
        /// The positions one extraction's walk visits: those listing a node placed on a kept page,
        /// or a node that lists one.
        /// </summary>
        internal Reach ReachOf(AcroFormPageFilter.State state)
        {
            var reach = new Reach();
            var reached = new HashSet<PdfDictionary>();
            var pending = new Stack<PdfDictionary>();

            void Reached(PdfDictionary node)
            {
                if (reached.Add(node)) pending.Push(node);
            }

            // The placed nodes, by the three tests State.IsPlacedOnKeptPage applies.
            foreach (int number in state.PlacedObjNums)
                if (_byObjectNumber.TryGetValue(number, out var numbered)) Reached(numbered);
            foreach (var direct in state.PlacedDictionaries)
                if (_listedAt.ContainsKey(direct)) Reached(direct);
            foreach (int page in state.KeptPageObjNums)
                if (_byPageReference.TryGetValue(page, out var onPage))
                    foreach (var node in onPage)
                        if (!state.IsListedElsewhere(node)) Reached(node);   // the /P fallback, as State applies it

            // Then every node listing one, with the position of each listing.
            while (pending.Count > 0)
            {
                foreach (var (owner, position) in _listedAt[pending.Pop()])
                {
                    if (owner == null)
                    {
                        reach.TopLevelPositions.Add(position);
                        continue;
                    }
                    Reached(owner);
                    if (!reach.KidPositions.TryGetValue(owner, out var positions))
                        reach.KidPositions[owner] = positions = new List<int>();
                    positions.Add(position);
                }
            }

            reach.TopLevelPositions.Sort();
            foreach (var positions in reach.KidPositions.Values) positions.Sort();
            return reach;
        }

        /// <summary>Ascending positions, of <c>/Fields</c> and of each listing node's <c>/Kids</c>.</summary>
        internal sealed class Reach
        {
            internal readonly List<int> TopLevelPositions = new List<int>();
            internal readonly Dictionary<PdfDictionary, List<int>> KidPositions =
                new Dictionary<PdfDictionary, List<int>>();

            internal IReadOnlyList<int> TopLevel => TopLevelPositions;

            internal IReadOnlyList<int> KidsOf(PdfDictionary node) =>
                KidPositions.TryGetValue(node, out var positions) ? positions : (IReadOnlyList<int>)Array.Empty<int>();
        }
    }
}
