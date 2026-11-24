// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Imaging;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Imaging;

/// <summary>
/// <see cref="BmpImageDecoder.TryReadLayout"/> answers "would this decode?" from the headers: every refusal the decode
/// makes is made there, before a pixel, so a converter that only asks (the Builder's <c>CanDecode</c>) decodes nothing
/// and its answer is the decode's.
/// </summary>
public class BmpLayoutTests
{
    /// <summary>A BMP of the given fields, a 40-byte info header, its pixel data <paramref name="pixelBytes"/> long.</summary>
    private static byte[] Bmp(int width, int height, int bpp = 24, int compression = 0, int planes = 1, int paletteCount = 0,
                              int pixelBytes = -1, int dibSize = 40)
    {
        int stride = ((width * Math.Max(1, bpp) + 31) / 32) * 4;
        int palette = bpp <= 8 ? (paletteCount > 0 ? paletteCount : 1 << Math.Max(1, bpp)) * 4 : 0;
        int offset = 54 + palette + (compression == 3 ? 12 : 0);
        int data = pixelBytes >= 0 ? pixelBytes : stride * Math.Abs(height);
        var b = new byte[Math.Min(offset + data, 64 * 1024 * 1024)];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BitConverter.GetBytes(b.Length).CopyTo(b, 2);
        BitConverter.GetBytes(offset).CopyTo(b, 10);
        BitConverter.GetBytes(dibSize).CopyTo(b, 14);
        BitConverter.GetBytes(width).CopyTo(b, 18);
        BitConverter.GetBytes(height).CopyTo(b, 22);
        BitConverter.GetBytes((short)planes).CopyTo(b, 26);
        BitConverter.GetBytes((short)bpp).CopyTo(b, 28);
        BitConverter.GetBytes(compression).CopyTo(b, 30);
        BitConverter.GetBytes(paletteCount).CopyTo(b, 46);
        return b;
    }

    public static TheoryData<string, byte[]> Variants() => new TheoryData<string, byte[]>
    {
        { "24-bit", Bmp(5, 3) },
        { "top-down 8-bit", Bmp(5, -3, bpp: 8) },
        { "1-bit", Bmp(9, 2, bpp: 1) },
        { "16-bit bit-fields", Bmp(4, 4, bpp: 16, compression: 3) },
        { "32-bit", Bmp(3, 3, bpp: 32) },
        { "truncated pixels", Bmp(100, 100, pixelBytes: 10) },
        { "RLE", Bmp(4, 4, bpp: 8, compression: 1) },
        { "12 bits a pixel", Bmp(4, 4, bpp: 12) },
        { "zero width", Bmp(0, 4) },
        { "int.MinValue height", Bmp(4, int.MinValue, pixelBytes: 0) },
        { "two planes", Bmp(4, 4, planes: 2) },
        { "palette past the depth", Bmp(4, 4, bpp: 4, paletteCount: 300) },
        { "core header", Bmp(4, 4, dibSize: 12) },
        { "past the pixel ceiling", Bmp(10_000, 10_000, pixelBytes: 0) },
        { "too short", new byte[20] },
    };

    /// <summary>The header read and the decode give the same answer and the same reason, for every refusal and the controls.</summary>
    [Theory]
    [MemberData(nameof(Variants))]
    public void TheHeaderRead_IsTheDecodesAnswer(string variant, byte[] bmp)
    {
        bool decodes = BmpImageDecoder.TryDecode(bmp, out _, out _, out _, out string decodeReason);
        bool reads = BmpImageDecoder.TryReadLayout(bmp, out var layout, out string layoutReason);

        Assert.True(decodes == reads, variant);
        Assert.Equal(decodeReason, layoutReason);
        if (reads) Assert.True(layout.Width > 0 && layout.Height > 0, variant);
    }

    /// <summary>Asking about a 4000 × 3000 BMP reads its headers and allocates next to nothing; its decode is 36 MB.</summary>
    [Fact]
    public void AskingAboutALargeBmp_DecodesNothing()
    {
        byte[] bmp = Bmp(4000, 3000);
        BmpImageDecoder.TryReadLayout(bmp, out _, out _);   // warm

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool reads = BmpImageDecoder.TryReadLayout(bmp, out var layout, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(reads);
        Assert.Equal((4000, 3000), (layout.Width, layout.Height));
        Assert.True(allocated < 4096, $"the header read allocated {allocated:N0} bytes");
    }
}
