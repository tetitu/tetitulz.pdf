// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Threading.Tasks;

namespace Tetitulz.Pdf.HtmlToPdf
{
    public sealed class HtmlToPdfConverterService : IHtmlToPdfConverter
    {
        public Task<byte[]> ConvertFileToBytesAsync(string inputHtmlPath, HtmlToPdfOptions? options = null)
            => HtmlToPdfConverter.ConvertFileToBytesAsync(inputHtmlPath, options);

        public Task<byte[]> ConvertHtmlToBytesAsync(string htmlContent, HtmlToPdfOptions? options = null)
            => HtmlToPdfConverter.ConvertHtmlToBytesAsync(htmlContent, options);

        public Task<byte[]> ConvertUrlToBytesAsync(string url, HtmlToPdfOptions? options = null)
            => HtmlToPdfConverter.ConvertUrlToBytesAsync(url, options);
    }
}
