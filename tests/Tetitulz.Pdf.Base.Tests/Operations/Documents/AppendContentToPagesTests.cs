// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// <see cref="PdfPageContentAppender.AppendContentToPages"/>: each page its own content, all of it
/// in one incremental revision; every index checked before anything is written; an empty map copies
/// the input.
/// </summary>
public class AppendContentToPagesTests
{
    [Fact]
    public void EachPageGetsItsOwnContent_InOneRevision()
    {
        byte[] input = MinimalPdfBuilder.CreateMultiPagePdf(4);
        var builders = new Dictionary<int, Action<PdfContentStreamBuilder>>
        {
            [3] = b => b.Rectangle(3, 1, 2, 2),
            [0] = b => b.Rectangle(9, 1, 2, 2),
        };

        using var output = new MemoryStream();
        PdfPageContentAppender.AppendContentToPages(new MemoryStream(input), output, builders);
        byte[] stamped = output.ToArray();

        Assert.Equal(PdfRevisions.List(new MemoryStream(input)).Count + 1, PdfRevisions.List(new MemoryStream(stamped)).Count);
        using var doc = PdfDocument.Open(new MemoryStream(stamped));
        Assert.Contains("9 1 2 2 re", Content(doc, 0), StringComparison.Ordinal);
        Assert.DoesNotContain(" re", Content(doc, 1), StringComparison.Ordinal);
        Assert.DoesNotContain(" re", Content(doc, 2), StringComparison.Ordinal);
        Assert.Contains("3 1 2 2 re", Content(doc, 3), StringComparison.Ordinal);
        Assert.DoesNotContain("9 1 2 2 re", Content(doc, 3), StringComparison.Ordinal);
    }

    /// <summary>An index past the last page is refused with the document's range, and nothing is written — the earlier pages included.</summary>
    [Fact]
    public void AnIndexPastTheLastPage_IsRefusedNamingTheRange_AndNothingIsWritten()
    {
        byte[] input = MinimalPdfBuilder.CreateMultiPagePdf(2);
        var builders = new Dictionary<int, Action<PdfContentStreamBuilder>>
        {
            [0] = b => b.Rectangle(1, 1, 2, 2),
            [2] = b => b.Rectangle(1, 1, 2, 2),
        };

        using var output = new MemoryStream();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfPageContentAppender.AppendContentToPages(new MemoryStream(input), output, builders));
        Assert.Contains("between 0 and 1", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void ANullBuilder_IsRefused()
    {
        var builders = new Dictionary<int, Action<PdfContentStreamBuilder>> { [0] = null };

        Assert.Throws<ArgumentException>(() => PdfPageContentAppender.AppendContentToPages(
            new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(1)), new MemoryStream(), builders));
    }

    [Fact]
    public void AnEmptyMap_CopiesTheInputUnchanged()
    {
        byte[] input = MinimalPdfBuilder.CreateMultiPagePdf(2);

        using var output = new MemoryStream();
        PdfPageContentAppender.AppendContentToPages(new MemoryStream(input), output,
            new Dictionary<int, Action<PdfContentStreamBuilder>>());

        Assert.Equal(input, output.ToArray());
    }

    private static string Content(PdfDocument doc, int pageIndex)
        => Encoding.ASCII.GetString(doc.Pages[pageIndex].GetContentStreamData());
}
