// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfFormFieldBuilder.AddFields"/>: several fields in one appended revision.
/// </summary>
/// <remarks>
/// The fields are read back through <see cref="PdfFormReader"/>, which shares no code with the
/// builder — the independent opinion — rather than by inspecting the dictionaries the builder just
/// wrote.
/// </remarks>
public class FormFieldBatchTests
{
    [Fact]
    public void EveryFieldIsAddedInOneRevision_OnThePageItNames()
    {
        byte[] pdf = AddFields(TwoPagePdf(),
            Text("name", page: 0, value: "Alice"),
            Checkbox("agree", page: 1, isChecked: true),
            Combo("country", page: 0, value: "IL"));

        // One revision appended to the original: two cross-reference sections in all. Through the
        // single-field methods the same three fields would have been four.
        Assert.Equal(2, Occurrences(pdf, "startxref"));

        var fields = PdfFormReader.GetFields(new MemoryStream(pdf)).ToDictionary(f => f.FullyQualifiedName);
        Assert.Equal(new[] { "agree", "country", "name" }, fields.Keys.OrderBy(k => k, StringComparer.Ordinal));

        Assert.Equal(PdfFieldType.Text, fields["name"].FieldType);
        Assert.Equal("Alice", fields["name"].Value);
        Assert.Equal(0, Assert.Single(fields["name"].Widgets).PageIndex);

        Assert.Equal(PdfFieldType.Button, fields["agree"].FieldType);
        Assert.True(fields["agree"].IsChecked);
        Assert.Equal(1, Assert.Single(fields["agree"].Widgets).PageIndex);

        Assert.Equal(PdfFieldType.Choice, fields["country"].FieldType);
        Assert.Equal("IL", fields["country"].Value);
        Assert.Equal(0, Assert.Single(fields["country"].Widgets).PageIndex);

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.Equal(2, Annots(doc, 0).Count);
        Assert.Single(Annots(doc, 1));
    }

    /// <summary>
    /// <b>The appearance-font memo is live in a batch</b>, and it was unreachable before: two text
    /// fields ask for <c>/Helv</c> twice and must get the same object, named by both appearances and
    /// by the form's <c>/DR</c>. Without the memo the second request mints a second font object.
    /// </summary>
    [Fact]
    public void FieldsSharingAFontShareOneFontObject_AndTheFormNamesIt()
    {
        byte[] pdf = AddFields(TwoPagePdf(), Text("first", 0, "a"), Text("second", 0, "b"));

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        int[] fromAppearances = Annots(doc, 0).Select(a => HelvIn(doc, AppearanceResources(doc, a))).ToArray();
        var dr = (PdfDictionary)doc.ResolveObject(doc.AcroForm.Dictionary[PdfName.DR])!;

        Assert.Equal(2, fromAppearances.Length);
        Assert.Equal(fromAppearances[0], fromAppearances[1]);
        Assert.Equal(fromAppearances[0], HelvIn(doc, dr));
        Assert.Equal(1, Occurrences(pdf, "/BaseFont /Helvetica"));
    }

    /// <summary>
    /// One field whose appearance is incomplete is enough to ask the viewer to draw — <b>wherever
    /// it sits in the batch</b>. The form here already exists without the flag, because a form this
    /// call creates is written with it regardless, which would hide a check of the first field only.
    /// </summary>
    [Fact]
    public void AnIncompleteAppearanceAnywhereInTheBatchAsksTheViewerToDraw()
    {
        // The text field's appearance is complete; the combo box, second, gets none.
        byte[] pdf = AddFields(TwoPagePdf("/AcroForm << /Fields [] >>"),
            Text("complete", 0, "Latin"), Combo("incomplete", 0, "IL"));

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.True(doc.AcroForm.NeedAppearances);
    }

    /// <summary>The control: a batch whose appearances are all complete leaves an existing form's flag alone.</summary>
    [Fact]
    public void ABatchOfCompleteAppearancesDoesNotAskTheViewerToDraw()
    {
        byte[] pdf = AddFields(TwoPagePdf("/AcroForm << /Fields [] >>"),
            Text("first", 0, "Latin"), Checkbox("agree", 0, isChecked: true));

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.False(doc.AcroForm.NeedAppearances);
        Assert.Equal(2, PdfFormReader.GetFields(new MemoryStream(pdf)).Count);
    }

