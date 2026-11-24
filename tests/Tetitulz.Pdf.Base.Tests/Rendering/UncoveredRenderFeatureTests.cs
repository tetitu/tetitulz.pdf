// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;
using SkiaSharp;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Pixel coverage for rendering features the 2026-08-06 audit found had <b>none</b> — not a weak
/// test, no test at all. Each is a feature the renderer implements and nothing checked.
///
/// <para>Listed in GAPS.md §2 under "What the audit says about the tests": shading types 5/6/7,
/// every blend mode except Multiply (including all four non-separable ones), an <c>/SMask /S /Alpha</c>
/// on the raster path, and text render mode 6.</para>
/// </summary>
public sealed class UncoveredRenderFeatureTests
{
    // ====================================================================
    //  Blend modes
    // ====================================================================

    /// <summary>
    /// Red over green. Each mode has a different, arithmetically determined answer, so the
    /// expectations cannot be satisfied by a renderer that ignores <c>/BM</c> — that would paint
    /// plain red everywhere and fail every row but the last.
    /// </summary>
    [Theory]
    [InlineData("Multiply",   0,   0,   0)]   // 1*0, 0*1, 0*0
    [InlineData("Screen",   255, 255,   0)]   // 1+0-0, 0+1-0, 0
    [InlineData("Darken",     0,   0,   0)]   // min per channel
    [InlineData("Lighten",  255, 255,   0)]   // max per channel
    [InlineData("Difference", 255, 255, 0)]   // |1-0|, |0-1|, 0
    [InlineData("Exclusion",  255, 255, 0)]   // a+b-2ab
    [InlineData("Normal",   255,   0,   0)]   // the control: source wins
    public void SeparableBlendMode_CompositesAgainstTheBackdrop(string mode, int r, int g, int b)
    {
        using var bmp = PixelAssert.Render(BlendPdf(mode));
        var c = PixelAssert.At(bmp, 50, 50);

        Assert.Equal(r, c.Red,   tolerance: 4);
        Assert.Equal(g, c.Green, tolerance: 4);
        Assert.Equal(b, c.Blue,  tolerance: 4);
    }

    /// <summary>
    /// The non-separable modes (§11.3.5.3) had no coverage at all. <c>Luminosity</c> keeps the
    /// backdrop's hue and saturation and takes the source's luminosity, so red-over-green must come
    /// out a *green* darker than the backdrop — never red, and never the untouched backdrop.
    /// </summary>
    [Fact]
    public void LuminosityBlendMode_KeepsBackdropHueAndTakesSourceLuminosity()
    {
        using var bmp = PixelAssert.Render(BlendPdf("Luminosity"));
        var c = PixelAssert.At(bmp, 50, 50);

        Assert.True(c.Green > c.Red && c.Green > c.Blue, $"hue should stay green, got {c}");
        Assert.True(c.Green < 250, $"luminosity should darken the backdrop, got {c}");
    }

    /// <summary>
    /// <c>Hue</c> takes the source's hue with the backdrop's saturation and luminosity, so
    /// red-over-green must come out *red*-hued — the opposite of Luminosity above. Asserting the
    /// pair is what makes each a claim about its own mode.
    /// </summary>
    [Fact]
    public void HueBlendMode_TakesTheSourceHue()
    {
        using var bmp = PixelAssert.Render(BlendPdf("Hue"));
        var c = PixelAssert.At(bmp, 50, 50);

        Assert.True(c.Red > c.Green && c.Red > c.Blue, $"hue should become red, got {c}");
    }

    // ====================================================================
    //  Shading types 5, 6 and 7
    // ====================================================================

    /// <summary>
    /// Type 5 (lattice-form Gouraud mesh) had no test anywhere. A 2x2 lattice of red vertices must
    /// paint the quad they span.
    /// </summary>
    [Fact]
    public void LatticeMeshShading_Type5_PaintsTheQuad()
    {
        using var bmp = PixelAssert.Render(LatticeMeshPdf());
        var c = PixelAssert.At(bmp, 150, 150);

        Assert.True(c.Red > 200 && c.Green < 60, $"expected the mesh's red, got {c}");
    }

