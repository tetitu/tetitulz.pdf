// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The order signatures may come in, and what a certification permits after it — enforced when
/// signing and reported when verifying.
/// </summary>
/// <remarks>
/// <para><b>ISO 32000-2 §12.8.1:</b> "At most one certification signature"; approval signatures "shall
/// follow the certification signature if one is present"; "any number of document timestamp
/// signatures". <b>§12.8.2.2, Table 257, <c>/P 1</c>:</b> "No changes to the document shall be
/// permitted; any change to the document shall invalidate the signature."</para>
///
/// <para>The verifier tests build their non-conforming documents through the signer's internal
/// entry point with the checks off, because the public one refuses to write them.</para>
/// </remarks>
public sealed class SignatureOrderingTests
{
    private static readonly Lazy<(byte[] Pfx, string Password)> Signer = new(() => SigningTestCrypto.Shared(() =>
    {
        var ca = SigningTestCrypto.Ca("CN=Ordering Root");
        return SigningTestCrypto.Pfx(SigningTestCrypto.Leaf(ca, "CN=Ordering Signer"), ca.Cert);
    }));

    // ------------------------------------------------------------------ signing refuses

    [Fact]
    public void ACertifiedDocumentCannotBeCertifiedAgain()
    {
        byte[] certified = Sign(SignedPdfFixtures.MinimalPdf(), "Author", DocMdpPermission.FormsAndSignatures);

        var (ex, written) = Refused(certified, "Second", DocMdpPermission.FormsAndSignatures);

        Assert.Contains("at most one certification signature", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, written);
    }

    [Fact]
    public void ASignedDocumentCannotBeCertified()
    {
        byte[] approved = Sign(SignedPdfFixtures.MinimalPdf(), "Approval");

        var (ex, written) = Refused(approved, "Author", DocMdpPermission.FormsAndSignatures);

        Assert.Contains("approval signatures to follow the certification signature", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, written);
    }

    /// <summary>§12.8.1 orders certification and approval signatures only; timestamps are "any number".</summary>
    [Fact]
    public void ADocumentTimestampDoesNotStopCertification()
    {
        var root = SigningTestCrypto.Ca("CN=Ordering TSA Root");
        byte[] timestamped = SignedPdfFixtures.AppendDocumentTimestamp(
            SignedPdfFixtures.MinimalPdf(), SigningTestCrypto.TsaCert(root), DateTime.UtcNow, new[] { root.Cert },
            sigDictObjNum: 6, sigFieldObjNum: 7);

        byte[] certified = Sign(timestamped, "Author", DocMdpPermission.FormsAndSignatures);

        var author = VerifyAll(certified).Single(r => r.FieldName == "Author");
        Assert.True(author.IsCertification);
        Assert.DoesNotContain(author.ValidationErrors, e => e.Contains("§12.8.1", StringComparison.Ordinal));
    }

    [Fact]
    public void AnApprovalSignatureOnAP1CertifiedDocumentIsRefused()
    {
        byte[] certified = Sign(SignedPdfFixtures.MinimalPdf(), "Author", DocMdpPermission.NoChanges);

        var (ex, written) = Refused(certified, "Approval", DocMdpPermission.None);

        Assert.Contains("AllowCertificationInvalidation", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, written);
    }

    [Fact]
    public void AnApprovalSignatureOnAP1CertifiedDocumentIsWrittenWhenTheCallerAcceptsBreakingIt()
    {
        byte[] certified = Sign(SignedPdfFixtures.MinimalPdf(), "Author", DocMdpPermission.NoChanges);

        byte[] signed = Sign(certified, "Approval", allowCertificationInvalidation: true);

        var author = VerifyAll(signed).Single(r => r.FieldName == "Author");
        Assert.NotEmpty(author.MdpViolations);
    }

    [Theory]
    [InlineData(DocMdpPermission.FormsAndSignatures)]
    [InlineData(DocMdpPermission.CommentAnnotations)]
    public void AnApprovalSignatureIsAllowedWhereTheCertificationPermitsSigning(DocMdpPermission permission)
    {
        byte[] certified = Sign(SignedPdfFixtures.MinimalPdf(), "Author", permission);

        byte[] signed = Sign(certified, "Approval");

        var results = VerifyAll(signed);
        Assert.Equal(2, results.Count);
        Assert.Empty(results.Single(r => r.FieldName == "Author").MdpViolations);
    }

