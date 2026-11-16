// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// Four shapes a revision appended after a signature can take to pass as a permitted change, each
/// judged by what the signed revision held: an object it referred to without holding, supplied later;
/// a signature it held, put into another field; an annotation dressed in a signature's keys; and LTV
/// data taken away. Each is paired with the permitted change it imitates.
/// </summary>
public sealed class SignedRevisionShapeTests
{
    private const string Pages = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";

    // ------------------------------------------------------------------ a reference to nothing, filled

    [Fact]
    public void AnObjectTheSignedPageReferredToWithoutHolding_SuppliedAsLtvData_IsNotPermittedAtP1()
    {
        // The page's /Contents names 9, which the signed revision does not hold; the revision supplies
        // 9 as a stream of stream keys alone and lists it under /DSS, where LTV data is admitted.
        byte[] signed = Build(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, Pages),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents [4 0 R 9 0 R] >>"),
            (4, Stream("(signed) Tj")));
        byte[] revised = Revise(signed,
            (9, Stream("1 1 1 rg 0 0 100 100 re f (REPLACED) Tj")),
            (1, "<< /Type /Catalog /Pages 2 0 R /DSS << /Certs [9 0 R] >> >>"));

        DocMdpAnalysis analysis = Analyze(revised, signed.Length, 1);

