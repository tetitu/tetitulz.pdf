// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// The trailer's <c>/ID</c> (§14.4): reading an existing one, and deriving a deterministic
    /// replacement when there is none.
    /// </summary>
    /// <remarks>
    /// <para><b>One implementation, because the two writers disagreed about reading it.</b>
    /// <c>IncrementalUpdateWriter</c> resolved an indirect <c>/ID</c> before testing its shape;
    /// <c>PdfCompressedFileWriter</c> used <c>trailer[PdfName.ID] is PdfArray</c>, and
    /// <see cref="PdfDictionary"/>'s indexer does not follow a reference — so <c>/ID 7 0 R</c> read
    /// as "not an array", which that writer treats as "no identifier at all" and answers by
    /// **synthesising a new one**.</para>
    ///
    /// <para><b>That silently changes the document's identity.</b> §14.4 makes the first element
    /// permanent for the life of the file: readers use the pair to tell one document's revisions
    /// from another's, and an encrypted file's key derivation reads the first element. A rewrite
    /// that invents a fresh identifier produces a file that no longer matches its own earlier
    /// revisions — and it is the "absence is meaningful" trap, because a failed shape test is
    /// indistinguishable here from a document that never had an <c>/ID</c>.</para>
    ///
    /// <para><b>Both halves are 16 bytes and both writers said so separately</b>, in two private
    /// constants that happened to agree. They are one constant now.</para>
    /// </remarks>
    /// <remarks>
    /// <b>Public because Core's <c>InternalsVisibleTo</c> names no plugin.</b>
    /// <c>Plugins.Conformance.PdfAccessibilityFixer</c> synthesises an <c>/ID</c> too, and while
    /// this was <c>internal</c> it could not reach the rule — so it carried its own copy, which
    /// wrote the SAME 16 bytes into both halves. Same reasoning, and the same remedy, as
    /// <c>Core.Forms.AcroFieldClassification</c>.
    /// </remarks>
    public static class PdfFileId
    {
        /// <summary>
        /// Bytes per half of the <c>/ID</c> array. §14.4 specifies the file identifier as a
        /// 16-byte (128-bit) value.
        /// </summary>
        public const int HalfLengthBytes = 16;

        /// <summary>
        /// The <c>/ID</c> array when <paramref name="idObj"/> is a well-formed two-string one, else
        /// <c>null</c>.
        /// </summary>
        /// <param name="idObj">The raw trailer value — may be a <see cref="PdfReference"/>.</param>
        /// <param name="resolve">
        /// Follows an indirect reference. May be <c>null</c> when the caller has nothing to resolve
        /// against, in which case an indirect <c>/ID</c> is reported as absent — the honest answer,
        /// and the one that makes the caller's fallback explicit rather than accidental.
        /// </param>
        public static PdfArray Existing(PdfObject idObj, Func<PdfObject, PdfObject> resolve)
        {
            if (idObj is PdfReference && resolve != null) idObj = resolve(idObj);

            return idObj is PdfArray id && id.Count >= 2
                && id[0] is PdfString && id[1] is PdfString
                ? id
                : null;
        }

        /// <summary>
        /// Splits <paramref name="digest"/> into the two halves of an <c>/ID</c> array.
        /// </summary>
        /// <remarks>
        /// A pure function of the digest: no <see cref="Random"/> and no timestamp, so two runs over
        /// the same content produce the same file — which is what makes a byte-comparison test
        /// possible at all. A <c>null</c> or short digest yields zero-filled halves, which is
        /// degenerate but still deterministic.
        /// </remarks>
        public static PdfArray FromDigest(byte[] digest)
        {
            var first = new byte[HalfLengthBytes];
            var second = new byte[HalfLengthBytes];

            if (digest != null)
            {
                Array.Copy(digest, 0, first, 0, Math.Min(HalfLengthBytes, digest.Length));
                if (digest.Length > HalfLengthBytes)
                {
                    Array.Copy(digest, HalfLengthBytes, second, 0,
                        Math.Min(HalfLengthBytes, digest.Length - HalfLengthBytes));
                }
            }

            var id = new PdfArray();
            id.Add(new PdfString(first, isHex: true));
            id.Add(new PdfString(second, isHex: true));
            return id;
        }
    }
}
