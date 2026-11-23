// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>What tier 2 produced for one tile: its layout with every code-block's chunks, and how the read ended.</summary>
    internal sealed class JpxTileDecode
    {
        internal JpxTileDecode(JpxTileLayout layout, byte[] data, long dataStart, long dataEnd)
        {
            Layout = layout;
            Data = data;
            DataStart = dataStart;
            DataEnd = dataEnd;
        }

        internal JpxTileLayout Layout { get; }

        /// <summary>The buffer every chunk's range points into: the codestream, or the tile-parts joined.</summary>
        internal byte[] Data { get; }

        internal long DataStart { get; }
        internal long DataEnd { get; }

        internal int PacketsRead { get; set; }

        /// <summary>Each packet's extent in the tile data: SOP, in-stream header and body.</summary>
        internal List<long> PacketLengths { get; } = new List<long>();

        /// <summary>Up to where the tile data was read.</summary>
        internal long BytesConsumed { get; set; }

        /// <summary>Null when every packet the progression names was read whole; otherwise why reading stopped.</summary>
        internal string StopReason { get; set; }

        internal bool Complete => StopReason == null;

        /// <summary>
        /// Tolerated irregularities: SOP or EPH where the COD says none, a wrong Nsop, bytes after the
        /// last packet. A kind that repeats per packet is written out <see cref="ReportedPerKind"/>
        /// times and then counted (<see cref="Report"/>), so a tile of 10<sup>5</sup> packets cannot
        /// build 10<sup>5</sup> strings.
        /// </summary>
        internal List<string> Deviations { get; } = new List<string>();

        /// <summary>How many deviations of one per-packet kind are written out by text before the rest are counted.</summary>
        internal const int ReportedPerKind = 8;

        internal const string WrongNsop = "an SOP marker segment carries the wrong Nsop";
        internal const string UnexpectedEph = "an EPH marker follows a header although the COD does not require one";

        private Dictionary<string, int> _counts;

        /// <summary>Adds a deviation of a per-packet <paramref name="kind"/>: the first <see cref="ReportedPerKind"/> by text, the rest counted for <see cref="FinishDeviations"/>.</summary>
        internal void Report(string kind, string text)
        {
            if (_counts == null) _counts = new Dictionary<string, int>();
            _counts.TryGetValue(kind, out int seen);
            _counts[kind] = seen + 1;
            if (seen < ReportedPerKind) Deviations.Add(text);
        }

        /// <summary>Appends one line per kind that overflowed <see cref="ReportedPerKind"/>, with the count.</summary>
        internal void FinishDeviations()
        {
            if (_counts == null) return;
            foreach (KeyValuePair<string, int> kind in _counts)
                if (kind.Value > ReportedPerKind)
                    Deviations.Add($"{kind.Value - ReportedPerKind} more packets where {kind.Key}");
        }
    }

    /// <summary>
    /// The PPM series joined, and each tile-part's entry in it (T.800 §A.7.4: one Nppm-counted entry
    /// per tile-part in codestream order, continued across segments) — built once per codestream by
    /// the first tile that reads it, however many tiles follow.
    /// </summary>
    internal sealed class JpxPackedMainHeaders
    {
        private JpxPackedMainHeaders(byte[] all)
        {
            All = all;
        }

        /// <summary>Every PPM payload after its Zppm, in Zppm order, end to end.</summary>
        internal byte[] All { get; }

        /// <summary>Each tile-part's packet headers as a range of <see cref="All"/>.</summary>
        internal Dictionary<JpxTilePart, JpxRange> Slices { get; } = new Dictionary<JpxTilePart, JpxRange>();

        internal static bool TryBuild(JpxCodestream cs, WorkBudget budget, out JpxPackedMainHeaders index, out string reason)
        {
            index = null;
            reason = null;
            var built = new JpxPackedMainHeaders(JpxTier2Decoder.Join(cs.Data, cs.PackedHeadersMain));
            byte[] all = built.All;

            var parts = new List<JpxTilePart>();
            foreach (JpxTile t in cs.Tiles.Values) parts.AddRange(t.Parts);
            parts.Sort((a, b) => a.Data.Start.CompareTo(b.Data.Start));
            budget.Tick(1 + parts.Count + all.Length / JpxDecoder.SamplesPerUnit);   // the copy, and one unit per entry

            long at = 0;
            foreach (JpxTilePart part in parts)
            {
                if (at + 4 > all.Length)
                {
                    reason = "the PPM packet headers end before every tile-part has its entry (T.800 §A.7.4)";
                    return false;
                }
                long n = JpxHeader.ReadU32(all, at);
                at += 4;
                if (at + n > all.Length)
                {
                    reason = $"a PPM entry declares {n} bytes of packet headers, past the end of the series (T.800 §A.7.4)";
                    return false;
                }
                built.Slices[part] = new JpxRange(at, at + n);
                at += n;
            }
            index = built;
            return true;
        }
    }

    /// <summary>
    /// Tier 2 (T.800 Annex B): reads a tile's packets, in the progression the codestream signals, into
    /// the code-blocks of its layout. Packet headers come from the tile data, or from the PPM or PPT
    /// segments when the codestream relocated them (§A.7.4, §A.7.5); SOP marker segments are skipped
    /// and their sequence numbers checked (§A.8.1). A truncated tile decodes as far as it goes
    /// (§B.11); a malformed packet stops the tile, and the reason is on the result.
    /// </summary>
    internal static class JpxTier2Decoder
    {
        private const int Sop = 0xFF91;

        internal static bool TryDecodeTile(JpxCodestream cs, int tileIndex, WorkBudget budget, out JpxTileDecode result, out string reason)
        {
            result = null;
            if (!JpxTileLayout.TryBuild(cs, tileIndex, out JpxTileLayout layout, out reason, budget)) return false;
            if (!cs.Tiles.TryGetValue(tileIndex, out JpxTile tile) || tile.Parts.Count == 0)
            {
                reason = $"tile {tileIndex} has no tile-part";
                return false;
            }

            // Packets may not span tile-parts (§B.11), but reading them from one buffer costs a copy
            // only when there are several parts, and forgives an encoder that split one anyway.
            byte[] data;
            long start, end;
            if (tile.Parts.Count == 1)
            {
                data = cs.Data;
                start = tile.Parts[0].Data.Start;
                end = tile.Parts[0].Data.End;
            }
            else
            {
                long total = 0;
                foreach (JpxTilePart part in tile.Parts) total += part.Data.Length;
                data = new byte[total];
                long at = 0;
                foreach (JpxTilePart part in tile.Parts)
                {
                    System.Array.Copy(cs.Data, part.Data.Start, data, at, part.Data.Length);
                    at += part.Data.Length;
                }
                start = 0;
                end = total;
            }

            if (!TryPackedHeaders(cs, tile, budget, out byte[] packed, out reason)) return false;
            result = new JpxTileDecode(layout, data, start, end);
            JpxBitReader packedReader = packed == null ? null : new JpxBitReader(packed, 0, packed.Length);
            bool ephRequired = layout.Coding.EphUsed;
            bool sopAllowed = layout.Coding.SopAllowed;

            long pos = start;
            int packetIndex = 0;
            bool sopWithoutFlag = false;
            foreach (JpxPacketAddress address in JpxPacketSequence.Enumerate(layout, cs.ProgressionChangesFor(tileIndex), budget))
            {
                long packetStart = pos;
                if (pos + 2 <= end && data[pos] == 0xFF && data[pos + 1] == (Sop & 0xFF) && pos + 6 > end)
                {
                    result.StopReason = $"the tile data ends inside the SOP marker segment before packet {packetIndex} ({address})";
                    break;
                }
                if (pos + 6 <= end && data[pos] == 0xFF && data[pos + 1] == (Sop & 0xFF))
                {
                    int nsop = (data[pos + 4] << 8) | data[pos + 5];
                    if (nsop != (packetIndex & 0xFFFF))
                        result.Report(JpxTileDecode.WrongNsop, $"the SOP before packet {packetIndex} ({address}) carries Nsop {nsop}");
                    if (!sopAllowed && !sopWithoutFlag)
                    {
                        sopWithoutFlag = true;
                        result.Deviations.Add("SOP marker segments appear although the COD does not allow them");
                    }
                    pos += 6;
                }

                if (packedReader == null && pos >= end)
                {
                    result.StopReason = $"the tile data ends before packet {packetIndex} ({address})";
                    break;
                }
                JpxBitReader header = packedReader ?? new JpxBitReader(data, pos, end);
                if (!JpxPacketDecoder.TryRead(layout, address, header, packedReader == null, data, ref pos, end, ephRequired,
                        budget, result, out bool truncated, out string problem))
                {
                    result.StopReason = $"packet {packetIndex} ({address}) at offset {packetStart}: {problem}";
                    break;
                }
                result.PacketsRead++;
                result.PacketLengths.Add(pos - packetStart);
                packetIndex++;
                if (truncated)
                {
                    result.StopReason = $"the tile data ends inside packet {packetIndex - 1} ({address})";
                    break;
                }
            }

            result.BytesConsumed = pos - start;
            if (result.StopReason == null && pos < end)
                result.Deviations.Add($"{end - pos} bytes follow the tile's last packet");
            CheckPacketLengthsAgainstPlt(tile, result);
            result.FinishDeviations();
            return true;
        }

        /// <summary>
        /// PLT is advisory (T.800 §A.7.3), an independent statement of every packet's length: the
        /// lengths read here are checked against it, tile-part by tile-part, and the first
        /// disagreement is reported — a packet's length is stated nowhere else, so a misread bit
        /// shifts every packet after it, and PLT is the one witness.
        /// </summary>
        private static void CheckPacketLengthsAgainstPlt(JpxTile tile, JpxTileDecode result)
        {
            int packet = 0;
            foreach (JpxTilePart part in tile.Parts)
            {
                foreach (long stated in part.PacketLengths)
                {
                    if (packet >= result.PacketLengths.Count) return;
                    long read = result.PacketLengths[packet];
                    if (stated != read)
                    {
                        result.Deviations.Add($"tile-part {part.Index}'s PLT says packet {packet} is {stated} bytes; {read} were read");
                        return;
                    }
                    packet++;
                }
            }
        }

        /// <summary>
        /// The tile's packet headers when PPM or PPT relocated them, joined in order; null when they
        /// are in the tile data. PPM's series is indexed once per codestream
        /// (<see cref="JpxPackedMainHeaders"/>) and this tile's entries sliced from it.
        /// </summary>
        private static bool TryPackedHeaders(JpxCodestream cs, JpxTile tile, WorkBudget budget, out byte[] packed, out string reason)
        {
            packed = null;
            reason = null;
            if (cs.PackedHeadersMain.Count > 0)
            {
                JpxPackedMainHeaders index = cs.PackedMainHeaders;
                if (index == null)
                {
                    if (!JpxPackedMainHeaders.TryBuild(cs, budget, out index, out reason)) return false;
                    cs.PackedMainHeaders = index;
                }
                var ranges = new List<JpxRange>();
                foreach (JpxTilePart part in tile.Parts)
                    if (index.Slices.TryGetValue(part, out JpxRange slice)) ranges.Add(slice);
                packed = Join(index.All, ranges);
                return true;
            }

            var ppt = new List<JpxRange>();
            foreach (JpxTilePart part in tile.Parts) ppt.AddRange(part.PackedHeaders);
            if (ppt.Count > 0) packed = Join(cs.Data, ppt);
            return true;
        }

        internal static byte[] Join(byte[] source, List<JpxRange> ranges)
        {
            long total = 0;
            foreach (JpxRange r in ranges) total += r.Length;
            var joined = new byte[total];
            long at = 0;
            foreach (JpxRange r in ranges)
            {
                System.Array.Copy(source, r.Start, joined, at, r.Length);
                at += r.Length;
            }
            return joined;
        }
    }
}
