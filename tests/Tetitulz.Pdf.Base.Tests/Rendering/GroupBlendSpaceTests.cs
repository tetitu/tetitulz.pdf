// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A transparency group's blending colour space (§11.6.6 Table 147 <c>/CS</c>, §11.3.4): the
/// raster renderer composites a grey or CMYK group in its space through <see cref="GroupBlenders"/>,
/// so the pixels §11.3.6's formula gives in ink differ from the ones it gives in light wherever a
/// paint is translucent or blended; a grey group converts what is painted into it; the page's own
/// group is the root; an inherited space reaches a nested group; a soft-masked paint goes through
/// it; a space the standard does not admit is reported and inherited past; a separation plate,
/// which composites per colorant already, is left alone. The SVG export composites in sRGB and
/// says so.
/// </summary>
public class GroupBlendSpaceTests
{
    /// <summary>Half cyan over black: in CMYK the alpha weights interpolate inks — (0.5, 0, 0, 0.5)
    /// is (0.25, 0.5, 0.5) — where RGB interpolates light to (0, 0.5, 0.5).</summary>
    private static readonly SKColor HalfCyanOverBlackInCmyk = new SKColor(64, 128, 128);
    private static readonly SKColor HalfCyanOverBlackInRgb  = new SKColor(0, 128, 128);

    private const string HalfCyanOverBlack = "0 0 0 1 k 0 0 100 100 re f /Half gs 1 0 0 0 k 0 0 100 100 re f";

    [Fact]
    public void EveryGroupBlender_Compiles_AndTheCompilerRefusesABrokenProgram()
    {
        foreach (GroupBlendSpace space in new[] { GroupBlendSpace.Gray, GroupBlendSpace.Cmyk })
            foreach (SKBlendMode mode in Enum.GetValues(typeof(SKBlendMode)))
            {
                if (GroupBlenders.IsPdfMode(mode)) Assert.NotNull(GroupBlenders.For(space, mode));
                else Assert.Null(GroupBlenders.For(space, mode));
            }
        Assert.Null(GroupBlenders.For(GroupBlendSpace.Rgb, SKBlendMode.Multiply));

        // The control: the compiler the sixteen programs went through refuses a broken one.
        using var broken = SKRuntimeEffect.CreateBlender("half4 main(half4 s, half4 d) { return nonsense; }", out string errors);
        Assert.Null(broken);
        Assert.False(string.IsNullOrEmpty(errors));
    }

    /// <summary>
    /// The mask-shader route (<c>TryDrawWithMaskShader</c>) takes a paint whose mode reads as
    /// SrcOver or Src; a paint carrying a group blender must read as SrcOver, so a soft-masked
    /// paint inside a CMYK group goes through the mask shader with the blender in place.
    /// </summary>
    [Fact]
    public void APaintCarryingAGroupBlender_ReadsAsSrcOver()
    {
        using var paint = new SKPaint { BlendMode = SKBlendMode.Multiply };
        Assert.Equal(SKBlendMode.Multiply, paint.BlendMode);
        paint.Blender = GroupBlenders.For(GroupBlendSpace.Cmyk, SKBlendMode.Multiply);
        Assert.Equal(SKBlendMode.SrcOver, paint.BlendMode);
    }

    [Fact]
    public void ACmykGroup_InterpolatesATranslucentPaintInInk_AndAnRgbGroupInLight()
    {
        using var cmyk = PixelAssert.Render(GroupPdf("/DeviceCMYK", HalfCyanOverBlack));
        using var rgb  = PixelAssert.Render(GroupPdf("/DeviceRGB", HalfCyanOverBlack));
        AssertNear(HalfCyanOverBlackInCmyk, PixelAssert.At(cmyk, 50, 50));
        AssertNear(HalfCyanOverBlackInRgb,  PixelAssert.At(rgb, 50, 50));
    }

