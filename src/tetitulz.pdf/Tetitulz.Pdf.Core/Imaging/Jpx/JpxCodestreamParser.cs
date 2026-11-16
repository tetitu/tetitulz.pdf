// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// Parses a JPEG 2000 codestream's main and tile-part headers (T.800 Annex A) into a
    /// <see cref="JpxCodestream"/>, refusing by name anything this library does not decode.
    /// </summary>
    /// <remarks>
    /// <para><b>What it refuses, and why each refusal names its reason.</b> A structurally impossible
    /// value (a SIZ whose length disagrees with its component count, a code-block larger than 2^12
    /// samples, a precinct exponent of 0 above resolution level 0) and a codestream that needs an
    /// extension this library does not implement — a Part 2 capability outside the JPX baseline
    /// (T.801 Table A.2, §M.9.2.3), or any extended capability a CAP marker segment signals (T.800
    /// §A.5.2, which is how HTJ2K announces itself). The reason is the whole diagnostic: a decode that
    /// silently produced nothing would look like a missing image.</para>
    ///
    /// <para><b>What it tolerates.</b> An unknown marker segment is skipped by its length, as T.800
    /// §A.1 requires, and the markers 0xFF30 to 0xFF3F, which have no parameters, are skipped on their
    /// own. A tile-part whose length runs past the data is clamped to it and the codestream marked
    /// truncated: a truncated codestream still decodes, to a lower quality, and T.800 §A.4.4 NOTE 2
    /// expects a decoder to try.</para>
    ///
    /// <para><b>Nothing is copied.</b> Tile-part data and packed packet headers are
    /// <see cref="JpxRange"/>s into the source buffer. The work is linear in the data: every marker
    /// segment consumes its own bytes, and the tile count is checked
    /// (<see cref="JpxImageSize.TryValidateGrid"/>) before anything is allocated per tile.</para>
    /// </remarks>
    internal static class JpxCodestreamParser
    {
        // Marker codes: T.800 Table A.2, and T.801 Table A.1 for the Part 2 segments the JPX baseline
        // admits. Private to the codec, as JBIG2's segment types are to its decoder.
        private const int SOC = 0xFF4F, SOT = 0xFF90, SOD = 0xFF93, EOC = 0xFFD9;
        private const int SIZ = 0xFF51, CAP = 0xFF50, PRF = 0xFF56;
        private const int COD = 0xFF52, COC = 0xFF53, RGN = 0xFF5E, QCD = 0xFF5C, QCC = 0xFF5D, POC = 0xFF5F;
        private const int TLM = 0xFF55, PLM = 0xFF57, PLT = 0xFF58, PPM = 0xFF60, PPT = 0xFF61;
        private const int CRG = 0xFF63, COM = 0xFF64;
        /// <summary>The Part 2 marker codes kept in <see cref="JpxCodestream.Part2Segments"/>; <c>JpxPart2Header</c> reads them back by these.</summary>
        internal const int MCT = 0xFF74, MCC = 0xFF75, NLT = 0xFF76, MCO = 0xFF77, CBD = 0xFF78;

        /// <summary><c>Rsiz</c> bit 15: a T.801 (Part 2) capability is present (T.800 Table A.10 NOTE 1).</summary>
        internal const int RsizPart2 = 0x8000;

        /// <summary>The capability bits of a Part 2 <c>Rsiz</c> (T.801 Table A.2, bits 0 to 11).</summary>
        internal const int Part2CapabilityMask = 0x0FFF;

        /// <summary>T.801 Table A.2's multiple component transformation bit: admitted, in its restricted form, by the JPX baseline.</summary>
        private const int Part2MultipleComponentTransform = 1 << 8;

        /// <summary>
        /// The Part 2 capabilities T.801 Table A.2 marks "required to decode" that the JPX baseline does
        /// not admit (§M.9.2.3), by <c>Rsiz</c> bit. The "useful to decode" ones — trellis coded
        /// quantisation (bit 2), visual masking (bit 3), non-linear point transformation (bit 9) — may be
        /// ignored, by that table's own definition.
        /// </summary>
        private static readonly KeyValuePair<int, string>[] Part2Refused =
        {
            new KeyValuePair<int, string>(0, "variable DC offset"),
            new KeyValuePair<int, string>(1, "variable scalar quantisation"),
            new KeyValuePair<int, string>(4, "single sample overlap"),
            new KeyValuePair<int, string>(5, "arbitrary decomposition style"),
            new KeyValuePair<int, string>(6, "arbitrary transformation kernel"),
            new KeyValuePair<int, string>(7, "whole sample symmetric transformation kernel"),
            new KeyValuePair<int, string>(10, "arbitrary shaped region of interest"),
            new KeyValuePair<int, string>(11, "precinct-dependent quantisation"),
        };

        /// <summary>
        /// Parses JPEG 2000 bytes — a JP2 container, whose contiguous codestream box is located, or a
        /// raw codestream. False, with the reason, when there is no codestream or it is refused.
        /// </summary>
        internal static bool TryParse(byte[] data, out JpxCodestream codestream, out string reason)
        {
            codestream = null;
            // A fragmented codestream (T.801 M.11.3) arrives assembled into its own buffer, which
            // is then what the parsed codestream holds and every later read addresses.
            if (!JpxHeader.TryLocateCodestream(data, out byte[] buffer, out long start, out long end, out reason)) return false;
            return TryParse(buffer, start, end, out codestream, out reason);
        }

        /// <summary>Parses the codestream occupying <c>[start, end)</c> of <c>data</c>.</summary>
        internal static bool TryParse(byte[] data, long start, long end, out JpxCodestream codestream, out string reason)
        {
            codestream = null;
            reason = null;
            if (data == null || start < 0 || end > data.Length || start + 4 > end)
            {
                reason = "the codestream is too short to hold SOC and SIZ";
                return false;
            }
            if (U16(data, start) != SOC)
            {
                reason = "the codestream does not start with an SOC marker (T.800 §A.4.1)";
                return false;
            }
            if (U16(data, start + 2) != SIZ)
            {
                reason = "SOC is not followed by a SIZ marker segment (T.800 §A.5.1)";
                return false;
            }
            if (!JpxImageSize.TryRead(data, start + 4, end, out JpxImageSize size, out reason)) return false;
            if (!size.TryValidateGrid(out reason)) return false;
            if (!AcceptsCapabilities(size.Rsiz, out reason)) return false;

            var cs = new JpxCodestream(data, size);
            long pos = start + 4 + U16(data, start + 4);
            bool compact = size.Components.Length < 257;   // Ccoc, Cqcc, Crgn, CSpoc and CEpoc are 8 bits, else 16

            var ppm = new List<KeyValuePair<int, JpxRange>>();
            if (!TryParseHeader(cs, cs.Main, null, data, ref pos, end, compact, ppm, null, null, out reason)) return false;
            if (cs.Main.Coding == null)
            {
                reason = "the main header has no COD marker segment (T.800 §A.6.1)";
                return false;
            }
            if (cs.Main.Quantization == null)
            {
                reason = "the main header has no QCD marker segment (T.800 §A.6.4)";
                return false;
            }
            if (!SortByIndex(ppm, cs.PackedHeadersMain, "PPM", false, cs, out reason)) return false;

            bool anyPpt = false;
            while (pos + 2 <= end)
            {
                int marker = U16(data, pos);
                if (marker == EOC)
                {
                    cs.EndOfCodestreamSeen = true;
                    break;
                }
                if (marker != SOT)
                {
                    // Bytes that are neither a tile-part nor the end of the codestream (non-regression
                    // issue226.j2k has two before its EOC): skipped to the next SOT or EOC, and reported.
                    // A false SOT inside them fails the tile-part's own checks.
                    long junk = pos;
                    pos = NextTileBoundary(data, pos + 1, end);
                    cs.Deviations.Add(pos < end
                        ? $"{pos - junk} bytes at offset {junk} are not a tile-part; skipped to the {MarkerName(U16(data, pos))} at offset {pos}"
                        : $"{end - junk} bytes at offset {junk} are not a tile-part, and no SOT or EOC follows; the codestream ends there");
                    continue;
                }
                if (!TryParseTilePart(cs, data, ref pos, end, compact, out bool hasPpt, out reason)) return false;
                anyPpt |= hasPpt;
            }

            if (cs.Tiles.Count == 0)
            {
                reason = "the codestream has no tile-parts (T.800 §A.4)";
                return false;
            }
            if (anyPpt && cs.PackedHeadersMain.Count > 0)
            {
                reason = "packet headers are packed in both PPM and PPT marker segments (T.800 §A.7.4)";
                return false;
            }

            codestream = cs;
            return true;
        }

        /// <summary>Refuses a codestream that needs a capability this library does not decode.</summary>
        private static bool AcceptsCapabilities(int rsiz, out string reason)
        {
            reason = null;
            if ((rsiz & RsizPart2) == 0) return true;

            var refused = new List<string>();
            foreach (var bit in Part2Refused)
                if ((rsiz & (1 << bit.Key)) != 0) refused.Add(bit.Value);
            if (refused.Count == 0) return true;

            reason = "the codestream requires T.801 (Part 2) capabilities outside the JPX baseline: " +
                     string.Join(", ", refused);
            return false;
        }

        /// <summary>
        /// Parses marker segments from <c>pos</c> up to the next SOT (main header, <c>tilePart</c> null)
        /// or up to and past SOD (tile-part header), into <c>settings</c>.
        /// </summary>
        private static bool TryParseHeader(JpxCodestream cs, JpxHeaderSettings settings, JpxTilePart tilePart,
            byte[] data, ref long pos, long end, bool compact,
            List<KeyValuePair<int, JpxRange>> packed, List<KeyValuePair<int, JpxRange>> packetLengths,
            int? tilePartIndex, out string reason)
        {
            reason = null;
            bool main = tilePart == null && tilePartIndex == null;
            string where = main ? "the main header" : "a tile-part header";
            bool pocInThisHeader = false;

            while (true)
            {
                if (pos + 2 > end)
                {
                    reason = main ? "the main header ends before any tile-part" : "a tile-part header has no SOD marker";
                    return false;
                }
                int marker = U16(data, pos);
                // The main header ends at the first SOT — or at EOC, which the caller then reports as
                // a codestream without tile-parts rather than as a stray marker.
                if (main && (marker == SOT || marker == EOC)) return true;
                if (!main && marker == SOD)
                {
                    pos += 2;
                    return true;
                }
                if ((marker & 0xFF00) != 0xFF00 || marker == 0xFF00 || marker == 0xFFFF)
                {
                    reason = $"expected a marker in {where} at offset {pos}, found 0x{marker:X4}";
                    return false;
                }
                if (marker >= 0xFF30 && marker <= 0xFF3F)
                {
                    pos += 2;   // markers without parameters (T.800 §A.1.3)
                    continue;
                }
                if (marker == SOC || marker == SOD || marker == EOC || marker == SOT || marker == SIZ)
                {
                    reason = $"{MarkerName(marker)} cannot appear in {where} (offset {pos})";
                    return false;
                }
                if (pos + 4 > end)
                {
                    reason = $"{MarkerName(marker)} at offset {pos} is truncated";
                    return false;
                }

                int length = U16(data, pos + 2);
                long segStart = pos + 4;
                long segEnd = pos + 2 + length;
                if (length < 2 || segEnd > end)
                {
                    reason = $"{MarkerName(marker)} at offset {pos} declares {length} bytes, past the end of {(main ? "the codestream" : "its tile-part")}";
                    return false;
                }

                bool ok = true;
                // COD, COC, QCD, QCC and RGN belong to the main header and a tile's first tile-part
                // header (T.800 §A.6.1 to §A.6.5); POC is the one coding segment any tile-part header
                // may carry (§A.6.6, §B.12.3). A later tile-part restating one byte for byte — an
                // encoder that repeats the main header in every tile-part (non-regression
                // db11217111510058.jp2) — changes nothing and is tolerated; one that differs is refused.
                long key = SettingKey(marker, data, segStart, segEnd, compact);
                if (key >= 0 && tilePartIndex.HasValue && tilePartIndex.Value != 0)
                {
                    if (!settings.RawSegments.TryGetValue(key, out JpxRange inEffect))
                        cs.Main.RawSegments.TryGetValue(key, out inEffect);
                    if (inEffect.Length != segEnd - pos || !SameBytes(data, inEffect.Start, pos, inEffect.Length))
                    {
                        reason = $"{MarkerName(marker)} appears in tile-part {tilePartIndex.Value}, differing from the one in effect; it is allowed only in a tile's first tile-part (T.800 §A.6)";
                        return false;
                    }
                    cs.Deviations.Add($"{MarkerName(marker)} is restated in tile-part {tilePartIndex.Value}, identical to the one in effect; ignored (T.800 §A.6)");
                    pos = segEnd;
                    continue;
                }

                switch (marker)
                {
                    case CAP:
                        ok = main ? AcceptsExtendedCapabilities(cs, data, segStart, segEnd, out reason)
                                  : Refuse($"CAP appears in {where}; it belongs in the main header (T.800 §A.5.2)", out reason);
                        break;
                    case COD:
                        if (settings.Coding != null) { ok = Refuse($"{where} has a second COD (T.800 §A.6.1)", out reason); break; }
                        ok = TryParseCod(cs, data, segStart, segEnd, out JpxCodingDefault cod, out reason);
                        settings.Coding = cod;
                        break;
                    case COC:
                        ok = TryParseCoc(cs, data, segStart, segEnd, compact, out int cocComponent, out JpxComponentCoding coc, out reason);
                        if (ok && settings.ComponentCoding[cocComponent] != null)
                            ok = Refuse($"{where} has a second COC for component {cocComponent} (T.800 §A.6.2)", out reason);
                        if (ok) settings.ComponentCoding[cocComponent] = coc;
                        break;
                    case QCD:
                        if (settings.Quantization != null) { ok = Refuse($"{where} has a second QCD (T.800 §A.6.4)", out reason); break; }
                        ok = TryParseQuantization(data, segStart, segEnd, "QCD", out JpxQuantization qcd, out reason);
                        settings.Quantization = qcd;
                        break;
                    case QCC:
                        ok = TryReadComponentIndex(cs, data, ref segStart, segEnd, compact, "QCC", out int qccComponent, out reason)
                             && TryParseQuantization(data, segStart, segEnd, "QCC", out JpxQuantization qcc, out reason)
                             && Store(settings.ComponentQuantization, qccComponent, qcc, $"{where} has a second QCC for component {qccComponent} (T.800 §A.6.5)", out reason);
                        break;
                    case RGN:
                        ok = TryParseRgn(cs, data, segStart, segEnd, compact, out int rgnComponent, out int shift, out reason);
                        if (ok && settings.RoiShift[rgnComponent].HasValue)
                            ok = Refuse($"{where} has a second RGN for component {rgnComponent} (T.800 §A.6.3)", out reason);
                        if (ok) settings.RoiShift[rgnComponent] = shift;
                        break;
                    case POC:
                        if (pocInThisHeader) { ok = Refuse($"{where} has a second POC (T.800 §A.6.6)", out reason); break; }
                        pocInThisHeader = true;
                        ok = TryParsePoc(data, segStart, segEnd, compact, out List<JpxProgressionChange> poc, out reason);
                        if (!ok) break;
                        if (settings.ProgressionChanges == null)
                        {
                            // A tile's progression changes start in its first tile-part header (§B.12.3).
                            if (tilePartIndex.HasValue && tilePartIndex.Value != 0)
                            {
                                ok = Refuse($"POC appears in tile-part {tilePartIndex.Value} of a tile whose first tile-part has none (T.800 §A.6.6)", out reason);
                                break;
                            }
                            settings.ProgressionChanges = poc;
                        }
                        else
                        {
                            // A later tile-part's volumes follow the earlier ones (§B.12.3, Figure B.15b).
                            settings.ProgressionChanges.AddRange(poc);
                        }
                        break;
                    case PPM:
                        ok = main ? AddPacked(data, segStart, segEnd, packed, "PPM", out reason)
                                  : Refuse($"PPM appears in {where}; it belongs in the main header (T.800 §A.7.4)", out reason);
                        break;
                    case PPT:
                        ok = !main ? AddPacked(data, segStart, segEnd, packed, "PPT", out reason)
                                   : Refuse("PPT appears in the main header; it belongs in a tile-part header (T.800 §A.7.5)", out reason);
                        break;
                    case CBD:
                    case MCT:
                    case MCC:
                    case MCO:
                    case NLT:
                        // Part 2 segments the JPX baseline admits; meaningless without Rsiz's Part 2 bit.
                        // Kept per header as well, because a tile-part header's override a main header's.
                        if ((cs.Size.Rsiz & RsizPart2) != 0)
                        {
                            var kept = new KeyValuePair<int, JpxRange>(marker, new JpxRange(segStart, segEnd));
                            cs.Part2Segments.Add(kept);
                            settings.Part2Segments.Add(kept);
                        }
                        break;
                    case PLT:
                        // Informational, but an independent statement of every packet's length, kept
                        // for the tier-2 reader to check itself against. Meaningless in the main header
                        // (T.800 §A.7.3: tile-part headers only) — reported, as a misplaced TLM is refused.
                        if (main) cs.Deviations.Add($"a PLT marker segment at offset {pos} is in the main header; T.800 §A.7.3 puts packet lengths in tile-part headers, and it is ignored");
                        else ok = AddPacked(data, segStart, segEnd, packetLengths, "PLT", out reason);
                        break;
                    case PRF:
                    case TLM:
                    case PLM:
                        // Main-header-only (T.800 §A.5.3, §A.7.1, §A.7.2): in a tile-part header they
                        // are not PLTs, and reading their bytes as packet lengths would be wrong silently.
                        if (!main) ok = Refuse($"{MarkerName(marker)} appears in a tile-part header; it belongs in the main header (T.800 §A.7)", out reason);
                        break;
                    case CRG:
                    case COM:
                    default:
                        break;   // informational, or unknown: discarded by its length (T.800 §A.1)
                }
                if (!ok) return false;
                if (key >= 0) settings.RawSegments[key] = new JpxRange(pos, segEnd);
                pos = segEnd;
            }
        }

        /// <summary>
        /// The identity of the coding setting a marker segment carries — the marker for COD and QCD,
        /// marker and component for COC, QCC and RGN — or -1 for any other segment.
        /// </summary>
        private static long SettingKey(int marker, byte[] data, long s, long e, bool compact)
        {
            switch (marker)
            {
                case COD:
                case QCD:
                    return marker;
                case COC:
                case QCC:
                case RGN:
                    int width = compact ? 1 : 2;
                    if (s + width > e) return marker;   // no component to read: never matches a stored key
                    return ((long)marker << 16) | (uint)(compact ? data[s] : U16(data, s));   // 0..65535: unsigned, no sign to extend
                default:
                    return -1;
            }
        }

        private static bool SameBytes(byte[] data, long a, long b, long length)
        {
            for (long i = 0; i < length; i++)
                if (data[a + i] != data[b + i]) return false;
            return true;
        }

        /// <summary>The first SOT or EOC marker at or after <c>from</c>, or <c>end</c> when there is none.</summary>
        private static long NextTileBoundary(byte[] data, long from, long end)
        {
            for (long i = from; i + 2 <= end; i++)
                if (data[i] == 0xFF && (data[i + 1] == (SOT & 0xFF) || data[i + 1] == (EOC & 0xFF))) return i;
            return end;
        }

        private static bool TryParseTilePart(JpxCodestream cs, byte[] data, ref long pos, long end, bool compact,
            out bool hasPpt, out string reason)
        {
            hasPpt = false;
            if (pos + 12 > end)
            {
                reason = $"the SOT marker segment at offset {pos} is truncated";
                return false;
            }
            int lsot = U16(data, pos + 2);
            if (lsot != 10)
            {
                reason = $"the SOT marker segment at offset {pos} is {lsot} bytes long; T.800 Table A.5 fixes it at 10";
                return false;
            }
            int isot = U16(data, pos + 4);
            long psot = U32(data, pos + 6);
            int tpsot = data[pos + 10];
            int tnsot = data[pos + 11];
            if (isot >= cs.Size.NumTiles)
            {
                reason = $"a tile-part names tile {isot}; the codestream has {cs.Size.NumTiles}";
                return false;
            }
            if (psot != 0 && psot < 14)
            {
                reason = $"the tile-part at offset {pos} declares {psot} bytes; it cannot be shorter than its SOT and SOD (T.800 Table A.5)";
                return false;
            }

            long partEnd;
            bool eocAtEnd = end - 2 >= pos && U16(data, end - 2) == EOC;
            if (psot == 0)
            {
                partEnd = eocAtEnd ? end - 2 : end;   // "all data until the EOC"
                // Table A.5 allows 0 for the last tile-part only. A later SOT inside the range would
                // be swallowed as this tile-part's packet bytes and its tile lost without a word:
                // the range stops at the SOT instead, reported.
                long later = NextTileBoundary(data, pos + 12, partEnd);
                if (later < partEnd && U16(data, later) == SOT)
                {
                    cs.Deviations.Add($"the tile-part at offset {pos} declares Psot 0 ('to the EOC') but another SOT follows at offset {later}; T.800 Table A.5 allows 0 for the last tile-part only, so it ends there");
                    partEnd = later;
                }
            }
            else if (eocAtEnd && pos + psot > end - 2)
            {
                // An overshoot into or past a trailing EOC is the encoder's, not a download's: the EOC
                // is kept out of the tile-part's data, and seen.
                partEnd = end - 2;
                cs.Deviations.Add($"the tile-part at offset {pos} declares {psot} bytes, {pos + psot - (end - 2)} past the EOC; clamped to it");
            }
            else if (pos + psot > end)
            {
                partEnd = end;   // clamped: a truncated file still decodes
                cs.Deviations.Add($"the tile-part at offset {pos} declares {psot} bytes, {pos + psot - end} past the end of the codestream; clamped to it, and the codestream is truncated");
            }
            else
            {
                partEnd = pos + psot;
            }

            if (!cs.Tiles.TryGetValue(isot, out JpxTile tile))
            {
                tile = new JpxTile(isot, cs.Size.Components.Length);
                cs.Tiles.Add(isot, tile);
            }
            if (tpsot != tile.Parts.Count)
            {
                reason = $"tile {isot}'s tile-part {tpsot} appears where tile-part {tile.Parts.Count} belongs (T.800 §A.4.2)";
                return false;
            }
            // TNsot (Table A.6): 0, or the tile's exact tile-part count — then a TPsot at or past it,
            // or a count another tile-part of the tile declared differently, is a deviation; the
            // data is read either way.
            if (tnsot != 0)
            {
                if (tpsot >= tnsot)
                    cs.Deviations.Add($"tile {isot}'s tile-part {tpsot} declares {tnsot} tile-part(s) in all (TNsot), which it is not within; T.800 Table A.6 makes TNsot 0 or the exact count");
                else if (tile.DeclaredParts != 0 && tile.DeclaredParts != tnsot)
                    cs.Deviations.Add($"tile {isot}'s tile-part {tpsot} declares {tnsot} tile-part(s) in all (TNsot) where an earlier one declared {tile.DeclaredParts}");
                tile.DeclaredParts = tnsot;
            }

            long headerPos = pos + 12;
            var ppt = new List<KeyValuePair<int, JpxRange>>();
            var plt = new List<KeyValuePair<int, JpxRange>>();
            var probe = new JpxTilePart(tpsot, default);   // marks the header as a tile-part's
            if (!TryParseHeader(cs, tile.Settings, probe, data, ref headerPos, partEnd, compact, ppt, plt, tpsot, out reason))
                return false;

            var part = new JpxTilePart(tpsot, new JpxRange(headerPos, partEnd));
            if (!SortByIndex(ppt, part.PackedHeaders, "PPT", false, cs, out reason)) return false;
            var pltRanges = new List<JpxRange>();
            SortByIndex(plt, pltRanges, "PLT", true, cs, out _);
            ReadPacketLengths(data, pltRanges, part.PacketLengths);
            hasPpt = part.PackedHeaders.Count > 0;
            tile.Parts.Add(part);
            pos = partEnd;
            return true;
        }

        private static bool TryParseCod(JpxCodestream cs, byte[] data, long s, long e, out JpxCodingDefault cod, out string reason)
        {
            cod = null;
            if (e - s < 10) return Refuse("COD is shorter than its fixed parameters (T.800 Table A.12)", out reason);

            int scod = data[s];
            int order = data[s + 1];
            int layers = U16(data, s + 2);
            int mct = data[s + 4];
            if (order > 4) return Refuse($"COD names progression order {order}; T.800 Table A.16 defines 0 to 4", out reason);
            if (layers < 1) return Refuse("COD declares 0 quality layers (T.800 Table A.14)", out reason);
            bool part2Mct = (cs.Part2Capabilities & Part2MultipleComponentTransform) != 0;
            if (mct > 1 && !part2Mct)
                return Refuse($"COD names multiple component transformation {mct}; T.800 Table A.17 defines 0 and 1", out reason);
            // T.801 Table A.8: bit 0 is Annex G's RCT/ICT, bit 1 the array-based Part 2 transformation
            // (which MCO, MCC and MCT segments describe — JpxMultipleComponentTransform resolves it),
            // bit 2 the wavelet-based one, outside the JPX baseline (§M.9.2.3). Bit 0 never combines
            // with the others.
            if ((mct & 0x04) != 0)
                return Refuse("COD names a wavelet-based multiple component transformation (T.801 Table A.8); the JPX baseline admits only the array-based one (T.801 §M.9.2.3)", out reason);
            if ((mct & ~0x07) != 0)
                return Refuse($"COD names multiple component transformation {mct}; T.801 Table A.8 reserves it", out reason);
            if ((mct & 0x01) != 0 && mct != 1)
                return Refuse($"COD names multiple component transformation {mct}, combining Annex G's with a Part 2 one; T.801 Table A.8 defines no such combination", out reason);
            bool mctApplies = (mct & 0x01) != 0;
            if (mct == 1 && cs.Size.Components.Length < 3)
            {
                // Non-conforming but unambiguous: there are no three components to transform. Greyscale
                // codestreams in the wild carry the flag (non-regression 123.j2c, bug.j2c), and
                // Other decoders ignore it too.
                cs.Deviations.Add($"COD applies the component transformation to components 0-2 of a {cs.Size.Components.Length}-component image (T.800 G.2); ignored");
                mctApplies = false;
            }

            if (!TryParseSpcod(cs, data, s + 5, e, (scod & 0x01) != 0, "COD", out JpxComponentCoding component, out reason))
                return false;

            cod = new JpxCodingDefault((JpxProgressionOrder)order, layers, mctApplies,
                sopAllowed: (scod & 0x02) != 0, ephUsed: (scod & 0x04) != 0, component);
            return true;
        }

        private static bool TryParseCoc(JpxCodestream cs, byte[] data, long s, long e, bool compact,
            out int component, out JpxComponentCoding coc, out string reason)
        {
            coc = null;
            if (!TryReadComponentIndex(cs, data, ref s, e, compact, "COC", out component, out reason)) return false;
            if (e - s < 1) return Refuse("COC is truncated (T.800 Table A.22)", out reason);
            int scoc = data[s];
            return TryParseSpcod(cs, data, s + 1, e, (scoc & 0x01) != 0, "COC", out coc, out reason);
        }

        /// <summary>The SPcod / SPcoc parameters (T.800 Table A.15), which must fill the segment exactly.</summary>
        private static bool TryParseSpcod(JpxCodestream cs, byte[] data, long s, long e, bool userPrecincts, string name,
            out JpxComponentCoding coding, out string reason)
        {
            coding = null;
            if (e - s < 5) return Refuse($"{name} is shorter than its coding-style parameters (T.800 Table A.15)", out reason);

            int levels = data[s];
            int xcbValue = data[s + 1] & 0x0F;
            int ycbValue = data[s + 2] & 0x0F;
            int style = data[s + 3];
            int transform = data[s + 4];

            // Table A.19 reserves the two most significant bits; HTJ2K (ITU-T T.814 §A.4) gives them
            // meaning. Bit 6 alone: every code-block of the tile-component is an HT code-block (Table
            // A.3). Bits 6 and 7: each code-block may be either (Table A.4) — the MIXED set, where tier 2
            // tells a block's coder from its first contribution with bytes (§A.4's length rules). CAP
            // decides what bits 6 and 7 mean here (§A.3.2): with Ccap15's bits 14 and 15 both 0 the
            // codestream is HTONLY, every block is HT, and its NOTE says bits 6 and 7 may still both be
            // set — so Table A.4 is read as Table A.3: vertical causality, the one other bit that applies
            // to an HT code-block, kept, the Part 1 bits dropped. In a MIXED codestream the Part 1 bits
            // stay for the Part 1 blocks, but Table A.4 reserves bypass and termination on each pass, which
            // §A.4 forbids them.
            bool mixed = false;
            if ((style & 0xC0) == 0xC0)
            {
                if (cs.HtCapabilities < 0)
                    return Refuse($"{name} sets code-block style bits 6 and 7 (ITU-T T.814 Table A.4) with no CAP's Ccap15 to say whether the codestream is HTONLY (§A.3.2)", out reason);
                if ((cs.HtCapabilities & 0xC000) == 0x8000)
                    return Refuse($"{name} sets code-block style bit 7 in a codestream whose CAP's Ccap15 declares the HTDECLARED set, where bit 7 is 0 (ITU-T T.814 §A.3.2)", out reason);
                if ((cs.HtCapabilities & 0xC000) == 0)
                    style &= (int)(JpxCodeBlockStyle.HighThroughput | JpxCodeBlockStyle.VerticallyCausal);
                else if ((style & (int)(JpxCodeBlockStyle.SelectiveBypass | JpxCodeBlockStyle.TerminateEachPass)) != 0)
                    return Refuse($"{name} sets code-block style bits 6 and 7 with selective bypass or termination on each pass, which ITU-T T.814 Table A.4 reserves: the MIXED set's Part 1 code-blocks use neither (§A.4)", out reason);
                else
                    mixed = true;
            }
            if ((style & 0x80) != 0 && !mixed) return Refuse($"{name} sets code-block style bit 7 without bit 6, which T.800 Table A.19 reserves and T.814 Table A.4 does not define", out reason);
            if (levels > 32) return Refuse($"{name} declares {levels} decomposition levels; T.800 Table A.15 allows 0 to 32", out reason);
            if (xcbValue > 8 || ycbValue > 8)
                return Refuse($"{name} declares a code-block exponent offset of {System.Math.Max(xcbValue, ycbValue)}; T.800 Table A.18 allows 0 to 8", out reason);
            int xcb = xcbValue + 2, ycb = ycbValue + 2;
            if (xcb + ycb > 12)
                return Refuse($"{name} declares {1 << xcb}x{1 << ycb} code-blocks; T.800 Table A.18 limits xcb + ycb to 12", out reason);
            if (transform > 1) return Refuse($"{name} names wavelet transformation {transform}; T.800 Table A.20 defines 0 and 1", out reason);

            long expected = s + 5 + (userPrecincts ? levels + 1 : 0);
            if (e != expected)
                return Refuse($"{name}'s coding-style parameters are {e - s} bytes where they need {expected - s} (T.800 Table A.15, equation A-2)", out reason);

            byte[] precincts = null;
            if (userPrecincts)
            {
                precincts = new byte[levels + 1];
                for (int r = 0; r <= levels; r++)
                {
                    byte pp = data[s + 5 + r];
                    if (r > 0 && ((pp & 0x0F) == 0 || (pp >> 4) == 0))
                        return Refuse($"{name} sets a precinct exponent of 0 at resolution level {r}; only level 0 may (T.800 Table A.21)", out reason);
                    precincts[r] = pp;
                }
            }

            coding = new JpxComponentCoding(levels, xcb, ycb, (JpxCodeBlockStyle)style, transform == 1, precincts);
            reason = null;
            return true;
        }

        /// <summary>A QCD's or QCC's Sqcd and step sizes (T.800 Tables A.28 to A.30).</summary>
        private static bool TryParseQuantization(byte[] data, long s, long e, string name, out JpxQuantization q, out string reason)
        {
            q = null;
            if (e - s < 2) return Refuse($"{name} has no step sizes (T.800 Table A.27)", out reason);

            int sq = data[s];
            int style = sq & 0x1F;
            int guard = sq >> 5;
            long body = e - s - 1;
            int[] exponents, mantissas;

            switch (style)
            {
                case 0:   // no quantisation: an 8-bit exponent per sub-band (Table A.29)
                    exponents = new int[body];
                    mantissas = new int[body];
                    for (int i = 0; i < body; i++) exponents[i] = data[s + 1 + i] >> 3;
                    break;
                case 1:   // scalar derived: one 16-bit value, for NLLL (Table A.30)
                case 2:   // scalar expounded: a 16-bit value per sub-band
                    if (body % 2 != 0) return Refuse($"{name} has an odd number of bytes of 16-bit step sizes", out reason);
                    if (style == 1 && body != 2) return Refuse($"{name} is derived but signals {body / 2} step sizes; derived quantisation signals one (T.800 Table A.28)", out reason);
                    exponents = new int[body / 2];
                    mantissas = new int[body / 2];
                    for (int i = 0; i < exponents.Length; i++)
                    {
                        int v = U16(data, s + 1 + 2 * i);
                        exponents[i] = v >> 11;
                        mantissas[i] = v & 0x7FF;
                    }
                    break;
                default:
                    return Refuse($"{name} names quantisation style {style}; T.800 Table A.28 defines 0 to 2", out reason);
            }

            q = new JpxQuantization((JpxQuantizationStyle)style, guard, exponents, mantissas);
            reason = null;
            return true;
        }

        private static bool TryParseRgn(JpxCodestream cs, byte[] data, long s, long e, bool compact,
            out int component, out int shift, out string reason)
        {
            shift = 0;
            if (!TryReadComponentIndex(cs, data, ref s, e, compact, "RGN", out component, out reason)) return false;
            if (e - s != 2) return Refuse("RGN is not the length T.800 Table A.24 gives it", out reason);
            if (data[s] != 0) return Refuse($"RGN names ROI style {data[s]}; T.800 Table A.25 defines only 0 (implicit)", out reason);
            shift = data[s + 1];
            return true;
        }

        private static bool TryParsePoc(byte[] data, long s, long e, bool compact, out List<JpxProgressionChange> changes, out string reason)
        {
            changes = null;
            int entry = compact ? 7 : 9;
            long body = e - s;
            if (body < entry || body % entry != 0)
                return Refuse($"POC's {body} bytes are not a whole number of {entry}-byte progressions (T.800 equation A-6)", out reason);

            var list = new List<JpxProgressionChange>((int)(body / entry));
            for (long at = s; at < e; at += entry)
            {
                int rs = data[at];
                int cs = compact ? data[at + 1] : U16(data, at + 1);
                long p = at + (compact ? 2 : 3);
                int lye = U16(data, p);
                int re = data[p + 2];
                int ce = compact ? data[p + 3] : U16(data, p + 3);
                int order = data[p + (compact ? 4 : 5)];
                if (ce == 0) ce = compact ? 256 : 16384;   // "0 is interpreted as 256" (T.800 Table A.32)

                if (rs > 32 || re <= rs || re > 33)
                    return Refuse($"POC's resolution range {rs} to {re} is outside T.800 Table A.32's bounds", out reason);
                if (ce <= cs) return Refuse($"POC's component range {cs} to {ce} is empty (T.800 Table A.32)", out reason);
                if (lye < 1) return Refuse("POC ends a progression at layer 0 (T.800 Table A.32)", out reason);
                if (order > 4) return Refuse($"POC names progression order {order}; T.800 Table A.16 defines 0 to 4", out reason);
                list.Add(new JpxProgressionChange(rs, cs, lye, re, ce, (JpxProgressionOrder)order));
            }

            changes = list;
            reason = null;
            return true;
        }

        /// <summary>
        /// A CAP marker segment's <c>Pcap</c> (T.800 §A.5.2). Bit 15, <c>Pcap15</c>, is HTJ2K
        /// (ITU-T T.814 §A.3): its <c>Ccap15</c> follows and is kept on the codestream
        /// (<see cref="JpxCodestream.HtCapabilities"/>), refused only for the value Table A.2 reserves.
        /// Every other bit names an extension this library does not implement, and refuses the
        /// codestream by name.
        /// </summary>
        private static bool AcceptsExtendedCapabilities(JpxCodestream cs, byte[] data, long s, long e, out string reason)
        {
            reason = null;
            if (e - s < 4) return Refuse("CAP is shorter than its Pcap field (T.800 Table A.11bis)", out reason);
            long pcap = U32(data, s);
            if (pcap == 0) return true;

            var named = new List<string>();
            for (int i = 1; i <= 32; i++)
                if (i != 15 && (pcap & (1L << (32 - i))) != 0)
                    named.Add("Pcap" + i);
            if (named.Count > 0)
            {
                reason = "the codestream signals extended capabilities this library does not decode: " + string.Join(", ", named);
                return false;
            }

            // Only Pcap15 is set, so one Ccap field follows (T.800 Table A.11bis).
            if (e - s < 6) return Refuse("CAP declares Pcap15 (HTJ2K) without its Ccap15 field (ITU-T T.814 §A.3)", out reason);
            int ccap15 = U16(data, s + 4);
            if ((ccap15 & 0xC000) == 0x4000)
                return Refuse($"CAP's Ccap15 is 0x{ccap15:X4}: bits 14 and 15 of 01 are reserved (ITU-T T.814 Table A.2)", out reason);
            cs.HtCapabilities = ccap15;
            return true;
        }

        private static bool AddPacked(byte[] data, long s, long e, List<KeyValuePair<int, JpxRange>> packed, string name, out string reason)
        {
            reason = null;
            if (e - s < 1) return Refuse($"{name} has no index byte (T.800 §A.7)", out reason);
            packed.Add(new KeyValuePair<int, JpxRange>(data[s], new JpxRange(s + 1, e)));
            return true;
        }

        private static bool TryReadComponentIndex(JpxCodestream cs, byte[] data, ref long s, long e, bool compact,
            string name, out int component, out string reason)
        {
            component = 0;
            int width = compact ? 1 : 2;
            if (e - s < width) return Refuse($"{name} is truncated before its component index", out reason);
            component = compact ? data[s] : U16(data, s);
            if (component >= cs.Size.Components.Length)
                return Refuse($"{name} names component {component}; the image has {cs.Size.Components.Length}", out reason);
            s += width;
            reason = null;
            return true;
        }

        private static bool Store<T>(T[] slots, int index, T value, string duplicate, out string reason) where T : class
        {
            if (slots[index] != null) return Refuse(duplicate, out reason);
            slots[index] = value;
            reason = null;
            return true;
        }

        /// <summary>
        /// PLT's Iplt values (T.800 Table A.42): each packet length is a big-endian sequence of 7-bit
        /// groups, a set high bit meaning another group follows, and a value may continue from one PLT
        /// segment into the next. A length too long for 48 bits ends the reading; PLT is advisory.
        /// </summary>
        private static void ReadPacketLengths(byte[] data, List<JpxRange> ranges, List<long> into)
        {
            long value = 0;
            foreach (JpxRange range in ranges)
            {
                for (long i = range.Start; i < range.End; i++)
                {
                    int b = data[i];
                    value = (value << 7) | (uint)(b & 0x7F);
                    if (value >= 1L << 48) return;
                    if ((b & 0x80) == 0)
                    {
                        into.Add(value);
                        value = 0;
                    }
                }
            }
        }

        /// <summary>
        /// Orders PPM, PPT or PLT payloads by their Z index — T.800 §A.7.4 concatenates them in
        /// increasing order. §A.7.4 and §A.7.5 make the indices unique per series; two segments
        /// sharing one would leave the concatenation's order to the sort, so a duplicate refuses
        /// packed headers (they would be read in some order, silently wrong) and is a deviation for
        /// PLT, whose lengths are advisory. The sort is stable either way.
        /// </summary>
        /// <remarks>
        /// A counting sort over the index's 256 values. It was an insertion sort, on the reasoning
        /// that a series is short: it is as long as the file makes it, a segment being six bytes, and
        /// a tile-part's PLTs in the order 255 … 255, 0 … 0 cost a quarter of their count squared.
        /// </remarks>
        private static bool SortByIndex(List<KeyValuePair<int, JpxRange>> packed, List<JpxRange> into, string name, bool advisory,
            JpxCodestream cs, out string reason)
        {
            reason = null;
            var start = new int[257];
            foreach (var p in packed) start[p.Key + 1]++;
            for (int k = 1; k < start.Length; k++) start[k] += start[k - 1];
            var ordered = new KeyValuePair<int, JpxRange>[packed.Count];
            foreach (var p in packed)
            {
                ordered[start[p.Key]++] = p;
                SortedSegmentPlacements++;
            }
            for (int i = 1; i < ordered.Length; i++)
                if (ordered[i].Key == ordered[i - 1].Key)
                {
                    string what = $"two {name} marker segments carry the index {ordered[i].Key}; T.800 §A.7 numbers a series once each";
                    if (!advisory) return Refuse(what + ", and packed headers in an unknown order would decode wrong pixels", out reason);
                    cs.Deviations.Add(what + "; the packet lengths are advisory and are read in index order, those sharing one in file order");
                    break;
                }
            foreach (var p in ordered) into.Add(p.Value);
            return true;
        }

        /// <summary>
        /// Segments <see cref="SortByIndex"/> has put in place on the calling thread: the evidence
        /// that each is placed once, never shifted along the ones before it.
        /// </summary>
        [System.ThreadStatic] internal static long SortedSegmentPlacements;

        private static bool Refuse(string why, out string reason)
        {
            reason = why;
            return false;
        }

        private static string MarkerName(int marker)
        {
            switch (marker)
            {
                case SOC: return "SOC";
                case SOT: return "SOT";
                case SOD: return "SOD";
                case EOC: return "EOC";
                case SIZ: return "SIZ";
                case CAP: return "CAP";
                case PRF: return "PRF";
                case COD: return "COD";
                case COC: return "COC";
                case RGN: return "RGN";
                case QCD: return "QCD";
                case QCC: return "QCC";
                case POC: return "POC";
                case TLM: return "TLM";
                case PLM: return "PLM";
                case PLT: return "PLT";
                case PPM: return "PPM";
                case PPT: return "PPT";
                case CRG: return "CRG";
                case COM: return "COM";
                default: return $"marker 0x{marker:X4}";
            }
        }

        private static int U16(byte[] d, long i) => JpxHeader.ReadU16(d, i);

        private static long U32(byte[] d, long i) => JpxHeader.ReadU32(d, i);
    }
}
