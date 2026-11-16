// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Images;

/// <summary>
/// <see cref="BitmapPixels"/> — the walk that replaced the <c>SKBitmap.Pixels</c> round trips, and
/// the one place in the tree that still keeps the allocating path.
/// </summary>
/// <remarks>
/// <b>The two paths agree by construction, so the answer cannot pin the optimisation.</b> Deleting
/// the buffer walk entirely would leave every rendering assertion in the suite green and silently
/// restore the allocation — the same shape as <c>CMapCodec</c>'s sort, whose deletion abandoned the
/// CID index library-wide without reddening anything. <see cref="BitmapPixels.BufferWalks"/> and
/// <see cref="BitmapPixels.PropertyFallbacks"/> exist for that, and these are what read them.
/// </remarks>
public class BitmapPixelsTests
{
    /// <summary>A map over an 8888 bitmap walks the buffer.</summary>
    [Fact]
    public void MappingAnEightEightEightBitmapWalksTheBuffer()
    {
        using var bitmap = new SKBitmap(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul);
        BitmapPixels.ResetCounters();

        BitmapPixels.Map(bitmap, c => c);

        Assert.Equal(1, BitmapPixels.BufferWalks);
        Assert.Equal(0, BitmapPixels.PropertyFallbacks);
    }

    /// <summary>A colour type with no trustworthy offsets falls back, and says so.</summary>
    /// <remarks>
    /// The acceptance control for the probe above: a flag hard-coded to <c>true</c> would satisfy
    /// it, and this is the assertion that flag cannot pass.
    /// </remarks>
    [Fact]
    public void MappingAnUnsupportedColorTypeFallsBack()
    {
        // Four bytes per pixel, so the stride guard is not what declines it -- see
        // StraightPixelLayoutTests.AnUnsupportedColorTypeIsDeclined for why that matters.
        using var bitmap = new SKBitmap(4, 4, SKColorType.Rgba1010102, SKAlphaType.Premul);
        BitmapPixels.ResetCounters();

        BitmapPixels.Map(bitmap, c => c);

        Assert.Equal(0, BitmapPixels.BufferWalks);
        Assert.Equal(1, BitmapPixels.PropertyFallbacks);
    }

    /// <summary>The buffer walk produces the pixels the property route it replaced produced.</summary>
    /// <remarks>
    /// Compared on a bitmap carrying partial alpha, where the premultiply and un-premultiply the
    /// two routes perform separately could disagree. <b>Named for what it compares</b>: the second
    /// half here is a hand-written <c>SKBitmap.Pixels</c> round trip, not this type's own fallback
    /// arm — which an Rgba8888 fixture cannot reach. That arm is
    /// <see cref="TheClearFallbackClearsTheSamePixelsTheBufferWalkWould"/>.
    /// </remarks>
    [Fact]
    public void TheBufferWalkAgreesWithThePropertyRouteItReplaced()
    {
        BitmapPixels.Remap invert = c => new SKColor(
            (byte)(255 - c.Red), (byte)(255 - c.Green), (byte)(255 - c.Blue), c.Alpha);

        using SKBitmap viaBuffer = PartiallyTransparent();
        BitmapPixels.ResetCounters();
        BitmapPixels.Map(viaBuffer, invert);
        Assert.Equal(0, BitmapPixels.PropertyFallbacks);

        // The same work through the property, which is what the fallback arm does.
        using SKBitmap viaProperty = PartiallyTransparent();
        SKColor[] pixels = viaProperty.Pixels;
        for (int i = 0; i < pixels.Length; i++) pixels[i] = invert(pixels[i]);
        viaProperty.Pixels = pixels;

        Assert.Equal(viaProperty.GetPixelSpan().ToArray(), viaBuffer.GetPixelSpan().ToArray());
    }

