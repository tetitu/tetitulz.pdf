// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// Reads one packet (T.800 §B.10) into a tile layout's code-blocks: the header's inclusion,
    /// zero bit-plane, pass-count and length fields in the order §B.10.8 gives, the EPH marker when
    /// the COD requires it, then the body, sliced into one chunk per signalled length.
    /// </summary>
    internal static class JpxPacketDecoder
    {
        private const int Eph = 0xFF92;

        /// <summary>The zero bit-plane tag tree's threshold cap: a code-block claiming this many or more missing planes is malformed (P is below M<sub>b</sub> ≤ 37).</summary>
        private const int MaxZeroBitPlanes = 64;

        private struct Pending
        {
            internal JpxCodeBlock Block;
            internal int Passes;
            internal int Length;
            internal bool Terminated;
        }

        /// <summary>
        /// Reads the packet at <paramref name="address"/>. The header comes from <paramref name="header"/>;
        /// when <paramref name="headerInStream"/> it sits in <paramref name="data"/> at <paramref name="bodyPos"/>
        /// and the body follows it, otherwise (PPM, PPT) the body starts at <paramref name="bodyPos"/>.
        /// On return <paramref name="bodyPos"/> is past the body. False with <paramref name="problem"/>
        /// when the header is malformed; <paramref name="truncated"/> when the data ended inside the body.
        /// </summary>
        internal static bool TryRead(JpxTileLayout layout, JpxPacketAddress address, JpxBitReader header, bool headerInStream,
            byte[] data, ref long bodyPos, long bodyEnd, bool ephRequired, WorkBudget budget, JpxTileDecode result,
            out bool truncated, out string problem)
        {
            truncated = false;
            problem = null;
            budget.Tick();

            JpxPrecinct precinct = layout.Components[address.Component].Resolutions[address.Resolution].Precincts[address.Precinct];
            JpxCodeBlockStyle style = layout.Components[address.Component].Coding.CodeBlockStyle;
            var pending = new List<Pending>();

            if (!header.TryReadBit(out int nonEmpty)) return Fail("the header ends before its first bit", out problem);
            if (nonEmpty == 1)
            {
                foreach (JpxPrecinctBand pb in precinct.Bands)
                {
                    for (int i = 0; i < pb.Blocks.Length; i++)
                    {
                        JpxCodeBlock block = pb.Blocks[i];
                        int bx = i % pb.BlocksWide, by = i / pb.BlocksWide;
                        bool exhausted;
                        bool included;
                        if (block.Included)
                        {
                            if (!header.TryReadBit(out int bit)) return Fail("the header ends inside the inclusion bits", out problem);
                            included = bit == 1;
                        }
                        else
                        {
                            included = pb.Inclusion.Decode(header, bx, by, address.Layer + 1, out exhausted);
                            if (exhausted) return Fail("the header ends inside the inclusion tag tree", out problem);
                        }
                        if (!included) continue;
                        budget.Tick();

                        if (!block.Included)
                        {
                            block.Included = true;
                            int threshold = 1;
                            while (!pb.ZeroBitPlanes.Decode(header, bx, by, threshold, out exhausted))
                            {
                                if (exhausted) return Fail("the header ends inside the zero bit-plane tag tree", out problem);
                                if (++threshold > MaxZeroBitPlanes)
                                    return Fail($"a code-block claims more than {MaxZeroBitPlanes - 1} zero bit-planes", out problem);
                            }
                            block.ZeroBitPlanes = threshold - 1;
                        }

                        if (!TryReadPassCount(header, out int passes)) return Fail("the header ends inside a coding-pass count", out problem);
                        while (true)
                        {
                            if (!header.TryReadBit(out int more)) return Fail("the header ends inside an Lblock signal", out problem);
                            if (more == 0) break;
                            block.Lblock++;
                        }

                        if ((style & JpxCodeBlockStyle.HighThroughput) != 0 && block.Coder == JpxBlockCoder.Undecided)
                        {
                            if (!TryReadUndecided(header, block, passes, style, pending, out problem)) return false;
                            continue;
                        }

                        // One length per terminated pass in the packet, and one for the last pass
                        // included whether or not it terminates (§B.10.7.2).
                        JpxCodeBlockStyle blockStyle = block.Coder == JpxBlockCoder.Part1 ? style & Part1Bits : style;
                        int first = block.Passes, end = block.Passes + passes, segmentStart = first;
                        for (int p = first; p < end; p++)
                        {
                            bool terminated = IsTerminated(blockStyle, p, block.HtPlaceholderPasses);
                            if (!terminated && p != end - 1) continue;
                            int count = p - segmentStart + 1;
                            int bits = block.Lblock + FloorLog2(count);
                            if (bits > 31) return Fail($"a code-block length field of {bits} bits", out problem);
                            if (!header.TryReadBits(bits, out int length)) return Fail("the header ends inside a codeword-segment length", out problem);
                            pending.Add(new Pending { Block = block, Passes = count, Length = length, Terminated = terminated });
                            segmentStart = p + 1;
                        }
                    }
                }
            }

            long afterHeader = header.Align();
            if (header.TrySkipMarker(Eph))
            {
                if (!ephRequired) result.Report(JpxTileDecode.UnexpectedEph, $"an EPH marker follows the header of the packet at {address} although the COD does not require one");
                afterHeader += 2;
            }
            else if (ephRequired)
            {
                return Fail("no EPH marker follows the header, although the COD requires one (T.800 §A.8.2)", out problem);
            }
            if (headerInStream) bodyPos = afterHeader;

            foreach (Pending p in pending)
            {
                long start = bodyPos;
                long stop = start + p.Length;
                if (stop > bodyEnd)
                {
                    stop = bodyEnd;
                    truncated = true;
                }
                p.Block.Add(new JpxCodeChunk(new JpxRange(start, stop), p.Passes, p.Terminated, truncated, address.Layer));
                bodyPos = stop;
                if (truncated) break;
            }
            return true;
        }

        /// <summary>The style bits a Part 1 code-block of a MIXED tile-component is coded with (ITU-T T.814 Table A.4).</summary>
        private const JpxCodeBlockStyle Part1Bits = ~(JpxCodeBlockStyle.HighThroughput | JpxCodeBlockStyle.Mixed);

        /// <summary>
        /// A contribution from an HT tile-component's code-block that has had no bytes yet (ITU-T T.814
        /// §B.3). Before the block's first HT cleanup pass, 3·P0 placeholder passes carry no bytes and
        /// stand in no set T, so a contribution of placeholders alone is one segment of all its passes —
        /// a length of Lblock + ⌊log2 passes⌋ bits, 0 (NOTE 4: a first contribution may be only that).
        /// The contribution that holds the first cleanup pass holds no second (§B.3), so if this one does,
        /// its last pass is the cleanup's or one of the two refinement passes after it, and the passes
        /// up to the cleanup make one segment of Lblock + ⌊log2 n⌋ bits: those bits are read first, and
        /// a non-zero length is the cleanup's — the rest of the contribution then the refinement segment.
        /// A zero length was the start of a placeholder segment's longer field, read to its end.
        /// <para>
        /// In a MIXED tile-component (§A.4) the block may be a Part 1 one instead, whose contribution is
        /// one codeword segment (neither bypass nor termination on each pass), its length in
        /// Lblock + ⌊log2 passes⌋ bits. §A.4 tells them apart: an HT block's first non-zero length
        /// begins with a 0 bit and comes with Lblock above 3; anything else is a Part 1 block's, whose
        /// field the bits already read begin, read to its end. A Part 1 block's earlier contributions
        /// without bytes stay its passes, as they would have been for a Part 1 tile-component.
        /// </para>
        /// </summary>
        private static bool TryReadUndecided(JpxBitReader header, JpxCodeBlock block, int passes, JpxCodeBlockStyle style,
            List<Pending> pending, out string problem)
        {
            problem = null;
            bool mixed = (style & JpxCodeBlockStyle.Mixed) != 0;
            int total = block.Passes + passes;
            int refinement = (total - 1) % 3;
            int toCleanup = passes - refinement;
            int fieldBits = block.Lblock + FloorLog2(passes);
            if (fieldBits > 31) return Fail($"a code-block length field of {fieldBits} bits", out problem);

            int read = 0, length = 0;
            if (toCleanup >= 1)
            {
                read = block.Lblock + FloorLog2(toCleanup);
                if (!header.TryReadBits(read, out length)) return Fail("the header ends inside a codeword-segment length", out problem);
                bool htCleanup = length != 0 && (!mixed || (block.Lblock > 3 && length > 1 && (length >> (read - 1)) == 0));
                if (htCleanup)
                {
                    if (length < 2) return Fail($"an HT code-block's first cleanup segment is {length} byte; ITU-T T.814 §B.3 needs more than 1", out problem);
                    block.Coder = JpxBlockCoder.HighThroughput;
                    block.HtPlaceholderPasses = total - 1 - refinement;
                    pending.Add(new Pending { Block = block, Passes = toCleanup, Length = length, Terminated = true });
                    if (refinement > 0)
                    {
                        int bits = block.Lblock + FloorLog2(refinement);
                        if (!header.TryReadBits(bits, out int refinementLength)) return Fail("the header ends inside a codeword-segment length", out problem);
                        pending.Add(new Pending { Block = block, Passes = refinement, Length = refinementLength, Terminated = refinement == 2 });
                    }
                    return true;
                }
            }
            for (; read < fieldBits; read++)
            {
                if (!header.TryReadBit(out int bit)) return Fail("the header ends inside a codeword-segment length", out problem);
                length = (length << 1) | bit;
            }
            if (length != 0)
            {
                if (!mixed)
                    return Fail($"an HT code-block's passes before its first cleanup pass carry {length} bytes; placeholder passes carry none (ITU-T T.814 §B.1)", out problem);
                block.Coder = JpxBlockCoder.Part1;
            }
            pending.Add(new Pending { Block = block, Passes = passes, Length = length, Terminated = false });
            return true;
        }

        /// <summary>Table B.4.</summary>
        internal static bool TryReadPassCount(JpxBitReader header, out int passes)
        {
            passes = 0;
            if (!header.TryReadBit(out int bit)) return false;
            if (bit == 0) { passes = 1; return true; }
            if (!header.TryReadBit(out bit)) return false;
            if (bit == 0) { passes = 2; return true; }
            if (!header.TryReadBits(2, out int v)) return false;
            if (v < 3) { passes = 3 + v; return true; }
            if (!header.TryReadBits(5, out v)) return false;
            if (v < 31) { passes = 6 + v; return true; }
            if (!header.TryReadBits(7, out v)) return false;
            passes = 37 + v;
            return true;
        }

        /// <summary>
        /// Whether coding pass <paramref name="pass"/> (0 is the first cleanup pass) ends a codeword
        /// segment: every pass with termination on each pass (Table D.8); with selective bypass, the
        /// fourth cleanup pass and then, from the fifth bit-plane, each raw pair's magnitude refinement
        /// pass and each cleanup pass (Table D.9). For an HT code-block (ITU-T T.814 §B.2) the set T:
        /// after the <paramref name="htPlaceholderPasses"/>, each HT set's cleanup pass, and its
        /// refinement segment's last pass — every pass but a SigProp pass; the Part 1 flags do not apply.
        /// </summary>
        internal static bool IsTerminated(JpxCodeBlockStyle style, int pass, int htPlaceholderPasses = 0)
        {
            if ((style & JpxCodeBlockStyle.HighThroughput) != 0)
            {
                int inSets = pass - htPlaceholderPasses;
                return inSets >= 0 && inSets % 3 != 1;
            }
            if ((style & JpxCodeBlockStyle.TerminateEachPass) != 0) return true;
            if ((style & JpxCodeBlockStyle.SelectiveBypass) != 0)
            {
                if (pass == 9) return true;
                if (pass >= 10) return (pass - 10) % 3 != 0;
            }
            return false;
        }

        /// <summary>⌊log2 n⌋ for n ≥ 1, the pass-count term of equation B-19 — for the header's reader and its writer alike.</summary>
        internal static int FloorLog2(int n)
        {
            int log = 0;
            while (n > 1) { n >>= 1; log++; }
            return log;
        }

        private static bool Fail(string why, out string problem)
        {
            problem = why;
            return false;
        }
    }
}
