// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.IO.Compression;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Security tests for stream-filter decompression bombs and adversarial
/// predictor dimensions.  Each test passes a small input that would, absent
/// defensive caps, expand to gigabytes or corrupt internal buffers.
/// </summary>
/// <remarks>
/// In the <c>PdfLimits</c> collection because the cumulative-output tests lower
/// <see cref="PdfLimits.MaxCumulativeOutputBytes"/>, which is process-global — and lower it through
/// <c>PdfLimits</c>, as a host would: the pipeline used to copy the value into a static field at type
/// initialisation, so a host setting it after the first decode was not honoured (the review of
/// 2026-10-16, R1.5), and these tests set that field directly and so could not see it.
/// </remarks>
[Collection(Constants.PdfLimitsCollection.Name)]
public class DecompressionBombTests
{
    // ==================================================================
    //  RunLengthDecode — output-size cap
    // ==================================================================

    [Fact]
    public void RunLengthDecode_MaliciousRepeatRuns_ThrowsBeforeOomRisk()
    {
        // Each (0x00, byte) pair expands to 129 bytes of output (length=0 →
        // 257-0=257 hmm actually length byte 0 means "copy 1 literal".
        // To trigger the repeat path we need length >= 129.  Each "0x81 byte"
        // pair expands to 128 copies of `byte`.
        // Build ~1 MB of such pairs → 128 MB of decompressed output.  Our cap
        // is 512 MiB so we need enough pairs to cross it.
        // 4 Mi pairs × 128 bytes is 536,870,912 — EXACTLY the 512 MiB cap, not over it, despite
        // what this line used to claim. The filter's two checks were `>=` where Flate and LZW use
        // `>`, so landing precisely on the ceiling threw and the off-by-one fixture passed for the
        // wrong reason. The ceiling is inclusive now (a stream whose /DL is exactly n and which
        // decodes to exactly n bytes must be accepted), so the bomb has to genuinely exceed it.
        int pairCount = 4 * 1024 * 1024 + 1;
        byte[] input = new byte[pairCount * 2 + 1];
        for (int i = 0; i < pairCount; i++)
        {
            input[i * 2]     = 0x81;   // repeat 128 times
            input[i * 2 + 1] = 0xAA;
        }
        input[input.Length - 1] = 128;   // EOD

        var filter = new RunLengthDecodeFilter();
        Assert.Throws<InvalidDataException>(() => filter.Decode(input, null));
    }

