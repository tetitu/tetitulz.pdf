// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// What a linearized document's first object says about itself — ISO 32000-1 Annex F,
    /// Table F.1.
    /// </summary>
    /// <remarks>
    /// <para><b>Every value is the producer's claim, about a file this model has not read.</b> They
    /// are what <see cref="PdfRevisions.TryReadLinearization"/> found in the head, reported as
    /// written. Annex F's own parameters go stale the moment a revision is appended — <c>/L</c> no
    /// longer matches the file's length and the hint tables no longer describe it — and Table F.1
    /// says so. A caller that depends on them should check <see cref="FileLength"/> against the
    /// bytes it actually holds, which is the comparison a progressive fetch wants to make anyway.
    /// </para>
    /// <para><b>Offsets are from the start of the file</b>, as Annex F defines them, and not from
    /// the <c>%PDF-</c> header — for a linearized file those are the same position, because the
    /// header is where such a file begins.</para>
    /// </remarks>
    public sealed class PdfLinearizationInfo
    {
        internal PdfLinearizationInfo(
            long fileLength, int firstPageObjectNumber, long firstPageEndOffset,
            int pageCount, long mainCrossReferenceOffset,
            long hintStreamOffset, long hintStreamLength,
            long overflowHintOffset, long overflowHintLength)
        {
            FileLength = fileLength;
            FirstPageObjectNumber = firstPageObjectNumber;
            FirstPageEndOffset = firstPageEndOffset;
            PageCount = pageCount;
            MainCrossReferenceOffset = mainCrossReferenceOffset;
            HintStreamOffset = hintStreamOffset;
            HintStreamLength = hintStreamLength;
            OverflowHintOffset = overflowHintOffset;
            OverflowHintLength = overflowHintLength;
        }

        /// <summary>Table F.1 <c>/L</c> — the file's length when it was linearized.</summary>
        public long FileLength { get; }

        /// <summary>
        /// Table F.1 <c>/H</c>, first entry — where the primary hint stream begins.
        /// </summary>
        /// <remarks>
        /// <b>This is the one range a progressive client must fetch before it can locate anything.</b>
        /// The hint stream is what turns "I have the first N bytes" into "I can draw page 1", and it
        /// is deliberately placed where a reader can ask for it without the cross-reference table.
        /// </remarks>
        public long HintStreamOffset { get; }

        /// <summary>Table F.1 <c>/H</c>, second entry — the primary hint stream's length.</summary>
        public long HintStreamLength { get; }

        /// <summary>
        /// Table F.1 <c>/H</c>, third entry, or 0 — where the overflow hint stream begins.
        /// </summary>
        /// <remarks>
        /// Optional, and its absence is not a defect: the pair exists only for a file whose hint
        /// data is split across the two halves of the layout.
        /// </remarks>
        public long OverflowHintOffset { get; }

        /// <summary>Table F.1 <c>/H</c>, fourth entry, or 0 — the overflow hint stream's length.</summary>
        public long OverflowHintLength { get; }

        /// <summary>Table F.1 <c>/O</c> — the object number of the first page's page object.</summary>
        public int FirstPageObjectNumber { get; }

        /// <summary>
        /// Table F.1 <c>/E</c> — the offset just past the end of the first page's objects, so a
        /// client knows how many bytes make the first page renderable.
        /// </summary>
        public long FirstPageEndOffset { get; }

        /// <summary>Table F.1 <c>/N</c> — the number of pages the producer recorded.</summary>
        public int PageCount { get; }

        /// <summary>
        /// Table F.1 <c>/T</c> — the offset of the first entry of the main cross-reference table,
        /// which sits at the <i>front</i> of a linearized file rather than at the end.
        /// </summary>
        public long MainCrossReferenceOffset { get; }
    }
}
