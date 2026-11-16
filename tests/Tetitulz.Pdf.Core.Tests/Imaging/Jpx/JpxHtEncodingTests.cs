// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Imaging.Jpx;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// HTJ2K encoding (<see cref="JpxEncodingOptions.HighThroughput"/>): whole codestreams of HT
/// code-blocks, read back by this library's decoder and — the oracle that is not the writer — by
/// the reference decoder through an independent tool, sample for sample when lossless; the codestream's declarations
/// (Rsiz bit 14, CAP's Ccap15, the code-block style) and the JPH container (T.814 Annex D).
/// </summary>
public sealed partial class JpxHtEncodingTests
{
    private static byte[] Interleave(int[][] planes, int bitsPerComponent)
    {
        int components = planes.Length, count = planes[0].Length;
        var samples = new byte[count * components * (bitsPerComponent > 8 ? 2 : 1)];
        int at = 0;
        for (int i = 0; i < count; i++)
            for (int c = 0; c < components; c++)
            {
                if (bitsPerComponent > 8) samples[at++] = (byte)(planes[c][i] >> 8);
                samples[at++] = (byte)planes[c][i];
            }
        return samples;
    }

    private static JpxEncodingOptions Ht(bool lossless = true, bool container = false, int quality = 90)
        => new JpxEncodingOptions { HighThroughput = true, Lossless = lossless, Jp2Container = container, Quality = quality };

    private static int[] OurPlane(byte[] codestream, int component)
    {
        Assert.True(JpxDecoder.TryDecode(codestream, 0, out JpxDecodeResult decoded, out string reason), reason);
        return decoded.Image!.Samples[component];
    }

    /// <summary>
    /// What an HT codestream declares: Rsiz bit 14 (§A.2), a CAP with Pcap15 and a Ccap15 saying HTONLY,
    /// one HT set, reversible (§A.3), and the HT code-block style — read back through the parser and
    /// the header reader a PDF/A check uses.
    /// </summary>
    [Fact]
    public void AnHtCodestream_DeclaresItself()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(40, 30, 1, 3);
        byte[] j2k = JpxImageEncoding.Encode(Interleave(planes, 8), 40, 30, 1, 8, Ht());