    [Fact]
    public void RunLengthDecode_NormalInput_StillDecodes()
    {
        // 0x03 = copy 4 literals: "Hell"; 0x00 = copy 1 literal: "o"; 0x80 = EOD.
        byte[] input = { 0x03, (byte)'H', (byte)'e', (byte)'l', (byte)'l', 0x00, (byte)'o', 0x80 };
        var filter = new RunLengthDecodeFilter();
        byte[] output = filter.Decode(input, null);
        Assert.Equal(new byte[] { (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o' }, output);
    }

    // ==================================================================
    //  LZW — output-size cap
    // ==================================================================

    [Fact]
    public void LzwDecode_NormalStream_DecodesSuccessfully()
    {
        // Construct a minimal valid LZW stream that emits a few literal bytes.
        // Bit stream: ClearTable(256) as 9 bits, then 'A' 'B' 'C' as 9-bit codes, then EOD(257).
        // 256 = 100000000, 65 = 001000001, 66 = 001000010, 67 = 001000011, 257 = 100000001.
        // Concatenated MSB-first: 100000000 001000001 001000010 001000011 100000001
        // = 45 bits → 6 bytes (last byte padded).
        var bits = new System.Text.StringBuilder();
        bits.Append("100000000"); // ClearTable
        bits.Append("001000001"); // 'A'
        bits.Append("001000010"); // 'B'
        bits.Append("001000011"); // 'C'
        bits.Append("100000001"); // EOD
        while (bits.Length % 8 != 0) bits.Append('0');

        byte[] input = new byte[bits.Length / 8];
        for (int i = 0; i < bits.Length; i++)
            if (bits[i] == '1')
                input[i / 8] |= (byte)(0x80 >> (i % 8));

        var filter = new LzwDecodeFilter();
        byte[] output = filter.Decode(input, null);
        Assert.Equal(new byte[] { (byte)'A', (byte)'B', (byte)'C' }, output);
    }

    // ==================================================================
    //  PredictorHelper — rejects adversarial dimensions
    // ==================================================================

    [Fact]
    public void RemovePngPredictor_RejectsUnreasonableColumns()
    {
        // columns = 2_000_000 blows past our 1,000,000 cap → return input unchanged.
        byte[] input = new byte[1024];
        byte[] output = PredictorHelper.RemovePngPredictor(input,
            columns: 2_000_000, colors: 3, bitsPerComponent: 8);
        Assert.Same(input, output);
    }

    [Fact]
    public void RemovePngPredictor_RejectsUnreasonableBitsPerComponent()
    {
        byte[] input = new byte[1024];
        byte[] output = PredictorHelper.RemovePngPredictor(input,
            columns: 100, colors: 3, bitsPerComponent: 10_000);
        Assert.Same(input, output);
    }

    [Fact]
    public void RemovePngPredictor_ValidDimensions_DecodesNormally()
    {
        // Row = 10 bytes RGB (3 colors × 8 bpc × 3 columns = 9 bytes... let's keep it simple).
        // 3 columns × 3 colors × 8 bits = 72 bits = 9 bytes per row.
        // 1 row + 1 filter byte = 10 bytes.
        byte[] input = new byte[10]; // filter type 0, all zeros
        byte[] output = PredictorHelper.RemovePngPredictor(input,
            columns: 3, colors: 3, bitsPerComponent: 8);
        Assert.Equal(9, output.Length);
    }

    [Fact]
    public void RemoveTiffPredictor_RejectsUnreasonableColumns()
    {
        byte[] input = new byte[1024];
        byte[] output = PredictorHelper.RemoveTiffPredictor(input,
            columns: 5_000_000, colors: 3, bitsPerComponent: 8);
        Assert.Same(input, output);
    }

    // ==================================================================
    //  FilterPipeline — cumulative cross-stage output cap (§88 H1)
    // ==================================================================

    [Fact]
    public void FilterPipeline_TwoStageFlateDecode_CumulativeCapThrowsBeforeOomRisk()
    {
        // The hole a shared budget closes: each filter stage caps its own output at
        // 512 MB, so without one a 16-stage chain could reach 16 × 512 MB = 8 GB
        // before any per-stage guard fires.
        //
        // Setup: compress 2 MB of zeros twice. During the two-stage decode:
        //   Stage 1: ~100 bytes → ~2 KB (inner_compressed)
        //   Stage 2: ~2 KB    → 2 MB  (raw zeros)
        //   Cumulative total = ~2 MB, which exceeds the test threshold of 1 MB.
        byte[] raw = new byte[2 * 1024 * 1024]; // 2 MB of zeros — highly compressible
        byte[] inner = FlatCompress(raw);
        byte[] outer = FlatCompress(inner);

        var filterArray = new PdfArray(new PdfObject[]
        {
            new PdfName("FlateDecode"),
            new PdfName("FlateDecode"),
        });

        try
        {
            // Lowered AFTER a decode has run, so the pipeline's type is initialised: a copy taken then
            // would hold the default and this would not throw.
            FilterPipeline.Decode(FlatCompress(new byte[] { 1, 2, 3 }), new PdfName("FlateDecode"), null);
            PdfLimits.MaxCumulativeOutputBytes = 1L * 1024 * 1024; // 1 MB for the test
            Assert.Throws<PdfFilterException>(
                () => FilterPipeline.Decode(outer, filterArray, null));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    [Fact]
    public void FilterPipeline_SingleStageUnderCumulativeCap_Succeeds()
    {
        // A normal single-stage decode should be unaffected by the cumulative cap.
        byte[] raw = System.Text.Encoding.ASCII.GetBytes("hello world");
        byte[] compressed = FlatCompress(raw);

        try
        {
            PdfLimits.MaxCumulativeOutputBytes = 1L * 1024 * 1024; // 1 MB threshold
            byte[] result = FilterPipeline.Decode(compressed, new PdfName("FlateDecode"), null);
            Assert.Equal(raw, result);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    private static byte[] FlatCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78); // zlib CMF
        ms.WriteByte(0x9C); // zlib FLG
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data, 0, data.Length);
        return ms.ToArray();
    }
}
