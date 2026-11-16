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
/// A selected pattern survives the painting operator that uses it, so a second shape under the
/// same <c>scn</c> is painted with it too.
/// </summary>
/// <remarks>
/// <para><b>§8.6.8 gives painting operators no say over the current colour.</b> A colour stays
/// selected until a colour operator replaces it — <c>g</c>, <c>rg</c>, <c>k</c>, or an
/// <c>sc</c>/<c>scn</c> naming something else. Nothing about <c>f</c> or <c>S</c> unselects a
/// pattern.</para>
///
/// <para><b>The raster renderer cleared the flag after painting and the SVG writer did not</b>, so
/// one file got two answers: the first shape came out patterned and the second came out solid
/// black, because clearing the flag left the graphics state's fill colour — black by default —
/// as the paint. The SVG writer clears the same flag only at colour operators, which is the
/// correct reading, and that is the behaviour these tests pin on both sides.</para>
///
/// <para>Repeating a fill under one <c>scn</c> is ordinary content: it is what any producer emits
/// for a run of shapes sharing a hatch or a texture.</para>
/// </remarks>
public class PatternPersistsAcrossPaintsTests
{
    /// <summary>A tiling pattern that paints its whole 8x8 cell red.</summary>
    private const string RedTile =
        "<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 " +
        "/BBox [0 0 8 8] /XStep 8 /YStep 8 /Resources << >> /Length 26 >>\n" +
        "stream\n1 0 0 rg 0 0 8 8 re f\nendstream";

    /// <summary>Two fills, one <c>scn</c>: both must be the pattern's red.</summary>
    [Fact]
    public void ASecondFillUnderOneScnIsStillPatterned()
    {
        using SKBitmap bmp = Render(Pdf(
            "/Pattern cs /P1 scn 10 10 30 30 re f 55 55 30 30 re f"));

        SKColor first  = bmp.GetPixel(Px(25), Py(bmp, 25));
        SKColor second = bmp.GetPixel(Px(70), Py(bmp, 70));

        Assert.True(IsRed(first),
            $"the control failed: the FIRST fill was not the pattern's red but {first}");
        Assert.True(IsRed(second),
            $"the second fill lost the pattern and came out {second}; a painting operator "
          + "does not unselect a colour (ISO 32000-1 §8.6.8)");
    }

    /// <summary>The same for strokes, which clear a separate flag on the same mistake.</summary>
    [Fact]
    public void ASecondStrokeUnderOneScnIsStillPatterned()
    {
        using SKBitmap bmp = Render(Pdf(
            "/Pattern CS /P1 SCN 6 w 10 10 30 30 re S 55 55 30 30 re S"));

        SKColor first  = bmp.GetPixel(Px(10), Py(bmp, 25));
        SKColor second = bmp.GetPixel(Px(55), Py(bmp, 70));

        Assert.True(IsRed(first),
            $"the control failed: the FIRST stroke was not the pattern's red but {first}");
        Assert.True(IsRed(second),
            $"the second stroke lost the pattern and came out {second}");
    }

    /// <summary>
    /// And for <c>B</c>, which fills and strokes one path and cleared a third flag of its own.
    /// </summary>
    /// <remarks>
    /// <b>Written because a mutation survived.</b> Restoring the clear in the fill-and-stroke
    /// operator left the tests above green: they use <c>f</c> and <c>S</c>, and <c>B</c> is a third
    /// call site with its own copy of the same mistake. Three operators, three fixtures.
    /// </remarks>
    [Fact]
    public void ASecondFillStrokeUnderOneScnIsStillPatterned()
    {
        using SKBitmap bmp = Render(Pdf(
            "/Pattern cs /P1 scn /Pattern CS /P1 SCN 2 w 10 10 30 30 re B 55 55 30 30 re B"));

        SKColor first  = bmp.GetPixel(Px(25), Py(bmp, 25));
        SKColor second = bmp.GetPixel(Px(70), Py(bmp, 70));

        Assert.True(IsRed(first),
            $"the control failed: the FIRST B was not the pattern's red but {first}");
        Assert.True(IsRed(second),
            $"the second B lost the pattern and came out {second}");
    }

    /// <summary>
    /// And the vector exporter agrees, which is the half that was already right.
    /// </summary>
    /// <remarks>
    /// Asserted so the pair cannot drift back apart in the other direction: a later change that
    /// "fixed" the SVG writer to match the old raster behaviour would redden this.
    /// </remarks>
    [Fact]
    public void TheSvgExportPatternsBothFills()
    {
        using var ms = new MemoryStream(Pdf(
            "/Pattern cs /P1 scn 10 10 30 30 re f 55 55 30 30 re f"));
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        int patterned = 0;
        foreach (var e in System.Xml.Linq.XDocument.Parse(svg).Descendants())
        {
            if (e.Name.LocalName != "path") continue;
            string fill = e.Attribute("fill")?.Value;
            if (fill != null && fill.StartsWith("url(", StringComparison.Ordinal)) patterned++;
        }

        Assert.Equal(2, patterned);
    }

    /// <summary>A colour operator DOES unselect it — the acceptance control.</summary>
    /// <remarks>
    /// Without this, "never clear the flag" would satisfy the tests above, and a file that
    /// genuinely switches back to a solid colour would keep painting the pattern for ever.
    /// </remarks>
    [Fact]
    public void AColourOperatorUnselectsThePattern()
    {
        using SKBitmap bmp = Render(Pdf(
            "/Pattern cs /P1 scn 10 10 30 30 re f 0 0 1 rg 55 55 30 30 re f"));

        SKColor first  = bmp.GetPixel(Px(25), Py(bmp, 25));
        SKColor second = bmp.GetPixel(Px(70), Py(bmp, 70));

        Assert.True(IsRed(first), $"the first fill was not the pattern's red but {first}");
        Assert.True(second.Blue > 200 && second.Red < 60,
            $"`rg` did not take over from the pattern; the second fill was {second}");
    }

    // ====================================================================
    //  Fixtures
    // ====================================================================

    private static bool IsRed(SKColor c) => c.Red > 200 && c.Green < 60 && c.Blue < 60;

    private static int Px(float userX) => (int)userX;
    private static int Py(SKBitmap bmp, float userY) => bmp.Height - 1 - (int)userY;

    private static SKBitmap Render(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfOperations.RenderPageToPng(ms, 0, dpi: 72);
        SKBitmap bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    private static byte[] Pdf(string content)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R " +
            "/Resources << /Pattern << /P1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            RedTile,
        };

        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append('\n')
           .Append("0000000000 65535 f \n");
        foreach (int o in offsets) pdf.Append(o.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1)
           .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
