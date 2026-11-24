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
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// An ExtGState <c>/SMask</c> applies to images, in both renderers.
///
/// <para><b>Why this exists.</b> §11.6.4 applies the graphics-state soft mask to <b>every</b>
/// painting operation. Routing path fills, strokes, text and shadings through
/// <c>DrawWithOptionalSoftMask</c> but drawing images straight onto the canvas would render an image
/// under a <c>gs</c> soft mask completely unmasked — while the SVG exporter emits
/// <c>mask="url(#…)"</c> on its <c>&lt;image&gt;</c> elements. One file, two answers.</para>
///
/// <para>Not to be confused with an image's own <c>/SMask</c> entry, which is a different feature:
/// this is the mask set by <c>gs</c> on the graphics state.</para>
/// </summary>
public class ImageSoftMaskParityTests
{
    /// <summary>
    /// A luminosity soft mask that is black everywhere means alpha 0 everywhere, so a red image
    /// drawn under it must not reach the page.
    /// </summary>
    [Fact]
    public void RasterRender_AppliesAnExtGStateSoftMaskToAnImage()
    {
        byte[] pdf = MaskedImagePdf();

        using SKBitmap bmp = SKBitmap.Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72));
        SKColor centre = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);

        Assert.True(centre.Red > 200 && centre.Green > 200 && centre.Blue > 200,
            $"a fully-black luminosity soft mask must hide the image; got {centre}");
    }

    /// <summary>
    /// The same file through the SVG exporter, which already carried the mask reference. Pinned so
    /// the two stay in step rather than being fixed one at a time.
    /// </summary>
    [Fact]
    public void SvgExport_ReferencesTheSoftMaskOnTheImage()
    {
        byte[] pdf = MaskedImagePdf();

        var root = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0)).Root;
        bool anyMasked = root.Descendants()
            .Where(e => e.Name.LocalName is "image" or "use" or "g")
            .Any(e => e.Attribute("mask")?.Value.StartsWith("url(#") == true);

        Assert.True(anyMasked, "the SVG export should carry a mask reference on the image");
    }

    /// <summary>
    /// Without the <c>gs</c>, the same image paints — the control, so the test above cannot pass by
    /// the image having stopped drawing altogether.
    /// </summary>
    [Fact]
    public void RasterRender_WithoutTheSoftMask_StillDrawsTheImage()
    {
        byte[] pdf = MaskedImagePdf(applyGs: false);

        using SKBitmap bmp = SKBitmap.Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72));
        SKColor centre = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);

        Assert.True(centre.Red > 200 && centre.Green < 60 && centre.Blue < 60,
            $"the unmasked image should paint red; got {centre}");
    }

    // ------------------------------------------------------------------

    private static byte[] MaskedImagePdf(bool applyGs = true)
    {
        string content = applyGs
            ? "q /GS0 gs 72 0 0 72 0 0 cm /Img Do Q"
            : "q 72 0 0 72 0 0 cm /Img Do Q";
        const string maskForm = "0 g 0 0 72 72 re f";     // luminosity 0 over the whole page

        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void EmitBytes(byte[] raw) => acc.AddRange(raw);
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Contents 4 0 R "
            + "/Resources << /XObject << /Img 5 0 R >> /ExtGState << /GS0 6 0 R >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        // 5: a 1x1 solid red image.
        offsets.Add(acc.Count);
        Emit("5 0 obj\n<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB "
             + "/BitsPerComponent 8 /Length 3 >>\nstream\n");
        EmitBytes(new byte[] { 255, 0, 0 });
        Emit("\nendstream\nendobj\n");

        Obj("6 0 obj\n<< /Type /ExtGState /SMask << /S /Luminosity /G 7 0 R /BC [0] >> >>\nendobj\n");
        Obj($"7 0 obj\n<< /Type /XObject /Subtype /Form /Group << /S /Transparency /CS /DeviceGray >> "
            + $"/BBox [0 0 72 72] /Length {maskForm.Length} >>\nstream\n{maskForm}\nendstream\nendobj\n");

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
