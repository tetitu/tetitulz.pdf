// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations.Images;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfImageExporter.ExtractImage"/> exports what the raster renderer draws: every bit
/// depth, <c>/Decode</c>, every colour space and the masks.
/// </summary>
/// <remarks>
/// The exporter had a decoder of its own — 8 bits per component, five colour spaces — and refused
/// every other image, while it ignored <c>/Decode</c> altogether, so an inverted grey image exported
/// as its negative. GAPS.md §6 recorded the export below 8 bits as open, and that closing it meant
/// sharing the renderer's decode rather than writing a fourth. Each case here was a refusal, or a
/// wrong picture, before.
/// </remarks>
public class ImageExportDepthTests
{
    /// <summary>One bit per component: <c>0xAA</c> is white, black, white, … (§8.9.5.2, 1 is white in DeviceGray).</summary>
    [Fact]
    public void AOneBitImageExports()
    {
        byte[] pdf = DepthPdf(new byte[] { 0xAA }, 8, 1, "/DeviceGray", 1);

        Assert.Equal(new SKColor(255, 255, 255), Pixel(pdf, 0, 0));
        Assert.Equal(new SKColor(0, 0, 0), Pixel(pdf, 1, 0));
        Assert.Equal(new SKColor(0, 0, 0), Pixel(pdf, 7, 0));
    }

    /// <summary>Sixteen bits per component, taken to eight by their high byte.</summary>
    [Fact]
    public void ASixteenBitImageExports()
    {
        byte[] pdf = DepthPdf(new byte[] { 0xFF, 0xFF, 0x00, 0x00, 0x80, 0x00 }, 1, 1, "/DeviceRGB", 16);

        var p = Pixel(pdf, 0, 0);
        Assert.Equal(255, p.Red);
        Assert.Equal(0, p.Green);
        Assert.InRange(p.Blue, 127, 129);
    }

    /// <summary>
    /// <c>/Decode [1 0]</c> inverts a grey sample (§8.9.5.2): 0 shows white. The exporter's own decoder
    /// ignored the array and exported the negative; the image without it is the control.
    /// </summary>
    [Fact]
    public void ADecodeArrayIsApplied()
    {
        byte[] inverted = ImageFixtures.OneImagePdf(new byte[] { 0 }, 1, 1, "/DeviceGray", extraImageKeys: "/Decode [1 0] ");
        byte[] plain = ImageFixtures.OneImagePdf(new byte[] { 0 }, 1, 1, "/DeviceGray");

        Assert.Equal(new SKColor(255, 255, 255), Pixel(inverted, 0, 0));
        Assert.Equal(new SKColor(0, 0, 0), Pixel(plain, 0, 0));
    }

    /// <summary>A <c>/Separation</c> image goes through its tint transform: full tint of a red ink.</summary>
    [Fact]
    public void ASeparationImageExportsThroughItsTintTransform()
    {
        const string red = "[/Separation /Red /DeviceRGB << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [1 0 0] /N 1 >>]";
        byte[] pdf = ImageFixtures.OneImagePdf(new byte[] { 255 }, 1, 1, red);

        Assert.Equal(new SKColor(255, 0, 0), Pixel(pdf, 0, 0));
    }

    /// <summary>
    /// A soft mask is carried into the PNG as alpha: the masked pixel is transparent, the other
    /// opaque.
    /// </summary>
    [Fact]
    public void ASoftMaskBecomesAlpha()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(2, 1, 10, 200, 30), 2, 1, "/DeviceRGB",
            extraImageKeys: "/SMask 6 0 R ",
            extraObjects: b => b.Obj(6,
                "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceGray " +
                "/BitsPerComponent 8 /Length 2 >>", new byte[] { 0, 255 }));

        Assert.Equal(0, Pixel(pdf, 0, 0).Alpha);
        Assert.Equal(255, Pixel(pdf, 1, 0).Alpha);
    }

    /// <summary>One image of the given depth filling a page; <see cref="ImageFixtures.OneImagePdf"/> writes 8 bits.</summary>
    private static byte[] DepthPdf(byte[] samples, int width, int height, string colorSpace, int bitsPerComponent)
        => new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
                    "/Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>")
            .StreamObj(4, "<<", "q 72 0 0 72 0 0 cm /Im0 Do Q")
            .Obj(5, $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} " +
                    $"/ColorSpace {colorSpace} /BitsPerComponent {bitsPerComponent} /Length {samples.Length} >>", samples)
            .Finish();

    private static SKColor Pixel(byte[] pdf, int x, int y)
    {
        var img = PdfImageExporter.ExtractImage(new MemoryStream(pdf), 0, 0);
        Assert.Equal("image/png", img.MimeType);
        using var bitmap = SKBitmap.Decode(img.Data);
        return bitmap.GetPixel(x, y);
    }
}
