// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>
    /// The refusal every writer that appends a clear-text revision makes for an encrypted document.
    /// </summary>
    /// <remarks>
    /// <para>The incremental writer carries <c>/Encrypt</c> forward and only reports that the
    /// appended objects are written in the clear. A reader then decrypts them to garbage, so each
    /// writer must refuse first. Six writers each carried a copy of this sentence (page content,
    /// annotations, layers, named destinations, metadata, attachments). Five had none:
    /// <c>PdfPageManipulator</c> and <c>PdfFormFieldBuilder</c> failed on an encrypted document with
    /// an <c>ArgumentOutOfRangeException</c> (no catalog loads, so it has no pages),
    /// <c>PdfPageLabels</c> with a <c>NullReferenceException</c>, and <c>PdfInitialView</c> and
    /// <c>PdfBookmarksManager</c> refused for a reason that was not the real one. The shared tail,
    /// <c>PdfPageContentAppender.AppendRevision</c>, refuses as well, for a writer that reaches it
    /// without a refusal of its own.</para>
    /// <para>The encrypted routes are <c>Tetitulz.Pdf.Encryption.PdfEncryptedRoundTrip.Run</c> and,
    /// for a writer that plans, <c>PdfEncryptedIncrementalUpdate</c>.</para>
    /// </remarks>
    internal static class EncryptedRevision
    {
        /// <summary>Refuses <paramref name="doc"/> when it is encrypted.</summary>
        /// <param name="doc">The document the writer opened.</param>
        /// <param name="action">What the caller asked for, as in "Cannot <i>add annotations to</i> an encrypted document".</param>
        /// <param name="written">What would be written in clear text, as in "<i>they</i> would be written".</param>
        /// <param name="route">The encrypted route to name, as a sentence; <see langword="null"/> names the round trip.</param>
        /// <exception cref="PdfEncryptedDocumentException">The document is encrypted.</exception>
        internal static void Refuse(PdfDocument doc, string action, string written, string route = null)
        {
            if (!doc.IsEncrypted) return;
            throw new PdfEncryptedDocumentException(
                "Cannot " + action + " an encrypted document: " + written + " would be written in clear " +
                "text where the reader expects per-object ciphertext, and this assembly cannot encrypt. " +
                (route ?? "Decrypt first (Tetitulz.Pdf.Encryption.PdfDecryptor), edit, then re-encrypt — or use " +
                          "Tetitulz.Pdf.Encryption.PdfEncryptedRoundTrip.Run."));
        }
    }
}
