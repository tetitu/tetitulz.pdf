// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Base.Tests.Rendering;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for gap 28.3 — Image /Decode value remapping for JPEG and raw-pixel images.
/// </summary>
public sealed class ImageDecodeArrayTests
{
    // ------------------------------------------------------------------
    //  Raw-pixel images with /Decode
    // ------------------------------------------------------------------

    [Fact]
    /// <summary>
    /// The name promises the identity <c>/Decode</c> renders <b>unchanged</b>, so compare it
    /// pixel-for-pixel against the same image with no <c>/Decode</c> at all. An inverted control
    /// proves the comparison can fail — without it, a renderer that ignored <c>/Decode</c>
    /// entirely would pass this test by definition.
    /// </summary>
    public void RawPixelImage_IdentityDecode_RendersUnchanged()
    {
        using var identity = PixelAssert.Render(BuildPdfWithRawImage(decode: "[0 1 0 1 0 1]"));
        using var none     = PixelAssert.Render(BuildPdfWithRawImage(decode: null));
        using var inverted = PixelAssert.Render(BuildPdfWithRawImage(decode: "[1 0 1 0 1 0]"));

        var box = PixelAssert.InkBounds(none);
        Assert.NotNull(box);
        var (l, t, r, b) = box!.Value;

        // Identity is a no-op...
        Assert.Equal(none.GetPixel(l + 2, t + 2), identity.GetPixel(l + 2, t + 2));
        Assert.Equal(none.GetPixel(r - 2, t + 2), identity.GetPixel(r - 2, t + 2));
        Assert.Equal(none.GetPixel(l + 2, b - 2), identity.GetPixel(l + 2, b - 2));

        // ...and inversion is not, so "unchanged" is a real claim.
        Assert.NotEqual(none.GetPixel(l + 2, t + 2), inverted.GetPixel(l + 2, t + 2));
    }

    /// <summary>
    /// <c>/Decode [1 0]</c> on a DeviceGray image maps every sample v → 1-v, so each of the four
    /// quadrants must come out as the photographic negative of the identity render at the same
    /// point. The fixture's 2x2 grey samples are the ramp 0, 64, 128, 192, so the inverted render
    /// is 255, 191, 127, 63 — black becomes white and nothing stays where it was.
    ///
    /// <para>Asserted per quadrant against the no-<c>/Decode</c> baseline rather than against
    /// literals, so the test still measures inversion and not the fixture's sample values.</para>
    /// </summary>
    [Fact]
    public void RawPixelImage_InvertedDecode_InvertsTheSampleValues()
    {
        using var inverted = PixelAssert.Render(BuildPdfWithRawImage(decode: "[1 0]", components: 1));
        using var identity = PixelAssert.Render(BuildPdfWithRawImage(decode: null,    components: 1));

        // The image is drawn over user-space (100,400)-(300,600); these are the four quadrant
        // centres, top row first (PDF image row 0 is the TOP row).
        (float x, float y)[] quadrants = [(150, 550), (250, 550), (150, 450), (250, 450)];

        foreach (var (x, y) in quadrants)
        {
            var src = PixelAssert.At(identity, x, y);
            var dst = PixelAssert.At(inverted, x, y);
            Assert.True(PixelAssert.Near(dst, new SKColor(
                    (byte)(255 - src.Red), (byte)(255 - src.Green), (byte)(255 - src.Blue)), 4),
                $"at ({x},{y}) /Decode [1 0] gave {dst}; the inverse of the identity render " +
                $"{src} is RGB({255 - src.Red},{255 - src.Green},{255 - src.Blue}).");
        }

        // The black sample really did become white, so "inverted" is not vacuously true for a
        // mid-grey image that happens to be near its own inverse.
        Assert.True(PixelAssert.IsWhite(PixelAssert.At(inverted, 150, 550)),
            "the 0-valued sample did not become white under /Decode [1 0]");
        Assert.True(PixelAssert.Near(PixelAssert.At(identity, 150, 550), SKColors.Black, 4),
            "the baseline's 0-valued sample was not black, so the fixture is wrong");
    }

