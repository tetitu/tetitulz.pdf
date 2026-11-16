// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// A certification signature whose <c>/TransformMethod</c> is an <b>indirect reference</b> must
/// still be recognised as a DocMDP certification.
/// </summary>
/// <remarks>
/// <para><b>This is the fail-open direction, on the analyser that decides whether a certified
/// document's permissions are enforced at all.</b> <c>/TransformMethod</c> was read with
/// <c>GetName</c>, which tests the raw value, so a legal <c>/TransformMethod 12 0 R</c> (§7.3.10)
/// returned <see langword="null"/>. Null is not <c>/DocMDP</c>, so <c>ExtractPFromSigRef</c>
/// returned <c>0</c>, <c>FindMdpViaSigFields</c> found nothing, and <c>Analyze</c> reported
/// <i>"No DocMDP certification signature found in this document."</i> — with
/// <c>HasCertification = false</c>. Every restriction the document declares becomes
/// invisible.</para>
///
/// <para>Its two neighbours went opposite ways, which is why each was traced separately rather than
/// fixed as a batch: an unresolved <c>/P</c> falls to the spec default of <b>2</b> (form filling
/// allowed) where the file may have said <b>1</b> (no changes) — more permissive, though it does at
/// least set <c>UsedDefaultP</c>; while an unresolved <c>/ByteRange</c> is fail-<b>closed</b>, since
/// the caller treats <c>certEnd &lt;= 0</c> as "cannot reason about this file" and records a
/// violation. That one produced a false <i>non</i>-compliance report.</para>
///
/// <para>Asserted on <c>HasCertification</c> and the reported permission level — the two facts a
/// caller acts on. "Analyze returned a result" is true in every failure mode here.</para>
/// </remarks>
public class IndirectDocMdpTests
{
    /// <summary>
    /// The certification is found whichever way <c>/TransformMethod</c> is spelled, and reports the
    /// document's declared level. The direct spelling is the control: it always worked.
    /// </summary>
    [Theory]
    [InlineData(true)]   // /TransformMethod 9 0 R
    [InlineData(false)]  // /TransformMethod /DocMDP
    public void CertificationIsFound_WhicheverWayTransformMethodIsSpelled(bool indirect)
    {
        byte[] pdf = CertifiedPdf(indirect);

        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var analysis = DocMdpAnalyzer.Analyze(doc, pdf.Length);

        Assert.True(analysis.HasCertification,
            "the certification signature was not recognised: " + analysis.Summary);
        Assert.Equal(1, analysis.PermissionLevel);   // /P 1 — no changes permitted
    }

    /// <summary>
    /// The floor: a document with no certification really does report none, so the assertion above
    /// cannot be met by an analyser that claims certification unconditionally — and "no
    /// certification" is exactly the answer the defect produced.
    /// </summary>
    /// <summary>
    /// Wave 17 R1.6: Table 257's <c>/P</c> is an integer 1 to 3. Read as <c>(int)DoubleValue</c>,
    /// <c>2.5</c> became level 2 and a value past <c>int</c> wrapped; and a value outside 1–3 made
    /// the whole certification vanish — the restrictions the document declared were not enforced at
    /// all. A level that is not one of the three now reads as the absent one: the default, 2, with
    /// the certification kept.
    /// </summary>
    [Theory]
    [InlineData("2.5")]
    [InlineData("7")]
    [InlineData("0")]
    [InlineData("99999999999999")]   // past int and long-ish reals alike: not a level
    [InlineData("4294967297")]       // 2^32 + 1: the old cast wrapped it to 1
    public void AMalformedPermissionLevel_IsTheDefault_AndTheCertificationIsKept(string p)
    {
        byte[] pdf = CertifiedPdf(indirect: false, p: p);
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var analysis = DocMdpAnalyzer.Analyze(doc, pdf.Length);
        Assert.True(analysis.HasCertification, analysis.Summary);
        Assert.Equal(2, analysis.PermissionLevel);
    }

    /// <summary>The control: each legal level is read as itself.</summary>
    [Theory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("3", 3)]
    public void ALegalPermissionLevel_IsReadAsItself(string p, int expected)
    {
        byte[] pdf = CertifiedPdf(indirect: false, p: p);
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        Assert.Equal(expected, DocMdpAnalyzer.Analyze(doc, pdf.Length).PermissionLevel);
    }

    [Fact]
    public void UncertifiedDocument_ReportsNoCertification()
    {
        byte[] pdf = CertifiedPdf(indirect: false, certify: false);

        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        Assert.False(DocMdpAnalyzer.Analyze(doc, pdf.Length).HasCertification);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A signature field carrying a <c>/Reference</c> whose <c>/TransformMethod</c> is
    /// <c>/DocMDP</c> and whose <c>/TransformParams /P</c> is 1. Object 9 carries the method name
    /// when <paramref name="indirect"/>; <paramref name="certify"/> false drops the
    /// <c>/Reference</c> entirely.
    /// </summary>
    private static byte[] CertifiedPdf(bool indirect, bool certify = true, string p = "1")
    {
        string method = indirect ? "9 0 R" : "/DocMDP";
        string reference = certify
            ? " /Reference [<< /Type /SigRef /TransformMethod " + method +
              " /TransformParams << /Type /TransformParams /P " + p + " /V /1.2 >> >>]"
            : "";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] /SigFlags 3 >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Annots [6 0 R] >>",
            StreamObj("q Q\n"),
            "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached" +
            " /ByteRange [0 100 200 100] /Contents <00>" + reference + " >>",
            "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1) /Rect [0 0 10 10] /V 5 0 R >>",
            "null",
            "null",
            "/DocMDP");
    }

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "endstream";

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
