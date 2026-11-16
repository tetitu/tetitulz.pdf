// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging.Jpx;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// The HT decoder's word-at-a-time readers against T.814 §7.1's procedures as printed —
/// importMagSgnBit and importVLCBit, a bit at a time, written here — on segments full of 0xFF and the bytes that
/// follow it, read in random widths past their ends: every value the same, and every problem raised at the same
/// read. The codeword lookup by a seven-bit peek against the bit-at-a-time walk of the table, and a reused
/// workspace — whose arrays are no longer cleared wholesale — against a fresh one.
/// </summary>
public sealed class JpxHtReaderTests
{
    private const string MagSgnStuffing = "a MagSgn byte after 0xFF has its stuffing bit set (§7.1.2)";
    private const string MagSgnPast = "the MagSgn bit-stream is read past its segment (§7.1.2)";
    private const string VlcPast = "the VLC bit-stream is read past the MagSgn bytes (§7.1.4)";

    /// <summary>importMagSgnBit (§7.1.2) as printed: a byte when the last is used up, seven bits after 0xFF.</summary>
    private sealed class MagSgnAsPrinted(byte[] data, int pcup)
    {
        private int _pos, _bits, _tmp, _last;
        internal string Error { get; private set; }

        internal int Bit()
        {
            if (_bits == 0)
            {
                _bits = _last == 0xFF ? 7 : 8;
                if (_pos < pcup)
                {
                    _tmp = data[_pos];
                    if ((_tmp & (1 << _bits)) != 0) Error ??= MagSgnStuffing;
                }
                else if (_pos == pcup) _tmp = 0xFF;
                else
                {
                    Error ??= MagSgnPast;
                    _tmp = 0;
                }
                _last = _tmp;
                _pos++;
            }
            int bit = _tmp & 1;
            _tmp >>= 1;
            _bits--;
            return bit;
        }
    }

    /// <summary>importVLCBit (§7.1.4) as printed: backwards from the second-last byte's high nibble (modDcup).</summary>
    private sealed class VlcAsPrinted
    {
        private readonly byte[] _data;
        private readonly int _pcup;
        private int _pos, _bits, _tmp, _last;
        internal string Error { get; private set; }

        internal VlcAsPrinted(byte[] data, int lcup, int pcup)
        {
            _data = data;
            _pcup = pcup;
            _pos = lcup - 3;
            _last = data[lcup - 2] | 0x0F;
            _tmp = _last >> 4;
            _bits = (_tmp & 7) < 7 ? 4 : 3;
        }

        internal int Bit()
        {
            if (_bits == 0)
            {
                if (_pos >= _pcup) _tmp = _data[_pos];
                else
                {
                    Error ??= VlcPast;
                    _tmp = 0;
                }
                _bits = 8;
                if (_last > 0x8F && (_tmp & 0x7F) == 0x7F) _bits = 7;
                _last = _tmp;
                _pos--;
            }
            int bit = _tmp & 1;
            _tmp >>= 1;
            _bits--;
            return bit;
        }
    }

    /// <summary>A segment rich in the cases the readers treat apart: 0xFF, a byte above 0x8F, a low seven bits of 1s, a set top bit after 0xFF.</summary>
    private static byte[] Segment(Random random, int length)
    {
        byte[] special = { 0xFF, 0xFF, 0x7F, 0xFF, 0x90, 0x8F, 0x80, 0x00, 0xF0 };
        var data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = random.Next(3) == 0 ? special[random.Next(special.Length)] : (byte)random.Next(256);
        return data;
    }

    /// <summary>
    /// MagSgn: random widths from 0 to 62 bits, on until well past the byte after P_cup — each read's value is the
    /// printed procedure's bits, least significant first, and the problem the reads have met is the same, both
    /// kinds (a set stuffing bit, a read past the segment), each first raised at the same read.
    /// </summary>
    [Fact]
    public void MagSgn_ReadsAsTheProcedurePrintedDoes_WordForWord()
    {
        int stuffings = 0, pasts = 0;
        for (int seed = 0; seed < 400; seed++)
        {
            var random = new Random(seed);
            byte[] data = Segment(random, random.Next(1, 40));
            int pcup = random.Next(0, data.Length + 1);
            var printed = new MagSgnAsPrinted(data, pcup);
            var reader = new JpxHtBlockDecoder.MagSgnReader();
            reader.Start(data, pcup);
            long total = 0, limit = 8L * (pcup + 4);
            while (total < limit)
            {
                int m = random.Next(4) == 0 ? random.Next(0, 63) : random.Next(0, 12);
                long expected = 0;
                for (int i = 0; i < m; i++) expected |= (long)printed.Bit() << i;
                Assert.Equal(expected, reader.Read(m));
                Assert.Equal(printed.Error, reader.Problem);
                total += m;
            }
            if (printed.Error == MagSgnStuffing) stuffings++;
            if (printed.Error == MagSgnPast) pasts++;
        }
        Assert.True(stuffings > 20 && pasts > 20, $"stuffing problems {stuffings}, past-the-end {pasts}: both must be met");
    }

