// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The tile path against the whole-page path — the same file, the same options, and therefore the
/// same pixels.
///
/// <para><b>Why this suite exists.</b> <c>RenderPageTileToBitmap</c> took no
/// <see cref="PdfRenderOptions"/> at all, so four decisions the page path makes were silently
/// dropped from every tile: the render box came from <c>CropBox ?? MediaBox</c> instead of
/// <c>ResolveRenderBox</c>, <c>/UserUnit</c> was ignored, and
/// <see cref="PdfRenderOptions.SimulateOverprint"/> and
/// <see cref="PdfRenderOptions.DownsampleImagesToTarget"/> were left off however the caller set
/// them. On top of that the origin shift added the box's y origin and not its x, so a page whose
/// box does not start at x&#160;=&#160;0 put its content in the wrong tile horizontally while the
/// vertical axis was right — a discrepancy that is invisible on the [0 0 612 792] pages every
/// existing tile test used.</para>
///
/// <para>Two renderers reading one file and quietly producing different pixels is the defect class
/// this repository keeps finding; the tile is the page's other renderer. Each test below therefore
/// compares a tile (or a mosaic of them) against the whole page rather than checking the tile
/// alone.</para>
///
/// <para><b>Deliberately not asserted here:</b> a tile still honours neither <c>/Rotate</c> nor the
/// page's annotations. Those are behaviour changes for existing tile callers rather than bug fixes
/// — honouring <c>/Rotate</c> inside a tile changes what the caller's clip rectangle means — and
/// are tracked separately.</para>
/// </summary>
public class TileRenderParityTests
{
    private const int Dpi = 72;

    // ------------------------------------------------------------------
    //  The baseline property everything else is a variation on
    // ------------------------------------------------------------------

