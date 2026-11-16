// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Core.Text;
using System.Text;
using Tetitulz.Pdf.TestHelpers;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

public class PdfSanitizerTests
{
    [Fact]
    /// <summary>
    /// Sanitizing a document that has nothing dangerous in it must preserve it: same page count,
    /// same visible text. "At least one page" would hold for a document whose content the
    /// sanitizer had stripped along with the threats.
    /// </summary>
    public void Sanitize_HarmlessDocument_KeepsItsPagesAndText()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Sanitize Me");

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        PdfSanitizer.Sanitize(input, output);

        using var before = PdfDocument.Open(new MemoryStream(pdfBytes));
        using var after  = PdfDocument.Open(new MemoryStream(output.ToArray()));

        Assert.Equal(before.Pages.Count, after.Pages.Count);
        Assert.Contains("Sanitize Me", PdfTextExtractor.ExtractText(after.Pages[0]));
    }

    [Fact]
    public void Sanitize_RemovesOpenAction()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreatePdfWithJavaScript();

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        PdfSanitizer.Sanitize(input, output, new SanitizeOptions
        {
            RemoveJavaScript = true,
            RemoveOpenAction = true
        });

        output.Position = 0;
        using var doc = PdfDocument.Open(output);

        // The catalog should not have /OpenAction
        var catalogDict = doc.Catalog.Dictionary;
        Assert.False(catalogDict.ContainsKey(PdfName.OpenAction));
    }

    [Fact]
    /// <summary>
    /// The <b>default</b> options must already remove active content — that is what makes
    /// <c>Sanitize(input, output)</c> safe to call without thinking. The predecessor was named
    /// "ProducesCleanPdf" and checked only that the result had a page, so it passed with every
    /// sanitisation option defaulted to off.
    /// </summary>
    public void Sanitize_WithDefaults_RemovesJavaScriptWithoutBeingAsked()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreatePdfWithJavaScript();
        Assert.Contains("app.alert", Encoding.ASCII.GetString(pdfBytes));

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        PdfSanitizer.Sanitize(input, output);   // no options at all

        byte[] produced = output.ToArray();
        Assert.DoesNotContain("app.alert", Encoding.ASCII.GetString(produced));

        using var doc = PdfDocument.Open(new MemoryStream(produced));
        Assert.Single(doc.Pages);
        Assert.False(doc.Catalog.Dictionary.ContainsKey(PdfName.OpenAction));
    }

    [Fact]
    public void Sanitize_FilePaths_Works()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("File Sanitize");
        var inputFile = TempFiles.NewPath();
        var outputFile = TempFiles.NewPath();

        try
        {
            File.WriteAllBytes(inputFile, pdfBytes);
            PdfSanitizer.Sanitize(inputFile, outputFile);

            Assert.True(File.Exists(outputFile));
            using var doc = PdfDocument.Open(outputFile);
            Assert.True(doc.Pages.Count > 0);
        }
        finally
        {
            if (File.Exists(inputFile)) File.Delete(inputFile);
            if (File.Exists(outputFile)) File.Delete(outputFile);
        }
    }
}
