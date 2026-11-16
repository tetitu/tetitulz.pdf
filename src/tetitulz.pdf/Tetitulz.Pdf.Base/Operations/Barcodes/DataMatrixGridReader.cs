// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>
    /// Decodes an ECC-200 Data Matrix module grid.
    /// </summary>
    /// <remarks>
    /// <para><b>The mirror of the encoder, through <see cref="DataMatrixLayout"/>.</b> It strips the
    /// finder patterns back out to recover the mapping matrix, walks Annex F's placement in the
    /// same order to collect codewords, repairs them with Reed–Solomon, and reads the ASCII
    /// encodation and the C40, Text, X12, EDIFACT and Base 256 encodations it latches to.</para>
    ///
    /// <para><b>Placement is inverted by running the encoder's own walk.</b> Annex F's order is
    /// intricate — a diagonal "utah" step with four special corner cases — and a second
    /// implementation of it that reads instead of writes is the kind of near-duplicate that drifts
    /// by one module and is then almost impossible to see. Instead the placement is run once over a
    /// grid of <i>position markers</i>, which yields the mapping-matrix cell each codeword bit came
    /// from; reading is then a lookup.</para>
    /// </remarks>
    internal static class DataMatrixGridReader
    {
        /// <summary>Bits per codeword.</summary>
        private const int CodewordBits = 8;

        /// <summary>
        /// Decodes a symbol grid, or returns <see langword="null"/> with a reason.
        /// </summary>
        /// <param name="grid">Module grid, rows by columns; true is dark.</param>
        /// <param name="refusal">
        /// Why a grid that really was a Data Matrix could not be read, or null when it was not one.
        /// </param>
        internal static string Decode(bool[,] grid, out string refusal) => Decode(grid, out refusal, out _);

        /// <summary>As <see cref="Decode(bool[,], out string)"/>, with what the symbol's function codewords said.</summary>
        internal static string Decode(bool[,] grid, out string refusal, out BarcodeSymbolInfo info)
        {
            refusal = null;
            info = null;
            if (grid == null) return null;

            int rows = grid.GetLength(0), columns = grid.GetLength(1);
            int symbol = DataMatrixLayout.WithSize(rows, columns);
            if (symbol < 0) return null;

            if (!FindersMatch(symbol, grid)) return null;

            int[] codewords = ReadCodewords(symbol, grid);
            if (codewords == null) return null;

            // Each interleaved block is corrected on its own, as it was encoded.
            int blocks = DataMatrixLayout.InterleavedBlocks(symbol);
            int ecLength = DataMatrixLayout.EcCodewords(symbol) / blocks;
            var field = new GaloisField256(GaloisField256.DataMatrixPrimitive, ecLength, GaloisField256.DataMatrixGeneratorBase);

            // 144×144 has two error-correction orders in use (DataMatrixLayout.IsLargest): ISO's is
            // tried first, then the one common readers and writers use by default.
            int[] corrected = CorrectBlocks(symbol, codewords, blocks, ecLength, field, skewed: false);
            if (corrected == null && DataMatrixLayout.IsLargest(symbol))
                corrected = CorrectBlocks(symbol, codewords, blocks, ecLength, field, skewed: true);
            if (corrected == null)
            {
                refusal = "Data Matrix symbol's error correction could not repair it; the symbol is "
                        + "damaged beyond what ECC-200 can recover";
                return null;
            }
            codewords = corrected;

            return ReadAscii(codewords, DataMatrixLayout.DataCodewords(symbol), out refusal, out info);
        }

        /// <summary>Each interleaved block corrected on its own, in a copy; <c>null</c> when any block cannot be.</summary>
        private static int[] CorrectBlocks(int symbol, int[] codewords, int blocks, int ecLength, GaloisField256 field, bool skewed)
        {
            var stream = (int[])codewords.Clone();
            for (int b = 0; b < blocks; b++)
            {
                int[] words = DataMatrixLayout.Block(symbol, stream, b, skewed);
                if (!ReedSolomonDecoder.TryCorrect(field, words, ecLength, out _)) return null;
                DataMatrixLayout.PutBlock(symbol, stream, b, words, skewed);
            }
            return stream;
        }

        /// <summary>
        /// Whether every region's finder pattern is where it should be.
        /// </summary>
        /// <remarks>
        /// <b>This is the "is it a Data Matrix at all" test, and it has to be strict.</b> Unlike QR
        /// there is no format field with 32 legal values out of 32768 — the only structure a
        /// candidate grid can be checked against before decoding is the L and the clock track. A
        /// square of ink that satisfies both is worth running Reed–Solomon over; one that does not,
        /// is not.
        /// </remarks>
        private static bool FindersMatch(int symbol, bool[,] grid)
        {
            var expected = new bool[DataMatrixLayout.Rows(symbol), DataMatrixLayout.Columns(symbol)];
            DataMatrixLayout.PaintFinders(symbol, expected);

            int regionHeight = DataMatrixLayout.Rows(symbol) / DataMatrixLayout.RegionRows(symbol);
            int regionWidth = DataMatrixLayout.Columns(symbol) / DataMatrixLayout.RegionColumns(symbol);

            for (int y = 0; y < DataMatrixLayout.Rows(symbol); y++)
                for (int x = 0; x < DataMatrixLayout.Columns(symbol); x++)
                {
                    int inRegionY = y % regionHeight, inRegionX = x % regionWidth;
                    bool isFinder = inRegionY == 0 || inRegionY == regionHeight - 1
                                    || inRegionX == 0 || inRegionX == regionWidth - 1;

                    if (isFinder && grid[y, x] != expected[y, x]) return false;
                }

            return true;
        }

        /// <summary>Collects the codewords by running the placement over position markers.</summary>
        internal static int[] ReadCodewords(int symbol, bool[,] grid)
        {
            int mapRows = DataMatrixLayout.MappingRows(symbol);
            int mapColumns = DataMatrixLayout.MappingColumns(symbol);

            int total = DataMatrixLayout.DataCodewords(symbol) + DataMatrixLayout.EcCodewords(symbol);

            // The encoder's own placement walk, run once to record which codeword and which bit
            // landed in each mapping cell. Reading is then a lookup, and there is no second copy of
            // Annex F to drift from the first.
            var positions = new (int Codeword, int Bit)?[mapRows, mapColumns];
            Ecc200Placement.Trace(positions, mapRows, mapColumns);

            var codewords = new int[total];
            for (int y = 0; y < mapRows; y++)
                for (int x = 0; x < mapColumns; x++)
                {
                    if (!positions[y, x].HasValue) continue;

                    (int codeword, int bit) = positions[y, x].Value;
                    if (codeword < 0 || codeword >= total) continue;

                    DataMatrixLayout.ToSymbol(symbol, y, x, out int row, out int column);
                    if (grid[row, column])
                        codewords[codeword] |= 1 << (CodewordBits - bit);
                }

            return codewords;
        }

        /// <summary>Latch from ASCII to the C40 encodation.</summary>
        private const int LatchC40 = 230;

        /// <summary>Latch from ASCII to the Text encodation.</summary>
        private const int LatchText = 239;

        /// <summary>Latch from ASCII to the ANSI X12 encodation.</summary>
        private const int LatchX12 = 238;

        /// <summary>Return from C40, Text or X12 to ASCII.</summary>
        private const int Unlatch = 254;

        /// <summary>Latch from ASCII to the EDIFACT encodation.</summary>
        private const int LatchEdifact = 240;

        /// <summary>Latch from ASCII to the Base 256 encodation.</summary>
        private const int LatchBase256 = 231;

        /// <summary>The EDIFACT value that returns to ASCII.</summary>
        private const int EdifactUnlatch = 0x1F;

        /// <summary>
        /// Reads ISO/IEC 16022's ASCII encodation out of the corrected codewords, and the C40, Text,
        /// X12, EDIFACT and Base 256 encodations it latches to.
        /// </summary>
        /// <remarks>
        /// <para><b>The function codewords are reported, not read as text</b> (ISO/IEC 16022, as established readers
        /// read it). FNC1 (232, or 27 after a C40/Text Shift 2) in the first position marks GS1
        /// data (identifier <c>]d2</c>), after one letter or one digit pair an AIM application
        /// (<c>]d3</c>) — neither is transmitted — and elsewhere is the GS separator U+001D. Structured
        /// append (233) names the symbol's position and the message's length in its first following
        /// codeword (position − 1 in the high nibble, 17 − count in the low) and is followed by two file
        /// identification codewords; reader programming (234) marks an instruction to the reader; the
        /// 05 and 06 macros (236, 237) stand for the header <c>[)&gt;</c>RS<c>05</c>GS (or <c>06</c>) and
        /// the trailer RS EOT. Each of the four is legal only as the first codeword.</para>
        /// <para><b>An ECI (241) changes the character set of the bytes after it</b>, until the next
        /// one: its number follows in one to three codewords (ISO/IEC 16022 §5.4.4), the data is read
        /// as bytes as always, and each ECI's stretch is decoded in its set at the end — ISO-8859-1
        /// (ECI 1 and 3, the default, so nothing to convert), US-ASCII (27), UTF-8 (26) and UTF-16BE
        /// (25), the sets every target framework carries. Any other ECI is refused by its number, and
        /// bytes its set cannot hold are refused too. The AIM modifier gains 3 (4, 5 or 6), as the
        /// symbology identifier table has it for a symbol carrying an ECI.</para>
        /// </remarks>
        internal static string ReadAscii(int[] codewords, int dataCodewords, out string refusal)
            => ReadAscii(codewords, dataCodewords, out refusal, out _);

        /// <summary>As <see cref="ReadAscii(int[], int, out string)"/>, with what the function codewords said.</summary>
        internal static string ReadAscii(int[] codewords, int dataCodewords, out string refusal,
                                         out BarcodeSymbolInfo info)
        {
            refusal = null;
            info = null;
            var text = new StringBuilder();
            var symbol = new BarcodeSymbolInfo { Modifier = 1 };
            var fnc1 = new Fnc1State();
            string trailer = null;
            List<(int Start, int Eci)> eciSegments = null;

            for (int i = 0; i < dataCodewords; i++)
            {
                int value = codewords[i];

                if (value == DataMatrixLayout.PadCodeword) break;           // end of data
                if (value == 0) break;

                if (value == Fnc1Codeword)
                {
                    fnc1.Apply(text, symbol);
                    continue;
                }

                if (value == StructuredAppend || value == ReaderProgramming
                    || value == Macro05 || value == Macro06)
                {
                    if (i != 0)
                    {
                        refusal = $"Data Matrix codeword {value} (structured append, reader programming or a macro) "
                                + "is legal only as the first codeword";
                        return null;
                    }
                    if (value == ReaderProgramming) { symbol.ReaderProgramming = true; continue; }
                    if (value == Macro05 || value == Macro06)
                    {
                        text.Append("[)>\u001E").Append(value == Macro05 ? "05" : "06").Append('\u001D');
                        trailer = "\u001E\u0004";
                        fnc1.Started();
                        continue;
                    }

                    // Structured append: the sequence indicator and two file identification codewords.
                    if (i + 3 >= dataCodewords)
                    {
                        refusal = "Data Matrix structured append is cut short";
                        return null;
                    }
                    int indicator = codewords[i + 1];
                    int position = (indicator >> 4) + 1, count = 17 - (indicator & 0x0F);
                    if (count < 2 || count > 16 || position > count)
                    {
                        refusal = $"Data Matrix structured append names position {position} of {count} symbols";
                        return null;
                    }
                    symbol.SequencePosition = position;
                    symbol.SequenceCount = count;
                    i += 3;
                    continue;
                }

                if (value == Eci)
                {
                    if (!TryReadEciNumber(codewords, dataCodewords, ref i, out int eci, out refusal)) return null;
                    if (!EciCharacterSets.IsDecoded(eci))
                    {
                        refusal = EciCharacterSets.NotDecoded("Data Matrix", eci);
                        return null;
                    }
                    (eciSegments ??= new List<(int, int)>()).Add((text.Length, eci));
                    continue;
                }

                if (value >= 1 && value <= 128)
                {
                    text.Append((char)(value - 1));
                    fnc1.Saw(value - 1 >= 'A' && value - 1 <= 'Z' ? Fnc1State.Letter : Fnc1State.Other);
                    continue;
                }

                if (value >= DataMatrixLayout.DigitPairBase && value <= 229)
                {
                    int pair = value - DataMatrixLayout.DigitPairBase;
                    text.Append((char)('0' + (pair / 10)));
                    text.Append((char)('0' + (pair % 10)));
                    fnc1.Saw(Fnc1State.DigitPair);
                    continue;
                }

                if (value == DataMatrixLayout.UpperShiftCodeword)
                {
                    if (++i >= dataCodewords) return null;

                    int shifted = codewords[i];
                    if (shifted < 1 || shifted > 128) return null;

                    text.Append((char)(shifted - 1 + 128));
                    fnc1.Saw(Fnc1State.Other);
                    continue;
                }

                if (value == LatchC40 || value == LatchText || value == LatchX12)
                {
                    i = ReadTriplets(codewords, dataCodewords, i + 1, value, text, out refusal, symbol, fnc1);
                    if (refusal != null) return null;
                    continue;
                }

                if (value == LatchEdifact)
                {
                    i = ReadEdifact(codewords, dataCodewords, i + 1, text);
                    fnc1.Saw(Fnc1State.Other);
                    continue;
                }

                if (value == LatchBase256)
                {
                    i = ReadBase256(codewords, dataCodewords, i + 1, text, out refusal);
                    if (refusal != null) return null;
                    fnc1.Saw(Fnc1State.Other);
                    continue;
                }

                refusal = $"Data Matrix codeword {value} is unassigned in ECC 200";
                return null;
            }

            string decoded = text.ToString();
            if (eciSegments != null)
            {
                decoded = DecodeEciSegments(decoded, eciSegments, out refusal);
                if (decoded == null) return null;
                symbol.Modifier += 3;
            }
            if (trailer != null) decoded += trailer;
            info = symbol;
            return decoded;
        }


        /// <summary>
        /// The ECI number after codeword 241 (ISO/IEC 16022 §5.4.4.2): one codeword 1–127 for 0–126,
        /// two from 128–191 for 127–16 382, three from 192–253 for 16 383–999 999. Leaves
        /// <paramref name="i"/> on the last of them.
        /// </summary>
        internal static bool TryReadEciNumber(int[] codewords, int dataCodewords, ref int i, out int eci, out string refusal)
        {
            eci = 0;
            refusal = "Data Matrix ECI is cut short or malformed";
            if (i + 1 >= dataCodewords) return false;
            int c1 = codewords[++i];
            if (c1 >= 1 && c1 <= 127) { eci = c1 - 1; refusal = null; return true; }
            if (c1 >= 128 && c1 <= 191)
            {
                if (i + 1 >= dataCodewords) return false;
                int c2 = codewords[++i];
                if (c2 < 1 || c2 > 254) return false;
                eci = (c1 - 128) * 254 + (c2 - 1) + 127;
                refusal = null;
                return true;
            }
            if (c1 >= 192 && c1 <= 253)
            {
                if (i + 2 >= dataCodewords) return false;
                int c2 = codewords[++i], c3 = codewords[++i];
                if (c2 < 1 || c2 > 254 || c3 < 1 || c3 > 254) return false;
                eci = (c1 - 192) * 64516 + (c2 - 1) * 254 + (c3 - 1) + 16383;
                refusal = null;
                return true;
            }
            return false;
        }

        /// <summary>
        /// <paramref name="read"/>, whose characters are the symbol's bytes, with each ECI's stretch
        /// decoded in its set — the text before the first ECI is ISO-8859-1, the default, and is
        /// already what its bytes say. <c>null</c>, with the reason, for bytes a set cannot hold.
        /// </summary>
        private static string DecodeEciSegments(string read, List<(int Start, int Eci)> segments, out string refusal)
        {
            refusal = null;
            var result = new StringBuilder(read.Length);
            result.Append(read, 0, segments[0].Start);
            for (int s = 0; s < segments.Count; s++)
            {
                int start = segments[s].Start, end = s + 1 < segments.Count ? segments[s + 1].Start : read.Length;
                var bytes = new byte[end - start];
                for (int k = 0; k < bytes.Length; k++)
                {
                    char c = read[start + k];
                    if (c > 0xFF) { refusal = "Data Matrix data after an ECI is not bytes"; return null; }
                    bytes[k] = (byte)c;
                }

                string decoded = EciCharacterSets.Decode("Data Matrix", bytes, segments[s].Eci, out refusal);
                if (decoded == null) return null;
                result.Append(decoded);
            }
            return result.ToString();
        }

        /// <summary>FNC1 in ASCII.</summary>
        private const int Fnc1Codeword = 232;

        /// <summary>Structured append: three codewords follow.</summary>
        private const int StructuredAppend = 233;

        /// <summary>Reader programming.</summary>
        private const int ReaderProgramming = 234;

        /// <summary>The 05 macro.</summary>
        private const int Macro05 = 236;

        /// <summary>The 06 macro.</summary>
        private const int Macro06 = 237;

        /// <summary>Extended Channel Interpretation.</summary>
        private const int Eci = 241;

        /// <summary>
        /// Where an FNC1 stands, measured in the data read so far: first, after one letter or one
        /// digit pair (AIM), or anywhere later (a GS separator). The macros count as data.
        /// </summary>
        private sealed class Fnc1State
        {
            internal const int Letter = 1, DigitPair = 2, Other = 3;
            private int _dataSeen;
            private int _firstKind;
            private bool _sawFnc1;

            internal void Saw(int kind)
            {
                if (_dataSeen == 0) _firstKind = kind;
                _dataSeen++;
            }

            internal void Started() { _dataSeen = 2; }

            internal void Apply(StringBuilder text, BarcodeSymbolInfo symbol)
            {
                if (!_sawFnc1 && _dataSeen == 0) { symbol.IsGs1 = true; symbol.Modifier = 2; }
                else if (!_sawFnc1 && _dataSeen == 1 && (_firstKind == Letter || _firstKind == DigitPair)) symbol.Modifier = 3;
                else text.Append('\u001D');
                _sawFnc1 = true;
            }
        }

        /// <summary>
        /// Reads a C40, Text or X12 segment starting at <paramref name="start"/>, appending to
        /// <paramref name="text"/>, and returns the index of its last codeword — so the ASCII loop
        /// resumes after it.
        /// </summary>
        /// <remarks>
        /// <para>Codewords come in pairs, each packing three values:
        /// <c>(b1 × 256 + b2 − 1) = c1 × 1600 + c2 × 40 + c3</c>. A pair whose first codeword is 254
        /// returns to ASCII; so does a single codeword left at the end of the data, which ISO/IEC 16022
        /// encodes in ASCII.</para>
        /// <para>C40 and Text share the shape: a basic set (space, digits, and upper- or lowercase
        /// letters), Shift 1 to the control characters, Shift 2 to punctuation, and Shift 3 to ASCII
        /// 96–127 in C40 or the other case in Text. Shift 2's 30 is an upper shift (+128 on the next
        /// character) and its 27 is FNC1, which is refused. A shift left pending at the end of a segment
        /// is the encoder's padding and adds nothing. X12 has no shifts: CR, <c>*</c>, <c>&gt;</c>, space,
        /// digits and capitals. Checked against ISO/IEC 16022's tables as an independent
        /// <c>DecodedBitStreamParser</c> implements them.</para>
        /// </remarks>
        private static int ReadTriplets(int[] codewords, int dataCodewords, int start, int latch,
                                        StringBuilder text, out string refusal,
                                        BarcodeSymbolInfo symbol, Fnc1State fnc1)
        {
            refusal = null;
            int shift = 0;
            bool upper = false;
            int p = start;
            var values = new int[3];

            while (p + 1 < dataCodewords)
            {
                if (codewords[p] == Unlatch) return p;

                int packed = (codewords[p] << 8) + codewords[p + 1] - 1;
                values[0] = packed / 1600;
                values[1] = (packed % 1600) / 40;
                values[2] = packed % 40;
                p += 2;

                foreach (int value in values)
                {
                    if (latch == LatchX12)
                    {
                        char? x12 = value == 0 ? '\r' : value == 1 ? '*' : value == 2 ? '>' : value == 3 ? ' '
                                  : value < 14 ? (char)('0' + value - 4) : value < 40 ? (char)('A' + value - 14)
                                  : (char?)null;
                        if (x12 == null) { refusal = $"Data Matrix X12 value {value} is out of range"; return p; }
                        text.Append(x12.Value);
                        fnc1.Saw(x12.Value >= 'A' && x12.Value <= 'Z' ? Fnc1State.Letter : Fnc1State.Other);
                        continue;
                    }

                    char? c;
                    switch (shift)
                    {
                        case 0:
                            if (value < 3) { shift = value + 1; continue; }
                            c = value == 3 ? ' '
                              : value < 14 ? (char)('0' + value - 4)
                              : value < 40 ? (char)((latch == LatchText ? 'a' : 'A') + value - 14)
                              : (char?)null;
                            break;
                        case 1:
                            c = value < 32 ? (char)value : (char?)null;
                            break;
                        case 2:
                            if (value == 27) { fnc1.Apply(text, symbol); shift = 0; continue; }
                            if (value == 30) { upper = true; shift = 0; continue; }
                            c = value < 15 ? (char)('!' + value)
                              : value < 22 ? (char)(':' + value - 15)
                              : value < 27 ? (char)('[' + value - 22)
                              : (char?)null;
                            break;
                        default:
                            c = value >= 32 ? (char?)null
                              : latch == LatchC40 ? (char)(96 + value)
                              : value == 0 ? '`'
                              : value < 27 ? (char)('A' + value - 1)
                              : (char)('{' + value - 27);
                            break;
                    }

                    if (c == null)
                    {
                        refusal = $"Data Matrix {(latch == LatchText ? "Text" : "C40")} value {value} is out of range for its set";
                        return p;
                    }
                    text.Append(upper ? (char)(c.Value + 128) : c.Value);
                    fnc1.Saw(!upper && c.Value >= 'A' && c.Value <= 'Z' ? Fnc1State.Letter : Fnc1State.Other);
                    upper = false;
                    shift = 0;
                }
            }

            // A single codeword left at the end of the data is ASCII; the outer loop reads it.
            return p - 1;
        }

        /// <summary>
        /// Reads an EDIFACT segment starting at <paramref name="start"/> and returns the index of its
        /// last codeword.
        /// </summary>
        /// <remarks>
        /// Three codewords pack four 6-bit values, most significant first; a value below 32 stands for
        /// itself plus 64, so the set is ASCII 32–94. Value 31 returns to ASCII at the next codeword
        /// boundary, discarding the rest of the codeword it ends in; fewer than three codewords left
        /// are ASCII. Checked against an independent decoder.
        /// </remarks>
        private static int ReadEdifact(int[] codewords, int dataCodewords, int start, StringBuilder text)
        {
            int p = start;
            while (dataCodewords - p > 2)
            {
                int bits = (codewords[p] << 16) | (codewords[p + 1] << 8) | codewords[p + 2];
                for (int k = 0; k < 4; k++)
                {
                    int value = (bits >> (18 - 6 * k)) & 0x3F;
                    if (value == EdifactUnlatch)
                        return p + (6 * (k + 1) + 7) / 8 - 1;
                    text.Append((char)((value & 0x20) == 0 ? value | 0x40 : value));
                }
                p += 3;
            }
            return p - 1;
        }

        /// <summary>
        /// Reads a Base 256 segment starting at <paramref name="start"/> and returns the index of its
        /// last codeword, or sets <paramref name="refusal"/> when its length runs past the data.
        /// </summary>
        /// <remarks>
        /// Every codeword of the segment is randomised by its 1-based position in the data
        /// (<c>value − (149 × position mod 255 + 1)</c>, mod 256). The first is the length: 0 means the
        /// rest of the symbol, below 250 the count itself, and from 250 a second codeword extends it
        /// (<c>250 × (d1 − 249) + d2</c>). The sizes <see cref="DataMatrixLayout"/> lists stop at 204 data
        /// codewords, so in a grid that second form can only ever refuse. Bytes are read as ISO-8859-1.
        /// Checked against an independent decoder.
        /// </remarks>
        private static int ReadBase256(int[] codewords, int dataCodewords, int start, StringBuilder text,
                                       out string refusal)
        {
            refusal = null;
            int p = start;
            if (p >= dataCodewords) { refusal = "Data Matrix Base 256 segment has no length"; return p; }

            int d1 = Unrandomize255(codewords[p], p + 1);
            p++;
            int count;
            if (d1 == 0) count = dataCodewords - p;
            else if (d1 < 250) count = d1;
            else
            {
                if (p >= dataCodewords) { refusal = "Data Matrix Base 256 segment's length is truncated"; return p; }
                count = 250 * (d1 - 249) + Unrandomize255(codewords[p], p + 1);
                p++;
            }

            if (count > dataCodewords - p)
            {
                refusal = "Data Matrix Base 256 segment declares more bytes than the symbol holds";
                return p;
            }

            for (int n = 0; n < count; n++, p++)
                text.Append((char)Unrandomize255(codewords[p], p + 1));
            return p - 1;
        }

        /// <summary>ISO/IEC 16022's 255-state randomising, undone for the codeword at a 1-based data position.</summary>
        private static int Unrandomize255(int codeword, int position)
        {
            int value = codeword - ((149 * position) % 255 + 1);
            return value >= 0 ? value : value + 256;
        }
    }
}
