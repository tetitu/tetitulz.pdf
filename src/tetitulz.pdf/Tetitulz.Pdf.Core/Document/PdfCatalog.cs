// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Document
{
    public sealed class PdfCatalog
    {
        private readonly PdfDocument _document;

        public PdfDictionary Dictionary { get; }

        public PdfCatalog(PdfDictionary dictionary, PdfDocument document)
        {
            Dictionary = dictionary;
            _document = document;
        }

        public PdfDictionary PageTreeRoot
        {
            get
            {
                var pagesObj = Dictionary[PdfName.Pages];
                return _document.ResolveObject(pagesObj) as PdfDictionary;
            }
        }

        public PdfName Type => Dictionary.GetName(PdfName.Type);

        /// <summary>
        /// The catalog's <c>/Metadata</c> XMP stream (§14.3.2), or <c>null</c> when the catalog has
        /// none or the entry is not a stream.
        /// </summary>
        /// <remarks>
        /// <para><b>It returns a <see cref="PdfStream"/>, not a <see cref="PdfDictionary"/>.</b> A
        /// dictionary would be wrong in two ways at once: a caller could not reach
        /// <c>GetDecodedData()</c> without a cast, and <c>as PdfDictionary</c> <i>succeeds</i> for a
        /// <c>/Metadata</c> that is a plain dictionary — handing back a non-stream under a name
        /// promising one.</para>
        ///
        /// <para><b>This is deliberately NOT the one reader of catalog <c>/Metadata</c>.</b> Eight
        /// sites across Base, Conformance and Accessibility read that entry themselves, and
        /// consolidating them here would <i>lose information</i>: they distinguish three states,
        /// not two — absent, present but unreadable, and readable — and at least one records why
        /// that matters, since the <b>absence</b> of the key is visible even under encryption while
        /// the packet behind it is not. A single accessor answering <c>null</c> collapses the first
        /// two. The presence test stays at the callers; only this convenience lives here.</para>
        /// </remarks>
        public PdfStream GetMetadataStream()
        {
            var metaObj = Dictionary[PdfName.Metadata];
            return metaObj != null ? _document.ResolveObject(metaObj) as PdfStream : null;
        }

        /// <summary>
        /// Returns the document /ViewerPreferences wrapper, or an empty one with a
        /// null backing dict when the catalog carries no preferences.  The object
        /// is cheap to construct on demand — callers normally grab it once.
        /// </summary>
        public PdfViewerPreferences GetViewerPreferences()
            => PdfViewerPreferences.Read(Dictionary, _document.ResolveObject);
    }
}
