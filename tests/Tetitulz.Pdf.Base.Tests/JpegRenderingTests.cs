// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Rendering;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for JPEG output (Gap 36.1 — PdfPageRenderer JPEG path).
///
/// Verifies:
/// - JPEG bytes start with the SOI marker (0xFF 0xD8 0xFF).
/// - Output decodes back to an SKBitmap with the expected dimensions.
/// - Quality parameter is honoured (higher quality → larger file).
/// - Background is flattened to opaque white (no dark pixels on blank page).
/// - Quality bounds are enforced.
/// - Facade method signatures exist on PdfOperations.
/// </summary>
public class JpegRenderingTests
{
    // ------------------------------------------------------------------
    //  Minimal blank PDF helper
    // ------------------------------------------------------------------

    private static byte[] BuildMinimalPdf(string contentStream = "")
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            contentStream.Length, contentStream);
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
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

    private static bool StartsWithJpegSoi(byte[] data)
        => data != null && data.Length >= 3
           && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    // ------------------------------------------------------------------
    //  SOI / round-trip
    // ------------------------------------------------------------------

    [Fact]
    public void RenderPageAsJpg_ProducesValidJpeg()
    {
        byte[] pdf = BuildMinimalPdf("q Q");
        using var ms = new MemoryStream(pdf);
        byte[] jpg = PdfPageRenderer.RenderPageAsJpg(ms, 0, 72);

        Assert.NotNull(jpg);
        Assert.True(jpg.Length > 0);
        Assert.True(StartsWithJpegSoi(jpg), "JPEG must start with 0xFF 0xD8 0xFF (SOI marker).");

        using var decoded = SKBitmap.Decode(jpg);
        Assert.NotNull(decoded);
        // Letter @72dpi = 612x792.
        Assert.Equal(612, decoded.Width);
        Assert.Equal(792, decoded.Height);
    }

    [Fact]
    public void RenderPageAsJpg_BlankPage_FlattensToWhite()
    {
        // A blank page must render as pure white (not darkened by premul-alpha leak).
        byte[] pdf = BuildMinimalPdf("q Q");
        using var ms = new MemoryStream(pdf);
        byte[] jpg = PdfPageRenderer.RenderPageAsJpg(ms, 0, 36, quality: 100);

        using var decoded = SKBitmap.Decode(jpg);
        SKColor centre = decoded.GetPixel(decoded.Width / 2, decoded.Height / 2);
        // JPEG's DCT introduces minor noise; allow a generous tolerance, but the
        // centre of a blank page must be very light (no premul-alpha darkening).
        Assert.True(centre.Red   >= 240, $"Red={centre.Red}, expected ≥240");
        Assert.True(centre.Green >= 240, $"Green={centre.Green}, expected ≥240");
        Assert.True(centre.Blue  >= 240, $"Blue={centre.Blue}, expected ≥240");
    }

    // ------------------------------------------------------------------
    //  Quality
    // ------------------------------------------------------------------

    [Fact]
    public void RenderPageAsJpg_HigherQuality_ProducesLargerFile()
    {
        byte[] pdf = BuildMinimalPdf("q 0.5 0.25 0.75 rg 50 50 300 300 re f Q");
        using var lowMs  = new MemoryStream(pdf);
        using var highMs = new MemoryStream(pdf);

        byte[] low  = PdfPageRenderer.RenderPageAsJpg(lowMs,  0, 72, quality: 20);
        byte[] high = PdfPageRenderer.RenderPageAsJpg(highMs, 0, 72, quality: 95);

        Assert.True(StartsWithJpegSoi(low));
        Assert.True(StartsWithJpegSoi(high));
        Assert.True(high.Length > low.Length,
            $"quality=95 ({high.Length}B) should exceed quality=20 ({low.Length}B).");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-5)]
    public void RenderPageAsJpg_QualityOutOfRange_Throws(int quality)
    {
        byte[] pdf = BuildMinimalPdf("q Q");
        using var ms = new MemoryStream(pdf);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfPageRenderer.RenderPageAsJpg(ms, 0, 72, quality));
    }

    // ------------------------------------------------------------------
    //  RenderAllPagesAsJpg
    // ------------------------------------------------------------------

    [Fact]
    public void RenderAllPagesAsJpg_Stream_ReturnsOneJpegPerPage()
    {
        byte[] pdf = BuildMinimalPdf("q Q");
        using var ms = new MemoryStream(pdf);
        byte[][] pages = PdfPageRenderer.RenderAllPagesAsJpg(ms, 72);

        Assert.Single(pages);
        Assert.True(StartsWithJpegSoi(pages[0]));
    }

    [Fact]
    public void RenderAllPagesAsJpg_File_ReturnsOneJpegPerPage()
    {
        byte[] pdf  = BuildMinimalPdf("q Q");
        string path = Path.Combine(Path.GetTempPath(), $"jpg_test_{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            byte[][] pages = PdfPageRenderer.RenderAllPagesAsJpg(path, 72);
            Assert.Single(pages);
            Assert.True(StartsWithJpegSoi(pages[0]));
        }
        finally { File.Delete(path); }
    }

    // ------------------------------------------------------------------
    //  RenderPageTileAsJpg
    // ------------------------------------------------------------------

    [Fact]
    public void RenderPageTileAsJpg_ReturnsValidJpeg()
    {
        byte[] pdf = BuildMinimalPdf("q Q");
        using var ms = new MemoryStream(pdf);
        byte[] jpg = PdfPageRenderer.RenderPageTileAsJpg(ms, 0,
            clipX: 0, clipY: 0, clipW: 100, clipH: 100, dpi: 72);

        Assert.True(StartsWithJpegSoi(jpg));
        using var decoded = SKBitmap.Decode(jpg);
        Assert.Equal(100, decoded.Width);
        Assert.Equal(100, decoded.Height);
    }

    // ------------------------------------------------------------------
    //  PdfOperations facade
    // ------------------------------------------------------------------

    [Fact]
    public void PdfOperations_RenderPageToJpg_Stream_Works()
    {
        byte[] pdf = BuildMinimalPdf("q Q");
        using var ms = new MemoryStream(pdf);
        byte[] jpg = PdfOperations.RenderPageToJpg(ms, 0, 72);

        Assert.True(StartsWithJpegSoi(jpg));
    }

    [Fact]
    public void PdfOperations_RenderAllPagesToJpg_Stream_Works()
    {
        byte[] pdf = BuildMinimalPdf("q Q");
        using var ms = new MemoryStream(pdf);
        byte[][] pages = PdfOperations.RenderAllPagesToJpg(ms, 72);

        Assert.Single(pages);
        Assert.True(StartsWithJpegSoi(pages[0]));
    }

    [Fact]
    public void PdfOperations_RenderPageTileToJpg_Stream_Works()
    {
        byte[] pdf = BuildMinimalPdf("q Q");
        using var ms = new MemoryStream(pdf);
        byte[] jpg = PdfOperations.RenderPageTileToJpg(ms, 0, 0, 0, 100, 100, 72);

        Assert.True(StartsWithJpegSoi(jpg));
    }

    // ------------------------------------------------------------------
    //  Existing PNG path must still work (regression guard)
    // ------------------------------------------------------------------

    [Fact]
    public void RenderPage_PngPath_StillReturnsValidPng()
    {
        byte[] pdf = BuildMinimalPdf("q Q");
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);

        Assert.NotNull(png);
        Assert.True(png.Length >= 8);
        Assert.Equal(0x89, png[0]);
        Assert.Equal((byte)'P', png[1]);
        Assert.Equal((byte)'N', png[2]);
        Assert.Equal((byte)'G', png[3]);
    }
}
