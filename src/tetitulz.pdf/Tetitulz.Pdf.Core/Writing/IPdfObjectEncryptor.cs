// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// Encrypts the strings and stream data of objects being written into an <b>existing
    /// encrypted document</b>, using that document's own file encryption key.
    ///
    /// <para><b>Why this interface exists.</b> An incremental update appends objects to a file
    /// whose reader will decrypt everything it reads with a per-object key. Objects written in
    /// clear text therefore decode to garbage. Writing them encrypted needs the file encryption
    /// key inside <see cref="PdfWriter"/>, and <c>Tetitulz.Pdf.Core</c> has no cryptography: it cannot
    /// reference <c>Tetitulz.Pdf.Encryption</c> (the dependency runs the other way, and pulling
    /// BouncyCastle into Core would put it in every consumer's graph). So Core declares the
    /// operation and <c>Tetitulz.Pdf.Encryption.PdfIncrementalEncryption</c> supplies it.</para>
    ///
    /// <para><b>What the writer guarantees, so an implementation need not check.</b> It is never
    /// asked to encrypt the things ISO 32000-1 §7.6.2 excludes: the cross-reference stream, the
    /// <c>/Encrypt</c> dictionary, or the trailer's <c>/ID</c>. It <i>is</i> asked about a
    /// <c>/Type /Metadata</c> stream, and answers with <see cref="EncryptsMetadataStreams"/>,
    /// because only the encryption handler knows the document's <c>/EncryptMetadata</c> flag.</para>
    ///
    /// <para><b>Object and generation numbers matter.</b> Below AES-256 the key is derived per
    /// object from those two numbers, so encrypting object 12's bytes under object 11's number
    /// produces a stream that decrypts to noise. The writer always passes the numbers it is about
    /// to write in the object header.</para>
    /// </summary>
    public interface IPdfObjectEncryptor
    {
        /// <summary>
        /// Whether strings are encrypted at all. False when the document's string crypt filter is
        /// <c>/Identity</c> — in which case a string written in clear text is <i>correct</i>, and
        /// encrypting it would be the bug.
        /// </summary>
        bool EncryptsStrings { get; }

        /// <summary>Whether stream data is encrypted; false for an <c>/Identity</c> stream filter.</summary>
        bool EncryptsStreams { get; }

        /// <summary>
        /// Whether a <c>/Type /Metadata</c> stream is encrypted along with everything else. This is
        /// the document's <c>/EncryptMetadata</c> flag: when it is false the XMP packet is stored
        /// in the clear so indexers can read it, and encrypting a newly written one would make the
        /// document inconsistent with its own declaration.
        /// </summary>
        bool EncryptsMetadataStreams { get; }

        /// <summary>
        /// Encrypts one string's raw bytes for the given indirect object. Returns the bytes to
        /// serialise; an implementation that cannot encrypt must throw rather than return the
        /// plaintext, since a silent passthrough writes an unreadable document.
        /// </summary>
        byte[] EncryptString(byte[] plaintext, int objectNumber, int generationNumber);

        /// <summary>
        /// Encrypts one stream's data for the given indirect object. The result may be longer than
        /// the input (AES prepends a 16-byte initialisation vector and pads to the block size), so
        /// the caller must write <c>/Length</c> from the returned array, not the input.
        /// </summary>
        byte[] EncryptStream(byte[] plaintext, int objectNumber, int generationNumber);
    }

    /// <summary>
    /// What an <see cref="IPdfObjectEncryptor"/> also implements when the document gives embedded
    /// file streams a crypt filter of their own — ISO 32000-2 §7.6.5's <c>/EFF</c>, which viewers'
    /// "encrypt only file attachments" sets while leaving <c>/StmF</c> and <c>/StrF</c> at
    /// <c>/Identity</c>.
    /// </summary>
    /// <remarks>
    /// A separate interface because <see cref="IPdfObjectEncryptor"/> is public and netstandard2.0
    /// has no default interface members: adding a member to it would break every implementation
    /// outside this repository. <see cref="PdfWriter"/> asks for this one on a <c>/Type /EmbeddedFile</c>
    /// stream and falls back to <see cref="IPdfObjectEncryptor.EncryptStream"/> when it is absent —
    /// right whenever <c>/EFF</c> is absent too, since it then defaults to <c>/StmF</c>.
    /// </remarks>
    public interface IPdfEmbeddedFileEncryptor
    {
        /// <summary>Whether embedded file data is encrypted; false for an <c>/Identity</c> <c>/EFF</c>.</summary>
        bool EncryptsEmbeddedFiles { get; }

        /// <summary>
        /// Encrypts one embedded file stream's data under the <c>/EFF</c> crypt filter, for the given
        /// indirect object; the same contract as <see cref="IPdfObjectEncryptor.EncryptStream"/>.
        /// </summary>
        byte[] EncryptEmbeddedFile(byte[] plaintext, int objectNumber, int generationNumber);
    }

    /// <summary>
    /// What an <see cref="IPdfObjectEncryptor"/> also implements when it knows which stream is the
    /// document-level metadata stream — the catalog's <c>/Metadata</c>, the one stream
    /// <c>/EncryptMetadata false</c> leaves in clear (ISO 32000-2 Table 20, and Table 27's crypt-filter entry).
    /// </summary>
    /// <remarks>
    /// Without it <see cref="PdfWriter"/> leaves every <c>/Type /Metadata</c> stream in clear when
    /// <see cref="IPdfObjectEncryptor.EncryptsMetadataStreams"/> is false — a page's, an image's or a font's XMP
    /// too, which a reader then decrypts as it decrypts every stream but the catalog's. A separate interface
    /// for the reason <see cref="IPdfEmbeddedFileEncryptor"/> is one.
    /// </remarks>
    public interface IPdfDocumentMetadataEncryptor
    {
        /// <summary>The object number of the catalog's <c>/Metadata</c> stream; zero or less when it is not known.</summary>
        int DocumentMetadataObjectNumber { get; }
    }
}
