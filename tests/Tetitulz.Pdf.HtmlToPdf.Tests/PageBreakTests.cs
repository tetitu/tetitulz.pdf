// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// Paged-media control in the managed engine: <c>page-break-inside: avoid</c> /
/// <c>break-inside: avoid</c> keeps a block on one page, <c>page-break-after: avoid</c> keeps a
/// block with the one after it, and a table's header rows — and only its header rows — repeat.
/// </summary>
/// <remarks>
/// <para>Asserted on which PAGE each word lands on, read back from the produced PDF.</para>
/// <para><b>The filler is found, not guessed.</b> Each fixture is preceded by as many filler lines
/// as it takes for the control — the same markup without the property — to straddle a page
/// boundary. That is the precondition every assertion here depends on, so it is established
/// rather than assumed from paragraph heights the mapper is free to change.</para>
/// </remarks>
public class PageBreakTests
{
    private static string Filler(int count) =>
        string.Concat(Enumerable.Range(1, count).Select(i => $"<p>filler{i}</p>"));

    /// <summary>The page index each word is first drawn on.</summary>
    private static Dictionary<string, int> PagesOf(string html, params string[] words)
    {
        using var input = new MemoryStream(ToPdf(html), writable: false);
        var found = new Dictionary<string, int>();
        int page = 0;
        foreach (var p in PdfOperations.ExtractStructuredText(input))
        {
            foreach (var w in p.Lines.SelectMany(l => l.Words))
                foreach (string want in words)
                    if (!found.ContainsKey(want) && w.Text.Contains(want, StringComparison.Ordinal))
                        found[want] = page;
            page++;
        }
        foreach (string want in words)
            Assert.True(found.ContainsKey(want), $"\"{want}\" was not laid out at all");
        return found;
    }

    /// <summary>
    /// The smallest filler count at which <paramref name="control"/> puts <paramref name="first"/>
    /// and <paramref name="last"/> on different pages.
    /// </summary>
    private static int FillerThatSplits(Func<int, string> control, string first, string last)
    {
        for (int n = 1; n <= 80; n++)
        {
            var pages = PagesOf(control(n), first, last);
            if (pages[first] < pages[last]) return n;
        }
        Assert.Fail($"no filler count made the control split {first} from {last}; the test proves nothing");
        return -1;
    }

    private static string Block(string style) =>
        $"<div style='{style}'><p>BLOCKTOP</p><p>middle</p><p>middle</p><p>BLOCKEND</p></div>";

    [Theory]
    [InlineData("page-break-inside:avoid")]
    [InlineData("break-inside:avoid")]
    [InlineData("break-inside:avoid-page")]
    public void BreakInsideAvoid_MovesABlockThatWouldStraddleToTheNextPage(string css)
    {
        int n = FillerThatSplits(f => Filler(f) + Block(""), "BLOCKTOP", "BLOCKEND");
        var control = PagesOf(Filler(n) + Block(""), "BLOCKTOP", "BLOCKEND");
        var kept = PagesOf(Filler(n) + Block(css), "BLOCKTOP", "BLOCKEND");

        Assert.Equal(kept["BLOCKTOP"], kept["BLOCKEND"]);
        // Moved forward to where the control's end landed, not back onto the earlier page.
        Assert.Equal(control["BLOCKEND"], kept["BLOCKTOP"]);
    }