    /// <summary>§11.3.4: a subtractive space's blend function runs on the complements. Screen of
    /// full cyan over half black: every complement screens to 1, so no ink is left — white — where
    /// RGB's screen of cyan over mid-grey is a light cyan.</summary>
    [Fact]
    public void ACmykGroup_RunsScreenOnTheInksComplements()
    {
        const string content = "0 0 0 0.5 k 0 0 100 100 re f /Screen gs 1 0 0 0 k 0 0 100 100 re f";
        using var cmyk = PixelAssert.Render(GroupPdf("/DeviceCMYK", content));
        using var rgb  = PixelAssert.Render(GroupPdf("/DeviceRGB", content));
        AssertNear(new SKColor(255, 255, 255), PixelAssert.At(cmyk, 50, 50));
        AssertNear(new SKColor(128, 255, 255), PixelAssert.At(rgb, 50, 50));
    }

    /// <summary>§11.3.5.3: in CMYK the non-separable modes run on the complements of C, M and Y as
    /// RGB, and K comes from the source for Luminosity (from the backdrop for the other three).
    /// Half black under Luminosity over full cyan: the CMY complements, cyan's (0, 1, 1) set to the
    /// source's luminosity 1 (a K-only colour has no CMY ink — its complement is white), clip to
    /// white, and K is the source's 0.5 — a neutral mid grey. In RGB, SetLum(cyan, 0.5) is a cyan
    /// of luminosity 0.5, (0, 0.71, 0.71).</summary>
    [Fact]
    public void ACmykGroup_TakesLuminositysBlackFromTheSource()
    {
        const string content = "1 0 0 0 k 0 0 100 100 re f /Lum gs 0 0 0 0.5 k 0 0 100 100 re f";
        using var cmyk = PixelAssert.Render(GroupPdf("/DeviceCMYK", content));
        using var rgb  = PixelAssert.Render(GroupPdf("/DeviceRGB", content));
        AssertNear(new SKColor(128, 128, 128), PixelAssert.At(cmyk, 50, 50));
        AssertNear(new SKColor(0, 182, 182), PixelAssert.At(rgb, 50, 50), tolerance: 4);
    }

    [Theory]
    [InlineData("/DeviceGray")]
    [InlineData("[/CalGray << /WhitePoint [0.9505 1.0 1.089] >>]")]
    public void AGreyGroup_ConvertsWhatIsPaintedIntoIt(string cs)
    {
        const string red = "1 0 0 rg 0 0 100 100 re f";
        using var grey = PixelAssert.Render(GroupPdf(cs, red));
        using var rgb  = PixelAssert.Render(GroupPdf("/DeviceRGB", red));
        // BT.601: red's grey is 0.299.
        AssertNear(new SKColor(76, 76, 76), PixelAssert.At(grey, 50, 50));
        AssertNear(new SKColor(255, 0, 0), PixelAssert.At(rgb, 50, 50));
    }

    /// <summary>§11.6.6: the page group's <c>/CS</c> is honoured whatever its <c>/I</c>, and page
    /// content composites in it — the classic "the whole page renders grey" file.</summary>
    [Fact]
    public void ThePageGroupsSpace_IsTheRoot_ForPageContent()
    {
        using var grey = PixelAssert.Render(PagePdf("/Group << /S /Transparency /CS /DeviceGray >>", "1 0 0 rg 0 0 100 100 re f"));
        AssertNear(new SKColor(76, 76, 76), PixelAssert.At(grey, 50, 50));

        using var cmyk = PixelAssert.Render(PagePdf("/Group << /S /Transparency /CS /DeviceCMYK >>", HalfCyanOverBlack));
        AssertNear(HalfCyanOverBlackInCmyk, PixelAssert.At(cmyk, 50, 50));

        using var none = PixelAssert.Render(PagePdf("", HalfCyanOverBlack));
        AssertNear(HalfCyanOverBlackInRgb, PixelAssert.At(none, 50, 50));
    }

    /// <summary>A group that names no space inherits its parent's (§11.6.6, default value).</summary>
    [Fact]
    public void AGroupNamingNoSpace_InheritsItsParents()
    {
        byte[] pdf = NestedPdf(outerCs: "/DeviceCMYK",
            outer: "0 0 0 1 k 0 0 100 100 re f /In Do",
            inner: "/Half gs 1 0 0 0 k 0 0 100 100 re f");
        using var bmp = PixelAssert.Render(pdf);
        AssertNear(HalfCyanOverBlackInCmyk, PixelAssert.At(bmp, 50, 50));

        byte[] control = NestedPdf(outerCs: "/DeviceRGB",
            outer: "0 0 0 1 k 0 0 100 100 re f /In Do",
            inner: "/Half gs 1 0 0 0 k 0 0 100 100 re f");
        using var rgb = PixelAssert.Render(control);
        AssertNear(HalfCyanOverBlackInRgb, PixelAssert.At(rgb, 50, 50));
    }

