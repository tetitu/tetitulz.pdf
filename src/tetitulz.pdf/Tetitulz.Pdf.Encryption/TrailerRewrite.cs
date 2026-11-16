// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// Which entries of a source document's trailer a whole-file rewrite — <see cref="PdfDecryptor"/>,
    /// <see cref="PdfEncryptor"/>, and <see cref="PdfPublicKeyEncryptor"/> through the latter — does not carry into
    /// the classic trailer it writes.
    /// </summary>
    /// <remarks>
    /// <para>Everything else is carried, in the spirit of ISO 32000-2 §7.5.6 ("all the entries except the Prev entry
    /// ... whether modified or not"). What is not:</para>
    /// <list type="bullet">
    ///   <item><description><c>/Prev</c> and <c>/XRefStm</c> — byte offsets into the source's layout, which the
    ///   rewrite invalidates; a reader that prefers the cross-reference stream would seek there and find unrelated
    ///   object data.</description></item>
    ///   <item><description><c>/Type</c>, <c>/W</c>, <c>/Index</c>, <c>/Filter</c>, <c>/DecodeParms</c>,
    ///   <c>/Length</c> — a cross-reference stream's own dictionary, which the parser merges into the trailer it
    ///   exposes. §7.5.5 does not allow them in a classic trailer, and a stale <c>/Length</c> describes a stream the
    ///   output does not contain.</description></item>
    ///   <item><description><c>/Encrypt</c> — the decryptor removes the protection; the encryptors mint their
    ///   own.</description></item>
    ///   <item><description><c>/AuthCode</c> — a PDF MAC token belongs to the revision that sealed it. ISO/TS 32004
    ///   §6.5.1 has its <c>/ByteRange</c> cover the entire file as it stood when sealed, Annex B.1 takes only the
    ///   current revision's token into account, and Table 5 requires <c>/V</c> "at least 5" wherever the entry is
    ///   present. Carried into a decrypted file it sits beside no <c>/Encrypt</c> at all; carried into a re-encrypted
    ///   one it describes bytes that no longer end the file, which Annex B.2.2.2 reads as possible tampering. A writer
    ///   that seals sets its own token after this rule has run. Core's <c>PdfTrailerBuilder</c> drops it from an
    ///   appended revision for the same reason.</description></item>
    /// </list>
    /// <para><b>One rule, for both writers.</b> Two would drift: an encryptor dropping only <c>/Type</c> and
    /// <c>/XRefStm</c> writes <c>/W</c> and its siblings into classic trailers.</para>
    /// </remarks>
    internal static class TrailerRewrite
    {
        /// <summary>Whether <paramref name="key"/> is one of the source-trailer entries a rewrite must not carry.</summary>
        internal static bool IsNotCarried(PdfName key)
            => key == PdfName.Prev
            || key == PdfName.XRefStm
            || key == PdfName.Encrypt
            || key == PdfName.AuthCode
            || key == PdfName.Type
            || key == PdfName.W
            || key == PdfName.Index
            || key == PdfName.Filter
            || key == PdfName.DecodeParms
            || key == PdfName.Length;
    }
}
