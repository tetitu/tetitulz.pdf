// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Org.BouncyCastle.Cms;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// Two signatures whose fields share a partial name — <c>a.sig</c> and <c>b.sig</c> — are told apart
/// by every signing API that names a signature.
/// </summary>
/// <remarks>
/// <para><b>ISO 32000-2 §12.7.4.2 makes the fully qualified name a field's identity.</b> The verifier
/// named each result by its partial name, so both results here were "sig"; asking for <c>b.sig</c>
/// found nothing, and asking for <c>sig</c> answered about whichever signature came first. The
/// counter-signer looked fields up the same way. And the verifier picked out the certification
/// signature by that name, falling back to its signed range — so a certification whose own check
/// stopped before its range was read could not be identified at all.</para>
/// </remarks>
public sealed class SignatureFieldIdentityTests
{
    private static readonly Lazy<Keys> Signers = new(() => SigningTestCrypto.Shared(() =>
    {
        var root = SigningTestCrypto.Ca("CN=Identity Root");
        return new Keys(
            SigningTestCrypto.Pfx(SigningTestCrypto.Leaf(root, "CN=Identity Signer A"), root.Cert),
            SigningTestCrypto.Pfx(SigningTestCrypto.Leaf(root, "CN=Identity Signer B"), root.Cert),
            SigningTestCrypto.Pfx(SigningTestCrypto.Leaf(root, "CN=Identity Counter Signer"), root.Cert));
    }));

    [Fact]
    public void VerifyAllNamesEachSignatureByItsFullyQualifiedName()
    {
        using var doc = Open(TwoSignatures());

        var names = new PdfSignatureVerifier().VerifyAll(doc).Select(r => r.FieldName).OrderBy(n => n);

        Assert.Equal(new[] { "a.sig", "b.sig" }, names);
    }

    [Fact]
    public void VerifyingByFullyQualifiedNameAnswersAboutThatSignature()
    {
        using var doc = Open(TwoSignatures());

        var result = new PdfSignatureVerifier().Verify(doc, "b.sig");

        Assert.Equal("b.sig", result.FieldName);
        Assert.Equal("Identity Signer B", result.SignerCommonName);
        Assert.True(result.IsIntegrityValid);
    }

    [Fact]
    public void VerifyingByAPartialNameTwoFieldsShare_IsRefusedAsAmbiguous()
    {
        using var doc = Open(TwoSignatures());

        var ex = Assert.Throws<PdfVerificationException>(() => new PdfSignatureVerifier().Verify(doc, "sig"));

        Assert.Contains("fully qualified name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CounterSigningByFullyQualifiedNameCounterSignsThatSignature()
    {
        using var output = new MemoryStream();
        PdfCounterSigner.AddCounterSignature(
            new MemoryStream(TwoSignatures()), output, "b.sig",
            Signers.Value.Counter.Pfx, Signers.Value.Counter.Password);

        byte[] counterSigned = output.ToArray();
        Assert.Equal(1, CounterSignatureCount(counterSigned, "b.sig"));
        Assert.Equal(0, CounterSignatureCount(counterSigned, "a.sig"));
    }

    [Fact]
    public void CounterSigningByAPartialNameTwoFieldsShare_IsRefusedAsAmbiguous()
    {
        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfSigningException>(() => PdfCounterSigner.AddCounterSignature(
            new MemoryStream(TwoSignatures()), new MemoryStream(), "sig",
            Signers.Value.Counter.Pfx, Signers.Value.Counter.Password));

        Assert.Contains("fully qualified name", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The certification is identified by its signature dictionary, so it is found even when its own
    /// check stops before its signed range is read — which is when neither a name nor a range can.
    /// </summary>
    [Fact]
    public void TheCertificationIsIdentifiedWhenItsOwnCheckStopsEarly()
    {
        // b.sig certifies; its /SubFilter is then made one the verifier does not support (same
        // length, so no offset moves); a.sig approves afterwards.
        byte[] certified = Sign(TwoFieldsPdf(), "b.sig", Signers.Value.B, DocMdpPermission.FormsAndSignatures);
        byte[] unsupported = ReplaceOnce(certified, "/SubFilter /adbe.pkcs7.detached", "/SubFilter /adbe.pkcs7.detachex");
        byte[] approved = Sign(unsupported, "a.sig", Signers.Value.A, DocMdpPermission.None);

        using var doc = Open(approved);
        var results = new PdfSignatureVerifier().VerifyAll(doc);

        var certification = results.Single(r => r.FieldName == "b.sig");
        Assert.Equal(0, certification.ByteRangeCoverageEnd);   // the premise: its check stopped early
        Assert.True(certification.IsCertification);
        Assert.Equal(2, certification.CertificationPermission);
        Assert.False(results.Single(r => r.FieldName == "a.sig").IsCertification);
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Keys((byte[] Pfx, string Password) A, (byte[] Pfx, string Password) B, (byte[] Pfx, string Password) Counter);

    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    /// <summary><see cref="TwoFieldsPdf"/> with <c>a.sig</c> signed by A and then <c>b.sig</c> by B.</summary>
    private static byte[] TwoSignatures()
        => Sign(Sign(TwoFieldsPdf(), "a.sig", Signers.Value.A, DocMdpPermission.None),
                "b.sig", Signers.Value.B, DocMdpPermission.None);

    /// <summary>
    /// Two empty signature fields named <c>sig</c>, under parents <c>a</c> (5) and <c>b</c> (7) —
    /// objects 6 and 8, each merged with its widget.
    /// </summary>
    private static byte[] TwoFieldsPdf() => SignedPdfFixtures.Assemble(
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 7 0 R] /SigFlags 3 >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [6 0 R 8 0 R] >>",
        "<< /Length 0 >>\nstream\n\nendstream",
        "<< /T (a) /Kids [6 0 R] >>",
        "<< /T (sig) /FT /Sig /Type /Annot /Subtype /Widget /Rect [50 60 250 140] /F 4 /P 3 0 R /Parent 5 0 R >>",
        "<< /T (b) /Kids [8 0 R] >>",
        "<< /T (sig) /FT /Sig /Type /Annot /Subtype /Widget /Rect [50 160 250 240] /F 4 /P 3 0 R /Parent 7 0 R >>");

    private static byte[] Sign(byte[] pdf, string field, (byte[] Pfx, string Password) signer, DocMdpPermission certification)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = new MemoryStream(signer.Pfx),
            CertificatePassword = signer.Password,
            SignatureFieldName = field,
            DocMdpPermission = certification,
        });
        return output.ToArray();
    }

    private static byte[] ReplaceOnce(byte[] pdf, string find, string replace)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int at = text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(at >= 0 && text.IndexOf(find, at + 1, StringComparison.Ordinal) < 0, $"'{find}' is not in the fixture exactly once");
        return Encoding.Latin1.GetBytes(text.Substring(0, at) + replace + text.Substring(at + find.Length));
    }

    /// <summary>How many counter-signatures the CMS of the signature at <paramref name="field"/> carries.</summary>
    private static int CounterSignatureCount(byte[] pdf, string field)
    {
        using var doc = Open(pdf);
        var signature = doc.AcroForm.GetSignatureFields().Single(f => f.FullyQualifiedName == field && f.HasSignature);
        byte[] contents = SigningDefaults.TrimToAsn1Object(signature.SignatureValue!.GetString(PdfName.Contents)!.RawBytes);

        int count = 0;
        foreach (SignerInformation s in new CmsSignedData(contents).GetSignerInfos().GetSigners())
            count += s.GetCounterSignatures().Count;
        return count;
    }
}
