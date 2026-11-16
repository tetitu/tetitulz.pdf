// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <see cref="PdfRenderOptions.ImageResampler"/> reaches the pixels.
/// </summary>
/// <remarks>
/// <para><b>It did not, for a release, and the tests it had could not tell.</b> Both sampling
/// decisions — the image draw and the soft-mask draw — read the image's <c>/Interpolate</c> flag
/// directly, so nothing read the option: three of its four values were unreachable, and a caller
/// choosing <see cref="ImageResampler.Nearest"/> to keep a scan pixel-sharp silently got Mitchell
/// wherever the flag was true. Its only two tests asserted that the property returned what had been
/// assigned to it, which a dead property passes.</para>
///
/// <para><b>These assert a pixel instead.</b> The fixture is a 2x2 checkerboard blown up to 64x64
/// with <c>/Interpolate true</c>. Nearest-neighbour leaves every pixel one of the two source
/// colours; a cubic filter blends across the boundary and produces intermediate values. Counting
/// pixels that are neither source colour separates them without pinning any particular filter's
/// exact weights.</para>
/// </remarks>
public class ImageResamplerOptionTests
{
    private const int Dpi = 72;

    /// <summary>The default defers to <c>/Interpolate</c>, so a flagged image is smoothed.</summary>
    [Fact]
    public void TheDefaultHonoursTheInterpolateFlag()
    {
        int blended = BlendedPixels(new PdfRenderOptions());

        Assert.True(blended > 50,
            $"the control failed: /Interpolate true produced only {blended} blended pixels, so "
          + "this fixture cannot tell a cubic filter from nearest-neighbour");
    }

    /// <summary>
    /// Asking for nearest-neighbour overrides the flag — the case that silently did nothing.
    /// </summary>
    [Fact]
    public void NearestOverridesTheInterpolateFlag()
    {
        int blended = BlendedPixels(new PdfRenderOptions
        {
            ImageResampler = ImageResampler.Nearest,
        });

        Assert.True(blended == 0,
            $"Nearest was ignored: {blended} pixels are neither source colour, so the image was "
          + "still filtered despite the caller asking for pixel-accurate sampling");
    }

    /// <summary>And asking for a cubic filter overrides a <i>false</i> flag the other way.</summary>
    /// <remarks>
    /// The mirror of the test above, and the one that stops "always use nearest" from passing both.
    /// </remarks>
    [Fact]
    public void MitchellOverridesAnAbsentInterpolateFlag()
    {
        int plain = BlendedPixels(new PdfRenderOptions(), interpolate: false);
        int cubic = BlendedPixels(new PdfRenderOptions
        {
            ImageResampler = ImageResampler.Mitchell,
        }, interpolate: false);

        Assert.True(plain == 0,
            $"the control failed: /Interpolate false already blended {plain} pixels");
        Assert.True(cubic > 50,
            $"Mitchell was ignored on an unflagged image: only {cubic} blended pixels");
    }

    // ====================================================================
    //  Fixtures
    // ====================================================================

    /// <summary>
    /// Pixels inside the drawn image that are neither pure source colour — i.e. the filter blended.
    /// </summary>
    private static int BlendedPixels(PdfRenderOptions options, bool interpolate = true)
    {
        byte[] png = PdfPageRenderer.RenderPage(
            new MemoryStream(Pdf(interpolate)), 0, Dpi, options);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);

        int n = 0;
        // The image occupies user space (10,10)-(74,74) on a 100x100 page at 72 dpi.
        for (int y = 30; y < 70; y++)
            for (int x = 14; x < 70; x++)
            {
                SKColor c = bmp!.GetPixel(x, y);
                bool black = c.Red < 24 && c.Green < 24 && c.Blue < 24;
                bool white = c.Red > 231 && c.Green > 231 && c.Blue > 231;
                if (!black && !white) n++;
            }
        return n;
    }

    /// <summary>A 2x2 black/white checkerboard scaled to 64x64 on a 100x100 page.</summary>
    private static byte[] Pdf(bool interpolate)
    {
        // 1 bpc, 2x2: row 0 = 10b, row 1 = 01b, each row padded to a byte.
        byte[] samples = { 0x80, 0x40 };
        string interp = interpolate ? "true" : "false";
        string image =
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray " +
            $"/BitsPerComponent 1 /Interpolate {interp} /Length {samples.Length} >>\nstream\n"
          + Encoding.Latin1.GetString(samples) + "\nendstream";

        const string content = "q 64 0 0 64 10 10 cm /Im1 Do Q";

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R " +
            "/Resources << /XObject << /Im1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            image,
        };

        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append('\n')
           .Append("0000000000 65535 f \n");
        foreach (int o in offsets) pdf.Append(o.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1)
           .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
