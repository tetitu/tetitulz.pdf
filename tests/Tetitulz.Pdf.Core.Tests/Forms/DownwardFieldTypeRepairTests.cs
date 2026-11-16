// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Forms;

/// <summary>
/// <c>PdfFormField.ResolveFieldType</c> reads <c>/FT</c> off a kid when the ancestor chain declares
/// none. That step is a <b>repair</b> for a producer that put a terminal field's Required
/// <c>/FT</c> on its merged widget — it is not inheritance, which §12.7.3.1 flows
/// ancestor→descendant only — and it must therefore look at <b>widgets</b>, never at child fields.
/// </summary>
/// <remarks>
/// <para><b>It had no gate, so a grouping node took its type from its own children.</b> A node
/// whose kids are child <i>fields</i> typed <c>/Sig</c> was itself classified <c>Signature</c>, and
/// <c>PdfFormField.Create</c> built it as a <c>PdfSignatureField</c> — one with no <c>/V</c> of its
/// own, because the values live on the children. <c>PdfAcroForm.GetSignatureFields</c> collects by
/// type, so the document reported <b>three</b> signature fields where it has two: a phantom beside
/// the real ones.</para>
///
/// <para><b>Every existing fixture stays green under the fix</b>, which is why this was never
/// noticed: the shapes in the tree all put <c>/FT</c> on a bare widget, where the repair is
/// correct and the gate changes nothing. Only a group over typed child fields separates them, and
/// nothing built one.</para>
///
/// <para>The file here is <b>valid</b>. §12.7.3.1 permits a non-terminal field with typed
/// children, and Table 220 marks <c>/FT</c> "Required for terminal fields" — which these children
/// are and their parent is not.</para>
/// </remarks>
public class DownwardFieldTypeRepairTests
{
    /// <summary>
    /// Two signature child fields under an untyped group. The group is not a signature field.
    /// </summary>
    [Fact]
    public void AGroupOverTypedChildFieldsDoesNotTakeTheirType()
    {
        using var ms = new MemoryStream(GroupOverTwoSignatureFields());
        using var doc = PdfDocument.Open(ms);

        PdfFormField group = doc.AcroForm.Fields.Single();

        Assert.Equal(PdfFieldType.Unknown, group.FieldType);
        Assert.IsNotType<PdfSignatureField>(group);
    }

    /// <summary>
    /// And the signature listing reports the two fields that actually carry values, not three.
    /// </summary>
    /// <remarks>
    /// This is the consequence, and it is the assertion that matters: the phantom is value-less, so
    /// a caller filtering on <c>HasSignature</c> never saw it, while one enumerating
    /// <c>GetSignatureFields()</c> — to count them, to attribute revisions, to build a panel — did.
    /// </remarks>
    [Fact]
    public void TheSignatureListingHoldsOnlyTheFieldsThatCarryASignature()
    {
        using var ms = new MemoryStream(GroupOverTwoSignatureFields());
        using var doc = PdfDocument.Open(ms);

        List<PdfSignatureField> sigs = doc.AcroForm.GetSignatureFields();

        Assert.Equal(2, sigs.Count);
        Assert.All(sigs, s => Assert.True(s.HasSignature));
        Assert.Equal(new[] { "first", "second" }, sigs.Select(s => s.FieldName).OrderBy(n => n));
    }

    /// <summary>
    /// The control: the repair still fires for a <b>bare widget</b> kid, which is the shape it
    /// exists for.
    /// </summary>
    /// <remarks>
    /// Without this, gating the step out entirely satisfies both cases above — and would undo the
    /// fix that stopped a signed document being reported as unsigned
    /// (<see cref="InheritedFieldTypeTests"/>).
    /// </remarks>
    [Fact]
    public void AWidgetKidsTypeIsStillReadWhenTheChainDeclaresNone()
    {
        using var ms = new MemoryStream(GroupOverOneTypedWidget());
        using var doc = PdfDocument.Open(ms);

        PdfFormField group = doc.AcroForm.Fields.Single();

        Assert.Equal(PdfFieldType.Signature, group.FieldType);
        Assert.IsType<PdfSignatureField>(group);
    }

