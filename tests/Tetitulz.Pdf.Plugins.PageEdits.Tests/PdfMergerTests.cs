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

public class PdfMergerTests
{
    [Fact]
    public void Merge_TwoPdfs_CombinesPages()
    {
        byte[] pdf1 = MinimalPdfBuilder.CreateMinimalPdf("Doc 1");
        byte[] pdf2 = MinimalPdfBuilder.CreateMinimalPdf("Doc 2");

        using var input1 = new MemoryStream(pdf1);
        using var input2 = new MemoryStream(pdf2);
        using var output = new MemoryStream();

        PdfMerger.Merge(output, input1, input2);

        output.Position = 0;
        Assert.True(output.Length > 0);

        // Verify the merged PDF has 2 pages
        using var doc = PdfDocument.Open(output);
        Assert.Equal(2, doc.Pages.Count);
    }

    [Fact]
    public void Merge_ThreePdfs_CombinesAllPages()
    {
        byte[] pdf1 = MinimalPdfBuilder.CreateMinimalPdf("A");
        byte[] pdf2 = MinimalPdfBuilder.CreateMultiPagePdf(2);
        byte[] pdf3 = MinimalPdfBuilder.CreateMinimalPdf("C");

        using var input1 = new MemoryStream(pdf1);
        using var input2 = new MemoryStream(pdf2);
        using var input3 = new MemoryStream(pdf3);
        using var output = new MemoryStream();

        PdfMerger.Merge(output, input1, input2, input3);

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.Equal(4, doc.Pages.Count); // 1 + 2 + 1
    }

    [Fact]
    public void Merge_FilePaths_Works()
    {
        byte[] pdf1 = MinimalPdfBuilder.CreateMinimalPdf("File1");
        byte[] pdf2 = MinimalPdfBuilder.CreateMinimalPdf("File2");

        var file1 = TempFiles.NewPath();
        var file2 = TempFiles.NewPath();
        var outputFile = TempFiles.NewPath();

        try
        {
            File.WriteAllBytes(file1, pdf1);
            File.WriteAllBytes(file2, pdf2);

            PdfMerger.Merge(outputFile, file1, file2);

            Assert.True(File.Exists(outputFile));
            using var doc = PdfDocument.Open(outputFile);
            Assert.Equal(2, doc.Pages.Count);
        }
        finally
        {
            if (File.Exists(file1)) File.Delete(file1);
            if (File.Exists(file2)) File.Delete(file2);
            if (File.Exists(outputFile)) File.Delete(outputFile);
        }
    }
}
