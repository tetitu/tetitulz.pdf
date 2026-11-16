// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    public class DocxToPdfOptionsTests
    {
        [Fact]
        public void Default_HasExpectedValues()
        {
            var options = DocxToPdfOptions.Default;

            Assert.Equal(ConversionEngine.BuiltIn, options.Engine);
            Assert.Null(options.LibreOfficePath);
            Assert.Equal(612, options.PageWidth);
            Assert.Equal(792, options.PageHeight);
            Assert.Equal(72, options.MarginTop);
            Assert.Equal(72, options.MarginBottom);
            Assert.Equal(72, options.MarginLeft);
            Assert.Equal(72, options.MarginRight);
            Assert.Equal(12, options.DefaultFontSize);
            Assert.True(options.EmbedImages);
            Assert.True(options.CompressStreams);
            Assert.True(options.UseDocumentMargins);
        }

        [Fact]
        public void Properties_CanBeModified()
        {
            var options = new DocxToPdfOptions
            {
                PageWidth = 595.28,
                PageHeight = 841.89,
                MarginTop = 36,
                MarginBottom = 36,
                MarginLeft = 54,
                MarginRight = 54,
                DefaultFontSize = 10,
                EmbedImages = false,
                CompressStreams = false,
                UseDocumentMargins = false
            };

            Assert.Equal(595.28, options.PageWidth);
            Assert.Equal(841.89, options.PageHeight);
            Assert.Equal(36, options.MarginTop);
            Assert.Equal(54, options.MarginLeft);
            Assert.Equal(10, options.DefaultFontSize);
            Assert.False(options.EmbedImages);
            Assert.False(options.CompressStreams);
            Assert.False(options.UseDocumentMargins);
        }

        [Fact]
        public void Engine_CanBeSet()
        {
            var options = new DocxToPdfOptions { Engine = ConversionEngine.LibreOffice };
            Assert.Equal(ConversionEngine.LibreOffice, options.Engine);

            options.Engine = ConversionEngine.MicrosoftWord;
            Assert.Equal(ConversionEngine.MicrosoftWord, options.Engine);
        }

        [Fact]
        public void LibreOfficePath_CanBeSet()
        {
            var options = new DocxToPdfOptions
            {
                Engine = ConversionEngine.LibreOffice,
                LibreOfficePath = @"C:\Program Files\LibreOffice\program\soffice.exe"
            };
            Assert.Equal(@"C:\Program Files\LibreOffice\program\soffice.exe", options.LibreOfficePath);
        }
    }
}
