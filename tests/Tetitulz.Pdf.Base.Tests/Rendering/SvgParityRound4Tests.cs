// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Round 4 of the raster-vs-SVG parity sweep (GAPS.md §2), covering the four divergences round 3
/// left open on the SVG side:
///
/// <list type="number">
///   <item>the §9.7.4.3 per-glyph vertical position vector <c>v</c>, never applied on the
///         <c>&lt;text&gt;</c> path;</item>
///   <item>soft masks applied per element rather than per transparency group, and omitted from
///         plain <c>&lt;text&gt;</c> altogether;</item>
///   <item>a soft-mask group's <c>/BBox</c> and <c>/Matrix</c> ignored (and the <c>&lt;mask&gt;</c>
///         region left at SVG's object-bounding-box default);</item>
///   <item>the colour-key <c>/Mask</c> decoded-pixel fallback conditions, which asked about the
///         key's arity where the raster path asks about the image's.</item>
/// </list>
///
/// <para><b>Every assertion is on the parsed SVG or on decoded pixels</b> — never on a substring,
/// and never merely non-null. Where the claim is "the two renderers agree", both are rendered and
/// compared, the pattern <c>SvgRasterParityTests.CalGrayFill_SvgMatchesTheRasterRender</c>
/// established.</para>
/// </summary>
public sealed class SvgParityRound4Tests
{
    // ====================================================================
    //  1. The vertical position vector v (§9.7.4.3)
    // ====================================================================

    /// <summary>
    /// §9.7.4.3: in vertical writing a glyph is drawn at <c>currentPoint − v</c>. The position
    /// vector <c>v</c> centres the glyph on its column (Table 117's default x is the glyph's
    /// horizontal <b>half-width</b>) and drops it to the vertical baseline (<c>/DW2[0]</c>,
    /// default 880). Only <c>w1y</c> was read here, so the pen advanced correctly and every glyph
    /// was then drawn on its HORIZONTAL origin.
    ///
    /// <para>The whole run is one <c>&lt;text&gt;</c> element, so the shift is expressed as SVG's
    /// own per-character <c>x</c>/<c>y</c> coordinate lists, in the element's space where one unit
    /// is one em. With <c>/DW 1000</c> the half-width is 0.5 em and the default <c>/DW2[0]</c> is
    /// 0.88 em, so <c>v = (0.5, 0.88)</c> and the glyph sits at <c>−v</c>: <b>x = −0.5</b>, and
    /// <b>y = +0.88</b> because the element carries the extra Y-flip that turns a text-space −0.88
    /// into +0.88. The second glyph is one full em further down the column.</para>
    ///
    /// <para>With <c>v</c> dropped the lists read <c>(0, 0)</c> and <c>(0, 1)</c>, which is what
    /// this pins.</para>
    ///
    /// <para><b>These expectations were x and y the other way round</b> while the vertical glyph
    /// matrix still rotated 90°, which mapped the em's x-axis onto the column. That rotation was a
    /// defect — §9.7.4.3 changes the writing direction, not the glyph orientation — and removing it
    /// swapped the axes back. The placement code was unaffected: it works through the emitted
    /// matrix's inverse, so it was correct in both frames.</para>
    /// </summary>
    [Fact]
    public void VerticalWriting_PlacesEachGlyphAtCurrentPointMinusV()
    {
        XElement text = SingleVerticalText(w2: null);

        AssertCoords(text, "x", -0.5f, -0.5f);
        AssertCoords(text, "y", 0.88f, 1.88f);
    }

