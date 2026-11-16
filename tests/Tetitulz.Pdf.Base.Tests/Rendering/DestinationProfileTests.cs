// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The ordinary render into a destination profile (<see cref="PdfRenderOptions.DestinationProfile"/>):
/// the conversion, held to the reference colour engine's, and the profile embedded in each format.
/// </summary>
/// <remarks>
/// The three profiles are Compact-ICC-Profiles' (Clinton Ingram, saucecontrol/Compact-ICC-Profiles on GitHub),
/// released to the public domain under CC0 1.0. The expected values are the reference colour engine's — the reference colour engine, from
/// that collection's <c>sRGB-v4.icc</c> to each, relative colorimetric — for every colour of the levels 0, 68,
/// 187 and 255 per channel; checked over 4 096 colours (levels in steps of 17) while this was written, every
/// one within a level.
/// </remarks>
public class DestinationProfileTests
{
    private static readonly byte[] DisplayP3v4Reference = { 0, 0, 0, 0, 0, 65, 0, 0, 179, 0, 0, 245, 26, 67, 14, 26, 67, 67, 26, 67, 180, 26, 67, 245, 84, 184, 53, 84, 184, 84, 84, 184, 186, 84, 184, 249, 117, 251, 76, 117, 251, 99, 117, 251, 192, 117, 251, 253, 62, 6, 3, 62, 6, 65, 62, 6, 179, 62, 6, 245, 68, 68, 16, 68, 68, 68, 68, 68, 180, 68, 68, 245, 103, 185, 54, 103, 185, 84, 103, 185, 186, 103, 185, 249, 130, 251, 77, 130, 251, 99, 130, 251, 192, 130, 251, 253, 171, 35, 23, 171, 35, 70, 171, 35, 181, 171, 35, 246, 173, 76, 30, 173, 76, 72, 173, 76, 182, 173, 76, 246, 187, 187, 60, 187, 187, 88, 187, 187, 187, 187, 187, 250, 201, 253, 80, 201, 253, 102, 201, 253, 193, 201, 253, 254, 234, 51, 35, 234, 51, 75, 234, 51, 182, 234, 51, 247, 235, 84, 40, 235, 84, 77, 235, 84, 183, 235, 84, 247, 245, 190, 65, 245, 190, 91, 245, 190, 188, 245, 190, 251, 255, 255, 84, 255, 255, 105, 255, 255, 194, 255, 255, 255 };

    private static readonly byte[] AdobeCompatv4Reference = { 0, 0, 0, 0, 0, 68, 0, 0, 182, 0, 0, 250, 39, 70, 16, 39, 70, 70, 39, 70, 182, 40, 70, 250, 105, 186, 44, 105, 186, 79, 105, 186, 186, 105, 186, 253, 144, 255, 60, 144, 255, 88, 144, 255, 189, 144, 255, 255, 60, 0, 0, 60, 0, 68, 60, 0, 182, 60, 0, 250, 70, 70, 16, 70, 70, 70, 70, 70, 182, 70, 70, 250, 118, 186, 44, 118, 186, 79, 118, 186, 186, 118, 186, 253, 153, 255, 60, 153, 255, 88, 153, 255, 189, 153, 255, 255, 159, 0, 0, 159, 0, 68, 159, 0, 182, 159, 0, 250, 163, 70, 16, 163, 70, 70, 163, 70, 182, 163, 70, 250, 186, 186, 44, 186, 186, 79, 186, 186, 186, 186, 186, 253, 208, 255, 60, 208, 255, 88, 208, 255, 189, 208, 255, 255, 219, 0, 0, 219, 0, 68, 219, 0, 182, 219, 0, 250, 221, 70, 16, 221, 70, 70, 221, 70, 182, 221, 70, 250, 238, 186, 44, 238, 186, 79, 238, 186, 186, 238, 186, 253, 255, 255, 60, 255, 255, 88, 255, 255, 189, 255, 255, 255 };

