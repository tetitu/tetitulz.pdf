// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Images;

/// <summary>
/// <see cref="StraightPixelLayout"/> against the <c>SKBitmap.Pixels</c> round trip it replaces.
/// </summary>
/// <remarks>
/// <para><b>These are characterisation tests, and deliberately so.</b> The type's contract is
/// stated against the property rather than against the spec, so nothing about the <i>answer</i>
/// can be derived — only compared. That is the whole of what they are for: to make an unintended
/// change loud, including one arriving from a SkiaSharp upgrade rather than from this tree.</para>
///
/// <para><b>A round trip cannot see a read defect, which is why the halves are tested apart.</b>
/// Un-premultiply then premultiply is the identity for any reading that is an inverse, so a
/// composition test passes with a getter that is wrong by one on a thousand pixels — and did.
/// <see cref="AReadIsWithinOneOfTheProperty"/> is the assertion that found it, and the difference
/// it pins is real: SkiaSharp un-premultiplies in float32 and this uses Skia's integer table.</para>
/// </remarks>
public class StraightPixelLayoutTests
{
    /// <summary>How many of the 65 536 pixels a read may differ from the property on.</summary>
    /// <remarks>
    /// <b>Measured, not chosen.</b> If a SkiaSharp upgrade moves this, the thing to check is
    /// whether the divergence is still bounded by one and still confined to partial alpha — the
    /// two claims the type makes that actually matter. The count is here so the move is noticed.
    /// </remarks>
    private const int ExpectedReadDivergence = 621;

    /// <summary>A store is byte-identical to the setter, over every colour and alpha.</summary>
    /// <remarks>
    /// This is the half that has to be exact: it is what ends up in the file and on the screen.
    /// The premultiply is Skia's <c>SkMulDiv255Round</c> and the transparent-black collapse is the
    /// setter's own, so there is no residue to state here.
    /// </remarks>
    [Fact]
    public void AStoreIsByteIdenticalToTheSetter()
    {
        SKColor[] straight = EveryColourAndAlpha();

        using var viaSetter = new SKBitmap(256, 256, SKColorType.Rgba8888, SKAlphaType.Premul);
        viaSetter.Pixels = straight;

        using SKBitmap viaLayout = StoredThroughTheLayout(
            straight, SKColorType.Rgba8888, SKAlphaType.Premul);

        Assert.Equal(viaSetter.GetPixelSpan().ToArray(), viaLayout.GetPixelSpan().ToArray());
    }

    /// <summary>The same on the platform-preferred byte order.</summary>
    /// <remarks>
    /// Bgra8888 is not hypothetical — GAPS §13d records a bitmap arriving in it where Rgba8888 was
    /// assumed, and red and blue swapped in the output. The property is order-agnostic, so this is
    /// the only assertion that can see the offsets being applied to the wrong bytes.
    /// </remarks>
    [Fact]
    public void AStoreIsByteIdenticalOnThePlatformByteOrderToo()
    {
        SKColor[] straight = EveryColourAndAlpha();

        using var viaSetter = new SKBitmap(256, 256, SKColorType.Bgra8888, SKAlphaType.Premul);
        viaSetter.Pixels = straight;

        using SKBitmap viaLayout = StoredThroughTheLayout(
            straight, SKColorType.Bgra8888, SKAlphaType.Premul);

        Assert.Equal(viaSetter.GetPixelSpan().ToArray(), viaLayout.GetPixelSpan().ToArray());
    }

    /// <summary>
    /// A store into an opaque four-byte surface matches the setter, for the colours that surface
    /// can hold.
    /// </summary>
    /// <remarks>
    /// <para><b>Opaque colours only, and that is the honest domain rather than a convenience.</b>
    /// An <c>Rgb888x</c> surface has no alpha channel, so storing a partly transparent colour into
    /// one is a category error — and Skia answers it by collapsing the pixel to black, which is
    /// a reasonable thing to do with a value that cannot be represented and a pointless thing to
    /// reproduce. Over the colours a caller can actually store, the bytes must agree, and this is
    /// what pins the channel order on a format the 8888 tests never touch.</para>
    /// </remarks>
    [Fact]
    public void AStoreIntoAnOpaqueSurfaceMatchesTheSetter()
    {
        var opaque = new SKColor[256 * 256];
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
                opaque[(y * 256) + x] = new SKColor(
                    (byte)x, (byte)(255 - x), (byte)y, 255);

        using var viaSetter = new SKBitmap(256, 256, SKColorType.Rgb888x, SKAlphaType.Opaque);
        viaSetter.Pixels = opaque;

        using SKBitmap viaLayout = StoredThroughTheLayout(
            opaque, SKColorType.Rgb888x, SKAlphaType.Opaque);

        Assert.Equal(viaSetter.GetPixelSpan().ToArray(), viaLayout.GetPixelSpan().ToArray());
    }

