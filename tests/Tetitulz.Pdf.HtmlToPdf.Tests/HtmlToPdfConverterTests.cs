// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    public class HtmlToPdfConverterTests
    {
        [Fact]
        public void ConvertHtml_NullHtml_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                HtmlToPdfConverter.ConvertHtml((string)null!, "output.pdf"));
        }

        [Fact]
        public void ConvertHtml_NullOutputPath_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                HtmlToPdfConverter.ConvertHtml("<html></html>", (string)null!));
        }

        [Fact]
        public void ConvertHtmlToBytes_NullHtml_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                HtmlToPdfConverter.ConvertHtmlToBytes(null!));
        }

        [Fact]
        public void ConvertUrl_NullUrl_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                HtmlToPdfConverter.ConvertUrl((string)null!, "output.pdf"));
        }

        [Fact]
        public void ConvertFile_NullInputPath_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                HtmlToPdfConverter.ConvertFile(null!, "output.pdf"));
        }

        [Fact]
        public void ConvertHtml_NullOutputStream_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                HtmlToPdfConverter.ConvertHtml("<html></html>", (Stream)null!));
        }

        [Fact]
        public void ConvertUrl_NullOutputStream_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                HtmlToPdfConverter.ConvertUrl("https://example.com", (Stream)null!));
        }
    }
}
