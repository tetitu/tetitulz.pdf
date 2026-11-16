// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using System.Xml.Linq;
using Tetitulz.Pdf.HtmlToPdf.Markdown;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// <see cref="MarkdownToHtml"/> — the Markdown front-end onto the existing HTML pipeline.
/// </summary>
/// <remarks>
/// <para><b>Assertions parse the output as XML and read elements</b>, never substring-match it. This
/// repository's SVG suite substring-matched for its entire history and so missed an export that was
/// not even well-formed; the same trap applies here, and a converter's output being parseable is
/// half of what it promises.</para>
///
/// <para><b>The escaping cases are the security-relevant ones.</b> A Markdown document is content;
/// a converter that passes <c>&lt;script&gt;</c> through is an injection vector into whatever
/// consumes the HTML.</para>
/// </remarks>
public class MarkdownToHtmlTests
{
    [Theory]
    [InlineData("# One", "h1", "One")]
    [InlineData("## Two", "h2", "Two")]
    [InlineData("###### Six", "h6", "Six")]
    public void AtxHeadings_BecomeHeadingElements(string markdown, string element, string text)
    {
        var e = Assert.Single(Elements(markdown, element));
        Assert.Equal(text, e.Value);
    }

    /// <summary>Seven hashes is not a heading — §4.2 caps ATX at six.</summary>
    [Fact]
    public void SevenHashes_IsNotAHeading()
    {
        Assert.Empty(Elements("####### Seven", "h1"));
        Assert.Single(Elements("####### Seven", "p"));
    }

    /// <summary>
    /// A wrapped source paragraph is <b>one</b> paragraph. Treating each line as its own would give
    /// a caller a document with a paragraph break after every 80 columns.
    /// </summary>
    [Fact]
    public void WrappedLines_JoinIntoOneParagraph()
    {
        var p = Assert.Single(Elements("one two\nthree four", "p"));
        Assert.Equal("one two three four", p.Value);
    }

    [Fact]
    public void ABlankLineSeparatesParagraphs()
    {
        Assert.Equal(2, Elements("first\n\nsecond", "p").Count());
    }

