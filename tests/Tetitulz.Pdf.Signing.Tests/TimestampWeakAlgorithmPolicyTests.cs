// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.Helpers;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Tetitulz.Pdf.Signing.Timestamping;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <see cref="SignatureVerificationOptions"/>' weak-algorithm and key-size policy applies to RFC
/// 3161 timestamp tokens, not only to the document's CMS signatures.
///
/// <para><b>The defect.</b> <c>CmsSignatureVerifier</c> measured every document signature against
/// the policy — SHA-1 digest, a signature OID whose hash is MD5/MD2/SHA-1, an undersized signer key
/// — and <c>TsaTokenVerifier</c> measured nothing at all. A TimeStampToken is a CMS blob with its
/// own SignerInfo, its own message imprint and its own TSA certificate, so a SHA-1
/// <c>/DocTimeStamp</c> verified as fully valid.</para>
///
/// <para><b>Why that is worse than an unchecked signature.</b> A timestamp is not merely one more
/// thing to trust: it <i>sets the clock</i> certificate validity is judged at. So a forgeable token
/// does not just verify — it back-dates an expired signer certificate into its own validity window,
/// which is the exact failure <c>DeriveTrustedAssessmentTime</c> and
/// <c>ResolveDocumentTimestamps</c> already refuse an <i>unanchored</i> token for. The two entry
/// points had one of the two doors bolted.</para>
///
/// <para>The clock half is <see cref="OnlyAPolicyAcceptedTimestampBackDatesAnExpiredSigner"/>,
/// whose SHA-256 case is the control: without it the SHA-1 case would pass with document
/// timestamps disabled outright.</para>
/// </summary>
public sealed class TimestampWeakAlgorithmPolicyTests
{
    private static readonly byte[] Payload =
        System.Text.Encoding.ASCII.GetBytes("timestamped payload");

    private static readonly DateTime LongAgo = DateTime.UtcNow.AddYears(-6);

    // ---------------------------------------------------------------- token level

    /// <summary>
    /// Default policy (<c>RejectWeakAlgorithms == true</c>) refuses the token, and the refusal is
    /// reported as a <i>policy</i> refusal: the bytes still verified, so
    /// <c>IsCryptographicallyValid</c> stays true. Collapsing the two would make an obsolete
    /// algorithm indistinguishable from a forged token.
    /// </summary>
    [Fact]
    public void Sha1Token_IsRefusedByDefault_ButTheBytesStillVerify()
    {
        var tsa = SigningTestCrypto.TsaCert();
        byte[] token = SigningTestCrypto.TimestampToken(Payload, tsa, DateTime.UtcNow, "SHA-1");

        var result = TsaTokenVerifier.VerifyTimestampToken(token, Payload, new[] { tsa.Cert });

        Assert.False(result.IsValid, "A SHA-1 timestamp must be refused by the default policy.");
        Assert.True(result.IsCryptographicallyValid,
            "The token's own signature verified; only policy declined it.");
        Assert.True(result.WeakAlgorithm);
        Assert.Contains("SHA-1", result.WeakAlgorithmReason);
        Assert.Contains("policy", result.Error, StringComparison.OrdinalIgnoreCase);

        // A refused token must not come back looking anchored — IsTrusted / ChainStatus are
        // precisely what the callers consult before letting a token vouch for a time.
        Assert.False(result.IsTrusted);
        Assert.NotEqual(PdfSignatureChainStatus.Trusted, result.ChainStatus);
    }

    /// <summary>
    /// With rejection turned off the weakness is advisory: the token verifies, anchors, and still
    /// reports what is wrong with it. This is the half that proves the flag is read rather than the
    /// refusal being unconditional.
    /// </summary>
    [Fact]
    public void Sha1Token_IsFlaggedButAccepted_WhenRejectionIsDisabled()
    {
        var tsa = SigningTestCrypto.TsaCert();
        byte[] token = SigningTestCrypto.TimestampToken(Payload, tsa, DateTime.UtcNow, "SHA-1");

        var result = TsaTokenVerifier.VerifyTimestampToken(
            token, Payload, new[] { tsa.Cert }, null,
            new SignatureVerificationOptions { RejectWeakAlgorithms = false });

        Assert.True(result.IsValid, result.Error);
        Assert.True(result.WeakAlgorithm);
        Assert.Contains("SHA-1", result.WeakAlgorithmReason);
        Assert.True(result.IsTrusted, "An explicitly anchored TSA certificate is still trusted.");
    }

