// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxDecoder.TryDecode"/> assembling the image at a reduced resolution: the planes are
/// ⌈· / 2^r⌉ of the image area (T.800 B-14's ceilings), every tile placed by its reduced bounds, a
/// sub-sampled component replicated on the reduced grid, and the reduction clamped to the fewest
/// levels the codestream has. The values are checked against an exact expectation — the 5-3
/// filter keeps a linear ramp's even samples unchanged, so a ramp decoded r levels short is the
/// ramp at every 2^r-th sample — and against the reference decoder's own reduced decode.
/// </summary>
public sealed partial class JpxDecoderReducedImageTests
{
    // Odd at every level (49 → 25 → 13 → 7; 33 → 17 → 9 → 5): the last sample of each row and column is then a
    // low-pass one whose mirrored high-pass neighbours are 0, which keeps the ramp exact at the boundary too.
    // An even length would end on a high-pass sample whose mirror is not linear, and the last low-pass
    // sample would be off by one.
    private const int Width = 49, Height = 33;

    /// <summary>a·x + b·y + offset over the image; 8-bit, so the callers keep it under 256.</summary>
    private static int[] Ramp(int a, int b, int offset) => Ramp(Width, Height, a, b, offset);

    private static int[] Ramp(int width, int height, int a, int b, int offset)
    {
        var plane = new int[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) plane[y * width + x] = a * x + b * y + offset;
        return plane;
    }

    private static int CeilShift(int v, int r) => (v + (1 << r) - 1) >> r;

    /// <summary>
    /// The 5-3 analysis high-pass of a linear sequence is 0 — the odd sample equals the floor of
    /// its neighbours' mean exactly, and the mirrored left end is linear too — so the low-pass keeps
    /// the even samples unchanged, and the image r levels short is the ramp at every 2^r-th sample
    /// in each direction, exactly, with the reduced dimensions the ceilings give.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void GreyRamp_ReducedByLevels_IsTheRampAtEveryPowerOfTwoSample(int reduction)
    {
        int[] ramp = Ramp(2, 3, 5);
        byte[] codestream = JpxEncoder.EncodeLossless(new[] { ramp }, Width, Height, 8, false, new JpxEncodeOptions { Levels = 3 });

        Assert.True(JpxDecoder.TryDecode(codestream, reduction, out JpxDecodeResult result, out string reason), reason);
        Assert.Empty(result.Deviations);
        Assert.Equal(reduction, result.Reduction);
        JpxDecodedImage image = result.Image;
        Assert.NotNull(image);
        Assert.Equal(reduction, image.Reduction);
        Assert.Equal((CeilShift(Width, reduction), CeilShift(Height, reduction)), (image.Width, image.Height));

        int[] expected = new int[image.Width * image.Height];
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++) expected[y * image.Width + x] = ramp[(y << reduction) * Width + (x << reduction)];
        Assert.Equal(expected, image.Samples[0]);

        // The control: at reduction 0 the image is the ramp itself, at its own size.
        Assert.True(JpxDecoder.TryDecode(codestream, 0, out JpxDecodeResult full, out reason), reason);
        Assert.Equal((0, Width, Height), (full.Image.Reduction, full.Image.Width, full.Image.Height));
        Assert.Equal(ramp, full.Image.Samples[0]);

