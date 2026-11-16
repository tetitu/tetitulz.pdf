// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// Pins <see cref="Standard14Names.SystemFamily"/>, the Standard-14 → system-family map that
/// <c>Base.Rendering.FontCache</c> and the Accessibility font remediator now share.
/// </summary>
/// <remarks>
/// <para><b>Why a test rather than the comment it replaced.</b> The map existed in both callers,
/// and the second one carried a sentence saying its pairings were "deliberately the same ones
/// <c>FontCache</c> already resolves the Standard 14 through, so an embedded substitute matches
/// what this library renders". That is a real correctness requirement — if the two disagree, a
/// document gets one typeface embedded and a different one drawn — and it was enforced by nothing
/// but the sentence. Sharing one implementation is what enforces it; these tests are what stop the
/// shared one from being changed carelessly.</para>
///
/// <para><b>The non-canonicalising behaviour is pinned here because nothing else pins it.</b> It
/// reads like a missing feature: <c>FontCache</c> uses the answer as a gate, so teaching this method
/// to resolve <c>ArialMT</c> would divert every producer alias away from the name-derivation path.
/// Measured by mutation — making it canonicalise reddens six cases below and leaves <b>all 48
/// <c>PdfRendererTests</c> green</b> with host fonts installed, because that suite's four alias
/// cases (<c>ArialMT</c>, <c>ABCDEF+ArialMT</c>, <c>TimesNewRomanPSMT</c>, <c>CourierNewPSMT</c>)
/// reach the same family down either route. They assert the outcome, which both paths produce; they
/// never asserted which path ran. So these are the only tests standing between the renderer and a
/// silent change of routing, which is the reason the alias cases below look redundant and are
/// not.</para>
/// </remarks>
public class Standard14SystemFamilyTests
{
    [Theory]
    [InlineData(StandardFonts.Helvetica,            "Arial")]
    [InlineData(StandardFonts.HelveticaBold,        "Arial")]
    [InlineData(StandardFonts.HelveticaOblique,     "Arial")]
    [InlineData(StandardFonts.HelveticaBoldOblique, "Arial")]
    [InlineData(StandardFonts.TimesRoman,           "Times New Roman")]
    [InlineData(StandardFonts.TimesBold,            "Times New Roman")]
    [InlineData(StandardFonts.TimesItalic,          "Times New Roman")]
    [InlineData(StandardFonts.TimesBoldItalic,      "Times New Roman")]
    [InlineData(StandardFonts.Courier,              "Courier New")]
    [InlineData(StandardFonts.CourierBold,          "Courier New")]
    [InlineData(StandardFonts.CourierOblique,       "Courier New")]
    [InlineData(StandardFonts.CourierBoldOblique,   "Courier New")]
    [InlineData(StandardFonts.Symbol,               "Symbol")]
    [InlineData(StandardFonts.ZapfDingbats,         "ZapfDingbats")]
    [InlineData(StandardFonts.ZapfDingbatsAbbrev,   "ZapfDingbats")]
    public void EveryStandard14NameMapsToItsMetricCompatibleFamily(string standard14, string expected)
        => Assert.Equal(expected, Standard14Names.SystemFamily(standard14));

    /// <summary>
    /// The three Latin families are metric-compatible substitutes, which is the entire reason a
    /// caller may embed one under the original name and keep the document's <c>/Widths</c>. Pinning
    /// the styled variants onto the same family is therefore not redundant with the theory above:
    /// it is the claim that style is carried by <c>SKFontStyle</c>, not by a different family name.
    /// </summary>
    [Fact]
    public void AllFourVariantsOfAFamilyAgree()
    {
        Assert.Equal(Standard14Names.SystemFamily(StandardFonts.Helvetica),
                     Standard14Names.SystemFamily(StandardFonts.HelveticaBoldOblique));
        Assert.Equal(Standard14Names.SystemFamily(StandardFonts.TimesRoman),
                     Standard14Names.SystemFamily(StandardFonts.TimesBoldItalic));
        Assert.Equal(Standard14Names.SystemFamily(StandardFonts.Courier),
                     Standard14Names.SystemFamily(StandardFonts.CourierBoldOblique));
    }

    /// <summary>
    /// Not one of the fourteen means <c>null</c>, not a plausible default. <c>FontCache</c> treats a
    /// non-null answer as "take the substitute branch", so inventing a family here would silently
    /// route an arbitrary font past the name-derivation and CJK paths.
    /// </summary>
    [Theory]
    [InlineData("Calibri")]
    [InlineData("MS Gothic")]
    [InlineData("")]
    [InlineData(null)]
    public void ANameOutsideTheStandard14ResolvesToNull(string name)
        => Assert.Null(Standard14Names.SystemFamily(name));

    /// <summary>
    /// <b>The producer aliases must NOT resolve.</b> Every one of these is a real name a real
    /// producer writes, <see cref="Standard14Names.Canonical"/> maps all of them onto the Standard
    /// 14, and this method must still answer <c>null</c> — because its answer is a routing gate in
    /// <c>FontCache</c>, not a question about what the font is. A caller that wants the alias
    /// handled composes the two calls.
    /// </summary>
    [Theory]
    [InlineData("ArialMT")]
    [InlineData("Arial,Bold")]
    [InlineData("TimesNewRomanPSMT")]
    [InlineData("CourierNewPS-BoldMT")]
    [InlineData("Helv")]
    public void AProducerAliasDoesNotResolveButComposingWithCanonicalDoes(string alias)
    {
        Assert.Null(Standard14Names.SystemFamily(alias));

        // …and the composition a caller is told to spell does resolve, so the null above is a
        // deliberate routing decision rather than a gap in the map.
        Assert.NotNull(Standard14Names.SystemFamily(Standard14Names.Canonical(alias)));
    }

    /// <summary>
    /// The subset-tag form is aliasing too, and belongs on the same side of the line: callers strip
    /// the tag before asking. Pinned separately because <c>FontCache</c> strips it a few lines
    /// earlier and it would be easy to conclude this method should.
    /// </summary>
    [Fact]
    public void ASubsetTaggedNameDoesNotResolveUntilTheTagIsStripped()
    {
        Assert.Null(Standard14Names.SystemFamily("ABCDEF+" + StandardFonts.Helvetica));
        Assert.Equal("Arial", Standard14Names.SystemFamily(SubsetTag.Strip("ABCDEF+" + StandardFonts.Helvetica)));
    }
}
