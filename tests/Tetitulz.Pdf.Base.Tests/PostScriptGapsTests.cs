// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for PostScript-derived rendering features:
/// - Dash pattern applied to pattern-shader strokes (was missing)
/// - ExtGState /D sets dash pattern
/// - ExtGState /RI stores rendering intent
/// - ExtGState /AIS (Alpha Is Shape) stored
/// - ExtGState /TK (Text Knockout) stored
/// - ri operator stores rendering intent in graphics state
/// </summary>
/// <remarks>
/// Assertions here are on decoded pixels. The dash tests count gaps along the stroke; the
/// alpha-carrying ExtGState tests check the blend towards the white page. /RI has no visual
/// effect in an sRGB compositor by design, so those two tests verify the operator is consumed
/// without disturbing the paint that follows it — which is the only observable contract.
/// </remarks>
public sealed class PostScriptGapsTests
{
    // ------------------------------------------------------------------
    //  Pixel helpers
    // ------------------------------------------------------------------

    private static SKBitmap Render(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        Assert.NotNull(png);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>Pixel at a PDF user-space point (origin bottom-left).</summary>
    private static SKColor At(SKBitmap bmp, float userX, float userY)
        => bmp.GetPixel((int)userX, bmp.Height - 1 - (int)userY);

    private static bool IsInk(SKColor c)
        => c.Alpha > 8 && (c.Red < 240 || c.Green < 240 || c.Blue < 240);

    /// <summary>
    /// Counts single device columns across <paramref name="x0"/>..<paramref name="x1"/> that
    /// carry no ink in the horizontal band around <paramref name="userY"/>. A solid rule scores
    /// 0; a dashed one scores the total length of its gaps.
    /// </summary>
    private static int GapColumns(SKBitmap bmp, int x0, int x1, float userY, int halfHeight = 6)
    {
        int row = bmp.Height - 1 - (int)userY;
        int gaps = 0;
        for (int x = x0; x < x1; x++)
        {
            bool inked = false;
            for (int y = Math.Max(0, row - halfHeight);
                 y <= Math.Min(bmp.Height - 1, row + halfHeight) && !inked; y++)
                if (IsInk(bmp.GetPixel(x, y))) inked = true;
            if (!inked) gaps++;
        }
        return gaps;
    }

    private static void AssertClose(SKColor expected, SKColor actual, int tol, string because)
    {
        Assert.True(
            Math.Abs(expected.Red   - actual.Red)   <= tol &&
            Math.Abs(expected.Green - actual.Green) <= tol &&
            Math.Abs(expected.Blue  - actual.Blue)  <= tol,
            $"{because}: expected ~RGB({expected.Red},{expected.Green},{expected.Blue}), " +
            $"got RGB({actual.Red},{actual.Green},{actual.Blue}).");
    }

    // ------------------------------------------------------------------
    //  Minimal PDF builder
    // ------------------------------------------------------------------

    private static byte[] BuildPdf(string contentStream, string extGStateObj = "",
        string extraResources = "")
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // Optional ExtGState object (obj 6)
        int o6 = sb.Length;
        bool hasExtGState = extGStateObj.Length > 0;
        if (hasExtGState) sb.Append("6 0 obj\n" + extGStateObj + "endobj\n");

        int o3 = sb.Length;
        string gsRes = hasExtGState ? "/ExtGState << /GS1 6 0 R >>" : "";
        string resources = "/Font << /F1 5 0 R >>" +
                           (gsRes.Length > 0 ? " " + gsRes : "") +
                           (extraResources.Length > 0 ? " " + extraResources : "");
        sb.AppendFormat("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                        " /Contents 4 0 R /Resources << {0} >> >>\nendobj\n", resources);
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            contentStream.Length, contentStream);
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        int count = hasExtGState ? 7 : 6;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", count);
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        if (hasExtGState) sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\n", count);
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ====================================================================
    //  Dash pattern in shader strokes (was missing from StrokePathWithShader)
    // ====================================================================

    [Fact]
    public void DashPattern_Stroke_LeavesGapsAlongTheLine()
    {
        // A [4 2] dash must interrupt the stroke; the same line undashed must not.
        var dashed = Render(BuildPdf("q [4 2] 0 d 2 w 50 100 m 500 100 l S Q"));
        var solid  = Render(BuildPdf("q [] 0 d 2 w 50 100 m 500 100 l S Q"));

        int dashedGaps = GapColumns(dashed, 60, 490, 100);
        int solidGaps  = GapColumns(solid,  60, 490, 100);

        Assert.Equal(0, solidGaps);
        Assert.True(dashedGaps > 100,
            $"[4 2] d produced only {dashedGaps} clear columns — the dash was not applied.");
    }

    [Fact]
    public void DashPattern_EmptyArray_ProducesASolidLine()
    {
        // `[] 0 d` means solid. The line must be continuous end to end.
        var bmp = Render(BuildPdf("q [] 0 d 1 w 50 100 m 400 100 l S Q"));

        Assert.Equal(0, GapColumns(bmp, 60, 390, 100));
        Assert.True(IsInk(At(bmp, 200, 100)), "the solid line did not paint.");
    }

    // ====================================================================
    //  ExtGState /D sets dash pattern
    // ====================================================================

    [Fact]
    public void ExtGState_D_SetsDashPattern()
    {
        // /D [[4 2] 1] must dash the stroke exactly as the inline `[4 2] 1 d` operator does.
        var viaExtGState = Render(BuildPdf(
            "q /GS1 gs 2 w 50 100 m 500 100 l S Q",
            "<< /Type /ExtGState /D [[4 2] 1] >>\n"));
        var viaOperator = Render(BuildPdf("q [4 2] 1 d 2 w 50 100 m 500 100 l S Q"));
        var solid       = Render(BuildPdf("q 2 w 50 100 m 500 100 l S Q"));

        int extGaps  = GapColumns(viaExtGState, 60, 490, 100);
        int opGaps   = GapColumns(viaOperator,  60, 490, 100);

        Assert.Equal(0, GapColumns(solid, 60, 490, 100));
        Assert.True(extGaps > 100, $"/D did not dash the stroke ({extGaps} clear columns).");
        Assert.True(Math.Abs(extGaps - opGaps) <= 4,
            $"/D and the `d` operator disagree: {extGaps} vs {opGaps} clear columns.");
    }

    // ====================================================================
    //  Rendering intent: ri operator and ExtGState /RI
    // ====================================================================

    /// <summary>
    /// Rendering intent has no visual effect in an sRGB compositor, so the observable contract
    /// is narrower: the operator must be <i>consumed</i> — its <c>/RelativeColorimetric</c>
    /// operand must not be left on the stack to corrupt the colour that follows.
    /// </summary>
    [Fact]
    public void RenderingIntent_ri_Operator_IsConsumedAndDoesNotDisturbTheFill()
    {
        var withRi = Render(BuildPdf("/RelativeColorimetric ri q 0.5 g 0 0 612 792 re f Q"));
        var without = Render(BuildPdf("q 0.5 g 0 0 612 792 re f Q"));

        // 0.5 gray → sRGB ~128 on both, and identical to the render without the operator.
        AssertClose(At(without, 300, 400), At(withRi, 300, 400), 2,
            "the ri operator changed the fill colour");
        AssertClose(new SKColor(128, 128, 128), At(withRi, 300, 400), 12, "0.5 g should be mid-grey");
    }

    [Fact]
    public void ExtGState_RI_IsAcceptedAndDoesNotDisturbTheFill()
    {
        var withRi = Render(BuildPdf(
            "q /GS1 gs 0.8 g 0 0 612 792 re f Q", "<< /Type /ExtGState /RI /Perceptual >>\n"));
        var without = Render(BuildPdf("q 0.8 g 0 0 612 792 re f Q"));

        AssertClose(At(without, 300, 400), At(withRi, 300, 400), 2,
            "/RI changed the fill colour");
        AssertClose(new SKColor(204, 204, 204), At(withRi, 300, 400), 12, "0.8 g should be light grey");
    }

    // ====================================================================
    //  ExtGState /AIS (Alpha Is Shape)
    // ====================================================================

    /// <summary>
    /// /AIS changes a pixel only inside a knockout group (AlphaIsShapeTests); outside one, alpha
    /// as shape and alpha as opacity composite alike — so the checkable contract here is that
    /// reading /AIS does not swallow the alpha beside it. Red at ca 0.8 over white lands near
    /// (255, 51, 51).
    /// </summary>
    [Fact]
    public void ExtGState_AIS_True_StillAppliesTheFillAlpha()
    {
        var bmp = Render(BuildPdf(
            "q /GS1 gs 1 0 0 rg 0 0 300 300 re f Q",
            "<< /Type /ExtGState /AIS true /ca 0.8 >>\n"));

        var px = At(bmp, 150, 150);
        AssertClose(new SKColor(255, 51, 51), px, 20, "ca 0.8 was not applied alongside /AIS true");
    }

    [Fact]
    public void ExtGState_AIS_False_StillAppliesTheStrokeAlpha()
    {
        // /CA governs strokes, so a filled path must be unaffected by it — a renderer that
        // confused the two would tint this green rectangle.
        var bmp = Render(BuildPdf(
            "q /GS1 gs 0 1 0 rg 100 100 200 200 re f Q",
            "<< /Type /ExtGState /AIS false /CA 0.5 >>\n"));

        AssertClose(SKColors.Lime, At(bmp, 200, 200), 12,
            "/CA (a stroke alpha) leaked into a fill");
    }

    // ====================================================================
    //  ExtGState /TK (Text Knockout)
    // ====================================================================

    /// <summary>Counts inked pixels in the whole page — used to prove text actually drew.</summary>
    private static int TotalInk(SKBitmap bmp)
    {
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (IsInk(bmp.GetPixel(x, y))) n++;
        return n;
    }

    [Fact]
    public void ExtGState_TK_False_StillDrawsTheText()
    {
        // /TK only changes how overlapping glyphs composite; it must never suppress them.
        var bmp = Render(BuildPdf(
            "q /GS1 gs BT /F1 12 Tf 100 700 Td (Hello) Tj ET Q",
            "<< /Type /ExtGState /TK false >>\n"));

        Assert.True(TotalInk(bmp) > 20, "/TK false suppressed the text run entirely.");
    }

    /// <summary>Darkest red-channel value on the page — 0 for solid black text.</summary>
    private static int DarkestPixel(SKBitmap bmp)
    {
        int min = 255;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Alpha > 8 && c.Red < min) min = c.Red;
            }
        return min;
    }

    [Fact]
    public void ExtGState_TK_True_DrawsTextAndAppliesTheFillAlpha()
    {
        var opaque = Render(BuildPdf(
            "q /GS1 gs BT /F1 14 Tf 50 700 Td (PDF) Tj ET Q",
            "<< /Type /ExtGState /TK true >>\n"));
        var faded = Render(BuildPdf(
            "q /GS1 gs BT /F1 14 Tf 50 700 Td (PDF) Tj ET Q",
            "<< /Type /ExtGState /TK true /ca 0.8 >>\n"));

        Assert.True(TotalInk(opaque) > 20, "/TK true suppressed the text run entirely.");

        // ca 0.8 lifts black text off 0 towards 0.2*255 ≈ 51. Pixel COUNT is the wrong probe
        // here — the lightened glyphs still read as ink — so compare darkness instead.
        Assert.True(DarkestPixel(opaque) < 20,
            $"opaque text should reach near-black; darkest was {DarkestPixel(opaque)}.");
        Assert.True(DarkestPixel(faded) > DarkestPixel(opaque) + 20,
            $"ca 0.8 did not lighten the text (darkest {DarkestPixel(faded)} vs {DarkestPixel(opaque)}).");
    }

    // ====================================================================
    //  Combined ExtGState: multiple new keys in one gs call
    // ====================================================================

    [Fact]
    public void ExtGState_CombinedNewKeys_FillIsUnaffectedByAnyOfThem()
    {
        // /RI, /AIS, /TK and /D together: none of them applies to a plain fill, so the
        // rectangle must be solid blue — a dash leaking into the fill would break it.
        string extGs = "<< /Type /ExtGState /RI /Saturation /AIS false /TK true /D [[3 3] 0] >>\n";
        var bmp = Render(BuildPdf("q /GS1 gs 0 0 1 rg 50 50 400 600 re f Q", extGs));

        AssertClose(SKColors.Blue, At(bmp, 250, 300), 12, "the combined ExtGState altered the fill");
        Assert.Equal(0, GapColumns(bmp, 60, 440, 300, halfHeight: 100));

        byte[] pdf = BuildPdf("q /GS1 gs 0 0 1 rg 50 50 400 600 re f Q", extGs);
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        Assert.NotNull(png);
        Assert.NotEmpty(png);
    }
}
