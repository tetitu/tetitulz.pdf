// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// One inline image referenced by many elements is decoded and written once. Each <c>&lt;img&gt;</c>
    /// (and each <c>background-image</c>) decoded a fresh byte array, which the exporter — keying on array
    /// identity (<c>ImageObjectShare</c>) — then held and wrote as its own XObject, so N references became
    /// N decodes and N copies (a large repeated <c>data:</c> image ended in OutOfMemoryException). A
    /// per-conversion cache, keyed by the <c>data:</c> URI (and by the resolved path for local files, as
    /// <c>LoadRemote</c> caches a URL), hands every reference the same array, so it is written once (2026-10-23).
    /// </summary>
    public class RepeatedImageDedupTests
    {
        private static string PngDataUri()
        {
            const int w = 400, h = 400;
            var samples = new byte[w * h * 3];
            for (int i = 0; i < samples.Length; i++) samples[i] = (byte)(i % 251);   // not a trivially tiny solid
            byte[] png = PdfRawImageEncoder.EncodePng(samples, w, h, 3);
            return "data:image/png;base64," + Convert.ToBase64String(png);
        }

        [Fact]
        public void TheSameInlineImageRepeated_IsWrittenOnce()
        {
            string uri = PngDataUri();
            const int n = 120;
            string html = "<p>" + string.Concat(Enumerable.Repeat("<img src=\"" + uri + "\">", n)) + "</p>";

            byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(html, Managed());

            Assert.NotEmpty(pdf);
            // One image XObject, not n — the dedup wrote the shared array once (pre-fix: n distinct arrays,
            // n XObjects). The image dicts appear plainly in the managed layout's bytes.
            int images = Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Subtype\s*/Image\b").Count;
            Assert.Equal(1, images);
        }
    }
}
