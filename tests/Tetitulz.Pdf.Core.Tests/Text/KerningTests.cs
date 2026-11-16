// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Text;
using Xunit;
using F = Tetitulz.Pdf.TestHelpers.KerningFont;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// A producer's kerning (<see cref="TextRunOptions.Kerning"/>): the font's <c>GPOS</c> <c>kern</c>
/// pair adjustments, else its legacy <c>kern</c> table, applied to a composed run. The fixture's
/// pairs are listed on <see cref="F"/>; its em is 2000 units, so each answer here, in glyph space, is
/// half the value stored.
/// </summary>
public class KerningTests
{
    private static readonly TextRunOptions Kern = new TextRunOptions { Kerning = true };

    /// <summary>
    /// A glyph pair (format 1) changes the first glyph's advance, and its second value record the
    /// second glyph: A then V narrows A by 80 and raises V by 15.
    /// </summary>
    [Fact]
    public void AGlyphPair_NarrowsTheFirstGlyph_AndPlacesTheSecond()
    {
        ShapedGlyphRun run = Compose("AV", Kern);

        Assert.Equal(new[] { F.AGlyph, F.VGlyph }, run.GlyphIds);
        Assert.Equal((0d, 0d, -80d), Tuple(run.Placements[0]));
        Assert.Equal((0d, 15d, 0d), Tuple(run.Placements[1]));
        Assert.Equal(1120d, run.AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// A pair that only places its second glyph moves no advance, and is still written: A then o
    /// raises o by 10 — the pair set's second entry, found past its first.
    /// </summary>
    [Fact]
    public void APairThatOnlyPlaces_IsKept()
    {
        ShapedGlyphRun run = Compose("Ao", Kern);

        Assert.NotNull(run.Placements);
        Assert.True(run.Placements[0].IsIdentity);
        Assert.Equal((0d, 10d, 0d), Tuple(run.Placements[1]));
        Assert.Equal(1100d, run.AdvanceGlyphSpaceUnits);
    }

    /// <summary>The second pair set of the same subtable: T then o, −110.</summary>
    [Fact]
    public void AGlyphPair_FromAnotherPairSet()
    {
        ShapedGlyphRun run = Compose("To", Kern);

        Assert.Equal((0d, 0d, -110d), Tuple(run.Placements[0]));
        Assert.True(run.Placements[1].IsIdentity);
        Assert.Equal(990d, run.AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// A class pair (format 2): W (class 1) then a (second class 2) is −40 — the record at row 1,
    /// column 2 of a 2 × 3 matrix. W then o (second class 1) is a zero record, so nothing moves.
    /// </summary>
    [Fact]
    public void AClassPair_KernsOnlyItsClasses()
    {
        ShapedGlyphRun wa = Compose("Wa", Kern);
        Assert.Equal((0d, 0d, -40d), Tuple(wa.Placements[0]));
        Assert.Equal(1160d, wa.AdvanceGlyphSpaceUnits);

        ShapedGlyphRun wo = Compose("Wo", Kern);
        Assert.Null(wo.Placements);
        Assert.Equal(1200d, wo.AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// Off by default: the same pair, composed without <see cref="TextRunOptions.Kerning"/>, has no
    /// placements and the font's own advances, so a run written before kerning writes as it did.
    /// </summary>
    [Fact]
    public void KerningIsOffByDefault()
    {
        ShapedGlyphRun plain = Compose("AV", null);
        ShapedGlyphRun kerned = Compose("AV", Kern);

        Assert.Null(plain.Placements);
        Assert.Equal(1200d, plain.AdvanceGlyphSpaceUnits);
        Assert.NotNull(kerned.Placements);
    }

    /// <summary>
    /// Off in a run with marks too, where the run is positioned anyway: the dagesh is raised by the
    /// <c>mark</c> feature and bet then vav keeps its advances.
    /// </summary>
    [Fact]
    public void KerningIsOffByDefault_InARunWithMarks()
    {
        ShapedGlyphRun run = Compose($"{F.Bet}{F.Dagesh}{F.Vav}", null);

        Assert.Equal(new[] { F.VavGlyph, F.DageshGlyph, F.BetGlyph }, run.GlyphIds);
        Assert.True(run.Placements[0].IsIdentity);
        Assert.Equal((0d, 100d, 0d), Tuple(run.Placements[1]));
        Assert.Equal(900d, run.AdvanceGlyphSpaceUnits);
    }

    /// <summary>A run kerning leaves as it was has no placements: <c>x</c> pairs with nothing.</summary>
    [Fact]
    public void ARunNothingKerns_HasNoPlacements()
    {
        ShapedGlyphRun run = Compose("xAx", Kern);

        Assert.Null(run.Placements);
        Assert.Equal(1600d, run.AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// A font whose <c>GPOS</c> kerns the script is kerned by it alone: A then V is −80, not the
    /// legacy table's −70, nor both.
    /// </summary>
    [Fact]
    public void GposKerning_TakesPrecedenceOverTheLegacyTable()
    {
        ShapedGlyphRun run = Compose("AV", Kern, F.Build(gpos: true, legacyKern: true));

        Assert.Equal(-80d, run.Placements[0].AdvanceAdjustment);
        Assert.Equal(1120d, run.AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// With no <c>GPOS</c>, the legacy table kerns each pair as drawn: A V −70, then V A −60.
    /// </summary>
    [Fact]
    public void TheLegacyTable_KernsEachPair()
    {
        ShapedGlyphRun run = Compose("AVA", Kern, F.Build(gpos: false, legacyKern: true));

        Assert.Equal(new[] { F.AGlyph, F.VGlyph, F.AGlyph }, run.GlyphIds);
        Assert.Equal(-70d, run.Placements[0].AdvanceAdjustment);
        Assert.Equal(-60d, run.Placements[1].AdvanceAdjustment);
        Assert.True(run.Placements[2].IsIdentity);
        Assert.Equal(1670d, run.AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// Right to left, the pair's adjustment belongs to bet, the first glyph read, and narrows the gap
    /// after it in reading order — on its left, so the change is to the advance of vav, drawn left of
    /// it. Narrowing bet's own advance would close the gap on its right, at the line's start.
    /// </summary>
    [Fact]
    public void RightToLeft_TheGapAfterTheFirstGlyphRead_IsOnItsLeft()
    {
        ShapedGlyphRun run = Compose($"{F.Bet}{F.Vav}", Kern);

        Assert.Equal(new[] { F.VavGlyph, F.BetGlyph }, run.GlyphIds);
        Assert.Equal((0d, 0d, -50d), Tuple(run.Placements[0]));
        Assert.True(run.Placements[1].IsIdentity);
        Assert.Equal(850d, run.AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// A mark between the pair is skipped by the lookup, and the change goes past it to the letter:
    /// drawn, the dagesh sits between vav and bet, takes no room, and is placed from bet — so the
    /// gap is vav's. Given to the mark, it was dropped with the mark's advance. The <c>mark</c>
    /// feature still raises the dagesh: kerning joins the mark lookups, it does not replace them.
    /// </summary>
    [Fact]
    public void RightToLeft_AMarkBetweenThePair_IsSkipped()
    {
        ShapedGlyphRun run = Compose($"{F.Bet}{F.Dagesh}{F.Vav}", Kern);

        Assert.Equal(new[] { F.VavGlyph, F.DageshGlyph, F.BetGlyph }, run.GlyphIds);
        Assert.Equal((0d, 0d, -50d), Tuple(run.Placements[0]));
        Assert.Equal((0d, 100d, 0d), Tuple(run.Placements[1]));
        Assert.Equal(850d, run.AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// The legacy table's pairs are as drawn, left then right: vav bet −45 narrows vav. A mark
    /// between them is passed over.
    /// </summary>
    [Fact]
    public void TheLegacyTable_RightToLeft_KernsAsDrawn_PassingOverMarks()
    {
        byte[] legacy = F.Build(gpos: false, legacyKern: true);

        ShapedGlyphRun bare = Compose($"{F.Bet}{F.Vav}", Kern, legacy);
        Assert.Equal(new[] { F.VavGlyph, F.BetGlyph }, bare.GlyphIds);
        Assert.Equal(-45d, bare.Placements[0].AdvanceAdjustment);

        ShapedGlyphRun pointed = Compose($"{F.Bet}{F.Dagesh}{F.Vav}", Kern, legacy);
        Assert.Equal(new[] { F.VavGlyph, F.DageshGlyph, F.BetGlyph }, pointed.GlyphIds);
        Assert.Equal(-45d, pointed.Placements[0].AdvanceAdjustment);
        Assert.Equal(855d, pointed.AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// A legacy table claiming more pairs than it holds is not read: no pair kerns, and the run is
    /// composed with the font's own advances. Its control is the same table with its true count.
    /// </summary>
    [Fact]
    public void ATruncatedLegacyTable_KernsNothing()
    {
        ShapedGlyphRun truncated = Compose("AV", Kern, F.Build(gpos: false, legacyKern: true, legacyPairCount: 0xFFFF));
        Assert.Null(truncated.Placements);
        Assert.Equal(1200d, truncated.AdvanceGlyphSpaceUnits);

        ShapedGlyphRun control = Compose("AV", Kern, F.Build(gpos: false, legacyKern: true, legacyPairCount: 3));
        Assert.Equal(-70d, control.Placements[0].AdvanceAdjustment);
    }

    /// <summary>
    /// Only a horizontal, format 0 subtable that is neither a minimum table nor cross-stream kerns:
    /// vertical (0x0000), minimum (0x0003) and cross-stream (0x0005) subtables are passed over, and
    /// format 2 (0x0201) is not read. The control is the horizontal table.
    /// </summary>
    [Theory]
    [InlineData(0x0001, true)]
    [InlineData(0x0000, false)]
    [InlineData(0x0003, false)]
    [InlineData(0x0005, false)]
    [InlineData(0x0201, false)]
    public void TheLegacyTable_IsReadOnlyForPlainHorizontalPairs(int coverage, bool kerns)
    {
        ShapedGlyphRun run = Compose("AV", Kern, F.Build(gpos: false, legacyKern: true, legacyCoverage: coverage));

        if (kerns) Assert.Equal(-70d, run.Placements[0].AdvanceAdjustment);
        else Assert.Null(run.Placements);
        Assert.Equal(kerns ? 1130d : 1200d, run.AdvanceGlyphSpaceUnits);
    }

    private static ShapedGlyphRun Compose(string text, TextRunOptions options, byte[] font = null)
        => TextRunComposer.Compose(text, TrueTypeFontProgram.Parse(font ?? F.Build()), options);

    private static (double, double, double) Tuple(GlyphPlacement p) => (p.XOffset, p.YOffset, p.AdvanceAdjustment);
}
