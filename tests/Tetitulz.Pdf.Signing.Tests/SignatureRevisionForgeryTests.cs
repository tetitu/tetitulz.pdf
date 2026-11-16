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
/// What a revision appended after a genuine signature may not change, behind a self-signed decoy that
/// reaches the end of the file so the trailing-bytes error says nothing: each forged file names what
/// it changes, and the signature that signed the old version has to refuse it.
/// </summary>
public class SignatureRevisionForgeryTests
{
    private const string GenuineField = "Signature1";

    private static (byte[] Signed, Org.BouncyCastle.X509.X509Certificate Anchor) Genuine(byte[] pdf = null)
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);
        return (SignedPdfFixtures.Sign(pdf ?? SignedPdfFixtures.MinimalPdf(), pfx, pw, GenuineField), ca.Cert);
    }

    private static byte[] WithDecoy(byte[] pdf)
    {
        var attacker = SigningTestCrypto.SelfSignedLeaf("CN=Attacker");
        var (pfx, pw) = SigningTestCrypto.Pfx(attacker);
        return SignedPdfFixtures.Sign(pdf, pfx, pw, "Decoy");
    }

    private static SignatureVerificationResult VerifyGenuine(byte[] pdf, Org.BouncyCastle.X509.X509Certificate anchor)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var results = new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions { TrustAnchors = new[] { anchor } });
        Assert.NotEmpty(results);
        // A reported name a forger can change is no way to find the signature: its CMS has one signer.
        return results.First(r => r.SignerCommonName != "Attacker");
    }

    /// <summary>The text of an object as the file's last revision holds it, however it was written.</summary>
    private static string ObjectText(byte[] pdf, int number)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int at = text.LastIndexOf($"\n{number} 0 obj", StringComparison.Ordinal);
        Assert.True(at >= 0, $"object {number} not found");
        int end = text.IndexOf("endobj", at, StringComparison.Ordinal);
        return text.Substring(at + 1, end - at - 1);
    }

    private static byte[] Append(byte[] pdf, params (int Number, string Object)[] revision)
    {
        long prev = SignedPdfFixtures.FindStartXref(pdf);
        var body = new StringBuilder();
        var table = new StringBuilder("xref\n");
        foreach (var (number, obj) in revision)
        {
            long offset = pdf.Length + body.Length;
            body.Append(obj).Append("\nendobj\n");
            table.Append(number).Append(" 1\n").AppendFormat("{0:D10} 00000 n \n", offset);
        }
        long xref = pdf.Length + body.Length;
        body.Append(table).Append($"trailer\n<< /Size 64 /Root 1 0 R /Prev {prev} >>\nstartxref\n{xref}\n%%EOF\n");
        return Concat(pdf, Encoding.Latin1.GetBytes(body.ToString()));
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var all = new byte[a.Length + b.Length];
        Array.Copy(a, all, a.Length);
        Array.Copy(b, 0, all, a.Length, b.Length);
        return all;
    }

    private static int SignatureObject(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int at = text.IndexOf("/ByteRange", StringComparison.Ordinal);
        int start = text.LastIndexOf(" 0 obj", at, StringComparison.Ordinal);
        int line = text.LastIndexOf('\n', start) + 1;
        return int.Parse(text.Substring(line, start - line));
    }

    private static int FieldObject(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int at = text.IndexOf("(" + GenuineField + ")", StringComparison.Ordinal);
        int start = text.LastIndexOf(" 0 obj", at, StringComparison.Ordinal);
        int line = text.LastIndexOf('\n', start) + 1;
        return int.Parse(text.Substring(line, start - line));
    }

    /// <summary>
    /// A file with bytes ahead of its header: a table's offsets count from the header, a
    /// <c>/ByteRange</c>'s from the file's start, and a genuine signature stands in its own bytes all the same.
    /// </summary>
    [Fact]
    public void A_genuine_signature_on_a_file_with_bytes_before_its_header_stays_valid()
    {
        byte[] prefixed = Concat(Encoding.ASCII.GetBytes("some mail gateway's preamble, long enough to matter\n"), SignedPdfFixtures.MinimalPdf());
        var (signed, anchor) = Genuine(prefixed);

        SignatureVerificationResult genuine = VerifyGenuine(signed, anchor);

        Assert.True(genuine.IsValid, string.Join("; ", genuine.ValidationErrors));
    }

    [Fact]
    public void Control_APlainDecoyAloneLeavesTheGenuineSignatureValid()
    {
        var (signed, anchor) = Genuine();
        Assert.True(VerifyGenuine(WithDecoy(signed), anchor).IsValid);
    }

    [Theory]
    [InlineData("/Reason (Forged reason)")]
    [InlineData("/Name (Someone Else)")]
    [InlineData("/Location (Elsewhere)")]
    [InlineData("/M (D:19990101000000Z)")]
    public void AdversarialRewrittenSignatureDictionary_IsRefused(string entry)
    {
        var (signed, anchor) = Genuine();
        int number = SignatureObject(signed);
        string forged = ObjectText(signed, number).Replace("/Type /Sig", "/Type /Sig " + entry);
        Assert.Contains(entry, forged, StringComparison.Ordinal);

        byte[] attacked = WithDecoy(Append(signed, (number, forged)));

        Assert.False(VerifyGenuine(attacked, anchor).IsValid);
    }

    [Theory]
    [InlineData("/AP << /N 61 0 R >>")]
    [InlineData("/AS /Forged")]
    [InlineData("/DV (x)")]
    public void AdversarialSignedFieldsAppearanceRedrawn_IsRefused(string keys)
    {
        var (signed, anchor) = Genuine();
        int field = FieldObject(signed);
        string forged = ObjectText(signed, field);
        forged = forged.Substring(0, forged.LastIndexOf(">>", StringComparison.Ordinal)) + " " + keys + " >>";
        const string picture = "BT /F1 12 Tf 10 10 Td (Signed by the CEO) Tj ET";
        byte[] attacked = WithDecoy(Append(signed, (field, forged),
            (61, $"61 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 50] /Length {picture.Length} >>\nstream\n{picture}\nendstream")));

        SignatureVerificationResult genuine = VerifyGenuine(attacked, anchor);

        Assert.False(genuine.IsValid);
        Assert.Contains(genuine.ValidationErrors, e => e.Contains("a signed field changed", StringComparison.Ordinal));
    }

    [Fact]
    public void AdversarialWidgetOfASignedField_Redrawn_IsRefused()
    {
        // A field holding its signature, with its widget a kid of its own (§12.7.3.1): the value is one
        // node up from the object rewritten, which names no /FT or /T of its own.
        // The signed revision: a form whose signature field's widget is object 7.
        byte[] pdf = SignedPdfFixtures.Assemble(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] /SigFlags 3 >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [7 0 R] >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /FT /Sig /T (Kidded) /Kids [7 0 R] /V 8 0 R >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [0 0 10 10] /F 4 >>",
            "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 10 20 30] /Contents <00> >>");

        string annotation = ObjectText(pdf, 7).Replace("/F 4", "/F 4 /AP << /N 5 0 R >>");
        byte[] rewritten = Append(pdf, (7, annotation));

        using var doc = PdfDocument.Open(new MemoryStream(rewritten));
        DocMdpAnalysis analysis = DocMdpAnalyzer.AnalyzeChangesSince(doc, doc, pdf.Length, rewritten.Length, 3, null);

        Assert.Contains(analysis.Violations, v => v.Contains("a signed field changed", StringComparison.Ordinal));
    }

    /// <summary>
    /// A genuine signature's CMS and byte range copied into a new field, the words beside them the
    /// forger's: the digest matches, since the dictionary it reads them from is not among the bytes signed.
    /// </summary>
    [Theory]
    [InlineData("own object")]
    [InlineData("inline in its field")]
    public void AdversarialSignatureCopiedIntoANewField_IsRefused(string shape)
    {
        var (signed, anchor) = Genuine();
        int number = SignatureObject(signed);
        string dictionary = ObjectText(signed, number);
        dictionary = dictionary.Substring(dictionary.IndexOf("<<", StringComparison.Ordinal));
        dictionary = dictionary.Replace("/Type /Sig", "/Type /Sig /Reason (Approves everything below) /Name (The Board)");
        dictionary = dictionary.TrimEnd();

        string catalog = ObjectText(signed, 1);
        string page = ObjectText(signed, 3);
        Assert.Contains("/Fields [", catalog + ObjectText(signed, 1), StringComparison.Ordinal);
        catalog = catalog.Replace("/Fields [", "/Fields [61 0 R ");

        string value = shape == "own object" ? "62 0 R" : dictionary;
        var revision = new List<(int, string)>
        {
            (1, catalog),
            (61, $"61 0 obj\n<< /FT /Sig /T (ForgedApproval) /Type /Annot /Subtype /Widget /Rect [0 0 0 0] /F 2 /V {value} >>"),
        };
        if (shape == "own object") revision.Add((62, "62 0 obj\n" + dictionary));

        byte[] attacked = WithDecoy(Append(signed, revision.ToArray()));

        using var doc = PdfDocument.Open(new MemoryStream(attacked));
        var results = new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions { TrustAnchors = new[] { anchor } });

        SignatureVerificationResult forged = results.Single(r => r.FieldName == "ForgedApproval");
        Assert.False(forged.IsValid, "a copied signature reported valid: " + forged.SignerCommonName);
        Assert.Contains(forged.ValidationErrors, e => e.Contains("not among the bytes", StringComparison.Ordinal));

        // The signature it was copied from stands: nothing about it changed.
        Assert.True(results.Single(r => r.FieldName == GenuineField).IsValid);
    }

    [Fact]
    public void AdversarialGenuineSignatureNamedByASecondField_IsRefused()
    {
        var (signed, anchor) = Genuine();
        int number = SignatureObject(signed);
        string catalog = ObjectText(signed, 1).Replace("/Fields [", "/Fields [61 0 R ");

        byte[] attacked = WithDecoy(Append(signed, (1, catalog),
            (61, $"61 0 obj\n<< /FT /Sig /T (ApprovedByTheBoard) /Type /Annot /Subtype /Widget /Rect [0 0 0 0] /F 2 /V {number} 0 R >>")));

        using var doc = PdfDocument.Open(new MemoryStream(attacked));
        var results = new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions { TrustAnchors = new[] { anchor } });

        Assert.False(results.Single(r => r.FieldName == "ApprovedByTheBoard").IsValid);
    }

    [Fact]
    public void AdversarialCopyNamingAKinderLevel_DoesNotReplaceTheCertification()
    {
        // Two signature values over the same bytes, the kinder listed first: the stricter is the certification.
        static string Value(int p) =>
            $"<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 10 20 30] /Contents <00> " +
            $"/Reference [<< /Type /SigRef /TransformMethod /DocMDP /TransformParams << /Type /TransformParams /P {p} /V /1.2 >> >>] >>";

        byte[] pdf = SignedPdfFixtures.Assemble(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R] /SigFlags 3 >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /FT /Sig /T (Copy) /V 6 0 R >>",
            "<< /FT /Sig /T (Genuine) /V 7 0 R >>",
            Value(3), Value(1));

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.Equal(1, DocMdpAnalyzer.Analyze(doc, pdf.Length).PermissionLevel);
    }

    [Fact]
    public void AdversarialFieldRenamed_IsRefused()
    {
        var (signed, anchor) = Genuine();
        int field = FieldObject(signed);
        string forged = ObjectText(signed, field).Replace(GenuineField, "ApprovedByTheBoard");
        byte[] attacked = WithDecoy(Append(signed, (field, forged)));

        Assert.False(VerifyGenuine(attacked, anchor).IsValid);
    }

    [Fact]
    public void AdversarialContentStreamFreed_IsRefused()
    {
        var (signed, anchor) = Genuine();
        long prev = SignedPdfFixtures.FindStartXref(signed);
        string tail = $"xref\n4 1\n0000000000 00001 f \ntrailer\n<< /Size 64 /Root 1 0 R /Prev {prev} >>\nstartxref\n{signed.Length}\n%%EOF\n";

        byte[] attacked = WithDecoy(Concat(signed, Encoding.Latin1.GetBytes(tail)));

        Assert.False(VerifyGenuine(attacked, anchor).IsValid);
    }

    /// <summary>A form holding a choice field (7), with a font in its default resources and a second font (8) the file already holds.</summary>
    private static byte[] FormPdf()
    {
        const string content = "BT /F1 12 Tf 72 700 Td (signed content) Tj ET\n";
        return SignedPdfFixtures.Assemble(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> /Annots [7 0 R] >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Fields [7 0 R] /DR << /Font << /F1 5 0 R >> >> /DA (/F1 12 Tf 0 g) >>",
            "<< /Type /Annot /Subtype /Widget /FT /Ch /T (Choice) /Rect [10 10 90 30] /Opt [(Approve) (Reject)] /V (Approve) >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>");
    }

    /// <summary>The form rewritten where the signer left it: in the catalog, or its own object.</summary>
    private static byte[] WithFormRewritten(byte[] signed, string from, string to)
    {
        string catalog = ObjectText(signed, 1);
        if (catalog.Contains(from, StringComparison.Ordinal)) return Append(signed, (1, catalog.Replace(from, to)));

        string form = ObjectText(signed, 6);
        Assert.Contains(from, form, StringComparison.Ordinal);
        return Append(signed, (6, form.Replace(from, to)));
    }

    [Theory]
    [InlineData("/Font << /F1 5 0 R >>", "/Font << /F1 8 0 R >>")]   // the font a redrawn value is drawn in
    [InlineData("/Font << /F1 5 0 R >>", "/Font << >>")]
    [InlineData("/DR << /Font << /F1 5 0 R >> >>", "")]
    [InlineData("/DR << /Font << /F1 5 0 R >> >>", "/DR << >>")]
    [InlineData("/DA (/F1 12 Tf 0 g)", "/DA (/F1 40 Tf 1 g)")]
    [InlineData("/DA (/F1 12 Tf 0 g)", "/XFA 8 0 R /DA (/F1 12 Tf 0 g)")]
    public void AdversarialFormDictionaryChanged_IsRefused(string from, string to)
    {
        var (signed, anchor) = Genuine(FormPdf());
        byte[] attacked = WithDecoy(WithFormRewritten(signed, from, to));

        SignatureVerificationResult genuine = VerifyGenuine(attacked, anchor);

        Assert.False(genuine.IsValid);
        Assert.Contains(genuine.ValidationErrors, e => e.Contains("AcroForm", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/Font << /F1 5 0 R >>", "/Font << /F1 5 0 R /F2 8 0 R >>")]
    [InlineData("/DA (/F1 12 Tf 0 g)", "/NeedAppearances true /DA (/F1 12 Tf 0 g)")]
    public void AFormGainingAFontOrAskingForAppearances_IsPermittedAndSaid(string from, string to)
    {
        var (signed, anchor) = Genuine(FormPdf());
        byte[] added = WithDecoy(WithFormRewritten(signed, from, to));

        SignatureVerificationResult genuine = VerifyGenuine(added, anchor);

        Assert.True(genuine.IsValid, string.Join("; ", genuine.ValidationErrors));
        Assert.Contains(genuine.Warnings, w => w.Contains("AcroForm", StringComparison.Ordinal));
    }

    [Fact]
    public void AdversarialChoiceOptionsRewritten_IsRefused()
    {
        var (signed, anchor) = Genuine(FormPdf());
        string field = ObjectText(signed, 7);
        byte[] attacked = WithDecoy(Append(signed, (7, field.Replace("(Reject)", "(Approved by the board)"))));

        SignatureVerificationResult genuine = VerifyGenuine(attacked, anchor);

        Assert.False(genuine.IsValid);
        Assert.Contains(genuine.ValidationErrors, e => e.Contains("Opt", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/SigFlags 0", false)]
    [InlineData("/SigFlags 1", false)]
    [InlineData("", false)]
    [InlineData("/SigFlags 3", true)]
    public void ASignatureFlagKept_StaysPermitted_ACleared_IsRefused(string flags, bool valid)
    {
        var (signed, anchor) = Genuine(FormPdf());
        string catalog = ObjectText(signed, 1);
        var match = System.Text.RegularExpressions.Regex.Match(catalog, @"/SigFlags\s+\d+");
        Assert.True(match.Success, catalog);
        byte[] rewritten = Append(signed, (1, catalog.Replace(match.Value, flags + " /NeedAppearances true")));

        // The decoy would set the flags again, so the revision is read as it stands, its trailing bytes aside.
        using var doc = PdfDocument.Open(new MemoryStream(rewritten));
        var results = new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions { TrustAnchors = new[] { anchor } });
        SignatureVerificationResult genuine = results.Single(r => r.FieldName == GenuineField);

        bool refused = genuine.ValidationErrors.Any(e => e.Contains("AcroForm", StringComparison.Ordinal));
        Assert.Equal(!valid, refused);
    }

    [Fact]
    public void AFieldFilledAfterSigning_StaysPermitted()
    {
        var (signed, anchor) = Genuine(FormPdf());
        string field = ObjectText(signed, 7);
        byte[] filled = WithDecoy(Append(signed, (7, field.Replace("/V (Approve)", "/V (Reject)"))));

        Assert.True(VerifyGenuine(filled, anchor).IsValid);
    }

    [Theory]
    [InlineData("/OCProperties << /OCGs [] /D << /OFF [] >> >>")]
    [InlineData("/OpenAction << /S /JavaScript /JS (app.alert(1)) >>")]
    [InlineData("/AA << /WC << /S /JavaScript /JS (app.alert(1)) >> >>")]
    [InlineData("/Names << /JavaScript << /Names [(a) << /S /JavaScript /JS (app.alert(1)) >>] >> >>")]
    [InlineData("/PageLayout /TwoColumnLeft /Dests << /a [3 0 R /Fit] >>")]
    public void AdversarialCatalogEntryAdded_IsRefused(string entry)
    {
        var (signed, anchor) = Genuine();
        string forged = ObjectText(signed, 1).Replace("/Type /Catalog", "/Type /Catalog " + entry);
        Assert.Contains(entry, forged, StringComparison.Ordinal);

        byte[] attacked = WithDecoy(Append(signed, (1, forged)));

        Assert.False(VerifyGenuine(attacked, anchor).IsValid);
    }

    private static byte[] WithAnnotation(byte[] signed, string keys)
    {
        const string page = "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> /Annots [60 0 R] >>";
        return Append(signed, (3, page), (60, $"60 0 obj\n<< /Type /Annot /Subtype /Square /Rect [10 10 50 50] {keys} >>"));
    }

    [Theory]
    [InlineData("/AA << /PO << /S /JavaScript /JS (this.getField('x').value = 1) >> >>")]
    [InlineData("/A << /S /JavaScript /JS (app.alert(1)) >>")]
    [InlineData("/A << /S /Launch /F (calc.exe) >>")]
    [InlineData("/A << /S /SetOCGState /State [/Toggle] >>")]
    [InlineData("/A << /S /URI /URI (http://a) /Next << /S /JavaScript /JS (1) >> >>")]
    public void AdversarialAnnotationAdded_WithAnActionThatRuns_IsRefused(string keys)
    {
        var (signed, anchor) = Genuine();
        byte[] attacked = WithDecoy(WithAnnotation(signed, keys));

        SignatureVerificationResult genuine = VerifyGenuine(attacked, anchor);

        Assert.False(genuine.IsValid);
        Assert.Contains(genuine.ValidationErrors, e => e.Contains("with an action", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/A << /S /URI /URI (http://example.org) >>")]
    [InlineData("/A << /S /GoTo /D [3 0 R /Fit] >>")]
    public void AnAnnotationAdded_ThatOnlyLinks_StaysPermitted(string keys)
    {
        var (signed, anchor) = Genuine();
        Assert.True(VerifyGenuine(WithDecoy(WithAnnotation(signed, keys)), anchor).IsValid);
    }

    [Fact]
    public void AdversarialTableHiddenBehindAHybridReference_IsRefused()
    {
        var (signed, anchor) = Genuine();
        long prev = SignedPdfFixtures.FindStartXref(signed);

        // A new content stream, a cross-reference stream listing it, and a table that lists neither.
        var body = new StringBuilder();
        long content = signed.Length;
        const string words = "BT /F1 12 Tf 72 700 Td (PAY THE ATTACKER) Tj ET\n";
        body.Append($"4 0 obj\n<< /Length {words.Length} >>\nstream\n{words}endstream\nendobj\n");
        long stream = signed.Length + body.Length;
        var entries = new byte[] { 1, (byte)(content >> 24), (byte)(content >> 16), (byte)(content >> 8), (byte)content, 0, 0 };
        body.Append($"62 0 obj\n<< /Type /XRef /Size 64 /W [1 4 2] /Index [4 1] /Length {entries.Length} >>\nstream\n")
            .Append(Encoding.Latin1.GetString(entries)).Append("\nendstream\nendobj\n");
        long table = signed.Length + body.Length;
        body.Append($"xref\n62 1\n{stream:D10} 00000 n \ntrailer\n<< /Size 64 /Root 1 0 R /Prev {prev} /XRefStm {stream} >>\nstartxref\n{table}\n%%EOF\n");

        byte[] tampered = Concat(signed, Encoding.Latin1.GetBytes(body.ToString()));
        byte[] attacked = WithDecoy(tampered);

        Assert.False(VerifyGenuine(attacked, anchor).IsValid);
    }

    [Fact]
    public void AdversarialPageReplacedFromAnObjectStream_IsRefused()
    {
        var (signed, anchor) = Genuine();
        long prev = SignedPdfFixtures.FindStartXref(signed);

        const string member = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>";
        string members = "3 0 " + member;
        var body = new StringBuilder();
        long objStm = signed.Length;
        body.Append($"63 0 obj\n<< /Type /ObjStm /N 1 /First 4 /Length {members.Length} >>\nstream\n{members}\nendstream\nendobj\n");
        long xref = signed.Length + body.Length;
        byte[] rows =
        {
            2, 0, 0, 0, 63, 0, 0,                                                                                     // 3: member 0 of 63
            1, (byte)(objStm >> 24), (byte)(objStm >> 16), (byte)(objStm >> 8), (byte)objStm, 0, 0,                   // 63
            1, (byte)(xref >> 24), (byte)(xref >> 16), (byte)(xref >> 8), (byte)xref, 0, 0,                           // 62: this stream
        };
        body.Append($"62 0 obj\n<< /Type /XRef /Size 64 /Root 1 0 R /Prev {prev} /W [1 4 2] /Index [3 1 63 1 62 1] /Length {rows.Length} >>\nstream\n")
            .Append(Encoding.Latin1.GetString(rows)).Append("\nendstream\nendobj\n")
            .Append($"startxref\n{xref}\n%%EOF\n");

        byte[] attacked = WithDecoy(Concat(signed, Encoding.Latin1.GetBytes(body.ToString())));

        Assert.False(VerifyGenuine(attacked, anchor).IsValid);
    }

    [Fact]
    public void AdversarialObjectsAfterTheEnd_WithNoTable_AreRefusedWithoutADecoy()
    {
        var (signed, anchor) = Genuine();
        const string words = "BT /F1 12 Tf 72 700 Td (PAY THE ATTACKER) Tj ET\n";
        string tail = $"4 0 obj\n<< /Length {words.Length} >>\nstream\n{words}endstream\nendobj\n";

        byte[] attacked = Concat(signed, Encoding.Latin1.GetBytes(tail));

        Assert.False(VerifyGenuine(attacked, anchor).IsValid);
    }

    [Fact]
    public void AdversarialBrokenTable_TheFileRecoveredWithNewObjects_IsRefusedWithoutADecoy()
    {
        var (signed, anchor) = Genuine();
        const string words = "BT /F1 12 Tf 72 700 Td (PAY THE ATTACKER) Tj ET\n";
        string tail = $"4 0 obj\n<< /Length {words.Length} >>\nstream\n{words}endstream\nendobj\nstartxref\n0\n%%EOF\n";

        byte[] attacked = Concat(signed, Encoding.Latin1.GetBytes(tail));

        Assert.False(VerifyGenuine(attacked, anchor).IsValid);
    }
}
