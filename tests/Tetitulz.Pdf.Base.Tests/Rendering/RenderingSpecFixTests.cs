// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Pixel-level regression tests for the rendering-conformance fixes.
/// </summary>
/// <remarks>
/// Every test here asserts on <b>decoded pixels</b>, never merely that rendering did not throw.
/// The optional-content tests this file replaces asserted only <c>Assert.NotNull(png)</c> plus
/// <c>Assert.NotEmpty(png)</c>, so they passed even with layer hiding completely disabled — which
/// is exactly how the indirect-<c>/D</c> bug survived.
/// </remarks>
public sealed class RenderingSpecFixTests
{
    // ==================================================================
    //  Optional content (PDF §8.11.4.3)
    // ==================================================================

    /// <summary>
    /// A common viewer writes <c>/OCProperties &lt;&lt; … /D 8 0 R &gt;&gt;</c>. Reading /D without
    /// resolving the reference yielded null and disabled layer hiding entirely.
    /// </summary>
    [Fact]
    public void HiddenLayer_WithIndirectDefaultConfig_IsNotRendered()
    {
        byte[] pdf = BuildOcPdf(configIsIndirect: true, off: "/OFF [7 0 R]");
        Assert.True(IsBlank(Render(pdf)),
            "Content on an /OFF layer was rendered — the indirect /D was not resolved.");
    }

    [Fact]
    public void HiddenLayer_WithDirectDefaultConfig_IsNotRendered()
    {
        byte[] pdf = BuildOcPdf(configIsIndirect: false, off: "/OFF [7 0 R]");
        Assert.True(IsBlank(Render(pdf)));
    }

    /// <summary>A layer that is not switched off must still paint — the guard must not
    /// over-hide.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VisibleLayer_IsRendered(bool indirectConfig)
    {
        byte[] pdf = BuildOcPdf(configIsIndirect: indirectConfig, off: "");
        Assert.False(IsBlank(Render(pdf)), "A visible layer's content was suppressed.");
    }

    /// <summary>
    /// <c>/BaseState /OFF</c> hides every OCG in the document; only those listed in /ON come
    /// back. Ignoring it rendered all layers of such a document.
    /// </summary>
    [Fact]
    public void BaseStateOff_HidesLayerNotListedInOn()
    {
        byte[] pdf = BuildOcPdf(configIsIndirect: true, off: "/BaseState /OFF");
        Assert.True(IsBlank(Render(pdf)), "/BaseState /OFF did not hide the layer.");
    }

    /// <summary>/ON overrides /BaseState /OFF for the layers it names.</summary>
    [Fact]
    public void BaseStateOff_WithLayerInOn_IsRendered()
    {
        byte[] pdf = BuildOcPdf(configIsIndirect: true, off: "/BaseState /OFF /ON [7 0 R]");
        Assert.False(IsBlank(Render(pdf)), "/ON did not re-enable the layer.");
    }

    /// <summary>
    /// The top-level /OCGs array is spec-required but producers omit it. It used to be an
    /// early-return guard, so a document with a valid /OFF list but no /OCGs showed everything.
    /// </summary>
    [Fact]
    public void HiddenLayer_WithoutTopLevelOcgsArray_IsStillHidden()
    {
        byte[] pdf = BuildOcPdf(configIsIndirect: true, off: "/OFF [7 0 R]", includeOcgsArray: false);
        Assert.True(IsBlank(Render(pdf)), "Missing /OCGs disabled layer hiding.");
    }

    /// <summary>An annotation carrying /OC on a hidden layer must not draw (PDF §12.5.2).</summary>
    [Fact]
    public void Annotation_OnHiddenLayer_IsNotRendered()
    {
        byte[] pdf = BuildAnnotationPdf(
            annotExtra: "/OC 7 0 R",
            extraObjects: OcgObjects(off: true),
            catalogExtra: "/OCProperties 8 0 R");
        Assert.True(IsBlank(Render(pdf)), "A hidden-layer annotation was rendered.");
    }

    [Fact]
    public void Annotation_OnVisibleLayer_IsRendered()
    {
        byte[] pdf = BuildAnnotationPdf(
            annotExtra: "/OC 7 0 R",
            extraObjects: OcgObjects(off: false),
            catalogExtra: "/OCProperties 8 0 R");
        Assert.False(IsBlank(Render(pdf)));
    }

