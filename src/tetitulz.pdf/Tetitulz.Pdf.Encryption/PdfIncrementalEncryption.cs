// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// Supplies the <see cref="IPdfObjectEncryptor"/> that lets an <b>incremental update</b> be
    /// appended to an already-encrypted document.
    ///
    /// <para><b>Why.</b> An appended revision written in clear text is decrypted by the reader into
    /// garbage. The alternative — <see cref="PdfEncryptedRoundTrip"/> — decrypts the whole
    /// file, edits it, and re-encrypts, which works but rewrites every byte: it cannot preserve an
    /// existing signature (the <c>/ByteRange</c> no longer covers what it signed) and it cannot
    /// preserve the owner password (<c>/O</c> and <c>/U</c> derive one-way from both passwords).
    /// Encrypting only the appended objects keeps the original bytes untouched, so both survive.</para>
    ///
    /// <para><b>Same key, opposite direction.</b> The encryptor is built from the document's own
    /// <c>/Encrypt</c> dictionary through <c>PdfDecryptor.ResolveEncryption</c> — the same password
    /// validation, the same <c>/Length</c> clamping, the same crypt-filter resolution the decrypt
    /// path uses. It has to be: a key derived even slightly differently produces a revision that
    /// opens and shows nothing.</para>
    ///
    /// <para><b>Both security handlers.</b> A password opens a <c>/Standard</c> document; a
    /// certificate credential opens an <c>/Adobe.PubSec</c> one (see
    /// <see cref="TryCreate(PdfDocument, PdfDecryptionCredential, out IPdfObjectEncryptor, out string)"/>).</para>
    /// </summary>
    public static class PdfIncrementalEncryption
    {
        /// <summary>
        /// Builds an encryptor for <paramref name="pdf"/> using <paramref name="password"/>.
        /// </summary>
        /// <param name="pdf">The encrypted PDF. Read only; the position is restored.</param>
        /// <param name="password">User or owner password; <c>""</c> for an empty user password.</param>
        /// <param name="encryptor">The encryptor, or <see langword="null"/> on failure.</param>
        /// <param name="error">Why it could not be built, or <see langword="null"/> on success.</param>
        /// <returns>
        /// <see langword="false"/> when the document is not encrypted, uses an unsupported handler,
        /// or the password is wrong. A document that is <b>not</b> encrypted is a failure here on
        /// purpose: it needs no encryptor, and returning one would make the writer produce a file
        /// nothing can read.
        /// </returns>
        public static bool TryCreate(Stream pdf, string password,
                                     out IPdfObjectEncryptor encryptor, out string error)
        {
            encryptor = null;
            error = null;

            if (pdf == null) { error = "no PDF stream was supplied"; return false; }

            long restore = pdf.CanSeek ? pdf.Position : 0;
            try
            {
                // The fourth spelling of this block in the assembly, and the only one that already
                // rewound before copying. Routed through the shared leaf anyway: three siblings had
                // drifted into a truncating copy, and a correct duplicate is simply one that has not
                // drifted yet.
                MemoryStream ms = SeekableStream.Buffered(pdf);

                using var doc = PdfDocument.Open(ms);
                return TryCreate(doc, password, out encryptor, out error);
            }
            catch (PdfException ex) { error = ex.Message; return false; }
            catch (IOException ex) { error = ex.Message; return false; }
            finally
            {
                if (pdf.CanSeek) pdf.Position = restore;
            }
        }

        /// <summary>
        /// Builds an encryptor from an already-open document. Use this when the caller has parsed
        /// the file once already — <see cref="PdfDocument.Open(Stream)"/> is not cheap, and the
        /// stream overload would parse it a second time.
        /// </summary>
        public static bool TryCreate(PdfDocument document, string password,
                                     out IPdfObjectEncryptor encryptor, out string error)
        {
            encryptor = null;
            error = null;

            if (document == null) { error = "no document was supplied"; return false; }

            return TryCreate(document, PdfDecryptionCredential.Password(password), out encryptor, out error);
        }

        /// <summary>
        /// Builds an encryptor for a document opened with any credential — a password, a PKCS#12
        /// store, or a device holding the recipient's key.
        /// </summary>
        /// <remarks>
        /// <b>The per-object encryption is the same for both security handlers.</b> ISO 32000-2
        /// §7.6.5 gives <c>/Adobe.PubSec</c> its own way of recovering the <i>file</i> key — a CMS
        /// envelope rather than a password — and then hands that key to the same §7.6.3 per-object
        /// derivation and the same crypt filters. So the encryptor below is built from
        /// <c>ResolvedEncryption</c> alone and never asks which handler produced it; only the
        /// credential differs.
        /// </remarks>
        public static bool TryCreate(PdfDocument document, PdfDecryptionCredential credential,
                                     out IPdfObjectEncryptor encryptor, out string error)
        {
            encryptor = null;
            error = null;

            if (document == null) { error = "no document was supplied"; return false; }
            if (credential == null) { error = "no credential was supplied"; return false; }

            try
            {
                encryptor = new StandardHandlerObjectEncryptor(credential.Resolve(document), DocumentMetadataObjectNumber(document));
                return true;
            }
            catch (PdfException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// The encryptor for a document written whole under an <b>existing</b> document's key and
        /// algorithms — <see cref="PdfEncryptedRoundTrip"/>'s <see cref="ReEncryptionPolicy.KeepOriginal"/>:
        /// the same file key, stream, string and embedded-file algorithms and crypt filters the
        /// decryptor resolved, so the original <c>/Encrypt</c> dictionary describes the new file too.
        /// </summary>
        /// <param name="resolved">The original document's resolved encryption.</param>
        /// <param name="documentMetadataObjectNumber">
        /// The catalog's <c>/Metadata</c> stream's number — the one stream <c>/EncryptMetadata false</c>
        /// leaves in clear — or -1 when it is not known (<see cref="IPdfDocumentMetadataEncryptor"/>).
        /// </param>
        internal static IPdfObjectEncryptor ForResolved(PdfDecryptor.ResolvedEncryption resolved, int documentMetadataObjectNumber = -1)
        {
            if (resolved == null) throw new ArgumentNullException(nameof(resolved));
            return new StandardHandlerObjectEncryptor(resolved, documentMetadataObjectNumber);
        }

        /// <summary>
        /// The encryptor for a document being written for the first time under
        /// <paramref name="fileKey"/> — <see cref="PdfEncryptor"/>'s and <see cref="PdfPublicKeyEncryptor"/>'s
        /// compressed layout, where <c>PdfCompressedFileWriter</c> encrypts each object as it writes it.
        /// With <paramref name="onlyAttachments"/> the stream and string filters are <c>/Identity</c>
        /// and only embedded files take the algorithm (<c>/EFF</c>, ISO 32000-2 §7.6.5).
        /// </summary>
        internal static IPdfObjectEncryptor ForNewDocument(byte[] fileKey, EncryptionAlgorithm algorithm,
                                                           bool encryptMetadata, bool onlyAttachments,
                                                           int documentMetadataObjectNumber = -1)
        {
            if (fileKey == null) throw new ArgumentNullException(nameof(fileKey));
            return new StandardHandlerObjectEncryptor(new PdfDecryptor.ResolvedEncryption
            {
                Key = fileKey,
                StreamAlgorithm = onlyAttachments ? EncryptionAlgorithm.Identity : algorithm,
                StringAlgorithm = onlyAttachments ? EncryptionAlgorithm.Identity : algorithm,
                EmbeddedFileAlgorithm = algorithm,
                EncryptMetadata = encryptMetadata,
            }, documentMetadataObjectNumber);
        }

        /// <summary>
        /// The number of the stream the catalog of <paramref name="doc"/> names as its <c>/Metadata</c>, when
        /// that is a <c>/Type /Metadata</c> stream the document can read; -1 otherwise.
        /// </summary>
        internal static int DocumentMetadataObjectNumber(Core.Document.PdfDocument doc)
        {
            try
            {
                return doc?.ResolveObject(doc.Trailer?[PdfName.Root]) is PdfDictionary catalog
                       && catalog[PdfName.Metadata] is PdfReference reference
                       && doc.ResolveObject(reference) is PdfStream stream
                       && stream.GetName(PdfName.Type) == PdfName.Metadata
                    ? reference.ReferenceObjectNumber
                    : -1;
            }
            catch (PdfException) { return -1; }
        }

        /// <summary>
        /// Encrypts objects with the standard security handler's per-object keys — the exact
        /// inverse of <c>PdfDecryptor</c>'s read path, sharing its primitives so the two cannot
        /// drift.
        /// </summary>
        private sealed class StandardHandlerObjectEncryptor : IPdfObjectEncryptor, IPdfEmbeddedFileEncryptor, IPdfDocumentMetadataEncryptor
        {
            private readonly byte[] _fileKey;
            private readonly EncryptionAlgorithm _streamAlgorithm;
            private readonly EncryptionAlgorithm _stringAlgorithm;
            private readonly EncryptionAlgorithm _embeddedFileAlgorithm;

            internal StandardHandlerObjectEncryptor(PdfDecryptor.ResolvedEncryption resolved, int documentMetadataObjectNumber = -1)
            {
                _fileKey = resolved.Key;
                _streamAlgorithm = resolved.StreamAlgorithm;
                _stringAlgorithm = resolved.StringAlgorithm;
                _embeddedFileAlgorithm = resolved.EmbeddedFileAlgorithm;
                EncryptsMetadataStreams = resolved.EncryptMetadata;
                DocumentMetadataObjectNumber = documentMetadataObjectNumber;
            }

            public int DocumentMetadataObjectNumber { get; }

            public bool EncryptsEmbeddedFiles => _embeddedFileAlgorithm != EncryptionAlgorithm.Identity;

            public byte[] EncryptEmbeddedFile(byte[] plaintext, int objectNumber, int generationNumber)
                => Encrypt(plaintext, objectNumber, generationNumber, _embeddedFileAlgorithm);

            public bool EncryptsStrings => _stringAlgorithm != EncryptionAlgorithm.Identity;

            public bool EncryptsStreams => _streamAlgorithm != EncryptionAlgorithm.Identity;

            public bool EncryptsMetadataStreams { get; }

            public byte[] EncryptString(byte[] plaintext, int objectNumber, int generationNumber)
                => Encrypt(plaintext, objectNumber, generationNumber, _stringAlgorithm);

            public byte[] EncryptStream(byte[] plaintext, int objectNumber, int generationNumber)
                => Encrypt(plaintext, objectNumber, generationNumber, _streamAlgorithm);

            private byte[] Encrypt(byte[] plaintext, int objectNumber, int generationNumber,
                                   EncryptionAlgorithm algorithm)
                => PdfEncryptionPrimitives.EncryptData(plaintext, objectNumber, generationNumber, _fileKey, algorithm);
        }
    }
}
