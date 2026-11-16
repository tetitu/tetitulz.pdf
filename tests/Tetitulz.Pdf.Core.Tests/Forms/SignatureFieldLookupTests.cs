// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Forms;

/// <summary>
/// <c>PdfAcroForm.FindSignatureField</c> addresses a field by its fully qualified name, and by a
/// partial name only when exactly one signature field carries it.
/// </summary>
/// <remarks>
/// <para><b>ISO 32000-2 §12.7.4.2:</b> a field's fully qualified name is its partial names joined by
/// periods from the root, and it is that name, not the partial one, which identifies a field — two
/// parents may each hold a <c>sig</c>. The lookup compared the partial name at every depth and
/// returned the first hit. <c>b.sig</c> was therefore never found, and <c>sig</c> meant whichever
/// field the walk reached first — so the verifier and the counter-signer could not be pointed at
/// the second of two such signatures at all, and were pointed at the first without being told.</para>
/// </remarks>
public sealed class SignatureFieldLookupTests
{
    [Fact]
    public void AFullyQualifiedNameFindsTheNestedField()
    {
        using var doc = Open(TwoParentsPdf(topLevelName: "top"));

        var field = doc.AcroForm.FindSignatureField("b.sig");

        Assert.NotNull(field);
        Assert.Equal(8, field.Dictionary.ObjectNumber);
    }

    [Fact]
    public void APartialNameTwoFieldsShare_FindsNeither_AndCountsThem()
    {
        using var doc = Open(TwoParentsPdf(topLevelName: "top"));

        Assert.Null(doc.AcroForm.FindSignatureField("sig"));
        Assert.Null(doc.AcroForm.FindSignatureField("sig", out int partialNameMatches));
        Assert.Equal(2, partialNameMatches);
    }

    /// <summary>The control: a partial name only one field carries still finds that field.</summary>
    [Fact]
    public void APartialNameOneFieldCarries_StillFindsIt()
    {
        using var doc = Open(TwoParentsPdf(topLevelName: "top"));

        var field = doc.AcroForm.FindSignatureField("top");

        Assert.NotNull(field);
        Assert.Equal(9, field.Dictionary.ObjectNumber);
    }

    /// <summary>
    /// A field whose fully qualified name is the one asked for wins over another field that merely
    /// has it as a partial name — even when the walk reaches the other one first.
    /// </summary>
    [Fact]
    public void AFullyQualifiedNameWinsOverAnotherFieldsPartialName()
    {
        // /Fields [5 7 9]: 6 is a.sig and 8 is b.sig, reached before 9, the top-level "sig".
        using var doc = Open(TwoParentsPdf(topLevelName: "sig"));

        var field = doc.AcroForm.FindSignatureField("sig");

        Assert.NotNull(field);
        Assert.Equal(9, field.Dictionary.ObjectNumber);
    }

    /// <summary>
    /// The name reports give a field is its fully qualified one, and <c>null</c> — not an empty
    /// string — when no node in its chain carries a <c>/T</c>.
    /// </summary>
    [Fact]
    public void AReportedNameIsFullyQualified_OrNullWhenNothingIsNamed()
    {
        using var doc = Open(TwoParentsPdf(topLevelName: "top"));
        var fields = doc.AcroForm.GetSignatureFields();

        Assert.Contains(fields, f => f.ReportedName == "b.sig");

        var unnamed = new Tetitulz.Pdf.Core.Forms.PdfSignatureField(new Tetitulz.Pdf.Core.Objects.PdfDictionary(), doc, null);
        Assert.Equal("", unnamed.FullyQualifiedName);
        Assert.Null(unnamed.ReportedName);
    }

    /// <summary>
    /// The certification's field name is its fully qualified one, so it names one field.
    /// </summary>
    [Fact]
    public void TheCertificationFieldIsNamedByItsFullyQualifiedName()
    {
        byte[] pdf = TwoParentsPdf(topLevelName: "top", certifyB: true);
        using var doc = Open(pdf);

        var analysis = DocMdpAnalyzer.Analyze(doc, pdf.Length);

        Assert.True(analysis.HasCertification);
        Assert.Equal("b.sig", analysis.CertFieldName);
    }

    // ------------------------------------------------------------------

    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    /// <summary>
    /// <c>/Fields [5 7 9]</c>: parent <c>a</c> (5) holding signature field <c>sig</c> (6), parent
    /// <c>b</c> (7) holding another <c>sig</c> (8), and a top-level signature field (9). Objects 10 and
    /// 11 are signature dictionaries for 6 and 8; with <paramref name="certifyB"/> the second carries a
    /// DocMDP reference and no <c>/Perms</c> names it, so the analyzer has to find it through its field.
    /// </summary>
    private static byte[] TwoParentsPdf(string topLevelName, bool certifyB = false)
    {
        string reference = certifyB
            ? " /Reference [<< /Type /SigRef /TransformMethod /DocMDP" +
              " /TransformParams << /Type /TransformParams /P 2 /V /1.2 >> >>]"
            : "";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 7 0 R 9 0 R] /SigFlags 3 >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /T (a) /Kids [6 0 R] >>",
            "<< /FT /Sig /T (sig) /Parent 5 0 R /V 10 0 R >>",
            "<< /T (b) /Kids [8 0 R] >>",
            "<< /FT /Sig /T (sig) /Parent 7 0 R /V 11 0 R >>",
            $"<< /FT /Sig /T ({topLevelName}) >>",
            "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 10 20 10] /Contents <00> >>",
            "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 10 20 10] /Contents <00>" +
                reference + " >>");
    }

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
