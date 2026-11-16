// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO;

namespace Tetitulz.Pdf.Base.Encryption
{
    /// <summary>
    /// The one place a <b>read-only</b> operation in Base turns a possibly-encrypted stream into
    /// readable bytes, using whatever decryptor the host
    /// registered.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is in Base at all, given that Encryption already exists.</b> The
    /// dependency runs <c>Encryption → Base</c>, so Base cannot call the Encryption assembly
    /// directly; <see cref="PdfDecryptorHolder"/> inverts that with a registered interface, and
    /// the renderer has used it to decrypt for a long time. What was missing was any way for a
    /// non-rendering read — listing form fields, enumerating images — to do the same, so those
    /// operations threw on an encrypted document and a caller had to route through
    /// <c>Tetitulz.Pdf.Encryption</c>'s mirrors instead.</para>
    /// <para><b>Reads only. Writes stay in the Encryption assembly, deliberately.</b> A read
    /// decrypts, answers a question and discards the plaintext, so nothing about the file changes.
    /// A write has to re-encrypt, which needs the owner password and the original security
    /// handler's parameters — <c>PdfEncryptedRoundTrip</c> exists precisely because that cannot be
    /// reproduced from the decrypt side, and it reports what it cannot preserve rather than
    /// silently downgrading. Do not "finish the job" by adding a write helper here: it would
    /// either drop the encryption or need the whole re-encryption machinery, which is what lives
    /// on the other side of the interface.</para>
    /// <para><b>A wrong password throws.</b> Once a decryptor is registered, failing to decrypt is
    /// never silently answered with the encrypted bytes — that would return an empty field list
    /// for a document that has fields, which is indistinguishable from a document that has none.
    /// This mirrors <c>PdfPageRenderer.EnsureDecrypted</c>'s rule for exactly the same reason.</para>
    /// </remarks>
    public static class PdfDecryptedInput
    {
        /// <summary>
        /// Returns readable bytes for <paramref name="input"/>: decrypted when the document is
        /// encrypted and a decryptor is registered, otherwise the original bytes.
        /// </summary>
        /// <param name="input">
        /// The document. Read <b>in full</b> — a seekable stream is rewound first, because a PDF
        /// without its header cannot be parsed. This is not "from the current position", whatever
        /// the stream's type.
        /// </param>
        /// <param name="password">User or owner password; <c>null</c> is treated as empty.</param>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// The document is encrypted and no decryptor has been registered. The message names the
        /// registration call, because the failure is a missing assembly reference at the host and
        /// there is nothing about the file itself to investigate.
        /// </exception>
        public static byte[] ReadDecrypted(Stream input, string password)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            byte[] bytes = ReadAllBytes(input);

            bool isEncrypted;
            using (var probe = new MemoryStream(bytes, writable: false))
            using (var doc = PdfDocument.Open(probe))
                isEncrypted = doc.IsEncrypted;

            if (!isEncrypted) return bytes;

            var decryptor = PdfDecryptorHolder.Current
                ?? throw new InvalidOperationException(
                    "The document is encrypted and no decryptor is registered. Reference " +
                    "Tetitulz.Pdf.Encryption and call PdfEncryptionOperations.Register() at " +
                    "startup.");

            var output = new MemoryStream();
            using (var src = new MemoryStream(bytes, writable: false))
                decryptor.Decrypt(src, output, password ?? string.Empty);
            return output.ToArray();
        }

        /// <summary>
        /// <see cref="ReadDecrypted(Stream, string)"/> wrapped in a fresh, readable
        /// <see cref="MemoryStream"/> positioned at 0, for the many callers that want a stream.
        /// </summary>
        public static MemoryStream OpenDecrypted(Stream input, string password)
            => new MemoryStream(ReadDecrypted(input, password), writable: false);

        /// <summary>
        /// The document's whole bytes, from offset 0, whatever the concrete stream type — through
        /// <see cref="StreamBytes.ReadAll"/>, the one implementation of that.
        /// </summary>
        /// <remarks>
        /// <para><b>The two paths here agree</b>, and the contract is spelled out on
        /// <see cref="ReadDecrypted"/>: a <see cref="MemoryStream"/> fast path returning the entire
        /// buffer regardless of <c>Position</c>, beside a general path copying from the current
        /// position to the end, would give a host that had already read the header the whole
        /// document from a <c>MemoryStream</c> and a headerless fragment from the identical
        /// <c>FileStream</c> — same input, different result by stream type, which only shows up in
        /// production. <c>StreamBytes.ReadAll</c> makes that one behaviour by construction, and
        /// documents choosing it for this reason.</para>
        ///
        /// <para><b>No leak.</b> <c>PdfHelpers.EnsureSeekable(input).CopyTo(copy)</c>, for a
        /// non-seekable input, allocates a buffering <see cref="MemoryStream"/> and hands it back —
        /// which nothing there would dispose, because a caller cannot tell whether it was given
        /// its own stream or a new one. That ownership ambiguity is the open <c>EnsureSeekable</c>
        /// row in the consolidation backlog; the shared reader sidesteps it by never needing a
        /// seekable stream at all.</para>
        /// </remarks>
        private static byte[] ReadAllBytes(Stream input) => StreamBytes.ReadAll(input);
    }
}
