// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// The pdfscripting.com sample forms (<c>tools/corpus-fetch -Only pdfscripting</c>): thirteen
/// Acrobat-authored AcroForms, each built around its JavaScript — calculations, format and
/// keystroke scripts, document-level functions, timers, attachments. Each one is read, rendered,
/// filled, flattened and sanitized, and every answer is checked against the file's own
/// characteristics pinned in <see cref="Samples"/>. Skips when the set is not in the corpus.
/// </summary>
/// <remarks>
/// The set states no redistribution licence, so it is fetched, never committed. Every widget and
/// field script in several of these files lies behind an indirect <c>/Annots</c> or <c>/Fields</c>
/// array, the layout <c>SanitizerIndirectArrayTests</c> holds the sanitizer to in-memory.
/// </remarks>
public class PdfScriptingSamplesTests
{
    /// <summary>The file the corpus attribute looks for; the rest of the set is fetched with it.</summary>
    internal const string Sentinel = "SimpleFormCalculations.pdf";

    /// <summary>
    /// File, pages, terminal fields, JavaScript actions, attachments, and a writable text field
    /// (null when the form has none).
    /// </summary>
    internal static readonly (string File, int Pages, int Fields, int Scripts, int Attachments, string TextField)[] Samples =
    {
        ("AcquireMenuItemNames.pdf", 1, 4, 4, 0, "MenuItemName"),
        ("AcroForm_BasicToggle.pdf", 1, 1, 1, 1, null),
        ("AcroForm_ToggleButton_Sample.pdf", 1, 58, 65, 1, "ClustTeeth.1"),
        ("BouncingButton.pdf", 1, 4, 6, 0, null),
        ("ConditionalCalcScripts.pdf", 1, 15, 33, 0, "Quantity1"),
        ("DynamicEmail_AcroForm.pdf", 2, 30, 11, 0, "Employee Number"),
        ("ImExportAttachSample.pdf", 1, 9, 6, 3, "AttachName"),
        ("JavaScriptClock.pdf", 1, 20, 26, 0, "SWStatus"),
        ("JSPopupCalendar.pdf", 2, 50, 70, 1, "FR_00000_Calendar.CalendarYear"),
        ("Matrix2DOperations.pdf", 1, 59, 99, 0, "ScaleXVal"),
        ("PDFS_CopyPastListEntries.pdf", 1, 4, 2, 1, "ListEntries"),
        ("SimpleFormCalculations.pdf", 1, 12, 26, 0, "Calc1_A"),
        ("TheFlyv3_EN4Rdr.pdf", 1, 10, 8, 0, null),
    };

    public static IEnumerable<object[]> Files() => Samples.Select(s => new object[] { s.File });

    public static IEnumerable<object[]> FilesWithATextField() =>
        Samples.Where(s => s.TextField != null).Select(s => new object[] { s.File });

    [CorpusTheory(Sentinel)]
    [MemberData(nameof(Files))]
    public void Parse_FindsThePagesFieldsScriptsAndAttachments(string file)
    {
        var sample = SampleOf(file);
        byte[] pdf = Read(file);

        Assert.Equal(sample.Pages, PdfOperations.GetPageCount(Open(pdf)));
        Assert.Equal(sample.Fields, PdfOperations.GetFormFields(Open(pdf)).Count);
        Assert.Equal(sample.Scripts, Scripts(pdf));
        Assert.Equal(sample.Attachments, PdfOperations.ListAttachments(Open(pdf)).Count);
    }

    [CorpusTheory(Sentinel)]
    [MemberData(nameof(Files))]
    public void View_EveryPageRendersInk_AndTheSvgIsWellFormed(string file)
    {
        var sample = SampleOf(file);
        byte[] pdf = Read(file);

        for (int page = 0; page < sample.Pages; page++)
            Assert.True(InkedPixels(PdfOperations.RenderPageToPixels(Open(pdf), page, 36)) > 1000,
                $"page {page} of {file} rendered (nearly) blank");

        var svg = XDocument.Parse(PdfOperations.RenderPageToSvg(TestCorpus.Find(file), 0));
        Assert.Equal("svg", svg.Root.Name.LocalName);
        Assert.True(svg.Descendants().Count() > 10, "the SVG of page 0 holds almost nothing");
    }

