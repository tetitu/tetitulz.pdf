// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.Helpers;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The catalog the LTV (<c>/DSS</c>) revision writes must reproduce the catalog the signature
/// revision left behind, because a PDF incremental update REPLACES an object rather than merging
/// into it: any entry missing from the DSS catalog is deleted from the document.
///
/// <para><b>The residual defect.</b> The DSS revision no longer writes a two-key
/// <c>&lt;&lt; /Type /Catalog /DSS n 0 R &gt;&gt;</c> stub — it carries the original catalog plus
/// the new <c>/AcroForm</c>. But <c>/Perms /DocMDP</c> is created by the <i>signature</i> revision
/// and therefore appears in neither of those two sources, so it was still being dropped. A document
/// signed with <see cref="SignatureParameters.DocMdpPermission"/> set and
/// <see cref="SignatureParameters.EmbedLtv"/> on had its certification silently removed by the very
/// revision meant to strengthen it.</para>
///
/// <para><see cref="PdfSigner.BuildSignedCatalog"/> is exercised directly because the LTV path that
/// follows it can only run against a live OCSP/CRL responder, and a test must not need the network.
/// Step 3 of <c>PdfSigner.Sign</c> writes its result as the signature revision's catalog, and the
/// <c>EmbedLtv</c> branch clones the catalog it reads back out of that output —
/// <c>SignerDssRevisionTests</c> covers that half by handing <c>EmbedDss</c> evidence directly.</para>
///
/// <para><b>Parity.</b> Those two used to be separate implementations — step 3 streamed the
/// equivalent bytes to the <c>PdfWriter</c> by hand, and a doc comment on
/// <c>BuildSignedCatalog</c> asked the next editor to keep them in step. They now share one code
/// path, and <see cref="TheSignatureRevisionsCatalogIsExactlyWhatBuildSignedCatalogReturns"/> is
/// what holds that: it signs a document and compares the catalog the signature revision actually
/// wrote against this method's output, so re-inlining a hand-rolled writer that drifts by even one
/// key fails the build.</para>
/// </summary>
public sealed class DssRevisionCatalogTests
{
    private const int SigDictObjNum = 41;
    private const int SigFieldObjNum = 42;

    [Fact]
    public void CertificationSignature_KeepsPermsDocMdpPointingAtTheSignature()
    {
        var catalog = PdfSigner.BuildSignedCatalog(
            OriginalCatalog(), new List<PdfObject>(), SigFieldObjNum, fieldWasAdopted: false,
            SigDictObjNum, DocMdpPermission.NoChanges);

        var perms = catalog[PdfName.Perms] as PdfDictionary;
        Assert.True(perms != null,
            "/Perms is absent, so the DSS revision would un-certify the document it just gave LTV.");

        var docMdp = Assert.IsType<PdfReference>(perms![PdfName.DocMDP]);
        Assert.Equal(SigDictObjNum, docMdp.ReferenceObjectNumber);
    }

    [Fact]
    public void ApprovalSignature_WritesNoPerms()
    {
        var catalog = PdfSigner.BuildSignedCatalog(
            OriginalCatalog(), new List<PdfObject>(), SigFieldObjNum, fieldWasAdopted: false,
            SigDictObjNum, DocMdpPermission.None);

        Assert.Null(catalog[PdfName.Perms]);
    }

    [Fact]
    public void OriginalCatalogEntriesAndTheNewFieldBothSurvive()
    {
        var catalog = PdfSigner.BuildSignedCatalog(
            OriginalCatalog(), new List<PdfObject> { new PdfReference(7, 0) },
            SigFieldObjNum, fieldWasAdopted: false, SigDictObjNum, DocMdpPermission.None);

        // Entries only the ORIGINAL catalog can supply — a stub would lose these.
        Assert.Equal(2, ((PdfReference)catalog[PdfName.Pages]).ReferenceObjectNumber);
        Assert.Equal(9, ((PdfReference)catalog[PdfName.Names]).ReferenceObjectNumber);

        // The signature field joins the pre-existing ones rather than replacing them.
        var fields = ((PdfDictionary)catalog[PdfName.AcroForm]).GetArray(PdfName.Fields);
        Assert.NotNull(fields);
        Assert.Equal(new[] { 7, SigFieldObjNum },
                     new[] { Ref(fields!, 0), Ref(fields, 1) });
    }

