// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Operations;

namespace Tetitulz.Pdf.Accessibility.Internal
{
    /// <summary>
    /// One page's use of one Form XObject: every fact about <i>that pairing</i> which decides
    /// whether the page may be given a private copy of the form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The unit is the invocation, not the form.</b> A form reached directly from page 1 and
    /// through another form from page 2 is duplicable for neither: page 2's copy would have to be
    /// substituted inside the parent form, whose resources every page drawing that parent shares.
    /// A per-form flag would answer "mostly direct" and get page 2 wrong.
    /// </para>
    /// </remarks>
    internal sealed class FormInvocation
    {
        internal int PageIndex;
        internal int PageObjectNumber;

        /// <summary>The page dictionary as parsed. Never modified; cloned before any edit.</summary>
        internal PdfDictionary PageDictionary;

        /// <summary>The page's effective resources — its own, or the dictionary it inherits.</summary>
        internal PdfDictionary PageResources;

        /// <summary>
        /// <b>Every</b> name in this page's own <c>/XObject</c> that reaches the form, not just the
        /// first. One page routinely names one object twice (<c>/A 8 0 R /B 8 0 R</c>, content doing
        /// <c>/A Do /B Do</c>); repointing one and leaving the other would have the page drawing the
        /// copy and the original at once.
        /// </summary>
        internal readonly List<string> DirectNames = new List<string>();

        /// <summary>
        /// Membership index for <see cref="DirectNames"/>, so adding the Nth distinct name costs
        /// O(1) rather than an O(N) ordinal scan of the list.
        /// </summary>
        /// <remarks>
        /// N is the number of distinct <c>/XObject</c> names a page's content invokes, bounded only
        /// by the scanner's four-million-token ceiling — so one page invoking 100 000 distinct names
        /// that all resolve to one form spent 5×10^9 string comparisons building a list of 100 000.
        /// The list itself is kept because order matters to the repointer and because it is what
        /// <c>CanRepointXObjectNames</c> enumerates.
        /// </remarks>
        private readonly HashSet<string> _directNameSet = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Records <paramref name="name"/> as a direct route to the form, once.</summary>
        internal void AddDirectName(string name)
        {
            if (_directNameSet.Add(name)) DirectNames.Add(name);
        }

        /// <summary>True when this page reaches the form through its own resources (depth 0).</summary>
        internal bool DirectFromPage;

        /// <summary>True when this page also reaches the form from inside another form.</summary>
        internal bool ReachedThroughAnotherForm;

        /// <summary>False when the tagger declined this page; see <see cref="PageInvocations.PageWasReMarked"/>.</summary>
        internal bool PageWasReMarked;

        /// <summary>True when this page draws the form inside one of its existing <c>/Artifact</c> sequences (through a parent form drawn there too).</summary>
        internal bool InsideArtifact;

        /// <summary>True when this page draws the form inside another existing sequence, whose owner its content then belongs to.</summary>
        internal bool InsideSequence;

        /// <summary>True when this page draws the form outside every existing sequence.</summary>
        internal bool OutsideSequence;

        // There is deliberately no `IsDuplicable` property here. One existed, unused, computing
        // exactly what SharedFormDuplicator.DuplicableInvocations decides — and that method is the
        // right home for it, because a refusal has to carry a *reason* naming the page and the
        // obstacle, which a bool cannot. A second, silent way to answer the same question is how
        // the two ended up able to disagree; keep the one that can explain itself.
    }

    /// <summary>
    /// One Form XObject reachable from the pages, and whether the tagger may rewrite it.
    /// </summary>
    internal sealed class FormUsage
    {
        internal int ObjectNumber;
        internal PdfStream Stream;

        /// <summary>Resource name it was first seen under, for diagnostics only.</summary>
        internal string ResourceName;

        /// <summary>One entry per page that reaches this form, directly or through another form.</summary>
        internal readonly List<FormInvocation> Invocations = new List<FormInvocation>();

        /// <summary>
        /// Distinct page indexes that reach this form, in the order first seen.
        /// </summary>
        /// <remarks>
        /// Derived from <see cref="Invocations"/> rather than tracked alongside it, so the two can
        /// never disagree about how many pages draw the form — which is the question that decides
        /// whether it may be rewritten in place at all.
        /// </remarks>
        internal List<int> InvokingPageIndexes
        {
            get
            {
                var pages = new List<int>(Invocations.Count);
                for (int i = 0; i < Invocations.Count; i++) pages.Add(Invocations[i].PageIndex);
                return pages;
            }
        }