    /// <summary>A clear writes only the pixels its predicate names.</summary>
    /// <remarks>
    /// The claim that makes <c>Clear</c> exact rather than approximate: an untouched pixel is not
    /// read, so it cannot be re-converted, so it cannot drift. A stencil mask covering half an
    /// image must leave the other half byte-identical.
    /// </remarks>
    [Fact]
    public void AClearLeavesEveryPixelItDoesNotNameByteIdentical()
    {
        using SKBitmap cleared = PartiallyTransparent();
        using SKBitmap untouched = PartiallyTransparent();

        byte[] before = untouched.GetPixelSpan().ToArray();

        BitmapPixels.ResetCounters();
        BitmapPixels.Clear(cleared, (x, y) => x < 8);
        Assert.Equal(0, BitmapPixels.PropertyFallbacks);

        Span<byte> after = cleared.GetPixelSpan();
        for (int y = 0; y < cleared.Height; y++)
        {
            int rowStart = y * cleared.RowBytes;
            for (int x = 0; x < cleared.Width; x++)
            {
                int o = rowStart + (x * 4);
                if (x < 8)
                {
                    Assert.Equal(0, after[o] | after[o + 1] | after[o + 2] | after[o + 3]);
                    continue;
                }

                Assert.Equal(before[o], after[o]);
                Assert.Equal(before[o + 1], after[o + 1]);
                Assert.Equal(before[o + 2], after[o + 2]);
                Assert.Equal(before[o + 3], after[o + 3]);
            }
        }
    }

