// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The PNG effort dial and the unencoded pixel entry point.
/// <para>
/// Both exist because PNG encoding measured at roughly <b>97%</b> of a single-page render —
/// ~79 ms of an ~82 ms blank page at 150 dpi. The dial changes how hard the encoder works and
/// the pixel path skips it altogether, so the property that has to hold for either to be
/// acceptable is that <b>neither changes a pixel</b>. That is what these assert.
/// </para>
/// </summary>
public class PngCompressionAndPixelOutputTests
{
    private const int Dpi = 96;

    /// <summary>
    /// PNG is lossless at every zlib level and row filter, so the two settings must decode to
    /// byte-identical pixel buffers. If this ever fails, the fast path is not lossless and the
    /// default must go back.
    /// </summary>
    [Fact]
    public void PngCompression_FastAndSmallest_DecodeToIdenticalPixels()
    {
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithColorStateStack();

        byte[] fast = Render(pdf, PngCompression.Fast);
        byte[] best = Render(pdf, PngCompression.Smallest);

        using var fastBmp = SKBitmap.Decode(fast);
        using var bestBmp = SKBitmap.Decode(best);

        Assert.Equal(bestBmp.Width, fastBmp.Width);
        Assert.Equal(bestBmp.Height, fastBmp.Height);
        Assert.Equal(bestBmp.GetPixelSpan().ToArray(), fastBmp.GetPixelSpan().ToArray());
    }

    /// <summary>
    /// ...and the two really are different encodings, not the same bytes twice. Without this the
    /// test above would pass with the dial doing nothing at all.
    /// </summary>
    [Fact]
    public void PngCompression_FastAndSmallest_ProduceDifferentEncodedBytes()
    {
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithColorStateStack();

        byte[] fast = Render(pdf, PngCompression.Fast);
        byte[] best = Render(pdf, PngCompression.Smallest);

        Assert.NotEqual(best, fast);
    }

    /// <summary>
    /// The default is <see cref="PngCompression.Fast"/>. Pinned explicitly because it is a
    /// deliberate change to what every existing caller receives: same image, different bytes.
    /// </summary>
    [Fact]
    public void PngCompression_DefaultIsFast()
    {
        Assert.Equal(PngCompression.Fast, PdfRenderOptions.Default.PngCompression);

        byte[] pdf = MinimalPdfBuilder.CreatePdfWithColorStateStack();
        using var ms = new MemoryStream(pdf);
        byte[] withNoOptions = PdfPageRenderer.RenderPage(ms, 0, Dpi);

        Assert.Equal(Render(pdf, PngCompression.Fast), withNoOptions);
    }

    /// <summary>
    /// The unencoded path returns the same image the PNG path does. Compared against decoded PNG
    /// pixels rather than against itself, so a renderer that produced nothing would fail here.
    /// </summary>
    /// <remarks>
    /// The comparison decodes into an explicit RGBA8888 surface, and that is not incidental:
    /// bare <c>SKBitmap.Decode</c> returns the <i>platform-preferred</i> layout, which on Windows
    /// is <c>Bgra8888</c>. Comparing against it directly reports every pixel as differing with
    /// red and blue swapped — which is a fact about the decoder, not about the renderer.
    /// <see cref="RenderedPage.Pixels"/> is documented as RGBA8888 premultiplied, so the decode
    /// has to be pinned to the same layout for the comparison to mean anything.
    /// </remarks>
    [Fact]
    public void RenderPageToPixels_MatchesTheDecodedPng()
    {
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithColorStateStack();

        using var ms = new MemoryStream(pdf);
        RenderedPage pixels = PdfPageRenderer.RenderPageToPixels(ms, 0, Dpi);

        var info = new SKImageInfo(pixels.Width, pixels.Height,
                                   SKColorType.Rgba8888, SKAlphaType.Premul);
        using var fromPng = SKBitmap.Decode(Render(pdf, PngCompression.Smallest), info);

        Assert.Equal(info.Width, pixels.Width);
        Assert.Equal(info.Height, pixels.Height);
        Assert.Equal(fromPng.RowBytes, pixels.Stride);
        Assert.Equal(fromPng.GetPixelSpan().ToArray(), pixels.Pixels);
    }

    /// <summary>
    /// The documented layout is RGBA8888, not the platform-preferred BGRA. Pinned separately
    /// because it is the one thing a consumer must get right to display the buffer, and because
    /// the test above would still pass if both sides silently became BGRA together.
    /// </summary>
    [Fact]
    public void RenderPageToPixels_IsRgbaNotBgra()
    {
        // A page filled with pure red: 1 0 0 rg over the whole MediaBox.
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithRedRectangle();

        using var ms = new MemoryStream(pdf);
        RenderedPage page = PdfPageRenderer.RenderPageToPixels(ms, 0, Dpi);

        // Sample inside the red rectangle, which covers the left half of the page.
        (byte r, byte g, byte b) = PixelAt(page, page.Width / 4, page.Height / 2);

        Assert.True(r > 200, $"first channel should be red (R), was {r}");
        Assert.True(b < 60, $"third channel should be blue (B) and near zero, was {b}");
        Assert.True(g < 60, $"green should be near zero, was {g}");
    }

    /// <summary>
    /// The fixture paints the left half red and the right half blue, so this asserts the pixel
    /// buffer carries the actual page — not merely that it is the right size. Indexed through
    /// <see cref="RenderedPage.Stride"/>, which is the documented way to walk it.
    /// </summary>
    [Fact]
    public void RenderPageToPixels_CarriesThePaintedColours()
    {
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithColorStateStack();

        using var ms = new MemoryStream(pdf);
        RenderedPage page = PdfPageRenderer.RenderPageToPixels(ms, 0, Dpi);

        // Sample a row in the middle of the page, a quarter in from each side.
        int row = page.Height / 2;
        (byte r, byte g, byte b) left  = PixelAt(page, page.Width / 4, row);
        (byte r, byte g, byte b) right = PixelAt(page, page.Width * 3 / 4, row);

        Assert.True(left.r > 200 && left.g < 60 && left.b < 60,
            $"left half should be red, was ({left.r},{left.g},{left.b})");
        Assert.True(right.b > 200 && right.r < 60 && right.g < 60,
            $"right half should be blue, was ({right.r},{right.g},{right.b})");
    }

    /// <summary>RGBA8888 premultiplied, so channel order is R,G,B,A — see <see cref="RenderedPage.Pixels"/>.</summary>
    private static (byte r, byte g, byte b) PixelAt(RenderedPage page, int x, int y)
    {
        int offset = y * page.Stride + x * 4;
        return (page.Pixels[offset], page.Pixels[offset + 1], page.Pixels[offset + 2]);
    }

    private static byte[] Render(byte[] pdf, PngCompression compression)
    {
        using var ms = new MemoryStream(pdf);
        return PdfPageRenderer.RenderPage(ms, 0, Dpi,
            new PdfRenderOptions { PngCompression = compression });
    }
}
