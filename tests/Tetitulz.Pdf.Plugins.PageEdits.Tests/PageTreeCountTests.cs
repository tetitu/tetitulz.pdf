// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PageEdits;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// The <c>/Pages</c> node a newly assembled document gets must declare a <c>/Count</c> equal to the
/// number of entries in its <c>/Kids</c> (ISO 32000-1 §7.7.3.2).
/// </summary>
/// <remarks>
/// <b>Nothing asserted the entry itself.</b> Every existing split and merge test reads
/// <c>doc.Pages.Count</c>, which is the length of the walked <c>/Kids</c> — so it agrees with the
/// kids by definition and cannot see a wrong <c>/Count</c>. Setting the shared builder's
/// <c>/Count</c> to <c>kids.Count + 1</c> left the whole PageEdits suite green.
///
/// <para>That matters because a reader that trusts <c>/Count</c> — as a linearised-file reader or a
/// page-label lookup may — gets a page count the file does not have. The two builders this
/// consolidated passed the value from different places (one from the source page list, one from the
/// kids array); it now comes from the kids array by construction, and this is what holds that.</para>
/// </remarks>
public class PageTreeCountTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 3)]
    [InlineData(2, 5)]
    public void ASplitDocumentsPageCountEntryMatchesItsKids(int from, int to)
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(5));
        using var output = new MemoryStream();
        PdfSplitter.SplitRange(input, output, from, to);

        AssertCountMatchesKids(output.ToArray(), expected: to - from + 1);
    }

    [Fact]
    public void AMergedDocumentsPageCountEntryMatchesItsKids()
    {
        using var a = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(2));
        using var b = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(3));
        using var output = new MemoryStream();
        PdfMerger.Merge(output, a, b);

        AssertCountMatchesKids(output.ToArray(), expected: 5);
    }

    /// <summary>
    /// Reads <c>/Count</c> out of the catalog's <c>/Pages</c> node and compares it with the actual
    /// length of <c>/Kids</c> — not with <c>doc.Pages.Count</c>, which IS the kids length and so
    /// could never disagree with it.
    /// </summary>
    private static void AssertCountMatchesKids(byte[] pdf, int expected)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));

        var catalog = doc.ResolveObject(doc.Trailer[PdfName.Root]) as PdfDictionary;
        Assert.NotNull(catalog);
        var pages = doc.ResolveObject(catalog![PdfName.Pages]) as PdfDictionary;
        Assert.NotNull(pages);

        var kids = doc.ResolveObject(pages![PdfName.Kids]) as PdfArray;
        Assert.NotNull(kids);
        var count = doc.ResolveObject(pages[PdfName.Count]) as PdfNumber;
        Assert.NotNull(count);

        Assert.Equal(expected, kids!.Count);
        Assert.Equal(expected, (int)count!.LongValue);
    }
}
