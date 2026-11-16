// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Round 2 of the raster-vs-SVG parity sweep (GAPS.md §2, "Exhaustive raster-vs-SVG parity sweep").
///
/// <para>The sweep's own conclusion was that every recurrence of the parity defect traced back to
/// <b>one concept with more than one implementation</b>. The raster path had five colour-space
/// resolvers and two component→sRGB converters; the tests here pin the behaviour of the single
/// resolver and single converter that replaced them, and the four spec deviations that the
/// duplication had been hiding.</para>
/// </summary>
public sealed class RasterParityRound2Tests
{
    // ====================================================================
    //  Item 1 — one converter, one resolver
    // ====================================================================

    /// <summary>
    /// The declared colour space decides the interpretation; the operand count decides only when
    /// the space is one the converter does not know.
    /// </summary>
    /// <remarks>
    /// There were two converters with two different rules. <c>ArrayToColor</c> dispatched on the
    /// operand COUNT for every space (so one component in DeviceRGB was a grey) while
    /// <c>ComponentsToColor</c> dispatched on the channel INDEX for every space (so four components
    /// in an unknown space were an RGB triple with the fourth dropped). Both were reachable for the
    /// same <c>/Separation</c> colour — see the round-trip test below.
    /// </remarks>
    [Theory]
    // Known family wins over the count: one component in DeviceRGB is red, not grey…
    [InlineData(PdfColorSpaceNames.DeviceRGB, new[] { 0.5f }, 128, 0, 0)]
    // …and three components in DeviceGray are the first one replicated, not an RGB triple.
    [InlineData(PdfColorSpaceNames.DeviceGray, new[] { 0.5f, 0f, 1f }, 127, 127, 127)]
    // Unknown family → the count decides. One component is grey…
    [InlineData("NotAColourSpace", new[] { 0.5f }, 127, 127, 127)]
    // …and four are CMYK (0,0,0,1 is black; an RGB reading would drop the K and give white).
    [InlineData("NotAColourSpace", new[] { 0f, 0f, 0f, 1f }, 0, 0, 0)]
    public void ComponentsToColor_DispatchesOnTheDeclaredSpace_FallingBackToTheCount(
        string cs, float[] comps, int r, int g, int b)
    {
        var c = PdfColor.ComponentsToColor(comps, cs);

        Assert.InRange(c.Red,   r - 2, r + 2);
        Assert.InRange(c.Green, g - 2, g + 2);
        Assert.InRange(c.Blue,  b - 2, b + 2);
        Assert.Equal(255, c.Alpha);
    }

    /// <summary>
    /// §8.6.8: a Pattern space carries no colour components, so evaluating one as if it did must
    /// not produce paint. Only one of the two converters knew that.
    /// </summary>
    [Fact]
    public void ComponentsToColor_PatternSpace_IsTransparent()
    {
        Assert.Equal(0, PdfColor.ComponentsToColor(new[] { 1f, 0f, 0f }, PdfColorSpaceNames.Pattern).Alpha);
    }

    /// <summary>
    /// One <c>/Separation</c> space, two paint operations — a <c>sc</c> fill and an image — must
    /// produce the same colour.
    /// </summary>
    /// <remarks>
    /// They did not: <c>ResolveSeparationColor</c> converted the tint transform's output through
    /// <c>ArrayToColor</c> and the image decoder through <c>ComponentsToColor</c>. With a transform
    /// whose output arity disagrees with its alternate space (a malformed file, but one that must
    /// still render <i>consistently</i>) the count-dispatching converter returned a grey and the
    /// index-dispatching one returned red — from the same space, on the same page.
    /// </remarks>
    [Fact]
    public void SeparationColour_IsTheSame_WhetherSetByScOrDecodedFromAnImage()
    {
        using var bmp = PixelAssert.Render(SeparationFillAndImagePdf());

        var fill  = PixelAssert.At(bmp, 50, 150);   // `/CS0 cs 1 sc` rectangle
        var image = PixelAssert.At(bmp, 50,  50);   // 1x1 /Separation image, sample 0xFF

        Assert.True(PixelAssert.Near(fill, image, tolerance: 2),
            $"the same Separation space rendered as {fill} through sc and {image} through an image");
    }

