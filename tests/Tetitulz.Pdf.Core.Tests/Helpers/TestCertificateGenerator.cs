// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace Tetitulz.Pdf.Core.Tests.Helpers;

internal static class TestCertificateGenerator
{
    public static (byte[] pfxBytes, string password) GenerateTestCertificate(
        string commonName = "CN=Test Signer")
    {
        const string password = "test123";
        var random = new SecureRandom();

        // RSA key pair, from the per-test pool (see SigningTestCrypto.Keys).
        var keyPair = SigningTestCrypto.Keys();

        // Generate self-signed certificate
        var certGen = new X509V3CertificateGenerator();
        var serialNumber = BigInteger.ProbablePrime(120, random);
        certGen.SetSerialNumber(serialNumber);

        var subjectDn = new X509Name(commonName);
        certGen.SetSubjectDN(subjectDn);
        certGen.SetIssuerDN(subjectDn);
        certGen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        certGen.SetNotAfter(DateTime.UtcNow.AddYears(1));
        certGen.SetPublicKey(keyPair.Public);

        var signatureFactory = new Org.BouncyCastle.Crypto.Operators.Asn1SignatureFactory(
            "SHA256WithRSA", keyPair.Private, random);
        var cert = certGen.Generate(signatureFactory);

        // Export as PKCS#12
        var store = new Pkcs12StoreBuilder().Build();
        store.SetKeyEntry("test",
            new AsymmetricKeyEntry(keyPair.Private),
            new[] { new X509CertificateEntry(cert) });

        using var pfxStream = new MemoryStream();
        store.Save(pfxStream, password.ToCharArray(), random);

        return (pfxStream.ToArray(), password);
    }

    /// <summary>
    /// Generates a self-signed test certificate with a controllable validity window and optional
    /// KeyUsage, returning the PFX (for signing) plus the BouncyCastle certificate (for use as a
    /// trust anchor). Used by chain-validation tests.
    /// </summary>
    public static (byte[] pfxBytes, string password, X509Certificate cert) GenerateCustom(
        string commonName = "CN=Test Signer",
        DateTime? notBefore = null,
        DateTime? notAfter = null,
        int? keyUsage = null)
    {
        const string password = "test123";
        var random = new SecureRandom();

        var keyPair = SigningTestCrypto.Keys();

        var certGen = new X509V3CertificateGenerator();
        certGen.SetSerialNumber(BigInteger.ProbablePrime(120, random));
        var dn = new X509Name(commonName);
        certGen.SetSubjectDN(dn);
        certGen.SetIssuerDN(dn);
        certGen.SetNotBefore(notBefore ?? DateTime.UtcNow.AddDays(-1));
        certGen.SetNotAfter(notAfter ?? DateTime.UtcNow.AddYears(1));
        certGen.SetPublicKey(keyPair.Public);
        if (keyUsage.HasValue)
            certGen.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(keyUsage.Value));

        var signatureFactory = new Org.BouncyCastle.Crypto.Operators.Asn1SignatureFactory(
            "SHA256WithRSA", keyPair.Private, random);
        var cert = certGen.Generate(signatureFactory);

        var store = new Pkcs12StoreBuilder().Build();
        store.SetKeyEntry("test",
            new AsymmetricKeyEntry(keyPair.Private),
            new[] { new X509CertificateEntry(cert) });

        using var pfxStream = new MemoryStream();
        store.Save(pfxStream, password.ToCharArray(), random);

