// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Image-to-PDF decodes every page of a TIFF, and a page reading bytes an earlier page's strips read
/// is skipped and said: a file whose directories all name one strip asked for every page's pixels
/// from a few kilobytes.
/// </summary>
public sealed class ImageToPdfSharedTiffStripTests
{
    [Fact]
    public void AdversarialTiffOfPagesSharingOneStrip_WritesOnePageAndSaysSo()
    {
        var result = Convert(Pages(new[] { 0, 0, 0, 0, 0 }), out int pages);

        Assert.Equal(1, result.PagesWritten);
        Assert.Equal(1, pages);
        Assert.Equal(4, result.Warnings.Count(w => w.Contains("skipped", StringComparison.Ordinal)
                                                && w.Contains("an earlier page's strips read", StringComparison.Ordinal)));
    }

    /// <summary>The control: pages with their own data are every one written, with no warning.</summary>
    [Fact]
    public void ATiffOfPagesWithTheirOwnData_WritesEveryPage()
    {
        var result = Convert(Pages(new[] { 0, 100, 200 }), out int pages);

        Assert.Equal(3, result.PagesWritten);
        Assert.Equal(3, pages);
        Assert.Empty(result.Warnings);
    }

    private static ImageToPdfResult Convert(byte[] tiff, out int pages)
    {
        using var output = new MemoryStream();
        var result = PdfImageToPdf.Create(new[] { tiff }, output);
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        pages = doc.Pages.Count;
        return result;
    }

    /// <summary>A little-endian TIFF of 10 × 10 8-bit grey pages, uncompressed, page i's strip at offset 8 + <paramref name="stripOffsets"/>[i].</summary>
    private static byte[] Pages(int[] stripOffsets)
    {
        const int dataStart = 8, dataLength = 301;
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x49, 0x49, 42, 0 });
        int firstIfd = dataStart + dataLength + 1;
        ms.Write(BitConverter.GetBytes(firstIfd));
        var data = new byte[dataLength];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)i;
        ms.Write(data);
        ms.WriteByte(0);

        const int entries = 9, ifdSize = 2 + entries * 12 + 4;
        for (int page = 0; page < stripOffsets.Length; page++)
        {
            int next = page + 1 < stripOffsets.Length ? firstIfd + (page + 1) * ifdSize : 0;
            var tags = new List<(int Tag, int Type, int Count, int Value)>
            {
                (256, 3, 1, 10), (257, 3, 1, 10), (258, 3, 1, 8), (259, 3, 1, 1),
                (262, 3, 1, 1), (273, 4, 1, dataStart + stripOffsets[page]), (277, 3, 1, 1),
                (278, 3, 1, 10), (279, 4, 1, 100),
            };
            ms.Write(BitConverter.GetBytes((ushort)tags.Count));
            foreach (var (tag, type, count, value) in tags)
            {
                ms.Write(BitConverter.GetBytes((ushort)tag));
                ms.Write(BitConverter.GetBytes((ushort)type));
                ms.Write(BitConverter.GetBytes(count));
                ms.Write(BitConverter.GetBytes(value));
            }
            ms.Write(BitConverter.GetBytes(next));
        }
        return ms.ToArray();
    }
}