        /// <summary>Object number of the single invoking page; valid only when there is one.</summary>
        internal int PageObjectNumber = -1;

        /// <summary>The resource dictionary its own content resolves names against.</summary>
        internal PdfDictionary Resources;

        /// <summary>True when the form's content contains at least one text object.</summary>
        internal bool HasText;

        /// <summary>
        /// True when the form was refused <b>before</b> its text objects could be counted, so
        /// <see cref="HasText"/> means "not established" rather than "none".
        /// </summary>
        /// <remarks>
        /// The distinction decides whether the tagger reports the form. "It has no content" is a
        /// fact and must stay silent — a channel that fires on conformant input is one callers learn
        /// to ignore. "Its stream would not decode", "it is past the tagging ceiling", "the
        /// document-wide scan budget ran out" and "it would not tokenise safely" are all *unknowns*,
        /// and a form that may be full of untagged text has to be named.
        /// </remarks>
        internal bool TextUndetermined;

        /// <summary>Non-null when this form must not be tagged, and why.</summary>
        internal string SkipReason;

        /// <summary>
        /// True when every page draws the form inside an <c>/Artifact</c> sequence and nowhere else:
        /// artifact content (§14.8.2.2), neither tagged nor reported as untagged.
        /// </summary>
        internal bool ArtifactOnly;

        /// <summary>Decoded content, cached by the walk so the tagger need not decode twice.</summary>
        internal byte[] Content;

        /// <summary>
        /// The XObject names this form's own content invokes, tokenised <b>once</b>.
        /// </summary>
        /// <remarks>
        /// It is a pure function of <see cref="Content"/>, and re-deriving it would be the walk's
        /// dominant cost: a full <c>ContentStreamScanner.ScanDetailed</c> over the decoded bytes on
        /// <i>every</i> encounter, and a form is encountered once per resource name that reaches it
        /// and once per path through the form graph. One page whose <c>/XObject</c> holds 100 000
        /// distinct names all pointing at one 3 MiB form would tokenise 3 MiB a hundred thousand
        /// times, and <c>MaxTotalScanBytes</c> would see none of it —
        /// it charges the one-time decode in <see cref="FormXObjectInventory"/>'s <c>Measure</c>,
        /// not the re-scans.
        /// </remarks>
        internal IReadOnlyList<string> InvokedNames;

        internal bool IsCandidate => SkipReason == null && HasText && Stream != null;

        /// <summary>The invocation recorded for <paramref name="pageIndex"/>, or null.</summary>
        internal FormInvocation FindInvocation(int pageIndex)
        {
            for (int i = 0; i < Invocations.Count; i++)
                if (Invocations[i].PageIndex == pageIndex) return Invocations[i];
            return null;
        }
    }

    /// <summary>One page's <c>Do</c> invocations, as already computed while tagging that page.</summary>
    internal sealed class PageInvocations
    {
        internal int PageIndex;
        internal int PageObjectNumber;

        /// <summary>
        /// The page's <b>effective</b> resources — its own <c>/Resources</c> when it has one, else
        /// the dictionary it inherits from a <c>/Pages</c> ancestor. Reading the own key alone left
        /// every inheriting page's forms uninventoried.
        /// </summary>
        internal PdfDictionary Resources;

        internal IReadOnlyList<string> Names;

        /// <summary>Of <see cref="Names"/>, those a <c>Do</c> inside an existing <c>/Artifact</c> sequence passes.</summary>
        internal IReadOnlyCollection<string> NamesInsideArtifacts = Array.Empty<string>();

        /// <summary>Of <see cref="Names"/>, those a <c>Do</c> inside another existing sequence passes — every name when the page's marked content could not be read.</summary>
        internal IReadOnlyCollection<string> NamesInsideSequences = Array.Empty<string>();

        /// <summary>Of <see cref="Names"/>, those a <c>Do</c> outside every existing sequence passes.</summary>
        internal IReadOnlyCollection<string> NamesOutsideSequences = Array.Empty<string>();

