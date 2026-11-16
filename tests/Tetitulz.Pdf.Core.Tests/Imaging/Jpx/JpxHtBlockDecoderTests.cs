// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.Core.Imaging.Jpx;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxHtBlockDecoder"/> on code-blocks built by hand from ITU-T T.814 clause 7, for the
/// paths no corpus file reaches: vertically causal SigProp, a second HT set, placeholder passes,
/// and malformed segments. The corpora (<see cref="JpxHtConformanceTests"/>, <see cref="JpxHtOpenJpegTests"/>)
/// cover the cleanup, SigProp and MagRef passes at scale.
/// </summary>
/// <remarks>
/// <para><b>The cleanup segment</b> is a 2 × 8 block (QW = 1, QH = 4) with one significant sample,
/// (0, 4), worked through the clause by hand:</para>
/// <list type="bullet">
/// <item>Every quad has context 0 (its neighbours are insignificant), so each is an AZC quad and
/// takes a MEL symbol: 0, 0, 1, 0 for quad rows 0 to 3. From MEL state k = 0 (MEL_E 0 throughout):
/// bit 1 (a run of 1: symbol 0), bit 1 (symbol 0), bit 0 (no run: symbol 1), bit 1 (symbol 0) —
/// MEL byte <c>1101 1111</c> = 0xDF.</item>
/// <item>Quad row 2's symbol 1 is followed by its CxtVLC codeword: CxtVLC_table_1, context 0,
/// ρ = 1 (sample j = 0), u<sup>off</sup> = 0 is codeword 0, 3 bits — the VLC stream starts in the
/// upper nibble of the second-last byte (§7.1.4), so that nibble is 0.</item>
/// <item>u = 0 and a non-initial quad with one significant sample: κ = 1 (γ = 0), U = 1, one MagSgn
/// bit: 0, so v = 0, μ = 1, positive. MagSgn byte 0x00.</item>
/// <item>Layout [MagSgn 0x00][MEL 0xDF][0x03][0x00]: Scup = (0x00 · 16) + (0x03 &amp; 0xF) = 3, so
/// Pcup = 1 (§7.1.1).</item>
/// </list>
/// <para>With M<sub>b</sub> = 8 and P = 5 zero bit-planes, S<sub>blk</sub> = 5 and the cleanup's plane
/// is 8 − 1 − 5 = 2: μ = 1 is written (2μ + 1) · 2² = 12 (twice the magnitude with the half,
/// <see cref="JpxBlockDecoder"/>'s form). A sample SigProp makes significant at plane 1, negative, is
/// −(3 · 2¹) = −6.</para>
/// </remarks>
public sealed class JpxHtBlockDecoderTests
{
    private const int Width = 2, Height = 8, Mb = 8, ZeroPlanes = 5;
    private static readonly byte[] OneSample = { 0x00, 0xDF, 0x03, 0x00 };

    /// <summary>SigProp bits, all 1 (a byte after 0xFF carries a 0 stuffing bit, §7.1.5).</summary>
    private static readonly byte[] AllOnes = { 0xFF, 0x7F, 0xFF, 0x7F };

    private static int At(int[] v, int x, int y) => v[y * Width + x];

    /// <summary>A block of <paramref name="chunks"/>: each its bytes and pass count, laid end to end in one buffer.</summary>
    private static (JpxCodeBlock Block, byte[] Data) Block(int placeholderPasses, params (byte[] Bytes, int Passes)[] chunks)
    {
        var data = chunks.SelectMany(c => c.Bytes).ToArray();
        var block = new JpxCodeBlock(new JpxRect(0, 0, Width, Height))
        {
            Included = true, ZeroBitPlanes = ZeroPlanes, HtPlaceholderPasses = placeholderPasses,
        };
        long pos = 0;
        foreach (var (bytes, passes) in chunks)
        {
            block.Add(new JpxCodeChunk(new JpxRange(pos, pos + bytes.Length), passes, terminated: true));
            pos += bytes.Length;
        }
        return (block, data);
    }

