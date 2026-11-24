// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Operations;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Alpha-is-shape (<c>/AIS</c>, ISO 32000-1 §11.6.4.4): whether the alpha constants and the soft
/// mask supply an element's shape or its opacity.
/// </summary>
/// <remarks>
/// <para><b>Where it can be seen.</b> The compositing formulas use only the product of shape and
/// opacity, so outside a knockout group the two readings give the same pixel. Inside one they part:
/// an element whose alpha is opacity replaces what the group holds (it composites with the group's
/// transparent initial backdrop), while one whose alpha is shape is mixed in by it — §11.4.5.6's
/// <c>(1 − f)·C<sub>prev</sub> + f·C</c>, which is SrcOver.</para>
///
/// <para><b>The fixture</b> is <c>KnockoutTransparencyGroupTests</c>' — an opaque red square, then
/// a blue one at <c>ca 0.5</c> overlapping it, in a transparency group. In the overlap, blue
/// knocked out against the transparent backdrop lands on the white page as light blue (green ≈
/// 127); blue mixed over red by its shape is purple (green ≈ 0).</para>
/// </remarks>
public class AlphaIsShapeTests
{
    private const float OverlapX = 175, OverlapY = 175;

    [Fact]
    public void InAKnockoutGroup_AlphaAsShape_CompositesOverTheEarlierElement()
    {
        SKColor overlap = At(Squares(knockout: true, gs0: "/ca 0.5 /AIS true"));

        Assert.InRange(overlap.Green, 0, 40);      // red beneath, mixed by blue's shape: no green
        Assert.InRange(overlap.Red, 100, 160);
        Assert.InRange(overlap.Blue, 100, 160);
    }

    /// <summary>The control: the same group under <c>/AIS false</c> knocks the red out.</summary>
    [Fact]
    public void InAKnockoutGroup_AlphaAsOpacity_ReplacesTheEarlierElement()
    {
        SKColor overlap = At(Squares(knockout: true, gs0: "/ca 0.5 /AIS false"));

        Assert.InRange(overlap.Green, 100, 160);   // the page's white through blue at 0.5
        Assert.InRange(overlap.Blue, 220, 255);
    }

    /// <summary>
    /// Outside a knockout group the flag changes nothing: shape and opacity enter compositing only
    /// as their product. The overlap must still be the blended purple, not the white page.
    /// </summary>
    [Fact]
    public void OutsideAKnockoutGroup_AlphaAsShapeAndAsOpacity_GiveTheSamePixel()
    {
        SKColor shape = At(Squares(knockout: false, gs0: "/ca 0.5 /AIS true"));
        SKColor opacity = At(Squares(knockout: false, gs0: "/ca 0.5 /AIS false"));

        Assert.Equal(opacity, shape);
        Assert.InRange(shape.Green, 0, 40);
        Assert.InRange(shape.Blue, 100, 160);
    }

    /// <summary>
    /// A soft-masked fill in a knockout group under <c>/AIS false</c> knocks out: the mask is
    /// opacity, so blue at <c>ca 0.5</c> replaces the red rather than merging over it through the
    /// masked layer: the shader route takes the knockout paint (Src) as well.
    /// </summary>
    [Fact]
    public void ASoftMaskedFillInAKnockoutGroup_UnderAlphaAsOpacity_KnocksOut()
    {
        SKColor overlap = At(Squares(knockout: true, gs0: "/ca 0.5 /AIS false " + Mask, extra: WhiteMaskGroup));

        Assert.InRange(overlap.Green, 100, 160);
        Assert.InRange(overlap.Blue, 220, 255);
    }

    /// <summary>The same fill under <c>/AIS true</c>: the mask and <c>ca</c> are shape, so it composites over.</summary>
    [Fact]
    public void ASoftMaskedFillInAKnockoutGroup_UnderAlphaAsShape_CompositesOver()
    {
        SKColor overlap = At(Squares(knockout: true, gs0: "/ca 0.5 /AIS true " + Mask, extra: WhiteMaskGroup));

        Assert.InRange(overlap.Green, 0, 40);
        Assert.InRange(overlap.Blue, 100, 160);
    }

