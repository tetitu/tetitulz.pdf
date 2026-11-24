// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Text;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Imaging;

/// <summary>
/// <see cref="PngImageDecoder"/> — the one PNG decoder, asserted on decoded samples.
/// </summary>
/// <remarks>
/// <para><b>These assert the decoder's own contract, which is what neither predecessor had.</b>
/// Both copies were reached only through an <i>embedding</i> path — Builder's exporter and
/// DocxToPdf's image renderer — so every assertion about them ran through a PDF image XObject, and
/// the alpha plane in particular was observable only as a <c>/SMask</c> that one of the two threw
/// away before anyone could look. <c>Alpha</c> is now a first-class output and is tested as one.</para>
///
/// <para>Builder's <c>PngDecodeTests</c> still exists and still asserts through the exporter. That
/// is deliberate rather than redundant: it covers the decoder's output <i>reaching an XObject</i>,
/// including the 32x32 Adam7 sizing its own remarks explain, which is a different claim from the
/// decoder being correct.</para>
/// </remarks>
public class PngImageDecoderTests
{
    // Both channels vary with both axes, so a pixel landing in the wrong place cannot
    // coincidentally match — the failure mode an interlacing or stride bug produces.
    private static byte R(int x, int y) => (byte)(8 * x + 3);
    private static byte G(int x, int y) => (byte)(8 * y + 5);
    private static byte B(int x, int y) => (byte)((x * 5 + y * 11) & 0xFF);

