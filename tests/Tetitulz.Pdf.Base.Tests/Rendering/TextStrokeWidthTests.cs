// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A stroked text run's pen is the graphics state's line width <b>scaled by the CTM</b>
/// (§8.4.3.2, §9.3.6) — the same rule a path stroke follows, and the SVG exporter's.
/// </summary>
/// <remarks>
/// <para><b>A measurement.</b> Doubling the CTM and halving the font size leaves the glyph
/// geometry identical, so a stroked run's ink area must double as a path stroke's does on the same
/// page. The raw line width under a transform normalised to unit scale would leave it at 1.00x —
/// the pen never seeing the CTM — and disagree with the SVG writer's own tests
/// (<c>TextStrokeWidth_AccountsForTheCtmAsWellAsTheFontSize</c>) on every stroked run under a
/// scaled CTM.</para>
///
/// <para><b>The fixture is shaped so the pen cannot saturate.</b> Stroking 40 pt glyphs with a 4 pt
/// pen measures about 1.52x: doubling that pen merges the inner stroke bands across a Helvetica
/// stem, so the area stops growing with the width. A 1 pt pen on
/// 80 pt glyphs at 144 dpi keeps the pen a small fraction of every feature, and a 50% luminance
/// threshold counts anti-aliased fringe without bias — the path control below, which has no fringe
/// and cannot merge, is what says "doubles" means 2.00x on this rasteriser.</para>
///
/// <para><b>Assertions are ratios of ink areas, not pixel positions</b>, because which face the
/// platform substitutes for Helvetica differs between hosts.</para>
/// </remarks>
public class TextStrokeWidthTests
{
    private const int Dpi = 144;

    /// <summary>The pen doubles with the CTM, as a path's does.</summary>
    [Fact]
    public void AStrokedRunsPenScalesWithTheCtm()
    {
        // Same glyph geometry both times: the font size halves as the CTM doubles.
        int unscaled = DarkPixels("BT /F1 80 Tf 1 Tr 1 w 10 20 Td (IIII) Tj ET");
        int scaled   = DarkPixels("q 2 0 0 2 0 0 cm BT /F1 40 Tf 1 Tr 1 w 5 10 Td (IIII) Tj ET Q");

        double ratio = (double)scaled / unscaled;
        Assert.True(ratio > 1.7 && ratio < 2.3,
            $"a 2x CTM should double the pen's ink area; measured {unscaled} -> {scaled} ({ratio:F2}x)");
    }

    /// <summary>
    /// The control: a path stroke on the same page under the same CTMs, whose pen scales. If this
    /// ratio ever stops being ~2, the text assertion above is measuring the
    /// rasteriser rather than the rule.
    /// </summary>
    [Fact]
    public void APathStrokesPenScalesWithTheCtm_Control()
    {
        int unscaled = DarkPixels("1 w 10 50 m 150 50 l S");
        int scaled   = DarkPixels("q 2 0 0 2 0 0 cm 1 w 5 25 m 75 25 l S Q");

        double ratio = (double)scaled / unscaled;
        Assert.True(ratio > 1.7 && ratio < 2.3,
            $"a 2x CTM should double a path pen's ink area; measured {unscaled} -> {scaled} ({ratio:F2}x)");
    }

    /// <summary>
    /// The pen does <b>not</b> scale with the font size — §9.3.6 says the line width is in user
    /// space, not text space, so a larger font gets the same pen.
    /// </summary>
    /// <remarks>
    /// The discriminating control: a change that scaled the pen by the whole text
    /// matrix instead of the CTM alone would pass the test above and fail this one. Doubling the
    /// font size doubles the outline's perimeter, so the stroked area doubles from length alone; a
    /// pen that also doubled would quadruple it. The window is wide enough for the perimeter's
    /// own rounding and narrow enough to exclude the square.
    /// </remarks>
    [Fact]
    public void AStrokedRunsPenDoesNotScaleWithTheFontSize()
    {
        int small = DarkPixels("BT /F1 40 Tf 1 Tr 1 w 10 20 Td (IIII) Tj ET");
        int large = DarkPixels("BT /F1 80 Tf 1 Tr 1 w 10 20 Td (IIII) Tj ET");

        double ratio = (double)large / small;
        Assert.True(ratio > 1.5 && ratio < 3.0,
            $"doubling the font size doubles the perimeter, not the pen; measured {small} -> {large} ({ratio:F2}x)");
    }

    // ----------------------------------------------------------------

    /// <summary>
    /// Pixels darker than mid-grey. A 50% threshold is unbiased over an anti-aliased edge: the
    /// fringe pixels above it and below it cancel, so the count tracks the covered area rather
    /// than the area plus a one-pixel border per edge.
    /// </summary>
    private static int DarkPixels(string content)
    {
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(BuildPdf(content)), 0, Dpi);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);

        int n = 0;
        for (int y = 0; y < bmp!.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                if (c.Red + c.Green + c.Blue < 3 * 128) n++;
            }
        return n;
    }

    private static byte[] BuildPdf(string content)
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");
        var off = new List<int>();
        void Obj(string body) { off.Add(pdf.Length); pdf.Append(body); }
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 120] /Resources " +
            "<< /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(off.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        foreach (int o in off) pdf.Append(o.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(off.Count + 1)
           .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
