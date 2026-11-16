// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The PNG decoder behind <c>AddImage</c>, asserted on <b>decoded pixels</b>.
///
/// <para><b>Why pixels.</b> "an image XObject appeared in the output" is satisfied by a stream of
/// the right length holding the wrong samples, and an interlaced PNG that decodes to nothing at all
/// simply produces no XObject while the export still succeeds. Every case here inflates the
/// <c>FlateDecode</c> data the exporter would embed and reads individual RGB triples out of it, so
/// each one fails if the sample at a named coordinate moves.</para>
///
/// <para>The fixture is deliberately <b>32x32</b>. An Adam7 image's raw stream carries ~1.875 filter
/// bytes per image row instead of 1, so it is always larger than the sequential
/// <c>(stride+1)*height</c> figure the inflate cap was computed from — but only by enough to breach
/// the cap once the image is taller than roughly 18 rows. An 8x8 interlaced fixture (207 bytes
/// against a 216-byte cap) passes with the interlaced decoder deleted.</para>
/// </summary>
public class PngDecodeTests
{
    // Both colour channels vary with BOTH axes, so a pixel that lands in the wrong place — the
    // failure mode an Adam7 scatter bug produces — cannot coincidentally match.
    private static byte ExpectedR(int x, int y) => (byte)(8 * x + 3);
    private static byte ExpectedG(int x, int y) => (byte)(8 * y + 5);
    private static byte ExpectedB(int x, int y) => (byte)((x * 5 + y * 11) & 0xFF);

    // ---------------------------------------------------------------- Adam7

    /// <summary>
    /// A 32x32 interlaced PNG decodes to the same pixels as the identical sequential one.
    /// </summary>
    /// <remarks>
    /// The inflate cap was computed from the sequential raw size for every PNG, so
    /// <c>ReadCapped</c> threw for any interlaced image past ~18 rows, <c>catch { return null; }</c>
    /// swallowed it, and <c>DecodeAdam7</c> was unreachable. Asserting equality against the
    /// sequential decode of the same source image pins both halves at once: the cap must admit the
    /// stream, and the seven-pass scatter must put every sample back where it started.
    /// </remarks>
    [Fact]
    public void Adam7Png_DecodesToTheSamePixelsAsTheSequentialPng()
    {
        byte[] interlaced = MakePng(32, 32, interlace: true);
        byte[] sequential = MakePng(32, 32, interlace: false);

        byte[] fromInterlaced = DecodedRgb(interlaced, out int iw, out int ih);
        byte[] fromSequential = DecodedRgb(sequential, out int sw, out int sh);

        Assert.Equal(32, iw);
        Assert.Equal(32, ih);
        Assert.Equal(sw, iw);
        Assert.Equal(sh, ih);
        Assert.Equal(fromSequential, fromInterlaced);
    }

    /// <summary>Named pixels of the interlaced decode, one from each Adam7 pass.</summary>
    [Theory]
    [InlineData(0, 0)]    // pass 1
    [InlineData(4, 0)]    // pass 2
    [InlineData(0, 4)]    // pass 3
    [InlineData(2, 0)]    // pass 4
    [InlineData(0, 2)]    // pass 5
    [InlineData(1, 0)]    // pass 6
    [InlineData(0, 1)]    // pass 7
    [InlineData(31, 31)]  // the far corner, which only a complete walk reaches
    public void Adam7Png_PlacesEachPassSamplesAtTheRightCoordinate(int x, int y)
    {
        byte[] rgb = DecodedRgb(MakePng(32, 32, interlace: true), out int w, out _);

        int o = (y * w + x) * 3;
        Assert.Equal(ExpectedR(x, y), rgb[o]);
        Assert.Equal(ExpectedG(x, y), rgb[o + 1]);
        Assert.Equal(ExpectedB(x, y), rgb[o + 2]);
    }

    /// <summary>An interlaced PNG large enough to breach the old cap reaches the exported PDF.</summary>
    [Fact]
    public void Adam7Png_IsEmbeddedAsAnImageXObject()
    {
        byte[] pdf = new DocumentBuilder()
            .AddSection(s => s.AddParagraph(p => p.AddImage(MakePng(32, 32, interlace: true), 48, 48)))
            .Build().ToPdf();

        string raw = Encoding.Latin1.GetString(pdf);
        Assert.Contains("/Subtype /Image", raw);
        // The declared size too: "an image XObject exists" is also satisfied by the 8x8 fallback
        // an inflate-cap refusal would have left behind.
        Assert.Contains("/Width 32", raw);
        Assert.Contains("/Height 32", raw);
        Assert.Null(BuilderDiagnostics.LastError);
    }