    /// <summary>A soft-masked paint reaches the group through the mask-shader route with the
    /// group's blender in place: a 50 % luminosity mask on cyan over black is half cyan in ink.</summary>
    [Fact]
    public void ASoftMaskedPaint_CompositesInTheGroupsSpace()
    {
        const string content = "0 0 0 1 k 0 0 100 100 re f /Mask gs 1 0 0 0 k 0 0 100 100 re f";
        using var cmyk = PixelAssert.Render(GroupPdf("/DeviceCMYK", content));
        using var rgb  = PixelAssert.Render(GroupPdf("/DeviceRGB", content));
        AssertNear(HalfCyanOverBlackInCmyk, PixelAssert.At(cmyk, 50, 50), tolerance: 4);
        AssertNear(HalfCyanOverBlackInRgb,  PixelAssert.At(rgb, 50, 50), tolerance: 4);
    }

    /// <summary>A soft-masked paint under a blend mode: in an RGB group its native mode keeps it off
    /// the mask-shader route (which takes SrcOver and Src alone) and on the masked layer, whose merge
    /// blends; in a CMYK group the paint carries the Screen blender and reads as SrcOver, so it takes
    /// the mask shader with the blender in place. Cyan, Screen, half masked, over half black: in
    /// CMYK the screen leaves no ink and the mask lets half the black back, a light grey; in RGB a
    /// light cyan over grey. (The masked layer's own merge is reached in a non-RGB group only by a
    /// draw whose matrix has no inverse, which paints nothing — its blend is applied for consistency.)</summary>
    [Fact]
    public void ASoftMaskedBlendedPaint_CompositesInTheGroupsSpace_ThroughTheMaskShader()
    {
        const string content = "0 0 0 0.5 k 0 0 100 100 re f /MaskScreen gs 1 0 0 0 k 0 0 100 100 re f";
        using var cmyk = PixelAssert.Render(GroupPdf("/DeviceCMYK", content));
        using var rgb  = PixelAssert.Render(GroupPdf("/DeviceRGB", content));
        AssertNear(new SKColor(191, 191, 191), PixelAssert.At(cmyk, 50, 50), tolerance: 4);
        AssertNear(new SKColor(128, 191, 191), PixelAssert.At(rgb, 50, 50), tolerance: 4);
    }

    /// <summary>A group's own result composites onto its parent in the <i>parent's</i> space: a
    /// form of opaque cyan drawn at <c>ca 0.5</c> over black on a CMYK page interpolates in ink.</summary>
    [Fact]
    public void AGroupsResult_CompositesOntoItsParentInTheParentsSpace()
    {
        using var cmyk = PixelAssert.Render(PageWithFormPdf("/Group << /S /Transparency /CS /DeviceCMYK >>",
            "0 0 0 1 k 0 0 100 100 re f /Half gs /Fx Do", "1 0 0 0 k 0 0 100 100 re f"));
        using var rgb = PixelAssert.Render(PageWithFormPdf("",
            "0 0 0 1 k 0 0 100 100 re f /Half gs /Fx Do", "1 0 0 0 k 0 0 100 100 re f"));
        AssertNear(HalfCyanOverBlackInCmyk, PixelAssert.At(cmyk, 50, 50));
        AssertNear(HalfCyanOverBlackInRgb,  PixelAssert.At(rgb, 50, 50));
    }

    /// <summary>
    /// The shortcut's liveness: an opaque Normal paint inside a CMYK group keeps Skia's fixed mode
    /// (no blender asked for — the pixel is the same in every space), a translucent one goes
    /// through the group's blender. No pixel can show the first, so the request count does.
    /// </summary>
    [Fact]
    public void AnOpaqueNormalPaintInACmykGroup_TakesNoBlender_ATranslucentOneDoes()
    {
        int before = GroupBlenders.RequestsOnThisThread;
        using (PixelAssert.Render(GroupPdf("/DeviceCMYK", "0 0 0 1 k 0 0 100 100 re f 1 0 0 0 k 10 10 50 50 re f"))) { }
        Assert.Equal(before, GroupBlenders.RequestsOnThisThread);

        using (PixelAssert.Render(GroupPdf("/DeviceCMYK", HalfCyanOverBlack))) { }
        Assert.True(GroupBlenders.RequestsOnThisThread > before, "the translucent paint asked for no blender");
    }

