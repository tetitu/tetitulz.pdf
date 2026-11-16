// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tsp;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;

namespace Tetitulz.Pdf.Core.Tests.Helpers;

/// <summary>
/// Certificate / CA / TSA builders shared by the signing hardening tests. Everything is generated
/// in memory — no files, no network, per the project's self-contained test rule.
/// </summary>
internal static class SigningTestCrypto
{
    internal sealed class Issued
    {
        public X509Certificate Cert { get; init; }
        public AsymmetricKeyParameter Private { get; init; }
    }

    private static readonly SecureRandom Random = new();

    /// <summary>
    /// An RSA key pair distinct from every other key this test has been given — from the shared
    /// pool in <see cref="Tetitulz.Pdf.TestHelpers.RsaTestKeys"/>, whose remarks carry the rules
    /// (a key held by a <c>static</c> fixture must be made inside <c>RsaTestKeys.Shared</c>).
    /// Every RSA key this assembly's tests make comes from here.
    /// </summary>
    public static AsymmetricCipherKeyPair Keys(int bits = 2048) => Tetitulz.Pdf.TestHelpers.RsaTestKeys.Next(bits);

    /// <summary>A self-signed CA able to issue certificates and sign CRLs/OCSP.</summary>
    public static Issued Ca(string cn = "CN=Test Root CA", int pathLen = -1)
    {
        var kp = Keys();
        var dn = new X509Name(cn);
        // CAs get a wide window so they remain valid at the back-dated assessment times used by
        // the timestamp tests — a root that postdates the assessed instant would fail the chain
        // for the wrong reason.
        var gen = NewGen(dn, dn, kp.Public, DateTime.UtcNow.AddYears(-20), DateTime.UtcNow.AddYears(20));
        gen.AddExtension(X509Extensions.BasicConstraints, true,
            pathLen >= 0 ? new BasicConstraints(pathLen) : new BasicConstraints(true));
        gen.AddExtension(X509Extensions.KeyUsage, true,
            new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.CrlSign | KeyUsage.DigitalSignature));
        return new Issued { Cert = gen.Generate(Factory(kp.Private)), Private = kp.Private };
    }

    /// <summary>An intermediate CA issued by <paramref name="issuer"/>.</summary>
    public static Issued Intermediate(Issued issuer, string cn = "CN=Test Intermediate CA")
    {
        var kp = Keys();
        var gen = NewGen(issuer.Cert.SubjectDN, new X509Name(cn), kp.Public,
            DateTime.UtcNow.AddYears(-20), DateTime.UtcNow.AddYears(20));
        gen.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(true));
        gen.AddExtension(X509Extensions.KeyUsage, true,
            new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.CrlSign | KeyUsage.DigitalSignature));
        return new Issued { Cert = gen.Generate(Factory(issuer.Private)), Private = kp.Private };
    }

    /// <summary>An end-entity signing certificate.</summary>
    public static Issued Leaf(
        Issued issuer,
        string cn = "CN=Test Signer",
        DateTime? notBefore = null,
        DateTime? notAfter = null,
        int keyBits = 2048)
    {
        var kp = Keys(keyBits);
        var gen = NewGen(issuer.Cert.SubjectDN, new X509Name(cn), kp.Public, notBefore, notAfter);
        gen.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature));
        return new Issued { Cert = gen.Generate(Factory(issuer.Private)), Private = kp.Private };
    }

    /// <summary>A self-signed end-entity certificate (no CA above it).</summary>
    public static Issued SelfSignedLeaf(
        string cn = "CN=Self Signed",
        DateTime? notBefore = null,
        DateTime? notAfter = null,
        int keyBits = 2048)
    {
        var kp = Keys(keyBits);
        var dn = new X509Name(cn);
        var gen = NewGen(dn, dn, kp.Public, notBefore, notAfter);
        return new Issued { Cert = gen.Generate(Factory(kp.Private)), Private = kp.Private };
    }

    /// <summary>
    /// A timestamping certificate. <paramref name="issuer"/> null ⇒ self-signed, which is exactly
    /// the certificate an attacker can mint for themselves.
    /// </summary>
    public static Issued TsaCert(Issued issuer = null, string cn = "CN=Test TSA", bool withEku = true)
    {
        var kp = Keys();
        var dn = new X509Name(cn);
        var gen = NewGen(issuer?.Cert.SubjectDN ?? dn, dn, kp.Public);
        gen.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature));
        if (withEku)
            gen.AddExtension(X509Extensions.ExtendedKeyUsage, true,
                new ExtendedKeyUsage(KeyPurposeID.id_kp_timeStamping));
        return new Issued
        {
            Cert = gen.Generate(Factory(issuer?.Private ?? kp.Private)),
            Private = kp.Private,
        };
    }

    /// <summary>
    /// Builds a real RFC 3161 TimeStampToken over <paramref name="data"/>, asserting
    /// <paramref name="genTime"/>. <paramref name="chain"/> is embedded in the token so the
    /// verifier can walk it.
    /// </summary>
    public static byte[] TimestampToken(
        byte[] data, Issued tsa, DateTime genTime, params X509Certificate[] chain)
    {
        byte[] imprint = DigestUtilities.CalculateDigest("SHA-256", data);
        var reqGen = new TimeStampRequestGenerator();
        // RFC 3161 §2.4.1: without certReq the TSA omits its certificate from the token, and the
        // verifier then has nothing to check the signature — or the chain — against.
        reqGen.SetCertReq(true);
        var request = reqGen.Generate(new DerObjectIdentifier("2.16.840.1.101.3.4.2.1"), imprint);

        var gen = new TimeStampTokenGenerator(
            tsa.Private, tsa.Cert, "2.16.840.1.101.3.4.2.1", "1.2.3.4.5");

        var certs = new List<X509Certificate> { tsa.Cert };
        if (chain != null) certs.AddRange(chain);
        gen.SetCertificates(CollectionUtilities.CreateStore(certs));

        var token = gen.Generate(request, BigInteger.ValueOf(DateTimeSeed()), genTime);
        return token.GetEncoded();
    }

    /// <summary>
    /// Packages a leaf key plus its issuing chain into a PKCS#12 blob that
    /// <see cref="Tetitulz.Pdf.Signing.Models.SignatureParameters.CertificateStream"/> accepts, so the
    /// signer embeds the whole chain in the CMS and the verifier has something to walk.
    /// </summary>
    public static (byte[] pfx, string password) Pfx(Issued leaf, params X509Certificate[] chain)
    {
        const string password = "test123";
        var entries = new List<Org.BouncyCastle.Pkcs.X509CertificateEntry>
        {
            new(leaf.Cert),
        };
        if (chain != null)
            foreach (var c in chain) entries.Add(new Org.BouncyCastle.Pkcs.X509CertificateEntry(c));

        var store = new Org.BouncyCastle.Pkcs.Pkcs12StoreBuilder().Build();
        store.SetKeyEntry("test", new Org.BouncyCastle.Pkcs.AsymmetricKeyEntry(leaf.Private), entries.ToArray());

        using var ms = new System.IO.MemoryStream();
        store.Save(ms, password.ToCharArray(), Random);
        return (ms.ToArray(), password);
    }

    // Deterministic-enough serial without relying on Random for reproducibility of failures.
    private static long _seed = 1000;
    private static long DateTimeSeed() => System.Threading.Interlocked.Increment(ref _seed);

    private static X509V3CertificateGenerator NewGen(
        X509Name issuerDn, X509Name subjectDn, AsymmetricKeyParameter publicKey,
        DateTime? notBefore = null, DateTime? notAfter = null)
    {
        var gen = new X509V3CertificateGenerator();
        gen.SetSerialNumber(BigInteger.ValueOf(DateTimeSeed()));
        gen.SetIssuerDN(issuerDn);
        gen.SetSubjectDN(subjectDn);
        gen.SetNotBefore(notBefore ?? DateTime.UtcNow.AddDays(-1));
        gen.SetNotAfter(notAfter ?? DateTime.UtcNow.AddYears(5));
        gen.SetPublicKey(publicKey);
        return gen;
    }

    private static Asn1SignatureFactory Factory(AsymmetricKeyParameter key)
        => new("SHA256WithRSA", key, Random);
}