    [Fact]
    /// <summary>
    /// The baseline: with no <c>/Decode</c> the sample values reach the page untouched. The
    /// fixture fills a 2x2 DeviceRGB image with the ramp 0, 64, 128, … so the first pixel is
    /// exactly RGB(0, 64, 128) — an assertion no "did not throw" check can make, and the anchor
    /// every other case in this file is compared against.
    /// </summary>
    public void RawPixelImage_NoDecode_RendersTheRawSampleValues()
    {
        using var bmp = PixelAssert.Render(BuildPdfWithRawImage(decode: null));

        var box = PixelAssert.InkBounds(bmp);
        Assert.NotNull(box);
        var (l, t, _, _) = box!.Value;

        var first = bmp.GetPixel(l + 2, t + 2);
        Assert.Equal(new SKColor(0, 64, 128), new SKColor(first.Red, first.Green, first.Blue));
    }

    /// <summary>
    /// <c>/Decode [0 0.5 …]</c> maps each component onto the lower half of its range, so every
    /// channel of every pixel must come out at half the value the identity render produced.
    /// Compared channel-by-channel against that baseline, which is what makes the claim
    /// "half-range" checkable rather than a restatement of the fixture.
    /// </summary>
    [Fact]
    public void RawPixelImage_HalfRangeDecode_HalvesEveryComponent()
    {
        using var halved   = PixelAssert.Render(BuildPdfWithRawImage(decode: "[0 0.5 0 0.5 0 0.5]"));
        using var identity = PixelAssert.Render(BuildPdfWithRawImage(decode: null));

        (float x, float y)[] quadrants = [(150, 550), (250, 550), (150, 450), (250, 450)];

        bool anyChanged = false;
        foreach (var (x, y) in quadrants)
        {
            var src = PixelAssert.At(identity, x, y);
            var dst = PixelAssert.At(halved, x, y);
            Assert.True(PixelAssert.Near(dst, new SKColor(
                    (byte)(src.Red / 2), (byte)(src.Green / 2), (byte)(src.Blue / 2)), 4),
                $"at ({x},{y}) /Decode [0 0.5 …] gave {dst}; half of the identity render " +
                $"{src} is RGB({src.Red / 2},{src.Green / 2},{src.Blue / 2}).");
            if (!PixelAssert.Near(dst, src, 8)) anyChanged = true;
        }

        Assert.True(anyChanged,
            "the half-range render is indistinguishable from the identity render, so /Decode " +
            "was ignored (the fixture's samples are not all zero).");
    }

    // ------------------------------------------------------------------
    //  CMYK JPEG — /Decode must NOT be applied to the already-RGB output
    // ------------------------------------------------------------------

    /// <summary>
    /// Regression: CMYK JPEG images (DeviceCMYK color space, /Decode [1 0 1 0 1 0 1 0])
    /// were rendered with inverted colors because <c>ApplyDecodeArrayToBitmap</c> was
    /// applied to the already-converted RGB pixels from SkiaSharp's internal JPEG
    /// decoder (which handles the Adobe APP14 CMYK inversion automatically).
    ///
    /// The fix: skip /Decode remapping for JPEG images whose color space has 4
    /// components (DeviceCMYK / ICCBased N=4).
    /// </summary>
    [Fact]
    public void CmykJpeg_WithInvertedDecodeArray_DoesNotDoubleInvertColors()
    {
        // Build a small JPEG with clearly non-neutral pixels so color inversion
        // is easy to detect.  We use SkiaSharp to encode RGB (SkiaSharp can't
        // encode CMYK JPEG), then tag it in the PDF dictionary as DeviceCMYK
        // with /Decode [1 0 1 0 1 0 1 0].  This is exactly the structure in
        // real-world bank statements (כאל) that triggered the bug.
        byte[] jpegBytes = BuildRedJpeg(4, 4);

        // Before fix: red (255,0,0) JPEG with decode[0..1]=[1,0] would render as
        // cyan (0,255,255) because R,G,B were inverted.
        byte[] pdf = BuildPdfWithCmykJpeg(jpegBytes, decode: "[1 0 1 0 1 0 1 0]");

        byte[] png;
        using (var ms = new MemoryStream(pdf))
            png = PdfPageRenderer.RenderPage(ms, 0, 72);

        Assert.NotNull(png);
        Assert.NotEmpty(png);

        // Decode the rendered PNG and sample its pixels.  At least some pixels
        // should be in the red half (R > 128) and not the cyan half (R < 128)
        // — confirming the CMYK /Decode array was NOT applied to the RGB output.
        using var rendered = SKBitmap.Decode(png);
        Assert.NotNull(rendered);

        int redDominant = 0, cyanDominant = 0;
        for (int y = 0; y < rendered.Height; y++)
        for (int x = 0; x < rendered.Width; x++)
        {
            var px = rendered.GetPixel(x, y);
            if (px.Red > 128 && px.Green < 64 && px.Blue < 64) redDominant++;
            if (px.Red < 64 && px.Green > 128 && px.Blue > 128) cyanDominant++;
        }
        Assert.True(redDominant > cyanDominant,
            $"Expected red-dominant pixels (r={redDominant}) > cyan-dominant (r={cyanDominant}); " +
            "CMYK JPEG /Decode inversion appears to have been applied twice.");
    }

