// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// Reads the raw (arithmetic-coder bypass) segments of T.800 §D.6: bits MSB first; after a byte
    /// of 0xFF the next byte carries seven bits, its stuffed MSB skipped; and past the segment's end,
    /// or at a byte above 0x8F after 0xFF (a marker), ones — the 0xFF padding the decoder appends
    /// (§D.4.1, §D.6 NOTE 2).
    /// </summary>
    internal sealed class JpxRawReader
    {
        private byte[] _data;
        private int _pos, _end, _c, _ct;

        internal JpxRawReader(byte[] data) => _data = data ?? Array.Empty<byte>();

        /// <summary>Starts the segment <c>[start, end)</c> of the reader's buffer.</summary>
        internal void Start(int start, int end) => Start(_data, start, end);

        /// <summary>Starts the segment <c>[start, end)</c> of <paramref name="data"/>, which the reader then reads from; bytes outside it are never read.</summary>
        internal void Start(byte[] data, int start, int end)
        {
            _data = data ?? Array.Empty<byte>();
            _end = Math.Min(Math.Max(0, end), _data.Length);
            _pos = Math.Min(Math.Max(0, start), _end);
            _c = 0;
            _ct = 0;
        }

        internal int Bit()
        {
            if (_ct == 0)
            {
                if (_c == 0xFF)
                {
                    if (_pos >= _end || MqDecoder.EndsSegment(_data[_pos]))
                    {
                        _c = 0xFF;
                        _ct = 8;
                    }
                    else
                    {
                        _c = _data[_pos++];
                        _ct = 7;
                    }
                }
                else
                {
                    _c = _pos < _end ? _data[_pos++] : 0xFF;
                    _ct = 8;
                }
            }
            _ct--;
            return (_c >> _ct) & 1;
        }
    }
}
