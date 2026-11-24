// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Colour rules whose breach is silent: the file says one thing, the raster path does another,
/// and no diagnostic says so.
///
/// <para>Each test asserts decoded pixels or the computed value, never non-null output — and each
/// is mutation-checked by breaking the code it covers and confirming it goes red.</para>
/// </summary>
public sealed class ColourGapClosureTests
{
    // ====================================================================
    //  1. CalGray /Gamma for fills and strokes
    // ====================================================================

    /// <summary>
    /// §8.6.5.2: a CalGray component is raised to <c>/Gamma</c>. <c>OpSc</c> had no CalGray branch,
    /// so the fill fell through to <c>ComponentsToColor</c>, where CalGray is ALIASED to DeviceGray
    /// — 0.5 painted as 128 whatever the space said. With gamma 2.2 the luminance is
    /// 0.5^2.2 = 0.2176, sRGB 129; the gamma dropped (gamma 1, luminance 0.5) is 188.
    /// </summary>
    [Fact]
    public void CalGrayFill_AppliesGamma()
    {
        using var bmp = PixelAssert.Render(CalGrayPdf(gamma: "2.2"));

        var c = PixelAssert.At(bmp, 50, 50);
        Assert.InRange(c.Red, 125, 133);
        Assert.Equal(c.Red, c.Green);
        Assert.Equal(c.Green, c.Blue);
    }

    /// <summary>
    /// The control: the same content in a CalGray space with the default <c>/Gamma</c> 1 is the
    /// luminance 0.5 itself — sRGB 188, as the reference renderer renders it (187/188), lighter than DeviceGray's
    /// 128. Without it the test above could pass on a renderer that aliased CalGray to DeviceGray,
    /// which paints 128 for both. (It was <c>CalGrayFill_DefaultGamma_IsDeviceGray</c>, asserting 128,
    /// until CalGray was read as §8.6.5.2 defines it — luminance, not an encoded grey level.)
    /// </summary>
    [Fact]
    public void CalGrayFill_DefaultGamma_IsLinearLuminance()
    {
        using var bmp = PixelAssert.Render(CalGrayPdf(gamma: null));

        Assert.InRange(PixelAssert.At(bmp, 50, 50).Red, 185, 191);
    }

    /// <summary>Strokes take the same path (<c>SC</c>), and had the same defect.</summary>
    [Fact]
    public void CalGrayStroke_AppliesGamma()
    {
        using var bmp = PixelAssert.Render(CalGrayStrokePdf(gamma: "2.2"));

        // 20-unit-wide stroke centred on y = 50, sampled on its centre line.
        Assert.InRange(PixelAssert.At(bmp, 50, 50).Red, 125, 133);
    }

    // ====================================================================
    //  2. Lab /WhitePoint and /Range for fills
    // ====================================================================

    /// <summary>
    /// §8.6.5.4 Table 65: <c>/Range</c> bounds a* and b*, and a value outside it is clamped.
    /// The generic path ignored the space's parameters entirely, so <c>50 100 -100 sc</c> in a
    /// space declaring <c>/Range [-20 20 -20 20]</c> painted the fully saturated colour.
    ///
    /// <para>Asserted against the same Lab value written directly in an unrestricted space —
    /// no reference to the conversion's own output — plus the proof that the fixture can tell
    /// the two apart.</para>
    /// </summary>
    [Fact]
    public void LabFill_ClampsComponentsToRange()
    {
        using var clamped = PixelAssert.Render(LabPdf("/Range [-20 20 -20 20]", "50 100 -100"));
        using var direct  = PixelAssert.Render(LabPdf("", "50 20 -20"));
        using var unclamped = PixelAssert.Render(LabPdf("", "50 100 -100"));

        var c = PixelAssert.At(clamped, 50, 50);
        var d = PixelAssert.At(direct, 50, 50);
        var u = PixelAssert.At(unclamped, 50, 50);

        Assert.True(PixelAssert.Near(c, d, 2),
            $"/Range [-20 20] should clamp a*=100,b*=-100 to 20,-20: got {c}, expected {d}");
        // Sensitivity: the clamp has to be observable, or the assertion above proves nothing.
        Assert.False(PixelAssert.Near(u, d, 24),
            $"fixture is insensitive — unclamped {u} is indistinguishable from clamped {d}");
    }

