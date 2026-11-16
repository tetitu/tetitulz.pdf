// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for gap 28.2 — Type 2 (Shading) patterns.
/// All PDFs are built in memory.
/// </summary>
public sealed class ShadingPatternTests
{
    // ------------------------------------------------------------------
    //  Type 2 shading pattern — axial gradient filling a rectangle
    // ------------------------------------------------------------------

    // 1 pt = 1 px at 72 dpi, so the probes below are PDF user-space coordinates.
    private static bool IsUnpainted(SKColor c) => c.Red > 245 && c.Green > 245 && c.Blue > 245;

    [Fact]
    public void Type2Pattern_AxialShading_FillsRectWithTheGradient()
    {
        // /Coords [0 0 612 0], /C0 red → /C1 blue, painted through a PatternType 2 fill of the
        // whole page.  The rect must come out as the gradient, not as a flat colour and not blank.
        using var bmp = PixelAssert.Render(BuildPdfWithShadingPattern(shadingType: 2 /* axial */));

        SKColor left  = PixelAssert.At(bmp,   5f, 399f);
        SKColor mid   = PixelAssert.At(bmp, 306f, 399f);
        SKColor right = PixelAssert.At(bmp, 605f, 399f);

        Assert.True(left.Red > 200 && left.Blue < 60,   $"t≈0 should be /C0 red; got {left}.");
        Assert.True(PixelAssert.Near(mid, new SKColor(128, 0, 128), tolerance: 20),
            $"t=0.5 should be an even red/blue mix; got {mid}.");
        Assert.True(right.Blue > 200 && right.Red < 60, $"t≈1 should be /C1 blue; got {right}.");

        // Strictly monotonic along the axis — a flat fill or a mirrored gradient fails this.
        SKColor quarter = PixelAssert.At(bmp, 153f, 399f);
        Assert.True(left.Red > quarter.Red && quarter.Red > mid.Red && mid.Red > right.Red,
            "red must fall monotonically from the axis start to its end.");
    }

    [Fact]
    public void Type2Pattern_RadialShading_PaintsTheDisc()
    {
        // Radial shading (ShadingType 3) as a Type 2 pattern: /Coords [306 396 0 306 396 300],
        // red at the centre → blue at r=300, and /Extend defaulting to false, so the page
        // corners — 493 units from the centre — are left unpainted even though the fill covers
        // the whole MediaBox.
        using var bmp = PixelAssert.Render(BuildPdfWithShadingPattern(shadingType: 3 /* radial */));

        SKColor centre = PixelAssert.At(bmp, 306f, 395f);
        Assert.True(centre.Red > 200 && centre.Blue < 60, $"centre should be /C0 red; got {centre}.");

        SKColor rim = PixelAssert.At(bmp, 596f, 395f);
        Assert.True(rim.Blue > 200 && rim.Red < 60, $"r≈290 should be close to /C1 blue; got {rim}.");

        SKColor halfway = PixelAssert.At(bmp, 306f, 541f);
        Assert.True(PixelAssert.Near(halfway, new SKColor(128, 0, 128), tolerance: 25),
            $"r=145 is roughly half the radius, so red and blue should be balanced; got {halfway}.");

        Assert.True(IsUnpainted(PixelAssert.At(bmp, 5f, 4f)),
            "the corner is outside r=300 and /Extend is false, so it must stay unpainted.");
    }

    [Fact]
    public void Type1TilingPattern_TilesTheCellAcrossThePage_Regression()
    {
        // Type 1 tiling pattern must continue to render correctly after the Type 2 branch was
        // added.  The cell is a red 10×10 square with /XStep /YStep 10, so tiling it over the
        // fill leaves the entire page red — with no gaps between the cells.
        using var bmp = PixelAssert.Render(BuildPdfWithTilingPattern());

        var red = new SKColor(255, 0, 0);
        foreach (var (x, y) in new[] { (5f, 786f), (105f, 386f), (600f, 11f), (300f, 391f) })
        {
            SKColor c = PixelAssert.At(bmp, x, y);
            Assert.True(PixelAssert.Near(c, red, tolerance: 6),
                $"the tile colour should cover user ({x},{y}); got {c}.");
        }

        // Not just a few sampled points: essentially every pixel is a tile pixel.
        int redPixels = PixelAssert.CountColor(bmp, red, tolerance: 6);
        Assert.True(redPixels > bmp.Width * bmp.Height * 9 / 10,
            $"the tiling should leave no gaps; only {redPixels} of {bmp.Width * bmp.Height} pixels are red.");
    }

