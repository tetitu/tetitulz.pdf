// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.IO;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.TestHelpers;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

public class PdfSplitterTests
{
    [Fact]
    public void SplitRange_ExtractsPageRange()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMultiPagePdf(5);

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        PdfSplitter.SplitRange(input, output, 2, 4);

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.Equal(3, doc.Pages.Count); // Pages 2, 3, 4
    }

    [Fact]
    public void SplitRange_SinglePage()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMultiPagePdf(3);

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        PdfSplitter.SplitRange(input, output, 1, 1);

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.Single(doc.Pages);
    }

    [Fact]
    public void Split_AllPages_CreatesIndividualFiles()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMultiPagePdf(3);
        var inputFile = TempFiles.NewPath();
        var outputDir = Path.Combine(Path.GetTempPath(), "pdf_split_test_" + Path.GetRandomFileName());

        try
        {
            File.WriteAllBytes(inputFile, pdfBytes);
            Directory.CreateDirectory(outputDir);

            PdfSplitter.Split(inputFile, outputDir);

            var outputFiles = Directory.GetFiles(outputDir, "*.pdf");
            Assert.Equal(3, outputFiles.Length);

            // Verify each file is a valid single-page PDF
            foreach (var file in outputFiles)
            {
                using var doc = PdfDocument.Open(file);
                Assert.Single(doc.Pages);
            }
        }
        finally
        {
            if (File.Exists(inputFile)) File.Delete(inputFile);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void SplitRange_FilePaths_Works()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMultiPagePdf(4);
        var inputFile = TempFiles.NewPath();
        var outputFile = TempFiles.NewPath();

        try
        {
            File.WriteAllBytes(inputFile, pdfBytes);
            PdfSplitter.SplitRange(inputFile, outputFile, 2, 3);

            using var doc = PdfDocument.Open(outputFile);
            Assert.Equal(2, doc.Pages.Count);
        }
        finally
        {
            if (File.Exists(inputFile)) File.Delete(inputFile);
            if (File.Exists(outputFile)) File.Delete(outputFile);
        }
    }
}
