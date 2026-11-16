// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Plugins.Svg;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Svg.Tests;

/// <summary>
/// <see cref="SvgExportPlugin"/>'s handling of the <see cref="ExportOptions"/> bag.
///
/// <para>The plugin took an options object and read nothing out of it, so the page-box selection it
/// shares with the raster renderer (<c>ResolveRenderBox</c>) was unreachable through
/// <c>PdfOperations.Export</c>, and a caller who asked for the trim box got the media box with no
/// indication the request had been dropped.</para>
///
/// <para><b>Assertions parse the export as XML.</b> A substring match on <c>width="306"</c> would
/// pass on output that is not well-formed — the failure mode this suite has actually had — so the
/// page's extent is read off a parsed attribute instead.</para>
/// </summary>
public class SvgExportPluginOptionTests
{
    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    private static string Export(PdfDocument doc, ExportOptions? options)
    {
        using var output = new MemoryStream();
        new SvgExportPlugin().Export(doc, output, options);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static ExportOptions With(string key, object value)
    {
        var options = new ExportOptions();
        options.Extra[key] = value;
        return options;
    }

    /// <summary>
    /// The width and height of the page's own <c>&lt;svg&gt;</c> viewport, read from parsed XML.
    /// </summary>
    /// <remarks>
    /// Any <c>svg</c> element carrying both attributes counts, so the assertion holds whether the
    /// export is one document per page or one root wrapping a nested <c>&lt;svg&gt;</c> per page.
    /// </remarks>
    private static (double Width, double Height) PageExtent(string markup)
    {
        XNamespace svg = "http://www.w3.org/2000/svg";
        var element = XDocument.Parse(markup).Descendants(svg + "svg")
            .LastOrDefault(e => e.Attribute("width") != null && e.Attribute("height") != null);

        Assert.True(element is not null, "the export carries no <svg> element with a width and height");

        return (double.Parse(element!.Attribute("width")!.Value, CultureInfo.InvariantCulture),
                double.Parse(element.Attribute("height")!.Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A page whose <c>/TrimBox</c> is a quarter of its <c>/MediaBox</c>, so the two choices cannot
    /// be confused for one another.
    /// </summary>
    private static byte[] PdfWithTrimBox()
    {
        const string content = "1 0 0 rg 0 0 100 100 re f";

        var pdf = new StringBuilder();
        var offsets = new int[5];
        pdf.Append("%PDF-1.4\n");

        offsets[1] = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets[3] = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                   "/TrimBox [0 0 306 396] /Contents 4 0 R /Resources << >> >>\nendobj\n");

        offsets[4] = pdf.Length;
        pdf.Append("4 0 obj\n<< /Length ").Append(content.Length).Append(" >>\nstream\n")
           .Append(content).Append("\nendstream\nendobj\n");

        int xref = pdf.Length;
        pdf.Append("xref\n0 5\n0000000000 65535 f \n");
        for (int i = 1; i <= 4; i++) pdf.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    /// <summary>
    /// <c>UseTrimBox</c> reaches the renderer and changes the exported page extent. Without the
    /// option the media box is used, which is the contrast that makes the claim falsifiable.
    /// </summary>
    [Fact]
    public void Export_honours_UseTrimBox()
    {
        using var doc = Open(PdfWithTrimBox());

        var mediaBox = PageExtent(Export(doc, null));
        var trimBox  = PageExtent(Export(doc, With("UseTrimBox", true)));

        Assert.Equal(612d, mediaBox.Width, 3);
        Assert.Equal(792d, mediaBox.Height, 3);
        Assert.Equal(306d, trimBox.Width, 3);
        Assert.Equal(396d, trimBox.Height, 3);
    }

    /// <summary>The bag is hand-populated, so keys match case-insensitively.</summary>
    [Fact]
    public void Export_matches_keys_case_insensitively()
    {
        using var doc = Open(PdfWithTrimBox());

        Assert.Equal(306d, PageExtent(Export(doc, With("usetrimbox", true))).Width, 3);
    }

    /// <summary>A value written as a string is parsed, which is what a config-driven bag carries.</summary>
    [Fact]
    public void Export_accepts_a_boolean_written_as_a_string()
    {
        using var doc = Open(PdfWithTrimBox());

        Assert.Equal(306d, PageExtent(Export(doc, With("UseTrimBox", "true"))).Width, 3);
    }

    /// <summary>
    /// The important half. A mistyped key produced byte-identical output to a correct one, so
    /// nothing short of a refusal tells the caller their setting was dropped.
    /// </summary>
    [Fact]
    public void Export_refuses_an_unrecognised_key_and_names_it()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());

        var ex = Assert.Throws<ArgumentException>(() => Export(doc, With("UseTrimBoxes", true)));

        Assert.Contains("UseTrimBoxes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("UseTrimBox", ex.Message, StringComparison.Ordinal);   // the recognised set is listed
    }

    /// <summary>
    /// A password key is refused rather than accepted: this assembly cannot decrypt — it may not
    /// reference <c>Tetitulz.Pdf.Encryption</c> — so honouring one is a promise it cannot keep.
    /// </summary>
    [Fact]
    public void Export_refuses_a_Password_key_it_could_never_honour()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());

        Assert.Throws<ArgumentException>(() => Export(doc, With("Password", "secret")));
    }

    [Fact]
    public void Export_refuses_a_value_of_the_wrong_shape()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());

        var ex = Assert.Throws<ArgumentException>(() => Export(doc, With("UseTrimBox", 1)));

        Assert.Contains("UseTrimBox", ex.Message, StringComparison.Ordinal);
        Assert.Contains("boolean", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The contrast for every refusal above: an absent or empty bag exports exactly as before.
    /// </summary>
    [Fact]
    public void Export_with_no_keys_is_unchanged()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());

        Assert.Equal(Export(doc, null), Export(doc, new ExportOptions()));
    }
}