    /// <summary>
    /// The conversion must run against the space's own <c>/WhitePoint</c>, not an assumed D50.
    /// The reference value is external: sRGB pure red is L*a*b* (53.24, 80.09, 67.20) <b>under
    /// D65</b>, so a Lab space declaring the D65 white point must paint that triple as (255, 0, 0).
    /// Feeding it through the D50 pipeline — what the code did — yields ≈(250, 0, 7).
    /// </summary>
    [Fact]
    public void LabFill_UsesTheSpaceWhitePoint()
    {
        using var bmp = PixelAssert.Render(
            LabPdf("/WhitePoint [0.9505 1.0 1.089]", "53.2408 80.0925 67.2032"));

        var c = PixelAssert.At(bmp, 50, 50);
        Assert.True(c.Red >= 253, $"sRGB red under its own D65 white point should saturate: {c}");
        Assert.True(c.Green <= 2, $"expected no green: {c}");
        Assert.True(c.Blue <= 3,  $"D50 conversion leaks blue into sRGB red: {c}");
    }

    /// <summary>
    /// A fill and an image in the SAME Lab space must produce the same colour. They did not: the
    /// image path read <c>/Range</c> and the fill path read nothing, and neither honoured
    /// <c>/WhitePoint</c>. Bytes 255/255/0 with the default range are exactly L*=100, a*=100,
    /// b*=-100, so the two fixtures name one colour two ways.
    /// </summary>
    [Fact]
    public void LabFillAndLabImage_AgreeOnTheSameColour()
    {
        const string ws = "/WhitePoint [0.9505 1.0 1.089]";
        using var fill  = PixelAssert.Render(LabPdf(ws, "100 100 -100"));
        using var image = PixelAssert.Render(LabImagePdf(ws, "FFFF00>"));

        var f = PixelAssert.At(fill, 50, 50);
        var i = PixelAssert.At(image, 50, 50);
        Assert.True(PixelAssert.Near(f, i, 2), $"Lab fill {f} and Lab image {i} disagree");
    }

    // ====================================================================
    //  3. Indexed index rounding
    // ====================================================================

    /// <summary>
    /// §8.6.6.3: an <c>Indexed</c> operand "shall be rounded to the nearest integer", then clamped
    /// to [0, hival]. <c>OpSc</c> used <c>Math.Ceiling</c>, so every non-integral index picked the
    /// entry above the one the file named — <c>sc 2.4</c> painted palette entry 3.
    /// </summary>
    [Theory]
    [InlineData("0.2", 0)]   // ceiling → 1 (red)
    [InlineData("2.4", 2)]   // ceiling → 3 (blue)
    [InlineData("2.6", 3)]   // both agree — proves the fixture can reach entry 3
    [InlineData("2.5", 3)]   // midpoint away from zero; banker's rounding would say 2
    public void IndexedFill_RoundsToNearestIndex(string operand, int expectedEntry)
    {
        SKColor[] palette =
        {
            new SKColor(0, 0, 0), new SKColor(255, 0, 0),
            new SKColor(0, 255, 0), new SKColor(0, 0, 255),
        };

        using var bmp = PixelAssert.Render(IndexedPdf(operand));

        var c = PixelAssert.At(bmp, 50, 50);
        Assert.True(PixelAssert.Near(c, palette[expectedEntry], 8),
            $"index {operand} should select entry {expectedEntry} ({palette[expectedEntry]}), got {c}");
    }

    // ====================================================================
    //  4. 16-bpc CMYK /Decode
    // ====================================================================

