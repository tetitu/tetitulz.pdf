// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The render entry points added or reshaped alongside the format work: parallel batch rendering
/// with per-worker state, cancellation, size-driven rendering, the render-size query, stream and
/// callback output, and §12.5.3 annotation visibility under <see cref="RenderIntent.Print"/>.
/// </summary>
public class RenderEntryPointTests
{
    private const int Dpi = 72;
    private const int PageCount = 6;

    // ------------------------------------------------------- batch correctness

    /// <summary>
    /// The batch paths render every page on worker threads with their own document and caches. The
    /// load-bearing assertion is that each page still matches the same page rendered alone: sharing
    /// state within a worker is exactly the change that produces <i>nearly</i> the right output.
    /// </summary>
    [Theory]
    [InlineData(1)]     // sequential, on the calling thread
    [InlineData(2)]
    [InlineData(0)]     // auto — ProcessorCount
    public void RenderAllPages_MatchesPerPageRenderingAtEveryWorkerCount(int maxThreads)
    {
        byte[] pdf = MultiPagePdf();
        var options = new PdfRenderOptions { MaxRenderThreads = maxThreads };

        byte[][] batch = PdfPageRenderer.RenderAllPages(new MemoryStream(pdf), Dpi, options);
        Assert.Equal(PageCount, batch.Length);

        for (int i = 0; i < PageCount; i++)
        {
            using SKBitmap fromBatch = Decode(batch[i]);
            using SKBitmap alone = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), i, Dpi));

            Assert.Equal(alone.Width, fromBatch.Width);
            Assert.Equal(alone.Height, fromBatch.Height);

            int differing = 0;
            for (int y = 0; y < alone.Height; y++)
                for (int x = 0; x < alone.Width; x++)
                    if (alone.GetPixel(x, y) != fromBatch.GetPixel(x, y)) differing++;

            Assert.True(differing == 0,
                $"page {i} at {maxThreads} worker(s) differs from its standalone render " +
                $"in {differing} pixels");
        }
    }

    /// <summary>
    /// The JPEG batch path used to be a sequential loop opening a document per page while its PNG
    /// sibling shared one; both now go through the same core. Correctness first: every page must
    /// match the single-page JPEG render.
    /// </summary>
    [Fact]
    public void RenderAllPagesAsJpg_MatchesPerPageJpegRendering()
    {
        byte[] pdf = MultiPagePdf();

        byte[][] batch = PdfPageRenderer.RenderAllPagesAsJpg(new MemoryStream(pdf), Dpi);
        Assert.Equal(PageCount, batch.Length);

        for (int i = 0; i < PageCount; i++)
        {
            using SKBitmap fromBatch = Decode(batch[i]);
            using SKBitmap alone = Decode(PdfPageRenderer.RenderPageAsJpg(new MemoryStream(pdf), i, Dpi));

            Assert.Equal(alone.Width, fromBatch.Width);
            int differing = 0;
            for (int y = 0; y < alone.Height; y++)
                for (int x = 0; x < alone.Width; x++)
                    if (!PixelAssert.Near(alone.GetPixel(x, y), fromBatch.GetPixel(x, y), 2))
                        differing++;
            Assert.True(differing == 0, $"JPEG page {i} differs from its standalone render");
        }
    }

    /// <summary>
    /// Every page must be distinct and land at its own index. A batch that raced would most likely
    /// show up as a duplicated or mis-ordered page, which a per-page pixel comparison against the
    /// <i>wrong</i> page would catch only by luck; this asserts the mapping directly.
    /// </summary>
    [Fact]
    public void RenderAllPages_PlacesEachPageAtItsOwnIndex()
    {
        byte[] pdf = MultiPagePdf();
        byte[][] batch = PdfPageRenderer.RenderAllPages(new MemoryStream(pdf), Dpi);

        var inkPerPage = new List<int>();
        for (int i = 0; i < batch.Length; i++)
        {
            using SKBitmap bmp = Decode(batch[i]);
            inkPerPage.Add(PixelAssert.CountInk(bmp));
        }

        // The fixture draws i+1 bars on page i, so ink is strictly increasing. A shuffled or
        // duplicated result cannot satisfy that.
        for (int i = 1; i < inkPerPage.Count; i++)
            Assert.True(inkPerPage[i] > inkPerPage[i - 1],
                $"page {i} has {inkPerPage[i]} ink pixels, not more than page {i - 1} " +
                $"({inkPerPage[i - 1]}) — pages are mis-ordered or duplicated");
    }

    /// <summary>The callback overload must deliver every page exactly once, and hold none of them.</summary>
    [Fact]
    public void RenderAllPagesToImages_CallbackReceivesEveryPageExactlyOnce()
    {
        byte[] pdf = MultiPagePdf();
        var seen = new Dictionary<int, byte[]>();

        int count = PdfPageRenderer.RenderAllPagesToImages(new MemoryStream(pdf),
            PdfImageFormat.Png, (index, bytes) => seen[index] = bytes, Dpi);

        Assert.Equal(PageCount, count);
        Assert.Equal(PageCount, seen.Count);
        for (int i = 0; i < PageCount; i++)
        {
            Assert.True(seen.ContainsKey(i), $"page {i} was never delivered");
            using SKBitmap fromCallback = Decode(seen[i]);
            using SKBitmap alone = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), i, Dpi));
            Assert.Equal(PixelAssert.CountInk(alone), PixelAssert.CountInk(fromCallback));
        }
    }

    // ------------------------------------------------------------ cancellation

    /// <summary>
    /// A token cancelled before the call must stop the batch rather than render it.
    /// </summary>
    [Fact]
    public void RenderAllPages_AlreadyCancelledTokenThrowsWithoutRendering()
    {
        byte[] pdf = MultiPagePdf();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            PdfPageRenderer.RenderAllPages(new MemoryStream(pdf), Dpi, cts.Token));
    }

    /// <summary>
    /// Cancellation must reach <i>inside</i> a single page's content-stream loop, not merely the
    /// page boundary — the whole point being that one enormous page can be abandoned.
    /// </summary>
    /// <remarks>
    /// <para>The token is cancelled from the diagnostic sink as soon as the render is under way, so
    /// the page is genuinely mid-flight. The assertion is that the call throws rather than
    /// returning an image: a render that ignored the token would produce a perfectly good PNG.</para>
    /// <para>It is also the regression test for the interpreter's per-operator <c>catch</c>, which
    /// catches <see cref="Exception"/> and would otherwise swallow the cancellation and carry on to
    /// the last operator.</para>
    /// </remarks>
    [Fact]
    public void RenderPage_CancellationInsideTheContentStreamAbandonsTheRender()
    {
        byte[] pdf = ManyOperatorsPage();
        using var cts = new CancellationTokenSource();

        int reports = 0;
        int callingThread = Environment.CurrentManagedThreadId;
        void Handler(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != callingThread) return;
            reports++;
            cts.Cancel();
        }

        // The fixture draws an undecodable image first, which reports — and a report can only
        // happen once the content stream is executing, so cancelling from the handler trips the
        // token mid-stream rather than at the page boundary the outer check already passed.
        RenderDiagnostics.OnEvent += Handler;
        Exception thrown;
        try
        {
            thrown = Record.Exception(() =>
                PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi, null, cts.Token));
        }
        finally { RenderDiagnostics.OnEvent -= Handler; }

        // Asserted rather than assumed: if the fixture stopped reporting, the token would never be
        // cancelled and the test below would be checking nothing.
        Assert.True(reports > 0,
            "the fixture reported no diagnostic, so cancellation was never triggered mid-stream");
        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
    }

    /// <summary>An uncancelled token must change nothing at all.</summary>
    [Fact]
    public void RenderPage_UncancelledTokenProducesTheSameImage()
    {
        byte[] pdf = MultiPagePdf();
        byte[] withToken = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi, null,
                                                      CancellationToken.None);
        byte[] without = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi);
        Assert.Equal(without, withToken);
    }

    // ------------------------------------------------------- size-driven render

    /// <summary>
    /// The reported size must be the size the renderer actually produces. Reporting geometry
    /// separately from computing it is how the two drift, so this compares them directly.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void GetRenderSize_MatchesTheRenderedImageIncludingRotation(int rotate)
    {
        byte[] pdf = SinglePage("0 0 0 rg 100 100 200 300 re f", rotate);

        PageRenderSize size = PdfPageRenderer.GetRenderSize(new MemoryStream(pdf), 0, 150);
        using SKBitmap bmp = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 150));

        Assert.Equal(bmp.Width,  size.WidthPixels);
        Assert.Equal(bmp.Height, size.HeightPixels);
        Assert.Equal(rotate, size.Rotation);
        Assert.InRange(size.WidthPoints,  611f, 613f);
        Assert.InRange(size.HeightPoints, 791f, 793f);
    }

    /// <summary>Fitting to a width must produce that width, and preserve the aspect ratio.</summary>
    [Fact]
    public void RenderPageToFit_HitsTheRequestedWidthAndKeepsAspectRatio()
    {
        byte[] pdf = SinglePage("0 0 0 rg 100 100 200 300 re f");

        using SKBitmap bmp = Decode(PdfPageRenderer.RenderPageToFit(
            new MemoryStream(pdf), 0, maxWidthPixels: 400, maxHeightPixels: 0));

        Assert.Equal(400, bmp.Width);
        // 612x792 at 400 px wide is 517.6 -> 518 rows; allow the ceiling either way.
        Assert.InRange(bmp.Height, 516, 519);
    }

    /// <summary>With both bounds set, neither may be exceeded and the tighter one governs.</summary>
    [Fact]
    public void RenderPageToFit_RespectsBothBoundsAndTakesTheTighter()
    {
        byte[] pdf = SinglePage("0 0 0 rg 100 100 200 300 re f");

        using SKBitmap bmp = Decode(PdfPageRenderer.RenderPageToFit(
            new MemoryStream(pdf), 0, maxWidthPixels: 400, maxHeightPixels: 200));

        Assert.True(bmp.Width  <= 400, $"width {bmp.Width} exceeds the bound");
        Assert.True(bmp.Height <= 200, $"height {bmp.Height} exceeds the bound");
        // Height is the tighter bound on a portrait page, so it should be met almost exactly.
        Assert.InRange(bmp.Height, 199, 200);
    }

    /// <summary>A fitted render must contain the page's content, scaled — not a blank canvas.</summary>
    [Fact]
    public void RenderPageToFit_ScalesTheContentRatherThanCroppingIt()
    {
        byte[] pdf = SinglePage("0 0 0 rg 100 100 200 300 re f");

        using SKBitmap small = Decode(PdfPageRenderer.RenderPageToFit(
            new MemoryStream(pdf), 0, 306, 0));
        using SKBitmap full = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72));

        var smallBounds = PixelAssert.InkBounds(small);
        var fullBounds  = PixelAssert.InkBounds(full);
        Assert.NotNull(smallBounds);
        Assert.NotNull(fullBounds);

        // 306 px against a 612 pt page is exactly half scale, so the ink box must halve too.
        int expectedLeft  = fullBounds!.Value.left  / 2;
        int expectedRight = fullBounds.Value.right  / 2;
        Assert.InRange(smallBounds!.Value.left,  expectedLeft  - 2, expectedLeft  + 2);
        Assert.InRange(smallBounds.Value.right,  expectedRight - 2, expectedRight + 2);
    }

    /// <summary>"Fit nothing" has no useful reading and must be refused rather than guessed at.</summary>
    [Fact]
    public void RenderPageToFit_RejectsBothBoundsZero()
    {
        byte[] pdf = SinglePage("0 0 0 rg 100 100 200 300 re f");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfPageRenderer.RenderPageToFit(new MemoryStream(pdf), 0, 0, 0));
    }

    // ---------------------------------------------------------- stream output

    /// <summary>Writing to a stream must produce exactly the bytes the array overload returns.</summary>
    [Fact]
    public void RenderPageToStream_WritesTheSameBytesAsTheArrayOverload()
    {
        byte[] pdf = MultiPagePdf();

        byte[] expected = PdfPageRenderer.RenderPageToImage(new MemoryStream(pdf), 0,
            PdfImageFormat.WebpLossless, Dpi);

        using var output = new MemoryStream();
        PdfPageRenderer.RenderPageToStream(new MemoryStream(pdf), 0, output,
            PdfImageFormat.WebpLossless, Dpi);

        Assert.Equal(expected, output.ToArray());
    }

    // ------------------------------------------- §12.5.3 print vs screen flags

    /// <summary>
    /// An annotation flagged <c>NoView | Print</c> must appear when printing and not on screen.
    /// Both renderers applied the screen rule for every intent, so the one flag combination that
    /// exists to say "print but do not display" was suppressed in print output.
    /// </summary>
    [Fact]
    public void PrintIntent_DrawsNoViewPrintAnnotationThatScreenIntentHides()
    {
        // NoView (32) | Print (4) = 36.
        byte[] pdf = PageWithAnnotation(flags: 36);

        using SKBitmap screen = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi,
            new PdfRenderOptions { Intent = RenderIntent.Screen }));
        using SKBitmap print = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi,
            new PdfRenderOptions { Intent = RenderIntent.Print }));

        Assert.True(PixelAssert.IsBlank(screen),
            "a NoView annotation must not be drawn to a screen target");
        Assert.False(PixelAssert.IsBlank(print),
            "a NoView|Print annotation must be drawn to a print target");
    }

    /// <summary>
    /// An annotation without the Print flag must not print. It used to, because the print path
    /// applied the screen rule, under which a bare visible annotation is visible.
    /// </summary>
    [Fact]
    public void PrintIntent_OmitsAnnotationWithoutThePrintFlagThatScreenIntentShows()
    {
        byte[] pdf = PageWithAnnotation(flags: 0);

        using SKBitmap screen = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi,
            new PdfRenderOptions { Intent = RenderIntent.Screen }));
        using SKBitmap print = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi,
            new PdfRenderOptions { Intent = RenderIntent.Print }));

        Assert.False(PixelAssert.IsBlank(screen),
            "an annotation with no suppressing flag is visible on screen");
        Assert.True(PixelAssert.IsBlank(print),
            "§12.5.3 makes printing opt-in via /Print; an annotation without it must not print");
    }

    /// <summary>Hidden suppresses everywhere — the one rule both targets share.</summary>
    [Fact]
    public void HiddenAnnotation_IsOmittedByBothIntents()
    {
        // Hidden (2) | Print (4) = 6. Hidden wins.
        byte[] pdf = PageWithAnnotation(flags: 6);

        using SKBitmap screen = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi,
            new PdfRenderOptions { Intent = RenderIntent.Screen }));
        using SKBitmap print = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi,
            new PdfRenderOptions { Intent = RenderIntent.Print }));

        Assert.True(PixelAssert.IsBlank(screen));
        Assert.True(PixelAssert.IsBlank(print));
    }

    /// <summary>
    /// The SVG renderer must reach the same decision, or the two disagree on which annotations a
    /// print export contains — the divergence class this repository keeps finding.
    /// </summary>
    [Fact]
    public void SvgExport_AppliesTheSamePrintRuleAsTheRasterRenderer()
    {
        byte[] noViewPrint = PageWithAnnotation(flags: 36);
        byte[] noPrintFlag = PageWithAnnotation(flags: 0);

        string printNoView = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(noViewPrint), 0,
            new PdfRenderOptions { Intent = RenderIntent.Print });
        string screenNoView = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(noViewPrint), 0,
            new PdfRenderOptions { Intent = RenderIntent.Screen });
        string printNoFlag = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(noPrintFlag), 0,
            new PdfRenderOptions { Intent = RenderIntent.Print });

        // Parsed as XML, never substring-matched — the suite did that for its whole history and
        // missed an export that was not well-formed at all.
        Assert.True(HasAnnotationContent(printNoView),
            "a NoView|Print annotation must be exported to a print SVG");
        Assert.False(HasAnnotationContent(screenNoView),
            "a NoView annotation must not be exported to a screen SVG");
        Assert.False(HasAnnotationContent(printNoFlag),
            "an annotation without /Print must not be exported to a print SVG");
    }

    /// <summary>
    /// An annotation with no <c>/F</c> entry at all has every flag clear — §12.5.3 gives <c>/F</c>
    /// a default of 0 and makes no exception for absence — so it shows on screen and, with /Print
    /// clear, does not print. Both renderers reach the one predicate that says so
    /// (<c>AnnotationVisibility.IsHidden</c>).
    /// </summary>
    /// <remarks>
    /// The absent-key branch was carried by both renderer loops for a long time and pinned by
    /// nothing: consolidating the two copies into one predicate and mutating that branch to "draw
    /// it" left the suite green. A merge whose survivor no test reaches is a deletion, so this is
    /// the test that reaches it — on both renderers, against a screen control that proves the
    /// fixture paints at all.
    /// </remarks>
    [Fact]
    public void AnAnnotationWithNoFlagsEntryShowsOnScreenAndDoesNotPrint()
    {
        byte[] pdf = PageWithAnnotation(flags: null);

        using SKBitmap screen = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi,
            new PdfRenderOptions { Intent = RenderIntent.Screen }));
        using SKBitmap print = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi,
            new PdfRenderOptions { Intent = RenderIntent.Print }));

        Assert.False(PixelAssert.IsBlank(screen),
            "an annotation with no /F has Hidden and NoView clear and is visible on screen");
        Assert.True(PixelAssert.IsBlank(print),
            "/F defaults to 0, so an annotation without one has /Print clear and must not print");

        string screenSvg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0,
            new PdfRenderOptions { Intent = RenderIntent.Screen });
        string printSvg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0,
            new PdfRenderOptions { Intent = RenderIntent.Print });

        Assert.True(HasAnnotationContent(screenSvg),
            "an annotation with no /F must be exported to a screen SVG");
        Assert.False(HasAnnotationContent(printSvg),
            "an annotation with no /F must not be exported to a print SVG");
    }

    private static bool HasAnnotationContent(string svg)
    {
        var doc = System.Xml.Linq.XDocument.Parse(svg);
        foreach (var g in doc.Descendants())
        {
            if (g.Attribute("data-layer")?.Value != "annotations") continue;
            foreach (var child in g.Elements()) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ helpers

    private static SKBitmap Decode(byte[] encoded)
    {
        var bmp = SKBitmap.Decode(encoded);
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>Page <c>i</c> draws <c>i + 1</c> black bars, so ink strictly increases with index.</summary>
    private static byte[] MultiPagePdf()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        var kids = new StringBuilder();
        for (int i = 0; i < PageCount; i++) kids.Append(3 + i * 2).Append(" 0 R ");

        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj($"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {PageCount} >>\nendobj\n");

        for (int i = 0; i < PageCount; i++)
        {
            int pageObj = 3 + i * 2, contentObj = pageObj + 1;
            var content = new StringBuilder("0 0 0 rg\n");
            for (int bar = 0; bar <= i; bar++)
                content.Append("50 ").Append(100 + bar * 40).Append(" 200 20 re f\n");

            Obj($"{pageObj} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + $"/Resources << >> /Contents {contentObj} 0 R >>\nendobj\n");
            Obj($"{contentObj} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        }

        return Finish(sb, offsets);
    }

    /// <summary>
    /// A page whose content stream is long enough that a mid-stream cancellation has somewhere to
    /// land, and which draws an <b>undecodable image first</b> so a diagnostic fires as soon as the
    /// interpreter is running.
    /// </summary>
    /// <remarks>
    /// A missing XObject name would not do: <c>Do</c> returns silently when the resource is absent,
    /// so the first version of this fixture never reported and the cancellation was never
    /// triggered. The image declares 10x10 RGB — 300 bytes — and supplies three.
    /// </remarks>
    private static byte[] ManyOperatorsPage()
    {
        var content = new StringBuilder("q 100 0 0 100 50 600 cm /Im0 Do Q\n0 0 0 rg\n");
        for (int i = 0; i < 20000; i++)
            content.Append(i % 600).Append(' ').Append(i % 790).Append(" 3 3 re f\n");
        string body = content.ToString();

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new List<int>();
        void Obj(string s) { offsets.Add(sb.Length); sb.Append(s); }

        const string truncatedSamples = "\x01\x02\x03";
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {body.Length} >>\nstream\n{body}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /XObject /Subtype /Image /Width 10 /Height 10 "
            + "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode "
            + $"/Length {truncatedSamples.Length} >>\nstream\n{truncatedSamples}\nendstream\nendobj\n");

        return Finish(sb, offsets);
    }

    /// <summary>A page whose only mark is one annotation with an appearance stream.</summary>
    /// <summary>
    /// One Square annotation with a black appearance; <paramref name="flags"/> is its <c>/F</c>,
    /// or <see langword="null"/> to write no <c>/F</c> entry at all.
    /// </summary>
    private static byte[] PageWithAnnotation(int? flags)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        const string ap = "0 0 0 rg 0 0 100 50 re f";

        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << >> /Annots [4 0 R] >>\nendobj\n");
        Obj("4 0 obj\n<< /Type /Annot /Subtype /Square /Rect [100 100 200 150] "
            + (flags is int f ? $"/F {f} " : "")
            + "/AP << /N 5 0 R >> >>\nendobj\n");
        Obj($"5 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 100 50] /Length {ap.Length} >>\n"
            + $"stream\n{ap}\nendstream\nendobj\n");

        return Finish(sb, offsets);
    }

    private static byte[] SinglePage(string content, int rotate = 0)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        string rotateEntry = rotate != 0 ? $" /Rotate {rotate}" : "";
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" + rotateEntry
            + " /Resources << >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        return Finish(sb, offsets);
    }

    private static byte[] Finish(StringBuilder sb, List<int> offsets)
    {
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
