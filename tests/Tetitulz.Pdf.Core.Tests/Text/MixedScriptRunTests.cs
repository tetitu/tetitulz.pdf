// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Text;
using Xunit;
using F = Tetitulz.Pdf.TestHelpers.KerningFont;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// A run of several scripts takes each script's lookups segment by segment (GAPS.md §19c): a Latin
/// word beside Hebrew is kerned under <c>latn</c> while the Hebrew letters are kerned and pointed
/// under <c>hebr</c>. Before, the whole run was looked up under its first non-Latin script, so the
/// Latin word lost its kerning.
/// </summary>
/// <remarks>
/// <see cref="F"/> registers Latin kerning (A then V: −80) under <c>latn</c> — or another tag — and
/// Hebrew kerning (bet then vav: −50, marks skipped) and a <c>mark</c> feature raising every dagesh
/// by 100 under <c>hebr</c>; its advances are A 600, V 600, the space 250, bet 600, vav 300, the dagesh 0.
/// Each claim below has its arithmetic beside it, and the one-script controls are the existing
/// <c>KerningTests</c> and <c>ScriptTagTests</c> answers restated.
/// </remarks>
public class MixedScriptRunTests
{
    private static readonly TextRunOptions Kern = new TextRunOptions { Kerning = true };

    private static ShapedGlyphRun Compose(string text, byte[] font, TextRunOptions options = null)
        => TextRunComposer.Compose(text, TrueTypeFontProgram.Parse(font), options ?? Kern);

    /// <summary>
    /// "AV בּו": A V (−80 under <c>latn</c>), a space, bet dagesh vav (−50 under <c>hebr</c>, the
    /// dagesh raised 100): 600 + 600 + 250 + 600 + 0 + 300 − 80 − 50 = 2220. Under one <c>hebr</c>
    /// tag the Latin pair was not kerned: 2300.
    /// </summary>
    [Fact]
    public void ALatinWordBesideHebrew_IsKernedUnderLatin_WhileTheHebrewIsKernedAndPointedUnderHebrew()
    {
        ShapedGlyphRun run = Compose($"AV {F.Bet}{F.Dagesh}{F.Vav}", F.Build());

        Assert.Equal(2220d, run.AdvanceGlyphSpaceUnits);

        // The Latin pair: A's advance narrowed, V raised — latn's lookup, applied in a run whose
        // first scripted character is Hebrew.
        int a = Array.IndexOf(run.GlyphIds, F.AGlyph), v = Array.IndexOf(run.GlyphIds, F.VGlyph);
        Assert.Equal(-80d, run.Placements[a].AdvanceAdjustment);
        Assert.Equal(15d, run.Placements[v].YOffset);

        // The Hebrew: the bet-vav pair's −50 widens the gap after bet in reading order, which in the
        // right-to-left run is on the vav's side — the glyph drawn on bet's left — as the
        // single-script kerning tests have it; the dagesh is raised by the mark feature.
        int vav = Array.IndexOf(run.GlyphIds, F.VavGlyph), dagesh = Array.IndexOf(run.GlyphIds, F.DageshGlyph);
        Assert.Equal(-50d, run.Placements[vav].AdvanceAdjustment);
        Assert.Equal(100d, run.Placements[dagesh].YOffset);
    }

