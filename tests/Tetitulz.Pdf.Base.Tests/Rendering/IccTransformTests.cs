// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// ICC colour management for <b>vector</b> content and for the profiles
/// <c>SKColorSpace.CreateIcc</c> declines.
///
/// <para><b>The gap these close.</b> <c>PdfColor.ResolveSpace</c> collapses <c>[/ICCBased s]</c> to
/// the device space its <c>/N</c> stands for, so a fill or stroke never consulted the profile at
/// all; and on the image path the shipped provider was <c>SKColorSpace.CreateIcc</c> only, which
/// returns false for grey and CMYK profiles — a silent no-op for exactly the profiles that differ
/// most from the device fallback.</para>
///
/// <para><b>Every profile here is built byte by byte in this file.</b> A matrix/TRC profile is
/// about 300 bytes and entirely constructible, so nothing depends on a file on disk (and no test
/// can silently skip itself — banned pattern 2). Building them also means the adversarial cases —
/// a truncated profile, a tag offset past the end, an absurd curve point count — are exact rather
/// than approximated by a random byte string.</para>
///
/// <para><b>What makes each assertion falsifiable.</b> The device fallback and the ICC result are
/// deliberately far apart: a linear (gamma 1.0) tone curve maps 0.5 to sRGB <b>188</b>, where
/// DeviceGray paints <b>128</b>; a profile whose primaries are permuted paints blue where the
/// device space paints red. Asserting "some colour came out" would pass with the whole transform
/// deleted.</para>
/// </summary>
[Collection(IccTransformTests.SerialCollection)]
public sealed class IccTransformTests
{
    /// <summary>
    /// <c>IccTransform</c> is a <b>process-wide singleton</b>, exactly like <c>PdfPluginHost</c>,
    /// and <see cref="RenderingParityPhaseTests"/> registers and resets a provider on it. xUnit
    /// runs test classes in parallel across collections, so a render here can observe that class's
    /// stub provider — which converts nothing — and fall back to the device space mid-test.
    ///
    /// <para><b>This is not hypothetical, and it is not rare.</b> Run the two classes together
    /// without the <c>[Collection]</c> attribute above and 5–7 of these tests fail on <i>every</i>
    /// run; with it, none do. It is the same "passes alone, fails in the suite" shape CLAUDE.md
    /// documents for the plugin host.</para>
    ///
    /// <para>The string below is the collection xUnit implicitly puts a class carrying no
    /// <c>[Collection]</c> attribute into, so declaring a matching
    /// <see cref="IccTransformSerialCollection"/> pulls that class into the same serialised
    /// collection without editing it. The class name comes from <c>nameof</c> so renaming it is a
    /// compile error here rather than a silently re-introduced race. <b>Any new test class that
    /// touches <c>IccTransform</c> must join this collection too.</b></para>
    /// </summary>
    public const string SerialCollection =
        "Test collection for Tetitulz.Pdf.Base.Tests." + nameof(RenderingParityPhaseTests);

    // sRGB's primaries as a real sRGB profile stores them: chromatically adapted to the D50 PCS.
    // Their sum is the D50 white, which is what makes `1 1 1` come out white.
    private static readonly double[] SrgbRed   = { 0.4360, 0.2225, 0.0139 };
    private static readonly double[] SrgbGreen = { 0.3851, 0.7169, 0.0971 };
    private static readonly double[] SrgbBlue  = { 0.1431, 0.0606, 0.7141 };

    /// <summary>sRGB encoding of linear 0.5 — what a gamma-1.0 tone curve turns 0.5 into.</summary>
    private const int LinearHalfEncoded = 188;

    /// <summary>What DeviceGray paints for 0.5, and therefore the value the ICC path must not give.</summary>
    private const int DeviceHalf = 128;

    // ====================================================================
    //  Vector — the path that consulted no profile at all
    // ====================================================================

    /// <summary>
    /// A grey ICC profile with a linear tone curve says 0.5 is half the luminance of white, which
    /// encodes to sRGB 188. DeviceGray — the fallback, and previously the only behaviour — paints
    /// 128. The gap between them is the whole test.
    /// </summary>
    [Fact]
    public void IccBasedGrayFill_LinearToneCurve_PaintsProfileLuminanceNotDeviceGrey()
    {
        using var bmp = PixelAssert.Render(FillPdf(GrayProfile(CurvGamma(1.0)), n: 1, operands: "0.5"));

        var c = PixelAssert.At(bmp, 50, 50);
        Assert.InRange(c.Red, LinearHalfEncoded - 4, LinearHalfEncoded + 4);
        Assert.Equal(c.Red, c.Green);
        Assert.Equal(c.Red, c.Blue);
    }

    /// <summary>
    /// The control for the test above: the same 0.5, painted in DeviceGray, is 128. Without this
    /// the assertion above could be read as a claim about the fixture rather than the profile.
    /// </summary>
    [Fact]
    public void DeviceGrayFill_WithNoIccProfile_StillPaintsTheEncodedValue()
    {
        using var bmp = PixelAssert.Render(DeviceFillPdf("0.5 g"));

        Assert.InRange(PixelAssert.At(bmp, 50, 50).Red, DeviceHalf - 2, DeviceHalf + 2);
    }

