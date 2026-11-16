// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// Wraps a bare Type 1 font program in PFB sector headers.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a PDF's <c>/FontFile</c> is not a file.</b> §9.9 stores a Type 1 program as the
    /// three raw segments — clear text, <c>eexec</c>-encrypted binary, and the trailing zeros — with
    /// their lengths in <c>/Length1</c>, <c>/Length2</c> and <c>/Length3</c>. A <c>.pfb</c> file
    /// interleaves those same segments with six-byte sector headers, and every font tool reads the
    /// latter. Handing a caller the stream's decoded bytes therefore produces a file nothing opens.
    /// </para>
    ///
    /// <para><b>A leaf — no static fields, so no type initializer</b>, which is why it can live in
    /// Core and be called from both the renderer's font loader and the exporter. It was
    /// <c>private</c> on <c>FontCache</c>, a type that <i>does</i> have a static initializer, and
    /// CLAUDE.md records a sideways reference to one of those crashing the test host mid-run.</para>
    /// </remarks>
    public static class PfbAssembler
    {
        /// <summary>Sector marker byte that opens every PFB segment header.</summary>
        private const byte Marker = 0x80;

        /// <summary>Segment types: 1 = ASCII text, 2 = binary, 3 = end of file.</summary>
        private const byte Ascii = 1, Binary = 2, EndOfFile = 3;

        /// <summary>
        /// Builds a <c>.pfb</c> from the three segments of a PDF <c>/FontFile</c> stream.
        /// </summary>
        /// <param name="raw">The decoded <c>/FontFile</c> bytes.</param>
        /// <param name="length1">Length of the clear-text segment (<c>/Length1</c>).</param>
        /// <param name="length2">Length of the encrypted segment (<c>/Length2</c>).</param>
        /// <param name="length3">Length of the trailing segment (<c>/Length3</c>); 0 when absent.</param>
        /// <returns>The PFB bytes, or <c>null</c> when the lengths do not describe the data.</returns>
        /// <remarks>
        /// Returns null rather than throwing on inconsistent lengths: a producer that writes a wrong
        /// <c>/Length2</c> is common enough that the renderer treats it as "try the next approach",
        /// and an exporter needs the same answer rather than an exception out of a batch.
        /// <para><b>The sums are taken in <c>long</c>.</b> In <c>int</c>, <c>/Length1 2147483647
        /// /Length2 1</c> overflowed to a negative total that passed the bound, and the copy then
        /// threw out of the renderer's font loader and the exporter alike — from a file whose only
        /// fault is two numbers.</para>
        /// </remarks>
        public static byte[] Build(byte[] raw, int length1, int length2, int length3)
        {
            if (raw == null || length1 <= 0 || length2 <= 0) return null;
            if ((long)length1 + length2 > raw.Length) return null;
            if (length3 < 0 || (long)length1 + length2 + length3 > raw.Length) length3 = 0;

            int capacity = 6 + length1 + 6 + length2 + (length3 > 0 ? 6 + length3 : 0) + 2;
            using var ms = new MemoryStream(capacity);

            WriteSector(ms, Ascii, raw, 0, length1);
            WriteSector(ms, Binary, raw, length1, length2);
            if (length3 > 0) WriteSector(ms, Ascii, raw, length1 + length2, length3);

            ms.WriteByte(Marker);
            ms.WriteByte(EndOfFile);
            return ms.ToArray();
        }

        private static void WriteSector(MemoryStream ms, byte type, byte[] src, int offset, int length)
        {
            ms.WriteByte(Marker);
            ms.WriteByte(type);
            ms.WriteByte((byte)(length & 0xFF));
            ms.WriteByte((byte)((length >> 8) & 0xFF));
            ms.WriteByte((byte)((length >> 16) & 0xFF));
            ms.WriteByte((byte)((length >> 24) & 0xFF));
            ms.Write(src, offset, length);
        }
    }
}
