// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Plugins.Text;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Text.Tests;

/// <summary>
/// <see cref="TextExportPlugin"/> reached the way a host reaches it: <b>registered</b> with
/// <see cref="PdfPluginHost"/> and invoked through <c>PdfOperations.Export</c>.
/// </summary>
/// <remarks>
/// Calling <c>plugin.Export(doc, …)</c> directly — which is what the rest of this project's tests
/// do — exercises the adapter but not the thing that can actually be wrong: whether the host can
/// find it by format key, and whether it survives the document the facade hands it (the facade
/// opens the PDF itself and passes a <c>PdfDocument</c> whose stream is positioned wherever
/// parsing left it). A defect in either is invisible to a direct call. This is the lesson from
/// GAPS.md §15e, where two renderer plugins passed every direct-call test while being broken in
/// the only way they are ever used.
/// </remarks>
public class TextExportPluginDispatchTests
{
    [Fact]
    public void Export_ThroughTheFacade_FindsThePluginByFormatKeyAndWritesTheText()
    {
        PdfPluginHost.Instance.ResetForTesting();
        try
        {
            PdfPluginHost.Instance.Register(new TextExportPlugin());

            using var input = new MemoryStream(
                MinimalPdfBuilder.CreateMinimalPdf("ignored",
                    "BT /F1 12 Tf 1 0 0 1 100 700 Tm (HELLOEXPORT) Tj ET"));
            using var output = new MemoryStream();

            PdfOperations.Export(input, output, "txt");

            string text = Encoding.UTF8.GetString(output.ToArray());
            Assert.Contains("HELLOEXPORT", text);
        }
        finally
        {
            PdfPluginHost.Instance.ResetForTesting();
        }
    }

    /// <summary>
    /// The format key is matched case-insensitively by <c>FindExportPlugin</c>, so a host passing
    /// "TXT" must not silently get "no plugin registered".
    /// </summary>
    [Fact]
    public void Export_ThroughTheFacade_MatchesTheFormatKeyCaseInsensitively()
    {
        PdfPluginHost.Instance.ResetForTesting();
        try
        {
            PdfPluginHost.Instance.Register(new TextExportPlugin());

            using var input = new MemoryStream(
                MinimalPdfBuilder.CreateMinimalPdf("ignored",
                    "BT /F1 12 Tf 1 0 0 1 100 700 Tm (HELLOEXPORT) Tj ET"));
            using var output = new MemoryStream();

            PdfOperations.Export(input, output, "TXT");

            Assert.Contains("HELLOEXPORT", Encoding.UTF8.GetString(output.ToArray()));
        }
        finally
        {
            PdfPluginHost.Instance.ResetForTesting();
        }
    }
}
