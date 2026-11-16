// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>
    /// Turns a map of <c>object number → revised object</c> into the flat, generation-carrying,
    /// <b>object-number-ordered</b> list an incremental-update writer consumes.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> Six copies of the same three lines had accumulated across the
    /// accessibility writers — five in <c>PdfStructureTreeEditor</c> and one in
    /// <c>PdfReadingOrderAnalyzer</c> — and they had already drifted: the analyzer's copy filtered
    /// <c>kvp.Key &gt; 0 &amp;&amp; kvp.Value != null</c> and the other five did not. That is the
    /// repository's signature defect shape, and at three lines apiece it is below what a
    /// line-window duplicate detector will report, which is why it survived a duplication round.</para>
    ///
    /// <para><b>The filter is kept, and it is the correct side of that drift.</b> Object 0 is the
    /// free-list head and a null value is not an object; writing either produces a malformed
    /// revision. <see cref="DocumentRevisionWriter"/>, which writes the clear-text path, has always
    /// skipped and reported both — so the plan-building paths that omitted the check were the ones
    /// disagreeing with the writer they feed. Filtering here can only drop an entry that would have
    /// produced a broken file.</para>
    ///
    /// <para><b>Why the input stays an ordinary <c>Dictionary</c>, and why this sorts
    /// unconditionally.</b> A <c>SortedDictionary</c> at each producer would make the ordering
    /// intrinsic and save this sort — but it would also invite this method to <i>trust</i> its input
    /// to be ordered, and the parameter type is <c>IReadOnlyDictionary</c>, which a plain
    /// <c>Dictionary</c> satisfies. A helper that skips sorting because its callers "always" pass a
    /// sorted map is one silent caller away from emitting a revision in hash order. Sorting here
    /// costs one pass and makes the guarantee unconditional; the producers keep O(1) inserts and
    /// stop having to remember the rule at all.</para>
    ///
    /// <para>Ordering is not cosmetic: it is what makes a revision's bytes reproducible for the same
    /// input, which a signature over those bytes depends on.</para>
    /// </remarks>
    public static class RevisionObjects
    {
        /// <summary>
        /// The revised objects, ordered by object number, each carrying the generation the document
        /// already gave it.
        /// </summary>
        /// <param name="modified">
        /// Object number → revised object. Entries with a non-positive number or a null value are
        /// skipped and reported; the rest keep their number, so the revision supersedes rather than
        /// duplicates.
        /// </param>
        /// <param name="index">
        /// Object index for the document being revised, supplying each existing generation. When
        /// null, generation 0 is used — which is right for a freshly allocated object and wrong for
        /// superseding a gen&gt;0 one, so callers that supersede pass it.
        /// </param>
        /// <param name="operation">
        /// Stable <c>"Type.Method"</c> identifier used when reporting a skipped entry, so a caller
        /// reading <see cref="DocumentEditDiagnostics"/> can tell which plan dropped it.
        /// </param>
        public static List<IndirectObject> From(
            IReadOnlyDictionary<int, PdfObject> modified,
            PdfObjectIndex index,
            string operation)
            => From(null, modified, index, operation);

        /// <summary>
        /// The same, for a plan that both <b>creates</b> and <b>revises</b> objects.
        /// </summary>
        /// <remarks>
        /// <para><b>A modified object wins a collision</b>, which is why
        /// <paramref name="created"/> is applied first — matching
        /// <see cref="DocumentRevisionWriter"/>, which writes the clear-text path these plans
        /// mirror: a revision of an existing object is never something a caller meant to shadow
        /// with a freshly allocated one.</para>
        ///
        /// <para><b>That rule is currently unobservable, and saying so is the point.</b> Measured by
        /// mutation: dropping <paramref name="created"/> reddens 7 tests and dropping
        /// <paramref name="modified"/> reddens 13, so both maps are firmly reached — but
        /// <i>reversing the order changes nothing</i>. The two are disjoint by construction, because
        /// a plan mints fresh object numbers for what it creates and reuses existing ones for what
        /// it revises. The order is set to match the writer so that it is already right if that ever
        /// stops being true; it is not evidence of a collision anyone has seen.</para>
        /// </remarks>
        /// <param name="created">
        /// Objects the plan mints, keyed by their new number. May be null.
        /// </param>
        /// <param name="modified">
        /// Existing objects the plan revises, keyed by object number. May be null. Wins over
        /// <paramref name="created"/> on the same number.
        /// </param>
        /// <param name="index">
        /// Object index for the document being revised, supplying each existing generation. When
        /// null, generation 0 is used — right for a freshly allocated object and wrong for
        /// superseding a gen&gt;0 one, so callers that supersede pass it.
        /// </param>
        /// <param name="operation">
        /// Stable <c>"Type.Method"</c> identifier used when reporting a skipped entry.
        /// </param>
        public static List<IndirectObject> From(
            IReadOnlyDictionary<int, PdfObject> created,
            IReadOnlyDictionary<int, PdfObject> modified,
            PdfObjectIndex index,
            string operation)
        {
            int capacity = (created?.Count ?? 0) + (modified?.Count ?? 0);
            if (capacity == 0) return new List<IndirectObject>();

            var ordered = new SortedList<int, PdfObject>(capacity);
            Add(ordered, created, operation);
            Add(ordered, modified, operation);

            var list = new List<IndirectObject>(ordered.Count);
            foreach (KeyValuePair<int, PdfObject> kvp in ordered)
                list.Add(new IndirectObject(kvp.Key, index?.GetGeneration(kvp.Key) ?? 0, kvp.Value));

            return list;
        }

        private static void Add(
            SortedList<int, PdfObject> ordered,
            IReadOnlyDictionary<int, PdfObject> source,
            string operation)
        {
            if (source == null) return;

            foreach (KeyValuePair<int, PdfObject> kvp in source)
            {
                if (kvp.Key <= 0)
                {
                    DocumentEditDiagnostics.Report(operation,
                        "object number " + kvp.Key.ToString(CultureInfo.InvariantCulture) +
                        " is not a valid indirect object number; it was left out of the revision");
                    continue;
                }

                if (kvp.Value == null)
                {
                    DocumentEditDiagnostics.Report(operation,
                        "object " + kvp.Key.ToString(CultureInfo.InvariantCulture) +
                        " has no value; it was left out of the revision");
                    continue;
                }

                ordered[kvp.Key] = kvp.Value;
            }
        }
    }
}