        /// <summary>
        /// The page dictionary as parsed, so a form's copy can be attached to this page's own
        /// resources without re-resolving it.
        /// </summary>
        internal PdfDictionary PageDictionary;

        /// <summary>
        /// False when the tagger declined this page — it already carried marked content, its stream
        /// would not decode, or it exceeded a ceiling.
        ///
        /// <para>
        /// Such a page still <b>counts</b> as drawing the forms it invokes, because that is what
        /// keeps a shared form from being mistaken for a private one. It must never be given a copy
        /// of its own, though: it has no page-level <c>/StructParents</c>, so structural marked
        /// content inside a form it draws would manufacture a UA18 finding.
        /// </para>
        /// </summary>
        internal bool PageWasReMarked;
    }

    /// <summary>Forms found, plus the invocations that could not be followed.</summary>
    internal sealed class FormInventory
    {
        internal readonly List<FormUsage> Forms = new List<FormUsage>();

        /// <summary>
        /// Page index → why an invocation on it could not be resolved. These are *possible*
        /// untagged text, and must be reported: a name that resolves to nothing may have been a
        /// form full of text, and silence would read as "there was none".
        /// </summary>
        internal readonly List<KeyValuePair<int, string>> Unreadable =
            new List<KeyValuePair<int, string>>();
    }

    /// <summary>
    /// One (Form XObject, effective resource dictionary) pair whose subtree has been walked for the
    /// page currently being inventoried.
    /// </summary>
    /// <remarks>
    /// The resource dictionary belongs in the key because a form with no <c>/Resources</c> of its
    /// own resolves its names against the <i>invoking</i> stream's, so the same object really does
    /// have two different subtrees when two parents lend it two different dictionaries. Compared by
    /// reference, which is what the walk is handed and what the rest of this file's identity sets
    /// use.
    /// </remarks>
    internal readonly struct ExpansionKey : IEquatable<ExpansionKey>
    {
        private readonly int _objectNumber;
        private readonly PdfDictionary _resources;

        internal ExpansionKey(int objectNumber, PdfDictionary resources)
        {
            _objectNumber = objectNumber;
            _resources    = resources;
        }

        public bool Equals(ExpansionKey other)
            => _objectNumber == other._objectNumber
               && ReferenceEquals(_resources, other._resources);

        public override bool Equals(object obj) => obj is ExpansionKey other && Equals(other);

        public override int GetHashCode()
            => unchecked(_objectNumber * 397 ^
                         (_resources == null ? 0 : System.Runtime.CompilerServices
                             .RuntimeHelpers.GetHashCode(_resources)));
    }

    internal static class FormXObjectInventory
    {
        /// <summary>
        /// Nesting cap for forms invoking forms. It matches the deep checker's own
        /// <c>MaxFormXObjectDepth</c> (12) rather than <c>PdfLimits.MaxFormXObjectDepth</c> (32):
        /// the two answer different questions, and a walk that disagreed with the checker about how
        /// deep a document goes would inventory forms the audit never looked at. Exceeding it means
        /// a hostile or broken file, and the answer is "record it as unreadable", never a crash.
        ///
        /// <para>It is stated literally rather than referencing the deep
        /// checker's <c>private const</c>, which would have to be promoted for no other reason.</para>
        /// </summary>
        internal const int MaxDepth = 12;

        /// <summary>Total decoded form bytes this pass will read for the whole document.</summary>
        internal const int MaxTotalScanBytes = 32 * 1024 * 1024;

        private const string Operation = nameof(FormXObjectInventory);

        /// <summary>
        /// Why a form drawn by several pages cannot simply be rewritten where it lies.
        /// </summary>
        /// <remarks>
        /// Kept here, beside the walk that establishes the fact, rather than in the tagger that
        /// reports it: the constraint is a property of the file format, not of this pass. It is the
        /// reason the duplicator exists, and the message a caller sees whenever duplication cannot
        /// rescue a particular form.
        /// </remarks>
        internal static string SharedFormSkipReason(FormUsage usage) =>
            "it is invoked from " +
            usage.Invocations.Count.ToString(CultureInfo.InvariantCulture) +
            " pages, and a Form XObject has one /StructParents, so its marked content " +
            "could belong to only one of them";

