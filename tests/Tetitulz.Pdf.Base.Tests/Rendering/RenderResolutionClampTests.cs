// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The render-resolution clamp reports that it reduced the output (GAPS.md §2).
/// <para>
/// The clamp itself is correct and must stay — it is what stops an absurd <c>/MediaBox</c> from
/// being an out-of-memory allocation. Doing it <b>silently</b> was the defect: A4 at 1200 DPI is
/// 9 917 × 14 033 = 139 MP, so a caller asked for 1200 and received ≈1017 with no way to find out.
/// </para>
/// <para>
/// Every assertion here is on the <b>effective pixel dimensions</b> or on the reported detail,
/// never on "a PNG came back". A reduced-resolution PNG is a perfectly valid non-empty PNG, which
/// is exactly why this was invisible for as long as it was.
/// </para>
/// </summary>
public class RenderResolutionClampTests
{
    // ------------------------------------------------------------------
    //  The clamp arithmetic and its report, without allocating the bitmap
    // ------------------------------------------------------------------

    /// <summary>
    /// The area cap is the one a realistic caller hits — A4 at 1200 DPI is 139 MP against a 100 MP
    /// limit — and it cannot be driven end to end, because doing so would allocate the 400 MB
    /// bitmap the cap exists to prevent. So it is asserted on the helper directly.
    /// </summary>
    [Fact]
    public void AreaCapExceeded_ReducesDimensionsAndScale_AndReportsBoth()
    {
        int width = 9917, height = 14033;      // A4 at 1200 dpi = 139.2 MP
        float scale = 1200f / 72f;

        var reported = Capture(() => PdfPageRenderer.ClampRenderDimensions(ref width, ref height, ref scale));

        // The output really was reduced, and to within the limit.
        Assert.True((long)width * height <= PdfLimits.MaxImagePixelArea,
            $"area {(long)width * height} still exceeds {PdfLimits.MaxImagePixelArea}");
        Assert.True(width < 9917 && height < 14033);
        Assert.True(scale < 1200f / 72f);

        // ...and the caller is told, with both the requested and the effective figures, because
        // "it was reduced" without "to what" cannot be acted on.
        var (kind, detail) = Assert.Single(reported);
        Assert.Equal(RenderDiagnosticKind.RenderResolutionReduced, kind);
        Assert.Contains("9917x14033", detail);
        Assert.Contains($"{width}x{height}", detail);
    }

    /// <summary>
    /// The per-axis cap is the other branch. A 20000-px-wide page is clamped to the 16384 limit
    /// with its aspect ratio preserved.
    /// </summary>
    [Fact]
    public void AxisCapExceeded_ClampsToTheLimit_AndReports()
    {
        int width = 20000, height = 100;
        float scale = 1f;

        var reported = Capture(() => PdfPageRenderer.ClampRenderDimensions(ref width, ref height, ref scale));

        Assert.Equal(PdfLimits.MaxImageDimension, width);
        Assert.True(height < 100);                       // aspect ratio preserved, so height fell too
        Assert.Equal(RenderDiagnosticKind.RenderResolutionReduced, Assert.Single(reported).Kind);
    }

    /// <summary>
    /// The control that gives the two above their meaning: a request within both limits is passed
    /// through untouched and reports <b>nothing</b>. Without this, a helper that reported on every
    /// call would satisfy them.
    /// </summary>
    [Fact]
    public void WithinLimits_ChangesNothingAndReportsNothing()
    {
        int width = 1224, height = 1584;                 // A4-ish at 150 dpi
        float scale = 150f / 72f;

        var reported = Capture(() => PdfPageRenderer.ClampRenderDimensions(ref width, ref height, ref scale));

        Assert.Equal(1224, width);
        Assert.Equal(1584, height);
        Assert.Equal(150f / 72f, scale);
        Assert.Empty(reported);
    }

    // ------------------------------------------------------------------
    //  End to end, through a real render
    // ------------------------------------------------------------------

