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
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Decisions in <see cref="PdfFormReader"/> and <see cref="PdfFormFiller"/> that a mutation sweep of
/// <c>Base/Operations/Forms</c> found no test could see — against Base, and against the two other
/// suites that reach them (Encryption through <c>PdfFormFiller.Plan</c> and the reader, Templating
/// through <c>PdfFormFiller.Fill</c>).
/// </summary>
/// <remarks>
/// Every fixture is built through <see cref="FormPdfFixtures.Build"/> on the same prologue: object 1
/// the catalog, 2 the page tree, 3 the AcroForm, 4 the page, 5 the <c>/DR</c> Helvetica, fields from 6.
/// </remarks>
public class FormReaderFillerEdgeCaseTests
{
    // ================================================================
    //  The reader
    // ================================================================

    [Fact]
    public void AFieldReachedAgainThroughAKidsCycleIsReportedOnce()
    {
        // 6 is a group holding the terminal 7 and a group 8 whose only kid is 6 again. The visited set
        // is what stops 7 being described once per trip round the cycle, up to the depth cap.
        var fields = Read(Form("7 0 R", "6 0 R",
            "<< /T (g) /Kids [7 0 R 8 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (t) /Parent 6 0 R /Rect [0 0 10 10] /V (x) >>",
            "<< /T (h) /Parent 6 0 R /Kids [6 0 R] >>"));

        Assert.Equal("g.t", Assert.Single(fields).FullyQualifiedName);
    }

    [Fact]
    public void AFieldAtTheLastDepthTheTreeCapAllowsIsRead()
    {
        // The control for the test below: the same chain one level shorter is read to its end.
        Assert.Single(Read(Chain(PdfLimits.MaxTreeDepth - 1)));
    }

    [Fact]
    public void AFieldTreeDeeperThanTheCapIsReadNoFurtherThanTheCap()
    {
        // A characterisation of the cap, pinned so an unintended change is loud: a terminal at depth
        // MaxTreeDepth is past it. No real form nests a hundred groups; the cap is what bounds the
        // walk's recursion on a file built to be deep.
        Assert.Empty(Read(Chain(PdfLimits.MaxTreeDepth)));
    }

    [Fact]
    public void AnIndirectTooltipIsRead()
    {
        var field = Assert.Single(Read(Form("6 0 R", "6 0 R",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (n) /TU 7 0 R /Rect [0 0 10 10] >>",
            "(Your name)")));

        Assert.Equal("Your name", field.AlternateName);
    }