    // ---------------------------------------------------------------- hostile IHDR

    /// <summary>
    /// A malformed PNG whose IHDR chunk declares fewer than its mandatory 13 bytes returns
    /// <c>null</c> instead of indexing off the end of the caller's array.
    /// </summary>
    /// <remarks>
    /// The chunk loop validated only that the DECLARED length fit the buffer, then read 13 bytes
    /// regardless — so the 16-byte fixture below (signature plus an empty IHDR header, with no
    /// chunk body at all) threw <see cref="IndexOutOfRangeException"/> out of <c>Decode</c>, out of
    /// <c>DrawImageBlock</c> and out of <c>Document.ToPdf</c>, on a path whose contract is to report
    /// <see cref="BuilderDiagnosticKind.ImageDecodeFailed"/>.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(12)]
    public void MalformedPng_IhdrShorterThanThirteenBytes_DecodesToNull(int ihdrLength)
    {
        byte[] png = TruncatedIhdrPng(ihdrLength);

        // A direct call: an exception here fails the test, which is the guard's own evidence.
        DecodedImage decoded = BuilderImageDecoder.Decode(png);

        Assert.Null(decoded);
    }

    /// <summary>The whole export survives such an image and says so.</summary>
    [Fact]
    public void MalformedPng_IhdrShorterThanThirteenBytes_IsReportedNotThrown()
    {
        byte[] pdf = new DocumentBuilder()
            .AddSection(s => s.AddParagraph(p => p.AddImage(TruncatedIhdrPng(0), 24, 24)))
            .Build().ToPdf();

        Assert.Equal((byte)'%', pdf[0]);
        Assert.NotNull(BuilderDiagnostics.LastError);
        Assert.Equal(BuilderDiagnosticKind.ImageDecodeFailed, BuilderDiagnostics.LastError.Kind);
    }

    // ---------------------------------------------------------------- tRNS colour key

    /// <summary>
    /// A tRNS chunk on a NON-palette image is a colour key (PNG spec 11.3.2.1): one sample value
    /// per channel, and every pixel matching it exactly is fully transparent.
    /// </summary>
    /// <remarks>
    /// Only the palette form was read, so a keyed grey or RGB pixel decoded OPAQUE -- a logo saved
    /// with a transparent background by any tool that writes colour type 0 or 2 drew its background
    /// as a solid rectangle. <c>PngImage</c>, the OTHER PNG decoder in this tree, honoured both all
    /// along; the two decoders each had exactly what the other lacked.
    /// <para>The assertion is on the soft mask rather than on the RGB, because the alpha is carried
    /// out as a real /SMask instead of being composited away -- so a keyed pixel's colour bytes are
    /// deliberately UNCHANGED and reading them could never show the difference.</para>
    /// </remarks>
    [Fact]
    public void AGreyscaleTrnsValueMakesThatShadeTransparentAndLeavesTheOthersOpaque()
    {
        // 3x1 grey: 40, 128, 200 -- with 128 keyed.
        byte[] png = AssemblePng(3, 1, colorType: 0,
            raw: new byte[] { 0, 40, 128, 200 },
            trns: new byte[] { 0x00, 0x80 });

        DecodedImage img = BuilderImageDecoder.Decode(png);
        Assert.NotNull(img);
        Assert.NotNull(img.SoftMask);
        Assert.Equal(new byte[] { 255, 0, 255 }, img.SoftMask);
    }

    /// <summary>The RGB form of the same rule, with a near-miss pixel that must stay opaque.</summary>
    /// <remarks>
    /// The third pixel differs from the key in ONE unit of blue. A comparison that keyed on the
    /// scaled byte, or on any two of the three channels, punches a hole in it too -- which is the
    /// failure this fixture exists to separate from the plain case.
    /// </remarks>
    [Fact]
    public void AnRgbTrnsValueKeysOnlyTheExactColour()
    {
        byte[] png = AssemblePng(3, 1, colorType: 2,
            raw: new byte[] { 0, 10, 20, 30, 99, 99, 99, 10, 20, 31 },
            trns: new byte[] { 0, 10, 0, 20, 0, 30 });

        DecodedImage img = BuilderImageDecoder.Decode(png);
        Assert.NotNull(img);
        Assert.NotNull(img.SoftMask);
        Assert.Equal(new byte[] { 0, 255, 255 }, img.SoftMask);
    }

