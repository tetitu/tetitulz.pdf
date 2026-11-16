// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// <see cref="MqDecoder"/> against T.88 §E.3's procedures written as printed — INITDEC, DECODE with
/// LPS_EXCHANGE and MPS_EXCHANGE, RENORMD one bit at a time, BYTEIN with the marker rule — over the
/// four tables alone. The decoder renormalises several bits at once and reads its next state from
/// tables unrolled over the packed states; both must be the procedure's result, decision for
/// decision and state for state.
/// </summary>
public class MqDecoderProcedureTests
{
    private const int Contexts = 19;

    /// <summary>
    /// 400 segments, each rich in 0xFF and the bytes either side of the marker threshold, decoded
    /// through both of the decoder's entry points in turn, from random starting states, with a
    /// restart into a second segment half-way: every decision and every context state the
    /// procedure's.
    /// </summary>
    [Fact]
    public void Decode_DecidesAsTheProcedurePrinted_DecisionForDecision()
    {
        var random = new Random(4021);
        long lps = 0, readPartWay = 0;
        for (int trial = 0; trial < 400; trial++)
        {
            byte[] data = Segment(random, random.Next(0, 48));
            int split = random.Next(0, data.Length + 1);
            int[] start = new int[Contexts];
            for (int i = 0; i < Contexts; i++) start[i] = random.Next(0, 2 * 47);

            var mq = new MqDecoder(data, 0, split, Contexts);
            Array.Copy(start, mq.ContextStates, Contexts);
            var reference = new Procedure(data, 0, split, start);

            int decisions = random.Next(50, 700);
            for (int n = 0; n < decisions; n++)
            {
                if (n == decisions / 2)
                {
                    mq.Restart(split, data.Length);
                    reference.Restart(split, data.Length);
                }
                int cx = random.Next(Contexts);
                int expected = reference.Decode(cx, out bool wasLps, out int byteReadsPartWay);
                int actual;
                if ((n & 1) == 0) actual = mq.Decode(cx);
                else
                {
                    MqRegisters r = mq.Registers;
                    actual = mq.Decode(ref r, cx);
                    mq.Registers = r;
                }
                Assert.True(expected == actual, $"trial {trial}, decision {n}: the procedure decided {expected}, the decoder {actual}");
                Assert.True(reference.PackedState(cx) == mq.ContextStates[cx], $"trial {trial}, decision {n}: context {cx} is in state {reference.PackedState(cx)} by the procedure, {mq.ContextStates[cx]} in the decoder");
                if (wasLps) lps++;
                if (byteReadsPartWay > 0) readPartWay++;
            }
            for (int i = 0; i < Contexts; i++) Assert.Equal(reference.PackedState(i), mq.ContextStates[i]);
        }
        // Potency: the paths that differ from the procedure's shape were taken, many times.
        Assert.True(lps > 10_000, $"only {lps} LPS decisions");
        Assert.True(readPartWay > 1_000, $"only {readPartWay} renormalisations read a byte part-way");
    }

    /// <summary>A segment of <paramref name="length"/> bytes, a third of them 0xFF and many of the rest at the marker threshold's edges.</summary>
    private static byte[] Segment(Random random, int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            int pick = random.Next(9);
            data[i] = pick < 3 ? (byte)0xFF
                : pick == 3 ? (byte)0x8F
                : pick == 4 ? (byte)0x90
                : pick == 5 ? (byte)0x00
                : (byte)random.Next(256);
        }
        return data;
    }

    /// <summary>T.88 §E.3 as printed, its context states as an index and an MPS, over <see cref="MqDecoder"/>'s four tables.</summary>
    private sealed class Procedure
    {
        private readonly byte[] _data;
        private readonly int[] _index = new int[Contexts], _mps = new int[Contexts];
        private int _pos, _end, _a, _ct;
        private uint _c;
        private byte _b;

        internal Procedure(byte[] data, int start, int end, int[] packedStates)
        {
            _data = data;
            for (int i = 0; i < Contexts; i++)
            {
                _index[i] = packedStates[i] >> 1;
                _mps[i] = packedStates[i] & 1;
            }
            Restart(start, end);
        }

        internal int PackedState(int cx) => (_index[cx] << 1) | _mps[cx];

        internal void Restart(int start, int end)
        {
            _end = end;
            _pos = start;
            // INITDEC
            _b = Next();
            _c = (uint)_b << 16;
            ByteIn();
            _c <<= 7;
            _ct -= 7;
            _a = 0x8000;
        }

        private byte Next() => _pos < _end ? _data[_pos++] : (byte)0xFF;

        private void ByteIn()
        {
            if (_b == 0xFF)
            {
                byte b1 = _pos < _end ? _data[_pos] : (byte)0xFF;
                if (b1 > 0x8F)
                {
                    _c += 0xFF00;
                    _ct = 8;
                }
                else
                {
                    _pos++;
                    _b = b1;
                    _c += (uint)_b << 9;
                    _ct = 7;
                }
            }
            else
            {
                _b = Next();
                _c += (uint)_b << 8;
                _ct = 8;
            }
        }

        internal int Decode(int cx, out bool wasLps, out int byteReadsPartWay)
        {
            int i = _index[cx], mps = _mps[cx];
            int qe = MqDecoder.QeTable[i];
            int d;
            byteReadsPartWay = 0;
            _a -= qe;
            if ((_c >> 16) < (uint)qe)
            {
                // LPS_EXCHANGE
                if (_a < qe)
                {
                    _a = qe;
                    d = mps;
                    _index[cx] = MqDecoder.NmpsTable[i];
                }
                else
                {
                    _a = qe;
                    d = 1 - mps;
                    if (MqDecoder.SwitchTable[i] == 1) _mps[cx] = 1 - mps;
                    _index[cx] = MqDecoder.NlpsTable[i];
                }
                byteReadsPartWay = RenormD();
            }
            else
            {
                _c -= (uint)qe << 16;
                if ((_a & 0x8000) == 0)
                {
                    // MPS_EXCHANGE
                    if (_a < qe)
                    {
                        d = 1 - mps;
                        if (MqDecoder.SwitchTable[i] == 1) _mps[cx] = 1 - mps;
                        _index[cx] = MqDecoder.NlpsTable[i];
                    }
                    else
                    {
                        d = mps;
                        _index[cx] = MqDecoder.NmpsTable[i];
                    }
                    byteReadsPartWay = RenormD();
                }
                else d = mps;
            }
            wasLps = d != mps;
            return d;
        }

        /// <summary>RENORMD; the BYTEINs it made after its first shift — where the decoder's several-bit shift is split.</summary>
        private int RenormD()
        {
            int shifts = 0, partWay = 0;
            do
            {
                if (_ct == 0)
                {
                    ByteIn();
                    if (shifts > 0) partWay++;
                }
                _a <<= 1;
                _c <<= 1;
                _ct--;
                shifts++;
            }
            while ((_a & 0x8000) == 0);
            return partWay;
        }
    }
}
