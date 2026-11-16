// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The three code → glyph-name tables — <c>FontCache.WinAnsiNames</c>,
/// <c>FontCache.MacRomanNames</c> and <c>Core.Fonts.AdobeStandardEncodingNames</c> — agree
/// across the ASCII range <b>except at exactly two code points</b>.
///
/// <para><b>Why this is worth a test rather than a glance.</b> All three spell out codes 32–126 as
/// ~22 lines of <c>t[65]="A";t[66]="B";…</c>, and those blocks are identical except for
/// <c>0x27</c> and <c>0x60</c>. Two near-identical walls of literals differing in two places is
/// exactly where a typo lives undetected, and it is also exactly the shape that invites someone to
/// "deduplicate" them into one table — which would silently change StandardEncoding's apostrophe
/// and grave, and therefore the glyph a Type 1 font draws for them.</para>
///
/// <para>Existing coverage was three spot-checks on MacRoman's high range
/// (<c>RenderingGapsTier12Tests</c>) and the StandardEncoding tests in Core. Neither states the
/// relationship BETWEEN the tables, which is the thing a consolidation would break.</para>
/// </summary>
public class LatinEncodingTableAgreementTests
{
    /// <summary>
    /// §D.2: <c>0x27</c> is <c>quotesingle</c> in WinAnsi and MacRoman but <c>quoteright</c> in
    /// Adobe StandardEncoding, and <c>0x60</c> is <c>grave</c> against <c>quoteleft</c>. These are
    /// the only two disagreements below 127, and they are real — StandardEncoding predates the
    /// distinction between a typewriter apostrophe and a right single quote.
    /// </summary>
    private const int Apostrophe = 0x27;
    private const int Grave = 0x60;

    [Fact]
    public void WinAnsiAndMacRomanAreIdenticalThroughoutTheAsciiRange()
    {
        for (int c = 32; c <= 126; c++)
        {
            Assert.Equal(FontCache.WinAnsiNames[c], FontCache.MacRomanNames[c]);
        }
    }

    /// <summary>
    /// The load-bearing one: WinAnsi and StandardEncoding agree on every ASCII code EXCEPT the two
    /// named above. A consolidation that merged the three tables into one would fail here.
    /// </summary>
    [Fact]
    public void StandardEncodingMatchesWinAnsiAcrossAsciiApartFromTheTwoKnownCodePoints()
    {
        for (int c = 32; c <= 126; c++)
        {
            if (c == Apostrophe || c == Grave) continue;
            Assert.Equal(FontCache.WinAnsiNames[c], AdobeStandardEncodingNames.Get(c));
        }
    }

    /// <summary>
    /// And the two exceptions really are exceptions — without this the test above would pass just as
    /// happily if someone unified all three tables and the "known differences" quietly disappeared.
    /// </summary>
    [Theory]
    [InlineData(Apostrophe, "quotesingle", "quoteright")]
    [InlineData(Grave, "grave", "quoteleft")]
    public void TheTwoDivergentCodePointsKeepTheirOwnNames(int code, string winAnsi, string standard)
    {
        Assert.Equal(winAnsi, FontCache.WinAnsiNames[code]);
        Assert.Equal(winAnsi, FontCache.MacRomanNames[code]);
        Assert.Equal(standard, AdobeStandardEncodingNames.Get(code));
    }

    /// <summary>
    /// Anchors outside the shared block, so this file also notices a table being truncated or
    /// shifted rather than only mis-merged. MacRoman and WinAnsi diverge completely above 126.
    /// </summary>
    [Fact]
    public void TheTablesStillDivergeAboveTheAsciiRange()
    {
        Assert.Equal("Adieresis", FontCache.MacRomanNames[128]);
        Assert.Equal("Euro", FontCache.WinAnsiNames[128]);
        Assert.NotEqual(FontCache.MacRomanNames[128], FontCache.WinAnsiNames[128]);
    }
}