    /// <summary>
    /// Type 6 (Coons patch) had no test anywhere — including its silent
    /// <c>?? BuildMeshShader(shading)</c> degradation path.
    /// </summary>
    [Fact]
    public void CoonsPatchShading_Type6_PaintsThePatch()
    {
        using var bmp = PixelAssert.Render(CoonsPatchPdf(shadingType: 6));
        var c = PixelAssert.At(bmp, 150, 150);

        Assert.True(c.Red > 200 && c.Green < 60, $"expected the patch's red, got {c}");
    }

    /// <summary>
    /// Type 7 (tensor patch) differs from type 6 only by four interior control points, which for
    /// flag 0 means four extra coordinate pairs in the stream. Reading the wrong count would
    /// desynchronise the whole patch.
    /// </summary>
    [Fact]
    public void TensorPatchShading_Type7_PaintsThePatch()
    {
        using var bmp = PixelAssert.Render(CoonsPatchPdf(shadingType: 7));
        var c = PixelAssert.At(bmp, 150, 150);

        Assert.True(c.Red > 200 && c.Green < 60, $"expected the patch's red, got {c}");
    }

    // ====================================================================
    //  Soft mask — the /S /Alpha variant
    // ====================================================================

    /// <summary>
    /// Only <c>/S /Luminosity</c> was pixel-tested. An <b>alpha</b> mask takes the group's alpha
    /// channel: where the group painted (alpha 1) the content shows, and where it did not
    /// (alpha 0) the content is masked out — regardless of the colour it painted, which is exactly
    /// what distinguishes it from a luminosity mask.
    /// </summary>
    [Fact]
    public void AlphaSoftMask_MasksByCoverageNotByColour()
    {
        using var bmp = PixelAssert.Render(AlphaSoftMaskPdf());

        // The mask group paints an opaque BLACK square over the left half. As a luminosity mask
        // black means "hide"; as an alpha mask it means "show", because its alpha is 1.
        var inside  = PixelAssert.At(bmp, 25, 25);
        var outside = PixelAssert.At(bmp, 75, 25);

        Assert.True(PixelAssert.IsInk(inside),
            $"an alpha mask must show content where the group painted, got {inside}");
        Assert.False(PixelAssert.IsInk(outside),
            $"content outside the mask group must be hidden, got {outside}");
    }

    // ====================================================================
    //  Text render mode 6 — fill + stroke + clip
    // ====================================================================

    /// <summary>
    /// Modes 4, 5 and 7 were covered; 6 (fill, stroke and add to clip) was not. The subsequent
    /// full-page fill must be confined to the glyph area, which is what "adds to clip" means.
    /// </summary>
    [Fact]
    public void TextRenderMode6_FillsStrokesAndClips()
    {
        using var bmp = PixelAssert.Render(TextClipPdf(renderMode: 6));
        using var unclipped = PixelAssert.Render(TextClipPdf(renderMode: 0));

        int clippedInk = PixelAssert.CountInk(bmp);
        Assert.True(clippedInk > 0, "mode 6 painted nothing at all");
        // The page-covering fill after ET is confined to the glyphs, so far less of the page is
        // inked than when the same stream runs with a non-clipping render mode.
        Assert.True(clippedInk < PixelAssert.CountInk(unclipped) / 2,
            $"mode 6 did not clip: {clippedInk} inked pixels vs {PixelAssert.CountInk(unclipped)}");
    }

    // ====================================================================
    //  A soft mask's group is a form XObject — /BBox and /Matrix apply
    // ====================================================================

