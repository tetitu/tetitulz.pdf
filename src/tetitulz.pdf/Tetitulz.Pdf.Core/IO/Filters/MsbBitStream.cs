// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// An MSB-first bit reader over a <c>byte[]</c> — the bit order every PDF image filter uses,
    /// and the one implementation for the filters in this folder.
    /// </summary>
    /// <remarks>
    /// <para><b>One reader for CCITT and JBIG2</b> (<c>CcittFaxDecoder</c>, <c>Jbig2Decoder</c>):
    /// the same fields, <see cref="Eof"/> and <see cref="ReadBit"/>, and each format's additions —
    /// CCITT the peek and the seek, JBIG2 the multi-bit read. They are one coherent abstraction, so
    /// the union lives here rather than the intersection.</para>
    ///
    /// <para><b>The two multi-bit reads are deliberately not the same method.</b>
    /// <see cref="ReadBits"/> <i>consumes</i> and treats end-of-data as zero bits;
    /// <see cref="PeekBits"/> does not consume, caps at 24 bits and zero-fills past the end. A
    /// caller that confused them would decode silently wrong rather than fail, so they keep the
    /// names their decoders already used.</para>
    ///
    /// <para><b>Deliberately NOT merged with <see cref="Core.IO.MsbBitReader"/>, which is one
    /// namespace up and reads the same bit order.</b> That one is <i>stateless and positional</i>:
    /// every call takes an absolute <c>long</c> bit offset, because ISO 32000-1 Table 38 lets
    /// <c>/BitsPerSample</c> reach 32 and the function evaluator indexes a sample table at
    /// arbitrary offsets. This one is a <i>cursor</i> — it carries a position, seeks, aligns and
    /// reports EOF as -1, which is what a sequential Huffman decoder needs. Folding either into the
    /// other would give every caller of one an API shaped for the other's problem.</para>
    ///
    /// <para><b>The name matters because of that.</b> This type was first written as a second
    /// <c>MsbBitReader</c> in this namespace, which — <c>Filters</c> being nested inside <c>IO</c> —
    /// silently <b>shadowed</b> the positional one for every file in this folder, including
    /// <c>LzwDecodeFilter</c>, which forwards to it. The compiler caught it; a shadowing that
    /// happened to be call-compatible would not have been caught at all.</para>
    ///
    /// <para><b>No static fields</b> — a leaf, like the other shared helpers this project keeps in
    /// the filter and rendering rings.</para>
    /// </remarks>
    internal sealed class MsbBitStream
    {
        private readonly byte[] _data;
        private int _bytePos;
        private int _bitPos;   // 0 = MSB of current byte, 7 = LSB

        /// <summary>
        /// Reads <paramref name="data"/> from <paramref name="startOffset"/> bytes in. A null
        /// buffer is treated as empty, so every read reports end-of-data rather than throwing.
        /// </summary>
        internal MsbBitStream(byte[] data, int startOffset = 0)
        {
            _data = data ?? Array.Empty<byte>();
            _bytePos = Math.Max(0, startOffset);
        }

        internal int TotalBits => _data.Length * 8;
        internal int Position => _bytePos * 8 + _bitPos;
        internal bool Eof => _bytePos >= _data.Length;

        /// <summary>Returns the next bit (0/1) or -1 when exhausted.</summary>
        internal int ReadBit()
        {
            if (_bytePos >= _data.Length) return -1;
            int bit = (_data[_bytePos] >> (7 - _bitPos)) & 1;
            _bitPos++;
            if (_bitPos == 8) { _bitPos = 0; _bytePos++; }
            return bit;
        }

        /// <summary>
        /// Consumes <paramref name="count"/> bits, MSB first. Bits past the end of the data read
        /// as 0 rather than terminating, which is what lets a Huffman decoder run off the end of a
        /// truncated segment and fail on the resulting code instead of on the read.
        /// </summary>
        internal int ReadBits(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                int bit = ReadBit();
                if (bit < 0) bit = 0;
                value = (value << 1) | bit;
            }
            return value;
        }

        /// <summary>Peek up to 24 bits without consuming. Missing bits read as 0.</summary>
        internal int PeekBits(int count)
        {
            if (count < 0 || count > 24) throw new ArgumentOutOfRangeException(nameof(count));
            int bytePos = _bytePos, bitPos = _bitPos;
            int result = 0;
            for (int i = 0; i < count; i++)
            {
                if (bytePos >= _data.Length)
                {
                    result <<= 1;  // zero-fill past EOF
                }
                else
                {
                    int bit = (_data[bytePos] >> (7 - bitPos)) & 1;
                    result = (result << 1) | bit;
                    bitPos++;
                    if (bitPos == 8) { bitPos = 0; bytePos++; }
                }
            }
            return result;
        }

        internal void SkipBits(int count)
        {
            // Only forward skips are supported here; use SetPosition for an
            // absolute (possibly backward) seek. A negative count would yield
            // a negative _bitPos via C# truncating division and corrupt reads.
            if (count <= 0) return;
            int total = _bitPos + count;
            _bytePos += total / 8;
            _bitPos  = total % 8;
            if (_bytePos > _data.Length) { _bytePos = _data.Length; _bitPos = 0; }
        }

        /// <summary>Absolute seek to a bit position (clamped to the stream).</summary>
        internal void SetPosition(int absoluteBitPos)
        {
            if (absoluteBitPos < 0) absoluteBitPos = 0;
            _bytePos = absoluteBitPos / 8;
            _bitPos  = absoluteBitPos % 8;
            if (_bytePos > _data.Length) { _bytePos = _data.Length; _bitPos = 0; }
        }

        /// <summary>Advance to the next byte boundary (no-op if already aligned).</summary>
        internal void AlignToByte()
        {
            if (_bitPos != 0) { _bitPos = 0; _bytePos++; }
        }

        /// <summary>
        /// Reads <paramref name="count"/> whole bytes and advances past them. The caller must be
        /// byte-aligned (<see cref="AlignToByte"/> first) — this does not re-align, because a
        /// silent re-alignment would hide a caller bug that skipped bits it should have consumed
        /// (or vice versa). Clamps to the data actually remaining, like <see cref="ReadBits"/>
        /// treats a short read as zeros rather than throwing.
        /// </summary>
        /// <remarks>
        /// The count is the file's — a JBIG2 collective bitmap's BMSIZE, a refinement's size — so the
        /// clamp is taken in <c>long</c>: in <c>int</c>, a count near <see cref="int.MaxValue"/> past
        /// the first byte wrapped the sum negative, passed the clamp, and allocated two gigabytes.
        /// </remarks>
        internal byte[] ReadAlignedBytes(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if ((long)_bytePos + count > _data.Length)
                count = Math.Max(0, _data.Length - _bytePos);
            var result = new byte[count];
            Buffer.BlockCopy(_data, _bytePos, result, 0, count);
            _bytePos += count;
            return result;
        }
    }
}
