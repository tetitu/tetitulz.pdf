// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.HtmlToPdf;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Html.Tests;

/// <summary>
/// <see cref="HtmlFillableForms.ConvertHtmlToFillablePdf"/>: HTML in, a PDF with real AcroForm
/// fields out.
/// </summary>
/// <remarks>
/// Every assertion reads the output through Base's <see cref="PdfFormReader"/> or fills it through
/// Base's filler — code that shares nothing with the path that wrote the fields, so a field that
/// merely looks right in its own dictionaries cannot pass.
/// </remarks>
public class HtmlFillableFormsTests
{
    private static HtmlToPdfOptions Managed() => new() { RenderEngine = HtmlRenderEngine.Managed };

    private const string Form =
        "<p>Name <input name=\"fullname\" value=\"Alice\"></p>" +
        "<p>PIN <input type=\"password\" name=\"pin\"></p>" +
        "<p><input type=\"checkbox\" name=\"agree\" checked> I agree</p>" +
        "<p>Country <select name=\"country\"><option value=\"IL\">Israel</option>" +
        "<option value=\"GB\" selected>Britain</option></select></p>";

    private static Dictionary<string, PdfFormFieldInfo> Fields(byte[] pdf)
        => PdfFormReader.GetFields(new MemoryStream(pdf)).ToDictionary(f => f.FullyQualifiedName);

    [Fact]
    public void TheControlsBecomeFieldsAReaderSees_WithTheirKindsAndValues()
    {
        var fields = Fields(HtmlFillableForms.ConvertHtmlToFillablePdf(Form, Managed()));

        Assert.Equal(new[] { "agree", "country", "fullname", "pin" },
                     fields.Keys.OrderBy(k => k, StringComparer.Ordinal));

        Assert.Equal(PdfFieldType.Text, fields["fullname"].FieldType);
        Assert.Equal("Alice", fields["fullname"].Value);

        Assert.True(fields["pin"].IsPassword);
        Assert.Null(fields["pin"].Value);

        Assert.Equal(PdfFieldType.Button, fields["agree"].FieldType);
        Assert.True(fields["agree"].IsChecked);

        Assert.True(fields["country"].IsComboBox);
        Assert.Equal("GB", fields["country"].Value);
    }

    /// <summary>
    /// Each widget sits where the layout put its placeholder: the placement and the widget's
    /// <c>/Rect</c> agree, read by two different pieces of code.
    /// </summary>
    [Fact]
    public void EachWidgetSitsOverItsPlaceholder()
    {
        var laidOut = HtmlToPdfConverter.ConvertHtmlWithFormFields(Form, Managed());
        var fields = Fields(HtmlFillableForms.ConvertHtmlToFillablePdf(Form, Managed()));

        foreach (var placed in laidOut.FormFields)
        {
            var widget = Assert.Single(fields[placed.Field.Name].Widgets);
            Assert.Equal(placed.PageIndex, widget.PageIndex);
            Assert.Equal(placed.X, widget.X, 2);
            Assert.Equal(placed.Y, widget.Y, 2);
            Assert.Equal(placed.Width, widget.Width, 2);
            Assert.Equal(placed.Height, widget.Height, 2);
        }
        Assert.Equal(4, laidOut.FormFields.Count);

        // A text field's appearance font is the size its placeholder was laid out at, not the
        // builder's 10 pt default — which is what a fixture at 10 pt could never tell apart.
        var name = laidOut.FormFields.Single(f => f.Field.Name == "fullname");
        Assert.NotEqual(10, name.FontSize);
        Assert.Contains(" " + name.FontSize.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " Tf",
                        fields["fullname"].DefaultAppearance, StringComparison.Ordinal);
    }

    /// <summary>
    /// A radio group reaches the reader as <b>one</b> field — the group — with a widget per button,
    /// the chosen value, and every button where the layout put it.
    /// </summary>
    [Fact]
    public void ARadioGroupBecomesOneRadioFieldWithAWidgetPerButton()
    {
        const string html =
            "<p><input type=\"radio\" name=\"size\" value=\"s\"> S " +
            "<input type=\"radio\" name=\"size\" value=\"m\" checked> M " +
            "<input type=\"radio\" name=\"size\" value=\"l\"> L</p>";

        var laidOut = HtmlToPdfConverter.ConvertHtmlWithFormFields(html, Managed());
        var field = Assert.Single(PdfFormReader.GetFields(
            new MemoryStream(HtmlFillableForms.ConvertHtmlToFillablePdf(html, Managed()))));

        Assert.Equal("size", field.FullyQualifiedName);
        Assert.True(field.IsRadio);
        Assert.Equal("m", field.Value);
        Assert.Equal(laidOut.FormFields.Select(p => p.X), field.Widgets.Select(w => w.X), new ToleranceComparer(0.01));
    }

