// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Plugins.Html;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Html.Tests;

/// <summary>
/// The shape of <see cref="PdfHtmlConverter.ConvertToLayoutHtml(Stream, HtmlConversionOptions)"/>'s
/// output: a page container per page, the text as real DOM content, CSS positioning, and the
/// <c>EmbedImages</c> switch.
///
/// <para><b>These lived in <c>Tetitulz.Pdf.Base.Tests</c>.</b> They test this package, so they
/// belong beside its other tests — CLAUDE.md asks for one test project per production project. The
/// split had a cost beyond tidiness: the layout path's budget and diagnostic defects (see
/// <see cref="LayoutHtmlBudgetTests"/>) sat between two test files in two assemblies, one covering
/// the raster path's budget thoroughly and the other covering this path's markup, with nothing
/// looking at the intersection. <c>Base.Tests</c> keeps its reference to this package for the
/// cross-cutting guards that genuinely need every assembly loaded.</para>
/// </summary>
public class LayoutHtmlConversionTests
{
    [Fact]
    public void ConvertToLayoutHtml_ReturnsValidHtml()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Layout Test");
        using var ms = new MemoryStream(pdf);
        string html = PdfHtmlConverter.ConvertToLayoutHtml(ms);

        Assert.NotNull(html);
        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("</html>", html);
        Assert.Contains("pdf-page", html);
    }

    [Fact]
    public void ConvertToLayoutHtml_ContainsTextContent()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("SearchableText");
        using var ms = new MemoryStream(pdf);
        string html = PdfHtmlConverter.ConvertToLayoutHtml(ms);

        Assert.Contains("SearchableText", html);
    }

    [Fact]
    public void ConvertToLayoutHtml_WithImages_ContainsBase64Png()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("WithImage");
        using var ms = new MemoryStream(pdf);
        var opts = new HtmlConversionOptions { EmbedImages = true };
        string html = PdfHtmlConverter.ConvertToLayoutHtml(ms, opts);

        Assert.Contains("data:image/png;base64,", html);
    }

    [Fact]
    public void ConvertToLayoutHtml_WithoutImages_NoBase64()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("NoImage");
        using var ms = new MemoryStream(pdf);
        var opts = new HtmlConversionOptions { EmbedImages = false };
        string html = PdfHtmlConverter.ConvertToLayoutHtml(ms, opts);

        Assert.DoesNotContain("data:image/png;base64,", html);
    }

    [Fact]
    public void ConvertToLayoutHtml_MultiPage_AllPagesPresent()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(3);
        using var ms = new MemoryStream(pdf);
        var opts = new HtmlConversionOptions { EmbedImages = false };
        string html = PdfHtmlConverter.ConvertToLayoutHtml(ms, opts);

        // Should have 3 page divs
        int count = 0;
        int idx = 0;
        while ((idx = html.IndexOf("pdf-page", idx + 1)) >= 0) count++;
        Assert.True(count >= 3);
    }

    [Fact]
    public void ConvertToLayoutHtml_HasCssPositioning()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Positioned");
        using var ms = new MemoryStream(pdf);
        var opts = new HtmlConversionOptions { EmbedImages = false };
        string html = PdfHtmlConverter.ConvertToLayoutHtml(ms, opts);

        // Text should have absolute positioning
        Assert.Contains("position:absolute", html.Replace(" ", ""));
    }

    [Fact]
    public void ConvertToLayoutHtml_CustomTitle()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("test");
        using var ms = new MemoryStream(pdf);
        var opts = new HtmlConversionOptions { EmbedImages = false, Title = "My Custom Title" };
        string html = PdfHtmlConverter.ConvertToLayoutHtml(ms, opts);

        Assert.Contains("My Custom Title", html);
    }
}