    private static int[] Decode(JpxCodeBlockStyle style, int placeholderPasses, out string problem, params (byte[] Bytes, int Passes)[] chunks)
    {
        var (block, data) = Block(placeholderPasses, chunks);
        return JpxHtBlockDecoder.Decode(block, data, JpxCodeBlockStyle.HighThroughput | style, Mb, 0, out problem);
    }

    [Fact]
    public void TheHandBuiltCleanupSegmentDecodesItsOneSample()
    {
        int[] v = Decode(0, 0, out string problem, (OneSample, 1));
        Assert.Null(problem);
        Assert.Equal(12, At(v, 0, 4));
        Assert.Equal(15, v.Count(s => s == 0));
    }

    /// <summary>
    /// SigProp (§7.4) with every bit 1: the sample significant at (0, 4) makes its neighbours on the
    /// first stripe's last row, (0, 3) and (1, 3), candidates; each takes a bit (1) and a sign (1).
    /// Through the scan-causal refinement values every remaining sample of the second stripe follows.
    /// With the vertically causal style the first stripe's samples cannot see the second stripe
    /// (their neighbourhood stops at the stripe), so they take no bit and stay 0, while the second
    /// stripe decodes as before — the one difference the style makes.
    /// </summary>
    [Fact]
    public void VerticallyCausalSigPropDoesNotLookIntoTheNextStripe()
    {
        int[] open = Decode(0, 0, out string p1, (OneSample, 1), (AllOnes, 1));
        int[] causal = Decode(JpxCodeBlockStyle.VerticallyCausal, 0, out string p2, (OneSample, 1), (AllOnes, 1));
        Assert.Null(p1);
        Assert.Null(p2);

        Assert.Equal(-6, At(open, 0, 3));
        Assert.Equal(-6, At(open, 1, 3));
        Assert.Equal(0, At(causal, 0, 3));
        Assert.Equal(0, At(causal, 1, 3));
        Assert.Equal(0, At(open, 0, 2));   // two rows away from anything significant

        for (int y = 4; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int expected = x == 0 && y == 4 ? 12 : -6;   // the cleanup's sample is not refined by SigProp
                Assert.Equal(expected, At(open, x, y));
                Assert.Equal(expected, At(causal, x, y));
            }
    }

    /// <summary>
    /// MagRef (§7.5): with three passes the refinement segment's last byte, read backwards, gives each
    /// sample the cleanup made significant one refinement bit. The SigProp bits at its start are all 0
    /// (byte 0x00: the five candidates around (0, 4) stay insignificant). Last byte 0x01: the sample's
    /// bit is 1, its magnitude becomes 2μ + 1 = 3 at plane 1 — (2 · 3 + 1) · 2 = 14; last byte 0x00 (the
    /// control): bit 0, 2μ = 2 — (2 · 2 + 1) · 2 = 10. Without the MagRef pass it would stay 12.
    /// </summary>
    [Theory]
    [InlineData(0x01, 14)]
    [InlineData(0x00, 10)]
    public void MagRefRefinesEachCleanupSignificantSample(byte last, int expected)
    {
        int[] v = Decode(0, 0, out string problem, (OneSample, 1), (new byte[] { 0x00, last }, 2));
        Assert.Null(problem);
        Assert.Equal(expected, At(v, 0, 4));
        Assert.Equal(15, v.Count(s => s == 0));
    }

    /// <summary>A <paramref name="w"/> × <paramref name="h"/> block of the given chunks, decoded with M<sub>b</sub> = 8 and P = 5 (the cleanup at plane 2).</summary>
    private static int[] DecodeBlock(int w, int h, out string problem, params (byte[] Bytes, int Passes)[] chunks)
    {
        var data = chunks.SelectMany(c => c.Bytes).ToArray();
        var block = new JpxCodeBlock(new JpxRect(0, 0, w, h)) { Included = true, ZeroBitPlanes = ZeroPlanes };
        long pos = 0;
        foreach (var (bytes, passes) in chunks)
        {
            block.Add(new JpxCodeChunk(new JpxRange(pos, pos + bytes.Length), passes, terminated: true));
            pos += bytes.Length;
        }
        return JpxHtBlockDecoder.Decode(block, data, JpxCodeBlockStyle.HighThroughput, Mb, 0, out problem);
    }

