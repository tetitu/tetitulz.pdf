// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Regression tests for acute miter joins with a large miter limit (PDF 32000-2
/// §8.4.3.5), exercised by raw/LargeMitreLimit.pdf (line↔line) and
/// raw/LargeMitreLimit-Beziers.pdf (line↔curve).
///
/// SkiaSharp caps the miter join internally and drops the long "spike" the spec
/// mandates once a join is sharper than ratio ≈50, regardless of the requested
/// limit. PdfRenderContext detects joins permitted by a large miter limit but
/// too sharp for Skia and renders them itself: it strokes the body with a bevel
/// join and grafts an explicit miter triangle (computed from the true segment /
/// analytic curve tangents) onto each bevel, unioned into one fill so a
/// translucent stroke composites correctly.
/// </summary>
public sealed class MiterJoinCurveRenderingTests
{
    private const int PageW = 200;
    private const int PageH = 700;

    // Acute hairpin join at the vertex (100,200): a vertical line arrives going
    // up, then a cubic leaves heading almost straight back down (first control
    // at 100.9,150).  With a miter join + large limit this must produce a long
    // spike pointing up (toward higher Y / lower image rows).
    private static byte[] BuildMiterCurvePdf(int join, string miterLimit)
    {
        string cs =
            "10 w\n" +
            join + " j\n" +
            miterLimit + " M\n" +
            "1 0 0 RG\n" +
            "75 125 m\n" +
            "100 150 l\n" +
            "100 200 l\n" +
            "100.9 150 150 150 175 175 c\n" +
            "S\n";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int o3 = sb.Length;
        sb.AppendFormat(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {0} {1}]" +
            " /Contents 4 0 R /Resources << >> >>\nendobj\n", PageW, PageH);

        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);

        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static bool IsRed(SKColor c) => c.Red > 150 && c.Green < 100 && c.Blue < 100;

    /// <summary>Topmost (minimum) image row containing a red stroke pixel, or -1.</summary>
    private static int TopmostRedRow(byte[] png)
    {
        using var bmp = SKBitmap.Decode(png);
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (IsRed(bmp.GetPixel(x, y)))
                    return y;
        return -1;
    }

    [Fact]
    public void MiterJoin_AcuteLineToCurve_RendersLongSpike()
    {
        // Render at 72 DPI so 1 image pixel ≈ 1 PDF unit; vertex (100,200) sits
        // at image row ≈ PageH - 200 = 500.  The spike points up, so its tip is
        // at a much smaller row number.
        byte[] pdf = BuildMiterCurvePdf(join: 0, miterLimit: "200");
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72);

        int topRow = TopmostRedRow(png);
        Assert.True(topRow >= 0, "no stroke pixels rendered");

        // Vertex row ≈ 500.  Pre-fix the spike was clamped to a stub (topmost red
        // near the vertex, row ≳ 470).  The spike must now reach at least ~150
        // PDF units above the vertex (row ≤ 350).
        Assert.True(topRow <= 350,
            $"miter spike too short: topmost red row was {topRow}, expected <= 350");
    }

    [Fact]
    public void MiterJoin_ProducesTallerSpikeThanBevelJoin()
    {
        // Same geometry, different join. A bevel join (2) forms no spike, so its
        // topmost stroke pixel stays near the vertex; the miter spike reaches far
        // higher (a much smaller row number).
        byte[] miterPdf = BuildMiterCurvePdf(join: 0, miterLimit: "200");
        byte[] bevelPdf = BuildMiterCurvePdf(join: 2, miterLimit: "200");

        int miterTop = TopmostRedRow(PdfPageRenderer.RenderPage(new MemoryStream(miterPdf), 0, 72));
        int bevelTop = TopmostRedRow(PdfPageRenderer.RenderPage(new MemoryStream(bevelPdf), 0, 72));

        Assert.True(miterTop >= 0 && bevelTop >= 0, "stroke pixels missing");
        // Smaller row == higher on the page. The miter spike must rise well above
        // the bevel join (at least 100 px / PDF units higher).
        Assert.True(miterTop < bevelTop - 100,
            $"miter spike ({miterTop}) not clearly taller than bevel ({bevelTop})");
    }

    [Fact]
    public void BevelJoin_AcuteLineToCurve_HasNoTallSpike()
    {
        // A bevel join must NOT grow a spike regardless of miter limit.
        byte[] pdf = BuildMiterCurvePdf(join: 2, miterLimit: "200");
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72);

        int topRow = TopmostRedRow(png);
        Assert.True(topRow >= 0, "no stroke pixels rendered");
        // Bevel stays near the vertex (row ≈ 500); allow margin for stroke width.
        Assert.True(topRow >= 440,
            $"bevel join unexpectedly produced a spike: topmost red row was {topRow}");
    }

    // ------------------------------------------------------------------
    //  Line ↔ line miter join (raw/LargeMitreLimit.pdf) — Skia caps these
    //  internally and drops the spike entirely; PdfRenderContext draws it.
    // ------------------------------------------------------------------

    // Acute hairpin made purely of straight lines: up to the vertex (100,200),
    // then almost straight back down to (100.9,125). With a miter join + large
    // limit this must produce a long spike pointing up.
    private static byte[] BuildMiterLinePdf(int join, string miterLimit)
    {
        string cs =
            "10 w\n" +
            join + " j\n" +
            miterLimit + " M\n" +
            "1 0 0 RG\n" +
            "75 125 m\n" +
            "100 150 l\n" +
            "100 200 l\n" +
            "100.9 125 l\n" +
            "S\n";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.AppendFormat(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {0} {1}]" +
            " /Contents 4 0 R /Resources << >> >>\nendobj\n", PageW, PageH);
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void MiterJoin_AcuteLineToLine_RendersLongSpike()
    {
        // Vertex (100,200) ≈ image row 500. The line↔line spike (ratio ≈167) is
        // even taller than the curve case and reaches the top of the page.
        byte[] pdf = BuildMiterLinePdf(join: 0, miterLimit: "333");
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72);

        int topRow = TopmostRedRow(png);
        Assert.True(topRow >= 0, "no stroke pixels rendered");
        // Pre-fix Skia produced only a stub near the vertex (row ≳ 480). The
        // manual miter must reach far above it.
        Assert.True(topRow <= 300,
            $"line miter spike too short: topmost red row was {topRow}, expected <= 300");
    }

    [Fact]
    public void MiterJoin_LineToLine_TallerThanBevel()
    {
        byte[] miterPdf = BuildMiterLinePdf(join: 0, miterLimit: "333");
        byte[] bevelPdf = BuildMiterLinePdf(join: 2, miterLimit: "333");

        int miterTop = TopmostRedRow(PdfPageRenderer.RenderPage(new MemoryStream(miterPdf), 0, 72));
        int bevelTop = TopmostRedRow(PdfPageRenderer.RenderPage(new MemoryStream(bevelPdf), 0, 72));

        Assert.True(miterTop >= 0 && bevelTop >= 0, "stroke pixels missing");
        Assert.True(miterTop < bevelTop - 150,
            $"line miter spike ({miterTop}) not clearly taller than bevel ({bevelTop})");
    }

    /// <summary>
    /// A miter join on a path containing a CURVE, at a limit Skia handles itself, still strokes the
    /// whole path — the case <c>FlattenCurvesForMiter</c> exists for, and the one nothing reached.
    /// </summary>
    /// <remarks>
    /// <para><b>Every other test here misses this path.</b> The large-limit curve cases take the
    /// manual spike route, which strokes <c>_currentPath</c> directly; the bevel cases return before
    /// the flattener because the join is not Miter; and
    /// <see cref="MiterJoin_DefaultLimit_DoesNotGrowSpikeForVeryAcuteJoin"/> is line-to-line, so its
    /// path carries no curve verb and the flattener declines. Miter + curve + a limit Skia can
    /// serve is the only combination that runs it, and it had no test: a mutation making the
    /// flattener return an empty path reddened nothing.</para>
    ///
    /// <para>The assertion is ink at the curve's far end, not merely "some pixels came back". An
    /// empty flattened path loses the whole stroke, but so would a flattener that dropped only the
    /// curve verbs while keeping the lines — and that is the likelier defect, since flattening is
    /// exactly the curve-handling step.</para>
    /// </remarks>
    [Fact]
    public void MiterJoin_CurveAtDefaultLimit_StillStrokesTheWholeCurve()
    {
        byte[] pdf = BuildMiterCurvePdf(join: 0, miterLimit: "10");
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72);

        Assert.True(TopmostRedRow(png) >= 0, "no stroke pixels rendered at all");

        // The curve runs from the vertex (100,200) to (175,175). At 72 DPI on a 200x700 page one
        // image pixel is one PDF unit and row = 700 - y, so its far end sits near (175, 525).
        Assert.True(HasRedNear(png, 175, 525, 8),
            "the curve's far end is unstroked, so the flattened path lost its curve segments");
    }

    /// <summary>True when any pixel within <paramref name="radius"/> of (x, y) is stroke red.</summary>
    private static bool HasRedNear(byte[] png, int x, int y, int radius)
    {
        using var bmp = SKBitmap.Decode(png);
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int px = x + dx, py = y + dy;
                if (px < 0 || py < 0 || px >= bmp.Width || py >= bmp.Height) continue;
                if (IsRed(bmp.GetPixel(px, py))) return true;
            }
        return false;
    }

    [Fact]
    public void MiterJoin_DefaultLimit_DoesNotGrowSpikeForVeryAcuteJoin()
    {
        // With the default limit (10) the ratio-167 join exceeds the limit, so it
        // must bevel — no spike — exactly as Skia would render it. Guards against
        // the manual path firing when the miter is not actually permitted.
        byte[] pdf = BuildMiterLinePdf(join: 0, miterLimit: "10");
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72);

        int topRow = TopmostRedRow(png);
        Assert.True(topRow >= 0, "no stroke pixels rendered");
        Assert.True(topRow >= 440,
            $"limit-exceeding miter should bevel, but a spike appeared at row {topRow}");
    }
}
