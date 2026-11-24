// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Plugins.Html;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Html.Tests;

/// <summary>
/// The adapters' <c>Extra</c> option bags.
///
/// <para>Both plugins accepted an options object and read nothing out of it, so the entire
/// configurable surface of the engines behind them was unreachable through
/// <c>PdfOperations.Export</c>/<c>Import</c> — and, worse, a caller who set a key got exactly the
/// output of a caller who set none. Every test below asserts on an <b>observable difference</b> or
/// on a <b>refusal</b>; asserting that a key "was read" is not a claim a caller can act on.</para>
/// </summary>
public class HtmlPluginOptionTests
{
    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    private static string Export(PdfDocument doc, ExportOptions? options)
    {
        using var output = new MemoryStream();
        new HtmlExportPlugin().Export(doc, output, options);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static ExportOptions With(params (string Key, object Value)[] pairs)
    {
        var options = new ExportOptions();
        foreach (var (key, value) in pairs) options.Extra[key] = value;
        return options;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Export — the keys reach the converter
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>Title</c> is the cheapest key to observe end to end: it lands in the document's
    /// <c>&lt;title&gt;</c>, which is nowhere near the default.
    /// </summary>
    [Fact]
    public void Export_honours_Title()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());

        string html = Export(doc, With(("Title", "A Deliberately Distinct Title")));

        Assert.Contains("<title>A Deliberately Distinct Title</title>", html, StringComparison.Ordinal);
        // The default it replaced, so the assertion above cannot pass by accident.
        Assert.DoesNotContain("<title>PDF Document</title>", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>ImageDpi</c> changes the pixel extent of the page container and of the embedded image —
    /// the same number the renderer used, so the check is on real output rather than on a field.
    /// </summary>
    [Fact]
    public void Export_honours_ImageDpi()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());

        int at96  = PageWidthPx(Export(doc, With(("ImageDpi", 96))));
        int at192 = PageWidthPx(Export(doc, With(("ImageDpi", 192))));

        Assert.Equal(2 * at96, at192);
    }

