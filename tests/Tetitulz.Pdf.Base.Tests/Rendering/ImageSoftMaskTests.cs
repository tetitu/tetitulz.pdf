// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Tests that /SMask (soft mask) alpha is correctly applied when rendering image XObjects.
/// Root cause of original bug: bitmaps were decoded with SKAlphaType.Opaque, causing Skia
/// to ignore alpha bytes written by ApplyImageSoftMask — transparent stamp background appeared
/// as solid black instead of transparent.
/// </summary>
public class ImageSoftMaskTests
{
    [Fact]
    public void ImageWithSMask_TransparentAreasShowPageBackground()
    {
        // 3x1 RGB image: black(0,0,0) | red(255,0,0) | black(0,0,0)
        // SMask:         0(transparent) | 255(opaque) | 0(transparent)
        // Transparent pixels should show white page background (NOT solid black).
        byte[] pdfBytes = BuildSmaskPdf();
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdfBytes), 0, 72);

        using var pngData = SKData.CreateCopy(png);
        using var bmp = SKBitmap.Decode(pngData);
        Assert.NotNull(bmp);
        Assert.True(bmp.Width >= 3, $"Bitmap width={bmp.Width} too small");

        int midY = bmp.Height / 2;
        SKColor leftPx   = bmp.GetPixel(bmp.Width / 6,     midY);
        SKColor centerPx = bmp.GetPixel(bmp.Width / 2,     midY);
        SKColor rightPx  = bmp.GetPixel(5 * bmp.Width / 6, midY);

        // Transparent pixels (SMask=0): image has black, but alpha=0 → page shows through (white).
        Assert.True(leftPx.Red > 200,
            $"Left transparent pixel R={leftPx.Red} should be near-white (page bg), not black");
        Assert.True(rightPx.Red > 200,
            $"Right transparent pixel R={rightPx.Red} should be near-white (page bg), not black");

        // Opaque red pixel (SMask=255).
        Assert.True(centerPx.Red > centerPx.Blue,
            $"Center opaque pixel R={centerPx.Red} B={centerPx.Blue} should be reddish");
    }

    [Fact]
    public void ColorKeyMask_MaskedPixelsShowPageBackground()
    {
        // 3x1 RGB image: red(255,0,0) | green(0,255,0) | blue(0,0,255)
        // /Mask [200 255 0 10 0 10] masks pixel with R∈[200,255] G∈[0,10] B∈[0,10] → only red pixel masked
        // Masked pixel should show white page background.
        byte[] pdfBytes = BuildColorKeyMaskPdf();
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdfBytes), 0, 72);

        using var pngData = SKData.CreateCopy(png);
        using var bmp = SKBitmap.Decode(pngData);
        Assert.NotNull(bmp);

        int midY = bmp.Height / 2;
        SKColor leftPx   = bmp.GetPixel(bmp.Width / 6, midY);  // masked red → transparent
        SKColor centerPx = bmp.GetPixel(bmp.Width / 2, midY);  // unmasked green

        // Masked pixel: should show near-white page background, not solid red.
        Assert.True(leftPx.Red > 150 || leftPx.Green > 150,
            $"Masked pixel should be near page background, got R={leftPx.Red} G={leftPx.Green} B={leftPx.Blue}");

        // Unmasked green pixel: green channel dominant.
        Assert.True(centerPx.Green > centerPx.Red,
            $"Unmasked green pixel should have G>R, got R={centerPx.Red} G={centerPx.Green}");
    }

    /// <summary>
    /// Applying a soft mask walks the bitmap's own buffer, on a bitmap a real render produced.
    /// </summary>
    /// <remarks>
    /// <para><b>The unit tests prove the walk works; this proves the renderer reaches it.</b>
    /// <see cref="BitmapPixels"/> keeps an allocating fallback for a colour type whose byte
    /// offsets it cannot trust, and a decode that started handing back such a surface would take
    /// that path on every image — same pixels, and the whole allocation back. Nothing about the
    /// output could say so, because the two paths agree.</para>
    ///
    /// <para>The counters are <c>[ThreadStatic]</c>, and a single-page render stays on the calling
    /// thread, so this reads its own render's work rather than the suite's.</para>
    /// </remarks>
    [Fact]
    public void ApplyingASoftMaskWalksTheBitmapBuffer()
    {
        byte[] pdfBytes = BuildSmaskPdf();

        BitmapPixels.ResetCounters();
        PdfPageRenderer.RenderPage(new MemoryStream(pdfBytes), 0, 72);

        Assert.True(BitmapPixels.BufferWalks > 0,
            "the soft-mask apply never reached the buffer walk");
        Assert.Equal(0, BitmapPixels.PropertyFallbacks);
    }

    /// <summary>The SVG export applies the same soft mask through the same buffer walk.</summary>
    /// <remarks>
    /// <b>Both renderers or neither.</b> The two read the same files and must agree, and each has
    /// its own image <c>/SMask</c> pass — so a conversion applied to one is a divergence until it
    /// is applied to the other. The SVG side's is hand-written rather than going through
    /// <see cref="BitmapPixels"/>'s shapes, because it reads three surfaces; it reports through the
    /// same counters precisely so this assertion can exist.
    /// </remarks>
    [Fact]
    public void TheSvgExportAppliesASoftMaskThroughTheBitmapBuffer()
    {
        byte[] pdfBytes = BuildSmaskPdf();

        BitmapPixels.ResetCounters();
        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdfBytes), 0);

        // Parsed, not substring-matched: an SVG assertion that reads the text can be satisfied by
        // an attribute value.
        XDocument document = XDocument.Parse(svg);
        Assert.NotEmpty(document.Descendants().Where(e => e.Name.LocalName == "image"));

        Assert.True(BitmapPixels.BufferWalks > 0,
            "the SVG soft-mask apply never reached the buffer walk");
        Assert.Equal(0, BitmapPixels.PropertyFallbacks);
    }

    // ------------------------------------------------------------------
    //  PDF builder helpers — builds minimal but valid PDFs with correct xref
    // ------------------------------------------------------------------

    private static byte[] BuildSmaskPdf()
    {
        // Image XObject: 3×1 RGB, black | red | black
        byte[] imgPx  = { 0,0,0,  255,0,0,  0,0,0 };
        // SMask XObject: 3×1 Gray, 0 | 255 | 0
        byte[] maskPx = { 0, 255, 0 };

        return BuildPdf(w: 3, h: 1, imgPixels: imgPx, maskPixels: maskPx, colorKeyMask: null);
    }

    private static byte[] BuildColorKeyMaskPdf()
    {
        // Image XObject: 3×1 RGB, red | green | blue
        byte[] imgPx = { 255,0,0,  0,255,0,  0,0,255 };
        return BuildPdf(w: 3, h: 1, imgPixels: imgPx, maskPixels: null,
                        colorKeyMask: "[200 255 0 10 0 10]");
    }

    /// <summary>
    /// Builds a minimal valid PDF with a single page containing one image XObject.
    /// When maskPixels != null, the image has an /SMask.
    /// When colorKeyMask != null, the image has a /Mask colour-key array.
    /// </summary>
    private static byte[] BuildPdf(int w, int h, byte[] imgPixels,
        byte[] maskPixels, string colorKeyMask)
    {
        // ---- object assembly ----
        // obj 1: catalog, obj 2: pages, obj 3: page, obj 4: content stream
        // obj 5: image XObject, obj 6 (optional): SMask XObject

        var acc = new List<byte>();
        var offsets = new Dictionary<int, int>();

        void Emit(string s)  => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void EmitBytes(byte[] b) => acc.AddRange(b);

        Emit("%PDF-1.4\n");

        // Helper: emit object header, record offset
        void BeginObj(int num)
        {
            offsets[num] = acc.Count;
            Emit($"{num} 0 obj\n");
        }
        void EndObj() => Emit("endobj\n");

        int smaskRef = maskPixels != null ? 6 : 0;

        // obj 1: catalog
        BeginObj(1); Emit("<< /Type /Catalog /Pages 2 0 R >>\n"); EndObj();
        // obj 2: pages
        BeginObj(2); Emit("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\n"); EndObj();
        // obj 3: page (72×72 pt, image scaled to fill)
        BeginObj(3);
        Emit("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72]\n" +
             "   /Resources << /XObject << /Img 5 0 R >> >>\n" +
             "   /Contents 4 0 R >>\n");
        EndObj();

        // obj 4: content stream
        string cs = "q 72 0 0 72 0 0 cm /Img Do Q";
        BeginObj(4);
        Emit($"<< /Length {Encoding.Latin1.GetByteCount(cs)} >>\nstream\n{cs}\nendstream\n");
        EndObj();

        // obj 5: image XObject
        BeginObj(5);
        string smaskEntry = smaskRef > 0 ? $" /SMask {smaskRef} 0 R" : "";
        string maskEntry  = colorKeyMask != null ? $" /Mask {colorKeyMask}" : "";
        Emit($"<< /Type /XObject /Subtype /Image /Width {w} /Height {h}" +
             $" /ColorSpace /DeviceRGB /BitsPerComponent 8" +
             $"{smaskEntry}{maskEntry} /Length {imgPixels.Length} >>\nstream\n");
        EmitBytes(imgPixels);
        Emit("\nendstream\n");
        EndObj();

        // obj 6 (optional): SMask XObject
        if (maskPixels != null)
        {
            BeginObj(6);
            Emit($"<< /Type /XObject /Subtype /Image /Width {w} /Height {h}" +
                 $" /ColorSpace /DeviceGray /BitsPerComponent 8" +
                 $" /Length {maskPixels.Length} >>\nstream\n");
            EmitBytes(maskPixels);
            Emit("\nendstream\n");
            EndObj();
        }

        // xref
        int xrefOffset = acc.Count;
        int objCount = smaskRef > 0 ? 7 : 6;
        Emit($"xref\n0 {objCount}\n");
        Emit("0000000000 65535 f \n");
        for (int i = 1; i < objCount; i++)
            Emit(offsets.TryGetValue(i, out int off) ? $"{off:D10} 00000 n \n" : "0000000000 65535 f \n");
        Emit($"trailer\n<< /Size {objCount} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");

        return acc.ToArray();
    }
}
