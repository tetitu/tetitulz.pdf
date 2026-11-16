// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// Pure-managed CCITT T.6 (Group 4 / MMR) fax <b>encoder</b> — the write-side
    /// counterpart of <see cref="CcittFaxDecoder"/>.  Produces a codestream suitable
    /// for a PDF stream filtered with
    /// <c>/CCITTFaxDecode &lt;&lt; /K -1 /Columns columns /Rows rows /BlackIs1 blackIs1 &gt;&gt;</c>.
    ///
    /// <para><b>Only G4 (T.6, /K -1) is produced.</b>  G3 1-D and G3 mixed 1-D/2-D are
    /// deliberately out of scope: G4 is what every PDF producer emits for bitonal
    /// scans, and the extra modes would add code with no caller.</para>
    ///
    /// <para><b>Input layout</b> — <c>packedRows</c> is 1 bit per pixel,
    /// MSB first, each row starting on a byte boundary with stride
    /// <c>ceil(columns / 8)</c>.  That is byte-for-byte the layout
    /// <see cref="CcittFaxDecodeFilter"/> produces, so
    /// <c>Decode(Encode(x)) == x</c> for the same <c>/Columns</c>, <c>/Rows</c> and
    /// <c>/BlackIs1</c>.  Bits past <c>columns</c> in the last byte of a row are
    /// padding and are <b>ignored</b> on input; the decoder writes them back as the
    /// "white" value for the chosen <c>blackIs1</c> convention.</para>
    ///
    /// <para><b>Colour convention</b> — the PDF surface convention, matching
    /// <see cref="CcittFaxDecodeFilter"/>: with <c>blackIs1</c> false
    /// (the PDF default) a <c>0</c> bit is black and a <c>1</c> bit is white; with it
    /// true the sense is reversed.  Internally the coder uses T.6's own "1 = black"
    /// convention, exactly as <see cref="CcittFaxDecoder"/> does.</para>
    ///
    /// <para><b>Diagnostics</b> — this class has no failure-tolerant path and therefore
    /// no <c>Last*Error</c> slot: every rejected input throws a typed exception from
    /// <see cref="Encode"/> before a single byte is allocated from it.  Nothing here
    /// silently produces a partial or approximate codestream.</para>
    /// </summary>
    public static class CcittFaxEncoder
    {
        // ------------------------------------------------------------------
        //  DoS bounds
        // ------------------------------------------------------------------
        // Identical to the read side so a stream this encoder produces is always one the decoder
        // will accept: encoding past any of them emits a file that cannot be read back.
        //
        // The values live in PdfLimits and every site reads them there, the read side included. These three stay public because a
        // caller that wants to SKIP an oversized image rather than catch an exception tests them.
        /// <summary>Largest <c>/Columns</c> this encoder accepts (<see cref="PdfLimits.MaxCcittColumns"/>).</summary>
        public static int  MaxEncodeColumns => PdfLimits.MaxCcittColumns;
        /// <summary>Largest <c>/Rows</c> this encoder accepts (<see cref="PdfLimits.MaxCcittRows"/>).</summary>
        public static int  MaxEncodeRows    => PdfLimits.MaxCcittRows;
        /// <summary>Largest total pixel count this encoder accepts (<see cref="PdfLimits.MaxCcittPixels"/>).</summary>
        public static long MaxEncodePixels => PdfLimits.MaxCcittPixels;

        // Longest make-up run in T.4 Annex A (the shared 1792-2560 extension table).
        private const int MaxMakeupRun = 2560;

        /// <summary>
        /// Encodes a packed 1-bit-per-pixel raster as a CCITT Group 4 (T.6 MMR)
        /// codestream terminated by EOFB (two EOL codes), zero-padded to a byte
        /// boundary.
        /// </summary>
        /// <param name="packedRows">
        /// Packed raster, MSB first, <c>ceil(columns / 8)</c> bytes per row.  Must hold
        /// at least <c>rows</c> full rows; any trailing bytes are ignored.
        /// </param>
        /// <param name="columns">Pixels per row.  1 .. 1,000,000.</param>
        /// <param name="rows">Number of rows.  1 .. 200,000.</param>
        /// <param name="blackIs1">
        /// PDF <c>/BlackIs1</c>: false (default) means a 0 bit is black.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="packedRows"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="columns"/> or <paramref name="rows"/> is below 1 or above its
        /// cap, or their product exceeds the 64 Mi pixel budget.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="packedRows"/> is shorter than <c>rows * ceil(columns / 8)</c>.
        /// </exception>
        public static byte[] Encode(byte[] packedRows, int columns, int rows, bool blackIs1 = false)
        {
            if (packedRows == null) throw new ArgumentNullException(nameof(packedRows));

            // Validate the geometry BEFORE looking at the buffer, so an absurd
            // columns/rows pair is refused without touching (or sizing anything from)
            // the caller's data.
            if (columns < 1 || columns > MaxEncodeColumns)
                throw new ArgumentOutOfRangeException(nameof(columns), columns,
                    "CCITT G4 encode: /Columns must be between 1 and " + MaxEncodeColumns + ".");
            if (rows < 1 || rows > MaxEncodeRows)
                throw new ArgumentOutOfRangeException(nameof(rows), rows,
                    "CCITT G4 encode: /Rows must be between 1 and " + MaxEncodeRows + ".");
            long pixels = (long)columns * rows;
            if (pixels > MaxEncodePixels)
                throw new ArgumentOutOfRangeException(nameof(rows), rows,
                    "CCITT G4 encode: " + columns + " x " + rows + " exceeds the " +
                    MaxEncodePixels + " pixel budget.");

            int stride = (columns + 7) / 8;
            long required = (long)stride * rows;
            if (packedRows.LongLength < required)
                throw new ArgumentException(
                    "CCITT G4 encode: packedRows holds " + packedRows.LongLength +
                    " bytes but " + required + " are required for " + rows + " rows of " +
                    columns + " columns (" + stride + " bytes per row).", nameof(packedRows));

            // A G4 line is at worst a handful of bits per changing element; start at a
            // fraction of the raster and let the writer double as needed.
            int initialCapacity = (int)Math.Min(required / 2 + 64, 1 << 20);
            var writer = new MsbBitWriter(initialCapacity);

            byte[] refLine = new byte[columns]; // imaginary all-white line above row 0
            byte[] cur     = new byte[columns];

            for (int y = 0; y < rows; y++)
            {
                UnpackRow(packedRows, (long)y * stride, columns, blackIs1, cur);
                EncodeLine2D(writer, columns, refLine, cur);
                Buffer.BlockCopy(cur, 0, refLine, 0, columns);
            }

            // EOFB = two EOL codes.  CcittFaxDecoder.DecodeGroup4 peeks for exactly this
            // 24-bit pattern at each row boundary, which is the only way it can stop when
            // the caller supplies no /Rows.  A valid 2-D mode code never begins with six
            // zero bits, so an EOL prefix can never be mistaken for row data.
            WriteCode(writer, EolCode);
            WriteCode(writer, EolCode);

            return writer.ToArray();
        }

        // ------------------------------------------------------------------
        //  Raster unpacking
        // ------------------------------------------------------------------

        /// <summary>
        /// Expands one packed row into the T.6 internal form (one byte per pixel,
        /// 1 = black).  Bits at or past <paramref name="columns"/> are padding and are
        /// never read, so a caller whose padding holds garbage still encodes correctly.
        /// </summary>
        private static void UnpackRow(byte[] packed, long offset, int columns, bool blackIs1, byte[] cur)
        {
            // blackIs1 false (PDF default) → a 0 bit is black.
            int blackBit = blackIs1 ? 1 : 0;
            for (int x = 0; x < columns; x++)
            {
                int bit = (packed[offset + (x >> 3)] >> (7 - (x & 7))) & 1;
                cur[x] = bit == blackBit ? (byte)1 : (byte)0;
            }
        }

        // ------------------------------------------------------------------
        //  T.6 two-dimensional line coder
        // ------------------------------------------------------------------

        /// <summary>
        /// Emits the mode codes for one coding line against <paramref name="refLine"/>.
        ///
        /// b1/b2 come from <see cref="CcittFaxDecoder.FindB1"/> / <see cref="CcittFaxDecoder.FindB2"/>
        /// — the decoder's own reference-line helpers — rather than a private copy, so
        /// the two sides cannot drift on what "the first changing element to the right of
        /// a0 of opposite colour" means.  a1/a2 are the same two functions applied to the
        /// coding line, which is exactly their definition.
        /// </summary>
        private static void EncodeLine2D(MsbBitWriter w, int columns, byte[] refLine, byte[] cur)
        {
            int a0 = -1;       // imaginary white changing element just before pixel 0
            byte color = 0;    // colour of the run starting at a0; 0 = white

            while (a0 < columns)
            {
                int a1 = CcittFaxDecoder.FindB1(cur, a0, color);
                int b1 = CcittFaxDecoder.FindB1(refLine, a0, color);
                int b2 = CcittFaxDecoder.FindB2(refLine, b1);

                if (b2 < a1)
                {
                    // Pass mode: the reference line's colour changes twice before the
                    // coding line changes once.  a0 moves to b2, the colour does not.
                    WriteCode(w, PassCode);
                    a0 = b2;
                    continue;
                }

                int delta = a1 - b1;
                if (delta >= -3 && delta <= 3)
                {
                    // Vertical mode V(delta): three bits or fewer for the common case.
                    WriteCode(w, VerticalCodes[delta + 3]);
                    a0 = a1;
                    color = (byte)(1 - color);
                    continue;
                }

                // Horizontal mode: two run lengths, starting colour = colour of a0.
                int a2 = CcittFaxDecoder.FindB2(cur, a1);
                int start = a0 < 0 ? 0 : a0;
                WriteCode(w, HorizontalCode);
                WriteRun(w, a1 - start, color == 1);
                WriteRun(w, a2 - a1, color == 0);
                a0 = a2;
            }
        }

        /// <summary>
        /// Emits one run length as zero or more make-up codes followed by exactly one
        /// terminating code (T.4 §4.2.1).  Runs of 2624 or more repeat the 2560 make-up
        /// code until the remainder is representable.
        /// </summary>
        private static void WriteRun(MsbBitWriter w, int run, bool black)
        {
            int[] terminating = black ? BlackTerminating : WhiteTerminating;
            int[] makeup      = black ? BlackMakeup      : WhiteMakeup;

            while (run >= MaxMakeupRun + 64)
            {
                WriteCode(w, makeup[MaxMakeupRun / 64]);
                run -= MaxMakeupRun;
            }
            if (run >= 64)
            {
                int index = run / 64;              // 1 .. 40, i.e. 64 .. 2560
                WriteCode(w, makeup[index]);
                run -= index * 64;
            }
            WriteCode(w, terminating[run]);        // 0 .. 63
        }

        // ------------------------------------------------------------------
        //  Bit writing
        // ------------------------------------------------------------------
        //
        // MsbMsbBitWriter, shared with LzwEncodeFilter and mirroring MsbBitStream — bit order is
        // exactly the kind of decision that must be made once: output written LSB-first still decodes to SOMETHING, so
        // a second copy that disagreed would pass a smoke test and fail on real data.

        // ------------------------------------------------------------------
        //  Code tables (ITU-T T.4 Annex A / T.6)
        // ------------------------------------------------------------------
        //
        // A code is packed as `(length << 16) | bits`, the same shape
        // CcittFaxDecoder keys its lookup dictionaries with.  Entries are written as
        // literal bit strings so they can be read straight off the spec tables and
        // diffed against the decoder's copy.

        private static int Pack(string bits)
        {
            int v = 0;
            for (int i = 0; i < bits.Length; i++) v = (v << 1) | (bits[i] - '0');
            return (bits.Length << 16) | v;
        }

        private static void WriteCode(MsbBitWriter w, int packed) => w.WriteBits(packed & 0xFFFF, packed >> 16);

        private static readonly int PassCode       = Pack("0001");
        private static readonly int HorizontalCode = Pack("001");
        private static readonly int EolCode        = Pack("000000000001");

        /// <summary>Vertical mode codes indexed by <c>a1 - b1 + 3</c>, i.e. VL3 .. V0 .. VR3.</summary>
        private static readonly int[] VerticalCodes =
        {
            Pack("0000010"),  // VL(3)
            Pack("000010"),   // VL(2)
            Pack("010"),      // VL(1)
            Pack("1"),        // V(0)
            Pack("011"),      // VR(1)
            Pack("000011"),   // VR(2)
            Pack("0000011"),  // VR(3)
        };

        private static int[] PackAll(string[] bits)
        {
            var result = new int[bits.Length];
            for (int i = 0; i < bits.Length; i++) result[i] = Pack(bits[i]);
            return result;
        }

        /// <summary>White terminating codes, indexed by run length 0-63 (T.4 Table 2a).</summary>
        private static readonly int[] WhiteTerminating = PackAll(new[]
        {
            "00110101", "000111",   "0111",     "1000",     "1011",     "1100",
            "1110",     "1111",     "10011",    "10100",    "00111",    "01000",
            "001000",   "000011",   "110100",   "110101",   "101010",   "101011",
            "0100111",  "0001100",  "0001000",  "0010111",  "0000011",  "0000100",
            "0101000",  "0101011",  "0010011",  "0100100",  "0011000",  "00000010",
            "00000011", "00011010", "00011011", "00010010", "00010011", "00010100",
            "00010101", "00010110", "00010111", "00101000", "00101001", "00101010",
            "00101011", "00101100", "00101101", "00000100", "00000101", "00001010",
            "00001011", "01010010", "01010011", "01010100", "01010101", "00100100",
            "00100101", "01011000", "01011001", "01011010", "01011011", "01001010",
            "01001011", "00110010", "00110011", "00110100",
        });

        /// <summary>Black terminating codes, indexed by run length 0-63 (T.4 Table 2b).</summary>
        private static readonly int[] BlackTerminating = PackAll(new[]
        {
            "0000110111",   "010",          "11",           "10",
            "011",          "0011",         "0010",         "00011",
            "000101",       "000100",       "0000100",      "0000101",
            "0000111",      "00000100",     "00000111",     "000011000",
            "0000010111",   "0000011000",   "0000001000",   "00001100111",
            "00001101000",  "00001101100",  "00000110111",  "00000101000",
            "00000010111",  "00000011000",  "000011001010", "000011001011",
            "000011001100", "000011001101", "000001101000", "000001101001",
            "000001101010", "000001101011", "000011010010", "000011010011",
            "000011010100", "000011010101", "000011010110", "000011010111",
            "000001101100", "000001101101", "000011011010", "000011011011",
            "000001010100", "000001010101", "000001010110", "000001010111",
            "000001100100", "000001100101", "000001010010", "000001010011",
            "000000100100", "000000110111", "000000111000", "000000100111",
            "000000101000", "000001011000", "000001011001", "000000101011",
            "000000101100", "000001011010", "000001100110", "000001100111",
        });

        // Make-up tables are indexed by run/64: entry 0 is unused (a run under 64 needs
        // no make-up code), 1-27 are the colour-specific 64-1728 codes and 28-40 are the
        // 1792-2560 extension codes, which T.4 shares between the two colours.
        private static readonly string[] ExtendedMakeupBits =
        {
            "00000001000",  "00000001100",  "00000001101",  "000000010010",
            "000000010011", "000000010100", "000000010101", "000000010110",
            "000000010111", "000000011100", "000000011101", "000000011110",
            "000000011111",
        };

        private static int[] BuildMakeup(string[] colourBits)
        {
            var all = new string[1 + colourBits.Length + ExtendedMakeupBits.Length];
            all[0] = "0"; // unused placeholder — WriteRun never indexes it
            for (int i = 0; i < colourBits.Length; i++) all[1 + i] = colourBits[i];
            for (int i = 0; i < ExtendedMakeupBits.Length; i++)
                all[1 + colourBits.Length + i] = ExtendedMakeupBits[i];
            return PackAll(all);
        }

        /// <summary>White make-up codes 64-1728 (T.4 Table 3), then the shared 1792-2560 extension.</summary>
        private static readonly int[] WhiteMakeup = BuildMakeup(new[]
        {
            "11011",     "10010",     "010111",    "0110111",   "00110110",  "00110111",
            "01100100",  "01100101",  "01101000",  "01100111",  "011001100", "011001101",
            "011010010", "011010011", "011010100", "011010101", "011010110", "011010111",
            "011011000", "011011001", "011011010", "011011011", "010011000", "010011001",
            "010011010", "011000",    "010011011",
        });

        /// <summary>Black make-up codes 64-1728 (T.4 Table 3), then the shared 1792-2560 extension.</summary>
        private static readonly int[] BlackMakeup = BuildMakeup(new[]
        {
            "0000001111",    "000011001000",  "000011001001",  "000001011011",
            "000000110011",  "000000110100",  "000000110101",  "0000001101100",
            "0000001101101", "0000001001010", "0000001001011", "0000001001100",
            "0000001001101", "0000001110010", "0000001110011", "0000001110100",
            "0000001110101", "0000001110110", "0000001110111", "0000001010010",
            "0000001010011", "0000001010100", "0000001010101", "0000001011010",
            "0000001011011", "0000001100100", "0000001100101",
        });
    }
}
