// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Org.BouncyCastle.Asn1.Oiw;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;
using Org.BouncyCastle.Ocsp;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tsp;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Tetitulz.Pdf.Signing.Timestamping;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A public key whose verification costs more than its kind allows is refused before it is used —
/// <see cref="PublicKeyCost"/>, at its bounds, and at every site that verifies with a key a file or
/// a server supplied — and a chain walk spends one allowance of issuer verifications.
/// </summary>
/// <remarks>
/// <para>An RSA verification is an exponentiation by the public exponent, which the certificate's
/// author chooses: with a 16 384-bit modulus a 16 384-bit exponent took 8.7 s, 65 537 took 18 ms.
/// The costly keys here are a test key's own modulus with a 300-bit exponent — over FIPS 186-4's
/// 256 bits for a 2 048-bit modulus, and cheap — so a refusal is told from a failed signature by
/// <see cref="PublicKeyCost.Refusals"/>, never by a clock: the signatures here fail either way, the
/// key not being the one that signed.</para>
/// </remarks>
public sealed class CostlyPublicKeyTests
{
    private static readonly SecureRandom Random = new();
    private static long _serial = 91000;
    private static int _crlNumber;

    private static readonly byte[] Data = Encoding.ASCII.GetBytes("the bytes a signature covers");

    // ------------------------------------------------------------------ the bounds

    /// <summary>
    /// FIPS 186-4's e &lt; 2^256 up to a 3 072-bit modulus, OpenSSL's 64 bits above it — each at its
    /// edge, the modulus's included.
    /// </summary>
    [Theory]
    [InlineData(2048, 256, false)]
    [InlineData(2048, 257, true)]
    [InlineData(3072, 256, false)]
    [InlineData(3072, 257, true)]
    [InlineData(3073, 64, false)]
    [InlineData(3073, 65, true)]
    [InlineData(4096, 17, false)]
    public void Rsa_ExponentBound_FipsUpToThreeThousandBits_OpenSslAbove(int modulusBits, int exponentBits, bool refused)
    {
        var key = new RsaKeyParameters(false, Modulus(modulusBits), Exponent(exponentBits));

        string? why = PublicKeyCost.WhyTooCostly(key);

        Assert.Equal(refused, why != null);
        if (refused) Assert.Contains($"{exponentBits} bits", why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10000, 256, null)]
    [InlineData(10001, 256, "DSA p")]
    [InlineData(2048, 257, "DSA q")]
    public void Dsa_QAndPBounds(int pBits, int qBits, string? refusedFor)
    {
        string? why = PublicKeyCost.WhyTooCostly(DsaKey(pBits, qBits));

        if (refusedFor == null) Assert.Null(why);
        else Assert.Contains(refusedFor, why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(661, false)]
    [InlineData(662, true)]
    public void Ec_FieldBound(int fieldBits, bool refused)
    {
        string? why = PublicKeyCost.WhyTooCostly(EcKey(fieldBits));

        Assert.Equal(refused, why != null);
    }

    /// <summary><c>Affordable</c> throws what a verification throws for a key it cannot use, and counts it; a good key passes uncounted.</summary>
    [Fact]
    public void Affordable_ThrowsInvalidKeyAndCounts_AndPassesAGoodKey()
    {
        var good = (RsaKeyParameters)SigningTestCrypto.Keys().Public;
        int before = PublicKeyCost.Refusals;

        Assert.Same(good, PublicKeyCost.Affordable(good));
        Assert.Equal(before, PublicKeyCost.Refusals);

        var costly = new RsaKeyParameters(false, good.Modulus, Exponent(300));
        var ex = Assert.Throws<InvalidKeyException>(() => PublicKeyCost.Affordable(costly));
        Assert.Contains("300 bits", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before + 1, PublicKeyCost.Refusals);
    }

    // ------------------------------------------------------------------ the chain

    [Fact]
    public void Resolve_AnIssuerWithACostlyKey_IsRefusedBeforeTheLinkIsVerified()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        X509Certificate costlyCa = CostlyTwin(ca);

        var warnings = new List<string>();
        int before = PublicKeyCost.Refusals;
        var status = CertificateChainValidator.Resolve(leaf.Cert,
            CollectionUtilities.CreateStore(new List<X509Certificate> { costlyCa }), new[] { costlyCa }, DateTime.UtcNow, warnings);

        Assert.Equal(PdfSignatureChainStatus.Invalid, status);
        Assert.Equal(before + 1, PublicKeyCost.Refusals);
        Assert.Contains(warnings, w => w.Contains("public exponent", StringComparison.Ordinal));

        // The control: the CA's own certificate anchors the leaf, and nothing is refused.
        warnings.Clear();
        before = PublicKeyCost.Refusals;
        Assert.Equal(PdfSignatureChainStatus.Trusted, CertificateChainValidator.Resolve(leaf.Cert,
            CollectionUtilities.CreateStore(new List<X509Certificate> { ca.Cert }), new[] { ca.Cert }, DateTime.UtcNow, warnings));
        Assert.Equal(before, PublicKeyCost.Refusals);
    }

