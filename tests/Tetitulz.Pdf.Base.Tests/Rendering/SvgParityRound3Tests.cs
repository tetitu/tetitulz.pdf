// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Round 3 of the raster-vs-SVG parity sweep (GAPS.md §2, "Exhaustive raster-vs-SVG parity sweep",
/// item 9's list): <c>/Interpolate</c>, mask-stream decoding, <c>sh</c> alpha and <c>/BBox</c>,
/// tiling <c>/PaintType</c>, the §8.6.8 initial colour, Separation/DeviceN alternate resolution,
/// ExtGState reference resolution, Type 3 encoding / <c>/FontMatrix</c> / advance, vertical-writing
/// advance, untyped OCMDs, page-box normalisation with <c>/UserUnit</c>, and the two rounding
/// conventions.
///
/// <para><b>Every assertion is on the parsed SVG or on decoded pixels</b>, and where the claim is
/// "the two renderers agree" the test renders both and compares — the pattern
/// <c>SvgRasterParityTests.CalGrayFill_SvgMatchesTheRasterRender</c> established, and the reason
/// these defects survived a green suite: a substring check cannot tell a colour that is merely
/// present from the colour the other renderer produced.</para>
/// </summary>
public sealed class SvgParityRound3Tests
{
    // ====================================================================
    //  1. /Interpolate → image-rendering
    // ====================================================================

    /// <summary>
    /// §8.9.5.1 Table 89: <c>/Interpolate</c> asks for smoothing and defaults to <b>false</b>. The
    /// raster path reads it and picks Mitchell cubic against nearest-neighbour; the SVG export
    /// emitted no <c>image-rendering</c> at all, and every viewer's default is smoothing — so a
    /// 2×2 image blown up to 200×200 exported as a gradient where it rendered as four hard squares.
    /// </summary>
    [Fact]
    public void Interpolate_SelectsTheImageRenderingHint_AndDefaultsToPixelated()
    {
        string off = RenderImageSvg("/Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8",
                                    "FF0000 00FF00 0000FF FFFFFF>");
        string on  = RenderImageSvg("/Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8" +
                                    " /Interpolate true",
                                    "FF0000 00FF00 0000FF FFFFFF>");

        Assert.Equal("pixelated", SingleImage(off).Attribute("image-rendering")?.Value);
        Assert.Equal("auto",      SingleImage(on).Attribute("image-rendering")?.Value);
    }

    /// <summary>An inline image's <c>/I</c> is the same flag under its abbreviated name (Table 93).</summary>
    [Fact]
    public void Interpolate_InlineImageAbbreviation_IsHonoured()
    {
        string off = RenderSvg("q 200 0 0 200 100 400 cm " +
                               "BI /W 2 /H 2 /CS /RGB /BPC 8 /F /AHx ID FF000000FF000000FFFFFFFF> EI Q");
        string on  = RenderSvg("q 200 0 0 200 100 400 cm " +
                               "BI /W 2 /H 2 /CS /RGB /BPC 8 /I true /F /AHx ID FF000000FF000000FFFFFFFF> EI Q");

        Assert.Equal("pixelated", SingleImage(off).Attribute("image-rendering")?.Value);
        Assert.Equal("auto",      SingleImage(on).Attribute("image-rendering")?.Value);
    }

    // ====================================================================
    //  2. /SMask and stencil /Mask stream decoding
    // ====================================================================

    /// <summary>
    /// §8.9.5.2: an <c>/SMask</c>'s own <c>/Decode</c> maps its samples, and <c>[1 0]</c> — the way
    /// an inverted mask is written — swaps opaque for transparent. The SVG path never applied it:
    /// the base image's array is applied after rasterisation by <c>BuildImageDataUri</c>, a step the
    /// mask never went through, so the mask exported as its own negative.
    /// </summary>
    [Fact]
    public void ImageSMask_HonoursItsOwnDecodeArray()
    {
        using var plain    = DecodeFirstPng(RenderSMaskSvg(smaskExtras: ""));
        using var inverted = DecodeFirstPng(RenderSMaskSvg(smaskExtras: " /Decode [1 0]"));

        // Mask samples are 00 then FF, so alpha runs transparent → opaque…
        Assert.Equal(0,   plain.GetPixel(0, 0).Alpha);
        Assert.Equal(255, plain.GetPixel(1, 0).Alpha);
        // …and exactly the other way round under /Decode [1 0].
        Assert.Equal(255, inverted.GetPixel(0, 0).Alpha);
        Assert.Equal(0,   inverted.GetPixel(1, 0).Alpha);
    }

