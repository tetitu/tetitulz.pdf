// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <see cref="SignatureSeedValue.BuildDigestMethodDictionary"/> and
/// <see cref="PdfSigningOperations.SetSeedValueDigestMethods"/> — writing a Table 234 seed-value
/// dictionary's <c>/DigestMethod</c> entry onto a signature field.
/// </summary>
public class SignatureSeedValueTests
{
    // ------------------------------------------------------------------ dictionary shape

    [Fact]
    public void BuildDigestMethodDictionary_OptionalByDefault_HasNoFfEntry()
    {
        var dict = SignatureSeedValue.BuildDigestMethodDictionary(new[] { PdfDigestOids.Sha256Name });

        Assert.Equal("SV", dict.GetName(PdfName.Type)?.Value);
        Assert.Null(dict[PdfName.Ff]);
    }

    [Fact]
    public void BuildDigestMethodDictionary_Required_SetsFfBit7()
    {
        var dict = SignatureSeedValue.BuildDigestMethodDictionary(
            new[] { PdfDigestOids.Sha256Name }, required: true);

        Assert.Equal(64, (dict[PdfName.Ff] as PdfNumber)?.IntValue);
    }

    [Fact]
    public void BuildDigestMethodDictionary_PreservesNameOrder()
    {
        var dict = SignatureSeedValue.BuildDigestMethodDictionary(
            new[] { PdfDigestOids.Sha384Name, PdfDigestOids.Sha256Name });

        var names = (dict[PdfName.DigestMethod] as PdfArray)
            .Select(o => (o as PdfName)?.Value).ToArray();
        Assert.Equal(new[] { "SHA-384", "SHA-256" }, names);
    }