    // ==================================================================
    //  Annotation appearance streams (PDF §12.5.5 Algorithm 8.1)
    // ==================================================================

    /// <summary>
    /// The appearance stream's /Matrix must be applied (Algorithm 8.1 step 4).
    /// </summary>
    /// <remarks>
    /// A pure translation is <em>not</em> a valid probe: Algorithm 8.1 maps the transformed BBox
    /// back onto /Rect, so translating the whole appearance cancels out — correctly. A 90°
    /// rotation does not cancel: it moves the square to a different corner of the rect.
    /// </remarks>
    [Fact]
    public void AppearanceStream_Matrix_IsApplied()
    {
        // AP paints a 50×50 square in the lower-left quadrant of a 100×100 BBox.
        const string ap = "0 g 0 0 50 50 re f";

        byte[] noMatrix = BuildAnnotationPdf(apContent: ap, apBBox: "[0 0 100 100]");
        // 90° rotation: (x,y) → (-y,x). The square moves to the opposite corner of the fit.
        byte[] rotated = BuildAnnotationPdf(apContent: ap, apBBox: "[0 0 100 100]",
                                            apMatrix: "[0 1 -1 0 0 0]");

        var a = InkBounds(Render(noMatrix));
        var b = InkBounds(Render(rotated));

        Assert.NotNull(a);
        Assert.NotNull(b);
        // Before the fix /Matrix was dropped entirely and the two renders were pixel-identical.
        Assert.True(a!.Value.left != b!.Value.left || a.Value.top != b.Value.top,
            $"/Matrix was ignored: ink origin unchanged at ({a.Value.left},{a.Value.top}).");
    }

    /// <summary>
    /// /BBox clips the appearance (PDF §8.10.2). Content drawn outside the BBox must not
    /// escape the annotation.
    /// </summary>
    [Fact]
    public void AppearanceStream_BBox_ClipsOverflowingContent()
    {
        // BBox is 0..50 but the AP paints 0..100 — the overflow must be clipped away.
        byte[] clipped = BuildAnnotationPdf(
            apContent: "0 g 0 0 100 100 re f", apBBox: "[0 0 50 50]");
        byte[] exact = BuildAnnotationPdf(
            apContent: "0 g 0 0 50 50 re f", apBBox: "[0 0 50 50]");

        int clippedInk = CountInk(Render(clipped));
        int exactInk   = CountInk(Render(exact));

        Assert.True(clippedInk > 0, "Nothing rendered at all.");
        // Both must cover the same area: the clip removes exactly the overflow.
        Assert.True(Math.Abs(clippedInk - exactInk) <= Math.Max(8, exactInk / 10),
            $"/BBox did not clip: {clippedInk} inked px vs {exactInk} expected.");
    }

    // ==================================================================
    //  Shading
    // ==================================================================

    /// <summary>
    /// A type-1 (function-based) shading must be mapped through its /Domain, its /Matrix and the
    /// CTM. With no local matrix on the sampled bitmap, Skia placed the grid at device (0,0) at
    /// 1 bitmap pixel per output pixel and clamped the rest, so the gradient collapsed.
    /// </summary>
    [Fact]
    public void FunctionBasedShading_IsMappedThroughDomainAndMatrix()
    {
        // A type-4 PostScript function returning x as the grey level: a left-to-right ramp.
        const string fn =
            "<< /FunctionType 4 /Domain [0 1 0 1] /Range [0 1] /Length 16 >>\n" +
            "stream\n{ pop }\nendstream";

        // /Matrix scales the unit domain onto a 400×400 region at (100,200).
        string shading =
            "<< /ShadingType 1 /ColorSpace /DeviceGray /Domain [0 1 0 1] " +
            "/Matrix [400 0 0 400 100 200] /Function 6 0 R >>";

        byte[] pdf = BuildShadingPdf(shading, fnObject: fn,
                                     content: "q 100 200 400 400 re W n /Sh1 sh Q");
        var bmp = Render(pdf);

        // Sample two points inside the shaded band. The ramp runs left→right, so the right
        // sample must be lighter than the left one.
        var left  = bmp.GetPixel(ToPx(150), ToPxY(bmp, 400));
        var right = bmp.GetPixel(ToPx(450), ToPxY(bmp, 400));

        Assert.True(right.Red > left.Red + 30,
            $"Function-based shading is not mapped across its domain (left={left.Red}, right={right.Red}).");
    }