    /// <summary>
    /// A tile covering the whole page must BE the whole page. This is the plain-vanilla case —
    /// <c>/MediaBox [0 0 200 200]</c>, no <c>/UserUnit</c>, no box override — and it held before
    /// this round; it is here so the interesting cases below have a control that pins the shared
    /// geometry rather than only the four options.
    /// </summary>
    [Fact]
    public void FullPageTile_IsPixelIdenticalToTheWholePageRender()
    {
        byte[] pdf = Pdf("/MediaBox [0 0 200 200]", "1 0 0 rg 20 20 60 60 re f 0 0 1 rg 120 120 60 60 re f");

        using SKBitmap page = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi));
        using SKBitmap tile = Decode(PdfPageRenderer.RenderPageTile(new MemoryStream(pdf), 0, 0, 0, 200, 200, Dpi));

        Assert.Equal(page.Width, tile.Width);
        Assert.Equal(page.Height, tile.Height);

        int differing = 0;
        for (int y = 0; y < page.Height; y++)
            for (int x = 0; x < page.Width; x++)
                if (page.GetPixel(x, y) != tile.GetPixel(x, y)) differing++;

        Assert.Equal(0, differing);

        // …and it is the artwork, not two agreeing blank pages.
        Assert.True(page.GetPixel(50, 150).Red > 200 && page.GetPixel(50, 150).Green < 80,
            $"the red square should be at device (50,150), was {page.GetPixel(50, 150)}");
    }

    // ------------------------------------------------------------------
    //  SimulateOverprint
    // ------------------------------------------------------------------

    /// <summary>
    /// §11.7.4.2 overprint, simulated. The fixture is the one from
    /// <c>OverprintSimulationTests</c>: magenta over cyan composites to blue when simulated and
    /// stays magenta when it is not. The tile ignored the flag entirely, so it painted magenta
    /// while the page beside it painted blue.
    /// </summary>
    [Fact]
    public void Tile_HonoursSimulateOverprint_AsTheWholePageDoes()
    {
        byte[] pdf = OverprintPdf();
        var options = new PdfRenderOptions { SimulateOverprint = true };

        using SKBitmap page = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi, options));
        using SKBitmap tile = Decode(PdfPageRenderer.RenderPageTile(
            new MemoryStream(pdf), 0, 0, 0, 100, 100, Dpi, options));

        SKColor fromPage = page.GetPixel(50, 50);
        SKColor fromTile = tile.GetPixel(50, 50);

        Assert.True(fromPage.Blue > 200 && fromPage.Red < 60 && fromPage.Green < 60,
            $"the page render should composite magenta over cyan to blue, was {fromPage}");
        Assert.Equal(fromPage, fromTile);
    }

    /// <summary>
    /// The control: with simulation off the same tile must paint plain magenta. Without this the
    /// test above would pass on a tile that painted blue for some unrelated reason.
    /// </summary>
    [Fact]
    public void Tile_WithoutSimulateOverprint_PaintsTheSourceColourNormally()
    {
        byte[] pdf = OverprintPdf();

        using SKBitmap tile = Decode(PdfPageRenderer.RenderPageTile(
            new MemoryStream(pdf), 0, 0, 0, 100, 100, Dpi,
            new PdfRenderOptions { SimulateOverprint = false }));

        SKColor c = tile.GetPixel(50, 50);
        Assert.True(c.Red > 200 && c.Blue > 200 && c.Green < 60,
            $"with simulation off the tile should paint plain magenta, was {c}");
    }

    /// <summary>
    /// The headline claim of this round, stated in the two calls a caller actually makes:
    /// <c>session.RenderPage(0)</c> and <c>session.RenderPageTile(0, …)</c> covering the same area
    /// must agree. The session held the options all along and the tile entry point threw them away.
    /// </summary>
    [Fact]
    public void SessionPageAndSessionFullPageTile_AgreeOnTheSessionsRenderOptions()
    {
        using var session = PdfRenderSession.Open(new MemoryStream(OverprintPdf()),
            new PdfRenderOptions { SimulateOverprint = true });

        using SKBitmap page = Decode(session.RenderPage(0, Dpi));
        using SKBitmap tile = Decode(session.RenderPageTile(0, 0, 0, 100, 100, Dpi));

        int differing = 0;
        for (int y = 0; y < page.Height; y++)
            for (int x = 0; x < page.Width; x++)
                if (page.GetPixel(x, y) != tile.GetPixel(x, y)) differing++;

        Assert.Equal(0, differing);
        Assert.True(page.GetPixel(50, 50).Blue > 200 && page.GetPixel(50, 50).Red < 60,
            $"the session's SimulateOverprint should have reached both, page pixel was {page.GetPixel(50, 50)}");
    }

    // ------------------------------------------------------------------
    //  /UserUnit  (§14.11.1)
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>/UserUnit 2</c> makes one default-user-space unit two points, so a 100x100 page is
    /// 200x200 px at 72 dpi. The tile computed its pixel extent from the raw dpi ratio, so a tile
    /// covering the whole page came back at half the page's size with the content at half scale.
    /// </summary>
    [Fact]
    public void Tile_HonoursUserUnit_SoAFullPageTileMatchesThePage()
    {
        byte[] pdf = Pdf("/MediaBox [0 0 100 100] /UserUnit 2", "1 0 0 rg 20 20 60 60 re f");

        using SKBitmap page = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi));
        using SKBitmap tile = Decode(PdfPageRenderer.RenderPageTile(new MemoryStream(pdf), 0, 0, 0, 100, 100, Dpi));

        Assert.Equal(200, page.Width);
        Assert.Equal(200, tile.Width);
        Assert.Equal(page.Height, tile.Height);

        int differing = 0;
        for (int y = 0; y < page.Height; y++)
            for (int x = 0; x < page.Width; x++)
                if (page.GetPixel(x, y) != tile.GetPixel(x, y)) differing++;

        Assert.Equal(0, differing);

        // The square is drawn at twice the scale, so its centre lands mid-bitmap and the corner
        // stays white — a half-scale tile would put white at the centre.
        Assert.True(tile.GetPixel(100, 100).Red > 200 && tile.GetPixel(100, 100).Green < 80,
            $"the /UserUnit-scaled square should cover the bitmap centre, was {tile.GetPixel(100, 100)}");
        Assert.True(tile.GetPixel(10, 10).Green > 200, $"the corner should be white, was {tile.GetPixel(10, 10)}");
    }

    // ------------------------------------------------------------------
    //  BackgroundArgb
    // ------------------------------------------------------------------

    /// <summary>
    /// The fifth decision the tile path dropped. <c>RenderPageTileToBitmap</c> cleared to a
    /// hardcoded <c>SKColors.White</c>, so a caller asking for a coloured page got the colour on
    /// the whole-page render and opaque white on every tile of it — and nothing was reported.
    /// Asserted on a pixel the artwork does not cover, so it is the background being compared and
    /// not the drawing.
    /// </summary>
    [Fact]
    public void Tile_ClearsToBackgroundArgb_AsTheWholePageDoes()
    {
        byte[] pdf = Pdf("/MediaBox [0 0 100 100]", "1 0 0 rg 20 20 20 20 re f");
        // Opaque mid-blue: distinguishable from both white and the red square.
        var options = new PdfRenderOptions { BackgroundArgb = 0xFF3050C0 };

        using SKBitmap page = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi, options));
        using SKBitmap tile = Decode(PdfPageRenderer.RenderPageTile(
            new MemoryStream(pdf), 0, 0, 0, 100, 100, Dpi, options));

        SKColor fromPage = page.GetPixel(90, 10);   // outside the square
        Assert.True(fromPage.Blue > 150 && fromPage.Red < 100,
            $"the page render should carry the requested blue background, was {fromPage}");
        Assert.Equal(fromPage, tile.GetPixel(90, 10));

        int differing = 0;
        for (int y = 0; y < page.Height; y++)
            for (int x = 0; x < page.Width; x++)
                if (page.GetPixel(x, y) != tile.GetPixel(x, y)) differing++;
        Assert.Equal(0, differing);
    }

    /// <summary>
    /// The transparent case, which is the one a compositing caller actually uses: stitching tiles
    /// produced an opaque page where the whole-page render was transparent.
    /// </summary>
    [Fact]
    public void Tile_KeepsATransparentBackgroundTransparent()
    {
        byte[] pdf = Pdf("/MediaBox [0 0 100 100]", "1 0 0 rg 20 20 20 20 re f");
        var options = new PdfRenderOptions { BackgroundArgb = PdfRenderOptions.Transparent };

        using SKBitmap page = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi, options));
        using SKBitmap tile = Decode(PdfPageRenderer.RenderPageTile(
            new MemoryStream(pdf), 0, 0, 0, 100, 100, Dpi, options));

        Assert.Equal(0, page.GetPixel(90, 10).Alpha);
        Assert.Equal(0, tile.GetPixel(90, 10).Alpha);
        // …and the drawn square is still opaque in the tile, so "all transparent" cannot pass.
        Assert.Equal(255, tile.GetPixel(30, 70).Alpha);
    }

    /// <summary>
    /// The control: with no option at all a tile must still be white, so the two tests above cannot
    /// be satisfied by a tile that simply never clears its surface.
    /// </summary>
    [Fact]
    public void Tile_WithDefaultOptions_IsStillOpaqueWhite()
    {
        byte[] pdf = Pdf("/MediaBox [0 0 100 100]", "1 0 0 rg 20 20 20 20 re f");

        using SKBitmap tile = Decode(PdfPageRenderer.RenderPageTile(
            new MemoryStream(pdf), 0, 0, 0, 100, 100, Dpi));

        Assert.Equal(new SKColor(255, 255, 255, 255), tile.GetPixel(90, 10));
    }

    // ------------------------------------------------------------------
    //  ResolveRenderBox + the box origin
    // ------------------------------------------------------------------

    /// <summary>
    /// With <c>UseTrimBox</c> the page renders the <c>/TrimBox</c>, whose origin here is (50,50).
    /// The tile read <c>CropBox ?? MediaBox</c> and so rendered a different rectangle of the file
    /// at the same pixel size — same dimensions, different picture, which is the hardest kind of
    /// disagreement to notice.
    /// </summary>
    [Fact]
    public void Tile_UsesTheSameRenderBoxAsThePage_UnderUseTrimBox()
    {
        byte[] pdf = Pdf("/MediaBox [0 0 200 200] /TrimBox [50 50 150 150]", "1 0 0 rg 50 50 20 20 re f");
        var options = new PdfRenderOptions { UseTrimBox = true };

        using SKBitmap page = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi, options));
        using SKBitmap tile = Decode(PdfPageRenderer.RenderPageTile(
            new MemoryStream(pdf), 0, 0, 0, 100, 100, Dpi, options));

        // The square sits on the TrimBox's own origin, so it lands in the bottom-left corner.
        SKColor corner = page.GetPixel(10, 90);
        Assert.True(corner.Red > 200 && corner.Green < 80,
            $"the page's TrimBox render should put the square in the bottom-left corner, was {corner}");
        Assert.Equal(corner, tile.GetPixel(10, 90));

        int differing = 0;
        for (int y = 0; y < page.Height; y++)
            for (int x = 0; x < page.Width; x++)
                if (page.GetPixel(x, y) != tile.GetPixel(x, y)) differing++;

        Assert.Equal(0, differing);
    }

    /// <summary>
    /// A 2x2 mosaic of a page whose <c>/MediaBox</c> starts at (100,100) must reassemble the page.
    /// The shift was <c>{1,0,0,1, -clipX, -(pdfY + clipY)}</c> — the y term carried the box origin
    /// and the x term did not — so the artwork appeared one tile to the right of where it belongs
    /// and the tile that should have held it came back blank.
    /// </summary>
    [Fact]
    public void TileMosaic_MeasuresTheClipFromTheRenderBoxOrigin()
    {
        byte[] pdf = Pdf("/MediaBox [100 100 300 300]", "1 0 0 rg 110 110 40 40 re f");

        using SKBitmap page = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi));
        using SKBitmap mosaic = Mosaic(pdf, grid: 2, boxW: 200, boxH: 200, dpi: Dpi, options: null);

        Assert.Equal(page.Width, mosaic.Width);
        Assert.Equal(page.Height, mosaic.Height);

        // The square is 40 units in from the box origin, i.e. inside the bottom-left tile.
        SKColor inSquare = page.GetPixel(30, 170);
        Assert.True(inSquare.Red > 200 && inSquare.Green < 80,
            $"the page should have the square at device (30,170), was {inSquare}");
        Assert.Equal(inSquare, mosaic.GetPixel(30, 170));

        int differing = 0;
        for (int y = 0; y < page.Height; y++)
            for (int x = 0; x < page.Width; x++)
                if (page.GetPixel(x, y) != mosaic.GetPixel(x, y)) differing++;

        Assert.Equal(0, differing);
    }

    // ------------------------------------------------------------------
    //  DownsampleImagesToTarget
    // ------------------------------------------------------------------

    /// <summary>
    /// The reduced decode announces itself through
    /// <see cref="RenderDiagnosticKind.ImageDownsampled"/>, which is the only observable it has on
    /// a solid-colour image — and the point of asserting it here is that the flag reaches the tile
    /// at all. The reported native size is checked too, so a diagnostic fired for some other image
    /// would not satisfy it.
    /// </summary>
    [Fact]
    public void Tile_HonoursDownsampleImagesToTarget()
    {
        var reported = new List<string>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Listen(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            // Filtered to this thread: the sink is process-wide and a neighbouring test rendering
            // its own image would otherwise land in this list.
            if (Environment.CurrentManagedThreadId != ownThread) return;
            if (kind == RenderDiagnosticKind.ImageDownsampled) reported.Add(detail ?? string.Empty);
        }

        byte[] pdf = JpegImagePdf(1024);
        RenderDiagnostics.OnEvent += Listen;
        try
        {
            PdfPageRenderer.RenderPageTile(new MemoryStream(pdf), 0, 0, 0, 50, 50, 96,
                new PdfRenderOptions { DownsampleImagesToTarget = true });
        }
        finally { RenderDiagnostics.OnEvent -= Listen; }

        string detail = Assert.Single(reported);
        Assert.Contains("instead of 1024x1024", detail);
    }

    /// <summary>The control: with the flag off the tile must decode the image at its native size.</summary>
    [Fact]
    public void Tile_WithoutDownsampleImagesToTarget_DecodesTheImageWhole()
    {
        var reported = new List<string>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Listen(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            if (kind == RenderDiagnosticKind.ImageDownsampled) reported.Add(detail ?? string.Empty);
        }

        byte[] pdf = JpegImagePdf(1024);
        RenderDiagnostics.OnEvent += Listen;
        try
        {
            PdfPageRenderer.RenderPageTile(new MemoryStream(pdf), 0, 0, 0, 50, 50, 96,
                new PdfRenderOptions { DownsampleImagesToTarget = false });
        }
        finally { RenderDiagnostics.OnEvent -= Listen; }

        Assert.Empty(reported);
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    /// <summary>
    /// Renders a <paramref name="grid"/> x <paramref name="grid"/> mosaic of tiles covering the
    /// render box and stitches it back into one bitmap. Box dimensions are chosen so the division
    /// is exact — an odd split would round each tile up independently and the mosaic would be
    /// wider than the page for reasons that have nothing to do with what is under test.
    /// </summary>
    private static SKBitmap Mosaic(byte[] pdf, int grid, float boxW, float boxH, int dpi,
                                   PdfRenderOptions options)
    {
        float tileW = boxW / grid, tileH = boxH / grid;
        var tiles = new SKBitmap[grid, grid];
        try
        {
            int totalW = 0, totalH = 0;
            for (int ty = 0; ty < grid; ty++)
                for (int tx = 0; tx < grid; tx++)
                {
                    byte[] png = options == null
                        ? PdfPageRenderer.RenderPageTile(new MemoryStream(pdf), 0,
                            tx * tileW, ty * tileH, tileW, tileH, dpi)
                        : PdfPageRenderer.RenderPageTile(new MemoryStream(pdf), 0,
                            tx * tileW, ty * tileH, tileW, tileH, dpi, options);
                    tiles[tx, ty] = SKBitmap.Decode(png)
                        ?? throw new InvalidOperationException($"tile ({tx},{ty}) did not decode");
                    if (ty == 0) totalW += tiles[tx, ty].Width;
                    if (tx == 0) totalH += tiles[tx, ty].Height;
                }

            var stitched = new SKBitmap(totalW, totalH, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(stitched))
            {
                canvas.Clear(SKColors.White);
                int originY = totalH;
                for (int ty = 0; ty < grid; ty++)
                {
                    // Tile row 0 is the BOTTOM of the page: clipY is a PDF y, which grows upward.
                    originY -= tiles[0, ty].Height;
                    int originX = 0;
                    for (int tx = 0; tx < grid; tx++)
                    {
                        canvas.DrawBitmap(tiles[tx, ty], originX, originY, SKSamplingOptions.Default);
                        originX += tiles[tx, ty].Width;
                    }
                }
            }
            return stitched;
        }
        finally
        {
            foreach (SKBitmap b in tiles) b?.Dispose();
        }
    }

    private static SKBitmap Decode(byte[] png)
    {
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>
    /// A cyan 100x100 page with a magenta rectangle painted over it under <c>/OP /op /OPM 1</c>.
    /// Simulated, the source's zero cyan preserves the backdrop and the result is blue.
    /// </summary>
    private static byte[] OverprintPdf() => Pdf(
        "/MediaBox [0 0 100 100] /Resources << /ExtGState << /GS0 5 0 R >> >>",
        "1 0 0 0 k 0 0 100 100 re f q /GS0 gs 0 1 0 0 k 20 20 60 60 re f Q",
        "5 0 obj\n<< /Type /ExtGState /OP true /op true /OPM 1 >>\nendobj\n");

    /// <summary>
    /// A one-page PDF. Objects 1-4 are catalog / pages / page / contents; every element of
    /// <paramref name="extraObjects"/> is a complete numbered object starting at 5.
    /// </summary>
    private static byte[] Pdf(string pageExtras, string content, params string[] extraObjects)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R {pageExtras} /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        for (int i = 0; i < (extraObjects?.Length ?? 0); i++) Obj(extraObjects[i]);

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return acc.ToArray();
    }

    /// <summary>A 100x100 page drawing a solid-red JPEG of <paramref name="size"/> square at 24 pt.</summary>
    private static byte[] JpegImagePdf(int size)
    {
        byte[] jpeg = SolidRedJpeg(size);
        const string content = "q 24 0 0 24 10 10 cm /Im0 Do Q\n";

        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
            + "/Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        offsets.Add(acc.Count);
        Emit($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {size} /Height {size} "
             + $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode "
             + $"/Length {jpeg.Length} >>\nstream\n");
        acc.AddRange(jpeg);
        Emit("\nendstream\nendobj\n");

        int xref = acc.Count;
        int sizeEntries = offsets.Count + 1;
        Emit($"xref\n0 {sizeEntries}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {sizeEntries} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return acc.ToArray();
    }

    private static byte[] SolidRedJpeg(int size)
    {
        using var bmp = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(new SKColor(220, 30, 30));
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }
}
