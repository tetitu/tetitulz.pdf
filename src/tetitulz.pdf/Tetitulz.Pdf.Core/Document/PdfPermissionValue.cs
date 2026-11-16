// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// Reading the encryption dictionary's <c>/P</c> permission bitmask (§7.6.3, Table 22).
    ///
    /// <para><b>One reader for every caller.</b> <c>/P</c> is defined as a signed 32-bit integer,
    /// but producers write the <i>unsigned</i> spelling of the same bit pattern —
    /// <c>/P 4294963392</c> for <c>-3904</c> — and <see cref="PdfDictionary.GetInteger"/>'s
    /// <c>(int)</c> cast on a <c>double</c> <b>saturates</b> to <c>int.MaxValue</c> on .NET Core
    /// 3.0+ rather than truncating, so through it a legal bitmask would come back as "every
    /// permission granted".</para>
    /// </summary>
    /// <remarks>
    /// <para><b>What each copy did with the wrong number:</b></para>
    /// <list type="bullet">
    ///   <item><b>Key derivation.</b> Algorithm 2 hashes the four bytes of <c>/P</c>, low byte
    ///     first, so for R ≤ 4 a saturated value derives the <i>wrong file key</i> — the document
    ///     will not open with its own correct password. This is the expensive one: it is not a
    ///     permissions question at all.</item>
    ///   <item><b><c>EncryptionInfo.PValue</c>.</b> Reported <c>int.MaxValue</c> — all permissions
    ///     set — for a document that withheld most of them.</item>
    ///   <item><b><c>/Perms</c> authentication.</b> Compared the saturated value against the
    ///     authenticated one and announced <c>PMismatch</c>: an accusation of tampering against a
    ///     file nobody had touched, which is exactly the failure that check's own comment says it
    ///     was written to avoid.</item>
    ///   <item><b>PDF/UA UA06.</b> Read bit 10 out of <c>int.MaxValue</c>, so
    ///     "content extraction for accessibility is permitted" passed for every such document
    ///     whether it was or not.</item>
    /// </list>
    ///
    /// <para><b>The low 32 bits are the answer, and the spec says so.</b> §7.6.4.2's Algorithm 9
    /// stores the low-order 32 bits of <c>/P</c> in <c>/Perms</c> bytes 0–3, and Algorithm 2 hashes
    /// those same four bytes — so taking the low 32 bits accepts both spellings and is what the
    /// authenticated copy is compared against either way.</para>
    ///
    /// <para><b><c>null</c> is a third answer, not zero.</b> An absent, indirect or non-numeric
    /// <c>/P</c> is "I could not read it"; defaulting it to 0 would make every permission look
    /// withheld and make the <c>/Perms</c> comparison announce tampering. Callers that must have a
    /// number choose their own fallback and say why.</para>
    /// </remarks>
    public static class PdfPermissionValue
    {
        /// <summary>
        /// The <c>/P</c> entry of <paramref name="encryptDict"/> as a signed 32-bit bitmask, or
        /// <c>null</c> when it is absent or is not a direct number.
        /// </summary>
        public static int? Read(PdfDictionary encryptDict)
            => encryptDict == null ? null : FromNumber(encryptDict[PdfName.P]);

        /// <summary>
        /// The same rule for a <c>/P</c> value a caller has already resolved — the PDF/UA checker
        /// follows an indirect reference before reading it, which <see cref="Read"/> deliberately
        /// does not do (it has no document to resolve against).
        /// </summary>
        /// <remarks>
        /// Through <see cref="PdfNumber.FlagWord"/>, the one reading of a PDF bit field, which
        /// annotation <c>/F</c> shares. A <c>/P</c> that is not an integer is <c>null</c>: it used
        /// to be cast unchecked, so <c>/P 1e30</c> saturated to all ones — every permission granted,
        /// and PDF/UA's UA06 passed a document whose permissions nobody could read.
        /// </remarks>
        public static int? FromNumber(PdfObject value)
            => (value as PdfNumber)?.FlagWord;
    }
}