    /// <summary>
    /// A long MEL run (§7.3.3): a 2 × 172 block — 86 quads in one column, all of context 0 — with one
    /// significant sample, j = 0 of quad 85, so 85 MEL symbols of 0 then a 1. Coding them walks MEL_k up
    /// to its cap of 12 (runs 1, 1, 1, 2, 2, 2, 4, 4, 4, 8, 8, 16, then 32 at MEL_E[12] = 5) and puts eight
    /// 1-bits in the first MEL byte, 0xFF, so the next byte holds seven bits. Written by a model of the
    /// clause 7 writers (a MEL run-length coder that decodeMELSym inverts) and checked against its readers.
    /// A cap of 11 would read the same bits as significance at quads 69 and 77.
    /// </summary>
    [Fact]
    public void ALongMelRunReachesTheExponentCapAndAStuffedByte()
    {
        byte[] cleanup = { 0x00, 0xFF, 0x7C, 0x0F, 0x05, 0x00 };
        int[] v = DecodeBlock(2, 172, out string problem, (cleanup, 1));
        Assert.Null(problem);
        Assert.Equal(12, v[170 * 2]);
        Assert.Equal(2 * 172 - 1, v.Count(s => s == 0));
    }

    /// <summary>
    /// MagRef's first byte (§7.1.6): MR_last starts at 0xFF, so a last byte whose seven low bits are all
    /// 1 gives seven bits and the eighth comes from the byte below it. The block is 2 × 4 and wholly
    /// significant (every sample μ = 1, so SigProp has no candidate); refinement bytes [0x01, 0x7F] give
    /// all eight samples a MagRef bit of 1 — (2 · 3 + 1) · 2 = 14 each — the eighth, (1, 3), from 0x01's
    /// low bit. Were MR_last to start at 0, 0x7F would give eight bits, the eighth its stuffing 0, and
    /// (1, 3) would be 10.
    /// </summary>
    [Fact]
    public void MagRefsFirstByteSkipsItsStuffingBit()
    {
        byte[] cleanup = { 0x00, 0x7F, 0xD5, 0xB4, 0x00 };   // all eight significant, μ = 1, positive
        int[] cleanupOnly = DecodeBlock(2, 4, out string p1, (cleanup, 1));
        Assert.Null(p1);
        Assert.All(cleanupOnly, s => Assert.Equal(12, s));

        int[] refined = DecodeBlock(2, 4, out string p2, (cleanup, 1), (new byte[] { 0x01, 0x7F }, 2));
        Assert.Null(p2);
        Assert.All(refined, s => Assert.Equal(14, s));
    }

    /// <summary>
    /// Two HT sets (§B.1): the second re-codes the block one bit-plane further down, so it is the
    /// one decoded (S<sub>skip</sub> = 1, §B.3) — the same μ = 1 now at plane 1: 3 · 2 = 6 — and the
    /// first set's SigProp bits, which would refine its neighbours, are not used.
    /// </summary>
    [Fact]
    public void TheLastHtSetWithCleanupBytesIsTheOneDecoded()
    {
        int[] v = Decode(0, 0, out string problem, (OneSample, 1), (AllOnes, 2), (OneSample, 1));
        Assert.Null(problem);
        Assert.Equal(6, At(v, 0, 4));
        Assert.Equal(15, v.Count(s => s == 0));
    }

    /// <summary>A later set whose cleanup segment is empty (§B.3's Z_blk = 0) leaves the earlier set the decoded one.</summary>
    [Fact]
    public void AnEmptyLaterSetDoesNotReplaceTheOneBeforeIt()
    {
        int[] v = Decode(0, 0, out string problem, (OneSample, 1), (new byte[0], 2), (new byte[0], 1));
        Assert.Null(problem);
        Assert.Equal(12, At(v, 0, 4));
    }

