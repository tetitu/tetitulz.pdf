// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> draws an <c>&lt;image&gt;</c> whose <c>href</c> is a PNG or JPEG
/// <c>data:</c> URI (SVG 1.1 §5.7), as an inline image placed by <c>x</c>, <c>y</c>, <c>width</c>,
/// <c>height</c> and <c>preserveAspectRatio</c>.
/// </summary>
/// <remarks>
/// An image was reported and dropped, so a logo or a photo embedded in an exported drawing came out as
/// a hole. An inline image (<c>BI … ID … EI</c>) needs no resource, so the one-page skeleton carries
/// it as it is. A reference outside the document is not fetched.
/// </remarks>
public class SvgImageTests
{
    /// <summary>
    /// A 2 × 1 PNG, red then blue, into a 20 × 10 box at (10, 20): the aspect matches, so the scale is
    /// 10, and the unit square maps to [20 0 0 −10 10 30] — its top row at y = 20, unflipped.
    /// </summary>
    [Fact]
    public void APngIsDrawnInPlace()
    {
        string c = Content(Image(Png(2, 1, new SKColor(255, 0, 0), new SKColor(0, 0, 255)), @"x=""10"" y=""20"" width=""20"" height=""10"""));

        Assert.Equal(new[] { 20f, 0, 0, -10, 10, 30 }, Operands(c, "cm"));
        Assert.Contains("BI /W 2 /H 1 /CS /RGB /BPC 8 /F /Fl ID ", c, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 255, 0, 0, 0, 0, 255 }, Inflate(Samples(c)));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// A reader draws it: this library's renderer, a second opinion on the inline image's syntax and
    /// placement. The 2 × 1 image fills the top half of a 200-point page, flipped as by default: red
    /// on the left, blue on the right, the lower half blank.
    /// </summary>
    [Fact]
    public void TheRendererDrawsTheImage()
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" +
            Image(Png(2, 1, new SKColor(255, 0, 0), new SKColor(0, 0, 255)), @"width=""200"" height=""100""") + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });
        var page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);

        (byte R, byte G, byte B) At(int x, int y) { int i = y * page.Stride + x * 4; return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]); }
        Assert.Equal(((byte)255, (byte)0, (byte)0), At(50, 50));
        Assert.Equal(((byte)0, (byte)0, (byte)255), At(150, 50));
        Assert.Equal(((byte)255, (byte)255, (byte)255), At(100, 150));
    }

    /// <summary>A JPEG goes in as it is, <c>/DCT</c>, with its size and components from its frame.</summary>
    [Fact]
    public void AJpegIsCarriedAsItIs()
    {
        byte[] jpeg = Jpeg(4, 2);
        string c = Content(Image("data:image/jpeg;base64," + Convert.ToBase64String(jpeg), @"width=""4"" height=""2"""));

        Assert.Contains("BI /W 4 /H 2 /CS /RGB /BPC 8 /F /DCT ID ", c, StringComparison.Ordinal);
        Assert.Equal(jpeg, Samples(c));
    }

    /// <summary>
    /// A four-component JPEG goes in as the image writers embed it (Core's <c>JpegColorSpace</c>):
    /// with Adobe's marker, CMYK under the inverting <c>/Decode</c>; without it, its polarity unknown,
    /// not drawn and said. Each is the other's control.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFourComponentJpeg_IsCmykOnlyUnderAdobesMarker(bool adobe)
    {
        byte[] jpeg = FourComponentJpegHeader(adobe);
        string c = Content(Image("data:image/jpeg;base64," + Convert.ToBase64String(jpeg), @"width=""4"" height=""2"""));

        if (adobe)
        {
            Assert.Contains("BI /W 4 /H 2 /CS /CMYK /BPC 8 /F /DCT /D [1 0 1 0 1 0 1 0] ID ", c, StringComparison.Ordinal);
            Assert.Null(SvgToPdfConverter.LastError);
        }
        else
        {
            Assert.DoesNotContain("BI", Lines(c).SelectMany(l => l.Split(' ')));
            Assert.Contains("Adobe APP14", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A JPEG marker chain declaring a 4 × 2 four-component frame, with or without an Adobe APP14
    /// segment; no entropy data, since the converter carries the bytes and decodes none.
    /// </summary>
    private static byte[] FourComponentJpegHeader(bool adobe)
    {
        var b = new System.Collections.Generic.List<byte> { 0xFF, 0xD8 };
        if (adobe)
            b.AddRange(new byte[] { 0xFF, 0xEE, 0x00, 0x0E, (byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e',
                                    0x00, 0x64, 0x00, 0x00, 0x00, 0x00, 0x00 });
        b.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 20, 0x08, 0x00, 0x02, 0x00, 0x04, 0x04 });
        for (byte k = 1; k <= 4; k++) b.AddRange(new byte[] { k, 0x11, 0x00 });
        b.AddRange(new byte[] { 0xFF, 0xDA, 0x00, 14, 0x04 });
        for (byte k = 1; k <= 4; k++) b.AddRange(new byte[] { k, 0x00 });
        b.AddRange(new byte[] { 0x00, 0x3F, 0x00, 0xFF, 0xD9 });
        return b.ToArray();
    }

    /// <summary>
    /// A PNG with transparency is an image XObject with a soft mask, not an inline image, which can
    /// carry none: a half-transparent red pixel stays red, its alpha of 128 in the mask, and nothing is
    /// reported. Batch 29 composited it over white and reported that.
    /// </summary>
    [Fact]
    public void APngsAlphaIsKeptAsASoftMask()
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" +
            Image(Png(1, 1, new SKColor(255, 0, 0, 128)), @"width=""1"" height=""1""") + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200, FlipY = false });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        string c = Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());

        Assert.DoesNotContain("BI", Lines(c).SelectMany(l => l.Split(' ')));
        Assert.Contains("/Im0 Do", Lines(c));
        var xobjects = Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[Tetitulz.Pdf.Core.Objects.PdfName.XObject]));
        var image = Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfStream>(doc.ResolveObject(xobjects[new Tetitulz.Pdf.Core.Objects.PdfName("Im0")]));
        var mask = Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfStream>(doc.ResolveObject(image[Tetitulz.Pdf.Core.Objects.PdfName.SMask]));
        Assert.Equal(new byte[] { 255, 0, 0 }, image.GetDecodedData());
        Assert.Equal(new byte[] { 128 }, mask.GetDecodedData());
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// A linked, transparent image makes three objects, numbered in the order they were made: the
    /// mask (5), the image (6) that names it, and the link (7) the page's <c>/Annots</c> lists.
    /// </summary>
    [Fact]
    public void AnImageAndALinkShareTheObjectNumbering()
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200""><a href=""https://example.org/"">" +
            Image(Png(1, 1, new SKColor(255, 0, 0, 128)), @"width=""10"" height=""10""") + "</a></svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var pageDict = doc.Pages[0].Dictionary;

        var xobjects = Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[Tetitulz.Pdf.Core.Objects.PdfName.XObject]));
        var imageRef = Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfReference>(xobjects[new Tetitulz.Pdf.Core.Objects.PdfName("Im0")]);
        var image = Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfStream>(doc.ResolveObject(imageRef));
        var maskRef = Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfReference>(image[Tetitulz.Pdf.Core.Objects.PdfName.SMask]);
        var annots = Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfArray>(doc.ResolveObject(pageDict[Tetitulz.Pdf.Core.Objects.PdfName.Annots]));

        Assert.Equal(5, maskRef.ReferenceObjectNumber);
        Assert.Equal(6, imageRef.ReferenceObjectNumber);
        Assert.Equal(7, Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfReference>(Assert.Single(annots)).ReferenceObjectNumber);
    }

    /// <summary>
    /// A reader composites it: over a blue page-sized rect, a PNG transparent on the left and opaque
    /// red on the right renders blue then red. Composited over white, the left was white.
    /// </summary>
    [Fact]
    public void TheRendererCompositesTheSoftMask()
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200""><rect width=""200"" height=""200"" fill=""#0000ff""/>" +
            Image(Png(2, 1, new SKColor(255, 0, 0, 0), new SKColor(255, 0, 0, 255)), @"width=""200"" height=""100""") + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });
        var page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);

        (byte R, byte G, byte B) At(int x, int y) { int i = y * page.Stride + x * 4; return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]); }
        Assert.Equal(((byte)0, (byte)0, (byte)255), At(50, 50));
        Assert.Equal(((byte)255, (byte)0, (byte)0), At(150, 50));
    }

    /// <summary>Without a width and height the image takes its own size, one user unit a pixel.</summary>
    [Fact]
    public void AnImageWithoutASizeTakesItsOwn()
    {
        string c = Content(Image(Png(3, 2, SKColors.Red), @"x=""5"" y=""5"""));

        Assert.Equal(new[] { 3f, 0, 0, -2, 5, 7 }, Operands(c, "cm"));
    }

    /// <summary>slice overflows the box, so the box clips; meet does not need to.</summary>
    [Fact]
    public void ASlicedImageIsClippedToItsBox()
    {
        string sliced = Content(Image(Png(2, 1, SKColors.Red), @"width=""10"" height=""10"" preserveAspectRatio=""xMidYMid slice"""));

        Assert.Contains("W", Lines(sliced));
        Assert.True(Array.IndexOf(Lines(sliced), "W") < Array.IndexOf(Lines(sliced), "EI"));
    }

    /// <summary>
    /// A transparent image drawn as an XObject is clipped the same way, and the clip ends with it:
    /// every <c>q</c> has its <c>Q</c>, so what follows is not clipped to the image's box.
    /// </summary>
    [Fact]
    public void ASlicedTransparentImagesClipEndsWithIt()
    {
        string[] lines = Lines(Content(
            Image(Png(2, 1, new SKColor(255, 0, 0, 128)), @"width=""10"" height=""10"" preserveAspectRatio=""xMidYMid slice""") +
            @"<rect x=""50"" y=""50"" width=""10"" height=""10""/>"));

        Assert.True(Array.IndexOf(lines, "W") >= 0 && Array.IndexOf(lines, "W") < Array.IndexOf(lines, "/Im0 Do"), "the image was not clipped");
        Assert.Equal(lines.Count(l => l == "q"), lines.Count(l => l == "Q"));
    }

    /// <summary>A 2 × 1 GIF, red then blue, written by hand: header, a two-colour table, one image block of LZW codes clear, 0, 1, end.</summary>
    private const string RedBlueGif = "data:image/gif;base64,R0lGODlhAgABAIAAAP8AAAAA/ywAAAAAAgABAAACAkQKADs=";

    /// <summary>The same GIF with a graphics control extension naming index 1, the blue pixel, transparent.</summary>
    private const string RedTransparentGif = "data:image/gif;base64,R0lGODlhAgABAIAAAP8AAAAA/yH5BAEAAAEALAAAAAACAAEAAAICRAoAOw==";

    /// <summary>A GIF is decoded and carried as a PNG is: the same inline image, the same samples.</summary>
    [Fact]
    public void AGifIsDrawnLikeAPng()
    {
        string c = Content(Image(RedBlueGif, @"x=""10"" y=""20"" width=""20"" height=""10"""));

        Assert.Equal(new[] { 20f, 0, 0, -10, 10, 30 }, Operands(c, "cm"));
        Assert.Contains("BI /W 2 /H 1 /CS /RGB /BPC 8 /F /Fl ID ", c, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 255, 0, 0, 0, 0, 255 }, Inflate(Samples(c)));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>A GIF's transparent index is a soft mask: the image is an XObject, and the transparent pixel shows the page.</summary>
    [Fact]
    public void AGifsTransparentIndexIsASoftMask()
    {
        string c = Content(Image(RedTransparentGif, @"width=""200"" height=""100"""));
        var at = RenderAt(Image(RedTransparentGif, @"width=""200"" height=""100"""));

        Assert.Contains("/Im0 Do", c, StringComparison.Ordinal);
        Assert.Equal(((byte)255, (byte)0, (byte)0), at(50, 50));
        Assert.Equal(((byte)255, (byte)255, (byte)255), at(150, 50));
    }

    /// <summary>
    /// Hostile input: a GIF whose header declares 20 000 × 1 is refused by the authored-image size
    /// rule on the codec's dimensions, before a pixel is decoded, and reported — the same rule the
    /// PNG and JPEG paths apply. The image block holds no data at all.
    /// </summary>
    [Fact]
    public void AnOversizedGifIsRefusedBeforeItIsDecoded()
    {
        var gif = new MemoryStream();
        gif.Write(Encoding.ASCII.GetBytes("GIF89a"));
        gif.Write(new byte[] { 0x20, 0x4E, 0x01, 0x00, 0x80, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF });   // 20000 × 1, two colours
        gif.Write(new byte[] { 0x2C, 0x00, 0x00, 0x00, 0x00, 0x20, 0x4E, 0x01, 0x00, 0x00, 0x02, 0x00, 0x3B });   // the image block, empty; the trailer

        string c = Content(Image("data:image/gif;base64," + Convert.ToBase64String(gif.ToArray()), @"width=""10"" height=""10"""));

        Assert.DoesNotContain("BI", Lines(c).SelectMany(l => l.Split(' ')));
        Assert.Contains("20000x1", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
        Assert.Contains("per-side", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Hostile input: a GIF whose header is sound but whose image data is not — an LZW code size of
    /// 12, past the format's 8 — opens as a codec and fails to decode; it is reported and not drawn,
    /// rather than drawn from whatever the buffer held.
    /// </summary>
    [Fact]
    public void AGifThatDoesNotDecodeIsReported()
    {
        byte[] gif = Convert.FromBase64String(RedBlueGif.Substring("data:image/gif;base64,".Length));
        gif[29] = 0x0C;   // the LZW minimum code size, 2 in the sound file

        string c = Content(Image("data:image/gif;base64," + Convert.ToBase64String(gif), @"width=""10"" height=""10"""));

        Assert.DoesNotContain("BI", Lines(c).SelectMany(l => l.Split(' ')));
        Assert.DoesNotContain("/Im0 Do", c, StringComparison.Ordinal);
        Assert.Contains("did not decode", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>A WebP — lossless, so the samples are exact — is drawn like a PNG, and its alpha is a soft mask.</summary>
    [Fact]
    public void AWebPIsDrawnLikeAPng()
    {
        string opaque = Content(Image(WebP(2, 1, new SKColor(255, 0, 0), new SKColor(0, 0, 255)), @"width=""20"" height=""10"""));
        string translucent = Content(Image(WebP(2, 1, new SKColor(255, 0, 0), new SKColor(0, 0, 255, 0)), @"width=""20"" height=""10"""));

        Assert.Contains("BI /W 2 /H 1 /CS /RGB /BPC 8 /F /Fl ID ", opaque, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 255, 0, 0, 0, 0, 255 }, Inflate(Samples(opaque)));
        Assert.Contains("/Im0 Do", translucent, StringComparison.Ordinal);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>A 10 × 10 SVG document, its left half red and its right half blue, as the two data: URI forms.</summary>
    private const string InnerSvg =
        @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""10"" height=""10""><rect width=""5"" height=""10"" fill=""#ff0000""/><rect x=""5"" width=""5"" height=""10"" fill=""#0000ff""/></svg>";
    private static string SvgBase64(string svg = InnerSvg) => "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    private static string SvgUtf8(string svg = InnerSvg) => "data:image/svg+xml;utf8," + Uri.EscapeDataString(svg);

    /// <summary>
    /// An SVG image is a nested viewport: the 10-unit document in a 200 × 100 box keeps its aspect
    /// (xMidYMid meet), 100 wide and centred — red from 50 to 100, blue from 100 to 150, the page
    /// beside them; as base64 or as percent-encoded text.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnSvgImageIsANestedViewport(bool base64)
    {
        var at = RenderAt(Image(base64 ? SvgBase64() : SvgUtf8(), @"width=""200"" height=""100"""));

        Assert.Equal(((byte)255, (byte)0, (byte)0), at(75, 50));
        Assert.Equal(((byte)0, (byte)0, (byte)255), at(125, 50));
        Assert.Equal(((byte)255, (byte)255, (byte)255), at(25, 50));
        Assert.Equal(((byte)255, (byte)255, (byte)255), at(175, 50));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>The image's preserveAspectRatio governs: none stretches the document over the whole box.</summary>
    [Fact]
    public void AnSvgImageTakesTheImagesAspectRule()
    {
        var at = RenderAt(Image(SvgBase64(), @"width=""200"" height=""100"" preserveAspectRatio=""none"""));

        Assert.Equal(((byte)255, (byte)0, (byte)0), at(25, 50));
        Assert.Equal(((byte)0, (byte)0, (byte)255), at(175, 50));
    }

    /// <summary>An SVG image with no width or height on the image is the document's own size, at the image's place.</summary>
    [Fact]
    public void AnSvgImageWithoutASizeTakesTheDocumentsOwn()
    {
        var at = RenderAt(Image(SvgBase64(), @"x=""50"" y=""0"""));

        Assert.Equal(((byte)255, (byte)0, (byte)0), at(52, 5));
        Assert.Equal(((byte)0, (byte)0, (byte)255), at(57, 5));
        Assert.Equal(((byte)255, (byte)255, (byte)255), at(45, 5));
        Assert.Equal(((byte)255, (byte)255, (byte)255), at(65, 5));
    }

    /// <summary>The document's own ids resolve inside it: a gradient by url(#g) paints, red at its left and blue at its right.</summary>
    [Fact]
    public void AnSvgImagesIdsResolve()
    {
        string svg = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""10"" height=""10""><linearGradient id=""g""><stop offset=""0"" stop-color=""#ff0000""/><stop offset=""1"" stop-color=""#0000ff""/></linearGradient><rect width=""10"" height=""10"" fill=""url(#g)""/></svg>";
        var at = RenderAt(Image(SvgBase64(svg), @"width=""200"" height=""200"""));

        Assert.True(at(5, 100).Item1 > 200 && at(5, 100).Item3 < 50, $"the left was {at(5, 100)}, not red");
        Assert.True(at(195, 100).Item3 > 200 && at(195, 100).Item1 < 50, $"the right was {at(195, 100)}, not blue");
    }

    /// <summary>An SVG image inside an SVG image draws: the depth passes down.</summary>
    [Fact]
    public void AnSvgImageInsideAnSvgImageIsDrawn()
    {
        string outer = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""10"" height=""10"">" + Image(SvgBase64(), @"width=""10"" height=""10""") + "</svg>";
        var at = RenderAt(Image(SvgBase64(outer), @"width=""200"" height=""200"""));

        Assert.Equal(((byte)255, (byte)0, (byte)0), at(50, 100));
        Assert.Equal(((byte)0, (byte)0, (byte)255), at(150, 100));
    }

    /// <summary>An SVG image that does not parse, or whose root is not an svg, is reported and not drawn.</summary>
    [Theory]
    [InlineData("<svg", "does not parse")]
    [InlineData("<g xmlns=\"http://www.w3.org/2000/svg\"/>", "root is not an svg")]
    public void AnSvgImageThatIsNoDocumentIsReported(string svg, string reason)
    {
        string c = Content(Image(SvgUtf8(svg), @"width=""10"" height=""10"""));

        Assert.DoesNotContain(Lines(c), l => l.EndsWith(" re", StringComparison.Ordinal));
        Assert.Contains(reason, SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("photo.png", "data:")]
    [InlineData("data:image/x-nothing;base64,AAAAAAAA", "not an image this decodes")]
    [InlineData("data:image/png,not-base64", "base64")]
    public void AnImageThatCannotBeDrawnIsReported(string href, string reason)
    {
        string c = Content(Image(href, @"width=""10"" height=""10"""));

        Assert.DoesNotContain("BI", Lines(c).SelectMany(l => l.Split(' ')));
        Assert.Contains(reason, SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Hostile input: a PNG header declaring 20 000 × 1 pixels is refused by the authored-image size
    /// rule — past the per-side limit — before anything is decoded, and reported. The decoder's own
    /// ceiling is on area (40 megapixels), which this shape passes, so the refusal is the rule's.
    /// </summary>
    [Fact]
    public void AnOversizedImageIsRefusedBeforeItIsDecoded()
    {
        var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' });
        png.Write(new byte[] { 0, 0, 0x4E, 0x20, 0, 0, 0, 1, 8, 2, 0, 0, 0, 0, 0, 0, 0 });

        string c = Content(Image("data:image/png;base64," + Convert.ToBase64String(png.ToArray()), @"width=""10"" height=""10"""));

        Assert.DoesNotContain("BI", Lines(c).SelectMany(l => l.Split(' ')));
        Assert.Contains("20000x1", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
        Assert.Contains("per-side", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// An image takes <c>opacity</c>, its own and its ancestors', but not <c>fill-opacity</c>, which
    /// belongs to a paint, and an image has none.
    /// </summary>
    [Fact]
    public void AnImagesOpacityIsItsOwn()
    {
        string translucent = Content(Image(Png(1, 1, SKColors.Red), @"width=""1"" height=""1"" opacity=""0.5"""));
        string filled = Content(@"<g fill-opacity=""0.5"">" + Image(Png(1, 1, SKColors.Red), @"width=""1"" height=""1""") + "</g>");

        string[] lines = Lines(translucent);
        int gs = Array.FindIndex(lines, l => l.EndsWith(" gs", StringComparison.Ordinal));
        Assert.True(gs >= 0 && gs < Array.FindIndex(lines, l => l.StartsWith("BI ", StringComparison.Ordinal)), "the image's opacity was not set before it");
        Assert.DoesNotContain(Lines(filled), l => l.EndsWith(" gs", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ helpers

    private static string Image(string href, string attributes) => $@"<image href=""{href}"" {attributes}/>";

    private static string Png(int w, int h, params SKColor[] pixels)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (int i = 0; i < w * h; i++) bmp.SetPixel(i % w, i / w, pixels[Math.Min(i, pixels.Length - 1)]);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
    }

    /// <summary>A lossless WebP, so its samples come back as they went in.</summary>
    private static string WebP(int w, int h, params SKColor[] pixels)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (int i = 0; i < w * h; i++) bmp.SetPixel(i % w, i / w, pixels[Math.Min(i, pixels.Length - 1)]);
        using var pixmap = bmp.PeekPixels();
        using var data = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100));
        return "data:image/webp;base64," + Convert.ToBase64String(data.ToArray());
    }

    /// <summary>The page rendered by this library's renderer, read at a pixel: the default, flipped page mapping.</summary>
    private static Func<int, int, (byte, byte, byte)> RenderAt(string body)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });
        var page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);
        return (x, y) => { int i = y * page.Stride + x * 4; return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]); };
    }

    private static byte[] Jpeg(int w, int h)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bmp.Erase(SKColors.Green);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    /// <summary>The bytes between <c>ID </c> and the newline before <c>EI</c>.</summary>
    private static byte[] Samples(string content)
    {
        int id = content.IndexOf(" ID ", StringComparison.Ordinal) + 4;
        int ei = content.LastIndexOf("\nEI", StringComparison.Ordinal);
        return Encoding.Latin1.GetBytes(content.Substring(id, ei - id));
    }

    private static byte[] Inflate(byte[] zlib)
    {
        using var z = new ZLibStream(new MemoryStream(zlib), CompressionMode.Decompress);
        var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    private static string Content(string body)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200, FlipY = false });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
    }

    private static string[] Lines(string content) => content.Split('\n').Select(l => l.Trim()).ToArray();

    private static float[] Operands(string content, string op)
    {
        string line = Assert.Single(Lines(content), l => l.EndsWith(" " + op, StringComparison.Ordinal));
        return line.Substring(0, line.Length - op.Length - 1).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray();
    }
}