    /// <summary>The fallback clears the pixels its predicate names, at the right coordinates.</summary>
    /// <remarks>
    /// <para><b>The coordinates are the trap.</b> The buffer walk pairs (x, y) with a row slice and
    /// the fallback with <c>y * width + x</c> into a flat array, and a transposed fallback still
    /// clears the right <i>number</i> of pixels — so the predicate here is asymmetric and the
    /// assertion is positional.</para>
    ///
    /// <para><b>And the fixture has to reach the fallback at all.</b> An Rgba8888 bitmap takes the
    /// buffer walk, so the first version of this test executed nothing of the arm it was named for
    /// and transposing that arm survived. Rgba1010102 is declined by
    /// <c>StraightPixelLayout</c> — four bytes per pixel, so it is the colour type and not the
    /// stride that declines it — which is what routes this to the fallback.</para>
    /// </remarks>
    [Fact]
    public void TheClearFallbackClearsTheSamePixelsTheBufferWalkWould()
    {
        BitmapPixels.ClearsAt upperRight = (x, y) => x > y;

        using var bitmap = new SKBitmap(16, 16, SKColorType.Rgba1010102, SKAlphaType.Premul);
        var seed = new SKColor[16 * 16];
        for (int i = 0; i < seed.Length; i++) seed[i] = new SKColor(200, 150, 100, 255);
        bitmap.Pixels = seed;

        BitmapPixels.ResetCounters();
        BitmapPixels.Clear(bitmap, upperRight);

        Assert.Equal(0, BitmapPixels.BufferWalks);
        Assert.Equal(1, BitmapPixels.PropertyFallbacks);

        SKColor[] after = bitmap.Pixels;
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                SKColor pixel = after[(y * 16) + x];
                if (upperRight(x, y))
                    Assert.True(pixel.Alpha == 0, $"({x},{y}) should have been cleared");
                else
                    Assert.True(pixel.Alpha != 0, $"({x},{y}) should have been left alone");
            }
    }

    /// <summary>Two bitmaps of one size are paired pixel for pixel.</summary>
    [Fact]
    public void TheTwoBitmapMapPairsMatchingPixels()
    {
        using SKBitmap target = PartiallyTransparent();
        using var other = new SKBitmap(16, 16, SKColorType.Rgba8888, SKAlphaType.Unpremul);

        var alphas = new SKColor[16 * 16];
        for (int i = 0; i < alphas.Length; i++) alphas[i] = new SKColor((byte)(i & 0xFF), 0, 0, 255);
        other.Pixels = alphas;

        // Take the second bitmap's red as the first's alpha, which is what an /SMask does.
        BitmapPixels.ResetCounters();
        BitmapPixels.Map(target, other, (t, m) => new SKColor(t.Red, t.Green, t.Blue, m.Red));
        Assert.Equal(0, BitmapPixels.PropertyFallbacks);

        SKColor[] result = target.Pixels;
        for (int i = 0; i < result.Length; i++)
            Assert.Equal((byte)(i & 0xFF), result[i].Alpha);
    }

    /// <summary>Bitmaps of different sizes take the allocating path, which pairs by index.</summary>
    /// <remarks>
    /// <b>Not a performance decision.</b> A row walk pairs by (x, y) and a flat array pairs by
    /// position, and the two are the same pairing only at equal dimensions — so a fast path taken
    /// here would give a different answer, not a quicker one. The soft-mask caller reaches it when
    /// <c>SKBitmap.Resize</c> returns null.
    /// </remarks>
    [Fact]
    public void MismatchedDimensionsTakeTheAllocatingPath()
    {
        using SKBitmap target = PartiallyTransparent();
        using var other = new SKBitmap(8, 8, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        BitmapPixels.ResetCounters();

        BitmapPixels.Map(target, other, (t, m) => t);

        Assert.Equal(0, BitmapPixels.BufferWalks);
        Assert.Equal(1, BitmapPixels.PropertyFallbacks);
    }

    /// <summary>A region is respected: nothing outside it is written.</summary>
    /// <remarks>
    /// <para><b>Nothing about the overprint caller's output can assert this.</b> Outside the path
    /// the coverage surface's alpha is 0, so that blend returns the target pixel unchanged and
    /// walking the whole surface produces identical pixels — the region is a cost bound there,
    /// not a correctness one, which is why deleting it survived every rendering test in the suite.
    /// The blend here does change what it touches, so the bound becomes observable.</para>
    /// </remarks>
    [Fact]
    public void MapRegionWritesNothingOutsideTheRegion()
    {
        using SKBitmap target = PartiallyTransparent();
        using SKBitmap other = PartiallyTransparent();

        byte[] before = target.GetPixelSpan().ToArray();
        var region = SKRectI.Create(4, 5, 3, 2);   // x 4..6, y 5..6

        BitmapPixels.ResetCounters();
        BitmapPixels.MapRegion(target, other, region,
            (t, o, x, y) => new SKColor(255, 0, 0, 255));

        Assert.Equal(0, BitmapPixels.PropertyFallbacks);

        Span<byte> after = target.GetPixelSpan();
        for (int y = 0; y < target.Height; y++)
            for (int x = 0; x < target.Width; x++)
            {
                int o = (y * target.RowBytes) + (x * 4);
                bool inside = x >= 4 && x < 7 && y >= 5 && y < 7;

                if (inside)
                {
                    Assert.True(after[o] == 255 && after[o + 3] == 255,
                        $"({x},{y}) is inside the region and should have been written");
                    continue;
                }

                Assert.True(
                    after[o] == before[o] && after[o + 1] == before[o + 1] &&
                    after[o + 2] == before[o + 2] && after[o + 3] == before[o + 3],
                    $"({x},{y}) is outside the region and should have been left alone");
            }
    }

    /// <summary>The blend is handed the coordinates of the pixel it is given.</summary>
    /// <remarks>
    /// A transposed walk would still write the right <i>number</i> of pixels inside a square
    /// region, so the region here is oblong and the values written are the coordinates themselves.
    /// </remarks>
    [Fact]
    public void MapRegionHandsTheBlendItsOwnCoordinates()
    {
        using SKBitmap target = PartiallyTransparent();
        using SKBitmap other = PartiallyTransparent();

        BitmapPixels.MapRegion(target, other, SKRectI.Create(2, 3, 5, 4),
            (t, o, x, y) => new SKColor((byte)x, (byte)y, 0, 255));

        SKColor[] result = target.Pixels;
        for (int y = 3; y < 7; y++)
            for (int x = 2; x < 7; x++)
            {
                SKColor pixel = result[(y * target.Width) + x];
                Assert.Equal((byte)x, pixel.Red);
                Assert.Equal((byte)y, pixel.Green);
            }
    }

    /// <summary>The region walk pairs the two bitmaps at the same coordinates.</summary>
    /// <remarks>
    /// The reason this is a separate method from the index-paired <c>Map</c>: the two agree only at
    /// equal dimensions, and the overprint compositor's coverage surface need not match its target.
    /// </remarks>
    [Fact]
    public void MapRegionPairsTheTwoBitmapsByCoordinate()
    {
        using SKBitmap target = PartiallyTransparent();
        using var other = new SKBitmap(16, 16, SKColorType.Rgba8888, SKAlphaType.Unpremul);

        var marks = new SKColor[16 * 16];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                marks[(y * 16) + x] = new SKColor((byte)((x * 16) + y), 0, 0, 255);
        other.Pixels = marks;

        BitmapPixels.MapRegion(target, other, SKRectI.Create(0, 0, 16, 16),
            (t, o, x, y) => new SKColor(o.Red, 0, 0, 255));

        SKColor[] result = target.Pixels;
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                Assert.Equal((byte)((x * 16) + y), result[(y * 16) + x].Red);
    }

    /// <summary>The MapRegion fallback writes the same pixels at the same coordinates.</summary>
    /// <remarks>
    /// Reached by giving the target a colour type <c>StraightPixelLayout</c> declines, because an
    /// Rgba8888 fixture takes the buffer walk and leaves this arm untouched — the shape that let
    /// a transposed <c>Clear</c> fallback survive.
    /// </remarks>
    [Fact]
    public void TheMapRegionFallbackWritesTheSameCoordinates()
    {
        using var target = new SKBitmap(16, 16, SKColorType.Rgba1010102, SKAlphaType.Premul);
        using SKBitmap other = PartiallyTransparent();

        var seed = new SKColor[16 * 16];
        for (int i = 0; i < seed.Length; i++) seed[i] = new SKColor(0, 0, 0, 255);
        target.Pixels = seed;

        BitmapPixels.ResetCounters();
        BitmapPixels.MapRegion(target, other, SKRectI.Create(2, 3, 5, 4),
            (t, o, x, y) => new SKColor(255, 255, 255, 255));

        Assert.Equal(0, BitmapPixels.BufferWalks);
        Assert.Equal(1, BitmapPixels.PropertyFallbacks);

        SKColor[] result = target.Pixels;
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                bool inside = x >= 2 && x < 7 && y >= 3 && y < 7;
                SKColor pixel = result[(y * 16) + x];
                Assert.True(
                    inside == (pixel.Red > 128),
                    $"({x},{y}) inside={inside} but red={pixel.Red}");
            }
    }

    /// <summary>A surface that can be read but not written falls back.</summary>
    /// <remarks>
    /// <b>Gray8 reads fine and cannot be stored to</b>, so a walk that checked only "can I address
    /// this?" would write three channels into one byte. The check is
    /// <see cref="StraightPixelLayout.CanStore"/>, and this is what fails without it — the SVG
    /// export's soft masks are exactly this surface, so it is reached in earnest.
    /// </remarks>
    [Fact]
    public void ASurfaceThatCannotBeStoredToFallsBack()
    {
        using var bitmap = new SKBitmap(4, 4, SKColorType.Gray8, SKAlphaType.Opaque);
        BitmapPixels.ResetCounters();

        BitmapPixels.Map(bitmap, c => c);

        Assert.Equal(0, BitmapPixels.BufferWalks);
        Assert.Equal(1, BitmapPixels.PropertyFallbacks);
    }

    /// <summary>An opaque four-byte surface is walked, not fallen back from.</summary>
    /// <remarks>
    /// The acceptance control for the refusal above, and the case that matters in production: the
    /// SVG export's images are <c>Rgb888x</c>, so declining them would put every SVG image back on
    /// the allocating path while changing no pixel.
    /// </remarks>
    [Fact]
    public void AnOpaqueFourByteSurfaceIsWalked()
    {
        using var bitmap = new SKBitmap(4, 4, SKColorType.Rgb888x, SKAlphaType.Opaque);
        BitmapPixels.ResetCounters();

        BitmapPixels.Map(bitmap, c => new SKColor(c.Red, c.Green, c.Blue, c.Alpha));

        Assert.Equal(1, BitmapPixels.BufferWalks);
        Assert.Equal(0, BitmapPixels.PropertyFallbacks);
    }

    /// <summary>A 16x16 bitmap whose pixels span the alpha range.</summary>
    private static SKBitmap PartiallyTransparent()
    {
        var bitmap = new SKBitmap(16, 16, SKColorType.Rgba8888, SKAlphaType.Premul);
        var pixels = new SKColor[16 * 16];

        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new SKColor(
                (byte)(i * 7 & 0xFF), (byte)(i * 11 & 0xFF), (byte)(i * 13 & 0xFF), (byte)i);

        bitmap.Pixels = pixels;
        return bitmap;
    }

    // ====================================================================
    //  Fill — the positional write, added because Remap cannot serve one
    // ====================================================================

    /// <summary>
    /// A fill writes exactly what the producer wrote, byte for byte against the array route it
    /// replaces.
    /// </summary>
    /// <remarks>
    /// <b>The strongest correctness claim available, and the one the counters cannot make.</b>
    /// Both routes are compared over a pattern that varies every channel and every alpha,
    /// including the two values the layout's own remarks call out — 0, where the premultiplied
    /// form collapses to transparent black, and 255, where it is the identity.
    /// </remarks>
    [Theory]
    [InlineData(SKColorType.Rgba8888)]
    [InlineData(SKColorType.Bgra8888)]
    public void AFillWritesTheSamePixelsAsTheArrayRouteItReplaces(SKColorType colorType)
    {
        const int w = 13, h = 7;   // deliberately not a power of two, and not square

        using var viaArray = new SKBitmap(w, h, colorType, SKAlphaType.Premul);
        var pixels = new SKColor[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                pixels[(y * w) + x] = Sample(x, y);
        viaArray.Pixels = pixels;

        using var viaFill = new SKBitmap(w, h, colorType, SKAlphaType.Premul);
        BitmapPixels.Fill(viaFill, (row, y) =>
        {
            for (int x = 0; x < w; x++) row[x] = Sample(x, y);
        });

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                Assert.Equal(viaArray.GetPixel(x, y), viaFill.GetPixel(x, y));
    }

    /// <summary>A fill over a colour type the layout can address walks the buffer.</summary>
    [Theory]
    [InlineData(SKColorType.Rgba8888)]
    [InlineData(SKColorType.Bgra8888)]
    [InlineData(SKColorType.Rgb888x)]
    public void AFillOverAnAddressableColorTypeWalksTheBuffer(SKColorType colorType)
    {
        using var bitmap = new SKBitmap(4, 4, colorType, SKAlphaType.Opaque);
        BitmapPixels.ResetCounters();

        BitmapPixels.Fill(bitmap, (row, y) => { for (int x = 0; x < row.Length; x++) row[x] = SKColors.Red; });

        Assert.Equal(1, BitmapPixels.BufferWalks);
        Assert.Equal(0, BitmapPixels.PropertyFallbacks);
    }

    /// <summary>
    /// The acceptance control: a colour type the layout declines still fills, and says it fell back.
    /// </summary>
    /// <remarks>
    /// Without this, a <c>Fill</c> hard-coded to the buffer walk would satisfy the probe above.
    /// This is the assertion it cannot pass — and the fallback must still produce the pixels,
    /// because a fill that declines is a blank image.
    /// </remarks>
    [Fact]
    public void AFillOverAnUnsupportedColorTypeFallsBackAndStillFills()
    {
        using var bitmap = new SKBitmap(4, 4, SKColorType.Argb4444, SKAlphaType.Premul);
        BitmapPixels.ResetCounters();

        BitmapPixels.Fill(bitmap, (row, y) => { for (int x = 0; x < row.Length; x++) row[x] = SKColors.Red; });

        Assert.Equal(0, BitmapPixels.BufferWalks);
        Assert.Equal(1, BitmapPixels.PropertyFallbacks);
        Assert.Equal(SKColors.Red.Red, bitmap.GetPixel(2, 2).Red);
    }

    /// <summary>
    /// A producer that stops part-way leaves transparent black behind, not the previous row.
    /// </summary>
    /// <remarks>
    /// <b>This is the property the scratch row is cleared for, and several callers depend on it.</b>
    /// A decoder whose sample buffer runs short <c>break</c>s out of its loop, and today the
    /// unwritten tail of a fresh <c>SKColor[w*h]</c> supplies transparent black. The scratch row is
    /// reused across rows, so without the clear, row <i>n+1</i> would inherit row <i>n</i>'s
    /// colours wherever the producer stopped — the defect this test exists to make loud.
    /// </remarks>
    [Fact]
    public void AProducerThatStopsPartWayLeavesTransparentBlackNotThePreviousRow()
    {
        const int w = 8, h = 4;
        using var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);

        // Row 0 is filled solid; every later row writes only its first pixel.
        BitmapPixels.Fill(bitmap, (row, y) =>
        {
            if (y == 0) { for (int x = 0; x < w; x++) row[x] = SKColors.Red; }
            else row[0] = SKColors.Blue;
        });

        Assert.Equal(SKColors.Red, bitmap.GetPixel(w - 1, 0));
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(0, 1));
        for (int y = 1; y < h; y++)
            Assert.Equal(new SKColor(0, 0, 0, 0), bitmap.GetPixel(w - 1, y));
    }

    /// <summary>
    /// The <b>fallback</b> clears its scratch row too — the acceptance control for the test above.
    /// </summary>
    /// <remarks>
    /// A mutation removing the clear from the fast path is killed by the test above; the identical
    /// mutation on the fallback survived it, because that fixture's colour type is addressable and
    /// never reaches the allocating arm. Two paths need two fixtures. Argb4444 quantises colour, so
    /// the assertion is on alpha, which survives it exactly.
    /// </remarks>
    [Fact]
    public void TheFillFallbackAlsoClearsItsScratchRow()
    {
        const int w = 8, h = 4;
        using var bitmap = new SKBitmap(w, h, SKColorType.Argb4444, SKAlphaType.Premul);
        BitmapPixels.ResetCounters();

        BitmapPixels.Fill(bitmap, (row, y) =>
        {
            if (y == 0) { for (int x = 0; x < w; x++) row[x] = SKColors.Red; }
            else row[0] = SKColors.Blue;
        });

        Assert.Equal(1, BitmapPixels.PropertyFallbacks);
        Assert.Equal(255, bitmap.GetPixel(w - 1, 0).Alpha);
        for (int y = 1; y < h; y++)
            Assert.Equal(0, bitmap.GetPixel(w - 1, y).Alpha);
    }
    /// <summary>The two-bitmap overload fills both from one pass.</summary>
    /// <remarks>
    /// The CMYK overprint path decodes one sample run into a CMY raster and a K raster. Two
    /// separate fills would decode every sample twice, and converting one of the pair only would
    /// put a buffer walk and an allocating store inside a single loop body.
    /// </remarks>
    [Fact]
    public void TheTwoBitmapFillWritesBothFromOnePass()
    {
        const int w = 5, h = 3;
        using var first = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var second = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        BitmapPixels.ResetCounters();

        int passes = 0;
        BitmapPixels.Fill(first, second, (a, b, y) =>
        {
            passes++;
            for (int x = 0; x < w; x++) { a[x] = SKColors.Red; b[x] = SKColors.Lime; }
        });

        Assert.Equal(h, passes);                       // one pass per row, not two per row
        Assert.Equal(1, BitmapPixels.BufferWalks);     // and one walk, not two
        Assert.Equal(SKColors.Red.Red, first.GetPixel(4, 2).Red);
        Assert.Equal(SKColors.Lime.Green, second.GetPixel(4, 2).Green);
    }

    /// <summary>Mismatched sizes are refused rather than quietly re-paired.</summary>
    /// <remarks>
    /// The same reasoning the two-bitmap <c>Map</c> records: pairing by coordinates and pairing by
    /// buffer position agree only when the bitmaps match, so a fast path that re-paired them would
    /// be a different answer rather than a faster one.
    /// </remarks>
    [Fact]
    public void TheTwoBitmapFillRefusesMismatchedSizes()
    {
        using var first = new SKBitmap(4, 4, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var second = new SKBitmap(4, 5, SKColorType.Rgba8888, SKAlphaType.Opaque);
        BitmapPixels.ResetCounters();

        BitmapPixels.Fill(first, second, (a, b, y) =>
        {
            for (int x = 0; x < a.Length; x++) { a[x] = SKColors.Red; b[x] = SKColors.Lime; }
        });

        Assert.Equal(0, BitmapPixels.BufferWalks);
        Assert.Equal(0, BitmapPixels.PropertyFallbacks);
        Assert.Equal(new SKColor(0, 0, 0, 0), first.GetPixel(0, 0));
    }

    /// <summary>A colour that varies every channel and both alpha extremes.</summary>
    private static SKColor Sample(int x, int y) => new SKColor(
        (byte)((x * 31) & 0xFF),
        (byte)((y * 57) & 0xFF),
        (byte)(((x + y) * 19) & 0xFF),
        (byte)(((x * 7) + (y * 3)) % 3 == 0 ? 255 : ((x + y) % 5 == 0 ? 0 : 128)));
}
