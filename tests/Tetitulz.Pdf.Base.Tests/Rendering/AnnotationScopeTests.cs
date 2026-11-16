// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
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
/// <see cref="PdfRenderOptions.Annotations"/>: which annotations are drawn — every one, the form's
/// widgets alone, or none — decided in <c>AnnotationVisibility</c>, so the raster renderer and the
/// SVG export agree, under either intent.
/// </summary>
/// <remarks>
/// The page carries a widget whose appearance paints blue over the left half of the band and a
/// square annotation whose appearance paints red over the right, both flagged Print, and a black
/// content-stream bar along the bottom — so "drawn" is a decoded colour, and the bar is the
/// potency control for <see cref="AnnotationRendering.None"/>.
/// </remarks>
public class AnnotationScopeTests
{
    [Theory]
    [InlineData(AnnotationRendering.All, true, true)]
    [InlineData(AnnotationRendering.FormFieldsOnly, true, false)]
    [InlineData(AnnotationRendering.None, false, false)]
    public void TheRaster_DrawsTheScopesAnnotations_UnderEitherIntent(AnnotationRendering scope, bool widget, bool square)
    {
        foreach (RenderIntent intent in new[] { RenderIntent.Screen, RenderIntent.Print })
        {
            using SKBitmap bmp = Render(new PdfRenderOptions { Intent = intent, Annotations = scope });
            Assert.Equal(widget, IsBlue(PixelAssert.At(bmp, 50, 100)));
            Assert.Equal(square, IsRed(PixelAssert.At(bmp, 150, 100)));
            Assert.True(PixelAssert.IsInk(PixelAssert.At(bmp, 100, 15)));     // the content is always drawn
        }
    }

    [Theory]
    [InlineData(AnnotationRendering.All, 2)]
    [InlineData(AnnotationRendering.FormFieldsOnly, 1)]
    [InlineData(AnnotationRendering.None, 0)]
    public void TheSvgExport_HoldsTheScopesAnnotations(AnnotationRendering scope, int expected)
    {
        foreach (RenderIntent intent in new[] { RenderIntent.Screen, RenderIntent.Print })
        {
            using var ms = new MemoryStream(TwoAnnotations());
            XDocument doc = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(ms, 0, new PdfRenderOptions { Intent = intent, Annotations = scope }));
            XElement layer = Assert.Single(doc.Descendants(), e => e.Attribute("data-layer")?.Value == "annotations");
            Assert.Equal(expected, layer.Elements().Count());
        }
    }

    [Fact]
    public void AScopeOutsideTheEnum_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfRenderOptions { Annotations = (AnnotationRendering)3 });
        Assert.Equal(AnnotationRendering.None, new PdfRenderOptions { Annotations = AnnotationRendering.None }.Annotations);
        Assert.Equal(AnnotationRendering.All, PdfRenderOptions.Default.Annotations);
    }

    /// <summary>A renderer plugin draws the page whole, so a narrowed scope is one it cannot honour — and says so.</summary>
    [Fact]
    public void ANarrowedScope_IsReportedToAPluginRendererAsUnhonourable()
    {
        Assert.True(new PdfRenderOptions { Annotations = AnnotationRendering.FormFieldsOnly }.RequestsAnythingAPluginCannotHonour());
        Assert.True(new PdfRenderOptions { Annotations = AnnotationRendering.None }.RequestsAnythingAPluginCannotHonour());
        Assert.False(new PdfRenderOptions { Annotations = AnnotationRendering.All }.RequestsAnythingAPluginCannotHonour());
    }

    private static bool IsBlue(SKColor c) => c.Blue > 200 && c.Red < 60 && c.Green < 60;
    private static bool IsRed(SKColor c) => c.Red > 200 && c.Green < 60 && c.Blue < 60;

    private static SKBitmap Render(PdfRenderOptions options)
    {
        using var ms = new MemoryStream(TwoAnnotations());
        SKBitmap? bmp = SKBitmap.Decode(PdfPageRenderer.RenderPage(ms, 0, PixelAssert.Dpi, options));
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>
    /// A 200 × 200 page: a black bar over y 10–20 in its content; a text-field widget over
    /// [10 80 90 120] painting blue; a square annotation over [110 80 190 120] painting red; both /F 4.
    /// </summary>
    private static byte[] TwoAnnotations()
    {
        const string content = "0 g 10 10 180 10 re f";
        const string blue = "0 0 1 rg 0 0 80 40 re f";
        const string red = "1 0 0 rg 0 0 80 40 re f";
        return Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << >> /Annots [5 0 R 7 0 R] >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /F 4 /Rect [10 80 90 120] /AP << /N 6 0 R >> >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 80 40] /Length {blue.Length} >>\nstream\n{blue}\nendstream",
            "<< /Type /Annot /Subtype /Square /F 4 /Rect [110 80 190 120] /AP << /N 8 0 R >> >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 80 40] /Length {red.Length} >>\nstream\n{red}\nendstream");
    }

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