        Assert.False(analysis.IsCompliant);
        Assert.Contains(analysis.Violations, v => v.StartsWith("obj 9: the signed revision referred to it without holding it", StringComparison.Ordinal));
    }

    [Fact]
    public void NewLtvDataTheSignedRevisionNeverReferredTo_IsPermittedAtP1()
    {
        byte[] signed = Build(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, Pages),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>"),
            (4, Stream("(signed) Tj")));
        byte[] revised = Revise(signed,
            (9, Stream("certificate bytes")),
            (1, "<< /Type /Catalog /Pages 2 0 R /DSS << /Certs [9 0 R] >> >>"));

        DocMdpAnalysis analysis = Analyze(revised, signed.Length, 1);

        Assert.True(analysis.IsCompliant, string.Join("; ", analysis.Violations));
    }

    [Fact]
    public void AnObjectTheSignedPageReferredToWithoutHolding_SuppliedAsAForm_IsNotPermittedAtP2()
    {
        byte[] signed = Build(
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, Pages),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /XObject << /Fm 9 0 R >> >> >>"),
            (4, Stream("/Fm Do")));
        byte[] revised = Revise(signed, (9, Stream("(REPLACED) Tj", "/Type /XObject /Subtype /Form /BBox [0 0 100 100]")));

        DocMdpAnalysis analysis = Analyze(revised, signed.Length, 2);

        Assert.False(analysis.IsCompliant);
        Assert.Contains(analysis.Violations, v => v.StartsWith("obj 9: the signed revision referred to it without holding it", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ a held signature in another field

    [Fact]
    public void AnEmptySignatureFieldFilledWithASignatureTheSignedRevisionHeld_IsNotAFillIn()
    {
        byte[] signed = SignedFormWithAnEmptyField();
        byte[] revised = Revise(signed, (6, "<< /FT /Sig /T (Manager) /V 7 0 R /Type /Annot /Subtype /Widget /Rect [0 0 50 20] /P 3 0 R >>"));

        DocMdpAnalysis analysis = Analyze(revised, signed.Length, 3);

        Assert.False(analysis.IsCompliant);
        Assert.Contains(analysis.Violations, v => v.Contains("a signature the signed revision already held", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptySignatureFieldGivenANewSignature_IsPermitted()
    {
        byte[] signed = SignedFormWithAnEmptyField();
        byte[] revised = Revise(signed,
            (8, "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 0 0 0] /Contents <00> >>"),
            (6, "<< /FT /Sig /T (Manager) /V 8 0 R /Type /Annot /Subtype /Widget /Rect [0 0 50 20] /P 3 0 R >>"));

        DocMdpAnalysis analysis = Analyze(revised, signed.Length, 3);

        Assert.True(analysis.IsCompliant, string.Join("; ", analysis.Violations));
    }

    // ------------------------------------------------------------------ an annotation in a signature's keys

    [Fact]
    public void AnAnnotationCarryingASignaturesKeysAndAScript_IsJudgedAsTheAnnotationItIs()
    {
        byte[] signed = OnePageWithAnAnnotation();
        byte[] revised = Revise(signed,
            (7, "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /AA << /PO << /S /JavaScript /JS (app.alert(1)) >> >> " +
                "/ByteRange [0 0 0 0] /Contents () >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Annots [6 0 R 7 0 R] >>"));

        DocMdpAnalysis analysis = Analyze(revised, signed.Length, 3);

        Assert.False(analysis.IsCompliant);
        Assert.Contains(analysis.Violations, v => v.Contains("an annotation with an action other than following a link", StringComparison.Ordinal));
    }

    [Fact]
    public void ANewSignatureDictionary_IsStillAnApprovalSignature()
    {
        byte[] signed = OnePageWithAnAnnotation();
        byte[] revised = Revise(signed,
            (7, "<< /Type /Sig /Filter /Adobe.PPKLite /ByteRange [0 0 0 0] /Contents <00> >>"));

        DocMdpAnalysis analysis = Analyze(revised, signed.Length, 3);

        Assert.True(analysis.IsCompliant, string.Join("; ", analysis.Violations));
        Assert.Contains(analysis.PermittedChanges, c => c.Contains("approval signature added", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ LTV data taken away

    [Fact]
    public void ACatalogThatDropsTheDss_IsNotAnLtvAddition()
    {
        byte[] signed = SignedWithDss("<< /CRLs [8 0 R] >>");
        byte[] revised = Revise(signed, (1, "<< /Type /Catalog /Pages 2 0 R >>"));

        DocMdpAnalysis analysis = Analyze(revised, signed.Length, 3);

        Assert.False(analysis.IsCompliant);
        Assert.Contains(analysis.Violations, v => v.StartsWith("catalog: /DSS dropped LTV data", StringComparison.Ordinal));
    }

    [Fact]
    public void ACatalogWhoseDssKeepsEveryListAndAddsOne_IsAnLtvAddition()
    {
        byte[] signed = SignedWithDss("<< /CRLs [8 0 R] >>");
        byte[] revised = Revise(signed,
            (9, Stream("another crl")),
            (1, "<< /Type /Catalog /Pages 2 0 R /DSS << /CRLs [8 0 R 9 0 R] >> >>"));

        DocMdpAnalysis analysis = Analyze(revised, signed.Length, 3);

        Assert.True(analysis.IsCompliant, string.Join("; ", analysis.Violations));
    }

    [Fact]
    public void ADssObjectRewrittenWithoutItsCrl_IsNotAnLtvAddition()
    {
        byte[] signed = SignedWithDss("10 0 R", (10, "<< /CRLs [8 0 R] >>"));
        byte[] revised = Revise(signed, (10, "<< /CRLs [] >>"));

        DocMdpAnalysis analysis = Analyze(revised, signed.Length, 3);

        Assert.False(analysis.IsCompliant);
    }

    [Fact]
    public void ADssObjectRewrittenWithItsCrlAndAnother_IsAnLtvAddition()
    {
        byte[] signed = SignedWithDss("10 0 R", (10, "<< /CRLs [8 0 R] >>"));
        byte[] revised = Revise(signed, (9, Stream("another crl")), (10, "<< /CRLs [8 0 R 9 0 R] >>"));

        DocMdpAnalysis analysis = Analyze(revised, signed.Length, 3);

        Assert.True(analysis.IsCompliant, string.Join("; ", analysis.Violations));
    }

    // ------------------------------------------------------------------ fixtures

    private static DocMdpAnalysis Analyze(byte[] pdf, long signedEnd, int permission)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return DocMdpAnalyzer.AnalyzeChangesSince(doc, signedEnd, pdf.Length, permission);
    }

    /// <summary>A signed field 'Employee' (5, holding signature 7) and an empty signature field 'Manager' (6).</summary>
    private static byte[] SignedFormWithAnEmptyField() => Build(
        (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 6 0 R] /SigFlags 3 >> >>"),
        (2, Pages),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Annots [5 0 R 6 0 R] >>"),
        (4, Stream("(form) Tj")),
        (5, "<< /FT /Sig /T (Employee) /V 7 0 R /Type /Annot /Subtype /Widget /Rect [0 50 50 70] /P 3 0 R >>"),
        (6, "<< /FT /Sig /T (Manager) /Type /Annot /Subtype /Widget /Rect [0 0 50 20] /P 3 0 R >>"),
        (7, "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 0 0 0] /Contents <00> >>"));

    private static byte[] OnePageWithAnAnnotation() => Build(
        (1, "<< /Type /Catalog /Pages 2 0 R >>"),
        (2, Pages),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Annots [6 0 R] >>"),
        (4, Stream("(page) Tj")),
        (6, "<< /Type /Annot /Subtype /Square /Rect [0 0 1 1] >>"));

    /// <summary>A catalog whose /DSS is <paramref name="dss"/> (inline, or a reference to one of <paramref name="more"/>), and a CRL stream 8.</summary>
    private static byte[] SignedWithDss(string dss, params (int Number, string Body)[] more) => Build(
        new[]
        {
            (1, "<< /Type /Catalog /Pages 2 0 R /DSS " + dss + " >>"),
            (2, Pages),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>"),
            (4, Stream("(page) Tj")),
            (8, Stream("a crl listing the signer")),
        }.Concat(more).ToArray());

    private static string Stream(string text, string dict = "") => "<< " + dict + " /Length " + text.Length + " >>\nstream\n" + text + "\nendstream";

    private static byte[] Build(params (int Number, string Body)[] objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new SortedDictionary<int, int>();
        foreach (var (number, body) in objects.OrderBy(o => o.Number))
        {
            offsets[number] = sb.Length;
            sb.Append(number).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }
        int max = objects.Max(o => o.Number);
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(max + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int n = 1; n <= max; n++)
            sb.Append(offsets.TryGetValue(n, out int at) ? at.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n" : "0000000000 00000 f \n");
        sb.Append("trailer\n<< /Size ").Append(max + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>An incremental revision writing <paramref name="objects"/>, with a /Prev to the last table.</summary>
    private static byte[] Revise(byte[] pdf, params (int Number, string Body)[] objects)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int at = text.LastIndexOf("startxref", StringComparison.Ordinal);
        long prev = long.Parse(text.Substring(at + 9, text.IndexOf("%%EOF", at, StringComparison.Ordinal) - at - 9).Trim(), CultureInfo.InvariantCulture);
        int size = Math.Max(objects.Max(o => o.Number) + 1, SizeOf(text));

        var sb = new StringBuilder(text);
        var offsets = new SortedDictionary<int, int>();
        foreach (var (number, body) in objects)
        {
            offsets[number] = sb.Length;
            sb.Append(number).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n");
        foreach (var o in offsets)
            sb.Append(o.Key).Append(" 1\n").Append(o.Value.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(size).Append(" /Root 1 0 R /Prev ").Append(prev)
          .Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static int SizeOf(string text)
    {
        int at = text.LastIndexOf("/Size ", StringComparison.Ordinal);
        int end = at + 6;
        while (end < text.Length && char.IsDigit(text[end])) end++;
        return int.Parse(text.Substring(at + 6, end - at - 6), CultureInfo.InvariantCulture);
    }
}
