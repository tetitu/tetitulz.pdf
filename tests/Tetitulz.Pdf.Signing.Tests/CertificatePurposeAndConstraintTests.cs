// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// What <see cref="CertificateChainValidator"/> holds a chain to beyond signatures and dates: the
/// purpose a signer's certificate is issued for, the critical extensions a certificate carries, the
/// names a CA's name constraints admit — and where an issuer is looked for.
/// </summary>
/// <remarks>
/// Each refusal has its acceptance beside it built by the same fixture, so a certificate is refused
/// for the one thing that differs and not for how the fixture was made.
/// </remarks>
public sealed class CertificatePurposeAndConstraintTests
{
    private const string ServerAuth = "1.3.6.1.5.5.7.3.1";

    // ------------------------------------------------------------------ the signer's purpose

    /// <summary>
    /// A certificate with no extended key usage, or one naming a purpose a document is signed
    /// under, anchors; one issued for a web server or for timestamping alone does not, under the
    /// same root.
    /// </summary>
    [Theory]
    [InlineData(null, PdfSignatureChainStatus.Trusted)]
    [InlineData(PdfEkuOids.EmailProtection, PdfSignatureChainStatus.Trusted)]
    [InlineData(PdfEkuOids.DocumentSigning, PdfSignatureChainStatus.Trusted)]
    [InlineData(PdfEkuOids.CodeSigning, PdfSignatureChainStatus.Trusted)]
    [InlineData(PdfEkuOids.AnyExtendedKeyUsage, PdfSignatureChainStatus.Trusted)]
    [InlineData(PdfEkuOids.AdobeAuthenticDocumentsTrust, PdfSignatureChainStatus.Trusted)]
    [InlineData(PdfEkuOids.MicrosoftDocumentSigning, PdfSignatureChainStatus.Trusted)]
    [InlineData(ServerAuth, PdfSignatureChainStatus.Invalid)]
    [InlineData(PdfEkuOids.TimeStamping, PdfSignatureChainStatus.Invalid)]
    [InlineData(PdfEkuOids.OcspSigning, PdfSignatureChainStatus.Invalid)]
    public void Resolve_HoldsASignerToAPurposeADocumentIsSignedUnder(string? purpose, PdfSignatureChainStatus expected)
    {
        var root = SigningTestCrypto.Ca("CN=Purpose Root");
        X509Certificate leaf = EndEntity(root, "CN=Purpose Signer", purpose == null ? null : new[] { purpose });

        var warnings = new List<string>();
        PdfSignatureChainStatus status = CertificateChainValidator.Resolve(
            leaf, Store(leaf, root.Cert), new[] { root.Cert }, DateTime.UtcNow, warnings,
            requireDocumentSigningPurpose: true);

        Assert.Equal(expected, status);
        if (expected == PdfSignatureChainStatus.Invalid)
            Assert.Contains(warnings, w => w.Contains("extended key usage " + purpose, StringComparison.Ordinal));
    }

    /// <summary>One accepted purpose among several is enough: a certificate for a web server and for e-mail signs.</summary>
    [Fact]
    public void Resolve_AcceptsASignerWithOneDocumentPurposeAmongOthers()
    {
        var root = SigningTestCrypto.Ca("CN=Purpose Root");
        X509Certificate leaf = EndEntity(root, "CN=Two Purposes", new[] { ServerAuth, PdfEkuOids.EmailProtection });

        Assert.Equal(PdfSignatureChainStatus.Trusted, CertificateChainValidator.Resolve(
            leaf, Store(leaf, root.Cert), new[] { root.Cert }, DateTime.UtcNow, new List<string>(),
            requireDocumentSigningPurpose: true));
    }

    /// <summary>
    /// The purpose is asked only where the caller asks: a timestamp authority's leaf goes through
    /// the same walk with the question off, and its timestamping-only certificate anchors.
    /// </summary>
    [Fact]
    public void Resolve_DoesNotAskThePurposeUnlessTold()
    {
        var root = SigningTestCrypto.Ca("CN=Purpose Root");
        X509Certificate leaf = EndEntity(root, "CN=Server Only", new[] { ServerAuth });

        Assert.Equal(PdfSignatureChainStatus.Trusted, CertificateChainValidator.Resolve(
            leaf, Store(leaf, root.Cert), new[] { root.Cert }, DateTime.UtcNow, new List<string>()));
    }

