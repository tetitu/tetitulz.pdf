// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Regression tests for review-fix round on the stream-filter predictor / LZW
/// code paths:
///   1. LZW bitPos bounds check uses long arithmetic (no int overflow for &gt;= 256 MiB input).
///   2. LZW dictionary growth is capped at 4096 entries (no unbounded table bloat).
///   3. TIFF predictor (predictor=2) is correct for 16-bpc and bails for sub-byte depths.
/// </summary>
public class ReviewFixFilters_PredictorAndLzwTests
{
    // ==================================================================
    //  LZW — dictionary-growth cap must not break a normal round-trip
    // ==================================================================

    [Fact]
    public void LzwDecode_RoundTrip_RepetitiveBufferThatGrowsDictionary_Recovers()
    {
        // A long pseudo-random buffer (deterministic LCG) creates many distinct
        // substrings, filling the LZW dictionary to its 4096-entry code-space limit
        // and exercising the new freeze-at-cap path in the decoder.  The decode must
        // still reproduce the input exactly.
        byte[] original = new byte[50_000];
        uint s = 0x12345678u;
        for (int i = 0; i < original.Length; i++)
        {
            s = s * 1664525u + 1013904223u;
            original[i] = (byte)(s >> 24);
        }

        byte[] encoded = EncodeLzw(original);

        var filter = new LzwDecodeFilter();
        byte[] decoded = filter.Decode(encoded, null);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void LzwDecode_RoundTrip_ShortLiteralBuffer_Recovers()
    {
        byte[] original = System.Text.Encoding.ASCII.GetBytes("TOBEORNOTTOBEORTOBEORNOT");
        byte[] encoded = EncodeLzw(original);

        var filter = new LzwDecodeFilter();
        byte[] decoded = filter.Decode(encoded, null);

        Assert.Equal(original, decoded);
    }

    // ==================================================================
    //  TIFF predictor (predictor=2) — 16-bpc round-trip
    // ==================================================================

    [Fact]
    public void RemoveTiffPredictor_16Bpc_RoundTripsCorrectly()
    {
        const int columns = 4;
        const int colors = 3;       // RGB
        const int bpc = 16;
        int samplesPerRow = columns * colors;

        // Build two rows of arbitrary 16-bit samples.
        ushort[] row0 = { 100, 200, 300, 110, 210, 310, 120, 220, 320, 130, 230, 330 };
        ushort[] row1 = { 4000, 50000, 12345, 4010, 49000, 13000, 4020, 48000, 13500, 4030, 47000, 14000 };

        // Apply the TIFF predictor (horizontal differencing) to produce the
        // "predicted" stream that the decoder's RemoveTiffPredictor must reverse.
        byte[] predicted = ApplyTiffPredictor16(new[] { row0, row1 }, colors);

        byte[] restored = PredictorHelper.RemoveTiffPredictor(predicted, columns, colors, bpc);

        byte[] expected = SamplesToBytes(new[] { row0, row1 });
        Assert.Equal(expected, restored);
    }

    [Fact]
    public void RemoveTiffPredictor_8Bpc_RoundTripsCorrectly()
    {
        // Sanity: the existing 8-bpc path still round-trips after the refactor.
        const int columns = 5;
        const int colors = 2;
        const int bpc = 8;

        byte[][] rows =
        {
            new byte[] { 10, 20, 11, 21, 12, 22, 13, 23, 14, 24 },
            new byte[] { 200, 100, 201, 99, 202, 98, 203, 97, 204, 96 },
        };

        byte[] predicted = ApplyTiffPredictor8(rows, colors);
        byte[] restored = PredictorHelper.RemoveTiffPredictor(predicted, columns, colors, bpc);

        byte[] expected = Flatten(rows);
        Assert.Equal(expected, restored);
    }

    // ==================================================================
    //  TIFF predictor — sub-byte depths (1/2/4 bpc)
    // ==================================================================
    //
    // ISO 32000-1 §7.4.4.4 permits 1, 2 and 4 bits per component, so a 4-bpc indexed image with
    // /Predictor 2 is ordinary conformant input; returned unchanged, it would render as a
    // horizontal colour smear.

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void RemoveTiffPredictor_SubByteDepth_RoundTripsHorizontalDifferencing(int bpc)
    {
        const int columns = 4, colors = 1;
        int mask = (1 << bpc) - 1;

        // Two rows of `columns` samples, each within the depth's range.
        var rows = new[]
        {
            new[] { 1 & mask, 2 & mask, 3 & mask, 0 & mask },
            new[] { mask, 1 & mask, mask, 2 & mask },
        };

        byte[] predicted = ApplyTiffPredictorSubByte(rows, colors, bpc, columns);
        byte[] restored = PredictorHelper.RemoveTiffPredictor(predicted, columns, colors, bpc);

        Assert.Equal(ExpandSubByte(rows, bpc, columns), ExpandSubByte(restored, bpc, columns, rows.Length));
        Assert.Null(PredictorHelper.LastPredictorSkipReason);
    }

    [Fact]
    public void RemoveTiffPredictor_UnsupportedDepth_SaysWhyItReturnedTheInputUnchanged()
    {
        // 32 bpc is in range for the guard above but is not a depth this helper implements.
        // "No predictor" and "predictor present, not applied" must not look the same.
        byte[] input = new byte[16];
        for (int i = 0; i < input.Length; i++) input[i] = (byte)(0x37 + i);

        byte[] output = PredictorHelper.RemoveTiffPredictor(input, columns: 4, colors: 1,
            bitsPerComponent: 32);

        Assert.Same(input, output);
        Assert.NotNull(PredictorHelper.LastPredictorSkipReason);
        Assert.Contains("BitsPerComponent", PredictorHelper.LastPredictorSkipReason!);
    }

    [Fact]
    public void LastPredictorSkipReason_IsClearedByASubsequentAppliedPredictor()
    {
        // A slot a caller reads after the call must be reset at the START of the operation.
        PredictorHelper.RemoveTiffPredictor(new byte[16], columns: 4, colors: 1, bitsPerComponent: 32);
        Assert.NotNull(PredictorHelper.LastPredictorSkipReason);

        var rows = new[] { new[] { 1, 2, 3, 0 } };
        byte[] predicted = ApplyTiffPredictorSubByte(rows, colors: 1, bpc: 4, columns: 4);
        PredictorHelper.RemoveTiffPredictor(predicted, columns: 4, colors: 1, bitsPerComponent: 4);

        Assert.Null(PredictorHelper.LastPredictorSkipReason);
    }

    /// <summary>Applies TIFF horizontal differencing over sub-byte samples (forward direction).</summary>
    private static byte[] ApplyTiffPredictorSubByte(int[][] rows, int colors, int bpc, int columns)
    {
        int mask = (1 << bpc) - 1;
        int rowBytes = (columns * colors * bpc + 7) / 8;
        var outBytes = new byte[rowBytes * rows.Length];

        for (int r = 0; r < rows.Length; r++)
        {
            for (int s = 0; s < columns * colors; s++)
            {
                int diff = s >= colors ? (rows[r][s] - rows[r][s - colors]) & mask : rows[r][s] & mask;
                int bitPos = s * bpc;
                int idx = r * rowBytes + (bitPos >> 3);
                int shift = 8 - bpc - (bitPos & 7);
                outBytes[idx] = (byte)((outBytes[idx] & ~(mask << shift)) | (diff << shift));
            }
        }
        return outBytes;
    }

    private static int[][] ExpandSubByte(int[][] rows, int bpc, int columns) => rows;

    /// <summary>Unpacks sub-byte samples back into per-row arrays for comparison.</summary>
    private static int[][] ExpandSubByte(byte[] packed, int bpc, int columns, int rowCount)
    {
        int mask = (1 << bpc) - 1;
        int rowBytes = (columns * bpc + 7) / 8;
        var result = new int[rowCount][];
        for (int r = 0; r < rowCount; r++)
        {
            result[r] = new int[columns];
            for (int s = 0; s < columns; s++)
            {
                int bitPos = s * bpc;
                int idx = r * rowBytes + (bitPos >> 3);
                int shift = 8 - bpc - (bitPos & 7);
                result[r][s] = (packed[idx] >> shift) & mask;
            }
        }
        return result;
    }

    // ==================================================================
    //  Helpers
    // ==================================================================

    // --- TIFF predictor application (forward direction) ---

    private static byte[] ApplyTiffPredictor16(ushort[][] rows, int colors)
    {
        var outBytes = new List<byte>();
        foreach (var row in rows)
        {
            for (int s = 0; s < row.Length; s++)
            {
                int diff = s >= colors ? ((row[s] - row[s - colors]) & 0xFFFF) : row[s];
                outBytes.Add((byte)(diff >> 8));
                outBytes.Add((byte)(diff & 0xFF));
            }
        }
        return outBytes.ToArray();
    }

    private static byte[] ApplyTiffPredictor8(byte[][] rows, int colors)
    {
        var outBytes = new List<byte>();
        foreach (var row in rows)
        {
            for (int s = 0; s < row.Length; s++)
            {
                int diff = s >= colors ? ((row[s] - row[s - colors]) & 0xFF) : row[s];
                outBytes.Add((byte)diff);
            }
        }
        return outBytes.ToArray();
    }

    private static byte[] SamplesToBytes(ushort[][] rows)
    {
        var outBytes = new List<byte>();
        foreach (var row in rows)
            foreach (var v in row)
            {
                outBytes.Add((byte)(v >> 8));
                outBytes.Add((byte)(v & 0xFF));
            }
        return outBytes.ToArray();
    }

    private static byte[] Flatten(byte[][] rows)
    {
        var outBytes = new List<byte>();
        foreach (var row in rows) outBytes.AddRange(row);
        return outBytes.ToArray();
    }

    // --- Minimal LZW encoder matching LzwDecodeFilter's conventions ---
    // MSB-first bit packing, EarlyChange = 1, ClearTable=256, EndOfData=257,
    // initial 9-bit codes growing toward 12, a single ClearTable emitted at the
    // start.  Once the 4096-entry code space fills, the dictionary is FROZEN and
    // codes keep being emitted from it (no further ClearTable) — mirroring the
    // capped decoder exactly so the round-trip stays in sync.

    private static byte[] EncodeLzw(byte[] input)
    {
        const int ClearTable = 256;
        const int EndOfData = 257;
        const int EarlyChange = 1;

        var bitWriter = new BitWriter();
        var dict = new Dictionary<string, int>();

        void ResetDict()
        {
            dict.Clear();
            for (int i = 0; i < 256; i++)
                dict[((char)i).ToString()] = i;
        }

        int nextCode = 258;
        int codeSize = 9;
        ResetDict();
        bitWriter.Write(ClearTable, codeSize);

        if (input.Length == 0)
        {
            bitWriter.Write(EndOfData, codeSize);
            return bitWriter.ToArray();
        }

        string current = ((char)input[0]).ToString();
        for (int i = 1; i < input.Length; i++)
        {
            char c = (char)input[i];
            string combined = current + c;
            if (dict.ContainsKey(combined))
            {
                current = combined;
            }
            else
            {
                bitWriter.Write(dict[current], codeSize);

                // The decoder bumps codeSize when table.Count reaches
                // (1<<codeSize) - EarlyChange, after assigning the new entry.
                // Once the 4096-entry code space is full the dictionary freezes
                // (no ClearTable) — exactly what the capped decoder does.
                if (nextCode < (1 << 12))
                {
                    dict[combined] = nextCode++;
                    // The decoder's table lags one entry behind (its first code adds
                    // nothing), so it bumps codeSize one code later.  Compare against
                    // (nextCode - 1) to bump at the same point the decoder does.
                    if (nextCode - 1 >= (1 << codeSize) - EarlyChange && codeSize < 12)
                        codeSize++;
                }

                current = c.ToString();
            }
        }

        bitWriter.Write(dict[current], codeSize);
        bitWriter.Write(EndOfData, codeSize);
        return bitWriter.ToArray();
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new List<byte>();
        private int _bitBuffer;
        private int _bitCount;

        public void Write(int value, int numBits)
        {
            for (int i = numBits - 1; i >= 0; i--)
            {
                int bit = (value >> i) & 1;
                _bitBuffer = (_bitBuffer << 1) | bit;
                _bitCount++;
                if (_bitCount == 8)
                {
                    _bytes.Add((byte)_bitBuffer);
                    _bitBuffer = 0;
                    _bitCount = 0;
                }
            }
        }

        public byte[] ToArray()
        {
            if (_bitCount > 0)
            {
                _bitBuffer <<= (8 - _bitCount);
                _bytes.Add((byte)_bitBuffer);
                _bitCount = 0;
            }
            return _bytes.ToArray();
        }
    }
}