    /// <summary>
    /// §7.6.2 encrypts a signature dictionary's strings in an encrypted document; writing them in the
    /// clear produced a signature whose /Reason, /M and field name read back as garbage.
    /// </summary>
    [Fact]
    public void AnEncryptedDocumentIsRefused()
    {
        using var input = new MemoryStream(EncryptedLookingPdf());
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfSigningException>(() => new PdfSigner().Sign(input, output, Parameters("Approval")));

        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, output.Length);
    }

    // ------------------------------------------------------------------ verifying reports

    [Fact]
    public void ACertificationAddedAfterAnApprovalIsReported()
    {
        byte[] approved = Sign(SignedPdfFixtures.MinimalPdf(), "Approval");
        byte[] certifiedAfter = SignUnchecked(approved, "Author", DocMdpPermission.FormsAndSignatures);

        var results = VerifyAll(certifiedAfter);

        Assert.Contains(results.Single(r => r.FieldName == "Author").ValidationErrors,
            e => e.Contains("added after an approval signature", StringComparison.Ordinal));
        Assert.DoesNotContain(results.Single(r => r.FieldName == "Approval").ValidationErrors,
            e => e.Contains("§12.8.1", StringComparison.Ordinal));
    }

    [Fact]
    public void ASecondCertificationIsReported()
    {
        byte[] certified = Sign(SignedPdfFixtures.MinimalPdf(), "First", DocMdpPermission.CommentAnnotations);
        byte[] twice = SignUnchecked(certified, "Second", DocMdpPermission.CommentAnnotations);

        var results = VerifyAll(twice);

        Assert.Contains(results.Single(r => r.FieldName == "Second").ValidationErrors,
            e => e.Contains("second certification signature", StringComparison.Ordinal));
        Assert.DoesNotContain(results.Single(r => r.FieldName == "First").ValidationErrors,
            e => e.Contains("§12.8.1", StringComparison.Ordinal));
    }

    /// <summary>The control: the order §12.8.1 asks for reports nothing.</summary>
    [Fact]
    public void ACertificationFollowedByApprovalsIsNotReported()
    {
        byte[] certified = Sign(SignedPdfFixtures.MinimalPdf(), "Author", DocMdpPermission.FormsAndSignatures);
        byte[] signed = Sign(Sign(certified, "Approval1"), "Approval2");

        Assert.All(VerifyAll(signed), r =>
            Assert.DoesNotContain(r.ValidationErrors, e => e.Contains("§12.8.1", StringComparison.Ordinal)));
    }

    // ------------------------------------------------------------------ counter-signing

    /// <summary>
    /// A later signature's range covers every earlier signature's <c>/Contents</c>, so rewriting one
    /// in place breaks it.
    /// </summary>
    [Fact]
    public void ASignatureALaterSignatureCoversIsNotCounterSigned()
    {
        byte[] twice = Sign(Sign(SignedPdfFixtures.MinimalPdf(), "First"), "Second");

        using var input = new MemoryStream(twice);
        using var output = new MemoryStream();
        var ex = Assert.Throws<PdfSigningException>(() =>
            PdfCounterSigner.AddCounterSignature(input, output, "First", Signer.Value.Pfx, Signer.Value.Password));

        Assert.Contains("'Second' signs the bytes of 'First'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>The control: the newest signature has nothing after it to break.</summary>
    [Fact]
    public void TheNewestSignatureIsCounterSignedAndEveryEarlierOneStillVerifies()
    {
        byte[] twice = Sign(Sign(SignedPdfFixtures.MinimalPdf(), "First"), "Second");

        using var input = new MemoryStream(twice);
        using var output = new MemoryStream();
        PdfCounterSigner.AddCounterSignature(input, output, "Second", Signer.Value.Pfx, Signer.Value.Password);

        Assert.All(VerifyAll(output.ToArray()), r => Assert.True(r.IsIntegrityValid, r.FieldName));
    }

    // ------------------------------------------------------------------ each reading on its own

    /// <summary>
    /// Table 263 makes <c>/Perms /DocMDP</c> optional, so a certification is recognised from the
    /// signature's own <c>/Reference</c> too.
    /// </summary>
    [Fact]
    public void ACertificationWithoutPermsIsStillRecognised()
    {
        byte[] certified = Sign(SignedPdfFixtures.MinimalPdf(), "Author", DocMdpPermission.NoChanges);
        byte[] withoutPerms = AppendRevision(certified, CatalogWithout(certified, "Perms"));

        var (ex, _) = Refused(withoutPerms, "Approval", DocMdpPermission.None);

        Assert.Contains("DocMDP /P 1", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The certification the analyzer reads first — <c>/Perms /DocMDP</c> — is recognised even when no field carries it.</summary>
    [Fact]
    public void ACertificationNamedOnlyByPermsIsStillRecognised()
    {
        var (ex, _) = Refused(PdfCertifiedOnlyThroughPerms(), "Approval", DocMdpPermission.None);

        Assert.Contains("DocMDP /P 1", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The overlap is tested against both of the other signature's ranges, so a range that resumes
    /// inside the target's <c>/Contents</c> stops the counter-signature as surely as one that runs over it.
    /// </summary>
    [Fact]
    public void ASecondRangeOverTheContentsAlsoStopsCounterSigning()
    {
        byte[] signed = Sign(SignedPdfFixtures.MinimalPdf(), "First");
        var (holeStart, _) = ContentsHole(signed);

        int next = MaxObjectNumber(signed) + 1;
        byte[] covered = AppendRevision(signed,
            (next, $"<< /FT /Sig /T (Overlap) /V {next + 1} 0 R >>"),
            (next + 1, $"<< /Type /Sig /ByteRange [0 10 {holeStart + 2} 4] /Contents <00> >>"),
            CatalogWithField(signed, next));

        using var input = new MemoryStream(covered);
        using var output = new MemoryStream();
        var ex = Assert.Throws<PdfSigningException>(() =>
            PdfCounterSigner.AddCounterSignature(input, output, "First", Signer.Value.Pfx, Signer.Value.Password));
        Assert.Contains("'Overlap' signs the bytes of 'First'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Order is the order of the revisions, not of <c>/Fields</c>: here the certification adopts a field
    /// listed before the approval's, so a check reading fields in form order sees it first.
    /// </summary>
    [Fact]
    public void ACertificationAddedAfterAnApprovalIsReportedWhateverTheFieldOrder()
    {
        byte[] approved = Sign(PdfWithPreparedSignatureField("Author"), "Approval");
        byte[] certifiedAfter = SignUnchecked(approved, "Author", DocMdpPermission.FormsAndSignatures);

        var results = VerifyAll(certifiedAfter);

        Assert.Equal("Author", results[0].FieldName);   // the premise: form order puts the certification first
        Assert.Contains(results.Single(r => r.FieldName == "Author").ValidationErrors,
            e => e.Contains("added after an approval signature", StringComparison.Ordinal));
    }

    /// <summary>Two fields naming one certification dictionary are one certification, not two.</summary>
    [Fact]
    public void TwoFieldsNamingOneCertificationAreNotASecondCertification()
    {
        byte[] certified = Sign(SignedPdfFixtures.MinimalPdf(), "Author", DocMdpPermission.FormsAndSignatures);
        int signature = SignatureObjectNumber(certified, "Author");
        int next = MaxObjectNumber(certified) + 1;
        byte[] twoNames = AppendRevision(certified,
            (next, $"<< /FT /Sig /T (AuthorAgain) /V {signature} 0 R >>"),
            CatalogWithField(certified, next));

        var results = VerifyAll(twoNames);

        Assert.Equal(2, results.Count);   // the premise: the verifier reports per field
        Assert.All(results, r =>
            Assert.DoesNotContain(r.ValidationErrors, e => e.Contains("§12.8.1", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A signature whose range cannot be read has no place in the order, so it cannot put a
    /// certification out of it.
    /// </summary>
    [Fact]
    public void AnUnreadableApprovalDoesNotPutACertificationOutOfOrder()
    {
        byte[] certified = Sign(SignedPdfFixtures.MinimalPdf(), "Author", DocMdpPermission.FormsAndSignatures);
        int next = MaxObjectNumber(certified) + 1;
        byte[] withBroken = AppendRevision(certified,
            (next, $"<< /FT /Sig /T (Broken) /V {next + 1} 0 R >>"),
            (next + 1, "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /Contents <00> >>"),
            CatalogWithField(certified, next));

        var author = VerifyAll(withBroken).Single(r => r.FieldName == "Author");

        Assert.DoesNotContain(author.ValidationErrors, e => e.Contains("§12.8.1", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ helpers

    private static SignatureParameters Parameters(string field, DocMdpPermission permission = DocMdpPermission.None,
                                                  bool allowCertificationInvalidation = false)
        => new()
        {
            CertificateStream = new MemoryStream(Signer.Value.Pfx),
            CertificatePassword = Signer.Value.Password,
            SignatureFieldName = field,
            DocMdpPermission = permission,
            AllowCertificationInvalidation = allowCertificationInvalidation,
        };

    private static byte[] Sign(byte[] pdf, string field, DocMdpPermission permission = DocMdpPermission.None,
                               bool allowCertificationInvalidation = false)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, Parameters(field, permission, allowCertificationInvalidation));
        return output.ToArray();
    }

    private static byte[] SignUnchecked(byte[] pdf, string field, DocMdpPermission permission)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, Parameters(field, permission), refuseConflictingSignatures: false);
        return output.ToArray();
    }

    private static (PdfSigningException Exception, long Written) Refused(byte[] pdf, string field, DocMdpPermission permission)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        var ex = Assert.Throws<PdfSigningException>(() => new PdfSigner().Sign(input, output, Parameters(field, permission)));
        return (ex, output.Length);
    }

    private static System.Collections.Generic.IReadOnlyList<SignatureVerificationResult> VerifyAll(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        return new PdfSignatureVerifier().VerifyAll(doc);
    }

    /// <summary>
    /// Appends one incremental revision declaring <paramref name="objects"/>, chained to the document's
    /// newest cross-reference section and carrying its trailer's <c>/Root</c>.
    /// </summary>
    private static byte[] AppendRevision(byte[] pdf, params (int Number, string Body)[] objects)
    {
        int size = MaxObjectNumber(pdf) + 1;
        long prev = SignedPdfFixtures.FindStartXref(pdf);

        var body = new StringBuilder();
        var entries = new StringBuilder();
        foreach (var (number, text) in objects)
        {
            entries.Append($"{number} 1\n{pdf.Length + body.Length:D10} 00000 n \n");
            body.Append($"{number} 0 obj\n{text}\nendobj\n");
            size = Math.Max(size, number + 1);
        }

        long xref = pdf.Length + body.Length;
        body.Append("xref\n").Append(entries)
            .Append($"trailer\n<< /Size {size} /Root 1 0 R /Prev {prev} >>\nstartxref\n{xref}\n%%EOF\n");

        return pdf.Concat(Encoding.Latin1.GetBytes(body.ToString())).ToArray();
    }

    /// <summary>The document's current catalog (object 1), less <paramref name="key"/>.</summary>
    private static (int, string) CatalogWithout(byte[] pdf, string key)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        var catalog = Tetitulz.Pdf.Core.Objects.PdfDictionary.CloneShallow(doc.Catalog.Dictionary);
        Assert.True(catalog.Remove(new Tetitulz.Pdf.Core.Objects.PdfName(key)), $"the catalog has no /{key} to remove");
        return (1, Serialise(catalog));
    }

    /// <summary>The document's current catalog (object 1) with field <paramref name="field"/> added to its inline form.</summary>
    private static (int, string) CatalogWithField(byte[] pdf, int field)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        var catalog = Tetitulz.Pdf.Core.Objects.PdfDictionary.CloneShallow(doc.Catalog.Dictionary);
        var form = Tetitulz.Pdf.Core.Objects.PdfDictionary.CloneShallow(
            (Tetitulz.Pdf.Core.Objects.PdfDictionary)doc.ResolveObject(catalog[Tetitulz.Pdf.Core.Objects.PdfName.AcroForm]));
        var fields = new Tetitulz.Pdf.Core.Objects.PdfArray(
            (Tetitulz.Pdf.Core.Objects.PdfArray)doc.ResolveObject(form[Tetitulz.Pdf.Core.Objects.PdfName.Fields]));
        fields.Add(new Tetitulz.Pdf.Core.Objects.PdfReference(field, 0));
        form.Set(Tetitulz.Pdf.Core.Objects.PdfName.Fields, fields);
        catalog.Set(Tetitulz.Pdf.Core.Objects.PdfName.AcroForm, form);
        return (1, Serialise(catalog));
    }

    private static string Serialise(Tetitulz.Pdf.Core.Objects.PdfObject obj)
    {
        using var ms = new MemoryStream();
        new Tetitulz.Pdf.Core.Writing.PdfWriter(ms).WriteObject(obj);
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    private static int MaxObjectNumber(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        return doc.CrossReferenceTable.GetMaxObjectNumber();
    }

    private static int SignatureObjectNumber(byte[] pdf, string field)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        var signature = doc.AcroForm.GetSignatureFields().Single(f => f.FieldName == field).SignatureValue;
        Assert.True(signature.ObjectNumber > 0, "the fixture's signature dictionary is not an indirect object");
        return signature.ObjectNumber;
    }

    /// <summary>The first signature's <c>/Contents</c> hole — <c>/ByteRange</c> elements 1 and 2.</summary>
    private static (long Start, long End) ContentsHole(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        var signature = doc.AcroForm.GetSignatureFields().First(f => f.HasSignature).SignatureValue;
        var range = (Tetitulz.Pdf.Core.Objects.PdfArray)doc.ResolveObject(signature[Tetitulz.Pdf.Core.Objects.PdfName.ByteRange]);
        long start = ((Tetitulz.Pdf.Core.Objects.PdfNumber)range[0]).LongValue + ((Tetitulz.Pdf.Core.Objects.PdfNumber)range[1]).LongValue;
        return (start, ((Tetitulz.Pdf.Core.Objects.PdfNumber)range[2]).LongValue);
    }

    /// <summary>
    /// A one-page document whose catalog names a <c>/P 1</c> certification through <c>/Perms /DocMDP</c>
    /// only — no form, so no field walk can reach the signature.
    /// </summary>
    private static byte[] PdfCertifiedOnlyThroughPerms()
    {
        var b = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string s) { offsets.Add(b.Length); b.Append(s); }
        b.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Perms << /DocMDP 4 0 R >> >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        Obj("4 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached "
            + "/Reference [<< /Type /SigRef /TransformMethod /DocMDP /TransformParams << /Type /TransformParams /P 1 /V /1.2 >> >>] "
            + "/ByteRange [0 0 0 0] /Contents <00> >>\nendobj\n");
        int count = offsets.Count + 1;
        int xref = b.Length;
        b.Append($"xref\n0 {count}\n0000000000 65535 f \n");
        foreach (int o in offsets) b.Append(o.ToString("D10")).Append(" 00000 n \n");
        b.Append($"trailer\n<< /Size {count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(b.ToString());
    }

    /// <summary>A one-page document whose form holds one prepared, still unsigned signature field.</summary>
    private static byte[] PdfWithPreparedSignatureField(string name)
    {
        var b = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string s) { offsets.Add(b.Length); b.Append(s); }
        b.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>\nendobj\n");
        Obj($"4 0 obj\n<< /FT /Sig /T ({name}) /Type /Annot /Subtype /Widget /Rect [0 0 0 0] /P 3 0 R >>\nendobj\n");
        int count = offsets.Count + 1;
        int xref = b.Length;
        b.Append($"xref\n0 {count}\n0000000000 65535 f \n");
        foreach (int o in offsets) b.Append(o.ToString("D10")).Append(" 00000 n \n");
        b.Append($"trailer\n<< /Size {count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(b.ToString());
    }

    /// <summary>A document whose trailer declares /Encrypt with a well-formed standard handler dictionary.</summary>
    private static byte[] EncryptedLookingPdf()
    {
        var b = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string s) { offsets.Add(b.Length); b.Append(s); }
        b.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        Obj("4 0 obj\n<< /Filter /Standard /V 2 /R 3 /Length 128 /P -4 "
            + "/O <0000000000000000000000000000000000000000000000000000000000000000> "
            + "/U <0000000000000000000000000000000000000000000000000000000000000000> >>\nendobj\n");
        int count = offsets.Count + 1;
        int xref = b.Length;
        b.Append($"xref\n0 {count}\n0000000000 65535 f \n");
        foreach (int o in offsets) b.Append(o.ToString("D10")).Append(" 00000 n \n");
        b.Append($"trailer\n<< /Size {count} /Root 1 0 R /Encrypt 4 0 R /ID [<0102> <0102>] >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(b.ToString());
    }
}
