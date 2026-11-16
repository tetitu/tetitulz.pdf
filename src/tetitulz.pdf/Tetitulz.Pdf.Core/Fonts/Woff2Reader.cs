// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// A forward cursor over WOFF 2.0 bytes, with the format's two variable-length integer
    /// encodings.
    /// </summary>
    /// <remarks>
    /// <para><b>Every read is bounds-checked and reports by returning <c>false</c>.</b> The whole
    /// container is attacker-supplied — a web font inside a document — and every length in it is a
    /// claim, not a fact. A reader that threw would push a <c>try</c> around each of the fifty-odd
    /// reads in the decoder; one that returned a default would make "the field was 0" and "the field
    /// was not there" the same answer, which is how a truncated font becomes a plausible-looking
    /// one.</para>
    ///
    /// <para><b>The two encodings are not interchangeable and the spec uses both.</b>
    /// <see cref="TryReadBase128"/> is big-endian base-128 with a continuation bit, used for the
    /// table lengths in the directory; <see cref="TryRead255UInt16"/> is the byte-oriented scheme
    /// used inside the glyph substreams. Reading one with the other's routine silently succeeds on
    /// small values, which is the worst possible failure mode for a length.</para>
    /// </remarks>
    internal sealed class Woff2Reader
    {
        private readonly byte[] _data;

        internal Woff2Reader(byte[] data, int offset = 0)
        {
            _data = data ?? Array.Empty<byte>();
            Position = offset;
        }

        /// <summary>The cursor, in bytes from the start of the buffer.</summary>
        internal int Position { get; set; }

        /// <summary>Bytes left after <see cref="Position"/>.</summary>
        internal int Remaining => _data.Length - Position;

        internal bool TryReadU8(out int value)
        {
            value = 0;
            if (Remaining < 1) return false;
            value = _data[Position++];
            return true;
        }

        internal bool TryReadU16(out int value)
        {
            value = 0;
            if (Remaining < 2) return false;
            value = (_data[Position] << 8) | _data[Position + 1];
            Position += 2;
            return true;
        }

        /// <summary>A signed 16-bit big-endian value, for contour counts and bounding boxes.</summary>
        internal bool TryReadS16(out int value)
        {
            if (!TryReadU16(out value)) return false;
            if (value > short.MaxValue) value -= 65536;
            return true;
        }

        internal bool TryReadU32(out uint value)
        {
            value = 0;
            if (Remaining < 4) return false;
            value = ((uint)_data[Position] << 24) | ((uint)_data[Position + 1] << 16)
                    | ((uint)_data[Position + 2] << 8) | _data[Position + 3];
            Position += 4;
            return true;
        }

        internal bool TryReadBytes(int count, out byte[] value)
        {
            value = null;
            if (count < 0 || Remaining < count) return false;
            value = new byte[count];
            Buffer.BlockCopy(_data, Position, value, 0, count);
            Position += count;
            return true;
        }

        /// <summary>Advances without copying; <c>false</c> when there are not that many bytes.</summary>
        internal bool TrySkip(int count)
        {
            if (count < 0 || Remaining < count) return false;
            Position += count;
            return true;
        }

        /// <summary>
        /// UIntBase128 (WOFF2 §4.1): big-endian, seven bits per byte, high bit continues.
        /// </summary>
        /// <remarks>
        /// <para><b>The spec's three refusals are all implemented, and each is a real attack.</b>
        /// More than five bytes would overflow 32 bits; a leading <c>0x80</c> is a redundant zero
        /// prefix, which makes one value encodable many ways and so makes two readers disagree about
        /// whether two fonts are the same; and a sixth bit set on the first byte overflows. The
        /// reference decoder rejects all three, and a decoder that merely truncated would produce a
        /// length no producer intended.</para>
        /// </remarks>
        internal bool TryReadBase128(out uint value)
        {
            value = 0;
            uint accumulator = 0;

            for (int i = 0; i < 5; i++)
            {
                if (!TryReadU8(out int b)) return false;

                // A leading 0x80 is a zero with a continuation bit: no canonical encoding starts
                // that way.
                if (i == 0 && b == 0x80) return false;

                // Would overflow 32 bits on the shift below.
                if ((accumulator & 0xFE000000u) != 0) return false;

                accumulator = (accumulator << 7) | (uint)(b & 0x7F);
                if ((b & 0x80) == 0)
                {
                    value = accumulator;
                    return true;
                }
            }

            // Five bytes carrying a continuation bit: the value cannot fit.
            return false;
        }

        /// <summary>
        /// 255UInt16 (WOFF2 §4.2): one byte for most values, two or three for the rest.
        /// </summary>
        /// <remarks>
        /// <c>253</c> introduces a plain big-endian 16-bit value; <c>255</c> a byte plus 253;
        /// <c>254</c> a byte plus 506; anything else is itself. The result is a 16-bit quantity, so
        /// it is returned as an <see cref="int"/> already masked — a caller that treated an
        /// overflowing sum as a larger number would allocate on a value the format cannot express.
        /// </remarks>
        internal bool TryRead255UInt16(out int value)
        {
            value = 0;
            if (!TryReadU8(out int code)) return false;

            const int OneMoreByteCode1 = 255;
            const int OneMoreByteCode2 = 254;
            const int WordCode = 253;
            const int LowestUCode = 253;

            if (code == WordCode)
            {
                if (!TryReadU16(out value)) return false;
                return true;
            }

            if (code == OneMoreByteCode1)
            {
                if (!TryReadU8(out int b)) return false;
                value = b + LowestUCode;
                return true;
            }

            if (code == OneMoreByteCode2)
            {
                if (!TryReadU8(out int b)) return false;
                value = b + LowestUCode * 2;
                return true;
            }

            value = code;
            return true;
        }
    }
}