        // Liveness: the reduced decode did less — the finer sub-bands' code-blocks were not decoded
        // and the wavelet stopped short — which the budget's evidence shows, not a clock.
        Assert.True(result.Budget.Spent < full.Budget.Spent, $"reduced {result.Budget.Spent} units, full {full.Budget.Spent}");
    }

    /// <summary>
    /// What a reduced decode skips: the finer sub-bands' code-blocks at tier 1 as well as the
    /// wavelet's finer levels. On a 256x256 noise image with three levels and 64-px blocks the
    /// level-1 bands hold twelve of the nineteen blocks, so one level short is well under half
    /// the full decode's budget — a decode that planned tier 1 at full resolution and reduced only
    /// the wavelet would still be above half, which the ramp's "less than the full" cannot see.
    /// </summary>
    [Fact]
    public void ReducedDecode_ChargesUnderHalfTheBudget_BecauseTierOneSkipsTheFinerBands()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(256, 256, 1, 7);
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 256, 256, 8, false, new JpxEncodeOptions { Levels = 3 });
        Assert.True(JpxDecoder.TryDecode(codestream, 0, out JpxDecodeResult full, out string reason), reason);
        Assert.True(JpxDecoder.TryDecode(codestream, 1, out JpxDecodeResult one, out reason), reason);
        Assert.True(JpxDecoder.TryDecode(codestream, 2, out JpxDecodeResult two, out reason), reason);
        Assert.Equal((128, 128), (one.Image.Width, one.Image.Height));
        Assert.True(one.Budget.Spent * 2 < full.Budget.Spent, $"one level short spent {one.Budget.Spent} of the full {full.Budget.Spent}");
        // Two levels short leaves the four level-3 blocks of the seven: less again, though no longer by half.
        Assert.True(two.Budget.Spent < one.Budget.Spent, $"two levels short spent {two.Budget.Spent}, one {one.Budget.Spent}");
    }

    /// <summary>
    /// The component transformation runs on the reduced planes: with the RCT, three ramps whose
    /// R + 2G + B is a multiple of 4 everywhere give a linear Y, U and V, so the reduced decode is
    /// again exact per component after the inverse transformation.
    /// </summary>
    [Fact]
    public void RgbRampWithTheRct_ReducedOneLevel_IsExactPerComponent()
    {
        int[][] planes = { Ramp(2, 3, 5), Ramp(2, 2, 30), Ramp(2, 1, 60) };   // R + 2G + B = 8x + 8y + 125 → Y = 2x + 2y + 31
        byte[] codestream = JpxEncoder.EncodeLossless(planes, Width, Height, 8, false, new JpxEncodeOptions { Levels = 2, MultipleComponentTransform = true });

        Assert.True(JpxDecoder.TryDecode(codestream, 1, out JpxDecodeResult result, out string reason), reason);
        Assert.True(result.Codestream.Main.Coding.MultipleComponentTransform, "the fixture must use the RCT");
        JpxDecodedImage image = result.Image;
        Assert.Equal((1, 25, 17), (image.Reduction, image.Width, image.Height));
        for (int c = 0; c < 3; c++)
        {
            int[] expected = new int[25 * 17];
            for (int y = 0; y < 17; y++)
                for (int x = 0; x < 25; x++) expected[y * 25 + x] = planes[c][2 * y * Width + 2 * x];
            Assert.Equal(expected, image.Samples[c]);
        }
    }

    /// <summary>
    /// A reduction past the codestream's levels is clamped to them — the result says which — and
    /// gives exactly the decode at that many levels; a codestream with no levels at all decodes at
    /// its own size whatever was asked.
    /// </summary>
    [Fact]
    public void Reduction_BeyondTheLevels_IsClampedToTheLevelsTheCodestreamHas()
    {
        int[] ramp = Ramp(1, 2, 10);
        byte[] two = JpxEncoder.EncodeLossless(new[] { ramp }, Width, Height, 8, false, new JpxEncodeOptions { Levels = 2 });
        Assert.True(JpxDecoder.TryDecode(two, 5, out JpxDecodeResult clamped, out string reason), reason);
        Assert.True(JpxDecoder.TryDecode(two, 2, out JpxDecodeResult asked, out reason), reason);
        Assert.Equal(2, clamped.Reduction);
        Assert.Equal((2, 13, 9), (clamped.Image.Reduction, clamped.Image.Width, clamped.Image.Height));
        Assert.Equal(asked.Image.Samples[0], clamped.Image.Samples[0]);
        Assert.Equal(asked.ComponentPlane(0, out JpxRect extent), clamped.ComponentPlane(0, out JpxRect clampedExtent));
        Assert.Equal(extent.ToString(), clampedExtent.ToString());

        byte[] none = JpxEncoder.EncodeLossless(new[] { ramp }, Width, Height, 8, false, new JpxEncodeOptions { Levels = 0 });
        Assert.True(JpxDecoder.TryDecode(none, 3, out JpxDecodeResult flat, out reason), reason);
        Assert.Equal((0, Width, Height), (flat.Reduction, flat.Image.Width, flat.Image.Height));
        Assert.Equal(ramp, flat.Image.Samples[0]);
    }

    /// <summary>
    /// Nine 20x12 tiles over an image at (3, 2)-(43, 26), one level: reduced by it, the area is
    /// (2, 1)-(22, 13) — 20x12 — and each tile lands by its reduced bounds (tile 0 at (2, 1)-(10, 6),
    /// the last at (20, 12)-(22, 13)). The image and the component plane agree, and both are what
    /// the reference decoder decodes at the same reduction.
    /// </summary>
    [Fact]
    public void TiledImageWithAnOffset_ReducedOneLevel_PlacesEveryTileByItsReducedBounds()
    {
        JpxLosslessFixtures.Fixture fixture = JpxLosslessFixtures.Gray40x24_Tiles20x12_Offset3x2_OneLevel;
        Assert.True(JpxDecoder.TryDecode(fixture.Codestream, 1, out JpxDecodeResult result, out string reason), reason);
        Assert.Empty(result.Deviations);
        Assert.Equal(9, result.Tiles.Count);
        Assert.Equal("(2, 1)-(10, 6)", result.Tiles[0].Components[0].Bounds.ToString());
        Assert.Equal("(20, 12)-(22, 13)", result.Tiles[8].Components[0].Bounds.ToString());

        JpxDecodedImage image = result.Image;
        Assert.Equal((1, 20, 12), (image.Reduction, image.Width, image.Height));
        int[] plane = result.ComponentPlane(0, out JpxRect extent);
        Assert.Equal("(2, 1)-(22, 13)", extent.ToString());
        Assert.Equal(plane, image.Samples[0]);

        // Potency: the reduced image is not the full one — the full decode has four times the samples, and
        // the reduced one's first row is not the source's first row.
        Assert.True(JpxDecoder.TryDecode(fixture.Codestream, 0, out JpxDecodeResult full, out reason), reason);
        Assert.Equal(40 * 24, full.Image.Samples[0].Length);
        Assert.NotEqual(full.Image.Samples[0].Take(20).ToArray(), image.Samples[0].Take(20).ToArray());
    }

    /// <summary>
    /// A sub-sampled component on the reduced grid: a4_colr's 17x7 separations, b2_mono's 5x3,
    /// a6_mono_colr's 11x5 and 13x6. Each reduced grid position takes the component sample at
    /// (⌊x / XRsiz⌋, ⌊y / YRsiz⌋) clamped into the component's reduced extent — the class rule of
    /// <see cref="JpxDecodedImage"/>, applied to the reduced area exactly as to the full one — and
    /// the reduced extent is the full one's ceilings by 2.
    /// </summary>
    [JpxCorpusTheory]
    [InlineData("a4_colr")]
    [InlineData("b2_mono")]
    [InlineData("a6_mono_colr")]
    public void SubSampledComponents_ReducedOneLevel_AreReplicatedOnTheReducedGrid(string stem)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestCorpus.JpxRoot!, "input", "conformance", stem + ".j2c"));
        Assert.True(JpxDecoder.TryDecode(data, 1, out JpxDecodeResult result, out string reason), reason);
        Assert.True(JpxDecoder.TryDecode(data, 0, out JpxDecodeResult full, out reason), reason);
        JpxImageSize siz = result.Codestream.Size;
        Assert.Contains(siz.Components, c => c.XRsiz > 1 || c.YRsiz > 1);
        JpxDecodedImage image = result.Image;
        long ax0 = (siz.XOsiz + 1) >> 1, ay0 = (siz.YOsiz + 1) >> 1, ax1 = (siz.Xsiz + 1) >> 1, ay1 = (siz.Ysiz + 1) >> 1;
        Assert.Equal((1, (int)(ax1 - ax0), (int)(ay1 - ay0)), (image.Reduction, image.Width, image.Height));

        for (int c = 0; c < siz.Components.Length; c++)
        {
            int[] plane = result.ComponentPlane(c, out JpxRect extent);
            full.ComponentPlane(c, out JpxRect fullExtent);
            Assert.Equal(new JpxRect((fullExtent.X0 + 1) >> 1, (fullExtent.Y0 + 1) >> 1, (fullExtent.X1 + 1) >> 1, (fullExtent.Y1 + 1) >> 1).ToString(), extent.ToString());
            int xr = siz.Components[c].XRsiz, yr = siz.Components[c].YRsiz;
            int[] expected = new int[image.Width * image.Height];
            for (long gy = ay0; gy < ay1; gy++)
            {
                long cy = Math.Min(Math.Max(gy / yr, extent.Y0), extent.Y1 - 1) - extent.Y0;
                for (long gx = ax0; gx < ax1; gx++)
                {
                    long cx = Math.Min(Math.Max(gx / xr, extent.X0), extent.X1 - 1) - extent.X0;
                    expected[(gy - ay0) * image.Width + (gx - ax0)] = plane[cy * extent.Width + cx];
                }
            }
            Assert.Equal(expected, image.Samples[c]);
        }
    }

    /// <summary>
    /// Every profile codestream's image at one level short agrees with its component planes, with
    /// the replication for a sub-sampled one — the same rule at reduction 0, where the planes are
    /// held to the class-1 references. (The image is what a PDF consumer reads; the plane is what
    /// the conformance comparison reads.)
    /// </summary>
    [JpxCorpusTheory]
    [MemberData(nameof(JpxDecoderConformanceTests.ProfileCodestreams), MemberType = typeof(JpxDecoderConformanceTests))]
    public void ProfileCodestreams_ReducedOneLevel_ImageAgreesWithTheComponentPlanes(string stem)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(TestCorpus.JpxRoot!, "input", "conformance", stem + ".j2k"));
        Assert.True(JpxDecoder.TryDecode(data, 1, out JpxDecodeResult result, out string reason), reason);
        // p0_11 has no decomposition levels: the reduction is clamped to 0 and the image is the full one.
        int r = stem == "p0_11" ? 0 : 1;
        Assert.Equal(r, result.Reduction);
        JpxDecodedImage image = result.Image;
        Assert.NotNull(image);
        JpxImageSize siz = result.Codestream.Size;
        long ax0 = (siz.XOsiz + (1 << r) - 1) >> r, ay0 = (siz.YOsiz + (1 << r) - 1) >> r, ax1 = (siz.Xsiz + (1 << r) - 1) >> r, ay1 = (siz.Ysiz + (1 << r) - 1) >> r;
        Assert.Equal(((int)(ax1 - ax0), (int)(ay1 - ay0)), (image.Width, image.Height));
        int compared = 0;
        for (int c = 0; c < image.Components.Length; c++)
        {
            int[] plane = result.ComponentPlane(c, out JpxRect extent);
            int xr = image.Components[c].XRsiz, yr = image.Components[c].YRsiz;
            for (long gy = ay0; gy < ay1; gy++)
            {
                long cy = Math.Min(Math.Max(gy / yr, extent.Y0), extent.Y1 - 1) - extent.Y0;
                for (long gx = ax0; gx < ax1; gx++)
                {
                    long cx = Math.Min(Math.Max(gx / xr, extent.X0), extent.X1 - 1) - extent.X0;
                    if (plane[cy * extent.Width + cx] != image.Samples[c][(gy - ay0) * image.Width + (gx - ax0)])
                        Assert.Fail($"{stem} component {c} at reduced grid ({gx}, {gy}): image {image.Samples[c][(gy - ay0) * image.Width + (gx - ax0)]}, plane {plane[cy * extent.Width + cx]}");
                    compared++;
                }
            }
        }
        Assert.True(compared > 0);
    }
}
