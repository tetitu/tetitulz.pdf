// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using System.Text;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for JPEG 2000 (JPXDecode) image rendering when no codec, or a declining one, is present.
/// </summary>
/// <remarks>
/// Joins <see cref="TestInfrastructure.PluginHostCollection"/> because two of these tests reset the
/// process-wide <see cref="PdfPluginHost"/> to reach the "no codec registered" path; without the
/// collection they would race the plugin-dispatch suites, which register a JPX decoder.
/// </remarks>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public sealed class Jpeg2000RenderingTests
{
    // ====================================================================
    //  An undecodable codestream and no plugin: the renderer says so, and
    //  the image falls through to the raw-sample path.
    // ====================================================================

    /// <summary>
    /// A <c>/JPXDecode</c> image the in-house decoder cannot read, with no plugin registered to
    /// try after it, cannot be painted as the document intended — and the renderer must <b>say
    /// so</b>: the decoder's refusal, and that nothing else was asked. Before the in-house decoder
    /// existed (Wave 15g) this was the "no JPEG 2000 codec is available" configuration report;
    /// there is always a codec now, so what the report names is the codestream.
    ///
    /// <para>The predecessor rendered exactly this file and asserted only that a PNG came back.
    /// Until the <c>libopenjp2</c> probe was removed this test skipped on hosts that had the
    /// library; with no native codec it runs everywhere.</para>
    /// </summary>
    [Fact]
    public void PageWithJpxImage_UndecodableAndNoPlugin_ReportsTheDecoderRefusalAndThatNoPluginIsRegistered()
    {
        PdfPluginHost.Instance.ResetForTesting();          // no IImageDecodePlugin for JPXDecode

        var reported = new List<(RenderDiagnosticKind Kind, string Detail)>();
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            lock (reported) reported.Add((kind, detail ?? string.Empty));
        }

        RenderDiagnostics.OnEvent += Sink;
        try
        {
            using var ms = new MemoryStream(BuildPdfWithJpxImage());
            PdfPageRenderer.RenderPage(ms, 0, 72);
        }
        finally
        {
            RenderDiagnostics.OnEvent -= Sink;
        }

        // The decoder's refusal specifically, with the reason the decoder gave and the fact that
        // no plugin was there to ask next — not merely any report mentioning JPEG 2000.
        lock (reported)
            Assert.Contains(reported, r =>
                r.Kind == RenderDiagnosticKind.ImageDecodeFailed
                && r.Detail.IndexOf("the decoder declined this codestream", StringComparison.Ordinal) >= 0
                && r.Detail.IndexOf("no IImageDecodePlugin is registered", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// The control for the JPX tests above: whatever the JPEG 2000 path does, a page that never
    /// touches it must still render its ordinary content. So this draws something and checks the
    /// pixels — a red square where it was asked for, white where it was not.
    ///
    /// <para>The predecessor was named "AlwaysRenders" but its content stream was <c>q Q</c>:
    /// push and pop, drawing nothing. It then asserted a non-empty PNG came back, which a blank
    /// page satisfies — so the test passed whether or not the renderer could draw at all, which is
    /// no baseline for anything.</para>
    /// </summary>
    [Fact]
    public void PageWithoutJpxImage_RendersOrdinaryContent()
    {
        // 1 0 0 rg = red; a 100x100 box with its lower-left corner at (100, 600).
        byte[] pdf = BuildMinimalPdf("1 0 0 rg 100 600 100 100 re f");

        using var bmp = Rendering.PixelAssert.Render(pdf);

        // 1 px per point, so PDF user space maps straight through: (150, 650) is the box centre.
        var inside = Rendering.PixelAssert.At(bmp, 150, 650);
        Assert.True(Rendering.PixelAssert.Near(inside, new SKColor(255, 0, 0)),
            $"expected red inside the filled box, got {inside}");

        var outside = Rendering.PixelAssert.At(bmp, 400, 300);
        Assert.True(Rendering.PixelAssert.IsWhite(outside),
            $"expected white outside the filled box, got {outside}");
    }

    /// <summary>
    /// The last link in the <c>/JPXDecode</c> chain. After the in-house decoder and the plugin
    /// have both declined the codestream — reported by the door as <c>ImageDecodeFailed</c> —
    /// nothing is drawn: the placement rectangle stays white. This once fell through to the
    /// raw-sample path "as a best effort", which painted the codestream's bytes as pixels: the
    /// fixture's 16 bytes are also a valid 2x2 RGB block, and drawing them would paint the four
    /// quadrants in those colours (the owner's decision). The control: a codestream the decoder reads is
    /// drawn in the same placement.
    /// </summary>
    [Fact]
    public void PageWithJpxImage_EveryCodecDeclines_IsNotDrawn()
    {
        PdfPluginHost.Instance.ResetForTesting();

        using var bmp = Rendering.PixelAssert.Render(BuildPdfWithJpxImage());

        // Drawn by `q 100 0 0 100 100 600 cm /Im1 Do Q` over (100,600)-(200,700): the four
        // quadrant centres the raw-path fallback used to paint.
        foreach ((float x, float y) in new[] { (125f, 675f), (175f, 675f), (125f, 625f), (175f, 625f) })
        {
            SKColor at = Rendering.PixelAssert.At(bmp, x, y);
            Assert.True(Rendering.PixelAssert.IsWhite(at), $"({x},{y}) was painted {at}; an undecodable JPEG 2000 image draws nothing");
        }

        JpxLosslessFixtures.Fixture f = JpxLosslessFixtures.Rgb40x24_Mct_TwoLevels;
        using var drawn = Rendering.PixelAssert.Render(BuildPdfWithJpxImage(f.Codestream, f.Width, f.Height));
        int ink = 0;
        for (int y = 601; y < 700; y += 3)
            for (int x = 101; x < 200; x += 3)
                if (!Rendering.PixelAssert.IsWhite(Rendering.PixelAssert.At(drawn, x, y))) ink++;
        Assert.True(ink > 500, $"the control's decodable image painted only {ink} of the sampled points");
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static byte[] BuildMinimalPdf(string cs)
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
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", cs.Length, cs);
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

    private static byte[] BuildPdfWithJpxImage()
    {
        // 2×2 "image" bytes — no decoder reads them as JPEG 2000; they are also a 2x2 RGB block,
        // which is how the raw-path fallback's paint was once told apart from nothing.
        byte[] fakeJpx = new byte[] { 0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20,
                                      0x00, 0x00, 0x00, 0x14, 0x66, 0x74, 0x79, 0x70 };
        return BuildPdfWithJpxImage(fakeJpx, 2, 2);
    }

    /// <summary>A Letter page drawing <paramref name="fakeJpx"/> as a <paramref name="w"/> x <paramref name="h"/> DeviceRGB JPXDecode image at (100,600)-(200,700), hex-encoded.</summary>
    private static byte[] BuildPdfWithJpxImage(byte[] fakeJpx, int w, int h)
    {
        string hexData = "";
        foreach (byte b in fakeJpx) hexData += b.ToString("X2");
        hexData += ">";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o5 = sb.Length; // image XObject
        sb.AppendFormat(
            "5 0 obj\n<< /Type /XObject /Subtype /Image /Width " + w + " /Height " + h + " " +
            "/ColorSpace /DeviceRGB /BitsPerComponent 8 " +
            "/Filter [/ASCIIHexDecode /JPXDecode] /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            hexData.Length, hexData);
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Font << /F1 6 0 R >>" +
                  " /XObject << /Im1 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        string cs = "q 100 0 0 100 100 600 cm /Im1 Do Q";
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", cs.Length, cs);
        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
