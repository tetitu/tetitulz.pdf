// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <see cref="PdfSigner.Plan"/> / <see cref="PdfSigner.Complete"/>: a signature revision handed
/// over as objects, appended by any writer, and finished in the written file — the form an
/// encrypted document needs, exercised here through Core's plaintext
/// <see cref="IncrementalUpdateWriter"/> so the plan is proven against the verifier without a
/// file key in the picture.
/// </summary>
/// <remarks>
/// The encrypted composition itself lives in <c>Tetitulz.Pdf.Encryption</c> and is tested there;
/// what this file pins is that the two halves agree with <see cref="PdfSigner.Sign(Stream, Stream, SignatureParameters)"/>
/// about everything but the bytes' route into the file.
/// </remarks>
public sealed class SignaturePlanTests
{
    [Fact]
    public void APlannedSignatureAppendedByTheIncrementalWriterVerifiesAndCoversTheFile()
    {
        var (pfx, pw) = Certificate();
        byte[] pdf = SignedPdfFixtures.MinimalPdf();

        var signer = new PdfSigner();
        SignaturePlan plan;
        using (var doc = PdfDocument.Open(new MemoryStream(pdf)))
            plan = signer.Plan(doc, firstFreeObjectNumber: 0, Parameters(pfx, pw, "planned reason"));

        Assert.Equal("Signature1", plan.FieldName);
        Assert.Equal(3, plan.Objects.Count);

        byte[] signed = AppendAndComplete(pdf, plan, signer);

        Assert.Equal(pdf, signed.Take(pdf.Length).ToArray());   // the original bytes are untouched

        using var verify = new MemoryStream(signed);
        using var signedDoc = PdfDocument.Open(verify);
        Assert.Single(signedDoc.Pages);                         // chained, not re-declared

        var result = new PdfSignatureVerifier().VerifyAll(signedDoc).Single();
        Assert.True(result.IsIntegrityValid, string.Join("; ", result.ValidationErrors));
        Assert.True(result.CoversEntireDocument);
        Assert.True(result.IsValid, string.Join("; ", result.ValidationErrors));
        Assert.Equal("Signature1", result.FieldName);
        Assert.Equal("planned reason", result.Reason);
    }

    /// <summary>
    /// The object numbers come from whichever ceiling is higher — the caller's (an encrypted
    /// original's, which can exceed its decrypted copy's) or the graph's own.
    /// </summary>
    [Fact]
    public void PlanAllocatesPastTheHigherOfTheCallersCeilingAndTheGraphs()
    {
        var (pfx, pw) = Certificate();
        byte[] pdf = SignedPdfFixtures.MinimalPdf();   // objects 1..5
        var signer = new PdfSigner();

        using (var doc = PdfDocument.Open(new MemoryStream(pdf)))
        {
            SignaturePlan high = signer.Plan(doc, firstFreeObjectNumber: 40, Parameters(pfx, pw));
            Assert.Equal(40, high.SignatureObjectNumber);
            Assert.Equal(new[] { 40, 41, 1 }, high.Objects.Select(o => o.ObjectNumber).ToArray());
            Assert.Equal(42, high.NextObjectNumber);

            SignaturePlan low = signer.Plan(doc, firstFreeObjectNumber: 2, Parameters(pfx, pw));
            Assert.Equal(6, low.SignatureObjectNumber);
            Assert.Equal(8, low.NextObjectNumber);
        }
    }

    /// <summary>A DocMDP certification travels into the plan as a <c>/Reference</c>, and the verifier reads it back.</summary>
    [Fact]
    public void APlannedCertificationCarriesItsDocMdpReference()
    {
        var (pfx, pw) = Certificate();
        byte[] pdf = SignedPdfFixtures.MinimalPdf();
        var signer = new PdfSigner();

        var parameters = Parameters(pfx, pw);
        parameters.DocMdpPermission = DocMdpPermission.CommentAnnotations;

        SignaturePlan plan;
        using (var doc = PdfDocument.Open(new MemoryStream(pdf)))
            plan = signer.Plan(doc, 0, parameters);

        byte[] signed = AppendAndComplete(pdf, plan, signer);

        using var signedDoc = PdfDocument.Open(new MemoryStream(signed));
        var result = new PdfSignatureVerifier().VerifyAll(signedDoc).Single();
        Assert.True(result.IsIntegrityValid, string.Join("; ", result.ValidationErrors));
        Assert.True(result.IsCertification);
        Assert.Equal(3, result.CertificationPermission);
    }

