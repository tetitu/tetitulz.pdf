// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// What <see cref="PdfFormFieldBuilder"/> actually writes into the field it adds — the entries a
/// mutation sweep of <c>Base/Operations/Forms</c> found nothing asserted: the rectangle, the page
/// reference, the flags, a check box's selected state, a choice field's options, and what the call
/// must not disturb in the document it is adding to.
/// </summary>
/// <remarks>
/// The existing suites cover the appearance <i>stream</i> (its box, its length, its resources) and
/// the escaping of the value drawn into it. The field dictionary around it was asserted nowhere, so
/// the rectangle could be built from the wrong corner and a check box could be added permanently
/// unselected without a test noticing.
/// </remarks>
public class FormFieldBuilderShapeTests
{
    // ================================================================
    //  What the call must not disturb
    // ================================================================

    [Fact]
    public void APageIndexOutsideTheDocumentIsRefusedByName()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => AddText(Pdf(), Text(page: 5)));

        Assert.Equal("PageIndex", ex.ParamName);
    }

    [Theory]
    [InlineData("[9 0 R]")]      // a direct array
    [InlineData("10 0 R")]       // …and an indirect one, which §7.3.10 allows anywhere
    public void TheAnnotationsThePageAlreadyHadSurviveTheNewField(string annots)
    {
        byte[] pdf = AddText(Pdf(pageExtra: "/Annots " + annots, extra: LinkAnnot), Text());

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var subtypes = PageAnnots(doc).Select(a => (doc.ResolveObject(a) as PdfDictionary)?.GetName(PdfName.Subtype)?.Value);
        Assert.Equal(new[] { "Link", "Widget" }, subtypes);
    }

    [Fact]
    public void TheFieldsTheFormAlreadyHadSurviveTheNewField()
    {
        byte[] pdf = AddText(Pdf(catalogExtra: "/AcroForm << /Fields [9 0 R] >>", extra: ExistingField), Text());

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.Equal(2, ((PdfArray)doc.ResolveObject(doc.AcroForm.Dictionary[PdfName.Fields])!).Count);
    }

    [Fact]
    public void AFormThatAlreadyDefinesAFontNameKeepsItsOwnMeaningForIt()
    {
        // The generated appearance names /Helv. A form that already says what /Helv is has made a
        // statement about every field in it, so the builder must not redefine it.
        byte[] pdf = AddText(Pdf(catalogExtra: "/AcroForm << /Fields [] /DR << /Font << /Helv 9 0 R >> >> >>",
            extra: "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>"), Text());

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var dr = (PdfDictionary)doc.ResolveObject(doc.AcroForm.Dictionary[PdfName.DR])!;
        var fonts = (PdfDictionary)doc.ResolveObject(dr[PdfName.Font])!;
        var helv = (PdfDictionary)doc.ResolveObject(fonts[new PdfName("Helv")])!;
        Assert.Equal("Courier", helv.GetName(PdfName.BaseFont)?.Value);
    }

    [Fact]
    public void ANewFormAsksTheViewerToDrawItsFields()
    {
        // A form this call created carries no appearances for anything but the field just added.
        using var doc = Open(AddText(Pdf(), Text()));

        Assert.True((doc.ResolveObject(doc.AcroForm.Dictionary[PdfName.NeedAppearances]) as PdfBoolean)?.Value);
    }

    // ================================================================
    //  The field dictionary
    // ================================================================

    [Fact]
    public void TheWidgetRectangleIsTheFieldsBoxInPageCoordinates()
    {
        using var doc = Open(AddText(Pdf(), Text()));

        // x, y, x + width, y + height — not the width and height themselves.
        Assert.Equal(new[] { 10.0, 20.0, 110.0, 50.0 }, Rect(doc));
    }

    [Fact]
    public void TheWidgetNamesThePageItIsOn()
    {
        // Table 166's /P. A widget that does not name its page is one a reader cannot place when it
        // reaches it through /AcroForm /Fields rather than through the page.
        using var doc = Open(AddText(Pdf(), Text()));

        Assert.Equal(doc.Pages[0].Dictionary.ObjectNumber,
            (Widget(doc)[PdfName.P] as PdfReference)?.ReferenceObjectNumber);
    }

    [Fact]
    public void TheTooltipIsWrittenWhenThereIsOne()
    {
        using var doc = Open(AddText(Pdf(), Text(alternate: "Your full name")));

        Assert.Equal("Your full name", (doc.ResolveObject(Widget(doc)[PdfName.TU]) as PdfString)?.GetText());
    }

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(true, 0, PdfFieldFlags.Multiline)]
    [InlineData(false, 6, 0)]
    public void ATextFieldCarriesItsFlagsAndItsLengthLimit(bool multiline, int maxLength, int expectedFlags)
    {
        using var doc = Open(AddText(Pdf(), Text(multiline: multiline, maxLength: maxLength)));

        Assert.Equal(expectedFlags, (int)((doc.ResolveObject(Widget(doc)[PdfName.Ff]) as PdfNumber)?.LongValue ?? 0));
        Assert.Equal(maxLength, (int)((doc.ResolveObject(Widget(doc)[PdfName.MaxLen]) as PdfNumber)?.LongValue ?? 0));
    }

    [Theory]
    [InlineData(true, "Yes")]
    [InlineData(false, "Off")]
    public void ACheckBoxIsAddedInTheStateItWasAskedFor(bool isChecked, string expected)
    {
        // /AS is what a viewer draws from; a box added with /V /Yes and /AS /Off shows unchecked in
        // every one of them.
        using var doc = Open(AddCheckbox(Pdf(), isChecked));

        Assert.Equal(expected, Widget(doc).GetName(PdfName.AS)?.Value);
        Assert.Equal(expected, Widget(doc).GetName(PdfName.V)?.Value);
    }

    // ================================================================
    //  Choice fields
    // ================================================================

    [Theory]
    [InlineData(true, false, PdfFieldFlags.Combo)]
    [InlineData(false, true, PdfFieldFlags.MultiSelect)]
    [InlineData(true, true, PdfFieldFlags.Combo)]     // Table 231: a combo box is not multi-select
    public void AChoiceFieldCarriesTheFlagsItsKindAllows(bool combo, bool multiSelect, int expected)
    {
        using var doc = Open(AddChoice(Pdf(), combo, multiSelect, defaultValue: null));

        Assert.Equal(expected, (int)((doc.ResolveObject(Widget(doc)[PdfName.Ff]) as PdfNumber)?.LongValue ?? 0));
    }

    [Fact]
    public void AnOptionWithAnExportValueIsWrittenAsExportThenDisplay()
    {
        // §12.7.4.4: the pair is [export display], and /V holds the export value. Written the other
        // way round, a submitted form carries the label where the value belongs.
        using var doc = Open(AddChoice(Pdf(), combo: true, multiSelect: false, defaultValue: null));

        var opt = (PdfArray)doc.ResolveObject(Widget(doc)[PdfName.Opt])!;
        var pair = (PdfArray)doc.ResolveObject(opt[0])!;
        Assert.Equal("IL", (doc.ResolveObject(pair[0]) as PdfString)?.GetText());
        Assert.Equal("Israel", (doc.ResolveObject(pair[1]) as PdfString)?.GetText());
    }

    [Fact]
    public void AnOptionWithNoExportValueIsWrittenAsPlainText()
    {
        using var doc = Open(AddChoice(Pdf(), combo: true, multiSelect: false, defaultValue: null, export: null));

        var opt = (PdfArray)doc.ResolveObject(Widget(doc)[PdfName.Opt])!;
        Assert.Equal("Israel", (doc.ResolveObject(opt[0]) as PdfString)?.GetText());
    }

    [Fact]
    public void AChoiceFieldsDefaultValueIsWritten()
    {
        using var doc = Open(AddChoice(Pdf(), combo: true, multiSelect: false, defaultValue: "IL"));

        Assert.Equal("IL", (doc.ResolveObject(Widget(doc)[PdfName.V]) as PdfString)?.GetText());
    }

    // ================================================================
    //  Output that must not depend on the host
    // ================================================================

    [Fact]
    public void TheGeneratedContentUsesADecimalPointOnEveryCulture()
    {
        // A content stream is not localised: on de-DE, "0,50 w" is not an operator and the whole
        // appearance fails to parse.
        var original = Thread.CurrentThread.CurrentCulture;
        byte[] pdf;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            pdf = AddText(Pdf(), Text(defaultValue: "Ada"));
        }
        finally { Thread.CurrentThread.CurrentCulture = original; }

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var ap = (PdfDictionary)doc.ResolveObject(Widget(doc)[PdfName.AP])!;
        string content = Encoding.Latin1.GetString(((PdfStream)doc.ResolveObject(ap[PdfName.N])!).GetDecodedData());
        Assert.DoesNotContain(",", content, StringComparison.Ordinal);
    }

    // ================================================================
    //  Fixtures
    // ================================================================

    private const string LinkAnnot = "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] >>";
    private const string ExistingField = "<< /Type /Annot /Subtype /Widget /FT /Tx /T (old) /Rect [0 0 10 10] >>";

    private static FormTextField Text(int page = 0, bool multiline = false, int maxLength = 0,
                                      string alternate = null, string defaultValue = null) =>
        new FormTextField
        {
            FieldName = "name", PageIndex = page, X = 10, Y = 20, Width = 100, Height = 30,
            Multiline = multiline, MaxLength = maxLength, AlternateFieldName = alternate,
            DefaultValue = defaultValue,
        };

    private static byte[] AddText(byte[] pdf, FormTextField field)
    {
        using var output = new MemoryStream();
        PdfFormFieldBuilder.AddTextField(new MemoryStream(pdf), output, field);
        return output.ToArray();
    }

    private static byte[] AddCheckbox(byte[] pdf, bool isChecked)
    {
        using var output = new MemoryStream();
        PdfFormFieldBuilder.AddCheckbox(new MemoryStream(pdf), output, new FormCheckboxField
        {
            FieldName = "agree", PageIndex = 0, X = 10, Y = 20, Width = 20, Height = 20, IsChecked = isChecked,
        });
        return output.ToArray();
    }

    private static byte[] AddChoice(byte[] pdf, bool combo, bool multiSelect, string defaultValue,
                                    string export = "IL")
    {
        var options = new List<ChoiceOption> { new ChoiceOption("Israel", export), new ChoiceOption("Britain", "GB") };
        using var output = new MemoryStream();
        if (combo)
            PdfFormFieldBuilder.AddComboBox(new MemoryStream(pdf), output, new FormComboField
            {
                FieldName = "country", PageIndex = 0, X = 10, Y = 20, Width = 100, Height = 20,
                Options = options, MultiSelect = multiSelect, DefaultValue = defaultValue,
            });
        else
            PdfFormFieldBuilder.AddListBox(new MemoryStream(pdf), output, new FormListField
            {
                FieldName = "country", PageIndex = 0, X = 10, Y = 20, Width = 100, Height = 40,
                Options = options, MultiSelect = multiSelect, DefaultValue = defaultValue,
            });
        return output.ToArray();
    }

    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    private static PdfArray PageAnnots(PdfDocument doc) =>
        (PdfArray)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots])!;

    /// <summary>The field this call added: the last annotation on the page.</summary>
    private static PdfDictionary Widget(PdfDocument doc)
    {
        var annots = PageAnnots(doc);
        return (PdfDictionary)doc.ResolveObject(annots[annots.Count - 1])!;
    }

    private static double[] Rect(PdfDocument doc)
    {
        var rect = (PdfArray)doc.ResolveObject(Widget(doc)[PdfName.Rect])!;
        return Enumerable.Range(0, 4).Select(i => (doc.ResolveObject(rect[i]) as PdfNumber)!.DoubleValue).ToArray();
    }

    /// <summary>
    /// One page, and object 9 free for <paramref name="extra"/> (object 10 is an array naming it,
    /// for the indirect-<c>/Annots</c> case).
    /// </summary>
    private static byte[] Pdf(string catalogExtra = "", string pageExtra = "", string extra = "null")
    {
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R " + catalogExtra + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " + pageExtra + " >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "null", "null", "null", "null",
            extra,
            "[9 0 R]",
        };
        return FormPdfFixtures.Build(bodies);
    }
}
