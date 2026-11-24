// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Fonts;

/// <summary>
/// Every sfnt this library emits carries its table directory in ascending <b>byte</b> order
/// (OpenType §5.2), because that is what <c>searchRange</c>/<c>entrySelector</c>/<c>rangeShift</c>
/// exist to let a reader binary-search.
/// </summary>
/// <remarks>
/// <para><b>The defect this closes.</b> Four places open-coded the sfnt assembly; three sorted with
/// <c>StringComparer.Ordinal</c> and <c>PdfFontSubsetter.AssembleFont</c> used
/// <c>tables.Keys.OrderBy(t =&gt; t)</c> — the culture-sensitive default. Measured on a real tag
/// set, that places <c>OS/2</c> between <c>name</c> and <c>post</c> instead of first, because
/// ordinal comparison puts uppercase (0x41–0x5A) before lowercase (0x61–0x7A) and the culture-aware
/// comparer does not. Every font this subsetted carried a mis-ordered directory, since essentially
/// every real font has an <c>OS/2</c> table.</para>
///
/// <para><b>Why nothing caught it, and why nothing would have.</b> A linear-scanning reader — which
/// this library's own parser is, and most viewers are — finds the tables regardless of order, so
/// the font renders correctly and every existing font test passes. Only a reader that trusts the
/// binary-search hints, or a validator, sees it. That is the "renders in most viewers and fails
/// validation in others" failure <c>SfntBuilder</c>'s own remarks name as the worst way to be
/// wrong, and it survived four independent copies of the same routine.</para>
///
/// <para><b>These read the directory back out of the emitted bytes</b> rather than checking that
/// two sorting expressions agree — a comparison against a second copy of the formula proves only
/// that two spellings match.</para>
/// </remarks>
public sealed class SfntDirectoryOrderTests
{
    private const string Sample = "AaBbCc123";

    /// <summary>
    /// A subset emits an ordinal-sorted directory. The fixture has an <c>OS/2</c> table, which is
    /// exactly the tag the two orderings disagree about.
    /// </summary>
    [Fact]
    public void ASubsettedFontsTableDirectoryIsSortedByByteValue()
    {
        byte[] subset = PdfFontSubsetter.Subset(MinimalTrueType.Build(Sample), Sample);

        string[] tags = DirectoryTags(subset);

        Assert.Contains("OS/2", tags);   // the fixture reaches the tag under test
        AssertAscendingOrdinal(tags);
    }

    /// <summary>
    /// The floor: compaction, which removes tables rather than glyphs, sorts correctly too. Without
    /// it a change that broke both emitters the same way would leave the test above as the only
    /// witness, and a shared helper is exactly where a shared bug would live.
    /// </summary>
    [Fact]
    public void ACompactedFontsTableDirectoryIsSortedByByteValue()
    {
        byte[] font = MinimalTrueType.BuildWithDroppableTables(Sample);

        Assert.True(PdfFontSubsetter.TryCompact(font, out byte[] compacted, out string reason),
            $"the fixture produced nothing to compact: {reason}");

        string[] tags = DirectoryTags(compacted);

        Assert.Contains("OS/2", tags);
        AssertAscendingOrdinal(tags);
    }

    /// <summary>
    /// The directory's <c>length</c> field is each table's actual length while its offset must land
    /// on a 4-byte boundary inside the file — the pair that goes wrong together when a layout is
    /// rewritten, and the reason the tags alone are not enough evidence that the merge preserved
    /// the layout.
    /// </summary>
    [Fact]
    public void ASubsettedFontsTableRecordsPointInsideTheFileAndStayAligned()
    {
        byte[] subset = PdfFontSubsetter.Subset(MinimalTrueType.Build(Sample), Sample);
        int numTables = ReadU16(subset, 4);

        for (int i = 0; i < numTables; i++)
        {
            int record = 12 + i * 16;
            string tag = Encoding.ASCII.GetString(subset, record, 4);
            long offset = ReadU32(subset, record + 8);
            long length = ReadU32(subset, record + 12);

            Assert.True(offset % 4 == 0, $"table \"{tag}\" starts at {offset}, which is not 4-byte aligned");
            Assert.True(offset >= 12 + numTables * 16,
                $"table \"{tag}\" starts at {offset}, inside the directory");
            Assert.True(offset + length <= subset.Length,
                $"table \"{tag}\" runs to {offset + length} in a {subset.Length}-byte font");
        }
    }

    // ================================================================ helpers

    private static void AssertAscendingOrdinal(string[] tags)
    {
        var expected = tags.OrderBy(t => t, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, tags);

        // Stated a second way so the failure message names the pair rather than the whole list:
        // OrderBy would also be satisfied by an empty directory.
        Assert.True(tags.Length > 1, "a one-table directory cannot witness an ordering");
        for (int i = 1; i < tags.Length; i++)
            Assert.True(string.CompareOrdinal(tags[i - 1], tags[i]) < 0,
                $"\"{tags[i - 1]}\" must precede \"{tags[i]}\" in byte order");
    }

    private static string[] DirectoryTags(byte[] font)
    {
        int numTables = ReadU16(font, 4);
        var tags = new List<string>(numTables);
        for (int i = 0; i < numTables; i++)
            tags.Add(Encoding.ASCII.GetString(font, 12 + i * 16, 4));
        return tags.ToArray();
    }

    private static int ReadU16(byte[] d, int at) => (d[at] << 8) | d[at + 1];

    private static long ReadU32(byte[] d, int at)
        => ((long)d[at] << 24) | ((long)d[at + 1] << 16) | ((long)d[at + 2] << 8) | d[at + 3];
}
