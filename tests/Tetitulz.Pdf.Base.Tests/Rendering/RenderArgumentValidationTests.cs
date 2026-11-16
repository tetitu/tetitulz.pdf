// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Wave 17 R13.1–R13.3: the three public render arguments that were not validated. A tile
/// clip tested <c>clipW &lt;= 0</c> alone, which NaN passes; <c>ExtractInkCoverage</c> never
/// looked at its <c>dpi</c>; <c>InkAccumulator</c>'s constructor multiplied unchecked. Each
/// refusal is paired with the control that the same call, given a sound argument, answers.
/// </summary>
public sealed class RenderArgumentValidationTests
{
    // ------------------------------------------------------------------
    //  R13.1 — a tile's clip must be finite on all four sides
    // ------------------------------------------------------------------

    public static TheoryData<float, float, float, float, string> NonFiniteClips => new()
    {
        { float.NaN, 0f, 10f, 10f, "clipX" },
        { 0f, float.NegativeInfinity, 10f, 10f, "clipY" },
        { 0f, 0f, float.NaN, 10f, "clipW" },
        { 0f, 0f, 10f, float.PositiveInfinity, "clipH" },
    };

    [Theory]
    [MemberData(nameof(NonFiniteClips))]
    public void Tile_NonFiniteClip_IsRefusedNamingTheArgument(
        float clipX, float clipY, float clipW, float clipH, string parameter)
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();

        using (var ms = new MemoryStream(pdf, writable: false))
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => PdfPageRenderer.RenderPageTile(ms, 0, clipX, clipY, clipW, clipH, dpi: 72));
            Assert.Equal(parameter, ex.ParamName);
        }
        using (var ms = new MemoryStream(pdf, writable: false))
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => PdfPageRenderer.RenderPageTileAsJpg(ms, 0, clipX, clipY, clipW, clipH, dpi: 72));
            Assert.Equal(parameter, ex.ParamName);
        }

        using var session = PdfRenderSession.Open(new MemoryStream(pdf, writable: false));
        var fromImage = Assert.Throws<ArgumentOutOfRangeException>(
            () => session.RenderPageTileToImage(0, clipX, clipY, clipW, clipH, dpi: 72));
        Assert.Equal(parameter, fromImage.ParamName);
        var fromPixels = Assert.Throws<ArgumentOutOfRangeException>(
            () => session.RenderPageTileToPixels(0, clipX, clipY, clipW, clipH, 72));
        Assert.Equal(parameter, fromPixels.ParamName);
    }

    /// <summary>The control: a finite 10 × 10 pt clip at 72 dpi is a 10 × 10 px tile, on both routes.</summary>
    [Fact]
    public void Tile_FiniteClip_RendersAtItsSize()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();

        using (var ms = new MemoryStream(pdf, writable: false))
        {
            byte[] png = PdfPageRenderer.RenderPageTile(ms, 0, 0f, 0f, 10f, 10f, dpi: 72);
            using var bitmap = SKBitmap.Decode(png);
            Assert.Equal((10, 10), (bitmap.Width, bitmap.Height));
        }

        using var session = PdfRenderSession.Open(new MemoryStream(pdf, writable: false));
        RenderedPage tile = session.RenderPageTileToPixels(0, 0f, 0f, 10f, 10f, 72);
        Assert.Equal((10, 10), (tile.Width, tile.Height));
    }

    // ------------------------------------------------------------------
    //  R13.2 — ExtractInkCoverage's dpi
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0f)]
    [InlineData(-72f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void ExtractInkCoverage_NonPositiveOrNonFiniteDpi_IsRefused(float dpi)
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();

        using (var ms = new MemoryStream(pdf, writable: false))
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => PdfPageRenderer.ExtractInkCoverage(ms, 0, dpi));
            Assert.Equal("dpi", ex.ParamName);
        }
        using (var ms = new MemoryStream(pdf, writable: false))
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => PdfOperations.ExtractInkCoverage(ms, 0, new PdfRenderOptions(), dpi));
            Assert.Equal("dpi", ex.ParamName);
        }
    }

    /// <summary>The control: 72 dpi over the fixture's 612 × 792 pt page is a 612 × 792 map.</summary>
    [Fact]
    public void ExtractInkCoverage_PositiveDpi_SizesTheMapFromThePage()
    {
        using var ms = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf(), writable: false);
        InkAccumulator acc = PdfPageRenderer.ExtractInkCoverage(ms, 0, 72f);
        Assert.Equal((612, 792), (acc.Width, acc.Height));
    }

    // ------------------------------------------------------------------
    //  R13.3 — InkAccumulator's plane
    // ------------------------------------------------------------------

    [Fact]
    public void InkAccumulator_NegativeDimension_IsRefusedNamingIt()
    {
        Assert.Equal("width",  Assert.Throws<ArgumentOutOfRangeException>(() => new InkAccumulator(-1, 5)).ParamName);
        Assert.Equal("height", Assert.Throws<ArgumentOutOfRangeException>(() => new InkAccumulator(5, -1)).ParamName);
    }

    /// <summary>
    /// 70 000 × 70 000 is 4.9 × 10⁹ cells: more than one array holds. Multiplied in <c>int</c> it
    /// wrapped to 605 032 704, a plane the far corner's deposit indexed past.
    /// </summary>
    [Fact]
    public void InkAccumulator_PlaneLargerThanAnArray_IsRefused()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new InkAccumulator(70_000, 70_000));
        Assert.Equal("width", ex.ParamName);
    }

    /// <summary>The control: a sound plane is allocated at its size, and an empty one accepts and drops every deposit.</summary>
    [Fact]
    public void InkAccumulator_SoundDimensions_AllocateThePlane()
    {
        var acc = new InkAccumulator(3, 2);
        Assert.Equal(6, acc.GetOrCreateChannel("K").Length);

        var empty = new InkAccumulator(0, 5);
        empty.Deposit("K", 0, 0, 255);
        Assert.Empty(empty.Channels);
    }
}
