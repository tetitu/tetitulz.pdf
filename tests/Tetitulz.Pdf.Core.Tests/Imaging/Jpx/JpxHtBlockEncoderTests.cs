// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.Core.Imaging.Jpx;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxHtBlockEncoder"/>: a single HT cleanup segment per code-block, read back by
/// <see cref="JpxHtBlockDecoder"/>, and byte for byte the segments <see cref="JpxHtBlockDecoderTests"/>
/// hard-codes — one worked through ITU-T T.814 clause 7 by hand, two written by the clause 7 writer
/// model in <c>tools/htj2k-fixtures/make_segments.py</c> and checked against its readers.
/// </summary>
public sealed class JpxHtBlockEncoderTests
{
    /// <summary>Decodes <paramref name="segment"/> as a w × h block whose cleanup codes down to plane 0, at <paramref name="mb"/> planes.</summary>
    private static int[] Decode(byte[] segment, int w, int h, int mb, out string problem)
    {
        var block = new JpxCodeBlock(new JpxRect(0, 0, w, h)) { Included = true, ZeroBitPlanes = mb - 1 };
        block.Add(new JpxCodeChunk(new JpxRange(0, segment.Length), 1, terminated: true));
        return JpxHtBlockDecoder.Decode(block, segment, JpxCodeBlockStyle.HighThroughput, mb, 0, out problem);
    }

    /// <summary>The decoder's form for a coefficient decoded to plane 0: twice the magnitude plus the half, signed; 0 stays 0.</summary>
    private static int Expected(long q) => q == 0 ? 0 : (int)(Math.Sign(q) * (2 * Math.Abs(q) + 1));

    private static long[] Single(int w, int h, int x, int y, long value)
    {
        var c = new long[w * h];
        c[y * w + x] = value;
        return c;
    }

    /// <summary>
    /// The hand-worked segment: a 2 × 8 block, one μ = 1 at (0, 4) — four context-0 quads, MEL symbols
    /// 0, 0, 1, 0 (0xDF), quad row 2's codeword, one MagSgn bit.
    /// </summary>
    [Fact]
    public void OneSample_IsTheHandWorkedSegment()
        => Assert.Equal(new byte[] { 0x00, 0xDF, 0x03, 0x00 }, JpxHtBlockEncoder.EncodeCleanup(Single(2, 8, 0, 4, 1), 2, 8, out _));

    /// <summary>
    /// A long MEL run: 85 symbols of 0 then a 1 walk MEL_k to its cap and fill the first MEL byte with
    /// ones, so the next holds seven bits.
    /// </summary>
    [Fact]
    public void ALongMelRun_IsTheModelsSegment()
        => Assert.Equal(new byte[] { 0x00, 0xFF, 0x7C, 0x0F, 0x05, 0x00 }, JpxHtBlockEncoder.EncodeCleanup(Single(2, 172, 0, 170, 1), 2, 172, out _));

    /// <summary>A wholly significant 2 × 4 block of μ = 1: context-driven codewords and one sign bit each.</summary>
    [Fact]
    public void AWhollySignificantBlock_IsTheModelsSegment()
        => Assert.Equal(new byte[] { 0x00, 0x7F, 0xD5, 0xB4, 0x00 }, JpxHtBlockEncoder.EncodeCleanup(Enumerable.Repeat(1L, 8).ToArray(), 2, 4, out _));

    /// <summary>
    /// Of the Annex C entries consistent with a quad, the one costing least once its known bits are
    /// counted — not merely the shortest codeword. The one case in the tables where the two differ:
    /// CxtVLC_table_0, context 4, ρ = 0xB, only sample 1 at the bound. The 7-bit codeword knowing all
    /// three samples' top bits makes a 5-byte segment; the 6-bit one knowing none would make 6.
    /// </summary>
    [Fact]
    public void TheCheapestConsistentEntry_IsChosen()
    {
        // A 4 × 2 block, one quad pair in the first row. Quad 0: only (1, 1), μ 1 — so quad 1's
        // context is 4 (its south-west neighbour). Quad 1: (2, 0) μ 1, (2, 1) μ 4, (3, 1) μ 2 — ρ 0xB,
        // exponents 1, 3, 2: U = 3 > κ = 1, and only sample 1's bit 2 is set.
        var coefficients = new long[] { 0, 0, 1, 0, 0, 1, 4, 2 };
        byte[] segment = JpxHtBlockEncoder.EncodeCleanup(coefficients, 4, 2, out _);
        Assert.Equal(5, segment.Length);
        Assert.Equal(coefficients.Select(Expected).ToArray(), Decode(segment, 4, 2, 4, out string problem));
        Assert.Null(problem);
    }

