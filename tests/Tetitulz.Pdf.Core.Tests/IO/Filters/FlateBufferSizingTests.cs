// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.IO.Compression;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// <see cref="FlateDecodeFilter"/> sizes its inflate buffer from the compressed length instead of
/// growing a default-capacity <c>MemoryStream</c> from zero (row M10). That is an allocation
/// change and must not be anything else, so every test here asserts <b>decoded bytes</b> or the
/// <b>refusal</b>, never a buffer size and never a clock.
/// </summary>
/// <remarks>
/// <para><b>The lying-<c>/DL</c> cases are the point.</b> A sizing hint that is believed becomes a
/// cap, and a cap that truncates returns a prefix indistinguishable from a short stream that
/// decoded completely — the failure this codebase refuses everywhere else. The shipped filter does
/// not read <c>/DL</c> at all (a filter is handed <c>/DecodeParms</c>, and <c>/DL</c> lives on the
/// stream dictionary), so these pin that nobody later "improves" it into trusting one: a stream
/// declaring <c>/DL 1</c> must still decode in full, and one declaring <c>/DL 2000000000</c> must
/// not allocate towards it.</para>
///
/// <para><b>Two payload shapes, because the hint is a ratio.</b> Zeros deflate ~1000:1, so four
/// times the compressed length under-shoots by three orders of magnitude and the growth path runs;
/// pseudo-random bytes do not deflate at all, so the hint over-shoots and the single-segment path
/// runs. A test using only one of them exercises half the code.</para>
/// </remarks>
public class FlateBufferSizingTests
{
    // ---------------------------------------------------------------- round trip, every shape

    [Fact]
    public void EmptyPayload_DecodesToZeroBytes()
    {
        byte[] decoded = new FlateDecodeFilter().Decode(Deflate(Array.Empty<byte>()), null);
        Assert.Empty(decoded);
    }

    [Fact]
    public void TinyPayload_DecodesToExactlyItsBytes()
    {
        byte[] payload = { 0x2F, 0x00, 0xFF };
        Assert.Equal(payload, new FlateDecodeFilter().Decode(Deflate(payload), null));
    }