    [Fact]
    public void PlanRefusesEmbedLtvUsageRightsAndADocumentWithoutACatalog()
    {
        var (pfx, pw) = Certificate();
        var signer = new PdfSigner();

        using (var doc = PdfDocument.Open(new MemoryStream(SignedPdfFixtures.MinimalPdf())))
        {
            var ltv = Parameters(pfx, pw);
            ltv.EmbedLtv = true;
            var ex1 = Assert.Throws<PdfSigningException>(() => signer.Plan(doc, 0, ltv));
            Assert.Contains("EmbedLtv", ex1.Message);

            var rights = Parameters(pfx, pw);
            rights.UsageRights = UsageRights.FormFillIn;
            var ex2 = Assert.Throws<PdfSigningException>(() => signer.Plan(doc, 0, rights));
            Assert.Contains("UsageRights", ex2.Message);
        }

        // An encrypted original handed in by mistake: PdfDocument.Open stops at /Encrypt and loads
        // no catalog, which is exactly the document a plan cannot read.
        using (var encrypted = PdfDocument.Open(new MemoryStream(EncryptedShell())))
        {
            Assert.True(encrypted.IsEncrypted);
            var ex3 = Assert.Throws<PdfSigningException>(() => signer.Plan(encrypted, 0, Parameters(pfx, pw)));
            Assert.Contains("decrypted copy", ex3.Message);
        }
    }

    /// <summary>
    /// Complete finds the signature dictionary through the file's own cross-reference table, so a
    /// file the plan was never appended to is refused rather than patched at a guessed offset.
    /// </summary>
    [Fact]
    public void CompleteRefusesAFileThatDoesNotCarryThePlansObjects()
    {
        var (pfx, pw) = Certificate();
        byte[] pdf = SignedPdfFixtures.MinimalPdf();
        var signer = new PdfSigner();

        SignaturePlan plan;
        using (var doc = PdfDocument.Open(new MemoryStream(pdf)))
            plan = signer.Plan(doc, 0, Parameters(pfx, pw));

        using var unappended = new MemoryStream();
        unappended.Write(pdf, 0, pdf.Length);
        var ex = Assert.Throws<PdfSigningException>(() => signer.Complete(plan, unappended));
        Assert.Contains(plan.SignatureObjectNumber.ToString(), ex.Message);
        Assert.Equal(pdf, unappended.ToArray());   // nothing was written
    }

    /// <summary>
    /// The FieldMDP reference the plan builds as an object and the one <c>Sign</c> writes raw
    /// describe the same dictionary. The raw form differs by one space inside <c>/Fields</c>, and
    /// only there — asserted with that whitespace folded, so a drift in any entry shows.
    /// </summary>
    [Theory]
    [InlineData("All")]
    [InlineData("Include")]
    public void TheFieldMdpSigRefDictionaryMatchesTheRawWriter(string action)
    {
        var lockDict = new PdfDictionary();
        lockDict.Set(PdfName.Action, new PdfName(action));
        if (action != "All")
        {
            var fields = new PdfArray();
            fields.Add(new PdfString("amount"));
            fields.Add(new PdfString("payee"));
            lockDict.Set(PdfName.Fields, fields);
        }
        var field = new PdfDictionary();
        field.Set(PdfName.Lock, lockDict);

        SignatureFieldLock? fieldLock = SignatureFieldLock.Read(field, o => o);
        Assert.NotNull(fieldLock);

        string raw, planned;
        using (var ms = new MemoryStream())
        {
            fieldLock!.WriteSigRef(new PdfWriter(ms), 1, 2);
            raw = Encoding.ASCII.GetString(ms.ToArray());
        }
        using (var ms = new MemoryStream())
        {
            new PdfWriter(ms).WriteObject(fieldLock.SigRefDictionary(1, 2));
            planned = Encoding.ASCII.GetString(ms.ToArray());
        }

        Assert.Equal(Regex.Replace(raw, @"\s+\]", "]"), planned);
        Assert.Contains("/TransformMethod /FieldMDP", planned);
        // The catalog at its own generation — at 0 it would name no object (§7.3.10).
        Assert.Contains("/Data 1 2 R", planned);
    }

