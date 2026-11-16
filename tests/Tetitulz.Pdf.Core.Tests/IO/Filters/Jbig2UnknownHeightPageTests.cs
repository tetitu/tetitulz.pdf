// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// A page whose height is unknown (0xFFFFFFFF), set by its end-of-stripe segments (T.88 7.4.8.2,
/// 7.4.10), and the page-association field whose size the segment flags choose.
/// </summary>
/// <remarks>
/// The fixture is <see cref="Jbig2EncodeFilter"/>'s stream for a known-height picture, changed in two
/// places: the page information's height becomes 0xFFFFFFFF and it is declared striped, and an
/// end-of-stripe segment naming the stripe's last row is appended. The picture is then the same, and
/// the expected output is the known-height decode of that same picture, trimmed to the rows the
/// stripes name.
/// </remarks>
public class Jbig2UnknownHeightPageTests
{
    private const int Width = 37;
    private const int Height = 19;
    private const int Stride = (Width + 7) / 8;

    [Fact]
    public void AnEndOfStripeRow_SetsTheHeightOfAPageOfUnknownHeight()
    {
        byte[] known = KnownPicture();
        byte[] expected = Jbig2Decoder.Decode(known, null, out _, out _);

        byte[] unknown = WithUnknownHeight(known, endOfStripeRow: Height - 1);
        byte[] actual = Jbig2Decoder.Decode(unknown, null, out _, out int height);

        Assert.Equal(Height, height);
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// A stripe that ends above the bottom of the region drawn on it: the page is as tall as the stripe,
    /// and the region's rows beyond it are clipped rather than extending the page.
    /// </summary>
    [Fact]
    public void AStripeEndingAboveTheRegion_TrimsThePageAndClipsTheRegion()
    {
        byte[] known = KnownPicture();
        byte[] expected = Jbig2Decoder.Decode(known, null, out _, out _);

        const int rows = Height / 2;
        byte[] unknown = WithUnknownHeight(known, endOfStripeRow: rows - 1);
        byte[] actual = Jbig2Decoder.Decode(unknown, null, out _, out int height);

        Assert.Equal(rows, height);
        Assert.Equal(expected.AsSpan(0, Stride * rows).ToArray(), actual);
    }

    /// <summary>
    /// The page-association field is four bytes when the segment flags' bit 6 says so. Bit 7 is the
    /// deferred flag, and it once selected this width instead, so every segment after a four-byte page
    /// association was read from the wrong offset.
    /// </summary>
    [Fact]
    public void AFourByteFlagsBit6_ReadsTheFourByteAssociation()
    {
        byte[] header =
        {
            0x00, 0x00, 0x00, 0x05,       // segment number 5
            0x70,                         // type 48 (page information), page association is 4 bytes (bit 6)
            0x00,                         // no referred-to segments
            0x00, 0x00, 0x00, 0x07,       // page association 7
            0x00, 0x00, 0x00, 0x13,       // data length 19
        };

        var segment = Jbig2Decoder.ReadSegmentHeader(new Jbig2Decoder.Jbig2BitReader(header));

        Assert.Equal(48, segment.SegmentType);
        Assert.Equal(7u, segment.PageAssociation);
        Assert.Equal(19u, segment.DataLength);
        Assert.Equal(header.Length, segment.HeaderLength);
    }

    // ---------------------------------------------------------------- fixture

    private static byte[] KnownPicture()
    {
        var bits = new byte[Stride * Height];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if ((x * 7 + y * 3) % 5 == 0) bits[y * Stride + x / 8] |= (byte)(0x80 >> (x % 8));
        return Jbig2EncodeFilter.Encode(bits, Width, Height, Stride);
    }

    /// <summary>
    /// The known-height stream with its page information's height set to unknown and an end-of-stripe
    /// segment for <paramref name="endOfStripeRow"/> appended.
    /// </summary>
    private static byte[] WithUnknownHeight(byte[] known, int endOfStripeRow)
    {
        var stream = new List<byte>(known);

        // The first segment is the page information: an 11-byte header (segment number, flags, referred
        // count, one-byte page association, data length), then width, height, resolutions, flags and
        // striping in its 19-byte body.
        const int pageInfoBody = 11;
        Assert.Equal(48, known[4] & 0x3F);
        for (int i = 0; i < 4; i++) stream[pageInfoBody + 4 + i] = 0xFF;   // height = 0xFFFFFFFF
        stream[pageInfoBody + 17] = 0x80 | 0x00;                             // striped
        stream[pageInfoBody + 18] = Height & 0xFF;                           // maximum stripe size

        // The end-of-stripe segment: segment number 1000, type 50, no references, one-byte page
        // association 1, four data bytes: the row the stripe ends on.
        stream.AddRange(new byte[]
        {
            0x00, 0x00, 0x03, 0xE8,
            0x32,
            0x00,
            0x01,
            0x00, 0x00, 0x00, 0x04,
            (byte)(endOfStripeRow >> 24), (byte)(endOfStripeRow >> 16), (byte)(endOfStripeRow >> 8), (byte)endOfStripeRow,
        });
        return stream.ToArray();
    }
}
