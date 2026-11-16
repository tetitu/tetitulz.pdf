// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.IO.Compression;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO;

/// <summary>
/// <see cref="PdfStream.TryGetDecodedData(long, out byte[], out string)"/> — a decode bounded by
/// the <b>caller's</b> ceiling rather than Core's global one (GAPS.md §5c).
///
/// <para><b>What this closes.</b> A filtered stream that declares no <c>/DL</c> cannot be sized
/// from anything in the file — that is what a compression filter is — so a caller with a 4 MiB
/// limit still had to inflate up to <see cref="PdfLimits.MaxDecompressedBytes"/> (512 MiB) to
/// discover it was over. The transient was bounded, but by the wrong number.</para>
///
/// <para><b>Asserted on allocation, not on wall-clock.</b> GAPS.md §8a records wall-clock
/// assertions in DoS tests as a flakiness cause, and §7b records a 25-second run passing under a
/// 30-second budget. The refusal is deterministic and the allocation is measurable, so both are
/// asserted directly.</para>
/// </summary>
public class BoundedDecodeTests
{
    /// <summary>
    /// The point of the whole change: a stream that would inflate far past the caller's ceiling is
    /// refused, and the memory to prove it was never allocated.
    /// </summary>
    [Fact]
    public void FlateBomb_IsRefusedAtTheCallersCeiling_WithoutAllocatingPastIt()
    {
        const int Bomb = 64 * 1024 * 1024;      // 64 MiB of zeros, ~64 KB compressed
        const long Ceiling = 1 * 1024 * 1024;   // the caller will not accept more than 1 MiB

        var stream = FlateStream(new byte[Bomb]);

        // Per-THREAD, not process-wide. GC.GetTotalAllocatedBytes counts every thread, so under
        // the ordinary parallel test run this delta included whatever a neighbouring test class
        // happened to allocate in the same window — and the assertion below failed intermittently
        // for a reason that had nothing to do with the decoder. `dotnet test --blame`, which runs
        // sequentially, passed all 958 every time, which is what identified it as concurrency
        // rather than a wrong assertion.
        //
        // GetAllocatedBytesForCurrentThread makes the measurement *tighter*, not looser: it is the
        // number this test always meant. It is only valid because the inflate loop is synchronous
        // and stays on the calling thread — if TryGetDecodedData ever parallelises internally,
        // this undercounts and the guard silently weakens.
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool ok = stream.TryGetDecodedData(Ceiling, out byte[] data, out string reason);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.False(ok);
        Assert.Null(data);                       // no prefix — a partial decode is not an answer
        Assert.Contains("limit", reason, StringComparison.OrdinalIgnoreCase);

        // The inflate loop copies in 80 KB chunks into a doubling MemoryStream, so a run that
        // honours a 1 MiB ceiling cannot have touched tens of megabytes. Generous enough not to
        // be brittle, tight enough that the unbounded path (which reaches 64 MiB) fails it.
        Assert.True(allocated < 8 * 1024 * 1024,
            $"allocated {allocated} bytes decoding under a {Ceiling}-byte ceiling; the bound is not being honoured");
    }

    /// <summary>
    /// <b>A budget spent to nothing refuses.</b> The pipeline reads a ceiling below one as its own
    /// default, so without the check here the caller whose budget has just run out gets the largest
    /// decode there is, once a stream. The allocation shows it stopped; the control is a ceiling of one
    /// byte's room over the same stream's single byte.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void AdversarialCeilingOfNothing_RefusesRatherThanFallingBackToTheDefault(long ceiling)
    {
        var bomb = FlateStream(new byte[16 * 1024 * 1024]);

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool ok = bomb.TryGetDecodedData(ceiling, out byte[] data, out string reason);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.False(ok);
        Assert.Null(data);
        Assert.Contains("limit", reason, StringComparison.Ordinal);
        Assert.True(allocated < 1024 * 1024, $"allocated {allocated} bytes under a ceiling of {ceiling}");

        Assert.False(bomb.TryGetDecodedDataUncached(ceiling, out _, out _));
        Assert.True(FlateStream(new byte[1]).TryGetDecodedData(1, out byte[] one, out _));
        Assert.Single(one);
    }

    /// <summary>
    /// The control. The same stream under a ceiling that accommodates it decodes completely and
    /// correctly — without this, a method that refused everything would satisfy the test above.
    /// </summary>
    [Fact]
    public void WithinTheCeiling_DecodesCompletely()
    {
        byte[] payload = new byte[100_000];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);

        var stream = FlateStream(payload);