    /// <summary>
    /// The MEL coder's last run is closed with a 1 even when its bits end on a byte boundary, where the
    /// last byte's padding cannot close it and the decoder would read the VLC bytes as MEL. One column
    /// of quads, every one in context 0 — one significant quad at every position, followed by every
    /// length of trailing zeros — lands on that boundary for some of them; all decode.
    /// </summary>
    [Fact]
    public void TheLastMelRun_IsClosedWhereverItEnds()
    {
        for (int position = 0; position < 48; position++)
            for (int trailing = 1; trailing < 24; trailing++)
            {
                int h = 2 * (position + 1 + trailing);
                var coefficients = new long[2 * h];
                coefficients[(2 * position) * 2] = 1;
                byte[] segment = JpxHtBlockEncoder.EncodeCleanup(coefficients, 2, h, out _);
                int[] decoded = Decode(segment, 2, h, 2, out string problem);
                Assert.True(problem == null && decoded.SequenceEqual(coefficients.Select(Expected)),
                    $"a significant quad at {position} with {trailing} zero quads after it: {problem ?? "decoded differently"}");
            }
    }

    /// <summary>A block of zeros has no cleanup segment — no coding pass — and one coefficient makes one.</summary>
    [Fact]
    public void ABlockOfZeros_HasNoSegment()
    {
        Assert.Null(JpxHtBlockEncoder.EncodeCleanup(new long[16], 4, 4, out _));
        Assert.NotNull(JpxHtBlockEncoder.EncodeCleanup(Single(4, 4, 3, 3, -2), 4, 4, out _));
    }

    /// <summary>
    /// Random blocks, every shape the quad scan treats differently — odd widths and heights (a half
    /// quad pair, a half quad), one row of quads, tall, wide, the largest code-block — and every kind
    /// of content: sparse and dense, small and large magnitudes, both signs. Each decodes to exactly
    /// the coefficients it was made from.
    /// </summary>
    [Theory]
    [InlineData(1, 1, 1, 0.9, 3)]
    [InlineData(2, 2, 2, 0.5, 7)]
    [InlineData(3, 5, 3, 0.5, 100)]
    [InlineData(5, 3, 4, 0.8, 1)]
    [InlineData(4, 4, 5, 1.0, 1 << 12)]
    [InlineData(7, 9, 6, 0.3, 60)]
    [InlineData(16, 2, 7, 0.6, 1000)]
    [InlineData(2, 33, 8, 0.2, 5)]
    [InlineData(32, 32, 9, 0.05, 3)]
    [InlineData(32, 32, 10, 0.7, 1 << 16)]
    [InlineData(64, 64, 11, 0.4, 255)]
    [InlineData(64, 64, 12, 1.0, (1 << 20) - 1)]
    public void ARandomBlock_DecodesToItsCoefficients(int w, int h, int seed, double density, int maxMagnitude)
    {
        var random = new Random(seed);
        var coefficients = new long[w * h];
        for (int i = 0; i < coefficients.Length; i++)
        {
            if (random.NextDouble() >= density) continue;
            long m = 1 + random.Next(maxMagnitude);
            coefficients[i] = random.Next(2) == 0 ? m : -m;
        }
        if (coefficients.All(c => c == 0)) coefficients[0] = 1;

        byte[] segment = JpxHtBlockEncoder.EncodeCleanup(coefficients, w, h, out double distortion);
        int mb = 1;
        while ((maxMagnitude >> mb) != 0) mb++;
        int[] decoded = Decode(segment, w, h, mb + 1, out string problem);

        Assert.Null(problem);
        Assert.Equal(coefficients.Select(Expected).ToArray(), decoded);
        Assert.Equal(coefficients.Sum(c => (double)c * c), distortion);
    }

