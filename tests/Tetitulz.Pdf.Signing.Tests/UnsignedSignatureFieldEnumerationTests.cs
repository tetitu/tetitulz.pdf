// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Signing;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <see cref="PdfSigningOperations.GetUnsignedSignatureFieldNames"/> — the names a caller can pass
/// as <c>SignatureParameters.SignatureFieldName</c> to sign into a field the document already has.
/// </summary>
/// <remarks>
/// <para>The façade delegates to <c>PdfAcroForm.GetSignatureFields()</c> rather than walking the
/// field tree again, so these tests are about the <b>filter and the reported name</b>, not about
/// traversal — that walk has its own depth cap and cycle guard and its own tests.</para>
///
/// <para><b>The signed-field case is the one that matters.</b> Returning every signature field
/// would look correct on a document that has one empty field, which is what a naive fixture has;
/// it is only a document carrying both that separates "unsigned fields" from "signature
/// fields".</para>
/// </remarks>
public class UnsignedSignatureFieldEnumerationTests
{
    [Fact]
    public void AnEmptySignatureFieldIsListed()
    {
        var names = Enumerate(FormPdf(("Signature1", false)));
        Assert.Equal(new[] { "Signature1" }, names);
    }

    /// <summary>A field carrying a <c>/V</c> is signed, and must not be offered as available.</summary>
    [Fact]
    public void AFieldThatAlreadyCarriesAValueIsNotListed()
    {
        var names = Enumerate(FormPdf(("Signed", true), ("Empty", false)));
        Assert.Equal(new[] { "Empty" }, names);
    }

    /// <summary>A document with no <c>/AcroForm</c> at all yields nothing rather than throwing.</summary>
    [Fact]
    public void ADocumentWithNoAcroFormYieldsNothing()
    {
        Assert.Empty(Enumerate(NoFormPdf()));
    }

    /// <summary>The stream is left open and repositioned, so a caller can go on to sign it.</summary>
    [Fact]
    public void TheInputStreamIsUsableAfterwards()
    {
        using var ms = new MemoryStream(FormPdf(("Signature1", false)), writable: false);
        ms.Position = 3;
        PdfSigningOperations.GetUnsignedSignatureFieldNames(ms);

        Assert.True(ms.CanRead);
        Assert.Equal(3, ms.Position);
    }

    /// <summary>
    /// A field whose widget is a SEPARATE kid must be listed once, not once per widget.
    /// </summary>
    /// <remarks>
    /// The fixtures above merge field and widget into one dictionary, which is the common shape and
    /// the one that hides this: with a separate widget kid, the kid inherits <c>/FT /Sig</c> through
    /// <c>/Parent</c> (Table 220), so <c>GetSignatureFields</c> hands it back as a signature field
    /// too - and it carries no <c>/T</c>, so its fully qualified name is its parent's. This method's
    /// own remarks call that name "the identity that is required to be unique".
    /// </remarks>
    [Fact]
    public void AFieldWithASeparateWidgetKidIsListedOnce()
    {
        var names = Enumerate(FieldWithSeparateWidget("Signature1"));
        Assert.Equal(new[] { "Signature1" }, names);
    }

    /// <summary>
    /// A grouping node holding <c>/FT /Sig</c> for its kids is not itself signable, so only the
    /// terminal field beneath it is offered.
    /// </summary>
    [Fact]
    public void AGroupingParentIsNotOfferedAsASignableField()
    {
        var names = Enumerate(SignatureGroup("group", "inner"));
        Assert.Equal(new[] { "group.inner" }, names);
    }

    /// <summary>Field 4 carries the name; widget 5 is a separate kid with no <c>/T</c>.</summary>
    private static byte[] FieldWithSeparateWidget(string name)
    {
        var b = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string s) { offsets.Add(b.Length); b.Append(s); }

        b.Append("%PDF-1.7" + N);
        Obj("1 0 obj" + N + "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>" + N + "endobj" + N);
        Obj("2 0 obj" + N + "<< /Type /Pages /Kids [3 0 R] /Count 1 >>" + N + "endobj" + N);
        Obj("3 0 obj" + N + "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [5 0 R] >>" + N + "endobj" + N);
        Obj("4 0 obj" + N + $"<< /FT /Sig /T ({name}) /Kids [5 0 R] >>" + N + "endobj" + N);
        Obj("5 0 obj" + N + "<< /Type /Annot /Subtype /Widget /Parent 4 0 R /Rect [0 0 100 50] >>" + N + "endobj" + N);
        return Finish(b, offsets);
    }

    /// <summary>A group (4) holding <c>/FT</c> for one real terminal signature field (5).</summary>
    private static byte[] SignatureGroup(string group, string inner)
    {
        var b = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string s) { offsets.Add(b.Length); b.Append(s); }

        b.Append("%PDF-1.7" + N);
        Obj("1 0 obj" + N + "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>" + N + "endobj" + N);
        Obj("2 0 obj" + N + "<< /Type /Pages /Kids [3 0 R] /Count 1 >>" + N + "endobj" + N);
        Obj("3 0 obj" + N + "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [5 0 R] >>" + N + "endobj" + N);
        Obj("4 0 obj" + N + $"<< /FT /Sig /T ({group}) /Kids [5 0 R] >>" + N + "endobj" + N);
        Obj("5 0 obj" + N + $"<< /Type /Annot /Subtype /Widget /Parent 4 0 R /T ({inner}) /Rect [0 0 100 50] >>" + N + "endobj" + N);
        return Finish(b, offsets);
    }

    /// <summary>The one newline these hand-built fixtures use.</summary>
    private const string N = "\n";

    private static IReadOnlyList<string> Enumerate(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        return PdfSigningOperations.GetUnsignedSignatureFieldNames(ms);
    }

    /// <summary>A document whose /AcroForm carries one top-level /Sig field per tuple.</summary>
    private static byte[] FormPdf(params (string name, bool signed)[] fields)
    {
        var b = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string s) { offsets.Add(b.Length); b.Append(s); }

        b.Append("%PDF-1.7\n");
        int firstField = 4;
        string refs = string.Join(" ", Enumerable.Range(0, fields.Length)
                                                 .Select(i => $"{firstField + i} 0 R"));

        Obj($"1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [{refs}] >> >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [{refs}] >>\nendobj\n");

        foreach (var (name, signed) in fields)
        {
            // A /V is what makes a signature field signed; its contents are irrelevant here.
            string v = signed
                ? " /V << /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached >>"
                : string.Empty;
            Obj($"{offsets.Count + 1} 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Sig "
                + $"/T ({name}) /Rect [0 0 100 50]{v} >>\nendobj\n");
        }

        return Finish(b, offsets);
    }

    private static byte[] NoFormPdf()
    {
        var b = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string s) { offsets.Add(b.Length); b.Append(s); }
        b.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        return Finish(b, offsets);
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
    /// A field with an empty name has no name a caller can pass back to a signer, so it is not
    /// listed; the named field beside it still is.
    /// </summary>
    [Fact]
    public void AnUnsignedFieldWithAnEmptyName_IsNotListed()
    {
        var names = Enumerate(FormPdf(("", false), ("Ok", false)));

        Assert.Equal(new[] { "Ok" }, names);
    }
}
