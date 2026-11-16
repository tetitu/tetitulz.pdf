// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Text;
using Xunit;
using F = Tetitulz.Pdf.TestHelpers.RcltFont;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// A font's required contextual alternates (<c>GSUB</c> <c>rclt</c>): one glyph for another, by
/// context, before positioning. Dubai draws a beh before a final yeh as an alternate that sits on the
/// yeh, and its cursive joins are on the alternates; before <c>rclt</c> was read, neither applied.
/// See <see cref="F"/>.
/// </summary>
public class ContextualAlternatesTests
{
    /// <summary>
    /// Beh then yeh: the beh is drawn as its alternate in every shape that reaches it — chained and
    /// plain context rules, an extension lookup, a single substitution by array or by delta. The run's
    /// width is the alternate's (300) and the yeh's (450), and the alternate still maps back to the
    /// beh — <c>/ToUnicode</c> keeps the letter that was typed.
    /// </summary>
    [Theory]
    [InlineData(F.Form.Chain)]
    [InlineData(F.Form.Context)]
    [InlineData(F.Form.Extension)]
    [InlineData(F.Form.Single1)]
    [InlineData(F.Form.Single2)]
    public void BehBeforeYeh_IsDrawnAsTheAlternate(F.Form form)
    {
        ShapedGlyphRun run = Compose(F.Build(form), "بي");

        Assert.Equal(new[] { F.FinalYehGlyph, F.AlternateGlyph }, run.GlyphIds);
        Assert.Equal(new[] { 0x064A, 0x0628 }, run.Glyphs.Select(g => g.CodePoint));
        Assert.Equal(new[] { F.FinalYehGlyph, F.AlternateGlyph }, run.Glyphs.Select(g => g.GlyphId));
        Assert.Equal(750d, run.AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// The rule's context decides: a beh before another beh is not followed by a yeh, so the chained
    /// and plain context rules leave it alone. The unconditional single substitution is the control —
    /// it substitutes there too.
    /// </summary>
    [Theory]
    [InlineData(F.Form.Chain, F.InitialBehGlyph)]
    [InlineData(F.Form.Context, F.InitialBehGlyph)]
    [InlineData(F.Form.Single2, F.AlternateGlyph)]
    public void WithoutTheContext_TheBehStays(F.Form form, ushort expected)
    {
        ShapedGlyphRun run = Compose(F.Build(form), "بب");

        Assert.Equal(new[] { F.FinalBehGlyph, expected }, run.GlyphIds);
    }

    /// <summary>
    /// <c>calt</c> is a feature a user can turn off, and Word leaves it off: it applies only when the
    /// producer asks (<see cref="TextRunOptions.ContextualAlternates"/>). The same rule under
    /// <c>rclt</c> substitutes either way.
    /// </summary>
    [Fact]
    public void Calt_AppliesOnlyWhenAsked_RcltAlways()
    {
        var asked = new TextRunOptions { ContextualAlternates = true };
        Assert.Equal(F.InitialBehGlyph, Compose(F.Build(feature: "calt"), "بي").GlyphIds[1]);
        Assert.Equal(F.AlternateGlyph, Compose(F.Build(feature: "calt"), "بي", asked).GlyphIds[1]);
        Assert.Equal(F.AlternateGlyph, Compose(F.Build(feature: "rclt"), "بي").GlyphIds[1]);
        Assert.Equal(F.AlternateGlyph, Compose(F.Build(feature: "rclt"), "بي", asked).GlyphIds[1]);
    }

    /// <summary>
    /// Substitution comes before positioning: the cursive join is on the alternate only, so it applies
    /// because the beh became the alternate first. The yeh's advance runs to where its entry (430) meets
    /// the alternate's exit (10), 420; the alternate hangs 100 lower. Without <c>rclt</c> read (the
    /// <c>calt</c> font) the beh stays and nothing joins.
    /// </summary>
    [Fact]
    public void ACursiveJoinOnTheAlternate_AppliesAfterTheSubstitution()
    {
        ShapedGlyphRun run = Compose(F.Build(cursiveOnAlternate: true), "بي");
        Assert.Equal(new[] { F.FinalYehGlyph, F.AlternateGlyph }, run.GlyphIds);
        Assert.Equal(new[] { (0d, 0d, -30d), (0d, -100d, 0d) },
                     run.Placements.Select(p => (p.XOffset, p.YOffset, p.AdvanceAdjustment)));
        Assert.Equal(720d, run.AdvanceGlyphSpaceUnits);

        ShapedGlyphRun unjoined = Compose(F.Build(feature: "calt", cursiveOnAlternate: true), "بي");
        Assert.Null(unjoined.Placements);
        Assert.Equal(850d, unjoined.AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// A substituted glyph takes its own <c>GDEF</c> class: the alternate is a mark, so a later lookup
    /// that ignores marks passes it by and it stays the alternate. Read with the beh's class, the later
    /// lookup would turn it into the final beh.
    /// </summary>
    [Fact]
    public void ASubstitutedGlyph_TakesItsOwnClass()
    {
        ShapedGlyphRun run = Compose(F.Build(markAlternate: true), "بي");

        Assert.Equal(new[] { F.FinalYehGlyph, F.AlternateGlyph }, run.GlyphIds);
    }

    private static ShapedGlyphRun Compose(byte[] font, string text, TextRunOptions options = null)
        => TextRunComposer.Compose(text, TrueTypeFontProgram.Parse(font), options);
}