    [Fact]
    public void BuildDigestMethodDictionary_EmptyList_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            SignatureSeedValue.BuildDigestMethodDictionary(Array.Empty<string>()));
    }

    [Fact]
    public void BuildDigestMethodDictionary_NullEntry_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            SignatureSeedValue.BuildDigestMethodDictionary(new string[] { null }));
    }

    // ------------------------------------------------------------------ writing onto a document

    [Fact]
    public void SetSeedValueDigestMethods_WritesSvOnTheNamedField()
    {
        using var input = new MemoryStream(FormPdf("Signature1"));
        using var output = new MemoryStream();

        PdfSigningOperations.SetSeedValueDigestMethods(
            input, output, "Signature1", new[] { PdfDigestOids.Sha256Name, PdfDigestOids.Sha384Name });

        var sv = ReadSeedValue(output, "Signature1");
        Assert.NotNull(sv);
        Assert.Equal("SV", sv.GetName(PdfName.Type)?.Value);
        var names = (sv[PdfName.DigestMethod] as PdfArray)
            .Select(o => (o as PdfName)?.Value).ToArray();
        Assert.Equal(new[] { "SHA-256", "SHA-384" }, names);
        Assert.Null(sv[PdfName.Ff]);
    }

    [Fact]
    public void SetSeedValueDigestMethods_Required_SetsFfBitOnTheWrittenDictionary()
    {
        using var input = new MemoryStream(FormPdf("Signature1"));
        using var output = new MemoryStream();

        PdfSigningOperations.SetSeedValueDigestMethods(
            input, output, "Signature1", new[] { PdfDigestOids.Sha256Name }, required: true);

        var sv = ReadSeedValue(output, "Signature1");
        Assert.Equal(64, (sv[PdfName.Ff] as PdfNumber)?.IntValue);
    }

    /// <summary>The original field dictionary's own entries (e.g. /FT, /T) survive the edit —
    /// this writes a modified COPY of the field, not a fresh one that drops everything else.</summary>
    [Fact]
    public void SetSeedValueDigestMethods_PreservesTheFieldsOtherEntries()
    {
        using var input = new MemoryStream(FormPdf("Signature1"));
        using var output = new MemoryStream();

        PdfSigningOperations.SetSeedValueDigestMethods(
            input, output, "Signature1", new[] { PdfDigestOids.Sha256Name });

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        var field = doc.AcroForm.GetSignatureFields().Single(f => f.FullyQualifiedName == "Signature1");
        Assert.Equal(Core.Forms.PdfFieldType.Signature, field.FieldType);
        Assert.Equal("Signature1", field.FullyQualifiedName);
    }

    [Fact]
    public void SetSeedValueDigestMethods_UnknownFieldName_Throws()
    {
        using var input = new MemoryStream(FormPdf("Signature1"));
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfSigningException>(() =>
            PdfSigningOperations.SetSeedValueDigestMethods(
                input, output, "NoSuchField", new[] { PdfDigestOids.Sha256Name }));
        Assert.Contains("NoSuchField", ex.Message);
    }

    [Fact]
    public void SetSeedValueDigestMethods_NoAcroForm_Throws()
    {
        using var input = new MemoryStream(NoFormPdf());
        using var output = new MemoryStream();

        Assert.Throws<PdfSigningException>(() =>
            PdfSigningOperations.SetSeedValueDigestMethods(
                input, output, "Signature1", new[] { PdfDigestOids.Sha256Name }));
    }

    [Fact]
    public void SetSeedValueDigestMethods_EncryptedDocument_ThrowsAndSaysSo()
    {
        using var input = new MemoryStream(EncryptedPdf());
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfSigningException>(() =>
            PdfSigningOperations.SetSeedValueDigestMethods(
                input, output, "Signature1", new[] { PdfDigestOids.Sha256Name }));
        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ fixtures

    private const string N = "\n";

    /// <summary>
    /// Reads <c>/SV</c> straight off the AcroForm field dictionary through public APIs only —
    /// <c>PdfFormField.Dictionary</c> is internal to Core and not visible from this test project,
    /// so this walks <c>/AcroForm /Fields</c> itself rather than going through the wrapper.
    /// </summary>
    private static PdfDictionary ReadSeedValue(MemoryStream output, string fieldName)
    {
        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        var acroForm = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        var fields = doc.ResolveObject(acroForm[PdfName.Fields]) as PdfArray;
        foreach (var raw in fields)
        {
            var fieldDict = doc.ResolveObject(raw) as PdfDictionary;
            if (fieldDict?.GetString(PdfName.T)?.GetText() != fieldName) continue;
            return doc.ResolveObject(fieldDict[PdfName.SV]) as PdfDictionary;
        }
        return null;
    }

    /// <summary>A document whose /AcroForm carries one top-level /Sig field.</summary>
    private static byte[] FormPdf(string fieldName)
    {
        var b = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string s) { offsets.Add(b.Length); b.Append(s); }

        b.Append("%PDF-1.7" + N);
        Obj("1 0 obj" + N + "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>" + N + "endobj" + N);
        Obj("2 0 obj" + N + "<< /Type /Pages /Kids [3 0 R] /Count 1 >>" + N + "endobj" + N);
        Obj("3 0 obj" + N + "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>" + N + "endobj" + N);
        Obj("4 0 obj" + N
            + $"<< /Type /Annot /Subtype /Widget /FT /Sig /T ({fieldName}) /Rect [0 0 100 50] >>"
            + N + "endobj" + N);
        return Finish(b, offsets);
    }

    private static byte[] NoFormPdf()
    {
        var b = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string s) { offsets.Add(b.Length); b.Append(s); }
        b.Append("%PDF-1.7" + N);
        Obj("1 0 obj" + N + "<< /Type /Catalog /Pages 2 0 R >>" + N + "endobj" + N);
        Obj("2 0 obj" + N + "<< /Type /Pages /Kids [3 0 R] /Count 1 >>" + N + "endobj" + N);
        Obj("3 0 obj" + N + "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>" + N + "endobj" + N);
        return Finish(b, offsets);
    }

    private static byte[] EncryptedPdf()
    {
        var b = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string s) { offsets.Add(b.Length); b.Append(s); }

        string hex = new string('a', 64);

        b.Append("%PDF-1.7" + N);
        Obj("1 0 obj" + N + "<< /Type /Catalog /Pages 2 0 R >>" + N + "endobj" + N);
        Obj("2 0 obj" + N + "<< /Type /Pages /Kids [3 0 R] /Count 1 >>" + N + "endobj" + N);
        Obj("3 0 obj" + N + "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>" + N + "endobj" + N);
        Obj($"4 0 obj" + N + $"<< /Filter /Standard /V 2 /R 3 /Length 128 /P -1 /O <{hex}> /U <{hex}> >>" + N + "endobj" + N);

        int xref = b.Length;
        b.Append("xref" + N + "0 5" + N + "0000000000 65535 f " + N);
        foreach (int off in offsets) b.Append(off.ToString("D10")).Append(" 00000 n " + N);
        b.Append("trailer" + N + "<< /Size 5 /Root 1 0 R /Encrypt 4 0 R "
            + "/ID [<0102030405060708090a0b0c0d0e0f10> <0102030405060708090a0b0c0d0e0f10>] >>" + N
            + "startxref" + N + xref + N + "%%EOF" + N);
        return Encoding.ASCII.GetBytes(b.ToString());
    }

    private static byte[] Finish(StringBuilder b, List<int> offsets)
    {
        int count = offsets.Count + 1;
        int xref = b.Length;
        b.Append($"xref\n0 {count}\n0000000000 65535 f \n");
        foreach (int o in offsets) b.Append(o.ToString("D10")).Append(" 00000 n \n");
        b.Append($"trailer\n<< /Size {count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(b.ToString());
    }

    /// <summary>
    /// A field name that is empty is refused as an argument, before the document is read.
    /// </summary>
    [Fact]
    public void SetSeedValue_WithAnEmptyFieldName_IsRefusedAsAnArgument()
    {
        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();

        Assert.Throws<ArgumentException>(() =>
            PdfSigningOperations.SetSeedValueDigestMethods(input, output, "", new[] { "SHA256" }));
    }
}