    /// <summary>Without a tRNS chunk nothing is keyed, and no mask is allocated at all.</summary>
    [Fact]
    public void TheSameImageWithNoTrnsChunkIsFullyOpaque()
    {
        byte[] png = AssemblePng(3, 1, colorType: 0, raw: new byte[] { 0, 40, 128, 200 }, trns: null);

        DecodedImage img = BuilderImageDecoder.Decode(png);
        Assert.NotNull(img);
        Assert.Null(img.SoftMask);
    }

    // ---------------------------------------------------------------- filter type byte

    /// <summary>
    /// PNG defines filter types 0-4 (RFC 2083 section 6). An undefined byte is refused.
    /// </summary>
    /// <remarks>
    /// <c>Unfilter</c> had no default arm, so a byte of 5..255 fell out of the switch and the
    /// scanline was used AS IF unfiltered -- that row and every row after it, each filtered against
    /// the one before, decoded to garbage and a perfectly valid-looking image was returned. Only a
    /// corrupt or hostile file reaches it, which is precisely the input that must not produce
    /// confident output.
    /// </remarks>
    [Theory]
    [InlineData(5)]
    [InlineData(200)]
    public void AnUndefinedFilterTypeIsRefusedInsteadOfBeingTreatedAsUnfiltered(byte filter)
    {
        byte[] png = AssemblePng(2, 1, colorType: 2,
            raw: new byte[] { filter, 1, 2, 3, 4, 5, 6 }, trns: null);

        Assert.Null(BuilderImageDecoder.Decode(png));
    }

    /// <summary>
    /// The control the case above needs: filter 4 is DEFINED, so the identical fixture decodes --
    /// and decodes to the RIGHT pixels, which is the part that makes it a control.
    /// </summary>
    /// <remarks>
    /// Asserting only that something came back would be satisfied by a decoder that returned a
    /// blank image, and then the refusal test above would be passing for no reason anyone could
    /// see. Paeth over a zero previous row leaves the first pixel alone and adds the pixel to its
    /// left thereafter: {1,2,3} then {4,5,6} + {1,2,3} = {5,7,9}. A decoder that treated filter 4
    /// as "none" -- the exact failure the missing default arm produced for filter 5 -- returns
    /// {4,5,6} for the second pixel and fails here.
    /// </remarks>
    [Fact]
    public void ADefinedFilterTypeOnTheSameFixtureStillDecodesToTheUnfilteredPixels()
    {
        byte[] png = AssemblePng(2, 1, colorType: 2,
            raw: new byte[] { 4, 1, 2, 3, 4, 5, 6 }, trns: null);

        byte[] rgb = DecodedRgb(png, out int w, out int h);

        Assert.Equal(2, w);
        Assert.Equal(1, h);
        Assert.Equal(new byte[] { 1, 2, 3, 5, 7, 9 }, rgb);
    }