    [Fact]
    public void Resolve_ASelfSignedLeafWithACostlyKey_IsRefusedBeforeItsSelfSignatureIsVerified()
    {
        var keys = SigningTestCrypto.Keys();
        var name = new X509Name("CN=Costly Self Signed");
        X509Certificate leaf = Cert(name, name, Costly(keys.Public), keys.Private, ca: false);

        var warnings = new List<string>();
        int before = PublicKeyCost.Refusals;
        var status = CertificateChainValidator.Resolve(leaf, null, null, DateTime.UtcNow, warnings);

        Assert.Equal(PdfSignatureChainStatus.Invalid, status);
        Assert.Equal(before + 1, PublicKeyCost.Refusals);
        Assert.Contains(warnings, w => w.Contains("public exponent", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildChain_ACandidateWithACostlyKey_IsRefusedBeforeItIsTried()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);

        int before = PublicKeyCost.Refusals;
        CertificateChainValidator.BuildChain(leaf.Cert,
            CollectionUtilities.CreateStore(new List<X509Certificate> { CostlyTwin(ca) }));

        Assert.Equal(before + 1, PublicKeyCost.Refusals);
    }

    /// <summary>
    /// A bag of a hundred same-name decoys whose keys do not verify the leaf: the search tries
    /// <see cref="CertificateChainValidator.MaxIssuerVerifications"/> of them and stops, saying so.
    /// The control is three decoys before the genuine issuer: four verifications, and trusted.
    /// </summary>
    [Fact]
    public void AdversarialResolve_ManySameNameDecoys_SpendsOneAllowanceOfVerifications()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);

        var warnings = new List<string>();
        int before = CertificateChainValidator.IssuerVerifications;
        var status = CertificateChainValidator.Resolve(leaf.Cert,
            CollectionUtilities.CreateStore(Decoys(ca, 100)), new[] { ca.Cert }, DateTime.UtcNow, warnings);

        Assert.NotEqual(PdfSignatureChainStatus.Trusted, status);
        Assert.Equal(CertificateChainValidator.MaxIssuerVerifications, CertificateChainValidator.IssuerVerifications - before);
        Assert.Contains(warnings, w => w.Contains("verifications", StringComparison.Ordinal));