    /// <summary>
    /// A page whose <c>/MediaBox</c> is wider than the per-axis limit renders at the clamped size
    /// and says so. Driven through <see cref="PdfPageRenderer.RenderPageToPixels"/> so the
    /// assertion is on real output dimensions rather than on the helper's own arithmetic — and
    /// deliberately using a long thin page, so the clamped bitmap costs ~5 MB rather than the
    /// 400 MB an area-cap fixture would.
    /// </summary>
    [Fact]
    public void RenderingAnOversizedPage_ProducesTheClampedSize_AndReportsIt()
    {
        byte[] pdf = OnePagePdf(mediaBoxWidth: 20000, mediaBoxHeight: 100);

        RenderedPage page = null;
        var reported = Capture(() =>
        {
            using var ms = new MemoryStream(pdf);
            page = PdfPageRenderer.RenderPageToPixels(ms, 0, 72);
        });

        // 72 dpi means one pixel per point, so the request is 20000 px wide.
        Assert.Equal(PdfLimits.MaxImageDimension, page.Width);
        Assert.True(page.Width < 20000);

        var (kind, detail) = Assert.Single(reported);
        Assert.Equal(RenderDiagnosticKind.RenderResolutionReduced, kind);
        Assert.Contains("20000x100", detail);
    }

    /// <summary>
    /// The end-to-end control: an ordinary page renders at exactly the size asked for and reports
    /// nothing, so the test above cannot be passing on a report the renderer emits regardless.
    /// </summary>
    [Fact]
    public void RenderingAnOrdinaryPage_ReportsNoReduction()
    {
        byte[] pdf = OnePagePdf(mediaBoxWidth: 200, mediaBoxHeight: 200);

        RenderedPage page = null;
        var reported = Capture(() =>
        {
            using var ms = new MemoryStream(pdf);
            page = PdfPageRenderer.RenderPageToPixels(ms, 0, 72);
        });

        Assert.Equal(200, page.Width);
        Assert.Equal(200, page.Height);
        Assert.DoesNotContain(reported, r => r.Kind == RenderDiagnosticKind.RenderResolutionReduced);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="action"/> with a diagnostics sink attached and returns what it saw.
    /// Filtered to this one kind: the sink is process-wide, so a concurrently running test would
    /// otherwise leak its own unrelated diagnostics into these assertions.
    /// </summary>
    private static List<(RenderDiagnosticKind Kind, string Detail)> Capture(Action action)
    {
        var reported = new List<(RenderDiagnosticKind Kind, string Detail)>();

        // Filtering by KIND narrows the cross-talk but does not remove it: a concurrently running
        // test that also clamps resolution lands in this list, and Assert.Single/Assert.Empty below
        // are then claims about the whole process. That is the shape that actually failed once in
        // ImageDownsamplingTests. Filtering to the calling thread is what removes it.
        //
        // Safe here because every Capture caller renders a single page, which runs on the calling
        // thread; a batch render (RenderAllPages) raises diagnostics on worker threads and would
        // need the events collected differently.
        int ownThread = Environment.CurrentManagedThreadId;

        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            if (kind != RenderDiagnosticKind.RenderResolutionReduced) return;
            lock (reported) reported.Add((kind, detail ?? string.Empty));
        }

        RenderDiagnostics.OnEvent += Sink;
        try { action(); }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        return reported;
    }

    private static byte[] OnePagePdf(int mediaBoxWidth, int mediaBoxHeight)
    {
        const string content = "0 0 1 rg 10 10 50 50 re f";
        var acc = new List<byte>();
        var offsets = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.ASCII.GetBytes(s));
        void BeginObj(int n) { offsets[n] = acc.Count; Emit($"{n} 0 obj\n"); }

        Emit("%PDF-1.4\n");
        BeginObj(1); Emit("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObj(2); Emit("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        BeginObj(3);
        Emit($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {mediaBoxWidth} {mediaBoxHeight}] " +
             "/Contents 4 0 R /Resources << >> >>\nendobj\n");
        BeginObj(4);
        Emit($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        int xref = acc.Count;
        Emit("xref\n0 5\n0000000000 65535 f \n");
        for (int i = 1; i <= 4; i++) Emit($"{offsets[i]:D10} 00000 n \n");
        Emit("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        Emit($"startxref\n{xref}\n%%EOF\n");

        return acc.ToArray();
    }
}
