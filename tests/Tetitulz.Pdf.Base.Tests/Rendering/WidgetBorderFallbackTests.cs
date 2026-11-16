// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The frame a widget without an appearance stream gets is the one Table 192 gives it: its
/// <c>/MK /BC</c> colour (ISO 32000-2, lines 41229–41236 of <c>docs/iso/iso32000-2.txt</c>) at its
/// <c>/BS /W</c> width — and Table 168 (38780–38781) says of that width, "If this value is 0, no
/// border shall be drawn".
/// </summary>
/// <remarks>
/// <para><b>Both renderers framed every such widget in black.</b> The colour was read from
/// <c>/C</c> — Table 166's colour for a markup annotation's icon, a popup's title bar and a
/// <i>link's</i> border (38446–38451) — defaulting to black, and the width was floored at 1 pt
/// whatever the file said. So every unsigned signature field without an <c>/AP</c> was boxed in
/// black, in the raster render and the SVG export alike; a parity comparison reports a shared
/// defect as agreement.</para>
///
/// <para>The decision now has one implementation, <c>AnnotationFallbackModel.WidgetBorderColor</c>,
/// reached from both renderers. Every fixture carries <c>/C [0 0 1]</c>, so a frame drawn from
/// the wrong key would be blue and a frame drawn from the right one red. The raster half asserts
/// decoded pixels; the SVG half parses the export and reads the frame's attributes.</para>
/// </remarks>
public class WidgetBorderFallbackTests
{
    private const float X0 = 50, Y0 = 50, X1 = 150, Y1 = 100;

    // ------------------------------------------------------------------ raster

    [Fact]
    public void Raster_TheFrameTakesTheWidgetsBorderColourNotC()
    {
        using var bmp = PixelAssert.Render(Widget("/MK << /BC [1 0 0] >>"));

        Assert.True(PixelAssert.CountInk(bmp) > 0, "the widget declared a border colour and got no frame");
        SKColor ink = MeanInk(bmp);
        Assert.True(ink.Red >= ink.Green + 60 && ink.Red >= ink.Blue + 60,
            $"the frame should be /MK /BC's red, not /C's blue or a default black; got {ink}");
    }

    [Fact]
    public void Raster_AZeroWidthDrawsNoFrame()
    {
        using var bmp = PixelAssert.Render(Widget("/MK << /BC [1 0 0] >> /BS << /W 0 >>"));
        Assert.Equal(0, PixelAssert.CountInk(bmp));
    }

    [Fact]
    public void Raster_AWidgetWithoutAnMkBorderColourDrawsNoFrame()
    {
        using var bmp = PixelAssert.Render(Widget(""));
        Assert.Equal(0, PixelAssert.CountInk(bmp));
    }

    /// <summary>Table 192: zero elements means "No colour; transparent".</summary>
    [Fact]
    public void Raster_AnEmptyBorderColourIsTransparent()
    {
        using var bmp = PixelAssert.Render(Widget("/MK << /BC [] >>"));
        Assert.Equal(0, PixelAssert.CountInk(bmp));
    }

    /// <summary>
    /// Table 192 defines 0, 1, 3 and 4 components; any other count has not said what colour it
    /// wants, and the answer is no frame rather than a black one.
    /// </summary>
    [Fact]
    public void Raster_AMalformedBorderColourDrawsNoFrame()
    {
        using var bmp = PixelAssert.Render(Widget("/MK << /BC [0.5 0.5] >>"));
        Assert.Equal(0, PixelAssert.CountInk(bmp));
    }

    /// <summary>The case the defect was found through: an unsigned signature field is not a box.</summary>
    [Fact]
    public void Raster_AnUnsignedSignatureFieldWithoutAnAppearanceIsNotBoxed()
    {
        using var bmp = PixelAssert.Render(Widget("", fieldType: "/Sig"));
        Assert.Equal(0, PixelAssert.CountInk(bmp));
    }