    /// <summary>
    /// A soft-masked shading knocks out in a knockout group under <c>/AIS false</c>: the shading's
    /// shader is composed with the mask's, which replaces the group within the shading's clip at the
    /// mask's opacity (0.5). Through the masked layer it merged over the red instead. (This case
    /// asserted that report until shader paints took the mask-shader route.)
    /// </summary>
    /// <remarks>
    /// The second case puts the 0.5 in <c>ca</c> under a white mask: a shader paint keeps its
    /// colour on the paint, so Skia applies that alpha to the shader's output once.
    /// </remarks>
    [Theory]
    [InlineData("/AIS false", true)]
    [InlineData("/ca 0.5 /AIS false", false)]
    public void ASoftMaskedShadingInAKnockoutGroup_KnocksOut(string gs0, bool greyMask)
    {
        SKColor overlap = At(Squares(knockout: true, gs0: gs0 + " " + Mask, content: MaskedShading,
                                     extra: greyMask ? GreyMaskGroup : WhiteMaskGroup, resources: BlueShading));

        Assert.InRange(overlap.Green, 100, 160);   // blue at 0.5 over the page's white, red gone
        Assert.InRange(overlap.Blue, 220, 255);
    }

    /// <summary>
    /// A soft-masked fill with a shading pattern composes the pattern's shader with the mask's,
    /// and so knocks out too.
    /// </summary>
    [Fact]
    public void ASoftMaskedPatternFillInAKnockoutGroup_KnocksOut()
    {
        const string content = "1 0 0 rg 100 100 100 100 re f\n/GS0 gs\n/Pattern cs /P0 scn 150 150 100 100 re f";
        const string pattern =
            "/Pattern << /P0 << /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceRGB " +
            "/Coords [150 0 250 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [0 0 1] /C1 [0 0 1] /N 1 >> >> >> >> ";
        SKColor overlap = At(Squares(knockout: true, gs0: "/AIS false " + Mask, content: content,
                                     extra: GreyMaskGroup, resources: pattern));

        Assert.InRange(overlap.Green, 100, 160);
        Assert.InRange(overlap.Blue, 220, 255);
    }

    /// <summary>
    /// A soft-masked image knocks out in a knockout group under <c>/AIS false</c>: it is drawn
    /// through the mask shader, which replaces the group within the image at the mask's opacity
    /// (0.5 here). A masked layer would merge over the red instead — so this is also what shows the
    /// shader route is the one taken.
    /// </summary>
    [Fact]
    public void ASoftMaskedImageInAKnockoutGroup_KnocksOut()
    {
        SKColor overlap = At(Squares(knockout: true, gs0: "/AIS false " + Mask, content: MaskedImage, extra: GreyMaskGroup));

        Assert.InRange(overlap.Green, 100, 160);   // blue at 0.5 over the page's white, red gone
        Assert.InRange(overlap.Blue, 220, 255);
    }

    /// <summary>
    /// Outside a knockout group the mask still applies: blue at the mask's 0.5 over the red is the
    /// blended purple, as the layer route drew it.
    /// </summary>
    [Fact]
    public void ASoftMaskedImage_IsDrawnAtTheMasksOpacity()
    {
        SKColor overlap = At(Squares(knockout: false, gs0: "/AIS false " + Mask, content: MaskedImage, extra: GreyMaskGroup));

        Assert.InRange(overlap.Green, 0, 40);
        Assert.InRange(overlap.Red, 100, 160);
        Assert.InRange(overlap.Blue, 100, 160);
    }