    /// <summary>
    /// A partial /Extend must leave the non-extended end unpainted. Skia has a single tile mode
    /// for both ends, so [true false] used to fall back to Clamp and wrongly extended the end.
    /// </summary>
    [Fact]
    public void AxialShading_PartialExtend_DoesNotPaintBeyondTheNonExtendedEnd()
    {
        // Gradient spans x = 200..300. Extend start only.
        string shading =
            "<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [200 0 300 0] " +
            "/Extend [true false] /Function << /FunctionType 2 /Domain [0 1] " +
            "/C0 [0] /C1 [0] /N 1 >> >>";

        byte[] pdf = BuildShadingPdf(shading, content: "q 100 300 400 200 re W n /Sh1 sh Q");
        var bmp = Render(pdf);

        // x = 150 is before the start → extended → painted black.
        // x = 450 is past the end     → NOT extended → must stay white.
        var beforeStart = bmp.GetPixel(ToPx(150), ToPxY(bmp, 400));
        var afterEnd    = bmp.GetPixel(ToPx(450), ToPxY(bmp, 400));

        Assert.True(beforeStart.Red < 80, "Extend[0]=true did not paint before the start.");
        Assert.True(afterEnd.Red > 200,
            $"Extend[1]=false still painted past the end (grey {afterEnd.Red}).");
    }

    /// <summary>
    /// <c>sh</c> paints with the current alpha constant (PDF §11.6.4.2). Both <c>ca</c> and the
    /// active soft mask used to be dropped, so a shading under <c>ca 0.5</c> painted opaque.
    /// </summary>
    [Fact]
    public void ShOperator_HonoursFillAlpha()
    {
        string shading =
            "<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [100 0 500 0] " +
            "/Extend [true true] /Function << /FunctionType 2 /Domain [0 1] " +
            "/C0 [0] /C1 [0] /N 1 >> >>";

        byte[] opaque = BuildShadingPdf(shading, content: "q 100 300 400 200 re W n /Sh1 sh Q");
        byte[] half   = BuildShadingPdf(shading,
            content: "q /GSa gs 100 300 400 200 re W n /Sh1 sh Q",
            extGState: "/GSa << /ca 0.5 >>");

        byte opaqueGrey = Render(opaque).GetPixel(ToPx(300), ToPxY(Render(opaque), 400)).Red;
        var halfBmp = Render(half);
        byte halfGrey = halfBmp.GetPixel(ToPx(300), ToPxY(halfBmp, 400)).Red;

        Assert.True(opaqueGrey < 40, $"Opaque shading was not painted black (grey {opaqueGrey}).");
        Assert.True(halfGrey > opaqueGrey + 60,
            $"`sh` ignored ca=0.5 (grey {halfGrey} vs opaque {opaqueGrey}).");
    }

    /// <summary>
    /// A pattern-stroked path must use the graphics state's line width. DrawPatternPath only set
    /// Style=Stroke, leaving SKPaint's default 0 width, so every such stroke was a hairline.
    /// </summary>
    [Fact]
    public void PatternStroke_UsesGraphicsStateLineWidth()
    {
        string shadingPattern =
            "<< /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceGray " +
            "/Coords [0 0 612 0] /Extend [true true] /Function << /FunctionType 2 " +
            "/Domain [0 1] /C0 [0] /C1 [0] /N 1 >> >> >>";

        byte[] thin = BuildPatternPdf(shadingPattern,
            "q /Pattern CS /P1 SCN 1 w 100 400 m 500 400 l S Q");
        byte[] thick = BuildPatternPdf(shadingPattern,
            "q /Pattern CS /P1 SCN 20 w 100 400 m 500 400 l S Q");

        int thinInk  = CountInk(Render(thin));
        int thickInk = CountInk(Render(thick));

        Assert.True(thinInk > 0, "Pattern stroke painted nothing.");
        Assert.True(thickInk > thinInk * 3,
            $"`w 20` did not widen a pattern stroke ({thickInk} px vs {thinInk} px at w 1).");
    }

    // ==================================================================
    //  Transparency groups (PDF §11.6.6)
    // ==================================================================

