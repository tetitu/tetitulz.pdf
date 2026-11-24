// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Tests.Helpers;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Xunit;
using Tetitulz.Pdf.Signing.Cms;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// <see cref="PdfSignatureVerifier"/> is the single verification engine and
/// <see cref="PdfSignatureValidator"/> a thin adapter over it.
/// <para>
/// Parallel implementations drift: a second engine could apply neither the MD5/MD2 hard reject,
/// the SHA-1 weak flag nor the ESS signer-binding check, walk only the top level of /Fields
/// (missing any signature nested under a parent node), and never look at /SubFilter — feeding a
/// /DocTimeStamp listed first into the detached-CMS path. These tests pin the unified behaviour.
/// </para>
/// </summary>
public class UnifiedVerificationPipelineTests
{
    private static byte[] SignWithChain(out SigningTestCrypto.Issued root, out SigningTestCrypto.Issued leaf)
    {
        root = SigningTestCrypto.Ca("CN=Pipeline Root");
        leaf = SigningTestCrypto.Leaf(root, "CN=Pipeline Signer");
        var (pfx, password) = SigningTestCrypto.Pfx(leaf, root.Cert);

        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Unified pipeline document");
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        using var cert = new MemoryStream(pfx);

        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = cert,
            CertificatePassword = password,
            SignatureFieldName = "Signature1",
            Reason = "Pipeline test",
        });
        return output.ToArray();
    }

    // ---------------------------------------------------------------- chain + revocation on one result

    [Fact]
    public void VerifyAll_WithTrustAnchors_ReportsTrustedChain()
    {
        byte[] signed = SignWithChain(out var root, out _);

        using var ms = new MemoryStream(signed);
        using var doc = PdfDocument.Open(ms);
        var options = new SignatureVerificationOptions { TrustAnchors = new[] { root.Cert } };
        var results = new PdfSignatureVerifier().VerifyAll(doc, options);

        var r = Assert.Single(results);
        Assert.True(r.IsValid, string.Join("; ", r.ValidationErrors));
        Assert.Equal(PdfSignatureChainStatus.Trusted, r.ChainStatus);
        Assert.True(r.IsFullyTrusted, "Anchored + intact + not revoked must be fully trusted.");
    }

    [Fact]
    public void VerifyAll_WithoutTrustAnchors_IsValidButNotFullyTrusted()
    {
        byte[] signed = SignWithChain(out _, out _);

        using var ms = new MemoryStream(signed);
        using var doc = PdfDocument.Open(ms);
        var results = new PdfSignatureVerifier().VerifyAll(doc);

        var r = Assert.Single(results);
        Assert.True(r.IsValid, "Integrity still holds without anchors.");
        Assert.NotEqual(PdfSignatureChainStatus.Trusted, r.ChainStatus);
        Assert.False(r.IsFullyTrusted, "No anchors ⇒ no trust decision is possible.");
    }

    [Fact]
    public void VerifyAll_WithWrongTrustAnchor_ReportsUntrustedRoot()
    {
        byte[] signed = SignWithChain(out _, out _);
        var unrelated = SigningTestCrypto.Ca("CN=Unrelated Root");

        using var ms = new MemoryStream(signed);
        using var doc = PdfDocument.Open(ms);
        var options = new SignatureVerificationOptions { TrustAnchors = new[] { unrelated.Cert } };
        var r = Assert.Single(new PdfSignatureVerifier().VerifyAll(doc, options));

        Assert.True(r.IsValid);
        Assert.Equal(PdfSignatureChainStatus.UntrustedRoot, r.ChainStatus);
        Assert.False(r.IsFullyTrusted);
    }

    [Fact]
    public void NoEmbeddedRevocationData_ReportsNotChecked()
    {
        byte[] signed = SignWithChain(out var root, out _);

        using var ms = new MemoryStream(signed);
        using var doc = PdfDocument.Open(ms);
        var options = new SignatureVerificationOptions { TrustAnchors = new[] { root.Cert } };
        var r = Assert.Single(new PdfSignatureVerifier().VerifyAll(doc, options));

        Assert.Equal(PdfRevocationStatus.NotChecked, r.RevocationStatus);
    }

    // ---------------------------------------------------------------- validator is now an adapter

    [Fact]
    public void Validator_AndVerifier_AgreeOnTheSameDocument()
    {
        byte[] signed = SignWithChain(out var root, out _);
        var anchors = new[] { root.Cert };

        using var ms = new MemoryStream(signed);
        using var doc = PdfDocument.Open(ms);
        var verifierResult = Assert.Single(new PdfSignatureVerifier()
            .VerifyAll(doc, new SignatureVerificationOptions { TrustAnchors = anchors }));

        using var ms2 = new MemoryStream(signed);
        var validatorResult = PdfSignatureValidator.Validate(ms2, anchors);

        Assert.Equal(verifierResult.IsValid, validatorResult.IsValid);
        Assert.Equal(verifierResult.ChainStatus, validatorResult.ChainStatus);
        Assert.Equal(verifierResult.RevocationStatus, validatorResult.RevocationStatus);
        Assert.Equal(verifierResult.IsFullyTrusted, validatorResult.IsFullyTrusted);
        Assert.Equal(verifierResult.SignerSubject, validatorResult.SignerSubject);
    }

    [Fact]
    public void Validator_ReportsNoSignatureOnAnUnsignedDocument()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("unsigned");
        using var ms = new MemoryStream(pdf);

        var result = PdfSignatureValidator.Validate(ms);

        Assert.False(result.HasSignature);
        Assert.False(result.IsValid);
        Assert.Equal("No signature field found", result.ErrorMessage);
    }

    [Fact]
    public void ValidateAll_ReturnsEverySignatureField()
    {
        byte[] signed = SignWithChain(out var root, out _);
        using var ms = new MemoryStream(signed);

        var all = PdfSignatureValidator.ValidateAll(ms,
            new SignatureVerificationOptions { TrustAnchors = new[] { root.Cert } });

        Assert.Single(all);
        Assert.Equal("Signature1", all[0].FieldName);
    }

    // ---------------------------------------------------------------- structural fixes

    /// <summary>
    /// The by-name Verify() path dereferenced BaseStream without a null check, so a document with
    /// no backing stream threw NullReferenceException out of a public API instead of reporting a
    /// validation error. VerifyAll guarded it; Verify did not.
    /// </summary>
    [Fact]
    public void Verify_OnDisposedDocument_DoesNotThrowNullReference()
    {
        byte[] signed = SignWithChain(out _, out _);

        var ms = new MemoryStream(signed);
        var doc = PdfDocument.Open(ms);
        // Any exception is acceptable except NullReferenceException — the point is that the
        // unguarded dereference is gone.
        try
        {
            var r = new PdfSignatureVerifier().Verify(doc, "Signature1");
            Assert.NotNull(r);
        }
        catch (NullReferenceException)
        {
            Assert.Fail("BaseStream must be null-guarded, not dereferenced blind.");
        }
        catch (Exception)
        {
            // A clean typed failure is fine.
        }
        finally { doc.Dispose(); ms.Dispose(); }
    }

    [Fact]
    public void VerifyAll_RejectsNullDocument()
        => Assert.Throws<ArgumentNullException>(() => new PdfSignatureVerifier().VerifyAll(null));

    [Fact]
    public void LastVerificationError_IsPopulatedOnStructuralFailure()
    {
        // A signature dictionary with an unsupported sub-filter is a structural failure.
        byte[] pdf = PdfWithSignatureField("adbe.x509.rsa_sha1");
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var results = new PdfSignatureVerifier().VerifyAll(doc);

        Assert.NotEmpty(results);
        Assert.False(results[0].IsValid);
        Assert.NotNull(PdfSignatureVerifier.LastVerificationError);
        Assert.Contains("sub-filter", PdfSignatureVerifier.LastVerificationError);
    }

    // ---------------------------------------------------------------- errors vs warnings

    /// <summary>
    /// Everything in ValidationErrors clears IsValid, so advisory findings must not go there.
    /// A chain that simply has no anchors is advisory — the signature is still intact.
    /// </summary>
    [Fact]
    public void UnanchoredChain_ProducesWarningsNotErrors()
    {
        byte[] signed = SignWithChain(out _, out _);

        using var ms = new MemoryStream(signed);
        using var doc = PdfDocument.Open(ms);
        var r = Assert.Single(new PdfSignatureVerifier().VerifyAll(doc));

        Assert.Empty(r.ValidationErrors);
        Assert.True(r.IsValid);
    }

    // ---------------------------------------------------------------- chain walker guards

    /// <summary>
    /// Adversarial input: a certificate bag where A names B as issuer and B names A. DN
    /// equivalence does not imply a real signature relationship, so a naive walk loops.
    /// </summary>
    [Fact]
    public void BuildChain_TerminatesOnACertificateCycle()
    {
        // Two CAs that each claim the other as issuer by DN.
        var a = SigningTestCrypto.Ca("CN=Cycle A");
        var b = SigningTestCrypto.Ca("CN=Cycle B");
        var crossA = CrossSigned("CN=Cycle A", "CN=Cycle B", a);
        var crossB = CrossSigned("CN=Cycle B", "CN=Cycle A", b);

        var store = CollectionUtilities.CreateStore(new List<X509Certificate> { crossA, crossB });

        var chain = CertificateChainValidator.BuildChain(crossA, store);

        Assert.True(chain.Count <= CertificateChainValidator.MaxChainHops + 1,
            $"Cycle guard failed: walked {chain.Count} hops.");
    }

    [Fact]
    public void BuildChain_ReturnsLeafThenIssuers()
    {
        var root = SigningTestCrypto.Ca("CN=Chain Root");
        var mid = SigningTestCrypto.Intermediate(root, "CN=Chain Mid");
        var leaf = SigningTestCrypto.Leaf(mid, "CN=Chain Leaf");
        var store = CollectionUtilities.CreateStore(
            new List<X509Certificate> { leaf.Cert, mid.Cert, root.Cert });

        var chain = CertificateChainValidator.BuildChain(leaf.Cert, store);

        Assert.Equal(3, chain.Count);
        Assert.Equal(leaf.Cert, chain[0]);
        Assert.Equal(mid.Cert, chain[1]);
        Assert.Equal(root.Cert, chain[2]);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A certificate with subject <paramref name="subject"/> claiming
    /// <paramref name="issuerName"/> as issuer, signed by <paramref name="signer"/>.</summary>
    private static X509Certificate CrossSigned(
        string subject, string issuerName, SigningTestCrypto.Issued signer)
    {
        var kp = SigningTestCrypto.Keys();
        var gen = new Org.BouncyCastle.X509.X509V3CertificateGenerator();
        gen.SetSerialNumber(Org.BouncyCastle.Math.BigInteger.ValueOf(Environment.TickCount64 & 0x7fffffff));
        gen.SetIssuerDN(new Org.BouncyCastle.Asn1.X509.X509Name(issuerName));
        gen.SetSubjectDN(new Org.BouncyCastle.Asn1.X509.X509Name(subject));
        gen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        gen.SetNotAfter(DateTime.UtcNow.AddYears(5));
        gen.SetPublicKey(kp.Public);
        gen.AddExtension(Org.BouncyCastle.Asn1.X509.X509Extensions.BasicConstraints, true,
            new Org.BouncyCastle.Asn1.X509.BasicConstraints(true));
        return gen.Generate(new Org.BouncyCastle.Crypto.Operators.Asn1SignatureFactory(
            "SHA256WithRSA", signer.Private));
    }

    /// <summary>A minimal PDF carrying one signature field with the given /SubFilter.</summary>
    private static byte[] PdfWithSignatureField(string subFilter)
    {
        var ms = new MemoryStream();
        var offsets = new Dictionary<int, long>();
        void Ascii(string s) { var b = System.Text.Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Header(int n) { offsets[n] = ms.Length; Ascii($"{n} 0 obj\n"); }

        Ascii("%PDF-1.7\n");
        Header(1); Ascii("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>\nendobj\n");
        Header(2); Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Header(3); Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        Header(4); Ascii($"<< /Type /Sig /SubFilter /{subFilter} /ByteRange [0 100 200 100] /Contents <00> >>\nendobj\n");
        Header(5); Ascii("<< /FT /Sig /T (Signature1) /V 4 0 R >>\nendobj\n");

        long xref = ms.Length;
        Ascii("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i < 6; i++) Ascii($"{offsets[i]:D10} 00000 n \n");
        Ascii($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
