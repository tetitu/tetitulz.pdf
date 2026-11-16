// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Tests.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Images;

/// <summary>
/// <see cref="PdfImagePixels"/>: an image XObject's pixels as straight colour, through the
/// renderers' own decode — a palette looked up, one bit a sample widened, a soft mask as the
/// opacity — and an image that will not decode answered with null and the reason.
/// </summary>
public class PdfImagePixelsTests
{
    /// <summary>A document holding the image described as object 4 (and object 5, when given), and that image.</summary>
    private static (PdfDocument Document, PdfStream Image) Open(string imageHead, byte[] samples, string maskHead = null, byte[] maskSamples = null)
    {
        var b = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Im 4 0 R >> >> >>")
            .Obj(4, "<< /Type /XObject /Subtype /Image " + imageHead + " /Length " + samples.Length + " >>", samples);
        if (maskHead != null)
            b = b.Obj(5, "<< /Type /XObject /Subtype /Image " + maskHead + " /Length " + maskSamples.Length + " >>", maskSamples);
        var document = PdfDocument.Open(new MemoryStream(b.Finish()));
        return (document, (PdfStream)document.GetObject(4));
    }

    [Fact]
    public void Read_GivesAnRgbImageWithItsSoftMaskAsTheOpacity()
    {
        var (document, image) = Open(
            "/Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask 5 0 R",
            new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255 },
            "/Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8",
            new byte[] { 255, 255, 0, 255 });
        using (document)
        {
            PdfImagePixels pixels = PdfImagePixels.Read(document, image);

            Assert.NotNull(pixels);
            Assert.Equal((2, 2), (pixels.Width, pixels.Height));
            Assert.Equal(new byte[] { 255, 255, 0, 255 }, pixels.Alpha);
            // Red, green, then a pixel nobody sees — its colour is not kept — then white.
            Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 0, 255, 255, 255 }, pixels.Rgb);
            Assert.Null(PdfImagePixels.LastReadError);
        }
    }

    /// <summary>An indexed image's bytes are places in its palette: the pixels are the palette's colours, and an opaque image has no alpha.</summary>
    [Fact]
    public void Read_LooksAnIndexedImageUpInItsPalette()
    {
        var (document, image) = Open("/Width 2 /Height 1 /ColorSpace [/Indexed /DeviceRGB 1 <FF000000FF00>] /BitsPerComponent 8", new byte[] { 1, 0 });
        using (document)
        {
            PdfImagePixels pixels = PdfImagePixels.Read(document, image);

            Assert.NotNull(pixels);
            Assert.Equal(new byte[] { 0, 255, 0, 255, 0, 0 }, pixels.Rgb);
            Assert.Null(pixels.Alpha);
        }
    }

    /// <summary>One bit a sample: a set bit is white, a clear one black.</summary>
    [Fact]
    public void Read_WidensOneBitSamples()
    {
        var (document, image) = Open("/Width 8 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 1", new byte[] { 0b1010_0000 });
        using (document)
        {
            PdfImagePixels pixels = PdfImagePixels.Read(document, image);

            Assert.NotNull(pixels);
            Assert.Equal(8, pixels.Width);
            Assert.Equal(new byte[] { 255, 255, 255, 0, 0, 0, 255, 255, 255, 0, 0, 0 }, pixels.Rgb[..12]);
        }
    }

    /// <summary>An image whose stream will not decode is answered with null and the reason; the next good read clears the reason.</summary>
    [Fact]
    public void Read_OfAnImageThatWillNotDecode_AnswersNullAndSaysWhy()
    {
        var (broken, brokenImage) = Open("/Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /JPXDecode", new byte[] { 1, 2, 3, 4 });
        using (broken)
        {
            Assert.Null(PdfImagePixels.Read(broken, brokenImage));
            Assert.False(string.IsNullOrEmpty(PdfImagePixels.LastReadError));
        }

        var (good, goodImage) = Open("/Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", new byte[] { 128 });
        using (good)
        {
            PdfImagePixels pixels = PdfImagePixels.Read(good, goodImage);
            Assert.NotNull(pixels);
            Assert.Equal(new byte[] { 128, 128, 128 }, pixels.Rgb);
            Assert.Null(PdfImagePixels.LastReadError);
        }
    }
}