    /// <summary>
    /// And the value the two agree on is the one the DECLARED alternate space gives — the operand
    /// count is not evidence when the space is known.
    /// </summary>
    [Fact]
    public void SeparationColour_UsesTheDeclaredAlternateSpace_NotTheOutputCount()
    {
        using var bmp = PixelAssert.Render(SeparationFillAndImagePdf());
        var fill = PixelAssert.At(bmp, 50, 150);

        // The transform emits ONE value (0.5) into a declared /DeviceRGB alternate: red 128.
        // Counting operands instead would read it as DeviceGray and paint grey 128.
        Assert.InRange(fill.Red, 120, 136);
        Assert.InRange(fill.Green, 0, 8);
        Assert.InRange(fill.Blue,  0, 8);
    }

    /// <summary>
    /// A <c>/DeviceN</c> colorant-name array reached through an indirect reference still supplies
    /// the arity. Two of the five old resolvers followed the reference and two did not, so the same
    /// six-ink image was 6-component to the colour-key masker and 3-component to the decoder.
    /// </summary>
    [Fact]
    public void ColorComponents_DeviceN_ResolvesAnIndirectColorantNameArray()
    {
        using var doc = OpenFixture(DeviceNIndirectNamesPdf());
        var image = ImageXObject(doc);

        Assert.Equal(6, ImageSamples.ComponentCount(image, doc));
    }

    /// <summary>
    /// §8.6.6.3: an <c>/Indexed</c> space is ONE component (a palette index) whatever its base is —
    /// including when the base is itself an array, which is the form that used to make the shading
    /// resolver report the family "Indexed" and the arity 3.
    /// </summary>
    [Fact]
    public void ResolveSpace_IndexedOverAnArrayBase_IsOneComponent()
    {
        using var doc = OpenFixture(DeviceNIndirectNamesPdf());
        var cs = new PdfArray
        {
            new PdfName(PdfColorSpaceNames.Indexed),
            new PdfArray { new PdfName(PdfColorSpaceNames.CalRGB), new PdfDictionary() },
            new PdfNumber(1),
            new PdfString(new byte[] { 0, 0, 0, 255, 255, 255 }),
        };

        var resolved = PdfColor.ResolveSpace(cs, doc);

        Assert.Equal(PdfColorSpaceNames.Indexed, resolved.Family);
        Assert.Equal(1, resolved.Components);
    }

    /// <summary>
    /// §8.7.3.1: the underlying space of an uncoloured (PaintType 2) pattern may be any device,
    /// CIE-based or Separation/DeviceN space — so it may be an ARRAY. It was read as a bare name,
    /// which discarded every array form and left the operand-count guess (DeviceRGB) in its place.
    /// </summary>
    /// <remarks>
    /// Reachable only because the resolver now returns the array for <c>[/Pattern base]</c> too;
    /// the old resolver listed the families whose array it kept, and Pattern was not among them.
    /// </remarks>
    [Fact]
    public void UncolouredPattern_ReadsAnArrayUnderlyingSpace()
    {
        // L* 50, a* 60, b* -60 is a mid-lightness purple. Read as raw DeviceRGB the same three
        // numbers clamp to (255, 255, 0) — yellow — which is what the bare-name path produced.
        using var bmp = PixelAssert.Render(UncolouredPatternOverLabPdf());
        var c = PixelAssert.At(bmp, 20, 20);

        Assert.True(c.Blue > 100, $"expected the Lab purple's blue, got {c}");
        Assert.True(c.Green < 100, $"expected the Lab purple, not the raw-RGB yellow, got {c}");
    }

    // ====================================================================
    //  Item 2 — the soft mask is a graphics-state parameter (§8.4.2 Table 52)
    // ====================================================================

