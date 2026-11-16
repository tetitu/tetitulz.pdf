// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Net;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A signature field's seed value dictionary (ISO 32000-2 Tables 237 and 238) is honoured when the
/// field is signed — a required constraint the signer can meet shapes the signature, one it cannot
/// meet refuses it, and an optional one is a recommendation.
/// </summary>
/// <remarks>
/// <para><b>Table 237:</b> "The Ff entry in this signature field seed value dictionary specifies whether
/// the other entries in the dictionary shall be honoured or whether they are merely recommendations",
/// and for each required entry the signer cannot satisfy, "signing shall not take place".
/// <c>PdfSigner</c> adopted a seeded field without reading its <c>/SV</c>.</para>
///
/// <para>Each refusal names the entry, so a caller reading the exception knows which constraint the
/// author set; each acceptance is asserted on the signature the document ends up with.</para>
/// </remarks>
public sealed class SeedValueConstraintTests
{
    private sealed record Identity(SigningTestCrypto.Issued Root, SigningTestCrypto.Issued Leaf, byte[] Pfx, string Password);

    private static readonly Lazy<Identity> Signer = new(() => SigningTestCrypto.Shared(() =>
    {
        var root = SigningTestCrypto.Ca("CN=Seed Value Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Seed Value Signer");
        var (pfx, password) = SigningTestCrypto.Pfx(leaf, root.Cert);
        return new Identity(root, leaf, pfx, password);
    }));

    private static readonly Lazy<Identity> Stranger = new(() => SigningTestCrypto.Shared(() =>
    {
        var root = SigningTestCrypto.Ca("CN=Some Other Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Some Other Signer");
        var (pfx, password) = SigningTestCrypto.Pfx(leaf, root.Cert);
        return new Identity(root, leaf, pfx, password);
    }));

    // ================================================================ digest, sub-filter, filter

    [Fact]
    public void ARequiredDigestTheCallerDidNotAskFor_IsUsed()
    {
        var result = VerifyOne(Sign("<< /DigestMethod [/SHA512] /Ff 64 >>"));

        Assert.Equal(PdfDigestOids.Sha512, result.DigestAlgorithm);
        Assert.True(result.IsIntegrityValid);
    }

    [Fact]
    public void TheCallersDigestIsKeptWhenTheSeedAcceptsIt()
    {
        var result = VerifyOne(Sign("<< /DigestMethod [/SHA512 /SHA256] /Ff 64 >>"));

        Assert.Equal(PdfDigestOids.Sha256, result.DigestAlgorithm);
    }

