// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using F = Tetitulz.Pdf.TestHelpers.MarkAttachmentFont;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// A producer's mark attachment (<c>GPOS</c> <c>mark</c>/<c>mkmk</c>, GAPS.md §19c): a Hebrew point
/// is drawn where its letter's anchor says and takes no room. The fixture's anchors put qamats
/// (150, −50) from bet's origin, dagesh (250, 250), and qamats on dagesh (150, −150) — see
/// <see cref="MarkAttachmentFont"/>.
/// </summary>
/// <remarks>
/// Reordering reverses a right-to-left run, so the point comes <b>before</b> its letter in the
/// glyph order and both start at the same pen position; the placement is therefore the anchor
/// offset itself. Without attachment the point sat at the letter's left edge and, carrying an
/// advance of 300, pushed it 300 units right.
/// </remarks>
public class MarkAttachmentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMark_IsDrawnAtItsBasesAnchor_AndTakesNoRoom(bool extension)
    {
        var font = TrueTypeFontProgram.Parse(F.Build(extension));
        ShapedGlyphRun run = TextRunComposer.Compose($"{F.Bet}{F.Qamats}", font, null);

        Assert.Equal(new[] { F.QamatsGlyph, F.BetGlyph }, run.GlyphIds);
        Assert.NotNull(run.Placements);
        Assert.Equal((150d, -50d, -300d), Tuple(run.Placements[0]));
        Assert.True(run.Placements[1].IsIdentity);
        Assert.Equal(600d, run.AdvanceGlyphSpaceUnits);
        Assert.Equal(new[] { (int)F.Qamats, (int)F.Bet }, run.Glyphs.Select(g => g.CodePoint));
    }

    /// <summary>A mark after a mark attaches by the mark-to-mark anchor: qamats on dagesh, which is on bet.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMarkOnAMark_IsDrawnAtTheFirstMarksAnchor(bool extension)
    {
        var font = TrueTypeFontProgram.Parse(F.Build(extension));
        ShapedGlyphRun run = TextRunComposer.Compose($"{F.Bet}{F.Dagesh}{F.Qamats}", font, null);

        Assert.Equal(new[] { F.QamatsGlyph, F.DageshGlyph, F.BetGlyph }, run.GlyphIds);
        Assert.Equal((150d, -150d, -300d), Tuple(run.Placements[0]));
        Assert.Equal((250d, 250d, 0d), Tuple(run.Placements[1]));
        Assert.True(run.Placements[2].IsIdentity);
    }

    /// <summary>
    /// A second mark the mark-to-mark lookup does not cover attaches to the base, looking past the
    /// mark before it: dagesh after qamats is on bet at (250, 250), as it is when it comes first.
    /// </summary>
    [Fact]
    public void ASecondMarkWithNoMarkAnchor_AttachesToTheBase()
    {
        var font = TrueTypeFontProgram.Parse(F.Build());
        ShapedGlyphRun run = TextRunComposer.Compose($"{F.Bet}{F.Qamats}{F.Dagesh}", font, null);

        Assert.Equal(new[] { F.DageshGlyph, F.QamatsGlyph, F.BetGlyph }, run.GlyphIds);
        Assert.Equal((250d, 250d, 0d), Tuple(run.Placements[0]));
        Assert.Equal((150d, -50d, -300d), Tuple(run.Placements[1]));
    }

    /// <summary>
    /// A chained-context rule positions a mark through lookups no feature lists: holam after bet is
    /// anchored by the nested mark-to-base lookup at (500, 700), then moved (20, 10) by the nested
    /// single adjustment — (520, 710). After a Hebrew letter the rule's backtrack does not cover
    /// (aleph, drawn with the fixture's plain glyph), nothing positions it: it stays at its pen
    /// position. The control is Hebrew so the font's Hebrew lookups run at all.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AContextRule_AppliesItsNestedLookupsAtTheMark(bool extension)
    {
        var font = TrueTypeFontProgram.Parse(F.Build(extension, alsoCovers: "א"));
        ShapedGlyphRun run = TextRunComposer.Compose($"{F.Bet}{F.Holam}", font, null);
        Assert.Equal(new[] { F.HolamGlyph, F.BetGlyph }, run.GlyphIds);
        Assert.Equal((520d, 710d, 0d), Tuple(run.Placements[0]));

        ShapedGlyphRun after = TextRunComposer.Compose($"א{F.Holam}", font, null);
        Assert.Equal(new[] { F.HolamGlyph, F.LatinGlyph }, after.GlyphIds);
        Assert.Equal((0d, 0d, 0d), Tuple(after.Placements[0]));
    }

    /// <summary>
    /// A lookup that skips marks matches across them: the rule raising the <c>a</c> glyph after bet
    /// matches after bet and qamats, because the qamats is skipped — the glyph is raised 40 — and
    /// does not match after a glyph that is not bet.
    /// </summary>
    /// <remarks>
    /// The glyph is reached through the middle dot (U+00B7), a character of no script, so it stays
    /// in the Hebrew segment the rule runs over: a run is looked up script by script
    /// (<c>MixedScriptRunTests</c>), and a Latin <c>a</c> after bet would be its own <c>latn</c>
    /// segment, which a Hebrew rule does not see — as a shaper's itemizer keeps them apart too.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AContextLookupThatSkipsMarks_MatchesAcrossThem(bool extension)
    {
        var font = TrueTypeFontProgram.Parse(F.Build(extension, alsoCovers: "·"));
        ShapedGlyphRun run = TextRunComposer.Compose($"{F.Bet}{F.Qamats}·", font, new TextRunOptions { BaseDirection = BaseDirection.LeftToRight });

        int a = System.Array.IndexOf(run.GlyphIds, F.LatinGlyph);
        Assert.Equal((0d, 40d, 30d), Tuple(run.Placements[a]));
        Assert.Equal(600d + 500d + 30d, run.AdvanceGlyphSpaceUnits);   // bet, the glyph widened by 30; the qamats takes none

        ShapedGlyphRun control = TextRunComposer.Compose($"·{F.Qamats}·", font, new TextRunOptions { BaseDirection = BaseDirection.LeftToRight });
        Assert.All(control.Placements, p => Assert.Equal(0d, p.YOffset));
    }

    /// <summary>
    /// A hostile font whose context rule calls its own lookup is stopped by the nesting bound rather
    /// than recursing without end: the run composes, the rule's lookup positioning nothing.
    /// </summary>
    [Theory]
    [InlineData(1)]     // depth: the rule calls itself once per level, and only the nesting bound stops it
    [InlineData(50)]    // breadth: fifty calls per level would be 50^8 trials, and only the step bound stops it
    public void HostileSelfCallingContextRule_IsBounded(int records)
    {
        var font = TrueTypeFontProgram.Parse(F.Build(gpos: SelfCallingGpos(records)));

        ShapedGlyphRun run = TextRunComposer.Compose($"{F.Bet}{F.Holam}", font, null);
        Assert.Equal(new[] { F.HolamGlyph, F.BetGlyph }, run.GlyphIds);
        Assert.True(run.Placements[0].IsIdentity);
    }

    /// <summary>
    /// A <c>GPOS</c> of one script (hebr), one feature (mark → lookup 0) and one lookup: a chained
    /// context rule, format 3, input [holam], with <paramref name="records"/> records each calling
    /// lookup 0 — itself — at the holam.
    /// </summary>
    private static byte[] SelfCallingGpos(int records)
    {
        int coverage = 14 + 4 * records;
        var words = new System.Collections.Generic.List<int>
        {
            1, 0, 10, 30, 44,
            /* ScriptList */ 1, 0x6865, 0x6272, 8, /* Script */ 4, 0, /* LangSys */ 0, 0xFFFF, 1, 0,
            /* FeatureList */ 1, 0x6D61, 0x726B, 8, /* Feature */ 0, 1, 0,
            /* LookupList */ 1, 4, /* Lookup */ 8, 0, 1, 8,
            /* ChainContext 3 */ 3, 0, 1, coverage, 0, records,
        };
        for (int i = 0; i < records; i++) { words.Add(0); words.Add(0); }
        words.AddRange(new[] { /* Coverage */ 1, 1, (int)F.HolamGlyph });
        var gpos = new byte[words.Count * 2];
        for (int i = 0; i < words.Count; i++) { gpos[2 * i] = (byte)(words[i] >> 8); gpos[2 * i + 1] = (byte)words[i]; }
        return gpos;
    }

    /// <summary>
    /// In a left-to-right run a mark follows its base; one the lookups do not cover for its base
    /// (here after a Latin letter, a script the font has no lookups for) stays at its pen position
    /// and still takes no room.
    /// </summary>
    [Fact]
    public void AMarkWithNoAnchor_StaysAtItsPenPosition_AndTakesNoRoom()
    {
        var font = TrueTypeFontProgram.Parse(F.Build());
        ShapedGlyphRun run = TextRunComposer.Compose($"a{F.Qamats}", font, new TextRunOptions { BaseDirection = BaseDirection.LeftToRight });

        Assert.Equal(new[] { F.LatinGlyph, F.QamatsGlyph }, run.GlyphIds);
        Assert.True(run.Placements[0].IsIdentity);
        Assert.Equal((0d, 0d, -300d), Tuple(run.Placements[1]));
        Assert.Equal(500d, run.AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// What does not change: a run with no marks, and any run in a font with no <c>GDEF</c>, has no
    /// placements and its font advances — the composition every producer had before.
    /// </summary>
    [Fact]
    public void WithoutMarksOrGdef_NothingIsPlaced()
    {
        var font = TrueTypeFontProgram.Parse(F.Build());
        ShapedGlyphRun plain = TextRunComposer.Compose($"{F.Bet}{F.Bet}", font, null);
        Assert.Null(plain.Placements);
        Assert.Equal(1200d, plain.AdvanceGlyphSpaceUnits);

        var noGdef = TrueTypeFontProgram.Parse(F.Build(withGdef: false));
        ShapedGlyphRun marked = TextRunComposer.Compose($"{F.Bet}{F.Qamats}", noGdef, null);
        Assert.Null(marked.Placements);
        Assert.Equal(900d, marked.AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// Hostile <c>GPOS</c> tables — offsets past the table, a lookup list pointing at itself, an
    /// extension pointing outside the font — leave the font with no positioning rather than
    /// throwing or looping: the run is composed as if there were none.
    /// </summary>
    [Theory]
    [InlineData(new ushort[] { 1, 0, 0xFFF0, 0xFFF0, 0xFFF0 })]
    [InlineData(new ushort[] { 1, 0, 10, 10, 10, 0xFFFF, 0xFFFF })]
    [InlineData(new ushort[] { 1, 0, 10, 18, 24, 1, 0x6865, 0x6272, 8, 4, 0, 0, 0xFFFF, 1, 0, 1, 0x6D61, 0x726B, 8, 0, 1, 0, 1, 4, 9, 0, 1, 8, 1, 4, 0x7FFF, 0xFFFF })]
    public void HostileGpos_LeavesTheRunUnplaced(ushort[] words)
    {
        var gpos = new byte[words.Length * 2];
        for (int i = 0; i < words.Length; i++) { gpos[2 * i] = (byte)(words[i] >> 8); gpos[2 * i + 1] = (byte)words[i]; }
        var font = TrueTypeFontProgram.Parse(F.Build(gpos: gpos));

        ShapedGlyphRun run = TextRunComposer.Compose($"{F.Bet}{F.Qamats}", font, null);
        Assert.Equal(new[] { F.QamatsGlyph, F.BetGlyph }, run.GlyphIds);
        Assert.Null(run.Placements);
        Assert.Equal(900d, run.AdvanceGlyphSpaceUnits);
    }

    // ------------------------------------------------------------------ the writer

    /// <summary>A run with no placements is the <c>Tj</c> every producer wrote.</summary>
    [Fact]
    public void ARunWithoutPlacements_IsOneTj()
    {
        var font = TrueTypeFontProgram.Parse(F.Build());
        Assert.Equal("<00010001> Tj", GlyphRunShowText.Operators(TextRunComposer.Compose($"{F.Bet}{F.Bet}", font, null), 10));
    }

    /// <summary>
    /// A raised or lowered glyph is shown alone under a text rise restored after it, its horizontal
    /// offset a <c>TJ</c> adjustment before it and its return — with the advance it does not take —
    /// one after: qamats at (150, −50) of a 10-point em is a rise of −0.5, −150 before and
    /// 150 + 300 after.
    /// </summary>
    [Fact]
    public void APlacedMark_IsATextRiseAndTwoAdjustments()
    {
        var font = TrueTypeFontProgram.Parse(F.Build());
        ShapedGlyphRun run = TextRunComposer.Compose($"{F.Bet}{F.Qamats}", font, null);

        Assert.Equal("-0.5 Ts\n[-150 <0002> 450] TJ\n0 Ts\n<0001> Tj", GlyphRunShowText.Operators(run, 10));
        Assert.Equal("1.5 Ts\n[-150 <0002> 450] TJ\n2 Ts\n<0001> Tj", GlyphRunShowText.Operators(run, 10, rise: 2));
    }

    /// <summary>A glyph moved only sideways stays in the surrounding <c>TJ</c>.</summary>
    [Fact]
    public void ASidewaysPlacement_StaysInOneTj()
    {
        var placements = new[] { new GlyphPlacement(100, 0, -300, true), new GlyphPlacement(0, 0, 0, false) };
        Assert.Equal("[-100 <0005> 400 <0001>] TJ", GlyphRunShowText.Operators(new ushort[] { 5, 1 }, placements, 12));
    }

    /// <summary>
    /// Letter spacing follows a letter with its marks, not each glyph: under a <c>Tc</c> of 2 at 10
    /// points the qamats takes back 2·1000/10 = 200 after it, so the bet and its point are spaced
    /// once. The run counts one mark, so a producer measures one gap fewer than glyphs.
    /// </summary>
    [Fact]
    public void LetterSpacing_IsTakenBackAfterAMark()
    {
        var font = TrueTypeFontProgram.Parse(F.Build());
        ShapedGlyphRun run = TextRunComposer.Compose($"{F.Bet}{F.Qamats}", font, null);

        Assert.Equal(1, run.MarkCount);
        Assert.Equal("-0.5 Ts\n[-150 <0002> 650] TJ\n0 Ts\n<0001> Tj", GlyphRunShowText.Operators(run, 10, charSpacing: 2));
        Assert.Equal(0, TextRunComposer.Compose($"{F.Bet}{F.Bet}", font, null).MarkCount);
    }

    private static (double, double, double) Tuple(GlyphPlacement p) => (p.XOffset, p.YOffset, p.AdvanceAdjustment);
}