        var bag = Decoys(ca, 3);
        bag.Add(ca.Cert);
        before = CertificateChainValidator.IssuerVerifications;
        Assert.Equal(PdfSignatureChainStatus.Trusted, CertificateChainValidator.Resolve(leaf.Cert,
            CollectionUtilities.CreateStore(bag), new[] { ca.Cert }, DateTime.UtcNow, new List<string>()));
        Assert.Equal(4, CertificateChainValidator.IssuerVerifications - before);
    }

    /// <summary>
    /// <c>BuildChain</c> spends the same allowance across its hops, and follows no name match whose key
    /// did not verify: a decoy is no issuer, and revocation data checked under it says nothing. The
    /// control: the real issuer after a few decoys is followed.
    /// </summary>
    [Fact]
    public void AdversarialBuildChain_ManySameNameDecoys_SpendsOneAllowance_AndFollowsNoUnverifiedIssuer()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        List<X509Certificate> decoys = Decoys(ca, 100);

        int before = CertificateChainValidator.IssuerVerifications;
        List<X509Certificate> chain = CertificateChainValidator.BuildChain(leaf.Cert, CollectionUtilities.CreateStore(decoys));

        Assert.Equal(CertificateChainValidator.MaxIssuerVerifications, CertificateChainValidator.IssuerVerifications - before);
        Assert.Equal(new[] { leaf.Cert }, chain);

        var few = Decoys(ca, 3);
        few.Add(ca.Cert);
        Assert.Equal(new[] { leaf.Cert, ca.Cert }, CertificateChainValidator.BuildChain(leaf.Cert, CollectionUtilities.CreateStore(few)));
    }

    // ------------------------------------------------------------------ the CMS signer

    [Fact]
    public void CmsSigner_WithACostlyKey_IsRefusedBeforeTheSignatureIsVerified()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        X509Certificate costlyLeaf = CostlyTwin(leaf, ca.Private);

        int before = PublicKeyCost.Refusals;
        var result = CmsSignatureVerifier.VerifyDetachedSignature(Cms(leaf.Private, costlyLeaf), Data);

        Assert.False(result.IsCryptographicallyValid);
        Assert.Equal(before + 1, PublicKeyCost.Refusals);
        Assert.Contains("public exponent", result.Error, StringComparison.Ordinal);

        Assert.True(CmsSignatureVerifier.VerifyDetachedSignature(Cms(leaf.Private, leaf.Cert), Data).IsCryptographicallyValid);
    }

    // ------------------------------------------------------------------ CRLs

    [Fact]
    public void DssCrl_FromAnIssuerWithACostlyKey_IsRefusedBeforeItsSignatureIsVerified()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        byte[] crl = Crl(ca);

        int before = PublicKeyCost.Refusals;
        Assert.Null(DssBuilder.ValidateAndFilterCrl(crl, leaf.Cert, CostlyTwin(ca)));
        Assert.Equal(before + 1, PublicKeyCost.Refusals);
        Assert.Contains("public exponent", DssBuilder.LastCrlSignatureIssue, StringComparison.Ordinal);

        Assert.NotNull(DssBuilder.ValidateAndFilterCrl(crl, leaf.Cert, ca.Cert));
    }

    [Fact]
    public void EmbeddedCrl_FromAnIssuerWithACostlyKey_IsRefusedBeforeItsSignatureIsVerified()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        byte[] withDss = RevokedCertificateChainStatusTests.AppendDssWithCrl(SignedPdfFixtures.MinimalPdf(), Crl(ca));
        using var doc = PdfDocument.Open(new MemoryStream(withDss));

        var warnings = new List<string>();
        int before = PublicKeyCost.Refusals;
        var status = RevocationChecker.CheckChainWithOnlineFallback(doc, new List<X509Certificate> { leaf.Cert, CostlyTwin(ca) },
            warnings, new SignatureVerificationOptions(), null, null, (s, i) => null, s => null);

        Assert.NotEqual(PdfRevocationStatus.Good, status);
        Assert.Equal(before + 1, PublicKeyCost.Refusals);

        // The control: the CA's own certificate reads the same CRL as good.
        Assert.Equal(PdfRevocationStatus.Good, RevocationChecker.CheckChainWithOnlineFallback(doc,
            new List<X509Certificate> { leaf.Cert, ca.Cert }, new List<string>(),
            new SignatureVerificationOptions(), null, null, (s, i) => null, s => null));
    }

    // ------------------------------------------------------------------ OCSP

    /// <summary>
    /// The issuer's key and each carried certificate's are both asked first: an issuer with a costly
    /// key that also rides in the response is refused twice, once as the issuer, once as a candidate.
    /// </summary>
    [Fact]
    public void OcspResponder_TheIssuersCostlyKey_IsRefusedAsIssuerAndAsCarriedCandidate()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        X509Certificate costlyCa = CostlyTwin(ca);
        BasicOcspResp basic = Ocsp(ca.Private, ca.Cert, leaf.Cert.SerialNumber, costlyCa);

        int before = PublicKeyCost.Refusals;
        Assert.NotNull(OcspSingleResponse.WhyTheResponderCannotVouch(basic, costlyCa, DateTime.UtcNow));
        Assert.Equal(before + 2, PublicKeyCost.Refusals);

        Assert.Null(OcspSingleResponse.WhyTheResponderCannotVouch(basic, ca.Cert, DateTime.UtcNow));
    }

    /// <summary>
    /// A delegated responder whose own key is fine, under an issuer whose key is costly: the issuer
    /// is refused when it is asked to have signed the response, and again when it is asked to have
    /// issued the responder.
    /// </summary>
    [Fact]
    public void OcspResponder_ADelegatedResponderUnderACostlyIssuer_IsRefusedAtBothQuestions()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var responderKeys = SigningTestCrypto.Keys();
        X509Certificate responder = Cert(ca.Cert.SubjectDN, new X509Name("CN=Delegated Responder"),
            responderKeys.Public, ca.Private, ca: false, eku: KeyPurposeID.id_kp_OCSPSigning);
        BasicOcspResp basic = Ocsp(responderKeys.Private, ca.Cert, leaf.Cert.SerialNumber, responder);

        int before = PublicKeyCost.Refusals;
        Assert.NotNull(OcspSingleResponse.WhyTheResponderCannotVouch(basic, CostlyTwin(ca), DateTime.UtcNow));
        Assert.Equal(before + 2, PublicKeyCost.Refusals);

        Assert.Null(OcspSingleResponse.WhyTheResponderCannotVouch(basic, ca.Cert, DateTime.UtcNow));
    }

    // ------------------------------------------------------------------ timestamps

    [Fact]
    public void TimestampToken_FromATsaWithACostlyKey_IsRefusedBeforeItIsValidated()
    {
        var tsa = SigningTestCrypto.TsaCert();
        var costly = new SigningTestCrypto.Issued { Cert = CostlyTsa(tsa), Private = tsa.Private };
        byte[] token = SigningTestCrypto.TimestampToken(Data, costly, DateTime.UtcNow);

        int before = PublicKeyCost.Refusals;
        var result = TsaTokenVerifier.VerifyTimestampToken(token, Data);

        Assert.False(result.IsCryptographicallyValid);
        Assert.Equal(before + 1, PublicKeyCost.Refusals);
        Assert.Contains("public exponent", result.Error, StringComparison.Ordinal);

        Assert.True(TsaTokenVerifier.VerifyTimestampToken(SigningTestCrypto.TimestampToken(Data, tsa, DateTime.UtcNow), Data)
            .IsCryptographicallyValid);
    }

    [Fact]
    public void TsaClient_AReplyFromATsaWithACostlyKey_IsRefusedBeforeItIsValidated()
    {
        var tsa = SigningTestCrypto.TsaCert();
        var (request, reply) = TsaReply(tsa.Private, CostlyTsa(tsa));

        int before = PublicKeyCost.Refusals;
        var ex = Assert.Throws<PdfSigningException>(() => TsaClient.ValidateResponse(request, reply));

        Assert.Equal(before + 1, PublicKeyCost.Refusals);
        Assert.Contains("public exponent", ex.Message, StringComparison.Ordinal);

        var (goodRequest, goodReply) = TsaReply(tsa.Private, tsa.Cert);
        Assert.True(TsaTokenVerifier.VerifyTimestampToken(TsaClient.ValidateResponse(goodRequest, goodReply), Data)
            .IsCryptographicallyValid);
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>A random odd number of exactly <paramref name="bits"/> bits BouncyCastle accepts as a modulus.</summary>
    private static BigInteger Modulus(int bits)
    {
        for (int tries = 0; ; tries++)
        {
            var n = new BigInteger(bits, Random).SetBit(bits - 1).SetBit(0);
            try { _ = new RsaKeyParameters(false, n, BigInteger.ValueOf(65537)); return n; }
            catch (ArgumentException) when (tries < 5000) { }
        }
    }

    private static BigInteger Exponent(int bits)
        => bits == 17 ? BigInteger.ValueOf(65537) : new BigInteger(bits, Random).SetBit(bits - 1).SetBit(0);

    private static RsaKeyParameters Costly(AsymmetricKeyParameter key)
        => new(false, ((RsaKeyParameters)key).Modulus, Exponent(300));

    /// <summary>
    /// A DSA key whose <c>p</c> and <c>q</c> have exactly the bits asked for. BouncyCastle checks
    /// only that y^q ≡ 1 (mod p), which <c>p = 3a</c>, <c>y ≡ 1 (mod a)</c>, <c>y ≡ −1 (mod 3)</c> and
    /// an even <c>q</c> satisfy — a key no one could sign with, which an attacker's need not be.
    /// </summary>
    private static DsaPublicKeyParameters DsaKey(int pBits, int qBits)
    {
        var three = BigInteger.Three;
        BigInteger a, p;
        do
        {
            a = new BigInteger(pBits - 1, Random).SetBit(0);
            p = a.Multiply(three);
        }
        while (p.BitLength != pBits || a.Mod(three).SignValue == 0);
        BigInteger t = a.ModInverse(three);
        BigInteger y = BigInteger.One.Add(a.Multiply(t));
        BigInteger q = BigInteger.One.ShiftLeft(qBits - 1);
        return new DsaPublicKeyParameters(y, new DsaParameters(p, q, BigInteger.Two));
    }

    /// <summary>An EC key on y² = x³ + 1 over a prime field of exactly <paramref name="fieldBits"/> bits, at (2, 3).</summary>
    private static ECPublicKeyParameters EcKey(int fieldBits)
    {
        BigInteger q = BigInteger.ProbablePrime(fieldBits, Random);
        var curve = new FpCurve(q, BigInteger.Zero, BigInteger.One, q, BigInteger.One);
        ECPoint g = curve.CreatePoint(BigInteger.Two, BigInteger.Three);
        return new ECPublicKeyParameters(g, new ECDomainParameters(curve, g, q, BigInteger.One));
    }

    /// <summary>
    /// <paramref name="of"/>'s certificate again — same names, same CA standing — with its key's
    /// modulus under a 300-bit exponent, signed by <paramref name="signedBy"/> (the CA itself).
    /// </summary>
    private static X509Certificate CostlyTwin(SigningTestCrypto.Issued of, AsymmetricKeyParameter? signedBy = null)
        => Cert(of.Cert.IssuerDN, of.Cert.SubjectDN, Costly(of.Cert.GetPublicKey()), signedBy ?? of.Private,
            ca: of.Cert.GetBasicConstraints() >= 0);

    private static X509Certificate CostlyTsa(SigningTestCrypto.Issued tsa)
        => Cert(tsa.Cert.IssuerDN, tsa.Cert.SubjectDN, Costly(tsa.Cert.GetPublicKey()), tsa.Private,
            ca: false, eku: KeyPurposeID.id_kp_timeStamping);

    /// <summary>CA certificates named as <paramref name="ca"/> is, each under a key that is not its.</summary>
    private static List<X509Certificate> Decoys(SigningTestCrypto.Issued ca, int count)
    {
        var keys = SigningTestCrypto.Keys();
        return Enumerable.Range(0, count)
            .Select(_ => Cert(ca.Cert.SubjectDN, ca.Cert.SubjectDN, keys.Public, keys.Private, ca: true))
            .ToList();
    }

    private static X509Certificate Cert(X509Name issuer, X509Name subject, AsymmetricKeyParameter subjectKey,
        AsymmetricKeyParameter signingKey, bool ca, KeyPurposeID? eku = null)
    {
        var gen = new X509V3CertificateGenerator();
        gen.SetSerialNumber(BigInteger.ValueOf(System.Threading.Interlocked.Increment(ref _serial)));
        gen.SetIssuerDN(issuer);
        gen.SetSubjectDN(subject);
        gen.SetNotBefore(DateTime.UtcNow.AddYears(-1));
        gen.SetNotAfter(DateTime.UtcNow.AddYears(5));
        gen.SetPublicKey(subjectKey);
        if (ca)
        {
            gen.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(true));
            gen.AddExtension(X509Extensions.KeyUsage, true,
                new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.CrlSign | KeyUsage.DigitalSignature));
        }
        else
        {
            gen.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature));
        }
        if (eku != null)
            gen.AddExtension(X509Extensions.ExtendedKeyUsage, true, new ExtendedKeyUsage(eku));
        return gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", signingKey, Random));
    }

    private static byte[] Cms(AsymmetricKeyParameter signingKey, X509Certificate cert)
    {
        var gen = new CmsSignedDataGenerator();
        gen.AddSigner(signingKey, cert, CmsSignedGenerator.DigestSha256);
        gen.AddCertificates(CollectionUtilities.CreateStore(new List<X509Certificate> { cert }));
        return gen.Generate(new CmsProcessableByteArray(Data), false).GetEncoded();
    }

    private static byte[] Crl(SigningTestCrypto.Issued issuer)
    {
        var gen = new X509V2CrlGenerator();
        gen.SetIssuerDN(issuer.Cert.SubjectDN);
        gen.SetThisUpdate(DateTime.UtcNow.AddHours(-1));
        gen.SetNextUpdate(DateTime.UtcNow.AddDays(7));
        gen.AddExtension(X509Extensions.CrlNumber, false,
            new CrlNumber(BigInteger.ValueOf(System.Threading.Interlocked.Increment(ref _crlNumber))));
        return gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", issuer.Private, Random)).GetEncoded();
    }

    private static BasicOcspResp Ocsp(AsymmetricKeyParameter signingKey, X509Certificate issuer, BigInteger serial,
        params X509Certificate[] carried)
    {
        var gen = new BasicOcspRespGenerator(new RespID(issuer.SubjectDN));
        var certId = new CertificateID(new AlgorithmIdentifier(OiwObjectIdentifiers.IdSha1), issuer, serial);
        gen.AddResponse(certId, CertificateStatus.Good, DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddDays(1), null);
        return gen.Generate("SHA256WithRSA", signingKey, carried, DateTime.UtcNow.AddMinutes(-1));
    }

    private static (TimeStampRequest request, byte[] reply) TsaReply(AsymmetricKeyParameter signingKey, X509Certificate cert)
    {
        const string sha256 = "2.16.840.1.101.3.4.2.1";
        var reqGen = new TimeStampRequestGenerator();
        reqGen.SetCertReq(true);
        var request = reqGen.Generate(sha256, DigestUtilities.CalculateDigest("SHA-256", Data), BigInteger.ValueOf(42));

        var gen = new TimeStampTokenGenerator(signingKey, cert, sha256, "1.2.3.4.5");
        gen.SetCertificates(CollectionUtilities.CreateStore(new List<X509Certificate> { cert }));
        byte[] reply = new TimeStampResponseGenerator(gen, TspAlgorithms.Allowed)
            .Generate(request, BigInteger.ValueOf(7), DateTime.UtcNow)
            .GetEncoded();
        return (request, reply);
    }
}