    /// <summary>
    /// A /Group form composites as a unit: two overlapping opaque shapes inside it show a single
    /// 50% tint, not a double-darkened overlap. A plain form has ca applied per object, so the
    /// overlap IS darker. Rendering both identically (the old behaviour) is wrong either way.
    /// </summary>
    [Fact]
    public void TransparencyGroup_CompositesAsAUnit_UnlikePlainForm()
    {
        // Two overlapping black squares; the overlap region is sampled.
        const string formContent = "0 g 0 0 100 100 re f 50 0 100 100 re f";

        byte[] groupPdf = BuildFormPdf(formContent, isGroup: true,
            content: "q /GSa gs 1 0 0 1 100 400 cm /Fx1 Do Q", extGState: "/GSa << /ca 0.5 >>");
        byte[] plainPdf = BuildFormPdf(formContent, isGroup: false,
            content: "q /GSa gs 1 0 0 1 100 400 cm /Fx1 Do Q", extGState: "/GSa << /ca 0.5 >>");

        var groupBmp = Render(groupPdf);
        var plainBmp = Render(plainPdf);

        // (75, 50) in form space → overlap of the two squares.
        byte groupOverlap = groupBmp.GetPixel(ToPx(175), ToPxY(groupBmp, 450)).Red;
        byte plainOverlap = plainBmp.GetPixel(ToPx(175), ToPxY(plainBmp, 450)).Red;

        // The group's overlap is a single 50% black (~128); the plain form's overlap is two
        // 50% blacks stacked (~64). They must differ.
        Assert.True(groupOverlap > plainOverlap + 25,
            $"Group and plain form composited identically (group {groupOverlap}, plain {plainOverlap}).");
    }

    /// <summary>
    /// <c>Do</c> is a non-stroking paint operation, so only <c>ca</c> governs group opacity.
    /// The old <c>Math.Min(FillAlpha, StrokeAlpha)</c> let a low <c>CA</c> fade the fills too.
    /// </summary>
    [Fact]
    public void TransparencyGroup_UsesFillAlphaOnly_NotMinOfFillAndStroke()
    {
        const string formContent = "0 g 0 0 100 100 re f";

        byte[] pdf = BuildFormPdf(formContent, isGroup: true,
            content: "q /GSa gs 1 0 0 1 100 400 cm /Fx1 Do Q",
            extGState: "/GSa << /ca 1 /CA 0.1 >>");

        var bmp = Render(pdf);
        byte grey = bmp.GetPixel(ToPx(150), ToPxY(bmp, 450)).Red;

        // ca = 1 → the fill must be fully opaque black regardless of CA.
        Assert.True(grey < 40,
            $"CA=0.1 faded a ca=1 group fill (grey {grey}); Do must use ca alone.");
    }

    // ==================================================================
    //  Images
    // ==================================================================

    /// <summary>
    /// An Indexed image with /BitsPerComponent 16 made ReadPackedIndex compute 8/16 = 0 and then
    /// divide by it. The per-operator catch swallowed the DivideByZeroException, so the image
    /// vanished with a misleading diagnostic. It must now be rejected explicitly.
    /// </summary>
    [Fact]
    public void IndexedImage_WithInvalidBitsPerComponent_IsRejectedNotCrashed()
    {
        var kinds = new List<RenderDiagnosticKind>();
        void Sink(RenderDiagnosticKind kind, string detail, Exception? ex)
        {
            lock (kinds) kinds.Add(kind);
        }

        RenderDiagnostics.OnEvent += Sink;
        try
        {
            byte[] pdf = BuildIndexedImagePdf(bpc: 16);
            var ex = Record.Exception(() => Render(pdf));
            Assert.Null(ex);
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        Assert.Contains(RenderDiagnosticKind.ImageDecodeFailed, kinds);
    }

    /// <summary>Valid indexed depths still decode.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void IndexedImage_WithValidBitsPerComponent_Renders(int bpc)
    {
        byte[] pdf = BuildIndexedImagePdf(bpc);
        Assert.False(IsBlank(Render(pdf)), $"Indexed image at {bpc} bpc rendered nothing.");
    }

    // ==================================================================
    //  Rendering helpers
    // ==================================================================

    private const int Dpi = 72;

    private static SKBitmap Render(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfOperations.RenderPageToPng(ms, 0, dpi: Dpi);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>PDF user-space X → device pixel at <see cref="Dpi"/>.</summary>
    private static int ToPx(float userX) => (int)(userX * Dpi / 72f);

    /// <summary>PDF user-space Y (origin bottom-left) → device row (origin top-left).</summary>
    private static int ToPxY(SKBitmap bmp, float userY) => bmp.Height - 1 - (int)(userY * Dpi / 72f);

    /// <summary>True when every pixel is white (or transparent) — i.e. nothing was painted.</summary>
    private static bool IsBlank(SKBitmap bmp) => CountInk(bmp) == 0;

    /// <summary>Counts pixels that are neither white nor fully transparent.</summary>
    private static int CountInk(SKBitmap bmp)
    {
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Alpha > 8 && (c.Red < 240 || c.Green < 240 || c.Blue < 240)) n++;
            }
        return n;
    }