    /// <summary>
    /// A group composited into a knockout group under <c>/AIS true</c> is reported: one layer holds
    /// its shape and opacity only as their product. Under <c>/AIS false</c> the Src emulation is
    /// exact and nothing is said.
    /// </summary>
    [Fact]
    public void AGroupCompositedIntoAKnockoutGroupUnderAlphaAsShape_IsReported()
    {
        string content = "1 0 0 rg 100 100 100 100 re f\n/GS0 gs\n/Fm1 Do";
        string inner = FormPdfFixtures.StreamBody(
            "/Type /XObject /Subtype /Form /BBox [0 0 300 300] /Group << /S /Transparency >>",
            "0 0 1 rg 150 150 100 100 re f");

        var shape = Collect(Squares(knockout: true, gs0: "/ca 0.5 /AIS true", content: content, extra: inner));
        var opacity = Collect(Squares(knockout: true, gs0: "/ca 0.5 /AIS false", content: content, extra: inner));
        var plain = Collect(Squares(knockout: false, gs0: "/ca 0.5 /AIS true", content: content, extra: inner));

        Assert.Contains(shape, d => d.Contains("knockout group under /AIS true"));
        Assert.DoesNotContain(opacity, d => d.Contains("knockout group under /AIS true"));
        Assert.DoesNotContain(plain, d => d.Contains("knockout group under /AIS true"));
    }

    /// <summary>
    /// The mask is sampled where it lies on the page, not in the image's own pixels: a mask white
    /// left of x = 200 and black right of it shows the image's left half and hides its right.
    /// </summary>
    [Fact]
    public void ASoftMaskedImage_TakesTheMaskWhereItLiesOnThePage()
    {
        string halfMask = FormPdfFixtures.StreamBody(
            "/Type /XObject /Subtype /Form /BBox [0 0 300 300] /Group << /S /Transparency /CS /DeviceGray >>",
            "0 g 0 0 300 300 re f 1 g 0 0 200 300 re f");
        using var bmp = PixelAssert.Render(Squares(knockout: false, gs0: "/AIS false " + Mask,
                                                   content: MaskedImage, extra: halfMask));

        SKColor shown = PixelAssert.At(bmp, 175, 175);   // mask 1: blue over red
        SKColor imageOnly = PixelAssert.At(bmp, 190, 240);   // mask 1, image only: blue
        SKColor masked = PixelAssert.At(bmp, 225, 225);  // mask 0: the page

        Assert.InRange(shown.Blue, 220, 255);
        Assert.InRange(shown.Red, 0, 40);
        Assert.InRange(imageOnly.Blue, 220, 255);
        Assert.Equal(SKColors.White, masked);
    }

    /// <summary>
    /// A soft-masked paint keeps its blend mode (§11.3.5, §11.6.4.3): blue multiplied over red is
    /// black, for a fill, an image and a shading alike. The masked layer blended each against the
    /// layer's transparency — where every mode is the source colour — and merged it Normal.
    /// </summary>
    [Theory]
    [InlineData("fill")]
    [InlineData("image")]
    [InlineData("shading")]
    public void ASoftMaskedPaint_KeepsItsBlendMode(string paint)
    {
        string content = paint switch
        {
            "fill" => "1 0 0 rg 100 100 100 100 re f\n/GS0 gs\n0 0 1 rg 150 150 100 100 re f",
            "image" => MaskedImage,
            _ => MaskedShading,
        };
        SKColor overlap = At(Squares(knockout: false, gs0: "/BM /Multiply " + Mask, content: content,
                                     extra: WhiteMaskGroup, resources: BlueShading));

        Assert.InRange(overlap.Red, 0, 40);
        Assert.InRange(overlap.Green, 0, 40);
        Assert.InRange(overlap.Blue, 0, 40);
    }

    /// <summary>
    /// In a knockout group the blend mode does not apply — the element composites with the
    /// transparent initial backdrop — so a soft-masked shading under <c>/AIS true</c> and
    /// <c>/BM /Multiply</c> merges SrcOver: opaque blue, not blue multiplied into the red.
    /// </summary>
    [Fact]
    public void ASoftMaskedShadingInAKnockoutGroup_IgnoresTheBlendMode()
    {
        SKColor overlap = At(Squares(knockout: true, gs0: "/AIS true /BM /Multiply " + Mask, content: MaskedShading,
                                     extra: WhiteMaskGroup, resources: BlueShading));

        Assert.InRange(overlap.Red, 0, 40);
        Assert.InRange(overlap.Blue, 220, 255);
    }

