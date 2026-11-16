// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts
{
    /// <summary>
    /// <c>head</c>'s cross-reference entry must be the checksum of the table with
    /// <c>checkSumAdjustment</c> set to zero — the one exception the OpenType checksum rules make,
    /// and the reason the order of operations inside <see cref="SfntBuilder"/> is load-bearing.
    /// </summary>
    /// <remarks>
    /// <b>It was written from the bytes as they arrived.</b> The directory entry was computed in
    /// the same loop that copied the table data, and <c>checkSumAdjustment</c> was only zeroed
    /// afterwards — so for any font whose <c>head</c> carries a non-zero adjustment, which is every
    /// real font, the recorded checksum was wrong by exactly that adjustment. Measured on
    /// <c>arial.ttf</c> and <c>times.ttf</c> before the fix: the delta equalled the source font's
    /// own adjustment to the bit, 0xC288F0AA and 0x979AEBD3.
    ///
    /// <para><b>Every caller that hands <c>SfntBuilder</c> a verbatim <c>head</c> was affected</b> —
    /// <c>SfntTablePruner</c>, <c>Cff.CffSubsetter</c> and <c>TrueTypeGlyphPruner</c> — and the
    /// output goes into <c>/FontFile2</c> / <c>/FontFile3</c>. <c>PdfFontSubsetter</c> escaped only
    /// because it pre-zeroes the field, under a comment that asserted the order did not matter.</para>
    ///
    /// <para><b>Nothing in the tree verifies a checksum</b>, which is why this shipped: the test
    /// helpers write zero for every table checksum and for <c>checkSumAdjustment</c>, so a
    /// synthetic font cannot witness it. These cases build the table bytes directly with a non-zero
    /// adjustment, which is the only shape that can.</para>
    ///
    /// <para>The fix is to zero the field BEFORE the directory is computed, not to recompute the
    /// entry afterwards: recomputing from the final bytes is a different wrong answer, and would
    /// also invalidate the whole-file sum that the adjustment is itself derived from.</para>
    /// </remarks>
    public class SfntHeadChecksumTests
    {
        /// <summary>A value no correct implementation can produce by accident.</summary>
        private const uint SourceAdjustment = 0xC288F0AA;

        [Fact]
        public void TheHeadDirectoryEntryIgnoresTheIncomingCheckSumAdjustment()
        {
            byte[] font = SfntBuilder.Build(SfntBuilder.VersionTrueType, TablesWithHead(SourceAdjustment));
            Assert.NotNull(font);

            (uint recorded, int offset, int length) = HeadEntry(font!);
            Assert.Equal(WithAdjustmentZeroed(font!, offset, length), recorded);
        }

        /// <summary>
        /// The same font built with the field already zero must produce the SAME directory entry.
        /// </summary>
        /// <remarks>
        /// This is what makes the case above a statement about the incoming value rather than about
        /// checksums in general: the entry must not depend on what the caller happened to leave in
        /// the field.
        /// </remarks>
        [Fact]
        public void TheHeadDirectoryEntryIsTheSameWhicheverAdjustmentArrived()
        {
            byte[] withValue = SfntBuilder.Build(SfntBuilder.VersionTrueType, TablesWithHead(SourceAdjustment));
            byte[] withZero = SfntBuilder.Build(SfntBuilder.VersionTrueType, TablesWithHead(0));

            Assert.Equal(HeadEntry(withZero!).Recorded, HeadEntry(withValue!).Recorded);
        }

        /// <summary>
        /// The adjustment itself is still recomputed, so the whole file sums to the magic value.
        /// </summary>
        /// <remarks>
        /// The floor for the fix: zeroing the field earlier must not stop it being written. Without
        /// this, "zero it and never write it back" satisfies both cases above.
        /// </remarks>
        [Fact]
        public void TheWholeFileStillSumsToTheMagicValue()
        {
            byte[] font = SfntBuilder.Build(SfntBuilder.VersionTrueType, TablesWithHead(SourceAdjustment));

            (uint _, int offset, int _length) = HeadEntry(font!);
            Assert.NotEqual(SourceAdjustment, ReadU32(font!, offset + 8));
            Assert.NotEqual(0u, ReadU32(font!, offset + 8));

            Assert.Equal(0xB1B0AFBAu, Sum(font!, 0, (font!.Length + 3) & ~3));
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// A minimal well-formed table set. <c>head</c> is 54 bytes with the magic number in place
        /// and <paramref name="adjustment"/> at offset 8; the others exist only so the directory has
        /// more than one entry and <c>head</c> is not at a special offset.
        /// </summary>
        private static List<(string, byte[])> TablesWithHead(uint adjustment)
        {
            var head = new byte[54];
            WriteU32(head, 0, 0x00010000);          // version
            WriteU32(head, 4, 0x00010000);          // fontRevision
            WriteU32(head, 8, adjustment);          // checkSumAdjustment
            WriteU32(head, 12, 0x5F0F3CF5);         // magicNumber
            head[18] = 0x08;                        // unitsPerEm = 2048
            head[19] = 0x00;

            var cmap = new byte[16];
            for (int i = 0; i < cmap.Length; i++) cmap[i] = (byte)(i * 7 + 1);
            var maxp = new byte[6];
            WriteU32(maxp, 0, 0x00010000);

            return new List<(string, byte[])>
            {
                ("cmap", cmap),
                ("head", head),
                ("maxp", maxp),
            };
        }

        private static (uint Recorded, int Offset, int Length) HeadEntry(byte[] font)
        {
            int n = (font[4] << 8) | font[5];
            for (int i = 0; i < n; i++)
            {
                int dir = 12 + i * 16;
                if (font[dir] == (byte)'h' && font[dir + 1] == (byte)'e'
                    && font[dir + 2] == (byte)'a' && font[dir + 3] == (byte)'d')
                {
                    return (ReadU32(font, dir + 4), (int)ReadU32(font, dir + 8), (int)ReadU32(font, dir + 12));
                }
            }

            throw new InvalidOperationException("the built font has no head table");
        }

        /// <summary>The checksum the spec asks for: the table's bytes with the adjustment zeroed.</summary>
        private static uint WithAdjustmentZeroed(byte[] font, int offset, int length)
        {
            var copy = (byte[])font.Clone();
            WriteU32(copy, offset + 8, 0);
            return Sum(copy, offset, (length + 3) & ~3);
        }

        private static uint Sum(byte[] data, int offset, int length)
        {
            uint sum = 0;
            for (int i = offset; i + 3 < offset + length && i + 3 < data.Length; i += 4)
                sum = unchecked(sum + ReadU32(data, i));
            return sum;
        }

        private static uint ReadU32(byte[] d, int at)
            => (uint)((d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3]);

        private static void WriteU32(byte[] d, int at, uint v)
        {
            d[at] = (byte)(v >> 24);
            d[at + 1] = (byte)(v >> 16);
            d[at + 2] = (byte)(v >> 8);
            d[at + 3] = (byte)v;
        }
    }
}
