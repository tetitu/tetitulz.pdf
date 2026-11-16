// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfFormReader"/> — the read half of the form API. Its
/// <see cref="PdfFormFieldInfo.FullyQualifiedName"/> values are the keys
/// <see cref="PdfFormFiller"/> takes, and its <see cref="PdfFormFieldInfo.OnStates"/> are the
/// values a check box accepts, so the two halves are tested against the same fixtures.
/// </summary>
public class PdfFormReaderTests
{
    /// <summary>
    /// §12.5.5: <c>/AP /N</c> is EITHER one appearance stream OR a sub-dictionary of appearance
    /// states. A widget with a single stream has <b>no</b> states, and the reader must say so.
    ///
    /// <para><b>It did not.</b> <c>ReadWidgets</c> asked only
    /// <c>ResolveObject(ap[/N]) as PdfDictionary</c>, and <c>PdfStream</c> derives from
    /// <c>PdfDictionary</c>, so the cast succeeded for the stream case too and the loop published
    /// the appearance stream''s OWN keys as appearance states. Every text field with an appearance
    /// stream reported <c>Type</c>, <c>Subtype</c>, <c>BBox</c> and <c>Length</c> as things a
    /// caller could set <c>/AS</c> to.</para>
    ///
    /// <para>The assertion names the specific wrong values rather than only checking emptiness, so
    /// a regression says <i>what</i> leaked rather than only that a count moved.</para>
    /// </summary>
    [Fact]
    public void TextFieldWithASingleAppearanceStream_ReportsNoAppearanceStates()
    {
        byte[] pdf = FormPdfFixtures.TextFieldWithSingleAppearanceStream();

        var fields = PdfOperations.GetFormFields(new MemoryStream(pdf));
        var field = Assert.Single(fields);
        var widget = Assert.Single(field.Widgets);

        Assert.Empty(widget.OnStates);
        Assert.Empty(field.OnStates);

        // The exact keys the old code leaked, named so a regression is self-describing.
        foreach (string leaked in new[] { "Type", "Subtype", "BBox", "Length" })
        {
            Assert.DoesNotContain(leaked, widget.OnStates);
            Assert.DoesNotContain(leaked, field.OnStates);
        }
    }

    /// <summary>
    /// The counterpart, and the reason the test above is not vacuous: a widget whose <c>/AP /N</c>
    /// really IS a state sub-dictionary must still report its states. Without this, deleting the
    /// state branch outright would leave the file green.
    /// </summary>
    [Fact]
    public void CheckboxWithAStateSubDictionary_StillReportsItsOnState()
    {
        byte[] pdf = FormPdfFixtures.Checkbox(onState: "Yes");

        var fields = PdfOperations.GetFormFields(new MemoryStream(pdf));
        var field = Assert.Single(fields);
        var widget = Assert.Single(field.Widgets);

        Assert.Equal(new[] { "Yes" }, widget.OnStates);
        Assert.Equal(new[] { "Yes" }, field.OnStates);
    }
    [Fact]
    public void TextField_IsDescribedByNameTypeAndGeometry()
    {
        var field = Single(FormPdfFixtures.TextField(
            fieldName: "name", rect: "[100 700 300 720]",
            fieldEntries: "/DA (/Helv 12 Tf 0 g) /TU (Full name) /MaxLen 40 /Q 2"));

        Assert.Equal("name", field.FullyQualifiedName);
        Assert.Equal("name", field.PartialName);
        Assert.Equal("Full name", field.AlternateName);
        Assert.Equal(PdfFieldType.Text, field.FieldType);
        Assert.Equal(40, field.MaxLength);
        Assert.Equal(PdfQuadding.Right, field.Quadding);
        Assert.Equal("/Helv 12 Tf 0 g", field.DefaultAppearance);

        var widget = Assert.Single(field.Widgets);
        Assert.Equal(0, widget.PageIndex);
        Assert.Equal(100, widget.X);
        Assert.Equal(700, widget.Y);
        Assert.Equal(200, widget.Width);
        Assert.Equal(20, widget.Height);
    }

    [Fact]
    public void TextField_FlagsAreDecodedFromFf()
    {
        var field = Single(FormPdfFixtures.TextField(
            fieldEntries: $"/DA (/Helv 12 Tf 0 g) /Ff {PdfFieldFlags.Multiline | PdfFieldFlags.ReadOnly}"));

        Assert.True(field.IsMultiline);
        Assert.True(field.IsReadOnly);
        Assert.False(field.IsPassword);
        Assert.False(field.IsRequired);
    }

    /// <summary>
    /// The on-states are what <see cref="PdfFormFiller"/> will accept for this field, so a caller
    /// can drive a check box without knowing in advance whether the form calls it /Yes, /On or /1.
    /// </summary>
    [Fact]
    public void Checkbox_ReportsItsOnStateAndCheckedness()
    {
        var field = Single(FormPdfFixtures.Checkbox(onState: "On"));

        Assert.Equal(PdfFieldType.Button, field.FieldType);
        Assert.Equal(new[] { "On" }, field.OnStates);
        Assert.False(field.IsChecked);
        Assert.False(field.IsRadio);
    }

    [Fact]
    public void Checkbox_AfterFilling_ReadsBackAsChecked()
    {
        using var input = new MemoryStream(FormPdfFixtures.Checkbox());
        using var filled = new MemoryStream();
        PdfOperations.FillForm(input, filled, new System.Collections.Generic.Dictionary<string, string>
        {
            ["agree"] = "true",
        });

        filled.Position = 0;
        var field = Assert.Single(PdfOperations.GetFormFields(filled));
        Assert.True(field.IsChecked);
        Assert.Equal("Yes", field.Value);
    }

