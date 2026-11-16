// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.Helpers;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// When <see cref="SignatureParameters.SignatureFieldName"/> names an existing but still EMPTY
/// signature field, <see cref="PdfSigner"/> signs THAT field rather than inventing a second one.
///
/// <para><b>The defect.</b> <see cref="VisualSignatureBuilder"/> lays down a widget carrying the
/// stamp's <c>/Rect</c> and <c>/AP</c> under a generated <c>/T</c>, then hands the file to
/// <c>PdfSigner</c> with the same field name. The signer read that name as a <i>collision</i>: it
/// renamed itself (<c>VisualSig…</c> → <c>VisualSig2</c>) and allocated a brand-new field with
/// <c>/Rect [0 0 0 0]</c> to hold the <c>/V</c>. The output opened as one invisible valid signature
/// plus an empty, clickable, permanently UNSIGNED field displaying the stamp — the stamp was
/// decorative and the signature it advertised had no visual representation at all, while the class
/// doc promised "a visible signature stamp".</para>
///
/// <para>A field that is already SIGNED is still a genuine collision, so
/// <see cref="SignedFieldOfTheSameName_IsStillRenamedAround"/> pins that an existing signature can
/// never be overwritten by this path.</para>
/// </summary>
public sealed class VisibleSignatureFieldAdoptionTests
{
    private const string FieldName = "VisualSig1";

    [Fact]
    public void EmptyVisibleField_IsAdopted_SoTheStampedFieldIsTheOneThatCarriesTheSignature()
    {
        byte[] signed = Sign(PdfWithEmptySignatureWidget(FieldName), FieldName);

        using var ms = new MemoryStream(signed);
        using var doc = PdfDocument.Open(ms);

        // Exactly one signature field, and it is the stamped one.
        var fields = doc.AcroForm!.GetSignatureFields().ToList();
        Assert.True(fields.Count == 1,
            "Expected the stamped field to be adopted, so the document carries ONE signature " +
            $"field. Found {fields.Count}: {string.Join(", ", fields.Select(f => f.FieldName))}.");
        Assert.Equal(FieldName, fields[0].FieldName);
        Assert.True(fields[0].HasSignature);

        // The signed field is the visible one: its /Rect is the stamp rectangle, not [0 0 0 0],
        // and its appearance stream survived being superseded.
        var widget = SoleAcroFormField(doc);
        Assert.NotNull(widget[PdfName.V]);
        var rect = widget.GetArray(PdfName.Rect);
        Assert.NotNull(rect);
        Assert.Equal(new[] { 50d, 60d, 250d, 140d },
                     Enumerable.Range(0, 4).Select(i => rect!.GetDouble(i)).ToArray());
        Assert.NotNull(doc.ResolveObject(widget[PdfName.AP]) as PdfDictionary);

        // And it really verifies — adoption must not disturb the byte ranges.
        var result = new PdfSignatureVerifier().Verify(doc, FieldName);
        Assert.True(result.IsIntegrityValid,
            "Signature over the adopted field failed integrity: " +
            string.Join("; ", result.ValidationErrors));
    }

    [Fact]
    public void SignedFieldOfTheSameName_IsStillRenamedAround()
    {
        // Sign once (adopting the empty widget), then sign again with the same requested name.
        byte[] once = Sign(PdfWithEmptySignatureWidget(FieldName), FieldName);
        byte[] twice = Sign(once, FieldName);

        using var ms = new MemoryStream(twice);
        using var doc = PdfDocument.Open(ms);

        var names = doc.AcroForm!.GetSignatureFields().Select(f => f.FieldName).ToList();
        Assert.Equal(2, names.Count);
        Assert.Contains(FieldName, names);
        Assert.Contains(names, n => n != FieldName);

        // The first signature must be intact — an already-signed field is never superseded.
        var first = new PdfSignatureVerifier().Verify(doc, FieldName);
        Assert.True(first.IsIntegrityValid,
            "The pre-existing signature was destroyed: " + string.Join("; ", first.ValidationErrors));
    }

    /// <summary>
    /// The single entry of the output's <c>/AcroForm /Fields</c>, resolved. Reads the raw object
    /// graph rather than <c>PdfSignatureField</c>, whose dictionary accessor is internal to Core.
    /// </summary>
    private static PdfDictionary SoleAcroFormField(PdfDocument doc)
    {
        var acro = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        Assert.NotNull(acro);
        var arr = acro!.GetArray(PdfName.Fields);
        Assert.NotNull(arr);
        Assert.True(arr!.Count == 1,
            $"/AcroForm /Fields should list the adopted field once; it lists {arr.Count} entries.");
        var dict = doc.ResolveObject(arr[0]) as PdfDictionary;
        Assert.NotNull(dict);
        return dict!;
    }

    // ---------------------------------------------------------------- fixture

    /// <summary>
    /// A one-page PDF whose AcroForm holds a single EMPTY signature field: a widget with a real
    /// <c>/Rect</c> and an <c>/AP /N</c> appearance and no <c>/V</c> — the shape
    /// <see cref="VisualSignatureBuilder"/> writes before handing the file to the signer.
    /// </summary>
    private static byte[] PdfWithEmptySignatureWidget(string fieldName)
    {
        var objects = new (int num, string body)[]
        {
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /SigFlags 3 >> >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Annots [5 0 R] >>"),
            (4, "<< /Length 44 >>\nstream\nBT /F1 12 Tf 72 720 Td (Stamped) Tj ET\nendstream"),
            (5, $"<< /Type /Annot /Subtype /Widget /FT /Sig /T ({fieldName}) /F 132 " +
                "/Rect [50 60 250 140] /P 3 0 R /AP << /N 6 0 R >> >>"),
            (6, "<< /Type /XObject /Subtype /Form /BBox [0 0 200 80] /Length 32 >>\n" +
                "stream\n1 0 0 RG 2 w 5 5 190 70 re S\nendstream"),
        };

        var sb = new System.Text.StringBuilder();
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

        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] Sign(byte[] pdf, string fieldName)
    {
        var root = SigningTestCrypto.Ca("CN=Adoption Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Adoption Signer");
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
}
