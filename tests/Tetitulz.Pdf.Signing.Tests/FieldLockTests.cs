// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A signature field's <c>/Lock</c> (ISO 32000-2 Table 236) is carried into the signature as a FieldMDP
/// reference (§12.8.2.4, Table 259), and the verifier reports a locked field that changed afterwards.
/// </summary>
/// <remarks>
/// <para><b>§12.8.2.4:</b> "When the recipient signs the field, the signature, signature reference, and
/// transform parameters dictionaries shall be created. The Action and Fields entries in the transform
/// parameters dictionary shall be copied from the corresponding fields in the signature field lock
/// dictionary", and FieldMDP signatures "shall be validated in a similar manner to DocMDP signatures".
/// <c>PdfSigner</c> adopted a locked field and wrote no reference, and the verifier checked no lock:
/// an author's "these fields may not change once this person has signed" was dropped at the moment it
/// was meant to take effect.</para>
///
/// <para><b>The verifier's tests filter on the <c>FieldMDP:</c> prefix</b>, because a revision appended
/// after the last signature also earns every result the document-level "unsigned trailing bytes" error,
/// which is not what these cases are about.</para>
/// </remarks>
public sealed class FieldLockTests
{
    private static readonly Lazy<(byte[] Pfx, string Password)> Keys = new(() => SigningTestCrypto.Shared(() =>
    {
        var root = SigningTestCrypto.Ca("CN=Field Lock Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Field Lock Signer");
        return SigningTestCrypto.Pfx(leaf, root.Cert);
    }));

    private const string IncludeName = "<< /Type /SigFieldLock /Action /Include /Fields [(Name)] >>";
    private const string ExcludeDate = "<< /Type /SigFieldLock /Action /Exclude /Fields [(Date)] >>";
    private const string LockAll = "<< /Type /SigFieldLock /Action /All >>";

    // ================================================================ the signer

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdoptingALockedFieldWritesTheFieldMdpReference(bool indirectLock)
    {
        using var doc = Open(Sign(FormPdf(IncludeName, indirectLock)));

        var parameters = FieldMdpParameters(doc);

        Assert.Equal("Include", (doc.ResolveObject(parameters[PdfName.Action]) as PdfName)?.Value);
        var fields = Assert.IsType<PdfArray>(parameters[PdfName.Fields]);
        Assert.Equal("Name", Assert.IsType<PdfString>(Assert.Single(fields)).GetText());   // copied, direct
        Assert.Equal("1.2", (parameters[PdfName.V] as PdfName)?.Value);
    }

    /// <summary>Table 258: <c>/Data</c> is an indirect reference to the object analysed — the catalog.</summary>
    [Fact]
    public void TheReferenceNamesTheCatalogAsItsData()
    {
        using var doc = Open(Sign(FormPdf(IncludeName)));

        var sigRef = FieldMdpReference(doc);

        var data = Assert.IsType<PdfReference>(sigRef[PdfName.Data]);
        Assert.Equal("Catalog", ((doc.ResolveObject(data) as PdfDictionary)?[PdfName.Type] as PdfName)?.Value);
        Assert.NotNull(sigRef[PdfName.DigestMethod]);
    }

    /// <summary>
    /// …at the catalog's own generation. <c>/Data 1 0 R</c> to a catalog at 1 names no object
    /// (§7.3.10), and the lock's analysis would have no document to run on. Both routes are
    /// asserted: <c>Sign</c> writes the reference raw, and <c>Plan</c> builds it as an object.
    /// <see cref="TheReferenceNamesTheCatalogAsItsData"/> is the generation-0 control.
    /// </summary>
    [Fact]
    public void TheDataReferenceCarriesTheCatalogsGeneration_WrittenAndPlanned()
    {
        byte[] pdf = CatalogAtGenerationOne(FormPdf(IncludeName));

        using (var signed = Open(Sign(pdf)))
        {
            var data = Assert.IsType<PdfReference>(FieldMdpReference(signed)[PdfName.Data]);
            Assert.Equal((1, 1), (data.ReferenceObjectNumber, data.ReferenceGenerationNumber));
        }

        SignaturePlan plan;
        using (var doc = Open(pdf))
            plan = new PdfSigner().Plan(doc, 0, new SignatureParameters
            {
                CertificateStream = new MemoryStream(Keys.Value.Pfx),
                CertificatePassword = Keys.Value.Password,
                SignatureFieldName = "Sig",
            });

        PdfDictionary planned = plan.Objects
            .Select(o => o.Object as PdfDictionary)
            .SelectMany(d => (d?[PdfName.Reference] as PdfArray)?.OfType<PdfDictionary>() ?? Enumerable.Empty<PdfDictionary>())
            .Single(r => (r[PdfName.TransformMethod] as PdfName)?.Value == PdfName.FieldMDP.Value);
        var plannedData = Assert.IsType<PdfReference>(planned[PdfName.Data]);
        Assert.Equal((1, 1), (plannedData.ReferenceObjectNumber, plannedData.ReferenceGenerationNumber));
    }