    /// <summary>
    /// A radio group is one field with two widgets. Reporting it as two fields — or as one with a
    /// single widget — would make it impossible to place both buttons.
    /// </summary>
    [Fact]
    public void RadioGroup_IsOneFieldWithBothWidgets()
    {
        var field = Single(FormPdfFixtures.RadioGroup());

        Assert.Equal("colour", field.FullyQualifiedName);
        Assert.True(field.IsRadio);
        Assert.Equal(new[] { "Red", "Green" }, field.OnStates);
        Assert.Equal(2, field.Widgets.Count);
        Assert.Equal(new[] { "Red" }, field.Widgets[0].OnStates);
        Assert.Equal(new[] { "Green" }, field.Widgets[1].OnStates);
        Assert.All(field.Widgets, w => Assert.Equal(0, w.PageIndex));
    }

    [Fact]
    public void ChoiceField_ReportsExportAndDisplayValuesSeparately()
    {
        var field = Single(FormPdfFixtures.ChoiceField());

        Assert.Equal(PdfFieldType.Choice, field.FieldType);
        Assert.True(field.IsComboBox);
        Assert.Equal("IL", field.Value);

        Assert.Equal(2, field.Options.Count);
        Assert.Equal("Israel", field.Options[0].DisplayText);
        Assert.Equal("IL", field.Options[0].ExportValue);
        Assert.Equal("United Kingdom", field.Options[1].DisplayText);
        Assert.Equal("GB", field.Options[1].ExportValue);
    }

    [Fact]
    public void SignatureField_IsReportedWithItsType()
    {
        byte[] pdf = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Fields [6 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [6 0 R] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Annot /Subtype /Widget /FT /Sig /T (sig) /Rect [100 700 300 720] >>");

        Assert.Equal(PdfFieldType.Signature, Single(pdf).FieldType);
    }

    [Fact]
    public void DocumentWithNoAcroForm_ReadsAsNoFields()
    {
        using var input = new MemoryStream(Tetitulz.Pdf.TestHelpers.MinimalPdfBuilder.CreateMinimalPdf());
        Assert.Empty(PdfFormReader.GetFields(input));
    }

    /// <summary>
    /// The depth cap alone does not stop a two-node <c>/Kids</c> cycle: every traversal step
    /// creates a fresh wrapper object, so the walk keeps finding "new" fields. The visited set is
    /// what makes this terminate.
    /// </summary>
    [Fact]
    public void CyclicKidsGraph_TerminatesWithABoundedResult()
    {
        using var input = new MemoryStream(FormPdfFixtures.CyclicFieldTree());

        var fields = PdfFormReader.GetFields(input);

        Assert.True(fields.Count <= 2, $"a 2-node cycle yielded {fields.Count} fields");
    }

    /// <summary>
    /// <see cref="PdfFormField.Kids"/> returns an empty list, never <c>null</c>, so callers do not
    /// have to null-check something they must also length-check.
    /// </summary>
    [Fact]
    public void LeafField_HasAnEmptyKidsList()
    {
        using var input = new MemoryStream(FormPdfFixtures.TextField());
        using var doc = PdfDocument.Open(input);

        var field = Assert.Single(doc.AcroForm.Fields);
        Assert.Empty(field.Kids);
        Assert.True(field.IsTerminal);
    }

    [Fact]
    public void RadioGroup_IsNotTerminalUntilItsWidgetKidsAreRecognised()
    {
        using var input = new MemoryStream(FormPdfFixtures.RadioGroup());
        using var doc = PdfDocument.Open(input);

        var group = Assert.Single(doc.AcroForm.Fields);
        Assert.Equal(2, group.Kids.Count);
        // The kids carry no /T, so the group itself holds the value.
        Assert.True(group.IsTerminal);
        Assert.All(group.Kids, kid => Assert.Null(kid.FieldName));
    }

    /// <summary>
    /// A button widget's captions are read from its <c>/MK</c> (Table 192) — normal <c>/CA</c>, down
    /// <c>/AC</c>, rollover <c>/RC</c> — through an indirect <c>/MK</c> and an indirect caption; a
    /// widget without <c>/MK</c> has none (the control).
    /// </summary>
    [Fact]
    public void AButtonsCaptions_AreReadFromItsMK()
    {
        string[] objects =
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R 6 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (go) /Rect [0 0 50 20] /P 3 0 R /MK 5 0 R >>",
            "<< /CA 7 0 R /AC (Down) /RC <FEFF05E905DC05D505DD> >>",
            "<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (plain) /Rect [60 0 110 20] /P 3 0 R >>",
            "(Go)",
        };
        var sb = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        var fields = PdfFormReader.GetFields(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(sb.ToString())));

        PdfFormWidgetInfo go = fields.Single(f => f.FullyQualifiedName == "go").Widgets.Single();
        Assert.Equal("Go", go.NormalCaption);
        Assert.Equal("Down", go.DownCaption);
        Assert.Equal("שלום", go.RolloverCaption);
        PdfFormWidgetInfo plain = fields.Single(f => f.FullyQualifiedName == "plain").Widgets.Single();
        Assert.Null(plain.NormalCaption);
        Assert.Null(plain.DownCaption);
        Assert.Null(plain.RolloverCaption);
    }

    // ----------------------------------------------------------------

    private static PdfFormFieldInfo Single(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        return Assert.Single(PdfFormReader.GetFields(input));
    }
}
