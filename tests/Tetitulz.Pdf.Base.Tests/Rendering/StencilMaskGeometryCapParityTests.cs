// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A stencil <c>/Mask</c> whose declared geometry exceeds the image limits is refused by
/// <b>both</b> renderers (§8.9.6.3 plus this library's DoS caps).
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> Both renderers refuse a mask larger than
/// <c>PdfLimits.MaxImageDimension</c> / <c>MaxImagePixelArea</c>. With only a non-positive test, an
/// exporter would walk its sampling loop with the mask's declared stride: for
/// <c>/Width 200000 /Height 1</c> that is a stride of 25 000, every base row maps to mask row 0, and
/// with <c>/Decode [1 0]</c> reversing the sense every byte past the decoded data reads as bit 0 —
/// so the whole image would go to alpha 0. <b>The same file would render fully opaque and export
/// fully transparent, with no diagnostic on either side.</b></para>
/// <para><b>Why the control matters more than usual here.</b> "The image is still opaque" is also
/// what an exporter that had stopped applying masks altogether produces, so the second case masks
/// with a mask that <i>is</i> within the limits and requires both renderers to honour it. Pixels are
/// decoded out of the SVG's embedded PNG rather than the markup being substring-matched, because the
/// claim is about alpha, which markup cannot show.</para>
/// </remarks>
public sealed class StencilMaskGeometryCapParityTests
{
    // 200000 x 1 — past MaxImageDimension (16384) on the first axis alone.
    private const string OversizedMask =
        "6 0 obj\n<< /Type /XObject /Subtype /Image /ImageMask true /Width 200000 /Height 1"
        + " /BitsPerComponent 1 /Decode [1 0] /Filter /ASCIIHexDecode /Length 3 >>\nstream\n00>\nendstream\nendobj\n";

    // The same mask, small enough to be honoured: /Decode [1 0] + a zero bit means "mask it out".
    private const string SmallMask =
        "6 0 obj\n<< /Type /XObject /Subtype /Image /ImageMask true /Width 2 /Height 1"
        + " /BitsPerComponent 1 /Decode [1 0] /Filter /ASCIIHexDecode /Length 3 >>\nstream\n00>\nendstream\nendobj\n";

