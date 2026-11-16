// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// Writes packet-header bits (T.800 §B.10.1), the mirror of <see cref="JpxBitReader"/>: MSB
    /// first; after a byte of 0xFF the next byte carries seven bits under a stuffed zero; and
    /// <see cref="Finish"/> pads the last byte and, when that byte is 0xFF, emits the stuffed byte
    /// after it — "the last byte in the packet header shall not be an 0xFF value".
    /// </summary>
    internal sealed class JpxBitWriter
    {
        private readonly List<byte> _bytes = new List<byte>();
        private int _buffer, _count, _capacity = 8;

        /// <summary>Bits written so far, stuffed zeros not counted.</summary>
        internal int BitCount { get; private set; }

        internal void WriteBit(int bit)
        {
            _buffer = (_buffer << 1) | (bit & 1);
            BitCount++;
            if (++_count == _capacity) Emit();
        }

        /// <summary><paramref name="count"/> bits of <paramref name="value"/> (at most 31), MSB first.</summary>
        internal void WriteBits(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--) WriteBit((value >> i) & 1);
        }

        /// <summary>The header's bytes, the last one padded to the byte boundary.</summary>
        internal byte[] Finish()
        {
            if (_count > 0)
            {
                _buffer <<= _capacity - _count;
                Emit();
            }
            if (_capacity == 7)
            {
                _bytes.Add(0);   // the header ended on 0xFF: its stuffed byte must follow
                _capacity = 8;
            }
            return _bytes.ToArray();
        }

        private void Emit()
        {
            var b = (byte)_buffer;
            _bytes.Add(b);
            _capacity = b == 0xFF ? 7 : 8;
            _buffer = 0;
            _count = 0;
        }
    }
}
