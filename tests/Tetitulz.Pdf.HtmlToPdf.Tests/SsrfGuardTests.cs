// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading.Tasks;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Regression tests for the SSRF / local-file scheme guard (finding H1 of an
    /// earlier hardening round).
    /// URL validation runs before any browser is launched, so these do not need
    /// Playwright browsers installed.
    /// </summary>
    public class SsrfGuardTests
    {
        [Theory]
        [InlineData("file:///etc/passwd")]
        [InlineData("file:///C:/Windows/win.ini")]
        [InlineData("ftp://example.com/x")]
        [InlineData("not-a-url")]
        public async Task ConvertUrl_DisallowedScheme_Throws(string url)
        {
            await Assert.ThrowsAsync<HtmlToPdfException>(
                () => HtmlToPdfConverter.ConvertUrlToBytesAsync(url));
        }

        [Fact]
        public async Task ConvertFile_MissingFile_ThrowsFileNotFound()
        {
            await Assert.ThrowsAsync<System.IO.FileNotFoundException>(
                () => HtmlToPdfConverter.ConvertFileToBytesAsync(
                    @"Z:\definitely\missing\file-" + Guid.NewGuid().ToString("N") + ".html"));
        }
    }
}
