// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>
    /// §12.3.2.2 explicit destination array → zero-based page index. The single implementation,
    /// shared by every <c>Operations</c> reader that follows a destination: link annotations
    /// (<see cref="Extraction.PdfLinkExtractor"/>), outline items
    /// (<see cref="Structure.PdfBookmarksManager"/>) and the named-destination tree
    /// (<see cref="Structure.PdfNamedDestinations"/>) — and, through <see cref="IndexOfPage"/>, a
    /// document part's page range (<see cref="Metadata.PdfDPartReader"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>Why it is a type rather than three private helpers.</b> Three callers ask it; copies
    /// held together by a comment ("matching <c>PdfLinkExtractor.PageIndexFromDestArray</c>") are
    /// not held together. Prose asserting that two implementations agree is not the agreement.</para>
    ///
    /// <para><b>The three copies did agree on every well-formed file, with one exception, and it is
    /// the reason this merge is not pure hygiene.</b> Only the named-destination copy required the
    /// referenced object number to be <b>positive</b>. §7.3.10 makes object 0 permanently the head
    /// of the cross-reference free list, so <c>0 0 R</c> designates no object at all — but a page
    /// dictionary written <i>directly</i> inside <c>/Kids</c> (malformed, and tolerated by
    /// <c>PdfPageTree</c>) is a <see cref="PdfObject"/> whose <c>ObjectNumber</c> is the default
    /// <c>0</c>. So for a file carrying both, the link and outline copies matched that page and
    /// reported a confident, wrong page index where the destination pointed nowhere. The strict
    /// clause is the one kept.</para>
    ///
    /// <para><b>The first element is deliberately not resolved.</b> It <i>is</i> the page reference;
    /// following it would yield the page dictionary and discard the object number this lookup is
    /// built on. All three copies agreed about that and it is not an oversight.</para>
    ///
    /// <para>A leaf by construction: no static fields, hence no type initializer — the property that
    /// makes it safe for callers in different folders to share (see the <c>SvgSyntax</c> note in
    /// CLAUDE.md for what a type-initialization edge cost this repo once).</para>
    /// </remarks>
    internal static class PdfDestinationPage
    {
        /// <summary>Returned when the destination names no page in this document.</summary>
        internal const int NotFound = -1;

        /// <summary>
        /// The zero-based index of the page an explicit destination array designates, or
        /// <see cref="NotFound"/> when the array is empty, its first element is neither a page
        /// reference nor a number, the reference matches no page, or the number is out of range.
        /// </summary>
        /// <param name="doc">The document whose page list the destination is resolved against.</param>
        /// <param name="dest">
        /// The destination array — <c>[page /XYZ left top zoom]</c> and the other §12.3.2.2 forms.
        /// Only the first element is read.
        /// </param>
        internal static int IndexFromDestArray(PdfDocument doc, PdfArray dest)
        {
            if (doc == null || dest == null || dest.Count == 0) return NotFound;
            return IndexOfPage(doc, dest[0]);
        }

        /// <summary>
        /// The user-space y an explicit destination puts at the top of the window (§12.3.2.2,
        /// Table 149): <c>/XYZ</c>'s top, <c>/FitH</c>'s and <c>/FitBH</c>'s top, <c>/FitR</c>'s top.
        /// Null for the forms that name no top (<c>/Fit</c>, <c>/FitB</c>, <c>/FitV</c>,
        /// <c>/FitBV</c>), for a null operand (§12.3.2.2: "the current value … unchanged"), and for a
        /// number that is not finite or not there.
        /// </summary>
        internal static double? TopFromDestArray(PdfDocument doc, PdfArray dest)
        {
            if (dest == null || dest.Count < 2) return null;
            var form = Resolved(doc, dest[1]) as PdfName;
            int at = form == null ? -1
                : form.Equals(PdfName.XYZ) ? 3
                : form.Equals(PdfName.FitH) || form.Equals(PdfName.FitBH) ? 2
                : form.Equals(PdfName.FitR) ? 5
                : -1;
            if (at < 0 || at >= dest.Count) return null;
            if (!(Resolved(doc, dest[at]) is PdfNumber top)) return null;
            double y = top.DoubleValue;
            return double.IsNaN(y) || double.IsInfinity(y) ? (double?)null : y;
        }

        /// <summary>
        /// <see cref="TopFromDestArray"/> as the <c>float</c> every reader of a link's or an outline
        /// item's top keeps: null when there is none or it lies outside a <c>float</c>'s range — a
        /// cast of such a double is infinite, which no scroll position can take.
        /// </summary>
        internal static float? TopAsFloat(PdfDocument doc, PdfArray dest)
        {
            double? top = TopFromDestArray(doc, dest);
            return top.HasValue && top.Value >= float.MinValue && top.Value <= float.MaxValue ? (float?)(float)top.Value : null;
        }

        /// <summary>An operand after the indirect reference it may be; the operand itself without a document.</summary>
        private static PdfObject Resolved(PdfDocument doc, PdfObject obj) => doc?.ResolveObject(obj) ?? obj;

        /// <summary>
        /// The zero-based index of the page <paramref name="page"/> names — an indirect reference
        /// to a page object, or an integer page index — or <see cref="NotFound"/>.
        /// </summary>
        /// <remarks>
        /// <para>A destination's first element and a document part's <c>/Start</c> and <c>/End</c>
        /// (ISO 32000-2 §14.12, Table 409: "an indirect reference to the page object") are the
        /// same question, so they get the same answer: the reference is matched on its object
        /// number, not resolved, for the reason given on the type.</para>
        /// </remarks>
        internal static int IndexOfPage(PdfDocument doc, PdfObject page)
        {
            if (doc == null) return NotFound;

            // A remote/embedded destination writes the page as an integer index instead
            // (§12.3.2.2, and every /GoToR target). Out of range is a broken destination, not
            // page 0: reporting it as found is the failure mode this range check exists for.
            if (page is PdfNumber number)
            {
                // Not a page number until it is an integer: (int) on the double truncated
                // [1.5 /Fit] to page 1, a confident answer for a destination that names no page.
                if (!number.IsInteger) return NotFound;
                long index = number.LongValue;
                return index >= 0 && index < doc.Pages.Count ? (int)index : NotFound;
            }

            if (!(page is PdfReference pageRef)) return NotFound;
            if (pageRef.ReferenceObjectNumber <= 0) return NotFound;

            // The document's index of its pages by object number: a scan here cost every
            // destination of a walk the page count.
            int found = doc.PageIndexOfObject(pageRef.ReferenceObjectNumber);
            return found >= 0 ? found : NotFound;
        }
    }
}