    /// <summary>
    /// A CCITT <c>/SMask</c> whose <c>/DecodeParms</c> omits <c>/Columns</c> must take it from the
    /// mask's own <c>/Width</c>, or the decoder runs at the 1728-column fax default and every row
    /// after the first is read out of the wrong place. The raster path routes soft masks through
    /// <c>ContentStreamInterpreter.DecodeImageStream</c> for exactly this; the SVG path called
    /// <c>GetDecodedData()</c>.
    /// </summary>
    /// <remarks>
    /// Two rows are essential. With one row the mis-sized decode still puts that row's samples at
    /// offset 0, so the defect is invisible — the fixture would never reach the feature.
    /// </remarks>
    [Fact]
    public void ImageSMask_CcittWithoutColumns_TakesColumnsFromWidth()
    {
        using var bmp = DecodeFirstPng(RenderCcittSMaskSvg());

        // Both rows encode 4 white (opaque) then 4 black (transparent).
        for (int y = 0; y < 2; y++)
        {
            Assert.Equal(255, bmp.GetPixel(0, y).Alpha);
            Assert.Equal(255, bmp.GetPixel(3, y).Alpha);
            Assert.Equal(0,   bmp.GetPixel(4, y).Alpha);
            Assert.Equal(0,   bmp.GetPixel(7, y).Alpha);
        }
    }

    // ====================================================================
    //  3. sh — fill alpha and the shading /BBox
    // ====================================================================

    /// <summary>
    /// §11.6.4.2: <c>sh</c> paints under the current alpha, exactly as a fill does — the raster
    /// path builds its paint with <c>Color = Black.WithAlpha(FillAlpha·255)</c>. The SVG writer
    /// emitted a bare <c>fill="url(#…)"</c> rect, so a shading under <c>/ca 0.3</c> exported opaque.
    /// </summary>
    [Fact]
    public void ShadingFill_PaintsAtTheCurrentFillAlpha()
    {
        string svg = RenderShadingSvg(bbox: null, gsEntries: "/ca 0.3");

        Assert.Equal("0.3", GradientRect(svg).Attribute("fill-opacity")?.Value);
    }

    /// <summary>The opaque case must still say so, or the assertion above cannot fail for the right reason.</summary>
    [Fact]
    public void ShadingFill_WithNoAlphaSet_IsFullyOpaque()
    {
        string svg = RenderShadingSvg(bbox: null, gsEntries: "/LW 3");

        Assert.Equal("1", GradientRect(svg).Attribute("fill-opacity")?.Value);
    }

    /// <summary>
    /// §8.7.4.3: a shading's <c>/BBox</c>, in the current user space, limits the region <c>sh</c>
    /// paints. The raster path clips to it; the SVG writer painted a 65536-unit rectangle straight
    /// over it, so any shading with a <c>/BBox</c> flooded the whole page.
    /// </summary>
    [Fact]
    public void ShadingFill_ClipsToTheShadingBBox()
    {
        string svg = RenderShadingSvg(bbox: "[10 20 110 220]", gsEntries: "/LW 3");
        var doc = XDocument.Parse(svg);

        var rect = doc.Descendants().Single(
            e => e.Name.LocalName == "rect" && (e.Attribute("fill")?.Value ?? "").StartsWith("url("));
        var clipGroup = rect.Ancestors().FirstOrDefault(
            a => a.Name.LocalName == "g" && a.Attribute("clip-path") != null);
        Assert.NotNull(clipGroup);

        // The clip is the box mapped through the CTM (identity here), so the four corners are the
        // /BBox corners — not merely "some clip group exists".
        string id = clipGroup!.Attribute("clip-path")!.Value.Trim('u', 'r', 'l', '(', '#', ')');
        var clipPath = doc.Descendants().Single(
            e => e.Name.LocalName == "clipPath" && e.Attribute("id")?.Value == id);
        Assert.Equal("M10,20 L110,20 L110,220 L10,220 Z",
                     clipPath.Descendants().First(e => e.Name.LocalName == "path").Attribute("d")?.Value);
    }

    /// <summary>A shading with no <c>/BBox</c> must not be clipped — the control for the test above.</summary>
    [Fact]
    public void ShadingFill_WithNoBBox_IsNotClipped()
    {
        string svg = RenderShadingSvg(bbox: null, gsEntries: "/LW 3");

        Assert.DoesNotContain(GradientRect(svg).Ancestors(),
            a => a.Name.LocalName == "g" && a.Attribute("clip-path") != null);
    }

    // ====================================================================
    //  4. Tiling /PaintType
    // ====================================================================

    /// <summary>
    /// §8.7.3.1: a COLOURED pattern (<c>/PaintType 1</c>) supplies its own colour and its content
    /// stream starts from the initial graphics state. The SVG child writer cloned the current state
    /// unconditionally, so <c>1 0 0 rg /Pattern cs /P1 scn</c> painted a cell that relies on the
    /// default black RED — while the raster path built a fresh render context for the cell.
    /// </summary>
    [Fact]
    public void TilingPattern_PaintType1_CellStartsFromTheInitialGraphicsState()
    {
        // The cell sets no colour of its own, so it must paint in the initial black.
        string svg = RenderTilingSvg(paintType: 1, cell: "0 0 4 4 re f");

        Assert.Equal("rgb(0,0,0)", PatternCellPath(svg).Attribute("fill")?.Value);
    }