    /// <summary>
    /// Through the verifier: a document signed with a web-server certificate is not trusted under
    /// the default policy and says why; with <c>RequireSignerKeyPurpose</c> off it is, so the
    /// refusal is the purpose's and the option reaches the walk.
    /// </summary>
    [Fact]
    public void Verify_RefusesAServerCertificateAsASigner_UnlessThePolicyIsOff()
    {
        var root = SigningTestCrypto.Ca("CN=Purpose Root");
        var keys = SigningTestCrypto.Keys();
        X509Certificate cert = EndEntity(root, "CN=www.example.test", new[] { ServerAuth }, keys.Public);
        var leaf = new SigningTestCrypto.Issued { Cert = cert, Private = keys.Private };
        byte[] signed = Sign(leaf, root.Cert);

        SignatureVerificationResult strict = Verify(signed, new SignatureVerificationOptions
        {
            TrustAnchors = new[] { root.Cert },
        });
        Assert.True(strict.IsIntegrityValid, "The signature itself verifies; the refusal is the certificate's.");
        Assert.Equal(PdfSignatureChainStatus.Invalid, strict.ChainStatus);
        Assert.Contains(strict.Warnings, w => w.Contains("extended key usage " + ServerAuth, StringComparison.Ordinal));

        SignatureVerificationResult relaxed = Verify(signed, new SignatureVerificationOptions
        {
            TrustAnchors = new[] { root.Cert },
            RequireSignerKeyPurpose = false,
        });
        Assert.Equal(PdfSignatureChainStatus.Trusted, relaxed.ChainStatus);
    }

    // ------------------------------------------------------------------ the signer is told

    /// <summary>
    /// Signing with a certificate issued for another purpose still signs, and says at signing
    /// what default verification will refuse — in the verifier's own words, since both ask the
    /// same function.
    /// </summary>
    [Fact]
    public void Signing_WithACertificateIssuedForAnotherPurpose_ReportsWhatVerificationRefuses()
    {
        var root = SigningTestCrypto.Ca("CN=Purpose Root");
        var keys = SigningTestCrypto.Keys();
        var leaf = new SigningTestCrypto.Issued
        {
            Cert = EndEntity(root, "CN=www.example.test", new[] { ServerAuth }, keys.Public),
            Private = keys.Private,
        };

        byte[] signed = Sign(leaf, root.Cert);
        string? said = PdfSigner.LastSignerWarning;

        SignatureVerificationResult verified = Verify(signed, new SignatureVerificationOptions { TrustAnchors = new[] { root.Cert } });
        Assert.Equal(PdfSignatureChainStatus.Invalid, verified.ChainStatus);
        string refusal = Assert.Single(verified.Warnings, w => w.Contains("extended key usage", StringComparison.Ordinal));
        Assert.Equal(refusal, said);
    }

    /// <summary>The control: a certificate default verification accepts signs with nothing said, and verifies trusted.</summary>
    [Fact]
    public void Signing_WithACertificateVerificationAccepts_ReportsNothing()
    {
        var root = SigningTestCrypto.Ca("CN=Purpose Root");
        var keys = SigningTestCrypto.Keys();
        var leaf = new SigningTestCrypto.Issued
        {
            Cert = EndEntity(root, "CN=Document Signer", new[] { PdfEkuOids.DocumentSigning }, keys.Public),
            Private = keys.Private,
        };

        byte[] signed = Sign(leaf, root.Cert);

        Assert.Null(PdfSigner.LastSignerWarning);
        SignatureVerificationResult verified = Verify(signed, new SignatureVerificationOptions { TrustAnchors = new[] { root.Cert } });
        Assert.Equal(PdfSignatureChainStatus.Trusted, verified.ChainStatus);
        Assert.True(verified.IsValid, string.Join("; ", verified.ValidationErrors));
    }

    /// <summary>A SHA-1 digest is reported with the reason the verifier gives for refusing it.</summary>
    [Fact]
    public void Signing_WithSha1_ReportsTheWeakDigestTheVerifierNames()
    {
        var root = SigningTestCrypto.Ca("CN=Purpose Root");
        byte[] signed = Sign(SigningTestCrypto.Leaf(root), "SHA-1", root.Cert);
        string? said = PdfSigner.LastSignerWarning;

        SignatureVerificationResult verified = Verify(signed, new SignatureVerificationOptions { TrustAnchors = new[] { root.Cert } });
        Assert.True(verified.WeakAlgorithm);
        Assert.False(verified.IsValid);
        Assert.Equal("Weak signature algorithm: " + verified.WeakAlgorithmReason, said);
    }