        Assert.True(stream.TryGetDecodedData(1024 * 1024, out byte[] data, out string reason));
        Assert.Null(reason);
        Assert.Equal(payload, data);
    }

    /// <summary>
    /// An unfiltered stream is measured for free: its raw length <i>is</i> its decoded length, so
    /// an over-ceiling one is refused with no decode at all, and the reason says so.
    /// </summary>
    [Fact]
    public void UnfilteredStreamOverTheCeiling_IsRefusedWithoutDecoding()
    {
        var stream = new PdfStream { RawData = new byte[10_000] };

        Assert.False(stream.TryGetDecodedData(1000, out byte[] data, out string reason));
        Assert.Null(data);
        Assert.Contains("without decoding", reason);
    }

    /// <summary>
    /// A refusal must not poison the memo: the stream is intact, and an unbounded read still
    /// returns all of it. Otherwise one guarded call would corrupt every later one.
    /// </summary>
    [Fact]
    public void ARefusedDecode_DoesNotPreventALaterUnboundedOne()
    {
        byte[] payload = new byte[200_000];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 253);

        var stream = FlateStream(payload);

        Assert.False(stream.TryGetDecodedData(1000, out _, out _));
        Assert.Equal(payload, stream.GetDecodedData());
    }

    /// <summary>
    /// The compatibility claim the change rests on: passing Core's own limit is the behaviour
    /// every existing caller had. If this diverges, the three-argument
    /// <c>FilterPipeline.Decode</c> is no longer the same operation.
    /// </summary>
    [Fact]
    public void AtCoresOwnLimit_MatchesTheUnboundedPathExactly()
    {
        byte[] payload = new byte[50_000];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 199);

        Assert.True(FlateStream(payload)
            .TryGetDecodedData(PdfLimits.MaxDecompressedBytes, out byte[] bounded, out _));
        Assert.Equal(FlateStream(payload).GetDecodedData(), bounded);
    }

    /// <summary>
    /// <see cref="PdfStream.TryGetDecodedDataUncached"/> leaves no memo behind, and the cached form
    /// beside it does. The memo is read by changing <c>/Filter</c> without touching the raw bytes (a
    /// <c>Set</c> does not clear it): a remembered decode still answers the old filter's bytes, a
    /// fresh one reads the raw bytes as they now stand.
    /// </summary>
    [Fact]
    public void TheUncachedDecode_LeavesNoMemo_WhereTheCachedOneDoes()
    {
        byte[] payload = new byte[30_000];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 241);

        var uncached = FlateStream(payload);
        Assert.True(uncached.TryGetDecodedDataUncached(1024 * 1024, out byte[] data, out string reason));
        Assert.Null(reason);
        Assert.Equal(payload, data);
        uncached.Remove(PdfName.Filter);
        Assert.Equal(uncached.RawData, uncached.GetDecodedData());

        var cached = FlateStream(payload);
        Assert.True(cached.TryGetDecodedData(1024 * 1024, out _, out _));
        cached.Remove(PdfName.Filter);
        Assert.Equal(payload, cached.GetDecodedData());
    }

    /// <summary>
    /// The unfiltered arm too: its decode is the raw array, and the uncached form does not record it
    /// as the decode — a <c>/Filter</c> set afterwards is decoded, not answered from the memo.
    /// </summary>
    [Fact]
    public void TheUncachedDecode_OfAnUnfilteredStream_LeavesNoMemo()
    {
        byte[] payload = new byte[5_000];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 239);
        byte[] zlib = FlateStream(payload).RawData;

        var stream = new PdfStream { RawData = zlib };
        Assert.True(stream.TryGetDecodedDataUncached(1024 * 1024, out byte[] data, out _));
        Assert.Same(zlib, data);

        stream.Set(PdfName.Filter, PdfName.FlateDecode);
        Assert.Equal(payload, stream.GetDecodedData());
    }

    /// <summary>
    /// A memo that already exists is handed over as it is — no second decode, the same array.
    /// </summary>
    [Fact]
    public void TheUncachedDecode_ReturnsAnExistingMemo()
    {
        var stream = FlateStream(new byte[1_000]);
        byte[] memo = stream.GetDecodedData();

        Assert.True(stream.TryGetDecodedDataUncached(1024 * 1024, out byte[] data, out _));
        Assert.Same(memo, data);
    }

    private static PdfStream FlateStream(byte[] payload)
    {
        using var raw = new MemoryStream();
        raw.WriteByte(0x78);            // zlib header, which the filter skips
        raw.WriteByte(0x9C);
        using (var deflate = new DeflateStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(payload, 0, payload.Length);

        var stream = new PdfStream { RawData = raw.ToArray() };
        stream.Set(PdfName.Filter, PdfName.FlateDecode);
        return stream;
    }
}
