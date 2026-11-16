// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// Writes bits most-significant-first into a growable buffer — the mirror of
    /// <see cref="MsbBitStream"/>, and the one bit writer in the tree.
    /// </summary>
    /// <remarks>
    /// <para><b>Three producers pack bits MSB-first and they must agree with one reader.</b> CCITT G4
    /// and LZW both write codes whose widths are not byte multiples, and both are read back by
    /// <see cref="MsbBitStream"/>-shaped logic. A second copy of "write <c>n</c> bits" is a second
    /// place for the bit order to be decided, and bit order is the kind of mistake that produces
    /// output which decodes to <i>something</i> — so it survives a smoke test and fails on real
    /// data.</para>
    ///
    /// <para><b>Public for the third producer, which is in a plugin.</b> ISO 32000-1 Annex F
    /// linearization hint tables are "a bit stream, high-order bit first, … subdivided into fields
    /// of arbitrary width without regard to byte boundaries" (F.4), and
    /// <c>Plugins.Maintenance.PdfLinearizer</c> writes them. Core names no plugin in its
    /// <c>InternalsVisibleTo</c> list, so the shared code goes public rather than the reference
    /// going sideways — the same reason <see cref="Tetitulz.Pdf.Core.Fonts.SfntBuilder"/> is public.</para>
    ///
    /// <para><b>Unused bits in the final byte are zero.</b> Both formats tolerate that: T.6 reads
    /// them as fill bits, and an LZW decoder has already seen the end-of-data code by then. A
    /// caller that needs a different pad has to say so, and none does.</para>
    ///
    /// <para><b>Doubling growth, so the total copy cost stays linear in the output size.</b> The
    /// ceiling is <see cref="int.MaxValue"/> and passing it throws rather than silently truncating:
    /// a truncated codestream is a valid-looking one that decodes to the wrong thing.</para>
    /// </remarks>
    public sealed class MsbBitWriter
    {
        private byte[] _data;
        private int _byteCount;    // bytes touched, including a partially filled last one
        private int _bitsInLast;   // 0 = byte aligned, else bits already used in _data[_byteCount-1]

        /// <param name="capacity">
        /// An initial guess at the output size. Undersized is a performance question, not a
        /// correctness one; a floor of 16 keeps the doubling sane for tiny inputs.
        /// </param>
        public MsbBitWriter(int capacity)
        {
            _data = new byte[capacity < 16 ? 16 : capacity];
        }

        /// <summary>Bits written so far.</summary>
        public long BitCount => (long)(_byteCount == 0 ? 0 : _byteCount - 1) * 8
                                  + (_bitsInLast == 0 ? (_byteCount == 0 ? 0 : 8) : _bitsInLast);

        public void WriteBit(int bit)
        {
            if (_bitsInLast == 0)
            {
                if (_byteCount == _data.Length) Grow();
                _data[_byteCount] = 0;
                _byteCount++;
            }

            if (bit != 0) _data[_byteCount - 1] |= (byte)(0x80 >> _bitsInLast);
            _bitsInLast = (_bitsInLast + 1) & 7;
        }

        /// <summary>Writes the low <paramref name="count"/> bits of <paramref name="value"/>.</summary>
        /// <remarks>
        /// <paramref name="value"/> is a <see cref="long"/> because Annex F's hint tables carry
        /// unsigned 32-bit byte offsets, and the top bit of one of those is the sign bit of an
        /// <see cref="int"/>. Every existing caller passes an <see cref="int"/> and converts
        /// implicitly.
        /// </remarks>
        public void WriteBits(long value, int count)
        {
            for (int i = count - 1; i >= 0; i--) WriteBit((int)((value >> i) & 1));
        }

        /// <summary>
        /// Pads with zero bits up to the next byte boundary; a no-op when already aligned.
        /// </summary>
        /// <remarks>
        /// Annex F's hint tables are the caller that needs this: each group of like items ends on a
        /// byte boundary even though the items themselves do not, and a reader that skips to the
        /// next byte after each group reads garbage from a stream written without the padding.
        /// </remarks>
        public void AlignToByte()
        {
            while (_bitsInLast != 0) WriteBit(0);
        }

        /// <summary>
        /// The codestream. Any unused bits in the final byte are already zero.
        /// </summary>
        public byte[] ToArray()
        {
            var result = new byte[_byteCount];
            Buffer.BlockCopy(_data, 0, result, 0, _byteCount);
            return result;
        }

        private void Grow()
        {
            long next = (long)_data.Length * 2;
            if (next > int.MaxValue) next = int.MaxValue;
            if (next <= _data.Length)
                throw new InvalidOperationException("Bit writer output exceeds 2 GB.");

            var bigger = new byte[(int)next];
            Buffer.BlockCopy(_data, 0, bigger, 0, _byteCount);
            _data = bigger;
        }
    }
}
