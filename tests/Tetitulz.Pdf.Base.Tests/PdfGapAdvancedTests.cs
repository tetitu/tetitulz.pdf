// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Base.Tests.Rendering;
using SkiaSharp;
using System.Text;
using Tetitulz.Pdf.Base;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for the remaining gap implementations:
///   GAP 21.3 — Mesh shading smoke test (render does not throw)
///   GAP 21.4 — Function-based shading smoke test
///   GAP 21.8 — ICCBased color space component count
///   GAP 22.3 — Vertical CJK WMode detection (smoke test)
///   GAP 25.1 — Crypt filter in FilterPipeline (pass-through)
/// </summary>
public sealed class PdfGapAdvancedTests
{
    // ====================================================================
    //  GAP 21.3 — Mesh shading: rendering a PDF with a type-4 shading
    //  stream should not throw (best-effort rasterization).
    // ====================================================================

    [Fact]
    /// <summary>
    /// An axial (type 2) shading must produce an actual <b>gradient</b>: the two ends of the axis
    /// differ. "Did not throw" is satisfied by a shading dispatch that painted one flat colour, or
    /// nothing at all.
    /// </summary>
    public void RenderPage_WithAxialShading_PaintsAGradientAlongTheAxis()
    {
        using var bmp = PixelAssert.Render(BuildPdfWithAxialShading());

        var box = PixelAssert.InkBounds(bmp);
        Assert.NotNull(box);
        var (l, t, r, b) = box!.Value;
        int mid = (t + b) / 2;

        var left  = bmp.GetPixel(l + 2, mid);
        var right = bmp.GetPixel(r - 2, mid);

        // Same hue, different intensity — that is what makes it a gradient rather than a fill.
        Assert.True(right.Red > left.Red + 40,
            $"expected the axis to brighten from left to right, got {left} -> {right}");
        Assert.Equal(left.Green, right.Green);
        Assert.Equal(left.Blue, right.Blue);
    }

    // ====================================================================
    //  GAP 21.8 — ICCBased color space: /N correctly maps to components.
    // ====================================================================

    [Fact]
    /// <summary>
    /// An <c>[/ICCBased &lt;stream&gt;]</c> colour space with <c>/N 3</c> must map to DeviceRGB, so
    /// the image's samples reach the page as the colours they encode. Reading two corners proves
    /// the mapping happened; "did not throw" would also hold if the image were dropped or drawn as
    /// grey.
    /// </summary>
    public void RenderPage_WithIccBasedImage_MapsNThreeToDeviceRgb()
    {
        using var bmp = PixelAssert.Render(BuildPdfWithIccBasedImage());

        var box = PixelAssert.InkBounds(bmp);
        Assert.NotNull(box);
        var (l, t, r, b) = box!.Value;

        Assert.True(PixelAssert.Near(bmp.GetPixel(l + 2, t + 2), new SKColor(255, 0, 0)),
            $"top-left should be red, got {bmp.GetPixel(l + 2, t + 2)}");
        Assert.True(PixelAssert.Near(bmp.GetPixel(r - 2, b - 2), new SKColor(255, 255, 0)),
            $"bottom-right should be yellow, got {bmp.GetPixel(r - 2, b - 2)}");
    }

    // ====================================================================
    //  GAP 25.1 — Crypt filter: FilterPipeline must not throw on /Crypt.
    // ====================================================================

    [Fact]
    public void FilterPipeline_CryptFilter_PassesThrough()
    {
        // Simulate a stream whose /Filter includes /Crypt with /Identity parms.
        // The FilterPipeline should return the data unchanged rather than throwing.
        byte[] input = Encoding.ASCII.GetBytes("Hello Crypt");
        var filterObj  = new Tetitulz.Pdf.Core.Objects.PdfName("Crypt");
        var parms      = new Tetitulz.Pdf.Core.Objects.PdfDictionary();
        parms[new Tetitulz.Pdf.Core.Objects.PdfName("Name")] =
            new Tetitulz.Pdf.Core.Objects.PdfName("Identity");

        byte[] output = Tetitulz.Pdf.Core.IO.Filters.FilterPipeline.Decode(input, filterObj, parms);
        Assert.Equal(input, output);
    }

