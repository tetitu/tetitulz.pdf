// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using H = Tetitulz.Pdf.TestHelpers.Jp2Headers;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The operations beside the renderers that see a <c>JPXDecode</c> image's pixels (Wave 15g), with
/// no plugin registered: image export to PNG, the page thumbnail reader, the colour-space
/// converter and its inventory.
/// </summary>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public sealed class JpxOperationsTests : IDisposable
{
    public JpxOperationsTests() => PdfPluginHost.Instance.ResetForTesting();
    public void Dispose() => PdfPluginHost.Instance.ResetForTesting();

    private static JpxLosslessFixtures.Fixture Rgb => JpxLosslessFixtures.Rgb40x24_Mct_TwoLevels;
    private static JpxLosslessFixtures.Fixture Rgba => JpxLosslessFixtures.Rgba40x24_Mct_OneLevel;
    private static JpxLosslessFixtures.Fixture Gray => JpxLosslessFixtures.Gray40x24_TwoLevels_Rpcl_ThreeLayers;

    // ── export ──────────────────────────────────────────────────────────────

    /// <summary>ExtractImage keeps handing the codestream back; ExtractImageAsPng is the route to its pixels.</summary>
    [Theory]
    [InlineData("/ColorSpace /DeviceRGB /BitsPerComponent 8")]
    [InlineData("")]
    public void ExtractImageAsPng_DecodesAJpxImageToItsPixels(string extras)
    {
        byte[] pdf = JpxTestPdf.ImagePdf(Rgb.Codestream, Rgb.Width, Rgb.Height, extras);

        PdfImageExporter.PdfExtractedImage codestream = PdfOperations.ExtractImage(new MemoryStream(pdf), 0, 0);
        Assert.Equal("image/jp2", codestream.MimeType);
        Assert.Equal(Rgb.Codestream, codestream.Data);

        PdfImageExporter.PdfExtractedImage png = PdfOperations.ExtractImageAsPng(new MemoryStream(pdf), 0, 0);
        Assert.Equal(("image/png", Rgb.Width, Rgb.Height), (png.MimeType, png.Width, png.Height));
        using SKBitmap image = SKBitmap.Decode(png.Data);
        Assert.NotNull(image);
        Assert.Equal((Rgb.Width, Rgb.Height), (image.Width, image.Height));
        JpxTestPdf.AssertEveryPixel(image, (x, y) => JpxTestPdf.Pixel(Rgb, x, y));
    }

    /// <summary>A JPEG 2000 image with an opacity channel exports with its alpha.</summary>
    [Fact]
    public void ExtractImageAsPng_KeepsAJpxImagesOpacity()
    {
        byte[] jp2 = JpxTestPdf.Jp2(Rgba, H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb), H.Cdef((0, 0, 1), (1, 0, 2), (2, 0, 3), (3, 1, 0)));
        byte[] pdf = JpxTestPdf.ImagePdf(jp2, Rgba.Width, Rgba.Height, "/SMaskInData 1");

        using SKBitmap image = SKBitmap.Decode(PdfOperations.ExtractImageAsPng(new MemoryStream(pdf), 0, 0).Data);
        Assert.NotNull(image);
        int transparent = 0;
        for (int y = 0; y < Rgba.Height; y++)
            for (int x = 0; x < Rgba.Width; x++)
            {
                int a = Rgba.Planes[3][y * Rgba.Width + x];
                Assert.True(Math.Abs(image.GetPixel(x, y).Alpha - a) <= 1, $"alpha at ({x},{y}) is {image.GetPixel(x, y).Alpha}, the opacity sample {a}");
                if (a < 128) transparent++;
            }
        Assert.True(transparent > 100, "the fixture's opacity plane is too opaque to have tested anything");
    }

    // ── thumbnails ──────────────────────────────────────────────────────────

    /// <summary>A JPEG 2000 /Thumb comes back decoded — the samples and the space they are in — rather than as codestream bytes.</summary>
    [Theory]
    [InlineData("/ColorSpace /DeviceRGB /BitsPerComponent 8", "DeviceRGB")]
    [InlineData("", "DeviceRGB")]
    public void ReadThumbnail_DecodesAJpxThumb(string extras, string expectedSpace)
    {
        byte[] pdf = JpxTestPdf.Pdf(72, 72, "", "/Thumb 5 0 R ",
            ($"<< /Type /XObject /Subtype /Image /Width {Rgb.Width} /Height {Rgb.Height} {extras} /Filter /JPXDecode", Rgb.Codestream));

        PdfThumbnailManager.ThumbnailInfo info = PdfOperations.ReadPageThumbnail(new MemoryStream(pdf), 0);

        Assert.NotNull(info);
        Assert.Equal((Rgb.Width, Rgb.Height, expectedSpace), (info.Width, info.Height, info.ColorSpace));
        Assert.Equal(JpxTestPdf.Interleaved(Rgb, 0, 1, 2), info.PixelData);
    }

    /// <summary>
    /// What the reader cannot carry, it says (the review of 2026-09-26, row 30): a
    /// <c>/ColorSpace</c> array keeps the codestream and <see cref="PdfThumbnailManager.LastError"/>
    /// names it; an opacity channel is left out of the samples and named; the control leaves the
    /// slot null.
    /// </summary>
    [Fact]
    public void ReadThumbnail_SaysWhenAnArrayColourSpaceKeepsTheCodestream_AndWhenAnOpacityChannelIsLeftOut()
    {
        var lookup = new StringBuilder();
        for (int i = 0; i < 256; i++) lookup.Append(i.ToString("X2")).Append(i.ToString("X2")).Append(i.ToString("X2"));
        byte[] indexed = JpxTestPdf.Pdf(72, 72, "", "/Thumb 5 0 R ",
            ($"<< /Type /XObject /Subtype /Image /Width {Gray.Width} /Height {Gray.Height} /ColorSpace [/Indexed /DeviceRGB 255 <{lookup}>] /BitsPerComponent 8 /Filter /JPXDecode", Gray.Codestream));
        PdfThumbnailManager.ThumbnailInfo info = PdfOperations.ReadPageThumbnail(new MemoryStream(indexed), 0);
        Assert.Equal(Gray.Codestream, info.PixelData);
        Assert.Contains("/ColorSpace is an array", PdfThumbnailManager.LastError ?? "", StringComparison.Ordinal);

        byte[] jp2 = JpxTestPdf.Jp2(Rgba, H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb), H.Cdef((0, 0, 1), (1, 0, 2), (2, 0, 3), (3, 1, 0)));
        byte[] withOpacity = JpxTestPdf.Pdf(72, 72, "", "/Thumb 5 0 R ",
            ($"<< /Type /XObject /Subtype /Image /Width {Rgba.Width} /Height {Rgba.Height} /SMaskInData 1 /Filter /JPXDecode", jp2));
        info = PdfOperations.ReadPageThumbnail(new MemoryStream(withOpacity), 0);
        Assert.Equal("DeviceRGB", info.ColorSpace);
        Assert.Equal(JpxTestPdf.Interleaved(Rgba, 0, 1, 2), info.PixelData);
        Assert.Contains("opacity channel", PdfThumbnailManager.LastError ?? "", StringComparison.Ordinal);

        byte[] plain = JpxTestPdf.Pdf(72, 72, "", "/Thumb 5 0 R ",
            ($"<< /Type /XObject /Subtype /Image /Width {Rgb.Width} /Height {Rgb.Height} /Filter /JPXDecode", Rgb.Codestream));
        info = PdfOperations.ReadPageThumbnail(new MemoryStream(plain), 0);
        Assert.Equal(JpxTestPdf.Interleaved(Rgb, 0, 1, 2), info.PixelData);
        Assert.Null(PdfThumbnailManager.LastError);
    }

    /// <summary>The control: a Flate thumbnail's samples come back as they always did.</summary>
    [Fact]
    public void ReadThumbnail_FlateThumb_IsUnchanged()
    {
        byte[] samples = { 10, 20, 30, 40, 50, 60 };
        byte[] pdf = JpxTestPdf.Pdf(72, 72, "", "/Thumb 5 0 R ",
            ("<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8", samples));
        PdfThumbnailManager.ThumbnailInfo info = PdfOperations.ReadPageThumbnail(new MemoryStream(pdf), 0);
        Assert.Equal(samples, info.PixelData);
        Assert.Equal("DeviceRGB", info.ColorSpace);
    }

    // ── the colour-space converter ──────────────────────────────────────────

    /// <summary>A JPEG 2000 RGB image converts to grey — BT.601 of its decoded samples — and is written back as 8-bit Flate.</summary>
    [Theory]
    [InlineData("/ColorSpace /DeviceRGB /BitsPerComponent 8")]
    [InlineData("")]
    public void ConvertRgbToGrayscale_ConvertsAJpxImage_AndWritesItBackAsFlate(string extras)
    {
        byte[] pdf = JpxTestPdf.ImagePdf(Rgb.Codestream, Rgb.Width, Rgb.Height, extras);
        using var output = new MemoryStream();

        int converted = PdfColorSpaceConverter.ConvertRgbToGrayscale(new MemoryStream(pdf), output);

        Assert.Equal(1, converted);
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var image = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(5));
        Assert.Equal(PdfName.DeviceGray, doc.ResolveObject(image[PdfName.ColorSpace]) as PdfName);
        Assert.Equal(PdfName.FlateDecode, doc.ResolveObject(image[PdfName.Filter]) as PdfName);
        Assert.Equal(8, (doc.ResolveObject(image[PdfName.BitsPerComponent]) as PdfNumber)?.IntValue);
        Assert.Null(image[PdfName.SMaskInData]);

        byte[] grey = image.GetDecodedData();
        var expected = new byte[Rgb.Width * Rgb.Height];
        for (int i = 0; i < expected.Length; i++)
            expected[i] = (byte)((Rgb.Planes[0][i] * PdfColorSpaceNames.LumaRedPermille
                                  + Rgb.Planes[1][i] * PdfColorSpaceNames.LumaGreenPermille
                                  + Rgb.Planes[2][i] * PdfColorSpaceNames.LumaBluePermille) / PdfColorSpaceNames.LumaScale);
        Assert.Equal(expected, grey);
    }

    /// <summary>The opacity a JPEG 2000 image carried under /SMaskInData survives the conversion as the /SMask it stands for.</summary>
    [Fact]
    public void ConvertRgbToGrayscale_WritesAJpxImagesOpacityAsAnSMask()
    {
        byte[] jp2 = JpxTestPdf.Jp2(Rgba, H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb), H.Cdef((0, 0, 1), (1, 0, 2), (2, 0, 3), (3, 1, 0)));
        byte[] pdf = JpxTestPdf.ImagePdf(jp2, Rgba.Width, Rgba.Height, "/SMaskInData 1");
        using var output = new MemoryStream();

        Assert.Equal(1, PdfColorSpaceConverter.ConvertRgbToGrayscale(new MemoryStream(pdf), output));

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var image = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(5));
        Assert.Null(image[PdfName.SMaskInData]);
        var smask = Assert.IsAssignableFrom<PdfStream>(doc.ResolveObject(image[PdfName.SMask]));
        Assert.Equal(PdfName.DeviceGray, doc.ResolveObject(smask[PdfName.ColorSpace]) as PdfName);
        Assert.Equal((Rgba.Width, Rgba.Height), ((doc.ResolveObject(smask[PdfName.Width]) as PdfNumber)?.IntValue, (doc.ResolveObject(smask[PdfName.Height]) as PdfNumber)?.IntValue));
        Assert.Equal(Rgba.Planes[3], smask.GetDecodedData());
    }

    /// <summary>
    /// Under /SMaskInData 2 the samples are premultiplied by the opacity: the straight colour is
    /// what converts — divided out first, through the one un-premultiply rule — and the opacity
    /// goes out as the /SMask, which is straight by definition (the review of 2026-09-26, row 7).
    /// The mode-1 conversion of the same bytes differs.
    /// </summary>
    [Fact]
    public void ConvertRgbToGrayscale_DividesTheOpacityOutOfAnSMaskInData2ImageBeforeConverting()
    {
        byte[] jp2 = JpxTestPdf.Jp2(Rgba, H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb), H.Cdef((0, 0, 1), (1, 0, 2), (2, 0, 3), (3, 2, 0)));
        using var output = new MemoryStream();
        Assert.Equal(1, PdfColorSpaceConverter.ConvertRgbToGrayscale(new MemoryStream(JpxTestPdf.ImagePdf(jp2, Rgba.Width, Rgba.Height, "/SMaskInData 2")), output));

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var image = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(5));
        byte[] grey = image.GetDecodedData();
        var expected = new byte[Rgba.Width * Rgba.Height];
        for (int i = 0; i < expected.Length; i++)
        {
            int a = Rgba.Planes[3][i];
            int r = Straight(Rgba.Planes[0][i], a), g = Straight(Rgba.Planes[1][i], a), b = Straight(Rgba.Planes[2][i], a);
            expected[i] = (byte)((r * PdfColorSpaceNames.LumaRedPermille + g * PdfColorSpaceNames.LumaGreenPermille + b * PdfColorSpaceNames.LumaBluePermille) / PdfColorSpaceNames.LumaScale);
        }
        Assert.Equal(expected, grey);
        var smask = Assert.IsAssignableFrom<PdfStream>(doc.ResolveObject(image[PdfName.SMask]));
        Assert.Equal(Rgba.Planes[3], smask.GetDecodedData());

        using var straightOutput = new MemoryStream();
        Assert.Equal(1, PdfColorSpaceConverter.ConvertRgbToGrayscale(new MemoryStream(JpxTestPdf.ImagePdf(jp2, Rgba.Width, Rgba.Height, "/SMaskInData 1")), straightOutput));
        using var straightDoc = PdfDocument.Open(new MemoryStream(straightOutput.ToArray()));
        byte[] straightGrey = Assert.IsAssignableFrom<PdfStream>(straightDoc.GetObject(5)).GetDecodedData();
        int differ = 0;
        for (int i = 0; i < grey.Length; i++) if (grey[i] != straightGrey[i]) differ++;
        Assert.True(differ > 100, $"only {differ} samples differ between the mode-1 and mode-2 conversions");
    }

    private static int Straight(int premultiplied, int alpha) => alpha == 0 ? 0 : Math.Min(255, (premultiplied * 255 + alpha / 2) / alpha);

    /// <summary>A JPEG 2000 image whose own colour is not RGB — grey, or CMYK by count — is left alone, as any non-RGB image is.</summary>
    [Fact]
    public void ConvertRgbToGrayscale_LeavesANonRgbJpxImageAlone()
    {
        JpxLosslessFixtures.Fixture grey = JpxLosslessFixtures.Gray40x24_TwoLevels_Rpcl_ThreeLayers;
        byte[] pdf = JpxTestPdf.ImagePdf(grey.Codestream, grey.Width, grey.Height, "");
        using var output = new MemoryStream();
        Assert.Equal(0, PdfColorSpaceConverter.ConvertRgbToGrayscale(new MemoryStream(pdf), output));
        Assert.Equal(pdf, output.ToArray());
    }

    /// <summary>The inventory reads a JPEG 2000 image's own colour space from its JP2 header when the dictionary has none.</summary>
    [Fact]
    public void GetColorSpaces_ReadsAJpxImagesSpaceFromItsHeader()
    {
        byte[] sYcc = JpxTestPdf.ImagePdf(JpxTestPdf.Jp2(Rgb, H.ColrEnumerated(Jp2EnumeratedColourSpaces.SYcc)), Rgb.Width, Rgb.Height, "");
        var rgb = PdfOperations.GetColorSpaces(new MemoryStream(sYcc)).Single();
        Assert.Equal(("DeviceRGB", true), (rgb.Name, rgb.IsImageColorSpace));

        byte[] cmyk = JpxTestPdf.ImagePdf(JpxTestPdf.Jp2(Rgba, H.ColrEnumerated(Jp2EnumeratedColourSpaces.Cmyk)), Rgba.Width, Rgba.Height, "");
        Assert.Equal("DeviceCMYK", PdfOperations.GetColorSpaces(new MemoryStream(cmyk)).Single().Name);

        byte[] icc = JpxTestPdf.ImagePdf(JpxTestPdf.Jp2(Rgb, H.ColrIcc(H.IccProfile("RGB "))), Rgb.Width, Rgb.Height, "");
        Assert.Equal("ICCBased", PdfOperations.GetColorSpaces(new MemoryStream(icc)).Single().Name);

        // The dictionary's own space, when it has one, is what is reported.
        byte[] declared = JpxTestPdf.ImagePdf(Rgb.Codestream, Rgb.Width, Rgb.Height, "/ColorSpace /DeviceGray /BitsPerComponent 8");
        Assert.Equal("DeviceGray", PdfOperations.GetColorSpaces(new MemoryStream(declared)).Single().Name);
    }
}