    /// <summary>A certificate past its validity period at the signing time is reported.</summary>
    [Fact]
    public void Signing_WithAnExpiredCertificate_ReportsIt()
    {
        var root = SigningTestCrypto.Ca("CN=Purpose Root");
        var expired = SigningTestCrypto.Leaf(root, "CN=Expired Signer",
            notBefore: DateTime.UtcNow.AddYears(-2), notAfter: DateTime.UtcNow.AddDays(-1));

        Sign(expired, root.Cert);

        Assert.Contains("outside its validity period", PdfSigner.LastSignerWarning, StringComparison.Ordinal);
    }

    /// <summary>A signing key below the verifier's default floor is reported.</summary>
    [Fact]
    public void Signing_WithAKeyBelowTheDefaultFloor_ReportsIt()
    {
        var root = SigningTestCrypto.Ca("CN=Purpose Root");
        var keys = SigningTestCrypto.Keys(1024);
        var leaf = new SigningTestCrypto.Issued
        {
            Cert = EndEntity(root, "CN=Small Key", null, keys.Public),
            Private = keys.Private,
        };

        Sign(leaf, root.Cert);

        Assert.Contains("1024 bits", PdfSigner.LastSignerWarning, StringComparison.Ordinal);
    }

    /// <summary>
    /// The warning is one signing's: a clean signing after a warned one reports nothing, and so
    /// does a signing that fails before it has a certificate to judge.
    /// </summary>
    [Fact]
    public void TheWarning_DoesNotSurviveIntoTheNextSigning()
    {
        var root = SigningTestCrypto.Ca("CN=Purpose Root");

        Sign(SigningTestCrypto.Leaf(root), "SHA-1", root.Cert);
        Assert.NotNull(PdfSigner.LastSignerWarning);
        Sign(SigningTestCrypto.Leaf(root), root.Cert);
        Assert.Null(PdfSigner.LastSignerWarning);

        Sign(SigningTestCrypto.Leaf(root), "SHA-1", root.Cert);
        Assert.NotNull(PdfSigner.LastSignerWarning);
        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        Assert.Throws<Tetitulz.Pdf.Core.PdfSigningException>(() => new PdfSigner().Sign(input, new MemoryStream(), new SignatureParameters()));
        Assert.Null(PdfSigner.LastSignerWarning);
    }