    /// <summary>An 8-bit PNG of the given colour type from a ready-made raw stream, tRNS optional.</summary>
    private static byte[] AssemblePng(int w, int h, byte colorType, byte[] raw, byte[] trns)
    {
        byte[] idat;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw, 0, raw.Length);
            idat = ms.ToArray();
        }

        using var outMs = new MemoryStream();
        outMs.Write(PngSignature, 0, 8);
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, w);
        WriteBE(ihdr, 4, h);
        ihdr[8] = 8;                  // bit depth
        ihdr[9] = colorType;
        ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(outMs, "IHDR", ihdr);
        if (trns != null) WriteChunk(outMs, "tRNS", trns);   // must precede IDAT
        WriteChunk(outMs, "IDAT", idat);
        WriteChunk(outMs, "IEND", Array.Empty<byte>());
        return outMs.ToArray();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Inflates the RGB samples the exporter would embed for these PNG bytes.</summary>
    private static byte[] DecodedRgb(byte[] png, out int width, out int height)
    {
        DecodedImage img = BuilderImageDecoder.Decode(png);
        Assert.NotNull(img);                       // a null decode is the defect, not a weak assert
        Assert.Equal("FlateDecode", img.Filter);

        width = img.Width;
        height = img.Height;
        using var ms = new MemoryStream(img.Data);
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        z.CopyTo(outMs);
        byte[] rgb = outMs.ToArray();
        Assert.Equal(width * height * 3, rgb.Length);
        return rgb;
    }

    // The seven Adam7 passes (PNG spec §8.2), the same geometry the decoder walks.
    private static readonly int[] XOrigin = { 0, 4, 0, 2, 0, 1, 0 };
    private static readonly int[] YOrigin = { 0, 0, 4, 0, 2, 0, 1 };
    private static readonly int[] XStep = { 8, 8, 4, 4, 2, 2, 1 };
    private static readonly int[] YStep = { 8, 8, 8, 4, 4, 2, 2 };

    /// <summary>An 8-bit RGB PNG carrying the <c>Expected*</c> pattern, sequential or Adam7.</summary>
    private static byte[] MakePng(int w, int h, bool interlace)
    {
        byte[] raw = interlace ? Adam7Raw(w, h) : SequentialRaw(w, h);

        byte[] idat;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw, 0, raw.Length);
            idat = ms.ToArray();
        }

        using var outMs = new MemoryStream();
        outMs.Write(PngSignature, 0, 8);
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, w);
        WriteBE(ihdr, 4, h);
        ihdr[8] = 8;                          // bit depth
        ihdr[9] = 2;                          // colour type: truecolour
        ihdr[10] = 0; ihdr[11] = 0;
        ihdr[12] = (byte)(interlace ? 1 : 0);
        WriteChunk(outMs, "IHDR", ihdr);
        WriteChunk(outMs, "IDAT", idat);
        WriteChunk(outMs, "IEND", Array.Empty<byte>());
        return outMs.ToArray();
    }

    private static byte[] SequentialRaw(int w, int h)
    {
        byte[] raw = new byte[h * (1 + w * 3)];
        int p = 0;
        for (int y = 0; y < h; y++)
        {
            raw[p++] = 0;                     // filter: none
            for (int x = 0; x < w; x++)
            {
                raw[p++] = ExpectedR(x, y);
                raw[p++] = ExpectedG(x, y);
                raw[p++] = ExpectedB(x, y);
            }
        }
        return raw;
    }

    private static byte[] Adam7Raw(int w, int h)
    {
        using var ms = new MemoryStream();
        for (int pass = 0; pass < 7; pass++)
        {
            int pw = (w - XOrigin[pass] + XStep[pass] - 1) / XStep[pass];
            int ph = (h - YOrigin[pass] + YStep[pass] - 1) / YStep[pass];
            if (pw <= 0 || ph <= 0) continue;
            for (int py = 0; py < ph; py++)
            {
                ms.WriteByte(0);              // filter: none
                int y = YOrigin[pass] + py * YStep[pass];
                for (int px = 0; px < pw; px++)
                {
                    int x = XOrigin[pass] + px * XStep[pass];
                    ms.WriteByte(ExpectedR(x, y));
                    ms.WriteByte(ExpectedG(x, y));
                    ms.WriteByte(ExpectedB(x, y));
                }
            }
        }
        return ms.ToArray();
    }

    /// <summary>Signature plus an IHDR chunk header declaring <paramref name="len"/> body bytes.</summary>
    private static byte[] TruncatedIhdrPng(int len)
    {
        using var ms = new MemoryStream();
        ms.Write(PngSignature, 0, 8);
        var lenBytes = new byte[4];
        WriteBE(lenBytes, 0, len);
        ms.Write(lenBytes, 0, 4);
        byte[] type = Encoding.ASCII.GetBytes("IHDR");
        ms.Write(type, 0, 4);
        // The body is deliberately absent: the chunk header CLAIMS `len` bytes and the file ends.
        // `len` bytes of zero would also be short of 13 for every value tested.
        for (int i = 0; i < len; i++) ms.WriteByte(0);
        return ms.ToArray();
    }

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, data.Length);
        s.Write(len, 0, 4);
        byte[] t = Encoding.ASCII.GetBytes(type);
        var crcIn = new byte[4 + data.Length];
        Array.Copy(t, 0, crcIn, 0, 4);
        Array.Copy(data, 0, crcIn, 4, data.Length);
        s.Write(t, 0, 4);
        s.Write(data, 0, data.Length);
        uint crc = Crc32(crcIn);
        var crcb = new byte[4];
        WriteBE(crcb, 0, (int)crc);
        s.Write(crcb, 0, 4);
    }

    private static void WriteBE(byte[] b, int o, int v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    private static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (0xEDB88320 ^ (c >> 1)) : (c >> 1);
        }
        return c ^ 0xFFFFFFFF;
    }
}