    [Fact]
    public void FilterPipeline_CryptFilter_NullParms_PassesThrough()
    {
        byte[] input = Encoding.ASCII.GetBytes("crypt passthrough");
        var filterObj = new Tetitulz.Pdf.Core.Objects.PdfName("Crypt");
        byte[] output = Tetitulz.Pdf.Core.IO.Filters.FilterPipeline.Decode(input, filterObj, null);
        Assert.Equal(input, output);
    }

    // ====================================================================
    //  Helpers
    // ====================================================================

    /// <summary>Builds a minimal PDF with an axial shading (/sh operator).</summary>
    private static byte[] BuildPdfWithAxialShading()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        // Content stream: paint an axial shading named Sh1.
        string cs = "q /Sh1 sh Q";
        var offsets = new int[7];

        offsets[0] = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[1] = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets[2] = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                  "/Contents 4 0 R " +
                  "/Resources << /Shading << /Sh1 5 0 R >> >> >>\nendobj\n");

        offsets[3] = sb.Length;
        sb.Append($"4 0 obj\n<< /Length {cs.Length} >>\nstream\n{cs}\nendstream\nendobj\n");

        // Axial shading dict.
        offsets[4] = sb.Length;
        sb.Append("5 0 obj\n<< /ShadingType 2 /ColorSpace /DeviceRGB " +
                  "/Coords [0 0 200 200] /Function 6 0 R >>\nendobj\n");

        // Type 2 (exponential) function: black→red.
        offsets[5] = sb.Length;
        sb.Append("6 0 obj\n<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0] /C1 [1 0 0] /N 1 >>\nendobj\n");

        // Dummy obj 7 for xref alignment.
        offsets[6] = sb.Length;
        sb.Append("7 0 obj\n<< >>\nendobj\n");

        int xrefOff = sb.Length;
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xrefOff}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Builds a PDF with a 2×2 RGB image declared as ICCBased /N 3.</summary>
    private static byte[] BuildPdfWithIccBasedImage()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        // Tiny ICC profile stub: just enough bytes for a plausible stream header.
        // Real parsing only reads /N; the actual ICC bytes are ignored at this level.
        byte[] iccBytes = new byte[132]; // minimal size; content irrelevant for fallback
        iccBytes[36] = 0x52; iccBytes[37] = 0x47; iccBytes[38] = 0x42; iccBytes[39] = 0x20; // "RGB "
        string iccHex = BitConverter.ToString(iccBytes).Replace("-", "");

        // 2×2 raw RGB image (12 bytes).
        byte[] pixels = {
            255, 0, 0,  0, 255, 0,
            0, 0, 255,  255, 255, 0
        };
        string pixHex = BitConverter.ToString(pixels).Replace("-", "");

        var offsets = new int[7];

        offsets[0] = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[1] = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // Page: content uses Do to paint the image.
        string cs = "q 100 0 0 100 50 50 cm /Im1 Do Q";
        offsets[2] = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                  "/Contents 4 0 R " +
                  "/Resources << /XObject << /Im1 5 0 R >> >> >>\nendobj\n");

        offsets[3] = sb.Length;
        sb.Append($"4 0 obj\n<< /Length {cs.Length} >>\nstream\n{cs}\nendstream\nendobj\n");

        // Image XObject: ICCBased color space referencing object 7.
        offsets[4] = sb.Length;
        sb.Append($"5 0 obj\n<< /Type /XObject /Subtype /Image " +
                  $"/Width 2 /Height 2 /BitsPerComponent 8 " +
                  $"/ColorSpace [/ICCBased 6 0 R] /Filter /ASCIIHexDecode " +
                  $"/Length {pixHex.Length + 1} >>\n" +
                  $"stream\n{pixHex}>\nendstream\nendobj\n");

        // ICC profile stream with /N 3 (RGB).
        offsets[5] = sb.Length;
        sb.Append($"6 0 obj\n<< /N 3 /Filter /ASCIIHexDecode /Length {iccHex.Length + 1} >>\n" +
                  $"stream\n{iccHex}>\nendstream\nendobj\n");

        // Dummy obj 7.
        offsets[6] = sb.Length;
        sb.Append("7 0 obj\n<< >>\nendobj\n");

        int xrefOff = sb.Length;
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xrefOff}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