    /// <summary>A chosen button can be changed through Base's filler — the group is real, not a drawing of one.</summary>
    [Fact]
    public void AProducedRadioGroupCanBeFilled()
    {
        byte[] fillable = HtmlFillableForms.ConvertHtmlToFillablePdf(
            "<input type=\"radio\" name=\"size\" value=\"s\" checked><input type=\"radio\" name=\"size\" value=\"l\">", Managed());

        using var output = new MemoryStream();
        PdfOperations.FillForm(new MemoryStream(fillable), output, new Dictionary<string, string> { ["size"] = "l" });

        Assert.Equal("l", Fields(output.ToArray())["size"].Value);
    }

    /// <summary>
    /// HTML disables radios one at a time; a PDF group is read-only as a whole. It is locked only
    /// when every button is disabled — one disabled button must not lock the ones a user can choose.
    /// </summary>
    [Theory]
    [InlineData("<input type=\"radio\" name=\"g\" value=\"a\" disabled><input type=\"radio\" name=\"g\" value=\"b\">", false)]
    [InlineData("<input type=\"radio\" name=\"g\" value=\"a\"><input type=\"radio\" name=\"g\" value=\"b\" disabled>", false)]
    [InlineData("<input type=\"radio\" name=\"g\" value=\"a\" disabled><input type=\"radio\" name=\"g\" value=\"b\" disabled>", true)]
    public void ARadioGroupIsReadOnlyOnlyWhenEveryButtonIs(string html, bool readOnly)
    {
        Assert.Equal(readOnly, Fields(HtmlFillableForms.ConvertHtmlToFillablePdf(html, Managed()))["g"].IsReadOnly);
    }

    private sealed class ToleranceComparer : IEqualityComparer<double>
    {
        private readonly double _tolerance;
        public ToleranceComparer(double tolerance) => _tolerance = tolerance;
        public bool Equals(double a, double b) => Math.Abs(a - b) <= _tolerance;
        public int GetHashCode(double value) => 0;
    }

    [Fact]
    public void AReadonlyControlBecomesAReadOnlyField()
    {
        var fields = Fields(HtmlFillableForms.ConvertHtmlToFillablePdf(
            "<input name=\"locked\" value=\"x\" readonly><input name=\"open\" value=\"y\">", Managed()));

        Assert.True(fields["locked"].IsReadOnly);
        Assert.False(fields["open"].IsReadOnly);
    }

    /// <summary>The fields are real enough to fill: a value written by Base's filler reads back.</summary>
    [Fact]
    public void TheProducedFormCanBeFilled()
    {
        byte[] fillable = HtmlFillableForms.ConvertHtmlToFillablePdf(Form, Managed());

        using var output = new MemoryStream();
        PdfOperations.FillForm(new MemoryStream(fillable), output,
                               new Dictionary<string, string> { ["fullname"] = "Bob" });

        Assert.Equal("Bob", Fields(output.ToArray())["fullname"].Value);
    }

    /// <summary>A document with no mappable control is returned as the layout wrote it: no form at all.</summary>
    [Fact]
    public void ADocumentWithNoControlsGetsNoForm()
    {
        byte[] pdf = HtmlFillableForms.ConvertHtmlToFillablePdf("<p>Just prose.</p>", Managed());

        Assert.DoesNotContain("/AcroForm", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
        Assert.Empty(PdfFormReader.GetFields(new MemoryStream(pdf)));
        // Potency: the conversion produced the document, rather than nothing a reader would find a form in.
        Assert.Contains("Just prose", PdfOperations.ExtractText(new MemoryStream(pdf)), StringComparison.Ordinal);
    }

    /// <summary>
    /// The mapping onto Base's descriptors keeps an option's export value only where it differs
    /// from its text, which is what <see cref="ChoiceOption"/> documents null as meaning.
    /// </summary>
    [Fact]
    public void AnOptionWhoseValueIsItsTextCarriesNoSeparateExportValue()
    {
        var laidOut = HtmlToPdfConverter.ConvertHtmlWithFormFields(
            "<select name=\"c\"><option>Same</option><option value=\"X\">Different</option></select>", Managed());

        var choice = Assert.IsType<FormComboField>(HtmlFillableForms.ToFormField(Assert.Single(laidOut.FormFields)));
        Assert.Null(choice.Options[0].ExportValue);
        Assert.Equal("X", choice.Options[1].ExportValue);
    }
}
