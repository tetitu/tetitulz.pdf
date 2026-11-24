// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Operations.Extraction;

namespace Tetitulz.Pdf.Accessibility.Models
{
    /// <summary>
    /// One link annotation offered to a link-description provider: where it is, where it goes, and
    /// the description it carries now. The provider returns the text a screen reader announces for
    /// the link — its <c>/Contents</c>, where ISO 14289-1 §7.18.5 puts a link's alternate
    /// description — or <c>null</c> to leave the link as it is.
    /// </summary>
    /// <remarks>
    /// A description cannot be derived from the document: the URI or the words under the link say
    /// where it goes, not what a reader is told, and a description written from them passes UA22
    /// while telling a listener nothing. So the text comes from the caller, and this is what the
    /// caller decides it from.
    /// </remarks>
    public sealed class LinkContext
    {
        /// <summary>
        /// Zero-based position among the link annotations offered, in page order and each page's
        /// <c>/Annots</c> order. An annotation several pages list is offered once, at the first.
        /// </summary>
        public int LinkIndex { get; }

        /// <summary>Zero-based index of the page the link is on.</summary>
        public int PageIndex { get; }

        /// <summary>The link's rectangle and target: a URI, a page of this document, another file, a named action.</summary>
        public PdfLink Link { get; }

        /// <summary>The link's present <c>/Contents</c> text, or <c>null</c> when it has none.</summary>
        public string ExistingDescription { get; }

        /// <summary>Creates a context; the editor makes one per link it offers.</summary>
        public LinkContext(int linkIndex, int pageIndex, PdfLink link, string existingDescription)
        {
            LinkIndex           = linkIndex;
            PageIndex           = pageIndex;
            Link                = link;
            ExistingDescription = existingDescription;
        }
    }
}
