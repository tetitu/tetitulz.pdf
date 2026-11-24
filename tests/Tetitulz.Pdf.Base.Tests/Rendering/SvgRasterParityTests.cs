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
/// Parity between the SVG export and the raster path, for the divergences found by the 2026-08-06
/// rendering audit (GAPS.md §2, "full rendering audit", group 1).
///
/// <para><b>These assert on the parsed SVG, not on substrings of it.</b> The suite's existing SVG
/// tests are almost entirely <c>Assert.Contains("&lt;image", svg)</c>; the document was parsed as
/// XML exactly once in the whole tree. That is why every defect below — a fill silently lost, a
/// pattern placed by the wrong matrix, a hidden layer exported anyway — survived a green suite.</para>
/// </summary>
public sealed class SvgRasterParityTests
{
    // ====================================================================
    //  W / W* — the clip must not consume the path the painting operator needs
    // ====================================================================

    /// <summary>
    /// <c>re W f</c> must both fill the rectangle and establish the clip. The SVG writer used to
    /// apply the clip inside the <c>W</c> handler and clear the path with it, so <c>PaintPath</c>
    /// found nothing to paint and the fill vanished from the export.
    /// </summary>
    [Fact]
    public void ClipFollowedByFill_StillPaintsTheFill()
    {
        string svg = RenderSvg("0 0 1 rg 100 100 200 200 re W f");
        var painted = PaintedPaths(svg);

        Assert.Single(painted);
        Assert.Equal("rgb(0,0,255)", painted[0].Attribute("fill")?.Value);
    }

    /// <summary>
    /// §8.5.4: the clip takes effect only <i>after</i> the painting operator, so the fill itself
    /// must not sit inside the group the clip opens.
    /// </summary>
    [Fact]
    public void ClipFollowedByFill_FillIsOutsideTheNewClipGroup()
    {
        string svg = RenderSvg("0 0 1 rg 100 100 200 200 re W f");
        var painted = Assert.Single(PaintedPaths(svg));

        Assert.DoesNotContain(painted.Ancestors(),
            a => a.Name.LocalName == "g" && a.Attribute("clip-path") != null);
    }

    /// <summary>And the clip is still established for what follows.</summary>
    [Fact]
    public void ClipFollowedByFill_StillOpensTheClipGroup()
    {
        string svg = RenderSvg("0 0 1 rg 100 100 200 200 re W f 1 0 0 rg 0 0 50 50 re f");
        // One parse: XElement identity is per-tree, so re-parsing would compare across documents.
        var doc = XDocument.Parse(svg);

        var clipGroup = doc.Descendants().SingleOrDefault(
            e => e.Name.LocalName == "g" && e.Attribute("clip-path") != null);
        Assert.NotNull(clipGroup);

        // The second fill was issued after W f, so it is inside the clip group…
        var red = doc.Descendants().Single(
            e => e.Name.LocalName == "path" && e.Attribute("fill")?.Value == "rgb(255,0,0)");
        Assert.Contains(red.Ancestors(), a => a == clipGroup);

        // …and the first, which the W was written for, is not.
        var blue = doc.Descendants().Single(
            e => e.Name.LocalName == "path" && e.Attribute("fill")?.Value == "rgb(0,0,255)");
        Assert.DoesNotContain(blue.Ancestors(), a => a == clipGroup);
    }

    /// <summary><c>W n</c> — the case that already worked — must keep working.</summary>
    [Fact]
    public void ClipFollowedByEndPath_OpensTheClipGroup()
    {
        string svg = RenderSvg("100 100 200 200 re W n");
        var doc = XDocument.Parse(svg);

        Assert.Contains(doc.Descendants(),
            e => e.Name.LocalName == "clipPath");
        Assert.Contains(doc.Descendants(),
            e => e.Name.LocalName == "g" && e.Attribute("clip-path") != null);
    }

    // ====================================================================
    //  Tiling patterns — /Matrix, and steps that default to the BBox
    // ====================================================================

    /// <summary>
    /// A pattern's <c>/Matrix</c> maps pattern space to the page's default space (§8.7.3.1). The
    /// writer used to ignore it and emit the current CTM as <c>patternTransform</c> instead — which
    /// both dropped the matrix and applied the CTM a second time, since path coordinates already
    /// carry it.
    /// </summary>
    [Fact]
    public void TilingPattern_PatternTransformIsTheMatrix_NotTheCurrentCtm()
    {
        string svg = RenderTilingPatternSvg(
            patternExtras: "/Matrix [2 0 0 2 5 7]",
            // A CTM that is nothing like the matrix, so the two cannot be confused.
            contentPrefix: "q 3 0 0 3 40 60 cm ",
            contentSuffix: " Q");

        var pattern = SinglePattern(svg);
        Assert.Equal("matrix(2,0,0,2,5,7)", pattern.Attribute("patternTransform")?.Value);
    }

    [Fact]
    public void TilingPattern_NoMatrix_UsesIdentity_NotTheCurrentCtm()
    {
        string svg = RenderTilingPatternSvg(
            patternExtras: "",
            contentPrefix: "q 3 0 0 3 40 60 cm ",
            contentSuffix: " Q");

        var pattern = SinglePattern(svg);
        Assert.Equal("matrix(1,0,0,1,0,0)", pattern.Attribute("patternTransform")?.Value);
    }

    /// <summary>
    /// <c>/XStep</c> / <c>/YStep</c> default to the <c>/BBox</c> extent, not to the arbitrary 10
    /// units this writer used to assume — which detached the tiling period from the cell for every
    /// pattern that omitted them.
    /// </summary>
    [Fact]
    public void TilingPattern_AbsentSteps_DefaultToBBoxExtent()
    {
        string svg = RenderTilingPatternSvg(
            patternExtras: "",                      // no /XStep, no /YStep
            bbox: "[0 0 8 6]");

        var pattern = SinglePattern(svg);
        Assert.Equal("8", pattern.Attribute("width")?.Value);
        Assert.Equal("6", pattern.Attribute("height")?.Value);
    }

    /// <summary>The tile is anchored on the BBox corner, so a non-zero-origin cell is not shifted.</summary>
    [Fact]
    public void TilingPattern_TileIsAnchoredOnTheBBoxOrigin()
    {
        string svg = RenderTilingPatternSvg(patternExtras: "", bbox: "[3 4 11 12]");

        var pattern = SinglePattern(svg);
        Assert.Equal("3", pattern.Attribute("x")?.Value);
        Assert.Equal("4", pattern.Attribute("y")?.Value);
        Assert.Equal("8", pattern.Attribute("width")?.Value);
        Assert.Equal("8", pattern.Attribute("height")?.Value);
    }

    [Fact]
    public void TilingPattern_ExplicitSteps_AreUsedVerbatim()
    {
        string svg = RenderTilingPatternSvg(patternExtras: "/XStep 20 /YStep 30", bbox: "[0 0 8 8]");

        var pattern = SinglePattern(svg);
        Assert.Equal("20", pattern.Attribute("width")?.Value);
        Assert.Equal("30", pattern.Attribute("height")?.Value);
    }

    /// <summary>
    /// An SVG <c>&lt;pattern&gt;</c> tile establishes its own coordinate system at the tile corner
    /// and clips content to <c>[0,0,width,height]</c> in it. So anchoring the tile at the
    /// <c>/BBox</c> origin means the cell must be emitted with that origin subtracted — otherwise
    /// it is applied twice, displacing the tile and clipping most of the cell away.
    /// </summary>
    /// <remarks>
    /// Every other pattern fixture in the tree uses <c>/BBox [0 0 …]</c>, where the origin is zero
    /// and the defect is unobservable. That is exactly how it got in.
    /// </remarks>
    [Fact]
    public void TilingPattern_NonZeroBBoxOrigin_CellIsEmittedRelativeToTheTile()
    {
        // Cell content is a 6x6 mark at (12,12) inside a /BBox starting at (10,10).
        string svg = RenderTilingPatternSvg(
            patternExtras: "/XStep 20 /YStep 20", bbox: "[10 10 30 30]", cell: "1 0 0 rg 12 12 6 6 re f");

        var pattern = SinglePattern(svg);
        Assert.Equal("10", pattern.Attribute("x")?.Value);
        Assert.Equal("10", pattern.Attribute("y")?.Value);

        // Tile-local, the mark must sit at (2,2) — 12 minus the tile origin — so the tile's own
        // (10,10) translation puts it back at (12,12) in page space.
        var path = pattern.Descendants().First(e => e.Name.LocalName == "path");
        Assert.StartsWith("M2,2 ", path.Attribute("d")!.Value);
    }

    /// <summary>A BBox at the origin must be unaffected — the common case, and the control.</summary>
    [Fact]
    public void TilingPattern_ZeroBBoxOrigin_CellIsUnshifted()
    {
        string svg = RenderTilingPatternSvg(
            patternExtras: "/XStep 20 /YStep 20", bbox: "[0 0 20 20]", cell: "1 0 0 rg 12 12 6 6 re f");

        var path = SinglePattern(svg).Descendants().First(e => e.Name.LocalName == "path");
        Assert.StartsWith("M12,12 ", path.Attribute("d")!.Value);
    }

