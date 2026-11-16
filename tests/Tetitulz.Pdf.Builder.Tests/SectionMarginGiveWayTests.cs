// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A section whose margins swallow its page does not explode into one line (or one word) a page:
/// the margins give way so the content box is the whole page (<see cref="PageContentBox"/>).
/// </summary>
/// <remarks>
/// With <c>MarginTop + MarginBottom &gt;= PageHeightPt</c> (or the same on the width) the content box
/// is zero or less, and the exporter used to draw one line per page — a 1 KB document became
/// hundreds of thousands of pages. The margins now give way to zero on the swallowed axis, so a
/// degenerate section lays out in about as many pages as a normal one, with every word kept, and the
/// give-way is reported rather than silent. This is the Builder twin of DocxToPdf's
/// <c>SectionPageSize</c> fix (the F11 DoS sibling), reached through the Builder API and HTML
/// <c>@page</c> alike.
/// </remarks>
public class SectionMarginGiveWayTests
{
    private const int Words = 300;

    private static Document Doc(double marginTop, double marginBottom, double marginLeft, double marginRight)
    {
        var doc = new Document();
        var sec = new Section
        {
            PageWidthPt = 612, PageHeightPt = 792,
            MarginTop = marginTop, MarginBottom = marginBottom, MarginLeft = marginLeft, MarginRight = marginRight,
        };
        var p = new Paragraph();
        for (int i = 0; i < Words; i++) p.Runs.Add(new TextRun { Text = $"word{i} " });
        sec.Children.Add(p);
        doc.Sections.Add(sec);
        return doc;
    }

    private static (byte[] Pdf, List<BuilderDiagnostic> Diagnostics) Export(Document doc)
    {
        var diags = new List<BuilderDiagnostic>();
        var options = new PdfExportOptions { OnDiagnostic = diags.Add };
        return (doc.ToPdf(options), diags);
    }

    [Fact]
    public void MarginsThatSwallowThePage_GiveWay_SoTheDocumentDoesNotExplodeIntoOneWordAPage()
    {
        // 300 words with top+bottom and left+right each larger than the page: without the give-way
        // the content box is negative on both axes and the exporter draws one word a page (~300
        // pages). With it, the margins drop to zero and the page holds the lot.
        var (pdf, diags) = Export(Doc(marginTop: 400, marginBottom: 400, marginLeft: 320, marginRight: 320));

        int pages = PdfContentProbe.CountPages(pdf);
        Assert.True(pages <= 3, $"a page-swallowing margin exploded the page count: {pages} pages for {Words} words");

        // Every word is still drawn — the give-way fills the page, it does not drop content.
        string content = PdfContentProbe.DecodeStreams(pdf);
        var drawn = PdfContentProbe.DrawnTexts(content);
        Assert.Contains(drawn, d => d.Text.Contains("word0", System.StringComparison.Ordinal));
        Assert.Contains(drawn, d => d.Text.Contains($"word{Words - 1}", System.StringComparison.Ordinal));

        // And the give-way is reported, not silent — once for each swallowed axis.
        Assert.Equal(2, diags.Count(d => d.Kind == BuilderDiagnosticKind.ContentApproximated
                                          && d.Detail.Contains("give way", System.StringComparison.Ordinal)));
    }

    [Fact]
    public void NormalMargins_AreUnchanged_AndReportNoGiveWay()
    {
        // The control: a page with a real content box lays out the same 300 words in the same few
        // pages and reports no give-way — so the single page above is the give-way working, not
        // content that happened to fit, and the rule never fires on an ordinary document.
        var (pdf, diags) = Export(Doc(marginTop: 72, marginBottom: 72, marginLeft: 72, marginRight: 72));

        Assert.True(PdfContentProbe.CountPages(pdf) <= 3);
        Assert.DoesNotContain(diags, d => d.Detail.Contains("give way", System.StringComparison.Ordinal));
    }
}
