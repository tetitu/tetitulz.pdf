// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

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
/// Work a page repeats, each repetition charged: a visibility expression whose arrays a file shares, a mesh
/// shading's pieces and bitmap, an image the decoded-image cache does not hold, a mesh the SVG writer
/// rasterises, and the separation plates a page's spot inks each cost.
/// </summary>
[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public class RenderRepeatedWorkBudgetTests
{
    // ------------------------------------------------------------------ optional content /VE

    /// <summary>
    /// An OCMD whose <c>/VE</c> is twenty levels of <c>[/And X X X X]</c>, each X the same array one level
    /// down (inside the depth cap): as a tree that is 4^20 evaluations, as the graph it is twenty arrays. A content stream naming
    /// the OCMD fifty times evaluates it once. The control hides the group: the same expression, answered
    /// false, paints nothing.
    /// </summary>
    [Theory]
    [InlineData(false, 0, 0, 255)]
    [InlineData(true, 255, 255, 255)]
    public void AdversarialSharedVisibilityExpression_IsEvaluatedOnceAnArray_AndOnceAPage(bool hidden, int r, int g, int b)
    {
        const int levels = 20;
        byte[] pdf = SharedVisibilityExpressionPdf(levels, 4, 50, hidden);

        long before = ContentStreamInterpreter.VeArraysEvaluated;
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72);
        long evaluated = ContentStreamInterpreter.VeArraysEvaluated - before;

        using var bitmap = SKBitmap.Decode(png);
        SKColor centre = bitmap.GetPixel(5, 5);
        Assert.Equal((byte)r, centre.Red);
        Assert.Equal((byte)g, centre.Green);
        Assert.Equal((byte)b, centre.Blue);
        Assert.InRange(evaluated, 1, levels + 1);
    }

    private static byte[] SharedVisibilityExpressionPdf(int levels, int width, int marks, bool hidden)
    {
        var content = new StringBuilder();
        for (int i = 0; i < marks; i++) content.Append("/OC /M BDC 0 0 1 rg 0 0 10 10 re f EMC\n");
        string off = hidden ? " /OFF [4 0 R]" : "";
        var bodies = new List<string>
        {
            RenderFormXObjectBudgetTests.Obj(1, "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [4 0 R] /D << /Order [4 0 R]" + off + " >> >> >>"),
            RenderFormXObjectBudgetTests.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RenderFormXObjectBudgetTests.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Contents 6 0 R /Resources << /Properties << /M 5 0 R >> >> >>"),
            RenderFormXObjectBudgetTests.Obj(4, "<< /Type /OCG /Name (g) >>"),
            RenderFormXObjectBudgetTests.Obj(5, "<< /Type /OCMD /VE 7 0 R >>"),
            RenderFormXObjectBudgetTests.StreamObj(6, "", content.ToString()),
        };
        for (int i = 0; i < levels; i++)
        {
            string operand = i == levels - 1 ? " 4 0 R" : $" {7 + i + 1} 0 R";
            bodies.Add(RenderFormXObjectBudgetTests.Obj(7 + i, "[/And" + string.Concat(Enumerable.Repeat(operand, width)) + "]"));
        }
        return RenderFormXObjectBudgetTests.Assemble(bodies);
    }

    // ------------------------------------------------------------------ mesh shadings

    /// <summary>
    /// Each piece of a mesh — a free-form triangle, a lattice cell, a Coons patch — is charged the device area
    /// its bounding box covers at the shading's price: the fixtures' pieces each cover the whole hundred-pixel
    /// canvas, so a second piece costs 10 000 × 15 more paint work than one.
    /// </summary>
    [Theory]
    [InlineData("/Tri1 sh", "/Tri2 sh")]
    [InlineData("/Lat1 sh", "/Lat2 sh")]
    [InlineData("/Coons1 sh", "/Coons2 sh")]
    public void EachMeshPiece_IsChargedItsAreaAtTheShadingPrice(string one, string two)
    {
        Assert.Equal(150_000, Spent(two, out _) - Spent(one, out _));
    }

    /// <summary>
    /// A mesh is rebuilt into a page-sized bitmap at every <c>sh</c>, and that bitmap is an offscreen render
    /// the page pays for: under a ceiling of two and a half canvases the third is refused and reported once.
    /// The control, under the default ceiling, refuses none.
    /// </summary>
    [Theory]
    [InlineData("/Coons1", 25_000L, 1)]
    [InlineData("/Tri1", 25_000L, 1)]
    [InlineData("/Coons1", PdfLimits.DefaultMaxOffscreenPixelsPerPage, 0)]
    public void AMeshRebuiltAtEverySh_ChargesItsBitmap(string mesh, long ceiling, int reports)
    {
        try
        {
            PdfLimits.MaxOffscreenPixelsPerPage = ceiling;
            var reported = RenderFormXObjectBudgetTests.CaptureDiagnostics(() => Spent(string.Concat(Enumerable.Repeat(mesh + " sh ", 4)), out _));

            Assert.Equal(reports, reported.Count(d => d.Detail != null && d.Detail.Contains("offscreen renders", System.StringComparison.Ordinal)));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// A mesh of many full-page patches stops where the page's paint work runs out — reported once — rather
    /// than rasterising every patch it lists: under a ceiling of three canvases at the shading's price, the
    /// rest of the forty are left undrawn.
    /// </summary>
    [Fact]
    public void AdversarialMeshOfManyFullPagePatches_StopsAtThePaintCeiling()
    {
        try
        {
            PdfLimits.MaxPaintWorkPerPage = 450_000;
            double spent = 0;
            var reported = RenderFormXObjectBudgetTests.CaptureDiagnostics(() => spent = Spent("/Coons40 sh", out _));

            Assert.Equal(1, reported.Count(d => d.Kind == RenderDiagnosticKind.PaintWorkExceeded));
            Assert.InRange(spent, 450_000, 600_000);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// The SVG writer rasterises a mesh through an interpreter of its own, which runs under the page's budget:
    /// a two-patch mesh under a ceiling of one patch is refused, reported once, and draws nothing (no
    /// vector approximation stands in for a refusal). The control, under the default ceiling, is drawn.
    /// </summary>
    [Theory]
    [InlineData(200_000L, 1)]
    [InlineData(PdfLimits.DefaultMaxPaintWorkPerPage, 0)]
    public void TheSvgWritersMeshRaster_RunsUnderThePagesBudget(long ceiling, int reports)
    {
        try
        {
            PdfLimits.MaxPaintWorkPerPage = ceiling;
            string svg = null;
            var reported = RenderFormXObjectBudgetTests.CaptureDiagnostics(() =>
                svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(Pdf(100, "/Coons2 sh")), 0));

            Assert.Equal(reports == 0, svg.Contains("<image", System.StringComparison.Ordinal));   // drawn, or refused whole
            Assert.Equal(reports, reported.Count(d => d.Kind == RenderDiagnosticKind.PaintWorkExceeded));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    // ------------------------------------------------------------------ repeated image decodes

    /// <summary>
    /// A stencil mask is decoded again at every <c>Do</c> (its bitmap is the fill colour in force, so the cache
    /// does not hold it), and an unfiltered one spends no codec budget: each repeat is charged its pixels to the
    /// page's image-decoding budget. Under two and a half repeats of a hundred-by-hundred mask the fourth and
    /// fifth draws are refused and reported once; the control draws all five.
    /// </summary>
    [Theory]
    [InlineData(25_000L, 3, 1)]
    [InlineData(PdfLimits.DefaultMaxImageDecodeWorkPerPage, 5, 0)]
    public void ARepeatedlyDecodedImage_IsChargedEachRepeat(long ceiling, int drawn, int reports)
    {
        try
        {
            PdfLimits.MaxImageDecodeWorkPerPage = ceiling;
            const string content = "0 g q 100 0 0 100 0 0 cm /St Do Q q 100 0 0 100 100 0 cm /St Do Q q 100 0 0 100 200 0 cm /St Do Q " +
                                   "q 100 0 0 100 300 0 cm /St Do Q q 100 0 0 100 400 0 cm /St Do Q";
            SKBitmap bitmap = null;
            var reported = RenderFormXObjectBudgetTests.CaptureDiagnostics(() =>
            {
                using var doc = PdfDocument.Open(new MemoryStream(Pdf(500, content, 100)));
                bitmap = Render(doc, content, 500, 100, out _);
            });

            using (bitmap)
            {
                int painted = Enumerable.Range(0, 5).Count(i => bitmap.GetPixel(i * 100 + 50, 50).Red == 0);
                Assert.Equal(drawn, painted);
            }
            Assert.Equal(reports, reported.Count(d => d.Kind == RenderDiagnosticKind.ImageDecodeFailed
                && d.Detail != null && d.Detail.Contains("MaxImageDecodeWorkPerPage", System.StringComparison.Ordinal)));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    // ------------------------------------------------------------------ separation plates

    /// <summary>
    /// Each spot ink is a whole render of the page and a page-sized plate kept, so the number of spot plates
    /// is capped: under a cap of two, a page naming three inks has the four process plates and the first two
    /// spots, and says so once. The control has all three.
    /// </summary>
    [Theory]
    [InlineData(2, 6, 1)]
    [InlineData(PdfLimits.DefaultMaxSpotPlatesPerPage, 7, 0)]
    public void SpotPlates_AreCappedAPage_AndTheCapSaid(int cap, int plates, int reports)
    {
        try
        {
            PdfLimits.MaxSpotPlatesPerPage = cap;
            PdfSeparations seps = null;
            var reported = RenderFormXObjectBudgetTests.CaptureDiagnostics(() =>
                seps = PdfSeparationRenderer.RenderPage(new MemoryStream(SpotInksPdf(overprint: false)), 0, 72));

            Assert.Equal(plates, seps.Plates.Count);
            Assert.Equal(new[] { "Cyan", "Magenta", "Yellow", "Black", "Ink0", "Ink1", "Ink2" }.Take(plates),
                seps.Plates.Select(p => p.Colorant));
            Assert.Equal(reports, reported.Count(d => d.Kind == RenderDiagnosticKind.SpotPlatesCapped));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// The overprint preview recombines the plates the cap left: the first ink still shows red, and the ink
    /// past the cap, which has no plate, shows nothing. The control shows both.
    /// </summary>
    [Theory]
    [InlineData(1, 255)]
    [InlineData(PdfLimits.DefaultMaxSpotPlatesPerPage, 0)]
    public void TheOverprintPreview_RecombinesThePlatesTheCapLeft(int cap, int lastInkGreen)
    {
        try
        {
            PdfLimits.MaxSpotPlatesPerPage = cap;
            byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(SpotInksPdf(overprint: true)), 0, 72,
                new PdfRenderOptions { SimulateOverprint = true });

            using var bitmap = SKBitmap.Decode(png);
            SKColor first = bitmap.GetPixel(50, 50), last = bitmap.GetPixel(250, 50);
            Assert.True(first.Red > 200 && first.Green < 60, $"the first ink shows {first}");
            Assert.Equal((byte)lastInkGreen, last.Green);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>Three Separation inks, each red through its tint transform, filling one hundred-point square each.</summary>
    private static byte[] SpotInksPdf(bool overprint)
    {
        const string red = "<< /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [1 0 0] /N 1 >>";
        string spaces = string.Concat(Enumerable.Range(0, 3).Select(i => $" /C{i} [/Separation /Ink{i} /DeviceRGB {red}]"));
        string gs = overprint ? "/GS0 gs " : "";
        string content = gs + string.Concat(Enumerable.Range(0, 3).Select(i => $"/C{i} cs 1 scn {i * 100} 0 100 100 re f\n"));
        return RenderFormXObjectBudgetTests.Assemble(new List<string>
        {
            RenderFormXObjectBudgetTests.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            RenderFormXObjectBudgetTests.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RenderFormXObjectBudgetTests.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 100] /Contents 4 0 R " +
                "/Resources << /ColorSpace <<" + spaces + " >> /ExtGState << /GS0 << /Type /ExtGState /OP true /op true >> >> >> >>"),
            RenderFormXObjectBudgetTests.StreamObj(4, "", content),
        });
    }

    // ------------------------------------------------------------------ harness

    /// <summary>The paint work <paramref name="content"/> spends on a hundred-pixel canvas at one pixel a point.</summary>
    private static double Spent(string content, out RenderBudget budget)
    {
        using var doc = PdfDocument.Open(new MemoryStream(Pdf(100, content)));
        using var bitmap = Render(doc, content, 100, 100, out budget);
        return budget.PaintWorkSpent;
    }

    private static SKBitmap Render(PdfDocument doc, string content, int width, int height, out RenderBudget budget)
    {
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
    /// A page <paramref name="width"/> points wide carrying <paramref name="content"/> and the meshes and mask
    /// above. Every mesh piece covers the whole hundred-point square: a free-form triangle (<c>/Tri1</c>,
    /// <c>/Tri2</c>: one and two), a lattice cell (<c>/Lat1</c>, <c>/Lat2</c>), a Coons patch (<c>/Coons1</c>,
    /// <c>/Coons2</c>, <c>/Coons40</c>). <c>/St</c> is a hundred-by-hundred stencil mask that paints everywhere.
    /// </summary>
    private static byte[] Pdf(int width, string content, int height = 100)
    {
        const string mesh = " /ColorSpace /DeviceGray /BitsPerCoordinate 8 /BitsPerComponent 8 /Decode [0 100 0 100 0 1]";
        string triangle = Bytes(0, 0, 0, 0, 0, 255, 0, 128, 0, 0, 255, 255);
        string row = Bytes(0, 0, 0, 255, 0, 128), top = Bytes(0, 255, 0, 255, 255, 255);
        string patch = Bytes(0, 0, 0, 0, 85, 0, 170, 0, 255, 85, 255, 170, 255, 255, 255, 255, 170, 255, 85, 255, 0, 170, 0, 85, 0,
                             0, 80, 160, 255);
        string Coons(int n) => string.Concat(Enumerable.Repeat(patch, n));
        return RenderFormXObjectBudgetTests.Assemble(new List<string>
        {
            RenderFormXObjectBudgetTests.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            RenderFormXObjectBudgetTests.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RenderFormXObjectBudgetTests.Obj(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Contents 4 0 R " +
                "/Resources << /XObject << /St 12 0 R >> /Shading << /Tri1 5 0 R /Tri2 6 0 R /Lat1 7 0 R /Lat2 8 0 R " +
                "/Coons1 9 0 R /Coons2 10 0 R /Coons40 11 0 R >> >> >>"),
            RenderFormXObjectBudgetTests.StreamObj(4, "", content + "\n"),
            RenderFormXObjectBudgetTests.StreamObj(5, " /ShadingType 4 /BitsPerFlag 8" + mesh, triangle),
            RenderFormXObjectBudgetTests.StreamObj(6, " /ShadingType 4 /BitsPerFlag 8" + mesh, triangle + triangle),
            RenderFormXObjectBudgetTests.StreamObj(7, " /ShadingType 5 /VerticesPerRow 2" + mesh, row + top),
            RenderFormXObjectBudgetTests.StreamObj(8, " /ShadingType 5 /VerticesPerRow 2" + mesh, row + top + row),
            RenderFormXObjectBudgetTests.StreamObj(9, " /ShadingType 6 /BitsPerFlag 8" + mesh, Coons(1)),
            RenderFormXObjectBudgetTests.StreamObj(10, " /ShadingType 6 /BitsPerFlag 8" + mesh, Coons(2)),
            RenderFormXObjectBudgetTests.StreamObj(11, " /ShadingType 6 /BitsPerFlag 8" + mesh, Coons(40)),
            RenderFormXObjectBudgetTests.StreamObj(12, " /Type /XObject /Subtype /Image /Width 100 /Height 100 /ImageMask true /BitsPerComponent 1",
                new string('\0', 13 * 100)),
        });
    }

    private static string Bytes(params int[] values) => new string(values.Select(v => (char)v).ToArray());
}
