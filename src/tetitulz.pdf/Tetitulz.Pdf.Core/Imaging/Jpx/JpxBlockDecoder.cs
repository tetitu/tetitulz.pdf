// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// Tier 1 (T.800 Annex D): decodes one code-block's coefficient bits from the chunks tier 2
    /// gave it — the cleanup pass of the first bit-plane, then significance propagation, magnitude
    /// refinement and cleanup passes for each lower plane, in the stripe scan of §D.1, with the
    /// contexts of Tables D.1 to D.4 over the shared MQ decoder (Annex C). The code-block style
    /// flags are honoured: selective bypass (raw passes from the fifth bit-plane, §D.6), context
    /// reset after each pass, termination on each pass (a segment per pass, from the chunks),
    /// vertically causal contexts (§D.7) and segmentation symbols (§D.5, checked). ROI coefficients
    /// are scaled back by the Maxshift rule (§H.1).
    /// </summary>
    /// <remarks>
    /// <para><b>An instance is a workspace</b>, reused across every code-block of a decode: the
    /// magnitudes, the flag words, the joined chunks, the MQ decoder and the raw reader are
    /// allocated once, so a tile-component's thousands of blocks allocate nothing. The flag words
    /// are cleared per block; the magnitudes are not, since a sample's is written when it becomes
    /// significant and read only after. A workspace serves one thread at a time. The static <see cref="Decode(JpxCodeBlock, byte[], int, JpxCodeBlockStyle, int, int, out string)"/>
    /// is the one-block form the tests use.</para>
    /// <para><b>The neighbourhood is kept, not recomputed.</b> One flag word per column of a
    /// stripe (four rows) holds the significance of the column's 3 x 6 window, the signs of its
    /// six rows, and the refined and visited marks of its four — maintained as samples become
    /// significant, the way decoders' significance flags are — so a zero-coding context is
    /// one table lookup on nine bits, a sign context one lookup on eight, and a column with an
    /// empty window is skipped in one test. Vertically causal mode (§D.7) is a property of the
    /// maintenance: the first row of a stripe never marks the stripe above, so the row below a
    /// stripe stays unseen without a test per read.</para>
    /// <para><b>The passes are written per row of a stripe</b>, through helpers the JIT inlines
    /// with the row index a constant at each of the four call sites, so every shift and mask is
    /// an immediate and the coder's registers (<see cref="MqRegisters"/>) stay in a local for the
    /// whole pass. The bottom stripe of a block whose height is not a multiple of four goes
    /// through the same helpers with the row index a variable. The raw passes of selective bypass
    /// (§D.6) are separate, simpler loops: they need no context, and their reader is not the MQ
    /// decoder.</para>
    /// </remarks>
    internal sealed class JpxBlockDecoder
    {
        /// <summary>Nine zero-coding, five sign, three refinement, run-length, uniform.</summary>
        internal const int ContextCount = 19;
        /// <summary>The context numbering (Table D.7's rows): nine zero-coding contexts, then the five sign contexts, the three refinement contexts, run-length and uniform — the one statement, which the encoder's <see cref="JpxBlockContexts"/> reads.</summary>
        internal const int SignBase = 9, RefineBase = 14, RunLength = 17, Uniform = 18;

        /// <summary>
        /// Magnitudes are 64-bit with one fractional bit and a sign: more bit-planes than this
        /// cannot be held. T.800 allows 38-bit samples and M<sub>b</sub> up to 37 (E-2), plus an
        /// ROI shift (SPrgn, up to 255): a code-block asking for more than this is refused as a
        /// deviation and left zero. The class of legal file that reaches it is an ROI-coded image of
        /// about 30 bits and up, where H.2.2's minimum shift s = M<sub>b</sub> makes
        /// M<sub>b</sub> + s exceed 61 (and tier 2's zero-bit-plane count exceed 63); within 8–24-bit
        /// images neither bound is reachable, and other decoders refuse s ≥ 31 outright.
        /// </summary>
        internal const int MaxBitPlanes = 61;

        /// <summary>
        /// The most bit-planes an <c>int</c> band holds — twice the magnitude with the half bit and
        /// a sign in 32 bits. A band whose M<sub>b</sub> and ROI shift can exceed it is given to the
        /// <c>long</c> form of <see cref="Decode(JpxCodeBlock, byte[], int, JpxCodeBlockStyle, int, int, long[], int, int, out string)"/>;
        /// every band of every conformance codestream fits the <c>int</c> one, so the common decode
        /// keeps its narrower arrays.
        /// </summary>
        internal const int MaxNarrowBitPlanes = 30;

        // The flag word, one uint per column of a stripe, with a border column on each side and a
        // border stripe above and below the block:
        //   bits 0-17   σ: significance over the column's 3 x 6 window — rows −1 to 4 of the stripe
        //               (the row above it, its four, the row below) by columns −1 to 1, at bit
        //               3(r + 1) + (c + 1). The 3 x 3 neighbourhood of stripe row k is therefore
        //               (word >> 3k) & 0x1FF — NW N NE, W self E, SW S SE from bit 0 up — with the
        //               sample itself at bit 4 of that.
        //   bits 18-23  χ: the sign of this column's rows −1 to 4 (1 for negative), at bit 18 + (r + 1).
        //   bits 24-27  μ: stripe row k has been refined once (Table D.4), at bit 24 + k.
        //   bits 28-31  π: stripe row k was coded in this plane's significance pass, at bit 28 + k.
        private const uint SigmaAll = 0x3FFFF;
        private const uint SelfAll = (1u << 4) | (1u << 7) | (1u << 10) | (1u << 13);
        private const uint PiAll = 0xFu << 28;
        private const int ChiRow0 = 19, MuRow0 = 24, PiRow0 = 28;
        private const int NeighbourMask = 0x1FF & ~(1 << 4);

        /// <summary>Table D.1 by orientation, indexed by a row's nine-bit neighbourhood (the centre bit is ignored).</summary>
        private static readonly byte[][] ZeroCodingContexts = BuildZeroCodingContexts();

        /// <summary>Tables D.2 and D.3, indexed by <see cref="SignIndex"/>'s eight bits: the context, and the bit the decoded symbol is XORed with.</summary>
        private static readonly byte[] SignContexts, SignXorBits;

        static JpxBlockDecoder()
        {
            SignContexts = new byte[256];
            SignXorBits = new byte[256];
            for (int index = 0; index < 256; index++)
            {
                int hc = Contribution(index, 0) + Contribution(index, 2);
                int vc = Contribution(index, 4) + Contribution(index, 6);
                SignContexts[index] = (byte)SignContext(hc, vc, out int xorBit);
                SignXorBits[index] = (byte)xorBit;
            }
        }

        private uint[] _flags = Array.Empty<uint>();
        private JpxHtBlockDecoder _ht;

        /// <summary>This workspace's HT block decoder (ITU-T T.814), for a tile-component of HT code-blocks: made on first use, reused after.</summary>
        internal JpxHtBlockDecoder Ht => _ht ?? (_ht = new JpxHtBlockDecoder());

        private Dictionary<int, long[]> _blockValues;

        /// <summary>
        /// A zeroed array of exactly <paramref name="length"/> values, this workspace's own: the transcoder decodes
        /// each block into one and re-codes it, and its encoders take an array of the block's area. A tile's blocks
        /// come in at most four sizes (whole, and cut by the right edge, the bottom, or both), so one array a size
        /// serves every block this workspace decodes. The caller is done with it before it asks again.
        /// </summary>
        internal long[] BlockValues(int length)
        {
            _blockValues ??= new Dictionary<int, long[]>();
            if (!_blockValues.TryGetValue(length, out long[] values))
                _blockValues[length] = values = new long[length];
            else
                Array.Clear(values, 0, length);
            return values;
        }

        private long[] _data = Array.Empty<long>();     // twice the magnitude, with the half bit; the sign is in the flags
        private byte[] _joined = Array.Empty<byte>();
        private Segment[] _segments = new Segment[8];
        private readonly MqDecoder _mq = new MqDecoder(Array.Empty<byte>(), 0, 0, ContextCount);
        private readonly JpxRawReader _raw = new JpxRawReader(Array.Empty<byte>());
        private int _w, _h, _stride, _orientation;
        private bool _causal;

        /// <summary>
        /// Decodes <paramref name="block"/>, whose chunks lie in <paramref name="data"/>, into a fresh
        /// array: one value per sample in raster order — twice the reconstructed magnitude, the half
        /// of equation E-6 (r = ½) at the last decoded bit-plane, negated for a negative coefficient;
        /// 0 for a coefficient never found significant. <paramref name="magnitudeBitPlanes"/> is
        /// M<sub>b</sub> (E-2) and <paramref name="roiShift"/> the RGN scaling, which raises the plane
        /// count (H-3). <paramref name="problem"/> names a segmentation symbol that failed or a pass
        /// count past what the planes allow; the decode still returns what it read.
        /// </summary>
        internal static int[] Decode(JpxCodeBlock block, byte[] data, int orientation, JpxCodeBlockStyle style,
            int magnitudeBitPlanes, int roiShift, out string problem)
        {
            int w = Math.Max(0, (int)block.Bounds.Width), h = Math.Max(0, (int)block.Bounds.Height);
            var result = new int[w * h];
            new JpxBlockDecoder().Decode(block, data, orientation, style, magnitudeBitPlanes, roiShift, result, 0, w, out problem);
            return result;
        }

        /// <summary>The one-block form into a <c>long</c> array, for a band past <see cref="MaxNarrowBitPlanes"/>.</summary>
        internal static long[] DecodeWide(JpxCodeBlock block, byte[] data, int orientation, JpxCodeBlockStyle style,
            int magnitudeBitPlanes, int roiShift, out string problem)
        {
            int w = Math.Max(0, (int)block.Bounds.Width), h = Math.Max(0, (int)block.Bounds.Height);
            var result = new long[w * h];
            new JpxBlockDecoder().Decode(block, data, orientation, style, magnitudeBitPlanes, roiShift, result, 0, w, out problem);
            return result;
        }

        /// <summary>
        /// Decodes <paramref name="block"/> into <paramref name="target"/>: sample (x, y) of the block
        /// goes to <c>target[targetOffset + y · targetStride + x]</c>, in the convention of the
        /// one-block <see cref="Decode(JpxCodeBlock, byte[], int, JpxCodeBlockStyle, int, int, out string)"/>.
        /// Every sample of the block's rectangle is written, zeros for a block with nothing to decode.
        /// An <c>int</c> target holds <see cref="MaxNarrowBitPlanes"/> planes; a block past that is a
        /// problem here and a decode into the <c>long</c> form.
        /// </summary>
        internal void Decode(JpxCodeBlock block, byte[] data, int orientation, JpxCodeBlockStyle style,
            int magnitudeBitPlanes, int roiShift, int[] target, int targetOffset, int targetStride, out string problem)
        {
            if (!DecodeToWorkspace(block, data, orientation, style, magnitudeBitPlanes, roiShift, MaxNarrowBitPlanes, out int w, out int h, out problem))
            {
                Clear(target, targetOffset, targetStride, w, h);
                return;
            }
            Output(target, targetOffset, targetStride, roiShift);
        }

        /// <summary>As the <c>int</c> form, into a <c>long</c> array that holds every plane count up to <see cref="MaxBitPlanes"/>.</summary>
        internal void Decode(JpxCodeBlock block, byte[] data, int orientation, JpxCodeBlockStyle style,
            int magnitudeBitPlanes, int roiShift, long[] target, int targetOffset, int targetStride, out string problem)
        {
            if (!DecodeToWorkspace(block, data, orientation, style, magnitudeBitPlanes, roiShift, MaxBitPlanes, out int w, out int h, out problem))
            {
                Clear(target, targetOffset, targetStride, w, h);
                return;
            }
            Output(target, targetOffset, targetStride, roiShift);
        }

        /// <summary>
        /// The decode itself, into the workspace: true when <c>_data</c> holds the block's values to
        /// output; false when the target is to be cleared instead — a block with nothing to decode
        /// (<paramref name="w"/> and <paramref name="h"/> tell how much to clear; 0 for an empty
        /// block), or one whose planes are malformed or past <paramref name="maxPlanes"/>.
        /// </summary>
        private bool DecodeToWorkspace(JpxCodeBlock block, byte[] data, int orientation, JpxCodeBlockStyle style,
            int magnitudeBitPlanes, int roiShift, int maxPlanes, out int w, out int h, out string problem)
        {
            problem = null;
            w = (int)block.Bounds.Width;
            h = (int)block.Bounds.Height;
            if (w <= 0 || h <= 0)
            {
                w = h = 0;
                return false;
            }
            if (block.Chunks.Count == 0 || block.Passes == 0) return false;

            int planes = magnitudeBitPlanes + roiShift - block.ZeroBitPlanes;
            if (planes <= 0)
            {
                problem = magnitudeBitPlanes + roiShift <= 0
                    ? $"the band has no magnitude bit-planes (M_b is {magnitudeBitPlanes + roiShift}: guard bits and exponent give none, E-2), yet the code-block carries coding passes"
                    : $"the code-block signals {block.ZeroBitPlanes} zero bit-planes of {magnitudeBitPlanes + roiShift}, and coding passes for none";
                return false;
            }
            if (planes > maxPlanes)
            {
                problem = $"the code-block has {planes} bit-planes to decode; at most {maxPlanes} can be held";
                return false;
            }
            int passes = block.Passes;
            int maxPasses = 3 * planes - 2;
            if (passes > maxPasses)
            {
                problem = $"the code-block signals {passes} coding passes for {planes} bit-planes, which take {maxPasses}";
                passes = maxPasses;
            }

            // The chunks, joined unless already contiguous; each codeword segment a range of that
            // buffer (Tables D.8, D.9 as tier 2 marked them).
            byte[] buffer = Join(block, data, out int segmentCount);

            bool bypass = (style & JpxCodeBlockStyle.SelectiveBypass) != 0;
            bool reset = (style & JpxCodeBlockStyle.ResetContexts) != 0;
            bool segmentationSymbols = (style & JpxCodeBlockStyle.SegmentationSymbols) != 0;
            Prepare(w, h, orientation, (style & JpxCodeBlockStyle.VerticallyCausal) != 0);

            MqDecoder mq = _mq;
            JpxRawReader raw = _raw;
            ResetContexts(mq.ContextStates);

            int segment = -1, segmentPassesLeft = 0, segmentStart = 0, segmentEnd = 0;
            bool segmentIsRaw = false;
            for (int pass = 0; pass < passes; pass++)
            {
                int plane, type;   // type: 0 significance propagation, 1 magnitude refinement, 2 cleanup
                if (pass == 0)
                {
                    plane = planes - 1;
                    type = 2;
                }
                else
                {
                    plane = planes - 1 - ((pass - 1) / 3 + 1);
                    type = (pass - 1) % 3;
                }
                bool rawPass = bypass && pass >= 10 && type != 2;

                if (segmentPassesLeft == 0)
                {
                    segment++;
                    Segment s = segment < segmentCount ? _segments[segment] : new Segment(buffer.Length, buffer.Length, passes);
                    segmentStart = s.Start;
                    segmentEnd = s.End;
                    segmentPassesLeft = s.Passes;
                    segmentIsRaw = rawPass;
                    if (rawPass) raw.Start(buffer, s.Start, s.End);
                    else mq.Restart(buffer, s.Start, s.End);
                }
                else if (rawPass != segmentIsRaw)
                {
                    // A segment whose passes change coding mode is malformed (Table D.9); read it in
                    // the pass's mode from its start rather than stop.
                    segmentIsRaw = rawPass;
                    if (rawPass) raw.Start(buffer, segmentStart, segmentEnd);
                    else mq.Restart(buffer, segmentStart, segmentEnd);
                }
                segmentPassesLeft--;

                switch (type)
                {
                    case 0:
                        if (rawPass) SignificancePassRaw(plane);
                        else SignificancePass(plane);
                        break;
                    case 1:
                        if (rawPass) RefinementPassRaw(plane);
                        else RefinementPass(plane);
                        break;
                    default:
                        CleanupPass(plane);
                        // The symbol's four bits are always consumed; a block cut short by the tile data decodes
                        // its later passes from padding, so a wrong symbol there is the truncation's, already reported.
                        if (segmentationSymbols && !SegmentationSymbolHolds() && !block.Truncated && problem == null)
                            problem = $"the segmentation symbol after coding pass {pass} is wrong (T.800 §D.5)";
                        break;
                }
                if (reset) ResetContexts(mq.ContextStates);
            }
            return true;
        }

        /// <summary>
        /// A decoded value as a plain integer coefficient: the half dropped, the sign kept. For a
        /// reversible band fully decoded this is equation E-7's exact value; partially decoded it is
        /// E-8 with r = ½. (An arithmetic shift of the signed value would floor negatives one too low.)
        /// </summary>
        internal static int Truncate(int value) => value < 0 ? -((-value) >> 1) : value >> 1;

        /// <summary>The same for a <c>long</c> band's value.</summary>
        internal static long Truncate(long value) => value < 0 ? -((-value) >> 1) : value >> 1;

        /// <summary>Table D.7: every context at state 0, but uniform at 46, run-length at 3 and the all-zero-neighbours context at 4.</summary>
        internal static void ResetContexts(int[] states)
        {
            // A loop, not Array.Clear: nineteen stores, once per block and under the reset style
            // after every pass, cost less than a call into the runtime's memory clear.
            for (int i = 0; i < states.Length; i++) states[i] = 0;
            states[Uniform] = MqDecoder.State(46, 0);
            states[RunLength] = MqDecoder.State(3, 0);
            states[0] = MqDecoder.State(4, 0);
        }

        /// <summary>Table D.1: the context label from ΣH, ΣV and ΣD for a band orientation (0 LL, 1 HL, 2 LH, 3 HH).</summary>
        internal static int ZeroCodingContext(int orientation, int h, int v, int d)
        {
            switch (orientation)
            {
                case 1:   // HL: the roles of the horizontal and vertical sums swap
                    (h, v) = (v, h);
                    goto case 0;
                case 0:   // LL and LH
                case 2:
                    if (h == 2) return 8;
                    if (h == 1) return v >= 1 ? 7 : d >= 1 ? 6 : 5;
                    if (v == 2) return 4;
                    if (v == 1) return 3;
                    return d >= 2 ? 2 : d;
                default:  // HH
                    int hv = h + v;
                    if (d >= 3) return 8;
                    if (d == 2) return hv >= 1 ? 7 : 6;
                    if (d == 1) return hv >= 2 ? 5 : hv == 1 ? 4 : 3;
                    return hv >= 2 ? 2 : hv;
            }
        }

        /// <summary>
        /// Tables D.2 and D.3: the context label and the bit the decoded symbol is XORed with, from
        /// the horizontal and vertical contributions (each the sum of two neighbours' −1, 0 or 1).
        /// </summary>
        internal static int SignContext(int hc, int vc, out int xorBit)
        {
            hc = Math.Max(-1, Math.Min(1, hc));
            vc = Math.Max(-1, Math.Min(1, vc));
            if (hc == 0)
            {
                xorBit = vc < 0 ? 1 : 0;
                return SignBase + Math.Abs(vc);            // 9, 10
            }
            if (hc > 0)
            {
                xorBit = 0;
                return SignBase + 3 + vc;                  // 11, 12, 13
            }
            xorBit = 1;
            return SignBase + 3 - vc;                      // 11, 12, 13
        }

        /// <summary>
        /// The eight-bit index of the sign tables: W, E, N, S each as a significance bit followed by
        /// a negative bit (bits 0-1, 2-3, 4-5, 6-7).
        /// </summary>
        internal static int SignIndex(bool wSignificant, bool wNegative, bool eSignificant, bool eNegative,
            bool nSignificant, bool nNegative, bool sSignificant, bool sNegative) =>
            (wSignificant ? 1 : 0) | (wNegative ? 2 : 0) | (eSignificant ? 4 : 0) | (eNegative ? 8 : 0)
            | (nSignificant ? 16 : 0) | (nNegative ? 32 : 0) | (sSignificant ? 64 : 0) | (sNegative ? 128 : 0);

        /// <summary>The sign context Table D.3 gives a neighbourhood, by <see cref="SignIndex"/>, with its XOR bit.</summary>
        internal static int SignContextAt(int index, out int xorBit)
        {
            xorBit = SignXorBits[index];
            return SignContexts[index];
        }

        /// <summary>The zero-coding context Table D.1 gives a nine-bit neighbourhood (NW N NE W · E SW S SE from bit 0) in a band orientation.</summary>
        internal static int ZeroCodingContextAt(int orientation, int neighbourhood) => ZeroCodingContexts[orientation][neighbourhood & 0x1FF];

        private static byte[][] BuildZeroCodingContexts()
        {
            var tables = new byte[4][];
            for (int orientation = 0; orientation < 4; orientation++)
            {
                var table = new byte[512];
                for (int p = 0; p < 512; p++)
                {
                    int h = ((p >> 3) & 1) + ((p >> 5) & 1);
                    int v = ((p >> 1) & 1) + ((p >> 7) & 1);
                    int d = (p & 1) + ((p >> 2) & 1) + ((p >> 6) & 1) + ((p >> 8) & 1);
                    table[p] = (byte)ZeroCodingContext(orientation, h, v, d);
                }
                tables[orientation] = table;
            }
            return tables;
        }

        /// <summary>A neighbour's contribution of Table D.2 from its two bits at <paramref name="bit"/> (significant) and <paramref name="bit"/> + 1 (negative).</summary>
        private static int Contribution(int index, int bit) =>
            ((index >> bit) & 1) == 0 ? 0 : ((index >> (bit + 1)) & 1) != 0 ? -1 : 1;

        private readonly struct Segment
        {
            internal Segment(int start, int end, int passes)
            {
                Start = start;
                End = end;
                Passes = passes;
            }

            internal int Start { get; }
            internal int End { get; }
            internal int Passes { get; }
        }

        /// <summary>
        /// The buffer the block's codeword segments lie in, with <see cref="_segments"/> filled: the
        /// tile data itself when the chunks are contiguous there (one layer, or one packet per
        /// layer that happens to follow the last), else the chunks copied end to end into the
        /// workspace's own buffer.
        /// </summary>
        private byte[] Join(JpxCodeBlock block, byte[] data, out int segmentCount)
        {
            IReadOnlyList<JpxCodeChunk> chunks = block.Chunks;
            long total = 0, next = chunks[0].Data.Start;
            bool contiguous = true;
            foreach (JpxCodeChunk c in chunks)
            {
                if (c.Data.Start != next) contiguous = false;
                long length = Math.Max(0, c.Data.Length);
                next = c.Data.Start + length;
                total += length;
            }

            byte[] buffer;
            int at;
            if (contiguous)
            {
                buffer = data;
                at = (int)chunks[0].Data.Start;
            }
            else
            {
                if (_joined.Length < total) _joined = new byte[Math.Max(total, 2L * _joined.Length)];
                buffer = _joined;
                at = 0;
            }

            segmentCount = 0;
            int segmentStart = at, segmentPasses = 0;
            foreach (JpxCodeChunk c in chunks)
            {
                int length = (int)Math.Max(0, c.Data.Length);
                if (!contiguous) Array.Copy(data, c.Data.Start, buffer, at, length);
                at += length;
                segmentPasses += c.Passes;
                if (c.Terminated)
                {
                    AddSegment(ref segmentCount, new Segment(segmentStart, at, segmentPasses));
                    segmentStart = at;
                    segmentPasses = 0;
                }
            }
            if (segmentPasses > 0 || segmentCount == 0) AddSegment(ref segmentCount, new Segment(segmentStart, at, segmentPasses));
            return buffer;
        }

        private void AddSegment(ref int count, Segment segment)
        {
            if (count == _segments.Length) Array.Resize(ref _segments, 2 * count);
            _segments[count++] = segment;
        }

        /// <summary>
        /// Sizes the workspace for a block of w by h and clears its flag words with the borders.
        /// The magnitudes are not cleared: a sample's is written when it becomes significant, and
        /// read only after that — by the refinement passes, which visit significant samples, and
        /// by <see cref="Output(int[], int, int, int)"/>, which reads the significance bit first.
        /// </summary>
        private void Prepare(int w, int h, int orientation, bool causal)
        {
            _w = w;
            _h = h;
            _stride = w + 2;
            _orientation = orientation;
            _causal = causal;
            int samples = w * h;
            if (_data.Length < samples) _data = new long[Math.Max(samples, 2 * _data.Length)];
            int words = _stride * ((h + 3) / 4 + 2);
            if (_flags.Length < words) _flags = new uint[Math.Max(words, 2 * _flags.Length)];
            else Array.Clear(_flags, 0, words);
        }

        private static void Clear(int[] target, int offset, int stride, int w, int h)
        {
            for (int y = 0; y < h; y++) Array.Clear(target, offset + y * stride, w);
        }

        private static void Clear(long[] target, int offset, int stride, int w, int h)
        {
            for (int y = 0; y < h; y++) Array.Clear(target, offset + y * stride, w);
        }

        // ---- The three passes over the MQ decoder ---------------------------------------------
        //
        // Each walks the stripes (§D.1): the full ones with the four rows unrolled through the
        // per-row helper below, the bottom one — fewer than four rows, when the height is not a
        // multiple of four — through the same helper with the row a variable.

        /// <summary>§D.3.1: insignificant samples with a non-zero context; each is marked visited whether or not it becomes significant.</summary>
        private void SignificancePass(int plane)
        {
            uint[] flags = _flags;
            byte[] zc = ZeroCodingContexts[_orientation];
            MqDecoder mq = _mq;
            MqRegisters r = mq.Registers;
            int w = _w, h = _h, stride = _stride;
            int di0 = 0, fi0 = stride + 1, y0 = 0;
            for (; y0 + 4 <= h; y0 += 4, di0 += 4 * w, fi0 += stride)
            {
                int fi = fi0, di = di0;
                for (int x = 0; x < w; x++, fi++, di++)
                {
                    uint f = flags[fi];
                    if ((f & SigmaAll) == 0) continue;   // an empty window: every row's context is 0
                    SignificanceRow(ref r, ref f, flags, zc, mq, fi, di, 0, plane);
                    SignificanceRow(ref r, ref f, flags, zc, mq, fi, di + w, 1, plane);
                    SignificanceRow(ref r, ref f, flags, zc, mq, fi, di + 2 * w, 2, plane);
                    SignificanceRow(ref r, ref f, flags, zc, mq, fi, di + 3 * w, 3, plane);
                    flags[fi] = f;
                }
            }
            if (y0 < h)
            {
                int rows = h - y0, fi = fi0, di = di0;
                for (int x = 0; x < w; x++, fi++, di++)
                {
                    uint f = flags[fi];
                    if ((f & SigmaAll) == 0) continue;
                    for (int k = 0; k < rows; k++) SignificanceRow(ref r, ref f, flags, zc, mq, fi, di + k * w, k, plane);
                    flags[fi] = f;
                }
            }
            mq.Registers = r;
        }

        /// <summary>One row of the significance pass: a sample not yet significant whose context is not 0 is coded, and marked visited either way.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SignificanceRow(ref MqRegisters r, ref uint f, uint[] flags, byte[] zc, MqDecoder mq, int fi, int d, int k, int plane)
        {
            int shift = 3 * k;
            if ((f & (1u << (shift + 4))) != 0) return;
            int context = zc[(int)((f >> shift) & 0x1FF)];
            if (context == 0) return;
            if (mq.Decode(ref r, context) != 0)
            {
                int index = SignIndexAt(f, flags, fi, k);
                int negative = mq.Decode(ref r, SignContexts[index]) ^ SignXorBits[index];   // D-1
                _data[d] = 3L << plane;   // the bit, and the half below it
                f = MarkSignificant(f, fi, k, negative);
            }
            f |= 1u << (PiRow0 + k);
        }

        /// <summary>§D.3.3: significant samples not coded in this plane's significance pass.</summary>
        private void RefinementPass(int plane)
        {
            uint[] flags = _flags;
            long[] data = _data;
            MqDecoder mq = _mq;
            MqRegisters r = mq.Registers;
            int w = _w, h = _h, stride = _stride;
            long half = 1L << plane;   // the previous plane's half becomes this plane's bit; a new half goes below it
            int di0 = 0, fi0 = stride + 1, y0 = 0;
            for (; y0 + 4 <= h; y0 += 4, di0 += 4 * w, fi0 += stride)
            {
                int fi = fi0, di = di0;
                for (int x = 0; x < w; x++, fi++, di++)
                {
                    uint f = flags[fi];
                    if ((f & SelfAll) == 0) continue;   // no significant sample in the column
                    RefinementRow(ref r, ref f, data, mq, di, 0, half);
                    RefinementRow(ref r, ref f, data, mq, di + w, 1, half);
                    RefinementRow(ref r, ref f, data, mq, di + 2 * w, 2, half);
                    RefinementRow(ref r, ref f, data, mq, di + 3 * w, 3, half);
                    flags[fi] = f;
                }
            }
            if (y0 < h)
            {
                int rows = h - y0, fi = fi0, di = di0;
                for (int x = 0; x < w; x++, fi++, di++)
                {
                    uint f = flags[fi];
                    if ((f & SelfAll) == 0) continue;
                    for (int k = 0; k < rows; k++) RefinementRow(ref r, ref f, data, mq, di + k * w, k, half);
                    flags[fi] = f;
                }
            }
            mq.Registers = r;
        }

        /// <summary>One row of the refinement pass: a significant sample not visited this plane takes its next bit (Table D.4's three contexts) and is marked refined.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RefinementRow(ref MqRegisters r, ref uint f, long[] data, MqDecoder mq, int d, int k, long half)
        {
            int shift = 3 * k;
            if ((f & ((1u << (shift + 4)) | (1u << (PiRow0 + k)))) != 1u << (shift + 4)) return;
            int context = (f & (1u << (MuRow0 + k))) != 0 ? RefineBase + 2
                : ((f >> shift) & NeighbourMask) != 0 ? RefineBase + 1 : RefineBase;
            data[d] += mq.Decode(ref r, context) != 0 ? half : -half;
            f |= 1u << (MuRow0 + k);
        }

        /// <summary>§D.3.4: the samples neither significant nor coded in this plane, with run-length coding of all-zero columns (Table D.5).</summary>
        private void CleanupPass(int plane)
        {
            uint[] flags = _flags;
            byte[] zc = ZeroCodingContexts[_orientation];
            MqDecoder mq = _mq;
            MqRegisters r = mq.Registers;
            int w = _w, h = _h, stride = _stride;
            int di0 = 0, fi0 = stride + 1, y0 = 0;
            for (; y0 + 4 <= h; y0 += 4, di0 += 4 * w, fi0 += stride)
            {
                int fi = fi0, di = di0;
                for (int x = 0; x < w; x++, fi++, di++)
                {
                    uint f = flags[fi];
                    if ((f & SigmaAll) == 0)
                    {
                        // Four insignificant samples with zero contexts (none was visited: a visited
                        // sample had a significant neighbour, which would be in the window still).
                        if (mq.Decode(ref r, RunLength) == 0) continue;   // all four stay insignificant
                        int first = mq.Decode(ref r, Uniform) << 1;
                        first |= mq.Decode(ref r, Uniform);
                        int index = SignIndexAt(f, flags, fi, first);
                        int negative = mq.Decode(ref r, SignContexts[index]) ^ SignXorBits[index];
                        _data[di + first * w] = 3L << plane;
                        f = MarkSignificant(f, fi, first, negative);
                        if (first < 1) CleanupRow(ref r, ref f, flags, zc, mq, fi, di + w, 1, plane);
                        if (first < 2) CleanupRow(ref r, ref f, flags, zc, mq, fi, di + 2 * w, 2, plane);
                        CleanupRow(ref r, ref f, flags, zc, mq, fi, di + 3 * w, 3, plane);
                        flags[fi] = f;   // no visited mark to clear: the window was empty
                        continue;
                    }
                    CleanupRow(ref r, ref f, flags, zc, mq, fi, di, 0, plane);
                    CleanupRow(ref r, ref f, flags, zc, mq, fi, di + w, 1, plane);
                    CleanupRow(ref r, ref f, flags, zc, mq, fi, di + 2 * w, 2, plane);
                    CleanupRow(ref r, ref f, flags, zc, mq, fi, di + 3 * w, 3, plane);
                    flags[fi] = f & ~PiAll;
                }
            }
            if (y0 < h)
            {
                int rows = h - y0, fi = fi0, di = di0;
                for (int x = 0; x < w; x++, fi++, di++)
                {
                    uint f = flags[fi];
                    for (int k = 0; k < rows; k++) CleanupRow(ref r, ref f, flags, zc, mq, fi, di + k * w, k, plane);
                    flags[fi] = f & ~PiAll;
                }
            }
            mq.Registers = r;
        }

        /// <summary>One row of the cleanup pass: a sample neither significant nor visited this plane is coded with its zero-coding context.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CleanupRow(ref MqRegisters r, ref uint f, uint[] flags, byte[] zc, MqDecoder mq, int fi, int d, int k, int plane)
        {
            int shift = 3 * k;
            if ((f & ((1u << (shift + 4)) | (1u << (PiRow0 + k)))) != 0) return;
            if (mq.Decode(ref r, zc[(int)((f >> shift) & 0x1FF)]) != 0)
            {
                int index = SignIndexAt(f, flags, fi, k);
                int negative = mq.Decode(ref r, SignContexts[index]) ^ SignXorBits[index];
                _data[d] = 3L << plane;
                f = MarkSignificant(f, fi, k, negative);
            }
        }

        // ---- The raw passes of selective bypass (§D.6) --------------------------------------------

        /// <summary>The significance pass with raw bits: the same samples are coded, each bit and sign read as it is (D-2).</summary>
        private void SignificancePassRaw(int plane)
        {
            uint[] flags = _flags;
            byte[] zc = ZeroCodingContexts[_orientation];
            JpxRawReader raw = _raw;
            int w = _w, h = _h, stride = _stride;
            int di0 = 0, fi0 = stride + 1;
            for (int y0 = 0; y0 < h; y0 += 4, di0 += 4 * w, fi0 += stride)
            {
                int rows = Math.Min(4, h - y0);
                int fi = fi0, di = di0;
                for (int x = 0; x < w; x++, fi++, di++)
                {
                    uint f = flags[fi];
                    if ((f & SigmaAll) == 0) continue;
                    for (int k = 0; k < rows; k++)
                    {
                        int shift = 3 * k;
                        if ((f & (1u << (shift + 4))) != 0) continue;
                        if (zc[(int)((f >> shift) & 0x1FF)] == 0) continue;
                        if (raw.Bit() != 0)
                        {
                            _data[di + k * w] = 3L << plane;
                            f = MarkSignificant(f, fi, k, raw.Bit());
                        }
                        f |= 1u << (PiRow0 + k);
                    }
                    flags[fi] = f;
                }
            }
        }

        /// <summary>The refinement pass with raw bits.</summary>
        private void RefinementPassRaw(int plane)
        {
            uint[] flags = _flags;
            long[] data = _data;
            JpxRawReader raw = _raw;
            int w = _w, h = _h, stride = _stride;
            long half = 1L << plane;
            int di0 = 0, fi0 = stride + 1;
            for (int y0 = 0; y0 < h; y0 += 4, di0 += 4 * w, fi0 += stride)
            {
                int rows = Math.Min(4, h - y0);
                int fi = fi0, di = di0;
                for (int x = 0; x < w; x++, fi++, di++)
                {
                    uint f = flags[fi];
                    if ((f & SelfAll) == 0) continue;
                    for (int k = 0; k < rows; k++)
                    {
                        int shift = 3 * k;
                        if ((f & ((1u << (shift + 4)) | (1u << (PiRow0 + k)))) != 1u << (shift + 4)) continue;
                        data[di + k * w] += raw.Bit() != 0 ? half : -half;
                        f |= 1u << (MuRow0 + k);
                    }
                    flags[fi] = f;
                }
            }
        }

        /// <summary>§D.5: four uniform-context bits after a cleanup pass must read 1010.</summary>
        private bool SegmentationSymbolHolds()
        {
            int symbol = 0;
            for (int k = 0; k < 4; k++) symbol = (symbol << 1) | _mq.Decode(Uniform);
            return symbol == 0xA;
        }

        /// <summary>The sign-magnitude result into an <c>int</c> target, ROI coefficients scaled back (§H.1: those at or above 2^s); the caller kept the planes within <see cref="MaxNarrowBitPlanes"/>.</summary>
        private void Output(int[] target, int offset, int stride, int roiShift)
        {
            uint[] flags = _flags;
            long[] data = _data;
            int w = _w, h = _h;
            long threshold = RoiThreshold(roiShift);
            int di0 = 0, fi0 = _stride + 1;
            for (int y0 = 0; y0 < h; y0 += 4, di0 += 4 * w, fi0 += _stride)
            {
                int rows = Math.Min(4, h - y0);
                int fi = fi0, di = di0;
                for (int x = 0; x < w; x++, fi++, di++)
                {
                    uint f = flags[fi];
                    int d = di, t = offset + y0 * stride + x;
                    for (int k = 0; k < rows; k++, d += w, t += stride)
                    {
                        if ((f & (1u << (3 * k + 4))) == 0)
                        {
                            target[t] = 0;   // never significant; its magnitude was not written
                            continue;
                        }
                        long m = data[d];
                        if (roiShift > 0 && m >= threshold) m >>= roiShift;
                        target[t] = (int)(((f >> (ChiRow0 + k)) & 1) != 0 ? -m : m);
                    }
                }
            }
        }

        /// <summary>The same into a <c>long</c> target.</summary>
        private void Output(long[] target, int offset, int stride, int roiShift)
        {
            uint[] flags = _flags;
            long[] data = _data;
            int w = _w, h = _h;
            long threshold = RoiThreshold(roiShift);
            int di0 = 0, fi0 = _stride + 1;
            for (int y0 = 0; y0 < h; y0 += 4, di0 += 4 * w, fi0 += _stride)
            {
                int rows = Math.Min(4, h - y0);
                int fi = fi0, di = di0;
                for (int x = 0; x < w; x++, fi++, di++)
                {
                    uint f = flags[fi];
                    int d = di, t = offset + y0 * stride + x;
                    for (int k = 0; k < rows; k++, d += w, t += stride)
                    {
                        if ((f & (1u << (3 * k + 4))) == 0)
                        {
                            target[t] = 0;   // never significant; its magnitude was not written
                            continue;
                        }
                        long m = data[d];
                        if (roiShift > 0 && m >= threshold) m >>= roiShift;
                        target[t] = ((f >> (ChiRow0 + k)) & 1) != 0 ? -m : m;
                    }
                }
            }
        }

        /// <summary>
        /// §H.1's threshold in half units: 2^(s + 1). A magnitude holds at most <see cref="MaxBitPlanes"/>
        /// planes, so a shift above that puts every coefficient below 2^s — background, unshifted —
        /// and the threshold need not be computed: 1L &lt;&lt; (s + 1) wraps for s ≥ 63.
        /// </summary>
        internal static long RoiThreshold(int roiShift) =>
            roiShift > 0 && roiShift <= MaxBitPlanes ? 1L << (roiShift + 1) : long.MaxValue;

        /// <summary>
        /// The sign tables' index (<see cref="SignIndex"/>) for row <paramref name="k"/> of the column
        /// whose word is <paramref name="f"/> at <paramref name="fi"/>: the horizontal neighbours'
        /// significance from the word, their signs from the words beside it, the vertical ones' both
        /// from the word.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int SignIndexAt(uint f, uint[] flags, int fi, int k)
        {
            int nb = (int)(f >> (3 * k));
            return ((nb >> 3) & 1) | (((int)(flags[fi - 1] >> (ChiRow0 + k)) & 1) << 1)
                 | (((nb >> 5) & 1) << 2) | (((int)(flags[fi + 1] >> (ChiRow0 + k)) & 1) << 3)
                 | (((nb >> 1) & 1) << 4) | (((int)(f >> (ChiRow0 - 1 + k)) & 1) << 5)
                 | (((nb >> 7) & 1) << 6) | (((int)(f >> (ChiRow0 + 1 + k)) & 1) << 7);
        }

        /// <summary>
        /// Sample row <paramref name="k"/> of the stripe whose column word is <paramref name="f"/> at
        /// <paramref name="fi"/> has become significant with the sign <paramref name="negative"/>:
        /// every word whose window holds the sample is updated — its own (returned, and stored), the
        /// two beside it, the stripe below's when it is the last row, and the stripe above's when it
        /// is the first, unless vertically causal (§D.7).
        /// </summary>
        private uint MarkSignificant(uint f, int fi, int k, int negative)
        {
            uint[] flags = _flags;
            int shift = 3 * k;
            f |= (1u << (shift + 4)) | ((uint)negative << (ChiRow0 + k));
            flags[fi] = f;
            flags[fi - 1] |= 1u << (shift + 5);   // the column to the west sees an east neighbour
            flags[fi + 1] |= 1u << (shift + 3);   // the column to the east a west one
            if (k == 0)
            {
                if (!_causal)
                {
                    int north = fi - _stride;   // the stripe above: its row 4, bits 15-17 and χ bit 23
                    flags[north] |= (1u << 16) | ((uint)negative << 23);
                    flags[north - 1] |= 1u << 17;
                    flags[north + 1] |= 1u << 15;
                }
            }
            else if (k == 3)
            {
                int south = fi + _stride;   // the stripe below: its row −1, bits 0-2 and χ bit 18
                flags[south] |= (1u << 1) | ((uint)negative << 18);
                flags[south - 1] |= 1u << 2;
                flags[south + 1] |= 1u << 0;
            }
            return f;
        }
    }
}
