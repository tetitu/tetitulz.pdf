// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Plugins.JavaScript.Tests;

/// <summary>
/// The pdfscripting.com sample forms (<c>tools/corpus-fetch -Only pdfscripting</c>) opened through
/// <see cref="PdfOperations.ExecuteDocumentScripts(string, PdfJsExecutionContext)"/> with this engine:
/// the ones the object model covers run every script without an error, and each of the rest stops
/// for the reason GAPS.md §25 records — so closing one of those gaps fails here until its row moves.
/// Skips when the set is not in the corpus.
/// </summary>
[Collection(PluginHostCollection.Name)]
public sealed class PdfScriptingDocumentScriptsTests : IDisposable
{
    private const string Sentinel = "SimpleFormCalculations.pdf";
    private readonly JintJavaScriptPlugin _plugin = new();

    public PdfScriptingDocumentScriptsTests() => PdfPluginHost.Instance.Register(_plugin);

    public void Dispose() => PdfPluginHost.Instance.Unregister(_plugin.Name);

    [CorpusTheory(Sentinel)]
    [InlineData("AcquireMenuItemNames.pdf", null)]
    [InlineData("AcroForm_BasicToggle.pdf", null)]
    [InlineData("AcroForm_ToggleButton_Sample.pdf", null)]
    [InlineData("ConditionalCalcScripts.pdf", null)]
    [InlineData("DynamicEmail_AcroForm.pdf", null)]
    [InlineData("JavaScriptClock.pdf", null)]
    [InlineData("PDFS_CopyPastListEntries.pdf", null)]
    [InlineData("SimpleFormCalculations.pdf", null)]
    [InlineData("ImExportAttachSample.pdf", null)]                                     // this.dataObjects
    [InlineData("JSPopupCalendar.pdf", null)]                                          // a button's /MK /CA caption
    [InlineData("BouncingButton.pdf", null)]                                           // Matrix2D
    [InlineData("TheFlyv3_EN4Rdr.pdf", null)]                                          // Matrix2D
    // Its click scripts run at open: the matrix radio group has nothing selected, so the fields they
    // build the names of ("a" + "Off") are not there — a viewer runs them after a click, when one is.
    [InlineData("Matrix2DOperations.pdf", "null")]
    public void TheDocumentsScripts_RunAsTheObjectModelCoversThem(string file, string? stopsWith)
    {
        string? path = TestCorpus.Find(file);
        Assert.True(path != null, $"the pdfscripting set is incomplete: {file} is missing beside {Sentinel}");

        // The samples are viewer documents: their attachments are handed over, as a viewer would.
        PdfJsExecutionContext ctx = PdfOperations.ExecuteDocumentScripts(path!, new PdfJsExecutionContext { ExposeAttachments = true });

        if (stopsWith == null) Assert.True(ctx.Error == null, $"{file}: {ctx.Error}");
        else
        {
            Assert.NotNull(ctx.Error);
            Assert.Contains(stopsWith, ctx.Error!.Message, StringComparison.Ordinal);
        }
    }
}
