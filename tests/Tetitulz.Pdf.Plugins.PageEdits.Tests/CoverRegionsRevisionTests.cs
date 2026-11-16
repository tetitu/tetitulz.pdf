// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// <see cref="PdfRedactor.CoverRegions"/> writes one incremental revision however many pages it
/// covers, each page carrying its own boxes.
/// </summary>
/// <remarks>
/// It wrote a revision per page, each over the whole file the one before wrote: 800 pages took
/// 40 s against 2.6 s for 200. The revision count is the evidence — the pages look the same either
/// way.
/// </remarks>
public sealed class CoverRegionsRevisionTests
{
    [Fact]
    public void AdversarialManyPages_AreCoveredInOneRevision_EachWithItsOwnBox()
    {
        const int pages = 40;
        byte[] input = MinimalPdfBuilder.CreateMultiPagePdf(pages);
        var regions = Enumerable.Range(0, pages)
            .Select(k => new RedactionRegion { PageIndex = k, X = 10 + k, Y = 20, Width = 30, Height = 40 })
            .ToList();

        using var output = new MemoryStream();
        PdfRedactor.CoverRegions(new MemoryStream(input), output, regions);
        byte[] covered = output.ToArray();

        Assert.Equal(PdfRevisions.List(new MemoryStream(input)).Count + 1, PdfRevisions.List(new MemoryStream(covered)).Count);

        using var doc = PdfDocument.Open(new MemoryStream(covered));
        Assert.Equal(pages, doc.Pages.Count);
        for (int k = 0; k < pages; k++)
        {
            string content = Encoding.ASCII.GetString(doc.Pages[k].GetContentStreamData());
            Assert.Contains($"{10 + k} 20 30 40 re", content, System.StringComparison.Ordinal);
            Assert.DoesNotContain($"{10 + (k + 1) % pages} 20 30 40 re", content, System.StringComparison.Ordinal);
        }
    }

    /// <summary>Regions listed out of page order, several on one page, still land each on its page.</summary>
    [Fact]
    public void RegionsInAnyOrder_LandOnTheirOwnPages()
    {
        byte[] input = MinimalPdfBuilder.CreateMultiPagePdf(3);
        var regions = new List<RedactionRegion>
        {
            new() { PageIndex = 2, X = 7, Y = 20, Width = 30, Height = 40 },
            new() { PageIndex = 0, X = 5, Y = 20, Width = 30, Height = 40 },
            new() { PageIndex = 2, X = 9, Y = 20, Width = 30, Height = 40 },
        };

        using var output = new MemoryStream();
        PdfRedactor.CoverRegions(new MemoryStream(input), output, regions);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        string first = Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData());
        string second = Encoding.ASCII.GetString(doc.Pages[1].GetContentStreamData());
        string third = Encoding.ASCII.GetString(doc.Pages[2].GetContentStreamData());
        Assert.Contains("5 20 30 40 re", first, System.StringComparison.Ordinal);
        Assert.DoesNotContain(" re", second, System.StringComparison.Ordinal);
        Assert.Contains("(Page 2)", second, System.StringComparison.Ordinal);
        Assert.Contains("7 20 30 40 re", third, System.StringComparison.Ordinal);
        Assert.Contains("9 20 30 40 re", third, System.StringComparison.Ordinal);
    }
}
