// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Signing;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Signing;

/// <summary>
/// <see cref="PdfOperations.GetSignatures"/> — the list a caller asks "is this signed?" with.
/// </summary>
/// <remarks>
/// <para><b>It lists whatever <c>PdfAcroForm.GetSignatureFields</c> reaches, which includes nodes
/// that are not signature fields.</b> A widget kid inherits <c>/FT /Sig</c> through <c>/Parent</c>
/// (Table 220) and carries no <c>/T</c>, so it arrived here as a second entry with a <b>null</b>
/// field name and <c>HasSignature</c> false — one signature reported as two.</para>
///
/// <para><b>The filter is deliberately asymmetric, and that is the whole design.</b> A node
/// carrying its own <c>/V</c> is reported <i>whatever else is true of it</i>, because this method
/// answers a security question and hiding a signature is the one failure that must not happen —
/// on a <c>/Kids</c> cycle every node is non-terminal, so a symmetric filter would report a signed
/// document as unsigned. Only an <b>unsigned</b> node has to earn its place by being addressable.
/// </para>
/// </remarks>
public class SignatureListingTests
{
    /// <summary>One field with a separate widget kid is one signature, not two.</summary>
    [Fact]
    public void AWidgetKidIsNotListedAsASecondSignature()
    {
        var infos = List(FieldWithSeparateWidget(signed: true));

        var only = Assert.Single(infos);
        Assert.Equal("Signature1", only.FieldName);
        Assert.True(only.HasSignature);
    }

    /// <summary>The same shape unsigned: still one entry, still named.</summary>
    [Fact]
    public void AnUnsignedFieldWithAWidgetKidIsListedOnce()
    {
        var infos = List(FieldWithSeparateWidget(signed: false));

        var only = Assert.Single(infos);
        Assert.Equal("Signature1", only.FieldName);
        Assert.False(only.HasSignature);
    }

    /// <summary>
    /// <b>A signature is never hidden, even when the node is not addressable.</b> On a
    /// <c>/Kids</c> cycle every node is non-terminal, so an addressability filter applied to signed
    /// nodes too would answer "this document is unsigned" about a signed document.
    /// </summary>
    [Fact]
    public void ASignedNodeInACycleIsStillReported()
    {
        var infos = List(CyclicSignedField());

        Assert.Contains(infos, i => i.FieldName == "Sig1" && i.HasSignature);
    }

    /// <summary>
    /// A signature under a parent field is listed by its fully qualified name (ISO 32000-2 §12.7.4.2)
    /// — the name that tells it apart from a <c>sig</c> under another parent, and the one the
    /// verifier and the counter-signer accept.
    /// </summary>
    [Fact]
    public void ANestedSignatureIsListedByItsFullyQualifiedName()
    {
        var infos = List(NestedSignedField());

        var only = Assert.Single(infos);
        Assert.Equal("section.approval", only.FieldName);
        Assert.True(only.HasSignature);
    }

    /// <summary>
    /// The listing reports a signature's sub-filter and reason whichever way the dictionary spells
    /// them — the verifier follows a reference there, and the listing used not to, so the two public
    /// answers about one file disagreed.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheListingReadsTheSignatureDictionary_WhicheverWayItIsSpelled(bool indirect)
    {
        var infos = List(SignedField(indirect));

        var only = Assert.Single(infos);
        Assert.Equal("adbe.pkcs7.detached", only.SubFilter);
        Assert.Equal("a stated reason", only.Reason);
    }

    // ------------------------------------------------------------------

    private static IReadOnlyList<PdfSignatureInfo> List(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        return PdfOperations.GetSignatures(ms);
    }