    /// <summary>
    /// Builds the document-wide picture the form tagger needs <b>before</b> it rewrites anything.
    /// </summary>
    /// <remarks>
    /// <b>It never decodes a page.</b> The invoked names arrive from the per-page tagging pass,
    /// which already applied <c>MaxPageContentBytes</c> and the declared-length guard. An earlier
    /// draft re-read every page here and reintroduced exactly the decompression-bomb inflation
    /// those ceilings exist to prevent — caught by
    /// <c>PublicCeilingOptionsTests.AutoTag_CompressedBombPage_IsRefusedWithoutInflatingIt</c>.
    /// </remarks>
    /// <remarks>
    /// <para><b>Why this cannot be done per page.</b> A Form XObject has one stream dictionary and
    /// therefore one <c>/StructParents</c>, so its marked content can belong to exactly one
    /// parent-tree entry. A form invoked from three pages cannot carry three different structures.
    /// "How many pages invoke this form" is a document-level question, and it decides whether the
    /// form may be tagged at all — so it has to be answered before the first page is rewritten.
    /// See GAPS.md §5c.</para>
    /// <para>Forms are keyed by <b>object number</b>, never by resource name: the same object is
    /// routinely reachable under different names from different pages, and tagging it twice would
    /// splice marked content into the same stream twice.</para>
    /// </remarks>
        internal static FormInventory Collect(
            PdfDocument doc, PdfObjectIndex index,
            IReadOnlyList<PageInvocations> pageInvocations, int maxFormContentBytes)
        {
            var result = new FormInventory();
            var byObjectNumber = new Dictionary<int, FormUsage>();
            if (doc == null || pageInvocations == null) return result;

            int budget = MaxTotalScanBytes;

            for (int i = 0; i < pageInvocations.Count; i++)
            {
                PageInvocations page = pageInvocations[i];
                if (page.Names == null || page.Names.Count == 0) continue;

                // `expanded` is per PAGE, deliberately, and not per document. The walk records one
                // FormInvocation per (form, page), so a document-wide set would silently drop the
                // nested forms of every page after the first that reached them. Per page it is
                // exact: a second encounter of the same (form, resources) pair on the same page can
                // only re-derive invocations that pair already produced. The key includes the
                // resource dictionary because a form with no /Resources of its own inherits the
                // invoking stream's, so the same object genuinely walks differently under two
                // parents.
                Walk(doc, index, page.Names, page.Resources, page,
                     byObjectNumber, new HashSet<int>(), new HashSet<ExpansionKey>(), 0,
                     ref budget, maxFormContentBytes, result,
                     insideArtifact: false, insideSequence: false, outsideSequence: false);
            }

            // A form reached from more than one page cannot carry per-page structure: one stream
            // dictionary holds one /StructParents. The verdict itself is NOT taken here — the
            // duplicator may be able to give each page its own copy — so this pass records only
            // the fact, and leaves SkipReason for the cases no copy can rescue.
            foreach (FormUsage usage in byObjectNumber.Values)
            {
                if (usage.SkipReason != null) continue;

                if (usage.Invocations.Count == 1)
                    usage.PageObjectNumber = usage.Invocations[0].PageObjectNumber;

                // Where the pages draw it decides whose content it is. Inside another sequence it
                // belongs to that sequence's owner (§14.7.5.1); inside an artifact it is artifact
                // content (§14.8.2.2) — silently so when it is drawn nowhere else, and said when it is
                // also drawn as content, since marking it would put marked content inside the artifact.
                bool insideSequence = false, insideArtifact = false, outside = false;
                for (int i = 0; i < usage.Invocations.Count; i++)
                {
                    insideSequence |= usage.Invocations[i].InsideSequence;
                    insideArtifact |= usage.Invocations[i].InsideArtifact;
                    outside        |= usage.Invocations[i].OutsideSequence;
                }
                if (insideSequence)
                    usage.SkipReason = "it is drawn inside a marked-content sequence the page already carries, " +
                                       "so its content belongs to that sequence's owner";
                else if (insideArtifact && outside)
                    usage.SkipReason = "it is drawn inside an /Artifact sequence on one page and as content elsewhere, " +
                                       "and marked content inside an artifact is not permitted";
                else if (insideArtifact)
                    usage.ArtifactOnly = true;
            }

            foreach (FormUsage usage in byObjectNumber.Values) result.Forms.Add(usage);
            return result;
        }

