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
/// Raster/SVG parity for the image <c>/Decode</c> array (ISO 32000-1 §8.9.5.2).
/// </summary>
/// <remarks>
/// <para>Written after GAPS.md §2b, by applying its lesson to the next feature with the same
/// shape: a spec rule implemented once per renderer, where
/// <c>PdfSvgRenderer.ApplyDecodeArrayToSvgBitmap</c> carries a comment saying it "Mirrors the
/// behaviour of <c>ContentStreamInterpreter.ApplyDecodeArrayToBitmap</c>". §2b's finding was that a
/// comment asserting two implementations agree is a claim with a shelf life, and that only a test
/// holds it.</para>
/// <para><c>/Decode [0 0.5]</c> maps sample <c>v</c> to <c>v/2</c> in channel units, so every odd
/// sample lands on a half-level — the operand class that separates truncation from rounding.</para>
/// </remarks>
public sealed class DecodeArrayParityTests
{
    [Theory]
    [InlineData("FF7F>", 2)]   // 255 → 127.5, 127 → 63.5
    public void GreyImageDecodeArray_QuantisesIdenticallyInBothRenderers(string hex, int width)
    {
        byte[] pdf = GreyImagePdf(hex, width, "/Decode [0 0.5]");

        using var raster = PixelAssert.Render(pdf);
        byte rasterLeft  = PixelAssert.At(raster, 150f, 500f).Red;
        byte rasterRight = PixelAssert.At(raster, 250f, 500f).Red;

        using var svg = DecodeFirstSvgImage(pdf);
        byte svgLeft  = svg.GetPixel(0, 0).Red;
        byte svgRight = svg.GetPixel(1, 0).Red;

        Assert.Equal(rasterLeft, svgLeft);
        Assert.Equal(rasterRight, svgRight);
    }

    /// <summary>
    /// The identity control. <c>/Decode [0 1]</c> is a no-op in both renderers, so this passes
    /// whatever the quantisation convention is — it exists to show the fixture reaches the
    /// <c>/Decode</c> path at all rather than measuring something else.
    /// </summary>
    [Fact]
    public void GreyImageIdentityDecode_LeavesSamplesUntouched()
    {
        byte[] pdf = GreyImagePdf("FF7F>", 2, "/Decode [0 1]");

        using var raster = PixelAssert.Render(pdf);
        Assert.Equal(255, PixelAssert.At(raster, 150f, 500f).Red);
        Assert.Equal(127, PixelAssert.At(raster, 250f, 500f).Red);

        using var svg = DecodeFirstSvgImage(pdf);
        Assert.Equal(255, svg.GetPixel(0, 0).Red);
        Assert.Equal(127, svg.GetPixel(1, 0).Red);
    }

    /// <summary>
    /// <c>/Decode [6 0 R 7 0 R]</c> resolving to <c>[1 0]</c> inverts the image in both renderers.
    /// The SVG writer's check resolved the elements and said "not the identity"; its mapper then
    /// cast them raw, read <c>[0 1]</c>, and exported the scan un-inverted.
    /// </summary>
    [Fact]
    public void GreyImageIndirectDecode_InvertsInBothRenderers()
    {
        byte[] pdf = GreyImagePdf("FF7F>", 2, "/Decode [6 0 R 7 0 R]", "1", "0");

        using var raster = PixelAssert.Render(pdf);
        Assert.Equal(0, PixelAssert.At(raster, 150f, 500f).Red);
        Assert.Equal(128, PixelAssert.At(raster, 250f, 500f).Red);

        using var svg = DecodeFirstSvgImage(pdf);
        Assert.Equal(0, svg.GetPixel(0, 0).Red);
        Assert.Equal(128, svg.GetPixel(1, 0).Red);
    }

    // ------------------------------------------------------------------

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

    /// <param name="extraObjects">Bodies for objects 6, 7, … — values an indirect key can name.</param>
    private static byte[] GreyImagePdf(string hexData, int width, string extraImageKeys,
                                       params string[] extraObjects)
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

        int o5 = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "5 0 obj\n<< /Type /XObject /Subtype /Image /Width {0} /Height 1"
            + " /ColorSpace /DeviceGray /BitsPerComponent 8 {1} /Filter /ASCIIHexDecode"
            + " /Length {2} >>\nstream\n{3}\nendstream\nendobj\n",
            width, extraImageKeys, hexData.Length, hexData);

        var offsets = new System.Collections.Generic.List<int> { o1, o2, o3, o4, o5 };
        for (int i = 0; i < extraObjects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0} 0 obj\n{1}\nendobj\n", 6 + i, extraObjects[i]);
        }

        int xref = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture, "xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.AppendFormat(CultureInfo.InvariantCulture, "trailer\n<< /Size {0} /Root 1 0 R >>\n", offsets.Count + 1);
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
