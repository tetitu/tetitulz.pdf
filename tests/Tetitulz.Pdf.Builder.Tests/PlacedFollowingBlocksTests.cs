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
/// <see cref="AbsolutePlacement.FollowingBlocks"/>: a table drawn under a placed paragraph, at its
/// left edge and width, out of the flow — measured with the group where the placement needs the
/// group's height (a bottom anchor) or width (a right anchor with no width).
/// </summary>
public sealed class PlacedFollowingBlocksTests
{
    private static Table OneRow(params string[] cells)
    {
        var row = new TableRow();
        foreach (string text in cells)
        {
            var cell = new TableCell();
            cell.Paragraphs.Add(new Paragraph { SpacingAfter = 0, Runs = { new TextRun { Text = text, FontSize = 10 } } });
            row.Cells.Add(cell);
        }
        var table = new Table { BorderWidth = 0, SpacingAfter = 0 };
        table.Rows.Add(row);
        return table;
    }

    private static Paragraph Head(AbsolutePlacement place)
        => new Paragraph { Placement = place, SpacingAfter = 0, Runs = { new TextRun { Text = "HEADX", FontSize = 10 } } };

    private static (PdfContentProbe.DrawnText Head, PdfContentProbe.DrawnText A, PdfContentProbe.DrawnText B, double PageHeight, double PageWidth)
        Draw(AbsolutePlacement place, Table table)
    {
        var section = new Section();
        Paragraph head = Head(place);
        place.FollowingBlocks.Add(table);
        section.Children.Add(head);
        var doc = new Document();
        doc.Sections.Add(section);
        var drawn = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(doc.ToPdf()));
        return (drawn.Single(t => t.Text == "HEADX"), drawn.Single(t => t.Text == "CELLAX"), drawn.Single(t => t.Text == "CELLBX"),
                section.PageHeightPt, section.PageWidthPt);
    }

    /// <summary>The table under the paragraph, its columns across the placement's width from its left edge.</summary>
    [Fact]
    public void ATableFollowsThePlacedParagraph_AtItsLeftEdgeAndWidth()
    {
        var r = Draw(new AbsolutePlacement { LeftPt = 100, TopPt = 100, WidthPt = 200 }, OneRow("CELLAX", "CELLBX"));
        Assert.InRange(r.A.X, 100, 106);                    // the cell's own inset
        Assert.Equal(100, r.B.X - r.A.X, 1);                // two equal columns of 200
        Assert.True(r.A.Y < r.Head.Y - 5, $"the table is not under the paragraph: {r.A.Y} vs {r.Head.Y}");
        Assert.InRange(r.Head.Y, r.PageHeight - 100 - 14, r.PageHeight - 100);
    }

    /// <summary>A paragraph among the following blocks is drawn where the table above it ended, at the placement's left edge.</summary>
    [Fact]
    public void AParagraphAfterTheTable_IsDrawnBelowIt()
    {
        var section = new Section();
        var place = new AbsolutePlacement { LeftPt = 100, TopPt = 100, WidthPt = 200 };
        var table = OneRow("CELLAX", "CELLBX");
        table.Rows[0].HeightPt = 40;
        place.FollowingBlocks.Add(table);
        place.FollowingBlocks.Add(new Paragraph { SpacingAfter = 0, Runs = { new TextRun { Text = "TAILX", FontSize = 10 } } });
        section.Children.Add(Head(place));
        var doc = new Document();
        doc.Sections.Add(section);
        var drawn = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(doc.ToPdf()));

        var cell = drawn.Single(t => t.Text == "CELLAX");
        var tail = drawn.Single(t => t.Text == "TAILX");
        Assert.Equal(100, tail.X, 1);
        Assert.True(tail.Y < cell.Y - 30, $"the paragraph is not under the 40 pt row: {tail.Y} vs {cell.Y}");
    }

    private static (PdfContentProbe.DrawnText Head, System.Collections.Generic.List<PdfContentProbe.DrawnText> Drawn, Section Section, string Content)
        DrawGroup(AbsolutePlacement place, params IDocElement[] following)
    {
        var section = new Section();
        foreach (IDocElement block in following) place.FollowingBlocks.Add(block);
        section.Children.Add(Head(place));
        var doc = new Document();
        doc.Sections.Add(section);
        string content = PdfContentProbe.DecodeStreams(doc.ToPdf());
        var drawn = PdfContentProbe.DrawnTexts(content);
        return (drawn.Single(t => t.Text == "HEADX"), drawn, section, content);
    }

    private static Paragraph Text(string text) => new Paragraph { SpacingAfter = 0, Runs = { new TextRun { Text = text, FontSize = 10 } } };

    /// <summary>A list follows the placed paragraph as a cell's list is drawn: each item's text under the one above, inside the placement, its numbers drawn too.</summary>
    [Fact]
    public void AListFollowsThePlacedParagraph()
    {
        var list = new ListBlock { IsOrdered = true };
        list.Items.Add(new ListItem { Text = "ITEMONEX" });
        list.Items.Add(new ListItem { Text = "ITEMTWOX" });
        var r = DrawGroup(new AbsolutePlacement { LeftPt = 100, TopPt = 100, WidthPt = 200 }, list);

        var one = r.Drawn.Single(t => t.Text.Contains("ITEMONEX", StringComparison.Ordinal));
        var two = r.Drawn.Single(t => t.Text.Contains("ITEMTWOX", StringComparison.Ordinal));
        Assert.True(r.Head.Y > one.Y && one.Y > two.Y, $"the list is not stacked under the paragraph: {r.Head.Y}, {one.Y}, {two.Y}");
        Assert.Equal(one.X, two.X, 1);
        Assert.InRange(one.X, 100, 300);
        Assert.Contains(r.Drawn, t => t.Text.StartsWith("1.", StringComparison.Ordinal));   // the markers are drawn too
    }

    /// <summary>
    /// A box follows it with its border and padding: its paragraph in from the placement's edge by
    /// the box's insets, a vertical stroke at the box's left edge.
    /// </summary>
    [Fact]
    public void ABoxFollowsThePlacedParagraph_WithItsInsets()
    {
        var edge = new Border(1, "#000000");
        var r = DrawGroup(new AbsolutePlacement { LeftPt = 100, TopPt = 100, WidthPt = 200 },
            new BoxStart { PaddingLeft = 10, BorderLeft = edge, BorderRight = edge, BorderTop = edge, BorderBottom = edge },
            Text("INBOXX"),
            new BoxEnd(),
            Text("AFTERBOXX"));

        var inside = r.Drawn.Single(t => t.Text == "INBOXX");
        var after = r.Drawn.Single(t => t.Text == "AFTERBOXX");
        Assert.Equal(100 + 11, inside.X, 1);                 // the 1 pt border and 10 pt padding
        Assert.Equal(100, after.X, 1);                       // back at the placement's edge after the box
        Assert.True(r.Head.Y > inside.Y && inside.Y > after.Y);
        var verticals = PdfContentProbe.StrokedSegments(r.Content).Where(s => !s.IsHorizontal).ToList();
        Assert.Contains(verticals, s => Math.Abs(s.X0 - 100.5) < 0.6);
    }

    /// <summary>
    /// A paragraph placed against a box among the following blocks is drawn with that box, measured from its
    /// padding box (<see cref="AbsolutePlacement.CanFollow"/>): 40pt in from the box's left and 20pt
    /// below its top, and the block after it still follows in the flow of the group. One placed against a box
    /// that is not among them, or on every page, is not drawn there, and is reported.
    /// </summary>
    [Fact]
    public void AParagraphPlacedAgainstABoxAmongThem_IsDrawnWithIt()
    {
        var box = new BoxStart { PaddingLeft = 10, PaddingTop = 5 };
        var nested = new Paragraph { SpacingAfter = 0, Runs = { new TextRun { Text = "NESTEDX", FontSize = 10 } },
                                     Placement = new AbsolutePlacement { ContainingBox = box, LeftPt = 40, TopPt = 20 } };
        var r = DrawGroup(new AbsolutePlacement { LeftPt = 100, TopPt = 100, WidthPt = 200 },
            box, Text("INBOXX"), nested, new BoxEnd(), Text("AFTERBOXX"));

        var drawn = r.Drawn.Single(t => t.Text == "NESTEDX");
        var inside = r.Drawn.Single(t => t.Text == "INBOXX");
        Assert.Equal(100 + 40, drawn.X, 1);                     // the box's padding box's left, not its content's
        Assert.Equal(inside.Y + 5 - 20, drawn.Y, 1);            // 20 pt below the padding box's top, 5 pt above its content
        Assert.Equal(100, r.Drawn.Single(t => t.Text == "AFTERBOXX").X, 1);

        Assert.True(AbsolutePlacement.CanFollow(nested, new IDocElement[] { box, nested }));
        Assert.False(AbsolutePlacement.CanFollow(nested, new IDocElement[] { nested }));          // the box is not among them
        var everyPage = new Paragraph { Placement = new AbsolutePlacement { ContainingBox = box, EveryPage = true } };
        Assert.False(AbsolutePlacement.CanFollow(everyPage, new IDocElement[] { box, everyPage }));
        Assert.True(AbsolutePlacement.CanFollow(Text("PLAINX"), Array.Empty<IDocElement>()));
    }

    /// <summary>
    /// An exporter that draws no placement writes a nested placement's own following paragraph in the flow too
    /// (<c>ModelRules.InFlow</c> goes into it): the HTML has the outer's blocks, the nested paragraph and what follows it.
    /// </summary>
    [Fact]
    public void ANestedPlacementsFollowingParagraphs_AreWrittenInTheFlow()
    {
        var box = new BoxStart();
        var nested = new Paragraph { Runs = { new TextRun { Text = "NESTEDX" } }, Placement = new AbsolutePlacement { ContainingBox = box, LeftPt = 40, TopPt = 20 } };
        nested.Placement.Following.Add(Text("NESTEDTAILX"));
        var place = new AbsolutePlacement { LeftPt = 100, TopPt = 100, WidthPt = 200 };
        foreach (IDocElement block in new IDocElement[] { box, Text("INBOXX"), nested, new BoxEnd() }) place.FollowingBlocks.Add(block);
        var section = new Section();
        section.Children.Add(Head(place));
        var doc = new Document();
        doc.Sections.Add(section);

        using var ms = new System.IO.MemoryStream();
        new Tetitulz.Pdf.Builder.Export.HtmlExporter().Export(doc, ms);
        string html = System.Text.Encoding.UTF8.GetString(ms.ToArray());

        int head = html.IndexOf("HEADX", StringComparison.Ordinal), inBox = html.IndexOf("INBOXX", StringComparison.Ordinal);
        int nestedAt = html.IndexOf("NESTEDX", StringComparison.Ordinal), tail = html.IndexOf("NESTEDTAILX", StringComparison.Ordinal);
        Assert.True(head >= 0 && head < inBox && inBox < nestedAt && nestedAt < tail, $"order: {head}, {inBox}, {nestedAt}, {tail}");
    }

    /// <summary>A float among the following blocks floats at the placement's edge, the paragraph after it beside it.</summary>
    [Fact]
    public void AFloatAmongThem_HasTheParagraphAfterItBesideIt()
    {
        var floated = new Paragraph { Float = FloatSide.Left, FloatWidth = 60, SpacingAfter = 0, Runs = { new TextRun { Text = "FLOATX", FontSize = 10 } } };
        var r = DrawGroup(new AbsolutePlacement { LeftPt = 100, TopPt = 100, WidthPt = 300 }, floated, Text("BESIDEX"));

        var f = r.Drawn.Single(t => t.Text == "FLOATX");
        var beside = r.Drawn.Single(t => t.Text == "BESIDEX");
        Assert.InRange(f.X, 100, 104);
        Assert.True(beside.X >= 160, $"the paragraph is not beside the 60 pt float: {beside.X}");
        Assert.Equal(f.Y, beside.Y, 1);
    }

    /// <summary>
    /// The page's own floats are not the group's: a float the flow left on the page, where the placed
    /// group is drawn, does not push the group's following paragraph aside.
    /// </summary>
    [Fact]
    public void ThePagesFloats_DoNotPushTheGroupAside()
    {
        var section = new Section();
        // A float twenty lines tall down the left of the page's top.
        var tall = new Paragraph { Float = FloatSide.Left, FloatWidth = 200, SpacingAfter = 0 };
        for (int i = 0; i < 20; i++)
        {
            if (i > 0) tall.Runs.Add(new LineBreak());
            tall.Runs.Add(new TextRun { Text = "PAGEFLOAT" + i + "X", FontSize = 10 });
        }
        section.Children.Add(tall);
        var place = new AbsolutePlacement { LeftPt = section.MarginLeft, TopPt = section.MarginTop, WidthPt = 400 };
        place.Following.Add(Text("FOLLOWINGX"));
        place.FollowingBlocks.Add(new ListBlock { Items = { new ListItem { Text = "LISTEDX" } } });
        place.FollowingBlocks.Add(Text("GROUPTEXTX"));
        section.Children.Add(Head(place));
        var doc = new Document();
        doc.Sections.Add(section);
        var drawn = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(doc.ToPdf()));

        Assert.Equal(section.MarginLeft, drawn.Single(t => t.Text == "PAGEFLOAT0X").X, 1);
        double floatBottom = drawn.Single(t => t.Text == "PAGEFLOAT19X").Y;
        var group = drawn.Single(t => t.Text == "GROUPTEXTX");
        Assert.True(group.Y > floatBottom, "the group's text is not beside the float, so the test proves nothing");
        foreach (string text in new[] { "HEADX", "FOLLOWINGX", "GROUPTEXTX" })
            Assert.Equal(section.MarginLeft, drawn.Single(t => t.Text == text).X, 1);
    }

    /// <summary>A bottom-anchored group counts a following list's height: its last item stays above the anchor line.</summary>
    [Fact]
    public void ABottomAnchoredGroup_CountsAListsHeight()
    {
        var list = new ListBlock();
        for (int i = 1; i <= 5; i++) list.Items.Add(new ListItem { Text = "ROW" + i + "X" });
        var r = DrawGroup(new AbsolutePlacement { LeftPt = 100, WidthPt = 200, AnchorsTop = false, BottomPt = 50 }, list);

        var last = r.Drawn.Single(t => t.Text.Contains("ROW5X", StringComparison.Ordinal));
        Assert.InRange(last.Y, 50, 50 + 16);
        Assert.True(r.Head.Y > 50 + 5 * 10, "the paragraph is not above the five items");
    }

    /// <summary>A right-anchored group with a list among its blocks takes the room it has, as a list fills its band.</summary>
    [Fact]
    public void ARightAnchoredGroupWithAList_TakesTheRoomItHas()
    {
        var list = new ListBlock();
        list.Items.Add(new ListItem { Text = "ONEX" });
        var r = DrawGroup(new AbsolutePlacement { AnchorsLeft = false, RightPt = 50, TopPt = 100 }, list);
        Assert.Equal(0, r.Head.X, 1);   // the page's width less 50 pt, all of it the group's
    }

    /// <summary>
    /// Out of the flow: the paragraph after the placed group starts at the top of the page, as it
    /// would with no placed group at all.
    /// </summary>
    [Fact]
    public void ThePlacedTableTakesNoRoomInTheFlow()
    {
        var section = new Section();
        var place = new AbsolutePlacement { LeftPt = 300, TopPt = 72, WidthPt = 200 };
        place.FollowingBlocks.Add(OneRow("CELLAX", "CELLBX"));
        section.Children.Add(Head(place));
        section.Children.Add(new Paragraph { Runs = { new TextRun { Text = "FLOWX", FontSize = 10 } } });
        var doc = new Document();
        doc.Sections.Add(section);
        var drawn = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(doc.ToPdf()));
        var flow = drawn.Single(t => t.Text == "FLOWX");
        Assert.InRange(flow.Y, section.PageHeightPt - section.MarginTop - 14, section.PageHeightPt - section.MarginTop);
        Assert.Equal(section.MarginLeft, flow.X, 1);
    }

    /// <summary>
    /// Anchored at its bottom alone, the group ends <c>BottomPt</c> above the page's bottom edge — the
    /// table's height counted, so its row is above that line, not drawn through it.
    /// </summary>
    [Fact]
    public void ABottomAnchoredGroup_CountsTheTablesHeight()
    {
        var table = OneRow("CELLAX", "CELLBX");
        table.Rows[0].HeightPt = 60;
        var r = Draw(new AbsolutePlacement { LeftPt = 100, WidthPt = 200, AnchorsTop = false, BottomPt = 50 }, table);
        // The row's 60 pt sit between 50 and 110; its text near the row's top, the paragraph above.
        Assert.InRange(r.A.Y, 50 + 40, 50 + 60);
        Assert.InRange(r.Head.Y, 50 + 60, 50 + 60 + 14);
    }

    /// <summary>
    /// Anchored at its right alone with no width, the group is as wide as its widest block — here the
    /// table's stated 200 pt, not the short paragraph's — so its left edge is 250 pt in from the right.
    /// </summary>
    [Fact]
    public void ARightAnchoredGroup_IsAsWideAsItsTable()
    {
        var table = OneRow("CELLAX", "CELLBX");
        table.WidthPt = 200;
        var r = Draw(new AbsolutePlacement { AnchorsLeft = false, RightPt = 50, TopPt = 100 }, table);
        Assert.InRange(r.A.X, r.PageWidth - 250, r.PageWidth - 244);
        Assert.Equal(r.PageWidth - 250, r.Head.X, 1);
    }

    /// <summary>
    /// A table among the following blocks is validated with the rest of the model: a NaN row height
    /// is refused, the refusal naming its path through the placement.
    /// </summary>
    [Fact]
    public void AFollowingTablesMeasurements_AreValidated()
    {
        var section = new Section();
        var place = new AbsolutePlacement { LeftPt = 100, TopPt = 100, WidthPt = 200 };
        var table = OneRow("CELLAX", "CELLBX");
        table.Rows[0].HeightPt = double.NaN;
        place.FollowingBlocks.Add(table);
        section.Children.Add(Head(place));
        var doc = new Document();
        doc.Sections.Add(section);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => doc.ToPdf());
        Assert.Contains("Sections[0].Children[0].Placement.FollowingBlocks[0].Rows[0].HeightPt", ex.Message, StringComparison.Ordinal);

        table.Rows[0].HeightPt = 20;   // the control: the same table with a real height is drawn
        Assert.Contains(PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(doc.ToPdf())), t => t.Text == "CELLAX");
    }

    /// <summary>
    /// A placement holding two thousand boxes, each with a paragraph placed against it: each block's
    /// box was found by scanning the whole list, quadratic in its length (an XFA turned flow of forty
    /// thousand positioned instances was 3.2e9 comparisons a pass). The boxes are collected once now;
    /// every placed paragraph is still written.
    /// </summary>
    [Fact]
    public void AdversarialPlacementOfManyBoxes_FindsEachBoxWithoutAScan()
    {
        const int boxes = 2_000;
        var place = new AbsolutePlacement { LeftPt = 100, TopPt = 100, WidthPt = 200 };
        for (int i = 0; i < boxes; i++)
        {
            var box = new BoxStart();
            place.FollowingBlocks.Add(box);
            place.FollowingBlocks.Add(new Paragraph { Runs = { new TextRun { Text = "NESTEDX" } },
                                                      Placement = new AbsolutePlacement { ContainingBox = box, LeftPt = 4, TopPt = 2 } });
            place.FollowingBlocks.Add(new BoxEnd());
        }
        var section = new Section();
        section.Children.Add(Head(place));
        var doc = new Document();
        doc.Sections.Add(section);

        AbsolutePlacement.AmongScanned = 0;
        string html = System.Text.Encoding.UTF8.GetString(doc.ToHtml());

        Assert.Equal(boxes, CountOf(html, "NESTEDX"));
        Assert.Equal(0, AbsolutePlacement.AmongScanned);   // was 3 × 2 000² / 2 = six million

        // The PDF export reads the placed group three times (render, measure, shrink-to-fit); each
        // now asks the blocks against the list's boxes collected once, so it scans no more than the
        // HTML path. Before the delegation this was millions of comparisons.
        AbsolutePlacement.AmongScanned = 0;
        var drawn = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(doc.ToPdf()));
        Assert.Equal(boxes, drawn.Count(d => d.Text.Contains("NESTEDX", StringComparison.Ordinal)));
        Assert.Equal(0, AbsolutePlacement.AmongScanned);
    }

    private static int CountOf(string text, string word)
    {
        int n = 0;
        for (int at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + word.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}