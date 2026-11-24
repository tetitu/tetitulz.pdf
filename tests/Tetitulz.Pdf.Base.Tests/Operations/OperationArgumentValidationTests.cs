// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A caller's number must not reach a file unchecked. NaN passes every
/// comparison, <c>Math.Max(NaN, 1)</c> is NaN, and <c>PdfRealFormat</c> writes <c>0</c> for a
/// non-finite value — so a NaN option would come back as a successful write of a degenerate document.
/// Each entry refuses at its boundary, naming the parameter and (for an options object) the
/// member; each refusal is paired with the control that the same call, given sound numbers,
/// answers.
/// </summary>
public sealed class OperationArgumentValidationTests
{
    private static byte[] OnePage() => MinimalPdfBuilder.CreateMinimalPdf();

    private static int PageCount(MemoryStream output)
    {
        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        return doc.Pages.Count;
    }

    // ------------------------------------------------------------------
    //  R14.1 — Bates numbering: the pad width is capped; the stamp's numbers are finite
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(PdfBatesNumbering.MaxDigits + 1)]
    [InlineData(int.MaxValue)]      // PadLeft(int.MaxValue) — the two-gigabyte label
    [InlineData(-1)]
    public void Bates_DigitsOutsideTheCap_IsRefusedBeforeAnythingIsWritten(int digits)
    {
        var options = new BatesNumberingOptions { Digits = digits };

        var formatted = Assert.Throws<ArgumentOutOfRangeException>(() => PdfBatesNumbering.Format(1, options));
        Assert.Equal("options", formatted.ParamName);
        Assert.Contains("Digits", formatted.Message, StringComparison.Ordinal);

        // Refused at the entry, before the input is read: the input here is not a PDF at all, so an
        // Apply that only found out at the first stamp (Format validates too) would throw the parse's
        // exception instead — which is what a mutation removing the entry's check showed.
        using var input = new MemoryStream(new byte[] { 1, 2, 3 }, writable: false);
        using var output = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfBatesNumbering.Apply(input, output, options));
        Assert.Equal(0, output.Length);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfBatesNumbering.Apply(new[] { (Stream)input }, new[] { (Stream)output }, options));
        Assert.Equal(0, output.Length);
    }

    /// <summary>The control: the cap itself pads to its width, and a bundle stamps at it.</summary>
    [Fact]
    public void Bates_DigitsAtTheCap_PadsToIt()
    {
        var options = new BatesNumberingOptions { Digits = PdfBatesNumbering.MaxDigits };
        Assert.Equal(new string('0', PdfBatesNumbering.MaxDigits - 1) + "7", PdfBatesNumbering.Format(7, options));

        using var input = new MemoryStream(OnePage(), writable: false);
        using var output = new MemoryStream();
        Assert.Equal(2, PdfBatesNumbering.Apply(input, output, options));   // one page: next is 2
        Assert.Equal(1, PageCount(output));
    }

    [Theory]
    [InlineData(double.NaN, 36, "FontSize")]
    [InlineData(0, 36, "FontSize")]
    [InlineData(10, -1, "MarginPt")]
    [InlineData(10, double.PositiveInfinity, "MarginPt")]
    public void Bates_NonFiniteOrNonPositiveStampNumbers_AreRefusedNamingTheMember(
        double fontSize, double marginPt, string member)
    {
        var options = new BatesNumberingOptions { FontSize = fontSize, MarginPt = marginPt };
        using var input = new MemoryStream(OnePage(), writable: false);
        using var output = new MemoryStream();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PdfBatesNumbering.Apply(input, output, options));
        Assert.Equal("options", ex.ParamName);
        Assert.Contains(member, ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  R14.2 — a redaction area with a NaN side intersects nothing
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(float.NaN, 0f, 10f, 10f, "[0].X")]
    [InlineData(0f, float.NegativeInfinity, 10f, 10f, "[0].Y")]
    [InlineData(0f, 0f, float.NaN, 10f, "[0].Width")]
    [InlineData(0f, 0f, 10f, float.PositiveInfinity, "[0].Height")]
    public void Redact_AreaWithANonFiniteSide_IsRefusedNamingIt(float x, float y, float w, float h, string member)
    {
        using var input = new MemoryStream(OnePage(), writable: false);
        using var output = new MemoryStream();
        var areas = new[] { new PdfRedactionArea { PageIndex = 0, X = x, Y = y, Width = w, Height = h } };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PdfRedactionEngine.Redact(input, output, areas));
        Assert.Equal("areas", ex.ParamName);
        Assert.Contains(member, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The control: a finite area redacts and the output is a one-page document.</summary>
    [Fact]
    public void Redact_FiniteArea_WritesTheDocument()
    {
        using var input = new MemoryStream(OnePage(), writable: false);
        using var output = new MemoryStream();
        PdfRedactionEngine.Redact(input, output,
            new[] { new PdfRedactionArea { PageIndex = 0, X = 10, Y = 10, Width = 50, Height = 20 } });
        Assert.Equal(1, PageCount(output));
    }

    // ------------------------------------------------------------------
    //  R14.3 — numeric options at seven entries
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(double.NaN, 36, "FontSize")]
    [InlineData(0, 36, "FontSize")]
    [InlineData(10, -1, "MarginPt")]
    public void HeaderFooter_NonFiniteOrNonPositiveNumbers_AreRefused(double fontSize, double marginPt, string member)
    {
        using var input = new MemoryStream(OnePage(), writable: false);
        using var output = new MemoryStream();
        var options = new HeaderFooterOptions { FontSize = fontSize, MarginPt = marginPt };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PdfHeaderFooterWriter.AddHeaderFooter(input, output, options));
        Assert.Equal("options", ex.ParamName);
        Assert.Contains(member, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderFooter_SoundNumbers_WriteTheDocument()
    {
        using var input = new MemoryStream(OnePage(), writable: false);
        using var output = new MemoryStream();
        PdfHeaderFooterWriter.AddHeaderFooter(input, output, new HeaderFooterOptions());
        Assert.Equal(1, PageCount(output));
    }

    [Theory]
    [InlineData(double.NaN, 0, 4, "SheetWidthPt")]
    [InlineData(0, double.PositiveInfinity, 4, "SheetHeightPt")]
    [InlineData(0, 0, -1, "MarginPt")]
    [InlineData(0, 0, double.NaN, "MarginPt")]
    public void Nup_NonFiniteSheetOrNegativeMargin_IsRefused(double sheetW, double sheetH, double margin, string member)
    {
        using var input = new MemoryStream(OnePage(), writable: false);
        using var output = new MemoryStream();
        var options = new NupOptions { SheetWidthPt = sheetW, SheetHeightPt = sheetH, MarginPt = margin };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PdfNup.CreateNup(input, output, 2, 1, options));
        Assert.Equal("options", ex.ParamName);
        Assert.Contains(member, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nup_SoundOptions_WriteOneSheet()
    {
        using var input = new MemoryStream(OnePage(), writable: false);
        using var output = new MemoryStream();
        PdfNup.CreateNup(input, output, 2, 1, new NupOptions());
        Assert.Equal(1, PageCount(output));
    }

    private static byte[] TinyPng() => PdfRawImageEncoder.EncodePng(new byte[2 * 2 * 3], 2, 2, 3);

    /// <summary>
    /// A field's geometry and font size are refused for NaN and ±∞, and a size below 0: a NaN width
    /// reached the appearance stream as a literal <c>NaN</c> operand and a NaN font size the
    /// <c>/DA</c>, and the field was written. A radio button's geometry is its own.
    /// </summary>
    [Theory]
    [InlineData("text", double.NaN, 10, 120, 20, 10, "X")]
    [InlineData("text", 10, double.PositiveInfinity, 120, 20, 10, "Y")]
    [InlineData("text", 10, 10, double.NaN, 20, 10, "Width")]
    [InlineData("text", 10, 10, 120, -1, 10, "Height")]
    [InlineData("text", 10, 10, 120, 20, double.NaN, "FontSize")]
    [InlineData("combo", 10, 10, 120, 20, double.NegativeInfinity, "FontSize")]
    [InlineData("radio", 10, 10, double.NaN, 20, 10, "Width")]
    public void FormField_NonFiniteNumbers_AreRefusedNamingThem(
        string kind, double x, double y, double width, double height, double fontSize, string member)
    {
        Tetitulz.Pdf.Base.Operations.Forms.FormFieldBase field = kind switch
        {
            "text" => new Tetitulz.Pdf.Base.Operations.Forms.FormTextField
                { FieldName = "f", X = x, Y = y, Width = width, Height = height, FontSize = fontSize },
            "combo" => new Tetitulz.Pdf.Base.Operations.Forms.FormComboField
                { FieldName = "f", X = x, Y = y, Width = width, Height = height, FontSize = fontSize },
            _ => new Tetitulz.Pdf.Base.Operations.Forms.FormRadioGroupField
            {
                FieldName = "f",
                Buttons = { new Tetitulz.Pdf.Base.Operations.Forms.RadioButtonOption("a", 0, x, y, width, height) },
            },
        };

        using var input = new MemoryStream(OnePage(), writable: false);
        using var output = new MemoryStream();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Tetitulz.Pdf.Base.Operations.Forms.PdfFormFieldBuilder.AddFields(input, output, new[] { field }));
        Assert.Equal("fields", ex.ParamName);
        Assert.Contains(member, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void FormField_SoundNumbers_AreWritten()
    {
        using var input = new MemoryStream(OnePage(), writable: false);
        using var output = new MemoryStream();
        Tetitulz.Pdf.Base.Operations.Forms.PdfFormFieldBuilder.AddFields(input, output, new[]
        {
            new Tetitulz.Pdf.Base.Operations.Forms.FormTextField { FieldName = "f", X = 10, Y = 10, FontSize = 0 },
        });
        Assert.Single(Tetitulz.Pdf.Base.Operations.Forms.PdfFormReader.GetFields(new MemoryStream(output.ToArray())));
    }

    /// <summary>
    /// A context of <c>int.MaxValue</c> characters is the whole text on either side of the match: the
    /// end was computed as an <c>int</c>, overflowed, and the snippet's <c>Append</c> threw.
    /// </summary>
    [Fact]
    public void Search_AnUnboundedContext_IsTheWholeText()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("alpha beta gamma");
        var hit = Assert.Single(Tetitulz.Pdf.Base.Operations.Extraction.PdfTextSearch.Search(
            new MemoryStream(pdf), "beta", ignoreCase: true, contextChars: int.MaxValue));
        Assert.Contains("alpha", hit.ContextSnippet, StringComparison.Ordinal);
        Assert.Contains("gamma", hit.ContextSnippet, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(double.NaN, 595, 842, 0, "Dpi")]
    [InlineData(96, double.NaN, 842, 0, "PageWidthPoints")]
    [InlineData(96, 595, double.PositiveInfinity, 0, "PageHeightPoints")]
    [InlineData(96, 595, 842, double.NaN, "MarginPoints")]
    public void ImageToPdf_NonFiniteOption_IsRefused(double dpi, double pageW, double pageH, double margin, string member)
    {
        using var output = new MemoryStream();
        var options = new ImageToPdfOptions { Dpi = dpi, PageWidthPoints = pageW, PageHeightPoints = pageH, MarginPoints = margin };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PdfImageToPdf.Create(new[] { TinyPng() }, output, options));
        Assert.Equal("options", ex.ParamName);
        Assert.Contains(member, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageToPdf_SoundOptions_WriteOnePage()
    {
        using var output = new MemoryStream();
        var result = PdfImageToPdf.Create(new[] { TinyPng() }, output, new ImageToPdfOptions());
        Assert.Equal(1, result.PagesWritten);
        Assert.Equal(1, PageCount(output));
    }

    [Theory]
    [InlineData(0f, 50f, 10f, 100f, "ModuleWidth")]
    [InlineData(float.NaN, 50f, 10f, 100f, "ModuleWidth")]
    [InlineData(2f, 0f, 10f, 100f, "Height")]
    [InlineData(2f, 50f, -1f, 100f, "QuietZone")]
    [InlineData(2f, 50f, 10f, 0f, "Size")]
    public void Barcode_NonPositiveOrNonFiniteOption_IsRefused(float module, float height, float quiet, float size, string member)
    {
        var options = new BarcodeOptions { ModuleWidth = module, Height = height, QuietZone = quiet, Size = size };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfBarcodeWriter.GenerateContentStream("ABC", BarcodeType.Code128, 10f, 10f, options));
        Assert.Equal("options", ex.ParamName);
        Assert.Contains(member, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Barcode_NonFiniteOrigin_IsRefusedNamingIt()
    {
        Assert.Equal("x", Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfBarcodeWriter.GenerateContentStream("ABC", BarcodeType.Code128, float.NaN, 10f)).ParamName);
        Assert.Equal("y", Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfBarcodeWriter.GenerateContentStream("ABC", BarcodeType.Code128, 10f, float.PositiveInfinity)).ParamName);

        // The control: a finite origin draws bars.
        Assert.Contains(" re", PdfBarcodeWriter.GenerateContentStream("ABC", BarcodeType.Code128, 10f, 10f), StringComparison.Ordinal);
    }

    [Fact]
    public void VisualDiff_BadVerdictParameters_AreRefusedNamingThem()
    {
        byte[] pdf = OnePage();
        using var left = new MemoryStream(pdf, writable: false);
        using var right = new MemoryStream(pdf, writable: false);

        Assert.Equal("dpi", Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfVisualDiff.Compare(left, right, dpi: 0)).ParamName);
        Assert.Equal("thresholdPct", Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfVisualDiff.Compare(left, right, thresholdPct: float.NaN)).ParamName);
        Assert.Equal("thresholdPct", Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfVisualDiff.Compare(left, right, thresholdPct: -1f)).ParamName);
        Assert.Equal("pixelTolerance", Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfVisualDiff.Compare(left, right, pixelTolerance: -1)).ParamName);

        // The control: the same pair, with sound parameters, is equal.
        Assert.True(PdfVisualDiff.Compare(left, right, dpi: 36, thresholdPct: 0f, pixelTolerance: 0).AreEqual);
    }

    [Fact]
    public void UriLink_NonFiniteCorner_IsRefusedNamingIt()
    {
        using var input = new MemoryStream(OnePage(), writable: false);
        using var output = new MemoryStream();

        Assert.Equal("x1", Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfAnnotationWriter.AddUriLink(input, output, 0, double.NaN, 0, 10, 10, "https://example.org/")).ParamName);
        Assert.Equal("y2", Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfAnnotationWriter.AddUriLink(input, output, 0, 0, 0, 10, double.PositiveInfinity, "https://example.org/")).ParamName);

        var links = new[] { new PdfAnnotationWriter.UriLinkRequest { PageIndex = 0, X1 = 0, Y1 = 0, X2 = double.NaN, Y2 = 10, Uri = "https://example.org/" } };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PdfAnnotationWriter.AddUriLinks(input, output, links));
        Assert.Equal("links", ex.ParamName);
        Assert.Contains("[0].X2", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);

        // The control: a finite link is written and counted.
        input.Position = 0;
        Assert.Equal(1, PdfAnnotationWriter.AddUriLinks(input, output,
            new[] { new PdfAnnotationWriter.UriLinkRequest { PageIndex = 0, X1 = 10, Y1 = 10, X2 = 100, Y2 = 30, Uri = "https://example.org/" } }));
    }

    /// <summary>
    /// Every single-annotation door refuses a non-finite number by name: let through, it would reach
    /// the /Rect, /QuadPoints, /C or /BS as written zeros, and the write would report success.
    /// </summary>
    [Fact]
    public void Annotations_NonFiniteNumbers_AreRefusedNamingThem()
    {
        using var input = new MemoryStream(OnePage(), writable: false);
        using var output = new MemoryStream();
        string Refused(Action write) => Assert.Throws<ArgumentOutOfRangeException>(write).ParamName;

        Assert.Equal("y", Refused(() => PdfAnnotationWriter.AddTextNote(input, output, 0, 10, double.NaN, "note")));
        Assert.Equal("x2", Refused(() => PdfAnnotationWriter.AddHighlight(input, output, 0, 0, 0, double.PositiveInfinity, 10)));
        Assert.Equal("g", Refused(() => PdfAnnotationWriter.AddUnderline(input, output, 0, 0, 0, 10, 10, g: float.NaN)));
        Assert.Equal("y1", Refused(() => PdfAnnotationWriter.AddStrikeOut(input, output, 0, 0, double.NegativeInfinity, 10, 10)));
        Assert.Equal("width", Refused(() => PdfAnnotationWriter.AddFreeText(input, output, 0, 0, 0, double.NaN, 10, "t")));
        Assert.Equal("fontSize", Refused(() => PdfAnnotationWriter.AddFreeText(input, output, 0, 0, 0, 10, 10, "t", fontSize: float.NaN)));
        Assert.Equal("height", Refused(() => PdfAnnotationWriter.AddStamp(input, output, 0, 0, 0, 10, double.NaN)));
        Assert.Equal("lineWidth", Refused(() => PdfAnnotationWriter.AddRectangle(input, output, 0, 0, 0, 10, 10, lineWidth: float.NaN)));
        Assert.Equal(0, output.Length);

        // The control: the same doors with finite numbers write.
        input.Position = 0;
        PdfAnnotationWriter.AddHighlight(input, output, 0, 0, 0, 10, 10);
        Assert.True(output.Length > input.Length);
    }

    [Fact]
    public void WriteAnnotations_ANonFiniteImportedRect_IsSkippedAndSaid()
    {
        // An imported review's numbers are the file's: skipped and reported, as a stale page is.
        using var input = new MemoryStream(OnePage(), writable: false);
        using var output = new MemoryStream();
        var annotations = new[]
        {
            new Tetitulz.Pdf.Base.Operations.Forms.FormDataAnnotation("Square") { PageIndex = 0, Rect = new[] { 0, 0, double.NaN, 10 } },
            new Tetitulz.Pdf.Base.Operations.Forms.FormDataAnnotation("Square") { PageIndex = 0, Rect = new[] { 0d, 0, 10, 10 } },
        };

        Assert.Equal(1, PdfAnnotationWriter.WriteAnnotations(input, output, annotations));
        Assert.Contains("not a finite number", PdfAnnotationWriter.LastAnnotationsSkipped, StringComparison.Ordinal);
    }

    /// <summary>
    /// The inserter had the check before this wave, as a private copy nothing tested; it is the
    /// shared helper's now, and this is the test the copy never had.
    /// </summary>
    [Fact]
    public void InsertRaw_NonFinitePlacement_IsRefusedNamingIt()
    {
        using var input = new MemoryStream(OnePage(), writable: false);
        using var output = new MemoryStream();
        byte[] onePixel = new byte[3];

        Assert.Equal("xPt", Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfImageInserter.InsertRaw(input, output, 0, onePixel, 1, 1, double.NaN, 10, 10, 10)).ParamName);
        Assert.Equal("heightPt", Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfImageInserter.InsertRaw(input, output, 0, onePixel, 1, 1, 10, 10, 10, double.PositiveInfinity)).ParamName);

        // The control: a finite placement writes the document.
        input.Position = 0;
        PdfImageInserter.InsertRaw(input, output, 0, onePixel, 1, 1, 10, 10, 10, 10);
        Assert.Equal(1, PageCount(output));
    }

    [Fact]
    public void GcrFactor_NonFinite_IsRefusedAtTheCallAndAtTheSetter()
    {
        using var input = new MemoryStream(OnePage(), writable: false);
        using var output = new MemoryStream();
        Assert.Equal("gcrFactor", Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfColorSpaceConverter.ConvertRgbToCmyk(input, output, double.NaN)).ParamName);

#pragma warning disable CS0618 // the obsolete process-wide factor's own guard is under test
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfColorSpaceConverter.GcrFactor = double.NaN);
        // A refused set leaves the process-wide factor a number — a setter accepting NaN would
        // poison every later conversion on every thread.
        Assert.False(double.IsNaN(PdfColorSpaceConverter.GcrFactor));
#pragma warning restore CS0618

        // The control: a finite factor over a page with no images converts nothing and succeeds.
        input.Position = 0;
        Assert.Equal(0, PdfColorSpaceConverter.ConvertRgbToCmyk(input, output, 0.5));
    }
}
