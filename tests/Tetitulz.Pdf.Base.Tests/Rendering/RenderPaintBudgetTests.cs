// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The raster renderer's paint ceiling, <c>PdfLimits.MaxPaintWorkPerPage</c>: every paint is charged
/// the device area it covers inside the clip, times its kind's measured price.
/// </summary>
/// <remarks>
/// A paint's cost is its area, and the page box chooses the bitmap up to the area ceiling whatever
/// resolution is asked for: a page a million points square is a hundred megapixels even as a
/// thumbnail, and each page-sized fill on it costs 42 ms. The coverage-guided fuzzer's 6 KB file — a
/// form drawing itself a thousand times with one fill, on such a page — rendered for 198 s inside
/// every form ceiling.
/// </remarks>
[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public class RenderPaintBudgetTests
{
    [Theory]
    [InlineData("0 0 100 50 re f", 5000)]           // inside the canvas: its whole area
    [InlineData("150 150 100 100 re f", 2500)]      // half off two edges: the part inside the clip
    [InlineData("-50 -50 100 100 re f", 2500)]      // and off the other two
    [InlineData("300 300 10 10 re f", 0)]           // wholly outside: culled, never charged
    public void AFill_IsChargedTheDeviceAreaItCoversInsideTheClip(string content, double expected)
    {
        Assert.Equal(expected, Spent(content, 200));
    }

    /// <summary>
    /// The prices are pinned as numbers, not computed from <c>RenderBudget</c>'s weights: an expected
    /// value derived from the constant moves with it and cannot see the constant change.
    /// </summary>
    [Theory]
    [InlineData("q 100 0 0 50 0 0 cm /Im Do Q", 65_000)]      // 5 000 pixels × 13, nearest sampling
    [InlineData("q 100 0 0 50 0 0 cm /ImI Do Q", 440_000)]    // 5 000 pixels × 88, Mitchell
    [InlineData("/Sh sh", 600_000)]                           // the whole 40 000-pixel clip × 15
    public void EachPaintKind_IsChargedAtItsMeasuredPrice(string content, double expected)
    {
        Assert.Equal(expected, Spent(content, 200));
    }

    /// <summary>
    /// A text run is charged where it paints: the same run in render mode 0 spends paint work and in
    /// mode 3 (invisible), which paints nothing, spends none.
    /// </summary>
    [Fact]
    public void AVisibleTextRun_IsCharged_AnInvisibleOneIsNot()
    {
        double visible = Spent("BT /F1 100 Tf 10 50 Td (Hello) Tj ET", 400);
        double invisible = Spent("BT 3 Tr /F1 100 Tf 10 50 Td (Hello) Tj ET", 400);

        Assert.True(visible > 1000, $"a 100-point run spent {visible}");
        Assert.Equal(0, invisible);
    }

    /// <summary>
    /// A pattern fill is charged at the shading's price — its shader runs a pixel at a time — on top
    /// of what its cell's own fill spends in the offscreen cell.
    /// </summary>
    [Fact]
    public void APatternFill_IsChargedAtTheShadingPrice()
    {
        Assert.True(Spent("/Pattern cs /P1 scn 0 0 100 50 re f", 200) >= 75_000);   // 5 000 pixels × 15
    }

    /// <summary>
    /// A glyph drawn from an embedded Type 1 program's outline (<c>DrawGlyphPath</c>, not a Skia text
    /// blob) is charged its outline's area: the fixture's glyph is a 400-unit box, forty points square
    /// at 100 points.
    /// </summary>
    [Fact]
    public void AGlyphOutline_IsChargedItsArea()
    {
        const string content = "BT /F1 100 Tf 100 376 Td (A) Tj ET";
        using var bitmap = Render(EmbeddedType1Fixture.Page(content), content, 612, 792, out RenderBudget budget);

        Assert.InRange(budget.PaintWorkSpent, 1500, 1700);
    }

    /// <summary>
    /// A hairline across a thousand-pixel canvas is charged its length times the pen's width — about
    /// 1 414 × 12 pixels at the stroke's price — not the million pixels of its bounds, which the same
    /// diagonal costs as a filled triangle.
    /// </summary>
    [Fact]
    public void ADiagonalHairline_IsChargedItsInk_NotItsBounds()
    {
        double stroke = Spent("1 w 0 0 m 1000 1000 l S", 1000);
        double fill = Spent("0 0 m 1000 1000 l 1000 0 l h f", 1000);

        Assert.InRange(stroke, 1414 * 12 * 3 * 0.99, 1415 * 12 * 3 * 1.01);   // length × pen × 3, the stroke's price
        Assert.Equal(1000.0 * 1000, fill);
    }

    /// <summary>
    /// Past the ceiling the page's later paints are skipped and the refusal is reported once: under a
    /// ceiling of one and a half canvases, red covers the canvas, blue is refused, and so is green. The
    /// control, under the default ceiling, paints green last.
    /// </summary>
    [Theory]
    [InlineData(15_000L, 255, 0, 0, 1)]
    [InlineData(PdfLimits.DefaultMaxPaintWorkPerPage, 0, 255, 0, 0)]
    public void PastTheCeiling_LaterPaintsAreSkipped_AndReportedOnce(long ceiling, int r, int g, int b, int reports)
    {
        const string content = "1 0 0 rg 0 0 100 100 re f 0 0 1 rg 0 0 100 100 re f 0 1 0 rg 0 0 100 100 re f";
        try
        {
            PdfLimits.MaxPaintWorkPerPage = ceiling;
            SKColor centre = default;
            var reported = RenderFormXObjectBudgetTests.CaptureDiagnostics(() =>
            {
                using var bitmap = Render(content, 100, out _);
                centre = bitmap.GetPixel(50, 50);
            });

            Assert.Equal((byte)r, centre.Red);
            Assert.Equal((byte)g, centre.Green);
            Assert.Equal((byte)b, centre.Blue);
            Assert.Equal(reports, reported.Count(d => d.Kind == RenderDiagnosticKind.PaintWorkExceeded));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// The fuzzer's file, through the public renderer: a page a million points square (reduced to
    /// the area ceiling), a form drawing itself with a page-sized fill. Its paints are refused by the
    /// paint ceiling and reported once, and its depth refusal — every branch the form ceilings admit
    /// ends at the cap, a thousand <c>Do</c> operators a form — is reported once, not two million
    /// times.
    /// </summary>
    [Fact]
    public void AdversarialSelfDrawingFormOnAHugePage_IsBoundedByThePaintCeiling()
    {
        byte[] pdf = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fuzz",
            "render-self-drawing-form-on-a-huge-page.pdf"));
        try
        {
            PdfLimits.MaxPaintWorkPerPage = 1L << 30;
            var reported = RenderFormXObjectBudgetTests.CaptureDiagnostics(() =>
            {
                using var ms = new MemoryStream(pdf);
                PdfPageRenderer.RenderPageToPixels(ms, 0, 12);
            });

            Assert.Contains(reported, d => d.Kind == RenderDiagnosticKind.RenderResolutionReduced);
            Assert.Equal(1, reported.Count(d => d.Kind == RenderDiagnosticKind.PaintWorkExceeded));
            Assert.Equal(1, reported.Count(d => d.Detail != null && d.Detail.Contains("nesting exceeds", StringComparison.Ordinal)));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// A form drawing itself twice reaches the depth cap on every one of the branches the invocation
    /// ceiling admits; each renderer reports the cap once a page.
    /// </summary>
    [Theory]
    [InlineData("raster")]
    [InlineData("SVG")]
    public void TheDepthCap_IsReportedOnceAPage(string renderer)
    {
        const string res = "<< /XObject << /Fx 5 0 R >> >>";
        byte[] pdf = RenderFormXObjectBudgetTests.Assemble(new List<string>
        {
            RenderFormXObjectBudgetTests.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            RenderFormXObjectBudgetTests.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RenderFormXObjectBudgetTests.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources " + res + " >>"),
            RenderFormXObjectBudgetTests.StreamObj(4, "", "/Fx Do\n"),
            RenderFormXObjectBudgetTests.StreamObj(5, " /Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources " + res,
                "/Fx Do /Fx Do 0 0 1 rg 0 0 10 10 re f\n"),
        });

        var reported = RenderFormXObjectBudgetTests.CaptureDiagnostics(() =>
        {
            using var ms = new MemoryStream(pdf);
            if (renderer == "raster") PdfOperations.RenderPageToPng(ms, 0);
            else PdfOperations.RenderPageToSvg(ms, 0);
        });

        Assert.Equal(1, reported.Count(d => d.Detail != null && d.Detail.Contains("nesting exceeds", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The fuzzer's tiling cell: a step of 1e-300 under a thousand-point <c>/BBox</c>, so every tile
    /// is an approximated overlap, and a cell that fills with itself, so the build is attempted on
    /// every branch the invocation ceiling admits and every one it refuses. Each renderer reports the
    /// approximation once a page, not two hundred thousand times.
    /// </summary>
    [Theory]
    [InlineData("raster")]
    [InlineData("SVG")]
    public void AdversarialSelfFillingOverlappingCell_ReportsItsApproximationOnceAPage(string renderer)
    {
        byte[] pdf = RenderFormXObjectBudgetTests.Assemble(new List<string>
        {
            RenderFormXObjectBudgetTests.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            RenderFormXObjectBudgetTests.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RenderFormXObjectBudgetTests.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R " +
                                                "/Resources << /Pattern << /P1 5 0 R >> >> >>"),
            RenderFormXObjectBudgetTests.StreamObj(4, "", "/Pattern cs /P1 scn 0 0 200 200 re f\n"),
            RenderFormXObjectBudgetTests.StreamObj(5, " /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 1000 1000] " +
                "/XStep 1E-300 /YStep 1E-300 /Resources << /Pattern << /P1 5 0 R >> >>",
                "/Pattern cs /P1 scn 0 0 10 10 re f /Pattern cs /P1 scn 0 0 10 10 re f\n"),
        });

        var reported = RenderFormXObjectBudgetTests.CaptureDiagnostics(() =>
        {
            using var ms = new MemoryStream(pdf);
            if (renderer == "raster") PdfOperations.RenderPageToPng(ms, 0);
            else PdfOperations.RenderPageToSvg(ms, 0);
        });

        Assert.Equal(1, reported.Count(d => d.Kind == RenderDiagnosticKind.PatternApproximated
            && d.Detail != null && d.Detail.Contains("copies of", StringComparison.Ordinal)));
    }

    // ------------------------------------------------------------------ harness

    /// <summary>The paint work <paramref name="content"/> spends on a square canvas of <paramref name="size"/> pixels at one pixel a point.</summary>
    private static double Spent(string content, int size)
    {
        using var bitmap = Render(content, size, out RenderBudget budget);
        return budget.PaintWorkSpent;
    }

    private static SKBitmap Render(string content, int size, out RenderBudget budget)
        => Render(Pdf(size), content, size, size, out budget);

    /// <summary>Runs <paramref name="content"/> against the first page of <paramref name="pdf"/>, at one pixel a point.</summary>
    private static SKBitmap Render(byte[] pdf, string content, int width, int height, out RenderBudget budget)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var ctx = new PdfRenderContext(canvas, width, height, 1f);
        using var fonts = new FontCache();
        budget = new RenderBudget();
        var interpreter = new ContentStreamInterpreter(ctx, doc, doc.Pages[0].Resources, fonts) { FormBudget = budget };
        interpreter.Execute(Encoding.ASCII.GetBytes(content));
        canvas.Flush();
        return bitmap;
    }

    /// <summary>
    /// One page carrying the resources the contents above draw: a two-by-two image drawn with nearest
    /// sampling (<c>/Im</c>) and with <c>/Interpolate</c> (<c>/ImI</c>), an axial shading (<c>/Sh</c>), a
    /// tiling pattern (<c>/P1</c>) and Helvetica (<c>/F1</c>).
    /// </summary>
    private static byte[] Pdf(int size)
    {
        const string pixels = "ÿ\0\0\0ÿ\0\0\0ÿÿÿ\0";
        const string image = " /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8";
        return RenderFormXObjectBudgetTests.Assemble(new List<string>
        {
            RenderFormXObjectBudgetTests.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            RenderFormXObjectBudgetTests.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RenderFormXObjectBudgetTests.Obj(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {size} {size}] /Contents 4 0 R " +
                "/Resources << /XObject << /Im 5 0 R /ImI 6 0 R >> /Shading << /Sh 7 0 R >> /Pattern << /P1 8 0 R >> " +
                "/Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>"),
            RenderFormXObjectBudgetTests.StreamObj(4, "", "\n"),
            RenderFormXObjectBudgetTests.StreamObj(5, image + " /Interpolate false", pixels),
            RenderFormXObjectBudgetTests.StreamObj(6, image + " /Interpolate true", pixels),
            RenderFormXObjectBudgetTests.Obj(7, $"<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 {size} 0] " +
                "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> /Extend [true true] >>"),
            RenderFormXObjectBudgetTests.StreamObj(8, " /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] " +
                "/XStep 10 /YStep 10 /Resources << >>", "0 0 1 rg 0 0 5 5 re f\n"),
        });
    }
}
