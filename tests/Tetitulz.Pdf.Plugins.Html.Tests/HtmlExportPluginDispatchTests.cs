// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Plugins.Html;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Html.Tests;

/// <summary>
/// <see cref="HtmlExportPlugin"/> reached the way a host reaches it: <b>registered</b> with
/// <see cref="PdfPluginHost"/> and invoked through <c>PdfOperations.Export</c>.
/// </summary>
/// <remarks>
/// Only the PDF→HTML <i>export</i> direction is covered here; the import direction has its own
/// facade-dispatch case in <c>HtmlImportConversionTests</c>. This note used to say import went
/// through Playwright's headless browser and so could not be tested — it does not, and it can:
/// <c>HtmlToPdfOptions.RenderEngine</c> defaults to <c>Managed</c>.
/// </remarks>
[Collection(PluginHostCollection.Name)]
public class HtmlExportPluginDispatchTests
{
    [Fact]
    public void Export_ThroughTheFacade_FindsThePluginByFormatKeyAndWritesHtml()
    {
        PdfPluginHost.Instance.ResetForTesting();
        try
        {
            PdfPluginHost.Instance.Register(new HtmlExportPlugin());

            using var input = new MemoryStream(
                MinimalPdfBuilder.CreateMinimalPdf("ignored",
                    "BT /F1 12 Tf 1 0 0 1 100 700 Tm (HELLOEXPORT) Tj ET"));
            using var output = new MemoryStream();

            PdfOperations.Export(input, output, "html");

            string html = Encoding.UTF8.GetString(output.ToArray());
            Assert.NotEqual(0, html.Trim().Length);
            Assert.Contains("<", html);
            // The page's text must reach the output, or this is an empty shell that would still
            // satisfy any "did it write something" check.
            Assert.Contains("HELLOEXPORT", html);
        }
        finally
        {
            PdfPluginHost.Instance.ResetForTesting();
        }
    }
}
