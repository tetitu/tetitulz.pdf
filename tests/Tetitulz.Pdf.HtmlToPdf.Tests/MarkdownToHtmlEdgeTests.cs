// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.HtmlToPdf.Markdown;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// <see cref="MarkdownToHtml"/> at its edges: the text around an element, the lines after a table,
/// the closing tag of an ordered list, a delimiter row
/// without a leading pipe, an empty title, and the line cap.
/// </summary>
public class MarkdownToHtmlEdgeTests
{
    private static XElement Body(string markdown, string title = null)
        => XDocument.Parse(MarkdownToHtml.Convert(markdown, title)).Root!.Element("body")!;

    /// <summary>
    /// Emphasis wraps its text and adds nothing after it: the paragraph reads exactly as the
    /// source does without the asterisks.
    /// </summary>
    [Fact]
    public void EmphasisAddsNothingAroundItsText()
    {
        var p = Body("**bold** and *it*").Element("p")!;

        Assert.Equal("bold and it", p.Value);
        Assert.Equal("bold", p.Element("strong")!.Value);
    }

    /// <summary>The lines after a table are read: the table consumes its rows and no more.</summary>
    [Fact]
    public void TheLineAfterATableIsKept()
    {
        var body = Body("| a | b |\n| --- | --- |\n| 1 | 2 |\nafter");

        Assert.Single(body.Descendants("tbody").Single().Elements("tr"));
        Assert.Equal("after", body.Element("p")!.Value);
    }

    /// <summary>A table further down the document is measured from where it starts.</summary>
    [Fact]
    public void ATableAfterAParagraphIsMeasuredFromItsOwnStart()
    {
        var body = Body("intro\n\n| a | b |\n| --- | --- |\n| 1 | 2 |\n| 3 | 4 |\n\nafter");

        Assert.Equal(2, body.Descendants("tbody").Single().Elements("tr").Count());
        Assert.Equal(new[] { "intro", "after" }, body.Elements("p").Select(p => p.Value));
    }

    /// <summary>An ordered list followed by a paragraph is closed with <c>&lt;/ol&gt;</c>.</summary>
    [Fact]
    public void AnOrderedListIsClosedAsOne()
    {
        var body = Body("1. a\n2. b\n\npara");

        Assert.Equal(2, body.Element("ol")!.Elements("li").Count());
        Assert.Equal("para", body.Element("p")!.Value);
    }

    /// <summary>
    /// An ordered list nested in an unordered one is closed as an ordered list when the next item
    /// returns to the outer level.
    /// </summary>
    [Fact]
    public void ANestedOrderedListIsClosedAsOneOnReturningToTheOuterLevel()
    {
        var ul = Body("- a\n  1. x\n  2. y\n- b").Element("ul")!;

        Assert.Equal(2, ul.Elements("li").Count());
        Assert.Equal(2, ul.Descendants("ol").Single().Elements("li").Count());
    }

    /// <summary>A delimiter row that starts with its dashes, no leading pipe, still makes a table.</summary>
    [Fact]
    public void ADelimiterRowWithoutALeadingPipeMakesATable()
    {
        var body = Body("| a | b |\n--- | ---\n| 1 | 2 |");

        Assert.Single(body.Descendants("table"));
    }

    /// <summary>An empty title writes no <c>&lt;title&gt;</c>, as no title does.</summary>
    [Fact]
    public void AnEmptyTitleWritesNoTitleElement()
    {
        var head = XDocument.Parse(MarkdownToHtml.Convert("# x", "")).Root!.Element("head")!;
        Assert.Null(head.Element("title"));
    }

    /// <summary>
    /// The source is read up to <see cref="MarkdownToHtml.MaxLines"/> lines: a line just inside the
    /// cap is in the document, the one just past it is not.
    /// </summary>
    [Fact]
    public void LinesPastTheCapAreNotRead()
    {
        var inside = new StringBuilder();
        for (int i = 0; i < MarkdownToHtml.MaxLines - 1; i++) inside.Append("\n");
        Assert.Contains("INSIDE", MarkdownToHtml.Convert(inside + "INSIDE"));

        var past = new StringBuilder();
        for (int i = 0; i < MarkdownToHtml.MaxLines; i++) past.Append("\n");
        Assert.DoesNotContain("PAST", MarkdownToHtml.Convert(past + "PAST"));
    }
}
