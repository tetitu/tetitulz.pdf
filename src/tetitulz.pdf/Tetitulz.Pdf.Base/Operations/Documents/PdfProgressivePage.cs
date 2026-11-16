// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>A byte range of a file — what a progressive client asks its transport for.</summary>
    public sealed class PdfByteRange
    {
        internal PdfByteRange(long offset, long length)
        {
            Offset = offset;
            Length = length;
        }

        /// <summary>Absolute offset in the file.</summary>
        public long Offset { get; }

        /// <summary>Length in bytes.</summary>
        public long Length { get; }

        /// <inheritdoc/>
        public override string ToString() => Offset + "+" + Length;
    }

    /// <summary>
    /// One page read — or not yet read — from a file the client holds only part of.
    /// </summary>
    /// <remarks>
    /// <para><b>The two states are not symmetrical, and that is the point.</b> A read that succeeded
    /// carries the page; a read that could not carries <see cref="Missing"/>, which names the ranges
    /// to fetch before trying again. A progressive client is a loop over that: ask, fetch what it
    /// was told, ask again. Answering only "no" would leave it guessing which bytes to get.</para>
    /// </remarks>
    public sealed class PdfProgressivePage
    {
        private static readonly IReadOnlyList<PdfReference> NoReferences = new PdfReference[0];

        private readonly ProgressiveSections _sections;

        internal PdfProgressivePage(
            int pageIndex, PdfDictionary page, byte[] content, IReadOnlyList<PdfByteRange> missing,
            string error = null, ProgressiveSections sections = null,
            IReadOnlyList<PdfReference> unresolvedResources = null)
        {
            PageIndex = pageIndex;
            Page = page;
            Content = content;
            Missing = missing;
            Error = error;
            _sections = sections;
            UnresolvedResources = unresolvedResources ?? NoReferences;
        }

        /// <summary>
        /// References the page's <c>/Resources</c> reach that no fetched range holds — after the
        /// shared-object groups the hint table names for the page were fetched. Empty for a page
        /// read in full; never null.
        /// </summary>
        /// <remarks>
        /// Annex F puts a page's objects in its own run or in the shared section, so a reference
        /// still outside both is the file's defect, reported here rather than answered as absent:
        /// a renderer asking <see cref="Resolve"/> for it gets null and can say which font or image
        /// the page could not have.
        /// </remarks>
        public IReadOnlyList<PdfReference> UnresolvedResources { get; }

        /// <summary>
        /// The object numbered <paramref name="objectNumber"/>, parsed from the ranges fetched for
        /// this page — its own run and the shared-object groups it names. False when no fetched range
        /// holds it, or when the page was not read.
        /// </summary>
        public bool TryGetObject(int objectNumber, out PdfObject value)
        {
            value = _sections?.Parse(objectNumber);
            return value != null;
        }

        /// <summary>
        /// <paramref name="value"/> itself, or, for a reference, the object it names among the fetched
        /// ranges — null when none holds it. The resolver a caller drawing the page uses for its
        /// <c>/Resources</c>, a font's descriptor, an image's mask.
        /// </summary>
        public PdfObject Resolve(PdfObject value)
            => value is PdfReference reference
                ? (TryGetObject(reference.ReferenceObjectNumber, out PdfObject target) ? target : null)
                : value;

        /// <summary>The page this describes, counted from zero.</summary>
        public int PageIndex { get; }

        /// <summary>
        /// The page dictionary, or <c>null</c> when the bytes that hold it have not arrived.
        /// </summary>
        public PdfDictionary Page { get; }

        /// <summary>
        /// The page's content stream, decoded, or <c>null</c> when it could not be assembled from
        /// the ranges fetched so far.
        /// </summary>
        /// <remarks>
        /// <b>Separate from <see cref="Page"/> on purpose.</b> Annex F's page offset table names the
        /// content stream's own offset and length (Table F.3 items 5 and 6), so a client can have
        /// the page dictionary and not the content, or fetch the content range directly without
        /// resolving <c>/Contents</c> at all.
        /// </remarks>
        public byte[] Content { get; }

        /// <summary>
        /// The ranges still needed, empty when nothing is. Never null.
        /// </summary>
        public IReadOnlyList<PdfByteRange> Missing { get; }

        /// <summary>
        /// Why nothing more could be read, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// <b>The asynchronous path reports here and not through a <c>Last*</c> slot</b>, because
        /// those are <c>[ThreadStatic]</c> and a continuation may resume on another thread — the
        /// caveat CLAUDE.md records against `DssBuilder.TryBuildDssAsync`. The synchronous path sets
        /// both, so a caller may use whichever suits it.
        /// </remarks>
        public string Error { get; }
    }
}
