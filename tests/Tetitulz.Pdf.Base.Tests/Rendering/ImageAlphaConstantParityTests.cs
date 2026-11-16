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
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// An image is a non-stroking paint, drawn at the alpha constant <c>ca</c> and in the blend mode
/// (ISO 32000-1 §11.6.4.4, §11.3.5) — through <c>Do</c>, inline, and as a stencil mask, in both
/// renderers.
/// </summary>
/// <remarks>
/// <para><b>Both renderers dropped both.</b> <c>PdfRenderContext.DrawBitmap</c> painted with a bare
/// <c>SKPaint</c>, and <c>SvgContentWriter</c> emitted each image with no <c>opacity</c> and no
/// <c>mix-blend-mode</c>: a half-transparent logo rendered and exported opaque. They agreed, so no
/// parity test could see it; the SVG converter's filters, which draw their result back as an image
/// under the element's opacity, did.</para>
/// <para>Every fixture is a 72 × 72 page wholly covered by a 1 × 1 image: black (a DeviceGray
/// sample of 0, or a stencil painting the black fill) on the white page, or 50% grey over a red
/// page for the blend. The control, with no <c>gs</c>, is opaque and Normal in both.</para>
/// </remarks>
public sealed class ImageAlphaConstantParityTests
{
    public enum Route { XObject, Inline, Stencil }

    /// <summary>Under <c>ca 0.5</c> black over white is mid-grey in the raster, and <c>opacity="0.5"</c> in the export.</summary>
    [Theory]
    [InlineData(Route.XObject)]
    [InlineData(Route.Inline)]
    [InlineData(Route.Stencil)]
    public void AnImageIsPaintedAtTheAlphaConstant(Route route)
    {
        byte[] pdf = ImagePdf(route, "/ca 0.5", Black, red: false);

        Assert.InRange(RasterCentre(pdf).Red, 120, 135);
        Assert.Equal("0.5", Placed(Svg(pdf)).Attribute("opacity")?.Value?.TrimEnd('0').TrimEnd('.'));
    }

    /// <summary>The control: no <c>gs</c>, opaque black, and no <c>opacity</c> attribute.</summary>
    [Theory]
    [InlineData(Route.XObject)]
    [InlineData(Route.Inline)]
    [InlineData(Route.Stencil)]
    public void WithoutTheStateTheImageIsOpaque(Route route)
    {
        byte[] pdf = ImagePdf(route, null, Black, red: false);

        Assert.InRange(RasterCentre(pdf).Red, 0, 5);
        Assert.Null(Placed(Svg(pdf)).Attribute("opacity"));
        Assert.Null(Placed(Svg(pdf)).Attribute("style"));
    }

