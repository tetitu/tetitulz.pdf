// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Document;
using System.Text;
using Tetitulz.Pdf.DocxToPdf.Tests.Helpers;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    public class DocxToPdfConverterTests
    {
        [Fact]
        public void Convert_MinimalDocx_ProducesValidPdf()
        {
            var docxBytes = MinimalDocxBuilder.CreateMinimalDocx("Hello World");
            using var input = new MemoryStream(docxBytes);
            using var output = new MemoryStream();

            DocxToPdfConverter.Convert(input, output, DocxToPdfOptions.Default);

            var pdfBytes = output.ToArray();
            Assert.True(pdfBytes.Length > 0);
            Assert.Equal((byte)'%', pdfBytes[0]);
            Assert.Equal((byte)'P', pdfBytes[1]);
            Assert.Equal((byte)'D', pdfBytes[2]);
            Assert.Equal((byte)'F', pdfBytes[3]);
        }

        [Fact]
        public void Convert_StreamToStream_ProducesValidPdf()
        {
            var docxBytes = MinimalDocxBuilder.CreateMinimalDocx("Stream test");
            using var input = new MemoryStream(docxBytes);
            using var output = new MemoryStream();

            DocxToPdfConverter.Convert(input, output);

            var pdfBytes = output.ToArray();
            Assert.True(pdfBytes.Length > 0);
            // Verify contains %%EOF
            var text = System.Text.Encoding.ASCII.GetString(pdfBytes);
            Assert.Contains("%%EOF", text);
        }

        [Fact]
        public void ConvertToBytes_FromByteArray_ReturnsValidPdf()
        {
            var docxBytes = MinimalDocxBuilder.CreateMinimalDocx("Bytes test");

            var pdfBytes = DocxToPdfConverter.ConvertToBytes(docxBytes);

            Assert.NotNull(pdfBytes);
            Assert.True(pdfBytes.Length > 0);
            Assert.Equal((byte)'%', pdfBytes[0]);
            Assert.Equal((byte)'P', pdfBytes[1]);
        }

        [Fact]
        public void Convert_FilePaths_ProducesValidPdf()
        {
            var docxBytes = MinimalDocxBuilder.CreateMinimalDocx("File test");
            var inputPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.docx");
            var outputPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.pdf");

            try
            {
                File.WriteAllBytes(inputPath, docxBytes);
                DocxToPdfConverter.Convert(inputPath, outputPath);

                Assert.True(File.Exists(outputPath));
                var pdfBytes = File.ReadAllBytes(outputPath);
                Assert.True(pdfBytes.Length > 0);
                Assert.Equal((byte)'%', pdfBytes[0]);
            }
            finally
            {
                if (File.Exists(inputPath)) File.Delete(inputPath);
                if (File.Exists(outputPath)) File.Delete(outputPath);
            }
        }

        [Fact]
        public void Convert_NullInputPath_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                DocxToPdfConverter.Convert((string)null!, "output.pdf"));
        }

        [Fact]
        public void Convert_NullOutputStream_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                DocxToPdfConverter.Convert(new MemoryStream(), null!));
        }

        [Fact]
        public void Convert_WithBoldItalic_ProducesValidPdf()
        {
            var docxBytes = MinimalDocxBuilder.CreateDocxWithStyles("Bold Italic", bold: true, italic: true);
            using var input = new MemoryStream(docxBytes);
            using var output = new MemoryStream();

            DocxToPdfConverter.Convert(input, output);

            var pdfBytes = output.ToArray();
            Assert.True(pdfBytes.Length > 0);
            // The face the text is SET in, not a name somewhere in the file.
            Assert.Equal("Helvetica-BoldOblique", Tetitulz.Pdf.TestHelpers.PdfContentProbe.BaseFontOf(pdfBytes, "Bold"));
        }

        [Fact]
        public void Convert_WithTable_ProducesValidPdf()
        {
            var docxBytes = MinimalDocxBuilder.CreateDocxWithTable(3, 3);
            using var input = new MemoryStream(docxBytes);
            using var output = new MemoryStream();

            DocxToPdfConverter.Convert(input, output);

            var pdfBytes = output.ToArray();
            Assert.True(pdfBytes.Length > 0);
            Assert.Equal((byte)'%', pdfBytes[0]);
        }

        /// <summary>
        /// Twenty paragraphs must survive the conversion as readable text in a parseable PDF.
        /// "Some bytes came back" is equally true of a converter that emitted an empty page — and
        /// for a converter, an empty page is the exact failure worth catching.
        /// </summary>
        [Fact]
        public void Convert_MultiParagraph_KeepsTheParagraphTextInTheOutput()
        {
            var docxBytes = MinimalDocxBuilder.CreateMultiParagraphDocx(20);
            using var input = new MemoryStream(docxBytes);
            using var output = new MemoryStream();

            DocxToPdfConverter.Convert(input, output);

            using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
            Assert.True(doc.Pages.Count >= 1);

            var text = new StringBuilder();
            foreach (var page in doc.Pages) text.Append(PdfTextExtractor.ExtractText(page));

            // The fixture numbers its paragraphs, so first and last prove nothing was dropped
            // at either end of the flow.
            Assert.Contains("Paragraph 1", text.ToString());
            Assert.Contains("Paragraph 20", text.ToString());
        }

        [Fact]
        public void Convert_WithOptions_RespectsPageSize()
        {
            var docxBytes = MinimalDocxBuilder.CreateMinimalDocx("A4 test");
            using var input = new MemoryStream(docxBytes);
            using var output = new MemoryStream();

            var options = new DocxToPdfOptions
            {
                PageWidth = 595.28, // A4
                PageHeight = 841.89,
                UseDocumentMargins = false
            };

            DocxToPdfConverter.Convert(input, output, options);

            // The name promises the page size is respected, so read the /MediaBox back. The
            // predecessor asserted only that bytes came back, which holds for any page size —
            // including the converter's default.
            using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
            var box = doc.Pages[0].MediaBox;

            Assert.Equal(595.28, ((PdfNumber)box[2]).DoubleValue, 1);
            Assert.Equal(841.89, ((PdfNumber)box[3]).DoubleValue, 1);
        }

        [Fact]
        public void Convert_BuiltInEngine_ExplicitlySet_ProducesValidPdf()
        {
            var docxBytes = MinimalDocxBuilder.CreateMinimalDocx("BuiltIn engine test");
            using var input = new MemoryStream(docxBytes);
            using var output = new MemoryStream();

            DocxToPdfConverter.Convert(input, output, new DocxToPdfOptions
            {
                Engine = ConversionEngine.BuiltIn
            });

            var pdfBytes = output.ToArray();
            Assert.True(pdfBytes.Length > 0);
            Assert.Equal((byte)'%', pdfBytes[0]);
            Assert.Equal((byte)'P', pdfBytes[1]);
        }

        [Fact]
        public void ConversionEngine_HasExpectedValues()
        {
            Assert.Equal(0, (int)ConversionEngine.BuiltIn);
            Assert.Equal(1, (int)ConversionEngine.LibreOffice);
            Assert.Equal(2, (int)ConversionEngine.MicrosoftWord);
        }

        [Fact]
        public void Convert_LibreOffice_InvalidPath_ThrowsDocxToPdfException()
        {
            var docxBytes = MinimalDocxBuilder.CreateMinimalDocx("LO test");
            using var input = new MemoryStream(docxBytes);
            using var output = new MemoryStream();

            Assert.Throws<DocxToPdfException>(() =>
                DocxToPdfConverter.Convert(input, output, new DocxToPdfOptions
                {
                    Engine = ConversionEngine.LibreOffice,
                    LibreOfficePath = @"C:\nonexistent\soffice.exe"
                }));
        }
    }
}
