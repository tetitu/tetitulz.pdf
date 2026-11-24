// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Imaging;

/// <summary>
/// The 32-bit <c>BI_BITFIELDS</c> path of <see cref="BmpImageDecoder"/>, which assembles four
/// bytes into one 32-bit sample before masking channels out of it. The interesting inputs are the
/// ones where the <b>top</b> byte has its high bit set: that is the only operand that can reach
/// bit 31, and a byte-assembly expression mixing a signed and an unsigned operand there (CS0675)
/// sign-extends it. Every assertion names the exact RGB byte expected at a position — "it decoded
/// something" would pass with the mask extraction deleted.
/// </summary>
public class BmpBitfieldChannelTests
{
    // Channel masks that put one 8-bit channel in each of the three high bytes, so the red
    // channel is the one carried by bit 24..31 — the half a sign-extension would corrupt.
    private const uint RedMask = 0xFF000000;
    private const uint GreenMask = 0x00FF0000;
    private const uint BlueMask = 0x0000FF00;

    [Theory]
    // red, green, blue — the first four all set the high bit of the assembled sample.
    [InlineData(0xFF, 0x80, 0x01)]
    [InlineData(0xFF, 0xFF, 0xFF)]
    [InlineData(0x80, 0x00, 0x00)]
    [InlineData(0xC3, 0x2A, 0xF7)]
    // ...and these two do not, so a fix that only ever worked for the high half is visible too.
    [InlineData(0x7F, 0xFF, 0xFF)]
    [InlineData(0x00, 0x11, 0x22)]
    public void Bmp32BitBitfields_TopByteChannel_DecodesToTheExactChannelValue(int r, int g, int b)
    {
        byte[] bmp = MakeBmp32Bitfields(1, 1, new[] { new byte[] { (byte)r, (byte)g, (byte)b } });

        Assert.True(BmpImageDecoder.TryDecode(bmp, out byte[] rgb, out int w, out int h, out _));

        Assert.Equal(1, w);
        Assert.Equal(1, h);
        Assert.Equal((byte)r, rgb[0]);
        Assert.Equal((byte)g, rgb[1]);
        Assert.Equal((byte)b, rgb[2]);
    }

    [Fact]
    public void Bmp32BitBitfields_ByteOutsideEveryMask_DoesNotReachAnyChannel()
    {
        // Byte 0 of each sample is covered by no mask. Filling it with 0xFF must change nothing:
        // if it leaked into the assembled value the masks would still exclude it, but a shift that
        // is off by one — or an operand promoted to a wider signed type — would let it through.
        var pixels = new[] { new byte[] { 0xFF, 0x80, 0x01 }, new byte[] { 0x01, 0x80, 0xFF } };

        Assert.True(BmpImageDecoder.TryDecode(
            MakeBmp32Bitfields(2, 1, pixels, unusedByte: 0x00), out byte[] clean, out _, out _, out _));
        Assert.True(BmpImageDecoder.TryDecode(
            MakeBmp32Bitfields(2, 1, pixels, unusedByte: 0xFF), out byte[] noisy, out _, out _, out _));

        Assert.Equal(new byte[] { 0xFF, 0x80, 0x01, 0x01, 0x80, 0xFF }, clean);
        Assert.Equal(clean, noisy);
    }

