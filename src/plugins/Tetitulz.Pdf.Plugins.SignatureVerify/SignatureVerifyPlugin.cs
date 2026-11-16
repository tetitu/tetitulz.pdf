// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Plugins;

namespace Tetitulz.Pdf.Plugins.SignatureVerify
{
    /// <summary>
    /// <see cref="ISignatureVerifyPlugin"/> that verifies all cryptographic signatures in a PDF
    /// using the built-in <see cref="PdfSignatureVerifier"/>.
    ///
    /// Registration:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new SignatureVerifyPlugin());
    /// </code>
    ///
    /// <para><b>Results come back with no trust anchors.</b> This contract carries no options, so
    /// <see cref="SignatureVerificationResult.IsFullyTrusted"/> is structurally false on this path
    /// — without anchors nothing can be <c>Trusted</c>. Callers needing a trust verdict must use
    /// <c>PdfSignatureVerifier</c> directly and pass
    /// <c>SignatureVerificationOptions.TrustAnchors</c>.</para>
    /// </summary>
    public sealed class SignatureVerifyPlugin : ISignatureVerifyPlugin
    {
        public string Name        => "Tetitulz.Plugins.SignatureVerify";
        public string Description => "Verifies all cryptographic signatures in a PDF document.";
        public Version Version    => new Version(1, 0, 0);

        public void Initialize() { }
        public void Shutdown()   { }

        /// <summary>
        /// Verifies every signature in <paramref name="pdfStream"/>.
        /// </summary>
        /// <exception cref="PdfVerificationException">
        /// The document is encrypted. <see cref="PdfDocument"/> skips the catalog — and therefore
        /// the <c>/AcroForm</c> — of an encrypted file, so <c>VerifyAll</c> would take its
        /// "no AcroForm" branch and return an <b>empty list</b>: the answer "this document is not
        /// signed" to a security question about a document that may well be signed. A caller
        /// reading "no results" as "nothing to verify" and one reading it as "no invalid
        /// signatures" would both be wrong, so this refuses instead. It mirrors the
        /// <c>PdfVerificationException</c> <c>PdfSignatureVerifier.Verify</c> already throws for
        /// the same condition; this assembly cannot decrypt, so decryption is the caller's step.
        /// </exception>
        public IReadOnlyList<SignatureVerificationResult> VerifyAll(Stream pdfStream)
        {
            if (pdfStream == null) throw new ArgumentNullException(nameof(pdfStream));
            using var doc = PdfDocument.Open(pdfStream);

            if (doc.IsEncrypted)
                throw new PdfVerificationException(
                    "SignatureVerifyPlugin: the document is encrypted, so its signature fields " +
                    "cannot be read and an empty result would falsely mean 'not signed'. Decrypt " +
                    "it first with Tetitulz.Pdf.Encryption and verify the result.");

            return new PdfSignatureVerifier().VerifyAll(doc);
        }
    }
}
