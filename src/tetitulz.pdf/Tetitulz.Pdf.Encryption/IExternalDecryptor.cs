// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// A recipient key that stays on a device — a PKCS#11 token, a smart card, a cloud HSM, or a
    /// non-exportable CNG key in the Windows certificate store — for opening a document encrypted
    /// with the <c>/Adobe.PubSec</c> handler.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the decrypt-side counterpart of <c>IExternalSigner</c>, and it exists for
    /// the same reason.</b> <see cref="PdfPublicKeyDecryptor"/> recovers the file key through
    /// BouncyCastle, which needs an <c>AsymmetricKeyParameter</c> — key material, not a handle to
    /// it. A key that never leaves a card cannot produce one, so without this seam the only way in
    /// was a PKCS#12 file on disk: exactly the thing a hardware key exists to avoid.</para>
    ///
    /// <para><b>The seam is the CMS envelope, not the RSA primitive.</b> A raw
    /// <c>Decrypt(encryptedKey)</c> would have made every implementer reimplement CMS content
    /// decryption, and it is the mirror image of <c>IExternalSigner.SignData</c>, which is handed
    /// data and returns a complete CMS. Here the implementation is handed a complete CMS and
    /// returns data. On Windows that is one call to
    /// <c>System.Security.Cryptography.Pkcs.EnvelopedCms.Decrypt</c>, which works with a
    /// non-exportable key; a PKCS#11 implementation hands the blob to its middleware.</para>
    ///
    /// <para><b>Only the seed crosses the seam; the key derivation stays here.</b> The file
    /// encryption key is <c>H(seed ‖ recipient₁ ‖ recipient₂ ‖ …)</c> over every entry of
    /// <c>/Recipients</c> in file order — so an implementation that opened one envelope has not
    /// recovered the key and cannot get it wrong.</para>
    ///
    /// <para><b><see cref="UnwrapEnvelope"/> is called for one envelope each time the document's key is recovered</b> —
    /// <c>/Recipients</c> is attacker-controlled and may hold hundreds of entries; the library
    /// matches <see cref="GetRecipientCertificate"/> against each envelope's recipient identifiers
    /// first and calls the device only for the one envelope actually addressed to it. That
    /// ordering is deliberate: a card that is asked to open envelopes not addressed to it prompts
    /// for a PIN repeatedly, and enough wrong attempts lock it — a hostile file must not be able
    /// to reach that. An operation that recovers the key more than once (an appended encrypted
    /// revision, then its PDF MAC seal) asks the device each time, never for another envelope.</para>
    /// </remarks>
    public interface IExternalDecryptor
    {
        /// <summary>
        /// The DER-encoded X.509 certificate whose private key the device holds.
        /// </summary>
        /// <remarks>
        /// Public data, and it is what lets the library decide which envelope to send: the CMS
        /// recipient identifier names an issuer and serial number, or a subject key identifier,
        /// and both are read off this certificate. Returning the wrong certificate makes every
        /// document look like it was encrypted for somebody else.
        /// </remarks>
        /// <returns>The recipient certificate, DER-encoded.</returns>
        byte[] GetRecipientCertificate();

        /// <summary>
        /// Decrypts one <c>/Recipients</c> entry with the device's private key.
        /// </summary>
        /// <param name="cmsEnvelopedData">
        /// A DER-encoded CMS <c>EnvelopedData</c>, already checked to name
        /// <see cref="GetRecipientCertificate"/> as a recipient.
        /// </param>
        /// <returns>
        /// The envelope's plaintext content: a 20-byte seed followed by the 4-byte permissions mask
        /// (§7.6.5.3). Return <c>null</c> if the device declines; the caller reports that as a
        /// key that does not match its certificate rather than treating it as an empty seed.
        /// </returns>
        byte[] UnwrapEnvelope(byte[] cmsEnvelopedData);
    }
}
