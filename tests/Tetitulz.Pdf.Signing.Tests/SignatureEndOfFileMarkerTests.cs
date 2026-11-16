// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A signature whose byte range ends at <c>%%EOF</c>, with one end-of-line marker after it, covers the
/// whole document.
/// </summary>
/// <remarks>
/// <para><b>ISO 32000-2 §12.8.1:</b> the byte range runs "from the "%PDF-" comment at the beginning of
/// the PDF document to the end of the "%%EOF" comment, possibly followed by an optional EOL marker".
/// The verifier, the DocMDP analysis and the incremental-save report each compared the range's end with
/// the file's length, so a producer that leaves that marker outside the range had its signature reported
/// as not covering the document — by the verifier as bytes "appended after it", the way a shadow attack
/// looks.</para>
/// <para>No real CMS is needed: coverage is decided from <c>/ByteRange</c> before the signature value is
/// checked, and the fixture's zeroed <c>/Contents</c> keeps that step from mattering.</para>
/// </remarks>
public sealed class SignatureEndOfFileMarkerTests
{
    /// <summary>
    /// A one-page document with one signature field, whose <c>/ByteRange</c> ends right after
    /// <c>%%EOF</c>; <paramref name="afterEof"/> is appended after that, outside the range.
    /// </summary>
    internal static byte[] SignedEndingAtEof(string afterEof, bool certify = false)
    {
        const string rangePlaceholder = "[0 0000000000 0000000000 0000000000]";
        string contents = "<" + new string('0', 64) + ">";

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n"); }

        string perms = certify ? " /Perms << /DocMDP 5 0 R >>" : "";
        string reference = certify
            ? " /Reference [<< /Type /SigRef /TransformMethod /DocMDP /TransformParams << /Type /TransformParams /P 1 /V /1.2 >> >>]"
            : "";

        Obj($"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /SigFlags 3 >>{perms} >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>");
        Obj("<< /FT /Sig /T (Signature1) /Type /Annot /Subtype /Widget /Rect [0 0 0 0] /P 3 0 R /V 5 0 R >>");
        Obj($"<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached{reference} /ByteRange {rangePlaceholder} /Contents {contents} >>");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int offset in offsets) sb.AppendFormat("{0:D10} 00000 n \n", offset);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF", offsets.Count + 1, xref);

        string text = sb.ToString();
        int contentsStart = text.IndexOf("/Contents <", StringComparison.Ordinal) + "/Contents ".Length;
        int contentsEnd = contentsStart + contents.Length;
        int eofEnd = text.Length;
        string range = string.Format("[0 {0:D10} {1:D10} {2:D10}]", contentsStart, contentsEnd, eofEnd - contentsEnd);
        text = text.Replace(rangePlaceholder, range);

        return Encoding.ASCII.GetBytes(text + afterEof);
    }

    private static SignatureVerificationResult Verify(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        return new PdfSignatureVerifier().VerifyAll(doc).Single();
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void ARangeEndingAtEofBeforeOneEndOfLine_CoversTheWholeDocument(string endOfLine)
    {
        var result = Verify(SignedEndingAtEof(endOfLine));

        Assert.True(result.CoversEntireDocument);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("appended after it", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\n\n")]
    [InlineData("\r\r")]
    [InlineData("x")]
    public void AnythingMoreThanOneEndOfLine_IsStillOutsideTheSignature(string afterEof)
    {
        var result = Verify(SignedEndingAtEof(afterEof));

        Assert.False(result.CoversEntireDocument);
        Assert.Contains(result.Warnings, w => w.Contains("appended after it", StringComparison.Ordinal));
    }

    /// <summary>
    /// The document-level shadow check counts only signatures whose CMS verifies, which a zeroed
    /// <c>/Contents</c> never does — so this needs a real token over the range.
    /// </summary>
    [Theory]
    [InlineData("", false)]
    [InlineData("\n", true)]
    public void AVerifiedTimestampEndingAtEof_IsFlaggedAsAShadowAttackOnlyWhenMoreThanOneEndOfLineFollows(
        string appended, bool flagged)
    {
        var root = SigningTestCrypto.Ca("CN=End Of File Root");
        var tsa = SigningTestCrypto.TsaCert(root, "CN=End Of File TSA");
        byte[] timestamped = SignedPdfFixtures.AppendDocumentTimestamp(
            SignedPdfFixtures.MinimalPdf(), tsa, DateTime.UtcNow, new[] { root.Cert },
            sigDictObjNum: 6, sigFieldObjNum: 7, finalEndOfLineUnsigned: true);
        byte[] pdf = timestamped.Concat(Encoding.ASCII.GetBytes(appended)).ToArray();

        var result = Verify(pdf);

        Assert.True(result.IsIntegrityValid);
        Assert.Equal(timestamped.Length - 1, result.ByteRangeCoverageEnd);
        Assert.Equal(flagged, result.ValidationErrors.Any(
            e => e.Contains("appended after the last signature", StringComparison.Ordinal)));
    }

    [Fact]
    public void ACertificationEndingAtEofBeforeOneEndOfLine_HasNoIncrementalUpdates()
    {
        byte[] pdf = SignedEndingAtEof("\r\n", certify: true);
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));

        var analysis = DocMdpAnalyzer.Analyze(doc, pdf.Length);

        Assert.True(analysis.HasCertification);
        Assert.False(analysis.HasIncrementalUpdates);
    }
}