        Assert.True(JpxCodestreamParser.TryParse(j2k, out JpxCodestream cs, out string reason), reason);
        Assert.Equal(JpxEncoder.RsizHighThroughput, cs.Size.Rsiz & JpxEncoder.RsizHighThroughput);
        Assert.True(cs.HtCapabilities >= 0);
        Assert.Equal(0, cs.HtCapabilities & 0xF820);   // HTONLY, SINGLEHT, RGNFREE, HOMOGENEOUS, HTREV
        Assert.NotEqual(JpxCodeBlockStyle.None, cs.Main.Coding.Component.CodeBlockStyle & JpxCodeBlockStyle.HighThroughput);
        Assert.True(JpxHeader.TryReadHighThroughput(j2k, out bool ht));
        Assert.True(ht);
    }

    /// <summary>An irreversible HT codestream sets Ccap15 bit 5 (HTIRV, §A.3.6); a reversible one does not.</summary>
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 0x20)]
    public void Ccap15_Bit5_SaysWhetherTheTransformIsIrreversible(bool lossless, int bit5)
    {
        int[][] planes = JpxEncoderTestImages.Pattern(32, 32, 3, 5);
        byte[] j2k = JpxImageEncoding.Encode(Interleave(planes, 8), 32, 32, 3, 8, Ht(lossless));
        Assert.True(JpxCodestreamParser.TryParse(j2k, out JpxCodestream cs, out string reason), reason);
        Assert.Equal(bit5, cs.HtCapabilities & 0x20);
    }

    /// <summary>
    /// The magnitude bound P of Ccap15 bits 0 to 4 (§A.3.7) is the smallest whose B covers the planes:
    /// B = 8 at P = 0, P + 8 below 20, 4(P − 19) + 27 from 20 to 30.
    /// </summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(8, 0)]
    [InlineData(9, 1)]
    [InlineData(27, 19)]
    [InlineData(28, 20)]
    [InlineData(31, 20)]
    [InlineData(32, 21)]
    [InlineData(38, 22)]
    public void TheMagnitudeBound_IsTheSmallestThatCovers(int planes, int p)
        => Assert.Equal(p, JpxEncoder.MagnitudeBoundParameter(planes));

    /// <summary>The Part 1 encode is untouched: no CAP, Rsiz 0, a 'jp2 ' brand, the Part 1 code-block style.</summary>
    [Fact]
    public void WithoutHighThroughput_NothingChanges()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(30, 20, 3, 41);
        byte[] jp2 = JpxImageEncoding.Encode(Interleave(planes, 8), 30, 20, 3, 8, null);
        Assert.Equal("jp2 ", Encoding.ASCII.GetString(jp2, 20, 4));
        byte[] j2k = JpxImageEncoding.Encode(Interleave(planes, 8), 30, 20, 3, 8, new JpxEncodingOptions { Jp2Container = false });
        Assert.True(JpxCodestreamParser.TryParse(j2k, out JpxCodestream cs, out string reason), reason);
        Assert.Equal(0, cs.Size.Rsiz);
        Assert.Equal(-1, cs.HtCapabilities);
        Assert.Equal(JpxCodeBlockStyle.None, cs.Main.Coding.Component.CodeBlockStyle);
    }

    /// <summary>
    /// A JPH file enumerates no CMYK (T.814 Annex D), so an HT CMYK image in a container is refused with
    /// the reason, and the same image encodes as a raw codestream.
    /// </summary>
    [Fact]
    public void ACmykJphFile_IsRefused_AndTheCodestreamIsNot()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(16, 16, 4, 2);
        byte[] cmyk = Interleave(planes, 8);
        Assert.Contains("CMYK", Assert.Throws<ArgumentException>(() => JpxImageEncoding.Encode(cmyk, 16, 16, 4, 8, Ht(container: true))).Message);
        Assert.True(JpxHeader.IsRawCodestream(JpxImageEncoding.Encode(cmyk, 16, 16, 4, 8, Ht())));
    }

    // ------------------------------------------------------------------ rate control (JpxHtRateControl)

    private static double Psnr(int[] a, int[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++) { double d = a[i] - b[i]; sum += d * d; }
        return sum == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255 / (sum / a.Length));
    }

    /// <summary>A photograph-like image: a gradient under noise, so a byte saved is quality lost.</summary>
    private static int[][] Noisy(int w, int h, int seed)
    {
        var random = new Random(seed);
        var plane = new int[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                plane[y * w + x] = Math.Min(255, (x + y) * 2 + random.Next(40));
        return new[] { plane };
    }

    /// <summary>
    /// An earlier layer meets its own target, not only the file its total: with two layers under a target
    /// the first gets half the packet share, and in layer-first order its packets are the first half of
    /// the tile's — their bytes, headers included, within that half, however many blocks' headers that is.
    /// </summary>
    [Fact]
    public void AnEarlierLayer_MeetsItsOwnTarget()
    {
        int[][] planes = Noisy(80, 64, 13);
        byte[] whole = JpxImageEncoding.Encode(Interleave(planes, 8), 80, 64, 1, 8, Ht());
        long target = whole.Length * 2 / 3;
        // 4 × 4 code-blocks: hundreds of them, so the headers are a real part of a layer and an
        // allocation that counted only the segments would overrun it.
        var options = new JpxEncodeOptions { HighThroughput = true, Layers = 2, TargetBytes = target, CodeBlockWidthExponent = 2, CodeBlockHeightExponent = 2 };
        byte[] j2k = JpxEncoder.EncodeLossless(planes, 80, 64, 8, false, options);

        Assert.True(JpxCodestreamParser.TryParse(j2k, out JpxCodestream cs, out string reason), reason);
        Assert.True(JpxTier2Decoder.TryDecodeTile(cs, 0, new Tetitulz.Pdf.Core.Util.WorkBudget(Tetitulz.Pdf.Core.Constants.PdfLimits.MaxJpxDecodeWork),
            out JpxTileDecode tile, out reason), reason);
        long packets = tile.PacketLengths.Sum();
        long share = target - j2k.Length + packets;   // the target less the main header, tile-part header and EOC
        long firstLayer = tile.PacketLengths.Take(tile.PacketLengths.Count / 2).Sum();
        Assert.True(firstLayer <= share / 2, $"the first layer took {firstLayer} bytes of its {share / 2}");
        Assert.True(firstLayer > 0);
    }

    /// <summary>
    /// A block's candidates carry its band's weight on both sides — the energy of leaving it out and the
    /// error each set leaves — so blocks of different bands compete in the samples' units; its layer
    /// priority is the error a set removes per byte that set costs; and the sets are a cleanup at every
    /// plane its magnitudes reach, the one-pass ones leaving what a cleanup alone leaves, and above plane 0
    /// the refined ones leaving less for more bytes.
    /// </summary>
    [Fact]
    public void ABlocksCandidates_AreWeighted_AndRankedPerByte()
    {
        var random = new Random(17);
        var coefficients = new long[8 * 8];
        for (int i = 0; i < coefficients.Length; i++) coefficients[i] = random.Next(-300, 300);
        const double weight = 3;
        JpxHtRateControl.Candidates c = JpxHtRateControl.Encode(coefficients, 8, 8, 10, weight);

        Assert.Equal(coefficients.Sum(q => (double)q * q) * weight, c.Energy, 6);
        int planes = JpxPacketEncoder.BitLength(coefficients.Max(q => Math.Abs(q)));
        Assert.Equal(Enumerable.Range(0, planes), c.Sets.Where(s => s.Passes == 1).Select(s => s.Plane));
        for (int k = 0; k < c.Sets.Length; k++)
        {
            JpxHtSet set = c.Sets[k];
            Assert.Equal(set.Remaining * weight, c.RemainingAt(k), 6);
            Assert.Equal((c.Energy - c.RemainingAt(k)) / set.Length, c.Priority(k), 6);
            if (set.Passes == 1) Assert.Equal(JpxHtBlockEncoder.RemainingDistortion(coefficients, set.Plane), set.Remaining, 6);
            else
            {
                JpxHtSet alone = c.Sets.Single(s => s.Plane == set.Plane && s.Passes == 1);
                Assert.True(set.Length > alone.Length && set.Remaining < alone.Remaining,
                    $"plane {set.Plane}, {set.Passes} passes: {set.Length} bytes leaving {set.Remaining}, against {alone.Length} leaving {alone.Remaining}");
            }
        }
        Assert.Contains(c.Sets, s => s.Passes == 3);
    }

    /// <summary>The blocks of a codestream's first tile that carry more than one pass: HT sets with refinement passes, read back through tier 2.</summary>
    private static int RefinedBlocks(byte[] j2k)
    {
        Assert.True(JpxCodestreamParser.TryParse(j2k, out JpxCodestream cs, out string reason), reason);
        Assert.True(JpxTier2Decoder.TryDecodeTile(cs, 0, new Tetitulz.Pdf.Core.Util.WorkBudget(Tetitulz.Pdf.Core.Constants.PdfLimits.MaxJpxDecodeWork),
            out JpxTileDecode tile, out reason), reason);
        Assert.True(tile.Complete, tile.StopReason);
        return tile.Layout.Components.SelectMany(c => c.Resolutions).SelectMany(r => r.Precincts).SelectMany(p => p.Bands)
            .SelectMany(b => b.Blocks).Count(b => b.Passes >= 2);
    }

    /// <summary>A picture of hard-edged 16-pixel squares under light noise: edges every wavelet band carries, so a third of a plane at a time pays.</summary>
    private static int[][] Squares(int w, int h, int seed)
    {
        var random = new Random(seed);
        var plane = new int[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                plane[y * w + x] = ((x / 16 + y / 16) % 2) * 200 + random.Next(20);
        return new[] { plane };
    }

    /// <summary>
    /// A size target chooses HT sets with refinement passes where they pay (§7.4, §7.5): the codestream holds
    /// blocks of two and three passes, and at the same target the picture is better than when the allocator
    /// may choose whole cleanup planes only — a refinement pass steps a third of a plane where a cleanup steps
    /// a whole one (measured 2026-10-14 on this image: 0.1 to 1.1 dB at each target). Without refinement no
    /// block has more than one pass. Either way the allocation spends the target: the bytes λ's choice leaves
    /// go to the blocks' best upgrades, so the file comes within 3% of it.
    /// </summary>
    [Fact]
    public void ASizeTarget_UsesRefinementPasses_AndGainsByThem()
    {
        int[][] planes = Squares(128, 96, 4);
        byte[] whole = JpxEncoder.EncodeLossless(planes, 128, 96, 8, false, new JpxEncodeOptions { HighThroughput = true });
        Assert.Equal(0, RefinedBlocks(whole));
        foreach (long target in new[] { whole.Length / 2L, whole.Length / 3L, whole.Length / 4L, whole.Length / 6L, whole.Length / 8L })
        {
            byte[] refined = JpxEncoder.EncodeLossless(planes, 128, 96, 8, false, new JpxEncodeOptions { HighThroughput = true, TargetBytes = target });
            byte[] cleanups = JpxEncoder.EncodeLossless(planes, 128, 96, 8, false,
                new JpxEncodeOptions { HighThroughput = true, TargetBytes = target, HtRefinementPasses = false });
            Assert.True(refined.Length <= target && cleanups.Length <= target, $"{refined.Length} and {cleanups.Length} bytes for a target of {target}");
            Assert.True(refined.Length >= target * 97 / 100 && cleanups.Length >= target * 97 / 100,
                $"{refined.Length} and {cleanups.Length} bytes leave much of a target of {target} unspent");
            Assert.True(RefinedBlocks(refined) > 0, $"no block was refined at {target} bytes");
            Assert.Equal(0, RefinedBlocks(cleanups));
            double gained = Psnr(planes[0], OurPlane(refined, 0)), alone = Psnr(planes[0], OurPlane(cleanups, 0));
            Assert.True(gained > alone, $"at {target} bytes refinement gave {gained:F2} dB, cleanups alone {alone:F2} dB");
        }
    }

    /// <summary>One layer declares one HT set: Ccap15 bit 13 stays 0 (SINGLEHT).</summary>
    [Fact]
    public void OneLayer_DeclaresOneSet()
    {
        int[][] planes = Noisy(32, 32, 9);
        byte[] j2k = JpxImageEncoding.Encode(Interleave(planes, 8), 32, 32, 1, 8, Ht());
        Assert.True(JpxCodestreamParser.TryParse(j2k, out JpxCodestream cs, out string reason), reason);
        Assert.Equal(0, cs.HtCapabilities & 0x2000);
    }

    /// <summary>
    /// A cleanup at a plane decodes to what that plane holds: each magnitude shifted down by it, then
    /// the decoder's form at that plane — (2μ + 1)·2<sup>p</sup> — and the error it leaves is what
    /// <see cref="JpxHtBlockEncoder.RemainingDistortion"/> says.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public void ACleanupAtAPlane_DecodesThatPlane(int plane)
    {
        var random = new Random(plane);
        var coefficients = new long[8 * 8];
        for (int i = 0; i < coefficients.Length; i++) coefficients[i] = random.Next(-500, 500);
        byte[] segment = JpxHtBlockEncoder.EncodeCleanupAtPlane(coefficients, 8, 8, plane);

        const int mb = 10;
        var block = new JpxCodeBlock(new JpxRect(0, 0, 8, 8)) { Included = true, ZeroBitPlanes = mb - 1 - plane };
        block.Add(new JpxCodeChunk(new JpxRange(0, segment.Length), 1, terminated: true));
        int[] decoded = JpxHtBlockDecoder.Decode(block, segment, JpxCodeBlockStyle.HighThroughput, mb, 0, out string problem);
        Assert.Null(problem);

        double error = 0;
        for (int i = 0; i < coefficients.Length; i++)
        {
            long q = coefficients[i], mu = Math.Abs(q) >> plane;
            long expected = mu == 0 ? 0 : Math.Sign(q) * ((2 * mu + 1) << plane);
            Assert.Equal(expected, decoded[i]);
            double reconstructed = mu == 0 ? 0 : (mu + 0.5) * (1 << plane);
            error += (Math.Abs(q) - reconstructed) * (Math.Abs(q) - reconstructed);
        }
        Assert.Equal(error, JpxHtBlockEncoder.RemainingDistortion(coefficients, plane), 6);
    }

    /// <summary>A block whose sets are the given (bytes, error left) pairs, the error with nothing coded <paramref name="energy"/>.</summary>
    private static (JpxCodeBlock Block, JpxHtRateControl.Candidates Candidates) FakeBlock(double energy, params (int Bytes, double Left)[] sets) =>
        (new JpxCodeBlock(new JpxRect(0, 0, 1, 1)),
         new JpxHtRateControl.Candidates(sets.Select(s => new JpxHtSet(0, new byte[s.Bytes], null, 0, 1, s.Left)).ToArray(), 1, energy, 10));

    /// <summary>
    /// The fill spends what λ's step left on the upgrades removing the most error per byte first: with room for
    /// one of two 10-byte upgrades it takes block A's, which removes 100, over block B's, which removes 50.
    /// </summary>
    [Fact]
    public void TheFill_TakesTheBestUpgradeFirst()
    {
        var a = FakeBlock(300, (10, 200), (20, 100));
        var b = FakeBlock(300, (10, 200), (20, 150));
        var keys = new List<JpxCodeBlock> { a.Block, b.Block };
        var candidates = new Dictionary<JpxCodeBlock, JpxHtRateControl.Candidates> { [a.Block] = a.Candidates, [b.Block] = b.Candidates };
        var choice = new[] { 0, 0 };

        long bytes = JpxHtRateControl.Fill(keys, candidates, choice, 20, 30);
        Assert.Equal((1, 0, 30L), (choice[0], choice[1], bytes));
    }

    /// <summary>
    /// A block whose best upgrade per byte does not fit is offered, on the next round, its best that does: block
    /// A's best (100 removed for 20 more bytes) does not fit the 5 bytes left; its other (10 for 4 more) does.
    /// </summary>
    [Fact]
    public void TheFill_OffersTheBestUpgradeThatFits()
    {
        var a = FakeBlock(300, (10, 200), (14, 190), (30, 100));
        var keys = new List<JpxCodeBlock> { a.Block };
        var candidates = new Dictionary<JpxCodeBlock, JpxHtRateControl.Candidates> { [a.Block] = a.Candidates };
        var choice = new[] { 0 };

        long bytes = JpxHtRateControl.Fill(keys, candidates, choice, 10, 15);
        Assert.Equal((1, 14L), (choice[0], bytes));
    }
    /// <summary>
    /// A block's candidates are coded causally when the encode asks (§7.4's neighbourhood stopping at a stripe's
    /// last line): on a block whose first stripe's last row is insignificant above a significant row, the SigProp
    /// pass of the plane-1 set codes that row only when the row below may be seen — so the causal candidate's
    /// SigProp is shorter, and the cleanups, which causality does not touch, are the same.
    /// </summary>
    [Fact]
    public void TheCandidates_AreCodedCausallyWhenTheEncodeAsks()
    {
        var q = new long[8 * 8];
        for (int x = 0; x < 8; x++)
        {
            q[3 * 8 + x] = 1;     // the first stripe's last row: significant at plane 0 only
            q[4 * 8 + x] = 100;   // the next stripe's first row: significant from plane 6
        }
        JpxHtSet[] plain = JpxHtRateControl.Encode(q, 8, 8, 8, 1, JpxHtRateControl.SetsCoded.All, verticallyCausal: false).Sets;
        JpxHtSet[] causal = JpxHtRateControl.Encode(q, 8, 8, 8, 1, JpxHtRateControl.SetsCoded.All, verticallyCausal: true).Sets;
        JpxHtSet Refined(JpxHtSet[] sets) => sets.Single(s => s.Plane == 1 && s.Passes == 2);
        Assert.True(Refined(causal).SigPropLength < Refined(plain).SigPropLength,
            $"SigProp {Refined(causal).SigPropLength} bytes causally, {Refined(plain).SigPropLength} without");
        Assert.Equal(plain.Where(s => s.Passes == 1).Select(s => s.Data()), causal.Where(s => s.Passes == 1).Select(s => s.Data()));
    }
}