    /// <summary>
    /// <paramref name="pdf"/> with its catalog (object 1, the first body) at generation 1 — the
    /// header, the cross-reference row and the trailer's <c>/Root</c>, each rewritten in place at
    /// the same length so every offset still holds.
    /// </summary>
    private static byte[] CatalogAtGenerationOne(byte[] pdf)
    {
        string text = Encoding.ASCII.GetString(pdf);
        string header = "%PDF-1.7\n1 0 obj";
        Assert.StartsWith(header, text, StringComparison.Ordinal);
        text = "%PDF-1.7\n1 1 obj" + text.Substring(header.Length);

        const string freeHead = "0000000000 65535 f \n";
        int row = text.IndexOf(freeHead, StringComparison.Ordinal) + freeHead.Length;
        Assert.Equal(" 00000 n ", text.Substring(row + 10, 9));
        text = text.Substring(0, row + 10) + " 00001 n " + text.Substring(row + 19);

        Assert.Contains("/Root 1 0 R", text, StringComparison.Ordinal);
        text = text.Replace("/Root 1 0 R", "/Root 1 1 R");
        return Encoding.ASCII.GetBytes(text);
    }

    [Fact]
    public void AnAllLockWritesNoFieldsArray()
    {
        using var doc = Open(Sign(FormPdf(LockAll)));

        var parameters = FieldMdpParameters(doc);

        Assert.Equal("All", (parameters[PdfName.Action] as PdfName)?.Value);
        Assert.Null(parameters[PdfName.Fields]);
    }

    /// <summary>The control: a field without a lock gets no FieldMDP reference.</summary>
    [Fact]
    public void AFieldWithoutALockWritesNoFieldMdpReference()
    {
        using var doc = Open(Sign(FormPdf(lockEntry: null)));

        Assert.DoesNotContain(SignatureReferences(doc), r => IsFieldMdp(doc, r));
    }

    /// <summary>Table 236 <c>/P</c>: the lock's DocMDP level is the level the signature is made at.</summary>
    [Fact]
    public void ALockPermissionMakesTheSignatureACertificationAtThatLevel()
    {
        byte[] signed = Sign(FormPdf("<< /Type /SigFieldLock /Action /All /P 2 >>"));

        using var doc = Open(signed);
        var result = new PdfSignatureVerifier().VerifyAll(doc).Single();

        Assert.True(result.IsCertification);
        Assert.Equal(2, result.CertificationPermission);
        Assert.Contains(SignatureReferences(doc), r => IsFieldMdp(doc, r));
    }

