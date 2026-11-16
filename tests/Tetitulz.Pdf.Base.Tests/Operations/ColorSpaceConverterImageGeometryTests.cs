// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// What <c>PdfColorSpaceConverter</c> reads off an image before it rewrites the samples: the
/// geometry, whichever way it is spelled, and the sample depth it is able to convert.
/// </summary>
/// <remarks>
/// <para><b>The geometry was one level short of a fix beside it.</b> The comment above the
/// <c>/ColorSpace</c> read explains that it is resolved because an indirect value read as "not
/// DeviceRGB" and the image was skipped; <c>/Width</c> and <c>/Height</c>, two statements later, were
/// raw casts with the same consequence — an RGB image left behind in a document the caller asked to
/// be grey or CMYK, and a count that simply did not include it.</para>
///
/// <para><b>The depth was never read at all.</b> The conversions take one byte per channel, and the
/// only check before them is that the decoded buffer holds at least three bytes per pixel — which a
/// 16-bit image passes with room to spare. Its high and low bytes were converted as separate
/// channels, and the clone kept <c>/BitsPerComponent 16</c> over samples that were now one byte
/// each: the image was destroyed and counted as converted.</para>
/// </remarks>
public class ColorSpaceConverterImageGeometryTests
{
    [Theory]
    [InlineData(false)]   // /Width 2 /Height 2
    [InlineData(true)]    // /Width 6 0 R /Height 7 0 R
    public void AnRgbImageIsConvertedWhicheverWayItsGeometryIsSpelled(bool indirect)
    {
        byte[] pdf = ImagePdf(ImageFixtures.SolidRgb(2, 2, 200, 100, 50), bitsPerComponent: 8, indirect);

        using var output = new MemoryStream();
        int converted = PdfColorSpaceConverter.ConvertRgbToGrayscale(new MemoryStream(pdf), output);

        Assert.Equal(1, converted);
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var image = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(5));
        Assert.Equal(PdfName.DeviceGray, doc.ResolveObject(image[PdfName.ColorSpace]) as PdfName);
        Assert.Equal(4, image.GetDecodedData().Length);
    }

    /// <summary>
    /// A 16-bit RGB image is left exactly as it was, and not counted, rather than rewritten as eight
    /// bits under a sixteen-bit header.
    /// </summary>
    [Fact]
    public void ASixteenBitImageIsLeftAloneRatherThanConvertedAsEightBits()
    {
        var samples = new byte[2 * 2 * 6];
        for (int i = 0; i < samples.Length; i += 6) { samples[i] = 0xC8; samples[i + 2] = 0x64; samples[i + 4] = 0x32; }
        byte[] pdf = ImagePdf(samples, bitsPerComponent: 16, indirect: false);

        using var output = new MemoryStream();
        int converted = PdfColorSpaceConverter.ConvertRgbToGrayscale(new MemoryStream(pdf), output);

        Assert.Equal(0, converted);
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var image = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(5));
        Assert.Equal(PdfName.DeviceRGB, doc.ResolveObject(image[PdfName.ColorSpace]) as PdfName);
        Assert.Equal(samples, image.GetDecodedData());
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// One page drawing one uncompressed RGB image (object 5). Objects 6 and 7 hold its width and
    /// height, which the dictionary references when <paramref name="indirect"/>.
    /// </summary>
    private static byte[] ImagePdf(byte[] samples, int bitsPerComponent, bool indirect)
    {
        string width = indirect ? "6 0 R" : "2";
        string height = indirect ? "7 0 R" : "2";

        return new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
                    "/Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>")
            .StreamObj(4, "<<", "q 72 0 0 72 0 0 cm /Im0 Do Q")
            .Obj(5, $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} " +
                    $"/ColorSpace /DeviceRGB /BitsPerComponent {bitsPerComponent} /Length {samples.Length} >>", samples)
            .Obj(6, "2")
            .Obj(7, "2")
            .Finish();
    }
}