    /// <summary>
    /// Three placeholder passes (P0 = 1, §B.1) carry no bytes and count as a skipped bit-plane:
    /// S<sub>blk</sub> = P + P0 = 6, so the sample lands at plane 1 — and their passes come before
    /// the cleanup pass in the chunk, not after it.
    /// </summary>
    [Fact]
    public void PlaceholderPassesSkipABitPlaneEach()
    {
        int[] v = Decode(0, 3, out string problem, (OneSample, 4));
        Assert.Null(problem);
        Assert.Equal(6, At(v, 0, 4));
    }

    /// <summary>
    /// The ROI scaling (T.800 §H.1, applied after HT decoding, T.814 §7.6) is the block decoder's:
    /// with an ROI shift of 2 over M<sub>b</sub> = 6 the band has the same 8 planes, so the sample is
    /// again 12 — at or above the 2<sup>2 + 1</sup> threshold, so it is ROI and scaled down to 3.
    /// </summary>
    [Fact]
    public void AnRoiSampleIsScaledBackAsThePartOneDecoderScalesIt()
    {
        var (block, data) = Block(0, (OneSample, 1));
        int[] v = JpxHtBlockDecoder.Decode(block, data, JpxCodeBlockStyle.HighThroughput, Mb - 2, 2, out string problem);
        Assert.Null(problem);
        Assert.Equal(3, At(v, 0, 4));
    }

    /// <summary>
    /// Single-quad (2 × 2) cleanup segments with a U-VLC residual, written by a model of clause 7's
    /// writers and checked against its readers (they recover every bit). Quad 0 has context 0: MEL bit
    /// 0 (symbol 1), then CxtVLC_table_0's {ρ 1, u<sup>off</sup> 1, ϵ<sup>k</sup> 1, ϵ<sup>1</sup> 1}, codeword
    /// 0x3F in 7 bits. <c>Bound</c>: U-VLC prefix 000 (5), suffix 11111 (31, so an extension), extension
    /// 1111 (15) — u = 5 + 31 + 4 · 15 = 96, U = 97. <c>Magnitude</c>: prefix 1 — u = 1, U = 2, so
    /// m = U − k = 1 MagSgn bit (0) under the known 1: v = 2, μ = 2.
    /// </summary>
    private static readonly byte[] Bound = { 0x7F, 0xFF, 0x87, 0x75, 0x00 };
    private static readonly byte[] Magnitude = { 0x00, 0x7F, 0x17, 0x74, 0x00 };

    private static int[] DecodeQuad(byte[] cleanup, int zeroPlanes, out string problem, int mb = Mb)
    {
        var block = new JpxCodeBlock(new JpxRect(0, 0, 2, 2)) { Included = true, ZeroBitPlanes = zeroPlanes };
        block.Add(new JpxCodeChunk(new JpxRange(0, cleanup.Length), 1, terminated: true));
        return JpxHtBlockDecoder.Decode(block, cleanup, JpxCodeBlockStyle.HighThroughput, mb, 0, out problem);
    }

    /// <summary>
    /// An exponent bound past <see cref="JpxHtBlockDecoder.MaxExponentBound"/> would read more MagSgn
    /// bits than a <c>long</c> holds: refused before any is read, the block zero.
    /// </summary>
    [Fact]
    public void AnExponentBoundPastWhatALongHoldsIsRefused()
    {
        int[] v = DecodeQuad(Bound, 0, out string problem);
        Assert.Contains("exponent bound is 97", problem);
        Assert.All(v, s => Assert.Equal(0, s));
    }