    [Fact]
    public void Raster_TheFrameIsAsWideAsBsW()
    {
        using var thin = PixelAssert.Render(Widget("/MK << /BC [1 0 0] >> /BS << /W 1 >>"));
        using var thick = PixelAssert.Render(Widget("/MK << /BC [1 0 0] >> /BS << /W 3 >>"));

        int thinInk = PixelAssert.CountInk(thin), thickInk = PixelAssert.CountInk(thick);
        Assert.True(thinInk > 0, "the 1 pt frame painted nothing");
        Assert.True(thickInk > thinInk * 3 / 2,
            $"a 3 pt frame should carry visibly more ink than a 1 pt one; got {thickInk} vs {thinInk}");
    }

    // ------------------------------------------------------------------ SVG

    [Fact]
    public void Svg_TheFrameTakesTheWidgetsBorderColourNotC()
    {
        XElement rect = Assert.Single(Frames(Widget("/MK << /BC [1 0 0] >>")));

        Assert.Equal("rgb(255,0,0)", rect.Attribute("stroke")?.Value);
        Assert.Equal("none", rect.Attribute("fill")?.Value);
        Assert.Equal(X0, Attr(rect, "x"), 2);
        Assert.Equal(X1 - X0, Attr(rect, "width"), 2);
    }

    [Fact]
    public void Svg_AZeroWidthEmitsNoFrame()
        => Assert.Empty(Frames(Widget("/MK << /BC [1 0 0] >> /BS << /W 0 >>")));

    [Fact]
    public void Svg_AWidgetWithoutAnMkBorderColourEmitsNoFrame()
        => Assert.Empty(Frames(Widget("")));

    [Fact]
    public void Svg_AnUnsignedSignatureFieldWithoutAnAppearanceIsNotBoxed()
        => Assert.Empty(Frames(Widget("", fieldType: "/Sig")));

    [Fact]
    public void Svg_AMalformedBorderColourEmitsNoFrame()
        => Assert.Empty(Frames(Widget("/MK << /BC [0.5 0.5] >>")));

    [Fact]
    public void Svg_TheFrameWidthIsBsW()
    {
        XElement rect = Assert.Single(Frames(Widget("/MK << /BC [1 0 0] >> /BS << /W 3 >>")));
        Assert.Equal(3f, Attr(rect, "stroke-width"), 2);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Mean colour of the inked pixels — the page holds nothing but the frame.</summary>
    private static SKColor MeanInk(SKBitmap bmp)
    {
        long r = 0, g = 0, b = 0, n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                if (!PixelAssert.IsInk(c)) continue;
                r += c.Red; g += c.Green; b += c.Blue; n++;
            }
        Assert.True(n > 0, "no inked pixels to take a mean colour from");
        return new SKColor((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    /// <summary>The stroked rectangles inside the widget's annotation group.</summary>
    private static XElement[] Frames(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        XDocument doc = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(ms, 0));
        XElement group = Assert.Single(doc.Descendants().Where(
            e => e.Attribute("data-annot-subtype")?.Value == "Widget"));
        return group.Elements().Where(e => e.Name.LocalName == "rect").ToArray();
    }

    private static float Attr(XElement e, string name)
        => float.Parse(e.Attribute(name)?.Value ?? "NaN", CultureInfo.InvariantCulture);

    /// <summary>
    /// A 200×200 page with an empty content stream and one valueless widget over
    /// <c>/Rect [50 50 150 100]</c>, so every inked pixel is the frame.
    /// </summary>
    private static byte[] Widget(string extras, string fieldType = "/Tx") =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R" +
            " /Resources << >> /Annots [5 0 R] >>",
            "<< /Length 3 >>\nstream\nq Q\nendstream",
            $"<< /Type /Annot /Subtype /Widget /FT {fieldType} /T (f) /F 4" +
            $" /Rect [{F(X0)} {F(Y0)} {F(X1)} {F(Y1)}] /C [0 0 1] {extras} >>");

    private static string F(float v) => v.ToString(CultureInfo.InvariantCulture);

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets)
            sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
