// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// Two reads in <c>PdfSigner</c> that did not follow an indirect reference, each in a method whose
/// neighbouring read on the line above does.
/// </summary>
/// <remarks>
/// <para><b><c>/Fields</c> in <c>FindAdoptableSignatureField</c> silently changes which field gets
/// signed.</b> A null means "no adoptable field", so the caller takes the <i>rename</i> branch
/// instead — and <c>SignatureFieldRegistration.FieldNames</c> reads <c>/Fields</c> through the resolved path, so the
/// wanted name is still found and still collides. The result is that the named, empty signature
/// field the document provided is left <b>permanently unsigned</b>, while a fresh invisible field
/// carries the <c>/V</c>. The document is signed; it is just not signed where it asked to be.</para>
///
/// <para><b><c>/Kids</c> costs the widget its only link to a page.</b> <c>/Root</c> and
/// <c>/Pages</c> are both resolved two lines above it. A null leaves <c>firstPageObjNum</c> at 0,
/// so the widget is written with no <c>/P</c> — and <c>PdfSigner</c> never adds it to any page's
/// <c>/Annots</c>, so <c>/P</c> was the only link there was. An orphaned widget: a broken signature
/// panel entry and a PDF/UA widget-without-<c>/P</c>, while the signature itself still
/// verifies.</para>
///
/// <para><b>Asserted on which object carries the <c>/V</c>, and on the widget's <c>/P</c>.</b> Both
/// failures produce a valid, correctly-signed PDF — <c>IsValid</c> is true either way, and so is
/// "the output is bigger than the input". Only the structure separates them.</para>
/// </remarks>
public sealed class IndirectSignerReadTests
{
    /// <summary>
    /// Signing adopts the empty signature field the document already had, rather than renaming
    /// around it — whichever way <c>/Fields</c> is spelled. <c>false</c> is the control.
    /// </summary>
    [Theory]
    [InlineData(false)]  // /Fields [5 0 R]
    [InlineData(true)]   // /Fields 7 0 R
    public void SigningAdoptsTheExistingField_WhicheverWayFieldsIsSpelled(bool indirect)
    {
        byte[] signed = Sign(FormPdf(indirect), "Signature1");

        using var doc = PdfDocument.Open(new MemoryStream(signed));
        List<PdfDictionary> fields = SignatureFields(doc);

        // Exactly one field named Signature1, and it is the one carrying the signature value.
        var named = fields.Where(f => Text(doc, f, PdfName.T) == "Signature1").ToList();
        Assert.Single(named);
        Assert.NotNull(Resolve(doc, named[0][PdfName.V]));

        // The rename branch is what the defect took, and it mints "Signature1_2" or similar.
        Assert.DoesNotContain(fields, f =>
        {
            string t = Text(doc, f, PdfName.T);
            return t != null && t != "Signature1" && t.StartsWith("Signature1", StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// The signature widget names its page whichever way <c>/Kids</c> is spelled.
    /// </summary>
    /// <remarks>
    /// <c>/P</c> is the widget's only link to a page here — <c>PdfSigner</c> never appends it to a
    /// page's <c>/Annots</c> — so its absence orphans the widget outright.
    /// </remarks>
    [Theory]
    [InlineData(false)]  // /Kids [3 0 R]
    [InlineData(true)]   // /Kids 8 0 R
    public void TheWidgetNamesItsPage_WhicheverWayKidsIsSpelled(bool indirect)
    {
        // No AcroForm, so signing MINTS the field rather than adopting one. That matters: an
        // adopted field brings its own /P, and the first version of this test used the form
        // fixture — where the mutation survived, because firstPageObjNum was never consulted.
        byte[] signed = Sign(NoFormPdf(kidsIndirect: indirect), "Signature1");

        using var doc = PdfDocument.Open(new MemoryStream(signed));
        var withValue = SignatureFields(doc)
            .FirstOrDefault(f => Resolve(doc, f[PdfName.V]) != null);
        Assert.NotNull(withValue);

        // Which page, not merely "some object". The build-failing pattern-3 guard rejected the
        // first version of this assertion — `Assert.NotNull(…[/P])` — and was right to: a /P
        // pointing anywhere at all would satisfy it, and the defect writes no /P rather than a
        // wrong one, so the weaker form happened to work while claiming something it never checked.
        var target = Resolve(doc, withValue![PdfName.P]) as PdfDictionary;
        Assert.NotNull(target);
        Assert.Equal(doc.Pages[0].Dictionary.ObjectNumber, target!.ObjectNumber);
        Assert.Equal("Page", (Resolve(doc, target[PdfName.Type]) as PdfName)?.Value);
    }

    // ---------------------------------------------------------------- helpers

    private static PdfObject Resolve(PdfDocument doc, PdfObject o) =>
        o is PdfReference ? doc.ResolveObject(o) : o;

    private static string Text(PdfDocument doc, PdfDictionary d, PdfName key) =>
        (Resolve(doc, d[key]) as PdfString)?.GetText();

    /// <summary>Every terminal field in the signed output's AcroForm, walking <c>/Kids</c>.</summary>
    private static List<PdfDictionary> SignatureFields(PdfDocument doc)
    {
        var found = new List<PdfDictionary>();
        var acro = Resolve(doc, doc.Catalog!.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        Assert.NotNull(acro);

        Walk(doc, Resolve(doc, acro![PdfName.Fields]) as PdfArray, found, 0);
        return found;
    }

    private static void Walk(PdfDocument doc, PdfArray fields, List<PdfDictionary> found, int depth)
    {
        if (fields == null || depth > 8) return;
        for (int i = 0; i < fields.Count; i++)
        {
            if (Resolve(doc, fields[i]) is not PdfDictionary field) continue;
            found.Add(field);
            Walk(doc, Resolve(doc, field[PdfName.Kids]) as PdfArray, found, depth + 1);
        }
    }

    private static byte[] Sign(byte[] pdf, string fieldName)
    {
        var root = SigningTestCrypto.Ca("CN=Signer Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Signer Leaf");
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

    /// <summary>
    /// A form holding one <b>empty signature field</b> named <c>Signature1</c> — the field signing
    /// is meant to adopt. Object 7 is the <c>/Fields</c> array and object 8 the page-tree
    /// <c>/Kids</c>; the flags select which one the catalog points at rather than writing inline.
    /// </summary>
    private static byte[] FormPdf(bool indirect, bool kidsIndirect = false)
    {
        string fields = indirect ? "7 0 R" : "[5 0 R]";
        string kids = kidsIndirect ? "8 0 R" : "[3 0 R]";

        return SignedPdfFixtures.Assemble(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields " + fields + " /SigFlags 3 >> >>",
            "<< /Type /Pages /Kids " + kids + " /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [5 0 R] >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1)"
                + " /Rect [100 100 300 150] /F 4 /P 3 0 R >>",
            "null",
            "[5 0 R]",
            "[3 0 R]");
    }

    /// <summary>
    /// A bare one-page document with <b>no</b> <c>/AcroForm</c>, so signing mints the field and its
    /// widget from nothing — the only route by which <c>firstPageObjNum</c>, and therefore
    /// <c>/Kids</c>, decides the widget's <c>/P</c>. Object 8 holds the kids array when
    /// <paramref name="kidsIndirect"/>.
    /// </summary>
    private static byte[] NoFormPdf(bool kidsIndirect)
    {
        string kids = kidsIndirect ? "8 0 R" : "[3 0 R]";

        return SignedPdfFixtures.Assemble(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids " + kids + " /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "null", "null", "null",
            "[3 0 R]");
    }
}
