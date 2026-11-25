// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Forms;

/// <summary>
/// <c>PdfFormField.Create</c> and <c>PdfFormField.FieldType</c> must answer the same question the
/// same way (§12.7.3.1, Table 220).
/// </summary>
/// <remarks>
/// <para><b>They did not.</b> <c>Create</c> walked the <c>/Parent</c> chain by hand and omitted the
/// downward step <c>FieldType</c> applies — the <c>/FT</c> a widget kid declares when the chain
/// declares none. So for a signature written as
/// <c>&lt;&lt; /T (sig) /V 8 0 R /Kids [widget carrying /FT /Sig] &gt;&gt;</c>,
/// <c>field.FieldType</c> said <c>Signature</c> while <c>Create</c> built a plain
/// <c>PdfFormField</c>.</para>
///
/// <para><b>The consequence is a signed document reported as unsigned.</b>
/// <c>PdfAcroForm.GetSignatureFields</c> type-tests, so it skipped the field and returned the
/// <i>widget</i> that carries the <c>/FT</c>. The widget has no <c>/V</c> — deliberately, since a
/// widget showing its parent's value is not a second signature — so <c>HasSignature</c> was false
/// and <c>SignatureFieldWalk.DistinctSignatures</c> yielded nothing. The signature listing, the
/// revision attribution and <c>DocMdpAnalyzer</c> all saw an unsigned file.</para>
///
/// <para>The assertions are on <c>HasSignature</c> and the signature dictionary's own entries, not
/// on the list being non-empty: the list was never empty — it held the wrong node.</para>
/// </remarks>
public class InheritedFieldTypeTests
{
    [Fact]
    public void ASignatureTypedOnItsWidgetIsFoundWithItsValue()
    {
        using var ms = new MemoryStream(SignatureGroupPdf());
        using var doc = PdfDocument.Open(ms);

        PdfFormField group = doc.AcroForm.Fields.Single();

        // The two answers agree.
        Assert.Equal(PdfFieldType.Signature, group.FieldType);
        Assert.IsType<PdfSignatureField>(group);

        // And the one that carries /V is the one the walk hands back.
        PdfSignatureField found = doc.AcroForm.GetSignatureFields()
            .Single(f => f.HasSignature);

        Assert.Equal("sig", found.FieldName);
        Assert.Equal("Adobe.PPKLite", found.Filter);
        Assert.Equal("adbe.pkcs7.detached", found.SubFilter);
    }

    /// <summary>
    /// The control: the ordinary shape, where <c>/FT</c> is on the field itself. Without it the
    /// test above would pass with the downward step applied to everything.
    /// </summary>
    [Fact]
    public void ASignatureTypedOnTheFieldIsUnaffected()
    {
        using var ms = new MemoryStream(SignatureOnFieldPdf());
        using var doc = PdfDocument.Open(ms);

        PdfFormField field = doc.AcroForm.Fields.Single();

        Assert.Equal(PdfFieldType.Signature, field.FieldType);
        Assert.IsType<PdfSignatureField>(field);
        Assert.True(((PdfSignatureField)field).HasSignature);
    }

    /// <summary>
    /// Widget kids that declare <i>different</i> types leave the field <c>Unknown</c> rather than
    /// picking one — and <c>Create</c> must agree, or the two ladders have parted again.
    /// </summary>
    [Fact]
    public void DisagreeingWidgetKidsLeaveTheTypeUnknown()
    {
        using var ms = new MemoryStream(AmbiguousKidsPdf());
        using var doc = PdfDocument.Open(ms);

        PdfFormField group = doc.AcroForm.Fields.Single();

        Assert.Equal(PdfFieldType.Unknown, group.FieldType);
        Assert.IsNotType<PdfSignatureField>(group);
    }

