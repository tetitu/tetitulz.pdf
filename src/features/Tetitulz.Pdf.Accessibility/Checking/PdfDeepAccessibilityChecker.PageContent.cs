// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Accessibility.Checking
{
    public static partial class PdfDeepAccessibilityChecker
    {
        /// <summary>
        /// Classifies each page as blank, text, image (i.e. scanned), vector or mixed.
        /// </summary>
        /// <param name="doc">An open document. Not disposed.</param>
        /// <remarks>
        /// <para><b>It reuses the audit's page walk rather than adding a second scanner.</b>
        /// <c>CollectPages</c> already decodes each page's <c>/Contents</c>, descends into Form
        /// XObjects, and counts text-showing operators, image invocations and inline images; UA24
        /// ("image-only page") is derived from exactly those counts. A separate implementation of
        /// "count marks on a page" would be this repository's recurring defect, and the two would
        /// disagree the first time either changed.</para>
        ///
        /// <para><b>Why it lives in this feature rather than on <c>PdfOperations</c>.</b>
        /// <c>ContentStreamScanner</c> and <c>PageInfo</c> are <c>internal</c> to
        /// <c>Tetitulz.Pdf.Accessibility</c>, and the ring model forbids <c>Base → feature</c>. The
        /// alternatives were an illegal reference or a second scanner; moving the scanner down into
        /// Base would drag this feature's tagging ceilings with it and is not justified by a
        /// predicate.</para>
        ///
        /// <para><b>Annotation appearances are not walked.</b> The classification reads page
        /// <c>/Contents</c> and the Form XObjects it invokes, so a page whose only marks come from
        /// annotation <c>/AP</c> streams — a stamp, a signature block, a highlight — classifies as
        /// <see cref="PageContentKind.Blank"/>. That is a real limit, stated here rather than
        /// tolerated silently.</para>
        ///
        /// <para><b><see cref="PageContentKind.Undetermined"/> is not a failure to classify, it is a
        /// refusal to guess.</b> It is returned exactly when the page's content could not be decoded
        /// or its Form XObject scan was cut short — the same two honesty gates UA24 applies before it
        /// reports anything — because on a partial scan "no text was found" is an artefact of the
        /// ceiling rather than a fact about the page.</para>
        /// </remarks>
        public static IReadOnlyList<PageContentClassification> ClassifyPageContent(PdfDocument doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            // The same resolver the audit uses: PdfDocument.Load reaches no catalog for an encrypted
            // file, so reading doc.Catalog directly would classify every page of one as absent.
            PdfDictionary catalog = ResolveCatalog(doc);
            if (catalog == null) return Array.Empty<PageContentClassification>();

            var ctx = new CheckContext
            {
                Doc = doc,
                Catalog = catalog,
                Issues = new List<AccessibilityIssue>(),
                Encrypted = doc.IsEncrypted,
                MaxPageContentBytes = DefaultMaxPageContentBytes,
                ContentByteBudget = DefaultMaxDocumentContentBytes,
                MaxDocumentContentBytes = DefaultMaxDocumentContentBytes,
            };

            try { ctx.Index = PdfObjectIndex.Build(doc); }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.ClassifyPageContent",
                    "the object index could not be built", ex);
                ctx.Index = null;
            }

            CollectPages(ctx);

            var result = new List<PageContentClassification>(ctx.Pages?.Count ?? 0);
            if (ctx.Pages == null) return result;

            foreach (var page in ctx.Pages)
                result.Add(Classify(page));

            return result;
        }

        /// <summary>The classification a single scanned page yields.</summary>
        private static PageContentClassification Classify(PageInfo page)
        {
            string undetermined = null;
            if (page.ContentError != null)
                undetermined = "the page's content stream could not be decoded (" + page.ContentError + ")";
            else if (page.FormXObjectScanIncomplete)
                undetermined = "the page's Form XObject scan did not complete, so an absence of " +
                               "text or images here would be an artefact of the ceiling rather than " +
                               "a fact about the page";

            PageContentKind kind;
            if (undetermined != null)
            {
                kind = PageContentKind.Undetermined;
            }
            else
            {
                // The same three predicates UA24 asks, on the same PageInfo, so a change to either
                // reaches both — "image and no text" is one rule, not two.
                if (page.HasText && page.HasImage) kind = PageContentKind.Mixed;
                else if (page.HasText) kind = PageContentKind.TextOnly;
                else if (page.HasImage) kind = PageContentKind.ImageOnly;
                else if (page.HasVectorMarks) kind = PageContentKind.VectorOnly;
                else kind = PageContentKind.Blank;
            }

            return new PageContentClassification(
                page.PageNumber, kind,
                page.TextShowingOperators, page.ImageXObjectInvocations,
                page.HasInlineImage, page.PaintingOperators, undetermined);
        }
    }
}
