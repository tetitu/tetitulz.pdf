// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// The LZW decoder's output (2026-10-19): the codes read through a bit accumulator, and each string
/// written where it stays — chunks that grow, joined at the end — or handed to a sink from a scratch.
/// </summary>
public sealed class LzwDecodeOutputTests
{
    /// <summary>
    /// A source of long runs (strings up to the table's 4 096 bytes, which cross the chunks' ends) and
    /// stretches of noise (single-byte codes, ClearTable cycles), 6 MB from a small stream, so the whole
    /// decode passes through several chunks.
    /// </summary>
    private static byte[] Source()
    {
        var s = new byte[6 << 20];
        var random = new Random(1019);
        for (int at = 0; at < s.Length;)
        {
            bool noise = random.Next(3) == 0;
            int n = Math.Min(s.Length - at, random.Next(1, noise ? 20_000 : 300_000));
            if (noise) random.NextBytes(s.AsSpan(at, n));
            else s.AsSpan(at, n).Fill((byte)random.Next(256));
            at += n;
        }
        return s;
    }

    /// <summary>
    /// The whole decode, the sink's and an embedded run's are the source, byte for byte, under both
    /// <c>/EarlyChange</c> values; the embedded run ends just past its EOD code with other bytes after it.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void EveryDoor_IsTheSource_AcrossTheChunks(int earlyChange)
    {
        byte[] source = Source();
        byte[] lzw = LzwEncodeFilter.Encode(source, earlyChange);
        Assert.True(lzw.Length * 3L < source.Length, "the stream is not small against its output, so the chunks are not reached");

        var parms = new Tetitulz.Pdf.Core.Objects.PdfDictionary { [Tetitulz.Pdf.Core.Objects.PdfName.EarlyChange] = new Tetitulz.Pdf.Core.Objects.PdfNumber(earlyChange) };
        Assert.True(source.AsSpan().SequenceEqual(new LzwDecodeFilter().Decode(lzw, parms)));
        Assert.Null(LzwDecodeFilter.LastTruncationReason);

        using var sunk = new MemoryStream();
        LzwDecodeFilter.DecodeTo(lzw, earlyChange, long.MaxValue, sunk.Write);
        Assert.True(source.AsSpan().SequenceEqual(sunk.ToArray()));

        var embedded = new byte[lzw.Length + 40];
        embedded.AsSpan().Fill(0xFF);
        Buffer.BlockCopy(lzw, 0, embedded, 20, lzw.Length);
        byte[] fromRun = LzwDecodeFilter.DecodeEmbedded(embedded, 20, lzw.Length + 20, earlyChange, long.MaxValue, out int end);
        Assert.True(source.AsSpan().SequenceEqual(fromRun));
        Assert.Equal(20 + lzw.Length, end);
    }

    /// <summary>
    /// The whole decode allocates its output and the chunks it was written in — about twice the output,
    /// where a MemoryStream's growth and copy cost 3.8 times (182 MB for 48 MB); the tables and the
    /// scratch are a few kilobytes.
    /// </summary>
    [Fact]
    public void TheWholeDecode_AllocatesAboutTwiceItsOutput()
    {
        byte[] source = Source();
        byte[] lzw = LzwEncodeFilter.Encode(source);
        new LzwDecodeFilter().Decode(lzw, null);   // warm

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] decoded = new LzwDecodeFilter().Decode(lzw, null);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(source.Length, decoded.Length);
        Assert.True(allocated < 2.6 * source.Length, $"the decode allocated {allocated:N0} bytes for {source.Length:N0}");
    }

    /// <summary>
    /// A small stream — a content stream's few hundred bytes — allocates little: a first chunk sized
    /// from the stream (with the room one string needs), never a full chunk; the tables are 36 KB.
    /// </summary>
    [Fact]
    public void ASmallStream_AllocatesLittle()
    {
        byte[] source = System.Text.Encoding.ASCII.GetBytes(string.Concat(System.Linq.Enumerable.Repeat("BT /F1 12 Tf 72 700 Td (Hello) Tj ET\n", 10)));
        byte[] lzw = LzwEncodeFilter.Encode(source);
        new LzwDecodeFilter().Decode(lzw, null);   // warm

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] decoded = new LzwDecodeFilter().Decode(lzw, null);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(source, decoded);
        Assert.True(allocated < 128 << 10, $"the decode allocated {allocated:N0} bytes for {source.Length} bytes of output");
    }

    /// <summary>A stream cut inside its codes returns the prefix it decoded, the cut reported — through the chunks too.</summary>
    [Fact]
    public void ACutStream_ReturnsItsPrefix_AcrossTheChunks()
    {
        byte[] source = Source();
        byte[] lzw = LzwEncodeFilter.Encode(source);
        byte[] cut = lzw.AsSpan(0, lzw.Length * 3 / 4).ToArray();
        byte[] prefix = new LzwDecodeFilter().Decode(cut, null);
        Assert.InRange(prefix.Length, 1, source.Length - 1);
        Assert.True(source.AsSpan(0, prefix.Length).SequenceEqual(prefix));
        Assert.Null(LzwDecodeFilter.LastTruncationReason);   // the bytes ran out: no corrupt code
        Assert.Equal(-1, EndOf(cut));
    }

    private static int EndOf(byte[] lzw)
    {
        LzwDecodeFilter.DecodeEmbedded(lzw, 0, lzw.Length, 1, long.MaxValue, out int end);
        return end;
    }
}
