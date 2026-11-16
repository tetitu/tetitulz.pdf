// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="Standard14Metrics"/> is keyed by <b>Unicode</b>, and an AFM file is keyed by
/// <b>StandardEncoding code</b>. Those two disagree at exactly two places, and both were wrong.
/// </summary>
/// <remarks>
/// <para>StandardEncoding puts <c>quoteright</c> at code 39 and <c>quoteleft</c> at code 96
/// (<c>StandardEncodings.cs</c> says so in as many words), where ASCII has <c>quotesingle</c>
/// (U+0027) and <c>grave</c> (U+0060). Transcribing an AFM by its encoded code and filing each
/// width under that code's ASCII character mis-assigns those two — and only those two, since every
/// other printable ASCII code names the same glyph in both.</para>
///
/// <para><b>Why it survived.</b> It is invisible in four of the six fonts: in the Times family
/// <c>quotesingle</c> and <c>quoteright</c> are close or equal, so the same transcription error
/// yields the right number. Only Helvetica and Helvetica-Bold showed it — and there the wrong
/// values equalled this class's own U+2019/U+2018 entries <i>exactly</i>, which is the signature of
/// the mis-keying rather than a coincidence.</para>
///
/// <para><b>The Times rows are not padding.</b> They are what makes the Helvetica rows a claim
/// about a defect rather than about a preference: the same class already keyed those correctly, so
/// the intended convention is not in doubt.</para>
/// </remarks>
public class Standard14QuoteAndGraveTests
{
    /// <summary>U+0027 APOSTROPHE is <c>quotesingle</c>, never <c>quoteright</c>.</summary>
    [Theory]
    [InlineData(StandardFonts.Helvetica, 191)]
    [InlineData(StandardFonts.HelveticaOblique, 191)]
    [InlineData(StandardFonts.HelveticaBold, 238)]
    [InlineData(StandardFonts.HelveticaBoldOblique, 238)]
    [InlineData(StandardFonts.TimesRoman, 180)]
    [InlineData(StandardFonts.TimesBold, 278)]
    [InlineData(StandardFonts.TimesItalic, 214)]
    [InlineData(StandardFonts.TimesBoldItalic, 278)]
    public void ApostropheIsQuotesingle(string font, double expected)
        => Assert.Equal(expected, Standard14Metrics.GetWidthByUnicode(font, '\''));

    /// <summary>U+0060 GRAVE ACCENT is <c>grave</c>, never <c>quoteleft</c>.</summary>
    [Theory]
    [InlineData(StandardFonts.Helvetica)]
    [InlineData(StandardFonts.HelveticaBold)]
    [InlineData(StandardFonts.TimesRoman)]
    [InlineData(StandardFonts.TimesBold)]
    [InlineData(StandardFonts.TimesItalic)]
    [InlineData(StandardFonts.TimesBoldItalic)]
    public void GraveAccentIsGrave(string font)
        => Assert.Equal(333, Standard14Metrics.GetWidthByUnicode(font, '`'));

    /// <summary>
    /// The typographic quotes keep their own — different — widths. This is the half that pins the
    /// fix as "these are two distinct glyphs" rather than "U+0027 should be 191": before the fix
    /// the ASCII and typographic entries were equal in the Helvetica family, and a change that
    /// merely moved the equality would satisfy the theories above.
    /// </summary>
    [Theory]
    [InlineData(StandardFonts.Helvetica, 222)]
    [InlineData(StandardFonts.HelveticaBold, 278)]
    public void TheTypographicQuotesAreUnchangedAndDifferFromTheAsciiOnes(string font, double quoteWidth)
    {
        Assert.Equal(quoteWidth, Standard14Metrics.GetWidthByUnicode(font, '’'));  // quoteright
        Assert.Equal(quoteWidth, Standard14Metrics.GetWidthByUnicode(font, '‘'));  // quoteleft

        Assert.NotEqual(quoteWidth, Standard14Metrics.GetWidthByUnicode(font, '\''));
        Assert.NotEqual(quoteWidth, Standard14Metrics.GetWidthByUnicode(font, '`'));
    }

    /// <summary>
    /// A spot-check that the rest of the ASCII range was not disturbed — the fix touched two
    /// entries per Helvetica table and nothing else.
    /// </summary>
    [Fact]
    public void TheRestOfTheAsciiRangeIsUnchanged()
    {
        Assert.Equal(278, Standard14Metrics.GetWidthByUnicode(StandardFonts.Helvetica, ' '));
        Assert.Equal(355, Standard14Metrics.GetWidthByUnicode(StandardFonts.Helvetica, '"'));
        Assert.Equal(667, Standard14Metrics.GetWidthByUnicode(StandardFonts.Helvetica, '&'));
        Assert.Equal(333, Standard14Metrics.GetWidthByUnicode(StandardFonts.Helvetica, '('));
        Assert.Equal(556, Standard14Metrics.GetWidthByUnicode(StandardFonts.Helvetica, '_'));
        Assert.Equal(469, Standard14Metrics.GetWidthByUnicode(StandardFonts.Helvetica, '^'));
    }
}
