// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Plugins.Html;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Html.Tests;

/// <summary>
/// <see cref="PdfHtmlConverter.ConvertToLayoutHtml(Stream, HtmlConversionOptions)"/> against the two
/// guarantees its sibling <see cref="PdfHtmlConverter.ConvertToHtml(Stream, HtmlConversionOptions)"/>
/// already made, and did not.
///
/// <para><b>Both were live defects, and neither was visible from the other path's tests.</b>
/// <c>HtmlImageEmbeddingTests</c> covers the cumulative base-64 budget thoroughly — on the raster
/// path only — and the layout path's own tests (which lived in <c>Tetitulz.Pdf.Base.Tests</c>,
/// against a different assembly) covered the <c>EmbedImages</c> switch and nothing else. So:</para>
/// <list type="number">
///   <item><description><c>MaxEmbeddedImageBytes</c> was <b>ignored outright</b>. The layout path
///   embedded a PNG per page unconditionally. Measured on a five-page document with the budget set
///   to 1 KiB: 494 KiB of output against the raster path's 52 KiB. The option is reachable from
///   <see cref="HtmlExportPlugin"/>, which <i>accepts</i> the key and then routes to the path that
///   drops it — the accepted-and-ignored failure <c>PluginOptionReader</c> exists to
///   prevent.</description></item>
///   <item><description>The <c>[ThreadStatic]</c> warning slot was <b>never cleared</b> by this
///   path, so a layout conversion following a degraded raster one reported the earlier call's
///   message as its own — the stale-slot shape CLAUDE.md records for
///   <c>LastPermsValidation</c>.</description></item>
/// </list>
/// </summary>
public class LayoutHtmlBudgetTests
{
    private static int DataUriCount(string html) => Regex.Matches(html, "data:image/png;base64,").Count;

