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
/// Shading and pattern features asserted in <b>combination</b>, the last two paths where the
/// "two features each tested, their intersection never executed" shape could still hide.
/// </summary>
/// <remarks>
/// The same axial red→blue gradient is reached four ways: through the <c>sh</c> operator, as a
/// shading <b>pattern</b> filling a path, through that pattern's <c>/Matrix</c>, and as the fill
/// for <b>text</b>. Those are four different code paths for one spec concept, and this repo's
/// recurring defect is precisely one of them lagging.
/// </remarks>
public class ShadingPatternIntersectionTests
{
    // Red at x=0, blue at x=72, across a 72×72 page.
    private const string AxialShading =
        "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 72 0] /Extend [true true]" +
        " /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >>";

    private static byte[] BuildPdf(string content, string extraResources, params string[] extraObjs)
    {
        var acc = new List<byte>();
        var off = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(int n, string dict, byte[] sd = null)
        {
            off[n] = acc.Count;
            Emit($"{n} 0 obj\n{dict}");
            if (sd != null) { Emit("\nstream\n"); acc.AddRange(sd); Emit("\nendstream"); }
            Emit("\nendobj\n");
        }
        Emit("%PDF-1.4\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
               $"/Resources << {extraResources} >> /Contents 4 0 R >>");
        Obj(4, $"<< /Length {content.Length} >>", Encoding.Latin1.GetBytes(content));
        int n = 5;
        foreach (string body in extraObjs) Obj(n++, body);

        int max = n - 1;
        int xref = acc.Count;
        Emit($"xref\n0 {max + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= max; i++)
            Emit(off.TryGetValue(i, out int o) ? $"{o:D10} 00000 n \n" : "0000000000 65535 f \n");
        Emit($"trailer\n<< /Size {max + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }

    private static SKBitmap Render(byte[] pdf)
    {
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    /// <summary>
    /// One shading, two operators: <c>sh</c> paints it directly, a <c>/PatternType 2</c> fill paints
    /// it through the pattern machinery. They must agree — this is the raster path's internal
    /// equivalent of the SVG/raster parity rule.
    /// </summary>
    [Fact]
    public void ShOperator_AndShadingPatternFill_AgreeOnTheSameShading()
    {
        var direct = Render(BuildPdf(
            "q 0 0 72 72 re W n /Sh1 sh Q",
            "/Shading << /Sh1 5 0 R >>", AxialShading));

        var viaPattern = Render(BuildPdf(
            "/Pattern cs /P1 scn 0 0 72 72 re f",
            "/Pattern << /P1 5 0 R >>",
            "<< /PatternType 2 /Shading " + AxialShading + " >>"));

        foreach (int x in new[] { 12, 36, 60 })
        {
            SKColor a = direct.GetPixel(x, 36), b = viaPattern.GetPixel(x, 36);
            Assert.True(System.Math.Abs(a.Red - b.Red) <= 6 && System.Math.Abs(a.Blue - b.Blue) <= 6,
                $"at x={x} `sh` gave {a} but the shading pattern gave {b}");
        }

        // ...and the gradient really is a gradient, so the agreement above is not two blank pages.
        Assert.True(direct.GetPixel(12, 36).Red > direct.GetPixel(60, 36).Red + 80,
            "the control gradient must run red→blue; otherwise the comparison proves nothing");
    }

    /// <summary>
    /// §8.7.3.1: a pattern's <c>/Matrix</c> maps pattern space to the <b>default</b> page space.
    /// Translating the gradient right by half the page must move the colours with it.
    /// </summary>
    [Fact]
    public void ShadingPattern_MatrixTranslatesTheGradient()
    {
        var moved = Render(BuildPdf(
            "/Pattern cs /P1 scn 0 0 72 72 re f",
            "/Pattern << /P1 5 0 R >>",
            "<< /PatternType 2 /Matrix [1 0 0 1 36 0] /Shading " + AxialShading + " >>"));

        // With the shading shifted +36, x=36 is now the gradient's t=0 — pure red — and x=12 lies
        // before the start, which /Extend [true true] clamps to red as well.
        //
        // Asserted inline rather than through a helper: NoUnfalsifiableAssertionsTests reads method
        // BODIES, so extracting the Assert into a helper leaves this one looking assertion-free and
        // fails the guard. That is the guard being right — a body with no Assert is exactly the
        // shape it exists to ban — so the fix is to inline, never to exempt.
        SKColor start = moved.GetPixel(36, 36), before = moved.GetPixel(12, 36);
        Assert.True(start.Red > 200 && start.Blue < 60,
            $"x=36 after a +36 shift is the gradient start; expected red, got {start}");
        Assert.True(before.Red > 200 && before.Blue < 60,
            $"x=12 is before the shifted start and /Extend clamps; expected red, got {before}");
    }

    /// <summary>
    /// A shading pattern must reach <b>text</b>, not only paths. Pattern-filled glyphs go through a
    /// separate outline-capture path, so this is a genuinely different code route for the same
    /// concept.
    /// </summary>
    /// <remarks>
    /// A flat green fill is selected first and then replaced by the pattern. If the pattern is
    /// dropped anywhere along the text route, the glyphs paint green — a confidently wrong result
    /// that "some ink appeared" could never distinguish from success.
    /// </remarks>
    [Fact]
    public void ShadingPattern_FillsText_NotJustPaths()
    {
        var bmp = Render(BuildPdf(
            "0 1 0 rg /Pattern cs /P1 scn BT /F1 72 Tf 4 20 Td (HH) Tj ET",
            "/Pattern << /P1 5 0 R >> /Font << /F1 6 0 R >>",
            "<< /PatternType 2 /Shading " + AxialShading + " >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));

        int green = 0, gradient = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                if (c.Green > 150 && c.Red < 90 && c.Blue < 90) green++;
                else if (c.Red > 120 || c.Blue > 120)
                {
                    if (!(c.Red > 200 && c.Green > 200 && c.Blue > 200)) gradient++;
                }
            }

        Assert.True(gradient > 200, $"the glyphs must carry the gradient, found {gradient} such pixels");
        Assert.True(green == 0, $"the flat fill colour must not survive under a pattern, found {green} green pixels");
    }
}