    /// <summary>
    /// A block taller than a page cannot be kept together: it flows exactly as it would without the
    /// property — every line on the page it would have been on — rather than being pushed to a
    /// fresh page, or broken early at whatever point the rest of it happens to fit.
    /// </summary>
    /// <remarks>
    /// Every line is compared, not only the first and last: re-deciding the keep part-way down the
    /// block moves the page boundary a few lines earlier and still ends on the same page, which a
    /// first-and-last comparison cannot see — a mutation doing exactly that survived it.
    /// </remarks>
    [Fact]
    public void BreakInsideAvoid_OnABlockTallerThanAPage_FlowsAsItWouldWithout()
    {
        string[] words = new[] { "BEFORE", "TALLTOP" }
            .Concat(Enumerable.Range(1, 60).Select(i => $"L{i}Z"))
            .Concat(new[] { "TALLEND" })
            .ToArray();
        string Tall(string style) =>
            $"<p>BEFORE</p><div style='{style}'><p>TALLTOP</p>" +
            string.Concat(Enumerable.Range(1, 60).Select(i => $"<p>L{i}Z</p>")) + "<p>TALLEND</p></div>";

        var plain = PagesOf(Tall(""), words);
        var kept = PagesOf(Tall("page-break-inside:avoid"), words);

        Assert.True(plain["TALLEND"] > plain["TALLTOP"], "the tall block must span pages for this to mean anything");
        foreach (string w in words)
            Assert.True(plain[w] == kept[w], $"{w} is on page {kept[w]} with the property and {plain[w]} without it");
    }

    /// <summary>
    /// A single paragraph of several lines with <c>break-inside: avoid</c> is kept whole — the
    /// paragraph's own keep, with no chain around it.
    /// </summary>
    [Fact]
    public void BreakInsideAvoid_OnOneParagraph_KeepsItsLinesTogether()
    {
        string Para(string style) => $"<p style='{style}'>PARATOP<br>two<br>three<br>PARAEND</p>";

        int n = FillerThatSplits(f => Filler(f) + Para(""), "PARATOP", "PARAEND");
        var kept = PagesOf(Filler(n) + Para("break-inside:avoid"), "PARATOP", "PARAEND");

        Assert.Equal(kept["PARATOP"], kept["PARAEND"]);
    }

    /// <summary>A table with <c>page-break-inside: avoid</c> moves whole to the next page.</summary>
    [Fact]
    public void BreakInsideAvoid_OnATable_KeepsItsRowsOnOnePage()
    {
        string Table(string style) =>
            $"<table style='{style}'><tr><td>ROWFIRST</td></tr><tr><td>b</td></tr>" +
            "<tr><td>c</td></tr><tr><td>ROWLAST</td></tr></table>";

        int n = FillerThatSplits(f => Filler(f) + Table(""), "ROWFIRST", "ROWLAST");
        var kept = PagesOf(Filler(n) + Table("page-break-inside:avoid"), "ROWFIRST", "ROWLAST");

        Assert.Equal(kept["ROWFIRST"], kept["ROWLAST"]);
    }

    /// <summary>
    /// <c>page-break-after: avoid</c> keeps a heading with the block after it — Bootstrap's print
    /// stylesheet sets it on every <c>h2</c> and <c>h3</c>.
    /// </summary>
    [Theory]
    [InlineData("page-break-after:avoid")]
    [InlineData("break-after:avoid")]
    public void BreakAfterAvoid_KeepsAHeadingWithTheNextBlock(string css)
    {
        string Tail(string style) => $"<h3 style='{style}'>HEADING</h3><p>FOLLOWS</p>";

        int n = FillerThatSplits(f => Filler(f) + Tail(""), "HEADING", "FOLLOWS");
        var kept = PagesOf(Filler(n) + Tail(css), "HEADING", "FOLLOWS");

        Assert.Equal(kept["HEADING"], kept["FOLLOWS"]);
    }

    private const string Boxed = "border:1px solid #000;padding:6px;";

    /// <summary>
    /// A bordered block is one box, kept as a box (2026-10-19): <c>break-inside: avoid</c> moves it whole — the
    /// exporter measuring its content, where a box was judged by its stated least height alone, which it has none of.
    /// </summary>
    [Fact]
    public void BreakInsideAvoid_OnABox_MovesItWhole()
    {
        int n = FillerThatSplits(f => Filler(f) + Block(Boxed), "BLOCKTOP", "BLOCKEND");
        var control = PagesOf(Filler(n) + Block(Boxed), "BLOCKTOP", "BLOCKEND");
        var kept = PagesOf(Filler(n) + Block(Boxed + "break-inside:avoid"), "BLOCKTOP", "BLOCKEND");

        Assert.Equal(kept["BLOCKTOP"], kept["BLOCKEND"]);
        Assert.Equal(control["BLOCKEND"], kept["BLOCKTOP"]);
    }