    /// <summary>
    /// §8.9.5.2: <c>/Decode</c> applies at every bit depth. The generic 16-bit block's
    /// 4-component branch never consulted it, so an inverting CMYK <c>/Decode</c> — the array an
    /// Adobe-marked CMYK JPEG carries — was dropped and the image rendered as its own negative.
    /// All-zero samples are no ink (white); under <c>[1 0 …]</c> they are full ink (black).
    /// </summary>
    [Fact]
    public void Cmyk16BitImage_AppliesInvertingDecode()
    {
        using var bmp = PixelAssert.Render(Cmyk16Pdf("/Decode [1 0 1 0 1 0 1 0]"));

        var c = PixelAssert.At(bmp, 50, 50);
        Assert.True(c.Red < 12 && c.Green < 12 && c.Blue < 12,
            $"/Decode [1 0 …] should turn zero samples into full ink, got {c}");
    }

    /// <summary>The control: the same samples with no <c>/Decode</c> are no ink at all.</summary>
    [Fact]
    public void Cmyk16BitImage_WithoutDecode_IsUnpainted()
    {
        using var bmp = PixelAssert.Render(Cmyk16Pdf(""));

        var c = PixelAssert.At(bmp, 50, 50);
        Assert.True(c.Red > 243 && c.Green > 243 && c.Blue > 243,
            $"zero CMYK samples are white without /Decode, got {c}");
    }

    // ====================================================================
    //  5. Type 0 (sampled) function — multilinear interpolation for m >= 3
    // ====================================================================

    /// <summary>
    /// §7.10.2 interpolates a sampled function multilinearly. Three or more inputs took the
    /// NEAREST sample instead, quantising a 4-ink DeviceN→CMYK transform to its /Size grid.
    /// With only the (1,1,1) corner set, the value at (0.75, 0.75, 0.75) is 0.75³ = 0.421875;
    /// nearest-sample answers 1.0.
    /// </summary>
    [Theory]
    [InlineData(0.75f, 0.421875f)]
    [InlineData(0.5f,  0.125f)]
    [InlineData(1f,    1f)]        // exactly on the corner: both agree
    [InlineData(0f,    0f)]
    public void SampledFunction_ThreeInputs_InterpolatesMultilinearly(float input, float expected)
    {
        var fn = SampledFunction(inputs: 3, cornerValue: 255);

        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { input, input, input });

