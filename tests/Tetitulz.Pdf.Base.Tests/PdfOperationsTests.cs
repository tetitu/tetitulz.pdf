// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.TestHelpers;
using Tetitulz.Pdf.Base.Tests.Helpers;
using Xunit;
using Tetitulz.Pdf.Signing;

namespace Tetitulz.Pdf.Base.Tests;

public class PdfOperationsTests
{
    [Fact]
    public void ExtractText_FromStream_ReturnsText()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Test Content");
        using var ms = new MemoryStream(pdfBytes);
        string text = PdfOperations.ExtractText(ms);
        Assert.Contains("Test Content", text);
    }

    [Fact]
    public void ExtractText_FromFile_ReturnsText()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("File Content");
        var tempFile = TempFiles.NewPath();
        try
        {
            File.WriteAllBytes(tempFile, pdfBytes);
            string text = PdfOperations.ExtractText(tempFile);
            Assert.Contains("File Content", text);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ExtractText_SpecificPage_ReturnsPageText()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMultiPagePdf(3);
        var tempFile = TempFiles.NewPath();
        try
        {
            File.WriteAllBytes(tempFile, pdfBytes);
            string text = PdfOperations.ExtractText(tempFile, 2);
            Assert.Contains("Page 2", text);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Sign_ProducesValidSignedPdf()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Sign Test");
        var (pfxBytes, password) = TestCertificateGenerator.GenerateTestCertificate();

        var pfxFile = TempFiles.NewPath(".pfx");
        var inputFile = TempFiles.NewPath();
        var outputFile = TempFiles.NewPath();
        try
        {
            File.WriteAllBytes(pfxFile, pfxBytes);
            File.WriteAllBytes(inputFile, pdfBytes);

            PdfSigningOperations.Sign(inputFile, outputFile, pfxFile, password,
                reason: "Testing", location: "Test Lab");

            Assert.True(File.Exists(outputFile));
            byte[] signedBytes = File.ReadAllBytes(outputFile);
            Assert.True(signedBytes.Length > pdfBytes.Length);
        }
        finally
        {
            if (File.Exists(pfxFile)) File.Delete(pfxFile);
            if (File.Exists(inputFile)) File.Delete(inputFile);
            if (File.Exists(outputFile)) File.Delete(outputFile);
        }
    }
}