    [Fact]
    public void Type2Pattern_WithMatrix_ShiftsTheGradientOrigin()
    {
        // PatternType 2 with /Matrix [1 0 0 1 100 200] over /Coords [0 0 200 0] (green → red).
        // The axis therefore runs from user x=100 to x=300, not from 0 to 200.  /Extend is false,
        // so the two probes outside that span are the ones that prove the /Matrix was applied:
        // ignoring it would paint x=50 and leave x=295 unpainted instead.
        using var bmp = PixelAssert.Render(BuildPdfWithShadingPatternAndMatrix());

        SKColor start = PixelAssert.At(bmp, 105f, 399f);
        Assert.True(start.Green > 200 && start.Red < 60,
            $"just past the shifted axis start the colour should be /C0 green; got {start}.");

        SKColor end = PixelAssert.At(bmp, 295f, 399f);
        Assert.True(end.Red > 200 && end.Green < 60,
            $"just before the shifted axis end the colour should be /C1 red; got {end}.");

        Assert.True(IsUnpainted(PixelAssert.At(bmp, 50f, 399f)),
            "x=50 is before the shifted axis origin, so it must be unpainted.");
        Assert.True(IsUnpainted(PixelAssert.At(bmp, 400f, 399f)),
            "x=400 is past the shifted axis end, so it must be unpainted.");
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a minimal PDF that paints a rectangle filled with a Type 2
    /// (shading) pattern backed by an axial (type 2) or radial (type 3) shading.
    /// </summary>
    private static byte[] BuildPdfWithShadingPattern(int shadingType)
    {
        // Axial: /Coords [x0 y0 x1 y1]; Radial: /Coords [x0 y0 r0 x1 y1 r1]
        string coords = shadingType == 2
            ? "/Coords [0 0 612 0]"
            : "/Coords [306 396 0 306 396 300]";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // Shading dict (obj 6)
        int o6 = sb.Length;
        sb.AppendFormat(
            "6 0 obj\n<< /ShadingType {0} /ColorSpace /DeviceRGB {1}" +
            " /Function << /FunctionType 2 /Domain [0 1]" +
            " /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >>\nendobj\n",
            shadingType, coords);

        // Pattern dict referencing the shading (obj 7)
        int o7 = sb.Length;
        sb.Append("7 0 obj\n<< /PatternType 2 /Shading 6 0 R >>\nendobj\n");

        // Content stream: set pattern colour space then fill a rect (obj 4)
        const string cs = "/Pattern cs /P1 scn 0 0 612 792 re f";
        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Resources << /Pattern << /P1 7 0 R >>" +
            " /Font << /F1 5 0 R >> >> >>\nendobj\n");

        int o4 = sb.Length;
        sb.AppendFormat(
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);

        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.AppendFormat("{0:D10} 00000 n \n", o7);
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithShadingPatternAndMatrix()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int o6 = sb.Length;
        sb.Append(
            "6 0 obj\n<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0]" +
            " /Function << /FunctionType 2 /Domain [0 1]" +
            " /C0 [0 1 0] /C1 [1 0 0] /N 1 >> >>\nendobj\n");

        // Pattern with a translate matrix [1 0 0 1 100 200]
        int o7 = sb.Length;
        sb.Append(
            "7 0 obj\n<< /PatternType 2 /Shading 6 0 R /Matrix [1 0 0 1 100 200] >>\nendobj\n");

        const string cs = "/Pattern cs /P1 scn 0 0 612 792 re f";
        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Resources << /Pattern << /P1 7 0 R >>" +
            " /Font << /F1 5 0 R >> >> >>\nendobj\n");

        int o4 = sb.Length;
        sb.AppendFormat(
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);

        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.AppendFormat("{0:D10} 00000 n \n", o7);
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithTilingPattern()
    {
        // Simple coloured tiling pattern (red 10x10 tiles)
        const string tileCs = "1 0 0 rg 0 0 10 10 re f";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // Tiling pattern stream (obj 6)
        int o6 = sb.Length;
        sb.AppendFormat(
            "6 0 obj\n<< /PatternType 1 /PaintType 1 /TilingType 1" +
            " /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << >>" +
            " /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            tileCs.Length, tileCs);

        const string cs = "/Pattern cs /P1 scn 0 0 612 792 re f";
        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Resources << /Pattern << /P1 6 0 R >>" +
            " /Font << /F1 5 0 R >> >> >>\nendobj\n");

        int o4 = sb.Length;
        sb.AppendFormat(
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);

        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
