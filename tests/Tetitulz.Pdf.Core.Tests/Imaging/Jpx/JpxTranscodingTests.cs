// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxTranscoding"/> and <see cref="JpxTranscoder"/>: lossless transcoding between the Part 1 and HT
/// block coders, checked by T.803 (02/2024) §C.1.10–§C.1.12's own methods — the decoded images identical, the
/// quality-layer boundaries kept through the round trip, a selective transcode a MIXED codestream — on the reference decoder's
/// lossless fixtures (no corpus), against the reference decoder itself, and on the ISO suite when it is present.
/// </summary>
public sealed partial class JpxTranscodingTests
{
    private static JpxDecodeResult Decoded(byte[] codestream)
    {
        Assert.True(JpxDecoder.TryDecode(codestream, 0, out JpxDecodeResult result, out string reason), reason);
        Assert.Empty(result.Deviations);
        return result;
    }

    private static void AssertSamePlanes(byte[] expected, byte[] actual)
    {
        JpxDecodeResult a = Decoded(expected), b = Decoded(actual);
        Assert.Equal(a.Codestream.Size.Components.Length, b.Codestream.Size.Components.Length);
        for (int c = 0; c < a.Codestream.Size.Components.Length; c++)
            Assert.Equal(a.ComponentPlane(c, out _), b.ComponentPlane(c, out _));
    }

    /// <summary>Every contributing block of every tile, in layout order, with its coder and the passes each layer brought.</summary>
    internal static List<(JpxBlockCoder Coder, int[] PassesPerLayer)> Blocks(byte[] codestream)
    {
        Assert.True(JpxCodestreamParser.TryParse(codestream, out JpxCodestream cs, out string reason), reason);
        var list = new List<(JpxBlockCoder, int[])>();
        foreach (int t in cs.Tiles.Keys.OrderBy(k => k))
        {
            Assert.True(JpxTier2Decoder.TryDecodeTile(cs, t, new WorkBudget(10_000_000), out JpxTileDecode tile, out reason), reason);
            Assert.True(tile.Complete, tile.StopReason);
            foreach (JpxCodeBlock block in tile.Layout.Components.SelectMany(c => c.Resolutions).SelectMany(r => r.Precincts)
                         .SelectMany(p => p.Bands).SelectMany(b => b.Blocks))
            {
                var perLayer = new int[tile.Layout.Layers];
                foreach (JpxCodeChunk chunk in block.Chunks) perLayer[chunk.Layer] += chunk.Passes;
                JpxCodeBlockStyle style = tile.Layout.Components[0].Coding.CodeBlockStyle;
                list.Add((block.Coder, perLayer));
            }
        }
        return list;
    }

    public static IEnumerable<object[]> Fixtures() => JpxLosslessFixtures.All.Select(f => new object[] { f.Name });

    /// <summary>
    /// §C.1.10/§C.1.11 on the reference decoder's codestreams — one block and many, three layers, two levels, RPCL, tiles
    /// with an offset, the RCT and ICT, irreversible: Part 1 → HT decodes as the original did, every block HT,
    /// the CAP and CPF saying so; HT → Part 1 decodes the same again, Rsiz and the CAP as the original's; and
    /// every block brings, layer by layer, the passes the original's did — the layer boundaries kept there and back.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void APart1Codestream_RoundTripsThroughHt(string name)
    {
        byte[] original = JpxLosslessFixtures.All.Single(f => f.Name == name).Codestream;
        Assert.True(JpxTranscoding.TryToHighThroughput(original, out byte[] ht, out string reason), reason);
        AssertSamePlanes(original, ht);
        Assert.True(JpxCodestreamParser.TryParse(ht, out JpxCodestream htCs, out reason), reason);
        Assert.Equal(0x4000, htCs.Size.Rsiz & 0x4000);
        Assert.Equal(0, htCs.HtCapabilities & 0xC000);   // HTONLY
        var htBlocks = Blocks(ht);
        Assert.All(htBlocks.Where(b => b.PassesPerLayer.Sum() > 0), b => Assert.Equal(JpxBlockCoder.HighThroughput, b.Coder));

        Assert.True(JpxTranscoding.TryToPart1(ht, out byte[] back, out reason), reason);
        AssertSamePlanes(original, back);
        Assert.True(JpxCodestreamParser.TryParse(original, out JpxCodestream originalCs, out reason), reason);
        Assert.True(JpxCodestreamParser.TryParse(back, out JpxCodestream backCs, out reason), reason);
        Assert.Equal(originalCs.Size.Rsiz, backCs.Size.Rsiz);
        Assert.Equal(-1, backCs.HtCapabilities);

        var before = Blocks(original);
        var after = Blocks(back);
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++) Assert.Equal(before[i].PassesPerLayer, after[i].PassesPerLayer);