    // ------------------------------------------------------------------ radio groups

    /// <summary>
    /// A radio group is one field and one widget per button, on the pages the buttons name. The
    /// reader — which shares no code with the builder — sees one radio field whose value is the
    /// chosen button, with its widgets where they were put.
    /// </summary>
    [Fact]
    public void ARadioGroupIsOneFieldWithAWidgetPerButton_AcrossPages()
    {
        byte[] pdf = AddFields(TwoPagePdf(), Radio("size", "M", ("S", 0), ("M", 0), ("L", 1)));

        var field = Assert.Single(PdfFormReader.GetFields(new MemoryStream(pdf)));
        Assert.Equal("size", field.FullyQualifiedName);
        Assert.True(field.IsRadio);
        Assert.Equal("M", field.Value);
        Assert.Equal(new[] { 0, 0, 1 }, field.Widgets.Select(w => w.PageIndex));

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.Equal(2, Annots(doc, 0).Count);
        Assert.Single(Annots(doc, 1));
        // Only the chosen button is on; the others show /Off.
        var states = Enumerable.Range(0, 2).SelectMany(p => Annots(doc, p).Cast<PdfObject>())
            .Select(a => ((PdfDictionary)doc.ResolveObject(a)!).GetName(PdfName.AS)?.Value).ToList();
        Assert.Equal(new[] { "Off", "M", "Off" }, states);
    }

    /// <summary>
    /// The group's flags: Radio, NoToggleToOff — an HTML radio group cannot be cleared by clicking
    /// the chosen button again — and ReadOnly when the caller asks, with a control that it is
    /// absent otherwise.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARadioGroupCarriesItsFlags(bool readOnly)
    {
        var group = Radio("size", "S", ("S", 0), ("M", 0));
        group.ReadOnly = readOnly;
        var field = Assert.Single(PdfFormReader.GetFields(new MemoryStream(AddFields(TwoPagePdf(), group))));

        Assert.True(field.IsRadio);
        Assert.True(PdfFieldFlags.Has(field.Flags, PdfFieldFlags.NoToggleToOff));
        Assert.Equal(readOnly, field.IsReadOnly);
    }

    /// <summary>
    /// A button's on appearance differs from its off one — the dot is the only thing that shows
    /// which is chosen, and without it every button looks unselected.
    /// </summary>
    [Fact]
    public void ARadioButtonsOnAppearanceShowsTheDotItsOffAppearanceDoesNot()
    {
        using var doc = PdfDocument.Open(new MemoryStream(AddFields(TwoPagePdf(), Radio("size", "S", ("S", 0)))));
        var widget = (PdfDictionary)doc.ResolveObject(Annots(doc, 0)[0])!;
        var normal = (PdfDictionary)doc.ResolveObject(((PdfDictionary)doc.ResolveObject(widget[PdfName.AP])!)[PdfName.N])!;

        int Curves(PdfName state)
        {
            string content = Encoding.Latin1.GetString(((PdfStream)doc.ResolveObject(normal[state])!).GetDecodedData());
            return content.Split('\n').Count(line => line.EndsWith(" c", StringComparison.Ordinal));
        }

        // Disc and border: two circles of four curves each; the chosen button adds the dot.
        Assert.Equal(8, Curves(PdfName.Off));
        Assert.Equal(12, Curves(new PdfName("S")));
    }

    /// <summary>The group is really fillable: choosing another button through Base's filler moves the value and the states.</summary>
    [Fact]
    public void ARadioGroupCanBeFilled()
    {
        byte[] pdf = AddFields(TwoPagePdf(), Radio("size", "M", ("S", 0), ("M", 0), ("L", 1)));

        using var output = new MemoryStream();
        PdfOperations.FillForm(new MemoryStream(pdf), output, new Dictionary<string, string> { ["size"] = "L" });

        Assert.Equal("L", Assert.Single(PdfFormReader.GetFields(new MemoryStream(output.ToArray()))).Value);
    }

