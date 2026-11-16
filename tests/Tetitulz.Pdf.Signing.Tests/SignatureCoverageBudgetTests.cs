// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The bytes a verification reads and digests for all of a document's signatures together are
/// bounded by the document's own length (<see cref="PdfLimits.MaxSignatureCoverageMultiple"/>): each
/// signature may cover the whole file, so one genuine signature copied 200 times over a 4 MB revision
/// verified 201 times in 10.8 s — signatures × bytes, the square of the file.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public sealed class SignatureCoverageBudgetTests
{
    private const int Copies = 40;

    private static readonly Lazy<(byte[] Pdf, Org.BouncyCastle.X509.X509Certificate Anchor)> Fixture =
        new(() => SigningTestCrypto.Shared(() =>
        {
            var ca = SigningTestCrypto.Ca();
            var leaf = SigningTestCrypto.Leaf(ca);
            var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);
            byte[] signed = SignedPdfFixtures.Sign(Padded(200_000), pfx, pw, "Signature1");
            return (WithCopies(signed, Copies), ca.Cert);
        }));

    /// <summary>
    /// With the budget at four times the document's length, the signatures that fit are verified
    /// and every one past it is refused for the budget, with no coverage to vouch with.
    /// </summary>
    [Fact]
    public void AdversarialCopiesOfOneSignature_AreVerifiedUpToTheCoverageBudget_AndTheRestRefused()
    {
        var (pdf, anchor) = Fixture.Value;
        long covered = SignedLength(pdf);
        int fit = (int)(4L * pdf.Length / covered);
        Assert.InRange(fit, 2, Copies - 2);

        try
        {
            PdfLimits.MaxSignatureCoverageMultiple = 4;
            var results = Verify(pdf, anchor);

            Assert.Equal(Copies + 1, results.Count);
            Assert.Equal(fit, results.Count(r => r.IsIntegrityValid));
            var refused = results.Where(r => r.ValidationErrors.Any(e => e.Contains("MaxSignatureCoverageMultiple", StringComparison.Ordinal))).ToList();
            Assert.Equal(Copies + 1 - fit, refused.Count);
            Assert.All(refused, r => Assert.False(r.IsValid));
            Assert.All(refused, r => Assert.Equal(0, r.ByteRangeCoverageEnd));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>
    /// The number of signatures read is bounded too (<see cref="PdfLimits.MaxSignaturesPerDocument"/>):
    /// each costs its CMS work however little it covers. With the cap at ten, ten are verified and
    /// every one after them is refused for the count.
    /// </summary>
    [Fact]
    public void AdversarialCopiesOfOneSignature_AreVerifiedUpToTheSignatureCount_AndTheRestRefused()
    {
        var (pdf, anchor) = Fixture.Value;

        try
        {
            PdfLimits.MaxSignaturesPerDocument = 10;
            var results = Verify(pdf, anchor);

            Assert.Equal(Copies + 1, results.Count);
            Assert.Equal(10, results.Count(r => r.IsIntegrityValid));
            var refused = results.Where(r => r.ValidationErrors.Any(e => e.Contains("MaxSignaturesPerDocument", StringComparison.Ordinal))).ToList();
            Assert.Equal(Copies + 1 - 10, refused.Count);
            Assert.All(refused, r => Assert.False(r.IsValid));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>
    /// Document timestamps draw on the same budget: with room for one signature, the approval
    /// signature takes it, the covering timestamp is not verified and says so, and the approval —
    /// whose certificate has since expired — has no trusted time. The control is the default, under
    /// which the timestamp supplies it.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(PdfLimits.DefaultMaxSignaturesPerDocument, true)]
    public void ADocumentTimestampPastTheBudget_IsNotVerified_AndSaid(int maxSignatures, bool trustedTime)
    {
        var tokenTime = new DateTime(2020, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var root = SigningTestCrypto.Ca();
        var tsa = SigningTestCrypto.TsaCert(root);
        var expired = SigningTestCrypto.Leaf(root, "CN=Expired Signer",
            notBefore: tokenTime.AddYears(-1), notAfter: tokenTime.AddMonths(6));
        var (pfx, pw) = SigningTestCrypto.Pfx(expired, root.Cert);
        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw, signingTime: tokenTime);
        byte[] timestamped = SignedPdfFixtures.AppendDocumentTimestamp(
            signed, tsa, tokenTime, new[] { root.Cert }, sigDictObjNum: 8, sigFieldObjNum: 9, existingFieldRefs: "7 0 R ");

        try
        {
            PdfLimits.MaxSignaturesPerDocument = maxSignatures;
            var approval = Verify(timestamped, root.Cert).Single(r => r.FieldName == "Signature1");

            Assert.Equal(trustedTime, approval.HasTrustedTimestamp);
            Assert.Equal(!trustedTime, approval.Warnings.Any(w => w.Contains("Document timestamps past the verifier's budget", StringComparison.Ordinal)));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>The control: under the default budget every copy is verified.</summary>
    [Fact]
    public void UnderTheDefaultBudget_EveryCopyIsVerified()
    {
        var (pdf, anchor) = Fixture.Value;

        var results = Verify(pdf, anchor);

        Assert.Equal(Copies + 1, results.Count);
        Assert.All(results, r => Assert.True(r.IsIntegrityValid));
    }

    private static IReadOnlyList<SignatureVerificationResult> Verify(byte[] pdf, Org.BouncyCastle.X509.X509Certificate anchor)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions { TrustAnchors = new[] { anchor } });
    }

    /// <summary>The length the first signature's <c>/ByteRange</c> covers.</summary>
    private static long SignedLength(byte[] pdf)
    {
        var m = Regex.Match(Encoding.Latin1.GetString(pdf), @"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]");
        return long.Parse(m.Groups[2].Value) + long.Parse(m.Groups[4].Value);
    }

    /// <summary>A one-page document whose content stream is padded to about <paramref name="padBytes"/>.</summary>
    private static byte[] Padded(int padBytes)
    {
        string content = "BT /F1 12 Tf 72 700 Td (signed content) Tj ET\n" + string.Concat(Enumerable.Repeat("q Q\n", padBytes / 4));
        return SignedPdfFixtures.Assemble(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
    }

    /// <summary>
    /// <paramref name="signed"/> with a revision appending <paramref name="copies"/> signature fields,
    /// each holding a copy of its signature dictionary — the same <c>/ByteRange</c> and the same
    /// <c>/Contents</c>, so each verifies over the signed revision as the original does.
    /// </summary>
    private static byte[] WithCopies(byte[] signed, int copies)
    {
        string text = Encoding.Latin1.GetString(signed);
        string byteRange = Regex.Match(text, @"/ByteRange\s*\[[^\]]*\]").Value;
        string contents = Regex.Match(text, @"/Contents\s*<[0-9A-Fa-f]+>").Value;
        int size;
        using (var doc = PdfDocument.Open(new MemoryStream(signed)))
            size = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;

        var sb = new StringBuilder();
        var offsets = new SortedDictionary<int, int>();
        int start = signed.Length;
        var fields = new StringBuilder(Regex.Matches(text, @"/Fields\s*\[([^\]]*)\]").Last().Groups[1].Value).Append(' ');
        for (int k = 0; k < copies; k++)
        {
            int sig = size + 2 * k, field = sig + 1;
            offsets[sig] = start + sb.Length;
            sb.Append(sig).Append(" 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached ")
              .Append(byteRange).Append(' ').Append(contents).Append(" >>\nendobj\n");
            offsets[field] = start + sb.Length;
            sb.Append(field).Append(" 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Sig /T (copy").Append(k)
              .Append(") /V ").Append(sig).Append(" 0 R /Rect [0 0 0 0] /P 3 0 R >>\nendobj\n");
            fields.Append(field).Append(" 0 R ");
        }
        int catalog = size + 2 * copies;
        offsets[catalog] = start + sb.Length;
        sb.Append(catalog).Append(" 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [").Append(fields)
          .Append("] /SigFlags 3 >> >>\nendobj\n");
        int xref = start + sb.Length;
        sb.Append("xref\n");
        foreach (var kv in offsets) sb.Append(kv.Key).Append(" 1\n").Append(kv.Value.ToString("D10")).Append(" 00000 n \n");
        string prev = Regex.Matches(text, @"startxref\s+(\d+)").Last().Groups[1].Value;
        sb.Append("trailer\n<< /Size ").Append(catalog + 1).Append(" /Root ").Append(catalog).Append(" 0 R /Prev ").Append(prev)
          .Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        var all = new byte[signed.Length + sb.Length];
        Buffer.BlockCopy(signed, 0, all, 0, signed.Length);
        Encoding.Latin1.GetBytes(sb.ToString(), 0, sb.Length, all, signed.Length);
        return all;
    }
}
