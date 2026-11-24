// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Tests.Constants;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using H = Tetitulz.Pdf.TestHelpers.Jp2Headers;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// A JPX whose codestream is a Fragment Table (T.801 M.11.3: an <c>ftbl</c> holding an <c>flst</c>
/// of byte ranges) decodes as the contiguous file does; a fragment in another file or outside
/// this one is refused with the reason, and a list asking for more than the decompressed-bytes
/// ceiling stops at the ceiling.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public sealed class JpxFragmentTableTests
{
    private static JpxLosslessFixtures.Fixture Rgb => JpxLosslessFixtures.Rgb40x24_Mct_TwoLevels;

    [Fact]
    public void AFragmentedCodestream_DecodesLikeTheContiguousOne()
    {
        byte[] fragmented = Fragmented(Rgb.Codestream, 3);
        Assert.False(JpxHeader.TryLocateCodestream(fragmented, out _, out _));   // no jp2c: the three-argument form finds nothing
        Assert.True(JpxHeader.TryLocateCodestream(fragmented, out byte[] buffer, out long start, out long end, out string reason), reason);
        Assert.Equal((0L, (long)Rgb.Codestream.Length), (start, end));
        Assert.Equal(Rgb.Codestream, buffer);

        Assert.True(JpxHeader.TryReadImageSize(fragmented, out long w, out long h, out int components));
        Assert.Equal((40L, 24L, 3), (w, h, components));
        Assert.True(JpxPdfImage.TryDecode(fragmented, new Jp2PdfImageFacts(0, false, 0, false), null, 0, out JpxPdfImage image, out reason), reason);
        Assert.True(JpxPdfImage.TryDecode(H.Jp2File(Rgb.Codestream, H.Ihdr(24, 40, 3, 7), H.ColrEnumerated(16)), new Jp2PdfImageFacts(0, false, 0, false), null, 0, out JpxPdfImage contiguous, out reason), reason);
        Assert.Equal(contiguous.Colour, image.Colour);
        Assert.Equal(Interleaved(Rgb), image.Colour);
    }

    [Fact]
    public void AFragmentInAnotherFile_IsRefusedWithTheReason()
    {
        byte[] file = Fragmented(Rgb.Codestream, 2, dataReferenceOfSecond: 1);
        Assert.False(JpxHeader.TryLocateCodestream(file, out _, out _, out _, out string reason));
        Assert.Contains("fragment 1 of the codestream is in another file (data reference 1)", reason, StringComparison.Ordinal);
        Assert.False(JpxCodestreamParser.TryParse(file, out _, out string parserReason));
        Assert.Equal(reason, parserReason);
    }

    [Fact]
    public void AFragmentOutsideTheFile_IsRefusedWithTheReason()
    {
        byte[] file = Fragmented(Rgb.Codestream, 2, lengthOverrunOfSecond: 1000);
        Assert.False(JpxHeader.TryLocateCodestream(file, out _, out _, out _, out string reason));
        Assert.Contains("lies outside the", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A fragment list is a request to allocate: 65 535 fragments of 100 bytes, every one inside
    /// the file, ask for 6.5 MB, and the walk stops as soon as the running sum passes
    /// <see cref="PdfLimits.MaxDecompressedBytes"/>, before any copy. A fragment of 4 GB is
    /// refused earlier still, as outside the file.
    /// </summary>
    [Fact]
    public void AListAskingForMoreThanTheCeiling_StopsAtTheCeiling()
    {
        var flst = new List<byte> { 0xFF, 0xFF };
        for (int i = 0; i < 65535; i++)
        {
            flst.AddRange(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 });   // OFF 0
            flst.AddRange(new byte[] { 0, 0, 0, 100 });             // LEN 100
            flst.AddRange(new byte[] { 0, 0 });                     // DR 0
        }
        byte[] file = H.Container(Ftyp(), H.Jp2h(H.Ihdr(24, 40, 3, 7), H.ColrEnumerated(16)), H.Box("ftbl", H.Box("flst", flst.ToArray())));
        PdfLimits.MaxDecompressedBytes = 1L << 20;
        try
        {
            Assert.False(JpxHeader.TryLocateCodestream(file, out _, out _, out _, out string reason));
            Assert.Contains("asks for at least 1048600 bytes of codestream; PdfLimits.MaxDecompressedBytes is 1048576", reason, StringComparison.Ordinal);
        }
        finally { PdfLimits.ResetToDefaults(); }

        var huge = new List<byte> { 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0 };
        byte[] outside = H.Container(Ftyp(), H.Jp2h(H.Ihdr(24, 40, 3, 7), H.ColrEnumerated(16)), H.Box("ftbl", H.Box("flst", huge.ToArray())));
        Assert.False(JpxHeader.TryLocateCodestream(outside, out _, out _, out _, out string why));
        Assert.Contains("fragment 0 of the codestream (4294967295 byte(s) at offset 0) lies outside the", why, StringComparison.Ordinal);
    }

    /// <summary>The codestream in <paramref name="pieces"/> fragments, each in its own top-level box after the table; the list's offsets computed from the layout.</summary>
    private static byte[] Fragmented(byte[] codestream, int pieces, int dataReferenceOfSecond = 0, int lengthOverrunOfSecond = 0)
    {
        var lengths = new int[pieces];
        int each = codestream.Length / pieces;
        for (int i = 0; i < pieces; i++) lengths[i] = i == pieces - 1 ? codestream.Length - each * (pieces - 1) : each;

        byte[] prefix = H.Container(Ftyp(), H.Jp2h(H.Ihdr(24, 40, 3, 7), H.ColrEnumerated(16)));
        int tableBytes = 8 + 8 + 2 + 14 * pieces;
        var flst = new List<byte> { (byte)(pieces >> 8), (byte)pieces };
        long offset = prefix.Length + tableBytes;
        for (int i = 0; i < pieces; i++)
        {
            long contentOffset = offset + 8;
            int length = lengths[i] + (i == 1 ? lengthOverrunOfSecond : 0);
            flst.AddRange(new byte[] { 0, 0, 0, 0, (byte)(contentOffset >> 24), (byte)(contentOffset >> 16), (byte)(contentOffset >> 8), (byte)contentOffset });
            flst.AddRange(new byte[] { (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length });
            int dr = i == 1 ? dataReferenceOfSecond : 0;
            flst.AddRange(new byte[] { (byte)(dr >> 8), (byte)dr });
            offset += 8 + lengths[i];
        }
        var parts = new List<byte[]> { prefix, H.Box("ftbl", H.Box("flst", flst.ToArray())) };
        int at = 0;
        for (int i = 0; i < pieces; i++)
        {
            var piece = new byte[lengths[i]];
            Array.Copy(codestream, at, piece, 0, lengths[i]);
            parts.Add(H.Box("jpfr", piece));   // any box a reader skips
            at += lengths[i];
        }
        return H.Concat(parts.ToArray());
    }

    private static byte[] Ftyp() => H.Box("ftyp", H.Concat(
        new[] { (byte)'j', (byte)'p', (byte)'x', (byte)' ' }, new byte[] { 0, 0, 0, 0 },
        new[] { (byte)'j', (byte)'p', (byte)'2', (byte)' ' }, new[] { (byte)'j', (byte)'p', (byte)'x', (byte)'b' }));

    private static byte[] Interleaved(JpxLosslessFixtures.Fixture f)
    {
        var dst = new byte[f.Width * f.Height * 3];
        for (int i = 0; i < f.Width * f.Height; i++)
            for (int k = 0; k < 3; k++) dst[i * 3 + k] = f.Planes[k][i];
        return dst;
    }
}