    /// <summary>
    /// A climb stopped by its <b>cap</b> must answer <c>Unknown</c>, not fall through into the
    /// downward repair and take a widget kid's type.
    /// </summary>
    /// <remarks>
    /// <para>The up-loop ends two ways and only one of them may continue: the chain genuinely
    /// running out is the case the repair exists for; the cap stopping it with ancestors still
    /// unread is not. Control dropped straight through, so a bounded-work refusal became a
    /// <b>confidently wrong answer</b> — this file's root says <c>/FT /Sig</c> and the widget says
    /// <c>/FT /Tx</c>, and past the cap the signature field reported itself as text with nothing
    /// anywhere recording that the climb had been cut short.</para>
    ///
    /// <para>The within-cap control is what makes the case falsifiable: at depth 3 the climb
    /// reaches the root and must still answer <c>Signature</c>. Without it, "always return
    /// <c>Unknown</c>" satisfies the deep case.</para>
    /// </remarks>
    [Theory]
    [InlineData(3, PdfFieldType.Signature)]
    [InlineData(400, PdfFieldType.Unknown)]
    public void ACapExhaustedClimbDoesNotTakeAWidgetKidsType(int descend, PdfFieldType expected)
    {
        using var ms = new MemoryStream(DeepChainWithATypedWidget());
        using var doc = PdfDocument.Open(ms);

        PdfFormField node = doc.AcroForm.Fields.Single();
        for (int i = 0; i < descend && node.Kids.Count > 0; i++) node = node.Kids[0];

        Assert.Equal(expected, node.FieldType);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Object 6 carries <c>/FT /Sig</c>; object 7 names <b>itself</b> first in <c>/Kids</c> so a
    /// descent builds a chain of any depth, and names a bare widget carrying <c>/FT /Tx</c> second
    /// — the kid the downward repair would read.
    /// </summary>
    private static byte[] DeepChainWithATypedWidget() => Build(
        Catalog(), PageTree(), Page(), EmptyStream(), Resources(),
        "<< /T (root) /FT /Sig /Kids [7 0 R] >>",
        "<< /T (loop) /Kids [7 0 R 8 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /Rect [10 10 20 20] >>");

    /// <summary>
    /// Object 6 is an untyped group; objects 7 and 8 are child <b>fields</b> (each has its own
    /// <c>/T</c>) carrying <c>/FT /Sig</c> and a value.
    /// </summary>
    private static byte[] GroupOverTwoSignatureFields() => Build(
        Catalog(), PageTree(), Page(), EmptyStream(), Resources(),
        "<< /T (sigs) /Kids [7 0 R 8 0 R] >>",
        "<< /T (first) /FT /Sig /V 9 0 R /Parent 6 0 R >>",
        "<< /T (second) /FT /Sig /V 9 0 R /Parent 6 0 R >>",
        SignatureDictionary());

    /// <summary>Object 7 is a bare widget — no <c>/T</c>, no <c>/Kids</c> — carrying the type.</summary>
    private static byte[] GroupOverOneTypedWidget() => Build(
        Catalog(), PageTree(), Page(), EmptyStream(), Resources(),
        "<< /T (sig) /V 9 0 R /Kids [7 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /FT /Sig /Rect [10 10 20 20] >>",
        "<< >>",
        SignatureDictionary());

    private static string Catalog() =>
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>";
    private static string PageTree() => "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
    private static string Page() =>
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources 5 0 R >>";
    private static string EmptyStream() => "<< /Length 0 >>\nstream\n\nendstream";
    private static string Resources() => "<< >>";
    private static string SignatureDictionary() =>
        "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached "
        + "/ByteRange [0 100 200 300] /Contents <00> >>";

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
