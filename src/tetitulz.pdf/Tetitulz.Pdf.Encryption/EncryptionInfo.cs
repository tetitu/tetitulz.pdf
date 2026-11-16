// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// Pre-parsed encryption parameters from an encrypted PDF.
    /// Used for fast password validation without re-parsing the PDF each time.
    /// </summary>
    public sealed class EncryptionInfo
    {
        public int V { get; set; }
        public int R { get; set; }
        public int KeyLengthBits { get; set; }
        public int PValue { get; set; }
        public byte[] OValue { get; set; }
        public byte[] UValue { get; set; }
        public byte[] FileId { get; set; }
        public bool EncryptMetadata { get; set; }

        /// <summary>
        /// The full encryption dictionary, needed for AES-256 key derivation
        /// which reads /UE, /OE values.
        /// </summary>
        public PdfDictionary EncryptDict { get; set; }

        /// <summary>
        /// The <c>/CFM</c> of the standard crypt filter (<c>/CF</c> → <c>/StdCF</c> → <c>/CFM</c>),
        /// resolved while the document was still open.
        /// </summary>
        /// <remarks>
        /// <para><b>Internal</b>, so the public surface is unchanged. It exists because that walk is
        /// three consecutive dictionary reads and §7.3.10 permits any of them to be indirect —
        /// which made a reproducible AES-128 document refused for sanitize/optimize/redact with a
        /// reason untrue of the file.</para>
        ///
        /// <para><b>A resolved VALUE, not a resolver delegate</b>, and the difference is not
        /// stylistic. A <c>Func&lt;PdfObject, PdfObject&gt;</c> closing over the <c>PdfDocument</c>
        /// would outlive the <c>using</c> block which owns it, and the walk would throw
        /// <c>ObjectDisposedException: Cannot access a closed Stream</c> — the same shape as the
        /// <c>FontCache</c> use-after-dispose rule. Resolve eagerly, while the document is alive, and carry the answer rather than the means.</para>
        /// </remarks>
        internal string StdCryptFilterMethod { get; set; }

        /// <summary>
        /// Whether the trailer of the document this was read from carries an <c>/AuthCode</c>: an ISO/TS 32004 PDF MAC
        /// token (Table 5).
        /// </summary>
        /// <remarks>
        /// <b>Internal</b>, like <see cref="StdCryptFilterMethod"/>, so the public surface is unchanged. It is the one fact
        /// <see cref="PdfMacTokenSeal.SaltWhenProtected(PdfDictionary, int, bool)"/> needs that the rest of this type does
        /// not carry — the salt is in <see cref="EncryptDict"/> and bit 13 in <see cref="PValue"/>. Without it
        /// <c>PdfEncryptedRoundTrip</c> answered "does this document use PDF MAC?" from the other two, and re-encrypted
        /// a document holding a token with bit 13 set without any token, where an appended revision sealed it. An
        /// instance a caller builds itself leaves it false, and is answered from the salt and bit 13 as before.
        /// </remarks>
        internal bool TrailerHasAuthCode { get; set; }

        /// <summary>
        /// The security handler named by <c>/Filter</c> — <c>"Standard"</c> for a password-encrypted
        /// document, <c>"Adobe.PubSec"</c> for a certificate-encrypted one, or <c>null</c> when the
        /// key is absent.
        /// <para>
        /// It is here so the pre-flight answer names the handler: switching on <c>/V</c> and the
        /// <c>/StdCF</c> method alone would report an <c>/Adobe.PubSec</c> document — which this
        /// assembly writes, via <c>PdfPublicKeyEncryptor</c> — as <c>/Standard</c> AES-256 with an
        /// empty password. <c>PdfDecryptor.ResolveEncryption</c> rejects a non-<c>/Standard</c>
        /// handler; this makes the same fact available before it runs.
        /// </para>
        /// </summary>
        public string Filter { get; set; }
    }
}