    /// <summary>
    /// <c>q</c> saves the soft mask and <c>Q</c> restores it, so a mask established before the
    /// <c>q</c> still applies after the matching <c>Q</c>.
    /// </summary>
    /// <remarks>
    /// <c>GraphicsState.Clone</c> nulled it, citing §11.6.5.2 — which describes deriving a mask
    /// from a transparency group and says nothing about the state stack. §8.4.2 Table 52 lists the
    /// soft mask among the graphics-state parameters. <c>PopState</c> then disposed the live
    /// bitmap, so the mask was not merely forgotten but destroyed. The SVG renderer's
    /// <c>MemberwiseClone</c> had always kept it — here the raster side was the wrong one.
    /// </remarks>
    [Fact]
    public void SoftMask_EstablishedBeforeQ_StillAppliesAfterQ()
    {
        // Mask: white (opaque) over the left half of the page, black (transparent) over the right.
        // Two disjoint fills, one inside a q…Q block and one after it. Both must be masked.
        using var bmp = PixelAssert.Render(SoftMaskAcrossQPdf());

        Assert.True(PixelAssert.IsInk(PixelAssert.At(bmp, 100, 300)),
            "the fill inside q…Q is inside the mask and must paint");
        Assert.False(PixelAssert.IsInk(PixelAssert.At(bmp, 300, 300)),
            "the fill inside q…Q is outside the mask and must not paint");

        Assert.True(PixelAssert.IsInk(PixelAssert.At(bmp, 100, 100)),
            "the fill after Q is inside the mask and must paint");
        Assert.False(PixelAssert.IsInk(PixelAssert.At(bmp, 300, 100)),
            "the fill after Q is OUTSIDE the mask and must not paint — Q destroyed the mask");
    }

    /// <summary>
    /// A mask established INSIDE a <c>q…Q</c> block replaces the outer one only for the block, and
    /// the outer mask is live again after the <c>Q</c>.
    /// </summary>
    /// <remarks>
    /// This is the half that makes the ownership change falsifiable rather than a leak: the inner
    /// mask must be released at <c>Q</c> (nothing references it) while the outer one must not be
    /// (the restored frame does). Getting the release rule wrong in either direction shows up here
    /// — as a leak, or as drawing with a disposed <c>SKBitmap</c>.
    /// </remarks>
    [Fact]
    public void SoftMask_SetInsideQ_IsReplacedByTheOuterMaskAtQ()
    {
        // Outer mask (GS0): left half. Inner mask (GS1): top half.
        //   red  fill inside the q  → top half only
        //   blue fill after the Q   → left half only  (proves GS0 came back)
        using var bmp = PixelAssert.Render(NestedSoftMaskPdf());

        var topRight   = PixelAssert.At(bmp, 300, 300);
        var bottomLeft = PixelAssert.At(bmp, 100, 100);
        var topLeft    = PixelAssert.At(bmp, 100, 300);

        Assert.True(topRight.Red > 150 && topRight.Blue < 100,
            $"top-right must keep the inner mask's red — the outer mask forbids blue there; got {topRight}");
        Assert.True(bottomLeft.Blue > 150 && bottomLeft.Red < 100,
            $"bottom-left is inside the outer mask and outside the inner one, so blue only; got {bottomLeft}");
        Assert.True(topLeft.Blue > 150,
            $"top-left is inside both masks, so the later blue fill wins; got {topLeft}");
    }

    // ====================================================================
    //  Item 3 — cs/CS sets the initial colour (§8.6.8 Table 74)
    // ====================================================================

    /// <summary>
    /// §8.6.8: "shall then set the current colour to its initial value". <c>OpSetCS</c> set only the
    /// space, so the previous colour survived into a space it did not come from.
    /// </summary>
    [Theory]
    // A red fill, then a colour-space change, then a fill. The second fill must be the new space's
    // initial colour — black — not the red left over from `rg`.
    [InlineData("1 0 0 rg /DeviceGray cs")]
    [InlineData("1 0 0 rg /DeviceRGB cs")]
    // DeviceCMYK's initial value is 0 0 0 1, which is also black — but by a different route.
    [InlineData("1 0 0 rg /DeviceCMYK cs")]
    public void SetColorSpace_ResetsTheColourToBlack(string prologue)
    {
        using var bmp = PixelAssert.Render(SimplePage($"{prologue} 0 0 100 100 re f"));
        var c = PixelAssert.At(bmp, 50, 50);

        Assert.True(c.Red < 40 && c.Green < 40 && c.Blue < 40,
            $"cs must reset the colour to black; got {c} (the previous fill colour survived)");
    }

