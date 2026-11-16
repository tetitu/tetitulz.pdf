// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="BoxStart"/> / <see cref="BoxEnd"/>: a border, background and padding around a run of
/// blocks, laid out in the box's band and sliced at a page break.
/// </summary>
/// <remarks>
/// Asserted on geometry read back from the content stream — the stroked edges, where the text
/// starts — and on the order of operators, which is what "behind" means in a content stream.
/// Pure primaries make the colour operators unambiguous.
/// </remarks>
public class BlockBoxTests
{
    private static readonly Border Red = new(1, "#FF0000");

    private static BoxStart RedBox(double padding = 10) => new()
    {
        BorderTop = Red, BorderRight = Red, BorderBottom = Red, BorderLeft = Red,
        PaddingTop = padding, PaddingRight = padding, PaddingBottom = padding, PaddingLeft = padding,
    };

    private static Paragraph Para(string text, double spacing = 0)
    {
        var p = new Paragraph { SpacingBefore = spacing, SpacingAfter = spacing };
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }

    private static string Content(params IDocElement[] blocks)
    {
        var section = new Section();
        section.Children.AddRange(blocks);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf());
    }

    private static double PageLeft() => PdfContentProbe.XOf(Content(Para("Margin")), "Margin");

    /// <summary>One line's height, read from two unspaced paragraphs rather than assumed.</summary>
    private static double LineHeight()
    {
        string content = Content(Para("First"), Para("Second"));
        return PdfContentProbe.YOf(content, "First") - PdfContentProbe.YOf(content, "Second");
    }

    [Fact]
    public void ABoxDrawsItsBorderAroundItsBlocks_AndLaysThemOutInsideItsPadding()
    {
        string content = Content(RedBox(), Para("Inside"), Para("Also"), new BoxEnd(), Para("After"));
        var segments = PdfContentProbe.StrokedSegments(content);
        var page = new Section();
        double left = PageLeft(), right = page.PageWidthPt - page.MarginRight;
        double topOfText = page.PageHeightPt - page.MarginTop;

        Assert.Equal(4, segments.Count);
        var vertical = segments.Where(s => s.IsVertical).Select(s => s.X0).OrderBy(x => x).ToList();
        var horizontal = segments.Where(s => s.IsHorizontal).Select(s => s.Y0).OrderBy(y => y).ToList();

        // Every side half its 1pt band in from the box's edge: the page's margins at the sides,
        // the top of the text block above, and the top of the next block's line below.
        Assert.Equal(left + 0.5, vertical[0], 2);
        Assert.Equal(right - 0.5, vertical[1], 2);
        Assert.Equal(topOfText - 0.5, horizontal[1], 2);
        Assert.Equal(PdfContentProbe.YOf(content, "After") + LineHeight() + 0.5, horizontal[0], 2);
        // The text a border and a padding in: 1 + 10.
        Assert.Equal(left + 11, PdfContentProbe.XOf(content, "Inside"), 2);
        Assert.Equal(topOfText - 11 - LineHeight(), PdfContentProbe.YOf(content, "Inside"), 2);
    }

    /// <summary>
    /// A box's shadow is its own path, filled in the shadow's colour before the box's fill — behind
    /// it — and offset by the shadow's x to the right and y downward (PDF counts y up).
    /// </summary>
    [Fact]
    public void ABoxDrawsItsShadowBehindItsFill_AtItsOffset()
    {
        var box = new BoxStart { Shading = "#00FF00", Shadow = new BoxShadow { OffsetXPt = 2, OffsetYPt = 3, Color = "#0000FF" } };
        string[] lines = Content(box, Para("Inside"), new BoxEnd()).Split('\n').Select(l => l.Trim()).ToArray();

        int blue = System.Array.FindIndex(lines, l => Regex.IsMatch(l, @"^0(\.0+)? 0(\.0+)? 1(\.0+)? rg$"));
        int green = System.Array.FindIndex(lines, l => Regex.IsMatch(l, @"^0(\.0+)? 1(\.0+)? 0(\.0+)? rg$"));
        Assert.True(blue >= 0 && green > blue, $"the shadow ({blue}) must be filled before the box ({green})");
        double[] shadow = Rect(lines, blue), fill = Rect(lines, green);
        Assert.Equal(fill[0] + 2, shadow[0], 3);
        Assert.Equal(fill[1] - 3, shadow[1], 3);
        Assert.Equal(fill[2], shadow[2], 3);
        Assert.Equal(fill[3], shadow[3], 3);
    }

    /// <summary>A box with no fill, image or border and only a shadow still draws the shadow.</summary>
    [Fact]
    public void ABoxWithOnlyAShadowDrawsIt()
    {
        var box = new BoxStart { Shadow = new BoxShadow { OffsetXPt = 2, OffsetYPt = 3, Color = "#0000FF" } };
        string[] lines = Content(box, Para("Inside"), new BoxEnd()).Split('\n').Select(l => l.Trim()).ToArray();

        int blue = System.Array.FindIndex(lines, l => Regex.IsMatch(l, @"^0(\.0+)? 0(\.0+)? 1(\.0+)? rg$"));
        Assert.True(blue >= 0, "no fill in the shadow's colour");
        Assert.Equal("f", lines.Skip(blue).First(l => l is "f" or "f*"));
    }

    /// <summary>
    /// A paragraph whose only decoration is a shadow draws it: the shadow is painted in the
    /// paragraph's box branch, which did not count a shadow as a box, so such a paragraph drew nothing.
    /// </summary>
    [Fact]
    public void AParagraphWithOnlyAShadowDrawsIt()
    {
        var p = Para("Shaded");
        p.Shadow = new BoxShadow { OffsetXPt = 2, OffsetYPt = 3, Color = "#0000FF" };
        string[] lines = Content(p).Split('\n').Select(l => l.Trim()).ToArray();

        int blue = System.Array.FindIndex(lines, l => Regex.IsMatch(l, @"^0(\.0+)? 0(\.0+)? 1(\.0+)? rg$"));
        Assert.True(blue >= 0, "no fill in the shadow's colour");
        Assert.Equal("f", lines.Skip(blue).First(l => l is "f" or "f*"));
        Assert.DoesNotContain(Content(Para("Plain")).Split('\n'), l => Regex.IsMatch(l.Trim(), @"^0(\.0+)? 0(\.0+)? 1(\.0+)? rg$"));
    }

    /// <summary>
    /// A box's transform (batch 84) is set — <c>q</c> and its matrix — before everything the box
    /// paints and holds: its fill, moved back to the box's mark, its border and its blocks; and closed
    /// with <c>Q</c> after them, so the block before and the block after are outside it. A box
    /// without one writes no <c>cm</c>.
    /// </summary>
    [Fact]
    public void ABoxsTransformWrapsItsFillBorderAndBlocks()
    {
        var box = RedBox();
        box.Shading = "#0000FF";
        box.Transform = new double[] { 1, 0, 0, 1, 20, -30 };
        string[] lines = Content(Para("Before"), box, Para("Inside"), new BoxEnd(), Para("After")).Split('\n').Select(l => l.Trim()).ToArray();

        int cm = Assert.Single(lines.Select((l, i) => (l, i)).Where(p => p.l.EndsWith(" cm", System.StringComparison.Ordinal)).Select(p => p.i));
        Assert.Matches(@"^1(\.0+)? 0(\.0+)? 0(\.0+)? 1(\.0+)? 20(\.0+)? -30(\.0+)? cm$", lines[cm]);
        Assert.Equal("q", lines[cm - 1]);
        int close = MatchingClose(lines, cm);
        int fill = System.Array.FindIndex(lines, l => l == "0 0 1 rg");
        int stroke = System.Array.FindIndex(lines, l => l is "S" or "s");
        int before = System.Array.FindIndex(lines, l => l.Contains("(Before)", System.StringComparison.Ordinal));
        int inside = System.Array.FindIndex(lines, l => l.Contains("(Inside)", System.StringComparison.Ordinal));
        int after = System.Array.FindIndex(lines, l => l.Contains("(After)", System.StringComparison.Ordinal));
        Assert.True(before >= 0 && before < cm, $"the block before the box ({before}) is inside its transform ({cm})");
        Assert.True(cm < fill && fill < inside && inside < close, $"the fill ({fill}) and the block ({inside}) must be inside the transform ({cm}–{close})");
        Assert.True(cm < stroke && stroke < close, $"the border ({stroke}) must be stroked inside the transform ({cm}–{close})");
        Assert.True(after > close, $"the block after the box ({after}) is inside its transform ({cm}–{close})");
        Assert.Equal(lines.Count(l => l == "q"), lines.Count(l => l == "Q"));
        Assert.DoesNotContain(Content(RedBox(), Para("Inside"), new BoxEnd()).Split('\n'), l => l.TrimEnd().EndsWith(" cm", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// A transformed box sliced across pages sets its transform on each page, once per fragment, and
    /// closes it with the fragment, so every page's content balances.
    /// </summary>
    [Fact]
    public void ATransformedBoxSlicedAcrossPagesSetsItsTransformOnEachPage()
    {
        var section = new Section();
        section.Children.Add(new BoxStart { Shading = "#0000FF", Transform = new double[] { 1, 0, 0, 1, 20, -30 } });
        for (int i = 0; i < 60; i++) section.Children.Add(Para("Line " + i, spacing: 2));
        section.Children.Add(new BoxEnd());
        var doc = new Document();
        doc.Sections.Add(section);
        byte[] pdf = doc.ToPdf();
        string[] lines = PdfContentProbe.DecodeStreams(pdf).Split('\n').Select(l => l.Trim()).ToArray();

        int pages = PdfContentProbe.CountPages(pdf);
        Assert.True(pages >= 2, "the box was not sliced");
        Assert.Equal(pages, lines.Count(l => l.EndsWith(" cm", System.StringComparison.Ordinal)));
        Assert.Equal(lines.Count(l => l == "q"), lines.Count(l => l == "Q"));
    }

    /// <summary>A transformed box inside a table cell sets its transform round its fill and blocks there too, and closes it.</summary>
    [Fact]
    public void ATransformedBoxInACellSetsItsTransform()
    {
        var table = new Table();
        var row = new TableRow();
        var cell = new TableCell();
        cell.Blocks.Add(new BoxStart { Shading = "#0000FF", Transform = new double[] { 1, 0, 0, 1, 20, -30 } });
        cell.Blocks.Add(Para("Inside"));
        cell.Blocks.Add(new BoxEnd());
        row.Cells.Add(cell);
        table.Rows.Add(row);
        string[] lines = Content(Para("Before"), table).Split('\n').Select(l => l.Trim()).ToArray();

        int cm = Assert.Single(lines.Select((l, i) => (l, i)).Where(p => p.l.EndsWith(" cm", System.StringComparison.Ordinal)).Select(p => p.i));
        int close = MatchingClose(lines, cm);
        int fill = System.Array.FindIndex(lines, l => l == "0 0 1 rg");
        int inside = System.Array.FindIndex(lines, l => l.Contains("(Inside)", System.StringComparison.Ordinal));
        int before = System.Array.FindIndex(lines, l => l.Contains("(Before)", System.StringComparison.Ordinal));
        Assert.True(before >= 0 && before < cm, $"the paragraph before the table ({before}) is inside the cell box's transform ({cm})");
        Assert.True(cm < fill && fill < inside && inside < close, $"the fill ({fill}) and the block ({inside}) must be inside the transform ({cm}–{close})");
        Assert.Equal(lines.Count(l => l == "q"), lines.Count(l => l == "Q"));
    }

    /// <summary>The <c>Q</c> that closes the <c>q</c> just before line <paramref name="cm"/>, counting the pairs between.</summary>
    private static int MatchingClose(string[] lines, int cm)
    {
        int depth = 1;
        for (int i = cm + 1; i < lines.Length; i++)
        {
            if (lines[i] == "q") depth++;
            else if (lines[i] == "Q" && --depth == 0) return i;
        }
        Assert.Fail("the transform's q is never closed");
        return -1;
    }

    /// <summary>The <c>x y w h re</c> after line <paramref name="from"/>.</summary>
    private static double[] Rect(string[] lines, int from)
    {
        string re = lines.Skip(from).First(l => l.EndsWith(" re", System.StringComparison.Ordinal));
        return re.Split(' ').Take(4).Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }

    /// <summary>A box's vertical margins are space outside its border, above and below.</summary>
    [Fact]
    public void ABoxsMarginsAreOutsideItsBorder()
    {
        var box = RedBox();
        box.MarginTop = 20;
        box.MarginBottom = 30;
        box.MarginRight = 40;
        string content = Content(box, Para("Inside"), new BoxEnd(), Para("After"));
        var segments = PdfContentProbe.StrokedSegments(content);
        var horizontal = segments.Where(s => s.IsHorizontal).Select(s => s.Y0).OrderBy(y => y).ToList();
        var page = new Section();

        Assert.Equal(page.PageHeightPt - page.MarginTop - 20 - 0.5, horizontal[1], 2);
        Assert.Equal(PdfContentProbe.YOf(content, "After") + LineHeight() + 30 + 0.5, horizontal[0], 2);
        Assert.Equal(page.PageWidthPt - page.MarginRight - 40 - 0.5, segments.Where(s => s.IsVertical).Max(s => s.X0), 2);
    }

    /// <summary>A right-aligned paragraph in a box ends at the box's right padding.</summary>
    [Fact]
    public void ARightAlignedParagraphEndsAtThePadding()
    {
        var p = new Paragraph { Alignment = TextAlignment.Right, SpacingBefore = 0, SpacingAfter = 0 };
        p.Runs.Add(new TextRun { Text = "Right", FontSize = 12 });
        string content = Content(RedBox(), p, new BoxEnd());
        var page = new Section();

        double end = PdfContentProbe.XOf(content, "Right")
                     + Export.PdfExporter.AdvanceWidth("Right", bold: false, italic: false, size: 12);
        // The border's 1pt and the 10pt padding in from the right margin.
        Assert.Equal(page.PageWidthPt - page.MarginRight - 11, end, 1);
    }

    /// <summary>
    /// A box whose top border and padding would not fit on this page starts on the next, whole —
    /// not as a sliver of border at the foot of this one.
    /// </summary>
    [Fact]
    public void ABoxThatCannotStartOnThisPageStartsOnTheNext()
    {
        var page = new Section();
        var push = new Paragraph { SpacingBefore = page.PageHeightPt - page.MarginTop - page.MarginBottom - 20 };
        push.Runs.Add(new TextRun { Text = "Push" });
        var box = RedBox(padding: 30);

        var section = new Section();
        section.Children.Add(push);
        section.Children.Add(box);
        section.Children.Add(Para("Inside"));
        section.Children.Add(new BoxEnd());
        var doc = new Document();
        doc.Sections.Add(section);
        byte[] pdf = doc.ToPdf();

        Assert.Equal(2, PdfContentProbe.CountPages(pdf));
        Assert.Equal(4, PdfContentProbe.StrokedSegments(PdfContentProbe.DecodeStreams(pdf)).Count);
    }

    /// <summary>
    /// A box's bottom padding and border never cross the page's bottom margin: wherever its last
    /// block ends, they go on the page they fit on.
    /// </summary>
    [Fact]
    public void ABoxsBottomNeverCrossesTheBottomMargin()
    {
        var page = new Section();
        double floor = page.MarginBottom;
        double body = page.PageHeightPt - page.MarginTop - page.MarginBottom;

        for (int spacer = (int)body - 60; spacer < (int)body - 10; spacer++)
        {
            var push = new Paragraph { SpacingBefore = spacer, SpacingAfter = 0 };
            push.Runs.Add(new TextRun { Text = "Push" });
            var section = new Section();
            section.Children.Add(push);
            section.Children.Add(RedBox());
            section.Children.Add(Para("Inside"));
            section.Children.Add(new BoxEnd());
            var doc = new Document();
            doc.Sections.Add(section);

            var segments = PdfContentProbe.StrokedSegments(PdfContentProbe.DecodeStreams(doc.ToPdf()));
            Assert.All(segments, s => Assert.True(System.Math.Min(s.Y0, s.Y1) >= floor - 0.01,
                $"with a {spacer}pt spacer an edge reaches {System.Math.Min(s.Y0, s.Y1):F2}, below the margin at {floor}"));
        }
    }

    /// <summary>A box's background image is painted in it, behind its blocks.</summary>
    [Fact]
    public void ABoxPaintsItsBackgroundImage()
    {
        var box = new BoxStart
        {
            PaddingTop = 5,
            BackgroundImage = new BackgroundImage { Data = ParagraphBoxTests.MakeRgbPng(8, 8), Repeat = BackgroundRepeat.NoRepeat },
        };
        string content = Content(box, Para("Inside"), new BoxEnd());

        var draw = Assert.Single(ParagraphBoxTests.ImageDraws(content));
        Assert.True(content.IndexOf(" Do", System.StringComparison.Ordinal) < content.IndexOf("(Inside)", System.StringComparison.Ordinal),
            "the image is not painted before the text");
        Assert.Equal(PageLeft(), draw.x, 2);
        Assert.Empty(ParagraphBoxTests.ImageDraws(Content(new BoxStart { PaddingTop = 5 }, Para("Inside"), new BoxEnd())));
    }

    /// <summary>
    /// A whole box has its corners rounded; a box a page break cuts is square only at the cut —
    /// its first fragment keeps its top corners rounded, its last its bottom ones — as CSS's
    /// <c>box-decoration-break: slice</c> leaves it. It squared all four of each fragment.
    /// </summary>
    [Fact]
    public void AWholeBoxIsRounded_AndASlicedOneIsSquareOnlyAtTheCut()
    {
        static int Curves(string content) => Regex.Matches(content, @"\bc\n").Count;

        var whole = new BoxStart { Shading = "#0000FF", CornerRadiusPt = 6, PaddingTop = 4 };
        Assert.True(Curves(Content(whole, Para("Inside"), new BoxEnd())) >= 4, "the whole box is not rounded");
        Assert.Equal(0, Curves(Content(new BoxStart { Shading = "#0000FF", PaddingTop = 4 }, Para("Inside"), new BoxEnd())));

        var sliced = new List<IDocElement> { new BoxStart { Shading = "#0000FF", CornerRadiusPt = 6 } };
        for (int i = 0; i < 60; i++) sliced.Add(Para("Line " + i, spacing: 2));
        sliced.Add(new BoxEnd());
        string cut = Content(sliced.ToArray());
        // Two fragments, one fill each, two rounded corners each.
        Assert.Equal(2, Regex.Matches(cut, @"0 0 1 rg").Count);
        Assert.Equal(4, Curves(cut));
    }

    /// <summary>The fill is painted before the blocks, so it lies behind them.</summary>
    [Fact]
    public void TheFillIsPaintedBehindTheBlocks()
    {
        var box = RedBox();
        box.Shading = "#0000FF";
        string content = Content(Para("Before"), box, Para("Inside"), new BoxEnd());

        int fill = content.IndexOf("0 0 1 rg", System.StringComparison.Ordinal);
        int before = content.IndexOf("(Before)", System.StringComparison.Ordinal);
        int inside = content.IndexOf("(Inside)", System.StringComparison.Ordinal);

        Assert.True(fill >= 0 && before >= 0 && inside >= 0, "a fill or a text was not found");
        // After what preceded the box, before what is inside it.
        Assert.True(before < fill && fill < inside, $"fill at {fill}, before at {before}, inside at {inside}");
    }

    /// <summary>
    /// A box that breaks across a page is drawn once per page, sliced: its top border on the first
    /// page, its bottom border on the last, both sides on each, and the continuation starting at
    /// the top of the text block.
    /// </summary>
    [Fact]
    public void ABoxThatBreaksAcrossAPageIsSliced()
    {
        // A paragraph first, so the box's first fragment does not itself start at the top.
        var blocks = new List<IDocElement> { Para("Before"), RedBox() };
        for (int i = 0; i < 80; i++) blocks.Add(Para("Line " + i, spacing: 2));
        blocks.Add(new BoxEnd());

        var section = new Section();
        section.Children.AddRange(blocks);
        var doc = new Document();
        doc.Sections.Add(section);
        byte[] pdf = doc.ToPdf();
        var segments = PdfContentProbe.StrokedSegments(PdfContentProbe.DecodeStreams(pdf));

        int pages = PdfContentProbe.CountPages(pdf);
        Assert.True(pages >= 2, $"the box fit on {pages} page");
        // The top edge once, the bottom edge once, and both sides on every page.
        Assert.Equal(2, segments.Count(s => s.IsHorizontal));
        Assert.Equal(2 * pages, segments.Count(s => s.IsVertical));
        double topOfText = section.PageHeightPt - section.MarginTop;
        Assert.Contains(segments, s => s.IsVertical && System.Math.Abs(System.Math.Max(s.Y0, s.Y1) - topOfText) < 0.01);
    }

    /// <summary>Boxes nest: the inner one inside the outer's padding, its fill over the outer's.</summary>
    [Fact]
    public void BoxesNest()
    {
        var outer = RedBox();
        outer.Shading = "#0000FF";
        var inner = new BoxStart { Shading = "#00FF00", PaddingLeft = 5, MarginLeft = 3 };
        string content = Content(outer, Para("Outer"), inner, Para("Inner"), new BoxEnd(), new BoxEnd());

        double left = PageLeft();
        Assert.Equal(left + 11, PdfContentProbe.XOf(content, "Outer"), 2);
        // The outer's border and padding, the inner's margin and padding: 1 + 10 + 3 + 5.
        Assert.Equal(left + 19, PdfContentProbe.XOf(content, "Inner"), 2);

        int outerFill = content.IndexOf("0 0 1 rg", System.StringComparison.Ordinal);
        int innerFill = content.IndexOf("0 1 0 rg", System.StringComparison.Ordinal);
        Assert.True(outerFill >= 0 && outerFill < innerFill, "the outer fill is not beneath the inner one");
    }

    /// <summary>A negative margin widens the band past the page's margin, as Bootstrap's <c>.row</c> does.</summary>
    [Fact]
    public void ANegativeMarginWidensTheBand()
    {
        string content = Content(new BoxStart { MarginLeft = -20 }, Para("Wide"), new BoxEnd());

        Assert.Equal(PageLeft() - 20, PdfContentProbe.XOf(content, "Wide"), 2);
    }

    /// <summary>
    /// In a table cell the box takes its room in the row: the row is taller by the box's padding
    /// and borders, and its edges lie inside the cell.
    /// </summary>
    [Fact]
    public void ABoxInACellTakesItsRoomInTheRow()
    {
        static (string Content, double BelowY) Row(bool boxed)
        {
            var cell = new TableCell();
            if (boxed)
            {
                var box = RedBox();
                box.MarginTop = 5;
                box.MarginBottom = 4;
                cell.Blocks.Add(box);
            }
            cell.Blocks.Add(Para("Cell"));
            if (boxed) cell.Blocks.Add(new BoxEnd());
            cell.Blocks.Add(Para("Tail"));
            var table = new Table();
            var row = new TableRow();
            row.Cells.Add(cell);
            table.Rows.Add(row);
            string content = Content(table, Para("Below"));
            return (content, PdfContentProbe.YOf(content, "Below"));
        }

        var plain = Row(false);
        var boxed = Row(true);

        // Margins of 5pt and 4pt, and 10pt of padding and a 1pt border on each side.
        Assert.Equal(31, plain.BelowY - boxed.BelowY, 1);
        Assert.Equal(PdfContentProbe.XOf(plain.Content, "Cell") + 11, PdfContentProbe.XOf(boxed.Content, "Cell"), 2);
        // The block after the box is back at the cell's own edge.
        Assert.Equal(PdfContentProbe.XOf(plain.Content, "Tail"), PdfContentProbe.XOf(boxed.Content, "Tail"), 2);
        Assert.Contains("1 0 0 RG", boxed.Content);
    }

    /// <summary>
    /// An unmatched end is ignored, and a box left open is closed where its list ends — with its
    /// bottom border.
    /// </summary>
    [Fact]
    public void AnUnmatchedEndIsIgnored_AndAnUnclosedBoxIsClosed()
    {
        string content = Content(new BoxEnd(), RedBox(), Para("Open"));
        var segments = PdfContentProbe.StrokedSegments(content);

        Assert.Equal(2, segments.Count(s => s.IsHorizontal));
        Assert.True(PdfContentProbe.YOf(content, "Open") > segments.Where(s => s.IsHorizontal).Min(s => s.Y0));
    }

    /// <summary>
    /// The exporters that know no box keep its blocks: the markers are skipped, the content is not.
    /// </summary>
    [Fact]
    public void TheOtherExportersKeepTheBoxsBlocks()
    {
        var section = new Section();
        section.Children.Add(RedBox());
        section.Children.Add(Para("Boxed words"));
        section.Children.Add(new BoxEnd());
        var doc = new Document();
        doc.Sections.Add(section);

        Assert.Contains("Boxed words", System.Text.Encoding.UTF8.GetString(doc.ToHtml()));
    }

    /// <summary>
    /// A paragraph kept together inside a box is measured at the box's band, where it wraps to
    /// more lines than at the page's width — or it is judged to fit where it does not, and splits.
    /// </summary>
    [Fact]
    public void AKeptParagraphInABoxIsMeasuredInItsBand()
    {
        const string words = "KFIRST one two three four five six seven eight nine ten eleven twelve " +
                             "thirteen fourteen fifteen sixteen seventeen eighteen nineteen KLAST";
        var page = new Section();
        double body = page.PageHeightPt - page.MarginTop - page.MarginBottom;
        double band = (page.PageWidthPt - page.MarginLeft - page.MarginRight) / 4;

        static Paragraph Kept(bool keep)
        {
            var p = new Paragraph { KeepTogether = keep, SpacingBefore = 0, SpacingAfter = 0 };
            p.Runs.Add(new TextRun { Text = words });
            return p;
        }
        (string Content, bool Split) Lay(int spacer, bool boxed, bool keep)
        {
            var push = new Paragraph { SpacingBefore = spacer, SpacingAfter = 0 };
            push.Runs.Add(new TextRun { Text = "Push" });
            var blocks = new List<IDocElement> { push };
            if (boxed) blocks.Add(new BoxStart { PaddingLeft = band * 1.5, PaddingRight = band * 1.5 });
            blocks.Add(Kept(keep));
            if (boxed) blocks.Add(new BoxEnd());
            string content = Content(blocks.ToArray());
            return (content, PdfContentProbe.YOf(content, "KLAST") > PdfContentProbe.YOf(content, "KFIRST"));
        }

        // A spacer at which the paragraph fits beside it at the page's width but not in the band.
        int found = -1;
        for (int spacer = (int)body - 200; spacer < (int)body - 20 && found < 0; spacer += 2)
            if (!Lay(spacer, boxed: false, keep: false).Split && Lay(spacer, boxed: true, keep: false).Split)
                found = spacer;
        Assert.True(found > 0, "no spacer separated the two widths; the test proves nothing");

        var kept = Lay(found, boxed: true, keep: true);
        Assert.False(kept.Split, "the kept paragraph straddled the page break");
        Assert.True(PdfContentProbe.YOf(kept.Content, "KFIRST") > PdfContentProbe.YOf(kept.Content, "Push"),
            "the kept paragraph did not move to the next page");
    }

    /// <summary>A nested box's blocks end at its own right padding, not the outer box's.</summary>
    [Fact]
    public void ANestedBoxsBlocksEndAtItsOwnRightPadding()
    {
        var p = new Paragraph { Alignment = TextAlignment.Right, SpacingBefore = 0, SpacingAfter = 0 };
        p.Runs.Add(new TextRun { Text = "Right", FontSize = 12 });
        string content = Content(RedBox(), new BoxStart { PaddingRight = 7, MarginRight = 2 }, p, new BoxEnd(), new BoxEnd());
        var page = new Section();

        double end = PdfContentProbe.XOf(content, "Right")
                     + Export.PdfExporter.AdvanceWidth("Right", bold: false, italic: false, size: 12);
        // The outer's border and padding, then the inner's margin and padding: 1 + 10 + 2 + 7.
        Assert.Equal(page.PageWidthPt - page.MarginRight - 20, end, 1);
    }

    /// <summary>A border marked <see cref="Border.None"/> draws nothing and takes no room, whatever its width.</summary>
    [Fact]
    public void ABorderMarkedNoneTakesNoRoom()
    {
        var box = new BoxStart { BorderLeft = new Border(4, "#FF0000") { None = true }, PaddingLeft = 6 };
        string content = Content(box, Para("In"), new BoxEnd());

        Assert.Equal(PageLeft() + 6, PdfContentProbe.XOf(content, "In"), 2);
        Assert.Empty(PdfContentProbe.StrokedSegments(content));
        // The control: the same border drawn takes its 4pt.
        string drawn = Content(new BoxStart { BorderLeft = new Border(4, "#FF0000"), PaddingLeft = 6 }, Para("In"), new BoxEnd());
        Assert.Equal(PageLeft() + 10, PdfContentProbe.XOf(drawn, "In"), 2);
    }

    /// <summary>Boxes still open where the list ends are all closed, the outer as well as the inner.</summary>
    [Fact]
    public void EveryUnclosedBoxIsClosed()
    {
        string content = Content(RedBox(), RedBox(), Para("Open"));

        Assert.Equal(4, PdfContentProbe.StrokedSegments(content).Count(s => s.IsHorizontal));
    }

    /// <summary>
    /// Nested boxes cut by a page break keep their order on every page: the outer fill beneath the
    /// inner. Slicing the outer first moved its paint ahead of the inner's mark, and the inner's
    /// fill landed under it.
    /// </summary>
    [Fact]
    public void NestedBoxesSlicedAcrossAPage_KeepTheOuterFillBeneath()
    {
        var blocks = new List<IDocElement> { Para("Before"), new BoxStart { Shading = "#0000FF" }, new BoxStart { Shading = "#00FF00" } };
        for (int i = 0; i < 80; i++) blocks.Add(Para("Line " + i, spacing: 2));
        blocks.Add(new BoxEnd());
        blocks.Add(new BoxEnd());

        var section = new Section();
        section.Children.AddRange(blocks);
        var doc = new Document();
        doc.Sections.Add(section);
        byte[] pdf = doc.ToPdf();
        string content = PdfContentProbe.DecodeStreams(pdf);

        int pages = PdfContentProbe.CountPages(pdf);
        Assert.True(pages >= 2, $"the boxes fit on {pages} page");
        var fills = Regex.Matches(content, "0 0 1 rg|0 1 0 rg").Select(m => m.Value).ToList();
        Assert.Equal(Enumerable.Range(0, pages).SelectMany(_ => new[] { "0 0 1 rg", "0 1 0 rg" }), fills);
    }

    /// <summary>
    /// A box whose top padding is taller than a page, at the top of one, starts there: a new page
    /// would have no more room, and moving to it left the first page blank.
    /// </summary>
    [Fact]
    public void ABoxTallerThanAPage_AtTheTopOfOne_StartsThere()
    {
        var page = new Section();
        double body = page.PageHeightPt - page.MarginTop - page.MarginBottom;
        var section = new Section();
        section.Children.Add(new BoxStart { PaddingTop = body + 10, BorderTop = Red });
        section.Children.Add(Para("Inside"));
        section.Children.Add(new BoxEnd());
        var doc = new Document();
        doc.Sections.Add(section);
        byte[] pdf = doc.ToPdf();

        // The padding fills the first page, and the paragraph goes on the second.
        Assert.Equal(2, PdfContentProbe.CountPages(pdf));
        Assert.False(double.IsNaN(PdfContentProbe.YOf(PdfContentProbe.DecodeStreams(pdf), "Inside")), "the paragraph was not drawn");
    }

    private static string CellContent(params IDocElement[] blocks) => CellContent("", blocks);

    private static string CellContent(string cellBackground, params IDocElement[] blocks)
    {
        var cell = new TableCell { BackgroundColor = cellBackground };
        cell.Blocks.AddRange(blocks);
        var row = new TableRow();
        row.Cells.Add(cell);
        var table = new Table();
        table.Rows.Add(row);
        return Content(Para("Before"), table);
    }

    private static Paragraph RightAligned(string text)
    {
        var p = new Paragraph { Alignment = TextAlignment.Right, SpacingBefore = 0, SpacingAfter = 0 };
        p.Runs.Add(new TextRun { Text = text, FontSize = 12 });
        return p;
    }

    private static double EndOf(string content, string text) =>
        PdfContentProbe.XOf(content, text) + Export.PdfExporter.AdvanceWidth(text, bold: false, italic: false, size: 12);

    /// <summary>A box in a cell is painted over the cell's background, not under it.</summary>
    [Fact]
    public void ABoxInACellIsPaintedOverTheCellsBackground()
    {
        string content = CellContent("#FF0000", new BoxStart { Shading = "#0000FF", PaddingTop = 4 }, Para("Cell"), new BoxEnd());

        int cellFill = content.IndexOf("1 0 0 rg", System.StringComparison.Ordinal);
        int boxFill = content.IndexOf("0 0 1 rg", System.StringComparison.Ordinal);
        int text = content.IndexOf("(Cell)", System.StringComparison.Ordinal);
        Assert.True(cellFill >= 0 && boxFill >= 0 && text >= 0, "a fill or the text was not found");
        Assert.True(cellFill < boxFill && boxFill < text, $"cell fill at {cellFill}, box fill at {boxFill}, text at {text}");
    }

    /// <summary>In a cell too, a box's blocks end at its right padding and border.</summary>
    [Fact]
    public void InACell_ABoxsBlocksEndAtItsRightPadding()
    {
        string plain = CellContent(RightAligned("Right"));
        string boxed = CellContent(RedBox(), RightAligned("Right"), new BoxEnd());

        Assert.Equal(EndOf(plain, "Right") - 11, EndOf(boxed, "Right"), 1);
    }

    /// <summary>In a cell, an unmatched end is ignored and the cell's blocks still drawn.</summary>
    [Fact]
    public void InACell_AnUnmatchedEndIsIgnored()
    {
        string content = CellContent(new BoxEnd(), Para("Cell"));

        Assert.Equal(PdfContentProbe.XOf(CellContent(Para("Cell")), "Cell"), PdfContentProbe.XOf(content, "Cell"), 2);
    }

    /// <summary>
    /// In a cell, when an inner box closes the blocks after it return to the outer box's band —
    /// both its edges — not to the cell's.
    /// </summary>
    [Fact]
    public void InACell_ClosingAnInnerBoxReturnsToTheOuterBand()
    {
        string plain = CellContent(Para("Back"), RightAligned("BackR"));
        string nested = CellContent(RedBox(), new BoxStart { PaddingLeft = 5, PaddingRight = 5 }, Para("In"), new BoxEnd(),
                                    Para("Back"), RightAligned("BackR"), new BoxEnd());

        Assert.Equal(PdfContentProbe.XOf(plain, "Back") + 11, PdfContentProbe.XOf(nested, "Back"), 2);
        Assert.Equal(EndOf(plain, "BackR") - 11, EndOf(nested, "BackR"), 1);
    }

    /// <summary>
    /// A cell is measured the way it is drawn: a paragraph in a box wraps in the box's band, and the
    /// row is tall enough for every line of it.
    /// </summary>
    [Fact]
    public void InACell_ABoxedParagraphIsMeasuredInItsBand()
    {
        var p = new Paragraph { SpacingBefore = 0, SpacingAfter = 0 };
        p.Runs.Add(new TextRun { Text = "KFIRST one two three four five six seven eight nine ten eleven twelve KLAST" });
        var page = new Section();
        double inset = (page.PageWidthPt - page.MarginLeft - page.MarginRight) * 0.4;

        var cell = new TableCell();
        cell.Blocks.Add(new BoxStart { PaddingLeft = inset, PaddingRight = inset });
        cell.Blocks.Add(p);
        cell.Blocks.Add(new BoxEnd());
        var row = new TableRow();
        row.Cells.Add(cell);
        var table = new Table();
        table.Rows.Add(row);
        string content = Content(table, Para("Below"));

        Assert.True(PdfContentProbe.YOf(content, "KFIRST") - PdfContentProbe.YOf(content, "KLAST") > 3 * LineHeight() - 0.01,
            "the paragraph did not wrap in the band; the test proves nothing");
        Assert.True(PdfContentProbe.YOf(content, "KLAST") > PdfContentProbe.YOf(content, "Below"),
            "the row ends above the paragraph's last line");
    }

    /// <summary>In a cell, boxes left open where its blocks end are all closed, as though ended there.</summary>
    [Fact]
    public void InACell_EveryUnclosedBoxIsClosed()
    {
        static int Horizontal(string c) => PdfContentProbe.StrokedSegments(c).Count(s => s.IsHorizontal);

        string open = CellContent(RedBox(), RedBox(), Para("Open"));
        string closed = CellContent(RedBox(), RedBox(), Para("Open"), new BoxEnd(), new BoxEnd());
        string none = CellContent(Para("Open"));

        Assert.Equal(Horizontal(none) + 4, Horizontal(closed));
        Assert.Equal(Horizontal(closed), Horizontal(open));
    }

    /// <summary>
    /// On a tagged page a cell whose only block draws nothing in a cell — a table of contents —
    /// still reaches content: an empty marked sequence of its own, or its <c>/TD</c> is hollow.
    /// A cell whose block drew gets none.
    /// </summary>
    /// <remarks>
    /// Counted in the content, not the tree: a cell is exempt from pruning, so a hollow one is
    /// still written as a <c>/TD</c>.
    /// </remarks>
    [Fact]
    public void ACellWhoseBlocksDrawNothing_StillReachesContent()
    {
        static int EmptyCellMarks(IDocElement first)
        {
            var a = new TableCell();
            a.Blocks.Add(first);
            var b = new TableCell();
            b.Blocks.Add(Para("B"));
            var row = new TableRow();
            row.Cells.Add(a);
            row.Cells.Add(b);
            var table = new Table();
            table.Rows.Add(row);
            var section = new Section();
            section.Children.Add(table);
            var doc = new Document { Tagged = true };
            doc.Sections.Add(section);
            return Regex.Matches(PdfContentProbe.DecodeStreams(doc.ToPdf()), @"/TD <</MCID \d+>> BDC\s+EMC").Count;
        }

        Assert.Equal(1, EmptyCellMarks(new TocBlock()));
        Assert.Equal(0, EmptyCellMarks(Para("A")));
    }
}
