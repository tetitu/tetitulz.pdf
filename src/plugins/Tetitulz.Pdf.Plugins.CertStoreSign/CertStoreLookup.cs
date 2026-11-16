// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Plugins.CertStoreSign
{
    /// <summary>
    /// Finds a signing certificate in the operating system's certificate store.
    /// </summary>
    /// <remarks>
    /// <para>Separate from <see cref="CertStoreExternalSigner"/> on purpose: the signer takes an
    /// <see cref="X509Certificate2"/> and does not care where it came from, so it can be tested
    /// with a generated certificate and used with one the host obtained any other way. This class
    /// is the only part that touches ambient machine state.</para>
    ///
    /// <para><b>Off Windows this finds little or nothing, and that is not a failure to report
    /// loudly.</b> <c>X509Store</c> exists on every platform, but on Linux and macOS the personal
    /// store is a file-backed collection that a smart-card middleware does not populate. The
    /// methods return null rather than throwing, so a cross-platform host can fall back.</para>
    /// </remarks>
    public static class CertStoreLookup
    {
        /// <summary>
        /// The certificate with this SHA-1 thumbprint that has a usable private key, or
        /// <c>null</c>.
        /// </summary>
        /// <remarks>
        /// Thumbprint rather than subject name, because a subject is not unique — a renewed
        /// certificate normally shares the old one's subject, and picking "the first match" would
        /// silently sign with whichever the store happened to enumerate first.
        /// </remarks>
        /// <param name="thumbprint">SHA-1 thumbprint; spaces and case are ignored.</param>
        /// <param name="location">Which store to search. CurrentUser is where a token appears.</param>
        public static X509Certificate2? FindByThumbprint(
            string thumbprint, StoreLocation location = StoreLocation.CurrentUser)
        {
            if (string.IsNullOrWhiteSpace(thumbprint)) return null;

            string wanted = thumbprint.Replace(" ", string.Empty).Trim();

            using var store = new X509Store(StoreName.My, location);
            try { store.Open(OpenFlags.ReadOnly); }
            catch (Exception) { return null; }   // no such store on this platform or no access

            foreach (X509Certificate2 candidate in store.Certificates)
            {
                if (!string.Equals(candidate.Thumbprint, wanted, StringComparison.OrdinalIgnoreCase))
                    continue;

                // HasPrivateKey is the only usable filter here: for a token the key is a handle,
                // and asking for the key object itself can raise a PIN prompt just to look.
                if (!candidate.HasPrivateKey) continue;
                return candidate;
            }

            return null;
        }

        /// <summary>
        /// Every certificate in the personal store that could sign a document — a private key, an
        /// unexpired validity window, and extensions that permit it
        /// (<see cref="PermitsDocumentSigning"/>).
        /// </summary>
        /// <remarks>
        /// For a host that wants to offer the user a choice. Expiry and purpose are filtered
        /// because a signature made with an expired certificate, or with one issued for a web
        /// server, is not merely imperfect: it will not validate, and offering one in a picker is
        /// offering a mistake. A personal store holds many certificates with private keys that were
        /// never meant to sign — a debugging proxy alone leaves hundreds, one per site.
        /// </remarks>
        /// <param name="location">Which store to search.</param>
        /// <param name="nowUtc">The instant to judge validity against.</param>
        public static X509Certificate2Collection FindUsableSigningCertificates(
            StoreLocation location, DateTime nowUtc)
        {
            var result = new X509Certificate2Collection();

            using var store = new X509Store(StoreName.My, location);
            try { store.Open(OpenFlags.ReadOnly); }
            catch (Exception) { return result; }

            foreach (X509Certificate2 candidate in store.Certificates)
            {
                if (!candidate.HasPrivateKey) continue;
                if (candidate.NotBefore.ToUniversalTime() > nowUtc) continue;
                if (candidate.NotAfter.ToUniversalTime() < nowUtc) continue;
                if (!PermitsDocumentSigning(candidate)) continue;
                result.Add(candidate);
            }

            return result;
        }

        /// <summary>
        /// Whether <paramref name="certificate"/>'s own extensions allow a document signature: a
        /// key usage, when it has one, with digital signature or non-repudiation; an extended key
        /// usage, when it has one, naming a purpose a document is signed under
        /// (<see cref="PdfEkuOids.PermitsDocumentSigning"/>). These are the two things the verifier
        /// refuses a signer's certificate for, read the way it reads them: an absent extension
        /// restricts nothing.
        /// </summary>
        public static bool PermitsDocumentSigning(X509Certificate2 certificate)
        {
            if (certificate == null) throw new ArgumentNullException(nameof(certificate));

            foreach (X509Extension extension in certificate.Extensions)
            {
                if (extension is X509KeyUsageExtension usage
                    && (usage.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) == 0)
                    return false;

                if (extension is X509EnhancedKeyUsageExtension purposes)
                {
                    bool permitted = false;
                    foreach (Oid purpose in purposes.EnhancedKeyUsages)
                        if (purpose.Value != null && PdfEkuOids.PermitsDocumentSigning(purpose.Value)) { permitted = true; break; }
                    if (!permitted) return false;
                }
            }
            return true;
        }
    }
}