    [Fact]
    public void Truecolour_DecodesEveryPixel()
    {
        byte[] png = Rgb8(6, 5);

        Assert.True(PngImageDecoder.TryDecode(png, out var img));
        Assert.Equal(6, img.Width);
        Assert.Equal(5, img.Height);

        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 6; x++)
            {
                int p = (y * 6 + x) * 3;
                Assert.Equal(R(x, y), img.Rgb[p]);
                Assert.Equal(G(x, y), img.Rgb[p + 1]);
                Assert.Equal(B(x, y), img.Rgb[p + 2]);
            }
    }

    /// <summary>
    /// <b>An image that cannot be transparent has a null alpha plane, not an opaque one.</b> The
    /// contract a caller reads to decide whether to write an <c>/SMask</c> at all.
    /// </summary>
    [Fact]
    public void AnOpaqueTruecolourImage_HasNoAlphaPlane()
    {
        Assert.True(PngImageDecoder.TryDecode(Rgb8(4, 4), out var img));

        Assert.Null(img.Alpha);
    }

    /// <summary>
    /// Colour type 6 yields the alpha plane — the case a signature logo with a transparent
    /// background depends on.
    /// </summary>
    [Fact]
    public void TruecolourWithAlpha_YieldsThePerPixelAlpha()
    {
        // Pixel (x,y) gets alpha = x * 60, so the row runs 0, 60, 120, 180 — transparent through
        // to nearly opaque, and a decoder that returned a constant fails on the second pixel.
        var raw = new List<byte>();
        for (int y = 0; y < 3; y++)
        {
            raw.Add(0);                                   // filter: None
            for (int x = 0; x < 4; x++)
            {
                raw.Add(R(x, y)); raw.Add(G(x, y)); raw.Add(B(x, y));
                raw.Add((byte)(x * 60));
            }
        }

        byte[] png = Assemble(4, 3, colorType: 6, bitDepth: 8, raw.ToArray(), interlace: 0);

        Assert.True(PngImageDecoder.TryDecode(png, out var img));
        Assert.NotNull(img.Alpha);

        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 4; x++)
                Assert.Equal((byte)(x * 60), img.Alpha[y * 4 + x]);
    }

    /// <summary>
    /// A colour-key <c>tRNS</c> on a <b>non-palette</b> image (PNG §11.3.2.1): every pixel exactly
    /// matching the key is fully transparent, everything else opaque.
    /// </summary>
    /// <remarks>
    /// This is one of the two capabilities the union exists for — the Builder copy read only the
    /// palette form, so a logo saved as colour type 2 with a transparent background drew its
    /// background as a solid rectangle. Its own comment said the other decoder "has honoured both
    /// all along", which is the prose form of this test.
    /// </remarks>
    [Fact]
    public void AColourKeyOnATruecolourImage_MakesTheKeyedPixelsTransparent()
    {
        var raw = new List<byte>();
        for (int y = 0; y < 2; y++)
        {
            raw.Add(0);
            for (int x = 0; x < 3; x++)
            {
                // (1,y) is the keyed colour; the others are not.
                if (x == 1) { raw.Add(10); raw.Add(20); raw.Add(30); }
                else { raw.Add(200); raw.Add(100); raw.Add(50); }
            }
        }

        // tRNS for colour type 2 is three 2-byte samples, whatever the bit depth.
        byte[] trns = { 0, 10, 0, 20, 0, 30 };
        byte[] png = Assemble(3, 2, colorType: 2, bitDepth: 8, raw.ToArray(), interlace: 0, trns: trns);

        Assert.True(PngImageDecoder.TryDecode(png, out var img));
        Assert.NotNull(img.Alpha);

        for (int y = 0; y < 2; y++)
        {
            Assert.Equal(255, img.Alpha[y * 3 + 0]);
            Assert.Equal(0, img.Alpha[y * 3 + 1]);   // the keyed pixel
            Assert.Equal(255, img.Alpha[y * 3 + 2]);
        }
    }

    /// <summary>
    /// Greyscale expands to RGB with all three channels equal — a decoder that packed one channel
    /// where three were expected would shift every pixel after the first.
    /// </summary>
    [Fact]
    public void Greyscale_ExpandsToEqualRgbChannels()
    {
        var raw = new List<byte>();
        for (int y = 0; y < 2; y++)
        {
            raw.Add(0);
            for (int x = 0; x < 3; x++) raw.Add((byte)(40 + x * 30 + y));
        }

        byte[] png = Assemble(3, 2, colorType: 0, bitDepth: 8, raw.ToArray(), interlace: 0);

        Assert.True(PngImageDecoder.TryDecode(png, out var img));

        for (int i = 0; i < 6; i++)
        {
            byte expected = (byte)(40 + (i % 3) * 30 + i / 3);
            Assert.Equal(expected, img.Rgb[i * 3]);
            Assert.Equal(expected, img.Rgb[i * 3 + 1]);
            Assert.Equal(expected, img.Rgb[i * 3 + 2]);
        }
    }

    /// <summary>
    /// A 16-bit sample scales by its <b>whole</b> value, not its high byte. Reading only the high
    /// byte and then dividing by 65535 is what once made every 16-bit greyscale PNG come out pure
    /// black; <see cref="RasterSample"/> records it.
    /// </summary>
    [Fact]
    public void SixteenBitSamples_ScaleFromTheWholeSample()
    {
        // One pixel, mid-grey: 0x8000 of 0xFFFF is 128 after rounding.
        byte[] raw = { 0, 0x80, 0x00 };
        byte[] png = Assemble(1, 1, colorType: 0, bitDepth: 16, raw, interlace: 0);

        Assert.True(PngImageDecoder.TryDecode(png, out var img));
        Assert.Equal(128, img.Rgb[0]);
    }

    /// <summary>An Adam7 image decodes to the same pixels as the sequential one.</summary>
    /// <remarks>
    /// 32x32 for the reason Builder's suite documents: an Adam7 stream carries ~1.875 filter bytes
    /// per image row, so it exceeds the sequential <c>(stride+1)*height</c> inflate cap only once
    /// the image is taller than roughly 18 rows. An 8x8 fixture passes with interlacing deleted.
    /// </remarks>
    [Fact]
    public void Adam7_DecodesToTheSamePixelsAsTheSequentialImage()
    {
        Assert.True(PngImageDecoder.TryDecode(Rgb8(32, 32), out var sequential));
        Assert.True(PngImageDecoder.TryDecode(Interlaced(32, 32), out var interlaced));

        Assert.Equal(sequential.Width, interlaced.Width);
        Assert.Equal(sequential.Rgb, interlaced.Rgb);
    }

    // ------------------------------------------------------------------
    //  Hostile input — refused, never thrown
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0)]    // a zero-length IHDR that the chunk walk accepts
    [InlineData(12)]   // one byte short of the 13 PNG §11.2.2 requires
    public void AMalformedIhdr_IsRefusedRatherThanIndexingPastTheBuffer(int ihdrLength)
    {
        var png = new List<byte> { 137, 80, 78, 71, 13, 10, 26, 10 };
        png.AddRange(BeInt(ihdrLength));
        png.AddRange(Encoding.ASCII.GetBytes("IHDR"));
        png.AddRange(new byte[ihdrLength]);
        png.AddRange(BeInt(0));   // CRC placeholder

        Assert.False(PngImageDecoder.TryDecode(png.ToArray(), out var img));
        Assert.Null(img);

        // Named, not merely refused — and named by the WHOLE phrase, not by the token "IHDR".
        // Substring-matching "IHDR" alone did not discriminate: with the length check deleted, the
        // 12-byte case reads a zero width out of the truncated header and is refused by the
        // dimension guard, whose message *also* contains "IHDR". The refusal was real and came from
        // the wrong place — the trap CLAUDE.md records for the CRL whitelist.
        Assert.Contains("shorter than the 13 bytes", PngImageDecoder.LastError,
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// A file whose IHDR was never reached is blamed on the chunk that ended the walk; a file whose
    /// IHDR <b>was</b> read and genuinely declares a zero dimension is blamed on the IHDR. The
    /// root-cause fallback is consulted only for the first: consulted unconditionally, it would tell
    /// the second case the problem was a later chunk.
    /// </summary>
    [Fact]
    public void AZeroWidthIhdr_IsBlamedOnTheIhdrRatherThanOnALaterTruncatedChunk()
    {
        var png = new List<byte> { 137, 80, 78, 71, 13, 10, 26, 10 };
        png.AddRange(Ihdr(0, 4));                       // the IHDR itself declares width 0
        png.AddRange(BeInt(int.MaxValue - 4));          // ...and a later chunk aborts the walk
        png.AddRange(Encoding.ASCII.GetBytes("IDAT"));
        png.AddRange(new byte[32]);

        Assert.False(PngImageDecoder.TryDecode(png.ToArray(), out _));

        Assert.Contains("non-positive width or height", PngImageDecoder.LastError,
                        StringComparison.Ordinal);
        // The other direction, in the same fixture: the later chunk is NOT the reason, so its
        // message must not be the one reported. Without this the test passes on the old code.
        Assert.DoesNotContain("past the end", PngImageDecoder.LastError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A PNG with no IHDR at all says so, rather than claiming an IHDR it never saw declares a
    /// zero width.
    /// </summary>
    [Fact]
    public void AFileWithNoIhdrChunk_SaysTheHeaderIsMissing()
    {
        var png = new List<byte> { 137, 80, 78, 71, 13, 10, 26, 10 };
        png.AddRange(Chunk("IEND", Array.Empty<byte>()));

        Assert.False(PngImageDecoder.TryDecode(png.ToArray(), out _));

        Assert.Contains("no IHDR chunk", PngImageDecoder.LastError, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  Palette, and the header combinations PNG §11.2.2 does not define
    // ------------------------------------------------------------------

    /// <summary>
    /// The acceptance control for the two refusals below: a well-formed palette image decodes to
    /// the palette's own colours, so neither guard is passing by refusing everything.
    /// </summary>
    [Fact]
    public void APaletteImage_DecodesToThePaletteColours()
    {
        // Three entries, each distinct in all three channels.
        byte[] plte = { 10, 20, 30, 200, 100, 50, 7, 8, 9 };
        var raw = new List<byte>();
        for (int y = 0; y < 2; y++)
        {
            raw.Add(0);
            for (int x = 0; x < 3; x++) raw.Add((byte)((x + y) % 3));   // indices walk the palette
        }

        byte[] png = Assemble(3, 2, colorType: 3, bitDepth: 8, raw.ToArray(), interlace: 0,
                              plte: plte);

        Assert.True(PngImageDecoder.TryDecode(png, out var img));

        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 3; x++)
            {
                int e = ((x + y) % 3) * 3, p = (y * 3 + x) * 3;
                Assert.Equal(plte[e], img.Rgb[p]);
                Assert.Equal(plte[e + 1], img.Rgb[p + 1]);
                Assert.Equal(plte[e + 2], img.Rgb[p + 2]);
            }
    }

    /// <summary>
    /// Colour type 3 with no PLTE is refused. The palette lookup tolerated a null palette by
    /// substituting 0 for every channel, so such a file decoded "successfully" to a solid black
    /// image — a confidently wrong answer with <c>TryDecode</c> true and <c>LastError</c> null.
    /// </summary>
    [Fact]
    public void APaletteImageWithNoPlteChunk_IsRefusedRatherThanDecodedAsBlack()
    {
        var raw = new List<byte>();
        for (int y = 0; y < 2; y++)
        {
            raw.Add(0);
            for (int x = 0; x < 3; x++) raw.Add(1);
        }

        byte[] png = Assemble(3, 2, colorType: 3, bitDepth: 8, raw.ToArray(), interlace: 0);

        Assert.False(PngImageDecoder.TryDecode(png, out var img));
        Assert.Null(img);
        Assert.Contains("PLTE", PngImageDecoder.LastError, StringComparison.Ordinal);
    }

    /// <summary>
    /// PNG §11.2.2 Table 11.1 allows only certain bit-depth/colour-type pairs, and the pair was
    /// never checked — each half was validated alone. For colour types 2/4/6 the row walk steps
    /// <c>bitDepth / 8</c> bytes per sample, which is <b>zero</b> below 8 bpc, so every pixel in a
    /// row decoded from byte 0: a flat image, refused by nothing.
    /// </summary>
    [Theory]
    [InlineData(2, 1)]    // truecolour at 1 bpc — step 0
    [InlineData(2, 4)]    // truecolour at 4 bpc — step 0
    [InlineData(4, 2)]    // grey+alpha at 2 bpc — step 0
    [InlineData(6, 4)]    // RGBA at 4 bpc — step 0
    [InlineData(3, 16)]   // palette at 16 bpc — indices past any legal PLTE
    public void AnUndefinedBitDepthAndColourTypePair_IsRefusedByName(int colorType, int bitDepth)
    {
        byte[] png = Assemble(4, 4, (byte)colorType, (byte)bitDepth, new byte[64], interlace: 0,
                              plte: new byte[] { 1, 2, 3 });

        Assert.False(PngImageDecoder.TryDecode(png, out var img));
        Assert.Null(img);

        // The pair specifically. "bit depth" alone is also in the depth guard's message and
        // "colour type" alone is in the colour-type guard's, so neither token discriminates.
        Assert.Contains("does not allow", PngImageDecoder.LastError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The legal low-depth pairs still decode, so the pair check refuses the undefined combinations
    /// rather than every sub-byte image. 4-bit greyscale is colour type 0's own case.
    /// </summary>
    [Fact]
    public void ALegalLowDepthPair_StillDecodes()
    {
        // 4 bpc greyscale, one row of four pixels packed two per byte: 0x0, 0x5, 0xA, 0xF.
        byte[] raw = { 0, 0x05, 0xAF };
        byte[] png = Assemble(4, 1, colorType: 0, bitDepth: 4, raw, interlace: 0);

        Assert.True(PngImageDecoder.TryDecode(png, out var img));

        // Scaled from max 15: 0, 85, 170, 255.
        Assert.Equal(0, img.Rgb[0]);
        Assert.Equal(85, img.Rgb[3]);
        Assert.Equal(170, img.Rgb[6]);
        Assert.Equal(255, img.Rgb[9]);
    }

    /// <summary>
    /// A chunk length near <c>int.MaxValue</c> makes <c>dataStart + len</c> wrap negative in int
    /// arithmetic, which passes a naive bounds check and then reads past the buffer.
    /// </summary>
    [Fact]
    public void AnOversizedChunkLength_DoesNotWrapPastTheBoundsCheck()
    {
        var png = new List<byte> { 137, 80, 78, 71, 13, 10, 26, 10 };
        png.AddRange(BeInt(int.MaxValue - 4));
        png.AddRange(Encoding.ASCII.GetBytes("IDAT"));
        png.AddRange(new byte[32]);

        Assert.False(PngImageDecoder.TryDecode(png.ToArray(), out _));

        // Again the reason, not just the outcome: narrowing the guard to int arithmetic still
        // refuses, because the wrapped length then throws inside the chunk copy and the outer
        // catch turns it into false. That is a crash being swallowed, not a guard working.
        Assert.Contains("past the end", PngImageDecoder.LastError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })]   // signature only
    public void NonPngOrTruncatedInput_IsRefused(byte[] bytes)
    {
        Assert.False(PngImageDecoder.TryDecode(bytes, out var img));
        Assert.Null(img);
        Assert.NotNull(PngImageDecoder.LastError);
    }

    /// <summary>
    /// The reason slot is cleared by a <b>successful</b> decode, so a caller reading it after one
    /// cannot see the previous call's failure. Per-thread storage is not per-call scope — the
    /// defect CLAUDE.md records for <c>LastPermsValidation</c>.
    /// </summary>
    [Fact]
    public void ASuccessfulDecode_ClearsTheReasonLeftByAFailedOne()
    {
        Assert.False(PngImageDecoder.TryDecode(new byte[] { 1, 2, 3 }, out _));
        Assert.NotNull(PngImageDecoder.LastError);

        Assert.True(PngImageDecoder.TryDecode(Rgb8(2, 2), out _));
        Assert.Null(PngImageDecoder.LastError);
    }

    /// <summary>
    /// An IHDR declaring an enormous image is refused before anything is allocated for it — the
    /// dimension cap. 30000x30000 would otherwise drive a <c>width*height*3</c> allocation.
    /// </summary>
    [Fact]
    public void AnOversizedDeclaredImage_IsRefusedWithoutAllocating()
    {
        byte[] png = Assemble(30000, 30000, colorType: 2, bitDepth: 8, new byte[] { 0 }, interlace: 0);

        Assert.False(PngImageDecoder.TryDecode(png, out _));

        // The dimension ceiling specifically. With it deleted the call still returns false — the
        // one-byte IDAT cannot inflate to what the header describes — so asserting only the
        // refusal tests the *next* guard down and leaves this one free to be removed.
        Assert.Contains("megapixel", PngImageDecoder.LastError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The greyscale colour key (<c>tRNS</c> on colour type 0), which is a separate branch from the
    /// truecolour one two lines above it in the decoder.
    /// </summary>
    /// <remarks>
    /// Added because deleting that branch left all fifteen other cases green: the truecolour test
    /// covered colour type 2's key and nothing covered type 0's. <b>Two branches of one rule need
    /// two fixtures</b>, the same finding as the three separate <c>RequireTransitionName</c> calls.
    /// </remarks>
    [Fact]
    public void AColourKeyOnAGreyscaleImage_MakesTheKeyedPixelsTransparent()
    {
        var raw = new List<byte>();
        for (int y = 0; y < 2; y++)
        {
            raw.Add(0);
            for (int x = 0; x < 3; x++) raw.Add(x == 1 ? (byte)77 : (byte)200);
        }

        // tRNS for colour type 0 is a single 2-byte sample.
        byte[] png = Assemble(3, 2, colorType: 0, bitDepth: 8, raw.ToArray(), interlace: 0,
                              trns: new byte[] { 0, 77 });

        Assert.True(PngImageDecoder.TryDecode(png, out var img));
        Assert.NotNull(img.Alpha);

        for (int y = 0; y < 2; y++)
        {
            Assert.Equal(255, img.Alpha[y * 3 + 0]);
            Assert.Equal(0, img.Alpha[y * 3 + 1]);
            Assert.Equal(255, img.Alpha[y * 3 + 2]);
        }
    }

    /// <summary>
    /// Every row filter of PNG §9.2 is undone: rows filtered by Sub, Up, Average and Paeth in turn
    /// (filtered here by the specification's formulas) decode to the same pixels as the unfiltered
    /// image. Fixtures that write filter 0 alone would pass a decoder that never unfilters a row.
    /// </summary>
    [Fact]
    public void RowsFilteredByEachFilterType_DecodeToTheSamePixels()
    {
        const int w = 6, h = 8;
        Assert.True(PngImageDecoder.TryDecode(Filtered(w, h, y => (byte)(1 + y % 4)), out var img));

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = (y * w + x) * 3;
                Assert.Equal(R(x, y), img.Rgb[p]);
                Assert.Equal(G(x, y), img.Rgb[p + 1]);
                Assert.Equal(B(x, y), img.Rgb[p + 2]);
            }
    }

    /// <summary>
    /// A row filter type above 4 is refused: PNG §9.2 defines five, and a PDF <c>/Predictor</c> row
    /// that passes such a type through unchanged is the predictor's rule, not the format's. The
    /// control is the same image with every row a defined type.
    /// </summary>
    [Fact]
    public void ARowFilterTypeAboveFour_IsRefused()
    {
        Assert.True(PngImageDecoder.TryDecode(Filtered(4, 3, y => (byte)(y + 1)), out _));
        Assert.False(PngImageDecoder.TryDecode(Filtered(4, 3, y => y == 1 ? (byte)5 : (byte)0), out var img));
        Assert.Null(img);
    }

    /// <summary>
    /// <see cref="Rgb8"/>'s image with row y written under filter <paramref name="filterOf"/>(y): the
    /// four defined types encoded by PNG §9.2's formulas; any other type writes the row unfiltered.
    /// </summary>
    private static byte[] Filtered(int w, int h, Func<int, byte> filterOf)
    {
        const int bpp = 3;
        int stride = w * bpp;
        var raw = new List<byte>();
        var prev = new byte[stride];
        for (int y = 0; y < h; y++)
        {
            var row = new byte[stride];
            for (int x = 0; x < w; x++) { row[x * 3] = R(x, y); row[x * 3 + 1] = G(x, y); row[x * 3 + 2] = B(x, y); }

            byte type = filterOf(y);
            raw.Add(type);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? row[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                int predicted = type switch
                {
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => 0,
                };
                raw.Add((byte)(row[i] - predicted));
            }
            prev = row;
        }
        return Assemble(w, h, colorType: 2, bitDepth: 8, raw.ToArray(), interlace: 0);

        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }

    // ------------------------------------------------------------------
    //  Fixture
    // ------------------------------------------------------------------

    private static byte[] Rgb8(int w, int h)
    {
        var raw = new List<byte>();
        for (int y = 0; y < h; y++)
        {
            raw.Add(0);                                   // filter: None
            for (int x = 0; x < w; x++) { raw.Add(R(x, y)); raw.Add(G(x, y)); raw.Add(B(x, y)); }
        }
        return Assemble(w, h, colorType: 2, bitDepth: 8, raw.ToArray(), interlace: 0);
    }

    /// <summary>The same image as <see cref="Rgb8"/>, stored as seven Adam7 passes.</summary>
    private static byte[] Interlaced(int w, int h)
    {
        int[] xo = { 0, 4, 0, 2, 0, 1, 0 }, yo = { 0, 0, 4, 0, 2, 0, 1 };
        int[] xs = { 8, 8, 4, 4, 2, 2, 1 }, ys = { 8, 8, 8, 4, 4, 2, 2 };

        var raw = new List<byte>();
        for (int p = 0; p < 7; p++)
        {
            int pw = (w - xo[p] + xs[p] - 1) / xs[p];
            int ph = (h - yo[p] + ys[p] - 1) / ys[p];
            if (pw <= 0 || ph <= 0) continue;

            for (int ry = 0; ry < ph; ry++)
            {
                raw.Add(0);
                for (int rx = 0; rx < pw; rx++)
                {
                    int x = xo[p] + rx * xs[p], y = yo[p] + ry * ys[p];
                    raw.Add(R(x, y)); raw.Add(G(x, y)); raw.Add(B(x, y));
                }
            }
        }
        return Assemble(w, h, colorType: 2, bitDepth: 8, raw.ToArray(), interlace: 1);
    }

    /// <summary>A well-formed IHDR chunk, so a hostile fixture can pair one with a bad chunk.</summary>
    private static byte[] Ihdr(int w, int h, byte colorType = 2, byte bitDepth = 8, byte interlace = 0)
    {
        var ihdr = new List<byte>();
        ihdr.AddRange(BeInt(w));
        ihdr.AddRange(BeInt(h));
        ihdr.Add(bitDepth);
        ihdr.Add(colorType);
        ihdr.Add(0);            // compression
        ihdr.Add(0);            // filter
        ihdr.Add(interlace);
        return Chunk("IHDR", ihdr.ToArray());
    }

    private static byte[] Assemble(int w, int h, byte colorType, byte bitDepth, byte[] raw,
                                   byte interlace, byte[] trns = null, byte[] plte = null)
    {
        var png = new List<byte> { 137, 80, 78, 71, 13, 10, 26, 10 };
        png.AddRange(Ihdr(w, h, colorType, bitDepth, interlace));

        if (plte != null) png.AddRange(Chunk("PLTE", plte));
        if (trns != null) png.AddRange(Chunk("tRNS", trns));

        // Core's own encoder writes the zlib wrapping an IDAT needs.
        png.AddRange(Chunk("IDAT", FlateEncodeFilter.Encode(raw, CompressionLevel.Optimal)));
        png.AddRange(Chunk("IEND", Array.Empty<byte>()));
        return png.ToArray();
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var c = new List<byte>();
        c.AddRange(BeInt(data.Length));
        byte[] typed = Encoding.ASCII.GetBytes(type);
        c.AddRange(typed);
        c.AddRange(data);

        var crcInput = new List<byte>(typed);
        crcInput.AddRange(data);
        c.AddRange(BeInt(unchecked((int)Crc32(crcInput.ToArray()))));
        return c.ToArray();
    }

    private static byte[] BeInt(int v)
        => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320 & (uint)(-(int)(crc & 1)));
        }
        return crc ^ 0xFFFFFFFF;
    }
}
