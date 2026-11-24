// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §7.7.3.3, Table 30: <c>/Rotate</c> "shall be a multiple of 90".
///
/// <para><b>Seven places normalised it and only some validated it.</b> Every copy wrote
/// <c>((r % 360) + 360) % 360</c>; the two Accessibility ones then refused a non-multiple of 90,
/// the WinForms viewer's <c>PageGeometry</c> quantised it to 0, and <b>neither renderer checked at
/// all</b>. So <c>/Rotate 45</c> was refused by the tagger, reported as 0 by the viewer, and drawn
/// by both renderers at a literal 45° inside an <i>un-swapped</i> canvas — because the axis-swap
/// test <c>r == 90 || r == 270</c> answers "portrait" for every illegal angle. The page's corners
/// were clipped away, and every viewer overlay was placed for an upright page.</para>
///
/// <para>The assertions are on the rendered <b>geometry</b>, which is what a wrong rotation
/// changes; a page drawn at 45° is still a valid image of the right size.</para>
/// </summary>
public sealed class PageRotationTests
{
    // ── the shared reader ────────────────────────────────────────────────

    [Theory]
    [InlineData(null, 0, false)]
    [InlineData(0, 0, false)]
    [InlineData(90, 90, false)]
    [InlineData(180, 180, false)]
    [InlineData(270, 270, false)]
    [InlineData(360, 0, false)]
    [InlineData(450, 90, false)]        // 360 + 90
    [InlineData(-90, 270, false)]       // negative is legal and means the same quarter turn
    [InlineData(-450, 270, false)]
    public void LegalRotations_NormaliseToTheirQuarterTurn(int? raw, int expected, bool expectMalformed)
    {
        int r = PdfPageRotation.Normalize(raw, out bool malformed);

        Assert.Equal(expected, r);
        Assert.Equal(expectMalformed, malformed);
    }

    /// <summary>
    /// A non-multiple of 90 is reported <b>and</b> quantised to 0. Returning 45 would leave every
    /// caller to remember the check, which is exactly how four of them forgot.
    /// </summary>
    [Theory]
    [InlineData(45)]
    [InlineData(1)]
    [InlineData(405)]   // 360 + 45
    [InlineData(-45)]
    public void IllegalRotations_AreReportedAndTreatedAsUpright(int raw)
    {
        int r = PdfPageRotation.Normalize(raw, out bool malformed);

        Assert.True(malformed);
        Assert.Equal(0, r);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(90, true)]
    [InlineData(180, false)]
    [InlineData(270, true)]
    public void SwapsAxes_IsTrueForTheQuarterTurns(int rotation, bool expected)
        => Assert.Equal(expected, PdfPageRotation.SwapsAxes(rotation));

    // ── what that means for a rendered page ──────────────────────────────

    /// <summary>
    /// A landscape page at <c>/Rotate 90</c> renders portrait — the control that proves the
    /// rotation is honoured at all, so the <c>/Rotate 45</c> case below cannot pass by accident.
    /// </summary>
    [Fact]
    public void RotateNinety_SwapsTheRenderedAxes()
    {
        using var bmp = Render(RotatedPdf(90));
        Assert.True(bmp.Height > bmp.Width,
            $"a 200x100 page at /Rotate 90 should render taller than wide; got {bmp.Width}x{bmp.Height}");
    }

    /// <summary>
    /// <c>/Rotate 45</c> is malformed, so the page renders <b>upright</b> — same extent as
    /// <c>/Rotate 0</c>, not drawn at a literal 45° in a 200×100 bitmap.
    /// </summary>
    [Fact]
    public void RotateFortyFive_RendersUprightRatherThanAtFortyFiveDegrees()
    {
        using var malformed = Render(RotatedPdf(45));
        using var upright = Render(RotatedPdf(0));

        Assert.Equal(upright.Width, malformed.Width);
        Assert.Equal(upright.Height, malformed.Height);

        // And the ink is where an upright page puts it, not rotated into the corners.
        Assert.Equal(PixelIsInk(upright), PixelIsInk(malformed));
    }

    /// <summary>The SVG export reaches the same two conclusions.</summary>
    [Theory]
    [InlineData(90, true)]
    [InlineData(45, false)]
    [InlineData(0, false)]
    public void SvgExport_SwapsTheViewBoxOnlyForALegalQuarterTurn(int rotate, bool expectSwapped)
    {
        string svg = RenderSvg(RotatedPdf(rotate));
        var root = XDocument.Parse(svg).Root;

        float w = float.Parse(root.Attribute("width").Value, System.Globalization.CultureInfo.InvariantCulture);
        float h = float.Parse(root.Attribute("height").Value, System.Globalization.CultureInfo.InvariantCulture);

        // The page is 200 wide by 100 tall.
        Assert.Equal(expectSwapped, h > w);
    }

    /// <summary>
    /// And the two renderers agree — the property neither single-renderer assertion states, and the
    /// one that was false for every illegal angle.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(270)]
    public void RasterAndSvg_AgreeOnWhetherTheAxesSwap(int rotate)
    {
        using var bmp = Render(RotatedPdf(rotate));
        bool rasterSwapped = bmp.Height > bmp.Width;

        var root = XDocument.Parse(RenderSvg(RotatedPdf(rotate))).Root;
        float w = float.Parse(root.Attribute("width").Value, System.Globalization.CultureInfo.InvariantCulture);
        float h = float.Parse(root.Attribute("height").Value, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(rasterSwapped, h > w);
    }

    // ------------------------------------------------------------------

    private static bool PixelIsInk(SKBitmap bmp) => bmp.GetPixel(20, 20).Red < 128;

    private static SKBitmap Render(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    private static string RenderSvg(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    /// <summary>A landscape 200×100 page with a black square in one corner.</summary>
    private static byte[] RotatedPdf(int rotate) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R" +
            $" /Resources << >> /Rotate {rotate} >>",
            Stream("", "0 g 0 60 40 40 re f\n"));

    private static string Stream(string dictEntries, string data) =>
        $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
