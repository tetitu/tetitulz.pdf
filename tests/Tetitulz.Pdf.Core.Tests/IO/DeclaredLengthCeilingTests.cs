// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.IO.Compression;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO;

/// <summary>
/// <see cref="PdfStream.TryGetDecodedData(long, out byte[], out string)"/> consults a declared
/// <c>/DL</c> — ISO 32000-2 Table 5, the decoded length — <b>before</b> decoding (GAPS.md §12c).
/// </summary>
/// <remarks>
/// <para><b>The bound was honoured, but paid for.</b> A filtered stream whose producer declared a
/// <c>/DL</c> above the caller's ceiling still inflated up to that ceiling before it was refused.
/// The declaration is exactly the information the refusal needs, so it is read first, and the
/// reason names it.</para>
///
/// <para><b>A <c>/DL</c> at or under the ceiling changes nothing.</b> It is only what the producer
/// declared; the bound still applies during the inflate, so a lying <c>/DL</c> cannot buy an
/// allocation past the limit. And an unfiltered stream's raw length <i>is</i> its decoded length,
/// so there the declaration is not consulted at all.</para>
///
/// <para>The key is spelled as a literal here, as every fixture in this tree does, so that a typo in
/// the production constant's value cannot move the fixture and the assertion together.</para>
/// </remarks>
public class DeclaredLengthCeilingTests
{
    private static readonly PdfName DeclaredLength = new PdfName("DL");

    /// <summary>
    /// The point of the change: a declaration over the ceiling is refused with no inflate at all,
    /// and the refusal says what it read.
    /// </summary>
    [Fact]
    public void ADeclaredLengthOverTheCeiling_IsRefusedBeforeAnyByteIsInflated()
    {
        const int Bomb = 64 * 1024 * 1024;      // 64 MiB of zeros, ~64 KB compressed
        const long Ceiling = 1024 * 1024;       // the caller accepts 1 MiB

        var stream = FlateStream(new byte[Bomb]);
        stream.Set(DeclaredLength, new PdfNumber(Bomb));

        // Per thread, for the reason BoundedDecodeTests gives: process-wide counts include whatever
        // a neighbouring test class allocates in the same window.
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool ok = stream.TryGetDecodedData(Ceiling, out byte[] data, out string reason);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.False(ok);
        Assert.Null(data);
        Assert.Contains("/DL", reason, StringComparison.Ordinal);

        // A bounded inflate copies 80 KB chunks into a doubling MemoryStream up to the ceiling,
        // so it allocates megabytes; a refusal that reads /DL first allocates a message.
        Assert.True(allocated < 256 * 1024,
            $"allocated {allocated} bytes refusing a stream whose /DL was already over the ceiling");
    }

    /// <summary>At the ceiling is not over it: the declaration alone must not refuse.</summary>
    [Fact]
    public void ADeclaredLengthAtTheCeiling_StillDecodes()
    {
        byte[] payload = Payload(100_000, 251);
        var stream = FlateStream(payload);
        stream.Set(DeclaredLength, new PdfNumber(payload.Length));

        Assert.True(stream.TryGetDecodedData(payload.Length, out byte[] data, out string reason));
        Assert.Null(reason);
        Assert.Equal(payload, data);
    }

    /// <summary>
    /// The producer lies low: <c>/DL 10</c> on a stream that inflates to 200 000 bytes. The inflate
    /// keeps its own bound, and the refusal is the inflate's, not the declaration's.
    /// </summary>
    [Fact]
    public void ADeclaredLengthUnderTheCeiling_DoesNotLiftTheBoundOnTheInflate()
    {
        byte[] payload = Payload(200_000, 253);
        var stream = FlateStream(payload);
        stream.Set(DeclaredLength, new PdfNumber(10));

        Assert.False(stream.TryGetDecodedData(50_000, out byte[] data, out string reason));
        Assert.Null(data);
        Assert.DoesNotContain("/DL", reason, StringComparison.Ordinal);
    }

    /// <summary>A <c>/DL</c> that is not a number says nothing, and the decode proceeds.</summary>
    [Fact]
    public void ADeclaredLengthThatIsNotANumber_IsIgnored()
    {
        byte[] payload = Payload(1_000, 241);
        var stream = FlateStream(payload);
        stream.Set(DeclaredLength, new PdfName("Foo"));

        Assert.True(stream.TryGetDecodedData(100_000, out byte[] data, out string reason));
        Assert.Null(reason);
        Assert.Equal(payload, data);
    }

    /// <summary>
    /// Unfiltered, the raw length is the decoded length and is known for free; a <c>/DL</c> that
    /// disagrees with it is simply wrong and must not refuse a stream that fits.
    /// </summary>
    [Fact]
    public void AnUnfilteredStreamsDeclaredLength_IsNotConsulted()
    {
        var stream = new PdfStream { RawData = new byte[100] };
        stream.Set(DeclaredLength, new PdfNumber(999_999));

        Assert.True(stream.TryGetDecodedData(1000, out byte[] data, out string reason));
        Assert.Null(reason);
        Assert.Equal(100, data.Length);
    }

    /// <summary>
    /// The same memo rule <c>BoundedDecodeTests</c> pins for the inflate's refusal: a refusal on the
    /// declaration leaves the stream intact, and an unbounded read still returns all of it.
    /// </summary>
    [Fact]
    public void ARefusalOnTheDeclaredLength_DoesNotPoisonTheMemo()
    {
        byte[] payload = Payload(200_000, 199);
        var stream = FlateStream(payload);
        stream.Set(DeclaredLength, new PdfNumber(payload.Length));

        Assert.False(stream.TryGetDecodedData(1000, out _, out string reason));
        Assert.Contains("/DL", reason, StringComparison.Ordinal);
        Assert.Equal(payload, stream.GetDecodedData());
    }

    private static byte[] Payload(int length, int modulus)
    {
        var payload = new byte[length];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % modulus);
        return payload;
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
