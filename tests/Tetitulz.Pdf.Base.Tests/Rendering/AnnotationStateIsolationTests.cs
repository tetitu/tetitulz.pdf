// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A page content stream that leaves a dirty graphics state (an unbalanced <c>q</c> +
/// top-level <c>cm</c>, common in real-world PDFs) must NOT leak that CTM into the annotation
/// pass: a 75×75 signature appearance would inherit a ≈0.06× scale + translate and render as a
/// tiny dot in the wrong place (PDF §7.8.2 — content streams must not leak graphics state into
/// annotations).
/// </summary>
public sealed class AnnotationStateIsolationTests
{
    [Fact]
    public void AnnotationAppearance_NotAffectedByDirtyPageContentCtm()
    {
        // Page content dirties the CTM hard: unbalanced `q` + a 0.1× scale/translate.
        // The annotation must still render in clean page space at its /Rect.
        byte[] pdf = BuildPdf(
            pageContent: "q 0.1 0 0 0.1 10 10 cm",                 // never restored
            apContent:   "1 0 0 rg 0 0 100 100 re f",              // fill the 100×100 BBox red
            rect:        "[40 40 160 160]",
            bbox:        "[0 0 100 100]");

        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);        // 72dpi → 1px = 1pt
        using var bmp = SKBitmap.Decode(png);
        Assert.Equal(200, bmp.Width);
        Assert.Equal(200, bmp.Height);

        // /Rect [40 40 160 160] → screen x[40..160] y[40..160] (Y-flip is symmetric).
        // Centre of the rect must be the annotation's red fill.
        Assert.True(IsRed(bmp.GetPixel(100, 100)),
            $"rect centre should be red but was {bmp.GetPixel(100, 100)} — annotation inherited dirty CTM");
        Assert.True(IsRed(bmp.GetPixel(60, 60)), "rect interior should be red");

        // A point well outside the /Rect must remain white (the fill is bounded to the rect).
        Assert.True(IsWhite(bmp.GetPixel(185, 185)), "outside the rect must stay white");
    }

    private static bool IsRed(SKColor c)   => c.Red > 150 && c.Green < 100 && c.Blue < 100;
    private static bool IsWhite(SKColor c) => c.Red > 230 && c.Green > 230 && c.Blue > 230;

    private static byte[] BuildPdf(string pageContent, string apContent, string rect, string bbox)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200]" +
                  " /Contents 4 0 R /Resources << >> /Annots [6 0 R] >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            pageContent.Length, pageContent);
        int o5 = sb.Length;
        sb.AppendFormat(
            "5 0 obj\n<< /Type /XObject /Subtype /Form /BBox {0} /Resources << >>" +
            " /Length {1} >>\nstream\n{2}\nendstream\nendobj\n",
            bbox, apContent.Length, apContent);
        int o6 = sb.Length;
        sb.AppendFormat(
            "6 0 obj\n<< /Type /Annot /Subtype /Stamp /Rect {0}" +
            " /AP << /N 5 0 R >> >>\nendobj\n", rect);
        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        foreach (int o in new[] { o1, o2, o3, o4, o5, o6 })
            sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
