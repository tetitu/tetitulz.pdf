// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.Helpers;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A signature whose <c>/SubFilter</c> is an <b>indirect reference</b> must be verified on its
/// merits, not rejected for having an unrecognised sub-filter.
/// </summary>
/// <remarks>
/// <para><b>In a verifier the null direction is the whole question, and this one fell the wrong
/// way.</b> <c>PdfDictionary.GetName</c> does not follow a reference and §7.3.10 permits one here,
/// so <c>/SubFilter 9 0 R</c> read as absent, the ladder matched neither the CMS nor the RFC 3161
/// branch, and a <b>cryptographically valid signature was reported invalid</b> — quoting an empty
/// sub-filter the file does not contain.</para>
///
/// <para><b>The second-order damage is worse than the verdict.</b> That failure returns before
/// <c>/ByteRange</c> is read, so <c>ByteRangeCoverageEnd</c> stays <c>0</c>. A zero there disables
/// the shadow/append check for that signature and stops the DocMDP block matching it by coverage —
/// the hazard that block's own comment already names. A rejected signature is visible; a silently
/// skipped integrity check is not.</para>
///
/// <para>The same read in <c>ResolveDocumentTimestamps</c> made the field never become a
/// <c>DocumentTimestampCoverage</c>, so it could supply no assessment time. That only changes an
/// outcome when there is no verifying anchored CMS signature-timestamp — a B-B signature archived
/// later under a document timestamp is exactly that case, and it is the case tested here.</para>
///
/// <para><b>Both spellings produce a genuinely valid timestamp.</b> The appender mints its token
/// over the final bytes either way, so the fixtures differ only in how the name is written — not in
/// whether the cryptography holds. That is what makes the comparison meaningful.</para>
/// </remarks>
public sealed class IndirectSubFilterTests
{
    private static readonly DateTime LongAgo = DateTime.UtcNow.AddYears(-6);

    /// <summary>
    /// A PAdES B-LT document timestamp back-dates the signature it covers whichever way its
    /// <c>/SubFilter</c> is spelled. <c>false</c> is the control: it always worked.
    /// </summary>
    /// <remarks>
    /// The certificate expired years ago, so <c>Trusted</c> is reachable <i>only</i> through the
    /// timestamp's GenTime. That is what makes the chain status evidence about the timestamp having
    /// been recognised, rather than a claim that could pass for an unrelated reason.
    /// </remarks>
    [Theory]
    [InlineData(false)]  // /SubFilter /ETSI.RFC3161
    [InlineData(true)]   // /SubFilter 9 0 R
    public void ADocumentTimestamp_BackDatesTheSignatureItCovers_WhicheverWaySubFilterIsSpelled(
        bool indirect)
    {
        var root = SigningTestCrypto.Ca("CN=SubFilter Root");
        var tsa = SigningTestCrypto.TsaCert(root, "CN=SubFilter TSA");
        var expired = SigningTestCrypto.Leaf(
            root, "CN=Expired Signer",
            notBefore: DateTime.UtcNow.AddYears(-7),
            notAfter: DateTime.UtcNow.AddYears(-5));

        byte[] signed = Sign(MinimalPdfBuilder.CreateMinimalPdf("SubFilter fixture"),
                             expired, root, "Signature1");
        byte[] timestamped = DocTimeStampAppender.Append(
            signed, tsa, LongAgo, "DocTimeStamp1", indirect, root.Cert);

        var sig = VerifyField(timestamped, "Signature1", root, tsa);

        Assert.True(sig.HasTrustedTimestamp,
            "The document timestamp was not recognised, so it supplied no assessment time.");
        Assert.NotNull(sig.AssessmentTime);
        Assert.True(Math.Abs((sig.AssessmentTime!.Value - LongAgo).TotalMinutes) < 5,
            $"Expected the timestamp's GenTime ({LongAgo:u}), got {sig.AssessmentTime:u}.");
        Assert.Equal(PdfSignatureChainStatus.Trusted, sig.ChainStatus);
    }

