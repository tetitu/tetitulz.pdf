// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Builder.Import;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// XFA rich text (XFA 3.3 chapter 27): <see cref="XfaRichText.Read"/> reads an <c>exData</c>'s XHTML into
/// paragraphs of styled runs — the elements and CSS properties the chapter lists, whitespace collapsed
/// unless <c>xfa-spacerun</c> keeps it, links kept for http, https and mailto alone, what it does not
/// apply named — and the layout draws them, placed (the paragraphs after the first under it), flowed, in
/// a table cell and as a caption.
/// </summary>
public class XfaRichTextTests
{
    private const string Xhtml = "http://www.w3.org/1999/xhtml";

    private static List<XfaRichParagraph> Read(string body, ISet<string>? unapplied = null)
        => XfaRichText.Read(XElement.Parse($"<exData contentType=\"text/html\"><body xmlns=\"{Xhtml}\">{body}</body></exData>"),
                            unapplied ?? new HashSet<string>());

    [Fact]
    public void Paragraphs_TakeTheirStyles_AndRunsTheirs()
    {
        var unapplied = new HashSet<string>();
        var paragraphs = Read("<p style=\"text-align:center;margin-top:6pt\">Hello <b>bold</b> <span style=\"font-size:14pt;color:#FF0000;font-family:'Times New Roman', serif\">big</span><br/>"
                              + "next <i>it</i><sup>2</sup><sub>3</sub> <span style=\"text-decoration:underline\">u</span> <span style=\"text-decoration:line-through\">s</span> <u>notU</u> "
                              + "<span style=\"letter-spacing:2pt\">wide</span></p>"
                              + "<p style=\"margin-left:0.5in;text-indent:-10pt;line-height:14pt;margin-bottom:3pt\">second   \n para</p>", unapplied);
        Assert.Empty(unapplied);
        Assert.Equal(2, paragraphs.Count);

        XfaRichParagraph first = paragraphs[0];
        Assert.Equal(("center", 6.0), (first.HAlign, first.SpaceAbove!.Value));
        XfaRichRun Run(string text) => first.Runs.Single(r => r.Text == text);
        Assert.Equal("Hello ", first.Runs[0].Text);
        Assert.Null(first.Runs[0].Bold);
        Assert.True(Run("bold").Bold);
        Assert.Equal((14.0, "#FF0000", "Times New Roman"), (Run("big").Size!.Value, Run("big").Color, Run("big").Family));
        Assert.True(first.Runs.Single(r => r.LineBreak).LineBreak);
        Assert.True(Run("it").Italic);
        Assert.Equal((1, -1), (Run("2").Shift, Run("3").Shift));
        Assert.True(Run("u").Underline);
        Assert.Null(Run("notU").Underline);                                  // chapter 27: u and s "must not be supported"
        Assert.True(Run("s").Strike);
        Assert.Equal(2, Run("wide").LetterSpacing!.Value, 6);
        Assert.Equal("next ", first.Runs[first.Runs.FindIndex(r => r.LineBreak) + 1].Text);   // no space after the break

        // Text after a closed paragraph is a paragraph of its own, and a paragraph's trailing space is no part of it.
        var loose = Read("<p>a </p>tail");
        Assert.Equal(new[] { "a", "tail" }, loose.Select(q => string.Concat(q.Runs.Select(r => r.Text))));

        XfaRichParagraph second = paragraphs[1];
        Assert.Equal((36.0, -10.0, 14.0, 3.0), (second.MarginLeft!.Value, second.TextIndent!.Value, second.LineHeight!.Value, second.SpaceBelow!.Value));
        Assert.Equal("second para", string.Concat(second.Runs.Select(r => r.Text)));          // whitespace collapsed, trimmed
        Assert.Null(second.HAlign);
    }

    [Fact]
    public void SpaceRun_KeepsTheSpaces_AndWeightsAndShorthandsRead()
    {
        var keep = Read("<p>a <span style=\"xfa-spacerun:yes\">b   c</span></p>")[0];
        Assert.Equal("a b   c", string.Concat(keep.Runs.Select(r => r.Text)));

        var weights = Read("<p><b>x<span style=\"font-weight:400\">y</span><span style=\"font-weight:700\">z</span></b><span style=\"font-weight:bold\">w</span></p>")[0];
        Assert.Equal(new bool?[] { true, false, true, true }, weights.Runs.Select(r => r.Bold).ToArray());

        var margins = Read("<p style=\"margin:1pt 2pt 3pt 4pt\">m</p><p style=\"margin:5pt\">n</p><p style=\"margin:6pt 7pt\">o</p>");
        Assert.Equal((1.0, 2.0, 3.0, 4.0), (margins[0].SpaceAbove!.Value, margins[0].MarginRight!.Value, margins[0].SpaceBelow!.Value, margins[0].MarginLeft!.Value));
        Assert.Equal((5.0, 5.0, 5.0, 5.0), (margins[1].SpaceAbove!.Value, margins[1].MarginRight!.Value, margins[1].SpaceBelow!.Value, margins[1].MarginLeft!.Value));
        Assert.Equal((6.0, 7.0, 6.0, 7.0), (margins[2].SpaceAbove!.Value, margins[2].MarginRight!.Value, margins[2].SpaceBelow!.Value, margins[2].MarginLeft!.Value));

        var styled = Read("<p><span style=\"vertical-align:super\">a</span><span style=\"vertical-align:sub\">b</span><i><span style=\"font-style:normal\">c</span></i>"
                          + "<u><span style=\"text-decoration:none\">d</span></u><span style=\"text-decoration:word underline\">e</span></p>")[0];
        Assert.Equal(new[] { 1, -1 }, styled.Runs.Take(2).Select(r => r.Shift));
        Assert.False(styled.Runs[2].Italic);
        Assert.False(styled.Runs[3].Underline);
        Assert.True(styled.Runs[4].Underline);
    }