        // insideArtifact, insideSequence and outsideSequence say where the form these names are drawn
        // from is itself drawn on this page; at the page level (depth 0) each name decides for itself
        // from the page's own sets.
        private static void Walk(
            PdfDocument doc, PdfObjectIndex index, IReadOnlyList<string> names,
            PdfDictionary resources, PageInvocations page,
            Dictionary<int, FormUsage> byObjectNumber, HashSet<int> onThisPath,
            HashSet<ExpansionKey> expanded,
            int depth, ref int budget, int maxFormContentBytes, FormInventory result,
            bool insideArtifact, bool insideSequence, bool outsideSequence)
        {
            int pageIndex = page.PageIndex;

            if (names == null || names.Count == 0) return;
            if (depth > MaxDepth)
            {
                result.Unreadable.Add(new KeyValuePair<int, string>(pageIndex,
                    "Form XObject nesting exceeded " + MaxDepth.ToString(CultureInfo.InvariantCulture) +
                    " levels, so the deeper content could not be checked"));
                return;
            }

            PdfDictionary xobjects = Resolve<PdfDictionary>(doc, resources?[PdfName.XObject]);
            if (xobjects == null)
            {
                result.Unreadable.Add(new KeyValuePair<int, string>(pageIndex,
                    "an XObject was invoked but no /XObject resource dictionary resolves"));
                return;
            }

            for (int i = 0; i < names.Count; i++)
            {
                var form = Resolve<PdfStream>(doc, xobjects[new PdfName(names[i])]);
                if (form == null)
                {
                    // An invocation naming nothing readable. Reported, never assumed harmless:
                    // it may have been a form full of text.
                    result.Unreadable.Add(new KeyValuePair<int, string>(pageIndex,
                        "the invoked XObject /" + names[i] + " does not resolve to a stream"));
                    continue;
                }
                if (!string.Equals((Resolve<PdfObject>(doc, form[PdfName.Subtype]) as PdfName)?.Value,
                                   "Form", StringComparison.Ordinal))
                    continue;   // an image, or a PostScript XObject: no text to tag

                int objectNumber = index?.GetObjectNumber(form) ?? -1;
                if (objectNumber <= 0) continue;                 // direct stream; nothing to reference
                if (!onThisPath.Add(objectNumber)) continue;     // a form that draws itself

                if (!byObjectNumber.TryGetValue(objectNumber, out FormUsage usage))
                {
                    usage = new FormUsage
                    {
                        ObjectNumber = objectNumber,
                        Stream       = form,
                        ResourceName = names[i],
                    };
                    byObjectNumber[objectNumber] = usage;
                    Measure(doc, usage, ref budget, maxFormContentBytes);
                }

                FormInvocation invocation = usage.FindInvocation(pageIndex);
                if (invocation == null)
                {
                    invocation = new FormInvocation
                    {
                        PageIndex        = pageIndex,
                        PageObjectNumber = page.PageObjectNumber,
                        PageDictionary   = page.PageDictionary,
                        PageResources    = page.Resources,
                        PageWasReMarked  = page.PageWasReMarked,
                    };
                    usage.Invocations.Add(invocation);
                }

                // Where this drawing stands: at the page level each name's own sequence, below it the
                // parent form's — a form drawn by a form drawn inside an artifact is drawn inside it.
                bool artifactHere = depth == 0 ? page.NamesInsideArtifacts.Contains(names[i]) : insideArtifact;
                bool sequenceHere = depth == 0 ? page.NamesInsideSequences.Contains(names[i]) : insideSequence;
                bool outsideHere  = depth == 0 ? page.NamesOutsideSequences.Contains(names[i]) : outsideSequence;
                invocation.InsideArtifact  |= artifactHere;
                invocation.InsideSequence  |= sequenceHere;
                invocation.OutsideSequence |= outsideHere;

                if (depth == 0)
                {
                    invocation.DirectFromPage = true;

                    // Every name, not just the first: one page may reach one form under several,
                    // and a copy substituted at one of them leaves the others drawing the original.
                    invocation.AddDirectName(names[i]);
                }
                else
                {
                    invocation.ReachedThroughAnotherForm = true;
                }

                // A form with no /Resources inherits the invoking stream's.
                PdfDictionary formResources = usage.Resources ?? resources;

                // The subtree under this (form, resources) pair is walked once per page. Without
                // this, `onThisPath.Remove` at the end of the iteration meant the guard stopped only
                // self-reference: two names on one form doubled the work at every level, so 13 forms
                // chained two-ways bypassed the depth cap multiplicatively (2^12 leaf walks), and
                // 100 000 names on one form re-walked it 100 000 times. Everything the second
                // expansion would record — the nested forms' invocations for THIS page, all with
                // ReachedThroughAnotherForm set — the first one already recorded.
                if (usage.InvokedNames != null && expanded.Add(new ExpansionKey(objectNumber, formResources)))
                    Walk(doc, index, usage.InvokedNames, formResources, page,
                         byObjectNumber, onThisPath, expanded, depth + 1,
                         ref budget, maxFormContentBytes, result,
                         artifactHere, sequenceHere, outsideHere);

                onThisPath.Remove(objectNumber);
            }
        }