    /// <summary>
    /// §11.6.5.2 makes a soft mask's <c>/G</c> an ordinary form XObject, so §8.10.2's <c>/BBox</c>
    /// clip applies to it. A mask group that paints beyond its declared box must be clipped, so
    /// content outside the box stays masked out.
    /// </summary>
    /// <remarks>
    /// Ignored by both renderers until now, which is why it never showed as a divergence — the same
    /// blind spot as the shading tint transform and <c>Tz</c>. The mask group here paints white
    /// (fully visible) over the whole page but declares a <c>/BBox</c> covering only the left half.
    /// </remarks>
    [Fact]
    public void SoftMaskGroup_ClipsToItsBBox()
    {
        using var bmp = PixelAssert.Render(SoftMaskGroupPdf(bbox: "[0 0 50 100]", matrix: null));

        Assert.True(PixelAssert.IsInk(PixelAssert.At(bmp, 25, 50)),
            "inside the mask group's /BBox the content should show");
        Assert.False(PixelAssert.IsInk(PixelAssert.At(bmp, 75, 50)),
            "outside the /BBox the mask is black, so the content must be hidden");
    }

    /// <summary>…and the box is in FORM space, so <c>/Matrix</c> moves it.</summary>
    [Fact]
    public void SoftMaskGroup_BBoxIsMappedThroughItsMatrix()
    {
        // The same left-half box, translated 50 units right: now the RIGHT half shows.
        using var bmp = PixelAssert.Render(
            SoftMaskGroupPdf(bbox: "[0 0 50 100]", matrix: "[1 0 0 1 50 0]"));

        Assert.False(PixelAssert.IsInk(PixelAssert.At(bmp, 25, 50)),
            "/Matrix did not move the mask group's box");
        Assert.True(PixelAssert.IsInk(PixelAssert.At(bmp, 75, 50)),
            "the translated /BBox should reveal the right half");
    }