    private static readonly byte[] sGreyv4Reference = { 0, 11, 48, 70, 57, 60, 76, 90, 161, 162, 167, 173, 220, 221, 224, 228, 30, 34, 58, 77, 66, 68, 82, 95, 164, 164, 169, 175, 222, 222, 226, 230, 93, 95, 105, 115, 109, 110, 118, 127, 182, 182, 187, 192, 235, 235, 238, 242, 130, 131, 138, 145, 140, 141, 148, 154, 200, 201, 205, 209, 248, 248, 252, 255 };

    private static readonly int[] Levels = { 0, 68, 187, 255 };

    private static byte[] Profile(string base64) => Convert.FromBase64String(base64);

    public static IEnumerable<object[]> Profiles() => new[]
    {
        new object[] { "DisplayP3", CompactIccProfiles.DisplayP3v4, DisplayP3v4Reference, false },
        new object[] { "AdobeCompat", CompactIccProfiles.AdobeCompatv4, AdobeCompatv4Reference, false },
        new object[] { "sGrey", CompactIccProfiles.sGreyv4, sGreyv4Reference, true },
    };

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Convert_IsTheReferenceEnginesWithinALevel(string name, string base64, byte[] reference, bool grey)
    {
        var destination = RenderDestination.TryCreate(Profile(base64), true, out string reason);
        Assert.True(destination != null, name + ": " + reason);
        Assert.Equal(grey, destination.IsGrey);
        Assert.False(destination.IsTable);

        int i = 0, exact = 0;
        foreach (int r in Levels)
            foreach (int g in Levels)
                foreach (int b in Levels)
                {
                    SKColor ours = destination.Convert(new SKColor((byte)r, (byte)g, (byte)b));
                    byte[] theirs = grey ? new[] { reference[i], reference[i], reference[i] } : new[] { reference[3 * i], reference[3 * i + 1], reference[3 * i + 2] };
                    i++;
                    int worst = Math.Max(Math.Abs(ours.Red - theirs[0]), Math.Max(Math.Abs(ours.Green - theirs[1]), Math.Abs(ours.Blue - theirs[2])));
                    Assert.True(worst <= 1, $"{name} ({r}, {g}, {b}): ours {ours}, reference ({theirs[0]}, {theirs[1]}, {theirs[2]})");
                    if (worst == 0) exact++;
                }
        Assert.True(exact >= 48, $"{name}: only {exact} of 64 exact");   // not merely near: mostly the same
    }

