// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.Svg;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Svg.Tests;

/// <summary>
/// <see cref="SvgExportPlugin"/> — the <c>svg</c> export plugin.
///
/// <para>Assertions are on the emitted markup: one <c>&lt;svg&gt;</c> element per page, and drawing
/// content inside it. "A non-empty string came back" would pass with the renderer returning an
/// empty document, which is the shape of failure this plugin can actually have.</para>
/// </summary>
public class SvgExportPluginTests
{
    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    private static string Export(PdfDocument doc)
    {
        using var output = new MemoryStream();
        new SvgExportPlugin().Export(doc, output, null);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static readonly XNamespace SvgNs = "http://www.w3.org/2000/svg";

    /// <summary>
    /// Pages are the <c>&lt;svg&gt;</c> elements <b>inside</b> the single root, counted by parsing
    /// rather than by matching <c>&lt;svg</c> in the text — the substring count could not tell a
    /// root from a page, and cannot tell well-formed output from a concatenation at all.
    /// </summary>
    private static int CountPages(string markup) =>
        XDocument.Parse(markup).Root!.Elements(SvgNs + "svg").Count();

    [Fact]
    public void Export_EmitsOneSvgElementPerPage()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMultiPagePdf(3));

        Assert.Equal(3, CountPages(Export(doc)));
    }

    [Fact]
    public void Export_OfASinglePage_EmitsExactlyOne()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());

        Assert.Equal(1, CountPages(Export(doc)));
    }

    /// <summary>
    /// The page draws a filled rectangle, so the SVG must carry a drawing element for it. An
    /// <c>&lt;svg&gt;</c> wrapper with nothing inside is the failure this catches.
    /// </summary>
    [Fact]
    public void Export_CarriesThePagesDrawingContent()
    {
        using var doc = Open(MinimalPdfBuilder.CreatePdfWithRedRectangle());

        string svg = Export(doc);

        Assert.Matches(@"<(path|rect|polygon)\b", svg);
    }

    /// <summary>
    /// The plugin rewinds the caller's stream once per page. It does not own that stream, so the
    /// position has to be where the caller left it when the export returns.
    /// </summary>
    [Fact]
    public void Export_RestoresTheDocumentStreamPosition()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMultiPagePdf(2));
        const int position = 17;
        doc.BaseStream.Position = position;

        using var output = new MemoryStream();
        new SvgExportPlugin().Export(doc, output, null);

        Assert.Equal(position, doc.BaseStream.Position);
    }

    [Fact]
    public void FormatKey_AndMimeType_AreTheDocumentedContract()
    {
        var plugin = new SvgExportPlugin();

        Assert.Equal("svg", plugin.FormatKey);
        Assert.Equal("image/svg+xml", plugin.TargetMimeType);
        Assert.False(string.IsNullOrWhiteSpace(plugin.Name));
        Assert.NotNull(plugin.Version);
    }

    [Fact]
    public void Export_WithNullArguments_Throws()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();
        var plugin = new SvgExportPlugin();

        Assert.Throws<ArgumentNullException>(() => plugin.Export(null!, output, null));
        Assert.Throws<ArgumentNullException>(() => plugin.Export(doc, null!, null));
    }
}
