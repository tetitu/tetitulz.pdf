// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Splitter;

namespace Tetitulz.Pdf.Splitter.Tests;

/// <summary>The print-dialog range grammar, both ways, and its groups.</summary>
public sealed class PageRangeTextTests
{
    private static int[] Parse(string text, int pageCount = 20)
    {
        Assert.True(PageRangeText.TryParse(text, pageCount, out IReadOnlyList<int> pages, out string error), error);
        Assert.Null(error);
        return pages.ToArray();
    }

    private static string Refusal(string text, int pageCount = 20)
    {
        Assert.False(PageRangeText.TryParse(text, pageCount, out IReadOnlyList<int> pages, out string error));
        Assert.Empty(pages);
        Assert.NotNull(error);
        return error;
    }

    [Theory]
    [InlineData("1-3, 7", new[] { 1, 2, 3, 7 })]
    [InlineData("7, 1-3", new[] { 1, 2, 3, 7 })]          // ascending whatever the order typed
    [InlineData("2,2,2-3", new[] { 2, 3 })]               // de-duplicated
    [InlineData("12-9", new[] { 9, 10, 11, 12 })]         // a range typed backwards
    [InlineData("-3", new[] { 1, 2, 3 })]                 // open start
    [InlineData("18-", new[] { 18, 19, 20 })]             // open end
    [InlineData("1;4", new[] { 1, 4 })]                   // the semicolon separates too
    [InlineData("1,,2, ", new[] { 1, 2 })]                // mid-keystroke commas
    [InlineData(" 20 ", new[] { 20 })]                    // the last page
    public void Parses_the_print_dialog_grammar(string text, int[] expected)
        => Assert.Equal(expected, Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_text_is_a_success_with_no_pages(string text)
        => Assert.Empty(Parse(text));

    [Theory]
    [InlineData("0", "out of range")]
    [InlineData("21", "out of range")]
    [InlineData("1-21", "out of range")]
    [InlineData("+3", "not a page number")]
    [InlineData("3.5", "not a page number")]
    [InlineData("1,000", "out of range")]                 // "000" is page 0, never a thousands separator
    [InlineData("1-2-3", "more than one '-'")]
    [InlineData("-", "does not name a range")]
    [InlineData("a", "not a page number")]
    public void Refuses_what_cannot_mean_a_page_with_the_reason(string text, string reason)
        => Assert.Contains(reason, Refusal(text), StringComparison.Ordinal);

    [Fact]
    public void A_document_with_no_pages_refuses_any_page()
        => Assert.Contains("no pages", Refusal("1", pageCount: 0), StringComparison.Ordinal);

    [Theory]
    [InlineData(new[] { 1, 2, 3, 7 }, "1-3, 7")]
    [InlineData(new[] { 5 }, "5")]
    [InlineData(new[] { 1, 3, 5 }, "1, 3, 5")]
    [InlineData(new[] { 4, 5 }, "4-5")]
    [InlineData(new int[0], "")]
    public void Formats_runs_of_two_or_more_as_ranges(int[] pages, string expected)
        => Assert.Equal(expected, PageRangeText.Format(pages));

    [Theory]
    [InlineData("1-3, 7, 9-12, 20")]
    [InlineData("2, 4, 6")]
    [InlineData("1-20")]
    public void Format_reads_back_through_TryParse(string text)
    {
        int[] pages = Parse(text);
        Assert.Equal(pages, Parse(PageRangeText.Format(pages)));
    }

    [Fact]
    public void Groups_keep_each_part_in_the_order_typed()
    {
        Assert.True(PageRangeText.TryParseGroups("9-, 1-3, 3", 10, out var groups, out string error), error);
        Assert.Equal(3, groups.Count);
        Assert.Equal(new[] { 9, 10 }, groups[0]);
        Assert.Equal(new[] { 1, 2, 3 }, groups[1]);
        Assert.Equal(new[] { 3 }, groups[2]);              // a page may be in two groups
    }

    [Fact]
    public void Groups_refuse_as_the_union_does()
    {
        Assert.False(PageRangeText.TryParseGroups("1-3, 11", 10, out var groups, out string error));
        Assert.Contains("out of range", error, StringComparison.Ordinal);
        Assert.False(PageRangeText.TryParse("1-3, 11", 10, out _, out string unionError));
        Assert.Equal(error, unionError);
    }
}