    /// <summary>
    /// The planned form — the one an encrypted document is signed through — reports as the
    /// one-call form does: the warning is made where the CMS is, which both reach.
    /// </summary>
    [Fact]
    public void PlanAndComplete_ReportTheSameWarning()
    {
        var root = SigningTestCrypto.Ca("CN=Purpose Root");
        var (pfx, password) = SigningTestCrypto.Pfx(SigningTestCrypto.Leaf(root), root.Cert);
        byte[] pdf = SignedPdfFixtures.MinimalPdf();
        var parameters = new SignatureParameters
        {
            CertificateStream = new MemoryStream(pfx),
            CertificatePassword = password,
            DigestAlgorithm = "SHA-1",
        };

        // A warned signing first, so the plan has an earlier warning to clear: planning signs
        // nothing yet and reports nothing.
        Sign(SigningTestCrypto.Leaf(root), "SHA-1", root.Cert);
        Assert.NotNull(PdfSigner.LastSignerWarning);

        var signer = new PdfSigner();
        var appended = new MemoryStream();
        using (var doc = PdfDocument.Open(new MemoryStream(pdf)))
        {
            SignaturePlan plan = signer.Plan(doc, 0, parameters);
            Assert.Null(PdfSigner.LastSignerWarning);
            Tetitulz.Pdf.Core.Writing.IncrementalUpdateWriter.WriteUpdate(pdf, appended, plan.Objects,
                new Tetitulz.Pdf.Core.Writing.IncrementalUpdateOptions { Document = doc, Size = plan.NextObjectNumber });
            signer.Complete(plan, appended);
        }

        Assert.Contains("SHA-1", PdfSigner.LastSignerWarning, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ critical extensions

    private static readonly DerObjectIdentifier UnknownExtension = new("1.3.6.1.4.1.55555.1.1");

    /// <summary>
    /// A leaf marking critical an extension the walk does not process is refused (RFC 5280 §4.2);
    /// the same extension not marked critical changes nothing.
    /// </summary>
    [Theory]
    [InlineData(true, PdfSignatureChainStatus.Invalid)]
    [InlineData(false, PdfSignatureChainStatus.Trusted)]
    public void Resolve_RefusesALeafWithACriticalExtensionItDoesNotProcess(bool critical, PdfSignatureChainStatus expected)
    {
        var root = SigningTestCrypto.Ca("CN=Extension Root");
        X509Certificate leaf = EndEntity(root, "CN=Extension Signer", null, null,
            gen => gen.AddExtension(UnknownExtension, critical, new DerUtf8String("restriction")));

        var warnings = new List<string>();
        Assert.Equal(expected, CertificateChainValidator.Resolve(
            leaf, Store(leaf, root.Cert), new[] { root.Cert }, DateTime.UtcNow, warnings));
        if (critical)
            Assert.Contains(warnings, w => w.Contains("critical extension " + UnknownExtension.Id, StringComparison.Ordinal));
    }

    /// <summary>
    /// The same rule for an issuer on the path — and not for the trust anchor, which is the relying
    /// party's own statement: the intermediate carrying the extension refuses the chain under the
    /// root, and anchors it when it is itself the anchor.
    /// </summary>
    [Fact]
    public void Resolve_RefusesAnIssuerWithACriticalExtension_ButNotAnAnchor()
    {
        var root = SigningTestCrypto.Ca("CN=Extension Root");
        var midKeys = SigningTestCrypto.Keys();
        X509Certificate mid = Authority(root.Cert.SubjectDN, root.Private, new X509Name("CN=Marked Intermediate"), midKeys.Public,
            gen => gen.AddExtension(UnknownExtension, true, new DerUtf8String("restriction")));
        var midIssued = new SigningTestCrypto.Issued { Cert = mid, Private = midKeys.Private };
        X509Certificate leaf = EndEntity(midIssued, "CN=Below The Mark", null);
        var store = Store(leaf, mid, root.Cert);

        var warnings = new List<string>();
        Assert.Equal(PdfSignatureChainStatus.Invalid, CertificateChainValidator.Resolve(
            leaf, store, new[] { root.Cert }, DateTime.UtcNow, warnings));
        Assert.Contains(warnings, w => w.StartsWith("Issuer certificate carries the critical extension", StringComparison.Ordinal));

        Assert.Equal(PdfSignatureChainStatus.Trusted, CertificateChainValidator.Resolve(
            leaf, store, new[] { mid }, DateTime.UtcNow, new List<string>()));
    }

    // ------------------------------------------------------------------ name constraints

    /// <summary>
    /// A CA permitted to issue under one organisation's names alone: a signer named inside the
    /// subtree anchors, one named outside it does not.
    /// </summary>
    [Theory]
    [InlineData("O=Good Org,CN=Signer", PdfSignatureChainStatus.Trusted)]
    [InlineData("O=Other Org,CN=Signer", PdfSignatureChainStatus.Invalid)]
    public void Resolve_HoldsASignersNameToThePermittedSubtree(string subject, PdfSignatureChainStatus expected)
    {
        var rootKeys = SigningTestCrypto.Keys();
        var rootName = new X509Name("CN=Constrained Root");
        X509Certificate root = Authority(rootName, rootKeys.Private, rootName, rootKeys.Public, gen =>
            gen.AddExtension(X509Extensions.NameConstraints, true, new NameConstraints(
                new[] { new GeneralSubtree(new GeneralName(new X509Name("O=Good Org"))) }, null)));
        var rootIssued = new SigningTestCrypto.Issued { Cert = root, Private = rootKeys.Private };
        X509Certificate leaf = EndEntity(rootIssued, subject, null);

        var warnings = new List<string>();
        Assert.Equal(expected, CertificateChainValidator.Resolve(
            leaf, Store(leaf, root), new[] { root }, DateTime.UtcNow, warnings));
        if (expected == PdfSignatureChainStatus.Invalid)
            Assert.Contains(warnings, w => w.Contains("name constraints of", StringComparison.Ordinal));
    }

    /// <summary>
    /// A constraint reaches every certificate below the CA that states it, not only the one it
    /// issued: the root excludes a mail domain, an unconstrained intermediate issues a signer in it.
    /// </summary>
    [Theory]
    [InlineData("signer@good.example", PdfSignatureChainStatus.Trusted)]
    [InlineData("signer@evil.example", PdfSignatureChainStatus.Invalid)]
    public void Resolve_HoldsAnAlternativeNameToAnExcludedSubtreeTwoLevelsUp(string email, PdfSignatureChainStatus expected)
    {
        var rootKeys = SigningTestCrypto.Keys();
        var rootName = new X509Name("CN=Excluding Root");
        X509Certificate root = Authority(rootName, rootKeys.Private, rootName, rootKeys.Public, gen =>
            gen.AddExtension(X509Extensions.NameConstraints, true, new NameConstraints(
                null, new[] { new GeneralSubtree(new GeneralName(GeneralName.Rfc822Name, "evil.example")) })));
        var midKeys = SigningTestCrypto.Keys();
        X509Certificate mid = Authority(rootName, rootKeys.Private, new X509Name("CN=Plain Intermediate"), midKeys.Public, null);
        var midIssued = new SigningTestCrypto.Issued { Cert = mid, Private = midKeys.Private };
        X509Certificate leaf = EndEntity(midIssued, "CN=Mail Signer", null, null, gen =>
            gen.AddExtension(X509Extensions.SubjectAlternativeName, false,
                new GeneralNames(new GeneralName(GeneralName.Rfc822Name, email))));

        Assert.Equal(expected, CertificateChainValidator.Resolve(
            leaf, Store(leaf, mid, root), new[] { root }, DateTime.UtcNow, new List<string>()));
    }

    /// <summary>
    /// With no alternative name, a mail constraint applies to the address in the subject itself
    /// (RFC 5280 §4.2.1.10).
    /// </summary>
    [Theory]
    [InlineData("E=signer@good.example,CN=Signer", PdfSignatureChainStatus.Trusted)]
    [InlineData("E=signer@evil.example,CN=Signer", PdfSignatureChainStatus.Invalid)]
    public void Resolve_HoldsTheSubjectsOwnMailAddressToAMailConstraint(string subject, PdfSignatureChainStatus expected)
    {
        var rootKeys = SigningTestCrypto.Keys();
        var rootName = new X509Name("CN=Excluding Root");
        X509Certificate root = Authority(rootName, rootKeys.Private, rootName, rootKeys.Public, gen =>
            gen.AddExtension(X509Extensions.NameConstraints, true, new NameConstraints(
                null, new[] { new GeneralSubtree(new GeneralName(GeneralName.Rfc822Name, "evil.example")) })));
        var rootIssued = new SigningTestCrypto.Issued { Cert = root, Private = rootKeys.Private };
        X509Certificate leaf = EndEntity(rootIssued, subject, null);

        Assert.Equal(expected, CertificateChainValidator.Resolve(
            leaf, Store(leaf, root), new[] { root }, DateTime.UtcNow, new List<string>()));
    }

    // ------------------------------------------------------------------ issuers among the anchors

    /// <summary>
    /// A bag that leaves the root out — leaf and intermediate alone — anchors when the relying
    /// party holds that root, and not when it holds another.
    /// </summary>
    [Fact]
    public void Resolve_FindsTheRootAmongTheAnchors_WhenTheBagLeavesItOut()
    {
        var root = SigningTestCrypto.Ca("CN=Left Out Root");
        var mid = SigningTestCrypto.IssuedIntermediate(root, SigningTestCrypto.Keys(), "CN=Carried Intermediate");
        var leaf = SigningTestCrypto.Leaf(mid, "CN=Rootless Bag Signer");
        var bag = Store(leaf.Cert, mid.Cert);

        Assert.Equal(PdfSignatureChainStatus.Trusted, CertificateChainValidator.Resolve(
            leaf.Cert, bag, new[] { root.Cert }, DateTime.UtcNow, new List<string>()));

        var warnings = new List<string>();
        Assert.Equal(PdfSignatureChainStatus.UntrustedRoot, CertificateChainValidator.Resolve(
            leaf.Cert, bag, new[] { SigningTestCrypto.Ca("CN=Another Root").Cert }, DateTime.UtcNow, warnings));
        Assert.Contains(warnings, w => w.Contains("not found in the accompanying certificates or the trust anchors", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>BuildChain</c> reaches the same root, so the intermediate has an issuer to check its
    /// revocation against; with no anchors it stops at the intermediate.
    /// </summary>
    [Fact]
    public void BuildChain_AddsAnIssuerFoundAmongTheAnchors()
    {
        var root = SigningTestCrypto.Ca("CN=Left Out Root");
        var mid = SigningTestCrypto.IssuedIntermediate(root, SigningTestCrypto.Keys(), "CN=Carried Intermediate");
        var leaf = SigningTestCrypto.Leaf(mid, "CN=Rootless Bag Signer");
        var bag = Store(leaf.Cert, mid.Cert);

        Assert.Equal(new[] { leaf.Cert, mid.Cert, root.Cert },
            CertificateChainValidator.BuildChain(leaf.Cert, bag, new[] { root.Cert }));
        Assert.Equal(new[] { leaf.Cert, mid.Cert }, CertificateChainValidator.BuildChain(leaf.Cert, bag));
    }

    /// <summary>Through the verifier: a signature whose CMS carries the signer alone is trusted under the anchored root.</summary>
    [Fact]
    public void Verify_TrustsASignatureWhoseCmsLeavesItsRootOut()
    {
        var root = SigningTestCrypto.Ca("CN=Left Out Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Lone Signer");
        byte[] signed = Sign(leaf);   // no chain in the PKCS#12, so none in the CMS

        SignatureVerificationResult result = Verify(signed, new SignatureVerificationOptions
        {
            TrustAnchors = new[] { root.Cert },
        });

        Assert.Equal(PdfSignatureChainStatus.Trusted, result.ChainStatus);
    }

    // ------------------------------------------------------------------ fixtures

    private static readonly SecureRandom Random = new();
    private static long _serial = 880000;

    private static IStore<X509Certificate> Store(params X509Certificate[] certificates)
        => CollectionUtilities.CreateStore(new List<X509Certificate>(certificates));

    /// <summary>An end-entity certificate under <paramref name="issuer"/>, with the purposes and extensions asked for.</summary>
    private static X509Certificate EndEntity(
        SigningTestCrypto.Issued issuer, string subject, string[]? purposes,
        AsymmetricKeyParameter? publicKey = null, Action<X509V3CertificateGenerator>? more = null)
    {
        X509V3CertificateGenerator gen = Generator(issuer.Cert.SubjectDN, new X509Name(subject),
            publicKey ?? SigningTestCrypto.Keys().Public);
        gen.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature));
        if (purposes != null)
            gen.AddExtension(X509Extensions.ExtendedKeyUsage, false,
                new ExtendedKeyUsage(purposes.Select(p => KeyPurposeID.GetInstance(new DerObjectIdentifier(p))).ToArray()));
        more?.Invoke(gen);
        return gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", issuer.Private, Random));
    }

    /// <summary>A CA certificate, with whatever <paramref name="more"/> adds.</summary>
    private static X509Certificate Authority(
        X509Name issuer, AsymmetricKeyParameter signingKey, X509Name subject, AsymmetricKeyParameter subjectKey,
        Action<X509V3CertificateGenerator>? more)
    {
        X509V3CertificateGenerator gen = Generator(issuer, subject, subjectKey);
        gen.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(true));
        gen.AddExtension(X509Extensions.KeyUsage, true,
            new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.CrlSign | KeyUsage.DigitalSignature));
        more?.Invoke(gen);
        return gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", signingKey, Random));
    }

    private static X509V3CertificateGenerator Generator(X509Name issuer, X509Name subject, AsymmetricKeyParameter key)
    {
        var gen = new X509V3CertificateGenerator();
        gen.SetSerialNumber(BigInteger.ValueOf(System.Threading.Interlocked.Increment(ref _serial)));
        gen.SetIssuerDN(issuer);
        gen.SetSubjectDN(subject);
        gen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        gen.SetNotAfter(DateTime.UtcNow.AddYears(5));
        gen.SetPublicKey(key);
        return gen;
    }

    private static byte[] Sign(SigningTestCrypto.Issued leaf, params X509Certificate[] chain)
        => Sign(leaf, null, chain);

    private static byte[] Sign(SigningTestCrypto.Issued leaf, string? digest, params X509Certificate[] chain)
    {
        var (pfx, password) = SigningTestCrypto.Pfx(leaf, chain);
        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();
        using var certificate = new MemoryStream(pfx);
        var parameters = new SignatureParameters
        {
            CertificateStream = certificate,
            CertificatePassword = password,
        };
        if (digest != null) parameters.DigestAlgorithm = digest;
        new PdfSigner().Sign(input, output, parameters);
        return output.ToArray();
    }

    private static SignatureVerificationResult Verify(byte[] pdf, SignatureVerificationOptions options)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        return new PdfSignatureVerifier().VerifyAll(doc, options).Single();
    }
}
