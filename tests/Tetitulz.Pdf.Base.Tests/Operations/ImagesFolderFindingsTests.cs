// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// What reading <c>Base/Operations/Images/</c> end to end found: an exporter handing back a filter
/// chain's raw bytes as a codestream, thumbnails written into encrypted files and stored raw, a
/// raw insert accepting a buffer shorter than it declares, unfiltered samples stored uncompressed,
/// and an image-to-PDF path that decoded before it checked the size.
/// </summary>
public class ImagesFolderFindingsTests
{
    private static byte[] Jpeg()
    {
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    /// <summary>A one-page document whose single image XObject is object 5.</summary>
    private static byte[] PageWithImage(string imageDict, byte[] streamData, string extraObject = null)
    {
        var builder = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Im0 5 0 R >> >> >>")
            .Obj(4, "<< >>")
            .Obj(5, imageDict + $" /Length {streamData.Length} >>", streamData);
        if (extraObject != null) builder.Obj(6, extraObject);
        return builder.Finish();
    }

    // ------------------------------------------------------------------ the exporter

    /// <summary>
    /// A codestream under another filter is exported decoded through it. This returned the stream's
    /// raw bytes whenever the chain ended in <c>/DCTDecode</c> — here the ASCII hex text, labelled
    /// <c>image/jpeg</c>.
    /// </summary>
    [Fact]
    public void AJpegUnderAnotherFilterIsExportedAsTheJpeg()
    {
        byte[] jpeg = Jpeg();
        byte[] hex = Encoding.ASCII.GetBytes(Convert.ToHexString(jpeg) + ">");
        byte[] pdf = PageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceRGB " +
            "/BitsPerComponent 8 /Filter [/ASCIIHexDecode /DCTDecode]", hex);

        var extracted = PdfImageExporter.ExtractImage(new MemoryStream(pdf), 0, 0);

        Assert.Equal("image/jpeg", extracted.MimeType);
        Assert.Equal(jpeg, extracted.Data);
    }