    /// <summary>The one-script runs answer as they always did: 1120 for AV, 850 for bet dagesh vav.</summary>
    [Fact]
    public void ARunOfOneScript_IsOneSegment()
    {
        Assert.Equal(1120d, Compose("AV", F.Build()).AdvanceGlyphSpaceUnits);
        Assert.Equal(850d, Compose($"{F.Bet}{F.Dagesh}{F.Vav}", F.Build()).AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// A font registering its Latin pairs under <c>cyrl</c> alone: "АВ AV" kerns the Cyrillic (1120),
    /// the space (250) joins the Cyrillic segment, and the Latin word finds no <c>latn</c>, <c>DFLT</c>
    /// or <c>dflt</c> and stays 1200 — 2570, where one <c>cyrl</c> tag over the run kerned both (2490).
    /// A leading space joins the first scripted glyph after it: " АВ" is one segment, 250 + 1120.
    /// </summary>
    [Fact]
    public void AGlyphOfNoScript_JoinsItsNeighboursSegment_AndEachSegmentFallsBackOnItsOwn()
    {
        byte[] cyrillicKerning = F.Build(latinScriptTag: "cyrl");

        Assert.Equal(2570d, Compose($"{F.CyrillicA}{F.CyrillicVe} AV", cyrillicKerning).AdvanceGlyphSpaceUnits);
        Assert.Equal(1370d, Compose($" {F.CyrillicA}{F.CyrillicVe}", cyrillicKerning).AdvanceGlyphSpaceUnits);

        // The control: registered under latn, the Cyrillic segment kerns through the chain's last
        // step and the Latin one directly — both pairs, 2490.
        Assert.Equal(2490d, Compose($"{F.CyrillicA}{F.CyrillicVe} AV", F.Build()).AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// The legacy <c>kern</c> table, with no <c>GPOS</c> at all, still reaches every segment: A V −70
    /// and, as drawn, vav bet −45 — 2350 − 70 − 45 = 2235 for "AV בּו".
    /// </summary>
    [Fact]
    public void TheLegacyKernTable_ReachesEverySegmentWhoseScriptHasNoGposKerning()
    {
        ShapedGlyphRun run = Compose($"AV {F.Bet}{F.Dagesh}{F.Vav}", F.Build(gpos: false, legacyKern: true));
        Assert.Equal(2235d, run.AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// A mark in a second segment attaches to its base in that segment: "a" then bet and qamats
    /// (<c>MarkAttachmentFont</c>) draws the qamats at bet's anchor, (150, −50) from bet's origin,
    /// taking no room — the base's index in the run, not in the segment. A parent index left
    /// unmoved by the segment's start would hang the qamats from the <c>a</c>.
    /// </summary>
    [Fact]
    public void AMarkInASecondSegment_AttachesToItsBaseInThatSegment()
    {
        var font = TrueTypeFontProgram.Parse(Tetitulz.Pdf.TestHelpers.MarkAttachmentFont.Build());
        ShapedGlyphRun run = TextRunComposer.Compose(
            $"a{Tetitulz.Pdf.TestHelpers.MarkAttachmentFont.Bet}{Tetitulz.Pdf.TestHelpers.MarkAttachmentFont.Qamats}",
            font, new TextRunOptions { BaseDirection = BaseDirection.LeftToRight });

        Assert.Equal(new[] { Tetitulz.Pdf.TestHelpers.MarkAttachmentFont.LatinGlyph,
                             Tetitulz.Pdf.TestHelpers.MarkAttachmentFont.QamatsGlyph,
                             Tetitulz.Pdf.TestHelpers.MarkAttachmentFont.BetGlyph }, run.GlyphIds);
        GlyphPlacement qamats = run.Placements[1];
        Assert.Equal((150d, -50d, -300d), (qamats.XOffset, qamats.YOffset, qamats.AdvanceAdjustment));
        Assert.Equal(500d + 600d, run.AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// A ligature in a second segment is formed and its sources are the run's indices: "αfi"
    /// (<c>LigatureFont</c>, alpha sharing the <c>a</c> glyph) is the <c>a</c> glyph then the fi
    /// ligature whose text is "fi"; and "fiα", the ligature first and a one-for-one segment after
    /// it, keeps the alpha as itself. A source index left unmoved would name the wrong letters.
    /// </summary>
    [Fact]
    public void ALigatureInASecondSegment_FormsWithItsSourcesInTheRunsIndices()
    {
        var font = TrueTypeFontProgram.Parse(Tetitulz.Pdf.TestHelpers.LigatureFont.Build());
        var liga = new TextRunOptions { Ligatures = true, BaseDirection = BaseDirection.LeftToRight };

        ShapedGlyphRun after = TextRunComposer.Compose("αfi", font, liga);
        Assert.Equal(new[] { Tetitulz.Pdf.TestHelpers.LigatureFont.AGlyph, Tetitulz.Pdf.TestHelpers.LigatureFont.FiGlyph }, after.GlyphIds);
        Assert.Equal("fi", after.Glyphs[1].Text);
        Assert.Equal(0x03B1, after.Glyphs[0].CodePoint);
        Assert.Equal(480d + 520d, after.AdvanceGlyphSpaceUnits);

        ShapedGlyphRun before = TextRunComposer.Compose("fiα", font, liga);
        Assert.Equal(new[] { Tetitulz.Pdf.TestHelpers.LigatureFont.FiGlyph, Tetitulz.Pdf.TestHelpers.LigatureFont.AGlyph }, before.GlyphIds);
        Assert.Equal("fi", before.Glyphs[0].Text);
        Assert.Equal(0x03B1, before.Glyphs[1].CodePoint);
    }

    /// <summary>
    /// A mixed run's placements line up with its glyphs: as many placements as glyphs, and the glyphs
    /// of the Hebrew segment stand in visual order (vav, dagesh, bet) after the Latin ones.
    /// </summary>
    [Fact]
    public void AMixedRunsGlyphsAndPlacements_StayAligned()
    {
        ShapedGlyphRun run = Compose($"AV {F.Bet}{F.Dagesh}{F.Vav}", F.Build());

        Assert.Equal(run.GlyphIds.Length, run.Placements.Count);
        Assert.Equal(new[] { F.AGlyph, F.VGlyph, F.SpaceGlyph, F.VavGlyph, F.DageshGlyph, F.BetGlyph }, run.GlyphIds);
        Assert.Equal(run.GlyphIds.Length, run.Glyphs.Count);
    }
}
