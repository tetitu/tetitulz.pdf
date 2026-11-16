// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="BoxStart.KeepTogether"/>: a box whose minimum height does not fit the rest of the page
/// but fits the next opens at the next page's top; one that fits, or that no page could hold, opens
/// where it stands.
/// </summary>
/// <remarks>
/// A page whose text block is 200 pt tall (top and bottom margins taken from the default page), 180 pt
/// of it filled by nine 20 pt paragraphs, so 20 pt are left for the box.
/// </remarks>
public sealed class BoxKeepTogetherTests
{
    private static Section Page()
    {
        var section = new Section();
        section.MarginBottom = section.PageHeightPt - section.MarginTop - 200;
        for (int i = 1; i <= 9; i++)
            section.Children.Add(new Paragraph { LineSpacingPt = 20, SpacingAfter = 0, Runs = { new TextRun { Text = "FILL" + i + "X", FontSize = 10 } } });
        return section;
    }

    private static (PdfContentProbe.DrawnText First, PdfContentProbe.DrawnText Inside, int Pages) Box(double minHeight, bool keep)
    {
        Section section = Page();
        section.Children.Add(new BoxStart { MinHeightPt = minHeight, KeepTogether = keep });
        section.Children.Add(new Paragraph { LineSpacingPt = 10, SpacingAfter = 0, Runs = { new TextRun { Text = "INSIDEX", FontSize = 8 } } });
        section.Children.Add(new BoxEnd());
        var doc = new Document();
        doc.Sections.Add(section);
        byte[] pdf = doc.ToPdf();
        var drawn = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(pdf));
        return (drawn.Single(t => t.Text == "FILL1X"), drawn.Single(t => t.Text == "INSIDEX"), PdfContentProbe.CountPages(pdf));
    }

    /// <summary>A 60 pt box kept together, with 20 pt left: it opens at the next page's top, its block with it.</summary>
    [Fact]
    public void ABoxKeptTogether_ThatDoesNotFit_OpensOnTheNextPage()
    {
        var r = Box(60, keep: true);
        Assert.Equal(2, r.Pages);
        Assert.InRange(r.Inside.Y, r.First.Y - 2, r.First.Y + 12);   // at the top, where the first page's first line is
    }

    /// <summary>The control: the same box not kept together opens where it stands, its block in the 20 pt left.</summary>
    [Fact]
    public void ABoxNotKeptTogether_OpensWhereItStands()
    {
        var r = Box(60, keep: false);
        Assert.True(r.Inside.Y < r.First.Y - 150, $"the box's block is not at the page's foot: {r.Inside.Y} vs {r.First.Y}");
    }

    /// <summary>A box kept together that fits what is left opens where it stands.</summary>
    [Fact]
    public void ABoxKeptTogether_ThatFits_OpensWhereItStands()
    {
        var r = Box(15, keep: true);
        Assert.Equal(1, r.Pages);
        Assert.True(r.Inside.Y < r.First.Y - 150);
    }

    /// <summary>A box kept together that no page could hold is not moved: a fresh page would not hold it either.</summary>
    [Fact]
    public void ABoxKeptTogether_TallerThanAPage_OpensWhereItStands()
    {
        var r = Box(400, keep: true);
        Assert.True(r.Inside.Y < r.First.Y - 150, $"the box moved: {r.Inside.Y} vs {r.First.Y}");
    }
}