    /// <summary>
    /// And <c>v</c> comes from the font, not from a constant: a <c>/W2</c> entry states all three
    /// of <c>w1y v1x v1y</c> per CID (Table 117), and only the covered CID may take them.
    ///
    /// <para><c>/W2 [65 [-500 250 300]]</c> covers CID 65 alone: it advances half an em and its
    /// vector is (0.25, 0.30) em, so it sits at <c>x = −0.25</c>, <c>y = 0.30</c>. CID 66 is
    /// uncovered and falls back to the defaults — half-width 0.5 em and <c>/DW2[0]</c> 0.88 em —
    /// one <b>half</b> em further down the column, at <c>x = −0.5</c>, <c>y = 0.5 + 0.88</c>. A
    /// single vector applied to the whole run, or the defaults applied to both, gives neither pair.</para>
    ///
    /// <para>x and y were the other way round while the vertical glyph matrix still rotated — see
    /// the note on <see cref="VerticalWriting_PlacesEachGlyphAtCurrentPointMinusV"/>.</para>
    /// </summary>
    [Fact]
    public void VerticalWriting_PositionVectorComesFromW2_PerCid()
    {
        XElement text = SingleVerticalText(w2: "[65 [-500 250 300]]");

        AssertCoords(text, "x", -0.25f, -0.5f);
        AssertCoords(text, "y", 0.30f, 1.38f);
    }

