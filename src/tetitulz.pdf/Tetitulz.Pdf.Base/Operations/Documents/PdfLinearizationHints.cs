// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// ISO 32000-1 Annex F's primary hint stream, decoded — what a progressive reader needs to fetch
    /// one page without the cross-reference table.
    /// </summary>
    /// <remarks>
    /// <para><b>Positions here are file offsets, already corrected.</b> Annex F.4 NOTE 3 writes hint
    /// positions "as if the primary hint stream itself were not present", so a raw table position at
    /// or past the hint stream is short by its length. Correcting it at the boundary rather than at
    /// each use is deliberate: every caller would otherwise have to know the rule, and the one that
    /// forgot would be wrong by exactly one stream's length — a plausible-looking offset inside a
    /// real object.</para>
    ///
    /// <para><b>These are hints, not facts.</b> They are the producer's claims about a file this has
    /// not read past the hint stream, and an appended revision invalidates them without changing
    /// them. A client fetches the range a hint names and then reads what it got; it does not trust
    /// the hint about what is there.</para>
    /// </remarks>
    public sealed class PdfLinearizationHints
    {
        internal PdfLinearizationHints(
            IReadOnlyList<PdfPageHint> pages,
            IReadOnlyList<PdfSharedObjectHint> sharedObjects,
            int firstSharedObjectNumber,
            long firstSharedObjectOffset,
            int sharedObjectsInFirstPage,
            int firstPageObjectCount,
            PdfGenericHint outline,
            PdfGenericHint threads = null,
            PdfGenericHint namedDestinations = null,
            PdfGenericHint information = null,
            PdfGenericHint pageLabels = null,
            PdfGenericHint logicalStructure = null,
            PdfGenericHint renditions = null,
            IReadOnlyList<PdfGenericHint> embeddedFiles = null,
            IReadOnlyList<PdfThumbnailHint> thumbnails = null,
            PdfGenericHint thumbnailSharedObjects = null,
            PdfGenericHint interactiveForm = null)
        {
            InteractiveForm = interactiveForm;
            Thumbnails = thumbnails;
            ThumbnailSharedObjects = thumbnailSharedObjects;
            LogicalStructure = logicalStructure;
            Renditions = renditions;
            EmbeddedFiles = embeddedFiles;
            Threads = threads;
            NamedDestinations = namedDestinations;
            Information = information;
            PageLabels = pageLabels;
            Pages = pages;
            SharedObjects = sharedObjects;
            FirstSharedObjectNumber = firstSharedObjectNumber;
            FirstSharedObjectOffset = firstSharedObjectOffset;
            SharedObjectsInFirstPage = sharedObjectsInFirstPage;
            FirstPageObjectCount = firstPageObjectCount;
            Outline = outline;
        }

        /// <summary>One entry per page, in page order (Table F.3).</summary>
        public IReadOnlyList<PdfPageHint> Pages { get; }

        /// <summary>The shared object groups (Table F.5), indexed by the identifiers pages carry.</summary>
        public IReadOnlyList<PdfSharedObjectHint> SharedObjects { get; }

        /// <summary>
        /// The object number of the first entry in the shared section — the one after the first
        /// page's own objects, which occupy the table's opening <see cref="SharedObjectsInFirstPage"/>
        /// entries.
        /// </summary>
        public int FirstSharedObjectNumber { get; }

        /// <summary>The file offset of that object.</summary>
        public long FirstSharedObjectOffset { get; }

        /// <summary>
        /// How many of <see cref="SharedObjects"/>'s leading entries describe the first page's own
        /// objects rather than the shared section. Table F.5 puts both in one table, which is why a
        /// caller cannot index it without this.
        /// </summary>
        public int SharedObjectsInFirstPage { get; }

        /// <summary>Objects in the first page, as the page table declares it.</summary>
        public int FirstPageObjectCount { get; }

        /// <summary>The outline hint table (Table F.9, <c>/O</c>), or <c>null</c> when the hint stream has none.</summary>
        public PdfGenericHint Outline { get; }

        /// <summary>The thread information hint table (Table F.9, <c>/A</c>), or <c>null</c>.</summary>
        public PdfGenericHint Threads { get; }

        /// <summary>The named destination hint table (Table F.9, <c>/E</c>), or <c>null</c>.</summary>
        public PdfGenericHint NamedDestinations { get; }

        /// <summary>The information dictionary hint table (Table F.9, <c>/I</c>), or <c>null</c>.</summary>
        public PdfGenericHint Information { get; }

        /// <summary>The page label hint table (Table F.9, <c>/L</c>), or <c>null</c>.</summary>
        public PdfGenericHint PageLabels { get; }

        /// <summary>
        /// The logical structure hint table (Table F.10, <c>/C</c>), an extended generic table — its
        /// <see cref="PdfGenericHint.SharedObjectIdentifiers"/> listed — or <c>null</c>.
        /// </summary>
        public PdfGenericHint LogicalStructure { get; }

        /// <summary>
        /// The renditions name tree hint table (Table F.10, <c>/R</c>), an extended generic table, or
        /// <c>null</c>.
        /// </summary>
        public PdfGenericHint Renditions { get; }

        /// <summary>
        /// The embedded file stream hint table (Tables F.11 and F.12, <c>/B</c>): one entry per group, its
        /// <see cref="PdfGenericHint.FirstObjectNumber"/> the embedded file stream, its offset accumulated
        /// from the first group's location and the lengths before it — or <c>null</c> when the hint stream
        /// has none.
        /// </summary>
        public IReadOnlyList<PdfGenericHint> EmbeddedFiles { get; }

        /// <summary>
        /// The thumbnail hint table (Tables F.7 and F.8, <c>/T</c>): one entry per page that has a thumbnail,
        /// in page order — or <c>null</c> when the hint stream has none.
        /// </summary>
        public IReadOnlyList<PdfThumbnailHint> Thumbnails { get; }

        /// <summary>
        /// Table F.7 items 9–12: the thumbnail shared objects section — what thumbnails share and nothing else
        /// references — or <c>null</c> when there is no thumbnail table or the section is empty.
        /// </summary>
        public PdfGenericHint ThumbnailSharedObjects { get; }

        /// <summary>
        /// The interactive form hint table (Table F.10, <c>/V</c>), an extended generic table naming the objects
        /// <c>/AcroForm</c> reaches — a run of part 4 — or <c>null</c>.
        /// </summary>
        public PdfGenericHint InteractiveForm { get; }
    }

    /// <summary>One page's entry in the thumbnail hint table (Table F.8).</summary>
    public sealed class PdfThumbnailHint
    {
        internal PdfThumbnailHint(int pageIndex, int firstObjectNumber, long offset, int objectCount, long length)
        {
            PageIndex = pageIndex;
            FirstObjectNumber = firstObjectNumber;
            Offset = offset;
            ObjectCount = objectCount;
            Length = length;
        }

        /// <summary>The page, counted from 0.</summary>
        public int PageIndex { get; }

        /// <summary>The thumbnail image's object number: the group's first.</summary>
        public int FirstObjectNumber { get; }

        /// <summary>The group's file offset, with Annex F.4 NOTE 3 already applied.</summary>
        public long Offset { get; }

        /// <summary>How many objects the group contains.</summary>
        public int ObjectCount { get; }

        /// <summary>The group's length in bytes.</summary>
        public long Length { get; }
    }

    /// <summary>
    /// A generic hint table entry (Table F.9) — a run of objects serving one document feature,
    /// named so a client can fetch them together.
    /// </summary>
    public sealed class PdfGenericHint
    {
        internal PdfGenericHint(int firstObjectNumber, long offset, int objectCount, long length,
                                IReadOnlyList<int> sharedObjectIdentifiers = null)
        {
            SharedObjectIdentifiers = sharedObjectIdentifiers ?? Array.Empty<int>();
            FirstObjectNumber = firstObjectNumber;
            Offset = offset;
            ObjectCount = objectCount;
            Length = length;
        }

        /// <summary>
        /// An extended generic table's (Table F.10) shared object identifiers — indexes into
        /// <see cref="PdfLinearizationHints.SharedObjects"/> its objects reference; empty for a plain one.
        /// </summary>
        public IReadOnlyList<int> SharedObjectIdentifiers { get; }

        /// <summary>The object number the run starts at.</summary>
        public int FirstObjectNumber { get; }

        /// <summary>The run's file offset, with Annex F.4 NOTE 3 already applied.</summary>
        public long Offset { get; }

        /// <summary>How many objects the run contains.</summary>
        public int ObjectCount { get; }

        /// <summary>The run's length in bytes.</summary>
        public long Length { get; }
    }

    /// <summary>One page's entry in the Annex F page offset table (Table F.3).</summary>
    public sealed class PdfPageHint
    {
        internal PdfPageHint(
            int objectCount, long offset, long length, long contentOffset, long contentLength,
            IReadOnlyList<int> sharedObjectIdentifiers)
        {
            ObjectCount = objectCount;
            Offset = offset;
            Length = length;
            ContentOffset = contentOffset;
            ContentLength = contentLength;
            SharedObjectIdentifiers = sharedObjectIdentifiers;
        }

        /// <summary>How many objects this page's run occupies.</summary>
        public int ObjectCount { get; }

        /// <summary>
        /// The file offset where this page's objects begin — <b>the byte range to fetch</b>, with
        /// <see cref="Length"/>, and the reason the whole table is worth decoding.
        /// </summary>
        public long Offset { get; }

        /// <summary>The length in bytes of this page's run of objects.</summary>
        public long Length { get; }

        /// <summary>
        /// The file offset of this page's content stream — Table F.3's page-relative value with
        /// <see cref="Offset"/> already added.
        /// </summary>
        /// <remarks>
        /// <b>It is a hint, and a coarse one in practice.</b> Table F.3 measures it from the start
        /// of the page's objects, and a producer may declare the whole run rather than locate the
        /// stream within it — this library's own linearizer writes offset 0 and the page's full
        /// length. Use it to decide what to fetch; resolve <c>/Contents</c> to know what you got.
        /// </remarks>
        public long ContentOffset { get; }

        /// <summary>The declared length of this page's content stream, with the same caveat.</summary>
        public long ContentLength { get; }

        /// <summary>
        /// Indices into <see cref="PdfLinearizationHints.SharedObjects"/> this page also needs.
        /// Empty for the first page, whose objects are not shared by definition.
        /// </summary>
        public IReadOnlyList<int> SharedObjectIdentifiers { get; }
    }

    /// <summary>One group in the Annex F shared object hint table (Table F.5).</summary>
    public sealed class PdfSharedObjectHint
    {
        internal PdfSharedObjectHint(long length, int objectCount, bool hasSignature)
        {
            Length = length;
            ObjectCount = objectCount;
            HasSignature = hasSignature;
        }

        /// <summary>The group's length in bytes.</summary>
        public long Length { get; }

        /// <summary>How many objects the group contains (at least one).</summary>
        public int ObjectCount { get; }

        /// <summary>
        /// Whether the group carries an MD5 signature. Annex F allows one so a client can recognise
        /// an object it already holds; this library records its presence and does not check it.
        /// </summary>
        public bool HasSignature { get; }
    }
}