    /// <summary>The stroking operator is the same rule (§8.6.8 applies to <c>CS</c> too).</summary>
    [Fact]
    public void SetStrokeColorSpace_ResetsTheStrokeColourToBlack()
    {
        using var bmp = PixelAssert.Render(SimplePage(
            "1 0 0 RG /DeviceGray CS 20 w 0 50 m 100 50 l S"));
        var c = PixelAssert.At(bmp, 50, 50);

        Assert.True(c.Red < 40 && c.Green < 40 && c.Blue < 40, $"CS must reset the stroke colour; got {c}");
    }

    /// <summary>
    /// Table 74: <c>/Separation</c> and <c>/DeviceN</c> start at <b>tint 1.0</b> in every component —
    /// full ink, not none. The tint transform decides what that looks like, so this also proves the
    /// initial colour goes through the same colour pipeline an explicit <c>sc</c> does.
    /// </summary>
    [Fact]
    public void SetColorSpace_Separation_InitialTintIsOne()
    {
        // Transform: tint 0 → white, tint 1 → blue. The page is filled with no `sc` at all.
        using var bmp = PixelAssert.Render(SeparationInitialColourPdf());
        var c = PixelAssert.At(bmp, 50, 50);

        Assert.True(c.Blue > 200 && c.Red < 60,
            $"the initial colour of a Separation space is tint 1.0, which this transform maps to blue; got {c}");
    }

    /// <summary>Table 74: <c>/Indexed</c> starts at index 0 — a palette entry, not black.</summary>
    [Fact]
    public void SetColorSpace_Indexed_SelectsPaletteEntryZero()
    {
        // Palette entry 0 is pure red, entry 1 pure green; the page sets no index.
        using var bmp = PixelAssert.Render(IndexedInitialColourPdf());
        var c = PixelAssert.At(bmp, 50, 50);

        Assert.True(c.Red > 200 && c.Green < 60,
            $"the initial colour of an Indexed space is palette entry 0 (red here); got {c}");
    }

    /// <summary>
    /// Table 74: a <c>/Pattern</c> space's initial colour "causes nothing to be painted". Painting
    /// had no notion of that — <c>FillIsPattern</c> with no selected pattern fell through to the
    /// solid fill colour, so the operator painted an opaque black rectangle.
    /// </summary>
    [Fact]
    public void SetColorSpace_Pattern_PaintsNothing()
    {
        using var bmp = PixelAssert.Render(SimplePage("1 0 0 rg /Pattern cs 0 0 100 100 re f"));

        Assert.True(PixelAssert.IsBlank(bmp),
            "a fill in a Pattern space with no pattern selected must paint nothing (§8.6.8 Table 74)");
    }

    /// <summary>
    /// The control for the rule above: once <c>scn</c> selects a pattern the fill paints again, so
    /// "paints nothing" is a statement about the initial colour and not about Pattern spaces.
    /// </summary>
    [Fact]
    public void PatternSpace_WithAPatternSelected_StillPaints()
    {
        using var bmp = PixelAssert.Render(SelectedPatternPdf());

        Assert.False(PixelAssert.IsBlank(bmp), "a selected tiling pattern must still paint");
    }

    /// <summary>
    /// Re-entering the Pattern space RESETS the selection, because §8.6.8 makes <c>cs</c> set the
    /// initial colour whatever the space already was.
    /// </summary>
    /// <remarks>
    /// <b>Found by mutation-checking.</b> Deleting the whole initial-colour step left
    /// <see cref="SetColorSpace_Pattern_PaintsNothing"/> green: its fixture selects no pattern, so
    /// there is nothing for <c>cs</c> to clear and the paint-side guard alone carried the test.
    /// This fixture selects one first, which is the only way the clearing is observable.
    /// </remarks>
    [Fact]
    public void SetColorSpace_Pattern_ClearsAPreviouslySelectedPattern()
    {
        using var bmp = PixelAssert.Render(SelectedPatternPdf(reenterPatternSpace: true));

        Assert.True(PixelAssert.IsBlank(bmp),
            "the second `/Pattern cs` must reset the colour to 'nothing is painted'");
    }

