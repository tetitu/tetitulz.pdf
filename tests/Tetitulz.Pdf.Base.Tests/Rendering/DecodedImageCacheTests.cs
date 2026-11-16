// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text;
using SkiaSharp;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The decoded-image cache on <c>PdfRenderContext</c>: an image XObject drawn N times on a page
/// is decoded once, not N times.
/// <para>
/// The cache is keyed on the stream instance, but the pipeline it memoises is <b>not</b> purely a
/// function of the stream: <c>ApplyTransferFunction</c> reads the graphics state's <c>/TR</c>.
/// Caching across a change of transfer function would serve a stale bitmap and paint the second
/// draw with the first draw's colours — visually plausible, completely wrong, and invisible to
/// any test that draws the image only once. That is what these pin.
/// </para>
/// </summary>
public class DecodedImageCacheTests
{
    private const int Dpi = 72;   // 1 pt == 1 px

    /// <summary>
    /// The same image drawn twice, the second time under an inverting <c>/TR</c>. The two draws
    /// must differ. If the cache ignored the transfer function the second would reuse the first
    /// bitmap and both would be identical.
    /// </summary>
    [Fact]
    public void SameImage_DrawnAgainUnderATransferFunction_IsNotServedFromCache()
    {
        using SKBitmap bmp = Render(TwoDrawsPdf(secondDrawInverted: true));

        // Left draw: plain. Right draw: through /TR that maps v -> 1-v.
        SKColor plain = bmp.GetPixel(30, 100);
        SKColor inverted = bmp.GetPixel(130, 100);

        // The source image is mid-grey (128); inverted it is ~127 — too close to tell apart.
        // The fixture uses a dark image instead, so inversion is unmistakable.
        Assert.True(plain.Red < 100, $"first draw should be dark, was {plain}");
        Assert.True(inverted.Red > 150,
            $"second draw should be inverted by /TR, was {inverted} — a stale cached bitmap would match the first draw");
    }

    /// <summary>
    /// Two plain draws of the same image are identical. This is the cache's actual purpose, and
    /// it is the control for the test above: it shows the two sample points agree when nothing
    /// should change them, so the difference asserted above is the transfer function and not the
    /// positions.
    /// </summary>
    [Fact]
    public void SameImage_DrawnTwicePlainly_RendersIdenticalPixels()
    {
        using SKBitmap bmp = Render(TwoDrawsPdf(secondDrawInverted: false));

        SKColor first = bmp.GetPixel(30, 100);
        SKColor second = bmp.GetPixel(130, 100);

        Assert.Equal(first, second);
        Assert.True(first.Red < 100, $"both draws should carry the dark image, was {first}");
    }

    private static SKBitmap Render(byte[] pdf)
    {
        using var ms = new System.IO.MemoryStream(pdf);
        byte[] png = PdfOperations.RenderPageToPng(ms, 0, dpi: Dpi);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>
    /// A 200x200 page drawing one 2x2 dark-grey DeviceGray image twice: once at the left, once
    /// at the right. When <paramref name="secondDrawInverted"/> the second draw runs under an
    /// ExtGState whose <c>/TR</c> is the type-2 function mapping v to 1-v.
    /// </summary>
    private static byte[] TwoDrawsPdf(bool secondDrawInverted)
    {
        var acc = new List<byte>();
        var offsets = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void EmitBytes(byte[] b) => acc.AddRange(b);
        void BeginObj(int n) { offsets[n] = acc.Count; Emit($"{n} 0 obj\n"); }
        void EndObj() => Emit("endobj\n");

        // 2x2, all samples 0x20 — dark, so inverting it is unmistakable.
        byte[] samples = { 0x20, 0x20, 0x20, 0x20 };

        string second = secondDrawInverted
            ? "q /GI gs 1 0 0 1 100 0 cm 80 0 0 80 20 60 cm /Im0 Do Q\n"
            : "q 1 0 0 1 100 0 cm 80 0 0 80 20 60 cm /Im0 Do Q\n";
        string cs = "q 80 0 0 80 20 60 cm /Im0 Do Q\n" + second;

        Emit("%PDF-1.7\n");
        BeginObj(1); Emit("<< /Type /Catalog /Pages 2 0 R >>\n"); EndObj();
        BeginObj(2); Emit("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\n"); EndObj();

        BeginObj(3);
        Emit("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200]\n" +
             "   /Resources << /XObject << /Im0 5 0 R >> /ExtGState << /GI 6 0 R >> >>\n" +
             "   /Contents 4 0 R >>\n");
        EndObj();

        BeginObj(4);
        Emit($"<< /Length {Encoding.Latin1.GetByteCount(cs)} >>\nstream\n{cs}\nendstream\n");
        EndObj();

        BeginObj(5);
        Emit("<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray\n" +
             $"   /BitsPerComponent 8 /Length {samples.Length} >>\nstream\n");
        EmitBytes(samples);
        Emit("\nendstream\n");
        EndObj();

        // /TR as a type-2 exponential mapping 0 -> 1 and 1 -> 0.
        BeginObj(6);
        Emit("<< /Type /ExtGState /TR 7 0 R >>\n");
        EndObj();
        BeginObj(7);
        Emit("<< /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>\n");
        EndObj();

        int xrefOffset = acc.Count;
        const int objCount = 8;
        Emit($"xref\n0 {objCount}\n0000000000 65535 f \n");
        for (int i = 1; i < objCount; i++)
            Emit(offsets.TryGetValue(i, out int off) ? $"{off:D10} 00000 n \n" : "0000000000 65535 f \n");
        Emit($"trailer\n<< /Size {objCount} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");

        return acc.ToArray();
    }
}
