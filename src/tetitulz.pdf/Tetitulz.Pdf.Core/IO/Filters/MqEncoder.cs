// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// The MQ arithmetic <b>encoder</b> of ITU-T T.88 Annex E, shared by JBIG2 and JPEG 2000
    /// (ITU-T T.800 Annex C) as <see cref="MqDecoder"/> is — the mirror of that decoder.
    /// </summary>
    /// <remarks>
    /// <para><b>A prefix of a codeword segment decodes its first passes exactly, and
    /// <see cref="PrefixLengths"/> says how long the prefix must be.</b> JPEG 2000 lets a layer
    /// carry the first bytes of a code-block's segment and a rate allocator drop the rest; the
    /// decoder pads what it has with <c>0xFF</c> (T.800 D.4.1). Every decision through a coding
    /// pass is decoded correctly by any code value inside the interval the coder held at the end
    /// of that pass — the intervals nest — so the prefix must be long enough that the padded value
    /// (the prefix followed by ones, the largest value with that prefix) still lies below the
    /// interval's upper end. <see cref="Mark"/> records the interval after a pass; the lengths are
    /// computed once the segment is complete, against its final bytes, since a later carry can
    /// still raise the byte the coder was on.</para>
    ///
    /// <para><b>It reads the decoder's own state tables rather than carrying a copy.</b> Qe, NMPS,
    /// NLPS and SWITCH are 47 rows of specification data that the two halves must agree on
    /// exactly; two copies would agree until one of them was corrected, and the failure mode is a
    /// stream that only this library can read.</para>
    ///
    /// <para><b>The first output byte is discarded, and that is the specification's own shape.</b>
    /// E.3.7's BYTEOUT increments the pointer <i>before</i> it writes, so the byte the encoder
    /// starts on is never assigned — implementations either start one before the buffer or, as
    /// here, keep a dummy and drop it. Emitting it would prefix every stream with a zero.</para>
    ///
    /// <para><b>FLUSH's trailing <c>FF AC</c> is kept</b> by <see cref="Finish"/>. The decoder's
    /// BYTEIN treats <c>0xFF</c> followed by a byte above <c>0x8F</c> as end-of-data and pads from
    /// there, so the marker is what tells it the stream ended rather than ran out.</para>
    ///
    /// <para><b>JPEG 2000 terminates the same coder differently</b> (T.800 C.2.9, Figure C.11):
    /// the same SETBITS and two BYTEOUTs, then a final <c>0xFF</c> is discarded and no marker is
    /// written, because a codeword segment's length is signalled in the packet header and the
    /// decoder synthesises the <c>0xFF</c> padding itself (D.4.1). <see cref="FinishCodewordSegment"/>
    /// is that termination; it is the one other difference between the two standards' coders.</para>
    /// </remarks>
    internal sealed class MqEncoder
    {
        // Copied once from the decoder's tables rather than read through IReadOnlyList per
        // decision: same single source, no indirection in the inner loop.
        private static readonly int[] Qe = Copy(MqDecoder.QeTable);
        private static readonly int[] Nmps = Copy(MqDecoder.NmpsTable);
        private static readonly int[] Nlps = Copy(MqDecoder.NlpsTable);
        private static readonly int[] Swch = Copy(MqDecoder.SwitchTable);

        private readonly int[] _i;
        private readonly int[] _mps;
        private readonly List<byte> _out = new List<byte>(1024);

        private long _c;
        private int _a = 0x8000;
        private int _ct = 12;
        private int _bp;

        /// <param name="contextCount">Number of distinct contexts the caller will use.</param>
        internal MqEncoder(int contextCount)
        {
            _i = new int[contextCount];
            _mps = new int[contextCount];

            // BP starts on a byte BYTEOUT never assigns; it is dropped by Finish.
            _out.Add(0);
            _bp = 0;
        }

        /// <summary>
        /// Starts a context at probability state <paramref name="index"/> with <paramref name="mps"/>
        /// (T.800 Table D.7 starts three of JPEG 2000's contexts away from state 0).
        /// </summary>
        internal void SetState(int context, int index, int mps)
        {
            _i[context] = index;
            _mps[context] = mps;
        }

        /// <summary>
        /// The coder's interval at the end of a coding pass, taken by <see cref="Mark"/> for
        /// <see cref="PrefixLengths"/>: how many bytes were out, the byte the coder was on (a
        /// later carry may raise it by one; nothing earlier changes again), and the C, A and CT
        /// registers. The interval is [C, C + A) in units of C's bit 0; after CT more shifts, bit
        /// 27 of C is the carry into that last byte's least significant bit.
        /// </summary>
        internal readonly struct Checkpoint
        {
            internal Checkpoint(int bytes, byte last, long c, int a, int ct)
            {
                Bytes = bytes;
                Last = last;
                C = c;
                A = a;
                CT = ct;
            }

            /// <summary>Bytes emitted so far, the discarded first byte not counted: the coder is on segment byte <c>Bytes − 1</c> (−1 is the discarded one).</summary>
            internal int Bytes { get; }
            internal byte Last { get; }
            internal long C { get; }
            internal int A { get; }
            internal int CT { get; }
        }

        /// <summary>The interval now — after a coding pass, before the next one's first decision.</summary>
        internal Checkpoint Mark() => new Checkpoint(_bp, _out[_bp], _c, _a, _ct);

        /// <summary>
        /// For each checkpoint, in order, the shortest prefix of <paramref name="segment"/> — the
        /// bytes <see cref="FinishCodewordSegment"/> returned for the same coding — from which a
        /// decoder that pads with <c>0xFF</c> reproduces every decision made up to it. The lengths
        /// never decrease, and the last is at most the segment's length.
        /// </summary>
        /// <remarks>
        /// The padded value is the prefix followed by ones from the bit after it: a decoder's
        /// BYTEIN reads the end as a marker and adds <c>0xFF00</c> at every eighth shift, whether or
        /// not the prefix ends in <c>0xFF</c> (which is why a prefix may drop a trailing <c>0xFF</c>,
        /// and why bytes after one carry seven bits, one bit higher). The check is
        /// <c>C &lt; prefix + ones ≤ C + A</c> — the lower bound matters where the byte after an
        /// <c>0xFF</c> carries a set top bit, a carry the <c>0xFF</c> could not take, so the true
        /// value is above the prefix's ones — in a frame where the checkpoint's last byte's least
        /// significant bit weighs 2<sup>32</sup>: the checkpoint's own C and A, and up to four more
        /// bytes of the segment, fit a <c>long</c> there, and four are always enough — the fourth
        /// byte's least significant bit is finer than the interval's grid, and any prefix below
        /// C + A on that grid leaves room for the ones after it.
        /// </remarks>
        internal static int[] PrefixLengths(IReadOnlyList<Checkpoint> marks, byte[] segment)
        {
            int n = segment.Length;
            var pos = new int[n + 1];   // each byte's most significant bit, in bits from byte 0's
            for (int i = 0; i < n; i++) pos[i + 1] = pos[i] + (segment[i] == 0xFF ? 7 : 8);

            var lengths = new int[marks.Count];
            for (int k = 0; k < marks.Count; k++)
            {
                Checkpoint m = marks[k];
                int j = m.Bytes - 1;
                int p = j < 0 ? -8 : pos[j];
                long lower = ((long)m.Last << 32) + (m.C << (m.CT + 5));
                long upper = ((long)m.Last << 32) + ((m.C + m.A) << (m.CT + 5));
                long prefix = 0;
                int length = j < 0 ? 0 : j;
                while (true)
                {
                    // The padded value must lie in [C, C + A). Below the top it usually is, and above the
                    // bottom it usually is too, the prefix's ones bounding any continuation — except where the
                    // byte after an 0xFF has its stuffed top bit set: that bit is a carry the 0xFF could not
                    // take, the true value sits above the prefix and its ones, and the padded value can fall
                    // below C. Then more bytes are needed.
                    long ones = length == 0 ? 1L << (40 + p) : 1L << (32 + p - pos[length - 1]);
                    if ((prefix + ones <= upper && prefix + ones > lower) || length == n) break;
                    prefix += (long)segment[length] << (32 + p - pos[length]);
                    length++;
                }
                // A prefix ending in 0xFF pads to the same value without it (its ones are the 0xFF's bits
                // and the ones after), and no segment should end in one (B.10.7).
                if (length > 0 && length < n && segment[length - 1] == 0xFF) length--;
                lengths[k] = length;
            }
            return lengths;
        }

        /// <summary>Encodes one binary decision in the given context.</summary>
        internal void Encode(int context, int bit)
        {
            if (bit == _mps[context]) CodeMps(context);
            else CodeLps(context);
        }

        private void CodeMps(int cx)
        {
            int qe = Qe[_i[cx]];
            _a -= qe;

            if ((_a & 0x8000) != 0)
            {
                _c += qe;
                return;
            }

            // The interval became smaller than the LPS sub-interval, so the two swap roles.
            if (_a < qe) _a = qe;
            else _c += qe;

            _i[cx] = Nmps[_i[cx]];
            Renormalise();
        }

        private void CodeLps(int cx)
        {
            int qe = Qe[_i[cx]];
            _a -= qe;

            if (_a < qe) _c += qe;
            else _a = qe;

            if (Swch[_i[cx]] == 1) _mps[cx] = 1 - _mps[cx];
            _i[cx] = Nlps[_i[cx]];
            Renormalise();
        }

        private void Renormalise()
        {
            do
            {
                if (_ct == 0) ByteOut();
                _a <<= 1;
                _c <<= 1;
                _ct--;
            }
            while ((_a & 0x8000) == 0);

            if (_ct == 0) ByteOut();
        }

        /// <summary>T.88 E.3.7, including the bit-stuffing after an <c>0xFF</c>.</summary>
        private void ByteOut()
        {
            if (_out[_bp] == 0xFF)
            {
                Stuff();
                return;
            }

            if (_c < 0x8000000)
            {
                NoStuff();
                return;
            }

            // The carry propagates into the byte already written.
            _out[_bp] = (byte)(_out[_bp] + 1);
            if (_out[_bp] == 0xFF)
            {
                _c &= 0x7FFFFFF;
                Stuff();
                return;
            }

            NoStuff();
        }

        private void NoStuff()
        {
            _bp++;
            _out.Add((byte)(_c >> 19));
            _c &= 0x7FFFF;
            _ct = 8;
        }

        private void Stuff()
        {
            _bp++;
            _out.Add((byte)(_c >> 20));
            _c &= 0xFFFFF;
            _ct = 7;
        }

        /// <summary>T.88 E.3.8 FLUSH, and the encoded bytes: the flushed registers, then the <c>FF AC</c> marker.</summary>
        internal byte[] Finish()
        {
            FlushRegisters();

            if (_out[_bp] != 0xFF)
            {
                _bp++;
                _out.Add(0xFF);
            }
            _bp++;
            _out.Add(0xAC);
            return Emitted();
        }

        /// <summary>
        /// T.800 C.2.9 FLUSH, and the codeword segment: the flushed registers with a final
        /// <c>0xFF</c> discarded and no marker appended. The segment never ends in <c>0xFF</c>
        /// (B.10.7), and its length is what the packet header signals.
        /// </summary>
        internal byte[] FinishCodewordSegment()
        {
            FlushRegisters();

            if (_out[_bp] == 0xFF)
            {
                _out.RemoveAt(_bp);
                _bp--;
            }
            return Emitted();
        }

        /// <summary>SETBITS, then the two BYTEOUTs that empty the code register — the part of FLUSH both standards share.</summary>
        private void FlushRegisters()
        {
            SetBits();

            _c <<= _ct;
            ByteOut();
            _c <<= _ct;
            ByteOut();
        }

        /// <summary>The bytes written so far, without index 0 — the byte BYTEOUT never assigned.</summary>
        private byte[] Emitted()
        {
            var result = new byte[_out.Count - 1];
            _out.CopyTo(1, result, 0, result.Length);
            return result;
        }

        /// <summary>T.88 E.3.8 SETBITS: makes the final code word as long as it needs to be.</summary>
        private void SetBits()
        {
            long temp = _c + _a;
            _c |= 0xFFFF;
            if (_c >= temp) _c -= 0x8000;
        }

        private static int[] Copy(IReadOnlyList<int> source)
        {
            var copy = new int[source.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = source[i];
            return copy;
        }
    }
}