    [Fact]
    public void AnAdoptedFieldIsNotListedTwice()
    {
        // The adopted field is already among the existing entries; appending it again would make
        // /Fields name it twice.
        var catalog = PdfSigner.BuildSignedCatalog(
            OriginalCatalog(), new List<PdfObject> { new PdfReference(SigFieldObjNum, 0) },
            SigFieldObjNum, fieldWasAdopted: true, SigDictObjNum, DocMdpPermission.None);

        var fields = ((PdfDictionary)catalog[PdfName.AcroForm]).GetArray(PdfName.Fields);
        Assert.NotNull(fields);
        Assert.Single(fields!);
        Assert.Equal(SigFieldObjNum, Ref(fields, 0));
    }

    /// <summary>
    /// Signs a document and asserts that the catalog the <b>signature revision</b> wrote is,
    /// entry for entry, what <see cref="PdfSigner.BuildSignedCatalog"/> returns for the same inputs.
    ///
    /// <para>This is the guard that replaced a comment. The DSS revision supersedes the catalog
    /// object wholesale, so it must reproduce the signature revision's catalog exactly; while the
    /// two were separate implementations, nothing but prose stopped one from gaining a key the
    /// other lacked — and the <c>/Perms</c> defect this class documents is precisely that failure
    /// having already happened once.</para>
    ///
    /// <para><b>What it can and cannot catch.</b> It compares what the signer <i>wrote</i> against
    /// what this method <i>returns</i>, so any divergence introduced on the writing side fails it —
    /// a stub catalog, a dropped <c>/Perms</c>, a lost original entry, a re-inlined hand-rolled
    /// writer. A change made <i>inside</i> <c>BuildSignedCatalog</c> moves both sides together and
    /// is invisible here, which is the point rather than a hole: there is one decision about the
    /// signed catalog's keys, and it is made there. The fixture carries a pre-existing AcroForm
    /// field and asks for a certification signature, so <c>/Fields</c> merging and
    /// <c>/Perms /DocMDP</c> are both live rather than defaulted away.</para>
    ///
    /// <para>Verified falsifiable by mutation: making step 3 write the catalog with <c>/Perms</c>
    /// removed — the historical defect, reproduced — fails this on the <c>/Perms</c> text.</para>
    /// </summary>
    [Fact]
    public void TheSignatureRevisionsCatalogIsExactlyWhatBuildSignedCatalogReturns()
    {
        byte[] original = PdfWithOneFormField();
        byte[] signed = Sign(original, DocMdpPermission.NoChanges);

        using var signedMs = new MemoryStream(signed);
        using var signedDoc = PdfDocument.Open(signedMs);
        PdfDictionary written = signedDoc.Catalog.Dictionary;

        // The object numbers the signer allocated, read back out of its own output rather than
        // predicted, so the comparison cannot pass by both sides sharing a wrong guess.
        var writtenAcroForm = signedDoc.ResolveObject(written[PdfName.AcroForm]) as PdfDictionary;
        Assert.NotNull(writtenAcroForm);
        var writtenFields = writtenAcroForm!.GetArray(PdfName.Fields);
        Assert.NotNull(writtenFields);
        Assert.Equal(2, writtenFields!.Count); // the pre-existing field plus the new signature field
        int sigFieldObjNum = Ref(writtenFields, 1);
        var sigField = signedDoc.ResolveObject(writtenFields[1]) as PdfDictionary;
        Assert.NotNull(sigField);
        int sigDictObjNum = ((PdfReference)sigField![PdfName.V]).ReferenceObjectNumber;

        // Rebuild from the ORIGINAL document's own catalog, the way step 3 does.
        using var originalMs = new MemoryStream(original);
        using var originalDoc = PdfDocument.Open(originalMs);
        PdfDictionary rootObj = originalDoc.Catalog.Dictionary;
        var originalAcroForm =
            originalDoc.ResolveObject(rootObj[PdfName.AcroForm]) as PdfDictionary;
        var originalFields = originalAcroForm!.GetArray(PdfName.Fields);
        var existingFieldObjects = new List<PdfObject>();
        for (int i = 0; i < originalFields!.Count; i++) existingFieldObjects.Add(originalFields[i]);

        PdfDictionary expected = PdfSigner.BuildSignedCatalog(
            rootObj, existingFieldObjects, sigFieldObjNum, fieldWasAdopted: false,
            sigDictObjNum, DocMdpPermission.NoChanges);

        Assert.Equal(Canonical(expected), Canonical(written));
    }