        /// <summary>Decodes the form once, recording why it cannot be tagged if it cannot.</summary>
        private static void Measure(PdfDocument doc, FormUsage usage, ref int budget, int maxFormContentBytes)
        {
            usage.Resources = Resolve<PdfDictionary>(doc, usage.Stream[PdfName.Resources]);

            // Decoded under the smaller ceiling, stopped there: a form that passes it costs what the budget
            // pays for, and one measured only after a whole decode would cost its 512 MB ceiling, each, for
            // the few hundred bytes a double deflate takes.
            bool budgetDecides = budget < maxFormContentBytes;
            if (!usage.Stream.TryGetDecodedData(Math.Min(maxFormContentBytes, budget), out byte[] content, out string refused))
            {
                AccessibilityDiagnostics.Report(Operation,
                    "Form XObject " + usage.ObjectNumber.ToString(CultureInfo.InvariantCulture) +
                    " was not decoded: " + refused);
                // What it cost is the decoder's to know, and up to the ceiling: half of what is left is
                // spent, so the refused decodes together cost no more than twice the budget.
                budget -= budget / 2;
                usage.SkipReason = budgetDecides
                    ? "the document-wide form-scanning budget was exhausted before it was reached"
                    : "its content exceeds the " + maxFormContentBytes.ToString(CultureInfo.InvariantCulture) +
                      "-byte tagging limit, or would not decode";
                usage.TextUndetermined = true;
                return;
            }

            if (content == null || content.Length == 0) { usage.SkipReason = "it has no content"; return; }

            // An unfiltered stream, or one already decoded, is handed over whatever the ceiling.
            if (content.Length > maxFormContentBytes)
            {
                usage.SkipReason = "its content exceeds the " +
                    maxFormContentBytes.ToString(CultureInfo.InvariantCulture) + "-byte tagging limit";
                usage.TextUndetermined = true;
                return;
            }
            if (content.Length > budget)
            {
                usage.SkipReason = "the document-wide form-scanning budget was exhausted before it was reached";
                usage.TextUndetermined = true;
                return;
            }
            budget -= content.Length;
            usage.Content = content;

            ContentScanResult scan = ContentStreamScanner.ScanDetailed(content);
            if (!scan.IsSafe)
            {
                usage.SkipReason = "its content could not be scanned safely: " + scan.Error;
                usage.TextUndetermined = true;
                return;
            }

            // Derived here, once, so the walk never re-tokenises this stream. See
            // FormUsage.InvokedNames.
            usage.InvokedNames = scan.InvokedXObjectNames();

            // Whether the form holds text is now KNOWN, so it is recorded before the refusal below.
            // Leaving HasText false on a refusal path made TagForms' `!form.HasText` fast path
            // swallow the form entirely — a form full of untagged text produced no warning, no
            // PagesPartiallyTagged increment and a report that read as clean.
            usage.HasText = scan.TextObjectCount > 0;

            if (scan.HasMarkedContent) { usage.SkipReason = "it already carries marked content"; return; }
        }


        private static T Resolve<T>(PdfDocument doc, PdfObject obj) where T : PdfObject
            => obj == null ? null : ((doc?.ResolveObject(obj) as T) ?? obj as T);
    }
}