    /// <summary>
    /// A red page-filling fill under a luminosity soft mask whose group paints white over the whole
    /// page but declares the given <c>/BBox</c> (and optional <c>/Matrix</c>).
    /// </summary>
    private static byte[] SoftMaskGroupPdf(string bbox, string matrix)
    {
        string matrixEntry = matrix != null ? $" /Matrix {matrix}" : "";
        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /ExtGState << /G1 5 0 R >> >> >>",
            Stream("", "/G1 gs 1 0 0 rg 0 0 100 100 re f"),
            "<< /Type /ExtGState /SMask << /S /Luminosity /G 6 0 R /BC [0] >> >>",
            Stream($"/Type /XObject /Subtype /Form /BBox {bbox}{matrixEntry}" +
                   " /Group << /S /Transparency /CS /DeviceGray >>",
                   "1 g 0 0 100 100 re f"));
    }

    // ====================================================================
    //  Tz — horizontal scaling belongs in the text rendering matrix
    // ====================================================================

    /// <summary>
    /// §9.4.4 puts Th in <c>Trm</c>'s <c>a</c> slot, so <c>Tz</c> stretches the <b>glyphs</b>, not
    /// only the advances. At 50% the inked run must be about half as wide — and no shorter.
    /// </summary>
    /// <remarks>
    /// <b>Both renderers were wrong the same way</b>, applying Th to advances alone, so the text was
    /// spaced at half width with every glyph drawn full width. Same-wrong is invisible to a
    /// raster-vs-SVG comparison, which is why this outlived the parity sweep; it took reading §9.4.4.
    /// <para>The blocker was not the matrix but the draw path: it derived the em size from the
    /// matrix's <i>x</i>-axis and normalised by it, cancelling any horizontal scale. It now uses the
    /// y-axis, which is the true em.</para>
    /// </remarks>
    [Fact]
    public void HorizontalScaling_StretchesTheGlyphs_NotOnlyTheAdvances()
    {
        using var full = PixelAssert.Render(HorizontalScalingPdf(100));
        using var half = PixelAssert.Render(HorizontalScalingPdf(50));

        var f = PixelAssert.InkBounds(full);
        var h = PixelAssert.InkBounds(half);
        Assert.True(f.HasValue && h.HasValue, "one of the runs painted nothing");

        int fullW = f!.Value.right - f.Value.left, halfW = h!.Value.right - h.Value.left;
        int fullH = f.Value.bottom - f.Value.top,  halfH = h.Value.bottom - h.Value.top;

        // Width roughly halves…
        Assert.InRange(halfW, (int)(fullW * 0.35), (int)(fullW * 0.65));
        // …and the height does NOT change, which is what separates "glyphs stretched" from
        // "everything scaled". Under the old behaviour the width barely moved at all.
        Assert.InRange(halfH, fullH - 2, fullH + 2);
    }

    /// <summary>
    /// ...and <c>Tz</c> must scale the <b>advances</b> by the same factor, exactly once — here on
    /// the <b>system-substituted</b> font path, where SkiaSharp measures the advances.
    /// </summary>
    /// <remarks>
    /// <see cref="HorizontalScaling_StretchesTheGlyphs_NotOnlyTheAdvances"/> uses a single glyph on
    /// purpose, so it measures glyph stretching with no spacing in the picture and cannot see the
    /// advance path at all. Five glyphs make the spacing dominate the width, so a <c>Th</c> applied
    /// to spacing a second time would land the 50% render near a quarter width rather than a half.
    /// <para><b>Which advance path this reaches, and which it does not.</b> <c>/Helvetica</c> is not
    /// embedded, so <c>FontCache.IsSystemFallbackFont</c> is true and
    /// <c>ContentStreamInterpreter</c> sets every <c>pdfWidths</c> entry to <c>-1</c> — the PDF
    /// <c>/Widths</c> belong to the original font and would mis-space the substitute's glyphs. So
    /// this pins <c>Th</c> reaching the advances through the text matrix, and the arithmetic at the
    /// <c>pdfWidths[gi] = glyphAdv * widthScale</c> line never runs. Double-scaling that line leaves
    /// this test green, which is how the split was found;
    /// <c>FontGapClosureTests.HorizontalScaling_WithEmbeddedFontWidths_ScalesTheAdvancesExactlyOnce</c>
    /// covers the other half.</para>
    /// </remarks>
    [Fact]
    public void HorizontalScaling_SubstitutedFont_ScalesTheAdvancesExactlyOnce()
    {
        using var full = PixelAssert.Render(HorizontalScalingRunPdf(100));
        using var half = PixelAssert.Render(HorizontalScalingRunPdf(50));

        var f = PixelAssert.InkBounds(full);
        var h = PixelAssert.InkBounds(half);
        Assert.True(f.HasValue && h.HasValue, "one of the runs painted nothing");

        int fullW = f!.Value.right - f.Value.left, halfW = h!.Value.right - h.Value.left;

        // A single Th: the whole run — glyphs and the gaps between them — halves together.
        Assert.InRange(halfW, (int)(fullW * 0.42), (int)(fullW * 0.58));
    }

    /// <summary>
    /// §9.4.4: character spacing <c>Tc</c> is added to every glyph's advance — including in a
    /// <b>system-substituted</b> font, where the advances are measured by SkiaSharp.
    /// </summary>
    /// <remarks>
    /// SkiaSharp measures a glyph, not a PDF text state, so <c>Tc</c> and <c>Tw</c> can only reach
    /// the run if the renderer adds them itself. It did not: when the PDF <c>/Widths</c> are
    /// unusable (a non-embedded font) every entry is passed as <c>-1</c>, and the measuring branch
    /// added the glyph width alone. **Every standard-14 document — the most common kind there is —
    /// therefore ignored `Tc` and `Tw` outright.**
    /// <para>Five glyphs give four gaps, so <c>20 Tc</c> must widen the run by exactly 80 pt. That
    /// is far larger than any hinting difference, and it is measured as a *difference* between two
    /// renders so the host font's own advance never enters the assertion.</para>
    /// </remarks>
    [Fact]
    public void CharacterSpacing_SubstitutedFont_WidensTheRun()
    {
        using var none = PixelAssert.Render(CharSpacingPdf(0));
        using var wide = PixelAssert.Render(CharSpacingPdf(20));

        var n = PixelAssert.InkBounds(none);
        var w = PixelAssert.InkBounds(wide);
        Assert.True(n.HasValue && w.HasValue, "one of the runs painted nothing");

        int grew = (w!.Value.right - w.Value.left) - (n!.Value.right - n.Value.left);
        Assert.InRange(grew, 80 - 4, 80 + 4);
    }

    /// <summary>
    /// After a substituted-font run, <c>Tm</c> must advance by the run's <b>text-space</b> width —
    /// which includes <c>Th</c>. Only a <i>second</i> show operator can observe this.
    /// </summary>
    /// <remarks>
    /// The advance handed back to <c>Tm</c> is SkiaSharp's measurement, taken in the same Th-free
    /// space as <c>pdfWidths</c>: the screen matrix supplies <c>Th</c> when drawing, but nothing
    /// supplied it to <c>Tm</c>. So the pen ended up in the wrong place for everything that
    /// followed on the line, while the first run itself looked perfect — which is why every
    /// single-<c>Tj</c> fixture in the suite missed it.
    /// <para>Two runs of five glyphs at <c>50 Tz</c>: the whole thing must span exactly what one
    /// ten-glyph run does. Comparing against that rather than against an absolute width keeps the
    /// host font's own advance out of the assertion.</para>
    /// </remarks>
    [Fact]
    public void SubstitutedFont_SecondShowOperator_StartsWhereTheFirstRunEnded()
    {
        using var split = PixelAssert.Render(TwoRunPdf("(HHHHH) Tj (HHHHH) Tj"));
        using var one   = PixelAssert.Render(TwoRunPdf("(HHHHHHHHHH) Tj"));

        var s = PixelAssert.InkBounds(split);
        var o = PixelAssert.InkBounds(one);
        Assert.True(s.HasValue && o.HasValue, "one of the runs painted nothing");

        // Splitting a run in two must not move any glyph: same extent, to within antialiasing.
        Assert.InRange(s!.Value.right - s.Value.left,
                       (o!.Value.right - o.Value.left) - 3, (o.Value.right - o.Value.left) + 3);
    }

    private static byte[] TwoRunPdf(string showOps) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 100] /Contents 4 0 R" +
            " /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("", $"BT /F1 60 Tf 50 Tz 20 25 Td {showOps} ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

    /// <summary>
    /// The other half of §9.4.4's spacing term: <c>Tw</c> applies to the single-byte code 32 only,
    /// on the substituted path too.
    /// </summary>
    /// <remarks>
    /// Word spacing is what justified text is built from, so this is the commonest way a lost term
    /// would reach a real page. Tested separately from <c>Tc</c> because the two are not
    /// interchangeable: <c>Tc</c> is added to every glyph, <c>Tw</c> only where
    /// <c>codes[i].IsWordSpaceCode</c>. Adding both unconditionally would pass a
    /// <c>Tc</c>-only test and inflate every glyph here.
    /// <para>Three spaces in "H H H H" ⇒ <c>30 Tw</c> must widen the run by exactly 90 pt, and the
    /// four H glyphs must not move relative to one another beyond that.</para>
    /// </remarks>
    [Fact]
    public void WordSpacing_SubstitutedFont_WidensOnlyTheSpaces()
    {
        using var none = PixelAssert.Render(WordSpacingPdf(0));
        using var wide = PixelAssert.Render(WordSpacingPdf(30));

        var n = PixelAssert.InkBounds(none);
        var w = PixelAssert.InkBounds(wide);
        Assert.True(n.HasValue && w.HasValue, "one of the runs painted nothing");

        int grew = (w!.Value.right - w.Value.left) - (n!.Value.right - n.Value.left);
        Assert.InRange(grew, 90 - 4, 90 + 4);
    }

    /// <summary>Four glyphs separated by three spaces, so only <c>Tw</c> can widen it.</summary>
    private static byte[] WordSpacingPdf(int tw) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 100] /Contents 4 0 R" +
            " /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("", $"BT /F1 60 Tf {tw} Tw 20 25 Td (H H H H) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

    /// <summary>
    /// §12.5.5: an annotation's <c>/CA</c> constant opacity applies to its <b>appearance stream</b>,
    /// not only to the synthetic shape drawn when there is no <c>/AP</c>.
    /// </summary>
    /// <remarks>
    /// Both renderers read <c>/CA</c> in their fallback path alone, so a half-transparent stamp
    /// carrying an <c>/AP</c> painted fully opaque in each. **A shared deviation is invisible to a
    /// raster-versus-SVG comparison** — only the spec, or a fixture with a known right answer,
    /// catches one. Fixed on both sides in the same change.
    /// <para>The appearance fills its rect with pure black; at <c>/CA 0.5</c> over a white page the
    /// result must be mid-grey. Asserted as a range around 128 rather than exactly, since the
    /// composite goes through 8-bit rounding.</para>
    /// </remarks>
    [Fact]
    public void AnnotationConstantAlpha_AppliesToItsAppearanceStream()
    {
        using var opaque = PixelAssert.Render(AnnotAlphaPdf(null));
        using var half   = PixelAssert.Render(AnnotAlphaPdf("/CA 0.5"));

        Assert.InRange(PixelAssert.At(opaque, 50, 50).Red, 0, 8);
        Assert.InRange(PixelAssert.At(half, 50, 50).Red, 118, 138);
    }

    /// <summary>A 100×100 page with one annotation whose /AP fills its rect with black.</summary>
    private static byte[] AnnotAlphaPdf(string caEntry) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Annots [6 0 R] /Resources << >> >>",
            Stream("", " "),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Annot /Subtype /Square /Rect [0 0 100 100]" +
            (caEntry != null ? " " + caEntry : "") + " /AP << /N 7 0 R >> >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 100 100]" +
            " /Length 26 >>\nstream\n0 0 0 rg 0 0 100 100 re f\nendstream");

    /// <summary>Five glyphs, so four <c>Tc</c> gaps fall inside the measured extent.</summary>
    private static byte[] CharSpacingPdf(int tc) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 100] /Contents 4 0 R" +
            " /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("", $"BT /F1 60 Tf {tc} Tc 20 25 Td (HHHHH) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

    /// <summary>A single-glyph run, so the width is the glyph and not the spacing between glyphs.</summary>
    private static byte[] HorizontalScalingPdf(int tzPercent) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R" +
            " /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("", $"BT /F1 60 Tf {tzPercent} Tz 20 25 Td (H) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

    /// <summary>Five glyphs, so inter-glyph advance dominates the measured width.</summary>
    private static byte[] HorizontalScalingRunPdf(int tzPercent) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 100] /Contents 4 0 R" +
            " /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("", $"BT /F1 60 Tf {tzPercent} Tz 20 25 Td (HHHHH) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

    // ====================================================================
    //  Vertical writing — the position vector
    // ====================================================================

    /// <summary>
    /// §9.7.4.3: in vertical writing a glyph is drawn at <c>currentPoint − v</c>, where the position
    /// vector <c>v</c> maps its horizontal origin onto its vertical one. Only the advance
    /// (<c>w1y</c>) was read, so the pen stepped down correctly and every glyph was then painted on
    /// the <b>horizontal</b> origin — offset up and to the right of its column.
    /// </summary>
    /// <remarks>
    /// Asserted as a <b>difference</b> between two renders of the same text, because the absolute
    /// glyph position depends on the substitute font the host supplies. <c>/DW2 [880 -1000]</c> vs
    /// <c>[0 -1000]</c> changes only <c>v</c>'s y component, so any vertical displacement between
    /// the two inked regions can only come from the position vector.
    /// </remarks>
    [Fact]
    public void VerticalWriting_PositionVector_ShiftsTheGlyphOffTheHorizontalOrigin()
    {
        using var withVector = PixelAssert.Render(VerticalTextPdf("[880 -1000]"));
        using var noVector   = PixelAssert.Render(VerticalTextPdf("[0 -1000]"));

        var a = PixelAssert.InkBounds(withVector);
        var b = PixelAssert.InkBounds(noVector);

        Assert.True(a.HasValue, "the vertical run painted nothing at all");
        Assert.True(b.HasValue, "the control run painted nothing at all");

        // /DW2[0] 880 drops the glyph 0.88 em below where /DW2[0] 0 puts it. Rows count downward,
        // so "lower on the page" is a LARGER top.
        Assert.True(a!.Value.top > b!.Value.top,
            $"the position vector did not move the glyph: with v top={a.Value.top}, " +
            $"without v top={b.Value.top}");
    }

    // ====================================================================
    //  Fixtures
    // ====================================================================

    /// <summary>A one-glyph vertical (wMode 1) CID run, with the given <c>/DW2</c>.</summary>
    private static byte[] VerticalTextPdf(string dw2) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R" +
            " /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("", "BT /F1 48 Tf 100 150 Td <0041> Tj ET"),
            "<< /Type /Font /Subtype /Type0 /BaseFont /Arial /Encoding /Identity-V" +
            " /DescendantFonts [6 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Arial" +
            " /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>" +
            $" /FontDescriptor 7 0 R /DW 1000 /DW2 {dw2} >>",
            "<< /Type /FontDescriptor /FontName /Arial /Flags 4 /ItalicAngle 0" +
            " /Ascent 900 /Descent -200 /CapHeight 700 /StemV 80" +
            " /FontBBox [-200 -200 1000 900] >>");

    /// <summary>Opaque green backdrop, then an opaque red square through the given blend mode.</summary>
    private static byte[] BlendPdf(string blendMode) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /ExtGState << /G1 5 0 R >> >> >>",
            Stream("", "0 1 0 rg 0 0 100 100 re f /G1 gs 1 0 0 rg 0 0 100 100 re f"),
            $"<< /Type /ExtGState /BM /{blendMode} >>");

    private static byte[] LatticeMeshPdf()
    {
        // /VerticesPerRow 2, four vertices, no flags: x y r g b, all red.
        const string v = "0000FF0000" + "FF00FF0000" + "00FFFF0000" + "FFFFFF0000";
        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R" +
            " /Resources << /Shading << /Sh1 5 0 R >> >> >>",
            Stream("", "/Sh1 sh"),
            Stream("/ShadingType 5 /ColorSpace /DeviceRGB /VerticesPerRow 2 /BitsPerCoordinate 8" +
                   " /BitsPerComponent 8 /Decode [0 300 0 300 0 1 0 1 0 1] /Filter /ASCIIHexDecode",
                   v + ">"));
    }

    /// <summary>
    /// A mesh shading's <c>/Decode</c> array maps its raw colour samples onto the colour space's
    /// range, and a non-identity component range must actually be applied (§8.7.4.5.1, Table 80).
    /// </summary>
    /// <remarks>
    /// <para><b>Found by mutation, not by reading.</b> Types 4–7 shared one copy of this header
    /// parsing after a consolidation, so the survivor was mutated to check it was reached:
    /// replacing the component-range read with the identity <c>0…1</c> left <b>all 3 027</b> Base
    /// tests green. Every mesh and patch fixture in the suite was written with an identity range —
    /// <c>/Decode [0 300 0 300 0 1 0 1 0 1]</c> — so nothing could tell the two apart.</para>
    ///
    /// <para>Both rows draw vertices whose raw grey sample is <c>0xFF</c>, the maximum. Under the
    /// identity range that is white; under <c>[0 0.5]</c> it is mid grey. The control row is what
    /// makes the first one mean something: without it, a renderer that ignored <c>/Decode</c>
    /// entirely and always painted white would fail one row and pass the other, which is the same
    /// evidence as a renderer that reads it correctly.</para>
    /// </remarks>
    [Theory]
    [InlineData("0 0.5", 128)]   // the case that had no coverage
    [InlineData("0 1",   255)]   // control: the identity range every other fixture uses
    public void MeshShading_AppliesTheDecodeComponentRange(string componentRange, int expectedGrey)
    {
        using var bmp = PixelAssert.Render(GreyMeshPdf(componentRange));
        var c = PixelAssert.At(bmp, 150, 150);

        Assert.Equal(expectedGrey, c.Red,   tolerance: 6);
        Assert.Equal(expectedGrey, c.Green, tolerance: 6);
        Assert.Equal(expectedGrey, c.Blue,  tolerance: 6);
    }

    /// <summary>
    /// A free-form (type 4) Gouraud triangle in DeviceGray, every vertex carrying the maximum raw
    /// sample, so the painted grey is decided entirely by the <c>/Decode</c> component range.
    /// </summary>
    private static byte[] GreyMeshPdf(string componentRange)
    {
        // Per vertex: flag, x, y, one grey component — all 8-bit.
        // The triangle spans the page: (0,0), (300,0) and an apex at (150,300), so the sample
        // point at the centre is comfortably inside it.
        const string vertices =
            "00" + "00" + "00" + "FF" +
            "00" + "FF" + "00" + "FF" +
            "00" + "80" + "FF" + "FF";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R" +
            " /Resources << /Shading << /Sh1 5 0 R >> >> >>",
            Stream("", "/Sh1 sh"),
            Stream("/ShadingType 4 /ColorSpace /DeviceGray /BitsPerCoordinate 8" +
                   " /BitsPerComponent 8 /BitsPerFlag 8" +
                   $" /Decode [0 300 0 300 {componentRange}] /Filter /ASCIIHexDecode",
                   vertices + ">"));
    }

    private static byte[] CoonsPatchPdf(int shadingType)
    {
        // flag 0: 12 boundary control points (type 6) or 16 (type 7), then 4 corner colours.
        // The points walk the square 0..255 counter-clockwise at thirds.
        string boundary =
            "0000" + "0055" + "00AA" + "00FF" +   // left edge, bottom → top
            "55FF" + "AAFF" + "FFFF" +            // top edge
            "FFAA" + "FF55" + "FF00" +            // right edge, top → bottom
            "AA00" + "5500";                      // bottom edge
        // Type 7 adds four interior control points; put them inside the square.
        string interior = shadingType == 7 ? "5555" + "55AA" + "AAAA" + "AA55" : "";
        const string corners = "FF0000" + "FF0000" + "FF0000" + "FF0000";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R" +
            " /Resources << /Shading << /Sh1 5 0 R >> >> >>",
            Stream("", "/Sh1 sh"),
            Stream($"/ShadingType {shadingType} /ColorSpace /DeviceRGB /BitsPerCoordinate 8" +
                   " /BitsPerComponent 8 /BitsPerFlag 8 /Decode [0 300 0 300 0 1 0 1 0 1]" +
                   " /Filter /ASCIIHexDecode",
                   "00" + boundary + interior + corners + ">"));
    }

    private static byte[] AlphaSoftMaskPdf() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /ExtGState << /G1 5 0 R >> >> >>",
            Stream("", "/G1 gs 1 0 0 rg 0 0 100 100 re f"),
            "<< /Type /ExtGState /SMask << /S /Alpha /G 6 0 R >> >>",
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 100 100]" +
                   " /Group << /S /Transparency /CS /DeviceGray >>",
                   "0 g 0 0 50 50 re f"));

    private static byte[] TextClipPdf(int renderMode) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R" +
            " /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("", $"BT /F1 48 Tf {renderMode} Tr 10 30 Td (III) Tj ET " +
                       "0 0 1 rg 0 0 200 100 re f"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

    private static string Stream(string dictEntries, string data) =>
        $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
