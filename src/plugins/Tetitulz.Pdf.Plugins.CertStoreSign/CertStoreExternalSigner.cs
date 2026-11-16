// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.Plugins.CertStoreSign
{
    /// <summary>
    /// An <see cref="IExternalSigner"/> backed by an <see cref="X509Certificate2"/> whose private
    /// key lives in the operating system's key store — including a smart card, a USB token or an
    /// HSM, where the key never leaves the device.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this ships at all.</b> <c>SignatureParameters.ExternalSigner</c> is the right
    /// abstraction and does not change; but with no implementation in the box, every consumer that
    /// signs from a token writes the same adapter, and smart-card signing is the default in some
    /// markets. This is a convenience over the interface, not a replacement for it.</para>
    ///
    /// <para><b>The key is never exported.</b> <see cref="SignedCms"/> signs through the
    /// certificate's own CNG/CAPI handle, so a non-exportable key works — which is the entire point
    /// of a token. That also means the signing call may block on a PIN prompt; see
    /// <see cref="Silent"/>.</para>
    ///
    /// <para><b>It is a plugin because it is platform state, not because of a dependency.</b> The
    /// certificate store is an ambient, machine-specific resource: what it contains depends on who
    /// is logged in and what is plugged in. <c>Tetitulz.Pdf.Signing</c> must stay a library that
    /// behaves the same everywhere, so the thing that reaches into the machine lives out here.</para>
    /// </remarks>
    public sealed class CertStoreExternalSigner : IExternalSigner, IDisposable
    {
        private readonly X509Certificate2 _certificate;
        private readonly X509Certificate2Collection? _extraChainCerts;
        private readonly bool _ownsCertificate;

        /// <summary>Signs with <paramref name="certificate"/>.</summary>
        /// <param name="certificate">
        /// The signing certificate. Must have an accessible private key; the key itself is never
        /// read, only used.
        /// </param>
        /// <param name="extraChainCertificates">
        /// Intermediates to include when the store cannot build the chain itself — common with a
        /// token that holds only the leaf.
        /// </param>
        /// <param name="ownsCertificate">
        /// Whether <see cref="Dispose"/> should dispose <paramref name="certificate"/>. False by
        /// default: a caller who found the certificate themselves usually still owns it, and
        /// disposing someone else's <see cref="X509Certificate2"/> releases a key handle they are
        /// still using.
        /// </param>
        public CertStoreExternalSigner(
            X509Certificate2 certificate,
            X509Certificate2Collection? extraChainCertificates = null,
            bool ownsCertificate = false)
        {
            _certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
            if (!_certificate.HasPrivateKey)
                throw new ArgumentException(
                    "The certificate has no accessible private key, so it cannot sign. A store " +
                    "lookup that returns the public certificate only will land here.",
                    nameof(certificate));

            _extraChainCerts = extraChainCertificates;
            _ownsCertificate = ownsCertificate;
        }

        /// <summary>
        /// Whether the signing call must not show UI. <c>false</c> — the default — lets the key
        /// store prompt for a smart-card PIN, which an interactive host needs; a service should set
        /// it <c>true</c> so a missing PIN fails instead of hanging on a dialog no one can see.
        /// </summary>
        public bool Silent { get; set; }

#if NET
        /// <summary>
        /// The PIN of the device that holds the key — a smart card, a USB token — or the password
        /// of a key kept under one, handed to the key's provider so that the provider does not ask
        /// for it. <c>null</c> — the default — hands nothing over: the provider asks, or fails
        /// under <see cref="Silent"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>A wrong PIN is the provider's refusal</b>, a <see cref="CryptographicException"/>
        /// raised before anything is signed where the provider checks the PIN as it is handed over,
        /// and it counts against the device's tries: a host that keeps a PIN forgets it at the
        /// first refusal, since a device locks after a few. <b>A key that takes no PIN refuses one
        /// handed to it</b>, so a host hands a PIN over only after the key has asked for one — a
        /// signing call under <see cref="Silent"/> that failed.</para>
        ///
        /// <para><b>Windows, and the net10.0 build.</b> The PIN travels on the key's own handle: a
        /// key of a key storage provider takes it as its PIN property, a key of an older
        /// cryptographic service provider as the password it is opened with. The CMS is then
        /// signed through that handle; the netstandard2.0 build has no way to hand one to the CMS
        /// signer, so it has no such property. <see cref="Silent"/> governs the key the store
        /// opens when no PIN is handed over; with one, whether a window can still appear is the
        /// provider's.</para>
        /// </remarks>
        public string? KeyPin { get; set; }
#endif

        /// <summary>
        /// Which certificates to embed in the CMS. <see cref="X509IncludeOption.WholeChain"/> by
        /// default, because a PAdES verifier that cannot build the chain cannot report anything
        /// better than "unknown" — and the chain is what <c>/DSS</c> revocation evidence is keyed
        /// to.
        /// </summary>
        public X509IncludeOption IncludeOption { get; set; } = X509IncludeOption.WholeChain;

        /// <inheritdoc/>
        public byte[] SignData(byte[] data, string digestAlgorithm)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            var signer = new CmsSigner(_certificate)
            {
                DigestAlgorithm = new Oid(DigestOid(digestAlgorithm)),
                IncludeOption = IncludeOption,
            };

            if (_extraChainCerts != null)
                foreach (X509Certificate2 c in _extraChainCerts)
                    signer.Certificates.Add(c);

            // ESS signing-certificate-v2 (RFC 5035), which PAdES requires of a CMS written under
            // /ETSI.CAdES.detached (EN 319 142-1 §6.3; ISO 32000-2 §12.8.3.4): the signer's
            // certificate, hashed with the signature's digest, bound into the signed attributes.
            // With any signed attribute present, SignedCms adds the content-type and message-digest
            // attributes CMS requires beside it; without one it signed the digest alone.
            signer.SignedAttributes.Add(SigningCertificateV2(_certificate, digestAlgorithm));

#if NET
            // The key is opened here, handed its PIN, and used for this one signature: the CMS
            // signer would otherwise open a handle of its own, which has no PIN.
            using AsymmetricAlgorithm? unlocked = KeyPin != null ? OpenKeyWithPin(KeyPin) : null;
            if (unlocked != null) signer.PrivateKey = unlocked;
#endif

            // detached: the PDF byte range is the content and is not carried inside the CMS.
            var cms = new SignedCms(new ContentInfo(data), detached: true);
            cms.ComputeSignature(signer, Silent);
            return cms.Encode();
        }

        /// <summary>id-aa-signingCertificateV2 (RFC 5035 §3).</summary>
        private const string SigningCertificateV2Oid = "1.2.840.113549.1.9.16.2.47";

        /// <summary>
        /// The <c>SigningCertificateV2</c> attribute for <paramref name="certificate"/>:
        /// <c>SEQUENCE { certs SEQUENCE OF ESSCertIDv2 }</c>, one <c>ESSCertIDv2</c> holding the
        /// certificate's hash under the signature's digest. SHA-256 is the type's DEFAULT algorithm
        /// and so is omitted, as DER requires; another digest is named by its AlgorithmIdentifier
        /// without parameters (RFC 5754 §2).
        /// </summary>
        private static AsnEncodedData SigningCertificateV2(X509Certificate2 certificate, string digestAlgorithm)
        {
            string oid = DigestOid(digestAlgorithm);
            byte[] algorithmIdentifier;
            byte[] hash;
            switch (oid)
            {
                case "2.16.840.1.101.3.4.2.1":
                    algorithmIdentifier = Array.Empty<byte>();
                    using (var sha = SHA256.Create()) hash = sha.ComputeHash(certificate.RawData);
                    break;
                case "2.16.840.1.101.3.4.2.2":
                    algorithmIdentifier = Der(0x30, new byte[] { 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x02 });
                    using (var sha = SHA384.Create()) hash = sha.ComputeHash(certificate.RawData);
                    break;
                case "2.16.840.1.101.3.4.2.3":
                    algorithmIdentifier = Der(0x30, new byte[] { 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x03 });
                    using (var sha = SHA512.Create()) hash = sha.ComputeHash(certificate.RawData);
                    break;
                default:
                    algorithmIdentifier = Der(0x30, new byte[] { 0x06, 0x05, 0x2B, 0x0E, 0x03, 0x02, 0x1A });
                    using (var sha = SHA1.Create()) hash = sha.ComputeHash(certificate.RawData);
                    break;
            }

            byte[] essCertId = Der(0x30, Concat(algorithmIdentifier, Der(0x04, hash)));
            byte[] value = Der(0x30, Der(0x30, essCertId));
            return new AsnEncodedData(new Oid(SigningCertificateV2Oid), value);
        }

        /// <summary>One DER TLV of <paramref name="tag"/> over <paramref name="content"/> (lengths below 65 536).</summary>
        private static byte[] Der(byte tag, byte[] content)
        {
            byte[] length = content.Length < 0x80 ? new[] { (byte)content.Length }
                : content.Length < 0x100 ? new byte[] { 0x81, (byte)content.Length }
                : new byte[] { 0x82, (byte)(content.Length >> 8), (byte)content.Length };
            return Concat(new[] { tag }, length, content);
        }

        private static byte[] Concat(params byte[][] parts)
        {
            int total = 0;
            foreach (byte[] p in parts) total += p.Length;
            var result = new byte[total];
            int at = 0;
            foreach (byte[] p in parts) { Buffer.BlockCopy(p, 0, result, at, p.Length); at += p.Length; }
            return result;
        }

#if NET
        /// <summary>
        /// The certificate's private key, opened and handed <paramref name="pin"/>; the caller
        /// disposes it.
        /// </summary>
        private AsymmetricAlgorithm OpenKeyWithPin(string pin)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "A key's PIN is handed to its provider through Windows' key storage. Leave " +
                    "KeyPin unset on other systems.");

            AsymmetricAlgorithm? key =
                (AsymmetricAlgorithm?)_certificate.GetRSAPrivateKey() ?? _certificate.GetECDsaPrivateKey();
            try
            {
                switch (key)
                {
                    case RSACng rsa:
                        SetPin(rsa.Key, pin);
                        return rsa;
                    case ECDsaCng ecdsa:
                        SetPin(ecdsa.Key, pin);
                        return ecdsa;
                    case RSACryptoServiceProvider legacy:
                        var reopened = new RSACryptoServiceProvider(CspParametersWithPin(legacy.CspKeyContainerInfo, pin));
                        legacy.Dispose();
                        return reopened;
                    default:
                        throw new NotSupportedException(
                            "The certificate's key is not one a PIN can be handed to: it is neither " +
                            "an RSA nor an ECDSA key of a Windows provider. Leave KeyPin unset to " +
                            "let the provider ask.");
                }
            }
            catch
            {
                key?.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Hands <paramref name="pin"/> to a key storage provider's key: its PIN property
        /// (<c>NCRYPT_PIN_PROPERTY</c>), a null-terminated UTF-16 string.
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void SetPin(CngKey key, string pin)
        {
            byte[] value = System.Text.Encoding.Unicode.GetBytes(pin + "\0");
            try
            {
                key.SetProperty(new CngProperty("SmartCardPin", value, CngPropertyOptions.None));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(value);
            }
        }

        /// <summary>
        /// What opens the key <paramref name="key"/> describes — the same provider, container and
        /// key number, in the same store, and never a new key — with <paramref name="pin"/> as the
        /// key's password, which is how a cryptographic service provider takes a PIN.
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        internal static CspParameters CspParametersWithPin(CspKeyContainerInfo key, string pin)
        {
            var password = new System.Security.SecureString();
            foreach (char c in pin) password.AppendChar(c);
            password.MakeReadOnly();

            return new CspParameters(key.ProviderType, key.ProviderName, key.KeyContainerName)
            {
                KeyNumber = (int)key.KeyNumber,
                Flags = CspProviderFlags.UseExistingKey
                        | (key.MachineKeyStore ? CspProviderFlags.UseMachineKeyStore : CspProviderFlags.NoFlags),
                KeyPassword = password,
            };
        }
#endif

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        /// <remarks>
        /// <b>Not supported here, and the refusal is the honest answer.</b> <c>SignedCms</c> signs
        /// content, not a bare digest: it computes the digest itself so it can put the matching
        /// <c>messageDigest</c> attribute in the signed attributes. Handed only a hash there is no
        /// way to produce a CMS whose attributes agree with it. <c>PdfSigner</c> only ever calls
        /// <see cref="SignData"/>, which the interface says outright.
        /// </remarks>
        public byte[] SignDigest(byte[] digest, string digestAlgorithm)
            => throw new NotSupportedException(
                "This signer builds a CMS over the content, so it cannot sign a pre-computed " +
                "digest. Use SignData, which is the method PdfSigner calls.");

        /// <inheritdoc/>
        public IList<byte[]> GetCertificateChain()
        {
            var result = new List<byte[]> { _certificate.RawData };

            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            // The chain is being collected, not judged: a self-signed or partially-trusted chain
            // must still be embedded so a verifier can see what was claimed. Trust is decided by
            // the verifier's own anchors (SignatureVerificationOptions.TrustAnchors), not here.
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;

            if (chain.Build(_certificate))
            {
                for (int i = 1; i < chain.ChainElements.Count; i++)
                    result.Add(chain.ChainElements[i].Certificate.RawData);
            }

            if (_extraChainCerts != null)
            {
                foreach (X509Certificate2 c in _extraChainCerts)
                {
                    byte[] raw = c.RawData;
                    if (!ContainsRaw(result, raw)) result.Add(raw);
                }
            }

            return result;
        }

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        /// <remarks>
        /// Measured from the chain rather than guessed: the reservation has to hold the whole CMS,
        /// and an under-estimate is not a slow path but a failed signature. The headroom covers the
        /// signed attributes, the signature itself and a timestamp token if one is added later.
        /// </remarks>
        public int EstimatedSignatureSize
        {
            get
            {
                int certBytes = 0;
                foreach (byte[] c in GetCertificateChain()) certBytes += c.Length;
                return certBytes + 8192;
            }
        }

        /// <summary>Releases the certificate when this instance was told it owns it.</summary>
        public void Dispose()
        {
            if (_ownsCertificate) _certificate.Dispose();
        }

        private static bool ContainsRaw(List<byte[]> list, byte[] candidate)
        {
            foreach (byte[] existing in list)
            {
                if (existing.Length != candidate.Length) continue;
                bool same = true;
                for (int i = 0; i < existing.Length; i++)
                    if (existing[i] != candidate[i]) { same = false; break; }
                if (same) return true;
            }
            return false;
        }

        /// <summary>
        /// The OID of the digest this signer will use, from the library's digest name.
        /// </summary>
        /// <remarks>
        /// <para><b>One reading of the name — <c>Core.Constants.PdfDigestOids.FromName</c> — and a
        /// deliberately shorter allow-list than <c>PdfSigner</c>'s.</b> The CMS here is made by
        /// <see cref="System.Security.Cryptography.Pkcs.CmsSigner"/>, whose digest support is the
        /// platform's, and SHA-3 is available to it on some Windows builds and not others. A
        /// signer that accepts a name and then fails inside the platform would report the failure
        /// as the platform's; refusing here reports it as this plugin's, which is where the choice
        /// was made. SHA-1 stays for the signature dictionaries that still declare it.</para>
        /// </remarks>
        private static string DigestOid(string digestAlgorithm)
        {
            string oid = Core.Constants.PdfDigestOids.FromName(digestAlgorithm ?? "SHA-256");
            if (oid == Core.Constants.PdfDigestOids.Sha1 || oid == Core.Constants.PdfDigestOids.Sha256
                || oid == Core.Constants.PdfDigestOids.Sha384 || oid == Core.Constants.PdfDigestOids.Sha512)
                return oid;

            throw new ArgumentException(
                $"Unsupported digest algorithm '{digestAlgorithm}'. This signer accepts SHA-1, SHA-256, " +
                "SHA-384 and SHA-512.", nameof(digestAlgorithm));
        }
    }
}