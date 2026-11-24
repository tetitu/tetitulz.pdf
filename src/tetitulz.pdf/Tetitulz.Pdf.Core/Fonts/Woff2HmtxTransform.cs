// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// Reverses the WOFF 2.0 §5.4 <c>hmtx</c> transform, rebuilding the left side bearings from the
    /// glyph bounding boxes.
    /// </summary>
    /// <remarks>
    /// <para><b>What the transform exploits.</b> A TrueType glyph designed to the recommendations
    /// has its left side bearing equal to the <c>xMin</c> of its bounding box, so an encoder that
    /// finds this true for every glyph can drop the array and let the decoder read it back out of
    /// <c>glyf</c>. Two arrays can go independently: <c>lsb[]</c> for the proportional run and
    /// <c>leftSideBearing[]</c> for the monospaced tail, flagged by bits 0 and 1.</para>
    ///
    /// <para><b>An empty glyph's bearing is zero, not its <c>xMin</c>.</b> An empty glyph has no
    /// bounding box — the transform is only applicable when such a glyph's stored bearing was
    /// already zero — so reading four bytes of "bbox" from a zero-length glyph record would take
    /// whatever the next glyph starts with. That case is the one this file exists to get right, and
    /// it is settled in <see cref="ReadXMins"/>: <b>one producer, so there is no second place to
    /// get it wrong</b>. A second test at the point of use would be inert — the two agree by
    /// construction.</para>
    ///
    /// <para><b>Refuses by returning <see langword="null"/>.</b> §5.4 requires a decoder to reject
    /// the file when the flags are invalid, and "invalid" includes the shape that would otherwise
    /// pass silently: neither array eliminated, which is a transform that transforms nothing.</para>
    /// </remarks>
    internal static class Woff2HmtxTransform
    {
        /// <summary>Flags bit 0: the proportional <c>lsb[]</c> array is absent.</summary>
        private const int LsbAbsent = 0x01;

        /// <summary>Flags bit 1: the monospaced <c>leftSideBearing[]</c> array is absent.</summary>
        private const int LeftSideBearingAbsent = 0x02;

        /// <summary>
        /// The reconstructed <c>hmtx</c>, or <see langword="null"/> when the transformed table or
        /// the flags are invalid.
        /// </summary>
        /// <param name="transformed">The transformed <c>hmtx</c> as it left Brotli.</param>
        /// <param name="numGlyphs">From <c>maxp</c>.</param>
        /// <param name="numberOfHMetrics">From <c>hhea</c>.</param>
        /// <param name="xMins">
        /// Each glyph's <c>xMin</c>, from <see cref="ReadXMins"/> — which is where "an empty glyph
        /// has none, so its bearing is zero" is decided.
        /// </param>
        internal static byte[] Rebuild(byte[] transformed, int numGlyphs, int numberOfHMetrics,
                                       int[] xMins)
        {
            if (transformed == null || xMins == null) return null;
            if (numberOfHMetrics <= 0 || numberOfHMetrics > numGlyphs) return null;
            if (xMins.Length != numGlyphs) return null;

            var reader = new Woff2Reader(transformed);
            if (!reader.TryReadU8(out int flags)) return null;

            // §5.4: bits 2-7 are reserved and MUST be zero, and a transform that eliminates neither
            // array is not a transform.
            if ((flags & ~(LsbAbsent | LeftSideBearingAbsent)) != 0) return null;
            if ((flags & (LsbAbsent | LeftSideBearingAbsent)) == 0) return null;

            var advances = new int[numberOfHMetrics];
            for (int i = 0; i < numberOfHMetrics; i++)
                if (!reader.TryReadU16(out advances[i])) return null;

            int[] lsb = ReadOrDerive(reader, (flags & LsbAbsent) != 0, 0, numberOfHMetrics, xMins);
            if (lsb == null) return null;

            int trailing = numGlyphs - numberOfHMetrics;
            int[] trailingBearings = ReadOrDerive(
                reader, (flags & LeftSideBearingAbsent) != 0, numberOfHMetrics, trailing, xMins);
            if (trailingBearings == null) return null;

            // Anything left over means the declared arrays did not account for the table, which is
            // the same class of error as a short one and is not something to shrug at.
            if (reader.Remaining != 0) return null;

            var ms = new MemoryStream();
            for (int i = 0; i < numberOfHMetrics; i++)
            {
                WriteU16(ms, advances[i]);
                WriteU16(ms, lsb[i] & 0xFFFF);
            }
            foreach (int b in trailingBearings) WriteU16(ms, b & 0xFFFF);

            return ms.ToArray();
        }

        /// <summary>
        /// <paramref name="count"/> bearings, either read from the stream or taken from the glyph
        /// bounding boxes.
        /// </summary>
        /// <remarks>
        /// No range checks here: <see cref="Rebuild"/> has already made <paramref name="count"/>
        /// non-negative (<c>numberOfHMetrics &lt;= numGlyphs</c>) and every glyph index a valid one
        /// (<c>xMins.Length == numGlyphs</c>), so a guard here could not fire.
        /// </remarks>
        private static int[] ReadOrDerive(Woff2Reader reader, bool absent, int firstGlyph, int count,
                                          int[] xMins)
        {
            var values = new int[count];

            for (int i = 0; i < count; i++)
            {
                if (absent)
                {
                    values[i] = xMins[firstGlyph + i];
                }
                else if (!reader.TryReadS16(out values[i]))
                {
                    return null;
                }
            }

            return values;
        }

        /// <summary>
        /// Each glyph's <c>xMin</c>, read out of a reconstructed <c>glyf</c> through its
        /// <c>loca</c> — and <b>zero for an empty glyph</b>, which has no bounding box.
        /// </summary>
        /// <remarks>
        /// <b>Read from the reconstructed tables, not from the transformed ones.</b> The bounding
        /// box of a glyph whose box was inferred rather than stored exists only after the glyph is
        /// rebuilt — so this has to run downstream of <see cref="Woff2GlyfTransform"/>, and the
        /// §5.5 ordering rule (<c>loca</c> follows <c>glyf</c>) is what makes that possible in one
        /// pass over the directory.
        /// </remarks>
        internal static int[] ReadXMins(byte[] glyf, byte[] loca, int numGlyphs, int indexFormat)
        {
            if (glyf == null || loca == null || numGlyphs < 0) return null;

            int entrySize = indexFormat == 0 ? 2 : 4;
            if (loca.Length < (numGlyphs + 1) * entrySize) return null;

            var result = new int[numGlyphs];

            for (int g = 0; g < numGlyphs; g++)
            {
                long start = LocaAt(loca, g, indexFormat);
                long end = LocaAt(loca, g + 1, indexFormat);

                // A zero-length record is an EMPTY glyph: no contours, no bounding box, and §5.4
                // makes the hmtx transform applicable to such a font only when its stored bearing
                // was zero. So zero is the answer — reading four bytes of "bbox" from a record that
                // has none would take whatever the next glyph starts with, and produce a plausible
                // wrong number rather than a failure. This is the ONLY place that decides it.
                if (end <= start) { result[g] = 0; continue; }

                // numberOfContours is the first Int16; xMin the second.
                if (start + 4 > glyf.Length) return null;
                int xMin = (glyf[start + 2] << 8) | glyf[start + 3];
                if (xMin > short.MaxValue) xMin -= 65536;
                result[g] = xMin;
            }

            return result;
        }

        private static long LocaAt(byte[] loca, int index, int indexFormat)
            => indexFormat == 0
                ? ((loca[index * 2] << 8) | loca[index * 2 + 1]) * 2L
                : ((long)loca[index * 4] << 24) | ((long)loca[index * 4 + 1] << 16)
                  | ((long)loca[index * 4 + 2] << 8) | loca[index * 4 + 3];

        private static void WriteU16(Stream s, int value) => IO.BigEndian.Write16(s, value);
    }
}
