// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Base.Images;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Images;

/// <summary>
/// <see cref="TiffImageDecoder"/> — IFD parsing, strip assembly and the compressions TIFF shares
/// with PDF.
/// </summary>
/// <remarks>
/// <para><b>The fixtures are built byte by byte rather than checked in.</b> A TIFF is a header, a
/// set of 12-byte tag entries and a set of offsets, so building one is how a test says exactly
/// which tags it is about — and a checked-in file would carry a hundred tags this decoder does not
/// read, any of which could be the thing that made it pass.</para>
///
/// <para><b>Both byte orders are exercised</b>, because a TIFF declares its own endianness in the
/// first two bytes and a decoder that reads one and assumes the other produces plausible garbage:
/// a width of 256 read the wrong way round is 1, which fails, but a width of 257 is 257 either
/// way. The fixtures use asymmetric numbers for that reason.</para>
/// </remarks>
public class TiffImageDecoderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUncompressedGreyPageIsDecoded(bool bigEndian)
    {
        // 3x2, 8-bit grey, values chosen so a transposed read is a different picture.
        byte[] samples = { 0, 64, 128, 192, 255, 32 };
        byte[] tiff = Build(bigEndian, new[] { Page(3, 2, samples, compression: 1) });

        Assert.True(TiffImageDecoder.TryDecodePage(tiff, 0, out byte[] rgb, out int w, out int h),
            TiffImageDecoder.LastError);

        Assert.Equal(3, w);
        Assert.Equal(2, h);
        Assert.Equal(3 * 2 * 3, rgb.Length);

        for (int i = 0; i < samples.Length; i++)
        {
            Assert.Equal(samples[i], rgb[i * 3]);
            Assert.Equal(samples[i], rgb[(i * 3) + 1]);
            Assert.Equal(samples[i], rgb[(i * 3) + 2]);
        }
    }

    /// <summary>Every page of a multi-page file is reachable and they differ.</summary>
    /// <remarks>
    /// <b>Multi-page is the point of the decoder</b>, and "page 2 decoded" is satisfied by a
    /// decoder that ignored the index and returned page 1 — so the two pages carry different
    /// dimensions <i>and</i> different samples.
    /// </remarks>
    [Fact]
    public void EachPageOfAMultiPageFileIsReachable()
    {
        byte[] first = { 10, 20 };
        byte[] second = { 200, 210, 220, 230, 240, 250 };

        byte[] tiff = Build(false, new[]
        {
            Page(2, 1, first, compression: 1),
            Page(3, 2, second, compression: 1),
        });

        Assert.Equal(2, TiffImageDecoder.CountPages(tiff));

        Assert.True(TiffImageDecoder.TryDecodePage(tiff, 0, out byte[] rgb0, out int w0, out int h0),
            TiffImageDecoder.LastError);
        Assert.Equal(2, w0);
        Assert.Equal(1, h0);
        Assert.Equal(10, rgb0[0]);

        Assert.True(TiffImageDecoder.TryDecodePage(tiff, 1, out byte[] rgb1, out int w1, out int h1),
            TiffImageDecoder.LastError);
        Assert.Equal(3, w1);
        Assert.Equal(2, h1);
        Assert.Equal(200, rgb1[0]);
    }

    /// <summary>A page past the end of the chain is refused with a reason.</summary>
    [Fact]
    public void APageBeyondTheChainIsRefused()
    {
        byte[] tiff = Build(false, new[] { Page(2, 1, new byte[] { 1, 2 }, compression: 1) });

        Assert.False(TiffImageDecoder.TryDecodePage(tiff, 5, out _, out _, out _));
        Assert.Contains("chain ends", TiffImageDecoder.LastError, StringComparison.Ordinal);
    }

    /// <summary>PackBits and Deflate strips decode to the same pixels as an uncompressed one.</summary>
    /// <remarks>
    /// <b>Compared against the uncompressed page rather than against a literal</b>, so the
    /// comparison is about the compression alone: a decoder that got the geometry wrong would get
    /// it wrong in both and the test would still fail, because the uncompressed leg is asserted
    /// against its own literal elsewhere.
    /// </remarks>
    [Theory]
    [InlineData(32773)]     // PackBits
    [InlineData(8)]         // Deflate
    public void ACompressedStripDecodesToTheSamePixels(int compression)
    {
        byte[] samples = { 0, 64, 128, 192, 255, 32 };

        byte[] plain = Build(false, new[] { Page(3, 2, samples, compression: 1) });
        byte[] packed = Build(false, new[] { Page(3, 2, samples, compression) });

        Assert.True(TiffImageDecoder.TryDecodePage(plain, 0, out byte[] expected, out _, out _),
            TiffImageDecoder.LastError);
        Assert.True(TiffImageDecoder.TryDecodePage(packed, 0, out byte[] actual, out _, out _),
            TiffImageDecoder.LastError);

        Assert.Equal(expected, actual);
    }

    /// <summary>WhiteIsZero is inverted; BlackIsZero is not.</summary>
    /// <remarks>
    /// <b>Both halves, because either alone is satisfied by a decoder that ignores the tag.</b>
    /// Photometric 0 is the fax convention and the one a scanned page arrives in, so reading it as
    /// 1 produces a negative of every document this feature exists for.
    /// </remarks>
    [Fact]
    public void WhiteIsZeroIsInvertedAndBlackIsZeroIsNot()
    {
        byte[] samples = { 0, 255 };

        byte[] whiteIsZero = Build(false, new[]
            { Page(2, 1, samples, compression: 1, photometric: 0) });
        byte[] blackIsZero = Build(false, new[]
            { Page(2, 1, samples, compression: 1, photometric: 1) });

        Assert.True(TiffImageDecoder.TryDecodePage(whiteIsZero, 0, out byte[] inverted, out _, out _),
            TiffImageDecoder.LastError);
        Assert.True(TiffImageDecoder.TryDecodePage(blackIsZero, 0, out byte[] direct, out _, out _),
            TiffImageDecoder.LastError);

        Assert.Equal(255, inverted[0]);
        Assert.Equal(0, inverted[3]);
        Assert.Equal(0, direct[0]);
        Assert.Equal(255, direct[3]);
    }

    /// <summary>An RGB page keeps its channels in order.</summary>
    /// <remarks>
    /// The samples are all different, so a decoder that swapped two channels — or read the same
    /// one three times, which is what the grey path does — disagrees.
    /// </remarks>
    [Fact]
    public void AnRgbPageKeepsItsChannelOrder()
    {
        byte[] samples = { 10, 20, 30, 200, 100, 50 };
        byte[] tiff = Build(false, new[]
            { Page(2, 1, samples, compression: 1, photometric: 2, samplesPerPixel: 3) });

        Assert.True(TiffImageDecoder.TryDecodePage(tiff, 0, out byte[] rgb, out _, out _),
            TiffImageDecoder.LastError);

        Assert.Equal(new byte[] { 10, 20, 30, 200, 100, 50 }, rgb);
    }

    /// <summary>A palette page resolves through its colour map.</summary>
    /// <remarks>
    /// <b>TIFF's <c>/ColorMap</c> is three consecutive runs, not interleaved triples</b> — all the
    /// reds, then all the greens, then all the blues. A decoder reading it as triples produces a
    /// colour that is plausible and wrong, which is why the entries here are asymmetric.
    /// </remarks>
    [Fact]
    public void APalettePageResolvesThroughItsColourMap()
    {
        // Two entries: index 0 is (0x1100, 0x2200, 0x3300), index 1 is (0xAA00, 0xBB00, 0xCC00).
        var colourMap = new ushort[] { 0x1100, 0xAA00, 0x2200, 0xBB00, 0x3300, 0xCC00 };
        byte[] tiff = Build(false, new[]
            { Page(2, 1, new byte[] { 0, 1 }, compression: 1, photometric: 3, colourMap: colourMap) });

        Assert.True(TiffImageDecoder.TryDecodePage(tiff, 0, out byte[] rgb, out _, out _),
            TiffImageDecoder.LastError);

        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0xAA, 0xBB, 0xCC }, rgb);
    }

    /// <summary>An unsupported shape is refused by name rather than decoded as something else.</summary>
    /// <remarks>
    /// <b>Each row asserts its own substring</b>, so a refusal arriving from a different check
    /// cannot pass for the one under test — and the acceptance control is every other test in this
    /// file, which decode.
    /// </remarks>
    [Theory]
    [InlineData(7, "JPEG-compressed")]
    [InlineData(99, "not decoded")]
    public void AnUnsupportedCompressionIsRefusedByName(int compression, string expected)
    {
        byte[] tiff = Build(false, new[]
            { Page(2, 1, new byte[] { 1, 2 }, compression, rawStrip: true) });

        Assert.False(TiffImageDecoder.TryDecodePage(tiff, 0, out _, out _, out _));
        Assert.Contains(expected, TiffImageDecoder.LastError, StringComparison.Ordinal);
    }

    /// <summary>A circular IFD chain terminates rather than looping.</summary>
    /// <remarks>
    /// <b>An IFD names the next by offset, so a file can point one at itself.</b> A chain-length
    /// cap alone would walk it ten thousand times; the visited-offset set is what makes this
    /// return. Named <c>Cycle</c> so the non-throw is read as the behaviour under test.
    /// </remarks>
    [Fact]
    public void ACircularIfdChain_TerminatesRatherThanLooping()
    {
        byte[] tiff = Build(false, new[] { Page(2, 1, new byte[] { 1, 2 }, compression: 1) });

        // Point the first IFD's "next" field back at itself. The IFD's offset is in the header,
        // NOT at byte 8: the builder writes the strip data first, so assuming a fixed position
        // patches the middle of a strip and the file still parses.
        int ifd = FirstIfdOffset(tiff);
        int entries = tiff[ifd] | (tiff[ifd + 1] << 8);
        int nextField = ifd + 2 + (entries * 12);
        tiff[nextField] = (byte)ifd;
        tiff[nextField + 1] = 0;
        tiff[nextField + 2] = 0;
        tiff[nextField + 3] = 0;

        Assert.Equal(1, TiffImageDecoder.CountPages(tiff));
        Assert.False(TiffImageDecoder.TryDecodePage(tiff, 3, out _, out _, out _));
    }

    /// <summary>A strip offset outside the file is refused rather than read.</summary>
    [Fact]
    public void AStripPointingOutsideTheFile_IsRefused()
    {
        byte[] tiff = Build(false, new[] { Page(2, 1, new byte[] { 1, 2 }, compression: 1) });

        // Rewrite the StripOffsets value to a wild offset. It is the inline value of tag 273.
        int ifd = FirstIfdOffset(tiff);
        int entries = tiff[ifd] | (tiff[ifd + 1] << 8);
        for (int i = 0; i < entries; i++)
        {
            int at = ifd + 2 + (i * 12);
            int tag = tiff[at] | (tiff[at + 1] << 8);
            if (tag != 273) continue;

            tiff[at + 8] = 0xFF; tiff[at + 9] = 0xFF; tiff[at + 10] = 0; tiff[at + 11] = 0;
            break;
        }

        Assert.False(TiffImageDecoder.TryDecodePage(tiff, 0, out _, out _, out _));
        Assert.Contains("past the end", TiffImageDecoder.LastError, StringComparison.Ordinal);
    }

    /// <summary>Something that is not a TIFF at all is declined, not guessed at.</summary>
    [Fact]
    public void MalformedInput_IsDeclinedRatherThanGuessedAt()
    {
        Assert.False(TiffImageDecoder.IsTiff(null));
        Assert.False(TiffImageDecoder.IsTiff(new byte[] { 1, 2, 3 }));
        Assert.False(TiffImageDecoder.TryDecodePage(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' },
            0, out _, out _, out _));
        Assert.Equal(0, TiffImageDecoder.CountPages(new byte[] { 0x89, (byte)'P' }));
    }

    /// <summary>An image too large to materialise is refused before anything is allocated.</summary>
    /// <remarks>
    /// <b>The cap has to be checked before the raster is sized, not after.</b> A page declaring
    /// 40 000 x 40 000 is 1.6 billion pixels: allocating first and checking afterwards is the
    /// allocation the cap exists to prevent. Named <c>Oversized</c> so the guard reads the refusal
    /// as the behaviour under test.
    /// </remarks>
    [Fact]
    public void AnOversizedPage_IsRefusedBeforeAnythingIsAllocated()
    {
        byte[] tiff = Build(false, new[] { Page(2, 1, new byte[] { 1, 2 }, compression: 1) });

        // Rewrite the declared width and height. They are inline SHORT values, so the whole tag
        // fits its four-byte field.
        SetShortTag(tiff, 256, 40000);
        SetShortTag(tiff, 257, 40000);

        Assert.False(TiffImageDecoder.TryDecodePage(tiff, 0, out _, out _, out _));
        Assert.Contains("pixel cap", TiffImageDecoder.LastError, StringComparison.Ordinal);
    }

    /// <summary>A page split across several strips is reassembled in row order.</summary>
    /// <remarks>
    /// <b>A single-strip fixture cannot see the row arithmetic.</b> With one strip
    /// <c>min(rowsPerStrip, remaining)</c> is always <c>remaining</c>, so a decoder that ignored
    /// <c>RowsPerStrip</c> entirely would agree with one that honoured it. This is four rows in two
    /// strips, and the samples differ per row so a strip landing at the wrong offset is visible.
    /// </remarks>
    [Fact]
    public void APageSplitAcrossStripsIsReassembledInRowOrder()
    {
        // 2x4 grey. Row r is filled with (r+1)*40, so every row is distinguishable.
        var rows = new byte[4][];
        for (int r = 0; r < 4; r++) rows[r] = new byte[] { (byte)((r + 1) * 40), (byte)((r + 1) * 40) };

        byte[] tiff = BuildStripped(2, 4, rows, rowsPerStrip: 2);

        Assert.True(TiffImageDecoder.TryDecodePage(tiff, 0, out byte[] rgb, out int w, out int h),
            TiffImageDecoder.LastError);
        Assert.Equal(2, w);
        Assert.Equal(4, h);

        for (int r = 0; r < 4; r++)
        {
            int at = r * 2 * 3;
            Assert.Equal((byte)((r + 1) * 40), rgb[at]);
        }
    }

    /// <summary>Rewrites an inline SHORT tag's value in place.</summary>
    private static void SetShortTag(byte[] tiff, int tag, int value)
    {
        int ifd = FirstIfdOffset(tiff);
        int entries = tiff[ifd] | (tiff[ifd + 1] << 8);

        for (int i = 0; i < entries; i++)
        {
            int at = ifd + 2 + (i * 12);
            if ((tiff[at] | (tiff[at + 1] << 8)) != tag) continue;

            tiff[at + 8] = (byte)value;
            tiff[at + 9] = (byte)(value >> 8);
            tiff[at + 10] = 0;
            tiff[at + 11] = 0;
            return;
        }

        Assert.Fail($"tag {tag} is not in the fixture, so the test never reached what it names");
    }

    /// <summary>A single-page grey TIFF whose rows are split into strips.</summary>
    private static byte[] BuildStripped(int width, int height, byte[][] rows, int rowsPerStrip)
    {
        int strips = (height + rowsPerStrip - 1) / rowsPerStrip;

        var body = new MemoryStream();
        void W(byte[] b) => body.Write(b, 0, b.Length);
        byte[] U16(int v) => new[] { (byte)v, (byte)(v >> 8) };
        byte[] U32(int v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };

        W(new byte[] { 0x49, 0x49, 0x2A, 0x00 });
        W(U32(0));

        var stripAt = new int[strips];
        var stripLen = new int[strips];

        for (int s = 0; s < strips; s++)
        {
            stripAt[s] = (int)body.Length;
            for (int r = s * rowsPerStrip; r < Math.Min(height, (s + 1) * rowsPerStrip); r++)
                W(rows[r]);
            stripLen[s] = (int)body.Length - stripAt[s];
        }

        // The strip tables are arrays, so they live outside the four-byte field.
        int offsetsAt = (int)body.Length;
        foreach (int at in stripAt) W(U32(at));
        int countsAt = (int)body.Length;
        foreach (int len in stripLen) W(U32(len));

        int ifdAt = (int)body.Length;
        var tags = new (int Tag, int Type, int Count, int Value)[]
        {
            (256, 3, 1, width), (257, 3, 1, height), (258, 3, 1, 8), (259, 3, 1, 1),
            (262, 3, 1, 1), (273, 4, strips, offsetsAt), (277, 3, 1, 1),
            (278, 3, 1, rowsPerStrip), (279, 4, strips, countsAt), (284, 3, 1, 1),
        };

        W(U16(tags.Length));
        foreach ((int tag, int type, int count, int value) in tags)
        {
            W(U16(tag)); W(U16(type)); W(U32(count));

            // A single SHORT sits left-justified in the four-byte field; anything larger is an
            // offset to the data.
            W(type == 3 && count == 1
                ? new byte[] { (byte)value, (byte)(value >> 8), 0, 0 }
                : U32(value));
        }
        int nextAt = (int)body.Length;
        W(U32(0));

        byte[] file = body.ToArray();
        file[4] = (byte)ifdAt; file[5] = (byte)(ifdAt >> 8);
        file[6] = (byte)(ifdAt >> 16); file[7] = (byte)(ifdAt >> 24);
        return file;
    }

    // ================================================================ the fixture builder

    private sealed class PageSpec
    {
        internal int Width;
        internal int Height;
        internal byte[] Strip;              // already compressed
        internal int Compression;
        internal int Photometric;
        internal int SamplesPerPixel;
        internal ushort[] ColourMap;
    }

    private static PageSpec Page(int width, int height, byte[] samples, int compression,
                                 int photometric = 1, int samplesPerPixel = 1,
                                 ushort[] colourMap = null, bool rawStrip = false)
        => new PageSpec
        {
            Width = width,
            Height = height,
            Strip = rawStrip ? samples : Compress(samples, compression),
            Compression = compression,
            Photometric = photometric,
            SamplesPerPixel = samplesPerPixel,
            ColourMap = colourMap,
        };

    private static byte[] Compress(byte[] data, int compression)
    {
        switch (compression)
        {
            case 1:
                return data;

            case 32773:
            {
                // PackBits: literal runs only, which is the encoding a run-length coder falls back
                // to and the one this fixture needs to be unambiguous.
                var packed = new List<byte>();
                int i = 0;
                while (i < data.Length)
                {
                    int run = Math.Min(128, data.Length - i);
                    packed.Add((byte)(run - 1));
                    for (int j = 0; j < run; j++) packed.Add(data[i + j]);
                    i += run;
                }
                packed.Add(128);      // end of data
                return packed.ToArray();
            }

            case 8:
            {
                // Zlib-wrapped, which is what TIFF's Deflate means and what FlateDecodeFilter reads.
                using var ms = new MemoryStream();
                ms.WriteByte(0x78); ms.WriteByte(0x9C);
                using (var deflate = new DeflateStream(ms, CompressionMode.Compress, leaveOpen: true))
                    deflate.Write(data, 0, data.Length);

                uint a = 1, b = 0;
                foreach (byte v in data) { a = (a + v) % 65521; b = (b + a) % 65521; }
                uint adler = (b << 16) | a;
                ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16));
                ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
                return ms.ToArray();
            }

            default:
                return data;
        }
    }

    /// <summary>A TIFF holding the given pages, in the given byte order.</summary>
    private static byte[] Build(bool bigEndian, IReadOnlyList<PageSpec> pages)
    {
        // Layout: header, then for each page its strip bytes, its colour map, its IFD.
        var body = new MemoryStream();
        void W(byte[] b) => body.Write(b, 0, b.Length);

        W(bigEndian ? new byte[] { 0x4D, 0x4D, 0, 42 } : new byte[] { 0x49, 0x49, 42, 0 });
        W(U32(0, bigEndian));                              // first IFD offset, patched below

        var stripAt = new int[pages.Count];
        var mapAt = new int[pages.Count];

        for (int p = 0; p < pages.Count; p++)
        {
            stripAt[p] = (int)body.Length;
            W(pages[p].Strip);

            if (pages[p].ColourMap == null) continue;
            mapAt[p] = (int)body.Length;
            foreach (ushort v in pages[p].ColourMap) W(U16(v, bigEndian));
        }

        byte[] prefix = body.ToArray();
        var all = new MemoryStream();
        all.Write(prefix, 0, prefix.Length);

        var ifdAt = new int[pages.Count];
        var nextFieldAt = new int[pages.Count];

        for (int p = 0; p < pages.Count; p++)
        {
            PageSpec page = pages[p];
            ifdAt[p] = (int)all.Length;

            var tags = new List<(int Tag, int Type, int Count, int Value)>
            {
                (256, 3, 1, page.Width),
                (257, 3, 1, page.Height),
                (258, 3, 1, 8),
                (259, 3, 1, page.Compression),
                (262, 3, 1, page.Photometric),
                (273, 4, 1, stripAt[p]),
                (277, 3, 1, page.SamplesPerPixel),
                (278, 3, 1, page.Height),
                (279, 4, 1, page.Strip.Length),
                (284, 3, 1, 1),
            };

            if (page.ColourMap != null)
                tags.Add((320, 3, page.ColourMap.Length, mapAt[p]));

            tags.Sort((x, y) => x.Tag.CompareTo(y.Tag));

            var ifd = new MemoryStream();
            byte[] c = U16(tags.Count, bigEndian);
            ifd.Write(c, 0, c.Length);

            foreach ((int tag, int type, int count, int value) in tags)
            {
                byte[] t = U16(tag, bigEndian); ifd.Write(t, 0, t.Length);
                byte[] ty = U16(type, bigEndian); ifd.Write(ty, 0, ty.Length);
                byte[] n = U32(count, bigEndian); ifd.Write(n, 0, n.Length);

                // A SHORT that fits the four-byte field sits in its high half on a big-endian
                // file and its low half on a little-endian one -- the field is four bytes either
                // way and the value is left-justified.
                byte[] v = type == 3 && count == 1
                    ? (bigEndian
                        ? new byte[] { (byte)(value >> 8), (byte)value, 0, 0 }
                        : new byte[] { (byte)value, (byte)(value >> 8), 0, 0 })
                    : U32(value, bigEndian);
                ifd.Write(v, 0, v.Length);
            }

            byte[] ifdBytes = ifd.ToArray();
            all.Write(ifdBytes, 0, ifdBytes.Length);

            nextFieldAt[p] = (int)all.Length;
            byte[] next = U32(0, bigEndian);
            all.Write(next, 0, next.Length);
        }

        byte[] file = all.ToArray();

        Patch(file, 4, U32(ifdAt[0], bigEndian));
        for (int p = 0; p + 1 < pages.Count; p++)
            Patch(file, nextFieldAt[p], U32(ifdAt[p + 1], bigEndian));

        return file;
    }

    /// <summary>The first IFD's offset, from the header rather than assumed.</summary>
    private static int FirstIfdOffset(byte[] tiff)
        => tiff[4] | (tiff[5] << 8) | (tiff[6] << 16) | (tiff[7] << 24);

    private static void Patch(byte[] file, int at, byte[] value)
        => Buffer.BlockCopy(value, 0, file, at, value.Length);

    private static byte[] U16(int v, bool bigEndian) => bigEndian
        ? new[] { (byte)(v >> 8), (byte)v }
        : new[] { (byte)v, (byte)(v >> 8) };

    private static byte[] U32(int v, bool bigEndian) => bigEndian
        ? new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }
        : new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };
}
