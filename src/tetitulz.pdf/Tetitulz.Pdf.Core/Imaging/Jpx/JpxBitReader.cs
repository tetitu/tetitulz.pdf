// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// Reads packet-header bits (T.800 §B.10.1): MSB first, and after a byte of 0xFF the next byte
    /// carries only seven bits, its most significant bit being a stuffed zero. <see cref="Align"/> ends
    /// a header: it discards the rest of the current byte and, when that byte was 0xFF, the stuffed
    /// byte the encoder had to emit after it.
    /// </summary>
    internal sealed class JpxBitReader
    {
        private readonly byte[] _data;
        private readonly long _end;
        private long _pos;
        private int _buffer;
        private int _bitsLeft;
        private bool _lastWasFF;

        internal JpxBitReader(byte[] data, long start, long end)
        {
            _data = data;
            _pos = start;
            _end = end;
        }

        /// <summary>The next byte to be read; after <see cref="Align"/>, where the header ended.</summary>
        internal long Position => _pos;

        /// <summary>False when the data ends before the bit — the header is cut short.</summary>
        internal bool TryReadBit(out int bit)
        {
            if (_bitsLeft == 0)
            {
                if (_pos >= _end)
                {
                    bit = 0;
                    return false;
                }
                int b = _data[_pos++];
                if (_lastWasFF)
                {
                    _buffer = b & 0x7F;   // the stuffed zero in the MSB is skipped
                    _bitsLeft = 7;
                }
                else
                {
                    _buffer = b;
                    _bitsLeft = 8;
                }
                _lastWasFF = b == 0xFF;
            }
            _bitsLeft--;
            bit = (_buffer >> _bitsLeft) & 1;
            return true;
        }

        /// <summary><paramref name="count"/> bits (at most 31), MSB first.</summary>
        internal bool TryReadBits(int count, out int value)
        {
            value = 0;
            for (int i = 0; i < count; i++)
            {
                if (!TryReadBit(out int bit)) return false;
                value = (value << 1) | bit;
            }
            return true;
        }

        /// <summary>
        /// Ends the header at a byte boundary and returns the position after it, consuming the stuffed
        /// byte that must follow a final 0xFF ("the last byte in the packet header shall not be 0xFF").
        /// </summary>
        internal long Align()
        {
            _bitsLeft = 0;
            if (_lastWasFF)
            {
                if (_pos < _end) _pos++;
                _lastWasFF = false;
            }
            return _pos;
        }

        /// <summary>
        /// At a byte boundary: skips a two-byte marker (an EPH after a header) when it is next, and
        /// says whether it was.
        /// </summary>
        internal bool TrySkipMarker(int marker)
        {
            if (_bitsLeft != 0 || _pos + 2 > _end) return false;
            if (_data[_pos] != (byte)(marker >> 8) || _data[_pos + 1] != (byte)marker) return false;
            _pos += 2;
            _lastWasFF = false;
            return true;
        }
    }
}
