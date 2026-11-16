// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Text;
using Xunit;
using F = Tetitulz.Pdf.TestHelpers.CursiveFont;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// Cursive attachment (<c>GPOS</c> lookup type 3, the <c>curs</c> feature): a letter's exit anchor
/// meets the next letter's entry anchor, as a Nastaliq-style face joins its letters. Before the
/// lookups were read, each letter sat at its own advance, the joins apart and every letter on the
/// baseline. See <see cref="F"/> for the anchors.
/// </summary>
public class CursiveAttachmentTests
{
    /// <summary>
    /// Three behs, right to left: the final form is drawn first. Its advance runs from its origin to
    /// where its entry (480) meets the medial's exit (20), 460; the medial's likewise, 380 − 20 = 360.
    /// With the RightToLeft flag the earlier letter hangs from the later, each 100 lower: the medial at
    /// −100, the initial at −200. Through an extension lookup, as Dubai's are, the same.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThreeJoinedLetters_MeetAtTheirAnchors(bool extension)
    {
        ShapedGlyphRun run = Compose(F.Build(extension: extension), "ببب");

        Assert.Equal(new[] { F.FinalGlyph, F.MedialGlyph, F.InitialGlyph }, run.GlyphIds);
        Assert.Equal(new[] { (0d, 0d, -40d), (0d, -100d, -40d), (0d, -200d, 0d) }, Placed(run));
        Assert.Equal(1220d, run.AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// Without the RightToLeft flag the later letter hangs from the earlier: the medial 100 above the
    /// initial, the final 100 above that. The advances are the same joins.
    /// </summary>
    [Fact]
    public void WithoutTheRightToLeftFlag_TheLaterLetterHangsFromTheEarlier()
    {
        ShapedGlyphRun run = Compose(F.Build(flag: 0x8), "ببب");

        Assert.Equal(new[] { (0d, 200d, -40d), (0d, 100d, -40d), (0d, 0d, 0d) }, Placed(run));
    }

    /// <summary>
    /// A fatha between the letters: the lookup ignores marks, so the join reaches past it — the final's
    /// advance is still 460, the fatha takes no room, the initial is 100 lower. A lookup that does not
    /// ignore marks finds the fatha before the final, which has no exit, and joins nothing.
    /// </summary>
    [Fact]
    public void AnIgnoredMark_DoesNotBreakTheJoin()
    {
        ShapedGlyphRun skipping = Compose(F.Build(flag: 0x9), "بَب");
        Assert.Equal(new[] { F.FinalGlyph, F.FathaGlyph, F.InitialGlyph }, skipping.GlyphIds);
        Assert.Equal(new[] { (0d, 0d, -40d), (0d, 0d, -200d), (0d, -100d, 0d) }, Placed(skipping));
        Assert.Equal(860d, skipping.AdvanceGlyphSpaceUnits);

        ShapedGlyphRun interrupted = Compose(F.Build(flag: 0x1), "بَب");
        Assert.Equal(new[] { (0d, 0d, 0d), (0d, 0d, -200d), (0d, 0d, 0d) }, Placed(interrupted));
        Assert.Equal(900d, interrupted.AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// Left to right the earlier glyph is drawn on the left, so its advance is the join's: its exit
    /// (550) less the next one's entry (50), 500. With the RightToLeft flag it hangs 100 lower.
    /// </summary>
    [Fact]
    public void LeftToRight_TheEarlierGlyphsAdvanceIsTheJoins()
    {
        ShapedGlyphRun run = Compose(F.Build(), "xx");

        Assert.Equal(new[] { (0d, -100d, -100d), (0d, 0d, 0d) }, Placed(run));
        Assert.Equal(1100d, run.AdvanceGlyphSpaceUnits);
    }

    /// <summary>A cursive join needs no <c>GDEF</c>: without one the letters still meet.</summary>
    [Fact]
    public void WithoutGdef_TheLettersStillJoin()
    {
        ShapedGlyphRun run = Compose(F.Build(gdef: false), "بب");

        Assert.Equal(new[] { (0d, 0d, -40d), (0d, -100d, 0d) }, Placed(run));
    }

    /// <summary>
    /// Only an exit followed by an entry joins: the final form has no exit, so the final then the
    /// initial, in logical order, joins nothing. The control is the initial then the final.
    /// </summary>
    [Fact]
    public void AGlyphWithNoExit_JoinsNothing()
    {
        OpenTypeMarkPositioning gpos = OpenTypeMarkPositioning.TryLoad(F.Build(), 1000);

        OpenTypeMarkPositioning.Positions none = gpos.Apply("arab", new[] { F.FinalGlyph, F.InitialGlyph });
        Assert.Equal(new[] { -1, -1 }, none.JoinedTo);

        OpenTypeMarkPositioning.Positions joined = gpos.Apply("arab", new[] { F.InitialGlyph, F.FinalGlyph });
        Assert.Equal(new[] { 1, -1 }, joined.JoinedTo);
        Assert.Equal((20, 480), (joined.ExitX[0], joined.EntryX[1]));
        Assert.Equal((1, -100), (joined.CursiveParent[0], joined.CursiveY[0]));
    }

    /// <summary>
    /// Two lookups joining the same pair in opposite orientations hang each letter from the other — a
    /// cycle of parents. The heights are resolved once each along the chain: the final 100 above the
    /// initial, which takes the first lookup's −100 back to the baseline.
    /// </summary>
    [Fact]
    public void ACycleOfCursiveParents_IsResolvedOnce()
    {
        ShapedGlyphRun run = Compose(F.Build(bothOrientations: true), "بب");

        Assert.Equal(new[] { (0d, 100d, -40d), (0d, 0d, 0d) }, Placed(run));
    }

    /// <summary>
    /// A glyph already hanging from one keeps that parent. Over three letters the first lookup hangs
    /// the medial from the final; the second would hang it from the initial, and does not, so the
    /// final is 100 above the medial (the second lookup's own join) and the initial 100 below it.
    /// Re-parenting the medial would put the three at 200, 100 and 0.
    /// </summary>
    [Fact]
    public void AGlyphAlreadyHanging_KeepsItsFirstParent()
    {
        ShapedGlyphRun run = Compose(F.Build(bothOrientations: true), "ببب");

        Assert.Equal(new[] { (0d, 100d, -40d), (0d, 0d, -40d), (0d, -100d, 0d) }, Placed(run));
    }

    private static ShapedGlyphRun Compose(byte[] font, string text)
        => TextRunComposer.Compose(text, TrueTypeFontProgram.Parse(font), null);

    private static (double X, double Y, double Advance)[] Placed(ShapedGlyphRun run)
    {
        Assert.NotNull(run.Placements);
        return run.Placements.Select(p => (p.XOffset, p.YOffset, p.AdvanceAdjustment)).ToArray();
    }
}
