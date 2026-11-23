// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// Lossless transcoding between JPEG 2000's two block coders (ITU-T T.814 §8.8, Annex F.1): each
    /// code-block re-coded by the other coder so that it decodes to the same quantised values, the
    /// codestream's every other choice kept — tiles, components, precincts, quality layers, progression.
    /// </summary>
    /// <remarks>
    /// <para><b>A block keeps its pass count.</b> A Part 1 block of N passes after P zero bit-planes ends
    /// in its cleanup pass at plane top − ⌊(N − 1)/3⌋ and (N − 1) mod 3 refinement passes; its HT form is
    /// 3·⌊(N − 1)/3⌋ placeholder passes and one HT set of that cleanup and those refinement passes (§B.3:
    /// S<sub>blk</sub> = P + P0), so each layer carries as many passes as before — the quality layer
    /// boundaries kept, as T.803 §C.1.11 asks. The way back codes the set's planes by Part 1 from the
    /// block's top plane, the placeholders becoming the passes above the cleanup again.</para>
    /// <para><b>The values come from the decoders.</b> A block is decoded with no ROI descaling, so each
    /// value is (2μ + 1)·2<sup>k</sup> for the plane k the sample is known to: k is the value's trailing
    /// zeros, and the bits from k up are what the other coder codes.</para>
    /// <para><b>The headers.</b> Rsiz bit 14, CAP's Pcap15 and Ccap15, and a CPF carrying the Part 1
    /// profile (§A.6) are set on the way to HT and undone on the way back; COD and COC style bytes follow
    /// the coder. TLM, PLM, PLT, PPM and PPT are dropped — every length changes, and packet headers go
    /// into the tile data — and each tile is written as one tile-part, its tile-part headers' segments
    /// gathered into it.</para>
    /// </remarks>
    internal static class JpxTranscoder
    {
        private const int Soc = 0xFF4F, Siz = 0xFF51, Cap = 0xFF50, Cod = 0xFF52, Coc = 0xFF53, Qcd = 0xFF5C, Qcc = 0xFF5D, Rgn = 0xFF5E, Poc = 0xFF5F, Tlm = 0xFF55,
            Plm = 0xFF57, Plt = 0xFF58, Cpf = 0xFF59, Ppm = 0xFF60, Ppt = 0xFF61, Sot = 0xFF90, Sod = 0xFF93, Eoc = 0xFFD9;

        private const int RsizHighThroughput = 0x4000;
        private const long Pcap15 = 1L << (32 - 15);

        /// <summary>Which blocks a selective transcode leaves as Part 1: tile, component, resolution, band orientation, block index in its precinct-band.</summary>
        internal delegate bool KeepPart1(int tile, int component, int resolution, int orientation, int block);

        /// <summary>
        /// Transcodes <paramref name="codestream"/>. <paramref name="toHighThroughput"/> re-codes every
        /// Part 1 block as an HT one; otherwise every HT block is re-coded by Part 1. <paramref name="keep"/>,
        /// on the way to HT, names Part 1 blocks to leave as they are: the result is then a MIXED codestream
        /// (§A.4), T.803 §C.1.12's selective transcode. <paramref name="keepQualityLayers"/>, on the way to HT,
        /// keeps a block's placeholder passes where its passes before its last cleanup came in earlier layers
        /// than that cleanup — the boundaries T.803 §C.1.11 asks to keep, which common decoders cannot read (they
        /// refuses an HT block of more than three passes); without it, or where nothing is lost, they are folded
        /// into the block's zero bit-planes (S<sub>blk</sub> = P + P0 decodes the same). False, with
        /// <paramref name="reason"/>, for a codestream that does not decode whole or a block the other coder
        /// cannot carry.
        /// </summary>
        internal static bool TryTranscode(byte[] codestream, bool toHighThroughput, KeepPart1 keep, out byte[] result, out string reason,
            bool keepQualityLayers = true, WorkBudget documentWork = null)
        {
            result = null;
            if (!JpxCodestreamParser.TryParse(codestream, out JpxCodestream cs, out reason)) return false;
            if (!JpxDecodedImage.TryValidateArea(cs.Size, cs.Size.Components.Length, out reason)) return false;
            bool mixed = toHighThroughput && keep != null;
            if (!toHighThroughput && cs.HtCapabilities < 0)
            {
                reason = "the codestream has no HT code-blocks to transcode (no CAP with Pcap15)";
                return false;
            }
            if (toHighThroughput && cs.HtCapabilities >= 0)
            {
                reason = "the codestream is already HTJ2K (CAP's Pcap15 is set)";
                return false;
            }

            // The decoder's allowance for the file's bytes (its floor and per-byte share, no reconstruction —
            // a transcode reconstructs no samples), and a document's when one transcodes many images.
            var budget = new WorkBudget(JpxDecoder.WorkAllowed(codestream.Length, 0),
                "JPEG 2000 transcoding (PdfLimits.MaxJpxDecodeWorkPerByte)", false, documentWork);
            var tiles = new List<int>(cs.Tiles.Keys);
            tiles.Sort();
            var tileData = new Dictionary<int, List<byte>>();
            int planes = 0;
            bool irreversible = false;
            try
            {
                foreach (int t in tiles)
                {
                    if (!TranscodeTile(cs, t, toHighThroughput, keep, keepQualityLayers, budget, out List<byte> data, out int tilePlanes, out bool tileIrreversible, out reason))
                        return false;
                    tileData[t] = data;
                    planes = Math.Max(planes, tilePlanes);
                    irreversible |= tileIrreversible;
                }
            }
            catch (InvalidOperationException e)
            {
                // The HT cleanup encoder's limits (a suffix past 4079 bytes, a residual past §7.3.6's).
                reason = "a code-block cannot be coded as HT: " + e.Message;
                return false;
            }
            catch (WorkBudgetExceededException ex)
            {
                // A Try method answers false, as the decoder does: the work a codestream declares is the file's.
                reason = documentWork != null && ex.Budget == documentWork
                    ? $"the document's JPEG 2000 transcoding exceeded its {documentWork.Total} units (PdfLimits.MaxJpxDecodeWork)"
                    : $"the transcode exceeded PdfLimits.MaxJpxDecodeWorkPerByte over its {codestream.Length} bytes ({budget.Total} units); adversarial or amplifying input";
                return false;
            }

            result = Write(cs, codestream, tiles, tileData, toHighThroughput, mixed, planes, irreversible);
            return true;
        }

        // ---------------------------------------------------------------- the blocks

        /// <summary>One tile's packets, its blocks re-coded; <paramref name="planes"/> the most magnitude bit-planes any HT block's band holds.</summary>
        private static bool TranscodeTile(JpxCodestream cs, int t, bool toHt, KeepPart1 keep, bool keepQualityLayers, WorkBudget budget,
            out List<byte> data, out int planes, out bool irreversible, out string reason)
        {
            data = null;
            planes = 0;
            irreversible = false;
            if (!JpxTier2Decoder.TryDecodeTile(cs, t, budget, out JpxTileDecode tile, out reason)) return false;
            if (!tile.Complete)
            {
                reason = $"tile {t} does not decode whole ({tile.StopReason}); a transcode would lose what is missing";
                return false;
            }

            JpxTileLayout layout = tile.Layout;
            var blocks = new Dictionary<JpxCodeBlock, JpxEncodedBlock>();
            var workspace = new JpxBlockDecoder();
            for (int c = 0; c < layout.Components.Length; c++)
            {
                JpxTileComponent tc = layout.Components[c];
                JpxCodeBlockStyle style = tc.Coding.CodeBlockStyle;
                JpxQuantization quantization = cs.QuantizationFor(t, c);
                int roi = cs.RoiShiftFor(t, c);
                bool vc = (style & JpxCodeBlockStyle.VerticallyCausal) != 0;
                if (toHt && !tc.Coding.Reversible) irreversible = true;
                if (keep != null && (style & (JpxCodeBlockStyle.SelectiveBypass | JpxCodeBlockStyle.TerminateEachPass)) != 0)
                {
                    reason = $"tile {t}, component {c} codes its blocks with selective bypass or termination on each pass, which a MIXED codestream's Part 1 blocks may not use (ITU-T T.814 §A.4)";
                    return false;
                }
                // The style each block's passes will stand under in the output, which says where its codeword segments end.
                var htStyle = JpxCodeBlockStyle.HighThroughput | (vc ? JpxCodeBlockStyle.VerticallyCausal : 0) | (keep != null ? JpxCodeBlockStyle.Mixed : 0);
                JpxCodeBlockStyle part1Style = (style & JpxCodeBlockStyle.HighThroughput) != 0 ? JpxCodeBlockStyle.None : style;

                for (int r = 0; r < tc.Resolutions.Length; r++)
                {
                    JpxResolution resolution = tc.Resolutions[r];
                    foreach (JpxBand band in resolution.Bands)
                    {
                        int mb = JpxQuantizationSteps.MagnitudeBitPlanes(quantization, tc.Levels, r, band.Orientation) + roi;
                        foreach (JpxPrecinct precinct in resolution.Precincts)
                        {
                            JpxPrecinctBand pb = precinct.Bands[band.Orientation == 0 ? 0 : band.Orientation - 1];
                            for (int i = 0; i < pb.Blocks.Length; i++)
                            {
                                JpxCodeBlock block = pb.Blocks[i];
                                // A block with passes is decoded and coded again: at most three passes a
                                // plane of its band each way, a unit a pass, as tier 1 charges.
                                budget.Tick(block.Passes == 0 ? 1 : 1 + 6L * Math.Max(1, mb));
                                bool blockHt = block.IsHighThroughput(style);
                                bool wantHt = toHt && !(keep != null && !blockHt && keep(t, c, r, band.Orientation, i));
                                if (wantHt) planes = Math.Max(planes, mb);
                                // A block stays as written only where its style does: a Part 1 block a selective
                                // transcode keeps, or on the way back a block of a tile-component that never had
                                // HT. In one that had, the style byte becomes 0, so even a MIXED source's Part 1
                                // blocks are re-coded — their flags would no longer stand.
                                bool copy = toHt ? !wantHt : (style & JpxCodeBlockStyle.HighThroughput) == 0;
                                JpxEncodedBlock encoded;
                                if (block.Passes == 0)
                                    encoded = Absent(layout.Layers);
                                else if (copy)
                                    encoded = Copied(block, tile, layout.Layers, style);
                                else if (!TryRecode(block, tile, workspace, style, blockHt, band.Orientation, mb, vc, wantHt, keepQualityLayers, layout.Layers, out encoded, out reason))
                                {
                                    reason = $"tile {t}, component {c}, resolution {r}, band {band.Orientation}, code-block {block.Bounds}: {reason}";
                                    return false;
                                }
                                else
                                    encoded.Style = wantHt ? htStyle : part1Style;
                                blocks.Add(block, encoded);
                            }
                        }
                    }
                }
            }

            data = new List<byte>();
            var trees = new Dictionary<JpxPrecinctBand, JpxPrecinctBandTrees>();
            int packetIndex = 0;
            foreach (JpxPacketAddress address in JpxPacketSequence.Enumerate(layout, cs.ProgressionChangesFor(t), budget))
            {
                budget.Tick();
                if (layout.Coding.SopAllowed) JpxRateControl.AddSop(data, packetIndex);
                JpxPacketEncoder.Write(layout, address, blocks, trees, layout.Coding.EphUsed, data);
                packetIndex++;
            }
            return true;
        }

        private static JpxEncodedBlock Absent(int layers) =>
            new JpxEncodedBlock(new JpxBlockCode(Array.Empty<byte>(), 0, 0, Array.Empty<int>(), Array.Empty<double>()), 0, layers);

        /// <summary>A block kept as its coder wrote it: its chunks' bytes in order, each layer's passes as they came, under <paramref name="style"/>.</summary>
        private static JpxEncodedBlock Copied(JpxCodeBlock block, JpxTileDecode tile, int layers, JpxCodeBlockStyle style)
        {
            var bytes = new List<byte>();
            var lengths = new int[block.Passes];
            int pass = 0;
            var layerEnd = new int[layers];
            foreach (JpxCodeChunk chunk in block.Chunks)
            {
                for (long b = chunk.Data.Start; b < chunk.Data.End; b++) bytes.Add(tile.Data[b]);
                for (int k = 0; k < chunk.Passes; k++) lengths[pass++] = bytes.Count;
                layerEnd[chunk.Layer] = pass;
            }
            var encoded = new JpxEncodedBlock(new JpxBlockCode(bytes.ToArray(), 0, block.Passes, lengths, new double[block.Passes]), block.ZeroBitPlanes, layers)
            {
                Style = style,
                HtPlaceholderPasses = block.HtPlaceholderPasses,
            };
            Fill(layerEnd, encoded.LayerEnd);
            return encoded;
        }

        /// <summary>Cumulative passes per layer, a layer with none carrying the count before it.</summary>
        private static void Fill(int[] layerEnd, int[] into)
        {
            int running = 0;
            for (int l = 0; l < layerEnd.Length; l++)
            {
                running = Math.Max(running, layerEnd[l]);
                into[l] = running;
            }
        }

        /// <summary>
        /// Decodes a block by its own coder (<paramref name="sourceHt"/>) to its values, unscaled by any ROI
        /// shift, and codes them by the coder <paramref name="toHt"/> names with the passes they carry: an HT
        /// set ending where the Part 1 passes ended, or Part 1's passes down to where the HT set ended.
        /// </summary>
        private static bool TryRecode(JpxCodeBlock block, JpxTileDecode tile, JpxBlockDecoder workspace, JpxCodeBlockStyle style,
            bool sourceHt, int orientation, int mb, bool vc, bool toHt, bool keepQualityLayers, int layers, out JpxEncodedBlock encoded, out string reason)
        {
            encoded = null;
            reason = null;
            int w = (int)block.Bounds.Width, h = (int)block.Bounds.Height;
            var values = new long[w * h];
            string problem;
            int plane, setPasses;
            if (sourceHt)
            {
                workspace.Ht.Decode(block, tile.Data, style, mb, 0, values, 0, w, out problem);
                plane = workspace.Ht.DecodedPlane;
                setPasses = workspace.Ht.DecodedPasses;
            }
            else
            {
                workspace.Decode(block, tile.Data, orientation, style, mb, 0, values, 0, w, out problem);
                int top = mb - 1 - block.ZeroBitPlanes;
                plane = top - (block.Passes - 1) / 3;
                setPasses = (block.Passes - 1) % 3 + 1;
                if (plane < 0 || (plane == 0 && setPasses > 1))
                {
                    reason = $"{block.Passes} passes after {block.ZeroBitPlanes} zero bit-planes run past the band's {mb} planes";
                    return false;
                }
            }
            if (problem != null)
            {
                reason = problem;
                return false;
            }

            long[] known = KnownBits(values);
            var layerEnd = new int[layers];
            int pass = 0;
            foreach (JpxCodeChunk chunk in block.Chunks)
            {
                pass += chunk.Passes;
                layerEnd[chunk.Layer] = pass;
            }
            var sourceEnd = new int[layers];
            Fill(layerEnd, sourceEnd);

            if (toHt)
            {
                JpxHtSet set = JpxHtRefinementEncoder.EncodeSet(known, w, h, plane, setPasses, vc);
                if (set == null)
                {
                    // Passes that left every sample insignificant: an HT block cannot open with an empty cleanup (§B.3).
                    encoded = Absent(layers);
                    return true;
                }
                // 3·P0 placeholder passes stand for the Part 1 passes above the last cleanup. Where the layer
                // that brings the block's first pass brings its cleanup too, they mark no boundary, and folded
                // into the zero bit-planes (S_blk = P + P0) they decode the same — and stay readable by a
                // decoder that takes no placeholders (common decoders take at most three passes).
                int placeholders = block.Passes - setPasses;
                int firstLayer = Array.FindIndex(sourceEnd, e => e > 0);
                bool fold = !keepQualityLayers || sourceEnd[firstLayer] > placeholders;
                int kept = fold ? 0 : placeholders;
                int passes = kept + setPasses;
                var lengths = new int[passes];
                for (int p = 0; p < passes; p++)
                {
                    int inSet = p - kept;
                    lengths[p] = inSet < 0 ? 0 : inSet == 0 ? set.Cleanup.Length : inSet == 1 ? set.Cleanup.Length + set.SigPropLength : set.Length;
                }
                encoded = new JpxEncodedBlock(new JpxBlockCode(set.Data(), plane + 1, passes, lengths, new double[passes]),
                    block.ZeroBitPlanes + (fold ? placeholders / 3 : 0), layers)
                {
                    HtPlaceholderPasses = kept,
                };
                for (int l = 0; l < layers; l++) encoded.LayerEnd[l] = Math.Max(0, sourceEnd[l] - (placeholders - kept));
                return true;
            }

            JpxBlockCode code = setPasses == 0 ? null : JpxBlockEncoder.Encode(known, w, h, orientation);
            if (code == null || code.Planes == 0)
            {
                // Nothing decoded, or nothing significant: no pass of Part 1's would code anything.
                encoded = Absent(layers);
                return true;
            }
            int part1Passes = 3 * (code.Planes - 1 - plane) + setPasses;
            if (part1Passes > code.Passes || part1Passes < 1)
            {
                reason = $"the HT set at plane {plane} with {setPasses} passes does not fit the {code.Planes} planes its values hold";
                return false;
            }
            int end = code.Lengths[part1Passes - 1];
            var bytes = new byte[end];
            Array.Copy(code.Data, bytes, end);
            var partial = new int[part1Passes];
            Array.Copy(code.Lengths, partial, part1Passes);
            encoded = new JpxEncodedBlock(new JpxBlockCode(bytes, code.Planes, part1Passes, partial, new double[part1Passes]), mb - code.Planes, layers);
            // Part 1 codes every plane down from the values' top. Where the HT block had placeholders for those
            // passes they come off its first layers' counts; where it had folded them into its zero bit-planes
            // (fewer HT passes than Part 1 ones), the layer that brought its first pass brings them too.
            int excess = block.Passes - part1Passes;
            for (int l = 0; l < layers; l++)
                encoded.LayerEnd[l] = sourceEnd[l] == 0 ? 0 : Math.Min(part1Passes, Math.Max(0, sourceEnd[l] - excess));
            return true;
        }

        /// <summary>Each value's known bits: (2μ + 1)·2<sup>k</sup> → μ·2<sup>k</sup>, signed; 0 stays 0.</summary>
        internal static long[] KnownBits(long[] values)
        {
            var known = new long[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                long v = values[i];
                if (v == 0) continue;
                long m = Math.Abs(v);
                int k = 0;
                while (((m >> k) & 1) == 0) k++;
                long bits = ((m >> 1) >> k) << k;
                known[i] = v < 0 ? -bits : bits;
            }
            return known;
        }

        // ---------------------------------------------------------------- the headers

        private readonly struct Segment
        {
            internal Segment(int marker, int start, int end)
            {
                Marker = marker;
                Start = start;
                End = end;
            }

            internal int Marker { get; }
            internal int Start { get; }
            internal int End { get; }
        }

        /// <summary>The new code-block style byte for a COD or COC that carried <paramref name="style"/>.</summary>
        internal static int StyleByte(int style, bool toHt, bool mixed)
        {
            if (toHt)
            {
                if ((style & 0x40) != 0) return style;
                // Vertical causality is the one Part 1 flag an HT block uses (Table A.3); MIXED keeps the
                // flags Table A.4 lets its Part 1 blocks use.
                return mixed ? 0xC0 | (style & 0x3A) : 0x40 | (style & 0x08);
            }
            // Part 1's encoder here sets no style flag.
            return (style & 0x40) != 0 ? 0 : style;
        }

        private static byte[] Write(JpxCodestream cs, byte[] source, List<int> tiles, Dictionary<int, List<byte>> tileData,
            bool toHt, bool mixed, int planes, bool irreversible)
        {
            var main = new List<Segment>();
            int pos = 2;
            while (U16(source, pos) != Sot)
            {
                int end = SegmentEnd(source, pos);
                main.Add(new Segment(U16(source, pos), pos, end));
                pos = end;
            }
            var tileHeaders = new Dictionary<int, List<Segment>>();
            while (pos + 12 <= source.Length && U16(source, pos) == Sot)
            {
                int isot = U16(source, pos + 4);
                long psot = U32(source, pos + 6);
                if (!tileHeaders.TryGetValue(isot, out List<Segment> segments)) tileHeaders[isot] = segments = new List<Segment>();
                int p = pos + 12;
                while (U16(source, p) != Sod)
                {
                    int end = SegmentEnd(source, p);
                    segments.Add(new Segment(U16(source, p), p, end));
                    p = end;
                }
                if (psot == 0) break;   // the last tile-part, to EOC
                pos += (int)psot;
            }

            bool rgn = false, heterogeneous = false;
            foreach (Segment s in main) rgn |= s.Marker == Rgn;
            foreach (List<Segment> segments in tileHeaders.Values)
                foreach (Segment s in segments)
                {
                    rgn |= s.Marker == Rgn;
                    // §8.5: a functional segment in a tile-part header (PPT is dropped here).
                    heterogeneous |= s.Marker == Cod || s.Marker == Coc || s.Marker == Rgn || s.Marker == Qcd || s.Marker == Qcc || s.Marker == Poc;
                }

            int components = cs.Size.Components.Length;
            var output = new List<byte>();
            Add16(output, Soc);
            int profile = 0;
            foreach (Segment s in main)
                if (s.Marker == Cpf) profile = CpfNumber(source, s);

            foreach (Segment s in main)
            {
                switch (s.Marker)
                {
                    case Siz:
                    {
                        byte[] siz = Copy(source, s);
                        int rsiz = (siz[4] << 8) | siz[5];
                        int newRsiz = toHt ? (rsiz & ~0x0FFF) | RsizHighThroughput : (rsiz & ~(RsizHighThroughput | 0x0FFF)) | profile;
                        siz[4] = (byte)(newRsiz >> 8);
                        siz[5] = (byte)newRsiz;
                        output.AddRange(siz);
                        byte[] cap = CapSegment(source, main, toHt, mixed, rgn, heterogeneous, irreversible, planes);
                        if (cap != null) output.AddRange(cap);
                        if (toHt) output.AddRange(CpfSegment(rsiz & 0x0FFF));
                        break;
                    }
                    case Cap:
                    case Cpf:
                    case Tlm:
                    case Plm:
                    case Ppm:
                        break;
                    case Cod:
                    case Coc:
                        output.AddRange(Restyled(source, s, components, toHt, mixed));
                        break;
                    default:
                        output.AddRange(Copy(source, s));
                        break;
                }
            }

            foreach (int t in tiles)
            {
                var header = new List<byte>();
                if (tileHeaders.TryGetValue(t, out List<Segment> segments))
                {
                    // One tile-part now carries what the tile's parts did, and a tile-part header holds one POC
                    // (§A.6.6): the parts' progression entries, in their order, become one segment's.
                    List<Segment> pocs = segments.FindAll(s => s.Marker == Poc);
                    foreach (Segment s in segments)
                    {
                        if (s.Marker == Plt || s.Marker == Ppt) continue;
                        if (s.Marker == Poc)
                        {
                            if (pocs[0].Start == s.Start) header.AddRange(MergedPoc(source, pocs));
                            continue;
                        }
                        header.AddRange(s.Marker == Cod || s.Marker == Coc ? Restyled(source, s, components, toHt, mixed) : Copy(source, s));
                    }
                }
                List<byte> data = tileData[t];
                Add16(output, Sot);
                Add16(output, 10);
                Add16(output, t);
                Add32(output, 12 + header.Count + 2 + data.Count);
                output.Add(0);
                output.Add(1);
                output.AddRange(header);
                Add16(output, Sod);
                output.AddRange(data);
            }
            Add16(output, Eoc);
            return output.ToArray();
        }

        /// <summary>Where the marker segment at <paramref name="at"/> ends: past its length, or past the marker alone for 0xFF30 to 0xFF3F, which T.800 §A.1 reserves for markers without a segment.</summary>
        private static int SegmentEnd(byte[] source, int at)
        {
            int marker = U16(source, at);
            return marker >= 0xFF30 && marker <= 0xFF3F ? at + 2 : at + 2 + U16(source, at + 2);
        }

        /// <summary>The POC segments' progression entries end to end in one POC (T.800 Table A.32: the entries alone follow Lpoc).</summary>
        private static byte[] MergedPoc(byte[] source, List<Segment> pocs)
        {
            var entries = new List<byte>();
            foreach (Segment s in pocs)
                for (int b = s.Start + 4; b < s.End; b++) entries.Add(source[b]);
            var segment = new List<byte>(entries.Count + 4);
            Add16(segment, Poc);
            Add16(segment, entries.Count + 2);
            segment.AddRange(entries);
            return segment.ToArray();
        }

        /// <summary>A COD or COC with its style byte set for the coder (T.800 Tables A.12, A.23).</summary>
        private static byte[] Restyled(byte[] source, Segment s, int components, bool toHt, bool mixed)
        {
            byte[] segment = Copy(source, s);
            int at = s.Marker == Cod ? 12 : 4 + (components < 257 ? 1 : 2) + 1 + 3;
            segment[at] = (byte)StyleByte(segment[at], toHt, mixed);
            return segment;
        }

        /// <summary>
        /// CAP after the change: Pcap15 and its Ccap15 set on the way to HT — HTONLY or MIXED, one HT set per
        /// block, RGN and heterogeneity as found, bit 5 for an irreversible transform, the magnitude bound —
        /// and removed on the way back, the other parts' capabilities kept. Null when no part is left.
        /// </summary>
        private static byte[] CapSegment(byte[] source, List<Segment> main, bool toHt, bool mixed, bool rgn, bool heterogeneous, bool irreversible, int planes)
        {
            long pcap = 0;
            var ccap = new SortedDictionary<int, int>();
            foreach (Segment s in main)
            {
                if (s.Marker != Cap) continue;
                pcap = U32(source, s.Start + 4);
                int at = s.Start + 8;
                for (int i = 1; i <= 32; i++)
                {
                    if ((pcap & (1L << (32 - i))) == 0) continue;
                    ccap[i] = U16(source, at);
                    at += 2;
                }
            }
            if (toHt)
            {
                pcap |= Pcap15;
                ccap[15] = (mixed ? 0xC000 : 0) | (rgn ? 0x1000 : 0) | (heterogeneous ? 0x0800 : 0) | (irreversible ? 0x20 : 0)
                    | JpxEncoder.MagnitudeBoundParameter(planes);
            }
            else
            {
                pcap &= ~Pcap15;
                ccap.Remove(15);
            }
            if (pcap == 0) return null;
            var payload = new List<byte>();
            Add32(payload, pcap);
            foreach (int value in ccap.Values) Add16(payload, value);
            var segment = new List<byte>();
            Add16(segment, Cap);
            Add16(segment, payload.Count + 2);
            segment.AddRange(payload);
            return segment.ToArray();
        }

        /// <summary>CPF (ITU-T T.814 §A.6): CPFnum, the Part 1 codestream's profile, as Pcpf values of 16 bits — CPFnum + 1 in base 2<sup>16</sup>.</summary>
        private static byte[] CpfSegment(int profile)
        {
            var segment = new List<byte>();
            Add16(segment, Cpf);
            Add16(segment, 4);
            Add16(segment, profile + 1);
            return segment.ToArray();
        }

        /// <summary>CPFnum = −1 + Σ Pcpf<sub>i</sub>·2<sup>16(i−1)</sup>.</summary>
        private static int CpfNumber(byte[] source, Segment s)
        {
            long n = -1;
            int count = (U16(source, s.Start + 2) - 2) / 2;
            for (int i = 0; i < count && i < 2; i++) n += (long)U16(source, s.Start + 4 + 2 * i) << (16 * i);
            return (int)Math.Max(0, Math.Min(0x0FFF, n));
        }

        private static byte[] Copy(byte[] source, Segment s)
        {
            var b = new byte[s.End - s.Start];
            Array.Copy(source, s.Start, b, 0, b.Length);
            return b;
        }

        private static int U16(byte[] b, int at) => IO.BigEndian.U16(b, at);
        private static long U32(byte[] b, int at) => IO.BigEndian.U32(b, at);

        private static void Add16(List<byte> to, int v) => IO.BigEndian.Add16(to, v);

        private static void Add32(List<byte> to, long v) => IO.BigEndian.Add32(to, unchecked((uint)v));
    }
}
