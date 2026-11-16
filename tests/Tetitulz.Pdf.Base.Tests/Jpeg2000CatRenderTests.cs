// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// End-to-end regression test for JPEG 2000 (JPXDecode) image rendering by the in-house decoder,
/// with no plugin registered.
///
/// The PDF Association's UnknownFilter-objstm.pdf embeds a 100x67 JP2 "cat" photo. Before
/// Core decoded JPEG 2000 itself (Wave 15), the page rendered nothing for that image without a
/// JPX plugin (the old external codec's, retired on 2026-09-26); the in-house decoder is the codec now. This test
/// builds an equivalent in-memory PDF whose only content is the cat image filling the page, then
/// asserts the rendered output is the photo (lots of non-white, varied pixels) rather than a
/// blank page.
/// </summary>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public sealed class Jpeg2000CatRenderTests
{
    [Fact]
    public void CatJpxImage_RendersThePhoto_InHouseWithNoPluginRegistered()
    {
        byte[] pdf = BuildCatPdf();
        PdfPluginHost.Instance.ResetForTesting();
        try
        {
            Assert.Null(PdfPluginHost.Instance.FindDecodePlugin("JPXDecode"));
            using var ms = new MemoryStream(pdf);
            byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
            using var bmp = SKBitmap.Decode(png);
            Assert.NotNull(bmp);

            int nonWhite = 0, dark = 0;
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.Red < 235 || c.Green < 235 || c.Blue < 235) nonWhite++;
                    if (c.Red < 80 && c.Green < 80 && c.Blue < 80) dark++;
                }

            // The cat fills the page: most pixels are non-white and the black fur
            // contributes a solid block of dark pixels. A blank page would score ~0.
            Assert.True(nonWhite > 1500, $"expected the cat photo, only {nonWhite} non-white px");
            Assert.True(dark > 200, $"expected dark fur, only {dark} dark px (image likely not decoded)");
        }
        finally
        {
            PdfPluginHost.Instance.ResetForTesting();
        }
    }

    // Page is exactly the image size (100x67) and the image fills it, so the whole
    // raster is the photo — keeps the pixel assertion simple and robust.
    private static byte[] BuildCatPdf()
    {
        byte[] jp2 = CatJp2.Bytes;
        var hex = new StringBuilder(jp2.Length * 2 + 1);
        foreach (byte b in jp2) hex.Append(b.ToString("X2"));
        hex.Append('>'); // ASCIIHexDecode EOD

        string content = "q 100 0 0 67 0 0 cm /Im0 Do Q";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 67]" +
                  " /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            content.Length, content);
        int o5 = sb.Length;
        sb.AppendFormat(
            "5 0 obj\n<< /Type /XObject /Subtype /Image /Width 100 /Height 67 " +
            "/ColorSpace /DeviceRGB /BitsPerComponent 8 " +
            "/Filter [/ASCIIHexDecode /JPXDecode] /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            hex.Length, hex);
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
