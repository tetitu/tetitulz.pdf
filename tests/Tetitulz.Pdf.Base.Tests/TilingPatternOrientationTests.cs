// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Regression test for tiling-pattern vertical orientation.
///
/// The tiling-pattern shader is built from a cell bitmap plus a local matrix that
/// maps the bitmap onto the page.  The page canvas applies a plain Scale(dpi,dpi)
/// and the PDF→screen Y-flip lives in TransformPoint, so the fill path (and the
/// shader's canvas-local space) is screen space (Y-down).  The pattern /Matrix,
/// however, is defined in PDF page space (Y-up).  If the shader matrix omits the
/// screen↔page Y-flip bridge, pattern content renders vertically mirrored — which
/// is invisible for symmetric fills but flips an embedded image (e.g. the Maccabi
/// "approved digitally" signature stamp in raw/pdfFilename.pdf rendered upside-down).
/// </summary>
public sealed class TilingPatternOrientationTests
{
    // A 100×100 page filled with a single-tile pattern that paints ONLY its top
    // half (pattern y 50..100) red.  Upright → the top of the page is red and the
    // bottom is blank.  A vertical flip swaps them.
    private static byte[] BuildTopHalfPatternPdf()
    {
        const string patCs = "1 0 0 rg 0 50 100 50 re f";   // red rect, pattern y∈[50,100]
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100]" +
                  " /Contents 4 0 R /Resources << /Pattern << /P1 5 0 R >> >> >>\nendobj\n");
        const string pageCs = "/Pattern cs /P1 scn 0 0 100 100 re f";
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            pageCs.Length, pageCs);
        int o5 = sb.Length;
        sb.AppendFormat("5 0 obj\n<< /PatternType 1 /PaintType 1 /TilingType 1" +
            " /BBox [0 0 100 100] /XStep 100 /YStep 100" +
            " /Resources << >> /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            patCs.Length, patCs);
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

    private static bool IsRed(SKColor p) => p.Red > 200 && p.Green < 60 && p.Blue < 60;

    [Fact]
    public void TilingPattern_PaintingTopHalf_RendersAtTopOfPage_NotFlipped()
    {
        byte[] pdf = BuildTopHalfPatternPdf();
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);   // 100×100 px
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);

        // Pattern paints PDF y∈[50,100] (the page's top half).  In screen space the
        // top of the page is the small-y rows, so the red must be near the top.
        var topPixel    = bmp.GetPixel(bmp.Width / 2, bmp.Height / 10);       // ~screen top
        var bottomPixel = bmp.GetPixel(bmp.Width / 2, bmp.Height * 9 / 10);   // ~screen bottom

        Assert.True(IsRed(topPixel),
            $"top of page should be red (pattern top half), was {topPixel}");
        Assert.False(IsRed(bottomPixel),
            $"bottom of page should NOT be red (pattern was vertically flipped), was {bottomPixel}");
    }
}
