// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Base.Encryption;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// How <see cref="PdfEncryptedRoundTrip"/> re-protects the document after the operation has run.
    ///
    /// <para><b>Rebuilding the original protection from a password is impossible</b> — a document's
    /// <c>/O</c> and <c>/U</c> entries are derived one-way from its two passwords, and a caller only
    /// ever holds the one that opened the file — <b>but keeping it is not</b>:
    /// <see cref="KeepOriginal"/> reuses the original <c>/Encrypt</c> dictionary and first file
    /// identifier under the same file key, so nothing has to be rebuilt.</para>
    /// </summary>
    public enum ReEncryptionPolicy
    {
        /// <summary>
        /// Re-encrypt with the password that opened the document as <b>both</b> the user and the
        /// owner password, carrying the original algorithm, permission bits and
        /// <c>/EncryptMetadata</c> across.
        ///
        /// <para><b>The owner password does not survive.</b> Anyone who could open the document can
        /// now also change its permissions. <see cref="RoundTripResult.OwnerPasswordChanged"/> is
        /// <c>true</c> whenever this policy runs, so the loss is reported rather than assumed.</para>
        /// </summary>
        ReuseOpenPassword = 0,

        /// <summary>
        /// Re-encrypt with <see cref="EncryptedRoundTripOptions.ExplicitParameters"/>. Nothing is
        /// inferred from the input — the caller states the passwords, algorithm and permissions.
        /// </summary>
        Explicit = 1,

        /// <summary>
        /// Emit a <b>decrypted</b> document. Equivalent to running the operation and then
        /// <see cref="PdfEncryptionOperations.RemovePassword"/>. Must be asked for by name: it
        /// strips protection the input had, so it is never a fallback.
        /// </summary>
        None = 2,

        /// <summary>
        /// Re-encrypt with the <c>/Adobe.PubSec</c> handler to
        /// <see cref="EncryptedRoundTripOptions.RecipientCertificates"/>, carrying the input's
        /// algorithm, permission bits and PDF MAC setting across.
        /// </summary>
        /// <remarks>
        /// <para><b>The certificates have to be supplied, and that is not an oversight.</b> A
        /// public-key document does not carry its recipients' certificates — each
        /// <c>/Recipients</c> entry is a CMS envelope that <i>identifies</i> a certificate by issuer
        /// and serial number so the holder can recognise their own. There is nothing in the file to
        /// re-encrypt to, which is the same reason
        /// <see cref="ReuseOpenPassword"/> cannot reproduce an original <c>/O</c> and <c>/U</c>:
        /// what the document keeps is enough to <i>open</i> it, never enough to rebuild it.</para>
        ///
        /// <para>So the caller states the recipient list, exactly as they would when encrypting
        /// afresh. Supplying a different list is legitimate and is how a document is re-addressed;
        /// it is not detected or warned about, because the library cannot tell a deliberate change
        /// from the original set.</para>
        /// </remarks>
        Recipients = 3,

        /// <summary>
        /// Keep the input's encryption as it is: the output is written under the same file key with
        /// the original <c>/Encrypt</c> dictionary and first file identifier, so <b>both</b> passwords
        /// of a <c>/Standard</c> document still open it (and its permission bits stay), and every
        /// recipient of an <c>/Adobe.PubSec</c> one — no certificates need supplying. A PDF MAC token
        /// is sealed again under the original <c>/KDFSalt</c>.
        /// </summary>
        /// <remarks>
        /// Opt-in. <see cref="RoundTripResult.OwnerPasswordChanged"/> is <c>false</c>. The file key
        /// is reused across the two files, as an appended revision of an encrypted document reuses
        /// it; a caller wanting fresh key material picks <see cref="ReuseOpenPassword"/>,
        /// <see cref="Explicit"/> or <see cref="Recipients"/>. <b>Under RC4 that reuse is a keystream's:</b>
        /// an object's key is the file key with its number and generation (Algorithm 1), so an object
        /// the operation changed and the output writes under its number is encrypted with the same RC4
        /// keystream as the input's version, and anyone holding both files can XOR the two plaintexts
        /// out of them. AES draws a fresh initialisation vector for every string and stream, so it has
        /// no such reuse.
        /// </remarks>
        KeepOriginal = 4
    }

    /// <summary>
    /// Inputs for <see cref="PdfEncryptedRoundTrip.Run"/>.
    /// </summary>
    public sealed class EncryptedRoundTripOptions
    {
        /// <summary>
        /// The password that opens the document. The default — the empty string — is the
        /// owner-only case, where a document is encrypted but opens without a password.
        /// </summary>
        public string OpenPassword { get; set; } = "";

        /// <summary>How the output is re-protected. See <see cref="ReEncryptionPolicy"/>.</summary>
        public ReEncryptionPolicy Policy { get; set; } = ReEncryptionPolicy.ReuseOpenPassword;

        /// <summary>
        /// Required when <see cref="Policy"/> is <see cref="ReEncryptionPolicy.Explicit"/>;
        /// ignored otherwise.
        /// </summary>
        public EncryptionParameters ExplicitParameters { get; set; }

        /// <summary>
        /// What opens the document, when a password does not: a PKCS#12 store or a device holding
        /// a recipient's key.
        /// </summary>
        /// <remarks>
        /// <b>Null means <see cref="OpenPassword"/>.</b> The two are not alternatives a caller
        /// chooses between on taste — an <c>/Adobe.PubSec</c> document has no password and a
        /// <c>/Standard</c> one has no recipient, so the document decides which is usable and the
        /// wrong one is refused by name. This exists because the round trip's decrypt step is the
        /// only thing in it that ever needed a password.
        /// </remarks>
        public PdfDecryptionCredential Credential { get; set; }

        /// <summary>
        /// The recipients the output is encrypted to, as DER-encoded X.509 certificates. Required
        /// when <see cref="Policy"/> is <see cref="ReEncryptionPolicy.Recipients"/>; ignored
        /// otherwise.
        /// </summary>
        public IReadOnlyList<byte[]> RecipientCertificates { get; set; }

        /// <summary>
        /// Permit the round trip on a document that carries a digital signature.
        ///
        /// <para><b>Off by default, because the round trip invalidates every signature in the
        /// file.</b> Decrypting and re-encrypting rewrites every string and stream, so a
        /// signature's <c>/ByteRange</c> no longer covers the bytes it signed and verification
        /// fails afterwards. Refusing is the same fail-closed posture
        /// <c>PdfRedactionEngine</c> takes: a silently broken signature is worse than a thrown
        /// exception. Set this only when you intend to discard the signatures.</para>
        /// </summary>
        public bool AllowSignatureInvalidation { get; set; }
    }

    /// <summary>
    /// What <see cref="PdfEncryptedRoundTrip.Run"/> actually did — as opposed to what it was asked
    /// to do. Read <see cref="OwnerPasswordChanged"/> and <see cref="Warnings"/> before treating
    /// the output as equivalent to the input.
    /// </summary>
    public sealed class RoundTripResult
    {
        internal RoundTripResult(
            bool succeeded,
            ReEncryptionPolicy policyApplied,
            EncryptionAlgorithm? algorithmIn,
            EncryptionAlgorithm? algorithmOut,
            bool ownerPasswordChanged,
            IReadOnlyList<string> warnings,
            string failureReason)
        {
            Succeeded            = succeeded;
            PolicyApplied        = policyApplied;
            AlgorithmIn          = algorithmIn;
            AlgorithmOut         = algorithmOut;
            OwnerPasswordChanged = ownerPasswordChanged;
            Warnings             = warnings ?? Array.Empty<string>();
            FailureReason        = failureReason;
        }

        /// <summary>
        /// True when the operation ran <b>and</b> its result reached the caller's output stream.
        /// When false, the caller's output stream was never written to — see
        /// <see cref="FailureReason"/>.
        /// </summary>
        public bool Succeeded { get; }

        /// <summary>
        /// The policy that ran. This is <see cref="ReEncryptionPolicy.None"/> for an
        /// <i>unencrypted</i> input, which is not an error: the operation runs directly and the
        /// output is as unencrypted as the input was.
        /// </summary>
        public ReEncryptionPolicy PolicyApplied { get; }

        /// <summary>The input's algorithm, or <c>null</c> when the input was not encrypted.</summary>
        public EncryptionAlgorithm? AlgorithmIn { get; }

        /// <summary>The output's algorithm, or <c>null</c> when the output is not encrypted.</summary>
        public EncryptionAlgorithm? AlgorithmOut { get; }

        /// <summary>
        /// True when the output's owner password differs from the input's — always the case under
        /// <see cref="ReEncryptionPolicy.ReuseOpenPassword"/>, because the original is not
        /// recoverable. Surfaced so a caller cannot believe it survived.
        /// </summary>
        public bool OwnerPasswordChanged { get; }

        /// <summary>Advisories that do not make the result a failure (e.g. discarded signatures).</summary>
        public IReadOnlyList<string> Warnings { get; }

        /// <summary>Why the round trip did not run, or <c>null</c> on success.</summary>
        public string FailureReason { get; }
    }
}
