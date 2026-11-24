// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// What <see cref="Document.ToHtml"/> puts inside a quoted attribute.
///
/// <para><b>Asserted by parsing, not by substring.</b> "the exported HTML does not contain
/// <c>onmouseover</c>" is satisfied by an escaping bug that spells the payload differently, and
/// "it does contain <c>&amp;quot;</c>" says nothing about where. Every case here parses the anchor
/// or span as XML and reads the <i>attribute list</i>, so an injected attribute fails by existing
/// and a mangled value fails by not round-tripping.</para>
///
/// <para>The model strings these use are exactly what a converted document supplies:
/// <c>HtmlToBuilderMapper</c> assigns <c>LinkUri</c> verbatim from an <c>href</c>, and the DOCX
/// mapper copies <c>w:color</c> values into <see cref="TextRun.Color"/>.</para>
/// </summary>
public class HtmlExportEscapingTests
{
    private static string Html(TextRun run)
    {
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph();
        p.Runs.Add(run);
        section.Children.Add(p);
        doc.Sections.Add(section);
        return Encoding.UTF8.GetString(doc.ToHtml());
    }

    /// <summary>The first element of the given name in the export, parsed.</summary>
    private static XElement Element(string html, string name)
    {
        int start = html.IndexOf("<" + name, System.StringComparison.Ordinal);
        Assert.True(start >= 0, $"no <{name}> in the export:\n{html}");
        int end = html.IndexOf("</" + name + ">", start, System.StringComparison.Ordinal);
        Assert.True(end >= 0, $"unterminated <{name}> in the export:\n{html}");
        return XElement.Parse(html.Substring(start, end - start + name.Length + 3));
    }

    // ---------------------------------------------------------------- href

    /// <summary>
    /// A double quote in a link target must not be able to close the <c>href</c> attribute.
    /// </summary>
    /// <remarks>
    /// <c>Enc</c> escaped <c>&amp;</c>, <c>&lt;</c> and <c>&gt;</c> only, so
    /// <c>x" onmouseover="alert(1)</c> produced
    /// <c>&lt;a href="x" onmouseover="alert(1)"&gt;</c> — a second, executable attribute in
    /// whatever page displays the export.
    /// </remarks>
    [Fact]
    public void LinkUriWithADoubleQuote_CannotOpenASecondAttribute()
    {
        string html = Html(new TextRun
        {
            Text = "click",
            LinkUri = "https://example.com/\" onmouseover=\"alert(1)",
        });

        var a = Element(html, "a");

        // Exactly one attribute — the href — and it still carries the whole target verbatim.
        Assert.Single(a.Attributes());
        Assert.Equal("href", a.Attributes().First().Name.LocalName);
        Assert.Equal("https://example.com/\" onmouseover=\"alert(1)", a.Attribute("href")!.Value);
    }

    [Fact]
    public void LinkUriWithASingleQuote_RoundTripsThroughTheAttribute()
    {
        string html = Html(new TextRun { Text = "click", LinkUri = "https://example.com/?q='x'" });

        Assert.Equal("https://example.com/?q='x'", Element(html, "a").Attribute("href")!.Value);
    }

    /// <summary>
    /// A <c>javascript:</c> target is dropped outright. Quote-escaping stops a value breaking
    /// <i>out</i> of the attribute; it does nothing about a scheme that executes inside it.
    /// </summary>
    [Fact]
    public void JavascriptLinkUri_IsNotWrittenAsAnHref()
    {
        string html = Html(new TextRun { Text = "click", LinkUri = "javascript:alert(1)" });

        Assert.DoesNotContain("<a ", html);
        Assert.Contains("click", html);        // the text survives; only the anchor is dropped
    }

    [Fact]
    public void RelativeAndFragmentLinkUris_AreStillWritten()
    {
        Assert.Equal("page2.html", Element(Html(new TextRun { Text = "t", LinkUri = "page2.html" }), "a")
            .Attribute("href")!.Value);
        Assert.Equal("#anchor", Element(Html(new TextRun { Text = "t", LinkUri = "#anchor" }), "a")
            .Attribute("href")!.Value);
    }

    // ---------------------------------------------------------------- style

    /// <summary>
    /// The colour path never went through <c>Enc</c> at all, so a model colour could close the
    /// <c>style</c> attribute and open an event handler. Colours are now validated, not escaped:
    /// <c>&amp;quot;</c> is decoded back to a quote before a CSS parser sees it.
    /// </summary>
    [Fact]
    public void ColourThatIsNotAHexValue_IsDroppedRatherThanInterpolated()
    {
        string html = Html(new TextRun
        {
            Text = "total",
            Color = "red\" onmouseover=\"alert(1)",
        });

        Assert.DoesNotContain("onmouseover", html);
        int spanAt = html.IndexOf("<span", System.StringComparison.Ordinal);
        if (spanAt >= 0)
            Assert.Single(Element(html, "span").Attributes());   // only style, nothing injected
    }

