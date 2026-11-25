// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Second raster-vs-SVG parity round (GAPS.md §2, "Exhaustive raster-vs-SVG parity sweep",
/// items 1, 2, 3, 5, 6 and 8).
///
/// <para><b>Everything here asserts on the parsed SVG</b> (<see cref="XDocument"/>) or on decoded
/// pixels, never on a substring — the same rule <c>SvgRasterParityTests</c> established, and the
/// reason these divergences survived a green suite in the first place. Where the claim is about the
/// two renderers <i>agreeing</i>, both are run and the results are compared directly rather than
/// each being checked against a hand-written expectation.</para>
/// </summary>
public sealed class SvgParityRound2Tests
{
    // ====================================================================
    //  1. Annotations must render their /AP appearance stream (§12.5.5)
    // ====================================================================

    /// <summary>
    /// §12.5.5: an annotation with an appearance stream <i>shall</i> be rendered from it. The SVG
    /// pass ignored <c>/AP</c> completely and drew from the annotation's styling keys, so every
    /// subtype outside its eleven-case switch — <b>every AcroForm field and every signature</b> —
    /// exported as the <c>default</c> dashed grey rectangle while the raster renderer drew the real
    /// appearance.
    /// </summary>
    /// <remarks>
    /// The negative half named the <b>dashed</b> placeholder until a Widget gained a synthetic
    /// fallback of its own, at which point no Widget could emit that dash and the assertion could
    /// no longer fail. It now names what the fallback actually draws — a stroked rectangle over the
    /// <c>/Rect</c> — which is the thing that must not appear on top of a real appearance.
    /// </remarks>
    [Fact]
    public void WidgetAppearanceStream_IsRendered_NotTheSyntheticFallback()
    {
        var doc = XDocument.Parse(RenderAnnotSvg(
            annotExtras: "/Subtype /Widget /AP << /N 6 0 R >>",
            appearance: AppearanceStream("[0 0 10 10]", null, "0 0 1 rg 0 0 10 10 re f")));

        // The appearance's own blue fill reached the document…
        var blue = Assert.Single(doc.Descendants()
            .Where(e => e.Name.LocalName == "path" && e.Attribute("fill")?.Value == "rgb(0,0,255)"));
        Assert.Contains(blue.Ancestors(),
            a => a.Attribute("data-annot-subtype")?.Value == "Widget");

        // …and the synthetic drawing did not: the appearance stream owns the annotation outright.
        Assert.DoesNotContain(doc.Descendants(),
            e => e.Name.LocalName == "rect" && e.Attribute("stroke") != null);
    }

    /// <summary>
    /// Algorithm 8.1, steps 1–3: the transformed <c>/BBox</c> is fitted onto <c>/Rect</c>. Asserted
    /// through the emitted geometry, which is the only observable that can distinguish "fitted" from
    /// "drawn at the appearance's own coordinates".
    /// </summary>
    [Fact]
    public void AppearanceStream_IsScaledAndTranslatedOntoTheAnnotationRect()
    {
        var doc = XDocument.Parse(RenderAnnotSvg(
            annotExtras: "/Subtype /Widget /AP << /N 6 0 R >>",
            appearance: AppearanceStream("[0 0 10 10]", null, "0 0 1 rg 0 0 10 10 re f")));

        var blue = doc.Descendants().Single(
            e => e.Name.LocalName == "path" && e.Attribute("fill")?.Value == "rgb(0,0,255)");

        // /Rect is [100 100 300 200]; the 10x10 BBox maps onto exactly that box.
        Assert.Equal("M100,100 L300,100 L300,200 L100,200 Z", blue.Attribute("d")?.Value);
    }

    /// <summary>
    /// Step 1 is the part that is easy to skip: the <c>/BBox</c> corners go through the stream's
    /// <c>/Matrix</c> <i>before</i> the fit is computed. A 90° <c>/Matrix</c> over a non-square
    /// <c>/BBox</c> swaps the extent, so a fit computed from the untransformed box lands the content
    /// somewhere else entirely; done right, the appearance still exactly fills <c>/Rect</c>.
    /// </summary>
    [Fact]
    public void AppearanceStream_MatrixIsAppliedBeforeTheFitIsComputed()
    {
        var doc = XDocument.Parse(RenderAnnotSvg(
            annotExtras: "/Subtype /Widget /AP << /N 6 0 R >>",
            appearance: AppearanceStream("[0 0 10 20]", "[0 1 -1 0 0 0]", "0 0 1 rg 0 0 10 20 re f")));

        var blue = doc.Descendants().Single(
            e => e.Name.LocalName == "path" && e.Attribute("fill")?.Value == "rgb(0,0,255)");

        // The four corners of /Rect, visited in the order the rotated form space produces them.
        Assert.Equal("M300,100 L300,200 L100,200 L100,100 Z", blue.Attribute("d")?.Value);
    }

