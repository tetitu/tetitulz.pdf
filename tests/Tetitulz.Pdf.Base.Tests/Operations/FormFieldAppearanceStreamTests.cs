// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Operations.Forms;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The <b>dictionary</b> of the appearance stream <see cref="PdfFormFieldBuilder"/> generates for a
/// new field — that it is a form XObject, and that its box and length describe what it holds.
/// </summary>
/// <remarks>
/// <para><b>Written because a consolidation had almost nothing to prove itself against.</b> The
/// text-field and checkbox builders emitted the same eleven-line form-XObject tail byte for byte,
/// and merging them is a pure move whose only proof is that something reaches the survivor. Of five
/// mutations of that survivor — making it an image instead of a form, collapsing the box, swapping
/// its axes, emptying the data, and lying about the length — <b>four survived</b> the whole 243-test
/// form and appearance suite. Only emptying the content was caught.</para>
///
/// <para><b>Why the hole is shaped that way.</b> The tests that existed read the appearance's
/// <i>content</i> — escaping round trips, what the drawing operators say — through
/// <c>GetDecodedData</c>, which needs neither a correct <c>/BBox</c> nor a correct <c>/Subtype</c>
/// to answer. A viewer needs both: a form whose box is empty clips its own drawing away.</para>
/// </remarks>
public class FormFieldAppearanceStreamTests
{
    private const double FieldW = 190, FieldH = 30;
    private const double BoxW = 24, BoxH = 24;

    /// <summary>A generated appearance is a form XObject, whichever field type made it.</summary>
    /// <remarks>
    /// <c>/Subtype</c> is what tells a consumer how to interpret the stream at all; an appearance
    /// that claims to be an image is drawn as nothing by both of this library's renderers.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedAppearance_IsAFormXObject(bool checkbox)
    {
        PdfStream ap = Appearance(checkbox, out _);

        Assert.Equal("Form", Assert.IsType<PdfName>(ap[PdfName.Subtype]).Value);
        Assert.Equal(1, Assert.IsType<PdfNumber>(ap[PdfName.FormType]).IntValue);
    }

    /// <summary>
    /// The <c>/BBox</c> is anchored at the origin and sized to the widget, not collapsed or
    /// transposed.
    /// </summary>
    /// <remarks>
    /// Asserted per element rather than as "four numbers": a box of <c>[0 0 0 0]</c> has the right
    /// count and clips the whole appearance away, and a box with its axes swapped has the right
    /// numbers and crops a wide field to a tall sliver. The two field types have deliberately
    /// different aspect ratios so a transposition cannot pass by symmetry.
    /// </remarks>
    [Theory]
    [InlineData(false, FieldW, FieldH)]
    [InlineData(true, BoxW, BoxH)]
    public void GeneratedAppearance_HasAnOriginAnchoredBoxTheSizeOfTheWidget(
        bool checkbox, double expectedW, double expectedH)
    {
        PdfStream ap = Appearance(checkbox, out _);

        var bbox = Assert.IsType<PdfArray>(ap[PdfName.BBox]);
        Assert.Equal(4, bbox.Count);
        Assert.Equal(0, Num(bbox[0]));
        Assert.Equal(0, Num(bbox[1]));
        Assert.Equal(expectedW, Num(bbox[2]));
        Assert.Equal(expectedH, Num(bbox[3]));
    }

    /// <summary>
    /// <c>/Length</c> describes the bytes actually written.
    /// </summary>
    /// <remarks>
    /// A stream whose declared length is shorter than its data is truncated by a parser that trusts
    /// the key, and this library's own writer emits the value rather than deriving it — which is the
    /// case CLAUDE.md names for asserting a written number rather than a parsed object.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedAppearance_DeclaresTheLengthOfWhatItHolds(bool checkbox)
    {
        PdfStream ap = Appearance(checkbox, out byte[] data);

        Assert.Equal(data.Length, Assert.IsType<PdfNumber>(ap[PdfName.Length]).IntValue);
        Assert.True(data.Length > 0, "the builder wrote an empty appearance");
    }

    // ------------------------------------------------------------------

