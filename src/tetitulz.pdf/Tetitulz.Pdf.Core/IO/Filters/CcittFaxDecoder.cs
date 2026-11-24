// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// Pure-managed CCITT T.4 (Group 3) and T.6 (Group 4) fax decoder.
    /// Produces a packed 1-bit-per-pixel bitmap, MSB first, row-major.
    ///
    /// Internal convention:
    ///   - Bitmap "1" bit means BLACK while we are decoding (matches T.6 changing-
    ///     element coding, where the line starts in white and colour flips on each
    ///     change).  The caller applies /BlackIs1 to pick the PDF surface convention.
    ///
    /// Not covered (safe fallback to raw bytes):
    ///   - T.4 2D mixed mode (/K &gt; 0)  — we treat the first tag bit per line but
    ///     can't always recover from damaged rows; fall back to G3-1D of each line
    ///     when /EncodedByteAlign aligns boundaries.
    ///   - Corrupt streams — any decode exception bubbles up to the filter wrapper
    ///     which catches it and returns the raw data so callers still get something.
    /// </summary>
    internal static class CcittFaxDecoder
    {
        // ------------------------------------------------------------------
        //  Bit reader: MsbBitStream, in this folder. The nested copy here and
        //  Jbig2Decoder's were the same three fields and a byte-identical ReadBit;
        //  each had added members the other lacked, which is what kept the shared
        //  primitive from being noticed.
        // ------------------------------------------------------------------

        // ------------------------------------------------------------------
        //  Huffman tables (ITU-T T.4 Annex A)
        // ------------------------------------------------------------------
        //
        // Keyed as `(length << 16) | code`; value is `(run, isMakeup)`. The builder lists each
        // entry by its literal bit pattern so the data stays auditable against the spec tables.
        //
        // BUILT AT TYPE INITIALISATION, NOT LAZILY, and that is a correctness requirement rather
        // than a preference -- the same one recorded on `GaloisField256`. The previous version was
        // double-checked locking with the check on the wrong side of the fill:
        //
        //     if (_whiteTable != null) return;        // read outside the lock
        //     lock (_tableLock) {
        //         _whiteTable = new Dictionary<...>;  // PUBLISHED EMPTY
        //         AddW(...);                          // filled afterwards
        //
        // so a second thread could see the non-null empty dictionary, skip the lock and decode
        // against missing run lengths -- and since `_blackTable` was assigned later still, it could
        // also see that one null. It is not only a test-ordering problem: `RenderAllPagesCore`
        // renders pages on parallel workers, and a CCITT image decoded on two of them at once is
        // exactly this race. The CLR guarantees a type initialiser runs once, before any access.

        private static readonly Dictionary<int, (int run, bool isMakeup)> _whiteTable
            = new Dictionary<int, (int, bool)>(107);

        private static readonly Dictionary<int, (int run, bool isMakeup)> _blackTable
            = new Dictionary<int, (int, bool)>(107);

        static CcittFaxDecoder()
        {
            // ---- White terminating codes (T.4 Table 2a) ----
            // Format: (run, "bit-string")
            AddW(0, "00110101");     AddW(1, "000111");       AddW(2, "0111");
            AddW(3, "1000");         AddW(4, "1011");         AddW(5, "1100");
            AddW(6, "1110");         AddW(7, "1111");         AddW(8, "10011");
            AddW(9, "10100");        AddW(10, "00111");       AddW(11, "01000");
            AddW(12, "001000");      AddW(13, "000011");      AddW(14, "110100");
            AddW(15, "110101");      AddW(16, "101010");      AddW(17, "101011");
            AddW(18, "0100111");     AddW(19, "0001100");     AddW(20, "0001000");
            AddW(21, "0010111");     AddW(22, "0000011");     AddW(23, "0000100");
            AddW(24, "0101000");     AddW(25, "0101011");     AddW(26, "0010011");
            AddW(27, "0100100");     AddW(28, "0011000");     AddW(29, "00000010");
            AddW(30, "00000011");    AddW(31, "00011010");    AddW(32, "00011011");
            AddW(33, "00010010");    AddW(34, "00010011");    AddW(35, "00010100");
            AddW(36, "00010101");    AddW(37, "00010110");    AddW(38, "00010111");
            AddW(39, "00101000");    AddW(40, "00101001");    AddW(41, "00101010");
            AddW(42, "00101011");    AddW(43, "00101100");    AddW(44, "00101101");
            AddW(45, "00000100");    AddW(46, "00000101");    AddW(47, "00001010");
            AddW(48, "00001011");    AddW(49, "01010010");    AddW(50, "01010011");
            AddW(51, "01010100");    AddW(52, "01010101");    AddW(53, "00100100");
            AddW(54, "00100101");    AddW(55, "01011000");    AddW(56, "01011001");
            AddW(57, "01011010");    AddW(58, "01011011");    AddW(59, "01001010");
            AddW(60, "01001011");    AddW(61, "00110010");    AddW(62, "00110011");
            AddW(63, "00110100");

            // ---- White makeup codes (T.4 Table 3, 64-1728 step 64) ----
            AddWMakeup(64,   "11011");      AddWMakeup(128,  "10010");
            AddWMakeup(192,  "010111");     AddWMakeup(256,  "0110111");
            AddWMakeup(320,  "00110110");   AddWMakeup(384,  "00110111");
            AddWMakeup(448,  "01100100");   AddWMakeup(512,  "01100101");
            AddWMakeup(576,  "01101000");   AddWMakeup(640,  "01100111");
            AddWMakeup(704,  "011001100");  AddWMakeup(768,  "011001101");
            AddWMakeup(832,  "011010010");  AddWMakeup(896,  "011010011");
            AddWMakeup(960,  "011010100");  AddWMakeup(1024, "011010101");
            AddWMakeup(1088, "011010110");  AddWMakeup(1152, "011010111");
            AddWMakeup(1216, "011011000");  AddWMakeup(1280, "011011001");
            AddWMakeup(1344, "011011010");  AddWMakeup(1408, "011011011");
            AddWMakeup(1472, "010011000");  AddWMakeup(1536, "010011001");
            AddWMakeup(1600, "010011010");  AddWMakeup(1664, "011000");
            AddWMakeup(1728, "010011011");

            // ---- Black terminating codes (T.4 Table 2b) ----
            AddB(0, "0000110111");   AddB(1, "010");          AddB(2, "11");
            AddB(3, "10");           AddB(4, "011");          AddB(5, "0011");
            AddB(6, "0010");         AddB(7, "00011");        AddB(8, "000101");
            AddB(9, "000100");       AddB(10, "0000100");     AddB(11, "0000101");
            AddB(12, "0000111");     AddB(13, "00000100");    AddB(14, "00000111");
            AddB(15, "000011000");   AddB(16, "0000010111");  AddB(17, "0000011000");
            AddB(18, "0000001000");  AddB(19, "00001100111"); AddB(20, "00001101000");
            AddB(21, "00001101100"); AddB(22, "00000110111"); AddB(23, "00000101000");
            AddB(24, "00000010111"); AddB(25, "00000011000"); AddB(26, "000011001010");
            AddB(27, "000011001011"); AddB(28, "000011001100"); AddB(29, "000011001101");
            AddB(30, "000001101000"); AddB(31, "000001101001"); AddB(32, "000001101010");
            AddB(33, "000001101011"); AddB(34, "000011010010"); AddB(35, "000011010011");
            AddB(36, "000011010100"); AddB(37, "000011010101"); AddB(38, "000011010110");
            AddB(39, "000011010111"); AddB(40, "000001101100"); AddB(41, "000001101101");
            AddB(42, "000011011010"); AddB(43, "000011011011"); AddB(44, "000001010100");
            AddB(45, "000001010101"); AddB(46, "000001010110"); AddB(47, "000001010111");
            AddB(48, "000001100100"); AddB(49, "000001100101"); AddB(50, "000001010010");
            AddB(51, "000001010011"); AddB(52, "000000100100"); AddB(53, "000000110111");
            AddB(54, "000000111000"); AddB(55, "000000100111"); AddB(56, "000000101000");
            AddB(57, "000001011000"); AddB(58, "000001011001"); AddB(59, "000000101011");
            AddB(60, "000000101100"); AddB(61, "000001011010"); AddB(62, "000001100110");
            AddB(63, "000001100111");

            // ---- Black makeup codes (T.4 Table 3) ----
            AddBMakeup(64,   "0000001111");    AddBMakeup(128,  "000011001000");
            AddBMakeup(192,  "000011001001");  AddBMakeup(256,  "000001011011");
            AddBMakeup(320,  "000000110011");  AddBMakeup(384,  "000000110100");
            AddBMakeup(448,  "000000110101");  AddBMakeup(512,  "0000001101100");
            AddBMakeup(576,  "0000001101101"); AddBMakeup(640,  "0000001001010");
            AddBMakeup(704,  "0000001001011"); AddBMakeup(768,  "0000001001100");
            AddBMakeup(832,  "0000001001101"); AddBMakeup(896,  "0000001110010");
            AddBMakeup(960,  "0000001110011"); AddBMakeup(1024, "0000001110100");
            AddBMakeup(1088, "0000001110101"); AddBMakeup(1152, "0000001110110");
            AddBMakeup(1216, "0000001110111"); AddBMakeup(1280, "0000001010010");
            AddBMakeup(1344, "0000001010011"); AddBMakeup(1408, "0000001010100");
            AddBMakeup(1472, "0000001010101"); AddBMakeup(1536, "0000001011010");
            AddBMakeup(1600, "0000001011011"); AddBMakeup(1664, "0000001100100");
            AddBMakeup(1728, "0000001100101");

            // ---- Extended (common to both colours, 1792-2560) ----
            AddCommonExt(1792, "00000001000");   AddCommonExt(1856, "00000001100");
            AddCommonExt(1920, "00000001101");   AddCommonExt(1984, "000000010010");
            AddCommonExt(2048, "000000010011");  AddCommonExt(2112, "000000010100");
            AddCommonExt(2176, "000000010101");  AddCommonExt(2240, "000000010110");
            AddCommonExt(2304, "000000010111");  AddCommonExt(2368, "000000011100");
            AddCommonExt(2432, "000000011101");  AddCommonExt(2496, "000000011110");
            AddCommonExt(2560, "000000011111");
        }

        private static int KeyFor(string bits) => (bits.Length << 16) | BitsToInt(bits);

        private static int BitsToInt(string bits)
        {
            int v = 0;
            for (int i = 0; i < bits.Length; i++) v = (v << 1) | (bits[i] - '0');
            return v;
        }

        private static void AddW(int run, string bits)         => _whiteTable[KeyFor(bits)] = (run, false);
        private static void AddWMakeup(int run, string bits)   => _whiteTable[KeyFor(bits)] = (run, true);
        private static void AddB(int run, string bits)         => _blackTable[KeyFor(bits)] = (run, false);
        private static void AddBMakeup(int run, string bits)   => _blackTable[KeyFor(bits)] = (run, true);
        private static void AddCommonExt(int run, string bits)
        {
            int key = KeyFor(bits);
            _whiteTable[key] = (run, true);
            _blackTable[key] = (run, true);
        }

        // ------------------------------------------------------------------
        //  Low-level Huffman decoding
        // ------------------------------------------------------------------

        /// <summary>
        /// The longest run <see cref="DecodeColorRun"/> answers. A run is at most a row, so the
        /// value is never reached by a well-formed stream; it is the ceiling that keeps a stream of
        /// repeated make-up codes (2560 each, as many as the data holds) from wrapping the sum past
        /// <see cref="int.MaxValue"/> into a negative run, which moves the changing element backwards
        /// and misdecodes the row silently. Below 2^28 so that a 2D horizontal mode's two runs and
        /// their start stay in range for every column count the filter admits.
        /// </summary>
        internal const int MaxRun = 1 << 28;

        /// <summary>
        /// Decodes a colour run (white if <paramref name="isBlack"/> false,
        /// else black) from the stream.  Loops over make-up codes until a
        /// terminating code is found, the sum held under <see cref="MaxRun"/>.  Throws on malformed prefix.
        /// </summary>
        internal static int DecodeColorRun(MsbBitStream br, bool isBlack)
        {
            var table = isBlack ? _blackTable : _whiteTable;
            int total = 0;
            while (true)
            {
                int code = 0;
                bool found = false;
                int run = 0;
                bool isMakeup = false;
                // Black codes can be up to 13 bits; white up to 12.
                int maxLen = isBlack ? 13 : 12;
                for (int length = 1; length <= maxLen; length++)
                {
                    int bit = br.ReadBit();
                    if (bit < 0) throw new System.IO.InvalidDataException("CCITT: unexpected EOF in Huffman code");
                    code = (code << 1) | bit;
                    int key = (length << 16) | code;
                    if (table.TryGetValue(key, out var entry))
                    {
                        run = entry.run;
                        isMakeup = entry.isMakeup;
                        found = true;
                        break;
                    }
                }
                if (!found)
                    throw new System.IO.InvalidDataException("CCITT: unresolved Huffman prefix");

                total = Math.Min(total + run, MaxRun);
                if (!isMakeup) return total;
            }
        }

        // ------------------------------------------------------------------
        //  Reference-line utilities (for T.6 and T.4-2D vertical modes)
        // ------------------------------------------------------------------

        /// <summary>
        /// Finds b1: first changing element on the reference line strictly to the
        /// right of <paramref name="a0"/> whose colour is opposite of <paramref name="a0Color"/>.
        /// Line is considered to start in white (0), so position 0 is a changing
        /// element when refLine[0] != 0.  Returns refLine.Length (past end) when
        /// no such element exists.
        /// </summary>
        internal static int FindB1(byte[] refLine, int a0, byte a0Color)
        {
            int start = Math.Max(0, a0 + 1);
            byte target = (byte)(1 - a0Color);
            for (int p = start; p < refLine.Length; p++)
            {
                byte prev = p == 0 ? (byte)0 : refLine[p - 1];
                if (refLine[p] != prev && refLine[p] == target) return p;
            }
            return refLine.Length;
        }

        /// <summary>b2: next changing element on the reference line to the right of b1.</summary>
        internal static int FindB2(byte[] refLine, int b1)
        {
            for (int p = b1 + 1; p < refLine.Length; p++)
            {
                byte prev = refLine[p - 1];
                if (refLine[p] != prev) return p;
            }
            return refLine.Length;
        }

        private static void FillRun(byte[] line, int start, int end, byte color)
        {
            if (start < 0) start = 0;
            if (end > line.Length) end = line.Length;
            for (int i = start; i < end; i++) line[i] = color;
        }

        // ------------------------------------------------------------------
        //  2D mode decoding (T.6 codes, also used by T.4 2D tag lines)
        // ------------------------------------------------------------------
        private const int MODE_PASS = 0;
        private const int MODE_HORZ = 1;
        private const int MODE_V0   = 2;
        private const int MODE_VR1  = 3;
        private const int MODE_VL1  = 4;
        private const int MODE_VR2  = 5;
        private const int MODE_VL2  = 6;
        private const int MODE_VR3  = 7;
        private const int MODE_VL3  = 8;

        private static int ModeOffset(int mode) => mode switch
        {
            MODE_V0  => 0,
            MODE_VR1 => +1,
            MODE_VL1 => -1,
            MODE_VR2 => +2,
            MODE_VL2 => -2,
            MODE_VR3 => +3,
            MODE_VL3 => -3,
            _        => 0
        };

        /// <summary>
        /// Reads the next 2D mode code from the stream.  The codes are prefix-free
        /// so we decode by progressive bit read; longest 2D code is 7 bits.
        /// </summary>
        internal static int DecodeMode(MsbBitStream br)
        {
            // V0 = "1" (1 bit)
            int b1 = br.ReadBit();
            if (b1 < 0) throw new System.IO.InvalidDataException("CCITT: EOF in mode");
            if (b1 == 1) return MODE_V0;

            // Now first bit was 0 — continue.
            int b2 = br.ReadBit();
            if (b2 < 0) throw new System.IO.InvalidDataException("CCITT: EOF in mode");
            if (b2 == 1)
            {
                // "01x"
                int b3 = br.ReadBit();
                if (b3 < 0) throw new System.IO.InvalidDataException("CCITT: EOF in mode");
                return b3 == 1 ? MODE_VR1 : MODE_VL1; // 011 / 010
            }

            // "00..."
            int b3a = br.ReadBit();
            if (b3a < 0) throw new System.IO.InvalidDataException("CCITT: EOF in mode");
            if (b3a == 1)
            {
                // "001" → Horizontal
                return MODE_HORZ;
            }

            // "000..." — Pass or VR2/VL2/VR3/VL3.
            int b4 = br.ReadBit();
            if (b4 < 0) throw new System.IO.InvalidDataException("CCITT: EOF in mode");
            if (b4 == 1)
            {
                // "0001" → Pass
                return MODE_PASS;
            }

            // "0000..."
            int b5 = br.ReadBit();
            if (b5 < 0) throw new System.IO.InvalidDataException("CCITT: EOF in mode");
            int b6 = br.ReadBit();
            if (b6 < 0) throw new System.IO.InvalidDataException("CCITT: EOF in mode");
            if (b5 == 1)
            {
                // "00001" — VR2 = 000011, VL2 = 000010
                return b6 == 1 ? MODE_VR2 : MODE_VL2;
            }

            // "00000..." — VR3 = 0000011, VL3 = 0000010  (b6 must be 1)
            if (b6 == 0)
                throw new System.IO.InvalidDataException("CCITT: unknown 2D mode code");
            int b7 = br.ReadBit();
            if (b7 < 0) throw new System.IO.InvalidDataException("CCITT: EOF in mode");
            return b7 == 1 ? MODE_VR3 : MODE_VL3;
        }

        // ------------------------------------------------------------------
        //  T.6 (Group 4) decoder
        // ------------------------------------------------------------------

        // The two amplification bounds, and the encoder's three, are one set of numbers in
        // PdfLimits — see the reasoning written there. They were literals here and literals again
        // in CcittFaxEncoder, under a comment on the encoder asserting the two sides agreed.
        private static long MaxCcittPixels => PdfLimits.MaxCcittPixels;
        private static int MaxCcittRows => PdfLimits.MaxCcittRows;

        /// <summary>
        /// Row count assumed when <c>/Rows</c> is absent or non-positive. The spec makes
        /// <c>/Rows</c> optional (the image ends at EOFB/RTC instead), so this is only an upper
        /// bound for the decode loop; <see cref="ClampRowLimit"/> narrows it further via the
        /// pixel and row budgets, and the decoder stops early at end-of-block regardless.
        /// </summary>
        private static int DefaultCcittRowLimit => MaxCcittRows;

        private static int ClampRowLimit(int rows, int columns)
        {
            int requested = rows > 0 ? rows : DefaultCcittRowLimit;
            long maxRows = MaxCcittRows;
            if (columns > 0) maxRows = Math.Min(maxRows, MaxCcittPixels / columns);
            if (maxRows < 1) maxRows = 1;
            return (int)Math.Min(requested, maxRows);
        }


        /// <summary>
        /// Decodes a full T.6 (Group 4) MMR image.
        /// Returns packed bitmap (MSB first, 1 = black, 0 = white).  Caller is
        /// responsible for honouring /BlackIs1 by inverting the output.
        /// </summary>
        internal static byte[] DecodeGroup4(byte[] encoded, int columns, int rows,
            bool endOfBlock, out int rowsDecoded, Util.WorkBudget budget = null)
            => DecodeGroup4(encoded, 0, columns, rows, endOfBlock, out rowsDecoded, out _, out _, budget);

        /// <summary>
        /// As <see cref="DecodeGroup4(byte[], int, int, bool, out int, Util.WorkBudget)"/>, from
        /// <paramref name="startOffset"/> bytes in, reporting where the data ended.
        /// </summary>
        /// <param name="encoded">The coded data.</param>
        /// <param name="startOffset">Where this bitmap's data begins in <paramref name="encoded"/>.</param>
        /// <param name="columns">Pixels per row.</param>
        /// <param name="rows">Rows to decode.</param>
        /// <param name="endOfBlock">Whether an EOFB may end the data before <paramref name="rows"/>.</param>
        /// <param name="rowsDecoded">Rows actually decoded.</param>
        /// <param name="endOffset">
        /// The byte after this bitmap's data: past the last row, past an EOFB that follows it, rounded
        /// up to a byte — where the next bitmap in the same buffer starts, as independent decoders/// count it for a JBIG2 halftone's MMR bit planes.
        /// </param>
        internal static byte[] DecodeGroup4(byte[] encoded, int startOffset, int columns, int rows,
            bool endOfBlock, out int rowsDecoded, out int endOffset)
            => DecodeGroup4(encoded, startOffset, columns, rows, endOfBlock, out rowsDecoded, out endOffset, out _);

        /// <summary>
        /// As the overload above, and <paramref name="terminated"/>: whether the data said where it ends —
        /// an EOFB, or the <paramref name="rows"/> asked for — rather than running out.
        /// <paramref name="budget"/>, when given, is charged each row's <paramref name="columns"/> pixels
        /// before the row is decoded (<see cref="StreamFilter.Work"/>, a page's image decoding).
        /// </summary>
        internal static byte[] DecodeGroup4(byte[] encoded, int startOffset, int columns, int rows,
            bool endOfBlock, out int rowsDecoded, out int endOffset, out bool terminated, Util.WorkBudget budget = null)
        {
            ResetRowRecovery();
            bool sawEofb = false;
            var br = new MsbBitStream(encoded, startOffset);
            byte[] refLine = new byte[columns]; // all white (zero)
            byte[] cur = new byte[columns];

            // Safety cap on rows when caller didn't give /Rows and stream has no EOFB.
            // Computed BEFORE the output is reserved — see InitialPackedBytes.
            int rowLimit = ClampRowLimit(rows, columns);
            var lines = new PackedRows(columns, rowLimit);

            while (lines.Count < rowLimit)
            {
                // EOFB check: two consecutive EOL codes (24 bits all "000000000001 000000000001").
                if (endOfBlock && br.PeekBits(24) == 0x001001)
                {
                    br.SkipBits(24);
                    sawEofb = true;
                    break;
                }
                if (br.Eof) break;

                budget?.Tick(columns);
                Array.Clear(cur, 0, columns);
                DecodeLine2D(br, columns, refLine, cur);

                lines.Add(cur);
                (refLine, cur) = (cur, refLine);   // the row just decoded is the next one's reference
            }

            if (br.PeekBits(24) == 0x001001) { br.SkipBits(24); sawEofb = true; }
            endOffset = (br.Position + 7) / 8;
            terminated = sawEofb || (rows > 0 && lines.Count >= rows);

            rowsDecoded = lines.Count;
            return lines.ToArray();
        }

        /// <summary>
        /// Decodes a single 2D (T.6 / mixed-mode T.4) line using <paramref name="refLine"/>
        /// as the reference.  Updates <paramref name="cur"/> in place.  Throws on
        /// malformed Huffman prefixes (the filter wrapper catches and falls back).
        /// </summary>
        internal static void DecodeLine2D(MsbBitStream br, int columns, byte[] refLine, byte[] cur)
        {
            int a0 = -1;
            byte a0Color = 0; // start of line is white

            while (a0 < columns)
            {
                int mode = DecodeMode(br);
                switch (mode)
                {
                    case MODE_PASS:
                    {
                        int b1 = FindB1(refLine, a0, a0Color);
                        int b2 = FindB2(refLine, b1);
                        FillRun(cur, Math.Max(0, a0), b2, a0Color);
                        a0 = b2;
                        break;
                    }
                    case MODE_HORZ:
                    {
                        // After Horizontal: the colour of the first run is a0Color.
                        int run1 = DecodeColorRun(br, a0Color == 1);
                        int run2 = DecodeColorRun(br, a0Color == 0);
                        int start = Math.Max(0, a0);
                        FillRun(cur, start, start + run1, a0Color);
                        FillRun(cur, start + run1, start + run1 + run2, (byte)(1 - a0Color));
                        a0 = start + run1 + run2;
                        break;
                    }
                    default: // Vertical
                    {
                        int offset = ModeOffset(mode);
                        int b1 = FindB1(refLine, a0, a0Color);
                        int a1 = b1 + offset;
                        FillRun(cur, Math.Max(0, a0), Math.Min(columns, a1), a0Color);
                        a0 = a1;
                        a0Color = (byte)(1 - a0Color);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Decodes a single 1D (T.4 MH) line — alternating white/black Huffman
        /// runs until <paramref name="columns"/> pixels have been filled.  Does
        /// NOT consume any leading EOL / tag bit (caller's responsibility).
        /// Malformed prefixes within a line are absorbed as white padding so we
        /// still emit a full-width row instead of throwing.
        /// </summary>
        internal static void DecodeLine1D(MsbBitStream br, int columns, byte[] cur)
        {
            int pos = 0;
            byte color = 0; // lines start white
            while (pos < columns)
            {
                int run;
                bool recovered = false;
                try { run = DecodeColorRun(br, color == 1); }
                catch { run = columns - pos; recovered = true; }
                int end = Math.Min(columns, pos + run);

                // Pad in WHITE, not in the current colour, as the doc comment above says ("absorbed as
                // white padding"). `color` alternates, so padding in it would paint the rest of the
                // scanline solid black for a stream truncated mid-black-run, and a fax clipped by a wrong
                // /Length would come back as a full-height bitmap with black bands and no way to tell it
                // from a clean decode.
                FillRun(cur, pos, end, recovered ? (byte)0 : color);

                if (recovered)
                {
                    _lastRowRecovered ??=
                        $"CCITT: an unresolved Huffman prefix at column {pos} was padded to the " +
                        "end of the scanline in white; the row was reconstructed, not decoded.";
                }

                pos = end;
                color = (byte)(1 - color);
            }
        }

        /// <summary>
        /// Set when a scanline could not be decoded and was padded to full width instead;
        /// <c>null</c> when every row decoded cleanly. Surfaced by
        /// <see cref="CcittFaxDecodeFilter.LastRowRecovered"/>.
        /// </summary>
        /// <remarks>
        /// A patched row is a plausible-looking wrong answer — a full-width scanline that simply
        /// has the wrong pixels in it — so without this a partially-garbage decode was
        /// indistinguishable from a clean one. Reset by <see cref="ResetRowRecovery"/> at the start
        /// of each decode entry point.
        /// </remarks>
        internal static string LastRowRecovered => _lastRowRecovered;

        [ThreadStatic]
        private static string _lastRowRecovered;

        /// <summary>Clears <see cref="LastRowRecovered"/>; called at the start of every decode.</summary>
        internal static void ResetRowRecovery() => _lastRowRecovered = null;

        // ------------------------------------------------------------------
        //  T.4 (Group 3) 1D decoder
        // ------------------------------------------------------------------

        /// <summary>
        /// Decodes a T.4 (Group 3) 1D-MH image.  Each line is encoded as an
        /// alternating sequence of white-then-black Huffman runs preceded by an
        /// optional EOL code (000000000001).
        /// </summary>
        internal static byte[] DecodeGroup3_1D(byte[] encoded, int columns, int rows,
            bool endOfBlock, bool encodedByteAlign, bool requireEol, out int rowsDecoded, Util.WorkBudget budget = null)
            => DecodeGroup3_1D(encoded, 0, columns, rows, endOfBlock, encodedByteAlign, requireEol,
                               out rowsDecoded, out _, out _, budget);

        /// <summary>
        /// As the overload above, from <paramref name="startOffset"/>, reporting where the data ended —
        /// past the last row and the RTC after it, rounded up to a byte — and whether it said so (an RTC,
        /// or the <paramref name="rows"/> asked for) rather than running out. <paramref name="budget"/>,
        /// when given, is charged each row's <paramref name="columns"/> pixels before the row is decoded.
        /// </summary>
        internal static byte[] DecodeGroup3_1D(byte[] encoded, int startOffset, int columns, int rows,
            bool endOfBlock, bool encodedByteAlign, bool requireEol, out int rowsDecoded,
            out int endOffset, out bool terminated, Util.WorkBudget budget = null)
        {
            ResetRowRecovery();
            var br = new MsbBitStream(encoded, startOffset);
            bool sawRtc = false;

            // Clamp first, then reserve from the clamp — see InitialPackedBytes.
            int rowLimit = ClampRowLimit(rows, columns);
            var lines = new PackedRows(columns, rowLimit);
            byte[] cur = new byte[columns];

            // T.4: six consecutive EOLs mark end (RTC).
            int consecutiveEols = 0;

            while (lines.Count < rowLimit && !br.Eof)
            {
                bool sawEol = TryConsumeEol(br);
                if (sawEol)
                {
                    consecutiveEols++;
                    if (endOfBlock && consecutiveEols >= 6) { sawRtc = true; break; }
                }
                else if (requireEol && lines.Count == 0)
                {
                    throw new System.IO.InvalidDataException("CCITT G3: expected EOL");
                }
                else
                {
                    consecutiveEols = 0;
                }

                if (br.Eof) break;

                // Optionally align to byte boundary before each line.
                if (encodedByteAlign) br.AlignToByte();

                budget?.Tick(columns);
                Array.Clear(cur, 0, columns);
                DecodeLine1D(br, columns, cur);
                lines.Add(cur);
                if (lines.Count > 0) consecutiveEols = 0;
            }

            // The RTC after the last of /Rows rows belongs to the data.
            if (!sawRtc) sawRtc = ConsumeRtc(br);
            endOffset = (br.Position + 7) / 8;
            terminated = sawRtc || (rows > 0 && lines.Count >= rows);
            rowsDecoded = lines.Count;
            return lines.ToArray();
        }

        /// <summary>An RTC (six EOLs) at the cursor consumed; true when there was one.</summary>
        private static bool ConsumeRtc(MsbBitStream br)
        {
            int saved = br.Position, eols = 0;
            while (eols < 6 && TryConsumeEol(br)) eols++;
            if (eols == 6) return true;
            RewindTo(br, saved);
            return false;
        }

        // ------------------------------------------------------------------
        //  T.4 (Group 3) 2D mixed-mode decoder
        // ------------------------------------------------------------------

        /// <summary>
        /// Decodes a T.4 Group 3 mixed 1D/2D image (/K &gt; 0).  Per ITU-T T.4
        /// §2.5.3, each line is preceded by:
        ///   • an EOL code (000000000001, 12 bits)
        ///   • a 1-bit tag: 1 = 1D line (Modified Huffman), 0 = 2D line (T.6 Pass / Horizontal / Vertical codes).
        /// The first line in the stream is always 1D; after that the encoder is
        /// free to choose.
        /// </summary>
        /// <remarks>
        /// <b>The tag bit decides, and /K does not.</b> /K bounds how many 2D lines an encoder
        /// may write in a row, and enforcing it — reading the next line as 1D past K − 1 2D lines,
        /// whatever its tag says — would be wrong. A line coded 2D read as 1D is garbage, and so is
        /// every 2D line after it. PDF producers write /K 1 for "mixed" as often as the encoder's
        /// real value, and a fine-resolution fax is encoded with K = 4, so such a clamp would corrupt
        /// ordinary files. Other readers read the tag alone.
        /// </remarks>
        internal static byte[] DecodeGroup3_2D(byte[] encoded, int columns, int rows,
            bool endOfBlock, bool encodedByteAlign, out int rowsDecoded, Util.WorkBudget budget = null)
            => DecodeGroup3_2D(encoded, 0, columns, rows, endOfBlock, encodedByteAlign, out rowsDecoded, out _, out _, budget);

        /// <summary>As the overload above, from <paramref name="startOffset"/>, reporting where the data ended and whether it said so, and charging <paramref name="budget"/>, as <see cref="DecodeGroup3_1D(byte[], int, int, int, bool, bool, bool, out int, out int, out bool, Util.WorkBudget)"/> does.</summary>
        internal static byte[] DecodeGroup3_2D(byte[] encoded, int startOffset, int columns, int rows,
            bool endOfBlock, bool encodedByteAlign, out int rowsDecoded, out int endOffset, out bool terminated, Util.WorkBudget budget = null)
        {
            ResetRowRecovery();
            bool sawRtc = false;

            var br = new MsbBitStream(encoded, startOffset);
            byte[] refLine = new byte[columns];
            byte[] cur = new byte[columns];

            // Clamp first, then reserve from the clamp — see InitialPackedBytes.
            int rowLimit = ClampRowLimit(rows, columns);
            var lines = new PackedRows(columns, rowLimit);
            int consecutiveEols = 0;

            while (lines.Count < rowLimit && !br.Eof)
            {
                bool sawEol = TryConsumeEol(br);
                if (sawEol)
                {
                    consecutiveEols++;
                    if (endOfBlock && consecutiveEols >= 6) { sawRtc = true; break; }
                    // Mixed-mode T.4 G3: a real data row begins with EOL +
                    // 1-bit-tag + Huffman codes.  When the next 12 bits are
                    // ANOTHER EOL prefix, this was a back-to-back EOL (part of
                    // an RTC sequence or padding), not a row prefix — loop
                    // back to consume the next EOL.
                    if (br.Eof || br.PeekBits(12) == 0x001) continue;
                }
                else
                {
                    consecutiveEols = 0;
                }
                if (br.Eof) break;

                // 1-bit tag follows EOL: 1 → 1D line, 0 → 2D line.
                int tag = br.ReadBit();
                if (tag < 0) break;

                if (encodedByteAlign) br.AlignToByte();

                budget?.Tick(columns);
                Array.Clear(cur, 0, columns);
                if (tag == 1) DecodeLine1D(br, columns, cur);
                else DecodeLine2D(br, columns, refLine, cur);

                lines.Add(cur);
                (refLine, cur) = (cur, refLine);   // the row just decoded is the next one's reference
                if (lines.Count > 0) consecutiveEols = 0;
            }

            if (!sawRtc) sawRtc = ConsumeRtc(br);
            endOffset = (br.Position + 7) / 8;
            terminated = sawRtc || (rows > 0 && lines.Count >= rows);
            rowsDecoded = lines.Count;
            return lines.ToArray();
        }

        // Peek up to 16 bits looking for "000000000001" (twelve bits); if the
        // next 12 bits form an EOL, consume them and return true.  Otherwise
        // return false without consuming.
        private static bool TryConsumeEol(MsbBitStream br)
        {
            // EOL may be preceded by zero-padding bits; per spec the leading zero
            // run is not fixed in length (fill bits are allowed).  Look for the
            // classic 12-bit form at the current position.
            if (br.PeekBits(12) == 0x001)
            {
                br.SkipBits(12);
                return true;
            }
            // Accommodate extended leading-zero padding used by some encoders:
            // scan up to 16 extra bits of zeros before a single terminating 1.
            int saved = br.Position;
            int zeroes = 0;
            while (true)
            {
                int b = br.ReadBit();
                if (b < 0) { RewindTo(br, saved); return false; }
                if (b == 0)
                {
                    zeroes++;
                    if (zeroes > 24) { RewindTo(br, saved); return false; }
                }
                else
                {
                    if (zeroes >= 11) return true; // consumed as EOL
                    // Not an EOL — but the "1" we just read may be the first
                    // Huffman bit of a real code.  Rewind to saved position.
                    RewindTo(br, saved);
                    return false;
                }
            }
        }

        private static void RewindTo(MsbBitStream br, int savedPosition)
        {
            // Absolute seek — restores the reader to the saved bit position
            // regardless of direction. A backward rewind that was a silent no-op would
            // leave the "not an EOL" / EOF paths' consumed bits behind, and the caller
            // would decode the line starting mid-code (corrupting that row and all
            // following rows).
            br.SetPosition(savedPosition);
        }

        // ------------------------------------------------------------------
        //  Packing helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Packs decoded lines into MSB-first bytes, stride = ceil(columns/8).
        /// Internal convention: 1 = black, 0 = white.
        /// </summary>
        internal static byte[] PackLines(List<byte[]> lines, int columns)
        {
            if (columns <= 0 || lines == null || lines.Count == 0) return Array.Empty<byte>();
            var packed = new PackedRows(columns, lines.Count);
            foreach (byte[] row in lines) packed.Add(row);
            return packed.ToArray();
        }

        /// <summary>
        /// The most bytes a decoder reserves for its packed rows before it has decoded any; the buffer doubles as
        /// rows arrive, up to <see cref="ClampRowLimit"/> of them.
        /// </summary>
        /// <remarks>
        /// The reservation is taken from the clamped row count, never the raw <c>/Rows</c>, and is bounded besides:
        /// <c>/DecodeParms &lt;&lt;/Columns 8 /Rows 500000000&gt;&gt;</c> with two bytes of payload would otherwise
        /// reserve its whole declared image before a single bit was read — <c>CcittFaxDecodeFilter</c> bounds
        /// <c>/Columns</c> and places no bound on <c>/Rows</c>. A real fax page at 1728 columns is about 480 KB
        /// packed, so it grows from this a few times.
        /// </remarks>
        private const int InitialPackedBytes = 64 * 1024;

        /// <summary>
        /// Rows packed MSB first as they are decoded, stride ⌈columns/8⌉, 1 black — the decoders keep one row
        /// unpacked (and, coding 2D, its reference) and pack each into this, rather than a byte a pixel for every
        /// row until the end, which for a letter page at 300 dpi would be 9 MB to return 1 MB.
        /// </summary>
        internal sealed class PackedRows
        {
            private readonly int _columns, _stride;
            private byte[] _data;

            internal PackedRows(int columns, int rowLimit)
            {
                _columns = columns;
                _stride = (columns + 7) / 8;
                long reserve = Math.Min((long)Math.Max(1, rowLimit) * _stride, InitialPackedBytes);
                _data = new byte[Math.Max(_stride, (int)reserve)];
            }

            /// <summary>Rows packed so far.</summary>
            internal int Count { get; private set; }

            /// <summary>The bytes held for packed rows now — the evidence that a declared row count buys no more than <see cref="InitialPackedBytes"/> up front.</summary>
            internal int Reserved => _data.Length;

            /// <summary>Packs <paramref name="row"/>, one byte a pixel and nonzero for black, as the next row.</summary>
            internal void Add(byte[] row)
            {
                long end = (long)(Count + 1) * _stride;
                if (end > _data.Length)
                {
                    long grown = Math.Max(end, 2L * _data.Length);
                    Array.Resize(ref _data, (int)Math.Min(grown, int.MaxValue - 64));
                }
                int offset = Count * _stride;
                for (int x = 0; x < _columns; x++)
                    if (row[x] != 0)
                        _data[offset + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                Count++;
            }

            /// <summary>The packed rows, exactly <see cref="Count"/> of them.</summary>
            internal byte[] ToArray()
            {
                int length = Count * _stride;
                if (length == 0) return Array.Empty<byte>();
                if (length == _data.Length) return _data;
                var exact = new byte[length];
                Buffer.BlockCopy(_data, 0, exact, 0, length);
                return exact;
            }
        }

        /// <summary>
        /// Inverts every byte — used to flip the internal "1 = black" result into
        /// the PDF default "0 = black, 1 = white" surface when /BlackIs1 is false.
        /// </summary>
        internal static void InvertBytes(byte[] data)
        {
            for (int i = 0; i < data.Length; i++) data[i] ^= 0xFF;
        }
    }
}
