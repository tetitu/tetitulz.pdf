// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// Signing a document whose <c>/AcroForm /Fields</c> is an <b>indirect reference</b> must not
/// destroy the fields already in it.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> <c>PdfSigner</c> read the array with
/// <c>acroFormDict.GetArray(PdfName.Fields)</c>, which tests the <i>raw</i> value's type and does
/// not follow a reference — while resolving the <c>/AcroForm</c> itself on the line above. §7.3.10
/// permits <c>/Fields 12 0 R</c>, and for such a document the read returned <see langword="null"/>,
/// indistinguishable from an empty form.</para>
///
/// <para><b>Why that is destruction rather than omission.</b> The collected list <i>becomes</i> the
/// whole <c>/Fields</c> array of the appended revision. Empty in, so the revision declares a form
/// containing only the new signature — and because it supersedes the original <c>/AcroForm</c>
/// object, <b>every field the document already had is gone</b>, inside a revision a signature now
/// covers. Three sibling sites in this assembly had the identical shape:
/// <c>PdfDocumentTimestamper</c> (a document timestamp deleted the form) and
/// <c>VisualSignatureBuilder</c> twice — the second on <c>/Annots</c>, so adding a visible
/// signature erased every link and comment on the page.</para>
///
/// <para><b>The assertion is the surviving field, not the byte count.</b> A signed file that lost
/// its form is a perfectly valid, larger PDF with a real signature in it; asserting "output is
/// non-empty" or "output is bigger than input" passes with the defect present.</para>
/// </remarks>
public sealed class IndirectFieldsArrayTests
{
    /// <summary>
    /// The pre-existing field survives, whichever way <c>/Fields</c> is spelled. The direct
    /// spelling is the control: it always worked, so a fixture built on it could never go red.
    /// </summary>
    [Theory]
    [InlineData(true)]   // /Fields 6 0 R
    [InlineData(false)]  // /Fields [5 0 R]
    public void SigningKeepsTheExistingField_WhicheverWayFieldsIsSpelled(bool indirect)
    {
        byte[] signed = Sign(FormPdf(indirect));

        List<string> names = FieldNames(signed);

        Assert.Contains("Existing", names);      // the field the document already had
        Assert.Contains("Signature1", names);    // and the one signing added
    }

    // ------------------------------------------------------------------

    private static byte[] Sign(byte[] pdf)
    {
        var root = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(root);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, root.Cert);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = new MemoryStream(pfx),
            CertificatePassword = pw,
            SignatureFieldName = "Signature1",
        });
        return output.ToArray();
    }

    /// <summary>Every <c>/T</c> in the signed output's AcroForm, walking <c>/Kids</c>.</summary>
    private static List<string> FieldNames(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var names = new List<string>();

        var acroDict = doc.ResolveObject(doc.Catalog!.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        Assert.NotNull(acroDict);

        // Resolved here too — the signer may legitimately write /Fields either way, and reading it
        // the way the defect did would make this helper agree with the bug.
        var fields = doc.ResolveObject(acroDict![PdfName.Fields]) as PdfArray;
        Walk(doc, fields, names, 0);
        return names;
    }

    private static void Walk(PdfDocument doc, PdfArray? fields, List<string> names, int depth)
    {
        if (fields == null || depth > 8) return;
        for (int i = 0; i < fields.Count; i++)
        {
            if (doc.ResolveObject(fields[i]) is not PdfDictionary field) continue;
            string? t = field.GetString(PdfName.T)?.GetText();
            if (t != null) names.Add(t);
            Walk(doc, doc.ResolveObject(field[PdfName.Kids]) as PdfArray, names, depth + 1);
        }
    }

    /// <summary>
    /// A form holding one text field. When <paramref name="indirect"/> the <c>/Fields</c> array is
    /// object 6 and the AcroForm points at it, which is the only difference between the two.
    /// </summary>
    private static byte[] FormPdf(bool indirect)
    {
        string fields = indirect ? "6 0 R" : "[5 0 R]";

        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj($"1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields {fields} /SigFlags 3 >> >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\nendobj\n");
        Obj("4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Tx /T (Existing) /Rect [0 0 10 10] >>\nendobj\n");
        Obj("6 0 obj\n[5 0 R]\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