    // ====================================================================
    //  §8.6.8 — a pattern NAME outlives the /Pattern colour space
    // ====================================================================

    /// <summary>
    /// <c>g</c>/<c>rg</c>/<c>k</c> select a device colour space outright, so an earlier
    /// <c>/Pattern cs … scn</c> stops governing the paint. Neither renderer clears the pattern
    /// <b>name</b> — only the Pattern-space flag — so a gate that reads the name alone keeps
    /// painting the pattern that is no longer selected.
    /// </summary>
    /// <remarks>
    /// Both renderers ask for both, through <c>PatternPaint.SelectedName</c>
    /// (<c>PatternLeakAndRotatedImageTests</c>: a statement whose QR code would come out solid
    /// magenta); SVG gates asking for the name alone would make this page render the device colour
    /// and export the pattern.
    /// <para>The colour is pinned as well as matched, because "both renderers export the pattern"
    /// would satisfy equality just as well as "both paint the device colour".</para>
    /// </remarks>
    [Theory]
    [InlineData("1 0 0 rg",  255, 0, 0)]
    [InlineData("0 0 0 1 k",   0, 0, 0)]
    [InlineData("0.5 g",     128, 128, 128)]
    public void DeviceColourAfterAPatternWasSelected_SvgMatchesTheRasterRender(
        string solidColour, int r, int g, int b)
    {
        byte[] pdf = StalePatternNamePdf(solidColour);

        AssertSvgFillMatchesRaster(pdf);

        using var bmp = PixelAssert.Render(pdf);
        var c = PixelAssert.At(bmp, 50, 50);
        Assert.True(PixelAssert.Near(c, new SkiaSharp.SKColor((byte)r, (byte)g, (byte)b), 2),
            $"expected the device colour the operator selected, not the pattern's green; got {c}");
    }