    [Fact]
    public void AFieldWithNoQuaddingAnywhereIsLeftAligned()
    {
        var field = Assert.Single(Read(Form("6 0 R", "6 0 R",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (n) /Rect [0 0 10 10] >>")));

        Assert.Equal(PdfQuadding.Left, field.Quadding);
    }

    [Fact]
    public void AMultiSelectListBoxReportsItsFirstSelectionAsItsValue()
    {
        var field = Assert.Single(Read(Form("6 0 R", "6 0 R",
            "<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 2097152 /T (l) /Rect [0 0 10 10] "
            + "/Opt [(en) (he)] /V [(en) (he)] >>")));

        Assert.Equal("en", field.Value);
        Assert.Equal(new[] { "en", "he" }, field.SelectedValues);
    }

    [Fact]
    public void ADefaultValueWrittenAsANameIsRead()
    {
        var field = Assert.Single(Read(Form("6 0 R", "6 0 R",
            "<< /Type /Annot /Subtype /Widget /FT /Btn /T (c) /Rect [0 0 10 10] /V /Off /DV /Yes >>")));

        Assert.Equal("Yes", field.DefaultValue);
    }

    [Fact]
    public void ATextFieldWhoseValueHappensToBeANameIsNotChecked()
    {
        // Only a button can be checked; a text field holding /Yes holds a word.
        var field = Assert.Single(Read(Form("6 0 R", "6 0 R",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (n) /Rect [0 0 10 10] /V /Yes >>")));

        Assert.False(field.IsChecked);
    }

    [Fact]
    public void OptionsAreReadForAChoiceFieldOnly()
    {
        // /Opt belongs to a choice field (Table 231); on a text field it describes nothing.
        var field = Assert.Single(Read(Form("6 0 R", "6 0 R",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (n) /Rect [0 0 10 10] /Opt [(a) (b)] >>")));

        Assert.Empty(field.Options);
    }

    [Fact]
    public void AnOptionPairWithNoDisplayTextIsSkipped()
    {
        var field = Assert.Single(Read(Form("6 0 R", "6 0 R",
            "<< /Type /Annot /Subtype /Widget /FT /Ch /T (c) /Rect [0 0 10 10] "
            + "/Opt [[(a) 5] [(b) (Bee)]] >>")));

        var only = Assert.Single(field.Options);
        Assert.Equal("Bee", only.DisplayText);
        Assert.Equal("b", only.ExportValue);
    }

    [Fact]
    public void AReversedWidgetRectIsReportedFromItsLowerLeftCornerWithAPositiveSize()
    {
        var widget = Assert.Single(Assert.Single(Read(Form("6 0 R", "6 0 R",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (n) /Rect [300 720 100 700] >>"))).Widgets);

        Assert.Equal((100.0, 700.0, 200.0, 20.0), (widget.X, widget.Y, widget.Width, widget.Height));
    }

    [Fact]
    public void AnOnStateSharedByTwoWidgetsIsListedOnce()
    {
        // One check box shown in two places: both widgets offer /Yes, and the field has one on-state.
        var field = Assert.Single(Read(Form("7 0 R 8 0 R", "6 0 R",
            "<< /FT /Btn /T (c) /Kids [7 0 R 8 0 R] /V /Off >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [0 0 10 10] "
            + "/AP << /N << /Yes 9 0 R /Off 9 0 R >> >> /AS /Off >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [20 0 30 10] "
            + "/AP << /N << /Yes 9 0 R /Off 9 0 R >> >> /AS /Off >>",
            FormPdfFixtures.AppearanceStream())));

        Assert.Equal(new[] { "Yes" }, field.OnStates);
        // Decided by a set beside the list, not a search of it: a field's kids are the file's.
        Assert.Equal(1, field.OnStateSet.Seen);
    }

    [Theory]
    [InlineData("/Tx", true)]
    [InlineData("/Ch", false)]
    public void OnlyATextFieldIsAComb(string type, bool expected)
    {
        var field = Assert.Single(Read(Form("6 0 R", "6 0 R",
            "<< /Type /Annot /Subtype /Widget /FT " + type + " /Ff 16777216 /MaxLen 4 /T (n) /Rect [0 0 40 10] >>")));

        Assert.Equal(expected, field.IsComb);
    }

    // ================================================================
    //  The filler
    // ================================================================

    [Fact]
    public void WithAppearanceGenerationOffTheViewerIsAskedToDrawAndNoAppearanceIsWritten()
    {
        var (doc, _) = Fill(FormPdfFixtures.TextField(), ("name", "Ada"),
            new FormFillOptions { GenerateAppearances = false });

        Assert.True((doc.ResolveObject(Dict(doc, FormPdfFixtures.AcroFormObj)[PdfName.NeedAppearances]) as PdfBoolean)?.Value);
        Assert.Null(Dict(doc, 6)[PdfName.AP]);
    }

    [Fact]
    public void AFieldWrittenDirectlyInFieldsIsSkippedWithAReason()
    {
        // An incremental update replaces an object by its number; a field written inline has none.
        var (_, result) = Fill(FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>",
            Pages,
            "<< /Fields [<< /FT /Tx /T (inline) /Rect [0 0 10 10] >>] /DR << /Font << /Helv 5 0 R >> >> >>",
            Page(""),
            Helvetica), ("inline", "x"));

        Assert.Empty(result.FilledFields);
        Assert.Contains(result.Warnings, w => w.Contains("direct object", StringComparison.Ordinal));
    }

    [Fact]
    public void APushButtonIsNotFilled()
    {
        var (_, result) = Fill(Form("6 0 R", "6 0 R",
            "<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (go) /Rect [0 0 10 10] >>"), ("go", "yes"));

        Assert.Empty(result.FilledFields);
        Assert.Contains(result.Warnings, w => w.Contains("push button", StringComparison.Ordinal));
    }

    [Fact]
    public void ACheckBoxWithNoAppearanceStatesIsSetOnAndSaysThereIsNothingToSwitch()
    {
        var (doc, result) = Fill(Form("6 0 R", "6 0 R",
            "<< /Type /Annot /Subtype /Widget /FT /Btn /T (cb) /Rect [0 0 10 10] >>"), ("cb", "true"));

        Assert.Equal("Yes", (doc.ResolveObject(Dict(doc, 6)[PdfName.V]) as PdfName)?.Value);
        Assert.Contains("cb", result.FilledFields);
        Assert.Contains("cb", result.FieldsWithoutAppearance);
    }

    [Fact]
    public void ARadioStateMatchingExactlyWinsOverOneMatchingOnlyWithoutCase()
    {
        // Two widgets exporting "A" and "a". Asked for "a", the case-sensitive match is the answer;
        // a case-insensitive pass first would select the "A" widget, which is the other button.
        var (doc, _) = Fill(Form("7 0 R 8 0 R", "6 0 R",
            "<< /FT /Btn /Ff 32768 /T (r) /Kids [7 0 R 8 0 R] /V /Off >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [0 0 10 10] "
            + "/AP << /N << /A 9 0 R /Off 9 0 R >> >> /AS /Off >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [20 0 30 10] "
            + "/AP << /N << /a 9 0 R /Off 9 0 R >> >> /AS /Off >>",
            FormPdfFixtures.AppearanceStream()), ("r", "a"));

        Assert.Equal("Off", Dict(doc, 7).GetName(PdfName.AS)?.Value);
        Assert.Equal("a", Dict(doc, 8).GetName(PdfName.AS)?.Value);
    }

    [Fact]
    public void AWidgetWrittenDirectlyInKidsIsSkippedWithAReason()
    {
        var (_, result) = Fill(Form("", "6 0 R",
            "<< /FT /Tx /T (t) /DA (/Helv 12 Tf 0 g) "
            + "/Kids [<< /Type /Annot /Subtype /Widget /Rect [0 0 100 20] >>] >>"), ("t", "x"));

        Assert.Contains(result.Warnings, w => w.Contains("widget is a direct object", StringComparison.Ordinal));
    }

    [Fact]
    public void AFieldWhoseWidgetsAllFailToDrawIsListedOnceWithoutAnAppearance()
    {
        // Hebrew with no font supplied: neither widget can be drawn. One field, one entry.
        var (_, result) = Fill(Form("7 0 R 8 0 R", "6 0 R",
            "<< /FT /Tx /T (h) /DA (/Helv 12 Tf 0 g) /Kids [7 0 R 8 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [0 0 100 20] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [0 30 100 50] >>"), ("h", "שלום"));

        Assert.Equal(new[] { "h" }, result.FieldsWithoutAppearance);
    }

    [Fact]
    public void AWidgetWithAShortRectGetsNoAppearanceRatherThanOneOfAnInventedSize()
    {
        var (_, result) = Fill(FormPdfFixtures.TextField(rect: "[100 700]"), ("name", "Ada"));

        Assert.Contains("name", result.FieldsWithoutAppearance);
    }

    [Fact]
    public void AFormWrittenInlineInTheCatalogIsFilledWithoutWritingObjectZero()
    {
        // /AcroForm written directly in the catalog has no object number, so its /NeedAppearances
        // cannot be updated by an incremental update — and must not be written as object 0.
        byte[] input = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] /DR << /Font << /Helv 5 0 R >> >> >> >>",
            Pages,
            "null",
            Page("6 0 R"),
            Helvetica,
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [100 700 300 720] /DA (/Helv 12 Tf 0 g) >>");

        using var output = new MemoryStream();
        PdfFormFiller.Fill(new MemoryStream(input), output, new Dictionary<string, string> { ["name"] = "Ada" },
            new FormFillOptions());
        byte[] filled = output.ToArray();

        Assert.DoesNotContain("\n0 0 obj", Encoding.Latin1.GetString(filled), StringComparison.Ordinal);
        using var doc = PdfDocument.Open(new MemoryStream(filled));
        Assert.Equal("Ada", (doc.ResolveObject(Dict(doc, 6)[PdfName.V]) as PdfString)?.GetText());
    }

    // ================================================================
    //  Fixtures
    // ================================================================

    private const string Pages = "<< /Type /Pages /Kids [4 0 R] /Count 1 /MediaBox [0 0 612 792] >>";
    private const string Helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    private static string Page(string annots) =>
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [" + annots + "] >>";

    /// <summary>The shared prologue, then <paramref name="bodies"/> as objects 6 onwards.</summary>
    private static byte[] Form(string annots, string fields, params string[] bodies)
    {
        var all = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>",
            Pages,
            "<< /Fields [" + fields + "] /DR << /Font << /Helv 5 0 R >> >> >>",
            Page(annots),
            Helvetica,
        };
        all.AddRange(bodies);
        return FormPdfFixtures.Build(all.ToArray());
    }

    /// <summary>
    /// A chain of <paramref name="groups"/> nested groups, each the only kid of the one before, with
    /// a terminal text field at the bottom — at depth <paramref name="groups"/>.
    /// </summary>
    private static byte[] Chain(int groups)
    {
        var bodies = new List<string>();
        for (int i = 0; i < groups; i++)
            bodies.Add("<< /T (g" + i + ") /Kids [" + (7 + i) + " 0 R] >>");
        int terminal = 6 + groups;
        bodies.Add("<< /Type /Annot /Subtype /Widget /FT /Tx /T (leaf) /Rect [0 0 10 10] >>");
        return Form(terminal + " 0 R", "6 0 R", bodies.ToArray());
    }

    private static IReadOnlyList<PdfFormFieldInfo> Read(byte[] pdf) => PdfFormReader.GetFields(new MemoryStream(pdf));

    private static (PdfDocument Doc, FormFillResult Result) Fill(
        byte[] pdf, (string Name, string Value) value, FormFillOptions options = null)
    {
        var output = new MemoryStream();
        var result = PdfFormFiller.Fill(new MemoryStream(pdf), output,
            new Dictionary<string, string> { [value.Name] = value.Value }, options ?? new FormFillOptions());
        output.Position = 0;
        return (PdfDocument.Open(output), result);
    }

    private static PdfDictionary Dict(PdfDocument doc, int objectNumber) =>
        Assert.IsAssignableFrom<PdfDictionary>(doc.GetObject(objectNumber));
}