    /// <summary>
    /// <c>break-after: avoid</c> on a box keeps it with the block after it (2026-10-19; the keep fell on the box's
    /// end and was lost), and a heading kept with the block after it stays with a box there — the chain entering it.
    /// </summary>
    [Theory]
    [InlineData("box")]
    [InlineData("heading")]
    [InlineData("nested")]
    public void BreakAfterAvoid_KeepsABoxWithTheNextBlock_AndAHeadingWithABox(string which)
    {
        string Tail(string avoid) => which switch
        {
            "box" => $"<div style='{Boxed}{avoid}'><p>BOXTOP</p><p>BOXEND</p></div><p>FOLLOWS</p>",
            "heading" => $"<h3 style='{avoid}'>BOXEND</h3><div style='{Boxed}'><p>FOLLOWS</p><p>more</p></div>",
            // An element whose last block closes a box of its own: that box keeps with what follows.
            _ => $"<div style='{avoid}'><p>lead</p><div style='{Boxed}'><p>BOXEND</p></div></div><p>FOLLOWS</p>",
        };

        int n = FillerThatSplits(f => Filler(f) + Tail(""), "BOXEND", "FOLLOWS");
        var kept = PagesOf(Filler(n) + Tail("break-after:avoid"), "BOXEND", "FOLLOWS");
        Assert.Equal(kept["BOXEND"], kept["FOLLOWS"]);
    }

    /// <summary>
    /// A heading kept with a paragraph longer than a page needs only that paragraph's first line on
    /// its page — the paragraph is not required to fit, which would make the keep impossible.
    /// </summary>
    [Fact]
    public void BreakAfterAvoid_BeforeAParagraphLongerThanAPage_StillMovesTheHeading()
    {
        string longText = string.Join(" ", Enumerable.Repeat("words", 1500));
        string Tail(string style) => $"<h3 style='{style}'>HEADING</h3><p>FOLLOWS {longText}</p>";

        int n = FillerThatSplits(f => Filler(f) + Tail(""), "HEADING", "FOLLOWS");
        var kept = PagesOf(Filler(n) + Tail("page-break-after:avoid"), "HEADING", "FOLLOWS");

        Assert.Equal(kept["HEADING"], kept["FOLLOWS"]);
    }

    // ------------------------------------------------------------------ header rows

    private static int Occurrences(byte[] pdf, string word) =>
        Regex.Matches(DecodeStreams(pdf), @"\(" + Regex.Escape(word) + @"\)").Count;

    /// <summary>
    /// A row whose first cell is a <c>&lt;th&gt;</c> row LABEL is body content, not a header. It
    /// used to count as a header row, and every header row above a page break is repeated — so each
    /// page re-drew every row placed so far and sixty labelled rows became thirty-one pages.
    /// </summary>
    [Fact]
    public void RowsLabelledWithTh_AreNotRepeatedAsHeaders()
    {
        string rows = string.Concat(Enumerable.Range(1, 60).Select(i => $"<tr><th>LBL{i}X</th><td>value{i}</td></tr>"));
        byte[] pdf = ToPdf($"<table>{rows}</table>");

        Assert.True(CountPages(pdf) >= 2, "the fixture must paginate for a repeat to be observable");
        Assert.Equal(1, Occurrences(pdf, "LBL1X"));
        Assert.True(CountPages(pdf) <= 3, $"60 labelled rows took {CountPages(pdf)} pages");
    }

    /// <summary>A <c>&lt;thead&gt;</c> repeats on every page — the control for the test above.</summary>
    [Fact]
    public void TheadRows_AreRepeatedOnEveryPage()
    {
        string rows = string.Concat(Enumerable.Range(1, 60).Select(i => $"<tr><td>row{i}</td><td>value{i}</td></tr>"));
        byte[] pdf = ToPdf($"<table><thead><tr><th>COLHEAD</th><th>VALHEAD</th></tr></thead><tbody>{rows}</tbody></table>");

        int pages = CountPages(pdf);
        Assert.True(pages >= 2, "the fixture must paginate for a repeat to be observable");
        Assert.Equal(pages, Occurrences(pdf, "COLHEAD"));
    }

