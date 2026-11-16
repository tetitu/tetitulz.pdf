// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Forms;

/// <summary>
/// <see cref="Tetitulz.Pdf.Core.Forms.PdfFormField.IsAddressableField"/> — whether a node is the
/// field a fully qualified name addresses, as opposed to a grouping parent above it or a widget
/// annotation beneath it.
/// </summary>
/// <remarks>
/// <para><b>The pair that matters is this property against <c>IsTerminal</c>.</b> A bare widget has
/// no kids, so it is terminal; it is not a field. Every test below that asserts one asserts the
/// other beside it, because a property that merely agreed with <c>IsTerminal</c> everywhere would
/// be a synonym, and the whole point is the shape where they differ.</para>
///
/// <para><b>The walks stay permissive; this is the filter.</b> The cyclic case at the bottom is the
/// design decision written down: <c>GetSignatureFields</c> still returns the field, and the
/// property still says it is not addressable. Applying the predicate inside the walk instead was
/// tried and reverted — it makes a cyclic document report as having no signatures at all.</para>
/// </remarks>
public class AddressableFieldTests
{
    /// <summary>
    /// A field with a SEPARATE widget kid: the field is addressable, the widget is not — and the
    /// widget is <c>IsTerminal</c>, which is why terminality alone cannot be the filter.
    /// </summary>
    [Fact]
    public void AWidgetKidIsTerminalButNotAddressable()
    {
        using var ms = new MemoryStream(FieldWithSeparateWidget());
        using var doc = PdfDocument.Open(ms);

        var field = Assert.Single(doc.AcroForm.Fields);
        Assert.True(field.IsAddressableField);
        Assert.Equal("Signature1", field.FullyQualifiedName);

        var widget = Assert.Single(field.Kids);
        Assert.True(widget.IsTerminal, "a widget has no kids, so it is terminal");
        Assert.False(widget.IsAddressableField, "...but it is not a field");

        // The reason the duplicate happened: the widget's qualified name IS the field's, so a
        // listing that included both reported one name twice.
        Assert.Equal(field.FullyQualifiedName, widget.FullyQualifiedName);
    }

    /// <summary>
    /// A grouping node holding <c>/FT</c> for its kids is a field node, but not addressable —
    /// the name belongs to the terminal field beneath it.
    /// </summary>
    [Fact]
    public void AGroupingParentIsNotAddressableButItsChildIs()
    {
        using var ms = new MemoryStream(GroupWithInnerField());
        using var doc = PdfDocument.Open(ms);

        var group = Assert.Single(doc.AcroForm.Fields);
        Assert.False(group.IsTerminal);
        Assert.False(group.IsAddressableField);

        var inner = Assert.Single(group.Kids);
        Assert.True(inner.IsAddressableField);
        Assert.Equal("group.inner", inner.FullyQualifiedName);
    }

    /// <summary>
    /// The common shape — field and widget merged into one dictionary — is addressable. Without
    /// this control the property could reject everything and every other test here would pass.
    /// </summary>
    [Fact]
    public void AMergedFieldAndWidgetIsAddressable()
    {
        using var ms = new MemoryStream(MergedFieldAndWidget());
        using var doc = PdfDocument.Open(ms);

        var field = Assert.Single(doc.AcroForm.Fields);
        Assert.True(field.IsTerminal);
        Assert.True(field.IsAddressableField);
        Assert.Equal("Signature1", field.FullyQualifiedName);
    }

    /// <summary>
    /// <b>The separation, asserted directly.</b> On a <c>/Kids</c> cycle every node is
    /// non-terminal, so the property says "not addressable" — and the walk still returns the field.
    /// </summary>
    /// <remarks>
    /// This is the test that would have caught the reverted attempt. Filtering the walk by this
    /// predicate makes <c>GetSignatureFields</c> return nothing here, so a signed document reports
    /// as unsigned — which is a worse failure than listing an entry a caller skips. Keeping both
    /// assertions in one test is deliberate: they are the two halves of one decision, and a reader
    /// who changes the walk must see them together.
    /// </remarks>
    [Fact]
    public void OnACycleTheWalkStillReportsTheFieldWhileThePropertySaysNotAddressable()
    {
        using var ms = new MemoryStream(CyclicKids());
        using var doc = PdfDocument.Open(ms);

        var fields = doc.AcroForm.GetSignatureFields();

        // The walk terminates and reports each node once - once per node, not once per lap.
        Assert.Equal(2, fields.Count);
        Assert.Equal(new[] { "Sig1", "Sig2" }, new[] { fields[0].FieldName, fields[1].FieldName });

        // And neither is addressable, because each is the other's parent. A walk that applied the
        // predicate would therefore have returned NOTHING for this document.
        Assert.All(fields, f => Assert.False(f.IsAddressableField));
    }

    // ------------------------------------------------------------------

    private static byte[] FieldWithSeparateWidget() => Build(
        "1 0 obj" + N + "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>" + N + "endobj" + N,
        "2 0 obj" + N + "<< /Type /Pages /Kids [3 0 R] /Count 1 >>" + N + "endobj" + N,
        "3 0 obj" + N + "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [5 0 R] >>" + N + "endobj" + N,
        "4 0 obj" + N + "<< /FT /Sig /T (Signature1) /Kids [5 0 R] >>" + N + "endobj" + N,
        "5 0 obj" + N + "<< /Type /Annot /Subtype /Widget /Parent 4 0 R /Rect [0 0 100 50] >>" + N + "endobj" + N);

    private static byte[] GroupWithInnerField() => Build(
        "1 0 obj" + N + "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>" + N + "endobj" + N,
        "2 0 obj" + N + "<< /Type /Pages /Kids [3 0 R] /Count 1 >>" + N + "endobj" + N,
        "3 0 obj" + N + "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [5 0 R] >>" + N + "endobj" + N,
        "4 0 obj" + N + "<< /FT /Sig /T (group) /Kids [5 0 R] >>" + N + "endobj" + N,
        "5 0 obj" + N + "<< /Type /Annot /Subtype /Widget /Parent 4 0 R /T (inner) /Rect [0 0 100 50] >>" + N + "endobj" + N);

    private static byte[] MergedFieldAndWidget() => Build(
        "1 0 obj" + N + "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>" + N + "endobj" + N,
        "2 0 obj" + N + "<< /Type /Pages /Kids [3 0 R] /Count 1 >>" + N + "endobj" + N,
        "3 0 obj" + N + "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>" + N + "endobj" + N,
        "4 0 obj" + N + "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1) /Rect [0 0 100 50] >>" + N + "endobj" + N);

    /// <summary>Fields 4 and 5 are each other's kid.</summary>
    private static byte[] CyclicKids() => Build(
        "1 0 obj" + N + "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>" + N + "endobj" + N,
        "2 0 obj" + N + "<< /Type /Pages /Kids [3 0 R] /Count 1 >>" + N + "endobj" + N,
        "3 0 obj" + N + "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>" + N + "endobj" + N,
        "4 0 obj" + N + "<< /FT /Sig /T (Sig1) /Kids [5 0 R] >>" + N + "endobj" + N,
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
