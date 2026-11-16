// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>
    /// Pure-managed DataMatrix (ECC-200) encoder for small ASCII payloads.
    /// Supports the square sizes 10×10 through 132×132 (144×144, whose error-correction order readers disagree on, is read but not written) and the rectangular sizes 8×18 through 16×48,
    /// ASCII encodation only. Reed-Solomon uses the ECC-200 GF(256) polynomial 0x12D.
    /// </summary>
    internal static class DataMatrixEncoder
    {
        /// <summary>
        /// The largest error-correction codeword count in the ECC-200 size table above, so every
        /// generator this encoder can ask for is precomputed rather than built on demand.
        /// </summary>
        private const int MaxEcCodewords = 84;

        /// <summary>
        /// The field arithmetic, shared with <c>QrCodeEncoder</c> through
        /// <see cref="GaloisField256"/>. Both carried a token-for-token copy of the tables, the
        /// generator construction and the RS encoder; only the primitive polynomial differed --
        /// 0x12D here per ISO/IEC 16022 ECC-200, 0x11D there per ISO/IEC 18004 -- which is the
        /// standard rather than drift.
        /// </summary>
        private static readonly GaloisField256 Gf =
            new GaloisField256(GaloisField256.DataMatrixPrimitive, MaxEcCodewords, GaloisField256.DataMatrixGeneratorBase);

        public static bool[,] Encode(string text, bool rectangular = false)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            return EncodeCodewords(EncodeAscii(text), rectangular);
        }

        /// <summary>
        /// The symbol for data codewords already encoded — padded, error-corrected and placed exactly
        /// as <see cref="Encode"/> does. For a caller (a test) that needs an encodation this encoder
        /// does not write; the codewords must end in ASCII mode wherever padding follows.
        /// </summary>
        internal static bool[,] EncodeCodewords(List<int> codewords, bool rectangular = false)
        {
            if (codewords == null) throw new ArgumentNullException(nameof(codewords));

            int symIdx = DataMatrixLayout.SmallestFor(codewords.Count, rectangular);
            if (symIdx < 0)
                throw new ArgumentException(rectangular
                    ? "DataMatrix: payload exceeds the rectangular sizes (at most 49 data codewords)."
                    : "DataMatrix: payload exceeds supported sizes.");

            int rows = DataMatrixLayout.Rows(symIdx), cols = DataMatrixLayout.Columns(symIdx);
            int dataCount = DataMatrixLayout.DataCodewords(symIdx);
            int ecCount = DataMatrixLayout.EcCodewords(symIdx);

            // Pad with ASCII pad characters (129): the first as written, the rest 253-state randomised
            // by their 1-based position. The randomised value wraps by subtracting 254 only above 254,
            // so 254 itself stands — `% 254` wrote 0 there, at position 28 of every symbol padded that
            // far. Checked against an independent implementation.
            if (codewords.Count < dataCount)
            {
                codewords.Add(DataMatrixLayout.PadCodeword);
                while (codewords.Count < dataCount)
                {
                    int r = (149 * (codewords.Count + 1)) % 253 + 1;
                    int pad = DataMatrixLayout.PadCodeword + r;
                    codewords.Add(pad <= 254 ? pad : pad - 254);
                }
            }

            // Reed-Solomon, each interleaved block on its own (52x52 and larger are several): one code over the
            // whole stream is a different code, which no other reader corrects with.
            int blocks = DataMatrixLayout.InterleavedBlocks(symIdx);
            int blockData = dataCount / blocks, blockEc = ecCount / blocks;
            var stream = new int[dataCount + ecCount];
            for (int i = 0; i < dataCount; i++) stream[i] = codewords[i];
            for (int b = 0; b < blocks; b++)
            {
                int[] words = DataMatrixLayout.Block(symIdx, stream, b);
                var data = new int[blockData];
                Array.Copy(words, data, blockData);
                Array.Copy(ReedSolomon(data, blockEc), 0, words, blockData, blockEc);
                DataMatrixLayout.PutBlock(symIdx, stream, b, words);
            }
            var all = new List<int>(stream);

            // Annex F's placement operates on the MAPPING matrix -- the symbol with every region's
            // two finder rows and two finder columns taken out. Running it over the whole symbol
            // instead places data underneath the finder patterns, which are then painted on top of
            // it: for a 10x10 that destroyed 34 of the 98 modules it had just written.
            int mapRows = DataMatrixLayout.MappingRows(symIdx);
            int mapCols = DataMatrixLayout.MappingColumns(symIdx);

            var mapping = new bool?[mapRows, mapCols];
            Ecc200Placement.Place(mapping, mapRows, mapCols, all);

            var matrix = new bool[rows, cols];
            DataMatrixLayout.PaintFinders(symIdx, matrix);

            for (int y = 0; y < mapRows; y++)
                for (int x = 0; x < mapCols; x++)
                {
                    if (!mapping[y, x].HasValue) continue;

                    DataMatrixLayout.ToSymbol(symIdx, y, x, out int row, out int column);
                    matrix[row, column] = mapping[y, x].Value;
                }

            return matrix;
        }

        private static List<int> EncodeAscii(string text)
        {
            var cw = new List<int>();
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (i + 1 < text.Length && AsciiDigits.Is(c) && AsciiDigits.Is(text[i + 1]))
                {
                    // Digit pair encodes as single codeword: value = 10*d1 + d2 + 130
                    int d = (c - '0') * 10 + (text[i + 1] - '0');
                    cw.Add(d + DataMatrixLayout.DigitPairBase);
                    i += 2;
                }
                else if (c < 128)
                {
                    cw.Add((byte)c + 1); // ASCII: c+1
                    i++;
                }
                else if (c <= 0xFF)
                {
                    // Extended ASCII upper: use upper-shift (235) followed by (c - 128) + 1
                    cw.Add(DataMatrixLayout.UpperShiftCodeword);
                    cw.Add(c - 128 + 1);
                    i++;
                }
                else
                {
                    // A code point above U+00FF has no ASCII-encodation codeword. Left
                    // unchecked, `c - 128 + 1` produced a value far outside 0-255 (U+05D0
                    // yields 1361) which then indexed the 256-entry GfLog table and died with
                    // IndexOutOfRangeException deep inside Reed-Solomon — a fault, not the
                    // ArgumentException this encoder uses for its other refusals, and reached
                    // by any CJK, Cyrillic, Hebrew or emoji payload a caller passes through
                    // from user input. Refuse in the caller's terms instead.
                    throw new ArgumentException(
                        $"DataMatrix ASCII encodation cannot represent '{c}' (U+{(int)c:X4}); " +
                        "only code points U+0000-U+00FF are supported.",
                        nameof(text));
                }
            }
            return cw;
        }

        // The RS encoder and the generator construction live in GaloisField256.
        private static int[] ReedSolomon(int[] data, int ecLen) => Gf.ErrorCorrection(data, ecLen);
    }
}