    /// <summary>
    /// §12.5.5: when <c>/N</c> is a dictionary of sub-states, <c>/AS</c> names the current one. This
    /// is what makes a checkbox render checked rather than unchecked, and it had no expression at
    /// all in the SVG path.
    /// </summary>
    [Theory]
    [InlineData("/On",  "rgb(0,255,0)")]
    [InlineData("/Off", "rgb(255,0,0)")]
    public void AppearanceSubState_IsSelectedByAs(string asName, string expectedFill)
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << >> /Annots [5 0 R] >>",
            Stream("", " "),
            "<< /Type /Annot /Subtype /Widget /Rect [100 100 300 200]" +
            $" /AS {asName} /AP << /N << /On 6 0 R /Off 7 0 R >> >> >>",
            AppearanceStream("[0 0 10 10]", null, "0 1 0 rg 0 0 10 10 re f"),
            AppearanceStream("[0 0 10 10]", null, "1 0 0 rg 0 0 10 10 re f"));

        using var ms = new MemoryStream(pdf);
        var doc = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(ms, 0));

        var painted = doc.Descendants()
            .Where(e => e.Name.LocalName == "path" && (e.Attribute("fill")?.Value ?? "none") != "none")
            .ToList();
        Assert.Equal(expectedFill, Assert.Single(painted).Attribute("fill")?.Value);
    }

    /// <summary>
    /// §8.10.2: the <c>/BBox</c> clips the appearance. An appearance that paints outside its box —
    /// which producers do, relying on the clip — must not bleed past <c>/Rect</c>.
    /// </summary>
    [Fact]
    public void AppearanceStream_ContentIsClippedToTheBBox()
    {
        var doc = XDocument.Parse(RenderAnnotSvg(
            annotExtras: "/Subtype /Widget /AP << /N 6 0 R >>",
            appearance: AppearanceStream("[0 0 10 10]", null, "0 0 1 rg -50 -50 200 200 re f")));

        var blue = doc.Descendants().Single(
            e => e.Name.LocalName == "path" && e.Attribute("fill")?.Value == "rgb(0,0,255)");
        var clipGroup = Assert.Single(blue.Ancestors()
            .Where(a => a.Name.LocalName == "g" && a.Attribute("clip-path") != null));

        string clipId = clipGroup.Attribute("clip-path")!.Value.Trim('u', 'r', 'l', '(', ')', '#');
        var clip = doc.Descendants().Single(
            e => e.Name.LocalName == "clipPath" && e.Attribute("id")?.Value == clipId);

        // The clip is the /BBox mapped onto /Rect — i.e. exactly the annotation rectangle.
        Assert.Equal("M100,100 L300,100 L300,200 L100,200 Z",
            clip.Descendants().Single(e => e.Name.LocalName == "path").Attribute("d")?.Value);
    }

    /// <summary>
    /// The fallback must survive: an annotation with no appearance stream still gets the synthetic
    /// drawing, which is the other half of the split the raster path makes.
    /// </summary>
    [Fact]
    public void SquareAnnotationWithoutAppearance_StillDrawsTheSyntheticRectangle()
    {
        var doc = XDocument.Parse(RenderAnnotSvg(
            annotExtras: "/Subtype /Square /C [1 0 0]",
            appearance: "<< /Type /Null >>"));   // object 6 exists but nothing references it

        var rect = Assert.Single(doc.Descendants().Where(e => e.Name.LocalName == "rect"
            && e.Attribute("stroke") != null));
        Assert.Equal("rgb(255,0,0)", rect.Attribute("stroke")?.Value);
        Assert.Equal("100", rect.Attribute("x")?.Value);
    }

    /// <summary>
    /// And a Widget with no appearance still gets its synthetic border — so the test above ("the
    /// synthetic drawing is gone") is about <c>/AP</c> having taken over, not about the synthetic
    /// path having been deleted.
    /// </summary>
    /// <remarks>
    /// A Widget has a fallback of its own: it draws its field border, as the raster path does. The
    /// <b>dashed</b> placeholder is for subtypes nothing models — asserted in
    /// <c>AnnotationFallbackRenderingTests</c>, which owns that arm.
    /// <para>The border is the widget's own — Table 192's <c>/MK /BC</c> — so the fixture declares
    /// one; a widget that declares none has no border to draw (<c>WidgetBorderFallbackTests</c>).</para>
    /// </remarks>
    [Fact]
    public void WidgetWithoutAppearance_StillDrawsItsSyntheticBorder()
    {
        var doc = XDocument.Parse(RenderAnnotSvg(
            annotExtras: "/Subtype /Widget /MK << /BC [0 0 0] >>",
            appearance: "<< /Type /Null >>"));

        // /Rect is [100 100 300 200] — the border covers it, and is not dashed.
        var rect = Assert.Single(doc.Descendants().Where(
            e => e.Name.LocalName == "rect" && e.Attribute("stroke") != null));
        Assert.Equal("100", rect.Attribute("x")?.Value);
        Assert.Equal("200", rect.Attribute("width")?.Value);
        Assert.Null(rect.Attribute("stroke-dasharray"));
    }

    /// <summary>
    /// The <c>/F</c> Hidden bit still wins over an appearance stream (§12.5.3), so switching to the
    /// /AP path cannot have re-introduced content the raster path omits.
    /// </summary>
    [Fact]
    public void HiddenAnnotation_WithAppearanceStream_DrawsNothing()
    {
        var doc = XDocument.Parse(RenderAnnotSvg(
            annotExtras: "/Subtype /Widget /F 2 /AP << /N 6 0 R >>",
            appearance: AppearanceStream("[0 0 10 10]", null, "0 0 1 rg 0 0 10 10 re f")));

        Assert.DoesNotContain(doc.Descendants(),
            e => e.Name.LocalName == "path" && e.Attribute("fill")?.Value == "rgb(0,0,255)");
    }

    /// <summary>
    /// Cross-renderer: the appearance's fill must land at the same page position in both outputs.
    /// The raster render is the reference, so this cannot be satisfied by the SVG and the assertion
    /// agreeing on a wrong number.
    /// </summary>
    [Fact]
    public void AppearanceStreamPlacement_SvgMatchesTheRasterRender()
    {
        byte[] pdf = AnnotPdf(
            annotExtras: "/Subtype /Widget /AP << /N 6 0 R >>",
            appearance: AppearanceStream("[0 0 10 10]", null, "0 0 1 rg 0 0 10 10 re f"));

        using var bmp = PixelAssert.Render(pdf);
        var bounds = PixelAssert.InkBounds(bmp);
        Assert.NotNull(bounds);

        using var ms = new MemoryStream(pdf);
        var svgDoc = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(ms, 0));
        var blue = svgDoc.Descendants().Single(
            e => e.Name.LocalName == "path" && e.Attribute("fill")?.Value == "rgb(0,0,255)");

        // Raster ink box (device px at 72 dpi == PDF points) vs the SVG path's own extent.
        var (l, t, r, b) = bounds!.Value;
        Assert.InRange(l,  99, 101);
        Assert.InRange(r, 299, 301);
        // Page height 792, /Rect y 100..200 → rows 592..692 from the top.
        Assert.InRange(t, 591, 593);
        Assert.InRange(b, 691, 693);
        Assert.Equal("M100,100 L300,100 L300,200 L100,200 Z", blue.Attribute("d")?.Value);
    }

    // ====================================================================
    //  2. Line width is a USER-space length: the CTM applies to it
    // ====================================================================

    /// <summary>
    /// §8.4.3.2. Path coordinates already carry the CTM (they are emitted through <c>TxPt</c>), so
    /// leaving the width in user units made <c>3 0 0 3 0 0 cm 2 w</c> stroke a third as thick as the
    /// raster path drew it.
    /// </summary>
    [Fact]
    public void PathStrokeWidth_IsScaledByTheCtm()
    {
        var stroked = Assert.Single(StrokedPaths(
            RenderSvg("q 3 0 0 3 0 0 cm 2 w 0 0 0 RG 10 10 m 60 10 l S Q")));

        Assert.Equal(6f, Attr(stroked, "stroke-width"), 4);
    }

    /// <summary>
    /// A non-uniform CTM has no single scale, so both renderers take the geometric mean of the two
    /// axis lengths (<c>PdfRenderContext.BuildStrokePaint</c>). Chosen so that the mean (2) differs
    /// from both axes (4 and 1) and from their arithmetic mean (2.5).
    /// </summary>
    [Fact]
    public void PathStrokeWidth_NonUniformCtm_UsesTheGeometricMean()
    {
        var stroked = Assert.Single(StrokedPaths(
            RenderSvg("q 4 0 0 1 0 0 cm 3 w 0 0 0 RG 10 10 m 60 10 l S Q")));

        Assert.Equal(6f, Attr(stroked, "stroke-width"), 4);   // 3 * sqrt(4*1)
    }

    /// <summary>
    /// Cross-renderer, in decoded pixels: at 72 dpi one PDF point is one device pixel, so the
    /// thickness of the raster stroke <i>is</i> the width the SVG must declare.
    /// </summary>
    [Fact]
    public void ScaledStrokeWidth_SvgAgreesWithTheRasterStrokeThickness()
    {
        // 10x CTM over a 2 pt line = a 20 pt stroke, drawn as a long horizontal rule.
        byte[] pdf = SinglePagePdf("q 10 0 0 10 0 0 cm 2 w 0 0 0 RG 2 40 m 40 40 l S Q");

        using var bmp = PixelAssert.Render(pdf);
        var bounds = PixelAssert.InkBounds(bmp);
        Assert.NotNull(bounds);
        int rasterThickness = bounds!.Value.bottom - bounds.Value.top + 1;

        using var ms = new MemoryStream(pdf);
        float svgWidth = Attr(Assert.Single(StrokedPaths(PdfSvgRenderer.RenderPageToSvg(ms, 0))),
                              "stroke-width");

        // Anti-aliased edges put the raster count within a pixel either side of the true width;
        // the defect being pinned was a factor of ten, not a rounding difference.
        Assert.InRange(svgWidth, rasterThickness - 2f, rasterThickness + 2f);
    }

    /// <summary>The zero-width hairline substitution (§8.4.3.2) is a PAGE-unit length and must not
    /// itself be multiplied by the CTM, or a scaled-up page would draw a fat "hairline".</summary>
    [Fact]
    public void ZeroLineWidth_UnderAScaledCtm_StillEmitsTheOnePixelHairline()
    {
        var stroked = Assert.Single(StrokedPaths(
            RenderSvg("q 10 0 0 10 0 0 cm 0 w 0 0 0 RG 1 1 m 6 1 l S Q")));

        Assert.Equal((float)PdfUnits.PxToPoint, Attr(stroked, "stroke-width"), 4);
    }

    /// <summary>
    /// A degenerate CTM has no scale to apply. The raster path substitutes a factor of <b>1</b>
    /// there (<c>if (ctmScale &lt;= 0) ctmScale = 1f;</c>) rather than collapsing the width to zero,
    /// so the width passes through unchanged; the SVG side must land on the same number, not on the
    /// hairline. Pinned because "guard against a zero divisor" has two plausible answers and only
    /// one of them keeps the two renderers together.
    /// </summary>
    [Fact]
    public void DegenerateCtm_PositiveLineWidth_PassesTheWidthThroughUnscaled()
    {
        var stroked = Assert.Single(StrokedPaths(
            RenderSvg("q 0 0 0 0 0 0 cm 5 w 0 0 0 RG 10 10 m 60 10 l S Q")));

        Assert.Equal(5f, Attr(stroked, "stroke-width"), 4);
    }

    /// <summary>
    /// The <c>&lt;text&gt;</c> emitter divides the page width back out by its element transform, not by
    /// the font size alone, which is the whole transform only when Tm and the CTM are both unscaled —
    /// a <c>cm</c> would then make stroked text the wrong thickness.
    /// </summary>
    [Fact]
    public void TextStrokeWidth_AccountsForTheCtmAsWellAsTheFontSize()
    {
        var stroked = Assert.Single(StrokedTexts(RenderHelveticaSvg(
            "q 2 0 0 2 0 0 cm BT /F1 10 Tf 1 Tr 2 w 0 0 0 RG 50 700 Td (Hi) Tj ET Q")));

        // Page width 2*2 = 4; element scale 10*2 = 20; 4/20 = 0.2 — the same visible thickness the
        // unscaled case produces, which is the point: the two cancel only when both are counted.
        Assert.Equal(0.2f, Attr(stroked, "stroke-width"), 5);
    }

    /// <summary>
    /// The control for the pair: without the <c>cm</c>, the width halves relative to the element,
    /// because the element transform shrank but the page-space width did not.
    /// </summary>
    [Fact]
    public void TextStrokeWidth_WithoutACtm_IsTheWidthOverTheFontSize()
    {
        var stroked = Assert.Single(StrokedTexts(RenderHelveticaSvg(
            "BT /F1 10 Tf 1 Tr 2 w 0 0 0 RG 50 700 Td (Hi) Tj ET")));

        Assert.Equal(0.2f, Attr(stroked, "stroke-width"), 5);
    }

    /// <summary>
    /// The glyph-outline emitter is the worst case: its <c>&lt;use&gt;</c> children carry a
    /// glyph→page matrix that includes the 1/1000-em charstring scale, so an undivided page-space
    /// width came out roughly eighty times too thin and stroked text was invisible. The right value
    /// is the page width divided by that matrix's scale, which for a 10 pt font is
    /// <c>width / 10</c>.
    /// </summary>
    [EmbeddableFontFact("Hi")]
    public void GlyphOutlineStrokeWidth_IsDividedByTheGlyphMatrix()
    {
        byte[] ttf = EmbeddableFontProbe.BytesCovering("Hi");
        string svg = RenderEmbeddedFontSvg("BT /F1 10 Tf 1 Tr 2 w 0 0 0 RG 50 700 Td (Hi) Tj ET", ttf);

        var group = Assert.Single(XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "g" && e.Attribute("stroke") != null
                        && e.Attribute("stroke")!.Value != "none"));

        Assert.Equal(0.2f, Attr(group, "stroke-width"), 5);
    }

    /// <summary>And the CTM reaches that site too.</summary>
    [EmbeddableFontFact("Hi")]
    public void GlyphOutlineStrokeWidth_UnderAScaledCtm_StaysProportional()
    {
        byte[] ttf = EmbeddableFontProbe.BytesCovering("Hi");
        string svg = RenderEmbeddedFontSvg(
            "q 2 0 0 2 0 0 cm BT /F1 10 Tf 1 Tr 2 w 0 0 0 RG 50 300 Td (Hi) Tj ET Q", ttf);

        var group = Assert.Single(XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "g" && e.Attribute("stroke") != null
                        && e.Attribute("stroke")!.Value != "none"));

        // Page width 4, glyph matrix scale 20 → 0.2, i.e. the same ratio: the CTM appears in both
        // the numerator and the denominator, which is exactly what makes the stroke look right.
        Assert.Equal(0.2f, Attr(group, "stroke-width"), 5);
    }

    // ====================================================================
    //  3. Dash arrays: CTM-scaled, and the phase normalised
    // ====================================================================

    /// <summary>
    /// §8.4.3.6 dash lengths are user-space lengths, so the CTM applies to the array and the phase
    /// alike (<c>PdfRenderContext.BuildEvenDash</c>). Unscaled, the dashes stayed the size they
    /// would have been at CTM identity while the line they sat on grew.
    /// </summary>
    [Fact]
    public void DashArrayAndPhase_AreScaledByTheCtm()
    {
        var stroked = Assert.Single(StrokedPaths(
            RenderSvg("q 2 0 0 2 0 0 cm [6 3] 4 d 1 w 0 0 0 RG 10 10 m 200 10 l S Q")));

        Assert.Equal("12,6", stroked.Attribute("stroke-dasharray")?.Value);
        Assert.Equal("8",    stroked.Attribute("stroke-dashoffset")?.Value);
    }

    /// <summary>
    /// A negative phase is legal in PDF (§8.4.3.6: increment it until positive) and undefined in
    /// SVG, which was getting it verbatim. For an EVEN-length array the period is the sum.
    /// </summary>
    [Fact]
    public void NegativeDashPhase_EvenArray_IsNormalisedByTheSum()
    {
        var stroked = Assert.Single(StrokedPaths(
            RenderSvg("[6 3] -2 d 1 w 0 0 0 RG 10 10 m 200 10 l S")));

        Assert.Equal("6,3", stroked.Attribute("stroke-dasharray")?.Value);
        Assert.Equal("7",   stroked.Attribute("stroke-dashoffset")?.Value);   // -2 + 9
    }

    /// <summary>
    /// For an ODD-length array the on/off roles swap on each repeat, so the effective period is
    /// TWICE the sum. Reducing modulo the sum instead lands half a period off and inverts the
    /// dashes — the reason the raster path has <c>EffectiveDashPeriod</c> rather than a plain sum.
    /// </summary>
    [Fact]
    public void NegativeDashPhase_OddArray_IsNormalisedByTwiceTheSum()
    {
        var stroked = Assert.Single(StrokedPaths(
            RenderSvg("[3] -1 d 1 w 0 0 0 RG 10 10 m 200 10 l S")));

        Assert.Equal("3", stroked.Attribute("stroke-dasharray")?.Value);
        Assert.Equal("5", stroked.Attribute("stroke-dashoffset")?.Value);     // -1 + 2*3, not -1 + 3
    }

    /// <summary>
    /// An all-zero dash array is invalid in PDF; both SVG and Skia draw a solid line for it, so the
    /// attribute is omitted rather than emitted as <c>"0,0"</c>.
    /// </summary>
    [Fact]
    public void ZeroLengthDashArray_EmitsNoDashAttributes()
    {
        var stroked = Assert.Single(StrokedPaths(
            RenderSvg("[0 0] 0 d 1 w 0 0 0 RG 10 10 m 200 10 l S")));

        Assert.Null(stroked.Attribute("stroke-dasharray"));
        Assert.Null(stroked.Attribute("stroke-dashoffset"));
    }

    /// <summary>A negative entry is invalid and is clamped, as the raster path clamps it.</summary>
    [Fact]
    public void NegativeDashEntry_IsClampedToZero()
    {
        var stroked = Assert.Single(StrokedPaths(
            RenderSvg("[6 -3] 0 d 1 w 0 0 0 RG 10 10 m 200 10 l S")));

        Assert.Equal("6,0", stroked.Attribute("stroke-dasharray")?.Value);
    }

    // ====================================================================
    //  5. (GAPS item 4) A CMYK JPEG's /Decode must not be applied twice
    // ====================================================================

    /// <summary>
    /// A four-component JPEG's <c>/Decode [1 0 …]</c> describes the Adobe APP14 inversion the JPEG
    /// decoder has <b>already</b> performed. The raster path skips the array for exactly this case
    /// (<c>if (GetColorComponents(imageDict) != 4)</c>); the SVG path applied it to the decoded RGB,
    /// so every Adobe-marked CMYK JPEG exported as a photographic negative.
    /// </summary>
    [Fact]
    public void FourComponentJpeg_DecodeArrayIsNotAppliedASecondTime()
    {
        byte[] jpeg = SolidJpeg(new SKColor(200, 30, 40));
        string svg = RenderJpegImageSvg(jpeg,
            "/ColorSpace /DeviceCMYK /Decode [1 0 1 0 1 0 1 0]");

        var px = DecodedImagePixel(svg);
        Assert.True(PixelAssert.Near(px, new SKColor(200, 30, 40), 12),
            $"expected the decoder's own output, got {px} — the /Decode array was re-applied.");
    }

    /// <summary>
    /// The control, and the reason the rule is a rule and not a blanket skip: a THREE-component
    /// JPEG's <c>/Decode</c> is about its RGB channels and must still be honoured.
    /// </summary>
    [Fact]
    public void ThreeComponentJpeg_DecodeArrayIsStillApplied()
    {
        byte[] jpeg = SolidJpeg(new SKColor(200, 30, 40));
        string svg = RenderJpegImageSvg(jpeg, "/ColorSpace /DeviceRGB /Decode [1 0 1 0 1 0]");

        var px = DecodedImagePixel(svg);
        Assert.True(PixelAssert.Near(px, new SKColor(55, 225, 215), 12),
            $"expected the inverted colour, got {px} — the /Decode array was dropped.");
    }

    // ====================================================================
    //  6. (GAPS item 6) Image component count for array colour spaces
    // ====================================================================

    /// <summary>
    /// <c>[/ICCBased … /N 1]</c> is a one-component image. The decoder assumed three for every array
    /// colour space but Indexed and DeviceN, so the buffer was two-thirds short of the assumed
    /// layout and the image was dropped from the export entirely.
    /// </summary>
    [Fact]
    public void IccBasedGrayImage_IsDecoded_NotDropped()
    {
        // 2x1 one-component image: black, white.
        string svg = RenderRawImageSvg("/ColorSpace 6 0 R", "00FF>", width: 2, height: 1,
            extraObjects: new[] { "[/ICCBased 7 0 R]", Stream("/N 1", "  ") });

        using var bmp = DecodeEmbeddedImage(svg);
        Assert.Equal(2, bmp.Width);
        Assert.True(PixelAssert.Near(bmp.GetPixel(0, 0), SKColors.Black, 4));
        Assert.True(PixelAssert.Near(bmp.GetPixel(1, 0), SKColors.White, 4));
    }

    /// <summary>
    /// <c>/N 4</c> is CMYK. Read as RGB triples the samples desynchronise after the first pixel, so
    /// the image rendered as noise rather than as its colours.
    /// </summary>
    [Fact]
    public void IccBasedCmykImage_IsReadAsFourComponents()
    {
        // One pixel of pure cyan: C=1 M=0 Y=0 K=0 → rgb(0,255,255).
        string svg = RenderRawImageSvg("/ColorSpace 6 0 R", "FF000000>", width: 1, height: 1,
            extraObjects: new[] { "[/ICCBased 7 0 R]", Stream("/N 4", "  ") });

        using var bmp = DecodeEmbeddedImage(svg);
        Assert.True(PixelAssert.Near(bmp.GetPixel(0, 0), new SKColor(0, 255, 255), 4),
            $"got {bmp.GetPixel(0, 0)}");
    }

    /// <summary>
    /// A <c>/Separation</c> image is one tint per pixel, run through the space's tint transform. It
    /// was dropped: one component was read as three, and nothing evaluated the transform.
    /// </summary>
    [Fact]
    public void SeparationImage_IsDecodedThroughItsTintTransform()
    {
        // Tint 1 → red, tint 0 → white. Samples: full tint, no tint.
        const string tint = "<< /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [1 0 0] /N 1 >>";
        string svg = RenderRawImageSvg("/ColorSpace 6 0 R", "FF00>", width: 2, height: 1,
            extraObjects: new[] { $"[/Separation /Spot /DeviceRGB {tint}]" });

        using var bmp = DecodeEmbeddedImage(svg);
        Assert.True(PixelAssert.Near(bmp.GetPixel(0, 0), SKColors.Red, 4), $"got {bmp.GetPixel(0, 0)}");
        Assert.True(PixelAssert.Near(bmp.GetPixel(1, 0), SKColors.White, 4), $"got {bmp.GetPixel(1, 0)}");
    }

    /// <summary>
    /// A <c>/DeviceN</c> image WITH a tint transform took the grey-average fallback, on the strength
    /// of a comment claiming the writer had "no access to the function evaluator" — untrue since
    /// <c>RasteriseImage</c> became an instance method holding the document.
    /// </summary>
    [Fact]
    public void DeviceNImage_WithATintTransform_UsesIt_NotTheGreyAverage()
    {
        // Two inks; the transform maps (1,0) to pure blue, which no grey average can produce.
        const string tint = "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 1] /C1 [0 0 1] /N 1 >>";
        var events = new List<RenderDiagnosticKind>();
        string svg = CaptureDiagnostics(events, () =>
            RenderRawImageSvg("/ColorSpace 6 0 R", "FF00>", width: 1, height: 1,
                extraObjects: new[] { $"[/DeviceN [/A /B] /DeviceRGB {tint}]" }));

        using var bmp = DecodeEmbeddedImage(svg);
        Assert.True(PixelAssert.Near(bmp.GetPixel(0, 0), SKColors.Blue, 4), $"got {bmp.GetPixel(0, 0)}");
        Assert.DoesNotContain(RenderDiagnosticKind.DeviceNFallback, events);
    }

    /// <summary>
    /// A two-ink DeviceN with no tint transform — an array §8.6.6.5 does not allow — is refused, as
    /// the raster path refuses it: nothing is drawn and the decode failure is reported. Until batch
    /// 81 the SVG writer's own decode grey-averaged it and reported <c>DeviceNFallback</c>, so the
    /// same file exported a grey image and rendered none; one decode for both ends that.
    /// </summary>
    [Fact]
    public void DeviceNImage_WithoutATintTransform_IsRefusedAsTheRasterPathRefusesIt()
    {
        var events = new List<RenderDiagnosticKind>();
        string svg = CaptureDiagnostics(events, () =>
            RenderRawImageSvg("/ColorSpace 6 0 R", "FF00>", width: 1, height: 1,
                extraObjects: new[] { "[/DeviceN [/A /B] /DeviceRGB]" }));

        Assert.Contains(RenderDiagnosticKind.ImageDecodeFailed, events);
        Assert.DoesNotContain(RenderDiagnosticKind.DeviceNFallback, events);
        Assert.DoesNotContain("<image", svg, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cross-renderer: a <c>/CalGray</c> image is a gamma-corrected grey, not a DeviceGray one. The
    /// raster render is the reference — a shared expectation cannot make both wrong the same way.
    /// </summary>
    [Fact]
    public void CalGrayImage_SvgMatchesTheRasterRender()
    {
        const string cal = "[/CalGray << /WhitePoint [0.9505 1 1.089] /Gamma 2.2 >>]";
        byte[] pdf = RawImagePdf("/ColorSpace 6 0 R", "80>", width: 1, height: 1,
            extraObjects: new[] { cal });

        using var raster = PixelAssert.Render(pdf);
        var expected = PixelAssert.At(raster, 150, 500);   // inside the 100..300 x 400..600 placement

        using var ms = new MemoryStream(pdf);
        using var bmp = DecodeEmbeddedImage(PdfSvgRenderer.RenderPageToSvg(ms, 0));
        Assert.True(PixelAssert.Near(bmp.GetPixel(0, 0), expected, 4),
            $"SVG produced {bmp.GetPixel(0, 0)}, raster {expected}");
    }

    /// <summary>Same, for <c>/Lab</c> — three components, but not RGB ones.</summary>
    [Fact]
    public void LabImage_SvgMatchesTheRasterRender()
    {
        const string lab = "[/Lab << /WhitePoint [0.9505 1 1.089] /Range [-100 100 -100 100] >>]";
        // L* ≈ 50, a* ≈ +50, b* ≈ 0 — a mid magenta-ish colour no RGB reading would produce.
        byte[] pdf = RawImagePdf("/ColorSpace 6 0 R", "80BF80>", width: 1, height: 1,
            extraObjects: new[] { lab });

        using var raster = PixelAssert.Render(pdf);
        var expected = PixelAssert.At(raster, 150, 500);

        using var ms = new MemoryStream(pdf);
        using var bmp = DecodeEmbeddedImage(PdfSvgRenderer.RenderPageToSvg(ms, 0));
        Assert.True(PixelAssert.Near(bmp.GetPixel(0, 0), expected, 4),
            $"SVG produced {bmp.GetPixel(0, 0)}, raster {expected}");
    }

    /// <summary>
    /// The control for the pair above: the SVG really is reading the space, not coincidentally
    /// agreeing. A <c>/CalGray 0x80</c> and a <c>/DeviceGray 0x80</c> must differ, because /Gamma 2.2
    /// is what separates them.
    /// </summary>
    [Fact]
    public void CalGrayImage_DiffersFromTheSameSampleReadAsDeviceGray()
    {
        const string cal = "[/CalGray << /WhitePoint [0.9505 1 1.089] /Gamma 2.2 >>]";
        using var calBmp = DecodeEmbeddedImage(RenderRawImageSvg(
            "/ColorSpace 6 0 R", "80>", 1, 1, new[] { cal }));
        using var devBmp = DecodeEmbeddedImage(RenderRawImageSvg(
            "/ColorSpace /DeviceGray", "80>", 1, 1, Array.Empty<string>()));

        Assert.NotEqual(calBmp.GetPixel(0, 0), devBmp.GetPixel(0, 0));
    }

    // ====================================================================
    //  8. (GAPS item 8) Generated ids must be unique across child writers
    // ====================================================================

    /// <summary>
    /// Every generated id is minted per WRITER, and a page runs many of them — one per form XObject,
    /// pattern cell, soft mask, Type 3 glyph and annotation appearance, all writing into one
    /// document. A page that clips and invokes a form that also clips emitted two
    /// <c>id="clip1"</c>, and every <c>url(#clip1)</c> then resolved to whichever came first.
    /// </summary>
    [Fact]
    public void PageAndFormBothClip_EveryGeneratedIdIsUnique()
    {
        AssertAllIdsUnique(RenderClippingPageAndFormSvg());
    }

    /// <summary>
    /// The consequence, stated as the thing that actually breaks: a reference must resolve to
    /// exactly one definition. Asserted separately because "all ids distinct" and "no dangling or
    /// ambiguous reference" are different claims.
    /// </summary>
    [Fact]
    public void PageAndFormBothClip_EveryReferenceResolvesToExactlyOneElement()
    {
        AssertEveryReferenceResolves(RenderClippingPageAndFormSvg());
    }

    /// <summary>
    /// The text-clip counter was a second, independent per-writer counter, so a text clip on the
    /// page and one inside a form collided the same way.
    /// </summary>
    [Fact]
    public void TextClipOnThePageAndInsideAForm_EveryGeneratedIdIsUnique()
    {
        string svg = RenderHelveticaFormSvg(
            pageContent: "BT /F1 12 Tf 7 Tr 50 700 Td (Page) Tj ET /Fx1 Do",
            formContent: "BT /F1 12 Tf 7 Tr 50 600 Td (Form) Tj ET");

        AssertAllIdsUnique(svg);
        AssertEveryReferenceResolves(svg);
    }

    /// <summary>
    /// And the annotation pass shares the page's registry too, now that it runs real content
    /// streams: a clipping page plus a clipping appearance stream is the same collision again.
    /// </summary>
    /// <remarks>
    /// The appearance stream contains its own <c>W n</c> on purpose. Its <c>/BBox</c> clip already
    /// mints an id, but from the <c>psclip</c> prefix — so with per-writer counters the page's
    /// <c>clip1</c> and the appearance's <c>psclip1</c> do not collide and the test could not fail.
    /// A <c>W n</c> inside the appearance puts a second <c>clip1</c> in the document, which is the
    /// collision this is about. (Found by mutation: the first version of this fixture survived
    /// reverting <c>NextId</c> to a per-writer counter.)
    /// </remarks>
    [Fact]
    public void PageClipAndAnnotationAppearance_EveryGeneratedIdIsUnique()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << >> /Annots [5 0 R] >>",
            Stream("", "10 10 100 100 re W n 0 1 0 rg 20 20 50 50 re f"),
            "<< /Type /Annot /Subtype /Widget /Rect [100 100 300 200] /AP << /N 6 0 R >> >>",
            AppearanceStream("[0 0 10 10]", null, "0 0 8 8 re W n 0 0 1 rg 0 0 10 10 re f"));

        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        AssertAllIdsUnique(svg);
        AssertEveryReferenceResolves(svg);
    }

    // ====================================================================
    //  Helpers
    // ====================================================================

    private static void AssertAllIdsUnique(string svg)
    {
        var ids = XDocument.Parse(svg).Descendants()
            .Select(e => e.Attribute("id")?.Value)
            .Where(v => v != null)
            .ToList();

        var duplicates = ids.GroupBy(v => v, StringComparer.Ordinal)
                            .Where(g => g.Count() > 1)
                            .Select(g => g.Key)
                            .ToList();

        Assert.True(duplicates.Count == 0,
            $"duplicate generated id(s): {string.Join(", ", duplicates)} (of {ids.Count} total)");
    }

    private static void AssertEveryReferenceResolves(string svg)
    {
        var doc = XDocument.Parse(svg);
        var ids = doc.Descendants().Select(e => e.Attribute("id")?.Value)
                     .Where(v => v != null).ToList();

        var references = doc.Descendants()
            .SelectMany(e => e.Attributes())
            .Select(a => a.Value)
            .Where(v => v.StartsWith("url(#", StringComparison.Ordinal) && v.EndsWith(")", StringComparison.Ordinal))
            .Select(v => v.Substring(5, v.Length - 6))
            .ToList();

        Assert.NotEmpty(references);
        foreach (string r in references)
            Assert.Equal(1, ids.Count(i => string.Equals(i, r, StringComparison.Ordinal)));
    }

    private static string RenderClippingPageAndFormSvg()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /XObject << /Fx1 5 0 R >> >> >>",
            Stream("", "10 10 100 100 re W n 1 0 0 rg 20 20 50 50 re f /Fx1 Do"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 612 792]",
                   "200 200 100 100 re W n 0 0 1 rg 210 210 50 50 re f"));
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static string RenderHelveticaFormSvg(string pageContent, string formContent)
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /Font << /F1 6 0 R >> /XObject << /Fx1 5 0 R >> >> >>",
            Stream("", pageContent),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 612 792]" +
                   " /Resources << /Font << /F1 6 0 R >> >>", formContent),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    /// <summary>A form-XObject stream usable as an annotation appearance.</summary>
    private static string AppearanceStream(string bbox, string matrix, string content) =>
        Stream("/Type /XObject /Subtype /Form /BBox " + bbox +
               (matrix != null ? " /Matrix " + matrix : ""), content);

    private static byte[] AnnotPdf(string annotExtras, string appearance) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << >> /Annots [5 0 R] >>",
            Stream("", " "),
            $"<< /Type /Annot /Rect [100 100 300 200] {annotExtras} >>",
            appearance);

    private static string RenderAnnotSvg(string annotExtras, string appearance)
    {
        using var ms = new MemoryStream(AnnotPdf(annotExtras, appearance));
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    /// <summary>Solid-colour 8x8 JPEG, encoded by Skia so the bytes are a real codestream.</summary>
    private static byte[] SolidJpeg(SKColor colour)
    {
        using var bmp = new SKBitmap(8, 8, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(colour);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        return data.ToArray();
    }

    /// <summary>The single pixel every decoded image in these fixtures is uniform in.</summary>
    private static SKColor DecodedImagePixel(string svg)
    {
        using var bmp = DecodeEmbeddedImage(svg);
        return bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
    }

    private static string RenderJpegImageSvg(byte[] jpeg, string imageExtras, string[] extraObjects = null,
                                             string resourceExtras = "", string contentPrefix = "")
    {
        var sb = new StringBuilder();
        foreach (byte b in jpeg) sb.Append(b.ToString("X2"));
        sb.Append('>');

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            $" /Resources << /XObject << /Im1 5 0 R >> {resourceExtras} >> >>",
            Stream("", contentPrefix + "q 200 0 0 200 100 400 cm /Im1 Do Q"),
            Stream("/Type /XObject /Subtype /Image /Width 8 /Height 8 /BitsPerComponent 8 " +
                   imageExtras + " /Filter [/ASCIIHexDecode /DCTDecode]", sb.ToString()),
        };
        if (extraObjects != null) objects.AddRange(extraObjects);
        using var ms = new MemoryStream(Build(objects.ToArray()));
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    /// <summary>
    /// A JPEG passes through as itself only when nothing would change it. With a soft mask, a
    /// stencil <c>/Mask</c> or a transfer function in the state, the image goes through the shared
    /// decode and comes out a PNG with the effect baked in — the masks as alpha, the function on the
    /// colour — as the raster path draws it. Before batch 81 the writer's own decode did this; the
    /// gate is the same, and these pin it.
    /// </summary>
    [Fact]
    public void AJpegWithASoftMask_IsBakedIntoAPng()
    {
        string svg = RenderJpegImageSvg(SolidJpeg(SKColors.Red), "/ColorSpace /DeviceRGB /SMask 6 0 R",
            extraObjects: new[] { Stream("/Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /ASCIIHexDecode", new string('0', 128) + ">") });

        Assert.Equal(0, DecodedImagePixel(svg).Alpha);
    }

    [Fact]
    public void AJpegWithAStencilMask_IsBakedIntoAPng()
    {
        // Every sample 1: masked out under the default /Decode [0 1] (§8.9.6.4).
        string svg = RenderJpegImageSvg(SolidJpeg(SKColors.Red), "/ColorSpace /DeviceRGB /Mask 6 0 R",
            extraObjects: new[] { Stream("/Type /XObject /Subtype /Image /Width 8 /Height 8 /ImageMask true /BitsPerComponent 1 /Filter /ASCIIHexDecode", "FFFFFFFFFFFFFFFF>") });

        Assert.Equal(0, DecodedImagePixel(svg).Alpha);
    }

    [Fact]
    public void AJpegUnderATransferFunction_IsBakedIntoAPng()
    {
        string svg = RenderJpegImageSvg(SolidJpeg(SKColors.Red), "/ColorSpace /DeviceRGB",
            resourceExtras: "/ExtGState << /GS0 << /TR << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >> >> >>",
            contentPrefix: "/GS0 gs ");

        // Inverted: red becomes cyan.
        Assert.True(PixelAssert.Near(DecodedImagePixel(svg), SKColors.Cyan, 4), $"got {DecodedImagePixel(svg)}");
    }

    /// <summary>The control: a plain JPEG is the codestream itself, not a PNG.</summary>
    [Fact]
    public void APlainJpeg_PassesThroughAsItself()
    {
        string svg = RenderJpegImageSvg(SolidJpeg(SKColors.Red), "/ColorSpace /DeviceRGB");
        var image = XDocument.Parse(svg).Descendants().First(e => e.Name.LocalName == "image");

        Assert.StartsWith("data:image/jpeg;base64,", image.Attributes().First(a => a.Name.LocalName == "href").Value);
    }

    private static byte[] RawImagePdf(string colorSpaceEntry, string hexSamples,
                                      int width, int height, string[] extraObjects)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 200 0 0 200 100 400 cm /Im1 Do Q"),
            Stream($"/Type /XObject /Subtype /Image /Width {width} /Height {height}" +
                   $" /BitsPerComponent 8 {colorSpaceEntry} /Filter /ASCIIHexDecode", hexSamples),
        };
        objects.AddRange(extraObjects);
        return Build(objects.ToArray());
    }

    private static string RenderRawImageSvg(string colorSpaceEntry, string hexSamples,
                                            int width, int height, string[] extraObjects)
    {
        using var ms = new MemoryStream(
            RawImagePdf(colorSpaceEntry, hexSamples, width, height, extraObjects));
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static SKBitmap DecodeEmbeddedImage(string svg)
    {
        var image = XDocument.Parse(svg).Descendants().First(e => e.Name.LocalName == "image");
        string href = image.Attributes().First(a => a.Name.LocalName == "href").Value;
        const string prefix = "data:image/png;base64,";
        Assert.StartsWith(prefix, href);

        var bmp = SKBitmap.Decode(Convert.FromBase64String(href.Substring(prefix.Length)));
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>
    /// Collects the render diagnostics raised by <paramref name="body"/> — and <b>only</b> those.
    ///
    /// <para><c>RenderDiagnostics.OnEvent</c> is process-wide, so a handler subscribed here also
    /// receives every diagnostic raised by whatever else xUnit is running in parallel. A test that
    /// asserts a kind is <i>absent</i> is then really asserting "no other test emitted this while I
    /// ran", which fails intermittently and for reasons that have nothing to do with the code under
    /// test. Filtering to the calling thread fixes it precisely: <paramref name="body"/> renders
    /// synchronously here, so its diagnostics are raised on this thread and no others are.</para>
    ///
    /// <para>A positive assertion does not need this — a stray extra event cannot make
    /// <c>Assert.Contains</c> fail — which is why only the two negative-assertion sites in this
    /// suite were affected while the other twenty-odd sinks were not.</para>
    /// </summary>
    private static string CaptureDiagnostics(List<RenderDiagnosticKind> sink, Func<string> body)
    {
        int ownThread = Environment.CurrentManagedThreadId;
        void Handler(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            lock (sink) sink.Add(kind);
        }
        RenderDiagnostics.OnEvent += Handler;
        try { return body(); }
        finally { RenderDiagnostics.OnEvent -= Handler; }
    }

    private static byte[] SinglePagePdf(string contentStream) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << >> >>",
            Stream("", contentStream));

    private static string RenderSvg(string contentStream)
    {
        using var ms = new MemoryStream(SinglePagePdf(contentStream));
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static string RenderHelveticaSvg(string contentStream)
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("", contentStream),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    /// <summary>
    /// A page whose font embeds <paramref name="ttf"/> as a <c>/FontFile2</c>, which is what routes
    /// text through the glyph-outline emitter rather than the plain <c>&lt;text&gt;</c> path.
    /// </summary>
    private static string RenderEmbeddedFontSvg(string content, byte[] ttf)
    {
        var ms = new MemoryStream();
        var offsets = new int[8];
        void W(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Obj(int n) => offsets[n] = (int)ms.Position;

        W("%PDF-1.5\n");
        Obj(1); W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj(2); W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj(3); W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj(4); W($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj(5); W("5 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /Embedded" +
                  " /Encoding /WinAnsiEncoding /FontDescriptor 6 0 R >>\nendobj\n");
        Obj(6); W("6 0 obj\n<< /Type /FontDescriptor /FontName /Embedded /Flags 32" +
                  " /FontBBox [-500 -300 1300 1100] /ItalicAngle 0 /Ascent 1069 /Descent -293" +
                  " /CapHeight 714 /StemV 80 /FontFile2 7 0 R >>\nendobj\n");
        Obj(7); W($"7 0 obj\n<< /Length {ttf.Length} /Length1 {ttf.Length} >>\nstream\n");
                ms.Write(ttf, 0, ttf.Length);
                W("\nendstream\nendobj\n");

        int xref = (int)ms.Position;
        W("xref\n0 8\n0000000000 65535 f \n");
        for (int i = 1; i <= 7; i++) W($"{offsets[i]:D10} 00000 n \n");
        W("trailer\n<< /Size 8 /Root 1 0 R >>\n");
        W($"startxref\n{xref}\n%%EOF\n");

        using var input = new MemoryStream(ms.ToArray());
        return PdfSvgRenderer.RenderPageToSvg(input, 0);
    }

    private static List<XElement> StrokedPaths(string svg) =>
        XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "path")
            .Where(e => (e.Attribute("stroke")?.Value ?? "none") != "none")
            .ToList();

    private static List<XElement> StrokedTexts(string svg) =>
        XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "text")
            .Where(e => (e.Attribute("stroke")?.Value ?? "none") != "none")
            .ToList();

    private static float Attr(XElement e, string name) =>
        float.Parse(e.Attribute(name)!.Value, System.Globalization.CultureInfo.InvariantCulture);

    private static string Stream(string dictEntries, string data) =>
        $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    /// <summary>Assembles objects 1..n with a correct xref table. Bodies must be ASCII.</summary>
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
