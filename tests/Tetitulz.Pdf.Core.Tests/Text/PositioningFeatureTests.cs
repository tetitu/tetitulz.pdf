// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Text;
using Xunit;
using K = Tetitulz.Pdf.TestHelpers.KerningFont;
using M = Tetitulz.Pdf.TestHelpers.MarkAttachmentFont;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// Which <c>GPOS</c> features a run takes without being asked: <c>mark</c> and <c>mkmk</c> as before,
/// and now the Indic <c>abvm</c> and <c>blwm</c> (above- and below-base mark positioning, where a
/// Devanagari or Bengali font keeps its anchors) and <c>dist</c> (the pair adjustment Indic and
/// Khmer fonts space glyphs with). A shaper applies all of them unasked; <c>kern</c> alone waits for
/// the producer. The reader took only <c>mark</c>, <c>mkmk</c>, <c>curs</c> and <c>kern</c>, so an
/// Indic font's marks sat at their pen positions and its spacing pairs were never applied.
/// </summary>
public class PositioningFeatureTests
{
    /// <summary>
    /// A font with its mark-to-base lookups under <c>abvm</c> or <c>blwm</c> places the mark exactly as
    /// one listing them under <c>mark</c> does — the same placements, glyph for glyph.
    /// </summary>
    [Theory]
    [InlineData("abvm")]
    [InlineData("blwm")]
    public void IndicMarkFeatures_PlaceTheMark_AsMarkDoes(string feature)
    {
        ShapedGlyphRun under = Compose(M.Build(markFeature: feature), $"{M.Bet}{M.Qamats}");
        ShapedGlyphRun control = Compose(M.Build(), $"{M.Bet}{M.Qamats}");

        Assert.NotNull(under.Placements);
        Assert.Equal((150d, -50d, -300d), Tuple(under.Placements[0]));
        Assert.Equal(control.Placements.Select(Tuple), under.Placements.Select(Tuple));
        Assert.Equal(600d, under.AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// The Latin pairs under <c>dist</c> apply with no kerning asked for — A then V is 80 narrower
    /// (1120) — where the same pairs under <c>kern</c> wait for the option (1200 unasked, 1120 asked).
    /// </summary>
    [Fact]
    public void Dist_AppliesUnasked_WhereKernWaitsForTheOption()
    {
        var dist = TrueTypeFontProgram.Parse(K.Build(latinKernFeature: "dist"));
        var kern = TrueTypeFontProgram.Parse(K.Build());

        Assert.Equal(1120d, TextRunComposer.Compose("AV", dist, null).AdvanceGlyphSpaceUnits);
        Assert.Equal(1120d, TextRunComposer.Compose("AV", dist, new TextRunOptions { Kerning = true }).AdvanceGlyphSpaceUnits);
        Assert.Equal(1200d, TextRunComposer.Compose("AV", kern, null).AdvanceGlyphSpaceUnits);
        Assert.Equal(1120d, TextRunComposer.Compose("AV", kern, new TextRunOptions { Kerning = true }).AdvanceGlyphSpaceUnits);
    }

    /// <summary>The <c>dist</c> adjustment reaches the placements like a kern: V's placement carries the A's narrowing.</summary>
    [Fact]
    public void Dist_IsWrittenAsAPlacement()
    {
        ShapedGlyphRun run = Compose(K.Build(latinKernFeature: "dist"), "AV");

        Assert.NotNull(run.Placements);
        Assert.Equal(-80d, run.Placements[0].AdvanceAdjustment);
        Assert.Equal(15d, run.Placements[1].YOffset);
    }

    private static ShapedGlyphRun Compose(byte[] font, string text)
        => TextRunComposer.Compose(text, TrueTypeFontProgram.Parse(font), null);

    private static (double, double, double) Tuple(GlyphPlacement p) => (p.XOffset, p.YOffset, p.AdvanceAdjustment);
}