    /// <summary>The bag is hand-populated, so keys match case-insensitively.</summary>
    [Fact]
    public void Export_matches_keys_case_insensitively()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());

        Assert.Contains("<title>lowercase key</title>",
            Export(doc, With(("title", "lowercase key"))), StringComparison.Ordinal);
    }

    /// <summary>
    /// A string value is parsed with <see cref="CultureInfo.InvariantCulture"/>, which is what a
    /// bag populated from a config file carries.
    /// </summary>
    [Fact]
    public void Export_accepts_a_numeric_key_written_as_a_string()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());

        int fromInt    = PageWidthPx(Export(doc, With(("ImageDpi", 192))));
        int fromString = PageWidthPx(Export(doc, With(("ImageDpi", "192"))));

        Assert.Equal(fromInt, fromString);
    }

    /// <summary>
    /// <c>EmbedImages</c> reaches the converter: with it false no data URI is emitted at all.
    /// </summary>
    [Fact]
    public void Export_honours_EmbedImages()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());

        Assert.Contains("data:image/png;base64,", Export(doc, With(("EmbedImages", true))), StringComparison.Ordinal);
        Assert.DoesNotContain("data:image/png;base64,", Export(doc, With(("EmbedImages", false))), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Layout</c> selects the other conversion mode entirely — positioned semantic text rather
    /// than a page raster with an invisible overlay. The two emit different container classes.
    /// </summary>
    [Fact]
    public void Export_honours_Layout()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf("LayoutMarker"));

        string rasterized = Export(doc, With(("Layout", false)));
        string layout     = Export(doc, With(("Layout", true), ("EmbedImages", false)));

        Assert.Contains("class=\"page\"", rasterized, StringComparison.Ordinal);
        Assert.Contains("class=\"pdf-page\"", layout, StringComparison.Ordinal);
        Assert.Contains("LayoutMarker", layout, StringComparison.Ordinal);
    }

    /// <summary>No options at all stays exactly what it was — every default preserved.</summary>
    [Fact]
    public void Export_with_no_options_keeps_the_documented_defaults()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());

        string withNull  = Export(doc, null);
        string withEmpty = Export(doc, new ExportOptions());

        Assert.Contains("<title>PDF Document</title>", withNull, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,", withNull, StringComparison.Ordinal);
        Assert.Equal(withNull.Length, withEmpty.Length);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Export — refusals
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The important half. Silent, a mistyped key would be indistinguishable from an honoured one.
    /// </summary>
    [Fact]
    public void Export_refuses_an_unrecognised_key_and_names_it()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());

        var ex = Assert.Throws<ArgumentException>(() => Export(doc, With(("ImageDPIs", 300))));

        Assert.Contains("ImageDPIs", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ImageDpi", ex.Message, StringComparison.Ordinal);   // the recognised set is listed
    }

    [Fact]
    public void Export_refuses_a_value_of_the_wrong_shape()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());

        var ex = Assert.Throws<ArgumentException>(() => Export(doc, With(("ImageDpi", "not-a-number"))));

        Assert.Contains("ImageDpi", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Zero and negative DPI would produce a degenerate or exception-throwing render; refusing at
    /// the boundary names the setting instead of failing somewhere in the renderer.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-96)]
    public void Export_refuses_a_non_positive_ImageDpi(int dpi)
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());

        Assert.Throws<ArgumentException>(() => Export(doc, With(("ImageDpi", dpi))));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Import
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The bag is validated before anything is read or converted, so the refusal is observable
    /// without a browser — see <c>HtmlPluginTests</c> on why the conversion itself is not tested.
    /// </summary>
    [Fact]
    public void Import_refuses_an_unrecognised_key()
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("<p>x</p>"));
        using var output = new MemoryStream();
        var options = new ImportOptions();
        options.Extra["PageWidthInches"] = 8.5;

        var ex = Assert.Throws<ArgumentException>(
            () => new HtmlImportPlugin().Import(source, output, options));

        Assert.Contains("PageWidthInches", ex.Message, StringComparison.Ordinal);
        Assert.Contains("PageWidth", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The three sandbox switches are refused rather than accepted-and-ignored. Plugin input is
    /// untrusted, so a caller who asked to re-enable JavaScript must learn that it did not happen —
    /// silently ignoring the request reads as though the sandbox had been turned off.
    /// </summary>
    [Theory]
    [InlineData("JavaScriptEnabled")]
    [InlineData("WaitForNetworkIdle")]
    [InlineData("BlockPrivateNetworkAccess")]
    public void Import_refuses_the_security_switches(string key)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("<p>x</p>"));
        using var output = new MemoryStream();
        var options = new ImportOptions();
        options.Extra[key] = true;

        var ex = Assert.Throws<ArgumentException>(
            () => new HtmlImportPlugin().Import(source, output, options));

        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>RenderEngine</c> is parsed by name and only a defined member is accepted; an integer or a
    /// misspelling would otherwise select a member that does not exist.
    /// </summary>
    [Fact]
    public void Import_refuses_an_undefined_RenderEngine()
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("<p>x</p>"));
        using var output = new MemoryStream();
        var options = new ImportOptions();
        options.Extra["RenderEngine"] = "Managedd";

        var ex = Assert.Throws<ArgumentException>(
            () => new HtmlImportPlugin().Import(source, output, options));

        Assert.Contains("RenderEngine", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Managed", ex.Message, StringComparison.Ordinal);   // the valid names are listed
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Culture
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Numeric option values parse with <see cref="CultureInfo.InvariantCulture"/>, not the host's.
    ///
    /// <para>The test <b>runs under</b> <c>de-DE</c> rather than merely naming it: a culture test
    /// that does not change the culture asserts nothing. Under a comma-decimal culture
    /// <c>"1.5"</c> parses to 15 with <c>CurrentCulture</c> — three orders of magnitude off a page
    /// scale — and <c>"1,5"</c> parses to 1.5 instead of being refused. Both halves are asserted,
    /// because only the second one changes an exception into a silent success.</para>
    /// </summary>
    [Fact]
    public void Numeric_option_values_parse_invariantly_on_a_comma_decimal_host()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            // Sanity: the culture really is comma-decimal, or the rest of this proves nothing.
            Assert.Equal("1,5", 1.5d.ToString(CultureInfo.CurrentCulture));

            var extra = new Dictionary<string, object> { ["Scale"] = "1.5" };
            double scale = 1.0;
            PluginOptionReader.ReadPositiveDouble("T", extra, "Scale", ref scale);
            Assert.Equal(1.5d, scale, 6);

            var commaDecimal = new Dictionary<string, object> { ["Scale"] = "1,5" };
            double rejected = 1.0;
            Assert.Throws<ArgumentException>(
                () => PluginOptionReader.ReadPositiveDouble("T", commaDecimal, "Scale", ref rejected));
            Assert.Equal(1.0d, rejected);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// Integers too: the group separator a comma-decimal host would accept must not turn
    /// <c>"1,000"</c> into 1000 on one machine and refuse it on another.
    /// </summary>
    [Fact]
    public void Integer_option_values_parse_invariantly_on_a_comma_decimal_host()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var extra = new Dictionary<string, object> { ["ImageDpi"] = "300" };
            int dpi = 96;
            PluginOptionReader.ReadPositiveInt("T", extra, "ImageDpi", ref dpi);

            Assert.Equal(300, dpi);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static int PageWidthPx(string html)
    {
        var m = Regex.Match(html, @"class=""page"" style=""width:(\d+)px");
        Assert.True(m.Success, "no page container found in the exported HTML");
        return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}
