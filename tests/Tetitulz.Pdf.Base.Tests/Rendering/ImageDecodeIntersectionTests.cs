// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Image features that are each covered on their own, asserted here in <b>combination</b>.
/// </summary>
/// <remarks>
/// Four defects in the text path this month shared one shape: two features were each well tested
/// while the code that runs when both are active had never executed. <c>Tz</c>+embedded-font,
/// <c>Tc</c>+substituted-font and <c>Tz</c>+second-<c>Tj</c> were all live bugs sitting behind two
/// green tests. This file asks the same question of the image path, where <c>/Decode</c>,
/// <c>/ImageMask</c>, <c>/Mask</c>, <c>/SMask</c> and <c>/Indexed</c> each have their own coverage
/// and their pairings do not.
/// <para>Every fixture is one image filling the whole page, so a wrong answer is a differently
/// coloured page rather than a subtly shifted pixel.</para>
/// </remarks>
public class ImageDecodeIntersectionTests
{
    private static byte[] BuildImagePdf(string imageDictBody, byte[] imageData,
        string contentPrefix = "", IEnumerable<(int num, string body, byte[] stream)> extraObjs = null)
    {
        var acc = new List<byte>();
        var off = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(int n, string dict, byte[] sd = null)
        {
            off[n] = acc.Count;
            Emit($"{n} 0 obj\n{dict}");
            if (sd != null) { Emit("\nstream\n"); acc.AddRange(sd); Emit("\nendstream"); }
            Emit("\nendobj\n");
        }
        Emit("%PDF-1.4\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
               "/Resources << /XObject << /Img 5 0 R >> >> /Contents 4 0 R >>");
        string cs = contentPrefix + "q 72 0 0 72 0 0 cm /Img Do Q";
        Obj(4, $"<< /Length {cs.Length} >>", Encoding.Latin1.GetBytes(cs));
        Obj(5, imageDictBody + $" /Length {imageData.Length} >>", imageData);
        int maxObj = 5;
        if (extraObjs != null)
            foreach (var (num, body, stream) in extraObjs) { Obj(num, body, stream); if (num > maxObj) maxObj = num; }

        int xref = acc.Count;
        Emit($"xref\n0 {maxObj + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= maxObj; i++)
            Emit(off.TryGetValue(i, out int o) ? $"{o:D10} 00000 n \n" : "0000000000 65535 f \n");
        Emit($"trailer\n<< /Size {maxObj + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }

    private static SKBitmap Render(byte[] pdf)
    {
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    /// <summary>
    /// §8.9.6.2: an image mask's <c>/Decode [1 0]</c> reverses which sample paints. Sample 1 paints
    /// with <c>[1 0]</c> and is left alone with the <c>[0 1]</c> default.
    /// </summary>
    [Fact]
    public void ImageMask_DecodeReversed_InvertsWhichSamplePaints()
    {
        // One pixel, one bit, set: 0x80. Fill colour is red.
        byte[] one = { 0x80 };
        const string baseDict = "<< /Type /XObject /Subtype /Image /Width 1 /Height 1" +
                                " /ImageMask true /BitsPerComponent 1";

        var painted = Render(BuildImagePdf(baseDict + " /Decode [1 0]", one, "1 0 0 rg "));
        var blank   = Render(BuildImagePdf(baseDict, one, "1 0 0 rg "));

        Assert.True(painted.GetPixel(36, 36).Red > 200 && painted.GetPixel(36, 36).Green < 60,
            "with /Decode [1 0] a set bit must paint the fill colour");
        Assert.True(blank.GetPixel(36, 36).Green > 200,
            "with the default /Decode a set bit must leave the page unchanged");
    }

    /// <summary>
    /// §8.9.5.2: for an <c>/Indexed</c> image <c>/Decode</c> maps samples onto the <b>index</b>
    /// range, so <c>[1 0]</c> over a two-entry palette swaps the colours outright.
    /// </summary>
    [Fact]
    public void IndexedImage_DecodeRemapsTheIndex_NotTheColourComponents()
    {
        byte[] sampleZero = { 0x00 };
        const string baseDict =
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8" +
            " /ColorSpace [/Indexed /DeviceRGB 1 <FF000000FF00>]";

        var plain    = Render(BuildImagePdf(baseDict, sampleZero));
        var remapped = Render(BuildImagePdf(baseDict + " /Decode [1 0]", sampleZero));

        Assert.True(plain.GetPixel(36, 36).Red > 200, "index 0 is the red palette entry");
        Assert.True(remapped.GetPixel(36, 36).Green > 200,
            "/Decode [1 0] must map sample 0 onto index 1 — the green entry");
    }

    /// <summary>
    /// A base image's <c>/Decode</c> must still apply when an <c>/SMask</c> is present: the two
    /// describe different samples and neither supersedes the other.
    /// </summary>
    [Fact]
    public void BaseImageDecode_StillAppliesWhenAnSMaskIsPresent()
    {
        byte[] white = { 0xFF };
        byte[] opaque = { 0xFF };
        const string smask = "<< /Type /XObject /Subtype /Image /Width 1 /Height 1" +
                             " /BitsPerComponent 8 /ColorSpace /DeviceGray";
        var extra = new List<(int, string, byte[])> { (6, smask + " /Length 1 >>", opaque) };

        // Gray 255 inverted by /Decode [1 0] is black; the opaque SMask must not undo that.
        var bmp = Render(BuildImagePdf(
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8" +
            " /ColorSpace /DeviceGray /Decode [1 0] /SMask 6 0 R", white, "", extra));

        Assert.True(bmp.GetPixel(36, 36).Red < 60,
            "/Decode [1 0] over sample 255 is black — an opaque /SMask must not restore white");
    }

    /// <summary>
    /// §8.9.6.4: a colour-key <c>/Mask</c> over an <c>/Indexed</c> image compares <b>palette
    /// indices</b>, not the RGB the palette resolves to.
    /// </summary>
    [Fact]
    public void ColorKeyMask_OverIndexedImage_ComparesIndicesNotColours()
    {
        byte[] sampleZero = { 0x00 };
        const string baseDict =
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8" +
            " /ColorSpace [/Indexed /DeviceRGB 1 <FF000000FF00>]";

        var masked = Render(BuildImagePdf(baseDict + " /Mask [0 0]", sampleZero));
        // The control: the SAME image with no /Mask must paint. Without it this test would pass on
        // a fixture that drew nothing at all — the failure mode that made four text-path defects
        // invisible this month. "Nothing was painted" is only evidence of masking if something
        // would otherwise have been.
        var unmasked = Render(BuildImagePdf(baseDict, sampleZero));

        var u = unmasked.GetPixel(36, 36);
        Assert.True(u.Red > 200 && u.Green < 60, $"the unmasked control must paint red, got {u}");

        var c = masked.GetPixel(36, 36);
        Assert.True(c.Red > 200 && c.Green > 200 && c.Blue > 200,
            $"masking index 0 must leave the page unpainted, got {c}");
    }
}
