// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using SkiaSharp;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The OTF-wrapped CFF text path (<c>ContentStreamInterpreter.DrawOtfCffRun</c>), which now builds
/// <b>one</b> <c>SKTextBlob</c> for a whole run through <c>PdfRenderContext.DrawGlyphRunWithWidths</c>
/// instead of calling a single-glyph draw per character.
///
/// <para><b>What can go wrong, and therefore what these assert.</b> Batching moves the per-glyph
/// pen step out of the text matrix and into the positions handed to Skia, and the conversion
/// between those two spaces is where §9.4.4 bites: an advance is in unscaled text space, a position
/// is in the em-normalised local space the screen matrix maps, and the horizontal scaling
/// <c>Th</c> belongs to exactly one of them. <c>DrawStandardRun</c> records what happens when that
/// is got wrong — advances scaled by <c>Th</c> cubed. So every test here measures <b>where the ink
/// is</b>, not that ink exists.</para>
///
/// <para><b>The fixture glyph is a solid box</b> (<see cref="EmbeddedCffFixture"/>): 400 units
/// square on a 1000-unit em with a 500-unit advance. At 100 pt and 1 px per point that is a 40 pt
/// box stepping 50 pt, so consecutive glyphs leave a 10 pt gap of bare page between them — and a
/// run whose spacing collapsed or doubled shows up as ink in that gap, or as page where a box
/// should be. A real letter's bounds would depend on its outline instead.</para>
/// </summary>
public sealed class GlyphRunBatchingTests
{
    private const float FontSizePt = 100f;
    private const float OriginX = 100f, OriginY = 300f;
    private const float BoxPt = FontSizePt * EmbeddedCffFixture.GlyphBoxEm / 1000f;       // 40
    private const float AdvancePt = FontSizePt * EmbeddedCffFixture.GlyphAdvanceEm / 1000f; // 50

    /// <summary>A point inside the n-th box of a run whose glyphs step by <paramref name="step"/>.</summary>
    private static float BoxCentreX(int index, float step, float boxWidth = BoxPt)
        => OriginX + index * step + boxWidth * 0.5f;

    /// <summary>A point in the bare-page gap that follows the n-th box.</summary>
    private static float GapCentreX(int index, float step, float boxWidth = BoxPt)
        => OriginX + index * step + boxWidth + (step - boxWidth) * 0.5f;

    private const float MidBoxY = OriginY + 20f;   // halfway up a 40 pt box sitting on the baseline

    // ================================================================

    /// <summary>
    /// The base case: three glyphs in one <c>Tj</c> land on their own 50 pt pitch, with bare page
    /// between them. A batched run that lost its per-glyph positions would stack all three at the
    /// origin — one box, and page where boxes 2 and 3 belong.
    /// </summary>
    [Fact]
    public void OtfCffRun_PlacesEveryGlyphOnItsOwnAdvance()
    {
        using SKBitmap bmp = PixelAssert.Render(
            EmbeddedCffFixture.Page($"BT /F1 {FontSizePt:0} Tf {OriginX} {OriginY} Td (AAA) Tj ET"));

        Assert.True(PixelAssert.IsInk(PixelAssert.At(bmp, BoxCentreX(0, AdvancePt), MidBoxY)),
            "the first glyph is missing, so nothing below is measuring glyph placement");
        Assert.True(PixelAssert.IsInk(PixelAssert.At(bmp, BoxCentreX(1, AdvancePt), MidBoxY)),
            "the second glyph did not reach its own advance");
        Assert.True(PixelAssert.IsInk(PixelAssert.At(bmp, BoxCentreX(2, AdvancePt), MidBoxY)),
            "the third glyph did not reach its own advance");

        Assert.True(PixelAssert.IsWhite(PixelAssert.At(bmp, GapCentreX(0, AdvancePt), MidBoxY)),
            "the gap after the first glyph is inked, so the run is spaced too widely or the " +
            "glyphs are drawn too large");
        Assert.True(PixelAssert.IsWhite(PixelAssert.At(bmp, GapCentreX(1, AdvancePt), MidBoxY)),
            "the gap after the second glyph is inked");
    }