    /// <summary>
    /// A LEADING row of nothing but <c>&lt;th&gt;</c>, in a table with no <c>&lt;thead&gt;</c>, is
    /// still the table's header and repeats — the convention tables written without a
    /// <c>thead</c> rely on.
    /// </summary>
    [Fact]
    public void ALeadingRowOfThCells_IsStillARepeatedHeader()
    {
        string rows = string.Concat(Enumerable.Range(1, 60).Select(i => $"<tr><td>row{i}</td><td>value{i}</td></tr>"));
        byte[] pdf = ToPdf($"<table><tr><th>COLHEAD</th><th>VALHEAD</th></tr>{rows}</table>");

        int pages = CountPages(pdf);
        Assert.True(pages >= 2, "the fixture must paginate for a repeat to be observable");
        Assert.Equal(pages, Occurrences(pdf, "COLHEAD"));
    }

    /// <summary>
    /// A leading row of <c>&lt;th&gt;</c> above rows LABELLED with <c>&lt;th&gt;</c>: only the
    /// leading row is the header, and it repeats; the labelled rows are drawn once.
    /// </summary>
    /// <remarks>
    /// The fixture the renderer's half-page cap cannot hide. Were every labelled row a header too,
    /// the header block would be the whole table — over half a page — and nothing would repeat,
    /// so the leading row's count, not the labels', is what tells the two readings apart.
    /// </remarks>
    [Fact]
    public void ALeadingThRowAboveThLabelledRows_IsTheOnlyHeader()
    {
        string rows = string.Concat(Enumerable.Range(1, 60).Select(i => $"<tr><th>LBL{i}X</th><td>value{i}</td></tr>"));
        byte[] pdf = ToPdf($"<table><tr><th>COLHEAD</th><th>VALHEAD</th></tr>{rows}</table>");

        int pages = CountPages(pdf);
        Assert.True(pages >= 2, "the fixture must paginate for a repeat to be observable");
        Assert.Equal(pages, Occurrences(pdf, "COLHEAD"));
        Assert.Equal(1, Occurrences(pdf, "LBL1X"));
    }

    /// <summary>
    /// With a <c>&lt;thead&gt;</c>, the header is the <c>&lt;thead&gt;</c>: a first body row of
    /// <c>&lt;th&gt;</c> cells is a section heading inside the body, drawn once.
    /// </summary>
    [Fact]
    public void WithAThead_AThRowInTheBodyIsNotAHeader()
    {
        string rows = string.Concat(Enumerable.Range(1, 60).Select(i => $"<tr><td>row{i}</td></tr>"));
        byte[] pdf = ToPdf($"<table><thead><tr><th>COLHEAD</th></tr></thead>" +
                           $"<tbody><tr><th>SECTIONX</th></tr>{rows}</tbody></table>");

        int pages = CountPages(pdf);
        Assert.True(pages >= 2, "the fixture must paginate for a repeat to be observable");
        Assert.Equal(pages, Occurrences(pdf, "COLHEAD"));
        Assert.Equal(1, Occurrences(pdf, "SECTIONX"));
    }

    /// <summary>
    /// A header block taller than half a page is not repeated: a continuation page must keep room
    /// for the rows it continues.
    /// </summary>
    [Fact]
    public void AHeaderTallerThanHalfAPage_IsNotRepeated()
    {
        string head = string.Concat(Enumerable.Range(1, 30).Select(i => $"<tr><th>HEAD{i}X</th></tr>"));
        string rows = string.Concat(Enumerable.Range(1, 60).Select(i => $"<tr><td>row{i}</td></tr>"));
        byte[] pdf = ToPdf($"<table><thead>{head}</thead><tbody>{rows}</tbody></table>");

        Assert.True(CountPages(pdf) >= 2, "the fixture must paginate for a repeat to be observable");
        Assert.Equal(1, Occurrences(pdf, "HEAD1X"));
    }
}
