// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// The pdfscripting.com sample forms (<c>tools/corpus-fetch -Only pdfscripting</c>) through the
/// lossless preset: each file comes out smaller with every page, field and script it had, and its
/// first page draws the same pixels. Skips when the set is not in the corpus.
/// </summary>
public class PdfScriptingSamplesOptimizeTests
{
    private const string Sentinel = "SimpleFormCalculations.pdf";

    /// <summary>File, pages, terminal fields, JavaScript actions.</summary>
    private static readonly (string File, int Pages, int Fields, int Scripts)[] Samples =
    {
        ("AcquireMenuItemNames.pdf", 1, 4, 4),
        ("AcroForm_BasicToggle.pdf", 1, 1, 1),
        ("AcroForm_ToggleButton_Sample.pdf", 1, 58, 65),
        ("BouncingButton.pdf", 1, 4, 6),
        ("ConditionalCalcScripts.pdf", 1, 15, 33),
        ("DynamicEmail_AcroForm.pdf", 2, 30, 11),
        ("ImExportAttachSample.pdf", 1, 9, 6),
        ("JavaScriptClock.pdf", 1, 20, 26),
        ("JSPopupCalendar.pdf", 2, 50, 70),
        ("Matrix2DOperations.pdf", 1, 59, 99),
        ("PDFS_CopyPastListEntries.pdf", 1, 4, 2),
        ("SimpleFormCalculations.pdf", 1, 12, 26),
        ("TheFlyv3_EN4Rdr.pdf", 1, 10, 8),
    };

    public static IEnumerable<object[]> Files() => Samples.Select(s => new object[] { s.File });

    [CorpusTheory(Sentinel)]
    [MemberData(nameof(Files))]
    public void Lossless_ShrinksTheForm_AndKeepsItsPagesFieldsScriptsAndDrawing(string file)
    {
        var sample = Samples.Single(s => s.File == file);
        string path = TestCorpus.Find(file);
        Assert.True(path != null, $"the pdfscripting set is incomplete: {file} is missing beside {Sentinel}");
        byte[] pdf = File.ReadAllBytes(path);

        var output = new MemoryStream();
        OptimizationResult result = PdfOptimizer.Optimize(Open(pdf), output, OptimizationOptions.Lossless);
        byte[] optimized = output.ToArray();

        Assert.False(result.FellBackToInput);
        Assert.True(optimized.Length < pdf.Length, $"{pdf.Length} -> {optimized.Length} bytes");
        Assert.Equal(sample.Pages, PdfOperations.GetPageCount(Open(optimized)));
        Assert.Equal(sample.Fields, PdfOperations.GetFormFields(Open(optimized)).Count);
        Assert.Equal(sample.Scripts,
            PdfOperations.ExtractActions(Open(optimized)).Count(a => a.ActionType == PdfActionType.JavaScript));
        Assert.Equal(PdfOperations.RenderPageToPixels(Open(pdf), 0, 36).Pixels,
            PdfOperations.RenderPageToPixels(Open(optimized), 0, 36).Pixels);
    }

    private static Stream Open(byte[] pdf) => new MemoryStream(pdf, writable: false);
}
