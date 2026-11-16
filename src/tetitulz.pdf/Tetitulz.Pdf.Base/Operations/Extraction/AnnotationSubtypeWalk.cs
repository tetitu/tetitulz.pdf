// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Extraction
{
    /// <summary>Where an annotation walk stopped at the document ceiling, if it did.</summary>
    internal sealed class AnnotationWalkReport
    {
        private readonly System.Action<string> _onCut;

        /// <summary>A report read after the walk.</summary>
        internal AnnotationWalkReport() { }

        /// <summary>A report that also hands the cut's sentence to <paramref name="onCut"/> when the walk stops.</summary>
        internal AnnotationWalkReport(System.Action<string> onCut) { _onCut = onCut; }

        /// <summary>The page the walk stopped on, or <c>null</c> when it reached the end.</summary>
        internal int? CutAtPage { get; private set; }

        internal void Cut(int page)
        {
            CutAtPage = page;
            _onCut?.Invoke(Message);
        }

        /// <summary>The sentence a caller reports the cut with.</summary>
        internal string Message => CutAtPage is int page
            ? $"the pages name more than {PdfLimits.MaxAnnotationsPerDocument} annotation entries between them; " +
              $"those from page {page} on were not read"
            : null;
    }

    /// <summary>
    /// Every annotation in a document whose <c>/Subtype</c> is one the caller named, in page order.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> <see cref="Pdf3DExtractor"/> and
    /// <see cref="PdfRichMediaExtractor"/> each contained this walk twice — once to reach the
    /// <i>n</i>th matching annotation and once to list them all — four copies of the same nested
    /// loop, resolve, and subtype gate.</para>
    ///
    /// <para><b>One walk for <c>Pdf3DExtractor</c> and <c>PdfRichMediaExtractor</c>.</b> The subtype
    /// is resolved: <c>GetName</c> does not follow a reference, so <c>/Subtype 12 0 R</c> — legal
    /// under §7.3.10 — would read as absent and the loop walk past the annotation the caller asked
    /// for, ending in an <c>ArgumentOutOfRangeException</c> that says no such annotation exists.
    /// Twins resolve or fail separately; one walk cannot.</para>
    ///
    /// <para><b>Resolving is the point of the gate, not a detail.</b> Both the annotation reference
    /// and its <c>/Subtype</c> are followed, because <see cref="PdfDictionary.GetName"/> does not,
    /// and an unresolved value is indistinguishable from an absent one — which a subtype gate reads
    /// as "not the annotation you wanted".</para>
    ///
    /// <para>An iterator rather than a list: both call shapes want it lazily, since the indexed one
    /// stops at the <i>n</i>th match.</para>
    /// </remarks>
    internal static class AnnotationSubtypeWalk
    {
        /// <summary>
        /// The page index, the annotation dictionary, and its resolved <c>/Subtype</c> value — the
        /// last because a caller matching several subtypes needs to know which one it got.
        /// </summary>
        internal static IEnumerable<(int PageIndex, PdfDictionary Annotation, string Subtype)>
            BySubtype(PdfDocument doc, params string[] subtypes)
            => BySubtype(doc, null, subtypes);

        /// <summary><see cref="BySubtype(PdfDocument, string[])"/>, the document ceiling's cut recorded in <paramref name="report"/>.</summary>
        internal static IEnumerable<(int PageIndex, PdfDictionary Annotation, string Subtype)>
            BySubtype(PdfDocument doc, AnnotationWalkReport report, params string[] subtypes)
        {
            // The two clauses are not the same kind of thing, and a mutation of the pair says so:
            // for an EMPTY array the loop below iterates zero times and yields nothing anyway, so
            // that clause is a fast path. The NULL clause is the guard — without it `subtypes.Length`
            // throws. "No subtypes matches nothing" is therefore a property of the loop, which is
            // what makes it safe to state as a contract in All's remarks below.
            if (subtypes == null || subtypes.Length == 0) yield break;

            foreach (var entry in All(doc, report))
            {
                for (int s = 0; s < subtypes.Length; s++)
                    if (entry.Subtype == subtypes[s]) { yield return entry; break; }
            }
        }

        /// <summary>
        /// Every annotation in the document, in page order, with its resolved <c>/Subtype</c>.
        /// </summary>
        /// <param name="doc">The open document.</param>
        /// <param name="report">Where the document ceiling's cut is recorded; null for none.</param>
        /// <remarks>
        /// <b>"List everything" cannot be expressed through <see cref="BySubtype(PdfDocument, string[])"/>.</b> That overload
        /// <c>yield break</c>s on an empty filter — passing no subtypes means "match nothing", not
        /// "match all" — so an inventory needs this entry point rather than a clever argument. Both
        /// share one body, so the resolve rule this file exists for cannot apply to one and not the
        /// other.
        /// </remarks>
        internal static IEnumerable<(int PageIndex, PdfDictionary Annotation, string Subtype)>
            All(PdfDocument doc, AnnotationWalkReport report = null)
        {
            foreach (var (pageIdx, entry) in Entries(doc, report))
            {
                if (!(doc.ResolveObject(entry) is PdfDictionary annotDict)) continue;

                string sub = ResolvedSubtype(doc, annotDict);
                if (sub == null) continue;

                yield return (pageIdx, annotDict, sub);
            }
        }

        /// <summary>
        /// Every <c>/Annots</c> entry of every page, as written, in page order — bounded together by
        /// <see cref="PdfLimits.MaxAnnotationsPerDocument"/>, since pages may name one array: ten
        /// thousand pages naming one array of ten thousand entries is 10⁸ entries from one array.
        /// Every entry examined counts, whatever it turns out to be; the cut is recorded in
        /// <paramref name="report"/>.
        /// </summary>
        internal static IEnumerable<(int PageIndex, PdfObject Entry)> Entries(PdfDocument doc, AnnotationWalkReport report = null)
        {
            if (doc?.Pages == null) yield break;

            long left = PdfLimits.MaxAnnotationsPerDocument;
            for (int pageIdx = 0; pageIdx < doc.Pages.Count; pageIdx++)
            {
                var annots = doc.Pages[pageIdx].GetAnnotations();
                if (annots == null) continue;

                for (int ai = 0; ai < annots.Count; ai++)
                {
                    if (--left < 0)
                    {
                        report?.Cut(pageIdx);
                        yield break;
                    }
                    yield return (pageIdx, annots[ai]);
                }
            }
        }

        /// <summary>
        /// An annotation's <c>/Subtype</c>, following an indirect reference, or <c>null</c>.
        /// </summary>
        /// <param name="doc">The document, for resolving.</param>
        /// <param name="annot">The annotation dictionary.</param>
        /// <remarks>
        /// <b>Exposed so that callers outside this walk stop open-coding it.</b> The remarks above
        /// record the same defect being found in one twin and fixed there; it was then live for a
        /// third time in <c>PdfAnnotationEditor.RemoveBySubtype</c>, which read
        /// <c>annot[PdfName.Subtype] as PdfName</c> — so a producer sharing one name object across a
        /// thousand annotations (legal §7.3.10) had every one of them silently skipped by a removal
        /// that reported success. One implementation is the only thing that stops a fourth.
        /// </remarks>
        internal static string ResolvedSubtype(PdfDocument doc, PdfDictionary annot)
            => PdfResolve.Name(doc, annot, PdfName.Subtype)?.Value;
    }
}
