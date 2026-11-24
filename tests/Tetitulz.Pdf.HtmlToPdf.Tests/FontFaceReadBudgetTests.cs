// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A hostile <c>@font-face</c> cannot make the engine read a file once per reference. Walked with no
    /// entry budget and no cache of local reads, one block with N <c>url(bigfile)</c> entries — or N blocks
    /// each naming it — would read and allocate the file N times (≈160 GB for 100 000 entries of a 1.6 MB
    /// file, and worse for a 50 MB one). Entries count against a per-conversion budget, a file is read once
    /// per resolved path, and a file past the font decoders' 64 MiB bound is refused unread.
    /// </summary>
    public class FontFaceReadBudgetTests
    {
        [Fact]
        public void ManyFontFaceEntriesNamingOneFile_ReadItOnce_NotPerEntry()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ff_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // 500 KB that is not a font: TrueTypeFontProgram.Parse rejects it, the path that would
                // re-read the file on every one of the src entries below without the cache.
                File.WriteAllBytes(Path.Combine(dir, "notafont.ttf"), new byte[500_000]);

                string src = string.Join(",", Enumerable.Repeat("url(notafont.ttf)", 500));
                string html = "<style>@font-face{font-family:x;src:" + src + "}</style>" +
                              "<p style='font-family:x'>hi</p>";

                var o = new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed, BasePath = dir };

                long before = GC.GetAllocatedBytesForCurrentThread();
                byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(html, o);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.NotEmpty(pdf);
                // Unguarded, 500 × 500 KB ≈ 250 MB of reads; the file is read once (cache) and the entries
                // are capped. The allocation, measured per thread, is the work signal.
                Assert.True(allocated < 40_000_000,
                    $"500 @font-face entries naming one 500 KB file allocated {allocated:N0} bytes — it was read per entry");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }
    }
}
