// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// A page's <c>/Contents</c> array cannot concatenate past the decode ceiling: its combined decoded
/// size is bounded, so a document cannot make one page allocate gigabytes (DoS finding F2).
/// </summary>
/// <remarks>
/// Each part decodes under <see cref="PdfLimits.MaxDecompressedBytes"/> on its own, but their sum
/// was unbounded — a <c>/Contents</c> array repeating one stream, or listing thousands, concatenated
/// past 2 GiB, where the <c>MemoryStream</c> threw an untyped <c>IOException</c> only after
/// allocating it all. The concatenation now refuses with a <see cref="PdfException"/> when the
/// running total would cross the same ceiling, before the part is written. The ceiling is set low
/// here so the test measures the refusal, not the 512 MiB default.
/// </remarks>
public class PdfPageContentConcatenateCapTests
{
    private static PdfDictionary PageWithParts(int count)
    {
        var page = new PdfDictionary();
        var contents = new PdfArray();
        for (int i = 0; i < count; i++) contents.Add(new PdfStream());
        page.Set(PdfName.Contents, contents);
        return page;
    }

    [Fact]
    public void ConcatenateRefusesWhenTheCombinedContentWouldExceedTheCeiling()
    {
        long saved = PdfLimits.MaxDecompressedBytes;
        try
        {
            PdfLimits.MaxDecompressedBytes = 1_000_000;   // 1 MB
            // 100 parts of 20 KB each decode to ~2 MB in total, over the 1 MB ceiling: the part that
            // would cross the ceiling is refused, rather than the lot concatenated.
            var page = PageWithParts(100);
            var ex = Assert.Throws<PdfException>(() =>
                PdfPageContent.Concatenate(page, x => x, _ => new byte[20_000]));
            Assert.Contains("decode limit", ex.Message, StringComparison.Ordinal);
        }
        finally { PdfLimits.MaxDecompressedBytes = saved; }
    }

    [Fact]
    public void ConcatenateJoinsAPageUnderTheCeiling()
    {
        // The control: content that fits the ceiling is concatenated whole, with the one-byte
        // separator after each part — so the refusal above is the ceiling firing, not concatenation
        // being broken for every array.
        long saved = PdfLimits.MaxDecompressedBytes;
        try
        {
            PdfLimits.MaxDecompressedBytes = 1_000_000;
            var page = PageWithParts(10);   // 10 x 20 KB = 200 KB, well under 1 MB
            byte[] result = PdfPageContent.Concatenate(page, x => x, _ => new byte[20_000]);
            Assert.NotNull(result);
            Assert.Equal(10 * 20_000 + 10, result.Length);   // parts + one separator each
        }
        finally { PdfLimits.MaxDecompressedBytes = saved; }
    }
}
