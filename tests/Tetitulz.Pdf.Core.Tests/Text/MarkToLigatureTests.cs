// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Text;
using Xunit;
using F = Tetitulz.Pdf.TestHelpers.LigatureMarkFont;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// A mark on a ligature (<c>GPOS</c> mark-to-ligature, lookup type 5): a fatha after the lam-alef
/// ligature is drawn at the ligature's <b>last</b> component's anchor — the alef's — and takes no
/// room. Every Arabic font on a Windows host keeps these anchors in such lookups; before they were
/// read, the fatha stayed at its pen position and pushed the ligature along by its advance.
/// </summary>
public class MarkToLigatureTests
{
    /// <summary>
    /// The composer forms the ligature, then places the fatha on it: right to left, the fatha comes
    /// first in the glyph order and both start at the same pen position, so its placement is the
    /// anchor offset itself, (50, 650), and its advance (200) is taken back.
    /// </summary>
    [Fact]
    public void AFathaOnLamAlef_IsDrawnAtTheAlefsAnchor()
    {
        var font = TrueTypeFontProgram.Parse(F.Build());
        ShapedGlyphRun run = TextRunComposer.Compose("لاَ", font, null);

        Assert.Equal(new[] { F.FathaGlyph, F.LamAlefGlyph }, run.GlyphIds);
        Assert.Equal((50d, 650d, -200d), (run.Placements[0].XOffset, run.Placements[0].YOffset, run.Placements[0].AdvanceAdjustment));
        Assert.True(run.Placements[1].IsIdentity);
        Assert.Equal(600d, run.AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// A font whose own <c>GSUB</c> <c>rlig</c> forms the ligature from the lam and alef forms — and
    /// keeps its mark anchors on that glyph, not on the one its <c>cmap</c> maps U+FEFB to (Segoe
    /// UI's shape) — gets the ligature from its rules: the composer leaves the two forms for the
    /// substitution pass, the fatha lands at the formed glyph's anchor, and the text is still lam
    /// and alef. The default fixture, whose <c>cmap</c> glyph carries the anchors, is the control
    /// above: the encoded form, as before.
    /// </summary>
    [Fact]
    public void AFontWhoseOwnRulesFormTheLigature_GetsTheMarkOnThatGlyph()
    {
        var font = TrueTypeFontProgram.Parse(F.Build(gsubFormsLigature: true));
        ShapedGlyphRun run = TextRunComposer.Compose("لاَ", font, null);

        Assert.Equal(new[] { F.FathaGlyph, F.GsubLamAlefGlyph }, run.GlyphIds);
        Assert.Equal((50d, 650d, -200d), (run.Placements[0].XOffset, run.Placements[0].YOffset, run.Placements[0].AdvanceAdjustment));
        Assert.Equal(600d, run.AdvanceGlyphSpaceUnits);
        Assert.Equal("لا", run.Glyphs[1].Text);

        // The control: a font whose rules form nothing draws the encoded ligature, with its anchors.
        Assert.Equal(new[] { F.FathaGlyph, F.LamAlefGlyph },
                     TextRunComposer.Compose("لاَ", TrueTypeFontProgram.Parse(F.Build()), null).GlyphIds);
    }

    /// <summary>
    /// A font whose own rules draw lam-alef as <b>two</b> contextual glyphs — a lam piece and an
    /// alef piece, one for one, with the fatha's anchor a mark-to-base anchor on the alef piece —
    /// gets those glyphs and the mark on the alef piece: (250, 600) less the fatha's (100, 0), the
    /// fatha's advance taken back. This is Segoe UI's shape, which no ligature glyph models; the
    /// text is still lam and alef, one letter per glyph.
    /// </summary>
    [Fact]
    public void AFontWhoseOwnRulesRedrawThePairAsTwoGlyphs_GetsTheMarkOnTheAlefPiece()
    {
        var font = TrueTypeFontProgram.Parse(F.Build(gsubRedrawsAsTwoGlyphs: true));
        ShapedGlyphRun run = TextRunComposer.Compose("لاَ", font, null);

        Assert.Equal(new[] { F.FathaGlyph, F.AlefPieceGlyph, F.LamPieceGlyph }, run.GlyphIds);
        Assert.Equal((150d, 600d, -200d), (run.Placements[0].XOffset, run.Placements[0].YOffset, run.Placements[0].AdvanceAdjustment));
        Assert.Equal(600d, run.AdvanceGlyphSpaceUnits);
        Assert.Equal(new[] { 0x064E, 0x0627, 0x0644 }, run.Glyphs.Select(g => g.CodePoint));
    }

    /// <summary>
    /// A font with rules that say nothing about lam and alef still draws the encoded ligature, with
    /// the anchors its <c>cmap</c> glyph carries: having a <c>GSUB</c> is not redrawing the pair.
    /// </summary>
    [Fact]
    public void AFontWhoseRulesLeaveLamAlefAlone_DrawsTheEncodedLigature()
    {
        var font = TrueTypeFontProgram.Parse(F.Build(gsubLeavesLamAlefAlone: true));
        ShapedGlyphRun run = TextRunComposer.Compose("لاَ", font, null);

        Assert.Equal(new[] { F.FathaGlyph, F.LamAlefGlyph }, run.GlyphIds);
        Assert.Equal((50d, 650d, -200d), (run.Placements[0].XOffset, run.Placements[0].YOffset, run.Placements[0].AdvanceAdjustment));
    }

    /// <summary>
    /// Segoe UI itself: its required rules draw lam-alef as two pieces with mark-to-base anchors, and
    /// the glyph its <c>cmap</c> maps U+FEFB to has none — so the fatha is placed now, on glyphs the
    /// font's rules chose, where it used to stay at its pen position. Skipped where the font is not
    /// installed; the fixture above is the shape, this is the font the register named.
    /// </summary>
    [TestInfrastructure.SegoeUiFact]
    public void SegoeUi_PlacesTheFathaOnTheLamAlefItsOwnRulesDraw()
    {
        var font = TrueTypeFontProgram.Parse(System.IO.File.ReadAllBytes(TestInfrastructure.SegoeUiFactAttribute.Path));
        ShapedGlyphRun run = TextRunComposer.Compose("لاَ", font, null);

        Assert.Equal(3, run.GlyphIds.Length);
        int mark = -1;
        for (int i = 0; i < run.Placements.Count; i++) if (run.Placements[i].IsMark) mark = i;
        Assert.True(mark >= 0, "no glyph of the run is a mark");
        Assert.False(run.Placements[mark].IsIdentity, "the fatha stayed at its pen position");

        Assert.True(font.TryGetGlyph(0xFEFB, out ushort encoded) && encoded != 0);
        Assert.DoesNotContain(encoded, run.GlyphIds);
    }

    /// <summary>
    /// The attachment is to the last component: (150, 650) on the alef less the fatha's (100, 0).
    /// The lam's anchor, (450, 700), would put it at (350, 700).
    /// </summary>
    [Fact]
    public void TheMark_AttachesToTheLastComponent()
    {
        OpenTypeMarkPositioning.Positions p = Apply(F.Build(), F.LamAlefGlyph, F.FathaGlyph);

        Assert.Equal(0, p.Parent[1]);
        Assert.Equal((50, 650), (p.AnchorX[1], p.AnchorY[1]));
    }

    /// <summary>
    /// A last component with no anchor for the mark's class leaves the mark unattached — it does
    /// not fall back to another component's anchor. The control is the same font with the anchor.
    /// </summary>
    [Fact]
    public void ANullAnchorOnTheLastComponent_AttachesNothing()
    {
        Assert.Equal(-1, Apply(F.Build(nullLastAnchor: true), F.LamAlefGlyph, F.FathaGlyph).Parent[1]);
        Assert.Equal(0, Apply(F.Build(), F.LamAlefGlyph, F.FathaGlyph).Parent[1]);
    }

    /// <summary>
    /// A mark after a glyph the ligature coverage does not hold is not placed by the lookup: seen
    /// is a base, not a ligature. A mark not covered — the ligature after the fatha, read as a
    /// mark on nothing — is not either.
    /// </summary>
    [Fact]
    public void OnlyACoveredMarkOnACoveredLigature_IsAttached()
    {
        Assert.Equal(-1, Apply(F.Build(), F.SeenGlyph, F.FathaGlyph).Parent[1]);
        Assert.Equal(-1, Apply(F.Build(), F.FathaGlyph, F.LamAlefGlyph).Parent[1]);
        Assert.Equal(0, Apply(F.Build(), F.LamAlefGlyph, F.FathaGlyph).Parent[1]);
    }

    /// <summary>A second fatha looks past the first to the ligature, as a mark-to-base mark looks past marks to its base.</summary>
    [Fact]
    public void ASecondMark_LooksPastTheFirstToTheLigature()
    {
        OpenTypeMarkPositioning.Positions p = Apply(F.Build(), F.LamAlefGlyph, F.FathaGlyph, F.FathaGlyph);

        Assert.Equal(0, p.Parent[2]);
        Assert.Equal((50, 650), (p.AnchorX[2], p.AnchorY[2]));
    }

    private static OpenTypeMarkPositioning.Positions Apply(byte[] font, params ushort[] glyphs)
        => OpenTypeMarkPositioning.TryLoad(font, 1000).Apply("arab", glyphs);
}
