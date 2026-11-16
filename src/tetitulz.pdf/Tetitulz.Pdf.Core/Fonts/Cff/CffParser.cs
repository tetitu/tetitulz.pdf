// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Fonts.Cff
{
    /// <summary>
    /// Parses CFF (Compact Font Format) binary data into a <see cref="CffFont"/>.
    /// Reference: Adobe Technical Note #5176 "The Compact Font Format Specification".
    /// </summary>
    internal static class CffParser
    {
        /// <summary>
        /// Upper bound on the glyph count trusted from a font's header when walking a charset.
        /// A GID is a 16-bit value, so 65536 is the largest count that can be addressed at all;
        /// a malformed font advertising more would otherwise drive a multi-billion-iteration loop.
        /// </summary>
        /// <remarks>
        /// <para><b>Applied in exactly one place</b> — <see cref="TryWalkCharset"/>, which all three
        /// charset walks go through, the GID→SID walk included.</para>
        ///
        /// <para><b>It cannot fire today, and that is structural rather than a gap in the
        /// fixtures.</b> <c>glyphCount</c> is <c>charstrings.Count</c>, and a CFF1 INDEX declares
        /// its count in a <b>Card16</b> — so the value can never exceed 65535, one short of this
        /// bound. Mutating the clamp away therefore survives every test and always will, for as
        /// long as the count is read from that field. It is kept because it bounds the walk against
        /// its <i>argument</i> rather than against today's only caller: a future caller computing
        /// <c>glyphCount</c> some other way would inherit the guard rather than need to remember
        /// it. Recorded in GAPS.md §9 so a surviving mutation here reads as this, not as missing
        /// coverage.</para>
        /// </remarks>
        private const int MaxGlyphs = 65536;

        // ----------------------------------------------------------------
        //  Public entry point
        // ----------------------------------------------------------------

        /// <summary>
        /// Reason a CFF stream could not be parsed.  Surfaced via
        /// <see cref="LastFailureReason"/> so callers (and tests) can tell
        /// "CFF2 / variable font" apart from "corrupt data".
        /// </summary>
        public enum CffFailure
        {
            None,
            /// <summary>Header was missing or version was unrecognised (not 1).</summary>
            UnsupportedVersion,
            /// <summary>A CFF2 (variable-font) program that could not be parsed — e.g. an
            /// unsupported CFF2 sub-structure. CFF1 (incl. CID-keyed / FDSelect) and well-formed
            /// CFF2 at the default instance ARE supported; this fires only on CFF2 we cannot read.</summary>
            UnsupportedFeature,
            /// <summary>Data was malformed (offset out of range, bad INDEX header, etc.).</summary>
            Malformed,
            /// <summary>Empty / null buffer.</summary>
            EmptyData,
        }

        /// <summary>
        /// Reason for the most recent <see cref="Parse(byte[])"/> failure (thread-local).
        /// Reset to <see cref="CffFailure.None"/> on every call.
        /// </summary>
        [ThreadStatic]
        private static CffFailure _lastFailure;
        public static CffFailure LastFailureReason => _lastFailure;

        /// <summary>
        /// Parses CFF binary data.  Returns <c>null</c> if the data is not valid CFF;
        /// callers can inspect <see cref="LastFailureReason"/> to distinguish
        /// "CFF2 / variable font" from "corrupt data".
        /// </summary>
        public static CffFont Parse(byte[] data) => Parse(data, sfntDepth: 0);

        /// <summary>
        /// The real entry point. <paramref name="sfntDepth"/> counts <c>'OTTO'</c> containers already
        /// unwrapped.
        /// </summary>
        /// <remarks>
        /// <b>An sfnt may wrap a CFF; a CFF may not wrap an sfnt</b>, so one unwrap is the whole
        /// legitimate range and depth 1 is a refusal rather than a generous cap.
        ///
        /// <para>
        /// Without it this recursed forever. Nothing required the extracted table to be smaller than
        /// its container, so a table directory entry with offset 0 and length == the file length
        /// yielded a byte-for-byte copy of the input — which still began with <c>'OTTO'</c>. A
        /// 100-byte <c>/FontFile3</c> shaped that way killed the host **process**: a
        /// <see cref="StackOverflowException"/> cannot be caught in .NET Core, so
        /// <c>FontCache.LoadEmbedded</c>'s surrounding catch never ran. Rendering one page of an
        /// untrusted PDF was enough.
        /// </para>
        /// </remarks>
        private static CffFont Parse(byte[] data, int sfntDepth)
        {
            _lastFailure = CffFailure.None;
            if (data == null || data.Length < 4) { _lastFailure = CffFailure.EmptyData; return null; }

            // SFNT container: 'OTTO' (0x4F54544F) = CFF-based OpenType. Extract the CFF or CFF2 table
            // and recurse. TrueType (0x00010000) has no CFF table — report unsupported.
            if (data[0] == 0x4F && data[1] == 0x54 && data[2] == 0x54 && data[3] == 0x4F)
            {
                if (sfntDepth >= 1) { _lastFailure = CffFailure.Malformed; return null; }

                byte[] cffTable = ExtractCffTableFromSfnt(data);

                // A table that is not strictly smaller than its container cannot be a table of it;
                // it is the self-reference above wearing a different offset.
                if (cffTable != null && cffTable.Length >= data.Length)
                {
                    _lastFailure = CffFailure.Malformed;
                    return null;
                }

                if (cffTable != null) return Parse(cffTable, sfntDepth + 1);
                _lastFailure = CffFailure.Malformed;
                return null;
            }
            if (data[0] == 0x00 && data[1] == 0x01 && data[2] == 0x00 && data[3] == 0x00)
            {
                _lastFailure = CffFailure.UnsupportedVersion;
                return null;
            }

            // Sniff version before entering parser: CFF2 (major == 2) is a distinct
            // format with a different header layout (Adobe TN #5177).
            if (data[0] == 2)
            {
                try
                {
                    var cff2 = ParseCff2Internal(data);
                    if (cff2 != null) return cff2;
                }
                catch { }
                _lastFailure = CffFailure.UnsupportedFeature;
                return null;
            }
            if (data[0] != 1)
            {
                _lastFailure = CffFailure.UnsupportedVersion;
                return null;
            }
            try
            {
                var result = ParseInternal(data);
                if (result == null && _lastFailure == CffFailure.None)
                    _lastFailure = CffFailure.Malformed;
                return result;
            }
            catch
            {
                _lastFailure = CffFailure.Malformed;
                return null;
            }
        }

        // ----------------------------------------------------------------
        //  CFF2 parser (Adobe TN #5177 v2 / OpenType CFF2 table)
        // ----------------------------------------------------------------

        private static CffFont ParseCff2Internal(byte[] d)
        {
            // CFF2 header: major(1) minor(1) headerSize(1) topDictLength(uint16 BE)
            if (d.Length < 5) return null;
            byte hdrSize = d[2];
            int topDictLength = (d[3] << 8) | d[4];
            if (hdrSize < 5 || (long)hdrSize + topDictLength > d.Length) return null;

            // TOP DICT occupies [hdrSize .. hdrSize+topDictLength-1]
            var topDictBytes = new byte[topDictLength];
            Array.Copy(d, hdrSize, topDictBytes, 0, topDictLength);
            var topDict = ParseDict(topDictBytes);

            // GlobalSubr INDEX follows the TOP DICT immediately.
            int pos = hdrSize + topDictLength;
            var globalSubrs = ReadIndex(d, ref pos);

            // CharStrings INDEX — absolute offset from start of CFF2 data (key 17).
            int charStringsOffset = GetDictInt(topDict, 17, -1);
            if (charStringsOffset < 0 || charStringsOffset >= d.Length) return null;
            int p = charStringsOffset;
            var charstrings = ReadIndex(d, ref p);
            if (charstrings.Count == 0) return null;

            // Private DICT — key 18 = (size, offset), offset is absolute in CFF2.
            float defaultWidthX = 0f, nominalWidthX = 0f;
            var localSubrs = new List<byte[]>();
            if (topDict.TryGetValue(18, out var privateArr) && privateArr.Count == 2)
            {
                int privSize   = SaturatingCast.ToInt(privateArr[0]);
                int privOffset = SaturatingCast.ToInt(privateArr[1]);
                if (privOffset > 0 && privSize > 0 && (long)privOffset + privSize <= d.Length)
                {
                    var privData = new byte[privSize];
                    Array.Copy(d, privOffset, privData, 0, privSize);
                    var privDict = ParseDict(privData);
                    defaultWidthX = GetDictFloat(privDict, 20, 0f);
                    nominalWidthX = GetDictFloat(privDict, 21, 0f);
                    int subrsRel = GetDictInt(privDict, 19, -1);
                    if (subrsRel > 0)
                    {
                        long absSubrs = (long)privOffset + subrsRel;
                        if (absSubrs >= 0 && absSubrs < d.Length)
                        {
                            int sp = (int)absSubrs;
                            localSubrs = new List<byte[]>(ReadIndex(d, ref sp));
                        }
                    }
                }
            }

            // ItemVariationStore — key 24 = absolute offset (CFF2 only). The blend operator
            // (TN #5177 §5.3) removes one delta operand per variation *region* of the active
            // ItemVariationData (vsindex 0 by default), so size blend by that region count — NOT
            // the axis count, which is generally smaller for multi-axis fonts and would leave the
            // charstring stack misaligned (corrupting the outline even at the default instance).
            int blendRegionCount = 0;
            int varStoreOffset = GetDictInt(topDict, 24, -1);
            if (varStoreOffset > 0)
                blendRegionCount = ReadCff2BlendRegionCount(d, varStoreOffset);

            // CFF2 always uses identity GID mapping (no Encoding/Charset).
            var charCodeToGid = new Dictionary<int, int>();
            for (int gid = 0; gid < charstrings.Count && gid <= 255; gid++)
                charCodeToGid[gid] = gid;

            return new CffFont(
                charstrings, localSubrs, globalSubrs, charCodeToGid,
                defaultWidthX, nominalWidthX,
                glyphNameToGid: null,
                isCff2: true,
                blendRegionCount: blendRegionCount,
                fontMatrixScale: GetFontMatrixScale(topDict));
        }

        /// <summary>
        /// Reads the region count of the default (index 0) <c>ItemVariationData</c> in a CFF2 /
        /// OpenType <c>ItemVariationStore</c> — the number of delta operands the <c>blend</c>
        /// operator removes per blended value. Returns 0 on any malformed / out-of-bounds layout.
        /// <code>
        /// ItemVariationStore: uint16 format; Offset32 regionListOffset; uint16 ivdCount;
        ///                     Offset32 ivdOffsets[ivdCount]            (offsets from store start)
        /// ItemVariationData : uint16 itemCount; uint16 wordDeltaCount; uint16 regionIndexCount; …
        /// </code>
        /// </summary>
        internal static int ReadCff2BlendRegionCount(byte[] d, int varStoreOffset)
        {
            if (d == null || varStoreOffset < 0 || (long)varStoreOffset + 12 > d.Length) return 0;
            int ivdCount = (d[varStoreOffset + 6] << 8) | d[varStoreOffset + 7];
            if (ivdCount <= 0) return 0;
            long ivd0Rel = ((long)d[varStoreOffset + 8] << 24) | ((long)d[varStoreOffset + 9] << 16)
                         | ((long)d[varStoreOffset + 10] << 8) | d[varStoreOffset + 11];
            long ivd0Abs = (long)varStoreOffset + ivd0Rel;
            if (ivd0Abs < 0 || ivd0Abs + 6 > d.Length) return 0;
            return (d[(int)ivd0Abs + 4] << 8) | d[(int)ivd0Abs + 5]; // skip itemCount + wordDeltaCount
        }

        // ----------------------------------------------------------------
        //  Internal parsing (CFF1)
        // ----------------------------------------------------------------

        private static CffFont ParseInternal(byte[] d)
        {
            int pos = 0;

            // ---- Header ----
            byte major   = d[pos++];
            /* minor  */ pos++;
            byte hdrSize = d[pos++];
            /* offSize */ pos++;
            if (major != 1) return null;
            pos = hdrSize; // skip any extra header bytes

            // ---- Name INDEX (skip) ----
            pos = SkipIndex(d, pos);

            // ---- Top DICT INDEX ----
            int topDictStart = pos;
            var topDictRaw = ReadIndex(d, ref pos);
            if (topDictRaw.Count == 0) return null;

            // ---- String INDEX — read so we can resolve SID → glyph name ----
            var stringIndex = ReadIndex(d, ref pos);

            // ---- Global Subr INDEX ----
            var globalSubrs = ReadIndex(d, ref pos);

            // ---- Parse Top DICT (first font only) ----
            var topDict = ParseDict(topDictRaw[0]);

            // Top DICT key operators (single-byte):
            //  15 = charset offset, 16 = Encoding offset, 17 = CharStrings offset
            //  18 = Private (size, offset pair)
            // Two-byte (escape 12):
            //  36 = FDArray, 37 = FDSelect (CIDFonts — we skip FD-level per-glyph logic)

            int charStringsOffset = GetDictInt(topDict, 17, -1);
            if (charStringsOffset < 0) return null;

            // CID-keyed CFF: Top DICT contains ROS (12 30), FDArray (12 36), FDSelect (12 37).
            const int OpRos      = (12 << 8) | 30;
            const int OpFdArray  = (12 << 8) | 36;
            const int OpFdSelect = (12 << 8) | 37;
            if (topDict.ContainsKey(OpRos))
            {
                // charsetVal and glyphCount are read inside ParseCidKeyedCff from the TopDict
                // and CharStrings INDEX respectively — they're not yet declared here.
                int cidCharsetVal = GetDictInt(topDict, 15, 0);
                return ParseCidKeyedCff(d, topDict, charStringsOffset, cidCharsetVal,
                                        globalSubrs, OpFdArray, OpFdSelect);
            }

            // Encoding — 0 = StandardEncoding (default), 1 = ExpertEncoding, else offset
            int encodingVal = GetDictInt(topDict, 16, 0);

            // Charset — 0 = ISOAdobe (default), 1 = Expert, 2 = ExpertSubset, else offset
            int charsetVal = GetDictInt(topDict, 15, 0);

            // Private DICT location
            int privateDictSize   = 0;
            int privateDictOffset = -1;
            if (topDict.TryGetValue(18, out var privateArr) && privateArr.Count == 2)
            {
                privateDictSize   = SaturatingCast.ToInt(privateArr[0]);
                privateDictOffset = SaturatingCast.ToInt(privateArr[1]);
            }

            // ---- Charstrings INDEX ----
            int p = charStringsOffset;
            var charstrings = ReadIndex(d, ref p);
            int glyphCount = charstrings.Count;

            // ---- Private DICT + Local Subrs ----
            float defaultWidthX = 0f;
            float nominalWidthX = 0f;
            List<byte[]> localSubrs = new List<byte[]>();

            if (privateDictOffset > 0 && privateDictSize > 0 &&
                (long)privateDictOffset + privateDictSize <= d.Length)
            {
                var privData  = new byte[privateDictSize];
                Array.Copy(d, privateDictOffset, privData, 0, privateDictSize);
                var privDict  = ParseDict(privData);

                // 20 = defaultWidthX, 21 = nominalWidthX
                defaultWidthX = GetDictFloat(privDict, 20, 0f);
                nominalWidthX = GetDictFloat(privDict, 21, 0f);

                // 19 = Subrs (offset relative to start of Private DICT).  Guard
                // against malicious fonts with huge or negative subrs offsets
                // that would wrap to a negative absolute offset on addition.
                int subrsRelOffset = GetDictInt(privDict, 19, -1);
                if (subrsRelOffset > 0)
                {
                    long absSubrsOffsetL = (long)privateDictOffset + subrsRelOffset;
                    if (absSubrsOffsetL >= 0 && absSubrsOffsetL < d.Length)
                    {
                        int sp = (int)absSubrsOffsetL;
                        localSubrs = new List<byte[]>(ReadIndex(d, ref sp));
                    }
                }
            }

            // ---- Encoding → char code → GID map ----
            var charCodeToGid = BuildCharCodeToGid(d, encodingVal, charsetVal, glyphCount);

            // ---- Glyph name → GID (for PDF-level encoding overrides) ----
            var glyphNameToGid = BuildGlyphNameToGid(d, charsetVal, glyphCount, stringIndex);

            // The Expert charsets are approximated (SID == GID) above, and a charset that names
            // fewer glyphs than the font has was truncated, duplicated or pointed at a missing string.
            bool exactGlyphNames = (charsetVal == 0 || charsetVal >= 4)
                                   && glyphNameToGid != null && glyphNameToGid.Count == glyphCount;

            return new CffFont(
                charstrings,
                localSubrs,
                globalSubrs,
                charCodeToGid,
                defaultWidthX,
                nominalWidthX,
                glyphNameToGid,
                fontMatrixScale: GetFontMatrixScale(topDict),
                widthScaleIsKnown: TopMatrixScaleIsDeclared(topDict),
                hasExactGlyphNames: exactGlyphNames);
        }

        // ----------------------------------------------------------------
        //  Encoding / Charset → charCodeToGid
        // ----------------------------------------------------------------

        private static Dictionary<int, int> BuildCharCodeToGid(
            byte[] d, int encodingVal, int charsetVal, int glyphCount)
        {
            // Build SID → GID from charset first.
            // SID for GID 0 (notdef) is always 0.
            var sidToGid = new Dictionary<int, int> { [0] = 0 };

            // Keyed by SID; a repeated SID keeps the LAST glyph that claimed it, which is what the
            // open-coded loop did.
            WalkCharset(d, charsetVal, glyphCount, (gid, sid) => sidToGid[sid] = gid);

            // Now map char codes via encoding.
            var result = new Dictionary<int, int>();

            if (encodingVal == 0)
            {
                // Standard Encoding: char code → SID from the standard table
                foreach (var kv in StandardEncodingSidMap)
                {
                    if (sidToGid.TryGetValue(kv.Value, out int gid))
                        result[kv.Key] = gid;
                }
            }
            else if (encodingVal == 1)
            {
                // Expert Encoding — approximate: code == SID
                for (int code = 0; code < 256; code++)
                    if (sidToGid.TryGetValue(code, out int gid))
                        result[code] = gid;
            }
            else if (encodingVal >= 4 && encodingVal < d.Length - 1)
            {
                int p   = encodingVal;
                int fmt = d[p] & 0x7F;
                bool hasSupp = (d[p] & 0x80) != 0;
                p++;

                if (fmt == 0)
                {
                    // Format 0: nCodes uint8, then one char-code uint8 per GID starting at GID 1.
                    // code[i] is the character code for GID (i+1).
                    int nCodes = d[p++];
                    for (int gid = 1; gid <= nCodes && p < d.Length; gid++, p++)
                    {
                        int charCode = d[p];
                        result[charCode] = gid;
                    }
                }
                else if (fmt == 1)
                {
                    // Format 1: nRanges, each Range1 = (firstCode uint8, nLeft uint8).
                    // GIDs are assigned sequentially starting from GID 1 across all ranges.
                    int nRanges = d[p++];
                    int gid = 1;
                    for (int r = 0; r < nRanges && p + 1 < d.Length; r++)
                    {
                        int firstCode = d[p++];
                        int nLeft     = d[p++];
                        for (int i = 0; i <= nLeft; i++)
                            result[firstCode + i] = gid++;
                    }
                }

                if (hasSupp && p + 1 < d.Length)
                {
                    // Supplemental encoding: nSups, then pairs (code, SID)
                    int nSups = d[p++];
                    for (int i = 0; i < nSups && p + 2 < d.Length; i++, p += 3)
                    {
                        int code = d[p];
                        int sid  = (d[p + 1] << 8) | d[p + 2];
                        if (sidToGid.TryGetValue(sid, out int gid))
                            result[code] = gid;
                    }
                }
            }

            // Fallback: identity mapping for any glyph not covered above
            if (result.Count == 0)
            {
                for (int i = 0; i < Math.Min(glyphCount, 256); i++)
                    result[i] = i;
            }

            return result;
        }

        // ----------------------------------------------------------------
        //  Glyph name → GID map (for PDF encoding overrides)
        // ----------------------------------------------------------------

        /// <summary>
        /// Builds a glyph-name → GID dictionary by combining the charset (GID→SID)
        /// with the CFF string INDEX (SID→name for SIDs ≥ 391) and the standard string
        /// table (SIDs 0–390).  Returns null if no useful name data is available.
        /// </summary>
        private static Dictionary<string, int> BuildGlyphNameToGid(
            byte[] d, int charsetVal, int glyphCount, List<byte[]> stringIndex)
        {
            // GID → SID, through the one charset reading BuildCharCodeToGid uses; keyed by GID here.
            var gidToSid = new Dictionary<int, int> { [0] = 0 };
            WalkCharset(d, charsetVal, glyphCount, (gid, sid) => gidToSid[gid] = sid);

            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kv in gidToSid)
            {
                int gid = kv.Key;
                int sid = kv.Value;
                string name = SidToName(sid, stringIndex);
                if (name != null)
                    result[name] = gid;
            }
            return result.Count > 0 ? result : null;
        }

        /// <summary>
        /// Every glyph a charset names from GID 1, with its SID: the predefined charsets by their
        /// offsets 0–2 (CFF §13 — ISOAdobe GIDs 1–228 as SIDs 1–228; Expert and ExpertSubset read as
        /// SID == GID, an approximation), any other offset through <see cref="TryWalkCharset"/>.
        /// The charset reading of the SID→GID and GID→SID builders, which each carried the
        /// predefined arms.
        /// </summary>
        private static void WalkCharset(byte[] d, int charsetVal, int glyphCount, Action<int, int> emit)
        {
            if (charsetVal == 0)
            {
                for (int gid = 1; gid < Math.Min(glyphCount, 229); gid++)
                    emit(gid, gid);
            }
            else if (charsetVal == 1 || charsetVal == 2)
            {
                for (int gid = 1; gid < glyphCount; gid++)
                    emit(gid, gid);
            }
            else
            {
                TryWalkCharset(d, charsetVal, glyphCount, emit);
            }
        }

        /// <summary>
        /// Walks a §13 charset — formats 0, 1 and 2 — calling <paramref name="emit"/> with each
        /// glyph id and the SID (or CID) the charset assigns it, starting at GID 1. Returns
        /// <c>false</c> when the charset cannot be walked at all, so the caller can apply its own
        /// fallback.
        /// </summary>
        /// <remarks>
        /// <para><b>One walk for the SID→GID, GID→SID and CID→GID builders</b>, which read the same
        /// formats under the same cap. Copies drift, and a comment saying a cap is "shared by" two of
        /// three walks leaves the third with <b>no cap at all</b> — the shape CLAUDE.md calls a
        /// duplication report.</para>
        ///
        /// <para><b>What each copy contributed to the survivor:</b></para>
        /// <list type="bullet">
        ///   <item><description>the <c>MaxGlyphs</c> clamp, from two of the three — the GID→SID
        ///   walk now gets it too. Not live today, because <c>glyphCount</c> comes from a parsed
        ///   CharStrings INDEX and is bounded by the file; it is the guard that stops a later
        ///   change making it unbounded.</description></item>
        ///   <item><description>the <c>charsetVal &lt; d.Length</c> gate. The GID→SID copy used
        ///   <c>charsetVal + 1 &lt; d.Length</c>, which differs only for a charset whose format
        ///   byte is the file's last — and such a charset has no entries under either
        ///   reading.</description></item>
        /// </list>
        ///
        /// <para><b>One guard is dropped as inert rather than carried forward.</b> The SID→GID copy
        /// tested <c>sid &gt;= 0 &amp;&amp; sid &lt;= 0xFFFF</c> on a value built from two bytes,
        /// which cannot be anything else. A guard that cannot fail is the kind whose prose is about
        /// a real hazard while the code does nothing.</para>
        ///
        /// <para><b><c>nLeft</c> counts the glyphs AFTER the first</b> (hence <c>i &lt;= nLeft</c>),
        /// and format 2 differs from format 1 in that field's width and in nothing else.</para>
        /// </remarks>
        private static bool TryWalkCharset(byte[] d, int charsetVal, int glyphCount,
                                           Action<int, int> emit)
        {
            if (d == null || charsetVal < 4 || charsetVal >= d.Length) return false;

            int glyphLimit = Math.Min(glyphCount, MaxGlyphs);
            int p = charsetVal;
            byte format = d[p++];

            if (format == 0)
            {
                for (int gid = 1; gid < glyphLimit && p + 1 < d.Length; gid++, p += 2)
                    emit(gid, (d[p] << 8) | d[p + 1]);
                return true;
            }

            if (format == 1 || format == 2)
            {
                // The only difference between the two formats: format 1 spends one byte on nLeft,
                // format 2 spends two.
                int nLeftBytes = format == 1 ? 1 : 2;
                int entryBytes = 2 + nLeftBytes;

                int g = 1;
                while (g < glyphLimit && p + entryBytes - 1 < d.Length)
                {
                    int firstSid = (d[p] << 8) | d[p + 1];
                    int nLeft = format == 1 ? d[p + 2] : (d[p + 2] << 8) | d[p + 3];
                    p += entryBytes;

                    for (int i = 0; i <= nLeft && g < glyphLimit; i++, g++)
                        emit(g, firstSid + i);
                }
                return true;
            }

            return false;   // unknown format; the caller decides what that means
        }

        /// <summary>
        /// The first font's <c>ROS</c> (Top DICT 12 30: Registry and Ordering as SIDs, Supplement a
        /// number) — the character collection of a CID-keyed CFF; false for a font with none, or
        /// whose header, INDEXes or strings will not read.
        /// </summary>
        internal static bool TryReadRos(byte[] d, out string registry, out string ordering, out int supplement)
        {
            registry = ordering = null;
            supplement = 0;
            try
            {
                if (d == null || d.Length < 4 || d[0] != 1) return false;
                int pos = SkipIndex(d, d[2]);                      // the Name INDEX
                var topDicts = ReadIndex(d, ref pos);
                if (topDicts.Count == 0) return false;
                var strings = ReadIndex(d, ref pos);
                var topDict = ParseDict(topDicts[0]);
                if (!topDict.TryGetValue((12 << 8) | 30, out List<double> ros) || ros.Count < 3) return false;
                registry = SidToName(SaturatingCast.ToInt(ros[0]), strings);
                ordering = SidToName(SaturatingCast.ToInt(ros[1]), strings);
                supplement = SaturatingCast.ToInt(ros[2]);
                return registry != null && ordering != null;
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException || ex is ArgumentException || ex is OverflowException)
            {
                // A truncated INDEX or DICT: no collection read, which the caller says.
                return false;
            }
        }

        /// <summary>
        /// The names of the fonts a CFF font set holds, by their place in its Name INDEX (TN #5176
        /// §7) — null for an entry deleted (its first byte 0, an entry another reader
        /// also names) or lying outside the data — or null with
        /// <paramref name="reason"/> when the header or the Name INDEX will not read.
        /// </summary>
        internal static string[] TryReadFontNames(byte[] d, out string reason)
        {
            reason = null;
            if (d == null || d.Length < 4 || d[0] != 1 || d[2] < 4)
            {
                reason = "the data is no CFF 1 header";
                return null;
            }
            if (!TryLocateIndex(d, d[2], out int[] starts, out int[] lengths, out _) || starts.Length == 0)
            {
                reason = "its Name INDEX cannot be read";
                return null;
            }
            var names = new string[starts.Length];
            for (int k = 0; k < starts.Length; k++)
                if (starts[k] >= 0 && lengths[k] > 0 && d[starts[k]] != 0) names[k] = Text.Latin1Text.Decode(d, starts[k], lengths[k]);
            return names;
        }

        /// <summary>
        /// The font at <paramref name="index"/> of a CFF font set as a CFF holding it alone — an
        /// embedded CFF in PDF holds exactly one font (ISO 32000-2 §9.9) — or null with
        /// <paramref name="reason"/>. A set of one font is answered as it is.
        /// </summary>
        /// <remarks>
        /// Only the header and the four INDEXes that follow one another (TN #5176 Table 1) are read in
        /// sequence; everything after the Global Subr INDEX is reached by an offset. So the Name and Top
        /// DICT INDEXes are rewritten to the one font's entries, the String and Global Subr INDEXes
        /// copied, and the rest kept at the offsets it had, the gap the shorter INDEXes leave zeros: no
        /// offset in any DICT moves. The other fonts' data stays in the program, reached by nothing.
        /// A synthetic font (its Top DICT's <c>SyntheticBase</c>) draws through another font of the set
        /// and is refused. A font is chosen by its place in the Name INDEX, as FreeType's
        /// <c>cff_font_load</c> chooses a face.
        /// </remarks>
        internal static byte[] TryExtractFont(byte[] d, int index, out string reason)
        {
            reason = null;
            if (TryReadFontNames(d, out reason) is not string[] names) return null;
            if (index < 0 || index >= names.Length)
            {
                reason = $"the set holds {names.Length} fonts, and font {index} is none of them";
                return null;
            }
            if (names[index] == null)
            {
                reason = $"font {index}'s Name INDEX entry is deleted or outside the data";
                return null;
            }

            TryLocateIndex(d, d[2], out int[] nameStarts, out int[] nameLengths, out int namesEnd);
            // A Top DICT for each name, as FreeType's cff_font_load requires; one past them is reached by nothing.
            if (!TryLocateIndex(d, namesEnd, out int[] topStarts, out int[] topLengths, out int topEnd)
                || topStarts.Length < names.Length)
            {
                reason = "its Top DICT INDEX cannot be read, or holds fewer fonts than its Name INDEX names";
                return null;
            }
            if (topStarts[index] < 0)
            {
                reason = $"font {index}'s Top DICT lies outside the data";
                return null;
            }
            if (!TryLocateIndex(d, topEnd, out _, out _, out int stringsEnd)
                || !TryLocateIndex(d, stringsEnd, out _, out _, out int globalSubrsEnd))
            {
                reason = "its String or Global Subr INDEX cannot be read";
                return null;
            }

            var topDict = new byte[topLengths[index]];
            Array.Copy(d, topStarts[index], topDict, 0, topDict.Length);
            bool synthetic;
            try
            {
                synthetic = ParseDict(topDict).ContainsKey(OpSyntheticBase);
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException || ex is ArgumentException || ex is OverflowException)
            {
                reason = $"font {index}'s Top DICT cannot be read";
                return null;
            }
            if (synthetic)
            {
                reason = $"font {index} is a synthetic font (SyntheticBase), drawn through another font of the set";
                return null;
            }
            if (names.Length == 1 && topStarts.Length == 1) return d;

            using var single = new System.IO.MemoryStream(d.Length);
            single.Write(d, 0, d[2]);
            WriteOneItemIndex(single, d, nameStarts[index], nameLengths[index]);
            WriteOneItemIndex(single, d, topStarts[index], topLengths[index]);
            single.Write(d, topEnd, globalSubrsEnd - topEnd);
            if (single.Length > globalSubrsEnd)
            {
                reason = "its one-font INDEXes would not fit where the set's were";
                return null;
            }
            single.Position = globalSubrsEnd;   // the gap is written as zeros
            single.Write(d, globalSubrsEnd, d.Length - globalSubrsEnd);
            return single.ToArray();
        }

        /// <summary>Top DICT <c>SyntheticBase</c>, 12 20 (fontTools' <c>topDictOperators</c>; TN #5176).</summary>
        private const int OpSyntheticBase = (12 << 8) | 20;

        /// <summary>An INDEX of one item, <paramref name="length"/> bytes of <paramref name="d"/> from <paramref name="start"/>, its offsets as narrow as they fit.</summary>
        private static void WriteOneItemIndex(System.IO.Stream s, byte[] d, int start, int length)
        {
            int end = length + 1;
            int offSize = end <= 0xFF ? 1 : end <= 0xFFFF ? 2 : end <= 0xFFFFFF ? 3 : 4;
            s.WriteByte(0);
            s.WriteByte(1);
            s.WriteByte((byte)offSize);
            for (int b = offSize - 1; b >= 0; b--) s.WriteByte((byte)(1 >> (8 * b)));
            for (int b = offSize - 1; b >= 0; b--) s.WriteByte((byte)(end >> (8 * b)));
            s.Write(d, start, length);
        }

        /// <summary>
        /// Resolves a SID to a glyph name.  SIDs 0–390 use the predefined Adobe
        /// standard strings.  SIDs ≥ 391 use the CFF string INDEX (index = SID - 391).
        /// </summary>
        private static string SidToName(int sid, List<byte[]> stringIndex)
        {
            if (sid < StandardStrings.Length)
                return StandardStrings[sid];
            int idx = sid - 391;
            if (stringIndex != null && idx >= 0 && idx < stringIndex.Count && stringIndex[idx].Length > 0)
                return Text.Latin1Text.Decode(stringIndex[idx]);
            return null;
        }

        // ----------------------------------------------------------------
        //  INDEX parsing
        // ----------------------------------------------------------------

        /// <summary>
        /// Where each item of the INDEX at <paramref name="pos"/> <i>lives</i>, without copying it.
        /// </summary>
        /// <remarks>
        /// <para><b>This is the one place that knows the INDEX layout</b> (TN #5176 §5).
        /// <see cref="ReadIndex"/> is a copying wrapper over it, and <c>CffSubsetter</c> needs the
        /// offsets rather than the bytes — it overwrites unused charstrings <i>where they already
        /// are</i>, so a second walker that disagreed by one byte about <c>dataBase</c> would
        /// corrupt a neighbouring glyph rather than fail.</para>
        /// <para><paramref name="starts"/> holds <c>-1</c> for an item whose declared range falls
        /// outside the buffer, mirroring the empty item <see cref="ReadIndex"/> substitutes.</para>
        /// </remarks>
        /// <param name="endPos">
        /// Where the INDEX ends — and, on the failure paths, exactly how far the historical
        /// <see cref="ReadIndex"/> had advanced its <c>ref pos</c> when it gave up. The caller of a
        /// failed locate must adopt it, because the sequential header walk depends on that quirk.
        /// </param>
        /// <param name="d">The CFF blob.</param>
        /// <param name="pos">Offset of the INDEX header within <paramref name="d"/>.</param>
        /// <param name="starts">
        /// Absolute offset of each item, or <c>-1</c> where the declared range falls outside the
        /// buffer.
        /// </param>
        /// <param name="lengths">Byte length of each item, parallel to <paramref name="starts"/>.</param>
        internal static bool TryLocateIndex(
            byte[] d, int pos, out int[] starts, out int[] lengths, out int endPos)
        {
            starts = null;
            lengths = null;
            endPos = pos;

            if (d == null || pos < 0 || pos > d.Length - 2) return false;

            int count = (d[pos] << 8) | d[pos + 1];
            pos += 2;
            endPos = pos;
            if (count == 0)
            {
                starts = Array.Empty<int>();
                lengths = Array.Empty<int>();
                return true;
            }

            int offSize = d[pos++];
            endPos = pos;
            if (offSize < 1 || offSize > 4) return false;

            // Bounds-check the entire offset table before reading it (mirrors
            // SkipIndex). A truncated INDEX with a large declared count would
            // otherwise read past the end of the buffer.
            if (pos + (long)(count + 1) * offSize > d.Length) return false;

            int[] offsets = new int[count + 1];
            for (int i = 0; i <= count; i++)
            {
                int off = 0;
                for (int b = 0; b < offSize; b++)
                    off = (off << 8) | d[pos++];
                // The offsets must not descend (CFF §5: item i is offset[i] to offset[i+1]). A
                // descending pair made item i negative — skipped below — and let item i+1 start
                // back at the beginning of the data: offsets of 1, N, 1, N, … declared count/2
                // items of the whole data each, and ReadIndex copied them all, 32 GB from a 1 MB
                // INDEX. Ascending offsets make the items disjoint, so they total at most the data.
                if (i > 0 && off < offsets[i - 1]) return false;
                offsets[i] = off;
            }

            int dataBase = pos - 1; // offsets are 1-based; data starts at pos - 1
            starts = new int[count];
            lengths = new int[count];
            for (int i = 0; i < count; i++)
            {
                int start  = dataBase + offsets[i];
                int length = offsets[i + 1] - offsets[i];
                // The sum in long: in int, a hostile offset pair wrapped `start + length` negative,
                // passed, and ReadIndex then allocated the item — up to 2 GB — before the copy
                // failed. Parse's catch hid the throw, not the allocation.
                if (start < 0 || length < 0 || (long)start + length > d.Length)
                {
                    starts[i] = -1;
                    lengths[i] = 0;
                    continue;
                }
                starts[i] = start;
                lengths[i] = length;
            }

            endPos = dataBase + offsets[count];
            return true;
        }

        private static List<byte[]> ReadIndex(byte[] d, ref int pos)
        {
            if (!TryLocateIndex(d, pos, out int[] starts, out int[] lengths, out int endPos))
            {
                pos = endPos;
                return new List<byte[]>();
            }

            pos = endPos;
            var result = new List<byte[]>(starts.Length);
            for (int i = 0; i < starts.Length; i++)
            {
                if (starts[i] < 0) { result.Add(Array.Empty<byte>()); continue; }
                var item = new byte[lengths[i]];
                Array.Copy(d, starts[i], item, 0, lengths[i]);
                result.Add(item);
            }
            return result;
        }

        /// <summary>
        /// Locates every charstring in a <b>bare CFF1</b> blob by byte range, walking the same
        /// header sequence <see cref="ParseInternal"/> does.
        /// </summary>
        /// <param name="indexOffset">
        /// Where the CharStrings INDEX begins. <c>CffSubsetter</c> refuses to write below it, so a
        /// font whose offsets point back into the Top DICT cannot be made to corrupt itself.
        /// </param>
        /// <returns><c>false</c> with a <paramref name="reason"/> when the blob is not a CFF1 whose
        /// CharStrings INDEX can be found; never throws.</returns>
        /// <param name="cff">The bare CFF1 blob.</param>
        /// <param name="starts">Absolute offset of each charstring, by glyph index.</param>
        /// <param name="lengths">Byte length of each charstring, parallel to <paramref name="starts"/>.</param>
        /// <param name="reason">Why the walk gave up, or <c>null</c> on success.</param>
        internal static bool TryLocateCharStrings(
            byte[] cff, out int[] starts, out int[] lengths, out int indexOffset, out string reason)
        {
            starts = null;
            lengths = null;
            indexOffset = -1;
            reason = null;

            if (cff == null || cff.Length < 4) { reason = "the CFF data is empty or shorter than a header"; return false; }
            if (cff[0] == 2) { reason = "the program is CFF2 (a variable-font format), which is refused rather than rewritten"; return false; }
            if (cff[0] != 1) { reason = "unrecognised CFF major version " + cff[0].ToString(System.Globalization.CultureInfo.InvariantCulture); return false; }

            try
            {
                int pos = cff[2];                       // hdrSize
                if (pos < 4 || pos >= cff.Length) { reason = "the CFF header size is outside the data"; return false; }

                pos = SkipIndex(cff, pos);              // Name INDEX
                if (!TryLocateIndex(cff, pos, out int[] tdStarts, out int[] tdLengths, out _) ||
                    tdStarts.Length == 0 || tdStarts[0] < 0)
                {
                    reason = "the Top DICT INDEX could not be read";
                    return false;
                }

                var topDictBytes = new byte[tdLengths[0]];
                Array.Copy(cff, tdStarts[0], topDictBytes, 0, tdLengths[0]);
                var topDict = ParseDict(topDictBytes);

                int charStringsOffset = GetDictInt(topDict, 17, -1);
                if (charStringsOffset <= 0 || charStringsOffset >= cff.Length)
                {
                    reason = "the Top DICT names no CharStrings INDEX inside the data";
                    return false;
                }

                if (!TryLocateIndex(cff, charStringsOffset, out starts, out lengths, out _) || starts.Length == 0)
                {
                    reason = "the CharStrings INDEX could not be read";
                    starts = null;
                    lengths = null;
                    return false;
                }

                indexOffset = charStringsOffset;
                return true;
            }
            catch (Exception ex)
            {
                starts = null;
                lengths = null;
                indexOffset = -1;
                reason = "the CFF header walk failed (" + ex.Message + ")";
                return false;
            }
        }

        /// <summary>
        /// The Adobe StandardEncoding glyph name for a character code, which is how the deprecated
        /// <c>seac</c> form of <c>endchar</c> (TN #5177 §4.12 note J) names its two components —
        /// always through StandardEncoding, never through the font's own <c>/Encoding</c>.
        /// </summary>
        internal static bool TryGetStandardEncodingGlyphName(int code, out string name)
        {
            name = null;
            if (!StandardEncodingSidMap.TryGetValue(code, out int sid)) return false;
            if (sid <= 0 || sid >= StandardStrings.Length) return false;
            name = StandardStrings[sid];
            return true;
        }

        private static int SkipIndex(byte[] d, int pos)
        {
            if (pos + 2 > d.Length) return pos;
            int count = (d[pos] << 8) | d[pos + 1];
            pos += 2;
            if (count == 0) return pos;
            int offSize = d[pos++];
            if (offSize < 1 || offSize > 4) return pos;
            // Read last offset to skip to end of data.
            int offsetsSize = (count + 1) * offSize;
            if (pos + offsetsSize > d.Length) return d.Length;
            // Last offset is at pos + count * offSize
            int lastOffPos = pos + count * offSize;
            long lastOff = 0;
            for (int b = 0; b < offSize; b++)
                lastOff = (lastOff << 8) | d[lastOffPos + b];
            pos += offsetsSize;
            // Offsets are 1-based. In long, and clamped to the data: as an int, a four-byte last
            // offset with its top bit set was negative, and the walk stepped backwards onto
            // whatever bytes lay there, reading them as the next INDEX.
            long end = pos + lastOff - 1;
            return end < 0 || end > d.Length ? d.Length : (int)end;
        }

        // ----------------------------------------------------------------
        //  DICT parsing
        // ----------------------------------------------------------------

        /// <summary>
        /// Parses a CFF DICT into a map of operator → list of operands.
        /// Key is the operator byte (single) or (12 &lt;&lt; 8 | b2) for escapes.
        /// </summary>
        internal static Dictionary<int, List<double>> ParseDict(byte[] d)
        {
            var dict = new Dictionary<int, List<double>>();
            var stack = new List<double>();
            int pos = 0;

            while (pos < d.Length)
            {
                byte b0 = d[pos++];

                if (b0 == 29)
                {
                    // 4-byte int
                    if (pos + 3 >= d.Length) break;
                    int val = (sbyte)d[pos] << 24 | d[pos+1] << 16 | d[pos+2] << 8 | d[pos+3];
                    pos += 4;
                    stack.Add(val);
                }
                else if (b0 == 28)
                {
                    // 2-byte int (signed)
                    if (pos + 1 >= d.Length) break;
                    short val = (short)((d[pos] << 8) | d[pos+1]);
                    pos += 2;
                    stack.Add(val);
                }
                else if (b0 == 30)
                {
                    // BCD real number
                    stack.Add(ReadBcdReal(d, ref pos));
                }
                else if (b0 >= 32 && b0 <= 246)
                {
                    stack.Add(b0 - 139);
                }
                else if (b0 >= 247 && b0 <= 250)
                {
                    if (pos >= d.Length) break;
                    stack.Add((b0 - 247) * 256 + d[pos++] + 108);
                }
                else if (b0 >= 251 && b0 <= 254)
                {
                    if (pos >= d.Length) break;
                    stack.Add(-((b0 - 251) * 256) - d[pos++] - 108);
                }
                else if (b0 == 12)
                {
                    // Two-byte operator
                    if (pos >= d.Length) break;
                    int op = (12 << 8) | d[pos++];
                    dict[op] = new List<double>(stack);
                    stack.Clear();
                }
                else if (b0 <= 27)
                {
                    // Single-byte operator.  CFF1 uses 1-21; CFF2 extends to 24 (VariationStore).
                    // Bytes 22-27 are reserved in CFF1 but valid operators in CFF2.
                    dict[b0] = new List<double>(stack);
                    stack.Clear();
                }
                // 28-31 are numeric encodings handled above
            }

            return dict;
        }

        private static double ReadBcdReal(byte[] d, ref int pos)
        {
            // BCD encoded real: nibbles until 0xf terminator
            var sb = new System.Text.StringBuilder();
            while (pos < d.Length)
            {
                byte b = d[pos++];
                int hi = (b >> 4) & 0xF;
                int lo = b & 0xF;
                if (!AppendBcdNibble(sb, hi)) break;
                if (!AppendBcdNibble(sb, lo)) break;
            }
            return double.TryParse(sb.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double v) ? v : 0.0;
        }

        private static bool AppendBcdNibble(System.Text.StringBuilder sb, int nibble)
        {
            switch (nibble)
            {
                case 0xA: sb.Append('.'); return true;
                case 0xB: sb.Append('E'); return true;
                case 0xC: sb.Append("E-"); return true;
                case 0xD: return true; // reserved, skip
                case 0xE: sb.Append('-'); return true;
                case 0xF: return false; // terminator
                default:  sb.Append((char)('0' + nibble)); return true;
            }
        }

        // ----------------------------------------------------------------
        //  DICT helpers
        // ----------------------------------------------------------------

        internal static int GetDictInt(Dictionary<int, List<double>> dict, int key, int defaultVal)
        {
            if (!dict.TryGetValue(key, out var vals) || vals.Count == 0) return defaultVal;
            return SaturatingCast.ToInt(vals[vals.Count - 1]);
        }

        private static float GetDictFloat(Dictionary<int, List<double>> dict, int key, float defaultVal)
        {
            if (!dict.TryGetValue(key, out var vals) || vals.Count == 0) return defaultVal;
            return (float)vals[vals.Count - 1];
        }

        /// <summary>
        /// Returns the uniform glyph-space → text-space scale from the Top DICT /FontMatrix
        /// (operator 12 7, element [0]).  Defaults to 0.001 (a 1000-unit em) when absent or
        /// outside a sane range, so a corrupt matrix can't distort glyph sizes.
        /// </summary>
        private static float GetFontMatrixScale(Dictionary<int, List<double>> topDict)
        {
            if (topDict.TryGetValue(OpFontMatrix, out var fm) && fm.Count >= 1 && IsAcceptedMatrixScale(fm[0]))
                return (float)fm[0];
            return 0.001f;
        }

        private const int OpFontMatrix = (12 << 8) | 7;

        private static bool IsAcceptedMatrixScale(double sx)
            => !double.IsNaN(sx) && !double.IsInfinity(sx) && sx > 1e-6 && sx <= 0.1;

        /// <summary>
        /// Whether <see cref="GetFontMatrixScale"/>'s answer is the scale a non-CID font declares:
        /// true with no Top DICT <c>FontMatrix</c>, or one whose scale is accepted; false when a
        /// declared matrix was rejected and the 0.001 default stands in for it.
        /// </summary>
        private static bool TopMatrixScaleIsDeclared(Dictionary<int, List<double>> topDict)
            => !topDict.TryGetValue(OpFontMatrix, out var fm) || (fm.Count >= 1 && IsAcceptedMatrixScale(fm[0]));

        /// <summary>
        /// For a CID-keyed font, whether every Font DICT's effective horizontal scale equals
        /// <paramref name="scale"/>. A Font DICT's own <c>FontMatrix</c> is concatenated with the Top
        /// DICT's (TN #5176's CIDFont rule, as font engines apply it: an absent Top DICT matrix
        /// contributes nothing); without one, the Top DICT's matrix or the 0.001 default applies.
        /// </summary>
        private static bool CidMatrixScaleIsUniform(
            Dictionary<int, List<double>> topDict, List<Dictionary<int, List<double>>> fdDicts, float scale)
        {
            bool topDeclared = topDict.TryGetValue(OpFontMatrix, out var top);
            if (topDeclared && top.Count == 0) return false;

            foreach (var fdDict in fdDicts)
            {
                double effective;
                if (fdDict.TryGetValue(OpFontMatrix, out var own))
                {
                    if (own.Count == 0) return false;
                    effective = (topDeclared ? top[0] : 1.0) * own[0];
                }
                else
                {
                    effective = topDeclared ? top[0] : 0.001;
                }
                if (double.IsNaN(effective) || Math.Abs(effective - scale) > scale * 1e-6) return false;
            }
            return true;
        }

        // ----------------------------------------------------------------
        //  CID-keyed CFF (FDArray / FDSelect)  — ISO 32000-1 §9.7.4
        // ----------------------------------------------------------------

        /// <summary>
        /// Parses a CID-keyed CFF font (one that has an /ROS entry in the Top DICT).
        /// Reads the FDArray (array of Font DICTs, each with its own Private DICT)
        /// and FDSelect (GID → FD index mapping) so each glyph's charstring is
        /// evaluated with the correct local subroutines and width defaults.
        /// </summary>
        private static CffFont ParseCidKeyedCff(
            byte[] d,
            Dictionary<int, List<double>> topDict,
            int charStringsOffset,
            int charsetVal,
            List<byte[]> globalSubrs,
            int opFdArray,
            int opFdSelect)
        {
            // CharStrings INDEX
            int p = charStringsOffset;
            var charstrings = ReadIndex(d, ref p);
            int glyphCount  = charstrings.Count;
            if (glyphCount == 0) { _lastFailure = CffFailure.Malformed; return null; }

            // ---- FDArray ----
            int fdArrayOffset = GetDictInt(topDict, opFdArray, -1);
            if (fdArrayOffset < 0 || fdArrayOffset >= d.Length) { _lastFailure = CffFailure.Malformed; return null; }
            p = fdArrayOffset;
            var fdDicts = ReadIndex(d, ref p);
            int fdCount = fdDicts.Count;
            if (fdCount == 0) { _lastFailure = CffFailure.Malformed; return null; }

            // Parse each FD's Private DICT → (defaultW, nominalW, localSubrs). A Private DICT, and a Subrs
            // INDEX, that several Font DICTs name is read once and its list shared: 65 535 Font DICTs naming
            // one megabyte of Subrs would otherwise hold 65 535 copies of it at once. What distinct ones
            // copy is bounded by the program's length each, as a font's own never overlap; past it the
            // font is refused.
            var fdPrivate = new (float defW, float nomW, List<byte[]> subrs)[fdCount];
            var parsedFdDicts = new List<Dictionary<int, List<double>>>(fdCount);
            var privatesRead = new Dictionary<long, (float defW, float nomW, List<byte[]> subrs)>();
            var subrsRead = new Dictionary<int, List<byte[]>>();
            var noSubrs = new List<byte[]>();
            long privateBytes = 0, subrsBytes = 0;
            for (int fd = 0; fd < fdCount; fd++)
            {
                var fdDict = ParseDict(fdDicts[fd]);
                parsedFdDicts.Add(fdDict);
                (float defW, float nomW, List<byte[]> subrs) entry = (0f, 0f, noSubrs);
                if (fdDict.TryGetValue(18, out var privArr) && privArr.Count == 2)
                {
                    int privSize   = SaturatingCast.ToInt(privArr[0]);
                    int privOffset = SaturatingCast.ToInt(privArr[1]);
                    long key = ((long)privOffset << 32) | (uint)privSize;
                    if (privOffset > 0 && privSize > 0 && (long)privOffset + privSize <= d.Length
                        && !privatesRead.TryGetValue(key, out entry))
                    {
                        privateBytes += privSize;
                        if (privateBytes > d.Length) { _lastFailure = CffFailure.Malformed; return null; }
                        var privData = new byte[privSize];
                        Array.Copy(d, privOffset, privData, 0, privSize);
                        var privDict = ParseDict(privData);
                        float defW = GetDictFloat(privDict, 20, 0f);
                        float nomW = GetDictFloat(privDict, 21, 0f);
                        List<byte[]> subrs = noSubrs;
                        int subrsRel = GetDictInt(privDict, 19, -1);
                        if (subrsRel > 0)
                        {
                            long absSubrs = (long)privOffset + subrsRel;
                            if (absSubrs >= 0 && absSubrs < d.Length && !subrsRead.TryGetValue((int)absSubrs, out subrs))
                            {
                                int sp = (int)absSubrs;
                                subrs = new List<byte[]>(ReadIndex(d, ref sp));
                                foreach (byte[] subr in subrs) subrsBytes += subr.Length;
                                if (subrsBytes > d.Length) { _lastFailure = CffFailure.Malformed; return null; }
                                subrsRead[(int)absSubrs] = subrs;
                            }
                        }
                        entry = (defW, nomW, subrs);
                        privatesRead[key] = entry;
                    }
                }
                fdPrivate[fd] = entry;
            }

            // ---- FDSelect ----
            int[] gidToFd = new int[glyphCount]; // default: FD 0
            int fdSelectOffset = GetDictInt(topDict, opFdSelect, -1);
            if (fdSelectOffset >= 0 && fdSelectOffset < d.Length)
                ParseFdSelect(d, fdSelectOffset, glyphCount, gidToFd);
            // An FD index past the FDArray names no Private DICT. It is clamped to the last one, once,
            // here: the per-glyph branch below clamped and the shared one indexed with it, so an
            // FDSelect naming FD 1 of a one-entry FDArray threw and lost the whole font.
            for (int g = 0; g < glyphCount; g++)
                if (gidToFd[g] >= fdCount) gidToFd[g] = fdCount - 1;

            // ---- Determine if all glyphs share the same FD ----
            bool singleFd = fdCount == 1;
            if (!singleFd)
            {
                singleFd = true;
                for (int g = 1; g < glyphCount; g++)
                    if (gidToFd[g] != gidToFd[0]) { singleFd = false; break; }
            }

            List<byte[]> sharedSubrs;
            float sharedDefW, sharedNomW;
            IReadOnlyList<byte[]>[] localSubrsPerGid = null;
            float[] defWPerGid = null, nomWPerGid = null;

            if (singleFd)
            {
                int fd0 = gidToFd[0];
                sharedSubrs = fdPrivate[fd0].subrs;
                sharedDefW  = fdPrivate[fd0].defW;
                sharedNomW  = fdPrivate[fd0].nomW;
            }
            else
            {
                // Multiple FDs — store per-GID private data.
                sharedSubrs = new List<byte[]>();
                sharedDefW  = fdPrivate[0].defW;
                sharedNomW  = fdPrivate[0].nomW;
                localSubrsPerGid = new IReadOnlyList<byte[]>[glyphCount];
                defWPerGid       = new float[glyphCount];
                nomWPerGid       = new float[glyphCount];
                for (int g = 0; g < glyphCount; g++)
                {
                    int fd = gidToFd[g];
                    localSubrsPerGid[g] = fdPrivate[fd].subrs;
                    defWPerGid[g]       = fdPrivate[fd].defW;
                    nomWPerGid[g]       = fdPrivate[fd].nomW;
                }
            }

            // ---- CID → GID mapping from charset (inverted) ----
            var charCodeToGid = BuildCidCharCodeToGid(d, charsetVal, glyphCount);

            float scale = GetFontMatrixScale(topDict);
            return new CffFont(
                charstrings, sharedSubrs, globalSubrs, charCodeToGid,
                sharedDefW, sharedNomW,
                glyphNameToGid: null,
                isCff2: false,
                blendRegionCount: 0,
                isCidKeyed: true,
                localSubrsPerGid: localSubrsPerGid,
                defaultWidthsPerGid: defWPerGid,
                nominalWidthsPerGid: nomWPerGid,
                fontMatrixScale: scale,
                widthScaleIsKnown: CidMatrixScaleIsUniform(topDict, parsedFdDicts, scale));
        }

        /// <summary>
        /// Parses a CFF FDSelect structure and fills <paramref name="gidToFd"/> (one entry per GID);
        /// returns the entries it stored, the work a test reads.
        /// </summary>
        /// <remarks>
        /// A format 3 table's ranges ascend (Technical Note #5176, §19: the first at 0, each later one,
        /// and the sentinel, past the one before), so each range is filled from where the last one ended
        /// and the fills total at most the glyph count. A range that does not ascend ends the table: a
        /// file's 65 535 ranges alternating between 0 and the last glyph would otherwise refill the map once
        /// a range.
        /// </remarks>
        internal static long ParseFdSelect(byte[] d, int offset, int glyphCount, int[] gidToFd)
        {
            long stores = 0;
            if (offset >= d.Length) return stores;
            int format = d[offset];
            if (format == 0)
            {
                // One byte per GID
                for (int g = 0; g < glyphCount && offset + 1 + g < d.Length; g++, stores++)
                    gidToFd[g] = d[offset + 1 + g];
            }
            else if (format == 3)
            {
                if (offset + 3 > d.Length) return stores;
                int nRanges = (d[offset + 1] << 8) | d[offset + 2];
                int p = offset + 3;
                // Each Range3 = (firstGID uint16, fd uint8)
                // The sentinel (last firstGID) terminates the final range.
                int prevFirst = -1, prevFd = 0;
                for (int r = 0; r <= nRanges && p + 1 < d.Length; r++)
                {
                    int first = (d[p] << 8) | d[p + 1];
                    p += 2;
                    if (first <= prevFirst) break;
                    if (r < nRanges)
                    {
                        if (p >= d.Length) break;
                        int fd = d[p++];
                        if (prevFirst >= 0)
                        {
                            // Fill [prevFirst, first) with prevFd
                            for (int g = prevFirst; g < first && g < glyphCount; g++, stores++)
                                gidToFd[g] = prevFd;
                        }
                        prevFirst = first;
                        prevFd    = fd;
                    }
                    else
                    {
                        // Sentinel — fill [prevFirst, first) with prevFd
                        for (int g = prevFirst; g < first && g < glyphCount; g++, stores++)
                            gidToFd[g] = prevFd;
                    }
                }
            }
            return stores;
        }

        /// <summary>
        /// Builds a CID → GID map from the CFF charset for a CID-keyed font.
        /// The charset in a CID font maps GID → CID; we invert it.
        /// Falls back to identity (CID == GID) when the charset is a predefined id
        /// or cannot be parsed.
        /// </summary>
        private static Dictionary<int, int> BuildCidCharCodeToGid(byte[] d, int charsetVal, int glyphCount)
        {
            var cidToGid  = new Dictionary<int, int>();
            int glyphLim  = Math.Min(glyphCount, MaxGlyphs);

            if (charsetVal < 4)
            {
                // Predefined or no charset — identity
                for (int g = 0; g < glyphLim; g++) cidToGid[g] = g;
                return cidToGid;
            }
            cidToGid[0] = 0; // GID 0 (.notdef) → CID 0

            // The same §13 walk as the two SID builders, keyed by CID. A charset this cannot walk —
            // an offset past the end, or a format byte that is not 0, 1 or 2 — falls back to the
            // identity, which is this caller's own rule and not the walk's: a CID-keyed font with
            // an unreadable charset is better served by "CID == GID" than by an empty map.
            if (!TryWalkCharset(d, charsetVal, glyphCount, (gid, cid) => cidToGid[cid] = gid))
            {
                for (int g = 0; g < glyphLim; g++) cidToGid[g] = g;
            }

            return cidToGid;
        }

        // ----------------------------------------------------------------
        //  SFNT table extraction (OTF container support)
        // ----------------------------------------------------------------

        /// <summary>
        /// Extracts the raw CFF or CFF2 table bytes from an 'OTTO'-signature SFNT container.
        /// Returns null if the container is malformed or contains no CFF table.
        /// </summary>
        private static byte[] ExtractCffTableFromSfnt(byte[] d)
        {
            if (d.Length < 12) return null;
            int numTables = (d[4] << 8) | d[5];
            int dirBase   = 12;

            for (int i = 0; i < numTables; i++)
            {
                int entry = dirBase + i * 16;
                if (entry + 16 > d.Length) break;

                // Table tag (4 ASCII bytes): look for "CFF " or "CFF2"
                bool isCff  = d[entry]=='C' && d[entry+1]=='F' && d[entry+2]=='F' && d[entry+3]==' ';
                bool isCff2 = d[entry]=='C' && d[entry+1]=='F' && d[entry+2]=='F' && d[entry+3]=='2';
                if (!isCff && !isCff2) continue;

                uint offset = ((uint)d[entry+ 8] << 24) | ((uint)d[entry+ 9] << 16)
                            | ((uint)d[entry+10] <<  8) |  (uint)d[entry+11];
                uint length = ((uint)d[entry+12] << 24) | ((uint)d[entry+13] << 16)
                            | ((uint)d[entry+14] <<  8) |  (uint)d[entry+15];

                // Widened to long deliberately. In uint arithmetic `offset + length` WRAPS: an
                // entry declaring offset 0xFFFFFFF0 and length 0x20 sums to 0x10, passes a bounds
                // test against any real file, and then `(int)offset` is -16 and Array.Copy throws
                // ArgumentOutOfRangeException. That would escape — this runs outside Parse's try/catch —
                // out of a documented bool-plus-reason API (PdfFontSubsetter.TryMapCodePointsToCffGlyphs),
                // whose callers test for null and read LastFailureReason rather than catching.
                // SfntTablePruner and CffSubsetter make the same test in long.
                if (length == 0 || (long)offset + length > d.Length) return null;
                var cffData = new byte[length];
                Array.Copy(d, (int)offset, cffData, 0, (int)length);
                return cffData;
            }
            return null;
        }

        // ----------------------------------------------------------------
        //  Standard Encoding SID table (ISO 8859-1 / Adobe Standard)
        //  Maps char code → SID for the predefined Standard Encoding.
        //  Only the non-zero entries are listed; .notdef = 0 for all others.
        // ----------------------------------------------------------------

        private static readonly Dictionary<int, int> StandardEncodingSidMap =
            new Dictionary<int, int>
            {
                {32,  1}, {33,  2}, {34,  3}, {35,  4}, {36,  5}, {37,  6}, {38,  7},
                {39,  8}, {40,  9}, {41, 10}, {42, 11}, {43, 12}, {44, 13}, {45, 14},
                {46, 15}, {47, 16}, {48, 17}, {49, 18}, {50, 19}, {51, 20}, {52, 21},
                {53, 22}, {54, 23}, {55, 24}, {56, 25}, {57, 26}, {58, 27}, {59, 28},
                {60, 29}, {61, 30}, {62, 31}, {63, 32}, {64, 33}, {65, 34}, {66, 35},
                {67, 36}, {68, 37}, {69, 38}, {70, 39}, {71, 40}, {72, 41}, {73, 42},
                {74, 43}, {75, 44}, {76, 45}, {77, 46}, {78, 47}, {79, 48}, {80, 49},
                {81, 50}, {82, 51}, {83, 52}, {84, 53}, {85, 54}, {86, 55}, {87, 56},
                {88, 57}, {89, 58}, {90, 59}, {91, 60}, {92, 61}, {93, 62}, {94, 63},
                {95, 64}, {96, 65}, {97, 66}, {98, 67}, {99, 68},{100, 69},{101, 70},
                {102, 71},{103, 72},{104, 73},{105, 74},{106, 75},{107, 76},{108, 77},
                {109, 78},{110, 79},{111, 80},{112, 81},{113, 82},{114, 83},{115, 84},
                {116, 85},{117, 86},{118, 87},{119, 88},{120, 89},{121, 90},{122, 91},
                {123, 92},{124, 93},{125, 94},{126, 95},
                {161,96},{162,97},{163,98},{164,99},{165,100},
                {166,101},{167,102},{168,103},{169,104},{170,105},
                {171,106},{172,107},{173,108},{174,109},{175,110},
                {177,111},{178,112},{179,113},{180,114},{182,115},
                {183,116},{184,117},{185,118},{186,119},{187,120},
                {188,121},{189,122},{191,123},{193,124},{194,125},
                {195,126},{196,127},{197,128},{198,129},{199,130},
                {200,131},{202,132},{203,133},{205,134},{206,135},
                {207,136},{208,137},{225,138},{227,139},{232,140},
                {233,141},{234,142},{235,143},{241,144},{245,145},
                {248,146},{249,147},{250,148},{251,149},
            };

        // ----------------------------------------------------------------
        //  Adobe CFF standard string table (TN #5176 Appendix A, SIDs 0–390)
        // ----------------------------------------------------------------

        /// <summary>The 391 predefined SIDs of CFF Appendix A.</summary>
        /// <remarks>
        /// <para><b>Internal rather than private because <c>CffFontWriter</c> needs the same
        /// list read the other way round</b> — name to SID, where this reads SID to name.
        /// A writer carrying its own copy would be 391 strings that agree until one of them
        /// is corrected.</para>
        /// <para><b>Checked against two implementations, string for string</b>: two independent
        /// implementations' standard-string tables, which agree on all 391. A list lacking <c>ydieresis</c> (SID 227) and ending with
        /// a <c>Euro</c> no table holds would read SIDs 227–390 each as their successor: the ISOAdobe
        /// charset would name its glyph 228 <c>exclamsmall</c>, and the writer give <c>zcaron</c> the
        /// SID every other reader takes for <c>ydieresis</c> (<c>CffPredefinedCharsetTests</c>).</para>
        /// </remarks>
        internal static readonly string[] StandardStrings =
        {
            // 0–9
            ".notdef","space","exclam","quotedbl","numbersign","dollar","percent",
            "ampersand","quoteright","parenleft",
            // 10–19
            "parenright","asterisk","plus","comma","hyphen","period","slash",
            "zero","one","two",
            // 20–29
            "three","four","five","six","seven","eight","nine","colon","semicolon",
            "less",
            // 30–39
            "equal","greater","question","at","A","B","C","D","E","F",
            // 40–49
            "G","H","I","J","K","L","M","N","O","P",
            // 50–59
            "Q","R","S","T","U","V","W","X","Y","Z",
            // 60–69
            "bracketleft","backslash","bracketright","asciicircum","underscore",
            "quoteleft","a","b","c","d",
            // 70–79
            "e","f","g","h","i","j","k","l","m","n",
            // 80–89
            "o","p","q","r","s","t","u","v","w","x",
            // 90–99
            "y","z","braceleft","bar","braceright","asciitilde","exclamdown",
            "cent","sterling","fraction",
            // 100–109
            "yen","florin","section","currency","quotesingle","quotedblleft",
            "guillemotleft","guilsinglleft","guilsinglright","fi",
            // 110–119
            "fl","endash","dagger","daggerdbl","periodcentered","paragraph",
            "bullet","quotesinglbase","quotedblbase","quotedblright",
            // 120–129
            "guillemotright","ellipsis","perthousand","questiondown","grave",
            "acute","circumflex","tilde","macron","breve",
            // 130–139
            "dotaccent","dieresis","ring","cedilla","hungarumlaut","ogonek",
            "caron","emdash","AE","ordfeminine",
            // 140–149
            "Lslash","Oslash","OE","ordmasculine","ae","dotlessi","lslash",
            "oslash","oe","germandbls",
            // 150–159
            "onesuperior","logicalnot","mu","trademark","Eth","onehalf",
            "plusminus","Thorn","onequarter","divide",
            // 160–169
            "brokenbar","degree","thorn","threequarters","twosuperior","registered",
            "minus","eth","multiply","threesuperior",
            // 170–179
            "copyright","Aacute","Acircumflex","Adieresis","Agrave","Aring",
            "Atilde","Ccedilla","Eacute","Ecircumflex",
            // 180–189
            "Edieresis","Egrave","Iacute","Icircumflex","Idieresis","Igrave",
            "Ntilde","Oacute","Ocircumflex","Odieresis",
            // 190–199
            "Ograve","Otilde","Scaron","Uacute","Ucircumflex","Udieresis",
            "Ugrave","Yacute","Ydieresis","Zcaron",
            // 200–209
            "aacute","acircumflex","adieresis","agrave","aring","atilde",
            "ccedilla","eacute","ecircumflex","edieresis",
            // 210–219
            "egrave","iacute","icircumflex","idieresis","igrave","ntilde",
            "oacute","ocircumflex","odieresis","ograve",
            // 220–229
            "otilde","scaron","uacute","ucircumflex","udieresis","ugrave","yacute","ydieresis",
            "zcaron","exclamsmall",
            // 230–239
            "Hungarumlautsmall","dollaroldstyle","dollarsuperior","ampersandsmall","Acutesmall",
            "parenleftsuperior","parenrightsuperior","twodotenleader","onedotenleader",
            "zerooldstyle",
            // 240–249
            "oneoldstyle","twooldstyle","threeoldstyle","fouroldstyle","fiveoldstyle",
            "sixoldstyle","sevenoldstyle","eightoldstyle","nineoldstyle","commasuperior",
            // 250–259
            "threequartersemdash","periodsuperior","questionsmall","asuperior","bsuperior",
            "centsuperior","dsuperior","esuperior","isuperior","lsuperior",
            // 260–269
            "msuperior","nsuperior","osuperior","rsuperior","ssuperior","tsuperior","ff","ffi",
            "ffl","parenleftinferior",
            // 270–279
            "parenrightinferior","Circumflexsmall","hyphensuperior","Gravesmall","Asmall",
            "Bsmall","Csmall","Dsmall","Esmall","Fsmall",
            // 280–289
            "Gsmall","Hsmall","Ismall","Jsmall","Ksmall","Lsmall","Msmall","Nsmall","Osmall",
            "Psmall",
            // 290–299
            "Qsmall","Rsmall","Ssmall","Tsmall","Usmall","Vsmall","Wsmall","Xsmall","Ysmall",
            "Zsmall",
            // 300–309
            "colonmonetary","onefitted","rupiah","Tildesmall","exclamdownsmall","centoldstyle",
            "Lslashsmall","Scaronsmall","Zcaronsmall","Dieresissmall",
            // 310–319
            "Brevesmall","Caronsmall","Dotaccentsmall","Macronsmall","figuredash",
            "hypheninferior","Ogoneksmall","Ringsmall","Cedillasmall","questiondownsmall",
            // 320–329
            "oneeighth","threeeighths","fiveeighths","seveneighths","onethird","twothirds",
            "zerosuperior","foursuperior","fivesuperior","sixsuperior",
            // 330–339
            "sevensuperior","eightsuperior","ninesuperior","zeroinferior","oneinferior",
            "twoinferior","threeinferior","fourinferior","fiveinferior","sixinferior",
            // 340–349
            "seveninferior","eightinferior","nineinferior","centinferior","dollarinferior",
            "periodinferior","commainferior","Agravesmall","Aacutesmall","Acircumflexsmall",
            // 350–359
            "Atildesmall","Adieresissmall","Aringsmall","AEsmall","Ccedillasmall","Egravesmall",
            "Eacutesmall","Ecircumflexsmall","Edieresissmall","Igravesmall",
            // 360–369
            "Iacutesmall","Icircumflexsmall","Idieresissmall","Ethsmall","Ntildesmall",
            "Ogravesmall","Oacutesmall","Ocircumflexsmall","Otildesmall","Odieresissmall",
            // 370–379
            "OEsmall","Oslashsmall","Ugravesmall","Uacutesmall","Ucircumflexsmall",
            "Udieresissmall","Yacutesmall","Thornsmall","Ydieresissmall","001.000",
            // 380–390
            "001.001","001.002","001.003","Black","Bold","Book","Light","Medium","Regular",
            "Roman","Semibold",
        };
    }
}
