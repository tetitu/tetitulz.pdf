// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

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
/// A signature field prepared under a <b>parent node</b> is adopted, not renamed around.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> `FindAdoptableSignatureField` scanned only the top level of
/// <c>/AcroForm /Fields</c> while <c>SignatureFieldRegistration.FieldNames</c> walked <c>/Kids</c>, so the two halves
/// disagreed about the same document: the collision check saw the name, adoption did not. The signer
/// renamed itself and wrote a second, invisible field to carry the <c>/V</c>, leaving the prepared
/// field permanently unsigned — the same outcome, by a different route, that
/// <c>VisibleSignatureFieldAdoptionTests</c> was written for.</para>
///
/// <para><b>Every case counts the SIGNED fields, not the signature fields.</b> "A signature
/// verifies" is also true of the renamed-and-invented outcome — that is precisely what made the
/// defect survivable — so the discriminating assertion is that exactly one field ends up carrying a
/// <c>/V</c>, and that it is the prepared one.</para>
///
/// <para><b>Why not the raw count.</b> <c>GetSignatureFields</c> reports every node whose resolved
/// <c>/FT</c> is <c>/Sig</c>, and <c>/FT</c> is inheritable — so a parent node holding it for its
/// kids' benefit, and a widget inheriting it, are both reported. Neither can carry a <c>/V</c>, so
/// filtering on <c>HasSignature</c> is what a caller does anyway. Making the enumeration itself
/// strict was tried and reverted: it under-reports on the adversarial cyclic form that
/// <c>AcroFormCycleGuardTests</c> is built on, and hiding a real signature is a worse failure than
/// listing an entry a caller skips. Recorded in GAPS §6c.</para>
/// </remarks>
public sealed class NestedSignatureFieldAdoptionTests
{
    private const string Parent = "section";
    private const string Leaf = "approval";
    private const string Qualified = Parent + "." + Leaf;

    // ================================================================ adoption

    /// <summary>
    /// The nested field named by its fully qualified name is the one that ends up signed.
    /// </summary>
    [Fact]
    public void ANestedFieldIsAdoptedRatherThanRenamedAround()
    {
        byte[] signed = Sign(NestedFieldPdf(), Qualified);

        using var ms = new MemoryStream(signed);
        using var doc = PdfDocument.Open(ms);

        var signedFields = doc.AcroForm!.GetSignatureFields().Where(f => f.HasSignature).ToList();
        Assert.True(signedFields.Count == 1,
            "expected the prepared field to be adopted, so exactly ONE field carries a /V. Found " +
            $"{signedFields.Count}: {string.Join(", ", signedFields.Select(f => f.FieldName))}.");
        Assert.Equal(Leaf, signedFields[0].FieldName);

        // The adopted object is the SAME one the document prepared — object 6, still under its
        // parent — rather than a fresh field wearing the same name.
        PdfDictionary adopted = NestedField(doc);
        Assert.NotNull(adopted[PdfName.V]);
        Assert.Equal(50d, adopted.GetArray(PdfName.Rect)!.GetDouble(0));
        Assert.NotNull(doc.ResolveObject(adopted[PdfName.AP]) as PdfDictionary);

        Assert.True(Verify(signed, Leaf).IsIntegrityValid);
    }

    /// <summary>
    /// The parent node is not promoted into <c>/Fields</c> twice, and the adopted kid is not
    /// promoted into it at all.
    /// </summary>
    /// <remarks>
    /// §12.7.3.2 makes <c>/Fields</c> the <i>root</i> fields. A nested field added there as well
    /// would sit in the tree twice and have two different fully qualified names — a document that
    /// still verifies and that no two readers need agree about.
    /// </remarks>
    [Fact]
    public void TheAdoptedKidIsNotAlsoPromotedToARootField()
    {
        byte[] signed = Sign(NestedFieldPdf(), Qualified);

        using var ms = new MemoryStream(signed);
        using var doc = PdfDocument.Open(ms);

        var roots = (PdfArray)doc.ResolveObject(
            ((PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]))[PdfName.Fields]);