    private static string Layout(int pages, HtmlConversionOptions options)
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(pages));
        return PdfHtmlConverter.ConvertToLayoutHtml(input, options);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  The budget
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A budget of one byte cannot fit page 1's image, so no page embeds one.
    /// </summary>
    [Fact]
    public void An_exceeded_budget_embeds_no_page_image_on_the_layout_path()
    {
        string html = Layout(3, new HtmlConversionOptions { MaxEmbeddedImageBytes = 1 });

        Assert.Equal(0, DataUriCount(html));
    }

    /// <summary>
    /// The acceptance control, without which the refusal above passes with embedding deleted: a
    /// budget large enough embeds every page. This is the pairing CLAUDE.md requires of any file of
    /// refusals.
    /// </summary>
    [Fact]
    public void A_budget_large_enough_embeds_every_page_on_the_layout_path()
    {
        string html = Layout(3, new HtmlConversionOptions { MaxEmbeddedImageBytes = 512L * 1024 * 1024 });

        Assert.Equal(3, DataUriCount(html));
        Assert.Null(PdfHtmlConverter.LastConversionWarning);
    }

    /// <summary>
    /// The budget bounds the OUTPUT, which is the whole reason it exists — so the assertion is on
    /// the size of the document, not only on the count of images inside it. A budgeted conversion
    /// must be dramatically smaller than an unbudgeted one over the same input.
    /// </summary>
    [Fact]
    public void A_budgeted_layout_conversion_is_far_smaller_than_an_unbudgeted_one()
    {
        string budgeted = Layout(3, new HtmlConversionOptions { MaxEmbeddedImageBytes = 1 });
        string full     = Layout(3, new HtmlConversionOptions { MaxEmbeddedImageBytes = 512L * 1024 * 1024 });

        // The text layer is identical in both; only the page images differ, and they dominate.
        Assert.True(budgeted.Length * 2 < full.Length,
            $"budgeted output ({budgeted.Length} chars) is not materially smaller than the " +
            $"unbudgeted one ({full.Length} chars), so the budget bounded nothing");
    }

    /// <summary>
    /// The degradation reaches the caller on this path too. It reported nothing at all before:
    /// the layout conversion never wrote the slot.
    /// </summary>
    [Fact]
    public void An_exceeded_budget_is_reported_by_the_layout_path()
    {
        Layout(3, new HtmlConversionOptions { MaxEmbeddedImageBytes = 1 });

        string? warning = PdfHtmlConverter.LastConversionWarning;
        Assert.False(string.IsNullOrEmpty(warning),
            "the layout path exceeded the budget and reported nothing");
        Assert.Contains("budget", warning!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("page 1", warning!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3 of 3", warning!, StringComparison.Ordinal);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  The warning slot
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A clean layout conversion does not inherit a previous conversion's warning.
    /// </summary>
    /// <remarks>
    /// <b>The cross-path pairing is the point.</b> The degrading call is deliberately a
    /// <c>ConvertToHtml</c> and the clean one a <c>ConvertToLayoutHtml</c>: a layout path that never
    /// wrote the slot would report the raster call's message as its own, and a same-path pair could
    /// not see that.
    /// </remarks>
    [Fact]
    public void A_clean_layout_conversion_does_not_inherit_a_raster_conversions_warning()
    {
        using (var degraded = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(2)))
            PdfHtmlConverter.ConvertToHtml(degraded, new HtmlConversionOptions { MaxEmbeddedImageBytes = 1 });
        Assert.NotNull(PdfHtmlConverter.LastConversionWarning);

        Layout(2, new HtmlConversionOptions { MaxEmbeddedImageBytes = 512L * 1024 * 1024 });

        Assert.Null(PdfHtmlConverter.LastConversionWarning);
    }

    /// <summary>
    /// A conversion that <b>throws</b> still clears the slot, so the failure cannot be read as the
    /// previous call's degradation.
    /// </summary>
    /// <remarks>
    /// <b>This is the case that makes the entry-time reset load-bearing, and it took a surviving
    /// mutation to find.</b> Both conversions assign the slot as their last statement, so on a
    /// successful call the assignment alone clears a stale value — deleting
    /// <c>_lastConversionWarning = null</c> from the shared prologue left the whole suite green.
    /// The reset only earns its place when the conversion does not reach that final assignment,
    /// which is exactly the shape CLAUDE.md's rule is about: a slot a caller reads after an
    /// operation must be cleared at the <i>start</i> of that operation, on every path.
    /// </remarks>
    [Fact]
    public void A_conversion_that_throws_clears_a_previous_conversions_warning()
    {
        using (var degraded = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(2)))
            PdfHtmlConverter.ConvertToHtml(degraded, new HtmlConversionOptions { MaxEmbeddedImageBytes = 1 });
        Assert.NotNull(PdfHtmlConverter.LastConversionWarning);

        using var garbage = new MemoryStream(Encoding.ASCII.GetBytes("this is not a PDF at all"));
        Assert.ThrowsAny<Exception>(
            () => PdfHtmlConverter.ConvertToLayoutHtml(garbage, new HtmlConversionOptions()));

        Assert.Null(PdfHtmlConverter.LastConversionWarning);
    }

    /// <summary>
    /// <c>EmbedImages = false</c> is not a degradation on this path and must not report one.
    /// </summary>
    /// <remarks>
    /// It is the ordinary way to use the layout conversion — positioned semantic text with no raster
    /// underlay — unlike the raster path, where a page with no image is the whole content missing.
    /// A channel that fires on the intended use is one a caller learns to ignore.
    /// </remarks>
    [Fact]
    public void EmbedImages_false_is_not_reported_as_a_degradation_by_the_layout_path()
    {
        string html = Layout(2, new HtmlConversionOptions { EmbedImages = false });

        Assert.Equal(0, DataUriCount(html));
        Assert.Null(PdfHtmlConverter.LastConversionWarning);
        // The text is still there — that is what makes this a complete conversion rather than a
        // degraded one.
        Assert.Contains("pdf-line", html, StringComparison.Ordinal);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  The Stream overloads
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The stream-writing overloads produce exactly what the string-returning ones do.
    /// </summary>
    /// <remarks>
    /// They exist to avoid materialising the whole document as a string and then encoding that to a
    /// second full copy — at the default 512 MiB image budget the builder's chunks, the string and
    /// the byte array were all live at once. Byte-identical output is what makes that a pure memory
    /// change rather than a second markup writer that can drift.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_stream_overload_writes_exactly_what_the_string_overload_returns(bool layout)
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(2);

        string viaString;
        using (var input = new MemoryStream(pdf))
            viaString = layout
                ? PdfHtmlConverter.ConvertToLayoutHtml(input, new HtmlConversionOptions())
                : PdfHtmlConverter.ConvertToHtml(input, new HtmlConversionOptions());

        using var output = new MemoryStream();
        using (var input = new MemoryStream(pdf))
        {
            if (layout) PdfHtmlConverter.ConvertToLayoutHtml(input, output, new HtmlConversionOptions());
            else        PdfHtmlConverter.ConvertToHtml(input, output, new HtmlConversionOptions());
        }

        Assert.Equal(viaString, Encoding.UTF8.GetString(output.ToArray()));
    }

    /// <summary>
    /// The stream overload writes no byte-order mark and leaves the caller's stream open.
    /// </summary>
    /// <remarks>
    /// Both matter to <see cref="HtmlExportPlugin"/>, which hands it the host's output stream and
    /// must not close it — and whose previous <c>Encoding.UTF8.GetBytes</c> emitted no BOM either,
    /// so emitting one here would change every exported file's first three bytes.
    /// </remarks>
    [Fact]
    public void The_stream_overload_writes_no_bom_and_leaves_the_stream_open()
    {
        using var output = new MemoryStream();
        using (var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf()))
            PdfHtmlConverter.ConvertToHtml(input, output, new HtmlConversionOptions());

        byte[] bytes = output.ToArray();
        Assert.True(bytes.Length > 3);
        Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "a UTF-8 BOM was written; the string path this replaced emitted none");

        // Still writable — a closed stream throws here.
        output.WriteByte(0x20);
    }
}