    /// <summary>
    /// A horizontal run must NOT acquire coordinate lists — the font's own advances already agree
    /// with the PDF widths there, and per-character coordinates would override the shaping the
    /// viewer does. Without this the vertical fix would silently change every text element in
    /// every exported page.
    /// </summary>
    [Fact]
    public void HorizontalWriting_EmitsNoPerGlyphCoordinateLists()
    {
        string svg = SvgOf(Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("", "BT /F1 10 Tf 100 700 Td (AB) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));

        XElement text = Assert.Single(
            XDocument.Parse(svg).Descendants().Where(e => e.Name.LocalName == "text"));

        Assert.Null(text.Attribute("x"));
        Assert.Null(text.Attribute("y"));
    }

    // ====================================================================
    //  2. Soft masks: on plain <text>, and on the group rather than each element
    // ====================================================================

    /// <summary>
    /// <c>mask=</c> was emitted on <c>&lt;path&gt;</c>, on <c>&lt;image&gt;</c>/<c>&lt;use&gt;</c>,
    /// on the glyph-outline <c>&lt;g&gt;</c> and on the rasterized shading — but not on plain
    /// <c>&lt;text&gt;</c>. That is the element every NON-embedded font takes (base-14 and every
    /// system substitute), so ordinary text under a soft mask exported completely unmasked while
    /// the same run in an embedded font was masked.
    /// </summary>
    [Fact]
    public void SoftMask_IsAppliedToPlainTextElements()
    {
        string svg = RenderMaskedTextSvg();
        var doc = XDocument.Parse(svg);

        string maskId = Assert.Single(doc.Descendants().Where(e => e.Name.LocalName == "mask"))
            .Attribute("id")!.Value;
        XElement text = Assert.Single(doc.Descendants().Where(e => e.Name.LocalName == "text"));

        Assert.Equal($"url(#{maskId})", text.Attribute("mask")?.Value);
    }

    /// <summary>
    /// §11.6.5.2 / §11.4.5: the mask applies to a transparency group's <b>composited result</b>.
    /// The raster path does exactly that — <c>ContentStreamInterpreter.RunFormXObject</c> opens a
    /// layer, <c>ClearSoftMask</c> keeps the mask off the elements inside it, and
    /// <c>EndTransparencyGroup(groupSoftMask)</c> applies it once. The SVG child writer inherited
    /// the mask instead, so every element carried its own <c>mask=</c> and two overlapping shapes
    /// under a half-transparent mask each lost half their alpha where they overlap.
    ///
    /// <para>Asserted structurally, because that <i>is</i> the difference: exactly one element in
    /// the document may reference the mask, it must be the group wrapper, and nothing beneath it
    /// may reference it again.</para>
    /// </summary>
    [Fact]
    public void SoftMask_OnATransparencyGroup_RidesOnTheGroupNotOnEachElement()
    {
        var doc = XDocument.Parse(RenderMaskedGroupSvg(transparencyGroup: true));

        XElement masked = Assert.Single(
            doc.Descendants().Where(e => e.Attribute("mask") != null));

        Assert.Equal("g", masked.Name.LocalName);
        Assert.Equal(2, masked.Descendants().Count(
            e => e.Name.LocalName == "path" && (e.Attribute("fill")?.Value ?? "none") != "none"));
    }

    /// <summary>
    /// The control, without which the test above proves only that something carries the mask: a
    /// form that is <b>not</b> a transparency group has no composited result to mask, so §11.3.4
    /// applies the mask per painted object — which is what the raster path does there too (no
    /// layer is opened). Both painted paths must carry it, and no wrapper may.
    /// </summary>
    [Fact]
    public void SoftMask_OnAPlainFormXObject_StaysOnEachElement()
    {
        var doc = XDocument.Parse(RenderMaskedGroupSvg(transparencyGroup: false));

        var masked = doc.Descendants().Where(e => e.Attribute("mask") != null).ToList();

        Assert.Equal(2, masked.Count);
        Assert.All(masked, e => Assert.Equal("path", e.Name.LocalName));
    }

    // ====================================================================
    //  3. The soft-mask group's /BBox, /Matrix and mask region
    // ====================================================================

    /// <summary>
    /// §11.6.5.2 makes the mask's <c>/G</c> a form XObject, so §8.10.2's <c>/Matrix</c> applies to
    /// it exactly as it does to any form: it concatenates onto the CTM in effect at the <c>gs</c>.
    /// It was ignored, so a mask group declaring a translation masked the wrong region.
    /// </summary>
    [Fact]
    public void SoftMaskGroup_HonoursItsMatrix()
    {
        var plain      = MaskContentBounds(RenderSoftMaskSvg(matrix: null,   bbox: "[0 0 100 100]"));
        var translated = MaskContentBounds(RenderSoftMaskSvg(matrix: "[1 0 0 1 200 0]",
                                                             bbox: "[0 0 100 100]"));

        Assert.Equal((0f, 100f), plain);
        Assert.Equal((200f, 300f), translated);
    }

    /// <summary>
    /// And the group's <c>/BBox</c> clips it (§8.10.2), mapped through <c>/Matrix ∘ CTM</c> like
    /// every other form box. The fixture's group paints 100×100 inside a 50×50 box, so an
    /// unclipped export masks twice the area the file asked for.
    /// </summary>
    [Fact]
    public void SoftMaskGroup_ClipsItsContentToTheBBox_MappedThroughTheMatrix()
    {
        string plain      = RenderSoftMaskSvg(matrix: null, bbox: "[0 0 50 50]");
        string translated = RenderSoftMaskSvg(matrix: "[1 0 0 1 200 0]", bbox: "[0 0 50 50]");

        Assert.Equal((0f, 50f), MaskClipBounds(plain));
        Assert.Equal((200f, 250f), MaskClipBounds(translated));
    }

    /// <summary>
    /// A group with no <c>/BBox</c> is not clipped — the box is what produces the clip, not the
    /// presence of a group, so this separates "reads /BBox" from "always wraps the content".
    /// </summary>
    [Fact]
    public void SoftMaskGroup_WithoutABBox_EmitsNoClip()
    {
        string svg = RenderSoftMaskSvg(matrix: null, bbox: null);

        XElement mask = Assert.Single(
            XDocument.Parse(svg).Descendants().Where(e => e.Name.LocalName == "mask"));

        Assert.DoesNotContain(mask.Descendants(), e => e.Attribute("clip-path") != null);
    }

    /// <summary>
    /// The <c>&lt;mask&gt;</c> carried no <c>maskUnits</c>, so its region defaulted to
    /// <c>objectBoundingBox</c> — the <b>referencing</b> element's box grown by 10%. Two
    /// consequences: a soft mask's extent depended on which shape it landed on (so moving it onto a
    /// group wrapper, above, would change it), and an element with a zero-extent box — a horizontal
    /// stroked line, <c>fill="none"</c> — has its rendering disabled outright by the SVG mask
    /// rules. A soft mask is a graphics-state property, so its region is the page.
    /// </summary>
    [Fact]
    public void SoftMask_RegionIsThePageInUserSpace()
    {
        XElement mask = Assert.Single(
            XDocument.Parse(RenderSoftMaskSvg(matrix: null, bbox: "[0 0 100 100]"))
                .Descendants().Where(e => e.Name.LocalName == "mask"));

        Assert.Equal("userSpaceOnUse", mask.Attribute("maskUnits")?.Value);
        Assert.Equal(0f,   Num(mask, "x"));
        Assert.Equal(0f,   Num(mask, "y"));
        Assert.Equal(612f, Num(mask, "width"));
        Assert.Equal(792f, Num(mask, "height"));
    }

    /// <summary>
    /// And the region's origin is the page box's lower-left corner, not (0,0): the exporter's outer
    /// group applies <c>translate(-pdfX,-pdfY)</c>, so every emitted coordinate is a raw PDF one
    /// and a <c>/CropBox</c> with a non-zero origin puts the visible page nowhere near the origin.
    /// A region anchored at 0,0 would mask away everything past <c>pdfW</c>. The <c>/BC</c> backdrop
    /// rect takes the same origin, which it did not before.
    /// </summary>
    [Fact]
    public void SoftMask_RegionFollowsTheCropBoxOrigin()
    {
        string svg = RenderSoftMaskSvg(matrix: null, bbox: "[0 0 100 100]",
                                       extra: "/BC [1]",
                                       cropBox: "/CropBox [100 150 300 450]");
        XElement mask = Assert.Single(
            XDocument.Parse(svg).Descendants().Where(e => e.Name.LocalName == "mask"));

        Assert.Equal(100f, Num(mask, "x"));
        Assert.Equal(150f, Num(mask, "y"));
        Assert.Equal(200f, Num(mask, "width"));
        Assert.Equal(300f, Num(mask, "height"));

        XElement backdrop = mask.Elements().First();
        Assert.Equal("rect", backdrop.Name.LocalName);
        Assert.Equal(100f, float.Parse(backdrop.Attribute("x")!.Value, CultureInfo.InvariantCulture));
        Assert.Equal(150f, float.Parse(backdrop.Attribute("y")!.Value, CultureInfo.InvariantCulture));
    }

    // ====================================================================
    //  4. Colour-key /Mask — the decoded-pixel fallback conditions
    // ====================================================================

    /// <summary>
    /// §8.9.6.4 wants one <c>[min max]</c> pair per component, so the shared raw-sample evaluator
    /// (<c>ContentStreamInterpreter.TryComputeColorKeyMask</c>) declines a one-pair key on a
    /// three-component image and both renderers reach their fallback. The raster fallback requires
    /// the key to cover the image's components, so nothing is masked. The SVG writer's own
    /// condition was <c>pairs == 1 || pairs >= 3</c> — a question about the KEY's arity, never the
    /// image's — so it treated the pair as a grey range and erased every pixel whose RED fell
    /// inside it.
    /// </summary>
    [Fact]
    public void ColourKeyFallback_OnePairOnAnRgbImage_MasksNothing_LikeTheRaster()
    {
        // Left pixel is pure red (R=255, inside [200 255]); right is dark blue (R=0, outside).
        var probe = ColourKeyProbe("/DeviceRGB", "FF0000 000080>", "[200 255]");

        Assert.Equal(255, probe.SvgLeftAlpha);        // SVG: not masked
        Assert.False(probe.RasterLeftIsBackdrop);     // raster: not masked either
        Assert.Equal(255, probe.SvgRightAlpha);
    }

    /// <summary>
    /// The control in the opposite direction, which is what stops the test above from passing on a
    /// fallback that simply never fires. Two pairs on a ONE-component image: the raw evaluator
    /// still declines (the counts disagree), and the raster fallback accepts it — it asks only that
    /// the key COVER the image's components — masking on the first pair. The SVG writer's
    /// <c>pairs == 1 || pairs >= 3</c> rejected exactly this shape, so it masked nothing.
    /// </summary>
    [Fact]
    public void ColourKeyFallback_TwoPairsOnAGreyImage_Masks_LikeTheRaster()
    {
        // Left sample is 0xFF (inside [200 255]); right is 0x00 (outside).
        var probe = ColourKeyProbe("/DeviceGray", "FF00>", "[200 255 0 10]");

        Assert.Equal(0, probe.SvgLeftAlpha);          // SVG: masked
        Assert.True(probe.RasterLeftIsBackdrop);      // raster: masked too
        Assert.Equal(255, probe.SvgRightAlpha);
    }

    /// <summary>
    /// The fallback is defensible only at 8 bpc, where the decoded channel value and the source
    /// sample are the same number. The raster path tests for that; the SVG writer never did, so a
    /// 16-bpc image had its key — stated in 16-bit sample values — compared against 8-bit channels.
    /// Two pairs on a one-component image is the shape the raster fallback accepts, so the depth is
    /// the only thing left deciding, and here it must decide against masking.
    /// </summary>
    [Fact]
    public void ColourKeyFallback_SixteenBitSamples_MasksNothing_LikeTheRaster()
    {
        var probe = ColourKeyProbe("/DeviceGray", "FFFF 0000>", "[200 255 0 10]", bpc: 16);

        Assert.Equal(255, probe.SvgLeftAlpha);
        Assert.False(probe.RasterLeftIsBackdrop);
        Assert.Equal(255, probe.SvgRightAlpha);
    }

    /// <summary>
    /// An entry that is not a number: the raster fallback reads it as <b>0</b> and then orders the
    /// pair, so <c>[200 null]</c> is the range 0..200. The SVG writer defaulted it to <b>255</b>
    /// and never ordered anything, making the same entry 200..255 — the complementary range, so
    /// the two renderers masked opposite pixels rather than merely differing at an edge.
    /// </summary>
    [Fact]
    public void ColourKeyFallback_UnparseableMaximum_TakesTheRasterDefaultAndOrdering()
    {
        // Left sample 0xFF = 255, right 0x00. Under 0..200 the RIGHT pixel goes; under the old
        // 200..255 the LEFT one did.
        var probe = ColourKeyProbe("/DeviceGray", "FF00>", "[200 null 0 10]");

        Assert.Equal(255, probe.SvgLeftAlpha);
        Assert.Equal(0, probe.SvgRightAlpha);
        Assert.False(probe.RasterLeftIsBackdrop);
        Assert.True(probe.RasterRightIsBackdrop);
    }

    /// <summary>
    /// The raw-sample path is unaffected by any of this — a well-formed key still goes through the
    /// shared evaluator, never the fallback. Without this the two tests above could be satisfied by
    /// disabling colour-key masking outright.
    /// </summary>
    [Fact]
    public void ColourKey_WithOnePairPerComponent_StillMasksThroughTheSharedRawPath()
    {
        var probe = ColourKeyProbe("/DeviceRGB", "FF0000 000080>", "[200 255 0 40 0 40]");

        Assert.Equal(0, probe.SvgLeftAlpha);
        Assert.True(probe.RasterLeftIsBackdrop);
        Assert.Equal(255, probe.SvgRightAlpha);
    }

    // ====================================================================
    //  Fixtures and accessors
    // ====================================================================

    /// <summary>The one <c>&lt;text&gt;</c> element of a two-glyph Identity-V run at size 10.</summary>
    private static XElement SingleVerticalText(string w2)
    {
        string w2Entry = w2 != null ? $" /W2 {w2}" : "";
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /Font << /F1 5 0 R >> >> >>",
            // <0041 0042> = CIDs 65 and 66 under Identity-V: four bytes, two glyphs.
            Stream("", "BT /F1 10 Tf 100 700 Td <00410042> Tj ET"),
            "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding /Identity-V" +
            " /DescendantFonts [6 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Test" +
            " /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>" +
            $" /FontDescriptor 7 0 R /DW 1000{w2Entry} >>",
            "<< /Type /FontDescriptor /FontName /Test /Flags 4 /ItalicAngle 0" +
            " /Ascent 880 /Descent -120 /CapHeight 700 /StemV 80 /FontBBox [0 0 1000 1000] >>");

        return Assert.Single(
            XDocument.Parse(SvgOf(pdf)).Descendants().Where(e => e.Name.LocalName == "text"));
    }

    /// <summary>
    /// One coordinate list, compared per entry to 3 decimals. The emitted value passes through a
    /// page-space round trip and <c>G6</c> formatting, so 0.88 comes back as 0.879997 — a 3e-6
    /// relative error, and the reason this is not an exact comparison. Three decimals is still far
    /// tighter than the differences under test, which are whole tenths of an em.
    /// </summary>
    private static void AssertCoords(XElement text, string attribute, params float[] expected)
    {
        float[] actual = text.Attribute(attribute)!.Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => float.Parse(s, CultureInfo.InvariantCulture))
            .ToArray();

        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], actual[i], 3);
    }

    private static float Num(XElement e, string attribute) =>
        float.Parse(e.Attribute(attribute)!.Value, CultureInfo.InvariantCulture);

    /// <summary>A base-14 (non-embedded) run under an ExtGState soft mask.</summary>
    private static string RenderMaskedTextSvg() => SvgOf(Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
        " /Resources << /ExtGState << /G1 5 0 R >> /Font << /F1 7 0 R >> >> >>",
        Stream("", "/G1 gs BT /F1 24 Tf 100 700 Td (Masked) Tj ET"),
        "<< /Type /ExtGState /SMask << /S /Luminosity /G 6 0 R >> >>",
        Stream("/Type /XObject /Subtype /Form /BBox [0 0 612 792]" +
               " /Group << /S /Transparency /CS /DeviceGray >>", "0.5 g 0 0 300 792 re f"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));

    /// <summary>
    /// A form XObject painting two overlapping rectangles, invoked under a soft mask, optionally
    /// declaring itself a transparency group.
    /// </summary>
    private static string RenderMaskedGroupSvg(bool transparencyGroup)
    {
        string group = transparencyGroup
            ? " /Group << /S /Transparency /CS /DeviceRGB >>"
            : "";
        return SvgOf(Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /ExtGState << /G1 5 0 R >> /XObject << /Fx1 7 0 R >> >> >>",
            Stream("", "/G1 gs /Fx1 Do"),
            "<< /Type /ExtGState /SMask << /S /Luminosity /G 6 0 R >> >>",
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 612 792]" +
                   " /Group << /S /Transparency /CS /DeviceGray >>", "0.5 g 0 0 612 792 re f"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 400 400]" + group,
                   "1 0 0 rg 0 0 200 200 re f 0 0 1 rg 100 100 200 200 re f")));
    }

    private static string RenderSoftMaskSvg(string matrix, string bbox,
                                            string extra = "", string cropBox = "")
    {
        string matrixEntry = matrix != null ? $" /Matrix {matrix}" : "";
        string bboxEntry   = bbox   != null ? $" /BBox {bbox}"     : "";
        return SvgOf(Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " + cropBox +
            " /Contents 4 0 R /Resources << /ExtGState << /G1 5 0 R >> >> >>",
            Stream("", "/G1 gs 1 0 0 rg 0 0 400 400 re f"),
            $"<< /Type /ExtGState /SMask << /S /Luminosity /G 6 0 R {extra} >> >>",
            // The group paints 100x100, i.e. outside a 50x50 /BBox, so the clip is observable.
            Stream("/Type /XObject /Subtype /Form" + bboxEntry + matrixEntry +
                   " /Group << /S /Transparency /CS /DeviceGray >>", "1 g 0 0 100 100 re f")));
    }

    /// <summary>The x-extent of the painted path inside the single <c>&lt;mask&gt;</c>.</summary>
    private static (float min, float max) MaskContentBounds(string svg)
    {
        XElement mask = Assert.Single(
            XDocument.Parse(svg).Descendants().Where(e => e.Name.LocalName == "mask"));
        XElement path = Assert.Single(mask.Descendants().Where(
            e => e.Name.LocalName == "path" && (e.Attribute("fill")?.Value ?? "none") != "none"));
        return XExtent(path.Attribute("d")!.Value);
    }

    /// <summary>The x-extent of the clip the mask's content is wrapped in.</summary>
    private static (float min, float max) MaskClipBounds(string svg)
    {
        var doc = XDocument.Parse(svg);
        XElement mask = Assert.Single(doc.Descendants().Where(e => e.Name.LocalName == "mask"));
        XElement clipped = Assert.Single(
            mask.Descendants().Where(e => e.Attribute("clip-path") != null));

        string reference = clipped.Attribute("clip-path")!.Value;   // url(#smclipN)
        string id = reference.Substring(5, reference.Length - 6);
        XElement clipPath = Assert.Single(doc.Descendants().Where(
            e => e.Name.LocalName == "clipPath" && e.Attribute("id")?.Value == id));

        return XExtent(clipPath.Descendants()
            .First(e => e.Name.LocalName == "path").Attribute("d")!.Value);
    }

    /// <summary>Min/max X over the coordinate pairs in an SVG path's <c>d</c>.</summary>
    private static (float min, float max) XExtent(string d)
    {
        var xs = new List<float>();
        foreach (string token in d.Split(new[] { ' ', 'M', 'L', 'Z', 'z' },
                                         StringSplitOptions.RemoveEmptyEntries))
        {
            int comma = token.IndexOf(',');
            if (comma <= 0) continue;
            xs.Add(float.Parse(token.Substring(0, comma), CultureInfo.InvariantCulture));
        }
        Assert.NotEmpty(xs);
        return (xs.Min(), xs.Max());
    }

    /// <summary>What both renderers made of one colour-key <c>/Mask</c> on a 2x1 image.</summary>
    private readonly record struct ColourKeyResult(
        int SvgLeftAlpha, int SvgRightAlpha,
        bool RasterLeftIsBackdrop, bool RasterRightIsBackdrop);

    /// <summary>
    /// Renders a 2x1 raw-sample image over an opaque blue backdrop, both ways. The SVG side reports
    /// the alpha baked into the embedded PNG; the raster side reports whether the backdrop shows
    /// through where the left pixel is, which is the same question asked of a flattened surface.
    /// </summary>
    private static ColourKeyResult ColourKeyProbe(string colorSpace, string hexSamples, string mask,
                                                  int bpc = 8)
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "0 0 1 rg 100 400 200 200 re f q 200 0 0 200 100 400 cm /Im1 Do Q"),
            Stream($"/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace {colorSpace}" +
                   $" /BitsPerComponent {bpc} /Mask {mask} /Filter /ASCIIHexDecode", hexSamples));

        using SKBitmap embedded = DecodeFirstPng(SvgOf(pdf));
        Assert.Equal(2, embedded.Width);

        using SKBitmap raster = PixelAssert.Render(pdf);
        static bool IsBackdrop(SKColor c) => c.Blue > 200 && c.Red < 60 && c.Green < 60;

        return new ColourKeyResult(
            embedded.GetPixel(0, 0).Alpha, embedded.GetPixel(1, 0).Alpha,
            IsBackdrop(PixelAssert.At(raster, 150, 500)),
            IsBackdrop(PixelAssert.At(raster, 250, 500)));
    }

    private static SKBitmap DecodeFirstPng(string svg)
    {
        XElement image = XDocument.Parse(svg).Descendants()
            .First(e => e.Name.LocalName == "image");
        string href = image.Attributes().First(a => a.Name.LocalName == "href").Value;
        const string prefix = "data:image/png;base64,";
        Assert.StartsWith(prefix, href);

        var bmp = SKBitmap.Decode(Convert.FromBase64String(href.Substring(prefix.Length)));
        Assert.NotNull(bmp);
        return bmp!;
    }

    private static string SvgOf(byte[] pdf)
    {
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
