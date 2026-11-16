// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// <c>FlateDecodeFilter.DecodeZlib</c>: one zlib stream, read with no retreat. Each refusal here is
/// a stream the lenient <c>DecodeEmbedded</c> answers for — asserted beside it, so the difference
/// between the two readings is what each test shows.
/// </summary>
public class StrictZlibDecodeTests
{
    private const long Plenty = 1 << 20;

    private static byte[] Samples(int length)
        => Enumerable.Range(0, length).Select(i => (byte)(i * 7 % 251)).ToArray();

    /// <summary>A zlib stream from the platform's own encoder — not this library's.</summary>
    private static byte[] Zlib(byte[] data)
    {
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data, 0, data.Length);
        return packed.ToArray();
    }

    /// <summary>
    /// Measuring where the deflate data ends must cost a few inflates of the stream, not one
    /// inflater call per byte of it. The second pass hands over the run before the last measuring
    /// chunk "at once", but <see cref="DeflateStream"/> asks for only its own buffer's worth per
    /// read; when that run was spent on the first read, every later byte of a multi-megabyte stream
    /// went over alone — about 430 times slower than one inflate on incompressible data, seconds
    /// for one image. The evidence is the count of reads the inflater made, not a clock: a few
    /// thousand for 16 MB, where a read a byte is sixteen million.
    /// </summary>
    [Fact]
    public void MeasuringTheEnd_CostsAFewInflates_NotOneCallPerByte()
    {
        var source = new byte[16 * 1024 * 1024];
        new Random(7).NextBytes(source);                 // incompressible: the stream is as long as its source
        byte[] stream = Zlib(source);

        long ceiling = stream.Length / 256;              // two passes of a few KB a read are far below it

        Assert.Equal(source, FlateDecodeFilter.DecodeZlib(stream, 0, stream.Length, source.Length, out int end));
        long strict = FlateDecodeFilter.LastMeasureReads;
        Assert.Equal(stream.Length, end);
        Assert.InRange(strict, 2, ceiling);              // potency: the measuring inflate ran, in two passes

        Assert.Equal(source, FlateDecodeFilter.DecodeEmbedded(stream, 0, stream.Length, source.Length, out _));
        Assert.InRange(FlateDecodeFilter.LastMeasureReads, 2, ceiling);
    }

    [Fact]
    public void AZlibStream_DecodesToItsSource_AndEndsPastItsTrailer()
    {
        byte[] source = Samples(5000);
        byte[] stream = Zlib(source);
        byte[] run = new byte[] { 1, 2, 3 }.Concat(stream).Concat(new byte[] { 0x78, 0x9C, 9, 9 }).ToArray();

        byte[] decoded = FlateDecodeFilter.DecodeZlib(run, 3, run.Length - 3, Plenty, out int end);

        Assert.Equal(source, decoded);
        Assert.Equal(3 + stream.Length, end);
    }

    /// <summary>
    /// <c>78 01</c> is a zlib header; the byte after it is a reserved deflate block type, so no zlib
    /// reader goes on. From byte 0 the same bytes are raw deflate — <c>78</c> opens a stored block —
    /// which is what the lenient reading falls back to and answers with.
    /// </summary>
    [Fact]
    public void RawDeflateBehindAZlibHeader_IsRefused_WhereTheLenientReadingAnswers()
    {
        byte[] source = Samples(1539);
        byte[] dressed = new byte[] { 0x78, 0x01, 0x06, 0xFE, 0xF9 }
            .Concat(source.Take(1537))
            .Concat(new byte[] { 0x01, 0x02, 0x00, 0xFD, 0xFF })
            .Concat(source.Skip(1537)).ToArray();

        // The lenient reading: the whole source, ending on the run's last byte.
        Assert.Equal(source, FlateDecodeFilter.DecodeEmbedded(dressed, 0, dressed.Length, Plenty, out int lenientEnd));
        Assert.Equal(dressed.Length, lenientEnd);

        var refusal = Assert.Throws<PdfException>(
            () => FlateDecodeFilter.DecodeZlib(dressed, 0, dressed.Length, Plenty, out _));
        Assert.Contains("deflate data after the zlib header is not valid", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATrailerThatDisagreesWithTheOutput_IsRefused_WhereTheLenientReadingReportsIt()
    {
        byte[] stream = Zlib(Samples(400));
        stream[^1] ^= 0x01;

        Assert.Equal(400, FlateDecodeFilter.DecodeEmbedded(stream, 0, stream.Length, Plenty, out _).Length);
        Assert.Contains("Adler-32", FlateDecodeFilter.LastTruncationReason, StringComparison.Ordinal);

        var refusal = Assert.Throws<PdfException>(
            () => FlateDecodeFilter.DecodeZlib(stream, 0, stream.Length, Plenty, out _));
        Assert.Contains("Adler-32 trailer", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStreamCutBeforeItsTrailer_IsRefused()
    {
        byte[] stream = Zlib(Samples(400));

        var refusal = Assert.Throws<PdfException>(
            () => FlateDecodeFilter.DecodeZlib(stream, 0, stream.Length - 2, Plenty, out _));
        Assert.Contains("truncated", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The header's own fields: a compression method other than deflate, check bits that do not
    /// make a multiple of 31, a preset dictionary. The control is the same stream with its header
    /// as the encoder wrote it.
    /// </summary>
    [Theory]
    [InlineData(0, 0x79, "does not begin with a zlib header")]    // CM 9
    [InlineData(1, 0x01, "does not begin with a zlib header")]    // FCHECK
    [InlineData(1, 0x20, "preset dictionary")]                    // FDICT, check bits fixed below
    public void AHeaderThatIsNotAPlainZlibHeader_IsRefused(int index, byte value, string saying)
    {
        byte[] good = Zlib(Samples(64));
        Assert.Equal(64, FlateDecodeFilter.DecodeZlib(good, 0, good.Length, Plenty, out _).Length);

        byte[] bad = (byte[])good.Clone();
        if (value == 0x20)
        {
            // Set FDICT and keep the header a multiple of 31, so the refusal is the dictionary's.
            int flg = 0x20;
            flg += 31 - ((bad[0] << 8 | flg) % 31);
            bad[1] = (byte)flg;
        }
        else if (index == 1) bad[1] ^= value;
        else bad[0] = value;

        var refusal = Assert.Throws<PdfException>(
            () => FlateDecodeFilter.DecodeZlib(bad, 0, bad.Length, Plenty, out _));
        Assert.Contains(saying, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputPastTheCeiling_IsRefused()
    {
        byte[] stream = Zlib(new byte[100_000]);

        Assert.Throws<PdfException>(() => FlateDecodeFilter.DecodeZlib(stream, 0, stream.Length, 99_999, out _));
        Assert.Equal(100_000, FlateDecodeFilter.DecodeZlib(stream, 0, stream.Length, 100_000, out _).Length);
    }
}