    [Fact]
    public void HighlightThatIsNotAHexValue_IsDroppedRatherThanInterpolated()
    {
        string html = Html(new TextRun { Text = "total", Highlight = "yellow;\"><script>x</script" });

        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("yellow", html);
    }

    /// <summary>
    /// A bare CSS keyword is dropped too, and that is the deliberate answer rather than an
    /// oversight of the allow-list.
    /// </summary>
    /// <remarks>
    /// <para>A keyword is safe to interpolate — it is letters only — so "escape it and let it
    /// through" is a defensible rule and was implemented once. It is not the rule here, because
    /// <see cref="Export.PdfExporter"/>'s <c>ParseColorD</c> cannot read one: emitting
    /// <c>color:red</c> in HTML while the PDF export of the same model paints black is the two
    /// exporters disagreeing about one document, which is the class of defect this repository
    /// keeps finding.</para>
    /// <para>Nothing in the tree produces one either: <c>HtmlToBuilderMapper.TryNormalizeColor</c>
    /// normalises every CSS colour to <c>#RRGGBB</c> before it reaches the model, and OOXML's
    /// <c>w:color</c> is <c>ST_HexColorRGB</c>. The day the model accepts keywords, both exporters
    /// gain them together.</para>
    /// </remarks>
    [Theory]
    [InlineData("red")]
    [InlineData("rebeccapurple")]
    public void BareCssKeywordColour_IsDroppedSoBothExportersAgree(string keyword)
    {
        string html = Html(new TextRun { Text = "total", Color = keyword });

        Assert.DoesNotContain("color:" + keyword, html);
    }

    [Fact]
    public void HexColourAndHighlight_AreStillEmitted()
    {
        string html = Html(new TextRun { Text = "total", Color = "#FF0000", Highlight = "#00FF00" });

        string style = Element(html, "span").Attribute("style")!.Value;
        Assert.Contains("color:#FF0000;", style);
        Assert.Contains("background-color:#00FF00;", style);
    }

    /// <summary>
    /// A bare-hex colour — the form OOXML's <c>w:color</c> (<c>ST_HexColorRGB</c>) carries and the
    /// DOCX mapper copies into the model — is written with the leading <c>#</c> CSS requires
    /// (Builder-1:9). Returned unchanged, <c>color:FF0000</c> would reach the browser invalid and be
    /// ignored: the HTML would lose a colour the PDF keeps. The negative
    /// assertion sits beside the positive one, so an empty style could not pass it.
    /// </summary>
    [Fact]
    public void BareHexColour_GetsTheLeadingHashSoCssAcceptsIt()
    {
        string html = Html(new TextRun { Text = "total", Color = "FF0000", Highlight = "00FF00" });

        string style = Element(html, "span").Attribute("style")!.Value;
        Assert.Contains("color:#FF0000;", style);
        Assert.Contains("background-color:#00FF00;", style);
        Assert.DoesNotContain("color:FF0000", style); // never the bare, browser-ignored form
    }

    /// <summary>
    /// Only a CSS hex length — 3, 4, 6 or 8 digits — is emitted. A 5- or 7-digit string is hex but
    /// not a CSS colour; writing it would be a declaration the browser discards. The 6-digit case is
    /// the control that makes the two rejections non-vacuous.
    /// </summary>
    [Theory]
    [InlineData("FF0000", true)]    // 6 digits: a CSS length
    [InlineData("FF000", false)]    // 5 digits: not one
    [InlineData("FF00000", false)]  // 7 digits: not one
    public void OnlyACssHexLength_IsEmitted(string value, bool emitted)
    {
        string html = Html(new TextRun { Text = "total", Color = value });

        if (emitted)
            Assert.Contains("color:#" + value + ";", html);
        else
        {
            Assert.DoesNotContain("color:#" + value, html);
            Assert.DoesNotContain("color:" + value, html);
        }
    }

    /// <summary>A cell background is the DOCX mapper's <c>w:shd</c>, and takes the same route.</summary>
    [Fact]
    public void CellBackgroundThatIsNotAHexValue_IsDropped()
    {
        var doc = new Document();
        var section = new Section();
        var table = new Table();
        var row = new TableRow();
        var cell = new TableCell { BackgroundColor = "x\" onclick=\"alert(1)" };
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "cell" });
        cell.Blocks.Add(p);
        row.Cells.Add(cell);
        table.Rows.Add(row);
        section.Children.Add(table);
        doc.Sections.Add(section);

        string html = Encoding.UTF8.GetString(doc.ToHtml());

        Assert.DoesNotContain("onclick", html);
    }

    // ---------------------------------------------------------------- text content

    [Fact]
    public void TitleAndTextStillEscapeTheMarkupCharacters()
    {
        string html = Html(new TextRun { Text = "a < b & c > d" });

        Assert.Equal("a < b & c > d", Element(html, "p").Value);
    }
}
