// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfFormFieldBuilder"/> refuses a form it cannot read. An unreadable <c>/AcroForm</c>
/// or <c>/Fields</c> used to read as none, and the new revision's form held only the added fields;
/// an unreadable page <c>/Annots</c> left the page holding only the new widget, and an unreadable
/// <c>/DR</c> or <c>/DR /Font</c> only the new field's font, so the fonts the other fields' <c>/DA</c>
/// name stopped resolving.
/// </summary>
public sealed class FormFieldBuilderUnreadableFormTests
{
    [Theory]
    [InlineData(Unreadable.Form, "/AcroForm could not be read")]
    [InlineData(Unreadable.Fields, "/Fields array could not be read")]
    [InlineData(Unreadable.Annots, "Page 1's /Annots could not be read")]
    [InlineData(Unreadable.Dr, "form's /DR could not be read")]
    [InlineData(Unreadable.DrFont, "form's /DR /Font could not be read")]
    public void Adversarial_unreadable_form_refuses_an_added_field(Unreadable what, string reason)
    {
        var output = new MemoryStream();
        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfFormFieldBuilder.AddTextField(new MemoryStream(Pdf(what)), output, Field()));

        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// The control: a readable form keeps its field beside the added one, the page its widget beside
    /// the new one, and the <c>/DR</c> its font beside the new field's.
    /// </summary>
    [Fact]
    public void A_readable_form_keeps_its_fields()
    {
        var output = new MemoryStream();
        PdfFormFieldBuilder.AddTextField(new MemoryStream(Pdf(Unreadable.Nothing)), output, Field());

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var form = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]));
        var fields = Assert.IsType<PdfArray>(doc.ResolveObject(form[PdfName.Fields]));
        Assert.Equal(2, fields.Count);
        Assert.Equal(6, Assert.IsType<PdfReference>(fields[0]).ReferenceObjectNumber);

        var annots = Assert.IsType<PdfArray>(doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]));
        Assert.Equal(2, annots.Count);
        Assert.Equal(6, Assert.IsType<PdfReference>(annots[0]).ReferenceObjectNumber);

        var dr = Assert.IsType<PdfDictionary>(doc.ResolveObject(form[PdfName.DR]));
        var fonts = Assert.IsType<PdfDictionary>(doc.ResolveObject(dr[PdfName.Font]));
        Assert.Equal(10, Assert.IsType<PdfReference>(fonts[new PdfName("ZaDb")]).ReferenceObjectNumber);
        Assert.NotNull(fonts[new PdfName("Helv")]);
    }

    public enum Unreadable { Nothing, Form, Fields, Annots, Dr, DrFont }

    private static FormTextField Field()
        => new FormTextField { FieldName = "added", PageIndex = 0, X = 10, Y = 50, Width = 50, Height = 20 };

    /// <summary>
    /// A one-page document whose form (object 4) lists its fields through object 5, an array holding
    /// one text field (object 6); the page lists its annotations through object 7, and the form's
    /// <c>/DR</c> (object 8) its fonts through object 9. The unreadable one is a lone <c>]</c>.
    /// </summary>
    private static byte[] Pdf(Unreadable what)
    {
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots 7 0 R >>",
            what == Unreadable.Form ? "]" : "<< /Fields 5 0 R /DA (/Helv 0 Tf 0 g) /DR 8 0 R >>",
            what == Unreadable.Fields ? "]" : "[6 0 R]",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (x) /Rect [10 10 60 30] /P 3 0 R >>",
            what == Unreadable.Annots ? "]" : "[6 0 R]",
            what == Unreadable.Dr ? "]" : "<< /Font 9 0 R >>",
            what == Unreadable.DrFont ? "]" : "<< /ZaDb 10 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