    /// <summary>
    /// And the other half of the rule: an UNCOLOURED pattern (<c>/PaintType 2</c>) does take the
    /// current colour. Without this the test above would pass on a writer that always reset.
    /// </summary>
    [Fact]
    public void TilingPattern_PaintType2_CellTakesTheCurrentColour()
    {
        string svg = RenderTilingSvg(paintType: 2, cell: "0 0 4 4 re f");

        Assert.Equal("rgb(255,0,0)", PatternCellPath(svg).Attribute("fill")?.Value);
    }

    // ====================================================================
    //  5. cs/CS sets the §8.6.8 initial colour for EVERY space
    // ====================================================================

    /// <summary>
    /// Table 74: the initial colour in an <c>/Indexed</c> space is <b>index 0</b>, not black. The
    /// SVG writer gave every non-device space <c>rgb(0,0,0)</c>.
    /// </summary>
    [Fact]
    public void SetColorSpace_Indexed_SelectsPaletteEntryZero()
    {
        byte[] pdf = CsInitialColourPdf("[/Indexed /DeviceRGB 1 <FF000000FF00>]");

        AssertSvgFillMatchesRaster(pdf);
        Assert.Equal("rgb(255,0,0)", SvgFill(pdf));
    }

    /// <summary>
    /// Table 74 again: <c>/Separation</c> and <c>/DeviceN</c> start at tint <b>1.0</b> in every
    /// component — usually a saturated spot colour, and here deliberately blue so "black" cannot
    /// pass by accident.
    /// </summary>
    [Fact]
    public void SetColorSpace_Separation_StartsAtTintOne()
    {
        byte[] pdf = CsInitialColourPdf(
            "[/Separation /Spot /DeviceRGB << /FunctionType 2 /Domain [0 1]" +
            " /C0 [1 1 1] /C1 [0 0 1] /N 1 >>]");

        AssertSvgFillMatchesRaster(pdf);
        Assert.Equal("rgb(0,0,255)", SvgFill(pdf));
    }

    /// <summary>The device control: DeviceCMYK's initial colour is 0,0,0,1 and must stay black.</summary>
    [Fact]
    public void SetColorSpace_DeviceCmyk_StaysBlack()
    {
        byte[] pdf = CsInitialColourPdf("/DeviceCMYK");

        AssertSvgFillMatchesRaster(pdf);
        Assert.Equal("rgb(0,0,0)", SvgFill(pdf));
    }

