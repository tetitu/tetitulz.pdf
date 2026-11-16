// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// The WOFF 2.0 §5.4 <c>hmtx</c> transform, called directly: the decoder fixture in
/// <c>Woff2DecoderTests</c> proves the round trip on a real font, and these pin what one font cannot —
/// each flag combination, every refusal beside its acceptance control, the sign of a negative bearing,
/// and the empty-glyph rule in <see cref="Woff2HmtxTransform.ReadXMins"/>.
/// </summary>
public class Woff2HmtxTransformTests
{
    // Three glyphs, two long metrics: advances 500 and 600, bounding-box xMins -5, 7 and 9.
    private static readonly int[] XMins = { -5, 7, 9 };

    [Fact]
    public void LsbAbsent_DerivesTheProportionalBearings_AndReadsTheMonospacedOnes()
        => Assert.Equal(
            new byte[] { 0x01, 0xF4, 0xFF, 0xFB, 0x02, 0x58, 0x00, 0x07, 0xFF, 0xFD },
            Woff2HmtxTransform.Rebuild(new byte[] { 0x01, 0x01, 0xF4, 0x02, 0x58, 0xFF, 0xFD }, 3, 2, XMins));

    [Fact]
    public void LeftSideBearingAbsent_ReadsTheProportionalBearings_AndDerivesTheMonospacedOnes()
        => Assert.Equal(
            new byte[] { 0x01, 0xF4, 0x00, 0x01, 0x02, 0x58, 0x00, 0x02, 0x00, 0x09 },
            Woff2HmtxTransform.Rebuild(new byte[] { 0x02, 0x01, 0xF4, 0x02, 0x58, 0x00, 0x01, 0x00, 0x02 }, 3, 2, XMins));

    [Fact]
    public void BothAbsent_DerivesEveryBearing()
        => Assert.Equal(
            new byte[] { 0x01, 0xF4, 0xFF, 0xFB, 0x02, 0x58, 0x00, 0x07, 0x00, 0x09 },
            Woff2HmtxTransform.Rebuild(new byte[] { 0x03, 0x01, 0xF4, 0x02, 0x58 }, 3, 2, XMins));

    /// <summary>
    /// Each refusal, against the valid both-absent table in <see cref="BothAbsent_DerivesEveryBearing"/>.
    /// Each fixture is complete for what its flags declare, so it is refused for its own reason and
    /// not as a truncation (the sweep showed two that were not).
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x00, 0x01, 0xF4, 0x02, 0x58, 0x00, 0x01, 0x00, 0x02, 0x00, 0x09 }, 3, 2, "neither array eliminated")]
    [InlineData(new byte[] { 0x07, 0x01, 0xF4, 0x02, 0x58 }, 3, 2, "a reserved flag bit")]
    [InlineData(new byte[] { 0x83, 0x01, 0xF4, 0x02, 0x58 }, 3, 2, "the top reserved bit")]
    [InlineData(new byte[] { 0x03, 0x01, 0xF4, 0x02, 0x58, 0x00 }, 3, 2, "a byte left over")]
    [InlineData(new byte[] { 0x03, 0x01, 0xF4, 0x02 }, 3, 2, "a truncated advance")]
    [InlineData(new byte[] { 0x01, 0x01, 0xF4, 0x02, 0x58, 0xFF }, 3, 2, "a truncated monospaced bearing")]
    [InlineData(new byte[] { }, 3, 2, "no flags byte")]
    [InlineData(new byte[] { 0x03 }, 3, 0, "no long metrics")]
    [InlineData(new byte[] { 0x03, 0x01, 0xF4, 0x02, 0x58, 0x02, 0x58, 0x02, 0x58 }, 3, 4, "more long metrics than glyphs")]
    public void AnInvalidTable_IsRefused(byte[] transformed, int numGlyphs, int numberOfHMetrics, string because)
    {
        Assert.NotNull(Woff2HmtxTransform.Rebuild(new byte[] { 0x03, 0x01, 0xF4, 0x02, 0x58 }, 3, 2, XMins));
        Assert.True(Woff2HmtxTransform.Rebuild(transformed, numGlyphs, numberOfHMetrics, XMins) == null, because);
    }

    [Fact]
    public void XMinsOfTheWrongLength_AreRefused()
    {
        Assert.Null(Woff2HmtxTransform.Rebuild(new byte[] { 0x03, 0x01, 0xF4, 0x02, 0x58 }, 3, 2, new[] { -5, 7 }));
        // Too long: every glyph would find its xMin, so only the length check refuses it.
        Assert.Null(Woff2HmtxTransform.Rebuild(new byte[] { 0x03, 0x01, 0xF4, 0x02, 0x58 }, 3, 2, new[] { -5, 7, 9, 11 }));
        Assert.Null(Woff2HmtxTransform.Rebuild(new byte[] { 0x03, 0x01, 0xF4, 0x02, 0x58 }, 3, 2, null));
    }

    // glyf: glyph 0 at 0 (xMin -10), glyph 1 empty, glyph 2 at 10 (xMin 20). Reading a "bbox" for the
    // empty glyph at offset 10 would answer 20, so its zero is the rule and not an accident.
    private static readonly byte[] Glyf =
    {
        0x00, 0x01, 0xFF, 0xF6, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x01, 0x00, 0x14, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };

    [Fact]
    public void ReadXMins_LongLoca_SignExtends_AndGivesAnEmptyGlyphZero()
        => Assert.Equal(new[] { -10, 0, 20 },
            Woff2HmtxTransform.ReadXMins(Glyf, new byte[] { 0, 0, 0, 0, 0, 0, 0, 10, 0, 0, 0, 10, 0, 0, 0, 20 }, 3, 1));

    /// <summary>A short <c>loca</c> stores offsets halved (§5.4's source, OpenType <c>loca</c>).</summary>
    [Fact]
    public void ReadXMins_ShortLoca_DoublesItsOffsets()
        => Assert.Equal(new[] { -10, 0, 20 },
            Woff2HmtxTransform.ReadXMins(Glyf, new byte[] { 0, 0, 0, 5, 0, 5, 0, 10 }, 3, 0));

    [Fact]
    public void ReadXMins_RefusesALocaTooShort_AndARecordPastTheGlyfTable()
    {
        Assert.Null(Woff2HmtxTransform.ReadXMins(Glyf, new byte[] { 0, 0, 0, 5, 0, 5 }, 3, 0));
        Assert.Null(Woff2HmtxTransform.ReadXMins(Glyf, new byte[] { 0, 0, 0, 5, 0, 5, 0, 10, 0, 11 }, 4, 0)); // glyph 3 at 20, 2 bytes on
        Assert.NotNull(Woff2HmtxTransform.ReadXMins(Glyf, new byte[] { 0, 0, 0, 5, 0, 5, 0, 10 }, 3, 0));
    }
}
