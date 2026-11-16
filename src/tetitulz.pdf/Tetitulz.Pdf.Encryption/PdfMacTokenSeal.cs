// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// The <see cref="StandalonePdfMacSeal"/> for a document's own file encryption key and
    /// <c>/KDFSalt</c>: Core places and digests, <see cref="PdfMacToken"/> builds the token.
    /// </summary>
    internal sealed class PdfMacTokenSeal : StandalonePdfMacSeal
    {
        private readonly byte[] _fileEncryptionKey;
        private readonly byte[] _kdfSalt;

        internal PdfMacTokenSeal(byte[] fileEncryptionKey, byte[] kdfSalt)
        {
            _fileEncryptionKey = fileEncryptionKey ?? throw new ArgumentNullException(nameof(fileEncryptionKey));
            _kdfSalt = kdfSalt ?? throw new ArgumentNullException(nameof(kdfSalt));

            // A throwaway build over a zero digest: the DER length depends only on fixed-size fields
            // (§6.3.7 forbids the unauthenticated attributes that could vary it), never on their values.
            TokenLength = PdfMacToken.BuildStandaloneToken(fileEncryptionKey, kdfSalt, new byte[32], null).Length;
        }

        public override int TokenLength { get; }

        public override byte[] BuildToken(byte[] dataDigest)
            => PdfMacToken.BuildStandaloneToken(_fileEncryptionKey, _kdfSalt, dataDigest, null);

        /// <summary>
        /// The seal an appended revision of <paramref name="document"/> needs, or null when the
        /// document does not use PDF MAC.
        /// </summary>
        /// <remarks>
        /// <para><b>When.</b> Whenever <see cref="SaltWhenProtected(PdfDictionary, int, bool)"/> says the document is
        /// protected. §6.5.1 requires the most recent unsigned revision's <c>/ByteRange</c> to cover the entire file,
        /// which the previous token stops doing the moment anything is appended — Annex B.2.2.2 names exactly that,
        /// "another PDF processor failed to apply a new MAC after incrementally updating the document", as what a stale
        /// token indicates.</para>
        /// <para>The key is the file encryption key <see cref="PdfDecryptor.ResolveEncryption"/> derives
        /// — the same one the appended objects are encrypted with (§6.4: "the file encryption key used by
        /// the document's security handler").</para>
        /// </remarks>
        internal static PdfMacTokenSeal ForAppendedRevision(PdfDocument document, string password)
            => ForAppendedRevision(document, PdfDecryptionCredential.Password(password));

        /// <summary>
        /// As above, for a document opened with any credential. §6.4 keys the token on the file
        /// encryption key <b>whichever security handler produced it</b>, so the seal is the same
        /// and only the route to the key differs.
        /// </summary>
        internal static PdfMacTokenSeal ForAppendedRevision(
            PdfDocument document, PdfDecryptionCredential credential)
        {
            byte[] salt = SaltWhenProtected(document);
            if (salt == null) return null;

            return new PdfMacTokenSeal(credential.Resolve(document).Key, salt);
        }

        /// <summary>
        /// The key material a §6.5.2 <c>AttachedToSig</c> token needs, or false when the document is
        /// not protected by PDF MAC.
        /// </summary>
        /// <remarks>
        /// <b>The same two values <see cref="ForAppendedRevision(PdfDocument, PdfDecryptionCredential)"/> derives, handed over raw.</b>
        /// A token attached to a signature cannot be built when the revision is written — its
        /// <c>signatureDigest</c> does not exist until the CMS does — so the signer carries the key
        /// and salt to the completion step instead of carrying a seal. Deriving them here keeps
        /// "which key a PDF MAC is computed with" one answer: §6.4's "the file encryption key used
        /// by the document's security handler", which is what the appended objects are encrypted
        /// with too.
        /// </remarks>
        internal static bool TryAttachedToSigMaterial(
            PdfDocument document, string password, out byte[] fileEncryptionKey, out byte[] kdfSalt)
        {
            fileEncryptionKey = null;
            kdfSalt = SaltWhenProtected(document);
            if (kdfSalt == null) return false;

            PdfDecryptor.ResolvedEncryption resolved =
                PdfDecryptor.ResolveEncryption(document, password ?? string.Empty);   // SASLprep is the V≥5 derivation's
            fileEncryptionKey = resolved.Key;
            return true;
        }

        /// <summary>
        /// The document's 32-byte <c>/KDFSalt</c> when it is protected by PDF MAC, otherwise null: the decision
        /// <see cref="ForAppendedRevision(PdfDocument, PdfDecryptionCredential)"/> makes, without the key derivation, read off an open document. See
        /// <see cref="SaltWhenProtected(PdfDictionary, int, bool)"/> for the decision itself.
        /// </summary>
        internal static byte[] SaltWhenProtected(PdfDocument document)
        {
            PdfDictionary encrypt = document.EncryptionDictionary;
            int permissions = PdfPermissionValue.FromNumber(document.ResolveObject(encrypt?[PdfName.P])) ?? 0;
            return SaltWhenProtected(encrypt, permissions, TrailerHasToken(document.Trailer));
        }

        /// <summary>
        /// The one answer in this assembly to "is this document protected by PDF MAC": its 32-byte <c>/KDFSalt</c> when
        /// it is, otherwise null. An appended revision is sealed on it (<see cref="ForAppendedRevision(PdfDocument, PdfDecryptionCredential)"/>), and the whole-file
        /// round trip re-encrypts with a token on it (<c>PdfEncryptedRoundTrip.TryDeriveParameters</c>).
        /// </summary>
        /// <param name="encrypt">The encryption dictionary, or null.</param>
        /// <param name="permissions">
        /// Its <c>/P</c>. A caller that cannot read one passes 0, as <see cref="EncryptionInfo.PValue"/> and the key
        /// derivation already read it — so bit 13 reads clear and the protection is kept, never dropped on a guess.
        /// (<c>PdfDecryptor.ValidatePerms</c> refuses that reading on purpose: there, 0 would accuse an untouched file of
        /// tampering.)
        /// </param>
        /// <param name="trailerHasToken">Whether the trailer carries an <c>/AuthCode</c> — see <see cref="TrailerHasToken"/>.</param>
        /// <remarks>
        /// <para><b>Protected means a 32-byte salt and either permission bit 13 clear or a token already present.</b>
        /// ISO/TS 32004 Table 2 requires <c>/KDFSalt</c> "in documents that make use of PDF MAC", and §6.4 keys the token
        /// from exactly those 32 bytes. Bit 13 is the <i>requirement</i> — Table 3: "When zero, indicates that a PDF MAC
        /// token is required to be present in all revisions of the document" — not the definition of use: §6.5.1 binds
        /// any "document with a PDF MAC token", bit 13 or not, and Annex B.2.2.2 reads a token that stops short of the end
        /// of the file as one that "has become stale, e.g. because another PDF processor failed to apply a new MAC after
        /// incrementally updating the document".</para>
        /// <para><b>No salt, no seal.</b> A document with bit 13 clear but no <c>/KDFSalt</c> never carried a verifiable
        /// token, and producers wrote <c>/P</c> values with that bit clear long before ISO/TS 32004 gave it a
        /// meaning.</para>
        /// <para><b>Why there is only one.</b> Answering from the salt and bit 13 alone would seal a document holding a
        /// token with bit 13 set when a revision is appended and re-encrypt it without any token in the round trip; two
        /// answers would also read an unreadable <c>/P</c> in opposite directions.</para>
        /// </remarks>
        internal static byte[] SaltWhenProtected(PdfDictionary encrypt, int permissions, bool trailerHasToken)
        {
            if (!(encrypt?[PdfName.KDFSalt] is PdfString salt) || salt.RawBytes.Length != 32) return null;

            bool requiredInEveryRevision = (permissions & PdfMacRequiredPermissionBit) == 0;
            return requiredInEveryRevision || trailerHasToken ? salt.RawBytes : null;
        }

        /// <summary>
        /// Whether <paramref name="trailer"/> — or the cross-reference stream dictionary standing in for one — carries an
        /// <c>/AuthCode</c> (ISO/TS 32004 Table 5), read as the entry's presence: Table 5 requires it to be direct.
        /// </summary>
        internal static bool TrailerHasToken(PdfDictionary trailer) => trailer?[PdfName.AuthCode] != null;

        /// <summary>ISO/TS 32004 §5.1.2: permission bit 13 (mask <c>0x1000</c>) clear means "PDF MAC in every revision".</summary>
        internal const int PdfMacRequiredPermissionBit = 0x1000;

        /// <summary>
        /// ISO/TS 32004 Table 5: an <c>/AuthCode</c> is present only where the encryption
        /// dictionary's <c>/V</c> is at least this.
        /// </summary>
        internal const int MinimumEncryptionVersion = 5;
    }
}