    private static PdfStream Appearance(bool checkbox, out byte[] data)
    {
        using var output = new MemoryStream();
        using var input = new MemoryStream(OnePagePdf(), writable: false);

        if (checkbox)
            PdfFormFieldBuilder.AddCheckbox(input, output, new FormCheckboxField
            {
                FieldName = "C1",
                PageIndex = 0,
                X = 10, Y = 10, Width = BoxW, Height = BoxH,
            });
        else
            PdfFormFieldBuilder.AddTextField(input, output, new FormTextField
            {
                FieldName = "T1",
                PageIndex = 0,
                X = 10, Y = 10, Width = FieldW, Height = FieldH,
                DefaultValue = "value",
            });

        byte[] pdf = output.ToArray();
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        var annots = Assert.IsType<PdfArray>(doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]));

        for (int i = 0; i < annots.Count; i++)
        {
            var annot = doc.ResolveObject(annots[i]) as PdfDictionary;
            var ap = doc.ResolveObject(annot?[PdfName.AP]) as PdfDictionary;
            var normal = doc.ResolveObject(ap?[PdfName.N]);

            // Table 170: /N is either the stream itself or a subdictionary of appearance states.
            // A check box is the standard's own example of the second form, so both are followed —
            // and reaching the state only through /N is what pins that the level is there at all.
            var n = normal as PdfStream;
            if (n == null && normal is PdfDictionary states)
            {
                var selected = annot[PdfName.AS] as PdfName;
                Assert.NotNull(selected);
                n = doc.ResolveObject(states[selected]) as PdfStream;
                Assert.True(n != null, $"/AP /N has no stream for the selected state /{selected.Value}");
            }

            if (n == null) continue;
            data = n.GetDecodedData();
            return n;
        }

        throw new Xunit.Sdk.XunitException("the field carries no /AP /N appearance stream");
    }

    private static double Num(PdfObject o) => Assert.IsType<PdfNumber>(o).DoubleValue;

    // ------------------------------------------------------------------
    //  What the builder must not write, and what it must ask the viewer to draw
    // ------------------------------------------------------------------

    /// <summary>
    /// ISO 32000-2 Table 229 bit 14: a processor must never store a password field's value. The
    /// builder wrote a default value to <c>/V</c> and drew it in clear text in the appearance.
    /// </summary>
    [Fact]
    public void APasswordFieldWithADefaultValueIsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => AddText(OnePagePdf(), new FormTextField
        {
            FieldName = "pin", PageIndex = 0, X = 10, Y = 10, Password = true, DefaultValue = "1234",
        }));
        Assert.Contains("password", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The control: a password field without one is added as before.</summary>
    [Fact]
    public void APasswordFieldWithoutADefaultValueIsAdded()
    {
        byte[] pdf = AddText(OnePagePdf(), new FormTextField { FieldName = "pin", PageIndex = 0, X = 10, Y = 10, Password = true });

        Assert.Contains("/Ff 8192", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
    }

    /// <summary>
    /// A default value the appearance font cannot encode is left for the viewer to draw — on a form
    /// that already existed without <c>/NeedAppearances</c>, the case where nothing else asks it to.
    /// Latin-1 encoded, it would be drawn as question marks.
    /// </summary>
    [Fact]
    public void ADefaultValueTheAppearanceFontCannotEncodeIsLeftToTheViewer()
    {
        byte[] pdf = AddText(OnePagePdf(ExistingForm), new FormTextField
        {
            FieldName = "name", PageIndex = 0, X = 10, Y = 10, Width = FieldW, Height = FieldH, DefaultValue = "שלום",
        });

        Assert.True(NeedAppearances(pdf));
        Assert.DoesNotContain(" Tj", AppearanceText(pdf), StringComparison.Ordinal);
    }

    /// <summary>The control: a Latin default value is drawn, and asks the viewer for nothing.</summary>
    [Fact]
    public void ADefaultValueTheFontCanEncodeIsDrawnAndAsksNothingOfTheViewer()
    {
        byte[] pdf = AddText(OnePagePdf(ExistingForm), new FormTextField
        {
            FieldName = "name", PageIndex = 0, X = 10, Y = 10, Width = FieldW, Height = FieldH, DefaultValue = "Ada",
        });

        Assert.False(NeedAppearances(pdf));
        Assert.Contains("(Ada) Tj", AppearanceText(pdf), StringComparison.Ordinal);
    }

    /// <summary>
    /// A combo box gets no <c>/AP</c> at all, so on an existing form it showed nothing until
    /// something else raised the flag.
    /// </summary>
    [Fact]
    public void AComboBoxAddedToAnExistingFormAsksTheViewerToDrawIt()
    {
        using var output = new MemoryStream();
        PdfFormFieldBuilder.AddComboBox(new MemoryStream(OnePagePdf(ExistingForm)), output, new FormComboField
        {
            FieldName = "c", PageIndex = 0, X = 10, Y = 10,
            Options = new List<ChoiceOption> { new ChoiceOption("One"), new ChoiceOption("Two") },
        });

        Assert.True(NeedAppearances(output.ToArray()));
    }

    /// <summary>An existing form, written without <c>/NeedAppearances</c>.</summary>
    private const string ExistingForm = "/AcroForm << /Fields [] >>";

    private static byte[] AddText(byte[] pdf, FormTextField field)
    {
        using var output = new MemoryStream();
        PdfFormFieldBuilder.AddTextField(new MemoryStream(pdf), output, field);
        return output.ToArray();
    }

    private static bool NeedAppearances(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return (doc.ResolveObject(doc.AcroForm.Dictionary[PdfName.NeedAppearances]) as PdfBoolean)?.Value == true;
    }

    /// <summary>The decoded content of the one widget's <c>/AP /N</c>.</summary>
    private static string AppearanceText(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var annots = Assert.IsType<PdfArray>(doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]));
        var widget = Assert.IsAssignableFrom<PdfDictionary>(doc.ResolveObject(annots[annots.Count - 1]));
        var ap = Assert.IsAssignableFrom<PdfDictionary>(doc.ResolveObject(widget[PdfName.AP]));
        var n = Assert.IsType<PdfStream>(doc.ResolveObject(ap[PdfName.N]));
        return Encoding.Latin1.GetString(n.GetDecodedData());
    }

    private static byte[] OnePagePdf(string catalogExtra = "")
    {
        var sb = new StringBuilder();
        var offsets = new int[5];
        sb.Append("%PDF-1.7\n");

        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R " + catalogExtra + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
        };

        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Length; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