    /// <summary>
    /// The inheritance climbs are <b>capped</b>, and an unbounded <c>Parent</c> chain is reachable
    /// from public API: <c>Kids</c> materialises a fresh wrapper per read, so descending a
    /// <c>/Kids</c> cycle builds one as deep as the caller cares to go. A climb recursing over that
    /// would be a <c>StackOverflowException</c> the caller cannot catch.
    /// </summary>
    /// <remarks>
    /// <b>The assertion is the cap's own evidence, not a wall clock.</b> Only the root carries
    /// <c>/FT</c> and <c>/Ff</c>, so a climb that reaches it answers <c>Text</c> and 7 while one
    /// that stops short answers <c>Unknown</c> and 0. Descending <i>within</i> the cap is the
    /// control: without it, "Unknown" would also be what a climb that never ran returns.
    /// </remarks>
    [Theory]
    [InlineData(3, PdfFieldType.Text, 7)]
    [InlineData(400, PdfFieldType.Unknown, 0)]
    public void TheInheritanceClimbStopsAtItsCap(int descend, PdfFieldType expected, int expectedFlags)
    {
        using var ms = new MemoryStream(KidsCyclePdf());
        using var doc = PdfDocument.Open(ms);

        PdfFormField node = doc.AcroForm.Fields.Single();
        for (int i = 0; i < descend && node.Kids.Count > 0; i++) node = node.Kids[0];

        Assert.Equal(expected, node.FieldType);
        Assert.Equal(expectedFlags, node.Flags);
    }

    // ------------------------------------------------------------------

    /// <summary>The group carries <c>/T</c> and <c>/V</c>; its only widget kid carries <c>/FT</c>.</summary>
    private static byte[] SignatureGroupPdf() => Build(
        Catalog(), PageTree(), Page("7 0 R"), EmptyStream(), Resources(),
        "<< /T (sig) /V 8 0 R /Kids [7 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /FT /Sig /Rect [100 700 300 720] >>",
        SignatureDictionary());

    /// <summary>The ordinary shape: a merged field/widget carrying its own <c>/FT</c>.</summary>
    private static byte[] SignatureOnFieldPdf() => Build(
        Catalog(), PageTree(), Page("6 0 R"), EmptyStream(), Resources(),
        "<< /Type /Annot /Subtype /Widget /FT /Sig /T (sig) /V 7 0 R /Rect [100 700 300 720] >>",
        SignatureDictionary());

    /// <summary>Two widget kids, two different <c>/FT</c>s.</summary>
    private static byte[] AmbiguousKidsPdf() => Build(
        Catalog(), PageTree(), Page("7 0 R"), EmptyStream(), Resources(),
        "<< /T (mixed) /Kids [7 0 R 8 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /FT /Sig /Rect [10 10 20 20] >>",
        "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /FT /Tx /Rect [30 10 40 20] >>");

    /// <summary>
    /// Object 6 carries the inheritable entries; object 7 names <b>itself</b> in <c>/Kids</c>, so
    /// descending builds a <c>Parent</c> chain of any depth in which only the last node answers.
    /// </summary>
    /// <remarks>
    /// <b>Object 7 deliberately carries no <c>/Parent</c>, and putting one back breaks the cap
    /// test rather than improving the fixture.</b> With <c>/Parent 6 0 R</c> on it — which is what
    /// stood here — object 6 is <b>one hop</b> from every descendant however deep the descent went,
    /// so any resolver that follows the <c>/Parent</c> key answers <c>Text</c> at depth 400 and the
    /// <c>(400, Unknown, 0)</c> case goes red. That would look like the cap failing when what
    /// actually changed is which chain is walked.
    ///
    /// <para>The two are separate claims and this fixture isolates the first: with no
    /// <c>/Parent</c> anywhere, the descent chain is the only chain, so the case measures the cap
    /// and nothing else. <c>/Parent</c>-versus-descent belongs to its own fixtures, which assert
    /// on shapes where the two chains reach <i>different</i> nodes.</para>
    ///
    /// <para>Being <c>/Parent</c>-free makes the fixture more malformed, not less — Table 220 marks
    /// <c>/Parent</c> Required on any field that is another's child. That is fine here and is the
    /// point: this file is already deliberately malformed by its <c>/Kids</c> self-cycle, and a cap
    /// exists precisely for input no conformant producer emits.</para>
    /// </remarks>
    private static byte[] KidsCyclePdf() => Build(
        Catalog(), PageTree(), Page("7 0 R"), EmptyStream(), Resources(),
        "<< /T (root) /FT /Tx /Ff 7 /Kids [7 0 R] >>",
        "<< /T (loop) /Kids [7 0 R] >>");

    private static string Catalog() =>
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>";
    private static string PageTree() => "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
    private static string Page(string annot) =>
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources 5 0 R " +
        "/Annots [" + annot + "] >>";
    private static string EmptyStream() => "<< /Length 0 >>\nstream\n\nendstream";
    private static string Resources() => "<< >>";
    private static string SignatureDictionary() =>
        "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached " +
        "/ByteRange [0 100 200 300] /Contents <00> >>";

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
