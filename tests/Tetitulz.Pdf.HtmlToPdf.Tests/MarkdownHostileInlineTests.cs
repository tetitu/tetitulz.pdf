// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Tetitulz.Pdf.HtmlToPdf.Markdown;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// The Markdown converter's inline rules on hostile text: links and images, code spans and attribute
/// values — each linear, where a backtracking pattern or a per-span replace is quadratic — and a quote in a
/// link's target escaped rather than writing an attribute of its own.
/// </summary>
public class MarkdownHostileInlineTests
{
    private static XElement Body(string markdown)
        => XDocument.Parse(MarkdownToHtml.Convert(markdown, "t")).Root!.Element("body")!;

    [Fact]
    public void LinksAndImages_AreReadLeftmostFirst()
    {
        var body = Body("see [a](https://a.example) and ![pic](p.png) then [x[y](z) end");
        var links = body.Descendants("a").ToList();
        Assert.Equal(new[] { "https://a.example", "z" }, links.Select(a => (string)a.Attribute("href")!));
        Assert.Equal(new[] { "a", "x[y" }, links.Select(a => a.Value));   // the label runs to the first ']'
        var img = Assert.Single(body.Descendants("img"));
        Assert.Equal("p.png", (string)img.Attribute("src")!);
        Assert.Equal("pic", (string)img.Attribute("alt")!);
    }

    [Theory]
    [InlineData("[a](b c)")]      // white space ends the target before its ')'
    [InlineData("[a](b")]         // no ')'
    [InlineData("[a]b")]          // no '('
    [InlineData("[a](")]          // an empty target
    [InlineData("[a")]            // no ']'
    public void NotALink_StaysText(string markdown)
    {
        var body = Body(markdown);
        Assert.Empty(body.Descendants("a"));
        Assert.Contains(markdown.Replace("(", "").Substring(0, 2), body.Value.Replace("(", ""), StringComparison.Ordinal);
    }

    /// <summary>A quote in a link's target is escaped, not a way out of the attribute: one attribute, the whole target its value.</summary>
    [Fact]
    public void AQuoteInALinkTarget_StaysInsideTheAttribute()
    {
        var a = Assert.Single(Body("[x](a\"onclick=\"alert(1))").Descendants("a"));
        Assert.Single(a.Attributes());
        Assert.Equal("a\"onclick=\"alert(1", (string)a.Attribute("href")!);
    }

    /// <summary>
    /// A link pass that read the image pass's output found the '[' inside a generated alt and wrote its own
    /// quotes there: the alt closed early and the rest became an onerror handler. Each image and link is a
    /// placeholder until the passes are done, so the output parses as XML, and an HTML parser reads no
    /// handler and nothing but the image's own two attributes.
    /// </summary>
    [Theory]
    [InlineData("![x[y](x)](onerror=window.onerror=alert;throw+1//)")]
    [InlineData("![x](a[b)](onerror=window.onerror=alert;throw+1//)")]
    [InlineData("[a](![b](onerror=alert(1)//))")]
    public void AdversarialLinkInsideAnImage_WritesNoAttributeOfItsOwn(string markdown)
    {
        string html = MarkdownToHtml.Convert(markdown, "t");

        var body = XDocument.Parse(html).Root!.Element("body")!;
        Assert.All(body.Descendants(), e => Assert.DoesNotContain(e.Attributes(), a => a.Name.LocalName.StartsWith("on", StringComparison.Ordinal)));
        var parsed = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        Assert.All(parsed.QuerySelectorAll("*"), e => Assert.DoesNotContain(e.Attributes, a => a.Name.StartsWith("on", StringComparison.Ordinal)));
        Assert.All(parsed.QuerySelectorAll("img"), img => Assert.True(img.Attributes.Length <= 2));
    }

    /// <summary>
    /// The converter's placeholder, written in the source — inside an alt here — is no character a document
    /// holds: replaced, the output stays XML and carries no control character, and no rendered markup lands
    /// where the source put it.
    /// </summary>
    [Fact]
    public void AdversarialPlaceholderInTheSource_IsReplaced()
    {
        string html = MarkdownToHtml.Convert("![a](b) ![\u0001HTML0\u0001](x) and \u0001CODE0\u0001 `c`", "t");

        Assert.DoesNotContain('\u0001', html);
        var imgs = XDocument.Parse(html).Root!.Element("body")!.Descendants("img").ToList();
        Assert.Equal(new[] { "b", "x" }, imgs.Select(i => (string)i.Attribute("src")!));
        Assert.DoesNotContain("<", (string)imgs[1].Attribute("alt")!, StringComparison.Ordinal);
    }