    /// <summary>
    /// A SHA-256 token under the identical call is untouched — without this the two tests above
    /// would pass with <c>VerifyTimestampToken</c> hard-wired to refuse everything.
    /// </summary>
    [Fact]
    public void Sha256Token_IsNeitherFlaggedNorRefused()
    {
        var tsa = SigningTestCrypto.TsaCert();
        byte[] token = SigningTestCrypto.TimestampToken(Payload, tsa, DateTime.UtcNow);

        var result = TsaTokenVerifier.VerifyTimestampToken(token, Payload, new[] { tsa.Cert });

        Assert.True(result.IsValid, result.Error);
        Assert.False(result.WeakAlgorithm);
        Assert.Null(result.WeakAlgorithmReason);
        Assert.True(result.IsTrusted);
    }

    /// <summary>
    /// The message imprint is checked in its own right. RFC 3161 makes it a separate field from the
    /// algorithm the TSA signs the TSTInfo with — the requester picks one, the TSA the other — and
    /// it is the ONLY thing binding the token to the bytes, so a SHA-1 imprint under a SHA-256
    /// signature is still forgeable.
    /// </summary>
    /// <remarks>
    /// This case is why the imprint test is not folded into the SignerInfo one: with a single-digest
    /// fixture the SignerInfo check catches the token first and the imprint branch is unreachable,
    /// so removing it changes nothing that any test can see.
    /// </remarks>
    [Fact]
    public void Sha1MessageImprint_IsRefused_EvenWhenTheTokenIsSha256Signed()
    {
        var tsa = SigningTestCrypto.TsaCert();
        byte[] token = SigningTestCrypto.TimestampToken(
            Payload, tsa, DateTime.UtcNow, imprintDigestName: "SHA-1", tokenDigestName: "SHA-256");

        var result = TsaTokenVerifier.VerifyTimestampToken(token, Payload, new[] { tsa.Cert });

        Assert.False(result.IsValid);
        Assert.True(result.WeakAlgorithm);
        Assert.Contains("imprint", result.WeakAlgorithmReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SHA-1", result.WeakAlgorithmReason);
        Assert.True(result.IsCryptographicallyValid);
    }

    /// <summary>
    /// The key-size floor reaches the TSA certificate too. It is a different code path from the
    /// algorithm OIDs — the size comes out of the SubjectPublicKeyInfo — so a fix that only wired
    /// up the OID switch would leave this open.
    /// </summary>
    [Fact]
    public void UndersizedTsaKey_IsRefusedAndNamedAsTheTsaCertificate()
    {
        var tsa = SigningTestCrypto.TsaCert(cn: "CN=Small Key TSA", keyBits: 1024);
        byte[] token = SigningTestCrypto.TimestampToken(Payload, tsa, DateTime.UtcNow);

        var result = TsaTokenVerifier.VerifyTimestampToken(token, Payload, new[] { tsa.Cert });

        Assert.False(result.IsValid);
        Assert.True(result.WeakAlgorithm);
        Assert.Contains("1024", result.WeakAlgorithmReason);
        // "TSA", not "signer": the same helper now reports on two different certificates and a
        // caller reading the reason has to know which one failed.
        Assert.Contains("TSA", result.WeakAlgorithmReason, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- document level

    /// <summary>
    /// The <c>/DocTimeStamp</c> field's own verification result carries the refusal, and carries it
    /// as a validation error rather than as an integrity failure.
    /// </summary>
    [Fact]
    public void Sha1DocTimeStampField_ReportsInvalidWithTheWeaknessNamed()
    {
        var root = SigningTestCrypto.Ca("CN=Weak TS Root");
        var tsa = SigningTestCrypto.TsaCert(root, "CN=Weak TSA");

        byte[] pdf = DocTimeStampAppender.AppendWithTokenDigest(
            MinimalPdfBuilder.CreateMinimalPdf("Weak timestamp fixture"),
            tsa, DateTime.UtcNow, "SHA-1", "DocTimeStamp1", root.Cert);

        var ts = VerifyField(pdf, "DocTimeStamp1", root, tsa, null);

        Assert.False(ts.IsValid);
        Assert.True(ts.WeakAlgorithm);
        Assert.Contains("SHA-1", ts.WeakAlgorithmReason);
        Assert.Contains(ts.ValidationErrors, e => e.Contains("SHA-1", StringComparison.Ordinal));

        // The refusal must not masquerade as tampering: IsIntegrityValid is what the shadow-attack
        // coverage check consults, and a policy refusal that cleared it would make every signature
        // in a file ending in a SHA-1 timestamp report unsigned trailing bytes.
        Assert.True(ts.IsIntegrityValid,
            "A policy refusal must leave the cryptographic verdict intact.");
        Assert.False(ts.HasTrustedTimestamp);
    }

    /// <summary>
    /// <b>The point of the whole row.</b> A signature made with an already-expired certificate is
    /// rescued by an archival timestamp only when that timestamp is one the policy accepts.
    /// <para>
    /// Both cases use the identical fixture, differing only in the token's digest, so the SHA-256
    /// case proves the fixture really does reach the back-dating path — the SHA-1 assertion would
    /// otherwise pass with document timestamps ignored entirely.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("SHA-256", true)]
    [InlineData("SHA-1", false)]
    public void OnlyAPolicyAcceptedTimestampBackDatesAnExpiredSigner(
        string tokenDigest, bool expectBackDated)
    {
        var root = SigningTestCrypto.Ca("CN=Backdate Root");
        var tsa = SigningTestCrypto.TsaCert(root, "CN=Backdate TSA");

        var expired = SigningTestCrypto.Leaf(
            root, "CN=Expired Signer",
            notBefore: DateTime.UtcNow.AddYears(-7),
            notAfter: DateTime.UtcNow.AddYears(-5));

        // PAdES B-LT order: sign, then archive-timestamp over the signed bytes, so the timestamp's
        // own /ByteRange genuinely covers the signature.
        byte[] signed = Sign(MinimalPdfBuilder.CreateMinimalPdf("Backdate fixture"),
                             expired, root, "Signature1");
        byte[] timestamped = DocTimeStampAppender.AppendWithTokenDigest(
            signed, tsa, LongAgo, tokenDigest, "DocTimeStamp1", root.Cert);

        var sig = VerifyField(timestamped, "Signature1", root, tsa, null);

        Assert.NotNull(sig.AssessmentTime);
        if (expectBackDated)
        {
            Assert.True(sig.HasTrustedTimestamp);
            Assert.True(Math.Abs((sig.AssessmentTime!.Value - LongAgo).TotalMinutes) < 5,
                $"Expected the assessment time to be the token's GenTime ({LongAgo:u}), got " +
                $"{sig.AssessmentTime:u}.");
            Assert.Equal(PdfSignatureChainStatus.Trusted, sig.ChainStatus);
        }
        else
        {
            Assert.False(sig.HasTrustedTimestamp,
                "A refused timestamp must not be reported as an authenticated clock.");
            Assert.True(sig.AssessmentTime!.Value > DateTime.UtcNow.AddHours(-1),
                $"Assessment time was pulled back to {sig.AssessmentTime:u} by a timestamp the " +
                "policy refuses; the expired certificate has been back-dated into validity.");
            Assert.Equal(PdfSignatureChainStatus.Invalid, sig.ChainStatus);
            Assert.False(sig.IsFullyTrusted);
        }
    }

    /// <summary>
    /// Turning the policy off restores the back-dating, on the same bytes the case above refuses.
    /// That is what shows the refusal is the policy's decision and not a second, unrelated reason
    /// the SHA-1 fixture failed to back-date.
    /// </summary>
    [Fact]
    public void Sha1DocTimeStamp_BackDatesAgain_WhenRejectionIsDisabled()
    {
        var root = SigningTestCrypto.Ca("CN=Backdate Root");
        var tsa = SigningTestCrypto.TsaCert(root, "CN=Backdate TSA");

        var expired = SigningTestCrypto.Leaf(
            root, "CN=Expired Signer",
            notBefore: DateTime.UtcNow.AddYears(-7),
            notAfter: DateTime.UtcNow.AddYears(-5));

        byte[] signed = Sign(MinimalPdfBuilder.CreateMinimalPdf("Backdate fixture"),
                             expired, root, "Signature1");
        byte[] timestamped = DocTimeStampAppender.AppendWithTokenDigest(
            signed, tsa, LongAgo, "SHA-1", "DocTimeStamp1", root.Cert);

        var sig = VerifyField(timestamped, "Signature1", root, tsa, false);

        Assert.True(sig.HasTrustedTimestamp);
        Assert.NotNull(sig.AssessmentTime);
        Assert.True(Math.Abs((sig.AssessmentTime!.Value - LongAgo).TotalMinutes) < 5,
            $"Expected the assessment time to be the token's GenTime ({LongAgo:u}), got " +
            $"{sig.AssessmentTime:u}.");
        Assert.Equal(PdfSignatureChainStatus.Trusted, sig.ChainStatus);
    }

    /// <summary>
    /// A SHA-1 <c>/DocTimeStamp</c> the caller chose to accept is valid, and still says why it is
    /// weak: a warning, so "accepted" never reads as "nothing to see". The SHA-256 token is the
    /// control — valid, and no warning.
    /// </summary>
    [Theory]
    [InlineData("SHA-1", true)]
    [InlineData("SHA-256", false)]
    public void AcceptedDocTimeStamp_WarnsOfAWeakAlgorithmOnlyWhenItHasOne(string digest, bool expectWarning)
    {
        var root = SigningTestCrypto.Ca("CN=Accepted Weak TS Root");
        var tsa = SigningTestCrypto.TsaCert(root, "CN=Accepted Weak TSA");

        byte[] pdf = DocTimeStampAppender.AppendWithTokenDigest(
            MinimalPdfBuilder.CreateMinimalPdf("Accepted weak timestamp"),
            tsa, DateTime.UtcNow, digest, "DocTimeStamp1", root.Cert);

        var ts = VerifyField(pdf, "DocTimeStamp1", root, tsa, rejectWeak: false);

        Assert.True(ts.IsValid, "Errors: " + string.Join(" | ", ts.ValidationErrors));
        var weak = ts.Warnings.Where(w => w.StartsWith("Weak algorithm", StringComparison.Ordinal)).ToList();
        if (expectWarning)
            Assert.Contains(weak, w => w.Contains("SHA-1", StringComparison.Ordinal));
        else
            Assert.Empty(weak);
    }

    // ---------------------------------------------------------------- helpers

    private static byte[] Sign(
        byte[] pdf, SigningTestCrypto.Issued leaf, SigningTestCrypto.Issued root, string fieldName)
    {
        var (pfx, password) = SigningTestCrypto.Pfx(leaf, root.Cert);
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        using var cert = new MemoryStream(pfx);
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = cert,
            CertificatePassword = password,
            SignatureFieldName = fieldName,
        });
        return output.ToArray();
    }

    /// <param name="rejectWeak">
    /// <c>null</c> leaves <see cref="SignatureVerificationOptions.RejectWeakAlgorithms"/> at its
    /// default (true) rather than omitting the options object, because the anchors have to be
    /// supplied on the same instance.
    /// </param>
    private static SignatureVerificationResult VerifyField(
        byte[] pdf, string fieldName,
        SigningTestCrypto.Issued root, SigningTestCrypto.Issued tsa, bool? rejectWeak)
    {
        var options = new SignatureVerificationOptions
        {
            TrustAnchors = new[] { root.Cert },
            TimestampTrustAnchors = new[] { root.Cert, tsa.Cert },
        };
        if (rejectWeak.HasValue) options.RejectWeakAlgorithms = rejectWeak.Value;

        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        IReadOnlyList<SignatureVerificationResult> results =
            new PdfSignatureVerifier().VerifyAll(doc, options);

        var match = results.SingleOrDefault(r => r.FieldName == fieldName);
        Assert.True(match != null,
            $"No result for '{fieldName}'; the fixture never reached the code under test. " +
            $"Fields seen: {string.Join(", ", results.Select(r => r.FieldName ?? "<null>"))}");
        return match!;
    }
}