    /// <summary>A link is kept for http, https and mailto alone: a <c>javascript:</c> or relative target is its text.</summary>
    [Fact]
    public void Links_AreKeptForWebAndMailOnly()
    {
        var runs = Read("<p><a href=\"https://x.example/a\">web</a> <a href=\"mailto:a@x.example\">mail</a> <a href=\"javascript:alert(1)\">script</a> <a href=\"page.html\">relative</a></p>")[0].Runs;
        Assert.Equal("https://x.example/a", runs.Single(r => r.Text == "web").Link);
        Assert.Equal("mailto:a@x.example", runs.Single(r => r.Text == "mail").Link);
        Assert.Null(runs.Single(r => r.Text == "script").Link);
        Assert.Null(runs.Single(r => r.Text == "relative").Link);
    }

    [Fact]
    public void WhatIsNotApplied_IsNamed_AndAHeadIsNoContent()
    {
        var unapplied = new HashSet<string>();
        var p = Read("<p style=\"tab-stops:left 1in;text-align:middle\"><span style=\"font-size:120%;color:nonsense;text-shadow:1px 1px;xfa-font-vertical-scale:wide\">x</span></p>", unapplied)[0];
        Assert.Equal(new[] { "color: nonsense", "font-size: 120%", "text-align: middle", "text-shadow", "xfa-font-vertical-scale: wide" }, unapplied.OrderBy(s => s, StringComparer.Ordinal));
        Assert.Null(p.Runs[0].VerticalScale);
        Assert.Null(p.Runs[0].Size);
        Assert.Null(p.Runs[0].Color);
        Assert.Null(p.HAlign);

        var page = XfaRichText.Read(XElement.Parse($"<exData><html xmlns=\"{Xhtml}\"><head><title>T</title><style>p{{}}</style></head><body><p>x</p></body></html></exData>"), new HashSet<string>());
        Assert.Equal("x", string.Concat(page.SelectMany(q => q.Runs).Select(r => r.Text)));
    }