    /// <summary>An unpremultiplied surface neither multiplies nor divides.</summary>
    /// <remarks>
    /// The acceptance control for the premultiply: without it, "the bytes match" would also be
    /// satisfied by a layout that premultiplied unconditionally, since the fixture's own
    /// conversions would cancel.
    /// </remarks>
    [Fact]
    public void AnUnpremultipliedSurfaceIsPassedThroughUnchanged()
    {
        SKColor[] straight = EveryColourAndAlpha();

        using SKBitmap viaLayout = StoredThroughTheLayout(
            straight, SKColorType.Rgba8888, SKAlphaType.Unpremul);

        Assert.True(StraightPixelLayout.TryRead(viaLayout, out StraightPixelLayout layout));
        Span<byte> buffer = viaLayout.GetPixelSpan();

        int changed = 0;
        for (int y = 0; y < 256; y++)
        {
            Span<byte> row = layout.Row(buffer, y);
            for (int x = 0; x < 256; x++)
                if (layout.Get(row, x) != straight[(y * 256) + x]) changed++;
        }

        Assert.Equal(0, changed);
    }

    /// <summary>
    /// A read agrees with the property except on a pinned handful of pixels, and there by one.
    /// </summary>
    /// <remarks>
    /// <b>The two bounds are the claim; the count is the characterisation.</b> A divergence of one
    /// on a partially transparent pixel is one part in 255 on a pixel that is already the product
    /// of a lossy premultiply, and it cannot survive a re-premultiply — so it reaches output only
    /// through a caller that transforms colour. A divergence at alpha 0 or 255 would be something
    /// else entirely: those are the pixels every ordinary image is made of.
    /// </remarks>
    [Fact]
    public void AReadIsWithinOneOfTheProperty()
    {
        SKColor[] straight = EveryColourAndAlpha();

        using var bitmap = new SKBitmap(256, 256, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Pixels = straight;

        SKColor[] viaProperty = bitmap.Pixels;
        Assert.True(StraightPixelLayout.TryRead(bitmap, out StraightPixelLayout layout));
        Span<byte> buffer = bitmap.GetPixelSpan();

        int differing = 0, maxDelta = 0, atOpaque = 0, atTransparent = 0;

        for (int y = 0; y < 256; y++)
        {
            Span<byte> row = layout.Row(buffer, y);
            for (int x = 0; x < 256; x++)
            {
                SKColor mine = layout.Get(row, x);
                SKColor theirs = viaProperty[(y * 256) + x];
                if (mine == theirs) continue;

                differing++;
                maxDelta = Math.Max(maxDelta, Math.Abs(mine.Red - theirs.Red));
                maxDelta = Math.Max(maxDelta, Math.Abs(mine.Green - theirs.Green));
                maxDelta = Math.Max(maxDelta, Math.Abs(mine.Blue - theirs.Blue));
                maxDelta = Math.Max(maxDelta, Math.Abs(mine.Alpha - theirs.Alpha));
                if (theirs.Alpha == 255) atOpaque++;
                if (theirs.Alpha == 0) atTransparent++;
            }
        }

        Assert.Equal(0, atOpaque);
        Assert.Equal(0, atTransparent);
        Assert.Equal(1, maxDelta);
        Assert.Equal(ExpectedReadDivergence, differing);
    }

    /// <summary>Read then write is exactly the identity, on every pixel.</summary>
    /// <remarks>
    /// This is what makes the read divergence above harmless to a caller that changes nothing:
    /// the difference is below the resolution of the stored byte, so the re-premultiply absorbs
    /// it. It is a weaker claim than the two halves, and is here because it is the one a converted
    /// call site's untouched pixels actually rely on.
    /// </remarks>
    [Fact]
    public void AReadFollowedByAStoreLeavesEveryPixelAlone()
    {
        using var bitmap = new SKBitmap(256, 256, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Pixels = EveryColourAndAlpha();

        byte[] before = bitmap.GetPixelSpan().ToArray();

        Assert.True(StraightPixelLayout.TryRead(bitmap, out StraightPixelLayout layout));
        Span<byte> buffer = bitmap.GetPixelSpan();
        for (int y = 0; y < 256; y++)
        {
            Span<byte> row = layout.Row(buffer, y);
            for (int x = 0; x < 256; x++) layout.Set(row, x, layout.Get(row, x));
        }
        bitmap.NotifyPixelsChanged();

        Assert.Equal(before, bitmap.GetPixelSpan().ToArray());
    }

    /// <summary>A fully transparent pixel is stored as transparent black.</summary>
    /// <remarks>
    /// The setter's own behaviour, and the one that looks most like a quirk worth dropping: an
    /// invisible pixel renders the same either way, but keeping the decoded colour turns a uniform
    /// transparent region into noise for the PNG encoder the SVG export runs it through.
    /// </remarks>
    [Fact]
    public void AFullyTransparentPixelKeepsNoColour()
    {
        using var bitmap = new SKBitmap(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul);
        Assert.True(StraightPixelLayout.TryRead(bitmap, out StraightPixelLayout layout));

        Span<byte> buffer = bitmap.GetPixelSpan();
        layout.Set(layout.Row(buffer, 0), 0, new SKColor(200, 100, 50, 0));
        bitmap.NotifyPixelsChanged();

        Assert.Equal(new byte[] { 0, 0, 0, 0 }, bitmap.GetPixelSpan().ToArray());
    }

    /// <summary>A colour type with no trustworthy byte offsets is declined, not reinterpreted.</summary>
    /// <remarks>
    /// <para>Declining is the point: a caller reading this as "false means empty" would silently
    /// skip the operation, so every call site keeps its allocating path for this answer.</para>
    ///
    /// <para><b>Rgba1010102 rather than Gray8, and the difference is the whole test.</b> This one
    /// is four bytes per pixel, so it clears the stride guard and only the colour-type check can
    /// refuse it. A Gray8 fixture is declined for having a short row — which made deleting the
    /// colour-type check entirely a surviving mutation, against a test whose name said it was the
    /// thing under test.</para>
    /// </remarks>
    [Fact]
    public void AnUnsupportedColorTypeIsDeclined()
    {
        using var bitmap = new SKBitmap(2, 2, SKColorType.Rgba1010102, SKAlphaType.Premul);

        Assert.Equal(bitmap.RowBytes, bitmap.Width * 4);   // the stride guard cannot be what answers
        Assert.False(StraightPixelLayout.TryRead(bitmap, out _));
    }

    /// <summary>An opaque four-byte surface reads as opaque and keeps its padding byte.</summary>
    /// <remarks>
    /// <b>Rgb888x is what the SVG export's images decode to.</b> Handling only the two 8888 orders
    /// made this type decline every surface that export produces, so three converted call sites
    /// fell back to the allocating property on every image and the whole conversion was inert —
    /// with identical pixels, so nothing but the fallback counter could say so. Its fourth byte is
    /// padding rather than alpha: reading it as alpha would report whatever the allocator left.
    /// </remarks>
    [Fact]
    public void AnOpaqueFourByteSurfaceReadsAsOpaque()
    {
        using var bitmap = new SKBitmap(4, 1, SKColorType.Rgb888x, SKAlphaType.Opaque);
        Assert.True(StraightPixelLayout.TryRead(bitmap, out StraightPixelLayout layout));
        Assert.True(layout.CanStore);
        Assert.Equal(4, layout.BytesPerPixel);

        Span<byte> buffer = bitmap.GetPixelSpan();
        Span<byte> row = layout.Row(buffer, 0);

        // A store keeps the three channels and drops the alpha, which is what the surface means.
        layout.Set(row, 1, new SKColor(10, 20, 30, 40));

        Assert.Equal(new SKColor(10, 20, 30, 255), layout.Get(row, 1));

        // And the BYTES stay opaque, not merely what Get reports. Nothing in this tree reads the
        // padding byte of an Rgb888x surface, so recording a stray alpha there is invisible to
        // every rendering assertion -- which is exactly why it is asserted directly. A buffer that
        // disagrees with the alpha type its own bitmap declares is a trap for the next reader.
        Assert.Equal(255, buffer[(1 * 4) + 3]);
    }

    /// <summary>A grey surface reads as grey and refuses to be written.</summary>
    /// <remarks>
    /// <b>Gray8 is what the SVG export's soft masks decode to</b>, and it is one byte per pixel:
    /// there is nowhere to put three channels back, so <see cref="StraightPixelLayout.CanStore"/>
    /// is false and every writing caller has to check it. Reading is the whole point — a mask's
    /// luminosity is exactly one channel.
    /// </remarks>
    [Fact]
    public void AGreySurfaceReadsAsGreyAndCannotBeStoredTo()
    {
        using var bitmap = new SKBitmap(4, 1, SKColorType.Gray8, SKAlphaType.Opaque);
        Assert.True(StraightPixelLayout.TryRead(bitmap, out StraightPixelLayout layout));
        Assert.False(layout.CanStore);
        Assert.Equal(1, layout.BytesPerPixel);

        Span<byte> buffer = bitmap.GetPixelSpan();
        buffer[0] = 10; buffer[1] = 90; buffer[2] = 200; buffer[3] = 255;

        Span<byte> row = layout.Row(buffer, 0);
        Assert.Equal(new SKColor(90, 90, 90, 255), layout.Get(row, 1));
        Assert.Equal(new SKColor(200, 200, 200, 255), layout.Get(row, 2));

        // A store is a no-op rather than two channels silently thrown away.
        layout.Set(row, 1, new SKColor(1, 2, 3, 255));
        Assert.Equal((byte)90, buffer[1]);
    }

    /// <summary>A pixel format with no byte layout this understands is declined too.</summary>
    /// <remarks>
    /// <c>Argb4444</c> packs four channels into two bytes, so no byte offset addresses a channel.
    /// The other half of <see cref="StraightPixelLayout.TryRead"/>'s refusal, and separate because
    /// it is a separate reason.
    /// </remarks>
    [Fact]
    public void APackedPixelFormatIsDeclined()
    {
        using var bitmap = new SKBitmap(2, 2, SKColorType.Argb4444, SKAlphaType.Premul);

        Assert.False(StraightPixelLayout.TryRead(bitmap, out _));
    }

    // ==================================================================== helpers

    /// <summary>
    /// Every (channel value, alpha) pair, laid out 256 wide by 256 tall.
    /// </summary>
    /// <remarks>
    /// The three channels carry three different values, so a read or write that confused two of
    /// them cannot pass — a bitmap whose channels all held the same number would be invariant
    /// under exactly the swap these tests have to detect.
    /// </remarks>
    private static SKColor[] EveryColourAndAlpha()
    {
        var straight = new SKColor[256 * 256];

        for (int a = 0; a < 256; a++)
            for (int v = 0; v < 256; v++)
                straight[(a * 256) + v] = new SKColor(
                    (byte)v, (byte)(255 - v), (byte)((v + 85) & 0xFF), (byte)a);

        return straight;
    }

    private static SKBitmap StoredThroughTheLayout(
        SKColor[] straight, SKColorType colorType, SKAlphaType alphaType)
    {
        var bitmap = new SKBitmap(256, 256, colorType, alphaType);
        Assert.True(StraightPixelLayout.TryRead(bitmap, out StraightPixelLayout layout));

        Span<byte> buffer = bitmap.GetPixelSpan();
        for (int y = 0; y < 256; y++)
        {
            Span<byte> row = layout.Row(buffer, y);
            for (int x = 0; x < 256; x++) layout.Set(row, x, straight[(y * 256) + x]);
        }
        bitmap.NotifyPixelsChanged();

        return bitmap;
    }
}
