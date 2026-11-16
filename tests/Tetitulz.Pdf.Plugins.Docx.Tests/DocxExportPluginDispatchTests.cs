// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Plugins.Docx;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Docx.Tests;

/// <summary>
/// <see cref="DocxExportPlugin"/> reached the way a host reaches it: <b>registered</b> with
/// <see cref="PdfPluginHost"/> and invoked through <c>PdfOperations.Export</c>.
/// </summary>
public class DocxExportPluginDispatchTests
{
    [Fact]
    public void Export_ThroughTheFacade_FindsThePluginByFormatKeyAndWritesADocxPackage()
    {
        PdfPluginHost.Instance.ResetForTesting();
        try
        {
            PdfPluginHost.Instance.Register(new DocxExportPlugin());

            using var input = new MemoryStream(
                MinimalPdfBuilder.CreateMinimalPdf("ignored",
                    "BT /F1 12 Tf 1 0 0 1 100 700 Tm (HELLOEXPORT) Tj ET"));
            using var output = new MemoryStream();

            PdfOperations.Export(input, output, "docx");

            byte[] bytes = output.ToArray();
            Assert.True(bytes.Length > 4, "the docx export wrote nothing usable.");

            // A .docx is an OPC (ZIP) package: "PK\x03\x04". Asserting the container signature is
            // what separates "produced a document" from "produced some bytes".
            Assert.Equal(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, bytes.Take(4).ToArray());
        }
        finally
        {
            PdfPluginHost.Instance.ResetForTesting();
        }
    }
}