    // ------------------------------------------------------------------ the verifier's two views

    /// <summary>
    /// The two-view entry digests the bytes of its <c>original</c> argument and nothing else: the
    /// same document twice agrees with the single-view overload, and a different file's bytes
    /// under the same object graph fail integrity.
    /// </summary>
    [Fact]
    public void VerifyAllWithSeparateViewsDigestsTheBytesOfTheOriginalArgument()
    {
        var (pfx, pw) = Certificate();
        byte[] unsigned = SignedPdfFixtures.MinimalPdf();
        byte[] signed = SignedPdfFixtures.Sign(unsigned, pfx, pw);

        using var graph = PdfDocument.Open(new MemoryStream(signed));
        using var sameBytes = PdfDocument.Open(new MemoryStream(signed));
        using var otherBytes = PdfDocument.Open(new MemoryStream(unsigned));
        var verifier = new PdfSignatureVerifier();

        var single = verifier.VerifyAll(graph, null).Single();
        var twoViews = verifier.VerifyAll(graph, sameBytes, null, null).Single();
        Assert.True(single.IsIntegrityValid, string.Join("; ", single.ValidationErrors));
        Assert.Equal(single.IsIntegrityValid, twoViews.IsIntegrityValid);
        Assert.Equal(single.CoversEntireDocument, twoViews.CoversEntireDocument);
        Assert.Equal(single.IsValid, twoViews.IsValid);
        Assert.Equal(single.FieldName, twoViews.FieldName);

        var wrongBytes = verifier.VerifyAll(graph, otherBytes, null, null).Single();
        Assert.False(wrongBytes.IsIntegrityValid,
            "the graph's own stream was digested instead of the original argument's");
        Assert.False(wrongBytes.IsValid);

        var byName = verifier.Verify(graph, otherBytes, null, "Signature1", null);
        Assert.False(byName.IsIntegrityValid);
    }

    // ------------------------------------------------------------------

    /// <summary>Appends the plan's objects through Core's writer and completes the signature in the result.</summary>
    private static byte[] AppendAndComplete(byte[] pdf, SignaturePlan plan, PdfSigner signer)
    {
        var appended = new MemoryStream();
        using (var doc = PdfDocument.Open(new MemoryStream(pdf)))
        {
            IncrementalUpdateWriter.WriteUpdate(pdf, appended, plan.Objects,
                new IncrementalUpdateOptions { Document = doc, Size = plan.NextObjectNumber });
        }

        signer.Complete(plan, appended);
        return appended.ToArray();
    }

    /// <summary>
    /// The least a file needs for <see cref="PdfDocument.Open(Stream)"/> to call it encrypted: a
    /// trailer naming an <c>/Encrypt</c> dictionary. Nothing here can be decrypted, and nothing
    /// tries to.
    /// </summary>
    private static byte[] EncryptedShell()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Filter /Standard /V 1 /R 2 /Length 40 /P -1 /O <00> /U <00> >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 3\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n{1:D10} 00000 n \n", o1, o2);
        sb.Append("trailer\n<< /Size 3 /Root 1 0 R /Encrypt 2 0 R /ID [<01> <01>] >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static (byte[] Pfx, string Password) Certificate()
    {
        var root = SigningTestCrypto.Ca("CN=Plan Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Plan Leaf");
        return SigningTestCrypto.Pfx(leaf, root.Cert);
    }

    private static SignatureParameters Parameters(byte[] pfx, string pw, string reason = "test") => new()
    {
        CertificateStream = new MemoryStream(pfx),
        CertificatePassword = pw,
        SignatureFieldName = "Signature1",
        Reason = reason,
    };
}
