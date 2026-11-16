// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.Html;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Html.Tests;

/// <summary>
/// The HTML export and import plugins.
///
/// <para>Export assertions are on the markup — a document element and the page's own text — rather
/// than on the byte count, because an exporter that emitted only a boilerplate skeleton would
/// satisfy any length check while carrying none of the document.</para>
///
/// <para><b>Import's conversion IS tested — in <c>HtmlImportConversionTests</c>.</b> It was not,
/// and the reason recorded here was that the plugin "converts through Playwright's headless
/// browser by design", which resolves one <i>pinned build</i>, making a skip condition unreliable
/// and a test that can essentially never turn green noise rather than signal. That was true when
/// it was written and stopped being true when <c>HtmlToPdfOptions.RenderEngine</c> came to default
/// to <c>Managed</c>: the plugin's default path needs no browser at all. <b>A skip justified by a
/// fact that has since changed leaves the shipping default with no coverage</b>, which is what
/// happened here. What the plugin adds on top — the format key, argument validation, UTF-8
/// decoding — is covered there and below; its security defaults (JavaScript disabled, no
/// network-idle wait, because plugin input is untrusted) are still asserted where they are set
/// rather than through a browser round trip.</para>
/// </summary>
public class HtmlPluginTests
{
    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    private static string Export(byte[] pdf)
    {
        using var doc = Open(pdf);
        using var output = new MemoryStream();
        new HtmlExportPlugin().Export(doc, output, null);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    [Fact]
    public void Export_ProducesAnHtmlDocument()
    {
        string html = Export(MinimalPdfBuilder.CreateMinimalPdf());

        Assert.Matches(@"<(!DOCTYPE\s+html|html)\b", html);
        Assert.Contains("</html>", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Export_CarriesThePagesText()
    {
        string html = Export(MinimalPdfBuilder.CreateMinimalPdf("UniqueMarkerText"));

        Assert.Contains("UniqueMarkerText", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_OfAMultiPageDocument_CarriesEveryPage()
    {
        string html = Export(MinimalPdfBuilder.CreateMultiPagePdf(3));

        // Each page draws "Page N", and the text layer puts every word in a span of its own. This
        // asserted "Page1" until extraction ended a word at the drawn space.
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, ">Page</span>").Count);
        for (int page = 1; page <= 3; page++)
            Assert.Contains(">" + page + "</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_RestoresTheDocumentStreamPosition()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());
        const int position = 31;
        doc.BaseStream.Position = position;

        using var output = new MemoryStream();
        new HtmlExportPlugin().Export(doc, output, null);

        Assert.Equal(position, doc.BaseStream.Position);
    }

    [Fact]
    public void ExportAndImport_ShareTheFormatKey()
    {
        Assert.Equal("html", new HtmlExportPlugin().FormatKey);
        Assert.Equal("html", new HtmlImportPlugin().FormatKey);
    }

    [Fact]
    public void ExportPlugin_DeclaresTheHtmlMimeType()
    {
        Assert.Equal("text/html", new HtmlExportPlugin().TargetMimeType);
    }

    [Fact]
    public void Export_WithNullArguments_Throws()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();
        var plugin = new HtmlExportPlugin();

        Assert.Throws<ArgumentNullException>(() => plugin.Export(null!, output, null));
        Assert.Throws<ArgumentNullException>(() => plugin.Export(doc, null!, null));
    }

    /// <summary>
    /// Argument validation happens before the stream is read, so it is observable without the
    /// browser — see the note on the class about why the conversion itself is not tested here.
    /// </summary>
    [Fact]
    public void Import_WithNullArguments_Throws()
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("<p>x</p>"));
        using var output = new MemoryStream();
        var plugin = new HtmlImportPlugin();

        Assert.Throws<ArgumentNullException>(() => plugin.Import(null!, output, null));
        Assert.Throws<ArgumentNullException>(() => plugin.Import(source, null!, null));
    }
}
