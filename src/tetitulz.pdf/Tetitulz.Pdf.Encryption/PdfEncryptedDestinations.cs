// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// Named destinations added to or removed from an <b>encrypted</b> document, in place.
    ///
    /// <para><see cref="PdfNamedDestinations"/> refuses an encrypted document because
    /// <c>Tetitulz.Pdf.Base</c> cannot encrypt the name tree it writes. This assembly can: the
    /// destination is planned against a decrypted copy (<see cref="PdfNamedDestinations.PlanSetDestination"/>)
    /// and appended to the original bytes as one revision encrypted with the document's own key
    /// (<see cref="PdfEncryptedIncrementalUpdate"/>). Unlike <see cref="PdfEncryptedRoundTrip"/>, the
    /// original bytes are untouched, so a signature over them and the owner password both survive.</para>
    /// </summary>
    public static class PdfEncryptedDestinations
    {
        /// <summary>
        /// Adds or replaces a named destination pointing at a page of an encrypted document — the
        /// encrypted counterpart of <see cref="PdfNamedDestinations.SetDestination"/>.
        /// </summary>
        /// <param name="input">The encrypted PDF. Read only.</param>
        /// <param name="output">Receives the original bytes followed by the appended revision.</param>
        /// <param name="password">User or owner password.</param>
        /// <param name="name">Destination name; replaces any existing entry of that name.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="left">X position in points. Null = inherit current.</param>
        /// <param name="top">Y position in points. Null = inherit current.</param>
        /// <param name="zoom">Zoom factor. Null = inherit current.</param>
        public static void SetDestination(
            Stream input, Stream output, string password,
            string name, int pageIndex,
            double? left = null, double? top = null, double? zoom = null)
            => SetDestination(input, output, PdfDecryptionCredential.Password(password), name, pageIndex, left, top, zoom);

        /// <summary>As above, for a document opened with any credential, including a certificate.</summary>
        public static void SetDestination(
            Stream input, Stream output, PdfDecryptionCredential credential,
            string name, int pageIndex,
            double? left = null, double? top = null, double? zoom = null)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            PdfEncryptedIncrementalUpdate.Run(input, output, credential, (document, firstFree) =>
                new EncryptedRevision(PdfNamedDestinations.PlanSetDestination(
                    document, firstFree, name, pageIndex, left, top, zoom)));
        }

        /// <summary>
        /// Removes a named destination from an encrypted document — the encrypted counterpart of
        /// <see cref="PdfNamedDestinations.RemoveDestination"/>. A document with no destination of
        /// that name is written unchanged.
        /// </summary>
        /// <returns>True when the destination was found and removed.</returns>
        public static bool RemoveDestination(Stream input, Stream output, string password, string name)
            => RemoveDestination(input, output, PdfDecryptionCredential.Password(password), name);

        /// <summary>As above, for a document opened with any credential, including a certificate.</summary>
        public static bool RemoveDestination(Stream input, Stream output, PdfDecryptionCredential credential, string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            bool removed = false;
            PdfEncryptedIncrementalUpdate.Run(input, output, credential, (document, firstFree) =>
            {
                IReadOnlyList<IndirectObject> objects = PdfNamedDestinations.PlanRemoveDestination(document, firstFree, name);
                removed = objects != null;
                return new EncryptedRevision(objects);
            });
            return removed;
        }
    }
}
