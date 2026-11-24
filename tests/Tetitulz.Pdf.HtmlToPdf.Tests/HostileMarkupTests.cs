// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// The managed engine against the structure fuzzer's HTML corpus: four families of hostile markup,
/// each with the guard that answers and the control that keeps the guard
/// from refusing a page a person wrote.
/// </summary>
/// <remarks>
/// <para>Unguarded, a <c>colspan</c> of two billion builds two billion columns and runs out of memory
/// on a table of four hundred cells; a million-cell table runs out of memory; a selector of a thousand
/// descendant combinators sends AngleSharp's matcher into exponential recursion for the whole window;
/// a hundred thousand open list items send its tree builder into a quadratic scope walk for the whole
/// window. The first two are the mapper's to bound (the Standard's own clamps,
/// a cell budget); the last two are the parser's and the matcher's, which the mapper cannot interrupt,
/// so the document and the rule are refused before they are handed over.</para>
/// </remarks>
[Collection("HtmlMapperLimits")]
public class HostileMarkupTests
{
    private static byte[] Convert(string html, List<string>? reports = null)
    {
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = reports == null ? null : d => { lock (reports) reports.Add(d.Detail ?? ""); },
        });
        return output.ToArray();
    }

    private static int Pages(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        return doc.Pages.Count;
    }

    // ------------------------------------------------------------------ spans

    /// <summary>A span attribute is read as the HTML Standard reads it: an integer above 1, clamped.</summary>
    [Theory]
    [InlineData("2147483647", HtmlToBuilderMapper.MaxColSpan, 1000)]
    [InlineData("65535", HtmlToBuilderMapper.MaxRowSpan, 65534)]
    [InlineData("1001", HtmlToBuilderMapper.MaxColSpan, 1000)]
    [InlineData("7", HtmlToBuilderMapper.MaxColSpan, 7)]
    [InlineData("-1", HtmlToBuilderMapper.MaxColSpan, 1)]
    [InlineData("0", HtmlToBuilderMapper.MaxColSpan, 1)]
    [InlineData("1e9", HtmlToBuilderMapper.MaxColSpan, 1)]
    [InlineData("99999999999999999999", HtmlToBuilderMapper.MaxColSpan, 1)]
    [InlineData(null, HtmlToBuilderMapper.MaxColSpan, 1)]
    public void ASpanAttribute_IsAnIntegerAboveOne_ClampedToTheStandard(string? attribute, int max, int expected)
        => Assert.Equal(expected, HtmlToBuilderMapper.ParseSpan(attribute, max));

    /// <summary>
    /// The fuzzer's table: twenty rows of twenty cells whose spans run past <c>int</c>. Under the clamp
    /// it is laid out on the columns its cells start in, rather than running out of memory building columns,
    /// as a browser lays it out — the spans' empty columns are no width (TableGridBoundsTests). The
    /// claim that can be asserted here is that it converts and opens (the theory above pins what the
    /// clamp reads).
    /// </summary>
    [Fact]
    public void HostileSpansPastInt_ConvertUnderTheStandardsClamp()
    {
        var sb = new StringBuilder("<table>");
        string[] cols = { "2147483647", "4294967296", "-1", "0", "1e9", "99999999999999999999" };
        string[] rows = { "65534", "2147483647", "-5", "0", "1000000" };
        for (int r = 0; r < 20; r++)
        {
            sb.Append("<tr>");
            for (int c = 0; c < 20; c++)
                sb.Append("<td colspan=\"" + cols[(r + c) % 6] + "\" rowspan=\"" + rows[(r * c) % 5] + "\">s" + r + "_" + c + "</td>");
            sb.Append("</tr>");
        }
        sb.Append("<colgroup span=\"2147483647\"><col span=\"2147483647\"></colgroup></table>");

        byte[] pdf = Convert(sb.ToString());

        Assert.True(Pages(pdf) >= 1);
    }

    // ------------------------------------------------------------------ the cell budget

    /// <summary>
    /// Cells past the budget are dropped and the drop is reported once; the cells before it are in
    /// the output. The budget is lowered for the test and restored, so the fixture stays small.
    /// </summary>
    [Fact]
    public void TableCellsPastTheBudget_AreDroppedAndReportedOnce()
    {
        int saved = HtmlToBuilderMapper.MaxTableCells;
        try
        {
            HtmlToBuilderMapper.MaxTableCells = 50;
            var sb = new StringBuilder("<table>");
            for (int r = 0; r < 20; r++)
            {
                sb.Append("<tr>");
                for (int c = 0; c < 20; c++) sb.Append("<td>c" + (r * 20 + c) + "</td>");
                sb.Append("</tr>");
            }
            sb.Append("</table>");

            var reports = new List<string>();
            byte[] pdf = Convert(sb.ToString(), reports);

            string text = Tetitulz.Pdf.Base.PdfOperations.ExtractText(new MemoryStream(pdf));
            Assert.Contains("c0", text, StringComparison.Ordinal);
            Assert.Contains("c49", text, StringComparison.Ordinal);
            Assert.DoesNotContain("c399", text, StringComparison.Ordinal);
            Assert.Equal(1, reports.FindAll(r => r.Contains("cell mapping budget", StringComparison.Ordinal)).Count);
        }
        finally { HtmlToBuilderMapper.MaxTableCells = saved; }
    }

    /// <summary>The control: the same table under the default budget is whole, and nothing is reported.</summary>
    [Fact]
    public void TableCellsUnderTheBudget_AreAllMapped()
    {
        var sb = new StringBuilder("<table>");
        for (int r = 0; r < 20; r++)
        {
            sb.Append("<tr>");
            for (int c = 0; c < 20; c++) sb.Append("<td>c" + (r * 20 + c) + "</td>");
            sb.Append("</tr>");
        }
        sb.Append("</table>");

        var reports = new List<string>();
        byte[] pdf = Convert(sb.ToString(), reports);

        string text = Tetitulz.Pdf.Base.PdfOperations.ExtractText(new MemoryStream(pdf));
        Assert.Contains("c399", text, StringComparison.Ordinal);
        Assert.DoesNotContain(reports, r => r.Contains("cell mapping budget", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ selectors

    /// <summary>
    /// What the complexity counts: compounds along the combinators, selector functions, the longest
    /// member of a list. Whitespace beside an explicit combinator is not a second combinator.
    /// </summary>
    [Theory]
    [InlineData("p", 1)]
    [InlineData("div p", 2)]
    [InlineData("div > p.a:not(.b) ~ span", 4)]
    [InlineData("div   >   p", 2)]
    [InlineData("a, b c d", 3)]
    [InlineData("p:not(.a):not(.b):is(.c)", 4)]
    [InlineData("[data-x='a b c'] p", 2)]
    [InlineData("ul li:nth-child(2n + 1)", 2)]
    public void SelectorComplexity_CountsCompoundsAndFunctions(string selector, int expected)
        => Assert.Equal(expected, HtmlToBuilderMapper.SelectorComplexity(selector));

    /// <summary>
    /// A rule whose selector is forty compounds long is skipped and reported once; the rule beside it
    /// applies — the paragraph it bolds is set in the bold face, which a rule-dropping bug that dropped
    /// the whole sheet would lose.
    /// </summary>
    [Fact]
    public void HostileSelectorOfManyCompounds_IsSkippedAndReported_WhileOrdinaryRulesApply()
    {
        var chain = new StringBuilder();
        for (int i = 0; i < 40; i++) chain.Append("div ");
        string html = "<html><head><style>" + chain + "p { color: red } p.k { font-weight: bold }</style></head>" +
                      "<body><div><div><p class=\"k\">bold text</p></div></div></body></html>";

        var reports = new List<string>();
        byte[] pdf = Convert(html, reports);

        Assert.Equal(1, reports.FindAll(r => r.Contains("compound", StringComparison.Ordinal)).Count);
        Assert.Contains("Helvetica-Bold", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
    }

    /// <summary>The control: a selector of thirty-two compounds is at the cap and applies.</summary>
    [Fact]
    public void ASelectorAtTheCap_Applies()
    {
        var chain = new StringBuilder();
        for (int i = 0; i < 31; i++) chain.Append("div ");
        var nesting = new StringBuilder();
        for (int i = 0; i < 31; i++) nesting.Append("<div>");
        string html = "<html><head><style>" + chain + "p { font-weight: bold }</style></head><body>" + nesting +
                      "<p>bold text</p>" + new string('x', 0) + "</body></html>";

        var reports = new List<string>();
        byte[] pdf = Convert(html, reports);

        Assert.DoesNotContain(reports, r => r.Contains("compound", StringComparison.Ordinal));
        Assert.Contains("Helvetica-Bold", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ nesting

    /// <summary>
    /// Markup nested past the cap is refused before the parser sees it, by name. The fuzzer's file was
    /// a hundred thousand open list items; five thousand is past the cap and well inside a second.
    /// </summary>
    [Fact]
    public void HostileMarkupNestedPastTheCap_IsRefusedByName()
    {
        var sb = new StringBuilder("<html><body>");
        for (int i = 0; i < HtmlToBuilderMapper.MaxMarkupNesting + 1000; i++) sb.Append("<ol><li>");
        sb.Append("item");
        var ex = Assert.Throws<HtmlToPdfException>(() => Convert(sb.ToString()));
        Assert.Contains("nests more than", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Markup past <see cref="HtmlToPdfOptions.MaxHtmlBytes"/> is refused by name before the parser
    /// sees it — the engine's peak memory is some 55 times the markup (a 31 MB table took 1.7 GB), so
    /// the markup is the thing to bound. A 5 KB page under a 4 096-byte cap is refused; the same page
    /// at exactly its own size converts and one byte under is refused; a cap of 0 is off.
    /// </summary>
    [Fact]
    public void AdversarialMarkupPastTheSizeCap_IsRefusedByNameBeforeTheParse()
    {
        var sb = new StringBuilder("<html><body>");
        for (int i = 0; i < 500; i++) sb.Append("<p>para</p>");
        string html = sb.Append("</body></html>").ToString();
        long exact = Encoding.UTF8.GetByteCount(html);
        static byte[] Under(string h, long cap)
        {
            using var o = new MemoryStream();
            HtmlToPdfConverter.ConvertHtml(h, o, new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed, MaxHtmlBytes = cap });
            return o.ToArray();
        }

        var ex = Assert.Throws<HtmlToPdfException>(() => Under(html, 4096));
        Assert.Contains($"The markup is {exact} bytes of UTF-8, more than HtmlToPdfOptions.MaxHtmlBytes (4096)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("before it was parsed", ex.Message, StringComparison.Ordinal);
        Assert.Throws<HtmlToPdfException>(() => Under(html, exact - 1));
        Assert.True(Pages(Under(html, exact)) >= 1);
        Assert.True(Pages(Under(html, 0)) >= 1);
    }

    /// <summary>
    /// The control, and the scan's blind spots closed, each with more tags than the cap: a page nested
    /// a thousand deep converts (the mapper's own depth cap stops at 200 and says so); tags inside a
    /// script, a style or a comment are text, not elements; an end tag closes what its start tag opened,
    /// so thirty thousand sibling paragraphs are one deep. A scan that counted any of these would
    /// refuse a page a person could write.
    /// </summary>
    [Fact]
    public void MarkupInsideTheCap_OrInsideScriptsAndComments_OrAsSiblings_IsNotRefused()
    {
        int past = HtmlToBuilderMapper.MaxMarkupNesting + 5000;

        var deep = new StringBuilder("<html><body>");
        for (int i = 0; i < 1000; i++) deep.Append("<div>");
        deep.Append("deep");
        Assert.True(Pages(Convert(deep.ToString())) >= 1);

        var script = new StringBuilder("<html><head><script>var s = '");
        for (int i = 0; i < past / 3 + 1; i++) script.Append("<div><li><p>");
        script.Append("';</script><style>/* <div><div> */</style></head><body><!-- ");
        for (int i = 0; i < past; i++) script.Append("<section>");
        script.Append(" --><p>text</p></body></html>");
        Assert.True(Pages(Convert(script.ToString())) >= 1);

        var siblings = new StringBuilder("<html><body>");
        for (int i = 0; i < past; i++) siblings.Append("<p>s</p>");
        siblings.Append("</body></html>");
        Assert.True(Pages(Convert(siblings.ToString())) >= 1);
    }

    /// <summary>Void elements open nothing: more <c>&lt;br&gt;</c> and <c>&lt;img&gt;</c> than the cap are not depth.</summary>
    [Fact]
    public void VoidElements_DoNotCountAsNesting()
    {
        var sb = new StringBuilder("<html><body><p>");
        for (int i = 0; i < HtmlToBuilderMapper.MaxMarkupNesting + 5000; i++) sb.Append(i % 2 == 0 ? "<br>" : "<img src=\"data:,\" alt=\"\">");
        sb.Append("text</p></body></html>");
        Assert.True(Pages(Convert(sb.ToString())) >= 1);
    }
}
