// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tsp;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;

namespace Tetitulz.Pdf.Signing.Tests.TestInfrastructure;

/// <summary>
/// Certificate / CA / TSA builders for this assembly's signing tests. Everything is generated in
/// memory — no files, no network — per the project's self-contained test rule.
///
/// <para><b>This is the union of two copies that coexisted until 2026-08-14.</b> A second
/// <c>SigningTestCrypto</c> lived in <c>Helpers/</c>, a leftover of the parallel-session merge
/// recorded in TODO Phase 13, and the two had drifted: this one had <c>IssuedIntermediate</c> and
/// a required-issuer <c>TsaCert</c>, that one had <c>SelfSignedLeaf</c> and an optional-issuer
/// <c>TsaCert</c>. Both were live — eight test files used one, four the other — and a plain
/// <c>using</c> naming both was a compile error, which is how it was noticed. The superset is
/// here; nothing was dropped.</para>
/// </summary>
/// <remarks>
/// A near-identical helper exists in <c>Tetitulz.Pdf.Core.Tests</c>. It is duplicated rather than
/// shared because test projects may not reference one another (CLAUDE.md, "No cross-project test
/// dependencies"); this copy carries only what the signing tests here need.
/// </remarks>
internal static class SigningTestCrypto
{
    internal sealed class Issued
    {
        public X509Certificate Cert { get; init; } = null!;
        public AsymmetricKeyParameter Private { get; init; } = null!;
    }

    private static readonly SecureRandom Random = new();

    /// <summary>
    /// An RSA key pair distinct from every other key this test has been given — from the shared
    /// pool in <see cref="Tetitulz.Pdf.TestHelpers.RsaTestKeys"/>, whose remarks carry the rules.
    /// </summary>
    public static AsymmetricCipherKeyPair Keys(int bits = 2048) => Tetitulz.Pdf.TestHelpers.RsaTestKeys.Next(bits);

    /// <summary>
    /// Builds <paramref name="make"/> with fresh, never-pooled keys — required for a fixture shared
    /// across tests (a <c>static</c> field or <c>static Lazy</c>). See
    /// <see cref="Tetitulz.Pdf.TestHelpers.RsaTestKeys.Shared{T}"/>.
    /// </summary>
    public static T Shared<T>(Func<T> make) => Tetitulz.Pdf.TestHelpers.RsaTestKeys.Shared(make);

