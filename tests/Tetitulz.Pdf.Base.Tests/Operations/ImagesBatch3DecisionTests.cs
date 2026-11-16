// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The decisions in <see cref="PdfImageInserter"/> and <see cref="PdfThumbnailManager"/> that the
/// <c>Images</c> sweep, batch 3, found nothing asserting — each a mutation that survived.
/// </summary>
public class ImagesBatch3DecisionTests
{
    // ---------------------------------------------------------------- the inserter

    private static byte[] Page => MinimalPdfBuilder.CreateMinimalPdf();

    private static void InsertRaw(byte[] rgb, int w, int h, double x = 10, double y = 50, double wPt = 100, double hPt = 80)
    {
        using var output = new MemoryStream();
        PdfImageInserter.InsertRaw(new MemoryStream(Page), output, 0, rgb, w, h, x, y, wPt, hPt);
    }

    /// <summary>No JPEG at all is the null-argument the method documents, not a description refusal.</summary>
    [Fact]
    public void InsertJpeg_NullBytes_IsAnArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            PdfImageInserter.InsertJpeg(new MemoryStream(Page), new MemoryStream(), 0, null, 1, 1, 0, 0, 10, 10));
        Assert.Equal("jpegBytes", ex.ParamName);
    }

    /// <summary>A caller's height that disagrees with the frame is refused, as its width is; the frame's agreeing size is the control.</summary>
    [Fact]
    public void InsertJpeg_AHeightThatDisagreesWithTheFrame_IsRefused()
    {
        byte[] jpeg = ImageInsertionLimitTests.JpegDeclaring(10, 20);
        var ex = Assert.Throws<ArgumentException>(() =>
            PdfImageInserter.InsertJpeg(new MemoryStream(Page), new MemoryStream(), 0, jpeg, 10, 21, 0, 0, 10, 10));
        Assert.Contains("disagrees", ex.Message, StringComparison.Ordinal);

        using var output = new MemoryStream();
        PdfImageInserter.InsertJpeg(new MemoryStream(Page), output, 0, jpeg, 10, 20, 0, 0, 10, 10);
        Assert.True(output.Length > Page.Length);
    }

    /// <summary>Each size of zero is refused under its own name — a pixel size before anything is written, a placed size before it is drawn.</summary>
    [Theory]
    [InlineData(0, 1, 100, 80, "imageWidthPx")]
    [InlineData(1, 0, 100, 80, "imageHeightPx")]
    [InlineData(1, 1, 0, 80, "widthPt")]
    [InlineData(1, 1, 100, 0, "heightPt")]
    public void InsertRaw_AZeroSize_IsRefusedUnderItsOwnName(int w, int h, double wPt, double hPt, string param)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => InsertRaw(new byte[w * h * 3], w, h, wPt: wPt, hPt: hPt));
        Assert.Equal(param, ex.ParamName);
    }

    /// <summary>A height past the per-side limit is refused naming the height, not the width beside it.</summary>
    [Fact]
    public void InsertRaw_AnOversizedHeight_IsRefusedNamingTheHeight()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => InsertRaw(new byte[3], 1, PdfLimits.MaxImageDimension + 1));
        Assert.Equal("imageHeightPx", ex.ParamName);
    }

    /// <summary>A buffer one byte longer than the raster it claims is refused as surely as a short one.</summary>
    [Fact]
    public void InsertRaw_ABufferLongerThanTheRaster_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => InsertRaw(new byte[2 * 2 * 3 + 1], 2, 2));
        Assert.Equal("rgbBytes", ex.ParamName);
    }

    /// <summary>The image is placed where asked: its <c>cm</c> carries the size and then x before y.</summary>
    [Fact]
    public void InsertRaw_PlacesTheImageAtXThenY()
    {
        using var output = new MemoryStream();
        PdfImageInserter.InsertRaw(new MemoryStream(Page), output, 0, new byte[12], 2, 2, 10, 50, 100, 80);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var ops = ContentStreamParser.Parse(doc.Pages[0].GetContentStreamData());
        var cm = ops.Last(o => o.Operator == "cm").Operands.Select(o => ((PdfNumber)o).DoubleValue).ToArray();
        Assert.Equal(new[] { 100.0, 0, 0, 80, 10, 50 }, cm);
    }

    // ---------------------------------------------------------------- reading thumbnails

    /// <summary>A page index one past the last is refused naming the index, as a negative one is.</summary>
    [Fact]
    public void ReadThumbnail_AnIndexPastTheLastPage_IsRefused()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PdfThumbnailManager.ReadThumbnail(new MemoryStream(Page), 1));
        Assert.Equal("pageIndex", ex.ParamName);
    }

    private static byte[] JpxThumb(string dict, byte[] codestream)
        => JpxTestPdf.Pdf(72, 72, "", "/Thumb 5 0 R ", ($"<< /Type /XObject /Subtype /Image {dict} /Filter /JPXDecode", codestream));

    /// <summary>A decoded JPEG 2000 thumbnail's size is the codestream's, whatever the dictionary claims.</summary>
    [Fact]
    public void ReadThumbnail_ADecodedJpxThumb_TakesItsSizeFromTheCodestream()
    {
        var gray = JpxLosslessFixtures.Gray40x24_TwoLevels_Rpcl_ThreeLayers;
        var info = PdfThumbnailManager.ReadThumbnail(new MemoryStream(JpxThumb("/Width 7 /Height 5", gray.Codestream)), 0);
        Assert.Equal((gray.Width, gray.Height), (info.Width, info.Height));
    }

    /// <summary>A one-component codestream's samples are grey, a four-component one's CMYK — each named so.</summary>
    [Fact]
    public void ReadThumbnail_NamesAGreyAndACmykJpxThumbsSpace()
    {
        var gray = JpxLosslessFixtures.Gray40x24_TwoLevels_Rpcl_ThreeLayers;
        var info = PdfThumbnailManager.ReadThumbnail(new MemoryStream(JpxThumb($"/Width {gray.Width} /Height {gray.Height}", gray.Codestream)), 0);
        Assert.Equal(PdfColorSpaceNames.DeviceGray, info.ColorSpace);
        Assert.Equal(gray.Width * gray.Height, info.PixelData.Length);

        byte[] inks = Enumerable.Range(0, 8 * 4 * 4).Select(i => (byte)(i * 7)).ToArray();
        byte[] cmyk = JpxImageEncoding.Encode(inks, 8, 4, 4, 8, new JpxEncodingOptions { Jp2Container = false });
        info = PdfThumbnailManager.ReadThumbnail(new MemoryStream(JpxThumb("/Width 8 /Height 4", cmyk)), 0);
        Assert.Equal(PdfColorSpaceNames.DeviceCMYK, info.ColorSpace);
        Assert.Equal(inks, info.PixelData);
    }

    /// <summary>Only the pages that carry a <c>/Thumb</c> are listed.</summary>
    [Fact]
    public void ListPagesWithThumbnails_ListsOnlyThePagesThatCarryOne()
    {
        using var withThumbs = new MemoryStream();
        PdfThumbnailManager.GenerateThumbnails(new MemoryStream(TwoPages()), withThumbs, 8);
        Assert.Equal(new[] { 0, 1 }, PdfThumbnailManager.ListPagesWithThumbnails(new MemoryStream(withThumbs.ToArray())));
        Assert.Empty(PdfThumbnailManager.ListPagesWithThumbnails(new MemoryStream(TwoPages())));
    }

    // ---------------------------------------------------------------- generating thumbnails

    /// <summary>The resolution is bounded at both ends, and each bound itself is accepted.</summary>
    [Theory]
    [InlineData(PdfThumbnailManager.MinThumbnailDpi - 1, false)]
    [InlineData(PdfThumbnailManager.MaxThumbnailDpi + 1, false)]
    [InlineData(PdfThumbnailManager.MinThumbnailDpi, true)]
    [InlineData(PdfThumbnailManager.MaxThumbnailDpi, true)]
    public void GenerateThumbnails_TheResolutionIsBoundedAtBothEnds(int dpi, bool accepted)
    {
        void Generate() => PdfThumbnailManager.GenerateThumbnails(new MemoryStream(Page), new MemoryStream(), dpi);
        if (accepted) Assert.Equal(1, PdfThumbnailManager.GenerateThumbnails(new MemoryStream(Page), new MemoryStream(), dpi));
        else Assert.Equal("dpi", Assert.Throws<ArgumentOutOfRangeException>(Generate).ParamName);
    }

    /// <summary>
    /// A 200 × 100 page filled green comes back as a 200 × 100 thumbnail at 72 dpi — width and height
    /// each in their place — whose every sample is green, the channels in their order.
    /// </summary>
    [Fact]
    public void GenerateThumbnails_KeepsTheChannelsAndTheAxesInTheirPlaces()
    {
        using var output = new MemoryStream();
        Assert.Equal(1, PdfThumbnailManager.GenerateThumbnails(new MemoryStream(OnePage("0 1 0 rg 0 0 200 100 re f")), output, 72));

        var info = PdfThumbnailManager.ReadThumbnail(new MemoryStream(output.ToArray()), 0);
        Assert.Equal((200, 100), (info.Width, info.Height));
        Assert.Equal(200 * 100 * 3, info.PixelData.Length);
        Assert.Equal(new byte[] { 0, 255, 0 }, info.PixelData.Skip((50 * 200 + 100) * 3).Take(3).ToArray());
    }

    // ---------------------------------------------------------------- the image index

    /// <summary>
    /// One image the page draws by its own name <c>/Pg</c> and again, inside a form, as <c>/Inner</c>:
    /// object 5, under page 3's resources and form 6's.
    /// </summary>
    internal static byte[] ImageUnderTwoNames() => Objects(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R /Resources << /XObject << /Pg 5 0 R /Fm 6 0 R >> >> >>",
        "<< /Length 34 >>\nstream\nq 10 0 0 10 0 0 cm /Pg Do Q /Fm Do\nendstream",
        "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\n@\nendstream",
        "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << /XObject << /Inner 5 0 R >> >> /Length 13 >>\nstream\n/Inner Do    \nendstream");

    /// <summary>
    /// An image reached twice on one page is listed once, with page 0 once, and its names in ordinal
    /// order — <c>/Inner</c> before <c>/Pg</c>, the reverse of the order the walk meets them.
    /// </summary>
    [Fact]
    public void FindImages_AnImageReachedTwiceOnAPage_ListsThePageOnce_AndItsNamesInOrder()
    {
        var info = Assert.Single(PdfImageReplacer.FindImages(new MemoryStream(ImageUnderTwoNames())));
        Assert.Equal(new[] { 0 }, info.Pages);
        Assert.Equal(new[] { "Inner", "Pg" }, info.ResourceNames);
    }

    /// <summary>
    /// Replaced by its object number, the image is reported under the first name the walk meets — the
    /// page's own <c>/Pg</c>, not the form's <c>/Inner</c>.
    /// </summary>
    [Fact]
    public void ReplaceByObjectNumber_ReportsTheNameThePageDrawsItBy()
    {
        using var output = new MemoryStream();
        var result = PdfImageReplacer.ReplaceByObjectNumber(new MemoryStream(ImageUnderTwoNames()), output, 5,
            ReplacementImage.Gray8(new byte[] { 0 }, 1, 1));
        Assert.Equal("Pg", result.ResourceName);
    }

    // ---------------------------------------------------------------- the rasterizer

    /// <summary>A resolution of zero or below means the default, not a render at nothing.</summary>
    [Fact]
    public void RasterizeToPng_AZeroResolution_MeansTheDefault()
    {
        const string svg = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""30"" height=""15""><rect width=""30"" height=""15""/></svg>";
        byte[] atZero = SvgRasterizer.RasterizeToPng(svg, 0);
        byte[] atDefault = SvgRasterizer.RasterizeToPng(svg, SvgRasterizer.DefaultDpi);
        Assert.NotNull(atZero);
        using var zero = SkiaSharp.SKBitmap.Decode(atZero);
        using var standard = SkiaSharp.SKBitmap.Decode(atDefault);
        Assert.Equal((standard.Width, standard.Height), (zero.Width, zero.Height));
        Assert.Equal(80, zero.Width);       // 30 points at 192 dpi
    }

    // ---------------------------------------------------------------- fixtures

    private static byte[] OnePage(string content) => Build(new[] { content });
    private static byte[] TwoPages() => Build(new[] { "0 0 1 rg 0 0 50 50 re f", "1 0 0 rg 0 0 50 50 re f" });

    /// <summary>A document of 200 × 100 pages, each drawing its content.</summary>
    private static byte[] Build(IReadOnlyList<string> contents)
    {
        int n = contents.Count;
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, n).Select(i => $"{3 + 2 * i} 0 R"))}] /Count {n} >>",
        };
        for (int i = 0; i < n; i++)
        {
            bodies.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents {4 + 2 * i} 0 R /Resources << >> >>");
            bodies.Add($"<< /Length {contents[i].Length} >>\nstream\n{contents[i]}\nendstream");
        }
        return Objects(bodies.ToArray());
    }

    /// <summary>A document whose objects 1, 2, … are <paramref name="bodies"/>, with object 1 its catalog.</summary>
    internal static byte[] Objects(params string[] bodies)
    {
        using var ms = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        Write("%PDF-1.7\n");
        var offsets = new List<long>();
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(ms.Length);
            Write($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        long xref = ms.Length;
        var sb = new StringBuilder($"xref\n0 {bodies.Length + 1}\n0000000000 65535 f \n");
        foreach (long o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {bodies.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(sb.ToString());
        return ms.ToArray();
    }
}

/// <summary>
/// <see cref="DocumentImageIndex"/>'s depth cap, as a parameter: a form chain deeper than
/// <see cref="PdfLimits.MaxFormXObjectDepth"/> is walked no further, which is what keeps the walk's
/// recursion off the stack's end on an acyclic chain the cycle guard does not stop.
/// </summary>
[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public class DocumentImageIndexDepthTests
{
    /// <summary>The page draws form 5, which draws form 6, which draws form 7, which draws image 8.</summary>
    private static byte[] ThreeFormsDeep() => ImagesBatch3DecisionTests.Objects(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R /Resources << /XObject << /F 5 0 R >> >> >>",
        "<< /Length 5 >>\nstream\n/F Do\nendstream",
        "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /F 6 0 R >> >> /Length 5 >>\nstream\n/F Do\nendstream",
        "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /F 7 0 R >> >> /Length 5 >>\nstream\n/F Do\nendstream",
        "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /Im 8 0 R >> >> /Length 6 >>\nstream\n/Im Do\nendstream",
        "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\n@\nendstream");

    [Fact]
    public void AChainDeeperThanTheCap_IsWalkedNoFurther()
    {
        Assert.Single(PdfImageReplacer.FindImages(new MemoryStream(ThreeFormsDeep())));
        try
        {
            PdfLimits.MaxFormXObjectDepth = 2;
            Assert.Empty(PdfImageReplacer.FindImages(new MemoryStream(ThreeFormsDeep())));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }
}