        Assert.Single(roots);
        Assert.Equal(5, ((PdfReference)roots[0]).ReferenceObjectNumber);   // the parent node
    }

    /// <summary>
    /// <c>/FT</c> inherited from the parent is enough: the nested field need not carry its own.
    /// </summary>
    /// <remarks>
    /// Table 220 makes <c>/FT</c> inheritable, and a form generator that sets it once on the parent
    /// is producing an ordinary document. Reading it off the node alone finds nothing, so the type
    /// has to travel down the walk.
    /// </remarks>
    [Fact]
    public void AnInheritedFieldTypeIsEnough()
    {
        byte[] signed = Sign(NestedFieldPdf(fieldTypeOnParent: true), Qualified);

        using var ms = new MemoryStream(signed);
        using var doc = PdfDocument.Open(ms);

        Assert.Single(doc.AcroForm!.GetSignatureFields().Where(f => f.HasSignature));
        Assert.NotNull(NestedField(doc)[PdfName.V]);
    }

    // ================================================================ what must NOT be adopted

    /// <summary>
    /// A widget under the field is never adopted in the field's place.
    /// </summary>
    /// <remarks>
    /// <para><b>A terminal field's kids are its widgets, and they have no <c>/T</c> of their own</b>,
    /// so they inherit the field's fully qualified name. Without the "must carry its own name" test
    /// the walk finds a node whose qualified name matches and supersedes an annotation with a field
    /// dictionary — a document whose signature lives on a widget while its field is still empty.</para>
    ///
    /// <para><b>The field has to be ALREADY SIGNED for this to discriminate, which a mutation
    /// showed.</b> With an empty field the walk matches the field first and returns, so removing the
    /// own-name test changes nothing — the first version of this case passed either way. It is only
    /// once the field is disqualified (it has a <c>/V</c>, so it is a real collision) that the walk
    /// reaches the widget at all, and that is exactly when adopting one would be worst: the correct
    /// outcome is to rename around, and the mutated one silently overwrites an annotation belonging
    /// to a valid signature.</para>
    /// </remarks>
    [Fact]
    public void AWidgetUnderAnAlreadySignedFieldIsNotAdoptedInItsPlace()
    {
        byte[] once = Sign(NestedFieldPdf(splitWidget: true), Qualified);
        byte[] twice = Sign(once, Qualified);

        using var ms = new MemoryStream(twice);
        using var doc = PdfDocument.Open(ms);

        // Object 6 is the field and object 7 is its widget. The second signature must be a NEW
        // field, not the widget wearing a /V.
        var widget = (PdfDictionary)doc.GetObject(7);
        Assert.Null(widget[PdfName.V]);
        Assert.Equal(PdfName.Widget.Value, (widget[PdfName.Subtype] as PdfName)?.Value);

        // Two signatures, and the first one still verifies — the widget it draws was not rewritten.
        Assert.Equal(2, doc.AcroForm!.GetSignatureFields().Count(f => f.HasSignature));
        Assert.True(Verify(twice, Leaf).IsIntegrityValid);
    }

    /// <summary>
    /// A nested field that is already signed is a real collision and is renamed around, never
    /// overwritten.
    /// </summary>
    [Fact]
    public void ASignedNestedFieldIsStillACollision()
    {
        byte[] once = Sign(NestedFieldPdf(), Qualified);
        byte[] twice = Sign(once, Qualified);

        using var ms = new MemoryStream(twice);
        using var doc = PdfDocument.Open(ms);

        var signedFields = doc.AcroForm!.GetSignatureFields().Where(f => f.HasSignature).ToList();
        Assert.Equal(2, signedFields.Count);

        // The first signature is untouched — an incremental update cannot have rewritten it.
        Assert.True(Verify(twice, Leaf).IsIntegrityValid);
    }

    /// <summary>
    /// The leaf name alone does not adopt: §12.7.3.2 makes the fully qualified name the identity.
    /// </summary>
    /// <remarks>
    /// A leaf is not unique — two parents may each hold an <c>approval</c> — so matching on it would
    /// adopt whichever the walk reached first. `SignatureFieldRegistration.FieldNames` still adds the leaf to the
    /// collision set, deliberately: a name that is ambiguous is one a new field must not take, so the
    /// signer renames itself rather than adopting.
    /// </remarks>
    [Fact]
    public void TheLeafNameAloneDoesNotAdopt()
    {
        byte[] signed = Sign(NestedFieldPdf(), Leaf);

        using var ms = new MemoryStream(signed);
        using var doc = PdfDocument.Open(ms);

        var fields = doc.AcroForm!.GetSignatureFields().ToList();
        Assert.Single(fields.Where(f => f.HasSignature));            // the invented one
        Assert.Contains(fields, f => f.FieldName == Leaf && !f.HasSignature);  // still unsigned
        Assert.Contains(fields, f => f.HasSignature && f.FieldName != Leaf);
    }

    /// <summary>
    /// A <c>/Kids</c> graph that branches back on itself terminates instead of spinning, and the
    /// document still signs.
    /// </summary>
    /// <remarks>
    /// <para><b>The fixture branches, and a mutation is why.</b> A two-node cycle is bounded by the
    /// depth cap alone — 100 levels of one branch is nothing — so removing the visited set left the
    /// first version of this case green. <b>A depth cap bounds one branch, never the number of
    /// branches.</b> Here each node lists the other <i>twice</i>, so the work doubles per level:
    /// with the visited set it is two nodes, and without it 2^100.</para>
    ///
    /// <para>The assertion is the guard's own evidence — the call returns and produces a signed
    /// document — never a wall clock, which would pass on a fast enough machine and is the shape
    /// CLAUDE.md bans. Its potency is the mutation, not the elapsed time: with the visited set
    /// removed this does not return.</para>
    /// </remarks>
    [Fact]
    public void ABranchingKidsCycleDoesNotHangTheAdoptionWalk()
    {
        byte[] signed = Sign(NestedFieldPdf(kidsCycle: true), "nothing-here");

        using var ms = new MemoryStream(signed);
        using var doc = PdfDocument.Open(ms);

        Assert.Contains(doc.AcroForm!.GetSignatureFields(), f => f.HasSignature);
    }

    // ----------------------------------------------------------------

    /// <summary>The nested field object, resolved from the parent's <c>/Kids</c>.</summary>
    private static PdfDictionary NestedField(PdfDocument doc)
    {
        var acroForm = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]);
        var roots = (PdfArray)doc.ResolveObject(acroForm[PdfName.Fields]);
        var parent = (PdfDictionary)doc.ResolveObject(roots[0]);
        var kids = (PdfArray)doc.ResolveObject(parent[PdfName.Kids]);
        return (PdfDictionary)doc.ResolveObject(kids[0]);
    }

    /// <summary>
    /// A one-page document whose <c>/AcroForm /Fields</c> holds a single parent node, with the
    /// signature field as its kid.
    /// </summary>
    /// <param name="fieldTypeOnParent">Put <c>/FT /Sig</c> on the parent, so the kid inherits it.</param>
    /// <param name="splitWidget">Give the field a separate widget kid of its own.</param>
    /// <param name="kidsCycle">Point the kid's <c>/Kids</c> back at its parent.</param>
    private static byte[] NestedFieldPdf(
        bool fieldTypeOnParent = false, bool splitWidget = false, bool kidsCycle = false)
    {
        string parentType = fieldTypeOnParent ? " /FT /Sig" : "";
        string leafType = fieldTypeOnParent ? "" : " /FT /Sig";

        // The cycle branches: listing the parent twice doubles the work at every level, so the
        // depth cap alone does not bound it. See ABranchingKidsCycleDoesNotHangTheAdoptionWalk.
        string leafKids = splitWidget ? " /Kids [7 0 R]"
                        : kidsCycle ? " /Kids [5 0 R 5 0 R]"
                        : "";
        string leafAnnot = splitWidget
            ? ""
            : " /Type /Annot /Subtype /Widget /Rect [50 60 250 140] /P 3 0 R /AP << /N 8 0 R >>";

        var objects = new (int num, string body)[]
        {
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /SigFlags 3 >> >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Annots [" + (splitWidget ? "7 0 R" : "6 0 R") + "] >>"),
            (4, "<< /Length 44 >>\nstream\nBT /F1 12 Tf 72 720 Td (Stamped) Tj ET\nendstream"),
            (5, $"<< /T ({Parent}){parentType} /Kids [6 0 R{(kidsCycle ? " 6 0 R" : "")}] >>"),
            (6, $"<< /T ({Leaf}){leafType}{leafKids}{leafAnnot} /Parent 5 0 R >>"),
            (7, "<< /Type /Annot /Subtype /Widget /Rect [50 60 250 140] /P 3 0 R " +
                "/Parent 6 0 R /AP << /N 8 0 R >> >>"),
            (8, "<< /Type /XObject /Subtype /Form /BBox [0 0 200 80] /Length 32 >>\n" +
                "stream\n1 0 0 RG 2 w 5 5 190 70 re S\nendstream"),
        };

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
        var root = SigningTestCrypto.Ca("CN=Nested Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Nested Signer");
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

    private static SignatureVerificationResult Verify(byte[] pdf, string fieldName)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return new PdfSignatureVerifier().Verify(doc, fieldName);
    }
}
