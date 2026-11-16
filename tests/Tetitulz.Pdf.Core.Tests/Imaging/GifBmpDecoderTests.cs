// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Imaging;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Imaging;

/// <summary>
/// The GIF and BMP decoders, asserted on <b>decoded pixels</b> — every fixture is synthesised
/// with four known, distinct colours so a test can name the exact byte it expects at each
/// position. "It returned a buffer" would pass with the palette lookup deleted.
/// </summary>
public class GifBmpDecoderTests
{
    // Four corners, all distinct, none grey — so a channel swap (BGR vs RGB) is visible.
    private static readonly byte[][] Colours =
    {
        new byte[] { 255, 0, 0 },      // red
        new byte[] { 0, 255, 0 },      // green
        new byte[] { 0, 0, 255 },      // blue
        new byte[] { 255, 255, 0 },    // yellow
    };

    private static void AssertPixel(byte[] rgb, int width, int x, int y, byte[] expected)
    {
        int p = (y * width + x) * 3;
        Assert.Equal(expected[0], rgb[p]);
        Assert.Equal(expected[1], rgb[p + 1]);
        Assert.Equal(expected[2], rgb[p + 2]);
    }

    // ================================================================ GIF

    [Fact]
    public void Gif_2x2_DecodesEveryPixel()
    {
        byte[] gif = MakeGif(2, 2, new[] { 0, 1, 2, 3 });

        Assert.True(GifImageDecoder.TryDecode(gif, out byte[] rgb, out byte[] alpha,
            out int w, out int h, out bool more));

        Assert.Equal(2, w);
        Assert.Equal(2, h);
        Assert.Null(alpha);
        Assert.False(more);
        AssertPixel(rgb, 2, 0, 0, Colours[0]);
        AssertPixel(rgb, 2, 1, 0, Colours[1]);
        AssertPixel(rgb, 2, 0, 1, Colours[2]);
        AssertPixel(rgb, 2, 1, 1, Colours[3]);
    }

    /// <summary>
    /// The LZW here must be the GIF variant. A PDF <c>LZWDecode</c> — MSB-first, fixed clear
    /// code 256 — produces garbage from these same bytes, so decoding the right pixels is the
    /// assertion that the two were not conflated.
    /// </summary>
    [Fact]
    public void Gif_LargerImage_DecodesThroughDictionaryGrowth()
    {
        // 64×8 of a repeating pattern: long enough that the LZW dictionary fills past its initial
        // code size, so the decoder must walk prefix chains and widen its codes.
        int w = 64, h = 8;
        var indices = new int[w * h];
        for (int i = 0; i < indices.Length; i++) indices[i] = i % 4;

        byte[] compressed = LzwEncode(indices, 2);
        // The fixture must actually compress, or the dictionary path is not being exercised at
        // all and this test would pass against a decoder that only understands literal codes.
        // Two symbols per byte is the floor for 3-bit codes; real compression beats it easily.
        Assert.True(compressed.Length < indices.Length / 2,
            $"the fixture did not compress ({compressed.Length} bytes for {indices.Length} pixels), " +
            "so it does not exercise dictionary growth");

        Assert.True(GifImageDecoder.TryDecode(MakeGif(w, h, indices), out byte[] rgb, out _,
            out int dw, out int dh, out _));

        Assert.Equal(w, dw);
        Assert.Equal(h, dh);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                AssertPixel(rgb, w, x, y, Colours[(y * w + x) % 4]);
    }

    /// <summary>
    /// The KwKwK case — a code referring to a sequence the decoder is only now defining. It
    /// arises whenever a symbol run repeats immediately, which a solid block of one colour does
    /// on its very first dictionary entry.
    /// </summary>
    [Fact]
    public void Gif_RepeatedRun_ExercisesTheSelfReferentialCode()
    {
        int w = 32, h = 4;
        var indices = new int[w * h];       // all index 0 — the classic KwKwK generator

        Assert.True(GifImageDecoder.TryDecode(MakeGif(w, h, indices), out byte[] rgb, out _,
            out int dw, out int dh, out _));

        Assert.Equal(w, dw);
        Assert.Equal(h, dh);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                AssertPixel(rgb, w, x, y, Colours[0]);
    }

    [Fact]
    public void Gif_TransparentIndex_ProducesAnAlphaChannel()
    {
        byte[] gif = MakeGif(2, 2, new[] { 0, 1, 2, 3 }, transparentIndex: 1);

        Assert.True(GifImageDecoder.TryDecode(gif, out _, out byte[] alpha, out _, out _, out _));

        Assert.NotNull(alpha);
        Assert.Equal(255, alpha[0]);
        Assert.Equal(0, alpha[1]);     // the transparent index
        Assert.Equal(255, alpha[2]);
    }

