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
/// <see cref="FlateDecodeFilter.LastTruncationReason"/> (GAPS §7, 2026-10-14): a zlib stream whose
/// Adler-32 trailer is missing, or disagrees with the bytes produced, is reported as truncated or
/// corrupt — where <c>DeflateStream</c> returned the prefix it had as if it were the whole — and a
/// stream decoded whole, trailing bytes after its trailer included, is not.
/// </summary>
/// <remarks>
/// <para><b>The cut is at a block boundary on purpose.</b> Stored blocks (<c>BTYPE 00</c>) are
/// written by hand here, so the first block ends at a known byte and the input can be cut exactly
/// there: the inflater then has a complete block and no next one, the case that returns silently.
/// A cut inside a block is the inflater's own refusal and is not what this slot exists for.</para>
/// <para>Every assertion reads the slot <b>and</b> the output: the report without the prefix, or the
/// prefix without the report, is each half of the defect.</para>
/// </remarks>
public class FlateTruncationTests
{
    private static readonly byte[] First = Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray();
    private static readonly byte[] Second = Enumerable.Range(0, 200).Select(i => (byte)(255 - i)).ToArray();

    /// <summary>A zlib stream of two stored blocks and its trailer, with the offset where the first block ends.</summary>
    private static (byte[] Stream, int FirstBlockEnd) TwoStoredBlocks()
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78); ms.WriteByte(0x01);
        WriteStored(ms, First, final: false);
        int firstEnd = (int)ms.Length;
        WriteStored(ms, Second, final: true);
        byte[] whole = First.Concat(Second).ToArray();
        uint adler = Adler32(whole);
        ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16)); ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
        return (ms.ToArray(), firstEnd);
    }

    private static void WriteStored(Stream s, byte[] payload, bool final)
    {
        s.WriteByte((byte)(final ? 0x01 : 0x00));   // BFINAL, BTYPE 00, byte-aligned
        s.WriteByte((byte)(payload.Length & 0xFF)); s.WriteByte((byte)(payload.Length >> 8));
        int nlen = ~payload.Length & 0xFFFF;
        s.WriteByte((byte)(nlen & 0xFF)); s.WriteByte((byte)(nlen >> 8));
        s.Write(payload, 0, payload.Length);
    }

    /// <summary>RFC 1950's checksum, written here independently of the filters' copy.</summary>
    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (byte v in data) { a = (a + v) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }

    // ---------------------------------------------------------------- the whole stream

    [Fact]
    public void AWholeStream_DecodesWhole_AndReportsNothing()
    {
        var (stream, _) = TwoStoredBlocks();

        byte[] output = new FlateDecodeFilter().Decode(stream, null);

        Assert.Equal(First.Concat(Second).ToArray(), output);
        Assert.Null(FlateDecodeFilter.LastTruncationReason);
    }

    /// <summary>A producer's end-of-line, or a few bytes of anything, after the trailer is not a defect.</summary>
    [Theory]
    [InlineData(new byte[] { 0x0D, 0x0A })]
    [InlineData(new byte[] { 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20 })]
    public void BytesAfterTheTrailer_AreAllowedFor(byte[] trailing)
    {
        var (stream, _) = TwoStoredBlocks();

        byte[] output = new FlateDecodeFilter().Decode(stream.Concat(trailing).ToArray(), null);

        Assert.Equal(First.Length + Second.Length, output.Length);
        Assert.Null(FlateDecodeFilter.LastTruncationReason);
    }

    // ---------------------------------------------------------------- cut short

    /// <summary>
    /// Cut at the end of the first block, the inflater hands back that block and stops; the slot
    /// says so, with the count produced and the input length, and the output is the prefix.
    /// </summary>
    [Fact]
    public void AStreamCutAtABlockBoundary_ReturnsThePrefix_AndReportsIt()
    {
        var (stream, firstEnd) = TwoStoredBlocks();
        byte[] cut = stream.Take(firstEnd).ToArray();

        byte[] output = new FlateDecodeFilter().Decode(cut, null);

        Assert.Equal(First, output);
        string reason = FlateDecodeFilter.LastTruncationReason;
        Assert.NotNull(reason);
        Assert.StartsWith("FlateDecode:", reason, StringComparison.Ordinal);
        Assert.Contains($"{First.Length} byte(s) produced", reason, StringComparison.Ordinal);
        Assert.Contains($"{cut.Length} input byte(s)", reason, StringComparison.Ordinal);
        Assert.Contains("truncated or corrupt", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The trailer alone cut off: the data is all there, and the slot still says the stream was not
    /// whole — a caller cannot tell that from a cut one block earlier, and the file is damaged either way.
    /// </summary>
    [Fact]
    public void AStreamMissingOnlyItsTrailer_IsReported()
    {
        var (stream, _) = TwoStoredBlocks();
        byte[] cut = stream.Take(stream.Length - 4).ToArray();

        byte[] output = new FlateDecodeFilter().Decode(cut, null);

        Assert.Equal(First.Length + Second.Length, output.Length);
        Assert.NotNull(FlateDecodeFilter.LastTruncationReason);
    }

    /// <summary>A trailer that disagrees with the bytes produced: corrupt, and said so, in each of its four bytes.</summary>
    [Theory]
    [InlineData(4)]
    [InlineData(3)]
    [InlineData(2)]
    [InlineData(1)]
    public void ATrailerThatDisagrees_IsReported(int bytesFromEnd)
    {
        var (stream, _) = TwoStoredBlocks();
        byte[] corrupt = (byte[])stream.Clone();
        corrupt[corrupt.Length - bytesFromEnd] ^= 0x5A;

        byte[] output = new FlateDecodeFilter().Decode(corrupt, null);

        Assert.Equal(First.Length + Second.Length, output.Length);
        Assert.NotNull(FlateDecodeFilter.LastTruncationReason);
    }

    /// <summary>The slot is this decode's: a cut stream, then a whole one, leaves it null.</summary>
    [Fact]
    public void TheReason_IsClearedByTheNextDecode()
    {
        var (stream, firstEnd) = TwoStoredBlocks();
        var filter = new FlateDecodeFilter();

        filter.Decode(stream.Take(firstEnd).ToArray(), null);
        Assert.NotNull(FlateDecodeFilter.LastTruncationReason);

        filter.Decode(stream, null);
        Assert.Null(FlateDecodeFilter.LastTruncationReason);
    }

    /// <summary>
    /// What the encoder writes, the decoder's check accepts — through <c>DeflateStream</c>'s own
    /// blocks, at every level, so the check is of the trailer and not of this file's stored blocks.
    /// </summary>
    [Theory]
    [InlineData(CompressionLevel.Optimal)]
    [InlineData(CompressionLevel.Fastest)]
    [InlineData(CompressionLevel.NoCompression)]
    public void TheEncodersOutput_PassesTheCheck_AndCutShortDoesNot(CompressionLevel level)
    {
        byte[] payload = Enumerable.Range(0, 20_000).Select(i => (byte)((i * 31) ^ (i >> 7))).ToArray();
        byte[] encoded = FlateEncodeFilter.Encode(payload, level);

        byte[] whole = new FlateDecodeFilter().Decode(encoded, null);
        Assert.Equal(payload, whole);
        Assert.Null(FlateDecodeFilter.LastTruncationReason);

        new FlateDecodeFilter().Decode(encoded.Take(encoded.Length - 4).ToArray(), null);
        Assert.NotNull(FlateDecodeFilter.LastTruncationReason);
    }

    // ---------------------------------------------------------------- what is not checked

    /// <summary>
    /// A raw deflate stream has no trailer, so there is nothing to check against: it decodes and
    /// reports nothing — the stated limit, pinned so a check that misfires on raw input is caught.
    /// </summary>
    [Fact]
    public void ARawDeflateStream_IsNotChecked()
    {
        using var ms = new MemoryStream();
        WriteStored(ms, First, final: true);

        byte[] output = new FlateDecodeFilter().Decode(ms.ToArray(), null);

        Assert.Equal(First, output);
        Assert.Null(FlateDecodeFilter.LastTruncationReason);
    }

    // ---------------------------------------------------------------- too short to be deflate

    /// <summary>An empty stream is an empty stream; a one-byte one is not a deflate stream, decodes to nothing and says so — it was returned as the output.</summary>
    [Fact]
    public void AOneByteInput_DecodesToNothing_AndIsReported_AnEmptyOneIsNot()
    {
        byte[] one = new FlateDecodeFilter().Decode(new byte[] { 0x78 }, null);
        Assert.Empty(one);
        Assert.NotNull(FlateDecodeFilter.LastTruncationReason);
        Assert.Contains("1-byte", FlateDecodeFilter.LastTruncationReason!, StringComparison.Ordinal);

        byte[] none = new FlateDecodeFilter().Decode(Array.Empty<byte>(), null);
        Assert.Empty(none);
        Assert.Null(FlateDecodeFilter.LastTruncationReason);
    }
}