    [Fact]
    public void ARequiredDigestThisSignerLacks_IsRefused()
    {
        var ex = Assert.Throws<PdfSigningException>(() => Sign("<< /DigestMethod [/RIPEMD160] /Ff 64 >>"));

        Assert.Contains("/DigestMethod", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOptionalDigestThisSignerLacks_LeavesTheCallersChoice()
    {
        var result = VerifyOne(Sign("<< /DigestMethod [/RIPEMD160] >>"));

        Assert.Equal(PdfDigestOids.Sha256, result.DigestAlgorithm);
    }

    /// <summary>
    /// A weak digest the seed offers is passed over (<c>WeakAlgorithmPolicy.DetectWeakDigest</c>, the
    /// verifier's rule): the next acceptable one is chosen, a required list of SHA-1 alone is refused,
    /// and an optional one leaves the caller's choice. Followed, it would have the signer sign a prefix
    /// the document's author chose under a collision-broken hash.
    /// </summary>
    [Fact]
    public void AWeakDigestTheSeedOffers_IsPassedOver()
    {
        Assert.Equal(PdfDigestOids.Sha384, VerifyOne(Sign("<< /DigestMethod [/SHA1 /SHA384] /Ff 64 >>")).DigestAlgorithm);
        Assert.Equal(PdfDigestOids.Sha256, VerifyOne(Sign("<< /DigestMethod [/SHA1] >>")).DigestAlgorithm);

        var ex = Assert.Throws<PdfSigningException>(() => Sign("<< /DigestMethod [/SHA1] /Ff 64 >>"));
        Assert.Contains("/DigestMethod", ex.Message, StringComparison.Ordinal);
        Assert.Contains("weak", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARequiredSubFilterSelectsTheEncoding()
    {
        using var doc = Open(Sign("<< /SubFilter [/adbe.x509.rsa_sha1 /ETSI.CAdES.detached] /Ff 2 >>"));

        Assert.Equal("ETSI.CAdES.detached", SignatureField(doc).SubFilter);
    }

    [Fact]
    public void ARequiredSubFilterThisSignerLacks_IsRefused()
    {
        var ex = Assert.Throws<PdfSigningException>(() => Sign("<< /SubFilter [/adbe.x509.rsa_sha1] /Ff 2 >>"));

        Assert.Contains("/SubFilter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARequiredHandlerOtherThanThisSigners_IsRefused()
    {
        var ex = Assert.Throws<PdfSigningException>(() => Sign("<< /Filter /Other.Handler /Ff 1 >>"));

        Assert.Contains("/Filter", ex.Message, StringComparison.Ordinal);
        Assert.True(VerifyOne(Sign("<< /Filter /Adobe.PPKLite /Ff 1 >>")).IsIntegrityValid);   // the control
    }

    /// <summary>
    /// <c>/Ff</c> is a flag word: its low 32 bits, as <c>/P</c> and an annotation's <c>/F</c> are read.
    /// 2^32 has no low bit set, so nothing is required and the foreign handler is merely recommended;
    /// 2^32 + 1 has bit 1 set and refuses. A saturating read would have made both refuse (every bit
    /// set), an unchecked cast made both accept on .NET Framework (Wave 17, R6.4).
    /// </summary>
    [Fact]
    public void AFlagWordAboveThirtyTwoBits_IsReadByItsLowBits()
    {
        Assert.True(VerifyOne(Sign("<< /Filter /Other.Handler /Ff 4294967296 >>")).IsIntegrityValid);

        var ex = Assert.Throws<PdfSigningException>(() => Sign("<< /Filter /Other.Handler /Ff 4294967297 >>"));
        Assert.Contains("/Filter", ex.Message, StringComparison.Ordinal);
    }

    // ================================================================ reasons, version, the unsupported

    [Fact]
    public void RequiredReasons_TheCallersReasonMustBeOneOfThem()
    {
        const string seed = "<< /Reasons [(Approved) (Reviewed)] /Ff 8 >>";

        using var kept = Open(Sign(seed, p => p.Reason = "Reviewed"));
        Assert.Equal("Reviewed", SignatureField(kept).Reason);

        using var chosen = Open(Sign(seed));
        Assert.Equal("Approved", SignatureField(chosen).Reason);   // none given: the first

        var ex = Assert.Throws<PdfSigningException>(() => Sign(seed, p => p.Reason = "Because"));
        Assert.Contains("/Reasons", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Table 237: a required <c>/Reasons</c> of a single PERIOD means the signature carries no reason.</summary>
    [Fact]
    public void RequiredReasonsOfAPeriod_OmitTheReason()
    {
        using var doc = Open(Sign("<< /Reasons [(.)] /Ff 8 >>", p => p.Reason = "Because"));

        Assert.Null(SignatureField(doc).Reason);
    }

    [Fact]
    public void ARequiredParserVersionAboveThisSigners_IsRefused()
    {
        var ex = Assert.Throws<PdfSigningException>(() => Sign("<< /V 4 /Ff 4 >>"));

        Assert.Contains("/V", ex.Message, StringComparison.Ordinal);
        Assert.True(VerifyOne(Sign("<< /V 3 /Ff 4 >>")).IsIntegrityValid);   // the control
    }

    [Theory]
    [InlineData("<< /LegalAttestation [(I attest)] /Ff 16 >>", "/LegalAttestation")]
    [InlineData("<< /AddRevInfo true /Ff 32 >>", "/AddRevInfo")]
    [InlineData("<< /AppearanceFilter (Corporate) /Ff 256 >>", "/AppearanceFilter")]
    [InlineData("<< /TimeStamp << /Ff 1 >> >>", "/TimeStamp")]
    public void ARequiredConstraintThisSignerCannotMeet_IsRefused(string seed, string named)
    {
        var ex = Assert.Throws<PdfSigningException>(() => Sign(seed));

        Assert.Contains(named, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The same entries as recommendations are ignored, which Table 237 allows.</summary>
    [Theory]
    [InlineData("<< /LegalAttestation [(I attest)] >>")]
    [InlineData("<< /AddRevInfo true >>")]
    [InlineData("<< /AppearanceFilter (Corporate) >>")]
    public void AnOptionalConstraintThisSignerCannotMeet_IsIgnored(string seed)
    {
        Assert.True(VerifyOne(Sign(seed)).IsIntegrityValid);
    }

    // ================================================================ the level

    [Fact]
    public void LockDocumentTrue_CertifiesWithNoChangesAllowed()
    {
        var result = VerifyOne(Sign("<< /LockDocument /true /Ff 128 >>"));

        Assert.True(result.IsCertification);
        Assert.Equal(1, result.CertificationPermission);
    }

    [Fact]
    public void LockDocumentRequired_DisagreeingWithTheCaller_IsRefused()
    {
        var ex = Assert.Throws<PdfSigningException>(() =>
            Sign("<< /LockDocument /true /Ff 128 >>", p => p.DocMdpPermission = DocMdpPermission.FormsAndSignatures));
        Assert.Contains("/LockDocument", ex.Message, StringComparison.Ordinal);

        ex = Assert.Throws<PdfSigningException>(() =>
            Sign("<< /LockDocument /false /Ff 128 >>", p => p.DocMdpPermission = DocMdpPermission.NoChanges));
        Assert.Contains("/LockDocument", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Optional, the caller's level stands.</summary>
    [Fact]
    public void LockDocumentOptional_YieldsToTheCaller()
    {
        var result = VerifyOne(Sign("<< /LockDocument /true >>", p => p.DocMdpPermission = DocMdpPermission.FormsAndSignatures));

        Assert.Equal(2, result.CertificationPermission);
    }

    [Fact]
    public void AnMdpSeedMakesTheSignatureACertificationWhereTheCallerAskedForNothing()
    {
        var result = VerifyOne(Sign("<< /MDP << /P 2 >> >>"));

        Assert.True(result.IsCertification);
        Assert.Equal(2, result.CertificationPermission);
    }

    // ================================================================ the timestamp

    /// <summary>
    /// The server a seed value names is the document's choice of address, asked from the signing host:
    /// not contacted unless the caller allows it. A required timestamp with no server of the caller's
    /// is refused, naming the switch; an optional one signs without a timestamp. The control: the same
    /// server named by the caller is asked, and the seed's <c>/Ff 1</c> is met.
    /// </summary>
    [Fact]
    public void ATimestampingServerNamedByTheSeed_IsNotContactedUnlessTheCallerAllowsIt()
    {
        var root = Signer.Value.Root;
        using var tsa = new LoopbackTsa(SigningTestCrypto.TsaCert(root, "CN=Loopback TSA"), root.Cert);
        var direct = new SigningNetworkOptions { Proxy = new WebProxy() };

        var ex = Assert.Throws<PdfSigningException>(() =>
            Sign($"<< /TimeStamp << /URL ({tsa.Url}) /Ff 1 >> >>", p => p.TimestampNetwork = direct));
        Assert.Contains("/TimeStamp", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SignatureParameters.UseDocumentTimestampAuthority), ex.Message, StringComparison.Ordinal);

        var optional = VerifyOne(Sign($"<< /TimeStamp << /URL ({tsa.Url}) >> >>", p => p.TimestampNetwork = direct), root.Cert);
        Assert.True(optional.IsIntegrityValid);
        Assert.False(optional.HasTrustedTimestamp);
        Assert.Equal(0, tsa.Requests);

        byte[] signed = Sign("<< /TimeStamp << /URL (http://seed.example/tsa) /Ff 1 >> >>", p =>
        {
            p.TimestampAuthorityUrl = tsa.Url;
            p.TimestampNetwork = direct;
        });
        Assert.Equal(1, tsa.Requests);
        Assert.True(VerifyOne(signed, root.Cert).HasTrustedTimestamp);
    }

    /// <summary>
    /// Allowed, the document's server is reached through the guarded transport, where a private
    /// address — loopback here — is refused before any request; the caller's own TSA client, which
    /// trusts the caller's URL with a private host, would have asked it.
    /// </summary>
    [Fact]
    public void ATimestampingServerNamedByTheSeed_AtAPrivateAddress_IsRefusedWhenAllowed()
    {
        var root = Signer.Value.Root;
        using var tsa = new LoopbackTsa(SigningTestCrypto.TsaCert(root, "CN=Loopback TSA"), root.Cert);

        var ex = Assert.Throws<PdfSigningException>(() => Sign($"<< /TimeStamp << /URL ({tsa.Url}) /Ff 1 >> >>", p =>
        {
            p.UseDocumentTimestampAuthority = true;
            p.TimestampNetwork = new SigningNetworkOptions { Proxy = new WebProxy() };
        }));

        Assert.Contains("private or reserved", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, tsa.Requests);
    }

    /// <summary>
    /// The guarded request and the caller's request, against one loopback TSA: the caller's asks it
    /// and returns a token; the guarded one refuses the address and sends nothing.
    /// </summary>
    [Fact]
    public void TheDocumentsTimestampRequest_RefusesWhatTheCallersReaches()
    {
        var root = Signer.Value.Root;
        using var tsa = new LoopbackTsa(SigningTestCrypto.TsaCert(root, "CN=Loopback TSA"), root.Cert);
        var direct = new SigningNetworkOptions { Proxy = new WebProxy() };
        byte[] imprint = new byte[32];

        byte[] token = Timestamping.TsaClient.GetTimestampToken(imprint, PdfDigestOids.Sha256, tsa.Url, direct);
        Assert.Equal(1, tsa.Requests);
        Assert.NotNull(new Org.BouncyCastle.Tsp.TimeStampToken(new Org.BouncyCastle.Cms.CmsSignedData(token)).TimeStampInfo);

        var ex = Assert.Throws<PdfSigningException>(() =>
            Timestamping.TsaClient.GetTimestampTokenFromDocumentUrl(imprint, PdfDigestOids.Sha256, tsa.Url, direct));
        Assert.Contains("private or reserved", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, tsa.Requests);
    }

    // ================================================================ the certificate (Table 238)

    [Fact]
    public void ARequiredSubjectMustBeTheSigningCertificate()
    {
        Assert.True(VerifyOne(Sign($"<< /Cert << /Subject [<{Hex(Signer.Value.Leaf.Cert)}>] /Ff 1 >> >>")).IsIntegrityValid);

        var ex = Assert.Throws<PdfSigningException>(() => Sign($"<< /Cert << /Subject [<{Hex(Stranger.Value.Leaf.Cert)}>] /Ff 1 >> >>"));
        Assert.Contains("/Subject", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARequiredIssuerMustHaveIssuedTheSigningCertificate()
    {
        Assert.True(VerifyOne(Sign($"<< /Cert << /Issuer [<{Hex(Signer.Value.Root.Cert)}>] /Ff 2 >> >>")).IsIntegrityValid);

        var ex = Assert.Throws<PdfSigningException>(() => Sign($"<< /Cert << /Issuer [<{Hex(Stranger.Value.Root.Cert)}>] /Ff 2 >> >>"));
        Assert.Contains("/Issuer", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Table 238: <c>/OID</c> "shall only be used if the value of Issuer is not empty".</summary>
    [Fact]
    public void ARequiredPolicyTheCertificateLacks_IsRefusedOnlyBesideAnIssuer()
    {
        Assert.True(VerifyOne(Sign("<< /Cert << /OID [(1.2.3.4)] /Ff 4 >> >>")).IsIntegrityValid);

        var ex = Assert.Throws<PdfSigningException>(() =>
            Sign($"<< /Cert << /Issuer [<{Hex(Signer.Value.Root.Cert)}>] /OID [(1.2.3.4)] /Ff 6 >> >>"));
        Assert.Contains("/OID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARequiredSubjectNameMustBePresentInTheCertificate()
    {
        Assert.True(VerifyOne(Sign("<< /Cert << /SubjectDN [<< /CN (Seed Value Signer) >>] /Ff 8 >> >>")).IsIntegrityValid);

        var ex = Assert.Throws<PdfSigningException>(() => Sign("<< /Cert << /SubjectDN [<< /CN (Nobody) >>] /Ff 8 >> >>"));
        Assert.Contains("/SubjectDN", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The test leaf carries digitalSignature (position 1) and nothing else.</summary>
    [Fact]
    public void ARequiredKeyUsageIsMatchedBitByBit()
    {
        Assert.True(VerifyOne(Sign("<< /Cert << /KeyUsage [(1XXXXXXXX)] /Ff 32 >> >>")).IsIntegrityValid);
        Assert.True(VerifyOne(Sign("<< /Cert << /KeyUsage [(0) (1)] /Ff 32 >> >>")).IsIntegrityValid);   // any listed pattern
    }

    /// <summary>A '1' wants the bit set (nonRepudiation is not); a '0' wants it clear (digitalSignature is set).</summary>
    [Theory]
    [InlineData("X1XXXXXXX")]
    [InlineData("0XXXXXXXX")]
    public void ARequiredKeyUsageTheCertificateDoesNotHave_IsRefused(string pattern)
    {
        var ex = Assert.Throws<PdfSigningException>(() => Sign($"<< /Cert << /KeyUsage [({pattern})] /Ff 32 >> >>"));

        Assert.Contains("/KeyUsage", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARequiredCredentialUrl_IsRefusedUnlessItIsForABrowser()
    {
        var ex = Assert.Throws<PdfSigningException>(() =>
            Sign("<< /Cert << /URL (https://enrol.example) /URLType /Enrol /Ff 64 >> >>"));
        Assert.Contains("/URL", ex.Message, StringComparison.Ordinal);

        Assert.True(VerifyOne(Sign("<< /Cert << /URL (https://enrol.example) /Ff 64 >> >>")).IsIntegrityValid);   // Browser, the default
    }

    /// <summary>The control: a field without a seed value signs exactly as the caller asked.</summary>
    [Fact]
    public void AFieldWithoutASeedValue_SignsAsTheCallerAsked()
    {
        using var doc = Open(Sign(seed: null, p => { p.Reason = "Because"; p.UseEtsiPadesSubFilter = true; }));

        var field = SignatureField(doc);
        Assert.Equal("Because", field.Reason);
        Assert.Equal("ETSI.CAdES.detached", field.SubFilter);
    }

    // ---------------------------------------------------------------- helpers

    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    private static string Hex(X509Certificate cert) => BitConverter.ToString(cert.GetEncoded()).Replace("-", "");

    private static Core.Forms.PdfSignatureField SignatureField(PdfDocument doc)
        => doc.AcroForm.GetSignatureFields().Single(f => f.HasSignature);

    /// <summary>One empty signature field <c>Sig</c> carrying <paramref name="seed"/> as its <c>/SV</c>.</summary>
    private static byte[] FormPdf(string? seed) => SignedPdfFixtures.Assemble(
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /SigFlags 3 >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [5 0 R] >>",
        "<< /Length 0 >>\nstream\n\nendstream",
        "<< /FT /Sig /T (Sig) /Type /Annot /Subtype /Widget /Rect [50 600 250 640] /F 4 /P 3 0 R" +
            (seed == null ? "" : " /SV " + seed) + " >>");

    private static byte[] Sign(string? seed, Action<SignatureParameters>? configure = null)
    {
        var parameters = new SignatureParameters
        {
            CertificateStream = new MemoryStream(Signer.Value.Pfx),
            CertificatePassword = Signer.Value.Password,
            SignatureFieldName = "Sig",
        };
        configure?.Invoke(parameters);

        using var input = new MemoryStream(FormPdf(seed));
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, parameters);
        return output.ToArray();
    }

    private static SignatureVerificationResult VerifyOne(byte[] pdf, X509Certificate? anchor = null)
    {
        using var doc = Open(pdf);
        var options = new SignatureVerificationOptions();
        if (anchor != null) options.TrustAnchors = new[] { anchor };
        return new PdfSignatureVerifier().VerifyAll(doc, options).Single();
    }
}