    /// <summary>
    /// μ = 2 needs two magnitude bits; with no skipped plane the cleanup pass has one (S<sub>blk</sub>
    /// + 1, §7.6), so the sample is past the band: refused, the block zero. With one skipped plane
    /// (the control) it fits — at plane 8 − 1 − 1 = 6, (2 · 2 + 1) · 2⁶ = 320.
    /// </summary>
    [Fact]
    public void AMagnitudePastTheCodeBlocksPlanesIsRefused()
    {
        int[] refused = DecodeQuad(Magnitude, 0, out string problem);
        Assert.Contains("needs more than the code-block's 1 bit-planes", problem);
        Assert.All(refused, s => Assert.Equal(0, s));

        int[] fits = DecodeQuad(Magnitude, 1, out string none);
        Assert.Null(none);
        Assert.Equal(new[] { 320, 0, 0, 0 }, fits);
    }

    /// <summary>
    /// A MagSgn byte after 0xFF carries a stuffing bit of 0 in its most significant place (§7.1.2):
    /// one with it set is malformed. The quad: prefix 000 (5) and suffix 4 — u = 9, U = 10, nine MagSgn
    /// bits under the known 1 — read from <c>FF</c> (eight bits) and the stuffed byte after it (seven).
    /// The control, <c>FF 00</c>: v = 255 + 2⁹ = 767, μ = 384, negative; with M<sub>b</sub> = 16 and ten
    /// skipped planes the cleanup's plane is 5, so −(2 · 384 + 1) · 2⁵ = −24608. With <c>FF 80</c> the
    /// stuffing bit is set: refused, the block zero.
    /// </summary>
    [Fact]
    public void AMagSgnByteAfterFFWithItsStuffingBitSetIsRefused()
    {
        byte[] stuffed = { 0xFF, 0x00, 0x7F, 0x02, 0x07, 0x75, 0x00 };
        byte[] stuffingSet = { 0xFF, 0x80, 0x7F, 0x02, 0x07, 0x75, 0x00 };

        int[] ok = DecodeQuad(stuffed, 10, out string none, mb: 16);
        Assert.Null(none);
        Assert.Equal(new[] { -24608, 0, 0, 0 }, ok);

        int[] refused = DecodeQuad(stuffingSet, 10, out string problem, mb: 16);
        Assert.Contains("stuffing bit set", problem);
        Assert.All(refused, s => Assert.Equal(0, s));
    }

    /// <summary>
    /// A suffix length outside 2 to min(Lcup, 4079) (§7.1.1) is malformed: the block stays zero and
    /// says why. The control is the same bytes with Scup = 3.
    /// </summary>
    [Fact]
    public void ASuffixLongerThanTheSegmentIsRefused()
    {
        int[] v = Decode(0, 0, out string problem, (new byte[] { 0x00, 0xDF, 0x0F, 0x00 }, 1));   // Scup = 15 > Lcup = 4
        Assert.Contains("Scup is 15", problem);
        Assert.All(v, s => Assert.Equal(0, s));
    }

    /// <summary>More skipped planes than the band has leaves the cleanup pass no plane: refused, the block zero.</summary>
    [Fact]
    public void SkippingEveryPlaneOfTheBandIsRefused()
    {
        var (block, data) = Block(0, (OneSample, 1));
        block.ZeroBitPlanes = Mb;
        int[] v = JpxHtBlockDecoder.Decode(block, data, JpxCodeBlockStyle.HighThroughput, Mb, 0, out string problem);
        Assert.Contains("S_blk", problem);
        Assert.All(v, s => Assert.Equal(0, s));
    }

    /// <summary>
    /// Refinement at plane 0 would be below the band (the cleanup's plane is already 0): the passes are
    /// ignored and said to be, and the cleanup's value stands — (2μ + 1) · 2⁰ = 3.
    /// </summary>
    [Fact]
    public void RefinementBelowPlaneZeroIsIgnoredAndReported()
    {
        var (block, data) = Block(0, (OneSample, 1), (AllOnes, 1));
        block.ZeroBitPlanes = Mb - 1;
        int[] v = JpxHtBlockDecoder.Decode(block, data, JpxCodeBlockStyle.HighThroughput, Mb, 0, out string problem);
        Assert.Contains("below bit-plane 0", problem);
        Assert.Equal(3, At(v, 0, 4));
        Assert.Equal(0, At(v, 0, 3));
    }
}
