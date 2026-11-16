// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A translucent boxed paragraph is composited as one transparency-group form (a <c>Do</c> the page
/// paints once at the paragraph's opacity). Like a CSS-transformed box, that form is one painting
/// unit and cannot fragment across a page break (Builder-2:6, the group twin of 2:7).
/// </summary>
/// <remarks>
/// The box is reserved from <c>MeasureParagraph</c>, which counts the paragraph's own lines only.
/// Footnotes grow the page-foot note area as the lines are drawn, so the real drawn height can
/// exceed that reservation. When the content was rendered with pagination ON inside the open group,
/// a line crossing the foot started a new page <i>inside</i> the group form: its <c>q</c>/<c>Do</c>
/// and close straddled the break, and — as the probe that drove this fix showed — most of the lines
/// were simply lost (18 of 45 drawn, across a corrupt extra page). The group's content is now drawn
/// with pagination off, so an over-tall box overflows its page (as CSS clips a composited layer)
/// with every line intact, rather than fragmenting.
/// </remarks>
public class OpacityGroupPaginationTests
{
    private const string Line = "BOXLINE";

    // 45 lines of text — more than a page once footnotes eat the foot — each third line carrying a
    // footnote so the note area grows at draw time beyond what the box reservation foresaw.
    private static byte[] Render(double opacity)
    {
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph { Shading = "#cccccc", Opacity = opacity };
        for (int i = 0; i < 45; i++)
        {
            p.Runs.Add(new TextRun { Text = $"{Line}{i:00} the quick brown fox jumps over the lazy dog and keeps going a while. " });
            if (i % 3 == 0)
            {
                var fn = new Footnote();
                fn.Runs.Add(new TextRun { Text = $"Footnote {i}: a reasonably long note that consumes page-foot space over many words indeed indeed indeed." });
                p.Runs.Add(fn);
            }
        }
        section.Children.Add(p);
        doc.Sections.Add(section);
        return doc.ToPdf();
    }

    private static int BoxLineDraws(byte[] pdf) =>
        PdfContentProbe.InflatedStreams(pdf)
            .Sum(s => PdfContentProbe.DrawnTexts(s).Count(d => d.Text.Contains(Line, StringComparison.Ordinal)));

    [Fact]
    public void ATranslucentBoxedParagraph_IsNotSplitAcrossPages_AndKeepsEveryLine()
    {
        byte[] pdf = Render(opacity: 0.5);
        // The group form stays whole on one page.
        Assert.Equal(1, PdfContentProbe.CountPages(pdf));
        // And every one of the 45 lines is drawn — the pre-fix break inside the group dropped 27 of
        // them (only 18 survived), so this would fail on a document that paginated inside the group.
        Assert.Equal(45, BoxLineDraws(pdf));
    }

    /// <summary>
    /// The control: the identical content at full opacity is no transparency group, so it paginates
    /// as body text does — proving the single page above is the group keeping the box whole, not
    /// content that happened to fit on one page.
    /// </summary>
    [Fact]
    public void TheSameContentOpaque_IsNotAGroup_AndPaginates()
    {
        byte[] pdf = Render(opacity: 1.0);
        Assert.True(PdfContentProbe.CountPages(pdf) >= 2,
            "the opaque paragraph should span more than one page");
        Assert.Equal(45, BoxLineDraws(pdf));   // paginating keeps every line, across the pages
    }
}
