// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Rendered raster output carries the library's name in each format's own metadata slot — PNG
/// <c>tEXt</c>, JPEG <c>COM</c> — and is still a valid, pixel-identical image.
/// </summary>
/// <remarks>
/// The second half is the important half. A stamp that corrupts the render is far worse than no
/// stamp, so every test here <b>decodes</b> the result and checks the fixture's red rectangle is
/// still red, rather than only searching the bytes for a string.
/// </remarks>
public class ImageProvenanceTests
{
    private static byte[] RedPagePdf() => MinimalPdfBuilder.CreatePdfWithRedRectangle();

    [Fact]
    public void RenderPageToPng_CarriesTheProducerInATextChunk_AndStillDecodes()
    {
        using var input = new MemoryStream(RedPagePdf());
        byte[] png = PdfOperations.RenderPageToPng(input, 0, 72);

        // Still a PNG, by signature...
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);

        // ...carrying the stamp...
        string ascii = Encoding.Latin1.GetString(png);
        Assert.Contains("tEXt", ascii, StringComparison.Ordinal);
        Assert.Contains("Software", ascii, StringComparison.Ordinal);
        Assert.Contains(PdfProducer.ProductName, ascii, StringComparison.Ordinal);

        // ...and still decoding to the page that was rendered.
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        SKColor left = bmp.GetPixel(bmp.Width / 4, bmp.Height / 2);
        Assert.True(left.Red > 200 && left.Green < 60 && left.Blue < 60,
            $"expected the left half to still be red after stamping, got {left}");
    }

    /// <summary>
    /// The JFIF APP0 segment stays directly after SOI (JFIF 1.02: it "shall immediately follow the
    /// SOI marker"), and the producer comment comes after it. This test used to assert the COM at
    /// offset 2, pinning the defect.
    /// </summary>
    [Fact]
    public void RenderPageAsJpg_KeepsJfifApp0FirstAndCarriesTheProducerInALaterComment_AndStillDecodes()
    {
        using var input = new MemoryStream(RedPagePdf());
        byte[] jpg = PdfPageRenderer.RenderPageAsJpg(input, 0, 72);

        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, jpg[..4]);
        Assert.Equal("JFIF\0", Encoding.Latin1.GetString(jpg, 6, 5));
        int com = IndexOf(jpg, new byte[] { 0xFF, 0xFE });
        Assert.True(com > 4, $"the comment follows APP0 (found at {com})");

        Assert.Contains(PdfProducer.ProductName, Encoding.Latin1.GetString(jpg), StringComparison.Ordinal);

        using var bmp = SKBitmap.Decode(jpg);
        Assert.NotNull(bmp);
        SKColor left = bmp.GetPixel(bmp.Width / 4, bmp.Height / 2);
        Assert.True(left.Red > 180 && left.Green < 90 && left.Blue < 90,
            $"expected the left half to still be red after stamping, got {left}");
    }

    /// <summary>
    /// The producer string carries a version, so a support request can tell which build wrote the
    /// file. Asserting only the product name would pass against a bare literal.
    /// </summary>
    [Fact]
    public void ProducerValue_IncludesAVersion_AndNoCommitHash()
    {
        string v = PdfProducer.Value;

        Assert.StartsWith(PdfProducer.ProductName, v, StringComparison.Ordinal);
        Assert.True(v.Length > PdfProducer.ProductName.Length,
            $"the producer string carries no version: '{v}'");
        // Build metadata would make two otherwise identical outputs differ byte-for-byte.
        Assert.DoesNotContain("+", v);
    }

    // ---- ImageProvenance called directly: the byte layout of each stamp ----

    private static SKBitmap Checker()
    {
        var bmp = new SKBitmap(8, 8, SKColorType.Rgba8888, SKAlphaType.Premul);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                bmp.SetPixel(x, y, ((x + y) & 1) == 0 ? SKColors.Black : SKColors.White);
        return bmp;
    }

    private static byte[] Encode(SKEncodedImageFormat format)
    {
        using var bmp = Checker();
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    /// <summary>
    /// The encoder's APP0 (and any APPn after it) stays directly behind SOI; the comment goes after
    /// them, carries the producer, and the image decodes to the same pixels.
    /// </summary>
    [Fact]
    public void StampJpeg_KeepsTheApp0SegmentDirectlyAfterSoi_AndAddsTheProducerComment()
    {
        byte[] plain = Encode(SKEncodedImageFormat.Jpeg);
        Assert.True(plain[2] == 0xFF && plain[3] == 0xE0, "precondition: the encoder writes JFIF APP0 first");

        byte[] stamped = ImageProvenance.StampJpeg(plain);

        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, stamped[..4]);
        int com = IndexOf(stamped, new byte[] { 0xFF, 0xFE });
        Assert.True(com > 4, "the comment follows the APPn segments");
        Assert.Equal(PdfProducer.Value, Encoding.ASCII.GetString(stamped, com + 4, ((stamped[com + 2] << 8) | stamped[com + 3]) - 2));

        using var a = SKBitmap.Decode(plain);
        using var b = SKBitmap.Decode(stamped);
        Assert.Equal(a.Bytes, b.Bytes);
    }

    /// <summary>A JPEG with no APPn segment gets its comment straight after SOI, as before.</summary>
    [Fact]
    public void StampJpeg_WithoutAnAppSegment_PutsTheCommentAfterSoi()
    {
        byte[] bare = { 0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x02, 0xFF, 0xD9 };   // SOI, empty DQT, EOI
        byte[] stamped = ImageProvenance.StampJpeg(bare);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xFE }, stamped[..4]);
        Assert.Equal(new byte[] { 0xFF, 0xDB, 0x00, 0x02, 0xFF, 0xD9 }, stamped[^6..]);
    }

    /// <summary>The PNG tEXt chunk goes after IHDR with a correct CRC, and the image still decodes.</summary>
    [Fact]
    public void StampPng_AddsASoftwareChunkAfterIhdr_AndTheImageStillDecodes()
    {
        byte[] plain = Encode(SKEncodedImageFormat.Png);
        byte[] stamped = ImageProvenance.StampPng(plain);

        Assert.Equal(Encoding.ASCII.GetBytes("tEXt"), stamped[37..41]);   // 8 signature + 25 IHDR + 4 length
        Assert.True(IndexOf(stamped, Encoding.ASCII.GetBytes("Software\0" + PdfProducer.Value)) > 0);

        using var a = SKBitmap.Decode(plain);
        using var b = SKBitmap.Decode(stamped);
        Assert.NotNull(b);
        Assert.Equal(a.Bytes, b.Bytes);
    }

    /// <summary>
    /// A JFIF APP0 followed by an Exif APP1 (the layout cameras write): the comment goes after both,
    /// so APP1 still sits where Exif readers look for it.
    /// </summary>
    [Fact]
    public void StampJpeg_StepsOverEveryLeadingAppSegment()
    {
        byte[] jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x4A, 0x46, 0xFF, 0xE1, 0x00, 0x04, 0x45, 0x78, 0xFF, 0xDB, 0x00, 0x02, 0xFF, 0xD9 };
        byte[] stamped = ImageProvenance.StampJpeg(jpeg);
        Assert.Equal(jpeg[..14], stamped[..14]);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, stamped[14..16]);
        Assert.Equal(jpeg[14..], stamped[^6..]);
    }

    /// <summary>
    /// An APPn whose length runs past the data is not stepped over: the comment goes after the last
    /// well-formed segment, here straight after SOI, and the bytes after it are kept as they were.
    /// </summary>
    [Fact]
    public void StampJpeg_AnAppSegmentLongerThanTheData_IsNotSteppedOver()
    {
        byte[] broken = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0xFF, 0x4A };   // APP0 claiming 255 bytes
        byte[] stamped = ImageProvenance.StampJpeg(broken);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xFE }, stamped[..4]);
        Assert.Equal(new byte[] { 0xFF, 0xE0, 0x00, 0xFF, 0x4A }, stamped[^5..]);
    }

    /// <summary>
    /// The tEXt chunk's CRC is checked against an independent CRC-32 written here. A decode cannot
    /// see it: the reference PNG library drops an ancillary chunk with a bad CRC and decodes the image anyway.
    /// </summary>
    [Fact]
    public void StampPng_WritesTheChunksCrc()
    {
        byte[] stamped = ImageProvenance.StampPng(Encode(SKEncodedImageFormat.Png));
        int length = (stamped[33] << 24) | (stamped[34] << 16) | (stamped[35] << 8) | stamped[36];
        byte[] typeAndData = stamped[37..(41 + length)];
        uint stored = (uint)((stamped[41 + length] << 24) | (stamped[42 + length] << 16) | (stamped[43 + length] << 8) | stamped[44 + length]);

        uint crc = 0xFFFFFFFF;
        foreach (byte b in typeAndData)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        Assert.Equal(crc ^ 0xFFFFFFFF, stored);
    }

    [Fact]
    public void ANonImage_IsReturnedUnchanged()
    {
        byte[] junk = Encoding.ASCII.GetBytes("not an image at all");
        Assert.Same(junk, ImageProvenance.StampJpeg(junk));
        Assert.Same(junk, ImageProvenance.StampPng(junk));
    }
}
