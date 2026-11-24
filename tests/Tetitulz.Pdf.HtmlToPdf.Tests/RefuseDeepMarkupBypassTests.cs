// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// <see cref="HtmlToBuilderMapper.RefuseDeepMarkup"/> counts the depth the HTML tree builder builds, so
/// three shapes that would slip a hundred-thousand-deep document past a count of start tags against end
/// tags — and into AngleSharp's tree builder, which is quadratic in open list items — are refused.
/// </summary>
/// <remarks>
/// A trailing <c>/&gt;</c> on an HTML element is not self-closing (HTML opens the element); an end tag
/// with no matching open closes nothing; and a leading <c>&lt;!--&gt;</c> is a whole (empty) comment, not
/// one running to a <c>--&gt;</c> the file never writes. Read otherwise, each lets the fuzzer's
/// <c>&lt;ol&gt;&lt;li&gt;</c> × 100 000 through in disguise.
/// </remarks>
public class RefuseDeepMarkupBypassTests
{
    private const int Past = HtmlToBuilderMapper.MaxMarkupNesting + 2000;

    private static string Repeat(string unit, int times)
    {
        var sb = new StringBuilder(unit.Length * times);
        for (int i = 0; i < times; i++) sb.Append(unit);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ the three bypasses

    /// <summary>A trailing <c>/&gt;</c> self-closes nothing on an HTML element: these open, and nest.</summary>
    [Fact]
    public void SelfClosingSolidusOnHtmlElements_IsStillCounted()
    {
        string html = "<html><body>" + Repeat("<ol/><li/>", Past);
        var ex = Assert.Throws<HtmlToPdfException>(() => HtmlToBuilderMapper.RefuseDeepMarkup(html));
        Assert.Contains("nests more than", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>An end tag matching no open element is ignored, so it cannot keep the stack shallow.</summary>
    [Fact]
    public void EndTagsThatCloseNothing_DoNotLowerTheDepth()
    {
        string html = "<html><body>" + Repeat("<li></x></x>", Past);
        var ex = Assert.Throws<HtmlToPdfException>(() => HtmlToBuilderMapper.RefuseDeepMarkup(html));
        Assert.Contains("nests more than", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A leading empty comment <c>&lt;!--&gt;</c> ends there; the scan reads on and counts the rest.</summary>
    [Theory]
    [InlineData("<!-->")]
    [InlineData("<!--->")]
    [InlineData("<!-- a --!>")]
    public void AnEmptyOrBangTerminatedComment_DoesNotStopTheScan(string comment)
    {
        string html = "<html><body>" + comment + Repeat("<section>", Past);
        var ex = Assert.Throws<HtmlToPdfException>(() => HtmlToBuilderMapper.RefuseDeepMarkup(html));
        Assert.Contains("nests more than", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>End to end, through the public door: the disguised document is refused, not parsed.</summary>
    [Fact]
    public void ThroughThePublicDoor_ADisguisedDeepDocument_IsRefused()
    {
        string html = "<html><body>" + Repeat("<ol/><li/>", Past) + "deep</body></html>";
        using var output = new MemoryStream();
        var ex = Assert.Throws<HtmlToPdfException>(() => HtmlToPdfConverter.ConvertHtml(
            html, output, new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed }));
        Assert.Contains("nests more than", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the controls

    /// <summary>
    /// A real page is not refused: a self-closed foreign element inside <c>&lt;svg&gt;</c> opens nothing,
    /// so thousands of sibling <c>&lt;rect/&gt;</c> are one deep, not thousands. An overcount here would
    /// refuse a page a person wrote.
    /// </summary>
    [Fact]
    public void SelfClosedForeignElements_StayWithinTheDepthCap()
    {
        string html = "<html><body><svg>" + Repeat("<rect/>", Past) + "</svg></body></html>";
        HtmlToBuilderMapper.RefuseDeepMarkup(html);   // does not throw
    }

    /// <summary>
    /// The matching-end-tag control: opened and closed in pairs, the document is as deep as one element,
    /// however many pairs there are — the stack pops on the match.
    /// </summary>
    [Fact]
    public void BalancedSiblings_StayWithinTheDepthCap_HoweverMany()
    {
        string html = "<html><body>" + Repeat("<p>t</p>", Past) + "</body></html>";
        HtmlToBuilderMapper.RefuseDeepMarkup(html);   // does not throw
    }

    /// <summary>The acceptance control: a document just inside the cap converts.</summary>
    [Fact]
    public void AMarkupJustInsideTheCap_IsNotRefused()
    {
        string html = "<html><body>" + Repeat("<div>", HtmlToBuilderMapper.MaxMarkupNesting - 2) + "deep";
        HtmlToBuilderMapper.RefuseDeepMarkup(html);   // does not throw
    }
}
