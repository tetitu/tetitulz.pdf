// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Signing;

namespace Tetitulz.Pdf.Signing.Models
{
    /// <summary>
    /// Interface for external signing providers (PKCS#11 tokens, cloud HSMs, smart cards).
    /// Implement this to sign PDFs with keys that never leave a remote device.
    ///
    /// <para>Usage with PdfSigner:</para>
    /// <code>
    ///   var signer = new PdfSigner();
    ///   signer.Sign(input, output, new SignatureParameters
    ///   {
    ///       ExternalSigner = new MyPkcs11Signer("slot-id", "pin"),
    ///       DigestAlgorithm = "SHA-256",
    ///   });
    /// </code>
    /// </summary>
    public interface IExternalSigner
    {
        /// <summary>
        /// Signs a message digest using the external key material.
        /// <para>
        /// PdfSigner ALWAYS calls <see cref="SignData"/>; it never calls
        /// <see cref="SignDigest"/> directly.  This method is provided as a
        /// convenience for implementations whose remote signer takes a
        /// pre-computed digest (HSMs that exist purely as raw RSA-private-key
        /// engines).  Such implementations can leave <see cref="SignData"/>
        /// as a thin wrapper that hashes locally and calls SignDigest.
        /// </para>
        /// <para>
        /// The implementation should NOT hash the data — <paramref name="digest"/> is
        /// already the hash of the byte-range content.
        /// </para>
        /// </summary>
        /// <param name="digest">The hash of the PDF byte range to sign.</param>
        /// <param name="digestAlgorithm">Algorithm used to compute <paramref name="digest"/> (e.g., "SHA-256").</param>
        /// <returns>DER-encoded PKCS#7/CMS detached signature bytes.</returns>
        byte[] SignDigest(byte[] digest, string digestAlgorithm);

        /// <summary>
        /// Signs raw data (the full byte-range content) and returns PKCS#7/CMS bytes.
        /// <para>
        /// THIS IS THE METHOD PdfSigner CALLS.  Implementations should produce
        /// a complete PKCS#7 / CMS detached signature over <paramref name="data"/>;
        /// they may use any internal hashing strategy that suits the underlying
        /// signing device.
        /// </para>
        /// <para>
        /// Implementations that only have access to a raw key-signing primitive
        /// can hash locally and delegate to <see cref="SignDigest"/>.
        /// </para>
        /// </summary>
        /// <param name="data">The full byte-range content to sign.</param>
        /// <param name="digestAlgorithm">Digest algorithm name.</param>
        /// <returns>DER-encoded PKCS#7/CMS detached signature bytes.</returns>
        byte[] SignData(byte[] data, string digestAlgorithm);

        /// <summary>
        /// Returns the DER-encoded X.509 certificate chain for the signing key.
        /// The signer's certificate must be first in the list.
        /// </summary>
        IList<byte[]> GetCertificateChain();

        /// <summary>
        /// Estimated size (in bytes) of the PKCS#7 signature produced by this signer.
        /// Used to reserve space in the /Contents placeholder.
        /// </summary>
        int EstimatedSignatureSize { get; }
    }
}
