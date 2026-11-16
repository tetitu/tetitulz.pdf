// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Plugins.Html;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Html.Tests;

/// <summary>
/// What <see cref="PdfHtmlConverter.ConvertToHtml(Stream, HtmlConversionOptions)"/> does when a
/// page's image is <b>not</b> embedded, and whether it says so.
///
/// <para>Two defects lived here. The cumulative base-64 budget, on tripping, rewrote every later
/// page to <c>&lt;img src="page_N.png"&gt;</c> — files the stream entry point has no directory to
/// write and never wrote, so the caller got a string that looked complete with the last pages
/// showing broken-image icons, and nothing was thrown, set or reported. And the per-page render was
/// unconditional, so <c>EmbedImages=false</c> paid a full rasterize plus PNG encode per page for
/// bytes dropped one statement later — which made <see cref="PdfHtmlConverter.ConvertToHtmlFiles"/>
/// render every page twice.</para>
/// </summary>
public class HtmlImageEmbeddingTests
{
    private static int DataUriCount(string html) => Regex.Matches(html, "data:image/png;base64,").Count;
    private static int ExternalPngRefCount(string html) => Regex.Matches(html, @"src=""page_\d+\.png""").Count;
    // The element, not the CSS rule that styles it — the stylesheet names the class once too, so a
    // bare class-name count is one too high on every document.
    private static int PlaceholderCount(string html) =>
        Regex.Matches(html, @"<div class=""page-image-missing"">").Count;