    /// <summary>
    /// Sizes chosen to straddle every boundary the buffer logic has: below and above the 256-byte
    /// floor, either side of the 65 536-byte segment size, and exactly on it — the case where the
    /// last segment is filled precisely and the assembly loop must not read a partial one.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(65_535)]
    [InlineData(65_536)]
    [InlineData(65_537)]
    [InlineData(131_072)]
    [InlineData(200_003)]
    public void CompressiblePayload_DecodesByteForByte(int size)
        => AssertRoundTrips(Compressible(size));

    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(65_535)]
    [InlineData(65_536)]
    [InlineData(65_537)]
    [InlineData(131_072)]
    [InlineData(200_003)]
    public void IncompressiblePayload_DecodesByteForByte(int size)
        => AssertRoundTrips(Incompressible(size));

    /// <summary>
    /// The ratio extreme in the direction the hint cannot anticipate: 4 MB of zeros compresses to a
    /// few KB, so four times the compressed length is off by three orders of magnitude. It must
    /// still decode in full.
    /// </summary>
    [Fact]
    public void PayloadThatDeflatesAThousandToOne_DecodesInFull()
    {
        byte[] payload = new byte[4 * 1024 * 1024];
        byte[] compressed = Deflate(payload);

        Assert.True(compressed.Length * 100 < payload.Length, "fixture is not the extreme ratio it claims");

        byte[] decoded = new FlateDecodeFilter().Decode(compressed, null);
        Assert.Equal(payload.Length, decoded.Length);
        Assert.All(decoded, b => Assert.Equal((byte)0, b));
    }

    /// <summary>The predictor path still sees the same bytes — the buffer feeds it.</summary>
    [Fact]
    public void PngPredictorStage_StillReceivesTheSameDecodedBytes()
    {
        const int Columns = 100, Rows = 700;
        var predicted = new byte[Rows * (Columns + 1)];
        for (int row = 0; row < Rows; row++)
        {
            predicted[row * (Columns + 1)] = 2;      // PNG "Up"
            for (int col = 0; col < Columns; col++)
                predicted[row * (Columns + 1) + 1 + col] = (byte)((row * 31 + col * 7) % 251);
        }

        var parms = new PdfDictionary();
        parms[PdfName.Predictor] = new PdfNumber(12);
        parms[PdfName.Columns] = new PdfNumber(Columns);
        parms[PdfName.Colors] = new PdfNumber(1);
        parms[PdfName.BitsPerComponent] = new PdfNumber(8);

        byte[] decoded = new FlateDecodeFilter().Decode(Deflate(predicted), parms);

        Assert.Equal(Rows * Columns, decoded.Length);
        // Un-differencing "Up" over the fixture reproduces the running sum down each column.
        var expected = new byte[Rows * Columns];
        for (int row = 0; row < Rows; row++)
            for (int col = 0; col < Columns; col++)
            {
                byte above = row == 0 ? (byte)0 : expected[(row - 1) * Columns + col];
                expected[row * Columns + col] = (byte)(predicted[row * (Columns + 1) + 1 + col] + above);
            }
        Assert.Equal(expected, decoded);
    }

    // ---------------------------------------------------------------- /DL is not consulted

    [Fact]
    public void TruthfulDeclaredLength_DecodesToExactlyThatMany()
    {
        byte[] payload = Compressible(120_000);
        var stream = FlateStream(payload);
        stream[DeclaredLength] = new PdfNumber(payload.Length);

        Assert.Equal(payload, stream.GetDecodedData());
    }

    /// <summary>
    /// A <c>/DL</c> far <b>below</b> the truth. Believed as a capacity it would merely be slow;
    /// believed as a limit it would truncate, and 119 999 bytes of a 120 000-byte content stream
    /// parses as a valid — and wrong — page.
    /// </summary>
    [Fact]
    public void DeclaredLengthFarBelowTheTruth_StillDecodesEveryByte()
    {
        byte[] payload = Compressible(120_000);
        var stream = FlateStream(payload);
        stream[DeclaredLength] = new PdfNumber(1);

        Assert.Equal(payload, stream.GetDecodedData());
    }

    /// <summary>
    /// A <c>/DL</c> of two billion on a 100 KB stream: decoded in full, and without allocating
    /// anything like what the file asked for. Allocation is asserted per-thread rather than by
    /// wall-clock, the way <c>BoundedDecodeTests</c> does — the inflate loop is synchronous and
    /// stays on this thread.
    /// </summary>
    [Fact]
    public void AbsurdDeclaredLength_DecodesInFullWithoutAllocatingTowardsIt()
    {
        byte[] payload = Incompressible(100_000);
        var stream = FlateStream(payload);
        stream[DeclaredLength] = new PdfNumber(2_000_000_000);

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] decoded = stream.GetDecodedData();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(payload, decoded);
        Assert.True(allocated < 8 * 1024 * 1024,
            $"allocated {allocated} bytes decoding a 100 KB stream that declares /DL 2000000000");
    }

    // ---------------------------------------------------------------- the ceiling still refuses

    /// <summary>
    /// The refusal, with its message. A sizing hint that quietly became the
    /// cap would change this text, or stop it being thrown at all.
    /// </summary>
    [Fact]
    public void OverTheCeiling_ThrowsTheSameRefusalAsBefore()
    {
        byte[] payload = Compressible(300_000);
        var filter = new FlateDecodeFilter { MaxOutputBytes = 100_000 };

        var ex = Assert.Throws<PdfException>(() => filter.Decode(Deflate(payload), null));
        Assert.Equal("Flate stream exceeds the 100000-byte decode limit for this call.", ex.Message);
    }

    /// <summary>
    /// The boundary is inclusive: a stream that decodes to exactly the ceiling is accepted. The
    /// check is <c>&gt;</c> and not <c>&gt;=</c>, and <c>DecompressionBombTests</c> records an
    /// off-by-one here passing for the wrong reason once already.
    /// </summary>
    [Fact]
    public void ExactlyAtTheCeiling_IsAccepted()
    {
        byte[] payload = Incompressible(50_000);
        var filter = new FlateDecodeFilter { MaxOutputBytes = payload.Length };

        Assert.Equal(payload, filter.Decode(Deflate(payload), null));
    }

    [Fact]
    public void OneByteOverTheCeiling_IsRefused()
    {
        byte[] payload = Incompressible(50_000);
        var filter = new FlateDecodeFilter { MaxOutputBytes = payload.Length - 1 };

        var ex = Assert.Throws<PdfException>(() => filter.Decode(Deflate(payload), null));
        Assert.Equal($"Flate stream exceeds the {payload.Length - 1}-byte decode limit for this call.",
            ex.Message);
    }

    /// <summary>
    /// The production route: the same refusal reaches a caller through the pipeline and
    /// <see cref="PdfStream.TryGetDecodedData(long, out byte[], out string)"/> as a reason string,
    /// with no bytes — a prefix is never an answer.
    /// </summary>
    [Fact]
    public void ThroughThePipeline_AnOversizedStreamIsRefusedWithItsReasonAndNoBytes()
    {
        var stream = FlateStream(Compressible(400_000));

        Assert.False(stream.TryGetDecodedData(50_000, out byte[] data, out string reason));
        Assert.Null(data);
        Assert.Equal("Flate stream exceeds the 50000-byte decode limit for this call.", reason);
    }

    /// <summary>
    /// A tiny ceiling must not make the first buffer bigger than the ceiling it is about to refuse
    /// against — the one way a "reasonable first capacity" can defeat the limit it sits under.
    /// </summary>
    /// <remarks>
    /// <b>The payload is incompressible on purpose.</b> Eight megabytes of zeros compress to a few
    /// kilobytes, so any hint derived from the compressed length stays small whatever the sizing
    /// rule is, and the allocation clause below would pass with the clamps deleted — a fixture that
    /// cannot reach the feature. Eight megabytes of noise compresses to eight megabytes, so a hint
    /// of "four times the input" is 32 MB and only the clamps keep this test green.
    /// </remarks>
    [Fact]
    public void TinyCeilingOnALargeStream_RefusesWithoutAllocatingAMegabyte()
    {
        var stream = FlateStream(Incompressible(8 * 1024 * 1024));

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool ok = stream.TryGetDecodedData(1024, out byte[] data, out string reason);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.False(ok);
        Assert.Null(data);
        Assert.Equal("Flate stream exceeds the 1024-byte decode limit for this call.", reason);
        Assert.True(allocated < 256 * 1024,
            $"allocated {allocated} bytes decoding under a 1024-byte ceiling");
    }

    /// <summary>
    /// The default ceiling is unchanged, so the three-argument pipeline call behaves as it always
    /// did on an ordinary stream.
    /// </summary>
    [Fact]
    public void AtCoresOwnLimit_TheDefaultCeilingStillDecodesAnOrdinaryStream()
    {
        byte[] payload = Compressible(250_000);
        Assert.Equal(PdfLimits.MaxDecompressedBytes, new FlateDecodeFilter().MaxOutputBytes);
        Assert.Equal(payload, FilterPipeline.Decode(Deflate(payload), PdfName.FlateDecode, null));
    }

    // ---------------------------------------------------------------- helpers

    private static readonly PdfName DeclaredLength = new PdfName("DL");

    private static void AssertRoundTrips(byte[] payload)
    {
        byte[] decoded = new FlateDecodeFilter().Decode(Deflate(payload), null);
        Assert.Equal(payload.Length, decoded.Length);
        Assert.Equal(payload, decoded);
    }

    /// <summary>Deflates ~20:1 — the shape of a real content stream, and enough to make four times
    /// the compressed length an under-estimate.</summary>
    private static byte[] Compressible(int size)
    {
        var payload = new byte[size];
        for (int i = 0; i < size; i++) payload[i] = (byte)(0x41 + i % 7);
        return payload;
    }

    /// <summary>Does not deflate at all, so the compressed length over-estimates the output.</summary>
    private static byte[] Incompressible(int size)
    {
        var payload = new byte[size];
        uint state = 0x9E3779B9;
        for (int i = 0; i < size; i++)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            payload[i] = (byte)state;
        }
        return payload;
    }

    private static byte[] Deflate(byte[] payload)
    {
        using var raw = new MemoryStream();
        raw.WriteByte(0x78);            // zlib header, which the filter skips
        raw.WriteByte(0x9C);
        using (var deflate = new DeflateStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(payload, 0, payload.Length);
        return raw.ToArray();
    }

    private static PdfStream FlateStream(byte[] payload)
    {
        var stream = new PdfStream { RawData = Deflate(payload) };
        stream.Set(PdfName.Filter, PdfName.FlateDecode);
        return stream;
    }
}
