// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Forms;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// Form filling for <b>encrypted</b> documents.
    ///
    /// <para><see cref="PdfFormFiller"/> refuses one, and has to: it lives in
    /// <c>Tetitulz.Pdf.Base</c>, which cannot decrypt (the dependency runs
    /// <c>Encryption → Base</c>, so the reference cannot go the other way, and pulling BouncyCastle
    /// into Base would put it in every consumer's graph). This assembly can do both halves, so the
    /// composition lives here — the same shape as <see cref="PdfEncryptedRoundTrip"/>, where the
    /// operation travels to the crypto rather than the crypto travelling to the operation.</para>
    ///
    /// <para><b>The original bytes are preserved</b>, so an approval signature over them still
    /// verifies and the owner password is untouched — neither of which survives the decrypt /
    /// re-encrypt round trip. See <see cref="PdfEncryptedIncrementalUpdate"/>.</para>
    /// </summary>
    public static class PdfEncryptedForms
    {
        /// <summary>
        /// Fills form fields in an encrypted document and appends the result as one encrypted
        /// revision.
        /// </summary>
        /// <param name="input">The encrypted PDF. Read only.</param>
        /// <param name="output">Receives the original bytes followed by the appended revision.</param>
        /// <param name="password">User or owner password.</param>
        /// <param name="values">Fully-qualified field name → value, as for <see cref="PdfFormFiller"/>.</param>
        /// <param name="options">Fill policy; may be null.</param>
        /// <returns>What was filled, unmatched, or written without an appearance.</returns>
        public static FormFillResult Fill(
            Stream input,
            Stream output,
            string password,
            IDictionary<string, string> values,
            FormFillOptions options = null)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));

            FormFillResult result = null;
            PdfEncryptedIncrementalUpdate.Run(input, output, password, (document, firstFreeObjectNumber) =>
            {
                FormFillPlan plan = PdfFormFiller.Plan(document, values, options, firstFreeObjectNumber);
                result = plan.Result;
                return plan.Objects;
            });

            return result;
        }

        /// <summary>
        /// Reads the AcroForm of an encrypted document — names, types, values, choice options,
        /// check-box states and widget positions.
        /// </summary>
        /// <remarks>
        /// <b>Superseded.</b> <see cref="PdfFormReader.GetFields(Stream, string)"/> takes a
        /// password and reaches the same code — the decryptor is registered through
        /// <c>PdfDecryptorHolder</c>, which inverts the <c>Encryption -> Base</c> dependency, so
        /// the read never needed this assembly. This member remains and still works; it is
        /// deprecated rather than removed because it is public API.
        /// <para>The <b>write</b> members of this class are NOT deprecated and have no Base
        /// equivalent: filling a field re-encrypts the appended revision with the document's own
        /// key, which only this assembly can do.</para>
        /// </remarks>
        [Obsolete("Use Tetitulz.Pdf.Base.Operations.Forms.PdfFormReader.GetFields(Stream, string), " +
                  "which takes the same password and returns the same fields. Requires " +
                  "PdfEncryptionOperations.Register() at startup. The write members here are not " +
                  "deprecated.", error: false)]
        public static IReadOnlyList<PdfFormFieldInfo> GetFields(Stream input, string password)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            using var decrypted = new MemoryStream();
            PdfDecryptor.Decrypt(input, decrypted, password);
            decrypted.Position = 0;
            return PdfFormReader.GetFields(decrypted);
        }
    }
}