    [Fact]
    public void RgbJpeg_WithInvertedDecodeArray_DoesInvertColors()
    {
        // Sanity-check: a DeviceRGB JPEG with /Decode [1 0 1 0 1 0] SHOULD be inverted,
        // confirming the decode path is still active for non-CMYK JPEGs.
        byte[] jpegBytes = BuildRedJpeg(4, 4);
        byte[] pdf = BuildPdfWithRgbJpeg(jpegBytes, decode: "[1 0 1 0 1 0]");

        byte[] png;
        using (var ms = new MemoryStream(pdf))
            png = PdfPageRenderer.RenderPage(ms, 0, 72);

        Assert.NotNull(png);
        using var rendered = SKBitmap.Decode(png);
        Assert.NotNull(rendered);

        int cyanDominant = 0, redDominant = 0;
        for (int y = 0; y < rendered.Height; y++)
        for (int x = 0; x < rendered.Width; x++)
        {
            var px = rendered.GetPixel(x, y);
            if (px.Red < 64 && px.Green > 128 && px.Blue > 128) cyanDominant++;
            if (px.Red > 128 && px.Green < 64 && px.Blue < 64) redDominant++;
        }
        Assert.True(cyanDominant > redDominant,
            $"DeviceRGB JPEG /Decode [1 0 1 0 1 0] should invert red→cyan: " +
            $"cyan={cyanDominant} red={redDominant}");
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static byte[] BuildRedJpeg(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            bmp.SetPixel(x, y, new SKColor(220, 30, 30));
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
    }

    private static byte[] BuildPdfWithCmykJpeg(byte[] jpegBytes, string decode)
        => BuildPdfWithJpeg(jpegBytes, colorSpace: "/DeviceCMYK", decode: decode);

    private static byte[] BuildPdfWithRgbJpeg(byte[] jpegBytes, string decode)
        => BuildPdfWithJpeg(jpegBytes, colorSpace: "/DeviceRGB", decode: decode);

    /// <summary>
    /// Builds a binary-safe PDF with an embedded JPEG image XObject.
    /// The PDF is assembled from three byte segments so the raw JPEG bytes
    /// are never passed through an ASCII encoder.
    /// </summary>
    private static byte[] BuildPdfWithJpeg(byte[] jpegBytes, string colorSpace, string decode)
    {
        string decodeEntry = string.IsNullOrEmpty(decode) ? "" : $" /Decode {decode}";
        const string pageStream = "q 200 0 0 200 100 400 cm /Im1 Do Q";

        // --- Pass 1: compute byte offsets ---
        // We build the text sections as ASCII byte arrays and measure their sizes
        // before assembling the final document (xref needs exact byte offsets).

        static byte[] A(string s) => Encoding.ASCII.GetBytes(s);

        byte[] seg0 = A("%PDF-1.4\n");
        byte[] seg1 = A("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        byte[] seg2 = A("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // obj 5 = image XObject (JPEG stream follows immediately)
        byte[] seg5hdr = A(
            $"5 0 obj\n<< /Type /XObject /Subtype /Image /Width 4 /Height 4" +
            $" /BitsPerComponent 8 /ColorSpace {colorSpace}{decodeEntry}" +
            $" /Filter /DCTDecode /Length {jpegBytes.Length} >>\nstream\n");
        // jpegBytes inserted here (binary)
        byte[] seg5ftr = A("\nendstream\nendobj\n");

        byte[] seg3 = A(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Resources << /XObject << /Im1 5 0 R >> >> >>\nendobj\n");

        byte[] seg4 = A(
            $"4 0 obj\n<< /Length {pageStream.Length} >>\nstream\n{pageStream}\nendstream\nendobj\n");

        int o1   = seg0.Length;
        int o2   = o1 + seg1.Length;
        int o5   = o2 + seg2.Length;
        int o3   = o5 + seg5hdr.Length + jpegBytes.Length + seg5ftr.Length;
        int o4   = o3 + seg3.Length;
        int xref = o4 + seg4.Length;

        byte[] segXref = A(
            $"xref\n0 6\n0000000000 65535 f \n" +
            $"{o1:D10} 00000 n \n" +
            $"{o2:D10} 00000 n \n" +
            $"{o3:D10} 00000 n \n" +
            $"{o4:D10} 00000 n \n" +
            $"{o5:D10} 00000 n \n" +
            $"trailer\n<< /Size 6 /Root 1 0 R >>\n" +
            $"startxref\n{xref}\n%%EOF\n");

        // --- Pass 2: concatenate ---
        using var result = new MemoryStream();
        result.Write(seg0);
        result.Write(seg1);
        result.Write(seg2);
        result.Write(seg5hdr);
        result.Write(jpegBytes);       // raw binary — safe in MemoryStream
        result.Write(seg5ftr);
        result.Write(seg3);
        result.Write(seg4);
        result.Write(segXref);
        return result.ToArray();
    }

    /// <summary>
    /// Builds a minimal PDF with a tiny raw-pixel image XObject.
    /// The image is 2×2 pixels, DeviceRGB (or DeviceGray if components==1).
    /// <paramref name="decode"/> is the literal string to use as the /Decode value,
    /// or null to omit the /Decode entry entirely.
    /// </summary>
    private static byte[] BuildPdfWithRawImage(string decode, int components = 3)
    {
        // Build 2×2 pixel data (raw, uncompressed).
        int pixelBytes = 2 * 2 * components;
        var imgData = new byte[pixelBytes];
        // Fill with a simple ramp so values are non-trivial.
        for (int i = 0; i < imgData.Length; i++) imgData[i] = (byte)(i * 64);

        string colorSpace = components == 1 ? "/DeviceGray" : "/DeviceRGB";
        string decodeEntry = decode != null ? $" /Decode {decode}" : "";

        // Encode image bytes as hex
        var hexSb = new StringBuilder();
        foreach (byte b in imgData) hexSb.AppendFormat("{0:X2}", b);
        hexSb.Append(">");
        string hexData = hexSb.ToString();

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // Image XObject (obj 5) — ASCIIHex encoded to keep it ASCII-safe
        int o5 = sb.Length;
        sb.AppendFormat(
            "5 0 obj\n<< /Type /XObject /Subtype /Image /Width 2 /Height 2" +
            " /ColorSpace {0} /BitsPerComponent 8{1}" +
            " /Filter /ASCIIHexDecode /Length {2} >>\nstream\n{3}\nendstream\nendobj\n",
            colorSpace, decodeEntry, hexData.Length, hexData);

        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Resources << /Font << /F1 6 0 R >>" +
            " /XObject << /Im1 5 0 R >> >> >>\nendobj\n");

        const string cs = "q 200 0 0 200 100 400 cm /Im1 Do Q";
        int o4 = sb.Length;
        sb.AppendFormat(
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);

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
