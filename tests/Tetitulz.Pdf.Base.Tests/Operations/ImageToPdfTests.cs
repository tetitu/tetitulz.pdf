// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Image → PDF: one page per raster. The reverse direction had no entry point at all, while PDF →
/// image is thorough — and <c>PdfImageInserter</c> is a different operation, placing an image on an
/// <b>existing</b> page.
///
/// <para>Every assertion reads the <b>parsed image XObject and page geometry</b> out of the written
/// document. "A PDF came back" is true of a writer that emits a blank page, and a page whose image
/// is the wrong size, the wrong colour space or re-encoded still opens perfectly.</para>
/// </summary>
public sealed class ImageToPdfTests
{
    // ---------------------------------------------------------------- fixtures

    /// <summary>A PNG of the given size, encoded by the same codec the converter decodes with.</summary>
    private static byte[] Png(int width, int height, byte alpha = 255)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                bitmap.SetPixel(x, y, new SkiaSharp.SKColor(200, 100, 50, alpha));

        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A JPEG of the given size — three components, so DeviceRGB.</summary>
    private static byte[] Jpeg(int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                bitmap.SetPixel(x, y, new SkiaSharp.SKColor(10, 200, 90));

        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static byte[] Convert(IEnumerable<byte[]> images, ImageToPdfOptions options = null)
    {
        using var output = new MemoryStream();
        PdfOperations.CreatePdfFromImages(images, output, options);
        return output.ToArray();
    }

    /// <summary>The one image XObject on a page of the written document.</summary>
    private static PdfStream ImageOn(PdfDocument doc, int pageIndex)
    {
        var resources = doc.ResolveObject(doc.Pages[pageIndex].Dictionary[PdfName.Resources]) as PdfDictionary;
        var xobjects = Assert.IsType<PdfDictionary>(doc.ResolveObject(resources[PdfName.XObject]));
        Assert.NotEmpty(xobjects.Keys);
        return Assert.IsType<PdfStream>(doc.ResolveObject(xobjects[xobjects.Keys.First()]));
    }

    private static (double w, double h) PageSize(PdfDocument doc, int pageIndex)
    {
        var box = Assert.IsType<PdfArray>(
            doc.ResolveObject(doc.Pages[pageIndex].Dictionary[PdfName.MediaBox]));
        return ((box[2] as PdfNumber).DoubleValue - (box[0] as PdfNumber).DoubleValue,
                (box[3] as PdfNumber).DoubleValue - (box[1] as PdfNumber).DoubleValue);
    }

    // ---------------------------------------------------------------- the feature

    [Fact]
    public void Create_WritesOnePagePerImage()
    {
        byte[] pdf = Convert(new[] { Png(40, 30), Png(20, 20), Jpeg(60, 10) });

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        Assert.Equal(3, doc.Pages.Count);
    }

    /// <summary>
    /// The image really is on the page, at its own pixel dimensions — not a blank page of the right
    /// size, which is what a converter that measured and then dropped the image would produce.
    /// </summary>
    [Fact]
    public void Create_EmbedsTheImageAtItsOwnPixelDimensions()
    {
        byte[] pdf = Convert(new[] { Png(40, 30) });

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        PdfStream image = ImageOn(doc, 0);

        Assert.Equal("Image", image.GetName(PdfName.Subtype)?.Value);
        Assert.Equal(40d, (doc.ResolveObject(image[PdfName.Width]) as PdfNumber).DoubleValue);
        Assert.Equal(30d, (doc.ResolveObject(image[PdfName.Height]) as PdfNumber).DoubleValue);
    }

    /// <summary>
    /// A JPEG passes through as <c>/DCTDecode</c> with its bytes unchanged. Re-encoding a scan is a
    /// second generation of loss for no gain, and nothing about the output's appearance reveals it.
    /// </summary>
    [Fact]
    public void Create_EmbedsAJpegByteForByteRatherThanReEncodingIt()
    {
        byte[] jpeg = Jpeg(50, 40);
        byte[] pdf = Convert(new[] { jpeg });

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        PdfStream image = ImageOn(doc, 0);

        Assert.Equal("DCTDecode", (doc.ResolveObject(image[PdfName.Filter]) as PdfName)?.Value);
        Assert.Equal(jpeg, image.RawData);
    }

    /// <summary>A PNG is decoded and stored as Flate-compressed RGB samples — lossless either way.</summary>
    [Fact]
    public void Create_StoresANonJpegAsFlateCompressedSamples()
    {
        byte[] pdf = Convert(new[] { Png(16, 16) });

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        PdfStream image = ImageOn(doc, 0);

        Assert.Equal("FlateDecode", (doc.ResolveObject(image[PdfName.Filter]) as PdfName)?.Value);
        Assert.Equal(PdfColorSpaceNames.DeviceRGB,
            (doc.ResolveObject(image[PdfName.ColorSpace]) as PdfName)?.Value);
        // 16 x 16 x 3 bytes of samples, whatever they compressed to.
        Assert.Equal(16 * 16 * 3, image.GetDecodedData().Length);
    }

    /// <summary>A transparent PNG carries its alpha as an <c>/SMask</c>.</summary>
    [Fact]
    public void Create_TransparentImage_CarriesItsAlphaAsASoftMask()
    {
        byte[] pdf = Convert(new[] { Png(8, 8, alpha: 128) });

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        PdfStream mask = Assert.IsType<PdfStream>(doc.ResolveObject(ImageOn(doc, 0)[PdfName.SMask]));

        Assert.Equal(PdfColorSpaceNames.DeviceGray,
            (doc.ResolveObject(mask[PdfName.ColorSpace]) as PdfName)?.Value);
        Assert.Equal(8 * 8, mask.GetDecodedData().Length);
    }

    /// <summary>...and an opaque one carries none: it would change nothing and cost a full plane.</summary>
    [Fact]
    public void Create_OpaqueImage_CarriesNoSoftMask()
    {
        byte[] pdf = Convert(new[] { Png(8, 8) });

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        Assert.Null(doc.ResolveObject(ImageOn(doc, 0)[PdfName.SMask]));
    }

    /// <summary>
    /// The samples come from the decoded bitmap's buffer, not <c>GetPixel</c>: every alpha exactly
    /// the codec's, every colour exactly <c>GetPixel</c>'s where the pixel is opaque or invisible and
    /// within one level of it under partial alpha (Skia's integer table against its float rounding) —
    /// and the buffer was walked.
    /// </summary>
    [Fact]
    public void Create_ReadsTheDecodedBuffer_WithinOneLevelOfGetPixel()
    {
        const int W = 32, H = 16;
        byte[] png;
        using (var source = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(W, H, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul)))
        {
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    source.SetPixel(x, y, new SkiaSharp.SKColor((byte)(x * 8), (byte)(y * 16), (byte)(255 - x * 8), (byte)(x * 8 + y)));
            using var image = SkiaSharp.SKImage.FromBitmap(source);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            png = data.ToArray();
        }

        int walks = Base.Images.BitmapPixels.BufferWalks;
        byte[] pdf = Convert(new[] { png });
        Assert.True(Base.Images.BitmapPixels.BufferWalks > walks, "the decoded bitmap's buffer was not read");

        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        PdfStream written = ImageOn(doc, 0);
        byte[] rgb = written.GetDecodedData();
        byte[] alpha = Assert.IsType<PdfStream>(doc.ResolveObject(written[PdfName.SMask])).GetDecodedData();

        using var decoded = Base.Rendering.ContentStreamInterpreter.SafeDecodeEncodedImage(png);
        int partial = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                SkiaSharp.SKColor expected = decoded.GetPixel(x, y);
                int i = y * W + x;
                Assert.Equal(expected.Alpha, alpha[i]);
                int tolerance = expected.Alpha == 0 || expected.Alpha == 255 ? 0 : 1;
                if (tolerance == 1) partial++;
                Assert.InRange(rgb[i * 3] - expected.Red, -tolerance, tolerance);
                Assert.InRange(rgb[i * 3 + 1] - expected.Green, -tolerance, tolerance);
                Assert.InRange(rgb[i * 3 + 2] - expected.Blue, -tolerance, tolerance);
            }
        Assert.True(partial > W * H / 2, "the fixture is mostly partial alpha");
    }

    /// <summary>
    /// A <b>greyscale</b> JPEG is declared <c>/DeviceGray</c>, from its own component count.
    /// </summary>
    /// <remarks>
    /// <para><b>This test exists because a mutation survived.</b> Changing the one-component arm to
    /// <c>/DeviceRGB</c> — the exact defect <c>JpegImageDictionary</c>'s remarks record having been
    /// fixed once — left every other test here green, because no fixture was a greyscale JPEG.
    /// Skia's encoder writes three components whatever bitmap it is given, so the fixture is a
    /// hand-built SOF0 instead: twelve bytes, no encoder, and the component count is the point.</para>
    /// <para>The failure it guards against is invisible here and visible elsewhere: an XObject
    /// declaring three components over a one-component frame is reported as damaged by a reader
    /// that honours the declared space, while this library's own renderer trusts the codestream and
    /// draws it correctly.</para>
    /// </remarks>
    [Fact]
    public void Create_GreyscaleJpeg_IsDeclaredDeviceGray()
    {
        byte[] pdf = Convert(new[] { GreyscaleJpegHeader(width: 32, height: 16) });

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        PdfStream image = ImageOn(doc, 0);

        Assert.Equal(PdfColorSpaceNames.DeviceGray,
            (doc.ResolveObject(image[PdfName.ColorSpace]) as PdfName)?.Value);
        Assert.Equal(32d, (doc.ResolveObject(image[PdfName.Width]) as PdfNumber).DoubleValue);
    }

    /// <summary>
    /// A four-component JPEG with no Adobe APP14 marker is <b>refused</b>. Nothing in the
    /// codestream says which way round the samples are, and Adobe writes them inverted — so a guess
    /// yields a colour-negative image that opens perfectly, which is a wrong answer where a refusal
    /// was available.
    /// </summary>
    [Fact]
    public void Create_FourComponentJpegWithNoAdobeMarker_IsRefused()
    {
        using var output = new MemoryStream();
        var ex = Assert.Throws<PdfException>(() => PdfOperations.CreatePdfFromImages(
            new[] { JpegHeaderWithComponents(4, width: 8, height: 8) }, output));

        Assert.Contains("Adobe APP14", ex.Message);
    }

    /// <summary>A minimal JPEG carrying only an SOF0 frame header — enough to be described.</summary>
    private static byte[] GreyscaleJpegHeader(int width, int height)
        => JpegHeaderWithComponents(1, width, height);

    /// <summary>
    /// A JPEG whose SOF0 declares <paramref name="components"/> components. Hand-built because an
    /// encoder decides the component count for itself, and the count is what is under test.
    /// </summary>
    private static byte[] JpegHeaderWithComponents(int components, int width, int height)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };          // SOI

        int segmentLength = 8 + 3 * components;
        bytes.Add(0xFF); bytes.Add(0xC0);                    // SOF0
        bytes.Add((byte)(segmentLength >> 8)); bytes.Add((byte)segmentLength);
        bytes.Add(8);                                        // sample precision
        bytes.Add((byte)(height >> 8)); bytes.Add((byte)height);
        bytes.Add((byte)(width >> 8)); bytes.Add((byte)width);
        bytes.Add((byte)components);
        for (int i = 0; i < components; i++)
        {
            bytes.Add((byte)(i + 1));                        // component id
            bytes.Add(0x11);                                 // sampling factors
            bytes.Add(0);                                    // quantisation table
        }

        bytes.Add(0xFF); bytes.Add(0xD9);                    // EOI
        return bytes.ToArray();
    }

    // ---------------------------------------------------------------- sizing

    /// <summary>
    /// Under <c>MatchImage</c> the page is exactly the image's size at the given dpi — 96 px at
    /// 96 dpi is one inch, which is 72 points.
    /// </summary>
    [Fact]
    public void MatchImage_SizesThePageFromTheImageAndTheDpi()
    {
        byte[] pdf = Convert(new[] { Png(96, 192) },
            new ImageToPdfOptions { Sizing = ImagePageSizing.MatchImage, Dpi = 96 });

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        var (w, h) = PageSize(doc, 0);

        Assert.Equal(72d, w, 3);
        Assert.Equal(144d, h, 3);
    }

    /// <summary>A higher dpi means the same pixels occupy less paper.</summary>
    [Fact]
    public void MatchImage_HigherDpi_ProducesASmallerPage()
    {
        byte[] pdf = Convert(new[] { Png(96, 96) },
            new ImageToPdfOptions { Sizing = ImagePageSizing.MatchImage, Dpi = 192 });

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        var (w, h) = PageSize(doc, 0);

        Assert.Equal(36d, w, 3);
        Assert.Equal(36d, h, 3);
    }

    /// <summary>
    /// Under <c>FitToPage</c> the page is the paper size and the image keeps its aspect ratio.
    /// </summary>
    /// <remarks>
    /// <b>Scaling each axis independently to fill the page is the obvious implementation</b>, and
    /// it stretches every image whose proportions differ from the paper's — which is most of them.
    /// A 200×100 image on a 400×400 page must come out 400×200, not 400×400.
    /// </remarks>
    [Fact]
    public void FitToPage_PreservesTheAspectRatio()
    {
        var options = new ImageToPdfOptions
        {
            Sizing = ImagePageSizing.FitToPage,
            PageWidthPoints = 400,
            PageHeightPoints = 400,
            Center = true,
        };

        var (pageW, pageH, x, y, drawW, drawH) = PdfImageToPdf.Layout(200, 100, options);

        Assert.Equal(400d, pageW, 3);
        Assert.Equal(400d, pageH, 3);
        Assert.Equal(400d, drawW, 3);
        Assert.Equal(200d, drawH, 3);          // NOT 400
        Assert.Equal(0d, x, 3);
        Assert.Equal(100d, y, 3);              // centred vertically
    }

    /// <summary>A margin shrinks the drawable area on both axes.</summary>
    [Fact]
    public void FitToPage_HonoursTheMargin()
    {
        var options = new ImageToPdfOptions
        {
            Sizing = ImagePageSizing.FitToPage,
            PageWidthPoints = 400,
            PageHeightPoints = 400,
            MarginPoints = 50,
            Center = false,
        };

        var (_, _, x, y, drawW, drawH) = PdfImageToPdf.Layout(100, 100, options);

        Assert.Equal(50d, x, 3);
        Assert.Equal(50d, y, 3);
        Assert.Equal(300d, drawW, 3);
        Assert.Equal(300d, drawH, 3);
    }

    /// <summary>
    /// The margin comes off both sides of each axis: a 200×100 image on a 400×400 page with a
    /// 50-point margin has 300 points of width to fill, so it is drawn 300×150. With the margin taken
    /// off one side only it would be 350 wide. (The square case above cannot tell: there the height
    /// decides the scale.)
    /// </summary>
    [Fact]
    public void FitToPage_TakesTheMarginOffBothSides()
    {
        var options = new ImageToPdfOptions
        {
            Sizing = ImagePageSizing.FitToPage,
            PageWidthPoints = 400,
            PageHeightPoints = 400,
            MarginPoints = 50,
        };

        var (_, _, x, y, drawW, drawH) = PdfImageToPdf.Layout(200, 100, options);

        Assert.Equal(300d, drawW, 3);
        Assert.Equal(150d, drawH, 3);
        Assert.Equal(50d, x, 3);
        Assert.Equal(125d, y, 3);
    }

    /// <summary>A negative margin is no margin: the image fills the page, and is not drawn past it.</summary>
    [Fact]
    public void FitToPage_ANegativeMarginIsNone()
    {
        var options = new ImageToPdfOptions
        {
            Sizing = ImagePageSizing.FitToPage,
            PageWidthPoints = 400,
            PageHeightPoints = 400,
            MarginPoints = -50,
        };

        var (_, _, x, y, drawW, drawH) = PdfImageToPdf.Layout(100, 100, options);

        Assert.Equal((0d, 0d, 400d, 400d), (x, y, drawW, drawH));
    }

    /// <summary>
    /// A paper size of zero or less is floored at one point, so a page still exists and the image
    /// is scaled into it rather than to nothing — or, with a negative size, flipped.
    /// </summary>
    [Fact]
    public void FitToPage_ANonPositivePaperSizeIsFlooredAtOnePoint()
    {
        var options = new ImageToPdfOptions
        {
            Sizing = ImagePageSizing.FitToPage,
            PageWidthPoints = 0,
            PageHeightPoints = 400,
        };

        var (pageW, _, _, _, drawW, drawH) = PdfImageToPdf.Layout(100, 100, options);

        Assert.Equal(1d, pageW, 3);
        Assert.Equal(1d, drawW, 3);
        Assert.Equal(1d, drawH, 3);
    }

    /// <summary>A resolution of zero or less is the default 96 dpi, not a page of infinite size.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-300)]
    public void MatchImage_ANonPositiveDpiIsTheDefault(double dpi)
    {
        var (pageW, pageH, _, _, _, _) = PdfImageToPdf.Layout(96, 192,
            new ImageToPdfOptions { Sizing = ImagePageSizing.MatchImage, Dpi = dpi });

        Assert.Equal((72d, 144d), (pageW, pageH));
    }

    /// <summary>The page really is the paper size, not the image's.</summary>
    [Fact]
    public void FitToPage_WritesThePaperSizeAsTheMediaBox()
    {
        byte[] pdf = Convert(new[] { Png(10, 10) }, new ImageToPdfOptions
        {
            Sizing = ImagePageSizing.FitToPage,
            PageWidthPoints = PdfPageSizes.A4Width,
            PageHeightPoints = PdfPageSizes.A4Height,
        });

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        var (w, h) = PageSize(doc, 0);

        Assert.Equal(PdfPageSizes.A4Width, w, 1);
        Assert.Equal(PdfPageSizes.A4Height, h, 1);
    }

    /// <summary>
    /// A multi-page TIFF becomes one PDF page <b>per TIFF page</b>, not one per file.
    /// </summary>
    /// <remarks>
    /// <para><b>The page count is the assertion, and it is the thing easy to get wrong.</b>
    /// Every other format this accepts is one image, so the batch loop counted inputs; a TIFF is a
    /// linked list of IFDs and a scanner writes one per sheet, so counting inputs under-reports a
    /// three-sheet scan as one page.</para>
    ///
    /// <para>The pages carry different sizes, so "three pages" cannot be satisfied by the same page
    /// written three times.</para>
    /// </remarks>
    [Fact]
    public void Create_MultiPageTiff_WritesOnePdfPagePerTiffPage()
    {
        byte[] tiff = ThreePageTiff();

        using var output = new MemoryStream();
        ImageToPdfResult result = PdfOperations.CreatePdfFromImages(new[] { tiff }, output);

        Assert.Equal(3, result.PagesWritten);
        Assert.Empty(result.Warnings);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray(), writable: false));
        Assert.Equal(3, doc.Pages.Count);

        // MatchImage sizing is the default, so each page is its own image's size in points --
        // which differs per page here, and would not if the same page were emitted three times.
        var widths = new List<double>();
        foreach (PdfPage page in doc.Pages)
        {
            PdfArray box = page.MediaBox;
            widths.Add((box[2] as PdfNumber)?.DoubleValue ?? 0);
        }

        Assert.Equal(widths.Count, widths.Distinct().Count());
    }

    /// <summary>
    /// A page of a TIFF the decoder cannot read loses only itself: with the first of three pages
    /// written in an unsupported compression, the other two are written. Refusing the file at the
    /// first bad page would lose every good page after it.
    /// </summary>
    [Fact]
    public void Create_TiffWithOneUnreadablePage_WritesTheOthers()
    {
        using var output = new MemoryStream();
        ImageToPdfResult result = PdfOperations.CreatePdfFromImages(new[] { ThreePageTiff(badPage: 0) }, output);

        Assert.Equal(2, result.PagesWritten);
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray(), writable: false));
        Assert.Equal(2, doc.Pages.Count);
    }

    /// <summary>
    /// A three-page grey TIFF whose pages are 2x1, 3x1 and 4x1; <paramref name="badPage"/>, when
    /// given, is written with compression 99, which no decoder reads.
    /// </summary>
    private static byte[] ThreePageTiff(int badPage = -1)
    {
        var pages = new List<(int W, int H, byte[] Samples)>
        {
            (2, 1, new byte[] { 10, 20 }),
            (3, 1, new byte[] { 30, 40, 50 }),
            (4, 1, new byte[] { 60, 70, 80, 90 }),
        };

        var body = new MemoryStream();
        void W(byte[] b) => body.Write(b, 0, b.Length);
        byte[] U16(int v) => new[] { (byte)v, (byte)(v >> 8) };
        byte[] U32(int v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };

        W(new byte[] { 0x49, 0x49, 0x2A, 0x00 });
        W(U32(0));                                   // first-IFD offset, patched below

        var stripAt = new int[pages.Count];
        for (int p = 0; p < pages.Count; p++)
        {
            stripAt[p] = (int)body.Length;
            W(pages[p].Samples);
        }

        var ifdAt = new int[pages.Count];
        var nextAt = new int[pages.Count];

        for (int p = 0; p < pages.Count; p++)
        {
            ifdAt[p] = (int)body.Length;

            var tags = new (int Tag, int Type, int Value)[]
            {
                (256, 3, pages[p].W), (257, 3, pages[p].H), (258, 3, 8), (259, 3, p == badPage ? 99 : 1),
                (262, 3, 1), (273, 4, stripAt[p]), (277, 3, 1), (278, 3, pages[p].H),
                (279, 4, pages[p].Samples.Length), (284, 3, 1),
            };

            W(U16(tags.Length));
            foreach ((int tag, int type, int value) in tags)
            {
                W(U16(tag));
                W(U16(type));
                W(U32(1));
                W(type == 3
                    ? new byte[] { (byte)value, (byte)(value >> 8), 0, 0 }
                    : U32(value));
            }

            nextAt[p] = (int)body.Length;
            W(U32(0));
        }

        byte[] file = body.ToArray();
        void Patch(int at, int value)
        {
            file[at] = (byte)value; file[at + 1] = (byte)(value >> 8);
            file[at + 2] = (byte)(value >> 16); file[at + 3] = (byte)(value >> 24);
        }

        Patch(4, ifdAt[0]);
        for (int p = 0; p + 1 < pages.Count; p++) Patch(nextAt[p], ifdAt[p + 1]);
        return file;
    }

    // ---------------------------------------------------------------- honest failure

    /// <summary>
    /// A TIFF header with no readable IFD is refused <b>by name</b> rather than as unreadable.
    /// </summary>
    /// <remarks>
    /// <b>This used to say TIFF itself was refused, and that is no longer true</b> — the format is
    /// decoded, so the name was changed rather than the assertion. What it checks now is the
    /// honest-failure rule: a file whose header says TIFF and whose body says nothing is named as a
    /// TIFF problem, where "unreadable" would send a caller looking at the wrong thing.
    /// </remarks>
    [Fact]
    public void Create_TiffWithNoReadableIfd_IsRefusedByNameRatherThanAsUnreadable()
    {
        // A little-endian TIFF header: "II", 42, a first-IFD offset pointing at nothing — padded
        // past the twelve bytes RasterImageInspector requires before it will sniff at all.
        byte[] tiff = { 0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        using var output = new MemoryStream();
        var ex = Assert.Throws<PdfException>(
            () => PdfOperations.CreatePdfFromImages(new[] { tiff }, output));

        Assert.Contains("TIFF", ex.Message);
    }

    /// <summary>
    /// A TIFF whose first IFD lies past the end of the file holds no pages at all, and is refused
    /// as a TIFF before any page is tried — the fixture above has an IFD and fails at its decode.
    /// </summary>
    [Fact]
    public void Create_TiffWithNoPages_IsRefusedByName()
    {
        byte[] tiff = { 0x49, 0x49, 0x2A, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        using var output = new MemoryStream();
        var ex = Assert.Throws<PdfException>(
            () => PdfOperations.CreatePdfFromImages(new[] { tiff }, output));

        Assert.Contains("image 0: ", ex.Message);
        Assert.Contains("TIFF", ex.Message);
    }

    /// <summary>
    /// One unreadable image in a batch does not lose the rest — a hundred scans with one bad file
    /// should still produce ninety-nine pages, and say which one failed.
    /// </summary>
    [Fact]
    public void Create_SkipsAnUnreadableImageAndReportsIt()
    {
        using var output = new MemoryStream();
        var result = PdfOperations.CreatePdfFromImages(
            new[] { Png(10, 10), new byte[] { 1, 2, 3 }, Png(12, 12) }, output);

        Assert.Equal(2, result.PagesWritten);
        Assert.Single(result.Warnings);
        Assert.Contains("image 1", result.Warnings[0]);

        using var ms = new MemoryStream(output.ToArray(), writable: false);
        using var doc = PdfDocument.Open(ms);
        Assert.Equal(2, doc.Pages.Count);
    }

    /// <summary>
    /// Each skipped image is reported with the reason that applies to it: no bytes, and bytes that
    /// are no raster image — a frame declaring no width among them, which the inspector refuses
    /// before any route sizes a page from it. The PNG beside them is written, so the batch is a
    /// document and the reasons arrive as warnings.
    /// </summary>
    [Fact]
    public void ASkippedImage_IsReportedForItsOwnReason()
    {
        using var output = new MemoryStream();
        var result = PdfOperations.CreatePdfFromImages(
            new[] { Png(10, 10), Array.Empty<byte>(), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 },
                    JpegHeaderWithComponents(3, width: 0, height: 8) },
            output);

        Assert.Equal(1, result.PagesWritten);
        Assert.Equal(3, result.Warnings.Count);
        Assert.Contains("image 1: no bytes were supplied", result.Warnings[0]);
        Assert.Contains("image 2: the bytes are not a raster image this library recognises", result.Warnings[1]);
        Assert.Contains("image 3: the bytes are not a raster image this library recognises", result.Warnings[2]);
    }

    /// <summary>
    /// A null path in a list of files is an image with no bytes — reported, not thrown as an
    /// argument error from the file system.
    /// </summary>
    [Fact]
    public void ANullPath_IsAnImageWithNoBytes()
    {
        using var output = new MemoryStream();
        var ex = Assert.Throws<PdfException>(() =>
            PdfImageToPdf.Create(new string[] { null }, output));

        Assert.Contains("image 0: no bytes were supplied", ex.Message);
    }

    /// <summary>...unless the caller says a partial document is worse than none.</summary>
    [Fact]
    public void Create_StopOnUnreadableImage_ThrowsInsteadOfSkipping()
    {
        using var output = new MemoryStream();
        Assert.Throws<PdfException>(() => PdfOperations.CreatePdfFromImages(
            new[] { Png(10, 10), new byte[] { 1, 2, 3 } }, output,
            new ImageToPdfOptions { StopOnUnreadableImage = true }));
    }

    /// <summary>
    /// A batch with nothing readable in it throws rather than writing a zero-page PDF — which is
    /// not a legal document (§7.7.3.2), and would make the caller open the file to learn that
    /// nothing worked.
    /// </summary>
    [Fact]
    public void Create_WithNothingReadable_ThrowsRatherThanWritingAnEmptyDocument()
    {
        using var output = new MemoryStream();
        var ex = Assert.Throws<PdfException>(() => PdfOperations.CreatePdfFromImages(
            new[] { new byte[] { 1, 2, 3 } }, output));

        Assert.Contains("no document to write", ex.Message);
        Assert.Equal(0, output.Length);
    }

    // ---------------------------------------------------------------- JPEG 2000

    /// <summary>A JP2 file, from the in-house encoder, of the given size and component count.</summary>
    private static byte[] Jp2(int width, int height, int components, bool container = true)
    {
        var samples = new byte[width * height * components];
        for (int i = 0; i < samples.Length; i++) samples[i] = (byte)(i * 5 + 17);
        return Core.Imaging.Jpx.JpxImageEncoding.Encode(samples, width, height, components, 8,
            new Core.Imaging.Jpx.JpxEncodingOptions { Jp2Container = container });
    }

    /// <summary>
    /// A JPEG 2000 file passes through as <c>/JPXDecode</c> with its bytes unchanged, its size from
    /// the SIZ, and the colour space its colr box names; nothing decodes it on the way in.
    /// </summary>
    [Fact]
    public void Create_EmbedsAJpeg2000FileByteForByte_AsJpxDecode()
    {
        byte[] jp2 = Jp2(50, 40, 3);
        byte[] pdf = Convert(new[] { jp2 });

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        PdfStream image = ImageOn(doc, 0);

        Assert.Equal("JPXDecode", (doc.ResolveObject(image[PdfName.Filter]) as PdfName)?.Value);
        Assert.Equal(jp2, image.RawData);
        Assert.Equal(50d, (doc.ResolveObject(image[PdfName.Width]) as PdfNumber).DoubleValue);
        Assert.Equal(40d, (doc.ResolveObject(image[PdfName.Height]) as PdfNumber).DoubleValue);
        Assert.Equal(PdfColorSpaceNames.DeviceRGB, (doc.ResolveObject(image[PdfName.ColorSpace]) as PdfName)?.Value);
        Assert.Equal(8d, (doc.ResolveObject(image[PdfName.BitsPerComponent]) as PdfNumber).DoubleValue);
        Assert.Null(image[PdfName.SMaskInData]);
        Assert.Null(image[PdfName.SMask]);
        Assert.Equal((50d, 40d), (PageSize(doc, 0).w * 96 / 72, PageSize(doc, 0).h * 96 / 72));   // MatchImage at 96 dpi
    }

    /// <summary>A raw codestream, which has no colour box, is embedded with the colour space its component count implies.</summary>
    [Fact]
    public void Create_EmbedsARawCodestream_NamingTheColourSpaceByItsComponents()
    {
        byte[] j2k = Jp2(20, 10, 1, container: false);
        byte[] pdf = Convert(new[] { j2k });

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        PdfStream image = ImageOn(doc, 0);
        Assert.Equal("JPXDecode", (doc.ResolveObject(image[PdfName.Filter]) as PdfName)?.Value);
        Assert.Equal(PdfColorSpaceNames.DeviceGray, (doc.ResolveObject(image[PdfName.ColorSpace]) as PdfName)?.Value);
        Assert.Equal(j2k, image.RawData);
    }

    /// <summary>A codestream of two components names no PDF colour space; the page is skipped with that reason, beside a control that is not.</summary>
    [Fact]
    public void Create_RefusesAJpeg2000FileWhoseColourSpaceAPdfCannotName_WithTheReason()
    {
        var planes = new[] { new int[16], new int[16] };
        for (int i = 0; i < 16; i++) { planes[0][i] = i * 16; planes[1][i] = 255 - i * 16; }
        byte[] twoComponents = Core.Imaging.Jpx.JpxEncoder.EncodeLossless(planes, 4, 4, 8, false, null);

        using var output = new MemoryStream();
        ImageToPdfResult result = PdfImageToPdf.Create(new[] { twoComponents, Jp2(4, 4, 1) }, output);
        Assert.Equal(1, result.PagesWritten);
        string warning = Assert.Single(result.Warnings);
        Assert.Contains("2 colour channels", warning, StringComparison.Ordinal);
    }

    /// <summary>A JP2 that declares an opacity channel is written with <c>/SMaskInData 1</c> — the one key that lets a viewer use it — and no separate soft mask.</summary>
    [Fact]
    public void Create_AJpeg2000FileWithAnOpacityChannel_AsksForItThroughSMaskInData()
    {
        byte[] jp2 = TestHelpers.JpxEmbedFixtures.GreyWithOpacityJp2();
        byte[] pdf = Convert(new[] { jp2 });

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        PdfStream image = ImageOn(doc, 0);
        Assert.Equal("JPXDecode", (doc.ResolveObject(image[PdfName.Filter]) as PdfName)?.Value);
        Assert.Equal(PdfColorSpaceNames.DeviceGray, (doc.ResolveObject(image[PdfName.ColorSpace]) as PdfName)?.Value);
        Assert.Equal(1d, (doc.ResolveObject(image[PdfName.SMaskInData]) as PdfNumber)?.DoubleValue);
        Assert.Null(image[PdfName.SMask]);
        Assert.Equal(jp2, image.RawData);
    }

    /// <summary>
    /// The opacity mode the file declares is what is written (ISO 32000-2 Table 89): a
    /// premultiplied channel (cdef Typ 2) asks for <c>/SMaskInData 2</c>; an opacity channel for
    /// one colour alone is a form the key cannot express, so no key is written and the image is
    /// opaque.
    /// </summary>
    [Fact]
    public void Create_APremultipliedOpacityChannel_AsksForSMaskInData2_AndAPerColourOne_GetsNoKey()
    {
        byte[] pdf = Convert(new[] { TestHelpers.JpxEmbedFixtures.GreyWithPremultipliedOpacityJp2(), TestHelpers.JpxEmbedFixtures.RgbWithPerColourOpacityJp2() });

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        PdfStream premultiplied = ImageOn(doc, 0);
        Assert.Equal(PdfColorSpaceNames.DeviceGray, (doc.ResolveObject(premultiplied[PdfName.ColorSpace]) as PdfName)?.Value);
        Assert.Equal(2d, (doc.ResolveObject(premultiplied[PdfName.SMaskInData]) as PdfNumber)?.DoubleValue);

        PdfStream perColour = ImageOn(doc, 1);
        Assert.Equal(PdfColorSpaceNames.DeviceRGB, (doc.ResolveObject(perColour[PdfName.ColorSpace]) as PdfName)?.Value);
        Assert.Null(perColour[PdfName.SMaskInData]);
    }

    /// <summary>An sYCC file gets no <c>/ColorSpace</c>: naming one would override the file's and repaint it. The control names its space.</summary>
    [Fact]
    public void Create_AnSYccJpeg2000File_LeavesTheColourSpaceToTheFile()
    {
        byte[] pdf = Convert(new[] { TestHelpers.JpxEmbedFixtures.SYccJp2(), Jp2(4, 4, 3) });

        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        PdfStream sYcc = ImageOn(doc, 0);
        Assert.Equal("JPXDecode", (doc.ResolveObject(sYcc[PdfName.Filter]) as PdfName)?.Value);
        Assert.Null(sYcc[PdfName.ColorSpace]);
        Assert.Equal(8d, (doc.ResolveObject(sYcc[PdfName.BitsPerComponent]) as PdfNumber).DoubleValue);
        PdfStream control = ImageOn(doc, 1);
        Assert.Equal(PdfColorSpaceNames.DeviceRGB, (doc.ResolveObject(control[PdfName.ColorSpace]) as PdfName)?.Value);
    }
}