    /// <summary>
    /// A dictionary rendered with its entries in key order, so the comparison above tests the
    /// catalog's CONTENT and not the enumeration order of a <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/>
    /// — which differs between a parsed object and a freshly built one for reasons that mean
    /// nothing to a PDF reader. Values go through the real <see cref="PdfWriter"/>, so a reference,
    /// a name and a number are compared exactly as they would be written to the file.
    /// </summary>
    private static string Canonical(PdfObject obj)
    {
        using var ms = new MemoryStream();
        WriteCanonical(new PdfWriter(ms), obj);
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    private static void WriteCanonical(PdfWriter writer, PdfObject obj)
    {
        switch (obj)
        {
            case PdfDictionary dict and not PdfStream:
                writer.WriteRaw("<<");
                var entries = new List<KeyValuePair<PdfName, PdfObject>>();
                foreach (var kvp in dict) entries.Add(kvp);
                entries.Sort((a, b) => string.CompareOrdinal(a.Key.Value, b.Key.Value));
                foreach (var kvp in entries)
                {
                    writer.WriteRaw(" ");
                    writer.WriteObject(kvp.Key);
                    writer.WriteRaw(" ");
                    WriteCanonical(writer, kvp.Value);
                }
                writer.WriteRaw(" >>");
                return;

            case PdfArray arr:
                writer.WriteRaw("[");
                for (int i = 0; i < arr.Count; i++)
                {
                    if (i > 0) writer.WriteRaw(" ");
                    WriteCanonical(writer, arr[i]);
                }
                writer.WriteRaw("]");
                return;

            default:
                writer.WriteObject(obj);
                return;
        }
    }

    /// <summary>A one-page PDF whose AcroForm already holds one (non-signature) field, plus the
    /// catalog entries a stub catalog was observed to destroy.</summary>
    private static byte[] PdfWithOneFormField()
    {
        var objects = new (int num, string body)[]
        {
            (1, "<< /Type /Catalog /Pages 2 0 R /Names 6 0 R " +
                "/AcroForm << /Fields [5 0 R] /SigFlags 0 >> >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Annots [5 0 R] >>"),
            (4, "<< /Length 44 >>\nstream\nBT /F1 12 Tf 72 720 Td (Content) Tj ET\nendstream"),
            (5, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Text1) /F 4 " +
                "/Rect [50 60 250 90] /P 3 0 R >>"),
            (6, "<< /Dests 7 0 R >>"),
            (7, "<< /Names [] >>"),
        };

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[objects.Length + 1];
        foreach (var (num, body) in objects)
        {
            offsets[num] = sb.Length;
            sb.Append(num).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        int xrefPos = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        for (int i = 1; i <= objects.Length; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xrefPos).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] Sign(byte[] pdf, DocMdpPermission docMdp)
    {
        var root = SigningTestCrypto.Ca("CN=Catalog Parity Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Catalog Parity Signer");
        var (pfx, password) = SigningTestCrypto.Pfx(leaf, root.Cert);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        using var cert = new MemoryStream(pfx);
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = cert,
            CertificatePassword = password,
            DocMdpPermission = docMdp,
        });
        return output.ToArray();
    }

    private static int Ref(PdfArray arr, int i) => ((PdfReference)arr[i]).ReferenceObjectNumber;

    /// <summary>A catalog with the entries a DSS stub was observed to destroy.</summary>
    private static PdfDictionary OriginalCatalog()
    {
        var root = new PdfDictionary();
        root.Set(PdfName.Type, PdfName.Catalog);
        root.Set(PdfName.Pages, new PdfReference(2, 0));
        root.Set(PdfName.Names, new PdfReference(9, 0));
        return root;
    }
}