    /// <summary>
    /// <c>Tc</c> is added to each advance (§9.4.4), so a 10 pt character spacing widens the pitch
    /// from 50 pt to 60 pt. Asserted as ink at the wider pitch <i>and</i> page at the old one:
    /// "the glyphs moved" alone is also true of a run that moved by the wrong amount.
    /// </summary>
    [Fact]
    public void OtfCffRun_AddsCharacterSpacingToEachAdvance()
    {
        const float tc = 10f;
        using SKBitmap bmp = PixelAssert.Render(EmbeddedCffFixture.Page(
            $"BT /F1 {FontSizePt:0} Tf {tc:0} Tc {OriginX} {OriginY} Td (AAA) Tj ET"));

        // With Tc the three boxes span 100-140, 160-200 and 220-260; without it, 100-140, 150-190
        // and 200-240. The two probes are chosen where those disagree and no box edge is near:
        // 250 is inside the third box only if Tc was applied, and 210 is bare page only if it was.
        Assert.True(PixelAssert.IsInk(PixelAssert.At(bmp, 250f, MidBoxY)),
            "nothing is painted where Tc puts the third glyph, so the spacing was dropped");
        Assert.True(PixelAssert.IsWhite(PixelAssert.At(bmp, 210f, MidBoxY)),
            "the third glyph is still at the unspaced pitch, so Tc never reached the batched run");
    }

    /// <summary>
    /// <c>Tz</c> multiplies the whole advance term <i>and</i> stretches the glyph (§9.4.4 puts it
    /// in the matrix's <c>a</c> slot). At <c>50 Tz</c> both halve: 20 pt boxes on a 25 pt pitch.
    ///
    /// <para>This is the assertion that fails if <c>Th</c> is applied twice — once to the position
    /// handed to Skia and once by the screen matrix built from the same text matrix — which is the
    /// specific defect the batched path had to avoid, and the one <c>DrawStandardRun</c> already
    /// carries a comment about. Double-applied, glyph 2 would sit at 112.5 pt rather than 125 pt,
    /// which the gap probe catches.</para>
    /// </summary>
    [Fact]
    public void OtfCffRun_UnderHorizontalScaling_HalvesTheAdvanceExactlyOnce()
    {
        const float halfBox = BoxPt / 2f, halfAdvance = AdvancePt / 2f;
        using SKBitmap bmp = PixelAssert.Render(EmbeddedCffFixture.Page(
            $"BT /F1 {FontSizePt:0} Tf 50 Tz {OriginX} {OriginY} Td (AAA) Tj ET"));

        Assert.True(PixelAssert.IsInk(
                PixelAssert.At(bmp, BoxCentreX(2, halfAdvance, halfBox), MidBoxY)),
            "the third glyph is not at the Tz-halved pitch");
        Assert.True(PixelAssert.IsWhite(
                PixelAssert.At(bmp, GapCentreX(1, halfAdvance, halfBox), MidBoxY)),
            "the gap between the second and third glyph is inked: the boxes are wider than the " +
            "halved pitch, so Th reached the advance but not the glyph outline");
    }

    /// <summary>
    /// A second <c>Tj</c> in the same text object must start where the first left off, which is a
    /// claim about the <b>text matrix</b> rather than about the positions inside one blob. Batching
    /// computes that step once, as a sum, instead of per glyph, so it is exactly the thing a single
    /// summation bug would break — and no assertion inside a single run would notice.
    /// </summary>
    [Fact]
    public void OtfCffRun_LeavesTheTextMatrixWhereTheNextShowOperatorExpectsIt()
    {
        using SKBitmap bmp = PixelAssert.Render(EmbeddedCffFixture.Page(
            $"BT /F1 {FontSizePt:0} Tf {OriginX} {OriginY} Td (AA) Tj (A) Tj ET"));

        Assert.True(PixelAssert.IsInk(PixelAssert.At(bmp, BoxCentreX(2, AdvancePt), MidBoxY)),
            "the second Tj did not start at the first one's end");
        Assert.True(PixelAssert.IsWhite(PixelAssert.At(bmp, GapCentreX(2, AdvancePt), MidBoxY)),
            "there is ink past the third glyph, so the text matrix over-advanced");
    }

    /// <summary>
    /// Text rendering mode 7 (<c>Clip</c>, §9.3.6) paints nothing itself and makes the run's glyph
    /// outlines the clip for everything after <c>ET</c>. Batched, the accumulation happens once per
    /// run rather than once per glyph, and the two failure directions are opposite —
    /// clipping to nothing (the rectangle vanishes) or to everything (the rectangle survives
    /// whole). Both are caught by probing inside a glyph and inside the gap beside it.
    /// </summary>
    [Fact]
    public void OtfCffRun_InClipMode_ClipsLaterPaintToTheWholeRunsOutlines()
    {
        using SKBitmap bmp = PixelAssert.Render(EmbeddedCffFixture.Page(
            $"BT /F1 {FontSizePt:0} Tf 7 Tr {OriginX} {OriginY} Td (AAA) Tj ET\n" +
            "1 0 0 rg 0 0 612 792 re f"));

        SKColor inFirst = PixelAssert.At(bmp, BoxCentreX(0, AdvancePt), MidBoxY);
        SKColor inThird = PixelAssert.At(bmp, BoxCentreX(2, AdvancePt), MidBoxY);
        SKColor inGap = PixelAssert.At(bmp, GapCentreX(0, AdvancePt), MidBoxY);

        Assert.InRange(inFirst.Red, 200, 255);
        Assert.InRange(inFirst.Green, 0, 55);
        Assert.InRange(inThird.Red, 200, 255);
        Assert.InRange(inThird.Green, 0, 55);
        Assert.True(PixelAssert.IsWhite(inGap),
            $"the fill leaked between glyphs, so the clip covered more than the outlines: {inGap}");
    }