    /// <summary>A separation plate composites per colorant already — the group's space must not
    /// convert the plate's greys. The plates of a CMYK group are the plates of the same content
    /// in an RGB group, and no blender is asked for.</summary>
    [Fact]
    public void ASeparationPlate_IsLeftAlone_ByTheGroupsSpace()
    {
        int before = GroupBlenders.RequestsOnThisThread;
        PdfSeparations cmyk = PdfSeparationRenderer.RenderPage(new MemoryStream(GroupPdf("/DeviceCMYK", HalfCyanOverBlack)), 0, 72);
        Assert.Equal(before, GroupBlenders.RequestsOnThisThread);
        PdfSeparations rgb = PdfSeparationRenderer.RenderPage(new MemoryStream(GroupPdf("/DeviceRGB", HalfCyanOverBlack)), 0, 72);

        Assert.Equal(rgb["Cyan"].Tints, cmyk["Cyan"].Tints);
        Assert.Equal(rgb["Black"].Tints, cmyk["Black"].Tints);
        // Potency: the cyan plate holds half ink at the centre, the black plate half too (the
        // translucent cyan over black lets half the black through on its plate).
        byte centre = cmyk["Cyan"].Tints[(cmyk.Height / 2) * cmyk.Width + cmyk.Width / 2];
        Assert.InRange(centre, 100, 156);
    }

    [Fact]
    public void TheRaster_HonoursGreyAndCmyk_AndReportsASpaceTheStandardDoesNotAdmit()
    {
        Assert.DoesNotContain(RasterKinds(GroupPdf("/DeviceCMYK", HalfCyanOverBlack)), d => d.Kind == RenderDiagnosticKind.TransparencyGroupApproximated);
        Assert.DoesNotContain(RasterKinds(GroupPdf("/DeviceGray", HalfCyanOverBlack)), d => d.Kind == RenderDiagnosticKind.TransparencyGroupApproximated);

        var lab = RasterKinds(GroupPdf("[/Lab << /WhitePoint [0.9505 1.0 1.089] >>]", HalfCyanOverBlack));
        Assert.Contains(lab, d => d.Kind == RenderDiagnosticKind.TransparencyGroupApproximated && d.Detail.Contains("/Lab", StringComparison.Ordinal));

        var pageLab = RasterKinds(PagePdf("/Group << /S /Transparency /CS [/Lab << /WhitePoint [0.9505 1.0 1.089] >>] >>", HalfCyanOverBlack));
        Assert.Contains(pageLab, d => d.Kind == RenderDiagnosticKind.TransparencyGroupApproximated && d.Detail.StartsWith("page group:", StringComparison.Ordinal));
    }