    /// <summary>VLC: peeks consumed in part and reads of 1 to 32 bits, backwards past P_cup — values and problems as printed.</summary>
    [Fact]
    public void Vlc_ReadsAsTheProcedurePrintedDoes_WordForWord()
    {
        int pasts = 0, sevenBitBytes = 0;
        for (int seed = 0; seed < 400; seed++)
        {
            var random = new Random(seed);
            byte[] data = Segment(random, random.Next(2, 40));
            int lcup = data.Length, pcup = random.Next(0, lcup - 1);
            var printed = new VlcAsPrinted(data, lcup, pcup);
            var reader = new JpxHtBlockDecoder.VlcReader();
            reader.Start(data, lcup, pcup);
            long total = 0, limit = 8L * (lcup - pcup + 4);
            while (total < limit)
            {
                int n;
                int value;
                if (random.Next(2) == 0)
                {
                    n = random.Next(0, 8);
                    value = reader.Peek7() & ((1 << n) - 1);
                    reader.Skip(n);
                }
                else
                {
                    n = random.Next(0, 33);
                    value = reader.Read(n);
                }
                int expected = 0;
                for (int i = 0; i < n; i++) expected |= printed.Bit() << i;
                Assert.Equal(expected, value);
                Assert.Equal(printed.Error, reader.Problem);
                total += n;
            }
            if (printed.Error == VlcPast) pasts++;
            for (int i = pcup + 1; i < lcup - 2; i++) if (data[i] > 0x8F && (data[i - 1] & 0x7F) == 0x7F) sevenBitBytes++;
        }
        Assert.True(pasts > 100 && sevenBitBytes > 20, $"past-the-end {pasts}, seven-bit bytes {sevenBitBytes}: both must be met");
    }

    /// <summary>The seven-bit peek finds, for every context and every bit pattern, the codeword and length the bit-at-a-time walk of Annex C's lookup does.</summary>
    [Fact]
    public void ThePeekTable_FindsTheCodewordTheWalkDoes()
    {
        foreach (bool initial in new[] { true, false })
        {
            ushort[] lookup = JpxHtCodeTables.Lookup(initial);
            int[] peek = JpxHtCodeTables.Peek(initial);
            for (int context = 0; context < 8; context++)
                for (int bits = 0; bits < 128; bits++)
                {
                    int cwd = bits & 1, length = 1, entry;
                    while ((entry = lookup[context * 256 + ((1 << length) | cwd)]) == 0)
                    {
                        cwd |= ((bits >> length) & 1) << length;
                        length++;
                    }
                    Assert.Equal((entry, length), (peek[context * 128 + bits] & 0xFFFF, peek[context * 128 + bits] >> 16));
                }
        }
    }

    /// <summary>
    /// One workspace through a sequence of blocks — large and small, dense and sparse, cleanup alone and with one or
    /// two refinement passes, vertically causal or not — decodes each as a fresh workspace does: what the cleanup
    /// pass no longer clears, it writes before anything reads it.
    /// </summary>
    [Fact]
    public void AReusedWorkspace_DecodesEachBlockAsAFreshOneDoes()
    {
        const int mb = 12;
        var workspace = new JpxHtBlockDecoder();
        int refined = 0;
        for (int seed = 0; seed < 60; seed++)
        {
            var random = new Random(seed);
            int w = random.Next(1, 65), h = random.Next(1, 65);
            long[] q = JpxHtRefinementEncoderTests.RandomBlock(w, h, seed, random.Next(1, 3000), random.NextDouble());
            long max = q.Max(v => Math.Abs(v));
            if (max == 0) continue;
            int planes = 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)max);
            int plane = random.Next(0, planes), passes = plane == 0 ? 1 : random.Next(1, 4);
            bool causal = random.Next(2) == 0;
            JpxHtSet set = JpxHtRefinementEncoder.EncodeSet(q, w, h, plane, passes, causal);
            if (set == null) continue;

            byte[] data = set.Data();
            var block = new JpxCodeBlock(new JpxRect(0, 0, w, h)) { Included = true, ZeroBitPlanes = mb - 1 - set.Plane };
            block.Add(new JpxCodeChunk(new JpxRange(0, set.Cleanup.Length), 1, terminated: true));
            if (set.Passes > 1)
                block.Add(new JpxCodeChunk(new JpxRange(set.Cleanup.Length, data.Length), set.Passes - 1, terminated: set.Passes == 3));
            JpxCodeBlockStyle style = JpxCodeBlockStyle.HighThroughput | (causal ? JpxCodeBlockStyle.VerticallyCausal : 0);

            int[] fresh = JpxHtBlockDecoder.Decode(block, data, style, mb, 0, out string problem);
            Assert.Null(problem);
            var reused = new int[w * h];
            workspace.Decode(block, data, style, mb, 0, reused, 0, w, out problem);
            Assert.Null(problem);
            Assert.Equal(fresh, reused);
            if (set.Passes > 1) refined++;
        }
        Assert.True(refined > 10, $"{refined} blocks with refinement passes");
    }
}
