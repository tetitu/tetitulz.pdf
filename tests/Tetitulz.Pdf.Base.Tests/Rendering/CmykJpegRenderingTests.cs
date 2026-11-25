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
/// A real Adobe CMYK JPEG, through both renderers.
/// </summary>
/// <remarks>
/// <para><b>The fixture is a four-component JPEG, not an RGB one labelled CMYK.</b>
/// <c>ImageDecodeArrayTests</c>' CMYK regression encodes RGB — SkiaSharp cannot encode CMYK — and
/// tags it <c>/DeviceCMYK</c>, which pins the <c>/Decode</c> rule but never exercises a CMYK
/// decode, so it cannot check the renderer's "SkiaSharp handles the APP14 inversion". This one was written by an independent tool
/// 12.2.0 (<c>Image.new('CMYK')</c>, saved as JPEG at quality 100 without subsampling): 16×16, the
/// left eight columns pure cyan (C = 255) and the right eight pure yellow (Y = 255), with an Adobe
/// APP14 marker (transform 0) and the samples stored inverted — an independent tool reads it back as exactly
/// those two colours.</para>
///
/// <para><b>Both renderers, all three <c>/Decode</c> shapes.</b> A four-component JPEG's colour
/// comes from the decoder's own APP14 handling, so the array is not consulted: the inverted
/// convention producers write, an identity array and none at all render alike. That is a
/// statement about this library, pinned so it cannot drift on one side only — an SVG exporter
/// sending the last two out as raw CMYK JPEG bytes while the raster path converts them fails.</para>
/// </remarks>
public class CmykJpegRenderingTests
{
    private const string AdobeCmykJpegBase64 =
        "/9j/7gAOQWRvYmUAZAAAAAAA/9sAQwABAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEB" +
        "AQEBAQEBAQEBAQEB/8AAFAgAEAAQBEMRAE0RAFkRAEsRAP/EAB8AAAEFAQEBAQEBAAAAAAAAAAABAgMEBQYHCAkKC//EALUQAAIBAwMC" +
        "BAMFBQQEAAABfQECAwAEEQUSITFBBhNRYQcicRQygZGhCCNCscEVUtHwJDNicoIJChYXGBkaJSYnKCkqNDU2Nzg5OkNERUZHSElK" +
        "U1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6g4SFhoeIiYqSk5SVlpeYmZqio6Slpqeoqaqys7S1tre4ubrCw8TFxsfIycrS09TV1tfY" +
        "2drh4uPk5ebn6Onq8fLz9PX29/j5+v/aAA4EQwBNAFkASwAAPwD/AD/6/v4r+/iv7+K/3+KK/wAAeiv8Aeiv9/iiv9/iiv8AAHor/9k=";

    public static TheoryData<string> DecodeShapes => new()
    {
        " /Decode [1 0 1 0 1 0 1 0]",   // the Adobe convention producers write
        " /Decode [0 1 0 1 0 1 0 1]",   // identity
        "",                             // none
    };

    [Theory]
    [MemberData(nameof(DecodeShapes))]
    public void TheRasterRendererShowsAnAdobeCmykJpegInItsOwnColours(string decode)
    {
        byte[] png;
        using (var input = new MemoryStream(PdfWithImage(decode), writable: false))
            png = PdfPageRenderer.RenderPage(input, 0, 72);

        using SKBitmap page = SKBitmap.Decode(png);

        // The image fills 200×200 points at (100, 400); at 72 dpi its middle row is y = 292.
        AssertCyan(page.GetPixel(150, 292));
        AssertYellow(page.GetPixel(250, 292));
    }

    [Theory]
    [MemberData(nameof(DecodeShapes))]
    public void TheSvgExporterEmbedsAFourComponentJpegAsConvertedPixels(string decode)
    {
        string svg;
        using (var input = new MemoryStream(PdfWithImage(decode), writable: false))
            svg = PdfSvgRenderer.RenderPageToSvg(input, 0);

        XNamespace svgNs = "http://www.w3.org/2000/svg";
        XNamespace xlink = "http://www.w3.org/1999/xlink";
        string href = XDocument.Parse(svg).Descendants(svgNs + "image")
            .Select(e => (string)e.Attribute(xlink + "href"))
            .Single(h => h != null && h.StartsWith("data:image/", StringComparison.Ordinal));

        const string PngPrefix = "data:image/png;base64,";
        Assert.StartsWith(PngPrefix, href, StringComparison.Ordinal);

        using SKBitmap image = SKBitmap.Decode(Convert.FromBase64String(href.Substring(PngPrefix.Length)));
        AssertCyan(image.GetPixel(image.Width / 8, image.Height / 2));
        AssertYellow(image.GetPixel(image.Width * 7 / 8, image.Height / 2));
    }

    private static void AssertCyan(SKColor c)
        => Assert.True(c.Red < 64 && c.Green > 192 && c.Blue > 192, $"expected cyan, got {c}");

    private static void AssertYellow(SKColor c)
        => Assert.True(c.Red > 192 && c.Green > 192 && c.Blue < 64, $"expected yellow, got {c}");

    /// <summary>A one-page PDF drawing the fixture as a 16×16 DeviceCMYK DCTDecode image.</summary>
    private static byte[] PdfWithImage(string decodeEntry)
    {
        byte[] jpeg = Convert.FromBase64String(AdobeCmykJpegBase64);
        const string content = "q 200 0 0 200 100 400 cm /Im1 Do Q";

        static byte[] A(string s) => Encoding.ASCII.GetBytes(s);
        byte[] header = A("%PDF-1.4\n");
        byte[] catalog = A("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        byte[] pages = A("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        byte[] page = A("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
                        " /Resources << /XObject << /Im1 5 0 R >> >> >>\nendobj\n");
        byte[] contents = A($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        byte[] imageHead = A("5 0 obj\n<< /Type /XObject /Subtype /Image /Width 16 /Height 16 /BitsPerComponent 8" +
                             $" /ColorSpace /DeviceCMYK{decodeEntry} /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
        byte[] imageTail = A("\nendstream\nendobj\n");

        int o1 = header.Length;
        int o2 = o1 + catalog.Length;
        int o3 = o2 + pages.Length;
        int o4 = o3 + page.Length;
        int o5 = o4 + contents.Length;
        int xref = o5 + imageHead.Length + jpeg.Length + imageTail.Length;

        byte[] trailer = A($"xref\n0 6\n0000000000 65535 f \n{o1:D10} 00000 n \n{o2:D10} 00000 n \n" +
                           $"{o3:D10} 00000 n \n{o4:D10} 00000 n \n{o5:D10} 00000 n \n" +
                           $"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        using var pdf = new MemoryStream();
        foreach (byte[] part in new[] { header, catalog, pages, page, contents, imageHead, jpeg, imageTail, trailer })
            pdf.Write(part, 0, part.Length);
        return pdf.ToArray();
    }
}