    /// <summary>A self-signed CA able to issue certificates. Its window is deliberately wide so it
    /// is valid at every back-dated assessment time the timestamp tests use.</summary>
    public static Issued Ca(string cn = "CN=Test Root CA")
    {
        var kp = Keys();
        var dn = new X509Name(cn);
        var gen = NewGen(dn, dn, kp.Public, DateTime.UtcNow.AddYears(-20), DateTime.UtcNow.AddYears(20));
        gen.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(true));
        gen.AddExtension(X509Extensions.KeyUsage, true,
            new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.CrlSign | KeyUsage.DigitalSignature));
        return new Issued { Cert = gen.Generate(Factory(kp.Private)), Private = kp.Private };
    }

    /// <summary>An end-entity signing certificate.</summary>
    public static Issued Leaf(
        Issued issuer,
        string cn = "CN=Test Signer",
        DateTime? notBefore = null,
        DateTime? notAfter = null)
    {
        var kp = Keys();
        var gen = NewGen(issuer.Cert.SubjectDN, new X509Name(cn), kp.Public, notBefore, notAfter);
        gen.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature));
        return new Issued { Cert = gen.Generate(Factory(issuer.Private)), Private = kp.Private };
    }

    /// <summary>
    /// An intermediate CA with a caller-supplied key pair, so the SAME identity can be issued by
    /// two different roots — the cross-certification shape a CA root rollover produces, and the
    /// one that used to break chain building.
    /// </summary>
    public static Issued IssuedIntermediate(Issued issuer, AsymmetricCipherKeyPair keys, string cn)
    {
        var gen = NewGen(issuer.Cert.SubjectDN, new X509Name(cn), keys.Public,
            DateTime.UtcNow.AddYears(-10), DateTime.UtcNow.AddYears(10));
        gen.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(true));
        gen.AddExtension(X509Extensions.KeyUsage, true,
            new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.CrlSign | KeyUsage.DigitalSignature));
        return new Issued { Cert = gen.Generate(Factory(issuer.Private)), Private = keys.Private };
    }

    /// <summary>A timestamping certificate, issued by <paramref name="issuer"/> so it can be anchored.</summary>
    /// <summary>A self-signed end-entity certificate (no CA above it).</summary>
    public static Issued SelfSignedLeaf(string cn = "CN=Self Signed")
    {
        var kp = Keys();
        var dn = new X509Name(cn);
        var gen = NewGen(dn, dn, kp.Public);
        gen.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature));
        return new Issued { Cert = gen.Generate(Factory(kp.Private)), Private = kp.Private };
    }

    /// <summary>
    /// A timestamping certificate carrying the id-kp-timeStamping EKU RFC 3161 requires.
    /// <paramref name="issuer"/> is optional: with none the certificate is self-signed, which is
    /// what a test asserting "the TSA chain does not anchor" needs.
    /// </summary>
    /// <param name="keyBits">
    /// RSA modulus size. Below <c>SignatureVerificationOptions.MinimumRsaKeyBits</c> it lets a test
    /// drive the TSA-certificate half of the weak-algorithm policy, which is a different code path
    /// from the token's algorithm OIDs.
    /// </param>
    /// <param name="notBefore">Start of the validity period; twenty years ago by default.</param>
    /// <param name="notAfter">
    /// End of the validity period; twenty years ahead by default. A past one makes the authority an
    /// expired one — valid at an old token's time and not at the current time.
    /// </param>
    public static Issued TsaCert(Issued issuer = null, string cn = "CN=Test TSA", int keyBits = 2048,
        DateTime? notBefore = null, DateTime? notAfter = null)
    {
        var kp = Keys(keyBits);
        var dn = new X509Name(cn);
        var gen = NewGen(issuer?.Cert.SubjectDN ?? dn, dn, kp.Public,
            notBefore ?? DateTime.UtcNow.AddYears(-20), notAfter ?? DateTime.UtcNow.AddYears(20));
        gen.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature));
        gen.AddExtension(X509Extensions.ExtendedKeyUsage, true,
            new ExtendedKeyUsage(KeyPurposeID.id_kp_timeStamping));
        return new Issued
        {
            Cert = gen.Generate(Factory(issuer?.Private ?? kp.Private)),
            Private = kp.Private,
        };
    }

    /// <summary>
    /// A real RFC 3161 TimeStampToken over <paramref name="data"/> asserting
    /// <paramref name="genTime"/>, using SHA-256 throughout. <paramref name="chain"/> is embedded
    /// so the verifier can walk it.
    /// </summary>
    public static byte[] TimestampToken(
        byte[] data, Issued tsa, DateTime genTime, params X509Certificate[] chain)
        => TimestampToken(data, tsa, genTime, "SHA-256", chain);

    /// <summary>
    /// The same token with <paramref name="digestName"/> chosen by the caller — for the message
    /// imprint <i>and</i> for the TSA's own signature over the TSTInfo, which is how a real
    /// legacy TSA is configured.
    /// </summary>
    /// <remarks>
    /// Exists so a weak-algorithm test can mint a genuinely SHA-1 timestamp rather than assert
    /// about one. An unrecognised name throws instead of falling back to SHA-256: a silent
    /// substitution here would make a "SHA-1 is refused" test pass while verifying a SHA-256
    /// token, i.e. pass with the feature deleted.
    /// </remarks>
    public static byte[] TimestampToken(
        byte[] data, Issued tsa, DateTime genTime, string digestName, params X509Certificate[] chain)
        => TimestampToken(data, tsa, genTime, digestName, digestName, chain);

    /// <summary>
    /// The mixed shape: RFC 3161 lets the <b>requester</b> choose the message-imprint algorithm and
    /// the <b>TSA</b> choose the algorithm it signs the TSTInfo with, so the two are independent
    /// fields and a token may be SHA-256-signed over a SHA-1 imprint.
    /// </summary>
    /// <remarks>
    /// Needed to make the two halves of the policy separately falsifiable. With one name driving
    /// both, a mutation removing the imprint check survives — the SignerInfo check catches the same
    /// token first — which is how a live branch reads as dead code.
    /// </remarks>
    public static byte[] TimestampToken(
        byte[] data, Issued tsa, DateTime genTime,
        string imprintDigestName, string tokenDigestName, params X509Certificate[] chain)
    {
        byte[] imprint = DigestUtilities.CalculateDigest(imprintDigestName, data);
        var reqGen = new TimeStampRequestGenerator();
        // RFC 3161 §2.4.1: without certReq the TSA omits its certificate and the verifier has
        // nothing to check the signature — or the chain — against.
        reqGen.SetCertReq(true);
        var request = reqGen.Generate(new DerObjectIdentifier(DigestOid(imprintDigestName)), imprint);

        // The token's own signing-time attribute is the time it asserts, as a TSA's is. Left to the
        // generator it is the moment the test runs, and a token minted today under a certificate
        // that expired years ago then fails as a CMS — for the fixture's reason, not the file's.
        var signedAttributes = new Org.BouncyCastle.Asn1.Cms.AttributeTable(new Asn1EncodableVector(
            new Org.BouncyCastle.Asn1.Cms.Attribute(
                Org.BouncyCastle.Asn1.Cms.CmsAttributes.SigningTime,
                new DerSet(new Org.BouncyCastle.Asn1.Cms.Time(genTime)))));
        var gen = new TimeStampTokenGenerator(
            tsa.Private, tsa.Cert, DigestOid(tokenDigestName), "1.2.3.4.5", signedAttributes, null);

        var certs = new List<X509Certificate> { tsa.Cert };
        if (chain != null) certs.AddRange(chain);
        gen.SetCertificates(CollectionUtilities.CreateStore(certs));

        return gen.Generate(request, BigInteger.ValueOf(NextSerial()), genTime).GetEncoded();
    }

    /// <summary>
    /// Digest name to OID. An unrecognised name throws instead of falling back to SHA-256: a silent
    /// substitution here would make a "SHA-1 is refused" test pass while verifying a SHA-256 token,
    /// i.e. pass with the feature deleted.
    /// </summary>
    private static string DigestOid(string digestName) => digestName switch
    {
        "SHA-256" => "2.16.840.1.101.3.4.2.1",
        "SHA-1"   => "1.3.14.3.2.26",
        _ => throw new ArgumentException(
                 $"Unsupported timestamp digest name '{digestName}'.", nameof(digestName)),
    };

    /// <summary>Packages a leaf key plus its issuing chain into a PKCS#12 blob.</summary>
    public static (byte[] pfx, string password) Pfx(Issued leaf, params X509Certificate[] chain)
    {
        const string password = "test123";
        var entries = new List<Org.BouncyCastle.Pkcs.X509CertificateEntry> { new(leaf.Cert) };
        if (chain != null)
            foreach (var c in chain) entries.Add(new Org.BouncyCastle.Pkcs.X509CertificateEntry(c));

        var store = new Org.BouncyCastle.Pkcs.Pkcs12StoreBuilder().Build();
        store.SetKeyEntry("test", new Org.BouncyCastle.Pkcs.AsymmetricKeyEntry(leaf.Private), entries.ToArray());

        using var ms = new System.IO.MemoryStream();
        store.Save(ms, password.ToCharArray(), Random);
        return (ms.ToArray(), password);
    }

    private static long _serial = 1000;
    private static long NextSerial() => System.Threading.Interlocked.Increment(ref _serial);

    /// <summary>
    /// A self-signed certificate carrying one extra extension, for tests about how an extension's
    /// contents are read. The subject and validity are the CA builder's; only the extension varies.
    /// </summary>
    internal static X509Certificate CaWithExtension(
        DerObjectIdentifier oid, bool critical, Asn1Encodable value, string cn = "CN=Test Root CA")
    {
        var kp = Keys();
        var dn = new X509Name(cn);
        var gen = NewGen(dn, dn, kp.Public, DateTime.UtcNow.AddYears(-20), DateTime.UtcNow.AddYears(20));
        gen.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(true));
        gen.AddExtension(X509Extensions.KeyUsage, true,
            new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.CrlSign | KeyUsage.DigitalSignature));
        gen.AddExtension(oid, critical, value);
        return gen.Generate(Factory(kp.Private));
    }

    /// <summary>
    /// An end-entity certificate on a P-256 EC key, issued by <paramref name="issuer"/>'s RSA CA.
    /// </summary>
    /// <remarks>
    /// A certificate's signature algorithm and its subject key are independent, so an RSA CA
    /// issuing an EC leaf is the ordinary shape rather than a contrivance — and it is what makes
    /// the EC branch of a "which signature algorithm does this key use" decision reachable without
    /// a second CA.
    /// </remarks>
    public static Issued EcLeaf(Issued issuer, string cn = "CN=Test EC Signer")
    {
        var gen = new ECKeyPairGenerator();
        gen.Init(new ECKeyGenerationParameters(SecObjectIdentifiers.SecP256r1, Random));
        var kp = gen.GenerateKeyPair();

        var cert = NewGen(issuer.Cert.SubjectDN, new X509Name(cn), kp.Public);
        cert.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature));
        return new Issued { Cert = cert.Generate(Factory(issuer.Private)), Private = kp.Private };
    }

    private static X509V3CertificateGenerator NewGen(
        X509Name issuerDn, X509Name subjectDn, AsymmetricKeyParameter publicKey,
        DateTime? notBefore = null, DateTime? notAfter = null)
    {
        var gen = new X509V3CertificateGenerator();
        gen.SetSerialNumber(BigInteger.ValueOf(NextSerial()));
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