    /// <summary>
    /// The lock's level goes through the same §12.8.1 ordering rules as a caller's: a document that is
    /// already certified cannot take a second certification from a lock either.
    /// </summary>
    [Fact]
    public void ALockPermissionOnACertifiedDocument_IsRefusedLikeAnyCertification()
    {
        byte[] certified = Sign(FormPdf("<< /Type /SigFieldLock /Action /All /P 2 >>"),
                                DocMdpPermission.FormsAndSignatures, field: "Author");

        var ex = Assert.Throws<PdfSigningException>(() => Sign(certified));

        Assert.Contains("already certified", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALockPermissionThatDisagreesWithTheCallers_IsRefused()
    {
        var ex = Assert.Throws<PdfSigningException>(() =>
            Sign(FormPdf("<< /Type /SigFieldLock /Action /All /P 1 >>"), DocMdpPermission.FormsAndSignatures));

        Assert.Contains("/Lock", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<< /Type /SigFieldLock /Fields [(Name)] >>", "/Action")]
    [InlineData("<< /Type /SigFieldLock /Action /Include >>", "/Fields")]
    [InlineData("<< /Type /SigFieldLock /Action /All /P 7 >>", "/P")]
    public void ALockThatCannotBeHonoured_IsRefusedRatherThanDropped(string lockDict, string named)
    {
        var ex = Assert.Throws<PdfSigningException>(() => Sign(FormPdf(lockDict)));

        Assert.Contains(named, ex.Message, StringComparison.Ordinal);
    }

    // ================================================================ the verifier

    [Fact]
    public void ALockedFieldChangedAfterSigning_IsReportedOnTheLockingSignature()
    {
        byte[] changed = ChangeValue(Sign(FormPdf(IncludeName)), NameField, "Mallory");

        var errors = FieldMdpErrors(changed);

        Assert.Contains(errors, e => e.Contains("'Name'", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnlockedFieldChangedAfterSigning_IsNotReported()
    {
        byte[] changed = ChangeValue(Sign(FormPdf(IncludeName)), DateField, "2027-01-01");

        Assert.Empty(FieldMdpErrors(changed));
    }

    [Fact]
    public void AnAllLockReportsAnyFieldThatChanged()
    {
        byte[] changed = ChangeValue(Sign(FormPdf(LockAll)), DateField, "2027-01-01");

        Assert.Contains(FieldMdpErrors(changed), e => e.Contains("'Date'", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExcludeLockLeavesTheExcludedFieldFreeAndLocksTheRest()
    {
        byte[] signed = Sign(FormPdf(ExcludeDate));

        Assert.Empty(FieldMdpErrors(ChangeValue(signed, DateField, "2027-01-01")));
        Assert.Contains(FieldMdpErrors(ChangeValue(signed, NameField, "Mallory")),
            e => e.Contains("'Name'", StringComparison.Ordinal));
    }

    /// <summary>
    /// An <c>/Exclude</c> list names a parent and so frees its children, as an <c>/Include</c> list
    /// naming a parent locks them: the list was read hierarchically for one action only, so a fill of
    /// <c>group.a</c> the signer left open was reported as a violation. A field outside the group is
    /// still locked (the control).
    /// </summary>
    [Fact]
    public void AnExcludeLockNamingAParent_LeavesItsChildrenFree()
    {
        byte[] form = SignedPdfFixtures.Assemble(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 7 0 R 8 0 R] /SigFlags 3 /DA (/Helv 0 Tf 0 g) >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [6 0 R 7 0 R 8 0 R] >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /T (group) /Kids [6 0 R] >>",
            "<< /FT /Tx /T (a) /V (x) /Parent 5 0 R /Type /Annot /Subtype /Widget /Rect [50 700 250 720] /F 4 /P 3 0 R >>",
            "<< /FT /Sig /T (Sig) /Type /Annot /Subtype /Widget /Rect [50 600 250 640] /F 4 /P 3 0 R " +
                "/Lock << /Type /SigFieldLock /Action /Exclude /Fields [(group)] >> >>",
            "<< /FT /Tx /T (x) /V (y) /Type /Annot /Subtype /Widget /Rect [50 660 250 680] /F 4 /P 3 0 R >>");
        byte[] signed = Sign(form);

        Assert.Empty(FieldMdpErrors(ChangeValue(signed, 6, "z")));
        Assert.Contains(FieldMdpErrors(ChangeValue(signed, 8, "w")), e => e.Contains("'x'", StringComparison.Ordinal));
    }

    [Fact]
    public void ALockedFieldRemovedAfterSigning_IsReported()
    {
        byte[] removed = RemoveNameField(Sign(FormPdf(IncludeName)));

        Assert.Contains(FieldMdpErrors(removed), e => e.Contains("removed", StringComparison.Ordinal));
    }

    /// <summary>The control: nothing appended, nothing reported, and the signature verifies.</summary>
    [Fact]
    public void AnUnchangedDocumentReportsNoViolation()
    {
        byte[] signed = Sign(FormPdf(IncludeName));

        using var doc = Open(signed);
        var result = new PdfSignatureVerifier().VerifyAll(doc).Single();

        Assert.True(result.IsIntegrityValid);
        Assert.DoesNotContain(result.ValidationErrors, e => e.StartsWith("FieldMDP", StringComparison.Ordinal));
    }

    /// <summary>The Core analysis names the lock it found and the field that broke it.</summary>
    [Fact]
    public void TheAnalysisReportsTheLockAndTheChangedField()
    {
        byte[] changed = ChangeValue(Sign(FormPdf(IncludeName)), NameField, "Mallory");

        using var doc = Open(changed);
        var analysis = Assert.Single(FieldMdpAnalyzer.Analyze(doc, changed.Length));

        Assert.Equal("Sig", analysis.SignatureFieldName);
        Assert.Equal("Include", analysis.Action);
        Assert.Equal(new[] { "Name" }, analysis.Fields);
        Assert.False(analysis.IsCompliant);
        Assert.Contains(analysis.Violations, v => v.Contains("'Name'", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- helpers

    private const int NameField = 5;
    private const int DateField = 6;

    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    /// <summary>
    /// Two text fields, <c>Name</c> (5) and <c>Date</c> (6), and an empty signature field <c>Sig</c> (7)
    /// carrying <paramref name="lockEntry"/> as its <c>/Lock</c> — inline, or as object 8.
    /// </summary>
    private static byte[] FormPdf(string? lockEntry, bool indirectLock = false)
    {
        string lockValue = lockEntry == null ? "" : indirectLock ? " /Lock 8 0 R" : " /Lock " + lockEntry;

        return SignedPdfFixtures.Assemble(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 6 0 R 7 0 R] /SigFlags 3 /DA (/Helv 0 Tf 0 g) >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [5 0 R 6 0 R 7 0 R] >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /FT /Tx /T (Name) /V (Alice) /Type /Annot /Subtype /Widget /Rect [50 700 250 720] /F 4 /P 3 0 R >>",
            "<< /FT /Tx /T (Date) /V (2026-09-17) /Type /Annot /Subtype /Widget /Rect [50 660 250 680] /F 4 /P 3 0 R >>",
            "<< /FT /Sig /T (Sig) /Type /Annot /Subtype /Widget /Rect [50 600 250 640] /F 4 /P 3 0 R" + lockValue + " >>",
            lockEntry ?? "null");
    }

    private static byte[] Sign(byte[] pdf, DocMdpPermission permission = DocMdpPermission.None, string field = "Sig")
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = new MemoryStream(Keys.Value.Pfx),
            CertificatePassword = Keys.Value.Password,
            SignatureFieldName = field,
            DocMdpPermission = permission,
        });
        return output.ToArray();
    }

    /// <summary>Appends a revision giving field <paramref name="objectNumber"/> a new <c>/V</c>.</summary>
    private static byte[] ChangeValue(byte[] pdf, int objectNumber, string value)
    {
        using var doc = Open(pdf);
        var field = PdfDictionary.CloneShallow((PdfDictionary)doc.GetObject(objectNumber));
        field.Set(PdfName.V, new PdfString(value));
        return Append(pdf, doc, new IndirectObject(objectNumber, 0, field));
    }

    /// <summary>Appends a revision whose catalog's form no longer lists the <c>Name</c> field.</summary>
    private static byte[] RemoveNameField(byte[] pdf)
    {
        using var doc = Open(pdf);
        var catalog = PdfDictionary.CloneShallow(doc.Catalog.Dictionary);
        var form = PdfDictionary.CloneShallow((PdfDictionary)doc.ResolveObject(catalog[PdfName.AcroForm]));
        var fields = new PdfArray();
        foreach (var f in (PdfArray)doc.ResolveObject(form[PdfName.Fields]))
            if (((PdfReference)f).ReferenceObjectNumber != NameField) fields.Add(f);
        form.Set(PdfName.Fields, fields);
        catalog.Set(PdfName.AcroForm, form);
        return Append(pdf, doc, new IndirectObject(catalog.ObjectNumber > 0 ? catalog.ObjectNumber : 1, 0, catalog));
    }

    private static byte[] Append(byte[] pdf, PdfDocument doc, IndirectObject obj)
    {
        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteUpdate(pdf, output, new[] { obj }, new IncrementalUpdateOptions { Document = doc });
        return output.ToArray();
    }

    private static List<string> FieldMdpErrors(byte[] pdf)
    {
        using var doc = Open(pdf);
        return new PdfSignatureVerifier().VerifyAll(doc).Single(r => r.FieldName == "Sig")
            .ValidationErrors.Where(e => e.StartsWith("FieldMDP", StringComparison.Ordinal)).ToList();
    }

    private static IEnumerable<PdfDictionary> SignatureReferences(PdfDocument doc)
    {
        var signature = doc.AcroForm.GetSignatureFields().Single(f => f.HasSignature).SignatureValue!;
        if (doc.ResolveObject(signature[PdfName.Reference]) is not PdfArray references) yield break;
        foreach (var item in references)
            if (doc.ResolveObject(item) is PdfDictionary sigRef) yield return sigRef;
    }

    private static bool IsFieldMdp(PdfDocument doc, PdfDictionary sigRef)
        => (doc.ResolveObject(sigRef[PdfName.TransformMethod]) as PdfName)?.Value == "FieldMDP";

    private static PdfDictionary FieldMdpReference(PdfDocument doc)
        => Assert.Single(SignatureReferences(doc), r => IsFieldMdp(doc, r));

    private static PdfDictionary FieldMdpParameters(PdfDocument doc)
        => Assert.IsType<PdfDictionary>(FieldMdpReference(doc)[PdfName.TransformParams]);   // direct, §12.8.1
}