    // ====================================================================
    //  Item 4 — an /ImageMask with padded sample data
    // ====================================================================

    /// <summary>
    /// §8.9.6.2: a stencil mask is one-component and 1-bit. It used to get the 3-component RGB
    /// fallback (only one of the five resolvers knew about <c>/ImageMask</c>), which shut off the
    /// 1-bpc decode path and left only a fallback demanding <c>data.Length == stride*h</c>
    /// EXACTLY — so any trailing padding dropped the image.
    /// </summary>
    [Theory]
    [InlineData(0)]    // the control: no padding, which always worked
    [InlineData(1)]    // one trailing byte — what /EncodedByteAlign routinely leaves
    [InlineData(7)]
    public void ImageMask_WithTrailingPadding_StillPaints(int padBytes)
    {
        using var bmp = PixelAssert.Render(ImageMaskPdf(padBytes, declareBpc: true));

        var c = PixelAssert.At(bmp, 25, 75);
        Assert.True(c.Red > 200 && c.Green < 60 && c.Blue < 60,
            $"the stencil must paint in the fill colour (red); got {c} — the image was dropped");
    }

    /// <summary>
    /// §8.9.6.2 Table 90 makes <c>/BitsPerComponent</c> implicit for a stencil mask, so an image
    /// mask that omits it is still 1-bit — not the 8-bit default.
    /// </summary>
    [Fact]
    public void ImageMask_WithoutBitsPerComponent_IsStillOneBit()
    {
        using var bmp = PixelAssert.Render(ImageMaskPdf(padBytes: 3, declareBpc: false));

        var c = PixelAssert.At(bmp, 25, 75);
        Assert.True(c.Red > 200 && c.Green < 60 && c.Blue < 60, $"the stencil was not decoded; got {c}");
    }

    /// <summary>The computed value behind both: one component, from the shared arity answer.</summary>
    [Fact]
    public void ColorComponents_ImageMask_IsOne()
    {
        using var doc = OpenFixture(ImageMaskPdf(padBytes: 0, declareBpc: true));

        Assert.Equal(1, ImageSamples.ComponentCount(ImageXObject(doc), doc));
    }

    // ====================================================================
    //  Item 5 — special colour spaces at every legal bit depth
    // ====================================================================

    /// <summary>
    /// §8.6.5.2: a CalGray image applies <c>/Gamma</c>. The colour-space-aware decoder was gated on
    /// <c>bitsPerComponent == 8</c>, so the identical space at 2, 4 or 16 bpc fell through to a
    /// device-grey path and lost the gamma without a word.
    /// </summary>
    [Theory]
    // bpc, packed sample hex, the value under /Gamma 2.2, and what the gamma dropped (gamma 1) gives.
    // Both are A^G encoded through sRGB (§8.6.5.2 makes A^G the luminance; PdfColor.CalGrayToSRgb):
    // 2 bpc "10" is A = 2/3, 4 bpc "1000" is 8/15, 8 bpc 0x80 is 128/255, 16 bpc is 32768/65535.
    [InlineData(2,  "80",   172, 213)]
    [InlineData(4,  "80",   137, 193)]
    [InlineData(8,  "80",   129, 188)]   // the case that already worked — the control
    [InlineData(16, "8000", 128, 188)]
    public void CalGrayImage_AppliesGamma_AtEveryBitDepth(int bpc, string hex, int expected, int withoutGamma)
    {
        using var bmp = PixelAssert.Render(CalGrayImagePdf(bpc, hex));
        var c = PixelAssert.At(bmp, 50, 50);

        Assert.InRange(c.Red, expected - 4, expected + 4);
        Assert.True(Math.Abs(c.Red - withoutGamma) > 8,
            $"{bpc} bpc rendered {c.Red}, the un-gamma'd grey — /Gamma was dropped");
    }

