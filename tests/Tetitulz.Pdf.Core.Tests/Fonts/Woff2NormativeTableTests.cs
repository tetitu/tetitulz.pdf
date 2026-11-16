// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// The two WOFF 2.0 tables that exist only as enumerations are <b>internally consistent</b>, which
/// is checkable without owning the specification.
/// </summary>
/// <remarks>
/// <para><b>This is the JBIG2 Annex B lesson applied before the mistake.</b> Three of five Annex B
/// tables in this repository were once written from recollection and were <i>provably impossible</i>
/// — their Kraft sums came to 1.008, 1.25 and 1.406 — and they had to be deleted rather than left to
/// mislead. What made that discoverable was that a prefix code has an arithmetic invariant. A
/// 128-row triplet table has one too, and this file is it.</para>
///
/// <para><b>The invariant, and why it is not circular.</b> §5.2 says Byte Count is the size of the
/// whole triplet <i>including</i> the flag byte, which is carried in a different stream. So the
/// coordinate bytes are <c>ByteCount - 1</c>, and the bits they carry must be exactly
/// <c>xBits + yBits</c>. Nothing in the decoder computes the table from that rule — the decoder
/// reads the rows — so a row typed wrong in either column fails here.</para>
///
/// <para><b>A guard whose value is an absence has to prove it looked</b>, so every count has a
/// floor: 128 triplet rows and 63 known tags are what the specification defines, and a table that
/// had silently shrunk would otherwise satisfy every "for each row" assertion vacuously.</para>
/// </remarks>
public class Woff2NormativeTableTests
{
    /// <summary>The triplet table has exactly the 128 rows a seven-bit flag can index.</summary>
    [Fact]
    public void TheTripletTableHasOneRowPerFlagValue()
        => Assert.Equal(128, Woff2Tables.TripletCount);

    /// <summary>
    /// <b>Every row's bit widths account for exactly its coordinate bytes.</b>
    /// </summary>
    /// <remarks>
    /// The arithmetic check that makes a transcription error loud. A row claiming three bytes while
    /// carrying 8 + 8 bits would read one byte too many for every point using it, shifting the rest
    /// of the glyph's coordinates — a deformed outline rather than a failure.
    /// </remarks>
    [Fact]
    public void EveryTripletRowsBitWidthsMatchItsByteCount()
    {
        var offenders = new List<string>();

        for (int i = 0; i < Woff2Tables.TripletCount; i++)
        {
            Woff2Tables.TripletRow row = Woff2Tables.TripletAt(i);
            int coordinateBits = (row.ByteCount - 1) * 8;

            if (row.XBits + row.YBits != coordinateBits)
                offenders.Add($"row {i}: {row.XBits}+{row.YBits} bits in {row.ByteCount - 1} " +
                              "coordinate bytes");
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// <b>A sign is present exactly where the coordinate has bits.</b>
    /// </summary>
    /// <remarks>
    /// The second half of the same consistency argument. A sign on a zero-width coordinate is a row
    /// that negates a delta the row cannot express; a missing sign on a wide one silently makes
    /// every delta on that row zero, because the decoder multiplies by it.
    /// </remarks>
    [Fact]
    public void EveryTripletRowHasASignExactlyWhereItHasBits()
    {
        var offenders = new List<string>();

        for (int i = 0; i < Woff2Tables.TripletCount; i++)
        {
            Woff2Tables.TripletRow row = Woff2Tables.TripletAt(i);

            if ((row.XBits == 0) != (row.XSign == 0)) offenders.Add($"row {i}: x sign/bits disagree");
            if ((row.YBits == 0) != (row.YSign == 0)) offenders.Add($"row {i}: y sign/bits disagree");
            if (row.XSign != 0 && row.XSign != 1 && row.XSign != -1) offenders.Add($"row {i}: x sign");
            if (row.YSign != 0 && row.YSign != 1 && row.YSign != -1) offenders.Add($"row {i}: y sign");
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The only bit widths §5.2 uses are 0, 4, 8, 12 and 16, and the decoder branches on exactly
    /// those.
    /// </summary>
    /// <remarks>
    /// A width outside the set would fall through the decoder's branches into the generic
    /// whole-byte path and read <c>bits / 8</c> bytes — right for 8 and 16, and quietly wrong for
    /// anything else. Asserting the domain here is cheaper than a defensive branch there.
    /// </remarks>
    [Fact]
    public void TripletBitWidthsAreOnlyTheFiveTheDecoderHandles()
    {
        var allowed = new HashSet<int> { 0, 4, 8, 12, 16 };

        for (int i = 0; i < Woff2Tables.TripletCount; i++)
        {
            Woff2Tables.TripletRow row = Woff2Tables.TripletAt(i);
            Assert.Contains(row.XBits, allowed);
            Assert.Contains(row.YBits, allowed);

            // Nibble widths are shared between the two coordinates, so they come in pairs.
            if (row.XBits == 4 || row.YBits == 4) Assert.Equal(row.XBits, row.YBits);
            if (row.XBits == 12 || row.YBits == 12) Assert.Equal(row.XBits, row.YBits);
        }
    }

    /// <summary>The known-tag table has the 63 entries §4.1 defines, index 63 being the escape.</summary>
    [Fact]
    public void TheKnownTagTableHasSixtyThreeEntries()
    {
        Assert.Equal(63, Woff2Tables.KnownTagCount);
        Assert.Null(Woff2Tables.TagFor(63));
        Assert.Null(Woff2Tables.TagFor(-1));
    }

    /// <summary>
    /// <b>Every known tag is exactly four characters.</b>
    /// </summary>
    /// <remarks>
    /// The trap this catches is real and specific: §4.1 pads short tags with trailing spaces, and
    /// the specification's own rendering of the table shows <c>cvt&#160;</c>, <c>CFF&#160;</c> and
    /// <c>SVG&#160;</c> trimmed. A three-character tag matches no sfnt table, and
    /// <see cref="SfntBuilder"/> throws on it — which is the merciful outcome, and still a bug found
    /// at the wrong end.
    /// </remarks>
    [Fact]
    public void EveryKnownTagIsFourCharactersIncludingItsPadding()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < Woff2Tables.KnownTagCount; i++)
        {
            string tag = Woff2Tables.TagFor(i);

            Assert.NotNull(tag);
            Assert.Equal(4, tag!.Length);
            Assert.True(seen.Add(tag), $"tag '{tag}' appears twice; index {i} duplicates it");

            foreach (char c in tag)
                Assert.InRange(c, ' ', '~');   // sfnt tags are printable ASCII
        }
    }

    /// <summary>
    /// The three tags whose padding the specification's rendering hides, spelled out.
    /// </summary>
    /// <remarks>
    /// Named rather than left to the length check, because "four characters" is satisfied by
    /// <c>cvtX</c> too. These three are the ones a transcription actually gets wrong.
    /// </remarks>
    [Theory]
    [InlineData(8, "cvt ")]
    [InlineData(13, "CFF ")]
    [InlineData(36, "SVG ")]
    [InlineData(0, "cmap")]
    [InlineData(6, "OS/2")]
    [InlineData(10, "glyf")]
    [InlineData(11, "loca")]
    [InlineData(3, "hmtx")]
    public void TheTagsAtTheIndicesTheDecoderDependsOn(int index, string expected)
        => Assert.Equal(expected, Woff2Tables.TagFor(index));
}