    // ------------------------------------------------------------------
    //  Inline
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("**bold**", "strong", "bold")]
    [InlineData("__bold__", "strong", "bold")]
    [InlineData("*italic*", "em", "italic")]
    [InlineData("_italic_", "em", "italic")]
    public void Emphasis_BecomesTheRightElement(string markdown, string element, string text)
    {
        var e = Assert.Single(Elements(markdown, element));
        Assert.Equal(text, e.Value);
    }

    [Fact]
    public void ALink_BecomesAnAnchorWithItsHref()
    {
        var a = Assert.Single(Elements("see [the docs](https://example.com/x)", "a"));
        Assert.Equal("https://example.com/x", a.Attribute("href")!.Value);
        Assert.Equal("the docs", a.Value);
    }

    [Fact]
    public void AnImage_BecomesAnImgWithSrcAndAlt()
    {
        var img = Assert.Single(Elements("![a cat](cat.png)", "img"));
        Assert.Equal("cat.png", img.Attribute("src")!.Value);
        Assert.Equal("a cat", img.Attribute("alt")!.Value);
    }

    /// <summary>
    /// <b>A code span's contents are literal.</b> Underscores inside one must not italicise, which is
    /// why code is extracted before the emphasis rules run rather than after.
    /// </summary>
    [Fact]
    public void ACodeSpan_IsLiteralAndSuppressesEmphasis()
    {
        string html = MarkdownToHtml.Convert("use `a_b_c` here");

        var code = Assert.Single(Elements(html, "code", alreadyHtml: true));
        Assert.Equal("a_b_c", code.Value);
        Assert.Empty(Elements(html, "em", alreadyHtml: true));
    }

    /// <summary>
    /// <b>Markdown is content, not markup.</b> A converter that passes an element through is an
    /// injection vector into whatever consumes the HTML.
    /// </summary>
    [Fact]
    public void HtmlInTheSource_IsEscapedRatherThanPassedThrough()
    {
        string html = MarkdownToHtml.Convert("a <script>alert(1)</script> b");

        Assert.Empty(Elements(html, "script", alreadyHtml: true));
        Assert.Contains("<script>", Assert.Single(Elements(html, "p", alreadyHtml: true)).Value);
    }

    // ------------------------------------------------------------------
    //  Blocks
    // ------------------------------------------------------------------

    [Fact]
    public void AnUnorderedList_BecomesUlWithItems()
    {
        string html = MarkdownToHtml.Convert("- one\n- two\n- three");

        Assert.Single(Elements(html, "ul", alreadyHtml: true));
        Assert.Equal(new[] { "one", "two", "three" },
                     Elements(html, "li", alreadyHtml: true).Select(li => li.Value));
    }

    [Fact]
    public void AnOrderedList_BecomesOl()
    {
        string html = MarkdownToHtml.Convert("1. one\n2. two");

        Assert.Single(Elements(html, "ol", alreadyHtml: true));
        Assert.Empty(Elements(html, "ul", alreadyHtml: true));
    }

    /// <summary>
    /// Switching marker kind at the same depth closes one list and opens the other — continuing the
    /// wrong element silently renumbers the caller's list.
    /// </summary>
    [Fact]
    public void SwitchingListKind_StartsANewList()
    {
        string html = MarkdownToHtml.Convert("- bullet\n1. number");

        Assert.Single(Elements(html, "ul", alreadyHtml: true));
        Assert.Single(Elements(html, "ol", alreadyHtml: true));
    }

    /// <summary>An indented item nests, rather than becoming a sibling.</summary>
    [Fact]
    public void AnIndentedItem_Nests()
    {
        string html = MarkdownToHtml.Convert("- outer\n  - inner");

        var lists = Elements(html, "ul", alreadyHtml: true).ToArray();
        Assert.Equal(2, lists.Length);
        Assert.Contains(lists, l => l.Ancestors().Any(a => a.Name.LocalName == "ul"));
    }

    /// <summary>
    /// A fenced block is literal to the closing fence: its contents are neither escaped away nor
    /// interpreted as Markdown.
    /// </summary>
    [Fact]
    public void AFencedCodeBlock_IsLiteral()
    {
        string html = MarkdownToHtml.Convert("```\n# not a heading\n**not bold**\n```");

        var code = Assert.Single(Elements(html, "code", alreadyHtml: true));
        Assert.Contains("# not a heading", code.Value);
        Assert.Contains("**not bold**", code.Value);
        Assert.Empty(Elements(html, "h1", alreadyHtml: true));
        Assert.Empty(Elements(html, "strong", alreadyHtml: true));
    }

    [Fact]
    public void AFenceLanguage_BecomesAClass()
    {
        var code = Assert.Single(Elements("```csharp\nvar x = 1;\n```", "code"));
        Assert.Equal("language-csharp", code.Attribute("class")!.Value);
    }

    [Fact]
    public void AThematicBreak_BecomesAnHr()
    {
        Assert.Single(Elements("above\n\n---\n\nbelow", "hr"));
    }

    /// <summary>A quoted heading is still a heading, not quoted literal text.</summary>
    [Fact]
    public void ABlockquote_KeepsTheStructureInsideIt()
    {
        string html = MarkdownToHtml.Convert("> ## Quoted heading");

        Assert.Single(Elements(html, "blockquote", alreadyHtml: true));
        var h2 = Assert.Single(Elements(html, "h2", alreadyHtml: true));
        Assert.Equal("Quoted heading", h2.Value);
    }

    // ------------------------------------------------------------------
    //  Tables
    // ------------------------------------------------------------------

    [Fact]
    public void AGfmTable_BecomesATableWithHeaderAndBody()
    {
        string html = MarkdownToHtml.Convert("| a | b |\n| --- | --- |\n| 1 | 2 |\n| 3 | 4 |");

        Assert.Single(Elements(html, "table", alreadyHtml: true));
        Assert.Equal(new[] { "a", "b" }, Elements(html, "th", alreadyHtml: true).Select(e => e.Value));
        Assert.Equal(new[] { "1", "2", "3", "4" },
                     Elements(html, "td", alreadyHtml: true).Select(e => e.Value));
    }

    /// <summary>
    /// <b>The delimiter row is required, not inferred.</b> Without it, prose containing a pipe
    /// becomes a table — which is why it is what distinguishes the two.
    /// </summary>
    /// <remarks>
    /// <b>Two lines, deliberately.</b> The first draft used one, and a mutation deleting the
    /// delimiter check still passed — a single line fails the "is there a row after this one?" guard
    /// before the delimiter is ever consulted, so the test was passing for the wrong reason. Two
    /// pipe-bearing lines are what actually reach the check.
    /// </remarks>
    [Fact]
    public void ProseContainingAPipe_IsNotATable()
    {
        string html = MarkdownToHtml.Convert("choose a | b for the option\nor c | d instead");

        Assert.Empty(Elements(html, "table", alreadyHtml: true));
        Assert.Single(Elements(html, "p", alreadyHtml: true));
    }

    // ------------------------------------------------------------------

    /// <summary>Empty input is a valid, empty document rather than a crash.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmptyInput_IsAValidDocument(string markdown)
    {
        var doc = XDocument.Parse(MarkdownToHtml.Convert(markdown));
        Assert.Equal("html", doc.Root!.Name.LocalName);
    }

    /// <summary>The title reaches the head when given, and is absent when not.</summary>
    [Fact]
    public void TheTitleIsWrittenOnlyWhenSupplied()
    {
        Assert.Equal("My doc", Assert.Single(Elements("# x", "title", title: "My doc")).Value);
        Assert.Empty(Elements("# x", "title"));
    }

    // ------------------------------------------------------------------

    private static System.Collections.Generic.IEnumerable<XElement> Elements(
        string source, string localName, bool alreadyHtml = false, string title = null)
    {
        string html = alreadyHtml ? source : MarkdownToHtml.Convert(source, title);
        return XDocument.Parse(html).Descendants().Where(e => e.Name.LocalName == localName);
    }
}
