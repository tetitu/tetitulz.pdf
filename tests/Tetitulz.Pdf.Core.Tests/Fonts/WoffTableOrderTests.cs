// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts
{
    /// <summary>
    /// A decoded WOFF puts its <b>directory</b> in tag order (OpenType §5.1) and its <b>table
    /// data</b> in the container's order (WOFF 1.0 §6: "the same order as the well-formed input
    /// font"). Those are two different orders, and the decoder used to apply one to both.
    /// </summary>
    /// <remarks>
    /// <b>Why the data order is not cosmetic here.</b> This decoder passes each table's checksum
    /// AND <c>head.checkSumAdjustment</c> through verbatim instead of recomputing them — that is
    /// deliberate, and it is what makes a byte-identical round trip possible at all. But the
    /// adjustment is a function of the whole file, offsets included. Re-laying the data changed
    /// every offset after the first moved table, so the carried adjustment no longer described the
    /// font, and §5's byte-identical promise was unreachable.
    ///
    /// <para><b>A container already in tag order cannot detect this</b>, because the reorder is
    /// then a no-op — which is exactly why it stayed green: <c>MinimalWoff</c> wraps a font whose
    /// tables are already sorted. <c>Wrap(sfnt, reverseTableOrder: true)</c> is the missing shape.</para>
    ///
    /// <para><b>This is also why <c>WoffDecoder</c> is NOT routed through <c>SfntBuilder</c></b>,
    /// which the consolidation backlog considered and rejected: <c>SfntBuilder</c> lays data out in
    /// its own sorted order and structurally cannot express "directory in tag order, data in
    /// container order", quite apart from recomputing the checksums this decoder must preserve.</para>
    /// </remarks>
    public class WoffTableOrderTests
    {
        private const string Characters = "HeloZ";

        [Fact]
        public void TheDirectoryIsSortedByTagWhateverOrderTheContainerUsed()
        {
            byte[] sfnt = Decode(reverseTableOrder: true);

            List<string> tags = Directory(sfnt).Select(t => t.Tag).ToList();

            Assert.Equal(tags.OrderBy(t => t, StringComparer.Ordinal).ToList(), tags);
        }

        /// <summary>
        /// The table data follows the CONTAINER, so a reversed container yields offsets that
        /// descend as the directory ascends.
        /// </summary>
        [Fact]
        public void TheTableDataFollowsTheContainerOrderNotTheTagOrder()
        {
            List<(string Tag, int Offset)> reversed = Directory(Decode(reverseTableOrder: true));
            List<(string Tag, int Offset)> forward = Directory(Decode(reverseTableOrder: false));

            // Same tags, same directory order — only the offsets differ.
            Assert.Equal(forward.Select(t => t.Tag), reversed.Select(t => t.Tag));

            // Forward container: offsets ascend with the directory. Reversed: they descend.
            Assert.True(IsAscending(forward.Select(t => t.Offset)),
                "a tag-ordered container should lay its data out in the same order");
            Assert.True(IsDescending(reversed.Select(t => t.Offset)),
                "a reversed container's data must follow the container, not the directory");
        }

        /// <summary>
        /// Every table is still readable at the offset its directory entry names, in both orders.
        /// </summary>
        /// <remarks>
        /// The floor: an offset scheme can be "in container order" and still point at the wrong
        /// bytes. Without this, swapping the offsets around arbitrarily would satisfy the case
        /// above.
        /// </remarks>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void EveryTableIsReadableWhereItsDirectoryEntrySaysItIs(bool reverseTableOrder)
        {
            byte[] original = MinimalTrueType.Build(Characters);
            byte[] decoded = Decode(reverseTableOrder);

            foreach ((string tag, int offset, int length) in DirectoryWithLengths(decoded))
            {
                byte[] expected = TableOf(original, tag);
                Assert.NotNull(expected);
                Assert.Equal(expected!.Length, length);

                for (int i = 0; i < length; i++)
                    Assert.Equal(expected[i], decoded[offset + i]);
            }
        }

        // ------------------------------------------------------------------

        private static byte[] Decode(bool reverseTableOrder)
        {
            byte[] woff = MinimalWoff.Wrap(MinimalTrueType.Build(Characters), reverseTableOrder);
            byte[] sfnt = WoffDecoder.Decode(woff);
            Assert.NotNull(sfnt);
            return sfnt!;
        }

        private static List<(string Tag, int Offset)> Directory(byte[] sfnt)
            => DirectoryWithLengths(sfnt).Select(t => (t.Tag, t.Offset)).ToList();

        private static List<(string Tag, int Offset, int Length)> DirectoryWithLengths(byte[] sfnt)
        {
            int n = (sfnt[4] << 8) | sfnt[5];
            var result = new List<(string, int, int)>(n);
            for (int i = 0; i < n; i++)
            {
                int at = 12 + i * 16;
                string tag = new string(new[]
                {
                    (char)sfnt[at], (char)sfnt[at + 1], (char)sfnt[at + 2], (char)sfnt[at + 3],
                });
                result.Add((tag, (int)ReadU32(sfnt, at + 8), (int)ReadU32(sfnt, at + 12)));
            }
            return result;
        }

        private static byte[] TableOf(byte[] sfnt, string tag)
        {
            foreach ((string t, int offset, int length) in DirectoryWithLengths(sfnt))
            {
                if (!string.Equals(t, tag, StringComparison.Ordinal)) continue;
                var data = new byte[length];
                Array.Copy(sfnt, offset, data, 0, length);
                return data;
            }
            return null;
        }

        private static bool IsAscending(IEnumerable<int> values)
        {
            int previous = int.MinValue;
            foreach (int v in values) { if (v <= previous) return false; previous = v; }
            return true;
        }

        private static bool IsDescending(IEnumerable<int> values)
        {
            int previous = int.MaxValue;
            foreach (int v in values) { if (v >= previous) return false; previous = v; }
            return true;
        }

        private static uint ReadU32(byte[] d, int at)
            => (uint)((d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3]);
    }
}