    [Fact]
    public void ARadioGroupWithNothingChosenIsOff()
    {
        byte[] pdf = AddFields(TwoPagePdf(), Radio("size", null, ("S", 0), ("M", 0)));

        var field = Assert.Single(PdfFormReader.GetFields(new MemoryStream(pdf)));
        Assert.Equal("Off", field.Value);
    }

    [Theory]
    [InlineData("S", "S", "share the export value")]      // two buttons, one state: choosing one chooses both
    [InlineData("S", "Off", "reserves for the unselected state")]
    public void ARadioGroupTheWriterCannotRepresentIsRefused(string first, string second, string reason)
    {
        using var output = new MemoryStream();

        var ex = Assert.Throws<ArgumentException>(() => PdfFormFieldBuilder.AddFields(
            new MemoryStream(TwoPagePdf()), output, new FormFieldBase[] { Radio("size", null, (first, 0), (second, 0)) }));

        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void ARadioGroupSelectingAValueNoButtonOffersIsRefused()
    {
        using var output = new MemoryStream();

        var ex = Assert.Throws<ArgumentException>(() => PdfFormFieldBuilder.AddFields(
            new MemoryStream(TwoPagePdf()), output, new FormFieldBase[] { Radio("size", "XL", ("S", 0), ("M", 0)) }));

        Assert.Contains("'XL'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A group has no page of its own; each button's page is checked, the second page too.</summary>
    [Fact]
    public void ARadioButtonOnAPageTheDocumentDoesNotHaveIsRefused()
    {
        using var output = new MemoryStream();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PdfFormFieldBuilder.AddFields(
            new MemoryStream(TwoPagePdf()), output, new FormFieldBase[] { Radio("size", null, ("S", 0), ("M", 4)) }));

        Assert.Equal("PageIndex", ex.ParamName);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// The facade's two routes to a radio group — the one-field call and the batch — write the same
    /// field; and the batch stays one revision through the facade, beside a field of another kind.
    /// </summary>
    [Fact]
    public void TheFacadesSingleAndBatchRoutesWriteTheSameRadioGroup_TheBatchInOneRevision()
    {
        using var single = new MemoryStream();
        PdfOperations.AddRadioGroup(new MemoryStream(TwoPagePdf()), single, Radio("size", "L", ("S", 0), ("L", 1)));
        using var batch = new MemoryStream();
        PdfOperations.AddFormFields(new MemoryStream(TwoPagePdf()), batch,
            new FormFieldBase[] { Radio("size", "L", ("S", 0), ("L", 1)), Text("name", 0, "Alice") });

        var fromSingle = Assert.Single(PdfFormReader.GetFields(new MemoryStream(single.ToArray())));
        var fromBatch = PdfFormReader.GetFields(new MemoryStream(batch.ToArray())).ToDictionary(f => f.FullyQualifiedName);
        Assert.Equal(new[] { "name", "size" }, fromBatch.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var group in new[] { fromSingle, fromBatch["size"] })
        {
            Assert.True(group.IsRadio);
            Assert.Equal("L", group.Value);
            Assert.Equal(new[] { 0, 1 }, group.Widgets.Select(w => w.PageIndex));
        }
        Assert.Equal("Alice", fromBatch["name"].Value);
        Assert.Equal(2, Occurrences(batch.ToArray(), "startxref"));
    }

    private static FormRadioGroupField Radio(string name, string selected, params (string Value, int Page)[] buttons)
    {
        var group = new FormRadioGroupField { FieldName = name, SelectedValue = selected };
        double y = 700;
        foreach (var (value, page) in buttons)
        {
            group.Buttons.Add(new RadioButtonOption(value, page, 10, y, 12, 12));
            y -= 20;
        }
        return group;
    }

    // ------------------------------------------------------------------ refusals

    /// <summary>
    /// Two fields in one batch under one name are refused before a byte is written: §12.7.4.2 would
    /// read them as one field.
    /// </summary>
    [Fact]
    public void TwoFieldsSharingANameAreRefused_AndNothingIsWritten()
    {
        using var output = new MemoryStream();

        var ex = Assert.Throws<ArgumentException>(() => PdfFormFieldBuilder.AddFields(
            new MemoryStream(TwoPagePdf()), output, new FormFieldBase[] { Text("same", 0, "a"), Text("same", 1, "b") }));

        Assert.Contains("'same'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void APasswordFieldWithAValueIsRefused_WhereverItSitsInTheBatch()
    {
        using var output = new MemoryStream();
        var secret = new FormTextField
        {
            FieldName = "pin", PageIndex = 0, X = 10, Y = 10, Width = 50, Height = 20,
            Password = true, DefaultValue = "1234",
        };

        Assert.Throws<ArgumentException>(() => PdfFormFieldBuilder.AddFields(
            new MemoryStream(TwoPagePdf()), output, new FormFieldBase[] { Text("ok", 0, "fine"), secret }));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void APageTheDocumentDoesNotHaveIsRefusedByName_BeforeAnythingIsWritten()
    {
        using var output = new MemoryStream();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PdfFormFieldBuilder.AddFields(
            new MemoryStream(TwoPagePdf()), output, new FormFieldBase[] { Text("ok", 0, "a"), Text("far", 7, "b") }));

        Assert.Equal("PageIndex", ex.ParamName);
        Assert.Equal(0, output.Length);
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] AddFields(byte[] pdf, params FormFieldBase[] fields)
    {
        using var output = new MemoryStream();
        PdfFormFieldBuilder.AddFields(new MemoryStream(pdf), output, fields);
        return output.ToArray();
    }

    private static FormTextField Text(string name, int page, string value) => new()
    {
        FieldName = name, PageIndex = page, X = 10, Y = 700, Width = 120, Height = 18, DefaultValue = value,
    };

    private static FormCheckboxField Checkbox(string name, int page, bool isChecked) => new()
    {
        FieldName = name, PageIndex = page, X = 10, Y = 650, Width = 14, Height = 14, IsChecked = isChecked,
    };

    private static FormComboField Combo(string name, int page, string value) => new()
    {
        FieldName = name, PageIndex = page, X = 10, Y = 600, Width = 120, Height = 18, DefaultValue = value,
        Options = new List<ChoiceOption> { new ChoiceOption("Israel", "IL"), new ChoiceOption("Britain", "GB") },
    };

    private static PdfArray Annots(PdfDocument doc, int page)
        => (PdfArray)doc.ResolveObject(doc.Pages[page].Dictionary[PdfName.Annots])!;

    private static PdfDictionary AppearanceResources(PdfDocument doc, PdfObject widgetRef)
    {
        var widget = (PdfDictionary)doc.ResolveObject(widgetRef)!;
        var ap = (PdfDictionary)doc.ResolveObject(widget[PdfName.AP])!;
        var normal = (PdfStream)doc.ResolveObject(ap[PdfName.N])!;
        return (PdfDictionary)doc.ResolveObject(normal[PdfName.Resources])!;
    }

    /// <summary>The object number <c>/Helv</c> names in a resource dictionary's <c>/Font</c>.</summary>
    private static int HelvIn(PdfDocument doc, PdfDictionary resources)
    {
        var fonts = (PdfDictionary)doc.ResolveObject(resources[PdfName.Font])!;
        return ((PdfReference)fonts[new PdfName("Helv")]!).ReferenceObjectNumber;
    }

    private static int Occurrences(byte[] pdf, string token)
    {
        string raw = Encoding.Latin1.GetString(pdf);
        int n = 0;
        for (int i = raw.IndexOf(token, StringComparison.Ordinal); i >= 0;
             i = raw.IndexOf(token, i + token.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static byte[] TwoPagePdf(string catalogExtra = "") => FormPdfFixtures.Build(
        "<< /Type /Catalog /Pages 2 0 R " + catalogExtra + " >>",
        "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
        "<< /Length 0 >>\nstream\n\nendstream",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R >>",
        "<< /Length 0 >>\nstream\n\nendstream");
}