    /// <summary>
    /// The first line-pair's paired residual coding (§7.3.6) in all three of its forms — both residuals
    /// past 2 (a MEL 1), the first past 2 and the second small (one bit for the second), both small —
    /// and a residual long enough to need the suffix's extension (u ≥ 33).
    /// </summary>
    [Theory]
    [InlineData(40, 50)]
    [InlineData(40, 2)]
    [InlineData(3, 2)]
    [InlineData(1, 1)]
    [InlineData(1L << 40, 1L << 35)]
    public void TheFirstRowsResidualPair_RoundTrips(long a, long b)
    {
        // A 4 × 2 block: one row of two quads, each with two significant samples so neither κ nor the
        // predictor hides the residual.
        var coefficients = new long[] { a, -a, b, b, a, a, -b, b };
        byte[] segment = JpxHtBlockEncoder.EncodeCleanup(coefficients, 4, 2, out _);
        int mb = 1;
        while ((Math.Max(a, b) >> mb) != 0) mb++;
        int[] decoded;
        string problem;
        if (mb + 1 > JpxBlockDecoder.MaxNarrowBitPlanes)
        {
            var block = new JpxCodeBlock(new JpxRect(0, 0, 4, 2)) { Included = true, ZeroBitPlanes = mb };
            block.Add(new JpxCodeChunk(new JpxRange(0, segment.Length), 1, terminated: true));
            var wide = new long[8];
            new JpxHtBlockDecoder().Decode(block, segment, JpxCodeBlockStyle.HighThroughput, mb + 1, 0, wide, 0, 4, out problem);
            Assert.Null(problem);
            Assert.Equal(coefficients.Select(c => c == 0 ? 0 : Math.Sign(c) * (2 * Math.Abs(c) + 1)).ToArray(), wide);
            return;
        }
        decoded = Decode(segment, 4, 2, mb + 1, out problem);
        Assert.Null(problem);
        Assert.Equal(coefficients.Select(Expected).ToArray(), decoded);
    }
    /// <summary>
    /// The encoder's index of Annex C (<see cref="JpxHtBlockEncoder.EntryIndex"/>) holds, for every context, pattern
    /// and residual offset, exactly the entries a scan of the table accepts, in the table's order; and no two entries a
    /// quad could satisfy at once — the same key, known bits that agree where both are known — cost the same, so which
    /// of them comes first settles nothing (a reversed walk of a key's entries was a surviving mutation for that reason).
    /// </summary>
    [Fact]
    public void TheAnnexCIndex_HoldsEachKeysEntriesInTableOrder_AndNoQuadHasATie()
    {
        const int fields = JpxHtCodeTables.FieldsPerEntry;
        foreach (byte[] table in new[] { JpxHtCodeTables.Table0, JpxHtCodeTables.Table1 })
        {
            var index = new JpxHtBlockEncoder.EntryIndex(table);
            int indexed = 0;
            for (int context = 0; context < 8; context++)
                for (int rho = 0; rho < 16; rho++)
                    for (int uOff = 0; uOff < 2; uOff++)
                    {
                        int key = JpxHtBlockEncoder.EntryIndex.Key(context, rho, uOff);
                        int[] expected = Enumerable.Range(0, table.Length / fields).Select(n => n * fields)
                            .Where(i => table[i] == context && table[i + 1] == rho && table[i + 2] == uOff).ToArray();
                        Assert.Equal(expected, index.Entries.Skip(index.Start[key]).Take(index.Start[key + 1] - index.Start[key]).ToArray());
                        indexed += expected.Length;
                        for (int a = 0; a < expected.Length; a++)
                            for (int b = a + 1; b < expected.Length; b++)
                            {
                                int i = expected[a], j = expected[b];
                                if (((table[i + 4] ^ table[j + 4]) & table[i + 3] & table[j + 3]) != 0) continue;   // never both true of a quad
                                Assert.NotEqual(table[i + 6] - PopCount(table[i + 3]), table[j + 6] - PopCount(table[j + 3]));
                            }
                    }
            Assert.Equal(table.Length / fields, indexed);   // every entry under its own key, once
        }

        static int PopCount(int v) => Convert.ToString(v, 2).Count(ch => ch == '1');
    }
}
