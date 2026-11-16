// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Rendering a page in horizontal bands.
/// </summary>
/// <remarks>
/// <para><b>The assertion that matters is parity with the whole-page render</b>, and it is a real
/// one rather than a relative one: the banded path and the whole-page path are different code —
/// different allocator, different transform, a clip the page path does not have — so a change that
/// moved both equally is not available to them. Reassembling the bands and comparing every pixel is
/// what says the band geometry is right, and it is exactly what catches the two mistakes this
/// arithmetic invites: a y axis read the wrong way up, and seams where two neighbouring roundings
/// disagree by a row.</para>
///
/// <para><b>What banding buys is bounded memory, not less work</b>, and the tests say only that.
/// Each band re-executes the page's content stream because there is no display list to replay —
/// GAPS §11a and §11f — so there is no timing claim here to make or to pin.</para>
/// </remarks>
public class BandedRenderingTests
{
    /// <summary>Bands reassemble into exactly the whole-page render.</summary>
    /// <remarks>
    /// <b>Every pixel, not a sample.</b> A seam is one row wrong out of hundreds, and a y flip is
    /// invisible on any page whose content happens to be symmetric — so the fixture is deliberately
    /// asymmetric top to bottom, and the comparison is total.
    /// </remarks>
    [Theory]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(100)]
    public void BandsReassembleIntoTheWholePageRender(int bandHeight)
        => AssertBandsMatchWholePage(AsymmetricPagePdf(), bandHeight);

    /// <summary>
    /// The same property, on content that reaches Wave 8.1 Stage D's five gated paint methods
    /// — <c>AsymmetricPagePdf</c> above is all plain fills, which
    /// <c>IsCurrentPathOutsideClip</c> culls without ever consulting
    /// <see cref="PdfRenderContext.PaintSuppressed"/>, so it cannot exercise
    /// <see cref="PdfRenderSession.RenderPageBands"/>'s <c>DisplayListIndex</c> wiring at all — a
    /// first version of this suite proved that the hard way: a deliberately wrong Y-flip in the
    /// band-to-index coordinate conversion passed every existing test here, because none of them
    /// ever reach a gated method. This fixture spreads an image <c>Do</c>, a text run, a
    /// shading-pattern fill and a direct <c>sh</c> across four different 64-row bands of a
    /// 792-tall page, so a wrong band-to-index mapping puts the wrong content (or none) in at
    /// least one band.
    /// </summary>
    [Theory]
    [InlineData(64)]
    [InlineData(97)] // Deliberately not a divisor of any fixture coordinate below.
    public void BandsReassembleIntoTheWholePageRender_AcrossImageTextPatternAndShading(int bandHeight)
        => AssertBandsMatchWholePage(MixedGatedContentPagePdf(), bandHeight);

    /// <summary>
    /// Wave 8.1 Stage E: tiles of the gated fixture reassemble into the whole-page render. The tile
    /// path asks the display-list index for its rectangle, so an X conversion or comparison gone
    /// wrong drops the image, the text, the pattern or the shading from a tile they cross — the
    /// fixture's four sit across the grid's column boundaries (x 150 to 290 against columns at 204
    /// and 408 points, or 200 and 400). At 72 dpi a point is a pixel, so the tiles cut the page
    /// exactly.
    /// </summary>
    [Theory]
    [InlineData(3, 3)]
    [InlineData(4, 6)]
    public void TilesReassembleIntoTheWholePageRender_AcrossImageTextPatternAndShading(int columns, int rows)
    {
        byte[] pdf = MixedGatedContentPagePdf();
        using var session = PdfRenderSession.Open(new MemoryStream(pdf, writable: false));
        RenderedPage whole = session.RenderPageToPixels(0, Dpi);
        int tileW = whole.Width / columns, tileH = whole.Height / rows;
        Assert.Equal((whole.Width, whole.Height), (tileW * columns, tileH * rows));

        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
            {
                // clipY is the tile's foot, measured up from the page's foot.
                RenderedPage tile = session.RenderPageTileToPixels(0, c * tileW, whole.Height - (r + 1) * tileH, tileW, tileH, Dpi);
                Assert.Equal((tileW, tileH), (tile.Width, tile.Height));
                for (int y = 0; y < tileH; y++)
                    for (int x = 0; x < tileW; x++)
                        for (int channel = 0; channel < 4; channel++)
                        {
                            byte t = tile.Pixels[y * tile.Stride + x * 4 + channel];
                            byte w = whole.Pixels[(r * tileH + y) * whole.Stride + (c * tileW + x) * 4 + channel];
                            if (t != w)
                                Assert.Fail($"tile ({c}, {r}) at ({x}, {y}), channel {channel}: tile has {t}, page has {w}");
                        }
            }
    }

    /// <summary>
    /// The gates are live (CLAUDE.md, Testing, blind spot 2): a gated region draws what an ungated
    /// one draws, so only the count of skipped paints shows a gate was applied. A tile in the page's
    /// bottom-left corner — clear of the image, the text, the pattern and the shading, all right of
    /// x 150 — skips all four; a tile of the whole page skips none. The band covering the page's top
    /// 64 rows holds the image (y 750 to 790) and skips the other three; one band of the whole page
    /// skips none.
    /// </summary>
    [Fact]
    public void TheTileAndBandGatesSkipThePaintsTheirRegionMisses()
    {
        byte[] pdf = MixedGatedContentPagePdf();
        using var session = PdfRenderSession.Open(new MemoryStream(pdf, writable: false));
        PageRenderSize size = session.GetRenderSize(0, Dpi);

        Assert.True(session.RenderPageTileToPixels(0, 0, 0, 100, 100, Dpi).SuppressedPaints >= 4);
        Assert.Equal(0, session.RenderPageTileToPixels(0, 0, 0, size.WidthPoints, size.HeightPoints, Dpi).SuppressedPaints);

        var topBand = new List<int>();
        session.RenderPageBands(0, Dpi, 64, (i, band) => { if (i == 0) topBand.Add(band.SuppressedPaints); });
        Assert.True(topBand[0] >= 3, $"the top band skipped {topBand[0]} paints");
        int whole = -1;
        session.RenderPageBands(0, Dpi, size.HeightPixels, (_, band) => whole = band.SuppressedPaints);
        Assert.Equal(0, whole);
    }

    private static void AssertBandsMatchWholePage(byte[] pdf, int bandHeight)
    {
        using var session = PdfRenderSession.Open(new MemoryStream(pdf, writable: false));
        RenderedPage whole = session.RenderPageToPixels(0, Dpi);

        var bands = new List<(int Index, byte[] Pixels, int Width, int Height, int Stride)>();
        int count = session.RenderPageBands(0, Dpi, bandHeight,
            (i, band) => bands.Add((i, (byte[])band.Pixels.Clone(),
                                    band.Width, band.Height, band.Stride)));

        Assert.Equal(bands.Count, count);
        Assert.Equal(whole.Height, TotalRows(bands));

        int row = 0;
        foreach ((int index, byte[] pixels, int width, int height, int stride) in bands)
        {
            Assert.Equal(whole.Width, width);

            for (int y = 0; y < height; y++, row++)
                for (int x = 0; x < width; x++)
                {
                    int inBand = (y * stride) + (x * 4);
                    int inPage = (row * whole.Stride) + (x * 4);

                    for (int channel = 0; channel < 4; channel++)
                        if (pixels[inBand + channel] != whole.Pixels[inPage + channel])
                            Assert.Fail(
                                $"band {index} row {y} (page row {row}), x {x}, channel {channel}: "
                                + $"band has {pixels[inBand + channel]}, page has "
                                + $"{whole.Pixels[inPage + channel]}");
                }
        }
    }

    /// <summary>Every band is the requested height except the last, which takes the remainder.</summary>
    /// <remarks>
    /// <b>This is the memory claim, and it is the only one being made.</b> A band buffer is
    /// <c>bandHeight</c> rows, so peak allocation is bounded by the caller's choice rather than by
    /// the page — which is the whole point at 600 dpi, where a full page is a quarter of a gigabyte.
    /// </remarks>
    [Fact]
    public void EveryBandIsTheRequestedHeightExceptTheLast()
    {
        const int BandHeight = 64;
        byte[] pdf = AsymmetricPagePdf();

        using var session = PdfRenderSession.Open(new MemoryStream(pdf, writable: false));
        int pageHeight = session.GetRenderSize(0, Dpi).HeightPixels;

        var heights = new List<int>();
        int count = session.RenderPageBands(0, Dpi, BandHeight, (_, band) => heights.Add(band.Height));

        Assert.Equal((pageHeight + BandHeight - 1) / BandHeight, count);

        for (int i = 0; i < heights.Count - 1; i++)
            Assert.Equal(BandHeight, heights[i]);

        int expectedLast = pageHeight - (BandHeight * (heights.Count - 1));
        Assert.Equal(expectedLast, heights[heights.Count - 1]);
    }

    /// <summary>A band taller than the page yields one band covering it.</summary>
    /// <remarks>
    /// The degenerate case a caller reaches by asking for "one band" without knowing the page size.
    /// It must produce the page, not an over-tall buffer with blank rows below it.
    /// </remarks>
    [Fact]
    public void ABandTallerThanThePageYieldsOneBandOfExactlyThePage()
    {
        byte[] pdf = AsymmetricPagePdf();

        using var session = PdfRenderSession.Open(new MemoryStream(pdf, writable: false));
        int pageHeight = session.GetRenderSize(0, Dpi).HeightPixels;

        RenderedPage only = null;
        int count = session.RenderPageBands(0, Dpi, pageHeight * 4, (_, band) => only = band);

        Assert.Equal(1, count);
        Assert.Equal(pageHeight, only.Height);
    }

    /// <summary>A tile's raw pixels match the same tile rendered through the page.</summary>
    /// <remarks>
    /// <b>The primitive under the banding, checked on its own.</b> A tile covering the whole render
    /// box must reproduce the whole page — the property the tile path's own remarks say the box
    /// origin was chosen to give — and this is the raw-pixel form of it, which had no way to be
    /// asked before.
    /// </remarks>
    [Fact]
    public void ATileCoveringTheWholeBoxReproducesThePage()
    {
        byte[] pdf = AsymmetricPagePdf();

        using var session = PdfRenderSession.Open(new MemoryStream(pdf, writable: false));
        RenderedPage whole = session.RenderPageToPixels(0, Dpi);
        PageRenderSize size = session.GetRenderSize(0, Dpi);

        RenderedPage tile = session.RenderPageTileToPixels(
            0, 0f, 0f, size.WidthPoints, size.HeightPoints, Dpi);

        Assert.Equal(whole.Width, tile.Width);
        Assert.Equal(whole.Height, tile.Height);
        Assert.Equal(whole.Pixels, tile.Pixels);
    }

    /// <summary>Bands are cut correctly on a page whose /UserUnit is not 1.</summary>
    /// <remarks>
    /// <para><b>The case that separates two ways of computing the same number.</b> Bands are cut in
    /// pixels and converted with the page's own measured points-per-pixel. Deriving that from
    /// <c>72/dpi</c> instead gives an identical answer on an ordinary page — which is every other
    /// fixture here, so a mutation swapping one for the other survived them all.</para>
    ///
    /// <para>§14.11.1 makes one default-user-space unit equal <c>/UserUnit</c> points, so the page
    /// below renders at twice its nominal pixel size and the two formulas differ by a factor of
    /// two. A band cut with the wrong one covers the wrong strip of the page, and the parity
    /// comparison says so.</para>
    /// </remarks>
    [Fact]
    public void BandsAreCutCorrectlyWhenUserUnitIsNotOne()
    {
        byte[] pdf = AsymmetricPagePdf(userUnit: 2);

        using var session = PdfRenderSession.Open(new MemoryStream(pdf, writable: false));
        RenderedPage whole = session.RenderPageToPixels(0, Dpi);

        var rows = new List<byte[]>();
        session.RenderPageBands(0, Dpi, 48, (_, band) =>
        {
            for (int y = 0; y < band.Height; y++)
            {
                var row = new byte[band.Width * 4];
                Array.Copy(band.Pixels, y * band.Stride, row, 0, row.Length);
                rows.Add(row);
            }
        });

        Assert.Equal(whole.Height, rows.Count);

        for (int y = 0; y < whole.Height; y++)
            for (int i = 0; i < whole.Width * 4; i++)
                if (rows[y][i] != whole.Pixels[(y * whole.Stride) + i])
                    Assert.Fail($"row {y}, byte {i}: band has {rows[y][i]}, "
                                + $"page has {whole.Pixels[(y * whole.Stride) + i]}");
    }

    /// <summary>Bad arguments are refused rather than producing an empty or wrong render.</summary>
    [Fact]
    public void MalformedArgumentsAreRefused()
    {
        byte[] pdf = AsymmetricPagePdf();
        using var session = PdfRenderSession.Open(new MemoryStream(pdf, writable: false));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => session.RenderPageBands(0, Dpi, 0, (_, __) => { }));
        Assert.Throws<ArgumentNullException>(
            () => session.RenderPageBands(0, Dpi, 32, null));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => session.RenderPageBands(99, Dpi, 32, (_, __) => { }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => session.RenderPageTileToPixels(0, 0f, 0f, 0f, 10f, Dpi));
    }

    // ================================================================

    private const int Dpi = 72;

    private static int TotalRows(
        List<(int Index, byte[] Pixels, int Width, int Height, int Stride)> bands)
    {
        int rows = 0;
        foreach (var band in bands) rows += band.Height;
        return rows;
    }

    /// <summary>
    /// A page whose content differs top from bottom and left from right.
    /// </summary>
    /// <remarks>
    /// <b>Symmetry is what would hide a y flip.</b> The bars march down the page in changing widths
    /// and greys, so every band's content is distinguishable from every other band's — reassemble
    /// them in the wrong order, or read the y axis upside down, and the comparison fails on the
    /// first band rather than passing by coincidence.
    /// </remarks>
    private static byte[] AsymmetricPagePdf(int userUnit = 0)
    {
        var content = new StringBuilder();
        content.AppendLine("q");

        for (int i = 0; i < 20; i++)
        {
            double grey = i / 24.0;
            double y = 760 - (i * 38);
            double width = 40 + (i * 12);

            content.AppendLine(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0} g {1} {2} {3} {4} re f", grey, 30, y, width, 14));
        }

        // A mark in one corner only, so an upside-down page cannot match a right-way-up one.
        content.AppendLine("0 g 500 740 80 40 re f");
        content.AppendLine("Q");

        return OnePagePdf(content.ToString(), userUnit);
    }

    /// <summary>
    /// A 612x792 page whose content spreads an image <c>Do</c>, a text run, a shading-pattern
    /// fill and a direct <c>sh</c> at four well-separated heights — the operators Wave 8.1 Stage D
    /// gates, which <see cref="AsymmetricPagePdf"/> above never reaches.
    /// </summary>
    private static byte[] MixedGatedContentPagePdf()
    {
        const string content =
            "q 40 0 0 40 250 750 cm /Im0 Do Q\n" +
            "BT /F1 24 Tf 1 0 0 1 250 620 Tm (Hello) Tj ET\n" +
            "/Pattern cs /P0 scn 200 480 60 40 re f\n" +
            "q 150 180 100 40 re W n /Sh0 sh Q\n";

        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /XObject << /Im0 5 0 R >> /Font << /F1 6 0 R >> "
            + "/Shading << /Sh0 7 0 R >> /Pattern << /P0 8 0 R >> >> "
            + "/Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");

        byte[] rgb = { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0 }; // 2x2, four distinct colours
        var imageHead = Encoding.Latin1.GetBytes(
            $"5 0 obj\n<< /Type /XObject /Subtype /Image /Width 2 /Height 2 "
            + $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {rgb.Length} >>\nstream\n");
        offsets.Add(acc.Count);
        acc.AddRange(imageHead);
        acc.AddRange(rgb);
        Emit("\nendstream\nendobj\n");

        Obj("6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica "
            + "/Encoding /WinAnsiEncoding >>\nendobj\n");

        string axialShading = "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 612 792] >>";
        Obj($"7 0 obj\n{axialShading}\nendobj\n");
        Obj($"8 0 obj\n<< /Type /Pattern /PatternType 2 /Shading {axialShading} >>\nendobj\n");

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }

    private static byte[] OnePagePdf(string content, int userUnit = 0)
    {
        string unit = userUnit > 0 ? $" /UserUnit {userUnit}" : string.Empty;

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]{unit} /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
        };

        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");

        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.Append(offset.ToString("D10") + " 00000 n \n");
        sb.Append($"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