    /// <summary>
    /// §8.9.5.1: sub-byte samples are packed MSB-first and <b>every row starts on a byte
    /// boundary</b>, so a 3-wide 4-bpc image has a 2-byte stride with 4 bits of padding per row.
    /// </summary>
    /// <remarks>
    /// <b>Added after mutation-checking</b>: every other item-5 fixture is 1x1, which cannot
    /// observe a stride at all. Here row 1 begins at byte 2; reading it from byte 1 (a stride that
    /// forgot to round up) would take the padding nibble as its first sample and turn the
    /// bottom-left cell white.
    /// </remarks>
    [Fact]
    public void CalGrayImage_PacksEachRowOnAByteBoundary()
    {
        // 3x2 at 4 bpc. Row 0 = samples 8, 0, 15 (byte 1's high nibble); row 1 = 0, 0, 0.
        using var bmp = PixelAssert.Render(CalGrayGridPdf("80F00000"));

        var topLeft     = PixelAssert.At(bmp, 15, 45);
        var topRight    = PixelAssert.At(bmp, 75, 45);
        var bottomLeft  = PixelAssert.At(bmp, 15, 15);

        Assert.InRange(topLeft.Red, 130, 145);                     // sample 8 under /Gamma 2.2: 137
        Assert.True(topRight.Red > 220, $"sample 15 is white; got {topRight}");
        Assert.True(bottomLeft.Red < 40,
            $"row 1 must start at byte 2, not byte 1; got {bottomLeft} (it read the padding nibble)");
    }

    /// <summary>
    /// §8.6.6.4: a Separation image's samples are tints and go through the tint transform, at 1 bpc
    /// as much as at 8. At 1 bpc the old gate sent it to the bilevel-grey decoder instead, so a spot
    /// plate rendered black-and-white.
    /// </summary>
    [Fact]
    public void SeparationImage_AtOneBitPerComponent_AppliesTheTintTransform()
    {
        // Transform: tint 0 → white, tint 1 → blue. The single sample bit is 1.
        using var bmp = PixelAssert.Render(SeparationImagePdf(bpc: 1, hex: "80"));
        var c = PixelAssert.At(bmp, 50, 50);

        Assert.True(c.Blue > 200 && c.Red < 60,
            $"tint 1 must transform to blue; got {c} (decoded as bilevel grey)");
    }

    /// <summary>The other end of the same transform, so neither result can be a constant.</summary>
    [Fact]
    public void SeparationImage_AtOneBitPerComponent_ZeroTintIsTheOtherEnd()
    {
        using var bmp = PixelAssert.Render(SeparationImagePdf(bpc: 1, hex: "00"));
        var c = PixelAssert.At(bmp, 50, 50);

        Assert.True(c.Blue > 200 && c.Red > 200, $"tint 0 must transform to white; got {c}");
    }

    /// <summary>
    /// §8.6.5.3: a CalRGB image applies <c>/Gamma</c> and <c>/Matrix</c>. Same gate, same silence —
    /// here at 4 bpc.
    /// </summary>
    [Fact]
    public void CalRgbImage_AtFourBitsPerComponent_AppliesGamma()
    {
        // Every channel is raw 8 of 15 (≈0.533) under /Gamma [2.2 2.2 2.2]: ≈0.256 linear, which
        // the sRGB transfer function encodes back to ≈137. Read as plain DeviceRGB it is 8*17=136…
        // so the discriminating channel is the RED one, whose gamma here is 1.0 (linear 0.533 →
        // sRGB ≈192) while the other two are 2.2.
        using var bmp = PixelAssert.Render(CalRgbImagePdf(bpc: 4, hex: "888"));
        var c = PixelAssert.At(bmp, 50, 50);

        Assert.True(c.Red > c.Green + 30,
            $"the per-channel /Gamma was not applied at 4 bpc: {c} (all three channels came out equal)");
    }

    // ====================================================================
    //  Fixtures
    // ====================================================================

    private const string LabParams = "<< /WhitePoint [0.9505 1 1.089] /Range [-100 100 -100 100] >>";