    /// <summary>
    /// The timestamp field's own verification result, which is where the sub-filter ladder in
    /// <c>VerifyField</c> runs. It must report the real sub-filter and a whole byte range.
    /// </summary>
    /// <remarks>
    /// <c>ByteRangeCoverageEnd</c> is asserted because it is the second-order damage: the early
    /// return leaves it <c>0</c>, and a <c>0</c> is what silently disables the shadow-append check.
    /// Asserting only <c>IsIntegrityValid</c> would miss it.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheTimestampFieldItselfVerifies_WhicheverWaySubFilterIsSpelled(bool indirect)
    {
        var root = SigningTestCrypto.Ca("CN=SubFilter Root");
        var tsa = SigningTestCrypto.TsaCert(root, "CN=SubFilter TSA");

        byte[] timestamped = DocTimeStampAppender.Append(
            MinimalPdfBuilder.CreateMinimalPdf("SubFilter fixture"),
            tsa, LongAgo, "DocTimeStamp1", indirect, root.Cert);

        var ts = VerifyField(timestamped, "DocTimeStamp1", root, tsa);

        Assert.Equal("ETSI.RFC3161", ts.SubFilter);
        Assert.True(ts.IsIntegrityValid,
            "integrity: " + string.Join("; ", ts.ValidationErrors));
        Assert.True(ts.ByteRangeCoverageEnd > 0,
            "ByteRangeCoverageEnd stayed 0, so the shadow/append check is skipped for this field.");
    }

    /// <summary>
    /// The floor. A sub-filter that really is unsupported must still be reported as such — the fix
    /// resolves the value, it does not stop checking it. Without this, the assertions above would
    /// be satisfied by deleting the ladder.
    /// </summary>
    [Fact]
    public void AnUnsupportedSubFilter_IsStillReported()
    {
        var root = SigningTestCrypto.Ca("CN=SubFilter Root");
        var tsa = SigningTestCrypto.TsaCert(root, "CN=SubFilter TSA");

        byte[] timestamped = DocTimeStampAppender.Append(
            MinimalPdfBuilder.CreateMinimalPdf("SubFilter fixture"),
            tsa, LongAgo, "DocTimeStamp1", indirectSubFilter: false, tsaChain: root.Cert);

        // Rewrite the name in place, same length, so nothing else about the file moves.
        byte[] tampered = ReplaceOnce(timestamped, "/ETSI.RFC3161", "/ETSI.RFC9999");

        var ts = VerifyField(tampered, "DocTimeStamp1", root, tsa);

        Assert.False(ts.IsIntegrityValid);
        Assert.Contains(ts.ValidationErrors,
            e => e.Contains("Unsupported signature sub-filter", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Overwrites the first occurrence of <paramref name="find"/> with <paramref name="replace"/>,
    /// which must be the same length so no offset in the file shifts.
    /// </summary>
    private static byte[] ReplaceOnce(byte[] pdf, string find, string replace)
    {
        Assert.Equal(find.Length, replace.Length);

        byte[] needle = System.Text.Encoding.ASCII.GetBytes(find);
        for (int i = 0; i + needle.Length <= pdf.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < needle.Length && hit; j++) hit = pdf[i + j] == needle[j];
            if (!hit) continue;

            byte[] copy = (byte[])pdf.Clone();
            System.Text.Encoding.ASCII.GetBytes(replace).CopyTo(copy, i);
            return copy;
        }

        Assert.Fail($"'{find}' does not appear in the fixture, so the tamper never happened.");
        return pdf;
    }

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

    private static SignatureVerificationResult VerifyField(
        byte[] pdf, string fieldName,
        SigningTestCrypto.Issued root, SigningTestCrypto.Issued tsa)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var results = new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions
        {
            TrustAnchors = new[] { root.Cert },
            TimestampTrustAnchors = new[] { root.Cert, tsa.Cert },
        });

        var match = results.SingleOrDefault(r => r.FieldName == fieldName);
        Assert.True(match != null,
            $"No result for '{fieldName}'; the fixture never reached the code under test. " +
            $"Fields seen: {string.Join(", ", results.Select(r => r.FieldName ?? "<null>"))}");
        return match!;
    }
}