    [Fact]
    public void OversizedStencilMask_IsRefusedByBothRenderers_LeavingTheImageOpaque()
    {
        byte[] pdf = MaskedImagePdf(OversizedMask);

        using var raster = PixelAssert.Render(pdf);
        Assert.True(PixelAssert.IsInk(PixelAssert.At(raster, 150f, 500f)),
            "the raster path stopped drawing the base image of an over-cap masked image");

        using var svg = DecodeFirstSvgImage(pdf);
        Assert.Equal(255, svg.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public void StencilMaskWithinTheLimits_IsHonouredByBothRenderers()
    {
        byte[] pdf = MaskedImagePdf(SmallMask);

        using var raster = PixelAssert.Render(pdf);
        Assert.True(PixelAssert.IsWhite(PixelAssert.At(raster, 150f, 500f)),
            "the raster path ignored a stencil /Mask that is within the limits");

        using var svg = DecodeFirstSvgImage(pdf);
        Assert.Equal(0, svg.GetPixel(0, 0).Alpha);
    }

    /// <summary>The stencil mask lands on the pixel it names, in both renderers.</summary>
    /// <remarks>
    /// <para><b>A 2x1 fixture cannot see a transposed mask, and the ones above are 2x1.</b> Pixel
    /// (0,0) is index 0 whether the lookup is <c>y * width + x</c> or <c>x * height + y</c>, so
    /// swapping the two changes nothing they assert. This is 2x2 with exactly ONE masked pixel and
    /// two positions asserted, which is the smallest fixture that distinguishes them: transposed,
    /// the hole moves from (1,0) to (0,1).</para>
    ///
    /// <para>Both renderers, because the mask is applied by separate code on each side and a
    /// difference here is invisible to a test that only asks whether <i>something</i> is
    /// transparent.</para>
    /// </remarks>
    [Fact]
    public void TheStencilMaskClearsThePixelItNames_InBothRenderers()
    {
        byte[] pdf = TwoByTwoMaskedImagePdf();

        using SKBitmap svg = DecodeFirstSvgImage(pdf);
        Assert.Equal(2, svg.Width);
        Assert.Equal(2, svg.Height);

        Assert.Equal(0, svg.GetPixel(1, 0).Alpha);
        Assert.Equal(255, svg.GetPixel(0, 1).Alpha);
        Assert.Equal(255, svg.GetPixel(0, 0).Alpha);
        Assert.Equal(255, svg.GetPixel(1, 1).Alpha);

        // The raster twin, on the page. The image occupies 100..300 x 400..600 and the masked
        // sample is the image's top-right quarter, which is the page's 200..300 x 500..600 --
        // PDF y grows upward, so the image's first row is the page's upper half.
        using SKBitmap raster = PixelAssert.Render(pdf);
        Assert.True(PixelAssert.IsWhite(PixelAssert.At(raster, 250f, 550f)),
            "the raster path did not clear the masked sample");
        Assert.True(PixelAssert.IsInk(PixelAssert.At(raster, 150f, 450f)),
            "the raster path cleared a sample the mask did not name");
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A 2x2 mid-grey image at 100..300 x 400..600 whose stencil <c>/Mask</c> names exactly one
    /// sample: row 0 is <c>0x40</c>, so bit 1 of the first row -- image pixel (1,0).
    /// </summary>
    private static byte[] TwoByTwoMaskedImagePdf()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]"
                  + " /Contents 4 0 R /Resources << /XObject << /Im1 5 0 R >> >> >>\nendobj\n");

        const string cs = "q 200 0 0 200 100 400 cm /Im1 Do Q";
        int o4 = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", cs.Length, cs);

        const string data = "80808080>";
        int o5 = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "5 0 obj\n<< /Type /XObject /Subtype /Image /Width 2 /Height 2"
            + " /ColorSpace /DeviceGray /BitsPerComponent 8 /Mask 6 0 R /Filter /ASCIIHexDecode"
            + " /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            data.Length, data);

        // 1 bpc, rows padded to a byte, MSB first. 0x40 = 0100_0000: sample 0 is 0 and sample 1
        // is 1, and the default /Decode [0 1] makes 1 the masked one.
        const string maskData = "4000>";
        int o6 = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "6 0 obj\n<< /Type /XObject /Subtype /Image /ImageMask true /Width 2 /Height 2"
            + " /BitsPerComponent 1 /Filter /ASCIIHexDecode /Length {0} >>\nstream\n{1}\n"
            + "endstream\nendobj\n",
            maskData.Length, maskData);

        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        foreach (int off in new[] { o1, o2, o3, o4, o5, o6 })
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Renders to SVG and decodes the first embedded raster image back to a bitmap.</summary>
    private static SKBitmap DecodeFirstSvgImage(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        const string marker = "base64,";
        int start = svg.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "The SVG export embedded no base64 image; the fixture never reached the image path.");
        start += marker.Length;
        int end = svg.IndexOfAny(new[] { '"', '\'' }, start);
        Assert.True(end > start, "Malformed data URI in the SVG export.");

        var bmp = SKBitmap.Decode(Convert.FromBase64String(svg.Substring(start, end - start)));
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>A 2x1 mid-grey image drawn at 100..300 x 400..600, carrying the given /Mask stream.</summary>
    private static byte[] MaskedImagePdf(string maskObject)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]"
                  + " /Contents 4 0 R /Resources << /XObject << /Im1 5 0 R >> >> >>\nendobj\n");

        const string cs = "q 200 0 0 200 100 400 cm /Im1 Do Q";
        int o4 = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", cs.Length, cs);

        const string data = "8080>";
        int o5 = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "5 0 obj\n<< /Type /XObject /Subtype /Image /Width 2 /Height 1"
            + " /ColorSpace /DeviceGray /BitsPerComponent 8 /Mask 6 0 R /Filter /ASCIIHexDecode"
            + " /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            data.Length, data);

        int o6 = sb.Length;
        sb.Append(maskObject);

        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        foreach (int off in new[] { o1, o2, o3, o4, o5, o6 })
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
