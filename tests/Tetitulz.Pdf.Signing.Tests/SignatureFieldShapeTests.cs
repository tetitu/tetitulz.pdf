// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
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
/// The signer's two field walks — adoption (<c>PdfSigner.FindAdoptableSignatureField</c>) and the
/// names already in use (<c>SignatureFieldRegistration.FieldNames</c>) — read a field tree the way
/// Core's field model does.
/// </summary>
/// <remarks>
/// <para><b>Three disagreements with Core, one document shape each.</b> The walks joined partial
/// names by hand, so an empty <c>/T</c> added a segment (<c>section.</c>, <c>section..approval</c>)
/// that <c>PdfFormField.FullyQualifiedName</c> does not. They called a node with <c>/T ()</c> a named
/// field, where <c>AcroFieldClassification</c> calls it a widget. And adoption took <c>/FT</c> only
/// from a node and its ancestors, while <c>PdfFormField.FieldType</c> also reads it down from a bare
/// widget kid (the repair for a producer that merged the type into the widget).</para>
/// <para><b>The outcome each time was a second field.</b> Adoption did not see the prepared field,
/// the collision check did, and the signer renamed itself and wrote a new one: one field carries
/// the <c>/V</c> and the one the document prepared stays unsigned. Every case below therefore asserts
/// which object carries the <c>/V</c> and that <c>/Fields</c> did not grow.</para>
/// </remarks>
public sealed class SignatureFieldShapeTests
{
    /// <summary>
    /// A field <c>section</c> whose only widget carries <c>/T () /FT /Sig</c>: Core lists
    /// <c>section</c> as a signature field, and signing <c>section</c> signs it.
    /// </summary>
    [Fact]
    public void AFieldWhoseTypeIsOnItsNamelessWidgetIsAdopted()
    {
        byte[] pdf = Pdf(
            (5, "<< /T (section) /Kids [6 0 R] >>"),
            (6, "<< /T () /FT /Sig /Type /Annot /Subtype /Widget /Rect [50 60 250 140] /P 3 0 R /Parent 5 0 R >>"));

        // What Core says about the unsigned document, which is the claim the signer has to agree with.
        using (var ms = new MemoryStream(pdf))
        using (var doc = PdfDocument.Open(ms))
            Assert.Contains(doc.AcroForm!.GetSignatureFields(), f => f.FullyQualifiedName == "section");

        byte[] signed = Sign(pdf, "section");

        using var outMs = new MemoryStream(signed);
        using var outDoc = PdfDocument.Open(outMs);
        Assert.NotNull(((PdfDictionary)outDoc.GetObject(5))[PdfName.V]);
        Assert.Single(Roots(outDoc));
    }

    /// <summary>
    /// A nameless group between <c>section</c> and <c>approval</c> adds no segment (§12.7.4.2 and
    /// <c>FieldNames.Qualify</c>): the field is <c>section.approval</c>, and signing that name signs it.
    /// </summary>
    [Fact]
    public void AnEmptyPartialNameAddsNoSegment()
    {
        byte[] pdf = Pdf(
            (5, "<< /T (section) /Kids [6 0 R] >>"),
            (6, "<< /T () /Parent 5 0 R /Kids [7 0 R] >>"),
            (7, "<< /T (approval) /FT /Sig /Type /Annot /Subtype /Widget /Rect [50 60 250 140] /P 3 0 R /Parent 6 0 R >>"));

        using (var ms = new MemoryStream(pdf))
        using (var doc = PdfDocument.Open(ms))
            Assert.Contains(doc.AcroForm!.GetSignatureFields(), f => f.FullyQualifiedName == "section.approval");

        byte[] signed = Sign(pdf, "section.approval");

        using var outMs = new MemoryStream(signed);
        using var outDoc = PdfDocument.Open(outMs);
        Assert.NotNull(((PdfDictionary)outDoc.GetObject(7))[PdfName.V]);
        Assert.Single(Roots(outDoc));
    }

