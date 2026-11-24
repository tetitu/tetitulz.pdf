// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// Two properties of the revision <see cref="PdfSigner"/> writes: the catalog must stay a legal
/// dictionary, and a new signature field must not reuse a name already in the form.
/// </summary>
public sealed class SignerCatalogAndFieldNameTests
{
    /// <summary>
    /// Certifying a document that already carries a catalog <c>/Perms</c> — a Reader-extended
    /// (usage-rights) file, or one certified by another tool — emits the key once.
    /// §7.3.7 leaves a duplicate key undefined, so readers differ on which wins and the DocMDP the
    /// signer meant to certify with may be the one discarded.
    /// </summary>
    [Fact]
    public void CertifyingADocumentThatAlreadyHasPerms_WritesTheKeyOnce()
    {
        byte[] signed = Sign(PdfWithCatalogPerms(), docMdp: DocMdpPermission.NoChanges);

        string catalog = CatalogObjectText(signed);

        Assert.Equal(1, CountOccurrences(catalog, "/Perms"));
        Assert.Contains("/DocMDP", catalog);
    }

    /// <summary>
    /// And the merge keeps what the document already granted: a co-existing <c>/UR3</c> entry must
    /// survive the certification rather than being replaced wholesale.
    /// </summary>
    [Fact]
    public void CertifyingADocumentThatAlreadyHasPerms_KeepsItsOtherEntries()
    {
        byte[] signed = Sign(PdfWithCatalogPerms(), docMdp: DocMdpPermission.NoChanges);

        Assert.Contains("/UR3", CatalogObjectText(signed));
    }

    /// <summary>
    /// A signature field nested under a parent field node is a shape Core's AcroForm reader and the
    /// verifier both handle, but the signer's collision check read only the top level of
    /// <c>/Fields</c> — so it saw no names, renamed nothing, and wrote a second field with the same
    /// <c>/T</c>. §12.7.3.1 requires fully-qualified field names to be unique.
    /// </summary>
    [Fact]
    public void SigningIntoAFormWhoseFieldNameIsNested_DoesNotDuplicateThatName()
    {
        byte[] signed = Sign(PdfWithNestedSignatureField(), fieldName: "Signature1");

        List<string> names = FieldNames(signed);

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(names, n => n != "Signature1");   // the new field really was renamed
    }

    // ---------------------------------------------------------------- helpers

    private static byte[] Sign(
        byte[] pdf, DocMdpPermission docMdp = DocMdpPermission.None, string fieldName = "Signature1")
    {
        var root = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(root);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, root.Cert);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = new MemoryStream(pfx),
            CertificatePassword = pw,
            SignatureFieldName = fieldName,
            DocMdpPermission = docMdp,
        });
        return output.ToArray();
    }

    /// <summary>The text of the LAST revision's catalog object, which is the one the signer wrote.</summary>
    private static string CatalogObjectText(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int at = text.LastIndexOf("1 0 obj", StringComparison.Ordinal);
        Assert.True(at > 0, "the fixture's catalog is not object 1");
        int end = text.IndexOf("endobj", at, StringComparison.Ordinal);
        return text.Substring(at, end - at);
    }

    /// <summary>Every /T in the signed output's AcroForm, walking /Kids.</summary>
    private static List<string> FieldNames(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var names = new List<string>();
        var acro = doc.Catalog!.Dictionary[PdfName.AcroForm];
        var acroDict = doc.ResolveObject(acro) as PdfDictionary;
        Assert.NotNull(acroDict);
        Walk(doc, acroDict!.GetArray(PdfName.Fields), names, 0);
        return names;
    }

    private static void Walk(PdfDocument doc, PdfArray? fields, List<string> names, int depth)
    {
        if (fields == null || depth > 8) return;
        for (int i = 0; i < fields.Count; i++)
        {
            if (doc.ResolveObject(fields[i]) is not PdfDictionary field) continue;
            string? t = field.GetString(PdfName.T)?.GetText();
            if (t != null) names.Add(t);
            Walk(doc, field.GetArray(PdfName.Kids), names, depth + 1);
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
        return count;
    }

    /// <summary>A document whose catalog already carries /Perms with a /UR3 entry.</summary>
    private static byte[] PdfWithCatalogPerms() => BuildPdf(
        catalogExtra: " /Perms << /UR3 5 0 R >>",
        extraObjects: "5 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite >>\nendobj\n",
        acroForm: null);

    /// <summary>A document whose only signature field is a kid of a parent field node.</summary>
    private static byte[] PdfWithNestedSignatureField() => BuildPdf(
        catalogExtra: " /AcroForm << /Fields [5 0 R] /SigFlags 3 >>",
        extraObjects:
            "5 0 obj\n<< /T (Parent) /Kids [6 0 R] >>\nendobj\n"
          + "6 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1) /Rect [0 0 0 0] >>\nendobj\n",
        acroForm: null);

    private static byte[] BuildPdf(string catalogExtra, string extraObjects, string? acroForm)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj($"1 0 obj\n<< /Type /Catalog /Pages 2 0 R{catalogExtra}{acroForm} >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\nendobj\n");
        Obj("4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n");

        foreach (string body in SplitObjects(extraObjects))
            Obj(body);

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static IEnumerable<string> SplitObjects(string objects)
    {
        int at = 0;
        while (at < objects.Length)
        {
            int end = objects.IndexOf("endobj\n", at, StringComparison.Ordinal);
            if (end < 0) yield break;
            yield return objects.Substring(at, end - at + "endobj\n".Length);
            at = end + "endobj\n".Length;
        }
    }
}
