// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Accessibility.Models
{
    /// <summary>What kind of marks a page carries (ISO 32000-1 §8–§9 content categories).</summary>
    public enum PageContentKind
    {
        /// <summary>
        /// The page could not be classified — its content would not decode, or its Form XObject scan
        /// was cut short. <b>Not</b> a synonym for blank: on a partial scan an absence of marks is an
        /// artefact of the ceiling rather than a fact about the page.
        /// </summary>
        Undetermined,

        /// <summary>Nothing was painted: no text, no image, no path fill or stroke, no shading.</summary>
        Blank,

        /// <summary>Text, and no image.</summary>
        TextOnly,

        /// <summary>An image and no text — what a scanned page looks like.</summary>
        ImageOnly,

        /// <summary>Marks, but neither text nor an image — a chart, a rule, a logo.</summary>
        VectorOnly,

        /// <summary>Both text and an image.</summary>
        Mixed,
    }

    /// <summary>
    /// One page's content classification, with the counts it was derived from.
    /// </summary>
    /// <remarks>
    /// <para><b>The counts are exposed deliberately.</b> A caller batching thousands of documents
    /// needs to tune its own threshold — "a page with two stray text operators is still a scan" is a
    /// judgement about a corpus, not about PDF — and a bare enum cannot support that.</para>
    ///
    /// <para><b>Annotation appearances are not counted.</b> The classification reads page
    /// <c>/Contents</c> and the Form XObjects it invokes; a page whose only marks come from
    /// annotation <c>/AP</c> streams classifies as <see cref="PageContentKind.Blank"/>.</para>
    /// </remarks>
    public sealed class PageContentClassification
    {
        internal PageContentClassification(
            int pageNumber, PageContentKind kind,
            int textShowingOperators, int imageXObjectInvocations,
            bool hasInlineImage, int paintingOperators, string undeterminedReason)
        {
            PageNumber = pageNumber;
            Kind = kind;
            TextShowingOperators = textShowingOperators;
            ImageXObjectInvocations = imageXObjectInvocations;
            HasInlineImage = hasInlineImage;
            PaintingOperators = paintingOperators;
            UndeterminedReason = undeterminedReason;
        }

        /// <summary>One-based page number, as the accessibility report model uses.</summary>
        public int PageNumber { get; }

        /// <summary>What the page carries.</summary>
        public PageContentKind Kind { get; }

        /// <summary>
        /// Nothing was painted. <b>False for an undetermined page</b> — "we could not tell" must
        /// never read as "there is nothing there".
        /// </summary>
        public bool IsBlank => Kind == PageContentKind.Blank;

        /// <summary>
        /// An image and no text — the signature of a scanned page that has not been through OCR.
        /// False for an undetermined page, for the same reason.
        /// </summary>
        public bool IsScanned => Kind == PageContentKind.ImageOnly;

        /// <summary>Text-showing operators found, including inside Form XObjects.</summary>
        public int TextShowingOperators { get; }

        /// <summary>Image XObject invocations found, including inside Form XObjects.</summary>
        public int ImageXObjectInvocations { get; }

        /// <summary>Whether the page carries an inline image (<c>BI … ID … EI</c>).</summary>
        public bool HasInlineImage { get; }

        /// <summary>Path-painting and shading operators — marks that are neither text nor image.</summary>
        public int PaintingOperators { get; }

        /// <summary>
        /// Why the page could not be classified — non-null <b>exactly</b> when <see cref="Kind"/> is
        /// <see cref="PageContentKind.Undetermined"/>.
        /// </summary>
        public string UndeterminedReason { get; }
    }
}
