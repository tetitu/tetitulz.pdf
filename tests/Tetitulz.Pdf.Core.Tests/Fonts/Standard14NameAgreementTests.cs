// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="Standard14Names.Canonical"/> maps a producer-written font name onto one of the
/// fourteen, and the answer selects an AFM width table — so a wrong answer lays text out at the
/// wrong advances.
/// </summary>
/// <remarks>
/// <para>Two independent implementations of this rule existed, in the Accessibility font remediator
/// and in the PostScript plugin's <c>PsFontMapper</c>. They disagreed on ordinary input:</para>
/// <list type="bullet">
/// <item><c>TimesNewRomanPSMT</c> — one stripped the <c>psmt</c> suffix and answered Times-Roman;
/// the other missed its family table, fell into a style-word stripper that cut at <c>"roman"</c>
/// leaving <c>timesnew</c>, missed again, and answered <b>Helvetica</b>. A PostScript program
/// naming that font had its text emitted as <c>/BaseFont /Helvetica</c> and measured against the
/// Helvetica metrics.</item>
/// <item>The AcroForm short names <c>Cour</c> and <c>TiRo</c> were mapped by one and absent from
/// the other.</item>
/// </list>
/// </remarks>
public class Standard14NameAgreementTests
{
    /// <summary>
    /// <b>The defect.</b> The PostScript-suffixed spellings real producers write.
    /// </summary>
    [Theory]
    [InlineData("TimesNewRomanPSMT", StandardFonts.TimesRoman)]
    [InlineData("TimesNewRomanPS-BoldMT", StandardFonts.TimesBold)]
    [InlineData("TimesNewRomanPS-ItalicMT", StandardFonts.TimesItalic)]
    [InlineData("TimesNewRomanPS-BoldItalicMT", StandardFonts.TimesBoldItalic)]
    [InlineData("CourierNewPSMT", StandardFonts.Courier)]
    [InlineData("CourierNewPS-BoldMT", StandardFonts.CourierBold)]
    [InlineData("ArialMT", StandardFonts.Helvetica)]
    [InlineData("Arial-BoldMT", StandardFonts.HelveticaBold)]
    public void APostScriptSuffixedNameResolvesToItsRealFamily(string baseFont, string expected)
        => Assert.Equal(expected, Standard14Names.Canonical(baseFont));

    /// <summary>The AcroForm short names, which one of the two bodies had never heard of.</summary>
    [Theory]
    [InlineData("Helv", StandardFonts.Helvetica)]
    [InlineData("TiRo", StandardFonts.TimesRoman)]
    [InlineData("Cour", StandardFonts.Courier)]
    [InlineData("ZaDb", StandardFonts.ZapfDingbats)]
    public void AnAcroFormShortNameResolves(string baseFont, string expected)
        => Assert.Equal(expected, Standard14Names.Canonical(baseFont));

    /// <summary>The plain spellings, and the comma and hyphen style separators.</summary>
    [Theory]
    [InlineData("Helvetica", StandardFonts.Helvetica)]
    [InlineData("Helvetica-Bold", StandardFonts.HelveticaBold)]
    [InlineData("Arial,Bold", StandardFonts.HelveticaBold)]
    [InlineData("Arial,BoldItalic", StandardFonts.HelveticaBoldOblique)]
    [InlineData("Times-Roman", StandardFonts.TimesRoman)]
    [InlineData("Times New Roman", StandardFonts.TimesRoman)]
    [InlineData("Courier-Oblique", StandardFonts.CourierOblique)]
    [InlineData("Symbol", StandardFonts.Symbol)]
    [InlineData("ZapfDingbats", StandardFonts.ZapfDingbats)]
    public void APlainSpellingResolves(string baseFont, string expected)
        => Assert.Equal(expected, Standard14Names.Canonical(baseFont));

    /// <summary>A subset tag is stripped first, so a tagged standard-14 name still resolves.</summary>
    [Theory]
    [InlineData("ABCDEF+Times-Roman", StandardFonts.TimesRoman)]
    [InlineData("ZZZZZZ+TimesNewRomanPSMT", StandardFonts.TimesRoman)]
    public void ATaggedNameResolves(string baseFont, string expected)
        => Assert.Equal(expected, Standard14Names.Canonical(baseFont));

    /// <summary>
    /// <b>The floor.</b> A font that is genuinely not one of the fourteen must answer <c>null</c> —
    /// without this the rule could be "return Helvetica", which is the defect itself.
    /// </summary>
    [Theory]
    [InlineData("Garamond")]
    [InlineData("DejaVuSans")]
    [InlineData("Wingdings")]
    [InlineData("MinionPro-Regular")]
    [InlineData("")]
    [InlineData(null)]
    public void AFontThatIsNotStandard14AnswersNull(string? baseFont)
        => Assert.Null(Standard14Names.Canonical(baseFont));

    /// <summary>
    /// Every answer is a name <see cref="Standard14Metrics"/> recognises — the property that makes
    /// the result usable, since selecting a width table is the whole point.
    /// </summary>
    [Theory]
    [InlineData("TimesNewRomanPSMT")]
    [InlineData("Cour")]
    [InlineData("Arial-BoldMT")]
    [InlineData("Symbol")]
    [InlineData("ZaDb")]
    public void EveryAnswerIsAKnownStandard14Name(string baseFont)
    {
        string canonical = Standard14Names.Canonical(baseFont);

        Assert.NotNull(canonical);
        Assert.True(Standard14Metrics.IsStandard14(canonical),
            $"'{canonical}' must be a name the metrics table knows, or it selects nothing");
    }
}