    private static byte[] FieldWithSeparateWidget(bool signed)
    {
        string v = signed
            ? " /V << /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached >>"
            : string.Empty;

        return Build(
            "1 0 obj" + N + "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>" + N + "endobj" + N,
            "2 0 obj" + N + "<< /Type /Pages /Kids [3 0 R] /Count 1 >>" + N + "endobj" + N,
            "3 0 obj" + N + "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [5 0 R] >>" + N + "endobj" + N,
            "4 0 obj" + N + "<< /FT /Sig /T (Signature1)" + v + " /Kids [5 0 R] >>" + N + "endobj" + N,
            "5 0 obj" + N + "<< /Type /Annot /Subtype /Widget /Parent 4 0 R /Rect [0 0 100 50] >>" + N + "endobj" + N);
    }

    /// <summary>One signed field; objects 6 and 7 hold <c>/SubFilter</c> and <c>/Reason</c> when <paramref name="indirect"/>.</summary>
    private static byte[] SignedField(bool indirect) => Build(
        "1 0 obj" + N + "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>" + N + "endobj" + N,
        "2 0 obj" + N + "<< /Type /Pages /Kids [3 0 R] /Count 1 >>" + N + "endobj" + N,
        "3 0 obj" + N + "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>" + N + "endobj" + N,
        "4 0 obj" + N + "<< /FT /Sig /T (Signature1) /V 5 0 R >>" + N + "endobj" + N,
        "5 0 obj" + N + "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter " + (indirect ? "6 0 R" : "/adbe.pkcs7.detached") +
            " /Reason " + (indirect ? "7 0 R" : "(a stated reason)") + " >>" + N + "endobj" + N,
        "6 0 obj" + N + "/adbe.pkcs7.detached" + N + "endobj" + N,
        "7 0 obj" + N + "(a stated reason)" + N + "endobj" + N);

    /// <summary>Field 4 is the parent <c>section</c>; its kid 5, <c>approval</c>, carries the signature.</summary>
    private static byte[] NestedSignedField() => Build(
        "1 0 obj" + N + "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>" + N + "endobj" + N,
        "2 0 obj" + N + "<< /Type /Pages /Kids [3 0 R] /Count 1 >>" + N + "endobj" + N,
        "3 0 obj" + N + "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [5 0 R] >>" + N + "endobj" + N,
        "4 0 obj" + N + "<< /T (section) /Kids [5 0 R] >>" + N + "endobj" + N,
        "5 0 obj" + N + "<< /FT /Sig /T (approval) /Parent 4 0 R /Type /Annot /Subtype /Widget /Rect [0 0 100 50]" +
            " /V << /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached >> >>" + N + "endobj" + N);

    /// <summary>Fields 4 and 5 are each other's kid, and 4 carries a signature.</summary>
    private static byte[] CyclicSignedField() => Build(
        "1 0 obj" + N + "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>" + N + "endobj" + N,
        "2 0 obj" + N + "<< /Type /Pages /Kids [3 0 R] /Count 1 >>" + N + "endobj" + N,
        "3 0 obj" + N + "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>" + N + "endobj" + N,
        "4 0 obj" + N + "<< /FT /Sig /T (Sig1) /V << /Type /Sig /Filter /Adobe.PPKLite >> /Kids [5 0 R] >>" + N + "endobj" + N,
        "5 0 obj" + N + "<< /FT /Sig /T (Sig2) /Kids [4 0 R] >>" + N + "endobj" + N);

    private const string N = "\n";

    private static byte[] Build(params string[] objects)
    {
        var b = new StringBuilder();
        var offsets = new List<int>();

        b.Append("%PDF-1.7" + N);
        foreach (string o in objects) { offsets.Add(b.Length); b.Append(o); }

        int count = offsets.Count + 1;
        int xref = b.Length;
        b.Append("xref" + N + "0 " + count + N + "0000000000 65535 f " + N);
        foreach (int o in offsets) b.Append(o.ToString("D10")).Append(" 00000 n " + N);
        b.Append("trailer" + N + "<< /Size " + count + " /Root 1 0 R >>" + N
               + "startxref" + N + xref + N + "%%EOF" + N);
        return Encoding.ASCII.GetBytes(b.ToString());
    }
}
