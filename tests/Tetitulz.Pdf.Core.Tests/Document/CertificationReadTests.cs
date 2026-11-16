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
/// <see cref="DocMdpAnalyzer.TryReadCertification"/> — the graph half of the DocMDP verdict on its
/// own: is the document certified, and at what <c>/P</c>.
/// </summary>
/// <remarks>
/// <para>A caller that needs only that answer — the image replacer's certified-document refusal —
/// used to run the full analysis and discard its modification verdict, which meant handing it a
/// length. For a decrypted copy the length was first the copy's <c>startxref</c> offset and then the
/// copy's own length, and neither is meaningful: the copy's <c>/ByteRange</c> describes the original
/// file. The graph-only question needs no length, and these pin that it finds the same certification
/// the full analysis finds, by both spellings, and that a document without one reports none.</para>
/// </remarks>
public class CertificationReadTests
{
    /// <summary>Table 263's spelling: the catalog's <c>/Perms /DocMDP</c> names the signature.</summary>
    [Fact]
    public void ACertificationNamedByThePermsDictionary_IsReadWithItsLevel()
    {
        byte[] pdf = Build(perms: true, reference: Reference(2));
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        Assert.True(DocMdpAnalyzer.TryReadCertification(doc, out int p, out string description));
        Assert.Equal(2, p);
        AssertAgreesWithTheFullAnalysis(doc, pdf.Length, p, description);
    }

    /// <summary>
    /// The other spelling: no <c>/Perms</c>, and a signature field's value carries the
    /// <c>/Reference</c> whose transform method is <c>/DocMDP</c>.
    /// </summary>
    [Fact]
    public void ACertificationCarriedByASignatureField_IsReadWithItsLevel()
    {
        byte[] pdf = Build(perms: false, reference: Reference(1));
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        Assert.True(DocMdpAnalyzer.TryReadCertification(doc, out int p, out string description));
        Assert.Equal(1, p);
        AssertAgreesWithTheFullAnalysis(doc, pdf.Length, p, description);
    }

    /// <summary>The control: an approval signature certifies nothing.</summary>
    [Fact]
    public void AnApprovalSignatureAlone_ReportsNoCertification()
    {
        byte[] pdf = Build(perms: false, reference: "");
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        Assert.False(DocMdpAnalyzer.TryReadCertification(doc, out int p, out string description));
        Assert.Equal(0, p);
        Assert.Null(description);
        Assert.False(DocMdpAnalyzer.Analyze(doc, pdf.Length).HasCertification);
    }

    /// <summary>
    /// One search behind both entry points: the level and its Table 257 wording are what the full
    /// analysis reports for the same document, so a caller switching from one to the other reads the
    /// same certification.
    /// </summary>
    private static void AssertAgreesWithTheFullAnalysis(PdfDocument doc, long length, int p, string description)
    {
        var analysis = DocMdpAnalyzer.Analyze(doc, length);
        Assert.True(analysis.HasCertification);
        Assert.Equal(analysis.PermissionLevel, p);
        Assert.Equal(analysis.PermissionDescription, description);
        Assert.False(string.IsNullOrEmpty(description));
    }

    // ------------------------------------------------------------------

    private static string Reference(int p) =>
        " /Reference [<< /Type /SigRef /TransformMethod /DocMDP" +
        " /TransformParams << /Type /TransformParams /P " + p.ToString(CultureInfo.InvariantCulture) +
        " /V /1.2 >> >>]";

    /// <summary>
    /// Objects 1–6: catalog (with <c>/Perms</c> naming object 5 when <paramref name="perms"/>),
    /// pages, page, content, the signature dictionary carrying <paramref name="reference"/>, and
    /// the signature widget whose <c>/V</c> is that dictionary.
    /// </summary>
    private static byte[] Build(bool perms, string reference)
    {
        string permsEntry = perms ? " /Perms << /DocMDP 5 0 R >>" : "";
        return Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] /SigFlags 3 >>" + permsEntry + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Annots [6 0 R] >>",
            StreamObj("q Q\n"),
            "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached" +
            " /ByteRange [0 100 200 100] /Contents <00>" + reference + " >>",
            "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1) /Rect [0 0 10 10] /V 5 0 R >>");
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