    /// <summary>The same for JPEG 2000, under Flate — and the control: a lone codec filter.</summary>
    [Fact]
    public void AJpeg2000UnderFlateIsExportedAsTheCodestream_ALoneOneAsIs()
    {
        byte[] codestream = Encoding.ASCII.GetBytes("not decoded here: JPX passes through");

        byte[] wrapped = PageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceRGB " +
            "/BitsPerComponent 8 /Filter [/FlateDecode /JPXDecode]", Zlib(codestream));
        byte[] lone = PageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceRGB " +
            "/BitsPerComponent 8 /Filter /JPXDecode", codestream);

        Assert.Equal(codestream, PdfImageExporter.ExtractImage(new MemoryStream(wrapped), 0, 0).Data);
        Assert.Equal(codestream, PdfImageExporter.ExtractImage(new MemoryStream(lone), 0, 0).Data);
    }

    /// <summary>
    /// An indirect <c>/Filter</c> is read correctly through the indexer: the parser resolves it in
    /// place when it builds the stream. Asserted rather than assumed, because a review read the
    /// indexer and concluded otherwise.
    /// </summary>
    [Fact]
    public void AnIndirectFilterChainIsFollowed()
    {
        byte[] jpeg = Jpeg();
        byte[] hex = Encoding.ASCII.GetBytes(Convert.ToHexString(jpeg) + ">");
        byte[] pdf = PageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceRGB " +
            "/BitsPerComponent 8 /Filter 6 0 R", hex, "[/ASCIIHexDecode /DCTDecode]");

        var extracted = PdfImageExporter.ExtractImage(new MemoryStream(pdf), 0, 0);

        Assert.Equal("image/jpeg", extracted.MimeType);
        Assert.Equal(jpeg, extracted.Data);
    }

    // ------------------------------------------------------------------ thumbnails

    private static byte[] OnePage() => new PdfFixtureBuilder().Header()
        .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
        .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
        .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Resources << >> /Contents 4 0 R >>")
        .StreamObj(4, "<<", "0 0 1 rg 10 10 50 50 re f")
        .Finish();

    /// <summary>
    /// Thumbnails are not written into an encrypted document, whose reader would decrypt their
    /// clear-text samples into noise. This wrote them and reported the count; the unencrypted
    /// document is the control.
    /// </summary>
    [Fact]
    public void ThumbnailsAreRefusedForAnEncryptedDocument()
    {
        byte[] encrypted = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] >>")
            .Obj(4, "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -3904 " +
                    "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> " +
                    "/U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>")
            .Finish(" /Encrypt 4 0 R /ID [<01020304050607080910111213141516> <01020304050607080910111213141516>]");

        using var refusedOutput = new MemoryStream();
        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfThumbnailManager.GenerateThumbnails(new MemoryStream(encrypted), refusedOutput));
        Assert.Equal(0, refusedOutput.Length);

        using var output = new MemoryStream();
        Assert.Equal(1, PdfThumbnailManager.GenerateThumbnails(new MemoryStream(OnePage()), output));
    }

    /// <summary>
    /// A thumbnail is stored deflated, and still reads back as its full raster. It was stored raw,
    /// at three bytes a pixel for every page.
    /// </summary>
    [Fact]
    public void AThumbnailIsStoredDeflated()
    {
        using var output = new MemoryStream();
        PdfThumbnailManager.GenerateThumbnails(new MemoryStream(OnePage()), output, dpi: 72);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var thumb = (PdfStream)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Thumb]);
        Assert.Equal("FlateDecode", ((PdfName)thumb[PdfName.Filter]).Value);
        int w = ((PdfNumber)thumb[PdfName.Width]).IntValue, h = ((PdfNumber)thumb[PdfName.Height]).IntValue;
        Assert.Equal(w * h * 3, thumb.GetDecodedData().Length);
        Assert.True(thumb.RawData.Length < w * h * 3, "the thumbnail's samples were stored uncompressed");
    }

    // ------------------------------------------------------------------ the inserter and AddImage

    /// <summary>
    /// A raw buffer must be the raster it declares. A short one was written as an image stream
    /// shorter than its <c>/Width</c> × <c>/Height</c>. The exact buffer is the control, and its
    /// samples are stored deflated, decoding back unchanged.
    /// </summary>
    [Fact]
    public void ARawInsertMustBeExactlyTheRasterItDeclares_AndIsStoredDeflated()
    {
        var ex = Assert.Throws<ArgumentException>(() => PdfImageInserter.InsertRaw(
            new MemoryStream(OnePage()), new MemoryStream(), 0, new byte[4 * 4 * 3 - 1], 4, 4, 0, 0, 10, 10));
        Assert.Equal("rgbBytes", ex.ParamName);

        byte[] samples = Enumerable.Range(0, 4 * 4 * 3).Select(i => (byte)(i * 5)).ToArray();
        using var output = new MemoryStream();
        PdfImageInserter.InsertRaw(new MemoryStream(OnePage()), output, 0, samples, 4, 4, 0, 0, 10, 10);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var xobjects = (PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[PdfName.XObject]);
        var image = xobjects.Select(kv => doc.ResolveObject(kv.Value) as PdfStream)
            .Single(s => s != null && (s[PdfName.Subtype] as PdfName)?.Value == "Image");
        Assert.Equal("FlateDecode", ((PdfName)image[PdfName.Filter]).Value);
        Assert.Equal(samples, image.GetDecodedData());
    }

    /// <summary>
    /// The size limit is checked before the length: an oversized raster is refused as oversized,
    /// not as a short buffer.
    /// </summary>
    [Fact]
    public void AnOversizedRawInsertIsRefusedForItsSize()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PdfImageInserter.InsertRaw(
            new MemoryStream(OnePage()), new MemoryStream(), 0, new byte[3], 20000, 1, 0, 0, 10, 10));
        Assert.Contains("per-side limit", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ image to PDF

    /// <summary>
    /// A JPEG whose frame header claims 20 000 × 20 000 is refused: its header is written into the
    /// page's image dictionary verbatim, and the inserter already refused the same image.
    /// </summary>
    [Fact]
    public void AnOversizedJpegFrameIsNotWrittenIntoAPage()
    {
        byte[] hugeFrame =
        {
            0xFF, 0xD8,                                     // SOI
            0xFF, 0xC0, 0x00, 0x11, 0x08,                   // SOF0, length 17, 8-bit
            0x4E, 0x20, 0x4E, 0x20,                         // 20000 x 20000
            0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x00, 0x03, 0x11, 0x00,
            0xFF, 0xD9,                                     // EOI
        };

        var ex = Assert.Throws<PdfException>(() =>
            PdfImageToPdf.Create(new[] { hugeFrame }, new MemoryStream()));
        Assert.Contains("per-side limit", ex.Message, StringComparison.Ordinal);

        var result = PdfImageToPdf.Create(new[] { Jpeg() }, new MemoryStream());
        Assert.Equal(1, result.PagesWritten);
    }

    /// <summary>
    /// A PNG whose header claims 20 000 × 20 000 is refused on that claim, before anything is
    /// decoded. <c>SKBitmap.Decode</c> allocated the codec's real frame first.
    /// </summary>
    [Fact]
    public void AnOversizedPngIsRefusedBeforeItIsDecoded()
    {
        var ex = Assert.Throws<PdfException>(() =>
            PdfImageToPdf.Create(new[] { PngHeader(20000, 20000) }, new MemoryStream()));
        Assert.Contains("per-side limit", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A PNG signature and an IHDR chunk, CRC included — enough for a header reader.</summary>
    private static byte[] PngHeader(int width, int height)
    {
        var ihdr = new List<byte>();
        ihdr.AddRange(Encoding.ASCII.GetBytes("IHDR"));
        ihdr.AddRange(BigEndian(width));
        ihdr.AddRange(BigEndian(height));
        ihdr.AddRange(new byte[] { 8, 2, 0, 0, 0 });   // 8-bit truecolour
        byte[] chunk = ihdr.ToArray();

        var png = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        png.AddRange(BigEndian(13));
        png.AddRange(chunk);
        png.AddRange(BigEndian((int)Crc32(chunk)));
        png.AddRange(new byte[] { 0, 0, 0, 0 });
        png.AddRange(Encoding.ASCII.GetBytes("IEND"));
        png.AddRange(new byte[] { 0xAE, 0x42, 0x60, 0x82 });
        return png.ToArray();
    }

    private static byte[] BigEndian(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    // ------------------------------------------------------------------ ReplacementImage.Jpeg

    /// <summary>
    /// <c>ReplacementImage.Jpeg</c> answers through Core's <c>JpegColorSpace</c>: the same refusal,
    /// word for word, as the inserter's for a JPEG with an unsupported component count.
    /// </summary>
    [Fact]
    public void AReplacementJpegIsDescribedByTheSharedRule()
    {
        byte[] twoComponents =
        {
            0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0E, 0x08, 0x00, 0x08, 0x00, 0x08,
            0x02, 0x01, 0x11, 0x00, 0x02, 0x11, 0x00, 0xFF, 0xD9,
        };

        var replacement = Assert.Throws<ArgumentException>(() => ReplacementImage.Jpeg(twoComponents));
        var insert = Assert.Throws<ArgumentException>(() => PdfImageInserter.InsertJpeg(
            new MemoryStream(OnePage()), new MemoryStream(), 0, twoComponents, 8, 8, 0, 0, 10, 10));

        Assert.StartsWith("a JPEG with 2 components", replacement.Message, StringComparison.Ordinal);
        Assert.Equal(insert.Message, replacement.Message);
    }
}