    [Fact]
    public void Bmp32BitBitfields_TwoRows_KeepEachRowsOwnHighByte()
    {
        // Bottom-up storage plus a per-row high byte: a decoder that assembled the sample wrongly
        // in only one row, or flipped the rows, moves 0xFF between these two positions.
        var pixels = new[]
        {
            new byte[] { 0xFF, 0x00, 0x00 },   // top row
            new byte[] { 0x00, 0x00, 0xFF },   // bottom row
        };

        Assert.True(BmpImageDecoder.TryDecode(
            MakeBmp32Bitfields(1, 2, pixels), out byte[] rgb, out int w, out int h, out _));

        Assert.Equal(1, w);
        Assert.Equal(2, h);
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF }, rgb);
    }

    [Theory]
    // A red mask of all 32 bits: the maximum was (1 << 32) - 1, and C# reduces a shift by 32 to a
    // shift by 0, so it came out 0 and the scaling divided by zero out of a false-with-a-reason method.
    [InlineData(0xFF, 0xFF, 0xFF, 0xFF, 255)]
    [InlineData(0x80, 0x00, 0x00, 0x00, 128)]
    [InlineData(0x00, 0x00, 0x00, 0x00, 0)]
    public void Bmp32BitBitfields_AMaskOfAll32Bits_ScalesInsteadOfDividingByZero(int top, int b2, int b1, int b0, int red)
    {
        byte[] bmp = MakeBmp32Bitfields(1, 1, new[] { new byte[] { (byte)top, (byte)b2, (byte)b1 } }, unusedByte: (byte)b0,
            redMask: 0xFFFFFFFF, greenMask: 0, blueMask: 0);

        Assert.True(BmpImageDecoder.TryDecode(bmp, out byte[] rgb, out _, out _, out string reason), reason);
        Assert.Equal(new byte[] { (byte)red, 0, 0 }, rgb);
    }

    [Fact]
    public void Bmp32BitBitfields_ANonContiguousMask_IsClampedToAByteRatherThanWrapped()
    {
        // 0x00000F0F: the scale is read off the low run of four bits, so bit 8 alone is 256 of a
        // maximum of 15 — 4352 once scaled, whose low byte is 0. Clamped, it is the brightest value.
        byte[] bmp = MakeBmp32Bitfields(1, 1, new[] { new byte[] { 0x00, 0x00, 0x01 } }, unusedByte: 0x00,
            redMask: 0x00000F0F, greenMask: 0, blueMask: 0);

        Assert.True(BmpImageDecoder.TryDecode(bmp, out byte[] rgb, out _, out _, out _));
        Assert.Equal(255, rgb[0]);
    }

    // ================================================================ synthesis

    /// <summary>
    /// A BITMAPINFOHEADER BMP with <c>BI_BITFIELDS</c> 32-bpp samples, stored bottom-up, whose
    /// three channel masks are <see cref="RedMask"/> / <see cref="GreenMask"/> / <see cref="BlueMask"/>.
    /// <paramref name="pixelsTopDown"/> lists RGB triples in top-down order.
    /// </summary>
    private static byte[] MakeBmp32Bitfields(int w, int h, byte[][] pixelsTopDown, byte unusedByte = 0x00,
        uint redMask = RedMask, uint greenMask = GreenMask, uint blueMask = BlueMask)
    {
        const int MaskOffset = 54;             // straight after the 14-byte file header + 40-byte DIB
        const int PixelOffset = MaskOffset + 12;

        int rowBytes = w * 4;                  // 32 bpp is already a multiple of 4 bytes
        var bmp = new byte[PixelOffset + rowBytes * h];

        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        WriteLE32(bmp, 2, bmp.Length);
        WriteLE32(bmp, 10, PixelOffset);
        WriteLE32(bmp, 14, 40);                // DIB header size
        WriteLE32(bmp, 18, w);
        WriteLE32(bmp, 22, h);                 // positive: bottom-up
        WriteLE16(bmp, 26, 1);                 // planes
        WriteLE16(bmp, 28, 32);                // bpp
        WriteLE32(bmp, 30, 3);                 // BI_BITFIELDS
        WriteLE32(bmp, 34, rowBytes * h);      // image size

        WriteLE32(bmp, MaskOffset, unchecked((int)redMask));
        WriteLE32(bmp, MaskOffset + 4, unchecked((int)greenMask));
        WriteLE32(bmp, MaskOffset + 8, unchecked((int)blueMask));

        for (int y = 0; y < h; y++)
        {
            int srcRow = h - 1 - y;            // bottom-up on disk
            int o = PixelOffset + srcRow * rowBytes;
            for (int x = 0; x < w; x++)
            {
                byte[] c = pixelsTopDown[y * w + x];
                bmp[o + x * 4] = unusedByte;   // bits 0..7: no mask covers them
                bmp[o + x * 4 + 1] = c[2];     // bits 8..15  → BlueMask
                bmp[o + x * 4 + 2] = c[1];     // bits 16..23 → GreenMask
                bmp[o + x * 4 + 3] = c[0];     // bits 24..31 → RedMask
            }
        }

        return bmp;
    }

    private static void WriteLE32(byte[] b, int o, int v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
    }

    private static void WriteLE16(byte[] b, int o, int v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8);
    }
}
