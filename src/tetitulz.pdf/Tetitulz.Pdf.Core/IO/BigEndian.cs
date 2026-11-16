// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;

namespace Tetitulz.Pdf.Core.IO
{
    /// <summary>
    /// Big-endian (network order) 16- and 32-bit integers, read from and written to a byte array, a
    /// stream or a growing list — the byte order of every sfnt, CFF, WOFF, ICC, JPEG 2000 and JBIG2
    /// structure this library reads or writes.
    /// </summary>
    /// <remarks>
    /// <para><b>One implementation</b> for every font, ICC, JPEG 2000 and JBIG2 reader and writer,
    /// in each type a caller wants (<c>int</c>, <c>uint</c>, <c>long</c>) — private pairs agree
    /// only while nothing checks them. The local names stay, as forwarders, so each site still reads as the format it parses. A
    /// format whose byte order the file chooses (TIFF) or that is little-endian (GIF) answers a
    /// different question and keeps its own.</para>
    /// <para><c>System.Buffers.Binary.BinaryPrimitives</c> is the platform's answer and is not
    /// available to Core's netstandard2.0 leg without a <c>System.Memory</c> reference.</para>
    /// <para>The four byte-array members are <b>public</b> because <c>Plugins.PostScript</c> assembles
    /// a Type 42 font's program from its tables, and a plugin cannot see Core's internals.</para>
    /// </remarks>
    public static class BigEndian
    {
        /// <summary>The unsigned 16-bit value at <paramref name="at"/>.</summary>
        /// <param name="d">The bytes.</param>
        /// <param name="at">The offset of the first byte.</param>
        public static int U16(byte[] d, long at) => (d[at] << 8) | d[at + 1];

        /// <summary>The unsigned 32-bit value at <paramref name="at"/>.</summary>
        /// <param name="d">The bytes.</param>
        /// <param name="at">The offset of the first byte.</param>
        public static uint U32(byte[] d, long at)
            => ((uint)d[at] << 24) | ((uint)d[at + 1] << 16) | ((uint)d[at + 2] << 8) | d[at + 3];

        /// <summary>The low 16 bits of <paramref name="v"/> at <paramref name="at"/>.</summary>
        /// <param name="d">The bytes.</param>
        /// <param name="at">The offset of the first byte.</param>
        /// <param name="v">The value.</param>
        public static void Put16(byte[] d, long at, int v)
        {
            d[at] = (byte)(v >> 8);
            d[at + 1] = (byte)v;
        }

        /// <summary><paramref name="v"/> at <paramref name="at"/>.</summary>
        /// <param name="d">The bytes.</param>
        /// <param name="at">The offset of the first byte.</param>
        /// <param name="v">The value.</param>
        public static void Put32(byte[] d, long at, uint v)
        {
            d[at] = (byte)(v >> 24);
            d[at + 1] = (byte)(v >> 16);
            d[at + 2] = (byte)(v >> 8);
            d[at + 3] = (byte)v;
        }

        /// <summary>The low 16 bits of <paramref name="v"/>, written to <paramref name="s"/>.</summary>
        internal static void Write16(Stream s, int v)
        {
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }

        /// <summary><paramref name="v"/>, written to <paramref name="s"/>.</summary>
        internal static void Write32(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24));
            s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }

        /// <summary>The low 16 bits of <paramref name="v"/>, appended to <paramref name="to"/>.</summary>
        internal static void Add16(List<byte> to, int v)
        {
            to.Add((byte)(v >> 8));
            to.Add((byte)v);
        }

        /// <summary><paramref name="v"/>, appended to <paramref name="to"/>.</summary>
        internal static void Add32(List<byte> to, uint v)
        {
            to.Add((byte)(v >> 24));
            to.Add((byte)(v >> 16));
            to.Add((byte)(v >> 8));
            to.Add((byte)v);
        }
    }
}