    /// <summary>A target whose scheme no document link uses renders its label alone; the control's web link stays.</summary>
    [Theory]
    [InlineData("[a](javascript:alert%281%29)")]
    [InlineData("![a](javascript:alert%281%29)")]
    [InlineData("[a](JAVASCRIPT:x)")]
    [InlineData("[a](data:text/html,x)")]
    public void AdversarialScriptTarget_RendersTheLabelAlone(string markdown)
    {
        var body = Body(markdown);

        Assert.Empty(body.Descendants("a"));
        Assert.Empty(body.Descendants("img"));
        Assert.Contains("a", body.Value, StringComparison.Ordinal);
        Assert.Equal("https://a.example/", (string)Assert.Single(Body("[a](https://a.example/)").Descendants("a")).Attribute("href")!);
    }

    /// <summary>A linked image stays one: the image inside the link's label, emphasis inside the label applied.</summary>
    [Fact]
    public void ALinkedImageAndEmphasisInALabel_AreRendered()
    {
        var a = Assert.Single(Body("[![pic](p.png) **bold**](https://a.example/)").Descendants("a"));

        Assert.Equal("https://a.example/", (string)a.Attribute("href")!);
        Assert.Equal("p.png", (string)Assert.Single(a.Descendants("img")).Attribute("src")!);
        Assert.Equal("bold", Assert.Single(a.Descendants("strong")).Value);
    }

    [Fact]
    public void CodeSpans_AreRestoredInPlace()
    {
        var body = Body("a `x` b `y<z` c");
        Assert.Equal(new[] { "x", "y<z" }, body.Descendants("code").Select(c => c.Value));
    }

    [Fact]
    public void AHostileParagraphOfUnclosedBrackets_IsConvertedAndKeepsItsText()
        => AssertConverts(Repeat("[](", 200_000) + "Word");

    /// <summary>
    /// Two hundred thousand openers sharing one <c>]</c> that no <c>(</c> follows: the label is searched for once,
    /// the openers inside it skipped, where each would scan to it again — the count is the evidence, since a search
    /// runs at vector speed. The control: openers each with their own <c>]</c> are each searched.
    /// </summary>
    [Fact]
    public void AHostileParagraphOfOpenersSharingOneClose_IsSearchedOnce()
    {
        AssertConverts(Repeat("[", 200_000) + "] Word");

        long before = MarkdownToHtml.LabelSearches;
        MarkdownToHtml.Convert(Repeat("[", 1000) + "] Word", "t");
        Assert.InRange(MarkdownToHtml.LabelSearches - before, 1, 4);   // a pass for images, one for links

        before = MarkdownToHtml.LabelSearches;
        MarkdownToHtml.Convert(Repeat("[a] ", 1000) + "Word", "t");
        Assert.True(MarkdownToHtml.LabelSearches - before >= 1000, "each opener with its own ']' is searched");
    }

    [Fact]
    public void AHostileParagraphOfUnclosedImageOpeners_IsConvertedAndKeepsItsText()
        => AssertConverts(Repeat("![", 200_000) + "Word");

    [Fact]
    public void AHostileParagraphOfManyCodeSpans_IsConvertedAndKeepsItsText()
        => AssertConverts(Repeat("`c` ", 100_000) + "Word");

    private static string Repeat(string s, int count)
    {
        var sb = new StringBuilder(s.Length * count);
        for (int i = 0; i < count; i++) sb.Append(s);
        return sb.ToString();
    }

    /// <summary>Converts on a worker with a generous join; the paragraph's last word is in the output.</summary>
    private static void AssertConverts(string markdown)
    {
        string? html = null;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { html = MarkdownToHtml.Convert(markdown, "t"); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(60)), "the conversion did not finish");
        Assert.Null(failure);
        Assert.Contains("Word</p>", html!, StringComparison.Ordinal);
    }
}
