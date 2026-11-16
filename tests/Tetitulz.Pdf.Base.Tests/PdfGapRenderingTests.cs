// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Rendering gap tests:
///   GAP 26.1 — Parallel page rendering (RenderAllPages)
///   GAP 26.2 — Tile rendering (RenderPageTile)
/// </summary>
public sealed class PdfGapRenderingTests
{
    // ====================================================================
    //  GAP 26.1 — Parallel rendering
    // ====================================================================

    [Fact]
    public void RenderAllPagesToPng_SinglePage_ReturnsOneImage()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        byte[][] pages = PdfOperations.RenderAllPagesToPng(ms, dpi: 72);
        Assert.NotNull(pages);
        Assert.Single(pages);
        Assert.NotNull(pages[0]);
        Assert.True(pages[0].Length > 0);
    }

    [Fact]
    public void RenderAllPagesToPng_SinglePage_ProducesPngBytes()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        byte[][] pages = PdfOperations.RenderAllPagesToPng(ms, dpi: 72);
        // PNG magic bytes: 0x89 0x50 0x4E 0x47
        Assert.Equal(0x89, pages[0][0]);
        Assert.Equal(0x50, pages[0][1]);
        Assert.Equal(0x4E, pages[0][2]);
        Assert.Equal(0x47, pages[0][3]);
    }

    // ====================================================================
    //  GAP 26.2 — Tile rendering
    // ====================================================================

    /// <summary>
    /// A tile covering the whole page must BE the whole page, pixel for pixel.
    /// </summary>
    /// <remarks>
    /// This test's name claimed exactly that property for its whole life while its body asserted
    /// only that the tile's first two bytes were the PNG signature — which a blank page of the
    /// wrong size satisfies. It is the name/body disagreement CLAUDE.md warns about, and it is why
    /// four separate tile-versus-page divergences (the render box, <c>/UserUnit</c>,
    /// <c>SimulateOverprint</c>, <c>DownsampleImagesToTarget</c>) shipped unnoticed. The systematic
    /// version of this comparison lives in <c>Rendering/TileRenderParityTests</c>; this one stays
    /// because it is the caller-facing <c>PdfOperations</c> pair.
    /// </remarks>
    [Fact]
    public void RenderPageTile_FullPage_SameAsSinglePageRender()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms1 = new MemoryStream(pdf);
        byte[] fullPage = PdfOperations.RenderPageToPng(ms1, pageIndex: 0, dpi: 72);

        using var ms2 = new MemoryStream(pdf);
        // Render the full page as a tile (0,0,612,792).
        byte[] tile = PdfOperations.RenderPageTile(ms2, pageIndex: 0,
            clipX: 0, clipY: 0, clipW: 612, clipH: 792, dpi: 72);

        using SKBitmap a = SKBitmap.Decode(fullPage);
        using SKBitmap b = SKBitmap.Decode(tile);

        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);

        int differing = 0, ink = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                SKColor pa = a.GetPixel(x, y);
                if (pa != b.GetPixel(x, y)) differing++;
                if (pa.Red < 200 || pa.Green < 200 || pa.Blue < 200) ink++;
            }

        Assert.Equal(0, differing);
        // …and the two agreed on the page's text, not on two blank pages.
        Assert.True(ink > 0, "the fixture's text produced no dark pixels — the comparison is vacuous");
    }

    [Fact]
    public void RenderPageTile_SmallTile_ProducesSmallerImage()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var msSmall = new MemoryStream(pdf);
        byte[] smallTile = PdfOperations.RenderPageTile(msSmall, pageIndex: 0,
            clipX: 0, clipY: 0, clipW: 100, clipH: 100, dpi: 72);

        using var msFull = new MemoryStream(pdf);
        byte[] fullTile = PdfOperations.RenderPageTile(msFull, pageIndex: 0,
            clipX: 0, clipY: 0, clipW: 612, clipH: 792, dpi: 72);

        Assert.True(smallTile.Length < fullTile.Length,
            "A 100×100 tile should produce fewer PNG bytes than a 612×792 tile.");
    }

    [Fact]
    public void RenderPageTile_InvalidPageIndex_Throws()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfOperations.RenderPageTile(ms, pageIndex: 99,
                clipX: 0, clipY: 0, clipW: 100, clipH: 100, dpi: 72));
    }

    [Fact]
    public void RenderPageTile_ZeroClipDimension_Throws()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfOperations.RenderPageTile(ms, pageIndex: 0,
                clipX: 0, clipY: 0, clipW: 0, clipH: 100, dpi: 72));
    }
}
