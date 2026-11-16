// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// What tier 1 produced for one code-block, how the layers divide it, and the packet-header
    /// state tier 2 keeps for it. The passes a layer carries are decided by the rate allocator
    /// (<see cref="JpxRateControl"/>) and read by <see cref="JpxPacketEncoder"/>, which writes each
    /// layer's new passes as the next slice of the one codeword segment.
    /// </summary>
    internal sealed class JpxEncodedBlock
    {
        /// <summary>A block whose every pass is in the first layer, with the byte count as its only length: the shape the packet tests hand over.</summary>
        internal JpxEncodedBlock(byte[] data, int passes, int zeroBitPlanes)
            : this(new JpxBlockCode(data, passes == 0 ? 0 : (passes + 2) / 3, passes, FullLengths(data, passes), new double[passes]), zeroBitPlanes, 1)
        {
            if (passes > 0) LayerEnd[0] = passes;
        }

        internal JpxEncodedBlock(JpxBlockCode code, int zeroBitPlanes, int layers, double weight = 1)
        {
            Code = code;
            ZeroBitPlanes = zeroBitPlanes;
            Weight = weight;
            LayerEnd = new int[layers];
            Lblock = 3;
        }

        private static int[] FullLengths(byte[] data, int passes)
        {
            var lengths = new int[passes];
            for (int i = 0; i < passes; i++) lengths[i] = data.Length;
            return lengths;
        }

        internal JpxBlockCode Code { get; }

        /// <summary>The single codeword segment; empty when the block has no passes.</summary>
        internal byte[] Data => Code.Data;

        internal int Passes => Code.Passes;

        /// <summary>P of §B.10.5: M<sub>b</sub> less the planes coded.</summary>
        internal int ZeroBitPlanes { get; }

        /// <summary>What one unit of the block's distortion (<see cref="JpxBlockCode.Distortion"/>) is worth in the samples: the band's step squared times its synthesis gain and the component's.</summary>
        internal double Weight { get; }

        /// <summary>Per layer: the passes included through that layer, cumulative and non-decreasing; a layer adds <c>LayerEnd[l] − LayerEnd[l − 1]</c> passes.</summary>
        internal int[] LayerEnd { get; }

        /// <summary>True once a packet has carried the block (§B.10.4).</summary>
        internal bool Included { get; set; }

        /// <summary>The length-field state variable, initially 3 (§B.10.7.1).</summary>
        internal int Lblock { get; set; }

        /// <summary>Passes written by the packets so far.</summary>
        internal int PassesWritten { get; set; }

        /// <summary>Bytes of <see cref="Data"/> written by the packets so far.</summary>
        internal int BytesWritten { get; set; }

        /// <summary>
        /// The code-block style the passes were coded under, which says where the block's codeword segments
        /// end (T.800 §B.10.7.2; ITU-T T.814 §B.2 for an HT block) and, for an HT block of a MIXED
        /// tile-component, how its first length must read (§A.4). None for a Part 1 block of this encoder's.
        /// </summary>
        internal JpxCodeBlockStyle Style { get; set; }

        /// <summary>For an HT block, the 3·P0 placeholder passes before its HT set (ITU-T T.814 §B.1).</summary>
        internal int HtPlaceholderPasses { get; set; }

        /// <summary>True once a packet has written a non-zero length for the block.</summary>
        internal bool LengthWritten { get; set; }

        /// <summary>The first layer that carries a pass of the block, or <paramref name="layers"/> when none does: the inclusion tag tree's value (§B.10.4).</summary>
        internal int FirstLayer(int layers) => FirstLayer(layers, layers - 1);

        /// <summary>
        /// The same, reading layers 0 to <paramref name="upTo"/> only — what is decided while a
        /// rate allocator writes layer <paramref name="upTo"/>: a block none of those layers carries
        /// answers <paramref name="layers"/>, "not yet", whatever a later layer's slot holds from an
        /// earlier pass of the allocator.
        /// </summary>
        internal int FirstLayer(int layers, int upTo)
        {
            for (int l = 0; l <= upTo && l < layers; l++) if (LayerEnd[l] > 0) return l;
            return layers;
        }

        /// <summary>Back to the state before any packet was written, for another pass of the allocator.</summary>
        internal void ResetWriteState()
        {
            Included = false;
            Lblock = 3;
            PassesWritten = 0;
            BytesWritten = 0;
            LengthWritten = false;
        }
    }

    /// <summary>The two tag trees of a precinct-band (§B.10.2), on the encoder's side.</summary>
    internal sealed class JpxPrecinctBandTrees
    {
        internal JpxPrecinctBandTrees(JpxTagTreeEncoder inclusion, JpxTagTreeEncoder zeroBitPlanes)
        {
            Inclusion = inclusion;
            ZeroBitPlanes = zeroBitPlanes;
        }

        internal JpxTagTreeEncoder Inclusion { get; }
        internal JpxTagTreeEncoder ZeroBitPlanes { get; }
    }

    /// <summary>
    /// Writes one packet (T.800 §B.10): the header in the order of §B.10.8 — the non-empty bit,
    /// then per band and code-block the inclusion (through the tag tree until the block's first
    /// layer, one bit after), the zero bit-plane tag tree at first inclusion, Table B.4's pass
    /// count, the Lblock signalling and the length of the layer's slice of the codeword segment —
    /// the EPH marker when asked, then the body: each included block's slice in header order. A
    /// precinct none of whose blocks gains a pass is written as the zero-length packet of §B.10.3.
    /// The mirror of <see cref="JpxPacketDecoder"/>.
    /// </summary>
    internal static class JpxPacketEncoder
    {
        private const int Eph = 0xFF92;

        /// <summary>Table B.4's largest signalled pass count.</summary>
        internal const int MaxPasses = 164;

        internal static void Write(JpxTileLayout layout, JpxPacketAddress address, Dictionary<JpxCodeBlock, JpxEncodedBlock> blocks,
            Dictionary<JpxPrecinctBand, JpxPrecinctBandTrees> trees, bool eph, List<byte> output)
        {
            int layer = address.Layer;
            if (layer < 0 || layer >= layout.Layers) throw new ArgumentOutOfRangeException(nameof(address), $"layer {layer} of {layout.Layers}");
            JpxPrecinct precinct = layout.Components[address.Component].Resolutions[address.Resolution].Precincts[address.Precinct];

            bool any = false;
            foreach (JpxPrecinctBand pb in precinct.Bands)
                foreach (JpxCodeBlock block in pb.Blocks)
                    any |= blocks[block].LayerEnd[layer] > blocks[block].PassesWritten;

            var header = new JpxBitWriter();
            var body = new List<byte>();
            if (!any)
            {
                header.WriteBit(0);
            }
            else
            {
                header.WriteBit(1);
                foreach (JpxPrecinctBand pb in precinct.Bands)
                {
                    if (pb.Blocks.Length == 0) continue;
                    JpxPrecinctBandTrees t = TreesFor(pb, blocks, trees, layout.Layers, layer);
                    // The tree was built when this precinct-band was first written; every block whose
                    // first layer has been decided since lowers its value now — all of them before any
                    // is coded, since coding one block's inclusion walks the parents the others share —
                    // so the bits a trial measures are the bits the codestream's own write will code
                    // (an allocator that tried a layer against stale "not yet" values threw on the
                    // difference).
                    for (int i = 0; i < pb.Blocks.Length; i++)
                        t.Inclusion.Lower(i % pb.BlocksWide, i / pb.BlocksWide, blocks[pb.Blocks[i]].FirstLayer(layout.Layers, layer));
                    for (int i = 0; i < pb.Blocks.Length; i++)
                    {
                        JpxEncodedBlock block = blocks[pb.Blocks[i]];
                        int bx = i % pb.BlocksWide, by = i / pb.BlocksWide;
                        int passes = block.LayerEnd[layer] - block.PassesWritten;
                        if (block.Included)
                        {
                            header.WriteBit(passes > 0 ? 1 : 0);
                        }
                        else
                        {
                            // The tree answers "first layer below layer + 1?": yes exactly when this layer is the block's first.
                            t.Inclusion.Encode(header, bx, by, layer + 1);
                        }
                        if (passes <= 0) continue;

                        if (!block.Included)
                        {
                            block.Included = true;
                            // The decoder asks "below 1?", "below 2?", … until the answer is yes, at P + 1.
                            for (int threshold = 1; threshold <= block.ZeroBitPlanes + 1; threshold++)
                                t.ZeroBitPlanes.Encode(header, bx, by, threshold);
                        }

                        WritePassCount(header, passes);
                        WriteLengths(header, block, block.LayerEnd[layer], out int end);
                        for (int b = block.BytesWritten; b < end; b++) body.Add(block.Data[b]);
                        block.PassesWritten = block.LayerEnd[layer];
                        block.BytesWritten = end;
                    }
                }
            }

            output.AddRange(header.Finish());
            if (eph)
            {
                output.Add(Eph >> 8);
                output.Add(Eph & 0xFF);
            }
            output.AddRange(body);
        }

        /// <summary>
        /// The Lblock signalling and the lengths of the passes from the block's <see cref="JpxEncodedBlock.PassesWritten"/>
        /// to <paramref name="end"/>: a codeword segment ends where the decoder ends one (§B.10.7.2) — at the layer's
        /// last pass for a Part 1 block of this encoder's, which terminates none, and at set T's passes too for an HT
        /// block (ITU-T T.814 §B.2), its cleanup's segment, then its refinement passes'. Lblock rises once, far enough
        /// for every segment. Under MIXED (§A.4) an HT block's first non-zero length is written with Lblock above 3 and
        /// a leading 0, as the decoder tells it from a Part 1 block's. <paramref name="endBytes"/> is the bytes through the last pass.
        /// </summary>
        private static void WriteLengths(JpxBitWriter header, JpxEncodedBlock block, int end, out int endBytes)
        {
            var segments = new List<(int Passes, int Length)>();
            int first = block.PassesWritten, segmentStart = first, from = block.BytesWritten;
            for (int p = first; p < end; p++)
            {
                if (!JpxPacketDecoder.IsTerminated(block.Style, p, block.HtPlaceholderPasses) && p != end - 1) continue;
                int to = block.Code.Lengths[p];
                segments.Add((p - segmentStart + 1, to - from));
                segmentStart = p + 1;
                from = to;
            }
            endBytes = from;

            bool mixedHt = (block.Style & (JpxCodeBlockStyle.HighThroughput | JpxCodeBlockStyle.Mixed))
                == (JpxCodeBlockStyle.HighThroughput | JpxCodeBlockStyle.Mixed);
            int raise = 0;
            bool firstNonZero = !block.LengthWritten;
            foreach ((int n, int length) in segments)
            {
                int needed = BitLength(length);
                if (mixedHt && firstNonZero && length > 0)
                {
                    needed++;
                    raise = Math.Max(raise, 4 - block.Lblock);
                }
                if (length > 0) firstNonZero = false;
                raise = Math.Max(raise, needed - (block.Lblock + JpxPacketDecoder.FloorLog2(n)));
            }
            for (int k = 0; k < raise; k++) header.WriteBit(1);
            block.Lblock += raise;
            header.WriteBit(0);
            foreach ((int n, int length) in segments)
            {
                header.WriteBits(length, block.Lblock + JpxPacketDecoder.FloorLog2(n));
                if (length > 0) block.LengthWritten = true;
            }
        }

        /// <summary>Table B.4: the codeword for <paramref name="passes"/> (1 to 164).</summary>
        internal static void WritePassCount(JpxBitWriter bits, int passes)
        {
            if (passes < 1 || passes > MaxPasses) throw new ArgumentOutOfRangeException(nameof(passes), "Table B.4 signals 1 to 164 coding passes");
            if (passes == 1) bits.WriteBit(0);
            else if (passes == 2) bits.WriteBits(0b10, 2);
            else if (passes <= 5) { bits.WriteBits(0b11, 2); bits.WriteBits(passes - 3, 2); }
            else if (passes <= 36) { bits.WriteBits(0b1111, 4); bits.WriteBits(passes - 6, 5); }
            else { bits.WriteBits(0b1111_11111, 9); bits.WriteBits(passes - 37, 7); }
        }

        /// <summary>The precinct-band's tag trees, built on first use from the allocation as far as layer <paramref name="upTo"/> decides it: each block's first layer (or the layer count, for not yet), and P.</summary>
        private static JpxPrecinctBandTrees TreesFor(JpxPrecinctBand pb, Dictionary<JpxCodeBlock, JpxEncodedBlock> blocks,
            Dictionary<JpxPrecinctBand, JpxPrecinctBandTrees> trees, int layers, int upTo)
        {
            if (trees.TryGetValue(pb, out JpxPrecinctBandTrees t)) return t;
            var inclusion = new JpxTagTreeEncoder(pb.BlocksWide, pb.BlocksHigh);
            var zeroPlanes = new JpxTagTreeEncoder(pb.BlocksWide, pb.BlocksHigh);
            for (int i = 0; i < pb.Blocks.Length; i++)
            {
                JpxEncodedBlock block = blocks[pb.Blocks[i]];
                int bx = i % pb.BlocksWide, by = i / pb.BlocksWide;
                inclusion.SetValue(bx, by, block.FirstLayer(layers, upTo));
                zeroPlanes.SetValue(bx, by, block.ZeroBitPlanes);
            }
            t = new JpxPrecinctBandTrees(inclusion, zeroPlanes);
            trees.Add(pb, t);
            return t;
        }

        /// <summary>Bits that hold <paramref name="value"/>: ⌊log2 value⌋ + 1, and 0 for 0 — a length field's need, and a coefficient's bit-planes.</summary>
        internal static int BitLength(int value) => BitLength((long)value);

        /// <summary>The same for a <c>long</c>: a wide coefficient's bit-planes.</summary>
        internal static int BitLength(long value)
        {
            int bits = 0;
            while (value > 0) { value >>= 1; bits++; }
            return bits;
        }
    }
}
