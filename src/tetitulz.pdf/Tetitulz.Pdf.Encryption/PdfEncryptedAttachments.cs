// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// Attachments added to or removed from an <b>encrypted</b> document, in place.
    ///
    /// <para><see cref="PdfAttachmentManager"/> refuses an encrypted document because
    /// <c>Tetitulz.Pdf.Base</c> cannot encrypt the file it embeds. This assembly can: the attachment
    /// is planned against a decrypted copy (<see cref="PdfAttachmentManager.PlanAddAttachment"/>) and
    /// appended to the original bytes as one revision encrypted with the document's own key
    /// (<see cref="PdfEncryptedIncrementalUpdate"/>). Unlike <see cref="PdfEncryptedRoundTrip"/>, the
    /// original bytes are untouched, so a signature over them and the owner password both survive.</para>
    ///
    /// <para><b>The embedded file is encrypted under the document's <c>/EFF</c></b> (ISO 32000-2
    /// §7.6.5), not its <c>/StmF</c>: in a document that encrypts only its attachments the two differ,
    /// and a reader decrypts an embedded file under <c>/EFF</c>.</para>
    /// </summary>
    public static class PdfEncryptedAttachments
    {
        /// <summary>
        /// Embeds a file in an encrypted document — the encrypted counterpart of
        /// <see cref="PdfAttachmentManager.AddAttachment(Stream, Stream, string, byte[], string, string)"/>.
        /// An attachment of the same name is replaced.
        /// </summary>
        /// <param name="input">The encrypted PDF. Read only.</param>
        /// <param name="output">Receives the original bytes followed by the appended revision.</param>
        /// <param name="password">User or owner password.</param>
        /// <param name="filename">The attachment's name.</param>
        /// <param name="data">The file's bytes.</param>
        /// <param name="mimeType">Its media type, or null.</param>
        /// <param name="afRelationship">Its PDF 2.0 <c>/AFRelationship</c>, or null for none.</param>
        public static void AddAttachment(Stream input, Stream output, string password,
            string filename, byte[] data, string mimeType = null, string afRelationship = null)
            => AddAttachment(input, output, PdfDecryptionCredential.Password(password), filename, data, mimeType, afRelationship);

        /// <summary>As above, for a document opened with any credential, including a certificate.</summary>
        public static void AddAttachment(Stream input, Stream output, PdfDecryptionCredential credential,
            string filename, byte[] data, string mimeType = null, string afRelationship = null)
        {
            if (filename == null) throw new ArgumentNullException(nameof(filename));
            if (data == null) throw new ArgumentNullException(nameof(data));
            PdfEncryptedIncrementalUpdate.Run(input, output, credential, (document, firstFree) =>
                new EncryptedRevision(PdfAttachmentManager.PlanAddAttachment(
                    document, firstFree, filename, data, mimeType, afRelationship)));
        }

        /// <summary>
        /// Removes an attachment from an encrypted document's <c>/EmbeddedFiles</c> name tree — the
        /// encrypted counterpart of <see cref="PdfAttachmentManager.RemoveAttachment(Stream, Stream, string)"/>.
        /// A document with no such entry is written unchanged.
        /// </summary>
        /// <returns>True when an attachment was removed.</returns>
        public static bool RemoveAttachment(Stream input, Stream output, string password, string filename)
            => RemoveAttachment(input, output, PdfDecryptionCredential.Password(password), filename);

        /// <summary>As above, for a document opened with any credential, including a certificate.</summary>
        public static bool RemoveAttachment(Stream input, Stream output, PdfDecryptionCredential credential, string filename)
        {
            if (filename == null) throw new ArgumentNullException(nameof(filename));
            bool removed = false;
            PdfEncryptedIncrementalUpdate.Run(input, output, credential, (document, firstFree) =>
            {
                IReadOnlyList<IndirectObject> objects = PdfAttachmentManager.PlanRemoveAttachment(document, firstFree, filename);
                removed = objects != null;
                return new EncryptedRevision(objects);
            });
            return removed;
        }
    }
}
