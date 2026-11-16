// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// What opens an encrypted document: a password, a PKCS#12 store, or a device holding the
    /// recipient's private key.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists rather than an overload per credential per entry point.</b> Every
    /// write path in this assembly — appending a revision, round-tripping, signing, refreshing a
    /// PDF MAC — needs exactly two things from a credential: the file encryption key, and a
    /// decrypted copy to read the object graph from. Both are already implemented twice, once for
    /// each security handler, and both already answer in the same shapes
    /// (<c>PdfDecryptor.ResolvedEncryption</c> and a decrypted stream). Threading three overloads
    /// through four entry points would be twelve methods that differ in one line; this is one type
    /// and one extra overload each.</para>
    ///
    /// <para><b>The handler is not the caller's to choose.</b> A credential says what the caller
    /// holds, not which security handler the document uses — an `/Adobe.PubSec` document cannot be
    /// opened with a password and a `/Standard` one cannot be opened with a certificate, and the
    /// document decides which. Passing the wrong kind is refused by the resolver that reads
    /// <c>/Filter</c>, with the message that names the mismatch.</para>
    ///
    /// <para><b>A PKCS#12 store is copied, not held.</b> A <see cref="Stream"/> is positional and
    /// single-use, and a credential is resolved more than once on some paths — once for the key and
    /// once for the decrypted copy. Buffering at construction is what makes a credential reusable
    /// rather than something that silently works only the first time.</para>
    /// </remarks>
    public sealed class PdfDecryptionCredential
    {
        private readonly string _password;
        private readonly byte[] _pkcs12;
        private readonly string _storePassword;
        private readonly IExternalDecryptor _decryptor;

        private PdfDecryptionCredential(
            string password, byte[] pkcs12, string storePassword, IExternalDecryptor decryptor)
        {
            _password = password;
            _pkcs12 = pkcs12;
            _storePassword = storePassword;
            _decryptor = decryptor;
        }

        /// <summary>A user or owner password, for a <c>/Standard</c>-encrypted document.</summary>
        public static PdfDecryptionCredential Password(string password) =>
            new PdfDecryptionCredential(password ?? string.Empty, null, null, null);

        /// <summary>
        /// A PKCS#12 store holding a recipient's certificate and private key, for an
        /// <c>/Adobe.PubSec</c> document.
        /// </summary>
        /// <param name="store">The store. Read in full here; not disposed, and not retained.</param>
        /// <param name="password">The store's password, or null when it has none.</param>
        public static PdfDecryptionCredential Pkcs12(Stream store, string password)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            return new PdfDecryptionCredential(null, StreamBytes.ReadAll(store), password, null);
        }

        /// <summary>
        /// A device holding the recipient's private key — a smart card, a PKCS#11 token, an HSM, or
        /// a non-exportable key in the OS certificate store.
        /// </summary>
        public static PdfDecryptionCredential External(IExternalDecryptor decryptor)
        {
            if (decryptor == null) throw new ArgumentNullException(nameof(decryptor));
            return new PdfDecryptionCredential(null, null, null, decryptor);
        }

        /// <summary>Whether this credential is a password — the <c>/Standard</c> handler's.</summary>
        internal bool IsPassword => _decryptor == null && _pkcs12 == null;

        /// <summary>The password, for the paths that still take one directly.</summary>
        internal string PasswordValue => _password;

        /// <summary>
        /// The document's file encryption key and algorithms, by whichever route this credential is.
        /// </summary>
        internal PdfDecryptor.ResolvedEncryption Resolve(PdfDocument doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            if (_decryptor != null) return PdfPublicKeyDecryptor.Resolve(doc, _decryptor);

            if (_pkcs12 != null)
            {
                using var store = new MemoryStream(_pkcs12, writable: false);
                return PdfPublicKeyDecryptor.Resolve(doc, store, _storePassword);
            }

            // Named before the decryptor's "unsupported handler", because at this point the answer
            // is not "this cannot be done" but "this needs a different credential".
            if (PdfPublicKeyDecryptor.UsesPublicKeyHandler(doc))
                throw new PdfException(
                    "The document uses the /Adobe.PubSec security handler, which has no password. " +
                    "Open it with PdfDecryptionCredential.Pkcs12 or .External instead.");

            // The password goes in as given; SASLprep is applied inside the V≥5 key derivation,
            // where the revision is known (PdfDecryptor.ValidateAndDeriveAes256Key).
            return PdfDecryptor.ResolveEncryption(doc, _password ?? string.Empty);
        }

        /// <summary>
        /// Writes a decrypted copy of <paramref name="input"/> to <paramref name="output"/> — the
        /// object graph a write path reads from, since an encrypted document exposes no catalog.
        /// </summary>
        internal void Decrypt(Stream input, Stream output)
        {
            if (_decryptor != null) { PdfPublicKeyDecryptor.Decrypt(input, output, _decryptor); return; }

            if (_pkcs12 != null)
            {
                using var store = new MemoryStream(_pkcs12, writable: false);
                PdfPublicKeyDecryptor.Decrypt(input, output, store, _storePassword);
                return;
            }

            PdfDecryptor.Decrypt(input, output, _password);
        }

        /// <summary>
        /// A short description of the credential kind, for a refusal that has to name what was
        /// supplied without ever naming the secret itself.
        /// </summary>
        internal string Describe() =>
            _decryptor != null ? "an external decryptor"
            : _pkcs12 != null ? "a PKCS#12 store"
            : "a password";
    }
}
