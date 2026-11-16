// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// <see cref="Jbig2StandardHuffmanTableStructureTests"/> proves the 15 standard tables are
/// structurally possible prefix codes — pure mathematics, no spec text needed. It cannot prove
/// they decode to the <i>values</i> ITU-T T.88 Annex B actually assigns them, since a table can be
/// a perfectly valid prefix code and still disagree with the standard about which bit pattern
/// means which number.
///
/// <para>
/// This is the other half: a byte stream with a known, externally-verified decoded output — the
/// four bytes an independent, widely-deployed decoder checks itself with, and the four values it
/// expects of them — not this codebase's own derivation checked against itself.
/// </para>
/// </summary>
public class Jbig2StandardHuffmanTableOracleTests
{
    /// <summary>
    /// The byte stream read through Annex B's Tables B.4, B.2, B.2 and B.1 (Tables D, B, B, A here)
    /// decodes to { 8, 5, OOB, 8 }.
    /// </summary>
    [Fact]
    public void DecodesAnIndependentDecodersCheckStreamToItsKnownOutput()
    {
        var br = new MsbBitStream(new byte[] { 0xe9, 0xcb, 0xf4, 0x00 }, 0);

        int first  = Jbig2Decoder.StandardTableD.Decode(br);
        int second = Jbig2Decoder.StandardTableB.Decode(br);
        int third  = Jbig2Decoder.StandardTableB.Decode(br);
        int fourth = Jbig2Decoder.StandardTableA.Decode(br);

        Assert.Equal(8, first);
        Assert.Equal(5, second);
        Assert.Equal(Jbig2Decoder.Jbig2HuffmanTable.OOB, third);
        Assert.Equal(8, fourth);
    }

    /// <summary>
    /// A lower-range line decodes as <c>RangeLow - extra</c> — counting <i>downward</i> from
    /// RangeLow, not negating <c>RangeLow + extra</c>. The two formulas agree only when RangeLow
    /// is 0, which a real lower-range line's RangeLow never is (it is always one less than the
    /// lowest value the table's ascending rows cover) — so this is a case none of the
    /// standard-table tests above exercise: the oracle stream never touches a
    /// lower-range row of the tables it uses (D, B, B, A), and the structural tests
    /// (<see cref="Jbig2StandardHuffmanTableStructureTests"/>) check shape, not decoded values.
    /// A single-entry table isolates the formula from everything else a standard table's
    /// structure could be masking.
    /// </summary>
    [Fact]
    public void LowerRangeLineDecodesByCountingDownwardFromRangeLow()
    {
        var entry = new Jbig2Decoder.Jbig2HuffmanEntry
        {
            PrefixLen = 1,
            Prefix    = 0b0,
            RangeLow  = -1,
            RangeBits = 8,
            LowerSign = true,
        };
        var table = new Jbig2Decoder.Jbig2HuffmanTable(new[] { entry });

        // Bit stream: "0" (the 1-bit prefix) followed by "00000101" (extra = 5), padded to two
        // bytes: 0000 0010 1000 0000 = 0x02, 0x80.
        var br = new MsbBitStream(new byte[] { 0x02, 0x80 }, 0);

        int decoded = table.Decode(br);

        Assert.Equal(-6, decoded); // RangeLow(-1) - extra(5) = -6, not -(RangeLow + extra) = -4
    }
}