        return (pfxStream.ToArray(), password, cert);
    }

    /// <summary>
    /// Self-signed test certificate on a named elliptic curve (e.g. <c>"P-256"</c>,
    /// <c>"P-384"</c>, <c>"P-521"</c>, or a Brainpool curve like <c>"brainpoolP256r1"</c> — any
    /// name <see cref="ECNamedCurveTable"/> recognises), signed with ECDSA over
    /// <paramref name="digestName"/> (e.g. <c>"SHA256withECDSA"</c>). Used to verify TS 32002 (EC
    /// curve families) works end to end through the actual signing/verification pipeline, not
    /// merely that the OID tables name the algorithm.
    /// </summary>
    public static (byte[] pfxBytes, string password) GenerateEcTestCertificate(
        string curveName, string digestName, string commonName = "CN=Test EC Signer")
    {
        const string password = "test123";
        var random = new SecureRandom();

        X9ECParameters curveParams = ECNamedCurveTable.GetByName(curveName)
            ?? throw new ArgumentException($"Unknown curve: {curveName}", nameof(curveName));
        var domainParams = new ECDomainParameters(
            curveParams.Curve, curveParams.G, curveParams.N, curveParams.H, curveParams.GetSeed());

        var keyGen = new ECKeyPairGenerator();
        keyGen.Init(new ECKeyGenerationParameters(domainParams, random));
        AsymmetricCipherKeyPair keyPair = keyGen.GenerateKeyPair();

        var certGen = new X509V3CertificateGenerator();
        certGen.SetSerialNumber(BigInteger.ProbablePrime(120, random));
        var dn = new X509Name(commonName);
        certGen.SetSubjectDN(dn);
        certGen.SetIssuerDN(dn);
        certGen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        certGen.SetNotAfter(DateTime.UtcNow.AddYears(1));
        certGen.SetPublicKey(keyPair.Public);

        var signatureFactory = new Org.BouncyCastle.Crypto.Operators.Asn1SignatureFactory(
            digestName, keyPair.Private, random);
        X509Certificate cert = certGen.Generate(signatureFactory);

        var store = new Pkcs12StoreBuilder().Build();
        store.SetKeyEntry("test",
            new AsymmetricKeyEntry(keyPair.Private),
            new[] { new X509CertificateEntry(cert) });

        using var pfxStream = new MemoryStream();
        store.Save(pfxStream, password.ToCharArray(), random);

        return (pfxStream.ToArray(), password);
    }

    /// <summary>
    /// Self-signed test certificate with an Ed25519 or Ed448 (RFC 8032) key — TS 32002's other EC
    /// curve family. EdDSA signs the whole message itself (no separate pre-hash digest to name).
    /// </summary>
    public static (byte[] pfxBytes, string password) GenerateEdDsaTestCertificate(
        bool ed448, string commonName = "CN=Test EdDSA Signer")
    {
        const string password = "test123";
        var random = new SecureRandom();

        AsymmetricCipherKeyPair keyPair;
        string signatureName;
        if (ed448)
        {
            var gen = new Ed448KeyPairGenerator();
            gen.Init(new Ed448KeyGenerationParameters(random));
            keyPair = gen.GenerateKeyPair();
            signatureName = "Ed448";
        }
        else
        {
            var gen = new Ed25519KeyPairGenerator();
            gen.Init(new Ed25519KeyGenerationParameters(random));
            keyPair = gen.GenerateKeyPair();
            signatureName = "Ed25519";
        }

        var certGen = new X509V3CertificateGenerator();
        certGen.SetSerialNumber(BigInteger.ProbablePrime(120, random));
        var dn = new X509Name(commonName);
        certGen.SetSubjectDN(dn);
        certGen.SetIssuerDN(dn);
        certGen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        certGen.SetNotAfter(DateTime.UtcNow.AddYears(1));
        certGen.SetPublicKey(keyPair.Public);

        var signatureFactory = new Org.BouncyCastle.Crypto.Operators.Asn1SignatureFactory(
            signatureName, keyPair.Private, random);
        X509Certificate cert = certGen.Generate(signatureFactory);

        var store = new Pkcs12StoreBuilder().Build();
        store.SetKeyEntry("test",
            new AsymmetricKeyEntry(keyPair.Private),
            new[] { new X509CertificateEntry(cert) });

        using var pfxStream = new MemoryStream();
        store.Save(pfxStream, password.ToCharArray(), random);

        return (pfxStream.ToArray(), password);
    }
}
