// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.HtmlToPdf;
using Tetitulz.Pdf.HtmlToPdf.Markdown;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// The patterns the mapper and the Markdown reader ran over a document's text without a match timeout
/// are scans now — a regular expression over a field the file controls carries a timeout, and one
/// that is not a regular expression needs none. Each scan is held to the pattern it replaced, kept
/// here as the characterisation, over the edge cases and over a long run.
/// </summary>
public class HandScannedPatternTests
{
    private static readonly Regex ListItem = new(@"^(?<indent>[ \t]*)(?:(?<bullet>[-*+])|(?<number>\d{1,9})[.)])\s+(?<text>.*)$");
    private static readonly Regex Important = new(@"!\s*important\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex MediaKeyword = new("^-?[a-zA-Z_][a-zA-Z0-9_-]*$");
    private static readonly Regex BlankStrings = new(@"^(\s*(""\s*""|'\s*'))+\s*$");
    private static readonly Regex ViewBox = new(@"viewBox\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Spaces = new(@"[^\S ]+");

    [Theory]
    [InlineData("- item")]
    [InlineData("  * nested")]
    [InlineData("\t+ tabbed")]
    [InlineData("1. first")]
    [InlineData("123456789) nine digits")]
    [InlineData("1234567890. ten digits")]
    [InlineData("-no space")]
    [InlineData("1.no space")]
    [InlineData("- ")]
    [InlineData("-   \t  ")]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData("١. Arabic-Indic digit")]
    [InlineData("  12) two levels")]
    public void TheListItemScan_AgreesWithThePattern(string line)
    {
        bool scanned = MarkdownToHtml.TryListItem(line, out bool ordered, out int depth, out string text);
        Match m = ListItem.Match(line);

        Assert.Equal(m.Success, scanned);
        if (!m.Success) return;
        Assert.Equal(m.Groups["number"].Success, ordered);
        Assert.Equal(m.Groups["indent"].Value.Replace("\t", "    ").Length / 2, depth);
        Assert.Equal(m.Groups["text"].Value, text);
    }

    [Theory]
    [InlineData("red !important", 4)]
    [InlineData("red!IMPORTANT", 3)]
    [InlineData("red !   important", 4)]
    [InlineData("red important", -1)]
    [InlineData("!important", 0)]
    [InlineData("red !importan", -1)]
    [InlineData("important", -1)]
    [InlineData("a !b !important", 5)]
    public void TheImportantScan_AgreesWithThePattern(string value, int expected)
    {
        Match m = Important.Match(value);
        Assert.Equal(m.Success ? m.Index : -1, expected);
        Assert.Equal(expected, HtmlToBuilderMapper.ImportantSuffixStart(value));
    }

    [Theory]
    [InlineData("p3")] [InlineData("-webkit-x")] [InlineData("_a")] [InlineData("3")] [InlineData("-")]
    [InlineData("a b")] [InlineData("")] [InlineData("srgb")] [InlineData("aé")] [InlineData("--x")]
    public void TheMediaKeywordScan_AgreesWithThePattern(string value)
        => Assert.Equal(MediaKeyword.IsMatch(value), HtmlToBuilderMapper.IsMediaKeyword(value));

    [Theory]
    [InlineData("\"\"")] [InlineData("''")] [InlineData(" \"  \" '' ")] [InlineData("\"x\"")]
    [InlineData("")] [InlineData("  ")] [InlineData("\"'")] [InlineData("\" ")] [InlineData("'' \"\"x")]
    public void TheBlankStringsScan_AgreesWithThePattern(string content)
        => Assert.Equal(BlankStrings.IsMatch(content), HtmlToBuilderMapper.IsOnlyBlankStrings(content));

    [Theory]
    [InlineData("<svg viewBox=\"0 0 10 20\">")]
    [InlineData("<svg VIEWBOX = '0 0 5 5'>")]
    [InlineData("<svg viewBox=\"\" viewBox=\"1 2 3 4\">")]
    [InlineData("<svg viewbox>text viewBox=\"0 0 1 1'>")]
    [InlineData("<svg width=\"3\">")]
    [InlineData("<svg viewBox=\"0 0 1 1")]
    public void TheViewBoxScan_AgreesWithThePattern(string markup)
    {
        Match m = ViewBox.Match(markup);
        Assert.Equal(m.Success ? m.Groups[1].Value : null, HtmlToBuilderMapper.ViewBoxAttribute(markup));
    }

    [Theory]
    [InlineData("a  b")] [InlineData(" \t\n x   y  z ")] [InlineData("")] [InlineData("  q")]
    public void TheSpaceCollapse_AgreesWithThePattern(string raw)
        => Assert.Equal(Spaces.Replace(raw, " "), HtmlToBuilderMapper.CollapseSpaces(raw));

    private static readonly Regex MediaRange = new(
        @"^\s*(?<a>[^<>=]+?)\s*(?<op1><=|>=|<|>|=)\s*(?<b>[^<>=]+?)\s*(?:(?<op2><=|>=|<|>|=)\s*(?<c>[^<>=]+?)\s*)?$",
        RegexOptions.CultureInvariant);
    private static readonly Regex UrlScheme = new(@"^[A-Za-z][A-Za-z0-9+.\-]+:");

    [Theory]
    [InlineData("width >= 600px")]
    [InlineData("400px < width <= 700px")]
    [InlineData("width=600px")]
    [InlineData("  width  <  10px  ")]
    [InlineData("width => 1px")]
    [InlineData("a < b < c < d")]
    [InlineData("< 5px")]
    [InlineData("width <")]
    [InlineData("   < 5px")]
    [InlineData("a <= = b")]
    [InlineData("width")]
    [InlineData("a\nb > c")]
    public void TheMediaRangeParse_AgreesWithThePattern(string feature)
    {
        Match m = MediaRange.Match(feature);
        bool parsed = HtmlToBuilderMapper.TryMediaRange(feature, out string a, out string op1, out string b, out string? op2, out string? c);

        Assert.Equal(m.Success, parsed);
        if (!m.Success) return;
        Assert.Equal(m.Groups["a"].Value, a);
        Assert.Equal(m.Groups["op1"].Value, op1);
        Assert.Equal(m.Groups["b"].Value, b);
        Assert.Equal(m.Groups["op2"].Success ? m.Groups["op2"].Value : null, op2);
        Assert.Equal(m.Groups["c"].Success ? m.Groups["c"].Value : null, c);
    }

    [Theory]
    [InlineData("https://x")] [InlineData("data:image/png")] [InlineData("C:x")] [InlineData("/img/x.png")]
    [InlineData("a+b.c-d:")] [InlineData("ab")] [InlineData("1a:")] [InlineData("ab:")] [InlineData("a:")]
    public void TheUrlSchemeScan_AgreesWithThePattern(string url)
        => Assert.Equal(UrlScheme.IsMatch(url), HtmlToBuilderMapper.HasUrlScheme(url));

    /// <summary>
    /// The range syntax over runs of spaces between three operators: the pattern it replaced did not
    /// finish this in ten minutes at a hundred spaces; the parse reads it once and refuses it.
    /// </summary>
    [Fact]
    public void AdversarialMediaRangeWithSpaces_IsParsedInOnePass()
    {
        string s = new string(' ', 100_000);
        Assert.False(HtmlToBuilderMapper.TryMediaRange("x" + s + "<" + s + "y" + s + "<" + s + "z" + s + "<", out _, out _, out _, out _, out _));
        Assert.True(HtmlToBuilderMapper.TryMediaRange("x" + s + "<" + s + "y", out string a, out _, out string b, out _, out _));
        Assert.Equal(("x", "y"), (a, b));
    }

    /// <summary>The stylesheet <c>url()</c> pattern runs over a remote sheet, so it carries a match timeout.</summary>
    [Fact]
    public void TheCssUrlPatternCarriesAMatchTimeout()
        => Assert.NotEqual(Regex.InfiniteMatchTimeout, HtmlToBuilderMapper.CssUrl.MatchTimeout);

    /// <summary>A million characters of each scan's worst shape is read in one pass, the answer the pattern's.</summary>
    [Fact]
    public void AdversarialLongRuns_AreScannedInOnePass()
    {
        string spaces = new string(' ', 1_000_000);
        Assert.False(MarkdownToHtml.TryListItem(spaces + "x", out _, out _, out _));
        Assert.Equal(-1, HtmlToBuilderMapper.ImportantSuffixStart("!" + spaces + "importan"));
        Assert.False(HtmlToBuilderMapper.IsOnlyBlankStrings(string.Concat(System.Linq.Enumerable.Repeat("\"\" ", 300_000)) + "x"));
        Assert.Null(HtmlToBuilderMapper.ViewBoxAttribute(string.Concat(System.Linq.Enumerable.Repeat("viewBox= ", 100_000))));
        Assert.Equal(" x ", HtmlToBuilderMapper.CollapseSpaces(spaces + "x" + spaces));
    }
}