    /// <summary>
    /// The file's numbers are bounded: a style attribute past its cap is not read, and said; nesting past
    /// the depth cap is not followed, and said — the shallower text kept.
    /// </summary>
    [Fact]
    public void HostileRichText_IsBoundedAndSaysSo()
    {
        var unapplied = new HashSet<string>();
        var big = Read($"<p><span style=\"font-weight:bold;{new string(' ', XfaRichText.MaxStyleLength)}\">x</span></p>", unapplied)[0];
        Assert.Null(big.Runs[0].Bold);
        Assert.Contains(unapplied, u => u.StartsWith("a style attribute longer than", StringComparison.Ordinal));
        Assert.True(Read("<p><span style=\"font-weight:bold\">x</span></p>")[0].Runs[0].Bold);   // the control

        var deep = new HashSet<string>();
        var nested = new StringBuilder("<p>top");
        for (int i = 0; i < XfaRichText.MaxDepth + 10; i++) nested.Append("<span>");
        nested.Append("bottom");
        for (int i = 0; i < XfaRichText.MaxDepth + 10; i++) nested.Append("</span>");
        nested.Append("</p>");
        var text = string.Concat(Read(nested.ToString(), deep).SelectMany(q => q.Runs).Select(r => r.Text));
        Assert.Equal("top", text);
        Assert.Contains(deep, u => u.StartsWith("rich text nested past", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the layout

    private const string Ns = "http://www.xfa.org/schema/xfa-template/3.3/";

    private static XfaLayoutResult Layout(string body) =>
        XfaLayout.Layout(XfaTemplate.Parse(Encoding.UTF8.GetBytes(
            $"<template xmlns=\"{Ns}\"><subform name=\"form1\" layout=\"tb\"><pageSet><pageArea name=\"P\"><contentArea x=\"0.25in\" y=\"0.25in\" w=\"576pt\" h=\"756pt\"/>"
            + "<medium stock=\"letter\" short=\"612pt\" long=\"792pt\"/></pageArea></pageSet>" + body + "</subform></template>"))!, null);

    private static string Rich(string inner) => $"<exData contentType=\"text/html\"><body xmlns=\"{Xhtml}\">{inner}</body></exData>";

    private const string TwoParagraphs = "<p style=\"text-align:right\">One <b>bold</b></p><p>Two <span style=\"font-size:20pt\">big</span></p>";

    [Fact]
    public void APlacedDraw_IsItsParagraphs_TheRestUnderTheFirst()
    {
        var result = Layout("<subform w=\"576pt\" h=\"756pt\"><draw name=\"d\" x=\"1in\" y=\"1in\" w=\"3in\" h=\"1in\"><font typeface=\"Arial\" size=\"12pt\"/>"
                            + "<para vAlign=\"middle\"/><value>" + Rich(TwoParagraphs) + "</value></draw></subform>");
        Paragraph first = result.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>().Single(p => p.Runs.OfType<TextRun>().Any(r => r.Text == "bold"));
        Assert.Equal(TextAlignment.Right, first.Alignment);
        TextRun bold = first.Runs.OfType<TextRun>().Single(r => r.Text == "bold");
        Assert.True(bold.IsBold);
        Assert.Equal(12, bold.FontSize, 3);                                       // the container's size where the markup says none
        Assert.False(first.Runs.OfType<TextRun>().Single(r => r.Text == "One ").IsBold);

        Paragraph second = Assert.Single(first.Placement!.Following);
        Assert.Equal(TextAlignment.Left, second.Alignment);
        Assert.Equal(20, second.Runs.OfType<TextRun>().Single(r => r.Text == "big").FontSize, 3);
        Assert.Equal((72.0, CellVerticalAlign.Center), (first.Placement.BandHeightPt!.Value, first.Placement.BandAlign));
        // The second is in no list of its own: only under the first.
        Assert.DoesNotContain(result.Document.Sections.SelectMany(s => s.Children), b => ReferenceEquals(b, second));
        Assert.DoesNotContain(result.Approximations, a => a.StartsWith("text formatting", StringComparison.Ordinal));
    }

    [Fact]
    public void AFlowedDraw_ATableCell_AndACaption_TakeTheirParagraphs()
    {
        var flowed = Layout("<subform layout=\"tb\" w=\"576pt\"><draw name=\"d\" w=\"3in\" h=\"1in\"><value>" + Rich(TwoParagraphs) + "</value></draw></subform>");
        var blocks = flowed.Document.Sections.SelectMany(s => s.Children).ToList();
        int one = blocks.FindIndex(b => b is Paragraph p && p.Runs.OfType<TextRun>().Any(r => r.Text == "bold"));
        int two = blocks.FindIndex(b => b is Paragraph p && p.Runs.OfType<TextRun>().Any(r => r.Text == "big"));
        Assert.Equal(one + 1, two);
        Assert.IsType<BoxEnd>(blocks[two + 1]);                                  // both inside the draw's box

        var table = Layout("<subform layout=\"tb\" w=\"576pt\"><subform layout=\"table\" w=\"4in\"><subform layout=\"row\"><draw name=\"c\" w=\"2in\" h=\"30pt\"><value>"
                           + Rich(TwoParagraphs) + "</value></draw></subform></subform></subform>");
        TableCell cell = table.Document.Sections.SelectMany(s => s.Children).OfType<Table>().Single().Rows[0].Cells[0];
        Assert.Equal(2, cell.Paragraphs.Count);
        Assert.Equal(TextAlignment.Right, cell.Paragraphs[0].Alignment);

        var caption = Layout("<subform w=\"576pt\" h=\"756pt\"><field name=\"f\" x=\"1in\" y=\"1in\" w=\"4in\" h=\"1in\"><ui><textEdit/></ui><caption reserve=\"2in\"><value>"
                             + Rich("<p><b>Name</b></p>") + "</value></caption><value><text>V</text></value></field></subform>");
        TextRun name = caption.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>().SelectMany(p => p.Runs).OfType<TextRun>().Single(r => r.Text == "Name");
        Assert.True(name.IsBold);

        // A rich caption beside a flowed value: its runs, styled, then the value.
        var inline = Layout("<subform layout=\"tb\" w=\"576pt\"><field name=\"g\" w=\"4in\" h=\"20pt\"><ui><textEdit/></ui><caption><value>"
                            + Rich("<p><b>Label</b></p>") + "</value></caption><value><text>VAL</text></value></field></subform>");
        Paragraph line = inline.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>().Single(p => p.Runs.OfType<TextRun>().Any(r => r.Text == "Label"));
        Assert.Equal(new[] { "Label", " VAL" }, line.Runs.OfType<TextRun>().Select(r => r.Text));
        Assert.Equal(new[] { true, false }, line.Runs.OfType<TextRun>().Select(r => r.IsBold));
    }

    // ---------------------------------------------------------------- tab stops

    /// <summary>
    /// The tab-stop grammar (chapter 2, "Tab Stops" and "Tab Leader Pattern"): an alignment and a leader
    /// before each location, each applying to that stop alone; before/after as right/left; a content
    /// leader's text; the default stops beyond the last, every interval.
    /// </summary>
    [Fact]
    public void TabStops_ReadTheirAlignmentsAndLeaders()
    {
        var unapplied = new HashSet<string>();
        var stops = XfaTabStops.Parse("left 1in center leader(dots()) 2in right leader(rule(dashed 1pt)) 3in decimal 4in 5in before 6in after leader(rule(solid)) 1.5in leader(use-content(x)) 7in", unapplied);
        Assert.Equal(new[] { 72.0, 144, 216, 288, 360, 432, 108, 504 }, stops.Select(s => s.PosPt));
        Assert.Equal(new[] { TabAlignment.Left, TabAlignment.Center, TabAlignment.Right, TabAlignment.Decimal, TabAlignment.Left,
                             TabAlignment.Right, TabAlignment.Left, TabAlignment.Left }, stops.Select(s => s.Align));
        Assert.Equal(new[] { TabLeader.None, TabLeader.Dot, TabLeader.Hyphen, TabLeader.None, TabLeader.None,
                             TabLeader.None, TabLeader.Underscore, TabLeader.None }, stops.Select(s => s.Leader));
        Assert.Equal(new string?[] { null, null, null, null, null, null, null, "x" }, stops.Select(s => s.LeaderText));
        Assert.Empty(unapplied);

        var defaults = XfaTabStops.WithDefaults(new List<TabStop> { new TabStop { PosPt = 100 } }, 36);
        Assert.Equal(new[] { 100.0, 108, 144, 180 }, defaults.Take(4).Select(s => s.PosPt));
        Assert.Equal(XfaTabStops.MaxStops, XfaTabStops.WithDefaults(new List<TabStop>(), 1).Count);
        Assert.All(XfaTabStops.WithDefaults(new List<TabStop>(), 500), s => Assert.InRange(s.PosPt, 500, XfaTabStops.MaxPositionPt));
        Assert.Empty(XfaTabStops.WithDefaults(new List<TabStop>(), null));
    }

    /// <summary>
    /// A <c>use-content</c> leader's content "in CSS parameter style for a function, typically as a quoted string"
    /// (chapter 2): quoted in either mark, its escapes undone, a closing parenthesis inside it kept; spaces alone
    /// are <c>space()</c>; content past <see cref="XfaTabStops.MaxLeaderTextLength"/> is named, not a leader. A
    /// <c>leaderPatternWidth</c> and a <c>page</c> leader alignment are named; <c>none</c>, the default, is not.
    /// </summary>
    [Fact]
    public void AContentLeader_IsItsText()
    {
        var unapplied = new HashSet<string>();
        var stops = XfaTabStops.Parse("leader(use-content(\"*-\")) 1in leader(use-content('a\\'b)')) 2in leader(use-content(\"  \")) 3in leader(dots() none) 4in", unapplied);
        Assert.Equal(new string?[] { "*-", "a'b)", null, null }, stops.Select(s => s.LeaderText));
        Assert.Equal(new[] { 72.0, 144, 216, 288 }, stops.Select(s => s.PosPt));
        Assert.Empty(unapplied);

        var named = new HashSet<string>();
        var more = XfaTabStops.Parse($"leader(use-content(\"{new string('x', XfaTabStops.MaxLeaderTextLength + 1)}\")) 1in leader(dots() page 6pt) 2in", named);
        Assert.Null(more[0].LeaderText);
        Assert.Equal(TabLeader.Dot, more[1].Leader);
        Assert.Contains(named, u => u.StartsWith("tab leader: use-content", StringComparison.Ordinal));
        Assert.Contains("tab leader alignment", named);
        Assert.Contains("tab leader pattern width", named);
    }

    /// <summary>
    /// Drawn: a <c>use-content("*-")</c> leader fills the gap before its stop with whole repetitions, ending at
    /// the stop where the text after the tab begins; the control, the same stop with no leader, draws none.
    /// </summary>
    [Fact]
    public void AContentLeader_FillsTheGapWithItsText()
    {
        string Content(string leader)
        {
            var result = Layout("<subform w=\"576pt\" h=\"756pt\"><draw name=\"d\" x=\"1in\" y=\"1in\" w=\"5in\" h=\"1in\">"
                                + $"<font typeface=\"Courier\" size=\"10pt\"/><para tabStops=\"{leader} 3in\"/><value><text>Item\tPrice</text></value></draw></subform>");
            using var ms = new System.IO.MemoryStream();
            new Export.PdfExporter().Export(result.Document, ms);
            return Tetitulz.Pdf.TestHelpers.PdfContentProbe.DecodeStreams(ms.ToArray());
        }

        string drawn = Content("leader(use-content(&quot;*-&quot;))");
        var leader = System.Text.RegularExpressions.Regex.Match(drawn, @"\(((?:\*-)+)\) Tj");
        Assert.True(leader.Success, "no leader of the content was drawn");
        // Courier is 6pt a character at 10pt: the gap from "Item"'s end (24pt in) to the stop (216pt) holds 16 repetitions.
        Assert.Equal(16, leader.Groups[1].Value.Length / 2);
        double price = Tetitulz.Pdf.TestHelpers.PdfContentProbe.XOf(drawn, "Price");
        Assert.Equal(Tetitulz.Pdf.TestHelpers.PdfContentProbe.XOf(Content(""), "Price"), price, 1);
        Assert.DoesNotContain("*-", Content(""), StringComparison.Ordinal);
    }

    [Fact]
    public void HostileTabStops_AreBoundedAndSaySo()
    {
        var unapplied = new HashSet<string>();
        Assert.Empty(XfaTabStops.Parse(new string('x', XfaTabStops.MaxTextLength + 1), unapplied));
        Assert.Contains(unapplied, u => u.StartsWith("tab stops longer than", StringComparison.Ordinal));

        var nested = new HashSet<string>();
        Assert.Empty(XfaTabStops.Parse("leader(" + new string('(', 20) + new string(')', 21) + " 1in", nested));
        Assert.Contains(nested, u => u.StartsWith("tab stops nested past", StringComparison.Ordinal));

        var many = XfaTabStops.Parse(string.Join(" ", Enumerable.Range(1, 200).Select(i => i + "pt")), new HashSet<string>());
        Assert.Equal(XfaTabStops.MaxStops, many.Count);

        var far = new HashSet<string>();
        Assert.Empty(XfaTabStops.Parse("3000pt", far));
        Assert.Contains("tab stop: 3000pt", far);
        Assert.Single(XfaTabStops.Parse("1000pt", new HashSet<string>()));        // the control
    }

    /// <summary>A rich paragraph's stops and a span's <c>xfa-tab-count</c> advances; a tab character is whitespace there (chapter 27).</summary>
    [Fact]
    public void RichTabs_AreStopsAndAdvances()
    {
        var p = Read("<p style=\"tab-stops:left 0.5in right 2in;tab-interval:1in\"><span style=\"xfa-tab-count:1\"/>Charles<span style=\"xfa-tab-count:2\"/>Porter\tX</p>")[0];
        Assert.Equal(new[] { 36.0, 144 }, p.TabStops!.Select(s => s.PosPt));
        Assert.Equal(72, p.TabInterval!.Value, 6);
        Assert.Equal(new[] { "TAB", "Charles", "TAB", "TAB", "Porter X" }, p.Runs.Select(r => r.Tab ? "TAB" : r.Text));

        // The paragraph's own stops are in effect within it, in place of its container's (3.3 in here).
        var result = Layout("<subform w=\"576pt\" h=\"756pt\"><draw name=\"d\" x=\"1in\" y=\"1in\" w=\"4in\" h=\"1in\"><para tabStops=\"left 3.3in\"/><value>"
                            + Rich("<p style=\"tab-stops:right 2in;tab-interval:1in\"><span style=\"xfa-tab-count:1\"/>R</p>") + "</value></draw></subform>");
        Paragraph drawn = result.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>().Single(q => q.Runs.OfType<TextRun>().Any(r => r.Text == "R"));
        Assert.IsType<TabRun>(drawn.Runs[0]);
        Assert.Equal((144.0, TabAlignment.Right), (drawn.Tabs[0].PosPt, drawn.Tabs[0].Align));
        Assert.Equal(216, drawn.Tabs[1].PosPt, 6);                              // the defaults after it, every inch
        Assert.DoesNotContain(drawn.Tabs, t => Math.Abs(t.PosPt - 237.6) < 0.01);
    }

    /// <summary>A container's <c>para tabStops</c> and <c>tabDefault</c> set its paragraph's stops, and a tab character in its plain text advances to the next.</summary>
    [Fact]
    public void ParaTabs_SetThePlainTextsStops()
    {
        var result = Layout("<subform w=\"576pt\" h=\"756pt\"><draw name=\"d\" x=\"1in\" y=\"1in\" w=\"4in\" h=\"1in\"><para tabStops=\"center 1in\" tabDefault=\"0.5in\"/>"
                            + "<value><text>1\tSalt\t1 Kg</text></value></draw></subform>");
        Paragraph drawn = result.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>().Single(q => q.Runs.OfType<TextRun>().Any(r => r.Text == "Salt"));
        Assert.Equal(new[] { "1", "TAB", "Salt", "TAB", "1 Kg" }, drawn.Runs.Select(r => r is TabRun ? "TAB" : ((TextRun)r).Text));
        Assert.Equal((72.0, TabAlignment.Center), (drawn.Tabs[0].PosPt, drawn.Tabs[0].Align));
        Assert.Equal(new[] { 108.0, 144 }, drawn.Tabs.Skip(1).Take(2).Select(s => s.PosPt));
        Assert.DoesNotContain(result.Approximations, a => a.StartsWith("text formatting", StringComparison.Ordinal));
    }

    /// <summary>
    /// Chapter 27's page-break and orphan controls, and its kerning and font scale: read, and laid out —
    /// a break before, a page break closing the paragraph, kept with the next, kept together, the counts;
    /// pair kerning and a horizontal scale on the run. What the reader cannot apply is named.
    /// </summary>
    [Fact]
    public void BreaksOrphansKerningAndScale_AreReadAndLaidOut()
    {
        var unapplied = new HashSet<string>();
        var p = Read("<p style=\"page-break-before:always;page-break-after:always;page-break-inside:avoid;orphans:3;widows:2\">"
                     + "<span style=\"kerning-mode:pair;xfa-font-horizontal-scale:80%\">k</span><span style=\"kerning-mode:none\">n</span></p>"
                     + "<p style=\"page-break-after:avoid;page-break-before:avoid;orphans:0\"><span style=\"kerning-mode:x\">z</span></p>", unapplied);
        Assert.Equal((true, true, true, 3, 2), (p[0].PageBreakBefore, p[0].PageBreakAfter, p[0].KeepTogether, p[0].Orphans!.Value, p[0].Widows!.Value));
        Assert.Equal((true, 80), (p[0].Runs[0].Kerning!.Value, p[0].Runs[0].HorizontalScale!.Value));
        Assert.False(p[0].Runs[1].Kerning);
        Assert.True(p[1].KeepWithNext);
        // avoid before a paragraph keeps the one before with it; before the first, it is said.
        var avoided = Read("<p>one</p><p style=\"page-break-before:avoid\">two</p>");
        Assert.Equal(new[] { true, false }, avoided.Select(q => q.KeepWithNext));
        Assert.Equal((false, false, (int?)null), (p[1].PageBreakBefore, p[1].PageBreakAfter, p[1].Orphans));
        Assert.Equal(new[] { "kerning-mode: x", "orphans: 0" }, unapplied.OrderBy(s => s, StringComparer.Ordinal));
        Assert.True(p[0].KeepWithNext);                                     // the second's avoid before keeps the first with it
        // A first paragraph's previous is outside the text: kept with it by the layout, not named.
        var first = new HashSet<string>();
        Assert.True(Read("<p style=\"page-break-before:avoid\">alone</p>", first)[0].KeepWithPrevious);
        Assert.Empty(first);
        Assert.False(Read("<p>one</p><p style=\"page-break-before:avoid\">two</p>")[0].KeepWithPrevious);

        var result = Layout("<subform layout=\"tb\" w=\"576pt\"><draw name=\"d\" w=\"3in\"><value>"
                            + Rich("<p style=\"page-break-before:always;page-break-after:always;page-break-inside:avoid;orphans:3;widows:2\"><span style=\"kerning-mode:pair;xfa-font-horizontal-scale:80%\">K</span></p>")
                            + "</value></draw></subform>");
        Paragraph laid = result.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>().Single(q => q.Runs.OfType<TextRun>().Any(r => r.Text == "K"));
        Assert.Equal((true, true, 3, 2), (laid.PageBreakBefore, laid.KeepTogether, laid.Orphans, laid.Widows));
        Assert.IsType<PageBreak>(laid.Runs[^1]);
        TextRun k = laid.Runs.OfType<TextRun>().Single();
        Assert.Equal((true, 80), (k.Kerning!.Value, k.HorizontalScale));
    }

    /// <summary>A percentage of 1 to 1 000, with or without its sign, rounded; anything else none.</summary>
    [Theory]
    [InlineData("90%", 90)]
    [InlineData(" 90 ", 90)]
    [InlineData("12.6%", 13)]
    [InlineData("1000%", 1000)]
    [InlineData("1001%", null)]
    [InlineData("0%", null)]
    [InlineData("-5%", null)]
    [InlineData("wide", null)]
    [InlineData(null, null)]
    public void APercentage_IsReadInItsRange(string? text, int? expected) => Assert.Equal(expected, XfaRichText.Percent(text));

    /// <summary>
    /// A container's <c>font</c>: <c>kerningMode</c>, <c>lineThrough</c>, <c>letterSpacing</c>, <c>baselineShift</c>,
    /// <c>fontHorizontalScale</c> on its runs — and <c>fontVerticalScale</c>: glyphs 90% wide and 75%
    /// tall are the size at 75%, 7.5pt, drawn at 120% of it across, nothing reported.
    /// </summary>
    [Fact]
    public void AFontsKerningSpacingShiftAndScale_ReachItsRuns()
    {
        var result = Layout("<subform w=\"576pt\" h=\"756pt\"><draw name=\"d\" x=\"1in\" y=\"1in\" w=\"3in\" h=\"1in\">"
                            + "<font typeface=\"Arial\" size=\"10pt\" kerningMode=\"pair\" lineThrough=\"1\" letterSpacing=\"1pt\" baselineShift=\"2pt\" fontHorizontalScale=\"90%\" fontVerticalScale=\"75%\"/>"
                            + "<value><text>F</text></value></draw></subform>");
        TextRun f = result.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>().SelectMany(q => q.Runs).OfType<TextRun>().Single(r => r.Text == "F");
        Assert.Equal((true, true, 1.0, 2.0, 120, 7.5), (f.Kerning!.Value, f.IsStrikethrough, f.CharSpacing, f.BaselineShift, f.HorizontalScale, f.FontSize));
        Assert.DoesNotContain(result.Approximations, a => a.Contains("VerticalScale", StringComparison.Ordinal));

        var plain = Layout("<subform w=\"576pt\" h=\"756pt\"><draw name=\"d\" x=\"1in\" y=\"1in\" w=\"3in\" h=\"1in\"><font typeface=\"Arial\" size=\"10pt\"/>"
                           + "<value><text>G</text></value></draw></subform>");
        TextRun g = plain.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>().SelectMany(q => q.Runs).OfType<TextRun>().Single(r => r.Text == "G");
        Assert.Equal(((bool?)null, false, 0.0, 0.0, 100), (g.Kerning, g.IsStrikethrough, g.CharSpacing, g.BaselineShift, g.HorizontalScale));
        Assert.Empty(plain.Approximations);
    }

    [Fact]
    public void WhatTheReaderDidNotApply_IsReported()
    {
        var result = Layout("<subform w=\"576pt\" h=\"756pt\"><draw name=\"d\" x=\"1in\" y=\"1in\" w=\"3in\" h=\"1in\"><value>"
                            + Rich("<p style=\"text-shadow:1px 1px\">x</p>") + "</value></draw></subform>");
        Assert.Contains(result.Approximations, a => a.StartsWith("text formatting: text-shadow is not applied", StringComparison.Ordinal));
    }

    /// <summary>
    /// Rich text's <c>xfa-font-vertical-scale</c>: a run's own — over the container font's — scales
    /// its size, after its own <c>font-size</c> and horizontal scale; a run without one takes the font's; the
    /// container's vertical scale alone, with no horizontal one, draws its glyphs as wide as they were.
    /// </summary>
    [Fact]
    public void ARichRunsVerticalScale_ScalesItsSize_OverTheFonts()
    {
        var result = Layout("<subform w=\"576pt\" h=\"756pt\"><draw name=\"d\" x=\"1in\" y=\"1in\" w=\"3in\" h=\"2in\">"
                            + "<font typeface=\"Arial\" size=\"10pt\" fontVerticalScale=\"200%\"/><value>"
                            + Rich("<p>Own<span style=\"font-size:12pt;xfa-font-horizontal-scale:150%;xfa-font-vertical-scale:50%\">Tall</span></p>")
                            + "</value></draw></subform>");
        var runs = result.Document.Sections.SelectMany(s => s.Children).OfType<Paragraph>().SelectMany(q => q.Runs).OfType<TextRun>().ToList();
        TextRun own = runs.Single(r => r.Text == "Own"), tall = runs.Single(r => r.Text == "Tall");
        Assert.Equal((20.0, 50), (own.FontSize, own.HorizontalScale));    // the font's 200%: twice as tall, as wide
        Assert.Equal((6.0, 300), (tall.FontSize, tall.HorizontalScale));  // 12pt at 50%, 150% wide over the 50%
        Assert.Empty(result.Approximations);
    }

    /// <summary>
    /// Drawn: a <c>fontVerticalScale</c> of 200% leaves a line's glyphs as wide as at 100% — a right-aligned
    /// word starts where it did, so it ends where it did — drawn at twice the size and half the horizontal
    /// scale, and the line under it twice as far down, as an independent renderer lays it out.
    /// </summary>
    [Fact]
    public void AVerticalScale_KeepsTheWidth_AndDoublesTheHeight()
    {
        var plain = DrawnTwoLines("size=\"14pt\"", "");
        var scaled = DrawnTwoLines("size=\"14pt\" fontVerticalScale=\"200%\"", "");
        Assert.False(double.IsNaN(plain.X) || double.IsNaN(scaled.X), "a word was not drawn on its own");
        Assert.Equal(plain.X, scaled.X, 1);
        Assert.Equal(2 * plain.LineStep, scaled.LineStep, 1);
        Assert.Matches(@"\b28 Tf\b", scaled.Content);
        Assert.Matches(@"\b50 Tz\b", scaled.Content);
        Assert.DoesNotMatch(@"\b50 Tz\b", plain.Content);
    }

    /// <summary>
    /// A rich text's first paragraph's <c>page-break-before: avoid</c>: the block before the text in the
    /// flow is kept with it — the box of the draw before kept whole with the next block, and a field's caption it
    /// opens with likewise; the control, the same text without it, keeps nothing; a text first in the flow has no
    /// block before it to keep.
    /// </summary>
    [Fact]
    public void AFirstParagraphsAvoidBefore_KeepsTheBlockBeforeTheTextWithIt()
    {
        BoxStart FirstBox(string style, string second = "draw")
        {
            string text = second == "draw"
                ? "<draw name=\"b\" w=\"3in\"><value>" + Rich($"<p style=\"{style}\">two</p>") + "</value></draw>"
                : "<field name=\"b\" w=\"3in\"><caption placement=\"left\"><value>" + Rich($"<p style=\"{style}\">Cap</p>") + "</value></caption><value><text>v</text></value></field>";
            var result = Layout("<subform layout=\"tb\" w=\"576pt\"><draw name=\"a\" w=\"3in\"><value><text>one</text></value></draw>" + text + "</subform>");
            // Draw a's box: the box nearest before its paragraph.
            var children = result.Document.Sections.SelectMany(s => s.Children).ToList();
            int one = children.FindIndex(c => c is Paragraph p && p.Runs.OfType<TextRun>().Any(r => r.Text == "one"));
            return (BoxStart)children.Take(one).Last(c => c is BoxStart);
        }

        Assert.True(FirstBox("page-break-before:avoid").KeepWithNext);
        Assert.False(FirstBox("color:red").KeepWithNext);
        Assert.True(FirstBox("page-break-before:avoid", "field").KeepWithNext);
        Assert.False(FirstBox("color:red", "field").KeepWithNext);

        // After a table subform, the table keeps with the text.
        Table TableBefore(string style) => Layout("<subform layout=\"tb\" w=\"576pt\"><subform name=\"t\" layout=\"table\" columnWidths=\"1in\"><subform layout=\"row\"><draw name=\"c\" w=\"1in\"><value><text>cell</text></value></draw></subform></subform>"
                                                  + "<draw name=\"b\" w=\"3in\"><value>" + Rich($"<p style=\"{style}\">two</p>") + "</value></draw></subform>")
                                              .Document.Sections.SelectMany(s => s.Children).OfType<Table>().Single();
        Assert.True(TableBefore("page-break-before:avoid").KeepWithNext);
        Assert.False(TableBefore("color:red").KeepWithNext);

        var alone = Layout("<subform layout=\"tb\" w=\"576pt\"><draw name=\"b\" w=\"3in\"><value>" + Rich("<p style=\"page-break-before:avoid\">two</p>") + "</value></draw></subform>");
        Assert.DoesNotContain(alone.Document.Sections.SelectMany(s => s.Children).OfType<BoxStart>(), b => b.KeepWithNext);
        Assert.DoesNotContain(alone.Approximations, a => a.Contains("page-break-before", StringComparison.Ordinal));
    }

    /// <summary>
    /// A line's height: with no <c>lineHeight</c> the font's, 1.2 times its size, as an independent renderer and another reader
    /// lay a standard font out — 12pt for XFA's default 10pt, not the exporter's least 14pt, and 24pt for
    /// 20pt; a stated <c>lineHeight</c>, as it says; and a stated one grown by the font's vertical
    /// scale, as an independent renderer grows it.
    /// </summary>
    [Theory]
    [InlineData("size=\"10pt\"", "", 12.0)]
    [InlineData("size=\"20pt\"", "", 24.0)]
    [InlineData("size=\"10pt\"", "lineHeight=\"0pt\"", 12.0)]
    [InlineData("size=\"10pt\"", "lineHeight=\"20pt\"", 20.0)]
    [InlineData("size=\"10pt\" fontVerticalScale=\"150%\"", "lineHeight=\"20pt\"", 30.0)]
    public void ALinesHeight_IsTheFontsOrTheStatedOne(string font, string para, double step)
        => Assert.Equal(step, DrawnTwoLines(font, para).LineStep, 1);

    /// <summary>
    /// A draw of two lines, <c>Wide</c> over <c>Second</c>, right-aligned, in Helvetica under the <c>font</c> and
    /// <c>para</c> attributes given: where <c>Wide</c> starts, how far below it <c>Second</c>'s baseline is, and the
    /// page's content.
    /// </summary>
    private static (double X, double LineStep, string Content) DrawnTwoLines(string font, string para)
    {
        var result = Layout("<subform w=\"576pt\" h=\"756pt\"><draw name=\"d\" x=\"1in\" y=\"1in\" w=\"5in\" h=\"3in\">"
                            + $"<font typeface=\"Helvetica\" {font}/><para hAlign=\"right\" {para}/><value><text>Wide\nSecond</text></value></draw></subform>");
        using var ms = new System.IO.MemoryStream();
        new Export.PdfExporter().Export(result.Document, ms);
        string content = Tetitulz.Pdf.TestHelpers.PdfContentProbe.DecodeStreams(ms.ToArray());
        double y(string word) => Tetitulz.Pdf.TestHelpers.PdfContentProbe.YOf(content, word);
        return (Tetitulz.Pdf.TestHelpers.PdfContentProbe.XOf(content, "Wide"), y("Wide") - y("Second"), content);
    }
}