    /// <summary>Bounding box of the inked pixels, or null when the page is blank.</summary>
    private static (int left, int top, int right, int bottom)? InkBounds(SKBitmap bmp)
    {
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Alpha <= 8 || (c.Red >= 240 && c.Green >= 240 && c.Blue >= 240)) continue;
                if (x < l) l = x;
                if (x > r) r = x;
                if (y < t) t = y;
                if (y > b) b = y;
            }
        return r < 0 ? null : (l, t, r, b);
    }

    // ==================================================================
    //  PDF construction
    // ==================================================================

    /// <summary>
    /// Assembles a PDF from pre-formatted object bodies (1-based; index 0 is object 1) and
    /// writes a correct xref table. Keeping this local keeps every test self-contained.
    /// </summary>
    private static byte[] Assemble(IReadOnlyList<string> objects, int rootObjNumber = 1)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1)
          .Append(" /Root ").Append(rootObjNumber).Append(" 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Stream(string body, string dictExtra = "")
        => $"<< /Length {body.Length} {dictExtra}>>\nstream\n{body}\nendstream";

    /// <summary>
    /// Objects 7 (OCG) and 8 (OCProperties, direct /D) for the annotation optional-content tests.
    /// </summary>
    private static string[] OcgObjects(bool off) => new[]
    {
        "<< /Type /OCG /Name (Layer1) >>",
        "<< /OCGs [7 0 R] /D << " + (off ? "/OFF [7 0 R]" : "") + " >> >>",
    };

    /// <summary>
    /// A page whose content is wrapped in <c>/OC /OC1 BDC … EMC</c>. Object 7 is the OCG and 8
    /// the /OCProperties; when <paramref name="configIsIndirect"/> the default config moves into
    /// object 9 and /D becomes a reference — the shape a common viewer writes.
    /// </summary>
    private static byte[] BuildOcPdf(bool configIsIndirect, string off, bool includeOcgsArray = true)
    {
        const string content = "/OC /OC1 BDC 0 g 100 100 400 400 re f EMC";
        string ocgs = includeOcgsArray ? "/OCGs [7 0 R] " : "";

        var objs = new List<string>
        {
            /* 1 */ "<< /Type /Catalog /Pages 2 0 R /OCProperties 8 0 R >>",
            /* 2 */ "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            /* 3 */ "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                    "/Resources << /Properties << /OC1 7 0 R >> >> >>",
            /* 4 */ Stream(content),
            /* 5 */ "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            /* 6 */ "<< >>",
            /* 7 */ "<< /Type /OCG /Name (Layer1) >>",
            /* 8 */ configIsIndirect
                        ? $"<< {ocgs}/D 9 0 R >>"
                        : $"<< {ocgs}/D << {off} >> >>",
        };
        if (configIsIndirect) objs.Add(/* 9 */ $"<< {off} >>");
        return Assemble(objs);
    }

    /// <summary>A page carrying one /Square annotation with an /AP /N form XObject.</summary>
    private static byte[] BuildAnnotationPdf(
        string apContent = "0 g 0 0 50 50 re f",
        string apBBox = "[0 0 100 100]",
        string? apMatrix = null,
        string annotExtra = "",
        string catalogExtra = "",
        string[]? extraObjects = null)
    {
        string matrixEntry = apMatrix != null ? $"/Matrix {apMatrix} " : "";
        var objs = new List<string>
        {
            /* 1 */ $"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>",
            /* 2 */ "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            /* 3 */ "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                    "/Annots [5 0 R] /Resources << >> >>",
            /* 4 */ Stream(" "),
            /* 5 */ "<< /Type /Annot /Subtype /Square /Rect [100 400 200 500] " +
                    $"/F 4 /AP << /N 6 0 R >> {annotExtra} >>",
            /* 6 */ Stream(apContent,
                        $"/Type /XObject /Subtype /Form /BBox {apBBox} {matrixEntry}/Resources << >> "),
        };
        if (extraObjects != null) objs.AddRange(extraObjects);
        return Assemble(objs);
    }

    /// <summary>A page with a /Shading resource named /Sh1 (object 5).</summary>
    private static byte[] BuildShadingPdf(
        string shadingDict, string content, string? fnObject = null, string? extGState = null)
    {
        string gsEntry = extGState != null ? $"/ExtGState << {extGState} >> " : "";
        var objs = new List<string>
        {
            /* 1 */ "<< /Type /Catalog /Pages 2 0 R >>",
            /* 2 */ "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            /* 3 */ "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                    $"/Resources << /Shading << /Sh1 5 0 R >> {gsEntry}>> >>",
            /* 4 */ Stream(content),
            /* 5 */ shadingDict,
        };
        if (fnObject != null) objs.Add(/* 6 */ fnObject);
        return Assemble(objs);
    }

    /// <summary>A page with a /Pattern resource named /P1 (object 5).</summary>
    private static byte[] BuildPatternPdf(string patternDict, string content) => Assemble(new[]
    {
        /* 1 */ "<< /Type /Catalog /Pages 2 0 R >>",
        /* 2 */ "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        /* 3 */ "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Pattern << /P1 5 0 R >> >> >>",
        /* 4 */ Stream(content),
        /* 5 */ patternDict,
    });

    /// <summary>A page with a form XObject /Fx1 (object 5), optionally a transparency group.</summary>
    private static byte[] BuildFormPdf(
        string formContent, bool isGroup, string content, string? extGState = null)
    {
        string groupEntry = isGroup ? "/Group << /S /Transparency /CS /DeviceRGB >> " : "";
        string gsEntry = extGState != null ? $"/ExtGState << {extGState} >> " : "";
        return Assemble(new[]
        {
            /* 1 */ "<< /Type /Catalog /Pages 2 0 R >>",
            /* 2 */ "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            /* 3 */ "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                    $"/Resources << /XObject << /Fx1 5 0 R >> {gsEntry}>> >>",
            /* 4 */ Stream(content),
            /* 5 */ Stream(formContent,
                        "/Type /XObject /Subtype /Form /BBox [0 0 200 100] " +
                        $"{groupEntry}/Resources << >> "),
        });
    }

    /// <summary>A page drawing one Indexed image at the requested bit depth.</summary>
    private static byte[] BuildIndexedImagePdf(int bpc)
    {
        // 4×1 image; every sample is index 1 (black in the palette below).
        const int w = 4, h = 1;
        int stride = (w * bpc + 7) / 8;
        var row = new byte[stride];
        for (int x = 0; x < w; x++)
        {
            if (bpc >= 8)
            {
                int byteIdx = x * (bpc / 8);
                if (byteIdx < row.Length) row[byteIdx] = 1;
            }
            else
            {
                int pxPerByte = 8 / bpc;
                int shift = (pxPerByte - 1 - x % pxPerByte) * bpc;
                row[x / pxPerByte] |= (byte)(1 << shift);
            }
        }
        var hex = new StringBuilder();
        foreach (byte b in row) hex.AppendFormat("{0:X2}", b);
        hex.Append('>');

        // Palette: index 0 = white, index 1 = black.
        return Assemble(new[]
        {
            /* 1 */ "<< /Type /Catalog /Pages 2 0 R >>",
            /* 2 */ "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            /* 3 */ "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                    "/Resources << /XObject << /Im1 5 0 R >> >> >>",
            /* 4 */ Stream("q 200 0 0 200 100 400 cm /Im1 Do Q"),
            /* 5 */ Stream(hex.ToString(),
                        $"/Type /XObject /Subtype /Image /Width {w} /Height {h} " +
                        "/ColorSpace [/Indexed /DeviceRGB 1 <FFFFFF000000>] " +
                        $"/BitsPerComponent {bpc} /Filter /ASCIIHexDecode "),
        });
    }
}