    [Fact]
    public void Render_ToDisplayP3_ConvertsEveryPath_AndLeavesPlatesAlone()
    {
        byte[] pdf = RedAndGreyPage();
        var options = new PdfRenderOptions { DestinationProfile = Profile(CompactIccProfiles.DisplayP3v4) };
        var destination = RenderDestination.TryCreate(options.DestinationProfile, true, out _);

        var page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72, options);
        var srgb = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);
        int at = (5 * page.Width + 5) * 4;   // in the red square
        var red = destination.Convert(new SKColor(255, 0, 0));
        Assert.Equal((red.Red, red.Green, red.Blue), (page.Pixels[at], page.Pixels[at + 1], page.Pixels[at + 2]));
        Assert.Equal((255, 0, 0), ((int)srgb.Pixels[at], (int)srgb.Pixels[at + 1], (int)srgb.Pixels[at + 2]));
        Assert.True(red.Red < 255 && red.Green > 0, "Display P3's red is inside sRGB's: less than full red, some green");

        // A tile of the page is the page's pixels.
        byte[] tilePng = PdfOperations.RenderPageTile(new MemoryStream(pdf), 0, 0, 0, 20, 20, 72, options);
        using (var codec = SKCodec.Create(new MemoryStream(tilePng)))
        {
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var tile = new SKBitmap(info);
            codec.GetPixels(info, tile.GetPixels());
            SKColor c = tile.GetPixel(5, 5);
            Assert.Equal((red.Red, red.Green, red.Blue), (c.Red, c.Green, c.Blue));
        }

        // The plates are ink, never converted. Asked under Adobe RGB, whose gamma moves the page's grey: Display P3
        // keeps sRGB's tone curve, so its greys — all a plate pass draws — would come through either way.
        var adobe = new PdfRenderOptions { DestinationProfile = Profile(CompactIccProfiles.AdobeCompatv4) };
        int greyAt = (15 * page.Width + 30) * 4;
        Assert.NotEqual(srgb.Pixels[greyAt], PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72, adobe).Pixels[greyAt]);
        var plain = PdfSeparationRenderer.RenderPage(new MemoryStream(pdf), 0, 72);
        var withDestination = PdfSeparationRenderer.RenderPage(new MemoryStream(pdf), 0, 72, adobe);
        Assert.Contains(plain.Plates, p => p.Tints.Any(t => t > 0 && t < 255));
        Assert.Equal(plain.Plates.Select(p => p.Tints), withDestination.Plates.Select(p => p.Tints));
    }

    [Fact]
    public void Png_CarriesTheProfileInICCP_AndSkiaReadsItsColourSpace()
    {
        byte[] profile = Profile(CompactIccProfiles.DisplayP3v4);
        byte[] png = PdfOperations.RenderPageToPng(new MemoryStream(RedAndGreyPage()), 0, 72,
            new PdfRenderOptions { DestinationProfile = profile });

        var chunks = PngChunks(png);
        Assert.Equal("IHDR", chunks[0].Type);
        Assert.Equal("iCCP", chunks[1].Type);
        Assert.DoesNotContain(chunks, c => c.Type == "sRGB" || c.Type == "gAMA" || c.Type == "cHRM");
        byte[] data = chunks[1].Data;
        int nul = Array.IndexOf(data, (byte)0);
        Assert.Equal("ICC profile", Encoding.ASCII.GetString(data, 0, nul));
        Assert.Equal(0, data[nul + 1]);   // compression method: zlib
        Assert.Equal(profile, Inflate(data.Skip(nul + 2).ToArray()));

        // An independent reader (Skia's PNG codec) takes the chunk as the image's colour space, and it is not sRGB.
        using var codec = SKCodec.Create(new MemoryStream(png));
        Assert.NotNull(codec.Info.ColorSpace);
        Assert.False(codec.Info.ColorSpace.IsSrgb);
    }

    [Fact]
    public void Png_AColourChunkAlreadyThere_GivesWayToTheProfile()
    {
        // Skia writes none of them, so a PNG that carries every colour chunk is built by hand: after IHDR an old
        // iCCP, sRGB, gAMA and cHRM, each with its CRC — chunks a reader would take ahead of, or beside, the profile.
        byte[] plain = PdfOperations.RenderPageToPng(new MemoryStream(RedAndGreyPage()), 0, 72);
        var stale = new MemoryStream();
        stale.Write(plain, 0, 8 + 25);   // the signature and IHDR
        foreach (var (type, data) in new[]
        {
            ("iCCP", Encoding.ASCII.GetBytes("old\0\0").Concat(new byte[] { 0x78, 0x9C, 3, 0, 0, 0, 0, 1 }).ToArray()),
            ("sRGB", new byte[] { 0 }),
            ("gAMA", new byte[] { 0, 0, 0xB1, 0x8F }),
            ("cHRM", new byte[32]),
        })
            WriteChunk(stale, type, data);
        stale.Write(plain, 8 + 25, plain.Length - 8 - 25);
        Assert.Equal(new[] { "IHDR", "iCCP", "sRGB", "gAMA", "cHRM" }, PngChunks(stale.ToArray()).Take(5).Select(c => c.Type));

        byte[] profile = Profile(CompactIccProfiles.DisplayP3v4);
        var chunks = PngChunks(ImageProvenance.EmbedIccPng(stale.ToArray(), profile));
        Assert.Equal("iCCP", chunks[1].Type);
        Assert.Single(chunks, c => c.Type == "iCCP");
        Assert.DoesNotContain(chunks, c => c.Type == "sRGB" || c.Type == "gAMA" || c.Type == "cHRM");
        Assert.Contains(chunks, c => c.Type == "IDAT");   // the image itself kept
    }

    private static void WriteChunk(Stream to, string type, byte[] data)
    {
        byte[] typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        uint crc = 0xFFFFFFFF;
        foreach (byte b in typed)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        crc ^= 0xFFFFFFFF;
        to.Write(new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length }, 0, 4);
        to.Write(typed, 0, typed.Length);
        to.Write(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc }, 0, 4);
    }

    [Fact]
    public void Jpeg_CarriesTheProfileInApp2()
    {
        byte[] profile = Profile(CompactIccProfiles.AdobeCompatv4);
        byte[] jpeg = PdfOperations.RenderPageToJpg(new MemoryStream(RedAndGreyPage()), 0, 72, 90,
            new PdfRenderOptions { DestinationProfile = profile });

        Assert.Equal(profile, JpegIccProfile(jpeg));
        using var codec = SKCodec.Create(new MemoryStream(jpeg));
        Assert.NotNull(codec.Info.ColorSpace);
        Assert.False(codec.Info.ColorSpace.IsSrgb);
    }

    [Fact]
    public void Jpeg_ALargeProfile_IsSplitAcrossSegmentsInOrder()
    {
        byte[] jpeg = PdfOperations.RenderPageToJpg(new MemoryStream(RedAndGreyPage()), 0, 72, 90);
        var big = new byte[140_000];
        new Random(7).NextBytes(big);
        byte[] tagged = ImageProvenance.EmbedIccJpeg(jpeg, big);
        Assert.Equal(big, JpegIccProfile(tagged, expectedSegments: 3));
        // Nothing but the segments was added.
        Assert.Equal(jpeg.Length + big.Length + 3 * 18, tagged.Length);
    }

    [Theory]
    [InlineData(PdfImageFormat.WebpLossless)]
    [InlineData(PdfImageFormat.WebpLossy)]
    public void Webp_BecomesTheExtendedFormWithAnIccpChunk(PdfImageFormat format)
    {
        byte[] profile = Profile(CompactIccProfiles.DisplayP3v4);
        byte[] webp = PdfOperations.RenderPageToImage(new MemoryStream(RedAndGreyPage()), 0, format, 72, 80,
            new PdfRenderOptions { DestinationProfile = profile });

        Assert.Equal("RIFF", Encoding.ASCII.GetString(webp, 0, 4));
        Assert.Equal(webp.Length - 8, BitConverter.ToInt32(webp, 4));
        Assert.Equal("WEBPVP8X", Encoding.ASCII.GetString(webp, 8, 8));
        Assert.Equal(10, BitConverter.ToInt32(webp, 16));
        Assert.Equal(0x20, webp[20] & 0x20);   // ICC
        Assert.Equal(40 - 1, webp[24] | (webp[25] << 8) | (webp[26] << 16));   // canvas width − 1
        Assert.Equal(30 - 1, webp[27] | (webp[28] << 8) | (webp[29] << 16));
        Assert.Equal("ICCP", Encoding.ASCII.GetString(webp, 30, 4));
        int size = BitConverter.ToInt32(webp, 34);
        Assert.Equal(profile, webp.Skip(38).Take(size).ToArray());

        using var codec = SKCodec.Create(new MemoryStream(webp));
        Assert.Equal((40, 30), (codec.Info.Width, codec.Info.Height));
        Assert.NotNull(codec.Info.ColorSpace);
        Assert.False(codec.Info.ColorSpace.IsSrgb);
    }

    [Fact]
    public void GreyDestination_WritesGreyPngAndJpeg_AndReportsWebp()
    {
        byte[] profile = Profile(CompactIccProfiles.sGreyv4);
        var options = new PdfRenderOptions { DestinationProfile = profile };
        byte[] png = PdfOperations.RenderPageToPng(new MemoryStream(RedAndGreyPage()), 0, 72, options);
        var chunks = PngChunks(png);
        Assert.Equal(0, chunks[0].Data[9]);   // IHDR colour type 0: greyscale
        Assert.Equal("iCCP", chunks[1].Type);

        byte[] jpeg = PdfOperations.RenderPageToJpg(new MemoryStream(RedAndGreyPage()), 0, 72, 90, options);
        Assert.Equal(profile, JpegIccProfile(jpeg));
        Assert.Equal(1, JpegComponents(jpeg));

        var reports = Reports(() => PdfOperations.RenderPageToImage(new MemoryStream(RedAndGreyPage()), 0, PdfImageFormat.WebpLossless, 72, 100, options));
        Assert.Contains(reports, r => r.Contains("grey destination", StringComparison.Ordinal));
    }

    [Fact]
    public void RgbDestination_WithGreyOutput_IsReportedAndUntagged()
    {
        var options = new PdfRenderOptions { DestinationProfile = Profile(CompactIccProfiles.DisplayP3v4), PixelFormat = RenderPixelFormat.Grayscale };
        byte[] png = null;
        var reports = Reports(() => png = PdfOperations.RenderPageToPng(new MemoryStream(RedAndGreyPage()), 0, 72, options));
        Assert.Contains(reports, r => r.Contains("grey output of an RGB destination", StringComparison.Ordinal));
        Assert.DoesNotContain(PngChunks(png), c => c.Type == "iCCP");

        // The control: colour output of the same destination reports nothing.
        Assert.Empty(Reports(() => PdfOperations.RenderPageToPng(new MemoryStream(RedAndGreyPage()), 0, 72,
            new PdfRenderOptions { DestinationProfile = Profile(CompactIccProfiles.DisplayP3v4) })));
    }

    [Fact]
    public void TableDestination_GoesThroughItsB2A_OnTheGrid()
    {
        // Lab → RGB, grid 2: every channel L*, so a colour lands on grey by its lightness.
        var clut = new int[8 * 3];
        for (int cell = 0; cell < 8; cell++)
            for (int c = 0; c < 3; c++) clut[cell * 3 + c] = (cell & 4) != 0 ? 0xFFFF : 0;
        byte[] profile = IccB2AConversionTests.BuildB2AProfile("RGB ", 3, 3, 2, clut, "mft2");
        var destination = RenderDestination.TryCreate(profile, true, out string reason);
        Assert.True(destination != null, reason);
        Assert.True(destination.IsTable);
        Assert.False(destination.IsGrey);

        var page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(RedAndGreyPage()), 0, 72,
            new PdfRenderOptions { DestinationProfile = profile });
        int at = (5 * page.Width + 5) * 4;
        SKColor exact = destination.Convert(new SKColor(255, 0, 0));
        Assert.Equal(exact.Red, exact.Blue);   // grey
        Assert.InRange(page.Pixels[at], exact.Red - 2, exact.Red + 2);   // the grid's reading of it
        Assert.Equal(page.Pixels[at], page.Pixels[at + 2]);
        Assert.InRange((int)page.Pixels[at], 100, 160);   // red's L* is 53
    }

    [Fact]
    public void UnusableDestinations_AreReported_AndThePageStaysSrgb()
    {
        // Columns all alike: no inverse.
        double[] col = { 0.4, 0.3, 0.2 };
        byte[] singular = IccTransformTests.BuildProfile("mntr", "RGB ", "XYZ ",
            ("rXYZ", IccTransformTests.XyzTag(col[0], col[1], col[2])), ("gXYZ", IccTransformTests.XyzTag(col[0], col[1], col[2])),
            ("bXYZ", IccTransformTests.XyzTag(col[0], col[1], col[2])),
            ("rTRC", Gamma1), ("gTRC", Gamma1), ("bTRC", Gamma1), ("wtpt", IccTransformTests.XyzTag(0.9642, 1.0, 0.8249)));
        byte[] falling = IccTransformTests.BuildProfile("mntr", "GRAY", "XYZ ",
            ("kTRC", new byte[] { (byte)'c', (byte)'u', (byte)'r', (byte)'v', 0, 0, 0, 0, 0, 0, 0, 3, 0, 0, 0xFF, 0xFF, 0, 0, 0, 0 }),
            ("wtpt", IccTransformTests.XyzTag(0.9642, 1.0, 0.8249)));
        byte[] cmyk = SyntheticCmykProfile.Build(SyntheticCmykProfile.ConstantCyan(0x8000));

        foreach (var (profile, why) in new[] { (singular, "singular"), (falling, "not monotonic"), (cmyk, "PdfSeparationOptions.OutputIccProfile"), (new byte[200], "") })
        {
            var options = new PdfRenderOptions { DestinationProfile = profile };
            RenderedPage page = null;
            var reports = Reports(() => page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(RedAndGreyPage()), 0, 72, options));
            Assert.Contains(reports, r => r.StartsWith("the page was left in sRGB", StringComparison.Ordinal) && r.Contains(why, StringComparison.Ordinal));
            int at = (5 * page.Width + 5) * 4;
            Assert.Equal((255, 0, 0), ((int)page.Pixels[at], (int)page.Pixels[at + 1], (int)page.Pixels[at + 2]));
        }
    }

    [Fact]
    public void Destination_AndProof_KeepAPluginFromRendering()
    {
        Assert.False(new PdfRenderOptions().RequestsAnythingAPluginCannotHonour());
        Assert.True(new PdfRenderOptions { DestinationProfile = Profile(CompactIccProfiles.DisplayP3v4) }.RequestsAnythingAPluginCannotHonour());
        Assert.True(new PdfRenderOptions { ProofProfile = new byte[1] }.RequestsAnythingAPluginCannotHonour());
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>A 40 × 30 page: sRGB red at the top left (0 … 20), mid grey beside it.</summary>
    private static byte[] RedAndGreyPage() => HalftoneTests.Pdf(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 40 30] /Contents 4 0 R >>",
        HalftoneTests.Stream("", Encoding.ASCII.GetBytes("1 0 0 rg 0 10 20 20 re f 0.5 g 20 0 20 30 re f")));

    private static readonly byte[] Gamma1 = { (byte)'c', (byte)'u', (byte)'r', (byte)'v', 0, 0, 0, 0, 0, 0, 0, 0 };

    private static List<(string Type, byte[] Data)> PngChunks(byte[] png)
    {
        var chunks = new List<(string, byte[])>();
        int at = 8;
        while (at + 12 <= png.Length)
        {
            int length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            chunks.Add((Encoding.ASCII.GetString(png, at + 4, 4), png.Skip(at + 8).Take(length).ToArray()));
            at += 12 + length;
        }
        Assert.Equal(png.Length, at);
        return chunks;
    }

    private static byte[] Inflate(byte[] zlib)
    {
        using var input = new ZLibStream(new MemoryStream(zlib), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>The profile in a JPEG's APP2 ICC_PROFILE segments, reassembled by sequence number.</summary>
    private static byte[] JpegIccProfile(byte[] jpeg, int expectedSegments = 1)
    {
        var parts = new SortedDictionary<int, byte[]>();
        int at = 2;
        while (at + 4 <= jpeg.Length && jpeg[at] == 0xFF && jpeg[at + 1] != 0xDA)
        {
            int length = (jpeg[at + 2] << 8) | jpeg[at + 3];
            if (jpeg[at + 1] == 0xE2 && Encoding.ASCII.GetString(jpeg, at + 4, 12) == "ICC_PROFILE\0")
            {
                Assert.Equal(expectedSegments, jpeg[at + 17]);
                parts.Add(jpeg[at + 16], jpeg.Skip(at + 18).Take(length - 16).ToArray());
            }
            at += 2 + length;
        }
        Assert.Equal(Enumerable.Range(1, expectedSegments), parts.Keys);
        return parts.Values.SelectMany(p => p).ToArray();
    }

    /// <summary>The component count of a JPEG's frame header.</summary>
    private static int JpegComponents(byte[] jpeg)
    {
        int at = 2;
        while (at + 4 <= jpeg.Length && jpeg[at] == 0xFF)
        {
            byte marker = jpeg[at + 1];
            int length = (jpeg[at + 2] << 8) | jpeg[at + 3];
            if (marker >= 0xC0 && marker <= 0xC3) return jpeg[at + 9];
            at += 2 + length;
        }
        throw new InvalidDataException("no frame header");
    }

    private static List<string> Reports(Action action)
    {
        var reports = new List<string>();
        int thread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId == thread && kind == RenderDiagnosticKind.DestinationProfileUnsupported)
                lock (reports) reports.Add(detail ?? "");
        }
        RenderDiagnostics.OnEvent += Sink;
        try { action(); }
        finally { RenderDiagnostics.OnEvent -= Sink; }
        lock (reports) return new List<string>(reports);
    }
}
