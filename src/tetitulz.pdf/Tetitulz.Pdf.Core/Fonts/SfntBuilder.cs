// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// Assembles an sfnt container (TrueType or OpenType/CFF) from a set of tables: the header,
    /// the sorted table directory, 4-byte padding, per-table checksums and the
    /// <c>head.checkSumAdjustment</c> fix-up.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> <c>Core.Fonts.TrueTypeSubsetter</c>,
    /// <c>Plugins.Maintenance.TrueTypeGlyphPruner</c>, <c>Base.Rendering.CffOtfWrapper</c> and
    /// <c>Base.Operations.Fonts.PdfFontSubsetter</c> all assemble through it, for a TrueType or a
    /// CFF/OTF container alike. <b>Do not add another.</b></para>
    ///
    /// <para><b>Two rules a copy can get wrong.</b> The table directory is sorted with
    /// <c>StringComparer.Ordinal</c>: the culture-sensitive default places <c>OS/2</c> between
    /// <c>name</c> and <c>post</c> rather than first, violating §5.2's ascending-byte order in
    /// essentially every font. And <c>searchRange</c>/<c>entrySelector</c> come from integer
    /// arithmetic, not <c>Math.Log</c>, where a power of two can land on the wrong side of a
    /// <c>Math.Floor</c>. <b>Neither is visible in a font that renders</b> — a linear-scanning
    /// reader is unaffected by either — which is why copies could disagree unnoticed.</para>
    ///
    /// <para><b>One deliberate non-caller.</b> <c>Core.Fonts.WoffDecoder.Reassemble</c> writes the
    /// same layout and is <i>not</i> a copy of this: it carries each table's checksum through from
    /// the WOFF directory instead of recomputing, and leaves <c>head.checkSumAdjustment</c> alone.
    /// Routing it here would change both, so it is not assumed.</para>
    ///
    /// <para><b>Public rather than internal</b> because <c>Plugins.Maintenance</c> needs it and a
    /// plugin cannot see Core's internals — the same reason <c>IncrementalUpdateWriter</c> and
    /// <c>Base.Operations.VerifiedWrite</c> are public. Sharing downward into Core is the move;
    /// widening <c>InternalsVisibleTo</c> to name a plugin is not.</para>
    /// <para><b>The sfnt version is a parameter, which is the whole point.</b> An OpenType font with
    /// CFF outlines is <c>'OTTO'</c>; a TrueType one is <c>0x00010000</c>. Everything else about the
    /// container is identical, so hardcoding the version is what turned one assembler into three.</para>
    /// <para><b>Two details that are easy to get wrong and are handled here.</b> The directory's
    /// <c>length</c> field is the table's <i>actual</i> length while its checksum covers the
    /// <i>padded</i> bytes (§5.2 of the OpenType spec); and <c>head.checkSumAdjustment</c> must be
    /// zeroed before the whole-file sum is taken, then set to <c>0xB1B0AFBA − sum</c>. A font that
    /// gets either wrong still renders in most viewers and fails validation in others, which is the
    /// worst way to be wrong.</para>
    /// </remarks>
    public static class SfntBuilder
    {
        /// <summary>sfnt version for OpenType with CFF outlines.</summary>
        public const uint VersionOpenTypeCff = 0x4F54544Fu;   // 'OTTO'

        /// <summary>sfnt version for TrueType outlines.</summary>
        public const uint VersionTrueType = 0x00010000u;

        private const uint CheckSumMagic = 0xB1B0AFBAu;

        /// <summary>
        /// Builds the container. <paramref name="tables"/> is taken as given — this method does not
        /// invent, drop or validate tables, it only lays them out.
        /// </summary>
        /// <param name="sfntVersion">
        /// <see cref="VersionOpenTypeCff"/> or <see cref="VersionTrueType"/>. Passed through
        /// verbatim so a caller preserving an input font's version can do so.
        /// </param>
        /// <param name="tables">Tag → payload. Tags must be exactly four ASCII characters.</param>
        /// <returns>The assembled font, or <c>null</c> when there are no tables to write.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="tables"/> is null.</exception>
        /// <exception cref="ArgumentException">A tag is not four characters, or a payload is null.</exception>
        public static byte[] Build(uint sfntVersion, IReadOnlyList<(string tag, byte[] data)> tables)
        {
            if (tables == null) throw new ArgumentNullException(nameof(tables));
            if (tables.Count == 0) return null;

            // Sorted by tag: §5.2 requires the directory to be in ascending tag order, and a reader
            // that binary-searches it (which is what searchRange/entrySelector are for) gets wrong
            // answers otherwise.
            var sorted = new List<(string tag, byte[] data)>(tables);
            for (int i = 0; i < sorted.Count; i++)
            {
                if (sorted[i].tag == null || sorted[i].tag.Length != 4)
                    throw new ArgumentException(
                        $"sfnt table tags are exactly four characters; got \"{sorted[i].tag}\".",
                        nameof(tables));
                if (sorted[i].data == null)
                    throw new ArgumentException(
                        $"table \"{sorted[i].tag}\" has no data.", nameof(tables));
            }
            sorted.Sort((a, b) => string.CompareOrdinal(a.tag, b.tag));

            int n = sorted.Count;
            int directoryBytes = 12 + n * 16;

            var offsets = new int[n];
            var padded = new int[n];
            int cursor = directoryBytes;
            for (int i = 0; i < n; i++)
            {
                offsets[i] = cursor;
                padded[i] = (sorted[i].data.Length + 3) & ~3;
                cursor += padded[i];
            }

            byte[] font = new byte[cursor];   // zero-initialised, so padding bytes are already 0

            int sp = 1;
            while (sp * 2 <= n) sp *= 2;      // largest power of two <= n
            W32(font, 0, sfntVersion);
            W16(font, 4, (ushort)n);
            W16(font, 6, (ushort)(sp * 16));            // searchRange
            W16(font, 8, (ushort)Log2(sp));             // entrySelector
            W16(font, 10, (ushort)(n * 16 - sp * 16));  // rangeShift

            // The table data is copied FIRST and head.checkSumAdjustment zeroed BEFORE any
            // directory entry is computed. Order is load-bearing: an entry for head written from the
            // bytes as they arrived, before FixHeadChecksum changed them, would be wrong by exactly
            // the adjustment for any font whose head carries a non-zero checkSumAdjustment (i.e.
            // every real font). Measured on arial.ttf and times.ttf: the delta equals the source
            // font's adjustment to the bit.
            //
            // The spec (OpenType, "Calculating checksums") requires head's entry to be computed
            // with checkSumAdjustment SET TO ZERO, which is why it is zeroed earlier rather
            // than the entry recomputed afterwards -- recomputing it from the final bytes
            // would be a different wrong answer, and would also invalidate the whole-file sum that
            // the adjustment is derived from.
            for (int i = 0; i < n; i++)
                Array.Copy(sorted[i].data, 0, font, offsets[i], sorted[i].data.Length);

            int headIndex = -1;
            for (int i = 0; i < n; i++)
                if (sorted[i].tag == "head") { headIndex = i; break; }
            if (headIndex >= 0 && offsets[headIndex] + 12 <= font.Length)
                W32(font, offsets[headIndex] + 8, 0);

            int dir = 12;
            for (int i = 0; i < n; i++)
            {
                (string tag, byte[] data) = sorted[i];

                font[dir] = (byte)tag[0];
                font[dir + 1] = (byte)tag[1];
                font[dir + 2] = (byte)tag[2];
                font[dir + 3] = (byte)tag[3];
                // Checksum over the PADDED bytes, length field the ACTUAL length. Not a typo.
                W32(font, dir + 4, Checksum(font, offsets[i], padded[i]));
                W32(font, dir + 8, (uint)offsets[i]);
                W32(font, dir + 12, (uint)data.Length);
                dir += 16;
            }

            FixHeadChecksum(font, sorted, offsets);
            return font;
        }

        /// <summary>
        /// Zeroes <c>head.checkSumAdjustment</c>, sums the whole file, and writes
        /// <c>0xB1B0AFBA − sum</c>. A font with no <c>head</c> table is left alone.
        /// </summary>
        private static void FixHeadChecksum(
            byte[] font, List<(string tag, byte[] data)> tables, int[] offsets)
        {
            int headIndex = -1;
            for (int i = 0; i < tables.Count; i++)
                if (tables[i].tag == "head") { headIndex = i; break; }

            if (headIndex < 0) return;

            int headOffset = offsets[headIndex];
            if (headOffset + 12 > font.Length) return;

            W32(font, headOffset + 8, 0);

            int total = (font.Length + 3) & ~3;
            byte[] forSum = font;
            if (total != font.Length)
            {
                forSum = new byte[total];
                Array.Copy(font, forSum, font.Length);
            }

            W32(font, headOffset + 8, unchecked(CheckSumMagic - Checksum(forSum, 0, total)));
        }

        /// <summary>Big-endian 32-bit sum with wraparound, over a 4-byte-aligned range (§5.2).</summary>
        private static uint Checksum(byte[] data, int offset, int length)
        {
            uint sum = 0;
            for (int i = offset; i + 3 < offset + length && i + 3 < data.Length; i += 4)
            {
                sum = unchecked(sum + (uint)(
                    (data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3]));
            }
            return sum;
        }

        private static int Log2(int value)
        {
            int bits = 0;
            while (value > 1) { value >>= 1; bits++; }
            return bits;
        }

        private static void W16(byte[] data, int offset, ushort value) => IO.BigEndian.Put16(data, offset, value);

        private static void W32(byte[] data, int offset, uint value) => IO.BigEndian.Put32(data, offset, value);
    }
}