        Assert.Equal(expected, result[0], 4);
    }

    /// <summary>Four inputs — the 4-ink case the gap named — over 16 corners.</summary>
    [Fact]
    public void SampledFunction_FourInputs_InterpolatesMultilinearly()
    {
        var fn = SampledFunction(inputs: 4, cornerValue: 255);

        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 0.5f, 0.5f, 0.5f, 0.5f });

        Assert.Equal(0.0625f, result[0], 4);   // 0.5^4; nearest-sample would say 0
    }

    /// <summary>
    /// Interpolation is exponential in the input count and the input count is the colorant count
    /// the FILE chooses (§8.6.6.5 allows 32), so it is capped at
    /// <see cref="PdfLimits.MaxSampledFunctionInterpolationInputs"/>. Past the cap the evaluator
    /// must fall back to the nearest sample — not allocate 2^m corners, and not silently pretend
    /// it interpolated.
    /// </summary>
    [Fact]
    public void SampledFunction_AdversarialInputCount_FallsBackToNearestAndReports()
    {
        int m = PdfLimits.MaxSampledFunctionInterpolationInputs + 1;
        var fn = SampledFunction(inputs: m, cornerValue: 255);
        var at = new float[m];
        for (int i = 0; i < m; i++) at[i] = 0.75f;

        float[] result = PdfFunctionEvaluator.Evaluate(fn, at);

        // Nearest sample rounds every axis to 1 → the top corner, 1.0. Multilinear would be
        // 0.75^m ≈ 0.075, so this value alone says which path ran.
        Assert.Equal(1f, result[0], 4);
        Assert.Contains("interpolation is capped", PdfFunctionEvaluator.LastType0Failure ?? "");
    }

    /// <summary>
    /// Adversarial <c>/Size</c>: three axes of two billion entries multiply out past what a sample
    /// index can address, and multiplying the mixed-radix strides would wrap and name a
    /// valid-looking WRONG sample. The evaluator must recognise that no sample is reachable — say
    /// so, and answer 0 — rather than reading whatever the wrapped index lands on.
    /// </summary>
    [Fact]
    public void SampledFunction_AdversarialSizeOverflow_ReturnsZeroAndSaysWhy()
    {
        var fn = ParseSingleObject(
            "<< /FunctionType 0 /Domain [0 1 0 1 0 1] /Range [0 1]" +
            " /Size [2000000000 2000000000 2000000000] /BitsPerSample 8" +
            " /Filter /ASCIIHexDecode /Length 5 >>\nstream\nFFFF>\nendstream");

        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { 1f, 1f, 1f });

        // The guard's own evidence, which is what CLAUDE.md asks a DoS-guard test to assert: the
        // refusal value, and the reported reason for it. Both are unreachable unless the size check
        // ran, so together they say the guard held.
        Assert.Equal(0f, result[0], 4);
        Assert.Contains("more samples than an index can address",
                        PdfFunctionEvaluator.LastType0Failure ?? "");

        // No wall-clock assertion: it would add nothing the two assertions above do not already
        // prove, and a full-solution run's assemblies compete for the same cores, so elapsed time
        // measures the machine's load rather than the code's work.
    }

    /// <summary>
    /// The reason slot must be cleared at the START of an evaluation, on every path — otherwise
    /// the previous function's reason is still in it and a caller reads a failure that did not
    /// happen (the mistake <c>LastPermsValidation</c> made).
    /// </summary>
    [Fact]
    public void SampledFunction_AfterACappedCall_ClearsTheReasonOnTheNextGoodCall()
    {
        int m = PdfLimits.MaxSampledFunctionInterpolationInputs + 1;
        var wide = SampledFunction(inputs: m, cornerValue: 255);
        var at = new float[m];
        PdfFunctionEvaluator.Evaluate(wide, at);
        Assert.NotNull(PdfFunctionEvaluator.LastType0Failure);

        PdfFunctionEvaluator.Evaluate(SampledFunction(inputs: 3, cornerValue: 255),
                                      new[] { 0.5f, 0.5f, 0.5f });

        Assert.Null(PdfFunctionEvaluator.LastType0Failure);
    }

    // ====================================================================
    //  6. /HT, /SA and the rendering intent are reported, not silently dropped
    // ====================================================================

    /// <summary>
    /// A halftone that actually overrides the device's is reported.
    /// </summary>
    /// <remarks>
    /// Not <c>/HT /Default</c>, which is the one value that must <b>not</b> be reported: §10.5.1
    /// defines the name <c>/Default</c> as "revert to the device's halftone", so it names the
    /// behaviour this renderer already has. That negative case lives in
    /// <c>SvgRasterParityTests.NoOpGraphicsStateValue_IsReportedByNeitherRenderer</c>.
    /// </remarks>
    [Fact]
    public void Halftone_IsReportedAsIgnored()
    {
        var reports = RenderAndCollect(ExtGStatePdf(
            "/HT << /HalftoneType 1 /Frequency 60 /Angle 45 /SpotFunction /Round >>"));

        Assert.Contains(reports, d => d.Contains("Halftone"));
    }

    [Fact]
    public void StrokeAdjustment_IsReportedAsIgnored()
    {
        var reports = RenderAndCollect(ExtGStatePdf("/SA true"));

        Assert.Contains(reports, d => d.Contains("Stroke adjustment"));
    }

    /// <summary>
    /// An intent that names an ICC table is <b>honoured</b>, so it is not reported as ignored.
    /// </summary>
    /// <remarks>
    /// <para>A profile carrying separate <c>A2B0</c>/<c>A2B1</c>/<c>A2B2</c> tables has a stage for an
    /// intent to select, and both renderers select between them — so reporting the intent would be a
    /// diagnostic firing on conformant input, which is the noise floor
    /// <c>IgnoredParameterReporting.Deviates</c> exists to protect.</para>
    /// </remarks>
    [Fact]
    public void RenderingIntent_FromExtGState_IsHonouredAndNotReported()
    {
        var reports = RenderAndCollect(ExtGStatePdf("/RI /Perceptual"));

        Assert.DoesNotContain(reports, d => d.Contains("Rendering intent"));
    }

    [Fact]
    public void RenderingIntent_FromTheRiOperator_IsHonouredAndNotReported()
    {
        var reports = RenderAndCollect(ContentPdf("/Saturation ri 0 0 100 100 re f", ""));

        Assert.DoesNotContain(reports, d => d.Contains("Rendering intent"));
    }

    /// <summary>
    /// <c>/AbsoluteColorimetric</c> is honoured too, so no intent is reported: it is the relative
    /// table scaled by the profile's media white, checked against the reference colour engine
    /// (<c>IccAbsoluteColorimetricTests</c>). The potency control beside it: <c>/SA true</c>,
    /// ignored, is reported through the same render.
    /// </summary>
    [Fact]
    public void AbsoluteColorimetric_IsHonouredAndNotReported()
    {
        var reports = RenderAndCollect(ExtGStatePdf("/RI /AbsoluteColorimetric /SA true"));

        Assert.DoesNotContain(reports, d => d.Contains("Rendering intent"));
        Assert.Contains(reports, d => d.Contains("Stroke adjustment"));
    }

    /// <summary>
    /// §11.6.4.4: with <c>/AIS true</c> the soft mask and the alpha constants supply <i>shape</i>
    /// rather than opacity. It is <b>honoured</b> now — in a knockout group such an element
    /// composites over the group's earlier elements (<c>AlphaIsShapeTests</c>), and elsewhere the
    /// two readings give the same pixel — so it is no longer reported as ignored. This case
    /// asserted the opposite while nothing read the flag, and was renamed rather than re-asserted.
    /// The potency control beside it: <c>/SA true</c>, ignored, is reported through the same render.
    /// </summary>
    [Fact]
    public void AlphaIsShape_IsHonouredAndNotReported()
    {
        var reports = RenderAndCollect(ExtGStatePdf("/AIS true /SA true"));

        Assert.DoesNotContain(reports, d => d.Contains("/AIS"));
        Assert.Contains(reports, d => d.Contains("Stroke adjustment"));
    }

    /// <summary>
    /// <c>/AIS false</c> is the initial value and is exactly what this renderer does, so reporting
    /// it would tell a caller "we ignored this" about the case we get right.
    /// </summary>
    [Fact]
    public void AlphaIsShapeFalse_IsNotReported()
    {
        var reports = RenderAndCollect(ExtGStatePdf("/AIS false"));

        Assert.DoesNotContain(reports, d => d.Contains("/AIS"));
    }

    /// <summary>
    /// §9.3.8 <c>/TK</c> is <b>honoured</b> by the raster path
    /// (<c>ContentStreamInterpreter.BeginTextKnockout</c>), so neither value may be reported here.
    ///
    /// <para>The knockout behaviour itself is pinned by decoded pixels in <c>TextKnockoutTests</c>,
    /// and the SVG report by the case there that names it.</para>
    /// </summary>
    [Theory]
    [InlineData("/TK true")]
    [InlineData("/TK false")]
    public void TextKnockout_IsNoLongerReportedByTheRasterPath(string tk)
    {
        var reports = RenderAndCollect(ExtGStatePdf(tk));

        Assert.DoesNotContain(reports, d => d.Contains("/TK"));
    }

    /// <summary>
    /// The counterpart that keeps the six above honest: a file that sets none of them must
    /// produce no such report, or "ignored" could not be told from "absent" after all.
    /// </summary>
    [Fact]
    public void NoIgnoredParameters_ReportsNothing()
    {
        var reports = RenderAndCollect(ContentPdf("0 0 100 100 re f", ""));

        Assert.Empty(reports);
    }

    /// <summary>
    /// PDF 2.0's <c>/UseBlackPtComp</c> (ISO 32000-2 §8.6.5.9, PDF Association AN001) is honoured
    /// now — every value — so none is reported as ignored (what each does:
    /// <c>IccSourceBlackPointTests</c>). The potency control beside it: <c>/SA true</c>, which is
    /// ignored, is reported through the same render.
    /// </summary>
    [Theory]
    [InlineData("/UseBlackPtComp /ON")]
    [InlineData("/UseBlackPtComp /OFF")]
    [InlineData("/UseBlackPtComp /Default")]
    public void UseBlackPtComp_IsHonoured_SoNotReportedAsIgnored(string entry)
    {
        Assert.DoesNotContain(RenderAndCollect(ExtGStatePdf(entry)), d => d.Contains("UseBlackPtComp"));
        Assert.Contains(RenderAndCollect(ExtGStatePdf(entry + " /SA true")), d => d.Contains("/SA"));
    }

    // ====================================================================
    //  7. DeviceN /All colorant and /Subtype /NChannel
    // ====================================================================

    /// <summary>
    /// §8.6.6.4: the colorant name <c>/All</c> applies the tint to every colorant at once, so a
    /// full tint is full coverage — black — whatever the tint transform says about one ink.
    /// The fixture's transform maps tint 1 to blue, which is what the unhandled code painted.
    /// </summary>
    [Fact]
    public void SeparationAll_FullTint_PaintsAllColorants()
    {
        using var bmp = PixelAssert.Render(SeparationPdf("/All", "1"));

        var c = PixelAssert.At(bmp, 50, 50);
        Assert.True(c.Blue < 40 && c.Red < 40 && c.Green < 40,
            $"/All at tint 1 is full coverage of every ink, got {c}");
    }

    /// <summary>And a zero tint is no ink at all — the same reasoning in the other direction.</summary>
    [Fact]
    public void SeparationAll_ZeroTint_PaintsNothing()
    {
        using var bmp = PixelAssert.Render(SeparationPdf("/All", "0"));

        Assert.True(PixelAssert.IsWhite(PixelAssert.At(bmp, 50, 50)));
    }

    /// <summary>
    /// The colorant name may be written as an <b>indirect reference</b> (§7.3.10 allows any value
    /// but a stream to be), and <c>/All</c> must still be recognised — in <i>both</i> renderers.
    /// </summary>
    /// <remarks>
    /// <para>Read as <c>csArr[1] as PdfName</c>, which does not follow a reference, an indirect
    /// <c>/All</c> would read as <i>absent</i> and fall through to the tint transform — painting the
    /// registration mark <b>lighter than full coverage</b>, the exact defect the <c>/All</c> branch
    /// exists to prevent — while the SVG exporter, through <c>PdfResolve.Name</c>, exported it black.</para>
    ///
    /// <para>This is the failure mode CLAUDE.md names for the whole render path — the indexer does not
    /// follow an indirect reference — at an <i>array element</i> rather than a dictionary read.</para>
    /// </remarks>
    [Fact]
    public void SeparationAll_NamedIndirectly_IsStillFullCoverage()
    {
        using var bmp = PixelAssert.Render(IndirectAllSeparationPdf());

        var c = PixelAssert.At(bmp, 50, 50);
        Assert.True(c.Blue < 40 && c.Red < 40 && c.Green < 40,
            $"an indirect /All is still /All, so tint 1 is full coverage; got {c}");
    }

    /// <summary>
    /// And the two renderers agree about it. The raster and SVG paths each carry their own copy of
    /// the <c>/All</c> rule, which is how they came to disagree; this asserts the answer rather than
    /// the implementation.
    /// </summary>
    [Fact]
    public void SeparationAll_NamedIndirectly_AgreesBetweenRenderers()
    {
        byte[] pdf = IndirectAllSeparationPdf();

        using var bmp = PixelAssert.Render(pdf);
        var raster = PixelAssert.At(bmp, 50, 50);

        using var ms = new MemoryStream(pdf);
        string svg = Tetitulz.Pdf.Base.PdfOperations.RenderPageToSvg(ms, 0);

        // The SVG path emits the same colour as a CSS rgb(...) fill; both must be full coverage.
        Assert.True(raster.Blue < 40 && raster.Red < 40 && raster.Green < 40);
        Assert.DoesNotContain("rgb(255,255,255)", svg.Replace(" ", ""), StringComparison.Ordinal);
    }

    /// <summary>
    /// A named colorant keeps going through the tint transform: /All must be the special case, not
    /// a new rule for every Separation.
    /// </summary>
    [Fact]
    public void SeparationNamedColorant_StillUsesTheTintTransform()
    {
        using var bmp = PixelAssert.Render(SeparationPdf("/Spot", "1"));

        var c = PixelAssert.At(bmp, 50, 50);
        Assert.True(c.Blue > 200 && c.Red < 60, $"tint transform should paint blue, got {c}");
    }

    /// <summary>
    /// Table 71: <c>/Subtype /NChannel</c> adds colorant identities and mixing hints that this
    /// renderer does not model. The colour still comes from the tint transform — which is correct
    /// as far as it goes — so the approximation has to be reported or it is invisible.
    /// </summary>
    [Fact]
    public void DeviceNWithNChannelAttributes_IsReportedAndStillRendersTheTint()
    {
        byte[] pdf = NChannelPdf();
        var reports = RenderAndCollect(pdf);

        Assert.Contains(reports, d => d.Contains("NChannel"));

        using var bmp = PixelAssert.Render(pdf);
        var c = PixelAssert.At(bmp, 50, 50);
        Assert.True(c.Red > 200 && c.Blue < 60, $"tint transform should still paint red, got {c}");
    }

    // ====================================================================
    //  Fixtures
    // ====================================================================

    /// <summary>Renders the page and returns the details of every RenderParameterIgnored report.</summary>
    /// <remarks>
    /// The sink filters to the calling thread. <c>RenderDiagnostics.OnEvent</c> is process-wide, so
    /// an unfiltered handler also receives whatever xUnit is running in parallel — which turns
    /// <see cref="NoIgnoredParameters_ReportsNothing"/> into "no other test emitted
    /// RenderParameterIgnored while I ran". That was already a latent flake and every new reporting
    /// site raises the odds; the render below is synchronous, so its own diagnostics are raised on
    /// this thread and no others are.
    /// </remarks>
    private static List<string> RenderAndCollect(byte[] pdf)
    {
        var reports = new List<string>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            if (kind == RenderDiagnosticKind.RenderParameterIgnored)
                lock (reports) reports.Add(detail ?? "");
        }

        RenderDiagnostics.OnEvent += Sink;
        try
        {
            using var ms = new MemoryStream(pdf);
            PdfOperations.RenderPageToPng(ms, 0, dpi: PixelAssert.Dpi);
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        lock (reports) return new List<string>(reports);
    }

    private static byte[] CalGrayPdf(string gamma) =>
        ColorSpacePdf($"[/CalGray << /WhitePoint [0.9505 1.0 1.089]" +
                      (gamma != null ? $" /Gamma {gamma}" : "") + " >>]",
                      "/CS0 cs 0.5 sc 0 0 100 100 re f");

    private static byte[] CalGrayStrokePdf(string gamma) =>
        ColorSpacePdf($"[/CalGray << /WhitePoint [0.9505 1.0 1.089] /Gamma {gamma} >>]",
                      "/CS0 CS 0.5 SC 20 w 0 50 m 100 50 l S");

    private static byte[] LabPdf(string extraParams, string operands) =>
        ColorSpacePdf($"[/Lab << /WhitePoint [0.9642 1.0 0.8251] {extraParams} >>]",
                      $"/CS0 cs {operands} sc 0 0 100 100 re f");

    private static byte[] LabImagePdf(string extraParams, string hex) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 100 0 0 100 0 0 cm /Im1 Do Q"),
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8" +
                   $" /ColorSpace [/Lab << /WhitePoint [0.9642 1.0 0.8251] {extraParams} >>]" +
                   " /Filter /ASCIIHexDecode", hex));

    /// <summary>Palette of four entries — black, red, green, blue — selected by <c>sc</c>.</summary>
    private static byte[] IndexedPdf(string operand) =>
        ColorSpacePdf("[/Indexed /DeviceRGB 3 <000000FF000000FF000000FF>]",
                      $"/CS0 cs {operand} sc 0 0 100 100 re f");

    private static byte[] Cmyk16Pdf(string decodeEntry) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 100 0 0 100 0 0 cm /Im1 Do Q"),
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceCMYK" +
                   $" /BitsPerComponent 16 {decodeEntry} /Filter /ASCIIHexDecode",
                   "0000000000000000>"));

    private static byte[] SeparationPdf(string colorant, string tint) =>
        ColorSpacePdf($"[/Separation {colorant} /DeviceRGB" +
                      " << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [0 0 1] /N 1 >>]",
                      $"/CS0 cs {tint} sc 0 0 100 100 re f");

    /// <summary>
    /// The same Separation as <c>SeparationPdf("/All", "1")</c>, except the colorant name is a
    /// reference to object 5 rather than a direct <c>/All</c>.
    /// </summary>
    private static byte[] IndirectAllSeparationPdf() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources"
            + " << /ColorSpace << /CS0 [/Separation 5 0 R /DeviceRGB"
            + " << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [0 0 1] /N 1 >>] >> >> >>",
            Stream("", "/CS0 cs 1 sc 0 0 100 100 re f"),
            "/All");

    private static byte[] NChannelPdf() =>
        ColorSpacePdf(
            "[/DeviceN [/Cyan /Magenta] /DeviceRGB" +
            " << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [1 0 0] /N 1 >>" +
            " << /Subtype /NChannel /Colorants << /Cyan [/Separation /Cyan /DeviceRGB" +
            " << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [0 1 1] /N 1 >>] >>" +
            " /MixingHints << /Solidities << /Cyan 1.0 >> >> >>]",
            "/CS0 cs 1 1 sc 0 0 100 100 re f");

    private static byte[] ExtGStatePdf(string extGsEntries) =>
        ContentPdf("q /GS1 gs 0 0 100 100 re f Q",
                   $"/ExtGState << /GS1 << /Type /ExtGState {extGsEntries} >> >>");

    private static byte[] ColorSpacePdf(string csDef, string content) =>
        ContentPdf(content, $"/ColorSpace << /CS0 {csDef} >>");

    private static byte[] ContentPdf(string content, string resourceEntries) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            $" /Resources << {resourceEntries} >> >>",
            Stream("", content));

    /// <summary>
    /// A Type 0 function of <paramref name="inputs"/> inputs over a 2×2×… table whose only
    /// non-zero sample is the all-ones corner, so the value at any point is the product of the
    /// per-axis fractions — an arithmetic identity the test can predict without re-deriving the
    /// interpolation.
    /// </summary>
    private static PdfDictionary SampledFunction(int inputs, byte cornerValue)
    {
        var domain = new StringBuilder();
        var size   = new StringBuilder();
        for (int i = 0; i < inputs; i++) { domain.Append("0 1 "); size.Append("2 "); }

        int sampleCount = 1 << inputs;
        var hex = new StringBuilder(sampleCount * 2 + 1);
        for (int i = 0; i < sampleCount; i++)
            hex.Append((i == sampleCount - 1 ? cornerValue : (byte)0).ToString("X2"));
        hex.Append('>');

        return ParseSingleObject(
            $"<< /FunctionType 0 /Domain [{domain.ToString().Trim()}] /Range [0 1]" +
            $" /Size [{size.ToString().Trim()}] /BitsPerSample 8" +
            $" /Filter /ASCIIHexDecode /Length {hex.Length} >>\nstream\n{hex}\nendstream");
    }

    /// <summary>Parses a one-object PDF and returns that object, so fixtures stay literal PDF syntax.</summary>
    private static PdfDictionary ParseSingleObject(string objectBody)
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Fn 4 0 R >>",
            objectBody);

        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var fn = doc.ResolveObject(doc.Pages[0].Dictionary[new PdfName("Fn")]) as PdfDictionary;
        Assert.NotNull(fn);
        return fn!;
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
