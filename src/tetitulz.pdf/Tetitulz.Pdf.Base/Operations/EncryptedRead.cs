// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>
    /// The refusal every read door that takes no password makes for an encrypted document.
    /// </summary>
    /// <remarks>
    /// <para><see cref="PdfDocument.Open(System.IO.Stream)"/> decrypts nothing and loads no catalog
    /// for an encrypted file, so a reader asking it for fields, bookmarks, labels, layers, links or
    /// usage rights found none and said so — "none" for a document that may hold plenty, even a
    /// permissions-only file anyone can open, and indistinguishable from the true answer.
    /// <c>PdfNamedDestinations.ListDestinations</c> was the first door made to refuse; the rest
    /// answered empty, false or null, most with no diagnostic. A door with a password overload
    /// names it; the facade's <c>PdfOperations</c> doors decrypt first, and so reach the readers with
    /// a document that is not encrypted.</para>
    /// <para>The writers' refusal is <see cref="EncryptedRevision"/>.</para>
    /// <para><b>Public for <see cref="RefuseConversion"/></b>, which the converter plugins call and
    /// Base names no plugin in its <c>InternalsVisibleTo</c>; the read doors' <see cref="Refuse"/>
    /// stays internal.</para>
    /// </remarks>
    public static class EncryptedRead
    {
        /// <summary>
        /// Refuses to convert <paramref name="doc"/> when it is encrypted: <see cref="PdfDocument.Open(System.IO.Stream)"/>
        /// loads no page tree for such a file, so a converter would write a valid, empty output — a
        /// .txt, an SVG or HTML page, a .docx, an .xlsx with no sheet — and return normally. The
        /// text, SVG, HTML and Office converters all refuse through it.
        /// </summary>
        /// <param name="doc">The document the converter opened.</param>
        /// <param name="target">What the conversion writes, as in "Cannot convert an encrypted document to <i>DOCX</i>".</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="doc"/> is null.</exception>
        /// <exception cref="PdfEncryptedDocumentException">The document is encrypted.</exception>
        public static void RefuseConversion(PdfDocument doc, string target)
        {
            if (doc == null) throw new System.ArgumentNullException(nameof(doc));
            if (!doc.IsEncrypted) return;
            throw new PdfEncryptedDocumentException(
                "Cannot convert an encrypted document to " + target + ": its pages cannot be read, so the " +
                "output would be silently empty. Decrypt it first (Tetitulz.Pdf.Encryption.PdfDecryptor) and " +
                "convert the decrypted document.");
        }

        /// <summary>Refuses <paramref name="doc"/> when it is encrypted.</summary>
        /// <param name="doc">The document the reader opened.</param>
        /// <param name="what">What the caller asked for, as in "Cannot read <i>the form fields</i> of".</param>
        /// <param name="passwordDoor">The overload that takes a password, or <see langword="null"/> when there is none.</param>
        /// <exception cref="PdfEncryptedDocumentException">The document is encrypted.</exception>
        internal static void Refuse(PdfDocument doc, string what, string passwordDoor)
        {
            if (!doc.IsEncrypted) return;
            throw new PdfEncryptedDocumentException(
                "Cannot read " + what + " of an encrypted document without its password: it exposes no " +
                "catalog, so the answer would be that it has none. " +
                (passwordDoor != null
                    ? "Use " + passwordDoor + "."
                    : "Decrypt it first (Tetitulz.Pdf.Encryption.PdfDecryptor), or read it through the PdfOperations facade."));
        }
    }
}
