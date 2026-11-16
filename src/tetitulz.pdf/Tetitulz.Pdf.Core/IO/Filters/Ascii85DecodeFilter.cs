// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// §7.4.3 ASCII85Decode.
    /// </summary>
    /// <remarks>
    /// <para><b>This filter expands, and therefore honours
    /// <see cref="StreamFilter.MaxOutputBytes"/>.</b> A full group is five characters producing four
    /// bytes, which contracts — but <c>z</c> is <i>one</i> character producing four bytes, so a
    /// stream of nothing but <c>z</c> is a 4x amplification. It was grouped with ASCIIHexDecode as a
    /// filter that "cannot outgrow its input by enough to matter" and consulted no ceiling at all,
    /// so 256 MiB of <c>z</c> produced 1 GiB past a 512 MiB limit that never fired, and a caller
    /// passing a smaller ceiling of its own was overrun by 4x in silence — no exception, just a
    /// bigger buffer than it asked for.</para>
    /// </remarks>
    public sealed class Ascii85DecodeFilter : StreamFilter
    {
        private static readonly long[] Pow85 = { 85L * 85 * 85 * 85, 85L * 85 * 85, 85L * 85, 85, 1 };

        public override byte[] Decode(byte[] data, PdfDictionary parameters)
        {
            long maxOutputBytes = MaxOutputBytes;

            // ASCII85: 5 encoded chars → 4 decoded bytes; estimate output capacity.
            // Compute in long so the hint can't overflow to a negative int (which
            // List<byte>(capacity) rejects with ArgumentOutOfRangeException) on
            // inputs above ~536 MB.
            var output = new List<byte>((int)Math.Min(int.MaxValue, (long)data.Length * 4 / 5));
            var group = new List<byte>(5);

            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];

                // End of data
                if (b == (byte)'~')
                {
                    // Expect '>' after '~'
                    break;
                }

                // Skip whitespace
                if (PdfConstants.IsWhitespace(b))
                    continue;

                // 'z' represents four zero bytes
                if (b == (byte)'z')
                {
                    if (group.Count != 0)
                    {
                        // 'z' in the middle of a group is malformed; skip it (lenient).
                        continue;
                    }
                    // The one expanding path in this filter: one input byte, four output bytes.
                    if (output.Count + 4L > maxOutputBytes)
                        throw new InvalidDataException(
                            $"ASCII85Decode: output exceeded {maxOutputBytes} bytes");

                    output.Add(0);
                    output.Add(0);
                    output.Add(0);
                    output.Add(0);
                    continue;
                }

                if (b < (byte)'!' || b > (byte)'u')
                    continue; // Skip invalid characters (lenient).

                group.Add(b);

                if (group.Count == 5)
                {
                    DecodeGroup(group, output, 5, maxOutputBytes);
                    group.Clear();
                }
            }

            // Handle partial final group
            if (group.Count > 1)
            {
                // Save the original count BEFORE padding so DecodeGroup emits the right
                // number of output bytes (N input chars → N-1 output bytes).
                int originalCount = group.Count;
                // Pad with 'u' (85-1+33=117) to a full group of 5
                while (group.Count < 5)
                    group.Add((byte)'u');
                DecodeGroup(group, output, originalCount, maxOutputBytes);
            }
            // group.Count == 1: malformed (can't produce a byte); ignore (lenient).

            return output.ToArray();
        }

        private static void DecodeGroup(List<byte> group, List<byte> output, int originalCount,
                                       long maxOutputBytes)
        {
            long value = 0;
            for (int i = 0; i < 5; i++)
            {
                value += (group[i] - 33) * Pow85[i];
            }

            if (value > 0xFFFFFFFFL)
                return; // Overflow: malformed group — skip it (lenient).

            int outputCount = originalCount == 5 ? 4 : originalCount - 1;

            // A full group contracts 5 chars to 4 bytes, so this can only breach a ceiling the 'z'
            // path already pushed close to it. Checked anyway: the ceiling is a bound on the buffer
            // handed back, not on any one path's contribution to it.
            if (output.Count + (long)outputCount > maxOutputBytes)
                throw new InvalidDataException(
                    $"ASCII85Decode: output exceeded {maxOutputBytes} bytes");

            // The group's bytes most significant first, written straight out: a four-byte array
            // a group was 32 bytes of garbage (on a 64-bit runtime) for each four bytes of output.
            for (int i = 0; i < outputCount; i++)
            {
                output.Add((byte)(value >> (24 - 8 * i)));
            }
        }
    }
}
