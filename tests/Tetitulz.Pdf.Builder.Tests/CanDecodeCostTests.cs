// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Builder.Export;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="BuilderImageDecoder.CanDecode"/> answers for a BMP from its headers: the HTML mapper asks it before the
/// export decodes the picture, and asked by a whole decode, a 4000 × 3000 BMP was decoded twice.
/// </summary>
public class CanDecodeCostTests
{
    private static byte[] Bmp24(int width, int height, int pixelBytes)
    {
        var b = new byte[54 + pixelBytes];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BitConverter.GetBytes(b.Length).CopyTo(b, 2);
        BitConverter.GetBytes(54).CopyTo(b, 10);
        BitConverter.GetBytes(40).CopyTo(b, 14);
        BitConverter.GetBytes(width).CopyTo(b, 18);
        BitConverter.GetBytes(height).CopyTo(b, 22);
        BitConverter.GetBytes((short)1).CopyTo(b, 26);
        BitConverter.GetBytes((short)24).CopyTo(b, 28);
        return b;
    }

    /// <summary>A large BMP is answered with next to no allocation; a truncated one is still refused with the decode's reason.</summary>
    [Fact]
    public void ALargeBmp_IsAnsweredWithoutADecode()
    {
        byte[] bmp = Bmp24(4000, 3000, 12_000 * 3000);
        BuilderImageDecoder.CanDecode(bmp, out _);   // warm

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool can = BuilderImageDecoder.CanDecode(bmp, out string? reason);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(can, reason);
        Assert.True(allocated < 64 * 1024, $"CanDecode allocated {allocated:N0} bytes");

        Assert.False(BuilderImageDecoder.CanDecode(Bmp24(4000, 3000, 100), out string? truncated));
        Assert.Contains("truncated", truncated, StringComparison.Ordinal);
    }
}