    /// <summary>
    /// <c>/BM /Multiply</c>: 50% grey multiplied onto red is dark red, (128, 0, 0), where Normal
    /// paints grey; the export names the mode on the placed element.
    /// </summary>
    [Theory]
    [InlineData(Route.XObject)]
    [InlineData(Route.Inline)]
    public void AnImageIsPaintedInTheBlendMode(Route route)
    {
        byte[] pdf = ImagePdf(route, "/BM /Multiply", "\u0080", red: true);
        byte[] normal = ImagePdf(route, null, "\u0080", red: true);

        SKColor multiplied = RasterCentre(pdf), painted = RasterCentre(normal);
        Assert.InRange(multiplied.Red, 120, 135);
        Assert.InRange(multiplied.Green, 0, 5);
        Assert.InRange(painted.Green, 120, 135);        // the control: Normal paints the grey
        Assert.Contains("mix-blend-mode:multiply", Placed(Svg(pdf)).Attribute("style")?.Value ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// The same image drawn twice, opaque and then under <c>ca 0.5</c>: the export defines it once
    /// and places it with two <c>&lt;use&gt;</c>s, and the second carries the opacity alone.
    /// </summary>
    [Fact]
    public void ASecondDrawTakesItsOwnState()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Contents 4 0 R" +
            " /Resources << /ExtGState << /GS0 5 0 R >> /XObject << /Im0 6 0 R >> >> >>",
            Stream("", "q 36 0 0 72 0 0 cm /Im0 Do Q q /GS0 gs 36 0 0 72 36 0 cm /Im0 Do Q\n"),
            "<< /Type /ExtGState /ca 0.5 >>",
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", Black));

        var uses = Svg(pdf).Descendants().Where(e => e.Name.LocalName == "use").ToList();
        Assert.Equal(2, uses.Count);
        Assert.Null(uses[0].Attribute("opacity"));
        Assert.NotNull(uses[1].Attribute("opacity"));
    }

    /// <summary>
    /// A stencil filled with a pattern is exported as a rect painted through a mask; it takes the
    /// alpha constant too. The raster's pixel is half over white whatever the colour.
    /// </summary>
    [Fact]
    public void APatternFilledStencilIsPaintedAtTheAlphaConstant()
    {
        const string cell = "1 0 0 rg 0 0 10 10 re f";
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Contents 4 0 R" +
            " /Resources << /ExtGState << /GS0 5 0 R >> /XObject << /Im0 6 0 R >> /Pattern << /P0 7 0 R >> >> >>",
            Stream("", "q /GS0 gs /Pattern cs /P0 scn 72 0 0 72 0 0 cm /Im0 Do Q\n"),
            "<< /Type /ExtGState /ca 0.5 >>",
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ImageMask true /BitsPerComponent 1", Black),
            Stream("/PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << >>", cell));

        XElement placed = Svg(pdf).Descendants().Single(e => e.Name.LocalName == "rect" && e.Attribute("mask") != null);
        Assert.NotNull(placed.Attribute("opacity"));
        SKColor centre = RasterCentre(pdf);
        Assert.True(centre.Red >= 120 && centre.Green >= 120 && centre.Blue >= 120, $"painted opaque: {centre}");
    }

    // ------------------------------------------------------------------ measurement

    private static SKColor RasterCentre(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using SKBitmap bmp = SKBitmap.Decode(PdfPageRenderer.RenderPage(ms, 0, 72));
        Assert.NotNull(bmp);
        return bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
    }

    private static XElement Svg(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(ms, 0)).Root;
    }

    /// <summary>The element that places the image: the one carrying its matrix — an image, a use, or the stencil's rect.</summary>
    private static XElement Placed(XElement root)
        => root.Descendants().Single(e => e.Name.LocalName is "image" or "use" or "rect"
                                          && (e.Attribute("transform")?.Value.StartsWith("matrix(", StringComparison.Ordinal) ?? false));

    // ------------------------------------------------------------------ fixtures

    /// <summary>A black sample: a DeviceGray 0, or a stencil's 0 bit, which paints the fill colour (black).</summary>
    private const string Black = "\u0000";

    /// <summary>
    /// The page: optionally red first, then the 1 × 1 image over all of it under an ExtGState with
    /// <paramref name="state"/>, or none.
    /// </summary>
    private static byte[] ImagePdf(Route route, string state, string sample, bool red)
    {
        string gs = state != null ? "/GS0 gs " : "";
        string background = red ? "1 0 0 rg 0 0 72 72 re f " : "";
        string draw = route switch
        {
            Route.Inline => $"BI /W 1 /H 1 /BPC 8 /CS /G ID {sample}\nEI",
            Route.Stencil => "0 g /Im0 Do",
            _ => "/Im0 Do",
        };
        string image = route == Route.Stencil
            ? Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ImageMask true /BitsPerComponent 1", sample)
            : Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", sample);
        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Contents 4 0 R" +
            " /Resources << /ExtGState << /GS0 5 0 R >> /XObject << /Im0 6 0 R >> >> >>",
            Stream("", $"{background}q {gs}72 0 0 72 0 0 cm\n{draw}\nQ\n"),
            $"<< /Type /ExtGState {state} >>",
            image);
    }

    private static string Stream(string dictEntries, string data) =>
        $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    /// <summary>Assembles objects 1..n with a correct xref table. Bodies must be Latin-1.</summary>
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
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