    /// <summary>
    /// Table 74's Pattern row: "a pattern object that causes nothing to be painted". Selecting the
    /// space must clear any pattern a previous <c>scn</c> chose, and a paint issued before the next
    /// <c>scn</c> must produce no ink — the raster path's <c>PatternSelectedNothing</c>. The SVG
    /// writer kept the stale name and re-painted the old pattern.
    /// </summary>
    [Fact]
    public void SetColorSpace_Pattern_ClearsThePreviouslySelectedPattern()
    {
        string svg = RenderPatternReselectSvg(reselect: false);
        var doc = XDocument.Parse(svg);

        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "path");
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "pattern");
    }

    /// <summary>The control: with the <c>scn</c> still in force the same page does paint the pattern.</summary>
    [Fact]
    public void SetColorSpace_Pattern_WithScnStillInForce_Paints()
    {
        string svg = RenderPatternReselectSvg(reselect: true);
        var painted = Assert.Single(PaintedPaths(svg).Where(p => p.Ancestors()
            .All(a => a.Name.LocalName != "pattern")));

        Assert.StartsWith("url(#pat", painted.Attribute("fill")?.Value);
    }

    // ====================================================================
    //  6. Separation / DeviceN scn conversion
    // ====================================================================

    /// <summary>
    /// The alternate space is resolved through <see cref="PdfColor"/>'s single resolver, so an
    /// <c>[/ICCBased s]</c> alternate collapses to the device space its <c>/N</c> names. Taking the
    /// array head's name gave the family "ICCBased", which matched no case and fell to the
    /// 3-component default — an <c>/N 4</c> profile was read as an RGB triple with K discarded.
    /// </summary>
    [Fact]
    public void SeparationScn_IccBasedAlternate_ConvertsThroughItsDeviceSpace()
    {
        // Tint 1 → CMYK (0,1,1,0), which is red. Read as RGB (0,1,1) it would be cyan.
        byte[] pdf = SeparationScnPdf(
            alternate: "[/ICCBased 6 0 R]",
            function: "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 1 1 0] /N 1 >>",
            iccN: 4);

        AssertSvgFillMatchesRaster(pdf);
        Assert.Equal("rgb(255,0,0)", SvgFill(pdf));
    }

    /// <summary>
    /// The tint transform is evaluated <b>with</b> a reference resolver. Without one a Type 3
    /// stitching function cannot reach indirect <c>/Functions</c> entries — the normal way to write
    /// one — so the evaluation yielded nothing and the caller silently kept the previous colour.
    /// </summary>
    [Fact]
    public void SeparationScn_StitchingFunctionWithIndirectSubFunctions_IsEvaluated()
    {
        byte[] pdf = SeparationScnPdf(
            alternate: "/DeviceRGB",
            // /Functions is an indirect reference to an array of indirect references.
            function: "<< /FunctionType 3 /Domain [0 1] /Functions 7 0 R /Bounds []" +
                      " /Encode [0 1] >>",
            iccN: 0,
            extraObjects: new[]
            {
                "[8 0 R]",
                "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 1] /C1 [0 0 1] /N 1 >>",
            });

        AssertSvgFillMatchesRaster(pdf);
        // The page sets red before `scn`, which is what the un-resolved evaluation left behind.
        Assert.Equal("rgb(0,0,255)", SvgFill(pdf));
    }

    /// <summary>
    /// §8.6.6.4: the reserved colorant name <c>/All</c> applies the tint to every colorant at once —
    /// the registration-mark case — so the tint transform does not define the colour. The raster
    /// path paints full coverage of the four process inks; this branch did not exist in the SVG
    /// writer, which painted whatever the transform said.
    /// </summary>
    [Fact]
    public void SeparationAll_PaintsFullCoverage_NotTheTintTransformsColour()
    {
        // A transform that maps tint 1 to WHITE, so "used the transform" and "used /All" differ
        // as far apart as they can.
        byte[] pdf = SeparationScnPdf(
            alternate: "/DeviceRGB",
            function: "<< /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [1 1 1] /N 1 >>",
            iccN: 0,
            colorantName: "/All");

        AssertSvgFillMatchesRaster(pdf);
        Assert.Equal("rgb(0,0,0)", SvgFill(pdf));
    }

    // ====================================================================
    //  7. ExtGState entries are reference-resolved
    // ====================================================================

    /// <summary>
    /// <c>PdfDictionary</c>'s indexer returns the STORED value, so <c>/LW 6 0 R</c> — legal, and
    /// emitted in the wild — matched no <c>is PdfNumber</c> pattern and the entry was silently
    /// ignored while the raster path (which resolves throughout) applied it.
    /// </summary>
    [Fact]
    public void ExtGState_IndirectLineWidth_IsApplied()
    {
        string svg = RenderExtGStateSvg("/LW 6 0 R", "8", "0 0 m 100 100 l S");
        var stroked = Assert.Single(XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "path" && e.Attribute("stroke") != null
                        && e.Attribute("stroke")!.Value != "none"));

        Assert.Equal("8", stroked.Attribute("stroke-width")?.Value);
    }

    [Fact]
    public void ExtGState_IndirectFillAlpha_IsApplied()
    {
        string svg = RenderExtGStateSvg("/ca 6 0 R", "0.25", "0 0 100 100 re f");

        Assert.Equal("0.25", Assert.Single(PaintedPaths(svg)).Attribute("fill-opacity")?.Value);
    }

    // ====================================================================
    //  8. Type 3 fonts
    // ====================================================================

    /// <summary>
    /// §9.6.6.2: where <c>/Differences</c> does not cover a code, the base encoding does. The SVG
    /// writer's fallback was the literal <c>"." + code</c>, which matches no <c>/CharProcs</c>
    /// entry, so a Type 3 font with a <c>/BaseEncoding</c> and no <c>/Differences</c> exported
    /// notdef boxes where the raster path drew the glyph.
    /// </summary>
    [Fact]
    public void Type3_BaseEncodingFallback_ResolvesTheGlyphName()
    {
        string svg = RenderType3Svg(
            encoding: "<< /Type /Encoding /BaseEncoding /WinAnsiEncoding >>",
            charProcName: "A", widths: " /Widths [500]", fontMatrix: "[0.001 0 0 0.001 0 0]",
            text: "(A)", fontSize: 100);

        Assert.Contains(PaintedPaths(svg), p => p.Attribute("fill")?.Value == "rgb(0,255,0)");
    }

    /// <summary>
    /// A corrupt <c>/FontMatrix</c> — here a singular all-zero one — collapses the glyph transform
    /// and drops every glyph onto a point. The raster path rejects a non-finite or singular matrix
    /// and falls back to the default 1000-unit glyph space; the SVG writer used whatever was
    /// written.
    /// </summary>
    [Fact]
    public void Type3_SingularFontMatrix_FallsBackToTheDefault()
    {
        string good = RenderType3Svg(
            encoding: "<< /Type /Encoding /Differences [65 /ga] >>",
            charProcName: "ga", widths: " /Widths [500]", fontMatrix: "[0.001 0 0 0.001 0 0]",
            text: "(A)", fontSize: 100);
        string singular = RenderType3Svg(
            encoding: "<< /Type /Encoding /Differences [65 /ga] >>",
            charProcName: "ga", widths: " /Widths [500]", fontMatrix: "[0 0 0 0 0 0]",
            text: "(A)", fontSize: 100);

        Assert.Equal(GlyphPathData(good), GlyphPathData(singular));
    }

    /// <summary>
    /// With no <c>/Widths</c> and no <c>d0</c>/<c>d1</c> the advance is <b>zero</b>, as the raster
    /// path's <c>GetType3Width</c> gives. The SVG fallback was <c>FontSize · 0.6</c> — a value in
    /// POINTS fed into an expression that then multiplies by <c>fm[0] · FontSize</c>, so the
    /// advance came out proportional to FontSize squared.
    /// </summary>
    [Fact]
    public void Type3_NoWidths_AdvancesByZero_LikeTheRasterPath()
    {
        string svg = RenderType3Svg(
            encoding: "<< /Type /Encoding /Differences [65 /ga] >>",
            charProcName: "ga", widths: "", fontMatrix: "[0.001 0 0 0.001 0 0]",
            text: "(AA)", fontSize: 100);

        var glyphs = XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "path"
                        && (e.Attribute("fill")?.Value ?? "none") != "none")
            .Select(e => e.Attribute("d")?.Value)
            .ToList();

        Assert.Equal(2, glyphs.Count);
        Assert.Equal(glyphs[0], glyphs[1]);
    }

    // ====================================================================
    //  9. Vertical writing advances per GLYPH and reads /DW2
    // ====================================================================

    /// <summary>
    /// §9.7.4.3: each glyph advances by <c>w1y</c>, from <c>/W2</c> or <c>/DW2</c> (default
    /// <c>[880 −1000]</c>, i.e. one em). The SVG writer advanced by <c>bytes.Length · FontSize</c>,
    /// so a 2-byte-per-glyph Identity-V run — the normal CJK case — moved exactly twice as far as
    /// it should.
    /// </summary>
    [Fact]
    public void VerticalWriting_AdvancesPerGlyph_NotPerByte()
    {
        // Two glyphs (four bytes) at size 10 → 20 units of default /DW2 advance, not 40.
        float[] ys = VerticalRunBaselines(dw2: null);

        Assert.Equal(2, ys.Length);
        Assert.Equal(700f, ys[0], 3);
        Assert.Equal(680f, ys[1], 3);
    }

    /// <summary>
    /// And <c>/DW2</c> is read rather than assumed: halving <c>w1y</c> halves the advance. The old
    /// code ignored the entry entirely.
    /// </summary>
    [Fact]
    public void VerticalWriting_HonoursDw2()
    {
        float[] ys = VerticalRunBaselines(dw2: "[880 -500]");

        Assert.Equal(2, ys.Length);
        Assert.Equal(700f, ys[0], 3);
        Assert.Equal(690f, ys[1], 3);
    }

    // ====================================================================
    //  10. An untyped OCMD in BDC /OC
    // ====================================================================

    /// <summary>
    /// Producers routinely omit <c>/Type</c> from an OCMD. The SVG writer switched on it and, in
    /// its absence, looked for an <c>/OC</c> key a properties entry does not have — so the block was
    /// never suppressed, while the raster path treats a dictionary carrying <c>/OCGs</c> or
    /// <c>/VE</c> as an OCMD.
    /// </summary>
    [Fact]
    public void UntypedOcmd_InBdcOc_IsSuppressed()
    {
        string svg = RenderUntypedOcmdSvg(ocgOff: true);

        Assert.Empty(PaintedPaths(svg));
    }

    /// <summary>The same page with the layer ON must still paint, or the test above proves nothing.</summary>
    [Fact]
    public void UntypedOcmd_WithTheLayerOn_StillPaints()
    {
        string svg = RenderUntypedOcmdSvg(ocgOff: false);

        Assert.Equal("rgb(255,0,0)", Assert.Single(PaintedPaths(svg)).Attribute("fill")?.Value);
    }

    // ====================================================================
    //  11. Page box normalisation and /UserUnit
    // ====================================================================

    /// <summary>
    /// §7.9.5 permits a rectangle written from either corner pair. Subtracting blindly gave a
    /// NEGATIVE extent, which no viewer will render; the raster path normalises with
    /// <c>Math.Abs</c> / <c>Math.Min</c>.
    /// </summary>
    [Fact]
    public void ReversedCropBox_ProducesAPositiveExtent()
    {
        var root = XDocument.Parse(RenderPageSvg("/MediaBox [612 792 0 0]")).Root!;

        Assert.Equal("612", root.Attribute("width")?.Value);
        Assert.Equal("792", root.Attribute("height")?.Value);
        Assert.Equal("0 0 612 792", root.Attribute("viewBox")?.Value);
    }

    /// <summary>
    /// §14.11.1: <c>/UserUnit</c> scales the default user space, so one unit is <c>UserUnit</c>
    /// points. The raster path folds it into the device scale; the SVG export ignored it, so a
    /// <c>/UserUnit 2</c> page exported at half its intended size.
    /// </summary>
    [Fact]
    public void UserUnit_ScalesTheViewportAndTheContentGroup()
    {
        string svg = RenderPageSvg("/MediaBox [0 0 200 100] /UserUnit 2");
        var root = XDocument.Parse(svg).Root!;

        Assert.Equal("400", root.Attribute("width")?.Value);
        Assert.Equal("200", root.Attribute("height")?.Value);

        string transform = root.Elements().First(e => e.Name.LocalName == "g")
                               .Attribute("transform")!.Value;
        Assert.Contains("scale(2,-2)", transform);
    }

    /// <summary>The default is 1, and the emitted transform must be unchanged for it.</summary>
    [Fact]
    public void NoUserUnit_LeavesTheViewportInPoints()
    {
        string svg = RenderPageSvg("/MediaBox [0 0 200 100]");
        var root = XDocument.Parse(svg).Root!;

        Assert.Equal("200", root.Attribute("width")?.Value);
        Assert.Contains("scale(1,-1)",
            root.Elements().First(e => e.Name.LocalName == "g").Attribute("transform")!.Value);
    }

    // ====================================================================
    //  12. Rounding conventions
    // ====================================================================

    /// <summary>
    /// A 16-bit sample is down-converted with round-to-nearest —
    /// <c>(v·255 + 32767) / 65535</c>, <c>ContentStreamInterpreter.Scale16To8</c>. The SVG path kept
    /// the high byte, which truncates: a systematic −1 against the render on most samples.
    /// </summary>
    [Fact]
    public void SixteenBitImage_RoundsLikeTheRasterPath()
    {
        // 0x00FF → 1 when rounded, 0 when truncated. 0xFF00 → 254 rounded, 255 truncated.
        using var bmp = DecodeFirstPng(RenderImageSvg(
            "/Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 16", "00FFFF00>"));

        Assert.Equal(1,   bmp.GetPixel(0, 0).Red);
        Assert.Equal(254, bmp.GetPixel(1, 0).Red);
    }

    /// <summary>
    /// And the CMYK image path <b>rounds</b> — in both renderers, because they call the same
    /// converter (<c>PdfColor.CmykToRgb</c>) instead of each inlining <c>r = (1−c)(1−k)</c>.
    /// </summary>
    /// <remarks>
    /// Rounding, not truncation: every CIE space in <c>PdfColor</c> rounds via <c>Clamp255</c>, so
    /// truncating here would leave one file holding two conventions, and truncation biases every
    /// channel down half a level. GAPS.md §2b step 4.
    /// </remarks>
    [Fact]
    public void CmykImage_RoundsIdenticallyInBothRenderers()
    {
        // C=M=Y=K=0x14: (1−20/255)² · 255 = 216.57 — rounds to 217, truncates to 216.
        byte[] pdf = CmykImagePdf();

        using var svgBmp = DecodeFirstPng(SvgOf(pdf));
        using var raster = PixelAssert.Render(pdf);

        Assert.Equal(217, svgBmp.GetPixel(0, 0).Red);
        Assert.Equal(217, PixelAssert.At(raster, 200f, 500f).Red);

        // The property that actually matters is that the two agree — assert it directly, so a
        // future change to one renderer alone fails here even if it picks a defensible value.
        Assert.Equal(PixelAssert.At(raster, 200f, 500f).Red, svgBmp.GetPixel(0, 0).Red);
    }

    // ====================================================================
    //  Fixtures
    // ====================================================================

    private static string SvgOf(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static string RenderSvg(string contentStream)
        => SvgOf(Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << >> >>",
            Stream("", contentStream)));

    private static string RenderPageSvg(string pageExtras)
        => SvgOf(Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R {pageExtras} /Contents 4 0 R /Resources << >> >>",
            Stream("", "0 0 1 rg 10 10 20 20 re f")));

    private static string RenderImageSvg(string imageDictEntries, string hexData)
        => SvgOf(Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 200 0 0 200 100 400 cm /Im1 Do Q"),
            Stream($"/Type /XObject /Subtype /Image {imageDictEntries} /Filter /ASCIIHexDecode",
                   hexData)));

    private static byte[] CmykImagePdf()
        => Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 200 0 0 200 100 400 cm /Im1 Do Q"),
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceCMYK" +
                   " /BitsPerComponent 8 /Filter /ASCIIHexDecode", "14141414>"));

    /// <summary>A 2×1 RGB image with a 2×1 grey <c>/SMask</c> whose samples are 00 then FF.</summary>
    private static string RenderSMaskSvg(string smaskExtras)
        => SvgOf(Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 200 0 0 200 100 400 cm /Im1 Do Q"),
            Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceRGB" +
                   " /BitsPerComponent 8 /SMask 6 0 R /Filter /ASCIIHexDecode", "FF000000FF00>"),
            Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceGray" +
                   $" /BitsPerComponent 8{smaskExtras} /Filter /ASCIIHexDecode", "00FF>")));

    /// <summary>
    /// An 8×2 image whose <c>/SMask</c> is CCITT Group 4 with <c>/DecodeParms</c> deliberately
    /// missing <c>/Columns</c>. Both rows encode 4 white then 4 black.
    /// </summary>
    private static string RenderCcittSMaskSvg()
    {
        // Row 1: HORZ + white-4 ("1011") + black-4 ("011"). Row 2 repeats it: V0, V0.
        // Then EOFB (two EOLs). Codes are ITU-T T.4/T.6 as used by CcittFaxDecodeFilterTests.
        string bits = "001" + "1011" + "011" + "1" + "1"
                    + "000000000001" + "000000000001";
        string hex = BitsToHex(bits);

        return SvgOf(Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 200 0 0 200 100 400 cm /Im1 Do Q"),
            Stream("/Type /XObject /Subtype /Image /Width 8 /Height 2 /ColorSpace /DeviceGray" +
                   " /BitsPerComponent 8 /SMask 6 0 R /Filter /ASCIIHexDecode",
                   new string('8', 32) + ">"),                       // 16 opaque-grey samples
            Stream("/Type /XObject /Subtype /Image /Width 8 /Height 2 /ColorSpace /DeviceGray" +
                   " /BitsPerComponent 1" +
                   " /Filter [/ASCIIHexDecode /CCITTFaxDecode]" +
                   " /DecodeParms [null << /K -1 /Rows 2 /BlackIs1 false >>]",
                   hex + ">")));
    }

    private static string BitsToHex(string bits)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < bits.Length; i += 8)
        {
            int b = 0;
            for (int j = 0; j < 8; j++)
                if (i + j < bits.Length && bits[i + j] == '1') b |= 0x80 >> j;
            sb.Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    private static string RenderShadingSvg(string bbox, string gsEntries)
    {
        string bboxEntry = bbox != null ? $" /BBox {bbox}" : "";
        return SvgOf(Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /Shading << /Sh1 5 0 R >> /ExtGState << /G1 6 0 R >> >> >>",
            Stream("", "/G1 gs /Sh1 sh"),
            "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0]" +
            " /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >>" +
            bboxEntry + " >>",
            $"<< /Type /ExtGState {gsEntries} >>"));
    }

    private static string RenderTilingSvg(int paintType, string cell)
        => SvgOf(Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /Pattern << /P1 5 0 R >> >> >>",
            Stream("", "1 0 0 rg /Pattern cs /P1 scn 100 100 200 200 re f"),
            Stream($"/PatternType 1 /PaintType {paintType} /TilingType 1 /BBox [0 0 10 10]" +
                   " /XStep 10 /YStep 10", cell)));

    private static string RenderPatternReselectSvg(bool reselect)
    {
        string content = reselect
            ? "/Pattern cs /P1 scn 100 100 200 200 re f"
            // The second `cs` re-selects the Pattern space, which must clear /P1.
            : "/Pattern cs /P1 scn /Pattern cs 100 100 200 200 re f";

        return SvgOf(Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /Pattern << /P1 5 0 R >> >> >>",
            Stream("", content),
            Stream("/PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10]" +
                   " /XStep 10 /YStep 10", "0 0 1 rg 0 0 4 4 re f")));
    }

    private static byte[] CsInitialColourPdf(string colorSpace)
        => Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /ColorSpace << /CS0 5 0 R >> >> >>",
            // Green first: it is what a writer that ignores the initial colour would keep.
            Stream("", "0 1 0 rg /CS0 cs 0 0 100 100 re f"),
            colorSpace);

    private static byte[] SeparationScnPdf(string alternate, string function, int iccN,
                                           string colorantName = "/Spot",
                                           string[] extraObjects = null)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /ColorSpace << /CS0 5 0 R >> >> >>",
            // Red first: it is the colour a failed tint-transform evaluation leaves in place.
            Stream("", "1 0 0 rg /CS0 cs 1 scn 0 0 100 100 re f"),
            $"[/Separation {colorantName} {alternate} {function}]",
            iccN > 0
                ? Stream($"/N {iccN}", new string(' ', 4))
                : "<< /Dummy true >>",
        };
        if (extraObjects != null) objects.AddRange(extraObjects);
        return Build(objects.ToArray());
    }

    private static string RenderExtGStateSvg(string gsEntry, string indirectValue, string content)
        => SvgOf(Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /ExtGState << /G1 5 0 R >> >> >>",
            Stream("", "/G1 gs " + content),
            $"<< /Type /ExtGState {gsEntry} >>",
            indirectValue));

    private static string RenderType3Svg(string encoding, string charProcName, string widths,
                                         string fontMatrix, string text, int fontSize)
        => SvgOf(Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /Font << /T3 5 0 R >> >> >>",
            Stream("", $"BT /T3 {fontSize} Tf 100 700 Td {text} Tj ET"),
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000]" +
            $" /FontMatrix {fontMatrix} /CharProcs 6 0 R /Encoding {encoding}" +
            $" /FirstChar 65 /LastChar 65{widths} >>",
            $"<< /{charProcName} 7 0 R >>",
            Stream("", "0 1 0 rg 0 0 500 500 re f")));

    /// <summary>
    /// Renders a two-<c>Tj</c> Identity-V run and returns each <c>&lt;text&gt;</c> element's
    /// baseline Y (the <c>f</c> component of its transform, which is the text matrix translation).
    /// </summary>
    private static float[] VerticalRunBaselines(string dw2)
    {
        string dw2Entry = dw2 != null ? $" /DW2 {dw2}" : "";
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /Font << /F1 5 0 R >> >> >>",
            // Four bytes = two glyphs under Identity-V, then a second run so the advance shows up
            // as the difference between two emitted elements.
            Stream("", "BT /F1 10 Tf 100 700 Td <00410042> Tj <0043> Tj ET"),
            "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding /Identity-V" +
            " /DescendantFonts [6 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Test" +
            " /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>" +
            $" /FontDescriptor 7 0 R /DW 1000{dw2Entry} >>",
            "<< /Type /FontDescriptor /FontName /Test /Flags 4 /ItalicAngle 0" +
            " /Ascent 880 /Descent -120 /CapHeight 700 /StemV 80 /FontBBox [0 0 1000 1000] >>");

        return XDocument.Parse(SvgOf(pdf)).Descendants()
            .Where(e => e.Name.LocalName == "text")
            .Select(e => TransformComponent(e.Attribute("transform")!.Value, 5))
            .ToArray();
    }

    private static float TransformComponent(string transform, int index)
    {
        int open = transform.IndexOf('(');
        string inner = transform.Substring(open + 1, transform.IndexOf(')') - open - 1);
        return float.Parse(inner.Split(',')[index],
                           System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string RenderUntypedOcmdSvg(bool ocgOff)
    {
        string offEntry = ocgOff ? " /OFF [5 0 R]" : "";
        return SvgOf(Build(
            "<< /Type /Catalog /Pages 2 0 R" +
            $" /OCProperties << /OCGs [5 0 R] /D << /Order [5 0 R]{offEntry} >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /Properties << /P0 6 0 R >> >> >>",
            Stream("", "/OC /P0 BDC 1 0 0 rg 0 0 100 100 re f EMC"),
            "<< /Type /OCG /Name (Layer) >>",
            // No /Type — the shape producers actually emit, and the one the SVG writer ignored.
            "<< /OCGs [5 0 R] /P /AnyOn >>"));
    }

    // ====================================================================
    //  Assertions / accessors
    // ====================================================================

    private static void AssertSvgFillMatchesRaster(byte[] pdf)
    {
        using var bmp = PixelAssert.Render(pdf);
        var expected = PixelAssert.At(bmp, 50, 50);

        Assert.Equal($"rgb({expected.Red},{expected.Green},{expected.Blue})", SvgFill(pdf));
    }

    private static string SvgFill(byte[] pdf)
        => Assert.Single(PaintedPaths(SvgOf(pdf))).Attribute("fill")!.Value;

    private static List<XElement> PaintedPaths(string svg) =>
        XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "path")
            .Where(e => (e.Attribute("fill")?.Value ?? "none") != "none")
            // Hidden optional content is exported in a group a viewer does not display.
            .Where(e => !e.Ancestors().Any(a => (string)a.Attribute("display") == "none"))
            .ToList();

    private static XElement SingleImage(string svg) =>
        XDocument.Parse(svg).Descendants().Single(e => e.Name.LocalName == "image");

    private static XElement GradientRect(string svg) =>
        XDocument.Parse(svg).Descendants().Single(
            e => e.Name.LocalName == "rect" && (e.Attribute("fill")?.Value ?? "").StartsWith("url("));

    private static XElement PatternCellPath(string svg) =>
        XDocument.Parse(svg).Descendants().Single(e => e.Name.LocalName == "pattern")
            .Descendants().Single(e => e.Name.LocalName == "path");

    private static string GlyphPathData(string svg) =>
        Assert.Single(PaintedPaths(svg)).Attribute("d")!.Value;

    private static SkiaSharp.SKBitmap DecodeFirstPng(string svg)
    {
        var image = XDocument.Parse(svg).Descendants().First(e => e.Name.LocalName == "image");
        string href = image.Attributes().First(a => a.Name.LocalName == "href").Value;
        const string prefix = "data:image/png;base64,";
        Assert.StartsWith(prefix, href);

        var bmp = SkiaSharp.SKBitmap.Decode(Convert.FromBase64String(href.Substring(prefix.Length)));
        Assert.NotNull(bmp);
        return bmp!;
    }

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