    private static PdfDocument OpenFixture(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    /// <summary>The first (and only) image XObject of a fixture's first page.</summary>
    private static PdfDictionary ImageXObject(PdfDocument doc)
    {
        var res = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Resources]) as PdfDictionary;
        var xo  = doc.ResolveObject(res[PdfName.XObject]) as PdfDictionary;
        foreach (var key in xo.Keys)
            if (doc.ResolveObject(xo[key]) is PdfDictionary d) return d;
        throw new InvalidOperationException("fixture has no image XObject");
    }

    private static byte[] SimplePage(string content) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << >> >>",
            Stream("", content));

    /// <summary>
    /// A page painting the same <c>/Separation</c> colour twice — once through <c>sc</c> and once
    /// as a 1x1 image at full tint. The tint transform emits ONE value into a declared
    /// <c>/DeviceRGB</c> alternate, which is the arity mismatch the two converters disagreed on.
    /// </summary>
    private static byte[] SeparationFillAndImagePdf() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Contents 4 0 R" +
            " /Resources << /ColorSpace << /CS0 5 0 R >> /XObject << /Im1 6 0 R >> >> >>",
            Stream("", "/CS0 cs 1 sc 0 100 100 100 re f  q 100 0 0 100 0 0 cm /Im1 Do Q"),
            "[ /Separation /Spot /DeviceRGB 7 0 R ]",
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace 5 0 R" +
                   " /BitsPerComponent 8 /Filter /ASCIIHexDecode", "FF>"),
            "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [0.5] /N 1 >>");

    /// <summary>An image in a six-ink <c>/DeviceN</c> whose colorant-name array is indirect.</summary>
    private static byte[] DeviceNIndirectNamesPdf() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 100 0 0 100 0 0 cm /Im1 Do Q"),
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1" +
                   " /ColorSpace [/DeviceN 6 0 R /DeviceRGB 7 0 R] /BitsPerComponent 8" +
                   " /Filter /ASCIIHexDecode", "FF000000FF00>"),
            "[ /C /M /Y /K /S1 /S2 ]",
            "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0] /C1 [1 1 1] /N 1 >>");

    /// <summary>
    /// An uncoloured (PaintType 2) tiling pattern whose Pattern space declares an ARRAY underlying
    /// space — <c>[/Pattern [/Lab …]]</c>, legal under §8.7.3.1.
    /// </summary>
    private static byte[] UncolouredPatternOverLabPdf() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /Pattern << /P1 5 0 R >>" +
            $" /ColorSpace << /CS0 [/Pattern [/Lab {LabParams}]] >> >> >>",
            Stream("", "/CS0 cs 50 60 -60 /P1 scn 0 0 100 100 re f"),
            Stream("/PatternType 1 /PaintType 2 /TilingType 1 /BBox [0 0 10 10]" +
                   " /XStep 10 /YStep 10 /Resources << >>", "0 0 10 10 re f"));

    /// <summary>
    /// A luminosity soft mask opaque over the left half of a 400x400 page, with one fill inside a
    /// <c>q…Q</c> block (upper band) and one after it (lower band).
    /// </summary>
    private static byte[] SoftMaskAcrossQPdf() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Contents 4 0 R" +
            " /Resources << /ExtGState << /GS0 6 0 R >> >> >>",
            Stream("", "0 g /GS0 gs  q 0 200 400 200 re f Q  0 0 400 200 re f"),
            MaskGroup("0 0 200 400 re f"),
            "<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G 5 0 R /BC [0] >> >>");

    /// <summary>Outer mask over the left half, inner mask over the top half.</summary>
    private static byte[] NestedSoftMaskPdf() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Contents 4 0 R" +
            " /Resources << /ExtGState << /GS0 6 0 R /GS1 8 0 R >> >> >>",
            Stream("", "/GS0 gs 1 0 0 rg  q /GS1 gs 0 0 400 400 re f Q" +
                       "  0 0 1 rg 0 0 400 400 re f"),
            MaskGroup("0 0 200 400 re f"),                                  // 5: left half
            "<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G 5 0 R /BC [0] >> >>",
            MaskGroup("0 200 400 200 re f"),                                // 7: top half
            "<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G 7 0 R /BC [0] >> >>");

    /// <summary>A DeviceGray transparency group painting <paramref name="whiteArea"/> white.</summary>
    private static string MaskGroup(string whiteArea) =>
        Stream("/Type /XObject /Subtype /Form /BBox [0 0 400 400]" +
               " /Group << /Type /Group /S /Transparency /CS /DeviceGray >> /Resources << >>",
               "1 g " + whiteArea);

    /// <summary>A page filled with the initial colour of a Separation space and nothing else.</summary>
    private static byte[] SeparationInitialColourPdf() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /ColorSpace << /CS0 5 0 R >> >> >>",
            Stream("", "/CS0 cs 0 0 100 100 re f"),
            "[ /Separation /Spot /DeviceRGB" +
            " << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [0 0 1] /N 1 >> ]");

    /// <summary>A page filled with the initial colour of an Indexed space and nothing else.</summary>
    private static byte[] IndexedInitialColourPdf() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /ColorSpace << /CS0 5 0 R >> >> >>",
            Stream("", "1 1 1 rg /CS0 cs 0 0 100 100 re f"),
            "[ /Indexed /DeviceRGB 1 <FF000000FF00> ]");

    private static byte[] SelectedPatternPdf(bool reenterPatternSpace = false) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /Pattern << /P1 5 0 R >> >> >>",
            Stream("", "/Pattern cs /P1 scn "
                       + (reenterPatternSpace ? "/Pattern cs " : "")
                       + "0 0 100 100 re f"),
            Stream("/PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10]" +
                   " /XStep 10 /YStep 10 /Resources << >>", "0 g 0 0 10 10 re f"));

    /// <summary>
    /// An 8x8 stencil mask (all bits 0 → paint everywhere) with <paramref name="padBytes"/>
    /// trailing bytes, drawn in red over the lower-left quarter of the page.
    /// </summary>
    private static byte[] ImageMaskPdf(int padBytes, bool declareBpc)
    {
        var hex = new StringBuilder();
        for (int i = 0; i < 8 + padBytes; i++) hex.Append("00");
        hex.Append('>');

        string bpc = declareBpc ? " /BitsPerComponent 1" : "";
        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "1 0 0 rg q 50 0 0 50 0 50 cm /Im1 Do Q"),
            Stream($"/Type /XObject /Subtype /Image /Width 8 /Height 8 /ImageMask true{bpc}" +
                   " /Filter /ASCIIHexDecode", hex.ToString()));
    }

    private static byte[] CalGrayImagePdf(int bpc, string hex) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 100 0 0 100 0 0 cm /Im1 Do Q"),
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1" +
                   " /ColorSpace [/CalGray << /WhitePoint [0.9505 1 1.089] /Gamma 2.2 >>]" +
                   $" /BitsPerComponent {bpc} /Filter /ASCIIHexDecode", hex + ">"));

    /// <summary>A 3x2 CalGray image at 4 bpc filling a 90x60 page — one 30x30 cell per sample.</summary>
    private static byte[] CalGrayGridPdf(string hex) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 90 60] /Contents 4 0 R" +
            " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 90 0 0 60 0 0 cm /Im1 Do Q"),
            Stream("/Type /XObject /Subtype /Image /Width 3 /Height 2" +
                   " /ColorSpace [/CalGray << /WhitePoint [0.9505 1 1.089] /Gamma 2.2 >>]" +
                   " /BitsPerComponent 4 /Filter /ASCIIHexDecode", hex + ">"));

    private static byte[] CalRgbImagePdf(int bpc, string hex) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 100 0 0 100 0 0 cm /Im1 Do Q"),
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1" +
                   " /ColorSpace [/CalRGB << /WhitePoint [0.9505 1 1.089] /Gamma [1 2.2 2.2] >>]" +
                   $" /BitsPerComponent {bpc} /Filter /ASCIIHexDecode", hex + ">"));

    private static byte[] SeparationImagePdf(int bpc, string hex) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 100 0 0 100 0 0 cm /Im1 Do Q"),
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1" +
                   " /ColorSpace [/Separation /Spot /DeviceRGB" +
                   " << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [0 0 1] /N 1 >>]" +
                   $" /BitsPerComponent {bpc} /Filter /ASCIIHexDecode", hex + ">"));

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