    // ────────────────────────────────────────────────────────────────────────
    //  The budget
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A budget of one byte cannot fit page 1's image, so every page degrades. The claim is that
    /// the degraded pages carry <b>no</b> <c>page_N.png</c> reference: nothing wrote those files,
    /// so naming them is the defect.
    /// </summary>
    [Fact]
    public void An_exceeded_budget_never_points_at_png_files_that_were_not_written()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(3));

        string html = PdfHtmlConverter.ConvertToHtml(input,
            new HtmlConversionOptions { MaxEmbeddedImageBytes = 1 });

        Assert.Equal(0, ExternalPngRefCount(html));
        Assert.Equal(0, DataUriCount(html));
        Assert.Equal(3, PlaceholderCount(html));
    }

    /// <summary>
    /// The degradation reaches the caller. Before, nothing was thrown and no <c>Last*</c> slot was
    /// set, so a 300-page export that lost 120 pages' images looked identical to a complete one.
    /// </summary>
    [Fact]
    public void An_exceeded_budget_is_reported_with_the_page_it_happened_at()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(3));

        PdfHtmlConverter.ConvertToHtml(input, new HtmlConversionOptions { MaxEmbeddedImageBytes = 1 });

        string? warning = PdfHtmlConverter.LastConversionWarning;
        Assert.False(string.IsNullOrEmpty(warning), "the budget was exceeded and nothing was reported");
        Assert.Contains("budget", warning!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("page 1", warning!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3 of 3", warning!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cap is reachable at all — it used to be a private <c>const</c>. A generous budget embeds
    /// every page, which is the contrast that makes the test above mean something.
    /// </summary>
    [Fact]
    public void A_budget_large_enough_embeds_every_page_and_reports_nothing()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(3));

        string html = PdfHtmlConverter.ConvertToHtml(input,
            new HtmlConversionOptions { MaxEmbeddedImageBytes = 512L * 1024 * 1024 });

        Assert.Equal(3, DataUriCount(html));
        Assert.Equal(0, PlaceholderCount(html));
        Assert.Null(PdfHtmlConverter.LastConversionWarning);
    }

    /// <summary>
    /// The slot is per-call. A warning left over from a degraded conversion would tell the next
    /// caller their complete document was truncated.
    /// </summary>
    [Fact]
    public void A_complete_conversion_clears_a_previous_ones_warning()
    {
        using (var degraded = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(2)))
            PdfHtmlConverter.ConvertToHtml(degraded, new HtmlConversionOptions { MaxEmbeddedImageBytes = 1 });
        Assert.NotNull(PdfHtmlConverter.LastConversionWarning);

        using (var complete = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf()))
            PdfHtmlConverter.ConvertToHtml(complete, new HtmlConversionOptions());

        Assert.Null(PdfHtmlConverter.LastConversionWarning);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  EmbedImages = false
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The stream entry point writes no files, so it must not name any. <c>page_N.png</c> here was
    /// the same broken reference the budget path produced, just reached a different way.
    /// </summary>
    [Fact]
    public void The_stream_entry_point_with_EmbedImages_false_names_no_image_files()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(2));

        string html = PdfHtmlConverter.ConvertToHtml(input,
            new HtmlConversionOptions { EmbedImages = false });

        Assert.Equal(0, ExternalPngRefCount(html));
        Assert.Equal(2, PlaceholderCount(html));
        Assert.Contains("ConvertToHtmlFiles", PdfHtmlConverter.LastConversionWarning!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one path that really does write <c>page_N.png</c> keeps referencing them — and the files
    /// are there. This is what stops the fix above from being "delete the external-file mode".
    /// </summary>
    [Fact]
    public void ConvertToHtmlFiles_still_references_the_png_files_it_writes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tetitulz-html-" + Guid.NewGuid().ToString("N"));
        string pdfPath = Path.Combine(dir, "in.pdf");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(pdfPath, MinimalPdfBuilder.CreateMultiPagePdf(2));

            PdfHtmlConverter.ConvertToHtmlFiles(pdfPath, dir);

            string html = File.ReadAllText(Path.Combine(dir, "index.html"));
            Assert.Equal(2, ExternalPngRefCount(html));
            Assert.Equal(0, PlaceholderCount(html));

            // The reference is only correct because the file exists.
            Assert.True(File.Exists(Path.Combine(dir, "page_1.png")));
            Assert.True(File.Exists(Path.Combine(dir, "page_2.png")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  The render that used to happen anyway
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>EmbedImages=false</c> must not rasterize.
    ///
    /// <para>Asserted through <see cref="RenderDiagnostics"/> rather than a stopwatch: a wall-clock
    /// assertion is a flake, and "the HTML is the same either way" is true whether or not the
    /// render happened. The fixture draws a PostScript XObject, which with no
    /// <c>IPostScriptPlugin</c> registered makes the renderer report
    /// <see cref="RenderDiagnosticKind.PostScriptUnsupported"/> exactly when — and only when — a
    /// page is actually rasterized. So the diagnostic is a direct witness to the render.</para>
    ///
    /// <para>The handler filters to this thread, because the sink is process-wide and a neighbour
    /// test legitimately reporting one would otherwise fail this assertion (CLAUDE.md).</para>
    /// </summary>
    [Fact]
    public void Converting_with_EmbedImages_false_does_not_rasterize_any_page()
    {
        byte[] pdf = PdfWithPostScriptXObject();
        int thisThread = Environment.CurrentManagedThreadId;

        Assert.True(CountRenderReports(pdf, embedImages: true, thisThread) > 0,
            "the fixture never reached the renderer, so the negative case below proves nothing");

        Assert.Equal(0, CountRenderReports(pdf, embedImages: false, thisThread));
    }

    private static int CountRenderReports(byte[] pdf, bool embedImages, int thisThread)
    {
        var seen = new List<RenderDiagnosticKind>();
        void Handler(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != thisThread) return;
            if (kind == RenderDiagnosticKind.PostScriptUnsupported) seen.Add(kind);
        }

        RenderDiagnostics.OnEvent += Handler;
        try
        {
            using var input = new MemoryStream(pdf);
            PdfHtmlConverter.ConvertToHtml(input, new HtmlConversionOptions
            {
                EmbedImages = embedImages,
                IncludeText = false,
            });
        }
        finally
        {
            RenderDiagnostics.OnEvent -= Handler;
        }
        return seen.Count;
    }

    /// <summary>
    /// One page drawing a <c>/Subtype /PS</c> form XObject. With no PostScript plugin registered
    /// the renderer draws its placeholder and reports; nothing else in this conversion touches the
    /// renderer, so the report count is the render count.
    /// </summary>
    private static byte[] PdfWithPostScriptXObject()
    {
        const string content = "q /Ps1 Do Q";
        const string ps = "0 0 moveto";

        var pdf = new System.Text.StringBuilder();
        var offsets = new int[7];
        pdf.Append("%PDF-1.4\n");

        offsets[1] = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets[3] = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R " +
                   "/Resources << /XObject << /Ps1 5 0 R >> >> >>\nendobj\n");

        offsets[4] = pdf.Length;
        pdf.Append("4 0 obj\n<< /Length ").Append(content.Length).Append(" >>\nstream\n")
           .Append(content).Append("\nendstream\nendobj\n");

        offsets[5] = pdf.Length;
        pdf.Append("5 0 obj\n<< /Type /XObject /Subtype /PS /Length ").Append(ps.Length)
           .Append(" >>\nstream\n").Append(ps).Append("\nendstream\nendobj\n");

        int xref = pdf.Length;
        pdf.Append("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++) pdf.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return System.Text.Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
