// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// Builds the trailer of a revision appended to an existing file: carry the original's keys
    /// forward, restate <c>/Size</c>, and chain to the previous cross-reference section — but only
    /// when there is one this revision can legally name.
    /// </summary>
    /// <remarks>
    /// <para><b>One implementation</b>, for <c>Base.Operations</c>' writers
    /// (<c>PdfPageContentAppender</c>, <c>PdfBookmarksManager</c>, <c>PdfPageManipulator</c>,
    /// <c>PdfPageLabels</c>, <c>PdfMetadataWriter</c>, <c>PdfIccEmbedder</c>,
    /// <c>PdfColorSpaceConverter</c>), <c>IncrementalUpdateWriter</c> itself and
    /// <c>Tetitulz.Pdf.Signing</c> — copies of a trailer do not agree.</para>
    ///
    /// <para><b>The tenth was found later, and how it hid is the reusable part.</b> Every copy above
    /// lived in a named helper — <c>BuildTrailer</c>, <c>MergeTrailer</c> — so a grep for the name
    /// found them all. <c>PdfOperations.RotateAllPages</c> had <see cref="ForUpdate"/>'s body written
    /// out <i>inline</i>, three statements in the middle of a longer method, and no identifier to
    /// search for; it was found only by searching for the <b>rule</b> (a loop copying trailer entries
    /// forward, then a <c>/Size</c> restated). It was harmless, because that site passes its trailer
    /// to <c>IncrementalUpdateWriter.WriteUpdate</c>, which routes it through
    /// <see cref="ForAppendedRevision"/> and strips the keys again — but it had already drifted
    /// (no deny list), and it was one reordering away from mattering. <b>A consolidation's
    /// completeness check has to search for the behaviour, not the identifier.</b></para>
    ///
    /// <para><b>The <c>/Prev</c> guard is the half that matters, and only Core had it.</b> A
    /// <c>/Prev 0</c> points at the <c>%PDF</c> header and an out-of-range one points past the end
    /// of the file; either is a claim a reader cannot follow.
    /// <c>IncrementalUpdateWriter</c> has omitted the key in that case for some time, with a comment
    /// explaining why. <c>PdfSigner</c> and <c>VisualSignatureBuilder</c> build and write their own
    /// trailers directly through <c>PdfWriter.WriteTrailer</c>, so Core's guard never ran for them,
    /// and they wrote <c>/Prev</c> unconditionally. The value arrives as 0 whenever
    /// <c>startxref</c> cannot be located — the parser reports 0 when it recovered a file by
    /// scanning, and <c>PdfSigner</c>'s own 1&#160;KB tail scan reports 0 for the same reason.</para>
    ///
    /// <para><b>What omitting the key does not do, because it was measured.</b> It does not rescue
    /// the document. When the previous section is unnameable the appended revision declares only
    /// the objects it wrote, and nothing re-declares the rest: through <c>PdfSigner</c> the result
    /// reopens with a catalog and zero pages, and through <c>IncrementalUpdateWriter</c>'s
    /// six-argument overload it does not reopen at all. Core's recovery pass
    /// (<c>AddRecoveryEntries</c>, which re-declares the whole document when it cannot chain) exists
    /// only on the newer options overload, and <c>PdfSigner</c> has no equivalent. So the guard buys
    /// exactly one thing — the trailer stops asserting a history that is not there — and that is
    /// worth having on its own, because a <c>/Prev 0</c> is indistinguishable from a deliberate
    /// chain into the header. <b>The page does not survive, on either path</b>; the remaining
    /// gap is recorded in GAPS.md.</para>
    ///
    /// <para>A leaf by construction: no static fields, so no type initializer.</para>
    /// </remarks>
    public static class PdfTrailerBuilder
    {
        /// <summary>
        /// Length in bytes of a trailer <c>/ID</c> element. §14.4 recommends 16, the length of an
        /// MD5 digest, which is what every producer emits.
        /// </summary>
        public const int FileIdLengthBytes = 16;

        /// <summary>
        /// A fresh §14.4 file identifier for a document being written from scratch: a two-element
        /// array whose halves are the same 16 random bytes.
        /// </summary>
        /// <remarks>
        /// <para><b>Both elements are identical on purpose, and only for a NEW document.</b> §14.4
        /// gives <c>/ID[0]</c> as the identifier of the original document and <c>/ID[1]</c> as that
        /// of the current version; for a file that has no earlier version those are the same thing.
        /// A writer appending a <i>revision</i> must not use this — it has to carry the original's
        /// <c>/ID[0]</c> forward, which is what <see cref="ForUpdate"/> does by copying the key.</para>
        ///
        /// <para>Four writers built this themselves — <c>PdfDocumentBuilder</c>, <c>PdfNup</c>,
        /// <c>PdfTaggedDocumentBuilder</c> and the <c>PdfMerger</c> plugin — and had already drifted
        /// in the small way these things do: one named the length as a constant and three wrote
        /// <c>16</c>. That is harmless until someone decides 16 is the wrong number.</para>
        ///
        /// <para><b>The bytes are cryptographically random, and the alternative is worth naming.</b>
        /// A content-derived identifier (as <c>PdfAccessibilityFixer.SynthesiseFileId</c> uses) makes
        /// output reproducible, which is a real virtue — but it also makes two documents with the
        /// same content share an identifier, and §14.4's purpose is for a reader to tell one file
        /// from another. A writer that wants reproducibility should derive its own and say so,
        /// rather than this method quietly becoming deterministic for everyone.</para>
        /// </remarks>
        public static PdfArray NewFileId()
        {
            byte[] fileId = new byte[FileIdLengthBytes];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                rng.GetBytes(fileId);

            var id = new PdfArray();
            id.Add(new PdfString(fileId, true));
            id.Add(new PdfString(fileId, true));
            return id;
        }

        /// <summary>
        /// Whether <paramref name="previousXrefOffset"/> names a cross-reference section this
        /// revision may chain to: it must be a real offset, and it must lie before the revision
        /// doing the naming.
        /// </summary>
        /// <param name="previousXrefOffset">
        /// Candidate <c>/Prev</c> value. Callers commonly reach this with <c>0</c> — that is what a
        /// parser reports when it had to recover a damaged file by scanning, and what a tail scan
        /// reports when <c>startxref</c> sits outside its window. Omitting the key is the honest
        /// encoding of "there is no previous section I can name".
        /// </param>
        /// <param name="thisRevisionOffset">
        /// Where the revision being written begins — its first appended byte, or the offset of its
        /// own cross-reference section. Either is a valid upper bound; pass whichever the caller
        /// actually has.
        /// </param>
        public static bool IsUsablePreviousXref(long previousXrefOffset, long thisRevisionOffset)
            => previousXrefOffset > 0 && previousXrefOffset < thisRevisionOffset;

        /// <summary>
        /// Carries <paramref name="original"/> forward and restates <c>/Size</c>, dropping the keys
        /// the appended revision restates for itself.
        /// </summary>
        /// <remarks>
        /// <para><c>/Prev</c> is dropped rather than copied: the original's value names the section
        /// before <i>it</i>, which is not what the new revision is chaining to. Use
        /// <see cref="ForAppendedRevision"/> to set the correct one, or leave it to
        /// <see cref="IncrementalUpdateWriter"/>, which applies the same rule to whatever trailer it
        /// is handed.</para>
        ///
        /// <para><c>/XRefStm</c> is dropped too. It is a hybrid-reference file's offset (§7.5.8.4)
        /// of a cross-reference <i>stream</i> belonging to the section that declared it, and a new
        /// section must not claim a stream it did not write. Carrying it forward would not corrupt
        /// anything on the append path — this parser reaches the same stream through <c>/Prev</c>
        /// either way — so this is conformance, not a repair.</para>
        /// </remarks>
        /// <param name="original">The trailer of the revision being appended to.</param>
        /// <param name="newSize">
        /// One more than the highest object number the file will contain after this revision.
        /// </param>
        public static PdfDictionary ForUpdate(PdfDictionary original, int newSize)
        {
            var trailer = new PdfDictionary();

            if (original != null)
            {
                foreach (var kvp in original)
                {
                    if (IsCrossReferenceSectionKey(kvp.Key)) continue;
                    trailer.Set(kvp.Key, kvp.Value);
                }
            }

            trailer.Set(PdfName.Size, new PdfNumber(newSize));
            return trailer;
        }

        /// <summary>
        /// Keys that describe the cross-reference <b>section they came from</b> rather than the
        /// document, and so must never be carried into a new one.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this is more than <c>/Prev</c> and <c>/XRefStm</c>.</b> For a PDF 1.5+ file
        /// the parser merges the xref <i>stream's own dictionary</i> into
        /// <c>PdfDocument.Trailer</c> — there is no separate trailer to read — so that dictionary
        /// carries <c>/Type /XRef</c>, <c>/W</c>, <c>/Index</c>, <c>/Filter</c>,
        /// <c>/DecodeParms</c> and a now-stale <c>/Length</c>. Copying it wholesale in front of a
        /// classic <c>xref</c> table emitted
        /// <c>trailer &lt;&lt; /Type /XRef /W [1 4 2] /Filter /FlateDecode /Length 342 … &gt;&gt;</c>,
        /// which §7.5.5 does not permit — from every operation that appends a revision to a modern
        /// document.</para>
        ///
        /// <para><b>A deny list, because ISO 32000-2 §7.5.6 asks for everything else.</b> "The added
        /// trailer shall contain all the entries except the Prev entry (if present) from the previous
        /// trailer, whether modified or not." An allow list of four keys would drop whatever else a
        /// producer put there; <c>IncrementalUpdateWriter</c> goes through this method too.</para>
        ///
        /// <para><b><c>/AuthCode</c> belongs to its revision.</b> ISO/TS 32004 has its
        /// <c>/ByteRange</c> cover the whole file as it stood when that revision was sealed (6.5.1), and
        /// only the most recent revision's is taken into account (B.1). Carried into a revision nothing
        /// re-sealed, it would describe bytes that no longer end the file; a writer that seals sets its
        /// own.</para>
        /// </remarks>
        private static bool IsCrossReferenceSectionKey(PdfName key)
            => key == PdfName.Prev
            || key == PdfName.XRefStm
            || key == PdfName.AuthCode
            || key == PdfName.Type          // /XRef — the stream's own type
            || key == PdfName.W
            || key == PdfName.Index
            || key == PdfName.Filter
            || key == PdfName.DecodeParms
            || key == PdfName.Length;

        /// <summary>
        /// <see cref="ForUpdate"/> plus the <c>/Prev</c> chain, set only when
        /// <see cref="IsUsablePreviousXref"/> allows it.
        /// </summary>
        /// <param name="original">The trailer of the revision being appended to.</param>
        /// <param name="newSize">One more than the highest object number in the finished file.</param>
        /// <param name="previousXrefOffset">Candidate <c>/Prev</c> value; see the guard.</param>
        /// <param name="thisRevisionOffset">Where this revision begins; see the guard.</param>
        public static PdfDictionary ForAppendedRevision(
            PdfDictionary original, int newSize, long previousXrefOffset, long thisRevisionOffset)
        {
            var trailer = ForUpdate(original, newSize);

            if (IsUsablePreviousXref(previousXrefOffset, thisRevisionOffset))
                trailer.Set(PdfName.Prev, new PdfNumber(previousXrefOffset));

            return trailer;
        }
    }
}