    /// <summary>
    /// The names in use are the names Core lists: an empty <c>/T</c> is no name, and adds neither
    /// itself nor a trailing period to the set a new field must avoid.
    /// </summary>
    [Fact]
    public void ANamelessNodeAddsNoNameInUse()
    {
        var approval = new PdfDictionary();
        approval.Set(PdfName.T, new PdfString(Encoding.ASCII.GetBytes("approval")));
        var nameless = new PdfDictionary();
        nameless.Set(PdfName.T, new PdfString(Array.Empty<byte>()));
        nameless.Set(PdfName.Kids, new PdfArray { approval });
        var group = new PdfDictionary();
        group.Set(PdfName.T, new PdfString(Encoding.ASCII.GetBytes("section")));
        group.Set(PdfName.Kids, new PdfArray { nameless });
        var form = new PdfDictionary();
        form.Set(PdfName.Fields, new PdfArray { group });

        var names = SignatureFieldRegistration.FieldNames(form, o => o);

        Assert.Equal(new[] { "approval", "section", "section.approval" }, names.OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// A widget with <c>/T ()</c> is no field, though its qualified name is its parent's: once the
    /// field is signed, signing its name again is a collision renamed around, never the widget
    /// adopted in the field's place (which would put a <c>/V</c> on an annotation of a valid
    /// signature's field).
    /// </summary>
    [Fact]
    public void AWidgetWithAnEmptyNameUnderASignedFieldIsNotAdopted()
    {
        byte[] pdf = Pdf(
            (5, "<< /T (section) /Kids [6 0 R] >>"),
            (6, "<< /T () /FT /Sig /Type /Annot /Subtype /Widget /Rect [50 60 250 140] /P 3 0 R /Parent 5 0 R >>"));

        byte[] twice = Sign(Sign(pdf, "section"), "section");

        using var outMs = new MemoryStream(twice);
        using var outDoc = PdfDocument.Open(outMs);
        Assert.NotNull(((PdfDictionary)outDoc.GetObject(5))[PdfName.V]);
        Assert.Null(((PdfDictionary)outDoc.GetObject(6))[PdfName.V]);
        Assert.Equal(2, Roots(outDoc).Count);
    }

    /// <summary>
    /// The type read up from a bare widget is its node's own repair, and a field kid of that node
    /// does not inherit it: <c>grp.x</c> declares no type, Core lists no signature field by that
    /// name, and signing it is a new field rather than an adoption of <c>x</c>.
    /// </summary>
    [Fact]
    public void ATypeReadFromAWidgetIsNotInheritedByAFieldKid()
    {
        byte[] pdf = Pdf(
            (5, "<< /T (grp) /Kids [6 0 R 7 0 R] >>"),
            (6, "<< /T (x) /Parent 5 0 R >>"),
            (7, "<< /FT /Sig /Type /Annot /Subtype /Widget /Rect [50 60 250 140] /P 3 0 R /Parent 5 0 R >>"));

        using (var ms = new MemoryStream(pdf))
        using (var doc = PdfDocument.Open(ms))
            Assert.DoesNotContain(doc.AcroForm!.GetSignatureFields(), f => f.FullyQualifiedName == "grp.x");

        byte[] signed = Sign(pdf, "grp.x");

        using var outMs = new MemoryStream(signed);
        using var outDoc = PdfDocument.Open(outMs);
        Assert.Null(((PdfDictionary)outDoc.GetObject(6))[PdfName.V]);
        Assert.Equal(2, Roots(outDoc).Count);
    }

    /// <summary>
    /// The control for the first case: a kid whose <c>/T</c> is a real name is a field of its own,
    /// so its <c>/FT</c> does not travel up (that would be inheritance backwards). The group
    /// <c>section</c> is not a signature field, Core does not list it, and signing its name is a
    /// collision the signer renames around; neither node is given a <c>/V</c>.
    /// </summary>
    [Fact]
    public void ASignatureFieldsGroupIsNotItselfAdopted()
    {
        byte[] pdf = Pdf(
            (5, "<< /T (section) /Kids [6 0 R] >>"),
            (6, "<< /T (sig) /FT /Sig /Type /Annot /Subtype /Widget /Rect [50 60 250 140] /P 3 0 R /Parent 5 0 R >>"));

        using (var ms = new MemoryStream(pdf))
        using (var doc = PdfDocument.Open(ms))
            Assert.DoesNotContain(doc.AcroForm!.GetSignatureFields(), f => f.FullyQualifiedName == "section");

        byte[] signed = Sign(pdf, "section");

        using var outMs = new MemoryStream(signed);
        using var outDoc = PdfDocument.Open(outMs);
        Assert.Null(((PdfDictionary)outDoc.GetObject(5))[PdfName.V]);
        Assert.Null(((PdfDictionary)outDoc.GetObject(6))[PdfName.V]);
        Assert.Equal(2, Roots(outDoc).Count);
    }

    /// <summary>
    /// The potency control for the pair above: signing the kid by its qualified name adopts the kid.
    /// </summary>
    [Fact]
    public void TheNamedKidIsAdoptedByItsQualifiedName()
    {
        byte[] pdf = Pdf(
            (5, "<< /T (section) /Kids [6 0 R] >>"),
            (6, "<< /T (sig) /FT /Sig /Type /Annot /Subtype /Widget /Rect [50 60 250 140] /P 3 0 R /Parent 5 0 R >>"));

        byte[] signed = Sign(pdf, "section.sig");

        using var outMs = new MemoryStream(signed);
        using var outDoc = PdfDocument.Open(outMs);
        Assert.NotNull(((PdfDictionary)outDoc.GetObject(6))[PdfName.V]);
        Assert.Single(Roots(outDoc));
    }

    // ----------------------------------------------------------------

    private static PdfArray Roots(PdfDocument doc)
        => (PdfArray)doc.ResolveObject(
            ((PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]))[PdfName.Fields]);

    /// <summary>A one-page document whose <c>/AcroForm /Fields</c> is <c>[5 0 R]</c> and whose page lists the widget, the last object.</summary>
    private static byte[] Pdf(params (int num, string body)[] fields)
    {
        int widget = fields[fields.Length - 1].num;
        var objects = new (int num, string body)[]
        {
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /SigFlags 3 >> >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [{widget} 0 R] >>"),
            (4, "<< /Length 44 >>\nstream\nBT /F1 12 Tf 72 720 Td (Stamped) Tj ET\nendstream"),
        }.Concat(fields).ToArray();

        var sb = new StringBuilder("%PDF-1.7\n");
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

    private static byte[] Sign(byte[] pdf, string fieldName)
    {
        var root = SigningTestCrypto.Ca("CN=Shape Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Shape Signer");
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