    /// <summary>
    /// An ICCBased grey or CMYK group is admitted by its arity and composited in the device family —
    /// never through its profile — and the raster path says so rather than approximate in
    /// silence. An ICCBased RGB group composites in the canvas's own space and raises nothing; the
    /// device spaces are the control above. The page's own group is reported the same way.
    /// </summary>
    [Fact]
    public void TheRaster_ReportsAnIccBasedGreyOrCmykGroup_AsCompositedThroughTheDeviceModel()
    {
        var cmyk = RasterKinds(IccGroupPdf(4, HalfCyanOverBlack));
        var hit = Assert.Single(cmyk, d => d.Kind == RenderDiagnosticKind.TransparencyGroupApproximated);
        Assert.Contains("/ICCBased (/N 4)", hit.Detail, StringComparison.Ordinal);
        Assert.Contains("not through its ICC profile", hit.Detail, StringComparison.Ordinal);

        var grey = RasterKinds(IccGroupPdf(1, HalfCyanOverBlack));
        Assert.Contains(grey, d => d.Kind == RenderDiagnosticKind.TransparencyGroupApproximated
                                   && d.Detail.Contains("device grey", StringComparison.Ordinal));

        Assert.DoesNotContain(RasterKinds(IccGroupPdf(3, HalfCyanOverBlack)),
                              d => d.Kind == RenderDiagnosticKind.TransparencyGroupApproximated);

        var page = RasterKinds(PagePdf("/Group << /S /Transparency /CS [/ICCBased 8 0 R] >>", HalfCyanOverBlack, iccN: 4));
        Assert.Contains(page, d => d.Kind == RenderDiagnosticKind.TransparencyGroupApproximated
                                   && d.Detail.StartsWith("page group: transparency group /CS /ICCBased", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSvgExport_ReportsAGreyOrCmykGroup_AndThePageGroupToo()
    {
        Assert.Contains(SvgKinds(GroupPdf("/DeviceCMYK", HalfCyanOverBlack)), d => d.Kind == RenderDiagnosticKind.TransparencyGroupApproximated && d.Detail.Contains("CMYK", StringComparison.Ordinal));
        Assert.Contains(SvgKinds(PagePdf("/Group << /S /Transparency /CS /DeviceGray >>", HalfCyanOverBlack)), d => d.Kind == RenderDiagnosticKind.TransparencyGroupApproximated && d.Detail.Contains("grey", StringComparison.Ordinal));
        Assert.DoesNotContain(SvgKinds(GroupPdf("/DeviceRGB", HalfCyanOverBlack)), d => d.Kind == RenderDiagnosticKind.TransparencyGroupApproximated);
    }

    // ------------------------------------------------------------------ fixtures

    private const string ExtGStates =
        "/ExtGState << /Half << /ca 0.5 >> /Screen << /BM /Screen >> /Lum << /BM /Luminosity >>" +
        " /Mask << /SMask << /S /Luminosity /G 7 0 R >> >> /MaskScreen << /SMask << /S /Luminosity /G 7 0 R >> /BM /Screen >> >>";

    /// <summary>Page content (with the ExtGStates) invoking a form group that names no space.</summary>
    private static byte[] PageWithFormPdf(string pageExtra, string pageContent, string formContent) =>
        RenderFormXObjectBudgetTests.Assemble(new List<string>
        {
            RenderFormXObjectBudgetTests.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            RenderFormXObjectBudgetTests.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RenderFormXObjectBudgetTests.Obj(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /XObject << /Fx 5 0 R >> {ExtGStates} >> {pageExtra} >>"),
            RenderFormXObjectBudgetTests.StreamObj(4, "", pageContent + "\n"),
            RenderFormXObjectBudgetTests.StreamObj(5,
                " /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Group << /S /Transparency /I true >>",
                formContent + "\n"),
            RenderFormXObjectBudgetTests.Obj(6, "<< >>"),
            SoftMaskGroup(7),
        });

    /// <summary>A page whose content is one <c>Do</c> of a form group with <paramref name="groupCs"/>.</summary>
    private static byte[] GroupPdf(string groupCs, string groupContent) =>
        RenderFormXObjectBudgetTests.Assemble(new List<string>
        {
            RenderFormXObjectBudgetTests.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            RenderFormXObjectBudgetTests.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RenderFormXObjectBudgetTests.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /XObject << /Fx 5 0 R >> >> >>"),
            RenderFormXObjectBudgetTests.StreamObj(4, "", "/Fx Do\n"),
            RenderFormXObjectBudgetTests.StreamObj(5,
                $" /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Group << /S /Transparency /I true /CS {groupCs} >> /Resources << {ExtGStates} >>",
                groupContent + "\n"),
            RenderFormXObjectBudgetTests.Obj(6, "<< >>"),
            SoftMaskGroup(7),
        });

    /// <summary>
    /// <see cref="GroupPdf"/> with the group's <c>/CS</c> an <c>/ICCBased</c> stream of <paramref name="n"/>
    /// components (object 8; the bytes are not a profile, and the blending-space predicate reads only <c>/N</c>).
    /// </summary>
    private static byte[] IccGroupPdf(int n, string groupContent) =>
        RenderFormXObjectBudgetTests.Assemble(new List<string>
        {
            RenderFormXObjectBudgetTests.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            RenderFormXObjectBudgetTests.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RenderFormXObjectBudgetTests.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /XObject << /Fx 5 0 R >> >> >>"),
            RenderFormXObjectBudgetTests.StreamObj(4, "", "/Fx Do\n"),
            RenderFormXObjectBudgetTests.StreamObj(5,
                $" /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Group << /S /Transparency /I true /CS [/ICCBased 8 0 R] >> /Resources << {ExtGStates} >>",
                groupContent + "\n"),
            RenderFormXObjectBudgetTests.Obj(6, "<< >>"),
            SoftMaskGroup(7),
            RenderFormXObjectBudgetTests.StreamObj(8, $" /N {n}", "not a profile\n"),
        });

    /// <summary>Page content drawn directly on a page with <paramref name="pageExtra"/> in its dictionary.</summary>
    /// <param name="iccN">When set, object 8 is an <c>/ICCBased</c> stream of that many components for the page extra to name.</param>
    private static byte[] PagePdf(string pageExtra, string content, int? iccN = null)
    {
        var objects = new List<string>
        {
            RenderFormXObjectBudgetTests.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            RenderFormXObjectBudgetTests.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RenderFormXObjectBudgetTests.Obj(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << {ExtGStates} >> {pageExtra} >>"),
            RenderFormXObjectBudgetTests.StreamObj(4, "", content + "\n"),
            RenderFormXObjectBudgetTests.Obj(5, "<< >>"),
            RenderFormXObjectBudgetTests.Obj(6, "<< >>"),
            SoftMaskGroup(7),
        };
        if (iccN is int n) objects.Add(RenderFormXObjectBudgetTests.StreamObj(8, $" /N {n}", "not a profile\n"));
        return RenderFormXObjectBudgetTests.Assemble(objects);
    }

    /// <summary>An outer group with <paramref name="outerCs"/> invoking an inner group that names no space.</summary>
    private static byte[] NestedPdf(string outerCs, string outer, string inner) =>
        RenderFormXObjectBudgetTests.Assemble(new List<string>
        {
            RenderFormXObjectBudgetTests.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            RenderFormXObjectBudgetTests.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RenderFormXObjectBudgetTests.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /XObject << /Fx 5 0 R >> >> >>"),
            RenderFormXObjectBudgetTests.StreamObj(4, "", "/Fx Do\n"),
            RenderFormXObjectBudgetTests.StreamObj(5,
                $" /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Group << /S /Transparency /I true /CS {outerCs} >> /Resources << /XObject << /In 6 0 R >> >>",
                outer + "\n"),
            RenderFormXObjectBudgetTests.StreamObj(6,
                $" /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Group << /S /Transparency >> /Resources << {ExtGStates} >>",
                inner + "\n"),
            SoftMaskGroup(7),
        });

    /// <summary>A luminosity soft-mask group of mid grey: alpha 0.5 everywhere.</summary>
    private static string SoftMaskGroup(int num) =>
        RenderFormXObjectBudgetTests.StreamObj(num,
            " /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Group << /S /Transparency /CS /DeviceGray >>",
            "0.5 g 0 0 100 100 re f\n");

    private static IReadOnlyList<(RenderDiagnosticKind Kind, string Detail)> RasterKinds(byte[] pdf) =>
        RenderFormXObjectBudgetTests.CaptureDiagnostics(() =>
        {
            using var ms = new MemoryStream(pdf);
            PdfPageRenderer.RenderPageToPixels(ms, 0, 72);
        });

    private static IReadOnlyList<(RenderDiagnosticKind Kind, string Detail)> SvgKinds(byte[] pdf) =>
        RenderFormXObjectBudgetTests.CaptureDiagnostics(() =>
        {
            using var ms = new MemoryStream(pdf);
            PdfSvgRenderer.RenderPageToSvg(ms, 0);
        });

    private static void AssertNear(SKColor expected, SKColor actual, int tolerance = 3)
    {
        Assert.True(Math.Abs(expected.Red - actual.Red) <= tolerance
                 && Math.Abs(expected.Green - actual.Green) <= tolerance
                 && Math.Abs(expected.Blue - actual.Blue) <= tolerance,
            $"expected {expected} within {tolerance}, got {actual}");
    }
}
