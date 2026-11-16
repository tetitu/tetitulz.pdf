// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Where a stream embedded in a longer byte run ends — <c>DecodeEmbedded</c> on the RunLength, LZW
/// and Flate filters, <c>FindEnd</c> on RunLength and DCT. The PostScript plugin's
/// <c>currentfile /FlateDecode filter</c> image source is the consumer: the byte after the stream is
/// the program's next byte, so the end must be exact, never "about here". Every stream here is
/// followed by bytes that look like another stream's framing (a zlib header, a JPEG EOI, a RunLength
/// EOD) and by PostScript text, and the end is asserted against the encoder's own length.
/// </summary>
public class EmbeddedStreamEndTests
{
    private const long Plenty = 1 << 20;

    /// <summary>What follows a stream in a program: text, then bytes that frame other streams.</summary>
    private static readonly byte[] Follows =
        Encoding.ASCII.GetBytes(" 0.25 setgray newpath 0 0 moveto 9 0 lineto 9 9 lineto fill\n")
                .Concat(new byte[] { 0x78, 0x9C, 0xFF, 0xD9, 0x80, 0x00 }).ToArray();

    private static byte[] Pattern(int length)
        => Enumerable.Range(0, length).Select(i => (byte)(i * 7 % 251)).ToArray();

    private static byte[] Random(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>The stream at <paramref name="before"/> bytes in, with <see cref="Follows"/> after it.</summary>
    private static byte[] Embed(byte[] stream, int before, out int start)
    {
        start = before;
        return Enumerable.Repeat((byte)0x55, before).Concat(stream).Concat(Follows).ToArray();
    }

    // ================================================================ RunLength

    [Fact]
    public void RunLength_DecodesToTheSource_AndEndsJustPastItsEod()
    {
        byte[] source = Pattern(300).Concat(Enumerable.Repeat((byte)9, 200)).ToArray();   // literal runs, then a repeat
        byte[] encoded = RunLengthEncodeFilter.Encode(source);
        byte[] data = Embed(encoded, 3, out int start);

        byte[] decoded = RunLengthDecodeFilter.DecodeEmbedded(data, start, data.Length - start, Plenty, out int end);

        Assert.Equal(source, decoded);
        Assert.Equal(start + encoded.Length, end);
        Assert.Equal(0x80, data[end - 1]);   // the last byte taken is the EOD

        // Two methods, one question.
        Assert.Equal(end, RunLengthDecodeFilter.FindEnd(data, start, data.Length - start));
    }

    [Fact]
    public void RunLength_WithoutItsEod_ReportsNoEnd_ButReturnsTheRuns()
    {
        byte[] source = Pattern(100);
        byte[] encoded = RunLengthEncodeFilter.Encode(source);
        byte[] cut = encoded.Take(encoded.Length - 1).ToArray();   // the EOD byte alone

        byte[] decoded = RunLengthDecodeFilter.DecodeEmbedded(cut, 0, cut.Length, Plenty, out int end);

        Assert.Equal(source, decoded);
        Assert.Equal(-1, end);
        Assert.Equal(-1, RunLengthDecodeFilter.FindEnd(cut, 0, cut.Length));
    }

    [Fact]
    public void RunLength_TheCountBoundsTheWalk()
    {
        byte[] encoded = RunLengthEncodeFilter.Encode(Pattern(100));
        byte[] data = Embed(encoded, 2, out int start);

        Assert.Equal(-1, RunLengthDecodeFilter.FindEnd(data, start, encoded.Length - 1));   // the EOD is one past the count
        Assert.Equal(start + encoded.Length, RunLengthDecodeFilter.FindEnd(data, start, encoded.Length));
    }

    // ================================================================ LZW

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void Lzw_DecodesToTheSource_AndEndsJustPastItsEodCode(int earlyChange)
    {
        byte[] source = Pattern(5000);   // long enough for the code width to grow past 9 bits
        byte[] encoded = LzwEncodeFilter.Encode(source, earlyChange);
        byte[] data = Embed(encoded, 5, out int start);

        byte[] decoded = LzwDecodeFilter.DecodeEmbedded(data, start, data.Length - start, earlyChange, Plenty, out int end);

        Assert.Equal(source, decoded);
        Assert.Equal(start + encoded.Length, end);
        Assert.Null(LzwDecodeFilter.LastTruncationReason);
    }

    [Fact]
    public void Lzw_CutBeforeItsEodCode_ReportsNoEnd_AndReturnsAPrefix()
    {
        byte[] source = Pattern(1000);
        byte[] encoded = LzwEncodeFilter.Encode(source);
        byte[] cut = encoded.Take(encoded.Length - 3).ToArray();

        byte[] decoded = LzwDecodeFilter.DecodeEmbedded(cut, 0, cut.Length, 1, Plenty, out int end);

        Assert.Equal(-1, end);
        Assert.True(decoded.Length < source.Length, "a cut stream decodes to less than its source");
        Assert.Equal(source.Take(decoded.Length), decoded);
    }

    /// <summary>An empty stream is ClearTable then EOD — eighteen bits, so the EOD's last bit is in the third byte.</summary>
    [Fact]
    public void Lzw_AnEmptyStream_EndsAfterItsTwoCodes()
    {
        byte[] encoded = LzwEncodeFilter.Encode(Array.Empty<byte>());
        Assert.Equal(3, encoded.Length);   // fixture: 9 + 9 bits, padded
        byte[] data = Embed(encoded, 2, out int start);

        byte[] decoded = LzwDecodeFilter.DecodeEmbedded(data, start, data.Length - start, 1, Plenty, out int end);

        Assert.Empty(decoded);
        Assert.Equal(start + 3, end);
    }

    [Fact]
    public void Lzw_TheCountBoundsTheStream()
    {
        byte[] encoded = LzwEncodeFilter.Encode(Pattern(100));
        byte[] data = Embed(encoded, 2, out int start);

        LzwDecodeFilter.DecodeEmbedded(data, start, encoded.Length - 1, 1, Plenty, out int cut);
        Assert.Equal(-1, cut);

        LzwDecodeFilter.DecodeEmbedded(data, start, encoded.Length, 1, Plenty, out int whole);
        Assert.Equal(start + encoded.Length, whole);
    }

    [Fact]
    public void Lzw_ACorruptCode_ReportsNoEnd_AndWhy()
    {
        // 9-bit codes: ClearTable (256), then 300 — which names nothing right after a clear.
        // 1 0000 0000 | 1 0010 1100 → 10000000 01001011 00000000
        byte[] data = { 0x80, 0x4B, 0x00, 0x80, 0x00 };

        byte[] decoded = LzwDecodeFilter.DecodeEmbedded(data, 0, data.Length, 1, Plenty, out int end);

        Assert.Empty(decoded);
        Assert.Equal(-1, end);
        Assert.NotNull(LzwDecodeFilter.LastTruncationReason);
        Assert.Contains("follows a ClearTable", LzwDecodeFilter.LastTruncationReason, StringComparison.Ordinal);
    }

    // ================================================================ Flate

    [Fact]
    public void Flate_DecodesToTheSource_AndEndsJustPastItsTrailer()
    {
        byte[] source = Pattern(2000);
        byte[] encoded = FlateEncodeFilter.Encode(source);
        byte[] data = Embed(encoded, 7, out int start);

        byte[] decoded = FlateDecodeFilter.DecodeEmbedded(data, start, data.Length - start, Plenty, out int end);

        Assert.Equal(source, decoded);
        Assert.Equal(start + encoded.Length, end);
        Assert.Null(FlateDecodeFilter.LastTruncationReason);
    }

    /// <summary>
    /// The first pass hands the inflater 4 096 bytes at a time; a stream longer than that puts the
    /// end in a later chunk, so the second pass has a run to hand over whole before it goes byte by
    /// byte. Random bytes do not compress, so the stream is longer than its source.
    /// </summary>
    [Fact]
    public void Flate_AStreamLongerThanTheMeasuringChunk_EndsExactly()
    {
        byte[] source = Random(20_000, seed: 1);
        byte[] encoded = FlateEncodeFilter.Encode(source);
        Assert.True(encoded.Length > 4096, "fixture: the stream must span several measuring chunks");
        byte[] data = Embed(encoded, 1, out int start);

        byte[] decoded = FlateDecodeFilter.DecodeEmbedded(data, start, data.Length - start, Plenty, out int end);

        Assert.Equal(source, decoded);
        Assert.Equal(start + encoded.Length, end);
    }

    [Fact]
    public void Flate_EndingExactlyWhereTheRunEnds_IsTerminated()
    {
        byte[] source = Pattern(2000);
        byte[] encoded = FlateEncodeFilter.Encode(source);

        byte[] decoded = FlateDecodeFilter.DecodeEmbedded(encoded, 0, encoded.Length, Plenty, out int end);

        Assert.Equal(source, decoded);
        Assert.Equal(encoded.Length, end);
        Assert.Null(FlateDecodeFilter.LastTruncationReason);
    }

    /// <summary>
    /// A raw deflate stream — no zlib header, so no trailer — ends at its final block, whether or
    /// not bytes follow. Ending exactly at the run's end is the case the inflater alone cannot tell
    /// from a truncated stream, and is told apart by offering it one byte more.
    /// </summary>
    [Fact]
    public void Flate_RawDeflate_EndsAtItsFinalBlock_WithAndWithoutBytesAfter()
    {
        byte[] source = Pattern(2000);
        byte[] encoded = FlateEncodeFilter.Encode(source);
        byte[] raw = encoded.Skip(2).Take(encoded.Length - 6).ToArray();
        Assert.NotEqual(0x08, raw[0] & 0x0F);   // fixture: must not look like a zlib header

        byte[] followed = Embed(raw, 0, out _);
        Assert.Equal(source, FlateDecodeFilter.DecodeEmbedded(followed, 0, followed.Length, Plenty, out int endFollowed));
        Assert.Equal(raw.Length, endFollowed);

        Assert.Equal(source, FlateDecodeFilter.DecodeEmbedded(raw, 0, raw.Length, Plenty, out int endAlone));
        Assert.Equal(raw.Length, endAlone);
        Assert.Null(FlateDecodeFilter.LastTruncationReason);
    }

    [Fact]
    public void Flate_CutShort_ReportsNoEnd_AndReturnsAPrefix()
    {
        byte[] source = Pattern(2000);
        byte[] encoded = FlateEncodeFilter.Encode(source);
        byte[] cut = encoded.Take(encoded.Length - 8).ToArray();   // the trailer and the final block's tail

        byte[] decoded = FlateDecodeFilter.DecodeEmbedded(cut, 0, cut.Length, Plenty, out int end);

        Assert.Equal(-1, end);
        Assert.True(decoded.Length <= source.Length);
        Assert.Equal(source.Take(decoded.Length), decoded);
        Assert.NotNull(FlateDecodeFilter.LastTruncationReason);
        Assert.Contains("final deflate block never arrived", FlateDecodeFilter.LastTruncationReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Flate_ATrailerThatDoesNotFit_ReportsNoEnd()
    {
        byte[] source = Pattern(2000);
        byte[] encoded = FlateEncodeFilter.Encode(source);
        byte[] untrailed = encoded.Take(encoded.Length - 4).Concat(new byte[] { 0, 0 }).ToArray();   // two of the four

        byte[] decoded = FlateDecodeFilter.DecodeEmbedded(untrailed, 0, untrailed.Length, Plenty, out int end);

        Assert.Equal(source, decoded);
        Assert.Equal(-1, end);
        Assert.NotNull(FlateDecodeFilter.LastTruncationReason);
        Assert.Contains("trailer does not fit", FlateDecodeFilter.LastTruncationReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Flate_ATrailerThatDisagrees_IsReported_AndTheEndIsStillTheFramings()
    {
        byte[] source = Pattern(2000);
        byte[] encoded = FlateEncodeFilter.Encode(source);
        encoded[encoded.Length - 1] ^= 0xFF;
        byte[] data = Embed(encoded, 4, out int start);

        byte[] decoded = FlateDecodeFilter.DecodeEmbedded(data, start, data.Length - start, Plenty, out int end);

        Assert.Equal(source, decoded);
        Assert.Equal(start + encoded.Length, end);
        Assert.NotNull(FlateDecodeFilter.LastTruncationReason);
        Assert.Contains("disagrees", FlateDecodeFilter.LastTruncationReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Flate_TheCountBoundsTheStream()
    {
        byte[] encoded = FlateEncodeFilter.Encode(Pattern(2000));
        byte[] data = Embed(encoded, 2, out int start);

        FlateDecodeFilter.DecodeEmbedded(data, start, encoded.Length - 1, Plenty, out int cut);
        Assert.Equal(-1, cut);

        FlateDecodeFilter.DecodeEmbedded(data, start, encoded.Length, Plenty, out int whole);
        Assert.Equal(start + encoded.Length, whole);
    }

    [Fact]
    public void Flate_TheCeilingRefuses()
    {
        byte[] encoded = FlateEncodeFilter.Encode(Pattern(2000));

        var ex = Assert.Throws<PdfException>(
            () => FlateDecodeFilter.DecodeEmbedded(encoded, 0, encoded.Length, 100, out _));
        Assert.Contains("decode limit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Flate_OneByte_HoldsNoStream()
    {
        byte[] data = { 0x78, 0x9C, 0x01 };

        byte[] decoded = FlateDecodeFilter.DecodeEmbedded(data, 1, 1, Plenty, out int end);

        Assert.Empty(decoded);
        Assert.Equal(-1, end);
        Assert.Contains("holds no deflate data", FlateDecodeFilter.LastTruncationReason, StringComparison.Ordinal);
    }

    // ================================================================ DCT

    /// <summary>
    /// A JPEG codestream: SOI; an APP1 whose payload holds a thumbnail's <c>FF D9</c>; a scan header;
    /// entropy-coded data with a stuffed <c>FF 00</c> and a restart marker; <c>FF</c> fill; EOI.
    /// A search for <c>FF D9</c> ends it inside the APP1; a walk that reads the stuffed byte or the
    /// restart marker as a marker loses sync.
    /// </summary>
    private static byte[] Jpeg() => new byte[]
    {
        0xFF, 0xD8,                                                     // SOI
        0xFF, 0xE1, 0x00, 0x0C,                                         // APP1, segment length 12
        0x45, 0x78, 0x69, 0x66, 0x00, 0x00, 0xFF, 0xD9, 0x00, 0x00,     // "Exif", a thumbnail's EOI
        0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00,     // SOS, segment length 8
        0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56, 0x78,                 // entropy data: FF stuffed, RST0
        0xFF, 0xFF, 0xD9,                                               // fill, then the real EOI
    };

    [Fact]
    public void Dct_EndsJustPastTheEoi_NotAtAThumbnails_NorAtOneAfterIt()
    {
        byte[] jpeg = Jpeg();
        byte[] data = Embed(jpeg, 2, out int start);
        Assert.Contains((byte)0xD9, Follows);   // fixture: a decoy EOI follows the codestream too

        Assert.Equal(start + jpeg.Length, DctDecodeFilter.FindEnd(data, start, data.Length - start));
        Assert.Equal(jpeg.Length, DctDecodeFilter.FindEnd(jpeg, 0, jpeg.Length));
    }

    [Fact]
    public void Dct_Truncated_ReportsNoEnd()
    {
        byte[] jpeg = Jpeg();
        byte[] cut = jpeg.Take(jpeg.Length - 1).ToArray();

        Assert.Equal(-1, DctDecodeFilter.FindEnd(cut, 0, cut.Length));
    }

    [Fact]
    public void Dct_TheCountBoundsTheWalk()
    {
        byte[] jpeg = Jpeg();
        byte[] data = Embed(jpeg, 3, out int start);

        Assert.Equal(-1, DctDecodeFilter.FindEnd(data, start, jpeg.Length - 1));
        Assert.Equal(start + jpeg.Length, DctDecodeFilter.FindEnd(data, start, jpeg.Length));
    }

    // ================================================================ arguments

    [Fact]
    public void TheRun_IsRefusedBeforeAByteIsRead()
    {
        byte[] data = new byte[8];

        Assert.Throws<ArgumentNullException>(() => RunLengthDecodeFilter.FindEnd(null, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RunLengthDecodeFilter.DecodeEmbedded(data, 9, 0, Plenty, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => LzwDecodeFilter.DecodeEmbedded(data, 4, 5, 1, Plenty, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => FlateDecodeFilter.DecodeEmbedded(data, -1, 2, Plenty, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => DctDecodeFilter.FindEnd(data, 0, -1));
    }
}
