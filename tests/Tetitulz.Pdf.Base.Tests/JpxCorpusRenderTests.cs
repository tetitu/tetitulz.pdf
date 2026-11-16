// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// The JP2 colour rules against the reference decoder's conformance corpus, through the public surface — each
/// file embedded as a <c>JPXDecode</c> image XObject and the page rendered 1:1 — with no plugin
/// registered. The oracles: the reference decoder's PPMs for the sYCC 4:2:0 files, its TIFF baselines for the
/// palette and channel-definition files, the reference decoder's decode where the baseline applied
/// an ICC profile that ISO 32000-2 §7.4.9's precedence rule does not, and the class-1 PGX
/// references for the raw codestream.
/// </summary>
[Collection(PluginHostCollection.Name)]
public sealed class JpxCorpusRenderTests : IDisposable
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public JpxCorpusRenderTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
        PdfPluginHost.Instance.ResetForTesting();
    }

    public void Dispose() => PdfPluginHost.Instance.ResetForTesting();

    private static string Input(string name) => Path.Combine(TestCorpus.JpxRoot!, "input", "conformance", name);
    private static string Reference(string name) => Path.Combine(TestCorpus.JpxRoot!, "baseline", "conformance", name);

    /// <summary>The file rendered 1:1 through the page renderer: the image's samples as RGBA.</summary>
    private static RenderedPage RenderFile(string name, string extras, out byte[] data)
    {
        data = File.ReadAllBytes(Input(name));
        Assert.True(JpxHeader.TryReadImageSize(data, out long w, out long h, out _));
        using var ms = new MemoryStream(JpxTestPdf.ImagePdf(data, (int)w, (int)h, extras));
        RenderedPage page = PdfPageRenderer.RenderPageToPixels(ms, 0, 72);
        Assert.Equal(((int)w, (int)h), (page.Width, page.Height));
        return page;
    }

    private static byte[] Rgb(RenderedPage page)
    {
        var rgb = new byte[page.Width * page.Height * 3];
        int o = 0;
        for (int y = 0; y < page.Height; y++)
        {
            int row = y * page.Stride;
            for (int x = 0; x < page.Width; x++)
            {
                int p = row + x * 4;
                rgb[o++] = page.Pixels[p];
                rgb[o++] = page.Pixels[p + 1];
                rgb[o++] = page.Pixels[p + 2];
            }
        }
        return rgb;
    }

    private static string Sha256(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    // ── sYCC 4:2:0: zoo1 and subsampling_1 against the reference decoder's RGB ──────────

    /// <summary>
    /// zoo1 and subsampling_1 are sYCC with 4:2:0 chroma; the reference decoder's PPMs of them are RGB after its
    /// chroma up-sampling and colour conversion. The decoder up-samples by replication and converts
    /// with the ICT's constants, rounding where the reference decoder truncates, over a 9-7 decode that is
    /// itself within two levels of the reference decoder's. Measured 2026-09-25: peak 2/2/3 and mean
    /// 0.73/0.51/0.91 (zoo1), peak 2/2/3 and mean 0.72/0.53/0.89 (subsampling_1) — the limit is
    /// one level above the measured peak, and the mean must stay below a level.
    /// </summary>
    [JpxCorpusTheory]
    [InlineData("zoo1", 4)]
    [InlineData("subsampling_1", 4)]
    public void SYcc420_MatchesTheReferenceRgbWithinAPeakError(string stem, int peakLimit)
    {
        RenderedPage page = RenderFile(stem + ".jp2", "", out _);
        byte[] reference = File.ReadAllBytes(Reference(stem + ".ppm"));
        Assert.True(PnmHeader.TryRead(reference, out PnmHeader? h));
        Assert.Equal((page.Width, page.Height, 3), (h.Width, h.Height, h.Channels));

        byte[] rgb = Rgb(page);
        var peak = new int[3];
        var sum = new long[3];
        for (int c = 0; c < 3; c++)
        {
            int[] channel = h.ReadSamples(reference, c);
            for (int i = 0; i < channel.Length; i++)
            {
                int d = Math.Abs(rgb[i * 3 + c] - channel[i]);
                if (d > peak[c]) peak[c] = d;
                sum[c] += d;
            }
        }
        long count = (long)page.Width * page.Height;
        string measured = $"peak {peak[0]}/{peak[1]}/{peak[2]}, mean {sum[0] / (double)count:F3}/{sum[1] / (double)count:F3}/{sum[2] / (double)count:F3}";
        _output.WriteLine($"{stem} against the reference RGB: {measured}");
        Assert.True(peak[0] <= peakLimit && peak[1] <= peakLimit && peak[2] <= peakLimit, $"{stem}: {measured}, limit {peakLimit}");
        Assert.True(sum[0] / (double)count < 1.0 && sum[1] / (double)count < 1.0 && sum[2] / (double)count < 1.0, $"{stem}: {measured}; the mean error says the conversion is wrong, not merely rounded");
    }

    // ── the ISO JP2 files ───────────────────────────────────────────────────

    /// <summary>file9's palette expands exactly to the reference decoder's baseline (jp2_9.tif, byte-identical to an independent tool's decode).</summary>
    [JpxCorpusFact]
    public void Palette_File9_MatchesTheBaselineExactly()
    {
        RenderedPage page = RenderFile("file9.jp2", "", out _);
        Assert.Equal((768, 512), (page.Width, page.Height));
        Assert.Equal("c189c30a239bc892b01981825c8d9e0568eede7ab26e128e46315acabc1bf1e0", Sha256(Rgb(page)));
    }

    /// <summary>
    /// file2's cdef numbers its sYCC channels 3, 2, 1 (Cr first), and the baseline (jp2_2.tif, by
    /// opj_decompress) is the reordered channels converted to RGB. The decoder rounds where
    /// the reference decoder truncates, so sampled pixels agree within two levels; the raw, unconverted samples
    /// at (0, 0) are (175, 89, 140), which the conversion must have moved.
    /// </summary>
    [JpxCorpusFact]
    public void ChannelDefinitionAndSYcc_File2_MatchesTheBaselineWithinTwo()
    {
        RenderedPage page = RenderFile("file2.jp2", "", out _);
        Assert.Equal((480, 640), (page.Width, page.Height));
        var expected = new Dictionary<(int, int), (int, int, int)>
        {
            [(0, 0)] = (157, 168, 187), [(240, 320)] = (119, 120, 138), [(479, 639)] = (83, 84, 78), [(160, 160)] = (96, 102, 102),
            [(96, 426)] = (99, 81, 69), [(360, 91)] = (124, 120, 108), [(53, 637)] = (102, 94, 73), [(475, 320)] = (134, 137, 128),
        };
        byte[] rgb = Rgb(page);
        foreach (var kv in expected)
        {
            int i = (kv.Key.Item2 * page.Width + kv.Key.Item1) * 3;
            var got = (rgb[i], rgb[i + 1], rgb[i + 2]);
            Assert.True(Math.Abs(got.Item1 - kv.Value.Item1) <= 2 && Math.Abs(got.Item2 - kv.Value.Item2) <= 2 && Math.Abs(got.Item3 - kv.Value.Item3) <= 2,
                $"({kv.Key.Item1},{kv.Key.Item2}): expected {kv.Value}, got {got}");
        }
        Assert.NotEqual((175, 89, 140), ((int)rgb[0], (int)rgb[1], (int)rgb[2]));
    }

    /// <summary>
    /// file5 (ROMM-RGB at precedence 1 over an ICC profile at 0) takes the enumerated space
    /// (§7.4.9: highest precedence) and paints it through the ROMM-RGB transform (Stage D of 15k;
    /// the review of 2026-09-26, row 9): each pixel is <see cref="Jp2ExtendedColour.RommRgbToSRgb8"/>
    /// of the codestream's own samples, and not those samples read as sRGB, which is what
    /// the reference decoder hands back. The baseline's TIFF applied the profile instead, which the rule excludes.
    /// </summary>
    [JpxCorpusFact]
    public void EnumeratedSpaceOverIcc_File5_PaintsTheSamplesThroughTheRommTransform()
    {
        RenderedPage page = RenderFile("file5.jp2", "", out byte[] data);
        Assert.Equal((768, 512), (page.Width, page.Height));
        Assert.True(JpxDecoder.TryDecode(data, 0, out JpxDecodeResult raw, out string why), why);
        int[] r = raw.Image.Plane(0), g = raw.Image.Plane(1), b = raw.Image.Plane(2);
        byte[] rgb = Rgb(page);
        int differFromUntransformed = 0;
        foreach ((int x, int y) in new[] { (0, 0), (240, 320), (767, 511), (160, 160), (96, 426), (360, 91), (53, 400), (700, 320) })
        {
            int i = y * page.Width + x;
            Jp2ExtendedColour.RommRgbToSRgb8(r[i], g[i], b[i], 8, out byte er, out byte eg, out byte eb);
            var got = (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]);
            Assert.True(Math.Abs(got.Item1 - er) <= 1 && Math.Abs(got.Item2 - eg) <= 1 && Math.Abs(got.Item3 - eb) <= 1,
                $"({x},{y}): expected the ROMM transform of ({r[i]}, {g[i]}, {b[i]}) = ({er}, {eg}, {eb}), got {got}");
            if (Math.Abs(got.Item1 - r[i]) > 2 || Math.Abs(got.Item2 - g[i]) > 2 || Math.Abs(got.Item3 - b[i]) > 2) differFromUntransformed++;
        }
        Assert.True(differFromUntransformed >= 4, $"only {differFromUntransformed} of the eight pixels differ from the untransformed samples; the transform did not reach the page");

        Assert.True(JpxImageDecoder.TryDecodeSamples(data, null, null, out JpxPdfImage image, out string reason), reason);
        Assert.Equal(Jp2ColourSpaceKind.RommRgb, image.Plan.ColourSpace);
        Assert.Null(image.IccProfile);
    }

    /// <summary>
    /// file7 (e-sRGB at precedence 1 over an ICC profile at 0) is 16-bit: the enumerated space is
    /// taken and painted through the e-sRGB transform (Stage D of 15k). The pinned values are
    /// an independent tool's 8-bit decode of the same samples (the high byte of each 16-bit code), taken
    /// through <see cref="Jp2ExtendedColour.ESRgbToSRgb8"/> at 16 bits; the low byte the pin
    /// lost is worth under two levels after the transform, and the pixel must differ from the
    /// pin itself — the untransformed reading this once painted.
    /// </summary>
    [JpxCorpusFact]
    public void EnumeratedSpaceOverIcc_File7_SixteenBit_PaintsTheReferenceSamplesThroughTheESRgbTransform()
    {
        RenderedPage page = RenderFile("file7.jp2", "", out byte[] data);
        Assert.Equal((480, 640), (page.Width, page.Height));
        var reference = new Dictionary<(int, int), (int, int, int)>
        {
            [(0, 0)] = (176, 179, 186), [(240, 320)] = (154, 156, 167), [(479, 639)] = (154, 148, 135),
            [(160, 160)] = (126, 129, 130), [(96, 426)] = (158, 152, 145),
        };
        byte[] rgb = Rgb(page);
        foreach (var kv in reference)
        {
            int i = (kv.Key.Item2 * page.Width + kv.Key.Item1) * 3;
            var got = (rgb[i], rgb[i + 1], rgb[i + 2]);
            var expected = (Jp2ExtendedColour.ESRgbToSRgb8(kv.Value.Item1 * 256 + 128, 16), Jp2ExtendedColour.ESRgbToSRgb8(kv.Value.Item2 * 256 + 128, 16), Jp2ExtendedColour.ESRgbToSRgb8(kv.Value.Item3 * 256 + 128, 16));
            Assert.True(Math.Abs(got.Item1 - expected.Item1) <= 2 && Math.Abs(got.Item2 - expected.Item2) <= 2 && Math.Abs(got.Item3 - expected.Item3) <= 2,
                $"({kv.Key.Item1},{kv.Key.Item2}): expected {expected} (the e-sRGB transform of the reference {kv.Value}), got {got}");
            Assert.True(Math.Abs(got.Item1 - kv.Value.Item1) > 4, $"({kv.Key.Item1},{kv.Key.Item2}): {got} is the untransformed reading {kv.Value}");
        }

        Assert.True(JpxImageDecoder.TryDecodeSamples(data, null, null, out JpxPdfImage image, out string reason), reason);
        Assert.Equal(Jp2ColourSpaceKind.ESRgb, image.Plan.ColourSpace);
        Assert.Equal(new[] { 16, 16, 16 }, image.Plan.ColourChannelDepths);
        Assert.Null(image.IccProfile);
    }

    /// <summary>file6's 12-bit grey narrows by the one rule — a shift of four — to the reference decoder's samples shifted alike.</summary>
    [JpxCorpusFact]
    public void TwelveBitGrey_File6_NarrowsByTheShift()
    {
        RenderedPage page = RenderFile("file6.jp2", "", out byte[] data);
        Assert.Equal((768, 512), (page.Width, page.Height));
        byte[] rgb = Rgb(page);
        var grey = new byte[page.Width * page.Height];
        for (int i = 0; i < grey.Length; i++)
        {
            Assert.True(rgb[i * 3] == rgb[i * 3 + 1] && rgb[i * 3] == rgb[i * 3 + 2], $"pixel {i} is not grey");
            grey[i] = rgb[i * 3];
        }
        Assert.Equal("e0f1b55216eef5e06a1e164ff3a590c5887706f4687148e5d8f7e2530a13e121", Sha256(grey));

        Assert.True(JpxImageDecoder.TryDecodeSamples(data, null, null, out JpxPdfImage image, out string reason), reason);
        Assert.Equal(12, image.Plan.ColourChannelDepths[0]);
    }

    /// <summary>
    /// file8's only colour specification is a grey ICC profile, which this assembly's reader
    /// accepts: the plan chooses it and the samples are transformed through it. The rendered grey
    /// is the ICC provider's transform of the raw sample, and not the raw sample.
    /// </summary>
    [JpxCorpusFact]
    public void IccGrey_File8_IsTransformedThroughTheProfile()
    {
        RenderedPage page = RenderFile("file8.jp2", "", out byte[] data);
        Assert.Equal((700, 400), (page.Width, page.Height));

        Assert.True(JpxImageDecoder.TryDecodeSamples(data, null, null, out JpxPdfImage image, out string reason), reason);
        Assert.Equal((Jp2ColourSpaceKind.IccProfile, JpxColourModel.Gray, 1), (image.Plan.ColourSpace, image.ColourModel, image.ColourComponents));
        Assert.NotNull(image.IccProfile);

        // The raw samples through the provider, as the decoder applies it.
        using var raw = new SKBitmap(page.Width, page.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < page.Height; y++)
            for (int x = 0; x < page.Width; x++)
            {
                byte g = image.Colour[y * page.Width + x];
                raw.SetPixel(x, y, new SKColor(g, g, g));
            }
        Assert.True(IccTransform.Current.TransformToSRgb(image.IccProfile, raw), "the provider declined the grey profile");

        byte[] rgb = Rgb(page);
        int moved = 0, mismatched = 0;
        for (int y = 0; y < page.Height; y++)
            for (int x = 0; x < page.Width; x++)
            {
                int i = y * page.Width + x;
                SKColor expected = raw.GetPixel(x, y);
                if (Math.Abs(rgb[i * 3] - expected.Red) > 1) mismatched++;
                if (rgb[i * 3] != image.Colour[i]) moved++;
            }
        Assert.Equal(0, mismatched);
        Assert.True(moved > page.Width * page.Height / 2, $"only {moved} samples were changed by the profile");
    }

    /// <summary>
    /// p0_04, a raw codestream (9-7, ICT) with the dictionary's /ColorSpace /DeviceRGB: each
    /// channel against its class-1 reference within T.803 Table C.1's peak error (5, 4, 6).
    /// </summary>
    [JpxCorpusFact]
    public void RawCodestream_P0_04_WithDeviceRgb_MatchesTheClassOneReferences()
    {
        RenderedPage page = RenderFile("p0_04.j2k", "/ColorSpace /DeviceRGB /BitsPerComponent 8", out _);
        Assert.Equal((640, 480), (page.Width, page.Height));
        byte[] rgb = Rgb(page);
        int[] limits = { 5, 4, 6 };
        for (int c = 0; c < 3; c++)
        {
            byte[] reference = File.ReadAllBytes(Reference($"c1p0_04_{c}.pgx"));
            Assert.True(PgxHeader.TryRead(reference, out PgxHeader? h));
            int[] samples = h.ReadSamples(reference);
            Assert.Equal(page.Width * page.Height, samples.Length);
            int peak = 0;
            for (int i = 0; i < samples.Length; i++) peak = Math.Max(peak, Math.Abs(rgb[i * 3 + c] - samples[i]));
            Assert.True(peak <= limits[c], $"component {c}: peak error {peak}, limit {limits[c]}");
        }
    }
}