    /// <summary><c>CS</c>/<c>SC</c> route through the same converter as <c>cs</c>/<c>sc</c>.</summary>
    [Fact]
    public void IccBasedGrayStroke_LinearToneCurve_PaintsProfileLuminance()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /ColorSpace << /CS0 5 0 R >> >> >>",
            Stream("", "/CS0 CS 0.5 SC 40 w 0 50 m 100 50 l S"),
            "[ /ICCBased 6 0 R ]",
            IccStreamObject(GrayProfile(CurvGamma(1.0)), n: 1));

        using var bmp = PixelAssert.Render(pdf);

        Assert.InRange(PixelAssert.At(bmp, 50, 50).Red, LinearHalfEncoded - 4, LinearHalfEncoded + 4);
    }

    /// <summary>
    /// The public provider transforms a bitmap in the colour type it has. Skia was told every
    /// bitmap was RGBA, so a BGRA one ran the profile over red and blue swapped. The profile here
    /// takes device red to sRGB's green primary — not a red/blue swap, which would cancel the
    /// reinterpretation out — so red pixels come out green in either layout.
    /// </summary>
    [Theory]
    [InlineData(SKColorType.Rgba8888)]
    [InlineData(SKColorType.Bgra8888)]
    public void TheBitmapTransformReadsTheBitmapsOwnChannelOrder(SKColorType colorType)
    {
        byte[] profile = RgbProfile(CurvGamma(1.0), SrgbGreen, SrgbBlue, SrgbRed);
        using var bitmap = new SKBitmap(4, 4, colorType, SKAlphaType.Opaque);
        bitmap.Erase(new SKColor(255, 0, 0));

        Assert.True(new SkiaIccTransformProvider().TransformToSRgb(profile, bitmap));

        SKColor c = bitmap.GetPixel(1, 1);
        Assert.True(c.Green > 200 && c.Red < 60 && c.Blue < 60, $"expected green, got {c}");
    }

    /// <summary>
    /// The grey bitmap path maps the colour and keeps the alpha: a half-transparent mid grey through
    /// a linear tone curve becomes 188 and stays at alpha 128.
    /// </summary>
    [Fact]
    public void TheGreyBitmapPathKeepsEachPixelsAlpha()
    {
        using var bitmap = new SKBitmap(4, 4, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bitmap.Erase(new SKColor(128, 128, 128, 128));

        Assert.True(new SkiaIccTransformProvider().TransformToSRgb(GrayProfile(CurvGamma(1.0)), bitmap));

        SKColor c = bitmap.GetPixel(1, 1);
        Assert.Equal(128, c.Alpha);
        Assert.InRange(c.Red, LinearHalfEncoded - 4, LinearHalfEncoded + 4);
    }

    /// <summary>
    /// A colour the managed evaluator cannot take (no usable profile) falls back by component count:
    /// four components are CMYK through <c>PdfColor.CmykToRgb</c> — 0 1 1 0 is red, not black.
    /// </summary>
    [Fact]
    public void AnUnusableProfileFallsBackToCmykForFourComponents()
    {
        SKColor c = new SkiaIccTransformProvider().TransformToSRgb(new byte[0], new byte[] { 0, 255, 255, 0 });

        Assert.Equal(PdfColor.CmykToRgb(0f, 1f, 1f, 0f), c);
        Assert.True(c.Red > 200 && c.Green < 60, $"expected red, got {c}");
    }

    /// <summary>
    /// A three-component matrix/TRC profile with sRGB's primaries and a linear tone curve.
    /// <c>1 0.5 0</c> is linear-light, so the green channel encodes to 188 — the red and blue
    /// endpoints are identical to the device fallback, and the middle component is what separates
    /// "the profile was applied" from "the operands were copied to RGB".
    /// </summary>
    [Fact]
    public void IccBasedRgbFill_LinearToneCurve_GammaEncodesThroughTheProfile()
    {
        byte[] profile = RgbProfile(CurvGamma(1.0), SrgbRed, SrgbGreen, SrgbBlue);

        using var bmp = PixelAssert.Render(FillPdf(profile, n: 3, operands: "1 0.5 0"));

        var c = PixelAssert.At(bmp, 50, 50);
        Assert.InRange(c.Red, 248, 255);
        Assert.InRange(c.Green, LinearHalfEncoded - 8, LinearHalfEncoded + 8);
        Assert.InRange(c.Blue, 0, 10);
    }

    /// <summary>
    /// The matrix, not just the curves: the same profile with its red and blue columns swapped
    /// paints <c>1 0 0</c> BLUE. No component-copying fallback can produce that, so this cannot
    /// pass with <see cref="IccProfile"/>'s matrix multiply removed.
    /// </summary>
    [Fact]
    public void IccBasedRgbFill_PermutedPrimaries_PaintsWhatTheMatrixSays()
    {
        byte[] profile = RgbProfile(CurvGamma(1.0), SrgbBlue, SrgbGreen, SrgbRed);

        using var bmp = PixelAssert.Render(FillPdf(profile, n: 3, operands: "1 0 0"));

        var c = PixelAssert.At(bmp, 50, 50);
        Assert.InRange(c.Blue, 248, 255);
        Assert.InRange(c.Red, 0, 10);
        Assert.InRange(c.Green, 0, 10);
    }

    /// <summary>
    /// A <c>parametricCurveType</c> tone curve reaches the pixels too. Type 0 with g = 0.5 maps
    /// 0.25 to linear 0.5, which encodes to 188; DeviceGray would paint 0.25 as 64.
    /// </summary>
    [Fact]
    public void IccBasedGrayFill_ParametricToneCurve_UsesTheParametricFunction()
    {
        byte[] profile = GrayProfile(ParaCurve(0, new[] { 0.5 }));

        using var bmp = PixelAssert.Render(FillPdf(profile, n: 1, operands: "0.25"));

        Assert.InRange(PixelAssert.At(bmp, 50, 50).Red,
            LinearHalfEncoded - 4, LinearHalfEncoded + 4);
    }

    // ====================================================================
    //  Detected, reported, and NOT transformed
    // ====================================================================

    /// <summary>
    /// A CMYK profile whose <c>A2B0</c> cannot be read keeps the exact device-CMYK fallback it had
    /// <b>and</b> tells the caller — through the diagnostics sink and the <c>[ThreadStatic]</c>
    /// reason slot — that a profile was present and skipped. Those two are indistinguishable at
    /// the pixel level, which is the reason the slot exists.
    ///
    /// <para><b>Renamed, because half of what it used to claim is no longer true.</b> It was
    /// <c>IccBasedCmykFill_IsReportedAndKeepsTheDeviceFallback</c>, and its premise was that a
    /// CMYK profile is lut-based and therefore beyond this evaluator. The lut is now read
    /// (<c>IccLutProfileTests</c>), so what survives is the narrower and still-important claim:
    /// when the lut is <i>unreadable</i>, the fallback is exact and the reason names the tag. The
    /// fixture's <c>A2B0</c> is a 12-byte stub, which is why this file still reaches that
    /// path.</para>
    /// </summary>
    [Fact]
    public void IccBasedCmykFill_WithAnUnreadableLut_IsReportedAndKeepsTheDeviceFallback()
    {
        var reported = new List<string>();
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (kind == RenderDiagnosticKind.IccTransformFailed) reported.Add(detail ?? "");
        }

        RenderDiagnostics.OnEvent += Sink;
        try
        {
            // 0 1 1 0 in DeviceCMYK is red — the naive subtractive fallback, unchanged.
            using var bmp = PixelAssert.Render(FillPdf(CmykProfile(), n: 4, operands: "0 1 1 0"));

            var c = PixelAssert.At(bmp, 50, 50);
            Assert.InRange(c.Red, 248, 255);
            Assert.InRange(c.Green, 0, 8);
            Assert.InRange(c.Blue, 0, 8);
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        Assert.Contains(reported, d => d.Contains("A2B0"));
        Assert.Contains("A2B0", IccTransform.LastUnsupportedProfile ?? "");
    }

    /// <summary>
    /// An <c>[/ICCBased …]</c> used as an <c>/Indexed</c> <b>base</b> must consult the profile too —
    /// the palette entries are components in that space, not in the device space its <c>/N</c>
    /// happens to stand for.
    /// </summary>
    /// <remarks>
    /// The routing added for direct fills deliberately stopped at `sc`/`scn`, leaving the profile
    /// ignored wherever `[/ICCBased …]` appears *inside* another space. That is the same
    /// one-path-done-siblings-not asymmetry this codebase keeps re-creating, just inside a single
    /// renderer rather than between two. The permuted-primaries profile makes it unmissable: palette
    /// entry <c>FF 00 00</c> is BLUE through the profile and red through the device fallback.
    /// </remarks>
    [Fact]
    public void IccBasedAsIndexedBase_ConsultsTheProfile()
    {
        byte[] profile = RgbProfile(CurvGamma(1.0), SrgbBlue, SrgbGreen, SrgbRed);

        using var bmp = PixelAssert.Render(Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /ColorSpace << /CS0 5 0 R >> >> >>",
            Stream("", "/CS0 cs 0 sc 0 0 100 100 re f"),
            "[ /Indexed [/ICCBased 6 0 R] 1 <FF000000FF00> ]",
            IccStreamObject(profile, 3)));

        var c = PixelAssert.At(bmp, 50, 50);
        Assert.InRange(c.Blue, 248, 255);
        Assert.InRange(c.Red, 0, 10);
    }

    /// <summary>
    /// ...and the SVG export must reach the same palette entry the same way.
    /// </summary>
    /// <remarks>
    /// The two renderers keep <b>separate</b> palette builders on purpose
    /// (<c>BuildIndexedPalette</c> and <c>BuildSvgIndexedPalette</c>, one entry point each), which
    /// is precisely why an Indexed fix has to land twice. Four implementations of this one palette
    /// existed before they were consolidated to two; consolidating did not remove the obligation to
    /// change both.
    /// </remarks>
    [Fact]
    public void IccBasedAsIndexedBase_SvgMatchesTheRaster()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /ColorSpace << /CS0 5 0 R >> >> >>",
            Stream("", "/CS0 cs 0 sc 0 0 100 100 re f"),
            "[ /Indexed [/ICCBased 6 0 R] 1 <FF000000FF00> ]",
            IccStreamObject(RgbProfile(CurvGamma(1.0), SrgbBlue, SrgbGreen, SrgbRed), 3));

        string svg;
        using (var ms = new System.IO.MemoryStream(pdf)) svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        Assert.Contains("rgb(0,0,255)", svg.Replace(" ", ""));
        Assert.DoesNotContain("rgb(255,0,0)", svg.Replace(" ", ""));
    }

    /// <summary>
    /// A <c>/Separation</c> whose <c>alternateSpace</c> is <c>[/ICCBased …]</c> must run its tint
    /// transform's output through the profile — the transform produces components in that space,
    /// not in the device space its <c>/N</c> stands for.
    /// </summary>
    /// <remarks>
    /// Every nested site collapsed the alternate to a device name via <c>ResolveAlternateCsName</c>
    /// <i>before</i> any colour was computed, which threw the profile away. A tint of 1 maps to
    /// <c>1 0 0</c> here, so the permuted-primaries profile paints it blue and the device fallback
    /// paints it red — a difference no rounding can produce.
    /// </remarks>
    [Fact]
    public void SeparationWithIccAlternate_RunsTheTintOutputThroughTheProfile()
    {
        using var bmp = PixelAssert.Render(SeparationIccPdf());

        var c = PixelAssert.At(bmp, 50, 50);
        Assert.InRange(c.Blue, 248, 255);
        Assert.InRange(c.Red, 0, 10);
    }

    /// <summary>...and the SVG export must agree, since it owns a separate tint-transform site.</summary>
    [Fact]
    public void SeparationWithIccAlternate_SvgMatchesTheRaster()
    {
        string svg;
        using (var ms = new System.IO.MemoryStream(SeparationIccPdf()))
            svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        Assert.Contains("rgb(0,0,255)", svg.Replace(" ", ""));
        Assert.DoesNotContain("rgb(255,0,0)", svg.Replace(" ", ""));
    }

    /// <summary>
    /// <c>[/Separation /Spot [/ICCBased …] tint]</c> filled at tint 1. The tint transform is a
    /// Type 2 exponential mapping 0→black and 1→<c>1 0 0</c>.
    /// </summary>
    private static byte[] SeparationIccPdf() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /ColorSpace << /CS0 5 0 R >> >> >>",
            Stream("", "/CS0 cs 1 sc 0 0 100 100 re f"),
            "[ /Separation /Spot [/ICCBased 6 0 R] 7 0 R ]",
            IccStreamObject(RgbProfile(CurvGamma(1.0), SrgbBlue, SrgbGreen, SrgbRed), 3),
            "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0] /C1 [1 0 0] /N 1 >>");

    /// <summary>
    /// A shading whose <c>/ColorSpace</c> is <c>[/ICCBased …]</c> must consult the profile for its
    /// stops. This is a third code path for the same concept — <c>ShadingSpace</c> keeps only a
    /// device family name, so the space object had to be threaded through it to reach a profile.
    /// </summary>
    [Fact]
    public void IccBasedShadingSpace_ConsultsTheProfile()
    {
        using var bmp = PixelAssert.Render(IccShadingPdf());

        // Both stops are 1 0 0, so the whole band is the profile's answer: blue, not red.
        var c = PixelAssert.At(bmp, 50, 50);
        Assert.InRange(c.Blue, 248, 255);
        Assert.InRange(c.Red, 0, 10);
    }

    /// <summary>...and the SVG export emits the same stop colour.</summary>
    [Fact]
    public void IccBasedShadingSpace_SvgMatchesTheRaster()
    {
        string svg;
        using (var ms = new System.IO.MemoryStream(IccShadingPdf()))
            svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        Assert.Contains("rgb(0,0,255)", svg.Replace(" ", ""));
        Assert.DoesNotContain("rgb(255,0,0)", svg.Replace(" ", ""));
    }

    /// <summary>
    /// An axial shading in an <c>[/ICCBased …]</c> space, both endpoints <c>1 0 0</c> so the result
    /// is one flat colour and the assertion does not depend on where the gradient is sampled.
    /// </summary>
    private static byte[] IccShadingPdf() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /Shading << /Sh0 5 0 R >> >> >>",
            Stream("", "q 0 0 100 100 re W n /Sh0 sh Q"),
            "<< /ShadingType 2 /ColorSpace [/ICCBased 6 0 R] /Coords [0 0 100 0]" +
            " /Extend [true true] /Function 7 0 R >>",
            IccStreamObject(RgbProfile(CurvGamma(1.0), SrgbBlue, SrgbGreen, SrgbRed), 3),
            "<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [1 0 0] /N 1 >>");

    // ============================================================ SVG / raster parity

    /// <summary>
    /// The SVG renderer must consult the profile for a <b>vector</b> fill too.
    /// </summary>
    /// <remarks>
    /// A feature live in the raster path and invisible in SVG export is this codebase's documented
    /// recurring defect (JPEG 2000, <c>/I</c> isolation, PostScript XObjects), and ICC arrived the
    /// same way — routed in <c>ContentStreamInterpreter.OpSc</c> only. The permuted-primaries
    /// profile is the fixture that cannot be faked: it paints <c>1 0 0</c> BLUE, so a renderer that
    /// falls through to the device space its <c>/N 3</c> stands for emits red and fails here.
    /// </remarks>
    [Fact]
    public void IccBasedRgbFill_SvgConsultsTheProfileLikeTheRasterPath()
    {
        byte[] pdf = FillPdf(RgbProfile(CurvGamma(1.0), SrgbBlue, SrgbGreen, SrgbRed),
                             n: 3, operands: "1 0 0");

        string svg;
        using (var ms = new System.IO.MemoryStream(pdf)) svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        // rgb(0,0,255) — what the matrix says. The device fallback would emit rgb(255,0,0).
        Assert.Contains("rgb(0,0,255)", svg.Replace(" ", ""));
        Assert.DoesNotContain("rgb(255,0,0)", svg.Replace(" ", ""));
    }

    /// <summary>
    /// ...and the two renderers must also <b>decline</b> together. A profile one path handles and
    /// the other does not is the same asymmetry wearing the opposite sign.
    /// </summary>
    /// <remarks>
    /// <b>A control, not a discriminating test.</b> It asserts the device fallback, which is exactly
    /// what disabling the SVG routing produces — so it stays green under that mutation, and that is
    /// the point: the fallback must survive the fix intact. The discriminating half of the pair is
    /// <see cref="IccBasedRgbFill_SvgConsultsTheProfileLikeTheRasterPath"/>, which does fail.
    /// </remarks>
    [Fact]
    public void CmykProfile_BothRenderersFallBackToTheDeviceSpace()
    {
        byte[] pdf = FillPdf(CmykProfile(), n: 4, operands: "0 1 1 0");

        string svg;
        using (var ms = new System.IO.MemoryStream(pdf)) svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        using var bmp = PixelAssert.Render(pdf);

        // 0 1 1 0 subtractive is red in both — the shared, reported fallback.
        Assert.Contains("rgb(255,0,0)", svg.Replace(" ", ""));
        Assert.InRange(PixelAssert.At(bmp, 50, 50).Red, 248, 255);
    }

    /// <summary>
    /// §8.6.5.5 requires <c>/N</c> to match the profile's data colour space. When it does not, the
    /// honest answer is the device space <c>/N</c> names plus a report — not a guess about which of
    /// the two numbers is right.
    /// </summary>
    [Fact]
    public void IccBasedFill_ComponentCountDisagreesWithProfile_KeepsTheDeviceFallback()
    {
        // /N 3 over a one-component GRAY profile.
        using var bmp = PixelAssert.Render(FillPdf(GrayProfile(CurvGamma(1.0)), n: 3, operands: "1 0.5 0"));

        var c = PixelAssert.At(bmp, 50, 50);
        Assert.InRange(c.Green, DeviceHalf - 2, DeviceHalf + 2);   // DeviceRGB, not 188
        Assert.InRange(c.Red, 248, 255);
        Assert.Contains("component-count mismatch", IccTransform.LastUnsupportedProfile ?? "");
    }

    /// <summary>
    /// A page with no ICC profile anywhere must render byte for byte as it did. <c>1 0.5 0 rg</c>
    /// is DeviceRGB, so the middle component stays 128 — if the ICC work had leaked into the
    /// device path it would read 188 here.
    /// </summary>
    [Fact]
    public void DeviceRgbFill_WithNoIccProfile_IsUnchanged()
    {
        using var bmp = PixelAssert.Render(DeviceFillPdf("1 0.5 0 rg"));

        var c = PixelAssert.At(bmp, 50, 50);
        Assert.InRange(c.Red, 253, 255);
        Assert.InRange(c.Green, DeviceHalf - 2, DeviceHalf + 2);
        Assert.InRange(c.Blue, 0, 2);
    }

    // ====================================================================
    //  The host hook
    // ====================================================================

    /// <summary>
    /// A provider written before <see cref="IIccColorTransformProvider"/> existed cannot convert
    /// components, and a host that installed one must keep the vector behaviour it had: the device
    /// space, not this assembly's evaluator quietly overriding its provider.
    /// </summary>
    [Fact]
    public void IccBasedFill_ProviderWithoutComponentSupport_KeepsTheDeviceFallback()
    {
        IccTransform.Register(new BitmapOnlyProvider());
        try
        {
            using var bmp = PixelAssert.Render(FillPdf(GrayProfile(CurvGamma(1.0)), n: 1, operands: "0.5"));

            Assert.InRange(PixelAssert.At(bmp, 50, 50).Red, DeviceHalf - 2, DeviceHalf + 2);
        }
        finally { IccTransform.ResetToDefault(); }
    }

    /// <summary>
    /// ...and a provider that <i>does</i> implement it is the one asked. The stub answers with a
    /// colour the profile could never produce, so a fill painted in it proves the operands went
    /// through the registered provider rather than through the built-in evaluator.
    /// </summary>
    [Fact]
    public void IccBasedFill_CustomComponentProvider_IsAskedForTheColour()
    {
        var provider = new FixedColorProvider(new SKColor(0, 200, 100));
        IccTransform.Register(provider);
        try
        {
            using var bmp = PixelAssert.Render(FillPdf(GrayProfile(CurvGamma(1.0)), n: 1, operands: "0.5"));

            var c = PixelAssert.At(bmp, 50, 50);
            Assert.InRange(c.Red, 0, 6);
            Assert.InRange(c.Green, 196, 204);
            Assert.InRange(c.Blue, 96, 104);
        }
        finally { IccTransform.ResetToDefault(); }

        Assert.True(provider.Calls > 0, "the registered component provider was never consulted");
    }

    /// <summary>
    /// A provider implementing <see cref="IIccIntentColorTransformProvider"/> receives the intent the
    /// fill was set under (<c>/Saturation ri</c>) and black-point compensation on, and its colour is
    /// the one painted. Until 2026-10-17 no host provider could receive the intent.
    /// </summary>
    [Fact]
    public void IccBasedFill_IntentAwareProvider_ReceivesTheFillsIntent()
    {
        var provider = new IntentRecordingProvider(new SKColor(10, 220, 30));
        IccTransform.Register(provider);
        try
        {
            using var bmp = PixelAssert.Render(FillPdf(GrayProfile(CurvGamma(1.0)), n: 1, operands: "0.5", prefix: "/Saturation ri "));
            var c = PixelAssert.At(bmp, 50, 50);
            Assert.InRange(c.Green, 216, 224);
        }
        finally { IccTransform.ResetToDefault(); }

        Assert.Equal(PdfRenderingIntent.Saturation, provider.LastColourIntent);
        Assert.True(provider.LastColourCompensation);
    }

    /// <summary>An image's own <c>/Intent</c> reaches the intent-aware provider's bitmap transform.</summary>
    [Fact]
    public void IccBasedImage_IntentAwareProvider_ReceivesTheImagesIntent()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 100 0 0 100 0 0 cm /Im1 Do Q"),
            Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 /Intent /Perceptual" +
                   " /ColorSpace [/ICCBased 6 0 R] /Filter /ASCIIHexDecode", "80FF>"),
            IccStreamObject(GrayProfile(CurvGamma(1.0)), n: 1));

        var provider = new IntentRecordingProvider(new SKColor(0, 0, 0));
        IccTransform.Register(provider);
        try { using var bmp = PixelAssert.Render(pdf); }
        finally { IccTransform.ResetToDefault(); }

        Assert.Equal(PdfRenderingIntent.Perceptual, provider.LastImageIntent);
        Assert.True(provider.ImageCalls > 0, "the bitmap transform was never asked");
    }

    /// <summary>
    /// A JPEG 2000 image whose JP2 header carries its own ICC profile is transformed by the decoder,
    /// not the interpreter — and an intent-aware provider receives the image's <c>/Intent</c> there too.
    /// </summary>
    [Fact]
    public void JpxImageWithAnIccColr_IntentAwareProvider_ReceivesTheImagesIntent()
    {
        var rgb = JpxLosslessFixtures.Rgb40x24_Mct_TwoLevels;
        byte[] jp2 = JpxTestPdf.Jp2(rgb, Jp2Headers.ColrIcc(Jp2Headers.IccProfile("RGB "), precedence: 0));
        byte[] pdf = JpxTestPdf.ImagePdf(jp2, rgb.Width, rgb.Height, "/Intent /Saturation");

        var provider = new IntentRecordingProvider(new SKColor(0, 0, 0));
        IccTransform.Register(provider);
        try { using var bmp = JpxTestPdf.Render(pdf); }
        finally { IccTransform.ResetToDefault(); }

        Assert.True(provider.ImageCalls > 0, "the decoder never asked the provider to transform the bitmap");
        Assert.Equal(PdfRenderingIntent.Saturation, provider.LastImageIntent);
    }

    /// <summary>
    /// The image path tells a provider without the intent-aware interface that an image's own
    /// non-default <c>/Intent</c> was not forwarded; an image naming none says nothing.
    /// </summary>
    [Fact]
    public void IccBasedImage_ComponentOnlyProvider_IsToldTheIntentWasNotForwarded()
    {
        static byte[] ImagePdf(string intent) => Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 100 0 0 100 0 0 cm /Im1 Do Q"),
            Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8" + intent +
                   " /ColorSpace [/ICCBased 6 0 R] /Filter /ASCIIHexDecode", "80FF>"),
            IccStreamObject(GrayProfile(CurvGamma(1.0)), n: 1));
        static bool NotForwarded((RenderDiagnosticKind Kind, string Detail) e) => e.Kind == RenderDiagnosticKind.RenderParameterIgnored
            && e.Detail.Contains("IIccIntentColorTransformProvider", StringComparison.Ordinal);
        try
        {
            IccTransform.Register(new FixedColorProvider(new SKColor(0, 200, 100)));
            using (var silent = new ThreadLocalRenderDiagnostics())
            {
                using var bmp = PixelAssert.Render(ImagePdf(""));
                Assert.DoesNotContain(silent.Events, NotForwarded);
            }
            using (var asked = new ThreadLocalRenderDiagnostics())
            {
                using var bmp = PixelAssert.Render(ImagePdf(" /Intent /Perceptual"));
                Assert.Contains(asked.Events, e => NotForwarded(e) && e.Detail.Contains("/Perceptual", StringComparison.Ordinal));
            }
        }
        finally { IccTransform.ResetToDefault(); }
    }

    /// <summary>
    /// A provider without the intent-aware interface is told, once, that a non-default intent was not
    /// forwarded; under the default intent nothing is said.
    /// </summary>
    [Fact]
    public void IccBasedFill_ComponentOnlyProvider_IsToldTheIntentWasNotForwarded()
    {
        IccTransform.Register(new FixedColorProvider(new SKColor(0, 200, 100)));
        try
        {
            using (var asked = new ThreadLocalRenderDiagnostics())
            {
                using var bmp = PixelAssert.Render(FillPdf(GrayProfile(CurvGamma(1.0)), n: 1, operands: "0.5", prefix: "/Perceptual ri "));
                Assert.Contains(asked.Events, e => e.Kind == RenderDiagnosticKind.RenderParameterIgnored
                    && e.Detail.Contains("IIccIntentColorTransformProvider", StringComparison.Ordinal));
            }
            using (var silent = new ThreadLocalRenderDiagnostics())
            {
                using var bmp = PixelAssert.Render(FillPdf(GrayProfile(CurvGamma(1.0)), n: 1, operands: "0.5"));
                Assert.DoesNotContain(silent.Events, e => e.Detail.Contains("IIccIntentColorTransformProvider", StringComparison.Ordinal));
            }
        }
        finally { IccTransform.ResetToDefault(); }
    }

    /// <summary>
    /// The not-forwarded report is once per registered provider, not once in a thread's lifetime:
    /// the same provider rendering again stays quiet, and a second provider registered on the same
    /// thread is told for itself.
    /// </summary>
    [Fact]
    public void IccBasedFill_NotForwardedReport_IsOncePerProvider()
    {
        byte[] pdf = FillPdf(GrayProfile(CurvGamma(1.0)), n: 1, operands: "0.5", prefix: "/Perceptual ri ");
        static bool NotForwarded((RenderDiagnosticKind Kind, string Detail) e) => e.Kind == RenderDiagnosticKind.RenderParameterIgnored
            && e.Detail.Contains("IIccIntentColorTransformProvider", StringComparison.Ordinal);
        try
        {
            IccTransform.Register(new FixedColorProvider(new SKColor(0, 200, 100)));
            using (var first = new ThreadLocalRenderDiagnostics())
            {
                using var bmp = PixelAssert.Render(pdf);
                Assert.Contains(first.Events, NotForwarded);
            }
            using (var again = new ThreadLocalRenderDiagnostics())
            {
                using var bmp = PixelAssert.Render(pdf);
                Assert.DoesNotContain(again.Events, NotForwarded);
            }

            IccTransform.Register(new FixedColorProvider(new SKColor(0, 200, 100)));
            using (var second = new ThreadLocalRenderDiagnostics())
            {
                using var bmp = PixelAssert.Render(pdf);
                Assert.Contains(second.Events, NotForwarded);
            }
        }
        finally { IccTransform.ResetToDefault(); }
    }

    /// <summary>
    /// A provider that manages images but not vector colour <b>says so</b>, on both channels.
    /// </summary>
    /// <remarks>
    /// <para><b>The only fallback in <c>IccColorTransform</c> that used to tell nobody.</b> A host
    /// in this state gets colour-managed images beside device-space vector fills <i>in the same
    /// document</i>, and the result is identical at the pixel level to a document whose profile
    /// simply is not managed — so neither the picture nor
    /// <c>IccTransform.LastUnsupportedProfile</c> could distinguish them.</para>
    ///
    /// <para>Both channels are asserted because they answer different questions: the sink says it
    /// happened at all, and the slot describes the most recent consultation, which is what a caller
    /// reads straight after a render.</para>
    /// </remarks>
    [Fact]
    public void IccBasedFill_ProviderWithoutComponentSupport_ReportsWhyItFellBack()
    {
        IccTransform.Register(new BitmapOnlyProvider());
        try
        {
            using var diagnostics = new ThreadLocalRenderDiagnostics();
            using var bmp = PixelAssert.Render(FillPdf(GrayProfile(CurvGamma(1.0)), n: 1, operands: "0.5"));

            Assert.Contains(diagnostics.Events,
                e => e.Kind == RenderDiagnosticKind.IccTransformFailed
                  && e.Detail.Contains("IIccColorTransformProvider", StringComparison.Ordinal));

            Assert.Contains("IIccColorTransformProvider",
                            IccTransform.LastUnsupportedProfile ?? "", StringComparison.Ordinal);
        }
        finally { IccTransform.ResetToDefault(); }
    }

    /// <summary>
    /// An ordinary render reports nothing, because the built-in provider implements both seams.
    /// </summary>
    /// <remarks>
    /// <b>The acceptance control, and the bar a new channel has to clear.</b> A report that fires
    /// on conformant input is one a caller learns to ignore. The default
    /// <c>SkiaIccTransformProvider</c> implements <c>IIccColorTransformProvider</c>, so the branch
    /// under test is unreachable without a host deliberately registering a partial provider.
    /// </remarks>
    [Fact]
    public void IccBasedFill_WithTheDefaultProvider_ReportsNothing()
    {
        using var diagnostics = new ThreadLocalRenderDiagnostics();
        using var bmp = PixelAssert.Render(FillPdf(GrayProfile(CurvGamma(1.0)), n: 1, operands: "0.5"));

        Assert.DoesNotContain(diagnostics.Events,
            e => e.Detail.Contains("IIccColorTransformProvider", StringComparison.Ordinal));
    }

    /// <summary>An /ICCBased stream that cannot be decoded says so rather than falling back mutely.</summary>
    /// <remarks>
    /// The second silent path. <c>DecodedProfileBytes</c> answers null for a profile whose stream
    /// is missing or will not decode, which is a damaged document rather than an unmanaged one —
    /// and those are the two readings a caller needs told apart.
    /// </remarks>
    [Fact]
    public void IccBasedFill_WithAnUndecodableProfile_ReportsWhyItFellBack()
    {
        using var diagnostics = new ThreadLocalRenderDiagnostics();

        // /N 1 with the profile object replaced by a reference to nothing.
        using var bmp = PixelAssert.Render(FillPdfWithMissingProfile());

        Assert.Contains(diagnostics.Events,
            e => e.Kind == RenderDiagnosticKind.IccTransformFailed
              && e.Detail.Contains("could not be decoded", StringComparison.Ordinal));
    }

    /// <summary>Collects <see cref="RenderDiagnostics"/> events raised on the calling thread only.</summary>
    /// <remarks>
    /// Filtered rather than serialised: the sink is process-wide, so a neighbouring test's
    /// legitimate report would otherwise satisfy — or break — an assertion about this one.
    /// </remarks>
    private sealed class ThreadLocalRenderDiagnostics : IDisposable
    {
        private readonly int _thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        private readonly Action<RenderDiagnosticKind, string, Exception> _handler;
        private readonly List<(RenderDiagnosticKind Kind, string Detail)> _events = new();

        internal ThreadLocalRenderDiagnostics()
        {
            _handler = (k, d, _) =>
            {
                if (System.Threading.Thread.CurrentThread.ManagedThreadId != _thread) return;
                lock (_events) _events.Add((k, d));
            };
            RenderDiagnostics.OnEvent += _handler;
        }

        internal IReadOnlyList<(RenderDiagnosticKind Kind, string Detail)> Events
        {
            get { lock (_events) return _events.ToList(); }
        }

        public void Dispose() => RenderDiagnostics.OnEvent -= _handler;
    }

    // ====================================================================
    //  Images
    // ====================================================================

    /// <summary>
    /// The image path with a grey profile whose tone curve is <b>sampled</b>.
    ///
    /// <para><b>Why a sampled curve and not a gamma.</b> <c>SKColorSpace.CreateIcc</c> can only
    /// express a <i>parametric</i> transfer function, so it declines a <c>curveType</c> table
    /// outright (measured against SkiaSharp 4.151.0) — this is precisely the profile the managed
    /// evaluator exists for. With a gamma curve Skia would produce the same answer, and the test
    /// could not tell the two engines apart: the mutation check confirmed exactly that, so this
    /// fixture was rebuilt around the curve form Skia cannot take.</para>
    ///
    /// <para>The table is 0 → 0.75 → 1.0, so sample 0x80 lands just past its middle point at
    /// luminance 0.751, which encodes to sRGB 225 — nowhere near the 128 the untransformed bitmap
    /// carries.</para>
    /// </summary>
    [Fact]
    public void IccBasedGrayImage_SampledToneCurve_IsTransformedThroughTheProfile()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 100 0 0 100 0 0 cm /Im1 Do Q"),
            Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8" +
                   " /ColorSpace [/ICCBased 6 0 R] /Filter /ASCIIHexDecode", "80FF>"),
            IccStreamObject(GrayProfile(CurvTable(new[] { 0.0, 0.75, 1.0 })), n: 1));

        using var bmp = PixelAssert.Render(pdf);

        var dark  = PixelAssert.At(bmp, 25, 50);
        var light = PixelAssert.At(bmp, 75, 50);
        Assert.InRange(dark.Red, 221, 229);        // 0x80 → luminance 0.751 → sRGB 225
        Assert.InRange(light.Red, 250, 255);       // the curve's top point is 1.0: white stays white
    }

    /// <summary>
    /// The grey bitmap path on its own, through the 256-entry lookup table. Sample 0x40 sits a
    /// quarter of the way up the same curve, at luminance 0.375.
    /// </summary>
    [Fact]
    public void IccColorTransform_GrayTrcBitmap_MapsEverySampleThroughTheToneCurve()
    {
        byte[] profile = GrayProfile(CurvTable(new[] { 0.0, 0.75, 1.0 }));
        using var bitmap = new SKBitmap(2, 1);
        bitmap.SetPixel(0, 0, new SKColor(64, 64, 64));
        bitmap.SetPixel(1, 0, new SKColor(128, 128, 128));

        Assert.True(IccColorTransform.TryTransform(profile, bitmap));

        // 64/255 = 0.251 → 0.75 * (0.251 / 0.5) = 0.3765
        var low = bitmap.GetPixel(0, 0);
        Assert.InRange(low.Red, ExpectedByte(0.3765) - 3, ExpectedByte(0.3765) + 3);
        Assert.Equal(low.Red, low.Blue);
        Assert.InRange(bitmap.GetPixel(1, 0).Red, 221, 229);
    }

    /// <summary>
    /// The three-component bitmap path, exercised directly because it sits behind
    /// <c>SKColorSpace.CreateIcc</c> in the provider and Skia handles the ordinary RGB profile
    /// itself. Permuted primaries again: red in, blue out.
    /// </summary>
    [Fact]
    public void IccColorTransform_MatrixTrcBitmap_MapsEveryPixelThroughTheMatrix()
    {
        byte[] profile = RgbProfile(CurvGamma(1.0), SrgbBlue, SrgbGreen, SrgbRed);
        using var bitmap = new SKBitmap(2, 1);
        bitmap.SetPixel(0, 0, new SKColor(255, 0, 0));
        bitmap.SetPixel(1, 0, new SKColor(0, 0, 255));

        Assert.True(IccColorTransform.TryTransform(profile, bitmap));

        Assert.InRange(bitmap.GetPixel(0, 0).Blue, 248, 255);
        Assert.InRange(bitmap.GetPixel(0, 0).Red, 0, 10);
        Assert.InRange(bitmap.GetPixel(1, 0).Red, 248, 255);
        Assert.InRange(bitmap.GetPixel(1, 0).Blue, 0, 10);
    }

    /// <summary>A profile with no evaluable model must leave every pixel exactly as it found it.</summary>
    [Fact]
    public void IccColorTransform_UnsupportedProfileBitmap_LeavesEveryPixelUntouched()
    {
        using var bitmap = new SKBitmap(1, 1);
        bitmap.SetPixel(0, 0, new SKColor(10, 20, 30));

        Assert.False(IccColorTransform.TryTransform(CmykProfile(), bitmap));

        Assert.Equal(new SKColor(10, 20, 30), bitmap.GetPixel(0, 0));
    }

    // ====================================================================
    //  Hostile input
    // ====================================================================

    /// <summary>
    /// Every offset and length in an ICC profile is file-supplied. Each case here is a profile
    /// whose declarations point outside itself; the requirement is that the render completes with
    /// the device fallback and that the reason is <i>named</i> rather than swallowed.
    /// </summary>
    [Theory]
    [InlineData("truncated")]
    [InlineData("tagOffsetPastEnd")]
    [InlineData("absurdCurvePointCount")]
    [InlineData("tagSizeOverflow")]
    [InlineData("xyzTagStraddlesTheEnd")]
    [InlineData("emptyProfile")]
    public void IccBasedFill_MalformedProfile_KeepsTheDeviceFallbackAndSaysWhy(string flavour)
    {
        byte[] profile = MalformedProfile(flavour);

        using var bmp = PixelAssert.Render(FillPdf(profile, n: 1, operands: "0.5"));

        Assert.InRange(PixelAssert.At(bmp, 50, 50).Red, DeviceHalf - 2, DeviceHalf + 2);
        Assert.False(string.IsNullOrEmpty(IccTransform.LastUnsupportedProfile),
            $"the '{flavour}' profile was skipped with no reason recorded");
    }

    /// <summary>
    /// A sampled curve's point count is a <c>uInt32</c> that sizes an allocation. The cap is
    /// refused before the array exists — asserted by the reason naming the cap, which a parser
    /// that had allocated first could not report.
    /// </summary>
    [Fact]
    public void IccProfile_AdversarialCurvePointCount_IsRefusedAgainstTheCap()
    {
        var profile = IccProfile.Parse(GrayProfile(CurvWithDeclaredCount(uint.MaxValue)));

        Assert.Equal(IccModel.Unsupported, profile.Model);
        Assert.Contains("over the", profile.UnsupportedReason);
    }

    /// <summary>A tag whose offset+size runs past the profile reads as absent, never as data.</summary>
    [Theory]
    [InlineData("tagOffsetPastEnd")]
    [InlineData("tagSizeOverflow")]
    [InlineData("xyzTagStraddlesTheEnd")]
    public void IccProfile_MalformedTagBounds_IsUnsupportedRatherThanRead(string flavour)
    {
        // Parse, not the renderer: the contract is that this method never throws, and the
        // exception a missing bound would produce is caught one layer up where it would be
        // invisible. `xyzTagStraddlesTheEnd` is the case that needs the tag table's own bound —
        // the signature reader's internal bound is not enough, because the signature IS present
        // and it is the twelve bytes after it that are not.
        var profile = IccProfile.Parse(MalformedProfile(flavour));

        Assert.Equal(IccModel.Unsupported, profile.Model);
        Assert.Equal(0, profile.ComponentCount);
        Assert.False(string.IsNullOrEmpty(profile.UnsupportedReason));
    }

    // ====================================================================
    //  Parser / curve arithmetic
    // ====================================================================

    /// <summary>
    /// ICC.1:2010 Table 65. Each row is a parametric function evaluated at a point where a wrong
    /// branch or a transposed parameter gives a different answer, checked as the grey luminance the
    /// profile yields (so this exercises the parse, not just the arithmetic).
    /// </summary>
    [Theory]
    // type, parameters, input, expected linear luminance
    [InlineData(0, new[] { 2.0 },                                  0.5,  0.25)]
    [InlineData(1, new[] { 2.0, 1.0, 0.0 },                        0.5,  0.25)]
    [InlineData(1, new[] { 2.0, 1.0, -0.6 },                       0.5,  0.0)]   // below -b/a
    [InlineData(2, new[] { 2.0, 1.0, -0.6, 0.1 },                  0.5,  0.1)]   // below -b/a → c
    [InlineData(3, new[] { 2.0, 1.0, 0.0, 0.25, 0.6 },             0.5,  0.125)] // below d → cX
    [InlineData(3, new[] { 2.0, 1.0, 0.0, 0.25, 0.4 },             0.5,  0.25)]  // at/above d
    [InlineData(4, new[] { 2.0, 1.0, 0.0, 0.5, 0.6, 0.05, 0.02 },  0.5,  0.27)]  // below d → cX+f
    [InlineData(4, new[] { 2.0, 1.0, 0.0, 0.5, 0.4, 0.05, 0.02 },  0.5,  0.30)]  // above d → (aX+b)^g+e
    public void IccCurve_ParametricFunctionTypes_MatchIccTable65(
        int functionType, double[] parameters, double input, double expectedLuminance)
    {
        var profile = IccProfile.Parse(GrayProfile(ParaCurve(functionType, parameters)));
        Assert.Equal(IccModel.GrayTrc, profile.Model);

        Assert.True(profile.TryEvaluate(new[] { (float)input }, out SKColor c));

        Assert.InRange(c.Red, ExpectedByte(expectedLuminance) - 2, ExpectedByte(expectedLuminance) + 2);
    }

    /// <summary>A <c>curveType</c> with a count of one carries a <c>u8Fixed8</c> gamma, not a sample.</summary>
    [Fact]
    public void IccProfile_CurveTypeWithOnePoint_ReadsItAsAU8Fixed8Gamma()
    {
        var profile = IccProfile.Parse(GrayProfile(CurvGamma(2.0)));

        Assert.True(profile.TryEvaluate(new[] { 0.5f }, out SKColor c));

        Assert.InRange(c.Red, ExpectedByte(0.25) - 2, ExpectedByte(0.25) + 2);
    }

    /// <summary>A <c>curveType</c> with a count of zero is the identity (ICC.1 §10.6).</summary>
    [Fact]
    public void IccProfile_CurveTypeWithNoPoints_IsTheIdentity()
    {
        var profile = IccProfile.Parse(GrayProfile(CurvWithDeclaredCount(0)));

        Assert.True(profile.TryEvaluate(new[] { 0.5f }, out SKColor c));

        Assert.InRange(c.Red, ExpectedByte(0.5) - 2, ExpectedByte(0.5) + 2);
    }

    /// <summary>A sampled curve is interpolated between its points, not snapped to one.</summary>
    [Fact]
    public void IccProfile_SampledCurve_InterpolatesBetweenItsPoints()
    {
        // Three points: 0.0, 0.25, 1.0. Halfway between the first two is 0.125 — a value neither
        // sample carries, so snapping to the nearest point cannot produce it.
        var profile = IccProfile.Parse(GrayProfile(CurvTable(new[] { 0.0, 0.25, 1.0 })));

        Assert.True(profile.TryEvaluate(new[] { 0.25f }, out SKColor c));

        Assert.InRange(c.Red, ExpectedByte(0.125) - 2, ExpectedByte(0.125) + 2);
    }

    /// <summary>
    /// A grey profile's <c>wtpt</c> is what its luminance is scaled along, so a curve output of 1.0
    /// is that white — white, not a cast.
    /// </summary>
    [Fact]
    public void IccProfile_GrayFullLuminance_IsTheProfileWhite()
    {
        var profile = IccProfile.Parse(GrayProfile(CurvGamma(1.0)));

        Assert.True(profile.TryEvaluate(new[] { 1f }, out SKColor c));

        Assert.InRange(c.Red, 252, 255);
        Assert.InRange(c.Green, 252, 255);
        Assert.InRange(c.Blue, 252, 255);
    }

    /// <summary>
    /// Profile classes that are outside this evaluator are named, not guessed at. Both of these
    /// are refused on the device class alone, before any tag is read — a device-link profile's
    /// A2B0 maps device→device rather than device→PCS, so reading it as if it were an ordinary
    /// A2B0 would produce a confident wrong colour rather than a decline.
    /// </summary>
    [Theory]
    [InlineData("link", "device-link")]
    [InlineData("nmcl", "named-colour")]
    public void IccProfile_DeviceClassesOutsideThisEvaluator_AreNamedInTheReason(
        string deviceClass, string expectedInReason)
    {
        byte[] bytes = BuildProfile(deviceClass, "RGB ", "XYZ ",
            ("A2B0", new byte[] { (byte)'m', (byte)'f', (byte)'t', (byte)'2', 0, 0, 0, 0, 0, 0, 0, 0 }));

        var profile = IccProfile.Parse(bytes);

        Assert.Equal(IccModel.Unsupported, profile.Model);
        Assert.Contains(expectedInReason, profile.UnsupportedReason);
    }

    /// <summary>
    /// A data space with no matrix/TRC form and <b>no lut either</b> is named by its space, which
    /// is the one thing a caller could act on.
    ///
    /// <para>These rows used to carry a stub <c>A2B0</c> tag and expect the <i>space</i> to be
    /// named — a claim that only held while the lut was unreadable by definition. Now that a valid
    /// lut is evaluated, an unreadable one is a fact about the tag and is reported as one (see
    /// <see cref="IccProfile_AnUnreadableLut_NamesTheTagRatherThanTheSpace"/>), so the fixtures
    /// here carry no lut at all and the rows say what they now mean. The <c>("RGB ", "Lab ")</c>
    /// row was <b>deleted rather than re-expected</b>: a Lab PCS is no longer an obstacle in any
    /// combination, so there is no version of that claim left to make.</para>
    /// </summary>
    [Theory]
    [InlineData("CMYK", "XYZ ", "CMYK")]
    [InlineData("2CLR", "XYZ ", "2CLR")]
    [InlineData("2CLR", "Lab ", "2CLR")]
    public void IccProfile_ASpaceWithNeitherAMatrixNorALut_IsNamedByItsSpace(
        string dataSpace, string pcs, string expectedInReason)
    {
        byte[] bytes = BuildProfile("mntr", dataSpace, pcs,
            ("wtpt", XyzTag(0.9642, 1.0, 0.8249)));

        var profile = IccProfile.Parse(bytes);

        Assert.Equal(IccModel.Unsupported, profile.Model);
        Assert.Contains(expectedInReason, profile.UnsupportedReason);
        Assert.Contains("A2B0", profile.UnsupportedReason);
    }

    /// <summary>
    /// An <c>A2B0</c> that is present but unreadable names the <b>tag</b> and what is wrong with
    /// it. That is the more actionable of the two true things: the file is malformed, not of a
    /// kind this evaluator declines, and those call for opposite responses from a caller.
    /// </summary>
    [Fact]
    public void IccProfile_AnUnreadableLut_NamesTheTagRatherThanTheSpace()
    {
        byte[] bytes = BuildProfile("mntr", "CMYK", "Lab ",
            ("A2B0", new byte[] { (byte)'m', (byte)'f', (byte)'t', (byte)'2', 0, 0, 0, 0, 0, 0, 0, 0 }));

        var profile = IccProfile.Parse(bytes);

        Assert.Equal(IccModel.Unsupported, profile.Model);
        Assert.Contains("A2B0", profile.UnsupportedReason);
        Assert.Contains("too short", profile.UnsupportedReason);
    }

    /// <summary>
    /// A malformed <b>matrix/TRC</b> profile keeps its own specific reason when there is no lut to
    /// fall through to. Reached for while wiring the lut path in, and it was a real loss: the
    /// fall-through returned the generic "neither a matrix/TRC form nor an A2B0 lut" for a profile
    /// whose <c>rTRC</c> is simply malformed, which reads as "unsupported kind" for a file that is
    /// actually broken.
    /// </summary>
    [Fact]
    public void IccProfile_AMalformedCurveWithNoLut_KeepsTheCurvesOwnReason()
    {
        byte[] bytes = BuildProfile("mntr", "GRAY", "XYZ ",
            ("kTRC", new byte[] { (byte)'c', (byte)'u', (byte)'r', (byte)'v', 0, 0, 0, 0,
                                  0xFF, 0xFF, 0xFF, 0xFF }));   // absurd point count

        var profile = IccProfile.Parse(bytes);

        Assert.Equal(IccModel.Unsupported, profile.Model);
        Assert.Contains("kTRC", profile.UnsupportedReason);
    }

    // ====================================================================
    //  Stub providers
    // ====================================================================

    private sealed class BitmapOnlyProvider : IIccTransformProvider
    {
        public SKColor TransformToSRgb(byte[] iccProfileBytes, byte[] sourceComponents) => SKColors.Black;
        public bool TransformToSRgb(byte[] iccProfileBytes, SKBitmap bitmap) => false;
    }

    private sealed class IntentRecordingProvider : IIccTransformProvider, IIccColorTransformProvider, IIccIntentColorTransformProvider
    {
        private readonly SKColor _color;
        public PdfRenderingIntent? LastColourIntent, LastImageIntent;
        public bool LastColourCompensation;
        public int ImageCalls;
        public IntentRecordingProvider(SKColor color) { _color = color; }

        public SKColor TransformToSRgb(byte[] iccProfileBytes, byte[] sourceComponents) => _color;
        public bool TransformToSRgb(byte[] iccProfileBytes, SKBitmap bitmap) => false;
        public bool TryTransformToSRgb(byte[] iccProfileBytes, float[] components, out SKColor color) { color = default; return false; }
        public bool TryTransformToSRgb(byte[] iccProfileBytes, float[] components, PdfRenderingIntent intent,
                                       bool blackPointCompensation, out SKColor color)
        {
            LastColourIntent = intent;
            LastColourCompensation = blackPointCompensation;
            color = _color;
            return true;
        }
        public bool TransformToSRgb(byte[] iccProfileBytes, SKBitmap bitmap, PdfRenderingIntent intent, bool blackPointCompensation)
        {
            ImageCalls++;
            LastImageIntent = intent;
            return false;
        }
    }

    private sealed class FixedColorProvider : IIccTransformProvider, IIccColorTransformProvider
    {
        private readonly SKColor _color;
        public int Calls;
        public FixedColorProvider(SKColor color) { _color = color; }

        public SKColor TransformToSRgb(byte[] iccProfileBytes, byte[] sourceComponents) => _color;
        public bool TransformToSRgb(byte[] iccProfileBytes, SKBitmap bitmap) => false;
        public bool TryTransformToSRgb(byte[] iccProfileBytes, float[] components, out SKColor color)
        {
            Calls++;
            color = _color;
            return true;
        }
    }

    // ====================================================================
    //  Expected values
    // ====================================================================

    /// <summary>The sRGB byte a linear-light luminance encodes to (IEC 61966-2-1).</summary>
    private static int ExpectedByte(double linear)
    {
        double encoded = linear <= 0.0031308
            ? 12.92 * linear
            : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
        return (int)Math.Round(Math.Max(0, Math.Min(1, encoded)) * 255);
    }

    // ====================================================================
    //  ICC profile construction
    // ====================================================================

    /// <summary>A one-component grey profile: <c>kTRC</c> plus the D50 white point.</summary>
    private static byte[] GrayProfile(byte[] toneCurve) =>
        BuildProfile("mntr", "GRAY", "XYZ ",
            ("kTRC", toneCurve),
            ("wtpt", XyzTag(0.9642, 1.0, 0.8249)));

    /// <summary>A three-component matrix/TRC profile: one shared tone curve and three columns.</summary>
    private static byte[] RgbProfile(byte[] toneCurve, double[] red, double[] green, double[] blue) =>
        BuildProfile("mntr", "RGB ", "XYZ ",
            ("rXYZ", XyzTag(red[0], red[1], red[2])),
            ("gXYZ", XyzTag(green[0], green[1], green[2])),
            ("bXYZ", XyzTag(blue[0], blue[1], blue[2])),
            ("rTRC", toneCurve),
            ("gTRC", toneCurve),
            ("bTRC", toneCurve),
            ("wtpt", XyzTag(0.9642, 1.0, 0.8249)));

    /// <summary>A four-component profile with a lut — structurally valid, and outside this evaluator.</summary>
    private static byte[] CmykProfile() =>
        BuildProfile("prtr", "CMYK", "XYZ ",
            ("A2B0", new byte[] { (byte)'m', (byte)'f', (byte)'t', (byte)'2', 0, 0, 0, 0, 0, 0, 0, 0 }),
            ("wtpt", XyzTag(0.9642, 1.0, 0.8249)));

    private static byte[] MalformedProfile(string flavour)
    {
        byte[] good = GrayProfile(CurvGamma(1.0));
        switch (flavour)
        {
            case "emptyProfile":
                return new byte[0];

            case "truncated":
            {
                // Header intact (so 'acsp' still matches) but the tag data is gone.
                var cut = new byte[140];
                Array.Copy(good, cut, cut.Length);
                return cut;
            }

            case "tagOffsetPastEnd":
            {
                var copy = (byte[])good.Clone();
                // First tag entry's offset → far beyond the profile.
                WriteU32(copy, 136, 0x7FFF0000);
                return copy;
            }

            case "tagSizeOverflow":
            {
                var copy = (byte[])good.Clone();
                WriteU32(copy, 140, uint.MaxValue);   // first tag entry's size
                return copy;
            }

            case "absurdCurvePointCount":
                return GrayProfile(CurvWithDeclaredCount(uint.MaxValue));

            case "xyzTagStraddlesTheEnd":
            {
                // The nastiest shape: a tag whose offset is INSIDE the profile and whose declared
                // size runs off the end, with a valid 'XYZ ' signature at that offset. The
                // signature check passes, so only the tag table's own offset+size bound stands
                // between the reader and three s15Fixed16 values that are not there.
                var copy = (byte[])RgbProfile(CurvGamma(1.0), SrgbRed, SrgbGreen, SrgbBlue).Clone();
                int lastFour = copy.Length - 4;
                WriteSig(copy, lastFour, "XYZ ");
                WriteU32(copy, 136, (uint)lastFour);   // first tag entry (rXYZ) → offset
                WriteU32(copy, 140, 20);               // ...declared as a full XYZType
                return copy;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(flavour), flavour, "unknown flavour");
        }
    }

    /// <summary>
    /// Assembles a profile: the 128-byte header, the tag count, the 12-byte-per-tag table, then
    /// the tag data, each aligned to a four-byte boundary as ICC.1 §7.3.1 requires.
    /// </summary>
    internal static byte[] BuildProfile(string deviceClass, string dataSpace, string pcs,
        params (string signature, byte[] data)[] tags)
    {
        int cursor = 132 + tags.Length * 12;
        var offsets = new int[tags.Length];
        for (int i = 0; i < tags.Length; i++)
        {
            offsets[i] = cursor;
            cursor += tags[i].data.Length;
            while ((cursor & 3) != 0) cursor++;
        }

        var profile = new byte[cursor];
        WriteU32(profile, 0, (uint)cursor);          // profile size
        WriteSig(profile, 12, deviceClass);
        WriteSig(profile, 16, dataSpace);
        WriteSig(profile, 20, pcs);
        WriteSig(profile, 36, "acsp");
        WriteS15(profile, 68, 0.9642);               // PCS illuminant (D50)
        WriteS15(profile, 72, 1.0);
        WriteS15(profile, 76, 0.8249);
        WriteU32(profile, 128, (uint)tags.Length);

        for (int i = 0; i < tags.Length; i++)
        {
            int entry = 132 + i * 12;
            WriteSig(profile, entry, tags[i].signature);
            WriteU32(profile, entry + 4, (uint)offsets[i]);
            WriteU32(profile, entry + 8, (uint)tags[i].data.Length);
            Array.Copy(tags[i].data, 0, profile, offsets[i], tags[i].data.Length);
        }
        return profile;
    }

    /// <summary><c>XYZType</c> — signature, reserved, then three <c>s15Fixed16</c> values.</summary>
    internal static byte[] XyzTag(double x, double y, double z)
    {
        var tag = new byte[20];
        WriteSig(tag, 0, "XYZ ");
        WriteS15(tag, 8, x); WriteS15(tag, 12, y); WriteS15(tag, 16, z);
        return tag;
    }

    /// <summary><c>curveType</c> with a count of one: the value is a <c>u8Fixed8</c> gamma.</summary>
    internal static byte[] CurvGamma(double gamma)
    {
        var tag = new byte[14];
        WriteSig(tag, 0, "curv");
        WriteU32(tag, 8, 1);
        int fixed88 = (int)Math.Round(gamma * 256.0);
        tag[12] = (byte)(fixed88 >> 8);
        tag[13] = (byte)fixed88;
        return tag;
    }

    /// <summary><c>curveType</c> with sampled points.</summary>
    private static byte[] CurvTable(double[] samples)
    {
        var tag = new byte[12 + samples.Length * 2];
        WriteSig(tag, 0, "curv");
        WriteU32(tag, 8, (uint)samples.Length);
        for (int i = 0; i < samples.Length; i++)
        {
            int v = (int)Math.Round(samples[i] * 65535.0);
            tag[12 + i * 2] = (byte)(v >> 8);
            tag[13 + i * 2] = (byte)v;
        }
        return tag;
    }

    /// <summary>A <c>curveType</c> whose declared point count is not backed by any data.</summary>
    private static byte[] CurvWithDeclaredCount(uint count)
    {
        var tag = new byte[12];
        WriteSig(tag, 0, "curv");
        WriteU32(tag, 8, count);
        return tag;
    }

    /// <summary><c>parametricCurveType</c> — function type then its <c>s15Fixed16</c> parameters.</summary>
    private static byte[] ParaCurve(int functionType, double[] parameters)
    {
        var tag = new byte[12 + parameters.Length * 4];
        WriteSig(tag, 0, "para");
        tag[8] = (byte)(functionType >> 8);
        tag[9] = (byte)functionType;
        for (int i = 0; i < parameters.Length; i++) WriteS15(tag, 12 + i * 4, parameters[i]);
        return tag;
    }

    private static void WriteU32(byte[] d, int o, uint v)
    {
        d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16);
        d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v;
    }

    private static void WriteS15(byte[] d, int o, double v) =>
        WriteU32(d, o, unchecked((uint)(int)Math.Round(v * 65536.0)));

    private static void WriteSig(byte[] d, int o, string signature)
    {
        for (int i = 0; i < 4; i++) d[o + i] = (byte)signature[i];
    }

    // ====================================================================
    //  PDF fixtures
    // ====================================================================

    /// <summary>A 100×100 page filled through an <c>[/ICCBased …]</c> colour-space resource.</summary>
    private static byte[] FillPdf(byte[] profile, int n, string operands, string prefix = "") =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /ColorSpace << /CS0 5 0 R >> >> >>",
            Stream("", $"{prefix}/CS0 cs {operands} sc 0 0 100 100 re f"),
            "[ /ICCBased 6 0 R ]",
            IccStreamObject(profile, n));

    /// <summary>
    /// The same page, but the <c>[/ICCBased …]</c> array points at an object that is not a stream.
    /// </summary>
    /// <remarks>
    /// <b>A damaged document, not an unmanaged one</b>, which is the distinction the diagnostic
    /// exists to draw: both paint the device fallback and are identical at the pixel level. The
    /// object is a dictionary rather than a missing number so the file still parses — a dangling
    /// reference would be refused earlier and never reach the profile decode.
    /// </remarks>
    private static byte[] FillPdfWithMissingProfile() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /ColorSpace << /CS0 5 0 R >> >> >>",
            Stream("", "/CS0 cs 0.5 sc 0 0 100 100 re f"),
            "[ /ICCBased 6 0 R ]",
            "<< /N 1 >>");

    /// <summary>The same page filled in a device space — the control for every ICC assertion.</summary>
    private static byte[] DeviceFillPdf(string colourOperator) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << >> >>",
            Stream("", $"{colourOperator} 0 0 100 100 re f"));

    /// <summary>
    /// The ICC profile stream object. The profile is hex-encoded so the whole fixture stays ASCII,
    /// and it is a separate indirect object because a stream cannot be written inline in the
    /// <c>[/ICCBased …]</c> array (§7.3.8).
    /// </summary>
    private static string IccStreamObject(byte[] profile, int n)
    {
        string hex = ToHex(profile);
        return $"<< /N {n} /Filter /ASCIIHexDecode /Length {hex.Length} >>\nstream\n{hex}\nendstream";
    }

    private static string ToHex(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 2 + 1);
        foreach (byte b in data) sb.Append(b.ToString("X2"));
        sb.Append('>');
        return sb.ToString();
    }

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

/// <summary>
/// Serialises every test class that registers a provider on the process-wide
/// <c>IccTransform</c> singleton. See <see cref="IccTransformTests.SerialCollection"/> for why the
/// name is what it is and what happens without it.
/// </summary>
[CollectionDefinition(IccTransformTests.SerialCollection, DisableParallelization = true)]
public sealed class IccTransformSerialCollection { }
