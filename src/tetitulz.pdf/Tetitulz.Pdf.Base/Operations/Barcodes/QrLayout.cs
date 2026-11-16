// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>
    /// Where a QR symbol's modules go — the one answer, shared by the encoder and the reader.
    /// </summary>
    /// <remarks>
    /// <para><b>A reader is the encoder run backwards, so every rule they share has to be one
    /// rule.</b> Function-pattern placement, the reserved map, the mask functions and the zigzag
    /// data order are all things both sides must agree on exactly; a second copy that drifts by one
    /// module produces a reader that decodes its own library's output and nothing else — and, worse,
    /// a round trip that passes while both sides are wrong together.</para>
    ///
    /// <para><b>A leaf by construction: no static fields beyond pure data, so no type
    /// initializer.</b> <c>QrCodeEncoder</c> holds a <see cref="GaloisField256"/> in a static field,
    /// so a reader that reached into the encoder for these would take a type-initialization edge on
    /// it — the sideways-between-peers coupling CLAUDE.md forbids. The shared code went down instead.</para>
    ///
    /// <para><b>Scope is versions 1–10 at error-correction level M, byte mode</b>, matching what
    /// this library writes. Anything else is refused by name rather than guessed at.</para>
    /// </remarks>
    internal static class QrLayout
    {
        /// <summary>Lowest version this handles.</summary>
        internal const int MinVersion = 1;

        /// <summary>Highest version the encoder writes (level M, byte mode).</summary>
        internal const int MaxVersion = 10;

        /// <summary>Highest version there is, and the highest the reader reads.</summary>
        internal const int MaxReadVersion = 40;

        /// <summary>The format-info bits for error-correction level M.</summary>
        /// <remarks>
        /// Not 0b01 — the four levels are numbered L=01, M=00, Q=11, H=10 in the format field,
        /// which is deliberately not their quality order. Reading it as an ordinal is a classic way
        /// to decode an L symbol as an M one.
        /// </remarks>
        internal const int EccLevelM = 0b00;

        /// <summary>Byte mode's four-bit mode indicator.</summary>
        internal const int ByteMode = 0b0100;

        /// <summary>The constant the 15-bit format code is XORed with, so it is never all-zero.</summary>
        internal const int FormatMask = 0x5412;

        /// <summary>Generator for the format field's BCH(15,5) code.</summary>
        internal const int FormatBchPolynomial = 0x537;

        // Error-correction codewords per block and blocks per symbol, by level in quality order
        // (L, M, Q, H) and version (index 0 unused) — ISO/IEC 18004's Table 9, taken from two
        // independent implementations that agree on all 160 entries: Nayuki's QR Code generator
        // (these two arrays) and an independent implementation's version table (its blocks, checked against these with the
        // data codewords derived below). Data codewords are what the symbol's raw
        // codewords leave after the error correction, so they are derived, not a third list.
        private static readonly int[][] EcCodewordsPerBlock =
        {
            new[] { -1,  7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
            new[] { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
            new[] { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
            new[] { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        };

        private static readonly int[][] BlocksPerSymbol =
        {
            new[] { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4,  4,  4,  4,  4,  6,  6,  6,  6,  7,  8,  8,  9,  9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
            new[] { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5,  5,  8,  9,  9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
            new[] { -1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8,  8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
            new[] { -1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 },
        };

        /// <summary>The quality-order index (L 0, M 1, Q 2, H 3) of a level's format bits (L 01, M 00, Q 11, H 10).</summary>
        internal static int LevelIndex(int eccLevel) => eccLevel switch
        {
            0b01 => 0,
            0b00 => 1,
            0b11 => 2,
            _ => 3,
        };

        /// <summary>Modules per side for a version.</summary>
        internal static int SizeOf(int version) => 17 + (4 * version);

        /// <summary>
        /// The version a grid of this size holds, or -1 when no version has that size.
        /// </summary>
        /// <remarks>
        /// Every QR size is 17 + 4v, so a size that is not ≡ 1 (mod 4) is not a QR grid at all —
        /// which is a cheap and total rejection of a square of ink that happens to be square.
        /// </remarks>
        internal static int VersionOf(int size)
        {
            if (size < SizeOf(MinVersion) || size > SizeOf(MaxReadVersion)) return -1;
            if ((size - 17) % 4 != 0) return -1;
            return (size - 17) / 4;
        }

        /// <summary>
        /// The codewords a version's data region holds — every module that is not a function
        /// pattern, format or version field, in whole bytes (a few remainder bits are left over).
        /// </summary>
        internal static int RawCodewords(int version)
        {
            int modules = (16 * version + 128) * version + 64;        // all, less finders, timing, format
            if (version >= 2)
            {
                int align = version / 7 + 2;
                modules -= (25 * align - 10) * align - 55;               // the alignment patterns
                if (version >= 7) modules -= 36;                          // the two version fields
            }
            return modules / 8;
        }

        /// <summary>Total data codewords for a version at a level (format bits).</summary>
        internal static int DataCodewords(int version, int eccLevel)
        {
            int level = LevelIndex(eccLevel);
            return RawCodewords(version) - EcCodewordsPerBlock[level][version] * BlocksPerSymbol[level][version];
        }

        /// <summary>Total data codewords for a version at level M, the level the encoder writes.</summary>
        internal static int DataCodewords(int version) => DataCodewords(version, EccLevelM);

        /// <summary>
        /// The most payload bytes a version holds at level M in byte mode: the data codewords, less
        /// the 4-bit mode indicator and the character-count field, in whole bytes.
        /// </summary>
        /// <remarks>
        /// Derived rather than transcribed. The encoder carried ISO/IEC 18004's capacity table as a
        /// second list of ten numbers beside the codeword table it follows from, and a transcription
        /// error in either would have written a symbol whose count field disagrees with its data.
        /// </remarks>
        internal static int ByteCapacity(int version)
            => (DataCodewords(version) * 8 - 4 - CharacterCountBits(version)) / 8;

        /// <summary>Error-correction codewords per block at a level (format bits).</summary>
        internal static int EcPerBlock(int version, int eccLevel) => EcCodewordsPerBlock[LevelIndex(eccLevel)][version];

        /// <summary>Error-correction codewords per block at level M.</summary>
        internal static int EcPerBlock(int version) => EcPerBlock(version, EccLevelM);

        /// <summary>
        /// The data-codeword count of each block at a level, in the order the blocks are
        /// interleaved: the raw codewords split as evenly as they go, the short blocks first, each
        /// long one a data codeword longer.
        /// </summary>
        internal static int[] BlockSizes(int version, int eccLevel)
        {
            int level = LevelIndex(eccLevel);
            int blocks = BlocksPerSymbol[level][version], ec = EcCodewordsPerBlock[level][version];
            int raw = RawCodewords(version);
            int shortBlocks = blocks - raw % blocks, shortLength = raw / blocks;

            var sizes = new int[blocks];
            for (int i = 0; i < blocks; i++) sizes[i] = shortLength - ec + (i < shortBlocks ? 0 : 1);
            return sizes;
        }

        /// <summary>The data-codeword count of each block at level M.</summary>
        internal static int[] BlockSizes(int version) => BlockSizes(version, EccLevelM);

        /// <summary>Byte mode's character-count indicator is wider from version 10.</summary>
        internal static int CharacterCountBits(int version) => version < 10 ? 8 : 16;

        /// <summary>
        /// A mode's character-count width for a version (versions 1–9, 10–26, 27–40): numeric 10,
        /// 12, 14; alphanumeric 9, 11, 13; byte 8, 16, 16; kanji and Hanzi 8, 10, 12 — the two
        /// implementations above agree.
        /// </summary>
        internal static int CharacterCountBits(int mode, int version)
        {
            int range = version <= 9 ? 0 : version <= 26 ? 1 : 2;
            return mode switch
            {
                0b0001 => new[] { 10, 12, 14 }[range],
                0b0010 => new[] { 9, 11, 13 }[range],
                0b0100 => new[] { 8, 16, 16 }[range],
                0b1000 => new[] { 8, 10, 12 }[range],
                0b1101 => new[] { 8, 10, 12 }[range],
                _ => 0,
            };
        }

        /// <summary>
        /// Alignment-pattern centres on each axis: 6, then evenly spaced back from size − 7 by an even
        /// step — the rule both implementations compute, which gives every version's row of the
        /// standard's table.
        /// </summary>
        internal static int[] AlignmentPositions(int version)
        {
            if (version == 1) return new int[0];
            int count = version / 7 + 2;
            int step = (version * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
            var positions = new int[count];
            positions[0] = 6;
            for (int i = count - 1, at = SizeOf(version) - 7; i >= 1; i--, at -= step) positions[i] = at;
            return positions;
        }
        /// <summary>
        /// The function patterns and the map of modules they occupy.
        /// </summary>
        /// <param name="version">1–10.</param>
        /// <param name="modules">Receives the function patterns; data modules are left alone.</param>
        /// <param name="reserved">Receives true wherever a module is not data.</param>
        /// <remarks>
        /// <b>The reserved map is the reader's half of this.</b> Encoding uses it to know where not
        /// to put data; decoding uses the very same map to know where not to look for it, and to
        /// know which modules the mask must not be applied to.
        /// </remarks>
        internal static void BuildFunctionPatterns(int version, out bool[,] modules, out bool[,] reserved)
        {
            int size = SizeOf(version);
            modules = new bool[size, size];
            reserved = new bool[size, size];

            PlaceFinders(modules, reserved, size);
            PlaceSeparators(reserved, size);
            PlaceAlignment(modules, reserved, version, size);
            PlaceTiming(modules, reserved, size);
            ReserveFormat(reserved, size);
            if (version >= 7) ReserveVersion(reserved, size);

            // The dark module, which is always set and never data.
            modules[(4 * version) + 9, 8] = true;
            reserved[(4 * version) + 9, 8] = true;
        }

        /// <summary>
        /// The data modules in the order bits are written to them: column pairs right to left,
        /// snaking up and down, skipping the vertical timing column.
        /// </summary>
        /// <remarks>
        /// Yielding the order rather than writing into a grid is what lets one definition serve both
        /// directions — the encoder walks it placing bits, the reader walks it collecting them.
        /// </remarks>
        internal static IEnumerable<(int Y, int X)> DataModuleOrder(int size, bool[,] reserved)
        {
            int x = size - 1;
            while (x > 0)
            {
                if (x == 6) x--;   // the vertical timing pattern is not part of a column pair

                for (int i = 0; i < size; i++)
                {
                    bool up = ((size - 1 - x) / 2) % 2 == 0;
                    int y = up ? size - 1 - i : i;

                    for (int dx = 0; dx < 2; dx++)
                    {
                        int cx = x - dx;
                        if (!reserved[y, cx]) yield return (y, cx);
                    }
                }

                x -= 2;
            }
        }

        /// <summary>Whether the given mask inverts the module at (y, x).</summary>
        internal static bool MaskBit(int mask, int y, int x) => mask switch
        {
            0 => ((y + x) % 2) == 0,
            1 => (y % 2) == 0,
            2 => (x % 3) == 0,
            3 => ((y + x) % 3) == 0,
            4 => (((y / 2) + (x / 3)) % 2) == 0,
            5 => ((y * x) % 2) + ((y * x) % 3) == 0,
            6 => ((((y * x) % 2) + ((y * x) % 3)) % 2) == 0,
            7 => ((((y + x) % 2) + ((y * x) % 3)) % 2) == 0,
            _ => false,
        };

        /// <summary>
        /// The 15-bit format code for an error-correction level and mask, BCH-protected and masked.
        /// </summary>
        internal static int FormatCode(int eccLevel, int mask)
        {
            int format = (eccLevel << 3) | mask;
            int bch = format << 10;

            for (int i = 4; i >= 0; i--)
                if (((bch >> (i + 10)) & 1) == 1)
                    bch ^= FormatBchPolynomial << i;

            return ((format << 10) | bch) ^ FormatMask;
        }

        /// <summary>
        /// The module positions carrying the format code's bit <paramref name="bit"/>, both copies.
        /// </summary>
        /// <remarks>
        /// <b>Two copies, in different places, on purpose</b> — the format field is the one thing a
        /// reader cannot recover without, so it is written twice and a damaged symbol can still be
        /// read from whichever copy survived. Returning both positions from one definition is what
        /// keeps the writer and the reader agreeing about which is which — and also why both could
        /// be the <b>transpose</b> of the standard's with every round trip green, the library reading
        /// its own symbols and no other reader doing so. The positions are the
        /// standard's as Nayuki's <c>drawFormatBits</c> writes them, in row and column terms: bits
        /// 0–5 down column 8 from the top, 6 and 7 round the timing pattern's crossing, 8 at row 8
        /// column 7, 9–14 leftwards along row 8.
        /// </remarks>
        internal static (int Y, int X) FormatPositionA(int bit)
        {
            if (bit <= 5) return (bit, 8);
            if (bit == 6) return (7, 8);
            if (bit == 7) return (8, 8);
            if (bit == 8) return (8, 7);
            return (8, 14 - bit);
        }

        /// <summary>
        /// The second copy, split around the top-right and bottom-left finders: bits 0–7 leftwards
        /// along row 8 from the right edge, 8–14 down column 8 to the bottom.
        /// </summary>
        internal static (int Y, int X) FormatPositionB(int bit, int size)
            => bit < 8 ? (8, size - 1 - bit) : (size - 15 + bit, 8);

        // ==============================================================
        //  Function patterns
        // ==============================================================

        private static void PlaceFinders(bool[,] m, bool[,] r, int size)
        {
            int[][] corners = { new[] { 0, 0 }, new[] { 0, size - 7 }, new[] { size - 7, 0 } };
            foreach (int[] c in corners)
            {
                int cy = c[0], cx = c[1];
                for (int y = 0; y < 7; y++)
                    for (int x = 0; x < 7; x++)
                    {
                        bool dark = y == 0 || y == 6 || x == 0 || x == 6
                                    || (y >= 2 && y <= 4 && x >= 2 && x <= 4);
                        m[cy + y, cx + x] = dark;
                        r[cy + y, cx + x] = true;
                    }
            }
        }

        private static void PlaceSeparators(bool[,] r, int size)
        {
            for (int i = 0; i < 8; i++)
            {
                if (7 < size) { r[7, i] = true; r[i, 7] = true; }
                if (size - 8 >= 0) { r[size - 8, i] = true; r[size - 1 - i, 7] = true; }
                if (size - 8 >= 0) { r[7, size - 1 - i] = true; r[i, size - 8] = true; }
            }
        }

        private static void PlaceAlignment(bool[,] m, bool[,] r, int version, int size)
        {
            int[] positions = AlignmentPositions(version);
            foreach (int py in positions)
                foreach (int px in positions)
                {
                    if ((py <= 8 && px <= 8) || (py <= 8 && px >= size - 9)
                        || (py >= size - 9 && px <= 8)) continue;

                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            bool dark = Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1;
                            m[py + dy, px + dx] = dark;
                            r[py + dy, px + dx] = true;
                        }
                }
        }

        private static void PlaceTiming(bool[,] m, bool[,] r, int size)
        {
            for (int i = 8; i < size - 8; i++)
            {
                bool dark = i % 2 == 0;
                if (!r[6, i]) { m[6, i] = dark; r[6, i] = true; }
                if (!r[i, 6]) { m[i, 6] = dark; r[i, 6] = true; }
            }
        }

        private static void ReserveFormat(bool[,] r, int size)
        {
            for (int i = 0; i < 9; i++) r[8, i] = true;
            for (int i = 0; i < 8; i++) r[i, 8] = true;
            for (int i = 0; i < 8; i++) r[size - 1 - i, 8] = true;
            for (int i = 0; i < 8; i++) r[8, size - 1 - i] = true;
        }

        private static void ReserveVersion(bool[,] r, int size)
        {
            for (int y = 0; y < 6; y++)
                for (int x = 0; x < 3; x++)
                {
                    r[y, size - 11 + x] = true;
                    r[size - 11 + x, y] = true;
                }
        }
    }
}
