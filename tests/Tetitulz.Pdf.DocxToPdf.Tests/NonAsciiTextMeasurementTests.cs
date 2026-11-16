// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// Text is measured with each character's real advance, not with a flat mid-width for anything
/// outside ASCII.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> <c>StandardFontMetrics</c> carried its own table for codes 32–126 and
/// charged a flat <c>500</c> for every other character — under a class comment saying its purpose
/// was to match "the widths the PDF viewer actually advances by, instead of a single
/// average-character estimate", which is exactly what it then did outside ASCII. In Helvetica an
/// em dash is 1000 (measured 500, half), a right single quote is 222 (measured 500, more than
/// double), <c>©</c> is 737, and every accented Latin-1 letter is 556.</para>
///
/// <para>This is the code that decides where lines wrap and where centred and right-aligned text
/// starts, so the error lands in the layout of any document with a smart quote or an accent — most
/// real Word documents. Nothing threw and nothing warned; the page was simply laid out wrong.</para>
///
/// <para><b>These assert against <see cref="Standard14Metrics"/> rather than against transcribed
/// numbers.</b> A second copy of the widths in the test is a second thing to keep in step, and this
/// whole row exists because a second copy drifted.</para>
/// </remarks>
public class NonAsciiTextMeasurementTests
{
    /// <summary>At size 1000 a one-character measurement is the raw 1/1000-em advance.</summary>
    private static double Advance(char c, string font = StandardFonts.Helvetica)
        => StandardFontMetrics.MeasureText(c.ToString(), font, 1000);

    /// <summary>
    /// Characters the flat fallback got wrong, in both directions — an em dash is measured too
    /// narrow and a curly apostrophe too wide, so a test that only checked "not 500" could not be
    /// satisfied by a different constant.
    /// </summary>
    [Theory]
    [InlineData('—')]   // em dash — was 500, is 1000
    [InlineData('’')]   // right single quote — was 500, is 222
    [InlineData('“')]   // left double quote — was 500, is 333
    [InlineData('é')]
    [InlineData('ü')]
    [InlineData('©')]
    [InlineData('±')]
    [InlineData('€')]
    public void ANonAsciiCharacterIsMeasuredWithItsOwnAdvance(char c)
    {
        double expected = Standard14Metrics.GetWidthByUnicode(StandardFonts.Helvetica, c);

        Assert.True(expected > 0, $"fixture: Core has no width for U+{(int)c:X4}");
        Assert.Equal(expected, Advance(c), 3);
    }

    /// <summary>
    /// The em dash and the curly apostrophe are on opposite sides of the old flat 500, which is
    /// what makes the theory above a claim about real advances rather than about any single
    /// replacement constant.
    /// </summary>
    [Fact]
    public void TheOldFlatWidthWasWrongInBothDirections()
    {
        Assert.True(Advance('—') > 500, "an em dash is wider than the old flat fallback");
        Assert.True(Advance('’') < 500, "a curly apostrophe is narrower than it");
    }

    /// <summary>
    /// A run of non-ASCII text measures as the sum of its parts — the wrapping decision is made on
    /// whole runs, so a per-character fix that did not accumulate would still wrap in the wrong
    /// place.
    /// </summary>
    [Fact]
    public void ARunAccumulatesTheRealAdvances()
    {
        const string text = "café—”";
        double expected = 0;
        foreach (char c in text)
            expected += Standard14Metrics.GetWidthByUnicode(StandardFonts.Helvetica, c);

        Assert.Equal(expected / 1000 * 12, StandardFontMetrics.MeasureText(text, StandardFonts.Helvetica, 12), 6);
    }

    /// <summary>
    /// The control: ASCII is unchanged, so this is about extending coverage rather than about
    /// re-measuring everything. Courier stays monospaced at 600.
    /// </summary>
    [Fact]
    public void AsciiAndCourierAreUnchanged()
    {
        Assert.Equal(722, Advance('H'), 3);
        Assert.Equal(278, Advance(' '), 3);
        Assert.Equal(1015, Advance('@'), 3);

        Assert.Equal(600 * 3, StandardFontMetrics.MeasureText("Hé—", StandardFonts.Courier, 1000), 3);
    }

    /// <summary>
    /// A code point Core does not know still takes space rather than none — charging 0 would let an
    /// unknown glyph vanish from the measurement entirely, which is worse than the flat estimate
    /// this replaced.
    /// </summary>
    [Fact]
    public void AnUnknownCodePointStillTakesSpace()
    {
        const char unknown = '中';   // CJK — not a standard-14 glyph

        Assert.Equal(0, Standard14Metrics.GetWidthByUnicode(StandardFonts.Helvetica, unknown));
        Assert.True(Advance(unknown) > 0, "an unknown glyph must not be measured as zero-width");
    }
}
