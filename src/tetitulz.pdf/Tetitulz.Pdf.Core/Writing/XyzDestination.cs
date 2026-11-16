// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// The §12.3.2.2 <c>/XYZ</c> explicit destination — <c>[page /XYZ left top zoom]</c> — as every
    /// writer in the tree spells it.
    /// </summary>
    /// <remarks>
    /// <para><b>Five copies</b>: <see cref="PdfDocumentBuilder"/>'s named destinations, and in Base
    /// the named-destination writer, the initial view's <c>/OpenAction</c> and the outline writer,
    /// and DocxToPdf's heading destinations. They agreed on the array, and on <c>null</c> for a
    /// coordinate the caller leaves alone — "keep the viewer's current value", which is not 0.</para>
    ///
    /// <para><b>They disagreed on one page, which is why this is more than tidying.</b> A page
    /// dictionary written directly inside <c>/Kids</c> — malformed, and tolerated by the page tree —
    /// has no object number, so nothing can reference it. The named-destination and initial-view
    /// writers wrote its index, as a remote destination does; the outline writer wrote no
    /// destination at all, so a bookmark read from that page and written back pointed nowhere.
    /// <see cref="PageOf"/> is the one answer: the index, which every reader of destinations in
    /// the tree resolves (<c>Base.Operations.PdfDestinationPage</c>).</para>
    /// </remarks>
    internal static class XyzDestination
    {
        /// <summary>
        /// <c>[page /XYZ left top zoom]</c>, with <c>null</c> for each coordinate that is null here.
        /// </summary>
        internal static PdfArray Build(PdfObject page, double? left, double? top, double? zoom)
        {
            var dest = new PdfArray();
            dest.Add(page);
            dest.Add(PdfName.XYZ);
            dest.Add(left.HasValue ? (PdfObject)new PdfNumber(left.Value) : PdfNull.Instance);
            dest.Add(top.HasValue ? (PdfObject)new PdfNumber(top.Value) : PdfNull.Instance);
            dest.Add(zoom.HasValue ? (PdfObject)new PdfNumber(zoom.Value) : PdfNull.Instance);
            return dest;
        }

        /// <summary>The destination of the page written as object <paramref name="pageObjectNumber"/>.</summary>
        internal static PdfArray Build(int pageObjectNumber, double? left, double? top, double? zoom)
            => Build(new PdfReference(pageObjectNumber, 0), left, top, zoom);

        /// <summary>
        /// How a destination names page <paramref name="pageIndex"/> of <paramref name="doc"/>: a
        /// reference to its page object, or its zero-based index when the page dictionary has no
        /// object number to reference.
        /// </summary>
        internal static PdfObject PageOf(PdfDocument doc, int pageIndex)
        {
            int objNum = doc.Pages[pageIndex]?.Dictionary?.ObjectNumber ?? 0;
            return objNum > 0 ? new PdfReference(objNum, 0) : (PdfObject)new PdfNumber(pageIndex);
        }
    }
}
