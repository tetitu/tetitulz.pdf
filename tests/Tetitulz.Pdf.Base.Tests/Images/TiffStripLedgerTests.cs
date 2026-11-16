// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Base.Images;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Images;

/// <summary>
/// Decoding every page of one TIFF, a page reading bytes an earlier page's strips read is refused:
/// a file whose directories all name one small strip asked for every page's pixels from it — ten
/// thousand pages of 40 M pixels each over a few kilobytes.
/// </summary>
public sealed class TiffStripLedgerTests
{
    [Fact]
    public void AdversarialPagesSharingOneStrip_AreRefusedAfterTheFirst()
    {
        byte[] tiff = Pages(new[] { 0, 0, 0 }, stripBytes: 100);   // three directories, one strip
        var ledger = new TiffImageDecoder.StripLedger();

        Assert.True(TiffImageDecoder.TryDecodePage(tiff, 0, ledger, out _, out _, out _));
        Assert.False(TiffImageDecoder.TryDecodePage(tiff, 1, ledger, out _, out _, out _));
        Assert.Contains("an earlier page's strips read", TiffImageDecoder.LastError ?? "", StringComparison.Ordinal);
        Assert.False(TiffImageDecoder.TryDecodePage(tiff, 2, ledger, out _, out _, out _));
    }

    /// <summary>A page starting one byte into an earlier page's strip overlaps it, though no offset repeats.</summary>
    [Fact]
    public void AdversarialPageOffsetIntoAnEarlierStrip_IsRefused()
    {
        byte[] tiff = Pages(new[] { 0, 1 }, stripBytes: 100);
        var ledger = new TiffImageDecoder.StripLedger();

        Assert.True(TiffImageDecoder.TryDecodePage(tiff, 0, ledger, out _, out _, out _));
        Assert.False(TiffImageDecoder.TryDecodePage(tiff, 1, ledger, out _, out _, out _));
    }

    /// <summary>The controls: pages with their own data all decode under the ledger, and the public door, which keeps none, decodes a shared strip as before.</summary>
    [Fact]
    public void PagesWithTheirOwnData_AllDecode_AndTheLedgerlessDoorIsUnchanged()
    {
        var ledger = new TiffImageDecoder.StripLedger();
        byte[] own = Pages(new[] { 0, 100, 200 }, stripBytes: 100);
        for (int p = 0; p < 3; p++)
            Assert.True(TiffImageDecoder.TryDecodePage(own, p, ledger, out _, out _, out _));

        byte[] shared = Pages(new[] { 0, 0 }, stripBytes: 100);
        Assert.True(TiffImageDecoder.TryDecodePage(shared, 1, out _, out _, out _));
    }

    /// <summary>
    /// A little-endian TIFF of 10 × 10 8-bit grey pages, uncompressed, one strip each, the strip of
    /// page i at <c>dataStart + stripOffsets[i]</c> in a shared data block of 300 + 1 bytes.
    /// </summary>
    private static byte[] Pages(int[] stripOffsets, int stripBytes)
    {
        const int dataStart = 8, dataLength = 301;
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x49, 0x49, 42, 0 });
        int firstIfd = dataStart + dataLength + 1;   // word-aligned
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
                (278, 3, 1, 10), (279, 4, 1, stripBytes),
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
