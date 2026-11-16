// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Core.Document;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// In-place image replacement for <b>encrypted</b> documents.
    ///
    /// <para><see cref="PdfImageReplacer"/> refuses one because <c>Tetitulz.Pdf.Base</c> cannot
    /// decrypt. This assembly can, so the composition lives here — the image is read from a
    /// decrypted view and the replacement is appended to the original bytes, encrypted with the
    /// document's own key.</para>
    ///
    /// <para><b>The signature-preserving property survives.</b> The original bytes are untouched,
    /// so an approval signature over them still verifies — which is the entire point of replacing
    /// an image in place rather than rewriting the file. (And the same caveat applies: an
    /// incremental update <i>hides</i> the old pixels, it does not delete them. See GAPS.md §6a.)</para>
    /// </summary>
    public static class PdfEncryptedImages
    {
        /// <summary>
        /// Replaces the image XObject with the given object number, wherever it is referenced,
        /// and appends the result as one encrypted revision.
        /// </summary>
        /// <param name="input">The encrypted PDF. Read only.</param>
        /// <param name="output">Receives the original bytes followed by the appended revision.</param>
        /// <param name="password">User or owner password.</param>
        /// <param name="imageObjectNumber">
        /// From <see cref="FindImages"/> — object numbers are stable between the encrypted original
        /// and its decrypted copy, because decryption preserves them.
        /// </param>
        /// <param name="image">The replacement pixels.</param>
        /// <param name="options">Policy settings; may be null.</param>
        public static ImageReplacementResult ReplaceByObjectNumber(
            Stream input,
            Stream output,
            string password,
            int imageObjectNumber,
            ReplacementImage image,
            ImageReplacementOptions options = null)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));

            ImageReplacementResult result = null;
            PdfEncryptedIncrementalUpdate.Run(input, output, password, (document, firstFreeObjectNumber) =>
            {
                // The plan reads only whether the copy is certified and at what /P, which its object
                // graph answers; no length is passed because a decrypted copy's /ByteRange describes
                // the original file, and a modification verdict computed against the copy's bytes
                // would compare two different files. The two-view analysis for that question is
                // PdfEncryptionOperations.VerifyMdp.
                ImageReplacementPlan plan = PdfImageReplacer.Plan(
                    document, imageObjectNumber, image, options, firstFreeObjectNumber);
                result = plan.Result;
                return plan.Objects;
            });

            return result;
        }

        /// <summary>
        /// Every image XObject an encrypted document reaches, with the pages and resource names
        /// that reach it — the encrypted counterpart of <see cref="PdfImageReplacer.FindImages(Stream)"/>,
        /// which finds nothing here because an encrypted document exposes no catalog.
        /// </summary>
        /// <remarks>
        /// <b>Superseded</b> by <see cref="PdfImageReplacer.FindImages(Stream, string)"/>, which
        /// takes the same password and enumerates the same objects. Deprecated rather than removed
        /// because it is public API. <b>Replacing</b> an image in an encrypted document still
        /// belongs here and is not deprecated: it appends a revision encrypted with the document's
        /// own key, which is what preserves a signature.
        /// </remarks>
        [Obsolete("Use Tetitulz.Pdf.Base.Operations.Images.PdfImageReplacer.FindImages(Stream, " +
                  "string), which takes the same password and returns the same images. Requires " +
                  "PdfEncryptionOperations.Register() at startup. ReplaceByObjectNumber here is " +
                  "not deprecated.", error: false)]
        public static IReadOnlyList<DocumentImageInfo> FindImages(Stream input, string password)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            using var decrypted = new MemoryStream();
            PdfDecryptor.Decrypt(input, decrypted, password);
            decrypted.Position = 0;
            return PdfImageReplacer.FindImages(decrypted);
        }
    }
}
