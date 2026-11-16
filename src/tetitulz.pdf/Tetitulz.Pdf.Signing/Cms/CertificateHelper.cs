// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Signing;

namespace Tetitulz.Pdf.Signing.Cms
{
    internal static class CertificateHelper
    {
        public static CertificateData LoadFromPfx(string path, string? password)
        {
            using (var stream = File.OpenRead(path))
            {
                return LoadFromPfx(stream, password);
            }
        }

        public static CertificateData LoadFromPfx(Stream pfxStream, string? password)
        {
            var store = new Pkcs12StoreBuilder().Build();
            try
            {
                store.Load(pfxStream, password?.ToCharArray() ?? Array.Empty<char>());
            }
            catch (Exception ex) when (!(ex is PdfException))
            {
                // BouncyCastle reports a wrong password as an IOException and bytes that are not
                // PKCS#12 as whatever its ASN.1 reader threw — neither is the exception a signing
                // caller catches, and the first is the one they will hit.
                throw new PdfSigningException(
                    "The signing certificate's PKCS#12 store could not be opened — wrong password, " +
                    "or not a PKCS#12 file: " + ex.Message, ex);
            }

            AsymmetricKeyParameter? privateKey = null;
            X509Certificate? signerCert = null;
            var chain = new List<X509Certificate>();

            foreach (string alias in store.Aliases)
            {
                if (store.IsKeyEntry(alias))
                {
                    var keyEntry = store.GetKey(alias);
                    if (keyEntry.Key.IsPrivate)
                    {
                        privateKey = keyEntry.Key;
                        var certChain = store.GetCertificateChain(alias);
                        if (certChain != null && certChain.Length > 0)
                        {
                            signerCert = certChain[0].Certificate;
                            foreach (var ce in certChain)
                                chain.Add(ce.Certificate);
                        }
                        break;
                    }
                }
            }

            if (privateKey == null || signerCert == null)
                throw new PdfSigningException("No private key or certificate found in the PKCS#12 store.");

            return new CertificateData(signerCert, chain) { PrivateKey = privateKey };
        }

        /// <summary>
        /// Creates CertificateData from DER-encoded certificate bytes (for external signers).
        /// The first certificate in the list is the signer; remaining are the chain.
        /// PrivateKey is null — the external device holds it.
        /// </summary>
        public static CertificateData LoadFromDer(IList<byte[]> derCerts)
        {
            if (derCerts == null || derCerts.Count == 0)
                throw new PdfSigningException("No certificates provided.");

            var parser = new X509CertificateParser();
            var chain = new List<X509Certificate>();
            foreach (byte[] der in derCerts)
            {
                var cert = parser.ReadCertificate(der);
                if (cert != null) chain.Add(cert);
            }

            if (chain.Count == 0)
                throw new PdfSigningException("Could not parse any certificates from DER data.");

            // PrivateKey stays null: the external device holds it.
            return new CertificateData(chain[0], chain);
        }
    }

    internal sealed class CertificateData
    {
        /// <summary>
        /// Takes the two values that are never absent, so a <c>CertificateData</c> cannot exist
        /// without them.
        /// </summary>
        /// <remarks>
        /// <b>A constructor rather than <c>required</c>:</b> this assembly multi-targets
        /// <c>netstandard2.0</c>, whose reference assemblies carry no
        /// <c>RequiredMemberAttribute</c>, so <c>required</c> is a compile error on that leg. A
        /// constructor states the same invariant and compiles everywhere — and unlike <c>= null!</c>
        /// it is checked rather than asserted.
        /// </remarks>
        public CertificateData(X509Certificate certificate, List<X509Certificate> chain)
        {
            Certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
            Chain = chain ?? throw new ArgumentNullException(nameof(chain));
        }

        /// <summary>
        /// The signing key, or <c>null</c> when an external signer holds it — the one field that is
        /// genuinely absent on a legitimate path (<c>LoadFromDer</c> sets it null by design).
        /// </summary>
        public AsymmetricKeyParameter? PrivateKey { get; set; }

        public X509Certificate Certificate { get; }
        public List<X509Certificate> Chain { get; }
    }
}
