// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// Builds the single-level <c>/Pages</c> root every document writer in this repository emits
    /// (§7.7.3.2): <c>/Type /Pages</c>, <c>/Kids</c>, and a <c>/Count</c> taken from the kids.
    /// </summary>
    /// <remarks>
    /// <para><b>Five writers built this by hand</b> — <c>PdfDocumentBuilder</c> (Core),
    /// <c>PdfNup</c> and <c>PdfTaggedDocumentBuilder</c> (Base), <c>DocxRenderContext</c>
    /// (DocxToPdf) and <c>PdfObjectHelper</c> (the PageEdits plugin) — three of them byte-identical
    /// down to the local variable names.</para>
    ///
    /// <para><b>The fifth had already been consolidated, into the wrong ring.</b>
    /// <c>PdfObjectHelper.BuildPageTreeAndCatalog</c> is the shape the others wanted and was
    /// unreachable from any of them, because it is <c>internal</c> to a <i>plugin</i>. That is the
    /// same "share downward into a leaf" move CLAUDE.md records for
    /// <see cref="IncrementalUpdateWriter"/> and <c>PdfSanitizer</c>: the shared code goes
    /// <b>down</b> into Core, never sideways.</para>
    ///
    /// <para><b>Public, and for the documented reason.</b> Core's <c>InternalsVisibleTo</c> names
    /// no plugin, so a plugin cannot see Core internals — the same reason
    /// <c>AcroFieldClassification</c> and <c>SfntBuilder</c> are public. It is a writer primitive,
    /// not a feature.</para>
    ///
    /// <para><b>Why <c>/Count</c> is never a parameter.</b> §7.7.3.2 defines it as the number of
    /// leaf nodes below this one, so a caller supplying it separately can only ever agree with the
    /// kids or be wrong — and a <c>/Count</c> disagreeing with <c>/Kids</c> makes readers disagree
    /// about how many pages the document has. Deriving it removes the question.</para>
    ///
    /// <para>This builds a <b>flat</b> tree — every page a direct kid of one root. That is what all
    /// five callers emit and what the writers here produce; it is not a general balanced-tree
    /// builder, and a document large enough to want one would need a different type.</para>
    /// </remarks>
    public static class PdfPageTreeWriter
    {
        /// <summary>
        /// Builds the <c>/Pages</c> root over <paramref name="kids"/>.
        /// </summary>
        /// <param name="kids">
        /// The page references, in document order. <c>/Count</c> is taken from this.
        /// </param>
        /// <param name="objectNumber">
        /// Object number the root is written under. Stamped onto the returned dictionary for
        /// writers that read it back; harmless for those passing it to
        /// <c>PdfWriter.WriteIndirectObject</c> explicitly.
        /// </param>
        /// <returns>The <c>/Pages</c> dictionary.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="kids"/> is null.</exception>
        public static PdfDictionary BuildRoot(PdfArray kids, int objectNumber)
        {
            if (kids == null) throw new ArgumentNullException(nameof(kids));

            var pages = new PdfDictionary();
            pages.Set(PdfName.Type, PdfName.Pages);
            pages.Set(PdfName.Kids, kids);
            pages.Set(PdfName.Count, new PdfNumber(kids.Count));
            pages.ObjectNumber = objectNumber;
            pages.GenerationNumber = 0;
            return pages;
        }

        /// <summary>
        /// Builds the <c>/Pages</c> root over page <i>object numbers</i>, which is how four of the
        /// five callers held them — each becoming a generation-0 reference.
        /// </summary>
        /// <param name="pageObjectNumbers">The page objects, in document order.</param>
        /// <param name="objectNumber">Object number the root is written under.</param>
        /// <returns>The <c>/Pages</c> dictionary.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="pageObjectNumbers"/> is null.
        /// </exception>
        public static PdfDictionary BuildRoot(IEnumerable<int> pageObjectNumbers, int objectNumber)
        {
            if (pageObjectNumbers == null) throw new ArgumentNullException(nameof(pageObjectNumbers));

            var kids = new PdfArray();
            foreach (int pageObjNum in pageObjectNumbers)
                kids.Add(new PdfReference(pageObjNum, 0));

            return BuildRoot(kids, objectNumber);
        }
    }
}
