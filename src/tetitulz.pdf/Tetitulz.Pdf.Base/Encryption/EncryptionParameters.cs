// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Encryption
{
    /// <summary>
    /// Parameters for PDF encryption.
    /// </summary>
    public sealed class EncryptionParameters
    {
        /// <summary>
        /// The user password (required to open the document). Empty string means no password to open.
        /// </summary>
        public string UserPassword { get; set; } = "";

        /// <summary>
        /// The owner password (required to change permissions). Must be set.
        /// </summary>
        public string OwnerPassword { get; set; }

        /// <summary>
        /// Document permissions when opened with the user password.
        /// </summary>
        public PdfPermissions Permissions { get; set; } = PdfPermissions.All;

        /// <summary>
        /// Encryption algorithm to use.
        /// </summary>
        public EncryptionAlgorithm Algorithm { get; set; } = EncryptionAlgorithm.AES128;

        /// <summary>
        /// Whether to encrypt the document metadata stream.
        /// When false, the /Metadata stream remains in plaintext (readable by search engines).
        /// Default is true.
        /// </summary>
        public bool EncryptMetadata { get; set; } = true;

        /// <summary>
        /// Whether to protect the encrypted document's integrity with a PDF MAC token
        /// (ISO/TS 32004, PDF 2.0) — a standalone (unsigned-revision) token written to the
        /// trailer's <c>/AuthCode</c> dictionary, bound to the byte range of the whole file via
        /// HMAC-SHA256 under a key derived from the document's own file encryption key. Detects
        /// tampering with the ciphertext independently of any digital signature. Default is
        /// false; enabling it raises the minimum PDF version to 2.0 and clears permission bit 13
        /// (§7.6.4.2), which signals to a PDF MAC-aware reader that a token is required in every
        /// revision of the document.
        /// <para><b>Needs <see cref="EncryptionAlgorithm.AES256"/> or
        /// <see cref="EncryptionAlgorithm.AES256GCM"/></b> — <see cref="Algorithm"/>'s default,
        /// AES-128, is refused with it. ISO/TS 32004 allows a token only where the encryption
        /// dictionary's <c>/V</c> is at least 5: below it nothing authenticates the permission bit,
        /// so a token could be removed from the file without a reader being able to tell.</para>
        /// </summary>
        public bool EnablePdfMac { get; set; }

        /// <summary>
        /// Encrypt only the document's embedded files — the "encrypt only file attachments"
        /// (ISO 32000-2 §7.6.5): the document opens and reads without a password, and the password
        /// is asked for an attachment. Written as <c>/StmF</c> and <c>/StrF</c> <c>/Identity</c> with
        /// <c>/EFF</c> naming the crypt filter. Needs a crypt-filter algorithm (AES-128 or later);
        /// refused with RC4. Default is false.
        /// </summary>
        public bool EncryptOnlyAttachments { get; set; }

        /// <summary>
        /// Whether the encrypted file is written in the compressed layout — object streams and a
        /// cross-reference stream (ISO 32000-1 §7.5.7, §7.5.8), each object stream encrypted whole
        /// under its own number with its members in the clear inside it. <see langword="null"/>
        /// (the default) keeps the input's layout: a file that holds object streams is written with
        /// them, a classic one with a classic table. <see langword="true"/> packs any input,
        /// <see langword="false"/> unpacks any. The compressed layout declares PDF 1.5 at least.
        /// </summary>
        public bool? UseObjectStreams { get; set; }
    }

    /// <summary>
    /// Supported PDF encryption algorithms.
    /// </summary>
    public enum EncryptionAlgorithm
    {
        /// <summary>RC4 40-bit encryption (PDF 1.1+, maximum compatibility with legacy readers).</summary>
        RC4_40,

        /// <summary>RC4 128-bit encryption (PDF 1.4+, widely compatible).</summary>
        RC4_128,

        /// <summary>AES 128-bit encryption (PDF 1.6+).</summary>
        AES128,

        /// <summary>AES 256-bit encryption (PDF 2.0, highest security).</summary>
        AES256,

        /// <summary>
        /// AES-256 in Galois/Counter Mode (ISO/TS 32003, PDF 2.0): authenticated encryption, so
        /// tampered ciphertext is detected rather than silently decrypted to garbage. Password
        /// algorithms and key derivation are identical to <see cref="AES256"/> (revision 6); only
        /// the per-object cipher differs (/CFM /AESV4, security handler V=6/R=7).
        /// </summary>
        AES256GCM,

        /// <summary>No encryption: the Identity crypt filter or /CFM /None. Bytes pass
        /// through unchanged. Distinct from RC4_40 so a genuine no-op is never RC4-processed.</summary>
        Identity,

        /// <summary>
        /// RC4 40-bit encryption under security-handler revision 3 (PDF 1.4: <c>/V 1 /R 3 /Length 40</c>,
        /// the form ISO 32000-1's own PDF carries): the cipher and key length of <see cref="RC4_40"/>,
        /// the key derivation and <c>/U</c> of revision 3 — ISO 32000-1 §7.6.3.3 Algorithm 2's fifty
        /// MD5 rounds and Algorithm 5. Written to keep an input's revision through
        /// <c>PdfEncryptedRoundTrip</c>, which wrote revision 2 for such a document (GAPS §19h);
        /// for a new document choose <see cref="RC4_40"/> or <see cref="RC4_128"/>.
        /// </summary>
        /// <remarks>
        /// <b>Last on purpose.</b> An enum member's value is compiled into every caller as a constant,
        /// so a member inserted before <see cref="AES128"/> renumbered it: a caller built against the
        /// earlier assembly asking for AES-128 (2) would have been handed this, 40-bit RC4, with no
        /// error. New members go at the end; <c>EncryptionAlgorithm_HasExpectedValues</c> pins the rest.
        /// </remarks>
        RC4_40R3
    }
}