    [Fact]
    public void Gif_Interlaced_DecodesToTheSamePixelsAsProgressive()
    {
        // The strongest available form: the same image stored both ways must decode identically.
        // It cannot pass with the de-interlacing stubbed out.
        int w = 8, h = 8;
        var indices = new int[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                indices[y * w + x] = (x + y) % 4;

        byte[] plain = MakeGif(w, h, indices);
        byte[] interlaced = MakeGif(w, h, Interleave(indices, w, h), interlaced: true);

        Assert.True(GifImageDecoder.TryDecode(plain, out byte[] a, out _, out _, out _, out _));
        Assert.True(GifImageDecoder.TryDecode(interlaced, out byte[] b, out _, out _, out _, out _));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Gif_Animation_ReportsMoreFrames()
    {
        // Only the first frame is drawn; the caller is told rather than left to wonder why the
        // animation is a still.
        byte[] two = MakeGif(2, 2, new[] { 0, 1, 2, 3 }, extraFrame: true);

        Assert.True(GifImageDecoder.TryDecode(two, out _, out _, out _, out _, out bool more));
        Assert.True(more);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(12)]
    public void Gif_TruncatedInput_IsRefusedWithoutThrowing(int length)
    {
        byte[] full = MakeGif(2, 2, new[] { 0, 1, 2, 3 });
        var truncated = new byte[Math.Min(length, full.Length)];
        Array.Copy(full, truncated, truncated.Length);

        var ex = Record.Exception(() => GifImageDecoder.TryDecode(truncated, out _, out _, out _, out _, out _));
        Assert.Null(ex);
    }

    [Fact]
    public void Gif_MalformedLzwStream_IsRefusedWithoutHanging()
    {
        // Adversarial: random bytes where the LZW data should be. A dictionary walk that trusted
        // its prefix chain would loop forever here.
        byte[] gif = MakeGif(4, 4, new[] { 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3 });
        for (int i = gif.Length - 8; i < gif.Length - 1; i++) gif[i] = 0xAB;

        var ex = Record.Exception(() => GifImageDecoder.TryDecode(gif, out _, out _, out _, out _, out _));
        Assert.Null(ex);
    }

    // ================================================================ BMP

    [Fact]
    public void Bmp_24Bit_DecodesEveryPixelInTopDownOrder()
    {
        byte[] bmp = MakeBmp24(2, 2, new[] { Colours[0], Colours[1], Colours[2], Colours[3] });

        Assert.True(BmpImageDecoder.TryDecode(bmp, out byte[] rgb, out int w, out int h, out _));

        Assert.Equal(2, w);
        Assert.Equal(2, h);
        // The fixture lists pixels top-down; BMP stores them bottom-up, and the decoder must
        // return them top-down for a PDF image XObject. A missing flip swaps these two rows.
        AssertPixel(rgb, 2, 0, 0, Colours[0]);
        AssertPixel(rgb, 2, 1, 0, Colours[1]);
        AssertPixel(rgb, 2, 0, 1, Colours[2]);
        AssertPixel(rgb, 2, 1, 1, Colours[3]);
    }

    [Fact]
    public void Bmp_TopDown_DecodesToTheSamePixelsAsBottomUp()
    {
        var pixels = new[] { Colours[0], Colours[1], Colours[2], Colours[3] };

        Assert.True(BmpImageDecoder.TryDecode(MakeBmp24(2, 2, pixels), out byte[] up, out _, out _, out _));
        Assert.True(BmpImageDecoder.TryDecode(MakeBmp24(2, 2, pixels, topDown: true), out byte[] down, out _, out _, out _));

        Assert.Equal(up, down);
    }

    [Fact]
    public void Bmp_RowPaddingToFourBytes_IsHonoured()
    {
        // A 3-pixel 24-bit row is 9 bytes and pads to 12. Ignoring the padding shears the image.
        var pixels = new List<byte[]>();
        for (int i = 0; i < 9; i++) pixels.Add(Colours[i % 4]);

        Assert.True(BmpImageDecoder.TryDecode(MakeBmp24(3, 3, pixels.ToArray()),
            out byte[] rgb, out int w, out _, out _));

        for (int i = 0; i < 9; i++)
            AssertPixel(rgb, w, i % 3, i / 3, Colours[i % 4]);
    }

    [Fact]
    public void Bmp_8BitPalette_ExpandsThroughTheColourTable()
    {
        byte[] bmp = MakeBmp8(2, 2, new byte[] { 0, 1, 2, 3 });

        Assert.True(BmpImageDecoder.TryDecode(bmp, out byte[] rgb, out int w, out _, out _));

        AssertPixel(rgb, w, 0, 0, Colours[0]);
        AssertPixel(rgb, w, 1, 0, Colours[1]);
        AssertPixel(rgb, w, 0, 1, Colours[2]);
        AssertPixel(rgb, w, 1, 1, Colours[3]);
    }

    [Fact]
    public void Bmp_RleCompressed_IsDeclinedWithAReason()
    {
        byte[] bmp = MakeBmp24(2, 2, new[] { Colours[0], Colours[1], Colours[2], Colours[3] });
        bmp[30] = 1;   // BI_RLE8

        Assert.False(BmpImageDecoder.TryDecode(bmp, out _, out _, out _, out string reason));
        Assert.Contains("RLE", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bmp_TruncatedPixelData_IsDeclinedWithAReason()
    {
        byte[] bmp = MakeBmp24(4, 4, Repeat(Colours[0], 16));
        Array.Resize(ref bmp, bmp.Length - 20);

        Assert.False(BmpImageDecoder.TryDecode(bmp, out _, out _, out _, out string reason));
        Assert.Contains("truncated", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(53)]
    public void Bmp_TruncatedHeader_IsRefusedWithoutThrowing(int length)
    {
        var ex = Record.Exception(() => BmpImageDecoder.TryDecode(new byte[length], out _, out _, out _, out _));
        Assert.Null(ex);
    }

    // ================================================================ synthesis

    private static byte[][] Repeat(byte[] colour, int n)
    {
        var a = new byte[n][];
        for (int i = 0; i < n; i++) a[i] = colour;
        return a;
    }

    /// <summary>Reorders rows into GIF interlaced storage order (the inverse of the decoder's pass walk).</summary>
    private static int[] Interleave(int[] indices, int w, int h)
    {
        var rows = new List<int>();
        for (int y = 0; y < h; y += 8) rows.Add(y);
        for (int y = 4; y < h; y += 8) rows.Add(y);
        for (int y = 2; y < h; y += 4) rows.Add(y);
        for (int y = 1; y < h; y += 2) rows.Add(y);

        var result = new int[indices.Length];
        for (int i = 0; i < rows.Count; i++)
            Array.Copy(indices, rows[i] * w, result, i * w, w);
        return result;
    }

    internal static byte[] MakeGif(int w, int h, int[] indices, int transparentIndex = -1,
        bool interlaced = false, bool extraFrame = false)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("GIF89a"), 0, 6);
        Write16(ms, w); Write16(ms, h);
        ms.WriteByte(0x80 | 0x01);      // global colour table, 4 entries (2^(1+1))
        ms.WriteByte(0);                // background index
        ms.WriteByte(0);                // aspect ratio
        foreach (var c in Colours) ms.Write(c, 0, 3);

        if (transparentIndex >= 0)
        {
            ms.Write(new byte[] { 0x21, 0xF9, 4, 0x01, 0, 0, (byte)transparentIndex, 0 }, 0, 8);
        }

        WriteGifFrame(ms, w, h, indices, interlaced);
        if (extraFrame) WriteGifFrame(ms, w, h, indices, interlaced);

        ms.WriteByte(0x3B);             // trailer
        return ms.ToArray();
    }

    private static void WriteGifFrame(Stream ms, int w, int h, int[] indices, bool interlaced)
    {
        ms.WriteByte(0x2C);
        Write16(ms, 0); Write16(ms, 0);
        Write16(ms, w); Write16(ms, h);
        ms.WriteByte((byte)(interlaced ? 0x40 : 0x00));

        const int minCodeSize = 2;
        ms.WriteByte(minCodeSize);
        byte[] lzw = LzwEncode(indices, minCodeSize);
        for (int o = 0; o < lzw.Length; o += 255)
        {
            int n = Math.Min(255, lzw.Length - o);
            ms.WriteByte((byte)n);
            ms.Write(lzw, o, n);
        }
        ms.WriteByte(0);                // block terminator
    }

    /// <summary>
    /// A real GIF LZW encoder — it builds a dictionary, emits multi-symbol codes and grows the
    /// code size as the dictionary fills.
    ///
    /// <para>The obvious shortcut is to emit a clear code before every symbol: that is legal GIF,
    /// it is five lines, and it produces a stream the decoder can read without ever creating a
    /// dictionary entry or widening a code. It also leaves the decoder's prefix-chain walk, its
    /// code-size growth and its KwKwK case <b>completely untested</b> while a test named for
    /// dictionary growth passes. Doing it properly is what makes those assertions mean
    /// something.</para>
    ///
    /// <para>Round-tripping against our own decoder is self-consistent by construction, so
    /// <see cref="Gif_LargerImage_DecodesThroughDictionaryGrowth"/> additionally asserts that the
    /// encoded stream is genuinely compressed — which is only possible if multi-symbol dictionary
    /// codes were emitted, and therefore only decodable if the prefix chains were walked.</para>
    /// </summary>
    private static byte[] LzwEncode(int[] indices, int minCodeSize)
    {
        int clearCode = 1 << minCodeSize;
        int endCode = clearCode + 1;
        int next = endCode + 1;
        int codeSize = minCodeSize + 1;

        var dict = new Dictionary<(int prefix, int symbol), int>();
        var bits = new List<bool>();

        void Emit(int code)
        {
            for (int i = 0; i < codeSize; i++) bits.Add(((code >> i) & 1) != 0);
        }

        Emit(clearCode);
        if (indices.Length == 0) { Emit(endCode); return Pack(bits); }

        int prefix = indices[0];
        for (int i = 1; i < indices.Length; i++)
        {
            int symbol = indices[i];
            if (dict.TryGetValue((prefix, symbol), out int combined))
            {
                prefix = combined;
                continue;
            }

            Emit(prefix);
            if (next < 4096)
            {
                dict[(prefix, symbol)] = next++;
                // Widen ONE STEP LATER than the decoder does, because the decoder builds each
                // dictionary entry one code behind the encoder: after processing code k the
                // encoder has defined k entries and the decoder k-1, so their `next` values
                // differ by exactly one. The decoder widens when its next reaches 1<<codeSize;
                // the encoder must therefore widen when its own next reaches 1<<codeSize plus 1.
                // Getting this wrong desynchronises the bit width mid-stream, which looks exactly
                // like a decoder bug and is not one.
                if (next == (1 << codeSize) + 1 && codeSize < 12) codeSize++;
            }
            else
            {
                Emit(clearCode);
                dict.Clear();
                next = endCode + 1;
                codeSize = minCodeSize + 1;
            }
            prefix = symbol;
        }

        Emit(prefix);
        Emit(endCode);
        return Pack(bits);
    }

    private static byte[] Pack(List<bool> bits)
    {
        var bytes = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++)
            if (bits[i]) bytes[i >> 3] |= (byte)(1 << (i & 7));
        return bytes;
    }

    internal static byte[] MakeBmp24(int w, int h, byte[][] pixelsTopDown, bool topDown = false)
    {
        int rowBytes = ((w * 24 + 31) / 32) * 4;
        int pixelSize = rowBytes * h;
        var bmp = new byte[54 + pixelSize];

        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        WriteLE32(bmp, 2, bmp.Length);
        WriteLE32(bmp, 10, 54);         // pixel data offset
        WriteLE32(bmp, 14, 40);         // DIB header size
        WriteLE32(bmp, 18, w);
        WriteLE32(bmp, 22, topDown ? -h : h);
        WriteLE16(bmp, 26, 1);          // planes
        WriteLE16(bmp, 28, 24);         // bpp
        WriteLE32(bmp, 30, 0);          // BI_RGB

        for (int y = 0; y < h; y++)
        {
            int storedRow = topDown ? y : h - 1 - y;
            int o = 54 + storedRow * rowBytes;
            for (int x = 0; x < w; x++)
            {
                byte[] c = pixelsTopDown[y * w + x];
                bmp[o + x * 3] = c[2];      // BGR order on disk
                bmp[o + x * 3 + 1] = c[1];
                bmp[o + x * 3 + 2] = c[0];
            }
        }
        return bmp;
    }

    internal static byte[] MakeBmp8(int w, int h, byte[] indicesTopDown)
    {
        int rowBytes = ((w * 8 + 31) / 32) * 4;
        int paletteBytes = Colours.Length * 4;
        int offset = 54 + paletteBytes;
        var bmp = new byte[offset + rowBytes * h];

        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        WriteLE32(bmp, 2, bmp.Length);
        WriteLE32(bmp, 10, offset);
        WriteLE32(bmp, 14, 40);
        WriteLE32(bmp, 18, w);
        WriteLE32(bmp, 22, h);
        WriteLE16(bmp, 26, 1);
        WriteLE16(bmp, 28, 8);
        WriteLE32(bmp, 30, 0);
        WriteLE32(bmp, 46, Colours.Length);

        for (int i = 0; i < Colours.Length; i++)
        {
            bmp[54 + i * 4] = Colours[i][2];      // BGRA
            bmp[54 + i * 4 + 1] = Colours[i][1];
            bmp[54 + i * 4 + 2] = Colours[i][0];
        }

        for (int y = 0; y < h; y++)
        {
            int o = offset + (h - 1 - y) * rowBytes;
            for (int x = 0; x < w; x++) bmp[o + x] = indicesTopDown[y * w + x];
        }
        return bmp;
    }

    private static void Write16(Stream s, int v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); }

    private static void WriteLE32(byte[] b, int o, int v)
    { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }

    private static void WriteLE16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
}
