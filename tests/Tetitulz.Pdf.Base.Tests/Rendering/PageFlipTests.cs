// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <see cref="PdfRenderContext.PageFlip"/>, the page-space-to-canvas mirror the shading shaders, the
/// tiling pattern's bridge and the glyph outlines compose with: y about the page height, x untouched,
/// the DPI scale left to the canvas.
/// </summary>
public sealed class PageFlipTests
{
    [Fact]
    public void PageFlip_MirrorsYAboutThePageHeight_AndLeavesTheDpiScaleToTheCanvas()
    {
        using var bmp = new SKBitmap(10, 10);
        using var canvas = new SKCanvas(bmp);
        using var ctx = new PdfRenderContext(canvas, 300f, 500f, 2f);

        SKMatrix flip = ctx.PageFlip;

        Assert.Equal(new SKPoint(0f, 500f), flip.MapPoint(0f, 0f));
        Assert.Equal(new SKPoint(120f, 0f), flip.MapPoint(120f, 500f));
        Assert.Equal(new SKPoint(300f, 375f), flip.MapPoint(300f, 125f));
        Assert.Equal(1f, flip.Persp2);
        Assert.Equal(0f, flip.Persp0);
        Assert.Equal(0f, flip.Persp1);
    }
}