    /// <summary>
    /// The control: with nothing between the <c>scn</c> and the fill, the same fixture must still
    /// paint the pattern in <b>both</b> renderers — otherwise the theory above would pass with
    /// pattern support deleted from either one.
    /// </summary>
    [Fact]
    public void APatternStillSelected_IsPaintedByBothRenderers()
    {
        byte[] pdf = StalePatternNamePdf("");

        using var bmp = PixelAssert.Render(pdf);
        var c = PixelAssert.At(bmp, 50, 50);
        Assert.True(PixelAssert.Near(c, new SkiaSharp.SKColor(0, 255, 0), 8),
            $"raster did not paint the tiling pattern's green, got {c}");

        using var ms = new MemoryStream(pdf);
        var doc = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(ms, 0));
        Assert.Contains(doc.Descendants(), e => e.Name.LocalName == "pattern");
    }

    /// <summary>
    /// A GREEN <c>/PaintType 1</c> tiling pattern selected by <c>scn</c>, then
    /// <paramref name="solidColour"/> (possibly empty), then a full-page fill.
    /// </summary>
    private static byte[] StalePatternNamePdf(string solidColour) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /Pattern << /P1 5 0 R >> >> >>",
            Stream("", $"/Pattern cs /P1 scn {solidColour} 0 0 100 100 re f"),
            Stream("/PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 100 100]" +
                   " /XStep 100 /YStep 100 /Resources << >>", "0 1 0 rg 0 0 100 100 re f"));

    /// <summary>
    /// A rasterized shading <c>&lt;pattern&gt;</c> id must be unique across the whole document.
    /// <c>_clipIdCounter</c> restarts at 0 in every child writer, so two writers would mint the same
    /// id and every reference would resolve to whichever pattern was defined last.
    /// </summary>
    [Fact]
    public void RasterizedShadingPatterns_InTwoWriters_GetDistinctIds()
    {
        string svg = RenderTwoFunctionShadingPatternsSvg();
        var ids = XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "pattern")
            .Select(e => e.Attribute("id")?.Value)
            .ToList();

        Assert.Equal(2, ids.Count);
        Assert.Equal(2, ids.Distinct().Count());
    }

    // ====================================================================
    //  Optional content — a hidden layer must not reach the export
    // ====================================================================

    /// <summary>
    /// An image XObject whose <c>/OC</c> names an OCG that the default configuration turns OFF is
    /// skipped by both renderers — exported anyway, a watermark the viewer hides would be in the SVG.
    /// </summary>
    [Fact]
    public void HiddenOcgImageXObject_IsNotExported()
    {
        string hidden  = RenderOcImageSvg(ocgOff: true);
        string visible = RenderOcImageSvg(ocgOff: false);

        // The control proves the fixture actually exports an image when the layer is on, so the
        // negative assertion below cannot pass for the wrong reason.
        Assert.Contains(XDocument.Parse(visible).Descendants(), e => e.Name.LocalName == "image");
        Assert.DoesNotContain(XDocument.Parse(hidden).Descendants(), e => e.Name.LocalName == "image");
    }

    // ====================================================================
    //  Shading patterns the writer cannot express as a gradient
    // ====================================================================

    /// <summary>
    /// A shading pattern whose shading is type 1 or 4-7 has no SVG gradient equivalent. The writer
    /// returned null for it and the caller then painted <c>_gs.FillColor</c> — whatever solid colour
    /// happened to be current. Here that would be red, which is precisely the wrong answer.
    /// </summary>
    [Fact]
    public void FunctionShadingPattern_DoesNotPaintTheStaleFillColour()
    {
        string svg = RenderFunctionShadingPatternSvg();
        var painted = Assert.Single(PaintedPaths(svg));

        string fill = painted.Attribute("fill")?.Value;
        Assert.NotEqual("rgb(255,0,0)", fill);          // the colour set before `scn`
        Assert.StartsWith("url(#shpat", fill);
    }

    /// <summary>…and the pattern it points at carries the rasterized shading.</summary>
    [Fact]
    public void FunctionShadingPattern_EmitsARasterizedPatternWithAnImage()
    {
        string svg = RenderFunctionShadingPatternSvg();
        var doc = XDocument.Parse(svg);

        var pattern = doc.Descendants().Single(
            e => e.Name.LocalName == "pattern" &&
                 (e.Attribute("id")?.Value ?? "").StartsWith("shpat", StringComparison.Ordinal));
        Assert.Contains(pattern.Descendants(), e => e.Name.LocalName == "image");
    }

    // ====================================================================
    //  ExtGState /D — the dash phase
    // ====================================================================

    /// <summary>
    /// <c>/D</c> is <c>[dashArray phase]</c>, the same pair the <c>d</c> operator takes. The writer
    /// read the array and dropped the phase, silently moving every ExtGState dash to phase 0.
    /// </summary>
    [Fact]
    public void ExtGStateDash_PreservesThePhase()
    {
        string svg = RenderSvgWithExtGState("/D [[6 3] 4]", "0 0 0 RG 2 w 10 10 m 200 10 l S");
        var stroked = Assert.Single(StrokedPaths(svg));

        Assert.Equal("6,3", stroked.Attribute("stroke-dasharray")?.Value);
        Assert.Equal("4", stroked.Attribute("stroke-dashoffset")?.Value);
    }

    [Fact]
    public void ExtGStateDash_EmptyArrayClearsTheDash()
    {
        string svg = RenderSvgWithExtGState("/D [[] 0]", "0 0 0 RG 2 w 10 10 m 200 10 l S");
        var stroked = Assert.Single(StrokedPaths(svg));

        Assert.Null(stroked.Attribute("stroke-dasharray"));
    }

    // ====================================================================
    //  q/Q depth — bounded, and it says so
    // ====================================================================

    /// <summary>
    /// The SVG writer had no graphics-state depth cap where the raster path refuses the push at
    /// <see cref="Tetitulz.Pdf.Core.Constants.PdfLimits.MaxGraphicsStateStackDepth"/>. Asserted
    /// through the diagnostic rather than by timing, so it cannot pass for being merely fast.
    /// </summary>
    [Fact]
    public void GraphicsStateStack_UnbalancedSaves_AreBoundedAndReported()
    {
        var kinds = new List<RenderDiagnosticKind>();
        void Sink(RenderDiagnosticKind k, string d, Exception e) => kinds.Add(k);

        RenderDiagnostics.OnEvent += Sink;
        try
        {
            int over = Tetitulz.Pdf.Core.Constants.PdfLimits.MaxGraphicsStateStackDepth + 50;
            RenderSvg(string.Concat(Enumerable.Repeat("q ", over)));
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        Assert.Contains(RenderDiagnosticKind.ContentStreamError, kinds);
    }

    /// <summary>A legitimate nesting depth is untouched by the cap.</summary>
    [Fact]
    public void GraphicsStateStack_NormalNesting_StillRestoresState()
    {
        // Red is set inside the q/Q pair; after Q the fill must be the outer blue again.
        string svg = RenderSvg("0 0 1 rg q 1 0 0 rg Q 10 10 20 20 re f");
        var painted = Assert.Single(PaintedPaths(svg));

        Assert.Equal("rgb(0,0,255)", painted.Attribute("fill")?.Value);
    }

    // ====================================================================
    //  Images the SVG path could not read at all
    // ====================================================================

    /// <summary>
    /// A 1-bit-per-component image — which is every CCITT and JBIG2 image after decoding — fell
    /// through every 8-bpc branch of <c>RasteriseImage</c> and was dropped with no diagnostic.
    /// </summary>
    [Fact]
    public void OneBitGrayImage_IsExported()
    {
        // 4x4, 1 bpc gray: rows 1010…, so the image is not uniform and cannot be confused with a
        // blank fallback. Each row is byte-aligned (§8.9.5.1).
        string svg = RenderImageXObjectSvg(
            "/Width 4 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 1", "A0A0A0A0>");

        var image = Assert.Single(XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "image"));
        Assert.StartsWith("data:image/png;base64,", HrefOf(image));
    }

    /// <summary>The unpack must produce the declared pattern, not merely some bitmap.</summary>
    [Fact]
    public void OneBitGrayImage_UnpacksSamplesInTheRightOrder()
    {
        // 0xA0 = 1010 0000 → for a 4-wide row: white, black, white, black.
        string svg = RenderImageXObjectSvg(
            "/Width 4 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 1", "A0A0A0A0>");

        using var bmp = DecodeEmbeddedImage(svg);
        Assert.Equal(4, bmp.Width);
        Assert.Equal(255, bmp.GetPixel(0, 0).Red);
        Assert.Equal(0,   bmp.GetPixel(1, 0).Red);
        Assert.Equal(255, bmp.GetPixel(2, 0).Red);
        Assert.Equal(0,   bmp.GetPixel(3, 0).Red);
    }

    /// <summary>
    /// An inline image mask (<c>BI /IM true</c>) went to the 8-bpc data-URI builder, which cannot
    /// read a packed 1-bit buffer, so it was dropped.
    /// </summary>
    [Fact]
    public void InlineImageMask_IsExported()
    {
        string svg = RenderSvg(
            "1 0 0 rg q 200 0 0 200 100 400 cm " +
            "BI /W 4 /H 4 /IM true /BPC 1 /F /AHx ID A0A0A0A0> EI Q");

        Assert.Contains(XDocument.Parse(svg).Descendants(), e => e.Name.LocalName == "image");
    }

    /// <summary>
    /// <c>/Decode [1 0]</c> inverts which bit paints (§8.9.6.2). The stencil builder hard-coded the
    /// default sense, so an inverted stencil exported as its own negative.
    /// </summary>
    [Fact]
    public void ImageMaskDecode_InvertsWhichBitPaints()
    {
        // 0xA0 → 1010 in the first row. Default sense: a 0 bit paints.
        using var normal = DecodeEmbeddedImage(RenderImageXObjectSvg(
            "/Width 4 /Height 4 /ImageMask true /BitsPerComponent 1", "A0A0A0A0>"));
        using var inverted = DecodeEmbeddedImage(RenderImageXObjectSvg(
            "/Width 4 /Height 4 /ImageMask true /BitsPerComponent 1 /Decode [1 0]", "A0A0A0A0>"));

        // Pixel 0 has bit 1: unpainted by default, painted when inverted. Pixel 1 is the reverse.
        Assert.Equal(0,   normal.GetPixel(0, 0).Alpha);
        Assert.Equal(255, normal.GetPixel(1, 0).Alpha);
        Assert.Equal(255, inverted.GetPixel(0, 0).Alpha);
        Assert.Equal(0,   inverted.GetPixel(1, 0).Alpha);
    }

    // ====================================================================
    //  CIE-based fill colours — the two renderers must agree
    // ====================================================================

    /// <summary>
    /// <c>/CalGray</c> is not <c>/DeviceGray</c>: its <c>/Gamma</c> changes the value. The SVG
    /// writer aliased the two, so a CalGray fill exported lighter than it rendered.
    /// </summary>
    /// <remarks>
    /// Fixed on the raster side first, which is precisely how a one-renderer-only divergence gets
    /// created — the reason this test compares the two renderers rather than checking SVG alone.
    /// </remarks>
    [Fact]
    public void CalGrayFill_SvgMatchesTheRasterRender()
    {
        byte[] pdf = CieFillPdf("[/CalGray << /WhitePoint [0.9505 1 1.089] /Gamma 2.2 >>]", "0.5 sc");

        AssertSvgFillMatchesRaster(pdf);
    }

    /// <summary>
    /// A <c>/Lab</c> space's own <c>/WhitePoint</c>, not the D50 default the 3-argument helper
    /// assumes.
    /// </summary>
    [Fact]
    public void LabFill_SvgMatchesTheRasterRender()
    {
        byte[] pdf = CieFillPdf(
            "[/Lab << /WhitePoint [0.9505 1 1.089] /Range [-128 127 -128 127] >>]",
            "53.24 80.09 67.20 sc");

        AssertSvgFillMatchesRaster(pdf);
    }

    /// <summary>The control: a plain DeviceGray fill already agreed, and must still.</summary>
    [Fact]
    public void DeviceGrayFill_SvgMatchesTheRasterRender()
    {
        AssertSvgFillMatchesRaster(CieFillPdf("/DeviceGray", "0.5 sc"));
    }

    /// <summary>
    /// §8.6.3 reserves the four device family names and <c>/Pattern</c>: a <c>/ColorSpace</c>
    /// resource entry cannot shadow one, so <c>/DeviceGray cs</c> means DeviceGray whatever the
    /// resource dictionary says.
    /// </summary>
    /// <remarks>
    /// Both renderers short-circuit the reserved names before the resource lookup. Looking the
    /// resource up first would make this page -- which binds /DeviceGray to a spot colour -- fill
    /// BLACK when rendered and the SPOT COLOUR AT FULL TINT when exported, because Table 74's
    /// initial colour for a Separation is 1.0 in every component.
    /// <para><b>The file is malformed, and that is not a reason to leave it.</b> Two renderers
    /// disagreeing is a property of this library, not of the input, and here the spec says plainly
    /// which of the two answers is right. Asserting only that they MATCH would also pass if both
    /// had been changed to honour the shadowing resource, so the colour is pinned as well.</para>
    /// </remarks>
    [Fact]
    public void AColorSpaceResourceCannotShadowAReservedDeviceName()
    {
        byte[] pdf = ShadowedDeviceGrayPdf();

        AssertSvgFillMatchesRaster(pdf);

        using var bmp = PixelAssert.Render(pdf);
        var c = PixelAssert.At(bmp, 50, 50);
        Assert.True(c.Red < 20 && c.Green < 20 && c.Blue < 20,
            $"/DeviceGray cs must be DeviceGray black, not the shadowing spot colour; got {c}");
    }

    /// <summary>
    /// Table 74's initial colour for a <c>/Separation</c> is <b>1.0 in every component</b> -- full
    /// tint, through the tint transform -- and not black. Both renderers now read that from one
    /// implementation (<c>PdfColor.InitialColorOperands</c>); it was a private method in each.
    /// </summary>
    [Fact]
    public void ASeparationCsWithNoScTakesFullTintInBothRenderers()
    {
        byte[] pdf = SeparationInitialColorPdf();

        AssertSvgFillMatchesRaster(pdf);

        // The transform maps t=1 to CMYK(0,1,1,0) = red. Black here would mean the initial colour
        // was all-zeros, which is the rule this asserts is NOT applied to Separation.
        using var bmp = PixelAssert.Render(pdf);
        var c = PixelAssert.At(bmp, 50, 50);
        Assert.True(c.Red > 200 && c.Green < 60 && c.Blue < 60,
            $"Separation initial colour should be full tint (red), got {c}");
    }

    /// <summary>A tint transform taking t to CMYK(0, t, t, 0) -- red at full tint.</summary>
    private const string RedTintFunction =
        "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 1 1 0] /N 1 >>";

    /// <summary>A page whose /ColorSpace resources bind the RESERVED name /DeviceGray to a spot colour.</summary>
    private static byte[] ShadowedDeviceGrayPdf() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /ColorSpace << /DeviceGray 5 0 R >> >> >>",
            Stream("", "/DeviceGray cs 0 0 100 100 re f"),
            "[/Separation /Spot /DeviceCMYK " + RedTintFunction + "]");

    /// <summary>The same spot colour under a NON-reserved name, selected with `cs` and no `sc`.</summary>
    private static byte[] SeparationInitialColorPdf() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /ColorSpace << /CS0 5 0 R >> >> >>",
            Stream("", "/CS0 cs 0 0 100 100 re f"),
            "[/Separation /Spot /DeviceCMYK " + RedTintFunction + "]");

    /// <summary>
    /// Renders the same page both ways and requires the SVG fill colour to equal the raster pixel.
    /// </summary>
    private static void AssertSvgFillMatchesRaster(byte[] pdf)
    {
        using var bmp = PixelAssert.Render(pdf);
        var expected = PixelAssert.At(bmp, 50, 50);

        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        string fill = Assert.Single(PaintedPaths(svg)).Attribute("fill")!.Value;

        // Both sides truncate to a byte, so an exact match is the right bar; a 1-unit tolerance
        // would hide the DeviceGray-aliasing defect this pins (0.5 gamma-2.2 is 55, not 128).
        Assert.Equal($"rgb({expected.Red},{expected.Green},{expected.Blue})", fill);
    }

    private static byte[] CieFillPdf(string colorSpace, string setColour) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /ColorSpace << /CS0 5 0 R >> >> >>",
            Stream("", $"/CS0 cs {setColour} 0 0 100 100 re f"),
            colorSpace);

    /// <summary>
    /// A <c>/Separation</c> shading's function outputs are <b>tints</b>, which must go through the
    /// space's tint transform (§8.7.4.3). Both renderers skipped it and were wrong <i>differently</i>
    /// — the raster path put the tint on the red channel, the SVG writer read it as a grey level —
    /// so it never surfaced as a divergence.
    /// </summary>
    [Fact]
    public void SeparationShading_SvgMatchesTheRasterRender()
    {
        byte[] pdf = SeparationShadingPdf();

        // Raster: the tint rises left-to-right, so the far right is near full tint → near-pure blue.
        using var bmp = PixelAssert.Render(pdf);
        var far = PixelAssert.At(bmp, 99, 50);
        Assert.True(far.Blue > 240 && far.Red < 20,
            $"raster did not apply the tint transform: {far}");

        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        // SVG: the last gradient stop IS t = 1, so it is exactly the transform's C1.
        // (Not compared against a raster pixel: the two sample the gradient at different
        // positions, so an exact equality there would be pinning the sample grid, not the colour.)
        var stops = XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "stop").ToList();
        Assert.NotEmpty(stops);
        Assert.Equal("rgb(0,0,255)", stops[stops.Count - 1].Attribute("stop-color")!.Value);

        // …and the first stop is the other end, so the gradient is not a flat fill.
        Assert.Equal("rgb(0,0,0)", stops[0].Attribute("stop-color")!.Value);
    }

    /// <summary>An axial /Separation shading whose tint transform maps t to DeviceRGB (0, 0, t).</summary>
    private static byte[] SeparationShadingPdf() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /Shading << /Sh1 5 0 R >> >> >>",
            Stream("", "/Sh1 sh"),
            "<< /ShadingType 2 /Coords [0 0 100 0] /Extend [true true]" +
            " /ColorSpace [ /Separation /Spot /DeviceRGB" +
            "   << /FunctionType 2 /Domain [0 1] /C0 [0 0 0] /C1 [0 0 1] /N 1 >> ]" +
            " /Function << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> >>");

    // ====================================================================
    //  Form XObjects
    // ====================================================================

    /// <summary>
    /// §8.10.2: a form XObject's <c>/BBox</c> is required and clips its content. The SVG path never
    /// emitted that clip, so content a form declared out of bounds still reached the export while
    /// the raster path dropped it.
    /// </summary>
    [Fact]
    public void FormXObject_ClipsToItsBBox()
    {
        string svg = RenderFormXObjectSvg(bbox: "[0 0 50 50]", matrix: null);
        var doc = XDocument.Parse(svg);

        Assert.Contains(doc.Descendants(),
            e => e.Name.LocalName == "g" && e.Attribute("clip-path") != null);
    }

    /// <summary>
    /// And the box is in FORM space, so the clip must be mapped through <c>/Matrix</c> — the reason
    /// the clip could not simply be switched on where it already existed for the PostScript path.
    /// </summary>
    [Fact]
    public void FormXObject_BBoxClipIsMappedThroughTheMatrix()
    {
        string plain     = RenderFormXObjectSvg(bbox: "[0 0 50 50]", matrix: null);
        string translated = RenderFormXObjectSvg(bbox: "[0 0 50 50]", matrix: "[1 0 0 1 200 300]");

        Assert.NotEqual(ClipPathData(plain), ClipPathData(translated));
    }

    // ====================================================================
    //  Soft masks
    // ====================================================================

    /// <summary>
    /// An SVG <c>&lt;mask&gt;</c> is luminance-valued by default, so a <c>/S /Alpha</c> mask was
    /// exported as a luminosity mask — a black opaque shape masked everything out instead of
    /// nothing.
    /// </summary>
    [Fact]
    public void AlphaSoftMask_SelectsTheAlphaChannel()
    {
        string svg = RenderSoftMaskSvg(maskSubtype: "/Alpha", extra: "");
        var mask = Assert.Single(XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "mask"));

        Assert.Contains("mask-type:alpha", mask.Attribute("style")?.Value ?? "");
    }

    [Fact]
    public void LuminositySoftMask_DoesNotSelectTheAlphaChannel()
    {
        string svg = RenderSoftMaskSvg(maskSubtype: "/Luminosity", extra: "");
        var mask = Assert.Single(XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "mask"));

        Assert.DoesNotContain("mask-type:alpha", mask.Attribute("style")?.Value ?? "");
    }

    /// <summary>
    /// <c>/BC</c> is the value the mask takes where its group paints nothing (§11.6.5.2). SVG's
    /// default there is black — fully masked out — so an unread <c>/BC</c> erased content the file
    /// asked to keep. Painted as the mask's first child.
    /// </summary>
    [Fact]
    public void LuminositySoftMask_BackdropColourIsPaintedBehindTheGroup()
    {
        string svg = RenderSoftMaskSvg(maskSubtype: "/Luminosity", extra: "/BC [1]");
        var mask = Assert.Single(XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "mask"));

        var first = mask.Elements().First();
        Assert.Equal("rect", first.Name.LocalName);
        Assert.Equal("rgb(255,255,255)", first.Attribute("fill")?.Value);
    }

    [Fact]
    public void SoftMask_NoBackdrop_EmitsNoBackdropRect()
    {
        string svg = RenderSoftMaskSvg(maskSubtype: "/Luminosity", extra: "");
        var mask = Assert.Single(XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "mask"));

        Assert.DoesNotContain(mask.Elements(), e => e.Name.LocalName == "rect");
    }

    /// <summary>A <c>/TR</c> that SVG cannot express is reported, not dropped in silence.</summary>
    [Fact]
    public void SoftMaskTransferFunction_IsReported()
    {
        var kinds = new List<RenderDiagnosticKind>();
        void Sink(RenderDiagnosticKind k, string d, Exception e) => kinds.Add(k);

        RenderDiagnostics.OnEvent += Sink;
        try
        {
            RenderSoftMaskSvg(maskSubtype: "/Luminosity",
                extra: "/TR << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 2 >>");
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        Assert.Contains(RenderDiagnosticKind.TransparencyGroupApproximated, kinds);
    }

    /// <summary>
    /// <c>/TR /Identity</c> is the default transfer function, so honouring it is not an
    /// approximation and must not be reported as one.
    /// </summary>
    /// <remarks>
    /// The sink filters to this thread. <c>RenderDiagnostics.OnEvent</c> is process-wide, so an
    /// unfiltered handler also receives diagnostics from whatever xUnit is running in parallel —
    /// turning this assertion into "no other test emitted <c>TransparencyGroupApproximated</c>
    /// while I ran", which fails intermittently for reasons unrelated to the code under test. The
    /// render below is synchronous, so its diagnostics are raised on this thread and no others are.
    /// The sibling test above is immune because a stray extra event cannot fail
    /// <c>Assert.Contains</c>; only the negative assertion is exposed.
    /// </remarks>
    [Fact]
    public void SoftMaskIdentityTransfer_IsNotReported()
    {
        var kinds = new List<RenderDiagnosticKind>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind k, string d, Exception e)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            lock (kinds) kinds.Add(k);
        }

        RenderDiagnostics.OnEvent += Sink;
        try { RenderSoftMaskSvg(maskSubtype: "/Luminosity", extra: "/TR /Identity"); }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        Assert.DoesNotContain(RenderDiagnosticKind.TransparencyGroupApproximated, kinds);
    }

    // ====================================================================
    //  Overprint (§11.7.4.2) — the two renderers do NOT agree, and say so
    // ====================================================================

    /// <summary>
    /// The SVG export approximates overprint with <c>mix-blend-mode:multiply</c>. That is not the
    /// raster result, and the divergence is now reported rather than being discoverable only by
    /// comparing pixels.
    /// </summary>
    /// <remarks>
    /// <b>The fixture is the worked example in the diagnostic.</b> 50% cyan painted over 50% cyan:
    /// §11.7.4.2's nonzero-overprint rule preserves only the colorants the source leaves at zero,
    /// so the source's cyan of 0.5 <i>replaces</i> the backdrop's and the result is 50% cyan —
    /// which is what the raster assertion below pins, at the pixel. Multiply instead darkens
    /// towards 75%. Overprint files are prepress files, so a caller comparing a raster proof with
    /// an SVG one must be told which of the two is the approximation.
    /// </remarks>
    [Fact]
    public void Overprint_SvgApproximatesWithMultiply_AndReportsTheDivergenceFromRaster()
    {
        var kinds = new List<RenderDiagnosticKind>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind k, string d, Exception e)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            lock (kinds) kinds.Add(k);
        }

        string svg;
        RenderDiagnostics.OnEvent += Sink;
        try { svg = RenderOverprintSvg(); }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        Assert.Contains("mix-blend-mode:multiply", svg);
        Assert.Contains(RenderDiagnosticKind.OverprintApproximated, kinds);
    }

    /// <summary>
    /// The raster half of the pair, and the measurement the diagnostic's wording rests on: the
    /// same file lands on 50% cyan, where <c>multiply</c> would have darkened it to 75%.
    /// </summary>
    /// <remarks>
    /// This asserts the <i>divergence</i>, not that simulation ran — and the two are different
    /// claims here on purpose. Where the source and the backdrop share a colorant, §11.7.4.2's
    /// result is the same as a plain paint (the source's non-zero component replaces the
    /// backdrop's either way), so no pixel of this fixture can distinguish simulated from
    /// unsimulated. What it can distinguish is multiply, which keeps darkening: red 64 rather than
    /// 128. That overprint simulation itself changes pixels is pinned where it can be —
    /// <c>OverprintSimulationTests</c>, magenta over cyan.
    /// </remarks>
    [Fact]
    public void Overprint_RasterKeepsTheSourceColorant_WhereMultiplyWouldDarkenIt()
    {
        using var bmp = RenderOverprintRaster();
        // PDF (50,50) — inside both rectangles. The page is 200pt tall and rendered at 72 dpi, and
        // PDF y is up, so that is device row 150.
        SkiaSharp.SKColor c = bmp.GetPixel(50, 150);

        Assert.InRange(c.Red, 118, 138);   // 50% cyan; multiply would give ~64
        Assert.InRange(c.Green, 245, 255);
        Assert.InRange(c.Blue, 245, 255);
    }

    /// <summary>
    /// The raster path composites this file, so it must <i>not</i> claim an approximation. Only
    /// the SVG export does, and a diagnostic that fired on both would tell a caller nothing about
    /// which one to trust.
    /// </summary>
    [Fact]
    public void Overprint_RasterDoesNotReportTheSvgApproximation()
    {
        var kinds = new List<RenderDiagnosticKind>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind k, string d, Exception e)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            lock (kinds) kinds.Add(k);
        }

        RenderDiagnostics.OnEvent += Sink;
        try { RenderOverprintRaster().Dispose(); }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        Assert.DoesNotContain(RenderDiagnosticKind.OverprintApproximated, kinds);
    }

    /// <summary>
    /// With simulation off both renderers name the <b>same</b> operation — one shared message table.
    /// With it on they part, by the owner's choice: the raster renderer simulates the
    /// shading or pattern on the page's separation plates and says nothing, while the SVG writer,
    /// which has no plates, still names it.
    /// </summary>
    [Theory]
    [InlineData("q /GS0 gs 0 0 0 1 k /Sh1 sh Q", "shading (sh)")]
    [InlineData("q /GS0 gs /Pattern cs /P1 scn 10 10 50 50 re f Q", "pattern fill")]
    public void Overprint_BothRenderersNameTheSameOperation_WhenOff_AndOnlySvgWhenSimulating(string content, string reason)
    {
        Assert.Contains(OverprintReasons(content, raster: true, simulate: false),  d => d.Contains(reason));
        Assert.Contains(OverprintReasons(content, raster: false, simulate: false), d => d.Contains(reason));
        Assert.Empty(OverprintReasons(content, raster: true));
        Assert.Contains(OverprintReasons(content, raster: false), d => d.Contains(reason));
    }

    /// <summary>
    /// The text row left the theory above the way the image row did: by being <b>implemented</b>,
    /// and on both sides at once. The raster path composites every painting text mode per colorant
    /// through glyph capture; the SVG writer applies the same <c>multiply</c> approximation to
    /// <c>&lt;text&gt;</c> and glyph outlines that it applies to paths. So neither reports the
    /// operation as unsupported, and the SVG side says what it did instead — exactly as for a path.
    /// </summary>
    /// <remarks>
    /// Recorded as its own test rather than a vanished row: a deleted row is indistinguishable from
    /// a forgotten one. The fixture is a <i>stroking</i> mode, which is the half that was never
    /// composited anywhere before.
    /// </remarks>
    [Fact]
    public void OverprintOnStrokedText_IsSimulatedByRaster_AndApproximatedBySvg()
    {
        const string content = "q /GS0 gs 0 0 0 1 K BT /F1 24 Tf 1 Tr 10 10 Td (Hi) Tj ET Q";

        Assert.Empty(OverprintReasons(content, raster: true));
        Assert.Empty(OverprintReasons(content, raster: false));

        var kinds = new List<RenderDiagnosticKind>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind k, string d, Exception e)
        {
            if (Environment.CurrentManagedThreadId == ownThread) kinds.Add(k);
        }
        RenderDiagnostics.OnEvent += Sink;
        try
        {
            using var ms = new MemoryStream(OverprintReportingTests.BuildPdf(content));
            PdfSvgRenderer.RenderPageToSvg(ms, 0, new PdfRenderOptions { SimulateOverprint = true });
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        Assert.Contains(RenderDiagnosticKind.OverprintApproximated, kinds);
    }

    /// <summary>
    /// The image row is not in the theory above: §8.6.7 keeps non-zero overprint mode off images, so
    /// an overprinting image in DeviceCMYK, grey or RGB writes every process colorant and its ordinary
    /// draw is exact. The renderers agree through one rule
    /// (<c>OverprintCompositor.ImageLeavesColorantsAlone</c>, ImageOverprintTests): neither says
    /// anything about this DeviceRGB image. The RGB fill beside it is the potency control — both
    /// do report that, so the silence about the image is not a deaf sink.
    /// </summary>
    /// <remarks>
    /// With simulation off: with it on the raster renderer simulates the whole
    /// page on its plates and reports nothing at all, so the potency control is the opt-in report
    /// both renderers give the fill.
    /// </remarks>
    [Fact]
    public void OverprintOnAnRgbImage_IsReportedByNeitherRenderer_TheyAgreeAgain()
    {
        const string content = "q /GS0 gs 1 0 0 rg 10 10 10 10 re f 100 0 0 100 0 0 cm /Im1 Do Q";

        foreach (bool raster in new[] { true, false })
        {
            var reasons = OverprintReasons(content, raster, simulate: false);
            Assert.Contains(reasons, d => d.Contains("SimulateOverprint is off"));
            Assert.DoesNotContain(reasons, d => d.Contains("image", StringComparison.Ordinal));
        }
    }

    // ====================================================================
    //  Graphics-state parameters read, stored and not honoured (§8.4.5 Table 58)
    // ====================================================================

    /// <summary>
    /// Every parameter both renderers parse and drop must be reported by <b>both</b>, under the
    /// same kind. The SVG writer stored <c>/AIS</c>, <c>/TK</c>, <c>/RI</c> and <c>/SA</c> and
    /// reported none of them, and did not even look at <c>/HT</c> or <c>/BG</c> — so a prepress
    /// caller who exported to SVG was told nothing about the same file the raster path warned about.
    /// </summary>
    /// <remarks>
    /// <b>The assertion is on the SET of kinds, never on a count.</b> The two scopes differ on
    /// purpose: the raster one-shots are per content stream (a form XObject re-reports), the SVG
    /// ones per page. <c>Assert.Single</c> here would be a flake waiting for a fixture with a form
    /// in it.
    /// </remarks>
    [Theory]
    [InlineData("/HT << /HalftoneType 1 /Frequency 60 /Angle 45 /SpotFunction /Round >>",
                RenderDiagnosticKind.RenderParameterIgnored)]
    [InlineData("/HTO [0 0]",    RenderDiagnosticKind.RenderParameterIgnored)]
    [InlineData("/SA true",      RenderDiagnosticKind.RenderParameterIgnored)]
    // No /RI row: every intent is honoured by both renderers now — absolute colorimetric since its
    // media-white scaling (IccAbsoluteColorimetricTests) — so a row here would assert a diagnostic
    // on conformant input.
    // No /AIS row: the raster renderer honours it, and in SVG it changes no pixel (it matters
    // only in a knockout group, which SVG exports as non-knockout and reports there).
    [InlineData("/BG << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>",
                RenderDiagnosticKind.BlackGenerationUndercolorIgnored)]
    [InlineData("/UCR2 << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>",
                RenderDiagnosticKind.BlackGenerationUndercolorIgnored)]
    public void IgnoredGraphicsStateParameter_BothRenderersReportTheSameKind(
        string extGsEntries, RenderDiagnosticKind expected)
    {
        Assert.Contains(expected, GsParameterKinds(extGsEntries, raster: true));
        Assert.Contains(expected, GsParameterKinds(extGsEntries, raster: false));
    }

    /// <summary>
    /// <c>/TK</c> is not in the table above, because it is <b>implemented</b>: the raster path opens
    /// a knockout layer
    /// (<c>ContentStreamInterpreter.BeginTextKnockout</c>), and the SVG writer — which has no
    /// knockout model — emulates the common case exactly, glyphs painted opaque inside a group at
    /// their one alpha (<c>SvgContentWriter.BeginTextKnockout</c>); the case below keeps a deviation
    /// the SVG writer still reports.
    ///
    /// <para>Note the fixture has to <b>draw text at a translucent alpha</b>. The report fires from
    /// <c>BT</c> on the state the text object opens with, not from the <c>gs</c> that sets the key —
    /// <c>/TK</c>'s initial value is <c>true</c>, so gating on the token would announce the deviation
    /// only for files redundantly restating the default and stay silent on every file that simply
    /// relies on it.</para>
    /// </summary>
    [Fact]
    public void TextKnockout_IsHonouredByBothRenderers()
    {
        byte[] pdf = TranslucentTextPdf();

        Assert.DoesNotContain(RenderDiagnosticKind.RenderParameterIgnored,
                              RenderAndCollectKinds(pdf, raster: true));
        Assert.DoesNotContain(RenderDiagnosticKind.RenderParameterIgnored,
                              RenderAndCollectKinds(pdf, raster: false));
    }

    /// <summary>
    /// Under a blend mode the SVG writer's one-group emulation does not hold (the group would have
    /// to blend once where each glyph blends), so it reports; the raster path still honours it.
    /// </summary>
    [Fact]
    public void TextKnockout_UnderABlendMode_IsReportedBySvgOnly()
    {
        byte[] pdf = TranslucentTextPdf("/BM /Multiply");

        Assert.DoesNotContain(RenderDiagnosticKind.RenderParameterIgnored,
                              RenderAndCollectKinds(pdf, raster: true));
        Assert.Contains(RenderDiagnosticKind.RenderParameterIgnored,
                        RenderAndCollectKinds(pdf, raster: false));
    }

    /// <summary>
    /// A <c>/Group</c> with no <c>/S</c> is a transparency group in <b>both</b> renderers, so a
    /// <c>/CS</c> the standard does not admit as a blending space (§11.3.4: Lab) on it is
    /// approximated — and reported — by both. (A CMYK group would show it in the SVG export alone,
    /// since the raster path composites that one in its space — <c>GroupBlendSpaceTests</c>.)
    /// </summary>
    /// <remarks>
    /// Table 96 makes <c>/S</c> required and defines exactly one value for it, so a group
    /// dictionary that omits it can only have meant <c>/Transparency</c>; producers do omit it.
    /// The SVG writer required it, which silently demoted such a form to a plain XObject there
    /// while the raster path composited it as a group — <c>/CS</c> unreported, <c>/I</c> and
    /// <c>/K</c> unread.
    /// </remarks>
    [Fact]
    public void TransparencyGroupWithNoSubtype_IsAGroupInBothRenderers()
    {
        byte[] pdf = GroupCsPdfWithoutSubtype("[/Lab << /WhitePoint [0.9642 1.0 0.8251] >>]");
        Assert.Contains(RenderDiagnosticKind.TransparencyGroupApproximated, RenderAndCollectKinds(pdf, raster: true));
        Assert.Contains(RenderDiagnosticKind.TransparencyGroupApproximated, RenderAndCollectKinds(pdf, raster: false));
    }

    /// <summary>
    /// The counterpart, and the one that fails if presence is mistaken for deviation:
    /// <c>/HT /Default</c>, <c>/BG2 /Default</c>, <c>/UCR2 /Default</c> and <c>/SA false</c> name
    /// the behaviour both renderers already have, so neither may report them.
    /// </summary>
    /// <remarks>
    /// <para>Every one of these shipped as a false report. The presence test that produced them
    /// sat four lines above a <c>/TR2</c> handler that gets <c>/Default</c> right, and two entries
    /// below <c>/AIS</c> and <c>/TK</c>, which get it right by testing for <c>true</c> — so the
    /// correct shape was in the same screenful in three places.</para>
    /// <para>Why it matters more than a stray log line: a producer that resets graphics state
    /// between blocks writes exactly these values, so the diagnostic fired on files that deviate
    /// in no way at all. <see cref="RenderDiagnosticKind.RenderParameterIgnored"/> exists so a
    /// prepress caller can tell "the file asked for something we did not do" from "the file asked
    /// for nothing"; one that fires on conformant input teaches them to ignore the channel, which
    /// costs more than never having reported at all.</para>
    /// </remarks>
    [Theory]
    [InlineData("/HT /Default")]
    [InlineData("/BG2 /Default")]
    [InlineData("/UCR2 /Default")]
    [InlineData("/SA false")]
    public void NoOpGraphicsStateValue_IsReportedByNeitherRenderer(string extGsEntries)
    {
        Assert.DoesNotContain(RenderDiagnosticKind.RenderParameterIgnored,
                              GsParameterKinds(extGsEntries, raster: true));
        Assert.DoesNotContain(RenderDiagnosticKind.BlackGenerationUndercolorIgnored,
                              GsParameterKinds(extGsEntries, raster: true));
        Assert.DoesNotContain(RenderDiagnosticKind.RenderParameterIgnored,
                              GsParameterKinds(extGsEntries, raster: false));
        Assert.DoesNotContain(RenderDiagnosticKind.BlackGenerationUndercolorIgnored,
                              GsParameterKinds(extGsEntries, raster: false));
    }

    /// <summary>
    /// <c>/HT</c> written as an indirect reference — which is the normal way, since a halftone is
    /// a dictionary or a stream — must still be seen. The presence test this replaced used the
    /// dictionary indexer, which does not follow a reference; it happened to work only because a
    /// reference object is itself non-null, and the moment the check had to look at the VALUE it
    /// would have read every indirect halftone as absent.
    /// </summary>
    [Fact]
    public void IndirectHalftone_IsStillReportedByBothRenderers()
    {
        Assert.Contains(RenderDiagnosticKind.RenderParameterIgnored,
                        RenderAndCollectKinds(IndirectHalftonePdf(), raster: true));
        Assert.Contains(RenderDiagnosticKind.RenderParameterIgnored,
                        RenderAndCollectKinds(IndirectHalftonePdf(), raster: false));
    }

    /// <summary>
    /// PDF 2.0's <c>/UseBlackPtComp</c> (ISO 32000-2 §8.6.5.9) is honoured by <b>both</b> renderers,
    /// so neither reports any of its values as ignored; that they agree on what each value does is
    /// <c>IccSourceBlackPointTests</c>' pair of strip tests. Potency: <c>/SA true</c> beside it is reported.
    /// </summary>
    [Theory]
    [InlineData("/UseBlackPtComp /ON")]
    [InlineData("/UseBlackPtComp /OFF")]
    [InlineData("/UseBlackPtComp /Default")]
    public void UseBlackPtComp_IsReportedByNeitherRenderer(string entry)
    {
        foreach (bool raster in new[] { true, false })
        {
            Assert.DoesNotContain(RenderDiagnosticKind.RenderParameterIgnored, GsParameterKinds(entry, raster));
            Assert.Contains(RenderDiagnosticKind.RenderParameterIgnored, GsParameterKinds(entry + " /SA true", raster));
        }
    }

    /// <summary>
    /// The <see cref="GsPdf"/> shape with the halftone hoisted into object 5 and referenced.
    /// <see cref="Build"/> numbers its objects sequentially from 1, so the halftone is <c>5 0 R</c>.
    /// </summary>
    private static byte[] IndirectHalftonePdf() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
        " /Resources << /ExtGState << /G1 << /Type /ExtGState /HT 5 0 R >> >> >> >>",
        Stream("", "q /G1 gs 0 0 100 100 re f Q"),
        "<< /HalftoneType 1 /Frequency 60 /Angle 45 /SpotFunction /Round >>");

    /// <summary>
    /// The <c>ri</c> operator sets the same parameter as ExtGState <c>/RI</c>, and both renderers
    /// must treat both spellings alike.
    /// </summary>
    /// <remarks>
    /// <para>A dispatch swallowing the operator with a bare <c>break</c> would store the ExtGState
    /// form while the operator form vanished — which is why the pair is asserted together.</para>
    /// <para>Every intent is honoured, so neither renderer reports any; that both <b>read</b> the
    /// operator — absolute colorimetric visibly differs from relative in each — is
    /// <c>IccAbsoluteColorimetricTests</c>' pair of renderer tests.</para>
    /// </remarks>
    [Theory]
    [InlineData("/Saturation ri 0 0 100 100 re f")]
    [InlineData("/AbsoluteColorimetric ri 0 0 100 100 re f")]
    public void RenderingIntentOperator_IsReportedByNeitherRenderer(string content)
    {
        Assert.DoesNotContain(RenderDiagnosticKind.RenderParameterIgnored, ContentKinds(content, raster: true));
        Assert.DoesNotContain(RenderDiagnosticKind.RenderParameterIgnored, ContentKinds(content, raster: false));
    }

    /// <summary>
    /// Polarity, asserted on both renderers at once. §9.3.8 makes <c>/TK true</c> the knockout
    /// case; both renderers paint glyph by glyph with SrcOver, which is <c>/TK false</c> behaviour.
    /// Reporting <c>false</c> would name the one value they get right — and would do it under a
    /// method name that still read correctly, the failure mode no build guard can catch.
    /// </summary>
    [Theory]
    [InlineData("/TK false")]
    [InlineData("/AIS false")]
    public void IgnoredGraphicsStateParameter_OnTheHonouredValue_NeitherRendererReports(
        string extGsEntries)
    {
        Assert.DoesNotContain(RenderDiagnosticKind.RenderParameterIgnored,
                              GsParameterKinds(extGsEntries, raster: true));
        Assert.DoesNotContain(RenderDiagnosticKind.RenderParameterIgnored,
                              GsParameterKinds(extGsEntries, raster: false));
    }

    /// <summary>
    /// <c>/FL</c> is deliberately outside the set: flatness is a curve-approximation hint the
    /// geometry subsumes, not a colour decision. It is the one ExtGState key the SVG writer stores
    /// and must keep quiet about, so it is pinned rather than left to the next reader's judgement.
    /// </summary>
    [Fact]
    public void Flatness_IsNotReportedByEitherRenderer()
    {
        Assert.DoesNotContain(RenderDiagnosticKind.RenderParameterIgnored,
                              GsParameterKinds("/FL 10", raster: true));
        Assert.DoesNotContain(RenderDiagnosticKind.RenderParameterIgnored,
                              GsParameterKinds("/FL 10", raster: false));
    }

    // ====================================================================
    //  Transparency-group /CS (§11.6.6 Table 147)
    // ====================================================================

    /// <summary>
    /// A group declaring a space §11.3.4 does not admit as a blending space is an approximation
    /// in both renderers — it is composited in the parent's space — and both say so.
    /// </summary>
    /// <remarks>
    /// <b><c>/Lab</c> is the case for a reason.</b> It is a three-component space, so a predicate
    /// written as <c>Components == 3</c> — the obvious implementation — passes it straight through
    /// and both renderers then treat L*a*b* values as sRGB. The decision has to be on the family.
    /// <c>/ICCBased</c> is the mirror image: its <c>/N</c> is the only statement of what it stands
    /// for, so the arity <i>is</i> the question there.
    /// </remarks>
    [Theory]
    [InlineData("[/Lab << /WhitePoint [0.9642 1.0 0.8251] >>]")]
    [InlineData("[/Indexed /DeviceRGB 0 <000000>]")]
    public void TransparencyGroupCs_NotABlendingSpace_IsReportedByBothRenderers(string cs)
    {
        Assert.Contains(RenderDiagnosticKind.TransparencyGroupApproximated,
                        GroupCsKinds(cs, raster: true));
        Assert.Contains(RenderDiagnosticKind.TransparencyGroupApproximated,
                        GroupCsKinds(cs, raster: false));
    }

    /// <summary>
    /// A grey or CMYK group is composited in its space by the raster renderer
    /// (<c>GroupBlenders</c>; the pixels are <c>GroupBlendSpaceTests</c>'), so it has nothing to
    /// report there; the SVG export composites in sRGB and reports it. The one case the two
    /// renderers answer differently by design, and the diagnostic is what tells a caller which.
    /// </summary>
    [Theory]
    [InlineData("/DeviceCMYK")]
    [InlineData("/DeviceGray")]
    [InlineData("[/CalGray << /WhitePoint [0.9642 1.0 0.8251] >>]")]
    public void TransparencyGroupCs_GreyOrCmyk_IsHonouredByTheRaster_AndReportedBySvg(string cs)
    {
        Assert.DoesNotContain(RenderDiagnosticKind.TransparencyGroupApproximated,
                              GroupCsKinds(cs, raster: true));
        Assert.Contains(RenderDiagnosticKind.TransparencyGroupApproximated,
                        GroupCsKinds(cs, raster: false));
    }

    /// <summary>
    /// The negative half, and the one that decides whether the diagnostic is usable at all. An
    /// absent <c>/CS</c> means "inherit" (§11.6.6 makes it optional), and both renderers already
    /// honour the three RGB families — so all four of these must stay silent, or the stream is
    /// noise on the majority of real transparency groups.
    /// </summary>
    [Theory]
    [InlineData("/DeviceRGB")]
    [InlineData("[/CalRGB << /WhitePoint [0.9642 1.0 0.8251] >>]")]
    [InlineData(null)]
    public void TransparencyGroupCs_RgbOrAbsent_IsNotReportedByEitherRenderer(string? cs)
    {
        Assert.DoesNotContain(RenderDiagnosticKind.TransparencyGroupApproximated,
                              GroupCsKinds(cs, raster: true));
        Assert.DoesNotContain(RenderDiagnosticKind.TransparencyGroupApproximated,
                              GroupCsKinds(cs, raster: false));
    }

    /// <summary>
    /// The scope guard: a <b>soft mask's</b> <c>/G</c> group is <i>supposed</i> to be
    /// <c>/DeviceGray</c> (§11.6.5.2 — a luminosity mask has one channel by definition), so the
    /// group-colour-space report must never reach it.
    /// </summary>
    /// <remarks>
    /// Without the <c>Do</c>-only scope this fires on essentially every soft mask in existence:
    /// seven test files in this suite alone declare exactly this shape, and
    /// <see cref="SoftMaskIdentityTransfer_IsNotReported"/> turns red immediately. The identical
    /// <c>/DeviceGray</c> group <i>is</i> reported when a <c>Do</c> invokes it — that case is in
    /// the theory above — so this pins the scope rather than the predicate.
    /// </remarks>
    [Fact]
    public void SoftMaskGroupCs_DeviceGray_IsNotReportedByEitherRenderer()
    {
        var kinds = new List<RenderDiagnosticKind>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind k, string d, Exception e)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            lock (kinds) kinds.Add(k);
        }

        RenderDiagnostics.OnEvent += Sink;
        try
        {
            RenderSoftMaskSvg(maskSubtype: "/Luminosity", extra: "");
            using var ms = new MemoryStream(SoftMaskPdf());
            PdfPageRenderer.RenderPage(ms, 0, 72);
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        Assert.DoesNotContain(RenderDiagnosticKind.TransparencyGroupApproximated, kinds);
    }

    /// <summary>
    /// Renders a page whose only ExtGState carries <paramref name="extGsEntries"/> and returns
    /// every diagnostic kind one renderer raised for it.
    /// </summary>
    private static List<RenderDiagnosticKind> GsParameterKinds(string extGsEntries, bool raster) =>
        RenderAndCollectKinds(GsPdf(extGsEntries), raster);

    /// <summary>Renders <paramref name="content"/> as a bare page and collects the kinds.</summary>
    private static List<RenderDiagnosticKind> ContentKinds(string content, bool raster) =>
        RenderAndCollectKinds(Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << >> >>",
            Stream("", content)), raster);

    /// <summary>Renders a <c>Do</c>-invoked transparency group and collects the kinds.</summary>
    private static List<RenderDiagnosticKind> GroupCsKinds(string cs, bool raster) =>
        RenderAndCollectKinds(GroupCsPdf(cs), raster);

    /// <summary>
    /// Runs <paramref name="pdf"/> through one of the two renderers and returns every diagnostic
    /// kind it raised <b>on this thread</b>. <c>RenderDiagnostics.OnEvent</c> is process-wide, so
    /// an unfiltered handler would also collect whatever xUnit is running beside it and every
    /// negative assertion above would depend on the neighbours.
    /// </summary>
    private static List<RenderDiagnosticKind> RenderAndCollectKinds(byte[] pdf, bool raster)
    {
        var kinds = new List<RenderDiagnosticKind>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind k, string d, Exception e)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            lock (kinds) kinds.Add(k);
        }

        RenderDiagnostics.OnEvent += Sink;
        try
        {
            using var ms = new MemoryStream(pdf);
            if (raster) PdfPageRenderer.RenderPage(ms, 0, 72);
            else        PdfSvgRenderer.RenderPageToSvg(ms, 0);
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        lock (kinds) return new List<RenderDiagnosticKind>(kinds);
    }

    private static byte[] GsPdf(string extGsEntries) => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
        $" /Resources << /ExtGState << /G1 << /Type /ExtGState {extGsEntries} >> >> >> >>",
        Stream("", "q /G1 gs 0 0 100 100 re f Q"));

    /// <summary>
    /// A form XObject declaring itself a transparency group with the given <c>/CS</c> (omitted
    /// when <paramref name="cs"/> is null), invoked by <c>Do</c>.
    /// </summary>
    /// <remarks>
    /// <c>/S /Transparency</c> is written out rather than left to default, so this fixture
    /// measures <c>/CS</c> alone. The absent-<c>/S</c> case is its own test below — the renderers
    /// could disagree on it (one defaulting it to a transparency group, the other not), and writing
    /// <c>/S</c> in every fixture would hide that.
    /// </remarks>
    private static byte[] GroupCsPdf(string cs)
    {
        string csEntry = cs != null ? $" /CS {cs}" : "";
        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /XObject << /Fx1 5 0 R >> >> >>",
            Stream("", "/Fx1 Do"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 100 100]" +
                   $" /Group << /Type /Group /S /Transparency{csEntry} >>",
                   "1 0 0 rg 0 0 50 50 re f"));
    }

    /// <summary><see cref="GroupCsPdf"/> with the group's <c>/S</c> omitted.</summary>
    /// <summary>
    /// A text object drawn at <c>ca 0.5</c> and saying nothing about <c>/TK</c> — which is the
    /// point: knockout is what the file asks for by default.
    /// </summary>
    private static byte[] TranslucentTextPdf(string extraGs = "") => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources <<" +
        " /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >>" +
        $" /ExtGState << /GS1 << /Type /ExtGState /ca 0.5 {extraGs} >> >> >> >>",
        Stream("", "q /GS1 gs BT /F1 36 Tf 1 0 0 1 10 20 Tm (Hi) Tj ET Q"));

    private static byte[] GroupCsPdfWithoutSubtype(string cs)
    {
        string csEntry = cs != null ? $" /CS {cs}" : "";
        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /XObject << /Fx1 5 0 R >> >> >>",
            Stream("", "/Fx1 Do"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 100 100]" +
                   $" /Group << {csEntry} >>",
                   "1 0 0 rg 0 0 50 50 re f"));
    }

    /// <summary>
    /// The raster counterpart of <see cref="RenderSoftMaskSvg"/>: the same luminosity mask over a
    /// <c>/DeviceGray</c> group, which §11.6.5.2 says is what a luminosity group should declare.
    /// </summary>
    private static byte[] SoftMaskPdf() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R" +
        " /Resources << /ExtGState << /G1 5 0 R >> >> >>",
        Stream("", "/G1 gs 1 0 0 rg 0 0 200 200 re f"),
        "<< /Type /ExtGState /SMask << /S /Luminosity /G 6 0 R >> >>",
        Stream("/Type /XObject /Subtype /Form /BBox [0 0 100 100]" +
               " /Group << /Type /Group /S /Transparency /CS /DeviceGray >>",
               "0.5 g 0 0 100 100 re f"));

    /// <summary>
    /// Renders <paramref name="content"/> through one of the two renderers with overprint
    /// simulation on, and returns the details of every <c>OverprintNotSimulated</c> it raised.
    /// </summary>
    private static List<string> OverprintReasons(string content, bool raster, bool simulate = true)
    {
        var details = new List<string>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind k, string d, Exception e)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            if (k == RenderDiagnosticKind.OverprintNotSimulated)
                lock (details) details.Add(d ?? string.Empty);
        }

        var options = new PdfRenderOptions { SimulateOverprint = simulate };
        RenderDiagnostics.OnEvent += Sink;
        try
        {
            using var ms = new MemoryStream(OverprintReportingTests.BuildPdf(content));
            if (raster) PdfPageRenderer.RenderPage(ms, 0, 72, options);
            else        PdfSvgRenderer.RenderPageToSvg(ms, 0, options);
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        return details;
    }

    // ====================================================================
    //  Fixtures
    // ====================================================================

    /// <summary>
    /// 50% cyan over 50% cyan, the second fill set to overprint under <c>/OPM 1</c>.
    /// </summary>
    private const string OverprintContent =
        "0.5 0 0 0 k 0 0 100 100 re f q /GS0 gs 0.5 0 0 0 k 20 20 60 60 re f Q";

    private static string RenderOverprintSvg()
    {
        using var ms = new MemoryStream(OverprintReportingTests.BuildPdf(OverprintContent));
        return PdfSvgRenderer.RenderPageToSvg(ms, 0,
            new PdfRenderOptions { SimulateOverprint = true });
    }

    private static SkiaSharp.SKBitmap RenderOverprintRaster()
    {
        using var ms = new MemoryStream(OverprintReportingTests.BuildPdf(OverprintContent));
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72,
            new PdfRenderOptions { SimulateOverprint = true });
        var bmp = SkiaSharp.SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>The <c>d</c> of the first clipPath's path — the clip's actual geometry.</summary>
    private static string ClipPathData(string svg) =>
        XDocument.Parse(svg).Descendants()
            .First(e => e.Name.LocalName == "clipPath")
            .Descendants().First(e => e.Name.LocalName == "path")
            .Attribute("d")!.Value;

    private static string RenderFormXObjectSvg(string bbox, string matrix)
    {
        string matrixEntry = matrix != null ? $" /Matrix {matrix}" : "";
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /XObject << /Fx1 5 0 R >> >> >>",
            Stream("", "/Fx1 Do"),
            // The form paints well outside its BBox, so the clip is observable.
            Stream($"/Type /XObject /Subtype /Form /BBox {bbox}{matrixEntry}",
                   "1 0 0 rg 0 0 400 400 re f"));
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static string HrefOf(XElement image) =>
        image.Attributes().First(a => a.Name.LocalName == "href").Value;

    /// <summary>Decodes the first embedded PNG data URI in the document.</summary>
    private static SkiaSharp.SKBitmap DecodeEmbeddedImage(string svg)
    {
        var image = XDocument.Parse(svg).Descendants()
            .First(e => e.Name.LocalName == "image");
        string href = HrefOf(image);
        const string prefix = "data:image/png;base64,";
        Assert.StartsWith(prefix, href);

        var bmp = SkiaSharp.SKBitmap.Decode(Convert.FromBase64String(href.Substring(prefix.Length)));
        Assert.NotNull(bmp);
        return bmp;
    }

    private static string RenderImageXObjectSvg(string imageDictEntries, string hexData)
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "1 0 0 rg q 200 0 0 200 100 400 cm /Im1 Do Q"),
            Stream($"/Type /XObject /Subtype /Image {imageDictEntries} /Filter /ASCIIHexDecode",
                   hexData));
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static string RenderSoftMaskSvg(string maskSubtype, string extra)
    {
        const string groupCs = "0.5 g 0 0 100 100 re f";
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /ExtGState << /G1 5 0 R >> >> >>",
            Stream("", "/G1 gs 1 0 0 rg 0 0 200 200 re f"),
            $"<< /Type /ExtGState /SMask << /S {maskSubtype} /G 6 0 R {extra} >> >>",
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 100 100]" +
                   " /Group << /S /Transparency /CS /DeviceGray >>", groupCs));
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static List<XElement> PaintedPaths(string svg) =>
        XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "path")
            .Where(e => (e.Attribute("fill")?.Value ?? "none") != "none")
            // Hidden optional content is exported in a group a viewer does not display.
            .Where(e => !e.Ancestors().Any(a => (string)a.Attribute("display") == "none"))
            .ToList();

    private static List<XElement> StrokedPaths(string svg) =>
        XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "path")
            .Where(e => (e.Attribute("stroke")?.Value ?? "none") != "none")
            .ToList();

    private static XElement SinglePattern(string svg) =>
        XDocument.Parse(svg).Descendants().Single(e => e.Name.LocalName == "pattern");

    private static string RenderSvg(string contentStream)
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << >> >>",
            Stream("", contentStream));
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static string RenderSvgWithExtGState(string gsEntries, string contentStream)
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /ExtGState << /G1 5 0 R >> >> >>",
            Stream("", "/G1 gs " + contentStream),
            $"<< /Type /ExtGState {gsEntries} >>");
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static string RenderTilingPatternSvg(string patternExtras,
                                                 string bbox = "[0 0 10 10]",
                                                 string contentPrefix = "",
                                                 string contentSuffix = "",
                                                 string cell = "1 0 0 rg 0 0 4 4 re f")
    {
        string content = contentPrefix +
                         "/Pattern cs /P1 scn 100 100 200 200 re f" +
                         contentSuffix;

        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /Pattern << /P1 5 0 R >> >> >>",
            Stream("", content),
            Stream($"/PatternType 1 /PaintType 1 /TilingType 1 /BBox {bbox} {patternExtras}", cell));
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static string RenderFunctionShadingPatternSvg()
    {
        // Type 1 (function-based) shading: no SVG gradient can express it, so the writer must
        // rasterize it. /Matrix lifts the unit domain onto a 200x200 area of the page.
        const string shading =
            "<< /ShadingType 1 /ColorSpace /DeviceRGB /Domain [0 1 0 1]" +
            " /Function << /FunctionType 2 /Domain [0 1] /C0 [0 0 1] /C1 [0 0 1] /N 1 >> >>";

        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /Pattern << /P1 5 0 R >> >> >>",
            // Red is set first: it is what the buggy fallback painted.
            Stream("", "1 0 0 rg /Pattern cs /P1 scn 100 100 200 200 re f"),
            $"<< /PatternType 2 /Matrix [200 0 0 200 100 100] /Shading {shading} >>");
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    /// <summary>
    /// Two function-shading patterns, one used on the page and one inside a form XObject — so two
    /// different <c>SvgWriter</c> instances mint the ids.
    /// </summary>
    private static string RenderTwoFunctionShadingPatternsSvg()
    {
        const string shading =
            "<< /ShadingType 1 /ColorSpace /DeviceRGB /Domain [0 1 0 1]" +
            " /Function << /FunctionType 2 /Domain [0 1] /C0 [0 0 1] /C1 [0 0 1] /N 1 >> >>";

        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /Pattern << /P1 5 0 R >> /XObject << /Fx1 6 0 R >> >> >>",
            Stream("", "/Pattern cs /P1 scn 100 100 200 200 re f /Fx1 Do"),
            $"<< /PatternType 2 /Matrix [200 0 0 200 100 100] /Shading {shading} >>",
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 612 792]" +
                   " /Resources << /Pattern << /P2 7 0 R >> >>",
                   "/Pattern cs /P2 scn 350 350 100 100 re f"),
            $"<< /PatternType 2 /Matrix [100 0 0 100 350 350] /Shading {shading} >>");
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static string RenderOcImageSvg(bool ocgOff)
    {
        // 2x2 RGB image, hex-encoded.
        const string imgHex = "FF0000 00FF00 0000FF FFFFFF>";
        string offEntry = ocgOff ? " /OFF [6 0 R]" : "";

        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R" +
            $" /OCProperties << /OCGs [6 0 R] /D << /Order [6 0 R]{offEntry} >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 200 0 0 200 100 400 cm /Im1 Do Q"),
            Stream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB" +
                   " /BitsPerComponent 8 /OC 6 0 R /Filter /ASCIIHexDecode", imgHex),
            "<< /Type /OCG /Name (Watermark) >>");
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
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
