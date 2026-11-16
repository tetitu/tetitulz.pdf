// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Accessibility.Internal
{
    /// <summary>
    /// One page's copy of a shared Form XObject: the object number its <c>Do</c> will reach, and
    /// the invocation it was made for.
    /// </summary>
    internal sealed class FormCopy
    {
        /// <summary>Where this copy lives. The first copy reuses the original's number.</summary>
        internal int ObjectNumber;

        /// <summary>True when <see cref="ObjectNumber"/> is a number this pass minted.</summary>
        internal bool IsNewObject;

        internal FormInvocation Invocation;
    }

    /// <summary>What duplicating one form would take, worked out before anything is written.</summary>
    internal sealed class FormDuplicationPlan
    {
        internal bool Succeeded;

        /// <summary>Why the form cannot be duplicated, in the wording a caller will see.</summary>
        internal string SkipReason;

        /// <summary>The marked-up content, shared by every copy.</summary>
        internal PageContentRewriter.FormRewriteResult Tagged;

        /// <summary>One per page that will draw its own copy, in invocation order.</summary>
        internal readonly List<FormCopy> Copies = new List<FormCopy>();

        /// <summary>Decoded bytes this plan will charge to the budget once committed.</summary>
        /// <remarks>
        /// A <c>long</c>, and that is load-bearing rather than tidiness. The product is
        /// <c>Content.Length × (pages − 1)</c>, bounded only by
        /// <c>AutoTagOptions.MaxPageContentBytes</c> (4 MiB) and by the page count, so it overflows
        /// <c>int</c> once (pages − 1) ≥ 512 for a 4 MiB form. A negative product passed the
        /// "> remainingBudget" test, and <c>PdfAutoTagger</c>'s <c>duplicationBudget -=
        /// BudgetCost</c> then *raised* the remaining budget, so every later form was unbounded too:
        /// 600 pages × 4 MiB wrote 2.4 GB out of a 60 KB input while
        /// <see cref="SharedFormDuplicator.MaxDuplicatedFormBytes"/> never fired.
        /// </remarks>
        internal long BudgetCost;
    }

    /// <summary>
    /// Gives every page that draws a shared Form XObject its own copy, so each can carry its own
    /// <c>/StructParents</c> and its own structure (plan 08 item 49).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Form XObject has one stream dictionary and therefore one <c>/StructParents</c>. Without
    /// duplication a form drawn by more than one page would be left untagged — a letterhead, a stamp
    /// or a set of flattened fields keeping every page it appears on out of conformance, since those
    /// are exactly the things a document shares.
    /// </para>
    /// <para>
    /// <b>Duplication is all-or-nothing, and that is the safety property.</b> If the original stayed
    /// tagged for the first page while any other page still drew it, that page would render marked
    /// content whose owning element names the first page as its <c>/Pg</c>: a wrong tree that
    /// validates, which is the same failure class <c>/Stm</c> exists to prevent. So every copy is
    /// planned and every repoint proved possible before a single byte is written, and a form that
    /// cannot be fully duplicated is reported exactly as it was before.
    /// </para>
    /// <para>
    /// <b>The first page keeps the original object.</b> N pages therefore cost N−1 new objects, N−1
    /// repointed pages, and no edit at all to the first — and an incremental update cannot delete
    /// the original anyway, so reusing it is strictly cheaper than orphaning it.
    /// </para>
    /// </remarks>
    internal static class SharedFormDuplicator
    {
        private const string Operation = nameof(SharedFormDuplicator);

        /// <summary>
        /// Total <b>decoded</b> form bytes this pass will duplicate across one document.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Deliberately its own value rather than an alias of
        /// <see cref="FormXObjectInventory.MaxTotalScanBytes"/>: that one bounds a decode which
        /// happens once per form, while this bounds a quantity whose multiplier the <i>file</i>
        /// chooses. A 4 MiB form invoked from a thousand pages asks for a thousand copies of it in
        /// the output and a thousand sets of structure elements.
        /// </para>
        /// <para>
        /// Charged on <b>decoded</b> bytes rather than on the compressed payload, because the threat
        /// is a small hostile file: a form inflating from 3 KB of Flate to 4 MiB and drawn from
        /// every page costs an attacker nothing to ship, and compressed-byte accounting would let
        /// exactly that through.
        /// </para>
        /// <para>
        /// There is deliberately no separate cap on the number of copies. Copies cannot exceed
        /// pages, and this budget already trades the two off in the right direction — a large form
        /// gets few copies, a small one many.
        /// </para>
        /// </remarks>
        internal const int MaxDuplicatedFormBytes = 8 * 1024 * 1024;

        /// <summary>
        /// Serialised bytes charged for one entry of a <c>/Resources</c> (or <c>/XObject</c>)
        /// dictionary that <see cref="PageContentRewriter.TryRepointXObjectNames"/> copies inline
        /// onto a repointed page.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why this exists at all.</b> Repointing a page copies the page's <i>whole</i> effective
        /// <c>/Resources</c> — and its <c>/XObject</c> sub-dictionary — as a direct dictionary onto
        /// that page, unconditionally, for the reasons <c>TryRepointXObjectNames</c> documents.
        /// When resources are inherited from a <c>/Pages</c> node (the common layout) that
        /// document-wide dictionary is materialised once per repointed page, and that multiplier was
        /// entirely unbudgeted: <see cref="FormDuplicationPlan.BudgetCost"/> charged only
        /// <c>form.Content.Length × (copies − 1)</c>, which for a small shared form is a few tens of
        /// kilobytes however large the resources are. A 600-page file whose <c>/Pages</c> node
        /// carries a 10 MB <c>/XObject</c> dictionary and whose pages share one 100-byte form
        /// therefore charged 59 900 bytes and wrote roughly 6 GB.
        /// </para>
        /// <para>
        /// An estimate rather than a measurement, and deliberately so: the copy is shallow, so what
        /// each entry costs in the output is a name plus a reference — <c>/Fm000123 456 0 R</c> is
        /// about this — and counting entries is O(1) per dictionary where serialising to find out
        /// would cost more than the write it is trying to bound. The value only has to make the
        /// trade-off honest, not exact.
        /// </para>
        /// </remarks>
        internal const int EstimatedResourceEntryBytes = 16;

        /// <summary>
        /// Works out how <paramref name="form"/> would be duplicated. <b>Writes nothing</b>: the
        /// caller commits, and only ever commits a plan that succeeded.
        /// </summary>
        internal static FormDuplicationPlan Plan(
            PdfDocument doc,
            PdfObjectIndex index,
            FormUsage form,
            PageTagOptions tagOptions,
            bool compressRewrittenStream,
            long remainingBudget)
        {
            var plan = new FormDuplicationPlan();

            List<FormInvocation> targets = DuplicableInvocations(form, out plan.SkipReason);
            if (targets == null) return plan;

            // The first copy reuses the original object, so only the rest are new bytes. Computed
            // in long: see FormDuplicationPlan.BudgetCost for what the int product did.
            plan.BudgetCost = (long)(form.Content?.Length ?? 0) * (targets.Count - 1);
            if (plan.BudgetCost > remainingBudget)
            {
                plan.SkipReason = OverBudget(targets.Count);
                return plan;
            }

            // Every repoint must be known to work before any of them is performed — and every
            // repoint's inline /Resources copy must be paid for before any of them is written.
            for (int i = 1; i < targets.Count; i++)
            {
                FormInvocation invocation = targets[i];
                if (!PageContentRewriter.CanRepointXObjectNames(
                        doc, index, invocation.PageDictionary, invocation.PageResources,
                        invocation.DirectNames, out _, out PdfDictionary xobjects, out string why))
                {
                    plan.SkipReason =
                        "page " + (invocation.PageIndex + 1).ToString(CultureInfo.InvariantCulture) +
                        " could not be given its own copy: " + why;
                    return plan;
                }

                // The second, usually larger multiplier. Charged per page rather than once,
                // because the copy really is made per page.
                plan.BudgetCost += (long)EstimatedResourceEntryBytes *
                                   ((invocation.PageResources?.Count ?? 0) + (xobjects?.Count ?? 0));

                if (plan.BudgetCost > remainingBudget)
                {
                    plan.SkipReason = OverBudget(targets.Count);
                    return plan;
                }
            }

            // Tagged ONCE and shared. A form's classification does not depend on which page draws
            // it: BuildFormTagOptions switches off header/footer artifact detection, the only
            // page-space measurement, and everything left is document-level.
            plan.Tagged = PageContentRewriter.TagFormContent(
                form.Stream, form.Content, tagOptions, compressRewrittenStream);

            if (!plan.Tagged.Changed)
            {
                plan.SkipReason = plan.Tagged.SkipReason ?? "the tagger declined to modify it";
                return plan;
            }

            for (int i = 0; i < targets.Count; i++)
            {
                int objectNumber = i == 0 ? form.ObjectNumber : index.AllocateObjectNumber();
                if (objectNumber <= 0)
                {
                    plan.Copies.Clear();
                    plan.SkipReason = "no object number could be allocated for a copy of it";
                    return plan;
                }

                plan.Copies.Add(new FormCopy
                {
                    ObjectNumber = objectNumber,
                    IsNewObject  = i > 0,
                    Invocation   = targets[i],
                });
            }

            plan.Succeeded = true;
            return plan;
        }

        /// <summary>
        /// Commits <paramref name="plan"/>: one stream per copy, and one repointed page per copy
        /// after the first.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every repoint here was proved possible by <see cref="Plan"/>, so this cannot leave the
        /// document half-duplicated. A repoint that fails anyway is reported rather than swallowed,
        /// because at that point the guarantee has already been broken and silence would hide it.
        /// </para>
        /// <para>
        /// <b>And it is genuinely all-or-nothing, because the edits are staged.</b> Writing each
        /// copy straight into <paramref name="modifiedObjects"/> / <paramref name="newObjects"/> and
        /// returning <c>false</c> on the first repoint failure would leave the tagged original
        /// stamped <c>/StructParents k</c> in the edit set, page 2's copy in it, possibly page 2
        /// already repointed at that copy, and <c>nextParentTreeKey</c> not advanced, so key
        /// <c>k</c> would be handed out again to the next form: two streams claiming one
        /// parent-tree entry, and the abandoned form's spliced marked content resolving to another
        /// form's structure elements — a wrong tree that validates, which is the exact outcome this
        /// type's remarks say duplication exists to avoid.
        /// </para>
        /// <para>
        /// Staging also settles the key question without a second mechanism. A failed commit
        /// publishes no stream at all, so no output object was ever stamped with the keys it
        /// consumed and reissuing them is safe — which is why <c>TagForms</c> can keep leaving
        /// <c>nextParentTreeKey</c> where it was.
        /// </para>
        /// </remarks>
        internal static bool Commit(
            PdfDocument doc,
            PdfObjectIndex index,
            FormUsage form,
            FormDuplicationPlan plan,
            IReadOnlyList<int> parentTreeKeys,
            IDictionary<int, PdfObject> modifiedObjects,
            IDictionary<int, PdfObject> newObjects)
        {
            var stagedModified = new Dictionary<int, PdfObject>();
            var stagedNew      = new Dictionary<int, PdfObject>();

            // The repoint writes a revised PAGE into the modified set, and must be able to see an
            // earlier repoint of the same page (a page drawing two shared forms is repointed
            // twice). It therefore reads through a view that already holds this call's staged
            // edits on top of the caller's — but writes only into the staging copy.
            var visibleModified = new StagedEdits(modifiedObjects, stagedModified);

            for (int i = 0; i < plan.Copies.Count; i++)
            {
                FormCopy copy = plan.Copies[i];

                // The template is ALWAYS the original parsed form, never a stream emitted for an
                // earlier copy — otherwise each copy would inherit the previous page's
                // /StructParents and two pages would share one parent-tree entry.
                PdfStream stream = PageContentRewriter.EmitTaggedForm(
                    form.Stream, plan.Tagged, copy.ObjectNumber,
                    copy.IsNewObject ? 0 : index.GetGeneration(copy.ObjectNumber),
                    parentTreeKeys[i]);

                if (copy.IsNewObject) stagedNew[copy.ObjectNumber] = stream;
                else                  stagedModified[copy.ObjectNumber] = stream;

                if (!copy.IsNewObject) continue;

                if (!PageContentRewriter.TryRepointXObjectNames(
                        doc, index, copy.Invocation.PageDictionary, copy.Invocation.PageResources,
                        copy.Invocation.DirectNames, copy.ObjectNumber, visibleModified,
                        out string why))
                {
                    AccessibilityDiagnostics.Report(Operation,
                        "page " + (copy.Invocation.PageIndex + 1).ToString(CultureInfo.InvariantCulture) +
                        " could not be pointed at its copy of Form XObject " +
                        form.ObjectNumber.ToString(CultureInfo.InvariantCulture) + " after the plan " +
                        "had already established that it could: " + why +
                        ". Nothing was written: the staged copies were discarded.");
                    return false;
                }
            }

            foreach (KeyValuePair<int, PdfObject> entry in stagedModified)
                modifiedObjects[entry.Key] = entry.Value;
            foreach (KeyValuePair<int, PdfObject> entry in stagedNew)
                newObjects[entry.Key] = entry.Value;

            return true;
        }

        /// <summary>
        /// A write-through-to-staging, read-through-to-both view of the caller's modified-object
        /// set, so <see cref="Commit"/> can be atomic without giving up the read-your-own-writes
        /// behaviour <c>TryRepointXObjectNames</c> depends on.
        /// </summary>
        /// <remarks>
        /// Only the members <c>TryRepointXObjectNames</c> and <c>CloneForEdit</c> actually call are
        /// implemented; the rest throw rather than silently answering wrongly, because a partial
        /// dictionary that quietly returns "not found" is how a staged edit would get lost.
        /// </remarks>
        private sealed class StagedEdits : IDictionary<int, PdfObject>
        {
            private readonly IDictionary<int, PdfObject> _committed;
            private readonly IDictionary<int, PdfObject> _staged;

            internal StagedEdits(IDictionary<int, PdfObject> committed, IDictionary<int, PdfObject> staged)
            {
                _committed = committed;
                _staged    = staged;
            }

            public PdfObject this[int key]
            {
                get => _staged.TryGetValue(key, out PdfObject staged) ? staged : _committed[key];
                set => _staged[key] = value;
            }

            public bool TryGetValue(int key, out PdfObject value)
                => _staged.TryGetValue(key, out value) || _committed.TryGetValue(key, out value);

            public bool ContainsKey(int key) => _staged.ContainsKey(key) || _committed.ContainsKey(key);

            public void Add(int key, PdfObject value) => _staged[key] = value;

            public void Add(KeyValuePair<int, PdfObject> item) => _staged[item.Key] = item.Value;

            public int Count => throw new NotSupportedException();
            public bool IsReadOnly => false;
            public ICollection<int> Keys => throw new NotSupportedException();
            public ICollection<PdfObject> Values => throw new NotSupportedException();
            public bool Remove(int key) => throw new NotSupportedException();
            public bool Remove(KeyValuePair<int, PdfObject> item) => throw new NotSupportedException();
            public bool Contains(KeyValuePair<int, PdfObject> item) => throw new NotSupportedException();
            public void Clear() => throw new NotSupportedException();
            public void CopyTo(KeyValuePair<int, PdfObject>[] array, int index) => throw new NotSupportedException();
            public IEnumerator<KeyValuePair<int, PdfObject>> GetEnumerator() => throw new NotSupportedException();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
                => throw new NotSupportedException();
        }

        /// <summary>
        /// The one wording for a refusal on budget grounds, so the two places that can reach it
        /// (the form's own bytes, and the per-page <c>/Resources</c> copies) say the same thing.
        /// The reason names the budget rather than which half of the sum crossed it, because the
        /// caller's remedy — fewer or smaller shared forms — is the same either way.
        /// </summary>
        private static string OverBudget(int pageCount)
            => "duplicating it for " + pageCount.ToString(CultureInfo.InvariantCulture) +
               " pages would exceed the " +
               MaxDuplicatedFormBytes.ToString(CultureInfo.InvariantCulture) +
               "-byte document budget for duplicated form content";

        /// <summary>
        /// The pages that may each be given a copy, or null with the reason none may.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The test is per <b>invocation</b>, not per form. A form reached directly from one page
        /// and from inside another form on a second page is duplicable for neither: the second
        /// page's copy would have to be substituted in the parent form's resources, which every page
        /// drawing that parent shares.
        /// </para>
        /// <para>
        /// A page the tagger <b>skipped</b> counts here — leaving it out is what once let a shared
        /// form be mistaken for a private one — but it can never receive a copy, because it has no
        /// page-level <c>/StructParents</c> for the copy's content to belong to. So a form any of
        /// whose pages was skipped is left alone entirely.
        /// </para>
        /// </remarks>
        private static List<FormInvocation> DuplicableInvocations(FormUsage form, out string skipReason)
        {
            skipReason = null;

            var targets = new List<FormInvocation>(form.Invocations.Count);

            for (int i = 0; i < form.Invocations.Count; i++)
            {
                FormInvocation invocation = form.Invocations[i];

                if (invocation.ReachedThroughAnotherForm)
                {
                    skipReason =
                        "page " + (invocation.PageIndex + 1).ToString(CultureInfo.InvariantCulture) +
                        " reaches it from inside another form, and substituting a copy there would " +
                        "change every page drawing that form";
                    return null;
                }

                if (!invocation.PageWasReMarked)
                {
                    skipReason =
                        "page " + (invocation.PageIndex + 1).ToString(CultureInfo.InvariantCulture) +
                        " draws it but was itself skipped, so it has no /StructParents for a copy's " +
                        "content to belong to";
                    return null;
                }

                if (!invocation.DirectFromPage || invocation.DirectNames.Count == 0)
                {
                    skipReason =
                        "page " + (invocation.PageIndex + 1).ToString(CultureInfo.InvariantCulture) +
                        " draws it under no resource name this pass could identify";
                    return null;
                }

                targets.Add(invocation);
            }

            if (targets.Count == 0)
            {
                skipReason = "no page was found drawing it";
                return null;
            }

            return targets;
        }
    }
}