        // A block whose first layer brought its last cleanup had its placeholders folded into its zero
        // bit-planes: at most one HT set's three passes. Only a block whose passes came in earlier layers keeps
        // them, and there each layer brings the passes the original's did.
        for (int i = 0; i < before.Count; i++)
        {
            int[] layers = before[i].PassesPerLayer;
            int total = layers.Sum();
            if (total == 0) continue;
            int firstLayer = Array.FindIndex(layers, n => n > 0);
            int placeholders = 3 * ((total - 1) / 3);
            if (layers[firstLayer] > placeholders) Assert.True(htBlocks[i].PassesPerLayer.Sum() <= 3, $"block {i}: {htBlocks[i].PassesPerLayer.Sum()} HT passes");
            else Assert.Equal(layers, htBlocks[i].PassesPerLayer);
        }
    }

    /// <summary>
    /// Without quality layers kept, every HT block is one set of at most three passes — what the reference decoder reads,
    /// which refuses more — arriving in the layer that brought the original's last cleanup; the picture is the
    /// same, and the way back to Part 1 is too.
    /// </summary>
    [Theory]
    [InlineData("Gray40x24_Blocks16_Precincts32_ThreeLayers")]
    [InlineData("Gray40x24_TwoLevels_Rpcl_ThreeLayers")]
    public void WithoutLayersKept_EveryHtBlockIsOneSetOfAtMostThreePasses(string name)
    {
        byte[] original = JpxLosslessFixtures.All.Single(f => f.Name == name).Codestream;
        Assert.True(JpxTranscoding.TryToHighThroughput(original, keepQualityLayers: false, out byte[] ht, out string reason), reason);
        AssertSamePlanes(original, ht);
        var blocks = Blocks(ht);
        Assert.All(blocks, b => Assert.True(b.PassesPerLayer.Sum() <= 3, $"{b.PassesPerLayer.Sum()} passes"));

        // Each layer brings what the original's layers up to it brought past the placeholders — the set's passes
        // arrive with the original's, and a layer that brought placeholders alone brings nothing now.
        var before = Blocks(original);
        int early = 0;
        for (int i = 0; i < before.Count; i++)
        {
            int[] layers = before[i].PassesPerLayer;
            int total = layers.Sum();
            if (total == 0 || blocks[i].PassesPerLayer.Sum() == 0) continue;
            int placeholders = 3 * ((total - 1) / 3), cumulative = 0, brought = 0;
            for (int l = 0; l < layers.Length; l++)
            {
                cumulative += layers[l];
                int expected = Math.Max(0, cumulative - placeholders) - brought;
                Assert.Equal(expected, blocks[i].PassesPerLayer[l]);
                brought += expected;
                if (layers[l] > 0 && expected == 0) early++;
            }
        }
        Assert.True(early > 0, "no block had a layer of placeholders alone, so where its set arrives was never in question");
        Assert.True(JpxTranscoding.TryToHighThroughput(original, out byte[] kept, out reason), reason);
        Assert.Contains(Blocks(kept), b => b.PassesPerLayer.Sum() > 3);   // potency: the layered fixture does keep placeholders by default
        Assert.True(JpxTranscoding.TryToPart1(ht, out byte[] back, out reason), reason);
        AssertSamePlanes(original, back);
    }

    /// <summary>
    /// §C.1.12: a selective transcode — every other Part 1 block left as it was, the rest made HT — is a MIXED
    /// codestream (CAP's Ccap15 bits 14 and 15) whose tile-components hold blocks of both coders, and it decodes
    /// as the original; transcoded back to Part 1 it decodes so again, every block Part 1.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void ASelectiveTranscode_IsAMixedCodestream_ThatDecodesAsTheOriginal(string name)
    {
        byte[] original = JpxLosslessFixtures.All.Single(f => f.Name == name).Codestream;
        Assert.True(JpxTranscoder.TryTranscode(original, true, (t, c, r, o, i) => (t + c + r + o + i) % 2 == 0, out byte[] mixed, out string reason), reason);
        Assert.True(JpxCodestreamParser.TryParse(mixed, out JpxCodestream cs, out reason), reason);
        Assert.Equal(0xC000, cs.HtCapabilities & 0xC000);
        AssertSamePlanes(original, mixed);
        var blocks = Blocks(mixed).Where(b => b.PassesPerLayer.Sum() > 0).ToList();
        if (blocks.Count > 1)
        {
            Assert.Contains(blocks, b => b.Coder == JpxBlockCoder.HighThroughput);
            Assert.Contains(blocks, b => b.Coder == JpxBlockCoder.Part1);
        }

        Assert.True(JpxTranscoding.TryToPart1(mixed, out byte[] back, out reason), reason);
        AssertSamePlanes(original, back);
        Assert.Equal(-1, Parse(back).HtCapabilities);
    }

    private static JpxCodestream Parse(byte[] codestream)
    {
        Assert.True(JpxCodestreamParser.TryParse(codestream, out JpxCodestream cs, out string reason), reason);
        return cs;
    }

    /// <summary>The codestream with its SIZ's Rsiz set to <paramref name="rsiz"/>, and, when given, a marker inserted after SIZ.</summary>
    private static byte[] Relabelled(byte[] codestream, int rsiz, byte[] afterSiz = null)
    {
        byte[] c = (byte[])codestream.Clone();
        c[6] = (byte)(rsiz >> 8);
        c[7] = (byte)rsiz;
        if (afterSiz == null) return c;
        int sizEnd = 4 + ((c[4] << 8) | c[5]);
        return c.Take(sizEnd).Concat(afterSiz).Concat(c.Skip(sizEnd)).ToArray();
    }

    /// <summary>
    /// The Part 1 profile goes over in CPF (ITU-T T.814 §A.6): profile 1 (Rsiz 2) becomes an HTJ2K Rsiz of bit 14
    /// alone and CPFnum 2 — Pcpf 3, CPFnum + 1 — and comes back as Rsiz 2. CAP's Ccap15 says what the codestream is:
    /// HTONLY, SINGLEHT, the irreversible transform's bit 5 where there is one, and the magnitude bound B covering
    /// every band's planes (§A.3.7).
    /// </summary>
    [Theory]
    [InlineData("Gray40x24_TwoLevels_Rpcl_ThreeLayers", false)]
    [InlineData("Gray40x24_Irreversible_TwoLevels", true)]
    public void TheProfileGoesOverInCpf_AndCapSaysWhatTheCodestreamIs(string name, bool irreversible)
    {
        byte[] original = Relabelled(JpxLosslessFixtures.All.Single(f => f.Name == name).Codestream, 2);
        Assert.True(JpxTranscoding.TryToHighThroughput(original, out byte[] ht, out string reason), reason);
        JpxCodestream cs = Parse(ht);
        Assert.Equal(0x4000, cs.Size.Rsiz);
        int cpf = MainHeaderMarkers(ht).Single(m => m.Marker == 0xFF59).At;
        Assert.Equal(new byte[] { 0x00, 0x04, 0x00, 0x03 }, ht.Skip(cpf + 2).Take(4));

        Assert.Equal(irreversible ? 0x20 : 0, cs.HtCapabilities & 0xF820);
        int planes = 0;
        JpxComponentCoding coding = cs.Main.Coding.Component;
        for (int r = 0; r <= coding.DecompositionLevels; r++)
            for (int o = r == 0 ? 0 : 1; o <= (r == 0 ? 0 : 3); o++)
                planes = Math.Max(planes, JpxQuantizationSteps.MagnitudeBitPlanes(cs.Main.Quantization, coding.DecompositionLevels, r, o));
        Assert.Equal(JpxEncoder.MagnitudeBoundParameter(planes), cs.HtCapabilities & 0x1F);

        Assert.True(JpxTranscoding.TryToPart1(ht, out byte[] back, out reason), reason);
        Assert.Equal(2, Parse(back).Size.Rsiz);
        Assert.DoesNotContain(MainHeaderMarkers(back), m => m.Marker == 0xFF59);
    }

    /// <summary>The main header's markers up to the first SOT, each with where it starts; 0xFF30–0xFF3F have no length (T.800 §A.1).</summary>
    private static List<(int Marker, int At)> MainHeaderMarkers(byte[] codestream)
    {
        var list = new List<(int, int)>();
        int at = 2;
        while (true)
        {
            int marker = (codestream[at] << 8) | codestream[at + 1];
            if (marker == 0xFF90) return list;
            list.Add((marker, at));
            at += marker >= 0xFF30 && marker <= 0xFF3F ? 2 : 2 + ((codestream[at + 2] << 8) | codestream[at + 3]);
        }
    }

    /// <summary>A marker of T.800 §A.1's reserved range 0xFF30–0xFF3F has no length: the header walk steps over it, two bytes, and copies it.</summary>
    [Fact]
    public void AReservedMarkerWithoutALength_IsSteppedOver()
    {
        byte[] original = Relabelled(JpxLosslessFixtures.Gray40x24_OneBlock.Codestream, 0, new byte[] { 0xFF, 0x30 });
        Assert.True(JpxTranscoding.TryToHighThroughput(original, out byte[] ht, out string reason), reason);
        AssertSamePlanes(original, ht);
        Assert.Contains(MainHeaderMarkers(ht), m => m.Marker == 0xFF30);
    }

    /// <summary>
    /// Blocks that end in a refinement pass, each way — which no lossless fixture has, every block of theirs ending
    /// in plane 0's cleanup: a Part 1 encode cut to a size target stops blocks after a SigProp or a MagRef pass, an
    /// HT one under a target codes sets of two and three passes. Both transcode and come back decoding as they did,
    /// each layer bringing, there and back, the passes it brought.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlocksEndingInARefinementPass_TranscodeAndComeBack(bool fromHt)
    {
        int[][] planes = JpxEncoderTestImages.Pattern(64, 48, 1, 11);
        var options = new JpxEncodeOptions
        {
            HighThroughput = fromHt, Levels = 2, CodeBlockWidthExponent = 4, CodeBlockHeightExponent = 4, Layers = 2,
        };
        options.TargetBytes = JpxEncoder.EncodeLossless(planes, 64, 48, 8, false, options).Length / 3;
        byte[] original = JpxEncoder.EncodeLossless(planes, 64, 48, 8, false, options);
        // Potency: a block whose passes end in a refinement pass — the HT set's second or third, Part 1's (N − 1) mod 3.
        Assert.Contains(Blocks(original), b => b.PassesPerLayer.Sum() is int n && n > 0 && (fromHt ? n % 3 != 1 : (n - 1) % 3 != 0));

        byte[] other, back;
        string reason;
        if (fromHt)
        {
            Assert.True(JpxTranscoding.TryToPart1(original, out other, out reason), reason);
            Assert.True(JpxTranscoding.TryToHighThroughput(other, out back, out reason), reason);
        }
        else
        {
            Assert.True(JpxTranscoding.TryToHighThroughput(original, out other, out reason), reason);
            Assert.True(JpxTranscoding.TryToPart1(other, out back, out reason), reason);
        }
        AssertSamePlanes(original, other);
        AssertSamePlanes(original, back);
        if (!fromHt)
        {
            var before = Blocks(original);
            var after = Blocks(back);
            for (int i = 0; i < before.Count; i++) Assert.Equal(before[i].PassesPerLayer, after[i].PassesPerLayer);
        }
    }

    /// <summary>The codestream with its COD's code-block style byte (T.800 Table A.19, the SPcod after SGcod's four bytes) set to <paramref name="style"/>.</summary>
    private static byte[] WithCodStyle(byte[] codestream, int style)
    {
        byte[] c = (byte[])codestream.Clone();
        int cod = MainHeaderMarkers(c).Single(m => m.Marker == 0xFF52).At;
        c[cod + 12] = (byte)style;
        return c;
    }

    /// <summary>The codestream with <paramref name="segment"/> inserted at the start of its first tile-part's header, that tile-part's Psot grown to match.</summary>
    private static byte[] IntoFirstTilePart(byte[] codestream, byte[] segment)
    {
        List<(int Marker, int At)> main = MainHeaderMarkers(codestream);
        int sot = main[^1].At + (main[^1].Marker >= 0xFF30 && main[^1].Marker <= 0xFF3F ? 2 : 2 + ((codestream[main[^1].At + 2] << 8) | codestream[main[^1].At + 3]));
        Assert.Equal(0xFF90, (codestream[sot] << 8) | codestream[sot + 1]);
        byte[] c = codestream.Take(sot + 12).Concat(segment).Concat(codestream.Skip(sot + 12)).ToArray();
        long psot = ((long)c[sot + 6] << 24) | ((long)c[sot + 7] << 16) | ((long)c[sot + 8] << 8) | c[sot + 9];
        if (psot != 0)
        {
            psot += segment.Length;
            c[sot + 6] = (byte)(psot >> 24); c[sot + 7] = (byte)(psot >> 16); c[sot + 8] = (byte)(psot >> 8); c[sot + 9] = (byte)psot;
        }
        return c;
    }

    /// <summary>
    /// A selective transcode leaves Part 1 blocks as written in a MIXED codestream, whose Part 1 blocks §A.4 forbids
    /// selective bypass: a codestream that uses it is refused for that, while a whole transcode takes it, and the same
    /// codestream without the flag is transcoded selectively. Two-bit samples and no wavelet keep every block short of
    /// its tenth pass, where bypass begins (§D.6), so the flag changes nothing the passes read.
    /// </summary>
    [Fact]
    public void ASelectiveTranscode_RefusesSelectiveBypass_WhichMixedForbidsItsPart1Blocks()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(32, 32, 1, 3, precision: 2);
        byte[] plain = JpxEncoder.EncodeLossless(planes, 32, 32, 2, false, new JpxEncodeOptions { Levels = 0, CodeBlockWidthExponent = 4, CodeBlockHeightExponent = 4 });
        Assert.All(Blocks(plain), b => Assert.True(b.PassesPerLayer.Sum() < 10, $"{b.PassesPerLayer.Sum()} passes"));
        JpxTranscoder.KeepPart1 everyOther = (t, c, r, o, i) => i % 2 == 0;
        Assert.True(JpxTranscoder.TryTranscode(plain, true, everyOther, out _, out string reason), reason);

        byte[] bypass = WithCodStyle(plain, 0x01);
        AssertSamePlanes(plain, bypass);
        Assert.False(JpxTranscoder.TryTranscode(bypass, true, everyOther, out _, out reason));
        Assert.Contains("selective bypass or termination on each pass", reason, StringComparison.Ordinal);
        Assert.True(JpxTranscoding.TryToHighThroughput(bypass, out byte[] ht, out reason), reason);
        AssertSamePlanes(plain, ht);
    }

    /// <summary>
    /// Vertical causality (T.800 §D.7, style bit 3) goes over with the blocks: a Part 1 codestream coded causally,
    /// cut to a size target so its HT sets carry SigProp passes, whose neighbourhood the flag changes (T.814 §7.4),
    /// transcodes to an HT codestream whose style keeps the bit and decodes as the original — and back.
    /// </summary>
    [Fact]
    public void VerticalCausality_GoesOverWithTheBlocks()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(64, 48, 1, 17);
        var options = new JpxEncodeOptions { VerticallyCausal = true, Levels = 2, CodeBlockWidthExponent = 4, CodeBlockHeightExponent = 4 };
        options.TargetBytes = JpxEncoder.EncodeLossless(planes, 64, 48, 8, false, options).Length / 3;
        byte[] original = JpxEncoder.EncodeLossless(planes, 64, 48, 8, false, options);
        Assert.Equal(JpxCodeBlockStyle.VerticallyCausal, Parse(original).Main.Coding.Component.CodeBlockStyle);
        Assert.Contains(Blocks(original), b => b.PassesPerLayer.Sum() is int n && n > 1 && (n - 1) % 3 != 0);   // potency: sets with refinement passes

        Assert.True(JpxTranscoding.TryToHighThroughput(original, out byte[] ht, out string reason), reason);
        Assert.Equal(JpxCodeBlockStyle.HighThroughput | JpxCodeBlockStyle.VerticallyCausal, Parse(ht).Main.Coding.Component.CodeBlockStyle);
        AssertSamePlanes(original, ht);
        Assert.True(JpxTranscoding.TryToPart1(ht, out byte[] back, out reason), reason);
        AssertSamePlanes(original, back);
    }

    /// <summary>
    /// A functional segment in a tile-part header makes the codestream heterogeneous (ITU-T T.814 §A.3.5): CAP's
    /// Ccap15 bit 11 says so on the way to HT, for a copy of the main COD in the first tile-part — and not without it.
    /// </summary>
    [Fact]
    public void ACodInATilePartHeader_IsDeclaredHeterogeneous()
    {
        byte[] original = JpxLosslessFixtures.Gray40x24_Tiles20x12_Offset3x2_OneLevel.Codestream;
        (int Marker, int At) cod = MainHeaderMarkers(original).Single(m => m.Marker == 0xFF52);
        byte[] segment = original.Skip(cod.At).Take(2 + ((original[cod.At + 2] << 8) | original[cod.At + 3])).ToArray();
        byte[] heterogeneous = IntoFirstTilePart(original, segment);
        AssertSamePlanes(original, heterogeneous);

        Assert.True(JpxTranscoding.TryToHighThroughput(heterogeneous, out byte[] ht, out string reason), reason);
        Assert.Equal(0x0800, Parse(ht).HtCapabilities & 0x0800);
        AssertSamePlanes(original, ht);
        Assert.True(JpxTranscoding.TryToHighThroughput(original, out ht, out reason), reason);
        Assert.Equal(0, Parse(ht).HtCapabilities & 0x0800);
    }

    /// <summary>
    /// PLT states every packet's length (T.800 §A.7.3), and every length changes in a transcode: the segment is
    /// dropped, not carried stale — the decoder checks the lengths it reads against PLT and reports a disagreement,
    /// so an output that kept it would not decode cleanly. The control: the source, PLT and all, decodes cleanly.
    /// </summary>
    [Fact]
    public void PacketLengths_AreDropped_NotCarriedStale()
    {
        byte[] original = JpxLosslessFixtures.Gray40x24_TwoLevels_Rpcl_ThreeLayers.Codestream;
        JpxCodestream cs = Parse(original);
        Assert.True(JpxTier2Decoder.TryDecodeTile(cs, cs.Tiles.Keys.Single(), new WorkBudget(10_000_000), out JpxTileDecode tile, out string reason), reason);
        var plt = new List<byte> { 0 };   // Zplt
        foreach (long length in tile.PacketLengths)
        {
            var groups = new List<byte>();
            for (long v = length; ; v >>= 7)
            {
                groups.Insert(0, (byte)(v & 0x7F));
                if (v < 0x80) break;
            }
            for (int g = 0; g < groups.Count - 1; g++) groups[g] |= 0x80;
            plt.AddRange(groups);
        }
        byte[] segment = new byte[] { 0xFF, 0x58, (byte)((plt.Count + 2) >> 8), (byte)(plt.Count + 2) }.Concat(plt).ToArray();
        byte[] withPlt = IntoFirstTilePart(original, segment);
        Assert.Equal(tile.PacketLengths, Parse(withPlt).Tiles.Values.Single().Parts[0].PacketLengths);
        AssertSamePlanes(original, withPlt);

        Assert.True(JpxTranscoding.TryToHighThroughput(withPlt, out byte[] ht, out reason), reason);
        AssertSamePlanes(original, ht);
        Assert.Empty(Parse(ht).Tiles.Values.Single().Parts[0].PacketLengths);
    }

    /// <summary>The refusals, each for its own reason: HT to HT, Part 1 to Part 1, a codestream cut short; and the accepting controls beside them.</summary>
    [Fact]
    public void Transcoding_RefusesWhatItCannotDo()
    {
        byte[] part1 = JpxLosslessFixtures.Gray40x24_OneBlock.Codestream;
        Assert.False(JpxTranscoding.TryToPart1(part1, out _, out string reason));
        Assert.Contains("no HT code-blocks", reason, StringComparison.Ordinal);
        Assert.True(JpxTranscoding.TryToHighThroughput(part1, out byte[] ht, out reason), reason);
        Assert.False(JpxTranscoding.TryToHighThroughput(ht, out _, out reason));
        Assert.Contains("already HTJ2K", reason, StringComparison.Ordinal);
        Assert.True(JpxTranscoding.TryToPart1(ht, out _, out reason), reason);

        byte[] cut = part1.Take(part1.Length - 20).ToArray();
        Assert.False(JpxTranscoding.TryToHighThroughput(cut, out _, out reason));
        Assert.Contains("does not decode whole", reason, StringComparison.Ordinal);
    }

    /// <summary>A JP2 file becomes a JPH (brand 'jph ', not 'jp2 '-compatible) and back, its header boxes kept byte for byte.</summary>
    [Fact]
    public void AJp2File_BecomesAJph_AndBack()
    {
        byte[] jp2 = JpxEncoder.WrapAsJp2(JpxLosslessFixtures.Rgb40x24_Mct_TwoLevels.Codestream);
        Assert.True(JpxTranscoding.TryToHighThroughput(jp2, out byte[] jph, out string reason), reason);
        Assert.Equal("jph ", System.Text.Encoding.ASCII.GetString(jph, 20, 4));
        Assert.DoesNotContain("jp2 ", System.Text.Encoding.ASCII.GetString(jph, 0, 64));
        Assert.True(JpxHeader.TryLocateCodestream(jph, out long start, out long end));
        AssertSamePlanes(JpxLosslessFixtures.Rgb40x24_Mct_TwoLevels.Codestream, jph.Skip((int)start).Take((int)(end - start)).ToArray());

        Assert.True(JpxTranscoding.TryToPart1(jph, out byte[] back, out reason), reason);
        Assert.Equal("jp2 ", System.Text.Encoding.ASCII.GetString(back, 20, 4));
        Assert.True(JpxHeader.TryFindTopLevelBox(jp2, 0x6A703268, out long h0, out long h1));
        Assert.True(JpxHeader.TryFindTopLevelBox(back, 0x6A703268, out long b0, out long b1));
        Assert.Equal(jp2.Skip((int)h0).Take((int)(h1 - h0)), back.Skip((int)b0).Take((int)(b1 - b0)));
    }

    /// <summary>
    /// The container's refusals, each for its reason, beside the accepting control — the same JP2, sRGB: a JPX file's
    /// Reader Requirements box; CMYK on the way to a JPH, which enumerates none (T.814 Annex D.4); a file branded
    /// <c>jph </c> asked to go to HT, whose brand is not a Part 1 codestream's.
    /// </summary>
    [Fact]
    public void TheContainerRefusals_EachForItsReason()
    {
        byte[] cs = JpxLosslessFixtures.Rgb40x24_Mct_TwoLevels.Codestream;
        byte[] ihdr = Jp2Headers.Ihdr(24, 40, 3, 7);
        static byte[] Ftyp(string brand) => Jp2Headers.Box("ftyp", System.Text.Encoding.ASCII.GetBytes(brand + "\0\0\0\0" + brand));
        byte[] Jp2(string brand, int enumCs, params byte[][] before) =>
            Jp2Headers.Container(before.Prepend(Ftyp(brand)).Concat(new[] { Jp2Headers.Jp2h(ihdr, Jp2Headers.ColrEnumerated(enumCs)), Jp2Headers.Jp2c(cs) }).ToArray());

        Assert.True(JpxTranscoding.TryToHighThroughput(Jp2("jp2 ", 16), out _, out string reason), reason);

        Assert.False(JpxTranscoding.TryToHighThroughput(Jp2("jp2 ", 16, Jp2Headers.Box("rreq", new byte[] { 1, 0, 0, 0, 0, 0, 0 })), out _, out reason));
        Assert.Contains("Reader Requirements", reason, StringComparison.Ordinal);

        Assert.False(JpxTranscoding.TryToHighThroughput(Jp2("jp2 ", 12), out _, out reason));
        Assert.Contains("CMYK", reason, StringComparison.Ordinal);

        Assert.False(JpxTranscoding.TryToHighThroughput(Jp2("jph ", 16), out _, out reason));
        Assert.Contains("brand is not 'jp2 '", reason, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> IsoProfileCodestreams() =>
        JpxDecoderConformanceTests.Tolerances.Select(t => new object[] { t.Stem });

    /// <summary>
    /// The ISO Part 1 test codestreams transcoded to HT decode as the originals do — the suite's own derivation of
    /// its HTJ2K files (T.803 §6.1), done here — and back to Part 1 the same again.
    /// </summary>
    [JpxCorpusTheory]
    [MemberData(nameof(IsoProfileCodestreams))]
    public void TheIsoPart1Codestreams_RoundTripThroughHt(string stem)
    {
        byte[] original = File.ReadAllBytes(Path.Combine(TestCorpus.JpxRoot!, "input", "conformance", stem + ".j2k"));
        Assert.True(JpxTranscoding.TryToHighThroughput(original, out byte[] ht, out string reason), reason);
        AssertSamePlanes(original, ht);
        Assert.True(JpxTranscoding.TryToPart1(ht, out byte[] back, out reason), reason);
        AssertSamePlanes(original, back);
    }

    /// <summary>The ISO HTJ2K test codestreams, MIXED ones included, transcoded to Part 1 decode as they do.</summary>
    [IsoHtJpxCorpusTheory]
    [MemberData(nameof(IsoHtCodestreams))]
    public void TheIsoHtCodestreams_TranscodeToPart1(string file)
    {
        byte[] ht = File.ReadAllBytes(Path.Combine(TestCorpus.IsoHtJpxRoot!, file));
        Assert.True(JpxTranscoding.TryToPart1(ht, out byte[] part1, out string reason), reason);
        AssertSamePlanes(ht, part1);
    }

    public static IEnumerable<object[]> IsoHtCodestreams() => JpxIsoHtConformanceTests.Rows.Select(r => new object[] { r.File });
}