    // ================================================================

    private const string Mask = "/SMask << /Type /Mask /S /Luminosity /G 7 0 R >>";

    /// <summary>The red square, then a blue inline image over the overlap under <c>/GS0</c>.</summary>
    private const string MaskedImage =
        "1 0 0 rg 100 100 100 100 re f\n/GS0 gs\n" +
        "q 100 0 0 100 150 150 cm BI /W 1 /H 1 /CS /RGB /BPC 8 /F /AHx ID 0000FF> EI Q";

    /// <summary>The red square, then a blue shading over the overlap under <c>/GS0</c>.</summary>
    private const string MaskedShading =
        "1 0 0 rg 100 100 100 100 re f\n/GS0 gs\nq 150 150 100 100 re W n /Sh0 sh Q";

    /// <summary>A blue axial shading, <c>/Sh0</c>.</summary>
    private const string BlueShading =
        "/Shading << /Sh0 << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [150 0 250 0] " +
        "/Function << /FunctionType 2 /Domain [0 1] /C0 [0 0 1] /C1 [0 0 1] /N 1 >> >> >> ";

    /// <summary>A luminosity mask group painting mid grey everywhere: mask value 0.5.</summary>
    private static readonly string GreyMaskGroup = FormPdfFixtures.StreamBody(
        "/Type /XObject /Subtype /Form /BBox [0 0 300 300] /Group << /S /Transparency /CS /DeviceGray >>",
        "0.5 g 0 0 300 300 re f");

    /// <summary>A luminosity mask group painting white everywhere: mask value 1.</summary>
    private static readonly string WhiteMaskGroup = FormPdfFixtures.StreamBody(
        "/Type /XObject /Subtype /Form /BBox [0 0 300 300] /Group << /S /Transparency /CS /DeviceGray >>",
        "1 g 0 0 300 300 re f");

    /// <summary>
    /// A page drawing one transparency group (object 5), whose ExtGState <c>/GS0</c> (object 6)
    /// carries <paramref name="gs0"/>; <paramref name="extra"/>, when given, is object 7 and is
    /// reachable as the group's <c>/Fm1</c>.
    /// </summary>
    private static byte[] Squares(bool knockout, string gs0, string content = null, string extra = null,
                                  string resources = "")
    {
        content ??= "1 0 0 rg 100 100 100 100 re f\n/GS0 gs\n0 0 1 rg 150 150 100 100 re f";
        string xobjects = extra != null ? "/XObject << /Fm1 7 0 R >> " : "";

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 300] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R " +
            "/Resources << /XObject << /Fm0 5 0 R >> >> >>",
            FormPdfFixtures.StreamBody(string.Empty, "/Fm0 Do"),
            FormPdfFixtures.StreamBody(
                "/Type /XObject /Subtype /Form /BBox [0 0 300 300] " +
                $"/Resources << /ExtGState << /GS0 6 0 R >> {xobjects}{resources}>> " +
                $"/Group << /S /Transparency /CS /DeviceRGB /I true /K {(knockout ? "true" : "false")} >>",
                content),
            $"<< /Type /ExtGState {gs0} >>",
        };
        if (extra != null) bodies.Add(extra);
        return FormPdfFixtures.Build(bodies.ToArray());
    }

    private static SKColor At(byte[] pdf)
    {
        using var bmp = PixelAssert.Render(pdf);
        return PixelAssert.At(bmp, OverlapX, OverlapY);
    }

    /// <summary>The <c>TransparencyGroupApproximated</c> details one render reports, on this thread.</summary>
    private static List<string> Collect(byte[] pdf)
    {
        var reports = new List<string>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            if (kind == RenderDiagnosticKind.TransparencyGroupApproximated)
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
}
