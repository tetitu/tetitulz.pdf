// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.IO.Compression;
using System.Text;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

public class FilterTests
{
    [Fact]
    public void FlateDecodeFilter_DecompressesValidData()
    {
        byte[] original = Encoding.ASCII.GetBytes("Hello World! This is a test of Flate decompression.");

        // Compress with zlib format
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            // Write zlib header
            ms.WriteByte(0x78);
            ms.WriteByte(0x9C);
            using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(original, 0, original.Length);
            }
            compressed = ms.ToArray();
        }

        var filter = new FlateDecodeFilter();
        byte[] result = filter.Decode(compressed, null);

        Assert.Equal(original, result);
    }

    [Fact]
    public void AsciiHexDecodeFilter_DecodesValidHex()
    {
        byte[] input = Encoding.ASCII.GetBytes("48656C6C6F>");
        var filter = new AsciiHexDecodeFilter();
        byte[] result = filter.Decode(input, null);

        Assert.Equal("Hello", Encoding.ASCII.GetString(result));
    }

    [Fact]
    public void AsciiHexDecodeFilter_HandlesOddLength()
    {
        byte[] input = Encoding.ASCII.GetBytes("4>");
        var filter = new AsciiHexDecodeFilter();
        byte[] result = filter.Decode(input, null);

        Assert.Single(result);
        Assert.Equal(0x40, result[0]); // '4' padded with 0 -> 0x40
    }

    [Fact]
    public void AsciiHexDecodeFilter_SkipsWhitespace()
    {
        byte[] input = Encoding.ASCII.GetBytes("48 65 6C 6C 6F>");
        var filter = new AsciiHexDecodeFilter();
        byte[] result = filter.Decode(input, null);

        Assert.Equal("Hello", Encoding.ASCII.GetString(result));
    }

    [Fact]
    public void Ascii85DecodeFilter_DecodesBasicGroup()
    {
        // "Hello" in ASCII85 is "87cURD]j" (verified against standard encoding)
        // Actually let's use a known test vector:
        // "Man " encodes to "9jqo^" in ASCII85
        byte[] input = Encoding.ASCII.GetBytes("9jqo^~>");
        var filter = new Ascii85DecodeFilter();
        byte[] result = filter.Decode(input, null);

        Assert.Equal("Man ", Encoding.ASCII.GetString(result));
    }

    [Fact]
    public void Ascii85DecodeFilter_HandlesZShortcut()
    {
        // 'z' represents four zero bytes
        byte[] input = Encoding.ASCII.GetBytes("z~>");
        var filter = new Ascii85DecodeFilter();
        byte[] result = filter.Decode(input, null);

        Assert.Equal(4, result.Length);
        Assert.All(result, b => Assert.Equal(0, b));
    }

    /// <summary>
    /// The floor for the ceiling ASCII85 gained: a stream that uses <c>z</c> heavily but stays under
    /// the caller's limit must still decode, unchanged.
    /// </summary>
    /// <remarks>
    /// <see cref="Ascii85DecodeFilter"/> throws past the ceiling, so the refusal needs a floor.
    /// Without it the "refuse an oversized stream" test passes just as happily against a filter that
    /// refuses <i>every</i> stream.
    /// </remarks>
    [Fact]
    public void Ascii85DecodeFilter_ZRunUnderTheCeiling_StillDecodes()
    {
        byte[] input = Encoding.ASCII.GetBytes(new string('z', 1000) + "~>");

        var filter = new Ascii85DecodeFilter { MaxOutputBytes = 8 * 1024 };
        byte[] result = filter.Decode(input, null);

        Assert.Equal(4000, result.Length);
        Assert.All(result, b => Assert.Equal(0, b));
    }

    /// <summary>
    /// <c>z</c> is one character producing four bytes, so an all-<c>z</c> stream amplifies 4x and
    /// must be refused at the caller's ceiling rather than quietly overrunning it.
    /// </summary>
    [Fact]
    public void Ascii85DecodeFilter_OversizedZRun_IsRefusedAtTheCallersCeiling()
    {
        byte[] input = Encoding.ASCII.GetBytes(new string('z', 1000) + "~>");

        var filter = new Ascii85DecodeFilter { MaxOutputBytes = 512 };

        var ex = Assert.Throws<InvalidDataException>(() => filter.Decode(input, null));
        Assert.Contains("512", ex.Message);
    }

    [Fact]
    public void FilterPipeline_HandlesSingleFilter()
    {
        byte[] original = Encoding.ASCII.GetBytes("Test data");
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            ms.WriteByte(0x78);
            ms.WriteByte(0x9C);
            using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(original, 0, original.Length);
            }
            compressed = ms.ToArray();
        }

        var filterName = new PdfName("FlateDecode");
        byte[] result = FilterPipeline.Decode(compressed, filterName, null);
        Assert.Equal(original, result);
    }

    [Fact]
    public void FilterPipeline_HandlesFilterArray()
    {
        // First ASCII hex encode, then... well, just test the pipeline with a single filter in array form
        byte[] input = Encoding.ASCII.GetBytes("48656C6C6F>");
        var filterArray = new PdfArray();
        filterArray.Add(new PdfName("ASCIIHexDecode"));

        byte[] result = FilterPipeline.Decode(input, filterArray, null);
        Assert.Equal("Hello", Encoding.ASCII.GetString(result));
    }
}