    /// <summary>
    /// Pattern-filled text (<c>/Pattern cs … scn</c>) does not paint at all: the run's glyph
    /// outlines are <b>captured</b> and the pattern is then painted into the captured shape. The
    /// batched run has to keep that branch working — it hands the whole run to one method that
    /// checks <c>IsCapturingGlyphs</c> before drawing, and a run that both captured and painted, or
    /// captured only its first glyph, is the way this breaks.
    ///
    /// <para>The pattern is a red-to-blue axial shading spanning the run, so the three probes are
    /// three different claims: glyph 1 shows the red end, glyph 3 the blue end (both outlines
    /// reached the capture, at the right x), and the gap between them is bare page (the pattern was
    /// clipped to the glyphs rather than flooded).</para>
    /// </summary>
    [Fact]
    public void OtfCffRun_WithAPatternFill_CapturesTheWholeRunsOutlines()
    {
        using SKBitmap bmp = PixelAssert.Render(EmbeddedCffFixture.Page(
            $"/Pattern cs /P1 scn BT /F1 {FontSizePt:0} Tf {OriginX} {OriginY} Td (AAA) Tj ET",
            extraPageResources: "/Pattern << /P1 8 0 R >>",
            extraObjects:
                "8 0 obj\n<< /Type /Pattern /PatternType 2 /Shading << /ShadingType 2 "
              + "/ColorSpace /DeviceRGB /Coords [100 0 240 0] /Domain [0 1] "
              + "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> "
              + "/Extend [true true] >> >>\nendobj\n"));

        SKColor red = PixelAssert.At(bmp, 110f, MidBoxY);
        SKColor blue = PixelAssert.At(bmp, 230f, MidBoxY);
        SKColor gap = PixelAssert.At(bmp, GapCentreX(0, AdvancePt), MidBoxY);

        Assert.True(red.Red > 190 && red.Blue < 80, $"the run's red end is not painted: {red}");
        Assert.True(blue.Blue > 190 && blue.Red < 80, $"the run's blue end is not painted: {blue}");
        Assert.True(PixelAssert.IsWhite(gap),
            $"the pattern painted between the glyphs, so it was not clipped to the outlines: {gap}");
    }

    /// <summary>
    /// A glyph run under a non-Normal blend mode still composites as one element per glyph, because
    /// Skia rasterises a text blob glyph by glyph rather than merging coverage. Pinned because the
    /// <i>other</i> batching shape available here — merging outlines into a single
    /// <c>SKPath</c> — does not have that property, and choosing it for this path would change
    /// overlapping-glyph output. Multiply over an opaque green page leaves red at 0 and green
    /// untouched wherever the glyph covers.
    /// </summary>
    [Fact]
    public void OtfCffRun_UnderNonNormalBlend_StillPaintsEachGlyph()
    {
        using SKBitmap bmp = PixelAssert.Render(EmbeddedCffFixture.Page(
            "0 1 0 rg 0 0 612 792 re f\n" +
            "/GS0 gs 0 0 1 rg\n" +
            $"BT /F1 {FontSizePt:0} Tf {OriginX} {OriginY} Td (AAA) Tj ET",
            extraPageResources: "/ExtGState << /GS0 8 0 R >>",
            extraObjects: "8 0 obj\n<< /Type /ExtGState /BM /Multiply >>\nendobj\n"));

        // blue x green = black under Multiply; the bare page stays green.
        SKColor inGlyph = PixelAssert.At(bmp, BoxCentreX(1, AdvancePt), MidBoxY);
        SKColor inGap = PixelAssert.At(bmp, GapCentreX(1, AdvancePt), MidBoxY);

        Assert.InRange(inGlyph.Green, 0, 40);
        Assert.InRange(inGlyph.Blue, 0, 40);
        Assert.InRange(inGap.Green, 215, 255);
    }
}
