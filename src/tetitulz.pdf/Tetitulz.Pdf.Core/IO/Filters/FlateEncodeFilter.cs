// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.IO.Compression;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// Encodes data using Flate (zlib/deflate) compression.
    /// </summary>
    public sealed class FlateEncodeFilter
    {
        /// <summary>
        /// Encodes <paramref name="data"/> using the specified deflate compression level.
        /// Returns a zlib-wrapped stream (2-byte CMF/FLG header + deflate body + Adler-32).
        /// </summary>
        public static byte[] Encode(byte[] data, CompressionLevel level = CompressionLevel.Optimal)
        {
            if (data == null || data.Length == 0)
                return data ?? new byte[0];

            using (var output = new MemoryStream())
            {
                // zlib header: CM=8 (deflate), CINFO=7 (32K window).
                // The second byte (FLG) must make the two-byte value divisible by 31.
                // 0x789C = default compression, 0x7801 = fastest, 0x78DA = best compression.
                byte cmf = 0x78;
                byte flg = level == CompressionLevel.Fastest ? (byte)0x01 : (byte)0x9C;
                output.WriteByte(cmf);
                output.WriteByte(flg);

                using (var deflate = new DeflateStream(output, level, leaveOpen: true))
                {
                    deflate.Write(data, 0, data.Length);
                }

                // Adler-32 checksum (big-endian)
                uint adler = ComputeAdler32(data, data.Length);
                output.WriteByte((byte)(adler >> 24));
                output.WriteByte((byte)(adler >> 16));
                output.WriteByte((byte)(adler >> 8));
                output.WriteByte((byte)(adler));

                return output.ToArray();
            }
        }

        /// <summary>
        /// The zlib Adler-32 (RFC 1950 §8.2) of the first <paramref name="count"/> bytes of
        /// <paramref name="data"/> — the trailer this encoder writes and
        /// <see cref="FlateDecodeFilter"/> checks, one implementation for both.
        /// </summary>
        /// <remarks>
        /// The two sums are reduced modulo 65521 once per <see cref="AdlerBlockBytes"/> bytes instead of
        /// after every byte, as zlib's own <c>adler32.c</c> does. It is the same checksum: the reduction
        /// is only ever needed to keep the sums inside 32 bits. Reducing after every byte cost two
        /// divisions a byte — several times the inflate whose output it checks.
        /// </remarks>
        internal static uint ComputeAdler32(byte[] data, int count)
        {
            const uint MOD_ADLER = 65521;
            uint a = 1, b = 0;
            int i = 0;
            while (i < count)
            {
                int blockEnd = count - i > AdlerBlockBytes ? i + AdlerBlockBytes : count;
                for (; i < blockEnd; i++)
                {
                    a += data[i];
                    b += a;
                }
                a %= MOD_ADLER;
                b %= MOD_ADLER;
            }
            return (b << 16) | a;
        }

        /// <summary>
        /// zlib's NMAX: the most bytes that can be summed before <c>b</c> could pass 2^32 − 1 — the
        /// largest n with 255·n(n+1)/2 + (n+1)(65521 − 1) ≤ 2^32 − 1, so even a run of 0xFF bytes,
        /// starting from sums just under the modulus, cannot overflow.
        /// </summary>
        internal const int AdlerBlockBytes = 5552;
    }
}