    [CorpusTheory(Sentinel)]
    [MemberData(nameof(FilesWithATextField))]
    public void Edit_AFilledTextFieldReadsBack_WithAnAppearance(string file)
    {
        var sample = SampleOf(file);
        byte[] pdf = Read(file);

        var output = new MemoryStream();
        var result = PdfOperations.FillForm(Open(pdf), output,
            new Dictionary<string, string> { [sample.TextField] = "Probe 123" }, new FormFillOptions());
        byte[] filled = output.ToArray();

        Assert.Equal(new[] { sample.TextField }, result.FilledFields);
        Assert.Empty(result.FieldsWithoutAppearance);
        Assert.Equal("Probe 123",
            PdfOperations.GetFormFields(Open(filled)).Single(f => f.FullyQualifiedName == sample.TextField).Value);
        Assert.Equal(sample.Fields, PdfOperations.GetFormFields(Open(filled)).Count);
        Assert.Equal(sample.Pages, PdfOperations.GetPageCount(Open(filled)));
    }

    [CorpusTheory(Sentinel)]
    [MemberData(nameof(Files))]
    public void Edit_FlatteningLeavesNoFieldsAndKeepsThePagesDrawn(string file)
    {
        var sample = SampleOf(file);
        byte[] pdf = Read(file);

        var output = new MemoryStream();
        PdfOperations.FlattenForm(Open(pdf), output);
        byte[] flat = output.ToArray();

        Assert.Empty(PdfOperations.GetFormFields(Open(flat)));
        Assert.Equal(sample.Pages, PdfOperations.GetPageCount(Open(flat)));
        Assert.True(InkedPixels(PdfOperations.RenderPageToPixels(Open(flat), 0, 36)) > 1000,
            "the flattened first page rendered (nearly) blank");
    }

    /// <summary>
    /// Every script and attachment goes; the pages, the fields and what page 0 draws stay, pixel
    /// for pixel.
    /// </summary>
    [CorpusTheory(Sentinel)]
    [MemberData(nameof(Files))]
    public void Sanitize_RemovesEveryScriptAndAttachment_AndKeepsTheFormAndItsDrawing(string file)
    {
        var sample = SampleOf(file);
        byte[] pdf = Read(file);

        var output = new MemoryStream();
        PdfSanitizer.Sanitize(Open(pdf), output);
        byte[] clean = output.ToArray();

        Assert.Equal(0, Scripts(clean));
        Assert.Empty(PdfOperations.ListAttachments(Open(clean)));
        Assert.Equal(sample.Pages, PdfOperations.GetPageCount(Open(clean)));
        Assert.Equal(sample.Fields, PdfOperations.GetFormFields(Open(clean)).Count);
        Assert.Equal(PdfOperations.RenderPageToPixels(Open(pdf), 0, 36).Pixels,
            PdfOperations.RenderPageToPixels(Open(clean), 0, 36).Pixels);
    }

    internal static (string File, int Pages, int Fields, int Scripts, int Attachments, string TextField) SampleOf(string file)
        => Samples.Single(s => s.File == file);

    internal static byte[] Read(string file)
    {
        string path = TestCorpus.Find(file);
        Assert.True(path != null, $"the pdfscripting set is incomplete: {file} is missing beside {Sentinel}");
        return File.ReadAllBytes(path);
    }

    private static Stream Open(byte[] pdf) => new MemoryStream(pdf, writable: false);

    private static int Scripts(byte[] pdf) =>
        PdfOperations.ExtractActions(Open(pdf)).Count(a => a.ActionType == PdfActionType.JavaScript);

    /// <summary>Pixels that are opaque and not near white.</summary>
    private static int InkedPixels(RenderedPage page)
    {
        int n = 0;
        for (int y = 0; y < page.Height; y++)
            for (int x = 0; x < page.Width; x++)
            {
                int i = y * page.Stride + x * 4;
                if (page.Pixels[i + 3] > 0 && (page.Pixels[i] < 240 || page.Pixels[i + 1] < 240 || page.Pixels[i + 2] < 240))
                    n++;
            }
        return n;
    }
}
