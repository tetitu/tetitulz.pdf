// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Imaging;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Imaging;

/// <summary>
/// The BMP colour-table bound is computed in 64-bit arithmetic and capped at what the format can
/// index.
///
/// <para><b>Why this exists.</b> <c>biClrUsed</c> is read straight out of the file as a signed
/// <c>int</c> and was taken verbatim, then both the truncation check
/// (<c>paletteOffset + entries * 4 &gt; b.Length</c>) and the allocation
/// (<c>new byte[entries * 4]</c>) evaluated <c>entries * 4</c> in 32 bits. A declared entry count
/// near 2<sup>29</sup> wraps that product negative, so it passes a <c>&gt; b.Length</c> test that
/// was meant to stop it. The dimension checks a few lines above are deliberately widened —
/// <c>(long)w * h</c> — which is what makes this one an oversight rather than a house style.</para>
/// </summary>
public class BmpPaletteOverflowTests
{
    /// <summary>
    /// A colour table whose declared size overflows 32-bit arithmetic is refused with a reason,
    /// not tolerated into an allocation.
    /// </summary>
    [Fact]
    public void OversizedColourTable_IsRefusedWithAReason()
    {
        // 0x20000000 * 4 == 0x80000000, which is negative as a signed int.
        byte[] bmp = Bmp8Bpp(declaredPaletteEntries: 0x20000000);

        bool ok = BmpImageDecoder.TryDecode(bmp, out byte[] rgb, out _, out _, out string reason);

        Assert.False(ok);
        Assert.Null(rgb);
        Assert.False(string.IsNullOrEmpty(reason), "a refusal must say why");
    }

    /// <summary>
    /// An 8-bpp image cannot index more than 256 entries, so a larger declaration is malformed
    /// rather than merely large — and saying so is what stops the wrap from being reachable at all.
    /// </summary>
    [Fact]
    public void ColourTableLargerThanTheBitDepthCanIndex_IsRefused()
    {
        byte[] bmp = Bmp8Bpp(declaredPaletteEntries: 300);

        bool ok = BmpImageDecoder.TryDecode(bmp, out _, out _, out _, out string reason);

        Assert.False(ok);
        Assert.Contains("256", reason);
    }

    /// <summary>
    /// An ordinary 8-bpp BMP still decodes — the control, so the two refusals above cannot pass by
    /// the decoder having started rejecting every paletted image.
    /// </summary>
    [Fact]
    public void AnOrdinaryPalettedBmp_StillDecodes()
    {
        byte[] bmp = Bmp8Bpp(declaredPaletteEntries: 256, includeFullPalette: true);

        bool ok = BmpImageDecoder.TryDecode(bmp, out byte[] rgb, out int w, out int h, out string reason);

        Assert.True(ok, reason);
        Assert.Equal(1, w);
        Assert.Equal(1, h);
        Assert.Equal(3, rgb.Length);
    }

    // ------------------------------------------------------------------

    /// <summary>A 1x1 8-bpp BMP whose header declares <paramref name="declaredPaletteEntries"/>.</summary>
    private static byte[] Bmp8Bpp(int declaredPaletteEntries, bool includeFullPalette = false)
    {
        const int dibSize = 40;
        int paletteBytes  = includeFullPalette ? 256 * 4 : 4;
        int pixelOffset   = 14 + dibSize + paletteBytes;
        int rowBytes      = 4;                       // 1 px, padded to a 4-byte boundary
        var b = new byte[pixelOffset + rowBytes];

        b[0] = (byte)'B'; b[1] = (byte)'M';
        WriteLE32(b, 2, b.Length);                   // file size
        WriteLE32(b, 10, pixelOffset);               // pixel data offset
        WriteLE32(b, 14, dibSize);
        WriteLE32(b, 18, 1);                         // width
        WriteLE32(b, 22, 1);                         // height
        WriteLE16(b, 26, 1);                         // planes
        WriteLE16(b, 28, 8);                         // bits per pixel
        WriteLE32(b, 30, 0);                         // BI_RGB
        WriteLE32(b, 46, declaredPaletteEntries);    // biClrUsed

        // Palette entry 0 = white, so the control decodes to a known colour.
        int pal = 14 + dibSize;
        b[pal] = 255; b[pal + 1] = 255; b[pal + 2] = 255; b[pal + 3] = 0;

        return b;
    }

    private static void WriteLE16(byte[] b, int off, int v)
    {
        b[off] = (byte)(v & 0xFF); b[off + 1] = (byte)((v >> 8) & 0xFF);
    }

    private static void WriteLE32(byte[] b, int off, int v)
    {
        b[off] = (byte)(v & 0xFF);
        b[off + 1] = (byte)((v >> 8) & 0xFF);
        b[off + 2] = (byte)((v >> 16) & 0xFF);
        b[off + 3] = (byte)((v >> 24) & 0xFF);
    }
}
