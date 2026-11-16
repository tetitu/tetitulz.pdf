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
/// A CSS-transformed paragraph is one painting unit: it is not split across a page break (Builder-2:7).
/// </summary>
/// <remarks>
/// The transform is written as a <c>q</c>/<c>cm</c> pair wrapping the render. When the render
/// paginated inside it, the <c>q</c> stayed open on the first page — its footer and footnotes then
/// drew under the transform — and the second page ran a <c>Q</c> that popped a state it never pushed.
/// A transformed box that does not fit the rest of a page now moves to the next page whole, and one
/// taller than any page overflows (as CSS clips a transformed box) rather than fragmenting. The
/// control — the same paragraph with no transform — still paginates, so the single page is the
/// transform's doing, not content that happens to fit.
/// </remarks>
public class TransformPaginationTests
{
    private const string Word = "TRANSFORMEDX";

    private static int PagesSpannedByTheText(bool transformed)
    {
        var doc = new Document();
        var section = new Section();
        for (int i = 0; i < 5; i++) section.Children.Add(new Paragraph { Runs = { new TextRun { Text = "filler" } } });
        var p = new Paragraph();
        if (transformed) p.Transform = new double[] { 1, 0, 0, 1, 0, 0 };
        p.Runs.Add(new TextRun { Text = string.Join(" ", Enumerable.Repeat(Word, 400)) });   // taller than a page
        section.Children.Add(p);
        doc.Sections.Add(section);

        var drawn = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(doc.ToPdf()));
        var pages = new System.Collections.Generic.HashSet<int>();
        int page = 1;
        for (int i = 0; i < drawn.Count; i++)
        {
            if (i > 0 && drawn[i].Y > drawn[i - 1].Y + 0.5) page++;   // baseline jumped up: a new page
            if (drawn[i].Text.Contains(Word, StringComparison.Ordinal)) pages.Add(page);
        }
        return pages.Count;
    }

    [Fact]
    public void ATransformedParagraphTallerThanAPage_IsNotSplitAcrossPages()
    {
        Assert.Equal(1, PagesSpannedByTheText(transformed: true));
    }

    /// <summary>The control: identical content without a transform paginates, so the single page
    /// above is the transform keeping the box whole, not content that fits on one page.</summary>
    [Fact]
    public void TheSameContentWithoutATransform_Paginates()
    {
        Assert.True(PagesSpannedByTheText(transformed: false) >= 2,
            "the un-transformed paragraph should span more than one page");
    }
}
